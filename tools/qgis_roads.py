#!/usr/bin/env python3
"""
Real Tbilisi road infrastructure, built with QGIS/GDAL.

The old pipeline (build_map.py) gave every road a width from a 12-entry table
keyed on highway=*, threw the `layer` tag away, and split ways into three flat
lists (roads / bridges / tunnels).  That is why the city has 6 m primaries where
the real ones are 21 m, why a flyover and the street under it fight for the same
height, and why the interchanges read as ribbons crossing on a lawn.

This script keeps the engineering tags:

  lanes, lanes:forward/backward, width  -> the REAL carriageway width
  layer, bridge, tunnel                 -> who goes over whom, and how
  oneway, junction                      -> dual carriageways and roundabouts
  surface, service, embankment, cutting

Pipeline (all through the QGIS 4.0 bundle's GDAL/OGR + qgis_process):

  1. ogr2ogr  osm.pbf -> GPKG, Tbilisi bbox, highway IS NOT NULL, EPSG:4326
     (a custom osmconf.ini promotes the engineering tags to real columns)
  2. python   -> game-local metres, real widths, junction graph from shared
                 OSM node coordinates, structure classification
  3. qgis_process native:buffer (data-defined half width) + native:dissolve
     -> the paved surface, used for junction aprons so streets meet in tarmac
        instead of notching into grass
  4. -> public/tbilisi/roadnet.json.gz

Run:  python3 tools/qgis_roads.py
"""

import gzip
import json
import math
import os
import sqlite3
import struct
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "tools", "cache")
GIS = os.path.join(ROOT, "tools", "gis")
PBF = os.path.join(CACHE, "georgia-latest.osm.pbf")
OUT = os.path.join(ROOT, "public", "tbilisi", "roadnet.json.gz")

QGIS = "/Applications/QGIS-final-4_0_3.app/Contents"
BIN = os.path.join(QGIS, "MacOS")
ENV = {
    **os.environ,
    "GDAL_DATA": os.path.join(QGIS, "Resources", "qgis", "gdal"),
    "PROJ_DATA": os.path.join(QGIS, "Resources", "qgis", "proj"),
    "PROJ_LIB": os.path.join(QGIS, "Resources", "qgis", "proj"),
    "QT_QPA_PLATFORM": "offscreen",
}

BBOX = (41.62, 44.68, 41.84, 44.98)  # S, W, N, E — same as build_map.py
ORIGIN = (41.7252, 44.7458)
lat0, lon0 = ORIGIN
phi = math.radians(lat0)
M_LAT = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
M_LON = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)

# Classes we build as driveable road surface. Everything else (footway, steps,
# path, track, cycleway…) is pavement/trail and is handled elsewhere.
DRIVE = {
    "motorway": 0, "trunk": 1, "primary": 2, "secondary": 3, "tertiary": 4,
    "unclassified": 5, "residential": 6, "living_street": 7, "service": 8,
    "motorway_link": 9, "trunk_link": 10, "primary_link": 11,
    "secondary_link": 12, "tertiary_link": 13, "road": 14, "busway": 15,
    # Paved but not driven: Rustaveli's promenades, Freedom Square, the old
    # town lanes. Without them the centre of Tbilisi renders as lawn.
    "pedestrian": 16,
}
# highway=construction is a street being REBUILT (Rustaveli Avenue, Kostava
# Street, Aghmashenebeli Avenue are all tagged this way right now). The road is
# still there on the ground, so it is kept and classified by construction=*,
# falling back to a width/lane guess when that tag is missing.
CONSTRUCTION_FALLBACK = "secondary"
# Fallback lane counts when OSM has neither width nor lanes.
DEF_LANES = {
    "motorway": 4, "trunk": 4, "primary": 4, "secondary": 2, "tertiary": 2,
    "unclassified": 2, "residential": 2, "living_street": 1, "service": 1,
    "motorway_link": 1, "trunk_link": 1, "primary_link": 1,
    "secondary_link": 1, "tertiary_link": 1, "road": 2, "busway": 2,
    "pedestrian": 2,
}
# Metres of asphalt per lane, by class (motorway lanes really are wider).
LANE_W = {
    "motorway": 3.75, "trunk": 3.6, "primary": 3.5, "secondary": 3.4,
    "tertiary": 3.2, "unclassified": 3.0, "residential": 3.0,
    "living_street": 2.8, "service": 2.8, "road": 3.0, "busway": 3.4,
    "pedestrian": 4.0,
}
# Hard shoulder / kerb offset added on top of the running lanes.
SHOULDER = {"motorway": 3.0, "trunk": 2.0, "primary": 1.2, "secondary": 1.0}


def to_local(lat, lon):
    """lat/lon -> (x, z) metres in the game frame. X = west, Z = north."""
    return (-(lon - lon0) * M_LON, (lat - lat0) * M_LAT)


