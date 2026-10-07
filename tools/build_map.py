#!/usr/bin/env python3
"""
Build the full free 3D map of Tbilisi for TbilisiFly.

Sources (no API key, no billing):
  - OpenStreetMap via Overpass API: buildings, roads, water, parks/forests
  - AWS Open Data terrain tiles (terrarium PNGs): elevation

Output: public/tbilisi/city.json.gz in the game's local frame:
  X = west, Z = north, Y = up, origin at 41.7252, 44.7458 (Delisi spawn).
Coordinates are stored as integer decimeters.

Run:  python3 tools/build_map.py
Raw Overpass responses are cached in tools/cache/ so reruns are cheap.
"""

import base64
import gzip
import json
import math
import os
import struct
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO

import requests
from PIL import Image

try:
    import osmium
except ImportError:
    osmium = None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "tools", "cache")
OUT = os.path.join(ROOT, "public", "tbilisi", "city.json.gz")

# Full Tbilisi: Ponichala..Gldani / Digomi..Varketili
BBOX = (41.62, 44.68, 41.84, 44.98)  # S, W, N, E
ORIGIN = (41.7252, 44.7458)  # existing spawn (Delisi, Vazha-Pshavela Ave)

# The whole country, for cross-Georgia flights.
GEORGIA_BBOX = (41.05, 40.00, 43.60, 46.75)
# Cities that get real extruded buildings (S, W, N, E).
CITY_BOXES = {
    "tbilisi": BBOX,
    "batumi": (41.57, 41.56, 41.70, 41.69),
    "kutaisi": (42.21, 42.62, 42.33, 42.78),
    "gori": (41.95, 44.06, 42.02, 44.16),
    "rustavi": (41.51, 44.94, 41.60, 45.06),
    "kazbegi": (42.63, 44.58, 42.68, 44.66),
}
MAJOR_ROADS = ("motorway", "trunk", "primary", "secondary", "motorway_link", "trunk_link", "primary_link", "secondary_link")

GRID = 8  # 8x8 Overpass cells for buildings/roads
TERRAIN_STEP = 30.0  # meters between height samples
TERRAIN_Z = 13  # terrarium zoom (~14 m/px at this latitude)

MIRRORS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
]
HEADERS = {"User-Agent": "TbilisiFly/1.0 (personal flight game; contact: local)", "Accept": "*/*"}

lat0, lon0 = ORIGIN
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)


def to_local(lat, lon):
    """lat/lon -> (x, z) meters. X=west, Z=north."""
    return (-(lon - lon0) * M_LON, (lat - lat0) * M_LAT)


def to_latlon(x, z):
    return (lat0 + z / M_LAT, lon0 - x / M_LON)


_mirror = [0]  # sticky: keep using whichever mirror last succeeded


def overpass(query, cache_name, mirror_hint=None):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, cache_name + ".json")
    if os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    last_err = None
    for attempt in range(8):
        mi = ((mirror_hint if mirror_hint is not None else _mirror[0]) + attempt) % len(MIRRORS)
        url = MIRRORS[mi]
        try:
            r = requests.post(url, data={"data": query}, headers=HEADERS, timeout=240)
            if r.status_code == 200:
                _mirror[0] = mi
                data = r.json()
                with open(path, "w") as f:
                    json.dump(data, f)
                return data
            last_err = f"HTTP {r.status_code} from {url}: {r.text[:120]}"
        except Exception as e:  # noqa: BLE001
            last_err = f"{url}: {e}"
        sleep = 4 * (attempt + 1)
        print(f"    retry in {sleep}s ({last_err})", flush=True)
        time.sleep(sleep)
    raise RuntimeError(f"Overpass failed for {cache_name}: {last_err}")


def cells():
    s, w, n, e = BBOX
    dlat, dlon = (n - s) / GRID, (e - w) / GRID
    for i in range(GRID):
        for j in range(GRID):
            yield i, j, (s + i * dlat, w + j * dlon, s + (i + 1) * dlat, w + (j + 1) * dlon)


def ring_from_geometry(geom):
    return [(p["lat"], p["lon"]) for p in geom if p]


