# Tbilisi International Airport (TBS) assets, modeled in Blender:
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_airport_assets.py
#
# terminal  — the modern glass passenger terminal (240 m curtain-wall hall,
#             overhanging curved roof, TBILISI letters on the fascia)
# tower     — ATC control tower: tapered shaft, flared gallery, slanted dark cab
# vip       — the 1952 Stalinist neoclassical terminal: arcaded wings, central
#             tower with belvedere and gold spire
# jetbridge — telescoping glass jet bridge with rotunda and A-frame bogie
# hangar    — arched-roof maintenance hangar with segmented sliding doors
# airliner  — generic narrow-body (A320-class) for the gates and remote stands
# mast/windsock/radar/papi/applight/fueltruck/bus/stairs/tug — apron props
#
# Convention (same as metro/statues): real meter scale, origin at ground
# center, front facing -Y (→ three.js +Z after glTF Y-up export). Parts are
# named — src/airport.js maps part names to materials. Blender 4.2+/5.x.
import math
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import build_statues as bs  # noqa: E402  (reset/link/cube/cyl/join helpers)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'airport')
SHOTS = os.environ.get('AIRPORT_SHOTS', '/tmp')
os.makedirs(OUT, exist_ok=True)

# ---- real GIS footprints (public/tbilisi/airport.json) ---------------------
# Buildings are modeled on their EXACT OSM outlines, projected to local meters
# around the footprint centroid: x = east, y = north (the game re-orients).
import json  # noqa: E402

MLAT, MLON = 111067.92066361613, 83206.33860686835
AIRPORT = json.load(open(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'tbilisi', 'airport.json')))


def footprint(kind=None, name=None):
    """Return (ring_xy_meters, centroid_latlon) for a building from OSM."""
    for b in AIRPORT['buildings']:
        if (kind and b['kind'] == kind) or (name and b.get('name') == name):
            ring = b['ring']
            if ring[0] == ring[-1]:
                ring = ring[:-1]
            clat = sum(p[0] for p in ring) / len(ring)
            clon = sum(p[1] for p in ring) / len(ring)
            pts = [((p[1] - clon) * MLON, (p[0] - clat) * MLAT) for p in ring]
            # ensure counter-clockwise so edge normals point outward
            area = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] -
                       pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))
            if area < 0:
                pts = pts[::-1]
            return pts, (clat, clon)
    raise SystemExit(f'footprint not found: {kind or name}')


def offset_ring(pts, d):
    """Miter-offset a CCW polygon outward by d meters (inward if d<0)."""
    n = len(pts)
    out = []
    for i in range(n):
        ax, ay = pts[(i - 1) % n]
        bx, by = pts[i]
        cx, cy = pts[(i + 1) % n]
        # outward normals of the two edges (CCW ring -> right-hand normal)
        def nrm(px, py, qx, qy):
            ex, ey = qx - px, qy - py
            ln = math.hypot(ex, ey) or 1
            return ey / ln, -ex / ln
        n1 = nrm(ax, ay, bx, by)
        n2 = nrm(bx, by, cx, cy)
        mx, my = n1[0] + n2[0], n1[1] + n2[1]
        ml = math.hypot(mx, my) or 1
        dot = max(0.35, (mx / ml) * n1[0] + (my / ml) * n1[1])  # cap miter spikes
        out.append((bx + (mx / ml) * d / dot, by + (my / ml) * d / dot))
    return out


def prism(name, ring, z0, z1):
    """Extruded polygon (ngon top/bottom) between z0 and z1."""
    n = len(ring)
    verts = [(x, y, z0) for x, y in ring] + [(x, y, z1) for x, y in ring]
    faces = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, n + j, n + i))
    me = bpy.data.meshes.new(name)
    me.from_pydata([Vector(v) for v in verts], [], faces)
    me.validate()
    return bs.link(bpy.data.objects.new(name, me))

ONLY = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def name_obj(o, n):
    o.name = n
    return o


def export(name):
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f'{name}.glb'), export_format='GLB')
    tris = sum(len(o.data.loop_triangles) if hasattr(o.data, 'loop_triangles') else 0
               for o in bpy.data.objects if o.type == 'MESH')
    for o in bpy.data.objects:
        if o.type == 'MESH':
            o.data.calc_loop_triangles()
    tris = sum(len(o.data.loop_triangles) for o in bpy.data.objects if o.type == 'MESH')
    print(f'EXPORT {name}: {tris} tris')


def shoot(name, view=(0.9, -1.5, 0.5)):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x = 720
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
    cam.location = c + Vector(view).normalized() * dim * 1.5
    tgt = bs.link(bpy.data.objects.new('t', None))
    tgt.location = c
    tr = cam.constraints.new('TRACK_TO')
    tr.target = tgt
    tr.track_axis = 'TRACK_NEGATIVE_Z'
    tr.up_axis = 'UP_Y'
    sc.render.filepath = os.path.join(SHOTS, f'airport_{name}.png')
    bpy.ops.render.render(write_still=True)


