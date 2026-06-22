using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Vex.Inspector.Editor
{
    using Object = UnityEngine.Object;

    public static class AugmentDoctor
    {
        public static AugmentScan Scan()
        {
            var scan = new AugmentScan();
            scan.CollectTracks();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IAugmentDetector>())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                IAugmentDetector detector;
                try
                {
                    detector = (IAugmentDetector)Activator.CreateInstance(type);
                }
                catch
                {
                    continue;
                }

                try
                {
                    detector.Detect(scan);
                }
                catch (Exception e)
                {
                    scan.Add(AugmentFinding.Warn(null, $"Detector '{detector.Name}' threw: {e.Message}", null, null));
                }
            }

            return scan;
        }
    }

    public interface IAugmentDetector
    {
        string Name { get; }

        void Detect(AugmentScan scan);
    }

    public sealed class AugmentFinding
    {
        public enum Sev
        {
            Error,

            Warning
        }

        private AugmentFinding(Sev severity, Object subject, string message, string fixLabel, Action fix)
        {
            Severity = severity;
            Subject = subject;
            Message = message;
            FixLabel = fixLabel;
            Fix = fix;
        }

        public Sev Severity { get; }

        public Object Subject { get; }

        public string Message { get; }

        public string FixLabel { get; }

        public Action Fix { get; }

        public static AugmentFinding Error(Object subject, string message, string fixLabel, Action fix)
        {
            return new AugmentFinding(Sev.Error, subject, message, fixLabel, fix);
        }

        public static AugmentFinding Warn(Object subject, string message, string fixLabel, Action fix)
        {
            return new AugmentFinding(Sev.Warning, subject, message, fixLabel, fix);
        }
    }

    public sealed class AugmentScan
    {
        public readonly List<AugmentFinding> Findings = new();

        public readonly List<BoundTrack> Tracks = new();

        public int DirectorCount { get; private set; }

        public void Add(AugmentFinding finding)
        {
            if (finding != null) Findings.Add(finding);
        }

        internal void CollectTracks()
        {
            var directors = Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include);
            DirectorCount = directors.Length;

            foreach (var director in directors)
            {
                var playable = director.playableAsset;
                if (playable == null) continue;

                foreach (var binding in playable.outputs)
                {
                    var source = binding.sourceObject;
                    if (source == null) continue;

                    Tracks.Add(new BoundTrack
                    {
                        Director = director,
                        TrackAsset = source,
                        TrackName = string.IsNullOrEmpty(binding.streamName) ? source.name : binding.streamName,
                        TrackType = source.GetType().Name,
                        Bound = director.GetGenericBinding(source)
                    });
                }
            }
        }

        public struct BoundTrack
        {
            public PlayableDirector Director;

            public Object TrackAsset;

            public string TrackName;

            public string TrackType;

            public Object Bound;
        }
    }

    public sealed class NullTrackBindingDetector : IAugmentDetector
    {
        public string Name => "Null track binding";

        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (track.Bound != null) continue;

                scan.Add(AugmentFinding.Error(
                    track.Director,
                    $"Track '{track.TrackName}' ({track.TrackType}) on director '{track.Director.name}' has no binding — its clips do nothing. Re-drag the scene object onto the track.",
                    null,
                    null));
            }
        }
    }

    public sealed class TriggerCollisionResponseDetector : IAugmentDetector
    {
        private const string WantResponse = "RaiseTriggerEvents";

        public string Name => "Trigger collision response";

        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (!IsTriggerTrack(track) || track.Bound is not Component boundComponent) continue;

                foreach (var component in boundComponent.GetComponents<Component>())
                    CheckShape(scan, track, boundComponent, component);
            }
        }

        private static bool IsTriggerTrack(AugmentScan.BoundTrack track)
        {
            return track.TrackType != null && track.TrackType.IndexOf("Trigger", StringComparison.Ordinal) >= 0;
        }

        private static void CheckShape(AugmentScan scan, AugmentScan.BoundTrack track, Component boundComponent,
            Component component)
        {
            if (component == null || component.GetType().Name != "PhysicsShapeAuthoring") return;

            var prop = component.GetType().GetProperty("CollisionResponse");
            var value = prop?.GetValue(component);
            if (value == null || value.ToString() == WantResponse) return;

            var shape = component;
            scan.Add(AugmentFinding.Error(
                boundComponent.gameObject,
                $"Trigger source '{boundComponent.gameObject.name}' (bound by '{track.TrackName}') has collision response '{value}' — must be 'Raise Trigger Events' or the trigger never fires.",
                "Set Raise Trigger Events",
                () => SetRaiseTriggerEvents(shape, prop)));
        }

        private static void SetRaiseTriggerEvents(Component shape, PropertyInfo prop)
        {
            try
            {
                var enumValue = Enum.Parse(prop.PropertyType, WantResponse);
                Undo.RecordObject(shape, "Set Raise Trigger Events");
                prop.SetValue(shape, enumValue);
                EditorUtility.SetDirty(shape);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Augment Doctor] Could not set collision response: {e.Message}");
            }
        }
    }

    public sealed class SchemaNameCollisionDetector : IAugmentDetector
    {
        private const string SchemaRoot = "Assets/Settings/Schemas";

        public string Name => "Schema name collision";

        public void Detect(AugmentScan scan)
        {
            if (!AssetDatabase.IsValidFolder(SchemaRoot)) return;

            var byName = new Dictionary<string, List<string>>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { SchemaRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);
                if (!byName.TryGetValue(name, out var paths)) byName[name] = paths = new List<string>();

                paths.Add(path);
            }

            foreach (var pair in byName)
            {
                if (pair.Value.Count < 2) continue;

                var folders = pair.Value
                    .Select(p => Path.GetFileName(Path.GetDirectoryName(p)))
                    .Distinct();
                var subject = AssetDatabase.LoadAssetAtPath<Object>(pair.Value[0]);
                scan.Add(AugmentFinding.Warn(
                    subject,
                    $"{pair.Value.Count} schemas named '{pair.Key}' exist ({string.Join(", ", folders)}). Easy to bind the wrong one — confirm which type your Action/condition actually uses.",
                    null,
                    null));
            }
        }
    }

    public sealed class ObjectDefinitionBackLinkDetector : IAugmentDetector
    {
        public string Name => "ObjectDefinition back-link";

        public void Detect(AugmentScan scan)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ObjectDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadMainAssetAtPath(path);
                if (def == null) continue;

                var prefab = GetMember(def, "Prefab") as GameObject;
                if (prefab == null)
                {
                    scan.Add(AugmentFinding.Warn(
                        def,
                        $"ObjectDefinition '{def.name}' has no prefab — anything that spawns it gets nothing.",
                        null,
                        null));
                    continue;
                }

                Component authoring = null;
                foreach (var component in prefab.GetComponentsInChildren<Component>(true))
                    if (component != null && component.GetType().Name == "ObjectDefinitionAuthoring")
                    {
                        authoring = component;
                        break;
                    }

                if (authoring == null)
                {
                    scan.Add(AugmentFinding.Warn(
                        def,
                        $"Prefab '{prefab.name}' (spawned by '{def.name}') has no ObjectDefinitionAuthoring — the spawned entity has no identity. Add it and set Definition to '{def.name}'.",
                        null,
                        null));
                    continue;
                }

                var linked = GetMember(authoring, "Definition") as Object;
                if (linked == null)
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"Prefab '{prefab.name}' ObjectDefinitionAuthoring.Definition is empty — must point back to '{def.name}', or spawning silently fails.",
                        null,
                        null));
                else if (linked != def)
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"Prefab '{prefab.name}' ObjectDefinitionAuthoring.Definition points to '{linked.name}', not '{def.name}' — spawns the wrong identity.",
                        null,
                        null));
            }
        }

        private static object GetMember(object obj, string name)
        {
            var type = obj.GetType();
            var property = type.GetProperty(name);
            if (property != null) return property.GetValue(obj);

            return type.GetField(name)?.GetValue(obj);
        }
    }

    public sealed class ObjectDefinitionIdDetector : IAugmentDetector
    {
        public string Name => "ObjectDefinition id";

        public void Detect(AugmentScan scan)
        {
            var byId = new Dictionary<int, List<Object>>();

            foreach (var guid in AssetDatabase.FindAssets("t:ObjectDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadMainAssetAtPath(path);
                if (def == null) continue;

                var idProp = new SerializedObject(def).FindProperty("id");
                if (idProp == null) continue;

                var id = idProp.intValue;
                if (id == 0)
                {
                    scan.Add(AugmentFinding.Warn(
                        def,
                        $"ObjectDefinition '{def.name}' has id 0 — not imported/registered. Re-import, or clips referencing it resolve to nothing.",
                        null,
                        null));
                    continue;
                }

                if (!byId.TryGetValue(id, out var list)) byId[id] = list = new List<Object>();

                list.Add(def);
            }

            foreach (var pair in byId)
            {
                if (pair.Value.Count < 2) continue;

                var names = string.Join(", ", pair.Value.Select(o => o.name));
                foreach (var def in pair.Value)
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"ObjectDefinition '{def.name}' shares id {pair.Key} with: {names}. Spawns resolve to the wrong object — re-import to reassign.",
                        null,
                        null));
            }
        }
    }

    public sealed class TriggerTargetLinkDetector : IAugmentDetector
    {
        public string Name => "Trigger target link";

        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (track.TrackAsset is not TrackAsset trackAsset) continue;

                foreach (var clip in trackAsset.GetClips())
                {
                    var asset = clip.asset;
                    if (asset == null || asset.GetType().Name != "PhysicsTriggerInstantiateClip") continue;

                    var link = asset.GetType().GetField("targetLinkOverride")?.GetValue(asset) as Object;
                    if (link != null) continue;

                    var def = asset.GetType().GetField("objectDefinition")?.GetValue(asset) as Object;
                    var defName = def != null ? $"'{def.name}'" : "a payload";
                    scan.Add(AugmentFinding.Warn(
                        track.Director,
                        $"Trigger clip '{clip.displayName}' on '{track.Director.name}' spawns {defName} but has no targetLinkOverride — if the payload should act on what it hit, set it to the Essence Link, or it affects no one.",
                        null,
                        null));
                }
            }
        }
    }
}