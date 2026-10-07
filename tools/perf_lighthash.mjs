// Directly: what changes in the frame where the program count jumps? Three
// recompiles EVERY material in the scene when the lights hash, tone mapping or
// clipping-plane count changes, so this records all of them per frame.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_lighthash.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 220, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.42, 0, 0);
  const rows = [];
  window.__rows = rows;
  let last = performance.now();
  const tick = g._tick.bind(g);
  g._tick = () => {
    const t0 = performance.now();
    const gap = t0 - last; last = t0;
    tick();
    let dir = 0, pt = 0, spot = 0, hemi = 0, shadows = 0, morph = 0;
    g.scene.traverse((o) => {
      if (!o.visible) return;
      if (o.isDirectionalLight) { dir++; if (o.castShadow) shadows++; }
      else if (o.isPointLight) { pt++; if (o.castShadow) shadows++; }
      else if (o.isSpotLight) { spot++; if (o.castShadow) shadows++; }
      else if (o.isHemisphereLight) hemi++;
      if (o.isMesh && o.morphTargetInfluences) morph++;
    });
    rows.push({ gap: +gap.toFixed(0), p: g.renderer.info.programs.length,
      dir, pt, spot, hemi, shadows, morph,
      tm: g.renderer.toneMapping, cl: g.renderer.clippingPlanes.length,
      sm: g.renderer.shadowMap.enabled ? 1 : 0 });
  };
});
await page.waitForTimeout(3000);
await page.evaluate(() => { window.__rows.length = 0; });
await page.keyboard.down('Space');
await page.waitForTimeout(3000);
await page.keyboard.up('Space');
await page.waitForTimeout(3000);

const rows = await page.evaluate(() => window.__rows);
console.log(`\n${rows.length} frames. Rows where the PROGRAM COUNT moved, or the frame was slow:\n`);
console.log('frame   gap ms  programs   dirL ptL spotL hemiL shadows  morphMeshes  tone  clip  shadowMap');
let prev = rows[0];
for (let i = 1; i < rows.length; i++) {
  const r = rows[i];
  const jumped = r.p !== prev.p;
  if (jumped || r.gap > 60) {
    console.log(`${String(i).padStart(5)} ${String(r.gap).padStart(7)} ${String(r.p).padStart(9)}${jumped ? ` (+${r.p - prev.p})` : '     '}`
      + `   ${String(r.dir).padStart(4)}${String(r.pt).padStart(4)}${String(r.spot).padStart(6)}${String(r.hemi).padStart(6)}`
      + `${String(r.shadows).padStart(9)}${String(r.morph).padStart(13)}${String(r.tm).padStart(6)}${String(r.cl).padStart(6)}${String(r.sm).padStart(11)}`);
  }
  prev = r;
}
const uniq = (k) => [...new Set(rows.map((r) => r[k]))];
console.log('\ndistinct values seen across the burst:');
for (const k of ['dir', 'pt', 'spot', 'hemi', 'shadows', 'morph', 'tm', 'cl', 'sm']) console.log(`  ${k}: ${uniq(k).join(', ')}`);
await browser.close();
