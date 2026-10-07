# Tbilisi metro surface structures, sculpted in Blender:
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_metro_assets.py
# pavilion_arch — the Rustaveli/Marjanishvili-type grand stone arch hall;
# pavilion_hall — the later glass-box-under-concrete-slab type (Delisi…);
# entrance     — sunken stair portal with glass canopy;
# totem        — the M pylon. Named parts (stone/concrete/glass/frame/fascia/
# sign/steps/rail) get materials in src/metro.js. Real meter scale, origin at
# ground center, front facing -Y (→ three.js +z).
import math
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import build_statues as bs  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'metro')
SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')
os.makedirs(OUT, exist_ok=True)


def name_obj(o, n):
    o.name = n
    return o


def export(name):
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, f'{name}.glb'), export_format='GLB')
    print(f'EXPORT {name}')


def shoot(name):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x = 640
    sc.render.resolution_y = 520
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
    cam.location = c + Vector((0.9, -1.5, 0.5)).normalized() * dim * 1.6
    tgt = bs.link(bpy.data.objects.new('t', None))
    tgt.location = c
    tr = cam.constraints.new('TRACK_TO')
    tr.target = tgt
    tr.track_axis = 'TRACK_NEGATIVE_Z'
    tr.up_axis = 'UP_Y'
    sc.render.filepath = os.path.join(SHOTS, f'metro_{name}.png')
    bpy.ops.render.render(write_still=True)


def arch_band(name, r_in, r_out, depth, y, seg=20):
    """Half-ring arch (springline at z=z0) standing in the XZ plane."""
    verts = []
    faces = []
    z0 = 0.0
    for i in range(seg + 1):
        a = math.pi * i / seg
        ca, sa = math.cos(a), math.sin(a)
        for r in (r_in, r_out):
            for yy in (y - depth / 2, y + depth / 2):
                verts.append((r * ca, yy, z0 + r * sa))
    def idx(i, ring, side):
        return i * 4 + ring * 2 + side
    for i in range(seg):
        a0, a1 = i, i + 1
        faces.append((idx(a0, 0, 0), idx(a1, 0, 0), idx(a1, 0, 1), idx(a0, 0, 1)))  # inner
        faces.append((idx(a0, 1, 1), idx(a1, 1, 1), idx(a1, 1, 0), idx(a0, 1, 0)))  # outer
        faces.append((idx(a0, 0, 0), idx(a0, 1, 0), idx(a1, 1, 0), idx(a1, 0, 0)))  # front
        faces.append((idx(a1, 0, 1), idx(a1, 1, 1), idx(a0, 1, 1), idx(a0, 0, 1)))  # back
    me = bpy.data.meshes.new(name)
    me.from_pydata([Vector(v) for v in verts], [], faces)
    obj = bs.link(bpy.data.objects.new(name, me))
    for p in me.polygons:
        p.use_smooth = True
    return obj


def pavilion_arch():
    """Grand arched pavilion (Rustaveli type): stone arch, glazed lunette."""
    bs.reset()
    stone = []
    # hall behind the facade
    stone.append(bs.cube('hall', (17, 10, 5.6), (0, 1.0, 2.8)))
    stone.append(bs.cube('roofslab', (18.4, 11.4, 0.9), (0, 1.0, 6.0)))
    # flanking pylons + entablature over the arch
    for s in (-1, 1):
        stone.append(bs.cube('pylon', (3.2, 2.6, 8.2), (s * 7.6, -4.6, 4.1)))
    stone.append(bs.cube('entab', (18.4, 2.8, 1.6), (0, -4.5, 8.6)))
    stone.append(arch_band('arch', 4.9, 6.1, 2.6, -4.6))
    st = bs.join(stone)
    name_obj(st, 'stone')
    # glazed lunette + door band under the springline
    gl = [bs.cube('glz', (9.8, 0.4, 4.9), (0, -4.4, 2.45))]
    lun = arch_band('lun', 0.01, 4.85, 0.35, -4.35, seg=16)
    gl.append(lun)
    g = bs.join(gl)
    name_obj(g, 'glass')
    doors = bs.cube('doors', (7.6, 0.5, 3.0), (0, -4.55, 1.5))
    name_obj(doors, 'frame')
    steps = []
    for i in range(3):
        steps.append(bs.cube(f'st{i}', (12 - i * 0.0, 1.0, 0.18), (0, -6.2 - i * 1.0, 0.09 + (2 - i) * 0.18)))
    sp = bs.join(steps)
    name_obj(sp, 'steps')
    fas = bs.cube('fas', (10, 0.25, 1.15), (0, -5.95, 7.0))
    name_obj(fas, 'fascia')
    key = bs.cube('key', (1.1, 2.8, 1.8), (0, -4.6, 10.4))
    name_obj(key, 'stone2')
    sg = bs.cube('sgm', (2.2, 0.4, 2.2), (0, -4.4, 10.6))
    name_obj(sg, 'sign')
    shoot('pavilion_arch')
    export('pavilion_arch')


