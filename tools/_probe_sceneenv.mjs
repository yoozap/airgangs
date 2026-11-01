// Verification for Core/SceneEnvironmentSetup.cs — src/game.js:242-339
// `_setupScene()`'s renderer/camera/sky/sun/hemisphere/fog half.
//
//   node tools/_probe_sceneenv.mjs
//
// Prints the handful of CLEAN NUMERIC constants read straight out of
// src/game.js by regex, meant to pair 1:1 (same KEY, same number, within
// unity_port_check.py's tolerance) against unity/Assets/Editor/Probes/
// SceneEnvironmentProbe.cs's own `Fact(...)` lines of the same name — which
// that probe reads back off the REAL Camera/Light/RenderSettings/URP-asset/
// Volume-profile state after running Core/SceneEnvironmentSetup.Setup() for
// real (see that file's own header; it was run once this pass, 25/25 PASS).
//
// Deliberately narrow: colours, enum names (Trilight/ExponentialSquared/Soft)
// and the procedural-skybox approximation have no literal game.js counterpart
// to regex out, so they are NOT printed here — see unity_port_check.py's own
// JSONLY/CSONLY note for why an unpaired line is expected and fine, not a bug.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const gameJs = readFileSync(path.join(ROOT, 'src/game.js'), 'utf8');

function fact(key, value) {
  console.log(`${key}=${value}`);
}
function num(re, src = gameJs) {
  const m = src.match(re);
  if (!m) throw new Error(`pattern not found: ${re}`);
  return Number(m[1]);
}

// ---- camera: game.js:279-284 `new THREE.PerspectiveCamera(62, aspect, 1, 60000)` ----
fact('CAM_FOV', num(/new THREE\.PerspectiveCamera\(\s*(\d+)/));
fact('CAM_NEAR', num(/this\.container\.clientWidth \/ this\.container\.clientHeight,\s*\n\s*(\d+),/));
fact('CAM_FAR', num(/this\.container\.clientWidth \/ this\.container\.clientHeight,\s*\n\s*\d+,\s*\n\s*(\d+)/));

// ---- sun: game.js:315/320 ----
fact('SUN_INTENSITY', num(/new THREE\.DirectionalLight\(0xfff0dc,\s*([\d.]+)\)/));
fact('SUN_SHADOW_STRENGTH', num(/sun\.shadow\.intensity = ([\d.]+)/));

// ---- hemisphere: game.js:339 ----
fact('AMBIENT_INTENSITY', num(/new THREE\.HemisphereLight\(0xbdd8ff, 0x77664e, ([\d.]+)\)/));

// ---- fog: game.js:277 ----
fact('FOG_DENSITY', num(/new THREE\.FogExp2\(0xaec4d8, ([\d.]+)\)/));

// ---- shadow box: game.js:38 `SHADOW_NEAR_M = 160` (see SceneEnvironmentSetup.cs's
// own header for why this, not SHADOW_FAR_M, is the one static URP ShadowDistance) ----
fact('URP_SHADOW_DISTANCE', num(/const SHADOW_NEAR_M = (\d+)/));

// ---- tonemap exposure, converted to EV stops (URP's ColorAdjustments.postExposure
// unit) exactly as SceneEnvironmentProbe.cs's own check computes it ----
const exposureMultiplier = num(/this\.renderer\.toneMappingExposure = ([\d.]+)/);
fact('VOL_POST_EXPOSURE', Math.log2(exposureMultiplier));

// ---- bloom: game.js:399 `new UnrealBloomPass(new THREE.Vector2(w/4,h/4),
// 0.14, 0.5, 0.9)` -- (strength, radius, threshold) ----
const bloomArgs = gameJs.match(/new UnrealBloomPass\(new THREE\.Vector2\(w \/ 4, h \/ 4\), ([\d.]+), ([\d.]+), ([\d.]+)\)/);
if (!bloomArgs) throw new Error('UnrealBloomPass constructor not found');
fact('VOL_BLOOM_INTENSITY', Number(bloomArgs[1]));
fact('VOL_BLOOM_SCATTER', Number(bloomArgs[2]));
fact('VOL_BLOOM_THRESHOLD', Number(bloomArgs[3]));

// Pair against unity/Assets/Editor/Probes/SceneEnvironmentProbe.cs via:
//   python3 tools/unity_port_check.py <(node tools/_probe_sceneenv.mjs) <(grep -E "^[A-Z_]+=" scene_env_probe.log)

// ---------------------------------------------------------------------------
// WarnGrid reachability — this is NOT this pass's new code (Core/WarnGrid.cs
// and its wiring in CombatWorldAdapter.cs already existed, uncommitted, from
// an earlier pass) but it was sitting UNCLAIMED in docs/port_units/*.json, so
// this pass is filing it. Before doing that: a source-text check that the
// JS's game.js:408-414/2460-2492 warning grid is actually CONSTRUCTED and
// WIRED at runtime, not just defined — the exact "0% reached" trap this
// port's own skill doc warns about. Not a runtime proof by itself (no editor
// access was spent on a second scene for this); Boot.cs really does call
// CombatWorldAdapter.Install(Game) (Boot.cs:421, confirmed by direct read),
// which really does construct+assign WarnGridRef (CombatWorldAdapter.cs:536-
// 540, confirmed by direct read) — recorded here so the chain of custody for
// the claim is a grep, not just this session's say-so.
// ---------------------------------------------------------------------------
console.log('---- WarnGrid wiring presence (source-text check) ----');
function check(key, cond) {
  console.log(`JSONLY ${cond ? 'PASS' : 'FAIL'} ${key}`);
}
const bootCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/Boot.cs'), 'utf8');
const adapterCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/CombatWorldAdapter.cs'), 'utf8');
check('boot_calls_combatworldadapter_install', /CombatWorldAdapter\.Install\(Game\)/.test(bootCs));
check('adapter_constructs_warngrid', /AddComponent<WarnGrid>\(\)/.test(adapterCs));
check('adapter_assigns_warngridref', /game\.WarnGridRef\s*=/.test(adapterCs));
