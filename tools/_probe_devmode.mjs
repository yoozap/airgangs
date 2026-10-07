// Dev-mode gizmo check: open it, pick the crate, drag the gizmo with a real
// pointer drag, confirm the crate moved, the camera did not orbit, the
// colliders were re-indexed without duplicates, the spot was saved, and a
// reload puts the crate back where it was dropped. Run:
//   GAME_URL=http://localhost:5173/ node tools/_probe_devmode.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/07cfdb5d-9f4f-4adf-9f28-2ddf96613e4d/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/ammo-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/dev:|ammo|failed/i.test(m.text())) console.log('  [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
const waitCrate = () => page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box') f = o; }); return !!f;
}, null, { timeout: 240000 });

// (fresh profile: no overrides yet; reset at the end clears what this run saves)
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await waitCrate();
// stand 9 m from the crate, looking at it, on foot
await page.evaluate(async () => {
  const T = window.__THREE; const g = window.__game;
  await g.foot.ready; if (!g.foot.active) g.foot.toggle();
  let crate = null, wh = null;
  g.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; if (o.name === 'warehouse') wh = o; });
  const cp = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const axis = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  const side = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion);
  const from = cp.clone().addScaledVector(axis, 9).addScaledVector(side, -4); from.y = cp.y + 0.05;
  g.foot.pos.copy(from);
  const d = cp.clone().sub(from); g.foot.yaw = Math.atan2(-d.x, -d.z); g.foot.camYaw = g.foot.yaw;
  g.look.yaw = 0; g.look.pitch = 0.15;
});
await page.waitForTimeout(2000);
await page.keyboard.press('Backquote');
await page.waitForTimeout(300);
const opened = await page.evaluate(() => ({ active: window.__game.dev.active, items: window.__game.dev.items.map((i) => i.id) }));
console.log('opened:', JSON.stringify(opened));
// click on the crate to select it
const scr = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; });
  const p = new T.Vector3().setFromMatrixPosition(crate.matrixWorld); p.y += 1.0;
  p.project(g.camera);
  const r = g.renderer.domElement.getBoundingClientRect();
  return { x: r.left + (p.x + 1) / 2 * r.width, y: r.top + (1 - p.y) / 2 * r.height };
});
await page.mouse.click(scr.x, scr.y);
await page.waitForTimeout(400);
const sel = await page.evaluate(() => ({ selected: window.__game.dev.selected?.id ?? null, mode: window.__game.dev.ctl?.mode }));
console.log('after click:', JSON.stringify(sel));
await page.screenshot({ path: OUT + '/dev_selected.png' });

// drag the gizmo's centre handle (XYZ, screen-plane move) 140 px to the right
const giz = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  const p = new T.Vector3().setFromMatrixPosition(g.dev.selected.obj.matrixWorld).project(g.camera);
  const r = g.renderer.domElement.getBoundingClientRect();
  return { x: r.left + (p.x + 1) / 2 * r.width, y: r.top + (1 - p.y) / 2 * r.height };
});
const before = await page.evaluate(() => ({ p: window.__game.dev.selected.obj.position.toArray(), yaw: window.__game.look.yaw }));
await page.mouse.move(giz.x, giz.y); await page.waitForTimeout(150);
const hot = await page.evaluate(() => ({ axis: window.__game.dev.ctl.axis, hot: window.__game.dev.hot }));
console.log('hover gizmo centre:', JSON.stringify(hot));
await page.mouse.down();
for (let i = 1; i <= 14; i++) { await page.mouse.move(giz.x + i * 10, giz.y, { steps: 2 }); await page.waitForTimeout(16); }
await page.mouse.up();
await page.waitForTimeout(400);
const after = await page.evaluate(() => {
  const g = window.__game; const it = g.dev.selected;
  let n = 0; it.obj.traverse((m) => { if (m.isMesh) n += g.world.collidables.filter((c) => c === m).length; });
  return { p: it.obj.position.toArray(), yaw: g.look.yaw, saved: JSON.parse(localStorage.getItem('tf-dev-overrides') || '{}'), colliderRefs: n,
    snippet: g.dev.snippet(it) };
});
console.log('before:', JSON.stringify(before));
console.log('after :', JSON.stringify({ p: after.p, yaw: after.yaw, colliderRefs: after.colliderRefs }));
console.log('saved :', JSON.stringify(after.saved));
console.log(after.snippet);
await page.screenshot({ path: OUT + '/dev_moved.png' });

// a fresh entry into the world: the crate must come back where it was dropped
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await waitCrate();
await page.waitForTimeout(500);
const back = await page.evaluate(() => {
  let crate = null; window.__game.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; });
  return { p: crate.position.toArray().map((n) => +n.toFixed(2)), saved: window.__game.dev.items.find((i) => i.id === 'ammo_box')?.saved };
});
console.log('after re-entry:', JSON.stringify(back));
await page.evaluate(() => { window.__game.dev.open(); window.__game.dev.select('ammo_box'); window.__game.dev.reset(); });
const reset = await page.evaluate(() => {
  let crate = null; window.__game.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; });
  return { p: crate.position.toArray().map((n) => +n.toFixed(2)), saved: localStorage.getItem('tf-dev-overrides') };
});
console.log('after reset:', JSON.stringify(reset));
await ctx.close();
console.log('done');
