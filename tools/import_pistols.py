"""Bring the two downloaded pistols in as clean, table-sized props.

  Blender -b -P tools/import_pistols.py

Neither file is usable as downloaded:
  * the Beretta is an FPS *viewmodel* -- the gun rides a 6-bone armature but the
    file also carries two full Mixamo ARMS (5802 verts on a 44-bone rig) and a
    pair of 2 m icospheres. Dropped on a table that reads as severed arms.
  * both are modelled at arbitrary scale, so they are normalised to the real
    length of the weapon (Makarov PM 161.5 mm, Beretta 92FS 217 mm).

Each is reduced to ONE static mesh lying flat: longest axis along X, thinnest
axis up, origin centred on the footprint and sitting on z=0, so the game can
drop it on a table top without fudge factors.
"""
import os
import sys
import math

import bpy
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOME = os.path.expanduser("~")

# src, out name, real overall length (m), which mesh is the weapon, axis frame.
# "bbox" for a model that is already axis-aligned -- its box is its frame and
# PCA would tilt it by the grip rake. "pca" for one authored at an angle: the
# Beretta is an FPS viewmodel posed in a pair of hands, so its bounding box has
# nothing to do with the weapon and only PCA finds the real bore.
GUNS = [
    (f"{HOME}/Downloads/makarov_pistol.glb", "makarov", 0.1615, None, "bbox"),
    (f"{HOME}/Downloads/beretta_pistol_fps_animation.glb", "beretta", 0.217, "Object_62", "pca"),
]
MAX_TEX = 512


def only_the_weapon(keep_name):
    """Delete arms, armatures and junk; return the meshes that are the gun."""
    keep = []
    for o in list(bpy.data.objects):
        if o.type == 'MESH' and o.name.startswith('Icosphere'):
            bpy.data.objects.remove(o, do_unlink=True)
    for o in list(bpy.data.objects):
        if o.type != 'MESH':
            continue
        if keep_name and o.name != keep_name:
            bpy.data.objects.remove(o, do_unlink=True)
        else:
            keep.append(o)
    # bake the rest pose into the mesh, then drop every armature
    for o in keep:
        for m in list(o.modifiers):
            if m.type == 'ARMATURE':
                bpy.context.view_layer.objects.active = o
                bpy.ops.object.modifier_apply(modifier=m.name)
    for o in list(bpy.data.objects):
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o, do_unlink=True)
    return keep


