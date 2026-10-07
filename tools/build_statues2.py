# Realistic statue rebuild — per-monument builders on statue_lib.
#   /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_statues2.py [-- names...]
# EXPORT=1 writes GLBs to public/models/statues/. Previews always render to
# $STATUE_SHOTS. Contract: height 1.0, base z=0, standing figures face -Y in
# Blender (= three.js +z), equestrians face +X. Scale in JS = real height (m).
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import statue_lib as L
from statue_lib import P, Vector as V  # noqa: F401


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def finish(name, organic_parts, props=(), voxel=0.02, tris=24000, pat=0.5):
    core = L.join(list(organic_parts))
    core = L.remesh_decimate(core, voxel, tris)
    if props:
        for p in props:
            L.smooth(p)
        core = L.join([core] + list(props))
    if pat:
        L.patina(core, amount=pat)
    L.export(core, name, preview=True)
    return core


# --- Kartlis Deda: 20 m modernist aluminum figure ---------------------------
# Photo refs: LEFT hand raised near shoulder height offering the bowl, RIGHT
# arm angled down across the body holding the sword level at hip height,
# cylindrical gown, wreath-crown with veil down the back.
def kartlis_deda():
    reset()
    H = 1.8
    m = L.Meta('kd', resolution=0.016)
    armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((0.19 * H, -0.09 * H, 0.66 * H)),
            Vector((0.115 * H, -0.155 * H, 0.575 * H))]
    armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((-0.21 * H, -0.075 * H, 0.72 * H)),
            Vector((-0.155 * H, -0.14 * H, 0.795 * H))]
    j, limbs = L.figure(m, H=H, female=True, sway=0.0, arms={'R': armR, 'L': armL},
                        coat={'hem': 0.0}, head_pitch=0.06, arm_r=1.35)  # sleeved arms
    # the dress rises to the collar — the figure is one cylindrical silhouette
    gown = L.robe(H, r_hem=0.155 * H, top_z=0.80 * H, top_r=0.085 * H, name='gown')
    # headdress: the flat crown cap + hood mass framing the face
    cap = L.prim_cyl('cap', 0.056 * H, 0.062 * H, 0.038 * H, (0, -0.012 * H, j['crown'] + 0.005 * H), verts=18)
    m.ellipsoid(Vector((0, 0.012 * H, j['crown'] - 0.055 * H)), 0.075 * H, (1.0, 0.9, 1.05))  # hood
    veil = L.ribbon('veil', [Vector((0, 0.050 * H, j['crown'] - 0.01 * H)),
                             Vector((0, 0.070 * H, 0.84 * H)),
                             Vector((0, 0.075 * H, 0.76 * H))],
                    [0.085 * H, 0.10 * H, 0.11 * H], side=(1, 0, 0), thick=0.03 * H)
    # sword: broad blade held across the body at hip height, tip toward -x
    hr = j['handR']
    ang = -0.30  # slight downward slope of the blade
    blade = L.prim_box('blade', (0.30 * H, 0.016 * H, 0.05 * H),
                       (hr.x - 0.13 * H, hr.y, hr.z - 0.035 * H), rot=(0, ang, 0))
    guard = L.prim_box('guard', (0.018 * H, 0.026 * H, 0.09 * H), (hr.x + 0.015 * H, hr.y, hr.z + 0.008 * H))
    grip = L.prim_cyl('grip', 0.016 * H, 0.016 * H, 0.08 * H, (hr.x + 0.055 * H, hr.y, hr.z + 0.022 * H),
                      rot=(0, math.pi / 2 + ang, 0), verts=10)
    # bowl offered on the raised left palm
    hl = j['handL']
    bowl = L.prim_cyl('bowl', 0.050 * H, 0.068 * H, 0.045 * H, (hl.x, hl.y, hl.z + 0.03 * H), verts=18)
    finish('kartlis_deda', [m.mesh(), gown, veil] + limbs, [cap, blade, guard, grip, bowl],
           voxel=0.012, tris=38000, pat=0.0)  # aluminum: no bronze patina


# --- shared equestrian core --------------------------------------------------


