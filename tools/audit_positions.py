"""
Position audit: compare game buildings/roads (public/tbilisi/city.json.gz)
against BlenderGIS ground-truth dumps (~/Sites/tbilisi-game-map/chunks/<d>_ref.json),
district by district.

Outputs per district:
  - offset stats (median / p90 distance, systematic east/north bias)
  - unmatched counts (missing in game / extra in game)
  - overlay PNG (ref = green, game = red) in the scratchpad dir

Read-only with respect to the game: touches no game assets.
"""
import gzip
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

GAME = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF_DIR = '/Users/wazzap/Sites/tbilisi-game-map/chunks'
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else '/tmp/audit'
os.makedirs(OUT_DIR, exist_ok=True)

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

print('loading city.json.gz ...')
city = json.load(gzip.open(os.path.join(GAME, 'public/tbilisi/city.json.gz')))
meta = city['meta']
lat0, lon0 = meta['origin']['lat'], meta['origin']['lon']
M_LAT, M_LON = meta['mlat'], meta['mlon']

# per-district local frame: meters east/north of district bbox corner
def ll_to_en(lat, lon, blat, blon):
    return ((lon - blon) * M_LON, (lat - blat) * M_LAT)

def game_ring_ll(flat_dm):
    """game flat [x,z,...] dm -> [(lat,lon),...]  (X=west, Z=north)"""
    pts = []
    for i in range(0, len(flat_dm), 2):
        x, z = flat_dm[i] / 10.0, flat_dm[i + 1] / 10.0
        pts.append((lat0 + z / M_LAT, lon0 - x / M_LON))
    return pts

def centroid_area(en_pts):
    """polygon centroid + area (shoelace); falls back to mean for degenerate."""
    a = cx = cy = 0.0
    n = len(en_pts)
    for i in range(n):
        x1, y1 = en_pts[i]
        x2, y2 = en_pts[(i + 1) % n]
        cr = x1 * y2 - x2 * y1
        a += cr
        cx += (x1 + x2) * cr
        cy += (y1 + y2) * cr
    if abs(a) < 1e-6:
        xs = [p[0] for p in en_pts]; ys = [p[1] for p in en_pts]
        return sum(xs) / n, sum(ys) / n, 0.0
    a *= 0.5
    return cx / (6 * a), cy / (6 * a), abs(a)

# pre-extract all game buildings once: centroid lat/lon + ring
print('indexing game buildings ...')
game_b = []          # (lat, lon, area, ring_ll)
for b in city['buildings']:
    ring = game_ring_ll(b[1:])
    en = [((p[1] - lon0) * M_LON, (p[0] - lat0) * M_LAT) for p in ring]
    cx, cy, ar = centroid_area(en)
    game_b.append((lat0 + cy / M_LAT, lon0 + cx / M_LON, ar, ring))
# churches/mosques are stored separately as [cx_dm, cz_dm, area] — include as centroids
worship = []
for lst in (city.get('churches', []), city.get('mosques', [])):
    for w in lst:
        x, z = w[0] / 10.0, w[1] / 10.0
        worship.append((lat0 + z / M_LAT, lon0 - x / M_LON, w[2] if len(w) > 2 else 0))
game_lat = np.array([g[0] for g in game_b])
game_lon = np.array([g[1] for g in game_b])
print(f'  {len(game_b)} game buildings, {len(worship)} worship centroids')

MATCH_R = 40.0  # meters: search radius for centroid match

