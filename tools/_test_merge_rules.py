#!/usr/bin/env python3
"""Guard: the four claim-resolution rules in merge_port_units.py.

Written after breaking that decision block TWICE in one session — once by a
line-range edit that silently swallowed the keep-the-higher branch (which would
have re-opened a bug that once destroyed 2,532 lines of credited work), and once
by a search that matched the wrong occurrence of the same line. Both edits
reported success. A rule you cannot test is a rule that will rot.

Runs against a scratch copy of the repo's real docs/ so it never touches the
live manifest.

    python3 tools/_test_merge_rules.py
"""
import json, os, shutil, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = "Assets/Scripts/_RuleTest.cs"
JS = "parks.js"          # a real module, so norm_js resolves it


def merge_with(files, work):
    """Write each {name: claims} as its own unit file, merge, return covers.

    Several FILES, not one, because that is the real configuration and the
    difference is load-bearing: a restatement is re-marked on every run from the
    file it lives in, so the stale claim has to arrive as a SEPARATE file that
    sorts after it (gameadapter.json + playable.json). An earlier version of
    this test overwrote the single file each time, so nothing re-marked the key
    and it "failed" on correct code.
    """
    units = os.path.join(work, "docs", "port_units")
    for name, claims in files.items():
        json.dump(claims, open(os.path.join(units, name), "w"))
    # Run the COPY inside the sandbox: merge_port_units.py derives its own ROOT
    # from __file__, so cwd alone does not redirect it and the real manifest
    # would be the thing under test.
    subprocess.run([sys.executable, os.path.join(work, "tools", "merge_port_units.py")],
                   capture_output=True, cwd=work)
    man = json.load(open(os.path.join(work, "docs", "port_manifest.json")))
    for u in man["units"]:
        if u["cs"] == CS and u.get("js") == JS:
            return u.get("covers")
    return None


def case(name, files, want, work):
    got = merge_with(files, work)
    ok = got == want
    print(f"  {'PASS' if ok else 'FAIL'}  {name}: got {got}, want {want}")
    return ok


def main():
    with tempfile.TemporaryDirectory() as work:
        shutil.copytree(os.path.join(ROOT, "docs"), os.path.join(work, "docs"))
        shutil.copytree(os.path.join(ROOT, "src"), os.path.join(work, "src"),
                        ignore=shutil.ignore_patterns("*.map"))
        os.symlink(os.path.join(ROOT, "unity"), os.path.join(work, "unity"))
        os.makedirs(os.path.join(work, "tools"), exist_ok=True)
        shutil.copy(os.path.join(ROOT, "tools", "merge_port_units.py"),
                    os.path.join(work, "tools", "merge_port_units.py"))
        base = {"cs": CS, "js": JS, "note": "rule test", "verified": None}
        # Numbers stay small so the per-module clamp cannot interfere: the
        # budget is parks.js minus its exclusions minus what is already claimed,
        # and a big number here gets clamped, which an earlier version of this
        # test mistook for a broken rule (it read 71, the clamped remainder).
        A, B = "zz_a_ruletest.json", "zz_b_ruletest.json"   # A sorts before B
        ok = True
        # 4. an ordinary claim that RAISES is accepted
        ok &= case("ordinary raise accepted", {A: [dict(base, covers=9)]}, 9, work)
        # 3. an ordinary claim that LOWERS keeps the higher (delta-claim guard)
        ok &= case("ordinary lower refused", {A: [dict(base, covers=4)]}, 9, work)
        # 1. a restatement wins DOWNWARD
        ok &= case("restatement lowers", {A: [dict(base, covers=6, restates=True)]}, 6, work)
        # 2. a stale claim in a LATER-SORTING file cannot undo it, either way
        ok &= case("stale raise refused after restate",
                   {A: [dict(base, covers=6, restates=True)], B: [dict(base, covers=9)]}, 6, work)
        ok &= case("stale lower refused after restate",
                   {A: [dict(base, covers=6, restates=True)], B: [dict(base, covers=2)]}, 6, work)
        # 1b. a restatement also wins UPWARD (the mirrored failure)
        ok &= case("restatement raises",
                   {A: [dict(base, covers=6, restates=True)], B: [dict(base, covers=8, restates=True)]},
                   8, work)
        print("PASS - all four claim-resolution rules hold" if ok else
              "FAIL - a claim-resolution rule is broken")
        return 0 if ok else 1


sys.exit(main())
