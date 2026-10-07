"""Measure the REAL Mtkvari channel with the Google Elevation API.

  python3 tools/probe_river_profile.py

The 30 m DEM the map is built from cannot resolve a 60-100 m channel with 5 m
walls: it arrives as a broad ramp, which is why the water sheet sits too high
and climbs onto the embankment roads. This samples the real surface along the
Mtkvari centreline and across it, so the channel can be built from measured
numbers instead of a percentile guess.

Uses VITE_GMAPS_KEY from .env (the key already in the project for 3D Tiles).
Two requests of <=350 points; Elevation is billed per request, not per point.
"""
import json
import math
import os
import sys
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RIVER = os.path.join(ROOT, "public", "tbilisi", "river.json")
OUT = os.path.join(ROOT, "tools", "cache", "river_profile.json")
STEP = 220.0      # m between centreline samples
ARMS = (-110, -60, -30, 30, 60, 110)  # m across the channel


def key():
    for line in open(os.path.join(ROOT, ".env")):
        if line.startswith("VITE_GMAPS_KEY="):
            return line.split("=", 1)[1].strip()
    sys.exit("no VITE_GMAPS_KEY in .env")


def elevations(points, k):
    """points = [(lat, lon), ...] -> [metres]; batched to stay under the URL cap."""
    out = []
    for i in range(0, len(points), 256):
        chunk = points[i:i + 256]
        locs = "|".join(f"{la:.6f},{lo:.6f}" for la, lo in chunk)
        url = ("https://maps.googleapis.com/maps/api/elevation/json?locations="
               + urllib.parse.quote(locs) + "&key=" + k)
        with urllib.request.urlopen(url, timeout=60) as r:
            d = json.load(r)
        if d.get("status") != "OK":
            sys.exit(f"Elevation API: {d.get('status')} {d.get('error_message', '')}")
        out += [p["elevation"] for p in d["results"]]
    return out


def poly_len(pts):
    return sum(math.hypot(pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1])
               for i in range(len(pts) - 1))


def stitch_river(lines, names=("Mtkvari", "\u10db\u10e2\u10d9\u10d5\u10d0\u10e0\u10d8", "Kura")):
    """Join the named ways end to end into one downstream polyline."""
    pool = [list(L["pts"]) for L in lines if L["name"] in names]
    if not pool:
        sys.exit("no Mtkvari centreline in river.json")
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
    chains.sort(key=poly_len, reverse=True)
    best = chains[0]
    # local frame has Z = north, and the Mtkvari runs north-west to south-east
    # through Tbilisi, so downstream is DECREASING Z
    if best[0][1] < best[-1][1]:
        best.reverse()
    return best


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def main():
    k = key()
    riv = json.load(open(RIVER))
    o, mlat, mlon = riv["origin"], riv["mlat"], riv["mlon"]
    to_ll = lambda x, z: (o["lat"] + z / mlat, o["lon"] - x / mlon)  # noqa: E731

    # OSM splits the Mtkvari into 19 ways through the bbox (50 km in total);
    # the single longest one is the reach BELOW the city, so stitch them all
    # end to end and keep the piece that actually runs through Tbilisi.
    pts = stitch_river(riv["lines"])
    print(f"stitched centreline: {poly_len(pts) / 1000:.2f} km, "
          f"{len(pts)} vertices")

    # resample the centreline at a fixed step
    samples, acc = [], 0.0
    for i in range(len(pts) - 1):
        (x1, z1), (x2, z2) = pts[i], pts[i + 1]
        seg = math.hypot(x2 - x1, z2 - z1)
        if seg < 1e-6:
            continue
        t = STEP - acc
        while t <= seg:
            f = t / seg
            samples.append((x1 + (x2 - x1) * f, z1 + (z2 - z1) * f,
                            (x2 - x1) / seg, (z2 - z1) / seg))
            t += STEP
        acc = (acc + seg) % STEP

    query, meta = [], []
    for x, z, dx, dz in samples:
        nx, nz = -dz, dx          # across the channel
        query.append(to_ll(x, z)); meta.append(("mid", x, z))
        for a in ARMS:
            query.append(to_ll(x + nx * a, z + nz * a)); meta.append((a, x, z))
    print(f"centreline {len(samples)} stations, {len(query)} elevation points")
    els = elevations(query, k)

    stations, j = [], 0
    for x, z, dx, dz in samples:
        mid = els[j]; j += 1
        arms = {}
        for a in ARMS:
            arms[a] = els[j]; j += 1
        stations.append({"x": round(x, 1), "z": round(z, 1),
                         "lat": round(to_ll(x, z)[0], 6), "lon": round(to_ll(x, z)[1], 6),
                         "mid": round(mid, 2),
                         "arms": {str(a): round(v, 2) for a, v in arms.items()}})
    json.dump(stations, open(OUT, "w"), indent=1)

    print(f"\n{'km':>6} {'water':>8} {'L110':>7} {'L60':>7} {'L30':>7} "
          f"{'R30':>7} {'R60':>7} {'R110':>7}  bankrise")
    for i, s in enumerate(stations):
        a = s["arms"]
        rise = min(a["-110"], a["110"]) - s["mid"]
        print(f"{i * STEP / 1000:6.2f} {s['mid']:8.2f} {a['-110']:7.2f} {a['-60']:7.2f} "
              f"{a['-30']:7.2f} {a['30']:7.2f} {a['60']:7.2f} {a['110']:7.2f}  {rise:+6.2f}")
    mids = [s["mid"] for s in stations]
    print(f"\nwater surface: {mids[0]:.1f} m at the top -> {mids[-1]:.1f} m at the bottom, "
          f"fall {mids[0] - mids[-1]:.1f} m over {len(mids) * STEP / 1000:.1f} km")
    drops = sum(1 for i in range(1, len(mids)) if mids[i] > mids[i - 1] + 0.3)
    print(f"stations where the surface RISES downstream: {drops} (should be ~0)")
    print(f"saved {OUT}")


if __name__ == "__main__":
    main()
