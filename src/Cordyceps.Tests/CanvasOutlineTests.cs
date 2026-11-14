using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Outline;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Unit tests for <see cref="CanvasOutline"/>, behind gh_canvas(action='outline'): the overview
    /// (groups, networks, controls, problems), a group's detail, the view around one object, Lichen
    /// Thalli, and finding groups by name.
    /// </summary>
    public class CanvasOutlineTests
    {
        /// <summary>Builds a small canvas the way the Grasshopper adapter would.</summary>
        internal sealed class Canvas
        {
            private readonly List<OutlineNode> _nodes = new List<OutlineNode>();
            private readonly List<OutlineGroup> _groups = new List<OutlineGroup>();
            private int _next;

            private string NewId() => $"{++_next:x8}-0000-4000-8000-000000000000";

            public OutlineNode Node(string type, string nickname = null, OutlineKind kind = OutlineKind.Component,
                string value = null, float x = 0, float y = 0, bool unique = true)
            {
                var id = NewId();
                var n = new OutlineNode
                {
                    Id = id, ShortId = id.Substring(0, 8), Type = type, Nickname = nickname, NicknameUnique = unique,
                    Kind = kind, Value = value, X = x, Y = y
                };
                _nodes.Add(n);
                return n;
            }

            public OutlineOutput Output(OutlineNode n, string name, int items)
            {
                var o = n.Outputs.FirstOrDefault(p => p.Name == name);
                if (o == null) n.Outputs.Add(o = new OutlineOutput { Name = name });
                o.Items = items;
                return o;
            }

            public OutlinePort Input(OutlineNode n, string name)
            {
                var i = n.Inputs.FirstOrDefault(p => p.Name == name);
                if (i == null) n.Inputs.Add(i = new OutlinePort { Name = name });
                return i;
            }

            public void Wire(OutlineNode from, string fromPort, OutlineNode to, string toPort, int items = 1)
            {
                var name = fromPort ?? "out";
                var o = from.Outputs.FirstOrDefault(p => p.Name == name) ?? Output(from, name, items);
                o.Recipients.Add(to.Id);
                Input(to, toPort ?? "in").Sources.Add(new OutlineSource(from.Id, from.Kind == OutlineKind.Component ? fromPort : null));
            }

            public OutlineGroup Group(string name, float x, params object[] members)
            {
                var id = NewId();
                var g = new OutlineGroup { Id = id, ShortId = id.Substring(0, 8), Name = name, Colour = "#DCEDC8", X = x };
                foreach (var m in members)
                {
                    if (m is OutlineNode node) g.Members.Add(node.Id);
                    else if (m is OutlineGroup child) g.Children.Add(child.Id);
                }
                _groups.Add(g);
                return g;
            }

            public OutlineModel Build() => new OutlineModel("test.gh", _nodes, _groups);
        }

        internal static List<string> Lines(JToken t) => t?.Values<string>().ToList() ?? new List<string>();

        // ---- overview -------------------------------------------------------------------------------

        [Fact]
        public void Overview_GroupsInFlowOrder_WithWhatCrossesTheirBorders()
        {
            var c = new Canvas();
            var count = c.Node("Number Slider", "COUNT", OutlineKind.Slider, "4 (0..10)", x: 0);
            var nums = c.Node("Panel", "NUMS", OutlineKind.Panel, "\"1 | 2\" (2 lines)", x: 0, y: 50);
            var series = c.Node("Series", x: 200);
            var result = c.Node("Number", "RESULT", OutlineKind.Parameter, x: 300);
            var add = c.Node("Addition", x: 250);
            add.Level = "error";
            add.Message = "Data conversion failed from Text to Number";
            c.Wire(count, null, series, "C");
            c.Wire(nums, null, series, "S", items: 2);
            c.Wire(series, "S", result, null, items: 4);
            // MATHS sits to the left of INPUTS on the canvas, but INPUTS feeds it, so INPUTS comes first.
            c.Group("MATHS", -500, series, result, add);
            c.Group("INPUTS", 0, count, nums);

            var o = CanvasOutline.Overview(c.Build());

            Assert.True((bool)o["success"]);
            Assert.Equal("5 objects · 2 groups · 1 error · 0 warnings", (string)o["summary"]);
            Assert.Equal(new[] { "COUNT = 4 (0..10) [INPUTS]" }, Lines(o["controls"]));
            var groups = Lines(o["groups"]);
            Assert.Equal(2, groups.Count);
            Assert.StartsWith("INPUTS · #DCEDC8 · 2 objects", groups[0]);
            Assert.Contains("out: COUNT (1) → MATHS; NUMS (2) → MATHS", groups[0]);
            Assert.StartsWith("MATHS · #DCEDC8 · 3 objects · 1 error", groups[1]);
            Assert.Contains("in ← COUNT, NUMS", groups[1]);
            Assert.Null(o["ungrouped"]);
            Assert.Contains(Lines(o["problems"]), p => p.Contains("Addition [MATHS] error: Data conversion failed"));
            Assert.Contains("group='NAME'", (string)o["next"]);
        }

        [Fact]
        public void Overview_NestedGroupUnderItsParent_AndUngroupedObjects()
        {
            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"");
            var b = c.Node("Panel", "B", OutlineKind.Panel, "\"b\"");
            var loose = c.Node("Panel", "LOOSE", OutlineKind.Panel, "\"x\"");
            var child = c.Group("CHILD", 0, a);
            c.Group("PARENT", 0, b, child);

            var o = CanvasOutline.Overview(c.Build());

            var groups = Lines(o["groups"]);
            Assert.Single(groups);
            Assert.StartsWith("PARENT · #DCEDC8 · 2 objects", groups[0]);
            Assert.Contains("contains: CHILD (1 object)", groups[0]);
            Assert.Equal("1 object · inputs: LOOSE = \"x\"", (string)o["ungrouped"]);
        }

        [Fact]
        public void Overview_WithoutGroups_DescribesEachNetwork()
        {
            var c = new Canvas();
            var count = c.Node("Number Slider", "COUNT", OutlineKind.Slider, "12 (1..20)", x: 0);
            var divide = c.Node("Divide Curve", x: 100);
            var pts = c.Node("Point", "PTS", OutlineKind.Parameter, x: 200);
            c.Wire(count, null, divide, "N");
            c.Wire(divide, "P", pts, null, items: 12);
            c.Output(pts, "out", 12);

            var radius = c.Node("Panel", "RADIUS", OutlineKind.Panel, "\"2.5\"", x: 0, y: 300);
            var circle = c.Node("Circle", "RING", x: 100, y: 300);
            var loft = c.Node("Loft", x: 200, y: 300);
            c.Wire(radius, null, circle, "R");
            c.Wire(circle, "C", loft, "C");
            c.Output(loft, "L", 1);

            var note = c.Node("Panel", "NOTE", OutlineKind.Panel, "\"read me\"", x: 500);

            var o = CanvasOutline.Overview(c.Build());

            Assert.Null(o["groups"]);
            var networks = Lines(o["networks"]);
            Assert.Equal(2, networks.Count);
            Assert.All(networks, n => Assert.Contains("3 objects", n));
            Assert.Contains(networks, n => n.Contains("inputs: COUNT = 12 (1..20)") && n.Contains("ends: PTS (12)"));
            Assert.Contains(networks, n => n.Contains("inputs: RADIUS = \"2.5\"") && n.Contains("named: RING")
                                            && n.Contains($"ends: {loft.ShortId} Loft (1)"));
            Assert.Equal("1 object: NOTE", (string)o["unconnected"]);
            Assert.Contains("id='NAME'", (string)o["next"]);
            Assert.Equal(new[] { "COUNT = 12 (1..20)" }, Lines(o["controls"]));
        }

        // ---- group detail ---------------------------------------------------------------------------

        [Fact]
        public void Group_ListsUpstreamFirst_WithPortsSettingsAndOutsideSources()
        {
            var c = new Canvas();
            var edges = c.Node("Curve", "EDGES", OutlineKind.Parameter, x: -500);
            c.Group("OTHER", -500, edges);

            // Positions deliberately against the flow: the consumer is leftmost.
            var item = c.Node("List Item", x: 0);
            var split = c.Node("Text Split", x: 10);
            var panel = c.Node("Panel", "INDEX", OutlineKind.Panel, "\"0 | 2\" (2 lines)", x: 20);
            c.Wire(panel, null, split, "T");
            c.Input(split, "C").Stored = ",";
            c.Wire(split, "R", item, "i");
            c.Input(item, "i").Modifiers = "graft";
            c.Wire(edges, null, item, "L", items: 8);
            var after = c.Node("Line", x: 900);
            c.Wire(item, "i", after, "A", items: 4);
            var block = c.Group("BLOCK", 0, item, split, panel);

            var o = CanvasOutline.Groups(c.Build(), new[] { block });
            var g = (JObject)o["groups"][0];

            Assert.Equal("BLOCK", (string)g["group"]);
            Assert.Equal("3 objects · #DCEDC8", (string)g["summary"]);
            Assert.Equal("← EDGES [OTHER]", (string)g["in"]);
            var lines = Lines(g["objects"]);
            Assert.Equal(3, lines.Count);
            Assert.Equal($"{panel.ShortId} Panel \"INDEX\" = \"0 | 2\" (2 lines)", lines[0]);
            Assert.Equal($"{split.ShortId} Text Split · T ← INDEX · C = ,", lines[1]);
            Assert.Equal($"{item.ShortId} List Item · i ← {split.ShortId}.R (graft) · L ← EDGES [OTHER]", lines[2]);
            Assert.Equal($"{item.ShortId}.i (4) → {after.ShortId} Line", (string)g["out"]);
        }

        [Fact]
        public void Group_SourceWithASharedNickname_IsNamedByShortIdAndType()
        {
            var c = new Canvas();
            var one = c.Node("Text Split", "Split", unique: false, x: 0);
            c.Node("Text Split", "Split", unique: false, x: 0, y: 100);
            var item = c.Node("List Item", x: 100);
            c.Wire(one, "R", item, "i");
            var block = c.Group("BLOCK", 0, item);

            var g = (JObject)CanvasOutline.Groups(c.Build(), new[] { block })["groups"][0];

            Assert.Equal($"← {one.ShortId}.R (Text Split)", (string)g["in"]);
        }

        [Fact]
        public void Group_ChildGroupIsOneLine_AndThallusShowsTheAuthorsText()
        {
            var c = new Canvas();
            var a = c.Node("Panel", "A", OutlineKind.Panel, "\"a\"", x: 0);
            var b = c.Node("Panel", "B", OutlineKind.Panel, "\"b\"", x: 10);
            var own = c.Node("Merge", x: 100);
            c.Wire(a, null, own, "D1");
            var inner = c.Group("INNER", 0, a, b);
            inner.IsThallus = true;
            inner.Description = "Two inputs";
            var outer = c.Group("OUTER", 0, inner, own);
            outer.IsThallus = true;
            outer.Description = "Panels between curves.\nEdges use nodes.";
            outer.Properties.Add(new KeyValuePair<string, string>("purpose", "panels"));
            outer.ToLichen = true;
            var model = c.Build();

            var overview = CanvasOutline.Overview(model);
            Assert.Equal("3 objects · 2 groups (2 Thalli) · 0 errors · 0 warnings", (string)overview["summary"]);
            var line = Lines(overview["groups"]).Single();
            Assert.StartsWith("THALLUS OUTER · #DCEDC8 · 3 objects", line);
            Assert.Contains("author's description: \"Panels between curves. / Edges use nodes.\"", line);
            Assert.Contains("T → Lichen", line);
            Assert.Contains("contains: THALLUS INNER (2 objects)", line);

            var g = (JObject)CanvasOutline.Groups(model, new[] { outer })["groups"][0];
            Assert.Equal("THALLUS OUTER", (string)g["group"]);
            Assert.Equal("Panels between curves.\nEdges use nodes.", (string)g["authorDescription"]);
            Assert.Equal("purpose = panels", (string)g["authorProperties"]);
            var lines = Lines(g["objects"]);
            Assert.Equal(2, lines.Count);
            Assert.Equal("THALLUS INNER · 2 objects · author's description: \"Two inputs\" · open with group='INNER'", lines[0]);
            Assert.Equal($"{own.ShortId} Merge · D1 ← A", lines[1]);

            var innerDetail = (JObject)CanvasOutline.Groups(model, new[] { inner })["groups"][0];
            Assert.StartsWith("2 objects · inside THALLUS OUTER", (string)innerDetail["summary"]);
        }

        [Fact]
        public void Group_ThallusOutputWire_ShowsTheThallusName()
        {
            var c = new Canvas();
            var root = c.Node("Lichen");
            c.Input(root, "T").Sources.Add(new OutlineSource(null, null, "THALLUS ROOF"));
            var block = c.Group("EXPORT", 0, root);

            var g = (JObject)CanvasOutline.Groups(c.Build(), new[] { block })["groups"][0];

            Assert.Equal("← THALLUS ROOF", (string)g["in"]);
            Assert.Equal($"{root.ShortId} Lichen · T ← THALLUS ROOF", Lines(g["objects"]).Single());
        }

        [Fact]
        public void Group_TooManyObjects_IsCappedAndSaysHowManyAreLeft()
        {
            var c = new Canvas();
            var members = Enumerable.Range(0, 200).Select(i => (object)c.Node("Panel", kind: OutlineKind.Panel, x: i)).ToArray();
            var block = c.Group("BIG", 0, members);

            var g = (JObject)CanvasOutline.Groups(c.Build(), new[] { block })["groups"][0];

            Assert.Equal(CanvasOutline.MaxObjectLines, ((JArray)g["objects"]).Count);
            Assert.Equal(200 - CanvasOutline.MaxObjectLines, (int)g["more"]);
            Assert.Contains("more not shown", (string)g["note"]);
        }

        // ---- around ---------------------------------------------------------------------------------

        [Fact]
        public void Around_FollowsWiresBothWays_ToTheGivenDepth()
        {
            var c = new Canvas();
            var chain = Enumerable.Range(0, 5).Select(i => c.Node("Number", $"N{i}", OutlineKind.Parameter, x: i * 100)).ToList();
            for (int i = 0; i < 4; i++)
                c.Wire(chain[i], null, chain[i + 1], null);
            var model = c.Build();

            var o = CanvasOutline.Around(model, chain[2], 1);
            var lines = Lines(o["objects"]);
            Assert.Equal("N2", (string)o["around"]);
            Assert.Equal(1, (int)o["depth"]);
            Assert.Equal(3, lines.Count);
            Assert.StartsWith(chain[1].ShortId, lines[0]);
            Assert.StartsWith($"* {chain[2].ShortId} Number \"N2\" · ← N1", lines[1]);
            Assert.Equal("N0", (string)o["fedFrom"]);
            Assert.Equal("N4", (string)o["feeds"]);

            var wide = CanvasOutline.Around(model, chain[2], 0);
            Assert.Equal(CanvasOutline.DefaultDepth, (int)wide["depth"]);
            Assert.Equal(5, ((JArray)wide["objects"]).Count);
            Assert.Null(wide["fedFrom"]);
            Assert.Null(wide["feeds"]);
        }

        // ---- finding groups -------------------------------------------------------------------------

        [Fact]
        public void FindGroups_ExactThenCaseThenUniqueStart_NeverALooseGuess()
        {
            var c = new Canvas();
            var curves = c.Group("UPPER CURVES", 0);
            var diagonals = c.Group("UPPER DIAGONALS", 0);
            var gridLines = c.Group("GRID LINES (sorted W→E)", 0);
            var model = c.Build();

            var errors = new List<string>();
            Assert.Equal(new[] { diagonals }, CanvasOutline.FindGroups(model, new[] { "UPPER DIAGONALS" }, errors));
            Assert.Equal(new[] { diagonals }, CanvasOutline.FindGroups(model, new[] { "upper diagonals" }, errors));
            Assert.Equal(new[] { gridLines }, CanvasOutline.FindGroups(model, new[] { "GRID LINES" }, errors));
            Assert.Equal(new[] { curves }, CanvasOutline.FindGroups(model, new[] { curves.ShortId }, errors));
            Assert.Empty(errors);

            Assert.Empty(CanvasOutline.FindGroups(model, new[] { "UPPER" }, errors));
            Assert.Contains("matches 2 groups", errors.Single());

            errors.Clear();
            Assert.Empty(CanvasOutline.FindGroups(model, new[] { "UPPER DIAGONLS" }, errors));
            Assert.Contains("Did you mean 'UPPER DIAGONALS'?", errors.Single());
        }

        [Fact]
        public void TopoOrder_KeepsEverything_EvenInACycle()
        {
            var preds = new Dictionary<string, string[]> { ["a"] = new[] { "b" }, ["b"] = new[] { "a" }, ["c"] = new string[0] };
            var order = CanvasOutline.TopoOrder(new[] { "a", "b", "c" }, id => preds[id], id => (id == "a" ? 1f : id == "b" ? 2f : 3f, 0f));
            Assert.Equal(new[] { "c", "a", "b" }, order);
        }
    }
}
