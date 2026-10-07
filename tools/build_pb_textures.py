"""
Cut per-building facade textures from agent-reviewed specs.

Input: one or more spec JSONs (scratchpad/pb_specs/*.json), each a list of
  {bid, lat, lon, photo_title, url, quad: [[x,y]x4 TL,BL,BR,TR], bays, floors,
   credit, skip?: true}
Output: public/textures/pb/b<bid>.jpg (seamless-wrapped, brightness-normalized,
  same processing as fetch_facades.py) + public/textures/pb/manifest.json
  {b<bid>: {lat, lon, file, bays, floors, credit}}
Photos are cached in tools/cache/pb_photos/ by md5 of url.
"""
import glob
import hashlib
import json
import os
import sys
import urllib.request

import numpy as np
from PIL import Image, ImageOps

Image.MAX_IMAGE_PIXELS = None  # Commons panoramas exceed the default bomb guard

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'cache', 'pb_photos')
OUT = os.path.join(HERE, '..', 'public', 'textures', 'pb')
UA = {'User-Agent': 'TbilisiFly-map-builder/1.0 (contact: sergo93@gmail.com)'}
TARGET_MEAN = 172
MIN_YEAR = int(os.environ.get('PB_MIN_YEAR', '2010'))  # refuse older photos
_DATES = os.path.join(HERE, 'cache', 'pb_photo_dates.json')
PHOTO_YEARS = json.load(open(_DATES)) if os.path.exists(_DATES) else {}

os.makedirs(CACHE, exist_ok=True)
os.makedirs(OUT, exist_ok=True)


def fetch(url):
    path = os.path.join(CACHE, hashlib.md5(url.encode()).hexdigest() + '.jpg')
    if not (os.path.exists(path) and os.path.getsize(path) > 30000):
        req = urllib.request.Request(url, headers=UA)
        with open(path, 'wb') as f:
            f.write(urllib.request.urlopen(req, timeout=120).read())
    return path


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


