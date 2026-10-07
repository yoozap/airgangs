// WHAT THE DOWNLOADED CLIP DOES WITH THE MAGAZINE (user: "when reload happend
// you dont take bullets metal item of ak"). CARGADOR is a bone in the AKM's
// own rig, so its path is relative to the weapon. Logs, through the reload:
// the magazine's offset from its seated home IN THE GUN'S FRAME, and how far
// it is from the support hand.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const CLIP = process.env.CLIP || 'reload';
const ctx = await chromium.launchPersistentContext(OUT + '/mag-profile', {
  headless: true, viewport: { width: 900, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 240000 });
await page.evaluate(async () => {
  const g = window.__game, f = g.foot;
  await f.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!f.active) f.toggle();
  if (f._modelId !== 'banana') {
    const old = f.rig; f._modelId = 'banana'; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
  }
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  await g.pickups.restore([{ s: WEAPON_DEFS.akm.slot, id: 'akm' }], []);
  await g.gunAnims.rifle.load();
  f.drawWeapon(g.weapons.carried[WEAPON_DEFS.akm.slot]);
  g.renderer.setAnimationLoop(null);
  window.__step = (n) => {
    for (let i = 0; i < n; i++) {
      for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60);
      f.update(1 / 60); g.pistol.update(1 / 60);
    }
  };
});
await page.evaluate(() => window.__step(90));
const info = await page.evaluate(() => {
  const g = window.__game, T = window.__THREE, f = g.foot;
  const gun = f.held.obj;
  const names = [];
  gun.traverse((o) => names.push(`${o.name}<${o.type}>`));
  let mag = null, bolt = null;
  gun.traverse((o) => { if (!mag && /CARGADOR/i.test(o.name)) mag = o; });
  gun.traverse((o) => { if (!bolt && /CAMARA/i.test(o.name) && !/end/i.test(o.name)) bolt = o; });
  window.__mag = mag;
  window.__bolt = bolt;
  window.__boltHome = bolt ? bolt.position.clone() : null;
  window.__gun = gun;
  window.__magHome = mag ? mag.position.clone() : null;
  return { names: names.slice(0, 24), found: !!mag, anim: !!gun.userData?.anim,
    clips: gun.userData?.anim ? Object.keys(gun.userData.anim.clips ?? {}) : null };
});
console.log('gun tree:', info.names.join(' '));
console.log('magazine bone found:', info.found, 'parts anim:', info.anim, 'clips:', info.clips);
await page.evaluate((clip) => {
  const g = window.__game;
  g.pistol.ammo = clip === 'reloadEmpty' ? 0 : 1;
  g.pistol.reload();
}, CLIP);
for (let i = 0; i <= 26; i++) {
  const r = await page.evaluate(() => {
    window.__step(12);
    const T = window.__THREE, g = window.__game, f = g.foot;
    const mag = window.__mag, gun = window.__gun;
    if (!mag) return null;
    mag.updateWorldMatrix(true, false);
    gun.updateWorldMatrix(true, false);
    const off = mag.position.clone().sub(window.__magHome);     // bone-local travel
    const wp = new T.Vector3().setFromMatrixPosition(mag.matrixWorld);
    let lh = null; f.rig.obj.traverse((o) => { if (!lh && o.isBone && /^hand_l$/i.test(o.name)) lh = o; });
    lh.updateWorldMatrix(true, false);
    const hp = new T.Vector3().setFromMatrixPosition(lh.matrixWorld);
    const bolt = window.__bolt;
    const bTravel = bolt && window.__boltHome ? bolt.position.distanceTo(window.__boltHome) : null;
    return {
      rl: +g.pistol._reload.toFixed(2),
      bolt: bTravel === null ? null : +bTravel.toFixed(3),
      travel: +off.length().toFixed(3),
      off: off.toArray().map((n) => +n.toFixed(3)),
      toHand: +wp.distanceTo(hp).toFixed(2),
      vis: mag.visible,
    };
  });
  if (!r) { console.log('no magazine bone'); break; }
  console.log(`+${(i * 0.2).toFixed(1)}s rl=${r.rl} mag=${r.travel} bolt=${r.bolt} magToHand=${r.toHand}`);
}
await ctx.close();
