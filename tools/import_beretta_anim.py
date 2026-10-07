"""The Beretta WITH its moving parts: slide, trigger, hammer and magazine on
their own bones, carrying the Sketchfab clip's animation of them.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/import_beretta_anim.py

tools/import_pistols.py makes the STATIC beretta.glb from the same file
("Beretta Pistol FPS ANIMATION" by BURNER, CC BY 4.0) by baking the rest pose
into one mesh. This keeps the gun's 6-bone armature and the clip instead, so
the slide can cycle on a shot and lock back on the last round, and the
magazine can leave the gun during a reload -- driven in game by the same cut
points src/gunanim.js uses for the arms, so the two always agree.

Same frame, same size, same origin as the static one, so the game can swap
the file in under the existing seating: origin at the grip, muzzle along +X,
slide up along +Y (glTF), 217 mm long. The two Mixamo arms, the demo man's
body and the icospheres are thrown away; the gun's ROOT bone (ARMA_043) loses
its keys, because in the viewmodel it is animated with the hands -- the gun
in the game is placed by the hand it is in, not by the clip.
"""
import os
import sys
import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOME = os.path.expanduser("~")
SRC = f"{HOME}/Downloads/beretta_pistol_fps_animation.glb"
NAME = "beretta_anim"
REAL_LEN = 0.217           # m -- Beretta 92FS overall length
MESH_NODE = "Object_62"    # the gun mesh; the arms are Ch22_Body
GUN_ARM = "Armature.001"   # the gun's armature; "Armature" is the arms'
ROOT_BONES = ("_rootJoint", "ARMA_043")   # animated with the hands: dropped
MAX_TEX = 512

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

# The importer names objects after glTF NODES, and "Armature.001" in the file
# is a plain node (an empty here); the armature object it makes for the gun's
# skin is called whatever it likes. Find both by what they contain.
arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE' and 'ARMA_043' in o.data.bones), None)
mesh = next((o for o in bpy.data.objects if o.type == 'MESH' and o.data.name.startswith('hammer_beretta')), None)
if not mesh or not arm:
    sys.exit(f"gun not found: {[(o.name, o.type, getattr(o.data, 'name', None)) for o in bpy.data.objects]}")
print(f"[{NAME}] gun mesh '{mesh.name}' ({mesh.data.name}), armature '{arm.name}' "
      f"with {len(arm.data.bones)} bones, parent chain "
      f"{[p.name for p in [mesh.parent, getattr(mesh.parent, 'parent', None)] if p]}")

# ---- everything that is not the gun goes --------------------------------
keep = {mesh, arm}
for o in list(bpy.data.objects):
    if o in keep:
        continue
    if o.type in ('MESH', 'ARMATURE'):
        bpy.data.objects.remove(o, do_unlink=True)
# the gun must keep ITS armature's action; the arms' action is gone with them

# ---- bake the import hierarchy into the data -----------------------------
# The gun sits under Sketchfab_model (rotated), pistolanim.fbx (scaled 0.01)
# and the armature (scaled 100). The world matrices are folded into the mesh
# vertices and the armature's rest bones, and both objects sit at identity --
# bone keys are bone-local, so the clip is untouched by any of this.
bpy.context.view_layer.update()
mw_arm = arm.matrix_world.copy()
mw_mesh = mesh.matrix_world.copy()
for o in (mesh, arm):
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    if o.parent:
        bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
mesh.data.transform(mw_mesh)
arm.data.transform(mw_arm)
mesh.matrix_world = Matrix.Identity(4)
arm.matrix_world = Matrix.Identity(4)
for o in list(bpy.data.objects):
    if o.type == 'EMPTY':
        bpy.data.objects.remove(o, do_unlink=True)
mesh.parent = arm
mesh.matrix_parent_inverse = Matrix.Identity(4)
mod = next((m for m in mesh.modifiers if m.type == 'ARMATURE'), None)
if mod is None:
    mod = mesh.modifiers.new("Armature", 'ARMATURE')
mod.object = arm

# ---- the game frame, worked out on the rest pose -------------------------
# Same recipe as import_pistols.py (PCA for the bore, the deep end is the
# butt, the long half is the slide), expressed as ONE affine transform so it
# can be applied to the armature's rest bones as well as to the mesh. One
# difference: the axis shuffle is a proper rotation here, not a swap -- a
# mirrored armature would play its hammer backwards.
P = np.array([[v.co.x, v.co.y, v.co.z] for v in mesh.data.vertices], dtype=np.float64)
mean = P.mean(axis=0)
Pc = P - mean
vals, vecs = np.linalg.eigh(Pc.T @ Pc)
order = np.argsort(vals)[::-1]
e0, e1, e2 = vecs[:, order[0]], vecs[:, order[1]], vecs[:, order[2]]
if np.dot(np.cross(e0, e1), e2) < 0:
    e2 = -e2
