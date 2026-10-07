// TBS airport verification shots: top-down layout vs satellite, spawn view,
// terminal/apron closeup, approach view down runway 31L.
import { chromium } from 'playwright';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE:', m.text().slice(0, 200)); });
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000); // let airport GLBs land

const shot = async (name, fn) => {
  await page.evaluate(fn);
  await page.waitForTimeout(800);
  await page.evaluate(() => window.__game.renderer.render(window.__game.scene, window.__game.camera));
  await page.screenshot({ path: `${OUT}/airport_${name}.png` });
  console.log('shot', name);
};

await page.evaluate(() => {
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  window.__at = (lat, lon) => {
    const w = g.world;
    return {
      x: -(lon - w.meta.origin.lon) * w.meta.mlon,
      z: (lat - w.meta.origin.lat) * w.meta.mlat,
      y: w.heightAt(-(lon - w.meta.origin.lon) * w.meta.mlon, (lat - w.meta.origin.lat) * w.meta.mlat),
    };
  };
});

// 1. top-down over the whole field
await shot('top', () => {
  const g = window.__game;
  const p = window.__at(41.6682, 44.9522);
  g.camera.position.set(p.x, p.y + 2600, p.z + 1);
  g.camera.up.set(0, 0, -1);
  g.camera.lookAt(p.x, p.y, p.z);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});

// 2. what the player sees at spawn on runway 31L
await shot('spawn', () => {
  const g = window.__game;
  const sp = g.world.spawn;
  const y = g.world.heightAt(sp.x, sp.z);
  g.camera.up.set(0, 1, 0);
  g.camera.position.set(sp.x - Math.sin(sp.yaw) * -14, y + 7, sp.z - Math.cos(sp.yaw) * -14);
  g.camera.lookAt(sp.x + -Math.sin(sp.yaw) * 60, y + 3, sp.z + -Math.cos(sp.yaw) * 60);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});

// 3. terminal + gates from over the apron
await shot('terminal', () => {
  const g = window.__game;
  const p = window.__at(41.66780, 44.96140); // SW of the terminal, on the apron
  const t = window.__at(41.669742, 44.963939);
  g.camera.up.set(0, 1, 0);
  g.camera.position.set(p.x, p.y + 60, p.z);
  g.camera.lookAt(t.x, t.y + 10, t.z);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});

// 4. approach into 31L (from the SE, looking NW down the runway)
await shot('approach', () => {
  const g = window.__game;
  const a = window.__at(41.6540, 44.9702); // ~700 m out on final
  const b = window.__at(41.658265, 44.964827); // 31L threshold
  g.camera.up.set(0, 1, 0);
  g.camera.position.set(a.x, a.y + 90, a.z);
  g.camera.lookAt(b.x, b.y, b.z);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});

// 5. VIP terminal + tower area
await shot('vip', () => {
  const g = window.__game;
  const p = window.__at(41.67230, 44.95560);
  const t = window.__at(41.674056, 44.958342);
  g.camera.up.set(0, 1, 0);
  g.camera.position.set(p.x, p.y + 45, p.z);
  g.camera.lookAt(t.x, t.y + 8, t.z);
  g.world.update(g.camera.position);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
});

console.log('done');
await browser.close();
