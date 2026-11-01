// WHERE DO THE 8,925 DRAW CALLS GO?
//
// perf_layers ranks by triangles, which is the right question over the city
// centre (147M tris) and the WRONG one at the airport spawn, where the frame
// draws 8,925 calls for only 49M triangles -- 5k tris a call is a submission
// problem, not a geometry one. This ranks by CALLS, and splits the shadow pass
// off from the colour pass (three swaps in its own depth material there, so a
// layer that is cheap on screen can still be paying twice).
//   SPOT=spawn|centre ALT=60 node tools/_probe_drawcalls.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

const SPOTS = (process.env.SPOTS || 'spawn@60,centre@120,centre@40').split(',');

const attribute = () => page.evaluate(async () => {
  const g = window.__game;
  const r = g.renderer;
  const by = new Map();
  const orig = r.renderBufferDirect.bind(r);
  const label = (o, material) => {
    const u = o.userData || {};
    if (u.layer) return `${u.layer}${o.isInstancedMesh ? '' : ' (mesh)'}`;
    if (u.kind) return `kind:${u.kind}`;
    if (u.buildings) return 'buildings (merged chunk)';
    if (u.imposter) return 'imposter';
    if (o.name && o.name.length < 36) return o.name;
    if (o.isInstancedMesh) return `inst:${material?.name || material?.type}`;
    return `mat:${material?.name || material?.type || o.type}`;
  };
  // three substitutes its own depth material inside the shadow pass, so the
  // material type names the pass without having to hook the shadow map.
  const isShadow = (m) => !!m && (m.isMeshDepthMaterial || m.isMeshDistanceMaterial);
  r.renderBufferDirect = (camera, scene, geometry, material, object, group) => {
    const idx = geometry.index;
    const count = (group ? group.count : (idx ? idx.count : geometry.attributes.position?.count)) || 0;
    const inst = object.isInstancedMesh ? object.count : 1;
    const k = (isShadow(material) ? 'SHADOW ' : '') + label(object, material);
    const e = by.get(k) || { tris: 0, calls: 0, inst: 0 };
    e.tris += (count / 3) * inst; e.calls++; e.inst += inst;
    by.set(k, e);
    return orig(camera, scene, geometry, material, object, group);
  };
  await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
  by.clear();                       // discard the warm frame
  await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
  const rows = [...by.entries()];
  r.renderBufferDirect = orig;
  const calls = rows.reduce((a, b) => a + b[1].calls, 0);
  const tris = rows.reduce((a, b) => a + b[1].tris, 0);
  const fmt = ([k, v]) => `${String(v.calls).padStart(5)} calls ${String(+(v.tris / 1e6).toFixed(2)).padStart(7)}M ${String(v.inst).padStart(6)} inst  ${k}`;
  return {
    totalCalls: calls, totalMtris: +(tris / 1e6).toFixed(1),
    shadowCalls: rows.filter((r) => r[0].startsWith('SHADOW')).reduce((a, b) => a + b[1].calls, 0),
    byCalls: rows.slice().sort((a, b) => b[1].calls - a[1].calls).slice(0, 18).map(fmt),
    byTris: rows.slice().sort((a, b) => b[1].tris - a[1].tris).slice(0, 12).map(fmt),
  };
});

for (const spec of SPOTS) {
  const [spot, altS] = spec.split('@');
  const alt = +(altS || 60);
  await page.evaluate(([spot, alt]) => {
    const g = window.__game;
    const sp = spot === 'spawn' ? (g.world.spawn ?? { x: 0, z: 0 })
      : spot === 'oldtown' ? { x: 600, z: -400 } : { x: 0, z: 0 };
    if (g.foot?.active) g.foot._board();
    g.crashed = false; g.health = 1000; g.airborne = true;
    g.plane.position.set(sp.x, (g.world.heightAt(sp.x, sp.z) ?? 0) + alt, sp.z);
    g.vel.set(0, 0, 0); g.speed = 0;
    g._snapCamera?.();
  }, [spot, alt]);
  await page.waitForTimeout(9000);
  const out = await attribute();
  console.log(`\n======== ${spot} @ ${alt} m — ${out.totalCalls} calls (${out.shadowCalls} of them shadow), ${out.totalMtris}M tris ========`);
  console.log('-- by DRAW CALLS --\n' + out.byCalls.join('\n'));
  console.log('-- by TRIANGLES --\n' + out.byTris.join('\n'));
}
await browser.close();
