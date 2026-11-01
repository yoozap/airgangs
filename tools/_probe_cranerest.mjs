// The JS side of the "rest of crane.js / cinema.js" port check — the units
// this pass added on top of tools/_probe_crane.mjs / tools/_probe_cinema.mjs:
//   Props/WindFlowMesh.cs    (crane.js:1003-1066, the moving-air sheets)
//   Props/PlaneSling.cs      (crane.js:404-421, 442-460, 467-468)
//   Props/CraneHookFinder.cs (crane.js:169-217)
//   Props/CableRoutes.cs     (crane.js:630-655, added inside WarehouseCrane.cs)
//   Props/WindRider.cs       (OnWing(), the "is he on it" test split out pure)
//   Cinema/CinemaLights.cs   (cinema.js:1446-1478)
//
// Same documented pattern as _probe_crane.mjs: src/crane.js/src/cinema.js
// export nothing usable headless for any of these (a loaded GLTF/DOM/
// citymap/CharacterRig in every case), so every number below is a cited
// REPLICATION of the relevant closure, run against the SAME synthetic
// inputs CraneRestProbe.cs feeds the real ported C#.
//
//   node tools/_probe_cranerest.mjs
import * as THREE from 'three';

const f = (v, n = 4) => Number(v).toFixed(n);

// =============================================================================
// ---- WindFlowMesh — REPLICATED crane.js:1003-1066 --------------------------
// =============================================================================
const box = { minX: -5.5, maxX: 5.5, minY: -0.3, maxY: 0.5, minZ: -4.5, maxZ: 4.5 };
const SHEETS = 7, SHEET_X = 26, SHEET_Z = 30;

function geoFrom(b) {
  const span = b.maxX - b.minX, len = b.maxZ - b.minZ, hgt = b.maxY - b.minY;
  const yc = (b.minY + b.maxY) / 2;
  return {
    span, len, hgt, fw: span * 0.055, fh: hgt * 0.30,
    yc, yw: yc - hgt * 0.10, zc: (b.minZ + b.maxZ) / 2,
  };
}
function blockage(geo, x) {
  const a = Math.abs(x);
  if (a < geo.fw * 1.9) return { y: geo.yc, h: geo.fh * (1 - (a / (geo.fw * 1.9)) * 0.35) };
  if (a < geo.span * 0.5) return { y: geo.yw, h: geo.hgt * 0.05 * (1 - a / (geo.span * 0.5)) + geo.hgt * 0.012 };
  return { y: geo.yw, h: 0 };
}
function streamExtent(b) {
  const len = b.maxZ - b.minZ;
  return { z0: b.minZ - len * 0.8, z1: b.maxZ + len * 1.1 };
}
function sheetVertex(b, k, z0, z1, i, j) {
  const geo = geoFrom(b);
  const lift = (k / (SHEETS - 1) - 0.5) * geo.hgt * 1.75;
  const tz = j / SHEET_Z;
  const z = z0 + (z1 - z0) * tz;
  const u = (z - geo.zc) / (geo.len * 0.42);
  const bump = Math.exp(-u * u);
  const wake = z > geo.zc ? (1 - Math.exp(-(z - geo.zc) / (geo.len * 0.5))) : 0;
  const tx = i / SHEET_X;
  const x = (tx - 0.5) * geo.span * 1.34;
  const bl = blockage(geo, x);
  const sy = geo.yc + lift;
  const over = sy >= bl.y ? 1 : -1;
  const miss = Math.abs(sy - bl.y);
  const clear = bl.h + geo.hgt * 0.13;
  const push = bl.h <= 0 ? 0
    : Math.max(0, clear - miss) * (0.9 + 0.5 * Math.exp(-miss / (geo.hgt * 0.4)));
  const pos = [x, sy + over * push * bump - over * push * wake * 0.35, z];
  const uv = [tz * 3.1 + k * 0.37, tx * 0.9 + k * 0.21];
  const edge = Math.sin(Math.PI * tx) * Math.min(1, Math.sin(Math.PI * tz) * 2.4);
  const squeeze = 0.45 + 1.5 * (push / (geo.hgt * 0.5)) * bump;
  const c = Math.max(0, edge) * Math.min(1.8, squeeze);
  return { pos, uv, c };
}

const ext = streamExtent(box);
console.log(`CRANEREST_FLOW_Z0=${f(ext.z0, 3)}`);
console.log(`CRANEREST_FLOW_Z1=${f(ext.z1, 3)}`);

