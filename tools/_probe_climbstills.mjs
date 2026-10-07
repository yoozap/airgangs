// LOOK AT IT, from the reel's own camera. Steps scene 2 frame by frame and
// grabs a still at each listed cinema time -- the top of the climb and the
// draw that follows -- so the rifle's place on the back can be SEEN rather
// than inferred from a parent chain.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const AT = (process.env.AT ?? '0.80,0.90,0.95,0.98,1.00,1.03,1.10,1.25,1.50,1.60,1.70,1.85')
  .split(',').map(Number);
const ctx = await chromium.launchPersistentContext(OUT + '/stills-profile', {
  headless: true, viewport: { width: 1280, height: 720 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 300000 });
await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  await g.cinema.start(1);
  g.renderer.setAnimationLoop(null);
  window.__tick = () => {
    const dt = 1 / 60;
    g.cinema.update(dt);
    for (const ga of Object.values(g.gunAnims)) ga.update(dt * g.cinema.timeScale);
    g.foot.update(dt * g.cinema.timeScale);
    g.pistol.update(dt * g.cinema.timeScale);
    g.cinema.postUpdate(dt);
    g.renderer.render(g.scene, g.camera);   // the loop is off: draw it by hand
    return +g.cinema.t.toFixed(3);
  };
});
let next = 0;
for (let i = 0; i < 400 && next < AT.length; i++) {
  const t = await page.evaluate(() => window.__tick());
  if (t >= AT[next]) {
    const name = `${OUT}/climb_${String(AT[next]).replace('.', 'p')}.png`;
    await page.screenshot({ path: name });
    console.log(`t=${t} -> ${name}`);
    next++;
  }
}
await ctx.close();