def pavilion_hall():
    """Late-Soviet glass hall under a heavy concrete slab (Delisi type)."""
    bs.reset()
    conc = [bs.cube('slab', (21, 13, 1.3), (0, 0, 5.4))]
    conc.append(bs.cube('slab2', (22.4, 14.4, 0.45), (0, 0, 6.2)))
    for sx in (-8.6, 0, 8.6):
        for sy in (-5.2, 5.2):
            conc.append(bs.cyl(f'c{sx}{sy}', 0.38, 0.42, 4.8, (sx, sy, 2.4), verts=10))
    conc.append(bs.cube('back', (19, 1.2, 4.8), (0, 5.6, 2.4)))
    co = bs.join(conc)
    bs.smooth(co)
    name_obj(co, 'concrete')
    core = bs.cube('core', (14, 8.6, 4.3), (0, 0.6, 2.15))
    name_obj(core, 'glass')
    doors = bs.cube('doors', (6.5, 0.4, 3.0), (0, -3.75, 1.5))
    name_obj(doors, 'frame')
    steps = []
    for i in range(3):
        steps.append(bs.cube(f'st{i}', (10, 0.9, 0.16), (0, -7.0 - i * 0.9, 0.08 + (2 - i) * 0.16)))
    sp = bs.join(steps)
    name_obj(sp, 'steps')
    fas = bs.cube('fas', (12, 0.25, 1.0), (0, -6.6, 5.4))
    name_obj(fas, 'fascia')
    mull = []
    for i in range(7):
        mull.append(bs.cube(f'mu{i}', (0.14, 9.0, 4.3), (-7 + i * 2.33, 0.6, 2.15)))
    mm = bs.join(mull)
    name_obj(mm, 'frame2')
    sg = bs.cube('sgm', (2.0, 0.4, 2.0), (0, -5.2, 7.4))
    name_obj(sg, 'sign')
    shoot('pavilion_hall')
    export('pavilion_hall')


def entrance():
    """Sunken stair portal with glass canopy and railings."""
    bs.reset()
    conc = []
    for s in (-1, 1):
        conc.append(bs.cube(f'par{s}', (0.45, 6.5, 1.0), (s * 2.3, 0, 0.5)))
    conc.append(bs.cube('head', (5.05, 0.45, 1.0), (0, 3.25, 0.5)))
    co = bs.join(conc)
    name_obj(co, 'concrete')
    # descending dark stair ramp
    ramp = bs.cube('ramp', (3.7, 6.0, 0.3), (0, 0, 0.15))
    ramp.data.transform(__import__('mathutils').Matrix.Rotation(math.radians(-16), 4, 'X'))
    ramp.data.transform(__import__('mathutils').Matrix.Translation((0, 0, -0.4)))
    name_obj(ramp, 'steps')
    can = [bs.cube('roofg', (5.4, 7.4, 0.18), (0, 0, 3.0))]
    cn = bs.join(can)
    name_obj(cn, 'glass')
    fr = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            fr.append(bs.cyl(f'p{sx}{sy}', 0.09, 0.09, 3.0, (sx * 2.4, sy * 3.3, 1.5), verts=8))
    for s in (-1, 1):
        fr.append(bs.cyl(f'r{s}', 0.05, 0.05, 6.4, (s * 2.05, 0, 1.05), rot=(math.pi / 2, 0, 0), verts=6))
    f = bs.join(fr)
    bs.smooth(f)
    name_obj(f, 'frame')
    shoot('entrance')
    export('entrance')


