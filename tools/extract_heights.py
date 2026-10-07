"""
Overture buildings parquet -> tools/cache/heights.json
Flat [lat, lon, height_m] triples for every building with a real height
(Microsoft ML-estimated or OSM-sourced), used by build_map.py as the height
source for the ~85% of Tbilisi OSM buildings that carry no height tags.
"""
import json
import os

import pyarrow.parquet as pq

CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'cache')
SRC = os.path.join(CACHE, 'tbilisi_overture.parquet')
OUT = os.path.join(CACHE, 'heights.json')

t = pq.read_table(SRC)
print('rows:', t.num_rows)
print('cols:', [f.name for f in t.schema])

cols = {f.name for f in t.schema}
height = t.column('height').to_pylist() if 'height' in cols else [None] * t.num_rows
floors = t.column('num_floors').to_pylist() if 'num_floors' in cols else [None] * t.num_rows
bbox = t.column('bbox').to_pylist()

out = []
n_h = n_f = 0
for h, fl, bb in zip(height, floors, bbox):
    if h is None and fl is None:
        continue
    lat = (bb['ymin'] + bb['ymax']) / 2
    lon = (bb['xmin'] + bb['xmax']) / 2
    if h is not None and 2 < h < 400:
        n_h += 1
    elif fl is not None and 0 < fl < 80:
        h = fl * 3.2 + 1.5
        n_f += 1
    else:
        continue
    out.append([round(lat, 6), round(lon, 6), round(h, 1)])

print(f'with height: {n_h}, floors-only: {n_f}, total kept {len(out)} '
      f'of {t.num_rows} ({100 * len(out) / t.num_rows:.0f}%)')
json.dump(out, open(OUT, 'w'))
print('->', OUT, f'{os.path.getsize(OUT) / 1e6:.1f} MB')