def rider_on(mj, m, raised_arm=True, lean=0.0):
    """Seat a rider on horse joints mj. Returns rider joints + limb meshes."""
    W = mj['W']
    s = mj['saddle']
    H = W * 1.05  # rider height if standing; seated proportions derived
    hipz = s.z + 0.02 * W
    shz = hipz + 0.31 * H
    headc = Vector((s.x + lean * 0.1, 0, shz + 0.135 * H))
    m.ellipsoid((s.x, 0, hipz), H * 0.075, (1.15, 0.95, 0.85))
    m.chain([(s.x, 0, hipz), (s.x + lean * 0.06, 0, hipz + 0.17 * H), (s.x + lean * 0.12, 0, shz)],
            [H * 0.062, H * 0.070, H * 0.058])
    m.ellipsoid((s.x + lean * 0.12, 0, shz), H * 0.055, (2.15, 0.85, 0.8))
    m.chain([(s.x + lean * 0.12, 0, shz), (headc.x, 0, headc.z - 0.09 * H)], [H * 0.030, H * 0.027])
    L.face(m, headc, P['head_h'] * H, P['head_w'] * H, yaw=-math.pi / 2, pitch=0.12)
    limbs = []
    shp = Vector((s.x + lean * 0.12, 0, shz))
    for sd in (-1, 1):
        sh = shp + Vector((0, sd * P['shoulder_w'] * H * 0.95, 0))
        m.ball(sh, H * 0.048)
        if sd == 1 and raised_arm:  # right arm raised high, forward
            pts = [sh, sh + Vector((0.10 * H, 0.10 * H, 0.14 * H)), sh + Vector((0.26 * H, 0.13 * H, 0.30 * H))]
        else:  # holds the reins low
            pts = [sh, sh + Vector((0.10 * H, sd * 0.02 * H, -0.16 * H)),
                   sh + Vector((0.26 * H, sd * -0.03 * H, -0.24 * H))]
        limbs.append(L.limb('rarm%+d' % sd, pts, [0.040 * H, 0.030 * H, 0.022 * H]))
        d = (pts[-1] - pts[-2]).normalized()
        m.ellipsoid(pts[-1] + d * 0.04 * H, 0.045 * H, (0.6, 0.8, 1.0))
        # legs: thigh along the saddle, calf down the barrel
        knee = Vector((s.x + 0.16 * W, sd * W * 0.17, s.z - 0.12 * W))
        ankle = Vector((s.x + 0.13 * W, sd * W * 0.185, s.z - 0.38 * W))
        limbs.append(L.limb('rleg%+d' % sd, [Vector((s.x, sd * W * 0.10, hipz)), knee, ankle],
                            [0.055 * H, 0.042 * H, 0.028 * H]))
        foot = L.prim_box('rfoot%+d' % sd, (0.11 * H, 0.045 * H, 0.04 * H),
                          (ankle.x + 0.03 * H, ankle.y, ankle.z - 0.02 * H))
        limbs.append(L.smooth(foot))
    # cloak falling from the shoulders over the horse's rump
    limbs.append(L.ribbon('cloak', [shp + Vector((-0.06 * H, 0, 0.02 * H)),
                                    Vector((s.x - 0.32 * W, 0, s.z - 0.05 * W)),
                                    Vector((s.x - 0.42 * W, 0, s.z - 0.35 * W))],
                          [0.28 * H, 0.38 * H, 0.32 * H], side=(0, 1, 0), thick=0.028 * H))
    return {'handR': shp + Vector((0.26 * H, 0.13 * H, 0.30 * H)), 'H': H}, limbs


# --- David the Builder: walking horse, raised right arm ---------------------
def david():
    reset()
    m = L.Meta('dv', resolution=0.018)
    mj, hlimbs = L.horse(m, W=1.6)
    rj, rlimbs = rider_on(mj, m, raised_arm=True)
    finish('david', [m.mesh()] + hlimbs + rlimbs, voxel=0.02, tris=34000, pat=0.45)


