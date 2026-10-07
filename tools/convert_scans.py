# Convert real photogrammetry statue scans (threedscans.com, noe-3d.at CC0)
# into the game's normalized statue GLBs — the GTA-quality pass.
#   STATUE_SHOTS=… SCAN_DIR=… /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/convert_scans.py [-- names…]
# Per model: import → join → orient (per-model euler, degrees) → voxel-remesh
# only if needed → decimate to budget → recenter x/y, base z=0, height 1.0 →
# preview render → export GLB. Composites (riders, dragon, lance) assemble
# oriented pieces before normalize.
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

SCAN = os.environ.get('SCAN_DIR', '/tmp/scans')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'statues')
SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')

# rot: degrees applied X,Y,Z to bring the scan to Z-up with the statue facing
# -Y (standing figures) or +X (equestrian pieces). Filled in from previews.
SCANS = {
    'kartlis_deda': {'file': 'zenobia/Hosmer.OBJ', 'rot': (-90, 0, 180), 'tris': 40000},
    'poet': {'file': 'aule/Portrait_Statue_Of_-Aule_Meteli.obj', 'rot': (90, 0, 0), 'tris': 40000},
    'nino': {'file': 'hugh/St_Hugh.stl', 'rot': (90, 0, 180), 'tris': 40000},
    'king': {'file': 'theodoric/FullBody_Decimated.OBJ', 'rot': (180, 0, 0), 'tris': 40000},
    'street': {'file': 'cincinnatus/Lucius_Quinctius_Cincinnatus.stl', 'rot': (180, 0, 180), 'tris': 3200},
    'bust': {'file': 'wesley/johnwesley.stl', 'rot': (180, 0, 0), 'tris': 2500},
}
# scan pieces for the equestrian composites (not exported directly)
HORSE = {'file': 'rearing_horse.stl', 'rot': (0, 0, 90), 'tris': 40000}
DRAGON = {'file': 'dragon/Dragon_1.obj', 'rot': (0, 0, 180), 'tris': 9000}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def freeze(obj):
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    obj.modifiers.clear()
    obj.data = me
    return obj


def import_scan(path):
    ext = os.path.splitext(path)[1].lower()
    before = set(bpy.data.objects)
    if ext == '.stl':
        bpy.ops.wm.stl_import(filepath=path)
    elif ext == '.obj':
        bpy.ops.wm.obj_import(filepath=path)
    else:
        bpy.ops.import_scene.gltf(filepath=path)
    objs = [o for o in set(bpy.data.objects) - before if o.type == 'MESH']
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return obj


def orient(obj, rot):
    if any(rot):
        m = (Matrix.Rotation(math.radians(rot[2]), 4, 'Z')
             @ Matrix.Rotation(math.radians(rot[1]), 4, 'Y')
             @ Matrix.Rotation(math.radians(rot[0]), 4, 'X'))
        obj.data.transform(m)


def decimate(obj, tris):
    obj.data.calc_loop_triangles()
    n = len(obj.data.loop_triangles)
    if n > tris:
        d = obj.modifiers.new('dec', 'DECIMATE')
        d.ratio = tris / n
        freeze(obj)
    for p in obj.data.polygons:
        p.use_smooth = True


def bbox(obj):
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    return (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))


def normalize(obj):
    (x0, x1), (y0, y1), (z0, z1) = bbox(obj)
    h = z1 - z0
    s = 1.0 / h
    obj.data.transform(Matrix.Translation((-(x0 + x1) / 2, -(y0 + y1) / 2, -z0)))
    obj.data.transform(Matrix.Diagonal((s, s, s, 1)))
    return h


def shoot(obj, name, tag):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x = 560
    sc.render.resolution_y = 640
    (x0, x1), (y0, y1), (z0, z1) = bbox(obj)
    c = Vector(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))
    dim = max(x1 - x0, y1 - y0, z1 - z0)
    for label, d in (('front', Vector((0, -1, 0.12))), ('side', Vector((1, 0, 0.12))), ('profile', Vector((0.15, 1, 0.1)))):
        cam = bpy.data.objects.new('cam' + label, bpy.data.cameras.new('c'))
        link(cam)
        sc.camera = cam
        cam.location = c + d.normalized() * dim * 2.0
        tgt = bpy.data.objects.new('t' + label, None)
        link(tgt)
        tgt.location = c
        tr = cam.constraints.new('TRACK_TO')
        tr.target = tgt
        tr.track_axis = 'TRACK_NEGATIVE_Z'
        tr.up_axis = 'UP_Y'
        sc.render.filepath = os.path.join(SHOTS, f'scan_{name}_{tag}{label}.png')
        bpy.ops.render.render(write_still=True)


