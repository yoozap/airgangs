"""Slice pb_candidates.json into agent work packages (top-N by quality)."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = sys.argv[4] if len(sys.argv) > 4 else os.path.join(HERE, 'cache/pb_candidates.json')
cand = json.load(open(SRC))
TOP = int(sys.argv[1]) if len(sys.argv) > 1 else 300
AGENTS = int(sys.argv[2]) if len(sys.argv) > 2 else 6
outdir = sys.argv[3] if len(sys.argv) > 3 else '/tmp/pb_slices'
os.makedirs(outdir, exist_ok=True)

# dedupe: one building per photo title (a photo shows ONE building — keep the
# nearest/biggest match so ten neighbours don't all wear the same facade)
best_for_photo = {}
for e in cand:
    t = e['photos'][0]['title']
    cur = best_for_photo.get(t)
    if cur is None or (e['photos'][0]['dist'], -e['area']) < (cur['photos'][0]['dist'], -cur['area']):
        best_for_photo[t] = e
sel = sorted(best_for_photo.values(), key=lambda e: (e['photos'][0]['dist'], -e['area']))[:TOP]

for a in range(AGENTS):
    sl = sel[a::AGENTS]
    json.dump(sl, open(os.path.join(outdir, f'slice{a}.json'), 'w'), ensure_ascii=False)
    print(f'slice{a}: {len(sl)} buildings')
print(f'total {len(sel)} (from {len(cand)} candidates, {len(best_for_photo)} unique photos)')
