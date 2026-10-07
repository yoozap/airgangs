// Did the propeller detector actually find a propeller on every aeroplane
// that has one?   node tools/_probe_props.mjs [id ...]
import { chromium } from 'playwright';

const only = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
page.on('console', (m) => { if (/propeller|reject|detach|welded/.test(m.text())) console.log('  ·', m.text()); });
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto((process.env.GAME_URL || 'http://localhost:5173/') + '?props=debug');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 60000 });

const rows = await page.evaluate(async (only) => {
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  const list = PLANES.filter((p) => p.prop && (!only.length || only.includes(p.id)));
  for (const def of list) {
    try {
      const t0 = performance.now();
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      const props = plane.userData.propellers ?? [];
      out.push({
        id: def.id, want: def.props ?? 1, got: props.length,
        ms: Math.round(performance.now() - t0),
        span: +(plane.userData.halfSpan * 2).toFixed(1),
        detail: props.map((p) => ({
          r: +(p.userData.radius ?? 0).toFixed(2),
          split: p.name === 'propeller_split',
          axis: [p.userData.spinAxis?.x, p.userData.spinAxis?.y, p.userData.spinAxis?.z]
            .map((v) => +(v ?? 0).toFixed(2)),
        })),
      });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
}, only);

console.log('\nplane        span   want got   ms   propellers (radius, how it was found, spin axis)');
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  const flag = r.got < r.want ? '   <-- MISSING' : '';
  const d = r.detail.map((p) => `${p.r}m ${p.split ? 'cut' : 'named'} [${p.axis}]`).join('  ');
  console.log(`${r.id.padEnd(12)} ${String(r.span).padStart(5)}  ${r.want}   ${r.got}  ${String(r.ms).padStart(5)}   ${d}${flag}`);
}
await browser.close();
