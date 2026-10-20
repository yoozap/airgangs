// IS THE HOLE REAL, ON EVERY BODY SHAPE? Not "did the code run" — does the
// metal actually disappear from the picture?
//
// For each of the fleet's machines: stand the lens off a point on its body,
// READ THE RENDERED PIXELS, punch a hole there, read them again. If the metal
// went, the picture changed. Anything else — a shader hook that was not
// found, a material that was never patched, a model whose skin is built some
// way the patch does not reach — shows up as "no change".
//   node tools/_probe_realholes.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

// PLANES=an225,b767 restricts the run; OUT=... saves a before/after pair for
// any spot where the metal did not visibly go.
const ONLY = (process.env.PLANES || '').split(',').filter(Boolean);
const OUT = process.env.OUT || '';
// LOOSE=1 drops the line-of-sight requirement: for a machine wedged between
// two others there may be no clear stand-off at all, and the shot is worth
// looking at by eye even when the probe cannot certify the framing.
const LOOSE = process.env.LOOSE === '1';
if (OUT) fs.mkdirSync(`${OUT}/realholes`, { recursive: true });

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => {
  const t = m.text();
  if (t.startsWith('plane holes:')) console.log('  !', t);
});
await enterGame(page);

// The loop owns the camera, and it also owns the only moment the drawing
// buffer is readable: straight after the render, in the same task. So the
// pixel grab happens there.
await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  window.__grab = null;
  window.__pix = null;
  const two = document.createElement('canvas');
  two.width = 96; two.height = 96;
  const ctx = two.getContext('2d', { willReadFrequently: true });
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.plane.position.set(p.x, p.y, p.z);
    }
    tick();
    if (!window.__pin) return;
    const c = g.camera;
    c.position.set(p.cx, p.cy, p.cz);
    c.lookAt(p.lx, p.ly, p.lz);
    c.updateMatrixWorld(true);
    g.renderer.render(g.scene, c);
    if (window.__grab) {
      // the middle of the frame, where the aimed-at point lands
      const cv = g.renderer.domElement;
      const s = Math.min(cv.width, cv.height) * 0.22;
      ctx.clearRect(0, 0, 96, 96);
      ctx.drawImage(cv, (cv.width - s) / 2, (cv.height - s) / 2, s, s, 0, 0, 96, 96);
      window.__pix = Array.from(ctx.getImageData(0, 0, 96, 96).data);
      window.__grab = null;
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

const grab = async () => {
  await page.evaluate(() => { window.__grab = 1; window.__pix = null; });
  await page.waitForFunction(() => window.__pix !== null, null, { timeout: 8000 });
  return page.evaluate(() => window.__pix);
};
// A hole in a white aeroplane shows the white inside of its far skin, so the
// change per pixel can be a handful of levels and still be plainly a hole --
// a coarse threshold called that "no change" on the 767's wing when the hole
// was right there in the picture. Count a gentle delta, and carry the mean
// as well so a small bright change is not lost in a big flat frame.
const diff = (a, b) => {
  let n = 0;
  let sum = 0;
  for (let i = 0; i < a.length; i += 4) {
    const d = Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2]);
    sum += d;
    if (d > 8) n++;
  }
  const px = a.length / 4;
  return { pct: (100 * n) / px, mean: sum / px };
};

const n = await page.evaluate(() => window.__game.targets.list.length);
console.log(`fleet: ${n} machines — a hole punched at three places on each,`);
console.log('and the rendered pixels compared before and after\n');
console.log('plane              skin/glass  mats  compiled  hooks                 spine  wing  side  verdict');
console.log('-----------------  ----------  ----  --------  --------------------  -----  ----  ----  -------');
let bad = 0;

// Three places on every machine, in its OWN frame: the top of the body, out
// on a wing, and the side of the body. "all planes body shapes" means the
// hole has to appear wherever it is put, not just on whatever mesh happens
// to be biggest.
const SPOTS = [
  ['spine', (half, size) => [0, size * 0.05]],
  ['wing', (half, size) => [half * 0.45, 0]],
  ['side', (half, size) => [half * 0.1, -size * 0.12]],
];

