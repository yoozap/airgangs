# Build animated character GLBs for the WoW-style personage system.
#
# Imports the modular fantasy outfit glTFs (rigged, UE-style skeleton, no
# animations), authors procedural Idle + Run cycles by posing bones in
# armature space (pb.matrix — rest-orientation agnostic), and exports one
# GLB per race to public/models/personages/.
#
# Run: /Applications/Blender.app/Contents/MacOS/Blender --background --python tools/build_personages.py
# Blender 4.2+.

import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

# ---------------------------------------------------------------- CONFIG ----
BASE = os.path.expanduser("~/Desktop/game/personages")
OUTFITS = (
    BASE + "/Modular Character Outfits - Fantasy[Standard]/"
    "Modular Character Outfits - Fantasy[Standard]/Exports/glTF (Godot-Unreal)/Outfits"
)
ROBOT = BASE + "/Animated Robot by Quaternius/FBX"
ENEMY = BASE + "/Easy Animated Enemy Pack - Jan 2019/Easy Animated Enemy Pack - Jan 2019/FBX"
FARM = BASE + "/Farm Animals Animated  by Quaternius/FBX"
OUT_DIR = os.path.expanduser("~/tbilisifly/public/models/personages")

# Every model in the personages folder becomes a playable hero body.
# gltf kind → rigged but unanimated: procedural Idle/Run cycles (Poser below).
# fbx kind → the pack's own clips, remapped to Idle/Run by name suffix.
# height = target size in meters (models arrive in wildly different units).
PERSONAGES = {
    # modular fantasy humans — an OUTFITS pack, so the heads are built
    # procedurally (add_head) and skinned to the Head bone
    "ranger_m":  {"kind": "gltf", "src": OUTFITS + "/Male_Ranger.gltf", "height": 1.83,
                  "gender": "m", "hooded": True},
    "peasant_m": {"kind": "gltf", "src": OUTFITS + "/Male_Peasant.gltf", "height": 2.05,
                  "gender": "m", "hooded": False},
    "ranger_f":  {"kind": "gltf", "src": OUTFITS + "/Female_Ranger.gltf", "height": 1.72,
                  "gender": "f", "hooded": True},
    "peasant_f": {"kind": "gltf", "src": OUTFITS + "/Female_Peasant.gltf", "height": 1.70,
                  "gender": "f", "hooded": False},
    # Quaternius robot — full clip set, Running is a proper run
    "robot": {"kind": "fbx", "src": ROBOT + "/Robot.fbx", "height": 2.4,
              "idle": "Robot_Idle", "run": "Robot_Running"},
    # enemy pack
    "spider": {"kind": "fbx", "src": ENEMY + "/Spider.fbx", "height": 1.3,
               "idle": "Spider_Idle", "run": "Spider_Walk"},
    "rat":    {"kind": "fbx", "src": ENEMY + "/Rat.fbx", "height": 1.0,
               "idle": "Rat_Idle", "run": "Rat_Run"},
    "snake":  {"kind": "fbx", "src": ENEMY + "/Snake_angry.fbx", "height": 1.4,
               "idle": "Snake_Idle", "run": "Snake_Walk"},
    "frog":   {"kind": "fbx", "src": ENEMY + "/Frog.fbx", "height": 0.9,
               "idle": "Frog_Idle", "run": "Frog_Jump"},
    "wasp":   {"kind": "fbx", "src": ENEMY + "/Wasp.fbx", "height": 1.1,
               "idle": "Wasp_Flying", "run": "Wasp_Flying"},
    # farm animals — generic clip names, matched among each file's OWN actions
    "horse": {"kind": "fbx", "src": FARM + "/Horse.fbx", "height": 2.2, "idle": "Idle", "run": "Run"},
    "zebra": {"kind": "fbx", "src": FARM + "/Zebra.fbx", "height": 2.2, "idle": "Idle", "run": "Run"},
    "cow":   {"kind": "fbx", "src": FARM + "/Cow.fbx", "height": 1.9, "idle": "Idle", "run": "Run"},
    "pig":   {"kind": "fbx", "src": FARM + "/Pig.fbx", "height": 1.5, "idle": "Idle", "run": "Run"},
    "sheep": {"kind": "fbx", "src": FARM + "/Sheep.fbx", "height": 1.3, "idle": "Idle", "run": "Run"},
    "llama": {"kind": "fbx", "src": FARM + "/Llama.fbx", "height": 1.9, "idle": "Idle", "run": "Run"},
    "pug":   {"kind": "fbx", "src": FARM + "/Pug.fbx", "height": 0.7, "idle": "Idle", "run": "Run"},
    # J-Toastie Santa (Poly Pizza) — clips: Grounded/Idle/Jump/Sprint/Walk
    "santa": {"kind": "fbx",
              "src": os.path.expanduser("~/Desktop/game/characters/santa/SantaClaus.fbx"),
              "height": 1.8, "idle": "Idle", "run": "Sprint"},
}

