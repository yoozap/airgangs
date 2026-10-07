# Sculpt the Tbilisi statue figures in Blender and export GLBs for the game.
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_statues.py
# Each figure: organic parts (skin-modifier skeletons + ellipsoids, displaced
# robes) → voxel remesh union → smooth shade → decimate, then crisp props
# (swords, lance, cross, book) joined un-remeshed. Output normalized to
# height=1.0 with the base at z=0, so the game just sets scale = real height.
# Facing: standing figures face Blender -Y (→ three.js +z), equestrians +X.
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'statues')
SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')
os.makedirs(OUT, exist_ok=True)

# --- low-level helpers ------------------------------------------------------


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def freeze(obj):
    """Apply the whole modifier stack destructively."""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    obj.modifiers.clear()
    obj.data = me
    return obj


def skeleton(name, pts, radii, edges=None, sub=2):
    """Skin-modifier tube skeleton: pts [(x,y,z)...], radii per point."""
    me = bpy.data.meshes.new(name)
    if edges is None:
        edges = [(i, i + 1) for i in range(len(pts) - 1)]
    me.from_pydata([Vector(p) for p in pts], edges, [])
    obj = link(bpy.data.objects.new(name, me))
    obj.modifiers.new('skin', 'SKIN')
    s = obj.modifiers.new('sub', 'SUBSURF')
    s.levels = sub
    for i, r in enumerate(radii):
        sv = me.skin_vertices[0].data[i]
        sv.radius = (r, r)
    me.skin_vertices[0].data[0].use_root = True
    return freeze(obj)


def ellipsoid(name, loc, scale, rot=(0, 0, 0), seg=24):
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=seg // 2, radius=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, scale=True, rotation=True)
    return o


def cyl(name, r1, r2, depth, loc, rot=(0, 0, 0), verts=20):
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r1, radius2=r2, depth=depth, location=loc)
    o = bpy.context.object
    o.name = name
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, rotation=True)
    return o


def cube(name, dims, loc, rot=(0, 0, 0)):
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = dims
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, scale=True, rotation=True)
    return o


def robe(name, r_hem, r_waist, h, loc, folds=7):
    """Draped skirt/coat: clean A-line cone + vertical drapery ridges that the
    voxel remesh melts into classical statue folds. Returns a parts list."""
    parts = [cyl(name, r_hem, r_waist, h, (loc[0], loc[1], loc[2] + h / 2), verts=28)]
    smooth(parts[0])
    tilt = math.atan2(r_hem - r_waist, h)
    for i in range(folds):
        a = (i / folds) * math.pi * 2 + 0.4
        zc = h * 0.38
        rc = (r_hem + (r_waist - r_hem) * (zc / h)) * 0.97
        parts.append(ellipsoid(f'{name}_f{i}', (loc[0] + math.cos(a) * rc, loc[1] + math.sin(a) * rc, loc[2] + zc),
                               (0.045, 0.045, h * 0.4), rot=(0, -tilt, a), seg=12))
    return parts


def join(parts):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    return bpy.context.object


def organic(parts, voxel=0.035, tris=14000):
    """Union organic parts into one smooth statue mesh."""
    o = join(parts)
    rm = o.modifiers.new('rm', 'REMESH')
    rm.mode = 'VOXEL'
    rm.voxel_size = voxel
    freeze(o)
    o.data.calc_loop_triangles()
    n = len(o.data.loop_triangles)
    if n > tris:
        dec = o.modifiers.new('dec', 'DECIMATE')
        dec.ratio = tris / n
        freeze(o)
    for p in o.data.polygons:
        p.use_smooth = True
    return o


def smooth(o):
    for p in o.data.polygons:
        p.use_smooth = True
    return o


def normalize(obj):
    """Scale to height 1.0, base plane to z=0; keep x/y as designed."""
    me = obj.data
    zs = [v.co.z for v in me.vertices]
    h = max(zs) - min(zs)
    s = 1.0 / h
    me.transform(Matrix.Translation((0, 0, -min(zs))) )
    me.transform(Matrix.Diagonal((s, s, s, 1)))
    return h


