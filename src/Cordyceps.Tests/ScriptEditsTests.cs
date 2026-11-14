using System.Collections.Generic;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    public class ScriptEditsTests
    {
        private const string Script = "import math\ntol = 0.01\nfor c in crvs:\n    total += c.GetLength()\nreport = total\n";

        private static List<ScriptEdit> Parse(string json)
        {
            var errors = new List<string>();
            var edits = ScriptEdits.Parse(json, errors);
            Assert.Empty(errors);
            return edits;
        }

        [Fact]
        public void Apply_ReplacesTheOnePassage_AndSaysWhichLine()
        {
            var r = ScriptEdits.Apply(Script, Parse("[{\"old\": \"tol = 0.01\", \"new\": \"tol = 0.001\"}]"));
            Assert.True(r.Ok);
            Assert.Equal(Script.Replace("tol = 0.01", "tol = 0.001"), r.Source);
            Assert.Equal(new[] { 2 }, r.Lines);
        }

        [Fact]
        public void Apply_EditsRunInOrder_LaterOnesSeeEarlierOnes()
        {
            var r = ScriptEdits.Apply(Script, Parse(
                "[{\"old\": \"tol = 0.01\", \"new\": \"tol = 0.01\\nscale = 2\"}, {\"old\": \"scale = 2\", \"new\": \"scale = 3\"},"
                + " {\"old\": \"report = total\\n\", \"new\": \"\"}]"));
            Assert.True(r.Ok);
            Assert.Equal("import math\ntol = 0.01\nscale = 3\nfor c in crvs:\n    total += c.GetLength()\n", r.Source);
            Assert.Equal(new[] { 2, 3, 6 }, r.Lines);
        }

        [Fact]
        public void Apply_WindowsLineEndings_MatchUnixEdits_AndAreKept()
        {
            var crlf = Script.Replace("\n", "\r\n");
            var r = ScriptEdits.Apply(crlf, Parse("[{\"old\": \"for c in crvs:\\n    total\", \"new\": \"for c in curves:\\n    total\"}]"));
            Assert.True(r.Ok);
            Assert.Equal(crlf.Replace("for c in crvs:", "for c in curves:"), r.Source);
        }

        [Fact]
        public void Apply_TextOccurringTwice_FailsWithItsLines()
        {
            var r = ScriptEdits.Apply("a = 1\nb = 2\na = 1\n", Parse("[{\"old\": \"a = 1\", \"new\": \"a = 5\"}]"));
            Assert.False(r.Ok);
            Assert.Null(r.Source);
            Assert.Equal("edits[0]: the text to replace occurs 2 times (lines 1, 3); include more of the surrounding lines so it occurs once", Assert.Single(r.Errors));
        }

        [Fact]
        public void Apply_TextNotFound_NamesTheClosestLine_AndChangesNothing()
        {
            var r = ScriptEdits.Apply(Script, Parse("[{\"old\": \"tol = 0.1\", \"new\": \"tol = 0.2\"}, {\"old\": \"report = total\", \"new\": \"report = str(total)\"}]"));
            Assert.False(r.Ok);
            Assert.Null(r.Source);
            var error = Assert.Single(r.Errors);
            Assert.StartsWith("edits[0]: the text to replace isn't in the script. Closest line, 2: tol = 0.01", error);
            Assert.EndsWith("(earlier edits in this call apply first)", error);
        }

        // ---- review finding 4: where a refused edit points, trailing spaces, long lines ----

        [Fact]
        public void NotFound_WithAGenericFirstLine_PointsAtThePassageItMeant()
        {
            // "{" occurs at lines 2 and 7; the rest of the text is closest to what follows line 7.
            var script = "void A()\n{\n    int x = 1;\n}\n\nvoid B()\n{\n    int y = 2;\n}\n";
            var r = ScriptEdits.Apply(script, Parse("[{\"old\": \"{\\n    int y = 3;\\n}\", \"new\": \"{\\n    int y = 4;\\n}\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.Contains("Its first line is line 7", error);
            Assert.Contains("\n8:     int y = 2;", error);
        }

        [Fact]
        public void TrailingSpacesInTheScript_AreIgnored_AndTheAnswerSaysWhere()
        {
            var r = ScriptEdits.Apply("a = 1   \nb = 2\n", Parse("[{\"old\": \"a = 1\\nb = 2\", \"new\": \"a = 1\\nb = 3\"}]"));
            Assert.True(r.Ok);
            Assert.Equal("a = 1\nb = 3\n", r.Source);
            Assert.Equal("edits[0] matched ignoring trailing spaces on line 1", Assert.Single(r.Notes));
        }

        [Fact]
        public void TrailingSpacesInTheText_AreIgnored_Too()
        {
            var r = ScriptEdits.Apply("a = 1\nb = 2\nc = 3\n", Parse("[{\"old\": \"a = 1  \\nb = 2\\t\\nc\", \"new\": \"z\"}]"));
            Assert.True(r.Ok);
            Assert.Equal("z = 3\n", r.Source);
            Assert.Equal("edits[0] matched ignoring trailing spaces on lines 1-2", Assert.Single(r.Notes));
        }

        [Fact]
        public void AnExactMatch_IsUsedFirst()
        {
            // Ignoring trailing spaces, the text is at lines 1-2 and 3-4; exactly, only at 3-4.
            var r = ScriptEdits.Apply("a = 1  \nb\na = 1\nb\n", Parse("[{\"old\": \"a = 1\\nb\", \"new\": \"c\"}]"));
            Assert.True(r.Ok);
            Assert.Equal("a = 1  \nb\nc\n", r.Source);
            Assert.Empty(r.Notes);
        }

        [Fact]
        public void IgnoringTrailingSpaces_InSeveralPlaces_IsRefused()
        {
            var script = "a = 1 \nb\na = 1\t\nb\n";
            var r = ScriptEdits.Apply(script, Parse("[{\"old\": \"a = 1\\nb\", \"new\": \"c\"}]"));
            Assert.False(r.Ok);
            Assert.Null(r.Source);
            var error = Assert.Single(r.Errors);
            Assert.Contains("isn't in the script", error);
            Assert.EndsWith("(ignoring trailing spaces it occurs 2 times)", error);
        }

        [Fact]
        public void Indentation_StaysExact()
        {
            var r = ScriptEdits.Apply("if x:\n    a = 1\n", Parse("[{\"old\": \"if x:\\n  a = 1\", \"new\": \"if x:\\n  a = 2\"}]"));
            Assert.False(r.Ok);
            Assert.Contains("isn't in the script", Assert.Single(r.Errors));
        }

        [Fact]
        public void TrailingSpaces_WithWindowsLineEndings_KeepTheScriptsEndings()
        {
            var r = ScriptEdits.Apply("a = 1  \r\nb = 2\r\n", Parse("[{\"old\": \"a = 1\\nb = 2\", \"new\": \"a = 1\\nb = 3\"}]"));
            Assert.True(r.Ok);
            Assert.Equal("a = 1\r\nb = 3\r\n", r.Source);
            Assert.Single(r.Notes);
        }

        [Fact]
        public void Annotate_AddsTheEditFields_OnlyWhenTheWriteSucceeded()
        {
            var r = ScriptEdits.Apply("a = 1   \nb = 2\n", Parse("[{\"old\": \"a = 1\\nb = 2\", \"new\": \"a = 1\\nb = 3\"}]"));

            var ok = Newtonsoft.Json.Linq.JObject.Parse("{\"success\": true}");
            ScriptEdits.Annotate(ok, 1, r);
            Assert.Equal(1, (int)ok["edited"]);
            Assert.Equal(r.Source.Length, (int)ok["sourceLength"]);
            Assert.Single(ok["notes"]);

            // A failed write changed nothing: no "edited" beside success: false.
            var failed = Newtonsoft.Json.Linq.JObject.Parse("{\"success\": false, \"error\": \"Could not set the source\"}");
            ScriptEdits.Annotate(failed, 1, r);
            Assert.Null(failed["edited"]);
            Assert.Null(failed["editedLines"]);
            Assert.Null(failed["notes"]);
        }

        [Fact]
        public void NotFound_ALongLine_IsMarkedShortened()
        {
            var script = "x = 1\ny = '" + new string('z', 300) + "'\nz = 3\n";
            var r = ScriptEdits.Apply(script, Parse("[{\"old\": \"x = 2\", \"new\": \"x = 3\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.Contains("… [shortened; 306 characters]", error);
        }

        [Fact]
        public void Apply_TextNotFound_ShowsTheLinesAroundTheClosestMatch()
        {
            // So the edit can be corrected without reading the whole script again.
            var r = ScriptEdits.Apply(Script, Parse("[{\"old\": \"tol = 0.1\", \"new\": \"tol = 0.2\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.EndsWith("\nLines 1-4 now read:\n1: import math\n2: tol = 0.01\n3: for c in crvs:\n4:     total += c.GetLength()", error);
        }

        [Fact]
        public void Apply_NothingClose_ShowsNoLines()
        {
            var r = ScriptEdits.Apply(Script, Parse("[{\"old\": \"something else entirely here\", \"new\": \"x\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.Contains("Nothing close", error);
            Assert.DoesNotContain("now read", error);
        }

        private const string CSharp =
            "public class Script_Instance\n{\n    private void RunScript(\n\t\tList<Brep> cells,\n\t\tref object M,\n\t\tref object H)\n"
            + "    {\n        M = cells;\n    }\n}\n";

        [Fact]
        public void Apply_MissInRunScriptParameters_SaysConfigureKeepsThemInStep()
        {
            // configure had already added H, so an edit adding it after M no longer matches.
            var r = ScriptEdits.Apply(CSharp, Parse("[{\"old\": \"\\t\\tref object M)\", \"new\": \"\\t\\tref object M,\\n\\t\\tref object H)\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.StartsWith("edits[0]: the text to replace isn't in the script. Closest line, 5: ref object M,", error);
            Assert.Contains("That is RunScript's parameter list, which configure keeps in step with the ports", error);
            Assert.Contains("\n6: \t\tref object H)", error);
        }

        [Fact]
        public void Apply_MissInTheBody_HasNoRunScriptNote()
        {
            var r = ScriptEdits.Apply(CSharp, Parse("[{\"old\": \"M = cell;\", \"new\": \"M = null;\"}]"));
            var error = Assert.Single(r.Errors);
            Assert.Contains("Closest line, 8: M = cells;", error);
            Assert.DoesNotContain("RunScript's parameter list", error);
        }

        [Fact]
        public void Apply_IndentationWrong_SaysTheFirstLineMatches()
        {
            var r = ScriptEdits.Apply(Script, Parse("[{\"old\": \"for c in crvs:\\n  total += c.GetLength()\", \"new\": \"x\"}]"));
            Assert.StartsWith("edits[0]: the text to replace isn't in the script. Its first line is line 3, so the difference is in spacing, indentation or a later line",
                Assert.Single(r.Errors));
        }

        [Theory]
        [InlineData("", "edits: give a JSON list")]
        [InlineData("[", "edits: not valid JSON")]
        [InlineData("[]", "edits: must be a list")]
        [InlineData("[{\"new\": \"x\"}]", "edits[0].old: the exact text to replace")]
        [InlineData("[{\"old\": \"x\"}]", "edits[0].new: the replacement text")]
        [InlineData("[{\"old\": \"x\", \"new\": \"y\", \"replace\": true}]", "edits[0]: unknown field 'replace'")]
        public void Parse_BadInput_SaysWhatAnEditLooksLike(string json, string expected)
        {
            var errors = new List<string>();
            ScriptEdits.Parse(json, errors);
            Assert.Contains(errors, e => e.StartsWith(expected));
        }

        [Fact]
        public void Parse_SingleObject_IsOneEdit()
        {
            var edits = Parse("{\"old\": \"a\", \"new\": \"b\"}");
            Assert.Equal("a", Assert.Single(edits).Old);
        }
    }
}
