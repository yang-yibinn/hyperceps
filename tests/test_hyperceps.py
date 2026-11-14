#!/usr/bin/env python3
"""
Hyperceps functional tests.

Calls the plugin's tools through the official MCP Python client at http://127.0.0.1:26930/mcp,
exactly as an AI app would, and covers the functional checks listed in docs/design.md.

SAFETY: before every check the script reads the open document's file name (gh_document info)
and stops unless it is the scratch file. The scratch file is cleared and rebuilt by the tests;
never point this script at a real project.

Run (from the repo folder, with Rhino open on the scratch file and Hyperceps on its canvas):

    .venv\\Scripts\\python tests\\test_hyperceps.py

The 'thallus' check needs a second, hand-made file with Lichen Thalli and never clears it:

    .venv\\Scripts\\python tests\\test_hyperceps.py --scratch scratch\\hyperceps-thallus.gh --only thallus

Every other check clears the canvas, so they refuse to run on a file that holds Thalli.

Options:
    --skip-undo      skip the one check that needs you to press Ctrl+Z
    --only NAMES     run only these checks (comma-separated, e.g. --only chain,insert)
    --list           list the checks and exit
    --scratch PATH   a different scratch file (default: scratch/hyperceps-scratch.gh)
    --url URL        a different server (default: http://127.0.0.1:26930/mcp)

Exit code: 0 all passed, 1 a check failed, 2 refused (not the scratch file) or not connected.
"""

import argparse
import asyncio
import json
import os
import sys
import time
import urllib.request
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client

REPO = Path(__file__).resolve().parent.parent
DEFAULT_SCRATCH = REPO / "scratch" / "hyperceps-scratch.gh"
DEFAULT_URL = "http://127.0.0.1:26930/mcp"


class CheckFailed(Exception):
    pass


class NotScratch(Exception):
    pass


def expect(condition, message, detail=None):
    if not condition:
        if detail is not None:
            text = json.dumps(detail, ensure_ascii=False, default=str)
            message += "\n      " + (text if len(text) < 700 else text[:700] + " ...")
        raise CheckFailed(message)


class Gh:
    """Thin helpers over the MCP session: every call returns the tool's JSON payload."""

    def __init__(self, session, scratch, instructions=None):
        self.session = session
        self.scratch = scratch
        self.instructions = instructions

    async def call(self, tool, **arguments):
        result = await self.session.call_tool(tool, arguments)
        text = result.content[0].text
        data = json.loads(text)
        data["_isError"] = bool(result.is_error)
        return data

    async def batch(self, **spec):
        return await self.call("gh_batch", **spec)

    async def guard(self):
        info = await self.call("gh_document", action="info")
        path = info.get("filePath")
        if not path or os.path.normcase(os.path.abspath(path)) != os.path.normcase(str(self.scratch)):
            raise NotScratch(
                f"the open document is '{info.get('displayName')}' ({path or 'not saved'}), "
                f"not the scratch file {self.scratch}")
        return info

    async def clear(self):
        await self.call("gh_document", action="clear")
        await self.call("gh_document", action="solver", enabled="true")

    async def components(self):
        return (await self.call("gh_canvas", action="list")).get("components", [])

    async def by_prefix(self, prefix):
        for c in await self.components():
            if c["id"].startswith(prefix):
                return c
        raise CheckFailed(f"no object with GUID prefix {prefix}")

    async def by_nickname(self, nickname):
        found = [c for c in await self.components() if c.get("nickname") == nickname]
        expect(len(found) == 1, f"expected one object nicknamed {nickname!r}, found {len(found)}")
        return found[0]

    async def info(self, obj_id):
        return await self.call("gh_canvas", action="info", id=obj_id)

    async def value(self, obj_id):
        return (await self.call("gh_canvas", action="get", id=obj_id)).get("value")

    async def outputs(self, obj_id):
        return (await self.call("gh_inspect", action="outputs", id=obj_id)).get("outputs", [])

    async def groups(self):
        return (await self.call("gh_canvas", action="group_list")).get("groups", [])

    async def has_thalli(self):
        """True when the open canvas holds Lichen Thalli (the clearing checks would delete them)."""
        try:
            return "Thall" in (await self.call("gh_canvas", action="outline")).get("summary", "")
        except Exception:
            return False  # a build without 'outline'

    async def listing(self):
        """Everything a batch can change, in a comparable form."""
        comps = sorted(
            f"{c['id']}|{c.get('name')}|{c.get('nickname')}|{c.get('inputCount')}|{c.get('outputCount')}"
            f"|{round(c.get('x', 0))},{round(c.get('y', 0))}"
            for c in await self.components())
        wires = sorted(
            f"{w['source']['componentId']}.{w['source']['param']}>{w['target']['componentId']}.{w['target']['param']}"
            for w in (await self.call("gh_wire", action="list")).get("connections", []))
        groups = sorted(f"{g['name']}|{','.join(sorted(g.get('memberIds', [])))}" for g in await self.groups())
        return {"components": comps, "wires": wires, "groups": groups}


def same_listing(a, b):
    return a == b


def diff_listing(a, b):
    out = {}
    for key in a:
        gone = sorted(set(a[key]) - set(b[key]))
        new = sorted(set(b[key]) - set(a[key]))
        if gone or new:
            out[key] = {"missing": gone[:5], "extra": new[:5]}
    return out


# ---- checks ----------------------------------------------------------------------------------------
# Each check names the functional check it covers (docs/design.md). Checks clear the scratch file first, so they can run alone.

CHECKS = []


def check(name, spec, clears=True):
    def register(fn):
        CHECKS.append((name, spec, fn, clears))
        return fn
    return register


@check("chain", "A small chain builds, solves and returns the correct ref-to-GUID map")
async def chain(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "a", "param": "Number", "nickname": "A"},
            {"ref": "b", "param": "Number", "nickname": "B"},
            {"ref": "add", "type": "Addition"},
            {"ref": "show", "panel": ""},
        ],
        set=[{"target": "a", "value": 2}, {"target": "b", "value": 3}],
        wires=[["a", "add.0"], ["b", "add.1"], ["add.0", "show"]],
    )
    expect(r["ok"] and r["success"] and not r["_isError"], "batch failed", r)
    expect(r["changed"] and r["solved"], "expected changed and solved", r)
    expect(set(r["created"]) == {"a", "b", "add", "show"}, "created map should list the four refs", r)
    expected_types = {"a": "Number", "b": "Number", "add": "Addition", "show": "Panel"}
    for ref, prefix in r["created"].items():
        obj = await gh.by_prefix(prefix)
        expect(obj["name"] == expected_types[ref], f"{ref} -> {prefix} is a {obj['name']}, expected {expected_types[ref]}")
    out = await gh.outputs((await gh.by_prefix(r["created"]["show"]))["id"])
    expect(out and out[0]["preview"] and float(out[0]["preview"][0]) == 5.0, "2 + 3 should show 5", out)
    expect(len(json.dumps(r)) < 4096, "result should stay under 4 KB")


@check("names", "\"Merge\" resolves to Sets/Tree/Merge; an ambiguous name lists candidates and creates nothing; a missing plugin is named")
async def names(gh):
    await gh.clear()
    before = await gh.listing()
    r = await gh.batch(components=[{"ref": "m", "type": "Merge"}], options={"dryRun": True})
    expect(r["ok"] and r["dryRun"] and r["resolved"]["m"] == "Sets/Tree/Merge", "Merge should resolve to Sets/Tree/Merge", r)

    r = await gh.batch(components=[{"ref": "m", "type": "Merge"}, {"ref": "l", "type": "Line"}])
    errors = " | ".join(r["errors"])
    expect(not r["ok"] and r["_isError"] and not r["changed"], "an ambiguous name must fail the batch", r)
    expect("'Line' is ambiguous" in errors and "Curve/Primitive/Line" in errors and "Params/Geometry/Line" in errors,
           "the error should list both Line candidates", r)
    expect(same_listing(before, await gh.listing()), "an ambiguous name must create nothing (not even Merge)")

    r = await gh.batch(components=[{"ref": "k", "type": "Karamba/Model/Assemble"}])
    expect(not r["ok"] and "Karamba3D" in " ".join(r["errors"]), "a missing plugin should be named", r)


@check("rollback", "A bad port name partway through a batch rolls back completely; the canvas is unchanged")
async def rollback(gh):
    await gh.clear()
    base = await gh.batch(components=[{"ref": "src", "panel": "1", "nickname": "SRC"}, {"ref": "keep", "type": "Merge", "nickname": "KEEP"}],
                          wires=[["SRC", "KEEP.0"]])
    expect(base["ok"], "setup failed", base)
    before = await gh.listing()

    r = await gh.batch(
        components=[{"ref": "cull", "type": "Cull Pattern"}, {"ref": "pat", "panel": "true\nfalse"}, {"ref": "m2", "type": "Merge"}],
        delete=["KEEP"],
        wires=[["SRC", "cull.L"], ["pat", "m2.0"], ["m2.0", "cull.L"], ["pat", "cull.Pattern"]],
    )
    expect(not r["ok"] and not r["changed"], "a bad port must fail the whole batch", r)
    expect(any(e.startswith("wires[3]") and "Did you mean 'P'" in e for e in r["errors"]),
           "the error should name wires[3] and suggest 'P'", r)
    expect(same_listing(before, await gh.listing()), "canvas changed", diff_listing(before, await gh.listing()))

    # A failure while applying, forced with a time limit (0.1 ms) that checking 100 entries alone
    # exceeds: the first change (deleting KEEP and its wire) is applied, then undone from the
    # batch's own record. The Ctrl+Z check exercises the same record with every kind of change.
    many = [{"ref": f"t{i}", "type": "Merge"} for i in range(50)]
    r = await gh.batch(components=many, wires=[["SRC", f"t{i}.0"] for i in range(50)],
                       delete=["KEEP"], options={"timeoutSeconds": 0.0001})
    expect(not r["ok"] and r.get("rolledBack") and not r["changed"], "the time-out should roll back", r)
    after = await gh.listing()
    expect(same_listing(before, after), "canvas changed after rollback", diff_listing(before, after))


