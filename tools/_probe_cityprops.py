#!/usr/bin/env python3
"""Ground truth for parks.js/metro.js/malls.js/zones.js/shopfronts.js,
computed straight from the JSON data files (no Unity, no Three.js).
Compare with CityPropsProbe.cs's "CITYPROPS id=summary" line via
tools/unity_port_check.py (relative tolerance; never plain diff) — e.g.:

  python3 tools/_probe_cityprops.py > /tmp/js.txt
  "$UNITY" -batchmode -quit -projectPath unity \
      -executeMethod TbilisiFly.Probes.CityPropsProbe.Run -logFile /tmp/cs.txt
  grep '^CITYPROPS id=summary' /tmp/cs.txt > /tmp/cs_summary.txt
  python3 tools/unity_port_check.py /tmp/js.txt /tmp/cs_summary.txt

Deliberately NOT reproduced here (RNG-dependent, not worth the fragility):
the procedural furniture counts (trees/benches added while walking unfurnished
parks' paths, lamps, flower beds) — those depend on src/parks.js's rnd() hash
walk and are logged by the C# side as "CITYPROPS id=parks_detail" instead,
informational only. What IS reproduced below is exactly what is countable
without any randomness: real OSM data counts and deterministic geometry
(path length/segment counts, the POI wall-eligible count after the same
dedup rule shopfronts.js applies).
"""
import json
import math
import os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")


def load(name):
    with open(os.path.join(ROOT, "public", "tbilisi", name), encoding="utf-8") as f:
        return json.load(f)


def dist(a, b):
    return math.hypot(b[0] - a[0], b[1] - a[1])


# ---- parks.js ----
parks_data = load("parks.json")
parks = parks_data["parks"]

path_segments = 0
path_length = 0.0
for p in parks:
    for path in p["paths"]:
        for s in range(len(path) - 1):
            length = dist(path[s], path[s + 1])
            if length < 0.3 or length > 400:
                continue
            path_segments += 1
            path_length += length

real_benches = len(parks_data["benches"])
real_trees = len(parks_data["trees"])
real_canopy = len(parks_data["canopy"])
pitches = len(parks_data["pitches"])
ponds = len(parks_data["ponds"])
hedges = sum(
    1 for h in parks_data["hedges"]
    if dist((h[0], h[1]), (h[2], h[3])) < 120
)

# ---- boundary.js / BoundaryWall.cs ----
# Previously the CityPropsProbe.cs "CITYPROPS id=boundary" line had ZERO
# matching Python line (docs/port_units/README.md's verify2 brief calls this
# out by name: 0 paired values for a 112-line unit). Reproduced here against
# the Unity (GeoFrame) frame directly — not boundary.js's own game-frame
# xa/xb — because BoundaryWall.cs's MinX/MaxX/MinZ/MaxZ are logged in Unity
# space (GeoFrame.FromLatLon: x=(lon-lon0)*mlon EAST, z=(lat-lat0)*mlat
# NORTH), and the clamp/fade math that follows is frame-agnostic linear
# bounds math — reproducing it against the Unity-frame box pairs directly
# with BoundaryWall.cs instead of needing a manual mirror on either side.
mapmeta = load("mapmeta.json")
ORIGIN_LAT, ORIGIN_LON = mapmeta["origin"]["lat"], mapmeta["origin"]["lon"]
# Assets/Scripts/Data/GeoFrame.cs's own full-precision constants (matches
# mapmeta.json's own mlat/mlon exactly at this origin — verified equal).
MLAT, MLON = 111067.92066361613, 83206.33860686835
tb = mapmeta["cities"]["tbilisi"]  # [latMin, lonMin, latMax, lonMax] — src/boundary.js:27
lat_min, lon_min, lat_max, lon_max = tb


def from_lat_lon(lat, lon):
    return ((lon - ORIGIN_LON) * MLON, (lat - ORIGIN_LAT) * MLAT)  # (x east, z north)


p_min_x, p_min_z = from_lat_lon(lat_min, lon_min)
p_max_x, p_max_z = from_lat_lon(lat_max, lon_max)
b_minX, b_maxX = min(p_min_x, p_max_x), max(p_min_x, p_max_x)
b_minZ, b_maxZ = min(p_min_z, p_max_z), max(p_min_z, p_max_z)

print(
    f"CITYPROPS id=boundary hasBox=1 walls=4 "
    f"minX={b_minX:.1f} maxX={b_maxX:.1f} minZ={b_minZ:.1f} maxZ={b_maxZ:.1f}"
)

# src/boundary.js:9-14 / BoundaryWall.cs's own public consts, transcribed
# (tiny pure constants, same convention as tools/_probe_damage.mjs's
# tearRadius/bridgeOf — never exported from either source).
WALL_BOTTOM, WALL_TOP = -300.0, 5200.0
V_STEP, H_STEP = 500.0, 420.0
FADE_DIST, MARGIN = 2600.0, 60.0

