#!/usr/bin/env python3
"""Extract ALL named business + civic POIs from the cached Georgia PBF.

Unlike pois.json (coords only), pois2.json carries the REAL name of every
place so the game can paint actual fascia signs: shops, cafes, restaurants,
pharmacies, banks, hotels, fuel — plus civic: schools, kindergartens,
universities, hospitals, clinics, police, fire stations, malls.

Output: public/tbilisi/pois2.json
  {names: [string table], pois: {cat: [[x_dm, z_dm, nameIdx, area_m2], ...]}}
  nameIdx -1 = unnamed; area_m2 0 for nodes, footprint area for ways.
"""
import json
import math
import os

import osmium

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PBF = os.path.join(ROOT, "tools", "cache", "georgia-latest.osm.pbf")
OUT = os.path.join(ROOT, "public", "tbilisi", "pois2.json")

lat0, lon0 = 41.7252, 44.7458
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)

BBOX = (41.62, 44.68, 41.84, 44.98)  # Tbilisi only — signs are a city feature

SHOP_CATS = {
    "supermarket": "supermarket", "convenience": "minimarket",
    "mall": "mall", "department_store": "mall",
    "bakery": "bakery", "butcher": "butcher", "greengrocer": "grocer",
    "clothes": "clothes", "shoes": "clothes", "boutique": "clothes",
    "hairdresser": "beauty", "beauty": "beauty", "cosmetics": "beauty",
    "electronics": "electronics", "mobile_phone": "electronics",
    "computer": "electronics", "hardware": "hardware", "doityourself": "hardware",
    "furniture": "furniture", "florist": "florist", "books": "books",
    "jewelry": "jewelry", "alcohol": "alcohol", "wine": "alcohol",
    "car_repair": "auto", "car_parts": "auto", "car": "auto",
    "kiosk": "kiosk", "pet": "pet", "toys": "toys", "sports": "sports",
    "optician": "optician", "gift": "gift", "stationery": "books",
}
AMENITY_CATS = {
    "cafe": "cafe", "restaurant": "restaurant", "fast_food": "fastfood",
    "bar": "bar", "pub": "bar", "bank": "bank", "pharmacy": "pharmacy",
    "fuel": "fuel", "school": "school", "kindergarten": "kindergarten",
    "university": "university", "college": "university",
    "hospital": "hospital", "clinic": "clinic", "dentist": "clinic",
    "police": "police", "fire_station": "fire_station",
    "post_office": "post", "marketplace": "market",
    "cinema": "cinema", "theatre": "theatre", "library": "library",
    "veterinary": "vet",
}


def categorize(t):
    nm = (t.get("brand", "") + " " + t.get("name", "") + " " + t.get("name:en", "")).lower()
    if "mcdonald" in nm or "მაკდონალდ" in nm:
        return "mcdonalds"
    a = t.get("amenity")
    if a in AMENITY_CATS:
        return AMENITY_CATS[a]
    s = t.get("shop")
    if s in SHOP_CATS:
        return SHOP_CATS[s]
    if s:
        return "shop"  # any other shop=* value still gets a sign
    if t.get("tourism") == "hotel":
        return "hotel"
    if t.get("healthcare") == "hospital":
        return "hospital"
    return None


def best_name(t):
    # Georgian name first (game UI is Georgian), then brand, then any name
    for k in ("name:ka", "name", "brand:ka", "brand", "name:en"):
        v = t.get(k)
        if v:
            return v.strip()
    return None


names = []
name_idx = {}
buckets = {}


def add(lat, lon, cat, name, area):
    if not (BBOX[0] <= lat <= BBOX[2] and BBOX[1] <= lon <= BBOX[3]):
        return
    x = -(lon - lon0) * M_LON
    z = (lat - lat0) * M_LAT
    ni = -1
    if name:
        if name not in name_idx:
            name_idx[name] = len(names)
            names.append(name)
        ni = name_idx[name]
    buckets.setdefault(cat, []).append((round(x, 1), round(z, 1), ni, int(area)))


class H(osmium.SimpleHandler):
    def node(self, n):
        cat = categorize(n.tags)
        if cat:
            add(n.location.lat, n.location.lon, cat, best_name(n.tags), 0)

    def way(self, w):
        cat = categorize(w.tags)
        if cat and len(w.nodes) > 2:
            try:
                lats = [nd.lat for nd in w.nodes]
                lons = [nd.lon for nd in w.nodes]
                la = sum(lats) / len(lats)
                lo = sum(lons) / len(lons)
                # shoelace in local meters for footprint area
                xs = [-(o - lon0) * M_LON for o in lons]
                zs = [(a - lat0) * M_LAT for a in lats]
                area = 0.0
                for i in range(len(xs) - 1):
                    area += xs[i] * zs[i + 1] - xs[i + 1] * zs[i]
                add(la, lo, cat, best_name(w.tags), abs(area) / 2)
            except Exception:  # noqa: BLE001
                pass

    def area(self, a):
        # relations (multipolygon malls/hospitals) come through as areas;
        # ways are handled above, so keep only relation-based areas
        if a.from_way():
            return
        cat = categorize(a.tags)
        if not cat:
            return
        try:
            ring = list(a.outer_rings())[0]
            lats = [nd.lat for nd in ring]
            lons = [nd.lon for nd in ring]
            la = sum(lats) / len(lats)
            lo = sum(lons) / len(lons)
            xs = [-(o - lon0) * M_LON for o in lons]
            zs = [(a2 - lat0) * M_LAT for a2 in lats]
            area = 0.0
            for i in range(len(xs) - 1):
                area += xs[i] * zs[i + 1] - xs[i + 1] * zs[i]
            add(la, lo, cat, best_name(a.tags), abs(area) / 2)
        except Exception:  # noqa: BLE001
            pass


H().apply_file(PBF, locations=True, idx="flex_mem")

# de-dup per category: named entries win over unnamed, radius 25 m
out = {}
for cat, pts in buckets.items():
    pts = sorted(pts, key=lambda p: (p[2] < 0, -p[3]))  # named + biggest first
    uniq = []
    for p in pts:
        if all((p[0] - u[0]) ** 2 + (p[1] - u[1]) ** 2 > 25 * 25 for u in uniq):
            uniq.append(p)
    out[cat] = uniq

json.dump({"names": names, "pois": out}, open(OUT, "w"), ensure_ascii=False, separators=(",", ":"))
print("POIs ->", OUT, os.path.getsize(OUT) // 1024, "KB,", len(names), "unique names")
for cat in sorted(out, key=lambda c: -len(out[c])):
    named = sum(1 for p in out[cat] if p[2] >= 0)
    print(f"  {cat}: {len(out[cat])} ({named} named)")
