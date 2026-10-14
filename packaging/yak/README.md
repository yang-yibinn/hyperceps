# Hyperceps {{VERSION}}

**Claude builds and edits your Grasshopper definitions in a few validated calls instead of hundreds.**

Hyperceps is a Grasshopper plugin and a Claude skill that work together. This package installs the
plugin; the skill is in the `skill/hyperceps` folder next to this file, and on the release page.

## Two parts: install both

1. **Plugin:** installed by this package. Drop the component on your canvas: *Params → Util → Hyperceps*.
2. **Skill:**
   - **Claude Code** (including the Claude desktop app's Code tab): copy the `skill/hyperceps` folder
     from this package's folder into `~/.claude/skills`. This package's folder is
     `%APPDATA%\McNeel\Rhinoceros\packages\8.0\hyperceps\{{VERSION}}` on Windows (`AppData` is hidden in
     File Explorer; paste the path into the address bar) and
     `~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/hyperceps/{{VERSION}}` on macOS. On
     Windows, in PowerShell:
     `Copy-Item -Recurse -Force "$env:APPDATA\McNeel\Rhinoceros\packages\8.0\hyperceps\{{VERSION}}\skill\hyperceps" "$env:USERPROFILE\.claude\skills\"`
   - **Claude on the web or the desktop chat:** download `hyperceps-skill.zip` from
     https://github.com/yang-yibinn/hyperceps/releases and upload it as a custom skill under Skills in
     Claude's settings.
3. **Connect** your AI app to `http://127.0.0.1:26930/mcp`. For Claude Code:
   `claude mcp add --transport http hyperceps http://127.0.0.1:26930/mcp`

The plugin and the skill carry the same version number. When Claude connects, the plugin asks it to
load the skill, or to tell you once if it isn't installed.

Documentation, benchmark and source: https://github.com/yang-yibinn/hyperceps

Developed by Yibin Yang at Adrian Smith + Gordon Gill Architecture (AS+GG). Built from Cordyceps by
Brooks Talley (MIT), which was inspired by grasshopper-mcp by Alfred Chen.
