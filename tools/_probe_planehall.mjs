// The plane hall beside the warehouse: does it stand on clear ground, does
// every machine land inside it, and is the warehouse itself untouched?
//   node tools/_probe_planehall.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/tmp';
fs.mkdirSync(OUT + '/hall', { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => {
  const t = m.text();
  if (/plane hall|warehouse:|shed/.test(t) || m.type() === 'error') console.log('[page]', t.slice(0, 200));
});
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });

// the loop owns the camera: pin the pose from inside the tick
await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.crash?.dispose?.(); g.crash = null; g._wastedShown = false;
      g.container.classList.remove('wasted');
      g.ui.crash?.classList.add('hidden');
      g.plane.position.set(p.x, p.y, p.z);
    }
    tick();
    if (window.__pin) {
      const c = g.camera;
      c.position.set(window.__pin.cx, window.__pin.cy, window.__pin.cz);
      c.lookAt(window.__pin.lx, window.__pin.ly, window.__pin.lz);
      c.updateMatrixWorld(true);
      g.renderer.render(g.scene, c);
    }
  };
});

// enterGame resolves at world.ready — the airport and the hall are built a
// few frames later, down the scenery chain.
await page.waitForFunction(() => {
  let hall = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') hall = o; });
  return !!hall;
}, null, { timeout: 420000 });

const info = await page.evaluate(() => {
  const g = window.__game;
  let hall = null, wh = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') hall = o; if (o.name === 'warehouse') wh = o; });
  const p = hall?.getWorldPosition(new window.__THREE.Vector3());
  const w = wh?.getWorldPosition(new window.__THREE.Vector3());
  return {
    hall: hall ? { x: p.x, y: p.y, z: p.z, yaw: hall.rotation.y, ...hall.userData.shedShell } : null,
    warehouse: w ? { x: w.x, y: w.y, z: w.z, hasShed: !!wh.userData.shed } : null,
    spawn: g.world.spawn,
    dist: p && g.world.spawn ? Math.hypot(p.x - g.world.spawn.x, p.z - g.world.spawn.z) : null,
  };
});
console.log('hall:', JSON.stringify(info.hall));
console.log('warehouse:', JSON.stringify(info.warehouse), 'spawn->hall', info.dist?.toFixed(0), 'm');

// wait for the fill (it starts once the camera is within 900 m)
await page.evaluate(({ x, y, z }) => {
  window.__pin = { x, y: y + 120, z, cx: x, cy: y + 420, cz: z + 10, lx: x, ly: y, lz: z };
}, info.hall);
await page.waitForFunction(() => {
  let n = 0; window.__game.scene.traverse((o) => { if (o.userData?.planeId) n++; });
  return n >= 30;
}, null, { timeout: 420000 }).catch(() => console.log('TIMEOUT waiting for planes'));
await page.waitForTimeout(3000);