@check("variable", "Variable inputs (Merge with 3, Entwine with 2) and List Item's \"Item +1\" output work")
async def variable(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "merge", "type": "Merge", "inputs": 3},
            {"ref": "ent", "type": "Entwine", "inputs": 2},
            {"ref": "item", "type": "List Item", "addOutputs": 1},
            {"ref": "p", "panel": "x"},
        ],
        wires=[["p", "ent.1"], ["item.1", "merge.0"]],
    )
    expect(r["ok"], "batch failed", r)
    expect((await gh.by_prefix(r["created"]["merge"]))["inputCount"] == 3, "Merge should have 3 inputs")
    expect((await gh.by_prefix(r["created"]["ent"]))["inputCount"] == 2, "Entwine should have 2 inputs")
    item = await gh.info((await gh.by_prefix(r["created"]["item"]))["id"])
    expect(len(item["outputs"]) == 2, "List Item should have 2 outputs", item["outputs"])

    # The third input is real and wireable. (Grasshopper's Merge then adds a spare empty input of
    # its own once its last input is connected, so the count may grow to 4 - that is Merge, not us.)
    r = await gh.batch(wires=[["p", "merge.2"]])
    expect(r["ok"] and r["counts"]["wires"] == 1, "wiring Merge's third input failed", r)


@check("specials", "Panels with multiline on and off, sliders, value lists and toggles are set correctly")
async def specials(gh):
    await gh.clear()
    r = await gh.batch(components=[
        {"ref": "one", "panel": "a\nb", "multiline": True},
        {"ref": "many", "panel": "a\nb"},
        {"ref": "count", "slider": {"min": 5, "max": 9, "value": 8, "decimals": 0}, "nickname": "COUNT"},
        {"ref": "mode", "valueList": {"items": [{"name": "A", "value": "1"}, {"name": "B", "value": "2"}], "selected": "B"}},
        {"ref": "flag", "toggle": True},
    ])
    expect(r["ok"], "batch failed", r)
    ids = {ref: (await gh.by_prefix(p))["id"] for ref, p in r["created"].items()}
    one, many = await gh.outputs(ids["one"]), await gh.outputs(ids["many"])
    expect(one[0]["count"] == 1, "multiline on: the whole text is one item", one)
    expect(many[0]["count"] == 2, "multiline off (default): one item per line", many)
    expect(float(await gh.value(ids["count"])) == 8, "slider should be 8")
    expect(await gh.value(ids["mode"]) == "B", "value list should select B")
    expect(await gh.value(ids["flag"]) is True, "toggle should be true")

    r = await gh.batch(set=[
        {"target": "COUNT", "slider": {"min": 1, "max": 20, "value": 12}},
        {"target": "mode", "value": "A"},
        {"target": "flag", "value": False},
        {"target": "many", "panel": "x\ny\nz"},
    ])
    expect(r["ok"] and r["counts"]["set"] == 4, "setting existing objects failed", r)
    expect(float(await gh.value(ids["count"])) == 12, "slider should now be 12")
    expect(await gh.value(ids["mode"]) == "A", "value list should now select A")
    expect(await gh.value(ids["flag"]) is False, "toggle should now be false")
    expect((await gh.outputs(ids["many"]))[0]["count"] == 3, "panel should now give 3 items")

    r = await gh.batch(set=[{"target": "COUNT", "slider": {"value": 40}}])
    expect(not r["ok"] and "outside the range" in " ".join(r["errors"]), "a value outside the range is an error", r)


@check("values", "A value set on an input replaces what it held, including the input's default")
async def values(gh):
    await gh.clear()
    # Series' Start (0), Step (1) and Count (10) inputs hold defaults; setting must replace them.
    r = await gh.batch(components=[{"ref": "ser", "type": "Series"}],
                       set=[{"target": "ser.Start", "value": 5}, {"target": "ser.Count", "value": 3}])
    expect(r["ok"], "batch failed", r)
    out = (await gh.outputs((await gh.by_prefix(r["created"]["ser"]))["id"]))[0]
    expect(out["count"] == 3 and [float(v) for v in out["preview"]] == [5.0, 6.0, 7.0],
           "new Series: Start 5, Count 3 should give 5, 6, 7 (a default left in place adds items)", out)

    # The same on an existing component (a different code path: recorded for undo first).
    r = await gh.batch(set=[{"target": "ser.Start", "value": 10}, {"target": "ser.Count", "value": 2}])
    expect(r["ok"], "setting existing inputs failed", r)
    ser_id = (await gh.components())[0]["id"]
    out = (await gh.outputs(ser_id))[0]
    expect(out["count"] == 2 and [float(v) for v in out["preview"]] == [10.0, 11.0], "existing Series should now give 10, 11", out)


@check("modifiers", "Input and output port settings (graft, flatten, simplify, reverse) are applied")
async def modifiers(gh):
    await gh.clear()
    r = await gh.batch(components=[
        {"ref": "a", "type": "List Item", "inputModifiers": {"L": ["flatten", "simplify"], "i": "graft"}, "outputModifiers": {"i": "reverse"}},
        {"ref": "b", "type": "List Item", "inputModifiers": {"L": "reverse"}, "outputModifiers": {"i": ["graft", "simplify"]}},
        {"ref": "c", "type": "List Item", "outputModifiers": {"i": "flatten"}},
    ])
    expect(r["ok"], "batch failed", r)

    def mods(info, side, nick):
        return next(p for p in info[side] if p["nickname"] == nick)["modifiers"]

    a = await gh.info((await gh.by_prefix(r["created"]["a"]))["id"])
    b = await gh.info((await gh.by_prefix(r["created"]["b"]))["id"])
    c = await gh.info((await gh.by_prefix(r["created"]["c"]))["id"])
    expect(mods(a, "inputs", "L") == {"mapping": "flatten", "simplify": True, "reverse": False}, "a.L flatten+simplify", mods(a, "inputs", "L"))
    expect(mods(a, "inputs", "i")["mapping"] == "graft", "a.i graft")
    expect(mods(a, "outputs", "i")["reverse"], "a output reverse")
    expect(mods(b, "inputs", "L")["reverse"], "b.L reverse")
    expect(mods(b, "outputs", "i") == {"mapping": "graft", "simplify": True, "reverse": False}, "b output graft+simplify")
    expect(mods(c, "outputs", "i")["mapping"] == "flatten", "c output flatten")

    # Port settings on an existing component replace that port's settings (an addition to the first design).
    r = await gh.batch(set=[{"target": r["created"]["a"], "inputModifiers": {"L": "none"}}])
    expect(r["ok"], "clearing an existing port's settings failed", r)
    a = await gh.info(a["id"])
    expect(mods(a, "inputs", "L") == {"mapping": "none", "simplify": False, "reverse": False}, "a.L should be cleared", mods(a, "inputs", "L"))

    r = await gh.batch(components=[{"ref": "x", "type": "List Item", "inputModifiers": {"i": ["graft", "flatten"]}}])
    expect(not r["ok"] and "exclude each other" in " ".join(r["errors"]), "graft + flatten is an error", r)


@check("insert", "insert puts a component into an existing wire; inserting on a wire that doesn't exist is an error")
async def insert(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "points", "panel": "1\n2\n3\n4", "nickname": "POINTS"}, {"ref": "out", "param": "Number", "nickname": "POINT OUT"}],
                       wires=[["POINTS", "POINT OUT"]])
    expect(r["ok"], "setup failed", r)
    r = await gh.batch(
        components=[{"ref": "cull", "type": "Cull Pattern"}, {"ref": "pat", "panel": "true\nfalse", "nickname": "SKIP EVERY OTHER"}],
        insert=[{"ref": "cull", "on": ["POINTS.0", "POINT OUT.0"], "in": "L", "out": "L"}],
        wires=[["pat.0", "cull.P"]],
    )
    expect(r["ok"] and r["counts"]["inserts"] == 1, "insert failed", r)
    wires = (await gh.call("gh_wire", action="list"))["connections"]
    points, out, cull = (await gh.by_nickname("POINTS"))["id"], (await gh.by_nickname("POINT OUT"))["id"], (await gh.by_prefix(r["created"]["cull"]))["id"]
    pairs = {(w["source"]["componentId"], w["target"]["componentId"]) for w in wires}
    expect((points, out) not in pairs, "the original wire should be gone")
    expect((points, cull) in pairs and (cull, out) in pairs, "POINTS -> cull -> POINT OUT expected", wires)
    values = (await gh.outputs(out))[0]["preview"]
    expect([float(v) for v in values] == [1.0, 3.0], "POINT OUT should get every other point", values)

    r = await gh.batch(components=[{"ref": "cull2", "type": "Cull Pattern"}], insert=[{"ref": "cull2", "on": ["POINTS.0", "POINT OUT.0"]}])
    expect(not r["ok"] and not r["changed"] and "no wire" in " ".join(r["errors"]), "inserting on a missing wire is an error", r)


