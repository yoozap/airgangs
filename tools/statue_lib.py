# Realistic-figure library for the statue rebuild (blender-sculpture skill).
# Proportion-driven humans and horses: metaball masses + curve-bevel limbs +
# 5-mass faces, voxel remesh of the organic core only, crisp props joined
# unremeshed, vertex-color patina, normalized GLB export (height 1, base z=0).
# Import from a -b -P script; needs bpy.
import math
import os

import bpy
from mathutils import Euler, Matrix, Vector

SHOTS = os.environ.get('STATUE_SHOTS', '/tmp')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'public', 'models', 'statues')

# --- human proportion canon (fractions of total height H) -------------------
P = {
    'head_h': 0.118,      # monument canon (8.5 heads)
    'head_w': 0.094,
    'chin': 0.870,
    'eye': 0.936,
    'shoulder_y': 0.818,
    'shoulder_w': 0.135,  # half-width to shoulder joint
    'chest': 0.720,
    'waist': 0.630,
    'hip_y': 0.530,
    'hip_w': 0.093,       # half-width to hip joint
    'crotch': 0.500,
    'knee': 0.285,
    'ankle': 0.039,
    'foot_l': 0.140,
    'hand_l': 0.100,
    'upper_arm': 0.186,
    'forearm': 0.146,
}


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def eval_mesh(obj):
    """Freeze modifiers/metaballs/curves into a plain mesh object."""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    new = bpy.data.objects.new(obj.name + '_m', me)
    new.matrix_world = obj.matrix_world
    link(new)
    bpy.data.objects.remove(obj, do_unlink=True)
    return new


def join(parts):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    return bpy.context.object


def remesh_decimate(obj, voxel, tris):
    rm = obj.modifiers.new('rm', 'REMESH')
    rm.mode = 'VOXEL'
    rm.voxel_size = voxel
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    obj.modifiers.clear()
    obj.data = me
    obj.data.calc_loop_triangles()
    n = len(obj.data.loop_triangles)
    if n > tris:
        d = obj.modifiers.new('dec', 'DECIMATE')
        d.ratio = tris / n
        me = bpy.data.meshes.new_from_object(obj.evaluated_get(bpy.context.evaluated_depsgraph_get()))
        obj.modifiers.clear()
        obj.data = me
    for p in obj.data.polygons:
        p.use_smooth = True
    return obj


# --- metaball figure core ----------------------------------------------------


class Meta:
    """Collects metaball elements, converts to one mesh.

    Radii passed in are DESIRED SURFACE radii: the isosurface of an isolated
    ball forms at K*radius where K = sqrt(1 - (threshold/stiffness)^(1/3))
    (~0.575 at threshold .6 / stiffness 2), so we divide by K internally."""

    def __init__(self, name, resolution=0.018):
        self.mb = bpy.data.metaballs.new(name)
        self.mb.resolution = resolution
        self.mb.render_resolution = resolution
        self.mb.threshold = 0.6
        self.obj = link(bpy.data.objects.new(name, self.mb))

    def _k(self, stiff):
        return math.sqrt(max(0.05, 1 - (self.mb.threshold / stiff) ** (1 / 3)))

    def ball(self, co, r, stiff=2.0, neg=False):
        el = self.mb.elements.new()
        el.co = co
        el.radius = r / self._k(stiff)
        el.stiffness = stiff
        el.use_negative = neg
        return el

    def ellipsoid(self, co, r, scale, rot=(0, 0, 0), stiff=2.0, neg=False):
        el = self.mb.elements.new()
        el.type = 'ELLIPSOID'
        el.co = co
        el.radius = r / self._k(stiff)
        el.size_x, el.size_y, el.size_z = scale
        el.rotation = Euler(rot).to_quaternion()
        el.stiffness = stiff
        el.use_negative = neg
        return el

    def chain(self, pts, radii, stiff=2.0):
        """Capsule-ish chain: balls interpolated densely between waypoints."""
        for i in range(len(pts) - 1):
            a, b = Vector(pts[i]), Vector(pts[i + 1])
            ra, rb = radii[i], radii[i + 1]
            steps = max(2, int((b - a).length / (min(ra, rb) * 0.55)))
            for s in range(steps + 1):
                t = s / steps
                self.ball(a.lerp(b, t), ra + (rb - ra) * t, stiff)

    def mesh(self):
        return eval_mesh(self.obj)


