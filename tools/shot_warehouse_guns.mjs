// Frames the first warehouse bench from above, with the two pistols on it.
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_warehouse_guns.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/guns-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/failed|warehouse/i.test(m.text())) console.log('  [page]', m.text()); });
await enterGame(page, { user: 'gunshot', pass: 'gunshot123' });
await page.waitForTimeout(16000);
const info = await page.evaluate(() => {
  const g = window.__game, THREE = window.__THREE;
  const tables = []; g.scene.traverse((o) => { if (o.name === 'warehouse_table') tables.push(o); });
  const t = tables[0]; t.updateWorldMatrix(true, true);
  const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  const guns = [];
  g.scene.traverse((o) => {
    if (o.isMesh && /makarov|beretta|akm/i.test(o.name || '')) {
      o.updateWorldMatrix(true, false);
      const p = new THREE.Vector3().setFromMatrixPosition(o.matrixWorld);
      guns.push(`${o.name} @ ${p.toArray().map((v) => +v.toFixed(2))}`);
    }
  });
  // Freeze the game first: its chase camera re-renders every frame and would
  // overwrite this view before the screenshot lands.
  g.renderer.setAnimationLoop(null);
  g.camera.position.set(tp.x + 1.1, tp.y + 1.85, tp.z + 1.0);
  g.camera.lookAt(tp.x, tp.y + 1.2, tp.z);
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
  return { tables: tables.length, firstTable: tp.toArray().map((v) => +v.toFixed(1)), guns };
});
console.log(JSON.stringify(info, null, 1));
await page.waitForTimeout(500);
await page.screenshot({ path: DIR + '/guns_on_table.png' });
await ctx.close();
