# THE P-47 THUNDERBOLT, REBUILT — ELEMENTS ON THEIR HINGES, WHEELS ON THE FLOOR.
#
#   Blender -b --python tools/build_p47.py
#     -> public/models/planes/p47.glb
#
# The fleet converter (tools/build_planes.py) could not do this one. Two
# reasons, both visible in the game:
#
#   1. "his elements are broken". The Sketchfab source parents every panel to
#      its own empty -- Trup (fuselage), Stabilizator (tailplane), Smerovka
#      (rudder), L/RKridelko (ailerons), LKlapka (flaps), LPodvozek (gear).
#      build_planes selects the lot and calls transform_apply ONCE: Blender
#      bakes each object's own basis into its mesh but leaves the parent
#      inverse standing, so every child came out rotated off its hinge. The
#      tailplane ended up 2.6 m deep (it is a flat plate), the right aileron
#      1.4 m, the flaps on edge.
#      -> flatten the hierarchy FIRST (parent_clear keeps the world pose), bin
#         the empties, and only then apply.
#
#   2. "it not stain on wheels". The source is posed IN FLIGHT: banked 14 deg
#      and with the gear up in the wells. build_planes only ever yaws a model,
#      so the aeroplane stayed banked, and the lowest thing on it was a
#      wingtip -- which is what the hall rested on the floor.
#      -> level it on the AIRFRAME's own geometry (no object in this scene was
#         modelled square to it), swing the two main legs and the tailwheel
#         out, and pitch it onto all three wheels the way a taildragger
#         actually stands.
#
#   3. And while it was open: two of the four propeller blades are modelled
#      pointing along the thrust line, one out of the spinner and one back
#      through the cowling, and the disc is a foot wider than the real one.
#
# The source stores the airframe in a design frame once levelled: X across the
# span, Y up, Z out the nose. The game wants nose +Y, up +Z (glTF export maps
# +Y -> -Z), so the last step turns it 180 deg about (0,1,1).

import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.expanduser("~/Desktop/game/new planes/p-47d22_razorback_thunderbolt.glb")
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(argv[0]) if argv else os.path.join(
    ROOT, "public", "models", "planes", "p47.glb")

SPAN = 12.42        # m — Republic P-47D, wingtip to wingtip
MAX_TEX = 1024

# The part names are Czech: Trup fuselage, Smerovka rudder, Stabilizator
# tailplane, Kridelko aileron, Klapka flap, Podvozek undercarriage.
WING = "Trup_6"          # one panel across the whole span
FUSELAGE = "Trup_0"      # the body, nose to tail
ENGINE = "Motor_0"       # tells the nose line which end is the front
FIN = "Smerovka_0"       # the rudder: above the fuselage, so it fixes "up"
LEFT_GEAR = ["LPodvozek_0", "LPodvozek_1", "Plane.002_0"]      # wheel, leg, door
RIGHT_GEAR = ["LPodvozek.001_0", "LPodvozek.001_1", "Plane.003_0"]
LEG = {"left": "LPodvozek_1", "right": "LPodvozek.001_1"}      # what carries the hinge
TAIL_GEAR = ["Circle.014_0", "Circle.014_1"]
PROP = "Circle_2"        # blades, cuffs and spinner, all in one mesh
PROP_D = 3.96            # m — the real Hamilton Standard on a D-22 (13 ft)
BLADE_CLOCK = 45.0       # deg — park the four in an X, so no blade stands on
                         # the apron: this one's disc is nearly as deep as the
                         # undercarriage is long

GEAR_DEG = 88.0     # main legs, about the fore-aft axis through the outboard hinge
TAIL_DEG = 86.0     # tailwheel, about the span axis through its forward hinge
MAX_PITCH = 9.0     # deg — a real P-47 parks nearer 14, but this is also its
                    # flying pose: the tailwheel leg makes up the difference


# ---- helpers ---------------------------------------------------------------
def meshes():
    return [o for o in bpy.data.objects if o.type == "MESH"]


def xform(objs, M):
    """Move geometry, not objects: every object here sits at identity with its
    vertices in world coordinates, so the pose lives in the mesh data and any
    later measurement is a plain read of it."""
    seen = set()
    for o in objs:
        if o.data.name in seen:
            continue        # shared datablock: transform it once
        seen.add(o.data.name)
        o.data.transform(M)
    bpy.context.view_layer.update()


def bounds(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        for v in o.data.vertices:
            w = o.matrix_world @ v.co
            lo.x, lo.y, lo.z = min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)
            hi.x, hi.y, hi.z = max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)
    return lo, hi


def by_name(names):
    got = {o.name: o for o in meshes()}
    missing = [n for n in names if n not in got]
    if missing:
        raise SystemExit(f"p47: the source has no {missing} — did the model change?")
    return [got[n] for n in names]


