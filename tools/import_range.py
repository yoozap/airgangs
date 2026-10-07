"""Bring the new shooting range in as FURNITURE, not as a room.

  Blender -b -P tools/import_range.py

The download is a complete indoor range: floor, four walls, a suspended ceiling
with its lights, the entry doors, a pair of gun-safety posters -- and inside
that, the things that make it a range: lane dividers, target holders on their
rails and the lane controllers. It is going INSIDE the airport warehouse, which
has its own floor and walls, so those are stripped; the range's own CEILING is
kept, because it is what the lane lights and the target rail hang from.

Kept: everything whose name is not one of the shell parts below. The names are
the model's own ("SR Target Holder_dup_7.Shape_Target Paper"), so this survives
a re-download as long as the author keeps their naming.
"""
import os

import bpy
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(os.path.expanduser("~"), "Downloads", "shooting_range.glb")
OUT = os.path.join(ROOT, "public", "models", "shooting_range.glb")
MAX_TEX = 1024

# The shell. The two entry DOORS go with it: they are hung in the front wall,
# and with the wall gone they would stand in mid-air in the middle of the
# warehouse. The divider door stays -- that one belongs to the lane divider.
# The CEILING STAYS, on request: it carries the lane lights, the lane numbers
# and — the reason it had to come back once already — the target rail the
# holders hang from and run out along. The wall BEHIND THE TARGETS stays too -- the
# backstop. The model calls it "SR Wall Front"; its "SR Wall Back" is the one
# behind the firing line, and leaving that out is what lets the player walk
# straight in off the warehouse floor. Only the floor, the front and side walls and the two
# entry doors go, since the warehouse provides those and the doors would
# otherwise stand in mid-air.
#
# The SIGNAGE goes too: a pair of 84 x 61 cm "BASIC RULES OF GUN SAFETY"
# posters on the wall above the firing line. They are a real range's wall
# furniture and they read as a wall of English text in a Georgian game -- the
# one thing in the shed that is unmistakably somebody else's asset.
SHELL = ("sr floor", "sr wall back", "sr wall left", "sr wall right",
         "sr left door", "sr right door", "sr signage")
KEEP_ANYWAY = ()

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

dropped, kept = [], []
for o in list(bpy.data.objects):
    if o.type != 'MESH':
        continue
    n = (o.name or "").lower()
    if n.startswith(SHELL) and not (KEEP_ANYWAY and n.startswith(KEEP_ANYWAY)):
        dropped.append(o.name)
        bpy.data.objects.remove(o, do_unlink=True)
    else:
        kept.append(o.name)

# what is left, and how big is it
pts = [o.matrix_world @ Vector(c) for o in bpy.data.objects if o.type == 'MESH' for c in o.bound_box]
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
size = hi - lo

# sit it on its own floor and centre it on its footprint, so placing it in the
# game is just "put this point on the slab"
for o in bpy.data.objects:
    if o.type == 'MESH' and o.parent is None:
        o.location -= Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))

for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        img.file_format = 'JPEG'

bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', export_yup=True,
                          export_apply=True, export_animations=False,
                          export_image_format='JPEG', export_jpeg_quality=85)

tris = sum(len(p.vertices) - 2 for o in bpy.data.objects if o.type == 'MESH'
           for p in o.data.polygons)
print(f"[range] shell cut: {len(dropped)} meshes "
      f"({', '.join(sorted({d.split('.')[0] for d in dropped}))})")
print(f"[range] kept {len(kept)} meshes: "
      f"{', '.join(sorted({k.split('.')[0].replace('_dup_', ' x') for k in kept}))}")
print(f"[range] size {size.x:.1f} x {size.y:.1f} x {size.z:.1f} units, "
      f"{tris} tris, {os.path.getsize(OUT)//1024} KB")
