using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// One object a batch refers to: an existing canvas object, or one the batch creates (held in
    /// memory until the apply phase adds it). Ports are read through <see cref="Inputs"/> and
    /// <see cref="Outputs"/>, which show the state after this batch's configuration — for an existing
    /// component whose port count the batch changes, that is the planned list, not the live one.
    /// </summary>
    internal sealed class BatchItem
    {
        public IGH_DocumentObject Obj { get; set; }
        public bool IsNew { get; set; }
        public string Ref { get; set; }
        public ComponentEntry Entry { get; set; }
        /// <summary>A new object whose entry failed (only with atomic=false): skipped, and so is anything that needs it.</summary>
        public bool Failed { get; set; }
        /// <summary>Deleted earlier in this batch.</summary>
        public bool Deleted { get; set; }
        public string Label { get; set; }

        public List<IGH_Param> PlannedInputs { get; set; }
        public List<IGH_Param> PlannedOutputs { get; set; }

        public IReadOnlyList<IGH_Param> Inputs => PlannedInputs ?? LiveInputs(Obj);
        public IReadOnlyList<IGH_Param> Outputs => PlannedOutputs ?? LiveOutputs(Obj);

        public static IReadOnlyList<IGH_Param> LiveInputs(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component c) return c.Params.Input.ToList();
            if (obj is IGH_Param p) return AcceptsWires(p) ? new[] { p } : Array.Empty<IGH_Param>();
            return Array.Empty<IGH_Param>();
        }

        public static IReadOnlyList<IGH_Param> LiveOutputs(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component c) return c.Params.Output.ToList();
            if (obj is IGH_Param p) return new[] { p };
            return Array.Empty<IGH_Param>();
        }

        /// <summary>
        /// Sliders, toggles, value lists and buttons draw no input grip and take no wires; a panel,
        /// a graph mapper or a plain parameter does.
        /// </summary>
        public static bool AcceptsWires(IGH_Param p)
        {
            if (p is GH_NumberSlider || p is GH_BooleanToggle || p is GH_ValueList || p is GH_ButtonObject)
                return false;
            return p.Attributes == null || p.Attributes.HasInputGrip;
        }
    }

    /// <summary>
    /// The canvas as one batch sees it: existing objects (Cordyceps infrastructure stays invisible,
    /// as in every other tool), the objects the batch creates, and the naming rules that tie names
    /// to them. Refs and nicknames are case-sensitive; a name that reaches two different objects is
    /// an error; a near miss is offered as a suggestion and never used.
    /// </summary>
    internal sealed class BatchCanvas
    {
        private readonly Dictionary<Guid, BatchItem> _existing = new Dictionary<Guid, BatchItem>();
        private readonly Dictionary<string, List<BatchItem>> _byNickname = new Dictionary<string, List<BatchItem>>(StringComparer.Ordinal);
        private readonly Dictionary<string, BatchItem> _newByRef = new Dictionary<string, BatchItem>(StringComparer.Ordinal);
        private readonly List<GH_Group> _groups = new List<GH_Group>();
        private readonly GuidPrefixIndex _prefixes;

        public BatchCanvas(GH_Document doc)
        {
            Doc = doc;
            DocumentId = doc.DocumentID;
            InfraIds = ToolHelpers.GetCordycepsInfrastructureIds(doc);
            _prefixes = new GuidPrefixIndex(doc.Objects.Select(o => o.InstanceGuid));

            foreach (var obj in ToolHelpers.GetActiveObjects(doc, InfraIds))
            {
                if (obj is GH_Group g)
                {
                    _groups.Add(g);
                    continue;
                }
                var item = new BatchItem { Obj = obj, Label = ExistingLabel(obj) };
                _existing[obj.InstanceGuid] = item;
                if (!string.IsNullOrEmpty(obj.NickName))
                    AddNickname(obj.NickName, item);
            }
        }

        public GH_Document Doc { get; }
        public Guid DocumentId { get; }
        public HashSet<Guid> InfraIds { get; }
        public IReadOnlyList<GH_Group> Groups => _groups;
        public IEnumerable<BatchItem> ExistingItems => _existing.Values;

        /// <summary>Existing objects named by nickname or GUID, as written → prefix (dry-run report).</summary>
        public List<KeyValuePair<string, string>> ExistingNamed { get; } = new List<KeyValuePair<string, string>>();

        public string Prefix(Guid id) => _prefixes.Shortest(id);

        public bool IsBatchRef(string name) => _newByRef.ContainsKey(name);

        public void AddNew(BatchItem item)
        {
            _newByRef[item.Ref] = item;
        }

        public void AddNickname(string nickname, BatchItem item)
        {
            if (!_byNickname.TryGetValue(nickname, out var list))
                _byNickname[nickname] = list = new List<BatchItem>();
            if (!list.Contains(item)) list.Add(item);
        }

        public void RemoveNickname(string nickname, BatchItem item)
        {
            if (nickname != null && _byNickname.TryGetValue(nickname, out var list))
                list.Remove(item);
        }

        // Nicknames this batch renames away, old → new, so a later entry still using the old name
        // is told what happened instead of just "not found".
        private readonly Dictionary<string, string> _renamed = new Dictionary<string, string>(StringComparer.Ordinal);

        public void Renamed(string oldNickname, string newNickname)
        {
            if (!string.IsNullOrEmpty(oldNickname) && oldNickname != newNickname)
                _renamed[oldNickname] = newNickname;
        }

        /// <summary>Objects (other than <paramref name="except"/>) that already carry this nickname and survive the batch.</summary>
        public List<BatchItem> NicknameHolders(string nickname, BatchItem except = null) =>
            _byNickname.TryGetValue(nickname, out var list)
                ? list.Where(i => i != except && !i.Deleted).ToList()
                : new List<BatchItem>();

        /// <summary>The live object a session ref points to, or null when it is unknown or gone.</summary>
        public IGH_DocumentObject SessionRefObject(string name)
        {
            if (!BatchRefRegistry.TryGet(DocumentId, name, out var id)) return null;
            var obj = Doc.FindObject(id, true);
            return obj != null && !InfraIds.Contains(id) ? obj : null;
        }

        /// <summary>
        /// Resolve an object name (ref from this batch or an earlier one, unique nickname, GUID,
        /// unique GUID prefix of 8+ characters). Returns null and adds an error when it does not name
        /// exactly one usable object.
        /// </summary>
        public BatchItem Resolve(string name, string path, List<string> errors, bool newAllowed = true)
        {
            var s = (name ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                errors.Add($"{path}: no object named");
                return null;
            }

            var found = new List<KeyValuePair<string, BatchItem>>();
            string staleRef = null;

            if (_newByRef.TryGetValue(s, out var fresh))
                found.Add(new KeyValuePair<string, BatchItem>("ref (this batch)", fresh));

            // A batch ref shadows a session ref of the same name only when that one's object is gone
            // (creating it is refused otherwise), so the session ref is consulted only without one.
            if (fresh == null && BatchRefRegistry.TryGet(DocumentId, s, out var refId))
            {
                var obj = Doc.FindObject(refId, true);
                if (obj != null && !InfraIds.Contains(refId) && _existing.TryGetValue(obj.InstanceGuid, out var refItem))
                    found.Add(new KeyValuePair<string, BatchItem>("ref", refItem));
                else
                    staleRef = s;
            }

            List<BatchItem> nicknamed = _byNickname.TryGetValue(s, out var nl) ? nl.ToList() : new List<BatchItem>();
            if (nicknamed.Count > 1)
            {
                var ids = string.Join(", ", nicknamed.Take(5).Select(i => i.IsNew ? $"'{i.Ref}' (new)" : Prefix(i.Obj.InstanceGuid)));
                errors.Add($"{path}: nickname '{s}' is used by {nicknamed.Count} objects ({ids}); name one by ref or GUID prefix");
                return null;
            }
            if (nicknamed.Count == 1)
                found.Add(new KeyValuePair<string, BatchItem>("nickname", nicknamed[0]));

            if (Guid.TryParse(s, out var guid))
            {
                if (_existing.TryGetValue(guid, out var byGuid))
                    found.Add(new KeyValuePair<string, BatchItem>("GUID", byGuid));
            }
            else if (BatchNames.LooksLikeGuidPrefix(s))
            {
                var hits = _prefixes.StartingWith(s)
                    .Select(g => _existing.TryGetValue(Guid.Parse(g), out var it) ? it : null)
                    .Where(it => it != null)
                    .ToList();
                if (hits.Count > 1)
                {
                    errors.Add($"{path}: GUID prefix '{s}' matches {hits.Count} objects; give more characters");
                    return null;
                }
                if (hits.Count == 1)
                    found.Add(new KeyValuePair<string, BatchItem>("GUID prefix", hits[0]));
            }

            var distinct = found.Select(f => f.Value).Distinct().ToList();
            if (distinct.Count > 1)
            {
                var ways = string.Join("; ", found.Select(f => $"{f.Key} → {Describe(f.Value)}"));
                errors.Add($"{path}: '{s}' names more than one object ({ways}). Use a GUID prefix, or rename one");
                return null;
            }

            if (distinct.Count == 0)
            {
                if (staleRef != null)
                {
                    errors.Add($"{path}: ref '{s}' points to an object that no longer exists (deleted or undone)");
                    return null;
                }

                if (_renamed.TryGetValue(s, out var renamedTo))
                {
                    errors.Add($"{path}: '{s}' is renamed to '{renamedTo}' earlier in this batch (set runs before insert, wires and groups); use '{renamedTo}' or the object's ref");
                    return null;
                }

                // Suggest only names that still lead somewhere: refs whose objects are gone are left out.
                var liveRefs = BatchRefRegistry.All(DocumentId)
                    .Where(kv => _existing.ContainsKey(kv.Value))
                    .Select(kv => kv.Key);
                var candidates = _newByRef.Keys
                    .Concat(liveRefs)
                    .Concat(_byNickname.Where(kv => kv.Value.Any(i => !i.Deleted)).Select(kv => kv.Key));
                var closest = BatchNames.Closest(s, candidates);
                errors.Add($"{path}: no object named '{s}'." + (closest != null ? $" Did you mean '{closest}'?" : ""));
                return null;
            }

            var item = distinct[0];
            if (item.IsNew && !newAllowed)
            {
                errors.Add($"{path}: '{s}' is created in this batch, but this step runs before objects are created");
                return null;
            }
            if (item.Deleted)
            {
                errors.Add($"{path}: '{s}' is deleted earlier in this batch");
                return null;
            }
            if (item.Failed)
            {
                errors.Add($"{path}: '{s}' could not be created (see {item.Entry?.Path}), so this entry is skipped");
                return null;
            }

            if (!item.IsNew && !found.Any(f => f.Key == "ref") && !ExistingNamed.Any(kv => kv.Key == s))
                ExistingNamed.Add(new KeyValuePair<string, string>(s, Prefix(item.Obj.InstanceGuid)));
            return item;
        }

        /// <summary>
        /// A target that names an object, or <c>obj.port</c>. When both readings resolve to
        /// different objects (an object nicknamed <c>a.b</c> and an object <c>a</c> with a port
        /// <c>b</c>), it's an error. Used by gh_batch's <c>set</c> targets and gh_inspect's tree.
        /// </summary>
        public (BatchItem Item, string Port) ResolveObjectOrPort(string target, string path, List<string> errors)
        {
            var wholeErrors = new List<string>();
            var whole = Resolve(target, path, wholeErrors);

            int dot = target.LastIndexOf('.');
            BatchItem split = null;
            var splitErrors = new List<string>();
            string objPart = null, portPart = null;
            if (dot > 0 && dot < target.Length - 1)
            {
                objPart = target.Substring(0, dot).Trim();
                portPart = target.Substring(dot + 1).Trim();
                split = Resolve(objPart, path, splitErrors);
            }

            if (whole != null && split != null && whole != split)
            {
                errors.Add($"{path}: '{target}' could be the object '{target}' or port '{portPart}' on '{objPart}'; rename one of them");
                return (null, null);
            }
            if (whole != null) return (whole, null);
            if (split != null) return (split, portPart);

            errors.AddRange(objPart != null ? splitErrors : wholeErrors);
            return (null, null);
        }

        /// <summary>
        /// Resolve a port on an object. A component needs the port named; a single-port object
        /// (parameter, panel, slider…) may be named alone.
        /// </summary>
        public IGH_Param ResolvePort(BatchItem item, string port, bool isInput, string path, List<string> errors)
        {
            var side = isInput ? "input" : "output";
            var list = isInput ? item.Inputs : item.Outputs;

            if (item.Obj is IGH_Component)
            {
                if (port == null)
                {
                    errors.Add($"{path}: {item.Label} is a component; name the {side}, e.g. '{NameOf(item)}.0'. "
                        + $"{(isInput ? "Inputs" : "Outputs")}: {StrictPortLookup.List(Identities(list))}");
                    return null;
                }
                var lookup = StrictPortLookup.Resolve(port, Identities(list), isInput, item.Label);
                if (!lookup.IsResolved)
                {
                    errors.Add($"{path}: {lookup.Error}");
                    return null;
                }
                return list[lookup.Index];
            }

            if (item.Obj is IGH_Param)
            {
                if (list.Count == 0)
                {
                    errors.Add(isInput
                        ? $"{path}: {item.Label} takes no input wires"
                        : $"{path}: {item.Label} has no output");
                    return null;
                }
                if (port == null)
                    return list[0];
                var lookup = StrictPortLookup.Resolve(port, Identities(list), isInput, item.Label);
                if (!lookup.IsResolved)
                {
                    errors.Add($"{path}: {item.Label} has a single port; write '{NameOf(item)}' or '{NameOf(item)}.0'");
                    return null;
                }
                return list[0];
            }

            errors.Add($"{path}: {item.Label} has no ports");
            return null;
        }

        public static List<ParamIdentity> Identities(IReadOnlyList<IGH_Param> list) =>
            list.Select(p => new ParamIdentity(p.Name, p.NickName)).ToList();

        public string ExistingLabel(IGH_DocumentObject obj) =>
            !string.IsNullOrEmpty(obj.NickName) && obj.NickName != obj.Name
                ? $"'{obj.NickName}' ({obj.Name})"
                : $"{Prefix(obj.InstanceGuid)} ({obj.Name})";

        public RectangleF Box(IGH_DocumentObject obj) => obj.Attributes?.Bounds ?? RectangleF.Empty;

        private string Describe(BatchItem item) =>
            item.IsNew ? $"new '{item.Ref}' ({item.Obj.Name})" : $"{Prefix(item.Obj.InstanceGuid)} ({item.Obj.Name})";

        private string NameOf(BatchItem item) =>
            item.IsNew ? item.Ref : (!string.IsNullOrEmpty(item.Obj.NickName) ? item.Obj.NickName : Prefix(item.Obj.InstanceGuid));
    }
}