def stitch_rings(segments):
    """Join way segments (lists of latlon) into closed rings by endpoint matching."""
    segs = [s[:] for s in segments if len(s) >= 2]
    rings = []
    while segs:
        ring = segs.pop()
        guard = 0
        while ring[0] != ring[-1] and guard < 2000:
            guard += 1
            for k, s in enumerate(segs):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += s[-2::-1]
                elif s[-1] == ring[0]:
                    ring = s[:-1] + ring
                elif s[0] == ring[0]:
                    ring = s[::-1][:-1] + ring
                else:
                    continue
                segs.pop(k)
                break
            else:
                break  # open chain, give up on closing it
        if len(ring) >= 4 and ring[0] == ring[-1]:
            rings.append(ring)
    return rings


def pack_ring(ring):
    """latlon ring -> flat [x1,z1,...] int decimeters, closing point dropped."""
    pts = ring[:-1] if ring[0] == ring[-1] else ring
    out = []
    for lat, lon in pts:
        x, z = to_local(lat, lon)
        out += [round(x * 10), round(z * 10)]
    return out


def ring_area_m2(flat_dm):
    a = 0.0
    n = len(flat_dm) // 2
    for i in range(n):
        x1, z1 = flat_dm[2 * i] / 10, flat_dm[2 * i + 1] / 10
        x2, z2 = flat_dm[2 * ((i + 1) % n)] / 10, flat_dm[2 * ((i + 1) % n) + 1] / 10
        a += x1 * z2 - x2 * z1
    return abs(a) / 2


def parse_len(v):
    try:
        return float(str(v).lower().replace("m", "").replace(",", ".").strip())
    except (ValueError, TypeError):
        return None


try:  # real heights: Copernicus GHS-BUILT-H ANBH raster (100 m, 2018, CC-BY)
    from anbh import ANBH

    _anbh = ANBH(CACHE)
except Exception as _e:  # noqa: BLE001 — raster/rasterio optional
    print(f"  (no ANBH height raster: {_e} — falling back to type heuristics)")
    _anbh = None

SOVIET_FLOORS = (5, 9, 12, 16)  # discrete panel-block heights


def building_height(tags, osm_id, latlon=None, area=0.0):
    h = parse_len(tags.get("height") or tags.get("building:height"))
    if h and 2 < h < 400:
        return h
    lv = parse_len(tags.get("building:levels"))
    if lv and 0 < lv < 80:
        return lv * 3.2 + 1.5
    t = tags.get("building", "yes")
    rnd = (osm_id * 2654435761) % 1000 / 1000.0  # deterministic per building
    if t in ("garage", "garages", "shed", "hut", "kiosk", "service"):
        return 3
    low = t in ("house", "detached", "residential", "semidetached_house", "terrace")
    # satellite-measured neighborhood height, shaped per building
    cell = _anbh.sample(*latlon) if (_anbh and latlon) else 0.0
    if cell >= 3.5:
        if low:
            return min(5 + rnd * 4, max(5.0, cell * 0.9))
        base = cell * (0.85 + 0.5 * rnd)
        if area >= 350 and cell >= 13:
            # big footprint in a genuinely tall cell: snap to Soviet floor counts
            fl = min(SOVIET_FLOORS, key=lambda f: abs(f * 3.2 + 1.5 - base))
            return fl * 3.2 + 1.5
        if area and area < 120:
            base = min(base, 9.0 + rnd * 5)  # jittered cap: no uniform rooflines
        return max(3.5, min(base, 110.0))
    if t in ("apartments", "dormitory", "hotel", "office", "commercial"):
        return 15 + rnd * 18
    if low:
        return 5 + rnd * 4
    if t in ("church", "cathedral", "mosque", "synagogue", "temple"):
        return 18 + rnd * 10
    if t in ("industrial", "warehouse", "retail", "supermarket"):
        return 7 + rnd * 5
    return 6 + rnd * 6


ROAD_W = {
    "motorway": 18, "trunk": 16, "primary": 13, "secondary": 10, "tertiary": 8,
    "motorway_link": 9, "trunk_link": 8, "primary_link": 8, "secondary_link": 7,
    "residential": 6, "unclassified": 6, "living_street": 5,
}


def bld_query(s, w, n, e):
    return (
        f'[out:json][timeout:180];'
        f'(way["building"]({s},{w},{n},{e});'
        f'relation["building"]["type"="multipolygon"]({s},{w},{n},{e}););'
        f'out tags geom;'
    )