FPS = 24
MAX_TEX = 1024          # px — cap texture sides so each GLB stays web-friendly
RUN_FRAMES = 16         # 0.667 s run cycle
IDLE_FRAMES = 48        # 2 s breathing loop
D = math.radians        # degrees → radians shorthand


def clean_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for act in list(bpy.data.actions):
        act.use_fake_user = False  # else stashed clips survive and poison the
    for coll in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials,
                 bpy.data.images, bpy.data.actions):  # next file's clip diff
        for block in list(coll):
            if block.users == 0:
                coll.remove(block)


def import_model(path):
    before = set(bpy.data.objects)
    before_acts = set(bpy.data.actions)
    if path.lower().endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    new_acts = [a for a in bpy.data.actions if a not in before_acts]
    arm = next(o for o in new if o.type == "ARMATURE")
    return arm, new, new_acts


# FBX action names look like "Armature|Armature|Rat_Run"; match by suffix
# among THIS file's own actions only (packs share armature names).
def pick_clip(actions, want):
    for a in actions:
        tail = a.name.split("|")[-1]
        if tail == want or tail.startswith(want + "."):
            return a
    return None


# Blender 5.x actions are layered ("slotted"); legacy .fcurves is gone.
def action_fcurves(act):
    if hasattr(act, "fcurves"):
        return act.fcurves
    fcs = []
    for layer in act.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                fcs.extend(bag.fcurves)
    return fcs


def normalize_height(arm, objects, target, fix_actions=None):
    # Sources arrive in cm, meters and everything between: scale the whole
    # character to `target` meters and APPLY it (leaving scale on the armature
    # node breaks glTF skinning downstream). Applying does NOT touch baked
    # pose-location f-curves, so existing clips (fix_actions) are rescaled by
    # the same factor — otherwise a horse's gallop bob stays in raw FBX units.
    bpy.context.view_layer.update()
    zs = []
    for o in objects:
        if o.type != "MESH":
            continue
        for corner in o.bound_box:
            zs.append((o.matrix_world @ Vector(corner)).z)
    height = max(zs) - min(zs)
    arm.scale = arm.scale * (target / height)
    factor = arm.scale[0]  # total scale the apply below bakes into the data
    bpy.context.view_layer.update()
    for o in bpy.data.objects:
        o.select_set(o in objects or o is arm)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    for act in fix_actions or []:
        for fc in action_fcurves(act):
            if fc.data_path.endswith("location"):
                for kp in fc.keyframe_points:
                    kp.co[1] *= factor
                    kp.handle_left[1] *= factor
                    kp.handle_right[1] *= factor


def cap_textures():
    for img in bpy.data.images:
        w, h = img.size
        if max(w, h) > MAX_TEX:
            f = MAX_TEX / max(w, h)
            img.scale(int(w * f), int(h * f))