for (const [tag, k, i, j] of [['K0_I13_J0', 0, 13, 0], ['K3_I20_J25', 3, 20, 25]]) {
  const v = sheetVertex(box, k, ext.z0, ext.z1, i, j);
  console.log(`CRANEREST_FLOW_${tag}_X=${f(v.pos[0])}`);
  console.log(`CRANEREST_FLOW_${tag}_Y=${f(v.pos[1])}`);
  console.log(`CRANEREST_FLOW_${tag}_Z=${f(v.pos[2])}`);
  console.log(`CRANEREST_FLOW_${tag}_U=${f(v.uv[0])}`);
  console.log(`CRANEREST_FLOW_${tag}_V=${f(v.uv[1])}`);
  console.log(`CRANEREST_FLOW_${tag}_C=${f(v.c)}`);
}

// the shared quad-grid index buffer — crane.js:1052-1058
{
  const w = SHEET_X + 1;
  const idx = [];
  for (let j = 0; j < SHEET_Z; j++) {
    for (let i = 0; i < SHEET_X; i++) {
      const a = j * w + i;
      idx.push(a, a + 1, a + w, a + 1, a + w + 1, a + w);
    }
  }
  console.log(`CRANEREST_FLOW_INDEX_COUNT=${idx.length}`);
  console.log(`CRANEREST_FLOW_INDEX_FIRST6=${idx.slice(0, 6).join(',')}`);
}

// =============================================================================
// ---- PlaneSling — REPLICATED crane.js:404-421, 442-460, 467-468 -----------
// =============================================================================
{
  const span = 11, len = 9, maxY = 0.5;
  const falloff = [1, 0.8, 0.6, 0.4, 0.2, 0];
  const underRay = () => null; // always misses -> forces the hard-coded fallback, crane.js:417
  const feet = [];
  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) {
      const x = sx * span * 0.17;
      let foot = null;
      for (const ff of falloff) { foot = underRay(x, sz * len * 0.13 * ff); if (foot) break; }
      const p = foot ?? new THREE.Vector3(x, maxY - 0.55, sz * len * 0.13);
      p.y -= 0.03;
      feet.push(p);
    }
  }
  feet.forEach((p, i) => {
    console.log(`CRANEREST_SLING_FOOT${i}_X=${f(p.x)}`);
    console.log(`CRANEREST_SLING_FOOT${i}_Y=${f(p.y)}`);
    console.log(`CRANEREST_SLING_FOOT${i}_Z=${f(p.z)}`);
  });

  // AimRopes — crane.js:442-460. Pure R3xR3->transform math, no cross
  // product (setFromUnitVectors is handedness-invariant), so this is tested
  // directly, no frame-mirroring question applies.
  const block = new THREE.Vector3(1, 2, 3);
  const foot = block.clone().add(new THREE.Vector3(2, 3, 6)); // |d| = 7
  const d = foot.clone().sub(block);
  const l = d.length();
  const dir = d.clone().divideScalar(l);
  const q = new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir);
  const p = block.clone().addScaledVector(d, 0.5);
  const upRotated = new THREE.Vector3(0, 1, 0).applyQuaternion(q);
  console.log(`CRANEREST_SLING_ROPE_LENGTH=${f(l)}`);
  console.log(`CRANEREST_SLING_ROPE_MID_X=${f(p.x)}`);
  console.log(`CRANEREST_SLING_ROPE_MID_Y=${f(p.y)}`);
  console.log(`CRANEREST_SLING_ROPE_MID_Z=${f(p.z)}`);
  console.log(`CRANEREST_SLING_ROPE_UPROT_X=${f(upRotated.x)}`);
  console.log(`CRANEREST_SLING_ROPE_UPROT_Y=${f(upRotated.y)}`);
  console.log(`CRANEREST_SLING_ROPE_UPROT_Z=${f(upRotated.z)}`);
  console.log(`CRANEREST_SLING_ROPE_UPROT_MATCHES_DIR=${upRotated.distanceTo(dir) < 1e-4}`);

  // MidAirframe — crane.js:467-468
  const hookAt = new THREE.Vector3(1, 2, 3);
  const planeLocalY = -1.5;
  const mid = new THREE.Vector3(hookAt.x, hookAt.y + planeLocalY + (box.minY + box.maxY) / 2, hookAt.z);
  console.log(`CRANEREST_SLING_MID_X=${f(mid.x)}`);
  console.log(`CRANEREST_SLING_MID_Y=${f(mid.y)}`);
  console.log(`CRANEREST_SLING_MID_Z=${f(mid.z)}`);
}

