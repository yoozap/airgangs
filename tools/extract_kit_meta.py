#!/usr/bin/env python3
"""
Measure the real bounding boxes of the Blender-built kit GLBs so the Unity
port can pick kit variants WITHOUT opening the Unity editor or re-deriving
the numbers by hand.

Three.js (src/citymap.js) measures these at RUNTIME: it loads each kit GLB,
groups meshes by base name (glTF splits one multi-material mesh into one
primitive per material — `o.name.replace(/[._]\\d+$/, '')` restitches them),
merges the geometry per group and reads the merged bounding box:

    _loadOutKit   citymap.js:5574  models/outbuildings.glb  /^(garage|shed_yard|kiosk|hut_)/
    _loadAptKit   citymap.js:5661  models/apartments.glb    /^apt_/
    _loadShedKit  citymap.js:5758  models/sheds.glb         /^(shed_|hangar)/
    _loadHouseKit citymap.js:5856  models/houses.glb        /^house_/
    _loadPanelKit citymap.js:5957  models/panel_block.glb   /^block_(\\d)/  (sections 2..6)

Unity has no such step (BuildingKit.cs is pure data/mesh, no GameObject, no
scene load) so this script does the SAME measurement offline, in Python,
reading glTF accessor min/max directly (no Blender/Unity needed) and writes
one manifest consumed at runtime by BuildingKit.cs:

    unity/Assets/StreamingAssets/tbilisi/kit_meta.json

Run: python3 tools/extract_kit_meta.py
"""
import json
import re
import struct
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "public" / "models"
OUT = ROOT / "unity" / "Assets" / "StreamingAssets" / "tbilisi" / "kit_meta.json"


def load_glb(path):
    """Minimal GLB reader: returns the JSON chunk dict. No BIN chunk parsing
    needed — glTF accessors carry min/max, which is all a bounding box needs."""
    with open(path, "rb") as f:
        data = f.read()
    magic, version, length = struct.unpack_from("<4sII", data, 0)
    assert magic == b"glTF", f"{path} is not a GLB"
    offset = 12
    json_chunk = None
    while offset < length:
        chunk_len, chunk_type = struct.unpack_from("<I4s", data, offset)
        offset += 8
        chunk_data = data[offset:offset + chunk_len]
        offset += chunk_len
        if chunk_type == b"JSON":
            json_chunk = json.loads(chunk_data.decode("utf-8"))
            break
    assert json_chunk is not None, f"{path}: no JSON chunk"
    return json_chunk


def trs_matrix(node):
    if "matrix" in node:
        return np.array(node["matrix"], dtype=np.float64).reshape(4, 4).T
    t = node.get("translation", [0, 0, 0])
    r = node.get("rotation", [0, 0, 0, 1])  # x,y,z,w
    s = node.get("scale", [1, 1, 1])
    x, y, z, w = r
    # quaternion -> 3x3 rotation
    R = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ], dtype=np.float64)
    S = np.diag(s)
    M = np.eye(4)
    M[:3, :3] = R @ S
    M[:3, 3] = t
    return M


def world_matrices(gltf):
    """node index -> 4x4 world matrix, for the default scene."""
    nodes = gltf.get("nodes", [])
    world = {}

    def visit(idx, parent):
        node = nodes[idx]
        local = trs_matrix(node)
        m = parent @ local
        world[idx] = m
        for c in node.get("children", []):
            visit(c, m)

    scene_idx = gltf.get("scene", 0)
    roots = gltf["scenes"][scene_idx]["nodes"]
    for r in roots:
        visit(r, np.eye(4))
    return world


def mesh_group_bounds(path, name_filter):
    """-> {base_name: (W, D, H)} in WORLD space, mirroring the JS traverse +
    mergeGeometries + computeBoundingBox pipeline (minus the actual vertex
    merge — min/max of each primitive's own AABB, transformed to world space
    and unioned, gives the identical box without touching any vertex data)."""
    gltf = load_glb(path)
    world = world_matrices(gltf)
    meshes = gltf.get("meshes", [])
    accessors = gltf.get("accessors", [])

    groups = {}  # base_name -> list of (min8corners world)
    base_re = re.compile(r"[._]\d+$")

    for node_idx, node in enumerate(gltf.get("nodes", [])):
        if "mesh" not in node:
            continue
        name = node.get("name", f"node{node_idx}")
        base = base_re.sub("", name)
        if not name_filter(base):
            continue
        mesh = meshes[node["mesh"]]
        M = world[node_idx]
        for prim in mesh.get("primitives", []):
            pos_idx = prim.get("attributes", {}).get("POSITION")
            if pos_idx is None:
                continue
            acc = accessors[pos_idx]
            mn = acc.get("min")
            mx = acc.get("max")
            if mn is None or mx is None:
                continue
            # 8 corners of the local AABB -> world space
            corners = np.array([
                [mn[0], mn[1], mn[2], 1], [mx[0], mn[1], mn[2], 1],
                [mn[0], mx[1], mn[2], 1], [mx[0], mx[1], mn[2], 1],
                [mn[0], mn[1], mx[2], 1], [mx[0], mn[1], mx[2], 1],
                [mn[0], mx[1], mx[2], 1], [mx[0], mx[1], mx[2], 1],
            ], dtype=np.float64)
            world_corners = (M @ corners.T).T[:, :3]
            groups.setdefault(base, []).append(world_corners)

    out = {}
    for base, boxes in groups.items():
        all_pts = np.concatenate(boxes, axis=0)
        bmin = all_pts.min(axis=0)
        bmax = all_pts.max(axis=0)
        W = float(bmax[0] - bmin[0])
        H = float(bmax[1] - bmin[1])
        D = float(bmax[2] - bmin[2])
        out[base] = (W, D, H)
    return out


