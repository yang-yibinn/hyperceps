using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Batch;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Outline
{
    /// <summary>
    /// Hyperceps: gh_canvas(action='outline') — the canvas in a few short lines, so an agent can find
    /// its way around in one call instead of list + group_list + wire list + info calls.
    ///
    /// Three views:
    /// <list type="bullet">
    /// <item><b>Overview</b> (no group, no id): one line per top-level group with what crosses its
    /// border ("in" and "out", with item counts), the sliders, toggles and value lists, and the
    /// errors. A canvas without groups is described by its separate networks instead: their inputs,
    /// named objects and ends.</item>
    /// <item><b>Group detail</b> (group=): one line per object, upstream first, each naming what feeds
    /// it. Child groups and Lichen Thalli are one line each, opened with their own name.</item>
    /// <item><b>Around</b> (id=): what feeds one object and what it feeds, a few wires each way. For
    /// canvases without groups.</item>
    /// </list>
    /// Every name in the result works as an id in every tool. Host-free so it can be unit-tested; the
    /// adapter (GhCanvasTool.Outline) reads the document into an <see cref="OutlineModel"/>.
    /// </summary>
    public static class CanvasOutline
    {
        public const int MaxGroupLines = 60;
        public const int MaxObjectLines = 150;
        public const int MaxAroundObjects = 60;
        public const int DefaultDepth = 2;
        public const int MaxDepth = 10;
        public const int MaxControls = 20;
        public const int MaxProblems = 10;
        public const int MaxNetworks = 10;
        private const int MaxNames = 8;
        private const int MaxEdges = 6;
        private const int MaxDestinations = 4;

        // ---- overview -------------------------------------------------------------------------------

        public static JObject Overview(OutlineModel m)
        {
            var o = new JObject { ["success"] = true };
            if (!string.IsNullOrEmpty(m.Document)) o["document"] = m.Document;
            o["summary"] = Summary(m);

            var controls = m.Nodes.Where(n => n.IsControl).OrderBy(n => n.X).ThenBy(n => n.Y).ToList();
            if (controls.Count > 0)
                o["controls"] = Capped(controls.Select(n => $"{Label(n)} = {n.Value}{GroupTag(m, n.Id)}"), MaxControls);

            var top = GroupOrder(m);
            var grouped = new HashSet<string>();
            foreach (var g in m.Groups) grouped.UnionWith(m.EffectiveMembers(g));

            if (top.Count > 0)
            {
                o["groups"] = Capped(top.Select(g => GroupLine(m, g)), MaxGroupLines);
                var loose = m.Nodes.Where(n => !grouped.Contains(n.Id)).Select(n => n.Id).ToList();
                if (loose.Count > 0)
                    o["ungrouped"] = RegionText(m, loose);
            }
            else
            {
                AddNetworks(m, o);
            }

            var problems = Problems(m);
            if (problems.Count > 0) o["problems"] = Capped(problems, MaxProblems);

            o["next"] = top.Count > 0
                ? "group='NAME' shows how a group is wired inside (a JSON list for several); id='NAME' shows what feeds one object and what it feeds"
                : "id='NAME' (an input, an end or a named object) shows what feeds it and what it feeds; depth=N wires each way (default 2)";
            return o;
        }

        private static string Summary(OutlineModel m)
        {
            int errors = m.Nodes.Count(n => n.Level == "error");
            int warnings = m.Nodes.Count(n => n.Level == "warning");
            int thalli = m.Groups.Count(g => g.IsThallus);
            var groups = Count(m.Groups.Count, "group") + (thalli > 0 ? $" ({thalli} {(thalli == 1 ? "Thallus" : "Thalli")})" : "");
            return $"{Count(m.Nodes.Count, "object")} · {groups} · {Count(errors, "error")} · {Count(warnings, "warning")}";
        }

        private static string GroupLine(OutlineModel m, OutlineGroup g)
        {
            var members = m.EffectiveMembers(g);
            var parts = new List<string> { g.Title };
            if (g.Colour != null) parts.Add(g.Colour);
            parts.Add(Count(members.Count, "object"));
            AddProblemCounts(m, members, parts);

            if (g.IsThallus && !string.IsNullOrWhiteSpace(g.Description))
                parts.Add($"author's description: \"{Clip(OneLine(g.Description), 120)}\"");
            if (g.ToLichen) parts.Add("T → Lichen");

            var children = g.Children.Where(m.GroupById.ContainsKey).Select(c => m.GroupById[c]).ToList();
            if (children.Count > 0)
                parts.Add("contains: " + Join(children.Select(c => $"{c.Title} ({Count(m.EffectiveMembers(c).Count, "object")})"), MaxNames));

            var inbound = Inbound(m, members, brief: true);
            if (inbound.Count > 0) parts.Add("in ← " + Join(inbound, MaxNames));
            var outbound = Outbound(m, members);
            if (outbound.Count > 0) parts.Add("out: " + Join(outbound, MaxEdges, "; "));
            return string.Join(" · ", parts);
        }

        /// <summary>Top-level groups in data-flow order: a group comes after the groups that feed it.</summary>
        private static List<OutlineGroup> GroupOrder(OutlineModel m)
        {
            var top = m.TopLevelGroups.ToList();
            var ids = top.Select(g => g.Id).ToList();
            var preds = new Dictionary<string, HashSet<string>>();
            foreach (var g in top)
            {
                var p = new HashSet<string>();
                foreach (var n in m.EffectiveMembers(g))
                {
                    foreach (var u in m.Upstream(n))
                    {
                        var ug = m.OutermostGroup(u);
                        if (ug != null && ug.Id != g.Id) p.Add(ug.Id);
                    }
                }
                preds[g.Id] = p;
            }
            return TopoOrder(ids, id => preds[id], id => (m.GroupById[id].X, m.GroupById[id].Y))
                .Select(id => m.GroupById[id]).ToList();
        }

        private static void AddNetworks(OutlineModel m, JObject o)
        {
            var all = m.Nodes.Select(n => n.Id).ToList();
            var alone = all.Where(id => m.Upstream(id).Count == 0 && m.Downstream(id).Count == 0).ToList();
            var aloneSet = new HashSet<string>(alone);
            var networks = Components(m, all.Where(id => !aloneSet.Contains(id)).ToList())
                .OrderByDescending(c => c.Count).ToList();

            if (networks.Count > 0)
            {
                var lines = networks.Select((c, i) => (networks.Count == 1 ? "network · " : $"network {i + 1} · ") + RegionText(m, c));
                o["networks"] = Capped(lines, MaxNetworks);
            }
            if (alone.Count > 0)
                o["unconnected"] = Count(alone.Count, "object") + ": " + Join(alone.Select(id => Label(m.NodeById[id])), MaxNames);
        }

        /// <summary>
        /// A set of objects in a few words: where data comes in (sliders, panels, stored values), the
        /// objects the user named, where it ends (results nothing reads), the groups it feeds.
        /// </summary>
        private static string RegionText(OutlineModel m, IReadOnlyCollection<string> ids)
        {
            var set = new HashSet<string>(ids);
            var nodes = ids.Select(id => m.NodeById[id]).OrderBy(n => n.X).ThenBy(n => n.Y).ToList();
            var parts = new List<string> { Count(nodes.Count, "object") };

            var inputs = nodes.Where(IsInput).ToList();
            var ends = nodes.Where(n => !IsInput(n) && n.Outputs.Count > 0 && m.Downstream(n.Id).Count == 0).ToList();
            var shown = new HashSet<string>(inputs.Concat(ends).Select(n => n.Id));
            var named = nodes.Where(n => n.Nickname != null && !shown.Contains(n.Id)).ToList();

            if (inputs.Count > 0) parts.Add("inputs: " + Join(inputs.Select(n => $"{Label(n)} = {Clip(n.Value, 30)}"), MaxNames));
            if (named.Count > 0) parts.Add("named: " + Join(named.Select(Label), MaxNames));
            if (ends.Count > 0) parts.Add("ends: " + Join(ends.Select(n => $"{Label(n)} ({n.Outputs[0].Items})"), MaxNames));

            var feeds = ids.SelectMany(id => m.Downstream(id)).Where(r => !set.Contains(r))
                .Select(r => m.OutermostGroup(r)?.Title).Where(t => t != null).Distinct().ToList();
            if (feeds.Count > 0) parts.Add("feeds: " + Join(feeds, MaxNames));

            AddProblemCounts(m, set, parts);
            return string.Join(" · ", parts);
        }

        private static bool IsInput(OutlineNode n) =>
            n.Inputs.All(i => i.Sources.Count == 0)
            && (n.IsControl || n.Kind == OutlineKind.Panel || (n.Kind == OutlineKind.Parameter && n.Value != null));

        private static List<string> Problems(OutlineModel m) =>
            m.Nodes.Where(n => n.Level != null)
                .OrderBy(n => n.Level == "error" ? 0 : 1).ThenBy(n => n.X).ThenBy(n => n.Y)
                .Select(n => $"{Label(n)}{GroupTag(m, n.Id)} {n.Level}: {Clip(OneLine(n.Message), 100)}")
                .ToList();

        // ---- group detail ---------------------------------------------------------------------------

        public static JObject Groups(OutlineModel m, IReadOnlyList<OutlineGroup> groups)
        {
            int budget = MaxObjectLines;
            var list = new JArray();
            foreach (var g in groups)
                list.Add(GroupDetail(m, g, ref budget));
            return new JObject { ["success"] = true, ["groups"] = list };
        }

        private static JObject GroupDetail(OutlineModel m, OutlineGroup g, ref int budget)
        {
            var members = m.EffectiveMembers(g);
            var o = new JObject { ["group"] = g.Title, ["id"] = g.ShortId };

            var summary = new List<string> { Count(members.Count, "object") };
            if (m.ParentById.TryGetValue(g.Id, out var parent)) summary.Add("inside " + m.GroupById[parent].Title);
            if (g.Colour != null) summary.Add(g.Colour);
            if (g.ToLichen) summary.Add("T → Lichen");
            AddProblemCounts(m, members, summary);
            o["summary"] = string.Join(" · ", summary);

            if (g.IsThallus)
            {
                if (!string.IsNullOrWhiteSpace(g.Description))
                    o["authorDescription"] = Clip(g.Description.Trim(), 800);
                if (g.Properties.Count > 0)
                    o["authorProperties"] = Clip(string.Join("; ", g.Properties.Select(p => $"{p.Key} = {p.Value}")), 400);
            }

            var inbound = Inbound(m, members);
            if (inbound.Count > 0) o["in"] = "← " + Join(inbound, MaxNames * 2);

            // Units: the group's own objects, and each child group as one unit.
            var children = g.Children.Where(m.GroupById.ContainsKey).Select(c => m.GroupById[c]).ToList();
            var unitOf = new Dictionary<string, string>();
            foreach (var c in children)
            {
                foreach (var id in m.EffectiveMembers(c))
                    unitOf[id] = c.Id;
            }
            foreach (var id in g.Members.Where(m.NodeById.ContainsKey))
                unitOf[id] = id;

            var units = unitOf.Values.Distinct().ToList();
            var preds = units.ToDictionary(u => u, u => new HashSet<string>());
            foreach (var kv in unitOf)
            {
                foreach (var up in m.Upstream(kv.Key))
                {
                    if (unitOf.TryGetValue(up, out var upUnit) && upUnit != kv.Value)
                        preds[kv.Value].Add(upUnit);
                }
            }

            var order = TopoOrder(units, u => preds[u], u => UnitPosition(m, u));
            var lines = new JArray();
            int shown = 0;
            foreach (var u in order)
            {
                if (budget <= 0) break;
                lines.Add(m.GroupById.TryGetValue(u, out var child) ? ChildLine(m, child) : NodeLine(m, m.NodeById[u], members));
                budget--;
                shown++;
            }
            o["objects"] = lines;
            if (shown < order.Count)
            {
                o["more"] = order.Count - shown;
                o["note"] = $"{order.Count - shown} more not shown (at most {MaxObjectLines} lines per call); ask for one child group, or use id= on an object";
            }

            var outbound = Outbound(m, members);
            if (outbound.Count > 0) o["out"] = Join(outbound, MaxEdges * 2, "; ");
            return o;
        }

        private static (float, float) UnitPosition(OutlineModel m, string unit) =>
            m.NodeById.TryGetValue(unit, out var n) ? (n.X, n.Y) : (m.GroupById[unit].X, m.GroupById[unit].Y);

        private static string ChildLine(OutlineModel m, OutlineGroup child)
        {
            var parts = new List<string> { child.Title, Count(m.EffectiveMembers(child).Count, "object") };
            if (child.IsThallus && !string.IsNullOrWhiteSpace(child.Description))
                parts.Add($"author's description: \"{Clip(OneLine(child.Description), 60)}\"");
            parts.Add($"open with group='{(string.IsNullOrWhiteSpace(child.Name) ? child.ShortId : child.Name)}'");
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// One object: short id, type, the user's nickname, its value, and each input with what feeds
        /// it, its port settings or what it holds. Sources outside <paramref name="scope"/> carry the
        /// group they sit in.
        /// </summary>
        private static string NodeLine(OutlineModel m, OutlineNode n, ICollection<string> scope)
        {
            var head = $"{n.ShortId} {n.Type}";
            if (n.Nickname != null) head += $" \"{n.Nickname}\"";
            if (n.Value != null) head += " = " + n.Value;
            var parts = new List<string> { head };

            bool singlePort = n.Kind != OutlineKind.Component;
            foreach (var input in n.Inputs)
            {
                string text = null;
                var label = singlePort ? "" : input.Name + " ";
                if (input.Sources.Count > 0)
                {
                    var refs = input.Sources
                        .Select(s => s.NodeId != null && scope.Contains(s.NodeId) ? Ref(m, s) : RefOutside(m, s))
                        .Distinct().ToList();
                    text = label + "← " + Join(refs, 4);
                }
                else if (!singlePort && input.Stored != null)
                {
                    text = label + "= " + input.Stored;
                }

                if (input.Modifiers != null)
                    text = (text ?? label.Trim()) + $" ({input.Modifiers})";
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text.Trim());
            }

            foreach (var output in n.Outputs.Where(x => x.Modifiers != null))
                parts.Add(singlePort ? $"out ({output.Modifiers})" : $"out {output.Name} ({output.Modifiers})");

            if (n.Level != null)
                parts.Add($"{n.Level}: {Clip(OneLine(n.Message), 100)}");
            return string.Join(" · ", parts);
        }

        // ---- around ---------------------------------------------------------------------------------

        public static JObject Around(OutlineModel m, OutlineNode center, int depth)
        {
            depth = depth <= 0 ? DefaultDepth : Math.Min(depth, MaxDepth);

            var distance = new Dictionary<string, int> { [center.Id] = 0 };
            Walk(m, center.Id, depth, m.Upstream, distance);
            Walk(m, center.Id, depth, m.Downstream, distance);

            var taken = distance.OrderBy(kv => kv.Value)
                .ThenBy(kv => m.NodeById[kv.Key].X).ThenBy(kv => m.NodeById[kv.Key].Y)
                .Take(MaxAroundObjects).Select(kv => kv.Key).ToList();
            var set = new HashSet<string>(taken);

            var order = TopoOrder(taken, id => m.Upstream(id).Where(set.Contains), id => (m.NodeById[id].X, m.NodeById[id].Y));
            var lines = new JArray();
            foreach (var id in order)
            {
                var line = NodeLine(m, m.NodeById[id], set) + GroupTag(m, id);
                lines.Add(id == center.Id ? "* " + line : line);
            }

            var o = new JObject
            {
                ["success"] = true,
                ["around"] = Label(center),
                ["depth"] = depth,
                ["objects"] = lines
            };

            var further = taken.SelectMany(id => m.Upstream(id)).Where(u => !set.Contains(u)).Distinct()
                .Select(u => RefOutside(m, new OutlineSource(u))).ToList();
            if (further.Count > 0) o["fedFrom"] = Join(further, MaxNames);
            var onward = taken.SelectMany(id => m.Downstream(id)).Where(d => !set.Contains(d)).Distinct()
                .Select(d => RefOutside(m, new OutlineSource(d))).ToList();
            if (onward.Count > 0) o["feeds"] = Join(onward, MaxNames);

            if (distance.Count > taken.Count)
            {
                o["more"] = distance.Count - taken.Count;
                o["note"] = $"{distance.Count - taken.Count} more within {depth} wires not shown (at most {MaxAroundObjects}); use a smaller depth or a nearer object";
            }
            return o;
        }

        private static void Walk(OutlineModel m, string start, int depth, Func<string, HashSet<string>> next, Dictionary<string, int> distance)
        {
            var frontier = new List<string> { start };
            var seen = new HashSet<string> { start };
            for (int d = 1; d <= depth && frontier.Count > 0; d++)
            {
                var nextFrontier = new List<string>();
                foreach (var id in frontier)
                {
                    foreach (var n in next(id))
                    {
                        if (!seen.Add(n)) continue;
                        nextFrontier.Add(n);
                        if (!distance.TryGetValue(n, out var old) || d < old) distance[n] = d;
                    }
                }
                frontier = nextFrontier;
            }
        }

        // ---- finding groups -------------------------------------------------------------------------

        /// <summary>
        /// Groups by name: exact, then ignoring case, then a GUID prefix, then the only group whose name
        /// starts with the text ("GRID LINES" for "GRID LINES (sorted W→E)"). Never a loose guess:
        /// two candidates is an error naming both.
        /// </summary>
        public static List<OutlineGroup> FindGroups(OutlineModel m, IEnumerable<string> names, List<string> errors)
        {
            var found = new List<OutlineGroup>();
            foreach (var raw in names)
            {
                var name = (raw ?? "").Trim();
                var g = FindGroup(m, name, errors);
                if (g != null && !found.Contains(g)) found.Add(g);
            }
            return found;
        }

        private static OutlineGroup FindGroup(OutlineModel m, string name, List<string> errors)
        {
            if (name.Length == 0)
            {
                errors.Add("an empty group name");
                return null;
            }

            foreach (var match in new Func<OutlineGroup, bool>[]
            {
                g => g.Name == name,
                g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase),
                g => BatchNames.LooksLikeGuidPrefix(name) && g.Id.StartsWith(name.ToLowerInvariant(), StringComparison.Ordinal),
                g => g.Name != null && g.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            })
            {
                var hits = m.Groups.Where(match).ToList();
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1)
                {
                    errors.Add($"'{name}' matches {hits.Count} groups: {Join(hits.Select(h => $"{h.Title} (id {h.ShortId})"), 5)}; use the full name or the id");
                    return null;
                }
            }

            var closest = BatchNames.Closest(name, m.Groups.Select(g => g.Name));
            errors.Add($"no group named '{name}'."
                + (closest != null ? $" Did you mean '{closest}'?"
                    : m.Groups.Count > 0 ? $" Groups: {Join(m.Groups.Select(g => g.Title), 10)}"
                    : " This canvas has no groups; use id= on an object instead"));
            return null;
        }

        // ---- shared pieces --------------------------------------------------------------------------

        /// <summary>
        /// Objects outside <paramref name="members"/> wired into them (and Thallus outputs), by name.
        /// <paramref name="brief"/> (the overview) leaves the group off sources the user named.
        /// </summary>
        private static List<string> Inbound(OutlineModel m, ICollection<string> members, bool brief = false)
        {
            var result = new List<string>();
            foreach (var id in Ordered(m, members))
            {
                foreach (var input in m.NodeById[id].Inputs)
                {
                    foreach (var s in input.Sources)
                    {
                        if (s.NodeId != null && members.Contains(s.NodeId)) continue;
                        var named = s.NodeId != null && m.NodeById.TryGetValue(s.NodeId, out var src) && src.Nickname != null && src.NicknameUnique;
                        var text = brief && named ? Ref(m, s) : RefOutside(m, s);
                        if (!result.Contains(text)) result.Add(text);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Outputs inside <paramref name="members"/> that feed objects outside: "NAME (items) → where",
        /// where is the group (or the object) they feed.
        /// </summary>
        private static List<string> Outbound(OutlineModel m, ICollection<string> members)
        {
            var result = new List<string>();
            foreach (var id in Ordered(m, members))
            {
                var n = m.NodeById[id];
                foreach (var output in n.Outputs)
                {
                    var outside = output.Recipients.Where(r => m.NodeById.ContainsKey(r) && !members.Contains(r)).ToList();
                    if (outside.Count == 0) continue;
                    var where = outside.Select(r => m.OutermostGroup(r) is OutlineGroup g ? Clip(g.Title, 32) : Label(m.NodeById[r])).Distinct().ToList();
                    var name = n.Kind == OutlineKind.Component ? $"{n.Name}.{output.Name}" : n.Name;
                    result.Add($"{name} ({output.Items}) → {Join(where, MaxDestinations)}");
                }
            }
            return result;
        }

        private static IEnumerable<string> Ordered(OutlineModel m, IEnumerable<string> ids) =>
            ids.Where(m.NodeById.ContainsKey).OrderBy(id => m.NodeById[id].X).ThenBy(id => m.NodeById[id].Y);

        /// <summary>A source as a wire end: the object's name, plus ".port" when it is a component.</summary>
        private static string Ref(OutlineModel m, OutlineSource s)
        {
            if (s.NodeId == null || !m.NodeById.TryGetValue(s.NodeId, out var n))
                return s.Label ?? "?";
            return n.Kind == OutlineKind.Component && s.Port != null ? $"{n.Name}.{s.Port}" : n.Name;
        }

        /// <summary>A source outside the part being shown: also its type when it has no name, and its group.</summary>
        private static string RefOutside(OutlineModel m, OutlineSource s)
        {
            if (s.NodeId == null || !m.NodeById.TryGetValue(s.NodeId, out var n))
                return s.Label ?? "?";
            var text = Ref(m, s);
            if (n.Nickname == null || !n.NicknameUnique) text += $" ({n.Type})";
            return text + GroupTag(m, n.Id);
        }

        /// <summary>The object's name, with its type (or shared nickname) when the name is only a short id.</summary>
        private static string Label(OutlineNode n) =>
            n.Nickname == null ? $"{n.ShortId} {n.Type}"
            : n.NicknameUnique ? n.Nickname
            : $"{n.ShortId} \"{n.Nickname}\"";

        private static string GroupTag(OutlineModel m, string nodeId)
        {
            var g = m.InnermostGroup(nodeId);
            return g == null ? "" : $" [{Clip(g.Title, 32)}]";
        }

        private static void AddProblemCounts(OutlineModel m, ICollection<string> ids, List<string> parts)
        {
            int errors = ids.Count(id => m.NodeById.TryGetValue(id, out var n) && n.Level == "error");
            int warnings = ids.Count(id => m.NodeById.TryGetValue(id, out var n) && n.Level == "warning");
            if (errors > 0) parts.Add(Count(errors, "error"));
            if (warnings > 0) parts.Add(Count(warnings, "warning"));
        }

        /// <summary>Weakly connected sets of objects (wires in either direction) among <paramref name="ids"/>.</summary>
        private static List<List<string>> Components(OutlineModel m, List<string> ids)
        {
            var set = new HashSet<string>(ids);
            var seen = new HashSet<string>();
            var result = new List<List<string>>();
            foreach (var start in ids)
            {
                if (!seen.Add(start)) continue;
                var part = new List<string>();
                var stack = new Stack<string>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    part.Add(id);
                    foreach (var next in m.Upstream(id).Concat(m.Downstream(id)))
                    {
                        if (set.Contains(next) && seen.Add(next)) stack.Push(next);
                    }
                }
                result.Add(part);
            }
            return result;
        }

        /// <summary>
        /// Upstream first: an id comes after every id in <paramref name="preds"/> that is in the list.
        /// Among ids that are ready, the leftmost (then topmost) goes first. Anything left in a cycle is
        /// added at the end by position.
        /// </summary>
        public static List<string> TopoOrder(IReadOnlyCollection<string> ids, Func<string, IEnumerable<string>> preds, Func<string, (float X, float Y)> position)
        {
            var set = new HashSet<string>(ids);
            var indegree = ids.ToDictionary(id => id, _ => 0);
            var successors = ids.ToDictionary(id => id, _ => new List<string>());
            foreach (var id in ids)
            {
                foreach (var p in preds(id).Distinct())
                {
                    if (!set.Contains(p) || p == id) continue;
                    indegree[id]++;
                    successors[p].Add(id);
                }
            }

            var ready = ids.Where(id => indegree[id] == 0).ToList();
            var result = new List<string>(ids.Count);
            while (ready.Count > 0)
            {
                var next = ready.OrderBy(id => position(id).X).ThenBy(id => position(id).Y).First();
                ready.Remove(next);
                result.Add(next);
                foreach (var s in successors[next])
                {
                    if (--indegree[s] == 0) ready.Add(s);
                }
            }

            if (result.Count < ids.Count)
            {
                var placed = new HashSet<string>(result);
                result.AddRange(ids.Where(id => !placed.Contains(id)).OrderBy(id => position(id).X).ThenBy(id => position(id).Y));
            }
            return result;
        }

        private static JArray Capped(IEnumerable<string> lines, int max)
        {
            var list = lines.ToList();
            var array = new JArray(list.Take(max));
            if (list.Count > max) array.Add($"… {list.Count - max} more");
            return array;
        }

        private static string Join(IEnumerable<string> items, int max, string separator = ", ")
        {
            var list = items.ToList();
            var text = string.Join(separator, list.Take(max));
            return list.Count > max ? $"{text}{separator}+{list.Count - max} more" : text;
        }

        public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        public static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        private static string OneLine(string s) =>
            string.Join(" / ", (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}
