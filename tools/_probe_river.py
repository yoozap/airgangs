#!/usr/bin/env python3
"""Ground truth for the river/water/grass/worship Unity port, computed straight
from river.json and city.json.gz using the same algorithms
unity/Assets/Scripts/City/{RiverSheet,WaterBodies,GrassCover,WorshipPlacer}.cs
port from src/citymap.js. Compare with RiverProbe.cs
(tools/unity_port_check.py tools/_probe_river.out <unity log>.txt).

This is a REIMPLEMENTATION in Python, not a call into the C# — the point is
two independent readings of the same source data landing on the same numbers.
The ear-clip-with-holes triangulator here was prototyped against the real
river.json data before the C# was written (the biggest ring, 2416 outer +
1969-vertex hole merged, triangulates in well under a second with the
reflex-vertex-only optimisation — tested by hand during development; the
pure-Python version below is the same algorithm, just unrolled from that
prototype).
"""
import base64
import gzip
import json
import math
import struct

ROOT = "."


def load_city():
    d = json.load(gzip.open(f"{ROOT}/public/tbilisi/city.json.gz"))
    t = d["terrain"]
    raw = base64.b64decode(t["data"])
    full = struct.unpack("<%dH" % (len(raw) // 2), raw)
    cls_raw = base64.b64decode(t["classes"]) if t.get("classes") else None
    W, H, STEP = t["w"], t["h"], t["step"]
    w2, h2 = (W - 1) // 2 + 1, (H - 1) // 2 + 1
    step2 = STEP * 2
    heights = [0.0] * (w2 * h2)
    classes = [0] * (w2 * h2)
    for r in range(h2):
        fr = min(r * 2, H - 1)
        for c in range(w2):
            fc = min(c * 2, W - 1)
            heights[r * w2 + c] = full[fr * W + fc] / 10
            if cls_raw:
                classes[r * w2 + c] = cls_raw[fr * W + fc]
    grid = dict(w=w2, h=h2, step=step2, minx=t["minx"], minz=t["minz"], heights=heights, classes=classes)
    carve_water(grid)   # src/citymap.js:3068 — the LAST line of _buildTerrainSampler
    return d, grid


def carve_water(g):
    """src/citymap.js:3073-3115 `_carveWater`.

    This reference used to stop at the half-res resample and hand back the RAW
    grid, but citymap.js calls _carveWater as the last line of
    _buildTerrainSampler (line 3068) -- before river.json is even fetched
    (:586) and long before _buildRiverSheet (:880). So every height the river
    sheet, the bank walk and the lake/river classification read in the real
    game is a CARVED one, and a reference that skips it compares the port
    against a grid the JS never has. Measured on the real data: 830 of the
    1,104 river-mask vertices with a measured level stood ABOVE the water
    surface without this (mean +2.64 m, worst +29.20 m); with it, 95 do.
    """
    w, h = g["w"], g["h"]
    heights, classes = g["heights"], g["classes"]
    for p in range(2):
        src = list(heights)
        drop = 1.2 if p == 0 else 0.2
        for r in range(1, h - 1):
            for c in range(1, w - 1):
                i = r * w + c
                if classes[i] != 2:
                    continue
                nb = (src[i - 1] + src[i + 1] + src[i - w] + src[i + w]) / 4
                heights[i] = min(src[i], nb) - drop
    src = list(heights)
    for r in range(h):
        for c in range(w):
            i = r * w + c
            if classes[i] != 2:
                continue
            m = src[i]
            for dr in range(-2, 3):
                rr = r + dr
                if rr < 0 or rr >= h:
                    continue
                for dc in range(-2, 3):
                    cc = c + dc
                    if cc < 0 or cc >= w:
                        continue
                    v = src[rr * w + cc]
                    if v < m:
                        m = v
            heights[i] = m - 1.6


def clamp(v, a, b):
    return a if v < a else (b if v > b else v)


def sample_grid(g, x, z):
    fx = clamp((x - g["minx"]) / g["step"], 0, g["w"] - 1.001)
    fz = clamp((z - g["minz"]) / g["step"], 0, g["h"] - 1.001)
    c, r = int(fx), int(fz)
    dx, dz = fx - c, fz - r
    i = r * g["w"] + c
    h00, h10 = g["heights"][i], g["heights"][i + 1]
    h01, h11 = g["heights"][i + g["w"]], g["heights"][i + g["w"] + 1]
    if dx + dz <= 1:
        return h00 + dx * (h10 - h00) + dz * (h01 - h00)
    return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11)


city, grid = load_city()
river = json.load(open(f"{ROOT}/public/tbilisi/river.json"))
centre = [c for c in river.get("centre", []) if c[2] is not None]

# ---- carve mask + measured level — river.json:591-677 / RiverSheet.BuildCarveMask ----
CELL = 400
hash_ = {}
for i, c in enumerate(centre):
    cx, cz = math.floor(c[0] / CELL), math.floor(c[1] / CELL)
    for ox in (-1, 0, 1):
        for oz in (-1, 0, 1):
            hash_.setdefault((cx + ox, cz + oz), []).append(i)

MCELL = 12
mask = set()
cellsRC = []


def paint(ring):
    minz = min(p[1] for p in ring)
    maxz = max(p[1] for p in ring)
    r0, r1 = math.floor(minz / MCELL), math.floor(maxz / MCELL)
    n = len(ring)
    for r in range(r0, r1 + 1):
        zc = r * MCELL + MCELL / 2
        xs = []
        for i in range(n):
            a, b = ring[i], ring[(i + 1) % n]
            if (a[1] > zc) == (b[1] > zc):
                continue
            xs.append(a[0] + (b[0] - a[0]) * (zc - a[1]) / (b[1] - a[1]))
        xs.sort()
        for k in range(0, len(xs) - 1, 2):
            c0, c1 = math.floor(xs[k] / MCELL), math.floor(xs[k + 1] / MCELL)
            for c in range(c0, c1 + 1):
                kk = (c, r)
                if kk not in mask:
                    mask.add(kk)
                    cellsRC.append((c, r))


for poly in river.get("polys", []):
    if poly.get("kind") != "river":
        continue
    outer = poly.get("outer")
    if outer and len(outer) > 3:
        paint(outer)


def nearest_level(x, z):
    b = hash_.get((math.floor(x / CELL), math.floor(z / CELL)))
    if not b:
        return None
    bd, v = float("inf"), None
    for i in b:
        for j in (i - 1, i):
            if j < 0 or j + 1 >= len(centre):
                continue
            a, c = centre[j], centre[j + 1]
            ex, ez = c[0] - a[0], c[1] - a[1]
            l2 = ex * ex + ez * ez
            if l2 < 1e-6:
                continue
            t = ((x - a[0]) * ex + (z - a[1]) * ez) / l2
            t = 0 if t < 0 else (1 if t > 1 else t)
            d2 = (x - (a[0] + ex * t)) ** 2 + (z - (a[1] + ez * t)) ** 2
            if d2 < bd:
                bd, v = d2, a[2] + (c[2] - a[2]) * t
    return v


levelOf = {}
for (c, r) in cellsRC:
    lv = nearest_level(c * MCELL + MCELL / 2, r * MCELL + MCELL / 2)
    if lv is not None:
        levelOf[(c, r)] = lv

RIVER_DEPTH = 3.5


def height_at(x, z):
    y = sample_grid(grid, x, z)
    lv = levelOf.get((math.floor(x / MCELL), math.floor(z / MCELL)))
    if lv is not None:
        bed = lv - RIVER_DEPTH
        if bed < y:
            return bed
    return y


def measured_level_at(x, z):
    lvl = nearest_level(x, z)
    if lvl is None:
        return None
    cx, cz = math.floor(x / MCELL), math.floor(z / MCELL)
    for ox in (-1, 0, 1):
        for oz in (-1, 0, 1):
            if (cx + ox, cz + oz) in mask:
                return lvl
    return None


def level_at(x, z):
    m = measured_level_at(x, z)
    if m is not None:
        return m
    samples = [height_at(x, z)]
    for rr in (35, 75):
        for k in range(6):
            a = (k / 6) * math.pi * 2
            samples.append(height_at(x + math.cos(a) * rr, z + math.sin(a) * rr))
    samples.sort()
    return samples[int(len(samples) * 0.35)] + 0.7


print(f"RIVERP stations n={len(centre)} first={centre[0][2]:.3f} last={centre[-1][2]:.3f}")
print(f"RIVERP carve cells={len(levelOf)}")

# 5 evenly-spaced sample cells, sorted by key so the choice is deterministic
# across languages (never rely on hash/dict iteration order for this).
keys = sorted(levelOf.keys())
n = len(keys)
for idx, tag in zip([0, n // 4, n // 2, (3 * n) // 4, n - 1], ["cell0", "cell1", "cell2", "cell3", "cell4"]):
    c, r = keys[idx]
    x, z = c * MCELL + MCELL / 2, r * MCELL + MCELL / 2
    raw = sample_grid(grid, x, z)
    carved = height_at(x, z)
    lv = levelOf[(c, r)]
    print(f"RIVERP bed {tag} x={x:.1f} z={z:.1f} raw={raw:.3f} carved={carved:.3f} level={lv:.3f} bed={lv - RIVER_DEPTH:.3f}")

# ---- sheet rings — citymap.js:7373-7614 / RiverSheet.BuildSheetAndBanks ----


def signed_area(poly):
    a = 0.0
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        a += x1 * y2 - x2 * y1
    return a / 2


def cross(o, a, b):
    return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])


def point_in_tri(p, a, b, c):
    d1, d2, d3 = cross(p, a, b), cross(p, b, c), cross(p, c, a)
    hn = d1 < 0 or d2 < 0 or d3 < 0
    hp = d1 > 0 or d2 > 0 or d3 > 0
    return not (hn and hp)


def earclip_reflex(poly):
    n = len(poly)
    if n < 3:
        return []
    area = signed_area(poly)
    ccw = area > 0
    nxt = [(k + 1) % n for k in range(n)]
    prv = [(k - 1) % n for k in range(n)]

    def is_reflex(i):
        cr = cross(poly[prv[i]], poly[i], poly[nxt[i]])
        return (cr <= 0) if ccw else (cr >= 0)

    reflex = set(i for i in range(n) if is_reflex(i))
    tris = []
    remaining, cur, guard, stall = n, 0, 0, 0
    while remaining > 3 and guard < 400000:
        guard += 1
        i0, i1, i2 = prv[cur], cur, nxt[cur]
        if i0 == i2:
            break
        a, b, c = poly[i0], poly[i1], poly[i2]
        cr = cross(a, b, c)
        is_ear = (ccw and cr > 0) or (not ccw and cr < 0)
        if is_ear:
            for r in reflex:
                if r in (i0, i1, i2):
                    continue
                if point_in_tri(poly[r], a, b, c):
                    is_ear = False
                    break
        if is_ear:
            tris.append((i0, i1, i2))
            nxt[i0], prv[i2] = i2, i0
            reflex.discard(i1)
            remaining -= 1
            for v in (i0, i2):
                cr2 = cross(poly[prv[v]], poly[v], poly[nxt[v]])
                r2 = (cr2 <= 0) if ccw else (cr2 >= 0)
                if r2:
                    reflex.add(v)
                else:
                    reflex.discard(v)
            cur, stall = i2, 0
        else:
            cur = nxt[cur]
            stall += 1
            if stall > remaining + 2:
                break
    if remaining == 3:
        tris.append((cur, nxt[cur], nxt[nxt[cur]]))
    return tris


def bridge_hole(outer, hole):
    hi = max(range(len(hole)), key=lambda i: hole[i][0])
    hv = hole[hi]
    best_x, best_edge = None, None
    n = len(outer)
    for i in range(n):
        a, b = outer[i], outer[(i + 1) % n]
        if (a[1] > hv[1]) == (b[1] > hv[1]):
            continue
        t = (hv[1] - a[1]) / (b[1] - a[1])
        ix = a[0] + t * (b[0] - a[0])
        if ix < hv[0] - 1e-4:
            continue
        if best_x is None or ix < best_x:
            best_x, best_edge = ix, (i, (i + 1) % n, a, b)
    if best_edge is None:
        return None
    i, j, a, b = best_edge
    bidx = j if b[0] > a[0] else i
    hole_loop = hole[hi:] + hole[:hi]
    return outer[: bidx + 1] + hole_loop + [hv, outer[bidx]] + outer[bidx + 1 :]


def merge_holes(outer, holes):
    ring = list(outer)
    for h in sorted(holes, key=lambda h: -max(p[0] for p in h)):
        merged = bridge_hole(ring, h)
        if merged is not None:
            ring = merged
    return ring


rings = []


def add_ring(pts):
    area = signed_area(pts)
    if abs(area) < 400:
        return
    n = len(pts)
    step = max(1, n >> 5)
    banks = sorted(height_at(pts[k][0], pts[k][1]) for k in range(0, n, step))
    minx, maxx = min(p[0] for p in pts), max(p[0] for p in pts)
    minz, maxz = min(p[1] for p in pts), max(p[1] for p in pts)
    rings.append(dict(pts=pts, area=area, bbox=(minx, minz, maxx, maxz), spread=banks[-1] - banks[0]))


for poly in river["polys"]:
    outer = poly["outer"]
    oarea = signed_area(outer)
    add_ring([tuple(p) for p in (outer if oarea > 0 else list(reversed(outer)))])
    for h in poly.get("holes", []):
        if len(h) < 4:
            continue
        harea = signed_area(h)
        add_ring([tuple(p) for p in (h if harea < 0 else list(reversed(h)))])

outers = [r for r in rings if r["area"] > 0]
holes_ = [r for r in rings if r["area"] <= 0]


def bbox_contains(o, h):
    return o[0] <= h[0] and o[1] <= h[1] and h[2] <= o[2] and h[3] <= o[3]


def is_lake(o):
    return o["spread"] <= 4 and (o["bbox"][2] - o["bbox"][0]) < 900 and (o["bbox"][3] - o["bbox"][1]) < 900


pos, idxs = [], []
outers_used = 0
for oi, o in enumerate(outers):
    if is_lake(o):
        continue
    my_holes = [h["pts"] for h in holes_ if bbox_contains(o["bbox"], h["bbox"])]
    merged = merge_holes(o["pts"], my_holes)
    tris = earclip_reflex(merged)
    if not tris:
        continue
    outers_used += 1
    verts = list(merged)
    faces = [list(t) for t in tris]
    # diagnostic: per-ring counts BEFORE and AFTER the 2 subdivision passes,
    # to localise a count mismatch to one ring instead of guessing from totals
    print(f"RIVERP ringdbg i={oi} nholes={len(my_holes)} merged={len(merged)} pretris={len(tris)}")
    for _pass in range(2):
        mids = {}

        def mid_of(a, b):
            key = (a, b) if a < b else (b, a)
            if key in mids:
                return mids[key]
            m = len(verts)
            verts.append(((verts[a][0] + verts[b][0]) / 2, (verts[a][1] + verts[b][1]) / 2))
            mids[key] = m
            return m

        nxt = []
        for (a, b, c) in faces:
            ab, bc, ca = mid_of(a, b), mid_of(b, c), mid_of(c, a)
            nxt += [[a, ab, ca], [ab, b, bc], [ca, bc, c], [ab, bc, ca]]
        faces = nxt
    print(f"RIVERP ringdbg i={oi} fverts={len(verts)} ftris={len(faces)}")
    v0 = len(pos)
    for (x, z) in verts:
        pos.append((x, level_at(x, z), z))
    for f in faces:
        idxs.append((v0 + f[0], v0 + f[1], v0 + f[2]))


def tri_area(p0, p1, p2):
    return abs((p1[0] - p0[0]) * (p2[2] - p0[2]) - (p2[0] - p0[0]) * (p1[2] - p0[2])) / 2


total_area = sum(tri_area(pos[a], pos[b], pos[c]) for (a, b, c) in idxs)
print(f"RIVERP sheet verts={len(pos)} tris={len(idxs)} area_m2={total_area:.0f}")
print(f"RIVERP rings outers_total={len(outers)} outers_used={outers_used} holes_total={len(holes_)}")

# ---- water bodies — citymap.js:7255-7365 / WaterBodies.Build ----
lake_count, water_verts, water_tris = 0, 0, 0
for flat in city["water"]:
    n = len(flat) // 2
    if n < 3 or n > 20000:
        continue
    contour = [(flat[i * 2] / 10, flat[i * 2 + 1] / 10) for i in range(n)]
    area = signed_area(contour)
    area_abs = abs(area)
    if area_abs < 5000:
        continue
    per = sum(math.hypot(contour[(i + 1) % n][0] - contour[i][0], contour[(i + 1) % n][1] - contour[i][1]) for i in range(n))
    if per > 0 and (4 * math.pi * area_abs) / (per * per) < 0.22:
        continue
    banks = sorted(sample_grid(grid, p[0], p[1]) for p in contour)
    if banks[-1] - banks[0] > 5:
        continue
    tris = earclip_reflex(contour)
    if not tris:
        continue
    lake_count += 1
    water_verts += n
    water_tris += len(tris)
print(f"WATERP lakes n={lake_count} verts={water_verts} tris={water_tris}")

# ---- grass — citymap.js:8266-8420 / GrassCover.Build ----
w, h = grid["w"], grid["h"]
grass_cells = rock_cells = refined_cells = instances = 0
chunk_keys = set()
CHUNKM = 1000


def in_river_mask(x, z):
    return (math.floor(x / MCELL), math.floor(z / MCELL)) in mask


for r in range(h - 1):
    for c in range(w - 1):
        i = r * w + c
        cls = grid["classes"][i]
        if cls == 2:
            continue
        x0 = grid["minx"] + c * grid["step"]
        z0 = grid["minz"] + r * grid["step"]
        a = in_river_mask(x0, z0)
        refine = not (
            a == in_river_mask(x0 + grid["step"], z0)
            and a == in_river_mask(x0, z0 + grid["step"])
            and a == in_river_mask(x0 + grid["step"], z0 + grid["step"])
        )
        if refine:
            refined_cells += 1
            instances += 12 * 12
            chunk_keys.add((math.floor(x0 / CHUNKM), math.floor(z0 / CHUNKM), True))
            continue
        y00, y10 = grid["heights"][i], grid["heights"][i + 1]
        y01, y11 = grid["heights"][i + w], grid["heights"][i + w + 1]
        relief = max(y00, y10, y01, y11) - min(y00, y10, y01, y11)
        avgY = (y00 + y10 + y01 + y11) / 4
        steep = relief > grid["step"] * 1.1 and avgY > 620
        is_grass = not steep
        if is_grass:
            grass_cells += 1
        else:
            rock_cells += 1
        instances += 1
        chunk_keys.add((math.floor(x0 / CHUNKM), math.floor(z0 / CHUNKM), is_grass))

print(f"GRASSP chunks n={len(chunk_keys)} grassCells={grass_cells} rockCells={rock_cells} "
      f"refinedCells={refined_cells} instances={instances}")

# ---- worship — citymap.js:935-1113 / WorshipPlacer.Build ----
churches, mosques = city["churches"], city["mosques"]
print(f"WORSHIPP counts churches={len(churches)} mosques={len(mosques)}")
for tag, idx in [("church0", 0), ("church1", 100), ("church2", 300)]:
    xd, zd, area = churches[idx]
    x, z = xd / 10, zd / 10
    hgt = sample_grid(grid, x, z)
    print(f"WORSHIPP sample {tag} x={x:.1f} z={z:.1f} h={hgt:.2f}")
for tag, idx in [("mosque0", 0), ("mosque1", 5)]:
    xd, zd, area = mosques[idx]
    x, z = xd / 10, zd / 10
    hgt = sample_grid(grid, x, z)
    print(f"WORSHIPP sample {tag} x={x:.1f} z={z:.1f} h={hgt:.2f}")