def centroid(obj):
    c = Vector((0, 0, 0))
    for v in obj.data.vertices:
        c += obj.matrix_world @ v.co
    return c / len(obj.data.vertices)


def principal(obj):
    """The direction a part is longest in — power iteration on its covariance.
    For the fuselage that is the nose line, for the wing the span."""
    c = centroid(obj)
    cov = [[0.0] * 3 for _ in range(3)]
    for v in obj.data.vertices:
        d = (obj.matrix_world @ v.co) - c
        for i in range(3):
            for j in range(3):
                cov[i][j] += d[i] * d[j]
    axis = Vector((1, 0.3, 0.1)).normalized()
    for _ in range(200):
        nxt = Vector([sum(cov[i][j] * axis[j] for j in range(3)) for i in range(3)])
        if nxt.length < 1e-12:
            break
        axis = nxt.normalized()
    return axis


def loose(name):
    """Cut an object into its separate shells and hand them back."""
    obj = by_name([name])[0]
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.mesh.separate(type="LOOSE")
    return [o for o in meshes() if o.name == name or o.name.startswith(name + ".")]


def straighten_propeller(name, unit):
    """TWO OF THE FOUR BLADES ARE MODELLED OUT OF THE DISC.

    In the source one blade points straight out of the spinner and another
    straight back through the cowling — 2.3 m and 1.8 m along the thrust line
    instead of across it. That is the propeller the game has been showing, and
    it is also what made the aeroplane measure 13 m long.

    Each blade is swung back into the disc about the hub and then spaced on an
    even cross, and every cuff and tip piece travels with the blade it points
    at."""
    parts = loose(name)
    hub = Vector((0, 0, 0))
    n = 0
    for o in parts:
        for v in o.data.vertices:
            hub += o.matrix_world @ v.co
            n += 1
    hub /= n
    f, e1, e2 = Vector((0, 0, 1)), Vector((1, 0, 0)), Vector((0, 1, 0))  # nose, span, up
    clock = lambda v: math.degrees(math.atan2(v.dot(e2), v.dot(e1)))

    # the blades themselves: the big shells, one entry per blade (the model
    # carries each of them twice, one shell per face side)
    dirs = []
    for o in parts:
        d = centroid(o) - hub
        if len(o.data.polygons) < 20 or d.length * unit < 1.0:
            continue
        if any(d.normalized().dot(e.normalized()) > 0.9 for e in dirs):
            continue
        dirs.append(d)

    # step one: into the plane of the disc, keeping the clock angle each blade
    # already had. step two: onto an even cross, because two of them come back
    # 20 deg off the quarter they belong on.
    flat, turns = [], []
    for d in dirs:
        radial = d - f * d.dot(f)
        out = math.degrees(math.atan2(d.dot(f), radial.length))
        axis = radial.normalized().cross(f)
        flat.append((out, axis, Matrix.Rotation(math.radians(-out), 4, axis) @ d))
    for _, _, d in flat:
        turns.append(BLADE_CLOCK + 90 * round((clock(d) - BLADE_CLOCK) / 90) - clock(d))

    for o in parts:
        d = centroid(o) - hub
        if d.length * unit < 0.5:
            continue                     # hub, spinner, pitch gear: all stay
        i = max(range(len(dirs)), key=lambda k: dirs[k].normalized().dot(d.normalized()))
        out, axis, _ = flat[i]
        swing([o], hub, axis, -out)
        swing([o], hub, f, turns[i])
    # AND IT IS A FOOT TOO BIG. The model's disc measures 4.9 m against the
    # real 3.96 m, and on a taildragger that foot comes out of the propeller's
    # ground clearance: parked, the blades reached the apron before the wheels
    # did. Each blade is shortened along its own radius, which leaves the
    # chord, the thickness and the spinner alone.
    disc = max((centroid(o) - hub).length for o in parts) * 2 * unit
    k = PROP_D / disc
    for o in parts:
        d = centroid(o) - hub
        if d.length * unit < 0.5:
            continue
        r = (d - f * d.dot(f)).normalized()
        shrink = Matrix(((1 + (k - 1) * r.x * r.x, (k - 1) * r.x * r.y, (k - 1) * r.x * r.z),
                         ((k - 1) * r.y * r.x, 1 + (k - 1) * r.y * r.y, (k - 1) * r.y * r.z),
                         ((k - 1) * r.z * r.x, (k - 1) * r.z * r.y, 1 + (k - 1) * r.z * r.z))).to_4x4()
        xform([o], Matrix.Translation(hub) @ shrink @ Matrix.Translation(-hub))
    print(f"[p47] propeller: {len(dirs)} blades squared up "
          f"{[round(clock(d)) for _, _, d in flat]} -> "
          f"{[round(clock(d) + t) for (_, _, d), t in zip(flat, turns)]} deg, "
          f"disc {disc:.2f} -> {PROP_D:.2f} m")
    return parts


