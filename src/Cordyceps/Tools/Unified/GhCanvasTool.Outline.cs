using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Cordyceps.Core.Outline;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: gh_canvas(action='outline') — the canvas in a few short lines (see
    /// <see cref="CanvasOutline"/>). This part reads the live document into an
    /// <see cref="OutlineModel"/>; the views themselves are host-free.
    /// </summary>
    public partial class GhCanvasTool
    {
        private string ActionOutline(string id, string group, int depth)
        {
            // Reading the canvas needs the UI thread; answer at once if it is blocked.
            var liveness = SolverState.Shared.Derive();
            if (liveness.Ui == UiLiveness.Blocked)
                return StatusEnvelope.BusyResult(liveness);

            bool hasId = !string.IsNullOrWhiteSpace(id);
            bool hasGroup = !string.IsNullOrWhiteSpace(group);
            if (hasId && hasGroup)
                return ToolHelpers.ErrorResponse("outline takes 'group' (how a group is wired inside) or 'id' (what is around one object), not both");

            List<string> groupNames = null;
            if (hasGroup && !TryParseNames(group, out groupNames, out var parseError))
                return ToolHelpers.ErrorResponse(parseError);

            return _context.ExecuteOnUiThread(() =>
            {
                if (!ToolHelpers.TryGetActiveDocument(_context, out var doc, out var error))
                    return ToolHelpers.ErrorResponse(error);

                var model = OutlineReader.Read(doc);
                JObject result;
                if (groupNames != null)
                {
                    // Group names, plus 'network N' and 'ungrouped' (CanvasOutline.Regions).
                    var errors = new List<string>();
                    result = CanvasOutline.Open(model, groupNames, errors);
                    if (errors.Count > 0 || result == null)
                        return ToolHelpers.ErrorResponse(string.Join("; ", errors));
                }
                else if (hasId)
                {
                    if (!ToolHelpers.TryResolveId(doc, id, out var guid, out var idError))
                        return ToolHelpers.ErrorResponse(idError ?? $"no object named '{id}'");

                    var key = guid.ToString("D");
                    if (model.NodeById.TryGetValue(key, out var node))
                        result = CanvasOutline.Around(model, node, depth);
                    else if (model.GroupById.TryGetValue(key, out var g))
                        result = CanvasOutline.Groups(model, new[] { g });
                    else
                        return ToolHelpers.ErrorResponse($"Component not found: {id}");
                }
                else
                {
                    result = CanvasOutline.Overview(model);
                }
                return result.ToString(Formatting.None);
            });
        }

        /// <summary>One group name, or a JSON list of them.</summary>
        private static bool TryParseNames(string text, out List<string> names, out string error)
        {
            names = null;
            error = null;
            var s = text.Trim();
            if (!s.StartsWith("["))
            {
                names = new List<string> { s };
                return true;
            }
            try
            {
                names = JArray.Parse(s).Select(t => t.Type == JTokenType.String ? t.Value<string>() : null).ToList();
                if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
                {
                    error = "'group' as a list must hold group names, e.g. [\"POINTS\", \"UPPER CURVES\"]";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = $"'group' starts with '[' but isn't a JSON list: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>Reads a Grasshopper document into an <see cref="OutlineModel"/>. Call on the UI thread.</summary>
    internal static class OutlineReader
    {
        public static OutlineModel Read(GH_Document doc)
        {
            var infraIds = ToolHelpers.GetCordycepsInfrastructureIds(doc);
            var prefixes = new GuidPrefixIndex(doc.Objects.Select(o => o.InstanceGuid));
            var defaults = DefaultNicknames();
            var objects = ToolHelpers.GetActiveObjects(doc, infraIds).ToList();

            var groups = objects.OfType<GH_Group>().ToList();
            var groupIds = new HashSet<Guid>(groups.Select(g => g.InstanceGuid));
            var endpoints = objects.Where(LichenThallus.IsEndpoint).ToList();
            var endpointIds = new HashSet<Guid>(endpoints.Select(e => e.InstanceGuid));
            var items = objects.Where(o => !(o is GH_Group) && !endpointIds.Contains(o.InstanceGuid)
                                           && (o is IGH_Component || o is IGH_Param)).ToList();
            var itemIds = new HashSet<Guid>(items.Select(o => o.InstanceGuid));

            // A nickname names an object in every tool only when nothing else carries it, the
            // default nicknames of other objects included (gh_batch's rule).
            var nicknameCounts = objects.Where(o => !(o is GH_Group) && !string.IsNullOrEmpty(o.NickName))
                .GroupBy(o => o.NickName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            // Thallus outputs: the label a wire from one shows, and whether the Thallus reaches Lichen.
            var endpointLabel = new Dictionary<Guid, string>();
            var toLichen = new HashSet<Guid>();
            foreach (var e in endpoints)
            {
                var owner = LichenThallus.Owner(groups, e.InstanceGuid);
                endpointLabel[e.InstanceGuid] = "THALLUS " + (owner?.NickName ?? "?");
                if (owner != null && e is IGH_Component ec && ec.Params.Output.Any(p => p.Recipients.Count > 0))
                    toLichen.Add(owner.InstanceGuid);
            }

            var nodes = items.Select(o => ReadNode(doc, o, prefixes, defaults, nicknameCounts, itemIds, endpointLabel)).ToList();

            var outlineGroups = new List<OutlineGroup>();
            foreach (var g in groups)
            {
                var bounds = g.Attributes?.Bounds ?? default;
                var og = new OutlineGroup
                {
                    Id = g.InstanceGuid.ToString("D"),
                    ShortId = prefixes.Shortest(g.InstanceGuid),
                    Name = g.NickName,
                    Colour = ParseHelpers.ColorToHex(g.Colour),
                    X = bounds.X,
                    Y = bounds.Y,
                    ToLichen = toLichen.Contains(g.InstanceGuid)
                };
                foreach (var m in g.ObjectIDs)
                {
                    if (itemIds.Contains(m)) og.Members.Add(m.ToString("D"));
                    else if (groupIds.Contains(m) && m != g.InstanceGuid) og.Children.Add(m.ToString("D"));
                }

                if (LichenThallus.IsThallus(g) && LichenThallus.TryReadMetadata(g, out var description, out var properties))
                {
                    og.IsThallus = true;
                    og.Description = description;
                    og.Properties.AddRange(properties);
                }
                outlineGroups.Add(og);
            }

            return new OutlineModel(doc.DisplayName, nodes, outlineGroups);
        }

        private static OutlineNode ReadNode(GH_Document doc, IGH_DocumentObject obj, GuidPrefixIndex prefixes,
            Dictionary<Guid, string> defaults, Dictionary<string, int> nicknameCounts, HashSet<Guid> itemIds,
            Dictionary<Guid, string> endpointLabel)
        {
            var node = new OutlineNode
            {
                Id = obj.InstanceGuid.ToString("D"),
                ShortId = prefixes.Shortest(obj.InstanceGuid),
                Type = obj.Name,
                X = obj.Attributes.Pivot.X,
                Y = obj.Attributes.Pivot.Y
            };

            var nick = obj.NickName;
            if (!string.IsNullOrWhiteSpace(nick) && nick != obj.Name
                && !(defaults.TryGetValue(obj.ComponentGuid, out var standard) && nick == standard))
            {
                node.Nickname = nick;
                node.NicknameUnique = nicknameCounts.TryGetValue(nick, out var n) && n == 1
                    && !(BatchRefRegistry.TryGet(doc.DocumentID, nick, out var refId) && refId != obj.InstanceGuid);
            }

            switch (obj)
            {
                case GH_NumberSlider slider:
                    node.Kind = OutlineKind.Slider;
                    node.Value = $"{Number(slider.CurrentValue)} ({Number(slider.Slider.Minimum)}..{Number(slider.Slider.Maximum)})";
                    break;
                case GH_Panel panel:
                    node.Kind = OutlineKind.Panel;
                    if (panel.SourceCount == 0) node.Value = PanelText(panel.UserText);
                    break;
                case GH_BooleanToggle toggle:
                    node.Kind = OutlineKind.Toggle;
                    node.Value = toggle.Value ? "true" : "false";
                    break;
                case GH_ValueList list:
                    node.Kind = OutlineKind.ValueList;
                    node.Value = list.FirstSelectedItem?.Name ?? "(none)";
                    break;
                case IGH_Component _:
                    node.Kind = OutlineKind.Component;
                    break;
                case IGH_Param param:
                    node.Kind = OutlineKind.Parameter;
                    if (param.SourceCount == 0) node.Value = Stored(param);
                    break;
            }

            bool isComponent = obj is IGH_Component;
            foreach (var input in Tools.Batch.BatchItem.LiveInputs(obj))
            {
                var port = new OutlinePort { Name = input.NickName, Modifiers = Modifiers(input) };
                foreach (var s in input.Sources)
                {
                    var owner = s.Attributes?.GetTopLevel?.DocObject;
                    if (owner == null) continue;
                    if (endpointLabel.TryGetValue(owner.InstanceGuid, out var label))
                        port.Sources.Add(new OutlineSource(null, null, label));
                    else if (itemIds.Contains(owner.InstanceGuid))
                        port.Sources.Add(new OutlineSource(owner.InstanceGuid.ToString("D"), owner is IGH_Component ? s.NickName : null));
                }
                if (isComponent && input.SourceCount == 0)
                    port.Stored = Stored(input);
                node.Inputs.Add(port);
            }

            foreach (var output in Tools.Batch.BatchItem.LiveOutputs(obj))
            {
                var o = new OutlineOutput { Name = output.NickName, Items = output.VolatileDataCount, Modifiers = Modifiers(output) };
                foreach (var r in output.Recipients)
                {
                    var owner = r.Attributes?.GetTopLevel?.DocObject;
                    if (owner != null && itemIds.Contains(owner.InstanceGuid))
                        o.Recipients.Add(owner.InstanceGuid.ToString("D"));
                }
                node.Outputs.Add(o);
            }

            if (obj is IGH_ActiveObject active)
            {
                // A remark's level number (255) is above an error's: compare by meaning, not number.
                var level = active.RuntimeMessageLevel;
                if (MessageLevels.IsProblem((int)level))
                {
                    node.Level = MessageLevels.Name((int)level);
                    node.Message = active.RuntimeMessages(level).FirstOrDefault() ?? "";
                }
                node.Remark = MessageLevels.RemarkNote(active.RuntimeMessages(GH_RuntimeMessageLevel.Remark));
            }
            return node;
        }

        private static string Modifiers(IGH_Param p)
        {
            var list = new List<string>();
            if (p.DataMapping == GH_DataMapping.Graft) list.Add("graft");
            if (p.DataMapping == GH_DataMapping.Flatten) list.Add("flatten");
            if (p.Simplify) list.Add("simplify");
            if (p.Reverse) list.Add("reverse");
            return list.Count == 0 ? null : string.Join(", ", list);
        }

        /// <summary>What an unwired input or parameter holds: a few short values, or a count.</summary>
        private static string Stored(IGH_Param p)
        {
            var data = p.GetType().GetProperty("PersistentData")?.GetValue(p) as IGH_Structure;
            if (data == null || data.DataCount == 0) return null;

            var goo = data.AllData(true).ToList();
            if (goo.Count == 0) return null;
            if (goo.All(g => g is IGH_GeometricGoo gg && gg.IsReferencedGeometry))
                return $"{goo.Count} × {p.TypeName} from Rhino";
            if (goo.Count <= 3 && !goo.Any(g => g is IGH_GeometricGoo))
            {
                var text = string.Join(", ", goo.Select(g => g?.ToString() ?? "null"));
                if (text.Length <= 40) return text;
            }
            return $"{goo.Count} × {p.TypeName} stored";
        }

        private static string PanelText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 1) return $"\"{CanvasOutline.Clip(text, 60)}\"";
            return $"\"{CanvasOutline.Clip(string.Join(" | ", lines), 60)}\" ({lines.Length} lines)";
        }

        private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

        // Default nicknames by component id, so a renamed object can be told from an untouched one.
        private static Dictionary<Guid, string> _defaults;
        private static int _defaultsCount = -1;

        private static Dictionary<Guid, string> DefaultNicknames()
        {
            var proxies = Instances.ComponentServer.ObjectProxies;
            if (_defaults != null && _defaultsCount == proxies.Count)
                return _defaults;

            var d = new Dictionary<Guid, string>();
            foreach (var p in proxies)
            {
                if (!d.ContainsKey(p.Guid)) d[p.Guid] = p.Desc.NickName;
            }
            _defaults = d;
            _defaultsCount = proxies.Count;
            return d;
        }
    }
}
