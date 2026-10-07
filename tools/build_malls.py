# All Tbilisi malls / market halls, modeled in Blender on their REAL OSM
# footprints (tools/cache/malls.json, built by tools/extract_malls.py):
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_malls.py
#
# One GLB, one multi-material object per mall named mall_<bid>. Geometry is
# authored directly in the GAME frame: Blender x = game x (west+),
# Blender y = -game z (so glTF Y-up export lands z = north+), z = up —
# runtime places each object at (x, groundY, z) with NO rotation.
# Look: composite-cladding retail box on the exact outline — concrete plinth,
# full-perimeter display glazing, accent band, dark fascia crown, protruding
# glass entrance atrium + canopy on the longest street edge, roof RTUs and a
# skylight spine on the big ones. Four cladding/accent palettes rotate by bid
# so no two neighbouring malls match. ~700 tris each, 42 malls ≈ 30k tris.
import json
import math
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'public', 'models', 'malls.glb')
MALLS = json.load(open(os.path.join(HERE, 'cache', 'malls.json')))

bpy.ops.wm.read_factory_settings(use_empty=True)
D = bpy.data


def hexmat(name, hx, rough=0.85, metal=0.0):
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
    return m


CLADS = [hexmat('clad0', '#cfc9bc'), hexmat('clad1', '#d8d3c8'),
         hexmat('clad2', '#c4bbae'), hexmat('clad3', '#d9cfc0')]
ACCENTS = [hexmat('acc0', '#b4542f'), hexmat('acc1', '#22456e'),
           hexmat('acc2', '#5f7d54'), hexmat('acc3', '#c07f2e')]
M_GLASS = hexmat('mglass', '#20343a', rough=0.15, metal=0.35)
M_DARK = hexmat('mdark', '#2c2f33', rough=0.5, metal=0.3)
M_CONC = hexmat('mconc', '#9b968c')
M_ROOF = hexmat('mroof', '#7e7a72')
M_SKY = hexmat('msky', '#3d5a66', rough=0.2, metal=0.3)


def link(ob):
    bpy.context.collection.objects.link(ob)
    return ob


def prism(name, ring, z0, z1, mat):
    n = len(ring)
    verts = [(x, y, z0) for x, y in ring] + [(x, y, z1) for x, y in ring]
    faces = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, n + j, n + i))
    me = D.meshes.new(name)
    me.from_pydata([Vector(v) for v in verts], [], faces)
    me.validate()
    ob = link(D.objects.new(name, me))
    ob.data.materials.append(mat)
    return ob


def box(name, dims, loc, mat, rz=0.0):
    me = D.meshes.new(name)
    w, d, h = dims[0] / 2, dims[1] / 2, dims[2] / 2
    vs = [(sx * w, sy * d, sz * h) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    fs = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    me.from_pydata([Vector(v) for v in vs], [], fs)
    ob = link(D.objects.new(name, me))
    ob.rotation_euler[2] = rz
    ob.location = loc
    ob.data.materials.append(mat)
    return ob


def offset_ring(pts, d):
    """Offset a CCW ring outward by d (miter join, good enough for walls)."""
    n = len(pts)
    out = []
    for i in range(n):
        ax, ay = pts[(i - 1) % n]
        bx, by = pts[i]
        cx, cy = pts[(i + 1) % n]
        v1 = Vector((bx - ax, by - ay)).normalized()
        v2 = Vector((cx - bx, cy - by)).normalized()
        n1 = Vector((v1.y, -v1.x))
        n2 = Vector((v2.y, -v2.x))
        m = (n1 + n2)
        if m.length < 1e-6:
            m = n1
        m.normalize()
        scale = d / max(0.35, 1 + n1.dot(n2)) * 2 / 2  # miter limited
        out.append((bx + m.x * d / max(0.5, m.dot(n1)), by + m.y * d / max(0.5, m.dot(n1))))
    return out


def joined(name, obs):
    bpy.ops.object.select_all(action='DESELECT')
    for o in obs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]
    bpy.ops.object.join()
    ob = bpy.context.active_object
    ob.name = name
    ob.data.name = name
    return ob


