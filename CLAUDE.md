# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## Hyperceps

**Hyperceps** is a Grasshopper plugin plus a Claude skill (`skills/hyperceps`) that let an AI agent
build and edit Grasshopper definitions in a few validated calls. It is built from Cordyceps by Brooks
Talley (MIT) and developed by Yibin Yang at Adrian Smith + Gordon Gill Architecture (AS+GG). The
design is in [`docs/design.md`](docs/design.md), the `gh_batch` contract in
[`docs/gh-batch-schema.md`](docs/gh-batch-schema.md).

- **Identity:** assembly `Hyperceps.gha`, component *Params → Util → Hyperceps* (nickname `HYP`),
  default port **26930**, MCP server name `hyperceps`. The C# namespace and source folders stay
  `Cordyceps` on purpose (a smaller diff from Cordyceps, easier to bring in its fixes).
- **Plugin and skill share one version number** (`<Version>` in `src/Cordyceps/Cordyceps.csproj` and the
  "This skill is for Hyperceps X.Y.Z" line in `skills/hyperceps/SKILL.md`); a unit test checks it.
- **Keep existing tools' names, inputs and outputs unchanged.** Add new behaviour as new tools, new
  actions or optional inputs. Put new code in new files; keep edits to existing files small.
- **Testing:** never on real project files. The live test script only runs on a scratch `.gh` file.
- **Credit:** keep the MIT `LICENSE` (both copyright lines) and the credits to Brooks Talley and to
  Alfred Chen's grasshopper-mcp.

## Build and test

```bash
dotnet build src/Cordyceps/Cordyceps.csproj -c Release   # Release only; output: releases/Hyperceps.gha
dotnet test src/Cordyceps.Tests -c Release              # host-free unit tests
python tests/test_hyperceps.py --skip-undo              # live checks, with Grasshopper open on scratch/hyperceps-scratch.gh
powershell -File scripts/package.ps1                     # release files in artifacts/ (Yak package, .gha, skill zip)
```

The project targets .NET 8.0 and Grasshopper for Rhino 8. Debug builds are blocked.

## Architecture

- **McpServer.cs:** HTTP MCP server (JSON-RPC). Discovers tools by reflection (`[McpServerToolType]`,
  `[McpServerTool]`); `GetServerInstructions()` is the connection note every client receives (it opens
  with the version and the skill block from `Core/SkillPairing.cs`).
- **CordycepsComponent.cs:** the canvas component that starts and stops the server.
- **Core/GrasshopperContext.cs:** all document access runs on the UI thread through
  `ExecuteOnUiThread()`.
- **Core/ToolHelpers.cs:** document and object resolution (GUIDs, prefixes, refs, nicknames),
  infrastructure protection (the bridge component and what is wired to it are hidden and protected),
  response helpers.
- **Tools/Batch/:** `gh_batch`: parse (`Core/Batch`), plan and check everything (`BatchPlanner`), apply
  as one undo record with rollback (`BatchRunner`, `BatchOps`), names shared with every tool
  (`ObjectNames`).
- **Core/Outline, Core/Sweep, Core/TreeSummary.cs:** host-free logic behind `outline`, `sweep` and
  `tree`; the Grasshopper adapters are `GhCanvasTool.Outline.cs`, `GhDocumentTool.Sweep.cs` and
  `GhInspectTool.Tree.cs`.
- **Core/LichenThallus.cs:** reads Lichen Thalli without depending on Lichen; Thalli are never changed.
- **Tools/Unified/:** the single-step tools `gh_canvas`, `gh_wire`, `gh_document`, `gh_script`,
  `gh_inspect`, `rhino_scene`, `rhino_render`, each with an `action` parameter.

## Documentation audit (every change)

Agents only know what the plugin and skill tell them. After any change, check and update:

| What | Where |
| --- | --- |
| Tool help metadata | `ActionInfo` in the tool class |
| Connection note | `McpServer.cs` → `GetServerInstructions()` |
| Guides served as MCP resources | `src/Cordyceps/Knowledge/*.md` (registered in `Resources/ResourceRegistry.cs`) |
| Prompt templates | `Prompts/PromptRegistry.cs` |
| Common errors | `Knowledge/CommonErrorsGuide.md` |
| The skill | `skills/hyperceps/SKILL.md` |
| `gh_batch` contract | `docs/gh-batch-schema.md` and `Tools/Batch/GhBatchSchema.json` |
| Changelog | `CHANGELOG.md` |

## Key patterns

- Tool methods return JSON strings with a `success` field; results stay compact (no per-wire echo,
  capped lists).
- Logic that doesn't need Grasshopper goes in host-free classes under `Core/` and gets unit tests; the
  test project links those files directly.
- Use `Core.DebugLog` for logging.
