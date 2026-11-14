using System;
using System.Collections.Generic;
using System.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>One installed component or parameter type, as the strict type rule sees it.</summary>
    public sealed class TypeCandidate
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string SubCategory { get; set; }
        public Guid Guid { get; set; }
        public bool Deprecated { get; set; }
        public string Plugin { get; set; }
        /// <summary>Ships with Grasshopper (a core library), as opposed to an installed plugin.</summary>
        public bool BuiltIn { get; set; }
        /// <summary>Known to come from an installed plugin (not built in, plugin named).</summary>
        public bool FromPlugin => !BuiltIn && !string.IsNullOrEmpty(Plugin);
        public bool IsParam => string.Equals(Category, "Params", StringComparison.OrdinalIgnoreCase);
        public string PathText => $"{Category}/{SubCategory}/{Name}";

        /// <summary><c>Curve/Primitive/Line (component, Grasshopper, 4c4e56eb)</c>, for candidate lists.</summary>
        public string Describe() =>
            $"{PathText} ({(IsParam ? "parameter" : "component")}{(Deprecated ? ", deprecated" : "")}"
            + $"{(string.IsNullOrEmpty(Plugin) ? "" : ", " + Plugin)}, {Guid.ToString("D").Substring(0, 8)})";
    }

    /// <summary>Every installed type, indexed by lower-cased name and by GUID.</summary>
    public sealed class TypeCatalog
    {
        private readonly Dictionary<string, List<TypeCandidate>> _byName = new Dictionary<string, List<TypeCandidate>>();
        private readonly Dictionary<Guid, TypeCandidate> _byGuid = new Dictionary<Guid, TypeCandidate>();
        private readonly HashSet<string> _categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TypeCatalog(IEnumerable<TypeCandidate> candidates)
        {
            foreach (var c in candidates)
            {
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                All.Add(c);
                var key = c.Name.ToLowerInvariant();
                if (!_byName.TryGetValue(key, out var list))
                    _byName[key] = list = new List<TypeCandidate>();
                list.Add(c);
                _byGuid[c.Guid] = c;
                if (!string.IsNullOrEmpty(c.Category)) _categories.Add(c.Category);
            }
        }

        public List<TypeCandidate> All { get; } = new List<TypeCandidate>();
        public int Count => All.Count;

        public IReadOnlyList<TypeCandidate> ByName(string name) =>
            _byName.TryGetValue((name ?? string.Empty).ToLowerInvariant(), out var list) ? list : (IReadOnlyList<TypeCandidate>)Array.Empty<TypeCandidate>();

        public TypeCandidate ByGuid(Guid id) => _byGuid.TryGetValue(id, out var c) ? c : null;

        public bool HasCategory(string category) => _categories.Contains(category ?? string.Empty);
    }

    public enum TypeResolutionKind
    {
        Resolved,
        Ambiguous,
        NotFound,
        CategoryMissing,
        GuidNotInstalled
    }

    public sealed class TypeResolution
    {
        public TypeResolutionKind Kind { get; set; }
        public TypeCandidate Match { get; set; }
        /// <summary>The competing matches when <see cref="Kind"/> is Ambiguous.</summary>
        public List<TypeCandidate> Candidates { get; set; } = new List<TypeCandidate>();
        /// <summary>Close names when nothing matched — shown, never used.</summary>
        public List<TypeCandidate> Suggestions { get; set; } = new List<TypeCandidate>();
        /// <summary>The category the caller named, when it is not installed.</summary>
        public string Category { get; set; }
        /// <summary>The match is deprecated (allowed only because it was the only match).</summary>
        public bool UsedDeprecated { get; set; }
        /// <summary>Matches that exist outside the Params tab when <c>paramsOnly</c> filtered them out.</summary>
        public List<TypeCandidate> NonParamMatches { get; set; } = new List<TypeCandidate>();
        /// <summary>
        /// Plugin types with the same name, set aside because the name also belongs to exactly one
        /// built-in type, which was used. Reported as a warning, so the choice is visible.
        /// </summary>
        public List<TypeCandidate> SetAside { get; set; } = new List<TypeCandidate>();
    }

    /// <summary>
    /// The spec's one rule for component names, never loose: exact name (ignoring case) among
    /// non-deprecated types; exactly one match is used; a deprecated type counts only when it is
    /// the only match; when the other matches all come from plugins, the one built-in type is
    /// used and the plugin types are reported as set aside; anything else lists the candidates. <c>Category/Name</c> and
    /// <c>Category/Subcategory/Name</c> narrow the search; a GUID names one type directly.
    /// </summary>
    public static class TypeNameRule
    {
        public static TypeResolution Resolve(string text, TypeCatalog catalog, bool paramsOnly)
        {
            var s = (text ?? string.Empty).Trim();
            if (s.Length == 0)
                return new TypeResolution { Kind = TypeResolutionKind.NotFound };

            if (Guid.TryParse(s, out var guid))
            {
                var byGuid = catalog.ByGuid(guid);
                return byGuid != null
                    ? new TypeResolution { Kind = TypeResolutionKind.Resolved, Match = byGuid, UsedDeprecated = byGuid.Deprecated }
                    : new TypeResolution { Kind = TypeResolutionKind.GuidNotInstalled };
            }

            var parts = s.Split('/').Select(p => p.Trim()).ToArray();
            string category = null, subCategory = null, name;
            if (parts.Length == 1)
                name = parts[0];
            else if (parts.Length == 2)
            {
                category = parts[0];
                name = parts[1];
            }
            else
            {
                category = parts[0];
                subCategory = parts[1];
                name = string.Join("/", parts.Skip(2));
            }

            var matches = catalog.ByName(name)
                .Where(c => category == null || string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(c => subCategory == null || string.Equals(c.SubCategory, subCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A name that itself contains '/' is still found by its whole text.
            if (parts.Length > 1)
                matches.AddRange(catalog.ByName(s).Where(c => !matches.Contains(c)));

            var result = new TypeResolution();
            if (paramsOnly)
            {
                result.NonParamMatches = matches.Where(c => !c.IsParam && !c.Deprecated).ToList();
                matches = matches.Where(c => c.IsParam).ToList();
            }

            var live = matches.Where(c => !c.Deprecated).ToList();
            if (live.Count == 1)
            {
                result.Kind = TypeResolutionKind.Resolved;
                result.Match = live[0];
                return result;
            }
            if (live.Count > 1)
            {
                // Hyperceps: a name shared by one built-in type and only plugin types (Offset Curve
                // in Grasshopper and in Pufferfish) means the built-in one; agents almost always
                // do, and the warning names the plugin types. Two built-ins, or a type of unknown
                // origin, stay ambiguous.
                var builtIn = live.Where(c => c.BuiltIn).ToList();
                if (builtIn.Count == 1 && live.All(c => c.BuiltIn || c.FromPlugin))
                {
                    result.Kind = TypeResolutionKind.Resolved;
                    result.Match = builtIn[0];
                    result.SetAside = live.Where(c => c != builtIn[0]).ToList();
                    return result;
                }

                result.Kind = TypeResolutionKind.Ambiguous;
                result.Candidates = live;
                return result;
            }

            var deprecated = matches.Where(c => c.Deprecated).ToList();
            if (deprecated.Count == 1)
            {
                result.Kind = TypeResolutionKind.Resolved;
                result.Match = deprecated[0];
                result.UsedDeprecated = true;
                return result;
            }
            if (deprecated.Count > 1)
            {
                result.Kind = TypeResolutionKind.Ambiguous;
                result.Candidates = deprecated;
                return result;
            }

            if (category != null && !catalog.HasCategory(category))
            {
                result.Kind = TypeResolutionKind.CategoryMissing;
                result.Category = category;
                return result;
            }

            result.Kind = TypeResolutionKind.NotFound;
            result.Suggestions = Suggest(name, catalog, paramsOnly);
            return result;
        }

        /// <summary>
        /// Up to five close names: ones containing the text (or contained in it), then near misses
        /// by edit distance; current types before deprecated ones.
        /// </summary>
        private static List<TypeCandidate> Suggest(string name, TypeCatalog catalog, bool paramsOnly)
        {
            if (name.Length < 2) return new List<TypeCandidate>();
            var pool = catalog.All.Where(c => !paramsOnly || c.IsParam);

            var containing = pool
                .Where(c => c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                         || (c.Name.Length >= 4 && name.IndexOf(c.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(c => c.Deprecated ? 1 : 0)
                .ThenBy(c => Math.Abs(c.Name.Length - name.Length))
                .Take(5)
                .ToList();
            if (containing.Count > 0) return containing;

            int limit = Math.Max(2, name.Length / 4);
            var lower = name.ToLowerInvariant();
            return pool
                .Where(c => Math.Abs(c.Name.Length - name.Length) <= limit)
                .Select(c => new { c, d = BatchNames.EditDistance(lower, c.Name.ToLowerInvariant()) })
                .Where(x => x.d <= limit)
                .OrderBy(x => x.d)
                .ThenBy(x => x.c.Deprecated ? 1 : 0)
                .Take(5)
                .Select(x => x.c)
                .ToList();
        }
    }
}