def cube(name, dims, loc, rot=(0, 0, 0)):
    return bs.cube(name, dims, loc, rot)


def cyl(name, r1, r2, depth, loc, rot=(0, 0, 0), verts=20):
    return bs.cyl(name, r1, r2, depth, loc, rot, verts)


def joinparts(objs, name):
    """Join a list of objects into one mesh object with the given part name."""
    o = bs.join([x for x in objs if x])
    o.name = name
    return o


def text_mesh(name, body, size, depth):
    """Extruded 3D text, converted to a plain mesh, origin at text center."""
    cu = bpy.data.curves.new(name, type='FONT')
    cu.body = body
    cu.size = size
    cu.extrude = depth / 2
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    ob = bs.link(bpy.data.objects.new(name, cu))
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    bpy.data.objects.remove(ob)
    bpy.data.curves.remove(cu)
    return bs.link(bpy.data.objects.new(name, me))


def smooth(o):
    for p in o.data.polygons:
        p.use_smooth = True
    return o


# ---------------------------------------------------------------------------
# terminal — the REAL footprint (OSM rel 20405381) extruded: glass curtain
# walls with mullions on every edge, stone podium, deep overhanging roof deck.
# Built in true east/north meters; the game rotates it into the world.
# ---------------------------------------------------------------------------
def terminal():
    bs.reset()
    ring, _c = footprint(kind='main_terminal')
    H = 16.5          # curtain-wall top
    POD = 3.2         # stone podium height
    concrete, glass, frames, roofp, cols, dark, clad = [], [], [], [], [], [], []

    # plinth + podium + interior core follow the real outline
    concrete.append(prism('plinth', offset_ring(ring, 4.0), -0.2, 0.7))
    clad.append(prism('podium', offset_ring(ring, 0.25), 0.7, POD))
    dark.append(prism('core', offset_ring(ring, -2.5), 0.7, H - 1.2))
    concrete.append(prism('slab2', offset_ring(ring, -1.6), 7.2, 7.9))  # visible floor

    # curtain wall along every real edge: glass plane + mullions + transoms
    n = len(ring)
    for i in range(n):
        ax, ay = ring[i]
        bx, by = ring[(i + 1) % n]
        ex, ey = bx - ax, by - ay
        ln = math.hypot(ex, ey)
        if ln < 2.5:
            continue
        ux, uy = ex / ln, ey / ln
        ang = math.atan2(uy, ux)
        cx, cy = (ax + bx) / 2, (ay + by) / 2
        glass.append(cube('g', (ln, 0.16, H - POD), (cx, cy, POD + (H - POD) / 2), (0, 0, ang)))
        m = max(1, round(ln / 5.5))
        for k in range(m + 1):
            t = k / m
            frames.append(cube('f', (0.3, 0.55, H - 0.7),
                               (ax + ex * t, ay + ey * t, 0.7 + (H - 0.7) / 2), (0, 0, ang)))
        for z in (POD + 3.4, POD + 7.6, POD + 10.8):
            frames.append(cube('f', (ln, 0.5, 0.28), (cx, cy, z), (0, 0, ang)))

    # roof: deep overhanging deck on the real outline, with fascia + parapet
    roof_out = offset_ring(ring, 7.0)
    roofp.append(prism('roof', roof_out, H, H + 1.7))
    frames.append(prism('fascia', offset_ring(ring, 7.05), H + 0.35, H + 1.35))
    concrete.append(prism('plant', offset_ring(ring, -6.0), H + 1.7, H + 3.1))  # rooftop plant block

    # slim columns carrying the overhang, spaced along the real long edges
    for i in range(n):
        ax, ay = ring[i]
        bx, by = ring[(i + 1) % n]
        ex, ey = bx - ax, by - ay
        ln = math.hypot(ex, ey)
        if ln < 24:
            continue
        # outward normal (ring is CCW)
        nx, ny = ey / ln, -ex / ln
        for k in range(1, int(ln // 18) + 1):
            t = k * 18 / ln
            if t >= 1:
                break
            cols.append(cyl('col', 0.5, 0.5, H, (ax + ex * t + nx * 5.2, ay + ey * t + ny * 5.2, H / 2), verts=12))

    # TBILISI letters on the roof fascia of the longest edge (the airside front)
    best = max(range(n), key=lambda i: math.hypot(ring[(i + 1) % n][0] - ring[i][0],
                                                  ring[(i + 1) % n][1] - ring[i][1]))
    ax, ay = ring[best]
    bx, by = ring[(best + 1) % n]
    ex, ey = bx - ax, by - ay
    ln = math.hypot(ex, ey)
    ux, uy = ex / ln, ey / ln
    nx, ny = ey / ln, -ex / ln
    ang = math.atan2(uy, ux)
    txt = text_mesh('letters', 'T B I L I S I', 3.4, 0.5)
    # after the X 90° tilt the text faces -Y; yaw it onto the edge's outward normal
    txt.rotation_euler = (math.pi / 2, 0, math.atan2(nx, -ny))
    txt.location = ((ax + bx) / 2 + nx * 7.6, (ay + by) / 2 + ny * 7.6, H + 3.3)
    with bpy.context.temp_override(selected_objects=[txt], active_object=txt):
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    joinparts(concrete, 'concrete')
    joinparts(glass, 'glass')
    joinparts(frames, 'frame')
    joinparts(roofp, 'roof')
    joinparts(cols, 'columns')
    joinparts(dark, 'dark')
    joinparts(clad, 'clad')
    name_obj(txt, 'letters')
    export('terminal')
    shoot('terminal')


# ---------------------------------------------------------------------------
# tower — ATC control tower, ~42 m
# ---------------------------------------------------------------------------
def tower():
    bs.reset()
    H = 34.0                        # shaft height to the gallery
    shaft, cab_g, cab_f, white, dark = [], [], [], [], []
    shaft.append(cyl('s', 3.6, 2.4, H, (0, 0, H / 2), verts=16))
    smooth(shaft[-1])
    # base block with door
    white.append(cube('b', (10, 8, 3.5), (0, 0, 1.75)))
    dark.append(cube('dr', (2.2, 0.4, 2.8), (0, -4.1, 1.4)))
    # service ring windows up the shaft
    for z in (10, 17, 24):
        dark.append(cyl('w', 2.95 - z * 0.035, 2.95 - z * 0.035, 1.0, (0, 0, z), verts=16))
    # flared support under the gallery
    fl = cyl('fl', 2.6, 5.2, 3.4, (0, 0, H + 1.7), verts=16)
    smooth(fl)
    white.append(fl)
    # gallery deck + rail
    white.append(cyl('g', 5.9, 5.9, 0.7, (0, 0, H + 3.6), verts=18))
    cab_f.append(cyl('r', 5.95, 5.95, 0.12, (0, 0, H + 4.6), verts=18))
    # cab: outward-slanted dark glazing, control-tower profile
    cg = cyl('cg', 4.6, 5.5, 3.4, (0, 0, H + 5.6), verts=16)
    smooth(cg)
    cab_g.append(cg)
    # mullions
    for i in range(8):
        a = i * math.pi / 4
        cab_f.append(cube('m', (0.18, 0.5, 3.4), (math.cos(a) * 5.0, math.sin(a) * 5.0, H + 5.6), (0.24, 0, a + math.pi / 2)))
    # cab roof + radar dome + antenna
    white.append(cyl('cr', 5.7, 4.9, 0.8, (0, 0, H + 7.7), verts=18))
    dome = cyl('dome', 1.5, 0.9, 1.5, (0, 0, H + 8.9), verts=14)
    smooth(dome)
    white.append(dome)
    cab_f.append(cyl('ant', 0.08, 0.02, 4.5, (0, 0, H + 11.5), verts=8))
    joinparts(shaft, 'shaft')
    joinparts(white, 'white')
    joinparts(dark, 'dark')
    joinparts(cab_g, 'glass')
    joinparts(cab_f, 'frame')
    export('tower')
    shoot('tower')


# ---------------------------------------------------------------------------
# vip — the 1952 Stalinist terminal: wings + central tower with gold spire
# ---------------------------------------------------------------------------
def vip():
    bs.reset()
    ring, _c = footprint(name='VIP-Terminal')
    stucco, stone, colsp, cornice, roofp, glass, frame, gold, dark = [], [], [], [], [], [], [], [], []

    # the real 1952 outline: base course, 2-storey walls, cornice, roof
    WH = 9.5
    stone.append(prism('base', offset_ring(ring, 0.3), -0.2, 1.2))
    stucco.append(prism('walls', ring, 1.2, 1.2 + WH))
    cornice.append(prism('corn', offset_ring(ring, 0.55), 1.2 + WH, 1.2 + WH + 0.7))
    roofp.append(prism('roof', offset_ring(ring, -0.8), 1.2 + WH + 0.7, 1.2 + WH + 2.2))

    # windows + pilasters marching along every real facade edge
    n = len(ring)
    for i in range(n):
        ax, ay = ring[i]
        bx, by = ring[(i + 1) % n]
        ex, ey = bx - ax, by - ay
        ln = math.hypot(ex, ey)
        if ln < 7:
            continue
        ux, uy = ex / ln, ey / ln
        nx, ny = ey / ln, -ex / ln
        ang = math.atan2(uy, ux)
        bays = max(1, round(ln / 6.5))
        for k in range(bays):
            t = (k + 0.5) / bays
            wx = ax + ex * t + nx * 0.1
            wy = ay + ey * t + ny * 0.1
            frame.append(cube('f', (2.7, 0.5, 6.1), (wx, wy, 4.6), (0, 0, ang)))
            glass.append(cube('g', (2.2, 0.62, 5.6), (wx, wy, 4.6), (0, 0, ang)))
            if k < bays - 1:
                px = ax + ex * ((k + 1) / bays) + nx * 0.15
                py = ay + ey * ((k + 1) / bays) + ny * 0.15
                stucco.append(cube('pl', (0.9, 0.5, WH - 0.4), (px, py, 1.2 + (WH - 0.4) / 2), (0, 0, ang)))

    # central block + portico + tower, aligned to the building's real axis
    AXIS = math.radians(-43.5)  # principal axis of the footprint (from east)
    ca, sa = math.cos(AXIS), math.sin(AXIS)
    R = lambda x, y: (x * ca - y * sa, x * sa + y * ca)  # noqa: E731
    # front direction: perpendicular, toward the apron (SW side, local -y before rotation)
    fx, fy = R(0, -1)
    D = 33.0
    CB, CH = 26.0, 14.5
    cb = cube('cb', (CB, D, CH), (0, 0, 1.2 + CH / 2), (0, 0, AXIS))
    stucco.append(cb)
    cc = cube('cc', (CB + 2, D * 0.9, 0.8), (0, 0, 1.2 + CH + 0.4), (0, 0, AXIS))
    cornice.append(cc)
    for i in range(6):
        x = -CB / 2 + 2.4 + i * (CB - 4.8) / 5
        px, py = R(x, -D / 2 - 1.8)
        colsp.append(cyl('col', 0.55, 0.55, 11.5, (px, py, 1.2 + 5.75), verts=12))
    pp = R(0, -D / 2 - 1.8)
    cornice.append(cube('p', (CB, 4.2, 1.0), (pp[0], pp[1], 1.2 + 11.5 + 0.5), (0, 0, AXIS)))

    # tower above the central block: two setbacks, belvedere, spire
    T1, T2 = 12.0, 8.0
    stucco.append(cube('t1', (T1, T1, 7.5), (0, 0, 1.2 + CH + 3.75), (0, 0, AXIS)))
    cornice.append(cube('tc', (T1 + 1.4, T1 + 1.4, 0.6), (0, 0, 1.2 + CH + 7.5 + 0.3), (0, 0, AXIS)))
    # belvedere: open colonnade
    for i in range(8):
        a = i * math.pi / 4 + math.pi / 8
        colsp.append(cyl('bc', 0.35, 0.35, 5.0, (math.cos(a) * T2 * 0.42, math.sin(a) * T2 * 0.42, 1.2 + CH + 8.1 + 2.5), verts=10))
    stucco.append(cube('t2', (T2 * 0.62, T2 * 0.62, 5.0), (0, 0, 1.2 + CH + 8.1 + 2.5), (0, 0, AXIS)))
    cornice.append(cube('t2c', (T2, T2, 0.6), (0, 0, 1.2 + CH + 13.1 + 0.3), (0, 0, AXIS)))
    # pyramidal cap + gold spire with star
    cap = cyl('cap', 4.6, 0.6, 3.2, (0, 0, 1.2 + CH + 13.7 + 1.6), verts=8)
    roofp.append(cap)
    gold.append(cyl('sp', 0.28, 0.02, 7.5, (0, 0, 1.2 + CH + 16.9 + 3.75), verts=8))
    st = cyl('star', 0.85, 0.85, 0.18, (0, 0, 1.2 + CH + 16.9 + 7.2), (math.pi / 2, 0, 0), verts=5)
    gold.append(st)

    # entrance doors under the portico
    for x in (-4, 0, 4):
        dx, dy = R(x, -D / 2 - 0.1)
        dark.append(cube('d', (2.6, 0.6, 4.2), (dx, dy, 1.2 + 2.1), (0, 0, AXIS)))

    joinparts(stucco, 'stucco')
    joinparts(stone, 'stone')
    joinparts(colsp, 'columns')
    joinparts(cornice, 'cornice')
    joinparts(roofp, 'roof')
    joinparts(glass, 'glass')
    joinparts(frame, 'frame')
    joinparts(gold, 'gold')
    joinparts(dark, 'dark')
    export('vip')
    shoot('vip')


# ---------------------------------------------------------------------------
# jetbridge — rotunda at origin, telescoping tunnel to -Y, cab at the end
# ---------------------------------------------------------------------------
def jetbridge():
    bs.reset()
    white, glass, frame, dark = [], [], [], []
    Z0 = 4.4                        # floor height at the terminal end
    Z1 = 3.4                        # floor height at the aircraft door
    # rotunda on its column
    rot = cyl('rot', 2.2, 2.2, 3.2, (0, 0, Z0 + 1.2), verts=14)
    smooth(rot)
    white.append(rot)
    white.append(cyl('rc', 0.5, 0.5, Z0, (0, 0, Z0 / 2), verts=10))
    # three telescoping sections, each narrower, sloping down toward -Y
    y = -2.0
    for i, w in enumerate((3.2, 2.9, 2.6)):
        ln = 6.5
        zc = Z0 + (Z1 - Z0) * ((y - ln / 2 + 2) / -18)
        sec = cube('s', (w, ln, w * 0.82), (0, y - ln / 2, zc + 0.9))
        sec.rotation_euler = ((Z1 - Z0) / 18, 0, 0)
        with bpy.context.temp_override(selected_objects=[sec], active_object=sec):
            bpy.ops.object.transform_apply(rotation=True)
        white.append(sec)
        # glass band along both sides
        for sx in (-1, 1):
            g = cube('g', (0.08, ln - 0.6, w * 0.4), (sx * w / 2, y - ln / 2, zc + 1.1))
            g.rotation_euler = ((Z1 - Z0) / 18, 0, 0)
            with bpy.context.temp_override(selected_objects=[g], active_object=g):
                bpy.ops.object.transform_apply(rotation=True)
            glass.append(g)
        y -= ln - 0.9
    # cab, angled to meet the fuselage
    cab = cube('cab', (3.4, 3.2, 2.9), (0, y - 1.2, Z1 + 1.25), (0, 0, 0.5))
    white.append(cab)
    dark.append(cube('cd', (2.2, 0.3, 2.2), (0, y - 2.6, Z1 + 1.2), (0, 0, 0.5)))
    # A-frame drive bogie with wheels
    my = y + 4
    for sx in (-1, 1):
        frame.append(cyl('leg', 0.22, 0.22, Z1 + 0.4, (sx * 1.1, my, (Z1 + 0.4) / 2), (0, sx * 0.18, 0), verts=8))
    frame.append(cube('ax', (2.6, 0.3, 0.3), (0, my, 0.55)))
    for sx in (-1, 1):
        dark.append(cyl('wh', 0.55, 0.55, 0.4, (sx * 1.35, my, 0.55), (0, math.pi / 2, 0), verts=12))
    joinparts(white, 'white')
    joinparts(glass, 'glass')
    joinparts(frame, 'frame')
    joinparts(dark, 'dark')
    export('jetbridge')
    shoot('jetbridge')


# ---------------------------------------------------------------------------
# hangar — arched roof, 64 x 60 m, segmented sliding doors on -Y
# ---------------------------------------------------------------------------
def hangar():
    bs.reset()
    W, D, HH = 62.0, 58.0, 19.0     # span / depth / crown height
    SPRING = 8.0                    # arch springs from the top of straight walls
    steel, doors, frame, dark = [], [], [], []

    def arch_z(x):
        """Roof height at x: straight wall to SPRING, elliptical arch above."""
        t = min(1.0, abs(x) / (W / 2))
        return SPRING + (HH - SPRING) * math.sqrt(max(0.0, 1 - t * t))

    seg = 14
    xs = [-W / 2 + W * i / seg for i in range(seg + 1)]
    # roof shell with thickness, plus filled back gable and front gable band
    verts, faces = [], []
    for x in xs:
        z = arch_z(x)
        for y in (-D / 2, D / 2):
            verts.append((x, y, z))
            verts.append((x, y, z + 0.7))
    for i in range(seg):
        b = i * 4
        faces += [(b + 0, b + 2, b + 6, b + 4), (b + 1, b + 5, b + 7, b + 3),
                  (b + 0, b + 4, b + 5, b + 1), (b + 2, b + 3, b + 7, b + 6)]
    me = bpy.data.meshes.new('shell')
    me.from_pydata([Vector(v) for v in verts], [], faces)
    sh = bs.link(bpy.data.objects.new('shell', me))
    smooth(sh)
    steel.append(sh)
    # straight side walls
    for sx in (-1, 1):
        steel.append(cube('sw', (1.0, D, SPRING), (sx * (W / 2 - 0.5), 0, SPRING / 2)))
    # back gable: filled wall following the arch (fan of quads down to ground)
    gv, gf = [], []
    for i, x in enumerate(xs):
        gv.append((x, D / 2 - 0.5, 0))
        gv.append((x, D / 2 - 0.5, arch_z(x)))
    for i in range(seg):
        gf.append((i * 2, i * 2 + 2, i * 2 + 3, i * 2 + 1))
    gm = bpy.data.meshes.new('gable')
    gm.from_pydata([Vector(v) for v in gv], [], gf)
    steel.append(bs.link(bpy.data.objects.new('gable', gm)))
    # front gable band above the door opening (doors are 15 m tall)
    DH = 15.0
    fv, ff = [], []
    for i, x in enumerate(xs):
        fv.append((x, -D / 2 + 0.5, min(DH, arch_z(x))))
        fv.append((x, -D / 2 + 0.5, arch_z(x)))
    for i in range(seg):
        ff.append((i * 2, i * 2 + 2, i * 2 + 3, i * 2 + 1))
    fm = bpy.data.meshes.new('fgable')
    fm.from_pydata([Vector(v) for v in fv], [], ff)
    steel.append(bs.link(bpy.data.objects.new('fgable', fm)))
    # segmented sliding doors filling the opening, with dark joint strips
    for i in range(8):
        x = -W / 2 + 4.5 + i * (W - 9) / 7
        h = min(DH, arch_z(x)) - 0.3
        doors.append(cube('dr', (6.8, 0.5, h), (x, -D / 2 + 1.0, h / 2)))
        if i < 7:
            dark.append(cube('ds', (0.4, 0.55, min(DH, arch_z(x + 3.9)) - 1),
                             (x + 3.9, -D / 2 + 1.0, (min(DH, arch_z(x + 3.9)) - 1) / 2)))
    frame.append(cube('rail', (W, 1.6, 0.4), (0, -D / 2 + 1.0, 0.2)))
    joinparts(steel, 'steel')
    joinparts(doors, 'doors')
    joinparts(frame, 'frame')
    joinparts(dark, 'dark')
    export('hangar')
    shoot('hangar')


# ---------------------------------------------------------------------------
# airliner — A320-class narrow-body, nose to -Y, static gear, ~37 m
# ---------------------------------------------------------------------------
def airliner():
    bs.reset()
    FL = 37.0                # fuselage length
    R = 2.0                  # fuselage radius
    ZC = 3.3                 # fuselage centreline height (on gear)
    white, gray, dark, red, engines = [], [], [], [], []

    # fuselage: cylinder + nose cone + tail cone (rotated to lie along Y)
    body = cyl('fu', R, R, FL * 0.62, (0, 0, ZC), (math.pi / 2, 0, 0), verts=20)
    smooth(body)
    white.append(body)
    nose = cyl('no', R, 0.35, FL * 0.16, (0, -FL * 0.39, ZC - 0.1), (math.pi / 2 + 0.06, 0, 0), verts=20)
    smooth(nose)
    white.append(nose)
    tail = cyl('ta', R, 0.28, FL * 0.24, (0, FL * 0.43, ZC + 0.55), (-math.pi / 2 + 0.16, 0, 0), verts=20)
    smooth(tail)
    white.append(tail)
    # nose cap
    cap = bs.ellipsoid('cap', (0, -FL * 0.47 - 0.1, ZC - 0.42), (0.36, 0.55, 0.36), seg=12)
    smooth(cap)
    white.append(cap)
    # cockpit windows: dark slanted band around the nose taper
    cwb = cyl('cw', 1.15, 0.85, 0.8, (0, -FL * 0.40, ZC - 0.18), (math.pi / 2 + 0.12, 0, 0), verts=16)
    smooth(cwb)
    dark.append(cwb)
    # cabin window stripe both sides
    for sx in (-1, 1):
        dark.append(cube('ws', (0.06, FL * 0.58, 0.34), (sx * R * 0.985, -FL * 0.02, ZC + 0.55)))
    # belly fairing
    belly = bs.ellipsoid('be', (0, FL * 0.02, ZC - R * 0.75), (R * 0.92, FL * 0.18, R * 0.42), seg=14)
    smooth(belly)
    gray.append(belly)

    def lift_surface(name, root, span, root_chord, tip_chord, sweep, dihedral,
                     thick, vertical=False):
        """Tapered swept wing/fin as an 8-vert box with explicit corners.
        root = (x, y, z) of root leading-edge midpoint; span along +x
        (or +z if vertical); sweep shifts the tip back (+y)."""
        rx, ry, rz = root
        pts = []
        for tip in (0, 1):
            ch = root_chord if not tip else tip_chord
            sw = 0 if not tip else sweep
            for cy in (0, 1):
                yy = ry + sw + ch * cy
                for ct in (0, 1):
                    if vertical:
                        pts.append((rx - thick / 2 + thick * ct, yy, rz + span * tip))
                    else:
                        pts.append((rx + span * tip, yy, rz + dihedral * tip - thick / 2 + thick * ct))
        faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6),
                 (0, 2, 6, 4), (1, 5, 7, 3)]
        me = bpy.data.meshes.new(name)
        me.from_pydata([Vector(p) for p in pts], [], faces)
        return bs.link(bpy.data.objects.new(name, me))

    # wings: swept, tapered, slight dihedral; engines hung under mid-span
    for sx in (-1, 1):
        gray.append(lift_surface('wg', (sx * 1.6, -FL * 0.06, ZC - 0.75),
                                 sx * 15.2, 5.8, 1.9, 6.6, 1.5, 0.4))
        # winglet at the swept-back tip
        tipy = -FL * 0.06 + 6.6
        red.append(lift_surface('wl', (sx * 16.7, tipy, ZC - 0.75 + 1.4),
                                2.6, 1.6, 0.9, 1.1, 0, 0.12, vertical=True))
        # engine nacelle + pylon under mid-span
        en = cyl('en', 1.05, 0.95, 3.4, (sx * 5.6, -FL * 0.05, ZC - 2.05), (math.pi / 2, 0, 0), verts=16)
        smooth(en)
        engines.append(en)
        dark.append(cyl('ei', 0.99, 0.99, 0.25, (sx * 5.6, -FL * 0.05 - 1.72, ZC - 2.05), (math.pi / 2, 0, 0), verts=16))
        gray.append(cube('py', (0.3, 2.4, 1.0), (sx * 5.6, -FL * 0.035, ZC - 1.2)))

    # tail: swept fin (red accent) + horizontal stabilizers on the tail cone
    red.append(lift_surface('fin', (0, FL * 0.36, ZC + 1.2), 6.6, 5.2, 2.2, 4.6, 0, 0.42, vertical=True))
    for sx in (-1, 1):
        gray.append(lift_surface('st', (sx * 0.8, FL * 0.40, ZC + 0.7),
                                 sx * 5.8, 3.0, 1.2, 3.0, 0.5, 0.22))

    # landing gear
    dark.append(cyl('ng', 0.14, 0.14, 1.6, (0, -FL * 0.33, ZC - R - 0.5), verts=8))
    for sx in (-1, 0, 1):
        if sx == 0:
            dark.append(cyl('nw', 0.42, 0.42, 0.3, (0, -FL * 0.33, 0.45), (0, math.pi / 2, 0), verts=12))
            continue
        dark.append(cyl('mg', 0.16, 0.16, 1.7, (sx * 2.4, FL * 0.05, ZC - R - 0.45), verts=8))
        for dy in (-0.5, 0.5):
            dark.append(cyl('mw', 0.5, 0.5, 0.36, (sx * 2.4, FL * 0.05 + dy, 0.5), (0, math.pi / 2, 0), verts=12))

    joinparts(white, 'fuselage')
    joinparts(gray, 'gray')
    joinparts(dark, 'dark')
    joinparts(red, 'accent')
    joinparts(engines, 'engines')
    export('airliner')
    shoot('airliner')


