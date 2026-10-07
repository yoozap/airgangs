// What room is there at a firing point? The real floor (the stand point sits
// inside the counter, so a ray from above lands on the slab), the silhouette
// sheets, the dividers, the control box, and what a round can see from each of
// the spots the cinematic uses. Two stills from inside the booth as well.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/c0e39dd4-3ace-4f73-a796-074118a8f0dd/scratchpad';
const ctx = await chromium.launchPersistentContext(`${OUT}/cine-profile`, {
  headless: true, viewport: { width: 1600, height: 900 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'cinebot', pass: 'cinebot123' });
await page.waitForFunction(() => window.__game?.lanes?.lanes?.length >= 10, null, { timeout: 240000 });
await page.waitForTimeout(1500);

const LI = +(process.env.LANE ?? 5);
const r = await page.evaluate((LI) => {
  const T = window.__THREE;
  const g = window.__game;
  const out = {};
  const l = g.lanes.lanes[LI];
  const near = g.world.collidablesNear(l.stand.x, l.stand.z);
  const ray = new T.Raycaster(); ray.firstHitOnly = true;
  const rail = l.rail.clone();
  const right = new T.Vector3().crossVectors(new T.Vector3(0, 1, 0), rail).normalize();
  const at = (ox, oz, y) => new T.Vector3().copy(l.stand).setY(y)
    .addScaledVector(right, ox).addScaledVector(rail, oz);
  const down = (p, from = 2.3, far = 6) => {
    ray.set(p.clone().setY(from), new T.Vector3(0, -1, 0)); ray.far = far;
    const h = ray.intersectObjects(near, true)[0];
    ray.far = Infinity;
    return h ? +h.point.y.toFixed(2) : null;
  };
  // THE FLOOR: 1.5 m behind the counter, where nothing but floor can be
  const fy = down(at(0, -1.5, 0), 466.9, 3) ?? 465.42;  // under the range's 3.5 m ceiling, above the counter
  out.lane = LI;
  out.floor = fy;
  out.carrier = { at: +l.at.toFixed(2), min: +l.min.toFixed(2), max: +l.max.toFixed(2), range: +l.range.toFixed(2) };
  // the floor behind the firing line, every half metre
  out.floorBack = [];
  for (let d = 0; d <= 9; d += 0.5) {
    const y = down(at(0, -d, 0), fy + 1.5, 3);
    out.floorBack.push(`${d}:${y === null ? '-' : (y - fy).toFixed(2)}`);
  }
  // the counter: walk forward at knee height from 2 m back until something stops you
  const cast = (p, dir, far = 22) => {
    ray.set(p, dir.clone().normalize()); ray.far = far;
    const hit = ray.intersectObjects(near, true)[0];
    ray.far = Infinity;
    return hit ? { d: +hit.distance.toFixed(2), n: hit.object.name || hit.object.parent?.name } : { d: far };
  };
  out.counterFromBehind = { knee: cast(at(0, -2, fy + 0.5), rail), chest: cast(at(0, -2, fy + 1.2), rail),
    head: cast(at(0, -2, fy + 1.7), rail) };
  // the sheet
  const sh = l.papers[0];
  sh.updateWorldMatrix(true, false);
  const bb = new T.Box3().setFromObject(sh);
  out.sheet = { centreAboveFloor: +(bb.getCenter(new T.Vector3()).y - fy).toFixed(2),
    size: bb.getSize(new T.Vector3()).toArray().map((v) => +v.toFixed(2)),
    bottom: +(bb.min.y - fy).toFixed(2), downRange: +bb.getCenter(new T.Vector3()).sub(l.stand).dot(rail).toFixed(2) };
  // lateral room, at 1.2 m, at several distances behind the line
  out.lateral = {};
  for (const d of [0.9, 2, 3, 4, 5, 6]) {
    out.lateral[`b${d}`] = { right: cast(at(0, -d, fy + 1.2), right).d, left: cast(at(0, -d, fy + 1.2), right.clone().negate()).d,
      back: cast(at(0, -d, fy + 1.2), rail.clone().negate()).d, up: cast(at(0, -d, fy + 1.2), new T.Vector3(0, 1, 0)).d };
  }
  // sightlines to the three sheets from the spots the moves use
  const sheets = g.lanes.lanes.map((o, i) => {
    const s = o.papers[0];
    if (!s) return null;
    s.updateWorldMatrix(true, false);
    const c = new T.Box3().setFromObject(s).getCenter(new T.Vector3());
    const side = c.clone().sub(l.stand).dot(right);
    return { i, c, side };
  }).filter((s) => s && Math.abs(s.side) < 2 && s.c.distanceTo(l.stand) < 14);
  const see = (eye) => sheets.map((s) => {
    const d = s.c.clone().sub(eye);
    const len = d.length();
    ray.set(eye, d.normalize()); ray.far = len - 0.15;
    const hit = ray.intersectObjects(near, true)[0];
    ray.far = Infinity;
    return `${s.i}(x${s.side.toFixed(1)}):${hit ? `BLOCK@${hit.distance.toFixed(1)}` : 'ok'}`;
  }).join(' ');
  out.sight = {
    counter: see(at(0, -0.9, fy + 1.3)),
    rollBack: see(at(0, -4.6, fy + 1.3)),
    jumpRight: see(at(2.6, -4.6, fy + 1.8)),
    spin: see(at(2.6, -4.6, fy + 1.3)),
    slideLow: see(at(0.3, -1.2, fy + 0.5)),
    slideLow2: see(at(0.3, -2.0, fy + 0.6)),
  };
  // the control box and anything else on this counter
  const boxes = [];
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.visible) return;
    const n = o.name || '';
    if (!/control|button|panel|monitor|screen|console/i.test(n)) return;
    o.updateWorldMatrix(true, false);
    const b = new T.Box3().setFromObject(o);
    const c = b.getCenter(new T.Vector3());
    if (c.distanceTo(l.stand) > 3) return;
    boxes.push({ n, x: +c.clone().sub(l.stand).dot(right).toFixed(2), z: +c.clone().sub(l.stand).dot(rail).toFixed(2),
      y: +(c.y - fy).toFixed(2), size: b.getSize(new T.Vector3()).toArray().map((v) => +v.toFixed(2)) });
  });
  out.controls = boxes;
  // every distinct mesh name within 2.5 m of the stand, for orientation
  const names = new Map();
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.visible) return;
    o.updateWorldMatrix(true, false);
    const b = new T.Box3().setFromObject(o);
    if (b.isEmpty() || b.distanceToPoint(l.stand) > 1.5) return;
    const key = (o.name || '?').replace(/_dup_\d+/, '').replace(/\d+$/, '');
    if (!names.has(key)) names.set(key, { y: [+(b.min.y - fy).toFixed(2), +(b.max.y - fy).toFixed(2)],
      z: [+(b.min.clone().sub(l.stand).dot(rail)).toFixed(2), +(b.max.clone().sub(l.stand).dot(rail)).toFixed(2)] });
  });
  out.nearby = Object.fromEntries(names);
  // DIVIDERS DOWN RANGE: from the lane's centre line, how far to each side at
  // chest height, every metre from the counter to the backstop
  out.dividers = [];
  for (const z of [-1.5, -0.5, 0.3, 1, 2, 3, 5, 8, 10]) {
    out.dividers.push(`${z}:R${cast(at(0, z, fy + 1.2), right, 6).d}/L${cast(at(0, z, fy + 1.2), right.clone().negate(), 6).d}`
      + `/up${cast(at(0, z, fy + 1.2), new T.Vector3(0, 1, 0), 9).d}`);
  }
  // the near-left target: lane L-1's sheet brought in to 2.5 m. Seen from where a
  // slide would be shooting from (gun at ~0.85 m)?
  const left = sheets.find((s) => s.side < -1 && s.side > -2.2);
  if (left) {
    const ln = g.lanes.lanes[left.i];
    const nearPos = left.c.clone().addScaledVector(ln.rail, -(11.05 - 2.5));
    const seeFrom = (eye) => {
      const d = nearPos.clone().sub(eye); const len = d.length();
      ray.set(eye, d.normalize()); ray.far = len - 0.15;
      const hit = ray.intersectObjects(near, true)[0]; ray.far = Infinity;
      return hit ? `BLOCK@${hit.distance.toFixed(1)}(${hit.object.name || hit.object.parent?.name})` : 'ok';
    };
    out.nearLeftAt2_5 = {
      slide4: seeFrom(at(0.4, -4.0, fy + 0.85)), slide3: seeFrom(at(0.4, -3.0, fy + 0.85)),
      slide2: seeFrom(at(0.4, -2.2, fy + 0.85)), slide15: seeFrom(at(0.4, -1.5, fy + 0.85)),
      counter: seeFrom(at(0, -0.9, fy + 1.3)), spinR: seeFrom(at(3.0, -5.2, fy + 1.3)),
      rollBack: seeFrom(at(0, -5.2, fy + 1.3)),
    };
    // and the far ones from the spin spot
    const far = (s) => { const d = s.c.clone().sub(at(3.0, -5.2, fy + 1.3)); const len = d.length();
      ray.set(at(3.0, -5.2, fy + 1.3), d.normalize()); ray.far = len - 0.15;
      const hit = ray.intersectObjects(near, true)[0]; ray.far = Infinity;
      return `${s.i}:${hit ? `BLOCK@${hit.distance.toFixed(1)}` : 'ok'}`; };
    out.farFromSpin = sheets.map(far).join(' ');
  }
  // where the control box is: anything on the counter top, left or right of centre
  out.onCounter = [];
  for (const x of [-0.6, -0.4, -0.2, 0, 0.2, 0.4, 0.6]) {
    const y = down(at(x, -0.05, 0), fy + 1.6, 1.0);
    out.onCounter.push(`${x}:${y === null ? '-' : (y - fy).toFixed(2)}`);
  }
  return out;
}, LI);
console.log(JSON.stringify(r, null, 1));

// two stills: the hero at the firing point, from behind and from the lane
await page.evaluate(async (LI) => {
  const g = window.__game;
  await g.foot.ready;
  const l = g.lanes.lanes[LI];
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.plane.position.set(l.stand.x + 30, l.stand.y + 2, l.stand.z + 30);
  if (!g.foot.active) g.foot.toggle();
  const yaw = Math.atan2(-l.rail.x, -l.rail.z);
  const p = l.stand.clone().addScaledVector(l.rail, -1.0);
  g.foot._stand(p.x, 465.44, p.z, yaw);
}, LI);
await page.waitForTimeout(900);
await page.screenshot({ path: `${OUT}/cinema/range_behind.png` });
await page.evaluate(() => { window.__game.look.yaw = Math.PI; window.__game.look.dragging = true; });
await page.waitForTimeout(900);
await page.screenshot({ path: `${OUT}/cinema/range_front.png` });
await ctx.close();
