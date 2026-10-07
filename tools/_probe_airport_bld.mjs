// What is actually standing in the airport district, and how did the builder
// classify it? Shoots the area and dumps the per-building facts.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

// the spawn apron is the airport; work outward from there
const AP = { x: -18224.8, z: -7210.2 };

const stats = await page.evaluate((AP) => {
  const w = window.__game.world;
  // every building record the extruder kept, near the airport
  let near = 0; const byStyle = {}; const heights = [];
  for (const [, ch] of w.chunkMeshes.entries ? [] : []) { /* not iterable that way */ }
  // chunk records carry cx/cz; building records live on the chunk data
  const out = { panelHere: 0, total: 0 };
  for (const it of (w._panelPlacements || [])) {
    if ((it.x - AP.x) ** 2 + (it.z - AP.z) ** 2 < 2500 * 2500) out.panelHere++;
  }
  // collidables give a rough idea of what geometry is here
  const nearCol = w.collidablesNear ? w.collidablesNear(AP.x, AP.z, 1).length : 0;
  return { ...out, nearCol };
}, AP);
console.log('panel blocks within 2.5 km of the airport:', stats.panelHere, '| collidables in cell:', stats.nearCol);

const shot = async (tag, dx, dy, dz, pitch, yaw = 0) => {
  await page.evaluate(([AP, dx, dy, dz, pitch, yaw]) => {
    const g = window.__game;
    g.crashed = false; g.crash = null; g.hull = 1000;
    g.plane.position.set(AP.x + dx, (g.world.heightAt(AP.x + dx, AP.z + dz) || 0) + dy, AP.z + dz);
    g.airborne = true; g.vel.set(0, 0, -40); g.speed = 40;
    g.plane.rotation.set(pitch, yaw, 0);
    g.world.update(g.plane.position, 0.016);
  }, [AP, dx, dy, dz, pitch, yaw]);
  await page.waitForTimeout(6000);
  await page.screenshot({ path: `${OUT}/ap_${tag}.png` });
  console.log('shot', tag);
};

await shot('terminal', 120, 70, 260, -0.26);
await shot('village_n', 400, 55, -700, -0.14, 0.9);
await shot('village_e', -900, 50, 300, -0.12, -0.6);
await browser.close();