# --- St George (Freedom Monument): gold, levade, lance into dragon ----------
def george():
    reset()
    m = L.Meta('gg', resolution=0.018)
    mj, hlimbs = L.horse(m, W=1.5, levade=1.0)
    rj, rlimbs = rider_on(mj, m, raised_arm=True, lean=-0.3)
    parts = [m.mesh()] + hlimbs + rlimbs
    # pitch horse+rider up 32 deg around the hind hooves
    W = 1.5
    pivot = Vector((-W * 1.02 * 0.375, 0, 0.05))
    pitch_m = Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(-32), 4, 'Y') @ Matrix.Translation(-pivot)
    for o in parts:
        o.data.transform(pitch_m)
    # dragon coiled under the hind hooves (photo ref)
    dm = L.Meta('dragon', resolution=0.02)
    dm.chain([(-0.85, 0.30, 0.10), (-0.55, 0.02, 0.18), (-0.25, -0.18, 0.12), (0.05, 0.0, 0.20), (0.22, 0.10, 0.34)],
             [0.10, 0.15, 0.13, 0.09, 0.06])
    for s in (-1, 1):
        dm.ellipsoid((-0.5, s * 0.30, 0.20), 0.16, (1.4, 0.5, 0.25), rot=(s * 0.5, 0, s * 0.5))  # wings
    dm.ellipsoid((0.26, 0.13, 0.42), 0.09, (1.3, 0.7, 0.8))  # head rearing up at the lance
    parts.append(dm.mesh())
    # lance: near-vertical, from the raised right hand down into the dragon
    hand = pitch_m @ Vector((rj['handR'].x, rj['handR'].y, rj['handR'].z))
    tip = Vector((0.28, 0.13, 0.38))
    d = tip - hand
    eul = d.to_track_quat('-Z', 'Y').to_euler()
    lance = L.prim_cyl('lance', 0.014, 0.024, d.length + 0.25, hand + d * 0.5, rot=(eul.x, eul.y, eul.z), verts=8)
    finish('george', parts, [lance], voxel=0.02, tris=34000, pat=0.0)  # gold: no patina


# --- Rustaveli: poet in the pointed cap, scroll at the hip, cloaked left ----
def rustaveli():
    reset()
    H = 1.8
    m = L.Meta('ru', resolution=0.016)
    # right hand holds the scroll angled down at the hip; left arm tucked
    # under the heavy cloak over the left shoulder
    armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((0.17 * H, -0.055 * H, 0.64 * H)),
            Vector((0.10 * H, -0.115 * H, 0.555 * H))]
    armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((-0.15 * H, -0.05 * H, 0.66 * H)),
            Vector((-0.07 * H, -0.10 * H, 0.60 * H))]
    j, limbs = L.figure(m, H=H, sway=0.04, arms={'R': armR, 'L': armL},
                        coat={'hem': 0.06 * H}, head_pitch=0.16)
    coat = L.robe(H, r_hem=0.125 * H, top_z=0.62 * H, top_r=0.10 * H, name='coat')
    # heavy cloak draped over the LEFT shoulder, falling front and back
    cloak = L.ribbon('cloak', [Vector((-0.10 * H, 0.02 * H, P['shoulder_y'] * H + 0.045 * H)),
                               Vector((-0.155 * H, 0.03 * H, 0.60 * H)),
                               Vector((-0.16 * H, 0.035 * H, 0.28 * H))],
                     [0.16 * H, 0.20 * H, 0.22 * H], side=(0.55, 1, 0), thick=0.035 * H)
    # pointed soft medieval cap
    cp = j['crown_pos']
    cap = L.prim_cyl('cap', 0.052 * H, 0.034 * H, 0.055 * H, (cp.x, cp.y, cp.z + 0.012 * H), verts=14)
    tip = L.prim_cyl('captip', 0.030 * H, 0.0, 0.05 * H, (cp.x, cp.y + 0.012 * H, cp.z + 0.055 * H),
                     rot=(0.35, 0, 0), verts=10)
    # scroll in the right hand, angled down
    hr = j['handR']
    scroll = L.prim_cyl('scroll', 0.020 * H, 0.020 * H, 0.16 * H, (hr.x, hr.y, hr.z - 0.01 * H),
                        rot=(0.5, 0.35, 0), verts=12)
    finish('rustaveli', [m.mesh(), coat, cloak] + limbs, [cap, tip, scroll], voxel=0.015, tris=30000, pat=0.5)


