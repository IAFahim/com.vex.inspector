// <copyright file="DoctorScanTool.cs" company="Vex">
//     Copyright (c) Vex. All rights reserved.
// </copyright>

namespace Vex.Inspector.Editor
{
    using System.Collections.Generic;
    using System.Linq;
    using Newtonsoft.Json.Linq;
    using UnityCliConnector;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>
    /// unity-cli tool that runs the <see cref="AugmentDoctor" /> over the open scenes and returns the silent-failure
    /// findings as structured JSON. This is the P3-3 "assistant coherence" hook (DESIGNER_TOOLING_PLAN.md): the
    /// Codex assistant gets the exact, machine-readable finding list — "fix what's red" — instead of OCR'ing a
    /// screenshot of the Doctor window. The Doctor becomes the shared source of truth between designer, inspector,
    /// and assistant. No params.
    /// </summary>
    [UnityCliTool(
        Name = "doctor_scan",
        Group = "vex",
        Description = "Run the Augment Doctor over the open scenes and return silent-failure findings (null track bindings, wrong trigger collision response, schema name collisions) as JSON. No params.")]
    public static class DoctorScanTool
    {
        public static object HandleCommand(JObject @params)
        {
            var scan = AugmentDoctor.Scan();

            var findings = new List<object>(scan.Findings.Count);
            foreach (var f in scan.Findings)
            {
                findings.Add(new
                {
                    severity = f.Severity.ToString(),
                    subject = Path(f.Subject),
                    message = f.Message,
                    hasFix = f.Fix != null,
                    fixLabel = f.FixLabel,
                });
            }

            var errors = scan.Findings.Count(f => f.Severity == AugmentFinding.Sev.Error);
            var summary = findings.Count == 0
                ? $"No problems found ({scan.Tracks.Count} tracks across {scan.DirectorCount} directors)."
                : $"{errors} error(s), {findings.Count - errors} warning(s) across {scan.DirectorCount} directors.";

            return new SuccessResponse(summary, new
            {
                directorCount = scan.DirectorCount,
                trackCount = scan.Tracks.Count,
                findings,
            });
        }

        // Hierarchy path so the assistant can locate the object it's told to fix.
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
    }
}
