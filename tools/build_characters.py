# Build playable personage GLBs from downloaded character models — WITH real
# human Idle/Run cycles, whatever shape the source is in:
#
#   kind "auto": static mesh → generate a UE-named humanoid armature fitted to
#     the mesh (T-pose and arms-down both detected), bind with automatic
#     weights, author procedural Idle/Run.
#   kind "map":  already rigged → rename its bones to the canonical names via
#     `bonemap`, drop old actions/IK, author the same Idle/Run on its rig.
#   kind "clips": already rigged AND animated → keep its own clips, renamed
#     canonically (like prepare_fbx_actions in build_personages.py).
#
# The Poser here is build_personages.py's, generalized: bones are looked up
# through a name map and silently skipped when missing, and the arm-hang
# rotation is measured from the REST pose (a T-pose needs ~58° of hang added,
# an arms-down model needs none — hardcoding 58° made those arms point
# backwards).
#
# Run: Blender -b --python tools/build_characters.py -- all|ids|verify

import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

SRC = os.path.expanduser("~/Desktop/game/characters")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "public", "models", "personages")
VERIFY = os.path.join(ROOT, "tools", "cache", "chars_run_sheet.png")

MAX_TEX = 1024
MAX_TRIS = 60_000
HEIGHT = 1.8          # game shows every body at HERO_HEIGHT anyway
FPS = 24
RUN_FRAMES = 16
IDLE_FRAMES = 48
D = math.radians

MIXAMO = {  # canonical → bone-name PREFIX in the source rig
    "pelvis": "Hips", "spine_01": "Spine_", "spine_02": "Spine1", "spine_03": "Spine2",
    "neck_01": "Neck", "Head": "Head_", "clavicle_l": "LeftShoulder",
    "upperarm_l": "LeftArm", "lowerarm_l": "LeftForeArm", "hand_l": "LeftHand_",
    "clavicle_r": "RightShoulder", "upperarm_r": "RightArm",
    "lowerarm_r": "RightForeArm", "hand_r": "RightHand_",
    "thigh_l": "LeftUpLeg", "calf_l": "LeftLeg", "foot_l": "LeftFoot",
    "ball_l": "LeftToeBase", "thigh_r": "RightUpLeg", "calf_r": "RightLeg",
    "foot_r": "RightFoot", "ball_r": "RightToeBase",
}
OLDMAN = {
    "pelvis": "ventre", "spine_01": "buste", "neck_01": "coup", "Head": "tete",
    "clavicle_l": "epaule.G", "upperarm_l": "bras1.G", "lowerarm_l": "bras2.G",
    "hand_l": "mail1.G", "clavicle_r": "epaule.D", "upperarm_r": "bras1.D",
    "lowerarm_r": "bras2.D", "hand_r": "main1.D",
    "thigh_l": "jambe1.G", "calf_l": "jambe2.G", "foot_l": "pied.G",
    "thigh_r": "jambe1.D", "calf_r": "jambe2.D", "foot_r": "pied.D",
}
ELEPHANT = {
    "spine_01": "Spine1", "spine_02": "Spine2", "Head": "Head",
    "clavicle_l": "Shoulder.L", "upperarm_l": "UooerArm.L", "lowerarm_l": "LowerArm.L",
    "clavicle_r": "Shoulder.R", "upperarm_r": "UooerArm.R", "lowerarm_r": "LowerArm.R",
    "thigh_l": "UpperLeg.L", "calf_l": "LowerLeg.L", "foot_l": "Foot.L",
    "thigh_r": "UpperLeg.R", "calf_r": "LowerLeg.R", "foot_r": "Foot.R",
}

