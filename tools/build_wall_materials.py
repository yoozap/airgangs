"""
Cut windowless WALL MATERIAL tiles (brick / plaster / panel concrete) from the
real Tbilisi photo corpus in tools/cache/pb_photos_raw/.

Unlike build_pb_textures.py (whole facades WITH window grids), these are clean
between-window wall patches — the buildings that carry real 3D kit window
frames use them as their base surface, so the crops must contain NO windows,
sky or vegetation. Quads were hand-recorded from gridded previews of the
exemplar photos named in tools/FACADE_KIT_TYPOLOGY.md.

Output: public/textures/walls/<name>.jpg — a 512px seamless tile covering
exactly 2x2 wall cells of 3 m (scale_m = 6): the material patch is repeated at
its natural grain scale to fill the tile, then a subtle window (dark glazing,
light frame, per-cell brightness jitter) is baked at each 3 m cell center.
The wall UV convention is 1 unit = 3 m, so baked windows land exactly where
citymap.js places the 3D kit frames: close up the frame covers the painted
glass, far away (beyond the window cull / instance cap) the pattern still
reads as fenestration instead of a bare colored box.
walls.json manifest: {name: {file, scale_m}}. CREDITS.txt lists the sources.
"""
import json
import os

import numpy as np
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, 'cache', 'pb_photos_raw')
OUT = os.path.join(HERE, '..', 'public', 'textures', 'walls')
TARGET_MEAN = 168
SIZE = 512    # final tile pixels
CELLS = 2     # 3 m wall cells per tile side
TILE_M = 6.0  # meters covered by one tile repeat (CELLS * 3 m)

os.makedirs(OUT, exist_ok=True)

# quad: [[x,y] TL, BL, BR, TR] in original photo pixels; scale_m here is the
# PATCH's real-world width in meters (estimated from window widths in the same
# photo) — it sets how many times the patch repeats inside the final 6 m tile.
SPECS = {
    'brick_red': {
        'photo': '107619.jpg',  # b107619 — 19c red-brick eclectic, Marjanishvili
        'quad': [[1950, 1335], [1950, 1568], [2360, 1568], [2360, 1335]],
        'scale_m': 1.3,
        'gain_cap': 1.0,
        'flatten': 0.8,
        'frame': (94, 74, 56),
        'credit': 'Giftzwerg 88, CC BY-SA 3.0, via Wikimedia Commons',
        'title': 'File:Tiflis Straßenszene 7.jpg',
    },
    'brick_dark': {
        'photo': '129960.jpg',  # b129960 — brown long-format brick, Orkhevi
        'quad': [[3950, 800], [3950, 1240], [4490, 1240], [4490, 800]],
        'scale_m': 2.5,
        'gain_cap': 1.12,
        'frame': (88, 70, 52),
        'credit': 'Tiia Monto, CC BY-SA 3.0, via Wikimedia Commons',
        'title': 'File:Orkhevi I Lane.jpg',
    },
    'plaster_decay': {
        'photo': '5091.jpg',  # b5091 — plaster spalling to brick (decay ref)
        'quad': [[1440, 1400], [1440, 1748], [1652, 1748], [1652, 1400]],
        'scale_m': 2.2,
        'blend': 5,
        'frame': (126, 112, 94),
        'credit': 'Erekle K, CC BY-SA 4.0, via Wikimedia Commons',
        'title': 'File:Buildings on Chavchavadze Ave 37, Tbilisi.jpg',
    },
    'plaster_yellow': {
        'photo': '27226.jpg',  # b27226 — warm yellow old-town plaster
        'quad': [[1300, 1225], [1300, 1395], [1440, 1395], [1440, 1225]],
        'scale_m': 2.0,
        'flatten': 0.85,
        'punch': 0.3,
        'detrend_v': 0.9,
        'sat': 0.95,
        'frame': (224, 218, 206),
        'credit': 'Adam Jones, CC BY-SA 2.0, via Wikimedia Commons',
        'title': 'File:Architectural Detail - Old Town - Tbilisi - Georgia',
    },
    'plaster_pink': {
        'photo': '3470.jpg',  # b3470 — pink plaster (school 156)
        'quad': [[7130, 1840], [7130, 2040], [7400, 2040], [7400, 1840]],
        'scale_m': 1.5,
        'flatten': 0.5,
        'punch': 0.8,
        'frame': (228, 224, 216),
        'credit': 'David Osipov, CC BY 4.0, via Wikimedia Commons',
        'title': 'File:Tbilisi public school 156. Back view.jpg',
    },
    'panel_concrete': {
        'photo': '11614.jpg',  # b11614 — Soviet panel cell with seam grid
        'quad': [[2300, 650], [2300, 1140], [2462, 1140], [2462, 650]],
        'scale_m': 1.35,
        'punch': 0.35,
        'frame': (238, 238, 234),
        'credit': 'Supanut Arunoprayote, CC BY 4.0, via Wikimedia Commons',
        'title': 'File:Apartment Building on Aleksandre Tsulukidze II Turn Street.jpg',
    },
}