class Poser:
    """Pose bones with armature-space rotations about each bone's head —
    works whatever the rig's rest orientations are. Parents must be posed
    before children (apply() handles the ordering)."""

    def __init__(self, arm):
        self.arm = arm
        self.pose = arm.pose
        for pb in self.pose.bones:
            pb.rotation_mode = "QUATERNION"
        # Facing: from the ankle toward the toes — the run axes derive from it.
        foot = self.pose.bones["foot_l"].bone.matrix_local.translation
        ball = self.pose.bones["ball_l"].bone.matrix_local.translation
        fwd = Vector((ball.x - foot.x, ball.y - foot.y, 0.0)).normalized()
        self.fwd = fwd
        self.up = Vector((0, 0, 1))
        # +angle about swing_axis moves a hanging limb FORWARD (verified math).
        self.swing = fwd.cross(self.up).normalized() * -1.0
        # The rig rests in a T-pose: measure each arm's direction and find the
        # side-rotation sign that swings it DOWN toward the body.
        self.down = {}
        for side in ("l", "r"):
            ua = self.pose.bones[f"upperarm_{side}"].bone.matrix_local.translation
            hand = self.pose.bones[f"hand_{side}"].bone.matrix_local.translation
            armdir = (hand - ua).normalized()
            self.down[side] = -1.0 if self.fwd.cross(armdir).z > 0 else 1.0
        # depth order so parents pose first
        self._depth = {}
        for pb in self.pose.bones:
            d, b = 0, pb.bone
            while b.parent:
                d, b = d + 1, b.parent
            self._depth[pb.name] = d

    def reset(self):
        for pb in self.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()

    def apply(self, spec, frame, keyed):
        """spec: {bone: (swing_deg, side_deg, twist_deg, lift_z)} — swing is
        forward/back, side is lateral tilt, twist is about the up axis."""
        self.reset()
        for name in sorted(spec, key=lambda n: self._depth[n]):
            swing_deg, side_deg, twist_deg, lift = spec[name]
            pb = self.pose.bones[name]
            bpy.context.view_layer.update()
            M = pb.matrix.copy()
            pivot = M.to_translation()
            R = (
                Matrix.Rotation(D(twist_deg), 4, self.up)
                @ Matrix.Rotation(D(side_deg), 4, self.fwd)
                @ Matrix.Rotation(D(swing_deg), 4, self.swing)
            )
            pb.matrix = (
                Matrix.Translation(pivot + Vector((0, 0, lift)))
                @ R
                @ Matrix.Translation(-pivot)
                @ M
            )
        bpy.context.view_layer.update()
        for name in keyed:
            pb = self.pose.bones[name]
            pb.keyframe_insert("rotation_quaternion", frame=frame)
            pb.keyframe_insert("location", frame=frame)


# Bones every action keys — keying the same set in Idle and Run keeps glTF
# clips complete, so switching animations in three.js can't leave stale poses.
KEYED = [
    "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "Head",
    "clavicle_l", "clavicle_r", "upperarm_l", "upperarm_r",
    "lowerarm_l", "lowerarm_r", "hand_l", "hand_r",
    "thigh_l", "thigh_r", "calf_l", "calf_r",
    "foot_l", "foot_r", "ball_l", "ball_r",
]


def run_pose(phase, P):
    """One instant of a run cycle. phase in [0, 2π); left leg leads."""
    sL = math.sin(phase)
    sR = math.sin(phase + math.pi)
    dnL, dnR = P.down["l"], P.down["r"]
    # asymmetric thigh swing: big kick forward, shorter trail behind
    thigh = lambda s: 52 * max(s, 0.0) + 30 * min(s, 0.0)
    # knee folds hardest mid-recovery (leg swinging forward)
    knee = lambda ph: -(18 + 58 * (0.5 + 0.5 * math.sin(ph - 1.9)))
    foot = lambda s: 12 * s + 8
    arm = lambda s: 38 * s
    bob = 0.05 * math.sin(2 * phase) - 0.03  # two bounces per stride
    sway = 6 * math.sin(phase)               # shoulder/hip counter-rotation
    return {
        "pelvis":     (10, 0, sway, bob),          # forward lean + hip twist
        "spine_01":   (6, 0, -sway * 0.6, 0),
        "spine_02":   (4, 0, -sway * 0.6, 0),
        "spine_03":   (2, 0, 0, 0),
        "neck_01":    (-8, 0, 0, 0),               # head counter-lean, eyes up
        "Head":       (-6, 0, 0, 0),
        "clavicle_l": (0, dnL * 4, 0, 0),
        "clavicle_r": (0, dnR * 4, 0, 0),
        # arms hang from the T-pose (dn*58) and pump counter to the legs
        "upperarm_l": (arm(sR), dnL * 58, 0, 0),
        "upperarm_r": (arm(sL), dnR * 58, 0, 0),
        "lowerarm_l": (55 + 12 * sR, 0, 0, 0),     # elbows pumped, never straight
        "lowerarm_r": (55 + 12 * sL, 0, 0, 0),
        "hand_l":     (10, 0, 0, 0),
        "hand_r":     (10, 0, 0, 0),
        "thigh_l":    (thigh(sL), 0, 0, 0),
        "thigh_r":    (thigh(sR), 0, 0, 0),
        "calf_l":     (knee(phase), 0, 0, 0),
        "calf_r":     (knee(phase + math.pi), 0, 0, 0),
        "foot_l":     (foot(sL), 0, 0, 0),
        "foot_r":     (foot(sR), 0, 0, 0),
        "ball_l":     (0, 0, 0, 0),
        "ball_r":     (0, 0, 0, 0),
    }