# yaw (deg about Z, pre-rig) turns the character to face Blender -Y (glTF +Z).
CHARACTERS = {
    "stitch":    {"src": "stitch.glb", "kind": "auto"},
    "cyberfem":  {"src": "cyberpunk_female_full-body_character.glb", "kind": "map",
                  "bonemap": MIXAMO},
    "anime":     {"src": "anime_girl_character.glb", "kind": "auto", "yaw": 90},
    "elephant":  {"src": "animated_elephant_character.glb", "kind": "clips",
                  "keep": ["Idle"]},
    "ghost":     {"src": "horror_ghost_character_-_blood_stained_spirit.glb", "kind": "auto"},
    "tommy":     {"src": "excalibur_tommy_-_dae_character_creation.glb", "kind": "auto"},
    "angrybird": {"src": "angrybird_-_c4d_modeled_character.glb", "kind": "auto"},
    "mario":     {"src": "human_character_supermario.glb", "kind": "auto"},
    "donny":     {"src": "donny.glb", "kind": "auto"},
    "squid":     {"src": "squid_game_low_poly_character_unlit.glb", "kind": "auto"},
    "batman":    {"src": "batman_character.glb", "kind": "auto"},
    "carl":      {"src": "carl_fredricksen_character__3d_printable.glb", "kind": "auto"},
    # The Polygonal Mind bodies shipped as single rigid meshes -- no armature,
    # no clips -- so in game they slid along with a procedural hop and nothing
    # on them ever moved, because there was nothing on them TO move. Same
    # "auto" treatment as the rest: fit a humanoid armature to the mesh, bind
    # with automatic weights, author the shared Idle/Run.
    # yaw -90 is NOT optional here: the whole Polygonal Mind pack faces +X, and
    # build_armature measures the arm span along X on a mesh it assumes already
    # faces -Y. Without the turn it read the banana's arm reach as 0.85 (its
    # body half-depth) instead of 2.65, concluded "not a T-pose", and built
    # arms-down bones that sit nowhere near the real stick arms -- which is
    # exactly what "the bones are not inside the arms" looks like.
    "banana":       {"src": "Cool Bannana Guy by Polygonal Mind - TFlEjNafR1.glb", "kind": "auto", "yaw": -90, "rigid": True},
    "eggplant":     {"src": "Eggplant Character by Polygonal Mind - B2V0LKzpd2.glb", "kind": "auto", "yaw": -90, "rigid": True},
    "frog_man":     {"src": "Frog Man by Polygonal Mind - YWkleQeQka.glb", "kind": "auto", "yaw": -90, "rigid": True},
    "skeleton_boy": {"src": "Skeleton Boy by Polygonal Mind - Q0ZWVssZCg.glb", "kind": "auto", "yaw": -90, "rigid": True},
    "toad":         {"src": "Toad Ripoff by Polygonal Mind - Emg7ESavAf.glb", "kind": "auto", "yaw": -90, "rigid": True},
}


# --------------------------------------------------------------- helpers ----
def clean_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mesh_objects():
    return [o for o in bpy.data.objects if o.type == "MESH"]


def world_box():
    lo = Vector((1e18,) * 3)
    hi = Vector((-1e18,) * 3)
    for o in mesh_objects():
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
    return lo, hi


def normalize(cfg):
    """Face -Y, stand HEIGHT meters tall, feet on z=0, centered on origin."""
    root = bpy.data.objects.new("char_root", None)
    bpy.context.scene.collection.objects.link(root)
    for o in bpy.data.objects:
        if o.parent is None and o is not root:
            o.parent = root
    root.rotation_euler = (0, 0, D(cfg.get("yaw", 0)))
    bpy.context.view_layer.update()
    lo, hi = world_box()
    s = HEIGHT / (hi.z - lo.z)
    root.scale = (s,) * 3
    bpy.context.view_layer.update()
    lo, hi = world_box()
    mid = (lo + hi) / 2
    root.location = (-mid.x, -mid.y, -lo.z)
    bpy.context.view_layer.update()
    for o in bpy.data.objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.data.objects.remove(root, do_unlink=True)


def decimate_and_pack(pid):
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in mesh_objects())
    if tris > MAX_TRIS:
        ratio = MAX_TRIS / tris
        for o in mesh_objects():
            if len(o.data.polygons) < 500:
                continue
            mod = o.modifiers.new("dec", "DECIMATE")
            mod.ratio = ratio
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.modifier_apply(modifier="dec")
    for img in bpy.data.images:
        w, h = img.size
        if max(w, h) > MAX_TEX:
            f = MAX_TEX / max(w, h)
            img.scale(int(w * f), int(h * f))
    # depth-8/16 images crash the WEBP writer (see build_planes.py)
    remap = {}
    for img in list(bpy.data.images):
        if img.has_data and img.depth in (8, 16):
            clean = bpy.data.images.new(img.name + "_rgba", img.size[0], img.size[1], alpha=True)
            clean.pixels = img.pixels[:]
            remap[img] = clean
    for mat in bpy.data.materials:
        if mat.use_nodes:
            for node in mat.node_tree.nodes:
                if node.type == "TEX_IMAGE" and node.image in remap:
                    node.image = remap[node.image]
    return tris


def join_meshes():
    meshes = mesh_objects()
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    body.name = "Body"
    # orphan empties left over from the join tree
    for o in [o for o in bpy.data.objects if o.type == "EMPTY"]:
        bpy.data.objects.remove(o, do_unlink=True)
    body.parent = None
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    # weld touching pieces so shells become one island and deform smoothly
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=0.003)
    bpy.ops.object.mode_set(mode="OBJECT")
    return body


# ------------------------------------------------------------ auto rig ------
def sample_verts(body, step=7):
    mw = body.matrix_world
    verts = body.data.vertices
    return [mw @ verts[i].co for i in range(0, len(verts), step)]


