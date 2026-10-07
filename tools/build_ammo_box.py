# A wooden small-arms ammunition crate, modeled in Blender for the warehouse
# forecourt at TBS:
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_ammo_box.py
#
# The Soviet pattern crate the game's 9x18 Makarov is fed from: a planked pine
# box on two skids, a three-board lid with cross battens, hemp rope handles
# through an end cleat, galvanised corner guards, two strap hinges at the back
# and a hasp at the front, stencilled in black with calibre, count, weight
# and lot -- digits and Latin units only, no Russian lettering. Pine grain is a texture written pixel by pixel here (no bake, no
# external image) and packed into the GLB.
#
# Convention (same as the airport kit): real metre scale, origin at ground
# centre, lettering on the front which faces Blender -Y (three.js +Z after the
# Y-up export). Blender 4.2+/5.x, background mode.
import math
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import bmesh  # noqa: E402
import bpy  # noqa: E402
from mathutils import Matrix, Vector, noise  # noqa: E402

import build_statues as bs  # noqa: E402  (reset/link/join/shoot helpers)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'public', 'models', 'ammo_box.glb')
TEX = os.path.join(HERE, '..', 'models_raw', 'ammo_box_wood.png')
SHOTS = os.environ.get('AMMO_SHOTS', '/tmp')

# ---- CONFIG -----------------------------------------------------------------
L, W = 0.80, 0.40      # m — footprint; a two-can 7.62/9 mm crate is this size
PLANK = 0.022          # m — 22 mm sawn pine boards
GAP = 0.003            # m — daylight between boards, the shadow that says "planks"
SKID = 0.022           # m — two runners underneath keep the floor off wet ground
SIDE_H = 0.29          # m — the body's board height above the floor slab
LID_T = 0.025          # m — lid boards
BATTEN = (0.05, 0.022) # m — cleat width, thickness (lid cross battens, side cleats)
ROPE_R = 0.0085        # m — 17 mm hemp handle
STEEL_T = 0.0015       # m — strap and guard sheet steel
BEVEL = 0.0015         # m — one-segment bevel on every board edge; catches light
TEX_W, TEX_H = 1024, 512  # px — wood image; u covers 1.0 m, v 0.5 m
# ----------------------------------------------------------------------------

bs.reset()
scene = bpy.context.scene

# ---- materials --------------------------------------------------------------


