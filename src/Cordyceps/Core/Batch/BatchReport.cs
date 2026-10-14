using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>One runtime message in a batch result: a new object by ref, or an existing one.</summary>
    public sealed class RuntimeNote
    {
        public string Ref { get; set; }
        public string Obj { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Builds gh_batch's compact result. Every list is capped and long messages are cut, so a
    /// typical result stays well under 4 KB; wires are never echoed. <c>success</c> always equals
    /// <c>ok</c> — it is what makes the MCP server flag a failed call to the AI app.
    /// </summary>
    public sealed class BatchReport
    {
        public const int MaxErrors = 50;
        public const int MaxWarnings = 20;
        public const int MaxRuntime = 20;
        public const int MaxHints = 5;
        /// <summary>Refs listed in <c>created</c>; refs stay valid in later batches, so the rest are only counted.</summary>
        public const int MaxCreated = 40;
        public const int MaxErrorLength = 400;
        public const int MaxMessageLength = 200;

        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Hints { get; } = new List<string>();
        public List<RuntimeNote> Runtime { get; } = new List<RuntimeNote>();

        /// <summary>ref → shortest unique GUID prefix, in creation order.</summary>
        public List<KeyValuePair<string, string>> Created { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Dry run: ref → resolved type path.</summary>
        public List<KeyValuePair<string, string>> Resolved { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Dry run: existing object name as written → GUID prefix.</summary>
        public List<KeyValuePair<string, string>> Existing { get; } = new List<KeyValuePair<string, string>>();

        public BatchCounts Counts { get; } = new BatchCounts();

        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public bool RolledBack { get; set; }
        /// <summary>Null when no solve was attempted because the batch failed or was a dry run.</summary>
        public bool? Solved { get; set; }
        public long? ApplyMs { get; set; }
        public long? SolveMs { get; set; }

        public bool Ok => Errors.Count == 0;

        public string ToJson()
        {
            var o = new JObject
            {
                ["success"] = Ok,
                ["ok"] = Ok
            };
            if (DryRun) o["dryRun"] = true;
            o["changed"] = Changed;
            if (RolledBack) o["rolledBack"] = true;

            if (Created.Count > 0)
            {
                o["created"] = Map(Created.Take(MaxCreated).ToList());
                if (Created.Count > MaxCreated) o["createdMore"] = Created.Count - MaxCreated;
            }
            if (Resolved.Count > 0) o["resolved"] = Map(Resolved);
            if (Existing.Count > 0) o["existing"] = Map(Existing);

            if (Ok || Changed) o["counts"] = Counts.ToJson();
            if (Solved.HasValue) o["solved"] = Solved.Value;

            o["errors"] = Capped(Errors, MaxErrors, MaxErrorLength, "errors");
            o["warnings"] = Capped(Warnings, MaxWarnings, MaxMessageLength, "warnings");

            if (Runtime.Count > 0)
            {
                var runtime = new JArray();
                foreach (var note in Runtime.Take(MaxRuntime))
                {
                    var r = new JObject();
                    if (note.Ref != null) r["ref"] = note.Ref; else r["obj"] = note.Obj;
                    r["level"] = note.Level;
                    r["message"] = Cut(note.Message, MaxMessageLength);
                    runtime.Add(r);
                }
                if (Runtime.Count > MaxRuntime)
                    runtime.Add(new JObject { ["more"] = Runtime.Count - MaxRuntime });
                o["runtime"] = runtime;
            }

            o["hints"] = Capped(Hints, MaxHints, MaxMessageLength, "hints");

            if (ApplyMs.HasValue || SolveMs.HasValue)
            {
                var ms = new JObject();
                if (ApplyMs.HasValue) ms["apply"] = ApplyMs.Value;
                if (SolveMs.HasValue) ms["solve"] = SolveMs.Value;
                o["ms"] = ms;
            }

            return o.ToString(Formatting.None);
        }

        private static JObject Map(List<KeyValuePair<string, string>> pairs)
        {
            var m = new JObject();
            foreach (var kv in pairs)
                m[kv.Key] = kv.Value;
            return m;
        }

        private static JArray Capped(List<string> items, int max, int length, string what)
        {
            var a = new JArray(items.Take(max).Select(s => Cut(s, length)));
            if (items.Count > max)
                a.Add($"(+{items.Count - max} more {what})");
            return a;
        }

        public static string Cut(string s, int length) =>
            s == null ? string.Empty : s.Length <= length ? s : s.Substring(0, length - 1) + "…";
    }

    public sealed class BatchCounts
    {
        public int Components { get; set; }
        public int Set { get; set; }
        public int Inserts { get; set; }
        public int Wires { get; set; }
        public int Disconnected { get; set; }
        public int Deleted { get; set; }
        public int Groups { get; set; }

        public JObject ToJson() => new JObject
        {
            ["components"] = Components,
            ["set"] = Set,
            ["inserts"] = Inserts,
            ["wires"] = Wires,
            ["disconnected"] = Disconnected,
            ["deleted"] = Deleted,
            ["groups"] = Groups
        };
    }
}
