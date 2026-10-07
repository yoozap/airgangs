#!/usr/bin/env python3
"""Build real photo facade textures for the free OSM map.

Downloads freely-licensed photos of REAL Tbilisi buildings (Wikimedia
Commons) plus one CC0 curtain-wall texture (ambientCG), perspective-
rectifies the facade region of each photo, crops a repeating window-bay
tile, cross-fades the edges so it wraps seamlessly, and writes game-ready
textures to public/textures/facades/ together with facades.json (UV
repeat + attribution, read by src/citymap.js).

Sources are cached in tools/cache/facades/. Requires: pillow, numpy.
"""
import io
import json
import os
import urllib.request
import zipfile

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, 'cache', 'facades')
OUT = os.path.join(HERE, '..', 'public', 'textures', 'facades')
UA = {'User-Agent': 'TbilisiFly-map-builder/1.0 (contact: sergo93@gmail.com)'}

# Each entry: photo source, facade quad (TL,BL,BR,TR in source px), tile
# crop within the rectified image, and how many 3 m window bays / 3 m
# floors one texture repeat spans (the game sets texture.repeat from it).
FACADES = {
    # Neoclassical plaster house, Old Town (style 0: plain windows)
    'plaster': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/a/a5/Old_Town%2C_Tbilisi%2C_Georgia_%2848808336128%29.jpg'.replace('48808336128', '48808646896'),
        credit='Andrew Milligan sumo, CC BY 2.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Old_Town,_Tbilisi,_Georgia_(48808646896).jpg',
        quad=[(80, 30), (80, 1300), (5540, 1250), (5540, 0)], rect=(2048, 512),
        crop=(1300, 0, 1730, 512), bays=1, floors=1, wb=0.5),
    # Soviet 9-storey panel block, Tsulukidze St (style 1: loggias)
    'panel': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/7/7e/Apartment_Building_on_Aleksandre_Tsulukidze_II_Turn_Street_09.23_%283%29.jpg',
        credit='Supanut Arunoprayote, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Apartment_Building_on_Aleksandre_Tsulukidze_II_Turn_Street_09.23_(3).jpg',
        quad=[(1190, 15), (1160, 3950), (3155, 3950), (3155, 25)], rect=(1024, 2048),
        crop=(448, 32, 944, 900), bays=2, floors=4, wb=0.3),
    # Tan block with wooden loggias, Tsulukidze St (style 1 variant)
    'panel2': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/3/36/Apartment_Building_on_Aleqsandre_Tsulukidze_I_street_09.23.jpg',
        credit='Supanut Arunoprayote, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Apartment_Building_on_Aleqsandre_Tsulukidze_I_street_09.23.jpg',
        quad=[(2620, 0), (2620, 2320), (4640, 2320), (4640, 0)], rect=(1024, 1180),
        crop=(64, 255, 768, 915), bays=2, floors=2, wb=0.65),
    # Ornate XIX-century mint facade, Aghmashenebeli style (style 3: old Tbilisi)
    'oldtbilisi': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/e/e2/Classical_facades_of_Tbilisi.JPG',
        credit='Andriabenia, CC0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Classical_facades_of_Tbilisi.JPG',
        quad=[(260, 650), (285, 2300), (2765, 2300), (2740, 660)], rect=(1536, 1024),
        crop=(330, 20, 1210, 1000), bays=3, floors=2, wb=0.4),
    # 16-storey grey panel tower, Kvachantiradze St (style 1 variant: towers)
    'tower16': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/c/c6/Apartment_Building_on_Davit_Kvachantiradze_Street_09.23.jpg',
        credit='Supanut Arunoprayote, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Apartment_Building_on_Davit_Kvachantiradze_Street_09.23.jpg',
        quad=[(250, 1250), (162, 3770), (1337, 3778), (1350, 1245)], rect=(1024, 2200),
        crop=(0, 75, 1024, 2135), bays=5, floors=14, wb=0.35),
    # Weathered 5-storey khrushchyovka panels, Tsulukidze St (style 1 variant)
    'khrush5': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/65/Apartment_Building_on_Aleksandre_Tsulukidze_II_Turn_Street_09.23_%285%29.jpg',
        credit='Supanut Arunoprayote, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Apartment_Building_on_Aleksandre_Tsulukidze_II_Turn_Street_09.23_(5).jpg',
        quad=[(2993, 825), (2993, 3163), (3745, 3643), (3745, 632)], rect=(768, 1280),
        crop=(0, 30, 768, 1250), bays=3, floors=5, wb=0.3),
    # Ornate XIX-century floor band, 65 Aghmashenebeli Ave (style 3 variant)
    'oldtbilisi2': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/9/99/2025-03-26_65_Davit_Aghmashenebeli_Avenue_%28southern%29.jpg',
        credit='Yuriy75, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:2025-03-26_65_Davit_Aghmashenebeli_Avenue_(southern).jpg',
        quad=[(142, 635), (142, 1520), (3326, 1530), (3326, 640)], rect=(2816, 768),
        crop=(0, 0, 1792, 768), bays=7, floors=1, wb=0.4),
    # Tbilisi Opera: real Moorish striped wall + the ornate entrance arch
    # (used by the opera() landmark model, not by generic buildings)
    'opera_stripes': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/a/af/Tbilisi_Opera_House_%286%29.jpg',
        credit='Vladimer Shioshvili, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Tbilisi_Opera_House_(6).jpg',
        quad=[(700, 890), (700, 2047), (1216, 2047), (1216, 890)], rect=(640, 1280),
        crop=(0, 0, 640, 1280), bays=2, floors=2, wb=0.3),
    'opera_arch': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/a/af/Tbilisi_Opera_House_%286%29.jpg',
        credit='Vladimer Shioshvili, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Tbilisi_Opera_House_(6).jpg',
        quad=[(1647, 860), (1647, 2166), (2492, 2166), (2492, 860)], rect=(768, 1184),
        crop=(0, 0, 768, 1184), bays=3, floors=1, wb=0.3),  # bays=3 only to keep 768px width
    # Marjanishvili McDonald's: Georgian vertical sign, golden arches, stone wall
    'mcd_sign': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6b/Mc_Donalds_Tbilisi_%2810600396135%29.jpg',
        credit='Marco Fieber, CC BY-SA 2.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Mc_Donalds_Tbilisi_(10600396135).jpg',
        quad=[(524, 441), (524, 2520), (887, 2520), (887, 441)], rect=(256, 1462),
        crop=(0, 0, 256, 1462), bays=1, floors=1, wb=0.15),
    'mcd_arches': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6b/Mc_Donalds_Tbilisi_%2810600396135%29.jpg',
        credit='Marco Fieber, CC BY-SA 2.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Mc_Donalds_Tbilisi_(10600396135).jpg',
        quad=[(502, 25), (502, 431), (900, 431), (900, 25)], rect=(256, 256),
        crop=(0, 0, 256, 256), bays=1, floors=1, wb=0.15),
    'mcd_stone': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6b/Mc_Donalds_Tbilisi_%2810600396135%29.jpg',
        credit='Marco Fieber, CC BY-SA 2.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Mc_Donalds_Tbilisi_(10600396135).jpg',
        quad=[(77, 493), (77, 2434), (456, 2434), (456, 493)], rect=(256, 1280),
        crop=(0, 0, 256, 1280), bays=1, floors=2, wb=0.4),
    # Mtatsminda funicular upper station: the real 3-arch loggia front
    'funicular_arcade': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/thumb/archive-funicular02.jpg',
        local='wave_02.jpg',
        credit='Adam Jones, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Funicular02.jpg',
        quad=[(222, 697), (222, 2359), (1736, 2359), (1736, 697)], rect=(1024, 1124),
        crop=(0, 0, 1024, 1124), bays=4, floors=1, wb=0.3),
    # Jvari monastery: weathered ashlar sandstone (also the model reference)
    'jvari_stone': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/thumb/archive-jvari01.jpg',
        local='wave_05.jpg',
        credit='Diego Delso, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Jvari_Monastery_01.jpg',
        quad=[(4038, 2041), (4038, 2647), (4823, 2647), (4823, 2041)], rect=(512, 396),
        crop=(0, 0, 512, 396), bays=2, floors=1, wb=0.35),
    # Tbilisi Marriott, Rustaveli: white classical floors band. Quad hugs the
    # two pedimented bays + balustrade between the street trees — the old
    # wider cut reached into both crowns and tiled vine smears over the whole
    # hotel. norm=182 darkens the overcast-blown source so stucco relief
    # survives (the default normalization only ever lifts).
    'marriott_facade': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/thumb/archive-marriott1.jpg',
        local='wave_04.jpg',
        credit='LoMit, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Hotel_Marriott,_Tbilisi_1.jpg',
        quad=[(2309, 1210), (2309, 1800), (3280, 1800), (3280, 1210)], rect=(1024, 622),
        crop=(0, 0, 1024, 622), bays=2, floors=2, wb=0.35, norm=182),
    # Old weathered brick (sulfur-bath domes / Narikala fortress masonry).
    # RE-SOURCED: the old Leghvtakhevi cut sat on the bath parapet whose
    # diagonal ledge/shadow tiled as black/white zebra stripes on the Narikala
    # tower cylinders; this is the clean windowless red-brick patch from the
    # Marjanishvili photo also used by build_wall_materials.py (brick_red),
    # right edge trimmed to drop a hanging cable.
    'oldbrick': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/thumb/archive-marjanishvili-brick.jpg',
        local='oldbrick_src.jpg',
        credit='Giftzwerg 88, CC BY-SA 3.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Tiflis_Stra%C3%9Fenszene_7.jpg',
        quad=[(1950, 1335), (1950, 1568), (2290, 1568), (2290, 1335)], rect=(512, 352),
        crop=(0, 0, 512, 352), bays=2, floors=1, wb=0.3),
    # Glass curtain wall for modern towers (style 2), already seamless.
    # glassify: the raw tile is pale panes in a WHITE grout grid — mipmaps
    # average it to a featureless slab at flight distance, so post-process it
    # into a high-contrast dark-mullion curtain wall (see glassify()).
    'glass': dict(
        url='https://ambientcg.com/get?file=Facade006_1K-JPG.zip',
        zip_member='Facade006_1K-JPG_Color.jpg',
        credit='ambientCG.com, CC0',
        page='https://ambientcg.com/view?id=Facade006',
        bays=7, floors=8, wb=0.0, glassify=True),
    # Sameba cathedral: real travertine wall + the arched-window band, both
    # cut from one frontal courtyard photo (used by the landmark model).
    'sameba_stone': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6e/Sameba_Cathedral%2C_Courtyard_2%2C_Tbilisi%2C_Georgia.jpg',
        credit='Vyacheslav Argenberg, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Sameba_Cathedral,_Courtyard_2,_Tbilisi,_Georgia.jpg',
        quad=[(4370, 935), (4370, 1115), (4575, 1115), (4575, 935)], rect=(384, 336),
        crop=(0, 0, 384, 336), bays=2, floors=1, wb=0.35, soften=0.55),
    # carved Bolnisi-cross medallion, used as a gable decal on the model
    'sameba_cross': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6e/Sameba_Cathedral%2C_Courtyard_2%2C_Tbilisi%2C_Georgia.jpg',
        credit='Vyacheslav Argenberg, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Sameba_Cathedral,_Courtyard_2,_Tbilisi,_Georgia.jpg',
        quad=[(4365, 670), (4365, 890), (4585, 890), (4585, 670)], rect=(256, 256),
        crop=(0, 0, 256, 256), bays=1, floors=1, wb=0.35),
    'sameba_arches': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/6/6e/Sameba_Cathedral%2C_Courtyard_2%2C_Tbilisi%2C_Georgia.jpg',
        credit='Vyacheslav Argenberg, CC BY 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Sameba_Cathedral,_Courtyard_2,_Tbilisi,_Georgia.jpg',
        quad=[(2990, 545), (2990, 765), (4675, 610), (4675, 335)], rect=(1536, 224),
        crop=(0, 0, 1536, 224), bays=8, floors=1, wb=0.35),
    # Parliament of Georgia, Rustaveli facade: one clean arcade bay (giant
    # column + arch + recess), the sculpted pediment, and plain tuff stone.
    'parl_arcade': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/8/83/Parliament_building._Rustaveli_Avenue%2C_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        credit='Kober, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Parliament_building._Rustaveli_Avenue,_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        quad=[(290, 640), (290, 2010), (760, 2010), (760, 640)], rect=(256, 736),
        crop=(0, 0, 256, 736), bays=2, floors=6, wb=0.3),
    'parl_pediment': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/8/83/Parliament_building._Rustaveli_Avenue%2C_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        credit='Kober, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Parliament_building._Rustaveli_Avenue,_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        quad=[(2160, 30), (2160, 540), (3960, 540), (3960, 30)], rect=(1024, 290),
        crop=(0, 0, 1024, 290), bays=4, floors=1, wb=0.3),
    'parl_stone': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/8/83/Parliament_building._Rustaveli_Avenue%2C_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        credit='Kober, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Parliament_building._Rustaveli_Avenue,_Tbilisi._Georgia%27s_Independence_Day_celebrations_26.05.2017.jpg',
        quad=[(2960, 620), (2960, 880), (3140, 880), (3140, 620)], rect=(256, 368),
        crop=(0, 0, 256, 368), bays=1, floors=1, wb=0.3, soften=0.6),
    # Generic Georgian church template (instanced ~290×): warm tuff ashlar
    # wall and the carved arched-window drum band, both cut from Diego
    # Delso's Sioni Cathedral photos (the archetypal Tbilisi church stone).
    'church_stone': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/0/03/Catedral_de_Sioni%2C_Tiflis%2C_Georgia%2C_2016-09-29%2C_DD_103.jpg',
        credit='Diego Delso, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Catedral_de_Sioni,_Tiflis,_Georgia,_2016-09-29,_DD_103.jpg',
        quad=[(1500, 3910), (1500, 4700), (2500, 4750), (2500, 3960)], rect=(512, 400),
        crop=(0, 0, 512, 400), bays=1.5, floors=1.2, wb=0.3, soften=0.7),
    'church_arches': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/d/dd/Catedral_de_Sioni%2C_Tiflis%2C_Georgia%2C_2016-09-29%2C_DD_95.jpg',
        credit='Diego Delso, CC BY-SA 4.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:Catedral_de_Sioni,_Tiflis,_Georgia,_2016-09-29,_DD_95.jpg',
        quad=[(1460, 4470), (1460, 7230), (3320, 7300), (3320, 4450)], rect=(512, 768),
        crop=(0, 0, 512, 768), bays=3, floors=2.2, wb=0.3),
    # Tbilisi State Concert Hall (Philharmonic): one glazing tier of the
    # renovated teal curtain-wall drum, cut between two silver bands (the
    # quad follows the cylinder's band slope). Used by the landmark model.
    'phil_glass': dict(
        url='https://upload.wikimedia.org/wikipedia/commons/2/2d/MG_1295.JPGd_%2833878022914%29.jpg',
        credit='Max Benidze, CC BY-SA 2.0, via Wikimedia Commons',
        page='https://commons.wikimedia.org/wiki/File:MG_1295.JPGd_(33878022914).jpg',
        quad=[(580, 572), (580, 722), (700, 748), (700, 600)], rect=(512, 256),
        crop=(0, 0, 512, 256), bays=2, floors=1, wb=0.25),
    # Weathered concrete/bitumen for flat Soviet roofs; roofs use planar
    # world UVs with the same 1-unit-per-3m convention, so bays/floors here
    # mean "meters per repeat / 3".
    'roof_flat': dict(
        url='https://ambientcg.com/get?file=Concrete036_1K-JPG.zip',
        zip_member='Concrete036_1K-JPG_Color.jpg',
        credit='ambientCG.com, CC0',
        page='https://ambientcg.com/view?id=Concrete036',
        bays=3, floors=3, wb=0.0),
    # Corrugated sheet metal for pitched/old-town roofs — the per-building
    # red/rust/zinc vertex tints color it.
    'roof_metal': dict(
        url='https://ambientcg.com/get?file=CorrugatedSteel009_1K-JPG.zip',
        zip_member='CorrugatedSteel009_1K-JPG_Color.jpg',
        credit='ambientCG.com, CC0',
        page='https://ambientcg.com/view?id=CorrugatedSteel009',
        bays=2, floors=2, wb=0.0),
}

