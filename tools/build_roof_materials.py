"""Roof material tiles — the surface a flight game shows most.

Every pitched roof in Tbilisi shared ONE sheet-metal texture and every flat
roof one concrete photo, so a whole district repeated the same ribbing. These
tiles are generated procedurally (roofs are read from 50-400 m, where what
matters is rib pitch, seam rhythm, patina and streaking — not photographic
grain) and land in public/textures/roofs/ with a manifest the game reads the
same way as walls.json.

  python3 tools/build_roof_materials.py

  metal_corrugated  — classic sinusoidal corrugated sheet, rust blooms
  metal_seam        — standing-seam sheet, wide pans, clean ridges
  metal_patched     — mismatched patched sheets, heavy rust, tar smears
  flat_bitumen      — bitumen felt rolls with lapped joints and gravel dust
  flat_screed       — concrete screed with expansion joints and puddle stains
Colour comes from the per-building vertex tint (COTTAGE_ROOFS / FLAT_ROOFS),
so these are deliberately near-greyscale with only mild tonal variation.
"""
import json
import math
import os
import random

import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'public', 'textures', 'roofs')
SIZE = 512
os.makedirs(OUT, exist_ok=True)
random.seed(11)
rng = np.random.default_rng(11)


def noise(scale, octaves=4, size=SIZE):
    """Cheap fbm: upsampled white noise octaves."""
    acc = np.zeros((size, size), np.float32)
    amp = 1.0
    tot = 0.0
    for o in range(octaves):
        n = max(2, int(size / (scale * (2 ** o))))
        small = rng.random((n, n)).astype(np.float32)
        big = np.asarray(Image.fromarray((small * 255).astype(np.uint8))
                         .resize((size, size), Image.BICUBIC), np.float32) / 255
        acc += big * amp
        tot += amp
        amp *= 0.5
    return acc / tot


def streaks(strength=0.12):
    """Vertical weather streaking — rain washes down a roof slope."""
    s = noise(6, 3)
    s = np.asarray(Image.fromarray((s * 255).astype(np.uint8))
                   .filter(ImageFilter.GaussianBlur(1)), np.float32) / 255
    col = s.mean(axis=0, keepdims=True)          # per-column bias
    return 1.0 + (col - col.mean()) * strength * 6