def totem():
    """The M pylon by every entrance."""
    bs.reset()
    py = bs.cube('pyl', (0.5, 0.35, 3.6), (0, 0, 1.8))
    name_obj(py, 'pylon')
    sg = bs.cube('sg', (1.35, 0.3, 1.35), (0, 0, 4.3))
    name_obj(sg, 'sign')
    shoot('totem')
    export('totem')


def liberty():
    """Liberty Square: the Galleria glass front — wide curtain wall, deep
    canopy, signage band with the red M, broad steps down to the square."""
    bs.reset()
    # glass curtain wall
    gl = bs.cube('gw', (34, 0.5, 8.2), (0, 1.2, 4.4))
    name_obj(gl, 'glass')
    # dark mullion grid + doors + side returns
    fr = []
    for i in range(13):
        fr.append(bs.cube(f'm{i}', (0.18, 0.7, 8.2), (-16.5 + i * 2.75, 1.2, 4.4)))
    fr.append(bs.cube('band', (34, 0.7, 0.25), (0, 1.2, 3.0)))
    for s2 in (-1, 1):
        fr.append(bs.cube(f'ret{s2}', (0.6, 4.2, 8.6), (s2 * 17.2, 3.0, 4.3)))
    fr.append(bs.cube('doors', (9, 0.55, 3.1), (0, 1.0, 1.55)))
    f = bs.join(fr)
    name_obj(f, 'frame')
    # signage fascia + projecting canopy
    fas = bs.cube('fas', (30, 0.3, 2.0), (0, 0.75, 6.9))
    name_obj(fas, 'fascia')
    con = [bs.cube('canopy', (35, 3.4, 0.5), (0, -1.2, 8.9))]
    con.append(bs.cube('roof', (34.5, 5.0, 0.6), (0, 3.4, 8.4)))
    c = bs.join(con)
    name_obj(c, 'concrete')
    st = []
    for i in range(4):
        st.append(bs.cube(f's{i}', (30, 1.1, 0.17), (0, -2.2 - i * 1.1, 0.085 + (3 - i) * 0.17)))
    sp = bs.join(st)
    name_obj(sp, 'steps')
    shoot('liberty')
    export('liberty')


def galleria():
    """Galleria Tbilisi mall: curved dark roof shell over a glass block,
    white floor bands, ground-floor metro entrance with signage + canopy."""
    bs.reset()
    L, W2, H = 126, 40, 24
    gl = bs.cube('body', (L, W2, H), (0, 0, H / 2))
    name_obj(gl, 'glass')
    bands = []
    for i in range(1, 7):
        bands.append(bs.cube(f'b{i}', (L + 1.2, W2 + 1.2, 0.55), (0, 0, i * 3.6)))
    bb = bs.join(bands)
    name_obj(bb, 'concrete')
    # sweeping curved roof: half-cylinder vault along the long axis
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cylinder_add(vertices=36, radius=W2 / 2 + 2, depth=L + 4, location=(0, 0, H))
    roof = bpy.context.object
    roof.rotation_euler = (0, __import__('math').pi / 2, 0)
    bpy.ops.object.transform_apply(location=True, rotation=True)
    cut = bs.cube('cut', (L + 10, W2 + 12, W2), (0, 0, H - W2 / 2))
    mod = roof.modifiers.new('cut', 'BOOLEAN')
    mod.operation = 'DIFFERENCE'
    mod.object = cut
    bs.freeze(roof)
    bpy.data.objects.remove(cut, do_unlink=True)
    bs.smooth(roof)
    name_obj(roof, 'roof')
    # ground-floor metro front centered on the long face
    fr = [bs.cube('rec', (36, 1.2, 5.2), (0, -W2 / 2 - 0.3, 2.6))]
    fr.append(bs.cube('drs', (10, 1.6, 3.2), (0, -W2 / 2 - 0.5, 1.6)))
    f = bs.join(fr)
    name_obj(f, 'frame')
    can = bs.cube('can', (38, 4.5, 0.5), (0, -W2 / 2 - 2.6, 5.6))
    name_obj(can, 'concrete2')
    fas = bs.cube('fas', (30, 0.3, 1.9), (0, -W2 / 2 - 1.1, 7.0))
    name_obj(fas, 'fascia')
    sg = bs.cube('sgm', (2.6, 0.5, 2.6), (0, -W2 / 2 - 0.9, 9.6))
    name_obj(sg, 'sign')
    st = []
    for i in range(3):
        st.append(bs.cube(f's{i}', (32, 1.0, 0.17), (0, -W2 / 2 - 4.6 - i, 0.085 + (2 - i) * 0.17)))
    sp = bs.join(st)
    name_obj(sp, 'steps')
    shoot('galleria')
    export('galleria')


