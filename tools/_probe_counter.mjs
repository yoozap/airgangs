import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse: 2 shooting/.test(m.text())) console.log(' [page]', m.text()); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
console.log(JSON.stringify(await page.evaluate(async () => {
  const T = window.__THREE;
  const { GLTFLoader } = await import('/node_modules/three/examples/jsm/loaders/GLTFLoader.js');
  const s = (await new GLTFLoader().loadAsync('models/shooting_range.glb')).scene;
  s.updateMatrixWorld(true);
  let c = null;
  s.traverse((o) => { if (o.isMesh && (o.name||'').toLowerCase().replace(/[^a-z0-9]/g,'').includes('rangedividershapecounter')) c = o; });
  if (!c) return { found: false };
  const pos = c.geometry.attributes.position;
  const v = new T.Vector3(); const xs = [];
  for (let i = 0; i < pos.count; i++) xs.push(v.fromBufferAttribute(pos, i).applyMatrix4(c.matrixWorld).x);
  xs.sort((a, b) => a - b);
  const gaps = [];
  for (let i = 1; i < xs.length; i++) if (xs[i] - xs[i-1] > 0.25) gaps.push(+(xs[i]-xs[i-1]).toFixed(2));
  const b = new T.Box3().setFromObject(c);
  return { found: true, verts: pos.count, gaps: gaps.length, gapSizes: gaps.slice(0, 12),
    box: { min: b.min.toArray().map(v => +v.toFixed(2)), max: b.max.toArray().map(v => +v.toFixed(2)) } };
}), null, 1));
await ctx.close();
