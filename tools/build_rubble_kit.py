# Rubble debris kit for the building-destruction system:
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_rubble_kit.py
# Six low-poly, vertex-colored debris chunks (~1.6k tris total) exported into
# ONE GLB — src/citymap.js instances each named mesh into a global rubble
# layer and scatters them over collapsed footprints. Real meter scale, every
# chunk rests on the ground plane with its origin at the base center.
# Blender 4.2+; data-API only, safe to re-run.
import math
import os
import random

import bmesh
import bpy
from mathutils import Vector, noise

# ---------------- CONFIG ----------------
SEED = 41                     # deterministic fracture
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models')
SHOT = os.environ.get('RUBBLE_SHOT', '/tmp/rubble_kit.png')
COLL = 'RubbleKit'

CONCRETE = (0.42, 0.41, 0.38)   # weathered grey concrete
CONCRETE_DK = (0.24, 0.23, 0.21)  # fractured cores read darker
PLASTER = (0.78, 0.68, 0.52)    # warm Tbilisi facade plaster
BRICK = (0.46, 0.22, 0.15)      # old brick
BRICK_VAR = 0.12                # per-brick tint swing
CHAR = (0.05, 0.045, 0.04)      # burnt timber
RUST = (0.28, 0.14, 0.09)       # exposed rebar

random.seed(SEED)
noise.seed_set(SEED)


