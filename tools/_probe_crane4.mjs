// JS side of the "crane4" port unit: WarehouseCrane.SwitchboardLayout/
// LampColors (src/crane.js:666-707, 728-736) and CinemaRangeMath's three new
// small helpers — NearestLaneAt (src/cinema.js:1744-1754 `_laneAt`),
// TrolleyRunTo (src/cinema.js:1770-1777 `_runTo`'s pure half) and
// AimPointOnCard (src/cinema.js:1783-1795 `_aimPoint`).
//
// Neither crane.js nor cinema.js exports any of this — every function below
// is a REPLICATION of an unexported closure/method, cited by line number,
// the same documented pattern this project already uses elsewhere (see
// tools/_probe_crane.mjs's own header).
//
//   node tools/_probe_crane4.mjs
const f = (v, n = 4) => Number(v).toFixed(n);

// ---------------------------------------------------------------------------
// WarehouseCrane.SwitchboardLayout — crane.js:666-707, replicated verbatim.
// ---------------------------------------------------------------------------
const BOARD = { x: 0.50, y: 0.38, z: 0.40 };   // crane.js:130
const SOCKET_Y = 1.15;                          // crane.js:131

function switchboardLayout(board, wallAt) {
  return {
    boardBoxSize: { ...BOARD },
    boardBoxCenter: { x: board.x, y: BOARD.y / 2, z: board.z },              // crane.js:667
    socketBoxSize: { x: 0.13, y: 0.34, z: 0.26 },
    socketBoxCenter: { x: wallAt - 0.065, y: SOCKET_Y, z: board.z },        // crane.js:677
    buttonPos: { x: board.x - 0.1, y: BOARD.y + 0.017, z: board.z },         // crane.js:694
    lampPos: { x: board.x + 0.11, y: BOARD.y + 0.015, z: board.z },          // crane.js:703
  };
}
const board = { x: 13.0, y: 0, z: 30.2 };
const wallAt = 23.3;
const sb = switchboardLayout(board, wallAt);
console.log(`CRANE4_SB_BOARD_CENTER_X=${f(sb.boardBoxCenter.x)}`);
console.log(`CRANE4_SB_BOARD_CENTER_Y=${f(sb.boardBoxCenter.y)}`);
console.log(`CRANE4_SB_BOARD_CENTER_Z=${f(sb.boardBoxCenter.z)}`);
console.log(`CRANE4_SB_SOCKET_CENTER_X=${f(sb.socketBoxCenter.x)}`);
console.log(`CRANE4_SB_SOCKET_CENTER_Y=${f(sb.socketBoxCenter.y)}`);
console.log(`CRANE4_SB_SOCKET_CENTER_Z=${f(sb.socketBoxCenter.z)}`);
console.log(`CRANE4_SB_BUTTON_X=${f(sb.buttonPos.x)}`);
console.log(`CRANE4_SB_BUTTON_Y=${f(sb.buttonPos.y)}`);
console.log(`CRANE4_SB_BUTTON_Z=${f(sb.buttonPos.z)}`);
console.log(`CRANE4_SB_LAMP_X=${f(sb.lampPos.x)}`);
console.log(`CRANE4_SB_LAMP_Y=${f(sb.lampPos.y)}`);
console.log(`CRANE4_SB_LAMP_Z=${f(sb.lampPos.z)}`);

// ---------------------------------------------------------------------------
// WarehouseCrane.LampColors — crane.js:728-736 `setLamp()`, replicated.
// ---------------------------------------------------------------------------
function lampColors(mode) {   // mode: 0=off, 1=lo, 2=hi
  const on = mode > 0;
  const hot = mode === 2;
  return {
    lamp: !on ? 0x3a1010 : hot ? 0xff3a2a : 0x2bff5a,
    emissive: !on ? 0x000000 : hot ? 0xdd2211 : 0x22cc44,
    button: !on ? 0x1f6b2a : hot ? 0xb01818 : 0x9b2020,
  };
}
for (const mode of [0, 1, 2]) {
  const c = lampColors(mode);
  console.log(`CRANE4_LAMP_MODE${mode}_LAMP=${c.lamp.toString(16)}`);
  console.log(`CRANE4_LAMP_MODE${mode}_EMISSIVE=${c.emissive.toString(16)}`);
  console.log(`CRANE4_LAMP_MODE${mode}_BUTTON=${c.button.toString(16)}`);
}

