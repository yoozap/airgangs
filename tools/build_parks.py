#!/usr/bin/env python3
"""Build public/tbilisi/parks.json from the Overpass pulls in the scratchpad.

GIS pipeline (same as BlenderGIS, minus the GUI): park/zoo/theme-park polygons
+ footpaths + fountains + playgrounds + fairground attractions, projected into
game meters (mapmeta.json origin/mlat/mlon), paths clipped to park interiors.

Inputs (fetch first, see statues-wave memory for the queries):
  $SCRATCH/parks_all.json    parks/gardens/theme parks/zoos, out geom
  $SCRATCH/park_detail.json  footways/fountains/playgrounds/attractions, out geom
"""
import json
import math
import os
import sys

SCRATCH = os.environ.get(
    'SCRATCH', '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e9077323-f05c-4fb3-8bc5-0d128f721f3c/scratchpad')
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')

meta = json.load(open(os.path.join(ROOT, 'public', 'tbilisi', 'mapmeta.json')))
OLAT, OLON = meta['origin']['lat'], meta['origin']['lon']
MLAT, MLON = meta['mlat'], meta['mlon']


def to_xz(lat, lon):
    return (-(lon - OLON) * MLON, (lat - OLAT) * MLAT)


def ring_area(pts):  # game-space m²
    a = 0
    for i in range(len(pts) - 1):
        a += pts[i][0] * pts[i + 1][1] - pts[i + 1][0] * pts[i][1]
    return abs(a) / 2


def inside(pt, ring):
    x, z = pt
    ok = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, zi = ring[i]
        xj, zj = ring[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            ok = not ok
        j = i
    return ok


parks_raw = json.load(open(os.path.join(SCRATCH, 'parks_all.json')))['elements']
detail = json.load(open(os.path.join(SCRATCH, 'park_detail.json')))['elements']
# big multipolygon parks (Vake, Mtatsminda) fetched separately with geometry
try:
    extra = json.load(open(os.path.join(SCRATCH, 'parks_rel2.json')))['elements']
    have = {(e['type'], e['id']) for e in parks_raw}
    parks_raw = [e for e in parks_raw if not any((e['type'], e['id']) == (x['type'], x['id']) for x in extra)]
    parks_raw += extra
except FileNotFoundError:
    pass


def stitch(segs):
    """Join open outer-way segments into closed rings by matching endpoints."""
    segs = [list(s) for s in segs if len(s) > 1]
    rings = []
    while segs:
        ring = segs.pop()
        while ring[0] != ring[-1]:
            for i, s in enumerate(segs):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += s[-2::-1]
                elif s[0] == ring[0]:
                    ring = ring[::-1] + s[1:]
                elif s[-1] == ring[0]:
                    ring = s + ring[1:]
                else:
                    continue
                segs.pop(i)
                break
            else:
                break  # unclosable — keep as-is
        rings.append(ring)
    return rings

# --- park polygons ----------------------------------------------------------
parks = []
for e in parks_raw:
    t = e.get('tags', {})
    kind = t.get('tourism') or t.get('leisure')
    rings = []
    if e['type'] == 'way' and 'geometry' in e:
        rings = [[to_xz(g['lat'], g['lon']) for g in e['geometry']]]
    elif e['type'] == 'relation':
        segs = [[(g['lat'], g['lon']) for g in m['geometry']]
                for m in e.get('members', []) if m.get('role') == 'outer' and 'geometry' in m]
        rings = [[to_xz(*pt) for pt in ring] for ring in stitch(segs)]
    rings = [r for r in rings if len(r) > 3]
    if not rings:
        continue
    area = sum(ring_area(r) for r in rings)
    if area < 1500:  # tiny green scraps aren't parks
        continue
    xs = [p[0] for r in rings for p in r]
    zs = [p[1] for r in rings for p in r]
    parks.append({
        'name': t.get('name:en') or t.get('name') or '',
        'kind': 'zoo' if kind == 'zoo' else ('fair' if kind == 'theme_park' else 'park'),
        'area': round(area),
        'rings': rings,
        'bbox': (min(xs), min(zs), max(xs), max(zs)),
    })
print(f'{len(parks)} park polygons kept')


def park_of(pt):
    for p in parks:
        b = p['bbox']
        if not (b[0] - 5 <= pt[0] <= b[2] + 5 and b[1] - 5 <= pt[1] <= b[3] + 5):
            continue
        if any(inside(pt, r) for r in p['rings']):
            return p
    return None


# --- clip footpaths to park interiors --------------------------------------
for p in parks:
    p['paths'] = []
    p['fountains'] = []
    p['playgrounds'] = []
    p['attractions'] = []

n_seg = 0
for e in detail:
    t = e.get('tags', {})
    if t.get('highway') not in ('footway', 'path', 'pedestrian') or 'geometry' not in e:
        continue
    pts = [to_xz(g['lat'], g['lon']) for g in e['geometry']]
    run = []
    owner = None
    for pt in pts:
        p = park_of(pt)
        if p is not None and (owner is None or p is owner):
            owner = p
            run.append(pt)
        else:
            if owner is not None and len(run) > 1:
                owner['paths'].append([(round(x, 1), round(z, 1)) for x, z in run])
                n_seg += 1
            run = [pt] if p is not None else []
            owner = p
    if owner is not None and len(run) > 1:
        owner['paths'].append([(round(x, 1), round(z, 1)) for x, z in run])
        n_seg += 1
print(f'{n_seg} in-park path segments')

# --- fountains / playgrounds / attractions ----------------------------------
loose_fountains = []
loose_playgrounds = []
for e in detail:
    t = e.get('tags', {})
    if 'geometry' in e:
        pts = [to_xz(g['lat'], g['lon']) for g in e['geometry']]
        pt = (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))
        r = max(3.0, math.sqrt(ring_area(pts) / math.pi)) if len(pts) > 3 else 3.0
    elif 'lat' in e:
        pt = to_xz(e['lat'], e['lon'])
        r = 3.0
    else:
        continue
    pt = (round(pt[0], 1), round(pt[1], 1))
    p = park_of(pt)
    if t.get('amenity') == 'fountain':
        (p['fountains'] if p else loose_fountains).append((*pt, round(min(r, 12), 1)))
    elif t.get('leisure') == 'playground':
        (p['playgrounds'] if p else loose_playgrounds).append(pt)
    elif 'attraction' in t and p:
        kind = t['attraction']
        if kind in ('big_wheel', 'carousel', 'swing_carousel', 'drop_tower', 'roller_coaster',
                    'bumper_car', 'pirate_ship', 'train', 'amusement_ride'):
            entry = {'kind': kind, 'x': pt[0], 'z': pt[1]}
            if kind == 'roller_coaster' and 'geometry' in e and len(e['geometry']) > 3:
                entry['track'] = [[round(v, 1) for v in to_xz(g['lat'], g['lon'])] for g in e['geometry']]
            p['attractions'].append(entry)

