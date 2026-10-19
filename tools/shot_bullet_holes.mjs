// Photograph what the pistols leave behind, from close enough to judge it.
//
// The chase camera sits 5 m behind the hero, so a 9 mm hole is a couple of
// pixels on screen -- useless for checking the decals. This fires a group into
// a wall, FREEZES the game loop, then walks a free camera right up to the
// impacts and renders those frames by hand.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_bullet_holes.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/3842693f-a8a0-4010-9e7d-498b3a7bbc50/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/holes-profile', {
  headless: true,
  viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('  [pageerror]', e.message); });

await enterGame(page, { user: 'holebot', pass: 'holebot123' });
await page.waitForTimeout(16000);

const WHICH = process.env.GUN || 'beretta';

const setup = await page.evaluate(async (want) => {
  const g = window.__game;
  const THREE = window.__THREE;
  await g.foot.ready;
  // --- pistol in hand, off the warehouse bench ---
  let t = null;
  g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (!t) return { err: 'no warehouse table' };
  t.updateWorldMatrix(true, true);
  const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(tp.x, tp.y + 1.6, tp.z + 14);
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  g.pickups.scan();
  const it = g.pickups.items.find((x) => x.def.id === want);
  if (it) {
    const wp = it.obj.getWorldPosition(new THREE.Vector3());
    g.foot.pos.set(wp.x, tp.y + 2, wp.z + 0.3);
    for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
    g.pickups.update(0.016, g.foot.pos);
    g.pickups.take();
  }
  if (!g.foot.held && g.weapons.carried[1]) g.foot.drawWeapon(g.weapons.carried[1]);

  // --- a BIG building, so it survives the group instead of falling over ---
  let rec = null;
  for (const m of g.world.buildingMeshes ?? []) {
    for (const r of m.userData.buildings ?? []) {
      if (!rec || r.maxHp > rec.maxHp) rec = r;
    }
  }
  if (!rec) return { err: 'no building records' };
  const span = Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz);
  const mid = new THREE.Vector3(rec.cx, rec.base + 4.5, rec.cz);
  const ray = new THREE.Raycaster();
  const isBld = (o) => { for (; o; o = o.parent) if (o.userData?.buildings) return true; return false; };
  // Pick the SUNLIT side. The shaded face of a Tbilisi block is almost black
  // in these frames and a 9 mm hole in it is invisible, which tells you nothing.
  const sun = g.sunDir.clone().setY(0).normalize();
  let best = null;
  for (let k = 0; k < 24; k++) {
    const a = (k / 24) * Math.PI * 2;
    const out = new THREE.Vector3(Math.sin(a), 0, Math.cos(a));
    const lit = out.dot(sun);              // 1 = facing straight into the sun
    if (lit < 0.35) continue;
    const from = mid.clone().addScaledVector(out, span + 50);
    const gy = g._groundAt(from.x, from.z, from.y + 80);
    if (gy === null) continue;
    from.y = gy + 1.6;
    ray.set(from, mid.clone().sub(from).normalize());
    ray.far = span + 90;
    const tg = g.world.collidablesNear(from.x, from.z).concat(g.world.shootables ?? []);
    const h = ray.intersectObjects(tg, true).find((x) => isBld(x.object));
    if (!h) continue;
    const score = h.distance - lit * 30;   // near AND sunlit
    if (!best || score < best.score) best = { score, d: h.distance, point: h.point.clone(), out, lit };
  }
  if (!best) return { err: 'no sunlit facade visible from any side' };

  const stand = best.point.clone().addScaledVector(best.out, 7);
  const gy = g._groundAt(stand.x, stand.z, best.point.y + 50) ?? best.point.y;
  g.plane.position.set(stand.x, gy + 2, stand.z);
  g.foot.pos.set(stand.x, gy + 0.02, stand.z);
  const look = best.out.clone().negate();
  g.foot.yaw = Math.atan2(-look.x, -look.z);
  g.foot.camYaw = g.foot.yaw;
  for (let i = 0; i < 80; i++) g.foot.update(1 / 60);
  return {
    gun: g.foot.held?.name ?? null,
    building: g.world.addressOf?.(rec) || '(unnamed)',
    hp: rec.maxHp,
    aim: [best.point.x, best.point.y, best.point.z],
    normal: [best.out.x, best.out.y, best.out.z],
    sunlit: +best.lit.toFixed(2),
  };
}, WHICH);
console.log('setup:', JSON.stringify(setup));
if (setup.err) { await ctx.close(); process.exit(1); }