def road_query(s, w, n, e):
    classes = "|".join(ROAD_W)
    return (
        f'[out:json][timeout:180];'
        f'way["highway"~"^({classes})$"]["tunnel"!~"yes"]({s},{w},{n},{e});'
        f'out tags geom;'
    )


def prefetch_cells():
    """Warm the cache in parallel — one worker per Overpass mirror."""
    jobs = []
    for i, j, (s, w, n, e) in cells():
        jobs.append((bld_query(s, w, n, e), f"bld_{i}_{j}"))
        jobs.append((road_query(s, w, n, e), f"road_{i}_{j}"))
    todo = [(q, name) for q, name in jobs if not os.path.exists(os.path.join(CACHE, name + ".json"))]
    print(f"== Prefetch: {len(todo)} of {len(jobs)} cells to download ==", flush=True)
    done = [0]

    def work(item):
        q, name, k = item
        overpass(q, name, mirror_hint=k % len(MIRRORS))
        done[0] += 1
        if done[0] % 5 == 0:
            print(f"  prefetched {done[0]}/{len(todo)}", flush=True)

    with ThreadPoolExecutor(max_workers=len(MIRRORS)) as ex:
        list(ex.map(work, [(q, n, k) for k, (q, n) in enumerate(todo)]))


def fetch_buildings():
    print("== Buildings ==", flush=True)
    buildings, seen = [], set()
    for i, j, (s, w, n, e) in cells():
        data = overpass(bld_query(s, w, n, e), f"bld_{i}_{j}")
        count = 0
        for el in data.get("elements", []):
            key = (el["type"], el["id"])
            if key in seen:
                continue
            seen.add(key)
            tags = el.get("tags", {})
            if tags.get("building") == "no" or tags.get("location") == "underground":
                continue
            rings = []
            if el["type"] == "way" and el.get("geometry"):
                r = ring_from_geometry(el["geometry"])
                if len(r) >= 4 and r[0] == r[-1]:
                    rings = [r]
            elif el["type"] == "relation":
                outers = [
                    ring_from_geometry(m.get("geometry", []))
                    for m in el.get("members", [])
                    if m.get("role") == "outer" and m.get("geometry")
                ]
                rings = stitch_rings(outers)
            for r in rings:
                flat = pack_ring(r)
                if len(flat) < 6 or ring_area_m2(flat) < 6:
                    continue
                n2 = len(flat) // 2
                cll = to_latlon(sum(flat[0::2]) / n2 / 10, sum(flat[1::2]) / n2 / 10)
                h = building_height(tags, el["id"], latlon=cll, area=ring_area_m2(flat))
                buildings.append([round(h * 10)] + flat)
                count += 1
        print(f"  cell {i},{j}: +{count} (total {len(buildings)})", flush=True)
    return buildings


def fetch_roads():
    print("== Roads ==", flush=True)
    roads, seen = [], set()
    for i, j, (s, w, n, e) in cells():
        data = overpass(road_query(s, w, n, e), f"road_{i}_{j}")
        for el in data.get("elements", []):
            if el["id"] in seen or not el.get("geometry"):
                continue
            seen.add(el["id"])
            width = ROAD_W.get(el.get("tags", {}).get("highway"), 6)
            flat = []
            for p in el["geometry"]:
                x, z = to_local(p["lat"], p["lon"])
                flat += [round(x * 10), round(z * 10)]
            if len(flat) >= 4:
                roads.append([width] + flat)
        print(f"  cell {i},{j}: total {len(roads)}", flush=True)
    return roads


def fetch_areas(name, query_body):
    """Fetch polygon areas (water / green). Returns list of flat rings (dm)."""
    print(f"== {name} ==", flush=True)
    s, w, n, e = BBOX
    q = f"[out:json][timeout:180];({query_body.format(bbox=f'{s},{w},{n},{e}')});out tags geom;"
    data = overpass(q, name)
    polys, seen = [], set()
    for el in data.get("elements", []):
        key = (el["type"], el["id"])
        if key in seen:
            continue
        seen.add(key)
        rings = []
        if el["type"] == "way" and el.get("geometry"):
            r = ring_from_geometry(el["geometry"])
            if len(r) >= 4 and r[0] == r[-1]:
                rings = [r]
        elif el["type"] == "relation":
            outers = [
                ring_from_geometry(m.get("geometry", []))
                for m in el.get("members", [])
                if m.get("role") == "outer" and m.get("geometry")
            ]
            rings = stitch_rings(outers)
        for r in rings:
            flat = pack_ring(r)
            if len(flat) >= 6 and ring_area_m2(flat) > 200:
                polys.append(flat)
    print(f"  {len(polys)} polygons", flush=True)
    return polys


