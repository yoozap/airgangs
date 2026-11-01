#!/usr/bin/env python3
"""
build_tile_normals.py — generate normal + roughness/metallic maps for the 31
wall/roof albedo tiles that CityMaterials.cs binds (24 wall + 7 roof).

Why this exists: only ALBEDO was ever baked for these tiles (CityMaterials.cs's
own doc comment: "_BumpMap is left unset by design... a fixed per-family
_Smoothness stands in for a roughness map"). docs/QUALITY_BAR.md calls flat-lit
untextured walls "the single biggest remaining visual gap". This closes it with
a standard height-from-luminance -> normal derivation (PIL/numpy only — no
external normal-baking tool is available in this environment), plus a
per-pixel metallic/smoothness MODULATION map so the lighting isn't perfectly
flat even where the albedo photo itself is fairly uniform.

Reads:  unity/Assets/Resources/Art/Textures/walls/walls.json, roofs/roofs.json (for the
        real "family" of each tile — brick/stone/plaster/panel/metal/flat —
        which sets how deep the embossing is and the metallic/smoothness base).
Writes, next to each <stem>.jpg:
        <stem>_n.png    tangent-space normal map (RGB, OpenGL/Unity convention)
        <stem>_mgs.png  metallic (R) / smoothness (A) MODULATION map — a
                        multiplier around 1.0 that CityMaterials.cs applies on
                        top of its existing per-family _Metallic/_Smoothness
                        constants, so a material with no map bound (anything
                        this script hasn't touched) renders EXACTLY as before.
Also hand-writes a matching Unity .meta file for each new PNG (textureType=1
NormalMap + sRGBTexture=0 for the _n map, textureType=0 + sRGBTexture=0 for
the _mgs map — both carry linear data, never sRGB-decoded) since no agent may
run the Unity editor to let AssetDatabase generate one; Unity picks these up
verbatim on its next refresh, the same way every hand-authored asset in this
port already does.

Run: python3 tools/build_tile_normals.py
"""
import json
import os
import struct
import sys
import uuid
import zlib

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WALL_DIR = os.path.join(ROOT, "unity/Assets/Resources/Art/Textures/walls")
ROOF_DIR = os.path.join(ROOT, "unity/Assets/Resources/Art/Textures/roofs")

# Per-family tuning. "depth" sets how strongly luminance gradients bend the
# normal (deep mortar joints for brick/stone, a near-flat skin for panel/flat
# roofing). "metallic_base"/"rough_var" set the _mgs.png content.
FAMILY = {
    "brick":   dict(depth=3.6, blur=0.6, metallic=0.03, rough_var=0.35),
    "stone":   dict(depth=4.2, blur=0.8, metallic=0.02, rough_var=0.40),
    "plaster": dict(depth=1.4, blur=1.1, metallic=0.02, rough_var=0.22),
    "panel":   dict(depth=1.8, blur=0.9, metallic=0.04, rough_var=0.28),
    "metal":   dict(depth=2.6, blur=0.4, metallic=0.35, rough_var=0.45),
    "flat":    dict(depth=1.1, blur=1.2, metallic=0.05, rough_var=0.18),
}
DEFAULT_FAMILY = "plaster"


def load_manifest(path):
    if not os.path.exists(path):
        return {}
    with open(path) as f:
        return json.load(f)


def height_from_luminance(img, blur_sigma):
    """Grayscale height proxy: blurred luminance, normalised 0..1."""
    gray = img.convert("L")
    if blur_sigma > 0:
        gray = gray.filter(ImageFilter.GaussianBlur(radius=blur_sigma))
    h = np.asarray(gray, dtype=np.float32) / 255.0
    return h


def sobel_gradients(h):
    """3x3 Sobel, edge-replicated at the border (the tile wraps when used —
    a hard zero border would seam-light at the tile edge)."""
    hp = np.pad(h, 1, mode="edge")
    gx = (
        hp[0:-2, 2:] + 2 * hp[1:-1, 2:] + hp[2:, 2:]
        - hp[0:-2, 0:-2] - 2 * hp[1:-1, 0:-2] - hp[2:, 0:-2]
    )
    gy = (
        hp[2:, 0:-2] + 2 * hp[2:, 1:-1] + hp[2:, 2:]
        - hp[0:-2, 0:-2] - 2 * hp[0:-2, 1:-1] - hp[0:-2, 2:]
    )
    return gx, gy


def make_normal_map(img, depth, blur_sigma):
    h = height_from_luminance(img, blur_sigma)
    gx, gy = sobel_gradients(h)
    # Tangent-space normal: steeper gradient -> more tilt away from (0,0,1).
    nx = -gx * depth
    ny = -gy * depth
    nz = np.ones_like(nx)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx, ny, nz = nx / length, ny / length, nz / length
    r = np.clip((nx * 0.5 + 0.5) * 255.0, 0, 255).astype(np.uint8)
    g = np.clip((ny * 0.5 + 0.5) * 255.0, 0, 255).astype(np.uint8)  # +Y up (OpenGL/Unity)
    b = np.clip((nz * 0.5 + 0.5) * 255.0, 0, 255).astype(np.uint8)
    return Image.fromarray(np.dstack([r, g, b]))