def to_img(g, tint):
    a = np.clip(g, 0, 1)[:, :, None] * np.array(tint, np.float32)[None, None, :]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def metal_corrugated():
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    ribs = 0.5 + 0.5 * np.sin(x / SIZE * math.pi * 2 * 26)      # 26 ribs / tile
    g = 0.62 + 0.30 * ribs ** 1.6
    g *= 0.94 + 0.12 * noise(10)                                 # sheet mottle
    rust = np.clip(noise(4, 5) - 0.58, 0, 1) * 2.2               # rust blooms
    g = g * (1 - 0.45 * rust) + rust * 0.34
    g *= streaks(0.10)
    for r in range(0, SIZE, SIZE // 3):                          # lap joints
        g[r:r + 2] *= 0.72
    return to_img(g, (214, 208, 198))


def metal_seam():
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    pan = (x % (SIZE / 7))
    g = np.full((SIZE, SIZE), 0.80, np.float32)
    g += 0.10 * np.exp(-((pan - 2) ** 2) / 6)                    # raised seam
    g -= 0.06 * np.exp(-((pan - SIZE / 14) ** 2) / 400)          # pan dish
    g *= 0.96 + 0.08 * noise(14)
    g *= streaks(0.07)
    g[np.abs(y - SIZE / 2) < 1.2] *= 0.85                        # ridge line
    return to_img(g, (206, 210, 212))


def metal_patched():
    g = np.full((SIZE, SIZE), 0.74, np.float32)
    for _ in range(9):                                            # mismatched sheets
        x0, y0 = random.randrange(SIZE), random.randrange(SIZE)
        w, h = random.randrange(60, 190), random.randrange(50, 150)
        g[y0:y0 + h, x0:x0 + w] *= random.uniform(0.72, 1.18)
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    g *= 0.90 + 0.16 * (0.5 + 0.5 * np.sin(x / SIZE * math.pi * 2 * 18))
    rust = np.clip(noise(3, 5) - 0.48, 0, 1) * 2.6
    g = g * (1 - 0.6 * rust) + rust * 0.30
    tar = np.clip(noise(8, 3) - 0.66, 0, 1) * 3
    g *= 1 - 0.5 * tar                                            # tar patches
    g *= streaks(0.14)
    return to_img(np.clip(g, 0, 1), (198, 186, 174))


def flat_bitumen():
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    g = np.full((SIZE, SIZE), 0.55, np.float32)
    roll = SIZE / 5
    g *= 0.97 + 0.06 * np.sin(y / roll * math.pi * 2)             # roll bellies
    for r in range(0, SIZE, int(roll)):                           # lapped seams
        g[r:r + 3] *= 0.80
        g[r + 3:r + 6] *= 1.06
    g *= 0.90 + 0.20 * noise(3, 5)                                # gravel dust
    g += 0.10 * np.clip(noise(9, 3) - 0.6, 0, 1) * 3              # bright patches
    return to_img(np.clip(g, 0, 1), (188, 186, 180))


def flat_screed():
    g = np.full((SIZE, SIZE), 0.72, np.float32)
    g *= 0.93 + 0.14 * noise(5, 5)
    for r in range(0, SIZE, SIZE // 4):                           # expansion joints
        g[r:r + 2] *= 0.78
        g[:, r:r + 2] *= 0.78
    pud = np.clip(noise(4, 4) - 0.62, 0, 1) * 2.4                 # puddle stains
    g *= 1 - 0.28 * pud
    return to_img(np.clip(g, 0, 1), (206, 204, 198))


TILES = {
    'metal_corrugated': (metal_corrugated, 'metal', 3.0),
    'metal_seam': (metal_seam, 'metal', 3.5),
    'metal_patched': (metal_patched, 'metal', 3.0),
    'flat_bitumen': (flat_bitumen, 'flat', 6.0),
    'flat_screed': (flat_screed, 'flat', 6.0),
}

manifest = {}
for name, (fn, family, scale) in TILES.items():
    img = fn()
    # seam the tile so it repeats without a visible edge
    a = np.asarray(img).astype(np.float32)
    f = SIZE // 16
    for axis in (0, 1):
        w = np.linspace(0, 1, f, endpoint=False).reshape(-1, 1, 1)
        if axis == 1:
            w = w.reshape(1, -1, 1)
        head = a[:f] if axis == 0 else a[:, :f]
        tail = a[-f:] if axis == 0 else a[:, -f:]
        blend = head * w + tail[::-1] * (1 - w) if axis == 0 else head * w + tail[:, ::-1] * (1 - w)
        if axis == 0:
            a[:f] = blend
        else:
            a[:, :f] = blend
    # Normalise brightness to the ambientCG photo tiles (~168): the roof COLOUR
    # comes from the per-building vertex tint, and the palette was calibrated
    # against those photos — a tile averaging 98 turned rust-red roofs nearly
    # black in the old town.
    TARGET = 168.0
    a = np.clip(a * (TARGET / max(1.0, float(a.mean()))), 0, 255)
    Image.fromarray(a.astype(np.uint8)).save(
        os.path.join(OUT, name + '.jpg'), quality=88)
    manifest[name] = {'file': name + '.jpg', 'family': family, 'scale_m': scale}
    print(f'{name}: {family}, {scale} m tile')

# The two real photo tiles (ambientCG CC0) that the game already shipped stay
# in the rotation — they are better material references than anything drawn
# here, and now they are just two more buckets among the procedural ones.
import shutil
FACADES = os.path.join(HERE, '..', 'public', 'textures', 'facades')
for src, fam, scale in (('roof_flat.jpg', 'flat', 9.0), ('roof_metal.jpg', 'metal', 6.0)):
    p_src = os.path.join(FACADES, src)
    if os.path.exists(p_src):
        shutil.copyfile(p_src, os.path.join(OUT, src))
        manifest[src[:-4]] = {'file': src, 'family': fam, 'scale_m': scale,
                              'credit': 'ambientCG.com, CC0'}
        print(f'{src[:-4]}: {fam}, {scale} m tile (ambientCG photo)')

json.dump(manifest, open(os.path.join(OUT, 'roofs.json'), 'w'), indent=1)
print('manifest:', len(manifest), 'roof tiles ->', OUT)
