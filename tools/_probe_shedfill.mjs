// The JS side of the ShedFill port check (the scene-graph half of
// src/shed.js — unity/Assets/Scripts/Flight/ShedFill.cs).
//
// buildShed() itself cannot be run standalone in plain node (it loads a real
// GLB and cuts a real Mesh's index buffer — see tools/_probe_shedlayout.mjs's
// own docstring for the same limitation), so this script does two things
// that CAN be done without a loaded asset, at the REAL shed site (not the
// synthetic positions tools/_probe_shedlayout.mjs uses, which only exercises
// the pure math):
//
//   1. Re-derive the REAL warehouse/plane-hall site position from SHED_LL +
//      the REAL game-frame origin (GeoFrame.cs's Lat0/Lon0/MetresPerLat/
//      MetresPerLon, copied here — not exported from shed.js, since citymap's
//      meta comes from city.json at runtime).
//   2. Replicate LevelPad/Ramps at that real site with the same synthetic
//      height field ShedLayoutProbe.cs/_probe_shedlayout.mjs use, printing
//      PAD/RAMPS/DOOR lines in the SAME skeleton
//      unity/Assets/Editor/Probes/ShedFillProbe.cs prints after actually
//      building the real shed — meant to pair and diff via
//      tools/unity_port_check.py once that probe has run in the editor.
//
// The door-cut triangle counts, collider census and child-hierarchy lines
// ShedFillProbe.cs ALSO prints have no JS counterpart here on purpose — they
// need the real loaded mesh.
//
//   node tools/_probe_shedfill.mjs
import { SHED_LL, SHED_YAW } from '../src/shed.js';

const f = (v, n = 5) => Number(v).toFixed(n);

// tools/build_map.py ORIGIN, pinned the same way GeoFrame.cs's own comment
// insists on (full-precision series, not city.json's rounded meta.mlat/mlon).
const ORIGIN = { lat: 41.7252, lon: 44.7458, mlat: 111067.92066361613, mlon: 83206.33860686835 };

// src/shed.js:54-57 `p2` (no explicit pos — the lat/lon branch).
function siteFromLatLon(lat, lon, meta) {
  return { x: -(lon - meta.lon) * meta.mlon, z: (lat - meta.lat) * meta.mlat };
}

// src/planehall.js:85-99 `site` (module-private, replicated — see that
// file's own probe for why this is the honest thing to do).
function hallSite(meta, yaw) {
  const shed = siteFromLatLon(SHED_LL[0], SHED_LL[1], meta);
  const ux = Math.sin(yaw), uz = Math.cos(yaw);
  const vx = Math.cos(yaw), vz = -Math.sin(yaw);
  const OFF_X = 280, OFF_Z = -60;
  return { x: shed.x + vx * OFF_X + ux * OFF_Z, z: shed.z + vz * OFF_X + uz * OFF_Z };
}

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

// Same synthetic terrain as ShedFillProbe.cs's HeightAt — a gentle slope plus
// a sine ripple, deterministic and reproducible exactly in C#.
function heightAt(x, z) {
  return 100 + x * 0.01 + Math.sin(z * 0.003) * 3;
}

const DOOR0 = { x: 12.4, y: 9.15, z: 68.98 };
// The GLB's own documented footprint (src/shed.js:7-9 "ships at 16 x 46 x 5
// m") — ShedFillProbe.cs measures the REAL asset instead; these should be
// close but are not guaranteed bit-identical (extra geometry, decimals).
const UNSCALED = { x: 16.0, y: 5.0, z: 46.3 };

function runCase(id, scale, apron, rampLen) {
  const door = { x: (DOOR0.x * scale.x) / 3, y: (DOOR0.y * scale.y) / 3, z: (DOOR0.z * scale.z) / 3 };
  const size = { x: UNSCALED.x * scale.x, y: UNSCALED.y * scale.y, z: UNSCALED.z * scale.z };

  const meta = { lat: ORIGIN.lat, lon: ORIGIN.lon, mlat: ORIGIN.mlat, mlon: ORIGIN.mlon };
  const p2 = id === 'warehouse' ? siteFromLatLon(SHED_LL[0], SHED_LL[1], meta) : hallSite(meta, SHED_YAW);

  console.log(`SHELL id=${id} w=${f(size.x, 4)} l=${f(size.z, 4)} h=${f(size.y, 4)}`);
  console.log(`DOOR id=${id} x=${f(door.x, 6)} y=${f(door.y, 6)} z=${f(door.z, 6)}`);

  const halfW = size.x / 2 + apron, halfL = size.z / 2 + apron;
  const pad = levelPad(p2.x, p2.z, halfW, halfL, SHED_YAW, heightAt);
  console.log(`PAD id=${id} padTop=${f(pad.padTop)} padDepth=${f(pad.padDepth)}`);

  const rr = ramps(p2.x, p2.z, halfL, rampLen, SHED_YAW, pad.padTop, door.x, heightAt);
  const built = rr.filter((r) => r.needed).length;
  console.log(`RAMPS id=${id} built=${built}`);
}

runCase('warehouse', { x: 3, y: 3, z: 3 }, 7, 14);
runCase('hall', { x: 16, y: 7.5, z: 9.3 }, 15, 80);
