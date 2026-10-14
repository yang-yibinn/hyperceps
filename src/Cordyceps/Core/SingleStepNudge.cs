using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps's in-result nudge: after three single-step canvas edits in a row (gh_canvas add,
    /// gh_wire connect, ...), the result carries one line suggesting gh_batch. Reads don't break the
    /// streak (an agent typically reads between edits); a gh_batch call resets it. Every third edit of
    /// a streak is nudged, so a long streak is reminded without every result repeating it.
    /// Host-free so the counting can be unit-tested; McpServer calls it for every tool result.
    /// </summary>
    public sealed class SingleStepNudge
    {
        public const int Threshold = 3;

        private static readonly Dictionary<string, HashSet<string>> EditActions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["gh_canvas"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "add", "delete", "move", "rename", "constant", "set", "config", "preview", "enable",
                "group_create", "group_delete", "group_add", "group_remove", "group_rename", "group_color",
                "group_move", "zoomable", "modifier"
            },
            ["gh_wire"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "connect", "disconnect", "clear" }
        };

        private readonly object _lock = new object();
        private int _streak;

        public static bool IsSingleStepEdit(string tool, string action) =>
            tool != null && action != null && EditActions.TryGetValue(tool, out var actions) && actions.Contains(action.Trim());

        /// <summary>Record one tool call; returns the line to add to its result, or null.</summary>
        public string Observe(string tool, string action)
        {
            lock (_lock)
            {
                if (tool == "gh_batch")
                {
                    _streak = 0;
                    return null;
                }
                if (!IsSingleStepEdit(tool, action))
                    return null;

                _streak++;
                return _streak % Threshold == 0
                    ? $"That is {_streak} single-step edits in a row. For two or more changes, use one gh_batch call instead: it can create, set, wire and group a whole block as one undo step with one solve."
                    : null;
            }
        }

        /// <summary>
        /// Add the nudge to a JSON object result as a top-level "nudge" field. Anything else (no
        /// nudge, non-JSON text) comes back unchanged — a nudge must never break a result.
        /// </summary>
        public static string AddTo(string resultText, string nudge)
        {
            if (string.IsNullOrEmpty(nudge) || string.IsNullOrWhiteSpace(resultText))
                return resultText;
            try
            {
                if (JToken.Parse(resultText) is JObject o)
                {
                    o["nudge"] = nudge;
                    return o.ToString(Formatting.None);
                }
            }
            catch (JsonException)
            {
                // Not a JSON object: leave it as it is.
            }
            return resultText;
        }
    }
}
