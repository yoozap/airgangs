// In the game, not the film: standing inside a booth, does the hero walk
// THROUGH its divider wall when he walks at it? And where does the chase
// camera sit when he stands at the firing point?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const PROFILE = process.env.PROFILE
  ?? '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad/range-profile';
const ctx = await chromium.launchPersistentContext(PROFILE, {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_counters')
  && (window.__game?.lanes?.lanes?.length ?? 0) > 0, null, { timeout: 300000 });
await page.waitForTimeout(1500);
console.log(JSON.stringify(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  await f.ready;
  if (!f.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); f.toggle(); }
  const lane = g.lanes.lanes[5];
  const fwd = lane.rail.clone().setY(0).normalize();
  const yaw = Math.atan2(-fwd.x, -fwd.z);
  const right = new T.Vector3(Math.cos(yaw), 0, -Math.sin(yaw));
  const list = g.world.collidablesNear(lane.stand.x, lane.stand.z);
  const probe = lane.stand.clone().addScaledVector(fwd, -2.5);
  const down = new T.Raycaster(new T.Vector3(probe.x, lane.stand.y + 0.5, probe.z), new T.Vector3(0, -1, 0), 0, 8);
  const floor = down.intersectObjects(list, true)[0]?.point.y ?? null;
  const settle = () => { for (let i = 0; i < 40; i++) f.update(1 / 60); };
  const run = (label, z, faceYaw, frames = 90) => {
    const p = lane.stand.clone().addScaledVector(fwd, z);
    f._stand(p.x, floor + 0.02, p.z, faceYaw);
    g.keys.clear(); settle();
    f.camRef = f.yaw;
    g.keys.add('KeyW');
    for (let i = 0; i < frames; i++) f.update(1 / 60);
    g.keys.clear();
    const d = f.pos.clone().sub(p);
    return { label, across: +d.dot(right).toFixed(2), along: +d.dot(fwd).toFixed(2), dy: +(f.pos.y - floor).toFixed(2) };
  };
  const out = [];
  // inside the booth, 0.7 m behind the line, walking at the right-hand wall, then the left
  out.push(run('in booth, walk at right wall', -0.7, yaw - Math.PI / 2));
  out.push(run('in booth, walk at left wall', -0.7, yaw + Math.PI / 2));
  // from the open floor diagonally into the divider's back end
  out.push(run('open floor, diagonal into divider end', -4.0, yaw - Math.PI / 4, 150));
  // the chase camera with the hero at the firing point
  const p = lane.stand.clone().addScaledVector(fwd, -0.7);
  f._stand(p.x, floor + 0.02, p.z, yaw);
  g.keys.clear();
  for (let i = 0; i < 120; i++) f.update(1 / 60);
  const c = g.camera.position.clone().sub(lane.stand);
  out.push({ label: 'chase camera at firing point', camAcross: +c.dot(right).toFixed(2), camBehindLine: +(-c.dot(fwd)).toFixed(2), camUp: +(g.camera.position.y - floor).toFixed(2) });
  return { floor: +floor.toFixed(2), out };
}), null, 1));
await ctx.close();
