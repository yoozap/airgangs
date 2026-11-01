// The JS side of the CharCombatProbe check — the three named gaps this pass
// closed: Character/ShoulderPocket.cs (characters.js:927 _measurePocket),
// Combat/DamageDecals.cs (src/planedamage.js:1668-1998 + src/glass.js:34-85
// canvas art), Combat/TargetRegistry.cs + UI/Target/TargetRingView.cs
// (src/target.js's registry/pick/cycle/ring half).
//
// Plain Node, no Playwright, no browser shim: every fact this script can
// state honestly is a MODULE-PRIVATE CONSTANT, transcribed verbatim with its
// own source line cited — same precedent as tools/_probe_damage.mjs's
// tearRadius/bridgeOf. Everything else CharCombatProbe.cs proves (the real
// per-vertex ellipse fit on a loaded skinned body, the real leg-bend angles,
// whether a generated Texture2D actually has ink on it, a Unity Transform/
// Collider parent-walk, a Unity Camera's ScreenPointToRay) has NO Node-only
// equivalent — src/characters.js never measures a loaded GLB's vertices
// without a live Three.js scene, and src/target.js's picking is built on
// THREE.Raycaster against a live scene graph. Those are Unity-only facts,
// the same honesty standard tools/_probe_character.mjs's own "NOT comparable
// 1:1" note set for the BODY/RIG lines.
//
//   node tools/_probe_charcombat.mjs
const f = (v, n = 4) => Number(v).toFixed(n);

// ---- src/target.js:31-34 (module-private, never exported) ----
const PICK_RANGE = 700;   // target.js:31
const CYCLE_RANGE = 500;  // target.js:32
const CYCLE_DOT = 0.25;   // target.js:33
const WHEEL = 1.7;        // target.js:34

console.log(`CONST id=_const pickRangeM=${f(PICK_RANGE, 1)} cycleRangeM=${f(CYCLE_RANGE, 1)} cycleDot=${f(CYCLE_DOT, 3)}`);
console.log(`RINGCONST id=_const wheelHeightM=${f(WHEEL, 1)}`);

// ---- src/characters.js:385-387 (module-private, never exported) ----
const POCKET_AHEAD = 0.22;   // characters.js:385, rad
const POCKET_PROUD = 0.015;  // characters.js:386, m
const POCKET_DROP = 0.05;    // characters.js:387, m

console.log(`POCKETCONST id=_const pocketAheadRad=${f(POCKET_AHEAD, 3)} pocketProudM=${f(POCKET_PROUD, 4)} pocketDropM=${f(POCKET_DROP, 3)}`);

// ---- src/target.js:256-277 _ensureRing's band-width formula, Math.max(3,
// (halfSpan ?? 6) * 1.06) / clamp(r*0.06, 0.35, 1.6) — pure math, no scene
// needed, so THIS one is a genuine cross-engine check (not just a transcribed
// constant): run the same formula both sides and diff the RESULT numbers. ----
function ringRadius(halfSpan) { return Math.max(3, (halfSpan ?? 6) * 1.06); }
function ringBand(r) { return Math.min(1.6, Math.max(0.35, r * 0.06)); }
for (const [id, halfSpan] of [['spitfire', 5.5], ['an225', 44], ['default', null]]) {
  const r = ringRadius(halfSpan);
  console.log(`RING id=${id} radiusM=${f(r, 4)} bandM=${f(ringBand(r), 4)}`);
}

// ---- target.js:319 pulse / :320 colour — also pure math, diffable. ----
function pulseOpacity(dead, t) { return dead ? 0.25 : 0.45 + 0.25 * Math.sin(t * 3); }
for (const t of [0, 0.5, 1.2]) {
  console.log(`PULSE id=t${f(t, 1)}_alive alpha=${f(pulseOpacity(false, t), 4)}`);
  console.log(`PULSE id=t${f(t, 1)}_dead alpha=${f(pulseOpacity(true, t), 4)}`);
}
