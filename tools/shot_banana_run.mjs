// Play as the banana and prove its limbs move: two frames mid-run, plus the
// measured travel of the hand and foot bones between them.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 900, height: 760 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForFunction(() => !!window.__game?.foot?.rig, null, { timeout: 90000 });
// swap the hero's body for the banana
await page.evaluate(async () => {
  const m = await import('/src/characters.js');
  const g = window.__game;
  const rig = await m.makeCharacter('banana');
  g.foot.rig = rig;
});
const key = (c, d) => page.evaluate(([code, down]) => {
  for (const t of [window, document, document.body]) {
    t.dispatchEvent(new KeyboardEvent(down ? 'keydown' : 'keyup', { code, key: code, bubbles: true }));
  }
}, [c, d]);
for (let i = 0; i < 10 && !(await page.evaluate(() => window.__game.foot.active)); i++) {
  await key('Enter', true); await page.waitForTimeout(80); await key('Enter', false);
  await page.waitForTimeout(700);
}
await page.addStyleTag({ content: '#hud,#combat-hud,#skillbar,#weaponbar,#map-mini,#hud-hint,#attribution{display:none!important}' });
await page.evaluate(() => {
  const g = window.__game, f = g.foot;
  const pin = () => {
    g.camera.position.set(f.pos.x + 3.4, f.pos.y + 1.3, f.pos.z + 3.4);
    g.camera.lookAt(f.pos.x, f.pos.y + 0.85, f.pos.z);
    g.__pin = requestAnimationFrame(pin);
  };
  pin();
});
// Limb positions RELATIVE TO THE PELVIS. Measured in world space the numbers
// are dominated by the body travelling down the runway, which says nothing
// about whether the limbs are swinging.
const limbs = () => page.evaluate(() => {
  const rig = window.__game.foot.rig;
  const out = {};
  let root = null;
  rig.obj.updateMatrixWorld(true);
  rig.obj.traverse((o) => { if (o.isBone && o.name === 'pelvis') root = o; });
  if (!root) return out;
  const re = root.matrixWorld.elements;
  rig.obj.traverse((o) => {
    if (!o.isBone) return;
    if (['hand_l', 'foot_l', 'hand_r', 'foot_r'].includes(o.name)) {
      const e = o.matrixWorld.elements;
      out[o.name] = [+(e[12] - re[12]).toFixed(3),
        +(e[13] - re[13]).toFixed(3), +(e[14] - re[14]).toFixed(3)];
    }
  });
  return out;
});
await key('KeyW', true);
const track = [];
for (let i = 0; i < 14; i++) { await page.waitForTimeout(110); track.push(await limbs()); }
await key('KeyW', false);
const swing = (k) => {
  const xs = track.map((t) => t[k]).filter(Boolean);
  if (!xs.length) return null;
  const rng = (i) => +(Math.max(...xs.map((v) => v[i])) - Math.min(...xs.map((v) => v[i]))).toFixed(3);
  return { x: rng(0), y: rng(1), z: rng(2) };
};
console.log('limb swing relative to the pelvis (m):', JSON.stringify({
  hand_l: swing('hand_l'), hand_r: swing('hand_r'),
  foot_l: swing('foot_l'), foot_r: swing('foot_r') }));
const A = track[0], B = track[7];

console.log('clips on the banana:', JSON.stringify(await page.evaluate(() =>
  Object.keys(window.__game.foot.rig.actions))));
await browser.close();
