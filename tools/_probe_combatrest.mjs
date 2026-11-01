// Prints KEY=value lines read out of the REAL src/guns.js text (regex against
// the actual literals, never retyped from memory) so they can be diffed
// one-for-one against unity/Assets/Editor/Probes/CombatRestProbe.cs, which
// prints the exact same KEY names straight from the C# consts those literals
// were ported into (Combat/Guns.cs, Combat/GunFx.cs). Run:
//   node tools/_probe_combatrest.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const src = readFileSync(join(here, '..', 'src', 'guns.js'), 'utf8');

function need(re, label) {
  const m = src.match(re);
  if (!m) {
    console.error(`MISSING ${label} — regex did not match src/guns.js (line moved?)`);
    process.exitCode = 1;
    return null;
  }
  return m[1];
}

const out = {};
out.RATE = need(/const RATE = ([\d.]+);/, 'RATE');
out.RANGE = need(/const RANGE = ([\d.]+);/, 'RANGE');
out.BULLET_SPEED = need(/const BULLET_SPEED = ([\d.]+);/, 'BULLET_SPEED');
out.DAMAGE = need(/const DAMAGE = ([\d.]+);/, 'DAMAGE');
out.PLANE_DAMAGE = need(/const PLANE_DAMAGE = ([\d.]+);/, 'PLANE_DAMAGE');
out.SPREAD = need(/const SPREAD = ([\d.]+);/, 'SPREAD');
out.MAX_TRACERS = need(/const MAX_TRACERS = ([\d.]+);/, 'MAX_TRACERS');

// ShardBurst (src/guns.js:174-256)
out.SHARD_MAX = need(/const SHARD_MAX = ([\d.]+);/, 'SHARD_MAX');
out.SHARD_PER_BURST = need(/for \(let k = 0; k < (\d+); k\+\+\) \{\s*\n\s*const i = this\.cursor;/, 'SHARD_PER_BURST');
out.SHARD_LIFE = need(/this\.life\[i\] = Math\.min\(1, this\.life\[i\] \+ dt \/ ([\d.]+)\);\s*\n\s*this\.vel\[i \* 3 \+ 1\] -= 16/, 'SHARD_LIFE');
out.SHARD_GRAV = need(/this\.vel\[i \* 3 \+ 1\] -= ([\d.]+) \* dt; \/\/ glass falls hard/, 'SHARD_GRAV');

// ImpactDust (src/guns.js:297-377)
out.DUST_MAX = need(/const DUST_MAX = ([\d.]+);/, 'DUST_MAX');
out.DUST_LIFE = need(/const DUST_LIFE = ([\d.]+);/, 'DUST_LIFE');
out.DUST_GRAV = need(/this\.vel\[i \* 3 \+ 1\] -= ([\d.]+) \* dt; \/\/ dust settles/, 'DUST_GRAV');

// GunSound (src/guns.js:261-293)
out.GUNSOUND_BREECH_FREQ_BASE = need(/bp\.frequency\.value = ([\d.]+) \+ Math\.random/, 'GUNSOUND_BREECH_FREQ_BASE');
out.GUNSOUND_BREECH_FREQ_RANGE = need(/bp\.frequency\.value = [\d.]+ \+ Math\.random\(\) \* ([\d.]+);/, 'GUNSOUND_BREECH_FREQ_RANGE');
out.GUNSOUND_BREECH_Q = need(/bp\.Q\.value = ([\d.]+);/, 'GUNSOUND_BREECH_Q');
out.GUNSOUND_BREECH_GAIN = need(/g\.gain\.setValueAtTime\(([\d.]+) \* vol, t\);\s*\n\s*g\.gain\.exponentialRampToValueAtTime\(0\.001, t \+ 0\.09\)/, 'GUNSOUND_BREECH_GAIN');
out.GUNSOUND_BREECH_DUR = need(/g\.gain\.exponentialRampToValueAtTime\(0\.001, t \+ ([\d.]+)\);\s*\n\s*src\.connect/, 'GUNSOUND_BREECH_DUR');
out.GUNSOUND_THUMP_F0 = need(/o\.frequency\.setValueAtTime\(([\d.]+), t\);/, 'GUNSOUND_THUMP_F0');
out.GUNSOUND_THUMP_F1 = need(/o\.frequency\.exponentialRampToValueAtTime\(([\d.]+), t \+ 0\.05\);/, 'GUNSOUND_THUMP_F1');
out.GUNSOUND_THUMP_GAIN = need(/og\.gain\.setValueAtTime\(([\d.]+) \* vol, t\);/, 'GUNSOUND_THUMP_GAIN');
out.GUNSOUND_THUMP_DUR = need(/o\.stop\(t \+ ([\d.]+)\);/, 'GUNSOUND_THUMP_DUR');

// DecalLayer construction (src/guns.js:388-389) + stamp()'s own constants
out.HOLES_BASE_SIZE = need(/this\.holes = new DecalLayer\(game\.scene, makeHoleTexture\(\), ([\d.]+), 1600\);/, 'HOLES_BASE_SIZE');
out.HOLES_MAX = need(/this\.holes = new DecalLayer\(game\.scene, makeHoleTexture\(\), [\d.]+, (\d+)\);/, 'HOLES_MAX');
out.CRACKS_BASE_SIZE = need(/this\.cracks = new DecalLayer\(game\.scene, makeCrackTexture\(\), ([\d.]+), 400\);/, 'CRACKS_BASE_SIZE');
out.CRACKS_MAX = need(/this\.cracks = new DecalLayer\(game\.scene, makeCrackTexture\(\), [\d.]+, (\d+)\);/, 'CRACKS_MAX');
out.DECAL_LIFT = need(/addScaledVector\(normal, ([\d.]+)\)/, 'DECAL_LIFT');
out.STAMP_SCALE_MIN = need(/this\._s\.setScalar\(scale \* \(([\d.]+) \+ Math\.random\(\) \* ([\d.]+)\)\);/, 'STAMP_SCALE_MIN');
{
  const m = src.match(/this\._s\.setScalar\(scale \* \(([\d.]+) \+ Math\.random\(\) \* ([\d.]+)\)\);/);
  out.STAMP_SCALE_RANGE = m ? m[2] : null;
}

// _showToast / _updateCrosshair (src/guns.js:624-656)
out.TOAST_PCT_HIGH = need(/pct > (\d+) \? 'linear-gradient\(90deg,#29c6ff/, 'TOAST_PCT_HIGH');
out.TOAST_PCT_MID = need(/pct > (\d+) \? 'linear-gradient\(90deg,#ffb02e/, 'TOAST_PCT_MID');
out.TOAST_DURATION = need(/classList\.remove\('hidden'\);\s*\n\s*this\._toastT = ([\d.]+);/, 'TOAST_DURATION');
out.CROSSHAIR_AIM_DIST = need(/fwd\.multiplyScalar\((\d+)\)\.add\(g\.plane\.position\)\.project\(g\.camera\);/, 'CROSSHAIR_AIM_DIST');

for (const [k, v] of Object.entries(out)) console.log(`${k}=${v}`);
