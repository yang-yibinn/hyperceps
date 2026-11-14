using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Tools.Batch;
using Grasshopper.Kernel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: gh_inspect(action='tree') — branch paths and item counts for any output, read
    /// live from the canvas. Objects are named the gh_batch way (ref, unique nickname, GUID, GUID
    /// prefix; optionally <c>obj.port</c>), several at once as a JSON list, so an agent can check
    /// every output a batch affected in one call.
    /// </summary>
    public partial class GhInspectTool
    {
        private const int MaxTreeTargets = 50;

        private string ActionTree(string id, int limit)
        {
            // Reading volatile data needs the UI thread; answer at once if it is blocked.
            var liveness = SolverState.Shared.Derive();
            if (liveness.Ui == UiLiveness.Blocked)
                return StatusEnvelope.BusyResult(liveness);

            var targets = ParseTreeTargets(id, out var parseError);
            if (targets == null)
                return ToolHelpers.ErrorResponse(parseError);

            // 'limit' defaults to 100 for the log action; tree lists 20 path groups unless asked.
            int maxRuns = limit != 100 && limit > 0 ? limit : TreeSummary.DefaultMaxRuns;

            return _context.ExecuteOnUiThread(() =>
            {
                if (!ToolHelpers.TryGetActiveDocument(_context, out var doc, out var error))
                    return ToolHelpers.ErrorResponse(error);

                var canvas = new BatchCanvas(doc);
                var errors = new List<string>();
                var trees = new JArray();

                for (int i = 0; i < targets.Count; i++)
                {
                    var path = targets.Count == 1 ? "id" : $"id[{i}]";
                    var (item, port) = canvas.ResolveObjectOrPort(targets[i], path, errors);
                    if (item == null) continue;

                    IEnumerable<IGH_Param> outputs;
                    if (port == null)
                    {
                        outputs = item.Outputs;
                        if (!outputs.Any())
                        {
                            errors.Add($"{path}: {item.Label} has no outputs");
                            continue;
                        }
                    }
                    else
                    {
                        var p = canvas.ResolvePort(item, port, false, path, errors);
                        if (p == null) continue;
                        outputs = new[] { p };
                    }

                    foreach (var output in outputs)
                        trees.Add(DescribeTree(targets[i], item, output, maxRuns));
                }

                var result = new JObject
                {
                    ["success"] = errors.Count == 0,
                    ["trees"] = trees
                };
                if (errors.Count > 0)
                    result["errors"] = new JArray(errors);
                return result.ToString(Formatting.None);
            });
        }

        private static JObject DescribeTree(string target, BatchItem item, IGH_Param output, int maxRuns)
        {
            var data = output.VolatileData;
            var branches = new List<TreeBranch>(data.PathCount);
            for (int b = 0; b < data.PathCount; b++)
            {
                var list = data.get_Branch(b);
                int nulls = 0;
                foreach (var x in list)
                    if (x == null) nulls++;
                branches.Add(new TreeBranch(data.get_Path(b).Indices, list.Count, nulls));
            }

            var tree = new JObject
            {
                ["target"] = target,
                ["object"] = item.Label
            };
            if (item.Obj is IGH_Component)
                tree["output"] = output.NickName == output.Name ? output.Name : $"{output.NickName} ({output.Name})";
            tree["type"] = output.TypeName;

            var owner = item.Obj as IGH_ActiveObject;
            if (owner != null && owner.Phase != GH_SolutionPhase.Computed)
                tree["solved"] = false;

            foreach (var kv in TreeSummary.Describe(branches, maxRuns))
                tree[kv.Key] = kv.Value;

            // An error or warning on the object usually explains an empty or odd tree.
            if (owner != null && MessageLevels.IsProblem((int)owner.RuntimeMessageLevel))
            {
                var level = owner.RuntimeMessageLevel;
                var first = owner.RuntimeMessages(level).FirstOrDefault();
                if (first != null)
                    tree["message"] = $"{level}: {(first.Length > 160 ? first.Substring(0, 159) + "…" : first)}";
            }

            if (owner != null && owner.Phase != GH_SolutionPhase.Computed)
                tree["note"] = "not solved yet, so the tree may be empty or out of date. Is the solver on?";
            return tree;
        }

        /// <summary>One name, or a JSON list of names (each an object or 'obj.port').</summary>
        private static List<string> ParseTreeTargets(string id, out string error)
        {
            error = null;
            var text = (id ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "tree needs 'id': an object (ref, nickname, GUID or GUID prefix), 'obj.port', or a JSON list of them";
                return null;
            }

            if (!text.StartsWith("["))
                return new List<string> { text };

            try
            {
                var list = JArray.Parse(text).Select(t => t.Type == JTokenType.String ? t.Value<string>().Trim() : null).ToList();
                if (list.Count == 0 || list.Any(string.IsNullOrEmpty))
                {
                    error = "'id' as a list must hold object names, e.g. [\"item.0\", \"PAIRS\"]";
                    return null;
                }
                if (list.Count > MaxTreeTargets)
                {
                    error = $"at most {MaxTreeTargets} outputs per call";
                    return null;
                }
                return list;
            }
            catch (JsonException ex)
            {
                error = $"'id' starts with '[' but isn't a JSON list: {ex.Message}";
                return null;
            }
        }
    }
}
