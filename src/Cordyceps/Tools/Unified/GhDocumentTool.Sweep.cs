using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Sweep;
using Cordyceps.Tools.Batch;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: gh_document(action='sweep') — move one number slider, value list or toggle through
    /// a list of values, solving at each, and report the watched outputs' item counts (with a short
    /// preview of text and number outputs), the canvas's errors and warnings, and the solve time.
    /// The control is always put back and solved again, also when something fails partway. Checks
    /// everything first; refuses while the solver is off. See <see cref="SweepPlan"/>.
    /// </summary>
    public partial class GhDocumentTool
    {
        private string ActionSweep(string id, string values, string watch)
        {
            var liveness = SolverState.Shared.Derive();
            if (liveness.Solving || liveness.ModalInferred)
                return StatusEnvelope.BusyResult(liveness);

            if (!TryParseWatch(watch, out var watchNames, out var watchError))
                return ToolHelpers.ErrorResponse(watchError);

            return _context.ExecuteOnUiThread(() =>
            {
                if (!ToolHelpers.TryGetActiveDocument(_context, out var doc, out var error))
                    return ToolHelpers.ErrorResponse(error);
                if (!doc.Enabled || !GH_Document.EnableSolutions)
                    return ToolHelpers.ErrorResponse("the solver is off, so sweep can't compute each value. It stays off: ask the user, or turn it on with gh_document(action='solver', enabled=true)");

                var canvas = new BatchCanvas(doc);
                var errors = new List<string>();
                var item = canvas.Resolve(id, "id", errors);
                if (item == null)
                    return ToolHelpers.ErrorResponse(string.Join("; ", errors));

                var control = ReadControl(item.Obj, canvas);
                if (control == null)
                    return ToolHelpers.ErrorResponse($"{item.Label} is a {item.Obj.Name}; sweep moves a number slider, a value list or a toggle");

                var watched = ResolveWatch(canvas, watchNames, errors);
                if (watched.Count > SweepPlan.MaxWatch)
                    errors.Add($"watch: at most {SweepPlan.MaxWatch} outputs (a component without a port counts each of its outputs)");

                var notes = new List<string>();
                var steps = SweepPlan.Plan(control, values, errors, notes);
                if (errors.Count > 0)
                    return ToolHelpers.ErrorResponse(string.Join("; ", errors));

                var infraIds = ToolHelpers.GetCordycepsInfrastructureIds(doc);
                var before = ProblemLevels(doc, infraIds);
                var original = Capture(item.Obj);
                var results = new JArray();
                string stopped = null, failure = null, restoreFailure = null;

                var clock = Stopwatch.StartNew();
                long lastMs = 0;
                try
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (i > 0 && clock.Elapsed.TotalSeconds + lastMs / 1000.0 > SweepPlan.TimeBudgetSeconds)
                        {
                            stopped = $"stopped after {i} of {steps.Count} values to stay within the time limit (about {SweepPlan.TimeBudgetSeconds:0} s per call); sweep the rest in another call";
                            break;
                        }

                        var r = new SweepStepResult { Label = steps[i].Label };
                        r.SolveMs = lastMs = SetAndSolve(doc, item.Obj, steps[i]);
                        if (item.Obj is GH_NumberSlider slider && slider.CurrentValue != steps[i].Number)
                            r.Label += $" (the slider took {SweepPlan.Number(slider.CurrentValue)})";
                        foreach (var (name, param) in watched)
                            r.Watch.Add(ReadWatch(name, param));
                        CountProblems(doc, infraIds, before, canvas, r);
                        results.Add(SweepPlan.Line(r));
                    }
                }
                catch (Exception ex) // prawduct:allow prawduct/broad-except -- a solve can throw anything; report it and still restore the control
                {
                    failure = $"sweep failed at value {results.Count + 1}: {ex.Message}";
                    DebugLog.Error($"gh_document sweep failed: {ex}");
                }
                finally
                {
                    try
                    {
                        Restore(doc, item.Obj, original);
                    }
                    catch (Exception ex) // prawduct:allow prawduct/broad-except -- report a control that could not be put back rather than hide it
                    {
                        restoreFailure = $"could not put {control.Name} back to {control.Current} ({ex.Message}); set it by hand";
                        DebugLog.Error($"gh_document sweep restore failed: {ex}");
                    }
                }
                Instances.ActiveCanvas?.Invalidate();

                if (watched.Count == 0)
                    notes.Add("nothing watched: pass watch='[\"OUTPUT NAME\", \"obj.port\"]' to see item counts at each value");

                var o = new JObject
                {
                    ["success"] = failure == null && restoreFailure == null,
                    ["control"] = control.Describe(),
                    ["results"] = results
                };
                if (restoreFailure == null) o["restored"] = $"{control.Name} = {control.Current}";
                if (stopped != null) o["stopped"] = stopped;
                if (notes.Count > 0) o["notes"] = new JArray(notes);
                var problems = new[] { failure, restoreFailure }.Where(e => e != null).ToList();
                if (problems.Count > 0) o["error"] = string.Join("; ", problems);

                DebugLog.Info($"gh_document sweep {control.Name}: {results.Count} of {steps.Count} values, {clock.ElapsedMilliseconds} ms{(failure != null ? ", failed" : "")}");
                return o.ToString(Formatting.None);
            });
        }

        private static bool TryParseWatch(string watch, out List<string> names, out string error)
        {
            names = new List<string>();
            error = null;
            var s = (watch ?? "").Trim();
            if (s.Length == 0) return true;
            if (!s.StartsWith("["))
            {
                names.Add(s);
                return true;
            }
            try
            {
                names = JArray.Parse(s).Select(t => t.Type == JTokenType.String ? t.Value<string>().Trim() : null).ToList();
                if (names.Any(string.IsNullOrEmpty))
                {
                    error = "'watch' as a list must hold output names, e.g. [\"TOP BRACING\", \"chk.out\"]";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = $"'watch' starts with '[' but isn't a JSON list: {ex.Message}";
                return false;
            }
        }

        /// <summary>Each watched name as outputs: an object's outputs, or 'obj.port' (gh_inspect tree's rules).</summary>
        private static List<(string Name, IGH_Param Param)> ResolveWatch(BatchCanvas canvas, List<string> names, List<string> errors)
        {
            var result = new List<(string, IGH_Param)>();
            for (int i = 0; i < names.Count; i++)
            {
                var path = names.Count == 1 ? "watch" : $"watch[{i}]";
                var (w, port) = canvas.ResolveObjectOrPort(names[i], path, errors);
                if (w == null) continue;

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

        private static SweepControl ReadControl(IGH_DocumentObject obj, BatchCanvas canvas)
        {
            var name = !string.IsNullOrEmpty(obj.NickName) && obj.NickName != obj.Name
                ? obj.NickName
                : $"{canvas.Prefix(obj.InstanceGuid)} ({obj.Name})";
            switch (obj)
            {
                case GH_NumberSlider slider:
                    return new SweepControl
                    {
                        Kind = SweepControlKind.Slider,
                        Name = name,
                        Minimum = slider.Slider.Minimum,
                        Maximum = slider.Slider.Maximum,
                        Decimals = slider.Slider.DecimalPlaces,
                        Current = SweepPlan.Number(slider.CurrentValue)
                    };
                case GH_ValueList list:
                    return new SweepControl
                    {
                        Kind = SweepControlKind.ValueList,
                        Name = name,
                        Items = list.ListItems.Select(i => i.Name).ToList(),
                        Current = list.SelectedItems.Count == 0 ? "(none)" : string.Join(", ", list.SelectedItems.Select(i => i.Name))
                    };
                case GH_BooleanToggle toggle:
                    return new SweepControl
                    {
                        Kind = SweepControlKind.Toggle,
                        Name = name,
                        Current = toggle.Value ? "true" : "false"
                    };
                default:
                    return null;
            }
        }

        /// <summary>The control's value before the sweep: slider value, value-list selection, or toggle state.</summary>
        private static object Capture(IGH_DocumentObject obj) => obj switch
        {
            GH_NumberSlider slider => slider.CurrentValue,
            GH_ValueList list => list.ListItems.Select(i => i.Selected).ToArray(),
            GH_BooleanToggle toggle => toggle.Value,
            _ => null
        };

        /// <summary>
        /// Set one value with the solver paused (as gh_batch does, so the change itself doesn't
        /// solve), then solve once. Returns the solve time in milliseconds.
        /// </summary>
        private static long SetAndSolve(GH_Document doc, IGH_DocumentObject obj, SweepStep step)
        {
            return Solve(doc, obj, () =>
            {
                switch (obj)
                {
                    case GH_NumberSlider slider:
                        slider.SetSliderValue(step.Number);
                        break;
                    case GH_ValueList list:
                        for (int i = 0; i < list.ListItems.Count; i++)
                            list.ListItems[i].Selected = i == step.Index;
                        break;
                    case GH_BooleanToggle toggle:
                        toggle.Value = step.Flag;
                        break;
                }
            });
        }

        private static void Restore(GH_Document doc, IGH_DocumentObject obj, object original)
        {
            Solve(doc, obj, () =>
            {
                switch (obj)
                {
                    case GH_NumberSlider slider when original is decimal value:
                        slider.SetSliderValue(value);
                        break;
                    case GH_ValueList list when original is bool[] selected:
                        for (int i = 0; i < list.ListItems.Count && i < selected.Length; i++)
                            list.ListItems[i].Selected = selected[i];
                        break;
                    case GH_BooleanToggle toggle when original is bool flag:
                        toggle.Value = flag;
                        break;
                }
            });
        }

        private static long Solve(GH_Document doc, IGH_DocumentObject obj, Action set)
        {
            doc.Enabled = false;
            try
            {
                set();
                obj.Attributes?.ExpireLayout();
                (obj as IGH_ActiveObject)?.ExpireSolution(false);
            }
            catch
            {
                doc.Enabled = true;
                throw;
            }

            var clock = Stopwatch.StartNew();
            doc.Enabled = true;
            doc.NewSolution(false);
            return clock.ElapsedMilliseconds;
        }

        private static SweepWatchResult ReadWatch(string name, IGH_Param param)
        {
            var data = param.VolatileData;
            var w = new SweepWatchResult { Name = name, Items = data.DataCount, Branches = data.PathCount };
            var owner = param.Attributes?.GetTopLevel?.DocObject as IGH_ActiveObject;
            if (owner != null && owner.Phase != GH_SolutionPhase.Computed)
                w.Solved = false;
            if (data.DataCount > 0 && data.DataCount <= 3)
                w.Preview = SweepPlan.Preview(data.AllData(true).Select(g => g?.ScriptVariable()).ToList());
            return w;
        }

        private static Dictionary<Guid, GH_RuntimeMessageLevel> ProblemLevels(GH_Document doc, HashSet<Guid> infraIds)
        {
            var map = new Dictionary<Guid, GH_RuntimeMessageLevel>();
            foreach (var obj in ToolHelpers.GetActiveObjects(doc, infraIds))
            {
                if (obj is IGH_ActiveObject a)
                    map[obj.InstanceGuid] = a.RuntimeMessageLevel;
            }
            return map;
        }

        private static void CountProblems(GH_Document doc, HashSet<Guid> infraIds, Dictionary<Guid, GH_RuntimeMessageLevel> before,
            BatchCanvas canvas, SweepStepResult r)
        {
            foreach (var obj in ToolHelpers.GetActiveObjects(doc, infraIds))
            {
                if (!(obj is IGH_ActiveObject a)) continue;
                var level = a.RuntimeMessageLevel;
                if (level == GH_RuntimeMessageLevel.Error) r.Errors++;
                else if (level == GH_RuntimeMessageLevel.Warning) r.Warnings++;
                else continue;

                if (before.TryGetValue(obj.InstanceGuid, out var was) && was >= level) continue;
                var message = a.RuntimeMessages(level).FirstOrDefault() ?? "";
                if (message.Length > 60) message = message.Substring(0, 59) + "…";
                r.NewProblems.Add($"{canvas.ExistingLabel(obj)} {(level == GH_RuntimeMessageLevel.Error ? "error" : "warning")}: {message}");
            }
        }
    }
}
