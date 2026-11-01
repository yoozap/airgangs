#!/usr/bin/env python3
"""Ground truth for the road-network port, computed straight from
public/tbilisi/city.json.gz + public/tbilisi/roadnet.json.gz using the same
algorithm src/roads.js (mergeRoadNet) and src/citymap.js (_buildRoads/heightAt)
use. Compare with Assets/Editor/Probes/RoadProbe.cs via tools/unity_port_check.py
(relative tolerance — plain diff is useless between float64 Python and float32 C#).

Prints one ROADP id=... key=value line per fact:
  counts     — road/bridge/tunnel/junction totals after the merge
  byclass    — road COUNT by OSM highway class (0..16), net-sourced ways only
  deckarea   — sum(width * segment length) over every merged road, m^2
  rustaveli  — is a real way present near Rustaveli Avenue's real endpoints
  sample0..5 — 6 fixed points (first vertex of 6 widely-spaced merged roads):
               the TerrainSampler triangle height there, +0.8m, which is what
               the deck rides on — this is the "grass swallowed the asphalt"
               anti-regression check, replicated independently in Python.
"""
import base64
import gzip
import json
import math
import struct

CITY_PATH = "public/tbilisi/city.json.gz"
NET_PATH = "public/tbilisi/roadnet.json.gz"


def load_json_gz(path):
    with gzip.open(path, "rt") as f:
        return json.load(f)


