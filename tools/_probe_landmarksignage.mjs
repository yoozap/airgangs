// Ground truth for LandmarkSignage.cs — the two canvas-texture brand signs
// landmarks.js's `label()` paints (src/landmarks.js:44-60), which
// LandmarkPlacer.cs's own header flags as dropped ("minus the photo-facade
// textures"). This closes that gap for Marriott/Radisson RED specifically.
//
// Two independent checks, neither needing a browser/DOM (label() itself calls
// `document.createElement('canvas')`, so it cannot be called directly from
// plain node — same category as every other THREE/DOM-touching helper this
// project's probes replicate rather than execute, e.g. planehall.js's pack()):
//
//  1. REGEX-EXTRACT the real label(...) call arguments straight out of
//     src/landmarks.js's source text (not hand-retyped) for both signs, so a
//     future edit to the brand text/colours/size in the JS is caught here
//     rather than silently drifting from LandmarkSignage.cs's hardcoded
//     Hex()/string literals.
//  2. REPLICATE the same 5x7 bitmap-font rasterisation LandmarkSignage.cs's
//     BuildLabelTexture uses (cited by line, same pattern this project already
//     uses for module-private JS helpers like planehall.js's pack()) to
//     compute an expected foreground-pixel fraction and texture size for each
//     sign — directly comparable to LandmarkSignageProbe.cs's SIGNTEX_* lines.
//
//   node tools/_probe_landmarksignage.mjs
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/landmarks.js', import.meta.url), 'utf8');

// ---- 1. regex-extract the real label() call sites --------------------------
// landmarks.js:1162 marriott():
//   label('TBILISI MARRIOTT', 512, 54, '#efece4', '#4a4a4a')
// landmarks.js:1204 radissonRed():
//   label('RADISSON RED', 384, 60, '#c8102e', '#ffffff')
const CALL_RE = /label\('([^']+)',\s*(\d+),\s*(\d+),\s*'#([0-9a-fA-F]{6})',\s*'#([0-9a-fA-F]{6})'/g;
const calls = [];
let m;
while ((m = CALL_RE.exec(src))) {
  calls.push({ text: m[1], w: +m[2], h: +m[3], bg: m[4].toLowerCase(), fg: m[5].toLowerCase() });
}
// landmarks.js:44-60 label() is also called by other landmarks (e.g. the
// Concert Hall sign, the flag texture) that are NOT this file's scope — only
// the two brand signs below are looked up by exact text, so extra matches
// here are expected and harmless, not a failure.
console.log(`LABELCALLS_FOUND count=${calls.length} (>= 2 expected; extras are other landmarks' own label() calls, out of scope here)`);
const byText = Object.fromEntries(calls.map((c) => [c.text, c]));
for (const [id, text] of [['marriott', 'TBILISI MARRIOTT'], ['radissonRed', 'RADISSON RED']]) {
  const c = byText[text];
  if (!c) { console.log(`LABELCALL_${id} MISSING — expected text "${text}" not found in src/landmarks.js`); continue; }
  console.log(`LABELCALL_${id} text="${c.text}" w=${c.w} h=${c.h} bg=${c.bg} fg=${c.fg}`);
}

// ---- 2. replicate the 5x7 rasterisation (LandmarkSignage.cs BuildLabelTexture) ----
const GLYPH5X7 = {
  ' ': ['00000', '00000', '00000', '00000', '00000', '00000', '00000'],
  A: ['01110', '10001', '10001', '11111', '10001', '10001', '10001'],
  B: ['11110', '10001', '10001', '11110', '10001', '10001', '11110'],
  D: ['11110', '10001', '10001', '10001', '10001', '10001', '11110'],
  E: ['11111', '10000', '10000', '11110', '10000', '10000', '11111'],
  I: ['11111', '00100', '00100', '00100', '00100', '00100', '11111'],
  L: ['10000', '10000', '10000', '10000', '10000', '10000', '11111'],
  M: ['10001', '11011', '10101', '10001', '10001', '10001', '10001'],
  N: ['10001', '11001', '10101', '10011', '10001', '10001', '10001'],
  O: ['01110', '10001', '10001', '10001', '10001', '10001', '01110'],
  R: ['11110', '10001', '10001', '11110', '10100', '10010', '10001'],
  S: ['01111', '10000', '10000', '01110', '00001', '00001', '11110'],
  T: ['11111', '00100', '00100', '00100', '00100', '00100', '00100'],
};

function litFraction(text, w, h) {
  let scale = (h * 0.7) / 7;
  const n = text.length;
  let totalW = n * 6 * scale - scale;
  if (totalW > w * 0.94) scale *= (w * 0.94) / totalW;
  totalW = n * 6 * scale - scale;
  const x0 = (w - totalW) / 2;
  const y0 = (h - scale * 7) / 2;

  const lit = new Uint8Array(w * h);
  for (let ci = 0; ci < n; ci++) {
    const c = text[ci].toUpperCase();
    const rows = GLYPH5X7[c];
    if (!rows) continue;
    const cellX = x0 + ci * 6 * scale;
    for (let row = 0; row < 7; row++) {
      const bits = rows[row];
      for (let col = 0; col < 5; col++) {
        if (bits[col] !== '1') continue;
        const px0 = Math.floor(cellX + col * scale);
        const px1 = Math.ceil(cellX + (col + 1) * scale);
        const py0 = Math.floor(y0 + row * scale);
        const py1 = Math.ceil(y0 + (row + 1) * scale);
        for (let py = Math.max(0, py0); py < Math.min(h, py1); py++) {
          for (let px = Math.max(0, px0); px < Math.min(w, px1); px++) lit[py * w + px] = 1;
        }
      }
    }
  }
  let count = 0;
  for (let i = 0; i < lit.length; i++) count += lit[i];
  return count / lit.length;
}

for (const [id, text] of [['marriott', 'TBILISI MARRIOTT'], ['radissonRed', 'RADISSON RED']]) {
  const c = byText[text];
  if (!c) continue;
  const frac = litFraction(c.text, c.w, c.h);
  console.log(`SIGNTEX_expected_${id} w=${c.w} h=${c.h} fgFraction=${frac.toFixed(4)} fullyOpaque=true`);
}
