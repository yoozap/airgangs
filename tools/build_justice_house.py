"""Blender-headless: Tbilisi Public Service Hall (იუსტიციის სახლი) -> one glb.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_justice_house.py

Massimiliano & Doriana Fuksas, opened 2012-09-21, on the left bank of the
Mtkvari beside Dedaena Park. OSM way 1066392731, building=public, 7 levels --
the footprint below is that way, in metres relative to its centroid
(41.699024, 44.806656), already in the game's frame so it needs no yaw.

What the building actually is, from photographs:
  * eleven white "petal" canopies -- flattened organic domes with a wavy rim,
    thick at the crown and thin at the edge, overlapping at different heights
    from about 20 m to 33 m. They are the building; everything else shelters
    under them.
  * a loose group of glazed office volumes below, 3-5 storeys, stepped and
    offset with terraces between, NOT one slab. The glazing is a close-spaced
    vertical fin curtain wall in a pale green-gold tint, with darker flush
    glass on the recessed faces.
  * slender white steel columns, mostly in splayed groups of three or four
    like bare trees, carrying each petal.

Convention (same as the other kits): real metres, Z up, origin at the footprint
centroid at ground level, and Blender XY maps to the game's local X/Z so
export_yup drops it straight in at the right bearing.
"""
import math
import os
import random

import bmesh
import bpy
from mathutils import Vector, noise

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                   'public', 'models', 'justice_house.glb')
SEED = 20120921  # the opening date; any seed does, this one is just honest
random.seed(SEED)
noise.seed_set(SEED)

# ---- CONFIG ----------------------------------------------------------------
STOREY = 4.0          # m — floor to floor; the photos read as generous office
PODIUM_H = 3 * STOREY  # m — the continuous base the upper boxes sit on
GLASS_INSET = 1.2     # m — how far the glazing sits behind the slab edge
FIN_SPACING = 1.9     # m — vertical fins on the curtain wall, from the photos
FIN_DEPTH = 0.22      # m
PETAL_MIN_H = 19.5    # m — lowest petal underside
PETAL_MAX_H = 32.0    # m — highest
COL_R = 0.30          # m — the slender white columns

# OSM way 1066392731, metres from the centroid, in the game's frame
FOOTPRINT = [
    (48.44, -25.41), (35.68, -27.34), (38.69, -47.06), (24.15, -49.26),
    (26.50, -64.63), (-4.76, -69.38), (-7.75, -49.86), (-17.88, -51.39),
    (-22.31, -22.37), (-37.37, -24.65), (-48.21, 46.23), (-40.72, 47.37),
    (-22.50, 50.14), (-24.23, 61.37), (4.68, 65.75), (7.82, 45.23),
    (25.56, 47.93), (30.77, 13.76), (42.18, 15.50), (43.35, 7.82),
]

# Eleven petals: (x, y, crown height, long radius, short radius, rotation).
# Laid out over the footprint the way the aerial photograph shows them --
# a loose overlapping raft, biggest over the middle, spilling past the glass.
PETALS = [
    (-30.0, 38.0, 25.0, 25.0, 19.0, 0.35),
    (-31.0, 8.0, 21.5, 22.0, 17.0, -0.25),
    (-8.0, 46.0, 28.5, 24.0, 18.0, 0.15),
    (-6.0, 18.0, 32.0, 27.0, 20.0, -0.10),
    (-14.0, -14.0, 24.5, 23.0, 17.5, 0.42),
    (10.0, 38.0, 26.0, 21.0, 16.0, -0.35),
    (12.0, 4.0, 29.0, 25.0, 18.5, 0.22),
    (6.0, -28.0, 23.0, 22.0, 17.0, -0.45),
    (30.0, 18.0, 25.5, 20.0, 15.5, 0.30),
    (28.0, -12.0, 22.0, 19.0, 15.0, -0.20),
    (-38.0, -8.0, 20.0, 17.0, 13.5, 0.55),
]

bpy.ops.wm.read_factory_settings(use_empty=True)
D = bpy.data


