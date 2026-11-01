#!/usr/bin/env python3
"""Ground truth for the tunnel-cutting port, computed straight from
public/tbilisi/city.json.gz + roadnet.json.gz + tunnel_ground.json using the
same algorithm src/citymap.js `_planTunnelCuts`/`_buildTunnels`/`_cutAt`/
`boreFloorAt` use. Compare with Assets/Editor/Probes/TunnelProbe.cs via
tools/unity_port_check.py (relative tolerance — plain diff is useless between
float64 Python and float32 C#).

Prints one TUNP id=... key=value line per fact, matching TunnelProbe.cs:
  counts            — cut/lid/bore totals after planning
  mesh              — not reproduced here (mesh tri counts need no cross-check
                       beyond the data that drives them, which the lines below cover)
  mouth0..11        — each bore mouth's position, floor Y, terrain Y there
  barrel0..N        — each bore's radius/vault rise/flyable flag
  trench0..7        — THE DECISIVE CHECK: cut height below terrain INSIDE a
                       trench, and (near) equal to it OUTSIDE one
  borefloor0..N     — boreFloorAt samples along the first flyable bore
"""
import base64
import gzip
import json
import math
import os
import struct

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CITY_PATH = os.path.join(ROOT, "public/tbilisi/city.json.gz")
NET_PATH = os.path.join(ROOT, "public/tbilisi/roadnet.json.gz")
GROUND_PATH = os.path.join(ROOT, "public/tbilisi/tunnel_ground.json")

# src/citymap.js:448, 3353-3370
CUT_BATTER = 7.0
DEPTH = 5.4
BORE_DEPTH = 11.0
FLY_COVER = 14.0
GRADE = 0.07
BORE_GRADE = 0.11


def load_json_gz(path):
    with gzip.open(path, "rt") as f:
        return json.load(f)


class Terrain:
    # identical to tools/_probe_roads.py's Terrain (half-res TRIANGLE sampler,
    # NOT bilinear — see TerrainSampler.cs's own header for why that matters)
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


def legacy_entries(city, key):
    # PROBE-SIDE FIX (2026-10-30, pass-4 regression sweep): entry[0] on a
    # LEGACY city.roads/bridges/tunnels entry is ALREADY METRES. It used to
    # read `e[0] / 10.0` here, which made every legacy tunnel ten times too
    # narrow and so pinned `hw` at its 5.5 m floor for all of them.
    #
    # The same literal was found and fixed in tools/_probe_roads.py on
    # 2026-10-27 (see its own legacy_entries comment) and the fix was never
    # propagated to this file. Evidence, re-confirmed here:
    #   * src/roads.js:65-67 mergeRoadNet filters city.roads/bridges/tunnels
    #     RAW — nothing divides entry[0]. Only net.roads entries go through
    #     src/roads.js:35-39 legacy(), whose `entry[0] / 10` converts the QGIS
    #     pipeline's genuinely-decimetre w_dm field.
    #   * src/citymap.js:3435 `const width = tn[0];` — the tunnel planner reads
    #     the legacy width with no division, while dividing the COORDINATES
    #     (`tn[1 + i * 2] / 10`) on the very next line.
    #   * every other citymap.js consumer treats it as metres too:
    #     `road[0] < 6` (tracks/alleys), `road[0] / 2 + 1` (half-width),
    #     `road[0] >= 16` (trunk/motorway only).
    #   * tools/build_map.py writes `roads.append([width] + flat)` from a
    #     literal metre table (5..18), with no *10.
    # City/RoadBuilder.cs:338 reads it raw, which is correct; this oracle was
    # the stale side. Before the fix: cuts=1152 lids=74, all 8 sampled
    # trenches hw=5.50. After: matches the C#'s 1336/94 and the real
    # per-structure half-widths.
    out = []
    for e in city.get(key, []):
        width = float(e[0])
        flat = [v / 10.0 for v in e[1:]]
        out.append((width, flat))
    return out


