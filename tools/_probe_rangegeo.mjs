// What the reel has to work with at the range: every lane's firing point, rail,
// travel limits and sheet size, the lane pitch, and how much clear floor there
// is BEHIND the firing line for the hero to walk in across.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const PROFILE = process.env.PROFILE
  ?? '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad/range-profile';
const ctx = await chromium.launchPersistentContext(PROFILE, {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse:/.test(m.text())) console.log(' [page]', m.text()); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_counters')
  && (window.__game?.lanes?.lanes?.length ?? 0) > 0, null, { timeout: 300000 });
await page.waitForTimeout(1500);
console.log(JSON.stringify(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const lanes = g.lanes.lanes;
  const near = g.world.collidablesNear ? null : (g.world.collidables ?? []);
  const ray = new T.Raycaster(); ray.firstHitOnly = true;
  const clearBack = (lane, ox) => {
    const r = new T.Vector3(-lane.rail.z, 0, lane.rail.x); // right of the rail
    const from = lane.stand.clone().addScaledVector(r, ox); from.y = lane.stand.y + 1.0;
    const list = g.world.collidablesNear ? g.world.collidablesNear(from.x, from.z) : near;
    ray.set(from, lane.rail.clone().negate()); ray.far = 30;
    const h = ray.intersectObjects(list, true)[0];
    return h ? +h.distance.toFixed(2) : 30;
  };
  const out = lanes.map((l, i) => {
    const sheet = l.papers[0]; let m = null; sheet?.traverse((o) => { if (!m && o.isMesh) m = o; });
    const b = m ? new T.Box3().setFromObject(m) : null;
    return { i, stand: l.stand.toArray().map((x) => +x.toFixed(2)), rail: l.rail.toArray().map((x) => +x.toFixed(3)),
      range: +l.range.toFixed(2), min: +l.min.toFixed(2), max: +l.max.toFixed(2), at: l.at,
      sheet: b ? { size: b.getSize(new T.Vector3()).toArray().map((x) => +x.toFixed(2)), ctr: b.getCenter(new T.Vector3()).toArray().map((x) => +x.toFixed(2)) } : null,
      back: [clearBack(l, 0), clearBack(l, 1.5), clearBack(l, -1.5)] };
  });
  // the pitch: distance between neighbouring stands across the rail
  const pitch = out.length > 1 ? +Math.hypot(out[1].stand[0] - out[0].stand[0], out[1].stand[2] - out[0].stand[2]).toFixed(2) : null;
  const floorY = g._groundAt(lanes[0].stand.x, lanes[0].stand.z, lanes[0].stand.y + 2, 4);
  return { n: out.length, pitch, floorY, hero: g.foot?.pos?.toArray().map((x) => +x.toFixed(1)), lanes: out };
}), null, 1));
await ctx.close();
