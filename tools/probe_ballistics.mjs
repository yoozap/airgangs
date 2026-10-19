// Bullet physics, measured in the running game.
//
// Takes a pistol off the warehouse bench, teleports the hero into a city street,
// then fires at the things a player will actually shoot -- a facade, a window,
// the road square-on and the road at a glancing angle -- and reports what the
// round did: time of flight, what the surface was classified as, the size of
// the hole it stamped, how deep it got, and whether it dug in, punched through
// or skipped off. Then swaps pistols and does it all again.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/probe_ballistics.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/3842693f-a8a0-4010-9e7d-498b3a7bbc50/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/ball-profile', {
  headless: true,
  viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('  [pageerror]', e.message); });

await enterGame(page, { user: 'ballbot', pass: 'ballbot123' });
await page.waitForTimeout(16000);

// ---- take a named pistol off the bench ------------------------------------
const equip = (want) => page.evaluate(async (id) => {
  const g = window.__game;
  const THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null;
  g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (!t) return { err: 'no warehouse table in the scene' };
  t.updateWorldMatrix(true, true);
  const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(tp.x, tp.y + 1.6, tp.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.pickups.scan();
  const it = g.pickups.items.find((x) => x.def.id === id);
  if (it) {
    // The two pistols sit ~0.45 m apart on the bench and the take-card only
    // ever offers the NEAREST one, so stand right on top of the one we want and
    // close in until it is the one on offer.
    const wp = it.obj.getWorldPosition(new THREE.Vector3());
    let got = false;
    for (let k = 0; k < 4 && !got; k++) {
      g.foot.pos.set(wp.x, tp.y + 2, wp.z + 0.3 - k * 0.1);
      for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
      g.pickups.update(0.016, g.foot.pos);
      if (g.pickups.near?.def?.id === id) { g.pickups.take(); got = true; }
    }
    if (!got) return { err: `could not get near ${id}; nearest is ${g.pickups.near?.def?.id}` };
  }
  // Draw it straight onto the hand bone. select() both TOGGLES and debounces
  // re-presses, so calling it right after take() (which already called it)
  // either does nothing or holsters what we just picked up.
  const want = g.weapons.carried[1];
  if (want && !g.foot.held) g.foot.drawWeapon(want);
  for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
  const h = g.foot.held;
  return {
    held: h?.name ?? null,
    cart: g.pistol.cart && {
      label: g.pistol.cart.label, bulletMM: g.pistol.cart.bullet, caseMM: g.pistol.cart.case,
      massG: g.pistol.cart.mass, v0: g.pistol.cart.v0,
      energyJ: Math.round(g.pistol.cart.energy),
      supersonic: g.pistol.cart.supersonic, mag: g.pistol.cart.mag,
    },
    muzzleLocal: h?._muzzle,
  };
}, want);

// ---- stand the hero a few metres off a real building facade ---------------
// Shooting at the sky proves nothing, and the spawn point is out on the airport
// apron with nothing within 160 m. So: pick a real building out of the city's
// own records, drop the hero outside it, and raycast back at its middle to find
// where the facade actually is.
const faceAWall = (standOff = 6) => page.evaluate((off) => {
  const g = window.__game;
  const THREE = window.__THREE;
  // The biggest block in the city: deterministic (so runs are comparable) and
  // tough enough to stand through a magazine instead of falling over mid-test.
  let rec = null;
  for (const m of g.world.buildingMeshes ?? []) {
    for (const r of m.userData.buildings ?? []) if (!rec || r.maxHp > rec.maxHp) rec = r;
  }
  if (!rec) return { err: 'no building records' };

  const span = Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz);
  const mid = new THREE.Vector3(rec.cx, rec.base + 5, rec.cz);
  const ray = new THREE.Raycaster();
  const isBld = (o) => { for (; o; o = o.parent) if (o.userData?.buildings) return true; return false; };
  // approach from eight sides, keep the one whose facade is easiest to stand off
  let best = null;
  for (let k = 0; k < 16; k++) {
    const a = (k / 16) * Math.PI * 2;
    const out = new THREE.Vector3(Math.sin(a), 0, Math.cos(a));
    const from = mid.clone().addScaledVector(out, span + 40);
    const gy = g._groundAt(from.x, from.z, from.y + 60);
    if (gy === null) continue;
    from.y = gy + 1.6;
    ray.set(from, mid.clone().sub(from).normalize());
    ray.far = span + 80;
    const targets = g.world.collidablesNear(from.x, from.z).concat(g.world.shootables ?? []);
    const h = ray.intersectObjects(targets, true).find((x) => isBld(x.object));
    if (!h) continue;
    if (!best || h.distance < best.distance) {
      best = { distance: h.distance, point: h.point.clone(), out, from };
    }
  }
  if (!best) return { err: 'could not see the facade from any side' };

  const stand = best.point.clone().addScaledVector(best.out, off);
  const gy = g._groundAt(stand.x, stand.z, best.point.y + 40) ?? best.point.y;
  g.plane.position.set(stand.x, gy + 2, stand.z);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(stand.x, gy + 0.02, stand.z);
  // gameplay forward is -Z, so the yaw that looks back AT the wall is this
  const look = best.out.clone().negate();
  g.foot.yaw = Math.atan2(-look.x, -look.z);
  g.foot.camYaw = g.foot.yaw;
  g.vel.set(0, 0, 0); g.speed = 0; g.airborne = false;
  for (let i = 0; i < 80; i++) g.foot.update(1 / 60);
  return {
    building: g.world.addressOf?.(rec) || '(unnamed)',
    heightM: +(rec.top - rec.base).toFixed(1),
    stoodOff: off,
    facadeAt: [+best.point.x.toFixed(1), +best.point.y.toFixed(1), +best.point.z.toFixed(1)],
    heroY: +gy.toFixed(1),
  };
}, standOff);

