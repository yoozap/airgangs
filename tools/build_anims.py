# Build the shared MOVE LIBRARY out of Quaternius' Universal Animation Library 2
# (CC0) -- a lean GLB of clips plus the numbers a retarget needs, and nothing
# else. This touches no personage file and no movement code: the library is a
# separate asset that src/anims.js lays over whatever rig a body already has.
#
# Why a library and not a per-body bake: UAL2's skeleton is the UE5 mannequin
# (root/pelvis/spine_01/clavicle_l/thigh_l...) and tools/build_characters.py
# already names our armatures the same way, with the SAME parent chain -- which
# makes a retarget a constant rotation per bone (see src/anims.js). So the clips
# are stored once, in the mannequin's own frame, and converted at load. Bodies
# this tool has never heard of (peasant, ranger, anything added later) come
# along for free.
#
# What gets stripped, and why it MUST be:
#   * meshes/skins/materials -- 19 of the 20 MB, and we only want motion.
#   * per-bone TRANSLATION and SCALE tracks. Every UAL2 clip keyframes all
#     three channels on all 65 bones, but the per-bone translations are
#     CONSTANT (measured: zero span) -- they are the mannequin's own bone
#     lengths. Play them on our bodies and every limb snaps to mannequin
#     proportions and the skin tears. Rotations are the animation; they are all
#     we keep.
#   * ...except `pelvis` translation, which really does move (the hip bob).
#     It is exported in HIP HEIGHTS so a body of any size can scale it.
#   * `root`, whose translation is the root motion. We drive position from
#     physics, so the track goes -- but the displacement is measured off the
#     _RM file and written to the sidecar, because that is the one number a
#     canned move needs to line up with the world (ClimbUp_2m = 2.00 m up,
#     1.68 m forward).
#
# Run: python3 tools/build_anims.py
#
# Source: ~/Downloads/Universal Animation Library 2[Source] (CC0, @Quaternius)

import array
import json
import math
import os
import struct

import numpy as np

SRC = os.path.expanduser("~/Downloads/Universal Animation Library 2[Source]/Unreal-Godot")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "public", "models", "anims")
OUT_GLB = os.path.join(OUT_DIR, "moves.glb")
OUT_JSON = os.path.join(OUT_DIR, "moves.json")

# The clips we want, by what we would use them FOR. `loop` marks a cycle (the
# rest are one-shots that end on their last frame); `tag` groups them for the
# game side. A_TPose is not a move -- it is the reference pose the retarget
# measures the mannequin's limb directions in, and it must stay.
CLIPS = [
    ("A_TPose", "ref", False),
    # --- the ledge: foot.js climbs with world-locked IK, so these are pose
    # material for a 1 m / 2 m lip, not a replacement for the IK.
    ("StepUp", "climb", False),
    ("ClimbUp_1m", "climb", False),
    ("ClimbUp_2m", "climb", False),
    ("GetOffWall_2m", "climb", False),
    # --- parkour
    ("SafetyVault", "parkour", False),
    ("Slide_Start", "parkour", False),
    ("Slide_Loop", "parkour", True),
    ("Slide_Exit", "parkour", False),
    ("WallRun_L_Loop", "parkour", True),
    ("WallRun_R_Loop", "parkour", True),
    ("WallRun_Jump_L", "parkour", False),
    ("WallRun_Jump_R", "parkour", False),
    ("DoubleJump", "parkour", False),
    ("KipUp", "parkour", False),
    ("JogToFlip", "parkour", False),
    ("NinjaJump_Start", "parkour", False),
    ("NinjaJump_Idle_Loop", "parkour", True),
    ("NinjaJump_Land", "parkour", False),
    # --- locomotion. The strafe set the auto-rigged bodies never had
    # (characters.js: "these bodies ship one run cycle and no strafe clip").
    ("Walk_Fwd_Loop", "walk", True),
    ("Walk_Bwd_Loop", "walk", True),
    ("Walk_L_Loop", "walk", True),
    ("Walk_R_Loop", "walk", True),
    ("Walk_Fwd_L_Loop", "walk", True),
    ("Walk_Fwd_R_Loop", "walk", True),
    ("Walk_Bwd_L_Loop", "walk", True),
    ("Walk_Bwd_R_Loop", "walk", True),
    ("Walk_Carry_Loop", "walk", True),
    ("Turn180_L", "walk", False),
    ("Turn180_R", "walk", False),
    # --- taking a hit, throwing, hitting back
    ("Hit_Knockback", "hit", False),
    ("LiftAir_Fall_Air_Loop", "hit", True),
    ("LiftAir_Fall_Impact", "hit", False),
    ("Melee_Hook", "melee", False),
    ("Melee_Knee", "melee", False),
    ("Melee_Uppercut", "melee", False),
    ("Melee_Combo", "melee", False),
    ("OverhandThrow", "melee", False),
    # --- standing around
    ("Idle_FoldArms_Loop", "idle", True),
    ("Idle_Rail_Loop", "idle", True),
    ("Idle_Rail_Call", "idle", False),
    ("Idle_TalkingPhone_Loop", "idle", True),
    ("Idle_No_Loop", "idle", True),
    ("Surprise", "idle", False),
    ("Yes", "idle", False),
    ("Consume", "idle", False),
    ("Bandage_Loop", "idle", True),
]

