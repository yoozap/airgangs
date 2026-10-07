#!/usr/bin/env python3
"""Extract branded/business POIs from the cached Georgia PBF -> pois.json.

Groups real businesses (banks, restaurants, cafes, fuel, pharmacies, hotels,
shops, fast food) by category with local X/Z coordinates, so the game can put a
category-appropriate building model at each real location.
"""
import json
import math
import os

import osmium

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PBF = os.path.join(ROOT, "tools", "cache", "georgia-latest.osm.pbf")
OUT = os.path.join(ROOT, "public", "tbilisi", "pois.json")

lat0, lon0 = 41.7252, 44.7458
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)

# City bounding boxes (match build_map.CITY_BOXES) so we keep only in-city POIs.
CITY_BOXES = [
    (41.62, 44.68, 41.84, 44.98),   # tbilisi
    (41.57, 41.56, 41.70, 41.69),   # batumi
    (42.21, 42.62, 42.33, 42.78),   # kutaisi
    (41.95, 44.06, 42.02, 44.16),   # gori
    (41.51, 44.94, 41.60, 45.06),   # rustavi
    (42.63, 44.58, 42.68, 44.66),   # kazbegi
]

buckets = {k: [] for k in ("mcdonalds", "bank", "restaurant", "cafe", "fuel", "pharmacy", "hotel", "shop")}


def categorize(t):
    name = (t.get("brand", "") + " " + t.get("name", "") + " " + t.get("name:en", "")).lower()
    if "mcdonald" in name or "მაკდონალდ" in name:
        return "mcdonalds"
    a = t.get("amenity")
    if a == "bank":
        return "bank"
    if a in ("restaurant",):
        return "restaurant"
    if a in ("cafe", "fast_food"):
        return "cafe"
    if a == "fuel":
        return "fuel"
    if a == "pharmacy":
        return "pharmacy"
    if t.get("tourism") == "hotel":
        return "hotel"
    if t.get("shop") in ("supermarket", "mall", "department_store", "convenience"):
        return "shop"
    return None


def in_city(lat, lon):
    return any(s <= lat <= n and w <= lon <= e for (s, w, n, e) in CITY_BOXES)


def add(lat, lon, cat):
    if not in_city(lat, lon):
        return
    x, z = -(lon - lon0) * M_LON, (lat - lat0) * M_LAT
    buckets[cat].append((round(x, 1), round(z, 1)))


class H(osmium.SimpleHandler):
    def node(self, n):
        cat = categorize(n.tags)
        if cat:
            add(n.location.lat, n.location.lon, cat)

    def way(self, w):
        cat = categorize(w.tags)
        if cat and len(w.nodes) > 0:
            try:
                lats = [nd.lat for nd in w.nodes]
                lons = [nd.lon for nd in w.nodes]
                add(sum(lats) / len(lats), sum(lons) / len(lons), cat)
            except Exception:  # noqa: BLE001
                pass


H().apply_file(PBF, locations=True, idx="flex_mem")

# de-dup within each category (node + way of the same place)
out = {}
for cat, pts in buckets.items():
    uniq = []
    for x, z in pts:
        if all((x - u[0]) ** 2 + (z - u[1]) ** 2 > 60 * 60 for u in uniq):
            uniq.append((x, z))
    out[cat] = uniq

json.dump(out, open(OUT, "w"))
print("POIs ->", OUT)
for cat, pts in out.items():
    print(f"  {cat}: {len(pts)}")