# ---------------------------------------------------------------------------
# apron props — each exported to its own GLB, origin at ground center
# ---------------------------------------------------------------------------
def mast():
    bs.reset()
    pole = [cyl('p', 0.35, 0.18, 26, (0, 0, 13), verts=10)]
    heads = [cube('h', (4.2, 0.5, 0.5), (0, 0, 25.6))]
    for x in (-1.6, -0.55, 0.55, 1.6):
        heads.append(cube('l', (0.8, 0.5, 0.35), (x, 0, 25.2)))
    joinparts(pole, 'steel')
    joinparts(heads, 'lamp')
    export('mast')


def windsock():
    bs.reset()
    joinparts([cyl('p', 0.06, 0.06, 6, (0, 0, 3), verts=8)], 'steel')
    cone = cyl('c', 0.42, 0.16, 2.6, (0, -1.5, 5.7), (math.pi / 2, 0, 0), verts=10)
    smooth(cone)
    joinparts([cone], 'sock')
    export('windsock')


def radar():
    bs.reset()
    steel = [cyl('l', 1.6, 1.0, 12, (0, 0, 6), verts=8)]
    for i in range(3):
        steel.append(cyl('br', 1.55 - i * 0.2, 1.55 - i * 0.2, 0.12, (0, 0, 3 + i * 4), verts=8))
    joinparts(steel, 'steel')
    d = cyl('d', 2.6, 2.6, 0.5, (0, 0, 12.8), (0.5, 0, 0), verts=16)
    smooth(d)
    joinparts([d], 'dish')
    export('radar')


