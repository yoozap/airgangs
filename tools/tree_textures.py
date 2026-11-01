# -*- coding: utf-8 -*-
"""Rescue the chestnut trees' albedo across the GLB -> FBX bridge.

Same defect as tools/plane_liveries.py (read its header): the two tree GLBs
store every image as image/webp (EXT_texture_webp) and Blender's FBX exporter
embeds them under an EXTENSIONLESS RelativeFilename, so Unity never makes a
Texture2D and VegetationRenderer.BuildWindMaterial finds _BaseMap null on all
10 tree draw groups -> the WindFoliage shader's "white" default -> the white
"crystal" clusters on the Old Town hillsides (docs/open_items.json
oldtown-white-tree-clusters, 2026-11-01).

The fix goes back to the GLB, decodes the WebP images and writes ordinary PNG
assets plus a material map that Editor/TreeTexturePostprocessor.cs applies by
MATERIAL NAME (the FBX carries the GLB's names: EU43brk4, EU43lef1, ...).

    python3 tools/tree_textures.py

Writes:
    unity/Assets/Resources/Art/Vegetation/trees_new/Tex/<stem>/alb_<mat>.png
    unity/Assets/Resources/Art/Vegetation/trees_new/Tex/tree_map.txt   (TSV)
"""
import glob
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import plane_liveries as pl  # noqa: E402  (glb_read / tex_source / image_bytes / save_albedo / save_plain)

GLB_DIR = os.path.join(ROOT, "public", "models", "trees_new")
TREES_DIR = os.path.join(ROOT, "unity", "Assets", "Resources", "Art", "Vegetation", "trees_new")
TEX_DIR = os.path.join(TREES_DIR, "Tex")
MAP_PATH = os.path.join(TEX_DIR, "tree_map.txt")
ASSET_PREFIX = "Assets/Resources/Art/Vegetation/trees_new/Tex/"
WRITE_NORMALS = False


def run():
    rows = []
    total_img = 0
    for glb in sorted(glob.glob(os.path.join(GLB_DIR, "*.glb"))):
        stem = os.path.splitext(os.path.basename(glb))[0]
        fbx = os.path.join(TREES_DIR, stem + ".fbx")
        if not os.path.exists(fbx):
            print(f"TREETEXGEN skip {stem}: no FBX")
            continue
        fbx_names = set(pl.fbx_material_names(fbx))
        js, bin_ = pl.glb_read(glb)
        out_dir = os.path.join(TEX_DIR, stem)
        os.makedirs(out_dir, exist_ok=True)
        n_alb = n_nrm = unmatched = 0
        for m in js.get("materials", []):
            name = m.get("name", "")
            if name not in fbx_names:
                unmatched += 1
                print(f"TREETEXGEN unmatched {stem} material={name!r} fbx={sorted(fbx_names)}")
                continue
            pbr = m.get("pbrMetallicRoughness", {})
            bct = pbr.get("baseColorTexture")
            if bct is not None:
                img, _ = pl.tex_source(js, bct["index"])
                dst = os.path.join(out_dir, f"alb_{name}.png")
                pl.save_albedo(pl.image_bytes(js, bin_, img), dst)
                rows.append((stem, name, "alb", ASSET_PREFIX + f"{stem}/alb_{name}.png"))
                n_alb += 1
                total_img += 1
            nt = m.get("normalTexture")
            # The WindFoliage shader has no _BumpMap and everything under
            # Resources/ ships in the build whether referenced or not, so the
            # 1024² normal maps would only cost memory: not written.
            if nt is not None and WRITE_NORMALS:
                img, _ = pl.tex_source(js, nt["index"])
                dst = os.path.join(out_dir, f"nrm_{name}.png")
                pl.save_plain(pl.image_bytes(js, bin_, img), dst)
                rows.append((stem, name, "nrm", ASSET_PREFIX + f"{stem}/nrm_{name}.png"))
                if nt.get("scale") is not None:
                    rows.append((stem, name, "nrmscale", "%.4f" % float(nt["scale"])))
                n_nrm += 1
                total_img += 1
            rows.append((stem, name, "alphaMode", m.get("alphaMode", "OPAQUE")))
        print(f"TREETEXGEN {stem:<16} materials={len(js.get('materials', []))} albedo={n_alb} normal={n_nrm} unmatched={unmatched}")
    os.makedirs(TEX_DIR, exist_ok=True)
    with open(MAP_PATH, "w", encoding="utf8") as f:
        f.write("# stem\tmaterial\trole\tvalue  -- written by tools/tree_textures.py; read by Editor/TreeTexturePostprocessor.cs\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    print(f"TREETEXGEN rows={len(rows)} images={total_img} map={os.path.relpath(MAP_PATH, ROOT)}")


if __name__ == "__main__":
    run()