for (let i = 0; i < n; i++) {
  const head = await page.evaluate((idx) => {
    const g = window.__game;
    const rec = g.targets.list[idx];
    window.__rec = rec;
    if (!rec?.wrapper?.parent) return { id: rec?.def?.id ?? `#${idx}`, skip: 'not in the world' };
    const parts = g.planeDamage._partsOf(rec);
    return { id: rec.def.id, skin: parts.skin.length, glass: parts.glass.length };
  }, i);
  if (head.skip) { console.log(`${head.id.padEnd(17)}  ${head.skip}`); bad++; continue; }
  if (ONLY.length && !ONLY.includes(head.id)) continue;

  const shots = [];
  for (const [label, at] of SPOTS) {
    const aim = await page.evaluate(({ idx, lab, ax, az, loose }) => {
      const THREE = window.__THREE;
      const g = window.__game;
      const rec = g.targets.list[idx];
      const parts = g.planeDamage._partsOf(rec);
      if (!parts.skin.length) return { skip: 'no skin' };
      const w = rec.wrapper;
      const half = w.userData.halfSpan ?? 6;
      const size = w.userData.size ?? 12;
      const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
      // straight down onto the airframe at this station; walk a little if the
      // first try falls between things
      // THE BODY, not whatever the ray touches first. These models are full
      // of little parts -- a rigging pin, an aerial, a bomb on a rack -- and
      // a hole punched on one of those is a fair hole in an unfair test: it
      // is two centimetres of aeroplane and it barely changes the picture.
      // Only the machine's big meshes count as a body shape.
      const big = [];
      const bb = new THREE.Box3();
      for (const o of parts.skin) {
        bb.setFromObject(o, true);
        const d = bb.getSize(new THREE.Vector3()).length();
        if (d > 1.2) big.push(o);
      }
      const onto = big.length ? big : parts.skin;
      // ...and the lens must actually END UP LOOKING AT IT. Standing the
      // camera two metres off the surface normal can put it the other side of
      // a hangar wall or inside the machine next door, and then the test
      // measures a picture of a wall: the carpet's "no hole" was a shot of
      // the shed. Each candidate point is kept only if the view from it
      // reaches this aeroplane first.
      let hit = null;
      let nrm = null;
      let eye = null;
      let p = null;
      for (const slide of [0, 0.08, -0.08, 0.18, -0.18, 0.3, -0.3, 0.45, -0.45]) {
        const from = new THREE.Vector3(ax * half + slide * half, 40, az * size + slide * size)
          .applyMatrix4(w.matrixWorld);
        const h = new THREE.Raycaster(from, down, 0, 110).intersectObjects(onto, false)[0] ?? null;
        if (!h) continue;
        const nn = h.face
          ? h.face.normal.clone().transformDirection(h.object.matrixWorld).normalize()
          : new THREE.Vector3(0, 1, 0);
        if (nn.dot(down) > 0) nn.negate();
        const pp = h.point.clone();
        // These machines are parked wingtip to wingtip, so two metres off the
        // surface is often inside the one next door. Come closer rather than
        // give up -- but never inside the camera's own metre of near plane.
        // ...and from whichever direction is clear. Straight out along the
        // surface normal is the obvious one and in a packed hall it is often
        // straight into the aeroplane parked alongside, so the approach is
        // also tried leaning upward, where the air over the fleet is empty.
        let ee = null;
        const up = new THREE.Vector3(0, 1, 0);
        const ways = [
          nn.clone(),
          nn.clone().lerp(up, 0.45).normalize(),
          nn.clone().lerp(up, 0.8).normalize(),
          up.clone(),
        ];
        for (const way of ways) {
          for (const back of [2.2, 1.7, 1.35]) {
            const cand = pp.clone().addScaledVector(way, back);
            const look = new THREE.Raycaster(cand, pp.clone().sub(cand).normalize(), 0, back + 1);
            const all = parts.skin.concat(g.world.collidablesNear(cand.x, cand.z));
            const first = look.intersectObjects(all, true)[0];
            if (first && parts.skin.includes(first.object)) { ee = cand; break; }
          }
          if (ee) break;
        }
        if (!ee && loose) ee = pp.clone().addScaledVector(nn, 1.7);
        if (!ee) continue;                       // nothing but neighbours from here
        hit = h; nrm = nn; eye = ee; p = pp;
        break;
      }
      if (!hit) return { skip: 'no clear view' };
      window.__pin = { x: p.x, y: p.y + 400, z: p.z + 900,
        cx: eye.x, cy: eye.y, cz: eye.z, lx: p.x, ly: p.y, lz: p.z };
      window.__hole = { px: p.x, py: p.y, pz: p.z, nx: nrm.x, ny: nrm.y, nz: nrm.z };
      return { on: hit.object.name || '(unnamed)' };
    }, { idx: i, lab: label, ax: at(1, 1)[0], az: at(1, 1)[1], loose: LOOSE });
    if (aim.skip) { shots.push({ label, pct: null }); continue; }
    await page.waitForTimeout(240);
    const before = await grab();
    if (OUT) await page.screenshot({ path: `${OUT}/realholes/${head.id}_${label}_0.png` });
    await page.evaluate(() => {
      const THREE = window.__THREE;
      const g = window.__game;
      const rec = window.__rec;
      const h = window.__hole;
      const w = rec.wrapper;
      const lp = w.worldToLocal(new THREE.Vector3(h.px, h.py, h.pz));
      const inv = new THREE.Matrix4().copy(w.matrixWorld).invert();
      const ln = new THREE.Vector3(h.nx, h.ny, h.nz).transformDirection(inv).normalize();
      const list = ((rec._tears ??= {}).fus ??= []);
      list.push({ x: lp.x, y: lp.y, z: lp.z, nx: ln.x, ny: ln.y, nz: ln.z,
        n: 9, cal: 0.05, r: 0.3, bridge: 0.3, mark: null, part: 'fus' });
      g.planeDamage._syncHoles(rec);
    });
    await page.waitForTimeout(340);
    const after = await grab();
    const d = diff(before, after);
    const weak = d.pct < 8 && d.mean < 1.5;
    if (OUT && LOOSE) await page.screenshot({ path: `${OUT}/realholes/${head.id}_${label}_1.png` });
    if (OUT && weak) {
      await page.screenshot({ path: `${OUT}/realholes/${head.id}_${label}_1.png` });
      console.log(`    ${head.id}/${label}: ${d.pct.toFixed(1)}% mean ${d.mean.toFixed(1)} on ${aim.on}`);
    }
    shots.push({ label, pct: d.pct, mean: d.mean, weak });
  }

  // read the shader's own report AFTER it has had a frame to compile
  const sh = await page.evaluate(() => {
    const set = window.__rec._holeSet;
    return {
      mats: set?.mats.size ?? 0, done: set?.done ?? 0, failed: set?.failed ?? 0,
      v: (set?.vertexHook ?? '-').replace('#include <', '').replace('>', ''),
      f: (set?.fragmentHook ?? '-').replace('#include <', '').replace('>', ''),
    };
  });
  const cell = (s) => (s.pct === null ? '  miss' : `${s.pct.toFixed(0).padStart(3)}%`);
  const tried = shots.filter((s) => s.pct !== null);
  const good = tried.filter((s) => !s.weak).length;
  const ok = sh.failed === 0 && good === tried.length && good > 0;
  if (!ok) bad++;
  console.log(
    `${head.id.padEnd(17)}  ${String(head.skin).padStart(4)}/${String(head.glass).padEnd(5)}`
    + `  ${String(sh.mats).padStart(4)}  ${String(sh.done).padStart(3)} ok,${String(sh.failed).padStart(2)}`
    + `  ${`${sh.v}/${sh.f}`.slice(0, 20).padEnd(20)}`
    + `  ${shots.map(cell).join('  ')}  ${ok ? 'hole' : 'CHECK <<<'}`
  );
}
console.log(`\n${bad === 0 ? `REAL HOLES ON ALL ${n} MACHINES` : `${bad} machine(s) to look at`}`);
await browser.close();
