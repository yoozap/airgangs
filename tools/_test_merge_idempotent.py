#!/usr/bin/env python3
"""Guard: merging twice must produce the same manifest as merging once.

This is not hypothetical. `"restates": true` originally entered the sticky set
only when it actually LOWERED a claim that run. Because the merge re-seeds from
the manifest it wrote last time, the second run saw equality, never marked the
key, and a stale higher claim raised it straight back — so
CombatWorldAdapter.cs read 60 after one merge and 700 after the next, and the
headline silently depended on how many times anyone had run the script.

Run this after ANY change to merge_port_units.py:

    python3 tools/_test_merge_idempotent.py
"""
import hashlib, json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAN = os.path.join(ROOT, "docs", "port_manifest.json")


def sig():
    subprocess.run([sys.executable, os.path.join(ROOT, "tools", "merge_port_units.py")],
                   capture_output=True, check=True)
    m = json.load(open(MAN))
    rows = sorted((u.get("cs", ""), u.get("js", ""), u.get("covers", 0)) for u in m["units"])
    return (hashlib.sha256(json.dumps(rows).encode()).hexdigest()[:12],
            sum(u.get("covers", 0) for u in m["units"]), len(m["units"]))


a, b, c = sig(), sig(), sig()
for i, s in enumerate((a, b, c), 1):
    print(f"  run {i}: hash={s[0]} covers={s[1]} units={s[2]}")
if a == b == c:
    print("PASS - merging is idempotent")
    sys.exit(0)
print("FAIL - the manifest changes between identical runs; the headline depends on run count")
sys.exit(1)
