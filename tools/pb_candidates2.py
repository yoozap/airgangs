"""
Round-5 per-building photo candidates: join the NEW category-crawl photos
(tools/cache/commons_catcrawl.json — geotagged files the geosearch sweep never
saw) to game buildings. Buildings that already wear a pb texture (manifest)
are excluded; previously reviewed photo titles are excluded.

Output: tools/cache/pb_candidates_r5.json — same shape as pb_candidates.json:
  [{bid, lat, lon, area, h, photos: [{title, url, w, h, lic, artist, dist}]}]
"""
import glob
import gzip
import json
import math
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
crawl = json.load(open(os.path.join(HERE, 'cache/commons_catcrawl.json')))
old = json.load(open(os.path.join(HERE, 'cache/commons_photo_meta.json')))
manifest = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))
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

# photo titles already reviewed in earlier rounds (any spec backup) + already
# used by a manifest texture
seen_titles = {v.get('title') for v in manifest.values() if v.get('title')}
for d in glob.glob(os.path.join(HERE, 'cache/pb_specs_backup*')):
    for f in glob.glob(d + '/*.json'):
        try:
            for s in json.load(open(f)):
                t = s.get('photo_title') or s.get('title') or s.get('photo')
                if t:
                    seen_titles.add(t if t.startswith('File:') else 'File:' + t)
        except Exception:  # noqa: BLE001
            pass
print(f'{len(seen_titles)} previously used/reviewed titles')

# grid-index the NEW usable geotagged photos
cell = 0.0004
grid = {}
kept = 0
for t, m in crawl.items():
    if t in old or t in seen_titles:
        continue
    if not m.get('lat') or not m.get('url') or not m.get('w'):
        continue
    if m['w'] * m['h'] < 1_200_000:
        continue
    if not OK_LIC.search(m.get('lic') or ''):
        continue
    if JUNK.search(t):
        continue
    grid.setdefault((int(m['lat'] / cell), int(m['lon'] / cell)), []).append((t, m))
    kept += 1
print(f'{kept} new usable geotagged photos in the join')

done_bids = {int(k[1:]) for k in manifest}
out = []
for bid, b in enumerate(city['buildings']):
    if bid in done_bids:
        continue
    n2 = (len(b) - 1) // 2
    la = o['lat'] + sum(b[2::2]) / n2 / 10 / M_LAT
    lo = o['lon'] - sum(b[1::2]) / n2 / 10 / M_LON
    if not (41.62 <= la <= 41.84 and 44.68 <= lo <= 44.98):
        continue
    xs = b[1::2]
    zs = b[2::2]
    area = 0.0
    for i in range(n2):
        j = (i + 1) % n2
        area += xs[i] * zs[j] - xs[j] * zs[i]
    area = abs(area) / 200  # dm^2 -> m^2
    if area < 25:
        continue
    ci, cj = int(la / cell), int(lo / cell)
    photos = []
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for t, m in grid.get((ci + di, cj + dj), []):
                d = math.hypot((m['lat'] - la) * M_LAT, (m['lon'] - lo) * M_LON)
                if d < 35:
                    photos.append({
                        'title': t, 'url': m['url'], 'w': m['w'], 'h': m['h'],
                        'lic': m.get('lic'), 'artist': m.get('artist'), 'dist': round(d, 1),
                    })
    if photos:
        photos.sort(key=lambda p: p['dist'])
        out.append({
            'bid': bid, 'lat': round(la, 6), 'lon': round(lo, 6),
            'area': int(area), 'h': b[0] / 10, 'photos': photos[:4],
        })

out.sort(key=lambda e: e['photos'][0]['dist'])
dst = os.path.join(HERE, 'cache/pb_candidates_r5.json')
json.dump(out, open(dst, 'w'), ensure_ascii=False)
uniq = len({e['photos'][0]['title'] for e in out})
print(f'{len(out)} candidate buildings ({uniq} unique nearest-photos) -> {dst}')
