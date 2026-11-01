#!/usr/bin/env python3
"""Ground truth for BuildingBuilder + BuildingFacts, straight from the data files.

Mirrors src/citymap.js: the 10 m facts grid with a 9 m match radius
(citymap.js:4517), real storeys beating the raster height via
h = levels * 3.1 + 0.9 (citymap.js:4606), and the roof:shape pitch decision
(citymap.js:4820). Compare with unity/Assets/Editor/Probes/BuildingProbe.cs.
"""
import gzip, json, math, re

city = json.load(gzip.open("public/tbilisi/city.json.gz"))
lv = json.load(open("public/tbilisi/levels.json"))
SHAPES, MATERIALS, items = lv["shapes"], lv["materials"], lv["items"]

CELL, RADIUS = 10.0, 9.0
grid = {}
for it in items:
    x, z = it[0] / 10.0, it[1] / 10.0
    key = (round(x / CELL), round(z / CELL))
    grid.setdefault(key, []).append(it)

HEX = re.compile(r"^#[0-9a-f]{6}$", re.I)

def facts_at(x, z):
    gi, gj = round(x / CELL), round(z / CELL)
    best, bd = None, RADIUS
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for it in grid.get((gi + di, gj + dj), ()):
                d = math.hypot(it[0] / 10.0 - x, it[1] / 10.0 - z)
                if d < bd:
                    bd, best = d, it
    return best

n = with_facts = real_height = pitched = coloured = 0
shape_hist, mat_hist = {}, {}
for b in city["buildings"]:
    vc = (len(b) - 1) // 2
    if vc < 3:
        continue
    n += 1
    cx = sum(b[1 + i * 2] for i in range(vc)) / vc / 10.0
    cz = sum(b[2 + i * 2] for i in range(vc)) / vc / 10.0
    f = facts_at(cx, cz)
    if not f:
        continue
    with_facts += 1
    if f[2] > 0:
        real_height += 1
    shape = SHAPES[f[3]] if 0 <= f[3] < len(SHAPES) else ""
    if shape:
        shape_hist[shape] = shape_hist.get(shape, 0) + 1
        if shape not in ("flat", "skillion") and vc <= 16:
            pitched += 1
    mat = MATERIALS[f[4]] if 0 <= f[4] < len(MATERIALS) else ""
    if mat:
        mat_hist[mat] = mat_hist.get(mat, 0) + 1
    if len(f) > 5 and isinstance(f[5], str) and HEX.match(f[5]):
        coloured += 1

print(f"BLD buildings={n}")
print(f"BLD facts_items={len(items)} shapes={len(SHAPES)} materials={len(MATERIALS)}")
print(f"BLD withFacts={with_facts} realHeight={real_height} pitched={pitched} coloured={coloured}")
print("BLD roof_shapes=" + ",".join(f"{k}:{v}" for k, v in sorted(shape_hist.items(), key=lambda kv: -kv[1])))
print("BLD materials=" + ",".join(f"{k}:{v}" for k, v in sorted(mat_hist.items(), key=lambda kv: -kv[1])))
