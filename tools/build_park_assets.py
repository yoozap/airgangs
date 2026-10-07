# Park asset kit sculpted in Blender for the real-parks wave:
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_park_assets.py
# Multi-material assets keep named parts (stone/water/foliage/…) as separate
# meshes in the GLB; src/parks.js assigns materials by node name. All assets
# exported at real meter scale, origin at ground center (NO height normalize —
# unlike statues these are true-size props).
import math
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Matrix, Vector  # noqa: E402

import build_statues as bs  # noqa: E402  (reset/link/freeze/skeleton/ellipsoid/cyl/cube/organic/join)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'parks')
SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')
os.makedirs(OUT, exist_ok=True)


def name_obj(obj, name):
    obj.name = name
    return obj


def export(name):
    fp = os.path.join(OUT, f'{name}.glb')
    bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB')
    print(f'EXPORT {fp}')


def shoot(name, dist=2.2, up=0.4):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x = 560
    sc.render.resolution_y = 560
    xs, ys, zs = [], [], []
    for o in bpy.data.objects:
        if o.type == 'MESH':
            for v in o.data.vertices:
                w = o.matrix_world @ v.co
                xs.append(w.x)
                ys.append(w.y)
                zs.append(w.z)
    c = Vector(((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, (max(zs) + min(zs)) / 2))
    dim = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
    cam = bs.link(bpy.data.objects.new('cam', bpy.data.cameras.new('c')))
    sc.camera = cam
    cam.location = c + Vector((1.1, -1.5, up)).normalized() * dim * dist
    tgt = bs.link(bpy.data.objects.new('t', None))
    tgt.location = c
    tr = cam.constraints.new('TRACK_TO')
    tr.target = tgt
    tr.track_axis = 'TRACK_NEGATIVE_Z'
    tr.up_axis = 'UP_Y'
    sc.render.filepath = os.path.join(SHOTS, f'asset_{name}.png')
    bpy.ops.render.render(write_still=True)


def lathe(name, profile, seg=28):
    """Solid of revolution from an (r, z) profile polyline."""
    me = bpy.data.meshes.new(name)
    verts, faces = [], []
    n = len(profile)
    for s in range(seg):
        a = s / seg * math.pi * 2
        ca, sa = math.cos(a), math.sin(a)
        for r, z in profile:
            verts.append((r * ca, r * sa, z))
    for s in range(seg):
        s2 = (s + 1) % seg
        for i in range(n - 1):
            faces.append((s * n + i, s2 * n + i, s2 * n + i + 1, s * n + i + 1))
    me.from_pydata(verts, [], faces)
    obj = bs.link(bpy.data.objects.new(name, me))
    for p in me.polygons:
        p.use_smooth = True
    return obj


# --- assets -----------------------------------------------------------------


def fountain():
    """Vake-style two-tier stone fountain, Ø7 m, with water discs."""
    bs.reset()
    stone = [
        lathe('stone_basin', [(3.5, 0), (3.5, 0.75), (3.2, 0.8), (3.1, 0.35), (0.5, 0.3)]),
        lathe('stone_ped', [(0.5, 0.3), (0.32, 1.5), (0.55, 1.7)]),
        lathe('stone_bowl', [(0.1, 1.7), (1.5, 1.85), (1.65, 2.1), (1.45, 2.15), (0.2, 2.0)]),
        lathe('stone_top', [(0.12, 2.1), (0.1, 2.6), (0.3, 2.75), (0.05, 2.9)]),
    ]
    st = bs.join(stone)
    name_obj(st, 'stone')
    w1 = lathe('w1', [(0.02, 2.05), (1.35, 2.12)])
    w2 = lathe('w2', [(0.02, 0.36), (3.0, 0.42)])
    wa = bs.join([w1, w2])
    name_obj(wa, 'water')
    shoot('fountain')
    export('fountain')


def bench():
    """Slatted park bench, 1.9 m, cast-iron legs."""
    bs.reset()
    slats = []
    for i in range(4):
        slats.append(bs.cube(f's{i}', (1.9, 0.11, 0.035), (0, -0.24 + i * 0.13, 0.44)))
    for i in range(3):
        slats.append(bs.cube(f'b{i}', (1.9, 0.035, 0.12), (0, 0.3, 0.55 + i * 0.14)))
    wood = bs.join(slats)
    name_obj(wood, 'wood')
    legs = []
    for s in (-1, 1):
        legs.append(bs.cube(f'l{s}', (0.06, 0.55, 0.44), (s * 0.85, 0, 0.22)))
        legs.append(bs.cube(f'k{s}', (0.06, 0.08, 0.5), (s * 0.85, 0.3, 0.55)))
    iron = bs.join(legs)
    name_obj(iron, 'iron')
    shoot('bench')
    export('bench')


def parklamp():
    """Five-globe candelabra lamp, 4.2 m, old-Tbilisi style."""
    bs.reset()
    pole = [bs.cyl('p', 0.09, 0.05, 3.4, (0, 0, 1.7), verts=10)]
    pole.append(bs.cyl('base', 0.22, 0.12, 0.5, (0, 0, 0.25), verts=10))
    for i in range(4):
        a = i / 4 * math.pi * 2
        arm = bs.cyl('a', 0.025, 0.025, 0.7, (math.cos(a) * 0.3, math.sin(a) * 0.3, 3.55),
                     rot=(0, math.pi / 2 - 0.5, a), verts=6)
        pole.append(arm)
    ir = bs.join(pole)
    bs.smooth(ir)
    name_obj(ir, 'iron')
    globes = []
    for i in range(4):
        a = i / 4 * math.pi * 2
        globes.append(bs.ellipsoid('g', (math.cos(a) * 0.55, math.sin(a) * 0.55, 3.85), (0.16, 0.16, 0.2), seg=12))
    globes.append(bs.ellipsoid('g4', (0, 0, 4.05), (0.19, 0.19, 0.24), seg=12))
    gl = bs.join(globes)
    name_obj(gl, 'globes')
    shoot('parklamp')
    export('parklamp')


def flowerbed():
    """Round stone-edged bed, Ø3 m, mounded soil with blossom clumps."""
    bs.reset()
    ring = lathe('ring', [(1.5, 0), (1.5, 0.3), (1.35, 0.32), (1.35, 0.12)])
    name_obj(ring, 'stone')
    soil = lathe('soil', [(1.35, 0.1), (0.9, 0.32), (0.0, 0.42)])
    name_obj(soil, 'soil')
    blooms = []
    for i in range(24):
        a = i * 2.4
        r = 0.25 + (i % 5) * 0.22
        blooms.append(bs.ellipsoid('bl', (math.cos(a) * r, math.sin(a) * r, 0.42 - r * 0.12),
                                   (0.14, 0.14, 0.1), seg=8))
    bl = bs.join(blooms)
    name_obj(bl, 'bloom')
    shoot('flowerbed')
    export('flowerbed')


def alleytree():
    """Plane tree for park alleys: flared trunk, layered 8 m canopy."""
    bs.reset()
    trunk = bs.organic([
        bs.cyl('t', 0.32, 0.2, 3.2, (0, 0, 1.6), verts=8),
        bs.cyl('f1', 0.5, 0.3, 0.5, (0, 0, 0.25), verts=8),
        bs.skeleton('br1', [(0, 0, 2.8), (0.7, 0.3, 3.8)], [0.14, 0.08], sub=1),
        bs.skeleton('br2', [(0, 0, 3.0), (-0.6, 0.4, 4.0)], [0.13, 0.07], sub=1),
    ], voxel=0.09, tris=420)
    name_obj(trunk, 'trunk')
    lobes = []
    for i in range(9):
        a = i * 2.4
        r = 0.4 + (i % 3) * 0.75
        z = 4.6 + (i % 4) * 0.75
        s = 1.5 - (i % 3) * 0.25
        lobes.append(bs.ellipsoid('lo', (math.cos(a) * r, math.sin(a) * r, z), (s * 1.25, s * 1.25, s), seg=10))
    can = bs.organic(lobes, voxel=0.22, tris=700)
    name_obj(can, 'canopy')
    shoot('alleytree')
    export('alleytree')


def carousel():
    """Fairground carousel: platform, scalloped tent roof, horses. Ø7 m."""
    bs.reset()
    plat = [lathe('pl', [(3.5, 0.12), (3.5, 0.45), (0.2, 0.5)])]
    plat.append(bs.cyl('col', 0.22, 0.18, 3.4, (0, 0, 2.1), verts=12))
    deck = bs.join(plat)
    name_obj(deck, 'deck')
    roof = lathe('roofc', [(3.7, 3.4), (0.05, 5.0)])
    mod = roof.modifiers.new('d', 'DISPLACE')
    tex = bpy.data.textures.new('scallop', 'WOOD')
    tex.noise_scale = 0.4
    mod.texture = tex
    mod.strength = 0.12
    bs.freeze(roof)
    bs.smooth(roof)
    name_obj(roof, 'roof')
    posts = []
    horses = []
    for i in range(8):
        a = i / 8 * math.pi * 2
        x, y = math.cos(a) * 2.6, math.sin(a) * 2.6
        posts.append(bs.cyl(f'po{i}', 0.04, 0.04, 3.0, (x, y, 1.95), verts=6))
        horses.append(bs.ellipsoid(f'hb{i}', (x, y, 1.35), (0.42, 0.16, 0.2)))
        horses.append(bs.skeleton(f'hn{i}', [(x + math.cos(a + 1.57) * 0.3, y + math.sin(a + 1.57) * 0.3, 1.45),
                                             (x + math.cos(a + 1.57) * 0.55, y + math.sin(a + 1.57) * 0.55, 1.75)],
                                  [0.09, 0.06]))
    ps = bs.join(posts)
    name_obj(ps, 'deck2')
    hs = bs.join(horses)
    name_obj(hs, 'horses')
    shoot('carousel')
    export('carousel')


def playset():
    """Courtyard playground: swing frame + slide + spring rider."""
    bs.reset()
    metal = []
    for s in (-1, 1):
        metal.append(bs.cyl('a1', 0.05, 0.05, 2.6, (s * 1.5, 0.5, 1.15), rot=(0.35, 0, 0), verts=8))
        metal.append(bs.cyl('a2', 0.05, 0.05, 2.6, (s * 1.5, -0.5, 1.15), rot=(-0.35, 0, 0), verts=8))
    metal.append(bs.cyl('top', 0.05, 0.05, 3.2, (0, 0, 2.3), rot=(0, math.pi / 2, 0), verts=8))
    # slide: ladder + sloped chute
    metal.append(bs.cube('lad', (0.5, 0.08, 1.8), (3.2, 0.9, 0.9)))
    fr = bs.join(metal)
    bs.smooth(fr)
    name_obj(fr, 'metal')
    seats = [bs.cube('s1', (0.45, 0.22, 0.05), (-0.8, 0, 0.55)), bs.cube('s2', (0.45, 0.22, 0.05), (0.8, 0, 0.55))]
    chute = bs.cube('ch', (0.55, 2.2, 0.06), (3.2, -0.35, 1.0), rot=(0.6, 0, 0))
    seats.append(chute)
    seats.append(bs.ellipsoid('spring', (1.9, -1.3, 0.6), (0.35, 0.12, 0.3)))
    seats.append(bs.cyl('spc', 0.06, 0.06, 0.5, (1.9, -1.3, 0.3), verts=8))
    se = bs.join(seats)
    name_obj(se, 'color')
    shoot('playset')
    export('playset')


def elephant():
    """Zoo elephant, ~3.2 m at the head."""
    bs.reset()
    parts = [bs.ellipsoid('body', (0, 0, 1.85), (1.5, 0.95, 1.05))]
    parts.append(bs.ellipsoid('head', (1.7, 0, 2.2), (0.62, 0.55, 0.62)))
    parts.append(bs.skeleton('trunk', [(2.25, 0, 2.0), (2.7, 0.1, 1.3), (2.75, -0.1, 0.55), (2.6, 0, 0.2)],
                             [0.19, 0.14, 0.1, 0.08]))
    for s in (-1, 1):
        parts.append(bs.ellipsoid('ear', (1.55, s * 0.62, 2.35), (0.35, 0.1, 0.55), rot=(0, 0, s * 0.35)))
        parts.append(bs.cyl('fl', 0.24, 0.3, 1.15, (0.85, s * 0.5, 0.58), verts=10))
        parts.append(bs.cyl('hl', 0.26, 0.32, 1.15, (-0.85, s * 0.52, 0.58), verts=10))
    parts.append(bs.skeleton('tail', [(-1.45, 0, 2.1), (-1.75, 0, 1.3)], [0.07, 0.03]))
    body = bs.organic(parts, voxel=0.06, tris=6000)
    name_obj(body, 'hide')
    tusks = []
    for s in (-1, 1):
        tu = bs.cyl('tu', 0.02, 0.055, 0.75, (2.2, s * 0.22, 1.55), rot=(s * 0.25, -0.9, 0), verts=8)
        bs.smooth(tu)
        tusks.append(tu)
    t = bs.join(tusks)
    name_obj(t, 'tusk')
    shoot('elephant')
    export('elephant')


def giraffe():
    """Zoo giraffe, ~4.6 m."""
    bs.reset()
    parts = [bs.ellipsoid('body', (0, 0, 2.35), (1.05, 0.55, 0.68))]
    parts.append(bs.skeleton('neck', [(0.85, 0, 2.85), (1.55, 0, 4.15), (1.75, 0, 4.5)], [0.3, 0.19, 0.16]))
    parts.append(bs.ellipsoid('head', (1.98, 0, 4.62), (0.34, 0.16, 0.18), rot=(0, 0.5, 0)))
    for s in (-1, 1):
        parts.append(bs.cyl('oss', 0.025, 0.045, 0.28, (1.85, s * 0.09, 4.85), verts=6))
        parts.append(bs.skeleton('fl', [(0.65, s * 0.3, 2.2), (0.72, s * 0.28, 1.1), (0.7, s * 0.27, 0.1)],
                                 [0.16, 0.1, 0.07]))
        parts.append(bs.skeleton('hl', [(-0.65, s * 0.32, 2.2), (-0.75, s * 0.3, 1.1), (-0.72, s * 0.28, 0.1)],
                                 [0.18, 0.1, 0.07]))
    parts.append(bs.skeleton('tail', [(-1.0, 0, 2.5), (-1.3, 0, 1.6)], [0.06, 0.025]))
    body = bs.organic(parts, voxel=0.06, tris=6000)
    name_obj(body, 'hide')
    shoot('giraffe')
    export('giraffe')


ONLY = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
for fn in (fountain, bench, parklamp, flowerbed, alleytree, carousel, playset, elephant, giraffe):
    if ONLY and fn.__name__ not in ONLY:
        continue
    fn()
print('ASSETS DONE')