# --- micro detail: real benches, trees, tree rows, pitches, hedges, steps, ponds
micro = json.load(open(os.path.join(SCRATCH, 'park_micro.json')))['elements']


def resample(pts, step):
    out = [pts[0]]
    acc = 0.0
    for i in range(len(pts) - 1):
        x1, z1 = pts[i]
        x2, z2 = pts[i + 1]
        seg = math.hypot(x2 - x1, z2 - z1)
        d = step - acc
        while d < seg:
            t = d / seg
            out.append((x1 + (x2 - x1) * t, z1 + (z2 - z1) * t))
            d += step
        acc = (acc + seg) % step
    return out


def min_rect(pts):
    best = None
    for i in range(len(pts) - 1):
        ax, az = pts[i]
        bx, bz = pts[i + 1]
        L = math.hypot(bx - ax, bz - az)
        if L < 0.5:
            continue
        ux, uz = (bx - ax) / L, (bz - az) / L
        us = [(px * ux + pz * uz, -px * uz + pz * ux) for px, pz in pts]
        u0, u1 = min(a for a, _ in us), max(a for a, _ in us)
        v0, v1 = min(b for _, b in us), max(b for _, b in us)
        area = (u1 - u0) * (v1 - v0)
        if best is None or area < best[0]:
            cu, cv = (u0 + u1) / 2, (v0 + v1) / 2
            best = (area, cu * ux - cv * uz, cu * uz + cv * ux, u1 - u0, v1 - v0, math.atan2(ux, uz))
    return best


benches = []
rtrees = []
hedges = []
pitches = []
steps_l = []
ponds = []
for e in micro:
    t = e.get('tags', {})
    if 'lat' in e:
        pt = to_xz(e['lat'], e['lon'])
        if t.get('amenity') == 'bench':
            benches.append((round(pt[0], 1), round(pt[1], 1)))
        elif t.get('natural') == 'tree':
            rtrees.append((round(pt[0], 1), round(pt[1], 1)))
        continue
    if 'geometry' not in e:
        continue
    pts = [to_xz(g['lat'], g['lon']) for g in e['geometry']]
    if t.get('natural') == 'tree_row':
        rtrees += [(round(x, 1), round(z, 1)) for x, z in resample(pts, 8)]
    elif t.get('barrier') == 'hedge':
        for i in range(len(pts) - 1):
            if math.hypot(pts[i+1][0]-pts[i][0], pts[i+1][1]-pts[i][1]) > 1:
                hedges.append([round(v, 1) for v in (*pts[i], *pts[i + 1])])
    elif t.get('leisure') == 'pitch':
        r = min_rect(pts)
        if r and 8 < r[3] < 130 and 4 < r[4] < 90:
            pitches.append({'x': round(r[1], 1), 'z': round(r[2], 1), 'w': round(r[3], 1),
                            'h': round(r[4], 1), 'a': round(r[5], 3),
                            's': t.get('sport', '')[:16]})
    elif t.get('highway') == 'steps':
        steps_l.append([[round(x, 1), round(z, 1)] for x, z in pts])
    elif t.get('natural') == 'water' and len(pts) > 3:
        a = ring_area(pts)
        if 30 < a < 15000:
            ponds.append([[round(x, 1), round(z, 1)] for x, z in pts])

