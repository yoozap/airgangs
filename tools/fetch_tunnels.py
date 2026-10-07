"""Real ground elevation over Tbilisi's road tunnels -> public/tbilisi/tunnel_ground.json

  python3 tools/fetch_tunnels.py

Why: a bore's vault is sized by its COVER -- how much hill sits over the road.
The 30 m DEM the map is built from smooths Tbilisi's ridges badly: over the
Noe Zhordania tunnel under the Old Town it reports 410-412 m where the real
surface is 419-428 m, so the computed cover is ~10 m short and every bore
under the old town collapses to the 4.5 m minimum vault.

This walks every OSM road tunnel in the city, samples the real surface above
it with the Google Elevation API (VITE_GMAPS_KEY, the key already in .env),
and bakes a lookup the map reads when it plans the cuttings.

Output: {"cell": <metres>, "ground": {"<cx>_<cz>": <metres>, ...}} in the game's
local frame, so citymap can ask for any point over a tunnel.
"""
import json
import math
import os
import sys
import time
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "tools", "cache")
OUT = os.path.join(ROOT, "public", "tbilisi", "tunnel_ground.json")
META = os.path.join(ROOT, "public", "tbilisi", "mapmeta.json")
S, W, N, E = 41.60, 44.66, 41.86, 45.00
STEP = 25.0        # m between samples along a tunnel
SPREAD = 22.0      # m either side, so the whole corridor is covered
CELL = 20.0        # m lookup cell
MIRRORS = ["https://overpass-api.de/api/interpreter",
           "https://overpass.private.coffee/api/interpreter",
           "https://overpass.kumi.systems/api/interpreter"]
UA = {"User-Agent": "TbilisiFly/0.1 (+local dev build)"}
QUERY = f"""
[out:json][timeout:300];
way["tunnel"]["highway"]({S},{W},{N},{E});
out geom;
"""


def overpass():
    path = os.path.join(CACHE, "tunnels_city.json")
    if os.path.exists(path):
        return json.load(open(path))
    last = None
    for i in range(6):
        url = MIRRORS[i % len(MIRRORS)]
        try:
            req = urllib.request.Request(url, data=urllib.parse.urlencode({"data": QUERY}).encode(), headers=UA)
            d = json.load(urllib.request.urlopen(req, timeout=300))
            os.makedirs(CACHE, exist_ok=True)
            json.dump(d, open(path, "w"))
            return d
        except Exception as e:  # noqa: BLE001
            last = f"{url}: {e}"
            print(f"  retry ({last})", flush=True)
            time.sleep(4 * (i + 1))
    sys.exit(f"Overpass failed: {last}")


def gmaps_key():
    for line in open(os.path.join(ROOT, ".env")):
        if line.startswith("VITE_GMAPS_KEY="):
            return line.split("=", 1)[1].strip()
    sys.exit("no VITE_GMAPS_KEY in .env")


def elevations(points, key):
    out = []
    for i in range(0, len(points), 256):
        chunk = points[i:i + 256]
        locs = "|".join(f"{a:.6f},{b:.6f}" for a, b in chunk)
        url = ("https://maps.googleapis.com/maps/api/elevation/json?locations="
               + urllib.parse.quote(locs) + "&key=" + key)
        with urllib.request.urlopen(url, timeout=60) as r:
            d = json.load(r)
        if d.get("status") != "OK":
            sys.exit(f"Elevation API: {d.get('status')} {d.get('error_message','')}")
        out += [p["elevation"] for p in d["results"]]
        print(f"  measured {len(out)}/{len(points)}", flush=True)
    return out


def main():
    meta = json.load(open(META))
    o, mlat, mlon = meta["origin"], meta["mlat"], meta["mlon"]
    d = overpass()
    ways = [e for e in d["elements"] if len(e.get("geometry") or []) >= 2]
    print(f"{len(ways)} road tunnels in the city box")

    seen = {}
    for e in ways:
        g = e["geometry"]
        for i in range(len(g) - 1):
            a, b = g[i], g[i + 1]
            ax = -(a["lon"] - o["lon"]) * mlon
            az = (a["lat"] - o["lat"]) * mlat
            bx = -(b["lon"] - o["lon"]) * mlon
            bz = (b["lat"] - o["lat"]) * mlat
            seg = math.hypot(bx - ax, bz - az)
            steps = max(1, int(seg / STEP))
            for k in range(steps + 1):
                t = k / steps
                x = ax + (bx - ax) * t
                z = az + (bz - az) * t
                nx, nz = (-(bz - az) / (seg or 1), (bx - ax) / (seg or 1))
                for off in (-SPREAD, 0.0, SPREAD):
                    px, pz = x + nx * off, z + nz * off
                    ck = f"{int(math.floor(px / CELL))}_{int(math.floor(pz / CELL))}"
                    if ck not in seen:
                        seen[ck] = (o["lat"] + pz / mlat, o["lon"] - px / mlon)
    print(f"{len(seen)} lookup cells to measure")
    keys = list(seen)
    els = elevations([seen[k] for k in keys], gmaps_key())
    ground = {k: round(v, 1) for k, v in zip(keys, els)}
    json.dump({"cell": CELL, "ground": ground}, open(OUT, "w"), separators=(",", ":"))
    print(f"tunnel_ground.json: {len(ground)} cells, "
          f"{min(ground.values()):.0f}..{max(ground.values()):.0f} m")


if __name__ == "__main__":
    main()
