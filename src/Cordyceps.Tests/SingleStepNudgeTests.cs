using Cordyceps.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// The spec's nudge: after three single-step edits in a row, the result suggests gh_batch.
    /// Reads don't break a streak; gh_batch resets it.
    /// </summary>
    public class SingleStepNudgeTests
    {
        [Fact]
        public void ThirdEditInARow_IsNudged()
        {
            var n = new SingleStepNudge();
            Assert.Null(n.Observe("gh_canvas", "add"));
            Assert.Null(n.Observe("gh_wire", "connect"));
            var hint = n.Observe("gh_canvas", "set");
            Assert.NotNull(hint);
            Assert.Contains("gh_batch", hint);
            Assert.Contains("3 single-step edits", hint);
        }

        [Fact]
        public void ReadsDoNotBreakTheStreak()
        {
            var n = new SingleStepNudge();
            n.Observe("gh_canvas", "add");
            n.Observe("gh_canvas", "list");
            n.Observe("gh_inspect", "status");
            n.Observe("gh_canvas", "add");
            Assert.NotNull(n.Observe("gh_wire", "connect"));
        }

        [Fact]
        public void GhBatchResetsTheStreak()
        {
            var n = new SingleStepNudge();
            n.Observe("gh_canvas", "add");
            n.Observe("gh_canvas", "add");
            n.Observe("gh_batch", null);
            Assert.Null(n.Observe("gh_canvas", "add"));
            Assert.Null(n.Observe("gh_canvas", "add"));
            Assert.NotNull(n.Observe("gh_canvas", "add"));
        }

        [Fact]
        public void LongStreak_IsNudgedEveryThirdEdit()
        {
            var n = new SingleStepNudge();
            int nudges = 0;
            for (int i = 0; i < 9; i++)
                if (n.Observe("gh_canvas", "move") != null) nudges++;
            Assert.Equal(3, nudges);
        }

        [Theory]
        [InlineData("gh_canvas", "list", false)]
        [InlineData("gh_canvas", "ADD", true)]
        [InlineData("gh_wire", "list", false)]
        [InlineData("gh_wire", "disconnect", true)]
        [InlineData("gh_script", "set", false)]
        [InlineData("gh_document", "clear", false)]
        [InlineData("gh_inspect", "tree", false)]
        public void WhatCountsAsAnEdit(string tool, string action, bool expected) =>
            Assert.Equal(expected, SingleStepNudge.IsSingleStepEdit(tool, action));

        [Fact]
        public void AddTo_AddsAFieldToJsonResults()
        {
            var text = SingleStepNudge.AddTo("{\"success\":true,\"id\":\"x\"}", "use gh_batch");
            var o = JObject.Parse(text);
            Assert.Equal("use gh_batch", (string)o["nudge"]);
            Assert.True((bool)o["success"]);
        }

        [Fact]
        public void AddTo_LeavesOtherResultsAlone()
        {
            Assert.Equal("plain text", SingleStepNudge.AddTo("plain text", "use gh_batch"));
            Assert.Equal("{\"a\":1}", SingleStepNudge.AddTo("{\"a\":1}", null));
            Assert.Equal("[1,2]", SingleStepNudge.AddTo("[1,2]", "use gh_batch"));
        }
    }
}
