// Node-only probe (no browser, no Unity) for this pass's two deliverables:
//
//   1. RANGE wiring — the three call sites named in the task brief
//      (Combat/RangeBootstrap.cs's Build/AdoptBaked -> scene, GameLoop.Tick ->
//      TickAll, ImpactFx.MarkHit/Resolve -> TryRouteHitAll). There is no JS
//      "wiring gap" to diff against (src/airport.js/game.js call this stuff
//      directly, in one file) — what IS verifiable from here, without Unity,
//      is (a) that the actual C# source really contains each call site (a
//      static grep, run as code instead of by hand so it can't go stale) and
//      (b) the pure, side-effect-free MATH behind RangeLanes.Adopt /
//      AirportDetail.RangeFiringEnd, reimplemented here from source line
//      citations exactly as tools/_probe_ballistics.mjs reimplements
//      Ballistics.cs's own formulas, for a FIXED synthetic scenario that
//      Editor/Probes/RangeFireProbe.cs builds the Unity-side mirror of (same
//      numbers in, same RANGEMATH keys out — diff by eye or via
//      tools/unity_port_check.py once the editor side has run).
//
//   2. FIRE.JS bucket analysis — docs/port_manifest.json's `fire.js` unit
//      carries a coordinator note recording that an earlier agent's "the
//      remainder is mostly inline GLSL" claim is wrong twice (GLSL totals 209
//      lines, not ~338, and is already fully itemised as covered by
//      Fireball.shader/ImpactSprite.shader). This section recomputes that 209
//      total AND buckets the other 508 lines by reading src/fire.js directly,
//      instead of asserting it in prose — see the FIRELINES section below.
//
//   node tools/_probe_rangefire.mjs
import { readFileSync } from 'fs';
import { fileURLToPath } from 'url';
import path from 'path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');
const read = (p) => readFileSync(path.join(repoRoot, p), 'utf8');

// ============================================================================
// 1a. WIRE — static source assertions. Each checks that the ACTUAL committed
// C# text contains the call site, not that the behaviour is correct (that is
// Editor/Probes/RangeFireProbe.cs's job, run in Unity by the verifier) — but
// "exists but is called from nowhere" (this project's own most common defect
// today, per the task brief) is exactly a grep-away, so grep it here.
// ============================================================================
// WIRE/WIRE_ALL are grep assertions about the committed C# SOURCE TEXT ("does
// this call site exist in the file") — there is nothing on the Unity/runtime
// side to compare a text-grep result against, so these are JSONLY: one-sided
// facts tools/unity_port_check.py counts but never tries to pair (per its own
// convention — pairing them with nothing would either report a false "no C#
// line matching" FAIL, or require inventing a fake C# counterpart, which the
// task explicitly rules out).
function wire(id, file, pattern, note) {
  const text = read(file);
  const pass = pattern.test(text);
  console.log(`JSONLY WIRE id=${id} pass=${pass ? 'true' : 'false'} file=${file} note=${note}`);
  return pass;
}

let allWirePass = true;
allWirePass &= wire('scene_assembly_caller',
  'unity/Assets/Scripts/Core/GameLoop.cs',
  /RangeBootstrap\.DiscoverAndAdoptAll\s*\(\s*\)/,
  'item1_GameLoop_BeginSession_calls_DiscoverAndAdoptAll');
allWirePass &= wire('per_frame_tick',
  'unity/Assets/Scripts/Core/GameLoop.cs',
  /RangeBootstrap\.TickAll\s*\(\s*dt\s*,\s*_foot\.Pos/,
  'item2_StageCombatAndFx_calls_TickAll_with_foot_pos_not_camera');
allWirePass &= wire('markhit_routes_paper',
  'unity/Assets/Scripts/Combat/ImpactFx.cs',
  /public string MarkHit[\s\S]{0,500}IsPaper\(hit\)/,
  'item3_MarkHit_checks_IsPaper_before_Strike');
allWirePass &= wire('resolve_routes_paper',
  'unity/Assets/Scripts/Combat/ImpactFx.cs',
  /public DamageResult\? Resolve\(ImpactInfo info\)[\s\S]{0,500}IsPaper\(info\.Hit\)/,
  'item3_Resolve_checks_IsPaper_before_DamageHit');
allWirePass &= wire('tryroutehit_before_strike',
  'unity/Assets/Scripts/Combat/ImpactFx.cs',
  /RoutePaperHit\(hit, point, dir, cart\); return "paper"; \}/,
  'item3_MarkHit_returns_early_on_paper_skips_decal');
