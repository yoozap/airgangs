"""Bring the gun-range model in, keep the booth, and close its floating posts.

  Blender -b -P tools/import_targets.py

Two things are wrong with the download:
  * it is a whole range -- two free-standing target stands as well as the booth.
    Only the booth and the moving target it faces are wanted.
  * the booth POSTS DO NOT REACH THEIR BASES. Three of them start 19.09 units
    above the block they are supposed to stand on, which at game scale is a
    0.86 m hole between the post and its foot. This is in the source geometry,
    not a rendering artifact -- the parts really are that far apart.

The posts are stretched DOWN onto their bases rather than moved, so everything
they carry (the rails, the hanging target) stays at its original height.
"""
import os
import sys
from collections import defaultdict

import bpy
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(os.path.expanduser("~"), "Downloads", "gun_targets.glb")
OUT = os.path.join(ROOT, "public", "models", "gun_targets.glb")
MAX_TEX = 1024
COL = 6.0      # units -- how wide an "upright column" is when grouping parts
MIN_GAP = 3.0   # units -- ignore hairline seams
GROUNDED = 1.0  # units -- how close to the floor a base has to be

# BoothWall is deliberately NOT kept. Each wall is a 1.6 m OSB panel, and with
# 20 booths shoulder to shoulder they line up into a solid fence standing
# between the shooter and the targets -- the targets then read as embedded in
# the woodwork. The counter alone is what a firing point needs.
KEEP = ("boothtable", "moving_target", "target(full)_5")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SRC)

dropped = 0
for o in list(bpy.data.objects):
    if o.type != 'MESH':
        continue
    n, keep = o, False
    while n:
        # Blender keeps the glTF node names verbatim ("Moving Target Rails_7")
        # while three.js sanitises spaces to underscores. Normalise, or the
        # filter silently drops the rail run and only the booth survives.
        if (n.name or "").lower().replace(" ", "_").startswith(KEEP):
            keep = True
            break
        n = n.parent
    if not keep:
        bpy.data.objects.remove(o, do_unlink=True)
        dropped += 1

# split into loose pieces so each post and each base can be reasoned about
for o in list(bpy.data.objects):
    if o.type != 'MESH':
        continue
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.mesh.separate(type='LOOSE')

parts = []
for o in bpy.data.objects:
    if o.type != 'MESH' or not o.data.vertices:
        continue
    ws = [o.matrix_world @ v.co for v in o.data.vertices]
    parts.append(dict(obj=o,
                      cx=sum(p.x for p in ws) / len(ws),
                      cy=sum(p.y for p in ws) / len(ws),
                      x0=min(p.x for p in ws), x1=max(p.x for p in ws),
                      y0=min(p.y for p in ws), y1=max(p.y for p in ws),
                      lo=min(p.z for p in ws), hi=max(p.z for p in ws)))

# ---- the two little targets stuck on the booth's table structure --------
# The download pins a pair of small target cards to the upright right beside
# the counter. At game scale they are 2 x 13 cm boards floating by the table,
# and they read as two stray targets behind it. Cut them HERE, in the model,
# rather than hiding them in the game code.
cards = [q for q in parts
         if min(q["x1"] - q["x0"], q["y1"] - q["y0"], q["hi"] - q["lo"]) < 0.2   # a flat card
         and max(q["x1"] - q["x0"], q["hi"] - q["lo"]) < 4                        # and a small one
         and 14 < (q["lo"] + q["hi"]) / 2 < 20                                    # at counter height
         and q["cy"] < 0]                                                         # on the shooter's side
for q in cards:
    bpy.data.objects.remove(q["obj"], do_unlink=True)
parts = [q for q in parts if q not in cards]
if len(cards) != 2:
    print(f"[targets] WARNING: expected 2 table cards, cut {len(cards)}")

# ---- the target boards themselves --------------------------------------
# "we have 2 target, 1 with human, 1 with circle, remove both": the printed
# boards go, the frame that carried them stays. They are the only panels in the
# model of that size -- roughly 0.7 x 1.1 m at game scale -- so they are picked
# by shape rather than by a name the next re-import might change.
def _area(q):
    dx, dy, dz = q["x1"] - q["x0"], q["y1"] - q["y0"], q["hi"] - q["lo"]
    return max(dx * dy, dx * dz, dy * dz)

boards = [q for q in parts
          if min(q["x1"] - q["x0"], q["y1"] - q["y0"], q["hi"] - q["lo"]) < 0.6  # a flat sheet
          and _area(q) > 200                                                      # and a big one
          and q["cy"] > 100]                                                      # down the lane, NOT the counter
for q in boards:
    bpy.data.objects.remove(q["obj"], do_unlink=True)
parts = [q for q in parts if q not in boards]
print(f"[targets] board panels cut: {len(boards)} "
      + ", ".join(f"{_area(q):.0f}u2 at y{q['cy']:.0f}" for q in boards))

cols = defaultdict(list)
for p in parts:
    cols[(round(p["cx"] / COL), round(p["cy"] / COL))].append(p)

