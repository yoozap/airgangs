"""The AKM WITH its moving parts -- magazine, bolt carrier, trigger, mag
release, a round and a case -- on their own bones, carrying the Sketchfab
clip's animation of them.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/import_akm_anim.py

Source: "AKM FPS ANIMATION" by BURNER (CC BY 4.0), the same author and the same
layout as the Beretta: a pair of Mixamo arms (Ch22_Body) on one armature and
the rifle on another, 15-bone one (ARMA_044 root; CARGADOR magazine, CAMARA
bolt carrier, GATILLO trigger, COSO mag release, BALA round, CASQUILLO case,
SEGURO safety), and ONE 21.7 s clip with every move end to end. Same recipe
as tools/import_beretta_anim.py: the arms and the demo man go, the rifle's
armature and clip stay, the import hierarchy is baked into the data, and the
whole thing is put in the game frame -- origin at the PISTOL GRIP, muzzle
along +X, top of the receiver up along +Y (glTF), 880 mm long -- so it drops
in under the pistols' seating. The root bone's keys are dropped: in the
viewmodel it moves with the hands, in the game the hand it is in moves it.

The rifle is 66k triangles as downloaded (the receiver alone is 33k): the
big parts are decimated to about half, which a 90 cm object two metres from
the camera cannot show.
"""
import os
import sys
import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOME = os.path.expanduser("~")
SRC = f"{HOME}/Downloads/akm_fps_animation.glb"
NAME = "akm_anim"
THUMB = "akm"
REAL_LEN = 0.88            # m -- AKM overall length (stock fixed)
GUN_ROOT = "ARMA_044"      # the rifle's root bone; the arms' rig has none
ROOT_BONES = ("_rootJoint", GUN_ROOT)   # animated with the hands: dropped
ARMS = ("ch22", "textura")              # the demo man: anything named like this goes
DECIMATE_OVER = 6000       # polys -- parts bigger than this are halved
DECIMATE_RATIO = 0.45
MAX_TEX = 512

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE' and GUN_ROOT in o.data.bones), None)
if not arm:
    sys.exit(f"gun armature not found: {[(o.name, o.type) for o in bpy.data.objects if o.type == 'ARMATURE']}")
meshes = []
for o in bpy.data.objects:
    if o.type != 'MESH':
        continue
    nm = (o.data.name + ' ' + o.name).lower()
    if any(a in nm for a in ARMS):
        continue
    mod = next((m for m in o.modifiers if m.type == 'ARMATURE'), None)
    if mod and mod.object is arm:
        meshes.append(o)
if not meshes:
    sys.exit(f"no rifle meshes on {arm.name}")
print(f"[{NAME}] armature '{arm.name}' {len(arm.data.bones)} bones; {len(meshes)} parts: "
      + ", ".join(o.data.name.split('_low_')[-1] for o in meshes))

# ---- everything that is not the rifle goes --------------------------------
keep = set(meshes) | {arm}
for o in list(bpy.data.objects):
    if o in keep:
        continue
    if o.type in ('MESH', 'ARMATURE'):
        bpy.data.objects.remove(o, do_unlink=True)

# ---- the heavy parts, halved ----------------------------------------------
for o in meshes:
    if len(o.data.polygons) > DECIMATE_OVER:
        n0 = len(o.data.polygons)
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.select_all(action='DESELECT')
        o.select_set(True)
        d = o.modifiers.new("dec", 'DECIMATE')
        d.ratio = DECIMATE_RATIO
        # the armature modifier must stay LAST, or the decimate sees posed verts
        bpy.ops.object.modifier_move_to_index(modifier="dec", index=0)
        bpy.ops.object.modifier_apply(modifier="dec")
        print(f"[{NAME}]   {o.data.name.split('_low_')[-1]}: {n0} -> {len(o.data.polygons)} polys")

# ---- bake the import hierarchy into the data -----------------------------
bpy.context.view_layer.update()
mw = {o: o.matrix_world.copy() for o in meshes + [arm]}
for o in meshes + [arm]:
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    if o.parent:
        bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
for o in meshes:
    o.data.transform(mw[o])
    o.matrix_world = Matrix.Identity(4)
arm.data.transform(mw[arm])
arm.matrix_world = Matrix.Identity(4)
for o in list(bpy.data.objects):
    if o.type == 'EMPTY':
        bpy.data.objects.remove(o, do_unlink=True)
for o in meshes:
    o.parent = arm
    o.matrix_parent_inverse = Matrix.Identity(4)
    mod = next((m for m in o.modifiers if m.type == 'ARMATURE'), None)
    if mod is None:
        mod = o.modifiers.new("Armature", 'ARMATURE')
    mod.object = arm

