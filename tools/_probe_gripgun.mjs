// THE CORNER GRIP, unarmed and then with a pistol drawn, measured in the SAME
// run so the aeroplane is in the same state for both: where the solver was
// asked to put each hand, and where each hand actually ended up.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);

// take a pistol off the bench, but LEAVE IT HOLSTERED for the first reading
console.log('bench:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (!t) return 'no bench';
  t.updateWorldMatrix(true, true);
  const tp = new T.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(tp.x, tp.y + 1.6, tp.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.pickups.scan();
  const it = g.pickups.items.find((x) => x.def.id === 'beretta');
  if (it) {
    const wp = it.obj.getWorldPosition(new T.Vector3());
    for (let k = 0; k < 4; k++) {
      g.foot.pos.set(wp.x, tp.y + 2, wp.z + 0.3 - k * 0.1);
      for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
      g.pickups.update(0.016, g.foot.pos);
      if (g.pickups.near?.def?.id === 'beretta') { g.pickups.take(); break; }
    }
  }
  if (g.foot.held) g.foot.holster();     // off the hand for the first reading
  for (let i = 0; i < 40; i++) g.foot.update(1 / 60);
  return `carried ${g.weapons.carried.map((w) => w?.name ?? '-').join(',')}, held ${g.foot.held?.name ?? 'nothing'}`;
}));

await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(500);
await page.click('#fc-hi');
await page.waitForTimeout(2500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-2.6, 1.0, 0.3));
  g.foot.pos.copy(p); g.foot.vy = 0;
});
await page.waitForTimeout(8000);

const read = async (tag) => console.log(`\n${tag}\n` + await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, pl = window.__plane;
  pl.updateMatrixWorld(true);
  const d = g._gripDbg;
  const out = [`  stage ${g.heldByWind}, held ${g.foot.held?.name ?? 'none'}`];
  if (!d) return out.concat('  NO GRIP DEBUG — the grip block never ran').join('\n');
  const L = pl.worldToLocal(d.l.clone()), R = pl.worldToLocal(d.r.clone());
  out.push(`  measured body: arm ${d.arm?.toFixed?.(3)} m, shoulder ${d.sh?.toFixed?.(2)} m, `
    + `belly ${d.belly?.toFixed?.(3)} m;  corner at y ${d.gy?.toFixed?.(2)} z ${d.gz?.toFixed?.(2)}`);
  out.push(`  asked: L ${d.gotL ? `(${L.x.toFixed(2)}, ${L.y.toFixed(2)}, ${L.z.toFixed(2)})` : 'none'}`
    + `  R ${d.gotR ? `(${R.x.toFixed(2)}, ${R.y.toFixed(2)}, ${R.z.toFixed(2)})` : 'none'}`
    + `  solved: ${d.on}  edge z ${d.ez?.toFixed?.(2)} y ${d.ey?.toFixed?.(2)}`);
  const hands = {};
  g.foot.rig.obj.traverse((o) => {
    if (o.isBone && /hand|wrist/i.test(o.name) && !/finger|thumb/i.test(o.name)) {
      hands[/_l|left|\.l$/i.test(o.name) ? 'L' : 'R'] = o;
    }
  });
  for (const s of ['L', 'R']) {
    const h = hands[s];
    if (!h) continue;
    const w = h.getWorldPosition(new T.Vector3());
    const tgt = s === 'L' ? d.l : d.r;
    const got = s === 'L' ? d.gotL : d.gotR;
    const lp = pl.worldToLocal(w.clone());
    out.push(`  hand ${s}: at (${lp.x.toFixed(2)}, ${lp.y.toFixed(2)}, ${lp.z.toFixed(2)})`
      + (got ? `  ${w.distanceTo(tgt).toFixed(2)} m off its target` : '  (free)'));
  }
  return out.join('\n');
}));
await read('UNARMED (pistol holstered)');

console.log('\ndraw:', await page.evaluate(() => {
  const g = window.__game;
  const want = g.weapons.carried[1];
  if (want && !g.foot.held) g.foot.drawWeapon(want);
  return g.foot.held?.name ?? 'still nothing';
}));
await page.waitForTimeout(2000);
await read('ARMED (pistol in hand)');
await browser.close();