def build_mall(e):
    bid = e['bid']
    # game frame -> blender frame: (x, -z), ensure CCW
    ring = [(p[0], -p[1]) for p in e['poly']]
    area2 = sum(ring[i][0] * ring[(i + 1) % len(ring)][1]
                - ring[(i + 1) % len(ring)][0] * ring[i][1] for i in range(len(ring)))
    if area2 < 0:
        ring = ring[::-1]
    # drop micro-edges (OSM rings can be dense) to keep tris down
    slim = []
    for p in ring:
        if not slim or math.hypot(p[0] - slim[-1][0], p[1] - slim[-1][1]) > 2.2:
            slim.append(p)
    if len(slim) >= 4:
        ring = slim
    H = min(26.0, max(8.0, e['h']))
    pal = bid % 4
    parts = []
    parts.append(prism('plinth', offset_ring(ring, 0.9), -1.2, 0.55, M_CONC))
    parts.append(prism('body', ring, 0.55, H - 1.4, CLADS[pal]))
    # full-perimeter display glazing just proud of the cladding
    parts.append(prism('shopglass', offset_ring(ring, 0.14), 0.55, 4.0, M_GLASS))
    # accent band + dark fascia crown
    parts.append(prism('band', offset_ring(ring, 0.10), H * 0.52, H * 0.52 + 1.7, ACCENTS[pal]))
    parts.append(prism('fascia', offset_ring(ring, 0.16), H - 1.4, H + 0.4, M_DARK))
    parts.append(prism('roof', offset_ring(ring, -0.4), H - 1.5, H - 1.0, M_ROOF))

    # entrance atrium on the longest edge
    best, bi_ = 0, 0
    n = len(ring)
    for i in range(n):
        j = (i + 1) % n
        l2 = (ring[j][0] - ring[i][0]) ** 2 + (ring[j][1] - ring[i][1]) ** 2
        if l2 > best:
            best, bi_ = l2, i
    ax, ay = ring[bi_]
    bx, by = ring[(bi_ + 1) % n]
    ex, ey = bx - ax, by - ay
    ln = math.hypot(ex, ey)
    ux, uy = ex / ln, ey / ln
    nx, ny = uy, -ux  # outward for CCW
    angz = math.atan2(uy, ux)
    mx, my = ax + ex * 0.5, ay + ey * 0.5
    ew = min(ln * 0.45, 28.0)
    eh = min(H * 0.8, 12.0)
    ed = 5.0
    # glass box + dark portal frame + canopy on slim columns
    parts.append(box('atrium', (ew, ed, eh), (mx + nx * ed * 0.45, my + ny * ed * 0.45, eh / 2), M_GLASS, angz))
    parts.append(box('portal', (ew + 1.6, 0.8, 1.1), (mx + nx * (ed - 0.2), my + ny * (ed - 0.2), eh + 0.55), ACCENTS[pal], angz))
    parts.append(box('canopy', (ew + 6, 7.5, 0.55), (mx + nx * 4.2, my + ny * 4.2, eh + 0.1), M_DARK, angz))
    for s in (-1, 1):
        cxp = mx + ux * s * (ew / 2 + 2.4) + nx * 7.2
        cyp = my + uy * s * (ew / 2 + 2.4) + ny * 7.2
        parts.append(box(f'col{s}', (0.6, 0.6, eh), (cxp, cyp, eh / 2), M_CONC, angz))
    # sign anchor plate (runtime paints the REAL name canvas just in front)
    parts.append(box('signplate', (min(ew, 20), 0.5, 2.4), (mx + nx * (ed + 0.25), my + ny * (ed + 0.25), eh - 1.1), M_DARK, angz))

    # roof clutter: RTUs by bid hash + skylight spine on the big boxes
    rnd = lambda k: (math.sin(bid * 12.9898 + k * 78.233) * 43758.5453) % 1  # noqa: E731
    cxm = sum(p[0] for p in ring) / n
    cym = sum(p[1] for p in ring) / n
    span = math.sqrt(e['area'])
    for k in range(min(8, 3 + int(e['area'] / 3500))):
        ox = (rnd(k) - 0.5) * span * 0.5
        oy = (rnd(k + 40) - 0.5) * span * 0.5
        parts.append(box(f'rtu{k}', (3.2 + rnd(k + 7) * 2, 2.4, 1.6 + rnd(k + 9)),
                         (cxm + ox, cym + oy, H - 1.0 + 0.9), M_CONC, rnd(k + 3) * 3.14))
    if e['area'] > 8000:
        parts.append(box('skylight', (span * 0.55, 6.5, 1.5), (cxm, cym, H - 0.9), M_SKY, angz))
    ob = joined(f'mall_{bid}', parts)
    # entrance anchor for the runtime name sign, packed into custom props
    ob['sign'] = [mx + nx * (ed + 0.6), -(my + ny * (ed + 0.6)), eh - 1.1, math.atan2(nx, -ny)]
    return ob


tris = 0
for e in MALLS:
    ob = build_mall(e)
    ob.data.calc_loop_triangles()
    tris += len(ob.data.loop_triangles)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', use_selection=True,
                          export_apply=True, export_yup=True, export_extras=True)
print(f'malls exported: {OUT} — {len(MALLS)} malls, {tris} tris')
