"""
Blender-headless: a REAL Soviet-era Tbilisi panel block (khrushchevka, 1-464
family) as actual 3D geometry — no photographic textures anywhere.

  /Applications/Blender.app/Contents/MacOS/Blender -b -P tools/build_panel_block.py

WHY THIS EXISTS
---------------
The city's buildings are OSM footprints extruded into solid prisms in JS, with
Wikimedia photos stretched over them and a kit of boxes stuck on the front.
tools/build_facade_kit.py says it outright: "Walls are solid, so everything must
PROTRUDE; recession is faked by a dark pane just off the wall." That is the
whole problem. A window reads as real because it sits 100-150 mm BACK inside a
reveal and the jamb throws a shadow; you cannot fake that with a decal.

So this builds the shell WITH THE HOLES IN IT. Nothing is subtracted with a
boolean — a panel building is literally a grid of precast slabs with 20 mm
joints between them, so the wall is modelled as that grid and a window opening
is simply a cell left empty. Authentic and cheap at the same time.

WHAT A SECTION IS
-----------------
Khrushchevki were not designed as buildings, they were designed as *sections*
(подъезд) — one stair, one entrance, 2 flats per landing — repeated along the
street. A real block is 2-6 identical sections. SECTIONS below is that count,
which is why this one script covers most of the Soviet stock in Tbilisi.

Blender is Z-up; export_yup=True hands three.js its usual Y-up. Origin is at
the centre of the footprint, on the ground, so the game can drop it straight
onto an OSM footprint centroid.

Output: public/models/panel_block.glb  (27 MB raw)

Then SHRINK IT -- the raw export is ten times the size it needs to be:

  ./node_modules/.bin/gltf-transform optimize \
      public/models/panel_block.glb public/models/panel_block.glb \
      --compress draco --texture-compress webp --texture-size 1024 \
      --simplify false --join false --flatten false --palette false \
      --prune-attributes false        # 27.5 MB -> 1.8 MB

(tools/compress_models.sh does the same to every model and carries the same
flags; the four `false`s are not optional -- see the comment in it.)
"""

import bpy
import bmesh
import math
import os
import random

import numpy as np
from mathutils import Vector, noise

# =============================================================================
# CONFIG
# =============================================================================

SEED = 11

# --- massing (metres) ---------------------------------------------------------
SECTIONS = 3            # stairwells side by side; real blocks run 2-6
SECTION_VARIANTS = (2, 3, 4, 5, 6)   # lengths exported as block_2 .. block_6
SECTION_W = 12.0        # m — one 1-464 section is ~12 m of street frontage
DEPTH = 11.4            # m — standard khrushchevka depth
FLOORS = 5              # 5 is the definition: no lift above 5 storeys
FLOOR_H = 2.80          # m — the Soviet standard, and it is SHORT; that is the look
PLINTH_H = 0.75         # m — the base course the block stands on
PLINTH_OUT = 0.06       # m — how far the plinth projects past the wall

# --- precast panel grid -------------------------------------------------------
# The seams are the single most recognisable feature of this building type.
PANEL_GAP = 0.022       # m — joint between panels; reads as a dark line
PANEL_INSET = 0.015     # m — panel face sits proud of the backing wall
BACKING_T = 0.22        # m — the structural wall behind the panel skin

# --- windows ------------------------------------------------------------------
# W3 in tools/FACADE_KIT_TYPOLOGY.md: 1.45 x 1.5 two-leaf, 1.7-2.1 three-part.
WIN_W = 1.45            # m
WIN_W3P = 1.90          # m — the wider three-part opening
WIN_H = 1.50            # m — short and wide: a khrushchevka window is NOT tall
SILL_ABOVE_FLOOR = 0.85  # m
REVEAL = 0.13           # m — how far back the frame sits. THE critical number.
FRAME_FACE = 0.055      # m
FRAME_DEPTH = 0.075     # m
GLASS_SET = 0.025       # m — glass behind the frame face
SILL_T = 0.045          # m — concrete sill slab thickness
SILL_PROJ = 0.035       # m — how far it sticks out past the wall face
PVC_SHARE = 0.42        # fraction of windows replaced with white PVC (W4)

# --- balconies (B2: welded bar rail, the most common balcony in the city) -----
BALC_DEPTH = 0.95       # m — cantilever slab projection
BALC_SLAB_T = 0.12      # m
BALC_RAIL_H = 0.95      # m
BALC_BAR_D = 0.014      # m — 12 mm bar, rounded up for a 6-sided prism
BALC_BAR_SPACING = 0.11  # m
BALC_INFILL = (0.34, 0.26)  # P(flat sheet), P(corrugated sheet); rest open bars
BALC_GLAZED_SHARE = 0.30    # self-built glazed-in verandas — silhouette chaos

# --- roof ---------------------------------------------------------------------
PARAPET_H = 0.55        # m
PARAPET_T = 0.20        # m
COPING_OUT = 0.05       # m — coping overhangs both faces, throws a shadow line
VENT_COUNT = 4          # roof vent stacks per section

# --- shading ------------------------------------------------------------------
# A bevel catches a highlight and is most of what separates "model" from
# "placeholder" -- but only on edges you can actually see. Bevelling every edge
# of every overlapping box cost 38k -> 335k triangles (8.8x) for highlights on
# glazing bars that are sub-pixel beyond ~30 m. Pro practice is real geometry
# for SILHOUETTE and cheaper tricks for surface detail, so the bevel is spent
# on the big forms only: plinth, parapet, slabs, sills, canopy.
BEVEL_W = 0.014         # m
BEVEL_SEGMENTS = 1
BEVEL_ON = os.environ.get("NO_BEVEL") != "1"   # NO_BEVEL=1 to measure raw cost