def merge_road_net(city, net):
    # src/roads.js:44-94 mergeRoadNet, roads/tunnels only (bridges unused here)
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
    tunnels = [(w_, f) for (w_, f) in legacy_entries(city, "tunnels") if not in_city(f)]

    for entry in net["roads"]:
        w_dm, kind = entry[0], entry[1]
        flat = [v / 10.0 for v in entry[7:]]
        width = w_dm / 10.0
        if kind == 1:  # BRIDGE
            continue
        if kind == 2:  # TUNNEL
            tunnels.append((width, flat))
            continue
        roads.append((width, flat))

    return {"roads": roads, "tunnels": tunnels}


def building_centroids(city):
    # src/citymap.js:3315-3334 _indexBuildings
    out = []
    for b in city.get("buildings", []):
        n = (len(b) - 1) // 2
        if n < 3:
            continue
        sx = sz = 0.0
        for i in range(n):
            sx += b[1 + i * 2]
            sz += b[2 + i * 2]
        out.append((sx / n / 10.0, sz / n / 10.0))
    return out


class BuildingIndex:
    CELL = 50.0

    def __init__(self, pts):
        self.hash = {}
        for x, z in pts:
            k = (math.floor(x / self.CELL), math.floor(z / self.CELL))
            self.hash.setdefault(k, []).append((x, z))

    def near(self, x, z, r):
        # src/citymap.js:3336-3350 _buildingNear
        r2 = r * r
        cx0, cx1 = math.floor((x - r) / self.CELL), math.floor((x + r) / self.CELL)
        cz0, cz1 = math.floor((z - r) / self.CELL), math.floor((z + r) / self.CELL)
        for cx in range(cx0, cx1 + 1):
            for cz in range(cz0, cz1 + 1):
                for px, pz in self.hash.get((cx, cz), []):
                    if (px - x) ** 2 + (pz - z) ** 2 < r2:
                        return True
        return False


def load_ground_override(path):
    if not os.path.exists(path):
        return None, 20.0
    d = json.load(open(path))
    cell = d.get("cell", 20.0)
    g = {}
    for k, v in d.get("ground", {}).items():
        cx_s, cz_s = k.split("_", 1)
        g[(int(cx_s), int(cz_s))] = v
    return g, cell


def ground_get(g, cell, x, z, fallback):
    if g is None:
        return fallback
    k = (math.floor(x / cell), math.floor(z / cell))
    return g.get(k, fallback)


def dist_to_path(x, z, path):
    best = float("inf")
    for i in range(len(path) - 1):
        ex, ez = path[i + 1][0] - path[i][0], path[i + 1][1] - path[i][1]
        l2 = ex * ex + ez * ez
        t = ((x - path[i][0]) * ex + (z - path[i][1]) * ez) / l2 if l2 > 1e-6 else 0.0
        t = max(0.0, min(1.0, t))
        dx = x - (path[i][0] + ex * t)
        dz = z - (path[i][1] + ez * t)
        best = min(best, math.hypot(dx, dz))
    return best


