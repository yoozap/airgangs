import { chromium } from 'playwright';

// Fast text-only probe of the blast-hole plumbing: are the original facade
// quads really degenerated, is the shell in the scene, did cells drop?
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1500);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000);

const out = await page.evaluate(() => {
  const g = window.__game;
  // nearest intact mid-rise to the map origin
  let best = null;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      const h = r.top - r.base;
      if (r.hp < r.maxHp || h < 16 || h > 45) continue;
      const d = Math.hypot(r.cx, r.cz);
      if (!best || d < best.d) best = { m, r, d };
    }
  }
  if (!best) return { error: 'no building' };
  const { m, r } = best;
  const idx = m.geometry.index.array;
  const before = [idx[r.wIdx0], idx[r.wIdx0 + 1], idx[r.wIdx0 + 2]];
  const n = Math.floor((r.v1 - r.v0) / 3);
  // where do this building's wall triangles ACTUALLY sit?
  let found = -1;
  for (let i = 0; i + 2 < idx.length; i += 3) {
    if (idx[i] === r.v0 && idx[i + 1] === r.v0 + 1 && idx[i + 2] === r.v0 + n + 1) { found = i; break; }
  }
  const groups = m.geometry.groups.map((gr) => [gr.start, gr.count, gr.materialIndex]);
  const grpOf = (pos) => groups.findIndex((gr) => pos >= gr[0] && pos < gr[0] + gr[1]);
  // every triangle that touches this building, and where it lives
  const runs = [];
  for (let i = 0; i + 2 < idx.length; i += 3) {
    const a = idx[i];
    if (a < r.v0 || a >= r.v1) continue;
    const last = runs[runs.length - 1];
    if (last && last[1] + 3 === i) last[1] = i;
    else runs.push([i, i]);
  }
  const runInfo = runs.map(([s, e]) => ({ s, e: e + 3, tris: (e - s) / 3 + 1, grp: groups[grpOf(s)] }));
  r.chunk = m;
  // first punch builds the shell; then aim a real one at an actual wall cell
  g.world.punchHole(r, { x: r.cx, y: r.base + (r.top - r.base) * 0.5, z: r.cz }, 0.1, false);
  const target = r.shell?.cells[Math.floor(r.shell.cells.length * 0.4)];
  const hole = target ? g.world.punchHole(r, target, 5, false) : null;
  const wallIdxNow = [idx[35178], idx[35179], idx[35180]];
  const after = [idx[r.wIdx0], idx[r.wIdx0 + 1], idx[r.wIdx0 + 2]];
  const sh = r.shell;
  return {
    bi: r.bi, n, v0: r.v0, v1: r.v1, wallMat: r.wallMat, wIdx0: r.wIdx0, wIdx1: r.wIdx1,
    expectFirst: [r.v0, r.v0 + 1, r.v0 + n + 1],
    before, after,
    foundAt: found,
    runs: runInfo,
    wallIdxNow,
    triRange: m.userData.triRanges?.get(r.bi),
    roofRange: [r.rIdx0, r.rIdx1],
    foundGroup: found >= 0 ? groups[grpOf(found)] : null,
    wIdxGroup: groups[grpOf(r.wIdx0)] ?? null,
    matCount: Array.isArray(m.material) ? m.material.length : 1,
    groupCount: groups.length,
    idxLen: idx.length,
    hole: !!hole,
    shell: sh ? {
      cells: sh.cells.length, live: sh.live,
      inScene: !!sh.mesh.parent, visible: sh.mesh.visible,
      verts: sh.count, matName: sh.mesh.material?.uuid === (Array.isArray(m.material) ? m.material[r.wallMat]?.uuid : null),
      bbox: sh.mesh.geometry.boundingBox
        ? [sh.mesh.geometry.boundingBox.min.toArray().map((v) => +v.toFixed(1)),
          sh.mesh.geometry.boundingBox.max.toArray().map((v) => +v.toFixed(1))]
        : null,
      holeCenter: sh.holes[0] ? [sh.holes[0].x.toFixed(1), sh.holes[0].y.toFixed(1), sh.holes[0].z.toFixed(1)] : null,
      firstCell: sh.cells[0] ? [sh.cells[0].x.toFixed(1), sh.cells[0].y.toFixed(1), sh.cells[0].z.toFixed(1)] : null,
    } : null,
    recBase: r.base, recTop: r.top, cx: +r.cx.toFixed(1), cz: +r.cz.toFixed(1),
  };
});
console.log(JSON.stringify(out, null, 1));
await browser.close();