def make_mgs_map(img, metallic_base, rough_var, blur_sigma):
    """R = metallic multiplier, A = smoothness multiplier, both centred on 1.0
    so CityMaterials' existing family constant is unchanged wherever this
    texture is flat. Derived from local contrast: a locally rougher-looking
    patch (higher local std-dev of luminance, e.g. grime/mortar/rust) reads
    LESS smooth and (for the metal family) less uniformly metallic."""
    gray = img.convert("L")
    arr = np.asarray(gray, dtype=np.float32) / 255.0
    # local std-dev via a small box window, using an integral-image trick so
    # this stays fast at whatever resolution the tile is baked at.
    win = 5
    mean = np.asarray(
        Image.fromarray((arr * 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(win)),
        dtype=np.float32,
    ) / 255.0
    sq = np.asarray(
        Image.fromarray(np.clip(arr * arr * 255, 0, 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(win)),
        dtype=np.float32,
    ) / 255.0
    var = np.clip(sq - mean * mean, 0.0, 1.0)
    local_rough = np.sqrt(var)  # 0 = flat/uniform, higher = busy/grimy
    # normalise against the tile's own range so every tile uses its full span
    lo, hi = local_rough.min(), max(local_rough.max(), 1e-4)
    norm = (local_rough - lo) / (hi - lo)

    smoothness_mul = 1.0 + rough_var * (0.5 - norm)  # busier -> rougher (lower)
    metallic_mul = 1.0 + (0.4 if metallic_base > 0.1 else 0.15) * (0.5 - norm)

    a = np.clip(smoothness_mul * 190.0, 0, 255).astype(np.uint8)  # ~0.74 mid-grey = x1.0
    r = np.clip(metallic_mul * 190.0, 0, 255).astype(np.uint8)
    g = np.zeros_like(r)
    b = np.zeros_like(r)
    return Image.fromarray(np.dstack([r, g, b, a]))


def write_meta(png_path, texture_type):
    """Hand-authored Unity .meta — textureType 1 = NormalMap, 0 = Default.
    Both are sRGB-off (linear data channel-packed/normal-encoded, never
    gamma-decoded). Mirrors the shape of the project's existing .meta files
    (see brick_red.jpg.meta) trimmed to the fields that matter; Unity fills
    in the rest with defaults on next import."""
    meta_path = png_path + ".meta"
    if os.path.exists(meta_path):
        return  # never clobber a GUID that's already in use
    guid = uuid.uuid4().hex
    convert_to_normal = 0  # we already AUTHORED a real normal map — don't re-derive one from it
    body = f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 0
    linearTexture: 1
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: {convert_to_normal}
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: {texture_type}
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""
    with open(meta_path, "w") as f:
        f.write(body)


def process_dir(tex_dir, manifest_name):
    manifest = load_manifest(os.path.join(tex_dir, manifest_name))
    results = []
    for fname in sorted(os.listdir(tex_dir)):
        if not fname.lower().endswith(".jpg"):
            continue
        stem = fname[:-4]
        entry = manifest.get(stem, {})
        family = entry.get("family", DEFAULT_FAMILY)
        tune = FAMILY.get(family, FAMILY[DEFAULT_FAMILY])

        src_path = os.path.join(tex_dir, fname)
        img = Image.open(src_path).convert("RGB")

        normal_img = make_normal_map(img, tune["depth"], tune["blur"])
        mgs_img = make_mgs_map(img, tune["metallic"], tune["rough_var"], tune["blur"])

        n_path = os.path.join(tex_dir, f"{stem}_n.png")
        mgs_path = os.path.join(tex_dir, f"{stem}_mgs.png")
        normal_img.save(n_path)
        mgs_img.save(mgs_path)
        write_meta(n_path, texture_type=1)   # NormalMap
        write_meta(mgs_path, texture_type=0)  # Default (linear, channel-packed)

        results.append((stem, family, img.size))
    return results


def main():
    print("== walls ==")
    wall_results = process_dir(WALL_DIR, "walls.json")
    for stem, family, size in wall_results:
        print(f"wall {stem}: family={family} size={size[0]}x{size[1]} -> {stem}_n.png, {stem}_mgs.png")

    print("== roofs ==")
    roof_results = process_dir(ROOF_DIR, "roofs.json")
    for stem, family, size in roof_results:
        print(f"roof {stem}: family={family} size={size[0]}x{size[1]} -> {stem}_n.png, {stem}_mgs.png")

    total = len(wall_results) + len(roof_results)
    print(f"\nTOTAL tiles processed: {total} (expect 24 wall + 7 roof = 31)")
    print(f"wall tiles: {len(wall_results)}  roof tiles: {len(roof_results)}")
    print(f"new files written: {total * 4} (normal + mgs PNG + 2 .meta each)")


if __name__ == "__main__":
    main()