TARGET_MEAN = 172  # pre-lift brightness: vertex colors darken the map in-game


def fetch(url, name):
    path = os.path.join(CACHE, name)
    if not (os.path.exists(path) and os.path.getsize(path) > 50000):
        print('downloading', url)
        req = urllib.request.Request(url, headers=UA)
        with open(path, 'wb') as f:
            f.write(urllib.request.urlopen(req, timeout=120).read())
    return path


def seamless(arr, fx, fy):
    """Cross-fade opposite edges so the tile wraps without visible seams.

    Each edge column/row is paired with its MIRRORED counterpart across the
    wrap, meeting at a 50/50 mix exactly at the border (so column 0 equals
    column w-1 when tiled) and fading back to the original inward. The old
    version kept the outermost pixels pure original, which preserved the hard
    seam and painted a bright band of the opposite edge next to it.
    """
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


def glassify(arr, floors):
    """Turn the pale curtain-wall tile into a high-contrast one.

    The Facade006 color map is light-teal panes divided by WHITE mullions and
    spandrels; at flight distance mipmapping averages the whole thing into a
    flat teal slab (towers rendered as featureless cubes). Recolor the bright
    separator grid to dark aluminum, guarantee a strong dark slab band at
    every floor cell (one cell = 3 m), and deepen/cool the panes — the dark
    horizontal banding survives far into the mip chain, so towers keep a
    visible floor grid from any distance.
    """
    h, w, _ = arr.shape
    lum = arr.mean(axis=2)
    rmask = lum.mean(axis=1) > lum.mean() + 12  # bright spandrel rows
    cmask = lum.mean(axis=0) > lum.mean() + 12  # bright mullion columns
    slab = np.array([40, 44, 49], np.float32)   # dark slab band
    mull = np.array([56, 61, 66], np.float32)   # dark aluminum mullion
    arr[rmask, :, :] = arr[rmask, :, :] * 0.15 + slab * 0.85
    arr[:, cmask, :] = arr[:, cmask, :] * 0.20 + mull * 0.80
    # every floor cell MUST carry a dark slab band — stamp one where the
    # source had no bright spandrel to recolor
    fh = h / floors
    band = max(6, int(fh * 0.14))
    for k in range(floors):
        y0 = int(k * fh)
        if not rmask[y0:int((k + 1) * fh)].any():
            arr[y0:y0 + band] = arr[y0:y0 + band] * 0.25 + slab * 0.75
    # deepen + cool the glass panes so the bands contrast strongly
    pane = (~rmask)[:, None] & (~cmask)[None, :]
    target = np.array([104, 128, 142], np.float32)  # mid teal-blue
    arr[pane] = np.clip((arr[pane] - arr[pane].mean(0)) * 1.35 + target, 0, 255)
    return arr


