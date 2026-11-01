# Build the ხილ-ბოსტნეული (fruit & vegetable) race from 3D Everything's
# "Cartoon - Fruits and Vegetables" pack (Asset Store, bought 2026-10-26):
# fifteen LIMBLESS character meshes (a fruit with a face, ~5k tris, no bones,
# no clips). The user wants them "like the JS banana": cartoon arms with
# mitten hands and legs with shoes, a humanoid skeleton, idle + run, parkour
# later. So each body gets:
#
#   1. the fruit scaled to BODY_H and stood on legs so the whole figure is
#      HEIGHT (1.8 m — the game shows every hero at that height),
#   2. tube arms in T-pose with elbow/knee balls, mitten hands with a thumb,
#      tube legs and shoes — every limb piece its OWN mesh island with its
#      vertex weights authored here (the piece knows which bone it is), the
#      fruit itself rigid on spine_02 (a fruit does not bend),
#   3. tools/build_characters.py's UE-named 22-bone armature, built at the
#      exact joints the limbs were made with (no mesh fitting to guess),
#   4. the same Poser-authored Idle/Run that every other personage carries,
#   5. GLB to public/models/personages/<id>.glb; tools/glb_batch_fbx.py then
#      makes the Unity FBX (unity/Assets/Resources/Art/Characters/<id>.fbx)
#      and Editor/CharacterTexturePostprocessor puts the pack's albedo back
#      (the GLB→FBX bridge loses image names — the plane-livery lesson).
#
# Run: /Applications/Blender.app/Contents/MacOS/Blender -b --python tools/build_veggies.py -- all|<ids>|lineup
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "tools", "cache", "fv")
TEX = os.path.join(SRC, "Textures")
OUT_DIR = os.path.join(ROOT, "public", "models", "personages")
LINEUP = os.path.join(ROOT, "tools", "cache", "veg_lineup.png")

# Borrow the rig conventions, the Poser and the Idle/Run authoring from the
# personage pipeline without running its main().
_ns = {"__file__": os.path.join(ROOT, "tools", "build_characters.py"), "__name__": "build_characters"}
with open(os.path.join(ROOT, "tools", "build_characters.py"), encoding="utf-8") as _f:
    _src = _f.read().rsplit("\nmain()", 1)[0]
exec(compile(_src, "build_characters.py", "exec"), _ns)
Poser, author_action = _ns["Poser"], _ns["author_action"]
run_pose, idle_pose = _ns["run_pose"], _ns["idle_pose"]
RUN_FRAMES, IDLE_FRAMES = _ns["RUN_FRAMES"], _ns["IDLE_FRAMES"]

HEIGHT = 1.8
# ---- the figure, metres, in the final frame (face -Y, feet on z=0) --------
# Proportions are the OLD JS banana's (Polygonal Mind "Cool Banana Guy",
# git HEAD public/models/personages/banana.glb, measured): body 0.55-1.80 m,
# stick legs x=+-0.09 from 0.86 m down, arms rooted at ~42% of the body
# height on its sides (z ~1.08), 0.29/0.23 m upper/lower arm, small spread-
# finger hands, small feet. The user: "they must have foot hands like js
# project banana" and "hands and foots are not placed in real place".
BODY_BOTTOM = 0.55          # the fruit's lowest point
BODY_H = HEIGHT - BODY_BOTTOM
HIP_X, HIP_Z, KNEE_Z, ANKLE_Z = 0.09, 0.86, 0.47, 0.11
ARM_FRAC = 0.42             # arm root height as a fraction of the fruit's height (the old banana's)
ARM_SCAN = (0.30, 0.56)     # the band each arm scans for its own root on its side
# banana/cucumber ONLY (user: "only banana cucumber"): arms above the bent tip, on the stroke's sides
ARM_FRAC_BY = {"cucumber": 0.56}                 # banana: default 42% like the old JS banana (its eyes sit at 56%)
ARM_SCAN_BY = {"cucumber": (0.48, 0.66)}
UPPER, LOWER, HAND = 0.29, 0.23, 0.14
ARM_R, LEG_R = 0.036, 0.044
PALM = (0.050, 0.042, 0.028)     # x (along the arm), y, z radii
FINGER_R, FINGER_L = 0.014, 0.075
SHOE = (0.065, 0.125, 0.045)     # x, y (toe forward, -Y), z radii
SHOE_C = (-0.045, 0.045)         # shoe centre y, z