def build_armature(body, rigid=False):
    """UE-named humanoid skeleton fitted to the (normalized, -Y-facing) mesh."""
    vs = sample_verts(body)
    h = max(v.z for v in vs)

    def band(z0, z1):
        return [v for v in vs if z0 * h <= v.z <= z1 * h]

    # cartoon proportions put shoulders anywhere (a minion's are at half its
    # height) — find the widest height-band instead of assuming 0.75h
    prof = {}
    for k in range(35, 91, 5):
        zb = band(k / 100 - 0.03, k / 100 + 0.03)
        prof[k] = max((abs(v.x) for v in zb), default=0.0)
    best_k = max(prof, key=lambda k: prof[k])
    arm_r = prof[best_k]
    tpose = arm_r > 0.38 * h
    arm_z = best_k / 100 if tpose else 0.75
    legs = band(0.15, 0.35)
    xs = sorted(abs(v.x) for v in legs)
    hip_x = min(max(xs[len(xs) // 2] if xs else 0.09 * h, 0.05 * h), 0.16 * h)

    # the toy packs: pivot each arm where it meets the shell, and cover the
    # seam with a ball (see arm_roots). Done BEFORE the armature exists so the
    # balls are part of the body when it is skinned.
    roots = arm_roots(body, h, arm_z, arm_r) if (rigid and tpose) else {}
    for side, (c, r, vi) in roots.items():
        add_shoulder_ball(body, c, r * 1.35, vi)
        print(f"  shoulder_{side}: pivot ({c.x:.3f}, {c.y:.3f}, {c.z:.3f}) tube r={r*100:.1f}cm")

    arm = bpy.data.armatures.new("Armature")
    ob = bpy.data.objects.new("Armature", arm)
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")

    def eb(name, head, tail, parent=None, connect=False):
        b = arm.edit_bones.new(name)
        b.head, b.tail = Vector(head), Vector(tail)
        if parent:
            b.parent = arm.edit_bones[parent]
            b.use_connect = connect
        return b

    z = lambda f: f * h
    eb("pelvis", (0, 0, z(0.50)), (0, 0, z(0.57)))
    eb("spine_01", (0, 0, z(0.57)), (0, 0, z(0.64)), "pelvis", True)
    eb("spine_02", (0, 0, z(0.64)), (0, 0, z(0.71)), "spine_01", True)
    eb("spine_03", (0, 0, z(0.71)), (0, 0, z(0.77)), "spine_02", True)
    eb("neck_01", (0, 0, z(0.77)), (0, 0, z(0.83)), "spine_03", True)
    eb("Head", (0, 0, z(0.83)), (0, 0, z(0.97)), "neck_01", True)
    az = arm_z
    for sgn, side in ((1, "l"), (-1, "r")):
        # the measured socket when there is one, else the fitted guess
        root = tuple(roots[side][0]) if side in roots else (sgn * z(0.10), 0, z(az))
        eb(f"clavicle_{side}", (sgn * z(0.03), 0, z(az)), root, "spine_03")
        if tpose:
            eb(f"upperarm_{side}", root, (sgn * arm_r * 0.55, 0, z(az)),
               f"clavicle_{side}")
            eb(f"lowerarm_{side}", (sgn * arm_r * 0.55, 0, z(az)), (sgn * arm_r * 0.82, 0, z(az)),
               f"upperarm_{side}", True)
            eb(f"hand_{side}", (sgn * arm_r * 0.82, 0, z(az)), (sgn * arm_r, 0, z(az)),
               f"lowerarm_{side}", True)
        else:
            eb(f"upperarm_{side}", (sgn * z(0.11), 0, z(az - 0.01)), (sgn * z(0.15), 0, z(az - 0.19)),
               f"clavicle_{side}")
            eb(f"lowerarm_{side}", (sgn * z(0.15), 0, z(az - 0.19)), (sgn * z(0.17), 0, z(az - 0.35)),
               f"upperarm_{side}", True)
            eb(f"hand_{side}", (sgn * z(0.17), 0, z(az - 0.35)), (sgn * z(0.18), 0, z(az - 0.43)),
               f"lowerarm_{side}", True)
        eb(f"thigh_{side}", (sgn * hip_x, 0, z(0.48)), (sgn * hip_x, 0, z(0.26)), "pelvis")
        eb(f"calf_{side}", (sgn * hip_x, 0, z(0.26)), (sgn * hip_x, 0, z(0.06)),
           f"thigh_{side}", True)
        eb(f"foot_{side}", (sgn * hip_x, 0, z(0.06)), (sgn * hip_x, -z(0.07), z(0.02)),
           f"calf_{side}", True)
        eb(f"ball_{side}", (sgn * hip_x, -z(0.07), z(0.02)), (sgn * hip_x, -z(0.12), z(0.02)),
           f"foot_{side}", True)
    bpy.ops.object.mode_set(mode="OBJECT")

    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    # bone-heat is a lottery on these models (fails silently to zero weights,
    # or tears loose pieces off) — always use the deterministic island-aware
    # nearest-bone weighting instead
    bpy.ops.object.parent_set(type="ARMATURE_NAME")
    manual_weights(body, ob, rigid=rigid)
    return ob, ("whole-island rigid weights" if rigid else "island-rigid nearest-bone weights")


def islands(me):
    """Connected-component id per vertex (over edges)."""
    from collections import deque
    n = len(me.vertices)
    adj = [[] for _ in range(n)]
    for e in me.edges:
        a, b = e.vertices
        adj[a].append(b)
        adj[b].append(a)
    comp = [-1] * n
    nc = 0
    for i in range(n):
        if comp[i] >= 0:
            continue
        dq = deque([i])
        comp[i] = nc
        while dq:
            u = dq.popleft()
            for v2 in adj[u]:
                if comp[v2] < 0:
                    comp[v2] = nc
                    dq.append(v2)
        nc += 1
    return comp


def arm_roots(body, h, az, arm_r):
    """WHERE EACH ARM ACTUALLY MEETS THE BODY, measured off the mesh.

    A rigid piece rotates about its bone's HEAD, so on the toy packs the
    shoulder pivot has to sit exactly where the stick arm enters the shell.
    The 0.10h guess put the banana's pivot 6 cm inside its skin and 5 cm ahead
    of the socket, and every swing of the arm then carried the root 5-14 cm
    clear of the body (tools/_probe_gap.mjs) -- the "gap between the hands and
    the body". Per side: the centroid of the innermost vertices of the arm
    island in the shoulder band, the tube's radius a little further out, and
    one ring vertex (for its material and UV). Empty when there is no separate
    arm island to measure (a continuous body keeps the fitted guess)."""
    me = body.data
    comp = islands(me)
    from collections import Counter
    biggest = Counter(comp).most_common(1)[0][0]
    mw = body.matrix_world
    co = [mw @ v.co for v in me.vertices]
    zlo, zhi = (az - 0.08) * h, (az + 0.08) * h
    reach = {}
    for i, p in enumerate(co):
        if comp[i] != biggest:
            reach[comp[i]] = max(reach.get(comp[i], 0.0), abs(p.x))
    out = {}
    for sgn, side in ((1, "l"), (-1, "r")):
        # only islands that reach out along the arm are arms: a hat brim or a
        # pair of glasses in the same band stays out of it
        cand = [i for i, p in enumerate(co)
                if comp[i] != biggest and reach[comp[i]] > 0.5 * arm_r
                and sgn * p.x > 0 and zlo <= p.z <= zhi]
        if len(cand) < 3:
            continue
        xmin = min(abs(co[i].x) for i in cand)
        ring = [i for i in cand if abs(co[i].x) <= xmin + 0.015]
        c = sum((co[i] for i in ring), Vector()) / len(ring)
        # the tube's radius, read a hand further out where it is a tube and not
        # the rounded end of one
        band = [co[i] for i in cand if xmin + 0.03 <= abs(co[i].x) <= xmin + 0.09]
        r = 0.0
        if len(band) >= 4:
            bc = sum(band, Vector()) / len(band)
            r = sum(Vector((0, q.y - bc.y, q.z - bc.z)).length for q in band) / len(band)
        r = min(0.06, max(0.015, r or 0.025))
        out[side] = (c, r, ring[0])
    return out


def add_shoulder_ball(body, center, radius, vert_index):
    """A ball at the pivot, in the arm's own material, so the seam between a
    rigid arm and the shell is covered at every angle the arm takes -- the
    ball-and-socket every toy figure has. Joined into the body so it is
    skinned with the rest (it lands on the clavicle: whole-piece rigid)."""
    me = body.data
    mat_index, uv = 0, None
    uvl = me.uv_layers.active
    for poly in me.polygons:
        if vert_index in poly.vertices:
            mat_index = poly.material_index
            if uvl:
                for li in poly.loop_indices:
                    if me.loops[li].vertex_index == vert_index:
                        uv = uvl.data[li].uv.copy()
                        break
            break
    bpy.ops.object.select_all(action="DESELECT")
    bpy.ops.mesh.primitive_uv_sphere_add(segments=20, ring_count=14, radius=radius, location=center)
    ball = bpy.context.active_object
    bpy.ops.object.shade_smooth()
    if me.materials and mat_index < len(me.materials):
        ball.data.materials.append(me.materials[mat_index])
    if uv is not None and ball.data.uv_layers:
        bl = ball.data.uv_layers.active
        if uvl:
            bl.name = uvl.name
        for d in bl.data:
            d.uv = uv
    bpy.ops.object.select_all(action="DESELECT")
    ball.select_set(True)
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.join()
    return len(body.data.vertices)


def manual_weights(body, arm_ob, rigid=False):
    """Deterministic skinning. Toy-style models are many LOOSE pieces (hat,
    gloves, mustache…) — per-vertex weights tear those apart at pose time, so
    every small connected island is bound RIGIDLY to one bone via its
    centroid; only the big continuous islands get per-vertex two-bone
    blending."""
    from collections import Counter, deque

    for g in list(body.vertex_groups):
        body.vertex_groups.remove(g)
    segs = []
    for b in arm_ob.data.bones:
        if not b.use_deform:
            continue
        mw = arm_ob.matrix_world
        segs.append((b.name, mw @ b.head_local, mw @ b.tail_local))
    groups = {name: body.vertex_groups.new(name=name) for name, _, _ in segs}

    def seg_dist(p, a, b):
        ab = b - a
        t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-9)))
        return (p - (a + ab * t)).length

    def nearest_two(p):
        near = sorted(((seg_dist(p, h, t), n) for n, h, t in segs))[:2]
        (d1, n1), (d2, n2) = near[0], near[1]
        if d2 > d1 * 1.8:
            return [(n1, 1.0)]
        w1, w2 = 1.0 / (d1 + 1e-4), 1.0 / (d2 + 1e-4)
        s = w1 + w2
        return [(n1, w1 / s), (n2, w2 / s)]

    me = body.data
    n = len(me.vertices)
    comp = islands(me)
    sizes = Counter(comp)

    mw = body.matrix_world

    # RIGID: every island is bound whole to ONE bone. The toy packs are not
    # continuous bodies -- the banana is 128 separate pieces, a shell plus two
    # stick arms plus two feet plus a hat -- and per-vertex blending across
    # them puts body vertices on the LEG bones (396 on thigh_l, 308 on calf_l
    # for a banana whose legs are tiny), so the whole fruit stretched and
    # sheared every stride. Bound rigidly, the body keeps its shape exactly and
    # only the limb pieces travel, which is how the model was built.
    if rigid:
        island_v = {}
        for v in me.vertices:
            island_v.setdefault(comp[v.index], []).append(v.index)
        # the biggest island is the body: pin it to the torso so it can never
        # follow an arm or a leg, whatever happens to be nearest its centroid
        torso = next((nm for nm, _, _ in segs if nm == "spine_02"), segs[0][0])
        biggest = max(island_v, key=lambda c: len(island_v[c]))
        # how deep each bone sits in the hierarchy, so we can prefer the one
        # nearer the root when several reach the same piece
        depth = {}
        for b in arm_ob.data.bones:
            d, q = 0, b
            while q.parent:
                d += 1
                q = q.parent
            depth[b.name] = d
        for ci, idxs in island_v.items():
            pts = [mw @ me.vertices[i].co for i in idxs]
            centroid = sum(pts, Vector()) / len(pts)
            if ci == biggest:
                name = torso
                groups[name].add(idxs, 1.0, "REPLACE")
                continue
            rad = max((p - centroid).length for p in pts)
            # Every bone that actually reaches this piece is a candidate; of
            # those take the one CLOSEST TO THE ROOT. A rigid piece rotates
            # about its bone's head, so binding an arm to lowerarm_l -- whose
            # head sits out in mid-air beyond the shoulder -- swings the whole
            # piece away from the body and tears a gap open at the joint.
            # Pivoting from upperarm_l instead puts the centre of rotation at
            # the shoulder, inside the shell, and the piece stays attached.
            dists = sorted((seg_dist(centroid, h, t), nm) for nm, h, t in segs)
            best = dists[0][0]
            # only bones that genuinely REACH the piece are candidates -- a
            # generous radius let the spine qualify for an arm, which bound the
            # arms to the torso and stopped them moving altogether
            near = [nm for d, nm in dists if d <= best + 0.6 * rad]
            name = min(near, key=lambda nm: (depth[nm], dists[[n for _, n in dists].index(nm)][0]))

            # The piece is attached at `name`'s head -- shoulder or hip -- and
            # that attachment must stay RIGID or the seam reopens. Past it,
            # though, the piece should follow the chain, or the elbow and knee
            # in the clip have no geometry to move and the arm swings as one
            # stick. So: lock the inner stretch to the pivot bone, then blend
            # along the chain beyond it.
            chain = [name]
            bb = arm_ob.data.bones[name]
            while bb.children:
                bb = bb.children[0]
                chain.append(bb.name)
            cs = [(nm, h, t) for nm in chain for n2, h, t in segs if n2 == nm]
            if len(cs) < 2:
                groups[name].add(idxs, 1.0, "REPLACE")
                continue
            head0 = cs[0][1]
            chain_len = sum((t - h).length for _, h, t in cs) or 1.0
            lock = 0.34 * chain_len          # inner third stays on the pivot
            for vi in idxs:
                pv = mw @ me.vertices[vi].co
                if (pv - head0).length <= lock:
                    groups[name].add([vi], 1.0, "REPLACE")
                    continue
                ds = sorted((seg_dist(pv, h, t), nm) for nm, h, t in cs)
                (d1, n1), (d2, n2) = ds[0], ds[1]
                if d2 > d1 * 1.8:
                    groups[n1].add([vi], 1.0, "REPLACE")
                else:
                    w1, w2 = 1.0 / (d1 + 1e-4), 1.0 / (d2 + 1e-4)
                    tot = w1 + w2
                    groups[n1].add([vi], w1 / tot, "REPLACE")
                    groups[n2].add([vi], w2 / tot, "REPLACE")
        mod = next((m for m in body.modifiers if m.type == "ARMATURE"), None)
        if mod is None:
            mod = body.modifiers.new("Armature", "ARMATURE")
        mod.object = arm_ob
        return

    # big islands (torso+limb shells) deform per-vertex; small decorations
    # (gloves, straps, ears, buttons) inherit the weights of the nearest
    # big-island vertex so they stay glued to the surface they decorate
    big = {ci for ci, cn in sizes.items() if cn >= max(n * 0.10, 200)}
    if not big:
        big = {sizes.most_common(1)[0][0]}
    import mathutils
    kd = mathutils.kdtree.KDTree(sum(sizes[ci] for ci in big))
    big_pairs = {}
    for v in me.vertices:
        if comp[v.index] in big:
            p = mw @ v.co
            big_pairs[v.index] = nearest_two(p)
            kd.insert(p, v.index)
    kd.balance()
    small_cache = {}
    for v in me.vertices:
        ci = comp[v.index]
        if ci in big:
            pairs = big_pairs[v.index]
        else:
            if ci not in small_cache:
                pts = [mw @ me.vertices[i].co for i in range(n) if comp[i] == ci]
                centroid = sum(pts, Vector()) / len(pts)
                _, host, _ = kd.find(centroid)
                small_cache[ci] = big_pairs[host]
            pairs = small_cache[ci]
        for name, w in pairs:
            groups[name].add([v.index], w, "REPLACE")
    mod = next((m for m in body.modifiers if m.type == "ARMATURE"), None)
    if mod is None:
        mod = body.modifiers.new("Armature", "ARMATURE")
    mod.object = arm_ob