def panel_sections(path):
    # NOTE: unlike the other kits, panel_block.glb's exporter emits ONE bare
    # node name per variant ("block_2".."block_6") with no primitive-split
    # suffix (confirmed by inspection), so the usual `[._]\d+$` strip would
    # eat the trailing digit itself ("block_2" -> "block") and lose the
    # section count. Match the raw name directly.
    gltf = load_glb(path)
    sections = set()
    for node in gltf.get("nodes", []):
        if "mesh" not in node:
            continue
        m = re.match(r"^block_(\d)$", node.get("name", ""))
        if m:
            sections.add(int(m.group(1)))
    return sorted(sections)


def main():
    manifest = {}

    # ---- outbuildings: garage / shed_yard / kiosk / hut_ (citymap.js:5574) --
    out_path = MODELS / "outbuildings.glb"
    if out_path.exists():
        groups = mesh_group_bounds(out_path, lambda b: re.match(r"^(garage|shed_yard|kiosk|hut_)", b))
        items = [{"name": n, "W": w, "D": d, "H": h} for n, (w, d, h) in groups.items()]
        items.sort(key=lambda e: e["W"] * e["D"])
        manifest["out"] = items
        print(f"KIT out={len(items)} " + " ".join(f"{e['name']}:{e['W']:.1f}x{e['D']:.1f}x{e['H']:.1f}" for e in items))

    # ---- apartments: apt_ (citymap.js:5661) ---------------------------------
    apt_path = MODELS / "apartments.glb"
    if apt_path.exists():
        groups = mesh_group_bounds(apt_path, lambda b: re.match(r"^apt_", b))
        items = [{"name": n, "W": w, "D": d, "H": h} for n, (w, d, h) in groups.items()]
        # JS keeps Map insertion order (first-seen order in the node list) —
        # not sorted. Stabilize by name so re-runs are deterministic.
        items.sort(key=lambda e: e["name"])
        manifest["apt"] = items
        print(f"KIT apt={len(items)} " + " ".join(f"{e['name']}:{e['W']:.1f}x{e['D']:.1f}x{e['H']:.1f}" for e in items))

    # ---- sheds: shed_ / hangar (citymap.js:5758) ----------------------------
    shed_path = MODELS / "sheds.glb"
    if shed_path.exists():
        groups = mesh_group_bounds(shed_path, lambda b: re.match(r"^(shed_|hangar)", b))
        items = [{"name": n, "W": w, "D": d, "H": h} for n, (w, d, h) in groups.items()]
        items.sort(key=lambda e: e["W"] * e["D"])
        manifest["shed"] = items
        print(f"KIT shed={len(items)} " + " ".join(f"{e['name']}:{e['W']:.1f}x{e['D']:.1f}x{e['H']:.1f}" for e in items))

    # ---- houses: house_ (citymap.js:5856) -----------------------------------
    house_path = MODELS / "houses.glb"
    if house_path.exists():
        groups = mesh_group_bounds(house_path, lambda b: re.match(r"^house_", b))
        items = [{"name": n, "W": w, "D": d, "H": h} for n, (w, d, h) in groups.items()]
        items.sort(key=lambda e: e["W"] * e["D"])
        manifest["house"] = items
        print(f"KIT house={len(items)} " + " ".join(f"{e['name']}:{e['W']:.1f}x{e['D']:.1f}x{e['H']:.1f}" for e in items))

    # ---- panel block: block_N sections (citymap.js:5957) --------------------
    panel_path = MODELS / "panel_block.glb"
    if panel_path.exists():
        secs = panel_sections(panel_path)
        manifest["panel_sections"] = secs
        print(f"KIT panel_sections={secs}")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(manifest, indent=1))
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
