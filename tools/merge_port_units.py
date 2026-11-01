#!/usr/bin/env python3
"""Fold docs/port_units/*.json into docs/port_manifest.json.

Many agents port at once; having them all edit the manifest directly is a write
conflict. Each drops a JSON array of unit objects here instead and this merges
them, keyed by `cs` so a later claim replaces an earlier one for the same file.
"""
import glob, json, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
man_path = os.path.join(ROOT, "docs", "port_manifest.json")
man = json.load(open(man_path))

def norm_cs(path):
    """Normalise a C# path so one file cannot be claimed twice.

    Agents wrote the same file as both "Assets/Scripts/Combat/AirframeBreakup.cs"
    and "unity/Assets/Scripts/Combat/AirframeBreakup.cs". Keying on the raw string
    treated those as two units and breakup.js ended up claiming 741 of its 409
    lines — 181%.

    A multi-file claim must also key the same however its files are ORDERED.
    "FireSystem.cs, Fireball.shader, ImpactSprite.shader" and
    "Fireball.shader, FireSystem.cs" are the same claim written two ways, but
    keying on the raw string (or on its first token) made them two units, so a
    restatement of fire.js stacked on top of the claim it meant to replace:
    420 + 537 = 957 against a 537-line budget, and the clamp then quietly cut
    the NEWER, more accurate claim down to 117. The old entry kept billing
    forever because nothing ever removes a manifest unit no unit file claims.
    Sorting the parts makes order irrelevant.
    """
    def one(q):
        q = (q or "").replace("\\", "/").strip().lstrip("./")
        if q.startswith("unity/"):
            q = q[len("unity/"):]
        return q

    parts = [one(q) for q in (path or "").split(",")]
    parts = [q for q in parts if q]
    if not parts:
        return ""
    return ", ".join(sorted(parts))


def norm_js(raw):
    """Normalise a `js` field to a bare "name.js" module, or None if there is no
    JS counterpart at all.

    Two spellings of the same source file were silently worth zero. The manifest
    keys modules by bare name (`cinema`, via port_progress's `u["js"][:-3]`), but
    agents wrote "src/cinema.js", and that lookup missed, so 9,011 lines of real,
    compiling, probe-passing work counted as 0% — crane and cinema read 0.0% with
    2,404 lines ported. Worse, some agents used `js` as a NOTES field:
    "src/game.js (world.damageHit/nearWindow/...)", so the module name has prose
    welded to it. Take the first token, drop a leading src/, and keep the rest as
    a note rather than throwing it away.

    Returns (module_js, leftover_note). module_js is None for units that
    legitimately have no JS counterpart ("n/a", a tools/*.py bake, src/style.css)
    — those must NOT count against the JS line budget in either direction.
    """
    raw = (raw or "").strip()
    if not raw:
        return None, ""
    head, _, rest = raw.partition(" ")
    head = head.replace("\\", "/").strip().rstrip(",;")
    if head.startswith("src/"):
        head = head[len("src/"):]
    # A bare module name with no suffix ("foot", "guns") is a real spelling
    # agents use, and it used to resolve to None — so two correct cross-credit
    # claims filed by the port audit (RifleHolds.cs 110 lines of foot.js,
    # ImpactFx.cs 51 of guns.js) counted as zero against any module while
    # looking filed. Accept it when, and only when, src/<name>.js exists.
    if not head.endswith(".js") and os.path.exists(os.path.join(ROOT, "src", head + ".js")):
        head += ".js"
    if not head.endswith(".js"):
        return None, raw
    if not os.path.exists(os.path.join(ROOT, "src", head)):
        return None, raw
    return head, rest.strip()


# Keyed by (C# file, JS module), NOT by the C# file alone.
#
# One C# file can legitimately cover two JS modules: Combat/DamageDecals.cs
# ports 339 lines of planedamage.js AND 67 of glass.js, and an agent correctly
# filed that as two entries. Keyed on the path alone the second overwrote the
# first and 272 lines vanished — the same erasure as the delta-claim bug, from a
# different direction. The pair key lets a file restate its coverage PER MODULE,
# which is the thing a claim actually describes, and the per-module clamp below
# still caps each module at its source file's real line count.
by_cs = {(norm_cs(u["cs"]), u.get("js") or ""): u for u in man.get("units", [])}
added = replaced = 0
drops = []
lowered = []
raised = []
restated = set()
restated_by = {}
conflicts = {}

