// Fly to an actual placed panel block and shoot it from the air and from the
// street. Previous attempt teleported into geometry and just filmed a crash.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/real panel blocks/.test(m.text())) console.log('  [page]', m.text()); });
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

const where = await page.evaluate(() => {
  const w = window.__game.world;
  const p = w._panelPlacements || [];
  const by = {};
  for (const it of p) by[it.sections] = (by[it.sections] || 0) + 1;
  // pick the densest neighbourhood: the placement with the most neighbours
  let best = null; let bestN = -1;
  for (const a of p) {
    let n = 0;
    for (const b of p) if ((a.x - b.x) ** 2 + (a.z - b.z) ** 2 < 300 * 300) n++;
    if (n > bestN) { bestN = n; best = a; }
  }
  return { counts: by, best, neighbours: bestN, total: p.length };
});
console.log('placed by section count:', where.counts, '| densest cluster:', where.neighbours, 'blocks');

const shot = async (tag, dx, dy, dz, pitch) => {
  await page.evaluate(([b, dx, dy, dz, pitch]) => {
    const g = window.__game;
    g.crashed = false; g.crash = null;
    if (g.ui?.wasted) g.ui.wasted.classList.add('hidden');
    g.plane.position.set(b.x + dx, b.y + dy, b.z + dz);
    g.airborne = true; g.vel.set(0, 0, -30); g.speed = 30;
    g.plane.rotation.set(pitch, 0, 0);
    g.hull = 1000;
    g.world.update(g.plane.position, 0.016);
  }, [where.best, dx, dy, dz, pitch]);
  await page.waitForTimeout(5000);
  await page.screenshot({ path: `${OUT}/pb_${tag}.png` });
  console.log('shot', tag);
};

await shot('air', 0, 150, 190, -0.52);      // looking down on the cluster
await shot('close', 0, 26, 62, -0.12);      // street-ish, in front of one
await browser.close();
