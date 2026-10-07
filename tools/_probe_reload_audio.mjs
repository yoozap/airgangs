// Does a reload actually fire the three real one-shots, in order, on time?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 900, height: 600 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/gun audio/i.test(m.text())) console.log('  [page]', m.text()); });
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready, null, { timeout: 120000 });
await page.waitForTimeout(5000);

console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.sound?.start?.();                                   // the bank needs a context
  const fx = await import('/src/audiofx.js');
  await fx.samples.load();
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(p.x + 1.0, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);
  g.pickups.update(0.016, g.foot.pos);
  g.pickups.take();
  if (g.weapons.index !== 1) g.weapons.select(1);

  // Count at the WEB AUDIO layer, not at the module's: vite hands the game an
  // HMR-timestamped copy of audiofx.js, so patching the one this probe
  // imported patches a second instance nothing is playing through.
  const played = [];
  const byBuf = new Map();
  for (const [k, list] of Object.entries(fx.samples.buf)) for (const b of list) byBuf.set(b, k);
  const realStart = AudioBufferSourceNode.prototype.start;
  AudioBufferSourceNode.prototype.start = function (...a) {
    played.push(byBuf.get(this.buffer) ?? `synth(${this.buffer?.duration?.toFixed(2)}s)`);
    return realStart.apply(this, a);
  };

  const roles = Object.keys(fx.samples.buf);
  g.renderer.setAnimationLoop(null);
  g.pistol.update(1 / 60);   // settle _lastCart: the game loop does this for free
  const run = (dry) => {
    played.length = 0;
    g.pistol.ammo = dry ? 0 : 4;
    g.pistol._reload = 0;
    const started = g.pistol.reload();
    const diag = { started, held: g.foot.held?.id ?? null, mag: g.pistol.magSize,
      cues: g.pistol._cues.length, dur: +g.pistol._reload.toFixed(2) };
    const order = [];
    let i = 0;
    for (; i < 400; i++) {
      const before = played.length;
      g.pistol.update(1 / 60);
      for (let k = before; k < played.length; k++) order.push(`${played[k]}@${(i / 60).toFixed(2)}s`);
      if (!g.pistol.reloading) break;
    }
    diag.iters = i;
    diag.left = g.pistol._cues.length;
    diag.endReload = +g.pistol._reload.toFixed(3);
    diag.ammo = g.pistol.ammo;
    return { diag, order };
  };
  const durs = {};
  for (const k of ['mag_out', 'mag_in', 'slide'])
    durs[k] = (fx.samples.buf[k] ?? []).map((b) => +b.duration.toFixed(2));
  return { bankRoles: roles.length, durs,
    empty: run(true), tactical: run(false) };
}), null, 1));
await ctx.close();
