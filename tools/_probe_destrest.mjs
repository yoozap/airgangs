// Verification for the destruction-side gap-closing pass covering
// src/rockets.js, src/dents.js, src/impactfx.js and src/planedamage.js
// (docs/UNITY_PORT.md's "destrest" unit), PLUS the wiring gap that pass was
// told was the highest-value piece: Combat/PlaneAirframeHost.cs (a per-plane
// AirframeBreakup instance + wing-mesh resolution, so Core/PlaneHullDamage.cs's
// OnDetach/OnCrashScatter/OnSmokeClear/OnWingBend/OnWingRepair hooks have
// something to call — none of that existed in this port before this pass).
//
//   node tools/_probe_destrest.mjs
//
// THREE kinds of check here, all printed as KEY=value / PASS|FAIL lines meant
// to pair 1:1 with unity/Assets/Editor/Probes/DestRestProbe.cs's own output
// (not executed this session — no editor access, see this project's standing
// "coordinator holds the batch-mode lock" rule):
//   1. CONSTANTS read out of src/rockets.js and src/game.js by regex — diff
//      byte-for-byte against the C# `internal const` fields they were ported
//      into (Combat/Rockets.cs, Combat/PlaneAirframeHost.cs).
//   2. WIRING PRESENCE: a plain source-text check that the C# side actually
//      contains the call/assignment this pass's brief asked for — the honest,
//      Node-only half of "did I actually write it," same convention as
//      tools/_probe_corerest.mjs.
//   3. THE CONFIRMED GAP: a source-text check that Core/GameLoop.cs — read
//      only, never edited by this pass — still does NOT wire the 5 named
//      hooks, so the next pass (or the coordinator) can re-run this after
//      wiring them and watch these 3 lines flip from PASS (gap confirmed
//      open) to FAIL (gap closed), which is the intended lifecycle.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const rocketsJs = readFileSync(path.join(ROOT, 'src/rockets.js'), 'utf8');
const gameJs = readFileSync(path.join(ROOT, 'src/game.js'), 'utf8');
const planesJs = readFileSync(path.join(ROOT, 'src/planes.js'), 'utf8');
const rocketsCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Combat/Rockets.cs'), 'utf8');
const hostCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Combat/PlaneAirframeHost.cs'), 'utf8');
const planeHullDamageCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/PlaneHullDamage.cs'), 'utf8');
const gameLoopCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/GameLoop.cs'), 'utf8');
const damageDecalsCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Combat/DamageDecals.cs'), 'utf8');
const damageTexturesCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Combat/DamageTextures.cs'), 'utf8');

let allPass = true;
function fact(key, value) { console.log(`${key}=${value}`); }
function check(key, cond, note) {
  // JSONLY: every check() here is a regex over SOURCE TEXT (src/*.js or a .cs
  // file). The C# probe cannot mirror a grep, so these can never pair — the
  // marker tells tools/unity_port_check.py to count them, not pair them.
  console.log(`JSONLY ${cond ? 'PASS' : 'FAIL'} ${key}${note ? ` (${note})` : ''}`);
  if (!cond) allPass = false;
}
function num(re, src) {
  const m = src.match(re);
  if (!m) throw new Error(`pattern not found: ${re}`);
  return Number(m[1]);
}

// ---------------------------------------------------------------------------
// 1. src/rockets.js top-level constants (Combat/Rockets.cs's public consts)
// ---------------------------------------------------------------------------
fact('COOLDOWN', num(/const COOLDOWN = ([\d.]+);/, rocketsJs));
fact('DAMAGE', num(/const DAMAGE = ([\d.]+);/, rocketsJs));
fact('BLAST_R', num(/const BLAST_R = ([\d.]+);/, rocketsJs));
fact('HOLE_R', num(/const HOLE_R = ([\d.]+);/, rocketsJs));
fact('GLASS_R', num(/const GLASS_R = ([\d.]+);/, rocketsJs));
fact('RANGE', num(/const RANGE = ([\d.]+);/, rocketsJs));
fact('ACCEL', num(/const ACCEL = ([\d.]+);/, rocketsJs));
fact('TOP_SPEED', num(/const TOP_SPEED = ([\d.]+);/, rocketsJs));
fact('MAX_LIVE', num(/const MAX_LIVE = (\d+);/, rocketsJs));

