#!/usr/bin/env python3
"""Fetch Tbilisi's 10 raioni (admin_level=6) boundaries from Overpass and
write public/tbilisi/districts.json: [{name, tag, rings:[[[lat,lon],...]]}].

Rings are outer boundaries only, stitched from relation member ways and
Douglas-Peucker simplified to ~25 m so the whole file stays tiny.
"""
import json
import math
import urllib.request
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / 'public' / 'tbilisi' / 'districts.json'
CACHE = Path(__file__).resolve().parent / 'cache' / 'districts_overpass.json'

QUERY = """
[out:json][timeout:90];
area["name:en"="Tbilisi"]["admin_level"="4"]->.tb;
rel(area.tb)["boundary"="administrative"]["admin_level"="10"];
out geom;
"""

# Clean display names (the OSM names are genitive: "ვაკის რაიონი")
CLEAN = {
    'Vake': ('ვაკე', 'VAKE'),
    'Saburtalo': ('საბურთალო', 'SABURTALO'),
    'Gldani': ('გლდანი', 'GLDANI'),
    'Didube': ('დიდუბე', 'DIDUBE'),
    'Isani': ('ისანი', 'ISANI'),
    'Krtsanisi': ('კრწანისი', 'KRTSANISI'),
    'Mtatsminda': ('მთაწმინდა', 'MTATSMINDA'),
    'Nadzaladevi': ('ნაძალადევი', 'NADZALADEVI'),
    'Samgori': ('სამგორი', 'SAMGORI'),
    'Chugureti': ('ჩუღურეთი', 'CHUGHURETI'),
}


def fetch():
    if CACHE.exists():
        return json.loads(CACHE.read_text())
    req = urllib.request.Request(
        'https://overpass-api.de/api/interpreter',
        data=QUERY.encode(),
        headers={'User-Agent': 'tbilisifly/1.0'},
    )
    with urllib.request.urlopen(req, timeout=120) as r:
        data = json.loads(r.read())
    CACHE.parent.mkdir(exist_ok=True)
    CACHE.write_text(json.dumps(data))
    return data


def stitch(ways):
    """Order outer-way segments into closed rings."""
    segs = [list(w) for w in ways if len(w) >= 2]
    rings = []
    while segs:
        ring = segs.pop(0)
        grew = True
        while grew and ring[0] != ring[-1]:
            grew = False
            for i, s in enumerate(segs):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += s[-2::-1]
                elif s[-1] == ring[0]:
                    ring = s + ring[1:]
                elif s[0] == ring[0]:
                    ring = s[::-1] + ring[1:]
                else:
                    continue
                segs.pop(i)
                grew = True
                break
        rings.append(ring)
    return rings


def simplify(pts, tol):
    """Douglas-Peucker on [lat,lon] pairs, tol in degrees."""
    if len(pts) < 3:
        return pts
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        a, b = stack.pop()
        ax, ay = pts[a]
        bx, by = pts[b]
        dmax, imax = 0.0, -1
        dx, dy = bx - ax, by - ay
        L2 = dx * dx + dy * dy
        for i in range(a + 1, b):
            px, py = pts[i]
            if L2 == 0:
                d = math.hypot(px - ax, py - ay)
            else:
                t = max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / L2))
                d = math.hypot(px - (ax + t * dx), py - (ay + t * dy))
            if d > dmax:
                dmax, imax = d, i
        if dmax > tol:
            keep[imax] = True
            stack.append((a, imax))
            stack.append((imax, b))
    return [p for p, k in zip(pts, keep) if k]


def main():
    data = fetch()
    out = []
    for rel in data.get('elements', []):
        tags = rel.get('tags', {})
        en = tags.get('name:en', '')
        key = next((k for k in CLEAN if k.lower() in en.lower()), None)
        name, tag = CLEAN.get(key, (tags.get('name', en), en.upper()))
        ways = [
            [(round(p['lat'], 6), round(p['lon'], 6)) for p in m.get('geometry', [])]
            for m in rel.get('members', [])
            if m.get('role') == 'outer' and m.get('geometry')
        ]
        rings = [simplify(r, 0.00025) for r in stitch(ways)]
        rings = [r for r in rings if len(r) >= 4]
        if not rings:
            print(f'!! {en}: no closed rings, skipped')
            continue
        out.append({'name': name, 'tag': tag, 'rings': [[list(p) for p in r] for r in rings]})
        print(f'{name} ({tag}): {len(rings)} ring(s), {sum(len(r) for r in rings)} pts')
    out.sort(key=lambda d: d['name'])
    OUT.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')))
    print(f'-> {OUT} ({OUT.stat().st_size / 1024:.0f} KB, {len(out)} districts)')


if __name__ == '__main__':
    main()
