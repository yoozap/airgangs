"""Bring a downloaded prop GLB into the game at a sane weight.

  Blender -b -P tools/import_prop.py -- <source.glb> <out-name> [maxtex] [--tris N]
                                        [--spin <prefix>] [--spin-name <name>]

Downloaded props are routinely shipped at a resolution meant for a render, not
a game: the wooden table is 324 triangles carrying three 2048x2048 PNGs, 13 MB
in total, for an object about 1.5 m across. This resamples every texture to
`maxtex` (1024 by default, the same cap tools/build_characters.py uses) and
re-encodes as JPEG, which is the whole difference.

`--spin` keeps one part OUT of that join so the game can turn it: the objects
under a node whose name starts with `prefix` are joined into a second object of
their own, named by `--spin-name`, with its origin moved to the MEAN of its
vertices. For a rotor that mean is on the axis (five identical blades, evenly
spaced, average to the centre) where a bounding-box centre would be five
centimetres off it and the blades would wobble as they turned. Keep the object
unrotated and glTF hands the game a node whose local +Z is the way the prop
faces, which is the axis to spin about -- the same convention the aeroplanes'
propellers use.

A prop can be heavy the other way round too. The industrial fan is 400 000
triangles of modelled guard wire and nuts across 30 separate objects and no
texture at all -- 18 MB and thirty draw calls for a thing a metre tall. With
`--tris N` the whole prop is joined into ONE object (so it draws once per
material, not once per nut) and collapsed to that budget. Joining first is
what makes the collapse even: decimating 30 objects separately spends the
same ratio on a 65 000-triangle guard and on an 84-triangle screw.
"""
import os
import sys

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(args) < 2:
    sys.exit("usage: -- <source.glb> <out-name> [maxtex]")
SRC, NAME = args[0], args[1]
pos = [a for a in args[2:] if not a.startswith("--")]
MAX_TEX = int(pos[0]) if pos else 1024
TRIS = int(args[args.index("--tris") + 1]) if "--tris" in args else 0
SPIN = args[args.index("--spin") + 1] if "--spin" in args else None
SPIN_NAME = args[args.index("--spin-name") + 1] if "--spin-name" in args else "rotor"
# The spun part gets its own budget, because it is excluded from the body's
# join and so from the body's decimate: the fan's five blades arrived as
# 143 000 vertices of smooth-shaded aerofoil and sailed straight through.
SPIN_TRIS = int(args[args.index("--spin-tris") + 1]) if "--spin-tris" in args else 4000
OUT = os.path.join(ROOT, "public", "models", f"{NAME}.glb")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

shrunk = []
for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        was = tuple(img.size)
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        shrunk.append(f"{was[0]}x{was[1]} -> {img.size[0]}x{img.size[1]}")

def count():
    n = 0
    for o in bpy.data.objects:
        if o.type == "MESH":
            o.data.calc_loop_triangles()
            n += len(o.data.loop_triangles)
    return n


def owner(o):
    """The named node a mesh hangs under — the importer parents meshes to empties."""
    n = o.parent
    while n is not None and n.type == "MESH":
        n = n.parent
    return (n.name if n is not None else o.name)


def weld(objs, name):
    """Join a set of objects into one, transforms baked first."""
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    # The transforms have to be baked before the join or every piece that was
    # parented to its own empty (Sketchfab ships them that way) collapses onto
    # the origin -- the fan came out as a heap of guard wire in a cube.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.join()
    one = bpy.context.view_layer.objects.active
    one.name = name
    return one


tris = was_tris = count()
spun = None
if SPIN:
    picked = [o for o in bpy.data.objects
              if o.type == "MESH" and owner(o).startswith(SPIN)]
    if not picked:
        sys.exit(f"--spin {SPIN}: nothing under a node with that name")
    spun = weld(picked, SPIN_NAME)
    # the axis runs through the MEAN of the vertices, not the box centre
    import mathutils
    mid = mathutils.Vector((0, 0, 0))
    for v in spun.data.vertices:
        mid += v.co
    mid /= len(spun.data.vertices)
    for v in spun.data.vertices:
        v.co -= mid
    spun.location = spun.location + mid
    spun.data.calc_loop_triangles()
    had = len(spun.data.loop_triangles)
    if SPIN_TRIS and had > SPIN_TRIS:
        bpy.context.view_layer.objects.active = spun
        mod = spun.modifiers.new("decimate", "DECIMATE")
        mod.ratio = SPIN_TRIS / had
        bpy.ops.object.modifier_apply(modifier=mod.name)
        spun.data.calc_loop_triangles()
    print(f"[{NAME}] {SPIN_NAME}: {len(picked)} meshes, {len(spun.data.loop_triangles)} tris "
          f"(from {had}), pivot on the vertex mean")

if TRIS and tris > TRIS:
    meshes = [o for o in bpy.data.objects if o.type == "MESH" and o is not spun]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    # The transforms have to be baked before the join or every piece that was
    # parented to its own empty (Sketchfab ships them that way) collapses onto
    # the origin -- the fan came out as a heap of guard wire in a cube.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.join()
    one = bpy.context.view_layer.objects.active
    mod = one.modifiers.new("decimate", "DECIMATE")
    mod.ratio = TRIS / tris
    bpy.ops.object.modifier_apply(modifier=mod.name)
    tris = count()

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True,
                          export_image_format="JPEG", export_jpeg_quality=85,
                          export_apply=True)
print(f"[{NAME}] {tris} tris"
      + (f" (from {was_tris}, joined into one object)" if TRIS else "")
      + f", textures: {', '.join(shrunk) or 'unchanged'}")
print(f"[{NAME}] {os.path.getsize(SRC)//1024} KB -> {os.path.getsize(OUT)//1024} KB  {OUT}")
