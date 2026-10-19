// Does the game actually play the REAL recordings?
//
// Checks the bank downloaded and decoded, then fires each pistol and counts the
// AudioBufferSourceNodes that start -- a sample that silently fell back to the
// synthesiser would show up here as zero buffers played.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/check_gun_audio.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/3842693f-a8a0-4010-9e7d-498b3a7bbc50/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/audio-profile', {
  headless: true,
  viewport: { width: 1024, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist',
    '--autoplay-policy=no-user-gesture-required'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) errs.push(e.message); });
page.on('console', (m) => { if (/gun audio|bank failed/i.test(m.text())) console.log(' [page]', m.text()); });

// count every buffer source that gets started, and remember its length
await page.addInitScript(() => {
  window.__played = [];
  const hook = () => {
    const C = window.AudioContext || window.webkitAudioContext;
    if (!C || C.prototype.__hooked) return;
    C.prototype.__hooked = true;
    const orig = C.prototype.createBufferSource;
    C.prototype.createBufferSource = function createBufferSource(...a) {
      const src = orig.apply(this, a);
      const start = src.start.bind(src);
      src.start = (...s) => {
        window.__played.push({ t: +(performance.now() - (window.__t0 ?? 0)).toFixed(1),
          dur: src.buffer ? +src.buffer.duration.toFixed(3) : null,
          rate: +src.playbackRate.value.toFixed(3) });
        return start(...s);
      };
      return src;
    };
  };
  hook();
});

await enterGame(page, { user: 'audiobot', pass: 'audiobot123' });
await page.waitForTimeout(16000);

const bank = await page.evaluate(async () => {
  const r = await fetch('audio/guns/bank.json');
  const b = r.ok ? await r.json() : null;
  return b && Object.fromEntries(Object.entries(b).map(([k, v]) => [k, v.length]));
});
console.log('bank.json on the server:', JSON.stringify(bank));

for (const gun of ['makarov', 'beretta']) {
  const r = await page.evaluate(async (want) => {
    const g = window.__game;
    const THREE = window.__THREE;
    await g.foot.ready;
    let t = null;
    g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
    t.updateWorldMatrix(true, true);
    const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
    g.plane.position.set(tp.x, tp.y + 1.6, tp.z + 14);
    g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
    if (!g.foot.active) g.foot.toggle();
    g.pickups.scan();
    const it = g.pickups.items.find((x) => x.def.id === want);
    if (it) {
      const wp = it.obj.getWorldPosition(new THREE.Vector3());
      g.foot.pos.set(wp.x, tp.y + 2, wp.z + 0.3);
      for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
      g.pickups.update(0.016, g.foot.pos);
      g.pickups.take();
    }
    if (!g.foot.held && g.weapons.carried[1]) g.foot.drawWeapon(g.weapons.carried[1]);
    // ONE trigger pull. Anything beyond a single gun-report buffer here is the
    // bug the player reported: "I shot once, it sounds like many times."
    window.__played.length = 0;
    window.__t0 = performance.now();
    g.pistol._cool = 0; g.pistol._rounds = 0;
    g.pistol.fire();
    const single = window.__played.slice();
    for (let i = 0; i < 400; i++) { g.foot.update(1 / 60); g.bullets.update(1 / 120); g.impacts.update(1 / 60); }
    const withImpact = window.__played.slice();
    // then a magazine, to check the takes do not stack up
    window.__played.length = 0;
    for (let k = 0; k < 5; k++) {
      g.pistol._cool = 0; g.pistol._rounds = 0;
      g.pistol.fire();
      for (let i = 0; i < 40; i++) { g.foot.update(1 / 60); g.bullets.update(1 / 120); }
    }
    for (let i = 0; i < 200; i++) { g.bullets.update(1 / 120); g.impacts.update(1 / 60); }
    // what the bank holds for this gun, and whether a take comes back
    const mod = await import('/src/audiofx.js');
    const roles = Object.fromEntries(Object.entries(mod.samples.buf)
      .map(([k, v]) => [k, v.map((b) => +b.duration.toFixed(2))]));
    return {
      gun: g.foot.held?.name ?? null,
      cartridge: g.pistol.cart.id,
      sampleRole: g.pistol.cart.sample,
      bankReady: mod.samples.ready,
      roles,
      single,
      withImpact,
      played: window.__played.slice(0, 40),
    };
  }, gun);
  const real = r.played.filter((p) => p.dur !== null && p.dur > 0.05);
  const noise = r.played.filter((p) => p.dur === null || p.dur > 0.8);
  console.log(`\n=== ${gun}: ${r.gun}  cartridge=${r.cartridge}  role=${r.sampleRole}  bankReady=${r.bankReady}`);
  if (r.roles && Object.keys(r.roles).length) {
    console.log('   decoded takes:', JSON.stringify(r.roles));
  }
  console.log(`   ONE trigger pull -> ${r.single.length} buffer(s):`, JSON.stringify(r.single));
  console.log(`   ...once the round lands -> ${r.withImpact.length} buffer(s):`,
    JSON.stringify(r.withImpact));
  console.log(`   5 rounds -> ${r.played.length} buffer(s)`);
}

console.log('\npage errors:', errs.length ? JSON.stringify(errs.slice(0, 5)) : 'none');
await ctx.close();
