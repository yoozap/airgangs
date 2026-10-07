// Find the real building clusters around the airport, then shoot them.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

const AP = { x: -18224.8, z: -7210.2 };
const clusters = await page.evaluate((AP) => {
  const w = window.__game.world;
  // building chunk records: the extruder tags them, so find the ones with
  // actual building geometry near the airport
  const near = [];
  for (const c of w.chunkMeshes) {
    const d2 = (c.cx - AP.x) ** 2 + (c.cz - AP.z) ** 2;
    if (d2 > 4000 * 4000) continue;
    const tris = c.mesh?.geometry?.index ? c.mesh.geometry.index.count / 3
      : (c.mesh?.geometry?.attributes?.position?.count ?? 0) / 3;
    if (tris > 400) near.push({ x: +c.cx.toFixed(0), z: +c.cz.toFixed(0), tris: Math.round(tris), d: Math.round(Math.sqrt(d2)), name: c.mesh?.name || '' });
  }
  near.sort((a, b) => a.d - b.d);
  return near.slice(0, 14);
}, AP);
console.log('heaviest geometry chunks near the airport (dist m, tris):');
for (const c of clusters) console.log(`   ${String(c.d).padStart(5)} m  ${String(c.tris).padStart(7)} tris  at ${c.x},${c.z}  ${c.name}`);

const shot = async (tag, cx, cz, agl, pitch) => {
  await page.evaluate(([cx, cz, agl, pitch]) => {
    const g = window.__game;
    g.crashed = false; g.crash = null; g.hull = 1000;
    g.plane.position.set(cx, (g.world.heightAt(cx, cz) || 0) + agl, cz + 180);
    g.airborne = true; g.vel.set(0, 0, -40); g.speed = 40;
    g.plane.rotation.set(pitch, 0, 0);
    g.world.update(g.plane.position, 0.016);
  }, [cx, cz, agl, pitch]);
  await page.waitForTimeout(6000);
  await page.screenshot({ path: `${OUT}/apf_${tag}.png` });
  console.log('shot', tag, 'at', cx, cz);
};
for (let i = 0; i < Math.min(3, clusters.length); i++) {
  await shot(`c${i}`, clusters[i].x, clusters[i].z, 95, -0.30);
}
await browser.close();
