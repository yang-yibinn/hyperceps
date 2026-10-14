using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Batch;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Unit tests for <see cref="BatchSpecParser"/>: the shape of a gh_batch spec. The two
    /// examples from the spec must parse unchanged, and every shape mistake must be reported (all at
    /// once, naming its entry) rather than silently ignored.
    /// </summary>
    public class BatchSpecParserTests
    {
        private static BatchSpec Parse(BatchRawInput raw, out List<string> errors)
        {
            errors = new List<string>();
            return BatchSpecParser.Parse(raw, errors);
        }

        [Fact]
        public void Parse_SpecNewBlockExample_ParsesWithoutErrors()
        {
            var spec = Parse(new BatchRawInput
            {
                Components = @"[
                    {""ref"": ""tbl"",   ""panel"": ""0,0,2,2,4,4,6,6,8,8"", ""nickname"": ""BOTTOM CHORD INDEX""},
                    {""ref"": ""split"", ""type"": ""Text Split""},
                    {""ref"": ""item"",  ""type"": ""List Item"", ""addOutputs"": 1, ""inputModifiers"": {""i"": ""graft""}},
                    {""ref"": ""out"",   ""param"": ""Curve"", ""nickname"": ""PAIRS""}
                ]",
                Wires = @"[[""tbl.0"", ""split.0""], [""SEPARATOR.0"", ""split.1""],
                           [""BOTTOM CRVS.0"", ""item.L""], [""split.0"", ""item.i""], [""item.0"", ""out.0""]]",
                Groups = @"[{""name"": ""PAIRS"", ""color"": ""#DCEDC8"", ""members"": [""tbl"", ""split"", ""item"", ""out""]}]"
            }, out var errors);

            Assert.Empty(errors);
            Assert.Equal(4, spec.Components.Count);
            Assert.Equal(CreateKind.Panel, spec.Components[0].Kind);
            Assert.Equal("BOTTOM CHORD INDEX", spec.Components[0].Nickname);
            Assert.Equal(1, spec.Components[2].AddOutputs);
            Assert.True(spec.Components[2].InputModifiers.Single().Settings.Graft);
            Assert.Equal(CreateKind.Param, spec.Components[3].Kind);
            Assert.Equal("Curve", spec.Components[3].TypeText);
            Assert.Equal(5, spec.Wires.Count);
            Assert.Equal("BOTTOM CRVS", spec.Wires[2].From.Obj);
            Assert.Equal("0", spec.Wires[2].From.Port);
            Assert.Equal(4, spec.Groups[0].Members.Count);
            Assert.Equal(10, spec.OperationCount);
        }

        [Fact]
        public void Parse_SpecEditExample_ParsesInsertWithPorts()
        {
            var spec = Parse(new BatchRawInput
            {
                Components = @"[{""ref"": ""cull"", ""type"": ""Cull Pattern""}, {""ref"": ""pat"", ""panel"": ""true\nfalse"", ""nickname"": ""SKIP EVERY OTHER""}]",
                Insert = @"[{""ref"": ""cull"", ""on"": [""POSTS.0"", ""POST OUT.0""], ""in"": ""L"", ""out"": ""L""}]",
                Wires = @"[[""pat.0"", ""cull.P""]]"
            }, out var errors);

            Assert.Empty(errors);
            Assert.Equal("true\nfalse", spec.Components[1].PanelText);
            var ins = spec.Insert.Single();
            Assert.Equal("POSTS", ins.OnFrom.Obj);
            Assert.Equal("POST OUT", ins.OnTo.Obj);
            Assert.Equal("L", ins.In);
            Assert.Equal("L", ins.Out);
        }

        [Fact]
        public void Parse_InsertWithoutInOut_DefaultsToPortZero()
        {
            var spec = Parse(new BatchRawInput { Insert = @"[{""ref"": ""cull"", ""on"": [""a.0"", ""b.0""]}]" }, out var errors);
            Assert.Empty(errors);
            Assert.Equal("0", spec.Insert[0].In);
            Assert.Equal("0", spec.Insert[0].Out);
        }

        [Fact]
        public void Parse_UnknownField_SuggestsTheIntendedOne()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""a"", ""type"": ""Merge"", ""nickName"": ""X""}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("components[0]: unknown field 'nickName'") && e.Contains("Did you mean 'nickname'?"));
        }

        [Fact]
        public void Parse_ReportsEveryProblemAtOnce()
        {
            Parse(new BatchRawInput
            {
                Components = @"[{""type"": ""Merge""}, {""ref"": ""a.b"", ""type"": ""Merge""}, {""ref"": ""c""}]",
                Wires = @"[[""a.0""]]"
            }, out var errors);

            Assert.Contains(errors, e => e.StartsWith("components[0]: 'ref' is required"));
            Assert.Contains(errors, e => e.StartsWith("components[1].ref:") && e.Contains("no dots"));
            Assert.Contains(errors, e => e.StartsWith("components[2]: say what to create"));
            Assert.Contains(errors, e => e.StartsWith("wires[0]: must be a pair"));
        }

        [Fact]
        public void Parse_TwoShortcuts_IsAnError()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""a"", ""type"": ""Merge"", ""panel"": ""x""}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("use only one of type, panel"));
        }

        [Fact]
        public void Parse_XWithoutY_IsAnError()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""a"", ""type"": ""Merge"", ""x"": 10}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("give both x and y"));
        }

        [Fact]
        public void Parse_OutputsAndAddOutputs_IsAnError()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""a"", ""type"": ""List Item"", ""outputs"": 2, ""addOutputs"": 1}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("not both"));
        }

        [Theory]
        [InlineData(@"{""i"": [""graft"", ""flatten""]}", "graft and flatten exclude each other")]
        [InlineData(@"{""i"": ""graf""}", "unknown setting 'graf'")]
        [InlineData(@"{""i"": [""none"", ""simplify""]}", "'none' can't be combined")]
        [InlineData(@"{""i"": 3}", "a setting or a list of settings")]
        public void Parse_BadModifiers_AreErrors(string modifiers, string expected)
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""a"", ""type"": ""List Item"", ""inputModifiers"": " + modifiers + "}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains(expected));
        }

        [Fact]
        public void Parse_ModifierListAndNone_ParseToSettings()
        {
            var spec = Parse(new BatchRawInput
            {
                Set = @"[{""target"": ""x"", ""inputModifiers"": {""T"": [""flatten"", ""simplify""], ""L"": ""none"", ""R"": []}}]"
            }, out var errors);

            Assert.Empty(errors);
            var mods = spec.Set[0].InputModifiers;
            Assert.True(mods[0].Settings.Flatten && mods[0].Settings.Simplify && !mods[0].Settings.Graft);
            Assert.True(mods[1].Settings.IsEmpty);
            Assert.True(mods[2].Settings.IsEmpty);
        }

        [Fact]
        public void Parse_WildcardOnlyInDisconnect()
        {
            var spec = Parse(new BatchRawInput { Disconnect = @"[[""*"", ""comp.x""], [""slider"", ""*""]]" }, out var errors);
            Assert.Empty(errors);
            Assert.True(spec.Disconnect[0].From.IsWildcard);
            Assert.True(spec.Disconnect[1].To.IsWildcard);

            Parse(new BatchRawInput { Wires = @"[[""*"", ""comp.x""]]" }, out errors);
            Assert.Contains(errors, e => e.Contains("only allowed in disconnect"));

            Parse(new BatchRawInput { Disconnect = @"[[""*"", ""*""]]" }, out errors);
            Assert.Contains(errors, e => e.Contains("only one side"));
        }

        [Fact]
        public void Parse_ExplicitPortForm_NeverSplits()
        {
            var spec = Parse(new BatchRawInput { Wires = @"[[{""obj"": ""v1.2"", ""port"": ""L""}, {""obj"": ""out.x""}]]" }, out var errors);
            Assert.Empty(errors);
            Assert.Equal("v1.2", spec.Wires[0].From.Obj);
            Assert.Equal("L", spec.Wires[0].From.Port);
            Assert.Equal("out.x", spec.Wires[0].To.Obj);
            Assert.Null(spec.Wires[0].To.Port);
        }

        [Fact]
        public void Parse_NumericExplicitPort_IsText()
        {
            var spec = Parse(new BatchRawInput { Wires = @"[[{""obj"": ""a"", ""port"": 2}, ""b.0""]]" }, out var errors);
            Assert.Empty(errors);
            Assert.Equal("2", spec.Wires[0].From.Port);
        }

        [Fact]
        public void Parse_DoubleEncodedJson_IsUnwrapped()
        {
            var spec = Parse(new BatchRawInput { Delete = @"""[\""a\"", \""b\""]""" }, out var errors);
            Assert.Empty(errors);
            Assert.Equal(new[] { "a", "b" }, spec.Delete.Select(d => d.Name));
        }

        [Fact]
        public void Parse_InvalidJson_IsAnError()
        {
            Parse(new BatchRawInput { Wires = "[[\"a.0\", " }, out var errors);
            Assert.Contains(errors, e => e.StartsWith("wires: not valid JSON"));
        }

        [Fact]
        public void Parse_NotAList_IsAnError()
        {
            Parse(new BatchRawInput { Delete = @"{""a"": 1}" }, out var errors);
            Assert.Contains(errors, e => e.StartsWith("delete: must be a list"));
        }

        [Fact]
        public void Parse_Options_DefaultsAndCeilings()
        {
            var spec = Parse(new BatchRawInput(), out var errors);
            Assert.Empty(errors);
            Assert.True(spec.Options.Atomic);
            Assert.False(spec.Options.DryRun);
            Assert.True(spec.Options.Solve);
            Assert.Equal(1000, spec.Options.MaxOperations);
            Assert.Equal(60, spec.Options.TimeoutSeconds);

            Parse(new BatchRawInput { Options = @"{""maxOperations"": 6000, ""timeoutSeconds"": 500}" }, out errors);
            Assert.Contains(errors, e => e.StartsWith("options.maxOperations"));
            Assert.Contains(errors, e => e.StartsWith("options.timeoutSeconds"));

            spec = Parse(new BatchRawInput { Options = @"{""dryRun"": true, ""atomic"": false, ""solve"": ""false""}" }, out errors);
            Assert.Empty(errors);
            Assert.True(spec.Options.DryRun);
            Assert.False(spec.Options.Atomic);
            Assert.False(spec.Options.Solve);
        }

        [Fact]
        public void Parse_SliderRange_MustGoUp()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""s"", ""slider"": {""min"": 9, ""max"": 5}}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("min (9) must be less than max (5)"));
        }

        [Fact]
        public void Parse_ValueListSelection_IsChecked()
        {
            Parse(new BatchRawInput { Components = @"[{""ref"": ""v"", ""valueList"": {""items"": [{""name"": ""A"", ""value"": ""1""}], ""selected"": 3}}]" }, out var errors);
            Assert.Contains(errors, e => e.Contains("index 3 is out of range"));

            var spec = Parse(new BatchRawInput { Components = @"[{""ref"": ""v"", ""valueList"": {""items"": [{""name"": ""A"", ""value"": 1}, {""name"": ""B"", ""value"": ""2""}], ""selected"": ""B""}}]" }, out errors);
            Assert.Empty(errors);
            Assert.Equal("1", spec.Components[0].ValueList.Items[0].Value);
            Assert.Equal("B", spec.Components[0].ValueList.SelectedName);
        }

        [Fact]
        public void Parse_SetWithNothingToChange_IsAnError()
        {
            Parse(new BatchRawInput { Set = @"[{""target"": ""x""}]" }, out var errors);
            Assert.Contains(errors, e => e.StartsWith("set[0]: nothing to change"));
        }

        [Fact]
        public void Parse_SetValueNull_MeansClear()
        {
            var spec = Parse(new BatchRawInput { Set = @"[{""target"": ""item.i"", ""value"": null}]" }, out var errors);
            Assert.Empty(errors);
            Assert.True(spec.Set[0].HasValue);
            Assert.Equal(BatchValueKind.Null, spec.Set[0].Value.Kind);
        }

        [Fact]
        public void Parse_GroupNeedsNameOrExtend()
        {
            Parse(new BatchRawInput { Groups = @"[{""members"": [""a""]}, {""name"": ""A"", ""extend"": ""B"", ""members"": []}, {""extend"": ""B""}]" }, out var errors);
            Assert.Contains(errors, e => e.StartsWith("groups[0]: give 'name'"));
            Assert.Contains(errors, e => e.StartsWith("groups[1]: use 'name'"));
            Assert.Contains(errors, e => e.StartsWith("groups[2].members"));
        }

        [Fact]
        public void Parse_EmptyBatch_IsEmpty()
        {
            var spec = Parse(new BatchRawInput { Components = "[]", Wires = "  " }, out var errors);
            Assert.Empty(errors);
            Assert.True(spec.IsEmpty);
        }
    }

    public class PortAddressTests
    {
        [Theory]
        [InlineData("split.0", "split", "0")]
        [InlineData("cull.L", "cull", "L")]
        [InlineData("BOTTOM CRVS.0", "BOTTOM CRVS", "0")]
        [InlineData("v1.2.L", "v1.2", "L")]
        [InlineData("SEPARATOR", "SEPARATOR", null)]
        public void Parse_SplitsOnTheLastDot(string text, string obj, string port)
        {
            var a = PortAddress.Parse(text, out var error);
            Assert.Null(error);
            Assert.Equal(obj, a.Obj);
            Assert.Equal(port, a.Port);
        }

        [Theory]
        [InlineData(".0")]
        [InlineData("split.")]
        [InlineData("  ")]
        public void Parse_EmptyPart_IsAnError(string text)
        {
            Assert.Null(PortAddress.Parse(text, out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void Parse_Star_IsWildcard()
        {
            Assert.True(PortAddress.Parse("*", out _).IsWildcard);
        }
    }
}