floor = min(p["lo"] for p in parts)

fixed = []
for key, ps in cols.items():
    ps.sort(key=lambda a: a["lo"])
    hi = ps[0]["hi"]
    grounded = ps[0]["lo"] <= floor + GROUNDED
    for q in ps[1:]:
        gap = q["lo"] - hi
        # Only stand uprights back on a base that is ON THE FLOOR. Without this
        # the same rule welds the hanging targets down onto their rails, which
        # are MEANT to hang clear.
        if gap > MIN_GAP and grounded:
            # stretch this piece's lowest vertices down to meet the one below,
            # so whatever it carries above stays exactly where it was
            o = q["obj"]
            inv = o.matrix_world.inverted()
            foot_world = hi
            thresh = q["lo"] + gap * 0.05
            for v in o.data.vertices:
                if (o.matrix_world @ v.co).z <= thresh:
                    w = o.matrix_world @ v.co
                    v.co = inv @ Vector((w.x, w.y, foot_world))
            o.data.update()
            fixed.append((key, gap))
        hi = max(hi, q["hi"])

# ---- close the gap between the LEFT upright and the counter -------------
# The counter spans x -15.8..15.8. On the RIGHT the upright's inner face sits at
# x 15.8, flush against it. On the LEFT the upright is at x -20.7 -- a 4.9 unit
# hole between the leg and the table, which at game scale is a visible 22 cm.
# Measured rather than hard-coded, so a re-import still lands flush.
# the counter is the biggest THIN HORIZONTAL slab -- without the thinness test
# this picks a moving-target rail, which is far larger in plan
slabs = [q for q in parts if (q["hi"] - q["lo"]) < 2.0]
counter = max(slabs, key=lambda q: (q["x1"] - q["x0"]) * (q["y1"] - q["y0"]))
# only uprights beside the counter, at its own height -- not the distant rails
near = [q for q in parts
        if q["lo"] < counter["hi"] + 1.0 and q["hi"] > counter["lo"] - 1.0
        and abs(q["cy"] - (counter["y0"] + counter["y1"]) / 2) < 40]
# strictly BEYOND the counter's end -- brackets that already touch it sit at
# exactly counter.x0 and would make the gap measure as zero
left = [q for q in near if q["x1"] < counter["x0"] - 1.0]
shift = 0.0
if left:
    inner = max(q["x1"] for q in left)
    shift = counter["x0"] - inner
    if abs(shift) > 0.05:
        # Carry EVERY part of that upright, top to bottom -- panel faces, post
        # and the stone footings. Selected by CENTRE within a pad, not by strict
        # containment: the stones are wider than the panel they sit under, so a
        # containment test leaves them behind while the leg moves off them.
        PAD = 6.0
        lo_x = min(q["x0"] for q in left) - PAD
        hi_x = max(q["x1"] for q in left) + PAD
        left = [q for q in parts if lo_x <= q["cx"] <= hi_x]
        for q in left:
            o = q["obj"]
            inv = o.matrix_world.inverted()
            for v in o.data.vertices:
                w = o.matrix_world @ v.co
                v.co = inv @ Vector((w.x + shift, w.y, w.z))
            o.data.update()

# ---- clear the lane between the booth and its target --------------------
# The download hangs its target from a 14.6 m wire run carried on mid-lane
# posts, all of it at ~1.5 m -- exactly target height. With 20 lanes side by
# side every lane's rails cut straight across every other lane's target boards
# and the hall reads as a thicket of beams.
#
# Keep the booth and keep the target on the posts that actually hold it; delete
# the wires and the mid-lane posts, so the lane between them is empty.
# Drop the booth's big OSB PANELS. They belong to the BoothTable branch, not
# BoothWall, so filtering by branch name keeps them. They are tall sheets
# ~1 m wide; 20 of them shoulder to shoulder line up into a solid fence between
# the shooter and the targets. Posts and the counter are thin and stay.
panel_names = []
for q in parts:
    tall = (q["hi"] - q["lo"]) > 15          # reaches well above the counter
    # a sheet is wide in at least ONE horizontal axis; a post is narrow in both.
    # min() reads 0 here because these panels are single flat quads.
    wide = max(q["x1"] - q["x0"], q["y1"] - q["y0"]) > 5
    if tall and wide and q["cy"] < 60:       # only at the booth end
        panel_names.append(q["obj"].name)
for n in panel_names:
    o = bpy.data.objects.get(n)
    if o:
        bpy.data.objects.remove(o, do_unlink=True)
panels_cut = len(panel_names)
parts = [q for q in parts if bpy.data.objects.get(q["obj"].name) is not None] if not panels_cut else [
    dict(obj=o,
         cx=sum(p.x for p in [o.matrix_world @ v.co for v in o.data.vertices]) / len(o.data.vertices),
         cy=sum(p.y for p in [o.matrix_world @ v.co for v in o.data.vertices]) / len(o.data.vertices),
         x0=min((o.matrix_world @ v.co).x for v in o.data.vertices),
         x1=max((o.matrix_world @ v.co).x for v in o.data.vertices),
         y0=min((o.matrix_world @ v.co).y for v in o.data.vertices),
         y1=max((o.matrix_world @ v.co).y for v in o.data.vertices),
         lo=min((o.matrix_world @ v.co).z for v in o.data.vertices),
         hi=max((o.matrix_world @ v.co).z for v in o.data.vertices))
    for o in bpy.data.objects if o.type == 'MESH' and o.data.vertices]