def plan_tunnel_cuts(tunnels, roads, height_at, buildings, ground, gcell):
    # src/citymap.js:3352-3647 _planTunnelCuts
    ends = {}

    def key(x, z):
        return (round(x * 2), round(z * 2))

    for width, flat in roads:
        n = len(flat) // 2
        if n < 2:
            continue
        for i in (0, n - 1):
            k = key(flat[i * 2], flat[i * 2 + 1])
            ends.setdefault(k, []).append(((width, flat), i == 0))

    def walk(x, z, dx, dz, length):
        # citymap.js:3387-3422, incl. the odd-but-intentional append-not-replace
        # fallthrough when the matched road doesn't cover 60% of `length`
        out = []
        cands = ends.get(key(x, z), [])
        best = None
        best_dot = -1e18
        for (road, head) in cands:
            width, flat = road
            n = len(flat) // 2
            j = 1 if head else n - 2
            jx, jz = flat[j * 2] - x, flat[j * 2 + 1] - z
            l = math.hypot(jx, jz) or 1.0
            dot = (jx / l) * dx + (jz / l) * dz
            if dot > 0.4 and dot > best_dot:
                best_dot, best = dot, (road, head)
        if best is not None:
            (width, flat), head = best
            n = len(flat) // 2
            acc = 0.0
            px, pz = x, z
            k = 1
            while k < n and acc < length:
                i = k if head else n - 1 - k
                qx, qz = flat[i * 2], flat[i * 2 + 1]
                acc += math.hypot(qx - px, qz - pz)
                out.append((qx, qz))
                px, pz = qx, qz
                k += 1
            if acc >= length * 0.6:
                return out
        steps = 4
        for k in range(1, steps + 1):
            out.append((x + dx * (length * k) / steps, z + dz * (length * k) / steps))
        return out

    ways = []
    for width, flat in tunnels:
        n = len(flat) // 2
        if n < 2:
            continue
        pts = [(flat[i * 2], flat[i * 2 + 1]) for i in range(n)]
        L = sum(math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]) for i in range(1, n))
        if width < 5 and L < 40:
            continue
        if L < 12:
            continue
        ways.append({"pts": pts, "width": width, "L": L, "n": n})
    ways.sort(key=lambda w: -w["L"])

    taken = [False] * len(ways)
    groups = []
    for i in range(len(ways)):
        if taken[i]:
            continue
        taken[i] = True
        spine = ways[i]
        spread = spine["width"] / 2
        for j in range(i + 1, len(ways)):
            if taken[j]:
                continue
            far = 0.0
            for p in ways[j]["pts"]:
                far = max(far, dist_to_path(p[0], p[1], spine["pts"]))
            if far > 26:
                continue
            taken[j] = True
            spread = max(spread, far + ways[j]["width"] / 2)
        groups.append((i, spread))

    cuts, lids, bores = [], [], []

    for spine_idx, spread in groups:
        spine = ways[spine_idx]
        pts, width, n = spine["pts"], spine["width"], spine["n"]

        y0 = height_at(pts[0][0], pts[0][1])
        y1 = height_at(pts[n - 1][0], pts[n - 1][1])
        rise = 0.0
        for i in range(n):
            rise = max(rise, height_at(pts[i][0], pts[i][1]) - (y0 + (y1 - y0) * (i / (n - 1))))

        hw_guess = min(16.0, max(spread + 1.4, width / 2 + 1.4, 5.5))

        roofed = False
        for i in range(1, n - 1):
            f = i / (n - 1)
            if f < 0.2 or f > 0.8:
                continue
            if buildings.near(pts[i][0], pts[i][1], hw_guess + 5):
                roofed = True
                break

        side_hill = False
        for i in range(1, n - 1):
            f = i / (n - 1)
            if f < 0.25 or f > 0.75:
                continue
            dx, dz = pts[i + 1][0] - pts[i - 1][0], pts[i + 1][1] - pts[i - 1][1]
            l = math.hypot(dx, dz) or 1.0
            nx, nz = -dz / l, dx / l
            d = hw_guess + CUT_BATTER
            mid = height_at(pts[i][0], pts[i][1])
            up = max(height_at(pts[i][0] + nx * d, pts[i][1] + nz * d),
                     height_at(pts[i][0] - nx * d, pts[i][1] - nz * d))
            if up - mid > 5:
                side_hill = True
                break

        bore = rise > 6 or roofed or side_hill or spine["L"] > 260
        hw = min(16.0, max(spread + 1.4, width / 2 + 1.4, 5.5))
        bhw = min(11.0, max(width / 2 + 1.4, 5.0))

        dAx, dAz = pts[0][0] - pts[1][0], pts[0][1] - pts[1][1]
        lA = math.hypot(dAx, dAz) or 1.0
        dBx, dBz = pts[n - 1][0] - pts[n - 2][0], pts[n - 1][1] - pts[n - 2][1]
        lB = math.hypot(dBx, dBz) or 1.0

        depth = BORE_DEPTH if bore else DEPTH
        ramp_len = depth / (BORE_GRADE if bore else GRADE)
        ramp_a = walk(pts[0][0], pts[0][1], dAx / lA, dAz / lA, ramp_len)
        ramp_b = walk(pts[n - 1][0], pts[n - 1][1], dBx / lB, dBz / lB, ramp_len)

        corridor = list(reversed(ramp_a)) + pts + ramp_b
        sA = len(ramp_a)
        sB = sA + n - 1

        run = [0.0]
        for i in range(1, len(corridor)):
            run.append(run[i - 1] + math.hypot(corridor[i][0] - corridor[i - 1][0],
                                                corridor[i][1] - corridor[i - 1][1]))

        gnd = [height_at(p[0], p[1]) for p in corridor]
        gmeas = [ground_get(ground, gcell, p[0], p[1], gnd[i]) for i, p in enumerate(corridor)]

        sm = gnd[:]
        for _ in range(2):
            for i in range(1, len(sm) - 1):
                sm[i] = (sm[i - 1] + 2 * sm[i] + sm[i + 1]) / 4

        floor = [0.0] * len(corridor)
        if bore:
            yA, yB = sm[sA], sm[sB]
            span = (run[sB] - run[sA]) or 1.0
            for i in range(len(corridor)):
                if i < sA or i > sB:
                    floor[i] = sm[i]
                    continue
                t = (run[i] - run[sA]) / span
                line = yA + (yB - yA) * t
                in_from_end = min(run[i] - run[sA], run[sB] - run[i])
                w = min(1.0, in_from_end / 40)
                top = min(sm[i], gnd[i])
                floor[i] = min(line, top - FLY_COVER * w)
        else:
            last_run = run[-1]
            for i in range(len(corridor)):
                d = depth
                if i < sA:
                    d = depth * (run[i] / (run[sA] or 1.0))
                elif i > sB:
                    denom = (last_run - run[sB]) or 1.0
                    d = depth * (1 - (run[i] - run[sB]) / denom)
                floor[i] = sm[i] - max(0.0, d)

        for i in range(len(corridor) - 1):
            if bore and sA <= i < sB:
                continue
            cuts.append({
                "x1": corridor[i][0], "z1": corridor[i][1], "y1": floor[i],
                "x2": corridor[i + 1][0], "z2": corridor[i + 1][1], "y2": floor[i + 1],
                "hw": hw,
            })

        rec = {
            "pts": corridor[sA:sB + 1], "y": floor[sA:sB + 1],
            "gy": sm[sA:sB + 1], "gm": gmeas[sA:sB + 1],
            "hw": hw, "bhw": bhw, "width": width, "bore": bore,
        }
        if bore:
            bores.append(rec)
        else:
            lids.append(rec)

    return cuts, lids, bores


