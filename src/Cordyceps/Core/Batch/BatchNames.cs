using System;
using System.Collections.Generic;
using System.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// Small host-free naming helpers for gh_batch: what a ref may look like, and "did you mean"
    /// suggestions. Suggestions are only ever shown, never used to resolve anything.
    /// </summary>
    public static class BatchNames
    {
        /// <summary>
        /// Refs use letters, digits, '_' and '-' only, so <c>split.0</c> can never be misread and a
        /// ref is never confused with a port address.
        /// </summary>
        public static bool IsValidRef(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The candidate closest to <paramref name="name"/>: an exact match ignoring case first,
        /// then the nearest by edit distance (at most 2, or a third of the length), else null.
        /// </summary>
        public static string Closest(string name, IEnumerable<string> candidates)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var list = candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();

            var caseOnly = list.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (caseOnly != null) return caseOnly;

            int limit = Math.Max(2, name.Length / 3);
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (var c in list)
            {
                if (Math.Abs(c.Length - name.Length) > limit) continue;
                int d = EditDistance(name.ToLowerInvariant(), c.ToLowerInvariant());
                if (d <= limit && d < bestDistance)
                {
                    best = c;
                    bestDistance = d;
                }
            }
            return best;
        }

        /// <summary>Levenshtein distance; inputs are short names, so the simple table is fine.</summary>
        public static int EditDistance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        /// <summary>
        /// True when <paramref name="text"/> could be a GUID prefix: 8 or more hex digits, dashes
        /// allowed where a GUID has them.
        /// </summary>
        public static bool LooksLikeGuidPrefix(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 8 || text.Length > 36) return false;
            int hex = 0;
            foreach (var c in text)
            {
                if (c == '-') continue;
                if (!Uri.IsHexDigit(c)) return false;
                hex++;
            }
            return hex >= 8;
        }
    }

    /// <summary>
    /// Shortest unique GUID prefixes (at least 8 characters) for compact results, and prefix lookup
    /// for names like <c>9ac1d04b</c>. Built once per batch over every object on the canvas.
    /// </summary>
    public sealed class GuidPrefixIndex
    {
        public const int MinimumLength = 8;
        private readonly string[] _sorted;

        public GuidPrefixIndex(IEnumerable<Guid> ids)
        {
            _sorted = ids.Select(g => g.ToString("D")).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }

        /// <summary>The shortest prefix of <paramref name="id"/> that no other indexed id shares.</summary>
        public string Shortest(Guid id)
        {
            var s = id.ToString("D");
            int i = Array.BinarySearch(_sorted, s, StringComparer.Ordinal);
            int common = 0;
            if (i >= 0)
            {
                if (i > 0) common = Math.Max(common, CommonPrefix(s, _sorted[i - 1]));
                if (i < _sorted.Length - 1) common = Math.Max(common, CommonPrefix(s, _sorted[i + 1]));
            }
            else
            {
                int insert = ~i;
                if (insert > 0) common = Math.Max(common, CommonPrefix(s, _sorted[insert - 1]));
                if (insert < _sorted.Length) common = Math.Max(common, CommonPrefix(s, _sorted[insert]));
            }
            return s.Substring(0, Math.Min(s.Length, Math.Max(MinimumLength, common + 1)));
        }

        /// <summary>Every indexed id that starts with <paramref name="prefix"/> (ignoring case).</summary>
        public List<string> StartingWith(string prefix)
        {
            var p = prefix.Trim().ToLowerInvariant();
            var result = new List<string>();
            int i = Array.BinarySearch(_sorted, p, StringComparer.Ordinal);
            if (i < 0) i = ~i;
            for (; i < _sorted.Length && _sorted[i].StartsWith(p, StringComparison.Ordinal); i++)
                result.Add(_sorted[i]);
            return result;
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), k = 0;
            while (k < n && a[k] == b[k]) k++;
            return k;
        }
    }
}