UPRIGHT = {"banana", "cucumber"}   # 2026-10-31 23:30: the automatic stand-up/face-move was REJECTED by the user ("revert cucumber and banana");
                  # the banana/cucumber faces are edited BY HAND in Blender instead — see MANUAL below
MANUAL = os.path.join(SRC, "manual")   # <id>.blend with an object "Body" replaces the pack FBX when present

VEG = {  # id → (model file, albedo, normal)  — ids are the game's body ids
    "apple":      ("Apple.fbx",      "apple.png",      "apple N.png"),
    "banana":     ("Banana.fbx",     "banana.png",     "banana N.png"),
    "broccoli":   ("Broccoli.fbx",   "broccoli.png",   "broccoli N.png"),
    "cherry":     ("Cherry.fbx",     "cherry.png",     "cherry N.png"),
    "cucumber":   ("Cucumber.fbx",   "cucumber.png",   "cucumber N.png"),
    "kiwi":       ("Kiwi.fbx",       "kiwi.png",       "kiwi 7 N.png"),
    "lettuce":    ("Lettuce.fbx",    "lettuce.png",    "lettuce N.png"),
    "onion":      ("Onion.fbx",      "onion.png",      "onion N.png"),
    "orange":     ("Orange.fbx",     "orange.png",     "orange N.png"),
    "pear":       ("Pear.fbx",       "pear.png",       "pear N.png"),
    "plum":       ("Plum.fbx",       "plum.png",       None),
    "potato":     ("Potato.fbx",     "potato.png",     "potato N.png"),
    "red_pepper": ("Red pepper.fbx", "red pepper.png", "red pepper N.png"),
    "tomato":     ("Tomato.fbx",     "tomato.png",     "tomato N.png"),
    "strawberry": ("strawberry.fbx", "strawberry.png", "strawberry N.png"),
}


# ------------------------------------------------------------------ helpers
def clean_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mesh_objects():
    return [o for o in bpy.data.objects if o.type == "MESH"]


def bbox(objs):
    """World bounds off the VERTICES — Object.bound_box lags behind direct
    vertex edits (stand_up) until a depsgraph update, which once scaled a
    stood-up banana off its pre-rotation box."""
    lo = Vector((1e9,) * 3)
    hi = Vector((-1e9,) * 3)
    for o in objs:
        mw = o.matrix_world
        pts = [mw @ v.co for v in o.data.vertices] if o.type == "MESH" else [mw @ Vector(c) for c in o.bound_box]
        for p in pts:
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    return lo, hi


def mat_color(name, rgb, rough=0.7):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    return m


def mat_textured(name, albedo, normal):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = 0.65
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(os.path.join(TEX, albedo))
    tex.image.name = os.path.splitext(albedo)[0]
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    if normal and os.path.exists(os.path.join(TEX, normal)):
        ntex = nt.nodes.new("ShaderNodeTexImage")
        ntex.image = bpy.data.images.load(os.path.join(TEX, normal))
        ntex.image.colorspace_settings.name = "Non-Color"
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.inputs["Strength"].default_value = 0.6
        nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    return m, tex.image


def mean_color(image):
    """Average albedo of the pack texture — the limb colour. Sampled off a
    32x32 copy so a 2048² PNG does not cost 16M floats."""
    small = image.copy()
    small.scale(32, 32)
    px = small.pixels[:]
    r = g = b = 0.0
    n = 0
    for i in range(0, len(px), 4):
        if px[i + 3] < 0.5:
            continue
        r += px[i]; g += px[i + 1]; b += px[i + 2]
        n += 1
    bpy.data.images.remove(small)
    if n == 0:
        return (0.8, 0.7, 0.3)
    return (r / n, g / n, b / n)


def new_mesh_object(name, verts, faces, material):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    me.materials.append(material)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    for p in me.polygons:
        p.use_smooth = True
    return ob