// ---- fire a group, remembering exactly where each round landed -----------
// Damage is switched OFF for the duration: the point of these frames is the
// MARKS, and a facade that has been knocked down two stages and set on fire
// tells you nothing about what a bullet hole looks like.
const group = await page.evaluate(async () => {
  const g = window.__game;
  const pts = [];
  const prev = g.bullets.onImpact;
  const realDamage = g.world.damageHit;
  g.world.damageHit = () => null;
  let batch = 0;
  g.bullets.onImpact = (i) => { pts.push([i.point.x, i.point.y, i.point.z, i.surface, batch]); prev(i); };
  g.pistol.setAiming(true);
  for (let k = 0; k < 16; k++) {
    g.pistol._cool = 0;
    g.pistol._rounds = 0;                 // no reload pause mid-test
    g.look.yaw = (Math.random() - 0.5) * 0.030;
    g.look.pitch = (Math.random() - 0.5) * 0.026;
    for (let i = 0; i < 3; i++) g.foot.update(1 / 60);
    g.pistol.fire();
    for (let i = 0; i < 200; i++) g.bullets.update(1 / 240);
  }
  // a few deliberately flat ones along the pavement, for ricochet smears
  batch = 1;
  for (let k = 0; k < 8; k++) {
    g.pistol._cool = 0; g.pistol._rounds = 0;
    g.look.yaw = 1.5 + (Math.random() - 0.5) * 0.4;
    g.look.pitch = 0.035 + Math.random() * 0.02;
    for (let i = 0; i < 3; i++) g.foot.update(1 / 60);
    g.pistol.fire();
    for (let i = 0; i < 400; i++) g.bullets.update(1 / 240);
  }
  g.bullets.onImpact = prev;
  g.world.damageHit = realDamage;
  g.look.yaw = 0; g.look.pitch = 0;
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  // let the dust clear and the brass settle
  for (let i = 0; i < 180; i++) { g.impacts.update(1 / 60); }
  return { pts, hero: [+g.foot.pos.x.toFixed(1), +g.foot.pos.y.toFixed(1), +g.foot.pos.z.toFixed(1)] };
});
const heroEnd = group.hero;
const impacts = group.pts;
const counts = {};
for (const p of impacts) counts[p[3]] = (counts[p[3]] ?? 0) + 1;
console.log(`group: ${impacts.length} impacts ->`, JSON.stringify(counts),
  ' hero ended at', JSON.stringify(heroEnd));

// ---- freeze and photograph ----------------------------------------------
const frame = (name, opts) => page.evaluate(async (o) => {
  const g = window.__game;
  const THREE = window.__THREE;
  g.disposed = true;                       // stop the loop writing the camera
  // get the plane, the hero and the HUD out of the picture
  g.plane.position.set(g.plane.position.x + 400, g.plane.position.y + 220, g.plane.position.z + 400);
  if (g.foot.rig?.obj) g.foot.rig.obj.visible = false;
  g.ui.hud?.classList.add('hidden');
  document.querySelectorAll('#reticle, #pickup-card, #hit-toast, #zone-slam')
    .forEach((el) => { el.style.display = 'none'; });
  const c = new THREE.Vector3(...o.eye);
  g.camera.position.copy(c);
  g.camera.lookAt(new THREE.Vector3(...o.at));
  g.camera.fov = o.fov ?? 40;
  g.camera.near = 0.05;
  g.camera.updateProjectionMatrix();
  g.sky.position.copy(g.camera.position);   // the loop is frozen; keep the sky on us
  g.renderer.render(g.scene, g.camera);
  // how far the nearest stamped pit actually is from where we are pointing --
  // a frame with nothing in it is otherwise impossible to diagnose
  const M = new THREE.Matrix4();
  const P = new THREE.Vector3();
  let near = Infinity;
  for (const L of [g.impacts.pits, g.impacts.spall]) {
    for (let i = 0; i < L.mesh.count; i++) {
      L.mesh.getMatrixAt(i, M);
      P.setFromMatrixPosition(M);
      near = Math.min(near, P.distanceTo(c));
    }
  }
  return { eye: [+c.x.toFixed(1), +c.y.toFixed(1), +c.z.toFixed(1)],
           at: o.at.map((v) => +v.toFixed(1)), nearestPit: +near.toFixed(2) };
}, opts).then(async (info) => {
  await page.screenshot({ path: `${DIR}/${name}.png` });
  console.log(`  -> ${name}.png  ${JSON.stringify(info)}`);
});

