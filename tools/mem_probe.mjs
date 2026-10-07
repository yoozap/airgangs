import { chromium } from 'playwright';
import { enterGame } from '/Users/wazzap/tbilisifly/tools/enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
await enterGame(page);
await page.waitForTimeout(9000);
const r = await page.evaluate(() => {
  const g = window.__game;
  let bytes = 0, geos = 0, verts = 0;
  const byMat = {};
  const seen = new Set();
  const walk = (o) => { if (o.geometry && !seen.has(o.geometry.uuid)) { seen.add(o.geometry.uuid); geos++;
    let b = 0;
    for (const k in o.geometry.attributes) { const a = o.geometry.attributes[k]; b += a.array.byteLength; }
    if (o.geometry.index) b += o.geometry.index.array.byteLength;
    bytes += b;
    const m = Array.isArray(o.material) ? o.material[0] : o.material;
    const key = (o.name || '') + '|' + (m ? '#' + m.color?.getHexString() + (m.map ? '+map' : '') : 'nomat');
    byMat[key] = (byMat[key] || 0) + b;
    verts += o.geometry.attributes.position?.count || 0; } };
  g.scene.traverse(walk);
  for (const c of g.world.chunkMeshes || []) { walk(c.mesh); if (c.lodMesh) walk(c.lodMesh); }
  const top = Object.entries(byMat).sort((a,b)=>b[1]-a[1]).slice(0,12).map(([k,v])=>k+' '+(v/1e6).toFixed(0)+'MB');
  return { top, geos, mb: +(bytes/1e6).toFixed(1), millionVerts: +(verts/1e6).toFixed(2),
           heapMB: +((performance.memory?.usedJSHeapSize||0)/1e6).toFixed(0),
           chunks: (g.world.chunkMeshes||[]).length };
});
console.log(JSON.stringify(r));
await browser.close();
