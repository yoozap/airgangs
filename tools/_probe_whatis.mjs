// WHO OWNS A DRAW CALL?
//
// _probe_drawcalls names the layer by what the mesh carries, which is enough
// to rank it and not enough to go and fix it: 13 names x 144 copies of
// "Cube036_*" were 1,872 of the 4,652 calls at the spawn and nothing in the
// source or any GLB mentions that name. This walks up from the drawn object to
// whatever group put it in the world, and prints the ancestry with positions.
//   NAME=Cube036 node tools/_probe_whatis.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 24000));

console.log(await page.evaluate((needle) => {
  const g = window.__game;
  const out = [];
  const hits = [];
  g.scene.traverse((o) => { if (o.name?.startsWith(needle)) hits.push(o); });
  out.push(`${hits.length} objects named ${needle}* under the scene`);
  const chain = (o) => {
    const names = [];
    for (let p = o; p; p = p.parent) names.push(`${p.name || p.type}${p.isInstancedMesh ? `[x${p.count}]` : ''}`);
    return names.reverse().join(' > ');
  };
  const byChain = new Map();
  for (const h of hits) {
    const k = chain(h);
    byChain.set(k, (byChain.get(k) || 0) + 1);
  }
  for (const [k, n] of [...byChain].sort((a, b) => b[1] - a[1]).slice(0, 8)) out.push(`  x${n}  ${k}`);
  if (hits[0]) {
    const w = new (hits[0].position.constructor)();
    hits[0].getWorldPosition(w);
    out.push(`first at world ${w.x.toFixed(0)}, ${w.y.toFixed(0)}, ${w.z.toFixed(0)}`);
    // how many siblings does one copy have? that is the per-copy draw count
    out.push(`its parent has ${hits[0].parent?.children?.length} children`);
  }
  // ...and a roll-up of the whole scene by top-level group, so the next
  // question ("which system is this?") does not need another run.
  out.push('\n-- scene roots: meshes / Mtris (whole subtree, drawn or not) --');
  for (const root of g.scene.children) {
    let meshes = 0, tris = 0;
    root.traverse((o) => {
      if (!o.isMesh && !o.isInstancedMesh) return;
      meshes++;
      const gi = o.geometry;
      const c = gi?.index ? gi.index.count : (gi?.attributes?.position?.count ?? 0);
      tris += (c / 3) * (o.isInstancedMesh ? o.count : 1);
    });
    if (meshes) out.push(`  ${String(meshes).padStart(6)} meshes ${String(+(tris / 1e6).toFixed(2)).padStart(8)}M  ${root.name || root.type}`);
  }
  return out.join('\n');
}, process.env.NAME || 'Cube036'));
await browser.close();
