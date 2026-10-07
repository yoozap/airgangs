// Headless check: boot the OSM map, park the camera in different districts,
// screenshot the building facades.
import { chromium } from 'playwright';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
// stop the game loop so the chase cam doesn't fight the staged camera
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

const spots = [
  // [lat, lon, name, topDown] — oblique facade view or steep roof view
  [41.6975, 44.7995, 'street_rustaveli', 0],
  [41.7245, 44.7461, 'facade_saburtalo', 0],
  [41.7052, 44.7938, 'facade_aghmashenebeli', 0],
  [41.6878, 44.7968, 'facade_sololaki', 0],
  [41.794, 44.811, 'facade_gldani', 0],
  [41.6885, 44.8095, 'roofs_oldtown', 1],
  [41.733, 44.751, 'roofs_saburtalo', 1],
  [41.69466, 44.77889, 'tvtower_full', 2],
  [41.69466, 44.77889, 'tvtower_pod', 3],
  [41.69754, 44.81632, 'sameba', 4],
  [41.69754, 44.81632, 'sameba_close', 5],
  [41.69667, 44.79805, 'parliament_a', 6],
  [41.69667, 44.79805, 'parliament_b', 7],
  [41.70731, 44.78601, 'philharmonic_front', 8],
  [41.70731, 44.78601, 'philharmonic_air', 9],
  [41.691337, 44.807511, 'church_sioni', 10],
  [41.69794, 44.799223, 'church_kashveti', 10],
  [41.69035, 44.81003, 'bridge_metekhi', 11],
  [41.69687, 44.80827, 'bridge_baratashvili', 11],
  [41.70047, 44.80256, 'bridge_dry', 11],
  [41.72554, 44.78186, 'bridge_vakhushti', 11],
];
for (const [lat, lon, name, topDown] of spots) {
  await page.evaluate(([la, lo, top]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    if (top === 1) {
      g.camera.position.set(x + 90, gy + 330, z - 90);
      g.camera.lookAt(x, gy, z);
    } else if (top === 2) {
      g.camera.position.set(x + 330, gy + 190, z - 330); // full tower
      g.camera.lookAt(x, gy + 135, z);
    } else if (top === 3) {
      g.camera.position.set(x + 95, gy + 185, z - 75); // observation pod
      g.camera.lookAt(x, gy + 150, z);
    } else if (top === 4) {
      g.camera.position.set(x - 260, gy + 170, z + 150); // complex from the west gate side
      g.camera.lookAt(x, gy + 30, z);
    } else if (top === 5) {
      g.camera.position.set(x - 150, gy + 55, z + 75); // west front + grand stairs
      g.camera.lookAt(x + 20, gy + 45, z - 15);
    } else if (top === 6) {
      g.camera.position.set(x + 110, gy + 55, z + 110);
      g.camera.lookAt(x, gy + 16, z);
    } else if (top === 7) {
      g.camera.position.set(x - 110, gy + 55, z - 110);
      g.camera.lookAt(x, gy + 16, z);
    } else if (top === 8) {
      g.camera.position.set(x - 150, gy + 40, z - 105); // entrance from the junction
      g.camera.lookAt(x, gy + 17, z);
    } else if (top === 9) {
      g.camera.position.set(x - 130, gy + 240, z - 130); // drum + parcel from the air
      g.camera.lookAt(x, gy, z);
    } else if (top === 10) {
      g.camera.position.set(x + 55, gy + 32, z - 55); // instanced church close-up
      g.camera.lookAt(x, gy + 12, z);
    } else if (top === 11) {
      g.camera.position.set(x + 90, gy + 26, z + 70); // bridge side view
      g.camera.lookAt(x, gy - 2, z);
    } else {
      g.camera.position.set(x + 140, gy + 55, z - 140);
      g.camera.lookAt(x - 120, gy + 22, z + 120);
    }
  }, [lat, lon, topDown]);
  await page.waitForTimeout(1500); // let chunk culling + textures settle
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
console.log('attribution:', await page.evaluate(() => window.__game.world.attribution));
await browser.close();
