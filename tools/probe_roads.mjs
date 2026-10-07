import { chromium } from 'playwright';

// Are the roads actually there? Boots the city, reports load errors and the
// road/junction/bridge mesh counts, then checks Rustaveli's own coordinates.
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
const errs = [];
page.on('pageerror', (e) => errs.push('PAGEERROR: ' + e.message));
page.on('console', (m) => {
  const t = m.text();
  if (m.type() === 'error' && !/colormap/.test(t)) errs.push('CONSOLE: ' + t);
  if (/road|tunnel|junction|damage|restored/i.test(t)) console.log('LOG:', t);
});
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 })
  .catch(() => console.log('!! world never became ready'));
await page.waitForTimeout(4000);

console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const out = { ready: !!w.ready, roadsRef: w._roadsRef?.length ?? -1, junctions: w._junctions?.length ?? -1 };
  let roadMeshes = 0;
  let roadTris = 0;
  w.group.traverse((o) => {
    if (!o.isMesh || !o.geometry) return;
    const n = (o.name || '').toLowerCase();
    if (n.includes('road') || o.userData?.road) { roadMeshes++; roadTris += (o.geometry.index?.count ?? o.geometry.attributes.position?.count ?? 0) / 3; }
  });
  out.roadMeshes = roadMeshes;
  out.roadTris = Math.round(roadTris);
  out.groupChildren = w.group.children.length;
  out.chunkMeshes = w.chunkMeshes.length;
  out.buildingMeshes = w.buildingMeshes.length;
  // Rustaveli Avenue, in front of the parliament
  const xz = (lat, lon) => [-(lon - w.meta.origin.lon) * w.meta.mlon, (lat - w.meta.origin.lat) * w.meta.mlat];
  const [rx, rz] = xz(41.6977, 44.8009);
  out.rustaveliXZ = [Math.round(rx), Math.round(rz)];
  out.groundAtRustaveli = w.heightAt(rx, rz);
  // any road-ish geometry within 40 m of that point?
  let near = 0;
  for (const c of w.chunkMeshes) {
    if (Math.hypot(c.cx - rx, c.cz - rz) > 1200) continue;
    near++;
  }
  out.chunksNearRustaveli = near;
  return out;
})));
console.log(errs.length ? `ERRORS (${errs.length}):\n${errs.slice(0, 8).join('\n')}` : 'NO ERRORS');
await browser.close();
