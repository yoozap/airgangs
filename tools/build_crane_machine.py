"""Bring the C780 mobile crane into the warehouse — by LUFFING ITS BOOM DOWN.

  Blender -b -P tools/build_crane_machine.py -- <source.glb> [--deg 26] [--tris 90000]

The model (Sketchfab "crane machine", a FORWARD C780 rough-terrain crane) is
modelled at real size with the boom run out and raised to 47 degrees: 10.4 m of
carrier and 35.6 m of boom, standing 31 m tall. The warehouse's barrel vault
gives 14.7 m on the centreline. Shrinking the whole machine until it fits turns
a 10.4 m carrier into a 4.5 m one, which reads as a toy beside an 11.3 m
Mustang — so instead the boom comes DOWN, to the angle a crane actually works
at indoors, and the machine stays close to its real size.

That is four separate jobs, because the boom is not one rigid thing:

  THE BOOM swings about its foot pin. Everything on it — the sections, the
  head, the sheaves — goes with it.
  THE LUFFING RAM does not. Its base pin is on the superstructure and stays
  where it is; its rod end is on the boom and moves. So the ram is re-aimed at
  where that attachment ended up and scaled along its own axis to reach it,
  which is what a ram does: lowering the boom RETRACTS it (6.7 m to 5.6 m here).
  THE ROPES hang. They are modelled as single meshes that run along the boom
  AND fall vertically from the head, so they cannot be rotated as either one;
  they are dropped and the falls rebuilt as straight cylinders.
  THE HOOK BLOCK hangs too. It is carried to directly under the new head, at
  the height the game wants to hang an aeroplane from.

Everything is then joined per material group — 379 meshes is 379 draw calls —
decimated to a budget and the textures capped, the same as tools/import_prop.py
does for a prop that is heavy the other way round.

Blender axes after a glTF import: x is along the boom's reach (it reaches toward
-x), z is up, y is across. So the boom luffs about the +Y axis.
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not args:
    sys.exit("usage: -- <source.glb> [--deg 26] [--tris 90000] [--max-tex 1024]")
SRC = os.path.abspath(args[0])


def flag(name, default):
    return type(default)(args[args.index(name) + 1]) if name in args else default


# Where the boom ends up, in degrees above horizontal. 26 is not a style
# choice: src/crane.js scales whatever it loads until the top of the machine is
# TOP (13.6 m) under the shed's arch, so the boom angle is what decides how big
# the carrier comes out. At 26 degrees this stands 19.9 m, so the game's scale
# lands at 0.68 and the carrier finishes about 7.1 m long — a believable
# mid-size crane — with roughly 22 m of hook reach down the shed.
DEG = flag("--deg", 26.0)
TRIS = flag("--tris", 90000)
MAX_TEX = flag("--max-tex", 1024)
# How high the hook block hangs above the GROUND, in model metres. The machine
# goes in at REAL SIZE now (the user chose a real crane with its boom laid down
# over a scaled-down one with it raised), so this is metres in the shed: enough
# rope out of the head that the slung Mustang's wheels clear a man's head and
# no more. --hook moves it.
HOOK_UP = flag("--hook", 8.5)
NAME = "crane_machine"
OUT = os.path.join(ROOT, "public", "models", f"{NAME}.glb")

# The luffing ram, by name. Measured off the source (its pieces sit below the
# boom heel, between the boom and the deck) — there is no group for it, it is
# mixed in with the boom under `hand`, so the one honest way to name it is to
# name it. Barrel, rod, base clevis and the two pins.
RAM = {"pCylinder210", "pCube43", "pCube42", "pCylinder212", "pCylinder239",
       "pCylinder441", "pCube142", "pCube143"}

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)
bpy.context.view_layer.update()


def meshes():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def world_box(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


def group_of(o):
    """The named subtree a mesh hangs under — `hand` is the whole boom."""
    n = o
    while n.parent is not None:
        if n.name.split(".")[0] in GROUPS:
            return n.name.split(".")[0]
        n = n.parent
    return "root"


GROUPS = {"glass", "tyre", "rim1", "interior", "hydraulic", "base", "body1",
          "lader", "hand", "rotating", "hydrolic_lift", "cabin1", "other", "bottom"}

GROUND = world_box(meshes())[0].z
hand = [o for o in meshes() if group_of(o) == "hand"]
if not hand:
    sys.exit("no `hand` subtree — this is not the C780 source")

# ---- sort the boom subtree out ---------------------------------------------
# ROPE: a mesh that is centimetres thick in its NARROWEST direction and metres
# long. Two things make that the test rather than a bounding box:
#   * the ropes that run along the boom lie on a diagonal, so their box is
#     twenty-odd metres in TWO axes and they read as bulky. They were being
#     kept and swung with the boom, which put the end of one nine metres
#     underground and left the model "35 m tall".
#   * a rope mesh here is not a line. pCylinder236 carries the run UP the boom
#     and the fall back DOWN from the head in one mesh, so it is a flat sheet:
#     its second spread is 6.7 m. Only the narrowest direction tells you what
#     it is — 0.04 m against 0.14 m for the thinnest thing on the boom itself.
# HOOK: the block and its hook are the only things hanging far out at the
# boom's reach and LOW DOWN; everything else out there is up at the head.
def spread(o):
    """Standard deviation of a mesh's vertices along its own three axes."""
    m = o.matrix_world
    p = np.array([(m @ v.co)[:] for v in o.data.vertices], dtype=float)
    if len(p) < 4:
        return (0.0, 0.0, 0.0)
    p -= p.mean(axis=0)
    return tuple(np.sqrt(np.maximum(0, np.linalg.eigvalsh(np.cov(p, rowvar=False)))))


