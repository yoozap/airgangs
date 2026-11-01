#!/usr/bin/env python3
"""
Computes, straight from the data files (no Unity, no running game), the same
building-quality facts the Unity-side BuildingQualityProbe.cs is meant to
print — a from-scratch Python re-implementation of:

  WallMaterials.Hash / Classify / ResolveWallBucket / ResolveRoofBucket
  BuildingKit.MeasureObb / PanelFit / HouseFit / AptFit / ShedFit / OutFit / TryClaim
  BuildingParts.Expand (IsStepped)
  FacadeRelief.DecideEarly

...against unity/Assets/StreamingAssets/tbilisi/{city.json.gz,levels.json,
parts.json} and unity/Assets/Resources/Art/Textures/{walls,roofs}/*.json — the EXACT
same files the C# port reads. Deliberately scoped to the parts that do NOT
need terrain sampling (district/area/height/footprint-shape only): the actual
window/balcony ITEM placement depends on TerrainSampler.HeightAtGame, which
this script does not reimplement — see docs/port_units/buildingquality.json
for what that leaves unverified.

Prints `KEY=value` lines, each carrying a distinct textual id so
tools/unity_port_check.py's line-pairing (blank standalone numbers, keep
digits inside identifiers) lines them up against BuildingQualityProbe.cs's
output one for one.

Run: python3 tools/_probe_buildingquality.py
"""
import gzip
import json
import math
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SA = os.path.join(ROOT, "unity", "Assets", "StreamingAssets", "tbilisi")
ART = os.path.join(ROOT, "unity", "Assets", "Art", "Textures")

# ---- GeoFrame.cs constants (full precision — see that file's own warning
# not to round these to city.json's meta.mlat/mlon) ----
LAT0 = 41.7252
LON0 = 44.7458
M_LAT = 111067.92066361613
M_LON = 83206.33860686835


def hash_(x: float) -> float:
    """WallMaterials.Hash / citymap.js:4176-4180, bit-for-bit."""
    v = (x * 2654435761.0) % 4294967296.0
    if v < 0:
        v += 4294967296.0
    v_u = int(v) & 0xFFFFFFFF
    shifted13 = v_u >> 13
    xored = shifted13 ^ v_u
    # ToInt32 reinterpretation of a uint32 (sign bit)
    if xored >= 0x80000000:
        xored -= 0x100000000
    v2 = float(xored) * 1274126177.0
    li = int(v2)
    mod = li % 4294967296
    if mod < 0:
        mod += 4294967296
    shifted16 = mod >> 16
    return (shifted16 % 1000) / 1000.0


# ---- districts (WallMaterials.BuildDistricts) ----
def to_game_xz(lat, lon):
    return (-(lon - LON0) * M_LON, (lat - LAT0) * M_LAT)


_DISTRICTS_RAW = [
    ("historic", 41.6885, 44.8095, 800), ("historic", 41.6875, 44.7965, 750),
    ("historic", 41.695, 44.79, 650), ("historic", 41.7055, 44.794, 1100),
    ("historic", 41.696, 44.818, 700), ("classic", 41.706, 44.783, 800),
    ("classic", 41.7095, 44.758, 1300), ("soviet", 41.733, 44.751, 1800),
    ("soviet", 41.794, 44.811, 2000), ("soviet", 41.792, 44.78, 1300),
    ("soviet", 41.786, 44.837, 1200), ("soviet", 41.706, 44.907, 1800),
    ("soviet", 41.716, 44.876, 1200), ("soviet", 41.685, 44.856, 1600),
    ("soviet", 41.786, 44.752, 1800), ("soviet", 41.721, 44.713, 1300),
]
DISTRICTS = [(kind, *to_game_xz(lat, lon), r * r) for kind, lat, lon, r in _DISTRICTS_RAW]


