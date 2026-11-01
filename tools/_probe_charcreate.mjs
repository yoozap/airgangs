#!/usr/bin/env node
// Prints the JS-side ground truth for the character-create screen port
// (unity/Assets/Editor/Probes/CharCreateProbe.cs prints the C#-side numbers
// in the IDENTICAL KEY=value shape — see that file's own header). Source of
// truth: src/characters.js's RACES/PERSONAGES tables and the literal
// Georgian strings main.js:368-616 prints on this screen.
//
// Per .claude/skills/unity/SKILL.md's own gotcha ("the two sides' keys and
// field lists must match EXACTLY" / "print booleans lower-case"), this
// prints ONLY facts that unity/Assets/Editor/Probes/CharCreateProbe.cs also
// prints, under the exact same keys, so a plain `diff` between the two
// outputs IS the verification.
//
// Run: node tools/_probe_charcreate.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(path.join(ROOT, p), 'utf8');

const charactersJs = read('src/characters.js');
const mainJs = read('src/main.js');
const indexHtml = read('index.html');

// ---- RACES[] (src/characters.js:28-69) -------------------------------------
// Pulled with one regex per race object rather than a JS import: characters.js
// imports three.js and DOM-touching modules at the top of the file, so
// `import()`-ing it in plain Node (no browser/bundler) is not reliable. A
// fixed-shape literal-object scan is exact for this file because every RACES
// entry is a hand-written object literal, not a computed one.
function extractRaces(src) {
  const block = src.slice(src.indexOf('export const RACES'), src.indexOf('export const raceById'));
  const objs = block.match(/\{[\s\S]*?\n {2}\}/g) || [];
  const field = (obj, name) => {
    const m = obj.match(new RegExp(`${name}:\\s*'([^']*)'`));
    return m ? m[1] : null;
  };
  return objs.map((obj) => {
    const bodiesM = obj.match(/bodies:\s*\[([^\]]*)\]/);
    const bodies = bodiesM ? bodiesM[1].match(/'([^']+)'/g).map((s) => s.slice(1, -1)) : [];
    return {
      id: field(obj, 'id'),
      name: field(obj, 'name'),
      latin: field(obj, 'latin'),
      roleKa: field(obj, 'roleKa'),
      icon: field(obj, 'icon'),
      defModel: field(obj, 'defModel'),
      bodies,
    };
  }).filter((r) => r.id);
}

const races = extractRaces(charactersJs);

console.log(`RACE_COUNT=${races.length}`);
const rosterBodies = [];
races.forEach((r, i) => {
  console.log(`RACE_${i}_ID=${r.id}`);
  console.log(`RACE_${i}_NAME=${r.name}`);
  console.log(`RACE_${i}_LATIN=${r.latin}`);
  console.log(`RACE_${i}_ROLEKA=${r.roleKa}`);
  console.log(`RACE_${i}_ICON=${r.icon}`);
  console.log(`RACE_${i}_DEFMODEL=${r.defModel}`);
  console.log(`RACE_${i}_BODYCOUNT=${r.bodies.length}`);
  r.bodies.forEach((b, j) => console.log(`RACE_${i}_BODY_${j}=${b}`));
  rosterBodies.push(...r.bodies);
});

console.log(`ROSTER_BODY_COUNT=${rosterBodies.length}`);
rosterBodies.forEach((b, i) => console.log(`ROSTER_BODY_${i}=${b}`));

// ---- RACE_STAT_LABELS (src/main.js:261) ------------------------------------
const statLabelsM = mainJs.match(/RACE_STAT_LABELS\s*=\s*\[([^\]]*)\]/);
const statLabels = statLabelsM ? statLabelsM[1].match(/'([^']+)'/g).map((s) => s.slice(1, -1)) : [];
console.log(`STAT_LABEL_COUNT=${statLabels.length}`);
statLabels.forEach((s, i) => console.log(`STAT_LABEL_${i}=${s}`));

// ---- MAX_HEROES (src/main.js:128) ------------------------------------------
const maxHeroesM = mainJs.match(/MAX_HEROES\s*=\s*(\d+)/);
console.log(`MAX_HEROES=${maxHeroesM ? maxHeroesM[1] : ''}`);

// ---- literal screen strings (src/main.js:368-616) --------------------------
const SCREEN = mainJs.slice(mainJs.indexOf("let charMode = 'roster';"), mainJs.indexOf('// ---- equipped plane'));
const lit = (re) => {
  const m = SCREEN.match(re);
  return m ? m[1] : '';
};
console.log(`BTN_CREATE_TEXT=${lit(/btn\.textContent = '([^']+)';\s*\n\s*btn\.classList\.remove\('equipped'\);\s*\n\s*\} else/)}`);
console.log(`BTN_ENTER_TEXT=${lit(/btn\.textContent = '([^']+)'; \/\/ WoW's/)}`);
console.log(`NEW_HERO_ROW_TEXT=${lit(/add\.textContent = '([^']+)'/)}`);

// index.html's own character-screen block ONLY — lines 106-148 (the same
// range Character.uxml's own header cites). A naive indexOf+fixed-length
// slice overruns past line 148 into the UNRELATED combat-HUD target frame
// markup further down the file, which legitimately prints "DPS"/"TANK" as a
// PLANE's role chip (target.js/TargetCardView.cs, a different already-
// ported screen) — that false-positived HAS_BANNED_DPS before this was
// bounded by line number instead of a byte count.
const htmlLines = indexHtml.split('\n');
const charScreenHtml = htmlLines.slice(105, 148).join('\n'); // 0-based slice == 1-based lines 106-148
const placeholderM = charScreenHtml.match(/placeholder="([^"]*)"/);
console.log(`NAME_PLACEHOLDER=${placeholderM ? placeholderM[1] : ''}`);

// ---- hard constraints ------------------------------------------------------
// 1) no banned English role/death strings anywhere a player could see them
//    on THIS screen: characters.js's RACES/PERSONAGES tables + main.js's own
//    screen-building section (368-616) + index.html's character-screen
//    markup. Word-boundary, case-sensitive — these are the exact banned
//    tokens, not generic substrings.
// Strip `//` line comments first (same reasoning as tools/_probe_ui.mjs:
// a comment is documentation, not a rendered string, and must not force a
// false positive here).
const stripLineComments = (text) => text.replace(/\/\/[^\n]*/g, '');
const charactersJsDataBlock = charactersJs.slice(charactersJs.indexOf('export const RACES'), charactersJs.indexOf('export const personageById'));
const bannedScope = stripLineComments(charactersJsDataBlock) + '\n' + stripLineComments(SCREEN) + '\n' + charScreenHtml;
for (const tok of ['DPS', 'TANK', 'WASTED']) {
  const hit = new RegExp(`\\b${tok}\\b`).test(bannedScope);
  console.log(`HAS_BANNED_${tok}=${hit}`); // booleans lower-case per SKILL.md's own gotcha
}

// 2) no plane-picking UI in the character-screen markup (the ◄ ► cycler and
//    the preview plane were both cut 2026-10-19 — project memory).
console.log(`HAS_PLANE_UI_IN_MARKUP=${/plane/i.test(charScreenHtml)}`);

console.log(
  `JSONLY CHARCREATE_JS_SUMMARY races=${races.length} rosterBodies=${rosterBodies.length} statLabels=${statLabels.length}`
);
