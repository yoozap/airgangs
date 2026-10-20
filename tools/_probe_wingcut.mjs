// SAWING A WING OFF. Walk a line of bullets across a parked machine's wing,
// leading edge to trailing edge: does the wing come away along the row, does
// the piece tumble, do the holes that cut it go with it, and does a SPRAY of
// the same number of rounds leave it attached?
//   node tools/_probe_wingcut.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT || '/tmp';
const PLANE = process.env.PLANE || 'spitfire';
fs.mkdirSync(`${OUT}/wingcut`, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('[err]', m.text().slice(0, 160)); });
await enterGame(page);

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

// THE WING, measured. The row has to cross a real wing, so the probe looks
// for a spanwise station well outboard of the fuselage and takes the longest
// CONTIGUOUS run of skin fore and aft at it — first-to-last picks up the
// fuselage as well and calls a 2 m chord 6 m wide, which is how the first run
// of this probe shot a line into thin air past the trailing edge.
const wing = await page.evaluate((wanted) => {
  const THREE = window.__THREE;
  const g = window.__game;
  const rec = g.targets.list.find((r) => r.def.id === wanted) ?? g.targets.list[0];
  window.__rec = rec;
  const parts = g.planeDamage._partsOf(rec);
  const w = rec.wrapper;
  const half = w.userData.halfSpan ?? 6;
  const size = w.userData.size ?? 12;
  const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
  const hitAt = (lx, lz) => {
    const from = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
    return new THREE.Raycaster(from, down, 0, 80).intersectObjects(parts.skin, false)[0] ?? null;
  };
  const N = 60;
  const out = [];
  for (const side of [-1, 1]) {
    for (const f of [0.4, 0.5, 0.6, 0.7]) {
      const lx = side * half * f;
      let best = null;
      let run = null;
      for (let k = 0; k <= N; k++) {
        const lz = (k / N - 0.5) * size * 0.9;
        const h = hitAt(lx, lz);
        if (h) {
          if (!run) run = { lo: lz, hi: lz, on: h.object.name };
          else run.hi = lz;
        } else if (run) {
          if (!best || run.hi - run.lo > best.hi - best.lo) best = run;
          run = null;
        }
      }
      if (run && (!best || run.hi - run.lo > best.hi - best.lo)) best = run;
      if (best && best.hi - best.lo > 0.4) {
        out.push({ side, lx, f, lo: best.lo, hi: best.hi, chord: best.hi - best.lo, on: best.on });
      }
    }
  }
  // The most OUTBOARD station that is still a real wing: the fattest one is
  // often the fuselage side or a wing root fairing, and a "chord" five metres
  // deep is the machine's body, not its wing.
  out.sort((a, b) => b.f - a.f || b.chord - a.chord);
  const best = out[0] ?? null;
  window.__wing = best;
  return { id: rec.def.id, name: rec.def.name, hp: rec.hp, halfSpan: +half.toFixed(2),
    fusHalf: +(g.planeDamage._cutter(rec).lines().fusHalf).toFixed(2),
    tailZ: +(g.planeDamage._cutter(rec).lines().tailZ).toFixed(2),
    stations: out.map((o) => ({ lx: +o.lx.toFixed(2), chord: +o.chord.toFixed(2), on: o.on })),
    // the mirror of the chosen station, measured on its OWN wing rather than
    // assumed: these models are not symmetric to the centimetre, and a row
    // walked down a mirrored guess falls off the trailing edge
    other: out.find((o) => o.side === -best?.side && o.f === best?.f) ?? null,
    best };
}, PLANE);
console.log('wing:', JSON.stringify(wing));
if (!wing.best) { console.log('no wing station found — stopping'); await browser.close(); process.exit(0); }

// Fire one round straight down at a point on the wing, from above the fleet.
async function shootAt(lx, lz, { from = 7 } = {}) {
  return page.evaluate(({ lx, lz, from }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const w = window.__rec.wrapper;
    const parts = g.planeDamage._partsOf(window.__rec);
    const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
    const top = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
    const h = new THREE.Raycaster(top, down, 0, 80).intersectObjects(parts.skin, false)[0];
    if (!h) return { miss: true };
    const p = h.point.clone();
    const off = new THREE.Vector3(0.12, 1, 0.05).normalize();
    const eye = p.clone().addScaledVector(off, from);
    window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
      cx: eye.x, cy: eye.y, cz: eye.z, lx: p.x, ly: p.y, lz: p.z };
    const dir = p.clone().sub(eye).normalize();
    g.bullets.spawn(g.pistol.cart, eye.clone(), dir, g.world.collidablesNear(eye.x, eye.z),
      { from: 'probe' });
    return { on: h.object.name };
  }, { lx, lz, from });
}

function state(tag) {
  return page.evaluate((tag) => {
    const g = window.__game;
    const rec = window.__rec;
    const rows = rec._tears ?? {};
    const cut = g.planeDamage;
    return {
      tag,
      hp: rec.hp, maxHp: rec.maxHp, dead: !!rec.dead,
      rows: Object.fromEntries(Object.entries(rows).map(([k, v]) =>
        [k, rec._cutOff?.has(k) ? 'CUT' : `${(v ?? []).length}t/${(v ?? []).reduce((a, t) => a + t.n, 0)}r`])),
      marksOnPlane: (rec._marks ?? []).filter((m) => !m.dropped).length,
      why: cut._why,
      riders: cut.riders.length,
      marksOnPieces: cut.riders.reduce((a, r) => a + r.marks.length, 0),
      pieces: cut.wrecks.reduce((a, w) => a + w.parts.length, 0),
    };
  }, tag);
}

