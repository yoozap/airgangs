// PLANE AGAINST PLANE. Flies one machine into another at a real closing speed
// and prints what the collision solver actually did to both of them: the hull
// each one lost, how fast the struck machine was thrown, whether it came apart
// and whether the rammer kept flying.
//
// The point is the mass ratio. An An-225 into an Extra 330 must plough on
// through with a dent; the same two the other way round must kill the Extra.
//   node tools/_probe_ram.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

// The ram is staged in CLEAR AIR, 400 m over the hall: the machine being hit
// is lifted up there with its collider re-baked around it, and the rammer
// starts 90 m out. Nothing else is within reach, so what the numbers show is
// the collision and not a hangar wall — and the wreck then has a real fall,
// which tests the loose body, the fire and the arrival as well.
const CASES = [
  // me          them         closing m/s  gap m   stage
  ['an225',     'aerobatic',  100, 70, 'air'],   // the giant walks through the toy
  ['b767',      'cessna310',   90, 60, 'air'],   // airliner vs light twin
  ['kawasaki',  'an225',      100, 60, 'air'],   // ...and a light one against the giant
  ['spitfire',  'f4f',        100, 50, 'air'],   // two equals: both should die
  ['f15',       'mig21',       55, 40, 'air'],   // a survivable fighter-on-fighter hit
  ['t28',       'p47',          4, 12, 'taxi'],  // a taxi nudge: nobody is hurt
];

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3500);

// build the hall where the fleet lives rather than flying out to trigger it
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30,
  null, { timeout: 420000 });
const fleet = await page.evaluate(() => window.__game.targets.list.map((r) => r.def.id));
console.log(`${fleet.length} parked: ${fleet.join(' ')}\n`);

// the card's own numbers, for one machine of each size
const card = await page.evaluate(() => {
  const g = window.__game;
  const out = [];
  for (const id of ['aerobatic', 'spitfire', 'f15', 'b767', 'an225']) {
    const rec = g.targets.list.find((r) => r.def.id === id);
    if (!rec) continue;
    g.targets.set(rec);
    const txt = (i) => document.getElementById(i)?.textContent ?? '?';
    out.push({ id, stam: txt('tf-stam'), speed: txt('tf-speed'), ram: txt('tf-ram'), mass: txt('tf-mass') });
  }
  g.targets.clear();
  return out;
});
console.log('TARGET CARD');
for (const c of card) {
  console.log(`  ${c.id.padEnd(10)} 🛡 ${String(c.stam).padStart(5)}   ⚡ ${String(c.speed).padStart(11)}`
    + `   💥 ${String(c.ram).padStart(5)}   ⚖ ${c.mass}`);
}

// log every solve, with both sides' numbers taken around the call
await page.evaluate(() => {
  const g = window.__game;
  window.__ramlog = [];
  const orig = g.planephys.ram.bind(g.planephys);
  g.planephys.ram = (rec, hit, d, sp, part) => {
    const h0 = g.health; const t0 = rec.hp;
    const r = orig(rec, hit, d, sp, part);
    window.__ramlog.push({
      them: rec.def.id, part: part ?? 'nose', closing: +sp.toFixed(1),
      lost: +(h0 - g.health).toFixed(0), theyLost: +(t0 - rec.hp).toFixed(0),
      ourSpeed: +g.speed.toFixed(1), thrown: rec.body ? +rec.body.vel.length().toFixed(1) : 0,
      spin: rec.body ? +rec.body.omegaV.length().toFixed(2) : 0,
      theyDead: !!rec.dead, weDied: !!g.crashed, handled: r,
    });
    return r;
  };
});

