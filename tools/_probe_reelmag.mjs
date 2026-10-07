// THE MAGAZINE, INSIDE THE FILM (user: "check in video, it dont does it").
// Runs scene 2 and samples its reload at the corner: how far the cargador is
// out of the well, and how far the support hand is from it. Plus stills.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = `${process.env.OUT}/reelmag`;
fs.mkdirSync(OUT, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/profile`, {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });
const ok = await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  return g.cinema.start(1);
});
console.log('scene 2:', ok);
const stepTo = (t) => page.evaluate((t) => new Promise((done) => {
  const c = window.__game.cinema;
  const tick = () => (c.t >= t || !c.active ? done() : requestAnimationFrame(tick));
  tick();
}), t);
const read = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const gun = f.held?.obj;
  if (!gun) return { held: null };
  let mag = null; gun.traverse((o) => { if (!mag && o.isBone && /CARGADOR/i.test(o.name)) mag = o; });
  const home = f.held._magHome;
  mag?.updateWorldMatrix(true, false);
  let lh = null; f.rig.obj.traverse((o) => { if (!lh && o.isBone && /^hand_l$/i.test(o.name)) lh = o; });
  lh?.updateWorldMatrix(true, false);
  const mp = mag ? new T.Vector3().setFromMatrixPosition(mag.matrixWorld) : null;
  const hp = lh ? new T.Vector3().setFromMatrixPosition(lh.matrixWorld) : null;
  return {
    t: +g.cinema.t.toFixed(2), held: f.held?.id, rl: +(g.pistol?._reload ?? 0).toFixed(2),
    travel: mag && home ? +mag.position.distanceTo(home).toFixed(3) : null,
    bolt: f.held._boltBone && f.held._boltHome
      ? +f.held._boltBone.position.distanceTo(f.held._boltHome).toFixed(3) : null,
    magToHand: mp && hp ? +mp.distanceTo(hp).toFixed(2) : null,
    cover: !!f._cover,
  };
});
for (let t = 6.3; t <= 11.8; t += 0.3) {
  await stepTo(t);
  const r = await read();
  console.log(`t=${r.t} ${r.cover ? 'cover' : '     '} held=${r.held} rl=${r.rl} magOut=${r.travel} bolt=${r.bolt} magToHand=${r.magToHand}`);
}
// stills across the magazine change
for (const t of [6.9, 7.6, 8.3, 9.4, 10.3, 11.0]) {
  await page.evaluate(() => { window.__game.cinema._reset(); });
  await stepTo(t);
  await page.evaluate(() => { window.__game.cinema.paused = true; });
  await page.waitForTimeout(200);
  await page.screenshot({ path: `${OUT}/m${String(Math.round(t * 10)).padStart(3, '0')}.png` });
  await page.evaluate(() => { window.__game.cinema.paused = false; });
}
await ctx.close();
console.log('done');
