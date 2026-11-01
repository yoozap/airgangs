// A/B ON THE LEDGE PROBE. The old _findLedge is re-implemented here exactly as
// it was (down-probe, headroom, stand 0.35 m further in, no questions asked
// about what is under the feet there) and run side by side with the new one
// over a ring of stances round each prop. What we want to see: the same answer
// everywhere the old one was right, and a REFUSAL or a pulled-back stand point
// where the old one put the body inside something.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = 0; window.__game?.scene?.traverse((o) => { if (/^(warehouse_crane|ammo_box)$/.test(o.name)) ok++; });
  return ok >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const out = [];
  const STEP_UP = 0.65, CLIMB_MAX = 2.6, CLIMB_REACH = 1.0, CLIMB_CLEAR = 1.7;
  const ray = new T.Raycaster();
  // ---- the OLD probe, verbatim -------------------------------------------
  const oldFind = (dirX, dirZ) => {
    const feet = f.pos.y;
    const near = g.world.collidablesNear(f.pos.x, f.pos.z);
    if (!near.length) return null;
    for (const reach of [0.55, CLIMB_REACH]) {
      const px = f.pos.x + dirX * reach, pz = f.pos.z + dirZ * reach;
      ray.set(new T.Vector3(px, feet + CLIMB_MAX + 0.4, pz), new T.Vector3(0, -1, 0));
      ray.far = CLIMB_MAX + 0.6;
      const down = ray.intersectObjects(near, true);
      ray.far = Infinity;
      if (!down.length) continue;
      if (down[0].object.userData.noClimb) return null;
      const top = down[0].point.y, rise = top - feet;
      if (rise <= STEP_UP || rise > CLIMB_MAX) continue;
      ray.set(new T.Vector3(px, top + 0.12, pz), new T.Vector3(0, 1, 0));
      ray.far = CLIMB_CLEAR;
      const up = ray.intersectObjects(near, true);
      ray.far = Infinity;
      if (up.length) continue;
      return new T.Vector3(px + dirX * 0.35, top + 0.02, pz + dirZ * 0.35);
    }
    return null;
  };
  // Is a point standing INSIDE something? Ask the collider BVHs how far the
  // nearest triangle is from the body's chest there.
  const near0 = g.world.collidablesNear(f.pos.x, f.pos.z);
  const inv = new T.Matrix4(), local = new T.Vector3(), hit = {};
  const clearance = (p) => {
    let best = Infinity;
    for (const c of g.world.collidablesNear(p.x, p.z)) {
      if (!c.geometry?.boundsTree) continue;
      inv.copy(c.matrixWorld).invert();
      local.copy(p).applyMatrix4(inv);
      const r = c.geometry.boundsTree.closestPointToPoint(local, hit, 0, 6);
      if (r) best = Math.min(best, r.distance);
    }
    return best;
  };
  const props = [];
  g.scene.traverse((o) => {
    if (/^(warehouse_crane|ammo_box)$/.test(o.name) && !props.some((p) => p.name === o.name)) props.push(o);
  });
  for (const prop of props) {
    prop.updateMatrixWorld(true);
    const b = new T.Box3().setFromObject(prop);
    const mid = b.getCenter(new T.Vector3());
    const R = Math.max(b.max.x - b.min.x, b.max.z - b.min.z) / 2;
    let same = 0, refused = 0, pulled = 0, oldInside = 0, newInside = 0, both = 0;
    const rows = [];
    for (let i = 0; i < 24; i++) {
      const a = (i / 24) * Math.PI * 2;
      for (const d of [0.4, 0.8, 1.2]) {
        const p = new T.Vector3(mid.x + Math.sin(a) * (R + d), 0, mid.z + Math.cos(a) * (R + d));
        const gy = g._groundAt(p.x, p.z, b.min.y + 2, 1);
        if (gy === null) continue;
        f.pos.set(p.x, gy, p.z);
        const dir = new T.Vector3(mid.x - p.x, 0, mid.z - p.z).normalize();
        f.yaw = Math.atan2(-dir.x, -dir.z);
        const o1 = oldFind(dir.x, dir.z);
        const n1 = f._findLedge(dir.x, dir.z);
        if (!o1 && !n1) continue;
        both++;
        const oc = o1 ? clearance(new T.Vector3(o1.x, o1.y + 1.0, o1.z)) : null;
        const nc = n1 ? clearance(new T.Vector3(n1.x, n1.y + 1.0, n1.z)) : null;
        if (o1 && oc < 0.25) oldInside++;
        if (n1 && nc < 0.25) newInside++;
        if (o1 && !n1) { refused++; rows.push(`   refused: old stood at y${o1.y.toFixed(2)} with ${oc.toFixed(2)} m of clearance`); }
        else if (o1 && n1 && o1.distanceTo(n1) > 0.05) { pulled++; rows.push(`   pulled back ${o1.distanceTo(n1).toFixed(2)} m: clearance ${oc.toFixed(2)} -> ${nc.toFixed(2)} m`); }
        else same++;
      }
    }
    out.push(`${prop.name}: ${both} stances found a ledge — ${same} identical, ${pulled} stand point pulled back, ${refused} now refused`);
    out.push(`   body INSIDE the prop at the landing: old ${oldInside}, new ${newInside}`);
    out.push(rows.slice(0, 6).join('\n'));
  }
  return out.join('\n');
}));
await browser.close();