# --- St Nino (Tsereteli, Digomi): cross held ALOFT in the right hand, ------
# left palm open upward, flowing veil and robe.
def nino():
    reset()
    H = 1.8
    m = L.Meta('nn', resolution=0.016)
    armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((0.19 * H, -0.055 * H, 0.90 * H)),
            Vector((0.135 * H, -0.075 * H, 1.005 * H))]
    armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((-0.21 * H, -0.06 * H, 0.70 * H)),
            Vector((-0.185 * H, -0.125 * H, 0.76 * H))]
    j, limbs = L.figure(m, H=H, female=True, sway=0.0, arms={'R': armR, 'L': armL},
                        coat={'hem': 0.0}, head_pitch=0.06)
    gown = L.robe(H, r_hem=0.14 * H, top_z=0.60 * H, top_r=0.095 * H, name='gown')
    veil = L.ribbon('veil', [Vector((0, 0.03 * H, j['crown'])),
                             Vector((0, 0.07 * H, 0.76 * H)),
                             Vector((0, 0.08 * H, 0.62 * H))],
                    [0.10 * H, 0.14 * H, 0.15 * H], side=(1, 0, 0), thick=0.02 * H)
    # grapevine cross raised in the right hand, drooping arms
    hr = j['handR']
    post = L.prim_box('post', (0.020 * H, 0.018 * H, 0.26 * H), (hr.x, hr.y, hr.z + 0.09 * H))
    ca1 = L.prim_box('ca1', (0.085 * H, 0.016 * H, 0.024 * H),
                     (hr.x - 0.048 * H, hr.y, hr.z + 0.155 * H), rot=(0, 0.18, 0))
    ca2 = L.prim_box('ca2', (0.085 * H, 0.016 * H, 0.024 * H),
                     (hr.x + 0.048 * H, hr.y, hr.z + 0.155 * H), rot=(0, -0.18, 0))
    finish('nino', [m.mesh(), gown, veil] + limbs, [post, ca1, ca2], voxel=0.015, tris=30000, pat=0.55)


# --- Vazha-Pshavela (Ochiauri): right palm offered open, burka bunched over
# the left shoulder, bare-headed chokha highlander.
def vazha():
    reset()
    H = 1.8
    m = L.Meta('vz', resolution=0.016)
    # right arm extended sideways-forward, open palm up
    armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((0.26 * H, -0.09 * H, 0.72 * H)),
            Vector((0.33 * H, -0.16 * H, 0.68 * H))]
    armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((-0.17 * H, -0.02 * H, 0.62 * H)),
            Vector((-0.13 * H, -0.07 * H, 0.53 * H))]
    j, limbs = L.figure(m, H=H, sway=0.05, arms={'R': armR, 'L': armL},
                        coat={'hem': P['knee'] * H * 0.9}, head_pitch=0.08)
    # knee-length chokha coat
    coat = L.robe(H, r_hem=0.115 * H, top_z=0.60 * H, top_r=0.095 * H, name='chokha', hem_flare=0.8)
    coat.data.transform(Matrix.Translation((0, 0, P['knee'] * H * 0.85)) @ Matrix.Diagonal((1, 1, (0.62 - 0.24) / 0.62, 1)).to_4x4())
    # the heavy burka bunched over the LEFT shoulder and falling down the back
    m.ellipsoid(Vector((-P['shoulder_w'] * H - 0.02 * H, 0.01 * H, P['shoulder_y'] * H + 0.03 * H)),
                0.085 * H, (1.15, 0.9, 0.8))
    burka = L.ribbon('burka', [Vector((-0.15 * H, 0.03 * H, P['shoulder_y'] * H + 0.05 * H)),
                               Vector((-0.19 * H, 0.055 * H, 0.55 * H)),
                               Vector((-0.20 * H, 0.06 * H, 0.24 * H))],
                     [0.18 * H, 0.24 * H, 0.26 * H], side=(0.6, 1, 0), thick=0.04 * H)
    # cartridge rows (gazyrs) across the chest
    props = []
    for sd in (-1, 1):
        props.append(L.prim_box('gaz%+d' % sd, (0.075 * H, 0.02 * H, 0.075 * H),
                                (sd * 0.075 * H, -0.075 * H, 0.72 * H), rot=(0, 0, sd * 0.30)))
    props.append(L.prim_box('belt', (0.20 * H, 0.14 * H, 0.02 * H), (0, -0.01 * H, 0.56 * H)))
    finish('vazha', [m.mesh(), coat, burka] + limbs, props, voxel=0.015, tris=28000, pat=0.5)