def build(only=None):
    os.makedirs(CACHE, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    manifest = {}
    mpath = os.path.join(OUT, 'facades.json')
    if only and os.path.exists(mpath):  # partial rebuild keeps the rest
        with open(mpath) as fp:
            manifest = json.load(fp)
    for name, f in FACADES.items():
        if only and name not in only:
            continue
        if 'local' in f:  # source photo already cached under its own name
            src = os.path.join(CACHE, f['local'])
        else:
            src = fetch(f['url'], name + os.path.splitext(f['url'].split('?')[0])[1][:5])
        if 'zip_member' in f:
            with zipfile.ZipFile(src) as z:
                im = Image.open(io.BytesIO(z.read(f['zip_member']))).convert('RGB')
        else:
            im = Image.open(src).convert('RGB')
            im = im.transform(f['rect'], Image.QUAD, sum(f['quad'], ()),
                              resample=Image.BICUBIC).crop(f['crop'])
        arr = np.asarray(im).astype(np.float32)
        if f.get('soften'):  # flatten local contrast (plain-wall tiles)
            arr = arr.mean() + (arr - arr.mean()) * f['soften']

        if f['wb']:  # pull channel means toward grey (photos have color casts)
            means = arr.reshape(-1, 3).mean(0)
            arr = np.clip(arr * (1 + f['wb'] * (means.mean() / means - 1))[None, None, :], 0, 255)
        if 'norm' in f:
            # overexposed sources: normalize DOWN to this mean (the default
            # path only lifts, leaving blown whites clipped together)
            arr = np.clip(arr * (f['norm'] / arr.mean()), 0, 255)
        else:
            arr = np.clip(arr * max(1.0, min(1.45, TARGET_MEAN / arr.mean())), 0, 255)
        if f.get('glassify'):
            arr = glassify(arr, f['floors'])

        if 'zip_member' not in f:  # ambientCG tiles are seamless already
            # Wall tiles repeat horizontally around whole buildings (repeat
            # 1/bays) — the /24 feather is too narrow for wide multi-bay cuts
            # and leaves bright seam bands at every wrap, so those get a much
            # wider cross-fade. One-shot landmark cuts keep the narrow blend.
            wide = name in ('panel', 'panel2', 'tower16', 'khrush5', 'plaster',
                            'oldtbilisi', 'oldtbilisi2')
            fx = im.width // 12 if wide else im.width // 24
            fy = im.height // 16 if wide else im.height // 24
            arr = seamless(arr.astype(np.uint8), fx, fy)
        out = Image.fromarray(arr.astype(np.uint8))
        scale = min(1024, 256 * f['bays']) / out.width
        if scale < 1:
            out = out.resize((round(out.width * scale), round(out.height * scale)), Image.LANCZOS)
        out.save(os.path.join(OUT, name + '.jpg'), quality=87)
        manifest[name] = {'file': name + '.jpg', 'bays': f['bays'], 'floors': f['floors'],
                          'credit': f['credit'], 'source': f['page']}
        print(name, out.size)

    with open(mpath, 'w') as fp:
        json.dump(manifest, fp, indent=1)
    print('wrote', mpath)


if __name__ == '__main__':
    import sys
    build(only=set(sys.argv[1:]) or None)