@check("addressing", "Wires to existing objects work by GUID, GUID prefix and unique nickname; a duplicate nickname is an error")
async def addressing(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "p1", "panel": "1", "nickname": "ONE"}, {"ref": "p2", "panel": "2"}, {"ref": "p3", "panel": "3"},
                                   {"ref": "m", "type": "Merge", "inputs": 3, "nickname": "MERGE IN"}])
    expect(r["ok"], "setup failed", r)
    full_guid = (await gh.by_prefix(r["created"]["p2"]))["id"]
    r2 = await gh.batch(wires=[["ONE", "MERGE IN.0"], [full_guid, "MERGE IN.1"], [r["created"]["p3"], f"{r['created']['m']}.2"]])
    expect(r2["ok"] and r2["counts"]["wires"] == 3, "nickname, GUID and prefix wiring failed", r2)

    await gh.call("gh_canvas", action="add", type="Panel", x=0, y=800, nickname="DUPE")
    await gh.call("gh_canvas", action="add", type="Panel", x=0, y=900, nickname="DUPE")
    r3 = await gh.batch(wires=[["DUPE", "MERGE IN.0"]])
    expect(not r3["ok"] and "used by 2 objects" in " ".join(r3["errors"]), "a duplicate nickname must be an error", r3)
    r4 = await gh.batch(components=[{"ref": "q", "panel": "x", "nickname": "ONE"}])
    expect(not r4["ok"] and "already the nickname" in " ".join(r4["errors"]), "giving a used nickname must be an error", r4)


@check("lookup", "Every tool takes gh_batch's names for an object: ref, GUID prefix, unique nickname, and group names in group actions")
async def names_everywhere(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "n_src", "panel": "1\n2", "nickname": "NAME SRC"},
            {"ref": "n_merge", "type": "Merge"},
            {"ref": "n_out", "param": "Number", "nickname": "PAIRS"},
            {"ref": "n_script", "type": "Python 3 Script"},
        ],
        groups=[{"name": "PAIRS", "members": ["n_src", "n_merge", "n_out"]}],
    )
    expect(r["ok"], "setup failed", r)
    prefix = r["created"]["n_src"]

    for given, expected in [("n_merge", "Merge"), (prefix, "Panel"), ("NAME SRC", "Panel"), ("PAIRS", "Number")]:
        info = await gh.call("gh_canvas", action="info", id=given)
        expect(info.get("success") and info.get("name") == expected, f"gh_canvas info id={given!r} should find the {expected}", info)

    full = (await gh.by_prefix(prefix))["id"]
    expect((await gh.call("gh_canvas", action="info", id=full)).get("success"), "a full GUID must still work")

    wired = await gh.call("gh_wire", action="connect", sourceId="NAME SRC", targetId="n_merge", targetParam="0")
    expect(wired.get("success"), "gh_wire connect with a nickname and a ref", wired)
    listed = await gh.call("gh_wire", action="list", componentId="n_merge")
    expect(listed.get("success") and listed.get("count") == 1, "gh_wire list filtered by ref", listed)

    moved = await gh.call("gh_canvas", action="move", moves=json.dumps([{"id": "n_merge", "x": 400, "y": 400}]))
    expect(moved.get("success"), "gh_canvas move by ref", moved)

    out = await gh.call("gh_inspect", action="outputs", id="NAME SRC")
    expect(out.get("success"), "gh_inspect outputs by nickname", out)

    script = await gh.call("gh_script", action="info", id="n_script")
    expect(script.get("success"), "gh_script info by ref (as an agent uses it after a batch)", script)

    # A group and a parameter share the name PAIRS: group actions take the group, others the parameter.
    renamed = await gh.call("gh_canvas", action="group_rename", id="PAIRS", name="PAIRS BLOCK")
    expect(renamed.get("success") and renamed.get("name") == "PAIRS BLOCK", "group_rename by the group's name", renamed)
    added = await gh.call("gh_canvas", action="group_add", id="PAIRS BLOCK", ids=json.dumps(["n_script"]))
    expect(added.get("success") and added.get("addedCount") == 1, "group_add by group name with a ref as member", added)

    missing = await gh.call("gh_canvas", action="info", id="NAME SCR")
    expect(not missing.get("success") and "Did you mean 'NAME SRC'" in str(missing.get("error")), "a typo should be refused with a suggestion", missing)


@check("refs", "Refs created in one batch work in the next")
async def refs(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "split", "type": "Text Split"}, {"ref": "txt", "panel": "a,b,c"}, {"ref": "sep", "panel": ","}],
                       wires=[["txt", "split.0"], ["sep", "split.1"]])
    expect(r["ok"], "first batch failed", r)
    r = await gh.batch(components=[{"ref": "shown", "panel": ""}], wires=[["split.0", "shown"]])
    expect(r["ok"], "a ref from the previous batch should resolve", r)
    out = await gh.outputs((await gh.by_prefix(r["created"]["shown"]))["id"])
    expect(out[0]["count"] == 3, "split should give 3 parts", out)
    r = await gh.batch(components=[{"ref": "split", "panel": "x"}])
    expect(not r["ok"] and "already names" in " ".join(r["errors"]), "re-using a live ref must be an error", r)

    # Deleting the object frees its ref in the same batch (delete runs first).
    r = await gh.batch(delete=["split"], components=[{"ref": "split", "type": "Text Split"}], wires=[["txt", "split.0"]])
    expect(r["ok"] and r["counts"]["deleted"] == 1, "delete and re-create the same ref in one batch", r)
    info = await gh.call("gh_canvas", action="info", id="split")
    expect(info.get("success") and info.get("id", "").startswith(r["created"]["split"]), "the ref should now name the new object", info)


@check("groups", "Groups can be created and extended")
async def groups(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "a", "panel": "a"}, {"ref": "b", "panel": "b"}, {"ref": "c", "panel": "c"}],
                       groups=[{"name": "PAIRS", "color": "#DCEDC8", "members": ["a", "b"]}])
    expect(r["ok"] and r["counts"]["groups"] == 1, "creating a group failed", r)
    g = [x for x in await gh.groups() if x["name"] == "PAIRS"]
    expect(len(g) == 1 and g[0]["memberCount"] == 2, "PAIRS should have 2 members", g)
    r = await gh.batch(groups=[{"extend": "PAIRS", "members": ["c"]}])
    expect(r["ok"], "extending failed", r)
    g = [x for x in await gh.groups() if x["name"] == "PAIRS"]
    expect(g[0]["memberCount"] == 3, "PAIRS should now have 3 members", g)
    r = await gh.batch(groups=[{"name": "PAIRS", "members": ["c"]}])
    expect(not r["ok"] and "extend" in " ".join(r["errors"]), "creating a second PAIRS should suggest extend", r)


@check("undo", "One Ctrl+Z reverts the whole batch")
async def undo(gh, interactive=True):
    await gh.clear()
    base = await gh.batch(components=[
        {"ref": "u_src", "panel": "1\n2", "nickname": "U SRC"},
        {"ref": "u_sl", "slider": {"min": 0, "max": 10, "value": 3}, "nickname": "U SLIDER"},
        {"ref": "u_m", "type": "Merge", "nickname": "U MERGE"},
        {"ref": "u_old", "panel": "old", "nickname": "U OLD"},
        {"ref": "u_cull", "type": "Cull Pattern", "nickname": "U CULL"},
    ], wires=[["U SRC", "U MERGE.0"], ["U SLIDER", "U MERGE.1"], ["U OLD", "U CULL.L"]],
        groups=[{"name": "U GROUP", "members": ["u_src"]}])
    expect(base["ok"], "setup failed", base)
    slider_id = (await gh.by_nickname("U SLIDER"))["id"]
    before = await gh.listing()

    # One batch that touches every kind of change: delete, disconnect, create, set (value, nickname,
    # port setting), insert, wires, group.
    r = await gh.batch(
        delete=["U OLD"],
        disconnect=[["U SLIDER", "U MERGE.1"]],
        components=[{"ref": "u_new", "type": "Merge", "inputs": 3}, {"ref": "u_p", "panel": "x"}, {"ref": "u_ins", "param": "Number"}],
        set=[{"target": "U SLIDER", "value": 7}, {"target": "U MERGE", "nickname": "U MERGE RENAMED"},
             {"target": "U CULL", "inputModifiers": {"L": "graft"}}],
        insert=[{"ref": "u_ins", "on": ["u_src.0", "u_m.0"]}],  # refs: U MERGE is renamed by set, which runs first
        wires=[["u_p", "u_new.2"], ["U SLIDER", "u_new.0"]],
        groups=[{"extend": "U GROUP", "members": ["u_p"]}],
    )
    expect(r["ok"], "the batch to undo failed", r)
    after_batch = await gh.listing()
    expect(not same_listing(before, after_batch), "the batch should have changed the canvas")

    if not interactive:
        raise CheckFailed("SKIPPED (run without --skip-undo to press Ctrl+Z)")

    print("\n      >>> Click the Grasshopper canvas, then press Ctrl+Z ONCE. Waiting up to 90 s ...", flush=True)
    deadline = time.time() + 90
    current = after_batch
    while time.time() < deadline:
        await asyncio.sleep(1)
        await gh.guard()
        current = await gh.listing()
        if not same_listing(current, after_batch):
            await asyncio.sleep(1.5)  # let the undo finish
            current = await gh.listing()
            break
    else:
        raise CheckFailed("no Ctrl+Z seen within 90 s")

    expect(same_listing(before, current), "after one Ctrl+Z the canvas should match the listing taken before the batch",
           diff_listing(before, current))
    expect(float(await gh.value(slider_id)) == 3, "the slider value should be back to 3")
    print("      >>> Thanks. (Ctrl+Y would redo it; not needed.)", flush=True)


