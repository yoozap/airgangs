// The pickup cards at the benches: stand at the pistols' table (two cards),
// then at the automatics' table (the rifle's card), screenshot each with the
// HUD, and list what loot the scene holds (no belt expected).
//   node tools/_probe_cards.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/cards';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 1100, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(8000);

const setup = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const tables = [];
  g.scene.traverse((o) => { if (o.name === 'warehouse_table') tables.push(o); });
  for (const t of tables) t.updateWorldMatrix(true, true);
  window.__tables = tables.map((t) => new THREE.Vector3().setFromMatrixPosition(t.matrixWorld));
  g.renderer.setAnimationLoop(null);
  window.__at = (i, dx, dz) => {
    const p = window.__tables[i];
    g.foot.pos.set(p.x + dx, p.y + 1.5, p.z + dz);
    g.foot.yaw = Math.PI / 2;
    g.foot._snapCamera?.();
    for (let k = 0; k < 30; k++) { g.foot.update(1 / 60); g.pickups.update(1 / 60, g.foot.pos); }
    g.renderer.render(g.scene, g.camera);
    const cards = [...document.querySelectorAll('#pickup-cards .pk-card')].map((c) => (c.classList.contains('focus') ? '*' : '') + c.querySelector('.pk-name').textContent);
    return { cards, near: g.pickups.near?.def?.id ?? null };
  };
  // where each piece of loot sits against the nearest bench top (the pistols'
  // origin is on their footprint; the rifle lies on its side, half a width up)
  const loot = g.pickups.items.map((it) => {
    const p = new THREE.Vector3().setFromMatrixPosition(it.obj.matrixWorld);
    let best = null, bd = Infinity;
    for (const t of tables) { const d = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld).distanceTo(p); if (d < bd) { bd = d; best = t; } }
    const tp = new THREE.Vector3().setFromMatrixPosition(best.matrixWorld);
    const box = new THREE.Box3().setFromObject(it.obj);
    return { id: it.def.id, table: tables.indexOf(best), aboveTableBase: +(p.y - tp.y).toFixed(3), lowest: +(box.min.y - tp.y).toFixed(3), highest: +(box.max.y - tp.y).toFixed(3), dx: +(p.x - tp.x).toFixed(2), dz: +(p.z - tp.z).toFixed(2) };
  });
  window.__side = (i) => {
    const t = window.__tables[i];
    g.camera.position.set(t.x + 3.2, t.y + 1.9, t.z + 0.4);
    g.camera.lookAt(t.x, t.y + 1.2, t.z);
    g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
  };
  return { tables: tables.length, loot };
});
console.log(JSON.stringify(setup));
await page.evaluate(() => window.__side(1));
fs.writeFileSync(`${DIR}/table1_side.png`, await page.screenshot());
for (const [i, name] of [[0, 'pistols'], [1, 'automats'], [2, 'empty']]) {
  const r = await page.evaluate(([i]) => window.__at(i, 0.9, 0), [i]);
  console.log(name, JSON.stringify(r));
  fs.writeFileSync(`${DIR}/cards_${name}.png`, await page.screenshot());
}
await ctx.close();
