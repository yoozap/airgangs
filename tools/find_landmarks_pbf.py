"""
Offline landmark finder: scans the local Geofabrik PBF (same data the game is
built from) for every hand-placed landmark in src/landmarks.js, by name regex.
Prints real centroid, offset from the game's current coords, and the footprint's
longest-edge bearing with yaw candidates (game convention: yaw = -bearing).
"""
import math
import os
import re

import osmium

PBF = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'cache', 'georgia-latest.osm.pbf')
M_LAT, M_LON = 111067.92, 83206.34

# name, game(lat, lon, yaw), regex, (clat, clon, max_km)
TARGETS = [
    ('tv_tower',     (41.69466, 44.77889, 0.00), r'ტელეანძა|TV Broadcasting|TV Tower', (41.695, 44.78, 3)),
    ('sameba',       (41.69754, 44.81632, 0.50), r'სამების საკათედრო|Trinity Cathedral', (41.6975, 44.816, 2)),
    ('radisson_tbi', (41.70850, 44.79600, 0.15), r'Radisson|რედისონ', (41.708, 44.796, 3)),
    ('chronicle',    (41.74250, 44.81050, 0.40), r'მატიანე|Chronicle of Georgia|ქრონიკები', (41.7425, 44.8105, 4)),
    ('alphabet',     (41.65590, 41.63720, 0.00), r'ანბანის კოშკი|Alphabet Tower|Alphabetic Tower', (41.656, 41.637, 3)),
    ('ferris',       (41.65470, 41.64050, 1.20), r'ferris|ეშმაკის ბორბალი', (41.6547, 41.6405, 3)),
    ('opera',        (41.70090, 44.79650, -1.25), r'ოპერისა და ბალეტის|Opera', (41.7009, 44.7965, 2)),
    ('parliament',   (41.69667, 44.79805, -1.10), r'საქართველოს პარლამენტი|Parliament of Georgia', (41.6967, 44.798, 1.5)),
    ('dinamo',       (41.72310, 44.78960, 0.30), r'დინამო არენა|Dinamo Arena|ფაიჭაძე|Paichadze', (41.7231, 44.7896, 3)),
    ('philharmonic', (41.70480, 44.78930, 0.00), r'ფილარმონია|Philharmonic|საკონცერტო დარბაზ|Concert Hall', (41.7048, 44.7893, 2)),
    ('peace_bridge', (41.69280, 44.80850, 0.00), r'მშვიდობის ხიდი|Bridge of Peace|Peace Bridge', (41.6928, 44.8085, 1.5)),
]
COMP = [(n, g, re.compile(rx, re.I), c) for n, g, rx, c in TARGETS]

results = {n: [] for n, *_ in TARGETS}


def name_blob(t):
    return ' | '.join(v for k, v in t.items() if k == 'name' or k.startswith('name:')
                      or k in ('attraction', 'man_made'))


def consider(kind, oid, tags, pts):
    if not pts:
        return
    blob = name_blob(tags)
    if not blob:
        return
    clat = sum(p[0] for p in pts) / len(pts)
    clon = sum(p[1] for p in pts) / len(pts)
    for n, g, rx, (tlat, tlon, km) in COMP:
        if not rx.search(blob):
            continue
        if math.hypot((clat - tlat) * 111, (clon - tlon) * 83) > km:
            continue
        results[n].append((kind, oid, tags.get('name:en') or tags.get('name', '?'),
                           clat, clon, pts))


class H(osmium.SimpleHandler):
    def node(self, n):
        if n.tags and ('name' in n.tags or 'attraction' in n.tags):
            t = dict(n.tags)
            if 'ferris' in t.get('attraction', '') or any(rx.search(name_blob(t)) for _, _, rx, _ in COMP):
                consider('node', n.id, t, [(n.location.lat, n.location.lon)])

    def area(self, a):
        t = dict(a.tags)
        blob = name_blob(t)
        if not blob:
            return
        if not any(rx.search(blob) for _, _, rx, _ in COMP):
            return
        for outer in a.outer_rings():
            pts = [(nd.lat, nd.lon) for nd in outer if nd.location.valid()]
            consider('area', a.orig_id(), t, pts)
            break

    def way(self, w):
        t = dict(w.tags)
        if not t:
            return
        blob = name_blob(t)
        if not blob or not any(rx.search(blob) for _, _, rx, _ in COMP):
            return
        try:
            pts = [(nd.location.lat, nd.location.lon) for nd in w.nodes if nd.location.valid()]
        except osmium.InvalidLocationError:
            return
        if w.is_closed():
            return  # closed ways come through area()
        consider('way', w.id, t, pts)


print('scanning PBF ...')
H().apply_file(PBF, locations=True, idx='flex_mem')

for n, (glat, glon, gyaw), rx, c in COMP:
    print(f'\n== {n}: game=({glat}, {glon}) yaw={gyaw}')
    if not results[n]:
        print('   NOT FOUND')
        continue
    seen = set()
    for kind, oid, label, clat, clon, pts in results[n]:
        key = (round(clat, 4), round(clon, 4))
        if key in seen:
            continue
        seen.add(key)
        de = (clon - glon) * M_LON
        dn = (clat - glat) * M_LAT
        extra = ''
        if len(pts) > 2:
            best, bb = 0, 0
            for i in range(len(pts) - 1):
                e = (pts[i + 1][1] - pts[i][1]) * M_LON
                no = (pts[i + 1][0] - pts[i][0]) * M_LAT
                L = math.hypot(e, no)
                if L > best:
                    best, bb = L, math.degrees(math.atan2(e, no)) % 180
            dims = (max(p[1] for p in pts) - min(p[1] for p in pts)) * M_LON, \
                   (max(p[0] for p in pts) - min(p[0] for p in pts)) * M_LAT
            extra = (f' bbox={dims[0]:.0f}x{dims[1]:.0f}m edge-bearing={bb:.0f} '
                     f'yaw cands={-math.radians(bb):+.2f}/{-math.radians(bb - 90):+.2f}')
        print(f'   {kind}/{oid} "{label[:40]}" ({clat:.5f}, {clon:.5f}) '
              f'off={math.hypot(de, dn):.0f}m ({de:+.0f}E,{dn:+.0f}N){extra}')