@check("solver", "Solver on stays on and solves once; solver off stays off and doesn't solve")
async def solver(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "s", "slider": {"min": 0, "max": 10, "value": 1}, "nickname": "S IN"},
            {"ref": "p", "panel": "2", "nickname": "P IN"},
            {"ref": "add", "type": "Addition"},
            {"ref": "rec", "type": "Data Recorder"},
        ],
        wires=[["S IN", "add.0"], ["P IN", "add.1"], ["add.0", "rec.0"]],
    )
    expect(r["ok"] and r["solved"], "setup failed", r)
    rec_id = (await gh.by_prefix(r["created"]["rec"]))["id"]

    async def recorded():
        out = await gh.outputs(rec_id)
        return out[0]["count"] if out else 0

    n0 = await recorded()
    expect(n0 >= 1, "the Data Recorder recorded nothing, so solves can't be counted", await gh.outputs(rec_id))

    # Two changes upstream of the recorder in one batch: exactly one new record means one solve.
    r = await gh.batch(set=[{"target": "S IN", "value": 5}, {"target": "P IN", "panel": "3"}])
    expect(r["ok"] and r["solved"] is True and "solve" in r.get("ms", {}), "solver on: the batch should solve", r)
    n1 = await recorded()
    expect(n1 == n0 + 1, f"expected exactly one solve (recorder {n0} -> {n0 + 1}), got {n0} -> {n1}")
    expect((await gh.call("gh_document", action="info"))["solverEnabled"], "solver should still be on")

    await gh.call("gh_document", action="solver", enabled="false")
    turned_back_on = False
    try:
        r = await gh.batch(set=[{"target": "S IN", "value": 7}])
        expect(r["ok"] and r["changed"] and r["solved"] is False, "solver off: applied but not solved", r)
        expect(any("solver is off" in w for w in r["warnings"]), "the result should say the solver is off", r)
        expect(not (await gh.call("gh_document", action="info"))["solverEnabled"], "solver should still be off")
        # While unsolved, the recorder's output is simply cleared. The proof that the batch did not
        # solve: turning the solver back on solves once, which must add exactly one record.
        await gh.call("gh_document", action="solver", enabled="true")
        turned_back_on = True
        n2 = await recorded()
        expect(n2 == n1 + 1, f"solver off: no solve expected during the batch (recorder {n1} -> {n1 + 1} after turning it on), got {n1} -> {n2}")
    finally:
        if not turned_back_on:
            await gh.call("gh_document", action="solver", enabled="true")


@check("performance", "300 components and 500 wires apply in under 5 seconds, excluding solve")
async def performance(gh):
    await gh.clear()
    components, wires = [], []
    for i in range(100):
        components.append({"ref": f"n{i}", "panel": str(i)})
    for i in range(200):
        components.append({"ref": f"add{i}", "type": "Addition", "inputs": 3})
        wires.append([f"n{i % 100}", f"add{i}.0"])
        wires.append([f"n{(i + 1) % 100}", f"add{i}.1"])
        if i:
            wires.append([f"add{i - 1}.0", f"add{i}.2"])
    started = time.time()
    r = await gh.batch(components=components, wires=wires, options={"solve": False})
    round_trip = time.time() - started
    expect(r["ok"] and r["counts"]["components"] == 300 and r["counts"]["wires"] == len(wires), "big batch failed", r)
    print(f"      300 components, {len(wires)} wires: apply {r['ms']['apply']} ms, round trip {round_trip * 1000:.0f} ms, "
          f"result {len(json.dumps(r))} chars", flush=True)
    expect(r["ms"]["apply"] < 5000, f"apply took {r['ms']['apply']} ms")
    expect(len(json.dumps(r)) < 4096, "the result should stay under 4 KB")
    await gh.clear()


@check("tree", "The tree action reports correct paths and counts on a known test definition")
async def tree(gh):
    await gh.clear()
    # A known definition: six numbers, partitioned in twos, grafted, and flattened back.
    r = await gh.batch(
        components=[
            {"ref": "nums", "panel": "1\n2\n3\n4\n5\n6", "nickname": "NUMS"},
            {"ref": "size", "panel": "2"},
            {"ref": "part", "type": "Partition List"},
            {"ref": "grafted", "param": "Number", "inputModifiers": {"0": "graft"}},
            {"ref": "flat", "type": "Partition List", "outputModifiers": {"C": "flatten"}},
        ],
        wires=[["NUMS", "part.L"], ["size", "part.S"], ["NUMS", "grafted"], ["NUMS", "flat.L"], ["size", "flat.S"]],
    )
    expect(r["ok"] and r["solved"], "setup failed", r)

    def get(trees, target):
        found = [t for t in trees if t["target"] == target]
        expect(len(found) == 1, f"expected one tree for {target}", trees)
        return found[0]

    res = await gh.call("gh_inspect", action="tree", id=json.dumps(["NUMS", "part.0", "grafted", "flat.C"]))
    expect(res["success"] and len(res["trees"]) == 4, "tree on four outputs failed", res)
    nums, part, grafted, flat = (get(res["trees"], t) for t in ["NUMS", "part.0", "grafted", "flat.C"])

    expect(nums["branches"] == 1 and nums["items"] == 6 and nums["paths"] == ["{0}: 6"], "NUMS: {0} with 6 items", nums)
    expect(part["branches"] == 3 and part["items"] == 6 and part["perBranch"] == 2 and part["depth"] == 2
           and part["paths"] == ["{0;0}..{0;2}: 2 each"], "part: {0;0}..{0;2}, 2 each", part)
    expect(grafted["branches"] == 6 and grafted["perBranch"] == 1 and grafted["paths"] == ["{0;0}..{0;5}: 1 each"],
           "grafted: {0;0}..{0;5}, 1 each", grafted)
    expect(any("grafted shape" in n for n in grafted.get("notes", [])), "grafted: the note should say so", grafted)
    expect(flat["branches"] == 1 and flat["items"] == 6 and flat["paths"] == ["{0}: 6"], "flat: {0} with 6 items", flat)

    # A component without a port lists every output; a typo is an error but the rest still comes back.
    res = await gh.call("gh_inspect", action="tree", id=json.dumps(["part", "nosuch"]))
    expect(not res["success"] and len(res["trees"]) == 1 and "no object named 'nosuch'" in " ".join(res["errors"]),
           "a typo should be reported alongside the trees that worked", res)
    expect(len(json.dumps(res)) < 2000, "tree results should be compact")


@check("outline", "outline reads the canvas in one call: groups and what crosses their borders, a group's wiring, the objects outside groups, what is around one object, a canvas without groups and one of its networks")
async def outline(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "o_count", "slider": {"min": 0, "max": 10, "value": 4, "decimals": 0}, "nickname": "O COUNT"},
            {"ref": "o_start", "panel": "1", "nickname": "O START"},
            {"ref": "o_series", "type": "Series"},
            {"ref": "o_result", "param": "Number", "nickname": "O RESULT"},
            {"ref": "o_bad", "panel": "abc"},
            {"ref": "o_pt", "type": "Construct Point"},
            {"ref": "o_loose", "panel": "loose note", "nickname": "O LOOSE"},
        ],
        wires=[["O COUNT", "o_series.C"], ["O START", "o_series.S"], ["o_series.S", "O RESULT"], ["o_bad", "o_pt.X"]],
        groups=[{"name": "O INPUTS", "members": ["o_count", "o_start"]},
                {"name": "O MATHS", "members": ["o_series", "o_result"]},
                {"name": "O INNER", "members": ["o_bad", "o_pt"]}],
    )
    expect(r["ok"], "setup failed", r)
    nested = await gh.call("gh_canvas", action="group_add", id="O MATHS", ids=json.dumps(["O INNER"]))
    expect(nested["success"], "putting O INNER inside O MATHS failed", nested)

    ov = await gh.call("gh_canvas", action="outline")
    expect(ov["success"] and "7 objects" in ov["summary"] and "3 groups" in ov["summary"] and "1 error" in ov["summary"],
           "summary: 7 objects, 3 groups, 1 error", ov)
    expect("O COUNT = 4 (0..10) [O INPUTS]" in ov.get("controls", []), "the slider should be listed with its value and range", ov)
    lines = ov.get("groups", [])
    expect(len(lines) == 2 and lines[0].startswith("O INPUTS") and lines[1].startswith("O MATHS"),
           "two top-level groups, O INPUTS first because it feeds O MATHS", lines)
    expect("contains: O INNER (2 objects)" in lines[1], "the nested group should be listed under its parent", lines[1])
    expect("in ← O COUNT, O START" in lines[1] or "in ← O START, O COUNT" in lines[1],
           "O MATHS should say what flows in", lines[1])
    expect("out: " in lines[0] and "O COUNT (1) → O MATHS" in lines[0], "O INPUTS should say what flows out, with item counts", lines[0])
    expect("O LOOSE" in ov.get("ungrouped", ""), "the loose panel should be in 'ungrouped'", ov)
    expect("group='ungrouped'" in ov.get("next", ""), "the hint should name group='ungrouped' when objects sit outside groups", ov)
    expect(any("Construct Point" in p and "error" in p for p in ov.get("problems", [])), "the failing Construct Point should be in 'problems'", ov)
    expect(len(json.dumps(ov)) < 2500, f"the overview should be short ({len(json.dumps(ov))} chars)")

    det = await gh.call("gh_canvas", action="outline", group="O MATHS")
    expect(det["success"] and len(det["groups"]) == 1, "group detail failed", det)
    objs = det["groups"][0]["objects"]
    series = [l for l in objs if " Series" in l]
    expect(len(series) == 1 and "S ← O START [O INPUTS]" in series[0] and "C ← O COUNT [O INPUTS]" in series[0],
           "the Series line should name what feeds it", objs)
    expect(any(l.startswith("O INNER · 2 objects") and "group='O INNER'" in l for l in objs), "the child group should be one line", objs)
    result_i = next(i for i, l in enumerate(objs) if '"O RESULT"' in l)
    expect(objs.index(series[0]) < result_i, "upstream first: Series before O RESULT", objs)

    by_start = await gh.call("gh_canvas", action="outline", group="O MAT")
    expect(by_start["success"] and by_start["groups"][0]["group"] == "O MATHS", "the start of a unique name should find the group", by_start)
    two = await gh.call("gh_canvas", action="outline", group=json.dumps(["O INPUTS", "O INNER"]))
    expect(two["success"] and [g["group"] for g in two["groups"]] == ["O INPUTS", "O INNER"], "a JSON list of groups", two)
    vague = await gh.call("gh_canvas", action="outline", group="O")
    expect(not vague["success"] and "matches 3 groups" in vague["error"], "a name matching several groups is an error", vague)
    loose = await gh.call("gh_canvas", action="outline", group="ungrouped")
    expect(loose["success"] and loose["groups"][0]["group"] == "ungrouped"
           and loose["groups"][0]["summary"].startswith("1 object · outside any group")
           and any('"O LOOSE"' in l for l in loose["groups"][0]["objects"]),
           "group='ungrouped' should list the loose panel", loose)

    around = await gh.call("gh_canvas", action="outline", id="O RESULT", depth=1)
    expect(around["success"] and any(l.startswith("* ") and '"O RESULT"' in l for l in around["objects"]),
           "around: the object itself is marked '*'", around)
    expect(any(" Series" in l for l in around["objects"]) and "O COUNT" in around.get("fedFrom", ""),
           "around, depth 1: Series is shown, and what feeds it beyond that is named", around)

    # A canvas without groups is described by its networks.
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "n_count", "slider": {"min": 1, "max": 20, "value": 5, "decimals": 0}, "nickname": "N COUNT"},
            {"ref": "n_series", "type": "Series"},
            {"ref": "n_end", "param": "Number", "nickname": "N END"},
            {"ref": "n_note", "panel": "a note", "nickname": "N NOTE"},
        ],
        wires=[["N COUNT", "n_series.C"], ["n_series.S", "N END"]],
    )
    expect(r["ok"], "setup without groups failed", r)
    ov = await gh.call("gh_canvas", action="outline")
    networks = ov.get("networks", [])
    expect("groups" not in ov and len(networks) == 1, "one network, no groups", ov)
    expect("inputs: N COUNT = 5 (1..20)" in networks[0] and "ends: N END (5)" in networks[0],
           "the network should name its input and its end", networks)
    expect("N NOTE" in ov.get("unconnected", ""), "the lone panel should be 'unconnected'", ov)
    expect(ov.get("next", "").startswith("group='network'"), "the hint should name group='network'", ov)

    # The whole network in one call, upstream first, in the group view's format.
    net = await gh.call("gh_canvas", action="outline", group="network")
    expect(net["success"], "group='network' failed", net)
    g = net["groups"][0]
    objs = g["objects"]
    expect(g["group"] == "network" and g["summary"].startswith("3 objects · the only network") and len(objs) == 3,
           "the network view should list its 3 objects", net)
    expect('"N COUNT"' in objs[0] and " Series" in objs[1] and "C ← N COUNT" in objs[1] and '"N END"' in objs[2],
           "upstream first: N COUNT, Series (fed by N COUNT), N END", objs)
    same = await gh.call("gh_canvas", action="outline", group="network 1")
    expect(same["success"] and same["groups"][0]["objects"] == objs, "'network 1' should open the only network too", same)
    missing = await gh.call("gh_canvas", action="outline", group="network 2")
    expect(not missing["success"] and "group='network'" in missing["error"], "'network 2' should say there is one network", missing)


