using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// gh_batch's port rule: a 0-based index, or an exact name or nickname. A match that ignores
    /// case is accepted only when it is the only one. A partial match is never used, only offered as
    /// "did you mean" — the shared <see cref="ParamSpecLookup"/> (which the single-step tools keep
    /// using unchanged) also accepts a substring of the name, so <c>Pattern</c> would quietly land on
    /// Cull Pattern's "Cull Pattern" input. Numeric specs are handed to ParamSpecLookup as-is, so
    /// both rules read an index identically.
    /// </summary>
    public static class StrictPortLookup
    {
        public static ParamLookupResult Resolve(
            string spec,
            IReadOnlyList<ParamIdentity> ports,
            bool isInput,
            string ownerLabel)
        {
            ports ??= Array.Empty<ParamIdentity>();
            var side = isInput ? "input" : "output";

            if (ports.Count == 0)
                return ParamLookupResult.Failed($"{ownerLabel} has no {side}s");

            var s = (spec ?? string.Empty).Trim();
            if (s.Length == 0)
                return ParamLookupResult.Failed($"no {side} given for {ownerLabel}. {Capital(side)}s: {List(ports)}");

            if (IsNumeric(s))
            {
                var byIndex = ParamSpecLookup.Resolve(s, ports, isInput, ownerLabel);
                return byIndex.IsResolved
                    ? byIndex
                    : ParamLookupResult.Failed($"{side} index {s} doesn't exist on {ownerLabel}. {Capital(side)}s: {List(ports)}");
            }

            var exact = Matches(ports, p => p.Name == s || p.NickName == s);
            if (exact.Count == 1)
                return ParamLookupResult.Resolved(exact[0]);
            if (exact.Count > 1)
                return ParamLookupResult.Failed($"'{s}' matches {exact.Count} {side}s on {ownerLabel} ({string.Join(", ", exact)}); use the index. {Capital(side)}s: {List(ports)}");

            var caseless = Matches(ports, p =>
                p.Name.Equals(s, StringComparison.OrdinalIgnoreCase) || p.NickName.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (caseless.Count == 1)
                return ParamLookupResult.Resolved(caseless[0]);
            if (caseless.Count > 1)
                return ParamLookupResult.Failed($"'{s}' matches {caseless.Count} {side}s on {ownerLabel} when case is ignored ({string.Join(", ", caseless)}); write the exact case or the index. {Capital(side)}s: {List(ports)}");

            var near = Matches(ports, p =>
                Contains(p.Name, s) || Contains(p.NickName, s) || Contains(s, p.Name) || Contains(s, p.NickName));
            if (near.Count == 0)
            {
                var closest = BatchNames.Closest(s, ports.SelectMany(p => new[] { p.NickName, p.Name }));
                if (closest != null)
                    near = Matches(ports, p => p.NickName == closest || p.Name == closest);
            }

            var hint = near.Count == 0
                ? string.Empty
                : " Did you mean " + string.Join(" or ", near.Take(3).Select(i => $"'{ports[i].NickName}' ({ports[i].Name})")) + "?";

            return ParamLookupResult.Failed($"no {side} named '{s}' on {ownerLabel}.{hint} {Capital(side)}s: {List(ports)}");
        }

        /// <summary>Ports as <c>0 L (List), 1 P (Cull Pattern)</c>, for messages.</summary>
        public static string List(IReadOnlyList<ParamIdentity> ports)
        {
            var parts = new List<string>();
            for (int i = 0; i < ports.Count && i < 12; i++)
            {
                var p = ports[i];
                parts.Add(p.NickName == p.Name ? $"{i} {p.Name}" : $"{i} {p.NickName} ({p.Name})");
            }
            if (ports.Count > 12) parts.Add($"… {ports.Count - 12} more");
            return string.Join(", ", parts);
        }

        private static List<int> Matches(IReadOnlyList<ParamIdentity> ports, Func<ParamIdentity, bool> test)
        {
            var result = new List<int>();
            for (int i = 0; i < ports.Count; i++)
                if (test(ports[i])) result.Add(i);
            return result;
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle) && needle.Length >= 2
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsNumeric(string s)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
            int start = s[0] == '+' || s[0] == '-' ? 1 : 0;
            if (start >= s.Length) return false;
            for (int i = start; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