def mat(name, hx, rough=0.8, metal=0.0, alpha=1.0):
    m = D.materials.get(name)
    if m:
        return m
    m = D.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes['Principled BSDF']
    r, g, bl = (int(hx[i:i + 2], 16) / 255 for i in (1, 3, 5))
    b.inputs['Base Color'].default_value = (r ** 2.2, g ** 2.2, bl ** 2.2, alpha)
    b.inputs['Roughness'].default_value = rough
    b.inputs['Metallic'].default_value = metal
    if alpha < 1.0:
        b.inputs['Alpha'].default_value = alpha
        m.blend_method = 'BLEND'
    m.diffuse_color = (r ** 2.2, g ** 2.2, bl ** 2.2, alpha)
    return m


M_PETAL = mat('jh_petal', '#f2efe8', rough=0.55)        # the white shells
M_SOFFIT = mat('jh_soffit', '#e4e0d6', rough=0.7)       # their underside
M_COL = mat('jh_column', '#f4f4f2', rough=0.45, metal=0.15)
M_GLASS = mat('jh_glass', '#8fa98c', rough=0.12, metal=0.55, alpha=0.72)
M_FIN = mat('jh_fin', '#cfd6c4', rough=0.35, metal=0.3)  # the vertical fins
M_SLAB = mat('jh_slab', '#d8d6d0', rough=0.8)            # floor slab edges
M_DARK = mat('jh_darkglass', '#37423f', rough=0.1, metal=0.6)
M_PLINTH = mat('jh_plinth', '#b9b6ae', rough=0.9)


def finish(bm, name, material, uv_scale=4.0, smooth=False):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        ui, vi = (1, 2) if ax == 0 else (0, 2) if ax == 1 else (0, 1)
        for loop in f.loops:
            co = loop.vert.co
            loop[uv].uv = (co[ui] / uv_scale, co[vi] / uv_scale)
    mesh = D.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    if smooth:
        # the shells are a curved surface, not a stack of plates: flat shading
        # turned every ring of the radial grid into a visible concentric step
        mesh.polygons.foreach_set('use_smooth', [True] * len(mesh.polygons))
    mesh.update()
    ob = D.objects.new(name, mesh)
    ob.data.materials.append(material)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def cube(bm, w, d, h, x=0.0, y=0.0, z=0.0, rot=0.0):
    """w along X, d along Y, h along Z; (x, y, z) is the centre of the base."""
    hw, hd = w / 2, d / 2
    c, s = math.cos(rot), math.sin(rot)
    corners = [(-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd)]
    lo, hi = [], []
    for cx, cy in corners:
        px = x + cx * c - cy * s
        py = y + cx * s + cy * c
        lo.append(bm.verts.new((px, py, z)))
        hi.append(bm.verts.new((px, py, z + h)))
    bm.faces.new(lo[::-1])
    bm.faces.new(hi)
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((lo[i], lo[j], hi[j], hi[i]))


def prism(bm, poly, z0, z1):
    """Extrude a closed XY polygon between two heights."""
    lo = [bm.verts.new((x, y, z0)) for x, y in poly]
    hi = [bm.verts.new((x, y, z1)) for x, y in poly]
    n = len(poly)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((lo[i], lo[j], hi[j], hi[i]))
    bm.faces.new(lo[::-1])
    bm.faces.new(hi)


def inset_poly(poly, d):
    """Shrink a convex-ish polygon toward its centroid by d metres."""
    cx = sum(p[0] for p in poly) / len(poly)
    cy = sum(p[1] for p in poly) / len(poly)
    out = []
    for x, y in poly:
        vx, vy = x - cx, y - cy
        L = math.hypot(vx, vy) or 1
        out.append((cx + vx * (L - d) / L, cy + vy * (L - d) / L))
    return out


