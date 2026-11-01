#!/usr/bin/env python3
"""Ground truth for LandmarkPlacer.cs / AirportBuilder.cs, computed straight
from src/landmarks.js's own place() calls (lat/lon/yaw transcribed by hand,
cross-checked against the source at the given line numbers) and city.json.gz's
real terrain grid (the same half-res TRIANGLE sampler citymap.js/TerrainSampler.cs
use — see tools/_probe_city.py, whose height_at() this duplicates verbatim).

Prints one `LANDMARK id=... kind=... x=.. z=.. h_true=.. h_mirrored=.. yaw_deg=..`
line per landmark, in the SAME shape LandmarkProbe.cs prints (same key order,
same field names) so tools/unity_port_check.py actually pairs them — this is
the fix for the original version of this file, whose lines carried `name=`
(Georgian, quoted, with the JS line ref folded in) against the C# side's
`name=`/`kind=`/`asset=`/`ref=` mix: only 7 of 34 lines ever lined up. The id
here is a plain English slug (LandmarkProbe.cs declares the SAME slugs, in
the SAME declaration order, specifically to pair against this file — see its
own header comment) so the two sides key on identical text; the Georgian
display name itself is not part of either probe's line, so a translation bug
in it would NOT be caught here (it already is, independently, by UiProbe /
tools/ui_string_check.py, which does compare the Georgian text).

The decisive check: true heights must follow real Tbilisi topography (TV tower
~752 m, Sameba ~451 m, Rustaveli/Iveria valley ~438 m, Chronicle ~632 m, Delisi
spawn ~477 m — docs/QUALITY_BAR.md / the Unity porting brief). Mirrored heights
should NOT follow that pattern (697-912 m with no valley, per CityPlaceProbe).
This script asserts those five itself (LANDMARK_EXPECT lines, exit 1 on a
miss) — it is not something the cross-engine numeric diff can prove, since
both sides would have to independently get the SAME wrong placement to fool
it, so it stays a plain Python-side sanity check instead of a KEY=value the
Unity side is expected to echo back.
"""
import base64, gzip, json, math, struct, sys

d = json.load(gzip.open("public/tbilisi/city.json.gz"))
m, t = d["meta"], d["terrain"]
ORIGIN_LAT, ORIGIN_LON = m["origin"]["lat"], m["origin"]["lon"]
# Assets/Scripts/Data/GeoFrame.cs's own full-precision constants, NOT
# city.json's meta.mlat/mlon (111067.92 / 83206.34) — GeoFrame.cs's header
# comment explicitly warns those are rounded for the JSON and that the
# footprints/landmarks were placed with the full series. Sub-millimetre at
# these distances, but this script should compute with the exact same
# numbers the port does, not a "close enough" pair.
MLAT, MLON = 111067.92066361613, 83206.33860686835

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


def clamp(v, a, b):
    return a if v < a else (b if v > b else v)


def height_at(x, z):
    """src/citymap.js heightAt / TerrainSampler.cs HeightAtGame — the TRIANGLE
    the mesh actually draws, never bilinear (that was the roads-under-grass bug)."""
    fx = clamp((x - t["minx"]) / step2, 0, w2 - 1.001)
    fz = clamp((z - t["minz"]) / step2, 0, h2 - 1.001)
    c, r = int(fx), int(fz)
    dx, dz = fx - c, fz - r
    i = r * w2 + c
    h00, h10 = heights[i], heights[i + 1]
    h01, h11 = heights[i + w2], heights[i + w2 + 1]
    if dx + dz <= 1:
        return h00 + dx * (h10 - h00) + dz * (h01 - h00)
    return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11)


def to_local(lat, lon):
    """src/landmarks.js addLandmarks toLocal / src/airport.js ll — GAME frame
    (x=west). GeoFrame.FromLatLon gives the MIRRORED (Unity, x=east) result."""
    return (-(lon - ORIGIN_LON) * MLON, (lat - ORIGIN_LAT) * MLAT)


