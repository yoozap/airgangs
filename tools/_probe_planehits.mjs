// SHOOTING AN AEROPLANE. Stand beside a parked machine in the plane hall and
// put real rounds into it: does the round land on the SKIN (and not on the
// invisible box shell), is it classified as aluminium rather than concrete,
// does the canopy craze and then go, and do the holes sit on the airframe?
//   node tools/_probe_planehits.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT || '/tmp';
const PLANE = process.env.PLANE || 'spitfire';
fs.mkdirSync(`${OUT}/planehits`, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => {
  const t = m.text();
  if (m.type() === 'error') console.log('[err]', t.slice(0, 200));
  else if (t.startsWith('HIT ')) console.log(' ', t);
});
await enterGame(page);

// The loop owns the camera: pin the pose from inside the tick.
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
  // EVERY round, wherever it landed -- so a volley that misses the aeroplane
  // says what it hit instead rather than reporting a silent zero.
  window.__all = [];
  const onImpact = g.bullets.onImpact;
  g.bullets.onImpact = (info) => {
    window.__all.push(`${info.surface}:${info.hit?.object?.name || '?'}`);
    return onImpact(info);
  };
  // every round that lands on an aeroplane, as the game saw it
  window.__hits = [];
  const mark = g.planeDamage.mark.bind(g.planeDamage);
  g.planeDamage.mark = (info) => {
    window.__hits.push({
      part: info.hit.planePart, surface: info.surface ?? '(trace)',
      mesh: info.hit.object?.name, through: !!info.through, ric: !!info.ricochet,
      hole: +(info.holeW * 1000).toFixed(1), mul: info.hit.planeMul,
      dmg: info.damage, hull: info.S?.hull ?? 1,
    });
    return mark(info);
  };
});

