import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
console.log(JSON.stringify(await page.evaluate(async () => {
  const { GLTFLoader } = await import('/node_modules/three/examples/jsm/loaders/GLTFLoader.js');
  const s = (await new GLTFLoader().loadAsync('models/shooting_range.glb')).scene;
  const names = [];
  s.traverse((o) => { if (o.isMesh) names.push(o.name); });
  return { total: names.length, sample: names.slice(0, 6),
    divider: names.filter((n) => /divider/i.test(n) && !/door/i.test(n)).slice(0, 6),
    counterish: names.filter((n) => /counter/i.test(n)).slice(0, 6),
    paper: names.filter((n) => /paper/i.test(n)).slice(0, 4) };
}), null, 1));
await ctx.close();
