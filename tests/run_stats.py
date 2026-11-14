#!/usr/bin/env python3
"""
Measure benchmark runs from Claude Code session exports: time, model turns, tool calls, tokens, and
how the agent wrote its scripts.

A run is one request, or a request plus the follow-up messages sent after each reply (rounds). It is
measured from the moment the request was sent to the agent's last reply (or tool result) before the
next message after the run, so a connection check before it and questions after it are left out.

Usage (from the repository folder):
    .venv\\Scripts\\python tests\\run_stats.py EXPORT --request "Build the block"
    .venv\\Scripts\\python tests\\run_stats.py EXPORT --request "Write a script" --last "Now also report"
    .venv\\Scripts\\python tests\\run_stats.py EXPORT1 EXPORT2 --request "Build the block"
    .venv\\Scripts\\python tests\\run_stats.py --runs runs.csv

EXPORT is a session export: the .zip the app saves (session menu > Export), the folder it unzips to,
or the transcript.jsonl inside. --request is any part of the request's text (case-insensitive; the
last message containing it); without it, the last message in the session is measured. --last is any
part of the last follow-up message: every message from the request to that one starts a round.
Several exports give one table, a row per run.

runs.csv has the columns task, label, export, request, last and, optionally, done (last may be
empty; a relative export path is read from the CSV's folder; done is yes unless it says no, and a
pair with a run that isn't done is shown but left out of the ranges). Runs are grouped by task. A task with exactly two runs gets a
ratio row, first run / second run, so put the run you compare against (Cordyceps) first; a summary
gives each ratio's range across tasks and names the tasks where the second run used more.

Tokens: every model turn re-reads the whole conversation, so "total processed" adds up, per turn,
fresh input + cache writes + cache reads + output. That is the number a "fewer total tokens"
comparison uses. The breakdown is shown too. Total tokens aren't cost: cache reads are most of every
total and the cheapest, output the dearest. "Cost-weighted" weighs each kind by the usual price
ratios (COST_WEIGHTS: fresh input 1, cache write 1.25, cache read 0.1, output 5; an account's actual
prices may differ), in input-token equivalents.

Scripts: gh_script calls are followed per script component. A full write is set, or configure with
code; an edit is action='edit' (Hyperceps), measured by the length of its edits text. A read-back is
a get or info of a script the agent wrote or edited earlier in the same run. "Code sent" is the code
typed in full writes and edits; "sent / size" divides it by the size of the scripts it went to: about
1 per change means each change resent the whole script, small numbers mean edits. Full resends of
scripts over 200 lines and read-backs are listed as guidance flags (the Hyperceps skill says to edit
instead, and never to read your own script back; Cordyceps has no edit action).
"""

import argparse
import collections
import csv
import datetime
import json
import os
import re
import sys
import zipfile

LONG_SCRIPT_LINES = 200

# Price ratios per token kind, relative to fresh input (independent review, finding 7). Stated with
# every table that uses them.
COST_WEIGHTS = {"input_tokens": 1.0, "cache_creation_input_tokens": 1.25, "cache_read_input_tokens": 0.1,
                "output_tokens": 5.0}
COST_WEIGHTS_TEXT = "fresh input 1, cache write 1.25, cache read 0.1, output 5"


def cost_weighted(totals):
    return sum(totals.get(kind, 0) * w for kind, w in COST_WEIGHTS.items())
PLUGIN_NAMES = {"cordyceps": "Cordyceps", "hyperceps": "Hyperceps"}   # any other server is shown by its own name


class RunError(Exception):
    pass


def parse_time(text):
    return datetime.datetime.fromisoformat(text.replace("Z", "+00:00"))


