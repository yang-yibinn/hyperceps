# Creating Parametric {geometry_type}

Build the whole thing in one `gh_batch` call: inputs, the geometry component, the wires and a group.
gh_batch checks everything first, pauses the solver, and applies it as one undo step with one solve.

## Step 1: Pick the components

Choose the geometry component by its exact name (check with `gh_canvas(action='search', query='...')`
if unsure, or send the batch with `options={"dryRun": true}`):
- Circle: `"type": "Curve/Circle"` (plain "Circle" is also a parameter, so name the category)
- Box: `"type": "Box"`
- Curve: `"type": "Interpolate"`

## Step 2: One batch

Sliders get a capitalised nickname saying what they control; the geometry component keeps its own name.
Leave out x/y — the batch lays the block out left to right.

```
gh_batch(
  components=[
    {"ref": "radius", "slider": {"min": 0, "max": 100, "value": 50, "decimals": 1}, "nickname": "RADIUS"},
    {"ref": "geo", "type": "Curve/Circle"}
  ],
  wires=[["RADIUS", "geo.R"]],
  groups=[{"name": "{geometry_type}", "members": ["radius", "geo"]}]
)
```

## Step 3: Check the result

The result lists errors and warnings from the new objects (`runtime`). If something is off, fix it with a
small follow-up batch that refers to the same refs (`"geo"`, `"radius"`) — they stay valid.