def tube(name, p0, p1, r, material, rings=6, segs=14):
    """A capped cylinder from p0 to p1 with `rings` cross-sections, so a
    blended weight along its length has vertices to live on."""
    p0, p1 = Vector(p0), Vector(p1)
    axis = (p1 - p0)
    L = axis.length
    axis.normalize()
    helper = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((0, 1, 0))
    u = axis.cross(helper).normalized()
    v = axis.cross(u).normalized()
    verts, faces = [], []
    for i in range(rings + 1):
        t = i / rings
        c = p0 + axis * (L * t)
        for k in range(segs):
            a = 2 * math.pi * k / segs
            verts.append(c + u * (r * math.cos(a)) + v * (r * math.sin(a)))
    for i in range(rings):
        for k in range(segs):
            a = i * segs + k
            b = i * segs + (k + 1) % segs
            faces.append((a, b, b + segs, a + segs))
    c0 = len(verts); verts.append(p0)
    c1 = len(verts); verts.append(p1)
    for k in range(segs):
        faces.append((c0, (k + 1) % segs, k))
        faces.append((c1, rings * segs + k, rings * segs + (k + 1) % segs))
    ob = new_mesh_object(name, verts, faces, material)
    ob["t_axis"] = tuple(axis); ob["t_p0"] = tuple(p0); ob["t_len"] = L
    return ob


def ball(name, center, radii, material, segs=16, rings=10):
    verts, faces = [], []
    for i in range(1, rings):
        ph = math.pi * i / rings
        for k in range(segs):
            th = 2 * math.pi * k / segs
            verts.append(Vector((math.sin(ph) * math.cos(th) * radii[0],
                                 math.sin(ph) * math.sin(th) * radii[1],
                                 math.cos(ph) * radii[2])) + Vector(center))
    top = len(verts); verts.append(Vector(center) + Vector((0, 0, radii[2])))
    bot = len(verts); verts.append(Vector(center) - Vector((0, 0, radii[2])))
    for i in range(rings - 2):
        for k in range(segs):
            a = i * segs + k; b = i * segs + (k + 1) % segs
            faces.append((a, a + segs, b + segs, b))
    for k in range(segs):
        faces.append((top, (k + 1) % segs, k))
        last = (rings - 2) * segs
        faces.append((bot, last + k, last + (k + 1) % segs))
    return new_mesh_object(name, verts, faces, material)


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def weight_tube(ob, bone, next_bone, blend_from=0.72):
    """Inner part of the tube on `bone`, the last (1-blend_from) blending
    into `next_bone` so the joint ball covers a smooth bend."""
    axis, p0, L = Vector(ob["t_axis"]), Vector(ob["t_p0"]), ob["t_len"]
    g0 = ob.vertex_groups.new(name=bone)
    g1 = ob.vertex_groups.new(name=next_bone) if next_bone else None
    for v in ob.data.vertices:
        t = (v.co - p0).dot(axis) / L
        w = smoothstep(blend_from, 1.0, t) if g1 else 0.0
        g0.add([v.index], 1.0 - w, "REPLACE")
        if g1 and w > 0:
            g1.add([v.index], w, "REPLACE")


def weight_all(ob, bone, w=1.0, second=None, w2=0.0):
    g = ob.vertex_groups.new(name=bone)
    g.add([v.index for v in ob.data.vertices], w, "REPLACE")
    if second:
        g2 = ob.vertex_groups.new(name=second)
        g2.add([v.index for v in ob.data.vertices], w2, "REPLACE")


# ------------------------------------------------------------------ build
def prepare_body(pid, cfg):
    """Import the fruit, join it, face -Y, scale to BODY_H, stand it at BODY_BOTTOM."""
    model, albedo, normal = cfg
    manual = os.path.join(MANUAL, f"{pid}.blend")
    if os.path.exists(manual):
        # the user's hand-edited body (eyes moved etc.): append its "Body" object
        with bpy.data.libraries.load(manual, link=False) as (src, dst):
            dst.objects = [n for n in src.objects if n == "Body"] or list(src.objects)
        for o in dst.objects:
            if o is not None:
                bpy.context.scene.collection.objects.link(o)
        print(f"VEG id={pid} source=manual {manual}")
    else:
        bpy.ops.import_scene.fbx(filepath=os.path.join(SRC, model))
    for o in [o for o in bpy.data.objects if o.type in ("CAMERA", "LIGHT", "EMPTY")]:
        bpy.data.objects.remove(o, do_unlink=True)
    objs = mesh_objects()
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    body.name = "Body"
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    lo, hi = bbox([body])
    s = BODY_H / (hi.z - lo.z)
    body.scale = (s, s, s)
    bpy.context.view_layer.update()
    lo, hi = bbox([body])
    mid = (lo + hi) / 2
    body.location = (-mid.x, -mid.y, BODY_BOTTOM - lo.z)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    # the pack's own albedo (+ normal) on a material named after the texture,
    # which is what the Unity postprocessor keys on. The pack FBX already
    # carries a material of that very name — purge it first or ours becomes
    # "apple.001" and the lookup misses (it did: 1 of 15 matched).
    body.data.materials.clear()
    for m in list(bpy.data.materials):
        bpy.data.materials.remove(m)
    for im in list(bpy.data.images):
        bpy.data.images.remove(im)
    m, image = mat_textured(os.path.splitext(albedo)[0], albedo, normal)
    body.data.materials.append(m)
    if pid in UPRIGHT or True:
        if pid in UPRIGHT:
            stand_up(body, image, pid)
        else:
            auto_yaw(body, image, pid)
        # re-stand on the floor after the vertices moved
        lo, hi = bbox([body])
        s2 = BODY_H / (hi.z - lo.z)
        body.scale = (s2, s2, s2)
        bpy.context.view_layer.update()
        lo, hi = bbox([body])
        mid = (lo + hi) / 2
        body.location = (-mid.x, -mid.y, BODY_BOTTOM - lo.z)
        bpy.context.view_layer.update()
        bpy.ops.object.select_all(action="DESELECT")
        body.select_set(True)
        bpy.context.view_layer.objects.active = body
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    weight_all(body, "spine_02")
    return body, image