await page.waitForFunction(() => {
  let hall = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') hall = o; });
  return !!hall;
}, null, { timeout: 420000 });
const hall = await page.evaluate(() => {
  const g = window.__game;
  let h = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') h = o; });
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

// What the chosen machine is made of, before a shot is fired.
const parts = await page.evaluate((wanted) => {
  const g = window.__game;
  const rec = g.targets.list.find((r) => r.def.id === wanted) ?? g.targets.list[0];
  window.__rec = rec;
  const p = g.planeDamage._partsOf(rec);
  const by = {};
  for (const o of p.all) by[o.userData.planePart] = (by[o.userData.planePart] ?? 0) + 1;
  return { id: rec.def.id, name: rec.def.name, maxHp: rec.maxHp,
    skin: p.skin.length, glass: p.glass.length, parts: by,
    glassNames: p.glass.map((o) => o.name).slice(0, 6) };
}, PLANE);
console.log('airframe:', JSON.stringify(parts));

// WHERE THE SKIN ACTUALLY IS. Guessing a point in the aeroplane's own frame
// put three volleys through thin air and into the hangar floor, so the aim
// point is probed for: straight down from well above (lx, lz), onto the real
// airframe, and whatever it lands on is what gets shot.
async function skinAt(lx, lz) {
  return page.evaluate(({ lx, lz }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = window.__rec;
    const w = rec.wrapper;
    const from = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
    const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
    const ray = new THREE.Raycaster(from, down, 0, 80);
    const h = ray.intersectObjects(g.planeDamage._partsOf(rec).all, false)[0];
    return h ? { x: h.point.x, y: h.point.y, z: h.point.z, on: h.object.name,
      part: h.object.userData.planePart } : null;
  }, { lx, lz });
}

// Stand the camera a few metres off the machine's side, looking at a point on
// it, and fire real rounds down the line of sight — the full simulation, the
// same one the pistol in the hero's hand feeds.
async function shoot(at, { from = 7, n = 6 } = {}) {
  return page.evaluate(({ at, from, n }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const p = new THREE.Vector3(at.x, at.y, at.z);
    // FROM ABOVE. Standing beside a machine in a packed hall puts the lens
    // inside the one parked next to it and every round lands on THAT one --
    // which is how the first run of this probe shot up a P-51 by accident.
    // The air over the fleet is empty, so the shot comes down onto the skin.
    const off = new THREE.Vector3(0.3, 1, 0.12).normalize();
    const eye = p.clone().addScaledVector(off, from);
    window.__pin = {
      x: p.x, y: p.y + 300, z: p.z + 900,       // our own machine, well out of it
      cx: eye.x, cy: eye.y, cz: eye.z, lx: p.x, ly: p.y, lz: p.z,
    };
    const dir = p.clone().sub(eye).normalize();
    const cart = g.pistol.cart;
    const targets = g.world.collidablesNear(eye.x, eye.z);
    for (let i = 0; i < n; i++) {
      const d = dir.clone();
      d.x += (Math.random() - 0.5) * 0.008;
      d.y += (Math.random() - 0.5) * 0.008;
      d.z += (Math.random() - 0.5) * 0.008;
      g.bullets.spawn(cart, eye.clone(), d.normalize(), targets, { from: 'probe' });
    }
    return { cart: cart.id };
  }, { at, from, n });
}

// The same camera the shot came from, brought in to arm's length: whatever
// the volley hit is dead centre of frame.
async function closeUp(name, at, dist = 1.8) {
  await page.evaluate(({ at, dist }) => {
    const THREE = window.__THREE;
    const p = new THREE.Vector3(at.x, at.y, at.z);
    const off = new THREE.Vector3(0.3, 1, 0.12).normalize();
    const eye = p.clone().addScaledVector(off, dist);
    window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
      cx: eye.x, cy: eye.y, cz: eye.z, lx: p.x, ly: p.y, lz: p.z };
  }, { at, dist });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/planehits/${name}.png` });
}

// ...and straight onto one LAYER's marks (the crazing, the exits), square to
// the first of them, so what is being judged is in the middle of the frame.
// NOT closer than ~1.3 m: the game camera's near plane is a metre, and every
// close-up taken at arm's length was clipped away to nothing.
async function layerUp(name, layer, dist = 1.4) {
  const got = await page.evaluate(({ layer, dist }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = window.__rec;
    const L = g.planeDamage[layer];
    const live = (rec._marks ?? []).filter((m) => !m.dropped && m.layer === L);
    if (!live.length) return { marks: 0 };
    const m = new THREE.Matrix4().multiplyMatrices(rec.wrapper.matrixWorld, live[0].local);
    const p = new THREE.Vector3().setFromMatrixPosition(m);
    const n = new THREE.Vector3(0, 0, 1).transformDirection(m).normalize();
    const eye = p.clone().addScaledVector(n, dist);
    window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
      cx: eye.x, cy: eye.y, cz: eye.z, lx: p.x, ly: p.y, lz: p.z };
    window.__mark = { x: p.x, y: p.y, z: p.z };
    return { marks: live.length, size: +new THREE.Vector3().setFromMatrixScale(m).x.toFixed(3) };
  }, { layer, dist });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/planehits/${name}.png` });
  const where = await page.evaluate(() => {
    const THREE = window.__THREE;
    const g = window.__game;
    const m = window.__mark;
    const L = g.planeDamage;
    return {
      camToMark: +g.camera.position.distanceTo(new THREE.Vector3(m.x, m.y, m.z)).toFixed(2),
      fov: g.camera.fov, near: g.camera.near,
      counts: { holes: L.holes.mesh.count, exits: L.exits.mesh.count,
        craze: L.craze.mesh.count, scrapes: L.scrapes.mesh.count },
      crazeVisible: L.craze.mesh.visible && !!L.craze.mesh.parent,
    };
  });
  console.log(`  ${name}:`, JSON.stringify(got), JSON.stringify(where));
}

// ARE THE MARKS ACTUALLY ON THE AEROPLANE? The whole point of refine() is
// that a hole lands on the skin and not on the invisible crate around it, and
// that is a measurement, not a matter of opinion: from each mark, a short ray
// back along its own normal has to strike this machine's airframe within a
// centimetre or two.
async function marksOnSkin() {
  return page.evaluate(() => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = window.__rec;
    const live = (rec._marks ?? []).filter((m) => !m.dropped);
    const parts = g.planeDamage._partsOf(rec);
    const m = new THREE.Matrix4();
    const p = new THREE.Vector3();
    const n = new THREE.Vector3();
    const ray = new THREE.Raycaster();
    let on = 0; let off = 0; let worst = 0; let loose = 0;
    for (const mk of live) {
      m.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
      p.setFromMatrixPosition(m);
      n.set(0, 0, 1).transformDirection(m).normalize();
      ray.set(p.clone().addScaledVector(n, 0.25), n.clone().negate());
      ray.far = 0.5;
      const h = ray.intersectObjects(parts.all, false)[0];
      if (h) {
        on++;
        const gap = Math.abs(h.distance - 0.25);
        if (gap > 0.02) loose++;
        worst = Math.max(worst, gap);
      } else off++;
    }
    return { marks: live.length, onSkin: on, floating: off,
      over2cm: loose, worstGap: +worst.toFixed(4) };
  });
}

