# Convert downloaded plane GLBs into game-ready hangar planes.
#
# Sources arrive from Sketchfab/Poly Pizza in random units and orientations;
# the game (src/planes.js) expects real-world meters, nose toward glTF -Z,
# propeller meshes named "propeller*" with the pivot on the spin axis, and
# web-friendly file sizes.
#
# Run:
#   Blender -b --python tools/build_planes.py -- all          # convert
#   Blender -b --python tools/build_planes.py -- sheet        # top-down grid
#   Blender -b --python tools/build_planes.py -- thumbs       # hangar thumbs
#   Blender -b --python tools/build_planes.py -- a10 mig21    # some ids
#
# In Blender the nose is put at +Y (glTF export maps +Y → -Z). The `yaw`
# per plane (degrees, about Z) fixes each source's arbitrary facing — filled
# in by eyeballing the `sheet` render, where every nose must point UP.

import glob
import math
import os
import sys

import bpy
from mathutils import Vector

SRC = os.path.expanduser("~/Desktop/game/new planes")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "public", "models", "planes")
THUMB_DIR = os.path.join(ROOT, "public", "textures", "planes")
SHEET = os.path.join(ROOT, "tools", "cache", "planes_sheet.png")

MAX_TEX = 1024
MAX_TRIS = 120_000

# target = real-world max dimension in meters (max of wingspan/length) —
# scaling the model's max horizontal extent to it works whatever way the
# source happens to face. prop = mesh-name substrings to mark as propellers.
PLANES = {
    "a10":       {"src": "a-10a_thunderbolt_ii_usa.glb", "target": 17.5, "rot": (0, 0, 90)},
    "aerobatic": {"src": "aerobatic_airplane.glb", "target": 8.0, "rot": (0, 0, 90)},
    "an225":     {"src": "antonov_an-225.glb", "target": 88.4},
    "cargobro":  {"src": "battle_royale_plane.glb", "target": 40.4, "rot": (0, 0, 180)},
    "b767":      {"src": "boeing767.glb", "target": 54.9, "rot": (0, 0, 260)},
    "b737":      {"src": "boeing_737-300.glb", "target": 33.4, "rot": (0, 0, 180)},
    "c130":      {"src": "c-130j.glb", "target": 40.4, "prop": ["props"]},
    "cessna310": {"src": "cessna_310_airplane_-_low_poly.glb", "target": 11.2, "rot": (0, 0, -90)},
    "h6k":       {"src": "chinese_h-6k.glb", "target": 34.8},
    "neonfly":   {"src": "competition_jet_-_el_neonfly.glb", "target": 11.9, "rot": (0, 0, 90)},
    "f15":       {"src": "f-15c_msip_ii_with_gbu-8_hobos.glb", "target": 19.4, "rot": (0, 0, 180)},
    "f4f":       {"src": "grumman_f4f_wildcat_airplane.glb", "target": 11.6, "rot": (0, 0, -90)},
    "hsp75":     {"src": "henschel_hs_p_75.glb", "target": 9.1, "rot": (0, 0, -90)},
    "il76":      {"src": "ilyushin_il-76_-_full_interior.glb", "target": 50.5},
    "javelin":   {"src": "javelin_f.a.w._mk.9_airplane.glb", "target": 17.2, "rot": (0, 0, -90)},
    "lancaster": {"src": "lancaster_raf.glb", "target": 31.1,
                  "prop": ["propeller", "propfriction"]},
    "carpet":    {"src": "light_carpet_bomber.glb", "target": 33.5, "rot": (90, 0, 0)},
    "f117":      {"src": "lockheed_martin_f-117_nighthawk.glb", "target": 20.1, "rot": (0, 0, -90)},
    "mig21":     {"src": "mig-21_fishbed_-_cold_war_era_fighter_-_free.glb", "target": 15.8, "rot": (0, 0, -90),
                  # scene holds 3 jets + contrails/glow: keep the parked one
                  "strip": [".001", ".002", ".003", "trails", "glow"]},
    "mig21nk":   {"src": "mig-21pfm_north_korea.glb", "target": 15.8, "rot": (0, 0, 90)},
    "t28":       {"src": "north_american_t-28_trojan.glb", "target": 12.2, "rot": (0, 0, 180),
                  "prop": ["propeller"]},
    "xb35":      {"src": "northrop_xb_35.glb", "target": 52.4, "rot": (0, 0, -90)},
    "p47":       {"src": "p-47d22_razorback_thunderbolt.glb", "target": 12.4, "rot": (0, 0, -90)},
    "p51":       {"src": "p_51_mustang_evo.glb", "target": 11.3},
    # the source scene is a framed display: Object_9/5 are backdrop quads,
    # Object_2 the plinth — strip them, keep the jet
    "su33":      {"src": "su-33_flanker-d.glb", "target": 21.9, "rot": (0, 0, 180),
                  "strip": ["Object_9", "Object_5", "Object_2"]},
    "tornado":   {"src": "tornado.glb", "target": 16.7, "rot": (0, 0, 265)},
    "tu144":     {"src": "tu144.glb", "target": 65.7, "rot": (0, 0, 275)},
    "awacs":     {"src": "usa_air_defense_aircraft_free.glb", "target": 46.6},
    "yc130":     {"src": "yc-130prototype_of_c-130.glb", "target": 40.4},
}


