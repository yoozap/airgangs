"""
Blender-headless script: model the facade detail kit and export one glb.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_facade_kit.py

Objects (names matter — citymap.js looks them up): window, ac, balcony, cornice,
win_old, win_stalin, win_pvc, win_shop, win_brick, balc_wood, balc_metal,
balc_glazed, balc_juliet, balc_loggia, balc_loggia_p, drainpipe, entrance,
ent_old, grille, dish (typology: tools/FACADE_KIT_TYPOLOGY.md).
glTF convention after export_yup: x right, y up, z = outward wall normal.
Walls are solid, so everything must PROTRUDE (+z); recession is faked by a dark
pane just off the wall inside a protruding frame.
Output: public/models/facade_kit.glb
"""
import os

import bpy

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                   'public', 'models', 'facade_kit.glb')

bpy.ops.wm.read_factory_settings(use_empty=True)
D = bpy.data


def mat(name, rgba, rough=0.85, metal=0.0):
    m = D.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes['Principled BSDF']
    bsdf.inputs['Base Color'].default_value = rgba
    bsdf.inputs['Roughness'].default_value = rough
    bsdf.inputs['Metallic'].default_value = metal
    return m


def hexmat(name, hx, rough=0.85, metal=0.0):
    # hex swatches are sRGB; Base Color is linear — convert or everything
    # renders washed-out (the dark PVC gap pane became a light grey slab)
    r, g, b = (int(hx[i:i + 2], 16) / 255 for i in (1, 3, 5))
    return mat(name, (r ** 2.2, g ** 2.2, b ** 2.2, 1), rough, metal)


M_FRAME = mat('frame', (0.55, 0.50, 0.44, 1))   # warm wood-brown frames
M_GLASS = mat('glass', (0.025, 0.03, 0.036, 1), rough=0.2, metal=0.3)  # dark pane
M_SILL = mat('sill', (0.70, 0.67, 0.62, 1))
M_AC = mat('ac', (0.82, 0.82, 0.80, 1), rough=0.6)
M_RAIL = mat('rail', (0.42, 0.40, 0.38, 1))
M_CORNICE = mat('cornice', (0.86, 0.82, 0.74, 1))
# typology palette (tools/FACADE_KIT_TYPOLOGY.md)
M_PINE = hexmat('pine', '#B07A3C')            # W1 honey pine
M_STALWOOD = hexmat('stalwood', '#5F4630')    # W2 brown casement
M_SURR = hexmat('surround', '#E8E4DA')        # W2 white plaster surround
M_PVC = hexmat('pvc', '#F2F2EE', rough=0.5)   # W4 white PVC
M_GAP = hexmat('gap', '#20242A', rough=0.6)   # dark open-gap pane / doorway
M_ALU = hexmat('alu', '#33383B', rough=0.4, metal=0.5)   # W6 black aluminum
M_TEAL = hexmat('teal', '#7FA8A0', rough=0.15, metal=0.2)  # W6 tinted glass
M_FASCIA = hexmat('fascia', '#C1272D', rough=0.5)          # shop fascia band
M_SAGE = hexmat('sage', '#6F9E8A')            # B1 sage-painted wood
M_GWOOD = hexmat('gwood', '#8B6A45')          # B1 corbels / B4 glazing grid
M_WMETAL = hexmat('wmetal', '#E5E3DC', rough=0.6, metal=0.2)  # B2 white bars
M_GALV = hexmat('galv', '#9AA0A3', rough=0.45, metal=0.7)     # drainpipe
M_GRILLE = hexmat('grille', '#2A2A2A', rough=0.5, metal=0.3)
M_DISH = hexmat('dish', '#B9BDC0', rough=0.5, metal=0.3)


