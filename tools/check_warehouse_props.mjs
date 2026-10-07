// Are the things standing in the warehouse REAL? The user's three asks:
// climb onto the tables, never walk inside them, and no grass coming up
// through the floor.
//
// Checks, in order:
//   1. the merged prop colliders are registered in world.collidables
//   2. running at a table ends ON TOP of it, not through it
//   3. a ray at chest height into the firing line stops at the booth
//   4. the grass sheet (terrain + 0.20 m) stays under the slab everywhere
//
// Run:  GAME_URL=http://localhost:5173/ node tools/check_warehouse_props.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true,
  viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse:|failed/i.test(m.text())) console.log('  [page]', m.text()); });

await enterGame(page, { user: 'propbot', pass: 'propbot123' });
console.log('in the world, waiting for the warehouse props…');

// the props load in nested callbacks after the shed itself
await page.waitForFunction(() => {
  const c = window.__game?.world?.collidables ?? [];
  return c.some((m) => m.name === 'warehouse_tables') && c.some((m) => m.name === 'warehouse_targets');
}, null, { timeout: 180000 });

const colliders = await page.evaluate(() => {
  const c = window.__game.world.collidables;
  return c.filter((m) => m.name.startsWith('warehouse_')).map((m) => ({
    name: m.name,
    tris: m.geometry.index ? m.geometry.index.count / 3 : m.geometry.attributes.position.count / 3,
    bvh: !!m.geometry.boundsTree,
    inScene: !!m.parent,
  }));
});
console.log('1) colliders:', JSON.stringify(colliders));

// ---- 2) the table: solid to walk into, climbable to get on top -------------
const probe = await page.evaluate(async () => {
  const T = window.__THREE;
  const g = window.__game;
  await g.foot.ready;
  let wh = null;
  g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const tables = [];
  wh.traverse((o) => { if (o.name === 'warehouse_table') tables.push(o); });
  const t = tables[1]; // the middle one, clear of the pistols on table 1
  t.updateWorldMatrix(true, false);
  const tp = new T.Vector3().setFromMatrixPosition(t.matrixWorld);
  const box = new T.Box3().setFromObject(t);
  const slabY = new T.Vector3().setFromMatrixPosition(wh.matrixWorld).y;
  const across = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion); // shed local +X

  // climb out of the plane and stand 1.1 m off the table's long side
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.plane.position.set(tp.x + across.x * 20, slabY + 1.6, tp.z + across.z * 20);
  if (!g.foot.active) g.foot.toggle();
  if (!g.foot.active) throw new Error('could not climb out');
  const stand = tp.clone().addScaledVector(across, 1.1).setY(slabY + 0.02);
  g.foot.pos.copy(stand);
  g.foot.yaw = Math.atan2(across.x, across.z); // forward is (-sin,-cos): face the table
  g.foot._snapCamera();                        // W runs AWAY FROM THE CAMERA
  const dir = across.clone().negate();

  // a) can't go inside it: a chest-height ray into the table has to stop
  const rc = new T.Raycaster(stand.clone().setY(slabY + 1.1), dir.clone(), 0, 3);
  const wall = rc.intersectObjects(g.world.collidablesNear(stand.x, stand.z), true)[0];
  // b) can climb on it: the probe foot.js runs every frame must find the top
  const ledge = g.foot._findLedge(dir.x, dir.z);
  return {
    slabY: +slabY.toFixed(2),
    tableTop: +box.max.y.toFixed(2),
    wall: wall ? { dist: +wall.distance.toFixed(2), name: wall.object.name } : null,
    ledge: ledge ? { y: +ledge.y.toFixed(2), rise: +(ledge.y - slabY).toFixed(2) } : null,
  };
});
console.log('2) table:', JSON.stringify(probe));
console.log(`   a) walk into it: ${probe.wall && probe.wall.name === 'warehouse_tables'
  ? `PASS — blocked at ${probe.wall.dist} m` : 'FAIL — nothing in the way'}`);
console.log(`   b) climb probe:  ${probe.ledge && Math.abs(probe.ledge.y - probe.tableTop) < 0.1
  ? `PASS — ledge at the table top (${probe.ledge.rise} m up)` : 'FAIL — no ledge found'}`);

// and now actually run at it. A short tap: the mantle starts on contact and
// the table is only 1.06 m across, so holding W carries you over it and off
// the far side — which is exactly what a player would expect, but it is not
// what this check is measuring.
await page.keyboard.down('w');
await page.waitForTimeout(350);
await page.keyboard.up('w');
await page.waitForTimeout(1500);
const onTable = await page.evaluate(() => ({
  y: +window.__game.foot.pos.y.toFixed(2),
  health: window.__game.health,
}));
const rose = onTable.y - probe.slabY;
console.log(`   c) ran at it: y=${onTable.y} (slab ${probe.slabY}, top ${probe.tableTop}) -> rose ${rose.toFixed(2)} m `
  + `${Math.abs(onTable.y - probe.tableTop) < 0.15 ? 'PASS — standing on the table' : 'FAIL'}`);

// ---- 3) a bullet-height ray into the firing line ---------------------------
const ray = await page.evaluate(() => {
  const T = window.__THREE;
  const g = window.__game;
  let wh = null;
  g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const booths = [];
  wh.traverse((o) => { if (o.name === 'warehouse_targets') booths.push(o); });
  const b = booths[Math.floor(booths.length / 2)];
  b.updateWorldMatrix(true, false);
  const bp = new T.Vector3().setFromMatrixPosition(b.matrixWorld);
  const slabY = new T.Vector3().setFromMatrixPosition(wh.matrixWorld).y;
  const along = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  const from = bp.clone().addScaledVector(along, -8).setY(slabY + 1.1);
  const rc = new T.Raycaster(from, along.clone(), 0, 20);
  const hits = rc.intersectObjects(g.world.collidablesNear(from.x, from.z), true);
  return {
    booths: booths.length,
    hit: hits.length ? { dist: +hits[0].distance.toFixed(2), name: hits[0].object.name } : null,
  };
});
console.log('3) firing line:', JSON.stringify(ray),
  ray.hit && ray.hit.name === 'warehouse_targets' ? '— PASS' : '— FAIL');

// ---- 4) grass under the slab ----------------------------------------------
const grass = await page.evaluate(() => {
  const T = window.__THREE;
  const g = window.__game;
  let wh = null;
  g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateWorldMatrix(true, false);
  const p = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  const ux = Math.sin(wh.rotation.y), uz = Math.cos(wh.rotation.y);   // along
  const vx = Math.cos(wh.rotation.y), vz = -Math.sin(wh.rotation.y);  // across
  let worst = -Infinity, at = null;
  for (let a = -24; a <= 24; a += 1) {           // inside the walls only
    for (let b = -69; b <= 69; b += 1) {
      const x = p.x + vx * a + ux * b;
      const z = p.z + vz * a + uz * b;
      const t = g.world.heightAt(x, z);
      if (t == null) continue;
      const grassTop = t + 0.2;                  // citymap _buildGrass offset
      if (grassTop - p.y > worst) { worst = grassTop - p.y; at = [+a, +b, +t.toFixed(2)]; }
    }
  }
  return { slabY: +p.y.toFixed(2), worstAboveSlab: +worst.toFixed(2), at };
});
console.log('4) grass vs floor:', JSON.stringify(grass),
  grass.worstAboveSlab < 0 ? `— PASS (buried by ${(-grass.worstAboveSlab).toFixed(2)} m)` : '— FAIL, grass pokes through');

await page.screenshot({ path: OUT + '/props_check.png' });
console.log('shot ->', OUT + '/props_check.png');
await ctx.close();
