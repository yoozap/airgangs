#!/usr/bin/env node
// Verification for this pass's own work: the three confirmed gaps in
// src/main.js that had NO Unity counterpart at all before this pass —
//   1. main.js:618-657  SPAWN_CITIES / chosenPlane / planeAllowed / firstAllowedPlane
//   2. main.js:729-757  the 🎬 chip's dropdown + stop-button wiring
//   3. main.js:132-139  updateBadge
// unity/Assets/Editor/Probes/MainRestProbe.cs prints the C#-side numbers
// under the IDENTICAL KEY=value shape (Core/Boot.cs's new PlaneAllowed/
// FirstAllowedPlane/BadgeName/BadgeSub, Core/SpawnCities.cs, and the
// Georgian strings baked into UI/Hud/CinemaMenuView.cs) — a plain `diff`
// (via tools/unity_port_check.py) between the two outputs IS the
// verification. Per .claude/skills/unity/SKILL.md: booleans print lower-case
// on both sides, and the two sides' KEY sets must match exactly.
//
// This does NOT execute main.js (it imports three.js and touches the DOM,
// so it cannot run under plain Node) — same convention as
// tools/_probe_charcreate.mjs / tools/_probe_corerest.mjs: pull the REAL
// data tables out of the source files by regex, then run the REAL algorithm
// (planeAllowed/firstAllowedPlane/updateBadge, copied here verbatim from
// main.js) against that real data. The planeAllowed/firstAllowedPlane half
// of this is the actual point of the cross-check: Boot.cs's C# port does NOT
// carry a second copy of RACES[].planes as data — it reuses the race->role
// map StarterPlaneFor already has (because every id in RACES[].planes
// carries exactly the role its race is assigned). This probe proves that
// shortcut produces IDENTICAL answers to the real per-race `planes` list,
// for every case that matters.
//
// Run: node tools/_probe_mainrest.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(path.join(ROOT, p), 'utf8');

const mainJs = read('src/main.js');
const charactersJs = read('src/characters.js');
const planesJs = read('src/planes.js');
const indexHtml = read('index.html');

const lcBool = (b) => (b ? 'true' : 'false'); // SKILL.md: booleans lower-case on BOTH sides

// =============================================================================
// ---- 1. SPAWN_CITIES — main.js:639-657 -------------------------------------
// =============================================================================
const spawnBlock = mainJs.slice(mainJs.indexOf('const SPAWN_CITIES = {'), mainJs.indexOf('\n};', mainJs.indexOf('const SPAWN_CITIES = {')) + 3);
// one regex per "id: { lat: x, lon: y[, yaw: z] }" entry — tolerant of the
// real file's own comments between entries.
const entryRe = /(\w+):\s*\{\s*lat:\s*([\d.]+),\s*lon:\s*([\d.]+)(?:,\s*yaw:\s*(-?[\d.]+))?\s*\}/g;
const spawnCities = [];
let m;
while ((m = entryRe.exec(spawnBlock))) {
  spawnCities.push({ id: m[1], lat: Number(m[2]), lon: Number(m[3]), yaw: m[4] !== undefined ? Number(m[4]) : null });
}
console.log(`SPAWN_COUNT=${spawnCities.length}`);
console.log(`SPAWN_DEFAULT_ID=tbilisi`); // main.js:636 `let selectedCity = 'tbilisi'`
for (const c of spawnCities) {
  console.log(`SPAWN_${c.id}_LAT=${c.lat}`);
  console.log(`SPAWN_${c.id}_LON=${c.lon}`);
  console.log(`SPAWN_${c.id}_YAW=${c.yaw === null ? 'none' : c.yaw}`);
}
// main.js:692 `SPAWN_CITIES[selectedCity] ?? SPAWN_CITIES.tbilisi` — an unknown
// city id must fall back to tbilisi's own point.
const tbilisi = spawnCities.find((c) => c.id === 'tbilisi');
const resolve = (id) => spawnCities.find((c) => c.id === id) ?? tbilisi;
const unknown = resolve('not-a-real-city');
console.log(`SPAWN_RESOLVE_UNKNOWN_LAT=${unknown.lat}`);
console.log(`SPAWN_RESOLVE_UNKNOWN_LON=${unknown.lon}`);

// =============================================================================
// ---- 2. planeAllowed / firstAllowedPlane — main.js:622-627, applied 663-664 -
// =============================================================================
// RACES[].planes, the REAL per-race allow-list (src/characters.js:28-69).
function extractRaces(src) {
  const block = src.slice(src.indexOf('export const RACES'), src.indexOf('export const raceById'));
  const objs = block.match(/\{[\s\S]*?\n {2}\}/g) || [];
  return objs.map((obj) => {
    const idM = obj.match(/id:\s*'([^']*)'/);
    const planesM = obj.match(/planes:\s*\[([^\]]*)\]/);
    if (!idM || !planesM) return null;
    const planes = planesM[1].match(/'([^']+)'/g).map((s) => s.slice(1, -1));
    return { id: idM[1], planes };
  }).filter(Boolean);
}
const races = extractRaces(charactersJs);

// PLANES[] order — the array firstAllowedPlane()'s `.find` walks in order
// (src/planes.js:67-499). `id:` on its own line marks a new plane object.
const planeIds = [...planesJs.matchAll(/\n {4}id:\s*'([^']+)'/g)].map((x) => x[1]);
console.log(`PLANE_COUNT=${planeIds.length}`);