def box(name, w, h, d, x=0.0, v=0.0, out=0.0, material=None):
    """w along wall (x), h vertical, d depth; out = center along outward normal.

    Blender is z-up and export_yup maps Blender (x,y,z)->glTF (x,z,-y),
    so vertical goes on Blender z and outward on Blender -y.
    """
    bpy.ops.mesh.primitive_cube_add(size=1, location=(x, -out, v))
    ob = bpy.context.active_object
    ob.name = name
    # size=1 cube already spans ±0.5, so scale by the FULL dimension —
    # w/2 here silently produced half-size boxes at full-size positions
    ob.scale = (w, d, h)
    bpy.ops.object.transform_apply(scale=True)
    if material:
        ob.data.materials.append(material)
    return ob


def join(name, obs):
    bpy.ops.object.select_all(action='DESELECT')
    for o in obs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]
    bpy.ops.object.join()
    ob = bpy.context.active_object
    ob.name = name
    ob.data.name = name  # glTF names meshes after the datablock, not the object
    return ob


# ---- window 1.5x1.6: protruding frame, dark pane just off the wall ----------
W, H, T = 1.5, 1.6, 0.09
FD = 0.12  # frame depth outward
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, out=FD / 2, material=M_FRAME),
    box('r', T, H, FD, x=(W - T) / 2, out=FD / 2, material=M_FRAME),
    box('t', W, T, FD, v=(H - T) / 2, out=FD / 2, material=M_FRAME),
    box('b', W, T, FD, v=-(H - T) / 2, out=FD / 2, material=M_FRAME),
    box('mv', 0.05, H - 2 * T, 0.05, out=0.045, material=M_FRAME),
    box('mh', W - 2 * T, 0.05, 0.05, v=0.12, out=0.045, material=M_FRAME),
    box('g', W - 2 * T, H - 2 * T, 0.02, out=0.015, material=M_GLASS),
    box('s', W + 0.16, 0.06, 0.26, v=-(H / 2 + 0.02), out=0.13, material=M_SILL),
]
join('window', parts)

# ---- AC unit ----------------------------------------------------------------
parts = [box('body', 0.78, 0.55, 0.28, out=0.16, material=M_AC)]
for i in range(5):
    parts.append(box(f'fin{i}', 0.66, 0.02, 0.02,
                     v=-0.18 + i * 0.09, out=0.31, material=M_AC))
parts.append(box('brk1', 0.06, 0.08, 0.30, x=-0.28, v=-0.33, out=0.15, material=M_RAIL))
parts.append(box('brk2', 0.06, 0.08, 0.30, x=0.28, v=-0.33, out=0.15, material=M_RAIL))
join('ac', parts)

# ---- balcony: slab + vertical-bar railing -----------------------------------
parts = [box('slab', 2.7, 0.13, 1.15, out=0.575, material=M_SILL),
         box('hand', 2.7, 0.05, 0.05, v=0.95, out=1.12, material=M_RAIL)]
for i in range(13):
    parts.append(box(f'bar{i}', 0.03, 0.92, 0.03,
                     x=-1.26 + i * 0.21, v=0.48, out=1.12, material=M_RAIL))
for s in (-1, 1):
    parts.append(box(f'side{s}', 0.05, 0.05, 1.05, x=1.32 * s, v=0.95, out=0.55,
                     material=M_RAIL))
    for i in range(4):
        parts.append(box(f'sbar{s}{i}', 0.03, 0.92, 0.03, x=1.32 * s, v=0.48,
                         out=0.18 + i * 0.28, material=M_RAIL))
join('balcony', parts)

# ---- cornice: 3 m stepped molding -------------------------------------------
parts = [
    box('c1', 3.0, 0.10, 0.30, v=0.00, out=0.15, material=M_CORNICE),
    box('c2', 3.0, 0.10, 0.22, v=-0.10, out=0.11, material=M_CORNICE),
    box('c3', 3.0, 0.12, 0.13, v=-0.21, out=0.065, material=M_CORNICE),
]
join('cornice', parts)