def limb(name, pts, radii, res=8):
    """Tapered limb: bezier curve, per-point radius, round bevel."""
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = 1.0
    cu.bevel_resolution = 5
    cu.use_fill_caps = True
    cu.resolution_u = res
    sp = cu.splines.new('BEZIER')
    sp.bezier_points.add(len(pts) - 1)
    for bp, co, r in zip(sp.bezier_points, pts, radii):
        bp.co = Vector(co)
        bp.radius = r
        bp.handle_left_type = bp.handle_right_type = 'AUTO'
    return eval_mesh(link(bpy.data.objects.new(name, cu)))


def ribbon(name, pts, widths, side=(1, 0, 0), thick=0.02, sub=2):
    """Flat drapery strip (cloak, sash, veil): quads spanning `widths` along
    the `side` direction at each point, solidified to `thick`, subdivided so
    a later remesh/displace can fold it."""
    side = Vector(side).normalized()
    me = bpy.data.meshes.new(name)
    verts, faces = [], []
    for i, (co, w) in enumerate(zip(pts, widths)):
        c = Vector(co)
        verts += [c - side * (w / 2), c + side * (w / 2)]
        if i:
            k = 2 * i
            faces.append((k - 2, k - 1, k + 1, k))
    me.from_pydata(verts, [], faces)
    o = link(bpy.data.objects.new(name, me))
    s = o.modifiers.new('sub', 'SUBSURF')
    s.subdivision_type = 'SIMPLE'
    s.levels = sub
    sol = o.modifiers.new('sol', 'SOLIDIFY')
    sol.thickness = thick
    sol.offset = 0
    return eval_mesh(o)


def prim_cyl(name, r1, r2, depth, loc, rot=(0, 0, 0), verts=16):
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r1, radius2=r2, depth=depth, location=loc)
    o = bpy.context.object
    o.name = name
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def prim_box(name, dims, loc, rot=(0, 0, 0)):
    bpy.ops.object.select_all(action='DESELECT')
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = dims
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def smooth(o):
    for p in o.data.polygons:
        p.use_smooth = True
    return o


# --- face: the five shadow masses (sizes × head height hh) ------------------


def face(meta, head_c, hh, hw, yaw=0.0, pitch=0.17):
    """Adds skull + facial masses to `meta`. head_c = head center (chin+crown)/2.
    Face looks toward -Y (rotated by yaw around Z). pitch tilts the head down
    so the brow shadows the eyes."""
    rot = Matrix.Rotation(yaw, 3, 'Z') @ Matrix.Rotation(-pitch, 3, 'X')

    def at(x, y, z):  # face-local (x right, y toward viewer=-Y, z up from head center)
        return Vector(head_c) + rot @ Vector((x, -y, z))

    crown = hh / 2
    meta.ellipsoid(at(0, 0, 0.02 * hh), hh * 0.5, (0.72, 0.86, 1.0))          # skull
    meta.ellipsoid(at(0, hw * 0.30, crown - 0.45 * hh), hh * 0.30, (1.0, 0.42, 0.20))   # brow bar
    for s in (-1, 1):
        meta.ball(at(s * hw * 0.26, hw * 0.34, crown - 0.52 * hh), hh * 0.10, neg=True)  # eye sockets
        meta.ellipsoid(at(s * hw * 0.33, hw * 0.26, crown - 0.60 * hh), hh * 0.11, (0.9, 0.55, 0.7))  # cheekbones
        meta.ellipsoid(at(s * hw * 0.47, hw * 0.02, crown - 0.56 * hh), hh * 0.09, (0.35, 0.7, 1.0))  # ears
    meta.ellipsoid(at(0, hw * 0.40, crown - 0.63 * hh), hh * 0.13, (0.42, 0.55, 1.0))    # nose wedge
    meta.ellipsoid(at(0, hw * 0.30, crown - 0.90 * hh), hh * 0.11, (0.75, 0.6, 0.55))    # chin+jaw
    meta.ellipsoid(at(0, hw * 0.10, crown - 0.97 * hh), hh * 0.14, (0.85, 0.7, 0.5))     # jaw base


# --- posable human figure ----------------------------------------------------