class CutField:
    # src/citymap.js:3626-3645 (hash) + 3692-3720 _cutAt
    CELL = 40.0

    def __init__(self, cuts):
        self.hash = {}
        for c in cuts:
            r = c["hw"] + CUT_BATTER + 4
            x0 = math.floor((min(c["x1"], c["x2"]) - r) / self.CELL)
            x1 = math.floor((max(c["x1"], c["x2"]) + r) / self.CELL)
            z0 = math.floor((min(c["z1"], c["z2"]) - r) / self.CELL)
            z1 = math.floor((max(c["z1"], c["z2"]) + r) / self.CELL)
            for cx in range(x0, x1 + 1):
                for cz in range(z0, z1 + 1):
                    self.hash.setdefault((cx, cz), []).append(c)

    def cut_at(self, x, z, base):
        arr = self.hash.get((math.floor(x / self.CELL), math.floor(z / self.CELL)))
        if not arr:
            return None
        best = None
        for c in arr:
            ex, ez = c["x2"] - c["x1"], c["z2"] - c["z1"]
            l2 = ex * ex + ez * ez
            if l2 < 1e-6:
                continue
            t = ((x - c["x1"]) * ex + (z - c["z1"]) * ez) / l2
            t = max(0.0, min(1.0, t))
            px, pz = c["x1"] + ex * t, c["z1"] + ez * t
            d2 = (x - px) ** 2 + (z - pz) ** 2
            outer = c["hw"] + CUT_BATTER
            if d2 > outer * outer:
                continue
            floor = c["y1"] + (c["y2"] - c["y1"]) * t
            y = floor
            d = math.sqrt(d2)
            if d > c["hw"]:
                y = floor + (base - floor) * ((d - c["hw"]) / CUT_BATTER)
            if best is None or y < best:
                best = y
        return best