def run(cmd, **kw):
    r = subprocess.run(cmd, env=ENV, capture_output=True, text=True, **kw)
    if r.returncode != 0:
        sys.stderr.write(r.stdout + "\n" + r.stderr + "\n")
        raise SystemExit(f"failed: {' '.join(cmd[:3])}…")
    return r.stdout


# ---------------------------------------------------------------- 1. extract

def write_osmconf():
    """GDAL's stock osmconf.ini drops lanes/width/layer into an hstore blob."""
    src = os.path.join(QGIS, "Resources", "qgis", "gdal", "osmconf.ini")
    want = ("name,highway,construction,lanes,width,layer,bridge,tunnel,oneway,"
            "junction,surface,ref,service,maxspeed,lanes:forward,lanes:backward,"
            "bridge:structure,man_made,area,access,embankment,cutting,covered,"
            "railway,waterway")
    out, sec = [], None
    for line in open(src).read().splitlines():
        if line.startswith("["):
            sec = line.strip()
        if sec == "[lines]" and line.startswith("attributes="):
            line = "attributes=" + want
        out.append(line)
    dst = os.path.join(GIS, "osmconf_roads.ini")
    open(dst, "w").write("\n".join(out) + "\n")
    return dst


def extract():
    os.makedirs(GIS, exist_ok=True)
    gpkg = os.path.join(GIS, "tbilisi_roads_wgs.gpkg")
    if os.path.exists(gpkg) and os.environ.get("REUSE_GPKG"):
        return gpkg
    if os.path.exists(gpkg):
        os.remove(gpkg)
    conf = write_osmconf()
    s, w, n, e = BBOX
    env = dict(ENV, OSM_CONFIG_FILE=conf)
    cmd = [os.path.join(BIN, "ogr2ogr"), "-f", "GPKG", gpkg, PBF, "lines",
           "-spat", str(w), str(s), str(e), str(n),
           "-where", "highway IS NOT NULL", "-nln", "roads", "-nlt", "LINESTRING"]
    r = subprocess.run(cmd, env=env, capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stdout + r.stderr)
        raise SystemExit("ogr2ogr failed")
    print(f"  extracted {gpkg}")
    return gpkg


# ------------------------------------------------------------- 2. geometry io