R = np.stack([e0, e1, e2])                       # rows: barrel, grip-to-slide, across
# barrel -> X, grip-to-slide -> Z (glTF up), across -> -Y (a rotation, det +1)
Perm = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], dtype=np.float64) @ np.eye(3)
Perm = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], dtype=np.float64)   # (x, y, z) -> (x, z, -y)
A = Perm @ R
Q = Pc @ A.T
# which half is the slide: the one that runs the whole length
zmid = (Q[:, 2].min() + Q[:, 2].max()) / 2
def span_x(sel):
    xs = Q[sel, 0]
    return (xs.max() - xs.min()) if xs.size > 3 else 0.0
if span_x(Q[:, 2] > zmid) < span_x(Q[:, 2] < zmid):
    flipX = np.diag([1.0, -1.0, -1.0])            # 180 deg about X
    A = flipX @ A
    Q = Pc @ A.T
lo = Q.min(axis=0)
hi = Q.max(axis=0)
# the butt: the deep end along the barrel axis
nb = 24
step = (hi[0] - lo[0]) / nb
heights = []
for i in range(nb):
    sel = (Q[:, 0] >= lo[0] + i * step) & (Q[:, 0] <= lo[0] + (i + 1) * step)
    zs = Q[sel, 2]
    heights.append((zs.max() - zs.min()) if zs.size > 3 else 0.0)
butt_at_max_x = sum(heights[-nb // 3:]) > sum(heights[:nb // 3])
xr = (lo[0] + (hi[0] - lo[0]) * 0.66, hi[0]) if butt_at_max_x else (lo[0], lo[0] + (hi[0] - lo[0]) * 0.34)
gsel = (Q[:, 0] >= xr[0]) & (Q[:, 0] <= xr[1]) & (Q[:, 2] <= lo[2] + (hi[2] - lo[2]) * 0.45)
G = Q[gsel] if gsel.any() else Q
g = np.array([G[:, 0].mean(), (lo[1] + hi[1]) / 2, G[:, 2].mean()])
f = -1.0 if butt_at_max_x else 1.0
sc = REAL_LEN / max(hi[0] - lo[0], 1e-9)
D = np.diag([sc * f, sc * f, sc])                  # muzzle to +X: 180 about Z keeps it handed
# v' = D (A (v - mean) - g)  =  (D A) v + (-(D A) mean - D g)
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

# ---- the clip: the parts move, the gun itself does not --------------------
act = arm.animation_data.action if arm.animation_data else None
if act is None:
    sys.exit("the gun armature has no action")
# Blender 4.4+ keeps curves in layered actions (layer -> strip -> channelbag);
# older ones on the action itself. Walk whichever this build has.
def curve_bags(action):
    if hasattr(action, 'fcurves'):
        yield action.fcurves
        return
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                yield bag.fcurves
dropped = 0
kept = 0
for bag in curve_bags(act):
    for fc in list(bag):
        if any(f'pose.bones["{b}"]' in fc.data_path for b in ROOT_BONES):
            bag.remove(fc)
            dropped += 1
        else:
            kept += 1
# the root bones sit at their rest pose for good
for pb in arm.pose.bones:
    if pb.name in ROOT_BONES:
        pb.matrix_basis = Matrix.Identity(4)
print(f"[{NAME}] action '{act.name}': {kept} curves kept, {dropped} dropped "
      f"({', '.join(ROOT_BONES)}), {act.frame_range[0]:.0f}..{act.frame_range[1]:.0f} frames "
      f"at {bpy.context.scene.render.fps} fps")
bpy.context.scene.frame_start = int(act.frame_range[0])
bpy.context.scene.frame_end = int(act.frame_range[1])

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
print(f"[{NAME}] {len(pts)} verts, {d[0]*1000:.0f} x {d[1]*1000:.0f} x {d[2]*1000:.0f} mm (x len, y across, z up), "
      f"x {min(p.x for p in pts)*1000:.0f}..{max(p.x for p in pts)*1000:.0f}, "
      f"z {min(p.z for p in pts)*1000:.0f}..{max(p.z for p in pts)*1000:.0f}, {os.path.getsize(out)//1024} KB")
print(f"[{NAME}] bone heads: {heads}")