def clean_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def world_box(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        if o.type != "MESH":
            continue
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo.x, lo.y, lo.z = min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)
            hi.x, hi.y, hi.z = max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)
    return lo, hi


def build(pid, cfg):
    clean_scene()
    bpy.ops.import_scene.gltf(filepath=os.path.join(SRC, cfg["src"]))
    # planes are static in-game: imported cameras/lights/animations are noise
    for act in list(bpy.data.actions):
        bpy.data.actions.remove(act)
    for o in [o for o in bpy.data.objects if o.type in ("CAMERA", "LIGHT")]:
        bpy.data.objects.remove(o, do_unlink=True)
    # display plinths, backdrop quads, duplicate aircraft, contrails … strip
    # every object whose name contains a listed substring, with its subtree
    # (children are anonymous "Object_NN" meshes — they only die recursively)
    doomed = set()
    for o in bpy.data.objects:
        if any(key in o.name for key in cfg.get("strip", [])):
            stack = [o]
            while stack:
                cur = stack.pop()
                doomed.add(cur)
                stack.extend(cur.children)
    for o in doomed:
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.context.view_layer.update()

    # one root so scale/yaw apply to the whole hierarchy in a single place
    root = bpy.data.objects.new("plane_root", None)
    bpy.context.scene.collection.objects.link(root)
    for o in bpy.data.objects:
        if o.parent is None and o is not root:
            o.parent = root

    # rot = (rx, ry, rz) degrees: rx/ry right models lying on their side or
    # standing on the nose, rz turns the nose to +Y. Scale AFTER rotating so
    # the max-horizontal-extent rule sees the upright plane.
    rx, ry, rz = cfg.get("rot", (0, 0, 0))
    root.rotation_euler = (math.radians(rx), math.radians(ry), math.radians(rz))
    bpy.context.view_layer.update()
    lo, hi = world_box(bpy.data.objects)
    factor = cfg["target"] / max(hi.x - lo.x, hi.y - lo.y)
    root.scale = (factor,) * 3
    bpy.context.view_layer.update()

    # center on origin, belly at z=0
    lo, hi = world_box(bpy.data.objects)
    mid = (lo + hi) / 2
    root.location = (-mid.x * 1, -mid.y * 1, -lo.z)
    bpy.context.view_layer.update()

    # bake root transform into the children (glTF skinning not involved —
    # these are rigid meshes, applying everywhere is safe and keeps prop
    # pivots meaningful)
    for o in bpy.data.objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # decimate heavyweights so one hangar doesn't ship half a million tris
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons)
               for o in bpy.data.objects if o.type == "MESH")
    if tris > MAX_TRIS:
        ratio = MAX_TRIS / tris
        for o in bpy.data.objects:
            if o.type != "MESH" or len(o.data.polygons) < 1000:
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

    # single-channel images (depth 8/16 grayscale) crash Blender's WEBP
    # writer at export, leaving dangling {"extensions":{}} textures that
    # crash three's GLTFLoader — rebuild them as plain RGBA datablocks.
    remap = {}
    for img in list(bpy.data.images):
        if img.has_data and img.depth in (8, 16):
            clean = bpy.data.images.new(img.name + "_rgba", img.size[0], img.size[1], alpha=True)
            clean.pixels = img.pixels[:]
            remap[img] = clean
    fixed = 0
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        for node in mat.node_tree.nodes:
            if node.type == "TEX_IMAGE" and node.image in remap:
                node.image = remap[node.image]
                fixed += 1
    if fixed:
        print(f"[{pid}] rebuilt {fixed} low-depth textures as RGBA")

    # propellers: rename matching MESHES (lowercase — the game matches
    # startsWith('propeller') case-sensitively, so parent empties named
    # "Propeller0" stay inert) and re-pivot each to its own centroid so
    # rotation.z in glTF spins it about the fuselage axis.
    n = 0
    for key in cfg.get("prop", []):
        for o in bpy.data.objects:
            if o.type == "MESH" and key in o.name.lower() and not o.name.startswith("propeller"):
                bpy.ops.object.select_all(action="DESELECT")
                o.select_set(True)
                bpy.context.view_layer.objects.active = o
                bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="MEDIAN")
                o.name = f"propeller_{n}"
                n += 1

    out = os.path.join(OUT_DIR, f"{pid}.glb")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=True,
        export_animations=False,
        export_yup=True,
        export_image_format="WEBP",
        export_image_quality=80,
        export_apply=True,
    )
    lo, hi = world_box(bpy.data.objects)
    print(f"[{pid}] {hi.x - lo.x:.1f} x {hi.y - lo.y:.1f} m, {tris} tris, "
          f"{n} props, {os.path.getsize(out) // 1024} KB")