# _build's own 4-side list, src/boundary.js:41-46 / BoundaryWall.Build()'s
# identical `sides` array — same order so side index i pairs 1:1.
sides = [
    ((b_minX, b_minZ), (b_maxX, b_minZ), "z", b_minZ),
    ((b_minX, b_maxZ), (b_maxX, b_maxZ), "z", b_maxZ),
    ((b_minX, b_minZ), (b_minX, b_maxZ), "x", b_minX),
    ((b_maxX, b_minZ), (b_maxX, b_maxZ), "x", b_maxX),
]
for i, (a, bEnd, axis, at) in enumerate(sides):
    length = math.hypot(bEnd[0] - a[0], bEnd[1] - a[1])
    nV = max(2, round(length / V_STEP))
    nH = round((WALL_TOP - WALL_BOTTOM) / H_STEP)
    vertsExpected = (nV + 1) * 2 + (nH + 1) * 2
    print(f"BOUNDARYWALL id=side_{i} axis={axis} at={at:.1f} len={length:.1f} nV={nV} nH={nH} vertsExpected={vertsExpected}")

# clamp() — a point 500 m outside the west wall, well clear of the north/
# south walls too (z at box centre), src/boundary.js:96-107.
clamp_x = b_minX - 500.0
clamp_z = (b_minZ + b_maxZ) / 2.0
cx, cz, hit = clamp_x, clamp_z, False
if cx < b_minX + MARGIN:
    cx = b_minX + MARGIN
    hit = True
elif cx > b_maxX - MARGIN:
    cx = b_maxX - MARGIN
    hit = True
if cz < b_minZ + MARGIN:
    cz = b_minZ + MARGIN
    hit = True
elif cz > b_maxZ - MARGIN:
    cz = b_maxZ - MARGIN
    hit = True
print(f"BOUNDARYWALL id=clamp inX={clamp_x:.1f} inZ={clamp_z:.1f} outX={cx:.1f} outZ={cz:.1f} hit={1 if hit else 0}")

# update()/Fade() — a point 100 m inside the west wall (within MARGIN of
# nothing, so clamp wouldn't touch it), time=0 so pulse=0.8 exactly
# (0.8 + 0.2*sin(0)), src/boundary.js:83-92.
fade_x, fade_z, fade_t = b_minX + 100.0, (b_minZ + b_maxZ) / 2.0, 0.0
pulse = 0.8 + 0.2 * math.sin(fade_t * 2.4)
for i, (a, bEnd, axis, at) in enumerate(sides):
    d = abs((fade_x if axis == "x" else fade_z) - at)
    k = max(0.0, 1.0 - d / FADE_DIST)
    opacity = k * k * 0.6 * pulse
    print(f"BOUNDARYWALL id=fade_{i} axis={axis} at={at:.1f} opacity={opacity:.6f}")

# ---- metro.js ----
metro = load("metro.json")
metro_stations = len(metro["stations"])
metro_entrances = sum(len(s["entrances"]) for s in metro["stations"])

# MetroBuilder.Build() itself (not just CityPropsProbe) already logs a
# "METRO stations=.. entrances=.. totems=.. portalOnly=.. placements=.."
# line every time CityPropsProbe.Print() runs (Assets/Scripts/City/
# MetroBuilder.cs:207-208) — no C# edit needed, it was simply never paired.
# The station/entrance/totem/placement bookkeeping (metro.js:204-237's 5
# hand-placed hero stations by English name, then the generic pav=="none"
# portal-only branch vs the pavilion branch, src/metro.js:281-304) is plain
# data logic with NO wall-snap geometry and NO randomness, so it is safe to
# transcribe faithfully (unlike ShopfrontBuilder's wall-snap placement,
# left unreproduced below — see that section's own note).
HERO_TOTEM = {"Marjanishvili", "Avlabari", "Rustaveli", "Station Square-1", "Station Square"}
HERO_NO_TOTEM = {"Liberty Square"}  # galleria — hero building only, no totem
m_stations = m_entrances = m_totems = m_portal_only = m_placements = 0
for st in metro["stations"]:
    m_stations += 1
    en = st.get("en", "")
    ents = st.get("entrances", [])
    if en in HERO_NO_TOTEM:
        m_placements += 1  # hero building
        m_entrances += len(ents)
        m_placements += len(ents)  # one "entrance" placement each, no totem
        continue
    if en in HERO_TOTEM:
        m_placements += 1  # hero building
        m_placements += 1  # door totem
        m_totems += 1
        # AddEntrancePortalsOnly() (metro.js:204-237 hero branch) adds ONLY an
        # "entrance" placement per mapped entrance, no totem — unlike the
        # generic-station loop below, which is the ONE place a totem rides
        # along with every entrance. Missing this distinction first (over-
        # crediting entrance totems to the 4 hero-with-totem stations) is
        # exactly what the first draft of this script got wrong: totems
        # read 51 instead of the real 47, placements 110 instead of 106 —
        # off by exactly the hero stations' own entrance count (4).
        m_entrances += len(ents)
        m_placements += len(ents)  # entrance placement only
        continue
    # generic station
    pav = st.get("pav", "")
    if pav == "none":
        m_portal_only += 1
        m_placements += 1  # entrance portal at the station itself
        m_placements += 1  # name board
    else:
        m_placements += 1  # pavilion_arch / pavilion_hall
    m_placements += 1  # door totem, shared by both branches
    m_totems += 1
    m_entrances += len(ents)
    m_placements += 2 * len(ents)  # entrance + totem per mapped entrance
    m_totems += len(ents)