// =============================================================================
// ---- CraneHookFinder — REPLICATED crane.js:169-217 -------------------------
// =============================================================================
{
  const boxMin = new THREE.Vector3(-2, 0, -2);
  const boxMax = new THREE.Vector3(2, 3, 2);
  const H = boxMax.y - boxMin.y;
  const outerZ = boxMin.z + (boxMax.z - boxMin.z) * 0.78;
  const footBand = H * 0.035;

  const verts = [
    new THREE.Vector3(-1.8, 0, -1.9),
    new THREE.Vector3(1.8, 0, -1.5),
    new THREE.Vector3(0, 0.05, -1.7),
    new THREE.Vector3(0.2, 1.0, 1.5),
    new THREE.Vector3(0.0, 1.05, 1.8),
    new THREE.Vector3(-0.1, 1.4, 1.6),
  ];

  function compute(toldHook) {
    let feetAny = false, feetMinX = 0, feetMaxX = 0, feetMinZ = 0, feetMaxZ = 0;
    const hookPts = [];
    let hookY = Infinity;
    for (const v of verts) {
      if (v.y - boxMin.y < footBand) {
        if (!feetAny) { feetMinX = feetMaxX = v.x; feetMinZ = feetMaxZ = v.z; feetAny = true; }
        else {
          if (v.x < feetMinX) feetMinX = v.x; else if (v.x > feetMaxX) feetMaxX = v.x;
          if (v.z < feetMinZ) feetMinZ = v.z; else if (v.z > feetMaxZ) feetMaxZ = v.z;
        }
      }
      if (!toldHook && v.z > outerZ) { hookPts.push(v); if (v.y < hookY) hookY = v.y; }
    }
    const feetCenter = feetAny ? new THREE.Vector3((feetMinX + feetMaxX) / 2, 0, (feetMinZ + feetMaxZ) / 2) : new THREE.Vector3();
    if (toldHook) return { feetCenter, hook: toldHook.clone() };
    const band = hookY + H * 0.02;
    let sx = 0, sz = 0, n = 0;
    for (const p of hookPts) { if (p.y > band) continue; sx += p.x; sz += p.z; n++; }
    if (n > 0) { sx /= n; sz /= n; }
    return { feetCenter, hook: new THREE.Vector3(sx, hookY, sz) };
  }

  const geom = compute(null);
  console.log(`CRANEREST_HOOK_FEET_X=${f(geom.feetCenter.x)}`);
  console.log(`CRANEREST_HOOK_FEET_Z=${f(geom.feetCenter.z)}`);
  console.log(`CRANEREST_HOOK_GEOM_X=${f(geom.hook.x)}`);
  console.log(`CRANEREST_HOOK_GEOM_Y=${f(geom.hook.y)}`);
  console.log(`CRANEREST_HOOK_GEOM_Z=${f(geom.hook.z)}`);

  const told = compute(new THREE.Vector3(5, 6, 7));
  console.log(`CRANEREST_HOOK_TOLD_X=${f(told.hook.x)}`);
  console.log(`CRANEREST_HOOK_TOLD_Y=${f(told.hook.y)}`);
  console.log(`CRANEREST_HOOK_TOLD_Z=${f(told.hook.z)}`);
}

// =============================================================================
// ---- CableRoutes — REPLICATED crane.js:630-655 (perp via the SAME RightOf
// substitution WarehouseCrane.cs's FanLayout already established, not the
// JS's literal (-away.z,0,away.x) — see WarehouseCrane.cs's CableRoutes
// header for why that substitution is the correct one here) ----------------
// =============================================================================
{
  const BOARD = { x: 0.50, y: 0.38, z: 0.40 };
  const LEAD_R = 0.022, TRUNK_R = 0.034, SOCKET_Y = 1.15;
  const rightOf = (v) => new THREE.Vector3(v.z, 0, -v.x); // WarehouseCrane.RightOf

  const board = new THREE.Vector3(13.0, 0, 30.0);
  const plug = new THREE.Vector3(10, 0.5, 27.5);
  const index = 0, count = 3;

  const inlet = new THREE.Vector3(
    board.x - BOARD.x / 2 - 0.04,
    BOARD.y * 0.45,
    board.z + (index - (count - 1) / 2) * 0.13);
  const away = new THREE.Vector3(plug.x - board.x, 0, plug.z - board.z).normalize();
  const perp = rightOf(away);
  const slack = plug.clone().lerp(inlet, 0.5).addScaledVector(perp, 0.55);
  slack.y = LEAD_R;
  const outLead = plug.clone().addScaledVector(away, 0.35); outLead.y = LEAD_R;
  const inLead = inlet.clone().addScaledVector(away, 0.9); inLead.y = LEAD_R;
  const lead = [plug, outLead, slack, inLead, inlet];
  lead.forEach((p, i) => {
    console.log(`CRANEREST_CABLE_LEAD${i}_X=${f(p.x)}`);
    console.log(`CRANEREST_CABLE_LEAD${i}_Y=${f(p.y)}`);
    console.log(`CRANEREST_CABLE_LEAD${i}_Z=${f(p.z)}`);
  });

  const wallAt = 23.3;
  const trunk = [
    new THREE.Vector3(board.x + BOARD.x / 2 + 0.02, BOARD.y * 0.5, board.z),
    new THREE.Vector3(board.x + 0.9, TRUNK_R, board.z + 0.25),
    new THREE.Vector3((board.x + wallAt) / 2, TRUNK_R, board.z + 0.5),
    new THREE.Vector3(wallAt - 0.22, TRUNK_R, board.z),
    new THREE.Vector3(wallAt - 0.09, 0.30, board.z),
    new THREE.Vector3(wallAt - 0.07, SOCKET_Y - 0.17, board.z),
  ];
  trunk.forEach((p, i) => {
    console.log(`CRANEREST_CABLE_TRUNK${i}_X=${f(p.x)}`);
    console.log(`CRANEREST_CABLE_TRUNK${i}_Y=${f(p.y)}`);
    console.log(`CRANEREST_CABLE_TRUNK${i}_Z=${f(p.z)}`);
  });
}

