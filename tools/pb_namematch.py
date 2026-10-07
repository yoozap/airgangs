"""Geocode Commons photos whose TITLE names a building.

The category crawl left ~5,700 usable photos with no coordinates and no street
address in the title — but many name the building itself ("Melik-Azaryants
House", "თბილისის კლასიკური გიმნაზია"). Match those titles against every named
building in OSM, then to the game's footprint.

Output: tools/cache/pb_candidates_name.json (pb_candidates.json shape)
"""
import gzip
import json
import math
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
crawl = json.load(open(os.path.join(HERE, 'cache/commons_catcrawl.json')))
old = json.load(open(os.path.join(HERE, 'cache/commons_photo_meta.json')))
named = json.load(open(os.path.join(HERE, 'cache/named_buildings.json')))
city = json.load(gzip.open(os.path.join(HERE, '../public/tbilisi/city.json.gz')))
manifest = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))

M_LAT, M_LON = 111067.92, 83206.34
O_LAT, O_LON = 41.7252, 44.7458
OK_LIC = re.compile(r'CC BY|CC0|Public domain|PDM', re.I)
JUNK = re.compile(
    r'interior|inside|plaque|panorama|aerial|drone|night|dusk|construction|'
    r'ruins?\b|fresco|icon|mural|exhibit|collection|logo|map\b|plan\b|'
    r'view (from|of|over)|from the|people|crowd|concert|match|game|food|menu',
    re.I)

# names too generic to identify a building from a photo title
GENERIC = re.compile(
    r'^(hotel|bank|school|№?\s*\d+|market|mall|church|cathedral|mosque|museum|'
    r'theatre|theater|cinema|university|hospital|clinic|pharmacy|garage|'
    r'residence|apartments?|business cent|office|tower|plaza|palace|station|'
    r'vip[- ]terminal|terminal|complex|building)\b', re.I)


def norm(s):
    s = s.lower()
    s = re.sub(r'[^0-9a-zა-ჰა-ჰа-яё\s]+', ' ', s)
    return ' '.join(s.split())


# ---- build the key -> building index -------------------------------------
keys = {}
for b in named:
    for nm in [b['name']] + b.get('alts', []):
        if not nm or GENERIC.match(nm.strip()):
            continue
        k = norm(nm)
        words = k.split()
        # precision gate: a key must be distinctive, not a single short word
        if len(k) < 9:
            continue
        if len(words) == 1 and len(k) < 12:
            continue
        prev = keys.get(k)
        if prev is None or b['area'] > prev['area']:
            keys[k] = b
print(f'{len(keys)} distinctive building-name keys from {len(named)} named buildings')

# ---- grid the game footprints --------------------------------------------
cell = 60
grid = {}
for bid, b in enumerate(city['buildings']):
    n = (len(b) - 1) // 2
    cx = sum(b[1::2]) / n / 10
    cz = sum(b[2::2]) / n / 10
    ring = [(b[1 + i * 2] / 10, b[2 + i * 2] / 10) for i in range(n)]
    grid.setdefault((int(cx // cell), int(cz // cell)), []).append((bid, cx, cz, b[0] / 10, ring))


def inside(px, pz, ring):
    hit = False
    n = len(ring)
    for i in range(n):
        x1, z1 = ring[i]
        x2, z2 = ring[(i + 1) % n]
        if (z1 > pz) != (z2 > pz) and px < (x2 - x1) * (pz - z1) / (z2 - z1 + 1e-12) + x1:
            hit = not hit
    return hit


done_bids = {int(k[1:]) for k in manifest}
used_titles = {v.get('title') for v in manifest.values()}

out = {}
stats = {'title_hit': 0, 'bld_ok': 0}
for t, m in crawl.items():
    if m.get('lat') or t in old or t in used_titles:
        continue
    if not (m.get('url') and m.get('w')) or m['w'] * m['h'] < 1_200_000:
        continue
    if not OK_LIC.search(m.get('lic') or '') or JUNK.search(t):
        continue
    # A photo can be named "DSC_1234.jpg" and still sit in a building-specific
    # category ("Tbilisi Assembly Building in 2016"), so the CATEGORY names are
    # searched too — but only ones that look building-specific, never the broad
    # buckets ("Buildings in Tbilisi") which would match half the city.
    cats = [c for c in (m.get('cats') or [])
            if len(c) > 12 and not re.match(r'(buildings?|houses?|streets?|architecture|'
                                            r'balconies|doors|windows|churches) (in|of) ', c, re.I)]
    nt = norm(' ; '.join([t[5:] if t.startswith('File:') else t] + cats))
    hit = None
    for k, b in keys.items():
        if k in nt and (hit is None or len(k) > len(hit[0])):
            hit = (k, b)
    if not hit:
        continue
    stats['title_hit'] += 1
    b = hit[1]
    px = -(b['lon'] - O_LON) * M_LON
    pz = (b['lat'] - O_LAT) * M_LAT
    best = None
    ci, cj = int(px // cell), int(pz // cell)
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for bid, cx, cz, h, ring in grid.get((ci + di, cj + dj), []):
                if bid in done_bids:
                    continue
                d = math.hypot(cx - px, cz - pz)
                score = -1 if inside(px, pz, ring) else d
                if score < 25 and (best is None or score < best[0]):
                    best = (score, bid, cx, cz, h)
    if not best:
        continue
    stats['bld_ok'] += 1
    _, bid, cx, cz, h = best
    photo = {'title': t, 'url': m['url'], 'w': m['w'], 'h': m['h'],
             'lic': m.get('lic'), 'artist': m.get('artist'), 'dist': 0.0,
             'addr': b['name']}
    if bid in out:
        out[bid]['photos'].append(photo)
    else:
        out[bid] = {'bid': bid, 'lat': round(O_LAT + cz / M_LAT, 6),
                    'lon': round(O_LON - cx / M_LON, 6), 'area': 0, 'h': h,
                    'photos': [photo]}

res = sorted(out.values(), key=lambda e: -len(e['photos']))
dst = os.path.join(HERE, 'cache/pb_candidates_name.json')
json.dump(res, open(dst, 'w'), ensure_ascii=False)
print(f"{stats['title_hit']} titles named a building, {stats['bld_ok']} matched a footprint")
print(f'{len(res)} NEW buildings -> {dst}')
for e in res[:15]:
    print(f"  bid {e['bid']:<7} {len(e['photos']):>2} photo(s)  {e['photos'][0]['addr']}")
