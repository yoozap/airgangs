"""Batch GLB -> FBX for the Unity port's art bridge.

WHY FBX AND NOT glTFast (decided 2026-10-26, from evidence):
  1. Every model GLB from the Three.js pipeline declares EXT_texture_webp in
     extensionsRequired. glTFast does not implement it, so it refused all 144
     files, importing 0 subassets — indistinguishable from a missing asset.
  2. Converting the textures to PNG fixed that, and then glTFast 6.20.0
     SEGFAULTED the editor (exit 139) inside its own Burst job
     GLTFast.Jobs.ConvertUVsFloatToFloatInterleavedJob (Jobs.cs:853), reliably,
     on Assets/Art/Planes/p47.glb.
  3. Blender reads these files fine (it is the pipeline that made them, Draco
     included) and Unity's NATIVE ModelImporter handles FBX robustly — proven by
     banana.fbx (62 subassets) and spitfire.obj (105).

So Blender is the bridge. Reads the staged GLB, writes FBX where Unity wants it.

  blender -b --python tools/glb_batch_fbx.py -- <src_root> <dst_root>
"""
import bpy, sys, os, glob

argv = sys.argv[sys.argv.index("--") + 1:]
src_root, dst_root = argv[0], argv[1]

files = sorted(glob.glob(os.path.join(src_root, "**", "*.glb"), recursive=True))
print("BATCH files=%d" % len(files))

ok = fail = 0
for src in files:
    rel = os.path.relpath(src, src_root)
    dst = os.path.join(dst_root, os.path.splitext(rel)[0] + ".fbx")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=src)
        # Blender 5.x's glTF importer gives every imported armature a 2 m
        # "Icosphere" as its bones' custom display shape and LINKS it into the
        # scene; the FBX export then ships that sphere inside every rigged
        # asset (every personage carried one — the "grey faceted blob" in the
        # hero preview, the 2 m sphere the AKM was measured against). The GLB
        # files themselves never contain it. Drop it before exporting.
        for arm in [o for o in bpy.data.objects if o.type == 'ARMATURE']:
            for pb in arm.pose.bones:
                pb.custom_shape = None
        junk = [o for o in bpy.data.objects if o.type == 'MESH' and o.name.startswith('Icosphere')]
        for o in junk:
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
        if junk:
            print("BATCH stripped importer bone shapes: %d" % len(junk))
        meshes = [o for o in bpy.data.objects if o.type == 'MESH']
        arms   = [o for o in bpy.data.objects if o.type == 'ARMATURE']
        if not meshes and not arms:
            print("BATCH empty %s" % rel)
        bpy.ops.export_scene.fbx(
            filepath=dst,
            apply_unit_scale=True,
            axis_forward='-Z', axis_up='Y',
            add_leaf_bones=False,
            bake_anim=True, bake_anim_use_all_actions=True,
            bake_anim_use_nla_strips=False,
            path_mode='COPY', embed_textures=True,
            use_mesh_modifiers=True,
        )
        print("BATCH ok %-42s meshes=%-3d arms=%d acts=%d" % (
            rel, len(meshes), len(arms), len(bpy.data.actions)))
        ok += 1
    except Exception as e:
        print("BATCH FAIL %s :: %s" % (rel, e))
        fail += 1

print("BATCH done ok=%d fail=%d" % (ok, fail))