# ---- top-down contact sheet: every converted plane, nose must point UP ----
def sheet():
    clean_scene()
    ids = sorted(PLANES)
    cols = 6
    spacing = 110
    for i, pid in enumerate(ids):
        path = os.path.join(OUT_DIR, f"{pid}.glb")
        if not os.path.exists(path):
            continue
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=path)
        cell = Vector(((i % cols) * spacing, -(i // cols) * spacing, 0))
        for o in bpy.data.objects:
            if o not in before and o.parent is None:
                o.location += cell
        txt = bpy.data.curves.new(pid, type="FONT")
        txt.body = pid
        txt.size = 14
        ob = bpy.data.objects.new(pid + "_label", txt)
        ob.location = cell + Vector((-40, 48, 1))
        bpy.context.scene.collection.objects.link(ob)

    rows = (len(ids) + cols - 1) // cols
    cam = bpy.data.cameras.new("cam")
    cam.type = "ORTHO"
    cam.ortho_scale = cols * spacing + 40
    co = bpy.data.objects.new("cam", cam)
    co.location = ((cols - 1) * spacing / 2, -(rows - 1) * spacing / 2, 500)
    bpy.context.scene.collection.objects.link(co)
    bpy.context.scene.camera = co
    _render(SHEET, 2200, int(2200 * rows / cols))


def thumbs(ids):
    for pid in ids:
        path = os.path.join(OUT_DIR, f"{pid}.glb")
        if not os.path.exists(path):
            continue
        clean_scene()
        bpy.ops.import_scene.gltf(filepath=path)
        lo, hi = world_box(bpy.data.objects)
        size = max(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z)
        center = (lo + hi) / 2
        cam = bpy.data.cameras.new("cam")
        co = bpy.data.objects.new("cam", cam)
        # 3/4 view from the front-right, matching the original hangar thumbs
        # (nose is +Y here)
        direction = Vector((0.72, 0.58, 0.38)).normalized()
        co.location = center + direction * size * 1.45
        look = center - co.location
        co.rotation_euler = look.to_track_quat("-Z", "Y").to_euler()
        bpy.context.scene.collection.objects.link(co)
        bpy.context.scene.camera = co
        _render(os.path.join(THUMB_DIR, f"{pid}.png"), 512, 384, alpha=True)
        print(f"[thumb] {pid}")


def _render(path, w, h, alpha=False):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sh = sc.display.shading
    sh.light = "STUDIO"
    sh.color_type = "TEXTURE"
    sc.render.film_transparent = alpha
    sc.render.resolution_x = w
    sc.render.resolution_y = h
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA" if alpha else "RGB"
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("rendered", path)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(os.path.dirname(SHEET), exist_ok=True)
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else ["all"]
    if args == ["sheet"]:
        sheet()
    elif args[0] == "thumbs":
        thumbs(args[1:] or sorted(PLANES))
    else:
        ids = sorted(PLANES) if args == ["all"] else args
        for pid in ids:
            build(pid, PLANES[pid])
        print("done:", ", ".join(ids))


main()
