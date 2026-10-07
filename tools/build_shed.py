"""
Blender-headless: REAL industrial sheds, warehouses and hangars — the big flat
buildings that fill the airport district, the cargo apron and every industrial
edge of Tbilisi. No photographic textures.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_shed.py

WHY
---
The airport audit (tools/audit_airport_buildings.mjs) found 105 buildings over
800 m2 within 3 km of the runway, the largest 40,602 m2, all still extruded
prisms with a tiled photo on them. They are the most visible things in the
district precisely because they are enormous, and no kit model covered them.

WHAT MAKES A SHED READ AS A SHED
--------------------------------
Not windows — ribs. A profiled steel shed is a field of vertical ribs at a
fixed pitch, a shallow roof with a deep eaves overhang, a continuous gutter,
roller shutter doors at vehicle scale, a band of clerestory glazing high on
the wall, and roof lights. The rib shadow is the whole character, the same way
the reveal is on a house, so the ribs are real geometry, not a texture.

These are scaled HARD by citymap.js (a 40,000 m2 footprint is not 3x a
7,000 m2 one), so everything is built to tile: the rib pitch, the shutters and
the clerestory are generated from the footprint rather than fixed, and the
model is authored at a mid size with the caller stretching it.

Exports shed_s / shed_m / shed_l / hangar, origins centred, +x along the long
axis, ground at y=0.

Output: public/models/sheds.glb
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Vector, noise

SEED = 41

# (name, width along x, depth, eaves height, roof rise, shutters, clerestory)
VARIANTS = [
    ("shed_s",  22.0, 14.0, 5.2,  1.3, 2, True),
    ("shed_m",  40.0, 22.0, 7.0,  2.1, 3, True),
    ("shed_l",  70.0, 34.0, 9.0,  3.0, 4, True),
    ("hangar", 100.0, 60.0, 14.0, 6.0, 1, False),
]

RIB_PITCH = 1.20        # m — profiled sheet rib spacing
RIB_D = 0.075           # m — how far a rib stands proud. This is the character.
RIB_W = 0.34            # m
EAVES = 0.65            # m — deep overhang; the shadow under it grounds the box
FASCIA_H = 0.30         # m
GUTTER_R = 0.14         # m
PLINTH_H = 0.85         # m — concrete upstand, always present, always stained
PLINTH_OUT = 0.07       # m
CLEAR_H = 1.35          # m — clerestory band height
CLEAR_DROP = 0.55       # m — below the eaves
SHUTTER_W = 4.20        # m
SHUTTER_H = 4.60        # m
PERSON_DOOR_W = 1.05
PERSON_DOOR_H = 2.15
ROOFLIGHT_W = 2.6       # m

BEVEL_W = 0.012
BEVEL_SEGMENTS = 1
BEVEL_ON = os.environ.get("NO_BEVEL") != "1"

COLLECTION_NAME = "Sheds"
OUTPUT_NAME = "sheds"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "public", "models")

M_WALL, M_RIB, M_CONCRETE, M_TRIM, M_SHUTTER, M_GLASS, M_METAL, M_DOOR, M_ROOF = range(9)

PALETTE = [
    ("s_wall",     "#B8BDBE", 0.72, 0.18),   # painted profiled steel
    ("s_rib",      "#A9AFB1", 0.70, 0.20),
    ("s_concrete", "#AFA99C", 0.93, 0.0),
    ("s_trim",     "#6E7679", 0.65, 0.30),
    ("s_shutter",  "#8C8F8E", 0.62, 0.28),
    ("s_glass",    "#2A3138", 0.14, 0.0),
    ("s_metal",    "#8E9499", 0.55, 0.40),
    ("s_door",     "#3E4A52", 0.75, 0.0),
    ("s_roof",     "#9AA0A2", 0.74, 0.22),
]


def fresh_collection(name):
    ex = bpy.data.collections.get(name)
    if ex:
        for o in list(ex.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.collections.remove(ex)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    col = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(col)
    return col


def hex_rgb(hx):
    hx = hx.lstrip("#")
    out = []
    for i in (0, 2, 4):
        c = int(hx[i:i + 2], 16) / 255.0
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return tuple(out)


def make_material(name, hx, rough, metal):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    b = mat.node_tree.nodes.get("Principled BSDF")
    rgb = hex_rgb(hx)
    if b:
        b.inputs["Base Color"].default_value = (*rgb, 1.0)
        b.inputs["Roughness"].default_value = rough
        b.inputs["Metallic"].default_value = metal
    mat.diffuse_color = (*rgb, 1.0)
    return mat


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


# =============================================================================
# the elevation: ribs, shutters, clerestory
# =============================================================================

def shed_wall(bm, length, eaves_h, shutters, clerestory, rng, is_end=False):
    """One elevation in the local frame: +x along the wall, +y into the shed.

    The wall itself is a thin skin; the RIBS standing 75 mm proud of it at a
    1.2 m pitch are what makes it read as profiled steel rather than a painted
    box. They are the shed equivalent of a window reveal."""
    t = 0.14
    # plinth: concrete upstand the sheeting sits on
    add_box(bm, (0.0, -PLINTH_OUT, 0.0), (length, t + PLINTH_OUT, PLINTH_H), M_CONCRETE)

    cz0 = eaves_h - CLEAR_DROP - CLEAR_H       # clerestory band bottom
    top = cz0 if clerestory else eaves_h

    # openings along this elevation
    holes = []
    if shutters > 0 and not is_end:
        gap = length / (shutters + 1)
        for i in range(shutters):
            cx = gap * (i + 1)
            holes.append((cx - SHUTTER_W / 2, SHUTTER_W, SHUTTER_H, 'shutter'))
        # a person door beside the first shutter
        holes.append((gap - SHUTTER_W / 2 - 2.0, PERSON_DOOR_W, PERSON_DOOR_H, 'door'))
    elif is_end and shutters > 0:
        holes.append((length / 2 - SHUTTER_W / 2, SHUTTER_W, SHUTTER_H, 'shutter'))

    holes.sort(key=lambda h: h[0])

    # sheeting between the openings, from the plinth up to `top`
    edges = [0.0]
    for ox, ow, oh, _ in holes:
        edges += [max(0.0, ox), min(length, ox + ow)]
    edges.append(length)
    for i in range(0, len(edges) - 1, 2):
        a, b = edges[i], edges[i + 1]
        if b - a > 0.01:
            add_box(bm, (a, 0.0, PLINTH_H), (b - a, t, top - PLINTH_H), M_WALL)
    # the band over every opening
    hi = max([h[2] for h in holes], default=PLINTH_H)
    if holes and top > hi:
        add_box(bm, (0.0, 0.0, hi), (length, t, top - hi), M_WALL)

    # --- the ribs -------------------------------------------------------------
    n = max(2, int(length / RIB_PITCH))
    for i in range(n + 1):
        rx = min(length - RIB_W, i * (length / n))
        # skip ribs that would stand in front of an opening
        blocked = any(rx + RIB_W > ox - 0.05 and rx < ox + ow + 0.05
                      and PLINTH_H < oh for ox, ow, oh, _ in holes)
        z0 = PLINTH_H
        z1 = top
        if blocked:
            z0 = hi
            if z1 - z0 < 0.3:
                continue
        add_box(bm, (rx, -RIB_D, z0), (RIB_W, RIB_D, z1 - z0), M_RIB)

    # --- clerestory: a continuous glazed band under the eaves -----------------
    if clerestory:
        add_box(bm, (0.0, 0.0, cz0), (length, t * 0.5, 0.12), M_TRIM)
        add_box(bm, (0.0, 0.0, eaves_h - CLEAR_DROP), (length, t * 0.5, 0.12), M_TRIM)
        add_quad(bm, [(0.0, 0.05, cz0 + 0.12), (length, 0.05, cz0 + 0.12),
                      (length, 0.05, eaves_h - CLEAR_DROP), (0.0, 0.05, eaves_h - CLEAR_DROP)],
                 M_GLASS)
        # mullions every 2.4 m
        for i in range(1, max(2, int(length / 2.4))):
            mx = i * (length / max(2, int(length / 2.4)))
            add_box(bm, (mx - 0.04, -0.02, cz0), (0.08, 0.07, CLEAR_H), M_TRIM)
        add_box(bm, (0.0, 0.0, eaves_h - CLEAR_DROP + 0.12), (length, t, CLEAR_DROP - 0.12), M_WALL)

    # --- the openings themselves ---------------------------------------------
    for ox, ow, oh, kind in holes:
        if kind == 'shutter':
            # reveal, then the slatted curtain set back
            add_box(bm, (ox - 0.12, -0.05, PLINTH_H), (0.12, 0.34, oh - PLINTH_H), M_TRIM)
            add_box(bm, (ox + ow, -0.05, PLINTH_H), (0.12, 0.34, oh - PLINTH_H), M_TRIM)
            add_box(bm, (ox - 0.12, -0.05, oh), (ow + 0.24, 0.34, 0.26), M_TRIM)
            slat = 0.26
            ns = max(2, int((oh - PLINTH_H) / slat))
            for i in range(ns):
                add_box(bm, (ox, 0.16, PLINTH_H + i * ((oh - PLINTH_H) / ns)),
                        (ow, 0.05, (oh - PLINTH_H) / ns * 0.82), M_SHUTTER)
            # concrete apron in front of the door
            add_box(bm, (ox - 0.4, -2.6, 0.0), (ow + 0.8, 2.6, 0.06), M_CONCRETE)
        else:
            add_box(bm, (ox, 0.12, PLINTH_H), (ow, 0.06, oh - PLINTH_H), M_DOOR)
            add_box(bm, (ox - 0.07, 0.0, PLINTH_H), (0.07, 0.2, oh - PLINTH_H), M_TRIM)
            add_box(bm, (ox + ow, 0.0, PLINTH_H), (0.07, 0.2, oh - PLINTH_H), M_TRIM)
            add_box(bm, (ox - 0.07, 0.0, oh), (ow + 0.14, 0.2, 0.09), M_TRIM)


def shed_roof(bm, W, D, eaves_h, rise, rng):
    """Shallow duo-pitch with a deep overhang, fascia, gutter and roof lights.
    The overhang and the gutter line are what stop it reading as a lid."""
    e = EAVES
    x0, x1 = -e, W + e
    y0, y1 = -e, D + e
    apex = eaves_h + rise
    ymid = D / 2
    T = 0.10

    for ya in (y0, y1):
        v = [bm.verts.new((x0, ya, eaves_h)), bm.verts.new((x1, ya, eaves_h)),
             bm.verts.new((x1, ymid, apex)), bm.verts.new((x0, ymid, apex))]
        bm.faces.new(v).material_index = M_ROOF
        vb = [bm.verts.new((x0, ya, eaves_h - T)), bm.verts.new((x1, ya, eaves_h - T)),
              bm.verts.new((x1, ymid, apex - T)), bm.verts.new((x0, ymid, apex - T))]
        bm.faces.new(list(reversed(vb))).material_index = M_ROOF
        bm.faces.new((v[0], v[1], vb[1], vb[0])).material_index = M_ROOF
        # fascia + gutter along the eaves
        add_box(bm, (x0, ya - (0.05 if ya == y0 else 0.0), eaves_h - T - FASCIA_H),
                (x1 - x0, 0.05, FASCIA_H + T), M_TRIM)
        add_box(bm, (x0, ya - (GUTTER_R if ya == y0 else -0.02), eaves_h - T - FASCIA_H - GUTTER_R),
                (x1 - x0, GUTTER_R, GUTTER_R), M_METAL)

    # gable infill at both ends
    for xe in (0.0, W):
        add_quad(bm, [(xe, 0.0, eaves_h), (xe, D, eaves_h), (xe, ymid, apex)], M_WALL)
        add_quad(bm, [(xe + 0.002, 0.0, eaves_h), (xe + 0.002, ymid, apex), (xe + 0.002, D, eaves_h)], M_WALL)

    # ridge cap
    add_box(bm, (x0, ymid - 0.14, apex - 0.03), (x1 - x0, 0.28, 0.09), M_METAL)

    # roof lights: translucent panels in two rows, the signature of a big shed
    rows = 2
    n = max(2, int(W / 9.0))
    for r in range(rows):
        yy = D * (0.26 + 0.44 * r)
        zz = eaves_h + rise * (1 - abs(yy - ymid) / ymid) - 0.02
        for i in range(n):
            xx = (i + 0.5) * (W / n) - ROOFLIGHT_W / 2
            add_box(bm, (xx, yy - 1.1, zz), (ROOFLIGHT_W, 2.2, 0.07), M_GLASS)

    # roof-mounted plant: a couple of boxed units, which every shed has
    for i in range(max(1, int(W / 34))):
        px = W * (0.25 + 0.4 * i)
        add_box(bm, (px, ymid - 1.6, apex - 0.1), (3.2, 3.2, 1.1), M_METAL)


def build_shed(name, W, D, eaves_h, rise, shutters, clerestory, seed):
    rng = random.Random(seed)
    bm = bmesh.new()

    def sub(fn, *a, rot=0.0, at=(0.0, 0.0)):
        tmp = bmesh.new()
        fn(tmp, *a)
        c, s = math.cos(rot), math.sin(rot)
        for v in tmp.verts:
            x, y = v.co.x, v.co.y
            v.co.x = x * c - y * s + at[0]
            v.co.y = x * s + y * c + at[1]
        me = bpy.data.meshes.new("_t")
        tmp.to_mesh(me)
        tmp.free()
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    # slab
    add_box(bm, (-PLINTH_OUT, -PLINTH_OUT, -0.25),
            (W + 2 * PLINTH_OUT, D + 2 * PLINTH_OUT, 0.25), M_CONCRETE)

    sub(shed_wall, W, eaves_h, shutters, clerestory, rng, False)
    sub(shed_wall, W, eaves_h, max(0, shutters - 1), clerestory, random.Random(seed + 1), False,
        rot=math.pi, at=(W, D))
    sub(shed_wall, D, eaves_h, 1 if shutters else 0, clerestory, random.Random(seed + 2), True,
        rot=-math.pi / 2, at=(0.0, D))
    sub(shed_wall, D, eaves_h, 0, clerestory, random.Random(seed + 3), True,
        rot=math.pi / 2, at=(W, 0.0))

    shed_roof(bm, W, D, eaves_h, rise, rng)

    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(col):
    objs = []
    mats = [make_material(n, hx, r, m) for n, hx, r, m in PALETTE]
    for i, (name, W, D, eh, rise, sh, cl) in enumerate(VARIANTS):
        bm = build_shed(name, W, D, eh, rise, sh, cl, SEED + i * 53)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        o = bpy.data.objects.new(name, me)
        col.objects.link(o)
        for m in mats:          # never materials.clear() — it zeroes the indices
            o.data.materials.append(m)
        me.polygons.foreach_set("use_smooth", [False] * len(me.polygons))
        me.update()
        if BEVEL_ON:
            mod = o.modifiers.new("Bevel", "BEVEL")
            mod.width = BEVEL_W
            mod.segments = BEVEL_SEGMENTS
            mod.limit_method = "ANGLE"
            mod.angle_limit = math.radians(50)
            mod.harden_normals = True
        o.location = (0, i * 80, 0)
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
        print(f"    {o.name:10s} {triangle_count([o]):>7,} tris")
    print(f"Triangles : {triangle_count(objs):,}")
    print(f"Exported  : {path}")
    print("-" * 64)


if __name__ == "__main__":
    main()
