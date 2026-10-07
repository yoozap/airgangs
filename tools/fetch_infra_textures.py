"""Download the CC0 ambientCG PBR sets the river embankment is built from.

  python3 tools/fetch_infra_textures.py

Writes public/textures/<name>{,_nor,_rough}.jpg, the same triple the roads and
tunnels already use, so the kit materials can be wired up the same way.

Chosen against photographs of the Mtkvari through the centre:
  quaywall   Bricks066 — large pale ashlar blocks with deep recessed joints.
             Filmed from a boat at water level the Mtkvari wall is coursed
             stone of roughly this block size, not poured concrete; the 240 cm
             tile matches the real course height so it can map in metres.
  quaystone  Granite002A — pale speckled granite for the coping course and the
             balustrade above it, which are a different, lighter stone than
             the wall in every photograph of the embankment.
"""
import io
import os
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'public', 'textures')
CACHE = os.path.join(HERE, 'cache')
UA = 'TbilisiFly/0.1 (+local dev build)'

SETS = {
    'quaywall': dict(asset='Bricks066', res='1K'),
    'quaystone': dict(asset='Granite002A', res='1K'),
}
# ambientCG ships OpenGL and DirectX normals; three.js wants the OpenGL one.
SUFFIX = {'': '_Color', '_nor': '_NormalGL', '_rough': '_Roughness'}


def fetch(asset, res):
    name = f'{asset}_{res}-JPG.zip'
    path = os.path.join(CACHE, name)
    if not os.path.exists(path):
        os.makedirs(CACHE, exist_ok=True)
        url = f'https://ambientcg.com/get?file={name}'
        print(f'  downloading {url}')
        req = urllib.request.Request(url, headers={'User-Agent': UA})
        with urllib.request.urlopen(req) as r:
            data = r.read()
        with open(path, 'wb') as f:
            f.write(data)
    return zipfile.ZipFile(path)


def main():
    os.makedirs(OUT, exist_ok=True)
    for out_name, spec in SETS.items():
        asset, res = spec['asset'], spec['res']
        print(f'{out_name}: {asset} ({res}, CC0)')
        zf = fetch(asset, res)
        members = zf.namelist()
        for out_suffix, tag in SUFFIX.items():
            hit = next((m for m in members if m.endswith(f'{tag}.jpg')), None)
            if hit is None:
                print(f'  ! no {tag} in {asset}', file=sys.stderr)
                continue
            dst = os.path.join(OUT, f'{out_name}{out_suffix}.jpg')
            with open(dst, 'wb') as f:
                f.write(zf.read(hit))
            kb = os.path.getsize(dst) // 1024
            print(f'  {os.path.basename(dst)}  <- {hit}  ({kb} kB)')
    print('\nCredit: ambientCG.com, CC0 — '
          + ', '.join(s['asset'] for s in SETS.values()))


if __name__ == '__main__':
    main()
