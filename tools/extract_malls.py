"""Match mall/market POIs (pois2.json) to their city.json building footprints.

Output: tools/cache/malls.json
  [{name, lat, lon, h, area, bid, poly: [[x,z]...] meters relative to centroid}]
The game builds each of these as a real Blender-style mall; the OSM extrusion
is suppressed via a runtime exclusion by bid.
"""
import gzip
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
city = json.load(gzip.open(os.path.join(HERE, '../public/tbilisi/city.json.gz')))
d = json.load(open(os.path.join(HERE, '../public/tbilisi/pois2.json')))
names, pois = d['names'], d['pois']

lat0, lon0 = 41.7252, 44.7458
M_LAT, M_LON = 111067.92, 83206.34

targets = []
for cat, minarea in (('mall', 800), ('market', 2000)):
    for x, z, ni, area in pois.get(cat, []):
        if ni < 0:
            continue
        targets.append({'x': x, 'z': z, 'name': names[ni], 'cat': cat, 'poi_area': area})

# grid the buildings once (they are many, targets are few but 400 m search)
cell = 200
grid = {}
for bid, b in enumerate(city['buildings']):
    n = (b.length if False else (len(b) - 1) // 2)
    cx = sum(b[1::2]) / n / 10
    cz = sum(b[2::2]) / n / 10
    grid.setdefault((int(cx // cell), int(cz // cell)), []).append((bid, cx, cz))

out = []
for t in targets:
    ci, cj = int(t['x'] // cell), int(t['z'] // cell)
    best = None
    for di in (-2, -1, 0, 1, 2):
        for dj in (-2, -1, 0, 1, 2):
            for bid, cx, cz in grid.get((ci + di, cj + dj), []):
                dd = (cx - t['x']) ** 2 + (cz - t['z']) ** 2
                if best is None or dd < best[0]:
                    best = (dd, bid, cx, cz)
    if not best or best[0] > 300 ** 2:
        print('NO BUILDING for', t['name'])
        continue
    _, bid, cx, cz = best
    b = city['buildings'][bid]
    n = (len(b) - 1) // 2
    poly = [[b[1 + i * 2] / 10 - cx, b[2 + i * 2] / 10 - cz] for i in range(n)]
    area = 0.0
    for i in range(n):
        j = (i + 1) % n
        area += poly[i][0] * poly[j][1] - poly[j][0] * poly[i][1]
    out.append({
        'name': t['name'], 'cat': t['cat'],
        'lat': round(lat0 + cz / M_LAT, 6), 'lon': round(lon0 - cx / M_LON, 6),
        'x': round(cx, 1), 'z': round(cz, 1),
        'h': b[0] / 10, 'area': int(abs(area) / 2), 'bid': bid, 'poly': poly,
    })

# biggest first; drop tiny "malls" (POI mis-tags) under 600 m2
out = [e for e in out if e['area'] >= 600]
out.sort(key=lambda e: -e['area'])
dst = os.path.join(HERE, 'cache/malls.json')
json.dump(out, open(dst, 'w'), ensure_ascii=False, indent=1)
print(f'{len(out)} malls/markets -> {dst}')
for e in out[:20]:
    print(f"  {e['area']:>6} m2  h={e['h']:<5} {e['name']}")