def rustaveli():
    """Rustaveli station (photo-modeled): flat-roof modernist pavilion with the
    carved stone relief mural over the recessed door band, heavy right pier,
    projecting slab roof carrying the red M and blue რუსთაველი letters."""
    bs.reset()
    # relief mural wall: displaced panel reads as the carved sunburst stone
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_grid_add(x_subdivisions=48, y_subdivisions=24, size=1, location=(-2.5, -6.9, 5.6))
    rel = bpy.context.object
    rel.scale = (17, 4.4, 1)
    rel.rotation_euler = (__import__('math').pi / 2, 0, 0)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    tex = bpy.data.textures.new('carve', 'CLOUDS')
    tex.noise_scale = 0.9
    mod = rel.modifiers.new('d', 'DISPLACE')
    mod.texture = tex
    mod.strength = 0.5
    bs.freeze(rel)
    bs.smooth(rel)
    name_obj(rel, 'relief')
    # main body + right pier + side wall
    st = [bs.cube('body', (20, 6.5, 8.0), (-1.5, -3.2, 4.0))]
    st.append(bs.cube('pier', (3.4, 7.8, 8.6), (10.6, -3.4, 4.3)))
    st.append(bs.cube('sidew', (2.2, 7.8, 7.6), (-12.0, -3.4, 3.8)))
    s2 = bs.join(st)
    name_obj(s2, 'stone')
    # recessed dark door band with glass doors
    fr = [bs.cube('band', (15.5, 0.6, 3.1), (-2.5, -6.6, 1.55))]
    f = bs.join(fr)
    name_obj(f, 'frame')
    gl = bs.cube('drs', (13.5, 0.5, 2.7), (-2.5, -6.75, 1.35))
    name_obj(gl, 'glass')
    # thin projecting slab roof
    con = [bs.cube('slab', (26.5, 12.5, 0.7), (-0.7, -2.6, 8.6))]
    c = bs.join(con)
    name_obj(c, 'concrete')
    # roof signage: big M cube + letter plate along the roof edge
    sg = bs.cube('sgm', (4.0, 0.55, 4.0), (-9.2, -7.7, 11.0))
    name_obj(sg, 'sign')
    fas = bs.cube('fas', (16.0, 0.35, 2.2), (2.2, -7.95, 10.1))
    name_obj(fas, 'fascia')
    # plaza steps + hedge planters
    stp = []
    for i in range(2):
        stp.append(bs.cube(f's{i}', (22, 1.0, 0.16), (-1.5, -8.0 - i, 0.08 + (1 - i) * 0.16)))
    sp = bs.join(stp)
    name_obj(sp, 'steps')
    hg = [bs.cube('h1', (6, 1.6, 1.1), (-8, -10.6, 0.55)), bs.cube('h2', (6, 1.6, 1.1), (5, -10.6, 0.55))]
    hh = bs.join(hg)
    name_obj(hh, 'hedge')
    shoot('rustaveli')
    export('rustaveli')


def marjanishvili():
    """Marjanishvili: metro built into a cream classical block — rusticated
    stone ground floor, tall dark-glass door bays between piers, blue name
    frieze. Back embeds in the OSM block (it IS the ground floor)."""
    bs.reset()
    W = 22
    # cream upper wall (the building face above the shopfront)
    up = bs.cube('up', (W, 1.0, 6.0), (0, -3.0, 9.0))
    name_obj(up, 'cream')
    # rusticated ashlar band with pier rhythm
    st = [bs.cube('sill', (W, 1.4, 0.6), (0, -3.3, 0.3))]
    npier = 6
    for i in range(npier + 1):
        st.append(bs.cube(f'p{i}', (1.3, 1.5, 5.6), (-W/2 + i * (W/npier), -3.3, 3.0)))
    st.append(bs.cube('lintel', (W, 1.5, 0.7, ), (0, -3.3, 6.1)))
    ss = bs.join(st)
    name_obj(ss, 'ashlar')
    # dark recessed glass doors between piers
    gl = bs.cube('glz', (W - 2.0, 0.6, 4.9), (0, -2.9, 3.2))
    name_obj(gl, 'glass')
    fr = bs.cube('drs', (W - 3.2, 0.5, 2.9), (0, -3.0, 1.55))
    name_obj(fr, 'frame')
    # blue name frieze on the cream band
    fas = bs.cube('fas', (17, 0.3, 1.4), (0, -3.55, 6.9))
    name_obj(fas, 'fascia')
    sp = bs.cube('s0', (W, 1.0, 0.16), (0, -4.2, 0.08))
    name_obj(sp, 'steps')
    shoot('marjanishvili')
    export('marjanishvili')