@check("sweep", "sweep moves a slider, value list or toggle through values, reports each, and always puts it back")
async def sweep(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "w_count", "slider": {"min": 1, "max": 10, "value": 3, "decimals": 0}, "nickname": "W COUNT"},
            {"ref": "w_series", "type": "Series"},
            {"ref": "w_out", "param": "Number", "nickname": "W OUT"},
            {"ref": "w_list", "valueList": {"items": [{"name": "A", "value": "1"}, {"name": "B", "value": "2"}], "selected": "A"}, "nickname": "W LIST"},
            {"ref": "w_list_out", "param": "Number", "nickname": "W LIST OUT"},
            {"ref": "w_toggle", "toggle": False, "nickname": "W TOGGLE"},
            {"ref": "w_toggle_out", "param": "Boolean", "nickname": "W TOGGLE OUT"},
            {"ref": "w_note", "panel": "a note", "nickname": "W NOTE"},
        ],
        wires=[["W COUNT", "w_series.C"], ["w_series.S", "W OUT"], ["W LIST", "W LIST OUT"], ["W TOGGLE", "W TOGGLE OUT"]],
    )
    expect(r["ok"] and r["solved"], "setup failed", r)

    res = await gh.call("gh_document", action="sweep", id="W COUNT", values="[2, 5, 8]", watch='["W OUT"]')
    lines = res.get("results", [])
    expect(res["success"] and len(lines) == 3, "a three-value sweep should give three lines", res)
    for value, line in zip([2, 5, 8], lines):
        expect(line.startswith(f"{value} → W OUT {value}"), f"at {value}, W OUT should hold {value} items", line)
    expect(res.get("restored") == "W COUNT = 3" and float(await gh.value("W COUNT")) == 3, "the slider should be back at 3", res)
    expect("W COUNT (slider 1..10), was 3" == res.get("control"), "the control line names the slider, its range and its value", res)

    every = await gh.call("gh_document", action="sweep", id="W COUNT", watch="W OUT")
    expect(every["success"] and len(every["results"]) == 10, "no values on a 1..10 whole-number slider: every step", every)

    lst = await gh.call("gh_document", action="sweep", id="W LIST", watch='["W LIST OUT"]')
    expect(lst["success"] and [l.split(" →")[0] for l in lst["results"]] == ["A", "B"], "no values on a value list: every item", lst)
    expect("[1]" in lst["results"][0] and "[2]" in lst["results"][1], "the value list's output should show each item's value", lst)
    expect(lst.get("restored") == "W LIST = A", "the value list should be back on A", lst)

    tog = await gh.call("gh_document", action="sweep", id="W TOGGLE", watch="W TOGGLE OUT")
    expect(tog["success"] and tog["results"][0].startswith("false →") and "[true]" in tog["results"][1], "the toggle should be tried false then true", tog)
    expect(tog.get("restored") == "W TOGGLE = false", "the toggle should be back off", tog)

    bad = await gh.call("gh_document", action="sweep", id="W COUNT", values="[0, 4, 11]", watch="W OUT")
    expect(not bad["success"] and "0 is outside" in bad["error"] and "11 is outside" in bad["error"], "out-of-range values are all reported", bad)
    expect(float(await gh.value("W COUNT")) == 3, "a refused sweep changes nothing")

    panel = await gh.call("gh_document", action="sweep", id="W NOTE", values="[1]")
    expect(not panel["success"] and "number slider, a value list or a toggle" in panel["error"], "a panel can't be swept", panel)

    await gh.call("gh_document", action="solver", enabled="false")
    try:
        off = await gh.call("gh_document", action="sweep", id="W COUNT", values="[4]")
        expect(not off["success"] and "solver is off" in off["error"], "sweep is refused while the solver is off", off)
        expect(not (await gh.call("gh_document", action="info"))["solverEnabled"], "the solver should stay off")
    finally:
        await gh.call("gh_document", action="solver", enabled="true")
    expect(len(json.dumps(res)) < 1500, "sweep results should be short")


@check("script", "gh_script set/configure with solve=true solve once and return the script's results and messages (remarks too); without solve nothing changes; remarks aren't counted as warnings")
async def script(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "sc", "type": "Python 3 Script"}])
    expect(r["ok"], "setup failed", r)

    def result(res, name):
        found = [x for x in res.get("results", []) if x["name"] == name]
        expect(len(found) == 1, f"results should hold output '{name}'", res)
        return found[0]

    res = await gh.call("gh_script", action="configure", id="sc", inputs="[]", outputs=json.dumps([{"name": "a"}]),
                        code="#! python 3\na = 21 * 2", solve=True)
    expect(res["success"] and res.get("solved") is True and "solveMs" in res, "configure with solve=true should solve", res)
    a = result(res, "a")
    expect(a["count"] == 1 and a["preview"] == ["42"], "the script's output should come back in results", a)
    expect(res.get("messages") == [], "a script that runs cleanly has no messages", res)

    res = await gh.call("gh_script", action="set", id="sc", code="#! python 3\na = 1 / 0", solve=True)
    expect(res["success"] and res.get("solved") is True, "set with solve=true should solve", res)
    expect(any(m["level"] == "error" for m in res.get("messages", [])), "the script's error should be in messages", res)

    res = await gh.call("gh_script", action="set", id="sc", code="#! python 3\na = 5")
    expect(res["success"] and "results" not in res and "solved" not in res, "without solve, set answers as before", res)

    await gh.call("gh_document", action="solver", enabled="false")
    try:
        res = await gh.call("gh_script", action="set", id="sc", code="#! python 3\na = 6", solve=True)
        expect(res["success"] and res.get("solved") is False and "solver is off" in res.get("notSolved", ""),
               "with the solver off, the code is written but not solved", res)
        expect(not (await gh.call("gh_document", action="info"))["solverEnabled"], "the solver should stay off")
    finally:
        await gh.call("gh_document", action="solver", enabled="true")

    res = await gh.call("gh_script", action="set", id="sc", code="#! python 3\na = 7", solve=True)
    expect(result(res, "a")["preview"] == ["7"], "after turning the solver back on, set with solve=true shows the new value", res)

    # A script's remarks come back with its results, and on their own they aren't counted as warnings
    # (Grasshopper numbers a remark 255, above an error).
    r = await gh.batch(components=[{"ref": "rm", "type": "C# Script", "nickname": "REMARKS"}])
    expect(r.get("ok"), "setup of the remark script failed", r)
    remark_code = ("// Grasshopper Script Instance\n#region Usings\nusing System;\nusing Rhino;\nusing Grasshopper;\n"
                   "using Grasshopper.Kernel;\n#endregion\n\npublic class Script_Instance : GH_ScriptInstance\n{\n"
                   "    private void RunScript(ref object a, ref object levels)\n    {\n"
                   "        Component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, \"remark one\");\n"
                   "        Component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, \"remark two\");\n"
                   "        a = 1;\n"
                   "        levels = string.Format(\"{0} {1} {2} {3}\", (int)GH_RuntimeMessageLevel.Blank, (int)GH_RuntimeMessageLevel.Warning,\n"
                   "            (int)GH_RuntimeMessageLevel.Error, (int)GH_RuntimeMessageLevel.Remark);\n    }\n}")
    res = await gh.call("gh_script", action="configure", id="REMARKS", inputs="[]",
                        outputs=json.dumps([{"name": "a"}, {"name": "levels"}]), code=remark_code, solve=True)
    expect(res.get("success") and {"level": "remark", "text": "remark one"} in res.get("messages", []),
           "solve=true should return the script's remarks", res)
    # Core/MessageLevels hard-codes Grasshopper's numbers (the unit tests have no Grasshopper): check them here.
    expect(result(res, "levels")["preview"] == ["0 10 20 255"],
           "Grasshopper's message level numbers (Blank, Warning, Error, Remark) should be the ones Core/MessageLevels uses", res)
    out = await gh.call("gh_canvas", action="outline")
    expect(out.get("summary", "").endswith("0 errors · 0 warnings") and not out.get("problems"),
           "remarks alone are not counted or listed as warnings", out)
    near = await gh.call("gh_canvas", action="outline", id="REMARKS", depth=0)
    expect(any("remark: remark one (+1 more)" in line for line in near.get("objects", [])),
           "outline shows a component's remarks on its line", near)