# --- writers pair (photo ref): Ilia stocky, frock coat, arms at his sides;
# Akaki taller, mantle over the shoulders, right hand on an angled cane.
def writer(name, cane=False):
    reset()
    H = 1.8 if cane else 1.74  # Akaki reads taller
    m = L.Meta('wr', resolution=0.016)
    if cane:  # Akaki
        armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
                Vector((0.17 * H, -0.045 * H, 0.62 * H)),
                Vector((0.155 * H, -0.10 * H, 0.53 * H))]
        arms = {'R': armR}
        head_pitch = -0.06  # head tilted up
    else:  # Ilia: both arms at the sides (library default)
        arms = {}
        head_pitch = 0.10
    j, limbs = L.figure(m, H=H, sway=0.05, arms=arms, coat={'hem': 0.10 * H}, head_pitch=head_pitch)
    coat = L.robe(H, r_hem=0.12 * H, top_z=0.62 * H, top_r=0.10 * H, name='frock')
    # full beard mass
    cp = j['crown_pos']
    m.ellipsoid(Vector((cp.x, cp.y - 0.035 * H, cp.z - j['hh'] * 1.02)), 0.045 * H, (1.15, 0.8, 1.3))
    props = []
    parts = [m.mesh(), coat]
    if cane:
        hr = j['handR']
        # mantle over both shoulders
        parts.append(L.ribbon('mantle', [Vector((0, 0.035 * H, P['shoulder_y'] * H + 0.04 * H)),
                                         Vector((0, 0.06 * H, 0.55 * H)),
                                         Vector((0, 0.065 * H, 0.30 * H))],
                              [0.26 * H, 0.30 * H, 0.32 * H], side=(1, 0, 0), thick=0.035 * H))
        # cane angled outward under the right hand
        foot = Vector((hr.x + 0.06 * H, hr.y - 0.04 * H, 0.0))
        d = Vector((hr.x, hr.y, hr.z)) - foot
        eul = d.to_track_quat('Z', 'Y').to_euler()
        props.append(L.prim_cyl('cane', 0.010 * H, 0.012 * H, d.length, foot + d * 0.5,
                                rot=(eul.x, eul.y, eul.z), verts=8))
    finish(name, parts + limbs, props, voxel=0.016, tris=26000, pat=0.5)


# --- Pharnavaz (photo ref): both hands raised to the chest holding the ------
# crescent alphabet scroll; battlement crown; sword hangs at the right hip.
def pharnavaz():
    reset()
    H = 1.8
    m = L.Meta('ph', resolution=0.016)
    # both hands up at chest height, palms forward, holding the scroll ends
    armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((0.20 * H, -0.075 * H, 0.67 * H)),
            Vector((0.155 * H, -0.115 * H, 0.755 * H))]
    armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
            Vector((-0.20 * H, -0.075 * H, 0.63 * H)),
            Vector((-0.155 * H, -0.115 * H, 0.715 * H))]
    j, limbs = L.figure(m, H=H, sway=0.0, arms={'R': armR, 'L': armL}, coat={'hem': 0.0},
                        head_pitch=0.06, arm_r=1.15)
    robe_o = L.robe(H, r_hem=0.135 * H, top_z=0.58 * H, top_r=0.105 * H, name='kingrobe')
    # battlement crown + beard
    cp = j['crown_pos']
    crown = L.prim_cyl('crown', 0.052 * H, 0.058 * H, 0.05 * H, (cp.x, cp.y, cp.z - 0.005 * H), verts=8)
    m.ellipsoid(Vector((cp.x, cp.y - 0.035 * H, cp.z - j['hh'] * 1.02)), 0.042 * H, (1.1, 0.75, 1.35))
    # crescent scroll across the chest: shallow arc of boxes, curled ends
    props = [crown]
    arc_c = Vector((0, -0.125 * H, 0.70 * H))
    for i in range(5):
        a = (i - 2) / 2 * 0.55
        props.append(L.prim_box('sc%d' % i, (0.075 * H, 0.016 * H, 0.075 * H - abs(i - 2) * 0.008 * H),
                                arc_c + Vector((math.sin(a) * 0.19 * H, 0, (1 - math.cos(a)) * 0.10 * H + (0.02 * H if abs(i - 2) == 2 else 0))),
                                rot=(0, -a * 0.5, 0)))
    for sgn in (-1, 1):
        props.append(L.prim_cyl('curl%+d' % sgn, 0.022 * H, 0.022 * H, 0.06 * H,
                                arc_c + Vector((sgn * 0.20 * H, 0, 0.055 * H)), rot=(math.pi / 2, 0, 0), verts=10))
    # sheathed sword hanging point-down at the right hip
    props.append(L.prim_box('sheath', (0.024 * H, 0.018 * H, 0.34 * H), (0.145 * H, -0.045 * H, 0.36 * H)))
    props.append(L.prim_box('hilt', (0.07 * H, 0.016 * H, 0.018 * H), (0.145 * H, -0.045 * H, 0.545 * H)))
    props.append(L.prim_cyl('pom', 0.016 * H, 0.016 * H, 0.03 * H, (0.145 * H, -0.045 * H, 0.565 * H), verts=8))
    finish('pharnavaz', [m.mesh(), robe_o] + limbs, props, voxel=0.015, tris=30000, pat=0.5)