# ---- the petals ------------------------------------------------------------
# A Fuksas petal is not a dome on a stick: it is a flattened shell, thick at
# the crown and knife-thin at the rim, with a rim that WANDERS -- no two of the
# eleven have the same outline. Build it as a radial grid whose rim radius is a
# smooth wavy function of the angle, with a domed top and a shallower underside
# so the section tapers outward.
def petal(bm, cx, cy, crown, ra, rb, rot, k):
    RINGS, SEG = 14, 56
    thick = 4.2            # m at the crown -- the photos show a deep section
    edge = 0.25            # m at the rim, where it goes knife-thin
    rise = 6.0             # m the top surface climbs from rim to crown
    sag = 1.0              # m the underside dips below the rim line
    # a per-petal rim wobble, so the eleven read as eleven shapes
    a1, p1 = 0.10 + 0.05 * ((k * 7) % 3), k * 1.7
    a2, p2 = 0.06 + 0.03 * ((k * 5) % 3), k * 2.9

    def rim(t):
        return 1.0 + a1 * math.sin(2 * t + p1) + a2 * math.sin(3 * t + p2)

    def place(u, t, z):
        """u = 0 at centre, 1 at rim; t = angle."""
        r = rim(t) * u
        lx, ly = math.cos(t) * r * ra, math.sin(t) * r * rb
        c, s = math.cos(rot), math.sin(rot)
        return (cx + lx * c - ly * s, cy + lx * s + ly * c, z)

    top, bot = [], []
    for i in range(RINGS + 1):
        u = i / RINGS
        # dome: flat-ish near the crown, falling away fast at the rim
        zt = crown - rise * (u ** 1.8)
        zb = crown - thick + (thick - edge) * (u ** 1.5) - sag * (u ** 2) * 0.0
        if i == RINGS:
            zb = zt - edge
        rt, rbo = [], []
        for s in range(SEG):
            t = 2 * math.pi * s / SEG
            rt.append(bm.verts.new(place(u, t, zt)))
            rbo.append(bm.verts.new(place(u, t, zb)))
        top.append(rt)
        bot.append(rbo)
    for i in range(RINGS):
        for s in range(SEG):
            t2 = (s + 1) % SEG
            bm.faces.new((top[i][s], top[i][t2], top[i + 1][t2], top[i + 1][s]))
            bm.faces.new((bot[i + 1][s], bot[i + 1][t2], bot[i][t2], bot[i][s]))
    for s in range(SEG):            # close the knife edge
        t2 = (s + 1) % SEG
        bm.faces.new((top[RINGS][s], bot[RINGS][s], bot[RINGS][t2], top[RINGS][t2]))


bm = bmesh.new()
for k, (px, py, ph, ra, rb, rot) in enumerate(PETALS):
    petal(bm, px, py, ph, ra, rb, rot, k)
ob_petal = finish(bm, 'petals', M_PETAL, uv_scale=8.0, smooth=True)

# ---- the glazed volumes ----------------------------------------------------
# A continuous podium on the real footprint, then a loose set of upper boxes
# stepped back off it, which is what the aerial shows: not one block.
bm = bmesh.new()
prism(bm, inset_poly(FOOTPRINT, 0.0), 0.0, 0.35)        # plinth
ob_plinth = finish(bm, 'plinth', M_PLINTH, uv_scale=6.0)

bm = bmesh.new()
glass_poly = inset_poly(FOOTPRINT, GLASS_INSET)
prism(bm, glass_poly, 0.35, PODIUM_H)
ob_podium = finish(bm, 'podium_glass', M_GLASS, uv_scale=6.0)

# floor slab edges: the horizontal lines that read at distance
bm = bmesh.new()
for lvl in range(1, 8):
    z = 0.35 + lvl * STOREY
    if z > PODIUM_H + 4 * STOREY:
        break
    prism(bm, FOOTPRINT if z <= PODIUM_H else inset_poly(FOOTPRINT, 6.0),
          z - 0.35, z)
ob_slabs = finish(bm, 'slab_edges', M_SLAB, uv_scale=6.0)

# upper boxes, offset and turned slightly off the podium grid
UPPER = [
    (-28.0, 30.0, 34.0, 26.0, 2 * STOREY, 0.06),
    (2.0, 34.0, 30.0, 22.0, 2 * STOREY, -0.05),
    (14.0, -2.0, 30.0, 30.0, 3 * STOREY, 0.03),
    (-18.0, -22.0, 26.0, 22.0, 2 * STOREY, -0.08),
    (28.0, 10.0, 20.0, 20.0, 2 * STOREY, 0.10),
]
bm = bmesh.new()
for x, y, w, d, h, r in UPPER:
    cube(bm, w, d, h, x, y, PODIUM_H, r)
