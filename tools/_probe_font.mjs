#!/usr/bin/env node
// Which characters the game's own UI text actually needs a glyph for.
//
// The JS build shipped NO font: src/style.css:25 names "Noto Sans Georgian"
// and the browser supplied it from the OS, falling back PER GLYPH for anything
// that family lacked. So there is no JS-side font to compare against — what
// this probe establishes is the REQUIREMENT: the exact set of codepoints the
// original's own strings contain. unity/Assets/Editor/Probes/FontProbe.cs then
// asks the shipped FontAsset chain whether it can draw each one.
//
// Reads src/*.js (the spec), not the C# — so the two sides are independent.
import { readFileSync, readdirSync } from 'fs';
import { join } from 'path';

const SRC = new URL('../src/', import.meta.url).pathname;
const files = readdirSync(SRC).filter((f) => f.endsWith('.js'));

// Every string literal: '...', "..." and `...`. Deliberately crude — a false
// positive only adds a codepoint to the required set, which is the safe
// direction (we would demand a glyph we do not need, never miss one we do).
const LIT = /'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`/g;

const counts = new Map();
for (const f of files) {
  const text = readFileSync(join(SRC, f), 'utf8');
  for (const m of text.matchAll(LIT)) {
    const s = m[1] ?? m[2] ?? m[3] ?? '';
    for (const ch of s) {
      const cp = ch.codePointAt(0);
      if (cp < 0x80) continue;                 // ASCII: every font has it
      counts.set(cp, (counts.get(cp) ?? 0) + 1);
    }
  }
}

const cps = [...counts.keys()].sort((a, b) => a - b);
const block = (cp) =>
  cp >= 0x10a0 && cp <= 0x10ff ? 'georgian'
  : cp >= 0x1c90 && cp <= 0x1cbf ? 'georgian_ext'
  : cp >= 0x0400 && cp <= 0x04ff ? 'cyrillic'
  : cp >= 0x0370 && cp <= 0x03ff ? 'greek'
  : cp >= 0x2190 && cp <= 0x21ff ? 'arrows'
  : cp >= 0x2600 && cp <= 0x27bf ? 'symbols'
  : cp >= 0x1f300 && cp <= 0x1faff ? 'emoji'
  : cp >= 0x2500 && cp <= 0x25ff ? 'boxdraw'
  : 'other';

const byBlock = {};
for (const cp of cps) byBlock[block(cp)] = (byBlock[block(cp)] ?? 0) + 1;

console.log(`JSONLY FONTREQ distinct=${cps.length}`);
for (const k of Object.keys(byBlock).sort())
  console.log(`JSONLY FONTBLOCK id=${k} distinct=${byBlock[k]}`);
// One line per codepoint so the C# side pairs on it exactly.
for (const cp of cps)
  console.log(`FONTCP id=U+${cp.toString(16).toUpperCase().padStart(4, '0')} needed=1 uses=${counts.get(cp)}`);
