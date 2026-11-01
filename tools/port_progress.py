#!/usr/bin/env python3
"""Live Unity port progress, measured from the repo.

Percentages are JS lines REPLACED, not C# lines written — C# is more verbose, so
counting output would flatter the number. Coverage is declared per unit in
docs/port_manifest.json because big modules (citymap.js, 8552 lines) get ported
in slices; claiming a whole file for a partial port would be a lie.

  tools/port_progress.py          print a table
  tools/port_progress.py --json   emit JSON (for the dashboard)
"""
import json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
man = json.load(open(os.path.join(ROOT, "docs", "port_manifest.json")))

def js_lines(mod):
    p = os.path.join(ROOT, "src", mod + ".js")
    if not os.path.exists(p): return 0
    return sum(1 for _ in open(p, errors="replace"))

# Modules the user has DEFERRED out of scope (manifest "deferred_modules").
# Dropped from the budget entirely — both covered and remaining — so the
# percentage neither flatters itself with their finished lines nor charges
# hours for work nobody is doing. Distinct from "excluded": see the manifest's
# own _deferred_comment for why the two must not be conflated.
DEFERRED = set(man.get("deferred_modules", []))

totals = {s: sum(js_lines(m) for m in mods if m + ".js" not in DEFERRED)
          for s, mods in man["systems"].items()}
mod_to_system = {m: s for s, mods in man["systems"].items() for m in mods}

covered = {s: 0 for s in man["systems"]}
verified = {s: 0 for s in man["systems"]}
# Lines that must NEVER be written, so they do not belong in the denominator:
# dead code (a function with zero call sites anywhere in src/*.js) and builders
# whose geometry is delivered by a real exported asset instead. Keeping them in
# understates progress AND keeps sending agents to port code nothing calls;
# faking them as `covers` would instead claim work that was never done. They are
# subtracted from the budget and printed as their own auditable column.
#
# An entry needs {"js", "excluded", "reason"}; `reason` must be checkable, e.g.
# "0 call sites (grep src/*.js)" or "delivered by Art/Landmarks/tvTower.obj".
excluded = {s: 0 for s in man["systems"]}
for x in man.get("excluded", []):
    mod = (x.get("js") or "")[:-3]
    s = mod_to_system.get(mod)
    if s and x.get("reason"):
        excluded[s] += x.get("excluded", 0)
# Two silent drops used to happen right here, and both made the headline LIE
# without leaving a trace: a unit with no `js` was skipped, and a unit whose
# module maps to no system was skipped. Together that hid 1,507 lines of real,
# compiling, probe-passing C# — CityMaterials.cs (322), BuildPlayableScene.cs
# (255), BuildCityRender.cs (158), NetClient.cs (90) and 15 others. A port audit
# had to find it by reading the manifest by hand. Count them loudly instead.
# COUNTED BUT NOT BUILT. A line range can sit inside a claim (so it reads as
# ported) while the thing that would draw it is a literal no-op. citymap.js
# reported 0 lines left while the city rendered with no trees, rocks, groves,
# streetlights or road signs, because the per-module clamp pinned it at exactly
# its budget. Subtract these from `covered` so BOTH the percentage and the hour
# estimate see them. See docs/port_manifest.json "_not_built_comment".
not_built = {s: 0 for s in man["systems"]}
for x in man.get("not_built", []):
    mod = (x.get("js") or "")[:-3]
    sy = mod_to_system.get(mod)
    if sy:
        not_built[sy] += x.get("not_built", 0)

unattributed = []
unmapped = []
for u in man["units"]:
    if u.get("js") in DEFERRED:
        continue
    if not u["js"]:
        unattributed.append(u)
        continue
    mod = u["js"][:-3]
    s = mod_to_system.get(mod)
    if not s:
        unmapped.append(u)
        continue
    covered[s] += u["covers"]
    if u.get("verified"): verified[s] += u["covers"]

for s in man["systems"]:
    covered[s] = max(0, covered[s] - not_built[s])
    verified[s] = min(verified[s], covered[s])