# recovered.json holds STRUCTURAL ESTIMATES for code whose agent stalled before
# declaring coverage. It is a FALLBACK ONLY. Once an agent files a real claim for
# a module, the estimate for that module must be dropped — counting both made ui
# read 117.81% of a 2,105-line budget, which is impossible on its face.
real_mods = set()
for f in sorted(glob.glob(os.path.join(ROOT, "docs", "port_units", "*.json"))):
    if os.path.basename(f) == "recovered.json":
        continue
    try:
        for c in json.load(open(f)):
            if isinstance(c, dict) and c.get("js"):
                m, _ = norm_js(c["js"])
                if m:
                    real_mods.add(m)
    except Exception:
        pass
if real_mods:
    print(f"  {len(real_mods)} module(s) have agent-filed claims; estimates for them are dropped")

for f in sorted(glob.glob(os.path.join(ROOT, "docs", "port_units", "*.json"))):
    try:
        claims = json.load(open(f))
    except Exception as e:
        print(f"  SKIP {os.path.basename(f)}: {e}")
        continue
    if not isinstance(claims, list):
        print(f"  SKIP {os.path.basename(f)}: not a JSON array")
        continue
    is_estimate = os.path.basename(f) == "recovered.json"
    for c in claims:
        if not isinstance(c, dict) or "cs" not in c:
            continue
        if is_estimate and norm_js(c.get("js"))[0] in real_mods:
            continue  # a real claim supersedes the estimate for this module
        c.setdefault("verified", None)
        c.setdefault("covers", 0)
        # canonicalise the js field in place, preserving any prose as a note
        _m, _note = norm_js(c.get("js"))
        c["js"] = _m or ""
        if _note:
            c["note"] = (c.get("note", "") + " " + _note).strip()
        k = (norm_cs(c["cs"]), c.get("js") or "")
        c["cs"] = k[0]   # k is (cs, js) now — store just the path
        # Mark a restated key UNCONDITIONALLY, not only when it changes
        # something this run. The merge re-seeds from the manifest it wrote last
        # time, so on the second run the restatement EQUALS what is already
        # there, no branch fires, the key never enters `restated`, and the stale
        # claim raises it right back. The ledger then OSCILLATES:
        # CombatWorldAdapter.cs read 60 after one merge and 700 after the next,
        # which is worse than either number, because the headline silently
        # depended on how many times this script had been run.
        # tools/_test_merge_idempotent.py exists to catch exactly that.
        if c.get("restates"):
            restated.add(k)
        if k in by_cs:
            prev = by_cs[k].get("covers", 0)
            now = c.get("covers", 0)
            # ONE `cs` FILE GETS ONE CLAIM, AND IT IS THAT FILE'S WHOLE
            # COVERAGE. Four cases, in this order — the order is the contract:
            #
            #  1. The claim sets `"restates": true`. It WINS, up or down. This
            #     is the audited-correction escape hatch; without it the ledger
            #     was a ratchet that could only ever go up (CombatWorldAdapter
            #     claimed 700 game.js lines while citing zero of them, and there
            #     was no way to say 60). It must win in BOTH directions, or an
            #     audited claim that happens to RAISE loses to the stale one it
            #     is correcting — the same failure mirrored.
            #  2. The key has already been restated and this claim has not got
            #     the flag. REFUSE it, whichever way it moves. Otherwise the
            #     audited number survives only until the next file in SORT
            #     ORDER: playable.json's stale 694 undid an audited 60 purely
            #     because "p" > "g".
            #  3. An ordinary claim that LOWERS. Keep the higher number. A lower
            #     claim is nearly always a second agent reporting only the DELTA
            #     it added to a file someone else already claimed in full, and
            #     replacing erases the original — that destroyed 2,532 lines in
            #     one merge (FootPoses.cs 1294 -> 0, WindRider.cs 625 -> 0,
            #     DamageDecals.cs 339 -> 67, WarehouseCrane.cs 222 -> 26,
            #     Guns.cs 145 -> 0) and made the headline UNDERSTATE the port.
            #     To genuinely reduce a number, use case 1 with evidence.
            #  4. An ordinary claim that RAISES. Accept it; the per-module clamp
            #     below still caps the module at its real budget.
            if c.get("restates"):
                if now != prev:
                    lowered.append((f"{k[0]} [{k[1] or 'n/a'}]", prev, now,
                                    os.path.basename(f)))
                # CONFLICT: more than one file restating the SAME key. Resolution
                # was alphabetical-filename order with no warning, so the
                # last-processed file silently stomped the others: gameadapter.json
                # ('g') overwrote both coregame.json ('c', +4) and defects.json
                # ('d', +2) back to 60 on every run, and the agent that lost its
                # work could only find out by diffing the manifest. The number
                # being wrong is recoverable; being wrong SILENTLY is not, so this
                # now shouts and names every claimant. A human picks the total —
                # the tool must not guess, because these are written as totals by
                # authors who could not see each other.
                if k in restated_by and restated_by[k][-1][1] != now:
                    conflicts.setdefault(k, list(restated_by[k]))
                    conflicts[k].append((os.path.basename(f), now))
                restated_by.setdefault(k, []).append((os.path.basename(f), now))
                restated.add(k)
            elif k in restated and now != prev:
                raised.append((f"{k[0]} [{k[1] or 'n/a'}]", now, prev,
                               os.path.basename(f)))
                c = dict(c)
                c["covers"] = prev
                c["covers_refused_claim"] = now
            elif now < prev:
                drops.append((f"{k[0]} [{k[1] or 'n/a'}]", prev, now,
                              os.path.basename(f)))
                c = dict(c)
                c["covers"] = prev
                c["covers_lowered_claim"] = now

            replaced += 1
        else:
            added += 1
        by_cs[k] = c
    print(f"  merged {len(claims):>3} claims from {os.path.basename(f)}")

