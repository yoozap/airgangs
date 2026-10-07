# The inside of a cockpit: seat, harness, stick, panel, pedals, rails.
#
#   Blender -b --python tools/build_cockpit.py
#     -> public/models/cockpit.glb
#
# WHY THIS IS BUILT AND NOT DOWNLOADED. The user asked for a free plane
# interior off the internet. The free libraries (Poly Pizza, the CC0 end of
# Sketchfab) have cockpits, but every one of them is a SCI-FI MECH pod --
# "The cockpit", "Open cockpit design", "Cockpit control center" are all
# space-console furniture, and a mech bridge inside a Spitfire looks worse
# than the empty hole it replaces. The aircraft already in the game are no
# help either: their cockpits are the empty boxes this exists to fill, and
# the only interior parts among them (the Spitfire's `koltuk` seat and
# `zevkcubugu` stick) are two stylised blocks of 900 triangles. So this is
# the same shape of job as the quay kit, the metro pavilions and the belt: a
# few measured primitives -- but PAINTED, because a cockpit you can see into
# through glass is read by its colours before anything else.
#
# FRAME: authored the way tools/build_planes.py authors aircraft -- in Blender
# +Y is FORWARD (toward the nose) and +Z is up, so the glTF export's
# +Y -> -Z mapping lands it in the game's frame (nose -Z, up +Y). The ORIGIN
# is the SEAT PAN, because that is the one point the game knows: planes.js
# measures it as `cockpit.seat` and foot.js puts the pilot's hips there.
#
# SIZE: authored for the 1.8 m personages, so it is placed at scale 1 on every
# aircraft -- the airframes were already grown until a 1.8 m pilot fits.

import math
import os
import sys

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "public", "models", "cockpit.glb")

# metres, all measured off a seated 1.8 m pilot (planes.js PILOT_SIT 0.92)
SEAT_W = 0.46
SEAT_D = 0.44
PANEL_Y = 0.74   # how far forward of the hips the instrument panel stands
PEDAL_Y = 0.96
# The tub is deliberately SHALLOW and TIGHT. These cockpit wells are not
# deep: on the aerobatic the measured floor is level with the seat pan, so
# anything hung below the hips comes straight out through the belly — the
# first build had olive side panels sticking out under the fuselage. Nothing
# in this kit goes below TUB_FLOOR.
TUB_W = 0.60     # inside width of the cockpit tub
TUB_BACK = -0.44
TUB_FRONT = 1.08
TUB_FLOOR = -0.145
SILL = 0.46      # top of the walls — the canopy rail you climb over


def clean():
    bpy.ops.wm.read_factory_settings(use_empty=True)


# ---- the palette ------------------------------------------------------------
# A real cockpit is three or four colours and a lot of small bright ones: an
# olive-green tub, black boxes, brown leather, bare metal -- and then white
# dial faces, red needles, amber and green lamps, a red trigger. The small
# bright ones are what the eye finds through the glass, so they are the ones
# worth modelling.
PALETTE = {
    "ck_tub":      ((0.118, 0.141, 0.098), 0.85, 0.0),   # RAF interior green
    "ck_tub_dark": ((0.072, 0.086, 0.062), 0.9, 0.0),
    "ck_leather":  ((0.215, 0.118, 0.059), 0.62, 0.0),   # tan-brown seat hide
    "ck_leather_d": ((0.135, 0.070, 0.034), 0.7, 0.0),
    "ck_black":    ((0.035, 0.037, 0.040), 0.55, 0.0),   # panel and boxes
    "ck_rubber":   ((0.050, 0.050, 0.052), 0.95, 0.0),
    "ck_steel":    ((0.560, 0.580, 0.610), 0.30, 0.90),
    "ck_alu":      ((0.720, 0.735, 0.750), 0.38, 0.85),
    "ck_brass":    ((0.714, 0.520, 0.212), 0.32, 0.90),
    "ck_webbing":  ((0.470, 0.420, 0.290), 0.92, 0.0),   # tan harness tape
    "ck_dialface": ((0.930, 0.930, 0.915), 0.55, 0.0),   # white instrument faces
    "ck_red":      ((0.720, 0.090, 0.075), 0.45, 0.0),
    "ck_amber":    ((0.880, 0.520, 0.060), 0.40, 0.0),
    "ck_green":    ((0.120, 0.650, 0.240), 0.40, 0.0),
    "ck_white":    ((0.880, 0.885, 0.880), 0.55, 0.0),
    "ck_glass":    ((0.620, 0.780, 0.700), 0.08, 0.0),
}
EMIT = {"ck_red": 0.25, "ck_amber": 0.55, "ck_green": 0.45}


