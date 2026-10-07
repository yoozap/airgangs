// One shooting booth on its own, from the shooter's side and from above, so the
// pieces can be told apart (20 of them overlapping is unreadable).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 300000 });
await page.waitForTimeout(1500);

const look = async (file, from, to, hideOthers = true) => {
  await page.evaluate(({ from, to, hideOthers }) => {
    const T = window.__THREE, g = window.__game;
    let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
    const booths = []; wh.traverse((o) => { if (o.name === 'warehouse_targets') booths.push(o); });
    const keep = booths[Math.floor(booths.length / 2)];
    for (const b of booths) b.visible = !hideOthers || b === keep;
    keep.updateWorldMatrix(true, false);
    const p = new T.Vector3().setFromMatrixPosition(keep.matrixWorld);
    const along = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion); // lane
    const across = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion);
    const at = (v) => p.clone().addScaledVector(along, v[0]).addScaledVector(across, v[1]).setY(p.y + v[2]);
    g.renderer.setAnimationLoop(null);
    g.camera.position.copy(at(from));
    g.camera.lookAt(at(to));
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { from, to, hideOthers });
  await page.screenshot({ path: OUT + '/' + file });
  console.log('shot ->', file);
};
// the lane runs along -Z of the holder; the booth table is at the near end
await look('booth_counter.png', [13, 5, 2.2], [6, 0, 1.2]);   // close on the table end
await look('booth_lane.png', [11, 0, 1.7], [-8, 0, 1.4]);     // stood at the table, down the lane
await look('booth_side.png', [-9, 14, 3], [4, 0, 1.2]);
await look('booth_front.png', [-11, 0, 1.6], [6, 0, 1.4]);
await look('booth_top.png', [0, 0, 16], [0.01, 0, 0]);
await look('booth_all.png', [-14, 10, 2.5], [4, 0, 1.2], false);
await ctx.close();
