// Look at the retargeted library on a real body: tools/pages/moves.html, one
// PNG per phase so a cycle can be read as a strip.
//   node tools/_shot_moves.mjs [body] [clip,clip,...]
import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const body = process.argv[2] || 'banana';
const clips = process.argv[3] || '';
const phases = (process.env.PHASES || '0.0,0.25,0.5,0.75').split(',');
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 620 }, deviceScaleFactor: 2 });
page.on('console', (m) => { if (m.type() === 'error') console.log('  console:', m.text()); });
if (process.argv.includes('--audit')) {
  const bodies = (process.env.BODIES || body).split(',');
  await page.goto(`http://localhost:5173/tools/pages/moves.html?audit=1&bodies=${bodies.join(',')}`);
  const r = await page.waitForFunction(() => window.__ready, null, { timeout: 300000 })
    .then((h) => h.jsonValue());
  for (const a of r.audit) {
    console.log(`${a.take ? 'takes ' : '  --  '} ${a.id.padEnd(14)} ${String(a.clips).padStart(2)} clips`
      + `${a.ms ? `  ${a.ms} ms` : ''}${a.err ? `  ${a.err}` : ''}`);
  }
  console.log(`${r.audit.filter((a) => a.take).length}/${r.audit.length} bodies take the library`);
  await browser.close();
  process.exit(0);
}
for (const ph of phases) {
  const url = `http://localhost:5173/tools/pages/moves.html?body=${body}`
    + `&phase=${ph}${clips ? `&clips=${clips}` : ''}`;
  await page.goto(url);
  const r = await page.waitForFunction(() => window.__ready, null, { timeout: 60000 })
    .then((h) => h.jsonValue());
  if (r.error) { console.log('ERROR', r.error); break; }
  const f = `${OUT}/moves_${body}_${ph}.png`;
  await page.screenshot({ path: f });
  console.log(`${f}  retarget=${r.take}  ${r.clips.length} clips`);
}
await browser.close();