// ---------------------------------------------------------------------------
// CinemaRangeMath.NearestLaneAt — cinema.js:1744-1754 `_laneAt(x)`.
// ---------------------------------------------------------------------------
function nearestLaneAt(acrossOffset, railDotFwd, x, tol = 0.9) {
  let best = null;
  let bestD = tol;
  for (let i = 0; i < acrossOffset.length; i++) {
    if (railDotFwd[i] < 0.98) continue;
    const d = Math.abs(acrossOffset[i] - x);
    if (d < bestD) { bestD = d; best = i; }
  }
  return best;
}
// four lanes, one facing the wrong way (excluded), looking for x=3.3
const acrossOffset = [0.0, 1.67, 3.34, 3.3];
const railDotFwd = [1.0, 1.0, 1.0, 0.2];   // lane 3 runs the wrong way
console.log(`CRANE4_LANEAT_HIT=${nearestLaneAt(acrossOffset, railDotFwd, 3.3)}`);  // expect 2 (3.34 is closer than lane1's 1.67, and lane3 is excluded)
console.log(`CRANE4_LANEAT_MISS=${nearestLaneAt(acrossOffset, railDotFwd, 20.0)}`); // expect null -> printed as "null"

// ---------------------------------------------------------------------------
// CinemaRangeMath.TrolleyRunTo — cinema.js:1770-1777 `_runTo`'s pure half.
// ---------------------------------------------------------------------------
function trolleyRunTo(dist, range, min, max, atOverride) {
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const at = atOverride ?? clamp(dist - range, min, max);
  return { at, dist: range + at };
}
console.log(`CRANE4_RUNTO_CLAMPED=${f(trolleyRunTo(12.4, 2.0, 0, 8, null).dist)}`);      // 10.4 clamped to 8 -> range+8=10
console.log(`CRANE4_RUNTO_AT=${f(trolleyRunTo(12.4, 2.0, 0, 8, null).at)}`);
console.log(`CRANE4_RUNTO_OVERRIDE=${f(trolleyRunTo(12.4, 2.0, 0, 8, 1.5).dist)}`);      // 2.0+1.5=3.5

// ---------------------------------------------------------------------------
// CinemaRangeMath.AimPointOnCard — cinema.js:1783-1795 `_aimPoint(t)`.
// ---------------------------------------------------------------------------
function aimPointOnCard(centre, rightAxis, upAxis, cardW, cardH, cu, cv) {
  const norm = (v) => { const m = Math.hypot(v.x, v.y, v.z) || 1; return { x: v.x / m, y: v.y / m, z: v.z / m }; };
  let r = norm(rightAxis);
  let u = norm(upAxis);
  if (u.y < 0) u = { x: -u.x, y: -u.y, z: -u.z };
  return {
    x: centre.x + r.x * ((cu - 0.5) * cardW) + u.x * ((0.5 - cv) * cardH),
    y: centre.y + r.y * ((cu - 0.5) * cardW) + u.y * ((0.5 - cv) * cardH),
    z: centre.z + r.z * ((cu - 0.5) * cardW) + u.z * ((0.5 - cv) * cardH),
  };
}
// a card facing -Z (right=+X, up=+Y, but fed in UPSIDE DOWN -- up.y<0 -- to
// exercise the JS's own `if (uy.y<0) uy.negate()` flip), W=0.5,H=0.7,
// B27 X-ring at cu=0.52,cv=0.40 (papertargets.js's own real numbers)
const centre = { x: 10, y: 1.6, z: -5 };
const ap = aimPointOnCard(centre, { x: 1, y: 0, z: 0 }, { x: 0, y: -1, z: 0 }, 0.5, 0.7, 0.52, 0.40);
console.log(`CRANE4_AIMPOINT_X=${f(ap.x)}`);
console.log(`CRANE4_AIMPOINT_Y=${f(ap.y)}`);
console.log(`CRANE4_AIMPOINT_Z=${f(ap.z)}`);
// expect x = 10 + 1*(0.02*0.5) = 10.01; y = 1.6 + 1*(0.10*0.7) = 1.67 (up
// flipped back to +Y first); z unchanged = -5

console.log('CRANE4_PROBE_DONE=1');