def seal_propeller(name):
    """Join the shells back into one mesh the game will spin: `propeller_*` is
    what src/planes.js looks for, and its pivot is the hub."""
    parts = [o for o in meshes() if o.name == name or o.name.startswith(name + ".")]
    bpy.ops.object.select_all(action="DESELECT")
    for o in parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    prop = bpy.context.view_layer.objects.active
    prop.name = "propeller_0"
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="MEDIAN")
    b = bounds([prop])
    print(f"[p47] propeller sealed: {(b[1] - b[0]).x:.2f} m across, pivot at "
          f"({prop.location.x:.2f}, {prop.location.y:.2f}, {prop.location.z:.2f})")


def hinge(leg, axis, end):
    """The pivot of a retracted leg: the centroid of the end it hangs from.
    `axis` is the index the leg runs along, `end` which end of it (+1 = the
    far end along that axis)."""
    lo, hi = bounds([leg])
    edge = (hi[axis] - (hi - lo)[axis] * 0.12) if end > 0 \
        else (lo[axis] + (hi - lo)[axis] * 0.12)
    pts = [leg.matrix_world @ v.co for v in leg.data.vertices
           if ((leg.matrix_world @ v.co)[axis] >= edge if end > 0
               else (leg.matrix_world @ v.co)[axis] <= edge)]
    p = Vector((0, 0, 0))
    for q in pts:
        p += q
    return p / len(pts)


def swing(group, pivot, axis, deg):
    M = Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(deg), 4, axis) \
        @ Matrix.Translation(-pivot)
    xform(group, M)


# ---- 1. the source, flattened ----------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)
for act in list(bpy.data.actions):
    bpy.data.actions.remove(act)
for o in [o for o in bpy.data.objects if o.type in ("CAMERA", "LIGHT")]:
    bpy.data.objects.remove(o, do_unlink=True)

# THE FIX FOR THE BROKEN ELEMENTS. Clearing the parents keeps every panel
# exactly where it is but moves its full world pose into its own matrix, so the
# apply below is exact. Applying over the tree instead leaves each child's
# parent inverse behind and scatters the hinged parts.
bpy.ops.object.select_all(action="SELECT")
bpy.context.view_layer.objects.active = list(bpy.data.objects)[0]
bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
for o in [o for o in bpy.data.objects if o.type != "MESH"]:
    bpy.data.objects.remove(o, do_unlink=True)

# every object now carries its own world pose; bake it so the geometry below
# can be read straight off the vertices
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
bpy.context.view_layer.update()

# ---- 2. level it on its own geometry ---------------------------------------
# NOT on any object's rotation: in this scene every panel was modelled in its
# own arbitrary local frame (the wing's own rotation is 40 deg off the
# airframe), so a model levelled on one of them comes out standing on its
# tail. The AIRFRAME says where it is pointing — the fuselage's principal axis
# is the nose line, wingtip to wingtip is the span — and that is bank, pitch
# and yaw in one measurement.
fus = by_name([FUSELAGE])[0]
fwd = principal(fus)
if (centroid(by_name([ENGINE])[0]) - centroid(fus)).dot(fwd) < 0:
    fwd = -fwd                                   # point it at the engine, not the tail
span = principal(by_name([WING])[0])
span = (span - fwd * span.dot(fwd)).normalized() # square to the nose line
up = fwd.cross(span).normalized()
if (centroid(by_name([FIN])[0]) - centroid(fus)).dot(up) < 0:
    span, up = -span, fwd.cross(-span).normalized()   # the fin is above the fuselage
att = Matrix(((span.x, up.x, fwd.x), (span.y, up.y, fwd.y), (span.z, up.z, fwd.z))).to_4x4()
print(f"[p47] source attitude {[round(math.degrees(a)) for a in att.to_euler()]} deg — levelling")
xform(meshes(), att.inverted())

unit = SPAN / (bounds(meshes())[1].x - bounds(meshes())[0].x)   # m per source unit
print(f"[p47] levelled: {unit:.2f} m per unit")

# ---- 2b. the propeller, back in its own disc --------------------------------
straighten_propeller(PROP, unit)

