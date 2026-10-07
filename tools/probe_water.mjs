import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal'] });
const p = await b.newPage({ viewport: { width: 800, height: 400 } });
p.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(p);
await p.waitForTimeout(6000);
console.log(JSON.stringify(await p.evaluate(async () => {
  const g = window.__game, w = g.world;
  // reuse a THREE constructor already in the scene instead of importing
  const V3 = g.camera.position.constructor;
  // the pale sheet sits just NE of Metekhi church
  const lat = 41.6925, lon = 44.8060;
  const x = -(lon - w.meta.origin.lon) * w.meta.mlon;
  const z = (lat - w.meta.origin.lat) * w.meta.mlat;
  // walk the scene: which meshes' bounding boxes cover this point?
  const out = [];
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.geometry) return;
    if (!o.geometry.boundingBox) o.geometry.computeBoundingBox();
    const bb = o.geometry.boundingBox;
    if (!bb) return;
    if (x < bb.min.x - 5 || x > bb.max.x + 5 || z < bb.min.z - 5 || z > bb.max.z + 5) return;
    const span = Math.round(Math.max(bb.max.x - bb.min.x, bb.max.z - bb.min.z));
    out.push({
      name: o.name || '(unnamed)',
      col: o.material?.color ? '#' + o.material.color.getHexString() : '?',
      span,
      yTop: +bb.max.y.toFixed(1),
      tris: o.geometry.index ? o.geometry.index.count / 3 : 0,
    });
  });
  return out.filter((o) => /1e|9|8/.test('x') || true).slice(0, 14);
}, null)));
await b.close();
