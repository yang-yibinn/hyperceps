using System.Collections.Generic;
using System.Linq;

namespace Cordyceps.Core.Outline
{
    /// <summary>What kind of object a line describes; decides how its value is shown.</summary>
    public enum OutlineKind
    {
        Component,
        Parameter,
        Slider,
        Panel,
        Toggle,
        ValueList
    }

    /// <summary>One wire arriving at an input: the object it comes from, and that object's output port.</summary>
    public sealed class OutlineSource
    {
        public OutlineSource(string nodeId, string port = null, string label = null)
        {
            NodeId = nodeId;
            Port = port;
            Label = label;
        }

        /// <summary>The source object's id; null when it isn't an outline object (a Lichen Thallus output).</summary>
        public string NodeId { get; }

        /// <summary>The source's output port when the source is a component; null for a parameter.</summary>
        public string Port { get; }

        /// <summary>Shown instead of a name when <see cref="NodeId"/> is null.</summary>
        public string Label { get; }
    }

    /// <summary>An input port: what is wired into it, its port settings, or what it holds when nothing is wired.</summary>
    public sealed class OutlinePort
    {
        public string Name { get; set; }
        public List<OutlineSource> Sources { get; } = new List<OutlineSource>();

        /// <summary>Port settings in words, e.g. "graft" or "flatten, reverse"; null when none.</summary>
        public string Modifiers { get; set; }

        /// <summary>What an unwired input holds, e.g. "1" or "3 items"; null when nothing or not shown.</summary>
        public string Stored { get; set; }
    }

    /// <summary>An output port: how many items it holds, its port settings, and the objects it feeds.</summary>
    public sealed class OutlineOutput
    {
        public string Name { get; set; }
        public int Items { get; set; }
        public string Modifiers { get; set; }
        public List<string> Recipients { get; } = new List<string>();
    }

    /// <summary>One object on the canvas (never a group), read from Grasshopper by the adapter.</summary>
    public sealed class OutlineNode
    {
        public string Id { get; set; }

        /// <summary>The shortest unique GUID prefix (8+ characters); every tool accepts it as the object's id.</summary>
        public string ShortId { get; set; }

        /// <summary>The object's type as Grasshopper names it: "Addition", "Number Slider", "Panel".</summary>
        public string Type { get; set; }

        /// <summary>A nickname the user gave it; null when it still has its default nickname.</summary>
        public string Nickname { get; set; }

        /// <summary>No other object carries <see cref="Nickname"/>, so the nickname alone names it in every tool.</summary>
        public bool NicknameUnique { get; set; }

        public OutlineKind Kind { get; set; }

        /// <summary>A slider, panel, toggle or value list's value, or what an unwired parameter holds.</summary>
        public string Value { get; set; }

        public List<OutlinePort> Inputs { get; } = new List<OutlinePort>();
        public List<OutlineOutput> Outputs { get; } = new List<OutlineOutput>();

        /// <summary>"error", "warning" or null. Remarks are not problems (see MessageLevels).</summary>
        public string Level { get; set; }
        public string Message { get; set; }

        /// <summary>The component's remarks as one note ("remark: ... (+N more)"), or null.</summary>
        public string Remark { get; set; }

        public float X { get; set; }
        public float Y { get; set; }

        /// <summary>The name other lines and tools use for it: its own nickname when unique, else its short id.</summary>
        public string Name => Nickname != null && NicknameUnique ? Nickname : ShortId;

        public bool IsControl => Kind == OutlineKind.Slider || Kind == OutlineKind.Toggle || Kind == OutlineKind.ValueList;
    }

    /// <summary>A Grasshopper group, or a Lichen Thallus (a group with an author's description and properties).</summary>
    public sealed class OutlineGroup
    {
        public string Id { get; set; }
        public string ShortId { get; set; }
        public string Name { get; set; }
        public string Colour { get; set; }

        /// <summary>Objects directly in the group (never groups).</summary>
        public List<string> Members { get; } = new List<string>();

        /// <summary>Groups directly inside this one.</summary>
        public List<string> Children { get; } = new List<string>();

        public bool IsThallus { get; set; }

        /// <summary>The author's description (Thallus only).</summary>
        public string Description { get; set; }

        /// <summary>The author's key/value properties (Thallus only).</summary>
        public List<KeyValuePair<string, string>> Properties { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>The Thallus's T output is wired (to Lichen).</summary>
        public bool ToLichen { get; set; }

        public float X { get; set; }
        public float Y { get; set; }

        public string Title => (IsThallus ? "THALLUS " : "") + (string.IsNullOrWhiteSpace(Name) ? $"(unnamed group {ShortId})" : Name);
    }

