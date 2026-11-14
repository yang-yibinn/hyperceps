using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Outline;
using Newtonsoft.Json.Linq;
using Xunit;
using Canvas = Cordyceps.Tests.CanvasOutlineTests.Canvas;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Unit tests for outline's group='network N' and group='ungrouped' (CanvasOutline.Regions): a
    /// whole network, or the objects outside groups, in one call in the group view's format.
    /// </summary>
    public class CanvasOutlineRegionsTests
    {
        private static List<string> Lines(JToken t) => CanvasOutlineTests.Lines(t);

        /// <summary>Two networks (4 and 2 objects) and one unconnected panel; no groups.</summary>
        private static (OutlineModel Model, OutlineNode[] Big, OutlineNode[] Small) TwoNetworks()
        {
            var c = new Canvas();
            // Placed against the flow: the end is leftmost, so the order comes from the wires.
            var count = c.Node("Number Slider", "COUNT", OutlineKind.Slider, "12 (1..20)", x: 300);
            var divide = c.Node("Divide Curve", x: 200);
            var pts = c.Node("Point", "PTS", OutlineKind.Parameter, x: 100);
            var line = c.Node("Line", x: 0);
            c.Wire(count, null, divide, "N");
            c.Wire(divide, "P", pts, null, items: 12);
            c.Wire(pts, null, line, "A", items: 12);
            c.Input(divide, "C").Stored = "1 × Curve from Rhino";

            var radius = c.Node("Panel", "RADIUS", OutlineKind.Panel, "\"2.5\"", x: 0, y: 300);
            var circle = c.Node("Circle", "RING", x: 100, y: 300);
            c.Wire(radius, null, circle, "R");

            c.Node("Panel", "NOTE", OutlineKind.Panel, "\"read me\"", x: 500);
            return (c.Build(), new[] { count, divide, pts, line }, new[] { radius, circle });
        }

        private static JObject Open(OutlineModel m, params string[] names)
        {
            var errors = new List<string>();
            var o = CanvasOutline.Open(m, names, errors);
            Assert.Empty(errors);
            return o;
        }

        private static string OpenError(OutlineModel m, params string[] names)
        {
            var errors = new List<string>();
            Assert.Null(CanvasOutline.Open(m, names, errors));
            return Assert.Single(errors);
        }

        [Fact]
        public void Network_ListsEveryObject_UpstreamFirst_InTheGroupViewsFormat()
        {
            var (model, big, _) = TwoNetworks();

            var g = (JObject)Open(model, "network 1")["groups"][0];

            Assert.Equal("network 1", (string)g["group"]);
            Assert.Null(g["id"]);
            Assert.Equal("4 objects · network 1 of 2", (string)g["summary"]);
            Assert.Equal(new[]
            {
                $"{big[0].ShortId} Number Slider \"COUNT\" = 12 (1..20)",
                $"{big[1].ShortId} Divide Curve · N ← COUNT · C = 1 × Curve from Rhino",
                $"{big[2].ShortId} Point \"PTS\" · ← {big[1].ShortId}.P",
                $"{big[3].ShortId} Line · A ← PTS"
            }, Lines(g["objects"]));
            Assert.Null(g["in"]);
            Assert.Null(g["out"]);
            Assert.Null(g["more"]);
        }

        [Fact]
        public void Networks_AreNumberedAsTheOverviewLists_AndOpenTogether()
        {
            var (model, _, small) = TwoNetworks();

            var overview = Lines(CanvasOutline.Overview(model)["networks"]);
            Assert.StartsWith("network 1 · 4 objects", overview[0]);
            Assert.StartsWith("network 2 · 2 objects", overview[1]);

            var groups = (JArray)Open(model, "network 2", "Network1", "network 2")["groups"];
            Assert.Equal(2, groups.Count);
            Assert.Equal("network 2", (string)groups[0]["group"]);
            Assert.Equal($"{small[1].ShortId} Circle \"RING\" · R ← RADIUS", Lines(groups[0]["objects"])[1]);
            Assert.Equal("network 1", (string)groups[1]["group"]);
        }

        [Fact]
        public void Network_WithoutANumber_OnACanvasWithSeveral_SaysWhichNamesExist()
        {
            var (model, _, _) = TwoNetworks();
            Assert.Contains("this canvas has 2 networks; name one, 'network 1' to 'network 2'", OpenError(model, "network"));
            Assert.Contains("'network 1' to 'network 2'", OpenError(model, "network 3"));
            Assert.Contains("'network 1' to 'network 2'", OpenError(model, "network 0"));
        }

        [Fact]
        public void TheOnlyNetwork_OpensAsNetwork_OrNetwork1()
        {
            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"", x: 0);
            var b = c.Node("Text Split", x: 100);
            c.Wire(a, null, b, "T");
            var model = c.Build();

            Assert.StartsWith("network · 2 objects", Lines(CanvasOutline.Overview(model)["networks"]).Single());
            var g = (JObject)Open(model, "network")["groups"][0];
            Assert.Equal("network", (string)g["group"]);
            Assert.Equal("2 objects · the only network", (string)g["summary"]);
            Assert.Equal("network", (string)Open(model, "NETWORK 1")["groups"][0]["group"]);
            Assert.Contains("open it with group='network'", OpenError(model, "network 2"));
        }

        [Fact]
        public void Overview_NextHint_NamesTheNewViews()
        {
            var (model, _, _) = TwoNetworks();
            Assert.StartsWith("group='network 1' lists every object of that network", (string)CanvasOutline.Overview(model)["next"]);

            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"");
            var b = c.Node("Text Split", x: 100);
            c.Wire(a, null, b, "T");
            Assert.StartsWith("group='network' lists every object of the network", (string)CanvasOutline.Overview(c.Build())["next"]);

            var withGroups = new Canvas();
            var inGroup = withGroups.Node("Panel", "IN", OutlineKind.Panel, "\"x\"");
            withGroups.Group("BLOCK", 0, inGroup);
            Assert.DoesNotContain("ungrouped", (string)CanvasOutline.Overview(withGroups.Build())["next"]);
            withGroups.Node("Panel", "LOOSE", OutlineKind.Panel, "\"y\"");
            Assert.Contains("group='ungrouped' lists the objects outside groups", (string)CanvasOutline.Overview(withGroups.Build())["next"]);
        }

        [Fact]
        public void Ungrouped_ListsObjectsOutsideGroups_WithWhatCrossesTheBorder()
        {
            var c = new Canvas();
            var count = c.Node("Number Slider", "COUNT", OutlineKind.Slider, "4 (0..10)", x: 0);
            var series = c.Node("Series", x: 100);
            var result = c.Node("Number", "RESULT", OutlineKind.Parameter, x: 200);
            var after = c.Node("Line", x: 300);
            c.Wire(count, null, series, "C");
            c.Wire(series, "S", result, null, items: 4);
            c.Wire(result, null, after, "A", items: 4);
            c.Group("INPUTS", 0, count);
            c.Group("OUTPUT", 300, after);

            var g = (JObject)Open(c.Build(), "ungrouped")["groups"][0];

            Assert.Equal("ungrouped", (string)g["group"]);
            Assert.Equal("2 objects · outside any group", (string)g["summary"]);
            Assert.Equal("← COUNT [INPUTS]", (string)g["in"]);
            Assert.Equal(new[]
            {
                $"{series.ShortId} Series · C ← COUNT [INPUTS]",
                $"{result.ShortId} Number \"RESULT\" · ← {series.ShortId}.S"
            }, Lines(g["objects"]));
            Assert.Equal("RESULT (4) → OUTPUT", (string)g["out"]);
        }

        [Fact]
        public void Ungrouped_WhenEverythingIsGrouped_IsEmpty_NotAnError()
        {
            var c = new Canvas();
            c.Group("BLOCK", 0, c.Node("Panel", "A", OutlineKind.Panel, "\"a\""));
            var g = (JObject)Open(c.Build(), "UNGROUPED")["groups"][0];
            Assert.Equal("0 objects · outside any group", (string)g["summary"]);
            Assert.Empty((JArray)g["objects"]);
        }

        [Fact]
        public void AGroupWithTheSameName_Wins_ButAGroupStartingWithIt_DoesNot()
        {
            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"");
            var real = c.Group("Ungrouped", 0, a);
            c.Node("Panel", "LOOSE", OutlineKind.Panel, "\"x\"");
            var model = c.Build();

            var g = (JObject)Open(model, "ungrouped")["groups"][0];
            Assert.Equal("Ungrouped", (string)g["group"]);
            Assert.Equal(real.ShortId, (string)g["id"]);

            var c2 = new Canvas();
            c2.Group("UNGROUPED STUFF", 0, c2.Node("Panel", "A", OutlineKind.Panel, "\"a\""));
            c2.Node("Panel", "LOOSE", OutlineKind.Panel, "\"x\"");
            var region = (JObject)Open(c2.Build(), "ungrouped")["groups"][0];
            Assert.Equal("ungrouped", (string)region["group"]);
            Assert.Equal("1 object · outside any group", (string)region["summary"]);
        }

        [Fact]
        public void NetworkNames_OnACanvasWithGroups_AreGroupNames_AndTheErrorSaysWhy()
        {
            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"");
            var b = c.Node("Text Split", x: 100);
            c.Wire(a, null, b, "T");
            c.Group("BLOCK", 0, a);
            var error = OpenError(c.Build(), "network 1");
            Assert.StartsWith("no group named 'network 1'.", error);
            Assert.Contains("networks are named only on a canvas without groups", error);
        }

        [Fact]
        public void ABigNetwork_IsCappedAt250Lines_AndTheRestAreNamed()
        {
            var c = new Canvas();
            var chain = Enumerable.Range(0, 300).Select(i => c.Node("Number", $"N{i}", OutlineKind.Parameter, x: i)).ToList();
            for (int i = 0; i < chain.Count - 1; i++)
                c.Wire(chain[i], null, chain[i + 1], null);

            var g = (JObject)Open(c.Build(), "network")["groups"][0];

            Assert.Equal(CanvasOutline.MaxRegionLines, ((JArray)g["objects"]).Count);
            Assert.Equal(300 - CanvasOutline.MaxRegionLines, (int)g["more"]);
            var note = (string)g["note"];
            Assert.StartsWith("50 more not shown (at most 250 lines per call); use id= on one of them: N250, N251,", note);
            Assert.EndsWith("+30 more", note);
        }

        [Fact]
        public void AGroupAlone_KeepsItsCapOf150()
        {
            var c = new Canvas();
            var members = Enumerable.Range(0, 200).Select(i => (object)c.Node("Panel", kind: OutlineKind.Panel, x: i)).ToArray();
            c.Group("BIG", 0, members);

            var g = (JObject)Open(c.Build(), "BIG")["groups"][0];

            Assert.Equal(CanvasOutline.MaxObjectLines, ((JArray)g["objects"]).Count);
            Assert.Contains("(at most 150 lines per call)", (string)g["note"]);
        }
    }
}
