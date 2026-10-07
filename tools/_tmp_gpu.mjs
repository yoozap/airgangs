import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/reelperf';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 90000 });
for (const at of [8.2, 10.5]) {
  await page.waitForFunction((t) => window.__game.cinema.t >= t, at, { timeout: 90000 });
  await page.evaluate(() => { window.__game.cinema.paused = true; });
  await page.waitForTimeout(400);
  console.log(`--- t=${at}`);
  console.log(await page.evaluate(() => {
    const g = window.__game, cm = g.world;
    const gl = g.renderer.getContext();
    const draw = () => (g.composer && g._noPost !== true ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const bench = (label, setup, restore) => {
      setup?.();
      for (let i = 0; i < 3; i++) draw();
      gl.finish();
      const N = 8; const t0 = performance.now();
      for (let i = 0; i < N; i++) { draw(); gl.finish(); }   // finish() = real GPU time
      const ms = (performance.now() - t0) / N;
      const info = g.renderer.info.render;
      const r = `${label.padEnd(26)} ${ms.toFixed(1).padStart(7)} ms  draws ${String(info.calls).padStart(4)}  ${(info.triangles / 1e6).toFixed(1)}M tris`;
      restore?.();
      return r;
    };
    const out = [];
    // census of what is actually attached right now
    let hi = 0, lo = 0, hiTris = 0, loTris = 0;
    const fam = {};
    for (const c of cm.chunkMeshes) {
      if (c.hiOn) { hi++; const t = (c.mesh.geometry.index ? c.mesh.geometry.index.count : c.mesh.geometry.attributes.position.count) / 3 * (c.mesh.count || 1); hiTris += t;
        const k = c.mesh.name || c.mesh.material?.name || c.mesh.type; fam[k] = (fam[k] || 0) + t; }
      if (c.loOn) { lo++; loTris += (c.lodMesh.geometry.index ? c.lodMesh.geometry.index.count : c.lodMesh.geometry.attributes.position.count) / 3 * (c.lodMesh.count || 1); }
    }
    out.push(`attached hi=${hi} (${(hiTris/1e6).toFixed(1)}M tris)  lo=${lo} (${(loTris/1e6).toFixed(1)}M)`);
    out.push('top families: ' + Object.entries(fam).sort((a,b)=>b[1]-a[1]).slice(0,8)
      .map(([k,v])=>`${k || '?'}=${(v/1e6).toFixed(1)}M`).join('  '));
    out.push(bench('as shipped'));
    out.push(bench('no post', () => { g._noPost = true; }, () => { g._noPost = false; }));
    out.push(bench('no shadows', () => { g.renderer.shadowMap.enabled = false; },
      () => { g.renderer.shadowMap.enabled = true; }));
    out.push(bench('no shadows + no post', () => { g.renderer.shadowMap.enabled = false; g._noPost = true; },
      () => { g.renderer.shadowMap.enabled = true; g._noPost = false; }));
    // force every lod-capable chunk to its imposter
    const swapped = [];
    out.push(bench('imposters forced', () => {
      for (const c of cm.chunkMeshes) {
        if (!c.lodD2 || !c.hiOn) continue;
        swapped.push(c); cm.group.remove(c.mesh); c.hiOn = false;
        if (c.lodMesh) { cm.group.add(c.lodMesh); c.loOn = true; }
      }
    }, () => {
      for (const c of swapped) { cm.group.add(c.mesh); c.hiOn = true; if (c.lodMesh) { cm.group.remove(c.lodMesh); c.loOn = false; } }
      swapped.length = 0;
    }));
    return out.join('\n');
  }));
  await page.evaluate(() => { window.__game.cinema.paused = false; });
}
await ctx.close();
