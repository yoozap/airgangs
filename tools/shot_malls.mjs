// Verify the Blender malls: aerial-oblique shots of the biggest ones.
import { readFileSync } from 'fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const malls = JSON.parse(readFileSync(new URL('../public/tbilisi/malls.json', import.meta.url)));
const pick = (name) => malls.find((m) => m.name.includes(name));
const spots = [
  [pick('თბილისი მოლი'), 'mall_tbilisimall'],
  [pick('გალერია'), 'mall_galleria'],
  [pick('ისთ ფოინთი'), 'mall_eastpoint'],
  [pick('Дезертирка') ?? pick('ბაზარი'), 'mall_dezerterka'],
].filter(([m]) => m);

const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/malls/i.test(m.text())) console.log(m.text()); });
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
for (const [m, name] of spots) {
  await page.evaluate(([x, z]) => {
    const g = window.__game;
    const gy = g.world.heightAt(x, z) ?? 450;
    g.camera.position.set(x + 130, gy + 75, z - 130);
    g.camera.lookAt(x, gy + 8, z);
  }, [m.x, m.z]);
  await page.waitForTimeout(700);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name, m.name);
}
await browser.close();
