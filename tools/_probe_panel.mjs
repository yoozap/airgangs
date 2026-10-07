// Did the real panel blocks land in the city? Flies to Gldani (a Soviet
// microdistrict) and shoots it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => {
  const t = m.text();
  if (/panel block|panel blocks|kit missing|facade kit/i.test(t)) console.log('  [page]', t.slice(0, 200));
});
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

const shot = async (tag, lat, lon, agl, pitch) => {
  await page.evaluate(([lat, lon, agl, pitch]) => {
    const g = window.__game, m = g.world.meta;
    const x = -(lon - m.origin.lon) * m.mlon;
    const z = (lat - m.origin.lat) * m.mlat;
    g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + agl, z);
    g.airborne = true; g.vel.set(0, 0, -50); g.speed = 50;
    g.plane.rotation.set(pitch, 0, 0);
    g.world.update(g.plane.position, 0.016);
  }, [lat, lon, agl, pitch]);
  await page.waitForTimeout(7000);
  await page.screenshot({ path: `${OUT}/panel_${tag}.png` });
  console.log('shot', tag);
};

await shot('gldani_low', 41.794, 44.811, 90, -0.30);
await shot('gldani_street', 41.7935, 44.8105, 28, -0.10);
const stats = await page.evaluate(() => {
  const w = window.__game.world;
  return { kit: w._panelKit ? [...w._panelKit.keys()] : null,
           collidables: w.collidables.filter((m) => m.userData?.panelBlock).length,
           chunks: w.chunkMeshes.length };
});
console.log('kit variants:', stats.kit, ' panel colliders:', stats.collidables, ' chunk records:', stats.chunks);
await browser.close();
