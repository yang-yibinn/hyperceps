using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// The raw top-level gh_batch arguments. The MCP server hands each one over as JSON text
    /// (an array or object argument arrives as its raw JSON; a client that stringifies the JSON
    /// sends the same text), or null when the argument was left out.
    /// </summary>
    public sealed class BatchRawInput
    {
        public string Components { get; set; }
        public string Set { get; set; }
        public string Insert { get; set; }
        public string Wires { get; set; }
        public string Disconnect { get; set; }
        public string Delete { get; set; }
        public string Groups { get; set; }
        public string Options { get; set; }
    }

    /// <summary>
    /// Turns the raw arguments into a <see cref="BatchSpec"/>, reporting every shape problem at
    /// once (wrong types, unknown fields, conflicting shortcuts). Only the spec's form is checked
    /// here; whether names, ports and values resolve is the host's job, on the UI thread.
    /// </summary>
    public static class BatchSpecParser
    {
        private static readonly string[] ComponentFields =
        {
            "ref", "type", "param", "panel", "multiline", "slider", "valueList", "toggle", "nickname",
            "x", "y", "inputs", "outputs", "addOutputs", "inputModifiers", "outputModifiers"
        };

        private static readonly string[] SetFields =
        {
            "target", "value", "panel", "multiline", "slider", "valueList", "nickname",
            "inputs", "outputs", "inputModifiers", "outputModifiers"
        };

        private static readonly string[] InsertFields = { "ref", "on", "in", "out" };
        private static readonly string[] GroupFields = { "name", "extend", "color", "members" };
        private static readonly string[] SliderFields = { "min", "max", "value", "decimals" };
        private static readonly string[] ValueListFields = { "items", "selected" };

        private static readonly string[] OptionFields =
        {
            "atomic", "dryRun", "solve", "returnRuntimeMessages", "maxOperations", "timeoutSeconds"
        };

        private static readonly string[] Shortcuts = { "type", "param", "panel", "slider", "valueList", "toggle" };

        public static BatchSpec Parse(BatchRawInput raw, List<string> errors)
        {
            var spec = new BatchSpec();

            var components = ReadArray("components", raw.Components, errors);
            for (int i = 0; i < components.Count; i++)
            {
                var e = ParseComponent($"components[{i}]", components[i], errors);
                if (e != null) spec.Components.Add(e);
            }

            var set = ReadArray("set", raw.Set, errors);
            for (int i = 0; i < set.Count; i++)
            {
                var e = ParseSet($"set[{i}]", set[i], errors);
                if (e != null) spec.Set.Add(e);
            }

            var insert = ReadArray("insert", raw.Insert, errors);
            for (int i = 0; i < insert.Count; i++)
            {
                var e = ParseInsert($"insert[{i}]", insert[i], errors);
                if (e != null) spec.Insert.Add(e);
            }

            var wires = ReadArray("wires", raw.Wires, errors);
            for (int i = 0; i < wires.Count; i++)
            {
                var e = ParseWire($"wires[{i}]", wires[i], allowWildcard: false, errors);
                if (e != null) spec.Wires.Add(e);
            }

            var disconnect = ReadArray("disconnect", raw.Disconnect, errors);
            for (int i = 0; i < disconnect.Count; i++)
            {
                var e = ParseWire($"disconnect[{i}]", disconnect[i], allowWildcard: true, errors);
                if (e != null) spec.Disconnect.Add(e);
            }

            var delete = ReadArray("delete", raw.Delete, errors);
            for (int i = 0; i < delete.Count; i++)
            {
                var path = $"delete[{i}]";
                var name = ReadNameToken(path, delete[i], errors);
                if (name != null) spec.Delete.Add(new DeleteEntry { Path = path, Name = name });
            }

            var groups = ReadArray("groups", raw.Groups, errors);
            for (int i = 0; i < groups.Count; i++)
            {
                var e = ParseGroup($"groups[{i}]", groups[i], errors);
                if (e != null) spec.Groups.Add(e);
            }

            spec.Options = ParseOptions(raw.Options, errors);
            return spec;
        }

        // ---- top-level fields --------------------------------------------------------------

        private static List<JToken> ReadArray(string field, string text, List<string> errors)
        {
            var token = ReadJson(field, text, errors);
            if (token == null || token.Type == JTokenType.Null)
                return new List<JToken>();

            if (token is JArray array)
                return array.ToList();

            errors.Add($"{field}: must be a list ([...]), got {Kind(token)}");
            return new List<JToken>();
        }

        private static JToken ReadJson(string field, string text, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                var token = JToken.Parse(text);
                // A client that double-encodes sends a JSON string holding the JSON; unwrap once.
                if (token.Type == JTokenType.String)
                {
                    var inner = token.Value<string>().Trim();
                    if (inner.StartsWith("[") || inner.StartsWith("{"))
                        token = JToken.Parse(inner);
                }
                return token;
            }
            catch (JsonException ex)
            {
                errors.Add($"{field}: not valid JSON ({ex.Message})");
                return null;
            }
        }

        // ---- entries ------------------------------------------------------------------------

        private static ComponentEntry ParseComponent(string path, JToken token, List<string> errors)
        {
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"ref\": \"split\", \"type\": \"Text Split\"}}, got {Kind(token)}");
                return null;
            }

            CheckFields(path, obj, ComponentFields, errors);
            var entry = new ComponentEntry { Path = path };

            entry.Ref = ReadString(path, obj, "ref", errors);
            if (entry.Ref == null)
                errors.Add($"{path}: 'ref' is required (a local name such as \"split\")");
            else if (!BatchNames.IsValidRef(entry.Ref))
                errors.Add($"{path}.ref: '{entry.Ref}' can only use letters, digits, '_' and '-' (no dots or spaces)");

            var given = Shortcuts.Where(s => Find(obj, s) != null).ToList();
            if (given.Count == 0)
            {
                errors.Add($"{path}: say what to create with one of: {string.Join(", ", Shortcuts)}");
                return entry.Ref == null ? null : entry;
            }
            if (given.Count > 1)
                errors.Add($"{path}: use only one of {string.Join(", ", given)}");

            switch (given[0])
            {
                case "type":
                    entry.Kind = CreateKind.Type;
                    entry.TypeText = ReadString(path, obj, "type", errors);
                    break;
                case "param":
                    entry.Kind = CreateKind.Param;
                    entry.TypeText = ReadString(path, obj, "param", errors);
                    break;
                case "panel":
                    entry.Kind = CreateKind.Panel;
                    entry.PanelText = ReadText(path, obj, "panel", errors) ?? string.Empty;
                    break;
                case "slider":
                    entry.Kind = CreateKind.Slider;
                    entry.Slider = ParseSlider(path + ".slider", Find(obj, "slider"), errors);
                    break;
                case "valueList":
                    entry.Kind = CreateKind.ValueList;
                    entry.ValueList = ParseValueList(path + ".valueList", Find(obj, "valueList"), requireItems: true, errors);
                    break;
                case "toggle":
                    entry.Kind = CreateKind.Toggle;
                    entry.Toggle = ReadBool(path, obj, "toggle", errors);
                    break;
            }

            entry.Multiline = ReadBool(path, obj, "multiline", errors);
            if (entry.Multiline.HasValue && entry.Kind != CreateKind.Panel)
                errors.Add($"{path}.multiline: only applies to panels");

            entry.Nickname = ReadString(path, obj, "nickname", errors);
            entry.X = ReadNumber(path, obj, "x", errors);
            entry.Y = ReadNumber(path, obj, "y", errors);
            if (entry.X.HasValue != entry.Y.HasValue)
                errors.Add($"{path}: give both x and y, or neither for auto-layout");

            entry.Inputs = ReadCount(path, obj, "inputs", errors);
            entry.Outputs = ReadCount(path, obj, "outputs", errors);
            entry.AddOutputs = ReadCount(path, obj, "addOutputs", errors);
            if (entry.Outputs.HasValue && entry.AddOutputs.HasValue)
                errors.Add($"{path}: use 'outputs' (a total) or 'addOutputs' (extra ones), not both");

            ParseModifiers(path + ".inputModifiers", Find(obj, "inputModifiers"), entry.InputModifiers, errors);
            ParseModifiers(path + ".outputModifiers", Find(obj, "outputModifiers"), entry.OutputModifiers, errors);
            return entry;
        }

        private static SetEntry ParseSet(string path, JToken token, List<string> errors)
        {
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"target\": \"item.i\", \"value\": 2}}, got {Kind(token)}");
                return null;
            }

            CheckFields(path, obj, SetFields, errors);
            var entry = new SetEntry { Path = path };

            entry.Target = ReadString(path, obj, "target", errors);
            if (entry.Target == null)
            {
                errors.Add($"{path}: 'target' is required");
                return null;
            }

            var valueToken = Find(obj, "value");
            if (valueToken != null)
            {
                entry.HasValue = true;
                entry.Value = BatchValue.Parse(valueToken, out var valueError);
                if (entry.Value == null)
                    errors.Add($"{path}.value: {valueError}");
            }

            if (Find(obj, "panel") != null)
                entry.Panel = ReadText(path, obj, "panel", errors) ?? string.Empty;
            entry.Multiline = ReadBool(path, obj, "multiline", errors);

            if (Find(obj, "slider") != null)
                entry.Slider = ParseSlider(path + ".slider", Find(obj, "slider"), errors);
            if (Find(obj, "valueList") != null)
                entry.ValueList = ParseValueList(path + ".valueList", Find(obj, "valueList"), requireItems: false, errors);

            entry.Nickname = ReadString(path, obj, "nickname", errors);
            entry.Inputs = ReadCount(path, obj, "inputs", errors);
            entry.Outputs = ReadCount(path, obj, "outputs", errors);
            ParseModifiers(path + ".inputModifiers", Find(obj, "inputModifiers"), entry.InputModifiers, errors);
            ParseModifiers(path + ".outputModifiers", Find(obj, "outputModifiers"), entry.OutputModifiers, errors);

            bool changesSomething = entry.HasValue || entry.Panel != null || entry.Multiline.HasValue
                || entry.Slider != null || entry.ValueList != null || entry.Nickname != null
                || entry.Inputs.HasValue || entry.Outputs.HasValue
                || entry.InputModifiers.Count > 0 || entry.OutputModifiers.Count > 0
                || Find(obj, "inputModifiers") != null || Find(obj, "outputModifiers") != null;
            if (!changesSomething)
                errors.Add($"{path}: nothing to change. Give value, panel, slider, valueList, nickname, inputs/outputs or input/outputModifiers");

            return entry;
        }

        private static InsertEntry ParseInsert(string path, JToken token, List<string> errors)
        {
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"ref\": \"cull\", \"on\": [\"POSTS.0\", \"POST OUT.0\"]}}, got {Kind(token)}");
                return null;
            }

            CheckFields(path, obj, InsertFields, errors);
            var entry = new InsertEntry { Path = path };

            entry.Ref = ReadString(path, obj, "ref", errors);
            if (entry.Ref == null)
                errors.Add($"{path}: 'ref' is required (a component created in this batch)");

            var on = Find(obj, "on");
            if (!(on is JArray pair) || pair.Count != 2)
            {
                errors.Add($"{path}.on: must be the existing wire as [output, input]");
            }
            else
            {
                entry.OnFrom = ParseAddress($"{path}.on[0]", pair[0], allowWildcard: false, errors);
                entry.OnTo = ParseAddress($"{path}.on[1]", pair[1], allowWildcard: false, errors);
            }

            entry.In = ReadPortSpec(path, obj, "in", errors) ?? "0";
            entry.Out = ReadPortSpec(path, obj, "out", errors) ?? "0";
            return entry.Ref == null ? null : entry;
        }

        private static WireEntry ParseWire(string path, JToken token, bool allowWildcard, List<string> errors)
        {
            if (!(token is JArray pair) || pair.Count != 2)
            {
                errors.Add($"{path}: must be a pair [output, input], e.g. [\"split.0\", \"item.i\"]");
                return null;
            }

            var from = ParseAddress($"{path}[0]", pair[0], allowWildcard, errors);
            var to = ParseAddress($"{path}[1]", pair[1], allowWildcard, errors);
            if (from == null || to == null)
                return null;

            if (from.IsWildcard && to.IsWildcard)
            {
                errors.Add($"{path}: only one side can be \"*\"");
                return null;
            }

            return new WireEntry { Path = path, From = from, To = to };
        }

        private static GroupEntry ParseGroup(string path, JToken token, List<string> errors)
        {
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"name\": \"PAIRS\", \"members\": [...]}}, got {Kind(token)}");
                return null;
            }

            CheckFields(path, obj, GroupFields, errors);
            var entry = new GroupEntry
            {
                Path = path,
                Name = ReadString(path, obj, "name", errors),
                Extend = ReadString(path, obj, "extend", errors),
                Color = ReadString(path, obj, "color", errors)
            };

            if (entry.Name == null && entry.Extend == null)
                errors.Add($"{path}: give 'name' to create a group or 'extend' to add to an existing one");
            else if (entry.Name != null && entry.Extend != null)
                errors.Add($"{path}: use 'name' (create) or 'extend' (add to existing), not both");

            var members = Find(obj, "members");
            if (members != null)
            {
                if (members is JArray list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var name = ReadNameToken($"{path}.members[{i}]", list[i], errors);
                        if (name != null) entry.Members.Add(name);
                    }
                }
                else
                {
                    errors.Add($"{path}.members: must be a list of objects");
                }
            }
            else if (entry.Extend != null)
            {
                errors.Add($"{path}.members: list the objects to add to '{entry.Extend}'");
            }

            return entry;
        }

        private static BatchOptions ParseOptions(string text, List<string> errors)
        {
            var options = new BatchOptions();
            var token = ReadJson("options", text, errors);
            if (token == null || token.Type == JTokenType.Null)
                return options;

            if (!(token is JObject obj))
            {
                errors.Add($"options: must be an object, got {Kind(token)}");
                return options;
            }

            CheckFields("options", obj, OptionFields, errors);
            options.Atomic = ReadBool("options", obj, "atomic", errors) ?? options.Atomic;
            options.DryRun = ReadBool("options", obj, "dryRun", errors) ?? options.DryRun;
            options.Solve = ReadBool("options", obj, "solve", errors) ?? options.Solve;
            options.ReturnRuntimeMessages = ReadBool("options", obj, "returnRuntimeMessages", errors) ?? options.ReturnRuntimeMessages;

            var max = ReadCount("options", obj, "maxOperations", errors);
            if (max.HasValue)
            {
                if (max.Value < 1 || max.Value > BatchOptions.MaxOperationsCeiling)
                    errors.Add($"options.maxOperations: must be 1-{BatchOptions.MaxOperationsCeiling}");
                else
                    options.MaxOperations = max.Value;
            }

            var timeout = ReadNumber("options", obj, "timeoutSeconds", errors);
            if (timeout.HasValue)
            {
                if (timeout.Value <= 0 || timeout.Value > BatchOptions.TimeoutCeilingSeconds)
                    errors.Add($"options.timeoutSeconds: must be more than 0 and at most {BatchOptions.TimeoutCeilingSeconds}");
                else
                    options.TimeoutSeconds = timeout.Value;
            }

            return options;
        }

        // ---- nested shapes ------------------------------------------------------------------

        private static SliderSpec ParseSlider(string path, JToken token, List<string> errors)
        {
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"min\": 1, \"max\": 20, \"value\": 8, \"decimals\": 0}}");
                return new SliderSpec();
            }

            CheckFields(path, obj, SliderFields, errors);
            var slider = new SliderSpec
            {
                Min = ReadNumber(path, obj, "min", errors),
                Max = ReadNumber(path, obj, "max", errors),
                Value = ReadNumber(path, obj, "value", errors),
                Decimals = ReadCount(path, obj, "decimals", errors)
            };

            if (slider.Min.HasValue && slider.Max.HasValue && slider.Min.Value >= slider.Max.Value)
                errors.Add($"{path}: min ({Fmt(slider.Min.Value)}) must be less than max ({Fmt(slider.Max.Value)})");
            if (slider.Decimals.HasValue && slider.Decimals.Value > 12)
                errors.Add($"{path}.decimals: at most 12");
            return slider;
        }

        private static ValueListSpec ParseValueList(string path, JToken token, bool requireItems, List<string> errors)
        {
            var spec = new ValueListSpec();
            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must be an object like {{\"items\": [{{\"name\": \"A\", \"value\": \"1\"}}], \"selected\": 0}}");
                return spec;
            }

            CheckFields(path, obj, ValueListFields, errors);

            var items = Find(obj, "items");
            if (items != null)
            {
                if (!(items is JArray list) || list.Count == 0)
                {
                    errors.Add($"{path}.items: must be a non-empty list of {{\"name\": ..., \"value\": ...}}");
                }
                else
                {
                    spec.Items = new List<KeyValuePair<string, string>>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!(list[i] is JObject item))
                        {
                            errors.Add($"{path}.items[{i}]: must be {{\"name\": ..., \"value\": ...}}");
                            continue;
                        }
                        var name = Find(item, "name");
                        var value = Find(item, "value");
                        if (name == null || value == null || name.Type == JTokenType.Null || value.Type == JTokenType.Null)
                        {
                            errors.Add($"{path}.items[{i}]: needs both 'name' and 'value'");
                            continue;
                        }
                        spec.Items.Add(new KeyValuePair<string, string>(ScalarText(name), ScalarText(value)));
                    }
                }
            }
            else if (requireItems)
            {
                errors.Add($"{path}.items: list the value list's items");
            }

            var selected = Find(obj, "selected");
            if (selected != null && selected.Type != JTokenType.Null)
            {
                if (selected.Type == JTokenType.Integer)
                    spec.SelectedIndex = selected.Value<int>();
                else if (selected.Type == JTokenType.String)
                    spec.SelectedName = selected.Value<string>();
                else
                    errors.Add($"{path}.selected: an index or an item name");

                if (spec.SelectedIndex.HasValue && spec.Items != null
                    && (spec.SelectedIndex.Value < 0 || spec.SelectedIndex.Value >= spec.Items.Count))
                    errors.Add($"{path}.selected: index {spec.SelectedIndex.Value} is out of range (0-{spec.Items.Count - 1})");
                if (spec.SelectedName != null && spec.Items != null && !spec.Items.Any(i => i.Key == spec.SelectedName))
                    errors.Add($"{path}.selected: no item named '{spec.SelectedName}'. Items: {string.Join(", ", spec.Items.Select(i => i.Key))}");
            }

            return spec;
        }

        /// <summary>
        /// Port settings: <c>{"i": "graft"}</c>, <c>{"T": ["flatten", "simplify"]}</c>; <c>"none"</c>
        /// or <c>[]</c> means no settings. Graft and flatten exclude each other.
        /// </summary>
        public static void ParseModifiers(string path, JToken token, List<ModifierEntry> into, List<string> errors)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (!(token is JObject obj))
            {
                errors.Add($"{path}: must map ports to settings, e.g. {{\"i\": \"graft\"}}");
                return;
            }

            foreach (var prop in obj.Properties())
            {
                var portPath = $"{path}.{prop.Name}";
                var values = new List<string>();
                if (prop.Value.Type == JTokenType.String)
                    values.Add(prop.Value.Value<string>());
                else if (prop.Value is JArray array && array.All(v => v.Type == JTokenType.String))
                    values.AddRange(array.Select(v => v.Value<string>()));
                else
                {
                    errors.Add($"{portPath}: a setting or a list of settings (graft, flatten, simplify, reverse)");
                    continue;
                }

                var set = new ModifierSet();
                bool ok = true;
                foreach (var raw in values)
                {
                    switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "graft": set.Graft = true; break;
                        case "flatten": set.Flatten = true; break;
                        case "simplify": set.Simplify = true; break;
                        case "reverse": set.Reverse = true; break;
                        case "none":
                            if (values.Count > 1)
                            {
                                errors.Add($"{portPath}: 'none' can't be combined with other settings");
                                ok = false;
                            }
                            break;
                        default:
                            errors.Add($"{portPath}: unknown setting '{raw}'. Use graft, flatten, simplify or reverse");
                            ok = false;
                            break;
                    }
                }

                if (set.Graft && set.Flatten)
                {
                    errors.Add($"{portPath}: graft and flatten exclude each other");
                    ok = false;
                }

                if (ok)
                    into.Add(new ModifierEntry(prop.Name, set));
            }
        }

        private static PortAddress ParseAddress(string path, JToken token, bool allowWildcard, List<string> errors)
        {
            PortAddress address = null;
            if (token != null && token.Type == JTokenType.String)
            {
                address = PortAddress.Parse(token.Value<string>(), out var error);
                if (address == null)
                {
                    errors.Add($"{path}: {error}");
                    return null;
                }
            }
            else if (token is JObject obj)
            {
                var objName = Find(obj, "obj");
                if (objName == null || objName.Type != JTokenType.String || string.IsNullOrWhiteSpace(objName.Value<string>()))
                {
                    errors.Add($"{path}: the explicit form needs \"obj\", e.g. {{\"obj\": \"v1.2\", \"port\": \"L\"}}");
                    return null;
                }
                var portToken = Find(obj, "port");
                string port = null;
                if (portToken != null && portToken.Type != JTokenType.Null)
                {
                    if (portToken.Type == JTokenType.Integer || portToken.Type == JTokenType.String)
                        port = ScalarText(portToken).Trim();
                    else
                    {
                        errors.Add($"{path}.port: an index, name or nickname");
                        return null;
                    }
                }
                var text = objName.Value<string>().Trim();
                address = PortAddress.Of(text, port, port == null ? text : $"{text}.{port}");
            }
            else
            {
                errors.Add($"{path}: a port like \"split.0\" or {{\"obj\": ..., \"port\": ...}}, got {Kind(token)}");
                return null;
            }

            if (address.IsWildcard && !allowWildcard)
            {
                errors.Add($"{path}: \"*\" is only allowed in disconnect");
                return null;
            }

            return address;
        }

        // ---- small readers ------------------------------------------------------------------

        private static JToken Find(JObject obj, string name)
        {
            var prop = obj.Property(name, StringComparison.Ordinal);
            return prop?.Value;
        }

        /// <summary>
        /// Report fields the spec does not know, suggesting the intended one: an agent that
        /// writes 'nickName' or 'wire' should be told, not silently ignored.
        /// </summary>
        private static void CheckFields(string path, JObject obj, string[] known, List<string> errors)
        {
            foreach (var prop in obj.Properties())
            {
                if (known.Contains(prop.Name, StringComparer.Ordinal))
                    continue;

                var suggestion = BatchNames.Closest(prop.Name, known);
                errors.Add($"{path}: unknown field '{prop.Name}'."
                    + (suggestion != null ? $" Did you mean '{suggestion}'?" : $" Known: {string.Join(", ", known)}"));
            }
        }

        private static string ReadString(string path, JObject obj, string field, List<string> errors)
        {
            var token = Find(obj, field);
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.String)
            {
                errors.Add($"{path}.{field}: must be text, got {Kind(token)}");
                return null;
            }
            var s = token.Value<string>().Trim();
            if (s.Length == 0)
            {
                errors.Add($"{path}.{field}: is empty");
                return null;
            }
            return s;
        }

        /// <summary>Panel text keeps its whitespace and accepts numbers/booleans as their text.</summary>
        private static string ReadText(string path, JObject obj, string field, List<string> errors)
        {
            var token = Find(obj, field);
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer
                || token.Type == JTokenType.Float || token.Type == JTokenType.Boolean)
                return ScalarText(token);
            errors.Add($"{path}.{field}: must be text, got {Kind(token)}");
            return null;
        }

        private static string ReadPortSpec(string path, JObject obj, string field, List<string> errors)
        {
            var token = Find(obj, field);
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer)
                return ScalarText(token).Trim();
            errors.Add($"{path}.{field}: a port index, name or nickname");
            return null;
        }

        private static bool? ReadBool(string path, JObject obj, string field, List<string> errors)
        {
            var token = Find(obj, field);
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            if (token.Type == JTokenType.String && ParseHelpers.TryParseBool(token.Value<string>(), out var b))
                return b;
            errors.Add($"{path}.{field}: must be true or false");
            return null;
        }

        private static double? ReadNumber(string path, JObject obj, string field, List<string> errors)
        {
            var token = Find(obj, field);
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>();
            if (token.Type == JTokenType.String
                && double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            errors.Add($"{path}.{field}: must be a number");
            return null;
        }

        private static int? ReadCount(string path, JObject obj, string field, List<string> errors)
        {
            var number = ReadNumber(path, obj, field, errors);
            if (!number.HasValue)
                return null;
            if (number.Value < 0 || number.Value != Math.Floor(number.Value) || number.Value > int.MaxValue)
            {
                errors.Add($"{path}.{field}: must be a whole number, 0 or more");
                return null;
            }
            return (int)number.Value;
        }

        private static string ReadNameToken(string path, JToken token, List<string> errors)
        {
            if (token != null && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()))
                return token.Value<string>().Trim();
            errors.Add($"{path}: an object name (ref, nickname, GUID or GUID prefix)");
            return null;
        }

        private static string ScalarText(JToken token) =>
            token.Type == JTokenType.String
                ? token.Value<string>()
                : token.Type == JTokenType.Boolean
                    ? (token.Value<bool>() ? "true" : "false")
                    : Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);

        private static string Kind(JToken token)
        {
            if (token == null) return "nothing";
            switch (token.Type)
            {
                case JTokenType.Object: return "an object";
                case JTokenType.Array: return "a list";
                case JTokenType.String: return "text";
                case JTokenType.Integer:
                case JTokenType.Float: return "a number";
                case JTokenType.Boolean: return "true/false";
                case JTokenType.Null: return "null";
                default: return token.Type.ToString();
            }
        }

        private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }
}
