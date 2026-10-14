using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Core.Batch
{
    public enum BatchValueKind
    {
        /// <summary>JSON null: clear the stored values.</summary>
        Null,
        Number,
        Boolean,
        Text,
        Point,
        Vector,
        Domain,
        List
    }

    /// <summary>
    /// A <c>set</c> value as written, before it is matched to the target input's type. Host-free:
    /// the host turns it into Grasshopper data once it knows what the input takes.
    /// </summary>
    public sealed class BatchValue
    {
        private BatchValue(BatchValueKind kind) { Kind = kind; }

        public BatchValueKind Kind { get; private set; }
        public double Number { get; private set; }
        public bool Boolean { get; private set; }
        public string Text { get; private set; }

        /// <summary>Point/vector x,y,z (z = 0 when two were given) or domain start,end.</summary>
        public double[] Coords { get; private set; }

        public IReadOnlyList<BatchValue> Items { get; private set; }

        /// <summary>True when <see cref="Number"/> has no fractional part.</summary>
        public bool IsWhole => Kind == BatchValueKind.Number && Math.Abs(Number - Math.Round(Number)) < 1e-12;

        /// <summary>The value as a list of single values (a non-list value is a list of one).</summary>
        public IReadOnlyList<BatchValue> AsList() =>
            Kind == BatchValueKind.List ? Items : (Kind == BatchValueKind.Null ? Array.Empty<BatchValue>() : new[] { this });

        /// <summary>A short rendering for messages: <c>2.5</c>, <c>"abc"</c>, <c>{point:[0,0,1]}</c>.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case BatchValueKind.Null: return "null";
                case BatchValueKind.Number: return Number.ToString("R", CultureInfo.InvariantCulture);
                case BatchValueKind.Boolean: return Boolean ? "true" : "false";
                case BatchValueKind.Text: return "\"" + (Text.Length > 40 ? Text.Substring(0, 40) + "…" : Text) + "\"";
                case BatchValueKind.Point: return "{point:[" + Join(Coords) + "]}";
                case BatchValueKind.Vector: return "{vector:[" + Join(Coords) + "]}";
                case BatchValueKind.Domain: return "{domain:[" + Join(Coords) + "]}";
                case BatchValueKind.List: return "[" + string.Join(", ", Items.Take(5).Select(i => i.Describe())) + (Items.Count > 5 ? ", …" : "") + "]";
                default: return Kind.ToString();
            }
        }

        private static string Join(double[] v) => string.Join(",", v.Select(d => d.ToString("R", CultureInfo.InvariantCulture)));

        public static BatchValue FromNumber(double d) => new BatchValue(BatchValueKind.Number) { Number = d };
        public static BatchValue FromBoolean(bool b) => new BatchValue(BatchValueKind.Boolean) { Boolean = b };
        public static BatchValue FromText(string s) => new BatchValue(BatchValueKind.Text) { Text = s ?? string.Empty };

        /// <summary>
        /// Read a JSON value. Lists may not nest (one flat list of items); objects must be exactly
        /// one of <c>{"point": [x,y(,z)]}</c>, <c>{"vector": [x,y(,z)]}</c>, <c>{"domain": [a,b]}</c>.
        /// </summary>
        public static BatchValue Parse(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null)
                return new BatchValue(BatchValueKind.Null);

            if (token is JArray array)
            {
                var items = new List<BatchValue>();
                foreach (var item in array)
                {
                    if (item is JArray)
                    {
                        error = "nested lists aren't supported; give one flat list of values";
                        return null;
                    }
                    if (item.Type == JTokenType.Null)
                    {
                        error = "a list can't contain null";
                        return null;
                    }
                    var parsed = Parse(item, out error);
                    if (parsed == null) return null;
                    items.Add(parsed);
                }
                return new BatchValue(BatchValueKind.List) { Items = items };
            }

            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return FromNumber(token.Value<double>());
                case JTokenType.Boolean:
                    return FromBoolean(token.Value<bool>());
                case JTokenType.String:
                    return FromText(token.Value<string>());
            }

            if (token is JObject obj)
            {
                var props = obj.Properties().ToList();
                if (props.Count != 1)
                {
                    error = "a value object must be exactly one of {\"point\": [x,y,z]}, {\"vector\": [x,y,z]}, {\"domain\": [a,b]}";
                    return null;
                }

                var key = props[0].Name.Trim().ToLowerInvariant();
                BatchValueKind kind;
                switch (key)
                {
                    case "point": kind = BatchValueKind.Point; break;
                    case "vector": kind = BatchValueKind.Vector; break;
                    case "domain": kind = BatchValueKind.Domain; break;
                    default:
                        error = $"unknown value type '{props[0].Name}'. Use point, vector or domain";
                        return null;
                }

                if (!(props[0].Value is JArray nums) || !nums.All(n => n.Type == JTokenType.Integer || n.Type == JTokenType.Float))
                {
                    error = $"'{key}' needs a list of numbers";
                    return null;
                }

                var coords = nums.Select(n => n.Value<double>()).ToList();
                if (kind == BatchValueKind.Domain)
                {
                    if (coords.Count != 2)
                    {
                        error = "'domain' needs exactly two numbers: [start, end]";
                        return null;
                    }
                }
                else
                {
                    if (coords.Count != 2 && coords.Count != 3)
                    {
                        error = $"'{key}' needs two or three numbers: [x, y] or [x, y, z]";
                        return null;
                    }
                    if (coords.Count == 2) coords.Add(0);
                }

                return new BatchValue(kind) { Coords = coords.ToArray() };
            }

            error = $"unsupported value: {token.ToString(Newtonsoft.Json.Formatting.None)}";
            return null;
        }
    }
}
