# Hyperceps {{VERSION}}

**Claude builds and edits your Grasshopper definitions in a few validated calls instead of hundreds.**

Hyperceps is a Grasshopper plugin with a built-in guide for the AI agent. This package installs the
plugin; the plugin hands the guide (the `hyperceps` Claude skill) to the agent when it connects, so
there is nothing else to install.

## Get started

1. **Plugin:** installed by this package. Drop the component on your canvas: *Params → Util → Hyperceps*.
2. **Connect** your AI app to `http://127.0.0.1:26930/mcp`. For Claude Code:
   `claude mcp add --scope user --transport http hyperceps http://127.0.0.1:26930/mcp`

When the agent connects, the plugin asks it to read the guide before working on the canvas: the
installed `hyperceps` skill if your app has it, otherwise the plugin's own copy, which always matches
the plugin's version.

## Optional: install the guide as a Claude skill

The skill is in the `skill/hyperceps` folder next to this file, and on the release page, with the same
version number as the plugin. If an installed skill is for a different version, the agent reads the
plugin's copy instead and tells you once, so update or remove the skill when you update the plugin.

- **Claude Code** (including the Claude desktop app's Code tab): copy the `skill/hyperceps` folder
  from this package's folder into `~/.claude/skills`. This package's folder is
  `%APPDATA%\McNeel\Rhinoceros\packages\8.0\hyperceps\{{VERSION}}` on Windows (`AppData` is hidden in
  File Explorer; paste the path into the address bar) and
  `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/hyperceps/{{VERSION}}` on macOS. On
  Windows, in PowerShell:
  `New-Item -ItemType Directory -Force "$env:USERPROFILE\.claude\skills" | Out-Null; Copy-Item -Recurse -Force "$env:APPDATA\McNeel\Rhinoceros\packages\8.0\hyperceps\{{VERSION}}\skill\hyperceps" "$env:USERPROFILE\.claude\skills"`
- **Claude on the web or the desktop chat:** download `hyperceps-skill.zip` from
  https://github.com/yang-yibinn/hyperceps/releases and upload it as a custom skill under Skills in
  Claude's settings.

Documentation, benchmark and source: https://github.com/yang-yibinn/hyperceps

Developed by Yibin Yang at Adrian Smith + Gordon Gill Architecture (AS+GG). Built on top of Cordyceps
by Brooks Talley (MIT), which was inspired by grasshopper-mcp by Alfred Chen.
