// The whole chain in the real game: a real round from ballistics.js, classified
// as paper, routed to the card, scored. Fired straight at a range sheet.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/effacba5-9c0d-43bd-ac9c-0565a77831d7/scratchpad';
const ctx = await chromium.launchPersistentContext(`${OUT}/paper-${Date.now()}`, {
  headless: true, viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
const res = await fetch('http://localhost:3001/api/auth/login', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: 'movebot', password: 'movebot123' }) });
const { token } = await res.json();
await page.goto('http://localhost:5173/');
await page.evaluate((t) => localStorage.setItem('tf-token', t), token);
await enterGame(page, { user: 'movebot', pass: 'movebot123' });
await page.waitForFunction(() => window.__game?.paper?.sheets?.size > 0, null, { timeout: 240000 })
  .catch(() => console.log('(no sheets adopted — the range may not have loaded yet)'));
console.log(await page.evaluate(async () => {
  const g = window.__game, T = window.__THREE;
  const sheets = [...(g.paper?.sheets?.keys() ?? [])];
  if (!sheets.length) return 'NO PAPER ADOPTED';
  const mesh = sheets[0];
  const card = g.paper.sheets.get(mesh);
  mesh.updateWorldMatrix(true, false);
  // the CARD's centre, not the mesh origin: these sheets hang from a pivot at
  // their top edge, so the two are half a metre apart
  mesh.geometry.computeBoundingBox();
  const c = mesh.localToWorld(mesh.geometry.boundingBox.getCenter(new T.Vector3()));
  const nrm = new T.Vector3().setFromMatrixColumn(mesh.matrixWorld, card.flat).normalize();
  const out = [`sheets ${sheets.length} | card ${card.cardMM.toFixed(0)} mm`
    + ` on ${(card.W * 1000).toFixed(0)}x${(card.H * 1000).toFixed(0)} mm | canvas ${card.cw}x${card.ch}`];
  // five rounds from 8 m, straight at the middle, through the real sim -- from
  // BOTH sides of the sheet, because which way its normal points is the model's
  // business and only one side is the lane
  const before = g.paper.active?.shots.length ?? 0;
  for (const sign of [1, -1]) {
  const from = c.clone().addScaledVector(nrm, 8 * sign);
  const { CARTRIDGES, classifySurface } = await import('/src/ballistics.js');
  // WHICH WAY UP IS THE PRINTED FACE? Fire at the middle of the sheet and at a
  // point 150 mm above it, and see where each lands on the card. The chest rings
  // of a B-27 sit above the middle, so the high one must score the better.
  const up = new T.Vector3(0, 1, 0);
  for (const dy of [0, 0.15, -0.15]) {
    const at = c.clone().addScaledVector(up, dy);
    const r2 = new T.Raycaster(at.clone().addScaledVector(nrm, 2), nrm.clone().negate(), 0, 6);
    const h2 = r2.intersectObject(mesh, true)[0];
    if (!h2) { out.push(`  ${dy >= 0 ? '+' : ''}${(dy * 1000).toFixed(0)} mm: no hit`); continue; }
    const sh = g.paper.hit(mesh, h2.point, nrm.clone().negate(), CARTRIDGES['9x19'], h2.uv);
    out.push(`  ${dy >= 0 ? '+' : ''}${(dy * 1000).toFixed(0)} mm up the sheet:`
      + ` uv ${h2.uv.x.toFixed(3)},${h2.uv.y.toFixed(3)} -> ring ${sh?.ring}`
      + ` (${sh ? sh.r.toFixed(0) : '?'} mm off the aim point)`);
  }
  g.paper.reset();
  const cart = CARTRIDGES['9x19'];
  // first, without any bullet at all: does a ray onto the sheet read as paper?
  const ray = new T.Raycaster(c.clone().addScaledVector(nrm, 3), nrm.clone().negate(), 0, 10);
  const hit = ray.intersectObject(mesh, true)[0];
  out.push(hit ? `ray hits the sheet at ${hit.distance.toFixed(2)} m,`
    + ` classified "${classifySurface(hit, g.world)}"` : 'RAY MISSED THE SHEET');
  if (hit) {
    const shot = g.paper.hit(mesh, hit.point, nrm.clone().negate(), cart);
    out.push(shot ? `direct card hit: ring ${shot.ring} at ${shot.r.toFixed(0)} mm`
      : 'CARD REFUSED THE HIT');
  }
  for (let i = 0; i < 5; i++) {
    const aim = c.clone().add(new T.Vector3((Math.random() - 0.5) * 0.06, (Math.random() - 0.5) * 0.06, 0));
    g.bullets.spawn(g.pistol?.cartridge ?? cart, from, aim.sub(from).normalize(),
      g.world.shootables ?? [], { from: 'pistol' });
  }
  for (let i = 0; i < 90; i++) { g.bullets.update(1 / 120); await new Promise((r) => requestAnimationFrame(r)); }
  out.push(`  from ${sign > 0 ? 'the +normal side' : 'the -normal side'}: card now has `
    + `${g.paper.active?.shots.length ?? 0} shots`);
  }
  const live = g.paper.active;
  out.push(`sim rounds that landed on paper: ${(live?.shots.length ?? 0) - before - 0}`);
  out.push(live ? `shots ${live.shots.length} score ${live.score} spread ${live.spread.toFixed(1)} mm`
    + ` rings [${live.shots.map((s) => s.ring).join(',')}]` : 'NO HITS REGISTERED');
  return out.join('\n');
}));
await ctx.close();
