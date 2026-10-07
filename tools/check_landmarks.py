"""
Landmark placement checker: queries fresh OSM (overpass-api.de) for each
hand-placed landmark in src/landmarks.js, prints real centroid vs game coords,
offset in meters, and the footprint's longest-edge bearing -> suggested yaw.

Game yaw convention (memory): local x=west, z=north; object front +z; yaw = -bearing.
"""
import json
import math
import os
import time

import requests

CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'cache', 'landmarks')
os.makedirs(CACHE, exist_ok=True)
API = 'https://overpass-api.de/api/interpreter'

# name, game (lat, lon, yaw), overpass filter, search center+radius
TARGETS = [
    ('tv_tower',   (41.69466, 44.77889, 0.0),
     'nwr(around:1500,41.6947,44.7789)["man_made"~"tower|mast|communications_tower"]["height"];'),
    ('sameba',     (41.69754, 44.81632, 0.5),
     'nwr(around:700,41.6975,44.8163)["building"]["name"~"სამების|Trinity",i];'),
    ('radisson',   (41.7085, 44.796, 0.15),
     'nwr(around:1200,41.7080,44.7960)["name"~"Radisson|რედისონ",i];'),
    ('chronicle',  (41.7425, 44.8105, 0.4),
     'nwr(around:2500,41.7425,44.8105)["name"~"Chronicle|მატიანე|ქრონიკ",i];'),
    ('alphabet',   (41.6559, 41.6372, 0.0),
     'nwr(around:1200,41.6559,41.6372)["name"~"Alphabet|ანბანის",i];'),
    ('ferris',     (41.6547, 41.6405, 1.2),
     'nwr(around:1000,41.6547,41.6405)["attraction"="ferris_wheel"];'),
    ('opera',      (41.7009, 44.7965, -1.25),
     'nwr(around:600,41.7009,44.7965)["name"~"ოპერისა და ბალეტის|Opera",i]["building"];'),
    ('parliament', (41.69667, 44.79805, -1.1),
     'nwr(around:400,41.6967,44.7980)["name"~"პარლამენტი|Parliament",i];'),
    ('dinamo',     (41.7231, 44.7896, 0.3),
     'nwr(around:1200,41.7231,44.7896)["leisure"="stadium"]["name"~"დინამო|Dinamo|Paichadze|ფაიჭაძე",i];'),
    ('philharmonic', (41.7048, 44.7893, 0.0),
     'nwr(around:700,41.7048,44.7893)["name"~"ფილარმონია|Philharmonic|საკონცერტო დარბაზი|Concert Hall",i];'),
    ('peace_bridge', (41.6928, 44.8085, 0.0),
     'nwr(around:500,41.6928,44.8085)["name"~"მშვიდობის ხიდი|Peace Bridge",i];'),
]

M_LAT, M_LON = 111067.92, 83206.34  # game meta at 41.7252N


def fetch(name, filt):
    path = os.path.join(CACHE, name + '.json')
    if os.path.exists(path):
        return json.load(open(path))
    q = f'[out:json][timeout:60];({filt});out tags geom;'
    r = requests.post(API, data={'data': q},
                      headers={'User-Agent': 'TbilisiFly/1.0 landmark check'}, timeout=90)
    r.raise_for_status()
    d = r.json()
    json.dump(d, open(path, 'w'))
    time.sleep(2)
    return d


def geom_pts(el):
    if el['type'] == 'node':
        return [(el['lat'], el['lon'])]
    if el['type'] == 'way':
        return [(p['lat'], p['lon']) for p in el.get('geometry', [])]
    pts = []
    for m in el.get('members', []):
        if m.get('role') in ('outer', '') and m.get('geometry'):
            pts += [(p['lat'], p['lon']) for p in m['geometry']]
    return pts


def longest_edge_bearing(pts):
    best, bb = 0, None
    for i in range(len(pts) - 1):
        (la1, lo1), (la2, lo2) = pts[i], pts[i + 1]
        de = (lo2 - lo1) * M_LON
        dn = (la2 - la1) * M_LAT
        L = math.hypot(de, dn)
        if L > best:
            best, bb = L, math.degrees(math.atan2(de, dn)) % 180
    return bb, best


for name, (glat, glon, gyaw), filt in TARGETS:
    try:
        d = fetch(name, filt)
    except Exception as e:
        print(f'{name:13s} FETCH FAILED: {e}')
        continue
    els = d.get('elements', [])
    if not els:
        print(f'{name:13s} NOT FOUND in OSM with this filter')
        continue
    print(f'{name:13s} game=({glat},{glon}) yaw={gyaw}')
    seen = set()
    for el in els[:6]:
        pts = geom_pts(el)
        if not pts:
            continue
        clat = sum(p[0] for p in pts) / len(pts)
        clon = sum(p[1] for p in pts) / len(pts)
        key = (round(clat, 4), round(clon, 4))
        if key in seen:
            continue
        seen.add(key)
        de = (clon - glon) * M_LON
        dn = (clat - glat) * M_LAT
        dist = math.hypot(de, dn)
        tags = el.get('tags', {})
        label = tags.get('name:en') or tags.get('name', '?')
        bear = ''
        if len(pts) > 2:
            b, L = longest_edge_bearing(pts)
            bear = f' longest-edge bearing={b:.0f}deg(len {L:.0f}m) yaw cands={-math.radians(b):+.2f}/{-math.radians(b - 90):+.2f}'
        print(f'   {el["type"]}/{el["id"]} "{label[:44]}" c=({clat:.5f},{clon:.5f}) '
              f'off={dist:.0f}m ({de:+.0f}E,{dn:+.0f}N){bear}')