est = man.get("estimate", {})
diff = est.get("difficulty", {})
rate_lo = est.get("rate_low", 150)
rate_hi = est.get("rate_high", 400)

# The budget every percentage is taken against: real file size minus excluded.
budget = {s: totals[s] - excluded[s] for s in man["systems"]}
grand_excl = sum(excluded.values())
grand = sum(budget.values())
grand_cov = sum(covered.values())
grand_ver = sum(verified.values())

cs_files, cs_lines = 0, 0
for base in ("Assets/Scripts", "Assets/Editor"):
    d = os.path.join(ROOT, "unity", base)
    for dirpath, _, names in os.walk(d):
        for nm in names:
            if nm.endswith(".cs"):
                cs_files += 1
                cs_lines += sum(1 for _ in open(os.path.join(dirpath, nm), errors="replace"))

def eff(s):
    return (budget[s] - covered[s]) * diff.get(s, {}).get("x", 1.5)


def _parallel(eff, man, est, rate_lo, rate_hi):
    """Wall-clock, not serial hours.

    Claude holds the critical path (city + character: handedness risk, perf work,
    per-frame bone ordering — they need one consistent head). Independent systems
    go to N subagents. So the calendar is set by whichever side takes longer, plus
    a review charge on the critical path for verifying agent output, since the
    Unity project lock is exclusive and only one process can run probes.
    """
    par = est.get("parallel")
    if not par:
        return {}
    # Module-level critical path: the handful of files that need one head.
    crit_mods = par.get("critical_modules", [])
    mod_sys = {m: s for s, mods in man["systems"].items() for m in mods}
    covered_by_mod = {}
    for u in man.get("units", []):
        if u.get("js"):
            k = u["js"][:-3]
            covered_by_mod[k] = covered_by_mod.get(k, 0) + u.get("covers", 0)

    def mod_eff(m):
        n = js_lines(m)
        x = diff.get(mod_sys.get(m, ""), {}).get("x", 1.5)
        return max(0, n - covered_by_mod.get(m, 0)) * x

    crit = sum(mod_eff(m) for m in crit_mods)
    total = sum(eff(s) for s in man["systems"])
    pool = max(0.0, total - crit)
    n = max(1, par.get("agents", 1))
    # A dedicated verifier agent owns the Unity lock, so review no longer lands
    # on the critical path.
    review = 0.0 if par.get("review_owner") else 0.15 * pool
    out = {}
    for tag, rate in (("low", rate_hi), ("high", rate_lo)):
        mine = (crit + review) / rate
        theirs = (pool / n) / rate
        out[f"wall_{tag}"] = round(max(mine, theirs), 1)
        out[f"crit_{tag}"] = round(mine, 1)
        out[f"pool_each_{tag}"] = round(theirs, 1)
    out["agents"] = n
    # Name the critical path here, so the terminal AND the dashboard read the
    # same label from one computation. The page used to hardcode the sentence
    # "citymap.js and foot.js ... are held back" and kept printing it long
    # after citymap.js reached 0 lines left -- it showed a two-file problem
    # that was one file, and a stale agent count with it.
    excl_by_mod = {}
    for e in man.get("excluded", []):
        k = (e.get("js") or "")[:-3]
        excl_by_mod[k] = excl_by_mod.get(k, 0) + e.get("excluded", 0)
    hot = [m + ".js" for m in crit_mods
           if max(0, js_lines(m) - excl_by_mod.get(m, 0) - covered_by_mod.get(m, 0)) > 0]
    out["crit_label"] = " + ".join(hot) if hot else "nothing left"
    out["crit_count"] = len(hot)
    # The AUDITED count of genuinely unported lines, which is far below the raw
    # `remaining` budget: two independent cluster-by-cluster walks found foot.js
    # and characters.js had ZERO real lines left, and the raw figure charges
    # hours for that. Carried through so the dashboard can show the real work
    # beside the upper bound instead of only the upper bound.
    if par.get("audited_real_remaining") is not None:
        real = par["audited_real_remaining"]
        out["audited_real_remaining"] = real
        for tag, rate in (("low", rate_hi), ("high", rate_lo)):
            out[f"real_hours_{tag}"] = round((real * 1.5) / rate / max(1, n), 1)
    return out