# ---- win_old (W1): old-Tbilisi tall pine French casement 1.0 x 2.3 ----------
# 2 leaves x 3 panes (2 glazing bars each) + transom; origin = window center.
W, H, T, FD = 1.0, 2.3, 0.075, 0.10
TRV = 0.67  # transom bar center
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, out=FD / 2, material=M_PINE),
    box('r', T, H, FD, x=(W - T) / 2, out=FD / 2, material=M_PINE),
    box('t', W, T, FD, v=(H - T) / 2, out=FD / 2, material=M_PINE),
    box('b', W, T, FD, v=-(H - T) / 2, out=FD / 2, material=M_PINE),
    box('trb', W - 2 * T, 0.07, 0.08, v=TRV, out=0.05, material=M_PINE),
    box('mid', 0.06, 1.71, 0.07, v=-0.22, out=0.045, material=M_PINE),  # meeting stile
    box('tm', 0.035, 0.37, 0.05, v=0.89, out=0.04, material=M_PINE),    # transom mullion
    box('g', W - 2 * T, H - 2 * T, 0.02, out=0.012, material=M_GLASS),
]
for s in (-1, 1):  # 2 horizontal glazing bars per leaf -> 3 panes each
    for gv in (-0.505, 0.065):
        parts.append(box(f'gb{s}{gv}', 0.395, 0.035, 0.05,
                         x=0.2275 * s, v=gv, out=0.04, material=M_PINE))
join('win_old', parts)

# ---- win_stalin (W2): neoclassic casement 1.2 x 2.1, surround + pediment ----
W, H, T, FD = 1.2, 2.1, 0.08, 0.10
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, out=FD / 2, material=M_STALWOOD),
    box('r', T, H, FD, x=(W - T) / 2, out=FD / 2, material=M_STALWOOD),
    box('t', W, T, FD, v=(H - T) / 2, out=FD / 2, material=M_STALWOOD),
    box('b', W, T, FD, v=-(H - T) / 2, out=FD / 2, material=M_STALWOOD),
    box('trb', W - 2 * T, 0.06, 0.08, v=0.55, out=0.05, material=M_STALWOOD),
    box('mid', 0.07, 1.49, 0.07, v=-0.225, out=0.045, material=M_STALWOOD),
    box('lb1', 0.485, 0.035, 0.05, x=-0.278, v=-0.35, out=0.04, material=M_STALWOOD),
    box('lb2', 0.485, 0.035, 0.05, x=0.278, v=-0.35, out=0.04, material=M_STALWOOD),
    box('g', W - 2 * T, H - 2 * T, 0.02, out=0.012, material=M_GLASS),
    # white plaster surround: pilaster strips + frieze + pediment hood
    box('sl', 0.16, H + 0.25, 0.07, x=-(W / 2 + 0.08), out=0.035, material=M_SURR),
    box('sr', 0.16, H + 0.25, 0.07, x=(W / 2 + 0.08), out=0.035, material=M_SURR),
    box('fz', 1.52, 0.18, 0.07, v=H / 2 + 0.09, out=0.035, material=M_SURR),
    box('hd', 1.9, 0.10, 0.32, v=H / 2 + 0.23, out=0.16, material=M_SURR),
    box('hd2', 1.4, 0.12, 0.22, v=H / 2 + 0.34, out=0.11, material=M_SURR),
    # projecting sill on consoles
    box('sill', 1.5, 0.07, 0.22, v=-(H / 2 + 0.035), out=0.11, material=M_SILL),
    box('cl', 0.10, 0.14, 0.16, x=-0.45, v=-(H / 2 + 0.14), out=0.08, material=M_SURR),
    box('cr', 0.10, 0.14, 0.16, x=0.45, v=-(H / 2 + 0.14), out=0.08, material=M_SURR),
]
join('win_stalin', parts)