// ---- fire once, stepping the sim by hand so flight time is measured -------
const shoot = (o) => page.evaluate(async (opt) => {
  const g = window.__game;
  const THREE = window.__THREE;
  const impacts = [];
  const prev = g.bullets.onImpact;
  g.bullets.onImpact = (info) => {
    const m = info.hit.object.material;
    const mm = Array.isArray(m) ? m[info.hit.face?.materialIndex ?? 0] : m;
    impacts.push({
      surface: info.surface,
      mesh: info.hit.object.name || '(unnamed)',
      mat: `${mm?.name || mm?.type || '?'}${mm?.metalness !== undefined ? ` metal=${mm.metalness}` : ''}`,
      rangeM: +info.range.toFixed(2),
      speed: Math.round(info.speed),
      energyJ: Math.round(info.energy),
      grazeDeg: +(info.graze * 180 / Math.PI).toFixed(1),
      holeMM: +(info.holeW * 1000).toFixed(1),
      longMM: +(info.holeL * 1000).toFixed(1),
      penCM: +(info.depth * 100).toFixed(2),
      verdict: info.ricochet ? 'RICOCHET' : info.through ? 'THROUGH' : 'lodged',
      dmg: info.damage,
    });
    prev(info);
  };
  g.foot.yaw = opt.yaw; g.foot.camYaw = opt.yaw;
  g.look.yaw = 0; g.look.pitch = opt.pitch ?? 0;
  for (let i = 0; i < 14; i++) g.foot.update(1 / 60);
  g.centreAim();
  g.pistol.setAiming(true);
  g.pistol._bloom = 0; g.pistol._cool = 0;
  const fired = g.pistol.fire();
  let steps = 0;
  while (g.bullets.live.length && steps < 900) { g.bullets.update(1 / 240); steps++; }
  g.bullets.onImpact = prev;
  const last = (L) => {
    if (!L.mesh.count) return null;
    const m = new THREE.Matrix4(); const s = new THREE.Vector3();
    L.mesh.getMatrixAt((L.cursor - 1 + L.max) % L.max, m);
    m.decompose(new THREE.Vector3(), new THREE.Quaternion(), s);
    return `${L.mesh.count}@${(s.x * 1000).toFixed(0)}x${(s.y * 1000).toFixed(0)}mm`;
  };
  return {
    fired, tofS: +(steps / 240).toFixed(3), impacts,
    decals: { spall: last(g.impacts.spall), pit: last(g.impacts.pits),
      scuff: last(g.impacts.scuffs), star: last(g.impacts.stars), exit: last(g.impacts.exits) },
    casings: g.impacts.casings.list.length,
  };
}, o);

