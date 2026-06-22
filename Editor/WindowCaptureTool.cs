using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityEditor;
using UnityEngine;

namespace Vex.Inspector.Editor
{
    using Object = UnityEngine.Object;

    [UnityCliTool(
        Name = "window_capture",
        Group = "vex",
        Description =
            "Capture a rendered EditorWindow to a PNG (so an AI can see it). Params: window=<title or type substring, case-insensitive>; or list=true to list open windows; output_path optional.")]
    public static class WindowCaptureTool
    {
        public static object HandleCommand(JObject @params)
        {
            @params ??= new JObject();
            var p = new ToolParams(@params);

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null).ToList();

            if (p.GetBool("list"))
            {
                var listed = windows
                    .Select(w => (object)new
                    {
                        title = w.titleContent != null ? w.titleContent.text : string.Empty,
                        type = w.GetType().FullName,
                        width = Mathf.RoundToInt(w.position.width),
                        height = Mathf.RoundToInt(w.position.height)
                    })
                    .ToList();
                return new SuccessResponse($"{listed.Count} open editor window(s).", new { windows = listed });
            }

            var query = p.Get("window");
            if (string.IsNullOrEmpty(query))
                return new ErrorResponse(
                    "Specify 'window' (title or type substring), or pass list=true to see open windows.");

            var match = windows.FirstOrDefault(w => Matches(w, query));
            if (match == null)
                return new ErrorResponse($"No open editor window matches '{query}'. Pass list=true to see options.");

            var outputPath = ResolveOutputPath(p.Get("output_path"));

            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                match.Focus();
                match.Repaint();

                if (!TryCapture(match, outputPath, out var width, out var height, out var error))
                    return new ErrorResponse($"Could not capture '{match.titleContent?.text}': {error}");

                return new SuccessResponse(
                    $"Captured '{match.titleContent?.text}' ({match.GetType().Name}) -> {outputPath}",
                    new
                    {
                        path = outputPath, width, height, window = match.titleContent?.text,
                        type = match.GetType().FullName
                    });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Capture error: {e.Message}");
            }
        }

        private static bool Matches(EditorWindow w, string query)
        {
            query = query.ToLowerInvariant();
            var title = w.titleContent != null ? w.titleContent.text.ToLowerInvariant() : string.Empty;
            var type = w.GetType().Name.ToLowerInvariant();
            var full = w.GetType().FullName != null ? w.GetType().FullName.ToLowerInvariant() : string.Empty;
            return title.Contains(query) || type.Contains(query) || full.Contains(query);
        }

        private static string ResolveOutputPath(string userPath)
        {
            if (string.IsNullOrEmpty(userPath)) userPath = "Screenshots/window.png";

            if (Path.IsPathRooted(userPath)) return Path.GetFullPath(userPath);

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, userPath));
        }

        private static bool TryCapture(EditorWindow window, string path, out int width, out int height,
            out string error)
        {
            error = null;
            var ppp = EditorGUIUtility.pixelsPerPoint;
            width = Mathf.Max(1, Mathf.RoundToInt(window.position.width * ppp));
            height = Mathf.Max(1, Mathf.RoundToInt(window.position.height * ppp));

            try
            {
                var parentField =
                    typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance);
                var parent = parentField?.GetValue(window);
                var grab = parent?.GetType().GetMethod(
                    "GrabPixels",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(RenderTexture), typeof(Rect) },
                    null);

                if (grab != null)
                {
                    var rt = new RenderTexture(width, height, 24);
                    rt.Create();
                    try
                    {
                        grab.Invoke(parent, new object[] { rt, new Rect(0, 0, width, height) });
                        WriteRenderTexture(rt, width, height, path);
                        return true;
                    }
                    finally
                    {
                        RenderTexture.active = null;
                        Object.DestroyImmediate(rt);
                    }
                }
            }
            catch (Exception e)
            {
                error = "GrabPixels: " + e.Message;
            }

            try
            {
                var read = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.InternalEditorUtility")
                    ?.GetMethod("ReadScreenPixel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (read != null)
                {
                    var pos = window.position;
                    var x = pos.x * ppp;
                    var y = Screen.currentResolution.height - (pos.y + pos.height) * ppp;
                    var pixels = (Color[])read.Invoke(null, new object[] { new Vector2(x, y), width, height });
                    Texture2D tex = null;
                    try
                    {
                        tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                        tex.SetPixels(pixels);
                        tex.Apply();
                        var png = tex.EncodeToPNG();
                        if (png == null) throw new Exception("EncodeToPNG returned null");

                        File.WriteAllBytes(path, png);
                    }
                    finally
                    {
                        if (tex != null) Object.DestroyImmediate(tex);
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                error = (error != null ? error + " | " : string.Empty) + "ReadScreenPixel: " + e.Message;
            }

            error ??= "no capture method available (GrabPixels / ReadScreenPixel not found in this Unity version).";
            return false;
        }

        private static void WriteRenderTexture(RenderTexture rt, int width, int height, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                if (png == null) throw new Exception("EncodeToPNG returned null");

                File.WriteAllBytes(path, png);
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);

                RenderTexture.active = prev;
            }
        }

        public class Parameters
        {
            [ToolParameter(
                "Window title or type-name substring to capture (e.g. 'Inspector', 'Essence', 'Console'). Case-insensitive.",
                Required = false)]
            public string Window { get; set; }

            [ToolParameter("If true, list all open editor windows (title + type + size) instead of capturing.",
                Required = false)]
            public bool List { get; set; }

            [ToolParameter("Output PNG path, absolute or relative to project root (default Screenshots/window.png).",
                Required = false)]
            public string OutputPath { get; set; }
        }
    }
}