"""Blender-headless: Tbilisi street & river infrastructure kit -> one glb.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_infra_kit.py

Objects (names matter — citymap.js/bridges.js look them up):
  quay        4 m river embankment wall: battered concrete face, coping stone,
              a weep pipe and a rusted mooring ring — the Mtkvari runs between
              walls like this through the whole centre
  quay_step   the same wall with a flight of steps down to the water
  riprap      2 m of tumbled bank stone for the un-walled reaches
  kerb        3 m granite kerb + gutter channel, the piece every street needs
  kerb_ramp   dropped kerb for crossings
  crossing    zebra crossing plate (6 bars) laid on the asphalt
  trafficlight  3-head signal on a mast with a pedestrian head
  signpost    two-plate street sign on a pole
  guardrail   3 m steel W-beam barrier on posts
  busstop     glazed shelter with bench and a totem
  manhole     cast-iron cover
  bollard     cast bollard
  pylon_lamp  twin-arm avenue lamp (taller than the residential one)

Convention: real metres, Z up, origin at the piece's ground contact, the FRONT
face looking down -Y so after export_yup it faces +Z in three.js (same as the
facade kit). Everything is a plain mesh with a named material.
"""
import math
import os

import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                   'public', 'models', 'infra_kit.glb')

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
    return m


M_CONC = mat('inf_concrete', '#9a978f')
M_CONC_D = mat('inf_concrete_dark', '#7d7a72')
M_STONE = mat('inf_stone', '#8e8377')
M_GRANITE = mat('inf_granite', '#6f6c68', rough=0.75)
M_ASPHALT = mat('inf_asphalt', '#3a3a3c', rough=0.95)
M_PAINT = mat('inf_paint', '#eceae2', rough=0.7)
M_STEEL = mat('inf_steel', '#b9bdc0', rough=0.45, metal=0.7)
M_RUST = mat('inf_rust', '#8a5b3c', rough=0.8, metal=0.2)
M_DARK = mat('inf_dark', '#2b2e31', rough=0.5, metal=0.3)
M_GLASS = mat('inf_glass', '#22303a', rough=0.15, metal=0.35)
M_RED = mat('inf_red', '#c0392b', rough=0.6)
M_AMBER = mat('inf_amber', '#e8a33d', rough=0.6)
M_GREEN = mat('inf_green', '#2f8f4e', rough=0.6)
M_BLUE = mat('inf_blue', '#20518f', rough=0.6)