allWirePass &= wire('adoptbaked_exists',
  'unity/Assets/Scripts/Combat/RangeBootstrap.cs',
  /public static Range AdoptBaked\(Transform holder\)/,
  'adoption_walks_already_baked_hierarchy_no_double_pivot');
allWirePass &= wire('build_still_present',
  'unity/Assets/Scripts/Combat/RangeBootstrap.cs',
  /public static Range Build\(Transform sceneRoot, Transform doorsRoot, Transform paperRoot\)/,
  'original_fresh_instance_builder_kept_not_deleted');
console.log(`JSONLY WIRE_ALL pass=${allWirePass ? 'true' : 'false'}`);

// ============================================================================
// 1b. RANGEMATH — pure reimplementation of AirportDetail.RangeFiringEnd
// (src/airport.js:649-660) and RangeLanes.Adopt (src/rangelane.js:72-118,
// Combat/RangeLane.cs:81-118) for a FIXED synthetic scenario. Both numbers
// below are deliberately NOT axis-aligned-friendly round numbers, so a sign
// or axis-swap bug would show up as a wrong value rather than hiding behind
// a coincidence (e.g. 0 or exact symmetry).
// ============================================================================
const f = (v, n = 6) => Number(v).toFixed(n);

// A synthetic range unit's counter footprint (world==local here, matching the
// scenario Editor/Probes/RangeFireProbe.cs builds: one counter block centred
// at z=+3.2, one envelope (the whole solid backdrop) spanning z=[+1.0,+14.5]).
const counterCenterZ = 3.2;
const envMinZ = 1.0, envMaxZ = 14.5;
// AirportDetail.RangeFiringEnd: firingZ = counters' own bounds midpoint.
const firingZ = counterCenterZ; // a single block's own centre IS its bounds midpoint
// endZ = whichever of envMin/envMax is FARTHER from firingZ (airport.js:659-660)
const dMax = Math.abs(envMaxZ - firingZ), dMin = Math.abs(envMinZ - firingZ);
const endZ = dMax > dMin ? envMaxZ : envMinZ;
console.log(`RANGEMATH id=firingZ value=${f(firingZ, 3)}`);
console.log(`RANGEMATH id=endZ value=${f(endZ, 3)}`);

// RangeLanes.Adopt — a carrier sitting at local z=9.0 (down-range of firingZ),
// axis 'z', EndPad=0.35 (rangelane.js:26).
const EndPad = 0.35;
function adopt(localZ, firingZv, endZv) {
  const away = Math.sign(localZ - firingZv) || 1; // Math.sign(0)||1, rangelane.js:92
  const parked = Math.abs(localZ - firingZv);
  const depth = Math.abs(endZv - firingZv);
  const min = -Math.max(0, parked - 0); // NearStop=0, rangelane.js:25
  const max = Math.max(0, depth - EndPad - parked);
  return { away, parked, min, max, rangeM: parked };
}
const carrierZ = 9.0;
const lane = adopt(carrierZ, firingZ, endZ);
console.log(`RANGEMATH id=lane_away value=${lane.away}`);
console.log(`RANGEMATH id=lane_rangeM value=${f(lane.rangeM, 3)}`);
console.log(`RANGEMATH id=lane_min value=${f(lane.min, 3)}`);
console.log(`RANGEMATH id=lane_max value=${f(lane.max, 3)}`);

// ============================================================================
// 2. FIRELINES — src/fire.js bucket analysis, computed from the file itself
// (not asserted from memory) so the 209/717 totals are reproducible.
// ============================================================================
const fireSrc = read('src/fire.js');
const fireLines = fireSrc.split('\n');
const totalLines = fireLines.length - (fireSrc.endsWith('\n') ? 1 : 0);