# ---- 3. gear down ----------------------------------------------------------
# Each main leg lies SPANWISE in its well, hinged at the outboard end, wheel
# inboard — so it swings down about the fore-aft axis through that hinge. The
# well door travels with the leg, as a strut-hung door does.
mid_x = (bounds(meshes())[0].x + bounds(meshes())[1].x) / 2
left, right = by_name(LEFT_GEAR), by_name(RIGHT_GEAR)
for group, leg in ((left, LEG["left"]), (right, LEG["right"])):
    leg = by_name([leg])[0]
    # WHICH END IS THE HINGE IS A MEASUREMENT, NOT A CONSTANT: it is the end
    # away from the fuselage, and the leg swings down about it. Hard-coding
    # the sign put the wheels up the legs and the struts on the floor.
    side = 1.0 if centroid(leg).x > mid_x else -1.0
    swing(group, hinge(leg, 0, side), "Z", side * GEAR_DEG)
    # The door hangs off the top of the leg, against the wing: swung with the
    # leg it keeps the station it had over the WHEEL and ends up dangling past
    # the tyre, so it is slid back up the strut to the wing skin.
    door = group[2]
    xform([door], Matrix.Translation((0, hinge(leg, 0, side).y - bounds([door])[1].y, 0)))

# THE TWO LEGS ARE NOT MIRROR IMAGES. One strut is 9 cm longer than the other
# and the hinges sit at different heights, so an equal swing leaves one tyre
# 27 cm in the air -- an aeroplane standing on one wheel and a door. The short
# leg is let down until both tyres are on the same floor; the extra sits at
# the top of the strut, up inside the wheel well where nothing sees it.
floor = min(bounds([left[0]])[0].y, bounds([right[0]])[0].y)
for group in (left, right):
    gap = bounds([group[0]])[0].y - floor
    if gap > 1e-6:
        xform(group, Matrix.Translation((0, -gap, 0)))

# the tailwheel is stowed forward under the fin: it swings down and aft
tail = by_name(TAIL_GEAR)
swing(tail, hinge(tail[0], 2, +1), "X", -TAIL_DEG)

# the floor is where the TYRES are, not where a door or a strut happens to
# reach: measure the wheels alone
tyres = [left[0], right[0]]
wheels_lo = bounds(tyres)[0].y
tail_lo = bounds(tail)[0].y
mainz = (bounds(tyres)[0].z + bounds(tyres)[1].z) / 2
tailz = (bounds(tail)[0].z + bounds(tail)[1].z) / 2
print(f"[p47] track {(bounds(tyres)[1].x - bounds(tyres)[0].x) * unit:.2f} m, "
      f"tailwheel {(tail_lo - wheels_lo) * unit:.2f} m above the tyres")

# ---- 4. stand it on all three ----------------------------------------------
# Nose-up about the main wheels until the tailwheel reaches their floor. The
# geometry asks for about 14 deg -- which is what a real Thunderbolt sits at --
# but this pose is also the pose it FLIES in, and a fighter mushing along 14
# deg nose-high looks wrong in the chase camera. So it parks at MAX_PITCH and
# the tailwheel leg takes up the last few centimetres.
pitch = math.degrees(math.atan2(tail_lo - wheels_lo, mainz - tailz))
clamped = max(-MAX_PITCH, min(MAX_PITCH, pitch))
swing(meshes(), Vector((0, wheels_lo, mainz)), "X", -clamped)
drop = bounds(tail)[0].y - bounds(tyres)[0].y
xform(tail, Matrix.Translation((0, -drop, 0)))
for door in (left[2], right[2]):
    lift = bounds(tyres)[0].y + 0.10 / unit - bounds([door])[0].y
    if lift > 0:
        xform([door], Matrix.Translation((0, lift, 0)))   # a door is not a leg
print(f"[p47] parking attitude {clamped:.1f} deg nose-up"
      f" (geometry asked {pitch:.1f}), tailwheel let down {drop * unit:.2f} m")

# ---- 5. into the game's frame ----------------------------------------------
# design (X span, Y up, Z nose) -> game (X span, Y nose, Z up): a half turn
# about (0,1,1), which is a rotation, not a mirror — the markings stay on the
# sides they were painted on.
M = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
xform(meshes(), M)
lo, hi = bounds(meshes())
xform(meshes(), Matrix.Scale(SPAN / (hi.x - lo.x), 4))
lo, hi = bounds(meshes())
mid = (lo + hi) / 2
xform(meshes(), Matrix.Translation((-mid.x, -mid.y, -lo.z)))   # wheels on z=0
lo, hi = bounds(meshes())
print(f"[p47] {hi.x - lo.x:.2f} m span x {hi.y - lo.y:.2f} m long x {hi.z - lo.z:.2f} m tall")

# ---- 6. ship it ------------------------------------------------------------
seal_propeller(PROP)
for img in bpy.data.images:
    w, h = img.size
    if max(w, h) > MAX_TEX:
        f = MAX_TEX / max(w, h)
        img.scale(int(w * f), int(h * f))

bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.gltf(
    filepath=OUT,
    export_format="GLB",
    use_selection=True,
    export_animations=False,
    export_yup=True,
    export_image_format="WEBP",
    export_image_quality=80,
    export_apply=True,
)
print(f"[p47] wrote {OUT} ({os.path.getsize(OUT) // 1024} KB, {len(meshes())} meshes)")