def mat(name):
    m = bpy.data.materials.get(name)
    if m:
        return m
    rgb, rough, metal = PALETTE[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if name in EMIT:
        # the warning lamps and the trigger have to read inside a shadowed
        # tub seen through tinted glass; a little emission is what does it
        b.inputs["Emission Color"].default_value = (*rgb, 1)
        b.inputs["Emission Strength"].default_value = EMIT[name]
    if name == "ck_glass":
        m.blend_method = "BLEND"
        b.inputs["Alpha"].default_value = 0.35
    return m


def box(name, size, loc, m, rot=(0, 0, 0), bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = (size[0] / 2, size[1] / 2, size[2] / 2)
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(scale=True, rotation=True)
    if bevel:
        bm = o.modifiers.new("b", "BEVEL")
        bm.width = bevel
        bm.segments = 2
        bm.limit_method = "ANGLE"
        bpy.ops.object.modifier_apply(modifier="b")
    o.data.materials.append(mat(m))
    return o


def cyl(name, r, h, loc, m, rot=(0, 0, 0), verts=16):
    bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=h, location=loc, vertices=verts)
    o = bpy.context.object
    o.name = name
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(rotation=True)
    o.data.materials.append(mat(m))
    return o


def ball(name, r, loc, m, squash=1.0):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=r, location=loc, segments=16, ring_count=8)
    o = bpy.context.object
    o.name = name
    o.scale = (1, 1, squash)
    bpy.ops.object.transform_apply(scale=True)
    bpy.ops.object.shade_smooth()
    o.data.materials.append(mat(m))
    return o


# ---- the parts --------------------------------------------------------------

def build_tub():
    """THE COMPARTMENT. Not a floor with two low skirts — a closed box: floor,
    two walls that come all the way up to the canopy sill, a bulkhead across
    the front with the instrument panel mounted on it, and another behind the
    seat. Without the front bulkhead the panel hangs in the middle of the
    fuselage with nothing round it, which is exactly what the user saw: "my
    panel is in air inside body"."""
    L = TUB_FRONT - TUB_BACK
    midY = (TUB_FRONT + TUB_BACK) / 2
    wallH = SILL - TUB_FLOOR
    wallZ = (SILL + TUB_FLOOR) / 2
    box("tub_floor", (TUB_W, L, 0.03), (0, midY, TUB_FLOOR), "ck_tub_dark")
    for sx in (-1, 1):
        # the wall, floor to sill, the full length of the compartment
        box(f"tub_side_{sx}", (0.035, L, wallH), (sx * TUB_W / 2, midY, wallZ), "ck_tub")
        # the rail you put a hand on climbing in, capping it
        box(f"tub_sill_{sx}", (0.075, L, 0.045), (sx * TUB_W / 2, midY, SILL), "ck_tub_dark",
            bevel=0.015)
        # formers down the wall, so it is structure and not a slab
        for i, fy in enumerate((-0.28, 0.05, 0.38, 0.72)):
            box(f"tub_rib_{sx}_{i}", (0.05, 0.055, wallH - 0.04),
                (sx * (TUB_W / 2 - 0.035), fy, wallZ), "ck_tub_dark")
        # a cable run along the top of the wall
        cyl(f"tub_loom_{sx}", 0.016, L - 0.12, (sx * (TUB_W / 2 - 0.055), midY, SILL - 0.09),
            "ck_black", rot=(math.radians(90), 0, 0), verts=8)
    # FRONT BULKHEAD — the wall the panel is bolted to, ahead of the pedals so
    # the footwell is inside the box too
    box("tub_front", (TUB_W, 0.04, wallH), (0, TUB_FRONT, wallZ), "ck_tub")
    box("tub_front_rib", (TUB_W - 0.08, 0.05, 0.06), (0, TUB_FRONT - 0.04, SILL - 0.08),
        "ck_tub_dark")
    # REAR BULKHEAD — behind the seat, same height
    box("tub_back", (TUB_W, 0.04, wallH), (0, TUB_BACK, wallZ), "ck_tub")
    box("tub_back_rib", (TUB_W - 0.08, 0.05, 0.06), (0, TUB_BACK + 0.04, SILL - 0.08),
        "ck_tub_dark")
    # the shelf each side of the opening, between the sill and the panel, so
    # looking in from above you meet structure rather than a gap
    for sx in (-1, 1):
        box(f"tub_shelf_{sx}", (0.10, 0.34, 0.03), (sx * (TUB_W / 2 - 0.05), PANEL_Y - 0.18, SILL - 0.02),
            "ck_tub_dark")