# ---- one mesh: the parts keep their materials as slots -------------------
bpy.ops.object.select_all(action='DESELECT')
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
grip_idx = None
# remember which vertices are the pistol grip BEFORE the join (for the origin)
grip_obj = next((o for o in meshes if 'pistol grip' in o.data.name.lower()), None)
if grip_obj:
    vg = grip_obj.vertex_groups.new(name="__grip")
    vg.add(list(range(len(grip_obj.data.vertices))), 1.0, 'REPLACE')
mag_obj = next((o for o in meshes if 'magazine' in o.data.name.lower()), None)
if mag_obj:
    vg = mag_obj.vertex_groups.new(name="__mag")
    vg.add(list(range(len(mag_obj.data.vertices))), 1.0, 'REPLACE')
if len(meshes) > 1:
    bpy.ops.object.join()
mesh = bpy.context.view_layer.objects.active
mesh.parent = arm
mesh.matrix_parent_inverse = Matrix.Identity(4)
mod = next((m for m in mesh.modifiers if m.type == 'ARMATURE'), None)
if mod is None:
    mod = mesh.modifiers.new("Armature", 'ARMATURE')
mod.object = arm


def group_members(ob, name):
    g = ob.vertex_groups.get(name)
    if not g:
        return []
    out = []
    for v in ob.data.vertices:
        for ge in v.groups:
            if ge.group == g.index and ge.weight > 0.5:
                out.append(v.index)
                break
    return out


grip_v = group_members(mesh, "__grip")
mag_v = group_members(mesh, "__mag")

# ---- the game frame, worked out on the rest pose -------------------------
# PCA for the bore: a rifle is long and thin, so the first component IS the
# barrel and the second is the height (magazine + grip + sights, ~25 cm)
# against a ~7 cm width. Expressed as ONE affine transform applied to the mesh
# and to the armature's rest bones -- a proper rotation, never a mirror.
P = np.array([[v.co.x, v.co.y, v.co.z] for v in mesh.data.vertices], dtype=np.float64)
mean = P.mean(axis=0)
Pc = P - mean
vals, vecs = np.linalg.eigh(Pc.T @ Pc)
order = np.argsort(vals)[::-1]
e0, e1, e2 = vecs[:, order[0]], vecs[:, order[1]], vecs[:, order[2]]
if np.dot(np.cross(e0, e1), e2) < 0:
    e2 = -e2
R = np.stack([e0, e1, e2])                       # rows: barrel, grip-to-top, across
Perm = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], dtype=np.float64)   # (x, y, z) -> (x, z, -y)
A = Perm @ R
Q = Pc @ A.T
# WHICH WAY IS UP: the magazine and the pistol grip hang BELOW the bore (the
# "slide runs the whole length" test of the pistols is useless on a rifle: a
# barrel is round and both halves run the whole length). The grip's centre
# must end up below the rifle's median height.
hang = grip_v or mag_v
if hang:
    if Q[hang, 2].mean() > np.median(Q[:, 2]):
        A = np.diag([1.0, -1.0, -1.0]) @ A            # 180 deg about X keeps it handed
        Q = Pc @ A.T
lo = Q.min(axis=0)
hi = Q.max(axis=0)
# the butt: the deep end along the barrel axis (receiver, magazine, grip and
# stock against a bare barrel)
nb = 24
step = (hi[0] - lo[0]) / nb
heights = []
for i in range(nb):
    sel = (Q[:, 0] >= lo[0] + i * step) & (Q[:, 0] <= lo[0] + (i + 1) * step)
    zs = Q[sel, 2]
    heights.append((zs.max() - zs.min()) if zs.size > 3 else 0.0)