# ---- landmark exclusion rings (src/landmarks.js:9-42 EXCLUSIONS, consumed at
# citymap.js:4601, ported in BuildingBuilder.InLandmarkExclusion) ------------
#
# WHY THIS EXISTS: without it this oracle read the CORRECT C# as a 280-claim
# regression (withFacts 17604 -> 17545, coloured 40 -> 37, claims 195149 ->
# 194869). A real landmark/metro/mall/airport model stands at each of these
# spots, so citymap.js skips the OSM footprint there; the C# gained that skip
# on 2026-10-27 (the fix that removed 143 grey boxes from under real
# landmarks) and this file modelled no exclusions at all. Sixth stale-oracle
# finding -- see docs/QUALITY_BAR.md's "NON-FINDING 2026-10-30" entry.
#
# The rings are PARSED out of src/landmarks.js, never retyped: a hand-copied
# table is one more thing to go stale, which is the whole defect above.
def _load_exclusions():
    path = os.path.join(ROOT, "src", "landmarks.js")
    src = open(path, encoding="utf-8").read()
    m = re.search(r"export const EXCLUSIONS\s*=\s*\[(.*?)\n\];", src, re.S)
    if not m:
        raise SystemExit("_probe_buildingquality: EXCLUSIONS not found in src/landmarks.js")
    rings = []
    for lat, lon, r in re.findall(
            r"\{\s*lat:\s*(-?[\d.]+)\s*,\s*lon:\s*(-?[\d.]+)\s*,\s*r:\s*(-?[\d.]+)\s*\}", m.group(1)):
        x, z = to_game_xz(float(lat), float(lon))
        rings.append((x, z, float(r) ** 2))
    if not rings:
        raise SystemExit("_probe_buildingquality: EXCLUSIONS parsed empty")
    return rings


EXCLUSIONS = _load_exclusions()


def in_landmark_exclusion(x, z):
    """citymap.js:4601 -- true if a GAME-frame point is inside any ring."""
    for ex, ez, r2 in EXCLUSIONS:
        if (x - ex) ** 2 + (z - ez) ** 2 < r2:
            return True
    return False


OLD_TOWN = (-5089.0, -3910.0, 1700.0)


def district_at(x, z):
    for kind, dx, dz, r2 in DISTRICTS:
        if (x - dx) ** 2 + (z - dz) ** 2 < r2:
            return kind
    return None


def in_old_town(x, z, district):
    if district == "historic":
        return True
    return (x - OLD_TOWN[0]) ** 2 + (z - OLD_TOWN[1]) ** 2 < OLD_TOWN[2] ** 2


def is_cottage(area, h, old_town):
    return (not old_town) and h < 13.0 and 40.0 < area < 260.0


# ---- footprint geometry (BuildingKit.cs) ----
def polygon_area(xs, zs):
    """Signed shoelace area — THREE.ShapeUtils.area / BuildingKit.PolygonArea."""
    n = len(xs)
    a = 0.0
    prev = n - 1
    for q in range(n):
        a += xs[prev] * zs[q] - xs[q] * zs[prev]
        prev = q
    return a * 0.5


def measure_obb(xs, zs):
    n = len(xs)
    best_len, ang = 0.0, 0.0
    for i in range(n):
        j = (i + 1) % n
        dx, dz = xs[j] - xs[i], zs[j] - zs[i]
        d = math.hypot(dx, dz)
        if d > best_len:
            best_len, ang = d, math.atan2(dz, dx)
    ca, sa = math.cos(-ang), math.sin(-ang)
    u0 = u1 = v0 = v1 = None
    for i in range(n):
        x, z = xs[i], zs[i]
        u = x * ca - z * sa
        v = x * sa + z * ca
        u0 = u if u0 is None or u < u0 else u0
        u1 = u if u1 is None or u > u1 else u1
        v0 = v if v0 is None or v < v0 else v0
        v1 = v if v1 is None or v > v1 else v1
    return ang, u1 - u0, v1 - v0