POSE_DEG = {"banana": 16.0, "cucumber": 20.0}   # rigid body rotation about the front axis (Y), counter-clockwise as seen from the front
                                                 # — the user's own Blender pose (2026-10-31 23:29: stem up, tip down-left)
POSE_YAW = {"banana": 90.0, "cucumber": 90.0}   # then about the vertical: the user's 23:45 screenshot — seen EDGE-ON the pack's
                                                 # banana is a standing banana with the hook at its foot, so that side is the
                                                 # front: the face turns to the viewer's right, the hook points forward


POSE_PSI = {"banana": -10.0}   # yaw after the auto frame: 0 = pure profile, negative turns the face toward the camera
AUTO_POSE = {"banana"}   # 2026-11-01 00:40, user: "cucumber legs and hands are very good, but banana need fixes".
                         # The old JS banana is STRAIGHT from the front: its curve lies in the front/back plane and
                         # the face looks forward. The pack banana rests at a 45° yaw with its eyes off the chord, so
                         # the hand-typed POSE tables left it leaning with the legs under the hook and an arm at the
                         # mouth. For the banana the rigid rotation is now COMPUTED from the mesh instead: the thin
                         # end of the PCA chord (the stalk) goes up, the side the eyes protrude from goes to -Y.


def auto_frame(P, me, image):
    """(stem_dir, face_dir, n_eye) unit vectors of a fruit body. stem = the THIN
    end of the principal axis (the stalk). face = where the EYES are: polygons
    whose albedo at their UV centroid is eye-white or pupil-black (the pack
    paints the eyes into the one texture). A protrusion test was tried first
    and picked the BACK: the eye spheres' dense vertices drag every slice's
    mean centre toward the face, so the far side read as the bulge."""
    import numpy as np
    c = P.mean(axis=0)
    w, V = np.linalg.eigh(np.cov((P - c).T))
    axis = V[:, int(np.argmax(w))]
    t = (P - c) @ axis
    nb = 24
    bins = np.linspace(t.min(), t.max() + 1e-6, nb + 1)
    idx = np.clip(np.digitize(t, bins) - 1, 0, nb - 1)
    cen = np.array([P[idx == i].mean(axis=0) if (idx == i).any() else c for i in range(nb)])
    rad = np.linalg.norm(P - cen[idx], axis=1)
    med = np.array([np.median(rad[idx == i]) if (idx == i).any() else 0.0 for i in range(nb)])
    stem = -axis if med[:3].mean() < med[-3:].mean() else axis
    eyes, _nb, _nw = eye_polys(me, image)
    if len(eyes) < 10:
        raise RuntimeError(f"auto_frame: only {len(eyes)} eye polygons found")
    f = np.array(eyes).mean(axis=0) - c
    f -= (f @ axis) * axis
    f /= max(np.linalg.norm(f), 1e-9)
    return stem, f, len(eyes)