def main(spec_dir):
    manifest_path = os.path.join(OUT, 'manifest.json')
    manifest = json.load(open(manifest_path)) if os.path.exists(manifest_path) else {}
    # backfill lat/lon/url for specs truncated by dying agents
    # every candidate round (geosearch r1-4, category-crawl r5, address-geocoded)
    byid = {}
    for cand_path in sorted(glob.glob(os.path.join(HERE, 'cache', 'pb_candidates*.json'))):
        for e in json.load(open(cand_path)):
            byid.setdefault(e['bid'], e)
    n_ok = n_skip = n_err = n_old = 0
    for path in sorted(glob.glob(os.path.join(spec_dir, '*.json'))):
        try:
            specs = json.load(open(path))
        except Exception as e:  # noqa: BLE001
            print('bad spec file', path, e)
            continue
        for s in specs:
            if s.get('skip'):
                n_skip += 1
                continue
            ref = byid.get(s.get('bid'))
            if ref:
                s.setdefault('lat', ref['lat'])
                s.setdefault('lon', ref['lon'])
                s.setdefault('url', ref['photos'][0]['url'])
            if not (s.get('url') and s.get('quad') and s.get('lat') is not None):
                n_err += 1
                continue
            key = f"b{s['bid']}"
            # RECENCY RULE (user): the game shows Tbilisi as it looks now, so a
            # photo older than PB_MIN_YEAR is refused here, at the one choke
            # point every round passes through. Without this, re-running the
            # cutter over an old spec dir silently resurrects dropped facades.
            yr = PHOTO_YEARS.get(s.get('photo_title') or s.get('title'))
            if yr is not None and yr < MIN_YEAR:
                n_old += 1
                continue
            try:
                raw = Image.open(fetch(s['url']))
                q = s['quad']
                # EXIF-rotated photos have TWO valid coordinate frames and
                # reviewers legitimately used both: Commons reports upright
                # dimensions (and `sips` thumbnails come out upright), but some
                # reviewers mapped their measurements back to the stored
                # buffer. Guessing wrong yields a diagonal smear, so pick the
                # frame the quad actually FITS instead of assuming a convention.
                orient = raw.getexif().get(274, 1)
                upright = ImageOps.exif_transpose(raw)
                if s.get('rotate'):
                    im = raw  # spec says explicitly: measured in the stored frame
                elif orient in (5, 6, 7, 8) and upright.size != raw.size:
                    def fits(img):
                        w, h = img.size
                        return all(-0.02 * w <= x <= 1.02 * w and -0.02 * h <= y <= 1.02 * h
                                   for x, y in q)
                    fr, fu = fits(raw), fits(upright)
                    im = raw if (fr and not fu) else upright
                    if fr and fu:
                        print(f'NOTE {key}: quad fits both frames, assuming upright')
                else:
                    im = upright  # no-op for orientation 1
                im = im.convert('RGB')
                # Bounds guard: a quad measured against a differently-oriented
                # frame lands partly outside the photo and PIL fills the gap
                # with black. Eleven facades shipped as sky-and-black slivers
                # before this check existed — refuse the tile instead.
                W, H = im.size
                if any(x < -0.08 * W or x > 1.08 * W or y < -0.08 * H or y > 1.08 * H
                       for x, y in q):
                    print(f'ERR {key} quad far outside {W}x{H}: {q}')
                    n_err += 1
                    continue
                # small overshoot (a reviewer tracing to the very edge) is
                # clamped: PIL fills anything outside with pure black, which
                # tiles as a dark stripe down every bay of the wall
                q = [[min(max(x, 0), W - 1), min(max(y, 0), H - 1)] for x, y in q]
                w_px = max(abs(q[3][0] - q[0][0]), abs(q[2][0] - q[1][0]))
                h_px = max(abs(q[1][1] - q[0][1]), abs(q[2][1] - q[3][1]))
                # The game caps every pb texture at 512 px on the long side
                # (canvas downscale in citymap.pbMaterial), so writing bigger
                # tiles only cost download time and a resize on every load.
                rw = min(512, max(256, int(w_px)))
                rh = min(768, max(256, int(h_px * rw / max(1, w_px))))
                if rh > 512:
                    rw = max(128, int(rw * 512 / rh))
                    rh = 512
                im = im.transform((rw, rh), Image.QUAD, sum([tuple(p) for p in q], ()),
                                  resample=Image.BICUBIC)
                if s.get('rotate'):  # source photo stored sideways
                    im = im.rotate(s['rotate'], expand=True)
                arr = np.asarray(im).astype(np.float32)
                means = arr.reshape(-1, 3).mean(0)
                arr = np.clip(arr * (1 + 0.3 * (means.mean() / means - 1))[None, None, :], 0, 255)
                arr = np.clip(arr * max(1.0, min(1.45, TARGET_MEAN / arr.mean())), 0, 255)
                # Wide feather: these multi-bay tiles repeat horizontally on the
                # wall (repeat 1/bays), so a narrow blend leaves bright seam
                # bands at every wrap. width/12 x height/16 hides the joins.
                arr = seamless(arr.astype(np.uint8), rw // 12, rh // 16)
                Image.fromarray(arr).save(os.path.join(OUT, key + '.jpg'), quality=85)
                manifest[key] = {'lat': s['lat'], 'lon': s['lon'], 'file': key + '.jpg',
                                 'bays': s['bays'], 'floors': s['floors'],
                                 'credit': s.get('credit', ''),
                                 'title': s.get('photo_title', '')}
                n_ok += 1
            except Exception as e:  # noqa: BLE001
                print('ERR', key, e)
                n_err += 1
    # atomic replace — the game dev-server may fetch mid-bake
    with open(manifest_path + '.tmp', 'w') as f:
        json.dump(manifest, f, ensure_ascii=False)
    os.replace(manifest_path + '.tmp', manifest_path)
    with open(os.path.join(OUT, 'CREDITS.txt'), 'w') as f:
        f.write('Per-building facade photos, via Wikimedia Commons.\n'
                'Cut and color-normalized for use as game textures.\n\n')
        for key in sorted(manifest):
            m = manifest[key]
            parts = [m['file'], m.get('title', ''), m.get('credit', '')]
            f.write(' — '.join(p for p in parts if p) + '\n')
    artists = {m['credit'].split(',')[0] for m in manifest.values() if m.get('credit')}
    print(f'textures: {n_ok} cut, {n_skip} skipped by review, {n_old} refused as pre-{MIN_YEAR}, {n_err} errors; '
          f'manifest total {len(manifest)}, {len(artists)} distinct photographers')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '/tmp/pb_specs')
