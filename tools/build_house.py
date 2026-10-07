"""
Blender-headless: REAL Georgian suburban houses — the building type that fills
the airport district, Orkhevi, Ponichala and every village edge of the map.
No photographic textures: cut openings, real roofs, real verandas.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_house.py

WHY
---
Outside the named districts (`DISTRICTS` in citymap.js lists only historic /
classic / soviet) `districtAt()` returns null, so every house around the
airport falls through to HOUSE_WALLS — a tiled Wikimedia photo on an extruded
prism — with facade-kit boxes stuck on for windows and doors. That is what
reads as fake: a picture of a wall, and a window that is a frame in front of
a dark square rather than a hole with a reveal.

Same construction logic as tools/build_panel_block.py: the wall is built as
panels AROUND each opening, so the hole is real and nothing is booleaned.

WHAT A GEORGIAN SUBURBAN HOUSE IS
---------------------------------
1-2 storeys of plastered block or brick on a raised plinth; a pitched metal or
tile roof with a GENEROUS overhang (0.4-0.6 m — the eaves shadow is half the
character); very often an open veranda along the upper floor on turned or
square posts, which is the single most recognisable feature; an external stair
to it; a chimney. Ground floor frequently unfinished block while the upper is
painted — that mismatch is normal, not a defect.

Exports house_a .. house_e in one glb, origins centred on the footprint at
ground level, +x along the longer side, so citymap.js can drop them on OSM
footprints the way it does the panel blocks.

Output: public/models/houses.glb
"""

import bpy
import bmesh
import math
import os
import random
from mathutils import Vector, noise

# =============================================================================
# CONFIG
# =============================================================================

SEED = 23

# --- the variants: (name, width, depth, storeys, roof, veranda) --------------
# width runs along +x. "gable" = ridge along x, "hip" = hipped on all four.
# The first five were all square-ish (aspect 1.1-1.3), which is why the fitter
# could only claim 278 of 1062 cottages around the airport: real OSM house
# footprints are often small, or long and narrow. f/g/h cover that ground.
VARIANTS = [
    ("house_a", 8.6, 7.8, 1, "gable", False),
    ("house_b", 10.4, 8.6, 2, "hip", True),
    ("house_c", 12.2, 9.4, 2, "gable", True),
    ("house_d", 9.0, 9.0, 1, "hip", False),
    ("house_e", 13.5, 10.2, 2, "gable", False),
    ("house_f", 6.4, 5.6, 1, "gable", False),    # the small cottage
    ("house_g", 13.2, 6.3, 1, "gable", False),   # long and narrow, aspect 2.1
    ("house_h", 16.0, 7.6, 2, "gable", True),    # long two-storey with veranda
    # Measured from the rejects: the commonest unclaimed footprints around the
    # airport are 10x4, 12x4, 20x6, 18x6, 26x8 — aspect 2.5-3.5 and only 4-6 m
    # deep. Those are not detached houses at all, they are row buildings,
    # garage rows and long single-storey outbuildings, and nothing in the kit
    # was that shape.
    ("house_i", 12.0, 4.4, 1, "gable", False),   # narrow outbuilding / garage row
    ("house_j", 20.0, 5.6, 1, "gable", False),   # long single-storey row
    ("house_k", 26.0, 8.0, 2, "gable", False),   # long two-storey terrace
]

FLOOR_H = 2.90          # m — more generous than a khrushchevka
PLINTH_H = 0.55         # m — these sit up off the ground on a base course
PLINTH_OUT = 0.08       # m
WALL_T = 0.30           # m — block + plaster reads heavier than panel

# --- openings -----------------------------------------------------------------
WIN_W = 1.25            # m
WIN_H = 1.45            # m
SILL_ABOVE_FLOOR = 0.95  # m
REVEAL = 0.15           # m — deeper than the panel block: thicker wall
FRAME_FACE = 0.055      # m
FRAME_DEPTH = 0.08      # m
GLASS_SET = 0.03        # m
SILL_T = 0.05           # m
SILL_PROJ = 0.045       # m
PVC_SHARE = 0.55        # villages replaced windows earlier than the city did

DOOR_W = 1.05           # m
DOOR_H = 2.15           # m

# --- roof ---------------------------------------------------------------------
ROOF_PITCH = 23.0       # degrees
EAVES = 0.50            # m — the overhang. NON-NEGOTIABLE on a pitched roof.
FASCIA_H = 0.20         # m — the board at the eaves edge
FASCIA_T = 0.035        # m
ROOF_T = 0.11           # m — the roof slab has thickness; a plane reads as paper
RIDGE_T = 0.09          # m
CHIMNEY_W = 0.62        # m