def box(name, w, d, h, x=0.0, y=0.0, z=0.0, material=None, rot=None):
    """w = along X, d = along Y (depth), h = along Z (up); z is the CENTRE."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=(x, y, z))
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = (w, d, h)
    if rot:
        ob.rotation_euler = rot
    bpy.ops.object.transform_apply(scale=True, rotation=bool(rot))
    if material:
        ob.data.materials.append(material)
    return ob


def cyl(name, r, h, x=0.0, y=0.0, z=0.0, verts=12, material=None, rot=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r, depth=h,
                                        location=(x, y, z))
    ob = bpy.context.active_object
    ob.name = name
    if rot:
        ob.rotation_euler = rot
    bpy.ops.object.transform_apply(rotation=bool(rot))
    if material:
        ob.data.materials.append(material)
    return ob


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


# ---- quay: 4 m embankment wall, 3 m long -----------------------------------
# origin at the wall's TOP-BACK edge, at ground level: the wall drops to the
# water in -Z and its face looks toward -Y (the river).
def quay_parts(with_steps=False):
    p = [
        # battered face (slightly leaning back), in two courses
        box('face', 3.0, 0.55, 2.4, y=-0.28, z=-1.25, material=M_CONC),
        box('face2', 3.0, 0.42, 1.8, y=-0.22, z=-3.0, material=M_CONC_D),
        box('back', 3.0, 0.9, 4.2, y=0.45, z=-2.1, material=M_CONC_D),
        # coping stone along the top, proud of the face
        box('coping', 3.0, 1.05, 0.28, y=-0.32, z=0.14, material=M_STONE),
        # a shadow reveal between the courses
        box('reveal', 3.0, 0.06, 0.09, y=-0.53, z=-2.42, material=M_CONC_D),
    ]
    # weep pipe + rust stain
    p.append(cyl('weep', 0.09, 0.4, x=-0.9, y=-0.5, z=-2.0, verts=8,
                 material=M_RUST, rot=(math.pi / 2, 0, 0)))
    # mooring ring
    p.append(cyl('ring', 0.16, 0.05, x=1.0, y=-0.58, z=-0.7, verts=12,
                 material=M_RUST, rot=(math.pi / 2, 0, 0)))
    if with_steps:
        for i in range(7):
            p.append(box(f'step{i}', 1.6, 0.34, 0.18,
                         y=-0.55 - i * 0.30, z=-0.45 - i * 0.5, material=M_STONE))
        p.append(box('steprail', 0.06, 2.4, 0.06, x=0.85, y=-1.6, z=0.35,
                     material=M_STEEL))
    return p


join('quay', quay_parts(False))
join('quay_step', quay_parts(True))

# ---- riprap: 2 m of tumbled bank stone -------------------------------------
parts = []
for i in range(14):
    a = i * 2.399
    parts.append(box(f'rock{i}', 0.5 + 0.25 * ((i * 7) % 4), 0.45 + 0.2 * ((i * 5) % 3),
                     0.35 + 0.2 * ((i * 3) % 4),
                     x=-0.9 + (i % 5) * 0.45, y=-0.35 + ((i // 5) % 3) * 0.42,
                     z=-0.1 - ((i * 11) % 3) * 0.12, material=M_STONE,
                     rot=(0.2 * math.sin(a), 0.25 * math.cos(a), a)))
join('riprap', parts)

# ---- kerb: 3 m granite kerb + gutter ---------------------------------------
parts = [
    box('kerbstone', 3.0, 0.30, 0.34, z=-0.02, material=M_GRANITE),
    box('gutter', 3.0, 0.42, 0.06, y=-0.36, z=-0.16, material=M_CONC),
    box('pave', 3.0, 0.9, 0.10, y=0.60, z=-0.06, material=M_CONC),
]
join('kerb', parts)

parts = [
    box('rampstone', 3.0, 0.30, 0.16, z=-0.11, material=M_GRANITE),
    box('ramp', 3.0, 0.9, 0.08, y=0.60, z=-0.13, material=M_CONC),
]
join('kerb_ramp', parts)

# ---- crossing: 6 zebra bars on a 4 m plate ---------------------------------
parts = []
for i in range(6):
    parts.append(box(f'bar{i}', 0.45, 3.6, 0.02, x=-1.75 + i * 0.7, z=0.01,
                     material=M_PAINT))
join('crossing', parts)

# ---- traffic light ---------------------------------------------------------
parts = [
    cyl('base', 0.16, 0.22, z=0.11, verts=10, material=M_DARK),
    cyl('mast', 0.075, 4.2, z=2.2, verts=10, material=M_DARK),
    box('arm', 2.4, 0.09, 0.09, x=1.2, z=4.2, material=M_DARK),
    box('head', 0.34, 0.28, 0.95, x=2.3, z=3.85, material=M_DARK),
    box('visor', 0.40, 0.10, 0.06, x=2.3, y=-0.17, z=4.25, material=M_DARK),
    box('phead', 0.30, 0.26, 0.62, y=-0.16, z=2.55, material=M_DARK),
]
for i, m in enumerate((M_RED, M_AMBER, M_GREEN)):
    parts.append(cyl(f'lamp{i}', 0.10, 0.06, x=2.3, y=-0.15, z=4.15 - i * 0.29,
                     verts=10, material=m, rot=(math.pi / 2, 0, 0)))
for i, m in enumerate((M_RED, M_GREEN)):
    parts.append(cyl(f'plamp{i}', 0.085, 0.05, y=-0.30, z=2.72 - i * 0.28,
                     verts=10, material=m, rot=(math.pi / 2, 0, 0)))
join('trafficlight', parts)

# ---- signpost: two plates on a pole ----------------------------------------
parts = [
    cyl('pole', 0.045, 2.6, z=1.3, verts=8, material=M_STEEL),
    box('plate1', 1.1, 0.04, 0.22, x=0.5, z=2.35, material=M_BLUE),
    box('plate2', 0.9, 0.04, 0.20, x=0.4, z=2.05, material=M_PAINT),
]
join('signpost', parts)

# ---- guardrail: 3 m W-beam -------------------------------------------------
parts = [
    box('beam', 3.0, 0.10, 0.16, z=0.62, material=M_STEEL),
    box('beam2', 3.0, 0.06, 0.05, y=-0.07, z=0.62, material=M_STEEL),
    box('post', 0.12, 0.12, 0.75, x=-1.4, z=0.37, material=M_STEEL),
    box('post2', 0.12, 0.12, 0.75, x=1.4, z=0.37, material=M_STEEL),
]
join('guardrail', parts)

# ---- bus stop: glazed shelter ----------------------------------------------
parts = [
    box('roof', 4.2, 1.7, 0.10, z=2.45, y=0.1, material=M_DARK),
    box('back', 4.0, 0.05, 1.9, y=0.80, z=1.45, material=M_GLASS),
    box('sideL', 0.05, 1.6, 1.9, x=-2.0, z=1.45, material=M_GLASS),
    box('sideR', 0.05, 1.6, 1.9, x=2.0, z=1.45, material=M_GLASS),
    box('bench', 3.4, 0.42, 0.08, y=0.55, z=0.46, material=M_DARK),
    box('benchleg1', 0.08, 0.4, 0.45, x=-1.5, y=0.55, z=0.22, material=M_DARK),
    box('benchleg2', 0.08, 0.4, 0.45, x=1.5, y=0.55, z=0.22, material=M_DARK),
    cyl('totem', 0.09, 2.6, x=2.4, z=1.3, verts=8, material=M_DARK),
    box('totemplate', 0.5, 0.06, 0.8, x=2.4, z=2.1, material=M_BLUE),
]
for i in range(4):  # roof beams
    parts.append(box(f'beam{i}', 0.07, 1.6, 0.07, x=-1.8 + i * 1.2, y=0.1, z=2.36,
                     material=M_DARK))
join('busstop', parts)

# ---- manhole + bollard ------------------------------------------------------
join('manhole', [
    cyl('cover', 0.36, 0.05, z=0.02, verts=16, material=M_DARK),
    cyl('rim', 0.42, 0.03, z=0.005, verts=16, material=M_CONC_D),
])
join('bollard', [
    cyl('bshaft', 0.09, 0.9, z=0.45, verts=10, material=M_DARK),
    cyl('bcap', 0.11, 0.08, z=0.92, verts=10, material=M_DARK),
])

# ---- avenue lamp: twin arms -------------------------------------------------
parts = [
    cyl('lbase', 0.19, 0.4, z=0.2, verts=10, material=M_DARK),
    cyl('lmast', 0.09, 8.5, z=4.4, verts=10, material=M_DARK),
]
for s in (-1, 1):
    parts.append(box(f'larm{s}', 1.5, 0.09, 0.09, x=0.75 * s, z=8.5, material=M_DARK))
    parts.append(box(f'lhead{s}', 0.7, 0.30, 0.14, x=1.45 * s, z=8.42, material=M_DARK))
    parts.append(box(f'llamp{s}', 0.6, 0.24, 0.05, x=1.45 * s, z=8.33, material=M_PAINT))
join('pylon_lamp', parts)

# ---- tunnel_portal: headwall with the bore cut through it ------------------
# Authored for a 6 m half-width bore springing at 4.5 m (the shape citymap
# sweeps). The opening is formed from real geometry — two piers and an arch of
# voussoir blocks — so the wall reads as a face the road disappears into,
# instead of a concrete tube sitting on the grass. citymap scales it by hw/6.
HW, SPRING = 6.0, 4.5
parts = [
    box('pier_l', 3.0, 1.6, SPRING + 1.0, x=-(HW + 1.5), z=(SPRING + 1.0) / 2, material=M_CONC),
    box('pier_r', 3.0, 1.6, SPRING + 1.0, x=(HW + 1.5), z=(SPRING + 1.0) / 2, material=M_CONC),
]
# voussoirs following the arch, plus the spandrel above them
SEG = 14
for i in range(SEG + 1):
    a = math.pi * i / SEG
    x = HW * math.cos(a)
    y = SPRING + HW * math.sin(a)
    parts.append(box(f'vous{i}', 1.5, 1.7, 1.1, x=x, z=y,
                     material=M_STONE, rot=(0, -a + math.pi / 2, 0)))
# spandrel fill above the crown and out to the piers
parts.append(box('spandrel', 2 * (HW + 1.6), 1.5, 1.0, z=SPRING + HW + 1.1, material=M_CONC))
parts.append(box('coping', 2 * (HW + 1.9), 1.9, 0.30, z=SPRING + HW + 1.75, material=M_STONE))
# wing walls flaring back into the cutting
for sgn in (-1, 1):
    parts.append(box(f'wing{sgn}', 0.9, 6.0, SPRING, x=sgn * (HW + 2.6), y=2.9,
                     z=SPRING / 2, material=M_CONC_D, rot=(0, 0, sgn * 0.22)))
# a lit sign band over the opening
parts.append(box('portal_sign', 3.2, 0.25, 0.7, z=SPRING + HW + 0.55, y=-0.85,
                 material=M_BLUE))
join('tunnel_portal', parts)


# ===== BRIDGE PIECES, modelled from photographs of the real bridges =========
# refs viewed: Metekhi bridge (Feb 2012) — ornate cast-iron balustrade with
# scroll panels + candelabra lamp columns; Dry Bridge — red brick spandrel with
# a pale stone voussoir ring and a plain steel rail; Queen Tamar — coursed
# stone arch bridge, tall slightly-pointed arches, RIBBED piers, deep fascia
# with a dentil course, dark ornate rail on top.

M_IRON = mat('inf_iron', '#3c4044', rough=0.55, metal=0.35)
M_BRICK = mat('inf_brick', '#9c5a44', rough=0.9)
M_BRASS = mat('inf_brass', '#b98f3e', rough=0.4, metal=0.7)
M_GLOBE = mat('inf_globe', '#f2ead6', rough=0.35)

# ---- bal_iron: 3 m ornate cast-iron balustrade (Metekhi / Baratashvili) ----
parts = [
    box('toprail', 3.0, 0.13, 0.11, z=0.98, material=M_IRON),
    box('botrail', 3.0, 0.09, 0.07, z=0.16, material=M_IRON),
    box('newelL', 0.20, 0.20, 1.12, x=-1.45, z=0.56, material=M_IRON),
    box('newelR', 0.20, 0.20, 1.12, x=1.45, z=0.56, material=M_IRON),
    box('newelcapL', 0.26, 0.26, 0.10, x=-1.45, z=1.16, material=M_IRON),
    box('newelcapR', 0.26, 0.26, 0.10, x=1.45, z=1.16, material=M_IRON),
]
# scroll panel: a ring flanked by uprights, repeated 3x per 3 m bay
for b in range(3):
    bx = -0.9 + b * 0.9
    parts.append(cyl(f'ring{b}', 0.26, 0.05, x=bx, z=0.57, verts=14,
                     material=M_IRON, rot=(math.pi / 2, 0, 0)))
    parts.append(cyl(f'ringi{b}', 0.17, 0.055, x=bx, z=0.57, verts=14,
                     material=M_IRON, rot=(math.pi / 2, 0, 0)))
    for sx in (-1, 1):
        parts.append(box(f'bar{b}{sx}', 0.045, 0.045, 0.82, x=bx + sx * 0.42,
                         z=0.57, material=M_IRON))
join('bal_iron', parts)

# ---- bal_spindle: embankment balustrade, turned spindles on a low wall -----
parts = [
    box('plinth', 3.0, 0.26, 0.16, z=0.08, material=M_STONE),
    box('handrail', 3.0, 0.24, 0.12, z=1.02, material=M_STONE),
]
for i in range(9):
    parts.append(cyl(f'sp{i}', 0.055, 0.78, x=-1.35 + i * 0.34, z=0.55, verts=8,
                     material=M_STONE))
join('bal_spindle', parts)

# ---- bal_steel: plain vertical-bar rail (Dry Bridge, modern spans) ---------
parts = [
    box('srail', 3.0, 0.07, 0.07, z=1.02, material=M_IRON),
    box('srail2', 3.0, 0.05, 0.05, z=0.52, material=M_IRON),
    box('spost1', 0.09, 0.09, 1.05, x=-1.45, z=0.52, material=M_IRON),
    box('spost2', 0.09, 0.09, 1.05, x=1.45, z=0.52, material=M_IRON),
]
for i in range(14):
    parts.append(box(f'sbar{i}', 0.03, 0.03, 0.95, x=-1.32 + i * 0.203, z=0.5,
                     material=M_IRON))
join('bal_steel', parts)

# ---- lamp_candelabra: the ornate Metekhi lamp column -----------------------
parts = [
    box('lbase2', 0.52, 0.52, 0.30, z=0.15, material=M_IRON),
    box('lbase3', 0.40, 0.40, 0.22, z=0.40, material=M_IRON),
    cyl('lcol', 0.10, 3.5, z=2.25, verts=12, material=M_IRON),
    cyl('lcollar', 0.16, 0.12, z=3.95, verts=12, material=M_IRON),
]
# five globes: one on the crown, four on scrolled arms
parts.append(cyl('globeC', 0.17, 0.34, z=4.62, verts=12, material=M_GLOBE))
for i in range(4):
    a = i * math.pi / 2 + math.pi / 4
    ax, ay = math.cos(a) * 0.42, math.sin(a) * 0.42
    parts.append(box(f'arm{i}', 0.06, 0.06, 0.55, x=ax * 0.6, y=ay * 0.6, z=4.18,
                     material=M_IRON, rot=(ay * 0.5, -ax * 0.5, 0)))
    parts.append(cyl(f'globe{i}', 0.14, 0.28, x=ax, y=ay, z=4.36, verts=10,
                     material=M_GLOBE))
parts.append(cyl('finial', 0.05, 0.34, z=4.95, verts=8, material=M_BRASS))
join('lamp_candelabra', parts)

# ---- arch_rib: pier rib/pilaster of a stone arch bridge (Queen Tamar) ------
parts = [
    box('rib', 1.1, 1.3, 6.0, z=-3.0, material=M_STONE),
    box('ribcap', 1.35, 1.5, 0.35, z=0.05, material=M_STONE),
    box('ribfoot', 1.3, 1.5, 0.5, z=-6.2, material=M_CONC_D),
]
join('arch_rib', parts)

# ---- deck_fascia: deep edge band with a dentil course under the deck -------
parts = [
    box('fascia', 3.0, 0.35, 0.95, z=-0.5, material=M_STONE),
    box('fcornice', 3.0, 0.50, 0.18, z=0.03, material=M_STONE),
]
for i in range(6):
    parts.append(box(f'dentil{i}', 0.18, 0.42, 0.16, x=-1.25 + i * 0.5, z=-0.15,
                     material=M_STONE))
join('deck_fascia', parts)

# ---- arch_ring: pale stone voussoir ring (Dry Bridge over a road) ----------
parts = []
RR = 5.0
for i in range(15):
    a = math.pi * i / 14
    parts.append(box(f'vr{i}', 1.2, 1.5, 0.9, x=RR * math.cos(a), z=RR * math.sin(a),
                     material=M_STONE, rot=(0, -a + math.pi / 2, 0)))
join('arch_ring', parts)


# ---- cliff: the Mtkvari's natural rock bank (Metekhi / Old Tbilisi) --------
# Reference: "Mtkvari River Cruise" and "Mtkvari Cliff Tbilisi" — through the
# old town the river does NOT run between concrete quays, it runs in a gorge
# with a buff-grey layered rock face, vegetation rooted in the cracks and the
# houses standing right on the lip. A concrete wall there was simply wrong.
M_ROCK = mat('inf_rock', '#93866f', rough=0.95)
M_ROCK_D = mat('inf_rock_dark', '#6e6353', rough=0.95)
M_SCRUB = mat('inf_scrub', '#5f7345', rough=0.95)

parts = []
# stacked strata, each course jittered so the face reads as bedding planes
for i in range(7):
    h = 1.5
    zc = -0.75 - i * 1.45
    jut = 0.10 + 0.16 * ((i * 5) % 3)
    parts.append(box(f'strata{i}', 3.0, 0.7 + jut, h, y=-jut * 0.5, z=zc,
                     material=M_ROCK if i % 2 else M_ROCK_D,
                     rot=(0, 0.03 * math.sin(i * 1.7), 0)))
# broken blocks along the toe
for i in range(4):
    parts.append(box(f'toe{i}', 0.8, 0.7, 0.6, x=-1.1 + i * 0.75, y=-0.45,
                     z=-10.0, material=M_ROCK, rot=(0.2, 0.3 * i, 0.4 * i)))
# scrub clinging to two ledges
for i, zc in enumerate((-2.2, -5.6)):
    parts.append(box(f'scrub{i}', 1.1, 0.5, 0.55, x=-0.6 + i * 1.3, y=-0.55,
                     z=zc, material=M_SCRUB, rot=(0, 0, 0.3 * i)))
join('cliff', parts)


# ---- underpass_wall: 3 m of sunken-road retaining wall ---------------------
# What a Tbilisi underpass actually has: the road drops between concrete
# retaining walls and slips under the crossing street. NOT a triumphal arch
# standing on a lawn, which is what a hill-bore headwall looks like when it
# is placed on flat ground.
parts = [
    box('rwall', 3.0, 0.55, 3.2, z=-1.2, material=M_CONC),
    box('rcap', 3.0, 0.75, 0.24, z=0.52, material=M_CONC_D),
    box('rrail', 3.0, 0.07, 0.07, z=1.05, material=M_IRON),
    box('rpost1', 0.09, 0.09, 0.95, x=-1.4, z=0.95, material=M_IRON),
    box('rpost2', 0.09, 0.09, 0.95, x=1.4, z=0.95, material=M_IRON),
]
for i in range(3):  # shallow relief panels so it is not a blank slab
    parts.append(box(f'rpanel{i}', 0.75, 0.06, 2.2, x=-0.9 + i * 0.9, y=-0.30,
                     z=-1.3, material=M_CONC_D))
join('underpass_wall', parts)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', use_selection=True,
                          export_apply=True, export_yup=True)
names = [o.name for o in D.objects if o.type == 'MESH']
tris = 0
for o in D.objects:
    if o.type == 'MESH':
        o.data.calc_loop_triangles()
        tris += len(o.data.loop_triangles)
print('infra kit exported:', OUT, f'{len(names)} objects, {tris} tris')
print(sorted(names))
