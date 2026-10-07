"""Bring the downloaded belt in as a wearable, with a holster the game can
park a pistol in.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/import_belt.py

As downloaded it is unusable:
  * 26 MB of 4K textures (268 MB of VRAM) for a prop the size of a hand span
  * authored at ~5.5x life size, on its own axes, origin nowhere near the ring
  * no socket, so nothing knows where a pistol is supposed to sit

What comes out is a 0.33 m belt in the GAME FRAME -- ring in XZ, buckle
forward (-Z), holster on the wearer's right (+X), origin at the centre of the
ring -- carrying an empty called `holster` whose axes are the pose a pistol
takes when it is in it. src/weapons.js parents the weapon straight to that
empty, so the game never has to guess an offset.

The holster is FOUND, not hard-coded: the two slabs hanging off the band are
located by clustering everything that hangs below the strap, and the deeper of
the two is the holster. If the model is ever swapped for another belt, this
still finds it.
"""
import glob
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOME = os.path.expanduser("~")

# The NEWEST belt in Downloads, not a fixed name: a better model arrives as
# "belt (1).glb" and renaming it by hand is a step that gets forgotten.
# Override with BELT_SRC=/path/to/whatever.glb.
SRC = os.environ.get("BELT_SRC") or max(
    glob.glob(os.path.join(HOME, "Downloads", "belt*.glb")),
    key=os.path.getmtime, default=f"{HOME}/Downloads/belt.glb")
NAME = "belt"
WAIST_W = 0.33    # m across the hips -- a 95 cm belt, the size it leaves here.
                  # src/foot.js rescales it to each body's real hips anyway.
SUBDIV = 1        # simple subdivisions of the strap: 4 quads across the back
                  # is not enough to bend smoothly round a body
MAX_TEX = 2048   # it rides at hip height on a camera 2 m away: 1K showed


def one_mesh():
    """Everything joined, parents cleared, transforms baked."""
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    if not meshes:
        sys.exit("no mesh in the belt file")
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    # The glTF importer parents everything under an empty rotated +90 deg X to
    # get Y-up into Z-up. transform_apply only bakes the object's OWN matrix, so
    # that parent rotation survives and every axis measurement below would be
    # taken in the wrong frame.
    if ob.parent:
        bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return ob


