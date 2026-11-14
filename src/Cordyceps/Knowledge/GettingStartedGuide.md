# Grasshopper via Hyperceps

Visual dataflow graph for parametric 3D. Components on canvas connect via wires. Data flows left-to-right.

Hyperceps comes with a Claude skill, `hyperceps`, released with the same version number. It holds the
full working method and conventions; load it if your app has it. If not, `gh_inspect(action='skill')`
(or the resource `gh://skill`) returns the same text from the plugin. Read it once, before working on the canvas.

## Tools (8 total)

Use `action='help'` on the unified tools for parameters.

**Grasshopper:**
- `gh_batch` — **many edits in one call**: create, set values, insert into wires, wire, disconnect, delete, group. Use it for any change with two or more steps
- `gh_canvas` — **outline** (read the canvas in one short call), components, values, groups, bake, zoomable params, data modifiers (one change per call)
- `gh_wire` — connect, disconnect, list, clear, validate
- `gh_document` — save, clear, solver, **sweep** (try a slider at several values in one call), snapshots, capture
- `gh_script` — get/set script code, configure params
- `gh_inspect` — status, outputs, trace, disconnected, tree (branch paths and counts), skill (the working guide)

**Rhino:**
- `rhino_scene` — objects, layers, selection, visibility
- `rhino_render` — display, camera, materials, environments, render

## Workflow

Every tool call is a round trip, so build a whole block in one `gh_batch` call, then check it:

```
gh_canvas(action='outline')                    # read what is there: groups, what flows between them, sliders, errors
gh_canvas(action='outline', group='POINTS')     # how one group is wired inside (names to wire to)
gh_batch(components=[...], set=[...], wires=[...], groups=[...])   # one block, one undo step, one solve
gh_inspect(action='tree', id='["ref.0", "OUTPUT NAME"]')           # check the outputs whose structure changed
gh_batch(...)                                  # fix with a small follow-up batch
```

`gh_batch` checks everything first (nothing changes if anything is wrong, and every error comes back at
once), pauses the solver for you, and lays out objects you give no x/y. Entries name each other by
`ref`; refs keep working in later batches. A batch with errors changes nothing, so send the real batch; use `options={"dryRun": true}` only to try a few guessed names before writing a large block.
The single-step tools below remain for one change at a time.

## Reading the canvas

`gh_canvas(action='outline')` describes the definition in a few lines: one per group with what flows in
and out (with item counts), the sliders, toggles and value lists, and any errors. Every name it shows
works as an id in every tool.

- `group='NAME'` (or a JSON list of names): one line per object, upstream first, each with what feeds
  it, its port settings and what it holds. A child group is one line; open it with its own name.
- On a canvas without groups the overview lists each separate network's inputs and ends instead;
  `group='network 1'` (`'network'` when there is one) lists every object of a network in the same
  format, in one call. `group='ungrouped'` does the same for the objects outside groups.
- `id='NAME', depth=2`: what feeds one object and what it feeds, a few wires each way.
- Groups shown as `THALLUS` are Lichen Thalli: the author's description and properties explain the
  block. Hyperceps reads Thalli but never changes them.

## Values

- `gh_canvas(action='set', id='...', value='...')` — slider/panel/toggle
- `gh_canvas(action='config', id='...', min=..., max=...)` — slider range

## Checking across slider values

`gh_document(action='sweep', id='DIVISION COUNT', values='[5,7,9]', watch='["DIAGONALS", "checker.out"]')`
sets each value, solves, and returns one line per value: each watched output's item count (with how
many are empty, and a short preview of text and number outputs), the canvas's errors and warnings (naming new ones) and the
solve time. It then puts the control back. Value lists take item names and toggles true/false; leave
`values` out to try every value of a toggle, a value list or a small whole-number slider. Everything is
checked first, and it is refused while the solver is off.

## Scripts

**Seeing what it does**: add `solve=true` to `gh_script(action='set')` or `action='configure'` to solve once and get the script's `results` (outputs' counts and first values) and `messages` (errors, warnings, remarks) in the same answer.

**Changing part of a script**: `gh_script(action='edit', id='...', edits='[{"old": "exact text", "new": "replacement"}]', solve=true)` replaces only those passages and writes the result as `set` does. Each `old` must occur exactly once; if any edit fails, nothing changes and the error names the closest line. Send only what changes: rewriting a long script to fix a few lines costs minutes per round.

**Updating script code**: `gh_script(action='set')` preserves connections for params whose names survive unchanged. If params are renamed or removed, the response includes `lostConnections` — an array directly usable with `gh_wire(action='connect')` to restore wiring.