def papi():
    bs.reset()
    boxes = []
    for i in range(4):
        boxes.append(cube('b', (0.9, 0.5, 0.5), (i * 9.0 - 13.5, 0, 0.55)))
        boxes.append(cyl('lg', 0.07, 0.07, 0.4, (i * 9.0 - 13.5, 0, 0.2), verts=6))
    joinparts(boxes, 'papi')
    export('papi')


def applight():
    bs.reset()
    f = [cube('bar', (4.2, 0.25, 0.25), (0, 0, 1.6)),
         cyl('l1', 0.1, 0.1, 1.6, (-1.8, 0, 0.8), verts=6),
         cyl('l2', 0.1, 0.1, 1.6, (1.8, 0, 0.8), verts=6)]
    joinparts(f, 'steel')
    lamps = [cyl('la', 0.16, 0.16, 0.3, (x, -0.1, 1.6), (math.pi / 2, 0, 0), verts=8) for x in (-1.5, -0.75, 0, 0.75, 1.5)]
    joinparts(lamps, 'lamp')
    export('applight')


def fueltruck():
    bs.reset()
    body, dark, white = [], [], []
    white.append(cube('cab', (2.4, 2.2, 2.1), (0, -3.6, 1.55)))
    dark.append(cube('cw', (2.2, 0.3, 0.8), (0, -4.6, 2.0)))
    tank = cyl('tk', 1.25, 1.25, 6.4, (0, 0.7, 1.9), (math.pi / 2, 0, 0), verts=14)
    smooth(tank)
    body.append(tank)
    for y in (-3.4, 0.2, 2.6):
        for sx in (-1, 1):
            dark.append(cyl('w', 0.55, 0.55, 0.35, (sx * 1.15, y, 0.55), (0, math.pi / 2, 0), verts=10))
    joinparts(body, 'tank')
    joinparts(white, 'cab')
    joinparts(dark, 'dark')
    export('fueltruck')