if lowered:
    print("\n  DELIBERATE RESTATEMENTS (\"restates\": true — this claim WINS, up or down):")
    for k, prev, now, src in sorted(lowered, key=lambda t: t[2] - t[1]):
        print(f"    {k}: {prev} -> {now}  (from {src}, {now - prev:+d})")

if conflicts:
    print("\n  !! RESTATE CONFLICT — several files restate the same key to DIFFERENT")
    print("     totals. Resolution is filename order, which is arbitrary. Consolidate")
    print("     these into ONE authoritative claim and delete the others:")
    for k, v in conflicts.items():
        print(f"     {k[0]} [{k[1] or 'n/a'}]")
        for src, n in v:
            print(f"        {src:26} claims {n}")
        print(f"        -> applied: {v[-1][1]} (from {v[-1][0]}, last in filename order)")

if raised:
    print("\n  REFUSED to raise a deliberately restated claim (audited number kept):")
    for k, tried, kept, src in raised:
        print(f"    {k}: {src} claimed {tried}, kept the audited {kept}")

if drops:
    print("\n  COVERAGE DROPPED on a file that was already claimed:")
    for k, prev, now, src in sorted(drops, key=lambda t: t[1] - t[2], reverse=True):
        print(f"    {k}: {prev} -> {now}  (from {src}, -{prev - now})")
    print("    KEPT THE HIGHER VALUE in each case (the lower one is recorded as")
    print("    covers_lowered_claim). One `cs` file gets ONE claim and it is that")
    print("    file's WHOLE coverage — to add to a file someone else claimed,")
    print("    restate the total rather than filing the delta.")

# Sort on the pair, stringified, so a missing/odd field cannot make the
# comparison raise (it did once: str vs tuple).
man["units"] = sorted(by_cs.values(), key=lambda u: (str(u.get("cs", "")), str(u.get("js", ""))))

# Migrate units ALREADY in the manifest whose port_units source file is gone, so
# an old "src/foo.js" spelling cannot keep hiding its coverage. Note that the
# over-claim guard below joins ROOT/src/<js>, so "src/foo.js" resolved to
# "src/src/foo.js", did not exist, and was SKIPPED — which is why the guard never
# caught this. Normalising here arms the guard as well as the progress number.
_migrated = 0
for u in man["units"]:
    m, note = norm_js(u.get("js"))
    if (m or "") != (u.get("js") or ""):
        u["js"] = m or ""
        if note and note not in (u.get("note") or ""):
            u["note"] = ((u.get("note", "") or "") + " " + note).strip()
        _migrated += 1
if _migrated:
    print(f"  normalised the js field on {_migrated} pre-existing unit(s)")