// ...AND THEY RIDE IT. Shove the airframe half a street sideways and the
// holes have to go with it -- that is the whole reason the marks are held in
// the aeroplane's own frame rather than stamped on the world.
async function marksFollow() {
  const moved = await page.evaluate(() => {
    const w = window.__rec.wrapper;
    w.matrixAutoUpdate = true;
    w.position.x += 60;
    w.position.y += 12;
    w.rotation.y += 0.7;
    w.updateMatrixWorld(true);
    window.__game.planeDamage.update(0.016);
    return true;
  });
  const after = await marksOnSkin();
  await page.evaluate(() => {
    const w = window.__rec.wrapper;
    w.position.x -= 60;
    w.position.y -= 12;
    w.rotation.y -= 0.7;
    w.updateMatrixWorld(true);
    window.__game.planeDamage.update(0.016);
  });
  return { moved, after };
}

function report(tag) {
  return page.evaluate((tag) => {
    const g = window.__game;
    const rec = window.__rec;
    const hits = window.__hits.splice(0);
    const kinds = {};
    for (const h of hits) kinds[`${h.part}/${h.surface}`] = (kinds[`${h.part}/${h.surface}`] ?? 0) + 1;
    return {
      tag,
      landed: hits.length,
      kinds,
      sample: hits[0] ?? null,
      all: window.__all.splice(0),
      dealt: hits.reduce((a, h) => a + (h.dmg ?? 0) * (h.mul ?? 1), 0),
      ram: document.getElementById('tf-ram')?.textContent,
      hp: rec.hp, maxHp: rec.maxHp,
      canopyGone: !!rec.canopyGone,
      marks: (rec._marks ?? []).length,
      holes: g.planeDamage.holes.mesh.count,
      exits: g.planeDamage.exits.mesh.count,
      craze: g.planeDamage.craze.mesh.count,
      scrapes: g.planeDamage.scrapes.mesh.count,
    };
  }, tag);
}

// 1. THE CANOPY. Probed for straight down over the measured seat.
const seat = await page.evaluate(() => {
  const c = window.__rec.wrapper.userData.cockpit;
  return c ? { x: c.seat.x, z: c.seat.z } : { x: 0, z: 0 };
});
const glass = await skinAt(seat.x, seat.z);
console.log('canopy at:', JSON.stringify(glass));
if (glass) {
  await shoot(glass, { from: 7, n: 3 });
  await page.waitForTimeout(1200);
  console.log('canopy x3 ->', JSON.stringify(await report('canopy3')));
  await layerUp('canopy_crazed', 'craze', 1.35);
  await shoot(glass, { from: 7, n: 3 });
  await page.waitForTimeout(1200);
  console.log('canopy x6 ->', JSON.stringify(await report('canopy6')));
  await page.screenshot({ path: `${OUT}/planehits/canopy_gone.png` });
}

// 2. THE WING, square on from above.
let wing = null;
for (const [lx, lz] of [[2.2, 0.4], [1.6, 0], [2.6, -0.4], [1.2, 0.8], [-2, 0.2]]) {
  wing = await skinAt(lx, lz);
  if (wing && wing.part === 'skin') break;
  wing = null;
}
console.log('wing at:', JSON.stringify(wing));
if (wing) {
  await shoot(wing, { from: 6, n: 8 });
  await page.waitForTimeout(1400);
  console.log('wing x8 ->', JSON.stringify(await report('wing')));
  await page.screenshot({ path: `${OUT}/planehits/wing.png` });
  await closeUp('wing_close', wing, 2.2);
}

// 3. THE FUSELAGE SPINE, behind the cockpit.
const spine = await skinAt(0, seat.z + 1.6);
console.log('spine at:', JSON.stringify(spine));
if (spine) await page.evaluate((p) => { window.__spine = p; }, spine);
if (spine) {
  await shoot(spine, { from: 6, n: 8 });
  await page.waitForTimeout(1400);
  console.log('fuselage x8 ->', JSON.stringify(await report('fuselage')));
  await closeUp('fuselage_close', spine, 2.2);
  await layerUp('fuselage_exit', 'exits', 1.35);
  await layerUp('fuselage_hole', 'holes', 1.3);
  console.log('  marks on the skin ->', JSON.stringify(await marksOnSkin()));
  console.log('  after moving the machine ->', JSON.stringify(await marksFollow()));
}

