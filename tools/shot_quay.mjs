// River embankment shots framed the way the reference photographs are: stand
// on a bridge, look down the Mtkvari, and judge both banks at once. Camera and
// aim are plain lat/lon so the framing never depends on bank normals.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/infra:|bridges:|infra kit|river banks|^river:/i.test(m.text())) console.log(m.text().slice(0, 400)); });
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
// the HUD/skillbar/minimap are DOM over the canvas -- these shots are about
// the masonry, not the instruments
await page.addStyleTag({ content: '#hud,#combat-hud,#skillbar,#map-mini,#hud-hint,#attribution,#zone-slam,#hud-toast,#hud-speed,#hud-alt,#healthbar{display:none!important}' });

// Aim straight AT a walled bank segment from above and along it: the wall is
// then dead centre whatever the river does. name, lat/lon to search near,
// metres up, metres back along the bank.
const spots = [
  ['quay_a', 41.69450, 44.80560, 22, 40],
  ['quay_b', 41.69250, 44.80870, 22, 40],
  ['quay_c', 41.70050, 44.80430, 22, 40],
  ['quay_low', 41.69450, 44.80560, 5, 26],
];
// eye-level look at the wall FACE from out over the water -- the only view
// that shows the coping, the parapet standing on it and the lamp behind
const faces = [
  ['face_a', 41.69450, 44.80560, 13, 3],
  ['face_b', 41.70050, 44.80430, 13, 3],
  ['face_c', 41.69250, 44.80870, 16, 4],
];
for (const [name, lat, lon, up, back] of spots) {
  const info = await page.evaluate(([la, lo, u, bk]) => {
    const g = window.__game; const w = g.world;
    const tx = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const tz = (la - w.meta.origin.lat) * w.meta.mlat;
    const banks = (w._riverBanks || []).filter((b) => b.kind === 'wall');
    if (!banks.length) return null;
    let best = null; let bd = 1e9;
    for (const b of banks) {
      const d = (b.x - tx) ** 2 + (b.z - tz) ** 2;
      if (d < bd) { bd = d; best = b; }
    }
    // the piece runs along its local X, so that is the direction to back off in
    const ax = Math.cos(best.ang); const az = -Math.sin(best.ang);
    g.camera.position.set(best.x + ax * bk, best.y + u, best.z + az * bk);
    g.camera.lookAt(best.x, best.y - 1, best.z);
    return { x: +best.x.toFixed(1), z: +best.z.toFixed(1), y: +best.y.toFixed(1),
      lat: +(w.meta.origin.lat + best.z / w.meta.mlat).toFixed(5),
      lon: +(w.meta.origin.lon - best.x / w.meta.mlon).toFixed(5),
      sy: +best.sy.toFixed(2) };
  }, [lat, lon, up, back]);
  if (!info) { console.log('no walled bank for', name); continue; }
  await page.waitForTimeout(700);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name, JSON.stringify(info));
}
for (const [name, lat, lon, out, up] of faces) {
  await page.evaluate(([la, lo, o, u]) => {
    const g = window.__game; const w = g.world;
    const tx = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const tz = (la - w.meta.origin.lat) * w.meta.mlat;
    const banks = (w._riverBanks || []).filter((b) => b.kind === 'wall');
    let best = null; let bd = 1e9;
    for (const b of banks) {
      const d = (b.x - tx) ** 2 + (b.z - tz) ** 2;
      if (d < bd) { bd = d; best = b; }
    }
    // stand off the face along its +Z (the water side) and a little along it
    const wx = Math.sin(best.ang); const wz = Math.cos(best.ang);
    const ax = Math.cos(best.ang); const az = -Math.sin(best.ang);
    g.camera.position.set(best.x + wx * o + ax * o * 0.7, best.y + u,
      best.z + wz * o + az * o * 0.7);
    g.camera.lookAt(best.x, best.y + 0.9, best.z);
  }, [lat, lon, out, up]);
  await page.waitForTimeout(700);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
// From mid-channel looking back at the bank -- the boat's-eye view the TV
// footage is shot from, and the only one that judges wall + parapet together.
for (const [name, lat, lon] of [['water_a', 41.69550, 44.80700],
  ['water_b', 41.70050, 44.80430], ['water_c', 41.69250, 44.80870]]) {
  await page.evaluate(([la, lo]) => {
    const g = window.__game; const w = g.world;
    const tx = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const tz = (la - w.meta.origin.lat) * w.meta.mlat;
    const banks = (w._riverBanks || []).filter((b) => b.kind === 'wall');
    let best = null; let bd = 1e9;
    for (const b of banks) { const d = (b.x - tx) ** 2 + (b.z - tz) ** 2; if (d < bd) { bd = d; best = b; } }
    const wx = Math.sin(best.ang); const wz = Math.cos(best.ang);
    let out = 8; let low = 1e9;
    for (let d = 4; d <= 120; d += 4) {
      const h = w.heightAt(best.x + wx * d, best.z + wz * d);
      if (h != null && h < low) { low = h; out = d; }
    }
    out = Math.min(out, 46); // stay close enough to read the masonry
    const ax = Math.cos(best.ang); const az = -Math.sin(best.ang);
    g.camera.position.set(best.x + wx * out + ax * 16, best.y - 1.5,
      best.z + wz * out + az * 16);
    g.camera.lookAt(best.x, best.y + 0.5, best.z);
  }, [lat, lon]);
  await page.waitForTimeout(700);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
console.log('banks:', JSON.stringify(await page.evaluate(() => {
  const b = window.__game.world._riverBanks || [];
  const by = {};
  for (const s of b) by[s.kind] = (by[s.kind] || 0) + 1;
  return by;
})));
await browser.close();
