"""
Render a quick 3/4 view of any GLB so a model can be judged without opening
Blender. Reusable for the whole building programme.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/_preview_glb.py -- \
      public/models/panel_block.glb /tmp/shot.png [--elev 18] [--azim 35] [--res 1600]

Sun + sky only: the point is to read SILHOUETTE and the shadow in the window
reveals, which is exactly what a flat studio light would hide.
"""

import bpy
import math
import os
import sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
GLB = os.path.abspath(argv[0]) if argv else ""
OUT = os.path.abspath(argv[1]) if len(argv) > 1 else "/tmp/preview.png"


def flag(name, default):
    return float(argv[argv.index(name) + 1]) if name in argv else default


ELEV = flag("--elev", 16.0)     # degrees above the horizon — eye level-ish
AZIM = flag("--azim", 35.0)     # degrees around, 0 = straight on the long face
RES = int(flag("--res", 1600))
SAMPLES = int(flag("--samples", 48))

# ---- empty the scene (this file is only ever run headless on its own) -------
for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)

bpy.ops.import_scene.gltf(filepath=GLB)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
if not meshes:
    raise SystemExit(f"no meshes in {GLB}")

# ---- bounds, so the framing works for any model -----------------------------
corners = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
lo = Vector((min(c[i] for c in corners) for i in range(3)))
hi = Vector((max(c[i] for c in corners) for i in range(3)))
centre = (lo + hi) / 2
size = hi - lo
radius = max(size) * 0.62

# ---- camera -----------------------------------------------------------------
cam_data = bpy.data.cameras.new("Cam")
cam_data.lens = 50          # a normal lens; wide angles flatter architecture
cam = bpy.data.objects.new("Cam", cam_data)
bpy.context.scene.collection.objects.link(cam)

e, a = math.radians(ELEV), math.radians(AZIM)
dist = radius / math.tan(math.radians(14))
cam.location = centre + Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e),
                                math.sin(e))) * dist
# aim slightly below centre: buildings are photographed from the street
target = centre - Vector((0, 0, size.z * 0.12))
direction = target - cam.location
cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
bpy.context.scene.camera = cam

# ---- late-morning sun, the same angle the game uses -------------------------
sun_data = bpy.data.lights.new("Sun", type="SUN")
sun_data.energy = 4.0
sun_data.angle = math.radians(1.5)      # a tight sun = crisp reveal shadows
sun = bpy.data.objects.new("Sun", sun_data)
bpy.context.scene.collection.objects.link(sun)
sun.rotation_euler = (math.radians(48), 0, math.radians(135 + AZIM))

world = bpy.data.worlds.new("W")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.52, 0.62, 0.74, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 1.1
bpy.context.scene.world = world

# ground plane so the block casts a shadow and does not float
gm = bpy.data.meshes.new("Ground")
gm.from_pydata([(-500, -500, lo.z), (500, -500, lo.z), (500, 500, lo.z), (-500, 500, lo.z)],
               [], [(0, 1, 2, 3)])
gm.update()
ground = bpy.data.objects.new("Ground", gm)
bpy.context.scene.collection.objects.link(ground)
gmat = bpy.data.materials.new("GroundMat")
gmat.use_nodes = True
gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.21, 0.2, 0.19, 1)
gmat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
ground.data.materials.append(gmat)

# ---- render ------------------------------------------------------------------
scene = bpy.context.scene
for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE", "CYCLES"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        continue
if scene.render.engine == "CYCLES":
    scene.cycles.samples = SAMPLES
else:
    try:
        scene.eevee.taa_render_samples = SAMPLES
        scene.eevee.use_raytracing = True      # needed for real contact shadows
    except AttributeError:
        pass

scene.render.resolution_x = RES
scene.render.resolution_y = int(RES * 0.62)
scene.render.film_transparent = False
scene.view_settings.view_transform = "Filmic" if "Filmic" in [
    v.name for v in scene.view_settings.bl_rna.properties["view_transform"].enum_items
] else "Standard"
scene.render.filepath = OUT
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.render.render(write_still=True)

tris = 0
for o in meshes:
    o.data.calc_loop_triangles()
    tris += len(o.data.loop_triangles)
print(f"rendered {GLB} -> {OUT}  ({tris:,} tris, {size.x:.1f} x {size.y:.1f} x {size.z:.1f} m)")
