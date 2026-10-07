// WHERE DO THE MILLISECONDS GO? Wraps every subsystem the game tick calls and
// reports mean/p95 ms per frame, so optimisation lands on the real cost.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_systems.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { const t = m.text(); if (/load |phase|ERR/i.test(t)) console.log('  [page]', t.slice(0, 160)); });
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

const report = await page.evaluate(async () => {
  const g = window.__game;
  const S = {};                     // name -> samples[]
  const tally = (name, ms) => { (S[name] ||= []).push(ms); };
  // wrap obj.method, attributing the time to `name`
  const wrap = (obj, method, name) => {
    if (!obj || typeof obj[method] !== 'function' || obj['__w_' + method]) return;
    const fn = obj[method].bind(obj);
    obj['__w_' + method] = true;
    obj[method] = (...a) => { const t = performance.now(); const r = fn(...a); tally(name, performance.now() - t); return r; };
  };
  const pairs = [
    [g.foot, 'update', 'foot.update'],
    [g.pickups, 'update', 'pickups'],
    [g.footsteps, 'update', 'footsteps'],
    [g.pistol, 'update', 'pistol'],
    [g.smoke, 'update', 'smoke'],
    [g.breakup, 'update', 'breakup'],
    [g.guns, 'update', 'guns'],
    [g.rockets, 'update', 'rockets'],
    [g.bullets, 'update', 'bullets'],
    [g.impacts, 'update', 'impacts'],
    [g.paper, 'update', 'paper'],
    [g.lanes, 'update', 'lanes'],
    [g.fx, 'update', 'fx'],
    [g.blast, 'update', 'blast'],
    [g.glass, 'update', 'glass'],
    [g.combatHud, 'update', 'combatHud'],
    [g.world, 'update', 'world.update(CityMap)'],
    [g.zones, 'update', 'zones'],
    [g.boundary, 'update', 'boundary'],
    [g.composer, 'render', 'RENDER composer'],
    [g.renderer, 'render', 'RENDER renderer'],
  ];
  for (const [o, m, n] of pairs) wrap(o, m, n);
  for (const [k, ga] of Object.entries(g.gunAnims || {})) wrap(ga, 'update', 'gunAnim:' + k);

  // whole-tick time + rAF interval
  const tfn = g._tick.bind(g);
  let last = performance.now();
  g._tick = () => {
    const t = performance.now();
    tally('FRAME GAP (rAF)', t - last); last = t;
    tfn();
    tally('TICK TOTAL', performance.now() - t);
  };

  await new Promise((r) => setTimeout(r, 6000));
  const stat = (a) => {
    const s = [...a].sort((x, y) => x - y);
    return { n: a.length, mean: a.reduce((p, c) => p + c, 0) / a.length, p95: s[Math.floor(s.length * 0.95)] || 0, max: s[s.length - 1] || 0 };
  };
  const out = Object.entries(S).map(([k, v]) => ({ name: k, ...stat(v) }));
  const info = g.renderer.info.render;
  return {
    rows: out,
    frames: (S['TICK TOTAL'] || []).length,
    secs: 6,
    calls: info.calls, tris: info.triangles,
    onFoot: !!g.foot?.active, hq: !!g.hq, pr: g.renderer.getPixelRatio(),
    pos: g.foot?.active ? g.foot.pos.toArray().map((n) => +n.toFixed(1)) : g.plane.position.toArray().map((n) => +n.toFixed(1)),
    progs: g.renderer.info.programs?.length,
    geos: g.renderer.info.memory.geometries, texs: g.renderer.info.memory.textures,
  };
});

const r = report;
console.log(`\nonFoot=${r.onFoot} hq=${r.hq} pixelRatio=${r.pr} pos=[${r.pos}]`);
console.log(`fps ≈ ${(r.frames / r.secs).toFixed(1)}   draws=${r.calls}  tris=${(r.tris / 1e6).toFixed(2)}M  programs=${r.progs} geos=${r.geos} texs=${r.texs}\n`);
console.log('system'.padEnd(26), 'mean ms'.padStart(8), 'p95'.padStart(8), 'max'.padStart(8), 'calls'.padStart(7));
for (const row of r.rows.sort((a, b) => b.mean - a.mean)) {
  console.log(row.name.padEnd(26), row.mean.toFixed(2).padStart(8), row.p95.toFixed(2).padStart(8), row.max.toFixed(2).padStart(8), String(row.n).padStart(7));
}
await browser.close();
