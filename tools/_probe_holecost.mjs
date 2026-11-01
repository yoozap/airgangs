// WHAT A THOUSAND BULLET HOLES COST. The opening map holds 1024 of them per
// airframe and nothing is dropped any more, so the question is what the
// picture pays for a machine shot to pieces: the frame is pinned on the slung
// Mustang at arm's length (the worst case -- its panels fill the screen) and
// timed clean, then after every hundred rounds.
//   node tools/_probe_holecost.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/plane holes:|ERROR|error/i.test(m.text())) console.log('  !', m.text()); });
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_swing') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(+(process.env.WARM || 6000));

await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let plane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane;
  plane.updateWorldMatrix(true, true);
  const c = plane.getWorldPosition(new T.Vector3());
  window.__eye = c.clone().add(new T.Vector3(6, -1.5, 4));
  g.renderer.setAnimationLoop(null);
  g.camera.position.copy(window.__eye);
  g.camera.lookAt(c);
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 8; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
  window.__bench = async (n) => {
    const t = [];
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      t.push(performance.now() - t0);
    }
    t.sort((a, b) => a - b);
    return +t[Math.floor(t.length / 2)].toFixed(2);
  };
  // the burst: straight into the panels in front of the lens, where every
  // hole lands on the pixels being timed
  window.__burst = (n) => {
    const plane = window.__plane;
    const rec = g.targets.list.find((r) => r.wrapper === plane);
    const parts = g.planeDamage._partsOf(rec);
    const cart = g.pistol.cart;
    const eye = window.__eye;
    let fired = 0;
    for (let k = 0; k < n; k++) {
      const lx = (Math.random() - 0.5) * (plane.userData.halfSpan ?? 5) * 1.6;
      const lz = (Math.random() - 0.5) * (plane.userData.size ?? 10) * 0.8;
      const aim = new T.Vector3(lx, (Math.random() - 0.5) * 1.5, lz).applyMatrix4(plane.matrixWorld);
      g.bullets.spawn(cart, eye, aim.sub(eye).normalize(),
        g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
      fired++;
    }
    // the loop is stopped, so step the simulation by hand until they land
    for (let i = 0; i < 90; i++) { g.bullets.update(1 / 120); g.planeDamage.update(1 / 120); }
    return fired;
  };
  window.__state = () => {
    const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
    const set = rec._holeSet;
    let tears = 0;
    for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
    const ex = rec._exits?.length ?? 0;
    const spans = [...(set?.ranges?.values() ?? [])].map((r) => r.hi.value - r.lo.value);
    const texels = spans.reduce((a, b) => a + b, 0);
    return { tears, exits: ex, lit: set?.uniforms.uTearCount.value ?? 0, texels,
      panels: spans.length, worstPanel: Math.max(0, ...spans),
      marks: (rec._marks ?? []).filter((m) => !m.dropped).length };
  };
});

const bench = async () => {
  await page.evaluate(() => window.__bench(20));
  return page.evaluate(() => window.__bench(40));
};
console.log('\nrounds  tears  exits   lit  texels  worstPanel  marks   frame');
console.log('------  -----  -----  ----  ------  ----------  -----  ------');
let fired = 0;
for (let pass = 0; pass <= 6; pass++) {
  if (pass) fired += await page.evaluate((n) => window.__burst(n), 60);
  const ms = await bench();
  const st = await page.evaluate(() => window.__state());
  console.log(`${String(fired).padStart(6)}  ${String(st.tears).padStart(5)}`
    + `  ${String(st.exits).padStart(5)}  ${String(st.lit).padStart(4)}`
    + `  ${String(st.texels).padStart(6)}  ${String(st.worstPanel).padStart(10)}`
    + `  ${String(st.marks).padStart(5)}  ${String(ms).padStart(6)} ms`);
}
await browser.close();