    /// <summary>
    /// Everything the outline reads from one document. Built by the Grasshopper adapter; the rest is
    /// host-free so it can be unit-tested.
    /// </summary>
    public sealed class OutlineModel
    {
        public OutlineModel(string document, IEnumerable<OutlineNode> nodes, IEnumerable<OutlineGroup> groups)
        {
            Document = document;
            Nodes = nodes.ToList();
            Groups = groups.ToList();
            NodeById = Nodes.ToDictionary(n => n.Id);
            GroupById = Groups.ToDictionary(g => g.Id);

            foreach (var g in Groups)
            {
                foreach (var c in g.Children)
                {
                    if (GroupById.ContainsKey(c) && !ParentById.ContainsKey(c))
                        ParentById[c] = g.Id;
                }
            }

            foreach (var n in Nodes)
            {
                foreach (var input in n.Inputs)
                {
                    foreach (var s in input.Sources)
                    {
                        if (s.NodeId != null && NodeById.ContainsKey(s.NodeId))
                            Upstream(n.Id).Add(s.NodeId);
                    }
                }
                foreach (var output in n.Outputs)
                {
                    foreach (var r in output.Recipients)
                    {
                        if (NodeById.ContainsKey(r))
                            Downstream(n.Id).Add(r);
                    }
                }
            }

            // Wires are recorded on both ends; make sure each end knows the other.
            foreach (var kv in _down.ToList())
            {
                foreach (var r in kv.Value)
                    Upstream(r).Add(kv.Key);
            }
            foreach (var kv in _up.ToList())
            {
                foreach (var s in kv.Value)
                    Downstream(s).Add(kv.Key);
            }
        }

        public string Document { get; }
        public List<OutlineNode> Nodes { get; }
        public List<OutlineGroup> Groups { get; }
        public Dictionary<string, OutlineNode> NodeById { get; }
        public Dictionary<string, OutlineGroup> GroupById { get; }

        /// <summary>The group a group sits directly inside.</summary>
        public Dictionary<string, string> ParentById { get; } = new Dictionary<string, string>();

        private readonly Dictionary<string, HashSet<string>> _up = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, HashSet<string>> _down = new Dictionary<string, HashSet<string>>();

        /// <summary>Objects wired into this one.</summary>
        public HashSet<string> Upstream(string id) => _up.TryGetValue(id, out var s) ? s : _up[id] = new HashSet<string>();

        /// <summary>Objects this one feeds.</summary>
        public HashSet<string> Downstream(string id) => _down.TryGetValue(id, out var s) ? s : _down[id] = new HashSet<string>();

        public IEnumerable<OutlineGroup> TopLevelGroups => Groups.Where(g => !ParentById.ContainsKey(g.Id));

        /// <summary>Every object in a group, its child groups included.</summary>
        public HashSet<string> EffectiveMembers(OutlineGroup group)
        {
            var result = new HashSet<string>();
            var seen = new HashSet<string>();
            var stack = new Stack<OutlineGroup>();
            stack.Push(group);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (!seen.Add(g.Id)) continue;
                foreach (var m in g.Members)
                {
                    if (NodeById.ContainsKey(m)) result.Add(m);
                }
                foreach (var c in g.Children)
                {
                    if (GroupById.TryGetValue(c, out var child)) stack.Push(child);
                }
            }
            return result;
        }

        private Dictionary<string, OutlineGroup> _innermost;

        /// <summary>The smallest group an object is in (null when ungrouped).</summary>
        public OutlineGroup InnermostGroup(string nodeId)
        {
            if (_innermost == null)
            {
                _innermost = new Dictionary<string, OutlineGroup>();
                foreach (var g in Groups.OrderByDescending(Depth))
                {
                    foreach (var m in g.Members)
                    {
                        if (!_innermost.ContainsKey(m)) _innermost[m] = g;
                    }
                }
            }
            return _innermost.TryGetValue(nodeId, out var found) ? found : null;
        }

        /// <summary>The outermost group an object is in (null when ungrouped).</summary>
        public OutlineGroup OutermostGroup(string nodeId)
        {
            var g = InnermostGroup(nodeId);
            var seen = new HashSet<string>();
            while (g != null && ParentById.TryGetValue(g.Id, out var parent) && seen.Add(g.Id))
                g = GroupById[parent];
            return g;
        }

        public int Depth(OutlineGroup g)
        {
            int depth = 0;
            var seen = new HashSet<string>();
            var id = g.Id;
            while (ParentById.TryGetValue(id, out var parent) && seen.Add(id))
            {
                depth++;
                id = parent;
            }
            return depth;
        }
    }
}
