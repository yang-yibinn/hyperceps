using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cordyceps.Tools.Batch
{
    /// <summary>Phase 1's output: every change, resolved and checked, in the order phase 2 applies it.</summary>
    internal sealed class BatchPlan
    {
        public BatchReport Report { get; } = new BatchReport();
        public BatchCanvas Canvas { get; set; }
        public List<BatchItem> NewItems { get; } = new List<BatchItem>();
        public DeleteOp Delete { get; } = new DeleteOp { Path = "delete" };
        public List<BatchOp> Disconnects { get; } = new List<BatchOp>();
        public List<BatchOp> ExistingChanges { get; } = new List<BatchOp>();
        public List<BatchOp> Inserts { get; } = new List<BatchOp>();
        public List<BatchOp> Wires { get; } = new List<BatchOp>();
        public List<BatchOp> Groups { get; } = new List<BatchOp>();
        public HashSet<IGH_Param> NewParams { get; } = new HashSet<IGH_Param>();
        public HashSet<IGH_DocumentObject> NewObjects { get; } = new HashSet<IGH_DocumentObject>();
        /// <summary>options.watch, resolved: read back after the solve.</summary>
        public List<(string Name, IGH_Param Param)> Watch { get; } = new List<(string, IGH_Param)>();

        public IEnumerable<BatchItem> Created => NewItems.Where(i => !i.Failed && i.Obj != null);

        public IEnumerable<BatchOp> InOrder()
        {
            if (Delete.Objects.Count > 0) yield return Delete;
            foreach (var op in Disconnects) yield return op;
            foreach (var item in Created) yield return new AddOp { Path = item.Entry.Path, Object = item.Obj };
            foreach (var op in ExistingChanges) yield return op;
            foreach (var op in Inserts) yield return op;
            foreach (var op in Wires) yield return op;
            foreach (var op in Groups) yield return op;
        }

        public bool HasChanges => InOrder().Any();
    }

    /// <summary>
    /// Phase 1 of gh_batch: resolve and check every entry, in the spec's order (delete → disconnect
    /// → create → set → insert → wires → groups), and report every problem at once. New objects
    /// are built and configured in memory — including their variable ports — so their ports can be
    /// checked before anything touches the canvas. Nothing here changes the document.
    /// </summary>
    internal sealed class BatchPlanner
    {
        private readonly BatchSpec _spec;
        private readonly BatchPlan _plan = new BatchPlan();
        private readonly BatchCanvas _canvas;
        private readonly TypeCatalog _catalog;
        private readonly List<string> _errors;
        private readonly List<string> _warnings;

        // Wires as they will stand after earlier steps: removed by disconnect/insert, added by insert/wires.
        private readonly HashSet<(Guid, Guid)> _removed = new HashSet<(Guid, Guid)>();
        private readonly HashSet<(Guid, Guid)> _added = new HashSet<(Guid, Guid)>();

        private readonly List<(IGH_Param Param, string Path)> _valueTargets = new List<(IGH_Param, string)>();
        private readonly List<(BatchItem Item, string Setting)> _treeComponents = new List<(BatchItem, string)>();
        private readonly HashSet<string> _setAsideWarned = new HashSet<string>();
        private readonly Dictionary<BatchItem, int> _layoutIds = new Dictionary<BatchItem, int>();
        private readonly Dictionary<int, AutoLayout.Node> _layout = new Dictionary<int, AutoLayout.Node>();
        private readonly List<(int, int)> _edges = new List<(int, int)>();

        public BatchPlanner(GH_Document doc, BatchSpec spec)
        {
            _spec = spec;
            _canvas = new BatchCanvas(doc);
            _catalog = BatchCatalog.Get();
            _plan.Canvas = _canvas;
            _errors = _plan.Report.Errors;
            _warnings = _plan.Report.Warnings;
        }

        public BatchPlan Plan()
        {
            PlanDeletes();
            PlanDisconnects();
            PlanComponents();
            PlanSets();
            PlanInserts();
            PlanWires();
            PlanGroups();
            PlanWatch();
            PlanLayout();
            WarnWiredValues();
            HintTreeComponents();
            HintTextSplits();
            return _plan;
        }

        private static (Guid, Guid) Key(IGH_Param src, IGH_Param tgt) => (src.InstanceGuid, tgt.InstanceGuid);

        private bool WireExists(IGH_Param src, IGH_Param tgt) =>
            (tgt.Sources.Contains(src) && !_removed.Contains(Key(src, tgt))) || _added.Contains(Key(src, tgt));

        private bool IsNewParam(IGH_Param p) => _plan.NewParams.Contains(p);

        private string Port(BatchItem item, IGH_Param p, bool isInput) =>
            item.Obj is IGH_Component ? $"{item.Label} {(isInput ? "input" : "output")} '{p.NickName}'" : item.Label;

        private string SourcesOf(IGH_Param tgt)
        {
            var sources = tgt.Sources.Where(s => !_removed.Contains(Key(s, tgt))).ToList();
            if (sources.Count == 0) return "nothing is wired into it";
            return "it is fed by " + string.Join(", ", sources.Take(5).Select(s =>
            {
                var owner = ApplyContext.Owner(s);
                return owner is IGH_Component ? $"{_canvas.ExistingLabel(owner)} output '{s.NickName}'" : _canvas.ExistingLabel(owner);
            }));
        }

        // ---- delete -----------------------------------------------------------------------------

        private void PlanDeletes()
        {
            var batchRefs = new HashSet<string>(_spec.Components.Select(c => c.Ref).Where(r => r != null), StringComparer.Ordinal);
            var targets = new List<BatchItem>();

            foreach (var d in _spec.Delete)
            {
                if (batchRefs.Contains(d.Name) && _canvas.SessionRefObject(d.Name) == null)
                {
                    _errors.Add($"{d.Path}: '{d.Name}' is created in this batch; delete runs first, so it can't be deleted here");
                    continue;
                }

                var item = _canvas.Resolve(d.Name, d.Path, _errors, newAllowed: false);
                if (item == null) continue;
                if (LichenThallus.IsEndpoint(item.Obj))
                {
                    _errors.Add($"{d.Path}: {LichenThallus.EndpointRefusal(item.Label)}");
                    continue;
                }
                if (targets.Contains(item))
                {
                    _warnings.Add($"{d.Path}: {item.Label} is already being deleted");
                    continue;
                }
                targets.Add(item);
            }

            foreach (var item in targets)
            {
                item.Deleted = true;
                _plan.Delete.Objects.Add(item.Obj);
                foreach (var output in item.Outputs)
                {
                    foreach (var r in output.Recipients)
                        _removed.Add(Key(output, r));
                }
                foreach (var input in item.Inputs)
                {
                    foreach (var s in input.Sources)
                        _removed.Add(Key(s, input));
                }
            }
            _plan.Report.Counts.Deleted = targets.Count;
        }

        // ---- disconnect -------------------------------------------------------------------------

        private void PlanDisconnects()
        {
            foreach (var w in _spec.Disconnect)
            {
                int before = _errors.Count;
                var pairs = new List<(IGH_Param Src, IGH_Param Tgt)>();

                if (w.From.IsWildcard)
                {
                    var tgtItem = _canvas.Resolve(w.To.Obj, $"{w.Path}[1]", _errors, newAllowed: false);
                    var tgt = tgtItem == null ? null : _canvas.ResolvePort(tgtItem, w.To.Port, true, $"{w.Path}[1]", _errors);
                    if (tgt != null)
                    {
                        pairs.AddRange(tgt.Sources.Where(s => !_removed.Contains(Key(s, tgt)) && !IsInfra(s)).Select(s => (s, tgt)));
                        if (pairs.Count == 0) _warnings.Add($"{w.Path}: nothing is wired into {Port(tgtItem, tgt, true)}");
                    }
                }
                else if (w.To.IsWildcard)
                {
                    var srcItem = _canvas.Resolve(w.From.Obj, $"{w.Path}[0]", _errors, newAllowed: false);
                    var src = srcItem == null ? null : _canvas.ResolvePort(srcItem, w.From.Port, false, $"{w.Path}[0]", _errors);
                    if (src != null)
                    {
                        pairs.AddRange(src.Recipients.Where(r => !_removed.Contains(Key(src, r)) && !IsInfra(r)).Select(r => (src, r)));
                        if (pairs.Count == 0) _warnings.Add($"{w.Path}: nothing is wired out of {Port(srcItem, src, false)}");
                    }
                }
                else
                {
                    var srcItem = _canvas.Resolve(w.From.Obj, $"{w.Path}[0]", _errors, newAllowed: false);
                    var tgtItem = _canvas.Resolve(w.To.Obj, $"{w.Path}[1]", _errors, newAllowed: false);
                    var src = srcItem == null ? null : _canvas.ResolvePort(srcItem, w.From.Port, false, $"{w.Path}[0]", _errors);
                    var tgt = tgtItem == null ? null : _canvas.ResolvePort(tgtItem, w.To.Port, true, $"{w.Path}[1]", _errors);
                    if (src != null && tgt != null)
                    {
                        if (WireExists(src, tgt))
                            pairs.Add((src, tgt));
                        else
                            _errors.Add($"{w.Path}: there is no wire from {Port(srcItem, src, false)} to {Port(tgtItem, tgt, true)}; {SourcesOf(tgt)}");
                    }
                }

                if (_errors.Count > before) continue;
                foreach (var (src, tgt) in pairs)
                {
                    _removed.Add(Key(src, tgt));
                    _plan.Disconnects.Add(new DisconnectOp { Path = w.Path, Source = src, Target = tgt });
                }
                _plan.Report.Counts.Disconnected += pairs.Count;
            }
        }

        private bool IsInfra(IGH_Param p) => _canvas.InfraIds.Contains(ApplyContext.Owner(p).InstanceGuid);

        // ---- create -----------------------------------------------------------------------------

        private void PlanComponents()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in _spec.Components)
            {
                if (c.Ref == null) continue;
                int before = _errors.Count;

                if (!seen.Add(c.Ref))
                {
                    _errors.Add($"{c.Path}.ref: '{c.Ref}' is used twice in this batch");
                    continue;
                }

                // An earlier batch's ref is taken while its object lives; deleting that object in this
                // same batch (delete runs first) frees the name for the new one.
                var earlier = _canvas.SessionRefObject(c.Ref);
                if (earlier != null && !_plan.Delete.Objects.Contains(earlier))
                    _errors.Add($"{c.Path}.ref: '{c.Ref}' already names {_canvas.ExistingLabel(earlier)} from an earlier batch; pick a new ref, or use '{c.Ref}' to refer to that object");

                var holders = _canvas.NicknameHolders(c.Ref);
                if (holders.Count > 0)
                    _errors.Add($"{c.Path}.ref: '{c.Ref}' is also the nickname of {holders[0].Label}; pick another ref (refs are lowercase by convention)");

                var obj = Create(c);
                var item = new BatchItem
                {
                    Obj = obj,
                    IsNew = true,
                    Ref = c.Ref,
                    Entry = c,
                    Label = obj == null ? $"'{c.Ref}'" : $"'{c.Ref}' ({obj.Name})"
                };

                if (obj != null)
                {
                    obj.CreateAttributes();
                    Configure(item, c);
                    _plan.NewObjects.Add(obj);
                    foreach (var p in BatchItem.LiveInputs(obj).Concat(BatchItem.LiveOutputs(obj)))
                        _plan.NewParams.Add(p);

                    var setting = BatchCatalog.PortSettingFor(obj);
                    if (setting != null) _treeComponents.Add((item, setting));
                }

                item.Failed = obj == null || _errors.Count > before;
                _canvas.AddNew(item);
                _plan.NewItems.Add(item);
                if (!item.Failed)
                {
                    _plan.Report.Counts.Components++;
                    _plan.Report.Resolved.Add(new KeyValuePair<string, string>(c.Ref, BatchCatalog.PathOf(obj)));
                }
            }
        }

        private IGH_DocumentObject Create(ComponentEntry c)
        {
            switch (c.Kind)
            {
                case CreateKind.Panel: return new GH_Panel();
                case CreateKind.Slider: return BatchCatalog.NewSlider();
                case CreateKind.ValueList: return new GH_ValueList();
                case CreateKind.Toggle: return new GH_BooleanToggle();
            }

            if (c.TypeText == null) return null;
            var field = c.Kind == CreateKind.Param ? "param" : "type";
            var path = $"{c.Path}.{field}";
            var res = TypeNameRule.Resolve(c.TypeText, _catalog, paramsOnly: c.Kind == CreateKind.Param);

            switch (res.Kind)
            {
                case TypeResolutionKind.Resolved:
                    if (res.UsedDeprecated)
                        _warnings.Add($"{path}: '{c.TypeText}' resolved to a deprecated type, {res.Match.Describe()}, because it was the only match");
                    if (res.SetAside.Count > 0 && _setAsideWarned.Add(c.TypeText.ToLowerInvariant()))
                        _warnings.Add($"{path}: '{c.TypeText}' is Grasshopper's {res.Match.PathText}; "
                            + string.Join("; ", res.SetAside.Take(3).Select(x => $"{x.Plugin} has one too ('{x.Category}/{x.Name}')"))
                            + ". Write that name for the plugin's");
                    var obj = BatchCatalog.Emit(res.Match.Guid);
                    if (obj == null)
                        _errors.Add($"{path}: Grasshopper could not create {res.Match.Describe()}");
                    return obj;

                case TypeResolutionKind.Ambiguous:
                    var name = c.TypeText.Split('/').Last().Trim();
                    var fix = res.Candidates.Any(x => x.IsParam) && res.Candidates.Any(x => !x.IsParam) && c.Kind == CreateKind.Type
                        ? $" Write 'Category/Name' (e.g. '{res.Candidates.First(x => !x.IsParam).Category}/{name}') or use \"param\": \"{name}\""
                        : " Write 'Category/Name', 'Category/Subcategory/Name' or a GUID";
                    _errors.Add($"{path}: '{c.TypeText}' is ambiguous: {string.Join("; ", res.Candidates.Take(6).Select(x => x.Describe()))}.{fix}");
                    return null;

                case TypeResolutionKind.CategoryMissing:
                    var plugin = PluginRegistry.Instance.GetPluginForCategory(res.Category);
                    _errors.Add(plugin != null
                        ? $"{path}: no installed plugin provides '{res.Category}'. It belongs to {plugin.Name} ({plugin.Author}): {plugin.DocumentationUrl}"
                        : $"{path}: no installed plugin provides the category '{res.Category}'");
                    return null;

                case TypeResolutionKind.GuidNotInstalled:
                    _errors.Add($"{path}: no installed component has GUID {c.TypeText}; its plugin may not be installed");
                    return null;

                default:
                    if (c.Kind == CreateKind.Param && res.NonParamMatches.Count > 0)
                    {
                        _errors.Add($"{path}: '{c.TypeText}' is a component, not a parameter; use \"type\": \"{c.TypeText}\"");
                        return null;
                    }
                    var suggestions = res.Suggestions.Count > 0
                        ? " Did you mean " + string.Join(", ", res.Suggestions.Select(x => $"'{x.PathText}'")) + "?"
                        : string.Empty;
                    var pluginHint = c.TypeText.Contains("/") ? string.Empty : " If it comes from a plugin, check it is installed, or write 'Category/Name'.";
                    _errors.Add($"{path}: no {(c.Kind == CreateKind.Param ? "parameter" : "component")} named '{c.TypeText}'.{suggestions}{pluginHint}");
                    return null;
            }
        }

        private void Configure(BatchItem item, ComponentEntry c)
        {
            var obj = item.Obj;

            if (c.Nickname != null && CheckNickname(c.Nickname, item, $"{c.Path}.nickname"))
            {
                obj.NickName = c.Nickname;
                _canvas.AddNickname(c.Nickname, item);
            }

            switch (obj)
            {
                case GH_Panel panel when c.Kind == CreateKind.Panel:
                    PanelOp.Configure(panel, c.PanelText, c.Multiline ?? false);
                    break;
                case GH_NumberSlider slider when c.Kind == CreateKind.Slider:
                    var plan = SliderPlan(slider, c.Slider ?? new SliderSpec(), null, $"{c.Path}.slider");
                    if (plan != null) SliderOp.Configure(slider, plan);
                    break;
                case GH_ValueList list when c.Kind == CreateKind.ValueList && c.ValueList?.Items != null:
                    ValueListOp.Configure(list, c.ValueList.Items, SelectionIndex(c.ValueList, c.ValueList.Items));
                    break;
                case GH_BooleanToggle toggle when c.Kind == CreateKind.Toggle:
                    toggle.Value = c.Toggle ?? false;
                    break;
            }

            int? outputs = c.Outputs;
            if (c.AddOutputs.HasValue && obj is IGH_Component withOutputs)
                outputs = withOutputs.Params.Output.Count + c.AddOutputs.Value;
            else if (c.AddOutputs.HasValue)
                outputs = 1 + c.AddOutputs.Value;

            if (c.Inputs.HasValue || outputs.HasValue)
                PlanCounts(item, c.Inputs, outputs, c.Path, c.AddOutputs.HasValue ? "addOutputs" : "outputs");

            PlanModifiers(item, c.InputModifiers, true, $"{c.Path}.inputModifiers");
            PlanModifiers(item, c.OutputModifiers, false, $"{c.Path}.outputModifiers");

            obj.Attributes.ExpireLayout();
            obj.Attributes.PerformLayout();
            if (c.X.HasValue && c.Y.HasValue)
            {
                obj.Attributes.Pivot = new PointF((float)c.X.Value, (float)c.Y.Value);
                obj.Attributes.ExpireLayout();
            }
            else
            {
                int id = _layoutIds.Count;
                _layoutIds[item] = id;
                _layout[id] = new AutoLayout.Node { Id = id, Size = obj.Attributes.Bounds.Size };
            }
        }

        private static int? SelectionIndex(ValueListSpec spec, List<KeyValuePair<string, string>> items)
        {
            if (spec.SelectedIndex.HasValue) return spec.SelectedIndex;
            if (spec.SelectedName != null)
            {
                int i = items.FindIndex(kv => kv.Key == spec.SelectedName);
                return i >= 0 ? i : (int?)null;
            }
            return null;
        }

        /// <summary>
        /// Nicknames must be unique among surviving objects (groups aside), and must not also be a
        /// ref that points at a different object.
        /// </summary>
        private bool CheckNickname(string nickname, BatchItem item, string path)
        {
            var holders = _canvas.NicknameHolders(nickname, item);
            if (holders.Count > 0)
            {
                _errors.Add($"{path}: '{nickname}' is already the nickname of {holders[0].Label}; nicknames must be unique");
                return false;
            }
            if (_canvas.IsBatchRef(nickname) && _canvas.Resolve(nickname, path, new List<string>()) != item)
            {
                _errors.Add($"{path}: '{nickname}' is also a ref in this batch; pick another nickname");
                return false;
            }
            var refObj = _canvas.SessionRefObject(nickname);
            if (refObj != null && refObj != item.Obj)
            {
                _errors.Add($"{path}: '{nickname}' is also a ref from an earlier batch (for {_canvas.ExistingLabel(refObj)}); pick another nickname");
                return false;
            }
            return true;
        }

        /// <summary>
        /// The slider's final range and value. The value must fall inside the final range; the
        /// range must be ascending. Uses the shared SliderConfig plan, so batch and gh_canvas apply
        /// a slider configuration the same way.
        /// </summary>
        private SliderConfigPlan SliderPlan(GH_NumberSlider slider, SliderSpec spec, double? value, string path)
        {
            double min = spec.Min ?? (double)slider.Slider.Minimum;
            double max = spec.Max ?? (double)slider.Slider.Maximum;
            double? v = spec.Value ?? value;

            if (min >= max)
            {
                _errors.Add($"{path}: the range must go up: min {Fmt(min)} is not below max {Fmt(max)}");
                return null;
            }

            if (!v.HasValue && (spec.Min.HasValue || spec.Max.HasValue))
            {
                double current = (double)slider.CurrentValue;
                if (current < min || current > max)
                    v = Math.Min(Math.Max(current, min), max);
            }

            if (v.HasValue && (v.Value < min || v.Value > max))
            {
                _errors.Add($"{path}: value {Fmt(v.Value)} is outside the range {Fmt(min)}–{Fmt(max)}");
                return null;
            }

            return SliderConfig.Plan(
                spec.Min ?? double.NaN,
                spec.Max ?? double.NaN,
                v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : null,
                spec.Decimals ?? -1);
        }

        /// <summary>
        /// Port counts on a variable-parameter component. A new component is changed in memory now;
        /// an existing one gets a planned port list (its new ports already exist, unregistered) and
        /// a change for phase 2. Removing a wired port is refused.
        /// </summary>
        private void PlanCounts(BatchItem item, int? inputs, int? outputs, string path, string outputsField)
        {
            if (!(item.Obj is IGH_Component comp) || !(item.Obj is IGH_VariableParameterComponent v))
            {
                _errors.Add($"{path}: {item.Label} doesn't have a variable number of ports");
                return;
            }
            if (BatchCatalog.IsScriptComponent(item.Obj))
            {
                _errors.Add($"{path}: {item.Label} is a script component; set its inputs and outputs with gh_script");
                return;
            }

            foreach (var (target, side, field) in new[] { (inputs, GH_ParameterSide.Input, "inputs"), (outputs, GH_ParameterSide.Output, outputsField) })
            {
                if (!target.HasValue) continue;
                var remove = new List<IGH_Param>();
                var add = new List<IGH_Param>();
                if (!BatchPorts.Plan(comp, v, side, target.Value, remove, add, out var why))
                {
                    _errors.Add($"{path}.{field}: {why}");
                    continue;
                }

                bool isInput = side == GH_ParameterSide.Input;
                if (item.IsNew)
                {
                    BatchPorts.Apply(comp, side, remove, add);
                    foreach (var p in add) _plan.NewParams.Add(p);
                    continue;
                }

                var wired = remove.Where(p => isInput
                    ? p.Sources.Any(s => !_removed.Contains(Key(s, p)))
                    : p.Recipients.Any(r => !_removed.Contains(Key(p, r)))).ToList();
                if (wired.Count > 0)
                {
                    _errors.Add($"{path}.{field}: can't remove {(isInput ? "input" : "output")} '{wired[0].NickName}' from {item.Label}: it is wired. Disconnect it first");
                    continue;
                }

                var op = new PortCountOp { Path = path, Component = comp, Side = side };
                op.Remove.AddRange(remove);
                op.Add.AddRange(add);
                _plan.ExistingChanges.Add(op);
                foreach (var p in add) _plan.NewParams.Add(p);

                var planned = (isInput ? item.Inputs : item.Outputs).Where(p => !remove.Contains(p)).Concat(add).ToList();
                if (isInput) item.PlannedInputs = planned; else item.PlannedOutputs = planned;
            }
        }

        private void PlanModifiers(BatchItem item, List<ModifierEntry> entries, bool isInput, string path)
        {
            foreach (var m in entries)
            {
                var p = _canvas.ResolvePort(item, m.Port, isInput, $"{path}.{m.Port}", _errors);
                if (p == null) continue;
                if (item.IsNew || IsNewParam(p))
                    ModifierOp.Configure(p, m.Settings);
                else
                    _plan.ExistingChanges.Add(new ModifierOp { Path = path, Param = p, Settings = m.Settings });
            }
        }

        // ---- set --------------------------------------------------------------------------------

        private void PlanSets()
        {
            foreach (var s in _spec.Set)
            {
                int before = _errors.Count;
                int opsBefore = _plan.ExistingChanges.Count;
                var (item, port) = ResolveTarget(s.Target, $"{s.Path}.target");
                if (item == null) continue;

                if (port != null)
                {
                    bool onlyValue = s.Panel == null && !s.Multiline.HasValue && s.Slider == null && s.ValueList == null
                        && s.Nickname == null && !s.Inputs.HasValue && !s.Outputs.HasValue
                        && s.InputModifiers.Count == 0 && s.OutputModifiers.Count == 0;
                    if (!onlyValue)
                        _errors.Add($"{s.Path}: with a port target ('{s.Target}') only 'value' can be set; target the object itself for the rest");
                    else if (!s.HasValue)
                        _errors.Add($"{s.Path}: give a 'value' for '{s.Target}'");
                    else if (s.Value != null)
                    {
                        var p = _canvas.ResolvePort(item, port, true, $"{s.Path}.target", _errors);
                        if (p != null) PlanValue(item, p, s.Value, $"{s.Path}.value");
                    }
                }
                else
                {
                    PlanObjectSet(item, s);
                }

                if (_errors.Count > before)
                {
                    // Keep the entry all-or-nothing: drop any change it queued for phase 2.
                    _plan.ExistingChanges.RemoveRange(opsBefore, _plan.ExistingChanges.Count - opsBefore);
                    continue;
                }
                _plan.Report.Counts.Set++;
            }
        }

        private (BatchItem Item, string Port) ResolveTarget(string target, string path) =>
            _canvas.ResolveObjectOrPort(target, path, _errors);

        private void PlanObjectSet(BatchItem item, SetEntry s)
        {
            var obj = item.Obj;
            var path = s.Path;

            if (s.Nickname != null && CheckNickname(s.Nickname, item, $"{path}.nickname"))
            {
                var old = obj.NickName;
                if (item.IsNew) obj.NickName = s.Nickname;
                else _plan.ExistingChanges.Add(new NicknameOp { Path = path, Object = obj, Nickname = s.Nickname });
                _canvas.RemoveNickname(old, item);
                _canvas.AddNickname(s.Nickname, item);
                _canvas.Renamed(old, s.Nickname);
            }

            if (s.Panel != null || s.Multiline.HasValue || (s.HasValue && obj is GH_Panel))
            {
                if (!(obj is GH_Panel panel))
                    _errors.Add($"{path}: 'panel' and 'multiline' only apply to panels; {item.Label} isn't one");
                else if (s.Panel != null && s.HasValue)
                    _errors.Add($"{path}: give the panel text once, as 'panel' or 'value'");
                else
                {
                    var text = s.Panel ?? (s.HasValue ? ValueText(s.Value) : null);
                    if (item.IsNew) PanelOp.Configure(panel, text, s.Multiline);
                    else _plan.ExistingChanges.Add(new PanelOp { Path = path, Panel = panel, Text = text, Multiline = s.Multiline });
                }
            }

            if (s.Slider != null || (s.HasValue && obj is GH_NumberSlider))
            {
                if (!(obj is GH_NumberSlider slider))
                    _errors.Add($"{path}.slider: {item.Label} isn't a number slider");
                else
                {
                    double? value = null;
                    if (s.HasValue)
                    {
                        if (s.Slider?.Value != null)
                            _errors.Add($"{path}: give the slider value once, in 'slider' or as 'value'");
                        else if (s.Value?.Kind != BatchValueKind.Number)
                            _errors.Add($"{path}.value: a slider's value is a number, got {s.Value?.Describe()}");
                        else
                            value = s.Value.Number;
                    }
                    var plan = SliderPlan(slider, s.Slider ?? new SliderSpec(), value, $"{path}.slider");
                    if (plan != null)
                    {
                        if (item.IsNew) SliderOp.Configure(slider, plan);
                        else _plan.ExistingChanges.Add(new SliderOp { Path = path, Slider = slider, Plan = plan });
                    }
                }
            }

            if (s.ValueList != null || (s.HasValue && obj is GH_ValueList))
            {
                if (!(obj is GH_ValueList list))
                    _errors.Add($"{path}.valueList: {item.Label} isn't a value list");
                else
                    PlanValueList(item, list, s);
            }

            if (s.HasValue && obj is GH_BooleanToggle toggle)
            {
                bool? state = s.Value?.Kind == BatchValueKind.Boolean ? s.Value.Boolean
                    : s.Value?.Kind == BatchValueKind.Text && ParseHelpers.TryParseBool(s.Value.Text, out var b) ? b
                    : (bool?)null;
                if (!state.HasValue)
                    _errors.Add($"{path}.value: a toggle takes true or false, got {s.Value?.Describe()}");
                else if (item.IsNew) toggle.Value = state.Value;
                else _plan.ExistingChanges.Add(new ToggleOp { Path = path, Toggle = toggle, Value = state.Value });
            }

            if (s.Inputs.HasValue || s.Outputs.HasValue)
                PlanCounts(item, s.Inputs, s.Outputs, path, "outputs");

            PlanModifiers(item, s.InputModifiers, true, $"{path}.inputModifiers");
            PlanModifiers(item, s.OutputModifiers, false, $"{path}.outputModifiers");

            bool special = obj is GH_Panel || obj is GH_NumberSlider || obj is GH_ValueList || obj is GH_BooleanToggle;
            if (s.HasValue && !special && s.Value != null)
            {
                if (obj is IGH_Component comp)
                {
                    var first = comp.Params.Input.FirstOrDefault();
                    _errors.Add($"{path}: name the input to set, e.g. target '{s.Target}.{first?.NickName ?? "0"}'");
                }
                else if (obj is IGH_Param p)
                {
                    PlanValue(item, p, s.Value, $"{path}.value");
                }
                else
                {
                    _errors.Add($"{path}: {item.Label} has no value to set");
                }
            }
        }

        private void PlanValueList(BatchItem item, GH_ValueList list, SetEntry s)
        {
            var path = s.Path;
            var items = s.ValueList?.Items;
            var names = items ?? list.ListItems.Select(i => new KeyValuePair<string, string>(i.Name, i.Expression)).ToList();
            int? select = s.ValueList != null ? SelectionIndex(s.ValueList, names) : null;

            if (s.ValueList != null && s.ValueList.HasSelection && !select.HasValue)
                _errors.Add($"{path}.valueList.selected: no item '{s.ValueList.SelectedName ?? s.ValueList.SelectedIndex?.ToString()}'. Items: {string.Join(", ", names.Select(n => n.Key))}");

            if (s.HasValue)
            {
                if (s.ValueList != null && s.ValueList.HasSelection)
                {
                    _errors.Add($"{path}: give the selection once, as 'valueList.selected' or 'value'");
                    return;
                }
                var wanted = ValueText(s.Value);
                int i = names.FindIndex(n => n.Key == wanted);
                if (i < 0) i = names.FindIndex(n => n.Value == wanted);
                if (i < 0)
                {
                    _errors.Add($"{path}.value: no item named '{wanted}'. Items: {string.Join(", ", names.Select(n => n.Key))}");
                    return;
                }
                select = i;
            }

            if (item.IsNew) ValueListOp.Configure(list, items, select);
            else _plan.ExistingChanges.Add(new ValueListOp { Path = path, List = list, Items = items, Select = select });
        }

        private void PlanValue(BatchItem item, IGH_Param p, BatchValue value, string path)
        {
            if (!BatchValues.TryConvert(value, p, item.Label, out var goos, out var why))
            {
                _errors.Add($"{path}: {why}");
                return;
            }
            if (item.IsNew || IsNewParam(p))
                BatchValues.Store(p, goos);
            else
                _plan.ExistingChanges.Add(new StoreValueOp { Path = path, Param = p, Items = goos });
            _valueTargets.Add((p, path));
        }

        private static string ValueText(BatchValue v)
        {
            if (v == null) return string.Empty;
            switch (v.Kind)
            {
                case BatchValueKind.Text: return v.Text;
                case BatchValueKind.Number: return v.Number.ToString("R", CultureInfo.InvariantCulture);
                case BatchValueKind.Boolean: return v.Boolean ? "true" : "false";
                case BatchValueKind.List: return string.Join("\n", v.Items.Select(ValueText));
                case BatchValueKind.Null: return string.Empty;
                default: return v.Describe();
            }
        }

        // ---- insert -----------------------------------------------------------------------------

        private void PlanInserts()
        {
            foreach (var ins in _spec.Insert)
            {
                int before = _errors.Count;
                if (!_canvas.IsBatchRef(ins.Ref))
                {
                    _errors.Add($"{ins.Path}.ref: '{ins.Ref}' must be a component created in this batch");
                    continue;
                }
                var item = _canvas.Resolve(ins.Ref, $"{ins.Path}.ref", _errors);
                if (item == null || ins.OnFrom == null || ins.OnTo == null) continue;

                var srcItem = _canvas.Resolve(ins.OnFrom.Obj, $"{ins.Path}.on[0]", _errors);
                var tgtItem = _canvas.Resolve(ins.OnTo.Obj, $"{ins.Path}.on[1]", _errors);
                if (srcItem == null || tgtItem == null) continue;
                if (srcItem.IsNew || tgtItem.IsNew)
                {
                    _errors.Add($"{ins.Path}.on: the wire must already exist, and objects new in this batch have no wires yet");
                    continue;
                }

                var src = _canvas.ResolvePort(srcItem, ins.OnFrom.Port, false, $"{ins.Path}.on[0]", _errors);
                var tgt = _canvas.ResolvePort(tgtItem, ins.OnTo.Port, true, $"{ins.Path}.on[1]", _errors);
                var newIn = _canvas.ResolvePort(item, ins.In, true, $"{ins.Path}.in", _errors);
                var newOut = _canvas.ResolvePort(item, ins.Out, false, $"{ins.Path}.out", _errors);
                if (src == null || tgt == null || newIn == null || newOut == null) continue;

                if (!WireExists(src, tgt))
                {
                    _errors.Add($"{ins.Path}.on: there is no wire from {Port(srcItem, src, false)} to {Port(tgtItem, tgt, true)}; {SourcesOf(tgt)}");
                    continue;
                }
                if (_errors.Count > before) continue;

                _removed.Add(Key(src, tgt));
                _added.Add(Key(src, newIn));
                _added.Add(Key(newOut, tgt));
                _plan.Inserts.Add(new InsertOp { Path = ins.Path, Source = src, Target = tgt, NewInput = newIn, NewOutput = newOut });
                _plan.Report.Counts.Inserts++;

                if (_layoutIds.TryGetValue(item, out var id))
                {
                    _layout[id].InsertSource = _canvas.Box(srcItem.Obj);
                    _layout[id].InsertTarget = _canvas.Box(tgtItem.Obj);
                }
            }
        }

        // ---- wires ------------------------------------------------------------------------------

        private void PlanWires()
        {
            foreach (var w in _spec.Wires)
            {
                var srcItem = _canvas.Resolve(w.From.Obj, $"{w.Path}[0]", _errors);
                var tgtItem = _canvas.Resolve(w.To.Obj, $"{w.Path}[1]", _errors);
                var src = srcItem == null ? null : _canvas.ResolvePort(srcItem, w.From.Port, false, $"{w.Path}[0]", _errors);
                var tgt = tgtItem == null ? null : _canvas.ResolvePort(tgtItem, w.To.Port, true, $"{w.Path}[1]", _errors);
                if (src == null || tgt == null) continue;

                if (srcItem == tgtItem)
                {
                    _errors.Add($"{w.Path}: wiring {srcItem.Label} into itself would make a loop");
                    continue;
                }
                if (WireExists(src, tgt))
                {
                    _warnings.Add($"{w.Path}: {Port(srcItem, src, false)} is already wired to {Port(tgtItem, tgt, true)}; skipped");
                    continue;
                }

                _added.Add(Key(src, tgt));
                _plan.Wires.Add(new WireOp { Path = w.Path, Source = src, Target = tgt });
                _plan.Report.Counts.Wires++;

                bool srcAuto = _layoutIds.TryGetValue(srcItem, out var srcId);
                bool tgtAuto = _layoutIds.TryGetValue(tgtItem, out var tgtId);
                if (srcAuto && tgtAuto) _edges.Add((srcId, tgtId));
                else if (tgtAuto && !srcItem.IsNew) _layout[tgtId].Upstream.Add(_canvas.Box(srcItem.Obj));
                else if (tgtAuto && srcItem.IsNew && srcItem.Entry?.X != null)
                    _layout[tgtId].Upstream.Add(srcItem.Obj.Attributes.Bounds);
            }
        }

        // ---- groups -----------------------------------------------------------------------------

        private void PlanGroups()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in _spec.Groups)
            {
                int before = _errors.Count;
                Color? colour = null;
                if (g.Color != null)
                {
                    if (TryParseGroupColour(g.Color, out var c)) colour = MatchExistingAlpha(c, g.Color);
                    else _errors.Add($"{g.Path}.color: '{g.Color}' isn't a colour; use '#RRGGBB' or '#AARRGGBB'");
                }

                var members = new List<Guid>();
                for (int i = 0; i < g.Members.Count; i++)
                {
                    var m = _canvas.Resolve(g.Members[i], $"{g.Path}.members[{i}]", _errors);
                    if (m != null && LichenThallus.IsEndpoint(m.Obj))
                        _errors.Add($"{g.Path}.members[{i}]: {LichenThallus.EndpointRefusal(m.Label)}");
                    else if (m != null && !members.Contains(m.Obj.InstanceGuid)) members.Add(m.Obj.InstanceGuid);
                }

                if (g.Name != null)
                {
                    if (_canvas.Groups.Any(x => x.NickName == g.Name))
                        _errors.Add($"{g.Path}.name: a group named '{g.Name}' already exists; to add to it use {{\"extend\": \"{g.Name}\", \"members\": [...]}}");
                    else if (!seen.Add(g.Name))
                        _errors.Add($"{g.Path}.name: '{g.Name}' is created twice in this batch");
                    if (_errors.Count > before) continue;

                    var group = new GH_Group { NickName = g.Name, Name = g.Name };
                    if (colour.HasValue) group.Colour = colour.Value;
                    group.CreateAttributes();
                    var op = new GroupCreateOp { Path = g.Path, Group = group };
                    op.Members.AddRange(members);
                    _plan.Groups.Add(op);
                }
                else
                {
                    var group = FindGroup(g.Extend, $"{g.Path}.extend");
                    if (group != null && LichenThallus.IsThallus(group))
                        _errors.Add($"{g.Path}.extend: {LichenThallus.MembershipRefusal(group.NickName)}");
                    if (group == null || _errors.Count > before) continue;
                    var op = new GroupExtendOp { Path = g.Path, Group = group, Colour = colour };
                    op.Members.AddRange(members);
                    _plan.Groups.Add(op);
                }
                _plan.Report.Counts.Groups++;
            }
        }

        private GH_Group FindGroup(string name, string path)
        {
            var named = _canvas.Groups.Where(x => x.NickName == name).ToList();
            if (named.Count == 1) return named[0];
            if (named.Count > 1)
            {
                _errors.Add($"{path}: {named.Count} groups are named '{name}'; use a GUID prefix: {string.Join(", ", named.Take(5).Select(x => _canvas.Prefix(x.InstanceGuid)))}");
                return null;
            }

            if (Guid.TryParse(name, out var id) || BatchNames.LooksLikeGuidPrefix(name))
            {
                var byId = _canvas.Groups.Where(x => x.InstanceGuid.ToString("D").StartsWith(name.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
                if (byId.Count == 1) return byId[0];
            }

            var closest = BatchNames.Closest(name, _canvas.Groups.Select(x => x.NickName));
            _errors.Add($"{path}: no group named '{name}'."
                + (closest != null ? $" Did you mean '{closest}'?" : _canvas.Groups.Count > 0 ? $" Groups: {string.Join(", ", _canvas.Groups.Take(10).Select(x => x.NickName))}" : " There are no groups yet; use 'name' to create one"));
            return null;
        }

        /// <summary>
        /// Reusing a colour the file already has: group_list reports colours as #RRGGBB, so when a
        /// colour without its own transparency matches an existing group's, take that group's
        /// transparency too and the new group looks like its siblings.
        /// </summary>
        private Color MatchExistingAlpha(Color colour, string text)
        {
            var hex = text.Trim().TrimStart('#');
            if (hex.Length == 8) return colour;
            var twin = _canvas.Groups.FirstOrDefault(x => x.Colour.R == colour.R && x.Colour.G == colour.G && x.Colour.B == colour.B);
            return twin != null ? Color.FromArgb(twin.Colour.A, colour.R, colour.G, colour.B) : colour;
        }

        /// <summary>
        /// <c>#RRGGBB</c> (and R,G,B or named colours) get Grasshopper's usual group transparency;
        /// <c>#AARRGGBB</c> sets the transparency exactly.
        /// </summary>
        internal static bool TryParseGroupColour(string text, out Color colour)
        {
            const int usualAlpha = 150;
            var hex = text.Trim().TrimStart('#');
            if (hex.Length == 8 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                colour = Color.FromArgb(argb);
                return true;
            }
            if (ToolHelpers.TryParseColor(text.Trim(), out var c))
            {
                colour = Color.FromArgb(usualAlpha, c.R, c.G, c.B);
                return true;
            }
            colour = Color.Empty;
            return false;
        }

        // ---- layout, warnings, hints ------------------------------------------------------------

        private void PlanWatch()
        {
            var names = _spec.Options.Watch;
            if (names.Count == 0) return;
            _plan.Watch.AddRange(WatchedOutputs.Resolve(_canvas, names, i => $"options.watch[{i}]", _errors));
            if (_plan.Watch.Count > BatchOptions.MaxWatch)
                _errors.Add($"options.watch: {_plan.Watch.Count} outputs; at most {BatchOptions.MaxWatch} (a component named without a port counts each of its outputs)");
        }

        private void PlanLayout()
        {
            var auto = _layoutIds.Where(kv => !kv.Key.Failed).ToList();
            if (auto.Count == 0) return;

            var existing = _canvas.ExistingItems.Where(i => !i.Deleted).Select(i => _canvas.Box(i.Obj)).Where(b => !b.IsEmpty).ToList();
            var nodes = auto.Select(kv => _layout[kv.Value]).ToList();

            // New objects with an explicit position take part as fixed nodes, so blocks flow from them.
            foreach (var item in _plan.NewItems.Where(i => !i.Failed && i.Obj != null && !_layoutIds.ContainsKey(i)))
                existing.Add(item.Obj.Attributes.Bounds);

            var positions = AutoLayout.Place(nodes, _edges, existing);
            foreach (var kv in auto)
            {
                if (!positions.TryGetValue(kv.Value, out var topLeft)) continue;
                var attrs = kv.Key.Obj.Attributes;
                var offset = new SizeF(attrs.Pivot.X - attrs.Bounds.X, attrs.Pivot.Y - attrs.Bounds.Y);
                attrs.Pivot = new PointF(topLeft.X + offset.Width, topLeft.Y + offset.Height);
                attrs.ExpireLayout();
            }
        }

        private void WarnWiredValues()
        {
            foreach (var (p, path) in _valueTargets)
            {
                if (p.Sources.Any(s => !_removed.Contains(Key(s, p))) || _added.Any(k => k.Item2 == p.InstanceGuid))
                    _warnings.Add($"{path}: '{p.NickName}' is also wired, so its wire's data is used and this value is ignored");
            }
        }

        private void HintTreeComponents()
        {
            foreach (var (item, setting) in _treeComponents.Where(t => !t.Item.Failed))
            {
                var output = item.Outputs.FirstOrDefault();
                var feeds = _plan.Wires.OfType<WireOp>().Where(w => w.Source == output).ToList();
                if (feeds.Count == 1)
                {
                    var target = feeds[0].Target;
                    var owner = ApplyContext.Owner(target);
                    var ownerLabel = _plan.NewItems.FirstOrDefault(i => i.Obj == owner)?.Ref ?? _canvas.ExistingLabel(owner);
                    _plan.Report.Hints.Add($"{item.Entry.Path}: {item.Obj.Name} could be a port setting: drop it and put \"inputModifiers\": {{\"{target.NickName}\": \"{setting}\"}} on {ownerLabel}");
                }
                else
                {
                    _plan.Report.Hints.Add($"{item.Entry.Path}: {item.Obj.Name} could be a port setting ('{setting}') on the input it feeds, via inputModifiers");
                }
            }
        }

        /// <summary>
        /// A new Text Split fed by a new panel is a list written as "0,2,4": a panel already gives
        /// one item per line, so the split (and its separator) isn't needed. Only new objects are
        /// hinted about; existing ones are never converted.
        /// </summary>
        private void HintTextSplits()
        {
            foreach (var split in _plan.NewItems.Where(i => !i.Failed && i.Obj is IGH_Component c && c.Name == "Text Split"))
            {
                var textInput = ((IGH_Component)split.Obj).Params.Input.FirstOrDefault();
                var fromPanel = _plan.Wires.OfType<WireOp>()
                    .Where(w => w.Target == textInput)
                    .Select(w => _plan.NewItems.FirstOrDefault(i => i.Obj == ApplyContext.Owner(w.Source)))
                    .FirstOrDefault(i => i != null && i.Obj is GH_Panel);
                if (fromPanel != null)
                    _plan.Report.Hints.Add($"{split.Entry.Path}: write '{fromPanel.Ref}' one value per line instead (a panel gives one item per line) and wire it on directly; the Text Split isn't needed");
            }
        }

        private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }
}
