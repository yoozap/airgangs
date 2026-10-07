# THE INSIDE OF EACH AEROPLANE, CUT FROM THAT AEROPLANE.
#
#   Blender -b --python tools/build_plane_interiors.py
#   Blender -b --python tools/build_plane_interiors.py -- aerobatic spitfire
#     -> public/models/planes/<id>_cockpit.glb
#
# Four attempts at an interior failed before this one, and every one of them
# failed the same way: it was a shape I INVENTED (a box, a lofted trough, a
# region of hull copied at runtime) sitting inside a shape the aeroplane
# already had. "see plane body, i dont need u make just box, make it real with
# blender man."
#
# So: take the plane. Find its canopy. Cut the slab of fuselage around it out
# of the real airframe, push that skin inward by its own thickness and turn it
# to face in. What comes out is the cockpit of THAT machine -- every curve of
# it, because it is its own hull one skin deep -- and nothing can stick out of
# the aeroplane, because every vertex started on the hull and moved inwards.
#
# FRAME: the interior is exported in the FILE's own frame, so the game can add
# it straight into the cached model where `yaw`/`roll`, the centring, the
# scale and the belly drop all reach it. Planes whose file is stored nose-east
# or tail-first are rotated upright to be measured and rotated back to be
# written.

import json
import math
import os
import re
import sys

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "public", "models", "planes")

SKIN = 0.055        # m — how far in from the hull the interior wall sits
SIDE_MARGIN = 1.15  # of the fuselage half-width at the cockpit
LEN_MARGIN = 1.05   # of the canopy's own length
DEPTH = 1.00        # m — how far below the canopy the cockpit is cut
MAX_FACES = 1800    # a cockpit seen through glass does not need more
GLASS_RX = re.compile(r"glass|canopy|cocpit|cockpit|kabina|cabina|windscreen|window"
                      r"|vidrio|cristal|staklo|hood|^cam$|^cam[0-9_]|_cam$", re.I)
INTERIOR_RGB = (0.075, 0.090, 0.062, 1.0)


def planes_from_source():
    """id, file and the orientation fix, read off src/planes.js so this cannot
    drift from the game."""
    txt = open(os.path.join(ROOT, "src", "planes.js"), encoding="utf-8").read()
    out = []
    # BOUND EACH DEF AT THE NEXT ONE. A fixed-size window ran off the end of
    # the short one-line defs and picked up the NEXT aeroplane's `yaw`, which
    # rotated the model before it was measured and put the cockpit box
    # somewhere the aeroplane was not.
    starts = [(m.group(1), m.end()) for m in re.finditer(r"id:\s*'([a-z0-9]+)'", txt)]
    for i, (pid, pos) in enumerate(starts):
        end = starts[i + 1][1] if i + 1 < len(starts) else len(txt)
        chunk = txt[pos:end]
        f = re.search(r"file:\s*'([^']+)'", chunk)
        if not f or not f.group(1).endswith(".glb"):
            continue

        def ang(key):
            g = re.search(key + r":\s*(-?[0-9.]+|-?Math\.PI\s*/\s*2|Math\.PI|-?Math\.PI)", chunk)
            if not g:
                return 0.0
            s = g.group(1)
            if "Math.PI" in s:
                v = math.pi
                if s.startswith("-"):
                    v = -v
                if "/" in s:
                    v /= 2
                return v
            return float(s)
        out.append({"id": pid, "file": os.path.join(ROOT, "public", f.group(1).lstrip("/")),
                    "yaw": ang("yaw"), "roll": ang("roll")})
    return out


def clean():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def is_glass(mat):
    if mat is None:
        return False
    if GLASS_RX.search(mat.name or ""):
        return True
    # NOT blend_method: in Blender 5 it no longer means what it did, and every
    # material on the aerobatic read as glass through it — the cowling won the
    # "canopy" on volume and the cockpit box landed on the engine.
    if mat.use_nodes:
        for n in mat.node_tree.nodes:
            if n.type == "BSDF_PRINCIPLED":
                a = n.inputs.get("Alpha")
                if a is not None and not a.is_linked and a.default_value < 0.95:
                    return True
    return False


