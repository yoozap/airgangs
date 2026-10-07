// Full-city flight smoke: fly across the center for ~50 s of game time,
// assert zero pageerrors, report renderer stats + attribution rollup.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const errors = [];
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => { errors.push(e.message); console.log('PAGEERROR:', e.message); });
await enterGame(page);
await page.waitForTimeout(12000); // shopfronts + malls + chestnuts settle

// launch toward the center at altitude and let the real loop run
await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  // Rustaveli coords in local frame
  const x = -(44.7995 - w.meta.origin.lon) * w.meta.mlon;
  const z = (41.6975 - w.meta.origin.lat) * w.meta.mlat;
  g.plane.position.set(x + 4000, (w.heightAt(x, z) ?? 450) + 650, z - 4000);
  g.plane.lookAt(x, (w.heightAt(x, z) ?? 450) + 300, z);
  g.airborne = true;
  const fwd = new g.vel.constructor(0, 0, -1).applyQuaternion(g.plane.quaternion);
  g.vel.copy(fwd).multiplyScalar(120);
});
for (let i = 0; i < 5; i++) {
  await page.waitForTimeout(9000);
  const s = await page.evaluate(() => {
    const g = window.__game;
    return {
      pos: [Math.round(g.plane.position.x), Math.round(g.plane.position.y), Math.round(g.plane.position.z)],
      calls: g.renderer.info.render.calls,
      tris: Math.round(g.renderer.info.render.triangles / 1e5) / 10,
      signs: g.world.shopSigns ? g.world.shopSigns.meshes.filter((m) => m.visible).length : -1,
    };
  });
  console.log(`t=${(i + 1) * 9}s`, JSON.stringify(s));
  await page.screenshot({ path: `${OUT}/smoke_${i}.png` });
}
const attrib = await page.evaluate(() => window.__game.world.attribution?.slice(-260));
console.log('attribution tail:', attrib);
console.log(errors.length ? `FAIL: ${errors.length} pageerrors` : 'PASS: 0 pageerrors');
await browser.close();