def ring_and_hangers(ob):
    """Split the belt into the STRAP and the things hanging off it.

    The strap is the band of verts around the median height; anything well
    below that is hanging -- a holster, a pouch, a keeper. Returns the ring's
    centre and half-widths plus the hanging clusters, grouped by where they sit
    around the ring.
    """
    P = [v.co.copy() for v in ob.data.vertices]
    zs = sorted(p.z for p in P)
    zmed = zs[len(zs) // 2]
    band = [p for p in P if abs(p.z - zmed) < 0.10]
    hang = [p for p in P if p.z < zmed - 0.14]
    cx = (min(p.x for p in band) + max(p.x for p in band)) / 2
    cy = (min(p.y for p in band) + max(p.y for p in band)) / 2
    ring = dict(cx=cx, cy=cy, cz=zmed,
                hx=(max(p.x for p in band) - min(p.x for p in band)) / 2,
                hy=(max(p.y for p in band) - min(p.y for p in band)) / 2,
                top=max(p.z for p in band), bot=min(p.z for p in band))
    # group the hanging verts by azimuth around the ring
    hs = sorted(hang, key=lambda p: math.atan2(p.y - cy, p.x - cx))
    groups = []
    for p in hs:
        if groups:
            q = groups[-1][-1]
            if abs(math.atan2(p.y - cy, p.x - cx) - math.atan2(q.y - cy, q.x - cx)) > 0.35:
                groups.append([])
        else:
            groups.append([])
        groups[-1].append(p)
    out = []
    for g in groups:
        if len(g) < 40:        # a buckle keeper, not a pouch
            continue
        ctr = sum(g, Vector()) / len(g)
        lo = Vector((min(p.x for p in g), min(p.y for p in g), min(p.z for p in g)))
        hi = Vector((max(p.x for p in g), max(p.y for p in g), max(p.z for p in g)))
        out.append(dict(pts=g, ctr=ctr, lo=lo, hi=hi,
                        az=math.atan2(ctr.y - cy, ctr.x - cx),
                        drop=ring['bot'] - lo.z,
                        depth=hi.y - lo.y))
    return ring, out


# How a pistol hangs in a duty holster: muzzle essentially straight down, with
# the butt canted a little FORWARD so the grip clears the ribs. 10 deg is what
# a plain belt rig looks like; the FBI cant is the other way and reads as a
# cross-draw, which is not what is wanted here.
RAKE = math.radians(10)


def holster_frame(cl, ring):
    """The pose a pistol takes while it is in this holster.

    The pistols come out of import_pistols.py with muzzle along +X, slide-top
    along +Y and the weapon's right side along +Z. Belt frame here is Blender's
    with the strap already spun: +X the wearer's right, +Y forward (glTF -Z
    after the Y-up export), +Z up.

    How a pistol sits in a strong-side holster:
        muzzle    DOWN, canted RAKE forward -- so the grip tips back into the
                  hand that reaches for it. (The FBI cant is the other way and
                  reads as a cross-draw.)
        slide top FORWARD. The grip hangs off the opposite face of the frame
                  from the slide, so top-forward is what puts the grip BACK.
                  The first version had the top pointing OUTWARD, which stood
                  the slide perpendicular to the hip with the grip driven into
                  the ribs -- the flat of the gun showed from the front.
        right     +X, i.e. outward on the right hip. The flat of the weapon
                  lies against the body; on the left hip the same frame puts
                  its right face against the body, which is a mirrored holster
                  and looks the same from outside.
    `cl` and `ring` are unused now: the pose is the same on every hanger.
    """
    fwd = Vector((0.0, 1.0, 0.0))
    up = Vector((0.0, 0.0, 1.0))
    muzzle = (-up * math.cos(RAKE) + fwd * math.sin(RAKE)).normalized()
    top = (fwd * math.cos(RAKE) + up * math.sin(RAKE)).normalized()
    side = muzzle.cross(top).normalized()      # = +X
    # Columns are the empty's LOCAL axes -- and the Y-up export remaps those:
    # Blender local Z becomes glTF local Y, Blender local Y becomes glTF local
    # -Z. The pistol seats on the glTF axes (+X muzzle, +Y top, +Z right), so
    # the columns have to be laid out as (muzzle, -right, top) HERE for the
    # game to read (muzzle, top, right). Verified by composing the exported
    # node's quaternion: a frame written as (muzzle, top, right) came out
    # twisted a quarter turn about the barrel.
    return Matrix((
        (muzzle.x, -side.x, top.x),
        (muzzle.y, -side.y, top.y),
        (muzzle.z, -side.z, top.z),
    ))


def shrink_textures():
    for img in bpy.data.images:
        if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
            img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))


def render_thumb(ob, name):
    """Three-quarter view on transparency, for the pickup card -- same
    treatment the pistols get, so the loot prompts match."""
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
    cam.location = ctr + Vector((0.45, -1.0, 0.55)).normalized() * (rad * 2 / (0.8 * 0.8))
    cam.rotation_euler = (ctr - cam.location).normalized().to_track_quat('-Z', 'Y').to_euler()
    scn.camera = cam
    for key, loc, energy in (("k", (2, -3, 3), 700), ("f", (-3, -2, 1), 260)):
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


bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)
ob = one_mesh()

# DENSIFY THE STRAP BEFORE ANYTHING RESHAPES IT.
#
# The download is 2.6k triangles and most of them are in the buckle: the band
# itself is a handful of quads, four of them across the whole back. That is
# fine for a belt that is never bent, and this one gets bent twice -- rounded
# here, then conformed to a body at runtime -- and a strap pushed outward four
# quads at a time reads as a hexagon, not a belt.
#
# SIMPLE subdivision, not Catmull-Clark: it adds edge loops without moving the
# surface, so the holsters keep their shape and the only thing that changes is
# how finely the leather can follow a body.
sub = ob.modifiers.new("dense", 'SUBSURF')
sub.subdivision_type = 'SIMPLE'
sub.levels = sub.render_levels = SUBDIV
bpy.context.view_layer.objects.active = ob
bpy.ops.object.modifier_apply(modifier=sub.name)
print(f"[belt] densified x{4 ** SUBDIV}: {len(ob.data.vertices)} verts")

ring, hangers = ring_and_hangers(ob)
print(f"ring centre ({ring['cx']:.3f},{ring['cy']:.3f},{ring['cz']:.3f}) "
      f"half-widths {ring['hx']:.3f} x {ring['hy']:.3f}")
for h in hangers:
    print(f"  hanger az={math.degrees(h['az']):+7.1f}deg drop={h['drop']:.3f} "
          f"depth={h['depth']:.3f} n={len(h['pts'])}")
if not hangers:
    sys.exit("nothing hangs off this belt -- no holster to find")

