// BULLET HOLES IN MID-AIR. Every decal is stamped on whatever the round hit;
// if that thing is INVISIBLE, the mark is left hanging in the open with
// nothing under it. So: fire a lot of rounds around the apron and report
// every impact that landed on something nobody can see.
//   node tools/_probe_floaters.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT || '/tmp';
fs.mkdirSync(`${OUT}/floaters`, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(2000);

// ---- get to the plane hall, where the screenshots were taken -----------
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
      g.plane.position.set(p.x, p.y, p.z);
    }
    tick();
    if (window.__pin) {
      const c = g.camera;
      c.position.set(p.cx, p.cy, p.cz);
      c.lookAt(p.lx, p.ly, p.lz);
      c.updateMatrixWorld(true);
      g.renderer.render(g.scene, c);
    }
  };
});
await page.waitForFunction(() => {
  let h = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') h = o; });
  return !!h;
}, null, { timeout: 420000 });
const hall = await page.evaluate(() => {
  let h = null;
  window.__game.scene.traverse((o) => { if (o.name === 'planehall') h = o; });
  const p = h.getWorldPosition(new window.__THREE.Vector3());
  return { x: p.x, y: p.y, z: p.z };
});
await page.evaluate((h) => {
  window.__pin = { x: h.x, y: h.y + 120, z: h.z, cx: h.x, cy: h.y + 420, cz: h.z + 10,
    lx: h.x, ly: h.y, lz: h.z };
}, hall);
await page.waitForFunction(() => (window.__game.targets?.list.length ?? 0) >= 30,
  null, { timeout: 420000 }).catch(() => console.log('TIMEOUT waiting for the fleet'));
await page.waitForTimeout(2500);

await page.evaluate(() => {
  const g = window.__game;
  window.__log = [];
  const seen = (o) => { for (let p = o; p; p = p.parent) if (!p.visible) return false; return true; };
  const resolve = g.impacts.resolve.bind(g.impacts);
  g.impacts.resolve = (info) => {
    const o = info.hit?.object;
    window.__log.push({
      where: 'sim', surface: info.surface, name: o?.name || '(unnamed)',
      visible: o ? seen(o) : null, plane: !!info.hit?.planeRec,
    });
    return resolve(info);
  };
});

// Spray from the middle of the fleet, exactly where the screenshots are from.
const res = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  const rec = g.targets.list[Math.floor(g.targets.list.length / 2)];
  const eye = rec.wrapper.getWorldPosition(new THREE.Vector3());
  eye.y += 1.6;
  eye.x += 9;
  window.__eye = { x: eye.x, y: eye.y, z: eye.z };
  const cart = g.pistol.cart;
  const near = g.world.collidablesNear(eye.x, eye.z);
  let fired = 0;
  for (let a = 0; a < 44; a++) {
    for (const el of [-0.22, -0.06, 0.06, 0.2]) {
      const th = (a / 44) * Math.PI * 2;
      const d = new THREE.Vector3(Math.cos(th), el, Math.sin(th)).normalize();
      g.bullets.spawn(cart, eye.clone(), d, near, { from: 'probe' });
      fired++;
    }
  }
  return { fired, targets: near.length, fleet: g.targets.list.length };
});
console.log('fired:', JSON.stringify(res));
await page.waitForTimeout(4000);

const log = await page.evaluate(() => {
  const rows = window.__log.splice(0);
  const by = new Map();
  for (const r of rows) {
    const k = `${r.visible ? 'SEEN  ' : 'HIDDEN'} | ${r.plane ? 'plane' : r.surface ?? '-'} | ${r.name}`;
    by.set(k, (by.get(k) ?? 0) + 1);
  }
  return { total: rows.length, kinds: [...by.entries()].sort((a, b) => b[1] - a[1]).slice(0, 18) };
});
console.log(`impacts: ${log.total}`);
for (const [k, n] of log.kinds) console.log(` ${String(n).padStart(4)}  ${k}`);

