// AM I INSIDE THE THING? Walk the hero at a prop from every direction, and
// climb it from every direction, then ask the prop's own BVH how far the body
// is from the nearest triangle. A hero standing ON something is at least half
// a body from the inside of it; one whose chest is 10 cm from a surface with
// solid on both sides has walked INTO the machine ("sometimes i am goin inside
// crane").
//   PROP=warehouse_crane node tools/_probe_inside.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const PROP = process.env.PROP || 'warehouse_crane';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction((n) => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === n) ok = true; });
  return ok;
}, PROP, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
await page.evaluate((r) => { window.__R = r; }, +(process.env.R || 9));
console.log(await page.evaluate(async (PROP) => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const out = [];
  let prop = null; g.scene.traverse((o) => { if (!prop && o.name === PROP) prop = o; });
  prop.updateMatrixWorld(true);
  const box = new T.Box3().setFromObject(prop);
  // AIM AT THE MACHINE, NOT AT ITS BOUNDING BOX. A crane's box is mostly boom
  // and air, and its centre is out over open floor: a hero walked at that
  // walks clean past the carrier and keeps going down the shed.
  const mid = prop.isGroup || prop.position ? prop.getWorldPosition(new T.Vector3()) : box.getCenter(new T.Vector3());
  mid.y = box.min.y;
  const size = box.getSize(new T.Vector3());
  out.push(`${PROP}: ${size.x.toFixed(1)} x ${size.z.toFixed(1)} x ${size.y.toFixed(1)} m`);
  // the collider meshes this prop owns, with their BVHs
  const cols = [];
  prop.traverse((o) => { if (o.isMesh && o.geometry?.boundsTree) cols.push(o); });
  out.push(`${cols.length} collider mesh(es): ${cols.map((c) => c.name).join(', ')}`);
  const inv = new T.Matrix4(), local = new T.Vector3(), hit = {};
  const nearest = (p) => {           // metres from `p` to the prop's nearest triangle
    let best = Infinity;
    for (const c of cols) {
      inv.copy(c.matrixWorld).invert();
      local.copy(p).applyMatrix4(inv);
      const r = c.geometry.boundsTree.closestPointToPoint(local, hit, 0, 12);
      if (r) best = Math.min(best, r.distance);
    }
    return best;
  };
  const R = window.__R || 9;
  const rows = [];
  for (let i = 0; i < 12; i++) {
    const a = (i / 12) * Math.PI * 2;
    const from = new T.Vector3(mid.x + Math.sin(a) * R, 0, mid.z + Math.cos(a) * R);
    from.y = g._groundAt(from.x, from.z, box.min.y + 2, 1) ?? box.min.y;
    const dir = new T.Vector3(mid.x - from.x, 0, mid.z - from.z).normalize();
    const yaw = Math.atan2(-dir.x, -dir.z);
    const walkIn = (climb) => {
      f.pos.copy(from); f.vy = 0; f.speed = 0; if (f._climb) f._endClimb('abort');
      f.yaw = yaw; f.rig.obj.rotation.y = yaw; f.grounded = true;
      f._snapCamera?.();
      for (let k = 0; k < 20; k++) f.update(1 / 60);
      g.keys.add('KeyW');
      // WALK UNTIL HE IS PAST IT, not for a fixed time: a hero who slips by
      // the machine keeps walking down a 139 m shed and the run says nothing.
      let stalled = 0, best = 0, climbs = 0, worst = Infinity, worstY = 0, rose = 0;
      const look = () => {
        for (const h of [0.15, 0.6, 1.1, 1.6]) {
          const d = nearest(new T.Vector3(f.pos.x, f.pos.y + h, f.pos.z));
          if (d < worst) { worst = d; worstY = h; }
        }
        rose = Math.max(rose, f.pos.y - from.y);
      };
      for (let k = 0; k < 900 && stalled < 60 && best < R + 4; k++) {
        if (climb && !f._climb && k % 12 === 0) { f._jumpBuf = 0.2; if (f._tryClimb()) climbs++; }
        f.update(1 / 60);
        const d = new T.Vector3().subVectors(f.pos, from).setY(0).dot(dir);
        if (d > best + 0.02) { best = d; stalled = 0; } else stalled++;
        if (k % 3 === 0) look();
      }
      g.keys.delete('KeyW');
      look();
      return { best, climbs, worst, worstY, y: rose, pos: f.pos.clone() };
    };
    const w = walkIn(false);
    const c = walkIn(true);
    rows.push({ deg: Math.round(a * 180 / Math.PI), w, c });
  }
  out.push('\n  from    walk: advanced  rose   nearest surface |  climbing: advanced  rose   nearest   climbs');
  for (const r of rows) {
    const fmt = (x) => `${x.best.toFixed(1).padStart(5)} m ${x.y >= 0.4 ? ('+' + x.y.toFixed(1) + 'm').padStart(6) : '     -'} `
      + `${x.worst === Infinity ? '  --  ' : x.worst.toFixed(2).padStart(6)}${x.worst < 0.25 ? ' INSIDE' : '       '}`;
    void fmt;
    out.push(`  ${String(r.deg).padStart(4)}°   ${fmt(r.w)} |   ${fmt(r.c)}  x${r.c.climbs}`);
  }
  const bad = rows.filter((r) => r.w.worst < 0.25).length;
  const badC = rows.filter((r) => r.c.worst < 0.25).length;
  const up = rows.filter((r) => r.c.y >= 0.4).length;
  out.push(`\n  walked INTO it from ${bad}/12 directions; climbing ends inside from ${badC}/12; `
    + `${up}/12 got off the ground at all`);
  return out.join('\n');
}, PROP));
await browser.close();