def world_box(objs):
    lo = Vector((1e9,) * 3)
    hi = Vector((-1e9,) * 3)
    for o in objs:
        if o.type != "MESH":
            continue
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
    return lo, hi


def upright(objs, yaw, roll):
    """Put the nose at +Y. The game's fix is a three.js Y-rotation (yaw) and a
    Z-roll; after a glTF import those are a Blender Z-rotation and a Blender
    -Y rotation. Returns the empty everything hangs off, so it can be undone."""
    root = bpy.data.objects.new("fix", None)
    bpy.context.scene.collection.objects.link(root)
    for o in objs:
        if o.parent is None:
            o.parent = root
    root.rotation_euler = (0.0, -roll, yaw)
    bpy.context.view_layer.update()
    return root


def find_canopy(meshes, span, length, air_vol):
    """The glass, by material or by name — and SMALL. Half these files call a
    mesh glass when it is the whole fuselage skin, and a transparency test is
    just as generous, so the same sanity the game applies is applied here:
    narrow, centred, a fraction of the aeroplane, and not most of its
    length."""
    best = None
    for o in meshes:
        named = bool(GLASS_RX.search(o.name or "")) or any(
            GLASS_RX.search(ms.material.name or "") for ms in o.material_slots if ms.material)
        glassy = named or any(is_glass(ms.material) for ms in o.material_slots)
        if not glassy:
            continue
        lo, hi = world_box([o])
        size = hi - lo
        c = (lo + hi) / 2
        vol = size.x * size.y * size.z
        if size.x > span * 0.30 or abs(c.x) > span * 0.25:
            continue
        if size.y > length * 0.45 or vol > air_vol * 0.06:
            continue
        if os.environ.get("DEBUG_INTERIOR"):
            print(f"  [dbg] canopy candidate {o.name} vol={vol:.3f} size="
                  f"{tuple(round(v, 2) for v in size)}")
        # A NAMED canopy beats one found only by being see-through: the name
        # is the model's own word for it, the transparency is a guess.
        rank = (1 if named else 0, vol)
        if best is None or rank > best[0]:
            best = (rank, lo.copy(), hi.copy())
    return None if best is None else (best[1], best[2])


def guess_cockpit(meshes, lo, hi):
    """No glass named anywhere in the file — most of these models. The nose is
    +Y by now, so the cockpit is the first place behind it where the hull has
    risen to its full forward height: the greenhouse on a fighter, the flight
    deck on an airliner. Capped at 45% back, because nothing's cockpit is in
    its tail and the FIN is on the centreline too."""
    span = hi.x - lo.x
    length = hi.y - lo.y
    band = max(0.25, span * 0.06)
    bins = 48
    top = [-1e9] * bins
    for o in meshes:
        mw = o.matrix_world
        for v in o.data.vertices:
            p = mw @ v.co
            if abs(p.x) > band:
                continue
            f = (hi.y - p.y) / length          # 0 at the nose, 1 at the tail
            if f < 0.05 or f > 0.45:
                continue
            b = min(bins - 1, int((f - 0.05) / 0.40 * bins))
            top[b] = max(top[b], p.z)
    best = max(top)
    if best < -1e8:
        return None
    hit = next((i for i, t in enumerate(top) if t >= best * 0.92), 0)
    f = 0.05 + (hit + 0.5) / bins * 0.40
    y = hi.y - f * length
    return (Vector((-band, y - 0.55, best - 0.9)), Vector((band, y + 0.55, best)))


def fuselage_half_width(meshes, y0, y1, span):
    """How far out the hull stands over the cockpit's length, ignoring the
    wing: the widest point on the centreline band."""
    w = 0.0
    band = max(0.25, span * 0.10)
    for o in meshes:
        mw = o.matrix_world
        for v in o.data.vertices:
            p = mw @ v.co
            if p.y < y0 or p.y > y1 or abs(p.x) > band:
                continue
            w = max(w, abs(p.x))
    return w


