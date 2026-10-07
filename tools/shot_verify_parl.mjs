// Ground-truth check: hide every OSM building so the satellite drape shows
// the REAL parliament roof, then look straight down at the landmark model.
import { chromium } from 'playwright';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
await page.evaluate(() => {
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  for (const c of g.world.chunkMeshes) c.mesh.visible = false; // buildings/trees off
  const w = g.world;
  const x = -(44.79805 - w.meta.origin.lon) * w.meta.mlon;
  const z = (41.69667 - w.meta.origin.lat) * w.meta.mlat;
  const gy = w.heightAt(x, z) ?? 450;
  g.camera.position.set(x, gy + 420, z + 1);
  g.camera.up.set(0, 0, -1); // north up in the frame
  g.camera.lookAt(x, gy, z);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});
await page.waitForTimeout(1200);
await page.evaluate(() => window.__game.renderer.render(window.__game.scene, window.__game.camera));
await page.screenshot({ path: `${OUT}/verify_parl_sat.png` });
console.log('done');
await browser.close();
