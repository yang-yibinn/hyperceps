using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Sweep
{
    public enum SweepControlKind
    {
        Slider,
        ValueList,
        Toggle
    }

    /// <summary>The control being swept, as the planner needs it: its kind, range or items, and current value.</summary>
    public sealed class SweepControl
    {
        public SweepControlKind Kind { get; set; }
        public string Name { get; set; }
        public decimal Minimum { get; set; }
        public decimal Maximum { get; set; }
        public int Decimals { get; set; }
        public List<string> Items { get; set; } = new List<string>();
        public string Current { get; set; }

        public string Describe() => Kind switch
        {
            SweepControlKind.Slider => $"{Name} (slider {SweepPlan.Number(Minimum)}..{SweepPlan.Number(Maximum)}), was {Current}",
            SweepControlKind.ValueList => $"{Name} (value list: {string.Join(", ", Items.Take(8))}{(Items.Count > 8 ? ", …" : "")}), was {Current}",
            _ => $"{Name} (toggle), was {Current}"
        };
    }

    /// <summary>One value to set: its label in the results, and the slider value, list index or toggle state.</summary>
    public sealed class SweepStep
    {
        public string Label { get; set; }
        public decimal Number { get; set; }
        public int Index { get; set; }
        public bool Flag { get; set; }
    }

    /// <summary>What one watched output held after one step.</summary>
    public sealed class SweepWatchResult
    {
        public string Name { get; set; }
        public int Items { get; set; }

        /// <summary>
        /// How many of <see cref="Items"/> are empty (null). Shown as "(N empty)", because a count that
        /// includes empty slots reads as that many real items (a benchmark run reported 248 pieces of design
        /// geometry where 124 were empty).
        /// </summary>
        public int Empty { get; set; }

        public int Branches { get; set; }

        /// <summary>Up to three short values (text quoted), for text, number and true/false outputs; null otherwise.</summary>
        public string Preview { get; set; }

        public bool Solved { get; set; } = true;
    }

    /// <summary>Everything recorded for one step.</summary>
    public sealed class SweepStepResult
    {
        public string Label { get; set; }
        public List<SweepWatchResult> Watch { get; } = new List<SweepWatchResult>();
        public int Errors { get; set; }
        public int Warnings { get; set; }

        /// <summary>Objects that were not in error (or warning) before the sweep and are now, as "NAME level: message".</summary>
        public List<string> NewProblems { get; } = new List<string>();

        public long SolveMs { get; set; }
    }

    /// <summary>
    /// Hyperceps: the host-free part of gh_document(action='sweep'). Reads the requested values
    /// against the control (range, decimals, list items, true/false), reporting every problem at
    /// once, and formats one short line per step. The Grasshopper part sets each value, solves,
    /// reads the watched outputs and always puts the control back.
    /// </summary>
    public static class SweepPlan
    {
        public const int MaxValues = 20;
        public const int MaxWatch = 10;
        public const double TimeBudgetSeconds = 45;
        private const int MaxNewProblems = 3;

        /// <summary>A solve slower than this puts the control back after the answer, not before it.</summary>
        public const long RestoreAfterAnswerMs = 5000;

        /// <summary>
        /// Whether the control is put back after the answer: Grasshopper re-solves it then, instead of the
        /// call waiting for that solve. On a definition whose solve took 34 s, a one-value sweep spent
        /// another 34 s restoring the slider, and the call (67.5 s) outlasted the AI app's ~60 s limit.
        /// </summary>
        public static bool RestoreAfterAnswer(long lastSolveMs) => lastSolveMs > RestoreAfterAnswerMs;

        /// <summary>Seconds a restore is allowed for when checking the time limit before the next value.</summary>
        public static double RestoreAllowanceSeconds(long lastSolveMs) => RestoreAfterAnswer(lastSolveMs) ? 0 : lastSolveMs / 1000.0;

        /// <summary>The result's <c>restored</c> line: the control's value, and when it re-solves after the answer, how long that takes.</summary>
        public static string RestoredText(string name, string value, long lastSolveMs) =>
            RestoredText(name, value, RestoreAfterAnswer(lastSolveMs), lastSolveMs);

        /// <summary>Time already spent after which the control is put back after the answer, whatever the solves took.</summary>
        public const double RestoreAfterElapsedSeconds = 15;

        /// <summary>
        /// The full rule (review finding 3): put the control back after the answer when the slowest value
        /// or the original value's last solve took over 5 s, when more than 15 s has already been spent
        /// (the restore's own time isn't known), or when a step failed (the error is the answer). Ten
        /// values of 3 s with a 34 s original value would otherwise end 64 s into the call.
        /// </summary>
        public static bool RestoreAfterAnswer(long slowestStepMs, long originalSolveMs, double elapsedSeconds, bool failed) =>
            failed
            || RestoreAfterAnswer(Math.Max(slowestStepMs, originalSolveMs))
            || elapsedSeconds > RestoreAfterElapsedSeconds;

        /// <summary>
        /// Seconds to allow for the restore when checking the time limit before the next value: none when it
        /// will happen after the answer, else the larger of the slowest value and the original value.
        /// </summary>
        public static double RestoreAllowanceSeconds(long slowestStepMs, long originalSolveMs, double elapsedSeconds) =>
            RestoreAfterAnswer(slowestStepMs, originalSolveMs, elapsedSeconds, false) ? 0 : Math.Max(slowestStepMs, originalSolveMs) / 1000.0;

        /// <summary>
        /// The <c>restored</c> line for the full rule. <paramref name="estimateMs"/> is the expected re-solve
        /// time (the original value's last solve, else the slowest value); 0 leaves the time out.
        /// </summary>
        public static string RestoredText(string name, string value, bool afterAnswer, long estimateMs)
        {
            if (!afterAnswer) return $"{name} = {value}";
            return estimateMs >= 500
                ? $"{name} = {value} (Grasshopper re-solves it after this answer, about {(estimateMs / 1000.0).ToString("0", CultureInfo.InvariantCulture)} s)"
                : $"{name} = {value} (Grasshopper re-solves it after this answer)";
        }

        /// <summary>
        /// The steps for <paramref name="valuesJson"/>: a JSON list of values, or nothing for every
        /// value a small control has (both states of a toggle, every item of a value list, every
        /// step of a whole-number slider with at most <see cref="MaxValues"/> steps).
        /// </summary>
        public static List<SweepStep> Plan(SweepControl control, string valuesJson, List<string> errors, List<string> notes)
        {
            var steps = new List<SweepStep>();
            List<JToken> values;
            if (string.IsNullOrWhiteSpace(valuesJson))
            {
                values = DefaultValues(control, errors);
                if (values == null) return steps;
            }
            else
            {
                try
                {
                    var token = JToken.Parse(valuesJson.Trim());
                    values = token is JArray array ? array.ToList() : new List<JToken> { token };
                }
                catch (JsonException)
                {
                    // A bare word ("A", "true") is one value.
                    values = new List<JToken> { new JValue(valuesJson.Trim()) };
                }
            }

            if (values.Count == 0)
            {
                errors.Add("values: the list is empty");
                return steps;
            }
            if (values.Count > MaxValues)
            {
                errors.Add($"values: at most {MaxValues} values per call (got {values.Count}); split the sweep");
                return steps;
            }

            for (int i = 0; i < values.Count; i++)
            {
                var path = values.Count == 1 ? "values" : $"values[{i}]";
                var step = control.Kind switch
                {
                    SweepControlKind.Slider => SliderStep(control, values[i], path, errors, notes),
                    SweepControlKind.ValueList => ListStep(control, values[i], path, errors),
                    _ => ToggleStep(values[i], path, errors)
                };
                if (step != null) steps.Add(step);
            }
            return steps;
        }

        private static List<JToken> DefaultValues(SweepControl control, List<string> errors)
        {
            switch (control.Kind)
            {
                case SweepControlKind.Toggle:
                    return new List<JToken> { false, true };
                case SweepControlKind.ValueList:
                    if (control.Items.Count == 0)
                    {
                        errors.Add($"{control.Name} has no items to sweep");
                        return null;
                    }
                    if (control.Items.Count > MaxValues)
                    {
                        errors.Add($"values: {control.Name} has {control.Items.Count} items; name at most {MaxValues} of them");
                        return null;
                    }
                    return control.Items.Select(n => (JToken)n).ToList();
                default:
                    var count = control.Maximum - control.Minimum + 1;
                    if (control.Decimals == 0 && count >= 1 && count <= MaxValues)
                    {
                        var list = new List<JToken>();
                        for (var v = control.Minimum; v <= control.Maximum; v++)
                            list.Add(v);
                        return list;
                    }
                    errors.Add($"values: give the values to try, e.g. '[{Number(control.Minimum)}, {Number(control.Maximum)}]' "
                               + $"({control.Name} runs {Number(control.Minimum)}..{Number(control.Maximum)}"
                               + (control.Decimals > 0 ? $" with {control.Decimals} decimals)" : $", more than {MaxValues} steps)"));
                    return null;
            }
        }

        private static SweepStep SliderStep(SweepControl c, JToken token, string path, List<string> errors, List<string> notes)
        {
            if (!TryNumber(token, out var value))
            {
                errors.Add($"{path}: '{token}' isn't a number; {c.Name} is a number slider");
                return null;
            }
            if (value < c.Minimum || value > c.Maximum)
            {
                errors.Add($"{path}: {Number(value)} is outside {c.Name}'s range {Number(c.Minimum)}..{Number(c.Maximum)}");
                return null;
            }
            var rounded = Math.Round(value, c.Decimals, MidpointRounding.AwayFromZero);
            if (rounded != value)
                notes.Add($"{path}: {Number(value)} becomes {Number(rounded)} ({c.Name} has {c.Decimals} decimal{(c.Decimals == 1 ? "" : "s")})");
            return new SweepStep { Label = Number(rounded), Number = rounded };
        }

        private static SweepStep ListStep(SweepControl c, JToken token, string path, List<string> errors)
        {
            var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            int index = c.Items.IndexOf(text);
            if (index < 0)
            {
                var ci = c.Items.Select((n, i) => (n, i)).Where(x => string.Equals(x.n, text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (ci.Count == 1) index = ci[0].i;
            }
            if (index < 0 && token.Type == JTokenType.Integer)
            {
                var n = token.Value<int>();
                if (n >= 0 && n < c.Items.Count) index = n;
            }
            if (index < 0)
            {
                errors.Add($"{path}: {c.Name} has no item '{text}'. Items: {string.Join(", ", c.Items)}");
                return null;
            }
            return new SweepStep { Label = c.Items[index], Index = index };
        }

        private static SweepStep ToggleStep(JToken token, string path, List<string> errors)
        {
            bool? flag = null;
            if (token.Type == JTokenType.Boolean) flag = token.Value<bool>();
            else if (token.Type == JTokenType.Integer && (token.Value<int>() == 0 || token.Value<int>() == 1)) flag = token.Value<int>() == 1;
            else if (token.Type == JTokenType.String && ParseHelpers.TryParseBool(token.Value<string>(), out var b)) flag = b;

            if (flag == null)
            {
                errors.Add($"{path}: '{token}' isn't true or false; this control is a toggle");
                return null;
            }
            return new SweepStep { Label = flag.Value ? "true" : "false", Flag = flag.Value };
        }

        private static bool TryNumber(JToken token, out decimal value)
        {
            value = 0;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try
                {
                    value = token.Value<decimal>();
                    return true;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
            return token.Type == JTokenType.String
                   && decimal.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// One step in a line: "5 → DIAGONALS 166 · chk.out 1 ["BM=5 …"] · 0 errors · 1.5 s".
        /// </summary>
        public static string Line(SweepStepResult r)
        {
            var parts = r.Watch.Select(WatchText).ToList();

            // Warnings are always stated: "0 errors" alone left an agent unsure whether warnings were checked.
            var problems = CanvasCount(r.Errors, "error") + ", " + CanvasCount(r.Warnings, "warning");
            if (r.NewProblems.Count > 0)
                problems += $" (new: {string.Join("; ", r.NewProblems.Take(MaxNewProblems))}"
                            + (r.NewProblems.Count > MaxNewProblems ? $"; +{r.NewProblems.Count - MaxNewProblems} more" : "") + ")";
            parts.Add(problems);
            parts.Add($"{(r.SolveMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s");

            return $"{r.Label} → {string.Join(" · ", parts)}";
        }

        /// <summary>One watched output: <c>NAME items[ (N empty)][ in N branches][ [preview]][ (not solved)]</c>. gh_batch's watch uses it too.</summary>
        public static string WatchText(SweepWatchResult w)
        {
            var text = $"{w.Name} {w.Items}";
            if (w.Empty > 0) text += $" ({w.Empty} empty)";
            if (w.Branches > 1) text += $" in {w.Branches} branches";
            if (w.Preview != null) text += $" [{w.Preview}]";
            if (!w.Solved) text += " (not solved)";
            return text;
        }

        /// <summary>Short values for a preview: text quoted, each clipped, at most three.</summary>
        public static string Preview(IReadOnlyList<object> values)
        {
            if (values == null || values.Count == 0 || values.Count > 3) return null;
            var shown = new List<string>();
            foreach (var v in values)
            {
                switch (v)
                {
                    case string s:
                        shown.Add("\"" + Clip(s.Replace("\r\n", " / ").Replace("\n", " / "), 120) + "\"");
                        break;
                    case bool b:
                        shown.Add(b ? "true" : "false");
                        break;
                    case double d:
                        shown.Add(d.ToString("0.######", CultureInfo.InvariantCulture));
                        break;
                    case float f:
                        shown.Add(f.ToString("0.######", CultureInfo.InvariantCulture));
                        break;
                    case decimal m:
                        shown.Add(Number(m));
                        break;
                    case int _:
                    case long _:
                    case short _:
                        shown.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
                        break;
                    default:
                        return null;
                }
            }
            return Clip(string.Join(", ", shown), 240);
        }

        public static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

        private static string CanvasCount(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
