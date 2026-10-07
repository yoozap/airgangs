"""Blender-headless: realistic Mtkvari embankment kit -> one glb.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_embankment_kit.py

Replaces the blocky river pieces in infra_kit.glb. Modelled against photographs
of the Mtkvari through the centre (Baratashvili and Gorgasali embankments,
Wikimedia Commons): the river runs between BATTERED concrete walls topped by a
granite coping course, and above that a continuous stone balustrade with turned
balusters, broken by square piers, with lamp columns behind it. The old kit had
the wall and the coping but no parapet at all, so the river read as a kerb with
a drop behind it.

Objects (names matter -- src/infra.js looks them up):
  quay        3 m bay of embankment wall: battered face, two course reveals,
              granite coping with a drip chamfer, weep pipe, mooring ring
  quay_step   the same bay with the flight of steps down to the water
  bal_stone   3 m balustrade bay: moulded plinth, 7 turned balusters, handrail
  bal_pier    the square pier that interrupts the balustrade every few bays
  quay_lamp   embankment lamp column: fluted base, tapered shaft, lantern

Convention (same as the other kits): real metres, Z up, origin at the piece's
ground contact, the piece runs along X, and the FRONT face looks down -Y so
after export_yup it faces +Z in three.js. The river is at -Y.

UVs are box-projected at real-world scale so the ambientCG PBR sets that
src/infra.js binds (quaywall / quaystone, both CC0) tile in metres rather than
per-face. Run tools/fetch_infra_textures.py first to have them on disk.
"""
import math
import os

import bmesh
import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                   'public', 'models', 'embankment_kit.glb')

# ---- CONFIG ----------------------------------------------------------------
BAY = 3.0          # m — every piece is one 3 m bay, the placement step
WALL_H = 4.2       # m — waterline to walkway; scaled per segment in the game
BATTER = 0.34      # m — how far the face leans back over the full height.
                   #     Real river walls are never plumb; this is what makes
                   #     the wall read as engineering rather than a cardboard
                   #     strip when the light rakes across it.
COPING_H = 0.30    # m — granite coping course on top of the wall
COPING_OUT = 0.13  # m — how far the coping oversails the face (throws the drip)
REVEAL_D = 0.055   # m — depth of the horizontal construction-joint grooves
RAIL_H = 1.03      # m — balustrade height, the standard parapet
N_BALUSTER = 7     # per 3 m bay -> 0.43 m centres, what the photos show
BALUSTER_SIDES = 6  # cheap lathe: these are instanced ~12 000 times
UV_WALL = 2.4      # m per texture tile on the concrete
UV_STONE = 1.3     # m per texture tile on the granite

bpy.ops.wm.read_factory_settings(use_empty=True)
D = bpy.data


def mat(name, hx, rough=0.9, metal=0.0):
    m = D.materials.get(name)
    if m:
        return m
    m = D.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes['Principled BSDF']
    r, g, bl = (int(hx[i:i + 2], 16) / 255 for i in (1, 3, 5))
    b.inputs['Base Color'].default_value = (r ** 2.2, g ** 2.2, bl ** 2.2, 1)
    b.inputs['Roughness'].default_value = rough
    b.inputs['Metallic'].default_value = metal
    m.diffuse_color = (r ** 2.2, g ** 2.2, bl ** 2.2, 1)
    return m


# Tints multiply the photo texture in game, so they stay close to white; the
# colour comes from the ambientCG set, not from here.
M_WALL = mat('emb_wall', '#9a9a95', rough=0.92)
M_STONE = mat('emb_stone', '#c3c0b8', rough=0.75)
M_IRON = mat('emb_iron', '#33373b', rough=0.5, metal=0.4)
M_RUST = mat('emb_rust', '#6d4a33', rough=0.85, metal=0.2)
M_GLOBE = mat('emb_globe', '#f4edd9', rough=0.3)


def finish(bm, name, material, uv_scale):
    """bmesh -> object, with box-projected world-scale UVs and flat shading."""
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        # project along the dominant axis, in metres, so tiles are real-size
        u_i, v_i = (1, 2) if ax == 0 else (0, 2) if ax == 1 else (0, 1)
        for loop in f.loops:
            co = loop.vert.co
            loop[uv].uv = (co[u_i] / uv_scale, co[v_i] / uv_scale)
    mesh = D.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.polygons.foreach_set('use_smooth', [False] * len(mesh.polygons))
    mesh.update()
    ob = D.objects.new(name, mesh)
    ob.data.materials.append(material)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def sweep(bm, profile, x0, x1):
    """Extrude a closed (y, z) profile along X. profile is CCW seen from +X."""
    a = [bm.verts.new((x0, y, z)) for y, z in profile]
    b = [bm.verts.new((x1, y, z)) for y, z in profile]
    n = len(profile)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((a[i], a[j], b[j], b[i]))
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)


