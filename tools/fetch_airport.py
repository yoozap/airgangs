#!/usr/bin/env python3
"""
Fetch Tbilisi International Airport (TBS) from OSM and write a compact
public/tbilisi/airport.json the game can drape onto the terrain:

  { runways: [{ref, width, pts:[[lat,lon],...]}],
    taxiways: [{width, pts:[...]}, ...],
    aprons: [[[lat,lon],...ring], ...],
    buildings: [{name, kind, height, ring:[[lat,lon],...]}],
    gates: [[lat,lon,ref], ...],
    tower: [lat,lon] }
"""
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "public", "tbilisi", "airport.json")
CACHE = os.path.join(ROOT, "tools", "cache", "airport_overpass.json")

# TBS airport grounds
BBOX = "41.655,44.925,41.685,44.975"

Q = f"""[out:json][timeout:120];
(
  way["aeroway"]({BBOX});
  relation["aeroway"]({BBOX});
  node["aeroway"]({BBOX});
  way["building"]({BBOX});
);
out tags geom;"""


def fetch():
    if os.path.exists(CACHE):
        return json.load(open(CACHE))
    data = None
    for host in (
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass-api.de/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
    ):
        try:
            req = urllib.request.Request(
                host,
                data=("data=" + urllib.parse.quote(Q)).encode(),
                headers={"User-Agent": "tbilisifly/1.0"},
            )
            data = json.load(urllib.request.urlopen(req, timeout=180))
            break
        except Exception as e:  # noqa: BLE001
            print(f"  {host} failed: {e}")
    if data is None:
        raise SystemExit("all overpass mirrors failed")
    os.makedirs(os.path.dirname(CACHE), exist_ok=True)
    json.dump(data, open(CACHE, "w"))
    return data


def ring_of(el):
    if "geometry" not in el:
        return None
    return [[round(g["lat"], 6), round(g["lon"], 6)] for g in el["geometry"]]


def stitch(ways):
    """Join way fragments (lists of [lat,lon]) into closed rings."""
    segs = [list(w) for w in ways if len(w) >= 2]
    rings = []
    while segs:
        ring = segs.pop(0)
        grown = True
        while grown and ring[0] != ring[-1]:
            grown = False
            for i, s in enumerate(segs):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += list(reversed(s))[1:]
                elif s[-1] == ring[0]:
                    ring = s[:-1] + ring
                elif s[0] == ring[0]:
                    ring = list(reversed(s))[:-1] + ring
                else:
                    continue
                segs.pop(i)
                grown = True
                break
        rings.append(ring)
    return rings


def main():
    data = fetch()
    out = {"runways": [], "taxiways": [], "aprons": [], "buildings": [], "gates": [],
           "tower": None, "windsocks": [], "navaids": [], "helipads": []}
    # main passenger terminal: multipolygon rel 20405381, fetched separately
    trel = os.path.join(ROOT, "tools", "cache", "terminal_rel.json")
    if os.path.exists(trel):
        ways = [[[round(g["lat"], 6), round(g["lon"], 6)] for g in el["geometry"]]
                for el in json.load(open(trel))["elements"] if el["type"] == "way"]
        rings = sorted(stitch(ways), key=len, reverse=True)
        if rings:
            out["buildings"].append({"name": "Tbilisi International Airport",
                                     "kind": "main_terminal", "height": 15,
                                     "levels": "3", "ring": rings[0]})
    for el in data["elements"]:
        t = el.get("tags", {})
        aero = t.get("aeroway")
        if el["type"] == "node":
            if aero == "gate":
                out["gates"].append([el["lat"], el["lon"], t.get("ref", "")])
            elif aero == "windsock":
                out["windsocks"].append([el["lat"], el["lon"]])
            elif aero == "navigationaid":
                out["navaids"].append([el["lat"], el["lon"]])
            continue
        pts = ring_of(el)
        if not pts:
            # relation: take outer member geometries
            if el["type"] == "relation":
                for m in el.get("members", []):
                    if m.get("role") == "outer" and "geometry" in m:
                        pts = [[round(g["lat"], 6), round(g["lon"], 6)] for g in m["geometry"]]
                        break
            if not pts:
                continue
        if aero == "runway":
            out["runways"].append({
                "ref": t.get("ref", ""),
                "width": float(t.get("width", 45)),
                "pts": pts,
            })
        elif aero == "taxiway":
            out["taxiways"].append({"width": float(t.get("width", 18)), "pts": pts, "ref": t.get("ref", "")})
        elif aero == "apron":
            out["aprons"].append(pts)
        elif aero == "helipad":
            cx = sum(p[0] for p in pts) / len(pts)
            cy = sum(p[1] for p in pts) / len(pts)
            out["helipads"].append([round(cx, 6), round(cy, 6)])
        elif aero == "tower" or t.get("man_made") == "tower" and "control" in t.get("tower:type", ""):
            cx = sum(p[0] for p in pts) / len(pts)
            cy = sum(p[1] for p in pts) / len(pts)
            out["tower"] = [round(cx, 6), round(cy, 6)]
        # only airport-specific buildings; the surrounding village houses are
        # already in city.json as generic extrusions
        if aero in ("terminal", "hangar"):
            h = t.get("height") or t.get("building:height")
            try:
                h = float(str(h).replace("m", "").strip()) if h else None
            except ValueError:
                h = None
            out["buildings"].append({
                "name": t.get("name", t.get("name:en", "")),
                "kind": aero or t.get("building", "yes"),
                "height": h,
                "levels": t.get("building:levels"),
                "ring": pts,
            })
    json.dump(out, open(OUT, "w"))
    print(f"runways={len(out['runways'])} taxiways={len(out['taxiways'])} "
          f"aprons={len(out['aprons'])} buildings={len(out['buildings'])} "
          f"gates={len(out['gates'])} tower={out['tower']}")
    for r in out["runways"]:
        print("  runway", r["ref"], r["width"], "m,", len(r["pts"]), "pts",
              r["pts"][0], "->", r["pts"][-1])
    for b in out["buildings"]:
        if b["kind"] in ("terminal", "hangar") or "erminal" in (b["name"] or ""):
            print("  bld", b["kind"], b["name"], b["height"], b["levels"], len(b["ring"]), "pts")


if __name__ == "__main__":
    main()