def build_seat():
    """Brown leather over a metal bucket, on a tubular frame."""
    box("seat_pan", (SEAT_W, SEAT_D, 0.08), (0, -0.04, -0.04), "ck_leather", bevel=0.025)
    # two cushion seams across the pan
    for i, y in enumerate((-0.12, 0.04)):
        box(f"seat_seam_{i}", (SEAT_W - 0.04, 0.015, 0.015), (0, y, 0.005), "ck_leather_d")
    box("seat_back", (SEAT_W, 0.09, 0.56), (0, -0.26, 0.26), "ck_leather",
        rot=(math.radians(-11), 0, 0), bevel=0.025)
    for i, z in enumerate((0.12, 0.30, 0.46)):
        box(f"seat_back_seam_{i}", (SEAT_W - 0.05, 0.02, 0.014), (0, -0.215 - z * 0.19, z),
            "ck_leather_d", rot=(math.radians(-11), 0, 0))
    box("seat_head", (0.22, 0.10, 0.15), (0, -0.33, 0.58), "ck_leather_d",
        rot=(math.radians(-11), 0, 0), bevel=0.035)
    # the bucket and its frame
    for sx in (-1, 1):
        box(f"seat_wall_{sx}", (0.035, SEAT_D, 0.34), (sx * (SEAT_W / 2 + 0.01), -0.06, 0.12),
            "ck_alu", bevel=0.012)
        cyl(f"seat_leg_{sx}", 0.018, 0.13, (sx * 0.17, -0.10, -0.095), "ck_steel",
            rot=(math.radians(6), 0, 0), verts=10)
        cyl(f"seat_brace_{sx}", 0.015, 0.40, (sx * 0.19, -0.22, 0.14), "ck_steel",
            rot=(math.radians(-11), 0, 0), verts=10)
    box("seat_rail", (SEAT_W + 0.08, 0.05, 0.03), (0, -0.05, -0.135), "ck_steel")


def build_harness():
    """Tan webbing, a chrome box buckle and the red release."""
    for sx in (-1, 1):
        box(f"strap_sh_{sx}", (0.075, 0.025, 0.54), (sx * 0.12, -0.215, 0.29), "ck_webbing",
            rot=(math.radians(-13), 0, 0))
        box(f"strap_lap_{sx}", (0.075, 0.20, 0.022), (sx * 0.14, -0.02, 0.025), "ck_webbing",
            rot=(0, 0, math.radians(sx * 7)))
        box(f"strap_tab_{sx}", (0.045, 0.05, 0.012), (sx * 0.075, 0.035, 0.045), "ck_alu")
    box("buckle", (0.10, 0.08, 0.028), (0, 0.05, 0.05), "ck_alu", bevel=0.008)
    cyl("buckle_release", 0.030, 0.016, (0, 0.05, 0.068), "ck_red", verts=14)


