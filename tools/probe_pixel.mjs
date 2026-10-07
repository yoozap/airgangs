import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal'] });
const p = await b.newPage({ viewport: { width: 1400, height: 900 } });
p.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(p);
await p.waitForTimeout(8000);
console.log(JSON.stringify(await p.evaluate(() => {
  const g = window.__game, w = g.world;
  // same camera as the Metekhi shot
  const lat = 41.69035, lon = 44.81000, a = 70;
  const x = -(lon - w.meta.origin.lon) * w.meta.mlon;
  const z = (lat - w.meta.origin.lat) * w.meta.mlat;
  const gy = w.heightAt(x, z) ?? 400;
  g.camera.position.set(x + a * 0.7, gy + a, z + a * 0.7);
  g.camera.lookAt(x, gy, z);
  g.camera.updateMatrixWorld();
  w.update(g.camera.position, 0.7);
  const list = [];
  g.scene.traverse((o) => { if (o.isMesh && o.geometry && o.visible) list.push(o); });
  const rc = g.raycaster;
  rc.firstHitOnly = false;
  // fire at the middle of the pale patch (screen ~0.52, 0.22)
  rc.setFromCamera({ x: 0.04, y: 0.56 }, g.camera);
  const hits = rc.intersectObjects(list, false).slice(0, 5);
  return { total: list.length, hits: hits.map((h) => ({
    name: h.object.name || '(unnamed)',
    col: h.object.material?.color ? '#' + h.object.material.color.getHexString() : '?',
    map: h.object.material?.map?.image?.currentSrc?.split('/').pop() || h.object.material?.map ? 'tex' : 'none',
    y: +h.point.y.toFixed(1),
  })) };
})));
await b.close();
