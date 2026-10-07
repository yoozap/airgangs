"""
Blender-headless: REAL Tbilisi peripheral apartment blocks — the 4-9 storey
brick-and-plaster blocks that the airport audit found still wearing photos
(150 "small but tall", 95 "mid block"). No photographic textures.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_apartment.py

WHAT MAKES ONE READ AS REAL RATHER THAN AS A MODEL
--------------------------------------------------
Not polygon count — ASYMMETRY and CLUTTER. A block where every bay is the same
reads as generated however well the window is modelled. The rules this script
follows, all of them from how these buildings actually came to look that way:

* The balcony SLAB stacks in a column, because it is one structural line of
  cantilevers. But the ENCLOSURE is each flat's own decision, so the glazing,
  the infill and the paint change floor by floor within that column. That
  mismatch -- ordered structure, disordered skin -- is the whole look.
* Windows were replaced one flat at a time: brown wood originals next to white
  PVC, at random, forever.
* An AC unit per 2-3 flats, never in a grid. Satellite dishes beside balconies.
  Clotheslines strung between them. Drainpipes down every corner.
* The ground floor is a different building: shopfronts, roller shutters,
  signage boxes, a canopy, a step up from the pavement.
* The roof carries water tanks, vent stacks and antennas, and from the air that
  clutter is most of what you see.

Same construction as the rest of the kit: the wall is built as panels AROUND
the openings, so every hole is real and nothing is booleaned.

Exports apt_a .. apt_e, origins centred, +x along the street, ground at y=0.
Output: public/models/apartments.glb
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Vector, noise

SEED = 61

# (name, bays, storeys, depth, shopfront ground floor, roof)
VARIANTS = [
    ("apt_a", 5, 4, 12.0, False, "flat"),
    ("apt_b", 6, 5, 12.8, True,  "flat"),
    ("apt_c", 8, 5, 13.5, True,  "flat"),
    ("apt_d", 6, 9, 13.0, False, "flat"),
    ("apt_e", 4, 6, 11.5, True,  "pitch"),
]

BAY_W = 3.30            # m — one flat's worth of frontage
FLOOR_H = 3.00          # m
GROUND_H = 3.60         # m — the ground floor is always taller
PLINTH_H = 0.45
PLINTH_OUT = 0.07
WALL_T = 0.32

WIN_W = 1.40
WIN_H = 1.55
SILL_ABOVE = 0.90
REVEAL = 0.16           # m — the shadow line that does the work
FRAME_FACE = 0.055
FRAME_DEPTH = 0.08
GLASS_SET = 0.03
SILL_T = 0.05
SILL_PROJ = 0.04
PVC_SHARE = 0.48

BALC_DEPTH = 1.05
BALC_SLAB_T = 0.14
BALC_RAIL_H = 1.00
BALC_COL_SHARE = 0.58   # fraction of bays that carry a balcony column

SHOP_H = 2.90
PARAPET_H = 0.62
PARAPET_T = 0.22

AC_W, AC_H, AC_D = 0.78, 0.56, 0.30
DISH_R = 0.42
PIPE_R = 0.055

BEVEL_W = 0.012
BEVEL_SEGMENTS = 1
# OFF by default. Bevelling every edge of this much overlapping box geometry
# cost 130k -> 470k tris (3.6x) for highlights that are sub-pixel past ~30 m,
# and these are instanced hundreds of times across a district. The clutter --
# balconies, AC units, pipes, roof tanks -- already gives the silhouette all
# the edge detail it needs. BEVEL=1 to turn it back on for a hero render.
BEVEL_ON = os.environ.get("BEVEL") == "1"

COLLECTION_NAME = "Apartments"
OUTPUT_NAME = "apartments"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "public", "models")

(M_PLASTER, M_BRICK, M_CONCRETE, M_WOOD, M_PVC, M_GLASS, M_METAL, M_DOOR,
 M_ROOF) = range(9)

PALETTE = [
    ("a_plaster",  "#CFC3AC", 0.92, 0.0),
    ("a_brick",    "#9E6B4E", 0.94, 0.0),
    ("a_concrete", "#B2ABA0", 0.90, 0.0),
    ("a_wood",     "#6B4A2F", 0.82, 0.0),
    ("a_pvc",      "#F0F0EC", 0.55, 0.0),
    ("a_glass",    "#222932", 0.12, 0.0),
    ("a_metal",    "#C9C6BC", 0.60, 0.30),
    ("a_door",     "#3C3A33", 0.78, 0.0),
    ("a_roof",     "#6E6A63", 0.90, 0.05),
]


# ---------------------------------------------------------------- plumbing --

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


def add_cyl(bm, x, y, z0, z1, r, mat, sides=6, axis="z"):
    ring = [(r * math.cos(a), r * math.sin(a))
            for a in (i * 2 * math.pi / sides for i in range(sides))]
    if axis == "z":
        lo = [bm.verts.new((x + a, y + b, z0)) for a, b in ring]
        hi = [bm.verts.new((x + a, y + b, z1)) for a, b in ring]
    else:  # horizontal, along y
        lo = [bm.verts.new((x + a, z0, y + b)) for a, b in ring]
        hi = [bm.verts.new((x + a, z1, y + b)) for a, b in ring]
    for i in range(sides):
        j = (i + 1) % sides
        bm.faces.new((lo[i], lo[j], hi[j], hi[i])).material_index = mat
    bm.faces.new(lo).material_index = mat
    bm.faces.new(list(reversed(hi))).material_index = mat


# ------------------------------------------------------------------ window --

def window(bm, x0, z0, w, h, pvc):
    x1, z1 = x0 + w, z0 + h
    fm = M_PVC if pvc else M_WOOD
    jamb = REVEAL + FRAME_DEPTH
    t = 0.022
    add_box(bm, (x0 - t, 0.0, z0), (t, jamb, h), M_PLASTER)
    add_box(bm, (x1, 0.0, z0), (t, jamb, h), M_PLASTER)
    add_box(bm, (x0 - t, 0.0, z1), (w + 2 * t, jamb, t), M_PLASTER)
    add_box(bm, (x0 - 0.07, -SILL_PROJ, z0 - SILL_T),
            (w + 0.14, jamb + SILL_PROJ + 0.05, SILL_T), M_CONCRETE)
    y = REVEAL
    add_box(bm, (x0, y, z0), (w, FRAME_DEPTH, FRAME_FACE), fm)
    add_box(bm, (x0, y, z1 - FRAME_FACE), (w, FRAME_DEPTH, FRAME_FACE), fm)
    add_box(bm, (x0, y, z0), (FRAME_FACE, FRAME_DEPTH, h), fm)
    add_box(bm, (x1 - FRAME_FACE, y, z0), (FRAME_FACE, FRAME_DEPTH, h), fm)
    gx0, gx1 = x0 + FRAME_FACE, x1 - FRAME_FACE
    gz0, gz1 = z0 + FRAME_FACE, z1 - FRAME_FACE
    add_box(bm, (gx0 + (gx1 - gx0) * 0.5 - 0.025, y, gz0), (0.05, FRAME_DEPTH, gz1 - gz0), fm)
    if not pvc:
        add_box(bm, (gx0, y, gz1 - (gz1 - gz0) * 0.3), (gx1 - gx0, FRAME_DEPTH, 0.042), fm)
    add_quad(bm, [(gx0, y + GLASS_SET, gz0), (gx1, y + GLASS_SET, gz0),
                  (gx1, y + GLASS_SET, gz1), (gx0, y + GLASS_SET, gz1)], M_GLASS)


def balcony_door(bm, x0, z0, w, h, pvc):
    window(bm, x0, z0, w, h, pvc)
    add_box(bm, (x0 + FRAME_FACE, REVEAL + 0.01, z0 + FRAME_FACE),
            (w - 2 * FRAME_FACE, 0.05, 0.40), M_PVC if pvc else M_WOOD)


# ----------------------------------------------------------------- balcony --
# The slab is structural and stacks; the ENCLOSURE is the flat's own decision
# and changes floor by floor. That contrast is what sells the elevation.

def balcony(bm, x0, z, w, style, rng):
    d = BALC_DEPTH
    add_box(bm, (x0, -d, z - BALC_SLAB_T), (w, d, BALC_SLAB_T), M_CONCRETE)
    add_box(bm, (x0, -d, z - BALC_SLAB_T - 0.035), (w, 0.055, 0.035), M_CONCRETE)
    top = z + BALC_RAIL_H

    if style == "glazed_pvc" or style == "glazed_wood":
        pvc = style == "glazed_pvc"
        mat = M_PVC if pvc else M_WOOD
        gt = z + 2.45
        for px in (x0, x0 + w - 0.06):
            add_box(bm, (px, -d, z), (0.06, 0.06, gt - z), mat)
            add_box(bm, (px, -0.06, z), (0.06, 0.06, gt - z), mat)
        add_box(bm, (x0, -d, gt - 0.07), (w, d, 0.07), mat)
        add_box(bm, (x0, -d, z + 0.02), (w, d, 0.05), mat)
        panes = 4 if not pvc else 3
        for i in range(1, panes):
            add_box(bm, (x0 + w * i / panes - 0.02, -d, z), (0.04, 0.05, gt - z), mat)
        if not pvc:
            add_box(bm, (x0, -d, z + (gt - z) * 0.56), (w, 0.045, 0.038), mat)
        add_quad(bm, [(x0, -d + 0.03, z + 0.05), (x0 + w, -d + 0.03, z + 0.05),
                      (x0 + w, -d + 0.03, gt - 0.07), (x0, -d + 0.03, gt - 0.07)], M_GLASS)
        for sx in (x0, x0 + w):
            add_quad(bm, [(sx, -d + 0.03, z + 0.05), (sx, -0.04, z + 0.05),
                          (sx, -0.04, gt - 0.07), (sx, -d + 0.03, gt - 0.07)], M_GLASS)
        return

    if style == "brick":
        # the owner bricked the parapet up solid, very common
        add_box(bm, (x0, -d, z), (w, 0.14, BALC_RAIL_H + 0.12), M_BRICK)
        for sx in (x0, x0 + w - 0.14):
            add_box(bm, (sx, -d, z), (0.14, d, BALC_RAIL_H + 0.12), M_BRICK)
        add_box(bm, (x0 - 0.03, -d - 0.03, z + BALC_RAIL_H + 0.12),
                (w + 0.06, 0.2, 0.05), M_CONCRETE)
        return

    if style == "sheet":
        add_box(bm, (x0, -d, z + 0.05), (w, 0.02, BALC_RAIL_H - 0.1), M_METAL)
        add_box(bm, (x0, -d, top - 0.045), (w, 0.04, 0.045), M_METAL)
        for sx in (x0, x0 + w - 0.03):
            add_box(bm, (sx, -d, z), (0.03, d, BALC_RAIL_H), M_METAL)
        return

    # open welded bars
    add_box(bm, (x0, -d, top - 0.045), (w, 0.04, 0.045), M_METAL)
    add_box(bm, (x0, -d, z + 0.07), (w, 0.035, 0.035), M_METAL)
    n = max(3, int(w / 0.12))
    for i in range(n + 1):
        bx = x0 + i * (w / n)
        add_cyl(bm, bx, -d + 0.02, z + 0.07, top - 0.045, 0.008, M_METAL, 4)
    for sx in (x0 + 0.02, x0 + w - 0.02):
        add_box(bm, (sx - 0.015, -d, z + 0.07), (0.03, d - 0.02, 0.035), M_METAL)


# ------------------------------------------------------------------ clutter --

def ac_unit(bm, x, z):
    add_box(bm, (x, -AC_D, z), (AC_W, AC_D, AC_H), M_METAL)
    for bx in (x + 0.06, x + AC_W - 0.1):
        add_box(bm, (bx, -0.06, z - 0.07), (0.04, 0.06, 0.07), M_METAL)
    # the drip streak below is a material job, but the condensate pipe is real
    add_cyl(bm, x + AC_W * 0.5, -0.05, z - 0.9, z, 0.012, M_METAL, 4)


def dish(bm, x, z):
    add_box(bm, (x - 0.03, -0.26, z - 0.03), (0.06, 0.26, 0.06), M_METAL)
    add_cyl(bm, x, -0.30, z - DISH_R * 0.55, z + DISH_R * 0.55, DISH_R, M_METAL, 10, axis="z")


def drainpipe(bm, x, z0, z1):
    add_cyl(bm, x, -PIPE_R - 0.03, z0, z1, PIPE_R, M_METAL, 6)
    n = max(2, int((z1 - z0) / 1.6))
    for i in range(n):
        zz = z0 + (i + 0.5) * ((z1 - z0) / n)
        add_box(bm, (x - 0.09, -0.04, zz), (0.18, 0.05, 0.035), M_METAL)


def shopfront(bm, x0, w, z0):
    """The ground floor is a different building: glazing, fascia, shutter box,
    a step. Without it a block looks like it was dropped on the pavement."""
    h = SHOP_H
    add_box(bm, (x0 + 0.1, 0.0, z0), (w - 0.2, 0.22, 0.42), M_CONCRETE)   # stallriser
    # frame
    for sx in (x0 + 0.1, x0 + w - 0.22):
        add_box(bm, (sx, 0.08, z0 + 0.42), (0.12, 0.1, h - 0.42), M_METAL)
    add_box(bm, (x0 + 0.1, 0.08, z0 + h - 0.12), (w - 0.2, 0.1, 0.12), M_METAL)
    add_quad(bm, [(x0 + 0.2, 0.14, z0 + 0.42), (x0 + w - 0.2, 0.14, z0 + 0.42),
                  (x0 + w - 0.2, 0.14, z0 + h - 0.12), (x0 + 0.2, 0.14, z0 + h - 0.12)], M_GLASS)
    # fascia sign box over the opening + shutter housing
    add_box(bm, (x0 + 0.02, -0.14, z0 + h), (w - 0.04, 0.2, 0.62), M_METAL)
    add_box(bm, (x0 + 0.06, -0.18, z0 + h + 0.62), (w - 0.12, 0.24, 0.22), M_CONCRETE)
    add_box(bm, (x0 - 0.1, -0.5, 0.0), (w + 0.2, 0.5, 0.14), M_CONCRETE)   # step


def entrance(bm, cx, z0):
    w, h = 1.5, 2.3
    x0 = cx - w / 2
    rec = 0.3
    add_box(bm, (x0 - 0.04, 0.0, z0), (0.04, rec, h), M_PLASTER)
    add_box(bm, (x0 + w, 0.0, z0), (0.04, rec, h), M_PLASTER)
    add_box(bm, (x0 - 0.04, 0.0, z0 + h), (w + 0.08, rec, 0.04), M_PLASTER)
    add_box(bm, (x0, rec, z0), (w, 0.06, h), M_DOOR)
    for leaf in (0, 1):
        lx = x0 + 0.07 + leaf * (w / 2)
        for r in range(3):
            add_box(bm, (lx, rec - 0.018, z0 + 0.22 + r * 0.64),
                    (w / 2 - 0.13, 0.02, 0.46), M_DOOR)
    add_box(bm, (cx - 1.35, -1.25, z0 + h + 0.5), (2.7, 1.3, 0.15), M_CONCRETE)
    add_box(bm, (cx - 1.35, -1.25, z0 + h + 0.42), (2.7, 0.09, 0.08), M_CONCRETE)
    for i, s in enumerate((0.3, 0.15)):
        add_box(bm, (cx - 1.1, -0.42 - i * 0.3, 0.0), (2.2, 0.32, s), M_CONCRETE)
    add_box(bm, (x0 + w + 0.2, -0.015, z0 + 2.0), (0.36, 0.02, 0.26), M_METAL)


# ------------------------------------------------------------------- walls --

def wall_cell(bm, x0, z0, w, h, openings, mat):
    if not openings:
        add_box(bm, (x0, 0.0, z0), (w, WALL_T, h), mat)
        return
    openings = sorted(openings, key=lambda o: o[0])
    lo = min(o[1] for o in openings) - SILL_T
    hi = max(o[1] + o[3] for o in openings)
    if lo > z0:
        add_box(bm, (x0, 0.0, z0), (w, WALL_T, lo - z0), mat)
    if z0 + h > hi:
        add_box(bm, (x0, 0.0, hi), (w, WALL_T, (z0 + h) - hi), mat)
    edges = [x0]
    for ox, _, ow, _ in openings:
        edges += [ox, ox + ow]
    edges.append(x0 + w)
    for i in range(0, len(edges) - 1, 2):
        a, b = edges[i], edges[i + 1]
        if b - a > 0.004:
            add_box(bm, (a, 0.0, lo), (b - a, WALL_T, hi - lo), mat)
    for ox, oz, ow, oh in openings:
        if oz + oh < hi:
            add_box(bm, (ox, 0.0, oz + oh), (ow, WALL_T, hi - (oz + oh)), mat)
        if oz - SILL_T > lo:
            add_box(bm, (ox, 0.0, lo), (ow, WALL_T, (oz - SILL_T) - lo), mat)


BALC_STYLES = ["bars", "bars", "glazed_pvc", "glazed_wood", "sheet", "brick"]


def facade(bm, bays, storeys, shop, rng, brick):
    W = bays * BAY_W
    base_mat = M_BRICK if brick else M_PLASTER
    ent_bay = bays // 2

    # columnar decision: which bays carry a balcony slab at all
    cols = [b != ent_bay and rng.random() < BALC_COL_SHARE for b in range(bays)]

    z = PLINTH_H
    for f in range(storeys):
        fh = GROUND_H if f == 0 else FLOOR_H
        for b in range(bays):
            bx = b * BAY_W
            if f == 0:
                if b == ent_bay:
                    wall_cell(bm, bx, z, BAY_W, fh, [], base_mat)
                    entrance(bm, bx + BAY_W / 2, z)
                elif shop:
                    wall_cell(bm, bx, z, BAY_W, fh,
                              [(bx + 0.1, z, BAY_W - 0.2, SHOP_H)], base_mat)
                    shopfront(bm, bx, BAY_W, z)
                else:
                    ox = bx + BAY_W / 2 - WIN_W / 2
                    wall_cell(bm, bx, z, BAY_W, fh, [(ox, z + SILL_ABOVE, WIN_W, WIN_H)], base_mat)
                    window(bm, ox, z + SILL_ABOVE, WIN_W, WIN_H, rng.random() < PVC_SHARE)
                continue

            pvc = rng.random() < PVC_SHARE
            if cols[b]:
                # window + balcony door, and THIS flat's own enclosure choice
                dw, dh = 0.88, 2.25
                gap = 0.14
                total = WIN_W + gap + dw
                ox = bx + BAY_W / 2 - total / 2
                dx = ox + WIN_W + gap
                wall_cell(bm, bx, z, BAY_W, fh,
                          [(ox, z + SILL_ABOVE, WIN_W, WIN_H), (dx, z + 0.06, dw, dh)], base_mat)
                window(bm, ox, z + SILL_ABOVE, WIN_W, WIN_H, pvc)
                balcony_door(bm, dx, z + 0.06, dw, dh, pvc)
                balcony(bm, bx + 0.22, z, BAY_W - 0.44,
                        BALC_STYLES[rng.randrange(len(BALC_STYLES))], rng)
            else:
                ox = bx + BAY_W / 2 - WIN_W / 2
                wall_cell(bm, bx, z, BAY_W, fh, [(ox, z + SILL_ABOVE, WIN_W, WIN_H)], base_mat)
                window(bm, ox, z + SILL_ABOVE, WIN_W, WIN_H, pvc)
                # an AC on roughly every third flat, never on a grid
                if rng.random() < 0.34:
                    ac_unit(bm, bx + BAY_W / 2 + (0.55 if rng.random() < 0.5 else -1.3),
                            z + SILL_ABOVE + WIN_H + 0.12)
            if cols[b] and rng.random() < 0.22:
                dish(bm, bx + 0.12, z + 1.9)
        z += fh

    # drainpipes down the bay joints, a few of them
    top = PLINTH_H + GROUND_H + (storeys - 1) * FLOOR_H
    for b in range(1, bays):
        if rng.random() < 0.45:
            drainpipe(bm, b * BAY_W, 0.1, top)


def blank_wall(bm, width, storeys, rng, brick, some_windows=True):
    mat = M_BRICK if brick else M_PLASTER
    bays = max(1, int(round(width / 3.4)))
    bw = width / bays
    z = PLINTH_H
    for f in range(storeys):
        fh = GROUND_H if f == 0 else FLOOR_H
        for b in range(bays):
            bx = b * bw
            if not some_windows or rng.random() < 0.45:
                wall_cell(bm, bx, z, bw, fh, [], mat)
                continue
            ow = WIN_W * 0.8
            ox = bx + bw / 2 - ow / 2
            wall_cell(bm, bx, z, bw, fh, [(ox, z + SILL_ABOVE, ow, WIN_H)], mat)
            window(bm, ox, z + SILL_ABOVE, ow, WIN_H, rng.random() < PVC_SHARE)
        z += fh


# -------------------------------------------------------------------- roof --

def roof(bm, W, D, z, kind, rng):
    if kind == "pitch":
        rise = math.tan(math.radians(18)) * (D / 2 + 0.45)
        e = 0.45
        for ya in (-e, D + e):
            v = [bm.verts.new((-e, ya, z)), bm.verts.new((W + e, ya, z)),
                 bm.verts.new((W + e, D / 2, z + rise)), bm.verts.new((-e, D / 2, z + rise))]
            bm.faces.new(v).material_index = M_ROOF
            vb = [bm.verts.new((-e, ya, z - 0.1)), bm.verts.new((W + e, ya, z - 0.1)),
                  bm.verts.new((W + e, D / 2, z + rise - 0.1)), bm.verts.new((-e, D / 2, z + rise - 0.1))]
            bm.faces.new(list(reversed(vb))).material_index = M_ROOF
            bm.faces.new((v[0], v[1], vb[1], vb[0])).material_index = M_ROOF
            add_box(bm, (-e, ya - (0.04 if ya < 0 else 0), z - 0.1 - 0.18), (W + 2 * e, 0.04, 0.28), M_METAL)
        for xe in (0.0, W):
            add_quad(bm, [(xe, 0.0, z), (xe, D, z), (xe, D / 2, z + rise)], M_PLASTER)
            add_quad(bm, [(xe + 0.002, 0.0, z), (xe + 0.002, D / 2, z + rise), (xe + 0.002, D, z)], M_PLASTER)
        add_box(bm, (-e, D / 2 - 0.1, z + rise - 0.02), (W + 2 * e, 0.2, 0.07), M_METAL)
        top = z + rise
    else:
        add_box(bm, (0, 0, z), (W, D, 0.16), M_ROOF)
        t = PARAPET_T
        for (px, py), (sx, sy) in [((0, 0), (W, t)), ((0, D - t), (W, t)),
                                   ((0, 0), (t, D)), ((W - t, 0), (t, D))]:
            add_box(bm, (px, py, z + 0.16), (sx, sy, PARAPET_H), M_PLASTER)
            add_box(bm, (px - 0.05, py - 0.05, z + 0.16 + PARAPET_H),
                    (sx + 0.1, sy + 0.1, 0.07), M_CONCRETE)
        top = z + 0.16

    # the clutter that is most of what you see from the air
    for i in range(max(2, int(W / 7))):
        px = 0.9 + i * (W / max(2, int(W / 7)))
        add_cyl(bm, px, D * 0.3, top, top + 0.95, 0.23, M_CONCRETE, 8)
        add_box(bm, (px - 0.3, D * 0.3 - 0.3, top + 0.95), (0.6, 0.6, 0.07), M_CONCRETE)
    # water tanks
    for i in range(max(1, int(W / 11))):
        px = W * (0.22 + 0.46 * i)
        add_cyl(bm, px, D * 0.68, top, top + 1.25, 0.62, M_METAL, 10)
        add_box(bm, (px - 0.72, D * 0.68 - 0.72, top), (1.44, 1.44, 0.14), M_METAL)
    # stair head and an antenna mast
    add_box(bm, (W * 0.42, D * 0.42, top), (2.6, 2.8, 2.25), M_PLASTER)
    add_box(bm, (W * 0.42 - 0.06, D * 0.42 - 0.06, top + 2.25), (2.72, 2.92, 0.1), M_CONCRETE)
    add_cyl(bm, W * 0.2, D * 0.5, top, top + 3.4, 0.045, M_METAL, 4)


# ------------------------------------------------------------------- build --

def build_apt(name, bays, storeys, D, shop, rk, seed):
    rng = random.Random(seed)
    W = bays * BAY_W
    brick = rng.random() < 0.38
    H = PLINTH_H + GROUND_H + (storeys - 1) * FLOOR_H
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

    add_box(bm, (-PLINTH_OUT, -PLINTH_OUT, 0), (W + 2 * PLINTH_OUT, D + 2 * PLINTH_OUT, PLINTH_H), M_CONCRETE)
    z = PLINTH_H
    for f in range(storeys + 1):
        add_box(bm, (WALL_T, WALL_T, z - 0.1), (W - 2 * WALL_T, D - 2 * WALL_T, 0.1), M_CONCRETE)
        z += GROUND_H if f == 0 else FLOOR_H

    sub(facade, bays, storeys, shop, rng, brick)
    sub(facade, bays, storeys, False, random.Random(seed + 1), brick,
        rot=math.pi, at=(W, D))
    sub(blank_wall, D, storeys, random.Random(seed + 2), brick, True, rot=-math.pi / 2, at=(0.0, D))
    sub(blank_wall, D, storeys, random.Random(seed + 3), brick, True, rot=math.pi / 2, at=(W, 0.0))
    roof(bm, W, D, H, rk, rng)

    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(col):
    objs = []
    mats = [make_material(n, hx, r, m) for n, hx, r, m in PALETTE]
    for i, (name, bays, st, D, shop, rk) in enumerate(VARIANTS):
        bm = build_apt(name, bays, st, D, shop, rk, SEED + i * 71)
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
            md.segments = BEVEL_SEGMENTS
            md.limit_method = "ANGLE"
            md.angle_limit = math.radians(50)
            md.harden_normals = True
        o.location = (0, i * 30, 0)
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
        print(f"    {o.name:8s} {triangle_count([o]):>7,} tris")
    print(f"Triangles : {triangle_count(objs):,}")
    print(f"Exported  : {path}")
    print("-" * 64)


if __name__ == "__main__":
    main()