def eye_polys(me, image):
    """Centres of the polygons whose albedo at their UV centroid is pupil-black
    or eye-white. Black first: a white body (onion, kiwi flesh) would drown the
    sclera, the pupils are unique. Returns (centres, n_black, n_white)."""
    import numpy as np
    W, H = image.size
    px = np.array(image.pixels[:]).reshape(H, W, 4)[:, :, :3]
    uv = me.uv_layers.active.data
    black, white = [], []
    for poly in me.polygons:
        u = np.mean([uv[li].uv[0] for li in poly.loop_indices])
        v = np.mean([uv[li].uv[1] for li in poly.loop_indices])
        col = px[int(v % 1.0 * (H - 1)), int(u % 1.0 * (W - 1))]
        mx, mn = col.max(), col.min()
        if mx < 0.15:
            black.append(np.array(poly.center))
        elif mn > 0.75 and mx - mn < 0.2:
            white.append(np.array(poly.center))
    pts = black if len(black) >= 10 else (black + white)
    return pts, len(black), len(white)


def auto_yaw(body, image, pid):
    """2026-11-01 13:20, user: "my personages are not lokin fowart when i
    choose them, i tryed appla and he is walkin with backside". The pack's
    bodies rest at arbitrary yaws (the lineup showed kiwi, orange, plum, potato,
    pepper with their backs to the camera). Rigid rotation about the vertical
    axis ONLY, about the centroid, so the eyes look down -Y (the character's
    front). Face untouched, nothing tilted."""
    import numpy as np
    me = body.data
    P = np.array([[v.co.x, v.co.y, v.co.z] for v in me.vertices], dtype=np.float64)
    c = P.mean(axis=0)
    pts, nb, nw = eye_polys(me, image)
    if len(pts) < 10:
        print(f"AUTOYAW id={pid} eyePolys={len(pts)} black={nb} white={nw} SKIPPED (no eyes found)")
        return
    f = np.array(pts).mean(axis=0) - c
    f[2] = 0.0
    r = float(np.sqrt(((P[:, :2] - c[:2]) ** 2).sum(axis=1).mean()))
    if np.linalg.norm(f) < 0.15 * r:
        print(f"AUTOYAW id={pid} eyePolys={len(pts)} black={nb} white={nw} faceDir=({f[0]:.3f},{f[1]:.3f}) SKIPPED (eyes not off-centre: {np.linalg.norm(f):.3f} of r={r:.3f})")
        return
    # rotate about Z so that f -> (0,-1): angle from f to -Y
    ang = math.atan2(-1.0, 0.0) - math.atan2(f[1], f[0])
    ca, sa = math.cos(ang), math.sin(ang)
    x, y = P[:, 0] - c[0], P[:, 1] - c[1]
    P[:, 0] = c[0] + x * ca - y * sa
    P[:, 1] = c[1] + x * sa + y * ca
    for k, vert in enumerate(me.vertices):
        vert.co = Vector(P[k])
    me.update()
    bpy.context.view_layer.update()
    print(f"AUTOYAW id={pid} eyePolys={len(pts)} black={nb} white={nw} faceDir=({f[0]:.3f},{f[1]:.3f}) yawDeg={math.degrees(ang):.1f} face=untouched")


