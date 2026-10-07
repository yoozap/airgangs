// Is the firing stall shader COMPILATION? Counts GL programs across the first
// burst and times every compile.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_firstshot.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game;
  const gl = g.renderer.getContext();
  window.__c = { compiles: [], links: [] };
  const lp = gl.linkProgram.bind(gl);
  gl.linkProgram = (p) => { const t = performance.now(); lp(p); window.__c.links.push(+(performance.now() - t).toFixed(1)); };
  const cs = gl.compileShader.bind(gl);
  gl.compileShader = (s) => { const t = performance.now(); cs(s); window.__c.compiles.push(+(performance.now() - t).toFixed(1)); };
  // getProgramParameter(LINK_STATUS) is where ANGLE actually blocks on the
  // deferred compile, so time that too
  const gpp = gl.getProgramParameter.bind(gl);
  gl.getProgramParameter = (p, k) => { const t = performance.now(); const r = gpp(p, k); const ms = performance.now() - t; if (ms > 1) window.__c.links.push(+ms.toFixed(1)); return r; };
  window.__progs = () => g.renderer.info.programs.length;
});

const snap = async (label) => {
  const r = await page.evaluate(() => ({ progs: window.__progs(), compiles: window.__c.compiles.length, links: window.__c.links.length,
    linkMs: +window.__c.links.reduce((a, b) => a + b, 0).toFixed(0), compMs: +window.__c.compiles.reduce((a, b) => a + b, 0).toFixed(0) }));
  console.log(`${label.padEnd(26)} programs ${String(r.progs).padStart(4)}   shader compiles ${String(r.compiles).padStart(4)} (${r.compMs} ms)   link/status waits ${String(r.links).padStart(4)} (${r.linkMs} ms)`);
  return r;
};

await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 220, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.42, 0, 0);
});
await page.waitForTimeout(5000);
await snap('before firing');
await page.keyboard.down('Space');
await page.waitForTimeout(600);
await page.keyboard.up('Space');
await page.waitForTimeout(2500);
await snap('after a 0.6 s burst');
await page.keyboard.down('Space');
await page.waitForTimeout(2500);
await page.keyboard.up('Space');
await page.waitForTimeout(2500);
await snap('after a 2.5 s burst');
// and the rockets, which have their own shaders
await page.keyboard.press('Digit2');
await page.waitForTimeout(300);
await page.keyboard.down('Space');
await page.waitForTimeout(1200);
await page.keyboard.up('Space');
await page.waitForTimeout(3000);
await snap('after rockets');
await browser.close();
