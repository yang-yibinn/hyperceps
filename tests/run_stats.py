#!/usr/bin/env python3
"""
Measure one request in a Claude Code session transcript: time, model turns, tool calls and tokens.

For benchmark comparisons (for example Cordyceps against Hyperceps on the same request): run it on
each session's transcript with the same request text, so both are measured the same way - from the
moment the request was sent to the agent's last reply before your next message (a connection check
before it and any questions after it are left out).

Usage:
    .venv\\Scripts\\python tests\\run_stats.py TRANSCRIPT.jsonl --request "Build the top bracing"

TRANSCRIPT.jsonl is the transcript.jsonl inside a session export (session menu > Export).
--request is any part of the request's text (case-insensitive); without it, the last request in the
session is measured.

Tokens: every model turn re-reads the whole conversation, so "total processed" adds up, per turn,
fresh input + cache writes + cache reads + output. That is the number a "fewer total tokens"
comparison uses. The breakdown is shown too.
"""

import argparse
import collections
import datetime
import json
import sys


def parse_time(text):
    return datetime.datetime.fromisoformat(text.replace("Z", "+00:00"))


def user_text(rec):
    """The text a person typed (pasted text included), or None for tool results and system-inserted records."""
    msg = rec.get("message") or {}
    if rec.get("type") != "user" or msg.get("role") != "user" or rec.get("isMeta"):
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
    text = text.strip()
    return text or None


def main():
    parser = argparse.ArgumentParser(description="Time, turns, tool calls and tokens for one request")
    parser.add_argument("transcript")
    parser.add_argument("--request", help="part of the request's text (default: the last request)")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    records = []
    for line in open(args.transcript, encoding="utf-8"):
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError:
            continue

    prompts = [(i, user_text(r)) for i, r in enumerate(records) if user_text(r)]
    if not prompts:
        print("No typed requests found in this transcript.")
        return 1
    if args.request:
        matches = [(i, t) for i, t in prompts if args.request.lower() in t.lower()]
        if not matches:
            print(f"No request containing {args.request!r}. Requests in this session:")
            for _, t in prompts:
                print(f"  - {t[:100]}")
            return 1
        start, text = matches[-1]
    else:
        start, text = prompts[-1]

    later = [i for i, _ in prompts if i > start]
    end = later[0] if later else len(records)
    window = records[start:end]

    began = parse_time(records[start]["timestamp"])
    finished = began
    turns = {}
    tools = collections.Counter()
    for rec in window:
        if rec.get("timestamp"):
            finished = max(finished, parse_time(rec["timestamp"]))
        msg = rec.get("message") or {}
        if rec.get("type") != "assistant" or not isinstance(msg, dict):
            continue
        # One model turn can be split over several records; count its usage once.
        key = msg.get("id") or rec.get("requestId") or rec.get("uuid")
        if isinstance(msg.get("usage"), dict):
            turns[key] = msg["usage"]
        for block in msg.get("content") or []:
            if isinstance(block, dict) and block.get("type") == "tool_use":
                name = block.get("name", "?")
                action = (block.get("input") or {}).get("action")
                tools[f"{name} {action}" if action else name] += 1

    totals = collections.Counter()
    for usage in turns.values():
        for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens"):
            totals[k] += usage.get(k) or 0
    processed = sum(totals.values())
    seconds = (finished - began).total_seconds()

    print(f"Request: {text[:120]}")
    print(f"Time: {int(seconds // 60)} min {seconds % 60:.0f} s (request sent {began:%H:%M:%S} UTC, last reply {finished:%H:%M:%S} UTC)")
    grasshopper = sum(n for name, n in tools.items() if name.startswith(("mcp__cordyceps", "mcp__hyperceps")))
    print(f"Model turns: {len(turns)}")
    print(f"Tool calls: {sum(tools.values())} (Grasshopper calls through Cordyceps / Hyperceps: {grasshopper})")
    for name, n in tools.most_common():
        print(f"  {n:4} x {name}")
    print(f"Tokens, total processed: {processed:,}")
    print(f"  re-read from cache {totals['cache_read_input_tokens']:,}; cache writes {totals['cache_creation_input_tokens']:,}; "
          f"fresh input {totals['input_tokens']:,}; output {totals['output_tokens']:,}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