def carve_interior(meshes, lo, hi, name, axis_z):
    """Copy the hull, keep only what is in the cockpit box, drop the glass,
    push the skin in along its own normals and turn it to face inward."""
    bpy.ops.object.select_all(action="DESELECT")
    copies = []
    for o in meshes:
        d = o.copy()
        d.data = o.data.copy()
        bpy.context.scene.collection.objects.link(d)
        # UNPARENT, KEEPING THE WORLD TRANSFORM. The glTF importer hangs
        # everything off an empty of its own (rotated +90 deg X), and the
        # orientation fix adds another above that — so a copy that keeps its
        # parents has mesh data in some local space while the cockpit box was
        # measured in world space, and nothing is ever "inside" it.
        d.parent = None
        d.matrix_world = o.matrix_world.copy()
        copies.append(d)
    for d in copies:
        d.select_set(True)
    bpy.context.view_layer.objects.active = copies[0]
    if len(copies) > 1:
        bpy.ops.object.join()
    shell = bpy.context.object
    shell.name = name
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # The whole airframe, in world space, to test the result against: a face of
    # the finished interior has to have hull OUTSIDE it, or it is not an
    # interior. This is the check that cannot be fooled, and it is what caught
    # the Su-33, the Tornado, the Kawasaki and the H6K, where the cockpit was
    # measured somewhere the fuselage is not.
    hull = BVHTree.FromPolygons(
        [v.co.copy() for v in shell.data.vertices],
        [list(p.vertices) for p in shell.data.polygons], all_triangles=False)

    bm = bmesh.new()
    bm.from_mesh(shell.data)
    bm.faces.ensure_lookup_table()
    glass_slots = {i for i, ms in enumerate(shell.material_slots) if is_glass(ms.material)}
    doomed = []
    for f in bm.faces:
        c = f.calc_center_median()
        inside = (lo.x <= c.x <= hi.x and lo.y <= c.y <= hi.y and lo.z <= c.z <= hi.z)
        if not inside or f.material_index in glass_slots:
            doomed.append(f)
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    kept = len(bm.faces)
    if kept:
        # INWARD IS TOWARD THE FUSELAGE AXIS, not along the vertex normal.
        # Plenty of these files have their normals flipped somewhere, and on
        # those the offset pushed the interior OUT through the skin — green
        # patches on the Su-33's spine and the Tornado's nose. The axis runs
        # down the length at x=0, so "in" is never ambiguous.
        for v in bm.verts:
            to_axis = Vector((0.0, v.co.y, axis_z)) - v.co
            if to_axis.length > 1e-5:
                v.co += to_axis.normalized() * SKIN
        bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
        # and now throw away anything with no aeroplane outside it
        bm.faces.ensure_lookup_table()
        loose = []
        for f in bm.faces:
            c = f.calc_center_median()
            out = c - Vector((0.0, c.y, axis_z))
            if out.length < 1e-4:
                continue
            hit = hull.ray_cast(c, out.normalized(), 4.0)
            if hit[0] is None:
                loose.append(f)
        if loose and len(loose) < len(bm.faces):
            bmesh.ops.delete(bm, geom=loose, context="FACES")
        kept = len(bm.faces)
    bm.to_mesh(shell.data)
    bm.free()
    if not kept:
        bpy.data.objects.remove(shell, do_unlink=True)
        return None, 0

    # LIGHT. These are looked at through a canopy; a 14 000-face copy of an
    # F-15's cockpit skin is 766 KB of download for something the size of a
    # thumbnail on screen.
    if kept > MAX_FACES:
        mod = shell.modifiers.new("dec", "DECIMATE")
        mod.ratio = MAX_FACES / kept
        bpy.context.view_layer.objects.active = shell
        bpy.ops.object.modifier_apply(modifier="dec")
        kept = len(shell.data.polygons)

    shell.data.materials.clear()
    m = bpy.data.materials.new("cockpit_interior")
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = INTERIOR_RGB
    b.inputs["Roughness"].default_value = 0.93
    b.inputs["Metallic"].default_value = 0.05
    shell.data.materials.append(m)
    for p in shell.data.polygons:
        p.use_smooth = True
    return shell, kept


