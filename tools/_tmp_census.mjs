import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/census';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 120000 });
for (const at of [6.6, 10.4]) {
  await page.waitForFunction((t) => window.__game.cinema.t >= t, at, { timeout: 60000 });
  await page.evaluate(() => { window.__game.cinema.paused = true; });
  await page.waitForTimeout(250);
  console.log(`--- t=${at}`);
  console.log(await page.evaluate(() => {
    const g = window.__game;
    window.__chunkSet = new Set(g.world.chunkMeshes.flatMap((c) => [c.mesh, c.lodMesh]));
    // tally every draw of one frame, by what the object is and whose pass it is
    const r = g.renderer, orig = r.renderBufferDirect.bind(r);
    const tally = new Map(); let n = 0;
    r.renderBufferDirect = (cam, scn, geo, mat, obj, grp) => {
      n++;
      const chunk = window.__chunkSet.has(obj) ? 'CHUNK' : 'fixed';
      const tris = Math.round((geo.index ? geo.index.count : geo.attributes.position?.count ?? 0) / 3);
      const par = obj.parent?.name || obj.parent?.type || '?';
      const key = `${chunk} ${obj.isInstancedMesh ? `inst x${obj.count}` : 'mesh'} ${mat.type}`
        + ` ${mat.name || obj.name || '-'} tris~${tris < 50 ? '<50' : tris < 500 ? '<500' : tris < 5000 ? '<5k' : '5k+'} par=${par}`;
      tally.set(key, (tally.get(key) || 0) + 1);
      return orig(cam, scn, geo, mat, obj, grp);
    };
    if (g.hq && g.composer) g.composer.render(); else r.render(g.scene, g.camera);
    r.renderBufferDirect = orig;
    const rows = [...tally].sort((a, b) => b[1] - a[1]).slice(0, 18)
      .map(([k, v]) => `${String(v).padStart(5)}  ${k}`);
    const shadow = [...tally].filter(([k]) => k.startsWith('SHADOW')).reduce((a, [, v]) => a + v, 0);
    return [`${n} draws total, ${shadow} of them shadow`, ...rows].join('\n');
  }));
  await page.evaluate(() => { window.__game.cinema.paused = false; });
}
await ctx.close();
