---
name: hyperceps
description: The companion skill of the Hyperceps Grasshopper plugin - how to build and edit Grasshopper definitions efficiently through its MCP tools (gh_canvas outline, gh_batch, gh_inspect tree, gh_document sweep). Load it at the start of any task on a Grasshopper canvas through Hyperceps - reading a definition, adding or changing components, values, wires or groups, fixing data trees, checking slider values, or rebuilding part of a definition - so the work is done in a few validated calls instead of hundreds of single-step calls.
---

# Working with Grasshopper through Hyperceps

This skill is for Hyperceps 1.1.0. The Grasshopper plugin and this skill are one product with one
version number; the plugin's connection note opens with its version. If the two differ, tell the
user to update the older part.

Every tool call is a full round trip: you write the call, wait, and re-read the whole conversation
with the result added. A definition built one component per call costs hundreds of round trips.
Hyperceps adds `gh_canvas(action='outline')`, which reads the canvas in one short call, `gh_batch`,
which applies a whole block of edits in one call, and `gh_inspect(action='tree')`, which checks data
trees in one call. Using them well is the difference between a few dozen calls and several hundred.

The user asks for changes in plain language. Choosing to batch is your job; they won't ask for it.

## Which tool

| Situation | Use |
| --- | --- |
| Two or more changes of any kind (add + wire, several values, a whole block, a fix touching several objects) | **one `gh_batch` call** |
| Exactly one change (one value, one wire, one move) | the single-step tool (`gh_canvas`, `gh_wire`) |
| Checking the item counts a batch produced | `options={"watch": ["OUTPUT", "obj.port"]}` in the same `gh_batch`: counts come back with the result |
| Checking branch paths after a change | `gh_inspect(action='tree', id='[...]')` - several outputs at once |
| Checking results at several slider (value list, toggle) values | `gh_document(action='sweep', id='SLIDER', values='[...]', watch='[...]')` - one call, control put back |
| Errors and warnings across the whole canvas | `gh_canvas(action='outline')`: its summary and `problems` list. `gh_inspect(action='status')` lists every component (tens of thousands of characters on a real definition); use it only with `category=` or when you need each component's state |
| Script code (C#, Python) | `gh_script` (`gh_batch` can create the script component; set its code and ports with `gh_script`, adding `solve=true` to get its results and errors back in the same call). To change a script, `edit` with only the changed passages |
| Reading the canvas | `gh_canvas(action='outline')`; then `group='NAME'` for how one group is wired inside (`group='network 1'` for a whole network on a canvas without groups, `group='ungrouped'` for the objects outside groups), or `id='NAME'` for what is around one object |

If a result carries a `"nudge"` field, you have made several single-step edits in a row: switch to
`gh_batch` for the rest.

## The working loop

Before step 1: if your app loads tools on demand (Claude Code does), load `gh_canvas`, `gh_batch` and
`gh_inspect` in one step, and `gh_script` too when there is script work. A separate step for each tool
costs a turn, and loading tools you won't use (the `rhino_*` tools are long) makes every later turn
longer.

1. **Read once.** `gh_canvas(action='outline')` gives every group with what flows in and out of it, the
   sliders and the errors. Open only the groups you will work with: `outline` with `group='POINTS'`
   (a JSON list for several, in the same call). On a canvas without groups, open the network you will
   work in with `group='network 1'` (`'network'` when there is only one); when most objects sit
   outside groups, `group='ungrouped'` lists them. Either gives every object in one call, instead of
   one `id=` view after another. Note the names of the objects you will connect to:
   every name the outline shows (a nickname or a short id) works as an id in every tool.
2. **Plan one block.** A block is one stage that makes something (typically 20-80 objects): its inputs
   (existing objects), its components, the port settings, the parameter that carries its result, and
   its group.
3. **Send the real batch, not a dry run of it.** gh_batch checks every name and port before changing
   anything: a batch with an error changes nothing and lists every problem, just as a dry run would,
   and a batch without errors is applied in the same call. A dry run followed by the same batch means
   writing the whole block twice. Use `options={"dryRun": true}` only to try a few guessed type names
   or ports before you write a large block.
4. **Send the block as one `gh_batch`**, with `options={"watch": ["PAIRS"]}` naming the outputs
   whose counts tell you it worked (the block's result parameter, a checker script's output).
5. **Read the result.** `ok` / `changed` / `solved`, then `errors` (if any, nothing changed - fix them
   all and resend), `watch` (item counts after the solve), `warnings`, `runtime` (errors and warnings
   from new objects and from existing objects downstream whose messages changed) and `hints`.
6. **Check the trees** of the outputs whose branch paths matter (matching another stream, a path
   mismatch): `gh_inspect(action='tree', id='["item.0", "PAIRS"]')`.
7. **Fix with a small follow-up batch** that reuses the refs from step 4.
8. **Sweep a slider when the request is about results across its values**, or when you changed logic
   that an existing slider drives (a count, a spacing, a branch it selects):
   `gh_document(action='sweep', id='DIVISION COUNT', values='[5,7,9]', watch='["DIAGONALS"]')` gives one
   line per value (item counts, a short preview of text and number outputs, errors) and puts the
   slider back. A value you set, or a slider you just added, needs no sweep: step 4's `watch` already
   shows the result at that value. Never set a slider by hand several times to test it, and never
   leave it changed.
9. Next block. After a section of work, check for errors with `gh_canvas(action='outline')` (summary and
   problems), not `gh_inspect(action='status')`, which lists every component.

Don't add components one by one "to see what happens"; a small dry run with just the guessed types
answers that in one call.

## Writing a batch

```json
{
  "components": [{"ref": "...", "type": "..."}],
  "set":        [{"target": "...", "value": ...}],
  "insert":     [{"ref": "...", "on": ["SRC.0", "DST.0"]}],
  "wires":      [["output", "input"]],
  "disconnect": [["output", "input"]],
  "delete":     ["..."],
  "groups":     [{"name": "...", "members": ["..."]}],
  "options":    {"watch": ["..."]}
}
```

Entries always run in this order, whatever order you write them in:
**delete, disconnect, create, set, insert, wires, groups, then one solve.** Consequences:
- An object deleted in the batch can't be used later in it.
- An object renamed by `set` must be named by its new nickname (or its ref) in `insert`, `wires` and
  `groups`.
- `insert` needs a wire that exists before the batch.

### Creating objects

Every entry needs a lowercase `ref` (letters, digits, `_`, `-`) and one of:

| Field | Creates |
| --- | --- |
| `"type": "List Item"` | a component by its exact name. Also `"Sets/List Item"`, `"Sets/List/List Item"` or a GUID. Never matched loosely: a near miss is an error with suggestions |
| `"param": "Curve"` | a parameter object from the Params tab |
| `"panel": "0\n2\n4"` | a panel; each line is one item (`"multiline": true` makes the whole text one item) |
| `"slider": {"min": 1, "max": 20, "value": 8, "decimals": 0}` | a number slider |
| `"valueList": {"items": [{"name": "A", "value": "1"}], "selected": "A"}` | a value list |
| `"toggle": true` | a boolean toggle |

Optional: `nickname`, `inputs` / `outputs` (variable-parameter counts: Merge, Entwine...),
`addOutputs` (List Item's `Item +1`: `"addOutputs": 1`), `inputModifiers` / `outputModifiers`.
Leave out `x`/`y`: the batch lays new objects out left to right, right of what feeds them, and inserted
components midway along their wire.

"Line" and "Circle" are both a component and a parameter; write `"Curve/Line"` or use `"param": "Line"`.
A deprecated type is used only when it is the only match (you get a warning). A name that a plugin
shares with a built-in component (Offset Curve in Pufferfish) means the built-in one; the warning
names the plugin's (`'Pufferfish/Offset Curve'`) in case you wanted that.

### Naming objects and ports

An object is named by:
- a **ref** from this batch or an earlier batch on the same document (refs last for the Grasshopper
  session),
- a **unique nickname** (refs and nicknames are case-sensitive),
- a **GUID** or a unique **GUID prefix** of 8+ characters (the short ids `outline` shows).

The same names work as the `id` in **every** Hyperceps tool, not only `gh_batch`: after creating a
script component with `{"ref": "checker", "type": "Python 3 Script"}`, edit it with
`gh_script(action='set', id='checker', ...)`; inspect `gh_canvas(action='info', id='9ac1d04b')`; wire
`gh_wire(action='connect', sourceId='LOWER CRVS', targetId='item', targetParam='L')`. Group actions
also take a group's name. No need to look up full GUIDs.

A port is `"obj.port"` (split on the last dot): a 0-based index, the port's name or its nickname,
matched exactly (case is ignored only when that is unambiguous). In a wire the first item is always an
output and the second an input. A single-port object (parameter, panel, slider, toggle, value list)
can be named alone: `"SEPARATOR"` is `"SEPARATOR.0"`. Explicit form: `{"obj": "v1.2", "port": "L"}`.

### Changing existing objects (`set`)

```json
{"target": "item.i", "value": 2}
{"target": "pt.0", "value": {"point": [0, 0, 3000]}}
{"target": "COUNT", "slider": {"min": 1, "max": 20, "value": 8}}
{"target": "MODE", "value": "B"}
{"target": "INDICES", "panel": "0\n2\n4"}
{"target": "cull", "inputModifiers": {"L": "graft"}}
{"target": "merge", "inputs": 4}
{"target": "NEW OUTPUT", "nickname": "PAIRS"}
```

Values: numbers, booleans, text, `{"point": [x,y,z]}`, `{"vector": [...]}`, `{"domain": [a,b]}`, a
flat list of these, or `null` to clear. A value on an input that is also wired is ignored by
Grasshopper (you get a warning). Port settings in `set` replace that port's settings (`[]` clears).
Reducing a port count is refused while the port is wired.

### Wires, inserts, disconnects, deletes, groups

- `wires`: `[["SRC.0", "dst.L"], ...]`. A wire that already exists is skipped with a warning.
- `insert`: `{"ref": "cull", "on": ["POINTS.0", "POINT OUT.0"], "in": "L", "out": "L"}` puts a new
  component into an existing wire, keeping the wire's place among the input's sources.
- `disconnect`: `[["SRC.0", "dst.L"]]`; `["*", "dst.L"]` removes every wire into that input.
- `delete`: `["OLD BLOCK OUTPUT", "9ac1d04b"]`.
- `groups`: `{"name": "PAIRS", "color": "#DCEDC8", "members": [...]}` creates;
  `{"extend": "PAIRS", "members": [...]}` adds. To reuse a colour the file already has, pass the
  `#RRGGBB` that `outline` (or `group_list`) shows - the new group takes that group's transparency too.

## Conventions

1. **Never rename native components.** Give custom script components a descriptive name.
2. **Capitalised nicknames on the parameters at a block's edges** (the ones that hand data in or out:
   `LOWER CRVS`, `PAIRS`) **and on input panels and sliders**, saying what they hold, with units or
   meaning in brackets: `OFFSET (m)`. These are the handles you and later agents find things by.
   Refs stay lowercase.
3. **Each new block in a group named in capitals after what it makes.** Check the group colours in
   the outline and reuse the colour the file already uses for that kind of group; otherwise one
   colour per block.
4. **Data flows left to right**, new blocks right of what feeds them. Leave out x/y.
5. **Port settings instead of Graft Tree, Flatten Tree, Simplify Tree and Reverse List components**
   (`inputModifiers` / `outputModifiers`). Keep a component only for what a setting can't express:
   graft and flatten can't both be on one port, so "flatten then graft" is flatten on the upstream
   output plus graft on this input.
6. **A list of values is one panel, one value per line** - not a comma list plus Text Split.

Don't convert existing Graft/Flatten components or Text Splits unless the user asks you to tidy up.

## Data trees

Decide before wiring: should two lists pair item by item (same structure) or combine every A with every
B (graft one of them)? Then check with the tree action:

```
gh_inspect(action='tree', id='["part.0", "grafted", "PAIRS"]')
→ part.0:  branches 3, items 6, depth 2, perBranch 2,  paths ["{0;0}..{0;2}: 2 each"]
→ grafted: branches 6, items 6, perBranch 1,          paths ["{0;0}..{0;5}: 1 each"], notes ["one item per branch (grafted shape)"]
```

| Symptom in the result or tree | Likely cause | Fix |
| --- | --- | --- |
| Expected N×M results, got max(N, M) | both inputs flat on the same path (longest-list matching) | graft one input (`inputModifiers: {"B": "graft"}`) |
| `notes`: paths have different depths | inputs from sources with different histories | simplify on the input, or a Shift Paths component |
| `notes`: one item per branch, where a list operation expected lists | grafted too early | remove the graft (`set` with `inputModifiers: {"i": []}`) or move it downstream |
| Two inputs with `{0;0}..{0;n}` and `{0}` | one side grafted, the other not | graft the other side too, or flatten the grafted one if pairing is wanted |
| `emptyBranches` or `nulls` | culled items, or List Item past the end of a list | check indices (List Item `W` wrap), Cull patterns |
| `notes`: indices Simplify would remove | extra depth from upstream components | simplify the output (`outputModifiers: {"0": "simplify"}`) |
| `solved: false` on a tree | solver off or object not computed | tell the user; don't turn the solver on yourself |

## Worked examples

### 1. Building a block

Index pairs for the lower curves: a list of indices, each picked from the lower curves, one
result per index.

```json
gh_batch({
  "components": [
    {"ref": "idx",  "panel": "0\n0\n2\n2\n4\n4\n6\n6\n8\n8", "nickname": "LOWER CURVE INDEX"},
    {"ref": "item", "type": "List Item", "addOutputs": 1, "inputModifiers": {"i": "graft"}},
    {"ref": "out",  "param": "Curve", "nickname": "PAIRS"}
  ],
  "wires": [["LOWER CRVS", "item.L"], ["idx", "item.i"], ["item.0", "out"]],
  "groups": [{"name": "PAIRS", "members": ["idx", "item", "out"]}],
  "options": {"watch": ["PAIRS"]}
})
→ "watch": ["PAIRS 10 in 10 branches"]
```

No Text Split (the panel already gives one item per line), no Graft Tree (graft is a setting on
`item.i`), and one call builds the block and checks it: 10 items in 10 branches is one pair per index.
When the branch paths themselves matter, follow with `gh_inspect(action='tree', id='PAIRS')`.

### 2. Inserting into a wire

Skip every other point, on the existing wire from `POINTS` to `POINT OUT`:

```json
gh_batch({
  "components": [
    {"ref": "cull", "type": "Cull Pattern"},
    {"ref": "pat",  "panel": "true\nfalse", "nickname": "SKIP EVERY OTHER"}
  ],
  "insert": [{"ref": "cull", "on": ["POINTS.0", "POINT OUT.0"], "in": "L", "out": "L"}],
  "wires":  [["pat", "cull.P"]]
})
```

### 3. Swapping a component

Replace a `Larger Than` (its id starts `3f2a9c1e`) with `Smaller Than`, keeping its wiring. Read its
wires first with `gh_canvas(action='outline', id='3f2a9c1e', depth=1)`: its line says what feeds it
(`A ← MAX LENGTH · B ← LIMIT (m)`) and the line after it says it feeds a Gate And whose id starts
`7be21c04` (`A ← 3f2a9c1e.>`, the output's nickname). Then:

```json
gh_batch({
  "delete": ["3f2a9c1e"],
  "components": [{"ref": "lt", "type": "Smaller Than"}],
  "wires": [["MAX LENGTH", "lt.A"], ["LIMIT (m)", "lt.B"], ["lt.0", "7be21c04.A"]]
})
```

The delete runs first, so the new component's wires can't clash with the old ones.

### 4. Fixing a path mismatch

A comparison gives one result instead of one per curve: `gh_inspect(action='tree')` shows `{0}: 12` on
input A's source and `{0}: 1` on B's, so every curve is compared with a single value. That was intended
(longest list), but the user wants every curve compared with every limit: graft B.

```json
gh_batch({"set": [{"target": "3f2a9c1e", "inputModifiers": {"B": "graft"}}]})
gh_inspect(action='tree', id='3f2a9c1e')
```

In older definitions the same fix often appears as `Maximum -> Graft Tree -> Larger Than.B`. When the
user asks to tidy that up: one batch deletes the Graft Tree, wires Maximum straight into
`Larger Than.B` and sets `{"B": "graft"}`.

## Reading the outline

```
gh_canvas(action='outline')
→ summary:  "342 objects · 19 groups · 0 errors · 0 warnings"
→ controls: ["DIVISION COUNT = 8 (5..9) [INPUTS]", ...]
→ groups:   ["GRID LINES · #B8D8E8 · 11 objects · in ← BOUNDARY CURVE, DIVISION COUNT · out: GRID LINES (28) → UPPER CURVES, LOWER CURVES", ...]
gh_canvas(action='outline', group='CROSS LINES')
→ objects:  ["267acc87 Panel \"FIRST OFFSET\" = \"-99 | 0\" (2 lines)",
             "fd89dbf9 List Item · L ← LOWER CRVS [LOWER CURVES] · i ← dd74af1f.R (graft)", ...]
```

- Groups come in data-flow order. `in ←` names what feeds a group from outside; `out:` names what
  leaves it, with item counts and the groups it goes to.
- `remark: …` on an object's line is a remark its component added (a script's `AddRuntimeMessage`):
  information, not a problem, so it isn't counted in the summary or listed in `problems`.
- In a group's lines, `PORT ← SOURCE` is a wire, `PORT = value` is what an unwired input holds,
  `(graft)` is a port setting, and `[GROUP]` says where an outside source sits. A child group is one
  line; open it with its own name.
- Without groups, the overview lists each separate network (largest first) with its inputs, named
  objects and ends. `group='network 2'` (or `'network'` when there is one; a JSON list for several)
  lists every object of that network in the group view's format, upstream first, up to 250 lines; any
  rest are named so you can open them with `id=`. `id='NAME'` (and `depth=`) shows the neighbourhood
  of one object.
- `group='ungrouped'` lists the objects outside any group the same way, with `in ←` and `out:` for
  what crosses into and out of groups. A real group with one of these names wins.
- The overview's `controls` and `problems` replace a separate `gh_inspect(action='status')` call when
  you only need to know whether anything is wrong.

### Sweeping a control

```
gh_document(action='sweep', id='DIVISION COUNT', values='[5,7,9]', watch='["DIAGONALS", "checker.out"]')
→ control:  "DIVISION COUNT (slider 5..9), was 8"
→ results:  ["5 → DIAGONALS 166 · checker.out 1 [\"lines 166, rule 166\"] · 0 errors, 0 warnings · 1.5 s", ...]
→ restored: "DIVISION COUNT = 8"
```

- `values`: slider numbers, value-list item names, or true/false. Leave it out for every value of a
  toggle, a value list, or a whole-number slider with at most 20 steps.
- `watch`: outputs as in `gh_inspect(action='tree')`; a component without a port watches all its
  outputs. Previews appear for text, number and true/false outputs of up to 3 items, so a temporary
  checker script that outputs one line of text can be swept too.
- A count with `(N empty)` includes N empty (null) items: they aren't real objects, so report the
  count without them (`GEOMETRY 248 (124 empty)` is 124 pieces of design geometry).
- Errors listed as `new:` appeared at that value and weren't there before the sweep.
- It is refused while the solver is off; tell the user rather than turning it on.
- On a slow definition (a value, or the control's own value, solving in over 5 s; or a sweep running
  over 15 s; or a value failing), the control is set back and Grasshopper re-solves it after
  the answer (`restored` says so and for how long). A sweep or batch sent during that re-solve waits
  for it (up to 15 s) and then runs, so send the next one as usual. A sweep stops after about 45 s;
  sweep the rest in another call.

### Temporary checker scripts

Set one up in three calls, in this order (no need for `gh_script(action='help')`):

```
gh_batch(components=[{"ref": "checker", "type": "Python 3 Script"}])
gh_script(action='configure', id='checker',
          inputs='[{"name": "crvs", "type": "Curve", "access": "list"}]',
          outputs='[{"name": "report"}]',
          code='#! python 3\nreport = f"{len(crvs)} curves"')
gh_batch(wires=[["POINTS", "checker.crvs"]], options={"watch": ["checker.report"]})
# → "watch": ["checker.report 1 [\"12 curves\"]"]: wiring solves, and watch returns the report
```

Input types: `int`, `double`, `bool`, `string`, `Point3d`, `Curve`, `Line`, ...; `access` is `item`,
`list` or `tree`. Configure the inputs before wiring them: the ports exist only after `configure`.

- To change it, send only the change:
  `gh_script(action='edit', id='checker', edits='[{"old": "tol = 0.01", "new": "tol = 0.001"}]', solve=true)`.
  Each `old` must occur exactly once (add a neighbouring line to make it unique); edits apply in
  order; if one fails, nothing changes and the error shows the closest line and the lines around it
  as they now read: correct the edit from those, don't read the script again. If the exact text isn't
  there, spaces and tabs at the ends of lines are ignored (indentation still has to match), and
  `notes` in the answer names the lines. Use `set` only for a new script or a rewrite. You wrote the code, so don't read it back with `gh_script(action='get')`: each
  read is a turn and repeats the whole script.
- To add or remove an input or output, call `configure` with only `inputs` / `outputs` (no `code`),
  then `edit` the lines that use it. `configure` with `code` sends the whole script again. In C#,
  `configure` adds the port to `RunScript`'s parameters itself: edit only the body, or your `old`
  text won't match the rewritten line.
- A script you didn't write: read it once, with `gh_script(action='get')`. `info` returns the whole
  source too, so don't call both; for only its ports and wiring, use
  `gh_canvas(action='outline', id='SCRIPT', depth=1)`.
- Keep scripts short: one script per stage, wired together, rather than one script that does
  everything. Rewriting or re-reading a script of a thousand lines costs minutes per round.
- `solve=true` solves once and returns `results` (each output's count and first values, as
  `gh_inspect(action='outputs')` shows them) and `messages` (errors, warnings and remarks): one call per round,
  no separate `outputs` call. It doesn't solve while the solver is off or inside a cluster editor
  (`notSolved` says why).
- To check it across a slider, `sweep` the slider: a short text output (one line of counts) shows up in
  the sweep's preview at every value.
- New code runs at the next solve (`solve=true`, or any batch), on whatever is wired to the script
  then. To point a checker at other inputs, rewire it first (`gh_batch`), then send the new code; never
  send both at once.
- A running script can't be stopped, not even with Escape: only closing Rhino ends it. Keep a
  checker's work bounded: check the input size first (`if len(a) > 2000: report = "too many inputs"`)
  and don't compare every item with every other over thousands of items.
- Delete it when you are done.

### Lichen Thalli

Groups shown as `THALLUS` come from the Lichen plugin: the author marked the block and described it.
Read `authorDescription` and `authorProperties` (in `outline` with `group=`) before working out a
block's purpose from its geometry. Hyperceps never changes a Thallus - its members, name, colour,
nesting or hidden T output - and refuses if asked. When new objects belong in a Thallus, put them in a
group of their own and tell the user which objects to add (they select them and use the Thallus's
right-click menu, "Add selected objects to Thallus").

## Reading results

```json
{"success": true, "ok": true, "changed": true,
 "created": {"idx": "3f2a9c1e", "item": "9ac1d04b", "out": "c41d8a90"},
 "counts": {"components": 3, "set": 0, "inserts": 0, "wires": 3, "disconnected": 0, "deleted": 0, "groups": 1},
 "solved": true, "watch": ["PAIRS 10 in 10 branches"],
 "errors": [], "warnings": [], "runtime": [], "hints": [], "ms": {"apply": 40, "solve": 300}}
```

- `ok: false` with `changed: false`: nothing happened. Every error names its entry (`wires[3]: ...`) and
  usually the fix (`Did you mean 'P' (Cull Pattern)?`). Fix them all and resend the whole batch.
- `rolledBack: true`: something failed while applying and the batch was undone; the canvas is as before.
- `solved: false` with a warning: the solver is off. The batch is applied; tell the user.
- `watch` (only with `options.watch`): one line per watched output: item count, `(N empty)` when N
  of those items are empty (null), branches when more than one, up to three short text or number values, `(not solved)` if it didn't solve. Up to 10
  outputs; a component named without a port watches each of its outputs.
- `created` lists up to 40 refs (`createdMore` counts the rest). You rarely need the ids: keep using refs.
- `hints`: e.g. a Graft Tree that could be a port setting, a Text Split that a one-per-line panel
  replaces. Act on them in the next batch.

## Things that surprise people

- **Save early**: if the definition has no file yet (`gh_document(action='info')` shows no
  `filePath`), ask the user where to save it before building, save once with
  `gh_document(action='save', path='...')`, then after each block with `gh_document(action='save')`.
  A stuck script means closing Rhino, and an unsaved definition is lost.
- **Busy Grasshopper**: while Grasshopper is solving or held by a script, a call waits up to 15 s,
  then answers that it is busy (what is solving, for how long) without doing anything. Check again
  with `gh_inspect(action='connection')`, which answers at once, rather than sending other calls. The
  Claude app may also give up on a call after about a minute (a batch whose own solve is that slow)
  while Grasshopper keeps working, so keep each solve well under a minute.
- **Merge grows by itself**: once its last input is wired, Grasshopper adds a spare empty input. Ask for
  the count you need; don't be surprised by one more.
- **Default nicknames are not unique**: many components share theirs ("Item", "Split", "A+B"). Name
  existing objects by GUID prefix unless they have a capitalised nickname.
- **Refs are per document and per session**: after Rhino restarts or the document is reopened, refs are
  gone. Re-read the canvas (`outline`) and use nicknames or GUID prefixes.
- **The solver**: gh_batch pauses it during the batch and solves once. If the user has turned it off,
  it stays off; never turn it on yourself without asking.
- **Undo**: one Ctrl+Z on the canvas reverts a whole batch. `gh_document` undo/redo stay disabled.
- **Limits**: 1,000 entries per batch by default (`options.maxOperations`, up to 5,000) and 60 s
  (`options.timeoutSeconds`). A block never needs that much; split big work into blocks anyway.
- **Ambiguous components across plugins**: if two plugins share a component name, the error lists both
  with their plugins; pick one with `Category/Name` or its GUID. (A plugin sharing a built-in's name
  doesn't count: the built-in one is used, with a warning.)