# A note that says a file "ALSO covers" another module, with no matching claim
# for that module, is the cross-attribution artifact that made roads.js read 31%
# ported while being 100% ported, and sent a briefing to re-port 272 lines of
# already-finished road-deck code. The work is there; only the credit is missing,
# and every gap query keyed on the module misses it. Three existed; all three are
# now filed. This catches the next one on the merge that introduces it.
import re as _re
_have = {(u["cs"], u.get("js") or "") for u in man["units"]}
_missing = []
for u in man["units"]:
    for _m in _re.finditer(r"(?:ALSO|also|Also) covers\s+(?:src/)?([a-z_]+\.js)", u.get("note") or ""):
        other = _m.group(1)
        if other != (u.get("js") or "") and (u["cs"], other) not in _have:
            _missing.append((u["cs"], u.get("js"), other))
if _missing:
    print("\n  CROSS-ATTRIBUTION: a note says these files also cover another module,")
    print("  but no claim exists for it — so that module's coverage reads as missing:")
    for cs, tagged, other in _missing:
        print(f"    {cs}  tagged js={tagged}  also covers {other} (unclaimed)")
    print("  File a second claim with the same `cs` and js=<that module>; the merge")
    print("  key is (cs, js), so both coexist and the per-module clamp still applies.")

# No module may claim more lines than its source file has. This has gone wrong
# four times now (estimates counted alongside real claims; ui at 117%; one file
# under two path spellings putting breakup.js at 181%; and the js-spelling bug
# that made the guard itself skip every "src/foo.js" unit).
#
# Two honest causes remain, and they are CLAMPED rather than rejected:
#   1. A unit covering SEVERAL modules ("src/smoke.js, src/rockets.js (trail
#      only)") has one `covers` number and no declared split, so the first module
#      would take all 170 lines of its own 80-line file.
#   2. A unit that counted its own C# lines instead of JS lines covered
#      (devmode.js: 406 claimed of 357).
# Clamping can only ever LOWER the headline, so it cannot flatter the number; the
# declared value is kept in `covers_declared` and every clamp is printed.
import collections


def mod_lines(js):
    sp = os.path.join(ROOT, "src", js or "")
    if not js or not os.path.exists(sp):
        return None
    return sum(1 for _ in open(sp, errors="replace"))


# Drop a manifest unit that a unit file USED to spell differently.
#
# Nothing in this script ever removed a unit, so when a claim came back under a
# different `cs` spelling the old entry stayed and kept billing: fire.js read
# 654 of a 537-line budget from one claim written two ways. Purging every
# unclaimed unit would be wrong — a few legitimate entries (Data/TerrainSampler.cs,
# Data/CityData.cs) predate the per-agent unit files and no file claims them —
# so the test is deliberately narrow: drop it only when a LIVE claim for the SAME
# module names at least one of the same files. That is a respelling, not a
# legacy entry, and keeping both double-bills the same work.
live_keys = set()
live_sets = collections.defaultdict(list)   # js module -> file set of each live claim
for f in sorted(glob.glob(os.path.join(ROOT, "docs", "port_units", "*.json"))):
    # recovered.json is the structural-estimate FALLBACK. A vague bundled
    # estimate must never evict a specific real claim: its 62-line entry names
    # {BuildingBuilder, BuildingFacts, CityData, TerrainSampler} together, and
    # on a 1-file-overlap rule that deleted the separate, real CityData.cs (35)
    # and TerrainSampler.cs (64) units outright.
    if os.path.basename(f) == "recovered.json":
        continue
    try:
        raw = json.load(open(f))
    except Exception:
        continue
    for c in (raw if isinstance(raw, list) else raw.get("units", [])):
        if not isinstance(c, dict) or not c.get("cs"):
            continue
        mm, _ = norm_js(c.get("js") or "")
        live_keys.add((norm_cs(c["cs"]), mm or ""))
        if mm:
            live_sets[mm].append(set(norm_cs(c["cs"]).split(", ")))

attributed_now = {cs for (cs, js) in live_keys if js}