# ---------------------------------------------------------------------------
# Terrain triangle sampler — identical formula to tools/_probe_city.py /
# TerrainSampler.cs (the h10..h01 diagonal split, NOT bilinear).
# ---------------------------------------------------------------------------
class Terrain:
    def __init__(self, t):
        raw = base64.b64decode(t["data"])
        full = struct.unpack("<%dH" % (len(raw) // 2), raw)
        W, H, STEP = t["w"], t["h"], t["step"]
        w2, h2 = (W - 1) // 2 + 1, (H - 1) // 2 + 1
        step2 = STEP * 2
        heights = [0.0] * (w2 * h2)
        for r in range(h2):
            fr = min(r * 2, H - 1)
            for c in range(w2):
                fc = min(c * 2, W - 1)
                heights[r * w2 + c] = full[fr * W + fc] / 10
        self.w2, self.h2, self.step2 = w2, h2, step2
        self.minx, self.minz = t["minx"], t["minz"]
        self.heights = heights

    def height_at(self, x, z):
        def clamp(v, a, b):
            return a if v < a else (b if v > b else v)

        fx = clamp((x - self.minx) / self.step2, 0, self.w2 - 1.001)
        fz = clamp((z - self.minz) / self.step2, 0, self.h2 - 1.001)
        c, r = int(fx), int(fz)
        dx, dz = fx - c, fz - r
        i = r * self.w2 + c
        h00, h10 = self.heights[i], self.heights[i + 1]
        h01, h11 = self.heights[i + self.w2], self.heights[i + self.w2 + 1]
        if dx + dz <= 1:  # the triangle the mesh draws, not bilinear
            return h00 + dx * (h10 - h00) + dz * (h01 - h00)
        return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11)


# ---------------------------------------------------------------------------
# mergeRoadNet — src/roads.js:44-94, in metres (the JS keeps decimetres until
# consumption; converting once here is equivalent and matches RoadBuilder.cs).
# ---------------------------------------------------------------------------
def legacy_entries(city, key):
    # PROBE-SIDE FIX (2026-10-27, found while running this pair): width here
    # is ALREADY METRES, not decimetres. src/roads.js's own mergeRoadNet uses
    # city.roads/bridges/tunnels entries completely RAW (`city.roads.filter
    # (...)`, no /10 anywhere on entry[0]) — only net.roads entries go through
    # legacy(), whose `entry[0] / 10` divides a genuinely decimetre w_dm field
    # from the QGIS pipeline. tools/build_map.py confirms it for the legacy
    # arrays too: `roads.append([width] + flat)` with ROAD_W a literal metre
    # table (5..18), no *10. RoadBuilder.cs's LoadLegacyLists (and its own
    # extensive comment) already get this right; THIS script divided by 10
    # unconditionally and was the one that was wrong, undercounting every
    # legacy road's width ~10x and understating deckarea by ~3.2x citywide
    # (37.48M m^2 here vs RoadProbe.cs's 120.10M m^2 before this fix).
    # Coordinates genuinely are decimetre on both sources and still need /10.
    out = []
    for e in city.get(key, []):
        width = e[0]
        flat = [v / 10.0 for v in e[1:]]
        out.append((width, flat))
    return out


def merge_road_net(city, net):
    m = net["meta"]
    origin, mlat, mlon = m["origin"], m["mlat"], m["mlon"]
    s, w, n, e = m["bbox"]
    xW = -(e - origin["lon"]) * mlon
    xE = -(w - origin["lon"]) * mlon
    zS = (s - origin["lat"]) * mlat
    zN = (n - origin["lat"]) * mlat

    def in_city(flat):
        for i in range(0, len(flat), 2):
            x, z = flat[i], flat[i + 1]
            if xW < x < xE and zS < z < zN:
                return True
        return False

    roads = [(w_, f) for (w_, f) in legacy_entries(city, "roads") if not in_city(f)]
    bridges = [(w_, f) for (w_, f) in legacy_entries(city, "bridges") if not in_city(f)]
    tunnels = [(w_, f) for (w_, f) in legacy_entries(city, "tunnels") if not in_city(f)]
    meta = {}  # index in `roads` -> (cls, lanes, oneway, layer)

    for entry in net["roads"]:
        w_dm, kind, layer, cls, lanes, oneway, roundabout = entry[:7]
        flat = [v / 10.0 for v in entry[7:]]
        width = w_dm / 10.0
        rec = {"cls": cls, "lanes": lanes, "oneway": bool(oneway), "layer": layer}
        if kind == 1:  # BRIDGE
            bridges.append((width, flat))
            continue
        if kind == 2:  # TUNNEL
            tunnels.append((width, flat))
            continue
        meta[len(roads)] = rec
        roads.append((width, flat))

    return {
        "roads": roads, "bridges": bridges, "tunnels": tunnels, "meta": meta,
        "junctions": net.get("junctions", []),
    }


def main():
    city = load_json_gz(CITY_PATH)
    net = load_json_gz(NET_PATH)
    merged = merge_road_net(city, net)
    terrain = Terrain(city["terrain"])

    print(f"ROADP id=counts roads={len(merged['roads'])} bridges={len(merged['bridges'])} "
          f"tunnels={len(merged['tunnels'])} junctions={len(merged['junctions'])} "
          f"net_roads={len(net['roads'])} net_junctions={len(net.get('junctions', []))}")

    byclass = {}
    for rec in merged["meta"].values():
        byclass[rec["cls"]] = byclass.get(rec["cls"], 0) + 1
    line = "ROADP id=byclass " + " ".join(f"c{c}={byclass.get(c, 0)}" for c in range(17))
    print(line)

    area = 0.0
    for width, flat in merged["roads"]:
        for i in range(0, len(flat) - 2, 2):
            dx = flat[i + 2] - flat[i]
            dz = flat[i + 3] - flat[i + 1]
            area += width * math.hypot(dx, dz)
    print(f"ROADP id=deckarea area_m2={area:.1f}")

    # Rustaveli Avenue — real endpoints (Freedom Square / Rustaveli Square).
    # OSM tags the way highway=construction, which is why it was missing
    # until the QGIS pipeline fix (tools/qgis_roads.py) recovered it.
    m = city["meta"]
    def to_game(lat, lon):
        return (-(lon - m["origin"]["lon"]) * m["mlon"], (lat - m["origin"]["lat"]) * m["mlat"])

    rus_pts = [to_game(41.6934, 44.8015), to_game(41.7067, 44.7936)]
    best = 1e18
    best_width = 0.0
    for width, flat in merged["roads"]:
        for i in range(0, len(flat), 2):
            x, z = flat[i], flat[i + 1]
            for rx, rz in rus_pts:
                d = math.hypot(x - rx, z - rz)
                if d < best:
                    best, best_width = d, width
    present = 1 if best < 60.0 else 0
    print(f"ROADP id=rustaveli present={present} nearest_m={best:.1f} width={best_width:.1f}")

    # 6 fixed sample points: first vertex of 6 widely-spaced NET-SOURCED roads
    # (merged["meta"] keys — i.e. real Tbilisi ways, not the country-wide
    # legacy list, which runs out to Batumi and would just clamp to the grid
    # edge and prove nothing). Same deterministic indices RoadProbe.cs uses.
    net_idx = sorted(merged["meta"].keys())
    n = len(net_idx)
    pick = [net_idx[min(n - 1, i)] for i in (0, n // 6, 2 * n // 6, 3 * n // 6, 4 * n // 6, 5 * n // 6)]
    for k, ri in enumerate(pick):
        _, flat = merged["roads"][ri]
        x, z = flat[0], flat[1]
        h = terrain.height_at(x, z)
        deck_y = h + 0.8
        print(f"ROADP id=sample{k} x={x:.1f} z={z:.1f} deck_y={deck_y:.3f} terrain_y={h:.3f} diff_cm=0.00")


if __name__ == "__main__":
    main()
