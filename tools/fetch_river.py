"""Real Mtkvari channel from OpenStreetMap -> public/tbilisi/river.json

  python3 tools/fetch_river.py

Why this exists: the water sheet in game was traced with marching squares over
the 30 m terrain CLASS grid, where a cell counts as water if the river merely
touches it. The Mtkvari is 60-100 m wide, so that trace is up to a full cell
too wide on EACH side -- which is why the water climbs onto the embankment
roads. This pulls the actual OSM water polygons and the river centreline at
full precision instead, in game-local metres.

It also measures the real WATER SURFACE height along the stitched Mtkvari
centreline with the Google Elevation API (VITE_GMAPS_KEY, already in .env for
the 3D Tiles map) and bakes it in. The game used to guess the surface as a low
percentile of terrain within 75 m, which wanders by metres and has no reason to
fall downstream; measured, the Mtkvari drops from ~460 m where it enters the
bbox to ~337 m where it leaves, and past Dedaena it is 393 m -- some 7 m below
where the guess was putting it, which is why the sheet climbed onto the
embankment roads.

Output (public/tbilisi/river.json):
  {"origin":{...}, "mlat":.., "mlon":..,
   "polys":[{"outer":[[x,z],..], "holes":[[[x,z],..]], "kind":"river"|"water",
             "name":..}],
   "lines":[{"pts":[[x,z],..], "name":.., "main":bool}],
   "centre":[[x, z, waterLevel], ..]}    # the Mtkvari, ordered DOWNSTREAM
"""
import json
import math
import os
import time
import urllib.parse
import urllib.request

import requests

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "tools", "cache")
OUT = os.path.join(ROOT, "public", "tbilisi", "river.json")
META = os.path.join(ROOT, "public", "tbilisi", "mapmeta.json")

# the city bbox the map is built for (mapmeta.cities.tbilisi), padded a little
S, W, N, E = 41.60, 44.66, 41.86, 45.00

MIRRORS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
]
HEADERS = {"User-Agent": "TbilisiFly/0.1 (+local dev build)"}

QUERY = f"""
[out:json][timeout:300];
(
  way["natural"="water"]({S},{W},{N},{E});
  relation["natural"="water"]({S},{W},{N},{E});
  way["waterway"="riverbank"]({S},{W},{N},{E});
  relation["waterway"="riverbank"]({S},{W},{N},{E});
  way["waterway"="river"]({S},{W},{N},{E});
  way["waterway"="stream"]({S},{W},{N},{E});
);
out geom;
"""


def overpass(query, cache_name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, cache_name + ".json")
    if os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    last = None
    for attempt in range(8):
        url = MIRRORS[attempt % len(MIRRORS)]
        try:
            r = requests.post(url, data={"data": query}, headers=HEADERS, timeout=300)
            if r.status_code == 200:
                data = r.json()
                with open(path, "w") as f:
                    json.dump(data, f)
                return data
            last = f"HTTP {r.status_code} from {url}: {r.text[:140]}"
        except Exception as e:  # noqa: BLE001
            last = f"{url}: {e}"
        wait = 4 * (attempt + 1)
        print(f"  retry in {wait}s ({last})", flush=True)
        time.sleep(wait)
    raise RuntimeError(f"Overpass failed: {last}")


