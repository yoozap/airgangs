"""GLB -> FBX for the Unity port.

Unity's humanoid Avatar auto-mapper wants an FBX skeleton; glTFast can read the
static props straight from GLB, so ONLY the humanoid bodies come through here.
The roster is 4 bodies (see src/characters.js RACES[].bodies) and the elephant
is not humanoid, so this is a 3-file job.

  blender -b --python tools/glb_to_fbx.py -- out_dir body [body...]
"""
import bpy, sys, os

argv = sys.argv[sys.argv.index("--") + 1:]
out_dir, bodies = argv[0], argv[1:]
os.makedirs(out_dir, exist_ok=True)

for body in bodies:
    src = "public/models/personages/%s.glb" % body
    dst = os.path.join(out_dir, "%s.fbx" % body)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=src)

    arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE'), None)
    bones = len(arm.data.bones) if arm else 0

    bpy.ops.export_scene.fbx(
        filepath=dst,
        apply_unit_scale=True,      # 1 blender unit = 1 m = 1 unity unit
        axis_forward='-Z', axis_up='Y',
        add_leaf_bones=False,       # leaf bones confuse Unity's avatar mapper
        bake_anim=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=True,
        path_mode='COPY', embed_textures=True,
    )
    print("FBX %-12s bones=%-3d actions=%-2d -> %s" % (
        body, bones, len(bpy.data.actions), dst))
