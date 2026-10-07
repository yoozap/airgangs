import { chromium } from 'playwright';
import fs from 'node:fs';
const out = process.argv[2] || '/tmp/wl.png';
const url = process.argv[3] || 'http://localhost:4200/en/destiny-matrix';
const w = +(process.argv[4] || 1280), h = +(process.argv[5] || 860);
const b = await chromium.launch();
const p = await b.newPage({ viewport: { width: w, height: h } });
p.on('pageerror', (e) => console.log('ERR', e.message.slice(0, 160)));
await p.goto(url, { waitUntil: 'networkidle', timeout: 90000 }).catch((e) => console.log('goto', e.message.slice(0, 120)));
await p.waitForTimeout(2500);
// hide cookie banners / overlays if any
await p.screenshot({ path: out });
const box = await p.evaluate(() => {
  const q = (s) => document.querySelector(s)?.getBoundingClientRect();
  const r = (b) => b ? [Math.round(b.x), Math.round(b.y), Math.round(b.width), Math.round(b.height)] : null;
  return { form: r(q('app-birth-data-form')), btn: r(q('.submit-btn')), container: r(q('.witch-action-container')), left: r(q('.hero-left-column')) };
});
console.log(JSON.stringify(box));
await b.close();
