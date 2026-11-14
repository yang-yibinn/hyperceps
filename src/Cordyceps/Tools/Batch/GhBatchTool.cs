using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// gh_batch: many canvas edits in one call — validated first, applied as one undo step, solved
    /// once, answered with a short summary. The contract is <c>docs/gh-batch-schema.md</c>; the
    /// schema AI apps see is the embedded <c>GhBatchSchema.json</c>.
    /// </summary>
    [McpServerToolType]
    public class GhBatchTool
    {
        private const int WaitStepMs = 100;
        private readonly GrasshopperContext _context;

        public GhBatchTool(GrasshopperContext context)
        {
            _context = context;
        }

        [McpServerTool, McpInputSchema("Cordyceps.Tools.Batch.GhBatchSchema.json")]
        [Description("Apply many Grasshopper canvas edits in ONE call. Use it whenever a request needs two or more changes; build large work one block per batch and check each block before the next. "
            + "Create components (exact name, 'Category/Name' or GUID; or panel/slider/valueList/toggle/param shortcuts), set values, insert components into existing wires, add and remove wires, delete, and group. "
            + "Order: delete, disconnect, create, set, insert, wires, groups, then one solve. Everything is checked first and all errors come back at once; then the batch is applied as one undo step with the solver paused, and solved once. "
            + "Entries name each other by ref; refs keep working in later batches. Name existing objects by nickname or GUID prefix. A port is 'obj.port' (index, name or nickname). "
            + "Graft/flatten/simplify/reverse are port settings (inputModifiers/outputModifiers), not components; a list of values is one panel with one value per line, not a Text Split. "
            + "Leave out x/y for automatic left-to-right layout. Never rename native components; give capitalised nicknames to block-edge parameters and input panels/sliders. "
            + "options.watch reads outputs back after the solve (count, branches, short preview), so a block is built and checked in one call; check tree structure with gh_inspect(action='tree'). A batch with errors changes nothing, so send the real batch; use options.dryRun only to try a few guessed names first.")]
        public string GhBatch(
            [Description("Objects to create")] string components = null,
            [Description("Values and settings to change")] string set = null,
            [Description("New components to put into existing wires")] string insert = null,
            [Description("Connections to add, [output, input]")] string wires = null,
            [Description("Connections to remove, [output, input]")] string disconnect = null,
            [Description("Objects to delete")] string delete = null,
            [Description("Groups to create or extend")] string groups = null,
            [Description("atomic, dryRun, solve, returnRuntimeMessages, maxOperations, timeoutSeconds, watch")] string options = null)
        {
            var errors = new List<string>();
            var spec = BatchSpecParser.Parse(new BatchRawInput
            {
                Components = components,
                Set = set,
                Insert = insert,
                Wires = wires,
                Disconnect = disconnect,
                Delete = delete,
                Groups = groups,
                Options = options
            }, errors);

            if (errors.Count > 0)
                return Failure(errors);
            if (spec.IsEmpty)
                return Failure("the batch is empty: give at least one of components, set, insert, wires, disconnect, delete or groups"
                    + (spec.Options.Watch.Count > 0 ? ". To only read outputs, use gh_inspect(action='tree')" : ""));
            if (spec.OperationCount > spec.Options.MaxOperations)
                return Failure($"the batch has {spec.OperationCount} entries; the limit is {spec.Options.MaxOperations} "
                    + $"(options.maxOperations, at most {BatchOptions.MaxOperationsCeiling}). Split it into blocks");

            // Never edit during a solve: wait (off the UI thread) for a running one to finish, up to
            // the batch's timeout and at most HostBusyGate.MaxWait, so the answer arrives before the
            // AI app gives up on the call. A modal dialog only a person can close is reported, not waited on.
            var deadline = TimeSpan.FromSeconds(Math.Min(spec.Options.TimeoutSeconds, HostBusyGate.MaxWait.TotalSeconds));
            var waited = Stopwatch.StartNew();
            var dialog = new DialogCheck(); // the end of a long solve reads as a dialog for a moment
            while (true)
            {
                var live = SolverState.Shared.Derive();
                if (dialog.Confirmed(live.ModalInferred, DateTime.UtcNow))
                    return StatusEnvelope.BusyResult(live);

                if (!live.Solving && !live.ModalInferred)
                {
                    string result;
                    try
                    {
                        result = _context.ExecuteOnUiThread(() =>
                        {
                            if (!ToolHelpers.TryGetActiveDocument(_context, out var doc, out var error))
                                return Failure(error);
                            // The UI thread can be reached mid-solve (Grasshopper pumps messages while
                            // solving); come back once the solution has finished.
                            if (doc.SolutionState == GH_ProcessStep.Process)
                                return null;
                            return BatchRunner.Run(doc, spec);
                        });
                    }
                    catch (HostBusyException ex)
                    {
                        return Failure(ex.Message + " Nothing was changed");
                    }
                    if (result != null)
                        return result;
                }

                if (waited.Elapsed > deadline)
                    return Failure(HostBusyGate.Message(live, DateTime.UtcNow) + " Nothing was changed");
                Thread.Sleep(WaitStepMs);
            }
        }

        private static string Failure(string error) => Failure(new List<string> { error });

        private static string Failure(List<string> errors)
        {
            var report = new BatchReport();
            report.Errors.AddRange(errors);
            return report.ToJson();
        }
    }
}