// only the aimed group, and only its FIRST impacts: a round that skipped off
// and buried itself in a block 90 m away is still 'masonry', and averaging it
// in puts the camera in the middle of a field.
// Point the camera at THE DECALS THEMSELVES. Hunting for the facade the hero
// was aiming at turned out to be guesswork -- the stamps know exactly where
// they are and which way the wall faces, so read it off the last pit stamped.
const pitAt = (back) => page.evaluate((o) => {
  const g = window.__game;
  const THREE = window.__THREE;
  const L = g.impacts.pits;
  if (!L.mesh.count) return null;
  const M = new THREE.Matrix4();
  const P = new THREE.Vector3();
  const Q = new THREE.Quaternion();
  const S = new THREE.Vector3();
  const i = (L.cursor - 1 - o.back + L.max * 2) % L.max;
  L.mesh.getMatrixAt(i, M);
  M.decompose(P, Q, S);
  const n = new THREE.Vector3(0, 0, 1).applyQuaternion(Q);  // the surface normal
  // how many other pits are within half a metre -- i.e. is this one in the group
  let near = 0;
  const T = new THREE.Vector3();
  for (let k = 0; k < L.mesh.count; k++) {
    L.mesh.getMatrixAt(k, M);
    T.setFromMatrixPosition(M);
    if (T.distanceTo(P) < 0.6) near++;
  }
  return { p: [P.x, P.y, P.z], n: [n.x, n.y, n.z], neighbours: near };
}, { back });

// the pit with the most company is the middle of the group
let hub = null;
for (let b = 0; b < 14; b++) {
  const c = await pitAt(b);
  if (c && (!hub || c.neighbours > hub.neighbours)) hub = c;
}
console.log('group centre:', JSON.stringify(hub));
if (hub) {
  const [px, py, pz] = hub.p;
  const [nx, ny, nz] = hub.n;
  const eye = (d, up = 0) => [px + nx * d, py + ny * d + up, pz + nz * d];
  await frame('holes_close_30cm', { eye: eye(0.3), at: hub.p, fov: 55 });
  await frame('holes_close_80cm', { eye: eye(0.8, 0.05), at: hub.p, fov: 50 });
  await frame('holes_close_250cm', { eye: eye(2.5, 0.2), at: hub.p, fov: 42 });
  // from the side: a decal that floats off its wall shows up immediately here
  const sx = -nz;
  const sz = nx;
  await frame('holes_oblique', {
    eye: [px + nx * 0.75 + sx * 0.95, py + 0.15, pz + nz * 0.75 + sz * 0.95], at: hub.p, fov: 50,
  });
}
const ground = impacts.filter((p) => p[4] === 1);
if (ground.length) {
  const c = [0, 1, 2].map((i) => ground.reduce((s, p) => s + p[i], 0) / ground.length);
  await frame('ricochet_ground', { eye: [c[0], c[1] + 1.0, c[2] + 1.5], at: c, fov: 52 });
}
const brass = await page.evaluate(() => {
  const g = window.__game;
  const l = g.impacts.casings.list;
  if (!l.length) return null;
  const c = l[0];
  return {
    p: [c.p.x, c.p.y, c.p.z],
    gy: c.gy,
    groundNow: g._groundAt(c.p.x, c.p.z, c.p.y + 2, 2.4),
    bounces: c.bounces,
    resting: c.rest > 0,
    len: c.len,
    count: g.impacts.casings.mesh.count,
    inScene: !!g.impacts.casings.mesh.parent,
  };
});
console.log('brass:', JSON.stringify(brass));
if (brass) {
  await frame('brass', { eye: [brass.p[0] + 0.28, brass.p[1] + 0.22, brass.p[2] + 0.28], at: brass.p, fov: 26 });
  // where did it land in the frame? a casing that is simply not drawn and one
  // that is drawn behind the kerb look identical in a screenshot
  const ndc = await page.evaluate((p) => {
    const g = window.__game;
    const THREE = window.__THREE;
    const v = new THREE.Vector3(...p).project(g.camera);
    const cs = g.impacts.casings;
    return { ndc: [+v.x.toFixed(3), +v.y.toFixed(3), +v.z.toFixed(3)],
             count: cs.mesh.count, visible: cs.mesh.visible,
             inScene: !!cs.mesh.parent, frustumCulled: cs.mesh.frustumCulled,
             camNear: g.camera.near, dist: +g.camera.position.distanceTo(new THREE.Vector3(...p)).toFixed(3) };
  }, brass.p);
  console.log('  brass in frame:', JSON.stringify(ndc));
}
await ctx.close();
