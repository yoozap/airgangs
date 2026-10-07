"""Bring a downloaded prop GLB into the game at a sane weight.

  Blender -b -P tools/import_prop.py -- <source.glb> <out-name> [maxtex]

Downloaded props are routinely shipped at a resolution meant for a render, not
a game: the wooden table is 324 triangles carrying three 2048x2048 PNGs, 13 MB
in total, for an object about 1.5 m across. This resamples every texture to
`maxtex` (1024 by default, the same cap tools/build_characters.py uses) and
re-encodes as JPEG, which is the whole difference.
"""
import os
import sys

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(args) < 2:
    sys.exit("usage: -- <source.glb> <out-name> [maxtex]")
SRC, NAME = args[0], args[1]
MAX_TEX = int(args[2]) if len(args) > 2 else 1024
OUT = os.path.join(ROOT, "public", "models", f"{NAME}.glb")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

shrunk = []
for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        was = tuple(img.size)
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        shrunk.append(f"{was[0]}x{was[1]} -> {img.size[0]}x{img.size[1]}")

tris = 0
for o in bpy.data.objects:
    if o.type == "MESH":
        o.data.calc_loop_triangles()
        tris += len(o.data.loop_triangles)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True,
                          export_image_format="JPEG", export_jpeg_quality=85,
                          export_apply=True)
print(f"[{NAME}] {tris} tris, textures: {', '.join(shrunk) or 'unchanged'}")
print(f"[{NAME}] {os.path.getsize(SRC)//1024} KB -> {os.path.getsize(OUT)//1024} KB  {OUT}")