def material(name, rgb, rough=0.8, metal=0.0, image=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes['Principled BSDF']
    bsdf.inputs['Base Color'].default_value = (*rgb, 1.0)
    bsdf.inputs['Roughness'].default_value = rough
    bsdf.inputs['Metallic'].default_value = metal
    m.diffuse_color = (*rgb, 1.0)
    if image is not None:
        tex = m.node_tree.nodes.new('ShaderNodeTexImage')
        tex.image = image
        tex.location = (-400, 300)
        m.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    return m


def wood_image():
    """Aged pine written straight into pixels: growth rings run along u (the
    board's length), warped by two octaves of noise so no two boards match,
    with a slow tint drift so the whole crate is not one flat colour."""
    img = bpy.data.images.new('ammo_wood', TEX_W, TEX_H, alpha=False)
    light = (0.58, 0.45, 0.29)   # sRGB; darker than it looks here, the game's
    dark = (0.31, 0.22, 0.13)    # ACES + HDR lift a loaded prop a good deal
    px = [0.0] * (TEX_W * TEX_H * 4)
    two_pi = math.pi * 2
    i = 0
    for y in range(TEX_H):
        v = y / TEX_H
        for x in range(TEX_W):
            u = x / TEX_W
            warp = noise.noise(Vector((u * 2.5, v * 5.0, 0.37))) * 0.9 \
                + noise.noise(Vector((u * 11.0, v * 22.0, 1.1))) * 0.25
            ring = 0.5 + 0.5 * math.sin((v * 13.0 + warp + u * 0.8) * two_pi)
            ring = ring ** 2.2                       # thin dark latewood lines
            fine = noise.noise(Vector((u * 60.0, v * 140.0, 3.3))) * 0.08
            tint = noise.noise(Vector((u * 1.2, v * 1.2, 7.7))) * 0.07
            k = min(1.0, max(0.0, ring * 0.7 + 0.15 + fine))
            r = light[0] + (dark[0] - light[0]) * k + tint
            g = light[1] + (dark[1] - light[1]) * k + tint * 0.8
            b = light[2] + (dark[2] - light[2]) * k + tint * 0.5
            px[i] = min(1.0, max(0.0, r))
            px[i + 1] = min(1.0, max(0.0, g))
            px[i + 2] = min(1.0, max(0.0, b))
            px[i + 3] = 1.0
            i += 4
    img.pixels.foreach_set(px)
    img.filepath_raw = TEX
    img.file_format = 'PNG'
    img.save()
    return bpy.data.images.load(TEX)  # a file-backed image is what the exporter packs


WOOD = material('crate_wood', (0.6, 0.48, 0.32), rough=0.85, image=wood_image())
STEEL = material('crate_steel', (0.42, 0.43, 0.44), rough=0.5, metal=0.9)   # galvanised
IRON = material('crate_iron', (0.16, 0.16, 0.17), rough=0.55, metal=0.85)   # blackened hinge strap
ROPE = material('crate_rope', (0.58, 0.47, 0.30), rough=0.95)
PAINT = material('crate_paint', (0.04, 0.04, 0.045), rough=0.9)

PARTS = []  # (mesh object) collected for the final join

# ---- mesh helpers -------------------------------------------------------------


def _box_uvs(bm, uv):
    """Box-project UVs from world position: the dominant normal axis picks the
    two coordinates, scaled so the 1024x512 image spans 1.0 x 0.5 m."""
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda k: abs(n[k]))
        for loop in f.loops:
            p = loop.vert.co
            if ax == 2:      # top/bottom: grain along x
                uv_ = (p.x, p.y)
            elif ax == 1:    # long sides: grain along x
                uv_ = (p.x, p.z)
            else:            # ends: grain along y
                uv_ = (p.y, p.z)
            loop[uv].uv = (uv_[0] * 1.0, uv_[1] * 2.0)


def _finish(name, bm, mat, smooth=False):
    bm.normal_update()
    uv = bm.loops.layers.uv.new('UVMap')
    _box_uvs(bm, uv)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = smooth
    me.update()
    o = bs.link(bpy.data.objects.new(name, me))
    PARTS.append(o)
    return o


def box(name, dims, center, mat=WOOD, bevel=BEVEL):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(dims), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
    if bevel and min(dims) > bevel * 4:
        bmesh.ops.bevel(bm, geom=bm.verts[:] + bm.edges[:], offset=bevel,
                        offset_type='OFFSET', segments=1, profile=0.5, affect='EDGES')
    return _finish(name, bm, mat)


def cylinder(name, r, length, center, axis='z', mat=IRON, segs=12):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=segs, radius1=r, radius2=r, depth=length)
    if axis == 'x':
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, 'Y'), verts=bm.verts)
    elif axis == 'y':
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, 'X'), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
    return _finish(name, bm, mat, smooth=True)


def rope(name, pts):
    """A hemp handle: a bevelled poly curve through pts, baked to a mesh."""
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = ROPE_R
    cu.bevel_resolution = 4
    cu.use_fill_caps = True
    sp = cu.splines.new('POLY')
    sp.points.add(len(pts) - 1)
    for p, xyz in zip(sp.points, pts):
        p.co = (*xyz, 1.0)
    tmp = bs.link(bpy.data.objects.new(name + '_curve', cu))
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(tmp.evaluated_get(dg))
    bpy.data.objects.remove(tmp)
    me.materials.append(ROPE)
    for p in me.polygons:
        p.use_smooth = True
    o = bs.link(bpy.data.objects.new(name, me))
    PARTS.append(o)
    return o