def figure(meta, H=1.8, female=False, sway=0.05, arms=None, coat=None, head_yaw=0.0, head_pitch=0.17, arm_r=1.0):
    """Standing figure facing -Y. Origin under the weight (left) foot midline.
    sway: contrapposto pelvis shift (+x = weight on left leg).
    arms: dict {'R': [...points], 'L': [...]} in absolute coords, or callables(j) -> pts.
    coat: None or dict(hem=z, r_hem=..) — legs below hem are skipped, robe cone added.
    Returns dict of joint positions for prop placement."""
    hw = P['head_w'] * H * (0.94 if female else 1.0)
    hh = P['head_h'] * H
    shw = P['shoulder_w'] * H * (0.88 if female else 1.0)
    hpw = P['hip_w'] * H * (1.12 if female else 1.0)
    shoulder_z = P['shoulder_y'] * H
    hip_z = P['hip_y'] * H
    tilt = sway * 0.5

    j = {
        'shoulderR': Vector((shw, 0, shoulder_z + tilt * 0.3)),
        'shoulderL': Vector((-shw, 0, shoulder_z - tilt * 0.3)),
        'hipR': Vector((hpw + sway * 0.3, 0, hip_z - tilt)),
        'hipL': Vector((-hpw + sway * 0.3, 0, hip_z + tilt)),
        'head': Vector((0, -0.01 * H, (P['chin'] * H + P['chin'] * H + 2 * hh) / 2 - hh * 0.5 + hh * 0.5)),
    }
    head_c = Vector((0, -0.012 * H, P['chin'] * H + hh * 0.48))
    j['head'] = head_c

    # torso: pelvis, belly, chest, shoulders as overlapping ellipsoids
    pelvis_c = Vector((sway * 0.3, 0, hip_z))
    meta.ellipsoid(pelvis_c, H * 0.075, (hpw / (H * 0.075), 0.75, 0.9))
    meta.ellipsoid((sway * 0.15, 0.004 * H, P['waist'] * H), H * 0.068, (1.05, 0.78, 1.0))
    meta.ellipsoid((0, 0, P['chest'] * H), H * 0.085, (1.25, 0.80, 1.15))
    if female:
        meta.ellipsoid((0, -0.035 * H, P['chest'] * H + 0.01 * H), H * 0.055, (1.35, 0.7, 0.8))
    meta.ellipsoid((0, 0, shoulder_z - 0.005 * H), H * 0.055, (shw / (H * 0.055) * 1.06, 0.85, 0.8))
    meta.chain([(0, -0.005 * H, shoulder_z), (0, -0.01 * H, P['chin'] * H)], [H * 0.033, H * 0.028])  # neck

    face(meta, head_c, hh, hw, yaw=head_yaw, pitch=head_pitch)
    rot = Matrix.Rotation(head_yaw, 3, 'Z') @ Matrix.Rotation(-head_pitch, 3, 'X')
    j['crown_pos'] = head_c + rot @ Vector((0, 0, hh * 0.5))

    limbs = []
    # arms: default hang at sides slightly bent
    default = {
        'R': [j['shoulderR'], j['shoulderR'] + Vector((0.02 * H, -0.02 * H, -P['upper_arm'] * H)),
              j['shoulderR'] + Vector((0.03 * H, -0.06 * H, -(P['upper_arm'] + P['forearm']) * H))],
        'L': [j['shoulderL'], j['shoulderL'] + Vector((-0.02 * H, -0.02 * H, -P['upper_arm'] * H)),
              j['shoulderL'] + Vector((-0.03 * H, -0.06 * H, -(P['upper_arm'] + P['forearm']) * H))],
    }
    arms = arms or {}
    for side in ('R', 'L'):
        pts = arms.get(side, default[side])
        if callable(pts):
            pts = pts(j)
        r = [0.042 * H * arm_r, 0.031 * H * arm_r, 0.023 * H * arm_r]
        # deltoid mass at the shoulder
        meta.ball(pts[0], H * 0.052)
        limbs.append(limb('arm' + side, pts, r[:len(pts)] + [0.024 * H] * max(0, len(pts) - 3)))
        # hand: flattened ellipsoid continuing the forearm direction
        d = (Vector(pts[-1]) - Vector(pts[-2])).normalized()
        hc = Vector(pts[-1]) + d * (P['hand_l'] * H * 0.5)
        meta.ellipsoid(hc, P['hand_l'] * H * 0.5, (0.55, 0.8, 1.0),
                       rot=d.to_track_quat('Z', 'Y').to_euler())
        j['hand' + side] = Vector(pts[-1]) + d * (P['hand_l'] * H * 0.8)
        j['wrist' + side] = Vector(pts[-1])

    hem = coat['hem'] if coat is not None else None
    for side, hip in (('R', j['hipR']), ('L', j['hipL'])):
        wt = 1.0 if side == 'L' else 0.0  # weight leg straight, free leg bent
        knee = Vector((hip.x + (0.012 if wt else 0.035) * H, (-0.028 * H if not wt else 0), P['knee'] * H))
        ankle = Vector((hip.x + (0.008 if wt else 0.05) * H, (0.012 * H if wt else -0.02 * H), P['ankle'] * H))
        if hem is None:
            # bare legs, hip to ankle
            limbs.append(limb('leg' + side, [hip, knee, ankle], [0.062 * H, 0.042 * H, 0.026 * H]))
            f = prim_box('foot' + side, (0.052 * H, P['foot_l'] * H, 0.045 * H),
                         (ankle.x, ankle.y - P['foot_l'] * H * 0.32, 0.024 * H))
            limbs.append(smooth(f))
        elif hem >= P['knee'] * H * 0.6:
            # knee-length coat: shins visible below the hem
            limbs.append(limb('leg' + side, [Vector((hip.x, 0, hem + 0.05 * H)), knee, ankle],
                              [0.05 * H, 0.042 * H, 0.026 * H]))
            f = prim_box('foot' + side, (0.052 * H, P['foot_l'] * H, 0.045 * H),
                         (ankle.x, ankle.y - P['foot_l'] * H * 0.32, 0.024 * H))
            limbs.append(smooth(f))
        else:
            # full-length gown: just suggest feet peeking under the hem
            f = prim_box('foot' + side, (0.055 * H, P['foot_l'] * H * 0.8, 0.05 * H),
                         (hip.x * 0.7, -0.10 * H, 0.026 * H))
            limbs.append(smooth(f))
    j['H'] = H
    j['hh'] = hh
    j['crown'] = j['crown_pos'].z
    return j, limbs


