#!/usr/bin/env python3
"""Regenerate the three JSON datasets behind the live port dashboard.

The dashboard reads attached files, not the repo, so after every merge the files
have to be rebuilt and re-uploaded. This existed only as ad-hoc shell before, so
the page silently showed stale numbers (it read 35.73% for city while the repo
said 59.99%). Everything here comes from tools/port_progress.py --json so there
is exactly ONE copy of the estimate model; an earlier second copy in the page
drifted to a different answer (44-100 h vs 31-70 h) and had to be torn out.

  tools/dashboard_datasets.py <out_dir>
"""
import json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = sys.argv[1] if len(sys.argv) > 1 else "."
prog = json.loads(subprocess.check_output(
    [sys.executable, os.path.join(ROOT, "tools", "port_progress.py"), "--json"], text=True))

# The per-system "why" prose is editorial, not measured, so it is keyed here and
# carried across refreshes rather than regenerated or lost.
WHY = {
    "city": "8552-line generator plus instanced-rendering perf work from scratch — the Three.js "
            "\"detach chunks from the scene graph\" win has no Unity analogue.",
    "character": "Bone-level pose work: every layer must write in LateUpdate after the Animator, and "
                 "never rebase off its own output.",
    "flight": "Hand-integrated aerodynamics. Ports almost line-for-line; the handedness mirror is the "
              "only real hazard.",
    "combat": "Ballistics, damage decals and tear geometry — lots of surface math, all probe-checkable.",
    "ui": "UI Toolkit with USS carrying the existing CSS theme. Georgian strings throughout.",
    "core": "The game loop, world hooks and startup sequence: the integration hub everything else "
            "plugs into.",
}

systems = [{
    "system": s["name"], "js_lines": s["js_total"], "ported": s["covered"],
    "verified": s["verified"], "remaining": s["remaining"], "difficulty": s["difficulty"],
    "effort": s["effort"], "hours_low": s["hours_low"], "hours_high": s["hours_high"],
    "pct": s["pct"], "why": WHY.get(s["name"], ""),
} for s in prog["systems"]]

o = prog["overall"]
_man = json.load(open(os.path.join(ROOT, "docs", "port_manifest.json")))
# Two categories the LINE MODEL IS BLIND TO, surfaced on the card so the page
# cannot show a comfortable number the terminal contradicts:
#   not_built  — lines inside a claim whose builder is a literal no-op
#                (citymap.js trees/rocks/groves/streetlights/road signs). Already
#                subtracted from `ported` by port_progress.py.
#   extra_work — required work with NO JS lines at all, so it can never appear
#                in a percentage (the Humanoid avatar import).
_nb = sum(x.get("not_built", 0) for x in _man.get("not_built", []))
_ew = _man.get("extra_work", [])
_el = sum(x.get("hours_low", 0) for x in _ew)
_eh = sum(x.get("hours_high", 0) for x in _ew)

effort = [{
    "metric": "effort", "js_total": o["js_total"], "ported": o["covered"],
    "verified": o["verified"], "remaining": o["js_total"] - o["covered"],
    "effort_lines": o["effort"],
    "rate_low": o["rate_low"], "rate_high": o["rate_high"],
    "serial_hours_low": o["hours_low"], "serial_hours_high": o["hours_high"],
    "critical_hours_low": o["crit_low"], "critical_hours_high": o["crit_high"],
    "agent_share_low": o["pool_each_low"], "agent_share_high": o["pool_each_high"],
    "wall_hours_low": o["wall_low"], "wall_hours_high": o["wall_high"],
    "agents": o["agents"], "cs_lines": o["cs_lines"], "cs_files": o["cs_files"],
    "not_built_lines": _nb,
    "extra_work_hours_low": _el, "extra_work_hours_high": _eh,
    "extra_work_items": "; ".join(x["what"] for x in _ew),
    "real_wall_hours_low": max(o["wall_low"], _el),
    "real_wall_hours_high": max(o["wall_high"], _eh),
    # The headline sentence, COMPUTED. It used to be hardcoded in the page and
    # went stale twice over: it claimed 10 agents when 5 were running, and it
    # named "citymap.js and foot.js" months after citymap.js reached 0 lines
    # left -- presenting a one-file constraint as a two-file one.
    "critical_label": o.get("crit_label", "nothing left"),
    # The raw budget is an UPPER BOUND; this is what two independent audits
    # could actually find. Both are published so the page can show the real
    # work beside the bound rather than charging hours for lines nobody can
    # locate. See docs/BUDGET_AUDIT.md.
    "audited_real_lines": o.get("audited_real_remaining"),
    "real_hours_low": o.get("real_hours_low"),
    "real_hours_high": o.get("real_hours_high"),
    "headline": (
        f"{o['covered']:,} of {o['js_total']:,} JavaScript lines now run as C#. "
        + (f"{o['agents']} agents are working the independent systems while "
           f"{o.get('crit_label')} \u2014 the "
           + ("only file" if o.get("crit_count", 0) == 1 else f"{o.get('crit_count')} files")
           + " needing one consistent head \u2014 is held back."
           if o.get("crit_count", 0) else
           f"{o['agents']} agents are working in parallel and no file is left that needs "
           f"one consistent head, so the pool binds the clock, not any single file.")
        + (f" Of the {o['js_total'] - o['covered']:,} lines still in the budget, two "
           f"independent line-by-line audits could only find about "
           f"{o.get('audited_real_remaining'):,} that are genuinely unported \u2014 treat the "
           f"rest as accounting slack, not work."
           if o.get("audited_real_remaining") else "")
    ),
}]

man = _man
units = [{
    "cs_file": u["cs"], "js_source": u.get("js") or "n/a",
    "js_lines_covered": u.get("covers", 0),
    "verified": "yes" if u.get("verified") else "no",
    "evidence": u.get("verified") or u.get("note", "") or "",
} for u in man["units"]]

# What is ACTUALLY left, as items, because the line count cannot see it: every
# item here is finished-and-unreachable code, a wrong constant, or a structural
# defect -- none of which moves `remaining`. Hand-maintained in docs/open_items.json.
open_items = json.load(open(os.path.join(ROOT, "docs", "open_items.json")))
for it in open_items:
    it.setdefault("owner", ""); it.setdefault("date", "")

for name, rows in (("port_systems", systems), ("port_effort", effort), ("port_units", units), ("port_open_items", open_items)):
    path = os.path.join(out, name + ".json")
    json.dump(rows, open(path, "w"), indent=1, ensure_ascii=False)
    print(f"  wrote {path}  ({len(rows)} rows)")

# Note for whoever next edits this: the dashboard reads ATTACHED FILES, not the
# repo, so running this is only half the job — the three JSON files must then be
# re-uploaded to the artifact and each dataset's `url` repointed, or the page
# keeps serving the previous numbers. It drifted twice: once showing city at
# 35.73% while the repo said 59.99%, and once stuck on an old total long enough
# for the user to think the estimate was frozen. Re-upload after every merge.