// ---- AND THE MARKS THEMSELVES: is there anything under each one? --------
const floaters = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  const m = new THREE.Matrix4();
  const p = new THREE.Vector3();
  const n = new THREE.Vector3();
  const ray = new THREE.Raycaster();
  const layers = [
    ['impact.pits', g.impacts.pits], ['impact.spall', g.impacts.spall],
    ['impact.exits', g.impacts.exits], ['impact.scuffs', g.impacts.scuffs],
    ['impact.stars', g.impacts.stars],
    ['plane.holes', g.planeDamage.holes], ['plane.tears', g.planeDamage.tears],
    ['plane.exits', g.planeDamage.exits], ['plane.scrapes', g.planeDamage.scrapes],
  ];
  const out = [];
  const hits = [];
  for (const [name, L] of layers) {
    const mesh = L?.mesh;
    if (!mesh) continue;
    let live = 0; let air = 0; let over = 0;
    for (let i = 0; i < mesh.count; i++) {
      mesh.getMatrixAt(i, m);
      const s = new THREE.Vector3().setFromMatrixScale(m);
      if (s.x < 1e-4) continue;             // cleared or hidden slot
      live++;
      p.setFromMatrixPosition(m);
      n.set(0, 0, 1).transformDirection(m).normalize();
      // IS THERE ANYTHING YOU CAN SEE UNDER IT? Not "anything at all" --
      // half the things a bullet can hit are invisible colliders, and a mark
      // on one of those is exactly the bug being hunted. So the probe is cast
      // against VISIBLE geometry only.
      const near = g.world.collidablesNear(p.x, p.z);
      const all = [];
      const seen = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
      for (const o of near) if (seen(o)) all.push(o);
      for (const rec of g.targets.list) {
        const parts = rec._parts;
        if (parts) for (const o of parts.all) if (seen(o)) all.push(o);
      }
      ray.set(p.clone().addScaledVector(n, 0.3), n.clone().negate());
      ray.far = 0.62;
      const h = ray.intersectObjects(all, true)[0];
      if (!h) { air++; if (hits.length < 12) hits.push({ layer: name, why: 'centre', at: [+p.x.toFixed(1), +p.y.toFixed(1), +p.z.toFixed(1)] }); continue; }
      // ...AND DOES THE SURFACE REACH THE EDGES OF THE MARK? A decal laid on
      // something thinner than itself -- a cable, an aerial, a gear strut --
      // hangs over the sides with nothing behind it, which from a few metres
      // reads as a bullet hole floating in mid-air.
      const ex = new THREE.Vector3().setFromMatrixColumn(m, 0);
      const ey = new THREE.Vector3().setFromMatrixColumn(m, 1);
      let off = 0;
      for (const [sx, sy] of [[0.5, 0], [-0.5, 0], [0, 0.5], [0, -0.5]]) {
        const c = p.clone().addScaledVector(ex, sx).addScaledVector(ey, sy);
        ray.set(c.addScaledVector(n, 0.3), n.clone().negate());
        ray.far = 0.62;
        if (!ray.intersectObjects(all, true).length) off++;
      }
      if (off >= 2) {
        over++;
        if (hits.length < 12) {
          hits.push({ layer: name, why: `${off}/4 corners off`, on: h.object.name || '(unnamed)',
            size: +new THREE.Vector3().setFromMatrixScale(m).x.toFixed(3) });
        }
      }
    }
    if (live) out.push({ layer: name, live, inAir: air, overhanging: over });
  }
  return { out, hits: hits.slice(0, 12) };
});
console.log('marks with nothing VISIBLE under them:');
for (const r of floaters.out) console.log(`  ${r.layer.padEnd(14)} ${String(r.live).padStart(4)} live, ${r.inAir} in air, ${r.overhanging} hanging over an edge`);
if (floaters.hits.length) console.log('  examples:', JSON.stringify(floaters.hits));
await browser.close();