// 4. THE AEROPLANE'S OWN 12.7 mm. A different road entirely -- hitscan with a
// tracer, its own flat damage figure -- but it has to land on the real skin
// and mark it the same way (impactfx markHit -> planedamage trace).
const belly = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  const rec = window.__rec;
  const at = window.__spine;
  const p = new THREE.Vector3(at.x, at.y, at.z);
  const dir = new THREE.Vector3(0.3, -1, 0.12).normalize();
  const from = p.clone().addScaledVector(dir, -14);
  const ray = new THREE.Raycaster(from, dir, 0, 60);
  const hit = ray.intersectObjects(g.world.collidablesNear(from.x, from.z), true)[0];
  if (!hit) return { err: 'the gun ray found nothing' };
  const before = rec.hp;
  const marks0 = (rec._marks ?? []).length;
  g.guns._impact({ hit, dir, pos: from, dist: 0, maxDist: 0 });
  return {
    onCollider: hit.object.name, before, after: rec.hp, took: before - rec.hp,
    newMarks: (rec._marks ?? []).length - marks0,
  };
});
console.log('12.7 mm round ->', JSON.stringify(belly));

// 4b. THE SAME PLACE, TWELVE TIMES. "when i am shooting in 1 place this dot
//     must be more big": one tear that grows, not twelve stamps in a heap.
const grow = [];
for (let i = 0; i < 12; i++) {
  await shoot(spine, { from: 6, n: 1 });
  await page.waitForTimeout(220);
  grow.push(await page.evaluate(() => {
    const rec = window.__rec;
    let big = null;
    for (const list of Object.values(rec._tears ?? {})) {
      if (!list) continue;
      for (const t of list) if (!big || t.n > big.n) big = t;
    }
    const marks = (rec._marks ?? []).filter((m) => !m.dropped).length;
    return big ? { n: big.n, r: +(big.r * 1000).toFixed(1), marks } : { none: true, marks };
  }));
}
console.log('one spot, 12 rounds ->', JSON.stringify(grow[0]), '...', JSON.stringify(grow[11]));
await closeUp('grown_tear', spine, 1.6);

// 4c. THE AEROPLANE GOES AWAY AND SO DOES ITS DAMAGE. "dots of plane shoots
//     are in air": the hall switches its fleet off past 480 m and the marks
//     live in the scene, so they used to hang there over an empty floor.
const hidden = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  const rec = window.__rec;
  const m = new THREE.Matrix4();
  const scaleOf = () => {
    const live = (rec._marks ?? []).filter((x) => !x.dropped);
    if (!live.length) return null;
    live[0].layer.mesh.getMatrixAt(live[0].i, m);
    return +new THREE.Vector3().setFromMatrixScale(m).x.toFixed(4);
  };
  let fleet = null;
  g.scene.traverse((o) => { if (o.name === 'planehall_fleet') fleet = o; });
  const before = scaleOf();
  if (fleet) fleet.visible = false;
  g.planeDamage.update(0.016);
  const off = scaleOf();
  if (fleet) fleet.visible = true;
  g.planeDamage.update(0.016);
  const back = scaleOf();
  return { foundFleet: !!fleet, shownScale: before, hiddenScale: off, restored: back };
});
console.log('fleet switched off ->', JSON.stringify(hidden));

// 5. EMPTY IT. The hull bar falls, the engine smokes, then it goes.
const killed = await page.evaluate(() => {
  const g = window.__game;
  const rec = window.__rec;
  const steps = [];
  for (let k = 0; k < 60 && !rec.dead; k++) {
    g.world.damageHit({ object: rec.collider, point: rec.wrapper.position }, 48);
    if (k % 8 === 0) steps.push(rec.hp);
  }
  return { steps, hp: rec.hp, dead: rec.dead };
});
console.log('emptied with the 12.7 ->', JSON.stringify(killed));
await page.waitForTimeout(2000);
await page.evaluate(() => {
  const THREE = window.__THREE;
  const p = window.__rec.wrapper.getWorldPosition(new THREE.Vector3());
  window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
    cx: p.x + 16, cy: p.y + 7, cz: p.z + 16, lx: p.x, ly: p.y + 1, lz: p.z };
});
await page.waitForTimeout(1500);
await page.screenshot({ path: `${OUT}/planehits/dead.png` });

console.log('shots in', `${OUT}/planehits`);
await browser.close();
