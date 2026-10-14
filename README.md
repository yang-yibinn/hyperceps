# Hyperceps

**Claude builds and edits your Grasshopper definitions in a few validated calls instead of hundreds.**

**Same job, a fraction of the effort.** Given the same request as Cordyceps, the plugin it is built
from, Hyperceps did the work with **2.2–3.2x fewer tokens, 1.6–2.1x fewer turns and 2.6–4.7x fewer
Grasshopper calls**, and the same result, in two like-for-like runs on a 340-component truss definition
([benchmark](#benchmark)).

Hyperceps is a Grasshopper plugin and a Claude skill that work together. The plugin lets an AI agent
read and change the canvas through the [Model Context Protocol](https://modelcontextprotocol.io/); the
skill teaches Claude to do it efficiently: read the canvas once, build a whole block in one call,
check data trees, test slider values, and keep your naming conventions.

Developed by Yibin Yang at Adrian Smith + Gordon Gill Architecture (AS+GG). Built from
[Cordyceps](https://github.com/brookstalley/cordyceps) by Brooks Talley (MIT License), which was
inspired by [grasshopper-mcp](https://github.com/alfredatnycu/grasshopper-mcp) by Alfred Chen.

## Two parts: install both

The plugin and the skill are released together and carry the same version number (1.0.0). When
Claude connects, the plugin asks it to load the skill, or to tell you once if it isn't installed.
Other AI apps still get the core rules from the plugin itself.

1. **Plugin.** In Rhino 8, run `PackageManager`, search for **Hyperceps** and install it. Or download
   `Hyperceps.gha` from the [latest release](https://github.com/yang-yibinn/hyperceps/releases/latest),
   copy it into the Grasshopper Components folder (*File → Special Folders → Components Folder*),
   unblock it (Windows: right-click → Properties → Unblock), and restart Rhino.
2. **Skill.** Download `hyperceps-skill.zip` from the same release.
   - **Claude Code** (including the Claude desktop app's Code tab): unzip it and copy the `hyperceps`
     folder into `~/.claude/skills` (on Windows `%USERPROFILE%\.claude\skills`). If you installed the
     plugin with the Package Manager, the skill is already on your computer; on Windows, in PowerShell:
     `Copy-Item -Recurse -Force "$env:APPDATA\McNeel\Rhinoceros\packages\8.0\hyperceps\1.0.0\skill\hyperceps" "$env:USERPROFILE\.claude\skills\"`
   - **Claude on the web or the desktop chat:** upload the zip as a custom skill under Skills in
     Claude's settings.
3. **Connect** your AI app to `http://127.0.0.1:26930/mcp` (see [Connecting](#connecting)).
4. **Drop the component** on your canvas: *Params → Util → Hyperceps*. Then ask for what you want in
   plain language.

## What it adds to Cordyceps

| | |
| --- | --- |
| **`gh_batch`** | Many edits in one call: components, values, wires, inserts into existing wires, disconnections, deletions, groups and port settings. Everything is checked first and nothing changes if anything is wrong (every problem comes back at once, with a likely fix). The batch is one undo step, the solver is paused and solves once, and new objects are laid out left to right. |
| **`gh_canvas(action='outline')`** | The canvas in one short call: each group with what flows in and out (with item counts), the sliders, and the errors. `group=` shows how one group is wired; `id=` shows what is around one object. Every name it shows works as an id in every tool. |
| **`gh_inspect(action='tree')`** | Branch paths and item counts of any outputs, several at once, with notes on the usual data-tree mistakes. |
| **`gh_document(action='sweep')`** | A slider, value list or toggle tried at several values in one call: item counts, a short preview, errors and solve time per value, and the control put back. |
| **Names everywhere** | Every tool takes the names `gh_batch` uses: refs, unique nicknames and short GUID prefixes, not only full GUIDs. |
| **Agent guidance** | The connection note, tool descriptions, in-result hints and the skill steer the agent to work this way without being asked. |
| **Lichen Thalli** | With [Lichen](https://github.com/yang-yibinn/Lichen) installed, the outline shows each Thallus with its author's description and properties. Hyperceps never changes a Thallus. |

The tool names, inputs and outputs Cordyceps already had are unchanged.

## Benchmark

The same plain-language request, run with Cordyceps and with Hyperceps on copies of a 340-component
truss definition from practice: build a block of 202 bracing lines from a written description, check
the logic against reference geometry on a Rhino layer, and drive it from a slider. Each pair was run
on the same day in the same Claude app; all four runs produced the correct 202 lines.

| | Cordyceps | Hyperceps | Fewer with Hyperceps |
| --- | --- | --- | --- |
| **Pair 1** (Hyperceps pre-release, before `outline` and `sweep`) | 70 turns, 14.9M tokens, 147 Grasshopper calls | 45 turns, 6.8M tokens, 57 calls | 1.6x turns, 2.2x tokens, 2.6x calls |
| **Pair 2** (Hyperceps 1.0.0) | 126 turns, 30.6M tokens, 269 calls, 24 min | 59 turns, 9.5M tokens, 57 calls, 15 min | 2.1x turns, 3.2x tokens, 4.7x calls |

Tokens are the total the model processed, counting the conversation it re-reads on every turn; that is
what an agent session costs. Single runs vary (Cordyceps took 70 and 126 turns for the same request),
which is why each comparison is a pair run under the same conditions.

## Connecting

The server runs on port **26930** (change it with the component's `HttpPort` input).

<details>
<summary><strong>Claude Code</strong></summary>

```
claude mcp add --transport http hyperceps http://127.0.0.1:26930/mcp
```
</details>

<details>
<summary><strong>Claude Desktop</strong></summary>

Claude Desktop uses stdio transport, so it needs the `mcp-remote` bridge (requires
[Node.js](https://nodejs.org/)). Add to `claude_desktop_config.json` (macOS:
`~/Library/Application Support/Claude/`, Windows: `%APPDATA%\Claude\`) and restart Claude Desktop:

```json
{
  "mcpServers": {
    "hyperceps": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:26930/mcp"]
    }
  }
}
```
</details>

<details>
<summary><strong>Cursor, VS Code and other HTTP clients</strong></summary>

```json
{
  "mcpServers": {
    "hyperceps": {
      "type": "streamable-http",
      "url": "http://127.0.0.1:26930/mcp"
    }
  }
}
```
</details>

## Tools

8 tools, each with an `action` parameter (`action='help'` lists them).

| Tool | What it does |
| --- | --- |
| `gh_batch` | Many canvas edits in one validated, undoable call ([contract](docs/gh-batch-schema.md)) |
| `gh_canvas` | `outline`, components, values, groups, baking, variable parameters, port settings (one change per call) |
| `gh_wire` | Connections |
| `gh_document` | Save, clear, solver, `sweep`, snapshots, canvas and viewport capture |
| `gh_script` | C# and Python script components |
| `gh_inspect` | Connection probe, status, outputs, `tree`, data tracing, logs |
| `rhino_scene` | Rhino objects, layers, selection, visibility |
| `rhino_render` | Viewport, camera, materials, lighting, environments, render |

Guides for agents (data trees, type system, patterns, common errors) are served as MCP resources; you
can read them in [`src/Cordyceps/Knowledge/`](src/Cordyceps/Knowledge/). The design and the decisions
behind it are in [`docs/design.md`](docs/design.md).

## Alongside Cordyceps

Hyperceps installs next to the original Cordyceps. Use one of the two on a canvas at a time.

| | Cordyceps | Hyperceps |
| --- | --- | --- |
| Plugin file | `Cordyceps.gha` | `Hyperceps.gha` |
| Canvas component | *Params → Util → Cordyceps* | *Params → Util → Hyperceps* |
| Default port | 26929 | 26930 |
| MCP server name | `cordyceps` | `hyperceps` |

## Requirements

- Rhino 8.21 or later (.NET 8). Tested on Windows; macOS should work as it does for Cordyceps, but is
  untested
- Claude Code or a Claude app for the full experience (plugin and skill); any MCP client can use the
  plugin on its own

## Troubleshooting

| Problem | Solution |
| --- | --- |
| Plugin won't load | Check Rhino 8.21+. A manually installed `.gha` must be unblocked (Windows) or have its quarantine flag cleared (macOS); the Package Manager does this for you. |
| Can't connect | Is the Hyperceps component on the open canvas? Check the port (26930). |
| Claude says the skill isn't installed | Install the skill (step 2 above) and start a new session; skills load when a session starts. |
| Component not found | `gh_canvas(action='outline')` shows every object's name; names work in every tool. |

## Building and testing

```bash
dotnet build src/Cordyceps/Cordyceps.csproj -c Release   # Release only; lands in releases/Hyperceps.gha
dotnet test src/Cordyceps.Tests -c Release              # unit tests
```

`tests/test_hyperceps.py` runs the functional checks through the official MCP Python client against
a running Grasshopper. It only runs when the open document is the scratch file
`scratch/hyperceps-scratch.gh`, which it clears and rebuilds; never point it at a project. Set up with
Python 3.10+: `python -m venv .venv`, then `.venv\Scripts\pip install -r tests\requirements.txt`; run
`.venv\Scripts\python tests\test_hyperceps.py` (add `--skip-undo` to skip the one check that asks you
to press Ctrl+Z). The `thallus` check runs on a separate hand-made file with Lichen Thalli:
`--scratch scratch\hyperceps-thallus.gh --only thallus`.

Release files (Yak package, `Hyperceps.gha`, `hyperceps-skill.zip`) come from
`powershell -ExecutionPolicy Bypass -File scripts\package.ps1`, which builds, tests and checks that the
plugin and the skill carry the same version.

## Credits and license

- **Hyperceps:** Yibin Yang, at Adrian Smith + Gordon Gill Architecture (AS+GG). Built with Claude Code,
  directed by Yibin Yang, who set the design, the conventions and the benchmark and did the testing.
- **Cordyceps,** the plugin Hyperceps is built from: Brooks Talley
  ([github.com/brookstalley/cordyceps](https://github.com/brookstalley/cordyceps)).
- **grasshopper-mcp,** which inspired Cordyceps: Alfred Chen.

MIT License; see [LICENSE](LICENSE). [Changelog](CHANGELOG.md).