// =============================================================================
// ---- WindRider.OnWing — REPLICATED crane.js:1471-1476 ----------------------
// =============================================================================
{
  const geo = geoFrom(box);
  // crane.js:1471-1476 `onIt` — SkinAt/SurfaceAt never hit in this test (no
  // live skin to probe), so `top` falls through to the analytic blockage
  // model exactly as it does the first frame the wind ever takes the hero.
  function onWing(x, y, z) {
    const bl = blockage(geo, x);
    const top = bl.y + bl.h;
    const onIt = Math.abs(x) < geo.span * 0.55
      && z > box.minZ - 0.6 && z < box.maxZ + 0.6
      && y > top - 0.9 && y < top + 1.8;
    return { top, onIt };
  }
  const onWingPt = onWing(2, 0.1, 0);
  console.log(`CRANEREST_ONWING_ON_SURFACEY=${f(onWingPt.top)}`);
  console.log(`CRANEREST_ONWING_ON_RESULT=${onWingPt.onIt}`);
  const offWingPt = onWing(10, 0.1, 0);
  console.log(`CRANEREST_ONWING_OFF_SURFACEY=${f(offWingPt.top)}`);
  console.log(`CRANEREST_ONWING_OFF_RESULT=${offWingPt.onIt}`);
}

// =============================================================================
// ---- CinemaLights — REPLICATED cinema.js:1446-1478 -------------------------
// =============================================================================
{
  const hero = new THREE.Vector3(10, 0, 20);
  const right = new THREE.Vector3(1, 0, 0);
  const fwd = new THREE.Vector3(0, 0, 1);
  const key = hero.clone().addScaledVector(new THREE.Vector3(0, 1, 0), 3.4)
    .addScaledVector(right, 2.6).addScaledVector(fwd, 2.2);
  const keyTarget = hero.clone().addScaledVector(new THREE.Vector3(0, 1, 0), 1.1);
  const rim = hero.clone().addScaledVector(new THREE.Vector3(0, 1, 0), 2.9)
    .addScaledVector(right, -2.2).addScaledVector(fwd, -3.0);
  const rimTarget = hero.clone().addScaledVector(new THREE.Vector3(0, 1, 0), 1.2);
  console.log(`CRANEREST_CINEMA_KEY_X=${f(key.x)}`);
  console.log(`CRANEREST_CINEMA_KEY_Y=${f(key.y)}`);
  console.log(`CRANEREST_CINEMA_KEY_Z=${f(key.z)}`);
  console.log(`CRANEREST_CINEMA_KEYTARGET_Y=${f(keyTarget.y)}`);
  console.log(`CRANEREST_CINEMA_RIM_X=${f(rim.x)}`);
  console.log(`CRANEREST_CINEMA_RIM_Y=${f(rim.y)}`);
  console.log(`CRANEREST_CINEMA_RIM_Z=${f(rim.z)}`);
  console.log(`CRANEREST_CINEMA_RIMTARGET_Y=${f(rimTarget.y)}`);

  function flashIntensity(on, timer) {
    const isOn = on && timer > 0;
    if (!isOn) return 0;
    return 55 * Math.min(1, Math.max(0, timer / 0.045));
  }
  console.log(`CRANEREST_CINEMA_FLASH_OFF=${f(flashIntensity(false, 0.03))}`);
  console.log(`CRANEREST_CINEMA_FLASH_ON_FULL=${f(flashIntensity(true, 0.045))}`);
  console.log(`CRANEREST_CINEMA_FLASH_ON_HALF=${f(flashIntensity(true, 0.0225))}`);
}

console.log('CRANEREST_PROBE_DONE=1');
