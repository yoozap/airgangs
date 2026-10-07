"""Drop pre-MIN_YEAR photos from a candidate file before it goes to review.

The game must show Tbilisi as it looks now, so every NEW facade should come
from a recent photo. Commons dates live in extmetadata (DateTimeOriginal),
which the crawl does not fetch, so run this on any candidate file first:

  python3 tools/pb_datefilter.py tools/cache/pb_candidates_x.json [2020]

Rewrites the file in place, keeping only photos dated MIN_YEAR or newer (and,
with --keep-undated, ones Commons has no date for). Buildings left with no
photo are removed. Dates are cached in tools/cache/pb_photo_dates.json.
"""
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DATES = os.path.join(HERE, 'cache', 'pb_photo_dates.json')
API = 'https://commons.wikimedia.org/w/api.php'
UA = 'TbilisiFlyGame/1.0 (personal game project; sergo93@gmail.com)'


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


def year_of(titles, dates):
    """Fill `dates` for any unknown titles (25 per POST — long Georgian titles
    overflow a GET URL)."""
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
            m = re.search(r'(19|20)\d{2}', raw)
            dates[t] = int(m.group(0)) if m else None
        json.dump(dates, open(DATES, 'w'), ensure_ascii=False)
        print(f'  dated {i + len(batch)}/{len(todo)}')
        time.sleep(0.3)
    return dates


def main():
    path = sys.argv[1]
    min_year = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 2020
    keep_undated = '--keep-undated' in sys.argv
    cand = json.load(open(path))
    dates = json.load(open(DATES)) if os.path.exists(DATES) else {}
    titles = {p['title'] for e in cand for p in e['photos']}
    print(f'{len(cand)} buildings, {len(titles)} photos; dating…')
    year_of(sorted(titles), dates)

    kept, dropped_photos = [], 0
    for e in cand:
        photos = []
        for p in e['photos']:
            y = dates.get(p['title'])
            if y is None and keep_undated:
                photos.append(dict(p, addr=p.get('addr', 'undated')))
            elif y is not None and y >= min_year:
                photos.append(dict(p, addr=f'{y} photo'))
            else:
                dropped_photos += 1
        if photos:
            photos.sort(key=lambda p: -(dates.get(p['title']) or 0))
            kept.append(dict(e, photos=photos))
    json.dump(kept, open(path, 'w'), ensure_ascii=False)
    print(f'kept {len(kept)}/{len(cand)} buildings, dropped {dropped_photos} pre-{min_year} photos -> {path}')


if __name__ == '__main__':
    main()