out = {
    "systems": [
        {"name": s, "js_total": budget[s], "js_file_lines": totals[s],
         "excluded": excluded[s], "covered": covered[s],
         "verified": verified[s],
         "remaining": budget[s] - covered[s],
         "difficulty": diff.get(s, {}).get("x", 1.5),
         "effort": round(eff(s)),
         "hours_low": round(eff(s) / rate_hi, 1),
         "hours_high": round(eff(s) / rate_lo, 1),
         "pct": round(100.0 * covered[s] / budget[s], 2) if budget[s] else 0.0}
        for s in man["systems"]
    ],
    "overall": {
        "js_total": grand, "js_file_lines": sum(totals.values()),
        "excluded": grand_excl, "covered": grand_cov, "verified": grand_ver,
        "pct": round(100.0 * grand_cov / grand, 2),
        "pct_verified": round(100.0 * grand_ver / grand, 2),
        "cs_files": cs_files, "cs_lines": cs_lines,
        "effort": round(sum(eff(s) for s in man["systems"])),
        **_parallel(eff, man, est, rate_lo, rate_hi),
        "hours_low": round(sum(eff(s) for s in man["systems"]) / rate_hi, 1),
        "hours_high": round(sum(eff(s) for s in man["systems"]) / rate_lo, 1),
        "rate_low": rate_lo, "rate_high": rate_hi,
        "paces": est.get("paces", [10, 20, 40]),
    },
    "units": man["units"],
}

if "--json" in sys.argv:
    print(json.dumps(out, indent=2))
    sys.exit(0)

print(f"{'system':<11}{'js':>7}{'done':>7}{'left':>7}{'x':>5}{'%':>7}{'hours':>13}")
print("-" * 57)
for s in out["systems"]:
    print(f"{s['name']:<11}{s['js_total']:>7}{s['covered']:>7}{s['remaining']:>7}"
          f"{s['difficulty']:>5.1f}{s['pct']:>6.2f}%{s['hours_low']:>6.0f}-{s['hours_high']:<6.0f}")
print("-" * 57)
o = out["overall"]
print(f"{'TOTAL':<11}{o['js_total']:>7}{o['covered']:>7}"
      f"{o['js_total']-o['covered']:>7}{'':>5}{o['pct']:>6.2f}%"
      f"{o['hours_low']:>6.0f}-{o['hours_high']:<6.0f}")
print()
print(f"C# written: {o['cs_lines']} lines across {o['cs_files']} files")
print(f"Verified by probe: {o['pct_verified']:.2f}% of the JS line budget")
extra = man.get("extra_work", [])
if extra:
    el = sum(x.get("hours_low", 0) for x in extra)
    eh = sum(x.get("hours_high", 0) for x in extra)
    print(f"PLUS {len(extra)} item(s) the LINE MODEL CANNOT SEE: {el:.0f}-{eh:.0f} h")
    for x in extra:
        print(f"    {x['hours_low']:.0f}-{x['hours_high']:.0f} h  {x['what']}")
    print("  No JS lines, so these never appear in the percentage — but they are required.")
if man.get("not_built"):
    nb = sum(x.get("not_built", 0) for x in man["not_built"])
    print(f"COUNTED BUT NOT BUILT: {nb} lines of citymap.js are inside a claim yet have a no-op")
    print("  builder (trees/rocks/groves/streetlights/road signs). Already SUBTRACTED above.")

if unattributed or unmapped:
    ua = sum(u.get("covers", 0) for u in unattributed)
    um = sum(u.get("covers", 0) for u in unmapped)
    print(f"NOT COUNTED ANYWHERE: {len(unattributed)} unit(s) with no `js` module "
          f"({ua} lines) and {len(unmapped)} whose module maps to no system ({um} lines).")
    print("  This is real ported work the percentage above does NOT credit. Give each a")
    print("  `js` field (or map its module to a system) to fold it in. Largest first:")
    for u in sorted(unattributed + unmapped, key=lambda x: -x.get("covers", 0))[:8]:
        print(f"    {u.get('covers', 0):>5}  {(u.get('js') or '(no module)'):14} {u['cs'][:58]}")