def robe(H, r_hem, hem_flare=1.0, top_z=None, top_r=None, folds=True, name='robe', sway=0.03):
    """A-line gown/coat from shoulders/waist to ground with displaced folds.
    Returns a mesh object (dense, ready to join into organic core)."""
    top_z = top_z if top_z is not None else P['waist'] * H
    top_r = top_r if top_r is not None else 0.085 * H
    o = prim_cyl(name, r_hem, top_r, top_z, (sway * 0.3 * H, 0, top_z / 2), verts=48)
    # subdivide vertically so folds can form: use subsurf simple
    sub = o.modifiers.new('sub', 'SUBSURF')
    sub.subdivision_type = 'SIMPLE'
    sub.levels = 3
    o = eval_mesh(o)
    if folds:
        tex = bpy.data.textures.new(name + '_folds', 'MARBLE')
        tex.noise_scale = 0.55
        tex.turbulence = 6.0
        tex.noise_depth = 1
        emp = link(bpy.data.objects.new(name + '_e', None))
        emp.scale = (1, 1, 5.5)
        mod = o.modifiers.new('folds', 'DISPLACE')
        mod.texture = tex
        mod.direction = 'NORMAL'
        mod.strength = 0.035 * H * hem_flare
        mod.mid_level = 0.5
        mod.texture_coords = 'OBJECT'
        mod.texture_coords_object = emp
        o = eval_mesh(o)
        bpy.data.objects.remove(emp, do_unlink=True)
    return o


# --- horse (unit = withers height W; head length HL = W / 2.6) --------------


