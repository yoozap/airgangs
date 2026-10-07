"""Geocode address-titled Commons photos that carry NO coordinates.

Many heritage-house photos are titled "7 Shio Chitadze Street, Tbilisi" or
"ბროსეს ქუჩა 5" — a real address. Parse it, resolve the street via the OSM
street-name index, find the matching addr:housenumber point in
addresses.json.gz, then the building whose centroid is nearest that point.

Output: tools/cache/pb_candidates_addr.json (same shape as pb_candidates.json)
"""
import gzip
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
crawl = json.load(open(os.path.join(HERE, 'cache/commons_catcrawl.json')))
old = json.load(open(os.path.join(HERE, 'cache/commons_photo_meta.json')))
snames = json.load(open(os.path.join(HERE, 'cache/street_names.json')))
addrs = json.load(gzip.open(os.path.join(HERE, '../public/tbilisi/addresses.json.gz')))
city = json.load(gzip.open(os.path.join(HERE, '../public/tbilisi/city.json.gz')))
manifest = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))

M_LAT, M_LON = 111067.92, 83206.34
O_LAT, O_LON = 41.7252, 44.7458
OK_LIC = re.compile(r'CC BY|CC0|Public domain|PDM', re.I)
JUNK = re.compile(r'interior|inside|plaque|balcony detail|door of|window of|'
                  r'staircase|courtyard of|ceiling|ornament|fragment', re.I)

PATS = [
    re.compile(r'\b(\d{1,3})[a-zა-ჰ]?\s+([A-Z][\w\'\.-]*(?:\s+[A-Z][\w\'\.-]*){0,3})\s+'
               r'(?:Street|St\b|Avenue|Ave\b|Lane|Square|Sq\b|Alley|Rise|Road)', re.I),
    re.compile(r'([A-Z][\w\'\.-]*(?:\s+[A-Z][\w\'\.-]*){0,3})\s+'
               r'(?:Street|St\.|Avenue|Ave\.|Lane|Square)[, ]+(\d{1,3})', re.I),
    re.compile(r'([Ⴀ-ჿ]+(?:\s+[Ⴀ-ჿ]+){0,3})\s*(?:ქუჩა|გამზირი|მოედანი)\s*(\d{1,3})'),
]


def norm_en(s):
    s = s.lower()
    s = re.sub(r'\b(street|st|avenue|ave|lane|square|sq|road|alley|rise|blind|'
               r'ქუჩა|გამზირი|მოედანი|ჩიხი|გასასვლელი|улица|проспект)\b', ' ', s)
    s = re.sub(r"[^a-zႠ-ჿ]+", ' ', s)
    return ' '.join(sorted(w for w in s.split() if len(w) > 2))


def clean_body(t):
    body = t[5:] if t.startswith('File:') else t
    # Commons upload ids "(50497464096)" and years would masquerade as house
    # numbers; strip them along with the extension
    body = re.sub(r'\(\d{3,}\)|\b(19|20)\d{2}\b|\.jpe?g$|\.png$', ' ', body, flags=re.I)
    return body


def parse_title(t):
    """-> (street_text, housenumber) or None"""
    body = clean_body(t)
    for i, p in enumerate(PATS):
        m = p.search(body)
        if not m:
            continue
        g = m.groups()
        return (g[1], g[0]) if i == 0 else (g[0], g[1])
    return None


def parse_loose(t, resolves):
    """Address without the word 'street': 'ბროსეს 5', 'Betlemi 3', 'Тбилиси,
    Бетлеми 3'. Take the words around every number and keep the longest run
    that actually RESOLVES to a real street — the index is the validator, so a
    stray number in a title can't invent an address."""
    body = clean_body(t)
    body = re.sub(r'[,;:.–—-]', ' ', body)
    toks = [w for w in body.split() if w]
    best = None
    for i, w in enumerate(toks):
        m = re.fullmatch(r'(\d{1,3})[a-zა-ჰ]?', w)
        if not m:
            continue
        num = m.group(1)
        for side in ('before', 'after'):
            for take in (4, 3, 2, 1):
                if side == 'before':
                    words = toks[max(0, i - take):i]
                else:
                    words = toks[i + 1:i + 1 + take]
                if len(words) < take:
                    continue
                txt = ' '.join(words)
                if resolves(txt, num) and (best is None or take > best[2]):
                    best = (txt, num, take)
    return (best[0], best[1]) if best else None


# street name -> canonical Georgian name (index built from OSM highways)
en2ka = snames['en2ka']
# Georgian street key normalization for the addresses table
streets = addrs['streets']
st_norm = {}
for i, s in enumerate(streets):
    st_norm.setdefault(norm_en(s), []).append(i)

# Address points by (street_idx, number) — TBILISI ONLY. addresses.json.gz is
# built from the whole-Georgia PBF in ONE local frame, and Batumi, Kutaisi and
# Rustavi all have a Lado Asatiani St and a Pushkin St: without this box, 39
# Tbilisi house photos were pasted onto buildings in other cities.
BOX_X = (-9000, 12000)   # local metres, Tbilisi bbox 41.62-41.84 / 44.68-44.98
BOX_Z = (-12000, 13000)
apoints = {}
for x, z, si, num in addrs['items']:
    if not num:
        continue
    px, pz = x / 10, z / 10
    if not (BOX_X[0] <= px <= BOX_X[1] and BOX_Z[0] <= pz <= BOX_Z[1]):
        continue
    apoints.setdefault((si, num.lower().strip()), []).append((px, pz))