if o.get("excluded"):
    print(f"Excluded from the budget: {o['excluded']} lines of "
          f"{o['js_file_lines']} (dead code / delivered by an exported asset; "
          f"see docs/port_manifest.json \"excluded\" for the reason on each)")
print(f"Effort left: {o['effort']:,} difficulty-weighted lines "
      f"at {o['rate_low']}-{o['rate_high']} lines/hour")
print()
print(f"Serial effort:  {o['hours_low']:.0f}-{o['hours_high']:.0f} h")
if "wall_low" in o:
    print(f"With {o['agents']} agents in parallel:")
    # Name the files that ACTUALLY pin this, computed, not hardcoded. The label
    # said "citymap.js + foot.js" long after citymap.js reached 0 lines left,
    # which made the headline look like a two-file problem when it was one.
    _cm = [m for m in man.get("estimate", {}).get("parallel", {}).get("critical_modules", [])]
    _hot = []
    for _m in _cm:
        _n = js_lines(_m)
        _c = sum(u.get("covers", 0) for u in man["units"] if (u.get("js") or "") == _m + ".js")
        _e = sum(x.get("excluded", 0) for x in man.get("excluded", []) if x.get("js") == _m + ".js")
        if max(0, _n - _e - _c) > 0:
            _hot.append(f"{_m}.js")
    _label = " + ".join(_hot) if _hot else "nothing left"
    print(f"  critical path ({_label}){' ' * max(1, 22 - len(_label))}{o['crit_low']:.0f}-{o['crit_high']:.0f} h")
    # Say which side ACTUALLY binds, computed. This used to assert the critical
    # path was the wall-clock and hardcode "the other six are finishing sooner",
    # which went stale twice: the agent count changed, and the pool eventually
    # OVERTOOK the critical path, so the note was claiming the opposite of the
    # arithmetic right above it.
    _crit_binds = o["crit_high"] >= o["pool_each_high"]
    if _crit_binds and len(_hot) == 1:
        print(f"  ^ this BINDS the wall-clock: {_hot[0]} is one file and cannot be split,")
        print(f"    so adding agents cannot move it. The pool finishes sooner.")
    elif not _crit_binds:
        print(f"  ^ the POOL now binds, not the critical path ({o['agents']} agents at")
        print(f"    {o['pool_each_low']:.1f}-{o['pool_each_high']:.1f} h vs {o['crit_low']:.1f}-{o['crit_high']:.1f} h). More agents WOULD help here —")
        print(f"    until the pool drops back under the critical path again.")
    print(f"  each agent's share                     {o['pool_each_low']:.0f}-{o['pool_each_high']:.0f} h")
    _el = sum(x.get("hours_low", 0) for x in man.get("extra_work", []))
    _eh = sum(x.get("hours_high", 0) for x in man.get("extra_work", []))
    print(f"  WALL-CLOCK (lines only)                {o['wall_low']:.0f}-{o['wall_high']:.0f} h")
    if _eh:
        # The asset/wiring items run alongside the porting, so they join the
        # pool rather than the critical path — but they are NOT free, and the
        # line model is structurally blind to them (no JS lines to count).
        print(f"  + asset/wiring work (parallel)         {_el:.0f}-{_eh:.0f} h")
        print(f"  REAL WALL-CLOCK                        "
              f"{max(o['wall_low'], _el):.0f}-{max(o['wall_high'], _eh):.0f} h")
print()
print("Calendar on WALL-CLOCK hours, at each pace:")
key_lo = o.get("wall_low", o["hours_low"]); key_hi = o.get("wall_high", o["hours_high"])
for pace in o["paces"]:
    lo, hi = key_lo / pace, key_hi / pace
    print(f"  {pace:>2} h/week   {lo:5.1f} - {hi:5.1f} weeks   "
          f"({lo/4.345:4.1f} - {hi/4.345:4.1f} months)")