GREEN_LEISURE = ("park", "garden", "pitch", "golf_course")
GREEN_LANDUSE = ("forest", "grass", "meadow", "recreation_ground", "village_green", "cemetery", "orchard", "vineyard", "religious")
GREEN_NATURAL = ("wood", "scrub", "grassland")


def in_box(lat, lon, box):
    return box[0] <= lat <= box[2] and box[1] <= lon <= box[3]


def extract_from_pbf(path):
    """Parse a Geofabrik .osm.pbf locally — no Overpass servers involved.

    Buildings: every city in CITY_BOXES. Roads: all streets inside cities +
    major roads across the whole country. Water/green: Tbilisi only (they
    drive the terrain tint and tree placement there).
    """
    print("== Extracting from PBF (2 passes, a few minutes) ==", flush=True)
    buildings, roads, water, green = [], [], [], []
    churches, mosques, bridges, tunnels = [], [], [], []
    city_boxes = list(CITY_BOXES.values())

    def latlon_ring(node_iter):
        ring = []
        for nd in node_iter:
            try:
                ring.append((nd.lat, nd.lon))
            except Exception:  # noqa: BLE001 — node without location
                continue
        return ring

    def any_in(ring, box):
        return any(in_box(lat, lon, box) for lat, lon in ring)

    class Handler(osmium.SimpleHandler):
        def area(self, a):
            t = a.tags
            if "building" in t and t["building"] != "no":
                # canopies and bridge decks are NOT solid buildings: extruding
                # them puts an apartment-textured block over e.g. the Bridge
                # of Peace (way 1207561125, building=roof + man_made=bridge)
                if t["building"] == "roof" or t.get("man_made") == "bridge":
                    return
                kind = "b"
            elif t.get("natural") == "water" or t.get("waterway") == "riverbank" or t.get("landuse") in ("reservoir", "basin"):
                kind = "w"
            elif (
                t.get("leisure") in GREEN_LEISURE
                or t.get("landuse") in GREEN_LANDUSE
                or t.get("natural") in GREEN_NATURAL
            ):
                kind = "g"
            else:
                return
            tags = {
                k: t[k]
                for k in ("height", "building:height", "building:levels", "building", "location", "amenity", "religion")
                if k in t
            }
            if tags.get("location") == "underground":
                return
            for outer in a.outer_rings():
                ring = latlon_ring(outer)
                if len(ring) < 4:
                    continue
                if kind == "b":
                    if not any(any_in(ring, box) for box in city_boxes):
                        continue
                elif not any_in(ring, BBOX):
                    continue
                flat = pack_ring(ring)
                if len(flat) < 6:
                    continue
                if kind == "b":
                    area = ring_area_m2(flat)
                    if area < 6:
                        continue
                    # churches/mosques get real modeled silhouettes instead of boxes
                    worship = tags.get("building") in ("church", "cathedral", "chapel", "monastery", "basilica") or tags.get("amenity") == "place_of_worship"
                    if worship:
                        n2 = len(flat) // 2
                        cx = sum(flat[0::2]) / n2
                        cz = sum(flat[1::2]) / n2
                        entry = [round(cx), round(cz), round(area)]
                        rel = tags.get("religion", "")
                        if rel == "muslim" or tags.get("building") == "mosque":
                            mosques.append(entry)
                            continue
                        if rel in ("", "christian"):
                            churches.append(entry)
                            continue
                        # synagogues, yazidi temples… — plain buildings, no cross
                    n2 = len(flat) // 2
                    cll = to_latlon(sum(flat[0::2]) / n2 / 10, sum(flat[1::2]) / n2 / 10)
                    h = building_height(tags, a.orig_id(), latlon=cll, area=area)
                    buildings.append([round(h * 10)] + flat)
                elif ring_area_m2(flat) > 200:
                    (water if kind == "w" else green).append(flat)

        def way(self, wy):
            t = wy.tags
            hw = t.get("highway")
            if hw not in ROAD_W:
                return
            ring = latlon_ring(wy.nodes)
            if len(ring) < 2:
                return
            keep = (hw in MAJOR_ROADS and any_in(ring, GEORGIA_BBOX)) or any(
                any_in(ring, box) for box in city_boxes
            )
            if not keep:
                return
            flat = []
            for lat, lon in ring:
                x, z = to_local(lat, lon)
                flat += [round(x * 10), round(z * 10)]
            entry = [ROAD_W[hw]] + flat
            if t.get("tunnel") not in (None, "no"):
                tunnels.append(entry)  # portals only; the middle is underground
            elif t.get("bridge") not in (None, "no"):
                bridges.append(entry)  # real spans, not draped on terrain
            else:
                roads.append(entry)

    Handler().apply_file(path, locations=True, idx="flex_mem")
    print(f"  {len(buildings)} buildings, {len(roads)} roads, {len(water)} water, {len(green)} green, "
          f"{len(churches)} churches, {len(mosques)} mosques, {len(bridges)} bridges, {len(tunnels)} tunnels", flush=True)
    return buildings, roads, water, green, churches, mosques, bridges, tunnels


