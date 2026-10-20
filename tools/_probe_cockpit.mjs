// Where does the pilot sit, and how much does each airframe have to grow
// before a 1.8 m personage fits in it?
//   node tools/_probe_cockpit.mjs [id ...]
//
// Prints what findCockpit() in src/planes.js measured on every plane: the
// cockpit it found, the head room it has, the growth it asks for, and the
// span before and after. Read it like tools/build_planes.py's `target` table —
// anything that reads wrong gets a `cockpit:` override in the plane's def.
import { chromium } from 'playwright';

const only = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });

const rows = await page.evaluate(async (only) => {
  const { PLANES, loadPlane, makeGameplayPlane, findCockpit } = await import('/src/planes.js');
  const out = [];
  for (const def of (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES)) {
    try {
      // before: the airframe as the model ships it, cockpit unmeasured
      const raw = makeGameplayPlane(await loadPlane(def.id), { ...def, cockpit: null });
      const rb = new (raw.constructor)(); // placeholder, boxes come from userData
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      const c = plane.userData.cockpit;
      out.push({
        id: def.id,
        span0: +(raw.userData.halfSpan * 2).toFixed(2),
        span1: +(plane.userData.halfSpan * 2).toFixed(2),
        grow: c ? +c.grow.toFixed(2) : 1,
        room: c ? +(c.room / (c.grow || 1)).toFixed(2) : null,
        seat: c ? [+c.seat.x.toFixed(2), +c.seat.y.toFixed(2), +c.seat.z.toFixed(2)] : null,
        from: c?.from ?? '-',
      });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
}, only);

console.log('plane        span       ->  grown      x     cockpit room   seat y/z        found in');
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  const flag = r.grow >= 2.3 ? '  <-- CHECK' : '';
  console.log(`${r.id.padEnd(12)} ${String(r.span0).padStart(6)} m -> ${String(r.span1).padStart(6)} m`
    + `  ${('x' + r.grow.toFixed(2)).padStart(6)}   ${String(r.room ?? '-').padStart(5)} m`
    + `   x${String(r.seat?.[0] ?? '-').padStart(5)} ${String(r.seat?.[1] ?? '-').padStart(6)} ${String(r.seat?.[2] ?? '-').padStart(6)}   ${r.from}${flag}`);
}
await browser.close();