**Configuring params**: `gh_script(action='configure')` preserves wires the same way — params matching by name keep their connections, and removed/renamed params come back in `lostConnections`. It's a partial update: omit a side (don't pass `inputs` or `outputs`) to leave it untouched, or pass `[]` to explicitly clear that side. So configuring only `inputs` no longer wipes your outputs.

## Groups

- `gh_canvas(action='group_create', name='...', ids='[...]', color='#FF6B6B')`
- `gh_canvas(action='group_list')` (`outline` shows the same groups with their colours, more briefly)

## Variable Parameters (ZUI)

- `gh_canvas(action='zoomable', id='...', operation='add', side='input')` — append a param (optional `index` for position)
- `gh_canvas(action='zoomable', id='...', operation='remove', side='input', index=2)` — remove a param
- `gh_canvas(action='zoomable', id='...', operation='set_count', side='input', count=4)` — set total param count

Operations: `add`, `remove`, `set_count`. Params: `side` ('input'/'output', default 'input'), `index`, `count`. Use `gh_canvas(action='info')` to list current params.

## Data Modifiers (Flatten / Graft / Simplify / Reverse)

The per-port right-click options, on any component parameter or free-floating param:

- `gh_canvas(action='modifier', id='...', side='input', param='B', mapping='graft')` — graft one input
- `gh_canvas(action='modifier', id='...', side='output', param='0', simplify=true)` — simplify an output
- `gh_canvas(action='modifier', id='...', side='input', param='B')` — READ the current state
- `gh_canvas(action='modifier', id='...', side='input', param='B', mapping='none', simplify=false, reverse=false)` — clear

`mapping`: `none` | `flatten` | `graft`. `simplify`/`reverse`: true/false. Partial update — whatever you
omit stays as it is, and passing none of the three reads instead of writing. `param` takes a name or a
0-based index and is unnecessary for a free-floating param. `gh_canvas(action='info')` reports
`modifiers` for every param, so state round-trips.

## Capture

- `gh_document(action='capture_canvas')` — GH canvas
- `gh_document(action='capture_viewport')` — Rhino 3D view

## Materials

- `rhino_render(action='material_library')` — list types
- `rhino_render(action='material_instantiate', type='Metal', name='Gold', color='#FFD700')`
- `rhino_render(action='material_apply', ids='[...]', material='...')`

## Object Types

| Role | Description |
|------|-------------|
| component | Processing node with I/O (usually what you want) |
| parameter | Data container (Params category) |
| input | Interactive control (Params/Input) |

**Ambiguous names**: "Circle" and "Line" match both a component and a parameter. Use `Category/Name` (`Curve/Line`), a GUID, or in gh_batch `"param": "Line"`.

**Naming**: never rename native components. Give a capitalised nickname to the parameters at a block's edges and to input panels and sliders (`OFFSET (m)`), and a descriptive name to script components.

## Layout

With gh_batch, leave out x/y: new blocks are placed left to right, right of what feeds them.
Otherwise: avoid backwards wires. Stack inputs vertically at x=50 (70px vertical gaps). Processing columns at x=300, 450, 600... (150px horizontal gaps).
Use `gh_canvas(action='validate')` for overlaps. See `gh://docs/canvas-layout`.

## Busy vs. dead

Every tool response carries a compact `status` block — `{document, ui, solving}` — telling you which
`.gh` file the call acted on and whether the host is healthy. If a call is slow or silent, call
`gh_inspect(action='connection')`: it reads cached state and never touches the document, so it answers
even when everything else is stuck. `ui: "blocked"` with `solving: true` means **wait**;
`modal_inferred: true` means a dialog is open in Rhino and **only a human can clear it**.

`gh_document(action='recompute')` refuses (with `solving: true`) while a solution is already
running, rather than queueing silently. See `gh://docs/common-errors`.

## Clusters

When the cluster editor is open, `gh_document(action='recompute')` and `gh_document(action='solver', enabled=true)` are safe to use — they preserve cluster input data. Grasshopper's native recompute (F5) is NOT safe inside the cluster editor — it will null out all cluster inputs.

## Resources

- `gh://skill` — the working guide (the `hyperceps` skill), from the plugin
- `gh://docs/data-trees` — essential for list/tree ops
- `gh://docs/common-errors` — error→fix reference
- `gh://docs/rendering` — bake→materials→viewport→capture
- `gh://component/{name}` — component I/O docs
- `gh://patterns/*` — linear-array, grid-array