def fetch_terrain(bbox=BBOX, step=TERRAIN_STEP, zoom=TERRAIN_Z):
    """Terrarium tiles -> local-frame height grid (uint16 decimeters, base64)."""
    print(f"== Terrain {bbox} @ {step}m z{zoom} ==", flush=True)
    s, w, n, e = bbox

    def tile_xy(lat, lon, z):
        nt = 2 ** z
        xt = (lon + 180) / 360 * nt
        yt = (1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * nt
        return xt, yt

    x_min, y_min = tile_xy(n, w, zoom)  # note: y grows southward
    x_max, y_max = tile_xy(s, e, zoom)
    tx0, ty0 = int(x_min), int(y_min)
    tx1, ty1 = int(x_max), int(y_max)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    print(f"  {cols}x{rows} tiles @ z{zoom}", flush=True)

    mosaic = Image.new("RGB", (cols * 256, rows * 256))
    sess = requests.Session()
    for ty in range(ty0, ty1 + 1):
        for tx in range(tx0, tx1 + 1):
            path = os.path.join(CACHE, f"dem_{zoom}_{tx}_{ty}.png")
            if not os.path.exists(path):
                url = f"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{zoom}/{tx}/{ty}.png"
                for attempt in range(4):
                    try:
                        r = sess.get(url, headers=HEADERS, timeout=60)
                        r.raise_for_status()
                        with open(path, "wb") as f:
                            f.write(r.content)
                        break
                    except Exception as ex:  # noqa: BLE001
                        print(f"    dem retry {url}: {ex}", flush=True)
                        time.sleep(3 * (attempt + 1))
                else:
                    raise RuntimeError(f"terrain tile failed: {url}")
            mosaic.paste(Image.open(path), ((tx - tx0) * 256, (ty - ty0) * 256))
    px = mosaic.load()

    def elev_at(lat, lon):
        xt, yt = tile_xy(lat, lon, zoom)
        fx = (xt - tx0) * 256
        fy = (yt - ty0) * 256
        x0i, y0i = int(fx), int(fy)
        x0i = max(0, min(mosaic.width - 2, x0i))
        y0i = max(0, min(mosaic.height - 2, y0i))
        dx, dy = fx - x0i, fy - y0i

        def dec(xx, yy):
            r, g, b = px[xx, yy]
            return r * 256 + g + b / 256 - 32768

        h00, h10 = dec(x0i, y0i), dec(x0i + 1, y0i)
        h01, h11 = dec(x0i, y0i + 1), dec(x0i + 1, y0i + 1)
        return (h00 * (1 - dx) + h10 * dx) * (1 - dy) + (h01 * (1 - dx) + h11 * dx) * dy

    # Local-frame grid. X=west, Z=north; grid rows advance in +Z, cols in +X.
    x_a, z_a = to_local(s, e)  # one corner
    x_b, z_b = to_local(n, w)  # opposite corner
    min_x, max_x = min(x_a, x_b), max(x_a, x_b)
    min_z, max_z = min(z_a, z_b), max(z_a, z_b)
    gw = int((max_x - min_x) / step) + 1
    gh = int((max_z - min_z) / step) + 1
    print(f"  grid {gw}x{gh} @ {step}m", flush=True)
    heights = bytearray(gw * gh * 2)
    for r in range(gh):
        z = min_z + r * step
        for c in range(gw):
            x = min_x + c * step
            lat, lon = to_latlon(x, z)
            e_m = max(0.0, elev_at(lat, lon))
            struct.pack_into("<H", heights, (r * gw + c) * 2, min(65535, round(e_m * 10)))
        if r % 100 == 0:
            print(f"  row {r}/{gh}", flush=True)
    return {
        "minx": round(min_x, 1), "minz": round(min_z, 1),
        "step": step, "w": gw, "h": gh,
        "data": base64.b64encode(bytes(heights)).decode(),
    }


SAT_Z = 14  # Sentinel-2 cloudless zoom (~10 m/px)


def fetch_satellite(bbox=BBOX, zoom=SAT_Z, outname="satellite.jpg"):
    """Real ground from space: Sentinel-2 cloudless mosaic by EOX (free, CC BY-NC-SA)."""
    print(f"== Satellite {bbox} z{zoom} -> {outname} ==", flush=True)
    s, w, n, e = bbox

    def txy(lat, lon):
        nt = 2 ** zoom
        return (
            (lon + 180) / 360 * nt,
            (1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * nt,
        )

    x_min, y_min = txy(n, w)
    x_max, y_max = txy(s, e)
    tx0, ty0, tx1, ty1 = int(x_min), int(y_min), int(x_max), int(y_max)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    print(f"  {cols}x{rows} tiles @ z{zoom}", flush=True)
    mosaic = Image.new("RGB", (cols * 256, rows * 256))
    sess = requests.Session()
    for ty in range(ty0, ty1 + 1):
        for tx in range(tx0, tx1 + 1):
            path = os.path.join(CACHE, f"sat_{zoom}_{tx}_{ty}.jpg")
            if not os.path.exists(path):
                url = f"https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2020_3857/default/g/{zoom}/{ty}/{tx}.jpg"
                for attempt in range(4):
                    try:
                        r = sess.get(url, headers=HEADERS, timeout=60)
                        r.raise_for_status()
                        with open(path, "wb") as f:
                            f.write(r.content)
                        break
                    except Exception as ex:  # noqa: BLE001
                        print(f"    sat retry {url}: {ex}", flush=True)
                        time.sleep(2 * (attempt + 1))
                else:
                    raise RuntimeError(f"satellite tile failed: {url}")
            mosaic.paste(Image.open(path), ((tx - tx0) * 256, (ty - ty0) * 256))
        print(f"  row {ty - ty0 + 1}/{rows}", flush=True)
    out = os.path.join(ROOT, "public", "tbilisi", outname)
    mosaic.save(out, "JPEG", quality=82)
    print(f"  {mosaic.width}x{mosaic.height}px -> {out} ({os.path.getsize(out) / 1e6:.1f} MB)", flush=True)
    return {"z": zoom, "x0": tx0, "y0": ty0, "cols": cols, "rows": rows}


def rasterize_classes(terrain, green_polys, water_polys):
    """Even-odd scanline fill of green/water polygons onto the terrain grid."""
    print("== Land classes ==", flush=True)
    gw, gh = terrain["w"], terrain["h"]
    minx, minz, step = terrain["minx"], terrain["minz"], terrain["step"]
    classes = bytearray(gw * gh)

    def fill(flat, value):
        n = len(flat) // 2
        xs = [flat[2 * i] / 10 for i in range(n)]
        zs = [flat[2 * i + 1] / 10 for i in range(n)]
        r_lo = max(0, int((min(zs) - minz) / step))
        r_hi = min(gh - 1, int((max(zs) - minz) / step) + 1)
        for r in range(r_lo, r_hi + 1):
            z = minz + r * step
            hits = []
            for i in range(n):
                x1, z1, x2, z2 = xs[i], zs[i], xs[(i + 1) % n], zs[(i + 1) % n]
                if (z1 > z) != (z2 > z):
                    hits.append(x1 + (z - z1) / (z2 - z1) * (x2 - x1))
            hits.sort()
            for k in range(0, len(hits) - 1, 2):
                c_lo = max(0, int(math.ceil((hits[k] - minx) / step)))
                c_hi = min(gw - 1, int((hits[k + 1] - minx) / step))
                for c in range(c_lo, c_hi + 1):
                    classes[r * gw + c] = value
    for p in green_polys:
        fill(p, 1)
    for p in water_polys:
        fill(p, 2)
    print("  done", flush=True)
    return base64.b64encode(bytes(classes)).decode()


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    os.makedirs(CACHE, exist_ok=True)
    t0 = time.time()

    terrain = fetch_terrain()
    pbf = os.path.join(CACHE, "georgia-latest.osm.pbf")
    if osmium is not None and os.path.exists(pbf) and os.path.getsize(pbf) > 10_000_000:
        buildings, roads, water, green, churches, mosques, bridges, tunnels = extract_from_pbf(pbf)
    else:
        # Fallback: query Overpass servers cell by cell (slow when they're busy).
        churches, mosques, bridges, tunnels = [], [], [], []
        prefetch_cells()
        buildings = fetch_buildings()
        roads = fetch_roads()
        water = fetch_areas(
            "water",
            'way["natural"="water"]({bbox});relation["natural"="water"]({bbox});'
            'way["waterway"="riverbank"]({bbox});relation["waterway"="riverbank"]({bbox})',
        )
        green = fetch_areas(
            "green",
            'way["leisure"~"^(park|garden|pitch|golf_course)$"]({bbox});'
            'relation["leisure"="park"]({bbox});'
            'way["landuse"~"^(forest|grass|meadow|recreation_ground|village_green|cemetery|orchard|vineyard)$"]({bbox});'
            'relation["landuse"="forest"]({bbox});'
            'way["natural"~"^(wood|scrub|grassland)$"]({bbox});'
            'relation["natural"="wood"]({bbox})',
        )
    terrain["classes"] = rasterize_classes(terrain, green, water)
    satellite = fetch_satellite()
    country_terrain = fetch_terrain(GEORGIA_BBOX, 240.0, 10)
    country_sat = fetch_satellite(GEORGIA_BBOX, 11, "georgia-sat.jpg")
    # Sharp terrain + imagery for every other city (Tbilisi has its own above).
    details = {}
    for cname, cbox in CITY_BOXES.items():
        if cname == "tbilisi":
            continue
        details[cname] = {
            "terrain": fetch_terrain(cbox, 60.0, 12),
            "satellite": fetch_satellite(cbox, 13, f"sat_{cname}.jpg"),
            "file": f"sat_{cname}.jpg",
        }

    city = {
        "meta": {
            "origin": {"lat": lat0, "lon": lon0},
            "mlat": round(M_LAT, 2), "mlon": round(M_LON, 2),
            "bbox": BBOX,
            "satellite": satellite,
            "cities": CITY_BOXES,
            "attribution": "© OpenStreetMap contributors · Sentinel-2 cloudless by EOX · terrain: Mapzen/AWS",
        },
        "terrain": terrain,
        "country": {"terrain": country_terrain, "satellite": country_sat, "bbox": GEORGIA_BBOX},
        "details": details,
        "buildings": buildings,
        "churches": churches,
        "mosques": mosques,
        "bridges": bridges,
        "tunnels": tunnels,
        "roads": roads,
        "water": water,
        "green": green,
    }
    raw = json.dumps(city, separators=(",", ":")).encode()
    with gzip.open(OUT, "wb", compresslevel=9) as f:
        f.write(raw)
    # Small side-file for the in-game map UI (works in both world modes).
    mapmeta = {
        "origin": city["meta"]["origin"], "mlat": M_LAT, "mlon": M_LON,
        "satellite": satellite, "cities": CITY_BOXES,
        "country": {"satellite": country_sat, "bbox": GEORGIA_BBOX},
    }
    with open(os.path.join(ROOT, "public", "tbilisi", "mapmeta.json"), "w") as f:
        json.dump(mapmeta, f)
    print(
        f"\nDone in {time.time() - t0:.0f}s: {len(buildings)} buildings, {len(roads)} roads, "
        f"{len(water)} water, {len(green)} green.\n"
        f"{OUT}: {os.path.getsize(OUT) / 1e6:.1f} MB gz ({len(raw) / 1e6:.1f} MB raw)",
        flush=True,
    )


if __name__ == "__main__":
    sys.exit(main())