# --- street variety pack (light: ~3k tris each) ------------------------------
def street_variant(name, seed):
    reset()
    H = 1.8
    m = L.Meta('sv', resolution=0.022)
    import random
    rnd = random.Random(seed)
    style = seed % 5
    if style == 0:      # orator: right arm out
        armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
                Vector((0.22 * H, -0.10 * H, 0.80 * H)),
                Vector((0.30 * H, -0.20 * H, 0.88 * H))]
        j, limbs = L.figure(m, H=H, sway=0.05, arms={'R': armR}, coat={'hem': 0.08 * H})
        coat = [L.robe(H, r_hem=0.12 * H, top_z=0.6 * H, name='c', folds=False)]
    elif style == 1:    # standing coat, book
        j, limbs = L.figure(m, H=H, sway=0.04, coat={'hem': 0.08 * H})
        coat = [L.robe(H, r_hem=0.12 * H, top_z=0.6 * H, name='c', folds=False)]
    elif style == 2:    # female gown
        j, limbs = L.figure(m, H=H * 0.97, female=True, coat={'hem': 0.0})
        coat = [L.robe(H * 0.97, r_hem=0.14 * H, top_z=0.58 * H, name='c', folds=False)]
    elif style == 3:    # walker, bare legs
        j, limbs = L.figure(m, H=H, sway=0.06)
        coat = []
    else:               # thinker: arms crossed
        armR = [Vector((P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
                Vector((0.14 * H, -0.09 * H, 0.66 * H)),
                Vector((-0.08 * H, -0.11 * H, 0.70 * H))]
        armL = [Vector((-P['shoulder_w'] * H, 0, P['shoulder_y'] * H)),
                Vector((-0.14 * H, -0.09 * H, 0.62 * H)),
                Vector((0.08 * H, -0.11 * H, 0.66 * H))]
        j, limbs = L.figure(m, H=H, sway=0.05, arms={'R': armR, 'L': armL}, coat={'hem': 0.08 * H})
        coat = [L.robe(H, r_hem=0.12 * H, top_z=0.6 * H, name='c', folds=False)]
    finish(name, [m.mesh()] + coat + limbs, voxel=0.024, tris=3000, pat=0.5)


def bust_variant(name, seed):
    reset()
    H = 1.8
    m = L.Meta('bv', resolution=0.014)
    hh = P['head_h'] * H
    hw = P['head_w'] * H
    headc = Vector((0, 0, 0.30))
    L.face(m, headc, hh, hw, pitch=(0.06, 0.16, 0.10)[seed % 3])
    m.chain([(0, 0, headc.z - hh * 0.5), (0, 0, headc.z - hh * 0.85)], [H * 0.032, H * 0.038])
    # shoulders/chest cut like a classical herm
    m.ellipsoid((0, 0, headc.z - hh * 1.15), H * 0.055, (2.1, 0.85, 0.75))
    if seed % 3 == 1:  # hair mass
        m.ellipsoid(headc + Vector((0, 0.02 * H, hh * 0.28)), hh * 0.42, (1.05, 1.0, 0.7))
    core = L.join([m.mesh()])
    core = L.remesh_decimate(core, 0.016, 2400)
    # plinth block under the cut
    base = L.prim_box('cut', (0.30, 0.22, 0.06), (0, 0, 0.09))
    core = L.join([core, L.smooth(base)])
    L.patina(core, amount=0.4)
    L.export(core, name, preview=True)


ALL = {
    'kartlis_deda': kartlis_deda,
    'david': david,
    'george': george,
    'rustaveli': rustaveli,
    'nino': nino,
    'vazha': vazha,
    'writer_ilia': lambda: writer('writer_ilia', cane=True),
    'writer_akaki': lambda: writer('writer_akaki', cane=False),
    'pharnavaz': pharnavaz,
    'street0': lambda: street_variant('street0', 0),
    'street1': lambda: street_variant('street1', 1),
    'street2': lambda: street_variant('street2', 2),
    'street3': lambda: street_variant('street3', 3),
    'street4': lambda: street_variant('street4', 4),
    'bust0': lambda: bust_variant('bust0', 0),
    'bust1': lambda: bust_variant('bust1', 1),
    'bust2': lambda: bust_variant('bust2', 2),
}

only = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
for name, fn in ALL.items():
    if only and name not in only:
        continue
    print('BUILD', name)
    fn()
print('ALL DONE')