# --- veranda ------------------------------------------------------------------
VER_DEPTH = 1.85        # m
VER_POST = 0.135        # m — square posts
VER_RAIL_H = 0.95       # m

BEVEL_W = 0.013
BEVEL_SEGMENTS = 1
BEVEL_ON = os.environ.get("NO_BEVEL") != "1"

COLLECTION_NAME = "Houses"
OUTPUT_NAME = "houses"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "public", "models")

M_PLASTER, M_BLOCK, M_CONCRETE, M_WOOD, M_PVC, M_GLASS, M_METAL, M_DOOR, M_ROOF = range(9)

PALETTE = [
    ("h_plaster",  "#D8CDB4", 0.92, 0.0),   # painted render
    ("h_block",    "#B9B2A4", 0.95, 0.0),   # unfinished block ground floor
    ("h_concrete", "#B5AEA0", 0.90, 0.0),
    ("h_wood",     "#7A5634", 0.82, 0.0),   # veranda posts, old frames
    ("h_pvc",      "#F2F2EE", 0.55, 0.0),
    ("h_glass",    "#222730", 0.12, 0.0),
    ("h_metal",    "#8E9499", 0.55, 0.35),
    ("h_door",     "#4A3A2C", 0.75, 0.0),
    ("h_roof",     "#8C4332", 0.80, 0.08),  # red metal sheet / tile
]


# =============================================================================
# plumbing
# =============================================================================

