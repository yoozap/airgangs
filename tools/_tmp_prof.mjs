import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/reelperf4';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { const t = m.text(); if (/cinema|warm/.test(t)) console.log('[log]', t); });
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 120000 });
await page.waitForFunction(() => window.__game.cinema.t >= 6.4, null, { timeout: 60000 });
const cdp = await page.context().newCDPSession(page);
await cdp.send('Profiler.enable');
await cdp.send('Profiler.setSamplingInterval', { interval: 200 });
await cdp.send('Profiler.start');
await page.waitForTimeout(4000);
const { profile } = await cdp.send('Profiler.stop');
// self time per node, from the sample stream
const byId = new Map(profile.nodes.map((n) => [n.id, n]));
const self = new Map();
const deltas = profile.timeDeltas || [];
profile.samples.forEach((id, i) => {
  const n = byId.get(id); if (!n) return;
  const f = n.callFrame;
  const key = `${f.functionName || '(anon)'}  ${(f.url || '').split('/').pop()}:${f.lineNumber + 1}`;
  self.set(key, (self.get(key) || 0) + (deltas[i] || 0) / 1000);
});
const total = [...self.values()].reduce((a, b) => a + b, 0);
console.log(`--- CPU profile over the city, ${total.toFixed(0)} ms sampled`);
for (const [k, v] of [...self].sort((a, b) => b[1] - a[1]).slice(0, 22))
  console.log(`${(v / total * 100).toFixed(1).padStart(5)}%  ${v.toFixed(0).padStart(5)} ms  ${k}`);
console.log(await page.evaluate(() => {
  const g = window.__game, i = g.renderer.info;
  return `attached hi=${g.world.chunkMeshes.filter((c) => c.hiOn).length} `
    + `lo=${g.world.chunkMeshes.filter((c) => c.loOn).length}  geo=${i.memory.geometries} `
    + `tex=${i.memory.textures} progs=${i.programs.length} sceneKids=${g.scene.children.length} `
    + `groupKids=${g.world.group.children.length}`;
}));
await ctx.close();