blo_pin_z = world_box(hand)[0].z + 2.0   # about deck height: see the sweep below
rope, hook, ram, boom = [], [], [], []
for o in hand:
    lo, hi = world_box([o])
    c = (lo + hi) / 2
    sp = spread(o)   # ascending: two tiny ones and a long one is a cable
    # the glTF importer names a mesh object after the node that HOLDS the mesh,
    # which is "<part>_hand__0" — the part name is what RAM lists
    if o.name.split(".")[0].split("_hand__")[0] in RAM:
        ram.append(o)
    elif sp[0] < 0.10 and sp[2] > 1.5:
        rope.append(o)
    elif c.x < -18 and hi.z < 5:
        hook.append(o)
    else:
        boom.append(o)
print(f"[{NAME}] boom {len(boom)} meshes, ram {len(ram)}, rope {len(rope)}, hook {len(hook)}")

# ---- the foot pin, and the axis the boom sits on ----------------------------
# Taken off the boom itself rather than typed in: project every boom vertex on
# the boom's own direction and average the most heel-ward 2 per cent, which is
# the centre of the heel, not the corner of its bounding box.
blo, bhi = world_box(boom)
head = Vector((blo.x, 0.0, bhi.z))                 # out and up: the boom head
axis = Vector((blo.x - bhi.x, 0.0, bhi.z - blo.z)).normalized()
pts = []
for o in boom:
    m = o.matrix_world
    for v in o.data.vertices:
        w = m @ v.co
        pts.append((Vector((w.x, 0.0, w.z)).dot(axis), Vector((w.x, 0.0, w.z))))
