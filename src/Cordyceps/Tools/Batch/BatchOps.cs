using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace Cordyceps.Tools.Batch
{
    internal sealed class BatchTimeoutException : Exception
    {
        public BatchTimeoutException(double seconds)
            : base("the batch took longer than its time limit") { }
    }

    /// <summary>
    /// State shared by every change while a batch is applied. Each change records Grasshopper's
    /// own undo action <em>before</em> it touches the canvas, all into one <see cref="GH_UndoRecord"/>:
    /// pushed, that record is the single Ctrl+Z step; undone on failure, it is the rollback. Objects
    /// the batch creates need no record of their own changes — undoing their addition removes them.
    /// </summary>
    internal sealed class ApplyContext
    {
        private readonly HashSet<Guid> _wires = new HashSet<Guid>();
        private readonly HashSet<Guid> _generic = new HashSet<Guid>();
        private readonly HashSet<Guid> _nicknames = new HashSet<Guid>();
        private readonly HashSet<Guid> _modifiers = new HashSet<Guid>();
        private readonly HashSet<Guid> _persistent = new HashSet<Guid>();

        public ApplyContext(GH_Document doc, GH_UndoRecord record, double timeoutSeconds, HashSet<IGH_DocumentObject> newObjects, HashSet<IGH_Param> newParams)
        {
            Doc = doc;
            Record = record;
            TimeoutSeconds = timeoutSeconds;
            NewObjects = newObjects;
            NewParams = newParams;
        }

        public GH_Document Doc { get; }
        public GH_UndoRecord Record { get; }
        public double TimeoutSeconds { get; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public HashSet<IGH_DocumentObject> NewObjects { get; }
        public HashSet<IGH_Param> NewParams { get; }
        public Dictionary<Guid, IGH_DocumentObject> ToExpire { get; } = new Dictionary<Guid, IGH_DocumentObject>();

        /// <summary>An object added to the canvas whose undo action could not be recorded; rollback removes it by hand.</summary>
        public List<IGH_DocumentObject> AddedUnrecorded { get; } = new List<IGH_DocumentObject>();

        public void CheckTime()
        {
            if (Clock.Elapsed.TotalSeconds > TimeoutSeconds)
                throw new BatchTimeoutException(TimeoutSeconds);
        }

        public void Append(GH_UndoRecord r)
        {
            if (r == null) return;
            foreach (var action in r.Actions)
                Record.AddAction(action);
        }

        private bool IsNew(IGH_Param p) => NewParams.Contains(p) || (Owner(p) is IGH_DocumentObject o && NewObjects.Contains(o));

        public static IGH_DocumentObject Owner(IGH_Param p) => p.Attributes?.GetTopLevel?.DocObject ?? p;

        public void RecordWires(IGH_Param p)
        {
            if (IsNew(p) || !_wires.Add(p.InstanceGuid)) return;
            Append(Doc.UndoUtil.CreateWireEvent("Wire", p));
        }

        public void RecordGeneric(IGH_DocumentObject obj)
        {
            if (NewObjects.Contains(obj) || !_generic.Add(obj.InstanceGuid)) return;
            Append(Doc.UndoUtil.CreateGenericObjectEvent("Change", obj));
            // Restoring an archived object may not re-link its inputs; recording them puts the
            // wires back on undo either way (a wire record restores an exact source list).
            foreach (var input in BatchItem.LiveInputs(obj))
                RecordWires(input);
        }

        public void RecordNickname(IGH_DocumentObject obj)
        {
            if (NewObjects.Contains(obj) || !_nicknames.Add(obj.InstanceGuid)) return;
            Append(Doc.UndoUtil.CreateNickNameEvent("Nickname", obj));
        }

        public void RecordModifiers(IGH_Param p)
        {
            if (IsNew(p) || !_modifiers.Add(p.InstanceGuid)) return;
            Record.AddAction(new GH_DataModificationAction(p));
        }

        public void RecordPersistent(IGH_Param p)
        {
            if (IsNew(p) || !_persistent.Add(p.InstanceGuid)) return;
            Append(BatchValues.PersistentDataEvent(Doc, p));
        }

        public void Expire(IGH_DocumentObject obj)
        {
            if (obj != null) ToExpire[obj.InstanceGuid] = obj;
        }

        public void ExpireOwner(IGH_Param p) => Expire(Owner(p));
    }

    /// <summary>One planned change, validated in phase 1 and applied in phase 2.</summary>
    internal abstract class BatchOp
    {
        public string Path { get; set; }
        public abstract void Apply(ApplyContext ctx);
    }

    internal sealed class DeleteOp : BatchOp
    {
        public List<IGH_DocumentObject> Objects { get; } = new List<IGH_DocumentObject>();

        public override void Apply(ApplyContext ctx)
        {
            // Record every wire that touches a deleted object first: the deleted objects' own inputs
            // and everything downstream. Undo runs backwards, so these are restored last, after the
            // objects are back — whatever order the objects themselves come back in.
            var survivors = new List<IGH_DocumentObject>();
            foreach (var obj in Objects)
            {
                foreach (var input in BatchItem.LiveInputs(obj))
                    ctx.RecordWires(input);
                foreach (var output in BatchItem.LiveOutputs(obj))
                {
                    foreach (var recipient in output.Recipients.ToList())
                    {
                        ctx.RecordWires(recipient);
                        survivors.Add(ApplyContext.Owner(recipient));
                    }
                }
            }

            foreach (var obj in Objects)
            {
                ctx.Append(ctx.Doc.UndoUtil.CreateRemoveObjectEvent("Delete", obj));
                ToolHelpers.GetOwnerDocument(obj, ctx.Doc).RemoveObject(obj, false);
                ctx.CheckTime();
            }

            var deleted = new HashSet<IGH_DocumentObject>(Objects);
            foreach (var s in survivors.Where(s => !deleted.Contains(s)))
                ctx.Expire(s);
        }
    }

    internal sealed class DisconnectOp : BatchOp
    {
        public IGH_Param Source { get; set; }
        public IGH_Param Target { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordWires(Target);
            Target.RemoveSource(Source);
            ctx.ExpireOwner(Target);
        }
    }

    internal sealed class AddOp : BatchOp
    {
        public IGH_DocumentObject Object { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.Doc.AddObject(Object, false);
            ctx.AddedUnrecorded.Add(Object);
            ctx.Append(ctx.Doc.UndoUtil.CreateAddObjectEvent("Add", Object));
            ctx.AddedUnrecorded.Remove(Object);
            ctx.Expire(Object);
        }
    }

    internal sealed class NicknameOp : BatchOp
    {
        public IGH_DocumentObject Object { get; set; }
        public string Nickname { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordNickname(Object);
            Object.NickName = Nickname;
            Object.Attributes?.ExpireLayout();
            ctx.Expire(Object);
        }
    }

    /// <summary>Panel text and multiline setting.</summary>
    internal sealed class PanelOp : BatchOp
    {
        public GH_Panel Panel { get; set; }
        public string Text { get; set; }
        public bool? Multiline { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(Panel);
            Configure(Panel, Text, Multiline);
            ctx.Expire(Panel);
        }

        public static void Configure(GH_Panel panel, string text, bool? multiline)
        {
            if (text != null) panel.SetUserText(text);
            if (multiline.HasValue) panel.Properties.Multiline = multiline.Value;
            panel.Attributes?.ExpireLayout();
        }
    }

    internal sealed class SliderOp : BatchOp
    {
        public GH_NumberSlider Slider { get; set; }
        public SliderConfigPlan Plan { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(Slider);
            Configure(Slider, Plan);
            ctx.Expire(Slider);
        }

        /// <summary>
        /// Range first, then decimals, then value (setting the value clamps it to the current range).
        /// When the new minimum is above the old maximum, the maximum moves first so the range is
        /// never inverted on the way.
        /// </summary>
        public static void Configure(GH_NumberSlider slider, SliderConfigPlan plan)
        {
            if (plan.SetMinimum && plan.SetMaximum && plan.Minimum > slider.Slider.Maximum)
            {
                slider.Slider.Maximum = plan.Maximum;
                slider.Slider.Minimum = plan.Minimum;
            }
            else
            {
                if (plan.SetMinimum) slider.Slider.Minimum = plan.Minimum;
                if (plan.SetMaximum) slider.Slider.Maximum = plan.Maximum;
            }
            if (plan.SetDecimals) slider.Slider.DecimalPlaces = plan.Decimals;
            if (plan.SetValue) slider.SetSliderValue(plan.Value);
            slider.Attributes?.ExpireLayout();
        }
    }

    internal sealed class ValueListOp : BatchOp
    {
        public GH_ValueList List { get; set; }
        public List<KeyValuePair<string, string>> Items { get; set; }
        public int? Select { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(List);
            Configure(List, Items, Select);
            ctx.Expire(List);
        }

        public static void Configure(GH_ValueList list, List<KeyValuePair<string, string>> items, int? select)
        {
            if (items != null)
            {
                list.ListItems.Clear();
                foreach (var item in items)
                    list.ListItems.Add(new GH_ValueListItem(item.Key, item.Value));
                if (!select.HasValue) select = 0;
            }
            if (select.HasValue && select.Value >= 0 && select.Value < list.ListItems.Count)
                list.SelectItem(select.Value);
            list.Attributes?.ExpireLayout();
        }
    }

    internal sealed class ToggleOp : BatchOp
    {
        public GH_BooleanToggle Toggle { get; set; }
        public bool Value { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(Toggle);
            Toggle.Value = Value;
            ctx.Expire(Toggle);
        }
    }

    internal sealed class StoreValueOp : BatchOp
    {
        public IGH_Param Param { get; set; }
        public List<IGH_Goo> Items { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordPersistent(Param);
            BatchValues.Store(Param, Items);
            ctx.ExpireOwner(Param);
        }
    }

    internal sealed class ModifierOp : BatchOp
    {
        public IGH_Param Param { get; set; }
        public ModifierSet Settings { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordModifiers(Param);
            Configure(Param, Settings);
            ctx.ExpireOwner(Param);
        }

        public static void Configure(IGH_Param p, ModifierSet s)
        {
            p.DataMapping = ToolHelpers.ToGhDataMapping(s.Mapping);
            p.Simplify = s.Simplify;
            p.Reverse = s.Reverse;
        }
    }

    /// <summary>
    /// A new port count on an existing variable-parameter component. The ports to add were
    /// created during planning (so later entries could already wire and configure them); the ones
    /// to remove were checked to carry no wires.
    /// </summary>
    internal sealed class PortCountOp : BatchOp
    {
        public IGH_Component Component { get; set; }
        public GH_ParameterSide Side { get; set; }
        public List<IGH_Param> Remove { get; } = new List<IGH_Param>();
        public List<IGH_Param> Add { get; } = new List<IGH_Param>();

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(Component);
            BatchPorts.Apply(Component, Side, Remove, Add);
            ctx.Expire(Component);
        }
    }

    /// <summary>Puts a new component into an existing wire, keeping the wire's place among the input's sources.</summary>
    internal sealed class InsertOp : BatchOp
    {
        public IGH_Param Source { get; set; }
        public IGH_Param Target { get; set; }
        public IGH_Param NewInput { get; set; }
        public IGH_Param NewOutput { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordWires(Target);
            Target.ReplaceSource(Source, NewOutput);
            NewInput.AddSource(Source);
            ctx.ExpireOwner(Target);
            ctx.ExpireOwner(NewInput);
        }
    }

    internal sealed class WireOp : BatchOp
    {
        public IGH_Param Source { get; set; }
        public IGH_Param Target { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            if (Target.Sources.Contains(Source)) return;
            ctx.RecordWires(Target);
            Target.AddSource(Source);
            ctx.ExpireOwner(Target);
        }
    }

    internal sealed class GroupCreateOp : BatchOp
    {
        public GH_Group Group { get; set; }
        public List<Guid> Members { get; } = new List<Guid>();

        public override void Apply(ApplyContext ctx)
        {
            foreach (var id in Members)
                Group.AddObject(id);
            ctx.Doc.AddObject(Group, false);
            ctx.AddedUnrecorded.Add(Group);
            ctx.Append(ctx.Doc.UndoUtil.CreateAddObjectEvent("Group", Group));
            ctx.AddedUnrecorded.Remove(Group);
            Group.ExpireCaches();
        }
    }

    internal sealed class GroupExtendOp : BatchOp
    {
        public GH_Group Group { get; set; }
        public List<Guid> Members { get; } = new List<Guid>();
        public Color? Colour { get; set; }

        public override void Apply(ApplyContext ctx)
        {
            ctx.RecordGeneric(Group);
            foreach (var id in Members)
            {
                if (!Group.ObjectIDs.Contains(id))
                    Group.AddObject(id);
            }
            if (Colour.HasValue) Group.Colour = Colour.Value;
            Group.ExpireCaches();
        }
    }

    /// <summary>
    /// Variable-parameter port counts, following the same SDK steps as gh_canvas's zoomable
    /// action (copied here rather than shared, to leave that code untouched).
    /// </summary>
    internal static class BatchPorts
    {
        /// <summary>
        /// Plan a new count on one side: which ports go (from the end) and which new ones come. New
        /// ports are created but not registered. Returns false with a reason when the component
        /// refuses the count.
        /// </summary>
        public static bool Plan(IGH_Component comp, IGH_VariableParameterComponent v, GH_ParameterSide side,
            int target, List<IGH_Param> remove, List<IGH_Param> add, out string error)
        {
            error = null;
            var list = side == GH_ParameterSide.Input ? comp.Params.Input : comp.Params.Output;
            var sideName = side == GH_ParameterSide.Input ? "inputs" : "outputs";

            int count = list.Count;
            for (int i = count - 1; i >= target; i--)
            {
                if (!v.CanRemoveParameter(side, i))
                {
                    error = $"{comp.Name} can't have fewer than {i + 1} {sideName}";
                    return false;
                }
                remove.Add(list[i]);
            }

            for (int i = count; i < target; i++)
            {
                if (!v.CanInsertParameter(side, i))
                {
                    error = $"{comp.Name} can't have more than {i} {sideName}";
                    return false;
                }
                var p = v.CreateParameter(side, i);
                if (p == null)
                {
                    error = $"{comp.Name} didn't create {sideName.TrimEnd('s')} {i}";
                    return false;
                }
                add.Add(p);
            }
            return true;
        }

        public static void Apply(IGH_Component comp, GH_ParameterSide side, List<IGH_Param> remove, List<IGH_Param> add)
        {
            foreach (var p in remove)
            {
                if (side == GH_ParameterSide.Input) comp.Params.UnregisterInputParameter(p);
                else comp.Params.UnregisterOutputParameter(p);
            }
            foreach (var p in add)
            {
                if (side == GH_ParameterSide.Input) comp.Params.RegisterInputParam(p);
                else comp.Params.RegisterOutputParam(p);
            }
            if (comp is IGH_VariableParameterComponent v)
                v.VariableParameterMaintenance();
            comp.Params.OnParametersChanged();
            comp.Attributes?.ExpireLayout();
        }
    }
}
