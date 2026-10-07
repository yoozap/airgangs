#!/usr/bin/env python3
"""Real street addresses for every building — from the cached Geofabrik PBF.

Mirrors the building filters of build_map.extract_from_pbf so the output rows
line up with the same physical buildings, but matches at RUNTIME by centroid
(so exact ordering never matters). Label priority:
  1. addr:street + addr:housenumber  ("რუსთაველის გამზირი 12")
  2. building name                    ("ოპერის თეატრი")
  3. nearest named street within 150 m ("ვაჟა-ფშაველას გამზირი")

Output: public/tbilisi/addresses.json.gz
  { "streets": [...unique labels...], "items": [[x_dm, z_dm, streetIdx, "num"], ...] }
x/z in decimeters, same local frame as city.json (see build_map.to_local).
"""
import gzip
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_map import (  # noqa: E402
    CACHE, CITY_BOXES, ROAD_W, in_box, pack_ring, ring_area_m2, to_local,
)

import osmium  # noqa: E402

PBF = os.path.join(CACHE, "georgia-latest.osm.pbf")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "public", "tbilisi", "addresses.json.gz")

city_boxes = list(CITY_BOXES.values())


def latlon_ring(node_iter):
    ring = []
    for nd in node_iter:
        try:
            ring.append((nd.lat, nd.lon))
        except Exception:  # noqa: BLE001
            continue
    return ring


def any_in(ring, box):
    return any(in_box(lat, lon, box) for lat, lon in ring)


class Handler(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.blds = []       # (cx_dm, cz_dm, street, num, name)
        self.road_pts = []   # (x_m, z_m, name) — sampled points of named streets

    def area(self, a):
        t = a.tags
        if "building" not in t or t["building"] == "no":
            return
        if t["building"] == "roof" or t.get("man_made") == "bridge":
            return
        if t.get("location") == "underground":
            return
        street = t.get("addr:street", "")
        num = t.get("addr:housenumber", "")
        name = t.get("name", "")
        for outer in a.outer_rings():
            ring = latlon_ring(outer)
            if len(ring) < 4:
                continue
            if not any(any_in(ring, box) for box in city_boxes):
                continue
            flat = pack_ring(ring)
            if len(flat) < 6 or ring_area_m2(flat) < 6:
                continue
            n2 = len(flat) // 2
            cx = sum(flat[0::2]) / n2
            cz = sum(flat[1::2]) / n2
            self.blds.append((round(cx), round(cz), street, num, name))

    def way(self, wy):
        t = wy.tags
        if t.get("highway") not in ROAD_W:
            return
        name = t.get("name") or t.get("name:ka") or ""
        if not name:
            return
        ring = latlon_ring(wy.nodes)
        if len(ring) < 2 or not any(any_in(ring, box) for box in city_boxes):
            return
        # sample points every ~40 m along the way so nearest-lookup has coverage
        prev = None
        for lat, lon in ring:
            x, z = to_local(lat, lon)
            if prev is not None:
                d = math.hypot(x - prev[0], z - prev[1])
                steps = int(d // 40)
                for s in range(1, steps + 1):
                    tt = s / (steps + 1)
                    self.road_pts.append((prev[0] + (x - prev[0]) * tt, prev[1] + (z - prev[1]) * tt, name))
            self.road_pts.append((x, z, name))
            prev = (x, z)


def main():
    if not os.path.exists(PBF):
        sys.exit(f"missing {PBF} — run build_map.py first")
    h = Handler()
    print("parsing PBF (a few minutes)…", flush=True)
    h.apply_file(PBF, locations=True, idx="flex_mem")
    print(f"{len(h.blds)} buildings, {len(h.road_pts)} named-road points", flush=True)

    # grid index over road points for the nearest-street fallback
    CELL = 80.0
    grid = {}
    for x, z, name in h.road_pts:
        grid.setdefault((int(x // CELL), int(z // CELL)), []).append((x, z, name))

    def nearest_street(x, z, max_d=150.0):
        gi, gj = int(x // CELL), int(z // CELL)
        best, best_d2 = "", max_d * max_d
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                for px, pz, name in grid.get((gi + di, gj + dj), ()):
                    d2 = (px - x) ** 2 + (pz - z) ** 2
                    if d2 < best_d2:
                        best_d2, best = d2, name
        return best

    streets = {}
    items = []
    n_addr = n_name = n_road = 0
    for cx, cz, street, num, name in h.blds:
        if street:
            label, keep_num = street, num
            n_addr += 1
        elif name:
            label, keep_num = name, ""
            n_name += 1
        else:
            label = nearest_street(cx / 10.0, cz / 10.0)
            keep_num = ""
            if not label:
                continue
            n_road += 1
        si = streets.setdefault(label, len(streets))
        items.append([cx, cz, si, keep_num])

    out = {"streets": [s for s, _ in sorted(streets.items(), key=lambda kv: kv[1])], "items": items}
    raw = json.dumps(out, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    with gzip.open(OUT, "wb") as f:
        f.write(raw)
    print(f"{len(items)} labeled ({n_addr} real addr, {n_name} named, {n_road} nearest-street), "
          f"{len(streets)} unique streets, {len(raw) / 1e6:.1f} MB raw → {os.path.getsize(OUT) / 1e6:.1f} MB gz")


if __name__ == "__main__":
    main()
