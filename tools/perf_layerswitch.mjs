// WHY IS THE CENTRE 3.5x THE COST OF OLD TOWN ON THE SAME DRAWS AND TRIANGLES?
//
// 1,546 draws / 24.2M triangles over the centre renders in 17.6 ms; 1,370 /
// 20.0M over old town renders in 5.0. Whatever the difference is, it is not
// submission and it is not geometry, so this switches each layer off in turn
// on a PINNED frame and benches what is left. The layer names come from the
// tag _placeChunkedInstanced stamps on every chunk it makes.
//
// Only `.visible` is touched -- never a light, and never shadowMap.enabled,
// either of which recompiles every material in the scene and poisons the rest
// of the run.
//   SPOT=centre ALT=120 node tools/perf_layerswitch.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 28000));

await page.evaluate(async ([spot, alt]) => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  const at = spot === 'oldtown' ? { x: 600, z: -400 } : { x: 0, z: 0 };
  windMod.wind.time = 123.456; windMod.updateWind(0);
  g.camera.position.set(at.x, (w.heightAt(at.x, at.z) ?? 0) + alt, at.z);
  g.camera.lookAt(at.x + 600, (w.heightAt(at.x + 600, at.z + 400) ?? 0) + 10, at.z + 400);
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 8; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }

  // Everything the renderer will walk, bucketed by what made it.
  window.__buckets = () => {
    const m = new Map();
    const put = (k, o) => { (m.get(k) ?? m.set(k, []).get(k)).push(o); };
    for (const o of w.group.children) {
      const tag = o.userData?.layer;
      if (tag) put(tag.split('<')[1] || tag.split('<')[0], o);
      else if (o.userData?.buildings) put('buildings (merged chunks)', o);
      else put(`other: ${o.name || o.type}`, o);
    }
    // ...plus the things that are not the city at all
    return m;
  };
  window.__bench = async (n) => {
    const info = g.renderer.info;
    const t = [];
    let calls = 0, tris = 0;
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      info.autoReset = false; info.reset();
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      t.push(performance.now() - t0);
      calls += info.render.calls; tris += info.render.triangles;
    }
    info.autoReset = true;
    t.sort((a, b) => a - b);
    return { ms: +t[Math.floor(t.length / 2)].toFixed(2), draws: Math.round(calls / n), tris: +(tris / n / 1e6).toFixed(2) };
  };
}, [process.env.SPOT || 'centre', +(process.env.ALT || 120)]);

const names = await page.evaluate(() => [...window.__buckets()]
  .map(([k, v]) => [k, v.length]).sort((a, b) => b[1] - a[1]));
console.log(`\n${names.length} layers attached:`, names.slice(0, 12).map(([k, n]) => `${k}(${n})`).join(' '));

// THROW THE FIRST ONE AWAY. Moving the camera makes materials visible that
// have never been drawn from here, and the first frames compile their
// programs: the first bench at the centre read 17 ms and every bench after it
// read 4, which made each layer in turn look like it cost 13 ms.
await page.evaluate(() => window.__bench(30));
const base = await page.evaluate(() => window.__bench(40));
const base2 = await page.evaluate(() => window.__bench(40));
console.log(`baseline repeatability: ${base.ms} vs ${base2.ms} ms`);
console.log(`\nbaseline  ${String(base.ms).padStart(6)} ms  ${String(base.draws).padStart(5)} draws  ${base.tris}M tris\n`);
for (const [name] of names.slice(0, 14)) {
  const r = await page.evaluate(async (name) => {
    const hidden = [];
    for (const [k, list] of window.__buckets()) {
      if (k !== name) continue;
      for (const o of list) if (o.visible) { o.visible = false; hidden.push(o); }
    }
    await window.__bench(8);                 // let the hidden set settle
    const out = await window.__bench(30);
    for (const o of hidden) o.visible = true;
    return { ...out, n: hidden.length };
  }, name);
  const saved = base.ms - r.ms;
  console.log(`without ${name.padEnd(28)} ${String(r.ms).padStart(6)} ms  `
    + `(${saved >= 0 ? '-' : '+'}${Math.abs(saved).toFixed(1)} ms)  ${String(r.draws).padStart(5)} draws  `
    + `${String(r.tris).padStart(6)}M  [${r.n} objects]`);
}
await browser.close();
