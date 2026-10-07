// Find the most elevated generic span in the city and photograph it side-on.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/18a6821b-954c-4f22-8503-0687fdedd980/scratchpad';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => { const g = window.__game; g.renderer.setAnimationLoop(null); if (g.hud) g.hud.style.display = 'none'; });
const list = await page.evaluate(() => {
  const w = window.__game.world;
  const out = [];
  for (const br of w._bridgesRef || []) {
    const n = (br.length - 1) / 2;
    if (n < 2) continue;
    const mi = n >> 1;
    const x = br[1 + mi * 2] / 10, z = br[2 + mi * 2] / 10;
    const la = w.meta.origin.lat + z / w.meta.mlat, lo = w.meta.origin.lon - x / w.meta.mlon;
    if (la < 41.64 || la > 41.82 || lo < 44.70 || lo > 44.92) continue;
    out.push({ x, z, w: br[0], lat: +la.toFixed(5), lon: +lo.toFixed(5) });
  }
  return out;
});
console.log('city bridge ways:', list.length);
const picks = list.filter(b => b.w >= 10).slice(0, 60);
// rank by how far the deck is above the ground beneath it
const ranked = await page.evaluate((ps) => {
  const w = window.__game.world;
  return ps.map(p => ({ ...p, clear: 0 })).map(p => {
    let best = 0;
    for (let d = -30; d <= 30; d += 6) {
      const g0 = w._sampleGrid(w.grid, p.x, p.z);
      best = Math.max(best, g0 - w.heightAt(p.x + d, p.z));
    }
    return { ...p, clear: +best.toFixed(1) };
  }).sort((a, b) => b.clear - a.clear).slice(0, 6);
}, picks);
console.log(JSON.stringify(ranked));
let k = 0;
for (const b of ranked.slice(0, 3)) {
  await page.evaluate((p) => {
    const g = window.__game; const w = g.world;
    const gy = w._sampleGrid(w.grid, p.x, p.z);
    g.camera.position.set(p.x + 55, gy + 9, p.z + 55);
    g.camera.lookAt(p.x, gy + 5, p.z);
  }, b);
  await page.waitForTimeout(900);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/span_${k}.png` });
  console.log('shot span', k, b.lat, b.lon);
  k++;
}
await browser.close();
