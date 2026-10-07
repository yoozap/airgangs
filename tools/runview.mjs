// Shoot the treadmill: frames across one stride plus the elbow numbers.
import { chromium } from 'playwright';
import fs from 'fs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/effacba5-9c0d-43bd-ac9c-0565a77831d7/scratchpad';
const body = process.env.BODY || 'astronaut';
const speed = +(process.env.SPEED || 9);
const stroll = +(process.env.STROLL || 0);
const layer = process.env.LAYER === '0' ? 0 : 1;
const tag = process.env.TAG || 'run';
const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });
await p.evaluate(([s, st, l, d, a, v]) => window.__set({ speed: s, stroll: st, layer: l, view: v, dist: d, aim: a }),
  [speed, stroll, layer, +(process.env.DIST || 4.2), +(process.env.AIM || 0.95), +(process.env.VIEW || 90)]);
if (process.env.GRIP !== undefined) await p.evaluate((g) => window.__set({ grip: g }), +process.env.GRIP);
if (process.env.FREEZE) await p.evaluate(() => { rigFreeze = true; });
await p.evaluate(() => window.__step(240));            // settle the springs
const rows = [];
for (let i = 0; i < 6; i++) {
  await p.evaluate(() => window.__step(6));            // ~0.1 s of stride
  rows.push(await p.evaluate(() => window.__elbow()));
  fs.writeFileSync(`${OUT}/${tag}${i}.png`, await p.locator('canvas').screenshot());
}
console.log(`body=${body} speed=${speed} stroll=${stroll} layer=${layer}`);
for (const r of rows) console.log(JSON.stringify(r));
await b.close();
