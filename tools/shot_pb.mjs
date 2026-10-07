// Headless check: sample per-building real-photo facades (textures/pb/) and
// screenshot each from an oblique street view. Needs `npm run dev` running.
//   SHOT_DIR=/tmp/pb node tools/shot_pb.mjs            — geographic spread sample
//   PB_BIDS=b12345,b67890 node tools/shot_pb.mjs       — shoot specific buildings
import { readFileSync } from 'node:fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const N = Number(process.env.PB_N || 12);
const man = JSON.parse(readFileSync(new URL('../public/textures/pb/manifest.json', import.meta.url)));

let picks;
if (process.env.PB_BIDS) {
  picks = process.env.PB_BIDS.split(',').map((k) => [k.trim(), man[k.trim()]]);
  const missing = picks.filter(([, m]) => !m).map(([k]) => k);
  if (missing.length) throw new Error(`not in manifest: ${missing.join(', ')}`);
} else {
  // one building per coarse geographic bin, tallest first, until N — spreads the
  // sample across districts instead of clustering in the photo-dense center
  const bins = new Map();
  for (const [key, m] of Object.entries(man)) {
    const bk = `${Math.round(m.lat * 90)}_${Math.round(m.lon * 90)}`; // ~1.2km bins
    if (!bins.has(bk) || (m.floors || 1) > (bins.get(bk)[1].floors || 1)) bins.set(bk, [key, m]);
  }
  picks = [...bins.values()].sort((a, b) => (b[1].floors || 1) - (a[1].floors || 1)).slice(0, N);
}

const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(4000);
// stop the game loop so the chase cam doesn't fight the staged camera
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

for (const [key, m] of picks) {
  await page.evaluate(([la, lo, floors]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    const h = Math.max(9, (floors || 3) * 3);
    const d = 45 + h * 1.6; // back off further for taller buildings
    g.camera.position.set(x + d, gy + h * 0.9 + 12, z - d);
    g.camera.lookAt(x, gy + h * 0.5, z);
  }, [m.lat, m.lon, m.floors]);
  await page.waitForTimeout(1500); // let chunk culling + textures settle
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/pb_${key}.png` });
  console.log('shot', key, m.file, '-', m.credit);
}
console.log('attribution:', await page.evaluate(() => window.__game.world.attribution));
await browser.close();