pts.sort(key=lambda p: p[0])
heel = pts[:max(1, len(pts) // 50)]
PIN = Vector((0.0, 0.0, 0.0))
for _, p in heel:
    PIN += p
PIN /= len(heel)
# ...and the pin sits on the axis a little in from the heel face, not on it
PIN = PIN - axis * 0.0
was = math.degrees(math.atan2(-axis.z, axis.x) + math.pi) if axis.x < 0 else 0.0
was = math.degrees(math.asin(abs(axis.z)))
phi = math.radians(DEG) - math.radians(was)         # how far it has to come down
print(f"[{NAME}] boom pin at x {PIN.x:.2f} z {PIN.z:.2f} ({PIN.z - GROUND:.2f} m "
      f"over the ground), boom {was:.1f} deg -> {DEG:.1f} deg "
      f"({math.degrees(phi):+.1f} deg)")

# ...AND THE BOOM IS NOT ONLY `hand`. Bolts and a cable from the `other` group
# are bolted to the boom head, 26 m up. Left behind they held the model at 26 m
# and hung in mid-air over the lowered boom. Anything that SITS ON THE BOOM --
# out past the pin and close to the boom's own line -- goes with it, whatever
# group it was filed under; everything down on the carrier is untouched.
strays, stray_rope = [], []
for o in meshes():
    if o in hand:
        continue
    lo2, hi2 = world_box([o])
    c = (lo2 + hi2) / 2
    d = Vector((c.x - PIN.x, 0.0, c.z - PIN.z))
    along = d.dot(axis)
    off = (d - axis * along).length
    if along > 1.0 and off < 2.5:
        (stray_rope if (spread(o)[0] < 0.10 and spread(o)[2] > 1.5) else strays).append(o)
boom += strays
rope += stray_rope
print(f"[{NAME}] +{len(strays)} meshes and {len(stray_rope)} cables found bolted to the "
      f"boom outside its own subtree")

# ---- TELESCOPING: THE BOOM COMES IN BEFORE IT COMES DOWN --------------------
# "u need to make leg of crane more shorter and car will be more near to plane"
# -- the carrier stands as far back from the load as the boom is long, so the
# only way to park the machine beside the aeroplane is to pull the boom in.
#
# This is a telescopic boom and it is modelled as one: four nested box sections
# (11.6, 10.3, 9.7 and 10.3 m here), each running out about 8.4 m past the one
# inside it. So retracting is not scaling -- it is sliding sections 2, 3 and 4
# back down the axis until their far ends line up with the base section's,
# which is what the real thing does and what keeps the head, the sheaves and
# every fitting the right SIZE. --ext 1 is the model as it came (fully run
# out), 0 is fully retracted.
EXT = flag("--ext", 1.0)


def along(o):
    """A mesh's extent along the boom's axis, measured from the foot pin."""
    m = o.matrix_world
    ds = [Vector(((m @ v.co).x - PIN.x, 0.0, (m @ v.co).z - PIN.z)).dot(axis)
          for v in o.data.vertices]
    return (min(ds), max(ds)) if ds else (0.0, 0.0)


def off_axis(o):
    """...and how far off that line it sits, so a cable is not taken for a section."""
    m = o.matrix_world
    worst = 0.0
    for v in o.data.vertices:
        d = Vector(((m @ v.co).x - PIN.x, 0.0, (m @ v.co).z - PIN.z))
        worst = max(worst, (d - axis * d.dot(axis)).length)
    return worst


span = {o: along(o) for o in boom}
# THE SECTIONS are the long box beams: metres along the axis and wide enough to
# be structure rather than a hose. Sorted by their FAR end, they are the base
# section and the ones run out of it.
sections = sorted([o for o in boom
                   if span[o][1] - span[o][0] > 5.0 and off_axis(o) > 0.25],
                  key=lambda o: span[o][1])
if len(sections) < 2 or EXT >= 0.999:
    if EXT < 0.999:
        print(f"[{NAME}] no telescopic sections found — boom left as modelled")
else:
    far0 = span[sections[0]][1]
    shift = {}
    for k, sec in enumerate(sections):
        shift[k] = -(1.0 - EXT) * (span[sec][1] - far0)
    # Every other piece on the boom belongs to a section and rides with it: the
    # OUTERMOST section whose own span contains its middle (the sections
    # overlap, and a fitting in an overlap is bolted to the inner one -- the
    # one that moves). Anything past the last section's far end is head gear.
    groups = {k: [] for k in shift}
    for o in boom:
        c = (span[o][0] + span[o][1]) / 2
        k = 0
        for i, sec in enumerate(sections):
            lo2, hi2 = span[sec]
            if lo2 - 0.3 <= c <= hi2 + 0.3:
                k = i
        if c > span[sections[-1]][1]:
            k = len(sections) - 1
        groups[k].append(o)
    for k, objs in groups.items():
        if abs(shift[k]) < 1e-4:
            continue
        M = Matrix.Translation(axis * shift[k])
        for o in objs:                       # (put() is defined further down)
            if o.parent in objs:
                continue
            o.matrix_world = M @ o.matrix_world
    bpy.context.view_layer.update()
    span = {o: along(o) for o in boom}
    lens = ", ".join(f"{span[s][1] - span[s][0]:.1f}" for s in sections)
    print(f"[{NAME}] boom retracted to ext {EXT:.2f}: {len(sections)} sections ({lens} m), "
          f"now {max(span[o][1] for o in boom):.1f} m from the pin, "
          f"base section ends at {far0:.1f} m")

# Rotating a boom DOWN in this frame is a NEGATIVE turn about +Y: the reach is
# -x, so dropping the head decreases z while x goes further out. `phi` is
# already negative when the boom is being lowered, so it is used as it stands —
# negating it here raised the boom to 68 degrees and 37 m instead.
R = Matrix.Rotation(phi, 4, "Y")
SWING = Matrix.Translation(PIN) @ R @ Matrix.Translation(-PIN)


def put(objs, M):
    for o in objs:
        if o.parent in objs:
            continue            # the parent carries it
        o.matrix_world = M @ o.matrix_world


# the ram's rod end rides on the boom, so work out where it lands BEFORE moving
rlo, rhi = world_box(ram) if ram else (Vector(), Vector())
if ram:
    # base pin on the superstructure (low, near the deck) and rod end on the
    # boom (high) — the ram's own axis, in the boom's plane
    base = Vector((0.0, 0.0, 0.0))
    tip = Vector((0.0, 0.0, 0.0))
    rpts = []
    for o in ram:
        m = o.matrix_world
        for v in o.data.vertices:
            w = m @ v.co
            rpts.append(Vector((w.x, 0.0, w.z)))
    rpts.sort(key=lambda p: p.z)
    low = rpts[:max(1, len(rpts) // 20)]
    high = rpts[-max(1, len(rpts) // 20):]
    for p in low:
        base += p
    base /= len(low)
    for p in high:
        tip += p
    tip /= len(high)
    tip2 = SWING @ tip                       # where the boom takes it
    a0 = tip - base
    a1 = tip2 - base
    q = a0.rotation_difference(a1)
    grow = a1.length / max(1e-6, a0.length)
    # turn it onto the new line, then stretch it along that line to reach
    FIT = (Matrix.Translation(base)
           @ q.to_matrix().to_4x4()
           @ Matrix.Scale(grow, 4, a0.normalized())
           @ Matrix.Translation(-base))
    print(f"[{NAME}] luffing ram {a0.length:.2f} m -> {a1.length:.2f} m "
          f"({'retracts' if grow < 1 else 'extends'} {abs(1 - grow) * 100:.0f}%)")

put(boom, SWING)
if ram:
    put(ram, FIT)

# ---- the hook, and new ropes to it ------------------------------------------
# THE HEAD IS THE SHEAVES, not the top of the boom's bounding box. Measured at
# the far END of the boom — the highest vertices within a metre of its reach —
# because something bolted further back sits two metres proud of the axis, and
# hanging the falls off that ran them up through the head and out into the air.
bpy.context.view_layer.update()
nlo, nhi = world_box(boom)
tops = []
for o in boom:
    m = o.matrix_world
    for v in o.data.vertices:
        w = m @ v.co
        if w.x < nlo.x + 1.0:
            tops.append(w)
tops.sort(key=lambda w: -w.z)
HEAD = Vector((0.0, 0.0, 0.0))
for w in tops[:max(1, len(tops) // 5)]:
    HEAD += w
HEAD /= max(1, len(tops) // 5)
HEAD.y = 0.0
HEAD.z -= 0.35                                    # the rope leaves under the sheave
print(f"[{NAME}] boom head sheaves at x {HEAD.x:.2f} z {HEAD.z:.2f} "
      f"({HEAD.z - GROUND:.2f} m up)")

# The model hangs TWO things off the head: the main block, and a headache ball
# on the whip line below it. They are moved separately — carried under the head
# and hung at the heights the game wants — because the rope that used to join
# them has just been deleted, and moved as one lump the ball ends up floating
# with nothing above it.
hook.sort(key=lambda o: -world_box([o])[1].z)
block = hook[:1]
ball = hook[1:]
drops = []


def hang(objs, dx, up):
    """Carry a hanging thing under the head, bottom `up` metres off the ground."""
    if not objs:
        return None
    lo2, hi2 = world_box(objs)
    now = Vector(((lo2.x + hi2.x) / 2, (lo2.y + hi2.y) / 2, lo2.z))
    want = Vector((HEAD.x + dx, (lo2.y + hi2.y) / 2, GROUND + up))
    put(objs, Matrix.Translation(want - now))
    bpy.context.view_layer.update()
    lo2, hi2 = world_box(objs)
    return Vector(((lo2.x + hi2.x) / 2, 0.0, hi2.z))   # the top, to rope to


BLOCK_TOP = hang(block, 0.0, HOOK_UP)
drops.append((BLOCK_TOP, (-0.16, -0.055, 0.055, 0.16)))
# the point a load hangs from: the BOTTOM of the block, where the hook is
BLOCK = None
if block:
    _lo, _hi = world_box(block)
    BLOCK = Vector(((_lo.x + _hi.x) / 2, (_lo.y + _hi.y) / 2, _lo.z))
drops.append((hang(ball, 1.5, HOOK_UP - 2.2), (0.0,)))
print(f"[{NAME}] block hung {HOOK_UP:.2f} m up, ball {HOOK_UP - 2.2:.2f} m")

for o in rope:
    bpy.data.objects.remove(o, do_unlink=True)

# Straight falls between the head and each of them — a four-part reeve on the
# main block, a single line on the whip.
mat = boom[0].data.materials[0] if boom[0].data.materials else None
n = 0
for top, lanes in drops:
    if top is None:
        continue
    for dy in lanes:
        a = Vector((top.x, dy, HEAD.z))
        c = Vector((top.x, dy, top.z))
        d = c - a
        if d.length < 0.2:
            continue
        bpy.ops.mesh.primitive_cylinder_add(vertices=6, radius=0.035, depth=d.length,
                                            location=(a + c) / 2)
        cyl = bpy.context.active_object
        cyl.name = f"hoist_fall_{n}"
        n += 1
        cyl.rotation_mode = "QUATERNION"
        cyl.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
        if mat:
            cyl.data.materials.append(mat)
print(f"[{NAME}] {n} rope falls rebuilt")

# ---- weight ------------------------------------------------------------------
shrunk = []
for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        was_sz = tuple(img.size)
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        shrunk.append(f"{was_sz[0]}x{was_sz[1]}")


def count():
    n = 0
    for o in meshes():
        o.data.calc_loop_triangles()
        n += len(o.data.loop_triangles)
    return n


tris0 = count()
ms = meshes()
bpy.ops.object.select_all(action="DESELECT")
for o in ms:
    o.select_set(True)
bpy.context.view_layer.objects.active = ms[0]
# transforms baked before the join, or every piece parented to its own empty
# collapses onto the origin
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
bpy.ops.object.join()
one = bpy.context.view_layer.objects.active
one.name = NAME
if tris0 > TRIS:
    mod = one.modifiers.new("decimate", "DECIMATE")
    mod.ratio = TRIS / tris0
    bpy.ops.object.modifier_apply(modifier=mod.name)

# WHERE THE GAME HANGS THINGS FROM, written into the model rather than left to
# be guessed at. src/crane.js used to find a hook by looking for the lowest
# thing over the outer third of the jib, which on this machine finds the
# HEADACHE BALL on the whip line, not the main block. The build knows exactly
# where the block is, so it says so: glTF is Y-up, Blender is Z-up, hence the
# swap. three's GLTFLoader hands node extras back as object.userData.
if BLOCK is not None:
    one["hook"] = [BLOCK.x, BLOCK.z, -BLOCK.y]
    print(f"[{NAME}] hook written into the model at glTF "
          f"({BLOCK.x:.2f}, {BLOCK.z:.2f}, {-BLOCK.y:.2f})")

lo, hi = world_box([one])
print(f"[{NAME}] {count()} tris (from {tris0}), {len(one.data.materials)} materials, "
      f"textures capped: {len(shrunk)}")
print(f"[{NAME}] stands {hi.z - lo.z:.2f} m, {hi.x - lo.x:.2f} m long, {hi.y - lo.y:.2f} m wide")

os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.object.select_all(action="SELECT")
# export_extras: WITHOUT IT THE HOOK NEVER LEAVES BLENDER. The exporter drops
# custom properties by default, so `one["hook"]` above was written, printed,
# and then quietly thrown away -- src/crane.js found no userData.hook, fell
# back to its geometric search (which is written for the harbour crane's +Z
# jib) and hung the Mustang through the floor beside the carrier.
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True,
                          export_image_format="WEBP", export_apply=True,
                          export_extras=True)
print(f"[{NAME}] {os.path.getsize(SRC) // 1024} KB -> {os.path.getsize(OUT) // 1024} KB  {OUT}")