superseded = []
kept = []
for u in man["units"]:
    k = (norm_cs(u["cs"]), u.get("js") or "")
    mine = set(k[0].split(", "))
    # TWO shared files, not one. Sharing a single C# file between claims of
    # different scope is completely normal — BuildingBuilder.cs alone is named
    # by five separate citymap.js claims — so a 1-file overlap is no evidence
    # of a respelling. Two or more shared files means it IS the same bundle
    # written another way, which is the fire.js case exactly (FireSystem.cs AND
    # Fireball.shader appear in both spellings, so 420 + 537 billed against a
    # 537-line budget and the clamp then cut the newer, better claim to 117).
    if k not in live_keys and k[1] and any(len(mine & ls) >= 2 for ls in live_sets.get(k[1], [])):
        superseded.append((u["cs"], k[1], u.get("covers", 0)))
        continue
    # An UNATTRIBUTED unit (js == "") whose exact same files are now claimed
    # under a real module is that same claim, newly attributed. The rule above
    # can never catch this, because a single-file claim shares at most one file;
    # without this, correcting an attribution left the old entry behind billing
    # the same lines under NO module — invisible, since port_progress skips
    # units with no module. Two of the port audit's own cross-credits
    # (RifleHolds.cs 110, ImpactFx.cs 51) sat in the manifest twice this way the
    # moment norm_js learned to read a bare module name like "foot".
    if not k[1] and norm_cs(u["cs"]) in attributed_now:
        superseded.append((u["cs"], "(was unattributed)", u.get("covers", 0)))
        continue
    kept.append(u)
man["units"] = kept
if superseded:
    print("\n  DROPPED respelled units (a live claim names the same file):")
    for cs, js, cov in sorted(superseded, key=lambda t: -t[2]):
        print(f"    {js}: -{cov} lines, {cs[:70]}")

by_mod = collections.defaultdict(list)
for u in man["units"]:
    if u.get("js"):
        by_mod[u["js"]].append(u)

# A module's budget is its line count MINUS what is formally excluded: an
# excluded line is one nothing needs to port, so it cannot also be covered.
# Clamping against the raw `wc -l` let three modules bill more than they have
# (fire.js 717 covered + 180 excluded against a 717-line file, anims.js +134,
# citymap.js +107) while this very check printed "no module over-claims" —
# because each half was individually under the raw total. The audit that found
# it had to compute `covers + excluded > lines` by hand. That is now the budget
# everywhere, so the clamp and the refuse-to-write guard below both see it.
excl_by_mod = collections.defaultdict(int)
for e in man.get("excluded", []):
    m, _ = norm_js(e.get("js") or "")
    if m:
        excl_by_mod[m] += e.get("excluded", 0)

def budget(js):
    """Lines of `js` that a `covers` claim is allowed to account for."""
    n = mod_lines(js)
    return None if n is None else max(0, n - excl_by_mod.get(js, 0))

clamps = []
for js, units in sorted(by_mod.items()):
    n = budget(js)
    if n is None:
        continue
    total = sum(u.get("covers", 0) for u in units)
    if total <= n:
        continue
    # Take the overflow off the biggest claimants first: the largest claim is the
    # most likely to be the multi-module or C#-counted one.
    over = total - n
    for u in sorted(units, key=lambda x: -x.get("covers", 0)):
        if over <= 0:
            break
        c = u.get("covers", 0)
        cut = min(c, over)
        u.setdefault("covers_declared", c)
        u["covers"] = c - cut
        over -= cut
        clamps.append((js, u["cs"], c, u["covers"], n, total))

if clamps:
    print("\n  CLAMPED over-claims (declared kept in covers_declared):")
    for js, cs, was, now, n, total in clamps:
        ex = excl_by_mod.get(js, 0)
        extra = f" ({mod_lines(js)} lines - {ex} excluded)" if ex else ""
        print(f"    {js}: {total} claimed of {n}{extra} -> {os.path.basename(cs)} {was}->{now}")

# Anything still over is a genuine impossibility, not an attribution guess.
still = [(js, sum(u.get("covers", 0) for u in us), budget(js))
         for js, us in by_mod.items() if budget(js) is not None
         and sum(u.get("covers", 0) for u in us) > budget(js)]
if still:
    print("\n  OVER-CLAIM DETECTED — refusing to write an inflated manifest:")
    for js, c, n in sorted(still, key=lambda t: -t[1]):
        print(f"    {js}: claims {c} of {n} lines ({100.0 * c / n:.0f}%)")
    raise SystemExit(1)

json.dump(man, open(man_path, "w"), indent=2)
print(f"units now {len(man['units'])}  (+{added} new, {replaced} replaced)")
print(f"no module over-claims (checked {len(by_mod)} modules)")