# --- surface: real maps, and the dirt that proves the thing is outdoors -------
# Geometry alone still reads as a render: a panel with no grain and no
# weathering is a clean grey plane whatever shape its edges are. Two cheap
# layers fix that, and neither costs a triangle.
#
#   1. a PBR pair on every concrete surface — albedo grain + a normal map, so
#      the flat faces catch light instead of going matte,
#   2. vertex colours: splash-back dirt up the plinth, rain stains running out
#      of the sills, and a per-panel tint, because precast panels were cast in
#      different batches and never matched.
TEX_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "public", "textures")
TEX_BASE = "concrete.jpg"       # ambientCG, already in the repo
TEX_NORM = "concrete_nor.jpg"
TEX_PX = 1024           # what goes into the glb; 2K is waste at this distance
TEX_M = 1.4             # metres of wall per texture tile; finer reads better
                        # at the distance you actually fly past these
TEX_MEAN = 0.80         # the albedo is levelled to this before it is used, so
                        # multiplying it by the palette does not halve the city
TEX_GRAIN = 2.60        # how hard the levelled grain swings about that mean
CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cache")

# The block sits on the LOWEST terrain under its footprint, so on any slope
# the plinth is partly buried -- dirt that stopped at 1.3 m was underground on
# half the city. It also has to survive the vertex resolution: the wall is big
# flat panels, so the only places to paint are their corners.
DIRT_H = 2.60           # m — how far splash-back climbs the plinth
DIRT_MIN = 0.52         # darkest value at the ground
STREAK_DROP = 0.95      # m — how far a stain runs below a sill
STREAK_MIN = 0.60       # darkest value right under the sill
PANEL_TINT = 0.075      # +/- per-panel lightness jitter

# --- output -------------------------------------------------------------------
COLLECTION_NAME = "PanelBlock"
OUTPUT_NAME = "panel_block"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "public", "models")

# --- material slots: index order is referenced by every face ------------------
M_PANEL, M_SEAM, M_CONCRETE, M_WOOD, M_PVC, M_GLASS, M_METAL, M_DOOR, M_ROOF = range(9)

PALETTE = [
    # (name, hex, roughness, metallic)  — straight off the typology catalogue
    ("panel_concrete", "#CFC4AD", 0.92, 0.0),
    ("panel_seam",     "#8E8778", 0.95, 0.0),   # the joint, in shadow
    ("concrete_cast",  "#B5AEA0", 0.90, 0.0),
    ("frame_wood",     "#6B4A2F", 0.80, 0.0),
    ("frame_pvc",      "#F2F2EE", 0.55, 0.0),
    ("glass_dark",     "#20242A", 0.12, 0.0),
    ("metal_painted",  "#E5E3DC", 0.60, 0.25),
    ("door_dark",      "#3A3632", 0.75, 0.0),
    ("roof_felt",      "#4A4844", 0.95, 0.0),
]


# =============================================================================
# Scene plumbing
# =============================================================================

