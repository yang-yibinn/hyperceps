# Hyperceps design

Hyperceps lets an AI agent edit a Grasshopper canvas in a few validated calls instead of hundreds, and
steers the agent to work that way without being asked. It is built from Cordyceps by Brooks Talley
(MIT), whose tools stay as they were.

## Why

Cordyceps does one operation per call: add one component, set one value, wire one batch of
connections. Every call is a full round trip: the model writes it, waits, and re-reads the whole
conversation with the result added. Building a definition one component at a time costs hundreds of
round trips, and some results echo large JSON (a listing of every member GUID of every group). Cost and
time grow with every turn.

Hyperceps moves the many small steps into Grasshopper: one call describes a whole block, Grasshopper
applies it locally and answers in a few lines. The model still plans and debugs; it stops paying for
the round trips.

## Two parts

- **The plugin** (`Hyperceps.gha`): the MCP server, the tools, the guides served as MCP resources, and
  a connection note every client receives.
- **The Claude skill** (`skills/hyperceps`): the working method, the conventions, the data-tree
  mistakes to look for, and worked examples.

They are released together under one version number. The connection note opens with the plugin's
version and asks Claude to load the skill, or to tell the user once if it isn't installed; the skill
names the version it is for, and a unit test keeps the two equal.

## What it adds

| Addition | What it does |
| --- | --- |
| `gh_batch` | One JSON spec of components, values, wires, inserts, disconnections, deletions, groups and port settings; `options.watch` reads named outputs back after the solve. Contract: [gh-batch-schema.md](gh-batch-schema.md). |
| `gh_canvas(action='outline')` | The canvas in a few lines: groups in data-flow order with what crosses their borders, controls, problems; `group=` for one group's wiring, `id=` and `depth=` for an object's neighbourhood; networks for canvases without groups. |
| `gh_inspect(action='tree')` | Branch paths and item counts of several outputs at once, grouped into runs, with notes on common tree mistakes. |
| `gh_script` `edit` and `solve=true` | Change part of a script by sending only the changed passages, and get the script's results and errors back in the same call. |
| `gh_document(action='sweep')` | One slider, value list or toggle through several values: counts, previews, errors and solve time per value; the control is always put back. |
| Names everywhere | Every tool takes refs, unique nicknames and 8+ character GUID prefixes, by the same rules as `gh_batch`. |
| Agent guidance | Connection note, tool descriptions, in-result hints (a nudge after repeated single-step edits; a Graft Tree that could be a port setting; a Text Split a one-value-per-line panel replaces), the guides and the skill. |
| Lichen Thalli | Shown in the outline with the author's description and properties; never changed. |

## Decisions

| Topic | Decision |
| --- | --- |
| Relationship to Cordyceps | A separate plugin with its own assembly, plugin and component ids, port (26930) and MCP server name, so both install side by side. Existing tools keep their names, inputs and outputs; new behaviour comes as new tools, new actions and optional inputs. |
| Checking before changing | `gh_batch` resolves every name, port and value first and reports every problem at once; nothing changes unless everything is valid. A batch with errors therefore behaves like a dry run, so agents are told to send the real batch. |
| Undo and rollback | A batch is one Grasshopper undo step: one Ctrl+Z reverts it. A failure while applying is rolled back by reversing the batch's own recorded changes, not by snapshot/revert, which would replace the open file with an unsaved copy. |
| Solver | Paused during a batch, solved once at the end, left as the user had it. Sweep refuses while the solver is off rather than turning it on. |
| Names | Strict: exact names, `Category/Name` or GUIDs for component types, never a loose match; a near miss is an error with suggestions. Ambiguous names list their candidates, except a name shared by one built-in component and only plugin ones: the built-in one is used, with a warning naming the plugin's. |
| Busy Grasshopper | A call that finds Grasshopper solving, not responding or busy with another call waits up to 15 s, then stops before reaching it and says what is solving and for how long. Before, it waited until the AI app gave up (about a minute) with no explanation, and a stuck script cost minutes of blind retries. |
| Port settings | Graft, flatten, simplify and reverse are port settings, not Graft Tree / Flatten Tree / Simplify Tree / Reverse List components. |
| Lists of values | One panel, one value per line, not a comma list plus Text Split. |
| Compact results | No per-wire echo, every list capped, typical results well under 4 KB. |
| Conventions | Live in the skill, not in plugin code: never rename native components; capitalised nicknames on block-edge parameters and input panels and sliders; one group per block, named in capitals; data flows left to right. |
| Lichen Thalli | Read through Lichen's fixed component ids and saved field names, with no dependency on Lichen. Membership, names, colours and the hidden output are Lichen's to manage: Hyperceps refuses to change them, in `gh_batch` and in the single-step tools. |

## Testing

- **Unit tests** (`src/Cordyceps.Tests`): the host-free parts (batch parsing and rules, tree summaries,
  outline, sweep planning, skill pairing).
- **Functional checks** (`tests/test_hyperceps.py`, against a running Grasshopper on a scratch file
  only): a small chain builds and solves; strict name resolution; a bad port rolls back completely;
  variable inputs and outputs; panels, sliders, value lists and toggles; port settings; inserts into
  existing wires; addressing by GUID, prefix and nickname; refs across batches; groups; one Ctrl+Z
  reverts a batch; the solver is left as it was and solves once; 300 components and 500 wires in under
  5 seconds; tree, outline, sweep, script solving and editing, batch watch; a busy Grasshopper
  answers within about 15 s; saving without a path, long image paths, built-in names over plugin
  ones; the agent guidance reaches the client; every existing tool still works; Lichen Thalli are
  read and never changed.
- **Behaviour checks:** ordinary requests that don't mention batching lead the agent to `gh_batch`;
  port settings instead of tree components; the nudge after repeated single-step edits.
- **Benchmark:** the same request with Cordyceps and with Hyperceps, measured from each session's
  transcript (turns, total tokens processed, Grasshopper calls, time), run in pairs on the same day and
  app. Results are in the README.

## Later

- `verify_layer`: compare an output with reference geometry on a Rhino layer, only when the user asks
  for such a check.
- `find_components`: resolve many component names in one call.