butt_at_max_x = sum(heights[-nb // 3:]) > sum(heights[:nb // 3])
# the origin: the pistol grip, by name -- a rifle's "lower back end" is the
# magazine and the toe of the stock, neither of which is where the hand goes
if grip_v:
    G = Q[grip_v]
    g = np.array([G[:, 0].mean(), (lo[1] + hi[1]) / 2, G[:, 2].max() - 0.25 * (G[:, 2].max() - G[:, 2].min())])
else:
    xr = (lo[0] + (hi[0] - lo[0]) * 0.66, hi[0]) if butt_at_max_x else (lo[0], lo[0] + (hi[0] - lo[0]) * 0.34)
    gsel = (Q[:, 0] >= xr[0]) & (Q[:, 0] <= xr[1]) & (Q[:, 2] <= lo[2] + (hi[2] - lo[2]) * 0.45)
    G = Q[gsel] if gsel.any() else Q
    g = np.array([G[:, 0].mean(), (lo[1] + hi[1]) / 2, G[:, 2].mean()])
f = -1.0 if butt_at_max_x else 1.0
sc = REAL_LEN / max(hi[0] - lo[0], 1e-9)
D = np.diag([sc * f, sc * f, sc])                  # muzzle to +X: 180 about Z keeps it handed
A2 = D @ A
t2 = -(A2 @ mean) - D @ g
M = Matrix(((A2[0, 0], A2[0, 1], A2[0, 2], t2[0]),
            (A2[1, 0], A2[1, 1], A2[1, 2], t2[1]),
            (A2[2, 0], A2[2, 1], A2[2, 2], t2[2]),
            (0, 0, 0, 1)))
mesh.data.transform(M)
arm.data.transform(M)
mesh.data.update()
bpy.context.view_layer.update()
for nm in ("__grip", "__mag"):
    vg = mesh.vertex_groups.get(nm)
    if vg:
        mesh.vertex_groups.remove(vg)

# ---- the clip: the parts move, the rifle itself does not ------------------
act = arm.animation_data.action if arm.animation_data else None
if act is None:
    sys.exit("the rifle armature has no action")


def curve_bags(action):
    if hasattr(action, 'fcurves'):
        yield action.fcurves
        return
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                yield bag.fcurves


dropped = kept = 0
for bag in curve_bags(act):
    for fc in list(bag):
        if any(f'pose.bones["{b}"]' in fc.data_path for b in ROOT_BONES):
            bag.remove(fc)
            dropped += 1
        else:
            kept += 1
for pb in arm.pose.bones:
    if pb.name in ROOT_BONES:
        pb.matrix_basis = Matrix.Identity(4)
print(f"[{NAME}] action '{act.name}': {kept} curves kept, {dropped} dropped "
      f"({', '.join(ROOT_BONES)}), {act.frame_range[0]:.0f}..{act.frame_range[1]:.0f} frames "
      f"at {bpy.context.scene.render.fps} fps")
bpy.context.scene.frame_start = int(act.frame_range[0])
bpy.context.scene.frame_end = int(act.frame_range[1])
bpy.context.scene.frame_set(int(act.frame_range[0]))

for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        img.file_format = 'JPEG'

arm.name = NAME
mesh.name = f"{NAME}_mesh"
out = os.path.join(ROOT, "public", "models", f"{NAME}.glb")
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True)
mesh.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_yup=True,
                          use_selection=True, export_apply=True,
                          export_animations=True, export_frame_range=False,
                          export_image_format='JPEG', export_jpeg_quality=85)
pts = [v.co for v in mesh.data.vertices]
d = [max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)]
heads = {b.name: tuple(round(x, 3) for x in b.head_local) for b in arm.data.bones}
tris = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
print(f"[{NAME}] {len(pts)} verts, {tris} tris, {d[0]*1000:.0f} x {d[1]*1000:.0f} x {d[2]*1000:.0f} mm (x len, y across, z up), "
      f"x {min(p.x for p in pts)*1000:.0f}..{max(p.x for p in pts)*1000:.0f}, "
      f"z {min(p.z for p in pts)*1000:.0f}..{max(p.z for p in pts)*1000:.0f}, {os.path.getsize(out)//1024} KB")
print(f"[{NAME}] bone heads: {heads}")


# ---- the thumbnail, for the pickup card -----------------------------------
def render_thumb(ob, name):
    scn = bpy.context.scene
    scn.render.engine = 'BLENDER_EEVEE'
    scn.render.film_transparent = True
    scn.render.resolution_x = scn.render.resolution_y = 256
    scn.render.image_settings.file_format = 'PNG'
    scn.render.image_settings.color_mode = 'RGBA'
    pts = [ob.matrix_world @ v.co for v in ob.data.vertices]
    ctr = sum(pts, Vector()) / len(pts)
    rad = max((p - ctr).length for p in pts)
    cam_data = bpy.data.cameras.new("thumbcam")
    cam = bpy.data.objects.new("thumbcam", cam_data)
    scn.collection.objects.link(cam)
    cam_data.lens = 45
    span = max(p.x for p in pts) - min(p.x for p in pts)
    dist = span / (0.8 * 0.86)
    cam.location = ctr + Vector((0.22, -0.92, 0.32)).normalized() * dist
    direction = (ctr - cam.location).normalized()
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    scn.camera = cam
    for key, loc, energy in (("k", (2, -3, 3), 900), ("f", (-3, -2, 1), 350)):
        lmp = bpy.data.lights.new(f"thumb_{key}", type='AREA')
        lmp.energy = energy
        lmp.size = rad * 6
        lo = bpy.data.objects.new(f"thumb_{key}", lmp)
        lo.location = ctr + Vector(loc) * rad * 2
        lo.rotation_euler = (ctr - lo.location).normalized().to_track_quat('-Z', 'Y').to_euler()
        scn.collection.objects.link(lo)
    out_dir = os.path.join(ROOT, "public", "models", "thumbs")
    os.makedirs(out_dir, exist_ok=True)
    scn.render.filepath = os.path.join(out_dir, f"{name}.png")
    bpy.ops.render.render(write_still=True)
    return scn.render.filepath


thumb = render_thumb(mesh, THUMB)
print(f"[{NAME}] thumb {thumb} {os.path.getsize(thumb)//1024} KB")
