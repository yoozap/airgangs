import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const b = await chromium.launch({ args: ['--use-angle=metal'] });
const p = await b.newPage({ viewport: { width: 1200, height: 800 } });
p.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(p);
await p.waitForTimeout(8000);
await p.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const setup = async (hideWhat) => p.evaluate((mode) => {
  const g = window.__game, w = g.world;
  const lat = 41.69035, lon = 44.81000, a = 70;
  const x = -(lon - w.meta.origin.lon) * w.meta.mlon;
  const z = (lat - w.meta.origin.lat) * w.meta.mlat;
  const gy = w.heightAt(x, z) ?? 400;
  g.camera.position.set(x + a * 0.7, gy + a, z + a * 0.7);
  g.camera.lookAt(x, gy, z);
  w.update(g.camera.position, 0.7);
  let n = 0;
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.material) return;
    const col = o.material.color ? '#' + o.material.color.getHexString() : '';
    if (mode === 'water' && col === '#84956a') { o.visible = false; n++; }
    if (mode === 'ground' && o.material.map && col === '#ffffff'
        && (o.geometry.index?.count || 0) > 2000) { o.visible = false; n++; }
  });
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  return n;
}, hideWhat);
console.log('hid water meshes:', await setup('water'));
await p.screenshot({ path: `${OUT}/hide_water.png` });
console.log('hid ground meshes:', await setup('ground'));
await p.screenshot({ path: `${OUT}/hide_both.png` });
await b.close();
