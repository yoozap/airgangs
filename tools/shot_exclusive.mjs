// Verify the exclusivity wave: new kit variants (win_brick, balc_juliet,
// balc_loggia, ent_old), per-building u-phase + hue cast. Street-level shots
// in districts where each archetype lives. SPOTS env narrows the list.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/facade|kit/i.test(m.text())) console.log(m.text()); });
await enterGame(page);
await page.waitForTimeout(4000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

const spots = [
  [41.6878, 44.7968, 'x_sololaki'],       // old town: win_old + ent_old + juliet
  [41.7095, 44.7935, 'x_chugureti'],      // brick stock: win_brick + shutters
  [41.7090, 44.7560, 'x_vake'],           // classic: win_stalin + juliet + ent_old
  [41.7770, 44.8060, 'x_gldani'],         // soviet: balc_loggia painted panels
  [41.7252, 44.7458, 'x_delisi'],         // mixed: hue cast + u-phase variety
];
const only = process.env.SPOTS ? process.env.SPOTS.split(',') : null;
for (const [lat, lon, name] of spots) {
  if (only && !only.some((s) => name.includes(s))) continue;
  await page.evaluate(([la, lo]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + 65, gy + 22, z - 65);
    g.camera.lookAt(x - 40, gy + 10, z + 40);
  }, [lat, lon]);
  await page.waitForTimeout(1200);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position, 0.016);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
