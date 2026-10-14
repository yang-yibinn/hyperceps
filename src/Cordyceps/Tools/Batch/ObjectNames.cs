using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// Hyperceps: lets the single-step tools take the names gh_batch takes. Wherever a tool reads an
    /// object id, it accepts a full GUID (unchanged), an 8+ character GUID prefix, a ref from a gh_batch
    /// on the same document, or a unique nickname — by exactly gh_batch's rules (case-sensitive, never
    /// loose, an ambiguous name is an error naming the candidates). Agents mix gh_batch with gh_script and
    /// the inspection tools, and a short id or ref that works in one tool but not the next costs a
    /// failed call and a look-up each time.
    /// </summary>
    internal sealed class ObjectNames
    {
        private readonly BatchCanvas _canvas;

        public ObjectNames(GH_Document doc)
        {
            _canvas = new BatchCanvas(doc);
        }

        public static bool TryResolve(GH_Document doc, string text, out Guid id, out string error, bool groupsFirst = false) =>
            new ObjectNames(doc).TryResolve(text, out id, out error, groupsFirst);

        /// <param name="groupsFirst">
        /// For group actions: a group named in the text wins over an object with that nickname. A
        /// block's group and its output parameter are often named alike (PAIRS); every other action
        /// prefers the object.
        /// </param>
        public bool TryResolve(string text, out Guid id, out string error, bool groupsFirst = false)
        {
            error = null;
            string groupError = null;
            var s = (text ?? string.Empty).Trim();
            if (Guid.TryParse(s, out id))
                return true;

            if (groupsFirst && TryGroup(s, out id, out groupError))
                return true;

            var errors = new List<string>();
            var item = _canvas.Resolve(s, "id", errors);
            if (item != null)
            {
                id = item.Obj.InstanceGuid;
                return true;
            }

            if (!groupsFirst && TryGroup(s, out id, out groupError))
                return true;

            error = groupError ?? (errors.Count > 0 ? Clean(errors[0]) : $"no object named '{s}'");
            return false;
        }

        /// <summary>A group by its exact name (unique among groups) or by a unique GUID prefix.</summary>
        private bool TryGroup(string s, out Guid id, out string error)
        {
            id = Guid.Empty;
            error = null;

            var named = _canvas.Groups.Where(g => g.NickName == s).ToList();
            if (named.Count == 1)
            {
                id = named[0].InstanceGuid;
                return true;
            }
            if (named.Count > 1)
            {
                error = $"{named.Count} groups are named '{s}'; use a GUID prefix: {string.Join(", ", named.Take(5).Select(g => _canvas.Prefix(g.InstanceGuid)))}";
                return false;
            }

            if (BatchNames.LooksLikeGuidPrefix(s))
            {
                var p = s.ToLowerInvariant();
                var hits = _canvas.Groups.Where(g => g.InstanceGuid.ToString("D").StartsWith(p, StringComparison.Ordinal)).ToList();
                if (hits.Count == 1)
                {
                    id = hits[0].InstanceGuid;
                    return true;
                }
            }
            return false;
        }

        // gh_batch's messages start with the entry path ("id: "); a single-step tool has only one id.
        private static string Clean(string message) =>
            message.StartsWith("id: ", StringComparison.Ordinal) ? message.Substring(4) : message;
    }
}
