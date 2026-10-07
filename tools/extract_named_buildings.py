"""Every NAMED building in the Tbilisi bbox -> tools/cache/named_buildings.json

  [{name, alts: [name:en, alt_name, ...], lat, lon, area_m2}]

Used by tools/pb_namematch.py to geocode Commons photos whose title names a
building ("Melik-Azaryants House", "Hotel Iota") but carries no coordinates
and no street address.
"""
import json
import math
import os

import osmium

HERE = os.path.dirname(os.path.abspath(__file__))
PBF = os.path.join(HERE, 'cache', 'georgia-latest.osm.pbf')
OUT = os.path.join(HERE, 'cache', 'named_buildings.json')
BBOX = (41.62, 44.68, 41.84, 44.98)
NAME_KEYS = ('name:en', 'name:ru', 'alt_name', 'old_name', 'int_name', 'official_name')


def keep(tags):
    if 'name' not in tags:
        return False
    return bool(tags.get('building') or tags.get('building:part')
                or tags.get('tourism') in ('hotel', 'museum')
                or tags.get('amenity') in ('theatre', 'cinema', 'university', 'hospital'))


out = []


def ring_stats(nodes):
    lats = [n.lat for n in nodes]
    lons = [n.lon for n in nodes]
    la = sum(lats) / len(lats)
    lo = sum(lons) / len(lons)
    # shoelace in degrees -> rough m^2
    a = 0.0
    for i in range(len(lats) - 1):
        a += lons[i] * lats[i + 1] - lons[i + 1] * lats[i]
    area = abs(a) / 2 * 111320 * 111320 * math.cos(math.radians(la))
    return la, lo, area


class H(osmium.SimpleHandler):
    def way(self, w):
        if not keep(w.tags) or len(w.nodes) < 3:
            return
        try:
            la, lo, area = ring_stats(w.nodes)
        except Exception:  # noqa: BLE001
            return
        if not (BBOX[0] <= la <= BBOX[2] and BBOX[1] <= lo <= BBOX[3]):
            return
        out.append({
            'name': w.tags['name'],
            'alts': [w.tags[k] for k in NAME_KEYS if k in w.tags],
            'lat': round(la, 6), 'lon': round(lo, 6), 'area': int(area),
        })

    def area(self, a):
        if a.from_way() or not keep(a.tags):
            return
        try:
            ring = list(a.outer_rings())[0]
            la, lo, area = ring_stats(list(ring))
        except Exception:  # noqa: BLE001
            return
        if not (BBOX[0] <= la <= BBOX[2] and BBOX[1] <= lo <= BBOX[3]):
            return
        out.append({
            'name': a.tags['name'],
            'alts': [a.tags[k] for k in NAME_KEYS if k in a.tags],
            'lat': round(la, 6), 'lon': round(lo, 6), 'area': int(area),
        })


H().apply_file(PBF, locations=True, idx='flex_mem')
json.dump(out, open(OUT, 'w'), ensure_ascii=False)
print(f'{len(out)} named buildings in Tbilisi -> {OUT}')

# ---- runtime copy: same shape as pois2.json so the game can reuse the
# wall-snap + SignPool machinery and put each building's REAL name on it ----
GAME = os.path.join(HERE, '..', 'public', 'tbilisi', 'named.json')
lat0, lon0 = 41.7252, 44.7458
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)
names, idx, items = [], {}, []
seen = set()
for e in out:
    nm = e['name'].strip()
    x = round(-(e['lon'] - lon0) * M_LON, 1)
    z = round((e['lat'] - lat0) * M_LAT, 1)
    k = (round(x / 25), round(z / 25), nm)
    if k in seen:
        continue
    seen.add(k)
    if nm not in idx:
        idx[nm] = len(names)
        names.append(nm)
    items.append([x, z, idx[nm], e['area']])
json.dump({'names': names, 'pois': {'named': items}}, open(GAME, 'w'),
          ensure_ascii=False, separators=(',', ':'))
print(f'{len(items)} building name signs -> {GAME}')
for e in out[:10]:
    print(f"  {e['area']:>7} m2  {e['name']}  {e['alts']}")
