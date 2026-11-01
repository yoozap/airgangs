// Verification for this pass's own work: GameLoop.cs's new KeepCameraOutside/
// AdaptResolution/ApplyResume/ReseatOnScenery/dispose pieces, Fx/FxLightPool.cs,
// and the wiring the previous pass named but could not apply (PlaneController's
// Airborne branch, PlaneWallContact.RamOther, PlaneHullDamage's hooks,
// GameLoop.ResetPlane() -> Hull.ResetAll()).
//
//   node tools/_probe_corerest.mjs
//
// TWO KINDS of check here, both printed as KEY=value / PASS|FAIL lines meant
// to pair 1:1 with unity/Assets/Editor/Probes/CoreRestProbe.cs's own output
// (not executed this session — no editor access, see that file's header):
//   1. CONSTANTS read out of src/game.js by regex, same convention as
//      tools/_probe_gamerest.mjs — these diff byte-for-byte against the C#
//      `internal const` fields.
//   2. WIRING PRESENCE: a plain source-text check that the C# side actually
//      contains the call/assignment this pass's brief asked for. This is
//      NOT a runtime proof (CoreRestProbe.cs's own PASS/FAIL lines are the
//      runtime proof, once someone with editor access runs it) — it is the
//      honest, Node-only half of "did I actually write the wiring, not just
//      talk about it in a comment."
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const gameJs = readFileSync(path.join(ROOT, 'src/game.js'), 'utf8');
const gameLoopCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/GameLoop.cs'), 'utf8');
const planeControllerCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Flight/PlaneController.cs'), 'utf8');
const planeWallContactCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/PlaneWallContact.cs'), 'utf8');
const planeHullDamageCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/PlaneHullDamage.cs'), 'utf8');
const planeRaycastWorldCs = readFileSync(path.join(ROOT, 'unity/Assets/Scripts/Core/PlaneRaycastWorld.cs'), 'utf8');

let allPass = true;
function fact(key, value) {
  console.log(`${key}=${value}`);
}
function check(key, cond, note) {
  console.log(`JSONLY ${cond ? 'PASS' : 'FAIL'} ${key}${note ? ` (${note})` : ''}`);
  if (!cond) allPass = false;
}
function num(re, src = gameJs) {
  const m = src.match(re);
  if (!m) throw new Error(`pattern not found: ${re}`);
  return Number(m[1]);
}

// ---- constants, src/game.js ----
fact('CAM_MIN', num(/const CAM_MIN = ([\d.]+)/));
fact('CAM_SKIN', num(/const CAM_SKIN = ([\d.]+)/));
fact('ADAPT_EMA_EWMA', num(/p\.ema \+= \(Math\.min\(dt, 0\.1\) \* 1000 - p\.ema\) \* ([\d.]+)/));
fact('ADAPT_COOL_SECONDS', num(/cool: (\d+)/));
fact('ADAPT_BUDGET_MS', num(/p\.ema <= (\d+)/));
fact('ADAPT_DROPPED_SCALE', num(/this\._resScale = ([\d.]+)/));
fact('FX_LIGHT_POOL_SIZE', num(/for \(let i = 0; i < (\d+); i\+\+\) \{\s*\n\s*const l = new THREE\.PointLight/));

console.log('---- wiring presence (source-text check, not a runtime proof — see header) ----');

// ---- PlaneController.cs: the Airborne branch dispatch ----
check('plane_prevpos_field', /public Vector3 PrevPos \{ get; private set; \}/.test(planeControllerCs));
check('plane_airborne_field', /public bool Airborne = true;/.test(planeControllerCs));
check('plane_step_branches_airborne', /if \(Airborne\)/.test(planeControllerCs) && /GroundRoll\.Step\(/.test(planeControllerCs));
check('plane_touchwall_called_airborne_branch', /WallContact\.TouchWall\(dt, transform, true,/.test(planeControllerCs));
check('plane_touchwall_called_groundroll_branch', /WallContact\.TouchWall\(d, transform, false,/.test(planeControllerCs));
check('plane_airbornegroundcheck_called', /GroundRoll\.AirborneGroundCheck\(/.test(planeControllerCs));
check('plane_checkcollision_wrapper', /public void CheckCollision\(\)/.test(planeControllerCs));
check('plane_ramspin_roundtrips_planephys', /PlanePhys\.RamSpin\.Value/.test(planeControllerCs));

// ---- GameLoop.cs: the wiring the previous pass could not apply ----
check('gameloop_wireplanecombat_exists', /void WirePlaneCombat\(PlaneController p\)/.test(gameLoopCs));
check('gameloop_checkcollision_same_cadence', /CheckStructuralStress\(_collisionTimer\);[^\n]*\n\s*ActivePlane\.CheckCollision\(\);/.test(gameLoopCs));
check('gameloop_resetplane_calls_hull_resetall', /ActivePlane\.Hull\?\.ResetAll\(\);/.test(gameLoopCs));
check('gameloop_ramother_closure_over_recof', /_targets\.RecOf\(hit\.collider\)/.test(gameLoopCs) && /PlanePhys\.Ram\(rec, hit, dir, sp, part, pilotState/.test(gameLoopCs));
check('gameloop_targetregistry_recof_method', /public TbilisiFly\.Flight\.PlaneRecord RecOf\(Collider c\)/.test(gameLoopCs));
check('gameloop_hull_hooks_wired_count_at_least_6',
  (gameLoopCs.match(/p\.Hull\.On\w+ = /g) || []).length >= 6,
  `found ${(gameLoopCs.match(/p\.Hull\.On\w+ = /g) || []).length}`);
check('gameloop_keepcameraoutside_exists', /void KeepCameraOutside\(\)/.test(gameLoopCs));
check('gameloop_adaptresolution_exists', /void AdaptResolution\(float dt\)/.test(gameLoopCs));
check('gameloop_applyresume_exists', /public bool ApplyResume\(JObject s/.test(gameLoopCs));
check('gameloop_reseatonscenery_exists', /public void ReseatOnScenery\(\)/.test(gameLoopCs));
check('gameloop_ondestroy_saves', /void OnDestroy\(\)[\s\S]{0,80}SaveState\(true\);/.test(gameLoopCs));

// ---- PlaneWallContact.cs: CheckCollision now returns bool (Airborne needs it) ----
check('wallcontact_checkcollision_returns_bool', /public bool CheckCollision\(/.test(planeWallContactCs));

// ---- PlaneHullDamage.cs: the 13th hook this pass added ----
check('hulldamage_oncrashed_hook', /public Action OnCrashed;/.test(planeHullDamageCs));
check('hulldamage_oncrashed_invoked_in_explode', /OnCrashed\?\.Invoke\(\);/.test(planeHullDamageCs));

// ---- PlaneRaycastWorld.cs: the IPlaneWallWorld implementation itself ----
check('raycastworld_implements_iplanewallworld', /class PlaneRaycastWorld\s*:\s*IPlaneWallWorld,\s*IGroundProbeWorld,\s*IPlanePhysicsWorld/.test(planeRaycastWorldCs));
check('raycastworld_forwards_idamageworld', /public IDamageWorld Damage \{ get; set; \}/.test(planeRaycastWorldCs));
check('raycastworld_raycastall_real', /Physics\.RaycastAll\(origin, dir\.normalized, maxDist/.test(planeRaycastWorldCs));

console.log(allPass ? 'CORERESTPROBE(JS SIDE): ALL PASS' : 'CORERESTPROBE(JS SIDE): SOME FAILED');
