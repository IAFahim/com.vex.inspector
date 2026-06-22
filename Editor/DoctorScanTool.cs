using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityEngine;

namespace Vex.Inspector.Editor
{
    using Object = Object;

    [UnityCliTool(
        Name = "doctor_scan",
        Group = "vex",
        Description =
            "Run the Augment Doctor over the open scenes and return silent-failure findings (null track bindings, wrong trigger collision response, schema name collisions) as JSON. No params.")]
    public static class DoctorScanTool
    {
        public static object HandleCommand(JObject @params)
        {
            var scan = AugmentDoctor.Scan();

            var findings = new List<object>(scan.Findings.Count);
            foreach (var f in scan.Findings)
                findings.Add(new
                {
                    severity = f.Severity.ToString(),
                    subject = Path(f.Subject),
                    message = f.Message,
                    hasFix = f.Fix != null,
                    fixLabel = f.FixLabel
                });

            var errors = scan.Findings.Count(f => f.Severity == AugmentFinding.Sev.Error);
            var summary = findings.Count == 0
                ? $"No problems found ({scan.Tracks.Count} tracks across {scan.DirectorCount} directors)."
                : $"{errors} error(s), {findings.Count - errors} warning(s) across {scan.DirectorCount} directors.";

            return new SuccessResponse(summary, new
            {
                directorCount = scan.DirectorCount,
                trackCount = scan.Tracks.Count,
                findings
            });
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
    }
}