def bus():
    bs.reset()
    white = [cube('b', (2.5, 11.0, 2.6), (0, 0, 1.75))]
    glass = [cube('g', (2.54, 9.8, 1.0), (0, 0, 2.45)),
             cube('gf', (2.3, 0.3, 1.2), (0, -5.5, 2.2))]
    dark = []
    for y in (-3.6, 3.6):
        for sx in (-1, 1):
            dark.append(cyl('w', 0.5, 0.5, 0.3, (sx * 1.2, y, 0.5), (0, math.pi / 2, 0), verts=10))
    joinparts(white, 'body')
    joinparts(glass, 'glass')
    joinparts(dark, 'dark')
    export('bus')


def stairs():
    bs.reset()
    steel, white, dark = [], [], []
    white.append(cube('base', (2.2, 4.6, 1.2), (0, 1.4, 0.9)))
    n = 9
    for i in range(n):
        t = i / (n - 1)
        steel.append(cube('s', (1.6, 0.5, 0.12), (0, -0.8 - t * 3.4, 0.5 + t * 2.9)))
    for sx in (-1, 1):
        steel.append(cube('r', (0.08, 4.6, 0.1), (sx * 0.85, -2.5, 2.6), (0.65, 0, 0)))
    white.append(cube('top', (1.8, 1.6, 0.15), (0, -4.6, 3.5)))
    for y in (0.2, 2.6):
        for sx in (-1, 1):
            dark.append(cyl('w', 0.4, 0.4, 0.28, (sx * 1.0, y, 0.4), (0, math.pi / 2, 0), verts=10))
    joinparts(steel, 'steel')
    joinparts(white, 'body')
    joinparts(dark, 'dark')
    export('stairs')