// ---------------------------------------------------------------------------
// 2. src/rockets.js:99-130 RocketSound.whoosh (this pass's new audio wiring)
// ---------------------------------------------------------------------------
fact('WHOOSH_BP_FREQ0', num(/bp\.frequency\.setValueAtTime\((\d+), t\);/, rocketsJs));
fact('WHOOSH_BP_FREQ1', num(/bp\.frequency\.exponentialRampToValueAtTime\((\d+), t \+ 0\.45\);/, rocketsJs));
fact('WHOOSH_BP_Q', num(/bp\.Q\.value = ([\d.]+);/, rocketsJs));
fact('WHOOSH_BP_RATE', num(/src\.playbackRate\.value = ([\d.]+);/, rocketsJs));
fact('WHOOSH_BP_GAIN', num(/g\.gain\.exponentialRampToValueAtTime\(([\d.]+) \* vol, t \+ 0\.06\);/, rocketsJs));
fact('WHOOSH_BP_DUR', num(/src\.stop\(t \+ ([\d.]+)\);/, rocketsJs));
fact('WHOOSH_TONE_F0', num(/o\.frequency\.setValueAtTime\((\d+), t\);/, rocketsJs));
fact('WHOOSH_TONE_F1', num(/o\.frequency\.exponentialRampToValueAtTime\((\d+), t \+ 0\.12\);/, rocketsJs));
fact('WHOOSH_TONE_GAIN', num(/og\.gain\.setValueAtTime\(([\d.]+) \* vol, t\);/, rocketsJs));
fact('WHOOSH_TONE_DUR', num(/o\.stop\(t \+ ([\d.]+)\);/, rocketsJs));

// ---------------------------------------------------------------------------
// 3. src/game.js:2278-2335 — the wing mesh/material mutation half
//    (Combat/PlaneAirframeHost.cs's BendWing/RepairWing/WingTipBone)
// ---------------------------------------------------------------------------
fact('WING_DROOP_RAD', num(/rotation\.z \+= side === 'right' \? -([\d.]+) : [\d.]+;/, gameJs));
const charHex = gameJs.match(/m\.color\.setHex\((0x[0-9a-fA-F]+)\); \/\/ charred/);
fact('CHAR_COLOR_HEX', charHex ? charHex[1] : 'MISSING');
fact('CHAR_ROUGHNESS', num(/m\.roughness = (\d+);\s*\n\s*\}\s*\n\s*\}\s*\n\s*this\._setHint/, gameJs));
fact('REPAIR_ROUGHNESS', num(/m\.roughness = ([\d.]+);\s*\n\s*\}\s*\n\s*\}\s*\n\s*this\.smoke\.clear/, gameJs));

// ---------------------------------------------------------------------------
// 4. src/planes.js:1594-1611 wing-mesh classification by NAME prefix
// ---------------------------------------------------------------------------
check('planesjs_right_prefix_sagkanatsekil', /startsWith\('sagkanatsekil'\)/.test(planesJs));
check('planesjs_left_prefix_solkanatsekil', /startsWith\('solkanatsekil'\)/.test(planesJs));
check('host_right_prefix_sagkanatsekil', hostCs.includes('"sagkanatsekil"'));
check('host_left_prefix_solkanatsekil', hostCs.includes('"solkanatsekil"'));
check('host_classifies_by_local_bbox_center_x', /local\.center\.x > 0f/.test(hostCs));

