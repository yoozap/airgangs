// Look straight into a bore mouth, from outside and from in front of it, so a
// blocked portal is obvious. BORE=<index> picks the tube (default: the
// Gorgasali one under the Old Town).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const BORE = Number(process.env.BORE ?? 19);
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
await page.addStyleTag({ content: '#hud,#combat-hud,#skillbar,#map-mini,#hud-hint,#attribution{display:none!important}' });
for (const [name, end, back] of [['mouth_in', 0, 55], ['mouth_out', 1, 55],
  ['mouth_in_close', 0, 22], ['mouth_out_close', 1, 22]]) {
  const info = await page.evaluate(([bi, endIdx, dist]) => {
    const g = window.__game; const t = g.world._boreTubes[bi];
    const k = endIdx ? t.pts.length - 1 : 0;
    const k2 = endIdx ? t.pts.length - 2 : 1;
    const [mx, mz] = t.pts[k];
    const [nx2, nz2] = t.pts[k2];
    // outward = away from the tunnel
    let dx = mx - nx2; let dz = mz - nz2;
    const L = Math.hypot(dx, dz) || 1; dx /= L; dz /= L;
    const y = t.y[k];
    g.camera.position.set(mx + dx * dist, y + 6, mz + dz * dist);
    g.camera.lookAt(mx - dx * 12, y + 2.5, mz - dz * 12);
    return { x: Math.round(mx), z: Math.round(mz), floor: +y.toFixed(1),
      lat: +(g.world.meta.origin.lat + mz / g.world.meta.mlat).toFixed(5),
      lon: +(g.world.meta.origin.lon - mx / g.world.meta.mlon).toFixed(5) };
  }, [BORE, end, back]);
  await page.waitForTimeout(800);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name, JSON.stringify(info));
}
await browser.close();
