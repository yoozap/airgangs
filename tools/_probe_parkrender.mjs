// JS half of the ParkRenderer.cs port check — the RENDER/scene-wiring half of
// src/parks.js (ParkBuilder.cs/ParkProbe.cs + tools/_probe_parkbuilder.mjs
// already cover the data half; not touched here).
//
// Static analysis only, same convention as tools/_probe_cityinfra.mjs: pulls
// the chunking/maxDist constants straight out of the real source text via
// regex, and independently RE-COUNTS the big_wheel/carousel-family attraction
// kinds from the REAL public/tbilisi/parks.json data (not re-typed numbers),
// so the paired C# probe (unity/Assets/Editor/Probes/ParkRenderProbe.cs,
// which counts the SAME attractions while actually building/instantiating
// the Unity scene) has an exact, independently-derived number to match.
//
// The C# side also prints CSONLY facts with no JS equivalent (actual
// GameObjects/Mesh/Material/Graphics.RenderMeshInstanced calls submitted —
// "did it render", which only exists once there is a Unity scene) — those
// are intentionally not mirrored here.
//
//   node tools/_probe_parkrender.mjs
import { readFileSync } from 'node:fs';

const parksSrc = readFileSync(new URL('../src/parks.js', import.meta.url), 'utf8');
const parksJson = JSON.parse(readFileSync(new URL('../public/tbilisi/parks.json', import.meta.url), 'utf8'));

function must(re, label) {
  const m = parksSrc.match(re);
  if (!m) { console.log(`${label}=MISSING`); return null; }
  return m;
}

// =============================================================================
// inst()/instProp()'s chunking + per-category maxDist — src/parks.js:344-358
// (`citymap._placeChunkedInstanced(geo, mat, items, make, 1500, false, maxDist)`)
// and each call site's own trailing maxDist literal.
// =============================================================================
{
  const m = must(/const inst = \(geo, mat, items, make, maxDist = \d+\) => \{[\s\S]*?citymap\._placeChunkedInstanced\(\s*geo,\s*mat,\s*items,\s*\([\s\S]*?\},\s*(\d+),\s*false,\s*maxDist\s*\);/, 'PARKRENDER_CHUNKM');
  if (m) console.log(`PARKRENDER_CHUNKM=${m[1]}`);
}
{
  const m = must(/if \(parklampP\) instProp\(parklampP, lamps, \(l\) => at\(l\.x, l\.z, 0, 1\), (\d+)\);/, 'PARKRENDER_MAXDIST_LAMP');
  if (m) console.log(`PARKRENDER_MAXDIST_LAMP=${m[1]}`);
}
{
  const m = must(/instProp\(fountainProp, fs, \(f\) => at\(f\.x, f\.z, 0, f\.s\), (\d+)\);/, 'PARKRENDER_MAXDIST_FOUNTAIN');
  if (m) console.log(`PARKRENDER_MAXDIST_FOUNTAIN=${m[1]}`);
}
{
  const m = must(/instProp\(playsetP, gs\.filter\(\(g2\) => g2\.k % 5 < 3\), \(g2\) => at\(g2\.x, g2\.z, g2\.yaw, 1\), (\d+)\);/, 'PARKRENDER_MAXDIST_PLAYSET');
  if (m) console.log(`PARKRENDER_MAXDIST_PLAYSET=${m[1]}`);
}
{
  const m = must(/instProp\(slideP, gs\.filter\(\(g2\) => g2\.k % 5 === 3\), \(g2\) => at\(g2\.x, g2\.z, g2\.yaw, 1\), (\d+)\);/, 'PARKRENDER_MAXDIST_SLIDE');
  if (m) console.log(`PARKRENDER_MAXDIST_SLIDE=${m[1]}`);
}
{
  const m = must(/instProp\(monkeyP, gs\.filter\(\(g2\) => g2\.k % 5 === 4\), \(g2\) => at\(g2\.x, g2\.z, g2\.yaw, 1\), (\d+)\);/, 'PARKRENDER_MAXDIST_MONKEY');
  if (m) console.log(`PARKRENDER_MAXDIST_MONKEY=${m[1]}`);
}
{
  const m = must(/wheel\.scale\.setScalar\((\d+\.\d+)\);/, 'PARKRENDER_FERRISWHEEL_SCALE');
  if (m) console.log(`PARKRENDER_FERRISWHEEL_SCALE=${m[1]}`);
}

// =============================================================================
// Attraction kind counts — independently RE-COUNTED from the real
// public/tbilisi/parks.json (byte-identical to the Unity side's
// StreamingAssets copy; both probes' own file reads verify that
// independently elsewhere, not re-asserted here), not re-typed literals.
// =============================================================================
{
  let bigWheel = 0, carouselFamily = 0, dropTower = 0, total = 0;
  const carouselKinds = new Set(['carousel', 'swing_carousel', 'amusement_ride', 'kiddie_ride']);
  for (const p of parksJson.parks) {
    for (const a of p.attractions ?? []) {
      total++;
      if (a.kind === 'big_wheel') bigWheel++;
      else if (carouselKinds.has(a.kind)) carouselFamily++;
      else if (a.kind === 'drop_tower') dropTower++;
    }
  }
  console.log(`PARKRENDER_ATTR_BIGWHEEL=${bigWheel}`);
  console.log(`PARKRENDER_ATTR_CAROUSELFAMILY=${carouselFamily}`);
  console.log(`JSONLY PARKRENDER_ATTR_DROPTOWER=${dropTower} PARKRENDER_ATTR_TOTAL=${total}`);
}

// =============================================================================
// roller_coaster-with-track — confirms ParkBuilder.cs's own port-manifest note
// ("dead in the current data, no park has a 'track' field") still holds, so
// this file is right not to build it either.
// =============================================================================
{
  let rollerCoasterWithTrack = 0;
  for (const p of parksJson.parks) {
    for (const a of p.attractions ?? []) {
      if (a.kind === 'roller_coaster' && a.track) rollerCoasterWithTrack++;
    }
  }
  console.log(`JSONLY PARKRENDER_ROLLERCOASTER_WITH_TRACK=${rollerCoasterWithTrack}`);
}

console.log('NOTE this probe is static analysis only (regex + real parks.json counting) — it asserts the SPEC\'s own numbers, not that Unity rendered them; see ParkRenderProbe.cs\'s CSONLY lines for the render evidence.');