// ---------------------------------------------------------------------------
// 5. WIRING PRESENCE — Rockets.cs actually calls the audio this pass added
// ---------------------------------------------------------------------------
check('rockets_calls_whoosh_on_fire', /Whoosh\(MasterVolume\(\)\);/.test(rocketsCs));
check('rockets_calls_fxaudio_burstsweep', /FxAudio\.BurstSweep\(/.test(rocketsCs));
check('rockets_calls_fxaudio_tone', /FxAudio\.Tone\(/.test(rocketsCs));
check('rockets_calls_detonation', /FxAudio\.Detonation\(dist, vol, 1f\);/.test(rocketsCs));
check('rockets_calls_debrisrain', /FxAudio\.DebrisRain\(dist, vol,/.test(rocketsCs));
check('rockets_calls_collapserumble', /FxAudio\.CollapseRumble\(dist, vol,/.test(rocketsCs));
check('rockets_has_cameraposition_hook', /public Func<Vector3> CameraPosition/.test(rocketsCs));
check('rockets_has_mastervolume_hook', /public Func<float> MasterVolume/.test(rocketsCs));

// ---------------------------------------------------------------------------
// 6. WIRING PRESENCE — PlaneAirframeHost.cs exposes one call per named hook
// ---------------------------------------------------------------------------
check('host_has_airframebreakup', /public readonly AirframeBreakup Breakup;/.test(hostCs));
check('host_has_detachpart_for_ondetach', /public bool DetachPart\(string part, float spin, bool burn\)/.test(hostCs));
check('host_has_crashscatter_for_oncrashscatter', /public void CrashScatter\(Vector3 baseVel, float severity\)/.test(hostCs));
check('host_has_clearwingsmoke_for_onsmokeclear', /public void ClearWingSmoke\(\)/.test(hostCs));
check('host_has_bendwing_for_onwingbend', /public void BendWing\(string side\)/.test(hostCs));
check('host_has_repairwing_for_onwingrepair', /public void RepairWing\(string side\)/.test(hostCs));
check('host_has_wingtipbone_for_wingtip_func', /public Transform WingTipBone\(string side\)/.test(hostCs));

// ---------------------------------------------------------------------------
// 7. THE CONFIRMED GAP — Core/PlaneHullDamage.cs HAS the 5 hooks, but
//    Core/GameLoop.cs (read-only, not edited by this pass) does not yet
//    assign any of them. Re-run this probe after wiring; these 5 should flip
//    from PASS (gap open) to FAIL (gap closed, which is what we want then).
// ---------------------------------------------------------------------------
check('hull_declares_ondetach', /public Action<string, float, bool> OnDetach;/.test(planeHullDamageCs));
check('hull_declares_oncrashscatter', /public Action<Vector3, float> OnCrashScatter;/.test(planeHullDamageCs));
check('hull_declares_onsmokeclear', /public Action OnSmokeClear;/.test(planeHullDamageCs));
check('hull_declares_onwingbend', /public Action<string> OnWingBend;/.test(planeHullDamageCs));
check('hull_declares_onwingrepair', /public Action<string> OnWingRepair;/.test(planeHullDamageCs));
check('GAP_STILL_OPEN_ondetach_unwired_in_gameloop', !/p\.Hull\.OnDetach\s*=/.test(gameLoopCs));
check('GAP_STILL_OPEN_oncrashscatter_unwired_in_gameloop', !/p\.Hull\.OnCrashScatter\s*=/.test(gameLoopCs));
check('GAP_STILL_OPEN_onsmokeclear_unwired_in_gameloop', !/p\.Hull\.OnSmokeClear\s*=/.test(gameLoopCs));
check('GAP_STILL_OPEN_onwingbend_unwired_in_gameloop', !/p\.Hull\.OnWingBend\s*=/.test(gameLoopCs));
check('GAP_STILL_OPEN_onwingrepair_unwired_in_gameloop', !/p\.Hull\.OnWingRepair\s*=/.test(gameLoopCs));

// ---------------------------------------------------------------------------
// 8. planedamage.js double-count check: PlaneDamage.cs's own manifest note
//    says the 8 canvas-decal functions are "NOT COVERED... deferred as
//    art/VFX" — but DamageDecals.cs already covers exactly that span. Confirm
//    DamageDecals.cs genuinely contains all 8, so the next agent does not
//    re-port them a second time off a stale note.
// ---------------------------------------------------------------------------
// jag -> DamageTextures.Jag; punch (:1784, the destination-out punch) -> folded
// into DamageTextures' Over/default-transparent semantics, documented in its own
// header; atlasTexture -> DamageDecals.BuildAtlas; skinScrapeTexture ->
// DamageDecals.DrawSkinScrape/BuildScrapeTexture — different NAMES, same function,
// per src/planedamage.js:1660-1998's own line citations in DamageDecals.cs's header.
check('coverage_jag', /static .*Jag\(/.test(damageTexturesCs) || /static .*Jag\(/.test(damageDecalsCs));
check('coverage_punch', /planedamage\.js:1784 `punch`/.test(damageTexturesCs));
check('coverage_atlastexture', /BuildAtlas\(/.test(damageDecalsCs));
check('coverage_drawskinhole', damageDecalsCs.includes('DrawSkinHole'));
check('coverage_drawskintear', damageDecalsCs.includes('DrawSkinTear'));
check('coverage_drawskinexit', damageDecalsCs.includes('DrawSkinExit'));
check('coverage_skinscrapetexture', damageDecalsCs.includes('DrawSkinScrape') || damageDecalsCs.includes('BuildScrapeTexture'));
check('coverage_drawcraze', damageDecalsCs.includes('DrawCraze'));

// ---------------------------------------------------------------------------
// 9. tools/_probe_damagetex.mjs spall fix (this pass's other deliverable):
//    confirm the quantize8 step this pass added is actually in the file.
// ---------------------------------------------------------------------------
const damagetexMjs = readFileSync(path.join(ROOT, 'tools/_probe_damagetex.mjs'), 'utf8');
check('damagetex_mjs_quantizes_to_8bit', /function quantize8\(buf\)/.test(damagetexMjs) && /quantize8\(buf\);/.test(damagetexMjs));

// JSONLY: this is THIS SIDE's own verdict and it means the OPPOSITE of the C#
// probe's line of the same name. Five of the checks above are inverted by
// design (section 3: `GAP_STILL_OPEN_*` PASSes while GameLoop.cs has NOT wired
// the hook and FAILs once it has), so once a later pass wired them this side
// went permanently false while DestRestProbe.cs's ALL_PASS says "every C#-side
// assertion held". Pairing the two compared a bool against its own antonym:
// while DestRestProbe.cs ALSO had a failing check (host_bendwing_chars_material,
// which was reading "_Color" where the code writes "_BaseColor" — fixed) the two
// falses matched by accident and the mismatch was invisible.
console.log(`JSONLY ALL_PASS=${allPass}`);
