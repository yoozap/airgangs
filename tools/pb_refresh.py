"""Keep the city current: replace facades cut from pre-2020 photos.

The game must look like Tbilisi TODAY, so a facade photographed in 2011 can
show a building that has since been renovated, repainted or demolished. This
finds every manifest entry whose source photo predates `MIN_YEAR`, looks for a
NEWER photo of the same building among all candidate pools, and writes an
agent review package for the ones that can be upgraded.

  python3 tools/pb_refresh.py           # report + write the upgrade package
  python3 tools/pb_refresh.py --drop    # also delete facades with no newer photo
                                        # and no chance of one (pre-2010)
"""
import glob
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
API = 'https://commons.wikimedia.org/w/api.php'
UA = 'TbilisiFlyGame/1.0 (personal game project; sergo93@gmail.com)'
DATES = os.path.join(HERE, 'cache', 'pb_photo_dates.json')
MIN_YEAR = 2020
DROP_BEFORE = 2010  # older than this is dropped outright when no upgrade exists

man = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))
dates = json.load(open(DATES)) if os.path.exists(DATES) else {}

# every candidate photo we know about, per building
cands = {}
for f in glob.glob(os.path.join(HERE, 'cache', 'pb_candidates*.json')):
    for e in json.load(open(f)):
        cands.setdefault(e['bid'], {})
        for p in e['photos']:
            cands[e['bid']].setdefault(p['title'], p)


def api(params, tries=4):
    data = urllib.parse.urlencode(dict(params, format='json')).encode()
    for a in range(tries):
        try:
            req = urllib.request.Request(API, data=data, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=40) as r:
                return json.load(r)
        except Exception as e:  # noqa: BLE001
            print('  retry', a + 1, e)
            time.sleep(2 * (a + 1))
    return None


def fetch_dates(titles):
    todo = [t for t in titles if t not in dates]
    for i in range(0, len(todo), 25):
        batch = todo[i:i + 25]
        res = api({'action': 'query', 'titles': '|'.join(batch), 'prop': 'imageinfo',
                   'iiprop': 'extmetadata',
                   'iiextmetadatafilter': 'DateTimeOriginal|DateTime'})
        if not res:
            continue
        for page in res.get('query', {}).get('pages', {}).values():
            t = page.get('title')
            ii = (page.get('imageinfo') or [{}])[0]
            em = ii.get('extmetadata', {})
            raw = ((em.get('DateTimeOriginal') or {}).get('value')
                   or (em.get('DateTime') or {}).get('value') or '')
            raw = re.sub(r'<[^>]+>', ' ', raw)
            mm = re.search(r'(19|20)\d{2}', raw)
            dates[t] = int(mm.group(0)) if mm else None
        json.dump(dates, open(DATES, 'w'), ensure_ascii=False)
        print(f'  dates {i + len(batch)}/{len(todo)}')
        time.sleep(0.3)


# ---- which manifest entries are stale? -----------------------------------
stale = []
for key, m in man.items():
    y = dates.get(m.get('title'))
    if y is not None and y < MIN_YEAR:
        stale.append((key, int(key[1:]), y, m))
print(f'{len(stale)} of {len(man)} facades come from pre-{MIN_YEAR} photos')

# ---- date every alternative photo of those buildings ----------------------
alt_titles = set()
for _key, bid, _y, m in stale:
    for t in (cands.get(bid) or {}):
        if t != m.get('title'):
            alt_titles.add(t)
print(f'dating {len(alt_titles)} alternative photos of those buildings…')
fetch_dates(sorted(alt_titles))

pkg, keep, drop = [], [], []
for key, bid, y, m in stale:
    alts = []
    for t, p in (cands.get(bid) or {}).items():
        if t == m.get('title'):
            continue
        ay = dates.get(t)
        if ay is not None and ay >= MIN_YEAR:
            alts.append((ay, p))
    if alts:
        alts.sort(key=lambda a: -a[0])
        pkg.append({'bid': bid, 'lat': m['lat'], 'lon': m['lon'], 'area': 0, 'h': 0,
                    'photos': [dict(p, addr=f'{ay} photo') for ay, p in alts[:4]]})
    elif y < DROP_BEFORE:
        drop.append((key, y, m.get('title', '')))
    else:
        keep.append((key, y))

print(f'\n{len(pkg)} buildings have a {MIN_YEAR}+ photo available -> re-review')
print(f'{len(drop)} have only pre-{DROP_BEFORE} photos -> drop')
print(f'{len(keep)} keep their {DROP_BEFORE}-{MIN_YEAR - 1} photo (no newer one exists)')

outdir = os.path.join(HERE, 'cache', 'pb_r5_state', 'pb_pkgs_refresh')
os.makedirs(outdir, exist_ok=True)
os.makedirs(os.path.join(HERE, 'cache', 'pb_r5_state', 'pb_specs_refresh'), exist_ok=True)
N = max(1, min(6, (len(pkg) + 19) // 20))
for a in range(N):
    part = pkg[a::N]
    json.dump(part, open(os.path.join(outdir, f'pkg{a}.json'), 'w'), ensure_ascii=False)
    print(f'  refresh pkg{a}: {len(part)} buildings')

if '--drop' in sys.argv and drop:
    for key, y, t in drop:
        man.pop(key, None)
        f = os.path.join(HERE, '../public/textures/pb', key + '.jpg')
        if os.path.exists(f):
            os.remove(f)
    json.dump(man, open(os.path.join(HERE, '../public/textures/pb/manifest.json'), 'w'),
              ensure_ascii=False)
    print(f'dropped {len(drop)} pre-{DROP_BEFORE} facades; manifest now {len(man)}')