# The bone a retarget measures each bone's DIRECTION along: its primary child.
# Not "the first child in the file" -- pelvis has three, and the hand's first
# child is the index finger when the axis down the hand is the middle one.
# A bone missing from here (Head, the toes, the finger tips) has no direction
# to measure, and src/anims.js falls back to its bind basis for it.
PRIMARY = {
    "root": "pelvis", "pelvis": "spine_01", "spine_01": "spine_02",
    "spine_02": "spine_03", "spine_03": "neck_01", "neck_01": "Head",
    "clavicle_l": "upperarm_l", "upperarm_l": "lowerarm_l",
    "lowerarm_l": "hand_l", "hand_l": "middle_01_l",
    "clavicle_r": "upperarm_r", "upperarm_r": "lowerarm_r",
    "lowerarm_r": "hand_r", "hand_r": "middle_01_r",
    "thigh_l": "calf_l", "calf_l": "foot_l", "foot_l": "ball_l",
    "thigh_r": "calf_r", "calf_r": "foot_r", "foot_r": "ball_r",
}
for side in ("l", "r"):
    for f in ("index", "middle", "pinky", "ring", "thumb"):
        for i in (1, 2, 3):
            PRIMARY[f"{f}_0{i}_{side}"] = f"{f}_0{i+1}_{side}" if i < 3 else f"{f}_04_leaf_{side}"


# ---------------------------------------------------------------- glTF reading

def load_glb(path):
    with open(path, "rb") as f:
        magic, ver, _ = struct.unpack("<III", f.read(12))
        assert magic == 0x46546C67, path
        clen, _ = struct.unpack("<II", f.read(8))
        js = json.loads(f.read(clen))
        blen, _ = struct.unpack("<II", f.read(8))
        return js, f.read(blen)


NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_acc(js, bin_, i):
    a = js["accessors"][i]
    assert a["componentType"] == 5126, "only float accessors here"
    bv = js["bufferViews"][a["bufferView"]]
    n = NCOMP[a["type"]]
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    arr = array.array("f")
    arr.frombytes(bin_[off:off + a["count"] * n * 4])
    return np.array(arr, dtype=np.float32).reshape(a["count"], n)


# -------------------------------------------------------------------- FK maths

def quat_mat(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ], dtype=np.float64)