def lay_flat_and_scale(meshes, real_len, mode):
    """One joined mesh in a known frame, with the origin on the grip.

    Target frame, IN BLENDER (Z up), because `export_yup` then maps
    Blender (X, Y, Z) -> glTF (X, Z, -Y):
        X = barrel, muzzle towards +X
        Y = width  (becomes glTF -Z, the side-to-side axis)
        Z = height, slide towards +Z  (becomes glTF +Y, i.e. UP)

    Getting Y and Z the wrong way round is not subtle in game: the pistol ends
    up lying on its side in the hand, so the hero is pointing the flat of the
    slide at whatever it is aiming at.
    """
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    # The glTF importer parents everything under an empty rotated +90 deg X to
    # get from Y-up to Z-up. transform_apply only bakes the object's OWN
    # matrix, so that parent rotation survives and the axis work below is done
    # in the wrong frame -- which is how the Makarov ended up standing on its
    # grip while the Beretta lay flat.
    if ob.parent:
        bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # Put the weapon in a known frame. Which method is right depends on how the
    # source was authored, so it is chosen per weapon rather than guessed:
    #   "bbox" -- the model is already axis-aligned, so its bounding box IS its
    #             frame. PCA on these tilts the result by ~20 deg, because a
    #             pistol's raked grip drags the second component round with it.
    #   "pca"  -- the model was authored at an angle. The Beretta ships as an FPS
    #             viewmodel posed in a pair of hands, so its box has nothing to do
    #             with the weapon; "longest box side = barrel" produced a pistol
    #             rotated 135 deg with its grip in the air.
    import numpy as np
    P = np.array([[v.co.x, v.co.y, v.co.z] for v in ob.data.vertices], dtype=np.float64)
    P -= P.mean(axis=0)
    if mode == "bbox":
        d = P.max(axis=0) - P.min(axis=0)
        order = np.argsort(d)                      # thinnest .. longest
        R = np.eye(3)[[order[2], order[1], order[0]]]
    else:
        # eigenvectors of the covariance, largest spread first:
        # e0 = along the barrel, e1 = grip-to-slide, e2 = across the weapon
        vals, vecs = np.linalg.eigh(P.T @ P)
        order = np.argsort(vals)[::-1]
        e0, e1, e2 = vecs[:, order[0]], vecs[:, order[1]], vecs[:, order[2]]
        if np.dot(np.cross(e0, e1), e2) < 0:       # keep it right-handed
            e2 = -e2
        R = np.stack([e0, e1, e2])                 # rows: new X, Y, Z
    Q = P @ R.T
    # longest -> X, grip-to-slide -> Z (becomes glTF +Y, up), across -> Y
    for v, q in zip(ob.data.vertices, Q):
        v.co = Vector((float(q[0]), float(q[2]), float(q[1])))
    ob.data.update()

    pts = [v.co for v in ob.data.vertices]

    # WHICH WAY IS UP? Not by comparing extents around the centre -- that got
    # both pistols upside down. Use the shape: the slide-and-barrel side runs the
    # WHOLE length of the weapon, while the grip is short and only at the back.
    # So of the two halves along the height axis, the one with the greater
    # LENGTH is the slide. The slide must end up at +Z.
    zmid = (min(p.z for p in pts) + max(p.z for p in pts)) / 2
    def span_x(sel):
        xs = [p.x for p in pts if sel(p.z)]
        return (max(xs) - min(xs)) if len(xs) > 3 else 0.0
    if span_x(lambda z: z > zmid) < span_x(lambda z: z < zmid):
        for v in ob.data.vertices:            # 180 deg about X keeps it handed
            v.co = Vector((v.co.x, -v.co.y, -v.co.z))
        ob.data.update()
        pts = [v.co for v in ob.data.vertices]

    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))

    # Which end is the butt? Slice along the barrel axis and measure how TALL
    # each slice is: the barrel end is shallow, the end carrying slide + grip is
    # far deeper. No part names needed.
    nb = 24
    step = (hi.x - lo.x) / nb
    heights = []
    for i in range(nb):
        x0, x1 = lo.x + i * step, lo.x + (i + 1) * step
        zs = [v.co.z for v in ob.data.vertices if x0 <= v.co.x <= x1]
        heights.append((max(zs) - min(zs)) if len(zs) > 3 else 0.0)
    butt_at_max_x = sum(heights[-nb // 3:]) > sum(heights[:nb // 3])

    # the grip: the lower part of the butt end
    xr = (lo.x + (hi.x - lo.x) * 0.66, hi.x) if butt_at_max_x \
        else (lo.x, lo.x + (hi.x - lo.x) * 0.34)
    grip_pts = [v.co for v in ob.data.vertices
                if xr[0] <= v.co.x <= xr[1] and v.co.z <= lo.z + (hi.z - lo.z) * 0.45]
    if not grip_pts:
        grip_pts = [v.co for v in ob.data.vertices]
    gx = sum(p.x for p in grip_pts) / len(grip_pts)
    gy = (lo.y + hi.y) / 2
    gz = sum(p.z for p in grip_pts) / len(grip_pts)

    # Muzzle to +X. Flipping X alone would MIRROR the mesh, so Y flips with it:
    # together they are a 180 deg turn about Z, which keeps the model handed.
    f = -1.0 if butt_at_max_x else 1.0
    sc = real_len / max(hi.x - lo.x, 1e-9)
    for v in ob.data.vertices:
        v.co = Vector(((v.co.x - gx) * sc * f, (v.co.y - gy) * sc * f, (v.co.z - gz) * sc))
    ob.data.update()
    return ob


def shrink_textures():
    for img in bpy.data.images:
        if img.size[0] > MAX_TEX or img.size[1] > MAX_TEX:
            img.scale(min(img.size[0], MAX_TEX), min(img.size[1], MAX_TEX))
            img.file_format = 'JPEG'


def render_thumb(ob, name):
    """A small three-quarter view of the weapon on transparency, for the
    PUBG-style pickup prompt. Rendered from the model itself so the picture in
    the HUD is always the thing actually lying on the floor."""
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
    # Frame it from the lens, not by eye: with a 45 mm lens on the 36 mm
    # default sensor the visible width is 0.8*d, so d = span / (0.8 * fill).
    # The gun lies on its side (length along X, profile in Y, width in Z), so
    # the camera sits mostly ABOVE and slightly to the front -- that is the
    # view that reads as "a pistol" at 72 px in the HUD.
    cam_data.lens = 45
    span = max(hi_x - lo_x for hi_x, lo_x in [(max(p.x for p in pts), min(p.x for p in pts))])
    d = span / (0.8 * 0.80)
    cam.location = ctr + Vector((0.22, -0.92, 0.32)).normalized() * d
    direction = (ctr - cam.location).normalized()
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    scn.camera = cam

    for key, loc, energy in (("k", (2, -3, 3), 900), ("f", (-3, -2, 1), 350)):
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


for src, name, real_len, keep_name, mode in GUNS:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=src)
    meshes = only_the_weapon(keep_name)
    if not meshes:
        sys.exit(f"{name}: no weapon mesh left")
    ob = lay_flat_and_scale(meshes, real_len, mode)
    ob.name = name
    shrink_textures()
    out = os.path.join(ROOT, "public", "models", f"{name}.glb")
    # JPEG, or the shrunk images get re-encoded as PNG and the file grows back
    bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_yup=True,
                              export_apply=True, export_animations=False,
                              export_image_format='JPEG', export_jpeg_quality=85)
    pts = [v.co for v in ob.data.vertices]
    d = (max(p.x for p in pts) - min(p.x for p in pts),
         max(p.y for p in pts) - min(p.y for p in pts),
         max(p.z for p in pts) - min(p.z for p in pts))
    tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    thumb = render_thumb(ob, name)
    print(f"[{name}] {tris} tris, {len(ob.data.vertices)} verts, "
          f"{d[0]*1000:.0f} x {d[1]*1000:.0f} x {d[2]*1000:.0f} mm, "
          f"{os.path.getsize(out)//1024} KB, thumb {os.path.getsize(thumb)//1024} KB")
