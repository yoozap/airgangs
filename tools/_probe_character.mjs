// The JS side of the Character port check (CharacterRoster/CharacterRig).
// Prints the roster facts and the WALK_SET root-motion speeds straight out of
// src/characters.js + public/models/anims/moves.json, in the same KEY=value
// format unity/Assets/Editor/Probes/CharacterProbe.cs prints from the ported
// C# side — diff the WALK/CONST/RACE/PERSONAGE lines with
// tools/unity_port_check.py.
//
// NOT comparable 1:1: Unity's BODY/RIG lines measure facts only a loaded FBX
// can answer (bone count, whether the Avatar mapped Humanoid, the normalised
// standing height) — src/characters.js never measures those itself without a
// live GLTFLoader + a running Three.js scene, so this script does not try to
// fake a BODY line. The roster data (RACES/PERSONAGES/WALK_SET/HERO_HEIGHT)
// is what both sides can state honestly, and that is what gets diffed.
//
//   node tools/_probe_character.mjs
import { readFileSync } from 'node:fs';
import {
  RACES, PERSONAGES, WALK_SET, WALK_CLIP_SPEED, WALK_MAX_RATE, HERO_HEIGHT,
} from '../src/characters.js';

const f = (v, n = 4) => Number(v).toFixed(n);

// The 4-body roster only (see CharacterRoster.cs header on the other 35/9).
const ROSTER_IDS = new Set(['banana', 'eggplant', 'toad', 'elephant']);

const moves = JSON.parse(readFileSync(new URL('../public/models/anims/moves.json', import.meta.url)));

for (let i = 0; i < WALK_SET.length; i++) {
  const name = WALK_SET[i];
  const c = moves.clips[name];
  let deg = i * 45;
  if (deg > 180) deg -= 360;
  const moveLat = c ? c.move[0] : 0;
  const moveFwd = c ? c.move[2] : 0;
  const dur = c ? c.dur : 0;
  console.log(
    `WALK id=${name} angleDeg=${f(deg, 2)} moveLat=${f(moveLat)} moveFwd=${f(moveFwd)} ` +
    `dur=${f(dur)} speedLat=${f(dur > 0 ? moveLat / dur : 0, 5)} speedFwd=${f(dur > 0 ? moveFwd / dur : 0, 5)}`);
}

console.log(`CONST id=_const heroHeight=${f(HERO_HEIGHT, 3)} ` +
  `walkClipSpeed=${f(WALK_CLIP_SPEED, 5)} walkMaxRate=${f(WALK_MAX_RATE, 3)}`);

for (const race of RACES) {
  console.log(`RACE id=${race.id} roleKa=${race.roleKa} runSpeed=${f(race.runSpeed, 3)} ` +
    `bodies=${race.bodies.join(',')} defModel=${race.defModel}`);
}

for (const p of PERSONAGES) {
  if (!ROSTER_IDS.has(p.id)) continue;
  console.log(`PERSONAGE id=${p.id} name=${p.name} icon=${p.icon} ` +
    `proceduralOnly=${p.id === 'elephant' ? 1 : 0}`);
}
