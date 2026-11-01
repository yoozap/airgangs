// The JS side of the ShedLayout port check.
//
// SHED_LL/SHED_YAW are exported from src/shed.js and imported directly —
// real ground truth. buildShed() itself cannot be run standalone in plain
// node (it calls newGltfLoader().load('models/warehouse.glb', ...), which
// needs a real asset fetch and a THREE.Group scene graph this script does
// not have) — its ANALYTICAL pieces (the ground-pad levelling scan, the ramp
// shear parameters, the floor-tile UV scale) are therefore REPLICATED below,
// cited by line number, with a synthetic heightAt() standing in for
// citymap.heightAt. This mirrors the documented, already-established pattern
// for src/planehall.js's pack()/_walled in this same port.
//
//   node tools/_probe_shedlayout.mjs
import { SHED_LL, SHED_YAW } from '../src/shed.js';

const f = (v, n = 5) => Number(v).toFixed(n);

console.log(`CONST id=_const lat=${f(SHED_LL[0], 6)} lon=${f(SHED_LL[1], 6)} yaw=${f(SHED_YAW, 6)}`);

// ---- LevelPad: REPLICATED (src/shed.js:163-196) ----------------------------
function levelPad(px, pz, halfW, halfL, yaw, heightAt) {
  const ux = Math.sin(yaw), uz = Math.cos(yaw);
  const vx = Math.cos(yaw), vz = -Math.sin(yaw);
  const SAMPLE = 4;
  const nA = Math.max(8, Math.ceil((halfW * 2) / SAMPLE));
  const nB = Math.max(8, Math.ceil((halfL * 2) / SAMPLE));
  let hi = -Infinity, lo = Infinity;
  for (let i = 0; i <= nA; i++) {
    for (let j = 0; j <= nB; j++) {
      const a = -1 + (2 * i) / nA;
      const b = -1 + (2 * j) / nB;
      const sx = px + vx * halfW * a + ux * halfL * b;
      const sz = pz + vz * halfW * a + uz * halfL * b;
      const gy = heightAt(sx, sz);
      if (gy == null) continue;
      if (gy > hi) hi = gy;
      if (gy < lo) lo = gy;
    }
  }
  if (!Number.isFinite(hi)) hi = heightAt(px, pz) ?? 0;
  if (!Number.isFinite(lo)) lo = hi;
  const padTop = hi + 0.45;
  const padDepth = Math.max(1.5, padTop - lo + 1.0);
  return { padTop, padDepth, hi, lo };
}

// ---- Ramps: REPLICATED (src/shed.js:209-232) -------------------------------
function ramps(px, pz, halfL, rampLen, yaw, padTop, doorHalfW, heightAt) {
  const ux = Math.sin(yaw), uz = Math.cos(yaw);
  const rampHalf = doorHalfW * 0.98;
  const out = [];
  for (const sgn of [1, -1]) {
    const zNear = sgn * halfL;
    const zFar = sgn * (halfL + rampLen);
    const gy = heightAt(px + ux * zFar, pz + uz * zFar);
    if (gy == null) { out.push({ needed: false }); continue; }
    const dy = gy + 0.05 - padTop;
    if (dy > -0.35) { out.push({ needed: false }); continue; }
    const depth = Math.max(3, -dy + 1.5);
    const k = dy / (zFar - zNear);
    out.push({ needed: true, depth, k, zNear, zFar, rampHalf });
  }
  return out;
}

// A synthetic terrain: a gentle slope plus a sine ripple, deterministic and
// reproducible exactly in C# with the same formula.
function heightAt(x, z) {
  return 100 + x * 0.01 + Math.sin(z * 0.003) * 3;
}

// ---- warehouse-scale case (BASE=3, the player's own shed) ------------------
{
  const scale = { x: 3, y: 3, z: 3 };
  const DOOR0 = { x: 12.4, y: 9.15, z: 68.98 };
  const door = { x: (DOOR0.x * scale.x) / 3, y: (DOOR0.y * scale.y) / 3, z: (DOOR0.z * scale.z) / 3 };
  // the GLB's own unscaled footprint (measured once off the asset, stood in
  // here as a fixed constant the way the real buildShed reads box.getSize())
  const unscaled = { x: 16.0, y: 5.0, z: 46.3 };
  const size = { x: unscaled.x * scale.x, y: unscaled.y * scale.y, z: unscaled.z * scale.z };
  const px = 1000, pz = -500;
  const halfW = size.x / 2 + 7, halfL = size.z / 2 + 7;
  const pad = levelPad(px, pz, halfW, halfL, SHED_YAW, heightAt);
  console.log(`PAD id=warehouse padTop=${f(pad.padTop)} padDepth=${f(pad.padDepth)} hi=${f(pad.hi)} lo=${f(pad.lo)}`);
  const rr = ramps(px, pz, halfL, 14, SHED_YAW, pad.padTop, door.x, heightAt);
  rr.forEach((r, i) => {
    if (!r.needed) { console.log(`RAMP id=warehouse${i} needed=0`); return; }
    console.log(`RAMP id=warehouse${i} needed=1 depth=${f(r.depth)} k=${f(r.k, 6)} zNear=${f(r.zNear)} zFar=${f(r.zFar)} rampHalf=${f(r.rampHalf)}`);
  });
  const uv = { rx: size.x / 12, rz: size.z / 12 };
  console.log(`UV id=warehouse rx=${f(uv.rx)} rz=${f(uv.rz)}`);
}

// ---- plane-hall-scale case (far wider/longer stretch) ----------------------
{
  const scale = { x: 16, y: 7.5, z: 9.3 };
  const DOOR0 = { x: 12.4, y: 9.15, z: 68.98 };
  const door = { x: (DOOR0.x * scale.x) / 3, y: (DOOR0.y * scale.y) / 3, z: (DOOR0.z * scale.z) / 3 };
  const unscaled = { x: 16.0, y: 5.0, z: 46.3 };
  const size = { x: unscaled.x * scale.x, y: unscaled.y * scale.y, z: unscaled.z * scale.z };
  const px = -18371, pz = -7059;
  const halfW = size.x / 2 + 15, halfL = size.z / 2 + 15;
  const pad = levelPad(px, pz, halfW, halfL, SHED_YAW, heightAt);
  console.log(`PAD id=hall padTop=${f(pad.padTop)} padDepth=${f(pad.padDepth)} hi=${f(pad.hi)} lo=${f(pad.lo)}`);
  const rr = ramps(px, pz, halfL, 80, SHED_YAW, pad.padTop, door.x, heightAt);
  rr.forEach((r, i) => {
    if (!r.needed) { console.log(`RAMP id=hall${i} needed=0`); return; }
    console.log(`RAMP id=hall${i} needed=1 depth=${f(r.depth)} k=${f(r.k, 6)} zNear=${f(r.zNear)} zFar=${f(r.zFar)} rampHalf=${f(r.rampHalf)}`);
  });
  const uv = { rx: size.x / 12, rz: size.z / 12 };
  console.log(`UV id=hall rx=${f(uv.rx)} rz=${f(uv.rz)}`);
}