def main():
    meta = json.load(open(META))
    o, mlat, mlon = meta["origin"], meta["mlat"], meta["mlon"]

    def to_local(lat, lon):
        # same frame as the game: X = west, Z = north
        return [round(-(lon - o["lon"]) * mlon, 1), round((lat - o["lat"]) * mlat, 1)]

    data = overpass(QUERY, "river_tbilisi")
    els = data["elements"]
    print(f"overpass: {len(els)} elements")

    polys, lines = [], []
    for el in els:
        tags = el.get("tags", {}) or {}
        name = tags.get("name:en") or tags.get("name") or ""
        if tags.get("waterway") in ("river", "stream"):
            geom = el.get("geometry") or []
            if len(geom) < 2:
                continue
            lines.append({
                "pts": [to_local(p["lat"], p["lon"]) for p in geom],
                "name": name,
                "main": tags.get("waterway") == "river",
            })
            continue
        kind = "river" if tags.get("water") == "river" \
            or tags.get("waterway") == "riverbank" else "water"
        if el["type"] == "way":
            geom = el.get("geometry") or []
            if len(geom) < 4:
                continue
            polys.append({"outer": [to_local(p["lat"], p["lon"]) for p in geom],
                          "holes": [], "kind": kind, "name": name})
        elif el["type"] == "relation":
            # multipolygon: stitch the outer/inner ways into rings
            outers, inners = [], []
            for m in el.get("members", []):
                g = m.get("geometry") or []
                if len(g) < 2:
                    continue
                ring = [to_local(p["lat"], p["lon"]) for p in g]
                (outers if m.get("role") != "inner" else inners).append(ring)
            for ring in stitch(outers):
                if len(ring) >= 4:
                    polys.append({"outer": ring, "holes": [], "kind": kind, "name": name})
            stitched_inner = [r for r in stitch(inners) if len(r) >= 4]
            if stitched_inner and polys:
                polys[-1]["holes"] = stitched_inner

    # the Mtkvari itself, longest first, so the game can find the main channel
    lines.sort(key=lambda L: -poly_len(L["pts"]))
    polys.sort(key=lambda p: -abs(area(p["outer"])))
    centre = measure_levels(stitch_river(lines), o, mlat, mlon)
    out = {"origin": o, "mlat": mlat, "mlon": mlon, "polys": polys,
           "lines": lines, "centre": centre}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(out, f, separators=(",", ":"))
    big = [p for p in polys if abs(area(p["outer"])) > 20000]
    print(f"river.json: {len(polys)} water polygons ({len(big)} over 2 ha), "
          f"{len(lines)} waterway lines")
    for p in big[:8]:
        print(f"  {abs(area(p['outer'])) / 1e4:8.1f} ha  {p['kind']:6s} {p['name'][:40]}")
    for L in lines[:5]:
        print(f"  {poly_len(L['pts']) / 1000:8.2f} km  line   {L['name'][:40]}")
    print(f"  centreline: {len(centre)} stations, "
          f"{centre[0][2]:.1f} m -> {centre[-1][2]:.1f} m water surface")


MT_NAMES = ("Mtkvari", "\u10db\u10e2\u10d9\u10d5\u10d0\u10e0\u10d8", "Kura")
LEVEL_STEP = 150.0   # m between measured water-surface stations


def stitch_river(lines):
    """Join every way named Mtkvari into one polyline, ordered DOWNSTREAM.

    OSM has the river as 19 separate ways here (50 km all told) and the single
    longest one is the reach below the city, so picking "the longest way" reads
    the wrong river.
    """
    pool = [list(L["pts"]) for L in lines if L["name"] in MT_NAMES]
    chains = []
    while pool:
        cur = pool.pop()
        joined = True
        while joined:
            joined = False
            for i, w in enumerate(pool):
                if dist(cur[-1], w[0]) < 2:
                    cur += w[1:]; pool.pop(i); joined = True; break
                if dist(cur[-1], w[-1]) < 2:
                    cur += list(reversed(w))[1:]; pool.pop(i); joined = True; break
                if dist(cur[0], w[-1]) < 2:
                    cur = w[:-1] + cur; pool.pop(i); joined = True; break
                if dist(cur[0], w[0]) < 2:
                    cur = list(reversed(w))[:-1] + cur; pool.pop(i); joined = True; break
        chains.append(cur)
    # local Z is north and the Mtkvari runs NW -> SE, so downstream is -Z
    for c in chains:
        if c[0][1] < c[-1][1]:
            c.reverse()
    chains = [c for c in chains if poly_len(c) > 200]
    chains.sort(key=lambda c: -c[0][1])   # furthest upstream first
    # OSM leaves the name off a 684 m stretch near Ortachala, which split the
    # river into an upper (city) chain and a lower one. Walk downstream and
    # bridge any gap short enough to be that: without this the lower reach got
    # no measured level and inherited the city's, ~15 m too high.
    chain = chains.pop(0)
    while chains:
        nxt = min(chains, key=lambda c: dist(chain[-1], c[0]))
        gap = dist(chain[-1], nxt[0])
        if gap > 1500:
            break
        if gap > 5:
            print(f"  bridging a {gap:.0f} m gap in the named ways")
        chain += nxt
        chains.remove(nxt)
    return chain