def build_bore_tubes(bores, buildings):
    # src/citymap.js:7034-7173 (sizing + mouths only — the mesh itself is not
    # needed to verify the data TunnelProbe.cs prints)
    tubes, mouths = [], []
    PORTAL_CLEAR = 30.0
    for b in bores:
        P, Y, G = b["pts"], b["y"], b["gy"]
        dist = [0.0]
        for i in range(1, len(P)):
            dist.append(dist[i - 1] + math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]))
        acc = dist[-1]
        covers = []
        for i in range(len(P)):
            if dist[i] < PORTAL_CLEAR or acc - dist[i] < PORTAL_CLEAR:
                continue
            bld = 5.0 if buildings.near(P[i][0], P[i][1], 18) else 0.0
            top = max(G[i], b["gm"][i] if b.get("gm") else G[i])
            covers.append(top - Y[i] - bld)
        if not covers:
            covers = [G[i] - Y[i] for i in range(len(P))]
        covers.sort()
        cover = covers[len(covers) // 2]
        R = max(6.0, min(14.0, max(b["bhw"] * 1.7, 14.0)))
        H = max(9.0, min(12.0, cover - 2.0))
        flyable = R >= 8.5 and H >= 7.0
        tubes.append({"pts": P, "y": Y, "r": R, "h": H, "flyable": flyable})
        for i, j in ((0, 1), (len(P) - 1, len(P) - 2)):
            ex, ez = P[i][0] - P[j][0], P[i][1] - P[j][1]
            l = math.hypot(ex, ez) or 1.0
            mouths.append({
                "x": P[i][0], "z": P[i][1], "y": Y[i],
                "ang": math.atan2(-ex / l, -ez / l),
                "s": R / 6, "hill": True, "hw": R, "dx": ex / l, "dz": ez / l,
            })
    return tubes, mouths


class BoreField:
    # src/citymap.js:3253-3302 _indexBores / boreFloorAt
    CELL = 60.0

    def __init__(self, tubes):
        self.hash = {}
        for t in tubes:
            P = t["pts"]
            for i in range(len(P) - 1):
                pad = t["r"] + 6
                x1, z1 = P[i]
                x2, z2 = P[i + 1]
                x0 = math.floor((min(x1, x2) - pad) / self.CELL)
                x1b = math.floor((max(x1, x2) + pad) / self.CELL)
                z0 = math.floor((min(z1, z2) - pad) / self.CELL)
                z1b = math.floor((max(z1, z2) + pad) / self.CELL)
                for cx in range(x0, x1b + 1):
                    for cz in range(z0, z1b + 1):
                        self.hash.setdefault((cx, cz), []).append((t, i))

    def floor_at(self, x, z, y=None):
        arr = self.hash.get((math.floor(x / self.CELL), math.floor(z / self.CELL)))
        if not arr:
            return None
        for t, i in arr:
            P, Y, R, H = t["pts"], t["y"], t["r"], t["h"]
            x1, z1 = P[i]
            x2, z2 = P[i + 1]
            dx, dz = x2 - x1, z2 - z1
            l2 = (dx * dx + dz * dz) or 1.0
            tt = max(0.0, min(1.0, ((x - x1) * dx + (z - z1) * dz) / l2))
            px, pz = x1 + dx * tt, z1 + dz * tt
            if (x - px) ** 2 + (z - pz) ** 2 > R * R:
                continue
            floor = Y[i] + (Y[i + 1] - Y[i]) * tt
            if y is None or (floor - 4 < y < floor + H + 2):
                return floor
        return None


def main():
    city = load_json_gz(CITY_PATH)
    net = load_json_gz(NET_PATH)
    merged = merge_road_net(city, net)
    terrain = Terrain(city["terrain"])
    buildings = BuildingIndex(building_centroids(city))
    ground, gcell = load_ground_override(GROUND_PATH)

    cuts, lids, bores = plan_tunnel_cuts(
        merged["tunnels"], merged["roads"], terrain.height_at, buildings, ground, gcell)

    print(f"TUNP id=counts cuts={len(cuts)} lids={len(lids)} bores={len(bores)} "
          f"has_ground_override={1 if ground is not None else 0}")

    tubes, mouths = build_bore_tubes(bores, buildings)
    bore_field = BoreField(tubes)
    cut_field = CutField(cuts)

    for i, m in enumerate(mouths[:12]):
        gy = terrain.height_at(m["x"], m["z"])
        print(f"TUNP id=mouth{i} x={m['x']:.1f} z={m['z']:.1f} y={m['y']:.2f} "
              f"terrain_y={gy:.2f} hill={1 if m['hill'] else 0} hw={m['hw']:.2f} s={m['s']:.3f}")

    for i, t in enumerate(tubes):
        print(f"TUNP id=barrel{i} r={t['r']:.2f} h={t['h']:.2f} "
              f"flyable={1 if t['flyable'] else 0} pts={len(t['pts'])}")

    # THE DECISIVE CHECK, sampled at the MIDPOINT of each lid's own tunnel-way
    # span (`lid["pts"]`, not the approach ramps) — that span sits at the
    # full, constant DEPTH (src/citymap.js: no ramp-scaling between sA/sB), so
    # it is guaranteed to be the deepest, most representative point, unlike
    # the first few entries of `cuts` in list order, which are the SHALLOW
    # outer tip of an approach ramp (depth ramps from 0 there by design).
    li = 0
    for lid in lids:
        if li >= 8:
            break
        pts = lid["pts"]
        hw = lid["hw"]
        mid = len(pts) // 2
        mx, mz = pts[mid]
        uncut_in = terrain.height_at(mx, mz)
        cut_in = cut_field.cut_at(mx, mz, uncut_in)
        inside_y = cut_in if cut_in is not None else uncut_in
        inside_below = uncut_in - inside_y

        if mid + 1 < len(pts):
            ex, ez = pts[mid + 1][0] - pts[mid][0], pts[mid + 1][1] - pts[mid][1]
        else:
            ex, ez = pts[mid][0] - pts[mid - 1][0], pts[mid][1] - pts[mid - 1][1]
        length = math.hypot(ex, ez) or 1.0
        nx, nz = -ez / length, ex / length
        out_dist = hw + CUT_BATTER + 30
        ox, oz = mx + nx * out_dist, mz + nz * out_dist
        uncut_out = terrain.height_at(ox, oz)
        cut_out = cut_field.cut_at(ox, oz, uncut_out)
        outside_y = cut_out if cut_out is not None else uncut_out
        outside_diff = outside_y - uncut_out

        print(f"TUNP id=trench{li} hw={hw:.2f} inside_below_m={inside_below:.3f} "
              f"outside_diff_m={outside_diff:.4f}")
        li += 1
    print(f"TUNP id=trenchsamples count={li}")

    bi = 0
    for ti, t in enumerate(tubes):
        if not t["flyable"]:
            continue
        stride = max(1, len(t["pts"]) // 4)
        for k in range(0, len(t["pts"]), stride):
            px, pz = t["pts"][k]
            floor = bore_field.floor_at(px, pz, t["y"][k])
            fv = floor if floor is not None else -9999.0
            print(f"TUNP id=borefloor{bi} tube={ti} x={px:.1f} z={pz:.1f} "
                  f"floor={fv:.2f} expect={t['y'][k]:.2f}")
            bi += 1
        break
    print(f"TUNP id=flyablesamples count={bi}")

    # a point nowhere near any tunnel must come back null — proves the hash
    # lookup actually bounds the match instead of matching everything
    miss = bore_field.floor_at(-19000.0, -11600.0)
    print(f"TUNP id=boremiss floor={'none' if miss is None else f'{miss:.2f}'}")


if __name__ == "__main__":
    main()