const show = (tag, r) => {
  console.log(`\n  ${tag}  (flight ${r.tofS}s, brass on the ground: ${r.casings})`);
  if (!r.impacts.length) console.log('    nothing hit');
  for (const i of r.impacts) {
    console.log(`    ${i.surface.padEnd(8)} ${String(i.rangeM).padStart(7)} m  v=${String(i.speed).padStart(3)}  E=${String(i.energyJ).padStart(4)} J  `
      + `graze=${String(i.grazeDeg).padStart(5)}°  hole ${i.holeMM}x${i.longMM} mm  pen ${i.penCM} cm  ${i.verdict.padEnd(8)} dmg=${i.dmg}`);
    console.log(`             on "${i.mesh}" [${i.mat}]`);
  }
  console.log('    decals:', JSON.stringify(r.decals));
};

for (const gun of ['makarov', 'beretta']) {
  const e = await equip(gun);
  console.log(`\n================ ${gun}`);
  console.log(`  ${JSON.stringify(e)}`);
  if (!e.held) { console.log('  !! not in hand, skipping'); continue; }
  const where = await faceAWall(6);
  console.log(`  ${JSON.stringify(where)}`);
  if (where.err) continue;
  const yaw = await page.evaluate(() => window.__game.foot.yaw);
  // NB: positive look.pitch aims DOWN (game.js adds mouse-down to it).
  show('level into the facade', await shoot({ yaw, pitch: 0 }));
  show('up at the windows', await shoot({ yaw, pitch: -0.34 }));
  show('up at the windows, higher', await shoot({ yaw, pitch: -0.52 }));
  show('down at the pavement', await shoot({ yaw, pitch: 0.75 }));
  show('GLANCING along the pavement', await shoot({ yaw: yaw + 1.57, pitch: 0.05 }));
  show('very glancing', await shoot({ yaw: yaw + 1.57, pitch: 0.025 }));
}

// ---- a wall full of holes, photographed from close up ---------------------
const closeUp = async (tag, off, shots) => {
  const w = await faceAWall(off);
  if (w.err) { console.log('  no wall for the close-up:', w.err); return; }
  await page.evaluate(async (n) => {
    const g = window.__game;
    g.pistol.setAiming(true);
    for (let k = 0; k < n; k++) {
      g.pistol._cool = 0;
      g.pistol._rounds = 0;            // no reload pause in the middle of a test
      g.look.yaw = (Math.random() - 0.5) * 0.085;
      g.look.pitch = (Math.random() - 0.5) * 0.075;
      for (let i = 0; i < 3; i++) g.foot.update(1 / 60);
      g.pistol.fire();
      for (let i = 0; i < 160; i++) g.bullets.update(1 / 240);
    }
    g.look.yaw = 0; g.look.pitch = 0;
    g.pistol.setAiming(false);
    for (let i = 0; i < 40; i++) g.foot.update(1 / 60);
  }, shots);
  await page.waitForTimeout(1600);
  await page.screenshot({ path: `${DIR}/${tag}.png` });
  console.log(`  screenshot -> ${DIR}/${tag}.png  (wall ${w.wallAt} m, stood off ${off} m)`);
};
console.log('\n---- close-ups');
await closeUp('holes_wall_6m', 6, 26);
await closeUp('holes_wall_2m', 2.2, 10);
await ctx.close();