def panel_fit(xs, zs, panel_sections):
    n = len(xs)
    if n < 4 or n > 6 or not panel_sections:
        return None
    ang, length, dep = measure_obb(xs, zs)
    if dep < 8.5 or dep > 15.5:
        return None
    if length < 21 or length > 78:
        return None
    if abs(polygon_area(xs, zs)) < length * dep * 0.82:
        return None
    sections = max(2, min(6, round(length / 12.0)))
    if sections not in panel_sections:
        return None
    return ang, sections


def _best_fit(xs, zs, kit, lo_s, hi_s, aniso_max, err_fn):
    ang, length, dep = measure_obb(xs, zs)
    best = None
    best_err = float("inf")
    for ki, e in enumerate(kit):
        cx, cz = length / e["W"], dep / e["D"]
        if cx < lo_s or cx > hi_s or cz < lo_s or cz > hi_s:
            continue
        aniso = max(cx / cz, cz / cx)
        if aniso > aniso_max:
            continue
        err = err_fn(cx, cz, aniso, e)
        if err < best_err:
            best_err, best = err, (ki, cx, cz)
    return ang, length, dep, best


def house_fit(xs, zs, kit):
    n = len(xs)
    if n < 4 or n > 16 or not kit:
        return None
    ang, length, dep = measure_obb(xs, zs)
    if length < 5 or dep < 3.2 or length > 34 or dep > 18:
        return None
    if length / dep > 4.2:
        return None
    _, _, _, best = _best_fit(xs, zs, kit, 0.55, 1.95, 1.8,
                               lambda cx, cz, a, e: abs(1 - cx) + abs(1 - cz) + (a - 1) * 1.5)
    if not best:
        return None
    return ang, best[0]


def apt_fit(xs, zs, kit, h):
    n = len(xs)
    if n < 4 or n > 14 or not kit:
        return None
    ang, length, dep = measure_obb(xs, zs)
    if length < 8 or dep < 5.5 or length > 52 or dep > 30:
        return None
    if length / dep > 4.0:
        return None
    if abs(polygon_area(xs, zs)) < length * dep * 0.64:
        return None
    _, _, _, best = _best_fit(xs, zs, kit, 0.48, 2.2, 2.1,
                               lambda cx, cz, a, e: abs(e["H"] - h) / max(6, h) * 3.0 + abs(1 - cx) + abs(1 - cz) + (a - 1))
    if not best:
        return None
    return ang, best[0]


def shed_fit(xs, zs, kit, h):
    n = len(xs)
    if n < 4 or n > 14 or not kit:
        return None
    ang, length, dep = measure_obb(xs, zs)
    if length < 12 or dep < 6.5:
        return None
    if length / dep > 6.5:
        return None
    if abs(polygon_area(xs, zs)) < length * dep * 0.60:
        return None
    _, _, _, best = _best_fit(xs, zs, kit, 0.35, 3.6, 2.8,
                               lambda cx, cz, a, e: abs(math.log(cx)) + abs(math.log(cz)) + (a - 1))
    if not best:
        return None
    return ang, best[0]


def out_fit(xs, zs, kit):
    n = len(xs)
    if n < 4 or n > 12 or not kit:
        return None
    ang, length, dep = measure_obb(xs, zs)
    if length < 1.8 or dep < 1.4 or length > 18 or dep > 9:
        return None
    _, _, _, best = _best_fit(xs, zs, kit, 0.5, 2.2, 1e9,
                               lambda cx, cz, a, e: abs(math.log(cx)) + abs(math.log(cz)))
    if not best:
        return None
    return ang, best[0]


# ---- style chain (WallMaterials.Classify) ----
PAL_LEN = {  # just the counts — palette COLOUR values don't affect this probe
    "WarmWalls": 7, "HouseWalls": 6, "PanelWalls": 7, "GlassWalls": 4,
}