def adopt_rig(cfg):
    """Rename an existing rig's bones to canonical names, drop IK + actions."""
    arm_ob = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    inverse = {}
    for canon, prefix in cfg["bonemap"].items():
        for b in arm_ob.data.bones:
            if b.name == prefix or b.name.startswith(prefix):
                inverse[b.name] = canon
                break
    for old, canon in inverse.items():
        arm_ob.data.bones[old].name = canon  # vertex groups auto-follow
    for pb in arm_ob.pose.bones:
        for c in list(pb.constraints):
            pb.constraints.remove(c)
    for act in list(bpy.data.actions):
        bpy.data.actions.remove(act)
    return arm_ob


# ------------------------------- Poser (generalized from build_personages) --
class Poser:
    def __init__(self, arm):
        self.arm = arm
        self.pose = arm.pose
        for pb in self.pose.bones:
            pb.rotation_mode = "QUATERNION"
        self.fwd = Vector((0, -1, 0))  # every model is normalized to face -Y
        if "foot_l" in self.pose.bones and "ball_l" in self.pose.bones:
            foot = self.pose.bones["foot_l"].bone.matrix_local.translation
            ball = self.pose.bones["ball_l"].bone.matrix_local.translation
            f = Vector((ball.x - foot.x, ball.y - foot.y, 0.0))
            if f.length > 1e-4:
                self.fwd = f.normalized()
        self.up = Vector((0, 0, 1))
        self.swing = self.fwd.cross(self.up).normalized() * -1.0
        # arms: how far below horizontal the rest pose already hangs decides
        # how much side-rotation a run/idle needs to ADD (T-pose ~0°, arms
        # at the sides ~75-85° — adding the full hang there breaks the arm)
        self.down, self.hang = {}, {}
        for side in ("l", "r"):
            ua = self.pose.bones.get(f"upperarm_{side}")
            hand = self.pose.bones.get(f"hand_{side}")
            if not ua or not hand:
                self.down[side], self.hang[side] = 1.0, 0.0
                continue
            armdir = (hand.bone.matrix_local.translation
                      - ua.bone.matrix_local.translation)
            horiz = Vector((armdir.x, armdir.y, 0.0)).length
            self.hang[side] = math.degrees(math.atan2(-armdir.z, horiz)) if armdir.length > 1e-4 else 0.0
            if horiz > 1e-4:
                self.down[side] = -1.0 if self.fwd.cross(armdir.normalized()).z > 0 else 1.0
            else:
                self.down[side] = 1.0
        self._depth = {}
        for pb in self.pose.bones:
            d, b = 0, pb.bone
            while b.parent:
                d, b = d + 1, b.parent
            self._depth[pb.name] = d

    def hang_add(self, side, target):
        return max(0.0, target - self.hang[side])

    def reset(self):
        for pb in self.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()

    def apply(self, spec, frame, keyed):
        self.reset()
        for name in sorted(spec, key=lambda n: self._depth.get(n, 0)):
            if name not in self.pose.bones:
                continue
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
            pb = self.pose.bones.get(name)
            if pb:
                pb.keyframe_insert("rotation_quaternion", frame=frame)
                pb.keyframe_insert("location", frame=frame)


