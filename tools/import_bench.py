"""Re-import the warehouse bench and strip its floor debris.

  Blender -b -P tools/import_bench.py

The downloaded table is fine structurally -- four complete legs, symmetric
(x -86..-60 and +49..+75, with 17 and 20 units of top overhang). What makes it
look broken in game is loose BLOCKS lying on the floor at the corners, 39 units
tall against a 226-unit table, connected to nothing. Standing next to the bench
they read as a leg that has come away from the top.

Anything grounded that is not part of a leg is removed.
"""
import os
import sys

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(os.path.expanduser("~"), "Downloads", "wooden_table.glb")
OUT = os.path.join(ROOT, "public", "models", "wooden_table.glb")
MAX_TEX = 1024

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

for o in list(bpy.data.objects):
    if o.type != 'MESH':
        continue
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.mesh.separate(type='LOOSE')

parts = []
for o in bpy.data.objects:
    if o.type != 'MESH' or not o.data.vertices:
        continue
    ws = [o.matrix_world @ v.co for v in o.data.vertices]
    parts.append(dict(obj=o,
                      x0=min(p.x for p in ws), x1=max(p.x for p in ws),
                      y0=min(p.y for p in ws), y1=max(p.y for p in ws),
                      lo=min(p.z for p in ws), hi=max(p.z for p in ws)))

floor = min(p["lo"] for p in parts)
top = max(p["hi"] for p in parts)
height = top - floor

# a leg: starts on the floor and reaches most of the way up
legs = [p for p in parts if p["lo"] < floor + 5 and (p["hi"] - p["lo"]) > height * 0.3]

def overlaps_leg(p):
    for l in legs:
        if p["x0"] <= l["x1"] and p["x1"] >= l["x0"] \
           and p["y0"] <= l["y1"] and p["y1"] >= l["y0"]:
            return True
    return False

junk = [p for p in parts
        if p["hi"] < floor + height * 0.5 and not overlaps_leg(p)]
for p in junk:
    bpy.data.objects.remove(p["obj"], do_unlink=True)

for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        img.file_format = 'JPEG'

bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', export_yup=True,
                          export_apply=True, export_animations=False,
                          export_image_format='JPEG', export_jpeg_quality=85)

tris = sum(len(f.vertices) - 2 for o in bpy.data.objects if o.type == 'MESH'
           for f in o.data.polygons)
print(f"[bench] {len(legs)} leg planes kept, {len(junk)} floor blocks removed, "
      f"{tris} tris, {os.path.getsize(OUT)//1024} KB")