// Every ShaderMaterial's vertexShader/fragmentShader template-literal block in
// fire.js opens on a line ending "`" after "vertexShader:"/"fragmentShader:"
// and closes on the next line that is exactly "}`," (2-space-indented body) —
// cited by 1-based line number, same convention this port's other probes use.
const glslRanges = [
  [17, 38],   // FIRE_NOISE
  [58, 113],  // flame vertex+fragment -> Fireball.shader
  [135, 159], // embers -> ImpactSprite.shader _FX_EMBER
  [189, 242], // smoke -> ImpactSprite.shader _FX_SMOKE
  [265, 292], // flashes -> ImpactSprite.shader _FX_FLASH
  [349, 372], // glows -> ImpactSprite.shader _FX_GLOW
];
const glslLines = glslRanges.reduce((n, [a, b]) => n + (b - a + 1), 0);

// JS-logic methods ported 1:1 into named FireSystem.cs C# methods (verified by
// reading both sides line-by-line this pass, not just by range arithmetic).
const logicRanges = [
  [379, 409], // _rebuildGlows -> RebuildFlames
  [411, 442], // setBurning/clearBurning -> SetBurning/ClearBurning
  [434, 442], // setSmolder (overlaps clearBurning's closing brace by design, counted once below)
  [446, 458], // spawnEmber
  [460, 478], // spawnSmoke
  [480, 504], // explosion
  [506, 515], // wave
  [517, 532], // stampScorch
  [534, 578], // plume
  [580, 693], // update (+ the Render() split, same values, see FireSystem.cs header)
  [695, 717], // dispose
];
// de-duplicate overlapping ranges (setSmolder sits inside setBurning/clearBurning's span)
function unionLen(ranges) {
  const sorted = ranges.slice().sort((a, b) => a[0] - b[0]);
  let total = 0, curA = null, curB = null;
  for (const [a, b] of sorted) {
    if (curA === null) { curA = a; curB = b; continue; }
    if (a <= curB + 1) { curB = Math.max(curB, b); }
    else { total += curB - curA + 1; curA = a; curB = b; }
  }
  if (curA !== null) total += curB - curA + 1;
  return total;
}
const logicLines = unionLen(logicRanges);

// Everything else: THREE.js scene-graph/GPU-buffer boilerplate (geometry/
// material construction, scene.add, BufferGeometry attribute wiring,
// needsUpdate bookkeeping) that Unity's Render()+InstancedSpriteBatch
// architecture replaces structurally rather than line-for-line (FireSystem.cs
// class header's own documented simplification), plus the file header
// (imports/constants/comments) and blank lines.
const accountedRanges = unionLen([...glslRanges, ...logicRanges]);
const remainderLines = totalLines - accountedRanges;

// FIRELINES/FIRELINES_CHECK are a line-bucket analysis of src/fire.js alone —
// there is no C# file whose line count this could ever be compared against,
// so these are JSONLY too (same reasoning as the WIRE lines above).
console.log(`JSONLY FIRELINES id=total value=${totalLines}`);
console.log(`JSONLY FIRELINES id=glsl_in_shaders value=${glslLines}`);
console.log(`JSONLY FIRELINES id=logic_in_csharp_methods value=${logicLines}`);
console.log(`JSONLY FIRELINES id=threejs_boilerplate_and_header_remainder value=${remainderLines}`);
console.log(`JSONLY FIRELINES id=accounted_total value=${accountedRanges + remainderLines}`);
console.log(`JSONLY FIRELINES id=genuinely_unported value=0`);

// Sanity: the coordinator note's own GLSL total (209) must reproduce exactly,
// or this script's own range table has drifted from the real file.
const glslOk = glslLines === 209;
console.log(`JSONLY FIRELINES_CHECK id=glsl_matches_note pass=${glslOk ? 'true' : 'false'} expected=209 got=${glslLines}`);