def gpkg_wkb(blob):
    """GeoPackage BLOB -> list of (lon, lat) for a LineString."""
    flags = blob[3]
    env = (flags >> 1) & 0x07
    hdr = 8 + {0: 0, 1: 32, 2: 48, 3: 48, 4: 64}[env]
    wkb = blob[hdr:]
    order = "<" if wkb[0] == 1 else ">"
    (typ,) = struct.unpack_from(order + "I", wkb, 1)
    if typ % 1000 != 2:  # only LineString (2), incl. Z/M variants
        return None
    (npt,) = struct.unpack_from(order + "I", wkb, 5)
    dims = 2 + (1 if typ // 1000 in (1, 3) else 0) + (1 if typ // 1000 in (2, 3) else 0)
    pts = struct.unpack_from(order + f"{npt * dims}d", wkb, 9)
    return [(pts[i * dims], pts[i * dims + 1]) for i in range(npt)]


def num(v):
    """OSM numeric tag -> float ('2', '2.5', '10 m', '3;3' all appear)."""
    if v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    t = str(v).strip().lower().replace(",", ".")
    for sep in (";", "|"):
        if sep in t:
            t = t.split(sep)[0]
    t = t.replace("m", "").strip()
    try:
        f = float(t)
        return f if f > 0 else None
    except ValueError:
        return None


def road_width(t):
    """Real carriageway width in metres from the OSM tags."""
    hw = t["highway"]
    w = num(t.get("width"))
    if w and 2 <= w <= 60:
        return w
    lanes = num(t.get("lanes"))
    if not lanes:
        fw, bw = num(t.get("lanes:forward")), num(t.get("lanes:backward"))
        if fw or bw:
            lanes = (fw or 0) + (bw or 0)
    if not lanes or lanes > 12:
        lanes = DEF_LANES.get(hw, 2)
        # a one-way member of a dual carriageway carries half the traffic
        if str(t.get("oneway") or "").lower() in ("yes", "1", "-1", "true") and lanes > 1:
            lanes = max(1, lanes / 2)
    lw = LANE_W.get(hw, 3.0)
    return lanes * lw + SHOULDER.get(hw, 0.6)


def yes(v):
    return v is not None and str(v).lower() not in ("no", "false", "0", "")


def is_tunnel(v):
    """tunnel=building_passage is a road running UNDER a building, not a bore.

    888 of the 1355 tunnel-tagged ways in the Tbilisi box are building
    passages — courtyard archways in the Sololaki/Vera blocks. Treating them
    as tunnels is what stood concrete portal arches on the pavement.
    """
    return str(v or "").lower() in ("yes", "covered", "culvert", "avalanche_protector")


def load(gpkg):
    con = sqlite3.connect(gpkg)
    con.row_factory = sqlite3.Row
    cols = [r[1] for r in con.execute("PRAGMA table_info(roads)")]
    geom = "geom" if "geom" in cols else "geometry"
    sel = ",".join(f'"{c}"' for c in cols if c != geom)
    rows = con.execute(f"SELECT {sel}, {geom} FROM roads").fetchall()
    out = []
    for r in rows:
        t = {k: r[k] for k in r.keys()}
        hw = t.get("highway")
        if hw == "construction":
            # the street exists, it is just being rebuilt — keep it, classified
            # by what it is being built as
            hw = str(t.get("construction") or "").strip().lower()
            if hw not in DRIVE:
                lanes = num(t.get("lanes")) or 0
                width = num(t.get("width")) or 0
                hw = ("primary" if lanes >= 4 or width >= 18
                      else CONSTRUCTION_FALLBACK if lanes >= 2 or width >= 9
                      else "residential")
            t["highway"] = hw
        if hw not in DRIVE:
            continue
        ring = gpkg_wkb(r[geom])
        if not ring or len(ring) < 2:
            continue
        pts = [to_local(lat, lon) for lon, lat in ring]
        # drop the zero-length repeats OSM sometimes carries
        clean = [pts[0]]
        for p in pts[1:]:
            if math.hypot(p[0] - clean[-1][0], p[1] - clean[-1][1]) > 0.05:
                clean.append(p)
        if len(clean) < 2:
            continue
        layer = num(t.get("layer"))
        out.append({
            "pts": clean,
            "hw": hw,
            "w": road_width(t),
            "layer": int(layer) if layer is not None else 0,
            "bridge": yes(t.get("bridge")),
            "tunnel": is_tunnel(t.get("tunnel")),
            "passage": str(t.get("tunnel") or "").lower() == "building_passage",
            "covered": yes(t.get("covered")),
            "oneway": str(t.get("oneway") or "").lower() in ("yes", "1", "true"),
            "roundabout": (t.get("junction") or "") in ("roundabout", "circular"),
            "lanes": int(num(t.get("lanes")) or 0),
            "surface": t.get("surface") or "",
            "name": t.get("name") or "",
            "bstruct": t.get("bridge:structure") or "",
            "service": t.get("service") or "",
        })
    con.close()
    return out


# --------------------------------------------------------- 3. junction graph

def junctions(roads):
    """Nodes shared by 3+ driveable ways ON THE SAME LEVEL = a real junction.

    OSM ways share exact node coordinates where they connect, so hashing the
    rounded coordinate is an exact join — far better than a geometric
    intersection test, which also fires where a flyover crosses over a street.
    """
    from collections import defaultdict
    hits = defaultdict(list)
    for i, r in enumerate(roads):
        if r["hw"] == "service":
            continue
        for p in r["pts"]:
            hits[(round(p[0], 2), round(p[1], 2), r["layer"])].append(i)
    out = []
    for (x, z, layer), ids in hits.items():
        ids = set(ids)
        if len(ids) < 3:
            continue
        # Apron radius = the widest road's own half width, never more: the pad
        # fills the notch the ribbons leave between them without bulging out
        # past the kerb line into the pavement.
        rad = max(roads[i]["w"] for i in ids) / 2
        out.append({"x": x, "z": z, "layer": layer, "r": rad, "n": len(ids)})
    return out


# ------------------------------------------------ 4. paved surface via QGIS

def paved_surface(roads):
    """qgis_process: buffer every carriageway by its own half-width, dissolve.

    Returns the total paved area (m^2) per layer — a sanity number that proves
    the widths are real, and the polygons themselves for the aprons.
    """
    src = os.path.join(GIS, "roads_local.gpkg")
    if os.path.exists(src):
        os.remove(src)
    # a GeoPackage in game-local metres (no CRS: the units already are metres,
    # and forcing a real CRS here would re-project away from the frame every
    # other asset in the game is placed in)
    geojson = os.path.join(GIS, "roads_local.geojson")
    feats = []
    for r in roads:
        feats.append({
            "type": "Feature",
            "properties": {"halfw": round(r["w"] / 2, 2), "layer": r["layer"],
                           "bridge": int(r["bridge"]), "tunnel": int(r["tunnel"])},
            "geometry": {"type": "LineString",
                         "coordinates": [[round(x, 2), round(z, 2)] for x, z in r["pts"]]},
        })
    with open(geojson, "w") as f:
        json.dump({"type": "FeatureCollection", "features": feats}, f)
    buf = os.path.join(GIS, "roads_buf.gpkg")
    dis = os.path.join(GIS, "roads_paved.gpkg")
    for p in (buf, dis):
        if os.path.exists(p):
            os.remove(p)
    run([os.path.join(BIN, "qgis_process"), "run", "native:buffer", "--",
         f"INPUT={geojson}", "DISTANCE=field:halfw", "SEGMENTS=6",
         "END_CAP_STYLE=1", "JOIN_STYLE=1", "DISSOLVE=false", f"OUTPUT={buf}"])
    run([os.path.join(BIN, "qgis_process"), "run", "native:dissolve", "--",
         f"INPUT={buf}", "FIELD=layer", f"OUTPUT={dis}"])
    # qgis_process names the output table after the file; ask the gpkg itself
    con = sqlite3.connect(dis)
    tbl = con.execute(
        "SELECT table_name FROM gpkg_contents WHERE data_type='features'").fetchone()
    n = con.execute(f'SELECT count(*) FROM "{tbl[0]}"').fetchone()[0] if tbl else 0
    con.close()
    # total tarmac, straight out of the dissolved polygons — the number that
    # says whether the widths are real (Tbilisi carries roughly 9-10 km2)
    area = run([os.path.join(BIN, "ogrinfo"), "-q", dis, "-sql",
                f'SELECT sum(ST_Area(geom)) a FROM "{tbl[0]}"']) if tbl else ""
    km2 = None
    for line in area.splitlines():
        if "a (Real)" in line or "a (Integer)" in line:
            try:
                km2 = float(line.split("=")[1]) / 1e6
            except ValueError:
                pass
    return dis, n, km2


# ------------------------------------------------------------------- 5. write

def main():
    if not os.path.exists(PBF) or os.path.getsize(PBF) < 10_000_000:
        raise SystemExit(f"missing {PBF} — download georgia-latest.osm.pbf first")
    print("== extract (QGIS/GDAL OSM driver) ==", flush=True)
    gpkg = extract()
    print("== read + real widths ==", flush=True)
    roads = load(gpkg)
    print(f"  {len(roads)} driveable ways", flush=True)
    by = {}
    for r in roads:
        b = by.setdefault(r["hw"], [0, 0.0])
        b[0] += 1
        b[1] += r["w"]
    for hw in sorted(by, key=lambda k: -by[k][0]):
        n, tw = by[hw]
        print(f"    {hw:16s} {n:6d}  mean width {tw / n:5.1f} m")
    nb = sum(1 for r in roads if r["bridge"])
    nt = sum(1 for r in roads if r["tunnel"])
    nl = sum(1 for r in roads if r["layer"])
    print(f"  {nb} bridge ways, {nt} tunnel ways, {nl} with a layer tag", flush=True)

    print("== junctions ==", flush=True)
    jns = junctions(roads)
    print(f"  {len(jns)} real junction nodes", flush=True)

    print("== paved surface (qgis_process buffer+dissolve) ==", flush=True)
    try:
        paved, nlayers, km2 = paved_surface(roads)
        print(f"  dissolved into {nlayers} level(s): {paved}"
              + (f"  ({km2:.2f} km2 of carriageway)" if km2 else ""), flush=True)
    except SystemExit as e:
        print(f"  skipped: {e}", flush=True)

    # --- pack: decimetre ints, same convention as city.json.gz
    KIND = {"road": 0, "bridge": 1, "tunnel": 2, "passage": 3}
    out_roads = []
    for r in roads:
        kind = (KIND["tunnel"] if r["tunnel"] else KIND["bridge"] if r["bridge"]
                else KIND["passage"] if r["passage"] else 0)
        flat = []
        for x, z in r["pts"]:
            flat += [round(x * 10), round(z * 10)]
        out_roads.append([
            round(r["w"] * 10), kind, r["layer"], DRIVE[r["hw"]],
            r["lanes"], 1 if r["oneway"] else 0, 1 if r["roundabout"] else 0,
        ] + flat)
    data = {
        "meta": {"origin": {"lat": lat0, "lon": lon0}, "mlat": round(M_LAT, 2),
                 "mlon": round(M_LON, 2), "bbox": BBOX,
                 "classes": {v: k for k, v in DRIVE.items()},
                 "head": ["w_dm", "kind", "layer", "cls", "lanes", "oneway", "roundabout"]},
        "roads": out_roads,
        "junctions": [[round(j["x"] * 10), round(j["z"] * 10), round(j["r"] * 10),
                       j["layer"], j["n"]] for j in jns],
    }
    raw = json.dumps(data, separators=(",", ":")).encode()
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with gzip.open(OUT, "wb", compresslevel=9) as f:
        f.write(raw)
    print(f"\n{OUT}: {os.path.getsize(OUT) / 1e6:.2f} MB gz ({len(raw) / 1e6:.1f} MB raw)")


if __name__ == "__main__":
    sys.exit(main())
