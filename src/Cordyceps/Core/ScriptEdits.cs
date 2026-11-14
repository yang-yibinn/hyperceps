using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cordyceps.Core.Batch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core
{
    /// <summary>One replacement: text that occurs exactly once in the script, and what replaces it.</summary>
    public sealed class ScriptEdit
    {
        public string Old { get; set; }
        public string New { get; set; }
    }

    public sealed class ScriptEditResult
    {
        public bool Ok => Errors.Count == 0;
        /// <summary>The edited script, with the original's line endings; null when an edit failed.</summary>
        public string Source { get; set; }
        public List<string> Errors { get; } = new List<string>();
        /// <summary>1-based line in the edited script where each replacement starts, in edit order.</summary>
        public List<int> Lines { get; } = new List<int>();

        /// <summary>What the caller should know about edits that applied, e.g. a match ignoring trailing spaces.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Hyperceps: gh_script(action='edit') — change part of a script without sending all of it. In one
    /// session an agent rewrote a 40,000–70,000-character script in full about 25 times to change a
    /// few lines each time. Edits apply in order; each old text must occur exactly once in the script
    /// as it stands when that edit applies. Any failure fails the whole call, so nothing changes and
    /// every problem is reported at once; when the text wasn't found, with the closest line and the
    /// lines around it as they now read (and a note when that is a C# RunScript parameter list,
    /// which configure rewrites). Line endings are matched loosely (\r\n and \n) and the script keeps
    /// its own.
    /// </summary>
    public static class ScriptEdits
    {
        public const int MaxEdits = 50;
        private const int MaxLineCompare = 200;

        public static List<ScriptEdit> Parse(string json, List<string> errors)
        {
            var edits = new List<ScriptEdit>();
            var text = (json ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                errors.Add("edits: give a JSON list like [{\"old\": \"exact text in the script\", \"new\": \"its replacement\"}]");
                return edits;
            }

            JToken token;
            try
            {
                token = JToken.Parse(text);
            }
            catch (JsonException ex)
            {
                errors.Add($"edits: not valid JSON ({ex.Message})");
                return edits;
            }

            if (token is JObject single)
                token = new JArray(single);
            if (!(token is JArray list) || list.Count == 0)
            {
                errors.Add("edits: must be a list of {\"old\": ..., \"new\": ...}");
                return edits;
            }
            if (list.Count > MaxEdits)
            {
                errors.Add($"edits: at most {MaxEdits} in one call");
                return edits;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var path = $"edits[{i}]";
                if (!(list[i] is JObject o))
                {
                    errors.Add($"{path}: must be an object {{\"old\": ..., \"new\": ...}}");
                    continue;
                }
                foreach (var p in o.Properties().Where(p => p.Name != "old" && p.Name != "new"))
                    errors.Add($"{path}: unknown field '{p.Name}'; an edit has old and new");

                var oldToken = o["old"];
                var newToken = o["new"];
                if (oldToken == null || oldToken.Type != JTokenType.String || oldToken.Value<string>().Length == 0)
                {
                    errors.Add($"{path}.old: the exact text to replace, as it appears in the script");
                    continue;
                }
                if (newToken == null || newToken.Type != JTokenType.String)
                {
                    errors.Add($"{path}.new: the replacement text (\"\" deletes the old text)");
                    continue;
                }
                edits.Add(new ScriptEdit { Old = oldToken.Value<string>(), New = newToken.Value<string>() });
            }
            return edits;
        }

        public static ScriptEditResult Apply(string source, IReadOnlyList<ScriptEdit> edits)
        {
            var result = new ScriptEditResult();
            var crlf = (source ?? string.Empty).Contains("\r\n");
            var s = Lf(source ?? string.Empty);
            var starts = new List<int>();

            for (int i = 0; i < edits.Count; i++)
            {
                var old = Lf(edits[i].Old);
                var replacement = Lf(edits[i].New);
                var hits = Occurrences(s, old);
                int at, end;
                if (hits.Count == 1)
                {
                    at = hits[0];
                    end = at + old.Length;
                }
                else if (hits.Count > 1)
                {
                    result.Errors.Add($"edits[{i}]: the text to replace occurs {hits.Count} times (lines "
                          + string.Join(", ", hits.Take(5).Select(h => LineOf(s, h)))
                          + "); include more of the surrounding lines so it occurs once");
                    continue;
                }
                else
                {
                    // Not found exactly. Trailing spaces and tabs are invisible and mean nothing to the code
                    // (like line endings): ignoring them, does the text occur exactly once?
                    var loose = LooseMatch(s, old);
                    if (loose.Count != 1)
                    {
                        result.Errors.Add(NotFound(i, s, old, edits.Count > 1)
                            + (loose.Count > 1 ? $" (ignoring trailing spaces it occurs {loose.Count} times)" : ""));
                        continue;
                    }
                    at = loose.Start;
                    end = loose.End;
                    result.Notes.Add($"edits[{i}] matched ignoring trailing spaces on {LineList(loose.Lines)}");
                }

                int delta = replacement.Length - (end - at);
                for (int k = 0; k < starts.Count; k++)
                {
                    if (starts[k] >= end) starts[k] += delta;
                    else if (starts[k] > at) starts[k] = at;
                }
                s = s.Substring(0, at) + replacement + s.Substring(end);
                starts.Add(at);
            }

            if (!result.Ok)
                return result;
            foreach (var start in starts)
                result.Lines.Add(LineOf(s, start));
            result.Source = crlf ? s.Replace("\n", "\r\n") : s;
            return result;
        }

        private static string NotFound(int index, string s, string old, bool several)
        {
            var oldLines = old.Split('\n');
            int firstIndex = Array.FindIndex(oldLines, l => l.Trim().Length > 0);
            var first = firstIndex >= 0 ? oldLines[firstIndex].Trim() : string.Empty;
            var rest = firstIndex >= 0 ? oldLines.Skip(firstIndex + 1).ToList() : new List<string>();
            var lines = s.Split('\n');
            int best = -1, bestDistance = int.MaxValue;
            var target = Cap(first);

            // A first line like "{" or "else" can occur many times: of the lines that equal it, take the
            // one whose following lines are closest to the rest of the text, not the first in the script.
            var exact = Enumerable.Range(0, lines.Length).Where(j => first.Length > 0 && lines[j].Trim() == first).ToList();
            if (exact.Count > 0)
            {
                best = exact.OrderBy(j => RestDistance(lines, j, rest)).First();
                bestDistance = 0;
            }
            for (int j = 0; j < lines.Length && first.Length > 0 && exact.Count == 0; j++)
            {
                var line = lines[j].Trim();
                if (line.Length == 0) continue;
                var candidate = Cap(line);
                if (Math.Abs(candidate.Length - target.Length) > Math.Max(candidate.Length, target.Length) / 2) continue;
                var d = BatchNames.EditDistance(candidate, target);
                if (d < bestDistance)
                {
                    best = j;
                    bestDistance = d;
                }
            }

            string hint;
            bool close = best >= 0 && bestDistance <= Math.Max(3, target.Length / 4);
            if (best >= 0 && bestDistance == 0)
                hint = $" Its first line is line {best + 1}, so the difference is in spacing, indentation or a later line";
            else if (close)
                hint = $" Closest line, {best + 1}: {Clip(lines[best].Trim(), 120)}";
            else
                hint = " Nothing close: copy the text exactly as the script has it";
            if (close)
            {
                // The lines as they stand, so the edit can be corrected without reading the whole
                // script again (a benchmark agent re-read 47,000 characters after a miss like this).
                hint += "." + InRunScriptSignature(lines, best) + Around(lines, best, old.Split('\n').Length);
            }
            if (several)
                hint += (close ? "\n" : " ") + "(earlier edits in this call apply first)";
            return $"edits[{index}]: the text to replace isn't in the script.{hint}";
        }

        private const int ContextBefore = 2;
        private const int MaxContextLines = 8;
        private const int MaxContextLineLength = 160;

        /// <summary>The script's lines around <paramref name="best"/>, numbered, exactly as they are (long lines clipped).</summary>
        private static string Around(string[] lines, int best, int oldLines)
        {
            int from = Math.Max(0, best - ContextBefore);
            int to = Math.Min(lines.Length - 1, best + Math.Max(ContextBefore, oldLines));
            to = Math.Min(to, from + MaxContextLines - 1);
            while (to > from && lines[to].Trim().Length == 0) to--;
            var shown = new List<string>();
            for (int j = from; j <= to; j++)
            {
                var line = lines[j].TrimEnd();
                shown.Add(line.Length <= MaxContextLineLength
                    ? $"{j + 1}: {line}"
                    : $"{j + 1}: {line.Substring(0, MaxContextLineLength)}… [shortened; {line.Length} characters]");
            }
            return $"\nLines {from + 1}-{to + 1} now read:\n" + string.Join("\n", shown);
        }

        /// <summary>
        /// A note when <paramref name="best"/> is in a C# script's RunScript parameter list:
        /// configure rewrites that list when ports change, so an edit written before it misses there.
        /// </summary>
        private static string InRunScriptSignature(string[] lines, int best)
        {
            int start = Array.FindIndex(lines, l => l.Contains("void RunScript("));
            if (start < 0 || best < start) return string.Empty;
            int end = start;
            while (end < lines.Length - 1 && !lines[end].Contains(")")) end++;
            return best <= end
                ? " That is RunScript's parameter list, which configure keeps in step with the ports: add or remove a port with configure, and edit only the body."
                : string.Empty;
        }

        /// <summary>
        /// Adds what the edit did (<c>edited</c>, <c>editedLines</c>, <c>sourceLength</c>, <c>notes</c>) to the
        /// write's answer, only when the write succeeded: a failed write changed nothing, and "edited: 3"
        /// beside <c>success: false</c> would say otherwise (review finding 8).
        /// </summary>
        public static void Annotate(JObject written, int editCount, ScriptEditResult result)
        {
            if (written == null || result == null || written.Value<bool?>("success") == false)
                return;
            written["edited"] = editCount;
            written["editedLines"] = new JArray(result.Lines);
            written["sourceLength"] = result.Source?.Length ?? 0;
            if (result.Notes.Count > 0)
                written["notes"] = new JArray(result.Notes);   // e.g. a match ignoring trailing spaces
        }

        /// <summary>How far the lines after <paramref name="at"/> are from <paramref name="rest"/> (lower is closer).</summary>
        private static int RestDistance(string[] lines, int at, List<string> rest)
        {
            int total = 0;
            for (int k = 0; k < rest.Count && k < MaxContextLines; k++)
            {
                var want = Cap(rest[k].Trim());
                int j = at + 1 + k;
                total += j < lines.Length ? BatchNames.EditDistance(Cap(lines[j].Trim()), want) : want.Length;
            }
            return total;
        }

        private sealed class Loose
        {
            public int Count;
            public int Start;
            public int End;
            public List<int> Lines = new List<int>();
        }

        /// <summary>
        /// The text's occurrences when spaces and tabs at the end of each line are ignored, in the script and
        /// in the text (indentation stays exact). For exactly one, its range in the script and the lines
        /// whose trailing spaces made the difference.
        /// </summary>
        private static Loose LooseMatch(string s, string old)
        {
            var (script, map, _) = StripTrailing(s);
            var (text, _, oldDropped) = StripTrailing(old);
            var loose = new Loose();
            if (text.Length == 0) return loose;
            var hits = Occurrences(script, text);
            loose.Count = hits.Count;
            if (hits.Count != 1) return loose;

            loose.Start = map[hits[0]];
            loose.End = map[hits[0] + text.Length - 1] + 1;
            var lines = new SortedSet<int>();
            for (int q = loose.Start; q < loose.End; q++)   // the script's dropped characters inside the match
                if ((s[q] == ' ' || s[q] == '\t') && IsTrailing(s, q))
                    lines.Add(LineOf(s, q));
            int startLine = LineOf(s, loose.Start);
            foreach (var k in oldDropped)                    // trailing spaces the text had and the script hasn't
                lines.Add(startLine + k);
            loose.Lines.AddRange(lines);
            return loose;
        }

        /// <summary>
        /// The text without spaces and tabs at the end of each line; for each kept character, its position in
        /// the original; and the 0-based lines that lost some.
        /// </summary>
        private static (string Text, List<int> Map, List<int> DroppedLines) StripTrailing(string text)
        {
            var sb = new StringBuilder();
            var map = new List<int>();
            var dropped = new List<int>();
            int i = 0, line = 0;
            while (i <= text.Length)
            {
                int lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = text.Length;
                int keep = lineEnd;
                while (keep > i && (text[keep - 1] == ' ' || text[keep - 1] == '\t')) keep--;
                if (keep < lineEnd) dropped.Add(line);
                for (int k = i; k < keep; k++)
                {
                    sb.Append(text[k]);
                    map.Add(k);
                }
                if (lineEnd >= text.Length) break;
                sb.Append('\n');
                map.Add(lineEnd);
                i = lineEnd + 1;
                line++;
            }
            return (sb.ToString(), map, dropped);
        }

        /// <summary>Whether everything from <paramref name="q"/> to the end of its line is spaces and tabs.</summary>
        private static bool IsTrailing(string s, int q)
        {
            for (int k = q; k < s.Length && s[k] != '\n'; k++)
                if (s[k] != ' ' && s[k] != '\t') return false;
            return true;
        }

        /// <summary><c>line 41</c>, <c>lines 41-42</c>, <c>lines 3, 7-8</c>.</summary>
        private static string LineList(List<int> lines)
        {
            if (lines.Count == 0) return "no line";
            var parts = new List<string>();
            for (int k = 0; k < lines.Count; k++)
            {
                int a = lines[k];
                while (k + 1 < lines.Count && lines[k + 1] == lines[k] + 1) k++;
                parts.Add(a == lines[k] ? $"{a}" : $"{a}-{lines[k]}");
            }
            return (lines.Count == 1 ? "line " : "lines ") + string.Join(", ", parts);
        }

        /// <summary>Every start position of <paramref name="text"/>, overlapping ones included.</summary>
        private static List<int> Occurrences(string s, string text)
        {
            var hits = new List<int>();
            for (int i = s.IndexOf(text, StringComparison.Ordinal); i >= 0; i = s.IndexOf(text, i + 1, StringComparison.Ordinal))
                hits.Add(i);
            return hits;
        }

        private static int LineOf(string s, int position)
        {
            int line = 1;
            for (int i = 0; i < position && i < s.Length; i++)
                if (s[i] == '\n') line++;
            return line;
        }

        private static string Lf(string text) => (text ?? string.Empty).Replace("\r\n", "\n");

        private static string Cap(string text) => text.Length <= MaxLineCompare ? text : text.Substring(0, MaxLineCompare);

        private static string Clip(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "…";
    }
}