// main.js:622-627, copied verbatim (heroRaceId === null stands for `!hero`).
function planeAllowed(heroRaceId, planeId) {
  if (!heroRaceId) return true;
  const race = races.find((r) => r.id === heroRaceId);
  return race.planes.includes(planeId);
}
function firstAllowedPlane(heroRaceId) {
  return planeIds.find((id) => planeAllowed(heroRaceId, id)) ?? planeIds[0];
}

for (const [tag, raceId] of [['NOHERO', null], ['VEGGIE', 'veggie'], ['TOYS', 'toys']]) {
  console.log(`${tag}_ALLOWED_SPITFIRE=${lcBool(planeAllowed(raceId, 'spitfire'))}`);
  console.log(`${tag}_ALLOWED_B58=${lcBool(planeAllowed(raceId, 'b58'))}`);
  console.log(`${tag}_FIRST_ALLOWED=${firstAllowedPlane(raceId)}`);
}
// main.js:663-664 applied inside startFlight: a hero's STORED plane that is
// no longer allowed for its (possibly migrated) race must fall back.
const fallbackApplied = !planeAllowed('toys', 'spitfire'); // a toys hero stored with 'spitfire' must be rejected
console.log(`STARTFLIGHT_FALLBACK_NEEDED_TOYS_SPITFIRE=${lcBool(fallbackApplied)}`);
console.log(`STARTFLIGHT_FALLBACK_RESULT_TOYS=${firstAllowedPlane('toys')}`);

// =============================================================================
// ---- 3. updateBadge — main.js:132-139 --------------------------------------
// =============================================================================
function raceName(id) { return races_full.find((r) => r.id === id).name; }
// name + code need the real Georgian name and the real plane code, not just planes()
const races_full = [...charactersJs.matchAll(/\{\s*id:\s*'(\w+)',\s*name:\s*'([^']*)'/g)].map((x) => ({ id: x[1], name: x[2] }));
function planeCode(id) {
  const i = planesJs.indexOf(`id: '${id}'`);
  const seg = planesJs.slice(i, planesJs.indexOf('\n  },', i));
  return seg.match(/code:\s*'([^']+)'/)[1];
}

const user = { username: 'testpilot', level: 7, stars: 3 };
const hero = { name: 'გია', race: 'veggie', plane: 'spitfire' };

// main.js:135 `pb-name`
const badgeName = (u) => (u ? u.username.toUpperCase() : null);
// main.js:136-138 `pb-sub`
const badgeSub = (u, h) => {
  if (!u) return null;
  return h
    ? `${h.name} • ${raceName(h.race)} • ${planeCode(h.plane)} • LVL ${u.level}`
    : `LVL ${u.level} • ${u.stars} ★`;
};

console.log(`BADGE_NOUSER_NAME=${badgeName(null) ?? 'none'}`);
console.log(`BADGE_NOUSER_SUB=${badgeSub(null, null) ?? 'none'}`);
console.log(`BADGE_NOHERO_NAME=${badgeName(user)}`);
console.log(`BADGE_NOHERO_SUB=${badgeSub(user, null)}`);
console.log(`BADGE_HERO_NAME=${badgeName(user)}`);
console.log(`BADGE_HERO_SUB=${badgeSub(user, hero)}`);

// =============================================================================
// ---- 4. cinema menu chip strings — index.html:217-234, main.js:729-757 -----
// =============================================================================
// Byte-for-byte Georgian text check: these are the exact strings main.js's
// cinema-chip code queries off the DOM (never generates), so the SOURCE OF
// TRUTH for them is index.html, not main.js itself.
const tooltipM = indexHtml.match(/id="btn-cinema" title="([^"]+)"/);
console.log(`CINEMA_CHIP_TOOLTIP=${tooltipM[1]}`);
const titleM = indexHtml.match(/class="cm-title">([^<]+)</);
console.log(`CINEMA_MENU_TITLE=${titleM[1]}`);
const rowRe = /data-scene="(\d)">\s*<span class="cm-n">([^<]+)<\/span>\s*<span class="cm-t">([^<]+)<\/span>\s*<span class="cm-d">([^<]+)<\/span>/g;
let rm;
while ((rm = rowRe.exec(indexHtml))) {
  console.log(`CINEMA_ROW_${rm[1]}_N=${rm[2]}`);
  console.log(`CINEMA_ROW_${rm[1]}_T=${rm[3]}`);
  console.log(`CINEMA_ROW_${rm[1]}_D=${rm[4]}`);
}

// ---- chip BEHAVIOUR, main.js:739-751. main.js itself cannot be executed
// (see header), so this half proves INTENT from the real source text; the
// C# side (MainRestProbe.cs) proves the SAME four facts by actually
// constructing a CinemaDirector + CinemaMenuView and invoking the real
// (private) click handlers via reflection and reading back the real state
// transitions — a genuine runtime proof, not a text echo of this one.
const flat = mainJs.replace(/\s+/g, ' ');
console.log(`CHIP_STOPS_WHEN_ACTIVE=${lcBool(/if \(game\?\.cinema\?\.active\) \{ game\.cinema\.stop\(\); close\(\); return; \}/.test(flat))}`);
console.log(`CHIP_OPENS_MENU_ON_CLICK=${lcBool(/menu\.classList\.toggle\('hidden'\); chip\.classList\.toggle\('open', !menu\.classList\.contains\('hidden'\)\); mark\(\);/.test(flat))}`);
console.log(`ROW_CLICK_CLOSES_MENU=${lcBool(/row\.addEventListener\('click', \(e\) => \{ e\.stopPropagation\(\); close\(\);/.test(flat))}`);
console.log(`ROW_CLICK_TOGGLES_SCENE=${lcBool(/close\(\); game\?\.cinema\?\.toggle\(\+row\.dataset\.scene\);/.test(flat))}`);
