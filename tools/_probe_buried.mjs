// How many buildings are standing INSIDE the hill?
//
// A building's box is extruded from heightAt(centroid) - 2 up to
// heightAt(centroid) + h. On a slope the uphill corner of the same outline is
// metres above the centroid, so the wall there is buried that deep -- and a
// low building on a steep enough slope disappears into the grass entirely.
// This counts it, over the whole city, off the same data the builder uses.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/42a66776-883b-454e-ad1f-441d2105971b/scratchpad/engine';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 900, height: 600 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready !== false && window.__game?.started,
  null, { timeout: 420000 });
console.log(JSON.stringify(await page.evaluate(() => {
  // Straight off the records the builder left behind: base = gc - 2 + off and
  // top = gc + h, so the centroid ground and the height come back out of them.
  const w = window.__game.world;
  const out = { total: 0, buried1: 0, buried2: 0, sunk: 0, worst: [], note: "buried1 = less than 1 m of wall above the highest ground; buried2 = roof at or under it; sunk = FLOATING (floor above the lowest ground)" };
  for (const mesh of w.buildingMeshes ?? []) {
    for (const rec of mesh.userData.buildings ?? []) {
      // THE REAL OUTLINE, not its bounding box. The bottom ring of the box IS
      // the footprint, and it is sitting right there in the chunk's position
      // buffer between v0 and v1. Sampling the bbox corners instead reads the
      // ground at points the building does not stand on -- off the end of a
      // terrace, down a bank -- and reports hundreds of buildings floating
      // that are seated exactly as they should be.
      const pos = mesh.geometry.attributes.position;
      const ring = (rec.v1 - rec.v0) >= 6 ? (rec.v1 - rec.v0) : 0;
      let gmax = -Infinity, gmin = Infinity;
      let got = 0;
      for (let v = rec.v0; v < rec.v1 && got < 64; v++) {
        if (Math.abs(pos.getY(v) - rec.base) > 0.001) continue;   // bottom ring only
        got++;
        const g = w.heightAt(pos.getX(v), pos.getZ(v));
        if (g > gmax) gmax = g;
        if (g < gmin) gmin = g;
      }
      if (!got) continue;
      const gc = w.heightAt(rec.cx, rec.cz);
      const h = rec.top - gc;
      out.total++;
      // What matters is the FINISHED box against the ground it covers:
      //   clear  = how much wall stands above the highest ground it touches
      //   skirt  = how far the floor goes under the lowest (negative = floating)
      const clear = rec.top - gmax;
      const skirt = gmin - rec.base;
      if (clear < 1) out.buried1++;
      if (clear <= 0) out.buried2++;
      if (skirt < -0.01) out.sunk++;          // FLOATING over the slope
      const bury = -clear;
      if (out.worst.length < 10 || bury > out.worst[out.worst.length - 1].bury) {
        out.worst.push({ x: Math.round(rec.cx), z: Math.round(rec.cz),
          h: +h.toFixed(1), clear: +clear.toFixed(1), skirt: +skirt.toFixed(1),
          slope: +(gmax - gmin).toFixed(1) });
        out.worst.sort((a, b) => b.bury - a.bury);
        out.worst.length = Math.min(10, out.worst.length);
      }
    }
  }
  return out;
}), null, 1));
await ctx.close();