KEYED = [
    "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "Head",
    "clavicle_l", "clavicle_r", "upperarm_l", "upperarm_r",
    "lowerarm_l", "lowerarm_r", "hand_l", "hand_r",
    "thigh_l", "thigh_r", "calf_l", "calf_r",
    "foot_l", "foot_r", "ball_l", "ball_r",
]


def run_pose(phase, P):
    sL = math.sin(phase)
    sR = math.sin(phase + math.pi)
    dnL, dnR = P.down["l"], P.down["r"]
    thigh = lambda s: 52 * max(s, 0.0) + 30 * min(s, 0.0)
    # POSITIVE flexion. A knee bends BACKWARD -- the heel comes up toward the
    # buttock -- and under this poser's swing axis that is a positive angle.
    # The original negative value folded the shin forward, which reads exactly
    # as "the kneecap is on the back of the leg".
    knee = lambda ph: (18 + 58 * (0.5 + 0.5 * math.sin(ph - 1.9)))
    foot = lambda s: 12 * s + 8
    armsw = lambda s: 38 * s
    bob = 0.05 * math.sin(2 * phase) - 0.03
    sway = 6 * math.sin(phase)
    return {
        "pelvis":     (10, 0, sway, bob),
        "spine_01":   (6, 0, -sway * 0.6, 0),
        "spine_02":   (4, 0, -sway * 0.6, 0),
        "spine_03":   (2, 0, 0, 0),
        "neck_01":    (-8, 0, 0, 0),
        "Head":       (-6, 0, 0, 0),
        "clavicle_l": (0, dnL * 4, 0, 0),
        "clavicle_r": (0, dnR * 4, 0, 0),
        "upperarm_l": (armsw(sR), dnL * P.hang_add("l", 58), 0, 0),
        "upperarm_r": (armsw(sL), dnR * P.hang_add("r", 58), 0, 0),
        # NEGATIVE, for the same reason the knee is positive: an elbow folds
        # the hand FORWARD and up, which is the opposite sense to a knee
        # folding the heel back. Positive here bent the arm as though the
        # elbow were on the front of it.
        "lowerarm_l": (-(55 + 12 * sR), 0, 0, 0),
        "lowerarm_r": (-(55 + 12 * sL), 0, 0, 0),
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
    br = math.sin(phase)
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
        "upperarm_l": (3, dnL * (P.hang_add("l", 66) + 0.8 * br), 0, 0),
        "upperarm_r": (3, dnR * (P.hang_add("r", 66) + 0.8 * br), 0, 0),
        "lowerarm_l": (-12, 0, 0, 0),
        "lowerarm_r": (-12, 0, 0, 0),
        "hand_l":     (5, 0, 0, 0),
        "hand_r":     (5, 0, 0, 0),
        "thigh_l":    (0, -1.5, 0, 0),
        "thigh_r":    (0, 1.5, 0, 0),
        "calf_l":     (3, 0, 0, 0),
        "calf_r":     (3, 0, 0, 0),
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
        phase = 2 * math.pi * (f % n_frames) / n_frames
        poser.apply(pose_fn(phase, poser), f + 1, KEYED)
    act.use_fake_user = True
    track = arm.animation_data.nla_tracks.new()
    track.name = name
    track.mute = True
    track.strips.new(name, 1, act)
    arm.animation_data.action = None
    return act


# ------------------------------------------------------------------ build ---
def build(pid, cfg):
    clean_scene()
    bpy.ops.import_scene.gltf(filepath=os.path.join(SRC, cfg["src"]))
    for o in [o for o in bpy.data.objects if o.type in ("CAMERA", "LIGHT")]:
        bpy.data.objects.remove(o, do_unlink=True)
    for act in list(bpy.data.actions):
        if cfg["kind"] != "clips":
            bpy.data.actions.remove(act)

    normalize(cfg)
    tris = decimate_and_pack(pid)

    if cfg["kind"] == "auto":
        for o in [o for o in bpy.data.objects if o.type == "ARMATURE"]:
            bpy.data.objects.remove(o, do_unlink=True)
        for o in mesh_objects():
            for m in list(o.modifiers):
                if m.type == "ARMATURE":
                    o.modifiers.remove(m)
        body = join_meshes()
        arm_ob, how = build_armature(body, rigid=cfg.get("rigid", False))
    elif cfg["kind"] == "clips":
        arm_ob = next(o for o in bpy.data.objects if o.type == "ARMATURE")
        how = "own clips"
    else:
        arm_ob = adopt_rig(cfg)
        how = "adopted rig"

    if cfg["kind"] == "clips":
        # keep the pack's own clips, stashed on NLA so the exporter emits them
        for act in list(bpy.data.actions):
            if act.name not in cfg["keep"]:
                bpy.data.actions.remove(act)
        if not arm_ob.animation_data:
            arm_ob.animation_data_create()
        arm_ob.animation_data.action = None
        for tr in list(arm_ob.animation_data.nla_tracks):
            arm_ob.animation_data.nla_tracks.remove(tr)
        for act in bpy.data.actions:
            act.use_fake_user = True
            track = arm_ob.animation_data.nla_tracks.new()
            track.name = act.name
            track.mute = True
            track.strips.new(act.name, 1, act)
    else:
        poser = Poser(arm_ob)
        author_action(arm_ob, poser, "Idle", IDLE_FRAMES, idle_pose, 12)
        author_action(arm_ob, poser, "Run", RUN_FRAMES, run_pose, 2)
        poser.reset()

    out = os.path.join(OUT_DIR, f"{pid}.glb")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=True,
        export_animations=True,
        export_skins=True,
        export_yup=True,
        export_image_format="WEBP",
        export_image_quality=80,
        export_apply=False,
    )
    print(f"[{pid}] {how}, {tris} tris → {os.path.getsize(out) // 1024} KB")