def gmaps_key():
    for line in open(os.path.join(ROOT, ".env")):
        if line.startswith("VITE_GMAPS_KEY="):
            return line.split("=", 1)[1].strip()
    return None


def measure_levels(pts, o, mlat, mlon):
    """Resample the centreline and measure the real water surface on it."""
    stations, acc = [], 0.0
    for i in range(len(pts) - 1):
        (x1, z1), (x2, z2) = pts[i], pts[i + 1]
        seg = dist(pts[i], pts[i + 1])
        if seg < 1e-6:
            continue
        t = LEVEL_STEP - acc
        while t <= seg:
            f = t / seg
            stations.append([round(x1 + (x2 - x1) * f, 1), round(z1 + (z2 - z1) * f, 1)])
            t += LEVEL_STEP
        acc = (acc + seg) % LEVEL_STEP
    key = gmaps_key()
    if not key:
        print("  ! no VITE_GMAPS_KEY -- centreline written without levels")
        return [[x, z, None] for x, z in stations]
    els = elevations([(o["lat"] + z / mlat, o["lon"] - x / mlon) for x, z in stations], key)
    # Measured water surfaces still wobble by a few decimetres (the dataset is
    # sampled, and bridges show up as spikes). A river surface cannot climb, so
    # smooth, then force it non-increasing downstream.
    sm = []
    for i, v in enumerate(els):
        w = els[max(0, i - 2):i + 3]
        sm.append(sum(w) / len(w))
    for i in range(1, len(sm)):
        if sm[i] > sm[i - 1]:
            sm[i] = sm[i - 1]
    return [[x, z, round(v, 2)] for (x, z), v in zip(stations, sm)]


def elevations(points, key):
    """[(lat, lon)] -> [metres], batched to stay inside the URL limit."""
    out = []
    for i in range(0, len(points), 256):
        chunk = points[i:i + 256]
        locs = "|".join(f"{la:.6f},{lo:.6f}" for la, lo in chunk)
        url = ("https://maps.googleapis.com/maps/api/elevation/json?locations="
               + urllib.parse.quote(locs) + "&key=" + key)
        with urllib.request.urlopen(url, timeout=60) as r:
            d = json.load(r)
        if d.get("status") != "OK":
            raise RuntimeError(f"Elevation API: {d.get('status')} "
                               f"{d.get('error_message', '')}")
        out += [p["elevation"] for p in d["results"]]
        print(f"  measured {len(out)}/{len(points)} water-surface points")
    return out


def stitch(ways):
    """Join way fragments end-to-end into closed rings."""
    rings, pool = [], [list(w) for w in ways]
    while pool:
        cur = pool.pop()
        changed = True
        while changed and (cur[0] != cur[-1]):
            changed = False
            for i, w in enumerate(pool):
                if dist(cur[-1], w[0]) < 0.5:
                    cur += w[1:]; pool.pop(i); changed = True; break
                if dist(cur[-1], w[-1]) < 0.5:
                    cur += list(reversed(w))[1:]; pool.pop(i); changed = True; break
                if dist(cur[0], w[-1]) < 0.5:
                    cur = w[:-1] + cur; pool.pop(i); changed = True; break
                if dist(cur[0], w[0]) < 0.5:
                    cur = list(reversed(w))[:-1] + cur; pool.pop(i); changed = True; break
        rings.append(cur)
    return rings


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def poly_len(pts):
    return sum(dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1))


def area(ring):
    s = 0.0
    for i in range(len(ring)):
        x1, z1 = ring[i]
        x2, z2 = ring[(i + 1) % len(ring)]
        s += x1 * z2 - x2 * z1
    return s / 2


if __name__ == "__main__":
    main()
