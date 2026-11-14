using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Cordyceps.Core.Sweep;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// Runs one batch on the Rhino UI thread: plan (phase 1, no changes), apply (phase 2, one
    /// undo record, solver paused), then solve once. Any failure while applying is rolled back by
    /// undoing the batch's own record, so the document keeps its file name and undo history.
    /// </summary>
    internal static class BatchRunner
    {
        private static bool _documentHook;

        public static string Run(GH_Document doc, BatchSpec spec)
        {
            EnsureDocumentHook();
            // ms.apply covers checking and applying (everything but the solve), as the spec's limits do.
            var clock = Stopwatch.StartNew();

            BatchPlan plan;
            try
            {
                plan = new BatchPlanner(doc, spec).Plan();
            }
            catch (Exception ex) // prawduct:allow prawduct/broad-except -- planning boundary: a planner fault must come back as a batch error; nothing has changed yet
            {
                DebugLog.Error($"gh_batch planning failed: {ex}");
                var failed = new BatchReport();
                failed.Errors.Add($"internal error while checking the batch: {ex.Message}. Nothing was changed");
                return failed.ToJson();
            }

            var report = plan.Report;

            if (spec.Options.DryRun)
            {
                report.DryRun = true;
                report.Created.Clear();
                report.Existing.AddRange(plan.Canvas.ExistingNamed);
                Log(spec, report, "dry run");
                return report.ToJson();
            }

            report.Resolved.Clear();

            if (!report.Ok && spec.Options.Atomic)
            {
                Log(spec, report, "rejected");
                return report.ToJson();
            }

            if (!plan.HasChanges)
            {
                if (report.Ok) report.Warnings.Add("nothing to change");
                ReadWatch(plan);
                Log(spec, report, "nothing to apply");
                return report.ToJson();
            }

            return Apply(doc, spec, plan, clock);
        }

        private static string Apply(GH_Document doc, BatchSpec spec, BatchPlan plan, Stopwatch clock)
        {
            var report = plan.Report;
            var before = RuntimeSnapshot(doc);
            bool solverWasOn = doc.Enabled;
            var record = new GH_UndoRecord(UndoName(report.Counts));
            // The time limit covers checking and applying together: applying gets what checking left
            // (possibly nothing, in which case the first change is applied and then rolled back).
            var remaining = spec.Options.TimeoutSeconds - clock.Elapsed.TotalSeconds;
            var ctx = new ApplyContext(doc, record, remaining, plan.NewObjects, plan.NewParams);

            try
            {
                // Paused, not just "not asked to solve": a slider or value list change starts a solve
                // from its own change handler, and a solve mid-batch would see a half-built canvas.
                doc.Enabled = false;
                foreach (var op in plan.InOrder())
                {
                    op.Apply(ctx);
                    ctx.CheckTime();
                }
                foreach (var obj in ctx.ToExpire.Values)
                {
                    if (obj is IGH_ActiveObject active) active.ExpireSolution(false);
                }
            }
            catch (Exception ex) // prawduct:allow prawduct/broad-except -- apply boundary: whatever failed, the batch's own record is undone so the canvas is left as it was
            {
                var rollbackError = Rollback(doc, record, ctx);
                doc.Enabled = solverWasOn;
                if (solverWasOn && GH_Document.EnableSolutions)
                    doc.NewSolution(false);

                report.Changed = rollbackError != null;
                report.RolledBack = rollbackError == null;
                report.Errors.Add(ex is BatchTimeoutException
                    ? $"{ex.Message} (options.timeoutSeconds = {spec.Options.TimeoutSeconds:0.#}); everything was rolled back. Split the batch into smaller blocks"
                    : $"applying failed ({ex.Message}); everything was rolled back");
                if (rollbackError != null)
                    report.Errors.Add($"rollback did not complete ({rollbackError}). Check the canvas; Ctrl+Z may not cover this batch");
                DebugLog.Error($"gh_batch apply failed: {ex}");
                Log(spec, report, "rolled back");
                Instances.ActiveCanvas?.Invalidate();
                return report.ToJson();
            }
            finally
            {
                if (!doc.Enabled && solverWasOn) doc.Enabled = true;
            }

            doc.UndoServer.PushUndoRecord(record);
            report.Changed = true;
            report.ApplyMs = clock.ElapsedMilliseconds;

            var prefixes = new GuidPrefixIndex(doc.Objects.Select(o => o.InstanceGuid));
            foreach (var item in plan.Created)
            {
                BatchRefRegistry.Set(doc.DocumentID, item.Ref, item.Obj.InstanceGuid);
                report.Created.Add(new KeyValuePair<string, string>(item.Ref, prefixes.Shortest(item.Obj.InstanceGuid)));
            }

            Solve(doc, spec, plan, ctx, before, solverWasOn);
            ReadWatch(plan);
            Instances.ActiveCanvas?.Invalidate();
            Log(spec, report, "applied");
            return report.ToJson();
        }

        private static string Rollback(GH_Document doc, GH_UndoRecord record, ApplyContext ctx)
        {
            string error = null;
            try
            {
                record.Undo(doc);
            }
            catch (Exception ex) // prawduct:allow prawduct/broad-except -- rollback boundary: report a rollback that could not finish rather than masking the original failure
            {
                error = ex.Message;
                DebugLog.Error($"gh_batch rollback failed: {ex}");
            }

            foreach (var obj in ctx.AddedUnrecorded)
            {
                if (doc.Objects.Contains(obj)) doc.RemoveObject(obj, false);
            }
            return error;
        }

        private static void Solve(GH_Document doc, BatchSpec spec, BatchPlan plan, ApplyContext ctx,
            Dictionary<Guid, string> before, bool solverWasOn)
        {
            var report = plan.Report;
            if (!spec.Options.Solve)
            {
                report.Solved = false;
                return;
            }

            if (!solverWasOn || !GH_Document.EnableSolutions)
            {
                report.Solved = false;
                report.Warnings.Add("the solver is off, so the batch was applied but not solved; it stays off. Turn it on with gh_document(action='solver', enabled=true) to see results");
                return;
            }

            var clock = Stopwatch.StartNew();
            doc.NewSolution(false);
            report.SolveMs = clock.ElapsedMilliseconds;
            report.Solved = true;

            if (spec.Options.ReturnRuntimeMessages)
                CollectRuntime(plan, ctx, before);
        }

        /// <summary>options.watch: each output's count, branches and short preview, after the solve.</summary>
        private static void ReadWatch(BatchPlan plan)
        {
            foreach (var (name, param) in plan.Watch)
                plan.Report.Watch.Add(SweepPlan.WatchText(WatchedOutputs.Read(name, param)));
        }

        /// <summary>Message state of every existing object, to spot what the batch changed downstream.</summary>
        private static Dictionary<Guid, string> RuntimeSnapshot(GH_Document doc)
        {
            var map = new Dictionary<Guid, string>();
            foreach (var obj in doc.Objects)
            {
                if (obj is IGH_ActiveObject a)
                    map[obj.InstanceGuid] = Signature(a);
            }
            return map;
        }

        private static string Signature(IGH_ActiveObject a) =>
            a.RuntimeMessageLevel + "|" + string.Join("\n", a.RuntimeMessages(GH_RuntimeMessageLevel.Error))
            + "|" + string.Join("\n", a.RuntimeMessages(GH_RuntimeMessageLevel.Warning));

        /// <summary>
        /// Errors and warnings from new objects, and from existing objects downstream of what the
        /// batch touched whose messages changed. Errors first.
        /// </summary>
        private static void CollectRuntime(BatchPlan plan, ApplyContext ctx, Dictionary<Guid, string> before)
        {
            var notes = new List<(int Rank, RuntimeNote Note)>();
            var refs = plan.Created.ToDictionary(i => i.Obj.InstanceGuid, i => i.Ref);

            foreach (var item in plan.Created)
                Add(notes, item.Obj, item.Ref, null);

            var seen = new HashSet<Guid>();
            var queue = new Queue<IGH_DocumentObject>(ctx.ToExpire.Values);
            while (queue.Count > 0 && seen.Count < 5000)
            {
                var obj = queue.Dequeue();
                if (!seen.Add(obj.InstanceGuid)) continue;

                if (!refs.ContainsKey(obj.InstanceGuid) && obj is IGH_ActiveObject a
                    && before.TryGetValue(obj.InstanceGuid, out var was) && was != Signature(a))
                    Add(notes, obj, null, plan.Canvas.ExistingLabel(obj));

                foreach (var output in BatchItem.LiveOutputs(obj))
                {
                    foreach (var r in output.Recipients)
                        queue.Enqueue(ApplyContext.Owner(r));
                }
            }

            foreach (var n in notes.OrderBy(n => n.Rank))
                plan.Report.Runtime.Add(n.Note);
        }

        private static void Add(List<(int, RuntimeNote)> notes, IGH_DocumentObject obj, string reference, string label)
        {
            if (!(obj is IGH_ActiveObject a)) return;
            foreach (var (level, rank) in new[] { (GH_RuntimeMessageLevel.Error, 0), (GH_RuntimeMessageLevel.Warning, 1) })
            {
                foreach (var message in a.RuntimeMessages(level))
                    notes.Add((rank, new RuntimeNote { Ref = reference, Obj = label, Level = level.ToString(), Message = message }));
            }
        }

        private static string UndoName(BatchCounts c)
        {
            var parts = new List<string>();
            if (c.Components > 0) parts.Add($"{c.Components} new");
            if (c.Wires + c.Inserts > 0) parts.Add($"{c.Wires + c.Inserts} wires");
            if (c.Set > 0) parts.Add($"{c.Set} set");
            if (c.Deleted > 0) parts.Add($"{c.Deleted} deleted");
            return "Hyperceps batch" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "");
        }

        /// <summary>One line per batch in the Cordyceps log (gh_inspect action='log'), plus every error.</summary>
        private static void Log(BatchSpec spec, BatchReport report, string outcome)
        {
            var c = report.Counts;
            var created = string.Join(", ", report.Created.Take(30).Select(kv => $"{kv.Key}={kv.Value}"));
            DebugLog.Info($"gh_batch {outcome}: {spec.OperationCount} entries; components {c.Components}, set {c.Set}, inserts {c.Inserts}, "
                + $"wires {c.Wires}, disconnected {c.Disconnected}, deleted {c.Deleted}, groups {c.Groups}; "
                + $"errors {report.Errors.Count}, warnings {report.Warnings.Count}"
                + (report.ApplyMs.HasValue ? $"; apply {report.ApplyMs} ms" : "")
                + (report.SolveMs.HasValue ? $", solve {report.SolveMs} ms" : "")
                + (created.Length > 0 ? $"; created {created}" : ""));
            foreach (var e in report.Errors.Take(50))
                DebugLog.Warn($"gh_batch error: {e}");
        }

        /// <summary>Refs are per document and end with it: drop them when Grasshopper closes the document.</summary>
        private static void EnsureDocumentHook()
        {
            if (_documentHook) return;
            Instances.DocumentServer.DocumentRemoved += (server, removed) =>
            {
                if (removed != null) BatchRefRegistry.Clear(removed.DocumentID);
            };
            _documentHook = true;
        }
    }
}