def seamless(arr, fx, fy):
    """Mirror-paired edge cross-fade: opposite edges meet at a 50/50 mix at the
    wrap (col 0 == col w-1 when tiled) and fade to the original inward. The old
    blend kept the outermost pixels pure original — hard seam + bright band."""
    h, w, _ = arr.shape
    out = arr.astype(np.float32)
    if fx:
        wl = (0.5 + 0.5 * np.linspace(0, 1, fx, endpoint=False))[None, :, None]
        left, right = out[:, :fx].copy(), out[:, w - fx:].copy()
        out[:, :fx] = left * wl + right[:, ::-1] * (1 - wl)
        out[:, w - fx:] = right * wl[:, ::-1] + left[:, ::-1] * (1 - wl[:, ::-1])
    if fy:
        wl = (0.5 + 0.5 * np.linspace(0, 1, fy, endpoint=False))[:, None, None]
        top, bot = out[:fy].copy(), out[h - fy:].copy()
        out[:fy] = top * wl + bot[::-1] * (1 - wl)
        out[h - fy:] = bot * wl[::-1] + top[::-1] * (1 - wl[::-1])
    return np.clip(out, 0, 255).astype(np.uint8)


# Real photographed windows (tools/cache/window_crops.json, cut from the pb
# facade set by an agent) replace the DRAWN window below wherever a crop of a
# matching kind exists — a painted window is the last synthetic thing on an
# otherwise photographic wall.
WIN_CROPS = {}
_wc = os.path.join(HERE, 'cache', 'window_crops.json')
if os.path.exists(_wc):
    PB = os.path.join(HERE, '..', 'public', 'textures', 'pb')
    for e in json.load(open(_wc)):
        p_img = os.path.join(PB, e['file'])
        if not os.path.exists(p_img):
            continue
        try:
            WIN_CROPS[e['kind']] = Image.open(p_img).convert('RGB').crop(tuple(e['box']))
        except Exception as err:  # noqa: BLE001
            print('window crop failed', e.get('kind'), err)
    print(f'{len(WIN_CROPS)} real window crops loaded')

# which crop suits which wall family (falls back to any available)
WIN_FOR_FAMILY = {
    'brick': ['win_brick', 'win_grille', 'win_old'],
    'plaster': ['win_classic', 'win_pvc', 'win_old'],
    'stone': ['win_classic', 'win_old', 'win_brick'],
    'panel': ['win_soviet', 'win_pvc', 'win_dark'],
}


def pick_window(family, seed):
    if not WIN_CROPS:
        return None
    order = WIN_FOR_FAMILY.get(family, [])
    have = [WIN_CROPS[k] for k in order if k in WIN_CROPS] or list(WIN_CROPS.values())
    return have[seed % len(have)]