def classify(bi, h, n, area, district, cottage, old_town):
    r1 = hash_(bi + 1)
    r2 = hash_(bi + 77777)
    glass_min = 60.0 if district == "soviet" else 45.0

    style = 0
    red_roof = False
    style_pitch = False
    if h >= glass_min:
        style, red_roof = 2, False
    elif cottage:
        style, style_pitch = 0, (n <= 14 and r2 < 0.93)
    elif district == "historic" and area < 900 and h < 30:
        style, red_roof, style_pitch = 3, True, (n <= 10 and h < 15)
    elif district == "soviet" and h >= 10:
        style = 1 if r1 < 0.88 else 0
    elif district == "classic" and 8 <= h < 45:
        red = r2 < 0.4
        style, red_roof = (0 if r1 < 0.8 else 3), red
    elif h >= 13:
        style = 1 if r1 < 0.75 else 0
    elif old_town and area < 500:
        style, red_roof, style_pitch = 3, True, (n <= 10)
    elif area < 320 and r1 < 0.85:
        red = r2 < 0.7
        style, red_roof, style_pitch = 0, red, (red and n <= 10)
    else:
        style = 0
    return style, red_roof, style_pitch


def decide_early(bi, style, district, h, area, cottage, pitched_final):
    want_balconies = (style == 1 or (style == 0 and district == "soviet")) and h >= 10 and hash_(bi + 999) < 0.45
    want_wood = style == 3 and h >= 8 and hash_(bi + 999) < 0.4
    n_floors = min(16, int(math.floor(h / 3.2)))
    want_windows = (style in (0, 3)) and not want_balconies and 5 <= h < 51 and (n_floors >= 2 or (cottage and h >= 4))
    want_cornice = (style == 3 or (district == "classic" and style == 0)) and not pitched_final
    return want_balconies, want_wood, n_floors, want_windows, want_cornice


def resolve_wall_bucket(bi, style, district, want_windows, osm_material, wall_fams):
    bucket = style
    if want_windows:
        wv = hash_(bi + 61)
        if wv < 0.14:
            fam = "brick"
        elif style == 3 or district == "historic":
            fam = "plaster" if wv < 0.62 else "stone"
        elif district == "soviet":
            fam = "panel" if wv < 0.68 else "plaster"
        elif district == "classic":
            fam = "plaster" if wv < 0.66 else ("stone" if wv < 0.84 else "panel")
        else:
            fam = "panel" if wv < 0.5 else ("plaster" if wv < 0.86 else "stone")
        pool = wall_fams.get(fam) or wall_fams.get("plaster") or [4]
        bucket = pool[int(hash_(bi + 62) * len(pool)) % len(pool)]
    if osm_material and want_windows:
        fam = {"brick": "brick", "stone": "stone", "sandstone": "stone", "tuff": "stone",
               "concrete": "panel", "cement_block": "panel", "plaster": "plaster"}.get(osm_material)
        pool = wall_fams.get(fam) if fam else None
        if pool:
            bucket = pool[int(hash_(bi + 62) * len(pool)) % len(pool)]
    return bucket


