"""Min-area-rect orientation for specific landmark footprints (from district .osm chunks + PBF)."""
import math
import os
import xml.etree.ElementTree as ET

M_LAT, M_LON = 111067.92, 83206.34
CH = '/Users/wazzap/Sites/tbilisi-game-map/chunks'

WAYS = {  # way id -> district file
    585352693: 'old_town',    # sameba cathedral
    1299930496: 'center',     # radisson blu iveria
    59151604: 'center',       # opera
    17823425: 'saburtalo',    # dinamo arena
    125589004: 'old_town',    # bridge of peace main span
}


def min_rect(pts_en):
    hullpts = pts_en
    best = None
    for i in range(len(hullpts) - 1):
        ex = hullpts[i + 1][0] - hullpts[i][0]
        ey = hullpts[i + 1][1] - hullpts[i][1]
        L = math.hypot(ex, ey)
        if L < 0.5:
            continue
        ux, uy = ex / L, ey / L
        us = [p[0] * ux + p[1] * uy for p in hullpts]
        vs = [-p[0] * uy + p[1] * ux for p in hullpts]
        w, h = max(us) - min(us), max(vs) - min(vs)
        if best is None or w * h < best[0]:
            bearing = math.degrees(math.atan2(ux, uy)) % 180
            best = (w * h, bearing, w, h)
    return best


for wid, dist in WAYS.items():
    tree = ET.parse(os.path.join(CH, f'{dist}.osm'))
    root = tree.getroot()
    nodes = {n.get('id'): (float(n.get('lat')), float(n.get('lon'))) for n in root.iter('node')}
    way = None
    for w in root.iter('way'):
        if w.get('id') == str(wid):
            way = w
            break
    if way is None:
        print(wid, dist, 'NOT IN CHUNK')
        continue
    pts = [nodes[nd.get('ref')] for nd in way.iter('nd') if nd.get('ref') in nodes]
    la0 = sum(p[0] for p in pts) / len(pts)
    lo0 = sum(p[1] for p in pts) / len(pts)
    en = [((p[1] - lo0) * M_LON, (p[0] - la0) * M_LAT) for p in pts]
    _, bearing, w, h = min_rect(en)
    long_bear = bearing if w >= h else (bearing + 90) % 180
    print(f'{wid} ({dist}): centroid=({la0:.5f},{lo0:.5f}) rect={w:.0f}x{h:.0f}m '
          f'long-axis bearing={long_bear:.1f} yaw(x along long axis)={-math.radians(long_bear - 90):+.3f} '
          f'or {-math.radians(long_bear + 90):+.3f}')