# canopy fill: parks should read tree-covered like the satellite — sample
# interior points away from paths (procedural where mappers drew no trees)
canopy = []
for p in parks:
    if p['kind'] == 'fair':
        continue
    b = p['bbox']
    segs = [(a, b2) for path in p['paths'] for a, b2 in zip(path, path[1:])]
    step = 15
    nx = int((b[2] - b[0]) / step)
    nz = int((b[3] - b[1]) / step)
    for i in range(nx + 1):
        for j in range(nz + 1):
            hx = (i * 73856093 ^ j * 19349663) % 1000 / 1000
            if hx > 0.62:
                continue
            x = b[0] + i * step + (hx - 0.5) * 9
            z = b[1] + j * step + ((i * 83492791 ^ j * 2971215073) % 1000 / 1000 - 0.5) * 9
            if not any(inside((x, z), r) for r in p['rings']):
                continue
            ok = True
            for (ax, az), (bx2, bz2) in segs:
                L2 = (bx2 - ax) ** 2 + (bz2 - az) ** 2
                if L2 < 0.1:
                    continue
                tt = max(0, min(1, ((x - ax) * (bx2 - ax) + (z - az) * (bz2 - az)) / L2))
                if (x - (ax + (bx2 - ax) * tt)) ** 2 + (z - (az + (bz2 - az) * tt)) ** 2 < 20:
                    ok = False
                    break
            if ok:
                canopy.append((round(x, 1), round(z, 1)))

# parks with real mapped furniture keep it authoritative — no procedural rows
for p in parks:
    nb = sum(1 for bx, bz in benches
             if p['bbox'][0] <= bx <= p['bbox'][2] and p['bbox'][1] <= bz <= p['bbox'][3])
    nt = sum(1 for tx, tz in rtrees
             if p['bbox'][0] <= tx <= p['bbox'][2] and p['bbox'][1] <= tz <= p['bbox'][3])
    p['real_benches'] = nb
    p['real_trees'] = nt

print(f'micro: {len(benches)} benches, {len(rtrees)} real trees, {len(canopy)} canopy fill, '
      f'{len(pitches)} pitches, {len(hedges)} hedge segs, {len(steps_l)} stair ways, {len(ponds)} ponds')

out = {
    'benches': benches,
    'trees': rtrees,
    'canopy': canopy,
    'pitches': pitches,
    'hedges': hedges,
    'steps': steps_l,
    'ponds': ponds,
    'parks': [
        {
            'name': p['name'], 'kind': p['kind'], 'area': p['area'],
            'outline': [[[round(x, 1), round(z, 1)] for x, z in r] for r in p['rings']],
            'paths': p['paths'], 'fountains': p['fountains'],
            'playgrounds': p['playgrounds'], 'attractions': p['attractions'],
            'furnished': p['real_benches'] >= 3 or p['real_trees'] >= 10,
        }
        for p in parks
    ],
    'fountains': loose_fountains,      # squares/courtyards outside parks
    'playgrounds': loose_playgrounds,  # courtyard playgrounds
}
dst = os.path.join(ROOT, 'public', 'tbilisi', 'parks.json')
json.dump(out, open(dst, 'w'), separators=(',', ':'))
nf = sum(len(p['fountains']) for p in parks)
na = sum(len(p['attractions']) for p in parks)
npg = sum(len(p['playgrounds']) for p in parks)
print(f'wrote {dst}: {len(parks)} parks, {nf}+{len(loose_fountains)} fountains, '
      f'{npg}+{len(loose_playgrounds)} playgrounds, {na} attractions, {os.path.getsize(dst)//1024} KB')
for p in parks:
    if p['kind'] != 'park' or p['area'] > 40000:
        print(f"  {p['kind']:4s} {p['area']/10000:6.1f} ha  {p['name'][:46]}  paths={len(p['paths'])} attr={len(p['attractions'])}")
