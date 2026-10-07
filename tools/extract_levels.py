"""Real per-building facts from OSM: storey count, roof shape, material, colour.

Building heights in the game come from the GHSL satellite raster — a decent
average, but OSM carries the ACTUAL storey count for 17k Tbilisi buildings,
which fixes both the height and the number of window rows. Roof shape, wall
material and wall colour are rarer but equally real where they exist.

Output: public/tbilisi/levels.json
  {"items": [[x_dm, z_dm, levels, roofShape, material, colour], ...]}
  roofShape/material: small ints indexed into the SHAPES/MATERIALS lists.
  colour: "#rrggbb" or null.  x/z are local decimetres like city.json.
"""
import json
import math
import os

import osmium

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PBF = os.path.join(ROOT, 'tools', 'cache', 'georgia-latest.osm.pbf')
OUT = os.path.join(ROOT, 'public', 'tbilisi', 'levels.json')
BBOX = (41.62, 44.68, 41.84, 44.98)

lat0, lon0 = 41.7252, 44.7458
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)

SHAPES = ['', 'flat', 'gabled', 'hipped', 'pyramidal', 'skillion', 'half-hipped',
          'gambrel', 'mansard', 'dome', 'round', 'onion', 'cone']
MATERIALS = ['', 'plaster', 'brick', 'concrete', 'stone', 'glass', 'metal', 'wood',
             'cement_block', 'sandstone', 'tuff']
MAT_ALIAS = {'bricks': 'brick', 'reinforced_concrete': 'concrete',
             'concrete_block': 'cement_block', 'limestone': 'stone',
             'marble': 'stone', 'granite': 'stone'}
NAMED_COLOURS = {
    'white': '#ffffff', 'beige': '#f5f5dc', 'indianred': '#cd5c5c',
    'lightgrey': '#d3d3d3', 'lightgray': '#d3d3d3', 'grey': '#808080',
    'gray': '#808080', 'yellow': '#ffff00', 'red': '#ff0000', 'brown': '#a52a2a',
    'green': '#008000', 'blue': '#0000ff', 'orange': '#ffa500', 'pink': '#ffc0cb',
    'cream': '#fffdd0', 'sandybrown': '#f4a460', 'silver': '#c0c0c0',
}

items = []


def colour_of(v):
    if not v:
        return None
    v = v.strip().lower()
    if v.startswith('#') and len(v) in (4, 7):
        return v
    return NAMED_COLOURS.get(v)


def add(tags, lat, lon):
    if not (BBOX[0] <= lat <= BBOX[2] and BBOX[1] <= lon <= BBOX[3]):
        return
    lv = 0
    raw = tags.get('building:levels')
    if raw:
        try:
            lv = max(0, min(60, int(float(raw.split(';')[0].split(',')[0]))))
        except Exception:  # noqa: BLE001
            lv = 0
    shape = tags.get('roof:shape') or ''
    si = SHAPES.index(shape) if shape in SHAPES else 0
    mat = (tags.get('building:material') or '').lower()
    mat = MAT_ALIAS.get(mat, mat)
    mi = MATERIALS.index(mat) if mat in MATERIALS else 0
    col = colour_of(tags.get('building:colour'))
    if not (lv or si or mi or col):
        return
    x = -(lon - lon0) * M_LON
    z = (lat - lat0) * M_LAT
    items.append([round(x * 10), round(z * 10), lv, si, mi, col])


class H(osmium.SimpleHandler):
    def way(self, w):
        if 'building' not in w.tags or len(w.nodes) < 3:
            return
        try:
            lats = [n.lat for n in w.nodes]
            lons = [n.lon for n in w.nodes]
        except Exception:  # noqa: BLE001
            return
        add(w.tags, sum(lats) / len(lats), sum(lons) / len(lons))

    def area(self, a):
        if a.from_way() or 'building' not in a.tags:
            return
        try:
            ring = list(list(a.outer_rings())[0])
            lats = [n.lat for n in ring]
            lons = [n.lon for n in ring]
        except Exception:  # noqa: BLE001
            return
        add(a.tags, sum(lats) / len(lats), sum(lons) / len(lons))


H().apply_file(PBF, locations=True, idx='flex_mem')
json.dump({'shapes': SHAPES, 'materials': MATERIALS, 'items': items},
          open(OUT, 'w'), separators=(',', ':'))
n_lv = sum(1 for i in items if i[2])
n_sh = sum(1 for i in items if i[3])
n_mat = sum(1 for i in items if i[4])
n_col = sum(1 for i in items if i[5])
print(f'{len(items)} buildings with real facts -> {OUT} '
      f'({os.path.getsize(OUT) // 1024} KB)')
print(f'  levels {n_lv}, roof shape {n_sh}, material {n_mat}, colour {n_col}')
