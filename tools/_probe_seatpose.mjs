// THE SEATED POSE, HELD STILL AND STILL ALIVE. After the fix that freezes the
// pose the seat is built on, three things have to be true at once:
//   1. nothing drifts -- the bones stop moving when the controls stop moving
//      (the pilot used to wind round for ever: CharacterRig.freezePose)
//   2. the hands still FOLLOW the stick, so the pose is solved and not frozen
//   3. standing up gives the body back to the mixer: it walks again
//   node tools/_probe_seatpose.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text().slice(0, 150)); });
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(2500);

const out = await page.evaluate(async () => {
  const g = window.__game; const THREE = window.__THREE;
  const rig = g.foot.rig;
  const bones = [];
  rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
  const snap = () => bones.map((b) => b.quaternion.clone());
  const drift = (a, b) => Math.max(...bones.map((_, i) =>
    2 * Math.acos(Math.min(1, Math.abs(a[i].dot(b[i])))) * 180 / Math.PI));
  const hand = () => {
    const h = rig._handPoint?.('R');
    if (!h) return null;
    h.updateWorldMatrix(true, false);
    return new THREE.Vector3().setFromMatrixPosition(h.matrixWorld);
  };
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));

  // 1. sitting still
  await wait(600);
  const a = snap();
  await wait(2000);
  const still = drift(a, snap());

  // 2. the hands follow the stick: hold it hard back, then hard forward.
  // IN THE AIR: _flyControls reads no stick at all while the machine is
  // parked and stopped, so on the apron there is nothing for a hand to
  // follow and the test would pass by doing nothing.
  g.airborne = true;
  g.speed = 60;
  const real = g._readStick?.bind(g);
  g._readStick = () => ({ pitch: 1, roll: 0, rudder: 0 });
  await wait(900);
  const back = hand();
  g._readStick = () => ({ pitch: -1, roll: 1, rudder: 0 });
  await wait(900);
  const fwd = hand();
  g._readStick = real;
  await wait(600);
  const b = snap();
  await wait(1500);
  const stillAgain = drift(b, snap());

  // 3. on foot again
  g.foot._stand?.(g.plane.position.x + 6, g.plane.position.y - 1.7, g.plane.position.z + 2, 0);
  await wait(500);
  const c = snap();
  g.foot.keys = g.foot.keys ?? {};
  await wait(1200);
  const walking = drift(c, snap());

  return {
    still: +still.toFixed(2),
    stillAgain: +stillAgain.toFixed(2),
    handMoved: back && fwd ? +back.distanceTo(fwd).toFixed(3) : null,
    afoot: !!g.foot.active, seated: !!g.foot.seated,
    aliveOnFoot: +walking.toFixed(2),
    frozen: rig._frozen?.size ?? 0,
    planeId: g.plane?.userData?.planeId ?? null,
    nanPos: !Number.isFinite(g.plane?.position?.x ?? 0) || !Number.isFinite(g.foot?.pos?.x ?? 0),
  };
});
console.log(`state                 : seated ${out.seated}, afoot ${out.afoot}, plane ${out.planeId}`);
console.log(`seated and still      : ${out.still}° of drift over 2 s  (was 100-180° every 0.4 s)`);
console.log(`...and still again    : ${out.stillAgain}° over 1.5 s after working the stick`);
console.log(`hand follows the stick: ${out.handMoved} m between hard back and hard forward`);
console.log(`back on foot          : afoot ${out.afoot}, frozen bones ${out.frozen}, ${out.aliveOnFoot}° of movement`);
console.log(errs.length ? `page errors: ${[...new Set(errs)].slice(0, 5).join(' | ')}` : 'no page errors');
await browser.close();
