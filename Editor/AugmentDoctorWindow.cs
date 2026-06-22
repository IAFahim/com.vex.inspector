using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Vex.Inspector.Editor
{
    using Object = UnityEngine.Object;

    public sealed class AugmentDoctorWindow : EditorWindow
    {
        private static readonly Color ErrorColor = new(1f, 0.5f, 0.45f);
        private static readonly Color WarnColor = new(1f, 0.85f, 0.4f);
        private static readonly Color OkColor = new(0.5f, 0.9f, 0.55f);
        private bool autoRescan = true;

        private AugmentScan scan;
        private Vector2 scroll;
        private GUIStyle wrap;

        public static string ReportPath =>
            System.IO.Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "Library",
                "augment-doctor.json");

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Rescan();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnGUI()
        {
            wrap ??= new GUIStyle(EditorStyles.label) { wordWrap = true };

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(70))) Rescan();

                if (GUILayout.Button("Export", EditorStyles.toolbarButton, GUILayout.Width(60))) ExportJson();

                GUILayout.FlexibleSpace();
                autoRescan = GUILayout.Toggle(autoRescan, "Auto", EditorStyles.toolbarButton, GUILayout.Width(50));
            }

            if (scan == null) Rescan();

            var findings = scan.Findings;
            if (findings.Count == 0)
            {
                DrawAllClear();
                return;
            }

            DrawSummary(findings);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (var group in findings.GroupBy(f => f.Subject).OrderBy(g => Path(g.Key)))
                DrawGroup(group.Key, group.ToList());

            EditorGUILayout.EndScrollView();
        }

        private void OnFocus()
        {
            Rescan();
        }

        [MenuItem("Vex/Augment Doctor")]
        private static void Open()
        {
            var window = GetWindow<AugmentDoctorWindow>();
            window.titleContent = new GUIContent("Augment Doctor");
            window.minSize = new Vector2(360, 240);
            window.Rescan();
            window.Show();
        }

        private void OnHierarchyChanged()
        {
            if (autoRescan)
            {
                Rescan();
                Repaint();
            }
        }

        private void Rescan()
        {
            scan = AugmentDoctor.Scan();
        }

        private void DrawAllClear()
        {
            GUILayout.Space(20);
            var prev = GUI.color;
            GUI.color = OkColor;
            var centered = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("✓  No problems found", centered);
            GUI.color = prev;
            var sub = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUILayout.Label(
                $"Checked {scan.Tracks.Count} tracks across {scan.DirectorCount} directors in the open scenes.", sub);
        }

        private void DrawSummary(List<AugmentFinding> findings)
        {
            var errors = findings.Count(f => f.Severity == AugmentFinding.Sev.Error);
            var warnings = findings.Count - errors;
            EditorGUILayout.HelpBox(
                $"{errors} error(s), {warnings} warning(s) across {scan.DirectorCount} directors. Fix the errors — those mechanics will not work.",
                errors > 0 ? MessageType.Error : MessageType.Warning);
        }

        private void DrawGroup(Object subject, List<AugmentFinding> group)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(Path(subject), EditorStyles.boldLabel);
                    if (subject != null && GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(50)))
                        Ping(subject);
                }

                foreach (var finding in group) DrawFinding(finding);
            }
        }

        private void DrawFinding(AugmentFinding finding)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prev = GUI.color;
                GUI.color = finding.Severity == AugmentFinding.Sev.Error ? ErrorColor : WarnColor;
                GUILayout.Label(finding.Severity == AugmentFinding.Sev.Error ? "✗" : "▲", GUILayout.Width(16));
                GUI.color = prev;

                GUILayout.Label(finding.Message, wrap);

                if (finding.Fix != null && !string.IsNullOrEmpty(finding.FixLabel))
                    if (GUILayout.Button(finding.FixLabel, EditorStyles.miniButton, GUILayout.Width(140)))
                    {
                        finding.Fix();
                        Rescan();
                    }
            }
        }

        private void ExportJson()
        {
            var items = scan.Findings.Select(f => new ReportItem
            {
                severity = f.Severity.ToString(),
                subject = Path(f.Subject),
                message = f.Message,
                hasFix = f.Fix != null
            }).ToArray();

            var report = new Report
            {
                generatedAt = DateTime.Now.ToString("s"),
                directorCount = scan.DirectorCount,
                trackCount = scan.Tracks.Count,
                findings = items
            };

            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Debug.Log($"[Augment Doctor] Wrote {items.Length} findings to {ReportPath}");
        }

        private static string Path(Object subject)
        {
            if (subject == null) return "(project)";

            var go = subject as GameObject ?? (subject as Component)?.gameObject;
            if (go == null) return subject.name;

            var stack = new Stack<string>();
            for (var t = go.transform; t != null; t = t.parent) stack.Push(t.name);

            return string.Join("/", stack);
        }

        private static void Ping(Object subject)
        {
            try
            {
                EditorGUIUtility.PingObject(subject);
            }
            catch
            {
            }
        }

        [Serializable]
        private struct Report
        {
            public string generatedAt;
            public int directorCount;
            public int trackCount;
            public ReportItem[] findings;
        }

        [Serializable]
        private struct ReportItem
        {
            public string severity;
            public string subject;
            public string message;
            public bool hasFix;
        }
    }
}