def mat_quat(m):
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        return [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s,
                (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    i = int(np.argmax([m[0, 0], m[1, 1], m[2, 2]]))
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1.0 + m[i, i] - m[j, j] - m[k, k]) * 2
    q = [0.0, 0.0, 0.0, (m[k, j] - m[j, k]) / s]
    q[i] = 0.25 * s
    q[j] = (m[j, i] + m[i, j]) / s
    q[k] = (m[k, i] + m[i, k]) / s
    return q


def main():
    js, bin_ = load_glb(os.path.join(SRC, "UAL2.glb"))
    rm_js, rm_bin = load_glb(os.path.join(SRC, "UAL2_RM.glb"))
    nodes = js["nodes"]
    by_name = {n.get("name"): i for i, n in enumerate(nodes)}
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent[c] = i
    joints = js["skins"][0]["joints"]
    bone_names = [nodes[i].get("name") for i in joints]

    anims = {a.get("name"): a for a in js["animations"]}
    rm_anims = {a.get("name"): a for a in rm_js["animations"]}
    missing = [c for c, _, _ in CLIPS if c not in anims]
    assert not missing, f"not in the pack: {missing}"

    # ---- the reference pose: the mannequin at A_TPose, in model space. Node
    # rest transforms would do for a rig exported at rest, but the T-pose is
    # what a retarget wants to compare limb directions in, and it is right
    # here as a clip.
    tpose = {}
    ref = anims["A_TPose"]
    for ch in ref["channels"]:
        n = nodes[ch["target"]["node"]].get("name")
        if ch["target"]["path"] == "rotation":
            tpose[n] = read_acc(js, bin_, ref["samplers"][ch["sampler"]]["output"])[0]

    world_rot, world_pos = {}, {}

    def fk(idx):
        n = nodes[idx]
        nm = n.get("name")
        q = tpose.get(nm, n.get("rotation", [0, 0, 0, 1]))
        R = quat_mat(q)
        t = np.array(n.get("translation", [0, 0, 0]), dtype=np.float64)
        p = parent.get(idx)
        if p is not None and nodes[p].get("name") in world_rot:
            pn = nodes[p].get("name")
            world_rot[nm] = world_rot[pn] @ R
            world_pos[nm] = world_pos[pn] + world_rot[pn] @ t
        else:
            world_rot[nm] = R
            world_pos[nm] = t
        for c in n.get("children", []):
            if c in joints:
                fk(c)

    fk(by_name["root"])

    # Each bone's own axis: where its primary child sits, in the bone's LOCAL
    # frame, as a unit vector. This is the one number that lets src/anims.js
    # tell a rig whose arm bones run down +X from one whose run down -X, and
    # it is measured in the reference pose, not in a bind that may be a T-pose
    # on one body and arms-down on the next.
    axis = {}
    for nm in bone_names:
        kid = PRIMARY.get(nm)
        if not kid or kid not in by_name:
            continue
        v = np.array(nodes[by_name[kid]].get("translation", [0, 0, 0]), dtype=np.float64)
        L = np.linalg.norm(v)
        if L > 1e-7:
            axis[nm] = list(np.round(v / L, 6))

    hip = float(world_pos["pelvis"][1])

    # ---- clips: rotations only, plus the hip's own translation in hip heights
    out_nodes, out_anims = [], []
    acc_out, bv_out, blob = [], [], bytearray()

    def put(data, kind, count, mn=None, mx=None):
        """One accessor + bufferView over freshly appended float data."""
        while len(blob) % 4:
            blob.append(0)
        off = len(blob)
        blob.extend(np.asarray(data, dtype=np.float32).tobytes())
        bv_out.append({"buffer": 0, "byteOffset": off, "byteLength": len(blob) - off})
        a = {"bufferView": len(bv_out) - 1, "componentType": 5126,
             "count": count, "type": kind}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        acc_out.append(a)
        return len(acc_out) - 1

    # the skeleton, rebuilt without the mesh: bone nodes keep their rest
    # transforms so a reader can still see the source hierarchy.
    idx_of = {}
    for i in joints:
        idx_of[i] = len(out_nodes)
        n = nodes[i]
        out_nodes.append({"name": n.get("name"),
                          **{k: n[k] for k in ("translation", "rotation") if k in n}})
    for i in joints:
        kids = [idx_of[c] for c in nodes[i].get("children", []) if c in idx_of]
        if kids:
            out_nodes[idx_of[i]]["children"] = kids
    out_nodes.append({"name": "AnimLib", "children": [idx_of[by_name["root"]]]})

    meta = {"source": "Quaternius Universal Animation Library 2 (CC0)",
            "note": "built by tools/build_anims.py -- rotations only; see src/anims.js",
            "hipHeight": round(hip, 5),
            "forward": [0, 0, 1],
            "bones": bone_names,
            "refPose": {nm: [round(float(x), 6) for x in mat_quat(world_rot[nm])]
                        for nm in bone_names},
            # world positions in the same pose: src/anims.js reads the HIP AXIS
            # (left thigh -> right thigh) off these to work out how the
            # mannequin's model space is turned relative to a body's own, which
            # is the one thing a per-bone rotation cannot tell it. Get it wrong
            # and the body walks backwards inside its own mesh.
            "refPos": {nm: [round(float(x), 6) for x in world_pos[nm]]
                       for nm in bone_names},
            "boneAxis": axis,
            # the source hierarchy, so the retarget can walk up to the nearest
            # bone a body actually has (the 22-joint rigs have no `root`), and
            # the child each bone's axis was measured along
            "parent": {nm: nodes[parent[by_name[nm]]].get("name")
                       for nm in bone_names if by_name[nm] in parent
                       and nodes[parent[by_name[nm]]].get("name") in bone_names},
            "primary": {nm: PRIMARY[nm] for nm in bone_names
                        if nm in PRIMARY and PRIMARY[nm] in bone_names},
            # the hip's resting place in this same pose, in hip heights: the
            # pelvis track is only useful as a DELTA from it (see src/anims.js)
            "pelvisRef": [round(float(x) / hip, 6)
                          for x in nodes[by_name["pelvis"]].get("translation", [0, 0, 0])],
            "clips": {}}

    for name, tag, loop in CLIPS:
        a = anims[name]
        samplers, channels = [], []
        keys = {}          # input accessor in source -> index in ours
        for ch in a["channels"]:
            path = ch["target"]["path"]
            nm = nodes[ch["target"]["node"]].get("name")
            if nm not in idx_of and nm not in by_name:
                continue
            if path == "scale":
                continue
            if path == "translation" and nm != "pelvis":
                continue      # constant bone lengths, and root motion goes to the sidecar
            s = a["samplers"][ch["sampler"]]
            vals = read_acc(js, bin_, s["output"]).astype(np.float64)
            if path == "translation":
                # the hip bob, in hip heights -- src/anims.js multiplies by the
                # body's own hip height
                vals = vals / hip
            elif np.ptp(vals, axis=0).max() < 1e-6:
                # a bone that never moves in this clip: one key is enough
                vals = vals[:1]
            t = read_acc(js, bin_, s["input"]).astype(np.float64)
            if len(vals) == 1:
                t = t[:1]
            tk = (s["input"], len(vals))
            if tk not in keys:
                keys[tk] = put(t.ravel(), "SCALAR", len(t),
                               [float(t.min())], [float(t.max())])
            o = put(vals.ravel(), "VEC4" if path == "rotation" else "VEC3", len(vals))
            samplers.append({"input": keys[tk], "output": o, "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1,
                             "target": {"node": idx_of[by_name[nm]], "path": path}})
        out_anims.append({"name": name, "samplers": samplers, "channels": channels})

        # how far the move actually travels, off the root-motion build
        dur = float(max(read_acc(js, bin_, s2["input"])[:, 0].max() for s2 in a["samplers"]))
        move = [0.0, 0.0, 0.0]
        rise = 0.0
        ra = rm_anims.get(name)
        if ra:
            for ch in ra["channels"]:
                if (rm_js["nodes"][ch["target"]["node"]].get("name") == "root"
                        and ch["target"]["path"] == "translation"):
                    v = read_acc(rm_js, rm_bin, ra["samplers"][ch["sampler"]]["output"])
                    move = [round(float(x), 4) for x in (v[-1] - v[0])]
                    rise = round(float(v[:, 1].max() - v[0][1]), 4)
        meta["clips"][name] = {"tag": tag, "loop": loop, "dur": round(dur, 4),
                               "move": move, "rise": rise}

    gltf = {
        "asset": {"version": "2.0", "generator": "tbilisifly tools/build_anims.py"},
        "scene": 0,
        "scenes": [{"nodes": [len(out_nodes) - 1]}],
        "nodes": out_nodes,
        "animations": out_anims,
        "accessors": acc_out,
        "bufferViews": bv_out,
        "buffers": [{"byteLength": len(blob)}],
    }

    os.makedirs(OUT_DIR, exist_ok=True)
    jb = json.dumps(gltf, separators=(",", ":")).encode()
    jb += b" " * (-len(jb) % 4)
    blob.extend(b"\0" * (-len(blob) % 4))
    with open(OUT_GLB, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(jb) + 8 + len(blob)))
        f.write(struct.pack("<II", len(jb), 0x4E4F534A) + jb)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + bytes(blob))
    with open(OUT_JSON, "w") as f:
        json.dump(meta, f, indent=1)

    print(f"{OUT_GLB}  {len(CLIPS)} clips, {os.path.getsize(OUT_GLB)/1e6:.2f} MB")
    print(f"{OUT_JSON} hip {hip:.3f} m, {len(bone_names)} bones, "
          f"{len(axis)} with a measured axis")
    for n, c in meta["clips"].items():
        if any(abs(x) > 0.05 for x in c["move"]) or c["rise"] > 0.05:
            print(f"   {n:24} {c['dur']:5.2f}s  travel {c['move']}  rise {c['rise']}")


if __name__ == "__main__":
    main()