# ---- win_pvc (W4): white PVC 1.4 x 1.4, center mullion, one dark open gap ---
W, H, T, FD = 1.4, 1.4, 0.06, 0.08
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, out=FD / 2, material=M_PVC),
    box('r', T, H, FD, x=(W - T) / 2, out=FD / 2, material=M_PVC),
    box('t', W, T, FD, v=(H - T) / 2, out=FD / 2, material=M_PVC),
    box('b', W, T, FD, v=-(H - T) / 2, out=FD / 2, material=M_PVC),
    box('mid', 0.08, H - 2 * T, 0.07, out=0.05, material=M_PVC),
    box('gl', 0.6, H - 2 * T, 0.02, x=-0.34, out=0.015, material=M_GLASS),
    box('gr', 0.6, H - 2 * T, 0.02, x=0.34, out=0.015, material=M_GAP),  # tilted-open leaf
    box('s', W + 0.1, 0.05, 0.14, v=-(H / 2 + 0.015), out=0.07, material=M_SILL),
]
join('win_pvc', parts)

# ---- win_shop (W6): black-alu shopfront 3.0 x 2.8 + fascia band -------------
# Origin sits at the normal window-cell center (ground cell y = ground + 2.5),
# so the glazing is modeled 2.5 m DOWN from origin to land on the street.
W, H = 3.0, 2.8
VB, VT = -2.5, 0.3  # glazing bottom/top in object space
T, FD = 0.09, 0.12
VC = (VB + VT) / 2
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, v=VC, out=FD / 2, material=M_ALU),
    box('r', T, H, FD, x=(W - T) / 2, v=VC, out=FD / 2, material=M_ALU),
    box('b', W, 0.12, FD, v=VB + 0.06, out=FD / 2, material=M_ALU),
    box('t', W, T, FD, v=VT - 0.045, out=FD / 2, material=M_ALU),
    box('m1', 0.07, 2.55, 0.10, x=-0.75, v=VC, out=0.05, material=M_ALU),
    box('m2', 0.07, 2.55, 0.10, v=VC, out=0.05, material=M_ALU),
    box('m3', 0.07, 2.55, 0.10, x=0.75, v=VC, out=0.05, material=M_ALU),
    box('trb', W - 0.18, 0.07, 0.10, v=-0.3, out=0.05, material=M_ALU),
    box('g', W - 0.18, H - 0.2, 0.02, v=VC, out=0.012, material=M_TEAL),
    box('fas', W, 0.8, 0.14, v=VT + 0.4, out=0.07, material=M_FASCIA),  # fascia
]
join('win_shop', parts)

# ---- balc_wood (B1): old-town wooden gallery bay, sage, fretwork slots ------
# Origin like 'balcony': deck slab centered at v=0 against the wall.
BW, DP = 2.7, 1.3
parts = [
    box('deck', BW, 0.10, DP, out=DP / 2, material=M_SAGE),
    box('trim', BW, 0.09, 0.04, v=-0.07, out=DP - 0.02, material=M_SAGE),
    box('hand', BW, 0.07, 0.06, v=0.90, out=DP - 0.06, material=M_SAGE),
    box('bot', BW, 0.06, 0.05, v=0.16, out=DP - 0.06, material=M_SAGE),
    box('beam', BW, 0.10, 0.10, v=2.62, out=DP - 0.08, material=M_SAGE),
]
for i, px in enumerate((-1.05, 0.0, 1.05)):  # carved corbels under the deck
    parts.append(box(f'crb{i}', 0.12, 0.30, 0.85, x=px, v=-0.22, out=0.42,
                     material=M_GWOOD))
for i, px in enumerate((-1.3, 0.0, 1.3)):  # square posts up to the roof beam
    parts.append(box(f'post{i}', 0.10, 2.55, 0.10, x=px, v=1.325, out=DP - 0.08,
                     material=M_SAGE))
for i in range(13):  # fretwork-slot balustrade: boards with slot gaps
    parts.append(box(f'fw{i}', 0.13, 0.62, 0.035, x=-1.2 + i * 0.2, v=0.51,
                     out=DP - 0.06, material=M_SAGE))
