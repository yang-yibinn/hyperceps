# gh_batch contract

The exact contract of `gh_batch` in Hyperceps 1.0.0: arguments, order of operations, names, values,
results and errors. The design behind it is in [design.md](design.md); the decisions that shaped the
details are listed in [Decisions](#decisions), and the additions to the first design in
[Additions](#additions-to-the-first-design).

## 1. The call

The tool's arguments **are** the batch spec. There is no wrapper object and no `action` parameter:

```json
gh_batch({
  "components": [ ... ],
  "set":        [ ... ],
  "insert":     [ ... ],
  "wires":      [ ... ],
  "disconnect": [ ... ],
  "delete":     [ ... ],
  "groups":     [ ... ],
  "options":    { ... }
})
```

Every field is optional and defaults to empty. Entries always run in this order, whatever order
they are written in:

**delete → disconnect → create components → set → insert → wires → groups → solve**

Everything is checked before anything changes (see [Validation](#6-validation-and-errors)).

## 2. Naming things

### Component types (`type`)

| Written as | Meaning |
| --- | --- |
| `"List Item"` | Exact name, ignoring case |
| `"Sets/Merge"` | Category / name |
| `"Sets/Tree/Merge"` | Category / subcategory / name |
| `"3cadddef-1e2b-4c09-9390-0e8f78f7609f"` | Component GUID |

One rule, never loose: exact name (ignoring case) among non-deprecated components; exactly one
match is used; a deprecated component counts only when it is the only match. Anything else is an
error listing every candidate (name, category, plugin, GUID).

Checked on this machine:

| Name | Result |
| --- | --- |
| `Merge` | Sets/Tree/Merge (the other "Merge" is deprecated) |
| `Addition` | Maths/Operators/Addition (five deprecated ones ignored) |
| `Line`, `Circle` | **Error**: both a component (Curve/Primitive) and a parameter (Params/Geometry) exist. Write `Curve/Line`, or use `"param": "Line"` |

### Objects (`obj`)

Anywhere an object is named — wire ends, `set.target`, `delete`, group members:

| Form | Example | Rule |
| --- | --- | --- |
| ref | `split` | From this batch or an earlier batch on the same document |
| nickname | `BOTTOM CRVS` | Must be unique on the canvas. Groups are not searched |
| GUID | `9ac1d04b-…` | Full GUID |
| GUID prefix | `9ac1d04b` | 8 or more characters, must be unique |

- Refs and nicknames are **case-sensitive**, so lowercase refs never collide with capitalised
  nicknames. A near miss is offered as a suggestion, never used: `no object 'Bottom Crvs'. Did you
  mean 'BOTTOM CRVS'?`
- If a name resolves to two **different** objects (say a ref and a nickname), it is an error.
- Refs may use letters, digits, `_` and `-` only (no dots or spaces), so `split.0` is never
  ambiguous.
- The Hyperceps component, and anything wired to it, stays invisible, exactly as in the other
  tools.
- The single-step tools (gh_canvas, gh_wire, gh_script, gh_inspect) accept these same names wherever
  they take an object id; a full GUID behaves exactly as before. Group actions also take a group's
  name.

### Ports (`obj.port`)

- `"merge1.2"`, `"cull.L"`, `"item.i"`: the string splits on its **last** dot, so nicknames with
  dots still work. Explicit form: `{"obj": "v1.2", "port": "L"}`.
- The port is a 0-based index, a name or a nickname. An exact match wins; otherwise a match
  ignoring case is accepted **only if it is unique**. A partial match is never used, only suggested:
  `wires[3]: no input named 'Pattern' on 'cull' (Cull Pattern). Did you mean 'P' (Cull Pattern)?`
- The side is never guessed: in a wire the first item is an output and the second an input. (List
  Item has an input *and* an output nicknamed `i`.)
- A single-port object (a parameter, panel, slider, toggle or value list) may be named without a
  port: `"SEPARATOR"` means `"SEPARATOR.0"`.

## 3. Entries

### `components` — objects to create

Each entry needs a `ref` plus **one** of `type`, `param`, `panel`, `slider`, `valueList`, `toggle`.

| Field | Type | Meaning |
| --- | --- | --- |
| `ref` | string | Required, unique. Used by later entries and later batches |
| `type` | string | Component by name, path or GUID ([rules above](#component-types-type)) |
| `param` | string | Parameter object by name, searched in the Params tab only: `"Curve"`, `"Number"`, `"Line"` |
| `panel` | string | Panel with this text |
| `multiline` | bool | Panels only. `false` (default): each line is one item. `true`: the whole text is one item |
| `slider` | object | `{"min":5,"max":9,"value":8,"decimals":0}`. Range is set before value; the value must fall inside the range |
| `valueList` | object | `{"items":[{"name":"A","value":"1"}],"selected":0}`. `selected` is an index or an item name (default 0) |
| `toggle` | bool | Boolean toggle with this state |
| `nickname` | string | Only when you want one. Must be unique on the canvas |
| `x`, `y` | number | Canvas position (same coordinates as gh\_canvas add). Give both or neither; neither = auto-layout |
| `inputs`, `outputs` | integer | Port counts for variable-parameter components (Merge, Entwine, Addition…) |
| `addOutputs` | integer | Extra outputs on top of the default: `1` gives List Item's "Item +1" |
| `inputModifiers`, `outputModifiers` | object | Port settings: `{"i": "graft"}`, `{"T": ["flatten", "simplify"]}` |

Port-setting values: `graft`, `flatten`, `simplify`, `reverse` (one, or a list). `graft` and
`flatten` exclude each other. Script components (C#, Python) can be created by `type`; their code
and inputs are set with gh\_script, and `inputs`/`outputs` on them is an error pointing there.

### `set` — change new or existing objects

`target` names an object, or `obj.port` for an input's value. An entry may combine several
changes; they apply in this order: nickname → configuration (slider, value list, panel, port counts,
port settings) → value.

| Field | Applies to | Meaning |
| --- | --- | --- |
| `target` | — | Required. Object, or `obj.port` (always an **input** port) |
| `value` | input port, parameter object | Persistent value(s) — see below. `null` clears them |
| `value` | slider / toggle / value list / panel | The slider value, toggle state, selected item (by name or value), or panel text |
| `panel`, `multiline` | panel | Text, and the multiline setting (left unchanged if omitted on an existing panel) |
| `slider` | slider | Any of min/max/value/decimals; omitted ones stay as they are |
| `valueList` | value list | Replaces the items; `selected` as above |
| `nickname` | any object | Rename. Must be unique on the canvas |
| `inputs`, `outputs` | variable-parameter component | **Addition.** New port counts |
| `inputModifiers`, `outputModifiers` | component or parameter | **Addition.** Port settings on an existing object. Each listed port gets exactly the listed settings; `[]` or `"none"` clears them |

Value types for inputs and parameter objects:

| JSON | Becomes |
| --- | --- |
| `2`, `2.5` | Number (or Integer, if the input is an Integer input and the value is whole) |
| `true` | Boolean |
| `"text"` | Text, converted to the input's type when Grasshopper can (`"3"` → 3) |
| `{"point": [0, 0, 3000]}` | Point (`[x, y]` means z = 0) |
| `{"vector": [0, 0, 1]}` | Vector |
| `{"domain": [0, 1]}` | Domain |
| `[1, 2, 3]` | Several items (a flat list) |

A value that can't become the input's type is an error naming both:
`set[2]: input 'i' on item takes Integer; 2.5 isn't a whole number.`

Examples:

```json
{"target": "item.i", "value": 2}
{"target": "pt.0", "value": {"point": [0, 0, 3000]}}
{"target": "dom.0", "value": {"domain": [0, 1]}}
{"target": "tbl", "panel": "0,0,2,2", "multiline": false}
{"target": "COUNT", "slider": {"min": 1, "max": 20, "value": 8, "decimals": 0}}
{"target": "split", "nickname": "SPLIT INDEX"}
```

### `insert` — put a new component into an existing wire

```json
{"ref": "cull", "on": ["POSTS.0", "POST OUT.0"], "in": "L", "out": "L"}
```

| Field | Meaning |
| --- | --- |
| `ref` | A component created **in this batch** |
| `on` | The existing wire, `[output, input]`. It must exist, or it's an error |
| `in`, `out` | The ref's input that receives the wire and output that continues it. Default `0` |

The wire keeps its place among the input's sources, so Merge-style input order is preserved.

### `wires` — connections to add

`[["tbl.0", "split.0"], ["SEPARATOR", "split.1"]]` — always `[output, input]`. An input may have
several sources; they are added in the order written. A wire that already exists is skipped with a
warning.

### `disconnect` — connections to remove

Same shape as `wires`. The wire must exist, or it's an error. **Addition:** `"*"` on one side means
every wire into that input (`["*", "comp.x"]`) or out of that output (`["slider", "*"]`).

### `delete` — objects to delete

`["old_cull", "UNUSED", "9ac1d04b"]`: refs, nicknames, GUIDs or prefixes. Wires to deleted objects
go with them. Groups are not deleted here (use `gh_canvas(action='group_delete')`). Any later entry
that names a deleted object is an error. Deleting an object frees its ref, so the same batch may give
that ref to a new component (delete `tb_trim`, create a new `tb_trim`). The hidden T output of a
Lichen Thallus can't be deleted (see below).

### `groups` — create, or add to an existing group

```json
{"name": "PAIRS", "color": "#DCEDC8", "members": ["tbl", "split", "item", "out"]}
{"extend": "PAIRS", "members": ["cull", "pat"]}
```

| Field | Meaning |
| --- | --- |
| `name` | Create a new group with this name. If a group with this name already exists, it's an error suggesting `extend` |
| `extend` | Add to an existing group, named by its name (must be unique among groups), GUID or prefix |
| `color` | `#RRGGBB` (Grasshopper's usual group transparency, or the transparency of an existing group with the same colour, so reused colours match) or `#AARRGGBB`. With `extend`, recolours |
| `members` | Objects, named as anywhere else |

**Lichen Thalli are read-only.** A Lichen Thallus (shown as `THALLUS` by `gh_canvas(action='outline')`)
is a Grasshopper group with the author's description and properties and a hidden "Thallus Output"
component as its T output. gh\_batch refuses to `extend` a Thallus, and refuses the hidden output in
`delete` or in `members`, with an error saying how the user does it in Grasshopper. The same holds in
gh\_canvas (`group_add`, `group_remove`, `group_rename`, `group_color`, `group_delete`, `delete`,
`group_create`). Objects inside a Thallus can still be changed, wired and deleted as usual; Lichen
lists a deleted member as missing.

### `options`

| Option | Default | Meaning |
| --- | --- | --- |
| `atomic` | `true` | Any failure leaves the canvas exactly as it was. `false`: entries that fail checking, and entries that depend on them, are skipped and reported; the rest is applied (still one undo step). A failure while applying always rolls everything back |
| `dryRun` | `false` | Check and resolve only; change nothing |
| `solve` | `true` | Solve once at the end. A switched-off solver stays off and the result says so |
| `returnRuntimeMessages` | `true` | Errors and warnings from new objects, and from existing downstream objects whose state changed |
| `maxOperations` | `1000` | Safety limit: total entries across all lists. Hard ceiling 5,000 |
| `timeoutSeconds` | `60` | Safety limit on checking and applying (solve time excluded). Exceeding it rolls back. Hard ceiling 110 |

## 4. Undo, rollback and the solver

- One batch is one Grasshopper undo step: a single Ctrl+Z on the canvas reverts all of it.
- If anything fails while applying, gh\_batch reverses its own recorded changes (not
  snapshot/revert), so the file keeps its name and undo history.
- If a solve is running when the batch arrives, it waits for the solve to finish (up to the
  timeout) before changing anything.
- The solver is paused while applying. The batch solves only if `solve` is true **and** both the
  document's solver and Grasshopper's global solver lock are on. Their state is never changed.

## 5. Results

Every result has `success` and `ok` (always equal; `success` is what makes AI apps flag a failed
call). Lists are capped (errors 50, warnings 20, runtime 20, hints 5; messages cut at 200
characters). Wires are never echoed. The usual Cordyceps `status` block is added at the end.

**Success**

```json
{
  "success": true, "ok": true, "changed": true,
  "created": {"tbl": "3f2a9c1e", "split": "9ac1d04b", "item": "5be0712f", "out": "c41d8a90"},
  "counts": {"components": 4, "set": 0, "inserts": 0, "wires": 5, "disconnected": 0, "deleted": 0, "groups": 1},
  "solved": true,
  "errors": [], "warnings": [],
  "runtime": [{"ref": "item", "level": "Warning", "message": "..."}, {"obj": "POST OUT", "level": "Error", "message": "..."}],
  "hints": [],
  "ms": {"apply": 420, "solve": 1300}
}
```

`created` maps each ref to the shortest unique GUID prefix (8 characters unless longer is needed).
It lists up to 40 refs; `createdMore` counts the rest (refs stay valid in later batches, so the
GUIDs are rarely needed). Runtime messages name new objects by `ref` and existing ones by nickname
or GUID prefix (`obj`). `ms.apply` is checking plus applying; `ms.solve` is the solve.

**Failure** (nothing changed)

```json
{
  "success": false, "ok": false, "changed": false,
  "errors": [
    "wires[3]: no input named 'Pattern' on 'cull' (Cull Pattern). Did you mean 'P' (Cull Pattern)?",
    "components[1].type: 'Line' is ambiguous: Curve/Primitive/Line (component, 4c4e56eb), Params/Geometry/Line (parameter, 8529dbdf). Write 'Curve/Line' or use \"param\": \"Line\"."
  ],
  "warnings": []
}
```

Every error starts with the entry it is about (`wires[3]`, `components[1].type`) and gives a likely
fix where one exists.

**Dry run**

```json
{
  "success": true, "ok": true, "dryRun": true, "changed": false,
  "resolved": {"tbl": "Params/Input/Panel", "split": "Sets/Text/Text Split", "item": "Sets/List/List Item", "out": "Params/Geometry/Curve"},
  "existing": {"SEPARATOR": "1b2c3d4e", "BOTTOM CRVS": "8f9e0a1b"},
  "counts": {"components": 4, "set": 0, "inserts": 0, "wires": 5, "disconnected": 0, "deleted": 0, "groups": 1},
  "errors": [], "warnings": []
}
```

**Warnings and hints.** Warnings flag things that are allowed but probably unintended: a value set
on an input that is also wired (the wire wins), a duplicate wire, the solver being off. Hints are
the guidance's nudges, for example: `components[2]: Graft Tree could be a port setting: use
"inputModifiers": {"L": "graft"} on the component it feeds.` Existing components are never
converted.

## 6. Validation and errors

Phase 1 checks everything and reports **all** errors at once: types and missing plugins, refs and
object names, ports (including ports that only exist after `inputs`/`outputs`/`addOutputs` — new
objects are built and configured in memory first), value types, nickname uniqueness, slider ranges,
modifier combinations, wires that must exist, names that refer to objects deleted in the same batch,
and the limits. Phase 2 applies, only if phase 1 found nothing (or, with `atomic: false`, applies
the entries that passed).

**Missing plugins.** A plugin can be named only when the spec says which plugin a component comes
from:

- `"type": "Kangaroo2/Goals-Pt/Anchor"` with Kangaroo 2 not installed →
  `no installed plugin provides 'Kangaroo2'. It belongs to Kangaroo 2 (Daniel Piker): food4rhino.com/…`
  (from Cordyceps' list of known plugins; an unknown category just says no installed plugin provides it).
- A GUID that isn't installed → `no installed component has GUID …; its plugin may not be installed`.
- A bare name that doesn't exist (`"Anchor"`) can't be traced to a plugin, so the error is
  `no component named 'Anchor'` plus close names, and suggests writing `Category/Name`.

## 7. Plumbing (how the schema reaches the AI app)

Cordyceps' tool list generator only describes simple values (text, numbers, true/false). Two small
pieces make gh\_batch's nested spec visible to the AI app:

- **New file** `Core/McpInputSchemaAttribute.cs`: an attribute that points a tool at a JSON
  schema embedded in the plugin (`Tools/Batch/GhBatchSchema.json`).
- **Edit to `McpServer.cs`** (about 10 lines): when a tool carries that attribute, `tools/list`
  publishes the embedded schema instead of the generated one. Every other tool is unaffected.

The C# method still takes one text argument per top-level field. Cordyceps already passes an
object or array argument through as its JSON text, so no other server change is needed. A unit
test checks the published schema and the method's arguments never drift apart.

## 8. JSON Schema (as published in `tools/list`)

AI apps send every tool's schema with every request, so this one is deliberately lean: it gives
the structure and the field names, and leaves repeated shapes (port settings, wire ends) to short
descriptions. It is about 3,300 characters (roughly 950 tokens), the same size as gh\_canvas's
(3,100); all seven existing tools together are 12,900. The detailed rules in sections 2–6 are
enforced by the plugin and explained in its error messages, the tool description, the connection
note and the skill document (step 7).

```json
{
  "type": "object",
  "properties": {
    "components": {
      "type": "array",
      "description": "Objects to create. Each: unique ref + one of type|param|panel|slider|valueList|toggle.",
      "items": {
        "type": "object",
        "required": ["ref"],
        "properties": {
          "ref": {"type": "string", "description": "Local name for later entries and batches; letters, digits, _ -; lowercase by convention."},
          "type": {"type": "string", "description": "Exact name, 'Category/Name', 'Category/Subcategory/Name' or GUID. Never loose."},
          "param": {"type": "string", "description": "Params-tab object, e.g. 'Curve'."},
          "panel": {"type": "string", "description": "Panel text. A list is one value per line (no Text Split)."},
          "multiline": {"type": "boolean", "description": "Panel: false (default) one item per line; true whole text one item."},
          "slider": {"type": "object", "description": "{min,max,value,decimals}"},
          "valueList": {"type": "object", "description": "{items:[{name,value}], selected: index or name}"},
          "toggle": {"type": "boolean"},
          "nickname": {"type": "string", "description": "Only when wanted; unique. Capitals on key inputs/outputs."},
          "x": {"type": "number"},
          "y": {"type": "number", "description": "Omit x and y for auto-layout."},
          "inputs": {"type": "integer", "description": "Variable-parameter port counts (Merge, Entwine...)."},
          "outputs": {"type": "integer"},
          "addOutputs": {"type": "integer", "description": "Extra outputs, e.g. 1 = List Item 'Item +1'."},
          "inputModifiers": {"type": "object", "description": "{port: setting or [settings]}; graft|flatten|simplify|reverse, e.g. {\"i\":\"graft\"}."},
          "outputModifiers": {"type": "object"}
        }
      }
    },
    "set": {
      "type": "array",
      "description": "Change new or existing objects. target: object, or 'obj.port' for an input value.",
      "items": {
        "type": "object",
        "required": ["target"],
        "properties": {
          "target": {"type": "string"},
          "value": {"description": "number|bool|string|{point:[x,y,z]}|{vector:[x,y,z]}|{domain:[a,b]}, a list, or null to clear. Slider/toggle/value list/panel: value, state, item, text."},
          "panel": {"type": "string"},
          "multiline": {"type": "boolean"},
          "slider": {"type": "object"},
          "valueList": {"type": "object"},
          "nickname": {"type": "string"},
          "inputs": {"type": "integer"},
          "outputs": {"type": "integer"},
          "inputModifiers": {"type": "object", "description": "Listed ports get exactly these settings; [] clears."},
          "outputModifiers": {"type": "object"}
        }
      }
    },
    "insert": {
      "type": "array",
      "description": "{ref, on:[output,input], in, out}: put a new component into an existing wire. in/out default 0.",
      "items": {"type": "object", "required": ["ref", "on"]}
    },
    "wires": {
      "type": "array",
      "description": "[output, input] pairs. End = 'obj.port' (last dot splits; port = index, name or nickname), 'obj' for single-port objects, or {obj, port}. obj = ref, unique nickname, GUID or 8+ char GUID prefix.",
      "items": {"type": "array", "minItems": 2, "maxItems": 2}
    },
    "disconnect": {
      "type": "array",
      "description": "[output, input] pairs to remove; \"*\" on one side = all wires into that input / out of that output.",
      "items": {"type": "array", "minItems": 2, "maxItems": 2}
    },
    "delete": {"type": "array", "items": {"type": "string"}},
    "groups": {
      "type": "array",
      "description": "{name, color, members} creates; {extend: name|GUID, members, color} adds. color #RRGGBB or #AARRGGBB.",
      "items": {"type": "object"}
    },
    "options": {
      "type": "object",
      "description": "atomic (true), dryRun (false), solve (true), returnRuntimeMessages (true), maxOperations (1000), timeoutSeconds (60)."
    }
  }
}
```

## Decisions

1. **Refs and nicknames are case-sensitive**; component type names ignore case; ports ignore case only when that is unambiguous. This is what makes the convention
   "lowercase refs, capitalised nicknames" actually prevent clashes.
2. **A ref must not clash with an existing nickname** when it is created (error then, not later).
3. **Partial port names are never used**, only suggested. (The existing tools' port lookup does
   accept partial names; gh\_batch adds a strict check on top of it, so
   `'Pattern'` is suggested for `'P'`, never used.)
4. **Nickname lookups skip groups**, so a group and a parameter both called
   `PAIRS` work side by side. Groups are named in `groups` entries only.
5. **`value` on a wired input** is allowed with a warning (the wire wins). A **duplicate wire** is
   skipped with a warning. A **missing wire** in `disconnect` or `insert` is an error.
6. **Creating a group whose name already exists** is an error that suggests `extend`.
7. **`#RRGGBB` group colours get Grasshopper's usual transparency**; `#AARRGGBB` sets it exactly.
8. **`atomic: false`** means best effort: failed entries, and entries that depend on them, are
   skipped and reported; the rest is applied as one undo step. A timeout always rolls back.
9. **Limits are per call** (`maxOperations`, `timeoutSeconds`) with hard ceilings, rather than a
   new input on the Hyperceps component. One "operation" is one entry in any list.
10. **Results include `changed` and `solved`**, so an agent never has to infer
    whether the canvas was modified or solved.
11. **`insert` defaults `in` and `out` to port 0** and keeps the wire's position among the target
    input's sources.

## Additions to the first design

Each is small and makes common edits a single batch instead of a batch plus single-step calls.

| Addition | Why |
| --- | --- |
| `set.inputModifiers` / `set.outputModifiers` on existing objects | "Fix a path mismatch" usually means grafting an existing input |
| `set.inputs` / `set.outputs` on existing variable-parameter components | "Add another input to that Merge and wire X into it" in one batch. Reducing a count is refused if it would drop a wired port |
| `"*"` in `disconnect` | Replacing whatever feeds an input without first listing its wires |
| Bare object name for single-port objects (`"SEPARATOR"`) | Matches how people name panels and sliders; `"SEPARATOR.0"` still works |

## Not in gh_batch

Script code in a batch (use gh\_script), moving existing objects (use gh\_canvas move), persistent
values with more than one branch, and the later items in [design.md](design.md) (verify\_layer,
find\_components).
The compact canvas read is `gh_canvas(action='outline')` and the sweep is
`gh_document(action='sweep')`, both separate actions.