def idle_pose(phase, P):
    """Relaxed stand with a slow breath; arms hang with a slight bend."""
    br = math.sin(phase)  # one breath per loop
    dnL, dnR = P.down["l"], P.down["r"]
    return {
        "pelvis":     (1, 0, 0, -0.005 + 0.005 * br),
        "spine_01":   (1 + 1.2 * br, 0, 0, 0),
        "spine_02":   (1 + 1.5 * br, 0, 0, 0),
        "spine_03":   (0.5 + 0.8 * br, 0, 0, 0),
        "neck_01":    (-2 - 0.8 * br, 0, 0, 0),
        "Head":       (-1, 0, 0, 0),
        "clavicle_l": (0, dnL * (1 + 0.6 * br), 0, 0),
        "clavicle_r": (0, dnR * (1 + 0.6 * br), 0, 0),
        # hang from the T-pose, a touch out from the body, breathing slightly
        "upperarm_l": (3, dnL * (66 + 0.8 * br), 0, 0),
        "upperarm_r": (3, dnR * (66 + 0.8 * br), 0, 0),
        "lowerarm_l": (12, 0, 0, 0),
        "lowerarm_r": (12, 0, 0, 0),
        "hand_l":     (5, 0, 0, 0),
        "hand_r":     (5, 0, 0, 0),
        "thigh_l":    (0, -1.5, 0, 0),   # feet a touch apart
        "thigh_r":    (0, 1.5, 0, 0),
        "calf_l":     (-3, 0, 0, 0),
        "calf_r":     (-3, 0, 0, 0),
        "foot_l":     (3, 0, 0, 0),
        "foot_r":     (3, 0, 0, 0),
        "ball_l":     (0, 0, 0, 0),
        "ball_r":     (0, 0, 0, 0),
    }


def author_action(arm, poser, name, n_frames, pose_fn, step):
    act = bpy.data.actions.new(name)
    if not arm.animation_data:
        arm.animation_data_create()
    arm.animation_data.action = act
    for f in range(0, n_frames + 1, step):
        phase = 2 * math.pi * (f % n_frames) / n_frames  # wrap key == key 0
        poser.apply(pose_fn(phase, poser), f + 1, KEYED)
    act.use_fake_user = True
    # stash on NLA so the exporter emits it as its own glTF animation clip
    track = arm.animation_data.nla_tracks.new()
    track.name = name
    track.mute = True
    track.strips.new(name, 1, act)
    arm.animation_data.action = None
    return act


# ------------------------------------------------------------ heads --------
# The modular pack is outfits-only: hoods and collars with nothing inside.
# Build a stylized low-poly head — skull, eyes, brows, nose, mouth, ears,
# hair (cap + bun for women) — and skin it 100% to the Head bone so it
# follows every animation. Hooded characters get a slightly smaller, deeper
# head so the face peeks out of the hood without clipping.
# fallback flat tone; the real skin comes from the body's own texture — all
# head-skin UVs are pinned to a uniform skin patch found in T_Regular_*_Dark
# so the face matches the hands EXACTLY under any lighting
SKIN = {"m": (0.647, 0.447, 0.314, 1), "f": (0.651, 0.459, 0.318, 1)}
SKIN_UV = {"m": (0.0273, 0.9961), "f": (0.2461, 0.9961)}
HAIR = {"m": (0.16, 0.10, 0.05, 1), "f": (0.35, 0.19, 0.07, 1)}
DARK_EYE = (0.06, 0.045, 0.04, 1)
MOUTH = (0.45, 0.24, 0.20, 1)


