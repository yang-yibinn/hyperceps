using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core
{
    /// <summary>One branch of a data tree as the summary sees it: its path indices and item counts.</summary>
    public readonly struct TreeBranch
    {
        public TreeBranch(int[] path, int count, int nulls = 0)
        {
            Path = path ?? Array.Empty<int>();
            Count = count;
            Nulls = nulls;
        }

        public int[] Path { get; }
        public int Count { get; }
        public int Nulls { get; }

        public string PathText => "{" + string.Join(";", Path) + "}";
    }

    /// <summary>
    /// A compact description of one output's data tree, for gh_inspect(action='tree'): totals,
    /// path depth, items per branch, and the paths themselves grouped into runs — consecutive
    /// branches that differ only in their last index (by one each time) and hold the same number of
    /// items — so ten grafted branches read as <c>{0;0}..{0;9}: 1 each</c>, not ten lines.
    /// Host-free so the counting and grouping can be unit-tested.
    /// </summary>
    public static class TreeSummary
    {
        public const int DefaultMaxRuns = 20;
        public const int MaxRunsCeiling = 500;

        public static JObject Describe(IReadOnlyList<TreeBranch> branches, int maxRuns = DefaultMaxRuns)
        {
            maxRuns = Math.Max(1, Math.Min(maxRuns, MaxRunsCeiling));
            int items = branches.Sum(b => b.Count);
            int nulls = branches.Sum(b => b.Nulls);
            int empty = branches.Count(b => b.Count == 0);

            var o = new JObject
            {
                ["branches"] = branches.Count,
                ["items"] = items
            };
            if (nulls > 0) o["nulls"] = nulls;
            if (empty > 0) o["emptyBranches"] = empty;

            if (branches.Count == 0)
                return o;

            int minDepth = branches.Min(b => b.Path.Length);
            int maxDepth = branches.Max(b => b.Path.Length);
            o["depth"] = minDepth == maxDepth ? (JToken)minDepth : new JObject { ["min"] = minDepth, ["max"] = maxDepth };

            int minCount = branches.Min(b => b.Count);
            int maxCount = branches.Max(b => b.Count);
            o["perBranch"] = minCount == maxCount ? (JToken)minCount : new JObject { ["min"] = minCount, ["max"] = maxCount };

            var runs = Runs(branches);
            var list = new JArray();
            foreach (var run in runs.Take(maxRuns))
                list.Add(run);
            o["paths"] = list;
            if (runs.Count > maxRuns) o["morePathGroups"] = runs.Count - maxRuns;

            var notes = Notes(branches, minDepth, maxDepth, nulls, empty);
            if (notes.Count > 0) o["notes"] = new JArray(notes);
            return o;
        }

        /// <summary>
        /// Group consecutive branches into runs: same item count, same path except the last index,
        /// and that index going up by one. Each run is <c>"{0;0}..{0;9}: 1 each"</c> or
        /// <c>"{0;3}: 4"</c>.
        /// </summary>
        public static List<string> Runs(IReadOnlyList<TreeBranch> branches)
        {
            var runs = new List<string>();
            int i = 0;
            while (i < branches.Count)
            {
                int j = i;
                while (j + 1 < branches.Count && Continues(branches[j], branches[j + 1]))
                    j++;

                var first = branches[i];
                runs.Add(j == i
                    ? $"{first.PathText}: {first.Count}"
                    : $"{first.PathText}..{branches[j].PathText}: {first.Count} each");
                i = j + 1;
            }
            return runs;
        }

        private static bool Continues(TreeBranch a, TreeBranch b)
        {
            if (a.Count != b.Count || a.Path.Length != b.Path.Length || a.Path.Length == 0) return false;
            int last = a.Path.Length - 1;
            for (int k = 0; k < last; k++)
                if (a.Path[k] != b.Path[k]) return false;
            return b.Path[last] == a.Path[last] + 1;
        }

        /// <summary>The data-tree situations that most often explain a wrong result.</summary>
        private static List<string> Notes(IReadOnlyList<TreeBranch> branches, int minDepth, int maxDepth, int nulls, int empty)
        {
            var notes = new List<string>();
            if (minDepth != maxDepth)
                notes.Add($"paths have different depths ({minDepth} to {maxDepth}); branches from different sources won't line up. Simplify may help");
            if (branches.Count > 1 && branches.All(b => b.Count == 1))
                notes.Add("one item per branch (grafted shape)");
            if (empty > 0)
                notes.Add($"{empty} empty branch{(empty == 1 ? "" : "es")}");
            if (nulls > 0)
                notes.Add($"{nulls} null item{(nulls == 1 ? "" : "s")}");
            if (minDepth == maxDepth && minDepth > 1 && branches.Count > 0)
            {
                // Leading indices shared by every branch: a sign of extra, unneeded depth.
                int shared = 0;
                while (shared < minDepth - 1 && branches.All(b => b.Path[shared] == branches[0].Path[shared]))
                    shared++;
                if (shared > 0 && minDepth > 2)
                    notes.Add($"the first {shared} path index{(shared == 1 ? " is" : "es are")} the same in every branch; Simplify would remove {(shared == 1 ? "it" : "them")}");
            }
            return notes;
        }
    }
}
