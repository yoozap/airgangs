// The plane's belly gun now marks the city through the same impact layer as the
// pistol (impactfx.js). This flies over the centre, holds the trigger, and
// checks the rounds still land, still damage buildings, and now stamp a
// caliber-correct hole instead of the old half-metre crater.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/check_mg_impacts.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/3842693f-a8a0-4010-9e7d-498b3a7bbc50/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/mg-profile', {
  headless: true, viewport: { width: 1100, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) errs.push(e.message); });

await enterGame(page, { user: 'mgbot', pass: 'mgbot123' });
await page.waitForTimeout(16000);

const r = await page.evaluate(async () => {
  const g = window.__game;
  const THREE = window.__THREE;
  if (g.foot.active) g.foot._board();
  // park the plane low over the middle of the city, nose level
  let rec = null;
  for (const m of g.world.buildingMeshes ?? []) {
    for (const b of m.userData.buildings ?? []) if (!rec || b.maxHp > rec.maxHp) rec = b;
  }
  const h = rec.top - rec.base;
  g.plane.position.set(rec.cx, rec.base + h * 0.55, rec.cz + 260);
  g.plane.rotation.set(0, Math.PI, 0);          // looking down -Z at the block
  g.airborne = true; g.crashed = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.started = true;
  g.skillbar.current = 'gun';

  const before = {
    spall: g.impacts.spall.mesh.count, pits: g.impacts.pits.mesh.count, hp: rec.hp,
  };
  g.keys.add('Space');
  for (let i = 0; i < 260; i++) {
    g.guns.update(1 / 60);
    g.impacts.update(1 / 60);
  }
  g.keys.delete('Space');
  for (let i = 0; i < 120; i++) { g.guns.update(1 / 60); g.impacts.update(1 / 60); }

  // size of the last pit stamped, in mm
  const M = new THREE.Matrix4(); const S = new THREE.Vector3();
  const size = (L) => {
    if (!L.mesh.count) return null;
    L.mesh.getMatrixAt((L.cursor - 1 + L.max) % L.max, M);
    M.decompose(new THREE.Vector3(), new THREE.Quaternion(), S);
    return [+(S.x * 1000).toFixed(0), +(S.y * 1000).toFixed(0)];
  };
  return {
    building: g.world.addressOf?.(rec) || '(unnamed)',
    hpBefore: before.hp, hpAfter: rec.hp, stage: rec.stage,
    spallStamped: g.impacts.spall.mesh.count - before.spall,
    pitsStamped: g.impacts.pits.mesh.count - before.pits,
    lastPitMM: size(g.impacts.pits),
    lastSpallMM: size(g.impacts.spall),
    dustAlive: g.impacts.debris.life.filter((x) => x < 1).length,
  };
});
console.log(JSON.stringify(r, null, 1));
console.log('page errors:', errs.length ? JSON.stringify(errs.slice(0, 4)) : 'none');
await ctx.close();