def cube(bm, w, d, h, x=0.0, y=0.0, z=0.0):
    """w along X, d along Y, h along Z; (x, y, z) is the CENTRE."""
    hx, hy, hz = w / 2, d / 2, h / 2
    v = [bm.verts.new((x + sx * hx, y + sy * hy, z + sz * hz))
         for sx, sy, sz in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                            (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1))]
    for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
              (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
        bm.faces.new([v[i] for i in q])


def lathe(bm, rings, sides, x=0.0, y=0.0, z=0.0):
    """Revolve a (radius, height) profile about Z. rings run bottom to top."""
    loops = []
    for r, h in rings:
        ring = []
        for s in range(sides):
            a = 2 * math.pi * s / sides
            ring.append(bm.verts.new((x + math.cos(a) * r, y + math.sin(a) * r, z + h)))
        loops.append(ring)
    for k in range(len(loops) - 1):
        lo, hi = loops[k], loops[k + 1]
        for s in range(sides):
            t = (s + 1) % sides
            bm.faces.new((lo[s], lo[t], hi[t], hi[s]))
    bm.faces.new(list(reversed(loops[0])))
    bm.faces.new(loops[-1])


def tube(bm, r, h, sides=8, x=0.0, y=0.0, z=0.0, axis='Z'):
    rings = [(r, 0.0), (r, h)]
    if axis == 'Z':
        lathe(bm, rings, sides, x=x, y=y, z=z)
        return
    # a short horizontal pipe (the weep), swept along -Y
    loops = []
    for off in (0.0, h):
        ring = []
        for s in range(sides):
            a = 2 * math.pi * s / sides
            ring.append(bm.verts.new((x + math.cos(a) * r, y - off, z + math.sin(a) * r)))
        loops.append(ring)
    for s in range(sides):
        t = (s + 1) % sides
        bm.faces.new((loops[0][s], loops[0][t], loops[1][t], loops[1][s]))
    bm.faces.new(list(reversed(loops[0])))
    bm.faces.new(loops[1])


# ---- quay: the wall itself -------------------------------------------------
# Profile in (y, z), origin at the walkway on top of the wall. The river is
# -Y, so the face runs down and slightly back toward +Y as it descends.
def wall_profile():
    toe = -WALL_H
    face_top = -0.80          # where the face meets the underside of the coping
    face_bot = face_top + BATTER   # the batter: the toe sits BACK of the top
    return [
        (0.62, 0.0),                       # back of the coping, at walkway level
        (0.62, -WALL_H - 0.25),            # back face straight down into the bank
        (face_bot + 0.30, toe - 0.25),     # toe, thickened
        (face_bot, toe),
        (face_bot - REVEAL_D, toe + 0.02),
        # two construction-joint reveals up the face
        (face_top + BATTER * 0.66, -2.80),
        (face_top + BATTER * 0.66 - REVEAL_D, -2.74),
        (face_top + BATTER * 0.33, -1.40),
        (face_top + BATTER * 0.33 - REVEAL_D, -1.34),
        (face_top, -COPING_H),             # top of the face, under the coping
        (face_top - COPING_OUT, -COPING_H),         # coping oversail
        (face_top - COPING_OUT - 0.06, -COPING_H + 0.08),  # drip chamfer
        (face_top - COPING_OUT - 0.06, 0.0),        # coping top, river edge
    ]


def build_quay(name, with_steps):
    bm = bmesh.new()
    sweep(bm, wall_profile(), -BAY / 2, BAY / 2)
    ob_wall = finish(bm, name + '_wall', M_WALL, UV_WALL)

    # the coping is a different stone, so it is its own object joined after
    bm = bmesh.new()
    face_top = -0.80
    y_out = face_top - COPING_OUT - 0.06
    cube(bm, BAY, 0.62 - y_out, COPING_H,
         y=(0.62 + y_out) / 2, z=-COPING_H / 2 + 0.001)
    parts = [finish(bm, name + '_coping', M_STONE, UV_STONE)]

    bm = bmesh.new()
    # weep pipe and a mooring ring: the two details that say "river wall"
    tube(bm, 0.075, 0.22, 8, x=-0.85, y=face_top + BATTER * 0.4, z=-2.05, axis='Y')
    ob_weep = finish(bm, name + '_weep', M_RUST, UV_STONE)
    bm = bmesh.new()
    lathe(bm, [(0.17, 0.0), (0.17, 0.05)], 10,
          x=0.95, y=face_top + BATTER * 0.12 - 0.04, z=-0.62)
    ob_ring = finish(bm, name + '_ring', M_RUST, UV_STONE)
    parts += [ob_weep, ob_ring]

    if with_steps:
        # Filmed from the water, the stair does NOT step out into the river:
        # it runs ALONG the face, hugging the wall, from the walkway down to a
        # landing at water level. So the treads march in X, not in -Y.
        bm = bmesh.new()
        n_step = 13
        run = BAY / n_step
        for i in range(n_step):
            cube(bm, run, 1.05, 0.16,
                 x=-BAY / 2 + run * (i + 0.5),
                 y=face_top + BATTER * 0.2 - 0.55,
                 z=-0.22 - i * (WALL_H - 0.9) / n_step)
        # the landing the stair lands on, just above the water
        cube(bm, 1.5, 1.25, 0.20, x=BAY / 2 - 0.75,
             y=face_top + BATTER * 0.25 - 0.62, z=-WALL_H + 0.55)
        parts.append(finish(bm, name + '_steps', M_STONE, UV_STONE))

    return join(name, [ob_wall] + parts)


def join(name, obs):
    obs = [o for o in obs if o]
    bpy.ops.object.select_all(action='DESELECT')
    for o in obs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]
    bpy.ops.object.join()
    ob = bpy.context.active_object
    ob.name = name
    ob.data.name = name  # glTF names meshes after the DATABLOCK
    return ob