for s in (-1, 1):  # side balustrades
    parts.append(box(f'srail{s}', 0.06, 0.07, DP - 0.3, x=(BW / 2 - 0.03) * s,
                     v=0.90, out=DP / 2, material=M_SAGE))
    for i in range(5):
        parts.append(box(f'sfw{s}{i}', 0.035, 0.62, 0.13, x=(BW / 2 - 0.03) * s,
                         v=0.51, out=0.25 + i * 0.2, material=M_SAGE))
join('balc_wood', parts)

# ---- balc_metal (B2): cantilever slab + welded 12 mm bars @110 mm -----------
parts = [
    box('slab', 2.7, 0.13, 1.0, out=0.5, material=M_SILL),
    box('hand', 2.7, 0.05, 0.04, v=0.95, out=0.94, material=M_WMETAL),
]
for i in range(24):
    parts.append(box(f'bar{i}', 0.018, 0.86, 0.018, x=-1.265 + i * 0.11, v=0.52,
                     out=0.94, material=M_WMETAL))
for s in (-1, 1):
    parts.append(box(f'shand{s}', 0.04, 0.05, 0.9, x=1.33 * s, v=0.95, out=0.47,
                     material=M_WMETAL))
    for i in range(8):
        parts.append(box(f'sbar{s}{i}', 0.018, 0.86, 0.018, x=1.33 * s, v=0.52,
                         out=0.10 + i * 0.11, material=M_WMETAL))
join('balc_metal', parts)

# ---- balc_glazed (B4): self-built glazed veranda box 2.7 x 1.0 x 2.6 --------
W, DP, H = 2.7, 1.0, 2.6
parts = [
    box('floor', W, 0.12, DP, out=DP / 2, material=M_SILL),
    box('roof', W + 0.1, 0.10, DP + 0.08, v=H - 0.05, out=DP / 2, material=M_SILL),
    box('g', W - 0.1, 2.15, 0.02, v=1.375, out=DP - 0.05, material=M_GLASS),
]
for i in range(7):  # small-pane wood grid, front
    parts.append(box(f'v{i}', 0.05, 2.3, 0.06, x=-1.35 + i * 0.45, v=1.3,
                     out=DP - 0.05, material=M_GWOOD))
for gv in (0.28, 0.88, 1.48, 2.08, 2.42):
    parts.append(box(f'h{gv}', W, 0.05, 0.06, v=gv, out=DP - 0.05, material=M_GWOOD))
for s in (-1, 1):  # glazed sides
    parts.append(box(f'sg{s}', 0.02, 2.15, DP - 0.2, x=(W / 2 - 0.04) * s, v=1.375,
                     out=DP / 2, material=M_GLASS))
    for i, o in enumerate((0.1, 0.5, 0.9)):
        parts.append(box(f'sv{s}{i}', 0.06, 2.3, 0.05, x=(W / 2 - 0.03) * s, v=1.3,
                         out=o, material=M_GWOOD))
    for gv in (0.28, 2.42):
        parts.append(box(f'sh{s}{gv}', 0.06, 0.05, DP - 0.1, x=(W / 2 - 0.03) * s,
                         v=gv, out=DP / 2, material=M_GWOOD))
join('balc_glazed', parts)

