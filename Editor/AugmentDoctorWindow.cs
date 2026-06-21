// <copyright file="AugmentDoctorWindow.cs" company="Vex">
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
    using Object = UnityEngine.Object;

    /// <summary>
    /// The designer-facing "Augment Doctor" panel (DESIGNER_TOOLING_PLAN.md, P0). Read-only, modelled on the
    /// Essence Inspector: it scans the open scenes and lists every silent-failure trap as a finding with a
    /// one-click fix. Goes green when the augment is correctly wired — which serves both designer styles at once:
    /// Efarjeon's "is this correct?" checklist and NIbir888's "how do I repair it?" playbook.
    /// </summary>
    public sealed class AugmentDoctorWindow : EditorWindow
    {
        private static readonly Color ErrorColor = new(1f, 0.5f, 0.45f);
        private static readonly Color WarnColor = new(1f, 0.85f, 0.4f);
        private static readonly Color OkColor = new(0.5f, 0.9f, 0.55f);

        private AugmentScan scan;
        private Vector2 scroll;
        private bool autoRescan = true;
        private GUIStyle wrap;

        [MenuItem("Vex/Augment Doctor")]
        private static void Open()
        {
            var window = GetWindow<AugmentDoctorWindow>();
            window.titleContent = new GUIContent("Augment Doctor");
            window.minSize = new Vector2(360, 240);
            window.Rescan();
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += this.OnHierarchyChanged;
            this.Rescan();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= this.OnHierarchyChanged;
        }

        private void OnFocus()
        {
            // Re-validate when the designer comes back to the panel — cheap and keeps it honest.
            this.Rescan();
        }

        private void OnHierarchyChanged()
        {
            if (this.autoRescan)
            {
                this.Rescan();
                this.Repaint();
            }
        }

        private void Rescan()
        {
            this.scan = AugmentDoctor.Scan();
        }

        private void OnGUI()
        {
            this.wrap ??= new GUIStyle(EditorStyles.label) { wordWrap = true };

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    this.Rescan();
                }

                if (GUILayout.Button("Export", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    this.ExportJson();
                }

                GUILayout.FlexibleSpace();
                this.autoRescan = GUILayout.Toggle(this.autoRescan, "Auto", EditorStyles.toolbarButton, GUILayout.Width(50));
            }

            if (this.scan == null)
            {
                this.Rescan();
            }

            var findings = this.scan.Findings;
            if (findings.Count == 0)
            {
                this.DrawAllClear();
                return;
            }

            this.DrawSummary(findings);

            this.scroll = EditorGUILayout.BeginScrollView(this.scroll);

            // Group by subject so a designer sees all problems on one object together.
            foreach (var group in findings.GroupBy(f => f.Subject).OrderBy(g => Path(g.Key)))
            {
                this.DrawGroup(group.Key, group.ToList());
            }

            EditorGUILayout.EndScrollView();
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
            GUILayout.Label($"Checked {this.scan.Tracks.Count} tracks across {this.scan.DirectorCount} directors in the open scenes.", sub);
        }

        private void DrawSummary(List<AugmentFinding> findings)
        {
            var errors = findings.Count(f => f.Severity == AugmentFinding.Sev.Error);
            var warnings = findings.Count - errors;
            EditorGUILayout.HelpBox(
                $"{errors} error(s), {warnings} warning(s) across {this.scan.DirectorCount} directors. Fix the errors — those mechanics will not work.",
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
                    {
                        Ping(subject);
                    }
                }

                foreach (var finding in group)
                {
                    this.DrawFinding(finding);
                }
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

                GUILayout.Label(finding.Message, this.wrap);

                if (finding.Fix != null && !string.IsNullOrEmpty(finding.FixLabel))
                {
                    if (GUILayout.Button(finding.FixLabel, EditorStyles.miniButton, GUILayout.Width(140)))
                    {
                        finding.Fix();
                        this.Rescan();
                    }
                }
            }
        }

        /// <summary> The on-disk path the JSON report is written to (under Library, per-machine, gitignored). </summary>
        public static string ReportPath =>
            System.IO.Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "Library", "augment-doctor.json");

        // P3-3 (assistant coherence): dump findings as machine-readable JSON so the Codex assistant can read the
        // exact finding list (via WindowCaptureTool's sibling tools) instead of OCR'ing a screenshot. The Doctor
        // becomes the shared source of truth between designer, inspector, and assistant.
        private void ExportJson()
        {
            var items = this.scan.Findings.Select(f => new ReportItem
            {
                severity = f.Severity.ToString(),
                subject = Path(f.Subject),
                message = f.Message,
                hasFix = f.Fix != null,
            }).ToArray();

            var report = new Report
            {
                generatedAt = DateTime.Now.ToString("s"),
                directorCount = this.scan.DirectorCount,
                trackCount = this.scan.Tracks.Count,
                findings = items,
            };

            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Debug.Log($"[Augment Doctor] Wrote {items.Length} findings to {ReportPath}");
        }

        private static string Path(Object subject)
        {
            if (subject == null)
            {
                return "(project)";
            }

            var go = subject as GameObject ?? (subject as Component)?.gameObject;
            if (go == null)
            {
                return subject.name;
            }

            var stack = new Stack<string>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                stack.Push(t.name);
            }

            return string.Join("/", stack);
        }

        private static void Ping(Object subject)
        {
            // Guarded: PingObject can throw on SubScene/Timeline-preview objects; never touch Selection (it defers
            // a Hierarchy frame that throws asynchronously and escapes try/catch).
            try
            {
                EditorGUIUtility.PingObject(subject);
            }
            catch
            {
                // ignored
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