def shoot(name):
    """Workbench preview render, camera framed on the active object."""
    obj = bpy.context.object
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x = 640
    sc.render.resolution_y = 780
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    c = Vector(((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, (max(zs) + min(zs)) / 2))
    dim = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    link(cam)
    sc.camera = cam
    cam.location = c + Vector((1.1, -1.6, 0.45)).normalized() * dim * 2.1
    tgt = bpy.data.objects.new('tgt', None)
    link(tgt)
    tgt.location = c
    tr = cam.constraints.new('TRACK_TO')
    tr.target = tgt
    tr.track_axis = 'TRACK_NEGATIVE_Z'
    tr.up_axis = 'UP_Y'
    sc.render.filepath = os.path.join(SHOTS, f'statue_{name}.png')
    bpy.ops.render.render(write_still=True)


def export(name, preview=True):
    obj = bpy.context.object
    h = normalize(obj)
    if preview:
        shoot(name)
    fp = os.path.join(OUT, f'{name}.glb')
    bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB')
    obj.data.calc_loop_triangles()
    print(f'EXPORT {name}: natural height {h:.2f} m, {len(obj.data.loop_triangles)} tris → {fp}')


# --- figure builders --------------------------------------------------------


def build_horse(rearing=False):
    """Standing or rearing horse, facing +X, ~1.6 m withers. Returns parts."""
    parts = []
    parts.append(ellipsoid('barrel', (0, 0, 1.18), (1.05, 0.37, 0.47)))
    parts.append(ellipsoid('chest', (0.72, 0, 1.12), (0.4, 0.34, 0.46)))
    parts.append(ellipsoid('rump', (-0.78, 0, 1.22), (0.48, 0.36, 0.44)))
    # neck + head as one tube chain, mane ridge on top
    parts.append(skeleton('neck', [(0.85, 0, 1.45), (1.35, 0, 1.95), (1.62, 0, 2.12), (2.0, 0, 1.96)],
                          [0.24, 0.17, 0.14, 0.075]))
    parts.append(ellipsoid('cheek', (1.62, 0, 2.06), (0.16, 0.11, 0.13)))
    parts.append(ellipsoid('mane', (1.15, 0, 1.92), (0.3, 0.05, 0.17), rot=(0, math.radians(-42), 0)))
    for s in (-1, 1):
        parts.append(ellipsoid('ear', (1.52, s * 0.07, 2.26), (0.05, 0.03, 0.09), rot=(s * 0.25, 0.25, 0)))
    # legs
    for s in (-1, 1):
        if rearing:
            parts.append(skeleton('foreleg', [(0.72, s * 0.24, 1.0), (0.95, s * 0.23, 0.72), (0.72, s * 0.22, 0.6), (0.8, s * 0.22, 0.44)],
                                  [0.13, 0.09, 0.06, 0.07]))
        else:
            parts.append(skeleton('foreleg', [(0.72, s * 0.24, 1.0), (0.76, s * 0.22, 0.55), (0.73, s * 0.21, 0.12), (0.78, s * 0.21, 0.03)],
                                  [0.13, 0.085, 0.06, 0.075]))
        parts.append(skeleton('hindleg', [(-0.78, s * 0.26, 1.05), (-0.95, s * 0.24, 0.58), (-0.85, s * 0.22, 0.12), (-0.79, s * 0.22, 0.03)],
                              [0.16, 0.09, 0.06, 0.075]))
    parts.append(skeleton('tail', [(-1.15, 0, 1.45), (-1.35, 0, 0.95), (-1.38, 0, 0.5)], [0.1, 0.07, 0.04]))
    if rearing:
        # pitch the whole horse up around the hind hooves
        piv = Vector((-0.79, 0, 0.03))
        rot = Matrix.Rotation(math.radians(-30), 4, 'Y')
        for o in parts:
            o.data.transform(Matrix.Translation(piv) @ rot @ Matrix.Translation(-piv))
    return parts


def build_rider(rearing=False, raised_arm=True):
    """Armored rider seated at the saddle (origin: horse frame). Parts list."""
    parts = []
    lean = 0.12 if rearing else 0.0
    bx = 0.05
    parts.append(ellipsoid('pelvis', (bx, 0, 1.76), (0.21, 0.25, 0.15)))
    parts.append(skeleton('torso', [(bx, 0, 1.8), (bx + lean, 0, 2.18), (bx + lean * 2, 0, 2.42)], [0.17, 0.2, 0.17]))
    parts.append(ellipsoid('shoulders', (bx + lean * 2, 0, 2.36), (0.14, 0.27, 0.12)))
    parts.append(ellipsoid('neck', (bx + lean * 2, 0, 2.48), (0.07, 0.07, 0.12)))
    parts.append(ellipsoid('head', (bx + lean * 2, 0, 2.62), (0.125, 0.125, 0.14)))
    parts.append(ellipsoid('helm', (bx + lean * 2, 0, 2.7), (0.135, 0.135, 0.11)))
    parts.append(cyl('helmtip', 0.05, 0.0, 0.15, (bx + lean * 2, 0, 2.84)))
    # cloak falling behind
    parts.append(ellipsoid('cloak', (bx - 0.28, 0, 1.95), (0.16, 0.3, 0.42), rot=(0, math.radians(12), 0)))
    for s in (-1, 1):
        # thigh along the saddle, calf hanging at the horse's side
        parts.append(skeleton('leg', [(bx, s * 0.2, 1.76), (bx + 0.32, s * 0.35, 1.48), (bx + 0.28, s * 0.37, 1.05)],
                              [0.105, 0.085, 0.055]))
    if raised_arm:
        parts.append(skeleton('armR', [(bx + lean * 2, 0.12, 2.36), (bx + 0.12, 0.38, 2.56), (bx + 0.3, 0.44, 2.82)], [0.08, 0.065, 0.055]))
    else:  # lance arm, angled forward-down
        parts.append(skeleton('armR', [(bx + lean * 2, 0.12, 2.36), (bx + 0.24, 0.36, 2.22), (bx + 0.5, 0.32, 2.1)], [0.08, 0.065, 0.055]))
    parts.append(skeleton('armL', [(bx + lean * 2, -0.12, 2.36), (bx + 0.28, -0.32, 2.1), (bx + 0.5, -0.24, 2.0)], [0.08, 0.065, 0.055]))
    if rearing:
        piv = Vector((-0.79, 0, 0.03))
        rot = Matrix.Rotation(math.radians(-30), 4, 'Y')
        for o in parts:
            o.data.transform(Matrix.Translation(piv) @ rot @ Matrix.Translation(-piv))
    return parts


def equestrian():
    """Standing equestrian with raised right arm (Gorgasali/David/Saakadze)."""
    reset()
    organic(build_horse() + build_rider(), tris=16000)
    export('equestrian')


def george():
    """St George on the rearing horse, lance into the dragon below."""
    reset()
    parts = build_horse(rearing=True) + build_rider(rearing=True, raised_arm=False)
    # dragon coiled on the ground under the raised forehand
    parts.append(skeleton('dragon', [(-0.2, 0.3, 0.12), (0.2, 0.05, 0.2), (0.6, -0.2, 0.16), (0.95, 0, 0.35), (1.05, 0.08, 0.52)],
                          [0.13, 0.17, 0.15, 0.1, 0.08]))
    for s in (-1, 1):
        parts.append(ellipsoid('wing', (0.35, s * 0.35, 0.35), (0.28, 0.15, 0.06), rot=(s * 0.6, 0, s * 0.4)))
    body = organic(parts, tris=16000)
    # lance from the (rotated) right hand down into the dragon's neck
    piv = Vector((-0.79, 0, 0.03))
    rot = Matrix.Rotation(math.radians(-30), 4, 'Y')
    hand = (Matrix.Translation(piv) @ rot @ Matrix.Translation(-piv)) @ Vector((0.55, 0.32, 2.1))
    tip = Vector((0.9, 0, 0.5))
    d = tip - hand
    eul = d.to_track_quat('-Z', 'Y').to_euler()
    lance = cyl('lance', 0.012, 0.026, d.length + 0.8, hand + d / 2, rot=(eul.x, eul.y, eul.z), verts=8)
    smooth(lance)
    join([body, lance])
    export('george')


def kartlis_deda():
    """Mother of Georgia: long gown, sword flat in the right hand, bowl in left."""
    reset()
    parts = robe('gown', 0.46, 0.27, 1.06, (0, 0, 0))
    parts.append(skeleton('torso', [(0, 0, 1.02), (0, 0, 1.28), (0, 0, 1.48)], [0.26, 0.21, 0.16]))
    parts.append(ellipsoid('chest', (0, -0.03, 1.3), (0.21, 0.16, 0.17)))
    parts.append(ellipsoid('shoulders', (0, 0, 1.47), (0.25, 0.15, 0.1)))
    parts.append(ellipsoid('neckp', (0, 0, 1.56), (0.07, 0.07, 0.1)))
    parts.append(ellipsoid('head', (0, 0, 1.66), (0.11, 0.12, 0.135)))
    parts.append(ellipsoid('scarf', (0, 0.035, 1.7), (0.125, 0.14, 0.12)))
    parts.append(ellipsoid('headdress', (0, 0.01, 1.8), (0.14, 0.15, 0.055)))  # the flat crown-cap
    parts.append(skeleton('veil', [(0, 0.1, 1.62), (0, 0.14, 1.4)], [0.1, 0.13]))
    # both forearms level, offered forward
    parts.append(skeleton('armR', [(0.14, 0, 1.46), (0.36, -0.16, 1.24), (0.3, -0.36, 1.27)], [0.08, 0.06, 0.055]))
    parts.append(skeleton('armL', [(-0.14, 0, 1.46), (-0.36, -0.16, 1.24), (-0.3, -0.36, 1.27)], [0.08, 0.06, 0.055]))
    body = organic(parts, voxel=0.028, tris=14000)
    # sword held horizontally (blade across +X), bowl on the left palm
    blade = cube('blade', (0.68, 0.02, 0.05), (0.66, -0.36, 1.28))
    guard = cube('guard', (0.025, 0.035, 0.13), (0.34, -0.36, 1.28))
    bowl = cyl('bowl', 0.09, 0.13, 0.08, (-0.3, -0.37, 1.31), verts=16)
    smooth(bowl)
    join([body, blade, guard, bowl])
    export('kartlis_deda')


def poet():
    """Standing figure in a long coat, book at the chest (Rustaveli/writers)."""
    reset()
    parts = robe('coat', 0.32, 0.24, 1.12, (0, 0, 0), folds=5)
    parts.append(skeleton('torso', [(0, 0, 1.08), (0, 0, 1.32), (0, 0, 1.5)], [0.23, 0.2, 0.16]))
    parts.append(ellipsoid('shoulders', (0, 0, 1.49), (0.24, 0.14, 0.1)))
    parts.append(ellipsoid('neckp', (0, 0, 1.58), (0.07, 0.07, 0.1)))
    parts.append(ellipsoid('head', (0, -0.01, 1.68), (0.11, 0.12, 0.135)))
    parts.append(ellipsoid('hair', (0, 0.03, 1.73), (0.115, 0.12, 0.11)))
    parts.append(skeleton('armR', [(0.14, 0, 1.48), (0.28, -0.06, 1.14), (0.24, -0.12, 0.94)], [0.08, 0.055, 0.045]))
    parts.append(skeleton('armL', [(-0.14, 0, 1.48), (-0.3, -0.14, 1.24), (-0.14, -0.26, 1.3)], [0.08, 0.055, 0.05]))
    body = organic(parts, voxel=0.026, tris=12000)
    book = cube('book', (0.2, 0.06, 0.15), (-0.1, -0.28, 1.3), rot=(0, 0, 0.3))
    join([body, book])
    export('poet')


def king():
    """Robed king with a staff (Pharnavaz, Vazha-Pshavela variant)."""
    reset()
    parts = robe('cape', 0.38, 0.26, 1.14, (0, 0, 0), folds=6)
    parts.append(skeleton('torso', [(0, 0, 1.1), (0, 0, 1.34), (0, 0, 1.52)], [0.25, 0.21, 0.16]))
    parts.append(ellipsoid('shoulders', (0, 0, 1.51), (0.25, 0.15, 0.1)))
    parts.append(ellipsoid('neckp', (0, 0, 1.6), (0.07, 0.07, 0.1)))
    parts.append(ellipsoid('head', (0, -0.01, 1.7), (0.11, 0.12, 0.135)))
    parts.append(skeleton('armR', [(0.14, 0, 1.5), (0.32, -0.14, 1.3), (0.28, -0.2, 1.16)], [0.08, 0.06, 0.05]))
    parts.append(skeleton('armL', [(-0.14, 0, 1.5), (-0.34, -0.1, 1.28), (-0.34, -0.16, 1.1)], [0.08, 0.06, 0.05]))
    body = organic(parts, voxel=0.028, tris=12000)
    crown = cyl('crown', 0.115, 0.125, 0.09, (0, -0.01, 1.83), verts=12)
    staff = cyl('staff', 0.022, 0.022, 1.5, (0.29, -0.22, 0.76), verts=8)
    smooth(crown)
    smooth(staff)
    join([body, crown, staff])
    export('king')


def nino():
    """St Nino holding the grapevine cross with both hands."""
    reset()
    parts = robe('gown', 0.4, 0.25, 1.1, (0, 0, 0))
    parts.append(skeleton('torso', [(0, 0, 1.06), (0, 0, 1.3), (0, 0, 1.48)], [0.24, 0.2, 0.16]))
    parts.append(ellipsoid('shoulders', (0, 0, 1.47), (0.24, 0.14, 0.1)))
    parts.append(ellipsoid('neckp', (0, 0, 1.56), (0.07, 0.07, 0.1)))
    parts.append(ellipsoid('head', (0, 0, 1.66), (0.11, 0.12, 0.135)))
    parts.append(ellipsoid('scarf', (0, 0.035, 1.7), (0.125, 0.14, 0.12)))
    parts.append(skeleton('armR', [(0.14, 0, 1.46), (0.28, -0.18, 1.28), (0.1, -0.26, 1.31)], [0.08, 0.055, 0.05]))
    parts.append(skeleton('armL', [(-0.14, 0, 1.46), (-0.28, -0.18, 1.28), (-0.1, -0.26, 1.31)], [0.08, 0.055, 0.05]))
    body = organic(parts, voxel=0.028, tris=12000)
    # grapevine cross: drooping arms
    post = cube('post', (0.035, 0.03, 0.6), (0, -0.28, 1.42))
    armR = cube('caR', (0.22, 0.028, 0.045), (0.11, -0.28, 1.56), rot=(0, math.radians(10), 0))
    armL = cube('caL', (0.22, 0.028, 0.045), (-0.11, -0.28, 1.56), rot=(0, math.radians(-10), 0))
    join([body, post, armR, armL])
    export('nino')


def street():
    """Light generic street-statue figure for the instanced layer (~2k tris)."""
    reset()
    parts = robe('coat', 0.3, 0.22, 1.1, (0, 0, 0), folds=4)
    parts.append(skeleton('torso', [(0, 0, 1.06), (0, 0, 1.32), (0, 0, 1.5)], [0.22, 0.19, 0.15]))
    parts.append(ellipsoid('shoulders', (0, 0, 1.49), (0.23, 0.13, 0.1)))
    parts.append(ellipsoid('head', (0, 0, 1.64), (0.11, 0.115, 0.13)))
    parts.append(skeleton('armR', [(0.13, 0, 1.47), (0.27, -0.05, 1.12), (0.24, -0.1, 0.95)], [0.075, 0.05, 0.04]))
    parts.append(skeleton('armL', [(-0.13, 0, 1.47), (-0.28, -0.12, 1.22), (-0.16, -0.22, 1.28)], [0.075, 0.05, 0.045]))
    organic(parts, voxel=0.035, tris=2200)
    export('street')


if __name__ == '__main__':
    for fn in (equestrian, george, kartlis_deda, poet, king, nino, street):
        fn()
    print('ALL DONE')
