#!/usr/bin/env python3
"""
Independent cross-check for CityLoader.cs / FacadeProps.cs / ChunkedInstancer.cs
/ CityCulling.cs — no Unity, straight from src/citymap.js's own text and the
same unity/Assets/StreamingAssets/tbilisi/city.json.gz data the C# port reads.

Three things, each printed as `KEY=value` lines with a distinct textual id
(tools/unity_port_check.py blanks standalone numbers but keeps digits inside
identifiers, so every id below is spelled out, never just a number):

  1. PROVES the load ORDER CityLoader.cs hard-codes (CriticalStageOrder /
     SceneryStageOrder) against the REAL citymap.js source text — the
     scenery array literal and the _cullDue/CULL_SLICES/SHADOW_DIST/cullDist
     constants are regex-extracted here, not retyped by hand a second time.

  2. Reimplements citymap.js:4990-5407's AC/downpipe/entrance/dish/parapet/
     roof-clutter item generation (FacadeProps.cs's own job) in pure Python,
     over the SAME real footprints (reusing tools/_probe_buildingquality.py's
     already-verified hash/classify/kit-fit functions as a library import —
     see its own module docstring for why those are trustworthy), so the
     facadePropCounts line here can be diffed NUMBER FOR NUMBER against
     CityLoadProbe.cs's `CL id=facadePropCounts` line.

  3. Reimplements CollapseAnim.Step's pure kinematics (CityCulling.cs) for
     the same fixed time steps CityLoadProbe.cs prints, so `collapseAtT*`
     lines pair exactly.

Run: python3 tools/_probe_cityload.py
"""
import gzip
import importlib.util
import json
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SA = os.path.join(ROOT, "unity", "Assets", "StreamingAssets", "tbilisi")
ART = os.path.join(ROOT, "unity", "Assets", "Art", "Textures")
CITYMAP_JS = os.path.join(ROOT, "src", "citymap.js")

# cap to match CityLoadProbe.cs's own `MaxBuildings = 20000` — the two sides
# must walk the same prefix of city.json's buildings array to agree.
MAX_BUILDINGS = 20000


