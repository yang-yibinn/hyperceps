using System;
using System.Collections.Generic;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// A gh_batch spec after parsing: plain data, no Grasshopper types, so the parser and the
    /// rules that read it can be unit-tested in <c>Cordyceps.Tests</c>. Every entry keeps the
    /// path it was written at (<c>wires[3]</c>) so each error names the entry it is about.
    /// The contract is <c>docs/gh-batch-schema.md</c>.
    /// </summary>
    public sealed class BatchSpec
    {
        public List<ComponentEntry> Components { get; } = new List<ComponentEntry>();
        public List<SetEntry> Set { get; } = new List<SetEntry>();
        public List<InsertEntry> Insert { get; } = new List<InsertEntry>();
        public List<WireEntry> Wires { get; } = new List<WireEntry>();
        public List<WireEntry> Disconnect { get; } = new List<WireEntry>();
        public List<DeleteEntry> Delete { get; } = new List<DeleteEntry>();
        public List<GroupEntry> Groups { get; } = new List<GroupEntry>();
        public BatchOptions Options { get; set; } = new BatchOptions();

        /// <summary>One operation is one entry in any list (the unit <c>maxOperations</c> counts).</summary>
        public int OperationCount =>
            Components.Count + Set.Count + Insert.Count + Wires.Count
            + Disconnect.Count + Delete.Count + Groups.Count;

        public bool IsEmpty => OperationCount == 0;
    }

    public sealed class BatchOptions
    {
        public const int DefaultMaxOperations = 1000;
        public const int MaxOperationsCeiling = 5000;
        public const double DefaultTimeoutSeconds = 60;
        public const double TimeoutCeilingSeconds = 110;

        public bool Atomic { get; set; } = true;
        public bool DryRun { get; set; }
        public bool Solve { get; set; } = true;
        public bool ReturnRuntimeMessages { get; set; } = true;
        public int MaxOperations { get; set; } = DefaultMaxOperations;
        public double TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    }

    /// <summary>What a component entry creates.</summary>
    public enum CreateKind
    {
        Type,
        Param,
        Panel,
        Slider,
        ValueList,
        Toggle
    }

    public sealed class ComponentEntry
    {
        public string Path { get; set; }
        public string Ref { get; set; }
        public CreateKind Kind { get; set; }
        /// <summary>The text after <c>type</c> or <c>param</c>; null for the other shortcuts.</summary>
        public string TypeText { get; set; }
        public string PanelText { get; set; }
        public bool? Multiline { get; set; }
        public SliderSpec Slider { get; set; }
        public ValueListSpec ValueList { get; set; }
        public bool? Toggle { get; set; }
        public string Nickname { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public int? Inputs { get; set; }
        public int? Outputs { get; set; }
        public int? AddOutputs { get; set; }
        public List<ModifierEntry> InputModifiers { get; } = new List<ModifierEntry>();
        public List<ModifierEntry> OutputModifiers { get; } = new List<ModifierEntry>();
    }

    public sealed class SliderSpec
    {
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Value { get; set; }
        public int? Decimals { get; set; }
    }

    public sealed class ValueListSpec
    {
        /// <summary>Replacement items; null when only <c>selected</c> was given.</summary>
        public List<KeyValuePair<string, string>> Items { get; set; }
        public int? SelectedIndex { get; set; }
        public string SelectedName { get; set; }
        public bool HasSelection => SelectedIndex.HasValue || SelectedName != null;
    }

    public sealed class SetEntry
    {
        public string Path { get; set; }
        public string Target { get; set; }
        public bool HasValue { get; set; }
        public BatchValue Value { get; set; }
        public string Panel { get; set; }
        public bool? Multiline { get; set; }
        public SliderSpec Slider { get; set; }
        public ValueListSpec ValueList { get; set; }
        public string Nickname { get; set; }
        public int? Inputs { get; set; }
        public int? Outputs { get; set; }
        public List<ModifierEntry> InputModifiers { get; } = new List<ModifierEntry>();
        public List<ModifierEntry> OutputModifiers { get; } = new List<ModifierEntry>();
    }

    public sealed class InsertEntry
    {
        public string Path { get; set; }
        public string Ref { get; set; }
        public PortAddress OnFrom { get; set; }
        public PortAddress OnTo { get; set; }
        public string In { get; set; } = "0";
        public string Out { get; set; } = "0";
    }

    public sealed class WireEntry
    {
        public string Path { get; set; }
        public PortAddress From { get; set; }
        public PortAddress To { get; set; }
    }

    public sealed class DeleteEntry
    {
        public string Path { get; set; }
        public string Name { get; set; }
    }

    public sealed class GroupEntry
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Extend { get; set; }
        public string Color { get; set; }
        public List<string> Members { get; } = new List<string>();
    }

    /// <summary>
    /// Port settings for one port, as written: <c>{"i": "graft"}</c> or <c>{"T": ["flatten",
    /// "simplify"]}</c>. <see cref="Settings"/> is the complete set the port should end up with.
    /// </summary>
    public sealed class ModifierEntry
    {
        public ModifierEntry(string port, ModifierSet settings)
        {
            Port = port;
            Settings = settings;
        }

        public string Port { get; }
        public ModifierSet Settings { get; }
    }

    public sealed class ModifierSet
    {
        public bool Graft { get; set; }
        public bool Flatten { get; set; }
        public bool Simplify { get; set; }
        public bool Reverse { get; set; }

        public DataMappingChoice Mapping =>
            Graft ? DataMappingChoice.Graft : Flatten ? DataMappingChoice.Flatten : DataMappingChoice.None;

        public bool IsEmpty => !Graft && !Flatten && !Simplify && !Reverse;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Graft) parts.Add("graft");
            if (Flatten) parts.Add("flatten");
            if (Simplify) parts.Add("simplify");
            if (Reverse) parts.Add("reverse");
            return parts.Count == 0 ? "none" : string.Join("+", parts);
        }
    }

    /// <summary>
    /// One end of a wire as written: an object name plus an optional port, or the <c>*</c>
    /// wildcard (disconnect only). <c>"obj.port"</c> splits on its last dot, so nicknames that
    /// contain dots still work; the explicit form <c>{"obj": ..., "port": ...}</c> never splits.
    /// </summary>
    public sealed class PortAddress
    {
        private PortAddress(string obj, string port, bool wildcard, string text)
        {
            Obj = obj;
            Port = port;
            IsWildcard = wildcard;
            Text = text;
        }

        public string Obj { get; }

        /// <summary>The port as written, or null when none was (a single-port object).</summary>
        public string Port { get; }

        public bool IsWildcard { get; }

        /// <summary>The address as the caller wrote it, for messages.</summary>
        public string Text { get; }

        public static PortAddress Wildcard() => new PortAddress(null, null, true, "*");

        public static PortAddress Of(string obj, string port, string text) => new PortAddress(obj, port, false, text);

        /// <summary>
        /// Split a string address on its last dot. Returns null with an error for an empty object or
        /// port (<c>".0"</c>, <c>"split."</c>).
        /// </summary>
        public static PortAddress Parse(string text, out string error)
        {
            error = null;
            var s = (text ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                error = "empty port address";
                return null;
            }

            if (s == "*")
                return Wildcard();

            int dot = s.LastIndexOf('.');
            if (dot < 0)
                return Of(s, null, s);

            var obj = s.Substring(0, dot).Trim();
            var port = s.Substring(dot + 1).Trim();
            if (obj.Length == 0 || port.Length == 0)
            {
                error = $"'{s}' is not 'obj.port'. Write the object and the port, e.g. 'split.0', or use {{\"obj\": ..., \"port\": ...}}";
                return null;
            }

            return Of(obj, port, s);
        }
    }
}