def avlabari():
    """Avlabari: cream modernist ground-floor front, magenta M, blue name,
    a planted green band above the dark-glass entrance."""
    bs.reset()
    W = 18
    up = bs.cube('up', (W, 1.0, 3.0), (0, -3.0, 9.5))
    name_obj(up, 'cream')
    # green planted band (vines) over the entrance
    gb = bs.cube('green', (W, 1.2, 2.2), (0, -3.2, 7.4))
    name_obj(gb, 'hedge')
    body = bs.cube('body', (W, 1.4, 6.0), (0, -3.4, 3.0))
    name_obj(body, 'cream')
    gl = bs.cube('glz', (W - 1.4, 0.6, 5.0), (0, -3.0, 3.2))
    name_obj(gl, 'glass')
    fr = bs.cube('drs', (W - 2.6, 0.5, 3.0), (0, -3.1, 1.6))
    name_obj(fr, 'frame')
    sg = bs.cube('sgm', (2.4, 0.45, 2.4), (-6.0, -3.7, 7.6))
    name_obj(sg, 'sign')
    fas = bs.cube('fas', (10, 0.3, 1.5), (2.5, -3.7, 7.7))
    name_obj(fas, 'fascia')
    sp = bs.cube('s0', (W, 1.0, 0.16), (0, -4.4, 0.08))
    name_obj(sp, 'steps')
    shoot('avlabari')
    export('avlabari')


def station_square():
    """Station Square: brutalist terminal — a tall block whose floor slabs
    cantilever outward in horizontal louver bands over a recessed columned
    ground floor with the corner M + STATION SQUARE sign and a clock."""
    bs.reset()
    L, D = 88, 24
    conc = []
    # recessed glazed core on pilotis
    core = bs.cube('core', (L - 6, D - 6, 5.0), (0, 0, 2.5))
    name_obj(core, 'glass')
    for i in range(9):
        conc.append(bs.cyl(f'col{i}', 0.55, 0.6, 5.0, (-L/2 + 5 + i * (L-10)/8, -(D/2) + 1.5, 2.5), verts=10))
    # cantilevered floor slabs: each upper slab a touch wider (brise-soleil)
    for f in range(11):
        z = 6.2 + f * 2.4
        proj = 0.4 + f * 0.14
        conc.append(bs.cube(f'sl{f}', (L + proj * 2, D + proj * 2, 1.5), (0, 0, z)))
        # dark window band recessed behind each slab
    cc = bs.join(conc)
    bs.smooth(cc)
    name_obj(cc, 'concrete')
    # dark glazing bands between the slabs
    win = []
    for f in range(10):
        z = 7.6 + f * 2.4
        win.append(bs.cube(f'w{f}', (L - 2, D - 2, 1.0), (0, 0, z)))
    ww = bs.join(win)
    name_obj(ww, 'winband')
    # corner entrance fascia: white band with M + STATION SQUARE + clock
    fr = bs.cube('band', (L - 4, 1.0, 2.6), (0, -D/2 - 0.6, 5.4))
    name_obj(fr, 'fascia')
    sg = bs.cube('sgm', (3.4, 0.5, 3.4), (-L/2 + 16, -D/2 - 1.0, 6.4))
    name_obj(sg, 'sign')
    clk = bs.cyl('clk', 1.1, 1.1, 0.4, (0, -D/2 - 1.0, 8.0), rot=(1.5708, 0, 0), verts=16)
    bs.smooth(clk)
    name_obj(clk, 'clock')
    shoot('station_square')
    export('station_square')


for fn in (pavilion_arch, pavilion_hall, entrance, totem, liberty, galleria, rustaveli, marjanishvili, avlabari, station_square):
    fn()
print('METRO ASSETS DONE')