report = {}
for dist, (blat, blon, tlat, tlon) in DISTRICTS.items():
    ref = json.load(open(os.path.join(REF_DIR, f'{dist}_ref.json')))
    # game buildings inside bbox (small margin)
    mlat = 20 / M_LAT
    mlon = 20 / M_LON
    sel = np.where((game_lat >= blat - mlat) & (game_lat <= tlat + mlat) &
                   (game_lon >= blon - mlon) & (game_lon <= tlon + mlon))[0]
    g_en = np.array([ll_to_en(game_lat[i], game_lon[i], blat, blon) for i in sel]) \
        if len(sel) else np.zeros((0, 2))
    g_area = np.array([game_b[i][2] for i in sel])
    w_en = np.array([ll_to_en(la, lo, blat, blon) for la, lo, _ in worship
                     if blat <= la <= tlat and blon <= lo <= tlon])

    # grid index of game centroids
    cell = 50.0
    grid = {}
    for k, (x, y) in enumerate(g_en):
        grid.setdefault((int(x // cell), int(y // cell)), []).append(k)

    offs = []          # (dE, dN) ref -> matched game
    unmatched = []     # ref buildings with no game building nearby
    matched_g = set()
    ref_items = []     # (cx, cy, area, rings_en) for drawing
    for rb in ref['buildings']:
        ring = rb['rings'][0]
        en = [ll_to_en(la, lo, blat, blon) for la, lo in ring]
        cx, cy, ar = centroid_area(en)
        ref_items.append((cx, cy, ar, [[ll_to_en(la, lo, blat, blon) for la, lo in r]
                                       for r in rb['rings']]))
        # only judge refs whose centroid is inside the bbox proper
        rla, rlo = ring[0]
        if not (blat <= cy / M_LAT + blat <= tlat):  # placeholder, computed below
            pass
        best, bestd = -1, MATCH_R
        ci, cj = int(cx // cell), int(cy // cell)
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                for k in grid.get((ci + di, cj + dj), []):
                    d = math.hypot(g_en[k][0] - cx, g_en[k][1] - cy)
                    if d < bestd:
                        # require some area sanity when both have area
                        if ar > 1 and g_area[k] > 1 and not (0.2 < g_area[k] / ar < 5):
                            continue
                        best, bestd = k, d
        # inside-bbox test in lat/lon
        rlat = blat + cy / M_LAT
        rlon = blon + cx / M_LON
        inside = (blat + 0.0005 <= rlat <= tlat - 0.0005) and (blon + 0.0006 <= rlon <= tlon - 0.0006)
        if best >= 0:
            matched_g.add(best)
            if inside:
                offs.append((g_en[best][0] - cx, g_en[best][1] - cy))
        elif inside:
            # worship buildings live in separate lists — check those before calling it missing
            wnear = len(w_en) and np.min(np.hypot(w_en[:, 0] - cx, w_en[:, 1] - cy)) < MATCH_R
            if not wnear:
                unmatched.append((cx, cy, ar))

    offs = np.array(offs) if offs else np.zeros((0, 2))
    dists = np.hypot(offs[:, 0], offs[:, 1]) if len(offs) else np.array([])
    extra = [k for k in range(len(g_en)) if k not in matched_g]
    # extras only counted well inside bbox
    extra_in = [k for k in extra
                if blat + 0.0005 <= game_lat[sel[k]] <= tlat - 0.0005
                and blon + 0.0006 <= game_lon[sel[k]] <= tlon - 0.0006]

    rep = {
        'ref_buildings': len(ref['buildings']),
        'game_buildings_in_bbox': int(len(sel)),
        'matched': int(len(offs)),
        'missing_in_game': len(unmatched),
        'extra_in_game': len(extra_in),
        'median_offset_m': float(np.median(dists)) if len(dists) else None,
        'p90_offset_m': float(np.percentile(dists, 90)) if len(dists) else None,
        'max_offset_m': float(dists.max()) if len(dists) else None,
        'bias_east_m': float(np.median(offs[:, 0])) if len(offs) else None,
        'bias_north_m': float(np.median(offs[:, 1])) if len(offs) else None,
        'pct_over_5m': float((dists > 5).mean() * 100) if len(dists) else None,
        'pct_over_10m': float((dists > 10).mean() * 100) if len(dists) else None,
    }
    report[dist] = rep
    print(f"{dist:12s} ref={rep['ref_buildings']:5d} game={rep['game_buildings_in_bbox']:5d} "
          f"matched={rep['matched']:5d} miss={rep['missing_in_game']:4d} extra={rep['extra_in_game']:4d} "
          f"med={rep['median_offset_m']:.2f}m p90={rep['p90_offset_m']:.2f}m "
          f"bias=({rep['bias_east_m']:+.2f}E,{rep['bias_north_m']:+.2f}N) "
          f">5m={rep['pct_over_5m']:.1f}%")

    # ---- overlay image: ref green, game red ----
    W = (tlon - blon) * M_LON
    H = (tlat - blat) * M_LAT
    SCALE = min(2400 / W, 2400 / H)
    img = Image.new('RGB', (int(W * SCALE) + 20, int(H * SCALE) + 20), (14, 14, 18))
    dr = ImageDraw.Draw(img, 'RGBA')

    def px(x, y):
        return (10 + x * SCALE, img.height - 10 - y * SCALE)

    for cx, cy, ar, rings in ref_items:
        for r in rings:
            if len(r) >= 3:
                dr.polygon([px(x, y) for x, y in r], outline=(70, 220, 110, 255))
    for k in range(len(g_en)):
        ring = game_b[sel[k]][3]
        en = [ll_to_en(la, lo, blat, blon) for la, lo in ring]
        if len(en) >= 3:
            dr.polygon([px(x, y) for x, y in en], outline=(235, 80, 80, 200))
    for cx, cy, ar in unmatched:
        x, y = px(cx, cy)
        dr.ellipse([x - 6, y - 6, x + 6, y + 6], outline=(255, 210, 60, 255), width=2)
    dr.text((14, 8), f'{dist}  ref(green)={len(ref_items)}  game(red)={len(g_en)}  '
            f'yellow=missing({len(unmatched)})', fill=(240, 240, 240))
    img.save(os.path.join(OUT_DIR, f'{dist}.png'))

with open(os.path.join(OUT_DIR, 'report.json'), 'w') as f:
    json.dump(report, f, indent=1)
print('done ->', OUT_DIR)
