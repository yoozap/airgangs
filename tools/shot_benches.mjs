// The three work benches with the pistols on them: is anything from the firing
// line standing on or over them?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 300000 });
await page.waitForTimeout(1200);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const tables = []; const booths = [];
  wh.traverse((o) => {
    if (o.name === 'warehouse_table') tables.push(o);
    if (o.name === 'warehouse_targets') booths.push(o);
  });
  const wp = (o) => { o.updateWorldMatrix(true, false); return new T.Vector3().setFromMatrixPosition(o.matrixWorld); };
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const local = (o) => wp(o).applyMatrix4(inv);
  const t = tables.map(local), b = booths.map(local);
  window.__view = { t, b };
  return JSON.stringify({
    tables: t.map((v) => [+v.x.toFixed(1), +v.z.toFixed(1)]),
    boothsX: [Math.min(...b.map((v) => +v.x)).toFixed(1), Math.max(...b.map((v) => +v.x)).toFixed(1)],
    boothsZ: (+b[0].z).toFixed(1),
    nearestBoothToTable: Math.min(...b.map((v) => Math.hypot(v.x - t[0].x, v.z - t[0].z))).toFixed(1),
  });
}));

const shot = async (file, from, to) => {
  await page.evaluate(({ from, to }) => {
    const T = window.__THREE, g = window.__game;
    let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
    const toWorld = (l) => new T.Vector3(l[0], l[2], l[1]).applyMatrix4(wh.matrixWorld);
    g.camera.position.copy(toWorld(from));
    g.camera.lookAt(toWorld(to));
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { from, to });
  await page.screenshot({ path: `${OUT}/${file}` });
  console.log('shot ->', `${OUT}/${file}`);
};
// local frame: [x across, z along, y up]
await shot('benches.png', [-8, -52, 2.0], [-19.5, -58, 1.0]);
await shot('benches_wide.png', [2, -40, 3.2], [-19.5, -60, 1.0]);
await ctx.close();
