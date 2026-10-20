// The walk set in the real game (characters.js WALK_SET, src/anims.js):
// which cycle the legs actually get at each pace and direction, and what the
// body does about it. Running must still be the body's OWN run clip.
//   BODY=elephant node tools/_probe_walkset.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const BODY = process.env.BODY || 'banana';
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('console.error:', m.text().slice(0, 200)); });
await enterGame(page, { user: 'walkbot', pass: 'walkbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
});
await page.evaluate(async (id) => {
  const f = window.__game.foot;
  if (f._modelId === id) return;
  const old = f.rig; const at = f.pos.clone(); const yaw = f.yaw;
  f._modelId = id;
  await f._loadBody();
  if (old?.obj?.parent) old.obj.parent.remove(old.obj);
  if (!f.rig.obj.parent) window.__game.scene.add(f.rig.obj);
  f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
}, BODY);
await page.waitForTimeout(1500);
console.log('body:', await page.evaluate(() => window.__game.foot._modelId),
  '| library:', await page.evaluate(() => !!window.__game.foot.rig.moves));

// A WARM-UP STEP. The first key after the hero lands on foot goes nowhere --
// the loop is still settling -- and the first case would read 0 m/s.
await page.keyboard.down('KeyW');
await page.waitForTimeout(600);
await page.keyboard.up('KeyW');
await page.waitForTimeout(600);

// held keys, not press(): a tap between two frames never reaches the game loop
const cases = [
  ['run forward', ['KeyW']],
  ['walk forward', ['ControlLeft', 'KeyW']],
  ['walk left (Q)', ['ControlLeft', 'KeyQ']],
  ['walk right (E)', ['ControlLeft', 'KeyE']],
  ['walk back', ['ControlLeft', 'KeyS']],
  ['walk fwd-left', ['ControlLeft', 'KeyW', 'KeyQ']],
  ['crouch creep', ['KeyW']],
];
for (const [label, keys] of cases) {
  if (label === 'crouch creep') await page.keyboard.press('KeyC');
  for (const k of keys) await page.keyboard.down(k);
  await page.waitForTimeout(900);
  const st = await page.evaluate(() => {
    const f = window.__game.foot;
    return {
      clip: f.rig.current?.getClip?.()?.name ?? '-',
      rate: +(f.rig.current?.timeScale ?? 0).toFixed(2),
      speed: +f.speed.toFixed(2),
      turn: +(f.rig._strafeY ?? 0).toFixed(2),
    };
  });
  await page.screenshot({ path: `${OUT}/walk_${BODY}_${label.replace(/[^a-z]+/gi, '_')}.png` });
  console.log(`${label.padEnd(16)} ${st.clip.padEnd(18)} rate ${String(st.rate).padStart(5)}`
    + `  ${String(st.speed).padStart(5)} m/s  body turned ${st.turn} rad`);
  for (const k of keys) await page.keyboard.up(k);
  if (label === 'crouch creep') await page.keyboard.press('KeyC');
  await page.waitForTimeout(500);
}
await browser.close();
