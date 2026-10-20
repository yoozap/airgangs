// THE TARGET FRAME. Click a parked aeroplane in the plane hall: does its card
// come up beside our own hull card, with the right picture, name and hull —
// and does shooting it move the bar?
//   node tools/_probe_target.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/tmp';
fs.mkdirSync(OUT + '/target', { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('[err]', m.text().slice(0, 200)); });
await enterGame(page);

// The loop owns the camera: pin the pose from inside the tick, exactly as
// tools/_probe_planehall.mjs does.
await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.plane.position.set(p.x, p.y, p.z);
    }
    tick();
    if (window.__pin) {
      const c = g.camera;
      c.position.set(window.__pin.cx, window.__pin.cy, window.__pin.cz);
      c.lookAt(window.__pin.lx, window.__pin.ly, window.__pin.lz);
      c.updateMatrixWorld(true);
      g.renderer.render(g.scene, c);
    }
  };
});

await page.waitForFunction(() => {
  let hall = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') hall = o; });
  return !!hall;
}, null, { timeout: 420000 });

const hall = await page.evaluate(() => {
  const g = window.__game;
  let h = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') h = o; });
  const p = h.getWorldPosition(new window.__THREE.Vector3());
  return { x: p.x, y: p.y, z: p.z, yaw: h.rotation.y };
});

// fly to the hall so the fleet builds, then wait for the machines
await page.evaluate((h) => {
  window.__pin = { x: h.x, y: h.y + 120, z: h.z, cx: h.x, cy: h.y + 420, cz: h.z + 10,
    lx: h.x, ly: h.y, lz: h.z };
}, hall);
await page.waitForFunction(() => (window.__game.targets?.list.length ?? 0) >= 30,
  null, { timeout: 420000 }).catch(() => console.log('TIMEOUT waiting for the fleet'));
await page.waitForTimeout(2500);
console.log('registered machines:', await page.evaluate(() => window.__game.targets.list.length));

// Stand the camera on the taxi lane, out from the machine's own side of the
// hall, looking straight at it — then click the pixel its centre projects to.
// (Standing "in front of the nose" put the lens outside the hall for an
// 88 m An-225 and clicked the wall, which is exactly what should NOT target.)
async function aimAt(id, hall) {
  return page.evaluate(({ wanted, h }) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = g.targets.list.find((r) => r.def.id === wanted) ?? g.targets.list[0];
    const p = rec.wrapper.getWorldPosition(new THREE.Vector3());
    const toLane = new THREE.Vector3(h.x - p.x, 0, h.z - p.z).normalize();
    const d = (rec.wrapper.userData.size ?? 12) * 0.6 + 26;
    window.__pin = {
      x: p.x, y: p.y + 200, z: p.z + 600,          // our own machine, well away
      cx: p.x + toLane.x * d, cy: p.y + 6, cz: p.z + toLane.z * d,
      lx: p.x, ly: p.y, lz: p.z,
    };
    window.__aim = { x: p.x, y: p.y, z: p.z };
    return { id: rec.def.id, name: rec.def.name, maxHp: rec.maxHp };
  }, { wanted: id, h: hall });
}

// where the machine's centre lands on screen, so the click is a real click
async function screenOfAim() {
  return page.evaluate(() => {
    const THREE = window.__THREE;
    const g = window.__game;
    const v = new THREE.Vector3(window.__aim.x, window.__aim.y, window.__aim.z).project(g.camera);
    const r = g.renderer.domElement.getBoundingClientRect();
    return { x: r.left + (v.x * 0.5 + 0.5) * r.width, y: r.top + (-v.y * 0.5 + 0.5) * r.height };
  });
}

for (const id of ['spitfire', 'an225']) {
  const info = await aimAt(id, hall);
  await page.waitForTimeout(900);
  const at = await screenOfAim();
  await page.mouse.click(at.x, at.y);
  await page.waitForTimeout(400);
  const got = await page.evaluate(() => {
    const t = window.__game.targets;
    const el = document.getElementById('targetframe');
    const r = el.getBoundingClientRect();
    return {
      target: t.rec?.def?.id ?? null,
      hidden: el.classList.contains('hidden'),
      name: document.getElementById('tf-name').textContent,
      sub: document.getElementById('tf-sub').textContent,
      role: document.getElementById('tf-role').textContent,
      hp: document.getElementById('tf-hp').textContent,
      dist: document.getElementById('tf-dist').textContent,
      thumb: document.getElementById('tf-portrait').getAttribute('src'),
      ring: !!t.ring?.visible,
      box: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
    };
  });
  console.log(`click ${info.id} (${info.name}, ${info.maxHp} hp) ->`, JSON.stringify(got));
  await page.screenshot({ path: `${OUT}/target/click_${id}.png` });
  await page.screenshot({ path: `${OUT}/target/card_${id}.png`,
    clip: { x: 0, y: 70, width: 560, height: 110 } });

  // ...and now shoot it: the damage goes in through the city's own damageHit,
  // the same call a round and a rocket make.
  const hit = await page.evaluate(() => {
    const g = window.__game;
    const rec = g.targets.rec;
    const col = g.world.collidables.find((m) => m.userData?.planeRec === rec);
    if (!col) return { err: 'no collider for the target' };
    const before = rec.hp;
    for (let i = 0; i < 12; i++) g.world.damageHit({ object: col, point: rec.wrapper.position }, 60);
    return { before, after: rec.hp, shown: document.getElementById('tf-hp').textContent,
      width: document.getElementById('tf-fill').style.width };
  });
  console.log('  12 rounds ->', JSON.stringify(hit));
  await page.waitForTimeout(300);
  await page.screenshot({ path: `${OUT}/target/hit_${id}.png`,
    clip: { x: 0, y: 70, width: 560, height: 110 } });
}

// OUR OWN MACHINE is a target too: the card then reads the live hull out of
// game.health, and gets no selection ring (it would hang under us in flight).
const own = await page.evaluate(() => {
  const g = window.__game;
  g.health = 640;
  g.targets.set(g.targets._ownRec());
  g.targets.update(0.016);
  return {
    name: document.getElementById('tf-name').textContent,
    sub: document.getElementById('tf-sub').textContent,
    hp: document.getElementById('tf-hp').textContent,
    ring: !!g.targets.ring?.visible,
  };
});
console.log('own machine ->', JSON.stringify(own));
await page.screenshot({ path: `${OUT}/target/card_own.png`, clip: { x: 0, y: 70, width: 600, height: 110 } });

// TAB — the nearest machine in front of us. press() is invisible to the game
// loop, so the key is HELD for a frame or two.
await page.evaluate(() => window.__game.targets.clear());
await page.keyboard.down('Tab');
await page.waitForTimeout(90);
await page.keyboard.up('Tab');
await page.waitForTimeout(300);
console.log('Tab ->', await page.evaluate(() => window.__game.targets.rec?.def?.id ?? null));

// Esc drops the target (and must NOT open the settings while one is up)
await page.keyboard.down('Escape');
await page.waitForTimeout(90);
await page.keyboard.up('Escape');
await page.waitForTimeout(300);
console.log('Esc ->', await page.evaluate(() => ({
  target: window.__game.targets.rec?.def?.id ?? null,
  hidden: document.getElementById('targetframe').classList.contains('hidden'),
  settings: window.__game.settings.visible,
})));
await browser.close();