def stand_up(body, image, pid):
    """WHOLE-BODY ROTATION ONLY (user, 2026-10-31 23:45: "u need to rotate
    whole body", every face edit rejected): the mesh is rotated as one rigid
    piece about the front axis Y by POSE_DEG — the pose the user set by hand
    in Blender (stem up, tip down). The face stays exactly as the pack
    sculpted it, turned with the body. Nothing else is touched."""
    import numpy as np
    me = body.data
    P = np.array([[v.co.x, v.co.y, v.co.z] for v in me.vertices], dtype=np.float64)
    c = P.mean(axis=0)
    if pid in AUTO_POSE:
        stem, face, n_eye = auto_frame(P, me, image)
        # The pack paints the eyes on the FLANK of the crescent (face·crescent-normal = 0.86,
        # measured), not inside the curve like the old JS banana, so a face-forward pose shows a
        # C-shape and the only straight-looking front is the user's edge-on pick (23:45 screenshot:
        # stalk up, eyes on the viewer's right in profile). Frame: stalk → +Z, eyes → +X, then a
        # small yaw POSE_PSI about Z (the 7-angle sweep: -15..0 matches the screenshot).
        e3 = stem
        e1 = face - (face @ stem) * stem
        e1 /= max(np.linalg.norm(e1), 1e-9)
        e2 = np.cross(e3, e1)
        R0 = np.vstack([e1, e2, e3])    # rows: proper rotation taking stem→+Z, eyes→+X
        psi = math.radians(POSE_PSI.get(pid, 0.0))
        Rz = np.array([[math.cos(psi), -math.sin(psi), 0.0], [math.sin(psi), math.cos(psi), 0.0], [0.0, 0.0, 1.0]])
        R = Rz @ R0
        P = (P - c) @ R.T + c
        for k, vert in enumerate(me.vertices):
            vert.co = Vector(P[k])
        me.update()
        bpy.context.view_layer.update()
        print(f"POSE id={pid} auto stem=({stem[0]:.2f},{stem[1]:.2f},{stem[2]:.2f}) face=({face[0]:.2f},{face[1]:.2f},{face[2]:.2f}) "
              f"eyePolys={n_eye} psi={POSE_PSI.get(pid, 0.0):.0f} det={float(np.linalg.det(R)):.3f} face=untouched")
        return
    phi = math.radians(POSE_DEG.get(pid, 0.0))
    cp, sp = math.cos(phi), math.sin(phi)
    x, z = P[:, 0] - c[0], P[:, 2] - c[2]
    P[:, 0] = c[0] + x * cp - z * sp
    P[:, 2] = c[2] + x * sp + z * cp
    yaw = math.radians(POSE_YAW.get(pid, 0.0))
    cy, sy = math.cos(yaw), math.sin(yaw)
    x, y = P[:, 0] - c[0], P[:, 1] - c[1]
    P[:, 0] = c[0] + x * cy - y * sy
    P[:, 1] = c[1] + x * sy + y * cy
    for k, vert in enumerate(me.vertices):
        vert.co = Vector(P[k])
    me.update()
    bpy.context.view_layer.update()
    print(f"POSE id={pid} body_rot_deg={POSE_DEG.get(pid, 0.0):.1f} yaw_deg={POSE_YAW.get(pid, 0.0):.1f} face=untouched")


def arm_root(body, z_nom, sgn, scan=ARM_SCAN):
    """Where THIS arm meets the fruit. A banana or a cucumber is curved: at
    the nominal arm height one side may have no body at all, so each side
    scans a band of heights (40-70% of the fruit) for the height where the
    fruit reaches out furthest on that side, and the arm attaches THERE —
    3 cm inside the skin, at that vertex's y. Returns (x, y, z)."""
    best = (0.0, 0.0, z_nom)
    z0, z1 = BODY_BOTTOM + scan[0] * BODY_H, BODY_BOTTOM + scan[1] * BODY_H
    z = z0
    while z <= z1 + 1e-6:
        ext, ey = 0.0, 0.0
        for v in body.data.vertices:
            if abs(v.co.z - z) < 0.05 and v.co.x * sgn > ext:
                ext, ey = v.co.x * sgn, v.co.y
        # prefer the nominal height a little, so symmetric fruits stay level
        score = ext - 0.15 * abs(z - z_nom)
        if score > best[0] - 0.15 * abs(best[2] - z_nom):
            best = (ext, ey, z)
        z += 0.03
    return max(best[0] - 0.03, 0.08), best[1], best[2]