@check("edit", "gh_script edit replaces only the passages sent, across line endings, solves with solve=true, and changes nothing when an edit doesn't match once")
async def edit(gh):
    await gh.clear()
    r = await gh.batch(components=[{"ref": "ed", "type": "Python 3 Script", "nickname": "EDITED"}])
    expect(r["ok"], "setup failed", r)
    res = await gh.call("gh_script", action="configure", id="EDITED", inputs="[]", outputs=json.dumps([{"name": "report"}]),
                        code="#! python 3\ntol = 0.01\nscale = 2\nreport = 'tol %s scale %s' % (tol, scale)", solve=True)
    expect(res["success"], "configuring the script failed", res)

    def report(res):
        found = [x for x in res.get("results", []) if x["name"] == "report"]
        return found[0]["preview"] if found else None

    res = await gh.call("gh_script", action="edit", id="EDITED", solve=True,
                        edits=json.dumps([{"old": "tol = 0.01\nscale = 2", "new": "tol = 0.5\nscale = 3"}]))
    expect(res["success"] and res.get("edited") == 1 and res.get("solved") is True, "a two-line edit should apply and solve", res)
    expect(report(res) == ["tol 0.5 scale 3"], "the script should run the edited code", res)
    expect("source" not in res and isinstance(res.get("editedLines"), list), "edit reports the lines, not the source", res)

    before = (await gh.call("gh_script", action="get", id="EDITED"))["source"]
    missing = await gh.call("gh_script", action="edit", id="EDITED", edits=json.dumps([{"old": "tol = 0.9", "new": "tol = 1"}]))
    expect(not missing["success"] and "isn't in the script" in missing["error"] and "Nothing was changed" in missing["error"],
           "an edit whose text isn't in the script is refused", missing)
    expect("now read:" in missing["error"] and "2: tol = 0.5" in missing["error"],
           "a refused edit shows the lines around the closest match, so the script needn't be read again", missing)
    twice = await gh.call("gh_script", action="edit", id="EDITED", edits=json.dumps([{"old": "tol", "new": "t"}]))
    expect(not twice["success"] and "occurs" in twice["error"] and "times" in twice["error"], "an edit whose text occurs several times is refused", twice)
    expect((await gh.call("gh_script", action="get", id="EDITED"))["source"] == before, "refused edits change nothing")

    # Review finding 4: trailing spaces the script has and the edit's text hasn't are ignored, and said so.
    res = await gh.call("gh_script", action="set", id="EDITED",
                        code="#! python 3\ntol = 0.5   \nscale = 3\nreport = 'tol %s scale %s' % (tol, scale)")
    expect(res.get("success"), "setting a script with trailing spaces failed", res)
    kept = "tol = 0.5   \n" in (await gh.call("gh_script", action="get", id="EDITED"))["source"].replace("\r\n", "\n")
    res = await gh.call("gh_script", action="edit", id="EDITED", solve=True,
                        edits=json.dumps([{"old": "tol = 0.5\nscale = 3", "new": "tol = 0.25\nscale = 3"}]))
    expect(res.get("success") and report(res) == ["tol 0.25 scale 3"], "an edit differing only in trailing spaces should apply", res)
    if kept:
        expect(any("matched ignoring trailing spaces on line" in n for n in res.get("notes", [])),
               "the answer should say the edit matched ignoring trailing spaces", res)


@check("watch", "gh_batch options.watch reads the named outputs back after the one solve; a bad name changes nothing")
async def watch(gh):
    await gh.clear()
    r = await gh.batch(
        components=[
            {"ref": "v_count", "slider": {"min": 1, "max": 10, "value": 4, "decimals": 0}, "nickname": "V COUNT"},
            {"ref": "v_series", "type": "Series"},
            {"ref": "v_out", "param": "Number", "nickname": "V OUT"},
            {"ref": "v_note", "panel": "hello", "nickname": "V NOTE"},
        ],
        wires=[["V COUNT", "v_series.C"], ["v_series.S", "V OUT"]],
        options={"watch": ["V OUT", "v_note", "v_series"]},
    )
    expect(r["ok"] and r["solved"], "setup failed", r)
    expect(r.get("watch") == ["V OUT 4", 'v_note 1 ["hello"]', "v_series.S 4"],
           "watch should give a line per output: a nickname, a ref, and a component's outputs", r)

    r = await gh.batch(set=[{"target": "V COUNT", "value": 6}], options={"watch": ["V OUT"]})
    expect(r["ok"] and r.get("watch") == ["V OUT 6"], "watch should read existing outputs after the solve", r)

    # An item past the end of a list (wrap off) is empty: the line must say so, not count it as an item.
    nulls = await gh.batch(components=[{"ref": "v_item", "type": "List Item"}],
                           set=[{"target": "v_item.i", "value": 20}, {"target": "v_item.W", "value": False}],
                           wires=[["V OUT", "v_item.L"]], options={"watch": ["v_item.0"]})
    expect(nulls["ok"] and nulls.get("watch") == ["v_item.0 1 (1 empty)"],
           "watch should say how many items are empty", nulls)

    bad = await gh.batch(set=[{"target": "V COUNT", "value": 2}], options={"watch": ["NO SUCH OUTPUT"]})
    expect(not bad["ok"] and not bad["changed"] and any(e.startswith("options.watch[0]") for e in bad["errors"]),
           "a watch name that doesn't resolve is an error", bad)
    expect("watch" not in bad, "a rejected batch has no watch lines", bad)
    expect(float(await gh.value("V COUNT")) == 6, "a rejected batch changes nothing")

    dry = await gh.batch(set=[{"target": "V COUNT", "value": 2}], options={"dryRun": True, "watch": ["V OUT"]})
    expect(dry["ok"] and dry.get("dryRun") and "watch" not in dry, "a dry run checks watch names but reads nothing", dry)

    unsolved = await gh.batch(components=[{"ref": "v_series2", "type": "Series"}],
                              options={"solve": False, "watch": ["v_series2.S"]})
    expect(unsolved["ok"] and unsolved.get("watch") == ["v_series2.S 0 (not solved)"],
           "with solve false, watch says the output isn't solved", unsolved)

    empty = await gh.batch(options={"watch": ["V OUT"]})
    expect(not empty["ok"] and "gh_inspect(action='tree')" in empty["errors"][0],
           "a batch with only watch points to gh_inspect tree", empty)
    expect(len(json.dumps(r)) < 1500, "a watched batch result should stay short")


