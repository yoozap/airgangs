import { chromium } from 'playwright';
const b = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await p.goto(`http://localhost:5173/_runview.html?body=${process.env.BODY || 'astronaut'}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });
for (const [label, s, st, l] of [['sprint 9 m/s, clip only', 9, 0, 0], ['sprint 9 m/s, with layer', 9, 0, 1], ['walk 1.9 m/s, clip only', 1.9, 0, 0]]) {
  await p.evaluate(([s, st, l]) => window.__set({ speed: s, stroll: st, layer: l }), [s, st, l]);
  await p.evaluate(() => window.__step(180));
  console.log(label, JSON.stringify(await p.evaluate(() => window.__body(60)), null, 1).replace(/\n\s*/g, ' '));
}
await b.close();