def build_limbs(body, skin, glove, shoe_mat, pid=None):
    z_arm = BODY_BOTTOM + ARM_FRAC_BY.get(pid, ARM_FRAC) * BODY_H
    scan = ARM_SCAN_BY.get(pid, ARM_SCAN)
    joints = {"arm_z": z_arm}
    pieces = []
    low = [v.co.x for v in body.data.vertices if v.co.z < BODY_BOTTOM + 0.25 * BODY_H]
    low.sort()
    hip_cx = low[len(low) // 2] if low else 0.0          # legs stand under the LOWER body, not under the box centre
    joints["hip_cx"] = hip_cx
    for sgn, side in ((1, "l"), (-1, "r")):
        rx, ry, z_arm = arm_root(body, joints["arm_z"], sgn, scan)
        joints[f"root_x_{side}"], joints[f"root_y_{side}"], joints[f"arm_z_{side}"] = rx, ry, z_arm
        joints[f"elbow_x_{side}"], joints[f"wrist_x_{side}"], joints[f"tip_x_{side}"] = rx + UPPER, rx + UPPER + LOWER, rx + UPPER + LOWER + HAND
        x0 = sgn * rx
        elbow = Vector((sgn * (rx + UPPER), ry, z_arm))
        wrist = Vector((sgn * (rx + UPPER + LOWER), ry, z_arm))
        tip = Vector((sgn * (rx + UPPER + LOWER + HAND), ry, z_arm))
        ua = tube(f"upperarm_{side}", (x0, ry, z_arm), elbow, ARM_R, skin)
        weight_tube(ua, f"upperarm_{side}", f"lowerarm_{side}")
        la = tube(f"lowerarm_{side}", elbow, wrist, ARM_R * 0.92, skin)
        weight_tube(la, f"lowerarm_{side}", f"hand_{side}", 0.80)
        eb = ball(f"elbow_{side}", elbow, (ARM_R * 1.15,) * 3, skin)
        weight_all(eb, f"upperarm_{side}", 0.5, f"lowerarm_{side}", 0.5)
        # the hand: a palm at the wrist and three fingers fanned in the
        # horizontal plane (they point DOWN once the arm hangs), a thumb
        # forward — the old banana's spread cartoon hand, not a mitten
        pc = wrist + Vector((sgn * PALM[0] * 0.9, 0, 0))
        palm = ball(f"hand_{side}", pc, PALM, glove)
        weight_all(palm, f"hand_{side}")
        pieces += [ua, la, eb, palm]
        for k, ang in enumerate((-22.0, 0.0, 22.0)):
            a = math.radians(ang)
            f0 = pc + Vector((sgn * PALM[0] * 0.6, 0, 0))
            f1 = f0 + Vector((sgn * math.cos(a) * FINGER_L, math.sin(a) * FINGER_L, 0))
            fg = tube(f"finger_{side}{k}", f0, f1, FINGER_R, glove, rings=2, segs=8)
            weight_all(fg, f"hand_{side}")
            pieces.append(fg)
        t0 = pc + Vector((-sgn * PALM[0] * 0.2, -PALM[1] * 0.6, 0))
        t1 = t0 + Vector((sgn * 0.02, -FINGER_L * 0.8, 0))
        th = tube(f"thumb_{side}", t0, t1, FINGER_R, glove, rings=2, segs=8)
        weight_all(th, f"hand_{side}")
        pieces.append(th)

        hip = Vector((hip_cx + sgn * HIP_X, 0, HIP_Z + 0.08))
        knee = Vector((hip_cx + sgn * HIP_X, 0, KNEE_Z))
        ankle = Vector((hip_cx + sgn * HIP_X, 0, ANKLE_Z))
        th = tube(f"thigh_{side}", hip, knee, LEG_R, skin)
        weight_tube(th, f"thigh_{side}", f"calf_{side}")
        ca = tube(f"calf_{side}", knee, ankle, LEG_R * 0.9, skin)
        weight_tube(ca, f"calf_{side}", f"foot_{side}", 0.85)
        kb = ball(f"knee_{side}", knee, (LEG_R * 1.1,) * 3, skin)
        weight_all(kb, f"thigh_{side}", 0.5, f"calf_{side}", 0.5)
        sh = ball(f"shoe_{side}", (hip_cx + sgn * HIP_X, SHOE_C[0], SHOE_C[1]), SHOE, shoe_mat)
        weight_all(sh, f"foot_{side}")
        pieces += [th, ca, kb, sh]
    return pieces, joints


def join_all(body, pieces):
    bpy.ops.object.select_all(action="DESELECT")
    for o in pieces:
        o.select_set(True)
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.join()
    return body


def build_armature(body, j):
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

    az = j["arm_z"]
    chest = az - 0.02
    s1 = HIP_Z + 0.10
    s2 = s1 + (chest - s1) * 0.5
    hc0 = j["hip_cx"]
    eb("pelvis", (hc0, 0, HIP_Z - 0.02), (hc0 * 0.5, 0, s1))
    eb("spine_01", (hc0 * 0.5, 0, s1), (0, 0, s2), "pelvis", True)
    eb("spine_02", (0, 0, s2), (0, 0, chest), "spine_01", True)
    eb("spine_03", (0, 0, chest), (0, 0, az + 0.08), "spine_02", True)
    neck = az + 0.08
    eb("neck_01", (0, 0, neck), (0, 0, neck + 0.10), "spine_03", True)
    eb("Head", (0, 0, neck + 0.10), (0, 0, HEIGHT), "neck_01", True)
    for sgn, side in ((1, "l"), (-1, "r")):
        ry, az = j[f"root_y_{side}"], j[f"arm_z_{side}"]
        root = (sgn * j[f"root_x_{side}"], ry, az)
        eb(f"clavicle_{side}", (sgn * 0.04, ry, az), root, "spine_03")
        eb(f"upperarm_{side}", root, (sgn * j[f"elbow_x_{side}"], ry, az), f"clavicle_{side}")
        eb(f"lowerarm_{side}", (sgn * j[f"elbow_x_{side}"], ry, az), (sgn * j[f"wrist_x_{side}"], ry, az), f"upperarm_{side}", True)
        eb(f"hand_{side}", (sgn * j[f"wrist_x_{side}"], ry, az), (sgn * j[f"tip_x_{side}"], ry, az), f"lowerarm_{side}", True)
        hx = j["hip_cx"] + sgn * HIP_X
        eb(f"thigh_{side}", (hx, 0, HIP_Z), (hx, 0, KNEE_Z), "pelvis")
        eb(f"calf_{side}", (hx, 0, KNEE_Z), (hx, 0, ANKLE_Z), f"thigh_{side}", True)
        eb(f"foot_{side}", (hx, 0, ANKLE_Z), (hx, -0.11, 0.03), f"calf_{side}", True)
        eb(f"ball_{side}", (hx, -0.11, 0.03), (hx, -0.22, 0.03), f"foot_{side}", True)
    bpy.ops.object.mode_set(mode="OBJECT")

    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.parent_set(type="ARMATURE_NAME")   # keeps the groups authored above
    return ob


def build(pid):
    cfg = VEG[pid]
    clean_scene()
    body, image = prepare_body(pid, cfg)
    base = mean_color(image)
    skin = mat_color(f"{pid}_skin", base, 0.6)
    glove = mat_color("glove", (0.96, 0.94, 0.90), 0.5)
    shoe_mat = mat_color("shoe", (0.16, 0.12, 0.10), 0.45)
    pieces, joints = build_limbs(body, skin, glove, shoe_mat, pid)
    body = join_all(body, pieces)
    arm_ob = build_armature(body, joints)

    poser = Poser(arm_ob)
    author_action(arm_ob, poser, "Idle", IDLE_FRAMES, idle_pose, 12)
    author_action(arm_ob, poser, "Run", RUN_FRAMES, run_pose, 2)
    poser.reset()

    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, f"{pid}.glb")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", use_selection=True,
        export_animations=True, export_skins=True, export_yup=True,
        export_image_format="WEBP", export_image_quality=80, export_apply=False,
    )
    tris = sum(len(p.vertices) - 2 for p in body.data.polygons)
    lo, hi = bbox([body])
    print(f"VEG id={pid} tris={tris} verts={len(body.data.vertices)} height={hi.z - lo.z:.3f} "
          f"span={hi.x - lo.x:.3f} armZ={joints['arm_z_l']:.2f}/{joints['arm_z_r']:.2f} rootX={joints['root_x_l']:.2f}/{joints['root_x_r']:.2f} "
          f"bones={len(arm_ob.data.bones)} skin=({base[0]:.2f},{base[1]:.2f},{base[2]:.2f}) kb={os.path.getsize(out) // 1024}")