far = max(q["cy"] for q in parts) if parts else 0
doomed = []
for q in parts:
    long_run = (q["y1"] - q["y0"]) > 40          # a wire spanning the lane
    mid_lane = 60 < q["cy"] < far - 45           # furniture between the ends
    if long_run or mid_lane:
        doomed.append(q["obj"].name)             # names, not references:
cleared = len(doomed)                            # removing invalidates the rest
for n in doomed:
    o = bpy.data.objects.get(n)
    if o:
        bpy.data.objects.remove(o, do_unlink=True)

# rebuild the part table from what survived
parts = []
for o in bpy.data.objects:
    if o.type != 'MESH' or not o.data.vertices:
        continue
    ws = [o.matrix_world @ v.co for v in o.data.vertices]
    parts.append(dict(obj=o,
                      cx=sum(p.x for p in ws) / len(ws),
                      cy=sum(p.y for p in ws) / len(ws),
                      x0=min(p.x for p in ws), x1=max(p.x for p in ws),
                      y0=min(p.y for p in ws), y1=max(p.y for p in ws),
                      lo=min(p.z for p in ws), hi=max(p.z for p in ws)))

# ---- lengthen the lane and fill it with pillars -------------------------
# The lane runs along +Y: booth at y~0, support pillars at y~110 and y~210, the
# target at y~307 (14.6 m in game). STRETCH makes the walk to the target longer
# and keeps the pillar spacing the same, so extra pillars and wire appear in the
# middle rather than the existing ones just drifting apart.
#
# Wires are SCALED along Y (they run that way, so they simply get longer);
# everything else is TRANSLATED, or the pillars and the target would fatten.
STRETCH = 1.0   # 1.0 = the model's own lane length; >1 lengthens it
BOOTH_END = 50.0        # units -- anything past here belongs to the lane

def bounds(o):
    ws = [o.matrix_world @ v.co for v in o.data.vertices]
    return (min(p.y for p in ws), max(p.y for p in ws),
            min(p.x for p in ws), max(p.x for p in ws),
            min(p.z for p in ws), max(p.z for p in ws))

lane_objs = []
for o in [q["obj"] for q in parts if q["obj"].name in bpy.data.objects]:
    y0, y1, x0, x1, z0, z1 = bounds(o)
    cy = (y0 + y1) / 2
    if cy <= BOOTH_END:
        continue
    is_wire = (y1 - y0) > 40 and (x1 - x0) < 3 and (z1 - z0) < 6
    inv = o.matrix_world.inverted()
    for v in o.data.vertices:
        w = o.matrix_world @ v.co
        ny = w.y * STRETCH if is_wire else w.y + (STRETCH - 1.0) * cy
        v.co = inv @ Vector((w.x, ny, w.z))
    o.data.update()
    lane_objs.append((o, cy * STRETCH, is_wire))

# the pillars that now stand too far apart: clone one station into each new gap
stations = sorted({round(cy) for o, cy, w in lane_objs if not w and cy < 290 * STRETCH})
pillars = [o for o, cy, w in lane_objs if not w and 90 * STRETCH < cy < 130 * STRETCH]
added = 0
if STRETCH > 1.001 and pillars and stations:
    base_y = sum((bounds(o)[0] + bounds(o)[1]) / 2 for o in pillars) / len(pillars)
    target_y = max(cy for o, cy, w in lane_objs)
    step = 110.0            # original pillar pitch, kept after the stretch
    y = base_y + step
    while y < target_y - step * 0.5:
        for o in pillars:
            c = o.copy()
            c.data = o.data.copy()
            bpy.context.collection.objects.link(c)
            c.location.y += (y - base_y)
            added += 1
        y += step

for img in bpy.data.images:
    if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
        img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
        img.file_format = 'JPEG'

bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', export_yup=True,
                          export_apply=True, export_animations=False,
                          export_image_format='JPEG', export_jpeg_quality=85)

tris = sum(len(p.vertices) - 2 for o in bpy.data.objects if o.type == 'MESH'
           for p in o.data.polygons)
print(f"[targets] {dropped} range pieces dropped, {len(cards)} table cards cut, "
      f"{len(fixed)} floating parts "
      f"stood on their bases, left upright moved {shift:+.2f}u to the counter, "
      f"{cleared} mid-lane rails + {panels_cut} booth panels cleared, "
      f"lane x{STRETCH} with {added} pillar parts added, "
      f"({', '.join(f'{g:.1f}u' for _, g in fixed) or 'none'}), "
      f"{tris} tris, {os.path.getsize(OUT)//1024} KB")
