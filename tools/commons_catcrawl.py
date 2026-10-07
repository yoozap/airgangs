"""
Commons CATEGORY-TREE crawl for Tbilisi building photos — reaches the photos
the geosearch sweep could not see (files with no geotag, categorized under
street/building categories instead). BFS over subcategories from building-ish
seeds, then batch imageinfo (+ page coordinates where present).

Output: tools/cache/commons_catcrawl.json
  {title: {url, w, h, lic, artist, lat, lon, cats:[...]}}   (lat/lon may be null)
Resumable: reruns skip already-fetched titles.
"""
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'cache/commons_catcrawl.json')
API = 'https://commons.wikimedia.org/w/api.php'
UA = 'TbilisiFlyGame/1.0 (personal game project; sergo93@gmail.com)'

SEEDS = [
    'Category:Buildings in Tbilisi',
    'Category:Houses in Tbilisi',
    'Category:Architecture of Tbilisi',
    'Category:Cultural heritage monuments in Tbilisi',
    'Category:Streets in Tbilisi',
    'Category:Squares in Tbilisi',
    'Category:Shopping malls in Tbilisi',
    'Category:Shopping centers in Tbilisi',
    'Category:Hotels in Tbilisi',
    'Category:Schools in Tbilisi',
    'Category:Universities in Tbilisi',
    'Category:Hospitals in Tbilisi',
    'Category:Restaurants in Tbilisi',
    'Category:Shops in Georgia (country)',
    'Category:Markets in Tbilisi',
    'Category:McDonald\'s in Georgia (country)',
    'Category:Police in Georgia (country)',
    'Category:Cinemas in Tbilisi',
    'Category:Theatres in Tbilisi',
    'Category:Railway stations in Tbilisi',
    'Category:Districts of Tbilisi',
    # YEAR categories: the surest source of RECENT photos (the game must show
    # Tbilisi as it looks now — see the recency rule in the pipeline memory).
    'Category:2020 in Tbilisi', 'Category:2021 in Tbilisi',
    'Category:2022 in Tbilisi', 'Category:2023 in Tbilisi',
    'Category:2024 in Tbilisi', 'Category:2025 in Tbilisi',
    'Category:2026 in Tbilisi',
]

# subcategory branches that cannot yield facade photos
SKIP_CAT = re.compile(
    r'interior|inside|People |views? from|panoram|metro station|graffiti|'
    r'plaque|memorial plaque|doors? of|windows? of|details? of|frescos?|'
    r'sculptures|statues|monuments of|paintings|museum collection|'
    r'demolish|construction of|models of|maps of|aerial|night in',
    re.I)
# file titles that are clearly not facades (looser than pb_candidates JUNK —
# the agent review is the real filter; this only drops obvious garbage)
SKIP_FILE = re.compile(
    r'interior|inside|plaque|dashboard|menu|food|dish|logo|map|plan\b|'
    r'ceiling|fresco|icon(?! house)|grave|tomb|portrait|selfie',
    re.I)

MAX_DEPTH = 3
MAX_FILES = 40000


def api(params, tries=5):
    # POST, not GET: a batch of 50 Georgian titles percent-escapes into a URL
    # far past the 8 kB limit and every one of those batches came back 414,
    # silently dropping the photos in it.
    data = urllib.parse.urlencode(dict(params, format='json')).encode()
    for a in range(tries):
        try:
            req = urllib.request.Request(API, data=data, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=40) as r:
                return json.load(r)
        except Exception as e:
            wait = 2 * (a + 1)
            print(f'  api retry {a + 1} ({e}) sleep {wait}', flush=True)
            time.sleep(wait)
    return None


def main():
    done = {}
    if os.path.exists(OUT):
        done = json.load(open(OUT))
        print(f'resuming with {len(done)} files already fetched', flush=True)

    # -------- BFS the category tree --------
    seen_cats = set()
    file_cats = {}  # title -> [category names]
    queue = [(c, 0) for c in SEEDS]
    while queue:
        cat, depth = queue.pop(0)
        if cat in seen_cats:
            continue
        seen_cats.add(cat)
        cont = {}
        while True:
            res = api({
                'action': 'query', 'list': 'categorymembers',
                'cmtitle': cat, 'cmtype': 'subcat|file',
                'cmlimit': '500', **cont,
            })
            if not res:
                break
            for m in res.get('query', {}).get('categorymembers', []):
                t = m['title']
                if t.startswith('Category:'):
                    if depth < MAX_DEPTH and not SKIP_CAT.search(t):
                        queue.append((t, depth + 1))
                elif t.lower().endswith(('.jpg', '.jpeg', '.png')):
                    if not SKIP_FILE.search(t):
                        file_cats.setdefault(t, []).append(cat[9:])
            cont = res.get('continue', {})
            if not cont:
                break
            time.sleep(0.25)
        time.sleep(0.25)
        if len(file_cats) > MAX_FILES:
            print('file cap hit, stopping BFS', flush=True)
            break
        if len(seen_cats) % 50 == 0:
            print(f'cats {len(seen_cats)} queue {len(queue)} files {len(file_cats)}', flush=True)

    print(f'BFS done: {len(seen_cats)} categories, {len(file_cats)} files', flush=True)

    # -------- batch imageinfo + coordinates --------
    todo = [t for t in file_cats if t not in done]
    print(f'{len(todo)} files to fetch info for', flush=True)
    for i in range(0, len(todo), 25):
        batch = todo[i:i + 25]
        res = api({
            'action': 'query', 'titles': '|'.join(batch),
            'prop': 'imageinfo|coordinates',
            'iiprop': 'url|size|extmetadata',
            'iiextmetadatafilter': 'LicenseShortName|Artist',
            'coprop': 'type', 'colimit': '500',
        })
        if not res:
            continue
        for page in res.get('query', {}).get('pages', {}).values():
            t = page.get('title')
            ii = (page.get('imageinfo') or [{}])[0]
            em = ii.get('extmetadata', {})
            co = page.get('coordinates') or []
            lat = lon = None
            for c in co:
                if c.get('type') == 'camera' or lat is None:
                    lat, lon = c.get('lat'), c.get('lon')
            done[t] = {
                'url': ii.get('url'),
                'w': ii.get('width'), 'h': ii.get('height'),
                'lic': (em.get('LicenseShortName') or {}).get('value'),
                'artist': re.sub(r'<[^>]+>', '', (em.get('Artist') or {}).get('value') or '').strip(),
                'lat': lat, 'lon': lon,
                'cats': file_cats.get(t, []),
            }
        if (i // 25) % 20 == 0:
            json.dump(done, open(OUT, 'w'))
            print(f'info {i + len(batch)}/{len(todo)}', flush=True)
        time.sleep(0.3)

    json.dump(done, open(OUT, 'w'))
    with_gps = sum(1 for v in done.values() if v.get('lat'))
    print(f'DONE: {len(done)} files, {with_gps} with coordinates -> {OUT}', flush=True)


if __name__ == '__main__':
    main()
