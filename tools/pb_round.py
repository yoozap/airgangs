"""Assemble a per-building facade round from the current crawl, RECENT ONLY.

Runs the three matchers (GPS proximity, street-address titles, building-name
titles), date-filters every result to MIN_YEAR+, drops buildings that already
have a facade, dedupes so one photo can't dress two buildings, and slices the
survivors into agent packages.

  python3 tools/pb_round.py <round-name> [min_year] [agents]

Writes tools/cache/pb_r5_state/pb_pkgs_<round>/pkg*.json (+ empty specs dir).
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(HERE, 'cache', 'pb_r5_state')

name = sys.argv[1] if len(sys.argv) > 1 else 'recent'
min_year = sys.argv[2] if len(sys.argv) > 2 else '2020'
agents = int(sys.argv[3]) if len(sys.argv) > 3 else 6

STEPS = [
    ('pb_candidates2.py', 'cache/pb_candidates_r5.json'),   # geotagged
    ('pb_geocode.py', 'cache/pb_candidates_addr.json'),     # address in title
    ('pb_namematch.py', 'cache/pb_candidates_name.json'),   # building name in title/category
]

pool = {}
for script, out in STEPS:
    print(f'\n=== {script}')
    r = subprocess.run([sys.executable, os.path.join(HERE, script)],
                       capture_output=True, text=True)
    print(r.stdout.strip()[-600:] or r.stderr.strip()[-600:])
    path = os.path.join(HERE, out)
    if not os.path.exists(path):
        continue
    subprocess.run([sys.executable, os.path.join(HERE, 'pb_datefilter.py'), path, min_year],
                   check=False)
    for e in json.load(open(path)):
        cur = pool.get(e['bid'])
        if cur is None:
            pool[e['bid']] = e
        else:  # same building found twice — keep the richer photo list
            seen = {p['title'] for p in cur['photos']}
            cur['photos'] += [p for p in e['photos'] if p['title'] not in seen]

manifest = json.load(open(os.path.join(HERE, '../public/textures/pb/manifest.json')))
done = {int(k[1:]) for k in manifest}
used = {v.get('title') for v in manifest.values()}

# one photo dresses ONE building: keep it for the building it matched best
by_photo = {}
for bid, e in sorted(pool.items()):
    e['photos'] = [p for p in e['photos'] if p['title'] not in used]
    keep = []
    for p in e['photos']:
        owner = by_photo.get(p['title'])
        if owner is None:
            by_photo[p['title']] = bid
            keep.append(p)
        elif owner == bid:
            keep.append(p)
    e['photos'] = keep[:4]

sel = [e for bid, e in sorted(pool.items()) if bid not in done and e['photos']]
print(f'\n{len(sel)} buildings with a {min_year}+ photo and no facade yet')

outdir = os.path.join(STATE, f'pb_pkgs_{name}')
os.makedirs(outdir, exist_ok=True)
os.makedirs(os.path.join(STATE, f'pb_specs_{name}'), exist_ok=True)
n = max(1, min(agents, (len(sel) + 17) // 18))
for a in range(n):
    part = sel[a::n]
    json.dump(part, open(os.path.join(outdir, f'pkg{a}.json'), 'w'), ensure_ascii=False)
    print(f'  {name} pkg{a}: {len(part)} buildings, {sum(len(e["photos"]) for e in part)} photos')
