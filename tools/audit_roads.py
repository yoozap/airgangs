"""
Road position audit: BlenderGIS refs vs game roads, per district.
For every ref road vertex, distance to the nearest game road segment.
Also samples 'extra in game' buildings to classify why they had no ref match.
"""
import gzip
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

GAME = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF_DIR = '/Users/wazzap/Sites/tbilisi-game-map/chunks'
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else '/tmp/audit'

DISTRICTS = {
    "old_town":     (41.682, 44.795, 41.703, 44.825),
    "center":       (41.690, 44.775, 41.712, 44.802),
    "mtatsminda":   (41.682, 44.770, 41.703, 44.800),
    "vake":         (41.700, 44.755, 41.735, 44.795),
    "saburtalo":    (41.720, 44.758, 41.758, 44.800),
    "isani":        (41.668, 44.818, 41.698, 44.858),
    "gldani":       (41.755, 44.755, 41.790, 44.800),
    "didube":       (41.732, 44.788, 41.760, 44.820),
    "nadzaladevi":  (41.725, 44.815, 41.758, 44.858),
    "dighomi":      (41.758, 44.718, 41.790, 44.762),
    "ortachala":    (41.672, 44.800, 41.693, 44.835),
}

city = json.load(gzip.open(os.path.join(GAME, 'public/tbilisi/city.json.gz')))
meta = city['meta']
lat0, lon0 = meta['origin']['lat'], meta['origin']['lon']
M_LAT, M_LON = meta['mlat'], meta['mlon']


def road_ll(flat_dm):
    return [(lat0 + flat_dm[i + 1] / 10.0 / M_LAT, lon0 - flat_dm[i] / 10.0 / M_LON)
            for i in range(0, len(flat_dm), 2)]


# collect all game road segments (city roads + bridges + tunnels are separate lists)
segs = []  # (lat1,lon1,lat2,lon2)
for key in ('roads', 'bridges', 'tunnels'):
    for r in city.get(key, []):
        pts = road_ll(r[1:])
        for i in range(len(pts) - 1):
            segs.append((pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1]))
print(f'{len(segs)} game road segments')


def seg_dist(px, py, x1, y1, x2, y2):
    dx, dy = x2 - x1, y2 - y1
    ll = dx * dx + dy * dy
    t = 0 if ll == 0 else max(0, min(1, ((px - x1) * dx + (py - y1) * dy) / ll))
    return math.hypot(px - (x1 + t * dx), py - (y1 + t * dy))


rep = {}
for dist, (blat, blon, tlat, tlon) in DISTRICTS.items():
    def en(la, lo):
        return ((lo - blon) * M_LON, (la - blat) * M_LAT)

    cell = 100.0
    grid = {}
    dsegs = []
    for s in segs:
        if blat - 0.001 <= s[0] <= tlat + 0.001 and blon - 0.0012 <= s[1] <= tlon + 0.0012:
            x1, y1 = en(s[0], s[1])
            x2, y2 = en(s[2], s[3])
            k = len(dsegs)
            dsegs.append((x1, y1, x2, y2))
            for ci in range(int(min(x1, x2) // cell), int(max(x1, x2) // cell) + 1):
                for cj in range(int(min(y1, y2) // cell), int(max(y1, y2) // cell) + 1):
                    grid.setdefault((ci, cj), []).append(k)

    # only road classes the game intentionally keeps (build_map.py ROAD_W)
    ROAD_W = {"motorway", "trunk", "primary", "secondary", "tertiary",
              "motorway_link", "trunk_link", "primary_link", "secondary_link",
              "residential", "unclassified", "living_street"}
    ref = json.load(open(os.path.join(REF_DIR, f'{dist}_ref.json')))
    dists = []
    worst = []
    ref_pts_drawn = []
    for rr in ref['roads']:
        if rr.get('highway') not in ROAD_W:
            continue
        for la, lo in rr['rings'][0]:
            if not (blat + 0.0005 <= la <= tlat - 0.0005 and blon + 0.0006 <= lo <= tlon - 0.0006):
                continue
            px, py = en(la, lo)
            best = 1e9
            ci, cj = int(px // cell), int(py // cell)
            for di in (-1, 0, 1):
                for dj in (-1, 0, 1):
                    for k in grid.get((ci + di, cj + dj), []):
                        d = seg_dist(px, py, *dsegs[k])
                        if d < best:
                            best = d
            if best < 1e9:
                dists.append(best)
                if best > 15:
                    worst.append((round(la, 5), round(lo, 5), round(best, 1), rr.get('highway'), rr['name']))
            ref_pts_drawn.append((px, py, best))
    a = np.array(dists)
    rep[dist] = {
        'ref_road_pts': len(a),
        'median_m': float(np.median(a)),
        'p90_m': float(np.percentile(a, 90)),
        'pct_over_10m': float((a > 10).mean() * 100),
        'pct_over_20m': float((a > 20).mean() * 100),
        'worst': sorted(worst, key=lambda w: -w[2])[:8],
    }
    r = rep[dist]
    print(f"{dist:12s} pts={r['ref_road_pts']:6d} med={r['median_m']:.2f}m p90={r['p90_m']:.2f}m "
          f">10m={r['pct_over_10m']:.1f}% >20m={r['pct_over_20m']:.2f}%")

    W = (tlon - blon) * M_LON
    H = (tlat - blat) * M_LAT
    SC = min(2400 / W, 2400 / H)
    img = Image.new('RGB', (int(W * SC) + 20, int(H * SC) + 20), (14, 14, 18))
    dr = ImageDraw.Draw(img)
    for x1, y1, x2, y2 in dsegs:
        dr.line([(10 + x1 * SC, img.height - 10 - y1 * SC),
                 (10 + x2 * SC, img.height - 10 - y2 * SC)], fill=(200, 70, 70), width=2)
    for px, py, d in ref_pts_drawn:
        c = (70, 220, 110) if d < 10 else (255, 210, 60)
        dr.point((10 + px * SC, img.height - 10 - py * SC), fill=c)
    dr.text((14, 8), f'{dist} roads: game=red lines, ref=green pts (yellow >10m off)', fill=(240, 240, 240))
    img.save(os.path.join(OUT_DIR, f'{dist}_roads.png'))

with open(os.path.join(OUT_DIR, 'roads_report.json'), 'w') as f:
    json.dump(rep, f, indent=1)
print('done')