def roundify(ob, ring):
    """Make the strap a CIRCLE, and carry whatever hangs off it along.

    The downloaded belt is a strong oval -- its strap runs about 0.10 from the
    centre at the buckle and 0.17 at the back.  No body in the game is that
    shape, so worn it either cuts through the belly (fitted to the back) or
    hangs off the back in mid-air (fitted to the front); there is no uniform
    scale that puts the whole strap outside a round waist.

    So the band is pushed out onto one radius.  Every vertex -- band, buckle,
    holster, pouch -- moves radially by however far the BAND at its own azimuth
    had to move, so the hangers keep their shape and stay welded to the strap
    rather than being stretched with it.
    """
    c = Vector((ring['cx'], ring['cy']))
    N = 72
    prof = [0.0] * N
    for v in ob.data.vertices:
        if abs(v.co.z - ring['cz']) > 0.10:
            continue
        d = Vector((v.co.x, v.co.y)) - c
        if d.length < 1e-9:
            continue
        k = int((math.atan2(d.y, d.x) % (2 * math.pi)) / (2 * math.pi) * N) % N
        prof[k] = max(prof[k], d.length)
    # a sector the band is too coarse to reach gets its neighbours' radius
    for _ in range(3):
        for k in range(N):
            if prof[k] <= 0:
                nb = [prof[(k - 1) % N], prof[(k + 1) % N]]
                nb = [x for x in nb if x > 0]
                if nb:
                    prof[k] = sum(nb) / len(nb)
    if min(prof) <= 0:
        print("[belt] band does not close -- left as modelled")
        return None
    R = sum(prof) / N
    for v in ob.data.vertices:
        d = Vector((v.co.x, v.co.y)) - c
        if d.length < 1e-9:
            continue
        k = int((math.atan2(d.y, d.x) % (2 * math.pi)) / (2 * math.pi) * N) % N
        push = (R - prof[k]) / d.length
        v.co.x += d.x * push
        v.co.y += d.y * push
    ob.data.update()
    print(f"[belt] strap rounded: was {min(prof):.3f}..{max(prof):.3f}, now {R:.3f}")
    return R


# The HOLSTER is the deepest slab: a pistol holster is longer than a pouch
# because it has to swallow a barrel. The other hanger stays as a pouch.
holster = max(hangers, key=lambda h: h['drop'] * h['depth'])
print(f"holster picked at az={math.degrees(holster['az']):+.1f}deg")

def opposite_hips(ob, ring, keep, move):
    """Swing the second holster round to the OTHER hip.

    The two slabs are modelled 116 degrees apart -- both on the same side,
    which is how a belt looks on a shelf and not how one looks on a person.
    No rotation of the whole belt can put them on opposite hips, so the second
    one is carried round the band until it is 180 degrees from the first: a
    holster on each side, square with the body.

    Everything below the band and nearer to the second slab than to the first
    goes; the strap itself does not move.
    """
    c = Vector((ring['cx'], ring['cy']))
    spin = (keep['az'] + math.pi) - move['az']
    R = Matrix.Rotation(spin, 3, 'Z')
    moved = 0
    for v in ob.data.vertices:
        if v.co.z > ring['cz'] - 0.14:          # the strap, not a hanger
            continue
        d = Vector((v.co.x, v.co.y)) - c
        if d.length < 1e-9:
            continue
        az = math.atan2(d.y, d.x)
        da = lambda a: abs((az - a + math.pi) % (2 * math.pi) - math.pi)
        if da(move['az']) >= da(keep['az']):    # belongs to the one staying put
            continue
        p = Vector((v.co.x - c.x, v.co.y - c.y, v.co.z))
        q = R @ p
        v.co = Vector((q.x + c.x, q.y + c.y, q.z))
        moved += 1
    ob.data.update()
    print(f"[belt] second holster swung {math.degrees(spin):+.1f}deg to the far hip "
          f"({moved} verts)")
    return spin


# Round the strap BEFORE anything is measured off it again: the holster's own
# centre and the ring's half-widths both move when the band does.
if roundify(ob, ring) is not None:
    ring, hangers = ring_and_hangers(ob)
    holster = max(hangers, key=lambda h: h['drop'] * h['depth'])

# ...and put the second one on the opposite hip, so the pair is square with the
# body instead of both crowding the same side.
other = next((h for h in hangers if h is not holster), None)
if other is not None:
    opposite_hips(ob, ring, holster, other)
    ring, hangers = ring_and_hangers(ob)
    holster = min(hangers, key=lambda h: abs((h['az'] - holster['az'] + math.pi)
                                             % (2 * math.pi) - math.pi))

