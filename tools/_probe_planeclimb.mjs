// Is the aeroplane something you can stand on and climb, like a crate?
//   node tools/_probe_planeclimb.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/cockpit';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 700 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.addInitScript(() => { window.__SHOT_MODEL = 'banana'; });
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(4000);

console.log(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  if (g.foot._modelId !== 'banana') {
    g.foot.leaveSeat(); g.foot.rig?.obj.parent?.remove(g.foot.rig.obj);
    g.foot._modelId = 'banana'; await g.foot._loadBody();
  }
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.plane.quaternion.identity(); g.plane.updateMatrixWorld(true);
  if (!g.foot.active) { g.foot.sitInPlane(); g.foot._exitPlane(true); }
  // the shell is NOT in the world's collidables by design — it is handed to
  // the foot probes as an extra target (foot.js _planeTargets), because that
  // set is also what the aeroplane's own ground probe reads
  const inSet = (g.foot._planeTargets() ?? []).includes(g.plane.userData.shell);
  const c = g.plane.userData.cockpit;
  // FIND the wing rather than guessing where it is: sweep the port side for
  // the place the shell's surface is highest off the ground, which is the
  // wing root — the aerobatic's wing is forward of its cockpit, and a guessed
  // point behind it reads as "the plane is not solid" when it is.
  let wing = null; let best = -Infinity;
  for (let dx = 0.25; dx <= 0.7; dx += 0.05) {
    for (let dz = -4; dz <= 2; dz += 0.5) {
      const p = g.plane.localToWorld(new THREE.Vector3(-g.halfSpan * dx, 0, c.seat.z + dz));
      const rr = new THREE.Raycaster(new THREE.Vector3(p.x, p.y + 12, p.z), new THREE.Vector3(0, -1, 0), 0, 40);
      const h = rr.intersectObject(g.plane.userData.shell, true)[0];
      if (h && h.point.y > best) { best = h.point.y; wing = h.point.clone(); }
    }
  }
  if (!wing) return JSON.stringify({ error: 'no standable surface found on the shell' });
  const groundY = g._groundAt(wing.x, wing.z, wing.y + 30, 40);
  const wingTop = wing.y;
  // drop the hero on it from 3 m up and let gravity settle
  g.foot.pos.set(wing.x, wing.y + 3, wing.z);
  g.foot.vy = 0; g.foot.grounded = false;
  for (let i = 0; i < 180; i++) g.foot.update(1 / 60);
  const landed = g.foot.pos.y;
  const belowPlane = g.plane.position.y - 1.7;
  // and the shell's own top right there, for comparison
  const hit = { point: { y: wingTop } };
  const sb = new THREE.Box3().setFromObject(g.plane.userData.shell, true);
  const pb = new THREE.Box3().setFromObject(g.plane, true);
  // straight down the middle of the aircraft: this MUST hit the shell
  const mid = new THREE.Raycaster(new THREE.Vector3(g.plane.position.x, pb.max.y + 5, g.plane.position.z),
    new THREE.Vector3(0, -1, 0), 0, 60);
  const midHit = mid.intersectObject(g.plane.userData.shell, true)[0];
  // FROM THE WING ONTO THE SPINE. A wing is not a wall — you walk under a
  // wingtip, you do not climb it — but the fuselage beside it is one, and
  // that is the ledge the Assassin's-Creed climb is for.
  g.foot.pos.copy(wing); g.foot.pos.y += 0.02;
  g.foot.grounded = true; g.foot.vy = 0;
  g.foot.yaw = wing.x < g.plane.position.x ? Math.PI / 2 : -Math.PI / 2;
  g.foot.rig.obj.rotation.y = g.foot.yaw;
  g.foot._snapCamera();
  g.keys.add('KeyW');
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  g.keys.delete('KeyW');
  const spineClimb = !!g.foot._tryClimb();
  for (let i = 0; i < 200; i++) g.foot.update(1 / 60);
  const onSpine = +g.foot.pos.y.toFixed(2);

  // AND THE JUMP: from the airfield beside the wingtip, can a player get up?
  // just beyond the port wingtip, on the real airfield — not under the wing,
  // where the ground probe finds the aeroplane itself
  const edge = g.plane.localToWorld(new THREE.Vector3(-g.halfSpan - 1.1, 0, c.seat.z - 2));
  const gy = g.world.heightAt?.(edge.x, edge.z) ?? (g.plane.position.y - 1.7);
  g.foot.pos.set(edge.x, gy + 0.02, edge.z);
  g.foot.grounded = true; g.foot.vy = 0;
  g.foot.yaw = Math.atan2(g.plane.position.x - edge.x, g.plane.position.z - edge.z) + Math.PI;
  g.foot.rig.obj.rotation.y = g.foot.yaw;
  g.foot._snapCamera();
  // walk into the wing for a moment, the way a player would, then climb
  g.keys.add('KeyW');
  for (let i = 0; i < 40; i++) g.foot.update(1 / 60);
  g.keys.delete('KeyW');
  g.keys.add('KeyW'); g.keys.add('Space');
  for (let i = 0; i < 50; i++) g.foot.update(1 / 60);
  g.keys.delete('Space');
  for (let i = 0; i < 70; i++) g.foot.update(1 / 60);
  g.keys.delete('KeyW');
  const afterJump = +g.foot.pos.y.toFixed(2);
  const climbStarted = !!g.foot._tryClimb();
  for (let i = 0; i < 260; i++) g.foot.update(1 / 60);
  const afterClimb = +g.foot.pos.y.toFixed(2);
  window.__shot = () => { g.renderer.render(g.scene, g.camera); };
  return JSON.stringify({
    spineClimbFromWing: spineClimb,
    heroOnSpine: onSpine,
    jumpedOntoPlane: afterJump,
    climbStartedFromGround: climbStarted,
    groundBesideWing: +gy.toFixed(2),
    heroAfterClimb: afterClimb,
    gotUp: afterClimb > gy + 0.6,
    shellBox: [sb.min.toArray().map((v) => +v.toFixed(1)), sb.max.toArray().map((v) => +v.toFixed(1))],
    planeBox: [pb.min.toArray().map((v) => +v.toFixed(1)), pb.max.toArray().map((v) => +v.toFixed(1))],
    midRayHit: midHit ? +midHit.point.y.toFixed(2) : null,
    shellVisible: g.plane.userData.shell.visible,
    probeAt: [+wing.x.toFixed(1), +wing.y.toFixed(1), +wing.z.toFixed(1)],
    halfSpan: +g.halfSpan.toFixed(2),
    shellInCollidables: inSet,
    shellTris: g.plane.userData.shell.geometry.index
      ? g.plane.userData.shell.geometry.index.count / 3
      : g.plane.userData.shell.geometry.attributes.position.count / 3,
    wingTopY: hit ? +hit.point.y.toFixed(2) : null,
    heroLandedY: +landed.toFixed(2),
    standsOnWing: !!hit && Math.abs(landed - hit.point.y) < 0.35,
    airfieldY: +(groundY ?? belowPlane).toFixed(2),
    grounded: g.foot.grounded,
  }, null, 1);
}));
console.log(errs.length ? `page errors: ${errs.slice(0, 3).join(' | ')}` : 'no page errors');
await ctx.close();