def load(path):
    """The records and metadata of a session export: its .zip, the folder it unzips to, or a .jsonl."""
    meta = {}
    if not os.path.exists(path):
        raise RunError(f"{path}: not found")
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            name = "transcript.jsonl" if "transcript.jsonl" in names else next(
                (n for n in names if n.endswith(".jsonl") and "/" not in n), None)
            if not name:
                raise RunError(f"{path}: no transcript in this export")
            lines = z.read(name).decode("utf-8").splitlines()
            if "metadata.json" in names:
                meta = json.loads(z.read("metadata.json").decode("utf-8"))
    else:
        if os.path.isdir(path):
            path = os.path.join(path, "transcript.jsonl")
        with open(path, encoding="utf-8") as f:
            lines = f.read().splitlines()
        side = os.path.join(os.path.dirname(path), "metadata.json")
        if os.path.exists(side):
            with open(side, encoding="utf-8") as f:
                meta = json.load(f)
    records = []
    for line in lines:
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return records, meta


def user_text(rec):
    """The text a person typed (pasted text included), or None for tool results and system-inserted records."""
    msg = rec.get("message") or {}
    if rec.get("type") != "user" or msg.get("role") != "user" or rec.get("isMeta") or rec.get("isCompactSummary"):
        return None
    content = msg.get("content")
    if isinstance(content, str):
        text = content
    elif isinstance(content, list):
        if any(b.get("type") == "tool_result" for b in content if isinstance(b, dict)):
            return None
        text = " ".join(b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text")
    else:
        return None
    # The app puts notes in front of a message inside <system-reminder> tags; they aren't typed.
    while "<system-reminder>" in text and "</system-reminder>" in text:
        a = text.index("<system-reminder>")
        b = text.index("</system-reminder>", a) + len("</system-reminder>")
        text = text[:a] + text[b:]
    # Pasted text arrives wrapped in <pasted_content> tags; the words inside are what was sent.
    text = re.sub(r"</?pasted_content[^>]*>", "", text).strip()
    return text or None


def is_tool_result(rec):
    content = (rec.get("message") or {}).get("content")
    return rec.get("type") == "user" and isinstance(content, list) and any(
        isinstance(b, dict) and b.get("type") == "tool_result" for b in content)


def result_text(block):
    content = block.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text")
    return ""


def parse_result(text, is_error):
    """What a gh_script result says: success, the component's GUID, its source and source length."""
    out = {"chars": len(text), "success": not is_error, "id": None, "source": None, "sourceLength": None}
    try:
        data = json.loads(text)
    except (json.JSONDecodeError, TypeError):
        data = None
    if isinstance(data, dict):
        out["success"] = out["success"] and data.get("success") is not False
        out["id"] = data.get("id") if isinstance(data.get("id"), str) else None
        out["source"] = data.get("source") if isinstance(data.get("source"), str) else None
        if isinstance(data.get("sourceLength"), int):
            out["sourceLength"] = data["sourceLength"]
    else:
        # A large result is kept in a file and the conversation holds a preview: read what it shows.
        found = re.search(r'"id"\s*:\s*"([0-9a-fA-F-]{36})"', text)
        out["id"] = found.group(1) if found else None
        found = re.search(r'"sourceLength"\s*:\s*(\d+)', text)
        out["sourceLength"] = int(found.group(1)) if found else None
        if re.search(r'"success"\s*:\s*false', text):
            out["success"] = False
    return out


def line_count(text):
    return text.replace("\r\n", "\n").count("\n") + 1 if text else 0


def apply_edits(text, edits):
    """The source after Hyperceps edits, or None if an edit can't be followed here."""
    text = text.replace("\r\n", "\n")
    for e in edits:
        if not isinstance(e, dict) or not isinstance(e.get("old"), str) or not isinstance(e.get("new"), str):
            return None
        old = e["old"].replace("\r\n", "\n")
        if not old or old not in text:
            return None
        text = text.replace(old, e["new"].replace("\r\n", "\n"), 1)
    return text


class Script:
    def __init__(self, label):
        self.label = label
        self.text = None       # source as last known (written, edited or read)
        self.size = None       # characters
        self.written = False   # written or edited by the agent in this run
        self.full_writes = 0
        self.full_chars = 0
        self.resends = 0
        self.edits = 0
        self.edit_chars = 0
        self.readbacks = 0
        self.readback_chars = 0
        self.reads = 0
        self.max_lines = 0

    def lines(self):
        return line_count(self.text) if self.text is not None else None


class ScriptLog:
    """Follows gh_script calls per script component, whichever name the agent used for it."""

    def __init__(self):
        self.alias = {}
        self.scripts = {}
        self.flags = []
        self.rounds = collections.defaultdict(lambda: {"sent": 0, "touched": set(), "size": 0})

    def learn_refs(self, created):
        """gh_batch's created list (ref -> GUID prefix): a script created by ref and later named by its
        prefix or GUID is the same script."""
        for ref, prefix in (created or {}).items():
            if isinstance(ref, str) and isinstance(prefix, str) and ref.lower() not in self.alias:
                self.alias[ref.lower()] = prefix.lower()

    def _known(self, name):
        """The key of a script already seen under this name, its prefix or its full GUID."""
        name = self.alias.get(name, name)
        if name in self.scripts:
            return name
        if len(name) >= 8 and re.fullmatch(r"[0-9a-f-]+", name):   # GUIDs and prefixes only, not nicknames
            return next((k for k in self.scripts
                         if len(k) >= 8 and re.fullmatch(r"[0-9a-f-]+", k) and (k.startswith(name) or name.startswith(k))), None)
        return None

    def resolve(self, given, guid):
        g = (given or "").strip().lower()
        if guid:
            key = guid.lower()
            if key not in self.scripts:
                # Seen before under a name (a ref, nickname or prefix) without its GUID: merge.
                old = self._known(g) if g else None
                old = old or self._known(key)
                if old and old != key:
                    self.scripts[key] = self.scripts.pop(old)
                    for name, target in list(self.alias.items()):
                        if target == old:
                            self.alias[name] = key
            if g and g != key:
                self.alias[g] = key
        else:
            key = self._known(g) or g or "?"
        if key not in self.scripts:
            label = given or key
            self.scripts[key] = Script(label[:8] if re.fullmatch(r"[0-9a-fA-F-]{36}", label) else label)
        return key, self.scripts[key]

    def call(self, rnd, inp, text, is_error):
        action = str(inp.get("action") or "").lower()
        code = inp.get("code")
        if action not in ("set", "configure", "edit", "get", "info") or (action == "configure" and not code):
            return
        res = parse_result(text or "", is_error)
        key, s = self.resolve(inp.get("id"), res["id"])
        here = self.rounds[rnd]
        if action in ("set", "configure"):
            code = code if isinstance(code, str) else json.dumps(code)
            n = line_count(code)
            resend = s.written or s.text is not None
            s.full_writes += 1
            s.full_chars += len(code)
            here["sent"] += len(code)
            here["touched"].add(key)
            if resend:
                s.resends += 1
                if n > LONG_SCRIPT_LINES:
                    self.flags.append(f"round {rnd + 1}: full resend of {s.label} ({n} lines, {len(code):,} characters)")
            if res["success"]:
                s.text, s.size, s.written = code, len(code), True
        elif action == "edit":
            payload = inp.get("edits")
            raw = payload if isinstance(payload, str) else json.dumps(payload)
            try:
                parsed = json.loads(raw) if isinstance(payload, str) else payload
            except json.JSONDecodeError:
                parsed = None
            s.edits += 1
            s.edit_chars += len(raw or "")
            here["sent"] += len(raw or "")
            here["touched"].add(key)
            if res["success"]:
                s.written = True
                s.text = apply_edits(s.text, parsed) if s.text is not None and isinstance(parsed, list) else None
                s.size = res["sourceLength"] or (len(s.text) if s.text is not None else s.size)
        else:
            if s.written:
                s.readbacks += 1
                s.readback_chars += res["chars"]
                self.flags.append(f"round {rnd + 1}: read back {s.label} ({action}, {res['chars']:,} characters)")
            else:
                s.reads += 1
            if res["source"] is not None:
                s.text, s.size = res["source"], len(res["source"])
            elif res["sourceLength"]:
                s.size = res["sourceLength"]
        if s.lines():
            s.max_lines = max(s.max_lines, s.lines())

    def end_round(self, rnd):
        here = self.rounds[rnd]
        here["size"] = sum(self.scripts[k].size or 0 for k in here["touched"])

    def summary(self):
        mine = [s for s in self.scripts.values() if s.written]
        return {
            "scripts": len(mine),
            "full_writes": sum(s.full_writes for s in self.scripts.values()),
            "full_chars": sum(s.full_chars for s in self.scripts.values()),
            "resends": sum(s.resends for s in self.scripts.values()),
            "edits": sum(s.edits for s in self.scripts.values()),
            "edit_chars": sum(s.edit_chars for s in self.scripts.values()),
            "sent": sum(r["sent"] for r in self.rounds.values()),
            "final_size": sum(s.size or 0 for s in mine),
            "readbacks": sum(s.readbacks for s in self.scripts.values()),
            "readback_chars": sum(s.readback_chars for s in self.scripts.values()),
            "reads": sum(s.reads for s in self.scripts.values()),
            "largest_lines": max((s.max_lines for s in mine), default=0),
        }


def measure(records, request=None, last=None):
    prompts = [(i, t) for i, t in ((i, user_text(r)) for i, r in enumerate(records)) if t]
    if not prompts:
        raise RunError("No typed messages found in this transcript.")

    def listing():
        return "Messages in this session:\n" + "\n".join(f"  - {' '.join(t.split())[:100]}" for _, t in prompts)

    if request:
        matches = [(i, t) for i, t in prompts if request.lower() in t.lower()]
        if not matches:
            raise RunError(f"No message containing {request!r}. " + listing())
        start, text = matches[-1]
    else:
        start, text = prompts[-1]
    final = start
    if last:
        after = [i for i, t in prompts if i > start and last.lower() in t.lower()]
        if after:
            final = after[0]
        elif last.lower() not in text.lower():
            raise RunError(f"No message containing {last!r} after the request. " + listing())
    later = [i for i, _ in prompts if i > final]
    end = later[0] if later else len(records)
    starts = [i for i, _ in prompts if start <= i <= final]

    results = {}
    for rec in records[start:end]:
        content = (rec.get("message") or {}).get("content")
        if rec.get("type") == "user" and isinstance(content, list):
            for b in content:
                if isinstance(b, dict) and b.get("type") == "tool_result":
                    results[b.get("tool_use_id")] = (result_text(b), bool(b.get("is_error")))

    rounds = []
    turns, turn_round = {}, {}
    tools = collections.Counter()
    scripts = ScriptLog()
    compactions = []
    for idx in range(start, end):
        rec = records[idx]
        if idx in starts:
            if rounds:
                scripts.end_round(len(rounds) - 1)
            began = parse_time(rec["timestamp"])
            rounds.append({"began": began, "finished": began, "tools": collections.Counter(), "text": user_text(rec)})
        here = rounds[-1]
        # A round ends at the agent's last reply or tool result. The app's own records after it (a
        # stop-hook summary 1.4-3.0 s later, attachments and queued messages carrying the time of the
        # person's next message) are not the agent's work.
        if rec.get("timestamp") and (rec.get("type") == "assistant" or is_tool_result(rec)):
            here["finished"] = max(here["finished"], parse_time(rec["timestamp"]))
        if rec.get("type") == "system" and rec.get("subtype") == "compact_boundary":
            info = rec.get("compactMetadata") or {}
            compactions.append({"round": len(rounds), "trigger": info.get("trigger", "?"),
                                "before": info.get("preTokens"), "at": rec.get("timestamp")})
        msg = rec.get("message") or {}
        if rec.get("type") != "assistant" or not isinstance(msg, dict):
            continue
        # One model turn can be split over several records; count its usage once.
        key = msg.get("id") or rec.get("requestId") or rec.get("uuid")
        turn_round.setdefault(key, len(rounds) - 1)
        if isinstance(msg.get("usage"), dict):
            turns[key] = msg["usage"]
        for block in msg.get("content") or []:
            if isinstance(block, dict) and block.get("type") == "tool_use":
                name = block.get("name", "?")
                inp = block.get("input") or {}
                action = inp.get("action")
                label = f"{name} {action}" if action else name
                tools[label] += 1
                here["tools"][label] += 1
                parts = name.split("__")
                if len(parts) >= 3 and parts[0] == "mcp" and parts[1].startswith(("cordyceps", "hyperceps")) \
                        and parts[2] == "gh_script":
                    text_, err = results.get(block.get("id"), ("", False))
                    scripts.call(len(rounds) - 1, inp, text_, err)
                elif len(parts) >= 3 and parts[0] == "mcp" and parts[2] == "gh_batch":
                    text_, _ = results.get(block.get("id"), ("", False))
                    try:
                        scripts.learn_refs(json.loads(text_).get("created"))
                    except (json.JSONDecodeError, AttributeError, TypeError):
                        pass
    scripts.end_round(len(rounds) - 1)

    def token_totals(keys):
        totals = collections.Counter()
        for k in keys:
            for f in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens"):
                totals[f] += turns[k].get(f) or 0
        return totals

    def gh_calls(counter):
        return sum(n for name, n in counter.items() if name.startswith(("mcp__cordyceps", "mcp__hyperceps")))

    servers = collections.Counter()
    for name, n in tools.items():
        parts = name.split("__")
        if len(parts) >= 3 and parts[0] == "mcp" and parts[1].startswith(("cordyceps", "hyperceps")):
            servers[parts[1]] += n
    plugin = PLUGIN_NAMES.get(servers.most_common(1)[0][0], servers.most_common(1)[0][0]) if servers else "-"

    for n, rd in enumerate(rounds):
        keys = [k for k in turns if turn_round.get(k) == n]
        rd["turns"] = len(keys)
        rd["tokens"] = token_totals(keys)
        rd["gh"] = gh_calls(rd["tools"])
        rd["seconds"] = (rd["finished"] - rd["began"]).total_seconds()
        rd["sent"] = scripts.rounds[n]["sent"]
        rd["size"] = scripts.rounds[n]["size"]

    totals = token_totals(turns)
    return {
        "request": text,
        "rounds": rounds,
        "seconds": sum(rd["seconds"] for rd in rounds),
        "wall": (rounds[-1]["finished"] - rounds[0]["began"]).total_seconds(),
        "turns": len(turns),
        "tools": tools,
        "gh": gh_calls(tools),
        "plugin": plugin,
        "totals": totals,
        "processed": sum(totals.values()),
        "output": totals["output_tokens"],
        "weighted": cost_weighted(totals),
        "compactions": compactions,
        "scripts": scripts,
        "script_summary": scripts.summary(),
    }


def mmss(seconds):
    minutes, secs = divmod(int(round(seconds)), 60)   # round first: 179.6 s is 3:00, not 2:60
    return f"{minutes}:{secs:02d}"


def tokens(n):
    return f"{n / 1e6:.2f}M" if n >= 1e6 else f"{n / 1e3:.0f}k"


def chars(n):
    return f"{n / 1e3:.1f}k" if n >= 1000 else str(n)


def times(r):
    """A ratio as text: two decimals below 2 (so 0.97x and 1.03x stay apart), one above."""
    return f"{r:.2f}x" if r < 2 else f"{r:.1f}x"


def ratio(a, b):
    return times(a / b) if a and b else "-"


def compaction_text(m):
    c = m["compactions"]
    if not c:
        return "no"
    return f"yes, {len(c)} time{'s' if len(c) > 1 else ''} (" + "; ".join(
        f"round {x['round']}, {x['trigger']}" + (f", {x['before']:,} tokens before" if x["before"] else "") for x in c) + ")"


def print_run(m, meta):
    print(f"Request: {' '.join(m['request'].split())[:120]}")
    if meta:
        print(f"Session: {meta.get('title') or '-'} - model {meta.get('model') or '?'} - effort {meta.get('effort') or '?'}")
    rounds = m["rounds"]
    began, finished = rounds[0]["began"], rounds[-1]["finished"]
    seconds = m["seconds"]
    print(f"Time: {int(round(seconds)) // 60} min {int(round(seconds)) % 60} s (request sent {began:%H:%M:%S} UTC, last reply {finished:%H:%M:%S} UTC)"
          + (f"; the agent's time in {len(rounds)} rounds, {mmss(m['wall'])} including the waits between them" if len(rounds) > 1 else ""))
    print(f"Model turns: {m['turns']}")
    print(f"Tool calls: {sum(m['tools'].values())} (Grasshopper calls through Cordyceps / Hyperceps: {m['gh']})")
    for name, n in m["tools"].most_common():
        print(f"  {n:4} x {name}")
    t = m["totals"]
    print(f"Tokens, total processed: {m['processed']:,}")
    print(f"  re-read from cache {t['cache_read_input_tokens']:,}; cache writes {t['cache_creation_input_tokens']:,}; "
          f"fresh input {t['input_tokens']:,}; output {t['output_tokens']:,}")
    print(f"Cost-weighted: {m['weighted']:,.0f} input-token equivalents ({COST_WEIGHTS_TEXT}); "
          f"output is {t['output_tokens'] * COST_WEIGHTS['output_tokens'] / m['weighted']:.0%} of it" if m["weighted"] else "Cost-weighted: -")
    print(f"Summarized (compacted): {compaction_text(m)}")

    if len(rounds) > 1:
        print()
        print("| Round | Message | Time | Turns | Grasshopper calls | Output tokens | Total tokens | Code sent | Scripts' size | Sent / size |")
        print("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
        for n, rd in enumerate(rounds):
            size = f"{chars(rd['size'])} | {rd['sent'] / rd['size']:.2f}" if rd["size"] else "- | -"
            print(f"| {n + 1} | {' '.join(rd['text'].split())[:40].replace('|', '/')} | {mmss(rd['seconds'])} | {rd['turns']} | {rd['gh']} | "
                  f"{rd['tokens']['output_tokens']:,} | {tokens(sum(rd['tokens'].values()))} | {chars(rd['sent'])} | {size} |")

    s = m["script_summary"]
    log = m["scripts"]
    if not log.scripts:
        return
    print()
    print(f"Scripts: {s['scripts']} written or edited by the agent, the largest reaching {s['largest_lines']} lines")
    print(f"  Full writes: {s['full_writes']}, {s['full_chars']:,} characters ({s['resends']} of them resent a script already there)")
    print(f"  Edits: {s['edits']}, {s['edit_chars']:,} characters")
    print(f"  Code sent: {s['sent']:,} characters, {ratio(s['sent'], s['final_size'])} the size of the scripts it went to "
          f"({s['final_size']:,} characters at the end)")
    print(f"  Read-backs of its own scripts: {s['readbacks']} ({s['readback_chars']:,} characters); "
          f"reads of scripts it hadn't written: {s['reads']}")
    for script in log.scripts.values():
        lines = script.lines()
        print(f"  - {script.label}: {lines if lines is not None else '?'} lines, {script.size or 0:,} characters at the end; "
              f"{script.full_writes} full writes, {script.edits} edits, {script.readbacks} read-backs, {script.reads} other reads")
    if log.flags:
        print(f"Guidance flags ({len(log.flags)}):")
        for f in log.flags:
            print(f"  - {f}")


TABLE_HEAD = ("| Run | Plugin | Rounds | Time | Turns | Grasshopper calls | Total tokens | Output tokens | Cost-weighted | Full writes (chars) "
              "| Edits (chars) | Code sent / size | Largest script | Own read-backs | Compacted |")


def table_row(label, m):
    s = m["script_summary"]
    if m["scripts"].scripts:
        script_cells = (f"{s['full_writes']} ({chars(s['full_chars'])}) | {s['edits']} ({chars(s['edit_chars'])}) | "
                        f"{ratio(s['sent'], s['final_size'])} | {s['largest_lines']} lines | {s['readbacks']}")
    else:
        script_cells = "- | - | - | - | -"
    return (f"| {label} | {m['plugin']} | {len(m['rounds'])} | {mmss(m['seconds'])} | {m['turns']} | {m['gh']} | "
            f"{tokens(m['processed'])} | {tokens(m['output'])} | {tokens(m['weighted'])} | {script_cells} | {len(m['compactions']) or 'no'} |")


RATIOS = (("Time", "seconds"), ("Turns", "turns"), ("Grasshopper calls", "gh"), ("Total tokens", "processed"),
          ("Output tokens", "output"), ("Cost-weighted tokens", "weighted"))


def print_table(rows):
    """rows: (task, label, metrics or error text)."""
    print(TABLE_HEAD)
    print("|" + " --- |" * (TABLE_HEAD.count("|") - 1))
    tasks = collections.OrderedDict()
    for task, label, m in rows:
        tasks.setdefault(task, []).append((label, m))
    pairs = []
    for task, runs in tasks.items():
        for label, m in runs:
            name = f"{task}: {label}" if task else label
            if not isinstance(m, str) and not m.get("done", True):
                name += " (not done)"
            if isinstance(m, str):
                print(f"| {name} | {m[:200]} |")
            else:
                print(table_row(name, m))
        good = [(l, m) for l, m in runs if not isinstance(m, str)]
        if task and len(runs) == 2 and len(good) == 2:
            (la, a), (lb, b) = good
            cells = {key: ratio(a[key], b[key]) for _, key in RATIOS}
            counted = a.get("done", True) and b.get("done", True)
            if counted:
                pairs.append((task, a, b))
            print(f"| **{task}: {la} / {lb}**{'' if counted else ' (not in the ranges)'} | | | {cells['seconds']} | "
                  f"{cells['turns']} | {cells['gh']} | {cells['processed']} | {cells['output']} | {cells['weighted']} | | | | | | |")
    if len(pairs) > 1:
        print()
        print(f"Across {len(pairs)} tasks where both runs were done, first run / second run:")
        for title, key in RATIOS:
            values = [(task, a[key] / b[key]) for task, a, b in pairs if a[key] and b[key]]
            if not values:
                continue
            lo, hi = min(v for _, v in values), max(v for _, v in values)
            worse = [task for task, v in values if v < 1]
            print(f"  {title}: {times(lo)} to {times(hi)}" + (f"; the second run used more in {', '.join(worse)}" if worse else ""))
    print()
    print(f"Cost-weighted tokens weigh each kind by price ratio, in input-token equivalents: {COST_WEIGHTS_TEXT}.")


def main():
    parser = argparse.ArgumentParser(description="Time, turns, tool calls, tokens and script writing for benchmark runs")
    parser.add_argument("exports", nargs="*", help="session exports (.zip, folder or transcript.jsonl)")
    parser.add_argument("--request", help="part of the request's text (default: the last message)")
    parser.add_argument("--last", help="part of the last follow-up message (default: the request alone)")
    parser.add_argument("--runs", help="CSV with the columns task, label, export, request, last")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if args.runs:
        base = os.path.dirname(os.path.abspath(args.runs))
        rows = []
        with open(args.runs, encoding="utf-8-sig", newline="") as f:
            for row in csv.DictReader(f):
                path = (row.get("export") or "").strip()
                if path and not os.path.isabs(path):
                    path = os.path.join(base, path)
                try:
                    records, _ = load(path)
                    m = measure(records, (row.get("request") or "").strip() or None, (row.get("last") or "").strip() or None)
                except RunError as e:
                    m = str(e).splitlines()[0]
                if not isinstance(m, str):
                    m["done"] = (row.get("done") or "yes").strip().lower() not in ("no", "n", "false", "0")
                rows.append(((row.get("task") or "").strip(), (row.get("label") or "").strip() or path, m))
        print_table(rows)
        return 0

    if not args.exports:
        parser.error("give one or more session exports, or --runs")
    if len(args.exports) == 1:
        try:
            records, meta = load(args.exports[0])
            print_run(measure(records, args.request, args.last), meta)
        except RunError as e:
            print(e)
            return 1
        return 0

    rows = []
    for path in args.exports:
        try:
            records, meta = load(path)
            rows.append(("", meta.get("title") or os.path.basename(path), measure(records, args.request, args.last)))
        except RunError as e:
            rows.append(("", os.path.basename(path), str(e).splitlines()[0]))
    print_table(rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
