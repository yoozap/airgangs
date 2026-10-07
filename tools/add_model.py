#!/usr/bin/env python3
"""
Drop ANY 3D model (.obj / .glb / .gltf) into the game at real coordinates.

Usage:
  python3 tools/add_model.py mymodel.glb 41.7086 44.7962 --yaw 0.5 --scale 1.5

Where do models come from?
  - Your own photos -> Meshroom (free photogrammetry, alicevision.org):
    photograph a real building from all sides, Meshroom outputs a textured
    .obj — a REAL model of the REAL building. Legal because the photos are yours.
  - Any CC0/CC-BY model you have the rights to use.

The model is copied into public/models/custom/ and registered in
public/tbilisi/custom_models.json — the game loads and places it on the
terrain at the given lat/lon automatically.
"""
import argparse
import json
import os
import shutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEST = os.path.join(ROOT, "public", "models", "custom")
REG = os.path.join(ROOT, "public", "tbilisi", "custom_models.json")

p = argparse.ArgumentParser(description="Place a 3D model in TbilisiFly at real coordinates.")
p.add_argument("model", help="path to .obj / .glb / .gltf")
p.add_argument("lat", type=float)
p.add_argument("lon", type=float)
p.add_argument("--yaw", type=float, default=0.0, help="rotation in radians")
p.add_argument("--scale", type=float, default=1.0)
p.add_argument("--y-offset", type=float, default=0.0, help="meters above the terrain")
args = p.parse_args()

ext = os.path.splitext(args.model)[1].lower()
if ext not in (".obj", ".glb", ".gltf"):
    raise SystemExit(f"unsupported format {ext}; use .obj, .glb or .gltf")

os.makedirs(DEST, exist_ok=True)
name = os.path.basename(args.model)
shutil.copy(args.model, os.path.join(DEST, name))
# .obj often has a sibling .mtl and textures — copy them too if present
if ext == ".obj":
    base = os.path.splitext(args.model)[0]
    for extra in (base + ".mtl",):
        if os.path.exists(extra):
            shutil.copy(extra, DEST)

entries = []
if os.path.exists(REG):
    entries = json.load(open(REG))
entries = [e for e in entries if e["file"] != name]  # replace same-named model
entries.append({
    "file": name,
    "lat": args.lat,
    "lon": args.lon,
    "yaw": args.yaw,
    "scale": args.scale,
    "yOffset": args.y_offset,
})
json.dump(entries, open(REG, "w"), indent=1)
print(f"placed {name} at {args.lat},{args.lon} — {len(entries)} custom model(s) registered")