def fresh_collection(name):
    existing = bpy.data.collections.get(name)
    if existing:
        for obj in list(existing.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(existing)
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
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    rgb = hex_rgb(hx)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = metal
    mat.diffuse_color = (*rgb, 1.0)
    return mat


def activate(obj):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def call_op(op, **kw):
    try:
        return op(**kw)
    except TypeError as e:
        print(f"  ! {op.idname()} rejected an argument ({e}); retrying bare")
        return op(filepath=kw["filepath"])


# =============================================================================
# geometry helpers
# =============================================================================

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


def add_prism(bm, pts_bottom, height, mat):
    """Extrude a closed 2D outline (list of (x, y)) up by `height`."""
    lo = [bm.verts.new((x, y, 0.0)) for x, y in pts_bottom]
    hi = [bm.verts.new((x, y, height)) for x, y in pts_bottom]
    n = len(lo)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((lo[i], lo[j], hi[j], hi[i])).material_index = mat
    bm.faces.new(lo).material_index = mat
    bm.faces.new(list(reversed(hi))).material_index = mat


# =============================================================================
# the window — same anatomy as the panel block, deeper reveal
# =============================================================================

def window(bm, x0, z0, w, h, pvc=False, grille=False):
    x1, z1 = x0 + w, z0 + h
    fm = M_PVC if pvc else M_WOOD
    jamb = REVEAL + FRAME_DEPTH
    t = 0.022

    add_box(bm, (x0 - t, 0.0, z0), (t, jamb, h), M_PLASTER)
    add_box(bm, (x1, 0.0, z0), (t, jamb, h), M_PLASTER)
    add_box(bm, (x0 - t, 0.0, z1), (w + 2 * t, jamb, t), M_PLASTER)

    # sill: proud on both sides, below the opening, sloped face outward
    add_box(bm, (x0 - 0.08, -SILL_PROJ, z0 - SILL_T),
            (w + 0.16, jamb + SILL_PROJ + 0.05, SILL_T), M_CONCRETE)
    # a flat lintel band over the head — these houses show them
    add_box(bm, (x0 - 0.08, -0.03, z1), (w + 0.16, 0.03 + REVEAL, 0.12), M_CONCRETE)

    y = REVEAL
    add_box(bm, (x0, y, z0), (w, FRAME_DEPTH, FRAME_FACE), fm)
    add_box(bm, (x0, y, z1 - FRAME_FACE), (w, FRAME_DEPTH, FRAME_FACE), fm)
    add_box(bm, (x0, y, z0), (FRAME_FACE, FRAME_DEPTH, h), fm)
    add_box(bm, (x1 - FRAME_FACE, y, z0), (FRAME_FACE, FRAME_DEPTH, h), fm)

    gx0, gx1 = x0 + FRAME_FACE, x1 - FRAME_FACE
    gz0, gz1 = z0 + FRAME_FACE, z1 - FRAME_FACE
    add_box(bm, (gx0 + (gx1 - gx0) / 2 - 0.025, y, gz0), (0.05, FRAME_DEPTH, gz1 - gz0), fm)
    if not pvc:
        tz = gz1 - (gz1 - gz0) * 0.32
        add_box(bm, (gx0, y, tz), (gx1 - gx0, FRAME_DEPTH, 0.045), fm)

    add_quad(bm, [(gx0, y + GLASS_SET, gz0), (gx1, y + GLASS_SET, gz0),
                  (gx1, y + GLASS_SET, gz1), (gx0, y + GLASS_SET, gz1)], M_GLASS)

    # ground-floor security grille: vertical bars, very common out here
    if grille:
        n = max(3, int(w / 0.17))
        for i in range(n + 1):
            bx = x0 + i * (w / n)
            add_box(bm, (bx - 0.008, -0.055, z0 + 0.02), (0.016, 0.016, h - 0.04), M_METAL)
        add_box(bm, (x0, -0.055, z0 + h * 0.5), (w, 0.016, 0.016), M_METAL)


def front_door(bm, cx, z0):
    """The entrance, with the steps and little canopy these houses all have."""
    x0 = cx - DOOR_W / 2
    recess = 0.22
    t = 0.022
    add_box(bm, (x0 - t, 0.0, z0), (t, recess, DOOR_H), M_PLASTER)
    add_box(bm, (x0 + DOOR_W, 0.0, z0), (t, recess, DOOR_H), M_PLASTER)
    add_box(bm, (x0 - t, 0.0, z0 + DOOR_H), (DOOR_W + 2 * t, recess, t), M_PLASTER)
    # panelled leaf
    add_box(bm, (x0, recess, z0), (DOOR_W, 0.055, DOOR_H), M_DOOR)
    for r in range(3):
        add_box(bm, (x0 + 0.1, recess - 0.016, z0 + 0.2 + r * 0.62),
                (DOOR_W - 0.2, 0.018, 0.44), M_DOOR)
    # canopy on two brackets
    add_box(bm, (cx - 0.95, -0.95, z0 + DOOR_H + 0.14), (1.9, 1.0, 0.09), M_ROOF)
    for sx in (cx - 0.78, cx + 0.68):
        add_box(bm, (sx, -0.1, z0 + DOOR_H + 0.02), (0.1, 0.1, 0.14), M_METAL)
    # steps down to grade
    for i, s in enumerate((0.30, 0.15)):
        add_box(bm, (cx - 0.85, -0.38 - i * 0.30, 0.0), (1.7, 0.32, z0 - i * 0.0 - (0.0 if i else 0.0) + s), M_CONCRETE)


# =============================================================================
# walls with real holes
# =============================================================================

def wall_cell(bm, x0, z0, w, h, openings, mat):
    """One run of wall with the openings left as voids: band under, band over,
    piers between. Same trick as the panel block -- no booleans."""
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


def facade(bm, width, storeys, rng, with_door, two_tone):
    """One elevation in the local frame: +x along the wall, +y into the house."""
    # bays: an ODD count gives a centre, which is where the door goes
    bays = max(2, int(round(width / 3.0)))
    if with_door and bays % 2 == 0:
        bays += 1
    bw = width / bays
    for f in range(storeys):
        z = PLINTH_H + f * FLOOR_H
        # ground floor often left as bare block while the top is painted
        mat = M_BLOCK if (two_tone and f == 0) else M_PLASTER
        for b in range(bays):
            bx = b * bw
            centre = with_door and f == 0 and b == bays // 2
            if centre:
                dx = bx + bw / 2 - DOOR_W / 2
                wall_cell(bm, bx, z, bw, FLOOR_H, [(dx, z + 0.0, DOOR_W, DOOR_H)], mat)
                front_door(bm, bx + bw / 2, z)
                continue
            ow = WIN_W
            oz = z + SILL_ABOVE_FLOOR
            ox = bx + bw / 2 - ow / 2
            wall_cell(bm, bx, z, bw, FLOOR_H, [(ox, oz, ow, WIN_H)], mat)
            window(bm, ox, oz, ow, WIN_H,
                   pvc=rng.random() < PVC_SHARE, grille=(f == 0 and rng.random() < 0.5))


def blank_wall(bm, width, storeys, rng, two_tone, windows=True):
    bays = max(1, int(round(width / 3.4)))
    bw = width / bays
    for f in range(storeys):
        z = PLINTH_H + f * FLOOR_H
        mat = M_BLOCK if (two_tone and f == 0) else M_PLASTER
        for b in range(bays):
            bx = b * bw
            if not windows or rng.random() < 0.35:
                wall_cell(bm, bx, z, bw, FLOOR_H, [], mat)
                continue
            ow = WIN_W * 0.82
            oz = z + SILL_ABOVE_FLOOR
            ox = bx + bw / 2 - ow / 2
            wall_cell(bm, bx, z, bw, FLOOR_H, [(ox, oz, ow, WIN_H)], mat)
            window(bm, ox, oz, ow, WIN_H, pvc=rng.random() < PVC_SHARE,
                   grille=(f == 0 and rng.random() < 0.4))


# =============================================================================
# roof + veranda
# =============================================================================

def pitched_roof(bm, W, D, z, kind):
    """A real roof: thickness, overhang, fascia board, soffit and a ridge cap.
    A bare plane meeting the wall is the second biggest tell after flat
    windows."""
    e = EAVES
    rise = math.tan(math.radians(ROOF_PITCH)) * (D / 2 + e)
    x0, x1 = -e, W + e
    y0, y1 = -e, D + e
    apex = z + rise

    if kind == "gable":
        # two slopes meeting at a ridge running along x
        ymid = D / 2
        for sgn, ya, yb in ((-1, y0, ymid), (1, y1, ymid)):
            v = [bm.verts.new((x0, ya, z)), bm.verts.new((x1, ya, z)),
                 bm.verts.new((x1, yb, apex)), bm.verts.new((x0, yb, apex))]
            bm.faces.new(v).material_index = M_ROOF
            # underside, offset down by the slab thickness
            vb = [bm.verts.new((x0, ya, z - ROOF_T)), bm.verts.new((x1, ya, z - ROOF_T)),
                  bm.verts.new((x1, yb, apex - ROOF_T)), bm.verts.new((x0, yb, apex - ROOF_T))]
            bm.faces.new(list(reversed(vb))).material_index = M_ROOF
            # eaves edge closing the slab
            bm.faces.new((v[0], v[1], vb[1], vb[0])).material_index = M_ROOF
        # gable end walls filling the triangle
        for xe in (0.0, W):
            add_quad(bm, [(xe, 0.0, z), (xe, D, z), (xe, D / 2, apex)], M_PLASTER)
            add_quad(bm, [(xe + 0.001, 0.0, z), (xe + 0.001, D / 2, apex), (xe + 0.001, D, z)], M_PLASTER)
        # ridge cap
        add_box(bm, (x0, D / 2 - RIDGE_T / 2, apex - 0.02), (x1 - x0, RIDGE_T, 0.06), M_METAL)
        # fascia along both eaves
        for ya in (y0, y1):
            add_box(bm, (x0, ya - (FASCIA_T if ya == y0 else 0), z - ROOF_T - FASCIA_H),
                    (x1 - x0, FASCIA_T, FASCIA_H + ROOF_T), M_WOOD)
    else:  # hip
        cx, cy = W / 2, D / 2
        corners = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
        top = bm.verts.new((cx, cy, apex))
        for i in range(4):
            a = corners[i]
            b = corners[(i + 1) % 4]
            va = bm.verts.new((a[0], a[1], z))
            vb = bm.verts.new((b[0], b[1], z))
            bm.faces.new((va, vb, top)).material_index = M_ROOF
            vab = bm.verts.new((a[0], a[1], z - ROOF_T))
            vbb = bm.verts.new((b[0], b[1], z - ROOF_T))
            bm.faces.new((vab, vbb, va, vb)).material_index = M_ROOF
        for ya, xa, w_, d_ in ((y0, x0, x1 - x0, FASCIA_T), (y1, x0, x1 - x0, FASCIA_T)):
            add_box(bm, (xa, ya, z - ROOF_T - FASCIA_H), (w_, d_, FASCIA_H + ROOF_T), M_WOOD)
        for xa in (x0, x1 - FASCIA_T):
            add_box(bm, (xa, y0, z - ROOF_T - FASCIA_H), (FASCIA_T, y1 - y0, FASCIA_H + ROOF_T), M_WOOD)

    # chimney, off to one side, with the projecting cap that reads as domestic
    cxp = W * 0.26
    cyp = D * 0.34
    ch = apex + 0.85 - z
    add_box(bm, (cxp, cyp, z - 0.4), (CHIMNEY_W, CHIMNEY_W * 0.72, ch + 0.4), M_BLOCK)
    add_box(bm, (cxp - 0.07, cyp - 0.07, z + ch), (CHIMNEY_W + 0.14, CHIMNEY_W * 0.72 + 0.14, 0.1), M_CONCRETE)


def veranda(bm, W, D, storeys, rng):
    """The open upper-floor veranda. More than anything else this is what makes
    a house read as Georgian rather than generic."""
    z = PLINTH_H + (storeys - 1) * FLOOR_H
    d = VER_DEPTH
    # deck slab
    add_box(bm, (0.0, -d, z - 0.12), (W, d, 0.12), M_CONCRETE)
    # posts carrying the eaves
    n = max(3, int(W / 2.6))
    for i in range(n + 1):
        px = min(W - VER_POST, i * (W / n))
        add_box(bm, (px, -d + 0.06, z), (VER_POST, VER_POST, FLOOR_H), M_WOOD)
    # balustrade: top and bottom rails with slim balusters between
    add_box(bm, (0.0, -d + 0.02, z + VER_RAIL_H - 0.06), (W, 0.07, 0.06), M_WOOD)
    add_box(bm, (0.0, -d + 0.02, z + 0.08), (W, 0.06, 0.05), M_WOOD)
    m = max(6, int(W / 0.22))
    for i in range(m + 1):
        bx = i * (W / m)
        add_box(bm, (bx - 0.015, -d + 0.035, z + 0.12), (0.03, 0.03, VER_RAIL_H - 0.19), M_WOOD)
    # the external stair down to the yard, at one end
    sx = W * 0.08 if rng.random() < 0.5 else W * 0.76
    steps = 9
    for i in range(steps):
        add_box(bm, (sx, -d - 0.32 - i * 0.30, z - 0.12 - (i + 1) * (z / steps)),
                (1.05, 0.32, 0.14), M_CONCRETE)


# =============================================================================
# build
# =============================================================================

def build_house(name, W, D, storeys, roof, has_veranda, seed):
    rng = random.Random(seed)
    bm = bmesh.new()
    two_tone = rng.random() < 0.45
    H = PLINTH_H + storeys * FLOOR_H

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

    # plinth + floor slabs
    add_box(bm, (-PLINTH_OUT, -PLINTH_OUT, 0.0),
            (W + 2 * PLINTH_OUT, D + 2 * PLINTH_OUT, PLINTH_H), M_CONCRETE)
    for f in range(storeys + 1):
        add_box(bm, (WALL_T, WALL_T, PLINTH_H + f * FLOOR_H - 0.1),
                (W - 2 * WALL_T, D - 2 * WALL_T, 0.1), M_CONCRETE)

    # four elevations. The front carries the door; the veranda replaces it
    # upstairs, so a house with a veranda puts its door on the side.
    sub(facade, W, storeys, rng, not has_veranda, two_tone)
    sub(blank_wall, W, storeys, random.Random(seed + 1), two_tone, True,
        rot=math.pi, at=(W, D))
    sub(blank_wall, D, storeys, random.Random(seed + 2), two_tone, True,
        rot=-math.pi / 2, at=(0.0, D))
    sub(blank_wall, D, storeys, random.Random(seed + 3), two_tone, True,
        rot=math.pi / 2, at=(W, 0.0))

    if has_veranda:
        veranda(bm, W, D, storeys, rng)
    pitched_roof(bm, W, D, H, roof)

    for v in bm.verts:
        v.co.x -= W / 2
        v.co.y -= D / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(col):
    objects = []
    mats = [make_material(n, hx, r, m) for n, hx, r, m in PALETTE]
    for i, (name, W, D, st, roof, ver) in enumerate(VARIANTS):
        bm = build_house(name, W, D, st, roof, ver, SEED + i * 37)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        obj = bpy.data.objects.new(name, me)
        col.objects.link(obj)
        # never materials.clear() on a fresh mesh: it silently resets every
        # face's material_index to 0 (see build_panel_block.py)
        for mat in mats:
            obj.data.materials.append(mat)
        me.polygons.foreach_set("use_smooth", [False] * len(me.polygons))
        me.update()
        if BEVEL_ON:
            mod = obj.modifiers.new("Bevel", "BEVEL")
            mod.width = BEVEL_W
            mod.segments = BEVEL_SEGMENTS
            mod.limit_method = "ANGLE"
            mod.angle_limit = math.radians(50)
            mod.harden_normals = True
        obj.location = (0, i * 18, 0)
        objects.append(obj)
    return objects


def triangle_count(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    tot = 0
    for o in objs:
        if o.type != "MESH":
            continue
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg))
        me.calc_loop_triangles()
        tot += len(me.loop_triangles)
        bpy.data.meshes.remove(me)
    return tot


def export(objs, directory, name):
    os.makedirs(directory, exist_ok=True)
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    path = os.path.abspath(os.path.join(directory, name + ".glb"))
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
        print(f"    {o.name:12s} {triangle_count([o]):>7,} tris")
    print(f"Triangles : {triangle_count(objs):,}")
    print(f"Exported  : {path}")
    print("-" * 64)


if __name__ == "__main__":
    main()