# building centroid grid (keep the ring so we can test point-in-footprint —
# an address node usually sits INSIDE its own building, and that beats any
# nearest-centroid guess: a 45 m radius can hop to the neighbour's house)
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

# Words that are NOT a street even though the index resolves them: Tbilisi is
# also a street name somewhere, so "Photo of Tbilisi 10" geocoded as an
# address. A loose title must contain at least one token outside this set.
STOP = {
    'tbilisi', 'tbilisis', 'tiflis', 'tblisi', 'georgia', 'georgian', 'sakartvelo',
    'თბილისი', 'თბილისის', 'ტფილისი', 'საქართველო', 'საქართველოს',
    'photo', 'photos', 'foto', 'image', 'view', 'travel', 'tourism', 'night',
    'building', 'buildings', 'house', 'houses', 'street', 'city', 'old', 'new',
    'district', 'church', 'hotel', 'museum', 'panorama', 'ჩრდილო', 'ქალაქი',
}


def street_indices(street_txt, loose=False):
    """street text -> candidate street indices in the addresses table.

    `loose` is for titles that never said "street" ("ბროსეს 5"): there the
    substring fallback must be OFF. With it on, photo-series names like
    "Georgia Travel And Tourism 12" matched some street by shared letters and
    invented 1000 bogus addresses.
    """
    key = norm_en(street_txt)
    if not key:
        return []
    if loose and all(w in STOP for w in key.split()):
        return []  # "Tbilisi", "old city" — a series title, not an address
    ka = en2ka.get(key)
    idxs = st_norm.get(norm_en(ka) if ka else key, [])
    if idxs or loose:
        return idxs
    for k2, ii in st_norm.items():  # substring fallback (partial street names)
        if len(key) > 5 and (key in k2 or k2 in key):
            return ii
    return []


def resolves(street_txt, num):
    """true when this street+number is a REAL address point in Tbilisi"""
    for si in street_indices(street_txt, loose=True):
        if apoints.get((si, num.lower())):
            return True
    return False


out = {}
stats = {'parsed': 0, 'loose': 0, 'street_ok': 0, 'addr_ok': 0, 'bld_ok': 0}
for t, m in crawl.items():
    if m.get('lat') or t in old or t in used_titles:
        continue
    if not (m.get('url') and m.get('w')) or m['w'] * m['h'] < 1_200_000:
        continue
    if not OK_LIC.search(m.get('lic') or '') or JUNK.search(t):
        continue
    pa = parse_title(t)
    strict = bool(pa)
    if pa:
        stats['parsed'] += 1
    else:
        pa = parse_loose(t, resolves)  # 'ბროსეს 5' — no "street" word at all
        if not pa:
            continue
        stats['loose'] += 1
    street_txt, num = pa
    cand_si = street_indices(street_txt, loose=not strict)
    if not cand_si:
        continue
    stats['street_ok'] += 1
    pts = []
    for si in cand_si:
        pts += apoints.get((si, num.lower()), [])
    if not pts:
        continue
    stats['addr_ok'] += 1
    px, pz = pts[0]
    best = None
    ci, cj = int(px // cell), int(pz // cell)
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            for bid, cx, cz, h, ring in grid.get((ci + di, cj + dj), []):
                if bid in done_bids:
                    continue
                d = math.hypot(cx - px, cz - pz)
                # containment wins outright; otherwise a tight 18 m so the
                # facade can't be pinned onto the neighbour's house
                score = -1 if inside(px, pz, ring) else d
                if score < 18 and (best is None or score < best[0]):
                    best = (score, bid, cx, cz, h)
    if not best:
        continue
    stats['bld_ok'] += 1
    _, bid, cx, cz, h = best
    e = out.get(bid)
    photo = {'title': t, 'url': m['url'], 'w': m['w'], 'h': m['h'],
             'lic': m.get('lic'), 'artist': m.get('artist'), 'dist': 0.0,
             'addr': f'{street_txt} {num}'}
    if e:
        e['photos'].append(photo)
    else:
        out[bid] = {
            'bid': bid,
            'lat': round(O_LAT + cz / M_LAT, 6), 'lon': round(O_LON - cx / M_LON, 6),
            'area': 0, 'h': h, 'photos': [photo],
        }

res = sorted(out.values(), key=lambda e: -len(e['photos']))
dst = os.path.join(HERE, 'cache/pb_candidates_addr.json')
json.dump(res, open(dst, 'w'), ensure_ascii=False)
print(f"parsed {stats['parsed']} address titles, street resolved {stats['street_ok']}, "
      f"address point {stats['addr_ok']}, building matched {stats['bld_ok']}")
print(f'{len(res)} NEW buildings geocoded from titles -> {dst}')
for e in res[:12]:
    print(f"  bid {e['bid']:<7} {len(e['photos'])} photo(s)  {e['photos'][0]['addr']}")
