// Stand in the trench and look down it — the view that shows whether an
// underpass reads as a road going under something, or as a ditch.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/18a6821b-954c-4f22-8503-0687fdedd980/scratchpad';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => { const g = window.__game; g.renderer.setAnimationLoop(null); if (g.hud) g.hud.style.display = 'none'; });
const n = Number(process.env.N || 3);
for (let k = 0; k < n; k++) {
  const info = await page.evaluate((kk) => {
    const g = window.__game; const w = g.world;
    const inCity = (p) => { const la = w.meta.origin.lat + p[1] / w.meta.mlat, lo = w.meta.origin.lon - p[0] / w.meta.mlon;
      return la > 41.66 && la < 41.80 && lo > 44.72 && lo < 44.90; };
    const lids = w._lids.filter(l => inCity(l.pts[l.pts.length >> 1])).sort((a, b) => b.pts.length - a.pts.length);
    const lid = lids[kk];
    if (!lid) return null;
    // stand just outside the portal, on the ramp, looking into the mouth
    // stand ON the ramp centreline, half way down, looking into the mouth
    const C = lid.corr, P = lid.pts;
    const iM = C.findIndex((p) => p[0] === P[0][0] && p[1] === P[0][1]);
    const iC = Math.max(0, Math.floor(iM * 0.45));
    const cx = C[iC][0], cz = C[iC][1];
    g.camera.position.set(cx, w.heightAt(cx, cz) + 2.2, cz);
    g.camera.lookAt(P[1][0], lid.y[1] + 2.2, P[1][1]);
    const la = w.meta.origin.lat + P[0][1] / w.meta.mlat, lo = w.meta.origin.lon - P[0][0] / w.meta.mlon;
    return { lat: +la.toFixed(5), lon: +lo.toFixed(5), hw: +lid.hw.toFixed(1),
             depth: +(lid.gy[0] - lid.y[0]).toFixed(1), pts: P.length };
  }, k);
  if (!info) break;
  await page.waitForTimeout(900);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/in_underpass_${k}.png` });
  console.log('shot', k, JSON.stringify(info));
}
await browser.close();