const placed = await page.evaluate(() => {
  const THREE = window.__THREE;
  let hall = null;
  window.__game.scene.traverse((o) => { if (o.name === 'planehall') hall = o; });
  const d = hall.userData.shedShell;
  const fleet = hall.children.find((c) => c.name === 'planehall_fleet') ?? hall;
  // MEASURE IN THE HALL'S OWN FRAME. Box3.setFromObject reports world space,
  // and this hall is turned 136 degrees -- the world AABB of an 80 m Tu-144
  // is 83 m square, so mapping its corners back into hall space says it
  // straddles a lane it is nowhere near. Stand the hall up square for the
  // measurement instead, then put it back.
  const keep = { p: hall.position.clone(), r: hall.rotation.y };
  hall.position.set(0, 0, 0);
  hall.rotation.y = 0;
  hall.updateMatrixWorld(true);
  const rows = [];
  let intrude = null;
  for (const c of fleet.children) {
    if (!c.userData?.planeId) continue;
    const b = new THREE.Box3().setFromObject(c, true);
    const s = b.getSize(new THREE.Vector3());
    rows.push({ id: c.userData.planeId, x: +c.position.x.toFixed(1), z: +c.position.z.toFixed(1),
      span: +s.x.toFixed(1), len: +s.z.toFixed(1), minx: +b.min.x.toFixed(1), maxx: +b.max.x.toFixed(1) });
    const clear = b.min.x < 0 && b.max.x > 0 ? 0 : Math.min(Math.abs(b.min.x), Math.abs(b.max.x));
    if (!intrude || clear < intrude.clear) intrude = { id: c.userData.planeId, clear: +clear.toFixed(1) };
  }
  hall.position.copy(keep.p);
  hall.rotation.y = keep.r;
  hall.updateMatrixWorld(true);
  const halfW = d.width / 2, halfL = d.length / 2;
  const out = rows.filter((r) => Math.abs(r.x) > halfW || Math.abs(r.z) > halfL);
  return { n: rows.length, rows, outside: out, d, intrude };
});
console.log(`parked ${placed.n} planes; outside the walls: ${placed.outside.length}; `
  + `closest wingtip to the centreline ${placed.intrude?.clear} m (${placed.intrude?.id})`);
console.log(placed.rows.map((r) => `${r.id}@(${r.x},${r.z})`).join(' '));

// camera poses in HALL-LOCAL metres (x across the span, z along the length,
// y up from the slab) -- the hall is turned 136 degrees, so world offsets
// pointed the lens at a side wall.
// the baked colliders must sit on the aeroplanes they came from
const col = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  let hall = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') hall = o; });
  const planes = new Map();
  hall.traverse((o) => { if (o.userData?.planeId) planes.set(o.userData.planeId, o.getWorldPosition(new THREE.Vector3())); });
  let worst = 0, worstId = '', n = 0;
  for (const m of g.world.collidables) {
    if (!m.name?.startsWith('planehall_')) continue;
    n++;
    const id = m.name.slice('planehall_'.length);
    const p = planes.get(id);
    if (!p) continue;
    m.geometry.computeBoundingBox();
    const c = m.geometry.boundingBox.getCenter(new THREE.Vector3());
    const d = Math.hypot(c.x - p.x, c.z - p.z);
    if (d > worst) { worst = d; worstId = id; }
  }
  return { n, worst: +worst.toFixed(1), worstId };
});
console.log(`colliders: ${col.n} baked, worst offset from its aeroplane ${col.worst} m (${col.worstId})`);

const shots = [
  ['aerial',      { cam: [0, 640, 320], at: [0, 0, 0] }],
  ['outside',     { cam: [0, 45, 330], at: [0, 16, 120] }],
  ['lane_near',   { cam: [0, 10, 170], at: [0, 10, -120] }],   // down the lane
  ['lane_far',    { cam: [0, 12, -170], at: [0, 10, 100] }],   // and back up it
  ['inside_dps',  { cam: [55, 14, 170], at: [0, 10, 60] }],
  ['inside_tank', { cam: [60, 16, -40], at: [-20, 12, -150] }],
  ['both_sheds',  { cam: [-480, 250, 180], at: [0, 0, 0] }],
];
const toWorld = (h, [lx, ly, lz]) => {
  const ux = Math.sin(h.yaw), uz = Math.cos(h.yaw);
  const vx = Math.cos(h.yaw), vz = -Math.sin(h.yaw);
  return { x: h.x + vx * lx + ux * lz, y: h.y + ly, z: h.z + vz * lx + uz * lz };
};
for (const [name, pose] of shots) {
  const c = toWorld(info.hall, pose.cam);
  const a = toWorld(info.hall, pose.at);
  await page.evaluate((p) => { window.__pin = p; },
    { x: info.hall.x, y: info.hall.y + 400, z: info.hall.z,
      cx: c.x, cy: c.y, cz: c.z, lx: a.x, ly: a.y, lz: a.z });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${OUT}/hall/${name}.png` });
}
await browser.close();