def bake_windows(tile, seed, frame_rgb, family='plaster'):
    """Paint one subtle window per 3 m cell (dark glazing, light frame, faint
    mullion cross, lintel shadow + sill) with per-cell brightness jitter.
    Image row 0 is texture TOP, so 'above the window' = smaller row index."""
    px = SIZE // CELLS
    ppm = px / 3.0
    ww, wh = int(1.4 * ppm), int(1.5 * ppm)
    f = np.array(frame_rgb, np.float32)
    for cy in range(CELLS):
        for cx in range(CELLS):
            j = ((cx * 7349 + cy * 15877 + seed) % 97) / 97.0
            xc = cx * px + px // 2
            yc = cy * px + px // 2
            x0, x1 = xc - ww // 2, xc + ww // 2
            y0, y1 = yc - wh // 2, yc + wh // 2
            fr = 4
            bd = 3
            # wall tone sampled BEFORE the dark surround bake so the pane keeps
            # its close-range softening calibration (see comment below)
            wallc = tile[y0:y1, x0:x1].reshape(-1, 3).mean(0)
            # DARK reveal ring + heavier lintel: a light frame alone vanishes
            # into pale panel walls beyond ~500 m — the dark border is what the
            # mips average into a readable window grid at distance, without
            # re-darkening the deliberately softened pane.
            tile[y0 - fr - bd - 4:y0 - fr - bd, x0 - fr:x1 + fr] *= 0.62  # lintel
            tile[y0 - fr - bd:y1 + fr + bd, x0 - fr - bd:x1 + fr + bd] *= 0.55
            tile[y0 - fr:y1 + fr, x0 - fr:x1 + fr] = (
                tile[y0 - fr:y1 + fr, x0 - fr:x1 + fr] * 0.25 + f * 0.75)
            real = pick_window(family, seed + cx * 3 + cy * 7)
            if real is not None:
                # paste the photographed window into the opening, matched to
                # the tile's exposure so it doesn't read as a sticker
                w2, h2 = x1 - x0, y1 - y0
                rw2 = np.asarray(real.resize((max(2, w2), max(2, h2)), Image.LANCZOS),
                                 np.float32)
                cur = tile[y0:y1, x0:x1]
                gain = (cur.mean() + 1) / (rw2.mean() + 1)
                gain = min(1.45, max(0.7, gain)) * (0.9 + 0.2 * j)
                rw2 = np.clip(rw2 * gain, 0, 255)
                # Feather the outer border into the wall: the crop carries its
                # own surround from the source photo, and a hard edge reads as
                # a sticker pasted on a different material.
                fh = max(2, h2 // 7)
                fw = max(2, w2 // 7)
                ay = np.ones((h2, 1), np.float32)
                ay[:fh, 0] = np.linspace(0, 1, fh)
                ay[h2 - fh:, 0] = np.linspace(1, 0, fh)
                ax = np.ones((1, w2), np.float32)
                ax[0, :fw] = np.linspace(0, 1, fw)
                ax[0, w2 - fw:] = np.linspace(1, 0, fw)
                alpha = (ay * ax)[:, :, None] ** 0.7
                tile[y0:y1, x0:x1] = cur * (1 - alpha) + rw2 * alpha
                continue
            # Pane: SUBTLE dusty glazing, not a pristine navy sheet. The old
            # near-black pane read as "dark glass patches pasted on concrete"
            # wherever the 3D frame budget thinned out (and at grazing angles,
            # where the protruding frame parallax-uncovers it) — so pull the
            # glazing toward the local wall tone and keep some wall grain.
            pane = np.array((58, 66, 78), np.float32) * (0.85 + 0.45 * j)
            pane = pane * 0.6 + wallc * 0.4
            grad = np.linspace(1.35, 0.85, y1 - y0)[:, None, None]  # sky at top
            tile[y0:y1, x0:x1] = (
                tile[y0:y1, x0:x1] * 0.22 + pane[None, None, :] * grad * 0.78)
            mx = (x0 + x1) // 2
            my = y0 + (y1 - y0) // 2
            tile[y0:y1, mx - 1:mx + 1] = f * 0.92  # mullion cross
            tile[my - 1:my + 1, x0:x1] = f * 0.92
            tile[y1 + fr + bd:y1 + fr + bd + 3, x0 - 3:x1 + 3] = (
                tile[y1 + fr + bd:y1 + fr + bd + 3, x0 - 3:x1 + 3] * 0.35 + f * 0.65)
    np.clip(tile, 0, 255, out=tile)
    # Align with the 3D kit frames: citymap places them at y = base + 3f + 1.5
    # while wall v=0 sits at ground = base + 2, so frame centers land at
    # v = 5/6 of each floor cell (not 1/2). Roll the whole seamless tile up
    # by a third of a cell so the baked glazing hides exactly under a frame.
    return np.roll(tile, -round(px / 3), axis=0)


FAMILIES = {  # existing six, by material family (agent specs carry their own)
    'brick_red': 'brick', 'brick_dark': 'brick', 'plaster_decay': 'plaster',
    'plaster_yellow': 'plaster', 'plaster_pink': 'plaster', 'panel_concrete': 'panel',
}

# Extra tiles picked by an agent from the cached photo corpus
# (tools/cache/wall_specs_new.json, see prompt_walls.txt). Merged here so the
# hand-tuned six above keep their calibration knobs.
_extras = [os.path.join(HERE, 'cache', f) for f in
           ('wall_specs_new.json', 'wall_specs_new2.json')]
for _extra in _extras:
    if not os.path.exists(_extra):
        continue
    for e in json.load(open(_extra)):
        nm = e['name']
        if nm in SPECS:
            continue
        SPECS[nm] = {
            'photo': e['photo'], 'quad': e['quad'], 'scale_m': e['scale_m'],
            'frame': tuple(e['frame']), 'credit': e.get('credit', ''),
            'title': e.get('title', ''),
            # generic defaults: mild flatten so tiling bands don't show,
            # a little grain punch for smooth surfaces
            'flatten': 0.6, 'punch': 0.35,
        }
        FAMILIES[nm] = e.get('family', 'plaster')
    print(f'merged {len(SPECS) - 6} agent-picked tiles')


def main():
    manifest = {}
    for name, s in SPECS.items():
        im = Image.open(os.path.join(RAW, s['photo'])).convert('RGB')
        q = s['quad']
        w_px = max(abs(q[3][0] - q[0][0]), abs(q[2][0] - q[1][0]))
        h_px = max(abs(q[1][1] - q[0][1]), abs(q[2][1] - q[3][1]))
        # fit the longer side to 512, keep aspect (rounded to /64 for mips)
        if w_px >= h_px:
            rw, rh = 512, max(128, round(512 * h_px / max(1, w_px) / 64) * 64)
        else:
            rh, rw = 512, max(128, round(512 * w_px / max(1, h_px) / 64) * 64)
        im = im.transform((rw, rh), Image.QUAD, sum([tuple(p) for p in q], ()),
                          resample=Image.BICUBIC)
        arr = np.asarray(im).astype(np.float32)
        # VERY mild white balance (0.1 — these tiles must keep their real hue:
        # brick red, yellow/pink plaster) + brightness lift toward game mean
        means = arr.reshape(-1, 3).mean(0)
        arr = np.clip(arr * (1 + 0.1 * (means.mean() / means - 1))[None, None, :], 0, 255)
        arr = np.clip(arr * max(1.0, min(s.get('gain_cap', 1.35), TARGET_MEAN / arr.mean())), 0, 255)
        # optional illumination flatten: divide out broad sun/shadow gradients
        # so smooth plaster doesn't tile as visible bright/dark banding
        k = s.get('flatten', 0)
        # AUTO de-blotch: a patch with strong LOW-FREQUENCY variation (peeling
        # paint, sun/shade halves) turns into a visible checkerboard once it is
        # tiled 3x2 and mip-blurred — plaster_teal read as green-and-white
        # squares across a whole hillside. Measure the blotchiness on 8x8 block
        # means and raise `flatten` toward 1 as it grows.
        blocks = arr[:rh // 8 * 8, :rw // 8 * 8].reshape(
            8, rh // 8, 8, rw // 8, 3).mean(axis=(1, 3))
        blotch = float(blocks.reshape(-1, 3).std(axis=0).mean())
        if blotch > 9:
            k = max(k, min(0.95, 0.55 + (blotch - 9) * 0.05))
            print(f'  {name}: blotchy patch (sigma {blotch:.1f}) -> flatten {k:.2f}')
        if k:
            blur = Image.fromarray(arr.astype(np.uint8)).filter(
                ImageFilter.GaussianBlur(min(rw, rh) // 4))
            b = np.asarray(blur).astype(np.float32)
            flat = arr / np.maximum(b, 1) * b.reshape(-1, 3).mean(0)[None, None, :]
            arr = np.clip(k * flat + (1 - k) * arr, 0, 255)
        # optional row detrend: equalize per-row brightness (smooth plaster
        # only) so the seamless wrap can't leave a repeating horizontal band
        kd = s.get('detrend_v', 0)
        if kd:
            rows = arr.mean(axis=(1, 2), keepdims=True)
            arr = np.clip(arr + kd * (rows.mean() - rows), 0, 255)
        # optional grain punch: large-radius unsharp so smooth plaster still
        # reads as a real surface (roughcast, trowel marks) instead of paint
        kp = s.get('punch', 0)
        if kp:
            sm = Image.fromarray(arr.astype(np.uint8)).filter(ImageFilter.GaussianBlur(6))
            arr = np.clip(arr + kp * (arr - np.asarray(sm).astype(np.float32)), 0, 255)
        # restore a bit of the punch the haze/brightening washes out
        grey = arr.mean(axis=2, keepdims=True)
        arr = np.clip(grey + (arr - grey) * s.get('sat', 1.18), 0, 255)
        fdiv = s.get('blend', 8)  # seam blend width divisor (smaller = wider)
        patch = seamless(arr.astype(np.uint8), rw // fdiv, rh // fdiv)
        # ---- assemble the 6x6 m tile: repeat the patch at natural grain -----
        # scale, then bake a window at every 3 m cell center so distant or
        # window-capped buildings still read as fenestrated, not bare boxes
        pw_m = s['scale_m']            # physical size of the cut patch
        ph_m = pw_m * rh / rw
        rx = int(np.clip(round(TILE_M / pw_m), 1, 8))
        ry = int(np.clip(round(TILE_M / ph_m), 1, 8))
        big = np.tile(patch, (ry, rx, 1))
        tile = np.asarray(Image.fromarray(big).resize((SIZE, SIZE),
                          Image.LANCZOS)).astype(np.float32)
        # FINAL de-blotch, measured on the assembled tile: `flatten` above only
        # divides out a very broad gradient, but mid-frequency blotching (the
        # peeling-paint patches) survives tiling and reads as a chequerboard
        # across a whole hillside. Pull the tile's LOW-FREQUENCY component
        # toward its mean while keeping all the fine grain.
        low = np.asarray(Image.fromarray(tile.astype(np.uint8))
                         .filter(ImageFilter.GaussianBlur(SIZE // 12)), np.float32)
        excess = float(low.reshape(-1, 3).std(axis=0).mean())
        if excess > 8:
            pull = min(0.85, (excess - 8) / 26)
            tile = np.clip(tile - pull * (low - low.reshape(-1, 3).mean(axis=0)), 0, 255)
            print(f'  {name}: tile blotch sigma {excess:.1f} -> levelled {pull:.0%}')
        tile = bake_windows(tile, sum(map(ord, name)), s['frame'],
                            FAMILIES.get(name, 'plaster'))
        Image.fromarray(tile.astype(np.uint8)).save(
            os.path.join(OUT, name + '.jpg'), quality=88)
        manifest[name] = {'file': name + '.jpg', 'scale_m': TILE_M,
                          'family': FAMILIES.get(name, 'plaster')}
        print(f'{name}: patch {rw}x{rh} ({pw_m:.2f}x{ph_m:.2f} m) tiled {rx}x{ry} from {s["photo"]}')
    with open(os.path.join(OUT, 'walls.json'), 'w') as f:
        json.dump(manifest, f, indent=1)
    with open(os.path.join(OUT, 'CREDITS.txt'), 'w') as f:
        f.write('Windowless wall material tiles (brick/plaster/panel), cut from\n'
                'Wikimedia Commons photos and made seamless for game texturing.\n\n')
        for name, s in SPECS.items():
            f.write(f"{name}.jpg — {s['title']} — {s['credit']}\n")
    print('manifest:', len(manifest), 'tiles')


if __name__ == '__main__':
    main()