ob_upper = finish(bm, 'upper_glass', M_DARK, uv_scale=6.0)

# ---- the vertical fin curtain wall ----------------------------------------
# The single most recognisable thing about the elevations after the petals:
# close-spaced vertical fins running the full height of every glazed face.
bm = bmesh.new()
n = len(glass_poly)
for i in range(n):
    x1, y1 = glass_poly[i]
    x2, y2 = glass_poly[(i + 1) % n]
    seg = math.hypot(x2 - x1, y2 - y1)
    if seg < 2:
        continue
    ux, uy = (x2 - x1) / seg, (y2 - y1) / seg
    nx, ny = -uy, ux
    count = max(1, int(seg / FIN_SPACING))
    for f in range(count):
        t = (f + 0.5) * seg / count
        px, py = x1 + ux * t, y1 + uy * t
        cube(bm, 0.09, FIN_DEPTH, PODIUM_H - 0.8,
             px + nx * FIN_DEPTH * 0.4, py + ny * FIN_DEPTH * 0.4, 0.5,
             math.atan2(uy, ux))
ob_fins = finish(bm, 'fins', M_FIN, uv_scale=3.0)

# ---- the columns -----------------------------------------------------------
# Splayed groups of three or four, leaning out from a common foot to meet the
# petal soffit -- the "bare tree" the photographs show, not a plain post.
def column(bm, x0, y0, z0, x1, y1, z1, r=COL_R, seg=7):
    ax = Vector((x1 - x0, y1 - y0, z1 - z0))
    L = ax.length
    if L < 0.5:
        return
    ax.normalize()
    ref = Vector((0, 0, 1)) if abs(ax.z) < 0.9 else Vector((1, 0, 0))
    u = ax.cross(ref).normalized()
    v = ax.cross(u).normalized()
    lo, hi = [], []
    for s in range(seg):
        a = 2 * math.pi * s / seg
        off = u * (math.cos(a) * r) + v * (math.sin(a) * r)
        lo.append(bm.verts.new((x0 + off.x, y0 + off.y, z0 + off.z)))
        hi.append(bm.verts.new((x1 + off.x * 0.75, y1 + off.y * 0.75, z1 + off.z * 0.75)))
    for s in range(seg):
        t2 = (s + 1) % seg
        bm.faces.new((lo[s], lo[t2], hi[t2], hi[s]))
    bm.faces.new(lo[::-1])
    bm.faces.new(hi)


bm = bmesh.new()
for k, (px, py, ph, ra, rb, rot) in enumerate(PETALS):
    under = ph - 4.2            # the petal soffit at the crown
    base_z = PODIUM_H if k % 3 else 0.35   # some spring off the roof, some off the ground
    # one splayed cluster per petal, plus a couple of outriggers to the rim
    legs = 3 + (k % 2)
    foot = (px + math.cos(rot) * 2.0, py + math.sin(rot) * 2.0)
    for i in range(legs):
        a = 2 * math.pi * i / legs + rot
        spread = min(ra, rb) * 0.55
        column(bm, foot[0], foot[1], base_z,
               px + math.cos(a) * spread, py + math.sin(a) * spread, under - 0.15)
    for i in range(2):
        a = 2 * math.pi * (i + 0.5) / 2 + rot * 1.7
        column(bm, px + math.cos(a) * ra * 0.45, py + math.sin(a) * rb * 0.45, base_z,
               px + math.cos(a) * ra * 0.9, py + math.sin(a) * rb * 0.9,
               under + 0.6, r=COL_R * 0.8)
ob_cols = finish(bm, 'columns', M_COL, uv_scale=3.0)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', use_selection=True,
                          export_apply=True, export_yup=True)
tris = 0
rows = []
for o in D.objects:
    if o.type != 'MESH':
        continue
    o.data.calc_loop_triangles()
    t = len(o.data.loop_triangles)
    tris += t
    rows.append((o.name, t))
print('justice house exported:', OUT)
for n2, t in sorted(rows):
    print(f'  {n2:14s} {t:6d} tris')
print(f'  total {tris} tris')
