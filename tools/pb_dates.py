"""Audit the per-building facade set by PHOTO DATE.

The game must show today's Tbilisi, so facades should come from recent photos
(2020+). Commons carries DateTimeOriginal in extmetadata; the crawl never
fetched it. This pulls the date for every photo in the pb manifest and reports
the distribution, writing tools/cache/pb_photo_dates.json {title: year|null}.

  python3 tools/pb_dates.py            # audit only
  python3 tools/pb_dates.py --list     # also list the pre-2020 buildings
"""
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'cache', 'pb_photo_dates.json')
API = 'https://commons.wikimedia.org/w/api.php'
UA = 'TbilisiFlyGame/1.0 (personal game project; sergo93@gmail.com)'

man = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))
dates = json.load(open(OUT)) if os.path.exists(OUT) else {}

titles = sorted({m['title'] for m in man.values() if m.get('title')})
todo = [t for t in titles if t not in dates]
print(f'{len(titles)} distinct photos in the manifest, {len(todo)} without a known date')


def api(params, tries=4):
    # POST: long Georgian titles blow past the URL length limit as %XX escapes
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


for i in range(0, len(todo), 25):
    batch = todo[i:i + 25]
    res = api({
        'action': 'query', 'titles': '|'.join(batch), 'prop': 'imageinfo',
        'iiprop': 'extmetadata',
        'iiextmetadatafilter': 'DateTimeOriginal|DateTime',
    })
    if not res:
        continue
    for page in res.get('query', {}).get('pages', {}).values():
        t = page.get('title')
        ii = (page.get('imageinfo') or [{}])[0]
        em = ii.get('extmetadata', {})
        raw = ((em.get('DateTimeOriginal') or {}).get('value')
               or (em.get('DateTime') or {}).get('value') or '')
        raw = re.sub(r'<[^>]+>', ' ', raw)
        m = re.search(r'(19|20)\d{2}', raw)
        dates[t] = int(m.group(0)) if m else None
    json.dump(dates, open(OUT, 'w'), ensure_ascii=False)
    print(f'  {i + len(batch)}/{len(todo)}')
    time.sleep(0.3)

json.dump(dates, open(OUT, 'w'), ensure_ascii=False)

buckets = {'2020+': 0, '2015-2019': 0, '2010-2014': 0, 'pre-2010': 0, 'unknown': 0}
old_keys = []
for key, m in man.items():
    y = dates.get(m.get('title'))
    if y is None:
        buckets['unknown'] += 1
    elif y >= 2020:
        buckets['2020+'] += 1
    elif y >= 2015:
        buckets['2015-2019'] += 1
        old_keys.append((y, key, m.get('title', '')))
    elif y >= 2010:
        buckets['2010-2014'] += 1
        old_keys.append((y, key, m.get('title', '')))
    else:
        buckets['pre-2010'] += 1
        old_keys.append((y, key, m.get('title', '')))
print('\nfacades by photo year:')
for k, v in buckets.items():
    print(f'  {k:<10} {v}')
if '--list' in sys.argv:
    for y, key, t in sorted(old_keys):
        print(f'  {y}  {key:<10} {t[:70]}')
