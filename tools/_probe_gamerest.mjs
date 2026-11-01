#!/usr/bin/env node
// tools/_probe_gamerest.mjs
//
// Verifies the numeric constants the Unity port's new "game-rest" files
// (Core/PlaneHullDamage.cs, Core/PlaneGroundProbe.cs, Core/PlaneGroundRoll.cs,
// Core/PlaneWallContact.cs, Core/CharacterPreviewRig.cs) carried over from
// src/game.js (and the one src/planephys.js block PlaneWallContact.cs ported
// from source for ApplyRamResult, plus the three constants it REUSES from the
// already-ported Flight/PlanePhysics.cs instead of re-declaring them).
//
// Every number below is pulled with a regex straight out of the real source
// file on disk — never retyped from memory — and printed as KEY=value so this
// script and the C# probe (Assets/Editor/Probes/GameRestProbe.cs) can be
// diffed by a human or another script.
//
// Run: node tools/_probe_gamerest.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const game = readFileSync(path.join(root, 'src/game.js'), 'utf8');
const planephys = readFileSync(path.join(root, 'src/planephys.js'), 'utf8');

let failed = false;
function need(src, re, label, srcName) {
  const m = src.match(re);
  if (!m) {
    console.error(`MISSING ${label} (pattern not found in ${srcName})`);
    failed = true;
    return null;
  }
  return m[1];
}

function num(src, re, label, srcName) {
  const v = need(src, re, label, srcName);
  return v === null ? null : Number(v);
}

// ---- top-of-file constants (game.js:66-78) ----
console.log('MAX_HEALTH=' + num(game, /const MAX_HEALTH\s*=\s*([\d.]+)/, 'MAX_HEALTH', 'game.js'));
console.log('WHEEL_HEIGHT=' + num(game, /const WHEEL_HEIGHT\s*=\s*([\d.]+)/, 'WHEEL_HEIGHT', 'game.js'));
console.log('REVERSE_SPEED=' + num(game, /const REVERSE_SPEED\s*=\s*([\d.]+)/, 'REVERSE_SPEED', 'game.js'));
console.log('G=' + num(game, /const G\s*=\s*([\d.]+)/, 'G', 'game.js'));

