// The booths: how deep their divider walls reach back from the firing line,
// and whether a hero pushed sideways inside one is stopped by the wall or
// walks through it into the next booth.
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
  const ray = new T.Raycaster(); ray.firstHitOnly = true;
  const down = new T.Raycaster(new T.Vector3(lane.stand.x, lane.stand.y + 0.5, lane.stand.z), new T.Vector3(0, -1, 0), 0, 8);
  const floor = down.intersectObjects(list, true)[0]?.point.y ?? null;
  // walls: from 12 m behind the line, forward, on the divider lines and on the lane centre, at two heights
  const walls = {};
  for (const [name, ox] of [['centre', 0], ['divL', -0.835], ['divR', 0.835], ['nextL', -1.67], ['nextR', 1.67]]) {
    const r = [];
    for (const h of [0.3, 1.0, 1.6]) {
      const from = lane.stand.clone().addScaledVector(right, ox).addScaledVector(fwd, -12).setY(floor + h);
      ray.set(from, fwd); ray.far = 13;
      const hit = ray.intersectObjects(list, true)[0];
      r.push(hit ? { h, behindLine: +(12 - hit.distance).toFixed(2), what: hit.object.name } : { h, behindLine: null });
    }
    walls[name] = r;
  }
  // push the hero sideways inside the booth, 1 m behind the counter
  const results = [];
  for (const [label, z, key] of [['inBooth +D', -1.0, 'KeyD'], ['inBooth -A', -1.0, 'KeyA'], ['open floor +D', -5.0, 'KeyD']]) {
    const p = lane.stand.clone().addScaledVector(fwd, z);
    f._stand(p.x, floor + 0.02, p.z, yaw);
    f.camRef = f.yaw;
    g.keys.clear(); g.keys.add(key);
    for (let i = 0; i < 90; i++) f.update(1 / 60);
    g.keys.clear();
    const d = f.pos.clone().sub(p);
    results.push({ label, across: +d.dot(right).toFixed(2), along: +d.dot(fwd).toFixed(2), dy: +(f.pos.y - floor).toFixed(2) });
  }
  // and forward from the open floor into the booth: does the counter stop him, and where
  const p = lane.stand.clone().addScaledVector(fwd, -5.0);
  f._stand(p.x, floor + 0.02, p.z, yaw); f.camRef = f.yaw;
  g.keys.clear(); g.keys.add('KeyW');
  for (let i = 0; i < 150; i++) f.update(1 / 60);
  g.keys.clear();
  results.push({ label: 'open floor +W', along: +f.pos.clone().sub(p).dot(fwd).toFixed(2), stoppedBehindLine: +(-(f.pos.clone().sub(lane.stand).dot(fwd))).toFixed(2) });
  return { floor: +floor.toFixed(2), standY: +lane.stand.y.toFixed(2), walls, results };
}), null, 1));
await ctx.close();
