// Pairs with unity/Assets/Editor/Probes/FootAcrobaticsProbe.cs.
//
// The constants come out of the REAL src/foot.js by regex rather than being
// retyped here, so a drift between the JS and the C# shows up as a mismatch
// instead of agreeing with a copy of itself. Then it replicates _moveAim's
// clamp and _snapCamera's offset on the same inputs the C# probe uses.
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const num = (name) => {
  const m = src.match(new RegExp(`\\b${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`constant ${name} not found in src/foot.js`);
  return parseFloat(m[1]);
};
const AIM_YAW = num('AIM_YAW'), AIM_UP = num('AIM_UP'), AIM_DOWN = num('AIM_DOWN');
const AIM_ARM_YAW = num('AIM_ARM_YAW'), EYE = num('EYE');

const f = (x, n = 5) => x.toFixed(n);
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const norm = (v) => { const L = Math.hypot(...v); return [v[0] / L, v[1] / L, v[2] / L]; };

// Same identity body frame as the C# side: forward -Z, up +Y, side +X.
const FWD = [0, 0, -1], UP = [0, 1, 0], SIDE = [1, 0, 0];

function moveAim(a, twist) {
  const z = dot(FWD, a), x = dot(SIDE, a), y = dot(UP, a);
  const pitch = Math.asin(Math.max(-1, Math.min(1, y)));
  const yaw = Math.atan2(x, z);
  const cp = Math.max(AIM_DOWN, Math.min(AIM_UP, pitch));
  const maxYaw = Math.min(AIM_YAW, AIM_ARM_YAW + Math.abs(twist ?? 0));
  const cy = Math.max(-maxYaw, Math.min(maxYaw, yaw));
  if (cp === pitch && cy === yaw) return { v: a, unchanged: 1 };
  const c = Math.cos(cp);
  const out = [
    FWD[0] * c * Math.cos(cy) + SIDE[0] * c * Math.sin(cy) + UP[0] * Math.sin(cp),
    FWD[1] * c * Math.cos(cy) + SIDE[1] * c * Math.sin(cy) + UP[1] * Math.sin(cp),
    FWD[2] * c * Math.cos(cy) + SIDE[2] * c * Math.sin(cy) + UP[2] * Math.sin(cp),
  ];
  return { v: norm(out), unchanged: 0 };
}

for (const [id, aim, twist] of [
  ['inside', [0, 0, -1], null],
  ['slight_left', [-0.3, 0.1, -1], null],
  ['far_right', [1, 0, -0.2], null],
  ['far_left', [-1, 0, -0.2], null],
  ['far_right_twist', [1, 0, -0.2], 0.4],
  ['far_right_bigtwist', [1, 0, -0.2], 3.0],
  ['straight_up', [0, 1, 0], null],
  ['straight_down', [0, -1, 0], null],
]) {
  const a = norm(aim);
  const { v, unchanged } = moveAim(a, twist);
  const pitch = Math.asin(Math.max(-1, Math.min(1, dot(UP, v))));
  const yaw = Math.atan2(dot(SIDE, v), dot(FWD, v));
  console.log(`AIM id=${id} pitch=${f(pitch)} yaw=${f(yaw)} len=${f(Math.hypot(...v))} unchanged=${unchanged}`);
}

for (const yaw of [0, 0.7853982, 1.5707963, 3.1415927, -0.7853982]) {
  const pos = [10, 440, -20];
  const cam = [pos[0] + 6.2 * Math.sin(yaw), pos[1] + 2.6, pos[2] + 6.2 * Math.cos(yaw)];
  const look = [pos[0], pos[1] + EYE, pos[2]];
  console.log(`SNAP yaw=${f(yaw)} camX=${f(cam[0])} camY=${f(cam[1])} camZ=${f(cam[2])} ` +
              `lookX=${f(look[0])} lookY=${f(look[1])} lookZ=${f(look[2])} camYaw=${f(yaw)}`);
}