# ---- win_brick (W5): 1.0 x 1.8 sash, corbelled brick hood, olive shutters ---
M_BRKH = hexmat('brickhood', '#A8563A')       # W5 corbelled brick hood
M_SASH = hexmat('sash', '#3B3B3B', rough=0.6)  # W5 dark sash frame
M_SHUT = hexmat('shutter', '#9A9455')          # W5 olive louvered shutters
W, H, T, FD = 1.0, 1.8, 0.07, 0.10
parts = [
    box('l', T, H, FD, x=-(W - T) / 2, out=FD / 2, material=M_SASH),
    box('r', T, H, FD, x=(W - T) / 2, out=FD / 2, material=M_SASH),
    box('t', W, T, FD, v=(H - T) / 2, out=FD / 2, material=M_SASH),
    box('b', W, T, FD, v=-(H - T) / 2, out=FD / 2, material=M_SASH),
    box('meet', W - 2 * T, 0.06, 0.06, v=0.1, out=0.045, material=M_SASH),  # 1-over-1 sash meeting rail
    box('g', W - 2 * T, H - 2 * T, 0.02, out=0.012, material=M_GLASS),
    # corbelled brick crown hood over the lintel (3 stepped courses)
    box('hd1', W + 0.30, 0.09, 0.18, v=H / 2 + 0.10, out=0.09, material=M_BRKH),
    box('hd2', W + 0.16, 0.08, 0.13, v=H / 2 + 0.02, out=0.065, material=M_BRKH),
    box('hd3', W + 0.44, 0.07, 0.10, v=H / 2 + 0.185, out=0.05, material=M_BRKH),
    # projecting brick sill course
    box('sill', W + 0.2, 0.08, 0.16, v=-(H / 2 + 0.04), out=0.08, material=M_BRKH),
]
for s in (-1, 1):  # louvered shutters swung open against the wall
    sx = (W / 2 + 0.32) * s
    parts.append(box(f'sh{s}', 0.45, H - 0.15, 0.04, x=sx, out=0.03, material=M_SHUT))
    for i in range(6):  # louver slats read as relief lines
        parts.append(box(f'sl{s}{i}', 0.37, 0.03, 0.02, x=sx,
                         v=-0.65 + i * 0.26, out=0.055, material=M_SHUT))
join('win_brick', parts)

# ---- balc_juliet (B5): wrought-iron lace guard, 0.4 m bow, Vake-Vera --------
# Origin at the guard's BOTTOM rail against the wall (citymap puts it just
# under a window). Fish-belly bow faked by a second bar row pushed outward.
M_IRON = hexmat('iron', '#2E2E30', rough=0.5, metal=0.4)
BW = 1.5
parts = [
    box('bot', BW, 0.05, 0.05, v=0.02, out=0.12, material=M_IRON),
    box('hand', BW, 0.06, 0.06, v=0.95, out=0.12, material=M_IRON),
    box('mid', BW, 0.035, 0.035, v=0.5, out=0.38, material=M_IRON),  # belly rail
]
for i in range(11):
    px = -BW / 2 + 0.075 + i * (BW - 0.15) / 10
    # each picket = two segments meeting at the bellied mid rail
    parts.append(box(f'pl{i}', 0.022, 0.5, 0.022, x=px, v=0.26, out=0.25, material=M_IRON))
    parts.append(box(f'pu{i}', 0.022, 0.5, 0.022, x=px, v=0.72, out=0.25, material=M_IRON))
join('balc_juliet', parts)

# ---- balc_loggia (B3): precast ribbed loggia panel, concrete + painted ------
# Origin like 'balcony': panel face at the loggia edge, deck at v=0.
M_PANEL = hexmat('panelc', '#B5AEA0')
M_PAINT = hexmat('panelp', '#4F97AD')  # the sky-blue Gldani repaint
for nm, m in (('balc_loggia', M_PANEL), ('balc_loggia_p', M_PAINT)):
    parts = [
        box('deck', 2.8, 0.12, 1.05, out=0.52, material=M_SILL),
        box('panel', 2.8, 1.0, 0.06, v=0.52, out=1.0, material=m),
        box('cap', 2.8, 0.06, 0.12, v=1.05, out=1.0, material=M_SILL),
    ]
    for i in range(9):  # cast vertical ribs
        parts.append(box(f'rib{i}', 0.08, 0.9, 0.03, x=-1.2 + i * 0.3, v=0.5,
                         out=1.045, material=m))
    for s in (-1, 1):  # closed panel sides
        parts.append(box(f'side{s}', 0.06, 1.0, 0.95, x=1.37 * s, v=0.52,
                         out=0.5, material=m))
    join(nm, parts)

