# Hyperceps

**Claude builds and edits your Grasshopper definitions in a few validated calls instead of hundreds.**

**The bigger the job, the bigger the saving.** Given the same requests as Cordyceps 1.5.0, which it is
built on, Hyperceps used **2.6–2.8x fewer cost-weighted tokens and 2.1–2.6x fewer turns to
build a block** of a definition (two pairs, Hyperceps 1.0 and later), **2.2x fewer to explain a
164-object definition**, and **2.8x fewer to make four changes to a 918-line C# script**, in 3.7x less
time. A single small change costs about the same with either ([benchmark](#benchmark)).

Hyperceps is a Grasshopper plugin with a built-in guide for the AI agent. The plugin lets an agent
read and change the canvas through the [Model Context Protocol](https://modelcontextprotocol.io/); the
guide teaches it to do that efficiently: read the canvas once, build a whole block in one call, check
data trees, test slider values, edit scripts without resending them, and keep your naming conventions.
The guide is the `hyperceps` Claude skill, and the plugin hands it to the agent when it connects, so
there is nothing else to install.

Developed by Yibin Yang at Adrian Smith + Gordon Gill Architecture (AS+GG). Built on top of
[Cordyceps](https://github.com/brookstalley/cordyceps) by Brooks Talley (MIT License), which was
inspired by [grasshopper-mcp](https://github.com/alfredatnycu/grasshopper-mcp) by Alfred Chen.

## Install

1. **Plugin.** In Rhino 8, run `PackageManager`, search for **Hyperceps** and install it. Or download
   `Hyperceps.gha` from the [latest release](https://github.com/yang-yibinn/hyperceps/releases/latest),
   copy it into the Grasshopper Components folder (*File → Special Folders → Components Folder*),
   unblock it (Windows: right-click → Properties → Unblock), and restart Rhino.
2. **Connect** your AI app to `http://127.0.0.1:26930/mcp` (see [Connecting](#connecting)).
3. **Drop the component** on your canvas: *Params → Util → Hyperceps*. Then ask for what you want in
   plain language.

When the agent connects, the plugin asks it to read the guide before working on the canvas: the
installed `hyperceps` skill if your app has it, otherwise the plugin's own copy
(`gh_inspect(action='skill')`), which always matches the plugin's version.

<details>
<summary><strong>Optional: install the guide as a Claude skill</strong></summary>

The guide is also released as a Claude skill, `hyperceps-skill.zip`, with the same version number as
the plugin. Installing it is optional: Claude then loads it from its skill list instead of asking the
plugin. If an installed skill is for a different version, the agent reads the plugin's copy instead and
tells you once, so update or remove the skill when you update the plugin.

- **Claude Code** (including the Claude desktop app's Code tab): unzip it so that the `hyperceps`
  folder sits in `~/.claude/skills` (on Windows `%USERPROFILE%\.claude\skills`). Or download and
  unzip in one command, which also replaces an older copy. Windows (PowerShell):
  `Invoke-WebRequest "https://github.com/yang-yibinn/hyperceps/releases/latest/download/hyperceps-skill.zip" -OutFile "$env:TEMP\hyperceps-skill.zip" -UseBasicParsing; Expand-Archive -Force "$env:TEMP\hyperceps-skill.zip" "$env:USERPROFILE\.claude\skills"`
  macOS (Terminal):
  `mkdir -p ~/.claude/skills && curl -sL -o /tmp/hyperceps-skill.zip https://github.com/yang-yibinn/hyperceps/releases/latest/download/hyperceps-skill.zip && unzip -oq /tmp/hyperceps-skill.zip -d ~/.claude/skills`
- **Claude on the web or the desktop chat:** upload the zip as a custom skill under Skills in
  Claude's settings.
</details>

## What it adds to Cordyceps

| | |
| --- | --- |
| **`gh_batch`** | Many edits in one call: components, values, wires, inserts into existing wires, disconnections, deletions, groups and port settings. Everything is checked first and nothing changes if anything is wrong (every problem comes back at once, with a likely fix). The batch is one undo step, the solver is paused and solves once, and new objects are laid out left to right. `options.watch` returns the item counts of the outputs you name, so a block is built and checked in one call. |
| **`gh_canvas(action='outline')`** | The canvas in one short call: each group with what flows in and out (with item counts), the sliders, and the errors. `group=` shows how one group is wired, `group='network'` a whole network on a canvas without groups, and `id=` what is around one object. Every name it shows works as an id in every tool. |
| **`gh_inspect(action='tree')`** | Branch paths and item counts of any outputs, several at once, with notes on the usual data-tree mistakes. |
| **`gh_document(action='sweep')`** | A slider, value list or toggle tried at several values in one call: item counts (and how many are empty), a short preview, errors and solve time per value, and the control put back. |
| **`gh_script` `edit` and `solve=true`** | `edit` changes only the passages you send, so a long script is never sent again in full; `solve=true` returns the script's results and messages in the same call. |
| **A busy Grasshopper answers** | While Grasshopper is solving, a call waits up to 15 s, then says what is solving and for how long, instead of hanging until the AI app gives up. |
| **Names everywhere** | Every tool takes the names `gh_batch` uses: refs, unique nicknames and short GUID prefixes, not only full GUIDs. |
| **Agent guidance** | The guide (built into the plugin, and released as a Claude skill), the connection note, tool descriptions and in-result hints steer the agent to work this way without being asked. |
| **Lichen Thalli** | With [Lichen](https://github.com/yang-yibinn/Lichen) installed, the outline shows each Thallus with its author's description and properties. Hyperceps never changes a Thallus. |

The tool names, inputs and outputs Cordyceps already had are unchanged.

## Benchmark

Each kind of work was given to Cordyceps 1.5.0 and to Hyperceps as the same plain-language request,
on the same day, with the same Claude app and model (Opus 5.5, effort high), each from a fresh copy of
the same file. Every run below gave the correct result, checked against values measured beforehand.
The tasks, files and checks were chosen by Hyperceps's author.

| Work | Cost-weighted tokens | Turns | Grasshopper calls | Time |
| --- | --- | --- | --- | --- |
| Build a block of about 200 lines in a large definition, about 340 components (Hyperceps 1.0.0) | 4.2M → 1.6M, **2.6x** | 126 → 59, **2.1x** | 269 → 57, **4.7x** | 23:52 → 15:14, **1.6x** |
| Build a 45-object pattern block from a written description\* | 900k → 316k, **2.8x** | 45 → 17, **2.6x** | 110 → 15, **7.3x** | 6:45 → 3:00, **2.3x** |
| Explain a 164-object definition without groups | 327k → 147k, **2.2x** | 12 → 6, **2.0x** | 49 → 3, **16x** | 2:44 → 1:16, **2.2x** |
| Four changes to a 918-line C# script | 1.17M → 416k, **2.8x** | 32 → 24, about the same | 23 → 15, **1.5x** | 12:30 → 3:22, **3.7x** |
| Find and fix two planted faults\* | 396k → 277k, about the same | 27 → 21, about the same | 41 → 16, **2.6x** | 2:04 → 1:31, about the same |
| Write a new checker script, four requests in a row | 563k → 400k, about the same | 28 → 24, about the same | 33 → 14, **2.4x** | 3:47 → 3:01, about the same |
| One change: give a value its own slider | 139k → 124k, about the same | 10 → 8, about the same | 9 → 4, **2.2x** | 0:42 → 0:33, about the same |

Each cell reads Cordyceps → Hyperceps, then how many times less Hyperceps used. The first row is
Hyperceps 1.0.0 (October 2026); rows marked \* are from a first round in November with an earlier
build; the others are from a second round a day later, with 1.1's tools and guide.

**How to read it.** Cost-weighted tokens count each kind of token at its usual price ratio: fresh
input 1, cache writes 1.25, cache reads 0.1, output 5 (your account's prices may differ). That is
closer to what a session costs than the raw total, which is mostly cheap re-reads of the
conversation; by raw total the two block-building rows are 3.2x and 3.5x. Single runs vary: the same
request run on two days with the same Cordyceps differed by up to 17%, so a difference under about
1.5x is written "about the same".

**What it shows.** The saving grows with the work. Hyperceps always makes fewer Grasshopper calls,
but every turn also re-reads the agent's instructions, Hyperceps's guide among them, so a request of a
few turns gains little. Building a block and reading a large definition without groups are where it
pays most. For script work, look at time and written output: Cordyceps 1.5.0 has no way to change part of
a script, so its agent resends all of it for every change, while `edit` sends only the changed
lines (8.4x less written output on the 918-line script). Each change in this benchmark was right first time, the cheapest case for rewriting. In a
real session before `edit` existed, an agent grew a C# script to about 1,340 lines (40,000–70,000
characters) and rewrote it in full about 25 times; there the difference would be larger.

One more task, reading an item count at five slider values, isn't in the table: in the first round
both agents gave the same wrong count (they counted empty items). Hyperceps now says how many items
are empty, and in the second round its agent answered correctly.

An earlier pair on the first row's request, with a Hyperceps pre-release before `outline` and `sweep`, gave
2.2x fewer tokens (raw total), 1.6x fewer turns and 2.6x fewer calls; its cost-weighted figure isn't
available. These are single pairs, with one model and one app version at a time; that is their limit.

## Connecting

The server runs on port **26930** (change it with the component's `HttpPort` input).

<details>
<summary><strong>Claude Code</strong></summary>

```
claude mcp add --scope user --transport http hyperceps http://127.0.0.1:26930/mcp
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
| `gh_script` | C# and Python script components (`edit` changes only the passages you send; `solve=true` returns what the script produced) |
| `gh_inspect` | Connection probe, status, outputs, `tree`, data tracing, logs, and the agent's guide (`skill`) |
| `rhino_scene` | Rhino objects, layers, selection, visibility |
| `rhino_render` | Viewport, camera, materials, lighting, environments, render |

Guides for agents (the working guide, data trees, type system, patterns, common errors) are served as
MCP resources; you can read them in [`skills/hyperceps/SKILL.md`](skills/hyperceps/SKILL.md) and
[`src/Cordyceps/Knowledge/`](src/Cordyceps/Knowledge/). The design and the decisions behind it are in
[`docs/design.md`](docs/design.md).

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
- Any MCP client; the guide comes with the plugin. Built and measured with Claude (Claude Code and the
  Claude desktop app)

## Troubleshooting

| Problem | Solution |
| --- | --- |
| Plugin won't load | Check Rhino 8.21+. A manually installed `.gha` must be unblocked (Windows) or have its quarantine flag cleared (macOS); the Package Manager does this for you. |
| Can't connect | Is the Hyperceps component on the open canvas? Check the port (26930). |
| The agent works without the guide | Ask it to call `gh_inspect(action='skill')` first. If you installed the skill, keep it at the plugin's version, or remove it. |
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
plugin and the skill carry the same version. The skill in `skills/hyperceps` is also built into the
plugin, so the two are always the same text.

## Credits and license

- **Hyperceps:** Yibin Yang, at Adrian Smith + Gordon Gill Architecture (AS+GG). Built with Claude Code,
  directed by Yibin Yang, who set the design, the conventions and the benchmark and did the testing.
- **Cordyceps,** the plugin Hyperceps is built on: Brooks Talley
  ([github.com/brookstalley/cordyceps](https://github.com/brookstalley/cordyceps)).
- **grasshopper-mcp,** which inspired Cordyceps: Alfred Chen.

MIT License; see [LICENSE](LICENSE). [Changelog](CHANGELOG.md).
