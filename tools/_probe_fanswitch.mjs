// THE FAN SWITCH, the way the firing points work it: walk up to the board, a
// control card appears, and the button on it -- or B -- turns the blades.
// Also checks the cables still lie ON the slab and not in it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
page.on('console', (m) => { if (/warehouse:/.test(m.text())) console.log(' [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'fan_distro_board') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);

// stand the hero well away from the board first
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const at = (x, z) => new T.Vector3(x, 0, z).applyMatrix4(wh.matrixWorld);
  const far = at(13, 20);
  g.foot.pos.set(far.x, far.y + 0.05, far.z);
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
  return `hero parked 12 m from the board`;
}));
await page.waitForTimeout(600);
const away = await page.evaluate(() => document.getElementById('fan-ctl')?.style.display);

// now walk up to it
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const p = new T.Vector3(11.4, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(800);
console.log(await page.evaluate(([away]) => {
  const g = window.__game;
  const el = document.getElementById('fan-ctl');
  const out = [`card away from the board: display=${away || '(unset)'}`,
    `card standing at the board: display=${el?.style.display}`,
    `card reads: ${el?.textContent?.trim().replace(/\s+/g, ' ')}`];
  const rotors = [];
  g.scene.traverse((o) => { if (o.name === 'fan_rotor') rotors.push(o); });
  out.push(`${rotors.length} rotors`);
  window.__rot0 = rotors[0]?.rotation.z ?? 0;
  window.__rotors = rotors;
  return out.join('\n');
}, [away]));

// press B
await page.keyboard.down('b');
await page.waitForTimeout(60);
await page.keyboard.up('b');
await page.waitForTimeout(1800);
console.log(await page.evaluate(() => {
  const el = document.getElementById('fan-ctl');
  const d = (window.__rotors[0]?.rotation.z ?? 0) - window.__rot0;
  return `after B: card reads "${el?.querySelector('#fc-state')?.textContent}", `
    + `rotor turned ${d.toFixed(2)} rad in 1.8 s`;
}));
// and the click on the card's own button turns it off again
await page.click('#fc-pw');
await page.waitForTimeout(1200);
console.log(await page.evaluate(() => {
  const el = document.getElementById('fan-ctl');
  const w = (window.__rotors[0]?.rotation.z ?? 0);
  window.__w2 = w;
  return `after clicking the card button: "${el?.querySelector('#fc-state')?.textContent}"`;
}));
await page.waitForTimeout(1500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const d = (window.__rotors[0]?.rotation.z ?? 0) - window.__w2;
  const out = [`coasting down: ${d.toFixed(2)} rad in the last 1.5 s (should be small)`];
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const v = new T.Vector3();
  for (const n of ['fan_cables', 'crane_slings']) {
    const o = wh.children.find((c) => c.name === n)
      ?? wh.getObjectByName(n);
    if (!o) { out.push(`${n}: MISSING`); continue; }
    const b = new T.Box3();
    o.traverse((m) => {
      if (!m.isMesh) return;
      m.geometry.computeBoundingBox();
      const bb = m.geometry.boundingBox;
      const mm = new T.Matrix4().multiplyMatrices(inv, m.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        b.expandByPoint(v.applyMatrix4(mm));
      }
    });
    out.push(`${n}: y ${b.min.y.toFixed(3)}..${b.max.y.toFixed(3)}`
      + (n === 'fan_cables' ? (b.min.y < -0.001 ? '  *** BELOW THE SLAB ***' : '  on the slab') : ''));
  }
  const p = g.targets?.list?.find((r) => r.wrapper?.userData?.planeId === 'p51');
  out.push(`slung P-51 present: ${!!p}`);
  return out.join('\n');
}));
await browser.close();
