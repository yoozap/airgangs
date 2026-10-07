"""
Per-building photo candidates: join the full Commons geophoto inventory to
game buildings, filter junk, rank by quality → tools/cache/pb_candidates.json
[{bid, lat, lon, area_m2, photos: [{title, url, w, h, lic, artist, dist}]}]
"""
import gzip
import json
import math
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
photos = json.load(open(os.path.join(HERE, 'cache/commons_geophotos.json')))
meta = json.load(open(os.path.join(HERE, 'cache/commons_photo_meta.json')))
city = json.load(gzip.open(os.path.join(HERE, '../public/tbilisi/city.json.gz')))
M_LAT, M_LON = 111067.92, 83206.34
o = {'lat': 41.7252, 'lon': 44.7458}

JUNK = re.compile(
    r'plaque|statue|memorial|monument|interior|inside|dashboard|panorama|aerial|'
    r'drone|group photo|meeting|wikipedian|conference|人|sculpture|grave|tomb|'
    r'fresco|icon|mural|door knocker|bell|sign at|number sign|water tap|'
    r'wheelchair|playground|fountain|graffiti|mosaic|metro|bus|car|trolley|'
    r'street ?scene|view (from|of|over)|panoramio|night|evening|sunset|people',
    re.I)
OK_LIC = re.compile(r'CC BY|CC0|Public domain|PDM', re.I)

# grid-index photos
cell = 0.0004
grid = {}
for t, (la, lo) in photos.items():
    m = meta.get(t)
    if not m or not m.get('url') or not m.get('w'):
        continue
    if m['w'] * m['h'] < 1_200_000:  # need real resolution
        continue
    if not OK_LIC.search(m['lic'] or ''):
        continue
    if JUNK.search(t):
        continue
    grid.setdefault((int(la / cell), int(lo / cell)), []).append((t, la, lo, m))

out = []
for bid, b in enumerate(city['buildings']):
    n2 = (len(b) - 1) // 2
    la = o['lat'] + sum(b[2::2]) / n2 / 10 / M_LAT
    lo = o['lon'] - sum(b[1::2]) / n2 / 10 / M_LON
    if not (41.62 <= la <= 41.84 and 44.68 <= lo <= 44.98):
        continue
    xs = b[1::2]
    zs = b[2::2]
    area = abs(sum(xs[i] * zs[(i + 1) % n2] - xs[(i + 1) % n2] * zs[i] for i in range(n2))) / 200
    ci, cj = int(la / cell), int(lo / cell)
    cand = []
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for t, pla, plo, m in grid.get((ci + di, cj + dj), []):
                d = math.hypot((pla - la) * 111000, (plo - lo) * 83200)
                if d < 35:
                    cand.append({'title': t, 'url': m['url'], 'w': m['w'], 'h': m['h'],
                                 'lic': m['lic'], 'artist': re.sub(r'<[^>]+>', '', m['artist'] or '').strip(),
                                 'dist': round(d)})
    if cand:
        cand.sort(key=lambda c: (c['dist'], -c['w'] * c['h']))
        out.append({'bid': bid, 'lat': round(la, 6), 'lon': round(lo, 6),
                    'area': round(area), 'h': b[0] / 10, 'photos': cand[:3]})

out.sort(key=lambda e: (e['photos'][0]['dist'], -e['area']))
json.dump(out, open(os.path.join(HERE, 'cache/pb_candidates.json'), 'w'), ensure_ascii=False)
print(f'{len(out)} buildings with usable photo candidates '
      f'({sum(len(e["photos"]) for e in out)} photos)')
