# Best Practices

## Essential Rules

1. **Two or more changes: one `gh_batch` call.** It checks every entry before changing anything, pauses the solver, applies the batch as one undo step and solves once. Build large work one block per batch and check each block before the next: `options={"watch": ["BLOCK OUTPUT"]}` returns the item counts of the outputs you name in the same call. (Without gh_batch, disable the solver during bulk single-step edits: `gh_document(action='solver', enabled=false)` ... `enabled=true`.)

2. **Naming**: never rename native components — renamed components are hard to find and it is not the Grasshopper convention. DO give a capitalised nickname to the parameters at a block's edges (the ones that hand data in or out, e.g. `LOWER CRVS`) and to input panels and sliders (what they hold, units in brackets: `OFFSET (m)`); these are the stable handles to find things later. Name custom script components after their function. Put each block in a group named in capitals after what it makes.

3. **Validate before connecting**: gh_batch checks every port before wiring (use `options={"dryRun": true}` to check names alone). With single-step wiring, use `gh_wire(action='validate', ...)`

4. **Check for errors after changes**: `gh_canvas(action='outline')` — error and warning counts and a short problems list. `gh_inspect(action='status')` lists every component's state (long on a real definition); narrow it with `category=` when you need that detail

5. **Match data structures**: Check tree structures before connecting, and check changed outputs afterwards with `gh_inspect(action='tree', ...)`. See `gh://docs/data-trees`.

6. **Port settings, not tree components**: graft, flatten, simplify and reverse go on the port (gh_batch `inputModifiers` / `outputModifiers`), not as Graft Tree / Flatten Tree / Simplify Tree / Reverse List components.

7. **Lists of values in one panel**: one value per line (gh_batch panels have Multiline Data off, so each line is an item) instead of a comma list plus Text Split.

## Anti-Patterns

| Don't | Do |
|-------|-----|
| Guess component names | `gh_canvas(action='search', query='...')` |
| Ignore orange warnings | `gh_canvas(action='outline')` (problems list) |
| Chain without checking | Verify `result.success` |
| Flatten everything | Understand structure first |
| Create cycles | Design as DAG |

## Naming Conventions

| Type | Name |
|------|------|
| Native components | Never renamed |
| Block-edge parameters | Capitals, what they carry: "LOWER CRVS", "PAIRS" |
| Input sliders and panels | Capitals, what they hold (units/meaning in brackets): "OFFSET (m)" |
| Scripts | Function: "Calculate Area" |
| Groups | Capitals, after what the block makes: "UPPER CURVES" |
| gh_batch refs | lowercase: `split`, `item` |

## Expensive Operations (Cache with Data Dam)

- Boolean operations (Solid Union/Difference)
- Mesh operations on dense meshes
- Kangaroo simulations
- Curve/surface intersections

## Debugging

1. `gh_canvas(action='outline')` — find errors/warnings (the problems list names each one)
2. `gh_inspect(action='outputs', id='...')` — check branch/item counts
3. `gh_inspect(action='trace', id='...', direction='upstream')` — trace data flow
4. Add Panel components to visualize intermediate data

## Bake and Cleanup

```
gh_canvas(action='bake', id='...', layer='temp_preview')
// ... inspect, render ...
rhino_scene(action='layer_delete', name='temp_preview', deleteObjects=true)
```

## Error Recovery

| Error | Fix |
|-------|-----|
| "Component not found" | `gh_canvas(action='search')`, verify ID |
| "Connection failed" | `gh_canvas(action='info', id='...')` to check params |
| Solver loop | Disable solver, find cycle, fix, re-enable |
| Canvas frozen | `gh_document(action='solver', enabled=false)`, fix, re-enable |

See `gh://docs/common-errors` for comprehensive reference.