# ---- ent_old: historic carved double door + arched transom + stone surround -
M_DWOOD = hexmat('doorwood', '#5C3A22')  # aged walnut double door
M_STONE = hexmat('doorstone', '#D9CDB4')
parts = [
    # stone surround: pilasters + arch header block
    box('pl', 0.22, 2.9, 0.16, x=-0.95, v=1.45, out=0.08, material=M_STONE),
    box('pr', 0.22, 2.9, 0.16, x=0.95, v=1.45, out=0.08, material=M_STONE),
    box('arch', 2.12, 0.5, 0.18, v=3.05, out=0.09, material=M_STONE),
    box('key', 0.26, 0.62, 0.22, v=3.08, out=0.11, material=M_STONE),  # keystone
    # carved double door leaves with panel relief
    box('dl', 0.78, 2.35, 0.06, x=-0.41, v=1.2, out=0.03, material=M_DWOOD),
    box('dr', 0.78, 2.35, 0.06, x=0.41, v=1.2, out=0.03, material=M_DWOOD),
    box('pnl1', 0.5, 0.8, 0.03, x=-0.41, v=1.7, out=0.065, material=M_DWOOD),
    box('pnl2', 0.5, 0.8, 0.03, x=0.41, v=1.7, out=0.065, material=M_DWOOD),
    box('pnl3', 0.5, 0.55, 0.03, x=-0.41, v=0.65, out=0.065, material=M_DWOOD),
    box('pnl4', 0.5, 0.55, 0.03, x=0.41, v=0.65, out=0.065, material=M_DWOOD),
    # arched fanlight transom (dark glass over the leaves)
    box('fan', 1.6, 0.5, 0.03, v=2.62, out=0.025, material=M_GLASS),
    box('fanb1', 0.05, 0.5, 0.04, v=2.62, out=0.045, material=M_DWOOD),
    box('fanb2', 0.05, 0.5, 0.04, x=-0.5, v=2.62, out=0.045, material=M_DWOOD),
    box('fanb3', 0.05, 0.5, 0.04, x=0.5, v=2.62, out=0.045, material=M_DWOOD),
    # two worn stone steps
    box('st1', 2.2, 0.14, 0.5, v=0.07, out=0.25, material=M_STONE),
    box('st2', 2.0, 0.13, 0.32, v=0.20, out=0.16, material=M_STONE),
]
join('ent_old', parts)

# ---- drainpipe: 3 m galvanized 100 mm section, origin at the BOTTOM ---------
# citymap stretches it vertically (it.sy) to span the full facade height.
parts = [
    box('p', 0.10, 3.0, 0.10, v=1.5, out=0.16, material=M_GALV),
    # keep joint/brackets SHORT: citymap stretches this object vertically,
    # anything tall here becomes a slab on high facades
    box('jnt', 0.13, 0.05, 0.13, v=2.95, out=0.16, material=M_GALV),  # conical joint
    box('brk1', 0.05, 0.025, 0.16, v=0.6, out=0.08, material=M_GALV),
    box('brk2', 0.05, 0.025, 0.16, v=2.2, out=0.08, material=M_GALV),
]
join('drainpipe', parts)

# ---- entrance: recessed dark doorway 1.4 x 2.2 + concrete canopy slab -------
# Origin at street level under the door.
parts = [
    box('l', 0.10, 2.2, 0.12, x=-0.65, v=1.1, out=0.06, material=M_RAIL),
    box('r', 0.10, 2.2, 0.12, x=0.65, v=1.1, out=0.06, material=M_RAIL),
    box('head', 1.4, 0.12, 0.12, v=2.16, out=0.06, material=M_RAIL),
    box('door', 1.2, 2.1, 0.04, v=1.06, out=0.035, material=M_GAP),
    box('step', 1.8, 0.14, 0.55, v=0.07, out=0.27, material=M_SILL),
    box('canopy', 2.5, 0.12, 1.2, v=2.5, out=0.6, material=M_SILL),  # cantilever slab
]
join('entrance', parts)

