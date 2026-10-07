// Park the camera directly in front of a named brand's sign and shoot it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const WANT = (process.env.BRANDS || 'სპარი,ნიკორა,ავერსი,საქართველოს ბანკი,ფარმადეპო,პსპ').split(',');
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 500 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForFunction(() => window.__game?.world?.shopSigns, null, { timeout: 90000 });
await page.waitForTimeout(2500);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
for (const name of WANT) {
  const ok = await page.evaluate((nm) => {
    const g = window.__game, w = g.world, sp = w.shopSigns;
    const ni = sp.names.indexOf(nm);
    if (ni < 0) return false;
    const s = sp.spots.find((q) => q.ni === ni);
    if (!s) return false;
    // stand 9 m out along the sign's facing normal, at sign height
    const nx = Math.sin(s.ang), nz = Math.cos(s.ang);
    g.camera.position.set(s.x + nx * 9, s.y + 0.4, s.z + nz * 9);
    g.camera.lookAt(s.x, s.y, s.z);
    w.update(g.camera.position, 0.7);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    return true;
  }, name);
  if (!ok) { console.log('no sign spot for', name); continue; }
  const file = `${OUT}/sign_${WANT.indexOf(name)}.png`;
  await page.screenshot({ path: file });
  console.log('shot', name);
}
await browser.close();