def horse(meta, W=1.6, levade=0.0, head_arch=1.0):
    """Horse facing +X, standing on z=0. levade: 0 standing .. 1 full rear.
    Returns joints dict (saddle, poll, tail...) and limb mesh list."""
    HL = W / 2.6
    body_l = W * 1.02
    belly_z = W * 0.58
    top_z = W * 0.94
    # spine masses: chest, barrel, rump
    meta.ellipsoid((body_l * 0.32, 0, W * 0.70), W * 0.19, (1.05, 0.88, 1.25))   # chest/shoulder
    meta.ellipsoid((0, 0, W * 0.72), W * 0.21, (1.5, 0.88, 1.02))                # barrel
    meta.ellipsoid((-body_l * 0.33, 0, W * 0.74), W * 0.185, (1.12, 0.92, 1.1))  # rump
    meta.ellipsoid((-body_l * 0.30, 0, W * 0.88), W * 0.10, (0.9, 0.75, 0.9))    # croup
    meta.ellipsoid((body_l * 0.30, 0, W * 0.92), W * 0.09, (0.8, 0.7, 1.1))      # withers
    # neck: arched C-curve, poll high, head near vertical
    n0 = Vector((body_l * 0.40, 0, W * 0.88))
    n1 = Vector((body_l * 0.52, 0, W * 1.12))
    poll = Vector((body_l * 0.60, 0, W * 1.24 * head_arch + W * 1.02 * (1 - head_arch)))
    meta.chain([n0, n1, poll], [W * 0.125, W * 0.095, W * 0.055])
    # mane ridge along the neck topline
    for t in (0.15, 0.4, 0.65, 0.9):
        p = n0.lerp(n1, min(1, t * 1.6)) if t < 0.6 else n1.lerp(poll, (t - 0.6) / 0.4)
        meta.ellipsoid(p + Vector((-W * 0.045, 0, W * 0.05)), W * 0.055, (0.9, 0.32, 0.85))
    # head: jaw mass + muzzle taper, near-vertical nasal line
    jaw = poll + Vector((HL * 0.30, 0, -HL * 0.34))
    muzzle = poll + Vector((HL * 0.42, 0, -HL * 0.95))
    meta.ellipsoid(jaw, HL * 0.30, (0.85, 0.62, 0.95))
    meta.chain([jaw, muzzle], [HL * 0.20, HL * 0.135])
    meta.ellipsoid(muzzle + Vector((HL * 0.05, 0, -HL * 0.03)), HL * 0.14, (0.85, 0.75, 0.7))
    for s in (-1, 1):
        meta.ellipsoid(poll + Vector((-HL * 0.05, s * HL * 0.16, HL * 0.16)), HL * 0.12,
                       (0.32, 0.2, 0.9), rot=(s * 0.25, 0.3, 0))          # ears
        meta.ball(poll + Vector((HL * 0.18, s * HL * 0.17, -HL * 0.18)), HL * 0.075)  # eye ridge
    limbs = []
    hoof_h = W * 0.045
    # forelegs: shoulder→elbow→knee→fetlock→hoof
    for s in (-1, 1):
        sh = Vector((body_l * 0.32, s * W * 0.115, W * 0.62))
        if levade > 0.3:
            el = sh + Vector((W * 0.10, 0, -W * 0.14))
            kn = el + Vector((W * 0.16, s * -0.01, -W * 0.05))
            ft = kn + Vector((-W * 0.02, 0, -W * 0.17))
            hf = ft + Vector((-W * 0.06, 0, -W * 0.05))
        else:
            el = sh + Vector((W * 0.02, 0, -W * 0.17))
            kn = Vector((body_l * 0.33, s * W * 0.105, W * 0.30))
            ft = Vector((body_l * 0.335, s * W * 0.10, W * 0.12))
            hf = Vector((body_l * 0.34, s * W * 0.10, hoof_h))
            if s == 1 and levade == 0:  # walking: near fore raised
                kn += Vector((W * 0.10, 0, W * 0.12))
                ft += Vector((W * 0.06, 0, W * 0.22))
                hf = ft + Vector((-W * 0.04, 0, -W * 0.09))
        limbs.append(limb('fore%+d' % s, [sh, el, kn, ft, hf],
                          [W * 0.085, W * 0.062, W * 0.038, W * 0.030, W * 0.034]))
        limbs.append(prim_cyl('hooff%+d' % s, W * 0.041, W * 0.048, hoof_h, hf, verts=10))
    # hind legs: hip→stifle→hock→fetlock→hoof
    for s in (-1, 1):
        hip = Vector((-body_l * 0.33, s * W * 0.12, W * 0.66))
        st = Vector((-body_l * 0.28, s * W * 0.125, W * 0.44))
        ho = Vector((-body_l * 0.40, s * W * 0.115, W * 0.30))
        ft = Vector((-body_l * 0.38, s * W * 0.108, W * 0.10))
        hf = Vector((-body_l * 0.375, s * W * 0.105, hoof_h))
        limbs.append(limb('hind%+d' % s, [hip, st, ho, ft, hf],
                          [W * 0.10, W * 0.075, W * 0.045, W * 0.030, W * 0.034]))
        limbs.append(prim_cyl('hoofh%+d' % s, W * 0.042, W * 0.05, hoof_h, (hf.x, hf.y, hoof_h / 2), verts=10))
    # tail: hangs (or strut when rearing)
    tail_pts = [Vector((-body_l * 0.40, 0, W * 0.82)), Vector((-body_l * 0.50, 0, W * 0.50)),
                Vector((-body_l * 0.52, 0, W * 0.14 if levade > 0.3 else W * 0.28))]
    limbs.append(limb('tail', tail_pts, [W * 0.055, W * 0.06, W * 0.035]))
    j = {'saddle': Vector((-body_l * 0.02, 0, W * 0.97)), 'poll': poll, 'W': W, 'HL': HL,
         'chest': Vector((body_l * 0.35, 0, W * 0.7))}
    return j, limbs


