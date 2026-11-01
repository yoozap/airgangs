// WHAT IS ACTUALLY HANGING OFF THE CITY GROUP RIGHT NOW?
//
// The attribution rigs name a layer by what the mesh carries, and the layers
// that carry nothing stay anonymous -- "inst:MeshLambertMaterial, 6 calls,
// 44,002 instances" could be anything. This lists every child of the group the
// renderer walks, so the anonymous lines get a name: instance count, triangles,
// whether it is distance-culled at all (`chunk`), and whether three can
// frustum-cull it (a city-wide InstancedMesh has a bounding sphere the size of
// Georgia, so it is drawn wherever the camera looks).
//   SPOT=centre ALT=120 node tools/_probe_attached.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 24000));
await page.evaluate(([spot, alt]) => {
  const g = window.__game;
  const sp = spot === 'spawn' ? (g.world.spawn ?? { x: 0, z: 0 }) : { x: 0, z: 0 };
  if (g.foot?.active) g.foot._board();
  g.crashed = false; g.health = 1000; g.airborne = true;
  g.plane.position.set(sp.x, (g.world.heightAt(sp.x, sp.z) ?? 0) + alt, sp.z);
  g.vel.set(0, 0, 0); g.speed = 0;
  g._snapCamera?.();
}, [process.env.SPOT || 'centre', +(process.env.ALT || 120)]);
await page.waitForTimeout(9000);
console.log(await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const chunked = new Set(w.chunkMeshes.flatMap((c) => [c.mesh, c.lodMesh]).filter(Boolean));
  const rows = [];
  for (const o of w.group.children) {
    let tris = 0, meshes = 0, inst = 0;
    o.traverse((n) => {
      if (!n.isMesh && !n.isInstancedMesh) return;
      const gi = n.geometry;
      const c = gi?.index ? gi.index.count : (gi?.attributes?.position?.count ?? 0);
      const k = n.isInstancedMesh ? n.count : 1;
      tris += (c / 3) * k; meshes++; inst += k;
    });
    if (!meshes) continue;
    rows.push({
      tris, meshes, inst,
      name: o.userData?.layer || o.name || (o.isInstancedMesh ? `InstancedMesh(${o.material?.type})` : o.type),
      chunk: chunked.has(o) ? 'chunk' : '  -  ',
      cull: o.frustumCulled === false ? 'NEVER-CULLED' : '',
      shadow: o.castShadow ? 'casts' : '',
    });
  }
  rows.sort((a, b) => b.tris - a.tris);
  const tot = rows.reduce((a, b) => a + b.tris, 0);
  return `${w.group.children.length} children attached, ${(tot / 1e6).toFixed(1)}M triangles in them\n`
    + rows.slice(0, 26).map((r) => `${String(+(r.tris / 1e6).toFixed(2)).padStart(7)}M ${String(r.inst).padStart(6)} inst `
      + `${r.chunk} ${String(r.shadow).padEnd(5)} ${r.cull.padEnd(12)} ${r.name}`).join('\n');
}));
await browser.close();