# id, lat, lon, yaw_js (radians), kind, js line ref — src/landmarks.js:1404-1448.
# Order matches LandmarkPlacer.cs's BuiltLandmarks / ProceduralLandmarks /
# HeroStatues declaration order exactly (that file's own header cites this).
NAMED = [
    ("tv_tower", 41.69575, 44.78499, 0.0, "built", "landmarks.js:1404"),
    ("sameba", 41.69754, 44.81654, 3.14, "built", "landmarks.js:1406"),
    ("radisson", 41.70392, 44.79369, -0.78, "built", "landmarks.js:1408"),
    ("chronicle", 41.77056, 44.81026, -1.38, "built", "landmarks.js:1410"),
    ("alphabet_tower", 41.65596, 41.63934, 0.0, "built", "landmarks.js:1411"),
    ("ferris_wheel", 41.6541, 41.63674, 1.2, "built", "landmarks.js:1412"),
    ("opera", 41.70112, 44.79626, 0.75, "built", "landmarks.js:1414"),
    ("justice_house", 41.699024, 44.806656, 0.0, "built", "landmarks.js:1416"),
    ("parliament", 41.69667, 44.79805, -1.1, "built", "landmarks.js:1419"),
    ("dinamo", 41.72285, 44.78966, -0.88, "built", "landmarks.js:1420"),
    ("philharmonic", 41.70731, 44.78601, -2.15, "built", "landmarks.js:1423"),
    ("narikala", 41.68787, 44.80894, -0.7, "procedural", "landmarks.js:1425"),
    ("funicular_station", 41.69489, 44.78652, -1.48, "procedural", "landmarks.js:1426"),
    ("metekhi", 41.69015, 44.8113, 0.0, "procedural", "landmarks.js:1427"),
    ("svetitskhoveli", 41.84227, 44.72088, 0.0, "procedural", "landmarks.js:1428"),
    ("jvari", 41.83838, 44.73355, 0.0, "procedural", "landmarks.js:1429"),
    ("baths", 41.6876, 44.8111, 0.0, "procedural", "landmarks.js:1430"),
    ("marriott", 41.69916, 44.7983, 0.7, "procedural", "landmarks.js:1431"),
    ("biltmore", 41.70245, 44.79467, 0.75, "procedural", "landmarks.js:1432"),
    ("sheraton_metechi", 41.68885, 44.82317, -1.55, "procedural", "landmarks.js:1433"),
    ("radisson_red", 41.70525, 44.80045, -1.0, "procedural", "landmarks.js:1434"),
    # hero statues — landmarks.js:1439-1448
    ("freedom_monument", 41.69339, 44.80150, -0.79, "statue", "landmarks.js:1439"),
    ("kartlis_deda", 41.68812, 44.80460, -1.13, "statue", "landmarks.js:1440"),
    ("gorgasali", 41.69032, 44.81073, -0.35, "statue", "landmarks.js:1441"),
    ("rustaveli_monument", 41.70389, 44.78993, -2.36, "statue", "landmarks.js:1442"),
    ("david_builder", 41.76157, 44.76860, -1.05, "statue", "landmarks.js:1443"),
    ("saint_nino", 41.76249, 44.76732, 0.52, "statue", "landmarks.js:1444"),
    ("saakadze", 41.7245, 44.7765, 1.57, "statue", "landmarks.js:1445"),
    ("vazha_pshavela", 41.72384, 44.72901, -1.75, "statue", "landmarks.js:1446"),
    ("writers_pair", 41.69775, 44.79818, -0.7, "statue", "landmarks.js:1447"),
    ("pharnavaz", 41.68883, 44.82045, 1.05, "statue", "landmarks.js:1448"),
]

# Expected real Tbilisi elevations (metres) — the decisive check. If the
# computed h_true values disagree with these, placement is wrong; the
# expectation is not to be adjusted (porting brief, 2026-10-26).
EXPECTED = {
    "tv_tower": 752,
    "sameba": 451,
    "radisson": 438,  # Rustaveli/Iveria valley floor
    "chronicle": 632,
    "delisi_spawn": 477,
}
EXPECT_TOL_M = 30  # "~" in the brief — real topography, not a surveyed figure

sanity_fail = False
for id_, lat, lon, yaw_js, kind, ref in NAMED:
    # to_local() returns GAME frame (x=west) — the right frame for height_at(),
    # which indexes the SAME heightmap grid citymap.js/TerrainSampler.cs's own
    # HeightAtGame does. But LandmarkProbe.cs prints `pos.x`, which is UNITY
    # frame (x=east, from GeoFrame.FromLatLon — TerrainSampler.HeightAt(u, z)
    # internally does `HeightAtGame(-u, z)` to convert back). Printing GAME-
    # frame x here would silently disagree with the C# side's x on EVERY
    # landmark (opposite sign) even though the port is correct — negate only
    # the PRINTED x, not the height_at() argument, so the two probes' LANDMARK
    # lines carry the same real-world point in the same frame.
    x, z = to_local(lat, lon)
    h_true = height_at(x, z)
    h_mirr = height_at(-x, z)
    yaw_unity_deg = math.degrees(-yaw_js)
    x_unity = -x
    print(f"LANDMARK id={id_} kind={kind} x={x_unity:.1f} z={z:.1f} "
          f"h_true={h_true:.1f} h_mirrored={h_mirr:.1f} yaw_deg={yaw_unity_deg:.1f}")
    print(f"JSONLY LANDMARK_MESH id={id_} ref={ref} lat={lat:.6f} lon={lon:.6f}")
    if id_ in EXPECTED:
        exp = EXPECTED[id_]
        ok = abs(h_true - exp) <= EXPECT_TOL_M
        sanity_fail = sanity_fail or not ok
        print(f"JSONLY LANDMARK_EXPECT id={id_} expected_m={exp} got_m={h_true:.1f} ok={int(ok)}")