def pitch_all(objs, deg, pivot):
    """Rotate finished meshes around world-Y at pivot (for levade poses)."""
    m = Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(deg), 4, 'Y') @ Matrix.Translation(-Vector(pivot))
    for o in objs:
        o.data.transform(m)


# --- patina vertex colors -----------------------------------------------------

BRONZE_C = (0.36, 0.26, 0.16, 1.0)   # srgb-ish mid bronze (lamberts multiply it)
PATINA_C = (0.45, 0.62, 0.55, 1.0)
DARK_C = (0.22, 0.17, 0.12, 1.0)


def patina(obj, amount=0.55, streak=0.5):
    """Vertex-color weathering: verdigris in up-facing rain zones + crevices,
    darkened under-surfaces, bronze on high points. Exported as COLOR_0."""
    me = obj.data
    me.calc_loop_triangles()
    attr = me.color_attributes.new('Col', 'BYTE_COLOR', 'POINT')
    zs = [v.co.z for v in me.vertices]
    z0, z1 = min(zs), max(zs)
    import random
    rnd = random.Random(7)
    for i, v in enumerate(me.vertices):
        up = max(0.0, v.normal.z)
        t = up * up * amount
        t += streak * 0.25 * max(0.0, math.sin(v.co.x * 37 + v.co.y * 23) * 0.5 + 0.2) * up
        h = (v.co.z - z0) / max(1e-6, z1 - z0)
        t *= 0.55 + 0.45 * h  # rain hits the top more
        t = min(1.0, t + rnd.uniform(-0.06, 0.06))
        down = max(0.0, -v.normal.z) * 0.5
        c = [BRONZE_C[k] * (1 - t) + PATINA_C[k] * t for k in range(3)]
        c = [c[k] * (1 - down) + DARK_C[k] * down for k in range(3)]
        attr.data[i].color_srgb = (*c, 1.0)


# --- normalize / preview / export --------------------------------------------


def normalize(obj):
    me = obj.data
    xs = [v.co.x for v in me.vertices]
    ys = [v.co.y for v in me.vertices]
    zs = [v.co.z for v in me.vertices]
    h = max(zs) - min(zs)
    s = 1.0 / h
    me.transform(Matrix.Translation((-(min(xs) + max(xs)) / 2, -(min(ys) + max(ys)) / 2, -min(zs))))
    me.transform(Matrix.Diagonal((s, s, s, 1)))
    return h


def shoot(obj, name, views=('front', 'side', 'pers')):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'VERTEX' if obj.data.color_attributes else 'MATERIAL'
    sc.render.resolution_x = 560
    sc.render.resolution_y = 680
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    c = Vector(((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, (max(zs) + min(zs)) / 2))
    dim = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
    dirs = {'front': Vector((0, -1, 0.10)), 'side': Vector((1, 0, 0.10)),
            'pers': Vector((0.8, -1, 0.35)), 'back': Vector((0, 1, 0.1))}
    for label in views:
        cam = link(bpy.data.objects.new('cam' + label, bpy.data.cameras.new('c')))
        sc.camera = cam
        cam.location = c + dirs[label].normalized() * dim * 2.1
        tgt = link(bpy.data.objects.new('t' + label, None))
        tgt.location = c
        tr = cam.constraints.new('TRACK_TO')
        tr.target = tgt
        tr.track_axis = 'TRACK_NEGATIVE_Z'
        tr.up_axis = 'UP_Y'
        sc.render.filepath = os.path.join(SHOTS, f's2_{name}_{label}.png')
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(cam, do_unlink=True)
        bpy.data.objects.remove(tgt, do_unlink=True)


def export(obj, name, preview=True):
    h = normalize(obj)
    if preview:
        shoot(obj, name)
    os.makedirs(OUT, exist_ok=True)
    fp = os.path.join(OUT, f'{name}.glb')
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=fp, export_format='GLB', use_selection=True)
    obj.data.calc_loop_triangles()
    print(f'EXPORT {name}: natural h {h:.2f}, {len(obj.data.loop_triangles)} tris -> {fp}')
