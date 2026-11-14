using System.Collections.Generic;
using System.Linq;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps: Grasshopper's runtime message levels by number, and what each means for an agent.
    /// Grasshopper numbers them Blank 0, Warning 10, Error 20 and Remark 255, so a remark ranks above
    /// an error by number: "level >= Warning" counted a component with only remarks as a warning and
    /// showed the remark as one (a benchmark definition read "1 warning" that was a remark), while a
    /// remark next to a real warning was hidden everywhere. Host-free, so it is unit-tested.
    /// </summary>
    public static class MessageLevels
    {
        public const int Blank = 0;
        public const int Warning = 10;
        public const int Error = 20;
        public const int Remark = 255;

        private const int MaxRemarkLength = 100;

        /// <summary>"error", "warning", "remark", or null for blank or an unknown level.</summary>
        public static string Name(int level) => level switch
        {
            Error => "error",
            Warning => "warning",
            Remark => "remark",
            _ => null
        };

        /// <summary>An error or a warning. A remark is information, not a problem, whatever its number.</summary>
        public static bool IsProblem(int level) => level == Error || level == Warning;

        /// <summary>How serious a level is, for comparing two: error 2, warning 1, anything else (a remark too) 0.</summary>
        public static int Severity(int level) => level == Error ? 2 : level == Warning ? 1 : 0;

        /// <summary>
        /// A component's remarks as one short note, <c>remark: first one (+N more)</c>, or null when it has
        /// none.
        /// </summary>
        public static string RemarkNote(IEnumerable<string> remarks)
        {
            var list = (remarks ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (list.Count == 0) return null;
            var first = string.Join(" ", list[0].Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (first.Length > MaxRemarkLength) first = first.Substring(0, MaxRemarkLength - 1) + "…";
            return $"remark: {first}" + (list.Count > 1 ? $" (+{list.Count - 1} more)" : "");
        }
    }
}