@check("thallus", "Lichen Thalli: outline shows them with the author's text, and no tool changes them (needs the Thallus test file)", clears=False)
async def thallus(gh):
    ov = await gh.call("gh_canvas", action="outline")
    thalli = [l for l in ov.get("groups", []) if l.startswith("THALLUS ")]
    if not thalli:
        raise CheckFailed("SKIPPED (needs the file with Lichen Thalli: --scratch scratch\\hyperceps-thallus.gh --only thallus)")
    before = await gh.listing()

    outer_line = next((l for l in thalli if "contains: THALLUS " in l), None)
    expect(outer_line is not None, "a Thallus inside another should be listed under its parent", thalli)
    expect("author's description" in outer_line, "the outer Thallus line should carry the author's description", outer_line)
    outer_name = outer_line[len("THALLUS "):].split(" · ")[0]

    det = (await gh.call("gh_canvas", action="outline", group=outer_name))["groups"][0]
    expect(det.get("authorDescription") and det.get("authorProperties"), "the detail should carry the author's description and properties", det)
    child = [l for l in det["objects"] if l.startswith("THALLUS ")]
    expect(child, "the inner Thallus should be one line in the outer one's detail", det)
    inner_name = child[0][len("THALLUS "):].split(" · ")[0]
    inner = (await gh.call("gh_canvas", action="outline", group=inner_name))["groups"][0]
    expect(f"inside THALLUS {outer_name}" in inner["summary"], "the inner Thallus should say which one it is inside", inner)
    member = next((l.split(" ")[0] for l in inner["objects"] if not l.startswith("THALLUS ")), None)
    expect(member, "the inner Thallus should have an object of its own", inner)
    expect("Thallus Output" not in json.dumps([ov, det, inner]), "the hidden T outputs should never show in the outline")

    endpoints = [c["id"] for c in await gh.components() if c.get("name") == "Thallus Output"]
    expect(endpoints, "the Thalli's hidden T outputs should be on the canvas")

    def refused(res, words):
        text = " ".join(res.get("errors", [])) + " " + str(res.get("error", ""))
        return not res.get("success") and words in text

    attempts = [
        ("gh_batch extend", await gh.batch(groups=[{"extend": inner_name, "members": [member]}]), "Lichen Thallus"),
        ("gh_batch delete T output", await gh.batch(delete=[endpoints[0]]), "hidden T output"),
        ("gh_batch group with T output", await gh.batch(groups=[{"name": "TMP GROUP", "members": [endpoints[0]]}]), "hidden T output"),
        ("gh_canvas delete T output", await gh.call("gh_canvas", action="delete", id=endpoints[0]), "hidden T output"),
        ("gh_canvas group_add", await gh.call("gh_canvas", action="group_add", id=inner_name, ids=json.dumps([member])), "Lichen Thallus"),
        ("gh_canvas group_remove", await gh.call("gh_canvas", action="group_remove", id=inner_name, ids=json.dumps([member])), "Lichen Thallus"),
        ("gh_canvas group_rename", await gh.call("gh_canvas", action="group_rename", id=inner_name, name="RENAMED"), "Lichen Thallus"),
        ("gh_canvas group_color", await gh.call("gh_canvas", action="group_color", id=inner_name, color="#FF0000"), "Lichen Thallus"),
        ("gh_canvas group_delete", await gh.call("gh_canvas", action="group_delete", id=inner_name), "Lichen Thallus"),
        ("gh_canvas group_create with T output", await gh.call("gh_canvas", action="group_create", name="TMP GROUP", ids=json.dumps([endpoints[0]])), "hidden T output"),
    ]
    for what, res, words in attempts:
        expect(refused(res, words), f"{what} should be refused", res)

    after = await gh.listing()
    expect(same_listing(before, after), "nothing on the canvas may change", diff_listing(before, after))


@check("busy", "While Grasshopper is held by a slow script, calls answer 'busy' within about 15 s instead of hanging, and gh_batch changes nothing; a slow sweep answers before re-solving the restored value, and a sweep or batch sent during a solve waits through it (no false dialog)")
async def busy(gh):
    await gh.clear()
    r = await gh.batch(
        components=[{"ref": "b_sleeper", "type": "Python 3 Script", "nickname": "SLEEPER"},
                    {"ref": "b_count", "slider": {"min": 1, "max": 10, "value": 3, "decimals": 0}, "nickname": "B COUNT"}])
    expect(r["ok"], "setup failed", r)
    awake = "#! python 3\na = 1"
    res = await gh.call("gh_script", action="configure", id="SLEEPER", inputs="[]",
                        outputs=json.dumps([{"name": "a"}]), code=awake, solve=True)
    expect(res["success"], "configuring the sleeper failed", res)

    def solving(probe):
        return bool((probe.get("grasshopper") or {}).get("solving"))

    async def hold():
        """Start one call that solves the sleeper for 25 s, and return it (still running) once
        Grasshopper is solving: the other calls arrive during that solve, as in the session where
        a checker script held Grasshopper."""
        task = asyncio.create_task(gh.call("gh_script", action="set", id="SLEEPER", solve=True,
                                           code="#! python 3\nimport time\ntime.sleep(25)\na = 2"))
        for _ in range(50):
            if solving(await gh.call("gh_inspect", action="connection")):
                return task
            if task.done():
                break
            await asyncio.sleep(0.1)
        result = await task
        if not result.get("success"):
            raise CheckFailed(f"setting the sleeper failed: {result}")
        raise CheckFailed("the sleeper never started solving")

    async def release(task):
        if task is not None:
            await task
        await gh.call("gh_script", action="set", id="SLEEPER", code=awake, solve=True)

    task = None
    try:
        task = await hold()
        started = time.monotonic()
        info = await gh.call("gh_document", action="info")
        waited = time.monotonic() - started
        expect(info["_isError"] and "has been solving" in info.get("error", "") and "stopped before reaching it" in info["error"],
               "a call during a long solve should say Grasshopper is solving", info)
        expect(12 <= waited < 19, f"the busy answer should come after about 15 s, came after {waited:.1f} s")
        probe = await gh.call("gh_inspect", action="connection")
        expect(probe["success"] and solving(probe), "the connection probe answers at once during the solve", probe)
        held = await task
        task = None
        expect(held["success"] and held.get("solved") is True, "the slow call itself finishes normally", held)
        await release(None)
        expect((await gh.call("gh_document", action="info"))["success"], "after the solve, calls work again")

        task = await hold()
        r = await gh.batch(set=[{"target": "B COUNT", "value": 7}])
        expect(not r["ok"] and not r["changed"] and "has been solving" in r["errors"][0] and "Nothing was changed" in r["errors"][0],
               "gh_batch during a long solve should refuse and change nothing", r)
        await task
        task = None
        expect(float(await gh.value("B COUNT")) == 3, "the refused batch changed nothing")

        # A sweep whose solve is slow answers after one solve; Grasshopper puts the slider back after it.
        # The script sleeps only once the slider is wired, so every slow solve runs inside a call
        # (a solve Grasshopper starts by itself while no call is running reads as a dialog: see below).
        res = await gh.call("gh_script", action="configure", id="SLEEPER",
                            inputs=json.dumps([{"name": "n", "type": "int", "access": "item"}]),
                            code="#! python 3\nimport time\nif n:\n    time.sleep(8)\na = n")
        expect(res.get("success"), "giving the sleeper a slider input failed", res)
        wired = await gh.batch(wires=[["B COUNT", "SLEEPER.n"]])
        expect(wired.get("ok"), "wiring the slider to the sleeper failed", wired)
        started = time.monotonic()
        sw = await gh.call("gh_document", action="sweep", id="B COUNT", values="[5]", watch='["SLEEPER.a"]')
        took = time.monotonic() - started
        expect(sw.get("success") and "re-solves it after this answer" in sw.get("restored", ""),
               "after a slow solve the sweep should say Grasshopper re-solves the restored value after the answer", sw)
        expect(took < 13, f"the sweep should answer after one 8 s solve, not two (it took {took:.1f} s)")
        for _ in range(150):
            if not solving(await gh.call("gh_inspect", action="connection")):
                break
            await asyncio.sleep(0.2)
        expect(float(await gh.value("B COUNT")) == 3, "the slider is back at 3")
        out = await gh.call("gh_inspect", action="outputs", id="SLEEPER")
        a = [o for o in out.get("outputs", []) if o["name"] == "a"]
        expect(a and a[0]["preview"] == ["3"], "once Grasshopper has re-solved, the script has the restored value again", out)

        async def wait_free():
            for _ in range(150):
                if not solving(await gh.call("gh_inspect", action="connection")):
                    return
                await asyncio.sleep(0.2)

        # Review finding 2: a sweep sent while the restored value re-solves waits for that solve, then runs.
        first = await gh.call("gh_document", action="sweep", id="B COUNT", values="[5]", watch='["SLEEPER.a"]')
        expect(first.get("success") and "re-solves it after this answer" in first.get("restored", ""),
               "setup: a slow sweep that restores after its answer", first)
        started = time.monotonic()
        second = await gh.call("gh_document", action="sweep", id="B COUNT", values="[6]", watch='["SLEEPER.a"]')
        took = time.monotonic() - started
        expect(second.get("success") and (second.get("results") or [""])[0].startswith("6 →"),
               "a sweep sent while the restored value re-solves should wait for that solve and then run", second)
        expect(took < 25, f"the second sweep should wait about one solve, then run one (it took {took:.1f} s)")
        await wait_free()

        # Review finding 1: the end of a solve over 5 s reads as a dialog for a moment (the solve is
        # tracked; the next heartbeat hasn't landed). A gh_batch waiting through it must not report a
        # dialog. Configure without solve: Hyperceps's status refresh after the call solves the script.
        for attempt in range(3):
            res = await gh.call("gh_script", action="configure", id="SLEEPER",
                                code=f"#! python 3\nimport time\nif n:\n    time.sleep(8)\na = n + {attempt}")
            expect(res.get("success"), "giving the sleeper new code failed", res)
            r = await gh.batch(components=[{"ref": f"b_dlg{attempt}", "param": "Number"}])
            expect(r.get("ok"), "a gh_batch sent while an 8 s solve runs should wait through its end, not report a dialog", r)
            await wait_free()

        # Review finding 3: fast values but a slow original value. Putting the control back would take
        # 8 s inside the call; the sweep reads the original value's solve time first and restores after.
        res = await gh.call("gh_script", action="configure", id="SLEEPER",
                            code="#! python 3\nimport time\nif n == 3:\n    time.sleep(8)\na = n")
        expect(res.get("success"), "giving the sleeper new code failed", res)
        await asyncio.sleep(0.5)
        await wait_free()   # Hyperceps's refresh solves it at 3: 8 s, which Grasshopper records
        started = time.monotonic()
        fast = await gh.call("gh_document", action="sweep", id="B COUNT", values="[5, 6]", watch='["SLEEPER.a"]')
        took = time.monotonic() - started
        expect(fast.get("success") and "re-solves it after this answer" in fast.get("restored", ""),
               "with a slow original value, the sweep should restore after its answer", fast)
        expect(took < 5, f"two fast values should answer quickly, without the 8 s restore (it took {took:.1f} s)")
        await asyncio.sleep(0.5)
        await wait_free()
        expect(float(await gh.value("B COUNT")) == 3, "the slider is back at 3 after the fast sweep")
    finally:
        await release(task)


