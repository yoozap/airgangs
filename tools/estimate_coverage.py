#!/usr/bin/env python3
"""Estimate port coverage STRUCTURALLY, for agents that died before declaring it.

Eleven agents stalled mid-run (a dropped connection) without filing their
docs/port_units/*.json claims, so ~11,700 lines of C# exist that the manifest
knows nothing about. Guessing a number from file size would flatter it; instead
this measures something real: how many of the JS file's own functions have a
counterpart in the C# that cites it.

  coverage = js_lines * (matched functions / total functions)

A function counts as matched when a method or property of the same name (ignoring
case and the leading underscore JS uses for private) appears in a C# file that
cites that JS source. Imperfect — a renamed method reads as missing, and a stub
with the right name reads as present — so it is an ESTIMATE, written as such.
What a probe PROVES is tracked separately, in `verified`.
"""
import json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def js_functions(path):
    src = open(path, errors="replace").read()
    names = set()
    # class methods:  foo(a, b) {      /  async foo(...) {
    names |= set(re.findall(r"^\s{2}(?:async\s+)?([A-Za-z_][\w]*)\s*\([^)]*\)\s*\{", src, re.M))
    # top-level and exported functions
    names |= set(re.findall(r"^(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_][\w]*)", src, re.M))
    # const foo = (...) =>
    names |= set(re.findall(r"^(?:export\s+)?const\s+([A-Za-z_][\w]*)\s*=\s*(?:async\s*)?\(", src, re.M))
    names -= {"constructor", "if", "for", "while", "switch", "catch", "function", "return"}
    return {n for n in names if len(n) > 2}

def norm(n):
    return n.lstrip("_").lower()

cs_files = []
for dirpath, _, names in os.walk(os.path.join(ROOT, "unity", "Assets", "Scripts")):
    for nm in names:
        if nm.endswith(".cs"):
            p = os.path.join(dirpath, nm)
            cs_files.append((p, open(p, errors="replace").read()))

# Assign each C# file to exactly ONE primary JS module: the one it cites MOST.
#
# A first version credited every module a file merely MENTIONED, which double
# counted badly — Blast.cs mentions fire.js in a comment and fire.js was credited
# 267 lines it had nothing to do with; ImpactFx.cs did the same for glass,
# papertargets and rangelane. One file, one owner.
cites = {}
for p, text in cs_files:
    hits = re.findall(r"src/([a-z0-9_]+)\.js", text)
    if not hits:
        continue
    tally = {}
    for h in hits:
        tally[h] = tally.get(h, 0) + 1
    primary = max(tally, key=lambda k: (tally[k], -len(k)))
    cites.setdefault(primary, []).append((p, text))

rows = []
for mod, files in sorted(cites.items()):
    jsp = os.path.join(ROOT, "src", mod + ".js")
    if not os.path.exists(jsp):
        continue
    js_lines = sum(1 for _ in open(jsp, errors="replace"))
    fns = js_functions(jsp)
    if not fns:
        continue
    blob = "\n".join(t for _, t in files)
    cs_names = set(re.findall(r"\b(?:void|float|int|bool|string|Vector3|Quaternion|Color32|double|Mesh|var|public|private|static)\s+([A-Za-z_][\w]*)\s*\(", blob))
    cs_names |= set(re.findall(r"\b([A-Za-z_][\w]*)\s*\(", blob))
    cs_norm = {norm(n) for n in cs_names}
    matched = {f for f in fns if norm(f) in cs_norm}
    frac = len(matched) / len(fns)
    covers = int(round(js_lines * frac))
    cs_total = sum(sum(1 for _ in open(p, errors="replace")) for p, _ in files)
    covers = min(covers, js_lines, cs_total)  # never claim more than exists
    rows.append({
        "mod": mod, "js_lines": js_lines, "fns": len(fns), "matched": len(matched),
        "frac": round(frac, 3), "covers": covers, "cs_total": cs_total,
        "cs": sorted(os.path.relpath(p, os.path.join(ROOT, "unity")) for p, _ in files),
    })

rows.sort(key=lambda r: -r["covers"])
print(f"{'module':<14}{'js':>6}{'fns':>5}{'hit':>5}{'frac':>7}{'covers':>8}{'cs lines':>10}")
print("-" * 55)
for r in rows:
    print(f"{r['mod']:<14}{r['js_lines']:>6}{r['fns']:>5}{r['matched']:>5}"
          f"{r['frac']:>7.2f}{r['covers']:>8}{r['cs_total']:>10}")
print("-" * 55)
print(f"{'TOTAL':<14}{'':>6}{'':>5}{'':>5}{'':>7}{sum(r['covers'] for r in rows):>8}"
      f"{sum(r['cs_total'] for r in rows):>10}")

if "--write" in sys.argv:
    out = []
    for r in rows:
        out.append({
            "cs": r["cs"][0] if len(r["cs"]) == 1 else ", ".join(r["cs"]),
            "js": r["mod"] + ".js",
            "covers": r["covers"],
            "note": (f"structural estimate: {r['matched']}/{r['fns']} of the JS file's "
                     f"functions have a counterpart; {r['cs_total']} lines of C# across "
                     f"{len(r['cs'])} file(s). Agent stalled before declaring coverage."),
            "verified": None,
        })
    path = os.path.join(ROOT, "docs", "port_units", "recovered.json")
    json.dump(out, open(path, "w"), indent=1)
    print(f"\nwrote {len(out)} claims to docs/port_units/recovered.json")