def _import_buildingquality():
    """tools/_probe_buildingquality.py as a library — its hash_/district_at/
    in_old_town/is_cottage/polygon_area/classify/decide_early/*_fit functions
    are reused here verbatim rather than retyped a third time (JS -> C# ->
    Python once already in that file; a second Python copy would just be
    another place for the two to quietly drift)."""
    path = os.path.join(ROOT, "tools", "_probe_buildingquality.py")
    spec = importlib.util.spec_from_file_location("_probe_buildingquality", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


bq = _import_buildingquality()

FAIL = False


def check(label, cond):
    global FAIL
    status = "PASS" if cond else "FAIL"
    if not cond:
        FAIL = True
    print(f"JSONLY CL id={label} result={status}")


# =============================================================================
# 1. LOAD ORDER — proved against citymap.js's own text, not retyped.
# =============================================================================
js = open(CITYMAP_JS, encoding="utf-8").read()

m = re.search(r"const scenery = \[(.*?)\n\s*\];", js, re.S)
scenery_names = re.findall(r"\[\s*'([^']+)'", m.group(1)) if m else []
print(f"JSONLY CL id=sceneryOrderFromSource order={'>'.join(scenery_names)}")

EXPECTED_SCENERY = [
    "trees", "rocks", "grass", "streetlights", "road signs", "worship",
    "landmarks", "parks", "metro", "airport", "plane hall", "malls",
    "infrastructure", "shopfronts",
]
check("sceneryOrderMatchesCityLoader", scenery_names == EXPECTED_SCENERY)

cull_slices = int(re.search(r"CULL_SLICES\s*=\s*(\d+)", js).group(1))
shadow_dist = int(re.search(r"SHADOW_DIST\s*=\s*(\d+)", js).group(1))
full_move_sq = int(re.search(r"d2\s*>\s*(\d+)\)\s*this\._cullFull", js).group(1))
skip_move_sq = int(re.search(r"d2\s*<\s*(\d+)\s*&&\s*!this\._cullFull", js).group(1))
base_dist, dist_per_alt = (float(x) for x in re.search(
    r"this\.cullDist\s*\|\|\s*([\d.]+)\s*\+\s*alt\s*\*\s*([\d.]+)", js).groups())
print(f"JSONLY CL id=cullConstantsFromSource cullSlices={cull_slices} shadowDist={shadow_dist} "
      f"fullMoveSq={full_move_sq} skipMoveSq={skip_move_sq} baseCullDist={base_dist} cullDistPerAlt={dist_per_alt}")
check("cullSlicesMatchesCityCulling", cull_slices == 4)
check("shadowDistMatchesChunkedInstancer", shadow_dist == 500)
check("fullMoveSqMatchesCityCulling", full_move_sq == 250000)  # 500 m squared
check("skipMoveSqMatchesCityCulling", skip_move_sq == 4)        # 2 m squared
check("baseCullDistMatchesCityCulling", base_dist == 3500.0)
check("cullDistPerAltMatchesCityCulling", dist_per_alt == 3.0)

BUDGET_NAMES = {
    "acItems": 50000, "dishItems": 30000, "drainItems": 80000,
    "entranceItems": 60000, "parapetItems": 70000, "roofPropItems": 50000,
}
for name, expect in BUDGET_NAMES.items():
    found = re.findall(rf"{name}\.length\s*<\s*(\d+)", js)
    actual = int(found[0]) if found else None
    print(f"JSONLY CL id=budget_{name} fromSource={actual} expected={expect}")
    check(f"budgetMatches_{name}", actual == expect)


# =============================================================================
# 2. FACADE PROPS — reimplemented over real footprints, independent of
#    FacadeProps.cs itself (same source lines, fresh code).
# =============================================================================
with gzip.open(os.path.join(SA, "city.json.gz")) as fh:
    city = json.load(fh)
buildings = city["buildings"]

kit_meta_path = os.path.join(SA, "kit_meta.json")
kit_meta = json.load(open(kit_meta_path)) if os.path.exists(kit_meta_path) else {}
kit_house = kit_meta.get("house", [])
kit_out = kit_meta.get("out", [])
kit_apt = kit_meta.get("apt", [])
kit_shed = kit_meta.get("shed", [])
panel_sections = set(kit_meta.get("panel_sections", []))

# NOTE: no walls.json/wall_fams here — wallBucket only decides WINDOW
# material variants (FacadeRelief.cs's job), which this script does not
# recompute; none of the six item kinds below need it.


def kit_claimed(bi, xs, zs, n, area, h, district, cottage):
    """citymap.js:4661-4739 claim order — mirrors BuildingKit.TryClaim's
    FIRST-MATCH-WINS chain. Returns True if this footprint would get a real
    kit model instead of extrusion + relief (FacadeProps/FacadeRelief never
    run on it)."""
    if panel_sections and district == "soviet" and 13.0 <= h <= 19.5:
        if bq.panel_fit(xs, zs, panel_sections):
            return True
    if kit_house and cottage and bq.house_fit(xs, zs, kit_house):
        return True
    if kit_out and area <= 46.0 and bq.out_fit(xs, zs, kit_out):
        return True
    if kit_apt and not cottage and 8.5 <= h <= 34.0 and 120.0 <= area <= 1600.0 and bq.apt_fit(xs, zs, kit_apt, h):
        return True
    if kit_shed and area >= 260.0 and h <= 22.0 and not cottage and bq.shed_fit(xs, zs, kit_shed, h):
        return True
    return False


def us_table(xs, zs, n, u_phase):
    """citymap.js:4895-4905 — perimeter metres / 3, per-building phase."""
    us = [u_phase]
    perim = 0.0
    for i in range(n):
        j = (i + 1) % n
        perim += math.hypot(xs[j] - xs[i], zs[j] - zs[i])
        us.append(u_phase + perim / 3.0)
    return us


counts = {"ac": 0, "dish": 0, "drainpipe": 0, "entrance": 0, "parapet": 0, "roofProp": 0}
roof_variant_counts = {}
entrance_variant_counts = {"ent_old": 0, "entrance": 0}
n_processed = 0
n_kit_skipped = 0

n_total = min(len(buildings), MAX_BUILDINGS)
for bi in range(n_total):
    b = buildings[bi]
    n = (len(b) - 1) // 2
    if n < 3:
        continue
    xs = [b[1 + i * 2] / 10.0 for i in range(n)]
    zs = [b[2 + i * 2] / 10.0 for i in range(n)]
    cx, cz = sum(xs) / n, sum(zs) / n
    area = abs(bq.polygon_area(xs, zs))
    h = b[0] / 10.0
    district = bq.district_at(cx, cz)
    ot = bq.in_old_town(cx, cz, district)
    cottage = bq.is_cottage(area, h, ot)

    if kit_claimed(bi, xs, zs, n, area, h, district, cottage):
        n_kit_skipped += 1
        continue

    style, _red_roof, style_pitch = bq.classify(bi, h, n, area, district, cottage, ot)
    pitched_final = False  # see FacadeProps.BuildFacadeProps' own doc — not resolved at this integration point
    want_bal, want_wood, n_floors, want_win, _want_corn = bq.decide_early(bi, style, district, h, area, cottage, pitched_final)
    want_ac = h >= 6.0
    run_facade = (want_bal or want_wood or want_win or want_ac) and (n_floors >= 2 or cottage)
    n_processed += 1

    if run_facade:
        u_phase = math.floor(bq.hash_(bi + 8111) * 8)
        us = us_table(xs, zs, n, u_phase)

        ent_seg, ent_best = -1, 0.0
        for i in range(n):
            j = (i + 1) % n
            l2 = (xs[j] - xs[i]) ** 2 + (zs[j] - zs[i]) ** 2
            if l2 > ent_best:
                ent_best, ent_seg = l2, i

        pipe_count = 0
        for i in range(n):
            j = (i + 1) % n
            x1, z1 = xs[i], zs[i]
            ex, ez = xs[j] - x1, zs[j] - z1
            length = math.hypot(ex, ez)
            if length < 5.0:
                continue
            nx, nz = -ez / length, ex / length
            mx, mz = x1 + ex / 2.0, z1 + ez / 2.0
            if (mx - cx) * nx + (mz - cz) * nz < 0.0:
                nx, nz = -nx, -nz
            bays2 = int(math.floor(length / 3.4))

            if i == 0 and want_ac and counts["ac"] < 50000 and bq.hash_(bi + 31337) < 0.35:
                for u in range(2):
                    if bays2 <= 1:
                        break
                    t = (math.floor(bq.hash_(bi * 7 + u * 313) * bays2) + 0.5) / bays2
                    f = 1 + int(bq.hash_(bi * 13 + u * 71) * max(1, n_floors - 1))
                    # the JS also skips an AC that would be buried on a
                    # slope (py < heightAt(px,pz)+0.5) — this script has no
                    # TerrainSampler, so that one ground-slope gate is not
                    # reproduced here; everything upstream of it (the hash
                    # chain deciding WHERE a candidate AC/dish/drainpipe/
                    # entrance sits) is bit-for-bit the same, so this count is
                    # an acceptable OVER-count versus the real in-engine one.
                    counts["ac"] += 1

            if pipe_count < 2 and counts["drainpipe"] < 80000 and length > 7.0 and bq.hash_(bi * 31 + i * 7 + 5) < 0.6:
                counts["drainpipe"] += 1
                pipe_count += 1

            if i == ent_seg and (h >= 6.0 or cottage) and counts["entrance"] < 60000:
                old = (style == 3 or district == "historic"
                       or (district == "classic" and bq.hash_(bi + 833) < 0.7)
                       or (cottage and bq.hash_(bi + 833) < 0.45))
                counts["entrance"] += 1
                entrance_variant_counts["ent_old" if old else "entrance"] += 1

            if (want_bal or want_wood) and bays2 > 0:
                for k in range(bays2):
                    if bq.hash_(bi * 131 + i * 17 + k) < 0.45:
                        continue
                    for f in range(1, n_floors, 2):
                        if counts["dish"] < 30000 and bq.hash_(bi * 131 + i * 17 + k * 7 + f + 40009) < 0.15:
                            counts["dish"] += 1

    if not pitched_final and h >= 10.0:
        if counts["parapet"] < 70000:
            for i in range(n):
                j = (i + 1) % n
                plen = math.hypot(xs[j] - xs[i], zs[j] - zs[i])
                if plen >= 2.5:
                    counts["parapet"] += 1
        if counts["roofProp"] < 50000:
            rh1 = bq.hash_(bi + 90001)
            if h >= 20.0:
                counts["roofProp"] += 1
                roof_variant_counts["roof_box"] = roof_variant_counts.get("roof_box", 0) + 1
            if rh1 < 0.55:
                counts["roofProp"] += 1
                roof_variant_counts["roof_tank"] = roof_variant_counts.get("roof_tank", 0) + 1
            nv = 2 + int(bq.hash_(bi + 90005) * 3)
            counts["roofProp"] += nv
            roof_variant_counts["roof_vent"] = roof_variant_counts.get("roof_vent", 0) + nv
            if bq.hash_(bi + 90008) < 0.4:
                counts["roofProp"] += 1
                roof_variant_counts["ac"] = roof_variant_counts.get("ac", 0) + 1

print(f"NOTE CL id=facadePropsBuildings processed={n_processed} kitClaimsSkipped={n_kit_skipped} totalWalked={n_total}")
print(f"NOTE CL id=facadePropCounts ac={counts['ac']} dish={counts['dish']} drainpipe={counts['drainpipe']} "
      f"entrance={counts['entrance']} parapet={counts['parapet']} roofProp={counts['roofProp']}")
print(f"NOTE CL id=entranceVariants ent_old={entrance_variant_counts['ent_old']} entrance={entrance_variant_counts['entrance']}")
for name, c in sorted(roof_variant_counts.items()):
    print(f"NOTE CL id=roofPropVariant name={name} count={c}")
print("NOTE CL: diff this block's numbers against the matching "
      "`CL id=facadePropsBuildings` / `CL id=facadePropCounts` / "
      "`CL id=entranceVariants` / `CL id=roofPropVariant` lines CityLoadProbe.cs "
      "prints inside Unity — small drift is expected (this script's `py<...>` "
      "slope/ground checks are stubbed since it has no TerrainSampler; every "
      "other gate is bit-for-bit the same hash chain).")


# =============================================================================
# 3. COLLAPSE ANIMATION — CollapseAnim.Step's pure kinematics, citymap.js:
#    2409-2457 (dust/fire FX excluded — see CityCulling.cs's own doc).
# =============================================================================
def collapse_step(t, dur, from_kh, from_dark, from_lean, inst_or_landmark):
    p = min(1.0, t / dur)
    fall = p * p
    if inst_or_landmark:
        kh = from_kh + (0.13 - from_kh) * fall
        tilt = 0.02 + 0.06 * p
        wobble = math.sin(t * 31.0) * 0.03 * (1.0 - p)
        return dict(p=p, kh=kh, tilt=tilt, wobble=wobble, done=p >= 1.0)
    kh = from_kh + (0.12 - from_kh) * fall
    buckle = 0.35 + 1.05 * p
    dark = from_dark + (0.28 - from_dark) * p
    lean = from_lean + 0.09 * p
    return dict(p=p, kh=kh, buckle=buckle, dark=dark, lean=lean, done=p >= 1.0)


for step in range(1, 6):
    t = float(step)
    fi = collapse_step(t, 4.0, 1.0, 0.0, 0.0, True)
    fs = collapse_step(t, 4.0, 1.0, 0.0, 0.0, False)
    print(f"CL id=collapseAtT{step} instP={fi['p']:.4f} instKh={fi['kh']:.4f} instTilt={fi['tilt']:.4f} "
          f"instWobble={fi['wobble']:.4f} instDone={fi['done']} "
          f"shellP={fs['p']:.4f} shellKh={fs['kh']:.4f} shellBuckle={fs['buckle']:.4f} "
          f"shellDark={fs['dark']:.4f} shellLean={fs['lean']:.4f} shellDone={fs['done']}")

print("CL id=done")
if FAIL:
    print("CL RESULT: one or more source-text checks FAILED — see result=FAIL lines above", file=sys.stderr)
    sys.exit(1)