print(f"METRO stations={m_stations} entrances={m_entrances} totems={m_totems} portalOnly={m_portal_only} placements={m_placements}")

# ---- malls.js ----
malls = load("malls.json")
malls_in_json = len(malls)

# ---- zones.js ----
districts = load("districts.json")
subzones = load("subzones.json")
raions = len(districts)
subzone_count = len(subzones)

# ---- shopfronts.js — POI flatten + "named" dedup (src/shopfronts.js:99-137) ----
CAT_STYLE_KEYS = {
    "supermarket", "minimarket", "bakery", "butcher", "grocer", "clothes", "beauty",
    "electronics", "hardware", "furniture", "florist", "books", "jewelry", "alcohol",
    "auto", "kiosk", "pet", "toys", "sports", "optician", "gift", "shop", "cafe",
    "restaurant", "fastfood", "bar", "bank", "pharmacy", "post", "vet", "cinema",
    "hotel", "mcdonalds", "fuel", "market", "school", "kindergarten", "university",
    "hospital", "clinic", "police", "fire_station", "library", "theatre", "named",
}

pois2 = load("pois2.json")
names = list(pois2["names"])
pois = dict(pois2["pois"])

named_pts = None
named_path = os.path.join(ROOT, "public", "tbilisi", "named.json")
if os.path.exists(named_path):
    nd = json.load(open(named_path, encoding="utf-8"))
    base = len(names)
    names.extend(nd["names"])
    named_pts = [[p[0], p[1], p[2] + base, p[3] if len(p) > 3 else 0] for p in nd["pois"]["named"]]

brands_path = os.path.join(ROOT, "public", "tbilisi", "brands.json")
cat_override = {}
if os.path.exists(brands_path):
    for b in json.load(open(brands_path, encoding="utf-8")):
        if b.get("cat"):
            cat_override[b["name_ka"]] = b["cat"]

CELL = 80
grid = {}
all_pois = []


def gk(x, z):
    return (math.floor(x / CELL), math.floor(z / CELL))


def add_cat(cat0, pts):
    if cat0 not in CAT_STYLE_KEYS:
        return
    for x, z, ni, area in pts:
        bcat = cat_override.get(names[ni]) if ni >= 0 else None
        cat = bcat if (bcat and bcat in CAT_STYLE_KEYS) else cat0
        k = gk(x, z)
        if cat == "named":
            dup = False
            for di in (-1, 0, 1):
                for dj in (-1, 0, 1):
                    for p in grid.get((k[0] + di, k[1] + dj), []):
                        if p["cat"] != "named" and (p["x"] - x) ** 2 + (p["z"] - z) ** 2 < 30 * 30:
                            dup = True
                            break
                    if dup:
                        break
                if dup:
                    break
            if dup:
                continue
        rec = {"x": x, "z": z, "ni": ni, "cat": cat}
        all_pois.append(rec)
        grid.setdefault(k, []).append(rec)


for cat0, pts in pois.items():
    if cat0 != "named":
        add_cat(cat0, pts)
if named_pts is not None:
    add_cat("named", named_pts)
elif "named" in pois:
    add_cat("named", pois["named"])

shop_pois = len(all_pois)
shop_names = len(names)

print(
    f"CITYPROPS id=summary parks={len(parks)} pathSegments={path_segments} "
    f"pathLength={path_length:.1f} realBenches={real_benches} "
    f"realTrees={real_trees} realCanopy={real_canopy} "
    f"pitches={pitches} ponds={ponds} hedges={hedges} "
    f"metroStations={metro_stations} metroEntrances={metro_entrances} "
    f"mallsInJson={malls_in_json} raions={raions} subzones={subzone_count} "
    f"shopPois={shop_pois} shopNames={shop_names}"
)
