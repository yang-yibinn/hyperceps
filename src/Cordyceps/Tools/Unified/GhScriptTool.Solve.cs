using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cordyceps.Core;
using Grasshopper.Kernel;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: gh_script(action='set'|'configure', solve=true). After the code is written and the
    /// component rebuilt, solve once and add what the script produced (<c>results</c>, in the shape of
    /// gh_inspect(action='outputs')), its errors and warnings (<c>messages</c>) and the solve time, so a
    /// checking script takes one call per round instead of set + outputs. Without <c>solve</c> nothing
    /// changes. Never solves inside a cluster editor (that can break the cluster's input hooks) or while
    /// the user has the solver off; the answer says why.
    /// </summary>
    public partial class GhScriptTool
    {
        private const int MaxPreviewItems = 10;
        private const int MaxMessages = 10;
        private const int MaxMessageLength = 300;

        private static void AddSolveResults(Dictionary<string, object> response, IGH_DocumentObject component)
        {
            var doc = component.OnPingDocument();
            if (doc == null)
            {
                response["solved"] = false;
                response["notSolved"] = "the component isn't in a document";
                return;
            }
            if (doc.Owner != null)
            {
                response["solved"] = false;
                response["notSolved"] = "inside a cluster editor, where solving from here can break the cluster's input hooks; the script runs on the main canvas's next solve";
                return;
            }
            if (!doc.Enabled || !GH_Document.EnableSolutions)
            {
                response["solved"] = false;
                response["notSolved"] = "the solver is off, and it stays off; turn it on with gh_document(action='solver', enabled=true) if the user agrees";
                return;
            }

            var clock = Stopwatch.StartNew();
            doc.NewSolution(false);
            response["solved"] = true;
            response["solveMs"] = clock.ElapsedMilliseconds;

            var results = new List<object>();
            if (component is IGH_Component comp)
            {
                foreach (var output in comp.Params.Output)
                {
                    results.Add(new
                    {
                        name = output.Name,
                        type = output.TypeName,
                        count = output.VolatileDataCount,
                        branches = output.VolatileData.PathCount,
                        preview = output.VolatileData.AllData(true).Take(MaxPreviewItems).Select(d => d?.ToString() ?? "null").ToList(),
                        truncated = output.VolatileDataCount > MaxPreviewItems
                    });
                }
            }
            response["results"] = results;

            var messages = new List<object>();
            if (component is IGH_ActiveObject active)
            {
                foreach (var (level, name) in new[] { (GH_RuntimeMessageLevel.Error, "error"), (GH_RuntimeMessageLevel.Warning, "warning"), (GH_RuntimeMessageLevel.Remark, "remark") })
                {
                    foreach (var text in active.RuntimeMessages(level))
                    {
                        if (messages.Count >= MaxMessages) break;
                        var clipped = text.Length > MaxMessageLength ? text.Substring(0, MaxMessageLength - 1) + "…" : text;
                        messages.Add(new { level = name, text = clipped });
                    }
                }
            }
            response["messages"] = messages;
        }
    }
}
