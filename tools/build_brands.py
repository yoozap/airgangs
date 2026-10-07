"""Merge the researched brand books into public/tbilisi/brands.json.

Input: tools/cache/brands*.json — per-chain fascia colours, wordmark, mark
shape and a source note (see prompt_brands.txt). Output is what the game reads
to paint a SPAR sign SPAR-green with its fir mark instead of a generic plate.

No logo artwork is copied: only the colours, the wordmark text and a shape
family the renderer draws itself.
"""
import glob
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'public', 'tbilisi', 'brands.json')
HEX = re.compile(r'^#[0-9a-fA-F]{6}$')
MARKS = {'cross', 'leaf', 'fir', 'drop', 'circle', 'chevron', 'arch', 'square',
         'star', 'none'}

out, seen, bad = [], set(), 0
# brands0-2 (the 60 biggest chains) + brands_b* (the long tail)
for f in sorted(glob.glob(os.path.join(HERE, 'cache', 'brands*.json'))):
    try:
        rows = json.load(open(f))
    except Exception as e:  # noqa: BLE001
        print('skip', f, e)
        continue
    for b in rows:
        nm = (b.get('name_ka') or '').strip()
        if not nm or nm in seen:
            continue
        if not HEX.match(b.get('bg') or '') or not HEX.match(b.get('fg') or ''):
            bad += 1
            continue
        acc = b.get('accent')
        out.append({
            'name_ka': nm,
            'brand': b.get('brand') or '',
            'bg': b['bg'], 'fg': b['fg'],
            'accent': acc if HEX.match(acc or '') else None,
            'wordmark': (b.get('wordmark') or nm).strip(),
            'script': b.get('script') if b.get('script') in ('ka', 'latin', 'both') else 'ka',
            'mark': b.get('mark') if b.get('mark') in MARKS else 'none',
            # optional: the chain's REAL category when OSM mislabels it
            # (SOLO is Bank of Georgia's lounge, not a clinic; Mozaika is
            # cosmetics, not a bar; Gama is CNG fuel) — the game styles the
            # storefront from this instead of the OSM tag.
            **({'cat': b['cat']} if b.get('cat') else {}),
        })
        seen.add(nm)

json.dump(out, open(OUT, 'w'), ensure_ascii=False, separators=(',', ':'))
print(f'{len(out)} brands -> {OUT}' + (f' ({bad} rejected for bad colours)' if bad else ''))
for b in out[:12]:
    print(f"  {b['name_ka']:<22} {b['bg']} / {b['fg']}  mark={b['mark']:<8} {b['wordmark']}")