def _srgb2lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _mat(name, rgba):
    # colors above are picked as sRGB; Blender sockets take LINEAR — feeding
    # them raw washes the face out to porcelain in the game
    lin = tuple(_srgb2lin(c) for c in rgba[:3]) + (rgba[3],)
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = lin
        bsdf.inputs["Roughness"].default_value = 0.85
    m.diffuse_color = lin
    return m


def _sphere(bm, center, radii, mat_i, u=12, v=8):
    ret = bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0)
    verts = ret["verts"]
    for vx in verts:
        vx.co = Vector((vx.co.x * radii[0], vx.co.y * radii[1], vx.co.z * radii[2])) + Vector(center)
    for f in {f for vx in verts for f in vx.link_faces}:
        f.material_index = mat_i
        f.smooth = True
    return verts


def _box(bm, center, size, mat_i):
    ret = bmesh.ops.create_cube(bm, size=1.0)
    verts = ret["verts"]
    for vx in verts:
        vx.co = Vector((vx.co.x * size[0], vx.co.y * size[1], vx.co.z * size[2])) + Vector(center)
    for f in {f for vx in verts for f in vx.link_faces}:
        f.material_index = mat_i
    return verts


def add_head(arm, fwd, gender, hooded, height):
    bone = arm.data.bones.get("Head")
    if bone is None:
        return
    s = height / 1.8  # feature sizes tuned on a 1.8 m human
    if hooded:
        s *= 0.88  # fit inside the hood
    bm = bmesh.new()
    skin_i, hair_i, eye_i, mouth_i = 0, 1, 2, 3

    # local frame: X lateral, +Y face direction, Z up (mapped to armature below)
    rx = 0.082 * s * (0.94 if gender == "f" else 1.0)
    ry, rz = 0.088 * s, 0.108 * s
    _sphere(bm, (0, 0, 0), (rx, ry, rz), skin_i, u=14, v=10)          # skull
    _sphere(bm, (0, -0.01 * s, -rz * 0.9), (rx * 0.55, ry * 0.55, 0.05 * s), skin_i)  # jaw/neck plug
    for sx in (-1, 1):
        _sphere(bm, (sx * 0.038 * s, ry * 0.80, 0.012 * s), (0.014 * s,) * 3, eye_i, u=8, v=6)   # eyes
        _box(bm, (sx * 0.038 * s, ry * 0.84, 0.048 * s), (0.032 * s, 0.012 * s, 0.009 * s), hair_i)  # brows
        if not hooded:
            _sphere(bm, (sx * rx * 0.98, 0, -0.005 * s), (0.012 * s, 0.02 * s, 0.028 * s), skin_i, u=8, v=6)  # ears
    _sphere(bm, (0, ry * 0.97, -0.012 * s), (0.016 * s, 0.022 * s, 0.026 * s), skin_i, u=8, v=6)  # nose
    _box(bm, (0, ry * 0.88, -0.062 * s), (0.036 * s, 0.010 * s, 0.008 * s), mouth_i)              # mouth
    if not hooded:
        # hair cap: a slightly larger sphere, lower half cut away
        cap = _sphere(bm, (0, -0.008 * s, 0.012 * s), (rx * 1.09, ry * 1.06, rz * 1.04), hair_i, u=14, v=10)
        # hairline: open the whole face (front stays above the brows), keep the nape low
        low = [vx for vx in cap if (vx.co.z < 0.055 * s and vx.co.y > 0) or (vx.co.z < 0.005 * s and vx.co.y > -ry * 0.45)]
        bmesh.ops.delete(bm, geom=low, context="VERTS")
        if gender == "f":
            _sphere(bm, (0, -ry * 0.9, rz * 0.15), (0.045 * s,) * 3, hair_i)  # bun

    me = bpy.data.meshes.new("HeroHead")
    bm.to_mesh(me)
    bm.free()
    # skin slot: the BODY's own textured material, UVs pinned to a plain skin
    # patch — identical shading to the hands, whatever the light does
    body_skin = next(
        (m for m in bpy.data.materials if "Regular" in m.name and m.users), None
    )
    me.materials.append(body_skin or _mat("HeadSkin_" + gender, SKIN[gender]))
    me.materials.append(_mat("HeadHair_" + gender, HAIR[gender]))
    me.materials.append(_mat("HeadEye", DARK_EYE))
    me.materials.append(_mat("HeadMouth", MOUTH))
    uvs = me.uv_layers.new(name="UVMap")
    u, v = SKIN_UV[gender]
    for lp in me.loops:
        uvs.data[lp.index].uv = (u, v)
    ob = bpy.data.objects.new("HeroHead", me)
    bpy.context.scene.collection.objects.link(ob)

    # place at the Head bone, facing the way the character faces
    up = Vector((0, 0, 1))
    f2 = Vector((fwd.x, fwd.y, 0)).normalized()
    right = f2.cross(up).normalized() * -1.0
    center = bone.head_local.lerp(bone.tail_local, 0.68)
    if hooded:
        center -= f2 * 0.015 * s  # deeper into the hood
    frame = Matrix((
        (right.x, f2.x, up.x, center.x),
        (right.y, f2.y, up.y, center.y),
        (right.z, f2.z, up.z, center.z),
        (0, 0, 0, 1),
    ))
    ob.matrix_world = frame
    ob.parent = arm
    ob.matrix_parent_inverse = arm.matrix_world.inverted()
    vg = ob.vertex_groups.new(name="Head")
    vg.add(list(range(len(me.vertices))), 1.0, "REPLACE")
    mod = ob.modifiers.new("Armature", "ARMATURE")
    mod.object = arm
    return ob