# A holster belongs on the strong-hand side, and the heroes are right-handed.
# Turn the whole belt about the vertical until the holster sits at +X, which is
# the wearer's right once this is exported (Blender +X -> glTF +X).
spin = -holster['az']
R = Matrix.Rotation(spin, 3, 'Z')
ctr = Vector((ring['cx'], ring['cy'], ring['cz']))
sc = WAIST_W / (2 * max(ring['hx'], ring['hy']))
for v in ob.data.vertices:
    v.co = (R @ (v.co - ctr)) * sc
ob.data.update()
hctr = (R @ (holster['ctr'] - ctr)) * sc

# The socket. The pistols carry their origin at the BUTT of the grip and run
# forward along +X, which the frame above has pointed straight down -- so the
# whole weapon hangs BELOW this point. Put it a little above the top edge of
# the strap and the grip stands proud of the belt with the barrel down the
# thigh, which is what a holstered pistol looks like. Putting it at the middle
# of the slab (the obvious choice) buried the grip under the belt.
belt_top = (ring['top'] - ring['cz']) * sc
# How high the weapon rides. The socket is the point the pistol's GRIP BUTT
# sits on and the barrel hangs straight DOWN from it -- so the butt is the
# highest point of the gun, and for the grip to stand proud of the strap the
# way a holstered pistol's does, the butt has to sit ABOVE the strap's top
# edge by about a grip's length. Sinking it under the strap (tried) put the
# entire weapon below the belt, dangling in mid-air at the knee.
SEAT = +0.045
STANDOFF = 1.06   # x the ring radius: how far off the hip the holster hangs


def socket(name, h):
    """An empty at the mouth of one hanger, with its axes already set to the
    pose a pistol takes in it."""
    a = Matrix.Rotation(spin, 3, 'Z') @ Vector((math.cos(h['az']), math.sin(h['az']), 0))
    # STAND-OFF. The socket is on the strap, but what hangs out of it is 12 cm
    # BELOW the strap -- and a body is usually wider down there than it is at
    # the waist. Seated flush, the pistol ends up inside the hip. A real
    # holster hangs off the belt, not against it.
    mouth = a * (max(ring['hx'], ring['hy']) * sc * STANDOFF) + Vector((0, 0, belt_top + SEAT))
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = 'ARROWS'
    e.empty_display_size = 0.05
    # NO spin on the frame: holster_frame is written in the belt's final frame
    # (+X right, +Y forward), after the strap has been turned. The old frame was
    # built off the hanger's pre-spin azimuth and needed R; spinning this one
    # twisted every socket 148 degrees about the muzzle.
    e.matrix_world = Matrix.Translation(mouth) @ holster_frame(h, ring).to_4x4()
    bpy.context.scene.collection.objects.link(e)
    e.parent = ob
    e.matrix_parent_inverse = ob.matrix_world.inverted()
    print(f"[belt] socket {name} at az={math.degrees(h['az'] + spin) % 360:.1f}deg")
    return e


# BOTH HANGERS ARE HOLSTERS on this belt -- they come out the same size to the
# millimetre (drop 0.322, depth 0.396 each), which is why "the deeper one" was
# a coin toss. The picked one is spun to the wearer's right and is where a
# drawn-and-put-away pistol lives; the other is exported too, so a second
# weapon has somewhere to hang rather than being thrown on the floor.
emps = [socket("holster", holster)]
for h in hangers:
    if h is not holster:
        emps.append(socket("holster2", h))
        break
emp = emps[0]

ob.name = NAME
shrink_textures()
out = os.path.join(ROOT, "public", "models", f"{NAME}.glb")
bpy.ops.object.select_all(action='DESELECT')
ob.select_set(True)
for e in emps:
    e.select_set(True)
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_yup=True,
                          export_apply=True, export_animations=False,
                          export_image_format='WEBP', export_image_quality=94)
pts = [v.co for v in ob.data.vertices]
d = [max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)]
tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
thumb = render_thumb(ob, NAME)
print(f"[{NAME}] {tris} tris, {len(ob.data.vertices)} verts, "
      f"{d[0]*1000:.0f} x {d[1]*1000:.0f} x {d[2]*1000:.0f} mm, "
      f"{os.path.getsize(out)//1024} KB, thumb {os.path.getsize(thumb)//1024} KB")
print(f"[belt] {len(emps)} socket(s) exported")
