// JS side of the "crane5" port unit: WarehouseCrane.ScaleToFit/
// HookLocalInCrane (src/crane.js:167, 240-241) and cinema.js's own card-
// placement decision logic + the crate stage's final assembly step
// (Cinema/CinemaRangeMath.CardPlacement, CrateRowBuilder.Apply —
// src/cinema.js:1836-1837,1844, 1716-1720).
//
// Neither crane.js nor cinema.js exports any of this — every function below
// is a REPLICATION of an unexported expression, cited by line number, the
// same documented pattern this project already uses elsewhere (see
// tools/_probe_crane.mjs's own header).
//
//   node tools/_probe_crane5.mjs
const f = (v, n = 4) => Number(v).toFixed(n);

// ---------------------------------------------------------------------------
// WarehouseCrane.ScaleToFit — crane.js:167 `const s = H > TOP ? TOP / H : 1`.
// ---------------------------------------------------------------------------
const TOP = 13.7; // crane.js:64
function scaleToFit(measuredHeight, cap = TOP) {
  return measuredHeight > cap ? cap / measuredHeight : 1;
}
console.log(`CRANE5_SCALE_REAL_SIZE=${f(scaleToFit(10.0))}`);   // under the cap -> 1 (real size)
console.log(`CRANE5_SCALE_CAPPED=${f(scaleToFit(27.4))}`);      // double the cap -> 0.5

// ---------------------------------------------------------------------------
// WarehouseCrane.HookLocalInCrane — crane.js:240-241.
// ---------------------------------------------------------------------------
function hookLocalInCrane(hook, feetCenter, boxMinY, scale) {
  return {
    x: (hook.x - feetCenter.x) * scale,
    y: (hook.y - boxMinY) * scale,
    z: (hook.z - feetCenter.z) * scale,
  };
}
const hookAt = hookLocalInCrane({ x: 0.3, y: 7.9, z: 1.65 }, { x: 0.1, y: 0, z: -1.7 }, 0.0, 0.8);
console.log(`CRANE5_HOOKAT_X=${f(hookAt.x)}`);
console.log(`CRANE5_HOOKAT_Y=${f(hookAt.y)}`);
console.log(`CRANE5_HOOKAT_Z=${f(hookAt.z)}`);
// expect x=(0.3-0.1)*0.8=0.16, y=(7.9-0)*0.8=6.32, z=(1.65-(-1.7))*0.8=2.68

// ---------------------------------------------------------------------------
// Cinema/CinemaRangeMath.CardPlacement — cinema.js:1836-1837,1844.
// ---------------------------------------------------------------------------
function acrossOffset(c) { return c.at ?? c.x ?? 0; }
function usesOwnLane(c) { return !c.at && !c.x; }
function cardDistance(c, behind) { return c.dist ?? ((c.z ?? 0) - behind); }

// the range scene's own CARDS1 shape ({x,z}) and the crates scene's ({at,dist})
const rangeCard = { x: 3.3, z: 12.4 };
const cratesCard = { at: -0.9295, dist: 7.0 };
console.log(`CRANE5_CARD_RANGE_OFFSET=${f(acrossOffset(rangeCard))}`);
console.log(`CRANE5_CARD_RANGE_OWNLANE=${usesOwnLane(rangeCard)}`);
console.log(`CRANE5_CARD_RANGE_DIST=${f(cardDistance(rangeCard, 4.8))}`);   // 12.4-4.8=7.6
console.log(`CRANE5_CARD_CRATES_OFFSET=${f(acrossOffset(cratesCard))}`);
console.log(`CRANE5_CARD_CRATES_OWNLANE=${usesOwnLane(cratesCard)}`);
console.log(`CRANE5_CARD_CRATES_DIST=${f(cardDistance(cratesCard, 4.8))}`); // explicit dist wins -> 7.0
// his own lane: neither `at` nor `x` set at all
const ownLaneCard = { z: 11.0 };
console.log(`CRANE5_CARD_OWNLANE_TRUE=${usesOwnLane(ownLaneCard)}`);

// ---------------------------------------------------------------------------
// Cinema/CinemaRangeMath.CrateRowBuilder.Apply — cinema.js:1716-1720.
// ---------------------------------------------------------------------------
function applyCrateStage(stage, output) {
  return {
    cubicle: stage.cubicle - output.shift,
    counter: stage.counter - output.shift,
    o: output.row.o,
    y: output.row.o.y,
    row: output.row,
  };
}
const stage = { cubicle: 2.3, counter: 5.8, o: { x: 0, y: 0, z: 0 } };
const output = { shift: -5.5973, row: { o: { x: -3.7782, y: 0.0, z: 49.5973 }, len: 2.6683 } };
const applied = applyCrateStage(stage, output);
console.log(`CRANE5_APPLY_CUBICLE=${f(applied.cubicle)}`);   // 2.3-(-5.5973)=7.8973
console.log(`CRANE5_APPLY_COUNTER=${f(applied.counter)}`);   // 5.8-(-5.5973)=11.3973
console.log(`CRANE5_APPLY_O_X=${f(applied.o.x)}`);
console.log(`CRANE5_APPLY_O_Z=${f(applied.o.z)}`);
console.log(`CRANE5_APPLY_Y=${f(applied.y)}`);

console.log('CRANE5_PROBE_DONE=1');
