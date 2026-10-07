// WHICH light is the third one? Names every light in the scene, its parent
// chain and whether it is visible, and watches for one appearing.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);
const dump = async (label) => {
  const r = await page.evaluate(() => {
    const g = window.__game, out = [];
    g.scene.traverse((o) => {
      if (!o.isLight) return;
      const chain = [];
      for (let p = o; p; p = p.parent) chain.push(p.name || p.type);
      out.push({ type: o.type, name: o.name, vis: o.visible, i: +(o.intensity ?? 0).toFixed(2),
                 pos: o.position.toArray().map((n) => Math.round(n)), chain: chain.slice(0, 4).join(' < ') });
    });
    return out;
  });
  console.log(`\n--- ${label} (${r.length} lights) ---`);
  for (const l of r) console.log(`  ${l.type.padEnd(18)} vis=${String(l.vis).padEnd(5)} i=${String(l.i).padEnd(7)} ${JSON.stringify(l.pos).padEnd(22)} ${l.chain}`);
};
await dump('parked at the airport');
await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 220, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.42, 0, 0);
});
await page.waitForTimeout(6000);
await dump('over the city centre');
await page.keyboard.down('Space');
await page.waitForTimeout(2500);
await dump('while firing');
await page.keyboard.up('Space');
await browser.close();
