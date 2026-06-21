// <copyright file="AugmentDoctor.cs" company="Vex">
//     Copyright (c) Vex. All rights reserved.
// </copyright>

namespace Vex.Inspector.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Playables;
    using UnityEngine.Timeline;
    using Object = UnityEngine.Object;

    /// <summary>
    /// The validation spine for the designer "Augment Doctor" (see DESIGNER_TOOLING_PLAN.md, P0).
    /// The two designer wikis are effectively a spec for this: ~80% of their daily pain is <i>silent failure</i> —
    /// a clip/track/component exists but one field is wrong and nothing happens at runtime. Each such trap is a
    /// deterministic, detectable condition. This scans the open scenes (incl. SubScenes) and turns those traps into
    /// findings with a one-click fix.
    /// <para>
    /// Read-only by design (modelled on Essence Inspector): the scan never mutates; only an explicit
    /// <see cref="AugmentFinding.Fix" /> button mutates, and only where intent is unambiguous.
    /// </para>
    /// <para>
    /// Deliberately depends on <b>nothing</b> from the BovineLabs fork submodules — it reaches authoring components
    /// by type-name reflection so this package stays independent and survives package churn. Add a detector by
    /// implementing <see cref="IAugmentDetector" />; <see cref="TypeCache" /> auto-discovers it.
    /// </para>
    /// </summary>
    public static class AugmentDoctor
    {
        /// <summary> Run every registered detector over the currently open scenes and return the findings. </summary>
        public static AugmentScan Scan()
        {
            var scan = new AugmentScan();
            scan.CollectTracks();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IAugmentDetector>())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                IAugmentDetector detector;
                try
                {
                    detector = (IAugmentDetector)Activator.CreateInstance(type);
                }
                catch
                {
                    continue;
                }

                // ponytail: one bad detector must never blank the whole panel. Isolate each.
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

    /// <summary> A detector contributes findings to a scan. Implement + it is auto-discovered via TypeCache. </summary>
    public interface IAugmentDetector
    {
        /// <summary> Short human label, shown when the detector itself errors. </summary>
        string Name { get; }

        /// <summary> Inspect <paramref name="scan" /> and call <see cref="AugmentScan.Add" /> for each problem. </summary>
        void Detect(AugmentScan scan);
    }

    /// <summary> A single problem found in the open scenes. </summary>
    public sealed class AugmentFinding
    {
        /// <summary> Severity of a finding. </summary>
        public enum Sev
        {
            /// <summary> Will not work — almost certainly broken. </summary>
            Error,

            /// <summary> Probably wrong — worth a look. </summary>
            Warning,
        }

        private AugmentFinding(Sev severity, Object subject, string message, string fixLabel, Action fix)
        {
            this.Severity = severity;
            this.Subject = subject;
            this.Message = message;
            this.FixLabel = fixLabel;
            this.Fix = fix;
        }

        /// <summary> Severity. </summary>
        public Sev Severity { get; }

        /// <summary> The object to ping / group under (may be null for project-wide findings). </summary>
        public Object Subject { get; }

        /// <summary> What is wrong, in a designer's words. </summary>
        public string Message { get; }

        /// <summary> Button label for the fix, or null when there is no auto-fix. </summary>
        public string FixLabel { get; }

        /// <summary> One-click repair, or null when intent is ambiguous (designer must decide). </summary>
        public Action Fix { get; }

        /// <summary> An error-severity finding. </summary>
        public static AugmentFinding Error(Object subject, string message, string fixLabel, Action fix) =>
            new(Sev.Error, subject, message, fixLabel, fix);

        /// <summary> A warning-severity finding. </summary>
        public static AugmentFinding Warn(Object subject, string message, string fixLabel, Action fix) =>
            new(Sev.Warning, subject, message, fixLabel, fix);
    }

    /// <summary> Shared, pre-computed view of the open scenes handed to every detector. </summary>
    public sealed class AugmentScan
    {
        /// <summary> Every track output across every open <see cref="PlayableDirector" />. </summary>
        public readonly List<BoundTrack> Tracks = new();

        /// <summary> All findings emitted by detectors this scan. </summary>
        public readonly List<AugmentFinding> Findings = new();

        /// <summary> Number of directors found (for the "all clear" summary). </summary>
        public int DirectorCount { get; private set; }

        /// <summary> Add a finding. </summary>
        public void Add(AugmentFinding finding)
        {
            if (finding != null)
            {
                this.Findings.Add(finding);
            }
        }

        /// <summary> Gather every director's track bindings once, so detectors don't each re-walk the scene. </summary>
        internal void CollectTracks()
        {
            var directors = Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include);
            this.DirectorCount = directors.Length;

            foreach (var director in directors)
            {
                var playable = director.playableAsset;
                if (playable == null)
                {
                    continue;
                }

                // PlayableAsset.outputs is engine API (UnityEngine.Playables) — no Unity.Timeline reference needed.
                foreach (var binding in playable.outputs)
                {
                    var source = binding.sourceObject;
                    if (source == null)
                    {
                        continue; // markers / non-track outputs
                    }

                    this.Tracks.Add(new BoundTrack
                    {
                        Director = director,
                        TrackAsset = source,
                        TrackName = string.IsNullOrEmpty(binding.streamName) ? source.name : binding.streamName,
                        TrackType = source.GetType().Name,
                        Bound = director.GetGenericBinding(source),
                    });
                }
            }
        }

        /// <summary> One track output of one director and what it is bound to (Bound may be null). </summary>
        public struct BoundTrack
        {
            /// <summary> The owning director. </summary>
            public PlayableDirector Director;

            /// <summary> The track asset itself (a UnityEngine.Timeline.TrackAsset), for clip traversal. </summary>
            public Object TrackAsset;

            /// <summary> The track's display/stream name. </summary>
            public string TrackName;

            /// <summary> Simple type name of the track asset (e.g. "StatefulTriggerTrack"). </summary>
            public string TrackType;

            /// <summary> The object the track is bound to in this director, or null if unbound. </summary>
            public Object Bound;
        }
    }

    /// <summary>
    /// A3: a timeline track with no binding. The single most-repeated repair chore in NIbir888's wiki
    /// ("Timeline = found but binding = null"): the bound object got deleted/renamed and the clip silently
    /// does nothing. No auto-fix — we can't guess which object the designer meant; ping the director instead.
    /// </summary>
    public sealed class NullTrackBindingDetector : IAugmentDetector
    {
        /// <inheritdoc />
        public string Name => "Null track binding";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (track.Bound != null)
                {
                    continue;
                }

                scan.Add(AugmentFinding.Error(
                    track.Director,
                    $"Track '{track.TrackName}' ({track.TrackType}) on director '{track.Director.name}' has no binding — its clips do nothing. Re-drag the scene object onto the track.",
                    null,
                    null));
            }
        }
    }

    /// <summary>
    /// A1: a trigger SOURCE whose <c>PhysicsShapeAuthoring</c> collision response is not <c>RaiseTriggerEvents</c>.
    /// The #1 silent failure in both wikis: the StatefulTrigger track fires nothing because the shape is set to
    /// Collide/None. We only flag shapes that are actually bound by a trigger track (so we don't nag every collider).
    /// </summary>
    public sealed class TriggerCollisionResponseDetector : IAugmentDetector
    {
        private const string WantResponse = "RaiseTriggerEvents";

        /// <inheritdoc />
        public string Name => "Trigger collision response";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (!IsTriggerTrack(track) || track.Bound is not Component boundComponent)
                {
                    continue;
                }

                foreach (var component in boundComponent.GetComponents<Component>())
                {
                    CheckShape(scan, track, boundComponent, component);
                }
            }
        }

        // Only PhysicsShapeAuthoring shapes bound by a trigger track matter (don't nag every collider).
        private static bool IsTriggerTrack(AugmentScan.BoundTrack track) =>
            track.TrackType != null && track.TrackType.IndexOf("Trigger", StringComparison.Ordinal) >= 0;

        // Flag a single bound component if it's a PhysicsShapeAuthoring whose response isn't RaiseTriggerEvents.
        private static void CheckShape(AugmentScan scan, AugmentScan.BoundTrack track, Component boundComponent, Component component)
        {
            if (component == null || component.GetType().Name != "PhysicsShapeAuthoring")
            {
                return;
            }

            var prop = component.GetType().GetProperty("CollisionResponse");
            var value = prop?.GetValue(component);
            if (value == null || value.ToString() == WantResponse)
            {
                return;
            }

            var shape = component; // capture for the fix closure
            scan.Add(AugmentFinding.Error(
                boundComponent.gameObject,
                $"Trigger source '{boundComponent.gameObject.name}' (bound by '{track.TrackName}') has collision response '{value}' — must be 'Raise Trigger Events' or the trigger never fires.",
                "Set Raise Trigger Events",
                () => SetRaiseTriggerEvents(shape, prop)));
        }

        private static void SetRaiseTriggerEvents(Component shape, System.Reflection.PropertyInfo prop)
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

    /// <summary>
    /// A6: the same schema display name living under more than one schema folder — the classic
    /// "two StaggerMeter (one Stat, one Intrinsic)" trap from NIbir888's wiki. Both resolve to different keys, so
    /// binding the wrong one silently breaks a condition or applies to the wrong number. We can't know which is
    /// right, so warn and let the designer confirm. Pure asset scan — no scene needed.
    /// </summary>
    public sealed class SchemaNameCollisionDetector : IAugmentDetector
    {
        private const string SchemaRoot = "Assets/Settings/Schemas";

        /// <inheritdoc />
        public string Name => "Schema name collision";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            if (!AssetDatabase.IsValidFolder(SchemaRoot))
            {
                return; // not this project's layout — nothing to check
            }

            var byName = new Dictionary<string, List<string>>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { SchemaRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);
                if (!byName.TryGetValue(name, out var paths))
                {
                    byName[name] = paths = new List<string>();
                }

                paths.Add(path);
            }

            foreach (var pair in byName)
            {
                if (pair.Value.Count < 2)
                {
                    continue;
                }

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

    /// <summary>
    /// A2: the two-way ObjectDefinition ↔ prefab back-link is broken — the #2 silent failure in both wikis.
    /// A clip can reference an ObjectDefinition, but if the prefab it points to is missing, or the prefab's
    /// <c>ObjectDefinitionAuthoring.Definition</c> doesn't point back to the same asset, spawning silently fails
    /// or produces the wrong identity. Verified API via Timeline.Core's ObjectDefinitionFieldDrawer
    /// (<c>ObjectDefinition.Prefab</c>, <c>ObjectDefinitionAuthoring.Definition</c>); reached by reflection to
    /// keep this package independent. Ping-only — repairing a prefab asset blind is the designer's call.
    /// </summary>
    public sealed class ObjectDefinitionBackLinkDetector : IAugmentDetector
    {
        /// <inheritdoc />
        public string Name => "ObjectDefinition back-link";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ObjectDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadMainAssetAtPath(path);
                if (def == null)
                {
                    continue;
                }

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
                {
                    if (component != null && component.GetType().Name == "ObjectDefinitionAuthoring")
                    {
                        authoring = component;
                        break;
                    }
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
                {
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"Prefab '{prefab.name}' ObjectDefinitionAuthoring.Definition is empty — must point back to '{def.name}', or spawning silently fails.",
                        null,
                        null));
                }
                else if (linked != def)
                {
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"Prefab '{prefab.name}' ObjectDefinitionAuthoring.Definition points to '{linked.name}', not '{def.name}' — spawns the wrong identity.",
                        null,
                        null));
                }
            }
        }

        // Read a public property or field by name (Unity authoring API reached without a hard assembly reference).
        private static object GetMember(object obj, string name)
        {
            var type = obj.GetType();
            var property = type.GetProperty(name);
            if (property != null)
            {
                return property.GetValue(obj);
            }

            return type.GetField(name)?.GetValue(obj);
        }
    }

    /// <summary>
    /// A5: duplicate or zero ObjectDefinition ids. Ids auto-assign on import and must be unique — a duplicate
    /// resolves spawns to the wrong object, and a committed id of 0 means the asset was never imported/registered
    /// (a clip referencing it spawns nothing). Reads the serialized <c>id</c> field (verified via Timeline.Core's
    /// ObjectDefinitionFieldDrawer). No auto-fix — re-import reassigns ids.
    /// </summary>
    public sealed class ObjectDefinitionIdDetector : IAugmentDetector
    {
        /// <inheritdoc />
        public string Name => "ObjectDefinition id";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            var byId = new Dictionary<int, List<Object>>();

            foreach (var guid in AssetDatabase.FindAssets("t:ObjectDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadMainAssetAtPath(path);
                if (def == null)
                {
                    continue;
                }

                var idProp = new SerializedObject(def).FindProperty("id");
                if (idProp == null)
                {
                    continue; // layout drifted — skip rather than mislead
                }

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

                if (!byId.TryGetValue(id, out var list))
                {
                    byId[id] = list = new List<Object>();
                }

                list.Add(def);
            }

            foreach (var pair in byId)
            {
                if (pair.Value.Count < 2)
                {
                    continue;
                }

                var names = string.Join(", ", pair.Value.Select(o => o.name));
                foreach (var def in pair.Value)
                {
                    scan.Add(AugmentFinding.Error(
                        def,
                        $"ObjectDefinition '{def.name}' shares id {pair.Key} with: {names}. Spawns resolve to the wrong object — re-import to reassign.",
                        null,
                        null));
                }
            }
        }
    }

    /// <summary>
    /// A4: a trigger-spawn clip (<c>PhysicsTriggerInstantiateClip</c>) with no <c>targetLinkOverride</c>. Per
    /// NIbir888's wiki, trigger payloads almost always route to the contacted entity's Essence via the Essence Link;
    /// without it the spawned payload acts on its own Targets (often nothing), so damage/heal silently lands on no
    /// one. Warning, not error — some triggers legitimately don't retarget. Clip fields verified from source
    /// (<c>targetLinkOverride</c>, <c>objectDefinition</c>); the BL clip type is matched by name, so no hard
    /// reference to the Physics package.
    /// </summary>
    public sealed class TriggerTargetLinkDetector : IAugmentDetector
    {
        /// <inheritdoc />
        public string Name => "Trigger target link";

        /// <inheritdoc />
        public void Detect(AugmentScan scan)
        {
            foreach (var track in scan.Tracks)
            {
                if (track.TrackAsset is not TrackAsset trackAsset)
                {
                    continue;
                }

                foreach (var clip in trackAsset.GetClips())
                {
                    var asset = clip.asset;
                    if (asset == null || asset.GetType().Name != "PhysicsTriggerInstantiateClip")
                    {
                        continue;
                    }

                    var link = asset.GetType().GetField("targetLinkOverride")?.GetValue(asset) as Object;
                    if (link != null)
                    {
                        continue;
                    }

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
