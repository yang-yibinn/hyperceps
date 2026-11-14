using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Sweep;
using Xunit;

namespace Cordyceps.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SweepPlan"/>, behind gh_document(action='sweep'): reading the
    /// requested values against a slider, value list or toggle, the defaults when no values are
    /// given, and the one-line result format.
    /// </summary>
    public class SweepPlanTests
    {
        private static SweepControl Slider(decimal min, decimal max, int decimals = 0) =>
            new SweepControl { Kind = SweepControlKind.Slider, Name = "DIVISION COUNT", Minimum = min, Maximum = max, Decimals = decimals, Current = "8" };

        private static SweepControl List(params string[] items) =>
            new SweepControl { Kind = SweepControlKind.ValueList, Name = "MODE", Items = items.ToList(), Current = items.FirstOrDefault() };

        private static SweepControl Toggle() =>
            new SweepControl { Kind = SweepControlKind.Toggle, Name = "SHOW", Current = "false" };

        private static (List<SweepStep> Steps, List<string> Errors, List<string> Notes) Plan(SweepControl c, string values)
        {
            var errors = new List<string>();
            var notes = new List<string>();
            return (SweepPlan.Plan(c, values, errors, notes), errors, notes);
        }

        [Fact]
        public void Slider_ValuesInRange_BecomeStepsInOrder()
        {
            var (steps, errors, notes) = Plan(Slider(5, 9), "[5, 7, 8, 9]");
            Assert.Empty(errors);
            Assert.Empty(notes);
            Assert.Equal(new[] { "5", "7", "8", "9" }, steps.Select(s => s.Label));
            Assert.Equal(new[] { 5m, 7m, 8m, 9m }, steps.Select(s => s.Number));
        }

        [Fact]
        public void Slider_OutOfRangeAndNonNumbers_AreAllReportedAtOnce()
        {
            var (steps, errors, _) = Plan(Slider(5, 9), "[4, 7, \"abc\", 10]");
            Assert.Single(steps);
            Assert.Equal(3, errors.Count);
            Assert.Contains("values[0]: 4 is outside DIVISION COUNT's range 5..9", errors);
            Assert.Contains("values[2]: 'abc' isn't a number; DIVISION COUNT is a number slider", errors);
            Assert.Contains("values[3]: 10 is outside DIVISION COUNT's range 5..9", errors);
        }

        [Fact]
        public void Slider_ValueWithMoreDecimals_IsRoundedWithANote()
        {
            var (steps, errors, notes) = Plan(Slider(0, 10, 1), "[2.25, \"3.5\"]");
            Assert.Empty(errors);
            Assert.Equal(new[] { "2.3", "3.5" }, steps.Select(s => s.Label));
            Assert.Equal("values[0]: 2.25 becomes 2.3 (DIVISION COUNT has 1 decimal)", notes.Single());
        }

        [Fact]
        public void Slider_NoValues_TriesEveryStepOfASmallWholeNumberSlider()
        {
            var (steps, errors, _) = Plan(Slider(5, 9), null);
            Assert.Empty(errors);
            Assert.Equal(new[] { "5", "6", "7", "8", "9" }, steps.Select(s => s.Label));
        }

        [Fact]
        public void Slider_NoValues_OnALargeOrDecimalSlider_AsksForValues()
        {
            var (steps, errors, _) = Plan(Slider(0, 100), null);
            Assert.Empty(steps);
            Assert.Contains("give the values to try", errors.Single());

            (steps, errors, _) = Plan(Slider(0, 1, 2), "");
            Assert.Empty(steps);
            Assert.Contains("with 2 decimals", errors.Single());
        }

        [Fact]
        public void TooManyValues_IsRefused()
        {
            var values = "[" + string.Join(",", Enumerable.Range(0, SweepPlan.MaxValues + 1)) + "]";
            var (steps, errors, _) = Plan(Slider(0, 100), values);
            Assert.Empty(steps);
            Assert.Contains($"at most {SweepPlan.MaxValues} values", errors.Single());
        }

        [Fact]
        public void ValueList_ByNameIgnoringCaseOrByIndex_AndAllItemsByDefault()
        {
            var (steps, errors, _) = Plan(List("Fast", "Slow", "Off"), "[\"slow\", 2, \"Fast\"]");
            Assert.Empty(errors);
            Assert.Equal(new[] { "Slow", "Off", "Fast" }, steps.Select(s => s.Label));
            Assert.Equal(new[] { 1, 2, 0 }, steps.Select(s => s.Index));

            (steps, errors, _) = Plan(List("Fast", "Slow"), null);
            Assert.Equal(new[] { "Fast", "Slow" }, steps.Select(s => s.Label));

            (steps, errors, _) = Plan(List("Fast", "Slow"), "[\"Medium\"]");
            Assert.Empty(steps);
            Assert.Equal("values: MODE has no item 'Medium'. Items: Fast, Slow", errors.Single());
        }

        [Fact]
        public void ValueList_AnItemNamedLikeANumber_WinsOverTheIndex()
        {
            var (steps, _, _) = Plan(List("2", "1", "0"), "[1]");
            Assert.Equal("1", steps.Single().Label);
            Assert.Equal(1, steps.Single().Index);
        }

        [Fact]
        public void Toggle_TrueFalseInAnyUsualSpelling_AndBothByDefault()
        {
            var (steps, errors, _) = Plan(Toggle(), "[true, \"no\", 1]");
            Assert.Empty(errors);
            Assert.Equal(new[] { true, false, true }, steps.Select(s => s.Flag));

            (steps, _, _) = Plan(Toggle(), null);
            Assert.Equal(new[] { "false", "true" }, steps.Select(s => s.Label));

            (steps, errors, _) = Plan(Toggle(), "[\"maybe\"]");
            Assert.Empty(steps);
            Assert.Contains("isn't true or false", errors.Single());
        }

        [Fact]
        public void ASingleBareValue_IsOneStep()
        {
            Assert.Equal("7", Plan(Slider(5, 9), "7").Steps.Single().Label);
            Assert.Equal("Slow", Plan(List("Fast", "Slow"), "Slow").Steps.Single().Label);
        }

        [Fact]
        public void Line_CountsPreviewProblemsAndTime()
        {
            var r = new SweepStepResult { Label = "5", SolveMs = 1475, Errors = 1, Warnings = 2 };
            r.Watch.Add(new SweepWatchResult { Name = "DIAGONALS", Items = 166 });
            r.Watch.Add(new SweepWatchResult { Name = "chk.out", Items = 1, Preview = "\"BM=5 lines 166\"" });
            r.Watch.Add(new SweepWatchResult { Name = "PTS", Items = 12, Branches = 3 });
            r.NewProblems.Add("'LOFT' (Loft) error: no curves");

            Assert.Equal(
                "5 → DIAGONALS 166 · chk.out 1 [\"BM=5 lines 166\"] · PTS 12 in 3 branches · 1 error, 2 warnings (new: 'LOFT' (Loft) error: no curves) · 1.5 s",
                SweepPlan.Line(r));
        }

        [Fact]
        public void WatchText_CountBranchesPreviewAndNotSolved()
        {
            Assert.Equal("DIAGONALS 166", SweepPlan.WatchText(new SweepWatchResult { Name = "DIAGONALS", Items = 166, Branches = 1 }));
            Assert.Equal("PTS 12 in 3 branches", SweepPlan.WatchText(new SweepWatchResult { Name = "PTS", Items = 12, Branches = 3 }));
            Assert.Equal("chk.report 1 [\"ok\"]", SweepPlan.WatchText(new SweepWatchResult { Name = "chk.report", Items = 1, Preview = "\"ok\"" }));
            Assert.Equal("NEW 0 (not solved)", SweepPlan.WatchText(new SweepWatchResult { Name = "NEW", Solved = false }));
        }

        [Fact]
        public void SlowSolve_PutsTheControlBackAfterTheAnswer()
        {
            // A 34 s solve: one value plus a 34 s restore outlasted the AI app's ~60 s limit.
            Assert.False(SweepPlan.RestoreAfterAnswer(1200));
            Assert.False(SweepPlan.RestoreAfterAnswer(SweepPlan.RestoreAfterAnswerMs));
            Assert.True(SweepPlan.RestoreAfterAnswer(34000));

            Assert.Equal(1.2, SweepPlan.RestoreAllowanceSeconds(1200), 3);
            Assert.Equal(0, SweepPlan.RestoreAllowanceSeconds(34000));

            Assert.Equal("MIN AREA = 0.05", SweepPlan.RestoredText("MIN AREA", "0.05", 1200));
            Assert.Equal("MIN AREA = 0.05 (Grasshopper re-solves it after this answer, about 34 s)",
                SweepPlan.RestoredText("MIN AREA", "0.05", 33800));
        }

        [Fact]
        public void RestoreRule_LooksAtTheSlowestValue_TheOriginal_TheTimeSpent_AndFailures()
        {
            // Review finding 3: the last value alone missed a slow original value and long sweeps.
            Assert.False(SweepPlan.RestoreAfterAnswer(1000, 0, 2, false));        // fast everything: restore in the call
            Assert.True(SweepPlan.RestoreAfterAnswer(1000, 34000, 2, false));     // fast values, slow original value
            Assert.True(SweepPlan.RestoreAfterAnswer(6000, 0, 2, false));         // a slow value, even if not the last
            Assert.True(SweepPlan.RestoreAfterAnswer(3000, 1000, 16, false));     // ten 3 s values: 15 s already spent
            Assert.True(SweepPlan.RestoreAfterAnswer(0, 0, 0, true));             // a failed step: the error is the answer

            Assert.Equal(4.0, SweepPlan.RestoreAllowanceSeconds(1000, 4000, 2), 3);
            Assert.Equal(0, SweepPlan.RestoreAllowanceSeconds(1000, 34000, 2));

            Assert.Equal("MIN AREA = 0.05", SweepPlan.RestoredText("MIN AREA", "0.05", false, 34000));
            Assert.Equal("MIN AREA = 0.05 (Grasshopper re-solves it after this answer, about 34 s)",
                SweepPlan.RestoredText("MIN AREA", "0.05", true, 34000));
            Assert.Equal("MIN AREA = 0.05 (Grasshopper re-solves it after this answer)",
                SweepPlan.RestoredText("MIN AREA", "0.05", true, 0));
        }

        [Fact]
        public void WatchText_SaysHowManyItemsAreEmpty()
        {
            // A benchmark run read "GEOMETRY 248 in 186 branches" as 248 pieces of design geometry; 124 were empty slots.
            Assert.Equal("GEOMETRY 248 (124 empty) in 186 branches",
                SweepPlan.WatchText(new SweepWatchResult { Name = "GEOMETRY", Items = 248, Empty = 124, Branches = 186 }));
            Assert.Equal("item.0 1 (1 empty)", SweepPlan.WatchText(new SweepWatchResult { Name = "item.0", Items = 1, Empty = 1 }));
            Assert.Equal("chk.report 3 (1 empty) [\"a\", \"b\"]",
                SweepPlan.WatchText(new SweepWatchResult { Name = "chk.report", Items = 3, Empty = 1, Preview = "\"a\", \"b\"" }));
        }

        [Fact]
        public void Line_WithoutWatchedOutputs_StillReportsProblemsAndTime()
        {
            var r = new SweepStepResult { Label = "true", SolveMs = 40 };
            Assert.Equal("true → 0 errors, 0 warnings · 0.0 s", SweepPlan.Line(r));
        }

        [Fact]
        public void Preview_ShowsShortSimpleValues_NothingForGeometryOrLongLists()
        {
            Assert.Equal("\"a / b\", 2.5, true", SweepPlan.Preview(new object[] { "a\nb", 2.5, true }));
            Assert.Equal("3", SweepPlan.Preview(new object[] { 3 }));
            Assert.Null(SweepPlan.Preview(new object[] { 1, 2, 3, 4 }));
            Assert.Null(SweepPlan.Preview(new object[] { new object() }));
            Assert.Null(SweepPlan.Preview(new object[0]));
        }

        [Fact]
        public void Describe_NamesTheControlItsRangeAndItsValueBefore()
        {
            Assert.Equal("DIVISION COUNT (slider 5..9), was 8", Slider(5, 9).Describe());
            Assert.Equal("MODE (value list: Fast, Slow), was Fast", List("Fast", "Slow").Describe());
            Assert.Equal("SHOW (toggle), was false", Toggle().Describe());
        }
    }
}