def tug():
    bs.reset()
    body = [cube('b', (1.8, 3.0, 1.0), (0, 0, 0.8)), cube('c', (1.6, 1.2, 1.0), (0, -0.6, 1.7))]
    dark = [cyl('w', 0.4, 0.4, 0.3, (sx * 0.95, y, 0.42), (0, math.pi / 2, 0), verts=10)
            for sx in (-1, 1) for y in (-1.0, 1.0)]
    # two baggage carts hitched behind
    for i in range(2):
        y = 3.2 + i * 3.0
        body.append(cube('ct', (1.6, 2.4, 1.3), (0, y, 1.05)))
        for sx in (-1, 1):
            dark.append(cyl('cw', 0.3, 0.3, 0.24, (sx * 0.85, y + 0.8, 0.32), (0, math.pi / 2, 0), verts=8))
    joinparts(body, 'body')
    joinparts(dark, 'dark')
    export('tug')


ALL = {
    'terminal': terminal, 'tower': tower, 'vip': vip, 'jetbridge': jetbridge,
    'hangar': hangar, 'airliner': airliner, 'mast': mast, 'windsock': windsock,
    'radar': radar, 'papi': papi, 'applight': applight, 'fueltruck': fueltruck,
    'bus': bus, 'stairs': stairs, 'tug': tug,
}

for n, fn in ALL.items():
    if ONLY and n not in ONLY:
        continue
    fn()
print('DONE')
