// WHY DO THE BUILDINGS COST 1,000 DRAW CALLS FOR 80,000 TRIANGLES?
//
// A merged building chunk is ONE mesh with a material array: a group per wall
// bucket, per roof bucket, and one more per per-building photo facade. three
// issues a draw per group, so the chunk's cost is its MATERIAL COUNT, not its
// size -- and the shadow pass pays it again. This counts them.
import { chromium } from 'playwright';
import { enterGame } from "./enter.mjs";
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 24000));
console.log(await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const out = [];
  const ms = w.buildingMeshes || [];
  let groups = 0, tris = 0, tiny = 0, tinyTris = 0;
  const hist = new Map();
  for (const m of ms) {
    const gr = m.geometry.groups;
    groups += gr.length;
    for (const q of gr) {
      const t = q.count / 3;
      tris += t;
      if (t < 200) { tiny++; tinyTris += t; }
    }
    hist.set(gr.length, (hist.get(gr.length) || 0) + 1);
  }
  out.push(`${ms.length} building chunks, ${groups} groups total (${(groups / ms.length).toFixed(1)} per chunk)`);
  out.push(`${(tris / 1e6).toFixed(2)}M triangles in them; ${tiny} groups are under 200 tris (${(tinyTris / 1e3).toFixed(0)}k tris)`);
  out.push(`groups-per-chunk histogram: ${[...hist].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join(' ')}`);
  // materials per chunk, and how many are the SAME material object reused
  const sample = ms.slice(0, 3).map((m) => {
    const mats = Array.isArray(m.material) ? m.material : [m.material];
    const uniq = new Set(mats).size;
    const names = mats.map((x) => x.name || x.type).join(',');
    return `  chunk: ${mats.length} material slots, ${uniq} distinct  [${names.slice(0, 180)}]`;
  });
  out.push('sample chunks:', ...sample);
  // how far is the spawn from the plane hall, and is the fleet shown there?
  const sp = w.spawn ?? { x: 0, z: 0 };
  const hall = g.scene.getObjectByName('planehall');
  if (hall) {
    hall.updateMatrixWorld(true);
    const e = hall.matrixWorld.elements;      // THREE is not on window here
    const hx = e[12], hz = e[14];
    out.push(`\nspawn (${sp.x.toFixed(0)}, ${sp.z.toFixed(0)}) is ${Math.hypot(sp.x - hx, sp.z - hz).toFixed(0)} m from the hall centre`);
    const fleet = g.scene.getObjectByName('planehall_fleet');
    out.push(`fleet visible at the spawn: ${fleet?.visible}; ${fleet?.children.length} aeroplanes`);
    let kits = 0, kitMeshes = 0, kitTris = 0;
    fleet?.children.forEach((p) => {
      const k = p.userData.cockpitKit;
      if (!k) return;
      kits++;
      k.traverse((o) => {
        if (!o.isMesh) return;
        kitMeshes++;
        const gi = o.geometry;
        kitTris += ((gi.index ? gi.index.count : gi.attributes.position.count) / 3);
      });
    });
    out.push(`cockpit kits in the fleet: ${kits} kits, ${kitMeshes} meshes, ${(kitTris / 1e3).toFixed(0)}k tris`);
  }
  return out.join('\n');
}));
await browser.close();
