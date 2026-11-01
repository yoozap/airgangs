// Ground truth for unity/Assets/Scripts/City/LandmarkFacades.cs's Table —
// regex-extracts every real `photoMat(file, rx, ry, 0xHEX)` call out of each
// of the 8 (of 10) procedural landmarks' own builder function body in
// src/landmarks.js (not retyped/estimated), the same "extracted
// programmatically... not hand-typed" discipline LandmarkPlacer.cs's own
// CAT_STYLE/exclusion ports and tools/_probe_landmarksignage.mjs already use.
//
// Also checks the facade kinds those calls reference (file name minus
// extension) are present in facades.json WITH a credit — this project's
// CREDITS_MISSING=0 obligation.
//
// Run: node tools/_probe_landmarkfacades.mjs

import { readFileSync } from 'node:fs';

const js = readFileSync(new URL('../src/landmarks.js', import.meta.url), 'utf8');
const lines = js.split('\n');

// Extract one function's body by brace-matching from `function NAME(` to the
// matching closing brace — plain, no regex-across-braces trickery.
function extractFunctionBody(name) {
  const startRe = new RegExp(`function ${name}\\s*\\(`);
  const startLine = lines.findIndex((l) => startRe.test(l));
  if (startLine < 0) throw new Error(`function ${name} not found`);
  let depth = 0, started = false, endLine = -1;
  for (let i = startLine; i < lines.length; i++) {
    for (const ch of lines[i]) {
      if (ch === '{') { depth++; started = true; }
      else if (ch === '}') { depth--; if (started && depth === 0) { endLine = i; break; } }
    }
    if (endLine >= 0) break;
  }
  return { body: lines.slice(startLine, endLine + 1).join('\n'), startLine: startLine + 1, endLine: endLine + 1 };
}

// photoMat('file.jpg', rx, ry, 0xHEX) — rx/ry may be a plain number or a
// `a / b` division expression (biltmore's 34/21, 104/24).
const PHOTOMAT_RE = /photoMat\(\s*'([^']+)'\s*,\s*([0-9.]+(?:\s*\/\s*[0-9.]+)?)\s*,\s*([0-9.]+(?:\s*\/\s*[0-9.]+)?)\s*,\s*0x([0-9a-fA-F]+)\s*\)/g;

function evalNum(expr) {
  if (expr.includes('/')) {
    const [a, b] = expr.split('/').map((s) => parseFloat(s.trim()));
    return a / b;
  }
  return parseFloat(expr);
}

const LANDMARKS = ['narikala', 'funicularStation', 'metekhi', 'svetitskhoveli', 'jvari', 'baths', 'marriott', 'biltmore'];

const kindsUsed = new Set();
for (const name of LANDMARKS) {
  const { body, startLine, endLine } = extractFunctionBody(name);
  let m;
  const calls = [];
  PHOTOMAT_RE.lastIndex = 0;
  while ((m = PHOTOMAT_RE.exec(body))) {
    const file = m[1];
    const rx = evalNum(m[2]);
    const ry = evalNum(m[3]);
    const hex = m[4].toLowerCase().padStart(6, '0');
    calls.push({ file, rx, ry, hex });
    kindsUsed.add(file.replace(/\.jpg$/, ''));
  }
  console.log(`JSONLY LANDMARKFACADE_JS id=${name} lines=${startLine}-${endLine} photoMatCalls=${calls.length}`);
  for (const c of calls) {
    console.log(`JSONLY LANDMARKFACADE_JS_CALL id=${name} file=${c.file} kind=${c.file.replace(/\.jpg$/, '')} rx=${c.rx.toFixed(4)} ry=${c.ry.toFixed(4)} tintHex=${c.hex}`);
  }
}

// Cross-check against facades.json's credits (same file the Unity side reads).
const manifestPath = new URL('../unity/Assets/Resources/Art/Textures/facades/facades.json', import.meta.url);
let manifest = {};
try {
  manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
} catch (e) {
  console.log(`JSONLY LANDMARKFACADE_JS_MANIFEST error=${e.message}`);
}
let creditsMissing = 0;
for (const kind of [...kindsUsed].sort()) {
  const e = manifest[kind];
  const present = !!e;
  const hasCredit = present && !!e.credit;
  if (!hasCredit) creditsMissing++;
  console.log(`JSONLY LANDMARKFACADE_JS_KIND kind=${kind} manifestPresent=${present} hasCredit=${hasCredit}`);
}
console.log(`JSONLY LANDMARKFACADE_JS_SUMMARY kindsUsed=${kindsUsed.size} creditsMissing=${creditsMissing}`);
