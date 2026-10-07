import { chromium } from 'playwright';

// Multiplayer check: a hole punched in one session must come back on the next
// one — through localStorage AND through the damage API (Mongo), which is what
// makes every pilot see the same broken city.
const URL = process.env.URL || 'http://localhost:5174';
const API = process.env.API || 'http://localhost:3001';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));

const boot = async () => {
  await page.waitForTimeout(1200);
  await page.evaluate(() => document.getElementById('btn-map').click());
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
  await page.waitForTimeout(4000);
};

await page.goto(URL);
await boot();

// punch a hole in a specific building, straight through the game's own path
const shot = await page.evaluate(() => {
  const g = window.__game;
  let best = null;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      const h = r.top - r.base;
      if (r.hp < r.maxHp || h < 16 || h > 40) continue;
      const d = Math.hypot(r.cx, r.cz);
      if (!best || d < best.d) best = { m, r, d };
    }
  }
  if (!best) return { error: 'no target' };
  const { m, r } = best;
  r.chunk = m;
  g.world.punchHole(r, { x: r.cx, y: r.base + 9, z: r.cz }, 0.1); // seeds the shell
  const cell = r.shell.cells[Math.floor(r.shell.cells.length * 0.35)];
  g.world.punchHole(r, cell, 3.2);
  return {
    bi: r.bi,
    holes: r.shell.holes.length,
    live: r.shell.live,
    cells: r.shell.cells.length,
    addr: g.world.addressOf(r) || '(none)',
  };
});
console.log('punched:', JSON.stringify(shot));
await page.waitForTimeout(3500); // debounced localStorage write + API push

const api = await (await fetch(`${API}/api/damage`)).json();
const row = api[String(shot.bi)];
console.log('in DB:', JSON.stringify(row));

// reload and see what comes back
await page.reload();
await boot();
const after = await page.evaluate((bi) => {
  const g = window.__game;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.bi !== bi) continue;
      return {
        hasShell: !!r.shell,
        holes: r.shell?.holes.length ?? 0,
        live: r.shell?.live ?? -1,
        cells: r.shell?.cells.length ?? -1,
      };
    }
  }
  return { error: 'building not found' };
}, shot.bi);
console.log('after reload:', JSON.stringify(after));

const ok = after.hasShell && after.holes >= 2 && after.live < after.cells;
console.log(ok ? 'PERSIST OK' : 'PERSIST FAILED');
await browser.close();
