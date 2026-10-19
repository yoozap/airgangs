// Did the maps and the weathering actually survive into the running game?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForTimeout(26000);
console.log(await page.evaluate(() => {
  const g = window.__game;
  const kit = g.world._panelKit;
  if (!kit) return 'NO KIT';
  const out = [];
  const e = kit.get(5) ?? [...kit.values()][0];
  const attrs = Object.keys(e.geo.attributes);
  out.push(`geometry attributes: ${attrs.join(' ')}  groups=${e.geo.groups.length}`);
  const col = e.geo.attributes.color;
  if (col) {
    let lo = 9, hi = -9, sum = 0;
    for (let i = 0; i < col.count; i++) { const v = col.getX(i); lo = Math.min(lo, v); hi = Math.max(hi, v); sum += v; }
    out.push(`COLOR_0: n=${col.count} min=${lo.toFixed(3)} max=${hi.toFixed(3)} mean=${(sum / col.count).toFixed(3)}`);
  }
  for (const m of e.mats) {
    out.push(`${m.name}: map=${m.map ? m.map.image?.width + 'px' : 'none'}`
      + ` normalMap=${m.normalMap ? 'yes' : 'no'} vertexColors=${m.vertexColors}`
      + ` color=#${m.color.getHexString()} rough=${m.roughness.toFixed(2)}`);
  }
  return out.join('\n');
}));
await browser.close();