def build(p):
    clean()
    if not os.path.exists(p["file"]):
        return f'{p["id"]}: no file'
    bpy.ops.import_scene.gltf(filepath=p["file"])
    for act in list(bpy.data.actions):
        bpy.data.actions.remove(act)
    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    if not meshes:
        return f'{p["id"]}: no meshes'
    fix = upright(meshes, p["yaw"], p["roll"])
    bpy.context.view_layer.update()
    lo, hi = world_box(meshes)
    span = hi.x - lo.x
    length = hi.y - lo.y
    air_vol = max(1e-6, span * length * (hi.z - lo.z))
    can = find_canopy(meshes, span, length, air_vol)
    if can is None:
        can = guess_cockpit(meshes, lo, hi)
    if can is None:
        return f'{p["id"]}: no canopy found — skipped'
    clo, chi = can
    cy = (clo.y + chi.y) / 2
    half_len = max(0.45, (chi.y - clo.y) / 2 * LEN_MARGIN)
    w = fuselage_half_width(meshes, cy - half_len, cy + half_len, span)
    if w < 0.1:
        return f'{p["id"]}: no fuselage at the cockpit — skipped'
    box_lo = Vector((-w * SIDE_MARGIN, cy - half_len, chi.z - DEPTH))
    # STOP BELOW THE CANOPY. Cutting up to the glass line takes the canopy's
    # own opaque FRAME with it, and that is a raised box standing above the
    # spine — pushed 5 cm inward it is still a raised box, which is what
    # showed on the Su-33, the Tornado and the Kawasaki. The cockpit walls
    # that matter are the ones below the sill; above it there is glass.
    box_hi = Vector((w * SIDE_MARGIN, cy + half_len, clo.z + (chi.z - clo.z) * 0.45))

    # everything is measured in the upright frame; bake that into the copies,
    # carve, then put the result back into the FILE's frame for export
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
    bpy.context.view_layer.update()

    if os.environ.get("DEBUG_INTERIOR"):
        print(f'  [dbg] {p["id"]} airframe lo={tuple(round(v,2) for v in lo)} '
              f'hi={tuple(round(v,2) for v in hi)}')
        print(f'  [dbg] canopy lo={tuple(round(v,2) for v in clo)} hi={tuple(round(v,2) for v in chi)}')
        print(f'  [dbg] w={w:.2f} box lo={tuple(round(v,2) for v in box_lo)} '
              f'hi={tuple(round(v,2) for v in box_hi)}')
    # the fuselage's own centreline height, a little under the canopy
    axis_z = (clo.z + chi.z) / 2 - (chi.z - clo.z) * 0.9
    shell, tris = carve_interior(meshes, box_lo, box_hi, f'{p["id"]}_cockpit', axis_z)
    if shell is None:
        return f'{p["id"]}: nothing inside the cockpit box — skipped'
    shell.parent = None
    shell.rotation_euler = (0.0, p["roll"], -p["yaw"])   # back to the file's frame
    bpy.ops.object.select_all(action="DESELECT")
    shell.select_set(True)
    bpy.context.view_layer.objects.active = shell
    bpy.ops.object.transform_apply(rotation=True)

    out = os.path.join(SRC, f'{p["id"]}_cockpit.glb')
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_yup=True,
                              use_selection=True, export_apply=True)
    kb = os.path.getsize(out) // 1024
    return f'{p["id"]}: {tris} faces, {kb} KB  (cockpit at y={cy:.2f}, half-width {w:.2f} m)'


only = [a for a in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else []
rows = []
for p in planes_from_source():
    if only and p["id"] not in only:
        continue
    try:
        rows.append(build(p))
    except Exception as e:  # one bad model must not stop the batch
        rows.append(f'{p["id"]}: FAILED {e}')
print("\n==== INTERIORS ====")
for r in rows:
    print("  " + r)
