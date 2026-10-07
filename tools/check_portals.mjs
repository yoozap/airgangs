// What "portals" exist in this build, and do they work? There is no rendering
// portal in the engine — these are the real, geometric ones:
//   • road-tunnel portals: the mouths, with a flyable barrel behind them and
//     boreFloorAt() making the tunnel floor the ground inside
//   • hill portals / headwalls from the infra kit
//   • metro stair portals at the mapped entrances
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/tunnel cuts|portals|metro:/i.test(m.text())) console.log('  [page]', m.text().slice(0, 200)); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForTimeout(12000);

console.log(JSON.stringify(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, w = g.world;
  const bores = w._bores ?? [];
  // walk the middle of each bore and ask the engine where the floor is
  const probes = bores.slice(0, 6).map((b) => {
    const i = Math.floor(b.pts.length / 2);
    const [x, z] = b.pts[i];
    const floor = b.y[i];
    const inside = w.boreFloorAt(x, z, floor + 2);      // in the barrel
    const above = w.boreFloorAt(x, z, floor + 40);      // over the hill
    const terrain = w.heightAt(x, z);
    return {
      len: b.pts.length, hw: +b.hw.toFixed(1),
      floorY: +floor.toFixed(1),
      hillAbove: terrain == null ? null : +(terrain - floor).toFixed(1),
      insideFloor: inside == null ? null : +inside.toFixed(1),
      aboveReturnsNull: above === null,                  // must be null up there
      groundInBarrel: +(g._groundAt(x, z, floor + 2) ?? NaN).toFixed(1),
    };
  });
  let portals = 0, metroPortals = 0;
  g.scene.traverse((o) => {
    const n = (o.name || '').toLowerCase();
    if (n.includes('portal')) portals++;
    if (n.includes('metro') && n.includes('portal')) metroPortals++;
  });
  return { bores: bores.length, cuts: w._cuts?.length ?? 0, lids: w._lids?.length ?? 0,
    namedPortalObjects: portals, metroPortals, probes };
}), null, 1));
await ctx.close();