def build_panel():
    """The face a pilot actually looks at: black panel, white dials, red
    needles, three coloured lamps and a row of switches."""
    tilt = math.radians(-14)
    box("panel_face", (0.60, 0.05, 0.36), (0, PANEL_Y, 0.20), "ck_black", rot=(tilt, 0, 0),
        bevel=0.012)
    box("panel_coaming", (0.64, 0.17, 0.055), (0, PANEL_Y - 0.055, 0.41), "ck_leather_d",
        rot=(tilt, 0, 0), bevel=0.028)
    # six instruments: steel bezel, white face, red needle, a glass cover
    for i in range(6):
        cx = (-0.175, 0, 0.175)[i % 3]
        top = i < 3
        cz = 0.30 if top else 0.14
        cy = PANEL_Y - 0.030 - (0.0 if top else 0.040)
        r = 0.064 if i in (1, 4) else 0.056
        cyl(f"dial_bezel_{i}", r, 0.030, (cx, cy, cz), "ck_steel",
            rot=(math.radians(76), 0, 0), verts=22)
        cyl(f"dial_face_{i}", r - 0.011, 0.034, (cx, cy - 0.004, cz), "ck_dialface",
            rot=(math.radians(76), 0, 0), verts=22)
        # tick marks round the face
        for t in range(8):
            a = t * math.pi / 4
            box(f"dial_tick_{i}_{t}",
                (0.005, 0.005, 0.012),
                (cx + math.sin(a) * (r - 0.020), cy - 0.020, cz + math.cos(a) * (r - 0.020)),
                "ck_black", rot=(math.radians(76), 0, a))
        a = (0.7, -1.2, 2.3, -0.6, 1.9, 0.3)[i]
        box(f"needle_{i}", (0.007, 0.007, r - 0.014),
            (cx + math.sin(a) * (r - 0.022) * 0.5, cy - 0.022,
             cz + math.cos(a) * (r - 0.022) * 0.5),
            "ck_red", rot=(math.radians(76), 0, a))
        cyl(f"dial_hub_{i}", 0.010, 0.012, (cx, cy - 0.026, cz), "ck_black",
            rot=(math.radians(76), 0, 0), verts=10)
    # the three lamps: green, amber, red, in a row under the gauges
    for i, col in enumerate(("ck_green", "ck_amber", "ck_red")):
        cyl(f"lamp_{i}", 0.014, 0.022, (-0.22 + i * 0.055, PANEL_Y - 0.065, 0.055), col,
            rot=(math.radians(76), 0, 0), verts=10)
    # toggle switches with a red guard over the first
    for i in range(5):
        x = 0.02 + i * 0.048
        box(f"switch_base_{i}", (0.026, 0.026, 0.020), (x, PANEL_Y - 0.055, 0.055), "ck_alu",
            rot=(math.radians(-14), 0, 0))
        box(f"switch_lever_{i}", (0.008, 0.008, 0.028), (x, PANEL_Y - 0.068, 0.072), "ck_black",
            rot=(math.radians(-14 + (12 if i % 2 else -12)), 0, 0))
    box("switch_guard", (0.034, 0.03, 0.034), (0.02, PANEL_Y - 0.078, 0.078), "ck_red",
        rot=(math.radians(-50), 0, 0))
    # a placard, white on black, where a placard goes
    box("placard", (0.12, 0.012, 0.022), (-0.235, PANEL_Y - 0.052, 0.012), "ck_white",
        rot=(math.radians(-14), 0, 0))
    # the reflector sight on top of the coaming
    box("sight_body", (0.085, 0.13, 0.075), (0, PANEL_Y - 0.09, 0.47), "ck_black", bevel=0.015)
    box("sight_glass", (0.105, 0.012, 0.095), (0, PANEL_Y - 0.165, 0.515), "ck_glass",
        rot=(math.radians(22), 0, 0))


def build_stick():
    """Chrome shaft, black grip, red trigger, leather boot."""
    cyl("stick_shaft", 0.018, 0.40, (0, 0.30, 0.10), "ck_steel", rot=(math.radians(8), 0, 0),
        verts=12)
    cyl("stick_boot", 0.062, 0.055, (0, 0.283, -0.060), "ck_leather_d", verts=16)
    box("stick_grip", (0.056, 0.072, 0.155), (0, 0.327, 0.335), "ck_black",
        rot=(math.radians(8), 0, 0), bevel=0.022)
    ball("stick_top", 0.034, (0, 0.333, 0.415), "ck_black", squash=0.8)
    box("stick_trigger", (0.028, 0.026, 0.045), (0, 0.287, 0.330), "ck_red",
        rot=(math.radians(8), 0, 0))
    cyl("stick_button", 0.011, 0.012, (0, 0.318, 0.405), "ck_amber",
        rot=(math.radians(98), 0, 0), verts=10)


