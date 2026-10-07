// The Galleria was destroyed in the previous run and saved — verify the ruin
// restores visually on a fresh load (landmark squash/char persistence).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(5000);
const info = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const rec = w.landmarkRecs.find((r) => r.name.includes('გალერია'));
  if (!rec) return { error: 'not found' };
  // if restore left the collapse to animate, fast-forward it
  for (let i = 0; i < 200; i++) w._updateCollapses(0.05);
  const gy = w.heightAt(rec.x, rec.z) ?? 450;
  g.camera.position.set(rec.x + 110, gy + 55, rec.z - 110);
  g.camera.lookAt(rec.x, gy + 4, rec.z);
  w.update(g.camera.position, 0.7);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  return { name: rec.name, hp: rec.hp, stage: rec.stage, scaleY: rec.root.scale.y };
});
console.log('mall ruin:', JSON.stringify(info));
await page.screenshot({ path: `${OUT}/dmg_mall_ruin.png` });
await browser.close();