# Keep only Idle/Run from a pack's clip set, renamed canonically and stashed
# on NLA tracks so the glTF exporter emits exactly two animation clips.
def prepare_fbx_actions(arm, acts, idle_name, run_name):
    idle = pick_clip(acts, idle_name) or (acts[0] if acts else None)
    run = pick_clip(acts, run_name)
    for fallback in ("Run", "Walk", "WalkSlow", "Jump"):  # not every animal runs
        if run is None:
            run = pick_clip(acts, fallback)
    if run is None:
        run = idle
    if not idle:
        raise RuntimeError(f"no clips at all in {[a.name for a in acts]}")
    if idle is run:  # e.g. the wasp only flies — same clip serves both states
        run = idle.copy()
    idle.name = "Idle"
    run.name = "Run"
    for a in acts:
        if a not in (idle, run) and a.users == 0:
            bpy.data.actions.remove(a)
    if not arm.animation_data:
        arm.animation_data_create()
    arm.animation_data.action = None
    for tr in list(arm.animation_data.nla_tracks):
        arm.animation_data.nla_tracks.remove(tr)
    for act in (idle, run):
        act.use_fake_user = True
        track = arm.animation_data.nla_tracks.new()
        track.name = act.name
        track.mute = True
        track.strips.new(act.name, 1, act)


def build(pid, cfg):
    clean_scene()
    arm, objects, acts = import_model(cfg["src"])
    normalize_height(arm, objects, cfg["height"],
                     fix_actions=acts if cfg["kind"] == "fbx" else None)
    cap_textures()

    if cfg["kind"] == "gltf":
        poser = Poser(arm)
        head = add_head(arm, poser.fwd, cfg["gender"], cfg["hooded"], cfg["height"])
        if head:
            objects.append(head)
        author_action(arm, poser, "Idle", IDLE_FRAMES, idle_pose, 12)
        author_action(arm, poser, "Run", RUN_FRAMES, run_pose, 2)
        poser.reset()
    else:
        prepare_fbx_actions(arm, acts, cfg["idle"], cfg["run"])

    out = os.path.join(OUT_DIR, f"{pid}.glb")
    for o in bpy.data.objects:
        o.select_set(True)
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=True,
        export_animations=True,
        export_skins=True,
        export_yup=True,
        export_apply=False,  # never apply modifiers on skinned meshes
    )
    tris = sum(
        len(o.data.polygons) * 2 for o in bpy.data.objects if o.type == "MESH"
    )
    print(f"[{pid}] → {out}  (~{tris} tris, {os.path.getsize(out)//1024} KB)")


def main():
    print("Blender", ".".join(str(v) for v in bpy.app.version))
    os.makedirs(OUT_DIR, exist_ok=True)
    bpy.context.scene.render.fps = FPS
    only = [a for a in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else []
    for pid, cfg in PERSONAGES.items():
        if only and pid not in only:
            continue
        build(pid, cfg)
    print("done:", ", ".join(only or PERSONAGES))


main()