def stencil(name, text, size, at, face):
    """Black stencil lettering, 0.6 mm proud of the board so it never
    z-fights. `face` is which side it sits on: 'front' (-Y), 'back' (+Y),
    'top' (+Z), 'end+' (+X) or 'end-' (-X); `at` is the lettering centre."""
    cu = bpy.data.curves.new(name, 'FONT')
    cu.body = text
    cu.size = size
    cu.extrude = 0.0003
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    cu.resolution_u = 6
    tmp = bs.link(bpy.data.objects.new(name + '_text', cu))
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(tmp.evaluated_get(dg))
    bpy.data.objects.remove(tmp)
    # text is born in the XY plane facing +Z, reading along +X
    rot = {
        'top': Matrix.Identity(4),
        'front': Matrix.Rotation(math.pi / 2, 4, 'X'),                 # +Z -> -Y
        'back': Matrix.Rotation(-math.pi / 2, 4, 'X') @ Matrix.Rotation(math.pi, 4, 'Z'),
        'end+': Matrix.Rotation(math.pi / 2, 4, 'Z') @ Matrix.Rotation(math.pi / 2, 4, 'X'),
        'end-': Matrix.Rotation(-math.pi / 2, 4, 'Z') @ Matrix.Rotation(math.pi / 2, 4, 'X'),
    }[face]
    me.transform(Matrix.Translation(at) @ rot @ Matrix.Translation((0, 0, 0.0003)))
    me.materials.append(PAINT)
    me.update()
    o = bs.link(bpy.data.objects.new(name, me))
    PARTS.append(o)
    return o


# ---- the crate --------------------------------------------------------------
z0 = SKID                       # top of the skids = bottom of the floor
z1 = z0 + PLANK                 # top of the floor slab = bottom of the side boards
z2 = z1 + SIDE_H                # top of the body = underside of the lid
z3 = z2 + LID_T                 # top of the lid boards
H = z3 + BATTEN[1]              # overall, including the lid battens

# skids
for sx in (-1, 1):
    box(f'skid{sx}', (0.045, W, SKID), (sx * (L / 2 - 0.07), 0, SKID / 2))
# floor
box('floor', (L, W, PLANK), (0, 0, z0 + PLANK / 2), bevel=0)
# long sides: two boards each, the lower one taller as the real ones are
bh = [(SIDE_H - GAP) * 0.55, (SIDE_H - GAP) * 0.45]
for sy in (-1, 1):
    z = z1
    for k, hgt in enumerate(bh):
        box(f'side{sy}_{k}', (L, PLANK, hgt), (0, sy * (W / 2 - PLANK / 2), z + hgt / 2))
        z += hgt + GAP
# ends: boards sit between the long sides
for sx in (-1, 1):
    z = z1
    for k, hgt in enumerate(bh):
        box(f'end{sx}_{k}', (PLANK, W - 2 * PLANK, hgt), (sx * (L / 2 - PLANK / 2), 0, z + hgt / 2))
        z += hgt + GAP
# vertical cleats on the long sides, in from the corners
for sx in (-1, 1):
    for sy in (-1, 1):
        box(f'cleat{sx}{sy}', (BATTEN[0], BATTEN[1], SIDE_H),
            (sx * (L / 2 - 0.075), sy * (W / 2 + BATTEN[1] / 2), z1 + SIDE_H / 2))
# the horizontal rope cleat on each end, mid-height, the handle threads it
for sx in (-1, 1):
    box(f'ropecleat{sx}', (BATTEN[1], W - 0.05, 0.07),
        (sx * (L / 2 + BATTEN[1] / 2), 0, z1 + SIDE_H * 0.55))
# lid: three boards along the length
bw = (W - 2 * GAP) / 3
for k in range(3):
    y = -W / 2 + bw / 2 + k * (bw + GAP)
    box(f'lid{k}', (L, bw, LID_T), (0, y, z2 + LID_T / 2))
# two cross battens on the lid, just in from the ends
for sx in (-1, 1):
    box(f'lidbatten{sx}', (BATTEN[0], W - 0.02, BATTEN[1]),
        (sx * (L / 2 - 0.10), 0, z3 + BATTEN[1] / 2))

# ---- hardware ---------------------------------------------------------------
# galvanised corner guards: an L of two plates on the bottom and lid corners
for sx in (-1, 1):
    for sy in (-1, 1):
        for zc, hz in ((z0 + 0.035, 0.07), (z3 - 0.03, 0.06)):
            box(f'guardx{sx}{sy}{zc:.2f}', (0.045, STEEL_T, hz),
                (sx * (L / 2 - 0.0225), sy * (W / 2 + STEEL_T / 2), zc), mat=STEEL, bevel=0)
            box(f'guardy{sx}{sy}{zc:.2f}', (STEEL_T, 0.045, hz),
                (sx * (L / 2 + STEEL_T / 2), sy * (W / 2 - 0.0225), zc), mat=STEEL, bevel=0)