# ---- grille: ground-floor vertical-bar window grille 1.2 x 1.4 --------------
parts = [
    box('gt', 1.2, 0.05, 0.03, v=0.675, out=0.17, material=M_GRILLE),
    box('gb', 1.2, 0.05, 0.03, v=-0.675, out=0.17, material=M_GRILLE),
    box('gm', 1.2, 0.04, 0.03, out=0.17, material=M_GRILLE),  # mid scroll rail
]
for i in range(9):
    parts.append(box(f'gv{i}', 0.025, 1.4, 0.025, x=-0.52 + i * 0.13, out=0.17,
                     material=M_GRILLE))
join('grille', parts)

# ---- dish: 0.45 m offset satellite dish on a wall arm -----------------------
parts = [box('mnt', 0.08, 0.30, 0.05, out=0.025, material=M_GALV),
         box('arm', 0.06, 0.06, 0.30, v=0.02, out=0.18, material=M_GALV)]
p1 = box('plate', 0.90, 0.90, 0.05, out=0.36, material=M_DISH)
p1.rotation_euler[0] = 0.35  # offset-dish tilt (baked into the join)
p2 = box('plate2', 0.68, 0.68, 0.05, v=0.02, out=0.40, material=M_DISH)
p2.rotation_euler[0] = 0.35
parts += [p1, p2,
          box('lnb_arm', 0.04, 0.04, 0.35, v=-0.22, out=0.5, material=M_DISH),
          box('lnb', 0.07, 0.07, 0.10, v=-0.32, out=0.62, material=M_DISH)]
join('dish', parts)

# ---- roof kit: flat roofs are 60-80 % of the aerial view — give them -------
# parapet lips + shaft/tank/vent clutter (citymap places these at roof level
# with a long 1500 m cull; the whole point is being visible from the air).
M_CONC = mat('conc', (0.55, 0.53, 0.49, 1))
M_RUST = hexmat('rust', '#8A5B3C', rough=0.7, metal=0.2)

# parapet: 3 m lip along the roof edge, origin v=0 AT the roof plane, centered
# on the wall line (citymap stretches it per segment via it.sx like cornice)
parts = [
    box('pl', 3.0, 0.55, 0.22, v=0.275, material=M_CONC),
    box('pcap', 3.0, 0.07, 0.30, v=0.585, material=M_CONC),
]
join('parapet', parts)

# roof_box: elevator machine room 2.5 m cube + slab cap + dark door
parts = [
    box('shaft', 2.5, 2.5, 2.5, v=1.25, material=M_CONC),
    box('slab', 2.8, 0.12, 2.8, v=2.56, material=M_CONC),
    box('door', 0.9, 1.9, 0.06, v=0.95, out=1.27, material=M_GAP),
]
join('roof_box', parts)

# roof_tank: rusty water tank drum on four short legs
bpy.ops.mesh.primitive_cylinder_add(vertices=10, radius=0.85, depth=1.3,
                                    location=(0, 0, 1.05))
tank = bpy.context.active_object
tank.name = 'tk'
tank.data.materials.append(M_RUST)
parts = [tank]
for sx_, sy_ in ((-1, -1), (-1, 1), (1, -1), (1, 1)):
    parts.append(box(f'leg{sx_}{sy_}', 0.08, 0.42, 0.08, x=0.5 * sx_, v=0.21,
                     out=0.5 * sy_, material=M_RAIL))
join('roof_tank', parts)

# roof_vent: concrete ventilation stack with cap
parts = [
    box('vb', 0.6, 0.8, 0.6, v=0.4, material=M_CONC),
    box('vc', 0.75, 0.08, 0.75, v=0.84, material=M_CONC),
]
join('roof_vent', parts)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', use_selection=True,
                          export_apply=True, export_yup=True)
print('kit exported:', OUT, 'objects:',
      [o.name for o in D.objects if o.type == 'MESH'])