build_quay('quay', False)
build_quay('quay_step', True)

# ---- bal_stone: the balustrade bay -----------------------------------------
# plinth -> turned balusters -> moulded handrail, the arrangement in every
# photograph of the central embankment.
def build_balustrade():
    bm = bmesh.new()
    # moulded plinth: a chamfered base course the balusters stand on
    sweep(bm, [(-0.16, 0.0), (0.16, 0.0), (0.16, 0.13), (0.12, 0.19),
               (-0.12, 0.19), (-0.16, 0.13)], -BAY / 2, BAY / 2)
    # handrail: wider than the plinth, with a drip groove under the overhang
    top = RAIL_H
    sweep(bm, [(-0.17, top - 0.15), (0.17, top - 0.15), (0.17, top - 0.09),
               (0.14, top - 0.04), (0.14, top), (-0.14, top),
               (-0.14, top - 0.04), (-0.17, top - 0.09)], -BAY / 2, BAY / 2)
    ob = finish(bm, 'bal_rails', M_STONE, UV_STONE)

    bm = bmesh.new()
    # the turned profile: foot torus, waisted neck, swelling belly, cap
    prof = [(0.062, 0.19), (0.062, 0.245), (0.046, 0.30), (0.038, 0.40),
            (0.050, 0.52), (0.068, 0.64), (0.072, 0.72), (0.055, 0.80),
            (0.047, 0.84), (0.060, 0.88)]
    step = BAY / N_BALUSTER
    for i in range(N_BALUSTER):
        lathe(bm, prof, BALUSTER_SIDES, x=-BAY / 2 + step * (i + 0.5))
    ob2 = finish(bm, 'bal_spindles', M_STONE, UV_STONE)
    return join('bal_stone', [ob, ob2])


build_balustrade()

# ---- bal_pier: the square pier the balustrade dies into ---------------------
bm = bmesh.new()
cube(bm, 0.46, 0.46, 0.16, z=0.08)                 # base
cube(bm, 0.38, 0.38, RAIL_H - 0.30, z=0.16 + (RAIL_H - 0.30) / 2)  # die
cube(bm, 0.50, 0.50, 0.12, z=RAIL_H - 0.08)        # cornice cap
cube(bm, 0.34, 0.34, 0.09, z=RAIL_H + 0.02)        # low pyramid stub
finish(bm, 'bal_pier', M_STONE, UV_STONE)

# ---- quay_lamp: the embankment lamp column ---------------------------------
# The column standing at the head of the river stair in the footage is the
# five-globe candelabra, not a single lantern: one globe on the crown and four
# on scrolled arms. Cast-iron, on a stepped plinth.
bm = bmesh.new()
lathe(bm, [(0.23, 0.0), (0.23, 0.14), (0.18, 0.19), (0.18, 0.34),
           (0.135, 0.41), (0.12, 1.00), (0.10, 1.12), (0.078, 3.10),
           (0.098, 3.22), (0.075, 3.34)], 8)
for i in range(4):  # the scrolled arms
    a = i * math.pi / 2 + math.pi / 4
    dx, dy = math.cos(a), math.sin(a)
    bm_arm = None
    for k in range(3):  # three short segments approximate the scroll
        t = 0.16 + k * 0.17
        cube(bm, 0.055, 0.055, 0.055,
             x=dx * t, y=dy * t, z=3.30 + k * 0.11)
    del bm_arm
ob_col = finish(bm, 'lamp_col', M_IRON, UV_STONE)

bm = bmesh.new()
lathe(bm, [(0.16, 3.34), (0.13, 3.44)], 8)  # crown collar
ob_iron = finish(bm, 'lamp_iron', M_IRON, UV_STONE)

bm = bmesh.new()
# crown globe plus the four on the arms
lathe(bm, [(0.10, 3.44), (0.155, 3.58), (0.155, 3.70), (0.09, 3.82)], 8)
for i in range(4):
    a = i * math.pi / 2 + math.pi / 4
    dx, dy = math.cos(a) * 0.50, math.sin(a) * 0.50
    lathe(bm, [(0.085, 3.52), (0.13, 3.63), (0.13, 3.73), (0.075, 3.83)], 8,
          x=dx, y=dy)
ob_glass = finish(bm, 'lamp_glass', M_GLOBE, UV_STONE)
join('quay_lamp', [ob_col, ob_iron, ob_glass])

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
    n = len(o.data.loop_triangles)
    tris += n
    rows.append((o.name, n))
print('embankment kit exported:', OUT)
for n, t in sorted(rows):
    print(f'  {n:12s} {t:5d} tris')
print(f'  total {tris} tris')
