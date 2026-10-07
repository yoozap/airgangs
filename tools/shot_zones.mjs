import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE:', m.text()); });
await page.goto('http://localhost:5173/');
await page.evaluate(() => localStorage.removeItem('tf-pins'));
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);

// 1. spawn: the zone slam should have fired for Saburtalo — re-fire it for the shot
await page.evaluate(() => {
  const g = window.__game;
  console.log('zone at spawn:', g.zones.current?.name);
  g.zones._announce(g.zones.current ?? { name: '?', tag: '?' });
});
await page.waitForTimeout(1200);
await page.screenshot({ path: `${OUT}/zone_announce.png` });
console.log('shot zone_announce');

// 2. clamp sanity: a point far west of the box must be pushed back in
const clamp = await page.evaluate(() => {
  const g = window.__game;
  const b = g.boundary;
  const pos = { x: b.box.maxX + 5000, y: 800, z: b.box.maxZ + 3000 };
  const vel = { x: 50, y: 0, z: 80 };
  const hit = b.clamp(pos, vel);
  return { hit, pos, vel, box: b.box };
});
console.log('clamp test:', JSON.stringify(clamp));

// 3. boundary wall visual: park the camera near the north wall
await page.evaluate(() => {
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  const b = g.boundary.box;
  const midX = (b.minX + b.maxX) / 2;
  const gy = g.world.heightAt(midX, b.maxZ - 900) ?? 500;
  g.camera.position.set(midX, gy + 260, b.maxZ - 1400);
  g.camera.up.set(0, 1, 0);
  g.camera.lookAt(midX, gy + 150, b.maxZ);
  g.boundary.update({ x: midX, z: b.maxZ - 500 }, 1.2);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});
await page.waitForTimeout(2500);
await page.evaluate(() => {
  const g = window.__game;
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});
await page.screenshot({ path: `${OUT}/boundary_wall.png` });
console.log('shot boundary_wall');

// 4. big map with districts; click twice to drop pins
await page.evaluate(() => window.__game.mapui.toggle());
await page.waitForTimeout(400);
const box = await page.evaluate(() => {
  const r = document.getElementById('bigmap-canvas').getBoundingClientRect();
  return { x: r.left, y: r.top, w: r.width, h: r.height };
});
await page.mouse.click(box.x + box.w * 0.45, box.y + box.h * 0.4);
await page.mouse.click(box.x + box.w * 0.62, box.y + box.h * 0.62);
await page.waitForTimeout(300);
await page.screenshot({ path: `${OUT}/map_districts_pins.png` });
console.log('shot map_districts_pins, pins:', await page.evaluate(() => JSON.stringify(window.__game.mapui.pins)));

// 5. remove one pin by clicking it again
await page.mouse.click(box.x + box.w * 0.62, box.y + box.h * 0.62);
console.log('after remove click, pins:', await page.evaluate(() => JSON.stringify(window.__game.mapui.pins)));

// 5b. zoomed map over the old town: subzone borders + labels
await page.evaluate(() => {
  const g = window.__game;
  const m = g.mapui;
  const c = m._citySat();
  m.zoom = 2.5;
  const [px, py] = m._pxLL(c, 41.6905, 44.808); // Abanotubani / old town
  m.cx = px;
  m.cy = py;
  m._clampCenter();
  g.zones.update(-(44.8065 - m.meta.origin.lon) * m.meta.mlon, (41.69055 - m.meta.origin.lat) * m.meta.mlat);
  m._drawBig();
});
await page.waitForTimeout(300);
await page.screenshot({ path: `${OUT}/map_subzones.png` });
console.log('shot map_subzones, zone:', await page.evaluate(() => document.getElementById('za-name').textContent + ' / ' + document.getElementById('za-tag').textContent));

// 6. minimap + beams: close map, drop a pin 600 m from the plane, resume loop
await page.evaluate(() => {
  const g = window.__game;
  const m = g.mapui.meta;
  const p = g.plane.position;
  const lat = m.origin.lat + (p.z + 600) / m.mlat;
  const lon = m.origin.lon - (p.x + 300) / m.mlon;
  g.mapui.pins.push({ lat, lon });
  g.mapui._savePins();
  g.mapui.toggle();
  g.renderer.setAnimationLoop(() => g._tick());
});
await page.waitForTimeout(1500);
await page.screenshot({ path: `${OUT}/minimap_pin_beam.png` });
console.log('shot minimap_pin_beam, zone label:', await page.evaluate(() => document.getElementById('zone-label').textContent));
await browser.close();
