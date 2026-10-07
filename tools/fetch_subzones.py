#!/usr/bin/env python3
"""Fetch Tbilisi neighborhood polygons (place=suburb/quarter/neighbourhood
ways+relations) from Overpass -> public/tbilisi/subzones.json:
[{name, tag, area, rings}] sorted by area ascending, so the first
point-in-polygon hit is the smallest (most specific) zone.
"""
import json
import math
import time
import urllib.request
from pathlib import Path

from fetch_districts import simplify, stitch

OUT = Path(__file__).resolve().parent.parent / 'public' / 'tbilisi' / 'subzones.json'
CACHE = Path(__file__).resolve().parent / 'cache' / 'subzones_overpass.json'

QUERY = """
[out:json][timeout:120];
area["name:en"="Tbilisi"]["admin_level"="4"]->.tb;
(
  way(area.tb)["place"~"suburb|quarter|neighbourhood"];
  rel(area.tb)["place"~"suburb|quarter|neighbourhood"];
);
out geom;
"""

SERVERS = [
    'https://overpass-api.de/api/interpreter',
    'https://overpass.kumi.systems/api/interpreter',
]


def fetch():
    if CACHE.exists():
        return json.loads(CACHE.read_text())
    last = None
    for url in SERVERS:
        try:
            req = urllib.request.Request(url, data=QUERY.encode(), headers={'User-Agent': 'tbilisifly/1.0'})
            with urllib.request.urlopen(req, timeout=180) as r:
                data = json.loads(r.read())
            if data.get('elements'):
                CACHE.parent.mkdir(exist_ok=True)
                CACHE.write_text(json.dumps(data))
                return data
        except Exception as e:  # busy server: try the next one
            last = e
            time.sleep(5)
    raise SystemExit(f'overpass failed: {last}')


def ring_area(ring):
    """Shoelace in degree² (comparison only)."""
    a = 0.0
    for i in range(len(ring) - 1):
        a += ring[i][1] * ring[i + 1][0] - ring[i + 1][1] * ring[i][0]
    return abs(a) / 2


def latinize(en):
    return ''.join(c for c in en.upper() if c.isalpha() or c == ' ').strip()


def main():
    data = fetch()
    out = []
    for el in data.get('elements', []):
        tags = el.get('tags', {})
        name = tags.get('name')
        if not name:
            continue
        # some raions carry a place=suburb tag too — that's the district layer's job
        if 'რაიონი' in name or 'District' in tags.get('name:en', ''):
            continue
        if el['type'] == 'way':
            ways = [[(round(p['lat'], 6), round(p['lon'], 6)) for p in el.get('geometry', [])]]
        else:
            ways = [
                [(round(p['lat'], 6), round(p['lon'], 6)) for p in m.get('geometry', [])]
                for m in el.get('members', [])
                if m.get('role') in ('outer', '') and m.get('type') == 'way' and m.get('geometry')
            ]
        rings = [simplify(r, 0.00015) for r in stitch(ways)]
        rings = [r for r in rings if len(r) >= 4 and r[0] == r[-1]]
        if not rings:
            continue
        tag = latinize(tags.get('name:en', '')) or name
        out.append({
            'name': name,
            'tag': tag,
            'area': round(sum(ring_area(r) for r in rings), 9),
            'rings': [[list(p) for p in r] for r in rings],
        })
    # duplicates (a way and a relation for the same hood): keep the larger version
    seen = {}
    for z in sorted(out, key=lambda z: -z['area']):
        if z['name'] not in seen:
            seen[z['name']] = z
    out = sorted(seen.values(), key=lambda z: z['area'])
    OUT.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')))
    print(f'{len(out)} subzones -> {OUT} ({OUT.stat().st_size / 1024:.0f} KB)')
    for z in out[:8]:
        print('  smallest:', z['name'], z['tag'])
    for z in out[-4:]:
        print('  largest:', z['name'], z['tag'])


if __name__ == '__main__':
    main()