def main():
    # ---- load data ----
    with gzip.open(os.path.join(SA, "city.json.gz")) as fh:
        city = json.load(fh)
    buildings = city["buildings"]

    levels = json.load(open(os.path.join(SA, "levels.json")))
    shapes = levels.get("shapes", [])
    materials = levels.get("materials", [])
    facts_grid = {}
    for it in levels.get("items", []):
        x, z = it[0] / 10.0, it[1] / 10.0
        gi, gj = round(x / 10.0), round(z / 10.0)
        facts_grid.setdefault((gi, gj), []).append(it)

    def facts_at(x, z):
        gi, gj = round(x / 10.0), round(z / 10.0)
        best, bd = None, 9.0
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                for it in facts_grid.get((gi + di, gj + dj), []):
                    d = math.hypot(it[0] / 10.0 - x, it[1] / 10.0 - z)
                    if d < bd:
                        bd, best = d, it
        return best

    parts_path = os.path.join(SA, "parts.json")
    parts_grid = {}
    n_parts_items = 0
    if os.path.exists(parts_path):
        pd = json.load(open(parts_path))
        for it in pd.get("items", []):
            nv = (len(it) - 2) // 2
            cx = sum(it[2 + i * 2] for i in range(nv)) / nv / 10.0
            cz = sum(it[3 + i * 2] for i in range(nv)) / nv / 10.0
            k = (round(cx / 60.0), round(cz / 60.0))
            parts_grid.setdefault(k, []).append((it, cx, cz))
            n_parts_items += 1

    def parts_in_bounds(cx, cz, minx, maxx, minz, maxz):
        gi, gj = round(cx / 60.0), round(cz / 60.0)
        mine = []
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                for it, qcx, qcz in parts_grid.get((gi + di, gj + dj), []):
                    if minx <= qcx <= maxx and minz <= qcz <= maxz:
                        mine.append(it)
        return mine

    def is_stepped(parts):
        if not parts or len(parts) <= 1:
            return False
        buckets = {round(it[0] / 50.0) for it in parts}
        return len(buckets) > 1

    kit_meta = json.load(open(os.path.join(SA, "kit_meta.json")))
    kit_out = kit_meta.get("out", [])
    kit_apt = kit_meta.get("apt", [])
    kit_shed = kit_meta.get("shed", [])
    kit_house = kit_meta.get("house", [])
    panel_sections = set(kit_meta.get("panel_sections", []))

    wall_names, wall_fams = ["brick_red", "brick_dark", "plaster_decay", "plaster_yellow", "plaster_pink", "panel_concrete"], \
        {"brick": [4, 5], "plaster": [6, 7, 8], "panel": [9], "stone": []}
    walls_json = os.path.join(ART, "walls", "walls.json")
    if os.path.exists(walls_json):
        wm = json.load(open(walls_json))
        names = list(wm.keys())
        fams = {"brick": [], "plaster": [], "panel": [], "stone": []}
        for i, name in enumerate(names):
            fam = wm[name].get("family") or ("panel" if name.startswith("panel") else "plaster")
            fams.setdefault(fam, []).append(4 + i)
        wall_names, wall_fams = names, fams

    # ---- expand parts + classify every footprint ----
    n_total = len(buildings)
    n_expanded_extra = 0
    claim_counts = {"panel": 0, "house": 0, "out": 0, "apt": 0, "shed": 0, "extruded": 0}
    material_bucket_counts = {}
    roof_shape_counts = {}
    n_facts = 0
    n_want_windows = 0
    n_want_balconies = 0
    n_want_wood = 0
    n_want_cornice = 0
    n_pitched = 0
    n_coloured = 0
    n_excluded = 0
    excluded_bids = set()

    for bi, b in enumerate(buildings):
        n = (len(b) - 1) // 2
        if n < 3:
            continue
        xs = [b[1 + i * 2] / 10.0 for i in range(n)]
        zs = [b[2 + i * 2] / 10.0 for i in range(n)]
        minx, maxx, minz, maxz = min(xs), max(xs), min(zs), max(zs)
        cx, cz = sum(xs) / n, sum(zs) / n

        mine = parts_in_bounds(cx, cz, minx, maxx, minz, maxz) if parts_grid else []
        if is_stepped(mine):
            entries = []
            for it in mine:
                nv = (len(it) - 2) // 2
                pxs = [it[2 + i * 2] / 10.0 for i in range(nv)]
                pzs = [it[3 + i * 2] / 10.0 for i in range(nv)]
                entries.append((pxs, pzs, it[0] / 10.0, it[1] / 10.0))
            n_expanded_extra += len(entries) - 1
        else:
            entries = [(xs, zs, b[0] / 10.0, 0.0)]

        for exs, ezs, height, off in entries:
            en = len(exs)
            ecx, ecz = sum(exs) / en, sum(ezs) / en
            # citymap.js:4601 -- a real landmark model stands here instead, so
            # the OSM footprint is skipped before facts/claims/relief. Note the
            # JS applies this INSIDE the `expanded` loop (per 3D part volume),
            # not on the raw footprint, which is why one excluded building can
            # remove several claims; BuildingBuilder.cs:523 matches.
            if in_landmark_exclusion(ecx, ecz):
                n_excluded += 1
                excluded_bids.add(bi)
                continue
            fact = facts_at(ecx, ecz)
            if fact:
                n_facts += 1
            h = height
            if fact and fact[2] > 0:
                h = fact[2] * 3.1 + 0.9
            area = abs(polygon_area(exs, ezs))
            district = district_at(ecx, ecz)
            ot = in_old_town(ecx, ecz, district)
            cottage = is_cottage(area, h, ot)

            claimed = None
            if panel_sections and district == "soviet" and 13 <= h <= 19.5:
                if panel_fit(exs, ezs, panel_sections):
                    claimed = "panel"
            if not claimed and kit_house and cottage:
                if house_fit(exs, ezs, kit_house):
                    claimed = "house"
            if not claimed and kit_out and area <= 46:
                if out_fit(exs, ezs, kit_out):
                    claimed = "out"
            if not claimed and kit_apt and not cottage and 8.5 <= h <= 34 and 120 <= area <= 1600:
                if apt_fit(exs, ezs, kit_apt, h):
                    claimed = "apt"
            if not claimed and kit_shed and area >= 260 and h <= 22 and not cottage:
                if shed_fit(exs, ezs, kit_shed, h):
                    claimed = "shed"

            if claimed:
                claim_counts[claimed] += 1
                continue
            claim_counts["extruded"] += 1

            style, red_roof, style_pitch = classify(bi, h, en, area, district, cottage, ot)
            pitched = style_pitch
            shape = None
            if fact and 0 <= fact[3] < len(shapes) and shapes[fact[3]]:
                shape = shapes[fact[3]]
                pitched = not (shape in ("flat", "skillion")) and en <= 16
            if pitched:
                n_pitched += 1
            if fact and len(fact) > 5 and isinstance(fact[5], str):
                n_coloured += 1
            if shape:
                roof_shape_counts[shape] = roof_shape_counts.get(shape, 0) + 1

            want_bal, want_wood, n_floors, want_win, want_corn = decide_early(bi, style, district, h, area, cottage, pitched)
            if want_bal:
                n_want_balconies += 1
            if want_wood:
                n_want_wood += 1
            if want_win:
                n_want_windows += 1
            if want_corn:
                n_want_cornice += 1

            osm_mat = materials[fact[4]] if fact and 0 <= fact[4] < len(materials) else None
            bucket = resolve_wall_bucket(bi, style, district, want_win, osm_mat, wall_fams)
            label = f"style{bucket}" if bucket < 4 else (wall_names[bucket - 4] if bucket - 4 < len(wall_names) else f"loggia{bucket}")
            material_bucket_counts[label] = material_bucket_counts.get(label, 0) + 1

    # ---- print KEY=value lines, one distinct id per line ----
    print(f"BQ id=totals buildings={n_total} withFacts={n_facts} pitched={n_pitched} coloured={n_coloured}")
    print(f"BQ id=parts expandedExtra={n_expanded_extra} partsItems={n_parts_items}")
    # not compared against the C# (BuildRich surfaces no such counter) -- printed
    # on its OWN id so it cannot change another line's shape, which is the
    # documented way an extra field makes a probe compare nothing.
    print(f"BQ id=exclusions landmarkRings={len(EXCLUSIONS)} skippedEntries={n_excluded} skippedFootprints={len(excluded_bids)}")
    print(f"BQ id=claims panel={claim_counts['panel']} house={claim_counts['house']} out={claim_counts['out']} "
          f"apt={claim_counts['apt']} shed={claim_counts['shed']} extruded={claim_counts['extruded']}")
    print(f"BQ id=relief wantWindows={n_want_windows} wantBalconies={n_want_balconies} "
          f"wantWoodBalc={n_want_wood} wantCornice={n_want_cornice}")
    for shape, c in sorted(roof_shape_counts.items()):
        print(f"BQ id=roofshape_{shape} count={c}")
    for label, c in sorted(material_bucket_counts.items()):
        print(f"BQ id=wallbucket_{label} count={c}")


if __name__ == "__main__":
    main()