# strap hinges across the back joint
for sx in (-1, 1):
    x = sx * 0.24
    box(f'hinge{sx}', (0.075, STEEL_T, 0.055), (x, W / 2 + STEEL_T / 2, z2), mat=IRON, bevel=0)
    cylinder(f'knuckle{sx}', 0.006, 0.075, (x, W / 2 + 0.004, z2), axis='x')
# hasp at the front: strap hanging off the lid, staple on the body
box('hasp', (0.03, STEEL_T, 0.10), (0, -W / 2 - STEEL_T / 2, z2 + 0.015), mat=IRON, bevel=0)
cylinder('staple_bar', 0.004, 0.026, (0, -W / 2 - 0.013, z2 - 0.04), axis='x', segs=8)
for sx in (-1, 1):
    cylinder(f'staple_leg{sx}', 0.004, 0.016, (sx * 0.011, -W / 2 - 0.006, z2 - 0.04), axis='y', segs=8)
# a lead seal on a wire through the hasp, the thing you snap to open a sealed crate
cylinder('seal', 0.007, 0.004, (0, -W / 2 - 0.016, z2 - 0.055), axis='y', mat=STEEL, segs=10)

# ---- rope handles -----------------------------------------------------------
zr = z1 + SIDE_H * 0.55
for sx in (-1, 1):
    pts = []
    n = 22
    for i in range(n + 1):
        t = i / n
        s = math.sin(math.pi * t)
        pts.append((sx * (L / 2 + BATTEN[1] + 0.004 + 0.05 * s), -0.10 + 0.20 * t, zr - 0.075 * s))
    rope(f'rope{sx}', pts)

# ---- stencils ---------------------------------------------------------------
# Digits and Latin units only (the Cyrillic lettering of the first cut was
# asked off): calibre, round count, gross weight, lot number.
stencil('st_cal', '9×18', 0.085, (0, -W / 2, z1 + SIDE_H * 0.66), 'front')
stencil('st_cnt', '2240 × 9 mm', 0.030, (0, -W / 2, z1 + SIDE_H * 0.30), 'front')
stencil('st_lid', '9×18 · 2240', 0.046, (0, 0, z3), 'top')
stencil('st_wt', '24 KG', 0.030, (0, W / 2, z1 + SIDE_H * 0.30), 'back')
for sx, face in ((1, 'end+'), (-1, 'end-')):
    stencil(f'st_lot{sx}', 'LOT 14-82', 0.022, (sx * (L / 2), 0, z1 + SIDE_H * 0.86), face)

# ---- join + export ----------------------------------------------------------
crate = bs.join(PARTS)
crate.name = 'ammo_box'
crate.data.name = 'ammo_box'

xs = [v.co.x for v in crate.data.vertices]
ys = [v.co.y for v in crate.data.vertices]
zs = [v.co.z for v in crate.data.vertices]
print(f'ammo_box bounds: x {min(xs):.3f}..{max(xs):.3f}  y {min(ys):.3f}..{max(ys):.3f}  z {min(zs):.3f}..{max(zs):.3f}')

if os.environ.get('AMMO_SHOTS'):
    # workbench shows the pine only in TEXTURE colour mode
    scene.display.shading.color_type = 'TEXTURE'
    bs.shoot('ammo_box')

bpy.ops.object.select_all(action='DESELECT')
crate.select_set(True)
bpy.context.view_layer.objects.active = crate
kw = dict(filepath=OUT, export_format='GLB', use_selection=True, export_apply=True,
          export_yup=True, export_image_format='JPEG', export_jpeg_quality=85)
try:
    bpy.ops.export_scene.gltf(**kw)
except TypeError:
    kw.pop('export_jpeg_quality', None)
    bpy.ops.export_scene.gltf(**kw)
crate.data.calc_loop_triangles()
print(f'EXPORT ammo_box: {max(xs)-min(xs):.2f} x {max(ys)-min(ys):.2f} x {max(zs)-min(zs):.2f} m, '
      f'{len(crate.data.loop_triangles)} tris, {len(crate.data.materials)} materials → {OUT} '
      f'({os.path.getsize(OUT)/1024:.0f} KB)')