# ------------------------------------------------- run-frame verify sheet ---
def verify(ids):
    clean_scene()
    sp = 3.0
    cols = 7
    for i, pid in enumerate(ids):
        path = os.path.join(OUT_DIR, f"{pid}.glb")
        if not os.path.exists(path):
            continue
        before = set(bpy.data.objects)
        before_acts = set(bpy.data.actions)
        bpy.ops.import_scene.gltf(filepath=path)
        new = [o for o in bpy.data.objects if o not in before]
        new_acts = [a for a in bpy.data.actions if a not in before_acts]
        arm = next((o for o in new if o.type == "ARMATURE"), None)
        # each rig gets ITS OWN Run clip (names collide across imports)
        run = next((a for a in new_acts if "Run" in a.name), None)
        if arm and run:
            if not arm.animation_data:
                arm.animation_data_create()
            arm.animation_data.action = run
        cell = Vector(((i % cols) * sp, 0, -(i // cols) * 3.0))
        for o in new:
            if o.parent is None:
                o.location += cell
                o.rotation_euler.z += 0.6  # 3/4 so leg/arm swing reads
        txt = bpy.data.curves.new(pid, type="FONT")
        txt.body = pid
        txt.size = 0.22
        ob = bpy.data.objects.new(pid + "_label", txt)
        ob.location = cell + Vector((-1.2, 0, 2.15))
        ob.rotation_euler = (1.5708, 0, 0)
        bpy.context.scene.collection.objects.link(ob)
    bpy.context.scene.frame_set(5)  # mid-stride
    rows = (len(ids) + cols - 1) // cols
    cam = bpy.data.cameras.new("cam")
    cam.type = "ORTHO"
    cam.ortho_scale = cols * sp + 1
    co = bpy.data.objects.new("cam", cam)
    co.location = ((cols - 1) * sp / 2, -40, -(rows - 1) * 1.5 + 1.0)
    co.rotation_euler = (1.5708, 0, 0)
    bpy.context.scene.collection.objects.link(co)
    bpy.context.scene.camera = co
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.display.shading.light = "STUDIO"
    sc.display.shading.color_type = "TEXTURE"
    sc.render.resolution_x = 2200
    sc.render.resolution_y = int(2200 * (rows * 3.0 + 0.5) / (cols * sp + 1))
    sc.render.filepath = VERIFY
    bpy.ops.render.render(write_still=True)
    print("rendered", VERIFY)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    bpy.context.scene.render.fps = FPS
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else ["all"]
    if args[0] == "verify":
        verify(args[1:] or sorted(CHARACTERS))
        return
    ids = sorted(CHARACTERS) if args == ["all"] else args
    for pid in ids:
        try:
            build(pid, CHARACTERS[pid])
        except Exception as e:
            print(f"[{pid}] FAILED: {type(e).__name__}: {str(e)[:200]}")
    print("done:", ", ".join(ids))


main()
