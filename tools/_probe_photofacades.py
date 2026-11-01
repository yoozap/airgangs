#!/usr/bin/env python3
"""
Computes, straight from the data files (no Unity, no running game, no
browser), the same real-photo-facade facts the Unity-side PhotoFacadeProbe.cs
is meant to print — a from-scratch Python re-implementation of:

  PhotoFacades.LoadManifest / BuildAttribution / Assign (the exclusive
  cheapest-first photo<->building draw)

...against unity/Assets/Resources/Art/Textures/pb/manifest.json and
unity/Assets/StreamingAssets/tbilisi/city.json.gz — the EXACT same files the
C# port reads. This is deterministic, data-only maths (no terrain sampling,
no randomness), so unlike the window/balcony placement probes, this one is a
full oracle: every number it prints should match the C# probe exactly.

Ported from src/citymap.js:4066-4176 (manifest load + attribution) and
:4095-4143 (the exclusive assignment pass itself). The decisive number per
the JS's own comment: before the exclusive fix, matching every building
within 28 m of a photo put the same facade on up to a DOZEN neighbours
(1,532 buildings wearing 586 photos, 86% of them cloned). The fix: score
every (photo, building) pair inside the radius, take them cheapest-first,
and let each side be claimed once — so the max buildings sharing any single
photo MUST be exactly 1.

Prints `KEY=value` lines, each with a name distinct enough that
tools/unity_port_check.py's line-pairing (blank standalone numbers, keep
digits inside identifiers) lines them up against PhotoFacadeProbe.cs's output
one for one.

Run: python3 tools/_probe_photofacades.py
"""
import gzip
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SA = os.path.join(ROOT, "unity", "Assets", "StreamingAssets", "tbilisi")
PB = os.path.join(ROOT, "unity", "Assets", "Art", "Textures", "pb")

# ---- GeoFrame.cs constants, full precision (not used for the distance test
# itself -- see note below -- kept here only for documentation parity) ----
LAT0 = 41.7252
LON0 = 44.7458
M_LAT = 111067.92066361613
M_LON = 83206.33860686835

GRID_CELL = 30.0   # citymap.js:4082/4111 — Math.round(x / 30)
RADIUS = 28.0      # citymap.js:4123 — d < 28 * 28
RADIUS_SQ = RADIUS * RADIUS


def to_game_xz(lat, lon):
    """citymap.js:4079-4080 — the GAME frame (X=west). Distances and grid
    adjacency are invariant to a single consistent axis mirror (Unity mirrors
    X exactly once via GeoFrame), so computing everything in this frame, as
    citymap.js itself does, reproduces the same pairs and the same winner set
    the C# port gets via GeoFrame.FromLatLon/ToUnity."""
    return -(lon - LON0) * M_LON, (lat - LAT0) * M_LAT


def cell_key(x, z):
    return round(x / GRID_CELL), round(z / GRID_CELL)


def main():
    manifest_path = os.path.join(PB, "manifest.json")
    man = json.load(open(manifest_path))
    print(f"photofacades_manifest={len(man)}")

    # ---- attribution: the WHOLE manifest, counted the instant it loads
    # (citymap.js:4074-4078) — not the subset that goes on to win a photo. ----
    artists = set()
    for m in man.values():
        credit = m.get("credit") or ""
        first = credit.split(",")[0]
        if first:
            artists.add(first)
    attribution = (f" · {len(man)} building photos by {len(artists)} Wikimedia Commons "
                   "photographers (CC BY/BY-SA/CC0 — full list: textures/pb/CREDITS.txt)")
    print(f"photofacades_photographers={len(artists)}")
    print(f"photofacades_attribution_text={attribution}")

    # ---- load building footprints straight from city.json.gz ----
    with gzip.open(os.path.join(SA, "city.json.gz")) as fh:
        city = json.load(fh)
    buildings = city["buildings"]
    print(f"photofacades_cityfootprints={len(buildings)}")

    # pbGrid: photo entries with their game-frame position + 30 m cell key
    photos = []
    for key, m in man.items():
        x, z = to_game_xz(m["lat"], m["lon"])
        photos.append({"key": key, "x": x, "z": z,
                        "gi": round(x / GRID_CELL), "gj": round(z / GRID_CELL)})
    pb_grid = {}
    for p in photos:
        pb_grid.setdefault((p["gi"], p["gj"]), []).append(p)

    # bGrid: building centroid (game frame, matching the raw [h,x0,z0,...]
    # decimetre-packed footprints exactly like citymap.js's own b[1+i*2]/10) +
    # cell key. n < 3 is skipped, same as citymap.js:4109.
    b_grid = {}
    centroids = {}
    for bi, b in enumerate(buildings):
        n = (len(b) - 1) // 2
        if n < 3:
            continue
        cx = sum(b[1 + i * 2] for i in range(n)) / n / 10.0
        cz = sum(b[2 + i * 2] for i in range(n)) / n / 10.0
        centroids[bi] = (cx, cz)
        gi, gj = cell_key(cx, cz)
        b_grid.setdefault((gi, gj), []).append(bi)

    # pairs: every (photo, building) within RADIUS, 3x3 neighbour cells
    # (citymap.js:4117-4131)
    pairs = []
    for p in photos:
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                for bi in b_grid.get((p["gi"] + di, p["gj"] + dj), ()):
                    cx, cz = centroids[bi]
                    d2 = (p["x"] - cx) ** 2 + (p["z"] - cz) ** 2
                    if d2 < RADIUS_SQ:
                        pairs.append((d2, bi, p["key"]))

    # cheapest-first, each side claimed once (citymap.js:4132-4143)
    pairs.sort(key=lambda t: t[0])
    used_building = set()
    used_photo = set()
    assigned_by_building = {}
    photo_use_count = {}
    for d2, bi, pkey in pairs:
        if bi in used_building or pkey in used_photo:
            continue
        assigned_by_building[bi] = pkey
        used_building.add(bi)
        used_photo.add(pkey)
        photo_use_count[pkey] = photo_use_count.get(pkey, 0) + 1

    max_per_photo = max(photo_use_count.values()) if photo_use_count else 0
    print(f"photofacades_candidatepairs={len(pairs)}")
    print(f"photofacades_buildingsmatched={len(assigned_by_building)}")
    print(f"photofacades_photosused={len(used_photo)}")
    print(f"photofacades_maxperphoto={max_per_photo}")

    # a photo-relief sanity count: floors > 1 AND an assumed height >= 5 m
    # can't be computed without the building's real height (needs
    # BuildingFacts/BuildingKit height resolution, out of this oracle's
    # data-only scope) — left to the C# probe once it has a real building
    # list with heights; not printed here so nothing fakes a match.


if __name__ == "__main__":
    main()
