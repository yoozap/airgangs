// Verifies City/ShopfrontBuilder.cs ("verified": null in docs/port_manifest.json
// — the biggest unverified unit in the pass-3 worklist) against REAL placement
// data read straight out of the LIVE running src/shopfronts.js scene, instead
// of reimplementing the wall-snap search in Python/Node (judged too large by
// the prior pass — see ShopfrontBuilder.cs's own class doc: "the ENTIRE
// wall-snap search runs in GAME-frame metres").
//
// The oracle: src/shopfronts.js:72-375 `buildShopfronts(citymap, buildings)`
// builds moduleItems/awningItems/canopyItems/crossItems/signSpots as LOCAL
// arrays and only the last one survives past the function — as
// `citymap.shopSigns.spots` (SignPool's own `this.spots = spots`,
// shopfronts.js:387) + `citymap.shopSigns.names` (shopfronts.js:388). `citymap`
// itself is `game.world` (src/game.js:419 `this.world = new CityMap(...)`).
// Every non-fuel POI that produced ANYTHING (a module, an awning, a cross)
// also produced exactly one signSpot in the SAME per-POI loop
// (shopfronts.js:210-247) — same push site, same `{x,z,y,ang,cat,ni}` shape —
// so signSpots is a 1:1, in-order record of the wall-snap result for the
// building's OWN real geometry and the OWN real pois2.json/named.json/
// brands.json data this engine loaded, with no transcription anywhere.
//
// ShopfrontBuilder.cs's `Signs` list is built by the EXACT same iteration
// order (`foreach (var p in all)`, fuel-else-HasWall gate, see its own
// ShopfrontBuilder.cs:333-411) over data from the IDENTICAL JSON files
// (pois2.json/named.json/brands.json are shared StreamingAssets, not
// re-derived) — so `Signs[i]` must equal `spots[i]` index-for-index, PROVIDED
// both runtimes iterate the category keys and per-category points in the same
// (file) order, which JSON.parse (V8) and JObject.Parse (Json.NET) both
// preserve as insertion order. Position is compared in GAME frame (negate the
// C# side's GeoFrame.ToUnity mirror back: x_game = -Position.x) since that is
// the frame this script's own `spots[i].x` is already in.
//
//   node tools/_probe_shopfrontbuilder.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const ctx = await chromium.launchPersistentContext('/tmp/claude-shopfront-probe-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'shopprobe', pass: 'shopprobe123' });

// shopfronts.js is the LAST scenery stage (citymap.js:909), one stage per
// frame after world.ready — give it real time to reach the end of that list.
await page.waitForFunction(
  () => !!window.__game?.world?.shopSigns?.spots?.length,
  null,
  { timeout: 60000 },
);
// one more settle frame in case spots was just assigned this tick
await page.waitForTimeout(200);

const data = await page.evaluate(() => {
  const w = window.__game.world;
  const sp = w.shopSigns;
  const spots = sp.spots;
  const names = sp.names;
  const n = spots.length;
  const idx = [...new Set([0, 1, Math.floor(n / 4), Math.floor(n / 2), Math.floor((3 * n) / 4), n - 2, n - 1])]
    .filter((i) => i >= 0 && i < n);
  const rows = idx.map((i) => {
    const s = spots[i];
    const name = s.ni >= 0 && s.ni < names.length ? names[s.ni] : null;
    return { i, x: s.x, z: s.z, y: s.y, ang: s.ang, cat: s.cat, ni: s.ni, name };
  });
  // category histogram — a cheap aggregate cross-check independent of index
  // order, in case per-index pairing ever drifts for an unrelated reason.
  const catCounts = {};
  for (const s of spots) catCounts[s.cat] = (catCounts[s.cat] || 0) + 1;
  return { spotsCount: n, namesCount: names.length, rows, catCounts };
});

console.log(`SHOPSIGN_COUNT spots=${data.spotsCount} names=${data.namesCount}`);
for (const r of data.rows) {
  const nameEsc = JSON.stringify(r.name ?? '');
  console.log(`SHOPSIGN i=${r.i} x=${r.x.toFixed(3)} z=${r.z.toFixed(3)} y=${r.y.toFixed(3)} ` +
    `ang=${r.ang.toFixed(6)} cat=${r.cat} ni=${r.ni} name=${nameEsc}`);
}
const catKeys = Object.keys(data.catCounts).sort();
for (const k of catKeys) console.log(`SHOPSIGN_CAT cat=${k} count=${data.catCounts[k]}`);

await ctx.close();
