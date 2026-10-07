"""OSM `building:part` volumes — the real 3D massing of complex buildings.

A hotel with a tower over a podium, a mall with wings of different heights or a
church with a bell tower is ONE outline in city.json and extrudes as one flat
box. OSM maps those as building:part polygons, each with its own height and
min_height. 479 Tbilisi buildings have parts at DIFFERENT heights — this pulls
them out so the game can build the real stepped silhouette.

Output: public/tbilisi/parts.json
  {"items": [[h_dm, min_dm, x0,z0, x1,z1, ...], ...]}   local decimetres,
  same coordinate convention as city.json buildings.
"""
import json
import math
import os
import re

import osmium

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PBF = os.path.join(ROOT, 'tools', 'cache', 'georgia-latest.osm.pbf')
OUT = os.path.join(ROOT, 'public', 'tbilisi', 'parts.json')
BBOX = (41.62, 44.68, 41.84, 44.98)

lat0, lon0 = 41.7252, 44.7458
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)

items = []


def metres(tag, levels_tag, tags, fallback=None):
    v = tags.get(tag)
    if v:
        m = re.match(r'\s*([\d.]+)', v)
        if m:
            return float(m.group(1))
    v = tags.get(levels_tag)
    if v:
        try:
            return float(str(v).split(';')[0]) * 3.1
        except Exception:  # noqa: BLE001
            pass
    return fallback


def add(tags, ring):
    h = metres('height', 'building:levels', tags)
    if h is None or h <= 0:
        return
    mn = metres('min_height', 'building:min_level', tags, 0.0) or 0.0
    if mn >= h:
        return
    pts = []
    for la, lo in ring:
        if not (BBOX[0] <= la <= BBOX[2] and BBOX[1] <= lo <= BBOX[3]):
            return
        pts.append((round(-(lo - lon0) * M_LON * 10), round((la - lat0) * M_LAT * 10)))
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts.pop()
    if len(pts) < 3 or len(pts) > 60:
        return
    flat = [round(h * 10), round(mn * 10)]
    for x, z in pts:
        flat += [x, z]
    items.append(flat)


class H(osmium.SimpleHandler):
    def way(self, w):
        t = w.tags
        if 'building:part' not in t or t.get('building:part') == 'no':
            return
        if len(w.nodes) < 4:
            return
        try:
            add(t, [(n.lat, n.lon) for n in w.nodes])
        except Exception:  # noqa: BLE001
            pass

    def area(self, a):
        if a.from_way():
            return
        t = a.tags
        if 'building:part' not in t or t.get('building:part') == 'no':
            return
        try:
            ring = list(list(a.outer_rings())[0])
            add(t, [(n.lat, n.lon) for n in ring])
        except Exception:  # noqa: BLE001
            pass


H().apply_file(PBF, locations=True, idx='flex_mem')
json.dump({'items': items}, open(OUT, 'w'), separators=(',', ':'))
print(f'{len(items)} building parts -> {OUT} ({os.path.getsize(OUT) // 1024} KB)')
hs = sorted(i[0] / 10 for i in items)
if hs:
    print(f'  heights: min {hs[0]:.1f} m, median {hs[len(hs) // 2]:.1f} m, max {hs[-1]:.1f} m')
    print(f'  {sum(1 for i in items if i[1] > 0)} parts start above ground (min_height)')