def build_quadrant():
    """Throttle on the left, where a pilot's hand falls."""
    box("thr_box", (0.12, 0.28, 0.10), (-0.30, 0.30, 0.01), "ck_black", bevel=0.018)
    box("thr_slot", (0.025, 0.22, 0.012), (-0.30, 0.30, 0.062), "ck_tub_dark")
    box("thr_lever", (0.022, 0.05, 0.18), (-0.30, 0.245, 0.12), "ck_alu",
        rot=(math.radians(16), 0, 0))
    ball("thr_knob", 0.035, (-0.30, 0.215, 0.205), "ck_red", squash=0.85)
    box("mix_lever", (0.018, 0.04, 0.14), (-0.30, 0.355, 0.10), "ck_alu",
        rot=(math.radians(-8), 0, 0))
    ball("mix_knob", 0.026, (-0.30, 0.368, 0.172), "ck_amber", squash=0.85)
    # trim wheel on the right wall
    cyl("trim_wheel", 0.075, 0.028, (0.31, 0.20, 0.00), "ck_black",
        rot=(0, math.radians(90), 0), verts=20)
    cyl("trim_hub", 0.018, 0.045, (0.31, 0.20, 0.00), "ck_brass",
        rot=(0, math.radians(90), 0), verts=10)


def build_pedals():
    for sx in (-1, 1):
        box(f"pedal_{sx}", (0.105, 0.14, 0.045), (sx * 0.135, PEDAL_Y, -0.085), "ck_alu",
            rot=(math.radians(-24), 0, 0), bevel=0.008)
        box(f"pedal_pad_{sx}", (0.085, 0.10, 0.018), (sx * 0.135, PEDAL_Y - 0.012, -0.058),
            "ck_rubber", rot=(math.radians(-24), 0, 0))
        box(f"pedal_arm_{sx}", (0.030, 0.26, 0.030), (sx * 0.135, PEDAL_Y - 0.17, -0.135),
            "ck_steel")
        cyl(f"pedal_pivot_{sx}", 0.022, 0.05, (sx * 0.135, PEDAL_Y - 0.30, -0.140), "ck_brass",
            rot=(0, math.radians(90), 0), verts=10)
    box("rudder_bar", (0.34, 0.035, 0.035), (0, PEDAL_Y - 0.30, -0.140), "ck_steel")


def build():
    clean()
    build_tub()
    build_seat()
    build_harness()
    build_panel()
    build_stick()
    build_quadrant()
    build_pedals()

    # TWO objects, not one. The furniture is authored at pilot size and goes
    # in at scale 1 everywhere; the TUB is the only part whose width has to
    # answer to the aeroplane around it, so planes.js narrows it to the hull
    # it is going into (never widens it — that is how things end up sticking
    # out of a fuselage).
    def join(prefix, name):
        sel = [o for o in bpy.data.objects
               if o.type == "MESH" and o.name.startswith(prefix)]
        if not sel:
            return None
        bpy.ops.object.select_all(action="DESELECT")
        for o in sel:
            o.select_set(True)
        bpy.context.view_layer.objects.active = sel[0]
        bpy.ops.object.join()
        bpy.context.object.name = name
        return bpy.context.object

    tub = join("tub_", "cockpit_tub")
    rest = [o for o in bpy.data.objects if o.type == "MESH" and o is not tub]
    bpy.ops.object.select_all(action="DESELECT")
    for o in rest:
        o.select_set(True)
    bpy.context.view_layer.objects.active = rest[0]
    bpy.ops.object.join()
    root = bpy.context.object
    root.name = "cockpit_kit"

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", export_yup=True,
                              export_apply=True)
    tris = sum(len(p.vertices) - 2 for p in root.data.polygons)
    if tub:
        tris += sum(len(p.vertices) - 2 for p in tub.data.polygons)
    print(f"COCKPIT built -> {OUT}  {tris} tris, {len(bpy.data.materials)} materials"
          f"{', tub split out' if tub else ''}")


build()