console.log('\nCOLLISIONS');
let fails = 0;
for (const [me, them, speed, gap, stage] of CASES) {
  const setup = await page.evaluate(async ({ me, them, speed, gap, stage }) => {
    const g = window.__game; const THREE = window.__THREE;
    window.__ramlog.length = 0;
    g.resetPlane();
    await new Promise((r) => setTimeout(r, 120));
    if (g.def.id !== me) {
      const mine = g.targets.list.find((r) => r.def.id === me && !r.dead && !r.loose);
      if (!mine) return { error: `${me} not available to fly` };
      if (!g.swapToPlane(mine)) return { error: `could not take the ${me}` };
      await new Promise((r) => setTimeout(r, 200));
    }
    const t = g.targets.list.find((r) => r.def.id === them);
    if (!t) return { error: `${them} not parked` };
    // put it back together if an earlier case had it
    t.hp = t.maxHp; t.dead = false;
    const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
    if (stage === 'air') {
      // lift it into clear air and re-bake its collider around it up there
      if (t.collider) {
        g.world._removeCollidable(t.collider);
        delete t.collider.userData.planeRec;
        t.collider = null;
      }
      g.scene.attach(t.wrapper);
      t.wrapper.matrixAutoUpdate = true;
      t.wrapper.position.y += 400;
      t.wrapper.updateMatrixWorld(true);
      t.ringY = undefined;
      g.planephys._bakeCollider(t);
      tp.copy(t.wrapper.getWorldPosition(new THREE.Vector3()));
    }
    const floor = tp.y - 1.7;
    const start = tp.clone().add(new THREE.Vector3(0, 0, gap));
    const aim = tp.clone();
    const v = aim.clone().sub(start).normalize().multiplyScalar(speed);
    g.health = g.maxHealth;
    g._updateHealthBar();
    g.rates.set(0, 0, 0);
    g._ramSpin.set(0, 0, 0);
    g.plane.position.copy(start);
    g._prevPos.copy(start);
    g.plane.quaternion.setFromRotationMatrix(
      new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));
    if (stage === 'air') {
      g.airborne = true;
      g.vel.copy(v);
      g.speed = speed;
      // THE RUN-IN IS FLOWN BY HAND, one 60 Hz step at a time, through the
      // game's own swept contact. Letting the flight model fly it means
      // fighting gravity for the last few metres -- with the stick centred a
      // machine sinks several metres over a 70 m approach and simply passes
      // under a light aeroplane, which is a fact about aerodynamics and not
      // about collisions. This way the shot is the same every run.
      const dt = 1 / 60;
      for (let i = 0; i < 400 && !window.__ramlog.length && !g.crashed; i++) {
        g._prevPos.copy(g.plane.position);
        g.plane.position.addScaledVector(v, dt);
        g.vel.copy(v);
        g.speed = speed;
        if (g._touchWall(dt)) break;
        if (g.plane.position.distanceTo(aim) > gap + 60) break; // flown past
      }
    } else {
      // a taxi bump: on the wheels, rolling at the target
      g.airborne = false;
      g.vel.set(0, 0, 0);
      g.plane.position.y = floor + 1.7;
      g._prevPos.copy(g.plane.position);
      g.speed = speed;
      g.sound.rpm = 0.6;
    }
    return {
      myMass: Math.round(g.aero.mass), theirMass: Math.round(g.planephys.massOf(t)),
      theirHp: t.hp, theirMax: t.maxHp, from: [tp.x, tp.y, tp.z],
    };
  }, { me, them, speed, gap, stage });
  if (setup.error) { console.log(`  ${me} -> ${them}: ${setup.error}`); fails++; continue; }

  // wait for the struck machine to finish falling, burning and arriving
  await page.waitForFunction((them) => {
    const t = window.__game.targets.list.find((r) => r.def.id === them);
    return !t || !t.loose;
  }, them, { timeout: 30000 }).catch(() => {});
  await page.waitForTimeout(stage === 'air' ? 1200 : 3000);

  const out = await page.evaluate(({ them, from }) => {
    const g = window.__game; const THREE = window.__THREE;
    const t = g.targets.list.find((r) => r.def.id === them);
    const p = t?.wrapper?.getWorldPosition(new THREE.Vector3());
    return {
      log: window.__ramlog,
      moved: p ? +p.distanceTo(new THREE.Vector3(...from)).toFixed(1) : null,
      theirHp: t?.hp ?? null, dead: !!t?.dead, loose: !!t?.loose,
      solid: !!t?.collider, parent: t?.wrapper?.parent?.name || t?.wrapper?.parent?.type,
      ourHp: Math.round(g.health), weDied: g.crashed, ourSpeed: +g.speed.toFixed(1),
      bodies: g.planephys.bodies.length,
      hint: document.getElementById('hud-controls')?.textContent?.slice(0, 50) ?? '',
      mem: {
        heap: Math.round((performance.memory?.usedJSHeapSize ?? 0) / 1e6),
        geo: g.renderer.info.memory.geometries,
        tex: g.renderer.info.memory.textures,
        prog: g.renderer.info.programs?.length ?? 0,
        calls: g.renderer.info.render.calls,
        parts: g.planephys.bodies.reduce((n, b) => n + (b.breakup?.parts.length ?? 0), 0),
      },
    };
  }, { them, from: setup.from });

  const hits = out.log.length;
  const first = out.log[0];
  const tag = `${me} (${(setup.myMass / 1000).toFixed(1)} t) -> ${them} (${(setup.theirMass / 1000).toFixed(1)} t) @ ${speed} m/s`;
  console.log(`  ${tag}`);
  if (!hits) {
    console.log(`      NO CONTACT — the solver was never called. we ${out.weDied ? 'CRASHED' : `${out.ourHp} hp`},`
      + ` ${out.ourSpeed} m/s, hint "${out.hint}"`);
    fails++;
    continue;
  }
  console.log(`      first hit: we lost ${String(first.lost).padStart(5)} hp · they lost ${String(first.theyLost).padStart(5)} hp`
    + ` · thrown ${String(first.thrown).padStart(5)} m/s · spin ${first.spin} rad/s`
    + ` · our speed ${first.ourSpeed} m/s${first.part !== 'nose' ? ` · on the ${first.part} wing` : ' · nose on'}`);
  console.log(`      after the fall: they ${out.dead ? 'DESTROYED' : `${out.theirHp}/${setup.theirMax} hp`}`
    + `, moved ${out.moved} m, ${out.loose ? 'still loose' : 'settled'}, ${out.solid ? 'solid' : 'NO COLLIDER'}, under ${out.parent}`
    + ` · we ${out.weDied ? 'CRASHED' : `${out.ourHp} hp, ${out.ourSpeed} m/s`} · ${hits} solve(s)`);
  console.log(`      heap ${out.mem.heap} MB · ${out.mem.geo} geometries · ${out.mem.tex} textures`
    + ` · ${out.mem.prog} programs · ${out.mem.calls} draws`);
}

console.log(errs.length
  ? `\n${errs.length} page errors:\n  ` + [...new Set(errs)].slice(0, 8).join('\n  ')
  : '\nno page errors');
console.log(fails ? `\n${fails} case(s) did not run` : '\nall cases ran');
await browser.close();
