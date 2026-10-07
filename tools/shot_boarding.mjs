// The climb: hood up, personage onto the wing, down into the seat, hood shut
// — and the same backwards. Captures the whole move as a strip of frames.
//   MODEL=banana PLANE=aerobatic node tools/shot_boarding.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/boarding';
fs.mkdirSync(DIR, { recursive: true });
const ID = process.env.PLANE || 'aerobatic';

const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 900, height: 620 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(4000);

await page.evaluate(async (id) => {
  const g = window.__game, THREE = window.__THREE;
  const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
  await g.foot.ready;
  if (window.__SHOT_MODEL && g.foot._modelId !== window.__SHOT_MODEL) {
    g.foot.leaveSeat();
    g.foot.rig?.obj.parent?.remove(g.foot.rig.obj);
    g.foot._modelId = window.__SHOT_MODEL;
    await g.foot._loadBody();
  }
  const next = makeGameplayPlane(await loadPlane(id), planeById(id));
  next.position.copy(g.plane.position);
  next.quaternion.identity();
  g.scene.remove(g.plane);
  g.plane = next; g.scene.add(next);
  g.halfSpan = next.userData.halfSpan;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.foot.seated = false;
  // start ON FOOT, a few metres off the port wing, facing the aircraft
  g.foot.sitInPlane();
  g.foot._exitPlane(true);
  g.renderer.setAnimationLoop(null);
  // a fixed camera off the port bow: the whole climb happens in one shot
  const c = next.userData.cockpit;
  const at = next.localToWorld(new THREE.Vector3(-1, c.deck, c.seat.z));
  g.camera.position.copy(at).add(new THREE.Vector3(-7.5, 3.2, 7.5));
  g.camera.lookAt(at);
  window.__hold = () => { g.camera.position.copy(at).add(new THREE.Vector3(-7.5, 3.2, 7.5)); g.camera.lookAt(at); };
  document.getElementById('hud')?.classList.add('hidden');
}, ID);

// step the world by hand so the strip is evenly spaced
const frames = [];
const tick = async (n) => page.evaluate((n) => {
  const g = window.__game;
  for (let i = 0; i < n; i++) {
    const dt = 1 / 60;
    if (g.foot.active) g.foot.update(dt); else g.foot.updatePilot(dt);
    if (g.foot.rig) g.foot.rig.obj.updateMatrixWorld(true);
    const want = g.foot._boardSeq || g.foot._boardAfter ? 1 : 0;
    g._hoodT = (g._hoodT ?? want) + (want - (g._hoodT ?? want)) * Math.min(1, dt * 3.4);
    window.__setHood(g.plane, g._hoodT);
  }
  window.__hold();
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
  return { seated: !!g.foot.seated, seq: g.foot._boardSeq ? +g.foot._boardSeq.t.toFixed(2) : null,
    hood: +(g._hoodT ?? 0).toFixed(2) };
}, n);

await page.evaluate(async () => {
  const { setHoodOpen } = await import('/src/planes.js');
  window.__setHood = setHoodOpen;
});

const shoot = async (tag, i) => {
  const f = `${DIR}/${tag}_${String(i).padStart(2, '0')}.png`;
  await page.screenshot({ path: f });
  frames.push(f);
};

// The climb takes as long as it takes — the walk is distance-based and the
// hang/pull/mantle is the real one — so the strip is driven by STATE, not by
// a frame count. Toggling out on a timer caught the hero still climbing in
// and the whole "out" strip was the tail of the way in.
const busy = () => page.evaluate(() => {
  const f = window.__game.foot;
  return !!(f._boardSeq || f._boardAfter || f._climb);
});
const shootUntil = async (tag, done, max = 26) => {
  for (let i = 0; i < max; i++) {
    const r = await tick(12);
    await shoot(tag, i);
    if (i % 5 === 0) console.log(' ', JSON.stringify(r));
    if (await done()) { await tick(20); await shoot(tag, i + 1); return; }
  }
  console.log(`  (${tag}: never settled)`);
};
console.log('--- climbing IN');
await page.evaluate(() => window.__game.foot.toggle());
await shootUntil('in', async () => !(await busy()));
console.log('--- climbing OUT');
await page.evaluate(() => window.__game.foot.toggle());
await shootUntil('out', async () => !(await busy()));
console.log(`frames in ${DIR}`);
await ctx.close();
