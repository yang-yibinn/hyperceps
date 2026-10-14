using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Unit tests for <see cref="TreeSummary"/>, behind gh_inspect(action='tree'): totals, depth,
    /// items per branch, and path runs, on the tree shapes Grasshopper users meet every day.
    /// </summary>
    public class TreeSummaryTests
    {
        private static TreeBranch B(int count, params int[] path) => new TreeBranch(path, count);

        [Fact]
        public void Describe_FlatList_IsOneBranch()
        {
            var o = TreeSummary.Describe(new[] { B(6, 0) });
            Assert.Equal(1, (int)o["branches"]);
            Assert.Equal(6, (int)o["items"]);
            Assert.Equal(1, (int)o["depth"]);
            Assert.Equal(6, (int)o["perBranch"]);
            Assert.Equal(new[] { "{0}: 6" }, o["paths"].Values<string>());
            Assert.Null(o["notes"]);
        }

        [Fact]
        public void Describe_Grafted_GroupsIntoOneRunAndSaysSo()
        {
            var branches = Enumerable.Range(0, 10).Select(i => B(1, 0, i)).ToList();
            var o = TreeSummary.Describe(branches);
            Assert.Equal(10, (int)o["branches"]);
            Assert.Equal(10, (int)o["items"]);
            Assert.Equal(2, (int)o["depth"]);
            Assert.Equal(new[] { "{0;0}..{0;9}: 1 each" }, o["paths"].Values<string>());
            Assert.Contains("one item per branch (grafted shape)", o["notes"].Values<string>());
        }

        [Fact]
        public void Runs_BreakOnCountChangeGapOrNewParent()
        {
            var runs = TreeSummary.Runs(new[]
            {
                B(2, 0, 0), B(2, 0, 1), B(2, 0, 2),   // one run
                B(1, 0, 3),                            // count changes
                B(1, 0, 5),                            // gap in the index
                B(1, 1, 0), B(1, 1, 1)                 // new parent path
            });
            Assert.Equal(new[] { "{0;0}..{0;2}: 2 each", "{0;3}: 1", "{0;5}: 1", "{1;0}..{1;1}: 1 each" }, runs);
        }

        [Fact]
        public void Describe_UnevenBranches_ReportsMinAndMax()
        {
            var o = TreeSummary.Describe(new[] { B(3, 0), B(0, 1), B(5, 2) });
            Assert.Equal(0, (int)o["perBranch"]["min"]);
            Assert.Equal(5, (int)o["perBranch"]["max"]);
            Assert.Equal(1, (int)o["emptyBranches"]);
            Assert.Contains("1 empty branch", o["notes"].Values<string>());
        }

        [Fact]
        public void Describe_MixedDepths_IsFlagged()
        {
            var o = TreeSummary.Describe(new[] { B(1, 0), B(1, 0, 1) });
            Assert.Equal(1, (int)o["depth"]["min"]);
            Assert.Equal(2, (int)o["depth"]["max"]);
            Assert.Contains(o["notes"].Values<string>(), n => n.StartsWith("paths have different depths"));
        }

        [Fact]
        public void Describe_SharedLeadingIndices_SuggestSimplify()
        {
            var branches = Enumerable.Range(0, 4).Select(i => B(2, 0, 0, i)).ToList();
            var o = TreeSummary.Describe(branches);
            Assert.Contains(o["notes"].Values<string>(), n => n.Contains("Simplify"));
        }

        [Fact]
        public void Describe_Nulls_AreCounted()
        {
            var o = TreeSummary.Describe(new[] { new TreeBranch(new[] { 0 }, 3, 1) });
            Assert.Equal(1, (int)o["nulls"]);
            Assert.Contains("1 null item", o["notes"].Values<string>());
        }

        [Fact]
        public void Describe_Empty_IsJustZeros()
        {
            var o = TreeSummary.Describe(new List<TreeBranch>());
            Assert.Equal(0, (int)o["branches"]);
            Assert.Equal(0, (int)o["items"]);
            Assert.Null(o["paths"]);
        }

        [Fact]
        public void Describe_ManyRuns_AreCappedAndCounted()
        {
            // 60 branches with alternating counts can't be grouped: 60 runs.
            var branches = Enumerable.Range(0, 60).Select(i => B(i % 2 + 1, i)).ToList();
            var o = TreeSummary.Describe(branches, maxRuns: 20);
            Assert.Equal(20, ((JArray)o["paths"]).Count);
            Assert.Equal(40, (int)o["morePathGroups"]);
        }
    }
}