def reset():
    coll = bpy.data.collections.get(COLL)
    if coll:
        for o in list(coll.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.collections.remove(coll)
    # a fresh -b session still carries the default cube/camera/light — and the
    # cube lives in a CHILD collection, so sweep every object in the file
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    coll = bpy.data.collections.new(COLL)
    bpy.context.scene.collection.children.link(coll)
    for block in (bpy.data.meshes, bpy.data.materials):
        for d in list(block):
            if d.users == 0:
                block.remove(d)
    return coll


def paint(mesh, color_fn):
    """Per-vertex color attribute (loop domain — glTF exports COLOR_0)."""
    attr = mesh.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            v = mesh.vertices[mesh.loops[li].vertex_index].co
            r, g, b = color_fn(v, poly)
            attr.data[li].color = (r, g, b, 1.0)


VC_MAT = None


def vc_material():
    global VC_MAT
    if VC_MAT:
        return VC_MAT
    m = bpy.data.materials.new('rubble_vc')
    m.use_nodes = True
    bsdf = m.node_tree.nodes['Principled BSDF']
    attr = m.node_tree.nodes.new('ShaderNodeVertexColor')
    attr.layer_name = 'Col'
    m.node_tree.links.new(attr.outputs['Color'], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 0.95
    VC_MAT = m
    return m


def finish(coll, name, bm, color_fn):
    """bmesh → grounded object with vertex colors + material."""
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    paint(mesh, color_fn)
    mesh.materials.append(vc_material())
    obj = bpy.data.objects.new(name, mesh)
    coll.objects.link(obj)
    # rest on the ground: shift so min Z = 0, origin at base center
    zs = [v.co.z for v in mesh.vertices]
    xs = [v.co.x for v in mesh.vertices]
    ys = [v.co.y for v in mesh.vertices]
    dx = (max(xs) + min(xs)) / 2
    dy = (max(ys) + min(ys)) / 2
    for v in mesh.vertices:
        v.co.x -= dx
        v.co.y -= dy
        v.co.z -= min(zs)
    return obj


def jagged(bm, amp=0.12, scale=1.6):
    """Fracture look: noise-displace vertices along their normal-ish radial."""
    for v in bm.verts:
        n = noise.noise_vector(v.co * scale)
        v.co += Vector((n.x, n.y, n.z * 0.6)) * amp


# ---------------- chunks ----------------

def chunk_slab(coll):
    """Broken concrete floor slab — irregular pentagon, jagged rim."""
    bm = bmesh.new()
    pts = []
    n = 7
    for i in range(n):
        a = i / n * 2 * math.pi
        r = 0.9 + random.uniform(-0.28, 0.28)
        pts.append(bm.verts.new((math.cos(a) * r, math.sin(a) * r * 0.8, 0)))
    face = bm.faces.new(pts)
    res = bmesh.ops.extrude_face_region(bm, geom=[face])
    up = [g for g in res['geom'] if isinstance(g, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, verts=up, vec=(0.06, -0.04, 0.22))  # cracked tilt
    jagged(bm, amp=0.1)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    def col(v, poly):
        base = CONCRETE if poly.normal.z > 0.5 else CONCRETE_DK
        f = 0.9 + noise.noise(Vector(v) * 4.0) * 0.2
        return (base[0] * f, base[1] * f, base[2] * f)
    return finish(coll, 'rb_slab', bm, col)


def chunk_wall(coll):
    """Wall fragment: plaster face one side, torn concrete everywhere else."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= 1.5
        v.co.y *= 0.18
        v.co.z *= 1.1
    # tear the top edge down unevenly
    for v in bm.verts:
        if v.co.z > 0:
            v.co.z -= random.uniform(0.0, 0.45)
            v.co.x += random.uniform(-0.1, 0.1)
    jagged(bm, amp=0.06, scale=2.5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    def col(v, poly):
        if poly.normal.y < -0.6:  # the surviving painted facade side
            f = 0.92 + noise.noise(Vector(v) * 3.0) * 0.16
            return (PLASTER[0] * f, PLASTER[1] * f, PLASTER[2] * f)
        base = CONCRETE if abs(poly.normal.z) < 0.5 else CONCRETE_DK
        f = 0.88 + noise.noise(Vector(v) * 5.0) * 0.24
        return (base[0] * f, base[1] * f, base[2] * f)
    return finish(coll, 'rb_wall', bm, col)


def chunk_lump(coll):
    """Angular concrete boulder."""
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=0.55)
    for v in bm.verts:
        v.co *= 1.0 + noise.noise(v.co * 2.2) * 0.55
        v.co.z *= 0.72  # squat
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    def col(v, poly):
        f = 0.82 + noise.noise(Vector(v) * 6.0) * 0.35
        return (CONCRETE[0] * f, CONCRETE[1] * f, CONCRETE[2] * f)
    return finish(coll, 'rb_lump', bm, col)


def chunk_bricks(coll):
    """A spill of 8 loose bricks."""
    bm = bmesh.new()
    for i in range(8):
        b2 = bmesh.new()
        bmesh.ops.create_cube(b2, size=1.0)
        for v in b2.verts:
            v.co.x *= 0.25
            v.co.y *= 0.12
            v.co.z *= 0.09
        ang = random.uniform(0, math.pi)
        ca, sa = math.cos(ang), math.sin(ang)
        ox = random.uniform(-0.35, 0.35)
        oy = random.uniform(-0.3, 0.3)
        oz = 0.045 + (0.09 if random.random() < 0.3 else 0)
        tint = 1.0 + random.uniform(-BRICK_VAR, BRICK_VAR)
        for v in b2.verts:
            x, y = v.co.x, v.co.y
            v.co.x = x * ca - y * sa + ox
            v.co.y = x * sa + y * ca + oy
            v.co.z += oz
        mesh_tmp = bpy.data.meshes.new('tmpb')
        b2.to_mesh(mesh_tmp)
        b2.free()
        bm.from_mesh(mesh_tmp)
        bpy.data.meshes.remove(mesh_tmp)
        del tint  # per-brick swing comes from the noise in col() instead
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    def col(v, poly):
        f = 0.9 + noise.noise(Vector(v) * 8.0) * 0.3
        return (BRICK[0] * f, BRICK[1] * f, BRICK[2] * f)
    return finish(coll, 'rb_bricks', bm, col)


def chunk_beam(coll):
    """Charred roof beam, slightly bowed."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= 2.2
        v.co.y *= 0.14
        v.co.z *= 0.16
    # bow + char-crackle
    for v in bm.verts:
        v.co.z += math.sin((v.co.x / 2.2 + 0.5) * math.pi) * 0.08
    jagged(bm, amp=0.03, scale=6.0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    def col(v, poly):
        f = 0.7 + noise.noise(Vector(v) * 10.0) * 0.6  # ember glints in the char
        return (CHAR[0] * f * 1.6, CHAR[1] * f, CHAR[2] * f)
    return finish(coll, 'rb_beam', bm, col)


def chunk_rebar(coll):
    """Slab corner with three bent rebar rods sticking out."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= 0.8
        v.co.y *= 0.7
        v.co.z *= 0.28
    jagged(bm, amp=0.08, scale=3.0)
    rod_faces = []
    for i in range(3):
        b2 = bmesh.new()
        bmesh.ops.create_cone(
            b2, cap_ends=True, segments=5, radius1=0.022, radius2=0.022, depth=0.7)
        ang = random.uniform(-0.7, 0.7)
        for v in b2.verts:
            # tilt the rod and bend the tip
            z = v.co.z + 0.35
            v.co.x += math.sin(ang) * z + (z ** 2) * 0.35
            v.co.z = z * math.cos(ang) + 0.1
            v.co.x += 0.25 * i - 0.25
            v.co.y += random.uniform(-0.2, 0.2)
        m2 = bpy.data.meshes.new('tmpr')
        b2.to_mesh(m2)
        b2.free()
        vcount = len(bm.verts)
        bm.from_mesh(m2)
        bpy.data.meshes.remove(m2)
        bm.verts.ensure_lookup_table()
        rod_faces.extend(f for f in bm.faces if all(v.index >= vcount for v in f.verts))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    rod_face_set = set(f.index for f in rod_faces)

    def col(v, poly):
        if poly.index in rod_face_set:
            return RUST
        base = CONCRETE if poly.normal.z > 0.3 else CONCRETE_DK
        f = 0.85 + noise.noise(Vector(v) * 5.0) * 0.3
        return (base[0] * f, base[1] * f, base[2] * f)
    return finish(coll, 'rb_rebar', bm, col)


# ---------------- build + export ----------------

def main():
    coll = reset()
    objs = [
        chunk_slab(coll), chunk_wall(coll), chunk_lump(coll),
        chunk_bricks(coll), chunk_beam(coll), chunk_rebar(coll),
    ]
    tris = 0
    for o in objs:
        o.data.calc_loop_triangles()
        tris += len(o.data.loop_triangles)

    # preview shot: spread along X, render, then re-center for export
    for i, o in enumerate(objs):
        o.location.x = (i - (len(objs) - 1) / 2) * 2.2
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    bpy.context.scene.collection.objects.link(cam)
    cam.location = (0, -9.5, 5.5)
    cam.rotation_euler = (math.radians(58), 0, 0)
    bpy.context.scene.camera = cam
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN'))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    bpy.context.scene.collection.objects.link(sun)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.color_type = 'VERTEX'
    sc.render.resolution_x = 1100
    sc.render.resolution_y = 420
    sc.render.filepath = SHOT
    bpy.ops.render.render(write_still=True)

    for o in objs:
        o.location.x = 0.0
    bpy.data.objects.remove(cam, do_unlink=True)
    bpy.data.objects.remove(sun, do_unlink=True)

    fp = os.path.join(OUT, 'rubble_kit.glb')
    bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB')
    print(f'EXPORT {fp}: {len(objs)} chunks, {tris} tris, shot {SHOT}')


main()
