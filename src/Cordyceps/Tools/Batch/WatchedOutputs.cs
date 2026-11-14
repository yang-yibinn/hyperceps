using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core.Sweep;
using Grasshopper.Kernel;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// Hyperceps: outputs named for reading back, shared by gh_document(action='sweep') and gh_batch's
    /// <c>options.watch</c>. A name is an object (all its outputs), <c>obj.port</c>, or a ref, by
    /// gh_inspect(action='tree')'s rules; each is read as item count, how many of them are empty,
    /// branch count, a short preview of up to three text, number or true/false values, and whether
    /// its owner has solved.
    /// </summary>
    internal static class WatchedOutputs
    {
        /// <summary>Each name as outputs; <paramref name="pathOf"/> gives the error path for name i.</summary>
        public static List<(string Name, IGH_Param Param)> Resolve(BatchCanvas canvas, IReadOnlyList<string> names,
            Func<int, string> pathOf, List<string> errors)
        {
            var result = new List<(string, IGH_Param)>();
            for (int i = 0; i < names.Count; i++)
            {
                var path = pathOf(i);
                var (w, port) = canvas.ResolveObjectOrPort(names[i], path, errors);
                if (w == null || w.Failed) continue; // a failed new object already has its own error
                if (w.Deleted)
                {
                    errors.Add($"{path}: {w.Label} is deleted in this batch");
                    continue;
                }

                if (port != null)
                {
                    var p = canvas.ResolvePort(w, port, false, path, errors);
                    if (p != null) result.Add((names[i], p));
                    continue;
                }
                if (w.Outputs.Count == 0)
                {
                    errors.Add($"{path}: {w.Label} has no outputs");
                    continue;
                }
                foreach (var output in w.Outputs)
                    result.Add((w.Obj is IGH_Component ? $"{names[i]}.{output.NickName}" : names[i], output));
            }
            return result;
        }

        public static SweepWatchResult Read(string name, IGH_Param param)
        {
            var data = param.VolatileData;
            var w = new SweepWatchResult { Name = name, Items = data.DataCount, Branches = data.PathCount };
            if (data.DataCount > 0)
                w.Empty = data.DataCount - data.AllData(true).Count();
            var owner = param.Attributes?.GetTopLevel?.DocObject as IGH_ActiveObject;
            if (owner != null && owner.Phase != GH_SolutionPhase.Computed)
                w.Solved = false;
            if (data.DataCount > 0 && data.DataCount <= 3)
                w.Preview = SweepPlan.Preview(data.AllData(true).Select(g => g?.ScriptVariable()).ToList());
            return w;
        }
    }
}