# Delisi spawn (the data origin) — expected ~477 m.
h0 = height_at(0, 0)
print(f"LANDMARK id=delisi_spawn kind=origin x=0.0 z=0.0 "
      f"h_true={h0:.1f} h_mirrored={h0:.1f} yaw_deg=0.0")
exp0 = EXPECTED["delisi_spawn"]
ok0 = abs(h0 - exp0) <= EXPECT_TOL_M
sanity_fail = sanity_fail or not ok0
print(f"JSONLY LANDMARK_EXPECT id=delisi_spawn expected_m={exp0} got_m={h0:.1f} ok={int(ok0)}")

# Airport — 31L spawn and the two runway thresholds, for AirportBuilder's probe.
# Same GAME-vs-UNITY-frame note as the LANDMARK loop above: A/B are computed
# in GAME frame (right for height_at()) but PRINTED in UNITY frame (right for
# pairing against AirportBuilder.cs's GeoFrame.FromLatLon-derived A.x/B.x).
ap = json.load(open("public/tbilisi/airport.json"))
main = next((r for r in ap["runways"] if "31" in (r.get("ref") or "")), ap["runways"][0])
A = to_local(*main["pts"][0])
B = to_local(*main["pts"][-1])
L = math.hypot(B[0] - A[0], B[1] - A[1])
print(f"AIRPORT runway_ref={main['ref']} length_m={L:.1f} "
      f"A_13R_NW=({-A[0]:.1f},{A[1]:.1f}) h={height_at(*A):.1f} "
      f"B_31L_SE=({-B[0]:.1f},{B[1]:.1f}) h={height_at(*B):.1f}")

n_built = sum(1 for _, _, _, _, k, _ in NAMED if k == "built")
n_proc = sum(1 for _, _, _, _, k, _ in NAMED if k == "procedural")
n_hero = sum(1 for _, _, _, _, k, _ in NAMED if k == "statue")

# exclusions/streetStatues/busts: COUNTED from the live src/landmarks.js source
# text, never hand-typed — a literal here silently goes stale the moment the
# array gains or loses an entry (it did: this used to print exclusions=31
# while the source array had grown to 32 rings, e.g. the Concert Hall/
# philharmonic ring at landmarks.js:19 — a probe-side miscount, not a port
# bug; LandmarkPlacer.cs's Exclusions.Length agreed with the real array all
# along). Regexing the source is cheap insurance against the same drift
# happening again to any of these three counts.
import re as _re

_src = open("src/landmarks.js").read()
_excl_block = _re.search(r"EXCLUSIONS\s*=\s*\[(.*?)\n\];", _src, _re.S).group(1)
n_exclusions = len(_re.findall(r"\{\s*lat:", _excl_block))
_street_block = _re.search(r"STREET_STATUE_POINTS\s*=\s*\[(.*?)\];", _src, _re.S).group(1)
n_street = len(_re.findall(r"\[\s*[\d.]+\s*,\s*[\d.]+\s*\]", _street_block))
_bust_block = _re.search(r"BUST_POINTS\s*=\s*\[(.*?)\];", _src, _re.S).group(1)
n_busts = len(_re.findall(r"\[\s*[\d.]+\s*,\s*[\d.]+\s*\]", _bust_block))

print(f"LANDMARK_COUNT built={n_built} procedural={n_proc} heroStatues={n_hero} "
      f"exclusions={n_exclusions} streetStatues={n_street} busts={n_busts}")

if sanity_fail:
    print("FAIL - at least one LANDMARK_EXPECT elevation check did not match "
          "real Tbilisi topography (see ok=0 above). This is this script's OWN "
          "ground-truth sanity check, independent of the Unity-side diff.",
          file=sys.stderr)
    sys.exit(1)