// 1. A SPRAY, on the RIGHT wing: the same number of rounds, scattered. It
//    must NOT cut — a cut is a line, not a quantity of holes. (On its own
//    wing, so a spray that wrongly cuts cannot then invalidate the line test.)
const b = wing.best;
const chord = b.chord;
console.log(`station lx=${b.lx.toFixed(2)} chord≈${chord.toFixed(2)} m on ${b.on}`);
const o = wing.other ?? { lx: -b.lx, lo: b.lo, hi: b.hi, chord: b.chord };
const sprayX = o.lx;      // the other wing, measured on its own
// as many rounds as the chord needs — a 9 m An-225 wing is not perforated by
// the eight that cut a Spitfire
// one round every 12 cm or so: the metal a 9 mm ruins beyond its own hole
// (src/planedamage.js bridgeOf) is about that, so this is what it takes to
// perforate a chord with a PISTOL. A rifle or the aeroplane's gun needs far
// fewer, which is the point of them.
const ROUNDS = Math.min(34, Math.max(8, Math.ceil(chord / 0.12)));
console.log(`${ROUNDS} rounds per pass`);
for (let i = 0; i < ROUNDS; i++) {
  await shootAt(sprayX + (Math.random() - 0.5) * 1.6, o.lo + Math.random() * o.chord);
  await page.waitForTimeout(150);
}
await page.waitForTimeout(900);
const spray = await state('spray');
console.log(`SPRAY (must NOT cut) -> ${spray.rows.wingR === 'CUT' || spray.rows.wingL === 'CUT' ? 'FAIL' : 'ok'}`,
  JSON.stringify(spray));
await page.screenshot({ path: `${OUT}/wingcut/1_spray.png` });

// 2. NOW THE LINE, on the left wing: eight rounds walked across the chord.
for (let i = 0; i < ROUNDS; i++) {
  const t = i / (ROUNDS - 1);
  await shootAt(b.lx, b.lo + 0.06 * chord + t * chord * 0.88);
  await page.waitForTimeout(160);
}
await page.waitForTimeout(1400);
const cut = await state('line');
const cutSide = b.lx < 0 ? 'wingL' : 'wingR';
console.log(`LINE (must cut) -> ${cut.rows[cutSide] === 'CUT' ? 'ok' : 'FAIL'}`, JSON.stringify(cut));
await page.screenshot({ path: `${OUT}/wingcut/2_cut.png` });

// ...the stump, and the piece that came off it carrying the row that cut it.
async function frame(name, what, pad = 1.9) {
  const got = await page.evaluate(({ what, pad }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = window.__rec;
    let obj = rec.wrapper;
    if (what === 'piece') {
      const r = g.planeDamage.riders[g.planeDamage.riders.length - 1];
      if (!r) return { none: true };
      obj = r.obj;
    }
    obj.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(obj, true);
    const c = box.getCenter(new THREE.Vector3());
    const r = Math.max(1, box.getSize(new THREE.Vector3()).length() * 0.5);
    const d = Math.max(1.6, r * pad);
    window.__pin = { x: c.x, y: c.y + 400, z: c.z + 900,
      cx: c.x + d * 0.6, cy: c.y + d * 0.65, cz: c.z + d * 0.6,
      lx: c.x, ly: c.y, lz: c.z };
    return { r: +r.toFixed(2), d: +d.toFixed(2) };
  }, { what, pad });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/wingcut/${name}.png` });
  console.log(`  ${name}:`, JSON.stringify(got));
}
await frame('2a_stump', 'plane', 1.7);
await frame('2b_piece', 'piece', 2.2);

// 3. Watch the piece come down, from a step back.
await page.evaluate(() => {
  const THREE = window.__THREE;
  const p = window.__rec.wrapper.getWorldPosition(new THREE.Vector3());
  window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
    cx: p.x + 14, cy: p.y + 6, cz: p.z + 14, lx: p.x, ly: p.y, lz: p.z };
});
await page.waitForTimeout(1200);
await page.screenshot({ path: `${OUT}/wingcut/3_falling.png` });
await page.waitForTimeout(2500);
await page.screenshot({ path: `${OUT}/wingcut/4_down.png` });
console.log('settled ->', JSON.stringify(await state('settled')));

// 4. ...and a line across the wing that was SPRAYED, to prove the earlier
//    scatter does not poison the fit: the row the player just drew is what
//    counts, not everything that ever hit that wing.
for (let i = 0; i < ROUNDS; i++) {
  const t = i / (ROUNDS - 1);
  await shootAt(sprayX, o.lo + 0.06 * o.chord + t * o.chord * 0.88);
  await page.waitForTimeout(160);
}
await page.waitForTimeout(1600);
const second = await state('line-over-spray');
const sprayed = sprayX < 0 ? 'wingL' : 'wingR';
console.log(`LINE OVER SPRAY (must cut) -> ${second.rows[sprayed] === 'CUT' ? 'ok' : 'FAIL'}`,
  JSON.stringify(second));
await page.screenshot({ path: `${OUT}/wingcut/5_both.png` });

console.log('shots in', `${OUT}/wingcut`);
await browser.close();
