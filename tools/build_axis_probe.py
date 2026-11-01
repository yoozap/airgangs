"""An asymmetric, chiral axis marker for the Unity port.

The game frame is RIGHT-handed (X=west, Y=up, Z=north); Unity is LEFT-handed.
Whatever importer we use (glTFast) picks its own RH->LH convention, and the
generated city must agree with it or props and buildings disagree by a mirror.

This asset makes the convention readable at a glance:
  - three arms of different LENGTHS so the axes can't be confused (X=4, Y=3, Z=2)
  - a flat "F" lying in the XZ plane: an F is chiral, so if the importer
    mirrored us the F reads BACKWARDS. Length alone can't show that.

  blender -b --python tools/build_axis_probe.py -- out.glb
"""
import bpy, sys

out = sys.argv[sys.argv.index("--") + 1:][0]
bpy.ops.wm.read_factory_settings(use_empty=True)

def box(name, size, loc, rgb):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    m = bpy.data.materials.new(name + "_mat")
    m.use_nodes = True
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*rgb, 1)
    o.data.materials.append(m)
    return o

# arms: distinct lengths, distinct colours
box("AXIS_X_pos_4m", (4, .2, .2), (2, 0, 0), (1, .1, .1))   # +X
box("AXIS_Y_pos_3m", (.2, 3, .2), (0, 1.5, 0), (.1, 1, .1))  # +Y
box("AXIS_Z_pos_2m", (.2, .2, 2), (0, 0, 1), (.2, .3, 1))    # +Z

# a chiral "F" flat in the XZ plane (reads backwards under a mirror)
box("F_stem",    (.25, .1, 2.0), (-3.0, 0, 1.0), (1, 1, .2))
box("F_top_arm", (1.2,  .1, .25), (-2.4, 0, 1.9), (1, 1, .2))
box("F_mid_arm", (0.8,  .1, .25), (-2.6, 0, 1.0), (1, 1, .2))

bpy.ops.export_scene.gltf(filepath=out, export_format='GLB',
                          export_apply=True, export_yup=True)
print("PROBE wrote " + out)
