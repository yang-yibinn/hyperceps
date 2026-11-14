using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Outline
{
    /// <summary>
    /// Hyperceps: outline views of objects that aren't a Grasshopper group, opened with group= like a
    /// group (benchmark finding: on a canvas without groups an agent opened nine id= views one after
    /// another to read one network).
    /// <list type="bullet">
    /// <item><b>'network N'</b> (or 'network' when there is one), on a canvas without groups: every
    /// object of that network, as the overview numbers them (largest first).</item>
    /// <item><b>'ungrouped'</b>: every object outside any group.</item>
    /// </list>
    /// Each object is one line in the group view's format, in data-flow order. A group with the same
    /// name (ignoring case) wins. At most <see cref="MaxRegionLines"/> lines per call; the rest are
    /// named so they can be opened with id=.
    /// </summary>
    public static partial class CanvasOutline
    {
        public const int MaxRegionLines = 250;
        public const string UngroupedName = "ungrouped";
        private const int MaxRestNames = 20;

        private static readonly Regex NetworkName =
            new Regex(@"^network\s*(\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A set of objects shown like a group: a network, or the objects outside groups.</summary>
        private sealed class Region
        {
            public string Name;
            public string Where;
            public List<string> Members;
        }

        /// <summary>
        /// group=: groups and reserved names ('network N', 'ungrouped') in one call, with one line
        /// budget. Returns null when a name can't be resolved; <paramref name="errors"/> says why.
        /// </summary>
        public static JObject Open(OutlineModel m, IEnumerable<string> names, List<string> errors)
        {
            var groups = new List<OutlineGroup>();
            var regions = new List<Region>();
            var order = new List<object>();
            foreach (var raw in names)
            {
                var name = (raw ?? "").Trim();
                bool groupNamed = m.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (!groupNamed && TryRegion(m, name, errors, out var region))
                {
                    if (region != null && !regions.Any(r => r.Name == region.Name))
                    {
                        regions.Add(region);
                        order.Add(region);
                    }
                    continue;
                }

                int before = errors.Count;
                var g = FindGroup(m, name, errors);
                if (g == null && errors.Count > before && NetworkName.IsMatch(name))
                    errors[errors.Count - 1] += $" (networks are named only on a canvas without groups; group='{UngroupedName}' lists the objects outside groups)";
                if (g != null && !groups.Contains(g))
                {
                    groups.Add(g);
                    order.Add(g);
                }
            }
            if (errors.Count > 0) return null;

            int cap = regions.Count > 0 ? MaxRegionLines : MaxObjectLines;
            int budget = cap;
            var list = new JArray();
            foreach (var item in order)
            {
                list.Add(item is OutlineGroup g
                    ? GroupDetail(m, g, ref budget, cap)
                    : RegionDetail(m, (Region)item, ref budget, cap));
            }
            return new JObject { ["success"] = true, ["groups"] = list };
        }

        /// <summary>
        /// True when <paramref name="name"/> is a reserved name here; <paramref name="region"/> is null
        /// when it is one but can't be opened (the reason is in <paramref name="errors"/>).
        /// </summary>
        private static bool TryRegion(OutlineModel m, string name, List<string> errors, out Region region)
        {
            region = null;
            if (string.Equals(name, UngroupedName, StringComparison.OrdinalIgnoreCase))
            {
                var grouped = new HashSet<string>();
                foreach (var g in m.Groups) grouped.UnionWith(m.EffectiveMembers(g));
                region = new Region
                {
                    Name = UngroupedName,
                    Where = m.Groups.Count == 0 ? "this canvas has no groups" : "outside any group",
                    Members = m.Nodes.Where(n => !grouped.Contains(n.Id)).Select(n => n.Id).ToList()
                };
                return true;
            }

            var match = NetworkName.Match(name);
            if (!match.Success || m.Groups.Count > 0) return false;

            var networks = Networks(m).Networks;
            if (networks.Count == 0)
            {
                errors.Add($"'{name}': this canvas has no networks (no wires between its objects); the overview lists its objects");
                return true;
            }

            int index = 1;
            if (match.Groups[1].Success)
            {
                if (!int.TryParse(match.Groups[1].Value, out index)) index = 0;
            }
            else if (networks.Count > 1)
            {
                errors.Add($"'{name}': this canvas has {networks.Count} networks; name one, 'network 1' to 'network {networks.Count}' (largest first, as the overview lists them)");
                return true;
            }

            if (index < 1 || index > networks.Count)
            {
                errors.Add(networks.Count == 1
                    ? $"'{name}': this canvas has one network; open it with group='network'"
                    : $"'{name}': this canvas has {networks.Count} networks, 'network 1' to 'network {networks.Count}'");
                return true;
            }

            region = new Region
            {
                Name = networks.Count == 1 ? "network" : $"network {index}",
                Where = networks.Count == 1 ? "the only network" : $"network {index} of {networks.Count}",
                Members = networks[index - 1]
            };
            return true;
        }

        /// <summary>
        /// The canvas's separate networks (objects joined by wires), largest first, and the objects
        /// with no wires at all. The overview numbers networks in this order.
        /// </summary>
        private static (List<List<string>> Networks, List<string> Alone) Networks(OutlineModel m)
        {
            var all = m.Nodes.Select(n => n.Id).ToList();
            var alone = all.Where(id => m.Upstream(id).Count == 0 && m.Downstream(id).Count == 0).ToList();
            var aloneSet = new HashSet<string>(alone);
            var networks = Components(m, all.Where(id => !aloneSet.Contains(id)).ToList())
                .OrderByDescending(c => c.Count).ToList();
            return (networks, alone);
        }

        /// <summary>A region in the group view's format: one line per object, upstream first.</summary>
        private static JObject RegionDetail(OutlineModel m, Region r, ref int budget, int cap)
        {
            var members = new HashSet<string>(r.Members);
            var o = new JObject { ["group"] = r.Name };

            var summary = new List<string> { Count(members.Count, "object"), r.Where };
            AddProblemCounts(m, members, summary);
            o["summary"] = string.Join(" · ", summary);

            var inbound = Inbound(m, members);
            if (inbound.Count > 0) o["in"] = "← " + Join(inbound, MaxNames * 2);

            var order = TopoOrder(r.Members, id => m.Upstream(id), id => (m.NodeById[id].X, m.NodeById[id].Y));
            var lines = new JArray();
            int shown = 0;
            foreach (var id in order)
            {
                if (budget <= 0) break;
                lines.Add(NodeLine(m, m.NodeById[id], members));
                budget--;
                shown++;
            }
            o["objects"] = lines;
            if (shown < order.Count)
            {
                var rest = order.Skip(shown).Select(id => Label(m.NodeById[id]));
                o["more"] = order.Count - shown;
                o["note"] = $"{order.Count - shown} more not shown (at most {cap} lines per call); use id= on one of them: {Join(rest, MaxRestNames)}";
            }

            var outbound = Outbound(m, members);
            if (outbound.Count > 0) o["out"] = Join(outbound, MaxEdges * 2, "; ");
            return o;
        }
    }
}