def lineup(ids):
    clean_scene()
    n = len(ids)
    for i, pid in enumerate(ids):
        path = os.path.join(OUT_DIR, f"{pid}.glb")
        if not os.path.exists(path):
            continue
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=path)
        new = [o for o in bpy.data.objects if o not in before and o.parent is None]
        for o in new:
            o.location.x += (i - (n - 1) / 2) * 1.9
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = min(400 * n, 6000)
    scene.render.resolution_y = 900
    cam_data = bpy.data.cameras.new("c")
    cam = bpy.data.objects.new("c", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (0, -(1.45 * n + 3.0), 1.0)
    cam.rotation_euler = (math.radians(88), 0, 0)
    cam_data.lens = 35
    scene.camera = cam
    scene.render.filepath = LINEUP
    bpy.ops.render.render(write_still=True)
    print(f"LINEUP {LINEUP} bodies={n}")


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else ["all"]
    if args[0] == "lineup":
        lineup(args[1:] or sorted(VEG))
        return
    ids = sorted(VEG) if args == ["all"] else args
    for pid in ids:
        try:
            build(pid)
        except Exception as e:
            import traceback
            traceback.print_exc()
            print(f"VEG id={pid} FAILED: {type(e).__name__}: {str(e)[:200]}")
    print("done:", ", ".join(ids))


main()
