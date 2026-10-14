using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// gh_batch's port rule: exact names only; a partial name is suggested, never used. The
    /// Cull Pattern case is the spec's own example ('Pattern' → "Did you mean 'P'?").
    /// </summary>
    public class StrictPortLookupTests
    {
        private static readonly IReadOnlyList<ParamIdentity> CullInputs = new[]
        {
            new ParamIdentity("List", "L"),
            new ParamIdentity("Cull Pattern", "P")
        };

        [Theory]
        [InlineData("L", 0)]
        [InlineData("P", 1)]
        [InlineData("List", 0)]
        [InlineData("Cull Pattern", 1)]
        [InlineData("1", 1)]
        [InlineData("l", 0)]
        [InlineData("cull pattern", 1)]
        public void Resolve_ExactOrUniqueCaseless_Resolves(string spec, int index)
        {
            var r = StrictPortLookup.Resolve(spec, CullInputs, isInput: true, ownerLabel: "'cull' (Cull Pattern)");
            Assert.True(r.IsResolved, r.Error);
            Assert.Equal(index, r.Index);
        }

        [Fact]
        public void Resolve_PartialName_IsAnErrorWithSuggestion()
        {
            var r = StrictPortLookup.Resolve("Pattern", CullInputs, true, "'cull' (Cull Pattern)");
            Assert.False(r.IsResolved);
            Assert.Contains("no input named 'Pattern' on 'cull' (Cull Pattern)", r.Error);
            Assert.Contains("Did you mean 'P' (Cull Pattern)?", r.Error);
            Assert.Contains("Inputs: 0 L (List), 1 P (Cull Pattern)", r.Error);
        }

        [Fact]
        public void Resolve_IndexOutOfRange_NamesTheRange()
        {
            var r = StrictPortLookup.Resolve("5", CullInputs, true, "'cull' (Cull Pattern)");
            Assert.False(r.IsResolved);
            Assert.Contains("input index 5 doesn't exist", r.Error);
        }

        [Fact]
        public void Resolve_TwoExactMatches_AsksForIndex()
        {
            var ports = new[] { new ParamIdentity("Data", "D"), new ParamIdentity("Data", "D") };
            var r = StrictPortLookup.Resolve("D", ports, true, "'m' (Merge)");
            Assert.False(r.IsResolved);
            Assert.Contains("use the index", r.Error);
        }

        [Fact]
        public void Resolve_ExactCaseWinsOverACaselessTwin()
        {
            var ports = new[] { new ParamIdentity("Total", "T"), new ParamIdentity("Twist", "t") };
            Assert.Equal(0, StrictPortLookup.Resolve("T", ports, true, "x").Index);
            Assert.Equal(1, StrictPortLookup.Resolve("t", ports, true, "x").Index);
        }

        [Fact]
        public void Resolve_CaselessMatchesTwo_AsksForExactCase()
        {
            var ports = new[] { new ParamIdentity("alpha", "x"), new ParamIdentity("ALPHA", "y") };
            var r = StrictPortLookup.Resolve("Alpha", ports, true, "x");
            Assert.False(r.IsResolved);
            Assert.Contains("write the exact case or the index", r.Error);
        }

        [Fact]
        public void Resolve_OutputSide_SaysOutput()
        {
            var r = StrictPortLookup.Resolve("Q", new[] { new ParamIdentity("Item", "i") }, false, "'item' (List Item)");
            Assert.Contains("no output named 'Q'", r.Error);
            Assert.Contains("Outputs: 0 i (Item)", r.Error);
        }
    }

    /// <summary>
    /// The strict component-type rule, against a small catalog shaped like a real install:
    /// Merge with a deprecated twin, Line as both a component and a parameter, Addition with five
    /// deprecated versions.
    /// </summary>
    public class TypeNameRuleTests
    {
        private static readonly Guid MergeGuid = Guid.Parse("3cadddef-1e2b-4c09-9390-0e8f78f7609f");
        private static readonly Guid LineGuid = Guid.Parse("4c4e56eb-2f04-43f9-95a3-cc46a14f495a");
        private static readonly Guid LineParamGuid = Guid.Parse("8529dbdf-9b6f-42e9-8e1f-c7a2bde56a70");

        private static TypeCatalog Catalog() => new TypeCatalog(new[]
        {
            new TypeCandidate { Name = "Merge", Category = "Sets", SubCategory = "Tree", Guid = MergeGuid, Plugin = "Grasshopper" },
            new TypeCandidate { Name = "Merge", Category = "Sets", SubCategory = "Tree", Guid = Guid.NewGuid(), Deprecated = true },
            new TypeCandidate { Name = "Line", Category = "Curve", SubCategory = "Primitive", Guid = LineGuid },
            new TypeCandidate { Name = "Line", Category = "Params", SubCategory = "Geometry", Guid = LineParamGuid },
            new TypeCandidate { Name = "Addition", Category = "Maths", SubCategory = "Operators", Guid = Guid.NewGuid() },
            new TypeCandidate { Name = "Addition", Category = "Vector", SubCategory = "Vector", Guid = Guid.NewGuid(), Deprecated = true },
            new TypeCandidate { Name = "Addition", Category = "Display", SubCategory = "Colour", Guid = Guid.NewGuid(), Deprecated = true },
            new TypeCandidate { Name = "Cull Pattern", Category = "Sets", SubCategory = "Sequence", Guid = Guid.NewGuid() },
            new TypeCandidate { Name = "Old Thing", Category = "Sets", SubCategory = "List", Guid = Guid.NewGuid(), Deprecated = true },
            new TypeCandidate { Name = "Curve", Category = "Params", SubCategory = "Geometry", Guid = Guid.NewGuid() }
        });

        [Theory]
        [InlineData("Merge")]
        [InlineData("merge")]
        [InlineData("Sets/Merge")]
        [InlineData("Sets/Tree/Merge")]
        [InlineData("3cadddef-1e2b-4c09-9390-0e8f78f7609f")]
        public void Resolve_Merge_ResolvesToTheCurrentOne(string text)
        {
            var r = TypeNameRule.Resolve(text, Catalog(), paramsOnly: false);
            Assert.Equal(TypeResolutionKind.Resolved, r.Kind);
            Assert.Equal(MergeGuid, r.Match.Guid);
            Assert.False(r.UsedDeprecated);
        }

        [Fact]
        public void Resolve_Line_IsAmbiguousAndListsBoth()
        {
            var r = TypeNameRule.Resolve("Line", Catalog(), false);
            Assert.Equal(TypeResolutionKind.Ambiguous, r.Kind);
            Assert.Equal(2, r.Candidates.Count);
            Assert.Contains(r.Candidates, c => c.Guid == LineGuid);
            Assert.Contains(r.Candidates, c => c.Guid == LineParamGuid);
            Assert.Contains("Curve/Primitive/Line (component", r.Candidates.First(c => c.Guid == LineGuid).Describe());
        }

        [Fact]
        public void Resolve_Line_DisambiguatedByCategoryOrParamShortcut()
        {
            Assert.Equal(LineGuid, TypeNameRule.Resolve("Curve/Line", Catalog(), false).Match.Guid);
            Assert.Equal(LineParamGuid, TypeNameRule.Resolve("Line", Catalog(), paramsOnly: true).Match.Guid);
        }

        [Fact]
        public void Resolve_DeprecatedIgnoredWhenACurrentOneExists()
        {
            var r = TypeNameRule.Resolve("Addition", Catalog(), false);
            Assert.Equal(TypeResolutionKind.Resolved, r.Kind);
            Assert.Equal("Maths", r.Match.Category);
        }

        [Fact]
        public void Resolve_OnlyDeprecatedMatch_IsUsedAndFlagged()
        {
            var r = TypeNameRule.Resolve("Old Thing", Catalog(), false);
            Assert.Equal(TypeResolutionKind.Resolved, r.Kind);
            Assert.True(r.UsedDeprecated);
        }

        [Fact]
        public void Resolve_NeverLoose_SuggestsInstead()
        {
            var r = TypeNameRule.Resolve("Cull", Catalog(), false);
            Assert.Equal(TypeResolutionKind.NotFound, r.Kind);
            Assert.Null(r.Match);
            Assert.Contains(r.Suggestions, c => c.Name == "Cull Pattern");

            var typo = TypeNameRule.Resolve("Cull Patern", Catalog(), false);
            Assert.Equal(TypeResolutionKind.NotFound, typo.Kind);
            Assert.Contains(typo.Suggestions, c => c.Name == "Cull Pattern");
        }

        [Fact]
        public void Resolve_UninstalledCategory_IsReported()
        {
            var r = TypeNameRule.Resolve("Kangaroo2/Goals-Pt/Anchor", Catalog(), false);
            Assert.Equal(TypeResolutionKind.CategoryMissing, r.Kind);
            Assert.Equal("Kangaroo2", r.Category);
        }

        [Fact]
        public void Resolve_UnknownGuid_IsNotInstalled()
        {
            var r = TypeNameRule.Resolve(Guid.NewGuid().ToString(), Catalog(), false);
            Assert.Equal(TypeResolutionKind.GuidNotInstalled, r.Kind);
        }

        [Fact]
        public void Resolve_ParamShortcutOnAComponent_ReportsNonParamMatch()
        {
            var r = TypeNameRule.Resolve("Cull Pattern", Catalog(), paramsOnly: true);
            Assert.Equal(TypeResolutionKind.NotFound, r.Kind);
            Assert.Single(r.NonParamMatches);
        }
    }

    public class BatchValueTests
    {
        private static BatchValue P(string json, out string error) => BatchValue.Parse(JToken.Parse(json), out error);

        [Fact]
        public void Parse_Scalars()
        {
            Assert.Equal(2, P("2", out _).Number);
            Assert.True(P("2", out _).IsWhole);
            Assert.False(P("2.5", out _).IsWhole);
            Assert.True(P("true", out _).Boolean);
            Assert.Equal("abc", P("\"abc\"", out _).Text);
            Assert.Equal(BatchValueKind.Null, P("null", out _).Kind);
        }

        [Fact]
        public void Parse_GeometryObjects()
        {
            var pt = P(@"{""point"": [0, 0, 3000]}", out _);
            Assert.Equal(BatchValueKind.Point, pt.Kind);
            Assert.Equal(new[] { 0.0, 0, 3000 }, pt.Coords);

            var flat = P(@"{""point"": [1, 2]}", out _);
            Assert.Equal(new[] { 1.0, 2, 0 }, flat.Coords);

            Assert.Equal(BatchValueKind.Vector, P(@"{""vector"": [0, 0, 1]}", out _).Kind);
            Assert.Equal(new[] { 0.0, 1 }, P(@"{""domain"": [0, 1]}", out _).Coords);
        }

        [Fact]
        public void Parse_List_IsFlat()
        {
            var list = P("[1, 2, 3]", out var error);
            Assert.Null(error);
            Assert.Equal(3, list.AsList().Count);
            Assert.Null(P("[[1], [2]]", out error));
            Assert.Contains("nested lists", error);
        }

        [Theory]
        [InlineData(@"{""domain"": [0]}", "exactly two numbers")]
        [InlineData(@"{""point"": [1, 2, 3, 4]}", "two or three numbers")]
        [InlineData(@"{""plane"": [0, 0, 0]}", "unknown value type 'plane'")]
        [InlineData(@"{""point"": [1], ""vector"": [2]}", "exactly one of")]
        [InlineData(@"{""point"": [""a"", 1]}", "list of numbers")]
        public void Parse_BadValueObjects(string json, string expected)
        {
            Assert.Null(P(json, out var error));
            Assert.Contains(expected, error);
        }
    }

    public class BatchNamesTests
    {
        [Theory]
        [InlineData("split", true)]
        [InlineData("merge_1", true)]
        [InlineData("top-chords", true)]
        [InlineData("a.b", false)]
        [InlineData("a b", false)]
        [InlineData("", false)]
        public void IsValidRef(string name, bool valid) => Assert.Equal(valid, BatchNames.IsValidRef(name));

        [Fact]
        public void Closest_PrefersCaseOnlyThenNearMisses()
        {
            var known = new[] { "nickname", "inputs", "outputs", "inputModifiers" };
            Assert.Equal("nickname", BatchNames.Closest("nickName", known));
            Assert.Equal("inputs", BatchNames.Closest("inptus", known));
            Assert.Null(BatchNames.Closest("zzzzzz", known));
        }

        [Theory]
        [InlineData("9ac1d04b", true)]
        [InlineData("9AC1D04B-12", true)]
        [InlineData("9ac1d04", false)]
        [InlineData("split123", false)]
        public void LooksLikeGuidPrefix(string text, bool expected) => Assert.Equal(expected, BatchNames.LooksLikeGuidPrefix(text));
    }

    public class GuidPrefixIndexTests
    {
        [Fact]
        public void Shortest_IsEightCharactersUnlessShared()
        {
            var a = Guid.Parse("9ac1d04b-0000-0000-0000-000000000001");
            var b = Guid.Parse("9ac1d04b-0000-0000-0000-000000000002");
            var c = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var index = new GuidPrefixIndex(new[] { a, b, c });

            Assert.Equal("11111111", index.Shortest(c));
            Assert.Equal(a.ToString("D"), index.Shortest(a));
            Assert.Equal(2, index.StartingWith("9AC1D04B").Count);
            Assert.Single(index.StartingWith("1111"));
        }

        [Fact]
        public void Shortest_WorksForAnIdNotInTheIndex()
        {
            var index = new GuidPrefixIndex(new[] { Guid.Parse("9ac1d04b-0000-0000-0000-000000000001") });
            Assert.Equal("9ac1d04c", index.Shortest(Guid.Parse("9ac1d04c-0000-0000-0000-000000000001")));
            Assert.Equal("ffffffff", index.Shortest(Guid.Parse("ffffffff-0000-0000-0000-000000000001")));
        }
    }

    public class AutoLayoutTests
    {
        [Fact]
        public void Place_ChainFlowsLeftToRightFromUpstream()
        {
            var upstream = new RectangleF(0, 0, 100, 40);
            var nodes = new List<AutoLayout.Node>
            {
                new AutoLayout.Node { Id = 0, Size = new SizeF(80, 40) },
                new AutoLayout.Node { Id = 1, Size = new SizeF(80, 40) },
                new AutoLayout.Node { Id = 2, Size = new SizeF(80, 40) }
            };
            nodes[0].Upstream.Add(upstream);

            var pos = AutoLayout.Place(nodes, new[] { (0, 1), (1, 2) }, new List<RectangleF> { upstream });

            Assert.True(pos[0].X >= upstream.Right);
            Assert.True(pos[1].X > pos[0].X + 80);
            Assert.True(pos[2].X > pos[1].X + 80);
        }

        [Fact]
        public void Place_SameDepthStacksVertically()
        {
            var nodes = new List<AutoLayout.Node>
            {
                new AutoLayout.Node { Id = 0, Size = new SizeF(80, 40) },
                new AutoLayout.Node { Id = 1, Size = new SizeF(80, 40) }
            };
            var pos = AutoLayout.Place(nodes, Array.Empty<(int, int)>(), new List<RectangleF>());
            Assert.Equal(pos[0].X, pos[1].X);
            Assert.True(pos[1].Y >= pos[0].Y + 40);
        }

        [Fact]
        public void Place_NeverOverlapsExistingObjects()
        {
            var upstream = new RectangleF(0, 0, 100, 40);
            var blocker = new RectangleF(140, -20, 200, 300);
            var nodes = new List<AutoLayout.Node> { new AutoLayout.Node { Id = 0, Size = new SizeF(80, 40) } };
            nodes[0].Upstream.Add(upstream);

            var pos = AutoLayout.Place(nodes, Array.Empty<(int, int)>(), new List<RectangleF> { upstream, blocker });
            var box = new RectangleF(pos[0], new SizeF(80, 40));
            Assert.False(box.IntersectsWith(blocker));
        }

        [Fact]
        public void Place_InsertedComponentSitsBetweenTheWireEnds()
        {
            var src = new RectangleF(0, 0, 100, 40);
            var tgt = new RectangleF(500, 0, 100, 40);
            var nodes = new List<AutoLayout.Node> { new AutoLayout.Node { Id = 0, Size = new SizeF(80, 40), InsertSource = src, InsertTarget = tgt } };
            var pos = AutoLayout.Place(nodes, Array.Empty<(int, int)>(), new List<RectangleF> { src, tgt });
            Assert.True(pos[0].X > src.Right && pos[0].X + 80 < tgt.Left);
        }

        [Fact]
        public void Place_EmptyCanvas_StartsNearOrigin()
        {
            var nodes = new List<AutoLayout.Node> { new AutoLayout.Node { Id = 0, Size = new SizeF(80, 40) } };
            var pos = AutoLayout.Place(nodes, Array.Empty<(int, int)>(), new List<RectangleF>());
            Assert.Equal(new PointF(100, 100), pos[0]);
        }
    }

    public class BatchReportTests
    {
        [Fact]
        public void ToJson_SuccessAndOkAlwaysAgree()
        {
            var ok = JObject.Parse(new BatchReport { Changed = true }.ToJson());
            Assert.True((bool)ok["success"]);
            Assert.True((bool)ok["ok"]);

            var failed = new BatchReport();
            failed.Errors.Add("wires[3]: no input named 'Pattern'");
            var f = JObject.Parse(failed.ToJson());
            Assert.False((bool)f["success"]);
            Assert.False((bool)f["ok"]);
            Assert.False((bool)f["changed"]);
            Assert.Null(f["counts"]);
            Assert.True(McpResultFormatter.IsErrorResult(failed.ToJson()));
        }

        [Fact]
        public void ToJson_CapsListsAndCutsLongMessages()
        {
            var r = new BatchReport();
            for (int i = 0; i < 80; i++) r.Errors.Add($"e{i}");
            r.Warnings.Add(new string('w', 500));
            for (int i = 0; i < 30; i++) r.Runtime.Add(new RuntimeNote { Ref = "a", Level = "Warning", Message = "m" });

            var o = JObject.Parse(r.ToJson());
            Assert.Equal(BatchReport.MaxErrors + 1, ((JArray)o["errors"]).Count);
            Assert.Equal("(+30 more errors)", (string)o["errors"].Last);
            Assert.Equal(BatchReport.MaxMessageLength, ((string)o["warnings"][0]).Length);
            Assert.Equal(BatchReport.MaxRuntime + 1, ((JArray)o["runtime"]).Count);
        }

        [Fact]
        public void ToJson_LargeBatchListsSomeRefsAndCountsTheRest()
        {
            var r = new BatchReport { Changed = true };
            for (int i = 0; i < 300; i++)
                r.Created.Add(new KeyValuePair<string, string>($"a{i}", "3f2a9c1e"));

            var json = r.ToJson();
            var o = JObject.Parse(json);
            Assert.Equal(BatchReport.MaxCreated, ((JObject)o["created"]).Count);
            Assert.Equal(300 - BatchReport.MaxCreated, (int)o["createdMore"]);
            Assert.True(json.Length < 4096, $"result was {json.Length} chars");
        }

        [Fact]
        public void ToJson_TypicalSuccessIsSmall()
        {
            var r = new BatchReport { Changed = true, Solved = true, ApplyMs = 420, SolveMs = 1300 };
            foreach (var name in new[] { "tbl", "split", "item", "out" })
                r.Created.Add(new KeyValuePair<string, string>(name, "3f2a9c1e"));
            r.Counts.Components = 4;
            r.Counts.Wires = 5;
            r.Counts.Groups = 1;

            var json = r.ToJson();
            var o = JObject.Parse(json);
            Assert.Equal("3f2a9c1e", (string)o["created"]["split"]);
            Assert.Equal(5, (int)o["counts"]["wires"]);
            Assert.True((bool)o["solved"]);
            Assert.True(json.Length < 600, $"result was {json.Length} chars");
        }
    }

    public class BatchRefRegistryTests
    {
        [Fact]
        public void Refs_ArePerDocumentAndClearWithIt()
        {
            var docA = Guid.NewGuid();
            var docB = Guid.NewGuid();
            var obj = Guid.NewGuid();

            BatchRefRegistry.Set(docA, "split", obj);
            Assert.True(BatchRefRegistry.TryGet(docA, "split", out var found));
            Assert.Equal(obj, found);
            Assert.False(BatchRefRegistry.TryGet(docB, "split", out _));
            Assert.False(BatchRefRegistry.TryGet(docA, "Split", out _));

            BatchRefRegistry.Clear(docA);
            Assert.False(BatchRefRegistry.TryGet(docA, "split", out _));
        }
    }

    /// <summary>
    /// The schema AI apps see must load, and its top-level fields must be exactly the arguments the
    /// tool reads (<see cref="BatchRawInput"/>) — otherwise an agent could send a field the tool
    /// silently ignores.
    /// </summary>
    public class GhBatchSchemaTests
    {
        private static JsonElement Schema() =>
            McpInputSchema.Load(typeof(GhBatchSchemaTests).Assembly, "GhBatchSchema.json");

        [Fact]
        public void Schema_LoadsAsAnObject()
        {
            Assert.Equal(JsonValueKind.Object, Schema().ValueKind);
            Assert.Equal("object", Schema().GetProperty("type").GetString());
        }

        [Fact]
        public void Schema_TopLevelFieldsMatchTheToolArguments()
        {
            var published = Schema().GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
            var arguments = typeof(BatchRawInput).GetProperties()
                .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1))
                .OrderBy(n => n).ToList();
            Assert.Equal(arguments, published);
        }

        [Fact]
        public void Schema_StaysLean()
        {
            var minified = JsonSerializer.Serialize(Schema());
            Assert.True(minified.Length < 4000, $"schema is {minified.Length} chars; every AI request carries it");
        }

        [Fact]
        public void Load_MissingResource_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => McpInputSchema.Load(typeof(GhBatchSchemaTests).Assembly, "nope.json"));
        }
    }
}
