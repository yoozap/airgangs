# Convert the CC-BY Sketchfab photogrammetry scans (Badri Gogia) of the real
# Gorgasali and Saakadze monuments into game GLBs, KEEPING their photo
# textures (this is the realism the procedural models can't match).
#   SCAN_DIR=... /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/convert_scans2.py [-- names]
# Orient so the equestrian faces +X, normalize height 1 / base z=0, decimate
# with UVs preserved. Preview renders (EEVEE, textured) to $STATUE_SHOTS.
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

SCAN = os.environ.get('SCAN_DIR', '/tmp/scans')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'statues')
SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')

# rot: XYZ degrees to bring the scan to Z-up facing +X. Iterate via previews.
# crop: (x0, x1, y0, y1) in raw scan coords — keep only the monument, drop
# the surrounding plaza the drone scan swept up.
SCANS = {
    'gorgasali': {'file': 'gorgasali.glb', 'rot': (0, 0, 0), 'tris': 60000,
                  'crop': None, 'zcut': 1.1},
    'saakadze': {'file': 'saakadze.glb', 'rot': (0, 0, 90), 'tris': 60000,
                 'crop': None},
}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    objs = [o for o in set(bpy.data.objects) - before if o.type == 'MESH']
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.object
    # bake the FULL world matrix (incl. parent chain — Sketchfab GLBs nest
    # meshes under transformed empties) into the mesh data
    obj.data.transform(obj.matrix_world)
    obj.parent = None
    obj.matrix_world = Matrix.Identity(4)
    return obj


def bbox(obj):
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    return (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))


def crop_xy(obj, box):
    import bmesh
    x0, x1, y0, y1 = box
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    kill = [v for v in bm.verts if not (x0 <= v.co.x <= x1 and y0 <= v.co.y <= y1)]
    bmesh.ops.delete(bm, geom=kill, context='VERTS')
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(obj.data)
    bm.free()


def process(name, cfg):
    reset()
    obj = import_glb(os.path.join(SCAN, cfg['file']))
    if cfg.get('crop'):
        crop_xy(obj, cfg['crop'])
    if cfg.get('zcut'):
        import bmesh
        (bx0, bx1), (by0, by1), (bz0, bz1) = bbox(obj)
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        kill = [v for v in bm.verts if v.co.z < bz0 + cfg['zcut']]
        bmesh.ops.delete(bm, geom=kill, context='VERTS')
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.to_mesh(obj.data)
        bm.free()
    rot = cfg['rot']
    if any(rot):
        m = (Matrix.Rotation(math.radians(rot[2]), 4, 'Z')
             @ Matrix.Rotation(math.radians(rot[1]), 4, 'Y')
             @ Matrix.Rotation(math.radians(rot[0]), 4, 'X'))
        obj.data.transform(m)
    obj.data.calc_loop_triangles()
    n0 = len(obj.data.loop_triangles)
    if n0 > cfg['tris']:
        d = obj.modifiers.new('dec', 'DECIMATE')
        d.ratio = cfg['tris'] / n0
        dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
        obj.modifiers.clear()
        old = obj.data
        obj.data = me
    (x0, x1), (y0, y1), (z0, z1) = bbox(obj)
    h = z1 - z0
    print(f'{name}: {n0} tris -> {len(obj.data.loop_triangles)}; dims {x1-x0:.1f} x {y1-y0:.1f} x {h:.1f} m')
    obj.data.transform(Matrix.Translation((-(x0 + x1) / 2, -(y0 + y1) / 2, -z0)))
    obj.data.transform(Matrix.Diagonal((1 / h, 1 / h, 1 / h, 1)))
    # orientation previews from 4 sides (workbench: reliable, untextured)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'TEXTURE'
    sc.display.shading.show_backface_culling = False
    sc.render.resolution_x = 560
    sc.render.resolution_y = 640
    (nx0, nx1), (ny0, ny1), (nz0, nz1) = bbox(obj)  # post-normalize units
    dim = max(nx1 - nx0, ny1 - ny0, nz1 - nz0)
    c = Vector((0, 0, 0.5))
    for label, d in (('px', Vector((1, 0, 0.15))), ('nx', Vector((-1, 0, 0.15))),
                     ('py', Vector((0, 1, 0.15))), ('ny', Vector((0, -1, 0.15)))):
        cam = bpy.data.objects.new('cam' + label, bpy.data.cameras.new('c'))
        sc.collection.objects.link(cam)
        sc.camera = cam
        cam.location = c + d.normalized() * dim * 2.2
        tgt = bpy.data.objects.new('t' + label, None)
        sc.collection.objects.link(tgt)
        tgt.location = c
        tr = cam.constraints.new('TRACK_TO')
        tr.target = tgt
        tr.track_axis = 'TRACK_NEGATIVE_Z'
        tr.up_axis = 'UP_Y'
        sc.render.filepath = os.path.join(SHOTS, f'scan2_{name}_{label}.png')
        bpy.ops.render.render(write_still=True)
    if os.environ.get('EXPORT') == '1':
        fp = os.path.join(OUT, f'{name}.glb')
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB', use_selection=True)
        print(f'EXPORT {fp} ({os.path.getsize(fp) // 1024} KB)')


if __name__ == '__main__':
    only = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    for name, cfg in SCANS.items():
        if only and name not in only:
            continue
        process(name, cfg)
    print('DONE')