// ---- _updateHealthBar bands (game.js:2191-2206) ----
console.log('HEALTH_BAND_GOOD=' + num(game, /p > ([\d.]+)\s*\n\s*\?\s*'linear-gradient\(90deg, #29c6ff/, 'HEALTH_BAND_GOOD', 'game.js'));
console.log('HEALTH_BAND_WARN=' + num(game, /p > ([\d.]+)\s*\n\s*\?\s*'linear-gradient\(90deg, #ffb02e/, 'HEALTH_BAND_WARN', 'game.js'));

// ---- _collapseGear / _shearWing / _shearTail / _damageWing (game.js:1817-2300) ----
console.log('COLLAPSE_GEAR_DMG=' + num(game, /_collapseGear\(\) \{[\s\S]*?_applyDamage\((\d+)\)/, 'COLLAPSE_GEAR_DMG', 'game.js'));
console.log('SHEAR_WING_DMG=' + num(game, /_shearWing\(side, msg\) \{[\s\S]*?_applyDamage\((\d+)\)/, 'SHEAR_WING_DMG', 'game.js'));
console.log('SHEAR_TAIL_DMG=' + num(game, /_shearTail\(msg\) \{[\s\S]*?_applyDamage\((\d+)\)/, 'SHEAR_TAIL_DMG', 'game.js'));
console.log('WING_DAMAGE_CAP=' + num(game, /this\.struct\[part\] = Math\.min\(this\.struct\[part\], ([\d.]+)\)/, 'WING_DAMAGE_CAP', 'game.js'));
console.log('WING_DROOP_RAD=' + num(game, /wing\.mesh\.rotation\.z \+= side === 'right' \? -([\d.]+) : ([\d.]+)/, 'WING_DROOP_RAD', 'game.js'));

// ---- _touchdown thresholds (game.js:1736-1808) ----
console.log('TOUCHDOWN_FATAL_FWDY=' + num(game, /fwd\.y < -([\d.]+) && this\.speed > (\d+)/, 'TOUCHDOWN_FATAL_FWDY', 'game.js'));
console.log('TOUCHDOWN_BANK_RIGHTY=' + num(game, /Math\.abs\(right\.y\) > ([\d.]+) && this\.speed > 25/, 'TOUCHDOWN_BANK_RIGHTY', 'game.js'));
console.log('TOUCHDOWN_SHEAR_SPEED=' + num(game, /if \(this\.speed > (\d+)\) this\._shearWing\(side, 'ფრთამ მიწას/, 'TOUCHDOWN_SHEAR_SPEED', 'game.js'));
console.log('TOUCHDOWN_DAMAGEWING_DMG=' + num(game, /else this\._damageWing\(side, (\d+)\);/, 'TOUCHDOWN_DAMAGEWING_DMG', 'game.js'));
console.log('TOUCHDOWN_HARD_SINK=' + num(game, /if \(sink > ([\d.]+)\) \{\n\s*this\._crashDamage\(level/, 'TOUCHDOWN_HARD_SINK', 'game.js'));
console.log('TOUCHDOWN_MED_SINK=' + num(game, /\} else if \(sink > ([\d.]+)\) \{/, 'TOUCHDOWN_MED_SINK', 'game.js'));
console.log('COLLAPSE_GEAR_SINK_LEVEL=' + num(game, /if \(this\.struct\.gear && sink > \(level \? (\d+) : \d+\)\) this\._collapseGear/, 'COLLAPSE_GEAR_SINK_LEVEL', 'game.js'));
console.log('COLLAPSE_GEAR_SINK_UNLEVEL=' + num(game, /if \(this\.struct\.gear && sink > \(level \? \d+ : (\d+)\)\) this\._collapseGear/, 'COLLAPSE_GEAR_SINK_UNLEVEL', 'game.js'));

// ---- ground-roll (game.js:1540-1666) ----
console.log('ROLLING_FRICTION_GEAR=' + num(game, /\(this\.struct\.gear \? ([\d.]+) : [\d.]+\) \* G/, 'ROLLING_FRICTION_GEAR', 'game.js'));
console.log('ROLLING_FRICTION_BELLY=' + num(game, /\(this\.struct\.gear \? [\d.]+ : ([\d.]+)\) \* G/, 'ROLLING_FRICTION_BELLY', 'game.js'));
console.log('BRAKE_G=' + num(game, /s\.throttle < 0 && this\.speed > 0\.5 \? ([\d.]+) \* G/, 'BRAKE_G', 'game.js'));
console.log('REVERSE_ACCEL=' + num(game, /const drive = reversing \? (-?\d+) : thrust/, 'REVERSE_ACCEL', 'game.js'));
console.log('ROTATE_VR_FRAC=' + num(game, /this\.speed > this\.aero\.vr \* ([\d.]+)/, 'ROTATE_VR_FRAC', 'game.js'));
console.log('LIFTOFF_HEIGHT=' + num(game, /this\.plane\.position\.y - floorY > ([\d.]+)\) \{\n\s*this\._liftoff/, 'LIFTOFF_HEIGHT', 'game.js'));
console.log('LIFTOFF_EDGE_DROP=' + num(game, /floorY < this\.plane\.position\.y - (\d+) && Math\.abs\(this\.speed\) > 4/, 'LIFTOFF_EDGE_DROP', 'game.js'));
console.log('BELLY_SLIDE_DMG_PER_S=' + num(game, /this\._applyDamage\((\d+) \* dt\);/, 'BELLY_SLIDE_DMG_PER_S', 'game.js'));

// ---- _land (game.js:2178-2190) ----
console.log('LAND_PITCH_CLAMP=' + num(game, /e\.x = THREE\.MathUtils\.clamp\(e\.x, -([\d.]+), [\d.]+\)/, 'LAND_PITCH_CLAMP', 'game.js'));

// ---- _touchWall (game.js:1896-2016) ----
console.log('TOUCHWALL_MIN_SPEED=' + num(game, /_touchWall\(dt\) \{\n\s*const sp = this\.airborne[\s\S]*?if \(sp < (\d+)\) return false/, 'TOUCHWALL_MIN_SPEED', 'game.js'));
console.log('CEILING_HITCOOLDOWN=' + num(game, /this\._hitCooldown = ([\d.]+);\n\s*this\._crashDamage\(sp \* 0\.7/, 'CEILING_HITCOOLDOWN', 'game.js'));
console.log('BUILDINGSMASH_HEADON_SPEED=' + num(game, /headOn > ([\d.]+) && sp > (\d+)\) \{\n\s*this\._buildingSmash/, 'BUILDINGSMASH_HEADON_SPEED', 'game.js'));
console.log('BUILDINGSMASH_TOTAL_MULT=' + num(game, /this\.world\.damageHit\?\.\(hit, Math\.round\(sp \* (\d+)\)\)/, 'BUILDINGSMASH_TOTAL_MULT', 'game.js'));

// ---- _checkCollision (game.js:2017-2177) ----
console.log('WINGHIT_COOLDOWN_TICK=' + num(game, /this\._wingHitCooldown -= ([\d.]+); \/\/ collision tick interval/, 'WINGHIT_COOLDOWN_TICK', 'game.js'));
console.log('WINGHIT_MIN_SPEED=' + num(game, /else if \(Math\.abs\(this\.speed\) > (\d+)\) \{\n\s*const dir = backing/, 'WINGHIT_MIN_SPEED', 'game.js'));
console.log('WINGWALL_SHEAR_SPEED=' + num(game, /if \(Math\.abs\(this\.speed\) > (\d+)\) this\._shearWing\(side, 'ფრთა კედელს/, 'WINGWALL_SHEAR_SPEED', 'game.js'));
console.log('WINGWALL_DAMAGEWING_BASE=' + num(game, /else this\._damageWing\(side, (\d+) \+ Math\.abs\(this\.speed\) \* 2\.5\)/, 'WINGWALL_DAMAGEWING_BASE', 'game.js'));
console.log('WINGDRAG_MIN_SPEED=' + num(game, /this\._wingHitCooldown <= 0 && Math\.abs\(this\.speed\) > (\d+)\) \{\n\s*for \(const side of \['left', 'right'\]\) \{\n\s*if \(!this\.struct\[side/, 'WINGDRAG_MIN_SPEED', 'game.js'));
console.log('WINGDRAG_SHEAR_SPEED=' + num(game, /Math\.abs\(this\.speed\) > (\d+)\) \{\n\s*this\._shearWing\(side, 'ფრთამ სახურავს/, 'WINGDRAG_SHEAR_SPEED', 'game.js'));
console.log('WINGDRAG_DAMAGEWING_DMG=' + num(game, /this\._damageWing\(side, (\d+)\);\n\s*\/\/ the surface kicks/, 'WINGDRAG_DAMAGEWING_DMG', 'game.js'));

// ---- src/planephys.js:490-522 (ported into PlaneWallContact.ApplyRamResult) ----
console.log('RAM_SPECUS_GATE=' + num(planephys, /if \(part !== 'nose' && specUs > (\d+)\)/, 'RAM_SPECUS_GATE', 'planephys.js'));
console.log('RAM_TEARSPEC=' + num(planephys, /const TEAR_SPEC = (\d+)/, 'RAM_TEARSPEC', 'planephys.js'));
console.log('RAM_DEADZONE=' + num(planephys, /const DEADZONE = (\d+)/, 'RAM_DEADZONE', 'planephys.js'));
console.log('RAM_KPLANE=' + num(planephys, /const K_PLANE = ([\d.]+)/, 'RAM_KPLANE', 'planephys.js'));

// ---- main.js preview camera/lights (CharacterPreviewRig.cs) ----
const main = readFileSync(path.join(root, 'src/main.js'), 'utf8');
console.log('PREVIEW_FOV=' + num(main, /new THREE\.PerspectiveCamera\((\d+)/, 'PREVIEW_FOV', 'main.js'));
console.log('PREVIEW_SUN_INTENSITY=' + num(main, /DirectionalLight\(0xffffff, ([\d.]+)\)/, 'PREVIEW_SUN_INTENSITY', 'main.js'));
console.log('PREVIEW_RIM_INTENSITY=' + num(main, /DirectionalLight\(0x3fd6ff, ([\d.]+)\)/, 'PREVIEW_RIM_INTENSITY', 'main.js'));
console.log('PREVIEW_DRAG_SCALE=' + num(main, /charPrev\.holder\.rotation\.y \+= dx \* ([\d.]+)/, 'PREVIEW_DRAG_SCALE', 'main.js'));

if (failed) {
  console.error('\nFAILED: one or more patterns did not match the real source — fix the regex, do not hand-fill a number.');
  process.exit(1);
}
