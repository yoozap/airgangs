// Pairs with unity/Assets/Editor/Probes/RealFacadeProbe.cs.
//
// Reads the REAL public/textures/facades/facades.json and the REAL
// src/citymap.js, so the bindings, the tiling and the anisotropy choice are
// checked against the shipped data rather than against a copy of themselves.
import { readFileSync } from 'node:fs';

const man = JSON.parse(readFileSync(new URL('../public/textures/facades/facades.json', import.meta.url), 'utf8'));
const src = readFileSync(new URL('../src/citymap.js', import.meta.url), 'utf8');

// The apply() call list, extracted from the real function body rather than
// retyped — order and anisotropy both come from the source.
const at = src.indexOf('async _loadRealFacades(');
const body = src.slice(at, src.indexOf('\n  }', at));
const calls = [...body.matchAll(/apply\(([A-Za-z0-9_\[\]]+),\s*'([a-z0-9_]+)'(?:,\s*(\d+))?\)/g)]
  .map((m) => ({ target: m[1], kind: m[2], aniso: m[3] ? parseInt(m[3], 10) : 8 }));

console.log(`BINDING_COUNT=${calls.length}`);
for (const c of calls) console.log(`BINDING_${c.kind}=${c.target},${c.aniso}`);

// Tiling is 1/bays by 1/floors for every kind the bindings name.
for (const c of calls) {
  const e = man[c.kind];
  if (!e) { console.log(`TILING_${c.kind}=MISSING`); continue; }
  console.log(`TILING_${c.kind}=${(1 / (e.bays || 1)).toFixed(6)},${(1 / (e.floors || 1)).toFixed(6)}`);
  console.log(`BAYSFLOORS_${c.kind}=${e.bays || 1},${e.floors || 1}`);
}

// Every CC BY / CC BY-SA entry must carry a credit — a licence obligation, so
// a missing one is a real finding, not a cosmetic gap.
const named = calls.map((c) => c.kind);
const missing = named.filter((k) => !man[k] || !man[k].credit);
console.log(`CREDITS_PRESENT=${named.length - missing.length}`);
console.log(`CREDITS_MISSING=${missing.length}`);

// The roof pair is guarded on roofs.json's absence (citymap.js:6270-6271).
const guarded = /if \(!this\._roofMan\)/.test(body);
console.log(`ROOFPAIR_GUARDED=${guarded}`);
console.log(`MANIFEST_KINDS=${Object.keys(man).length}`);
