// The camera must never end up inside anything — least of all the aeroplane
// the hero is standing on.  node tools/_probe_camclip.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/camclip';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 640 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.addInitScript(() => { window.__SHOT_MODEL = 'banana'; });
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(4000);

const blocked = () => page.evaluate(() => {
  const g = window.__game, THREE = window.__THREE;
  const subject = g.foot.active
    ? new THREE.Vector3(g.foot.pos.x, g.foot.pos.y + 1.4, g.foot.pos.z)
    : g.plane.position.clone();
  const dir = new THREE.Vector3().subVectors(g.camera.position, subject);
  const len = dir.length();
  dir.normalize();
  const r = new THREE.Raycaster(subject, dir, 0, len - 0.05);
  const list = g.world.collidablesNear(subject.x, subject.z);
  let hit = r.intersectObjects(list, true)[0];
  // same rule as the game: the plane's own shell only counts when the hero is
  // out of it (its origin sits on one of the merged boxes' faces)
  const sh = g.foot.active ? g.plane.userData.shell : null;
  if (sh) { const h = r.intersectObject(sh, true)[0]; if (h && (!hit || h.distance < hit.distance)) hit = h; }
  return { camDist: +len.toFixed(2), blockedAt: hit ? +hit.distance.toFixed(2) : null,
    what: hit ? `${hit.object.name || hit.object.type}` : null,
    subjectY: +subject.y.toFixed(2), planeY: +g.plane.position.y.toFixed(2),
    onFoot: !!g.foot.active, heroY: +g.foot.pos.y.toFixed(2) };
});

// stand the hero on the wing, right where the user's screenshot was taken
await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  if (!g.foot.active) { g.foot._boardSeq = null; g.foot._boardAfter = null; g.foot.sitInPlane(); g.foot._exitPlane(true); }
  g.plane.quaternion.identity(); g.plane.updateMatrixWorld(true);
  g.speed = 0; g.vel.set(0, 0, 0); g.airborne = false;
  const c = g.plane.userData.cockpit;
  const w = g.plane.localToWorld(new THREE.Vector3(-(c.halfW + 0.45), c.deck, c.seat.z - 0.3));
  const r = new THREE.Raycaster(new THREE.Vector3(w.x, w.y + 4, w.z), new THREE.Vector3(0, -1, 0), 0, 12);
  const h = r.intersectObject(g.plane.userData.shell, true)[0];
  g.foot.pos.set(w.x, (h ? h.point.y : w.y) + 0.02, w.z);
  g.foot.grounded = true; g.foot.vy = 0;
  g.foot.yaw = 0; g.foot._snapCamera();
});
await page.waitForTimeout(1500);
console.log('hero on the wing      ', JSON.stringify(await blocked()));
await page.screenshot({ path: `${DIR}/on_wing.png` });

// and seated, flying
await page.evaluate(() => { const g = window.__game; g.foot._board(); g.airborne = true; g.vel.set(0, 0, -40); });
await page.waitForTimeout(2000);
console.log('seated, flying        ', JSON.stringify(await blocked()));
await page.screenshot({ path: `${DIR}/flying.png` });
console.log(errs.length ? `page errors: ${errs.slice(0, 3).join(' | ')}` : 'no page errors');
console.log(`frames in ${DIR}`);
await ctx.close();
