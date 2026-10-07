// Footsteps in game: run, walk, sprint, crouch-walk and stroll on the apron,
// then run on grass, then a jump -- counting the steps the feet fire, their
// rate, their alternation, the ground they were classified on, and how high
// the feet actually lift (the contact thresholds' sanity check).
//   node tools/_probe_steps.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/steps';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 900, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000);

const r = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj); g.foot._modelId = want; await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at); g.scene.add(g.foot.rig.obj); g.foot.pos.copy(at);
  }
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  const home = () => { g.foot.pos.set(tp.x + 6, tp.y + 2, tp.z + 12); g.foot.yaw = Math.PI / 2; g.foot.camYaw = Math.PI / 2; g.foot._snapCamera?.(); g.foot.speed = 0; g.foot.vy = 0; };
  g.renderer.setAnimationLoop(null);
  const fs = g.footsteps;
  const feet = g.foot.rig._legPair?.();
  const step = (n, keys = []) => {
    const hs = [];
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) {
      g.foot.update(1 / 60); g.pistol.update(1 / 60); fs.update(1 / 60, g.foot);
      if (feet) { feet.L.updateWorldMatrix(true, false); hs.push(new THREE.Vector3().setFromMatrixPosition(feet.L.matrixWorld).y - g.foot.pos.y); }
    }
    for (const k of keys) g.keys.delete(k);
    return hs;
  };
  const phase = (name, keys, n = 180) => {
    fs.log = [];
    const hs = step(n, keys);
    const log = fs.log;
    let alt = 0; for (let i = 1; i < log.length; i++) if (log[i].side !== log[i - 1].side) alt++;
    const surfaces = {}; for (const e of log) surfaces[e.surface] = (surfaces[e.surface] || 0) + 1;
    const gaits = {}; for (const e of log) gaits[e.gait] = (gaits[e.gait] || 0) + 1;
    return { name, steps: log.length, perSec: +(log.length / (n / 60)).toFixed(2), alternating: log.length > 1 ? +(alt / (log.length - 1)).toFixed(2) : null,
      speed: +Math.abs(g.foot.speed).toFixed(1), surfaces, gaits,
      footH: hs.length ? { min: +Math.min(...hs).toFixed(3), max: +Math.max(...hs).toFixed(3) } : null };
  };
  const out = [];
  home(); step(30);
  out.push(phase('run', ['KeyW']));
  home(); step(30);
  out.push(phase('walk', ['KeyW', 'ControlLeft']));
  home(); step(30);
  out.push(phase('sprint', ['KeyW', 'ShiftLeft']));
  home(); step(60); g.foot.toggleCrouch(); step(20);
  out.push(phase('crouch', ['KeyW']));
  g.foot.toggleCrouch(); home(); step(60);
  // grass: walk out from the apron until the ground says so
  let found = null;
  for (let dz = 6; dz < 80 && !found; dz += 2) {
    for (const dx of [-30, -15, 0, 15, 30]) {
      g.foot.pos.set(tp.x + dx, tp.y + 2, tp.z + 12 + dz);
      const gy = g._groundAt(g.foot.pos.x, g.foot.pos.z, g.foot.pos.y + 2, 4);
      if (gy === null) continue;
      g.foot.pos.y = gy;
      if (fs._surfaceAt(g.foot) === 'grass') { found = [dx, dz]; break; }
    }
  }
  if (found) { g.foot.yaw = 0; g.foot.camYaw = 0; g.foot._snapCamera?.(); step(60); out.push(phase('run_grass', ['KeyW'])); }
  else out.push({ name: 'run_grass', skipped: 'no grass within 80 m' });
  // a jump, and the landing
  home(); step(60);
  fs.log = [];
  const g0 = { grounded: g.foot.grounded, y: +g.foot.pos.y.toFixed(2) };
  step(5, ['KeyW']); step(3, ['KeyW', 'Space']);
  let vyMax = -Infinity, airFrames = 0;
  for (let i = 0; i < 100; i++) { g.keys.add('KeyW'); g.foot.update(1 / 60); g.pistol.update(1 / 60); fs.update(1 / 60, g.foot); vyMax = Math.max(vyMax, g.foot.vy); if (!g.foot.grounded) airFrames++; }
  g.keys.delete('KeyW');
  out.push({ name: 'jump', before: g0, vyMax: +vyMax.toFixed(2), airFrames, events: fs.log.filter((e) => e.gait === 'land').length, drop: fs.log.find((e) => e.gait === 'land')?.drop ?? null, stepsAfter: fs.log.filter((e) => e.gait !== 'land').length });
  return out;
});
for (const row of r) console.log(JSON.stringify(row));
await ctx.close();