@check("fixes", "Saving without a path uses the definition's own file; images save to paths over 260 characters; a built-in type wins over plugin namesakes; the time-limit error explains itself")
async def fixes(gh):
    await gh.clear()
    saved = await gh.call("gh_document", action="save")
    expect(saved["success"] and os.path.normcase(saved["path"]) == os.path.normcase(str(gh.scratch)),
           "save without a path should save to the scratch file itself", saved)

    import tempfile, shutil
    root = Path(tempfile.gettempdir()) / "hyperceps-longpath-check"
    target = root / ("x" * 120) / ("y" * 120) / "view.png"
    expect(len(str(target)) > 260, "the test path should be over 260 characters")
    try:
        shot = await gh.call("gh_document", action="capture_viewport", path=str(target), width=200, height=150)
        expect(shot["success"] and target.exists(), "a capture to a long path should be written", shot)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    r = await gh.batch(components=[{"ref": "f_offset", "type": "Offset Curve"}])
    expect(r["ok"], "'Offset Curve' should resolve to Grasshopper's (with a warning if a plugin has one too)", r)
    for w in (w for w in r["warnings"] if "Offset Curve" in w):
        expect("Grasshopper's Curve/Util/Offset Curve" in w and "has one too" in w, "the warning names the plugin's type", r)

    bad = await gh.batch(components=[{"ref": "f_x", "param": "Number"}], options={"timeoutSeconds": 500})
    expect(not bad["ok"] and any("the solve isn't counted" in e for e in bad["errors"]), "the time-limit error says the solve isn't counted", bad)


@check("guidance", "The agent guidance reaches the AI app: descriptions, connection note, nudge after repeated single-step edits, hints")
async def guidance(gh):
    await gh.clear()
    expect("WORKING METHOD" in (gh.instructions or "") and "gh_batch" in gh.instructions, "connection note should carry the working method")
    first = (gh.instructions or "").splitlines()[0]
    expect(first.startswith("Hyperceps ") and ": MCP control" in first and "+build" not in first,
           "the connection note should open with 'Hyperceps <version>:'", first)
    expect("GUIDE FIRST" in gh.instructions and '"hyperceps" skill' in gh.instructions and "gh_inspect(action='skill')" in gh.instructions,
           "the connection note should ask for the guide: the installed skill, or gh_inspect(action='skill')")
    sent = gh.instructions.replace("\r\n", "\n").replace("\n", "\r\n")
    expect(len(sent) <= 3900, f"the connection note should fit what Claude Code shows (4,096 characters); it is {len(sent)}")

    # The plugin serves its own copy of the skill: the repository's SKILL.md without its front matter.
    skill_file = (Path(__file__).resolve().parent.parent / "skills" / "hyperceps" / "SKILL.md").read_text(encoding="utf-8")
    body = skill_file.replace("\r\n", "\n").split("\n---\n", 1)[1].lstrip("\n")
    served = await gh.session.call_tool("gh_inspect", {"action": "skill"})
    text = served.content[0].text.replace("\r\n", "\n")
    expect(not served.is_error and text.startswith("The Hyperceps guide") and text.rstrip().endswith(body.rstrip()),
           "gh_inspect(action='skill') should return the repository's SKILL.md (rebuild and reinstall if it changed)", text[:300])
    version = first.split(" ")[1].rstrip(":")
    expect(f"This skill is for Hyperceps {version}." in text, f"the served skill should name the plugin's version {version}", text[:300])
    resource = await gh.session.read_resource("gh://skill")
    expect(body.rstrip() in resource.contents[0].text.replace("\r\n", "\n"), "the resource gh://skill should serve the same skill")
    tools = {t.name: t for t in (await gh.session.list_tools()).tools}
    expect("two or more changes" in tools["gh_batch"].description, "gh_batch description should state when to use it")
    for name in ("gh_canvas", "gh_wire"):
        expect("gh_batch" in tools[name].description, f"{name} description should point to gh_batch")

    # Three single-step edits in a row: the third result carries the nudge; gh_batch resets the streak.
    await gh.batch(components=[{"ref": "reset_streak", "panel": "x"}])
    results = [await gh.call("gh_canvas", action="add", type="Panel", x=0, y=100 * i) for i in range(3)]
    expect("nudge" not in results[0] and "nudge" not in results[1], "no nudge before the third edit", results[:2])
    expect("gh_batch" in results[2].get("nudge", ""), "the third single-step edit should carry the nudge", results[2])

    # A comma list split by a Text Split: the hint says a one-per-line panel replaces it.
    r = await gh.batch(components=[{"ref": "csv", "panel": "0,2,4"}, {"ref": "ts", "type": "Text Split"}, {"ref": "sep", "panel": ","}],
                       wires=[["csv", "ts.0"], ["sep", "ts.1"]])
    expect(r["ok"] and any("Text Split isn't needed" in h for h in r["hints"]), "the Text Split hint should appear", r)
    r = await gh.batch(components=[{"ref": "g", "type": "Graft Tree"}, {"ref": "li", "type": "List Item"}], wires=[["g.0", "li.i"]])
    expect(r["ok"] and any("port setting" in h for h in r["hints"]), "the Graft Tree hint should appear", r)


@check("existing", "Every existing tool still works (smoke test)")
async def existing(gh):
    await gh.clear()
    added = await gh.call("gh_canvas", action="add", type="Panel", x=100, y=100)
    expect(added["success"], "gh_canvas add", added)
    # (The original gh_canvas add calls "Merge" ambiguous because of its deprecated twin; that is
    # unchanged Cordyceps behaviour, so the smoke test uses an unambiguous name.)
    second = await gh.call("gh_canvas", action="add", type="Text Split", x=300, y=100)
    expect(second["success"], "gh_canvas add Text Split", second)
    wired = await gh.call("gh_wire", action="connect", sourceId=added["id"], targetId=second["id"], targetParam="0")
    expect(wired["success"], "gh_wire connect", wired)
    for tool, args in [
        ("gh_canvas", {"action": "list"}), ("gh_canvas", {"action": "info", "id": second["id"]}),
        ("gh_wire", {"action": "list"}), ("gh_document", {"action": "info"}),
        ("gh_inspect", {"action": "status"}), ("gh_inspect", {"action": "connection"}),
        ("gh_script", {"action": "help"}), ("rhino_scene", {"action": "layers"}),
        ("rhino_render", {"action": "modes"}),
    ]:
        res = await gh.call(tool, **args)
        expect(res.get("success", True) and not res["_isError"], f"{tool} {args['action']} failed", res)
    removed = await gh.call("gh_canvas", action="delete", ids=json.dumps([added["id"], second["id"]]))
    expect(removed["success"], "gh_canvas delete", removed)

    try:
        with urllib.request.urlopen("http://127.0.0.1:26929/health", timeout=2) as resp:
            server = json.loads(resp.read().decode()).get("server")
        print(f"      (original Cordyceps is also answering on 26929: {server})", flush=True)
    except Exception:
        print("      (original Cordyceps isn't running on 26929; that's fine - its load check was done in step 2)", flush=True)


# ---- runner ---------------------------------------------------------------------------------------

async def run(args):
    scratch = Path(args.scratch).resolve()
    selected = [c for c in CHECKS if not args.only or c[0] in args.only]
    results = []

    try:
        async with streamable_http_client(args.url) as streams:
            async with ClientSession(streams[0], streams[1]) as session:
                init = await session.initialize()
                gh = Gh(session, scratch, init.instructions)
                print(f"Connected to {init.server_info.name} {init.server_info.version}")
                try:
                    info = await gh.guard()
                except NotScratch as e:
                    print(f"REFUSING: {e}")
                    return 2
                print(f"Scratch file: {info['filePath']}\n")

                clearing = [c[0] for c in selected if c[3]]
                if clearing and await gh.has_thalli():
                    print(f"REFUSING: the open file holds Lichen Thalli, and these checks clear the canvas: {', '.join(clearing)}.")
                    print("Run only the Thallus check on it: --only thallus")
                    return 2

                for name, spec, fn, _ in selected:
                    print(f"[{name}] {spec}", flush=True)
                    try:
                        await gh.guard()
                        if name == "undo":
                            await fn(gh, interactive=not args.skip_undo)
                        else:
                            await fn(gh)
                        print("   PASS", flush=True)
                        results.append((name, spec, "PASS", ""))
                    except NotScratch as e:
                        print(f"   STOPPED: {e}")
                        results.append((name, spec, "STOPPED", str(e)))
                        break
                    except CheckFailed as e:
                        status = "SKIP" if str(e).startswith("SKIPPED") else "FAIL"
                        print(f"   {status}: {e}", flush=True)
                        results.append((name, spec, status, str(e)))
                if clearing:
                    await gh.clear()
    except NotScratch as e:
        print(f"REFUSING: {e}")
        return 2
    except Exception as e:  # connection problems: say what to check
        print(f"Could not talk to Hyperceps at {args.url}: {e!r}")
        print("Is Rhino open on the scratch file, with the Hyperceps component on the canvas?")
        return 2

    print("\nSummary (spec functional checks)")
    for name, spec, status, _ in results:
        print(f"  {status:7} {name:12} {spec}")
    failed = [r for r in results if r[2] in ("FAIL", "STOPPED")]
    print(f"\n{sum(r[2] == 'PASS' for r in results)} passed, {len(failed)} failed, {sum(r[2] == 'SKIP' for r in results)} skipped")
    return 1 if failed else 0


def main():
    parser = argparse.ArgumentParser(description="Hyperceps functional tests (scratch file only)")
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--scratch", default=str(DEFAULT_SCRATCH))
    parser.add_argument("--only", type=lambda s: [x.strip() for x in s.split(",") if x.strip()], default=None)
    parser.add_argument("--skip-undo", action="store_true")
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.list:
        for name, spec, _, _ in CHECKS:
            print(f"{name:12} {spec}")
        return 0
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