def export(name):
    fp = os.path.join(OUT, f'{name}.glb')
    bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB')
    print(f'EXPORT {fp}')


def prep(cfg):
    obj = import_scan(os.path.join(SCAN, cfg['file']))
    orient(obj, cfg['rot'])
    decimate(obj, cfg['tris'])
    return obj


# Composite tuning (normalized horse space, iterated via previews):
BASE_CUT = 0.08   # slice off the scan's base slab
PITCH = -12       # pitch the lunging horse up into a rearing attitude
# rider: scale, lean-back degrees, saddle x, saddle z
RIDER = (0.36, -15, -0.19, 0.71)


def composite(name, with_dragon):
    """Scanned rearing horse + sculpted rider (+ scanned dragon and lance)."""
    reset()
    sys.path.append(os.path.dirname(os.path.abspath(__file__)))
    import build_statues as bs
    horse = prep(HORSE)
    normalize(horse)
    # slice off the base slab, pitch up, re-ground on the hind hooves
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, BASE_CUT - 0.5))
    cutter = bpy.context.object
    cutter.scale = (4, 4, 1)
    bpy.ops.object.transform_apply(location=True, scale=True)
    cut = horse.modifiers.new('cut', 'BOOLEAN')
    cut.operation = 'DIFFERENCE'
    cut.object = cutter
    freeze(horse)
    bpy.data.objects.remove(cutter, do_unlink=True)
    horse.data.transform(Matrix.Rotation(math.radians(PITCH), 4, 'Y'))
    zs = [v.co.z for v in horse.data.vertices]
    horse.data.transform(Matrix.Translation((0, 0, -min(zs))))
    rider = bs.organic(bs.build_rider(rearing=False, raised_arm=not with_dragon), voxel=0.03, tris=7000)
    rs, rp, rx, rz = RIDER
    rider.data.transform(Matrix.Translation((-0.05, 0, -1.78)))
    rider.data.transform(Matrix.Diagonal((rs, rs, rs, 1)))
    rider.data.transform(Matrix.Rotation(math.radians(rp), 4, 'Y'))
    rider.data.transform(Matrix.Translation((rx, 0, rz)))
    parts = [horse, rider]
    if with_dragon:
        dragon = prep(DRAGON)
        (dx0, dx1), (dy0, dy1), (dz0, dz1) = bbox(dragon)
        ds = 0.22 / (dz1 - dz0)
        dragon.data.transform(Matrix.Translation((-(dx0 + dx1) / 2, -(dy0 + dy1) / 2, -dz0)))
        dragon.data.transform(Matrix.Diagonal((ds, ds, ds, 1)))
        dragon.data.transform(Matrix.Translation((0.42, 0.06, 0.02)))
        parts.append(dragon)
        # lance from the rider's lowered right hand into the dragon
        hand = Vector((0.3 - 0.05, 0.4, 2.2 - 1.78)) * rs
        hand.rotate(Matrix.Rotation(math.radians(rp), 3, 'Y'))
        hand += Vector((rx, 0, rz))
        tip = Vector((0.44, 0.06, 0.1))
        d = tip - hand
        bpy.ops.object.select_all(action='DESELECT')
        bpy.ops.mesh.primitive_cone_add(vertices=8, radius1=0.006, radius2=0.014,
                                        depth=d.length + 0.15, location=hand + d / 2)
        lance = bpy.context.object
        lance.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
        bpy.ops.object.transform_apply(location=True, rotation=True)
        for p in lance.data.polygons:
            p.use_smooth = True
        parts.append(lance)
    obj = join(parts)
    normalize(obj)
    shoot(obj, name, '')
    if os.environ.get('EXPORT') == '1':
        export(name)


def join(parts):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    return bpy.context.object


only = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
for name, cfg in SCANS.items():
    if only and name not in only:
        continue
    reset()
    obj = prep(cfg)
    (x0, x1), (y0, y1), (z0, z1) = bbox(obj)
    print(f'{name}: dims x={x1-x0:.2f} y={y1-y0:.2f} z={z1-z0:.2f} (pre-normalize)')
    normalize(obj)
    shoot(obj, name, '')
    if os.environ.get('EXPORT') == '1':
        export(name)
if not only or 'equestrian' in only:
    composite('equestrian', False)
if not only or 'george' in only:
    composite('george', True)
print('DONE')
