// The pilot in the cockpit: is the personage seated in the plane, at the right
// height, facing the nose, with its head in the glass instead of through it?
//   MODEL=banana PLANE=aerobatic node tools/shot_cockpit.mjs
//
// Three frames per plane — from the side, from three quarters, and from above
// — plus the measured numbers: how far the crown of the head is below the
// canopy top (positive = it fits) and where the hips sit.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/cockpit';
fs.mkdirSync(DIR, { recursive: true });
const SHEET = process.env.PLANE === 'all';
const PLANES = SHEET ? null : (process.env.PLANE || 'aerobatic').split(',');

const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 1100, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(4000);

const ids = PLANES ?? await page.evaluate(async () => (await import('/src/planes.js')).PLANES.map((p) => p.id));
for (const id of ids) {
  const info = await page.evaluate(async (id) => {
    const g = window.__game, THREE = window.__THREE;
    const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
    await g.foot.ready;
    // whichever personage is under test — the hero's own body otherwise
    if (window.__SHOT_MODEL && g.foot._modelId !== window.__SHOT_MODEL) {
      g.foot.leaveSeat();
      g.foot.rig?.obj.parent?.remove(g.foot.rig.obj);
      g.foot._modelId = window.__SHOT_MODEL;
      await g.foot._loadBody();
    }
    // swap the aircraft under the hero, exactly as a hangar change would
    if (g.plane.userData.id !== id) {
      const def = planeById(id);
      const next = makeGameplayPlane(await loadPlane(id), def);
      next.position.copy(g.plane.position);
      next.quaternion.copy(g.plane.quaternion);
      next.userData.id = id;
      g.scene.remove(g.plane);
      g.plane = next;
      g.scene.add(next);
      g.halfSpan = next.userData.halfSpan;
      g.foot.seated = false;
    }
    // WAIT FOR THE FURNITURE. fitCockpitKit attaches the seat, stick, panel
    // and pedals from a promise, so a freshly swapped aeroplane has none of
    // them for a microtask or two -- and a pilot seated before they arrive is
    // a pilot whose hands were never solved onto anything, which measures a
    // different (taller) body. The real game always has them: the aeroplane
    // is built long before anyone climbs in.
    for (let i = 0; i < 60 && !g.plane.userData.controls; i++) {
      await new Promise((r) => setTimeout(r, 25));
    }
    // parked and level: a banked aeroplane makes every vertical reading a lie
    g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
    g.plane.quaternion.identity();
    g.plane.updateMatrixWorld(true);
    if (g.foot.active) g.foot._board(); else g.foot.sitInPlane();
    g.foot.updateSeated(0.016);
    g.renderer.setAnimationLoop(null);
    for (let i = 0; i < 30; i++) g.foot.updateSeated(1 / 60);
    g.plane.updateMatrixWorld(true);
    const c = g.plane.userData.cockpit;
    // Head against canopy, both in WORLD y with the aircraft sitting level —
    // c.top is in the plane's pre-belly-drop frame, so it is converted the
    // same way the seat was.
    // THE BODY'S OWN BOX, not setFromObject(..., true). `precise` walks the
    // GEOMETRY's vertices, and on these skinned bodies those are the undeformed
    // FBX-scale ones -- so this printed a crown half a metre out and called a
    // correctly seated astronaut "0.06 m through the hood". CharacterRig
    // measures itself properly (it deforms the skin first).
    const lb = g.foot.rig._localBox();
    const head = new THREE.Box3(
      lb.min.clone().add(g.foot.rig.obj.position),
      lb.max.clone().add(g.foot.rig.obj.position)).applyMatrix4(g.plane.matrixWorld);
    const planeY = g.plane.position.y;
    const canopyY = planeY + (c ? c.top : 0);
    return {
      id, grow: +(c?.grow ?? 1).toFixed(2), room: +(c?.room ?? 0).toFixed(2),
      seat: c ? [+c.seat.y.toFixed(2), +c.seat.z.toFixed(2)] : null,
      hips: +(g.foot.rig.sit ?? 0).toFixed(2),
      sitTop: +(g.foot.rig.sitTop ?? 0).toFixed(3), sitBot: +(g.foot.rig.sitBot ?? 0).toFixed(3),
      legs: !!g.foot.rig._crouchRig(),
      body: g.foot.rig.id ?? '?',
      canopyTop: +(canopyY - planeY).toFixed(2),
      crown: +(head.max.y - planeY).toFixed(2),
      clear: +(canopyY - head.max.y).toFixed(2),
      span: +(g.plane.userData.halfSpan * 2).toFixed(2),
      from: c?.from ?? '-',
    };
  }, id);
  console.log(`${info.id.padEnd(11)} x${info.grow} span ${info.span} m  seat y${info.seat?.[0]} z${info.seat?.[1]}`
    + `  hips+${info.hips}${info.legs ? '' : ' (legs will not fold)'}`
    + `  canopy ${info.canopyTop}  head ${info.crown}  clearance ${info.clear} m  [sitTop ${info.sitTop} sitBot ${info.sitBot}]  ${info.from}`);

  // THE AEROPLANE AND THE PILOT, AND NOTHING ELSE. A cockpit seen past a HUD,
  // a city and a wing is a cockpit you cannot judge: everything but the plane
  // and whoever is sitting in it is hidden for the frame and put back after.
  // the sheet wants ONE tight frame per plane, close in on the cockpit
  const views = SHEET ? [['side', [1, 0.15, -0.25], 0.42]] : [
    ['inside', [0.75, 0.75, 0.55], 0.17],
    ['panel', [0.40, 0.55, -0.80], 0.16],
    ['side', [1, 0.1, 0], 1.1],
    ['quarter', [0.8, 0.5, -0.7], 1.2],
    ['front', [0.15, 0.25, -1], 1.0],
  ];
  for (const [name, dir, zoom] of views) {
    await page.evaluate(({ dir, zoom }) => {
      const g = window.__game, THREE = window.__THREE;
      const c = g.plane.userData.cockpit;
      const hidden = [];
      for (const o of g.scene.children) {
        if (o === g.plane || o === g.foot.rig?.obj || o.isLight) continue;
        if (o.visible) { hidden.push(o); o.visible = false; }
      }
      g.scene.background = new THREE.Color(0x202830);
      g.scene.fog = null;
      document.getElementById('hud')?.classList.add('hidden');
      const box = new THREE.Box3().setFromObject(g.plane, true);
      const r = box.getSize(new THREE.Vector3()).length() * 0.5;
      const at = g.plane.localToWorld(new THREE.Vector3(0, (c?.top ?? 1) - 0.4, c?.seat.z ?? 0));
      const d = new THREE.Vector3(...dir).normalize().multiplyScalar(r * zoom);
      g.camera.position.copy(at).add(d);
      g.camera.lookAt(at);
      g.camera.updateMatrixWorld();
      g.renderer.render(g.scene, g.camera);
      window.__restore = () => { for (const o of hidden) o.visible = true; };
    }, { dir, zoom });
    await page.screenshot({ path: `${DIR}/${id}_${name}.png` });
  }
  await page.evaluate(() => { window.__restore?.(); document.getElementById('hud')?.classList.remove('hidden'); });
}
console.log(`frames in ${DIR}`);
await ctx.close();