def fresh_collection(name):
    """Only ever touches what a previous run of THIS script made."""
    existing = bpy.data.collections.get(name)
    if existing:
        for obj in list(existing.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(existing)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    col = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(col)
    return col


def hex_rgb(hx):
    """sRGB hex -> linear float triple, which is what Blender wants."""
    hx = hx.lstrip("#")
    out = []
    for i in (0, 2, 4):
        c = int(hx[i:i + 2], 16) / 255.0
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return tuple(out)


def _load_image(fname, colorspace):
    path = os.path.abspath(os.path.join(TEX_DIR, fname))
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = colorspace
    return img


_ALBEDO = None


def _levelled_albedo():
    """The albedo photograph as a DETAIL layer, not a colour.

    concrete.jpg averages mid-grey. Multiply the palette by it as-is and every
    block comes out half as bright as it was, under a tint the game already
    tuned against the flat colours. So the tile is recentred on a near-white
    mean first: the grain, the stains and the aggregate all survive, the
    overall value does not move, and the palette still says what colour the
    building is.

    IT IS WRITTEN BACK OUT AS A FILE. Editing `img.pixels` of a file-backed
    image does not stick -- the next read comes back off the disk (all three
    materials printed the same raw 0.723 mean), and the glTF exporter embeds
    the FILE, so the levelling silently never shipped. The levelled tile is
    its own PNG in tools/cache/, loaded once and shared by every material.
    """
    global _ALBEDO
    if _ALBEDO is not None:
        return _ALBEDO
    src = _load_image(TEX_BASE, "sRGB")
    w, h = src.size
    if max(w, h) > TEX_PX:                      # keep the aspect: these are 2:1
        k = TEX_PX / max(w, h)
        w, h = max(1, int(w * k)), max(1, int(h * k))
        src.scale(w, h)
    px = np.empty(w * h * 4, dtype=np.float32)
    src.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    mean = float(px[:, :3].mean())
    if mean > 1e-4:
        # NOT a plain multiply: scaling a mid-grey photograph up to near-white
        # pushes its top half past 1.0 and CLIPS it, and the grain -- the only
        # reason the image is here -- flattens out in the highlights. Recentre
        # it on TEX_MEAN as a variation around 1.0 instead, and give that
        # variation a push, because concrete photographed in flat light has
        # less contrast than concrete looks like in the sun.
        px[:, :3] = np.clip(TEX_MEAN * (1.0 + TEX_GRAIN * (px[:, :3] / mean - 1.0)),
                            0.0, 1.0)
    px[:, 3] = 1.0
    out = bpy.data.images.new("pb_albedo", w, h, alpha=False)
    out.pixels.foreach_set(px.reshape(-1))
    out.file_format = "PNG"
    os.makedirs(CACHE_DIR, exist_ok=True)
    out.filepath_raw = os.path.join(CACHE_DIR, "pb_albedo.png")
    out.save()
    out.colorspace_settings.name = "sRGB"
    after = float(px[:, :3].mean())
    print(f"  albedo {TEX_BASE} {w}x{h}: mean {mean:.3f} -> {after:.3f} "
          f"(grain x{TEX_GRAIN}) -> {out.filepath_raw}")
    _ALBEDO = out
    return out


def make_material(name, hx, roughness, metallic, textured=False):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    rgb = hex_rgb(hx)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
    mat.diffuse_color = (*rgb, 1.0)   # solid-shading viewport colour
    if textured and bsdf:
        # albedo x palette -> Base Color. ONE multiply, because that is the
        # shape the glTF exporter understands: it becomes baseColorTexture
        # with baseColorFactor, not a baked copy of the image.
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = _levelled_albedo()
        tex.location = (-760, 300)
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 1.0
        mix.location = (-420, 300)
        nt.links.new(mix.inputs[6], tex.outputs["Color"])     # A
        mix.inputs[7].default_value = (*rgb, 1.0)             # B = the palette
        nt.links.new(bsdf.inputs["Base Color"], mix.outputs[2])
        nrm_tex = nt.nodes.new("ShaderNodeTexImage")
        nrm_img = _load_image(TEX_NORM, "Non-Color")
        if max(nrm_img.size) > TEX_PX:
            k = TEX_PX / max(nrm_img.size)
            nrm_img.scale(max(1, int(nrm_img.size[0] * k)), max(1, int(nrm_img.size[1] * k)))
        nrm_tex.image = nrm_img
        nrm_tex.location = (-760, -40)
        nrm = nt.nodes.new("ShaderNodeNormalMap")
        nrm.location = (-420, -40)
        # 1.0 is invisible on a wall lit this flatly; the exporter writes this
        # straight through as the glTF normalTexture scale.
        nrm.inputs["Strength"].default_value = 1.8
        nt.links.new(nrm.inputs["Color"], nrm_tex.outputs["Color"])
        nt.links.new(bsdf.inputs["Normal"], nrm.outputs["Normal"])
    return mat


def activate(obj):
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def call_op(op, **kwargs):
    try:
        return op(**kwargs)
    except TypeError as err:
        print(f"  ! {op.idname()} rejected an argument ({err}); retrying bare")
        return op(filepath=kwargs["filepath"])


# =============================================================================
# Geometry helpers
# =============================================================================

def add_box(bm, corner, size, mat=0):
    """Axis-aligned box from its minimum corner. Overlapping boxes are fine and
    are deliberately preferred over boolean unions — a union at these
    intersection counts is slow and produces garbage topology."""
    x0, y0, z0 = corner
    x1, y1, z1 = x0 + size[0], y0 + size[1], z0 + size[2]
    pts = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
           (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    v = [bm.verts.new(p) for p in pts]
    for a, b, c, d in [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                       (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]:
        bm.faces.new((v[a], v[b], v[c], v[d])).material_index = mat


def add_quad(bm, pts, mat=0):
    bm.faces.new([bm.verts.new(p) for p in pts]).material_index = mat


def add_bar(bm, x, y, z0, z1, d, mat):
    """A vertical round-ish bar — 6 sides is plenty at railing scale."""
    r = d * 0.5
    ring = [(x + r * math.cos(a), y + r * math.sin(a))
            for a in (i * math.pi / 3 for i in range(6))]
    lo = [bm.verts.new((px, py, z0)) for px, py in ring]
    hi = [bm.verts.new((px, py, z1)) for px, py in ring]
    for i in range(6):
        j = (i + 1) % 6
        bm.faces.new((lo[i], lo[j], hi[j], hi[i])).material_index = mat
    bm.faces.new(lo).material_index = mat
    bm.faces.new(list(reversed(hi))).material_index = mat


# =============================================================================
# The window — the piece the whole exercise is about
# =============================================================================

def window(bm, x0, z0, w, h, pvc=False, three_part=False):
    """A real window in the wall plane y=0, opening into +y (into the building).

    The stack that makes it read as an assembly rather than a sticker:
        wall face (y=0) -> reveal 130 mm -> frame 75 mm -> sash -> glass.
    Glass ends up ~160 mm behind the facade. That depth is the entire point.
    """
    x1, z1 = x0 + w, z0 + h
    frame_mat = M_PVC if pvc else M_WOOD
    jamb_d = REVEAL + FRAME_DEPTH

    # --- the reveal: real inner faces of a real hole --------------------------
    # Thin slabs rather than zero-thickness planes so they bevel and shade.
    t = 0.02
    add_box(bm, (x0 - t, 0.0, z0), (t, jamb_d, h), M_PANEL)          # left jamb
    add_box(bm, (x1, 0.0, z0), (t, jamb_d, h), M_PANEL)              # right jamb
    add_box(bm, (x0 - t, 0.0, z1), (w + 2 * t, jamb_d, t), M_PANEL)  # head soffit

    # --- sill: projects past the opening on BOTH sides, and sits BELOW z0 -----
    # A sill flush with the reveal looks wrong to everyone and nobody can say why.
    add_box(bm, (x0 - 0.07, -SILL_PROJ, z0 - SILL_T),
            (w + 0.14, jamb_d + SILL_PROJ + 0.06, SILL_T), M_CONCRETE)

    # --- frame: four overlapping boxes, never a mitred ring -------------------
    y = REVEAL
    add_box(bm, (x0, y, z0), (w, FRAME_DEPTH, FRAME_FACE), frame_mat)
    add_box(bm, (x0, y, z1 - FRAME_FACE), (w, FRAME_DEPTH, FRAME_FACE), frame_mat)
    add_box(bm, (x0, y, z0), (FRAME_FACE, FRAME_DEPTH, h), frame_mat)
    add_box(bm, (x1 - FRAME_FACE, y, z0), (FRAME_FACE, FRAME_DEPTH, h), frame_mat)

    gx0, gx1 = x0 + FRAME_FACE, x1 - FRAME_FACE
    gz0, gz1 = z0 + FRAME_FACE, z1 - FRAME_FACE

    # --- mullions -------------------------------------------------------------
    # W3 is two casements; the three-part adds a wide fixed centre light.
    mull = 0.05 if not pvc else 0.045
    splits = []
    if three_part:
        splits = [0.27, 0.73]       # narrow / wide / narrow — the real rhythm
    else:
        splits = [0.5]
    for s in splits:
        mx = gx0 + (gx1 - gx0) * s - mull / 2
        add_box(bm, (mx, y, gz0), (mull, FRAME_DEPTH, gz1 - gz0), frame_mat)

    # A transom over the casements on the wooden type (the little top light)
    if not pvc:
        tz = gz1 - (gz1 - gz0) * 0.30
        add_box(bm, (gx0, y, tz), (gx1 - gx0, FRAME_DEPTH, 0.045), frame_mat)

    # --- glass: ONE plane. Thickness is invisible and doubles the face count. --
    yg = y + GLASS_SET
    add_quad(bm, [(gx0, yg, gz0), (gx1, yg, gz0), (gx1, yg, gz1), (gx0, yg, gz1)],
             M_GLASS)


def balcony_door(bm, x0, z0, w, h, pvc=False):
    """The balcony door is a window down to the floor — same assembly, no sill,
    with a kick panel at the bottom."""
    window(bm, x0, z0, w, h, pvc=pvc)
    add_box(bm, (x0 + FRAME_FACE, REVEAL + 0.01, z0 + FRAME_FACE),
            (w - 2 * FRAME_FACE, 0.05, 0.42), M_PVC if pvc else M_WOOD)


# =============================================================================
# Balcony — B2, the welded-bar cantilever that is 40% of the city's balconies
# =============================================================================

def balcony(bm, cx, z, w, glazed, infill):
    """Cantilevered slab + railing at (cx centre, z floor level), projecting -y
    (outward). Variants matter more than detail here: a facade where every
    balcony is identical is the thing that screams 'generated'."""
    x0 = cx - w / 2
    d = BALC_DEPTH

    # cantilever slab, with the drip edge that stains every one of these
    add_box(bm, (x0, -d, z - BALC_SLAB_T), (w, d, BALC_SLAB_T), M_CONCRETE)
    add_box(bm, (x0, -d, z - BALC_SLAB_T - 0.03), (w, 0.05, 0.03), M_CONCRETE)

    if glazed:
        # B4: self-built glazed-in veranda. Wraps the whole opening in a grid.
        _glazed_veranda(bm, x0, z, w, d, infill)
        return

    top = z + BALC_RAIL_H
    # top and bottom flat rails
    add_box(bm, (x0, -d, top - 0.04), (w, 0.035, 0.04), M_METAL)
    add_box(bm, (x0, -d, z + 0.06), (w, 0.03, 0.03), M_METAL)
    for side_x in (x0, x0 + w - 0.035):
        add_box(bm, (side_x, -d, z), (0.035, d, BALC_RAIL_H), M_METAL)

    if infill < BALC_INFILL[0]:
        # flat sheet infill, bolted behind the bars
        add_box(bm, (x0, -d + 0.01, z + 0.05), (w, 0.012, BALC_RAIL_H - 0.12), M_METAL)
    elif infill < BALC_INFILL[0] + BALC_INFILL[1]:
        # corrugated sheet — the corrugation is what sells it, so model it
        pitch = 0.075
        n = max(1, int(w / pitch))
        for i in range(n):
            px = x0 + i * (w / n)
            add_box(bm, (px, -d + 0.005, z + 0.05),
                    (w / n * 0.55, 0.022, BALC_RAIL_H - 0.12), M_METAL)
    else:
        # open: 12 mm bars at 110 mm, the default
        n = max(2, int(w / BALC_BAR_SPACING))
        for i in range(n + 1):
            bx = x0 + i * (w / n)
            add_bar(bm, bx, -d + 0.02, z + 0.06, top - 0.04, BALC_BAR_D, M_METAL)

    # side rails so the balcony is enclosed on three sides
    for sx in (x0 + 0.02, x0 + w - 0.02):
        add_box(bm, (sx - 0.012, -d, z + 0.06), (0.024, d - 0.02, 0.03), M_METAL)


def _glazed_veranda(bm, x0, z, w, d, infill):
    """B4: the owner glazed it in. Wood small-pane grid or white PVC."""
    pvc = infill < 0.5
    mat = M_PVC if pvc else M_WOOD
    top = z + 2.35
    post = 0.055
    # corner posts and head/cill rails around the open sides
    for px in (x0, x0 + w - post):
        add_box(bm, (px, -d, z), (post, post, top - z), mat)
        add_box(bm, (px, -post, z), (post, post, top - z), mat)
    add_box(bm, (x0, -d, top - 0.06), (w, d, 0.06), mat)
    add_box(bm, (x0, -d, z + 0.02), (w, d, 0.05), mat)
    # pane grid on the front face
    panes = 5 if not pvc else 3
    for i in range(1, panes):
        mx = x0 + w * i / panes
        add_box(bm, (mx - 0.022, -d, z), (0.044, 0.05, top - z), mat)
    if not pvc:   # the old wooden ones have a horizontal bar too
        add_box(bm, (x0, -d, z + (top - z) * 0.55), (w, 0.045, 0.04), mat)
    add_quad(bm, [(x0, -d + 0.03, z + 0.05), (x0 + w, -d + 0.03, z + 0.05),
                  (x0 + w, -d + 0.03, top - 0.06), (x0, -d + 0.03, top - 0.06)],
             M_GLASS)
    # returns on both sides
    for sx in (x0, x0 + w):
        add_quad(bm, [(sx, -d + 0.03, z + 0.05), (sx, -0.05, z + 0.05),
                      (sx, -0.05, top - 0.06), (sx, -d + 0.03, top - 0.06)], M_GLASS)


# =============================================================================
# Entrance — carries more visual weight than any single window
# =============================================================================

def entrance(bm, cx, rng):
    """Recessed door + the cantilevered concrete canopy that every one of these
    stairwells has. Reference: tools/FACADE_KIT_TYPOLOGY.md C.2."""
    w, h = 1.45, 2.25
    x0 = cx - w / 2
    recess = 0.28          # deeper than the windows: the eye expects that

    # the recess box: jambs + soffit
    add_box(bm, (x0 - 0.03, 0.0, 0.0), (0.03, recess, h), M_PANEL)
    add_box(bm, (x0 + w, 0.0, 0.0), (0.03, recess, h), M_PANEL)
    add_box(bm, (x0 - 0.03, 0.0, h), (w + 0.06, recess, 0.03), M_PANEL)

    # double leaf, panelled — a flat slab reads as a cupboard
    add_box(bm, (x0, recess, 0.0), (w, 0.055, h), M_DOOR)
    for leaf in (0, 1):
        lx = x0 + 0.06 + leaf * (w / 2)
        for row in range(3):
            add_box(bm, (lx, recess - 0.018, 0.22 + row * 0.62),
                    (w / 2 - 0.12, 0.02, 0.44), M_DOOR)
    # fanlight over the door
    add_box(bm, (x0, recess, h + 0.02), (w, 0.05, 0.45), M_METAL)
    add_quad(bm, [(x0 + 0.04, recess + 0.02, h + 0.06),
                  (x0 + w - 0.04, recess + 0.02, h + 0.06),
                  (x0 + w - 0.04, recess + 0.02, h + 0.42),
                  (x0 + 0.04, recess + 0.02, h + 0.42)], M_GLASS)

    # canopy slab: 1.2 x 2.5, the mossy cantilever
    add_box(bm, (cx - 1.25, -1.15, h + 0.52), (2.5, 1.15 + 0.05, 0.14), M_CONCRETE)
    add_box(bm, (cx - 1.25, -1.15, h + 0.46), (2.5, 0.08, 0.06), M_CONCRETE)

    # two steps and the apron
    for i, s in enumerate((0.34, 0.17)):
        add_box(bm, (cx - 1.0, -0.42 - i * 0.30, 0.0), (2.0, 0.34, s), M_CONCRETE)

    # address plate beside the door
    add_box(bm, (x0 + w + 0.16, -0.015, 1.95), (0.34, 0.02, 0.24), M_METAL)


# =============================================================================
# Facade assembly
# =============================================================================

def build_long_facade(bm, length, rng, with_entrances=True):
    """One street elevation, built as the precast panel grid it really is.

    The rhythm is deliberately REGULAR. A khrushchevka elevation is a plain
    grid: every bay has a window on every floor, and the balconies stack in
    vertical columns because they hang off one structural line of cantilever
    slabs. Randomising a balcony per cell -- which is what this did first --
    scatters them over the wall and reads as noise, not as a building.
    Variation belongs in WHICH columns have balconies, how each one is
    infilled and whether its owner glazed it in, not in whether a slab exists.
    """
    bays_per_section = 4
    bay_w = SECTION_W / bays_per_section
    ent_bay = 1                       # the stairwell bay: door below, stair lights above

    for s_i in range(int(round(length / SECTION_W))):
        sx = s_i * SECTION_W

        # Decide the column properties ONCE per bay, then apply to every floor.
        cols = []
        for b in range(bays_per_section):
            cols.append({
                "balcony": b != ent_bay and rng.random() < 0.62,
                "three_part": rng.random() < 0.34,
                # one owner's glazing decision per column, mostly consistent
                "glazed": rng.random() < BALC_GLAZED_SHARE,
                "infill": rng.random(),
                "pvc": [rng.random() < PVC_SHARE for _ in range(FLOORS)],
            })

        for f in range(FLOORS):
            z = PLINTH_H + f * FLOOR_H
            for b in range(bays_per_section):
                bx = sx + b * bay_w
                c = cols[b]

                # --- stairwell bay ---------------------------------------
                if b == ent_bay:
                    if with_entrances and f == 0:
                        wall(bm, bx, z, bay_w, FLOOR_H)
                        entrance(bm, bx + bay_w / 2, rng)
                        continue
                    # the half-landing window, offset up half a storey: the one
                    # place this building type breaks its own grid
                    ow, oh = 1.05, 1.30
                    oz = z + FLOOR_H * 0.50
                    ox = bx + bay_w / 2 - ow / 2
                    wall(bm, bx, z, bay_w, FLOOR_H, opening=(ox, oz, ow, oh))
                    window(bm, ox, oz, ow, oh, pvc=False)
                    continue

                ow = WIN_W3P if c["three_part"] else WIN_W
                pvc = c["pvc"][f]
                oz = z + SILL_ABOVE_FLOOR

                # --- balcony column: window + balcony door on one slab ----
                if c["balcony"] and f > 0:
                    dw, dh = 0.85, 2.15
                    dz = z + 0.08
                    gap = 0.16
                    total = ow + gap + dw
                    ox = bx + bay_w / 2 - total / 2
                    dx = ox + ow + gap
                    wall(bm, bx, z, bay_w, FLOOR_H,
                         opening=(ox, oz, ow, WIN_H), opening2=(dx, dz, dw, dh))
                    window(bm, ox, oz, ow, WIN_H, pvc=pvc, three_part=c["three_part"])
                    balcony_door(bm, dx, dz, dw, dh, pvc=pvc)
                    balcony(bm, bx + bay_w / 2, z, bay_w - 0.45,
                            c["glazed"], c["infill"])
                    continue

                # --- plain bay ---------------------------------------------
                ox = bx + bay_w / 2 - ow / 2
                wall(bm, bx, z, bay_w, FLOOR_H, opening=(ox, oz, ow, WIN_H))
                window(bm, ox, oz, ow, WIN_H, pvc=pvc, three_part=c["three_part"])


def wall(bm, bx, z, w, h, opening=None, opening2=None):
    """Both layers of real wall for one cell: precast skin + backing."""
    _panel_cell(bm, bx, z, w, h, opening, opening2,
                y0=-PANEL_INSET, t=PANEL_INSET, g=PANEL_GAP)
    _panel_cell(bm, bx, z, w, h, opening, opening2,
                y0=0.0, t=BACKING_T, g=0.0)


def _panel_cell(bm, bx, z, w, h, opening=None, opening2=None,
                y0=None, t=None, g=None):
    """One storey of one bay of WALL, with the openings left as real voids.

    Called twice per cell, which is what makes the hole go all the way through:
      - the outer precast skin, inset with a 22 mm joint around each panel
      - the backing wall behind it, solid edge to edge, same voids
    The Shell used to draw a continuous slab across the facade instead, which
    buried every window inside 220 mm of concrete -- only the bits that
    protruded were visible, and the elevation read as a few boxes stuck on.

    With an opening the cell becomes the panels AROUND the hole: spandrel
    under, lintel band over, a pier each side. That is how the building was
    actually assembled.
    """
    g = PANEL_GAP if g is None else g
    y0 = -PANEL_INSET if y0 is None else y0
    t = PANEL_INSET if t is None else t

    openings = [o for o in (opening, opening2) if o]
    if not openings:
        add_box(bm, (bx + g / 2, y0, z + g / 2), (w - g, t, h - g), M_PANEL)
        return

    openings.sort(key=lambda o: o[0])
    left = bx + g / 2
    right = bx + w - g / 2
    bot = z + g / 2
    top = z + h - g / 2

    # a band under all openings and a band over all of them
    lo = min(o[1] for o in openings) - SILL_T
    hi = max(o[1] + o[3] for o in openings)
    if lo > bot:
        add_box(bm, (left, y0, bot), (right - left, t, lo - bot), M_PANEL)
    if top > hi:
        add_box(bm, (left, y0, hi), (right - left, t, top - hi), M_PANEL)

    # piers: before the first opening, between them, after the last
    edges = [left]
    for ox, _, ow, _ in openings:
        edges += [ox, ox + ow]
    edges.append(right)
    for i in range(0, len(edges) - 1, 2):
        a, b = edges[i], edges[i + 1]
        if b - a > 0.004:
            add_box(bm, (a, y0, lo), (b - a, t, hi - lo), M_PANEL)

    # each opening taller than its neighbours needs its own infill above
    for ox, oz, ow, ohh in openings:
        if oz + ohh < hi:
            add_box(bm, (ox, y0, oz + ohh), (ow, t, hi - (oz + ohh)), M_PANEL)
        if oz - SILL_T > lo:
            add_box(bm, (ox, y0, lo), (ow, t, (oz - SILL_T) - lo), M_PANEL)


def build_gable_facade(bm, width, rng):
    """The blank end wall. Khrushchevki gable ends are almost windowless —
    usually one small stair light per floor, often none at all. Leaving it
    blank but PANELLED is correct and is what makes the seams read."""
    for f in range(FLOORS):
        z = PLINTH_H + f * FLOOR_H
        # end walls use bigger panels: 2 per storey across the depth
        for p in range(2):
            px = p * (width / 2)
            wall(bm, px, z, width / 2, FLOOR_H)
    # a single ventilation slit column, which these end walls do have
    for f in range(1, FLOORS):
        z = PLINTH_H + f * FLOOR_H + 1.9
        add_box(bm, (width / 2 - 0.22, -0.04, z), (0.44, 0.05, 0.28), M_DOOR)


# =============================================================================
# Shell, roof, assembly
# =============================================================================

def build_block(sections, seed):
    """ONE khrushchevka of `sections` sections, as a single multi-material
    mesh. One mesh per building is what the game wants: a draw call costs the
    same whatever the triangle count, so a block that arrives as six objects
    costs six times as much to draw for no gain.

    Origin: centre of the footprint, on the ground, +x along the street
    frontage, so citymap.js can drop it on an OSM centroid and yaw it to the
    footprint's long axis.
    """
    rng = random.Random(seed)
    length = sections * SECTION_W
    height = PLINTH_H + FLOORS * FLOOR_H
    bm = bmesh.new()

    def sub(fn, *args, rot=0.0, at=(0.0, 0.0)):
        """Build a facade in its own bmesh, then fold it in rotated/translated.
        Keeps every facade function working in the same simple local frame:
        local +x along the wall, +y into the building, +z up."""
        tmp = bmesh.new()
        fn(tmp, *args)
        c, sn = math.cos(rot), math.sin(rot)
        for v in tmp.verts:
            x, y = v.co.x, v.co.y
            v.co.x = x * c - y * sn + at[0]
            v.co.y = x * sn + y * c + at[1]
        me = bpy.data.meshes.new("_tmp")
        tmp.to_mesh(me)
        tmp.free()
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    # ---- interior floors so you cannot see straight through the block -------
    for f in range(FLOORS + 1):
        add_box(bm, (BACKING_T, BACKING_T, PLINTH_H + f * FLOOR_H - 0.1),
                (length - 2 * BACKING_T, DEPTH - 2 * BACKING_T, 0.1), M_CONCRETE)
    add_box(bm, (-PLINTH_OUT, -PLINTH_OUT, 0),
            (length + 2 * PLINTH_OUT, DEPTH + 2 * PLINTH_OUT, PLINTH_H), M_CONCRETE)
    add_box(bm, (0, 0, height), (length, DEPTH, 0.18), M_ROOF)

    # ---- the four elevations -------------------------------------------------
    sub(build_long_facade, length, rng, True)                      # street front
    sub(build_long_facade, length, random.Random(seed + 1), False,
        rot=math.pi, at=(length, DEPTH))                           # courtyard rear
    sub(build_gable_facade, DEPTH, rng, rot=-math.pi / 2, at=(0.0, DEPTH))
    sub(build_gable_facade, DEPTH, random.Random(seed + 2),
        rot=math.pi / 2, at=(length, 0.0))

    _build_roof(bm, length, DEPTH, height, rng, sections)

    # centre the origin on the footprint
    for v in bm.verts:
        v.co.x -= length / 2
        v.co.y -= DEPTH / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(col):
    """A kit of block LENGTHS in one glb. citymap.js picks whichever fits the
    real OSM footprint best, so a 26 m block and a 50 m block are both real
    buildings rather than one model stretched."""
    objects = []
    textured = {M_PANEL, M_SEAM, M_CONCRETE}   # everything made of concrete
    mats = [make_material(n, hx, r, m, textured=(i in textured))
            for i, (n, hx, r, m) in enumerate(PALETTE)]
    for i, sections in enumerate(SECTION_VARIANTS):
        bm = build_block(sections, SEED + i * 101)
        me = bpy.data.meshes.new(f"block_{sections}")
        bm.to_mesh(me)
        bm.free()
        obj = bpy.data.objects.new(f"block_{sections}", me)
        col.objects.link(obj)
        for mat in mats:
            obj.data.materials.append(mat)
        _finish(obj, bevel=False)
        # BEFORE the layout offset, so every variant is weathered and mapped
        # in its own frame and the tiles line up across the kit.
        box_uv(obj)
        weather(obj)
        # lay the variants out side by side so the glb is also browsable
        obj.location = (0, i * (DEPTH + 8), 0)
        objects.append(obj)
    return objects


def _build_roof(bm, length, depth, z, rng, sections):
    """Flat roof with a parapet — and the coping overhanging both faces, which
    is what gives the top of the building its shadow line."""
    t = PARAPET_T
    runs = [((0, 0), (length, t)), ((0, depth - t), (length, t)),
            ((0, 0), (t, depth)), ((length - t, 0), (t, depth))]
    for (px, py), (sx, sy) in runs:
        add_box(bm, (px, py, z + 0.18), (sx, sy, PARAPET_H), M_PANEL)
        add_box(bm, (px - COPING_OUT, py - COPING_OUT, z + 0.18 + PARAPET_H),
                (sx + 2 * COPING_OUT, sy + 2 * COPING_OUT, 0.07), M_CONCRETE)

    # thin pipe guardrail above the parapet (typology C.6)
    for i in range(int(length / 2.4)):
        px = 0.6 + i * 2.4
        add_bar(bm, px, depth * 0.5, z + 0.25 + PARAPET_H, z + 0.95 + PARAPET_H,
                0.03, M_METAL)

    # vent stacks and the stair-head box, per section
    for s in range(sections):
        sx = s * SECTION_W
        add_box(bm, (sx + SECTION_W * 0.35, depth * 0.42, z + 0.18),
                (2.6, 3.0, 2.3), M_PANEL)
        add_box(bm, (sx + SECTION_W * 0.35 - 0.06, depth * 0.42 - 0.06, z + 2.48),
                (2.72, 3.12, 0.1), M_CONCRETE)
        for v in range(VENT_COUNT):
            vx = sx + 1.4 + v * 2.3
            add_bar(bm, vx, depth * 0.72, z + 0.18, z + 1.05, 0.26, M_CONCRETE)
            add_box(bm, (vx - 0.2, depth * 0.72 - 0.2, z + 1.05),
                    (0.4, 0.4, 0.07), M_CONCRETE)


def _to_object(name, bm, col):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    col.objects.link(obj)
    return obj


def _finish(obj, bevel=False):
    """Flat shading always — architecture is planar and smooth shading inflates
    it. Bevel only where it earns its triangles (see BEVEL_W)."""
    me = obj.data
    me.polygons.foreach_set("use_smooth", [False] * len(me.polygons))
    me.update()
    if not (BEVEL_ON and bevel):
        return
    mod = obj.modifiers.new("Bevel", "BEVEL")
    mod.width = BEVEL_W
    mod.segments = BEVEL_SEGMENTS
    mod.limit_method = "ANGLE"
    mod.angle_limit = math.radians(70)
    mod.harden_normals = True


def _hash01(*args):
    """Deterministic 0..1 noise from any number of float arrays. The classic
    sine hash: no tables, no seeds to thread around, same answer every build."""
    a = 0.0
    for i, v in enumerate(args):
        a = a + np.asarray(v, dtype=np.float64) * (12.9898, 78.233, 37.719)[i % 3]
    return np.abs(np.sin(a) * 43758.5453) % 1.0


def box_uv(obj, scale=TEX_M):
    """World-aligned box projection, in METRES.

    Architecture is axis-aligned boxes, so the dominant face normal picks the
    plane and the other two coordinates are the UV — every wall gets the same
    grain at the same size whatever direction it faces, and a 2 m tile stays
    2 m instead of stretching over a long block. No unwrap operator, no edit
    mode, nothing that needs a window to be open."""
    me = obj.data
    uv = me.uv_layers.get("UVMap") or me.uv_layers.new(name="UVMap")
    co = {v.index: v.co for v in me.vertices}
    for poly in me.polygons:
        n = poly.normal
        axis = max(range(3), key=lambda i: abs(n[i]))
        for li in poly.loop_indices:
            p = co[me.loops[li].vertex_index]
            if axis == 0:                      # faces +/-X: the gable ends
                u, v = p.y, p.z
            elif axis == 1:                    # faces +/-Y: the long facades
                u, v = p.x, p.z
            else:                              # faces +/-Z: roof, sills, soffits
                u, v = p.x, p.y
            uv.data[li].uv = (u / scale, v / scale)


def weather(obj):
    """Dirt, stains and batch variation as vertex colours — free realism.

    Three things, all of them things you can see on any of these blocks from
    the street:
      * splash-back up the plinth, where rain throws grit off the pavement,
      * a stain under each sill, because a khrushchevka sill has no drip and
        the runoff goes straight down the panel,
      * a per-panel tint, because the panels were cast in different batches
        and no two of them ever matched.
    White means untouched, so the game's per-instance tint keeps working
    exactly as before -- this only ever takes brightness away."""
    me = obj.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    x, z = co[:, 0], co[:, 2]

    f = np.ones(n, dtype=np.float64)
    # --- splash-back up the plinth -------------------------------------------
    t = np.clip(1.0 - z / DIRT_H, 0.0, 1.0) ** 2
    f *= 1.0 - (1.0 - DIRT_MIN) * t * (0.72 + 0.56 * _hash01(x * 0.9, z * 0.9))

    # --- run-off under every sill --------------------------------------------
    drop = np.full(n, 1e9)
    for fl in range(FLOORS):
        d = (PLINTH_H + fl * FLOOR_H + SILL_ABOVE_FLOOR) - z
        drop = np.where((d > 0) & (d < drop), d, drop)
    streak = np.clip(1.0 - drop / STREAK_DROP, 0.0, 1.0)
    # not every window stains, and the ones that do are not even about it
    bay = _hash01(np.floor(x / 1.2) * 7.0 + 0.5)
    f *= 1.0 - (1.0 - STREAK_MIN) * streak * (bay > 0.30) * (0.55 + 0.45 * bay)

    # --- the panels never matched --------------------------------------------
    cell = np.floor(x / 1.2) * 13.0 + np.floor((z - PLINTH_H) / FLOOR_H)
    tint = (_hash01(cell) - 0.5) * 2.0 * PANEL_TINT

    rgba = np.ones((n, 4), dtype=np.float32)
    rgba[:, :3] = np.clip(f + tint, 0.0, 1.0)[:, None]
    attr = me.color_attributes.get("Col") or me.color_attributes.new(
        name="Col", type="FLOAT_COLOR", domain="POINT")
    attr.data.foreach_set("color", rgba.reshape(-1))
    me.color_attributes.active_color = attr
    me.update()


def triangle_count(objects):
    total = 0
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        if obj.type != "MESH":
            continue
        ev = obj.evaluated_get(depsgraph)
        me = bpy.data.meshes.new_from_object(ev)
        me.calc_loop_triangles()
        total += len(me.loop_triangles)
        bpy.data.meshes.remove(me)
    return total


def export(objects, directory, name):
    os.makedirs(directory, exist_ok=True)
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    path = os.path.abspath(os.path.join(directory, name + ".glb"))
    opts = dict(
        filepath=path,
        export_format="GLB",
        use_selection=True,
        export_apply=True,      # bakes the bevel modifier
        export_yup=True,        # three.js convention
        export_materials="EXPORT",
        # ACTIVE, not MATERIAL: the weathering is read by the GAME (glTF
        # multiplies COLOR_0 into the base colour by spec), not by a Color
        # Attribute node here, and MATERIAL mode would drop it on the floor.
        export_vertex_color="ACTIVE",
        export_all_vertex_colors=False,
    )
    # Drop anything this Blender's exporter does not know rather than falling
    # back to a bare export and losing export_yup with it.
    known = set(bpy.ops.export_scene.gltf.get_rna_type().properties.keys())
    unknown = [k for k in opts if k not in known]
    for k in unknown:
        print(f"  ! exporter has no '{k}' in this Blender — skipped")
        opts.pop(k)
    bpy.ops.export_scene.gltf(**opts)
    return path


def main():
    random.seed(SEED)
    noise.seed_set(SEED)
    print("Blender", ".".join(str(v) for v in bpy.app.version))
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")

    col = fresh_collection(COLLECTION_NAME)
    objects = build(col)

    for obj in objects:
        activate(obj)
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    path = export(objects, OUTPUT_DIR, OUTPUT_NAME)

    corners = [obj.matrix_world @ Vector(c) for obj in objects for c in obj.bound_box]
    lo = Vector((min(c[i] for c in corners) for i in range(3)))
    hi = Vector((max(c[i] for c in corners) for i in range(3)))
    size = hi - lo
    print("-" * 64)
    print(f"Sections  : {SECTIONS} x {SECTION_W} m, {FLOORS} floors @ {FLOOR_H} m")
    print(f"Objects   : {len(objects)}")
    for o in objects:
        print(f"    {o.name:16s} {triangle_count([o]):>9,} tris")
    print(f"Triangles : {triangle_count(objects):,}")
    print(f"Size (m)  : {size.x:.2f} x {size.y:.2f} x {size.z:.2f}")
    print(f"Exported  : {path}")
    print("-" * 64)


if __name__ == "__main__":
    main()
