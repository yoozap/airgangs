"""
Blender-headless: REAL small outbuildings — garages, garage rows, yard sheds,
kiosks and transformer huts. No photographic textures.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_outbuilding.py

WHY
---
The airport audit's remaining 675 photo-textured buildings turned out to be
overwhelmingly TINY: 4x4 m / 15 m2 (72 of them), 8x4 m / 21 m2 (53),
4x4 / 24 m2 (47), 8x4 / 24 m2 (42), 8x6 / 30 m2 (38), even 4x2 m / 7 m2 (32).
Nothing in the kit went below ~35 m2, so every garage and yard shed in the
district was still a prism with a photo of an apartment block on it.

A SIZE WARNING WORTH KEEPING: those rows also reported heights of 8-12 m. A
4x4 m garage is not 12 m tall — that is the satellite height estimate failing
on small footprints, and anything placing these must CLAMP the height rather
than trust it.

Exports: garage_1 (single), garage_row (4 bays), shed_yard, kiosk, hut_trafo.
Origins centred, +x along the long side, ground at y=0.
Output: public/models/outbuildings.glb
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Vector, noise

SEED = 77

BEVEL_ON = os.environ.get("BEVEL") == "1"
BEVEL_W = 0.01

COLLECTION_NAME = "Outbuildings"
OUTPUT_NAME = "outbuildings"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "public", "models")

M_BLOCK, M_BRICK, M_CONCRETE, M_WOOD, M_TRIM, M_GLASS, M_METAL, M_DOOR, M_ROOF = range(9)

PALETTE = [
    ("o_block",    "#B4AEA2", 0.95, 0.0),    # breeze block, the default
    ("o_brick",    "#9C6B4C", 0.94, 0.0),
    ("o_concrete", "#ADA79B", 0.92, 0.0),
    ("o_wood",     "#6E5438", 0.85, 0.0),
    ("o_trim",     "#7C8184", 0.70, 0.25),
    ("o_glass",    "#242A31", 0.14, 0.0),
    ("o_metal",    "#9AA0A2", 0.58, 0.38),   # corrugated / up-and-over door
    ("o_door",     "#46504F", 0.75, 0.10),
    ("o_roof",     "#8A8E8C", 0.82, 0.18),
]


def fresh_collection(name):
    ex = bpy.data.collections.get(name)
    if ex:
        for o in list(ex.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.collections.remove(ex)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    c = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(c)
    return c


def hex_rgb(hx):
    hx = hx.lstrip("#")
    o = []
    for i in (0, 2, 4):
        c = int(hx[i:i + 2], 16) / 255.0
        o.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return tuple(o)


def make_material(name, hx, rough, metal):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    rgb = hex_rgb(hx)
    if b:
        b.inputs["Base Color"].default_value = (*rgb, 1.0)
        b.inputs["Roughness"].default_value = rough
        b.inputs["Metallic"].default_value = metal
    m.diffuse_color = (*rgb, 1.0)
    return m


def activate(o):
    for x in bpy.context.view_layer.objects:
        x.select_set(False)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o


def call_op(op, **kw):
    try:
        return op(**kw)
    except TypeError as e:
        print(f"  ! {op.idname()} rejected an argument ({e})")
        return op(filepath=kw["filepath"])


def add_box(bm, corner, size, mat=0):
    x0, y0, z0 = corner
    x1, y1, z1 = x0 + size[0], y0 + size[1], z0 + size[2]
    pts = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
           (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    v = [bm.verts.new(p) for p in pts]
    for a, b, c, d in [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                       (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]:
        bm.faces.new((v[a], v[b], v[c], v[d])).material_index = mat


def add_quad(bm, pts, mat=0):
    bm.faces.new([bm.verts.new(p) for p in pts]).material_index = mat


def mono_roof(bm, W, D, z, fall, mat=M_ROOF, over=0.22):
    """Mono-pitch slab falling toward -y, with a real edge thickness and a
    small overhang all round. A flat lid is the thing that reads as a box."""
    t = 0.09
    zf = z - fall
    v = [bm.verts.new((-over, -over, zf)), bm.verts.new((W + over, -over, zf)),
         bm.verts.new((W + over, D + over, z)), bm.verts.new((-over, D + over, z))]
    bm.faces.new(v).material_index = mat
    vb = [bm.verts.new((-over, -over, zf - t)), bm.verts.new((W + over, -over, zf - t)),
          bm.verts.new((W + over, D + over, z - t)), bm.verts.new((-over, D + over, z - t))]
    bm.faces.new(list(reversed(vb))).material_index = mat
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((v[i], v[j], vb[j], vb[i])).material_index = mat
    # a rolled front edge, where the rain comes off
    add_box(bm, (-over, -over - 0.03, zf - t - 0.07), (W + 2 * over, 0.06, 0.1), M_METAL)


def corrugate(bm, x0, z0, w, h, mat=M_METAL, pitch=0.16, depth=0.022):
    """A field of shallow ribs: what makes a sheet-metal door or wall read as
    sheet metal instead of painted board."""
    n = max(2, int(w / pitch))
    for i in range(n):
        px = x0 + i * (w / n)
        add_box(bm, (px, -depth, z0), (w / n * 0.55, depth, h), mat)


def garage_door(bm, cx, z0, w, h):
    """Up-and-over steel door with its frame and the lintel over it."""
    x0 = cx - w / 2
    add_box(bm, (x0 - 0.1, -0.02, z0), (0.1, 0.22, h), M_TRIM)
    add_box(bm, (x0 + w, -0.02, z0), (0.1, 0.22, h), M_TRIM)
    add_box(bm, (x0 - 0.12, -0.05, z0 + h), (w + 0.24, 0.26, 0.18), M_CONCRETE)
    add_box(bm, (x0, 0.02, z0), (w, 0.07, h), M_METAL)
    corrugate(bm, x0, z0 + 0.05, w, h - 0.1)
    # handle + a vent slot, the two details you actually notice
    add_box(bm, (cx - 0.12, -0.06, z0 + h * 0.42), (0.24, 0.05, 0.05), M_TRIM)
    add_box(bm, (cx - w * 0.3, -0.05, z0 + h * 0.80), (w * 0.6, 0.04, 0.08), M_DOOR)


def wall_run(bm, length, h, mat, brick=False):
    add_box(bm, (0.0, 0.0, 0.0), (length, 0.2, h), M_BRICK if brick else mat)


def build_garage(bays, seed):
    rng = random.Random(seed)
    W = bays * 3.1
    D = 5.6
    h = 2.55
    brick = rng.random() < 0.4
    bm = bmesh.new()
    add_box(bm, (-0.06, -0.06, -0.15), (W + 0.12, D + 0.12, 0.15), M_CONCRETE)
    # back and sides
    add_box(bm, (0.0, D - 0.2, 0.0), (W, 0.2, h), M_BRICK if brick else M_BLOCK)
    add_box(bm, (0.0, 0.0, 0.0), (0.2, D, h), M_BRICK if brick else M_BLOCK)
    add_box(bm, (W - 0.2, 0.0, 0.0), (0.2, D, h), M_BRICK if brick else M_BLOCK)
    # front: pier between each bay, door in each
    for b in range(bays):
        bx = b * 3.1
        add_box(bm, (bx, 0.0, 0.0), (0.2, 0.2, h), M_BRICK if brick else M_BLOCK)
        garage_door(bm, bx + 1.55, 0.0, 2.45, 2.15)
    add_box(bm, (W - 0.2, 0.0, 0.0), (0.2, 0.2, h), M_BRICK if brick else M_BLOCK)
    mono_roof(bm, W, D, h, 0.28)
    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build_shed(seed):
    rng = random.Random(seed)
    W, D, h = 4.0, 3.2, 2.35
    bm = bmesh.new()
    add_box(bm, (-0.05, -0.05, -0.12), (W + 0.1, D + 0.1, 0.12), M_CONCRETE)
    # corrugated walls on a timber frame
    for (x0, y0, w, d) in ((0, D - 0.12, W, 0.12), (0, 0, 0.12, D), (W - 0.12, 0, 0.12, D)):
        add_box(bm, (x0, y0, 0.0), (w, d, h), M_WOOD)
    corrugate(bm, 0.0, 0.0, W, h)
    add_box(bm, (0.0, 0.0, 0.0), (W, 0.1, h), M_WOOD)
    # a plank door and a little window
    add_box(bm, (0.55, -0.04, 0.0), (0.9, 0.06, 1.95), M_WOOD)
    add_box(bm, (0.5, -0.06, 1.95), (1.0, 0.08, 0.1), M_WOOD)
    add_box(bm, (2.3, -0.05, 1.25), (0.75, 0.05, 0.62), M_TRIM)
    add_quad(bm, [(2.36, -0.02, 1.31), (2.99, -0.02, 1.31),
                  (2.99, -0.02, 1.81), (2.36, -0.02, 1.81)], M_GLASS)
    mono_roof(bm, W, D, h, 0.3, over=0.26)
    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build_kiosk(seed):
    W, D, h = 3.2, 2.4, 2.75
    bm = bmesh.new()
    add_box(bm, (-0.06, -0.06, -0.12), (W + 0.12, D + 0.12, 0.12), M_CONCRETE)
    add_box(bm, (0.0, D - 0.12, 0.0), (W, 0.12, h), M_TRIM)
    for sx in (0.0, W - 0.12):
        add_box(bm, (sx, 0.0, 0.0), (0.12, D, h), M_TRIM)
    # glazed front over a stallriser, with a serving counter
    add_box(bm, (0.12, 0.0, 0.0), (W - 0.24, 0.12, 0.95), M_TRIM)
    add_quad(bm, [(0.16, 0.06, 0.95), (W - 0.16, 0.06, 0.95),
                  (W - 0.16, 0.06, h - 0.45), (0.16, 0.06, h - 0.45)], M_GLASS)
    add_box(bm, (0.1, -0.2, 0.9), (W - 0.2, 0.3, 0.09), M_TRIM)      # counter
    add_box(bm, (W / 2 - 0.03, 0.0, 0.95), (0.06, 0.1, h - 1.4), M_TRIM)
    # fascia box + a shallow awning: a kiosk is mostly signage
    add_box(bm, (-0.06, -0.1, h - 0.45), (W + 0.12, 0.26, 0.45), M_METAL)
    add_box(bm, (-0.2, -0.75, h - 0.02), (W + 0.4, 0.75, 0.07), M_METAL)
    mono_roof(bm, W, D, h, 0.18, over=0.14)
    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build_hut(seed):
    """Transformer / utility hut: blank blockwork, louvred vents, steel door,
    warning plate. Tiny, but there are hundreds of them."""
    W, D, h = 3.0, 2.6, 2.9
    bm = bmesh.new()
    add_box(bm, (-0.08, -0.08, -0.15), (W + 0.16, D + 0.16, 0.15), M_CONCRETE)
    add_box(bm, (0.0, 0.0, 0.0), (W, D, h), M_BLOCK)
    # steel door with its frame, set into the front face
    add_box(bm, (W / 2 - 0.5, -0.05, 0.0), (1.0, 0.07, 2.05), M_DOOR)
    add_box(bm, (W / 2 - 0.56, -0.06, 0.0), (0.06, 0.09, 2.11), M_TRIM)
    add_box(bm, (W / 2 + 0.5, -0.06, 0.0), (0.06, 0.09, 2.11), M_TRIM)
    add_box(bm, (W / 2 - 0.56, -0.06, 2.05), (1.12, 0.09, 0.06), M_TRIM)
    # louvre stacks either side
    for lx in (0.35, W - 0.95):
        for i in range(5):
            add_box(bm, (lx, -0.04, 1.15 + i * 0.13), (0.6, 0.04, 0.08), M_TRIM)
    add_box(bm, (W / 2 - 0.14, -0.06, 2.25), (0.28, 0.03, 0.2), M_METAL)  # warning plate
    # capping band + a small overhang
    add_box(bm, (-0.1, -0.1, h), (W + 0.2, D + 0.2, 0.12), M_CONCRETE)
    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


BUILDERS = [
    ("garage_1",   lambda s: build_garage(1, s)),
    ("garage_row", lambda s: build_garage(4, s)),
    ("shed_yard",  build_shed),
    ("kiosk",      build_kiosk),
    ("hut_trafo",  build_hut),
]


def build(col):
    objs = []
    mats = [make_material(n, hx, r, m) for n, hx, r, m in PALETTE]
    for i, (name, fn) in enumerate(BUILDERS):
        bm = fn(SEED + i * 29)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        o = bpy.data.objects.new(name, me)
        col.objects.link(o)
        for m in mats:          # never materials.clear(): it zeroes the indices
            o.data.materials.append(m)
        me.polygons.foreach_set("use_smooth", [False] * len(me.polygons))
        me.update()
        if BEVEL_ON:
            md = o.modifiers.new("Bevel", "BEVEL")
            md.width = BEVEL_W
            md.segments = 1
            md.limit_method = "ANGLE"
            md.angle_limit = math.radians(50)
        o.location = (0, i * 12, 0)
        objs.append(o)
    return objs


def triangle_count(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    t = 0
    for o in objs:
        if o.type != "MESH":
            continue
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg))
        me.calc_loop_triangles()
        t += len(me.loop_triangles)
        bpy.data.meshes.remove(me)
    return t


def export(objs, d, name):
    os.makedirs(d, exist_ok=True)
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    path = os.path.abspath(os.path.join(d, name + ".glb"))
    call_op(bpy.ops.export_scene.gltf, filepath=path, export_format="GLB",
            use_selection=True, export_apply=True, export_yup=True,
            export_materials="EXPORT")
    return path


def main():
    random.seed(SEED)
    noise.seed_set(SEED)
    print("Blender", ".".join(str(v) for v in bpy.app.version))
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    col = fresh_collection(COLLECTION_NAME)
    objs = build(col)
    for o in objs:
        activate(o)
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    path = export(objs, OUTPUT_DIR, OUTPUT_NAME)
    print("-" * 64)
    for o in objs:
        print(f"    {o.name:12s} {triangle_count([o]):>6,} tris")
    print(f"Triangles : {triangle_count(objs):,}")
    print(f"Exported  : {path}")
    print("-" * 64)


if __name__ == "__main__":
    main()
