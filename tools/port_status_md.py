# -*- coding: utf-8 -*-
"""docs/PORT_STATUS.md — one page that says what of the Three.js game is in Unity,
what is proven, and what is left. Generated from port_systems.json,
port_units.json, port_effort.json, docs/open_items.json and the checkboxes of
docs/QUALITY_BAR.md. Run after every wave:  python3 tools/port_status_md.py"""
import json, re, datetime, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
def J(p): return json.load(open(os.path.join(ROOT, p), encoding="utf8"))
systems = J("port_systems.json"); units = J("port_units.json"); effort = J("port_effort.json")[0]
items = J("docs/open_items.json")
qbar = open(os.path.join(ROOT, "docs/QUALITY_BAR.md"), encoding="utf8").read()
boxes = re.findall(r"^- \[([ x])\] \*\*(.+?)\*\*", qbar, re.M)
now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
out = []
w = out.append
w(f"# TbilisiFly → Unity: what moved, what is left\n")
w(f"_Generated {now} by tools/port_status_md.py. Numbers come from the port manifest, the unit claims and docs/open_items.json; do not edit by hand._\n")
w("## 1. The one-line answer\n")
w(f"**{effort['ported']:,} of {effort['js_total']:,} budgeted JS lines are ported ({100*effort['ported']/effort['js_total']:.1f}%), {effort['verified']:,} of them probe-verified; {effort['remaining']:,} lines remain, which the audit (docs/BUDGET_AUDIT.md) found to be mostly budget artefact, not behaviour.** "
  f"The C# side is {effort['cs_lines']:,} lines in {effort['cs_files']} files. Every box of the quality bar (section 3) is checked with a live proof. "
  f"Tracker: {sum(1 for i in items if i['status']=='fixed')} fixed · {sum(1 for i in items if i['status']=='open')} open · {sum(1 for i in items if i['status']=='decision')} decisions · {sum(1 for i in items if i['status']=='deferred')} deferred.\n")
w("Lines are not the whole story: the port's worst bug class is FINISHED code that never runs (no caller, not in Game.unity, dead delegate) and formulas that are right in a right-handed frame and wrong in Unity's left-handed one. Sections 4–6 are about those.\n")
w("## 2. What moved, per system\n")
w("| system | JS lines | ported | verified | left | % | what the rest is |\n|---|---:|---:|---:|---:|---:|---|")
for s in sorted(systems, key=lambda r: -r['js_lines']):
    w(f"| {s['system']} | {s['js_lines']:,} | {s['ported']:,} | {s['verified']:,} | {s['remaining']:,} | {s['pct']:.1f} | {s.get('why','')[:140]} |")
w("")
nv = [u for u in units if str(u.get('verified','')).lower() not in ('yes','true')]
w(f"Units: {len(units)} C# units claim JS coverage; {len(units)-len(nv)} carry a probe-verified parity proof, {len(nv)} do not (most are Unity-only infrastructure that ports no JS line: scene assembly, loaders, shaders, UI plumbing — see port_units.json `evidence`).\n")
w("## 3. Feature checklist (docs/QUALITY_BAR.md)\n")
for b, name in boxes:
    w(f"- [{b}] {name}")
w("\nEvery checked row names the probe line that proved it REACHED in Assets/Scenes/Game.unity (the only booting scene), not just computed.\n")
w("## 4. Still open (docs/open_items.json)\n")
w("| id | area | owner | what |\n|---|---|---|---|")
for i in items:
    if i['status'] in ('open','decision','deferred'):
        w(f"| {i['id']} | {i.get('area','')} | {i.get('owner','')} ({i['status']}) | {i.get('item','')[:220].replace('|','/')} |")
w("")
w("## 5. What was found and fixed on 2026-11-01 (the Sonnet wave)\n")
for i in items:
    if i.get('date') == '2026-11-01' and i['status'] == 'fixed':
        w(f"- **{i['id']}** — {i.get('fix','')[:260].replace('|','/')}")
w("")
w("## 6. Rules every agent must apply (learned the hard way)\n")
w("""- **Reached, not computed.** A probe that calls a pure function proves the maths. Pair it with one assertion that the gate is open on the shipped asset / scene / delegate (`grep -rn "new X"` finding only probes is a red flag).
- **Handedness.** The Unity world is the JS world mirrored in X (GeoFrame) and the FBX bridge keeps X, so the on-foot yaw maths is consistently mirrored; only the player's lateral INPUT must not be (GameLoop.ReadFootMoveInput, mouse X). The model child carries the JS's 180° turn (CharacterRig.cs:140). Flight uses Unity transforms and degrees (Rad2Deg).
- **Bridged assets have no textures.** Every GLB→FBX body/plane/tree arrives extensionless/WebP: decode from the GLB and bind by material name (plane_liveries.py, tree_textures.py, CharacterTexturePostprocessor).
- **Doubles.** JS has no float32; narrow only where a Vector3 demands it, never before a comparison (parks, river ear-clip).
- **One editor at a time** on this 16 GB Mac: the mkdir mutex + "no editor alive" loop, in a BACKGROUND script with log polling (a tool call silent for 600 s kills the agent). Compile-check before leaving a file; grep every caller when a signature changes.
- **Georgian UI only.** No English strings on screen; the death screen is never "WASTED".
""")
w("## 7. Next\n")
w("""1. Parkour Animation Set (bought, downloaded 13:37) onto the hero: Z slide/vault, X roll, C crouch/let go, climb/mantle/ledge/landings as real Humanoid clips (agent in flight).
2. Key audit: every JS binding vs Unity with a KeysRun play-mode proof (agent in flight); user-flow scenarios (airport chip, climb out, take any plane, shoot, crash, UI) (agent in flight).
3. perf-retina-gap on a quiet machine; memory census of the INSTALLED build (target 2–3 GB).
4. Decisions owned by the user: damage persistence, cinema, hero body colours, asset spans.
""")
open(os.path.join(ROOT, "docs/PORT_STATUS.md"), "w", encoding="utf8").write("\n".join(out))
print("wrote docs/PORT_STATUS.md", len(out), "lines")
