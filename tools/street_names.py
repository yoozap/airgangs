"""Street name index for geocoding photo titles: OSM highway names in
Georgian + English/transliterated variants -> tools/cache/street_names.json
  {"ka": {name_ka: [name_en, ...]}, "en2ka": {normalized_en: name_ka}}
Only Tbilisi bbox ways with a name are kept.
"""
import json
import math
import os
import re

import osmium

HERE = os.path.dirname(os.path.abspath(__file__))
PBF = os.path.join(HERE, 'cache', 'georgia-latest.osm.pbf')
OUT = os.path.join(HERE, 'cache', 'street_names.json')
BBOX = (41.62, 44.68, 41.84, 44.98)


def norm_en(s):
    """Lowercase, drop the street-type word and punctuation, sort words —
    'Shio Chitadze Street' and 'street of Shio Chitadze' both -> 'chitadze shio'."""
    s = s.lower()
    s = re.sub(r'\b(street|st|avenue|ave|lane|square|sq|road|alley|rise|blind|'
               r'ქუჩა|გამზირი|მოედანი|ჩიხი|გასასვლელი|аллея|улица|проспект)\b', ' ', s)
    s = re.sub(r"[^a-zႠ-ჿ]+", ' ', s)
    return ' '.join(sorted(w for w in s.split() if len(w) > 2))


class H(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.ka = {}
        self.en2ka = {}

    def way(self, w):
        if 'highway' not in w.tags:
            return
        name = w.tags.get('name')
        if not name or len(w.nodes) < 2:
            return
        try:
            lat = w.nodes[0].lat
            lon = w.nodes[0].lon
        except Exception:  # noqa: BLE001
            return
        if not (BBOX[0] <= lat <= BBOX[2] and BBOX[1] <= lon <= BBOX[3]):
            return
        alts = [w.tags.get(k) for k in ('name:en', 'name:ru', 'int_name', 'alt_name')]
        alts = [a for a in alts if a]
        self.ka.setdefault(name, set()).update(alts)
        for a in alts + [name]:
            n = norm_en(a)
            if n:
                self.en2ka.setdefault(n, name)


h = H()
h.apply_file(PBF, locations=True, idx='flex_mem')
json.dump({'ka': {k: sorted(v) for k, v in h.ka.items()}, 'en2ka': h.en2ka},
          open(OUT, 'w'), ensure_ascii=False)
print(f'{len(h.ka)} named Tbilisi streets, {len(h.en2ka)} normalized keys -> {OUT}')
