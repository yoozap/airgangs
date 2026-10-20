// SHOOTING THE MACHINE YOU JUST CLIMBED OUT OF. "bulet ... flew straight
// through body of plane": is the player's own aeroplane a thing a round can
// hit at all, or is it not in the world the bullets are cast against?
//   node tools/_probe_ownplane.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(1500);

// Climb out, and then shoot the machine you were just sitting in — the real
// pistol, the real bullet simulation, the real target set.
const out = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  const plane = g.plane;
  const shell = plane?.userData?.shell ?? null;
  const p = plane.getWorldPosition(new THREE.Vector3());
  const near = g.world.collidablesNear ? g.world.collidablesNear(p.x, p.z) : g.world.collidables;
  // pretend the hero is out of it, which is what makes it a target
  const was = g.foot ? g.foot.active : false;
  if (g.foot) g.foot.active = true;
  const own = g.ownPlaneTargets?.() ?? null;
  const side = new THREE.Vector3(1, 0, 0).transformDirection(plane.matrixWorld).normalize();
  const from = p.clone().addScaledVector(side, 8).setY(p.y + 0.4);
  const dir = p.clone().sub(from).normalize();
  const all = own ? near.concat(own) : near;
  const hit = new THREE.Raycaster(from, dir, 0, 40).intersectObjects(all, true)[0] ?? null;
  const rec = hit ? g.targets?.recOf(hit.object) : null;
  // ...and put a magazine into it, through the simulation
  const cart = g.pistol.cart;
  for (let i = 0; i < 12; i++) {
    const d = dir.clone();
    d.x += (Math.random() - 0.5) * 0.004;
    d.y += (Math.random() - 0.5) * 0.004;
    d.z += (Math.random() - 0.5) * 0.004;
    g.bullets.spawn(cart, from.clone(), d.normalize(), all, { from: 'probe' });
  }
  window.__shot = { x: p.x, y: p.y, z: p.z, fx: from.x, fy: from.y, fz: from.z };
  window.__wasFoot = was;
  return {
    hasShell: !!shell,
    shellInCollidables: near.includes(shell),
    handedToTheGun: !!own,
    hits: hit ? (hit.object.name || '(unnamed)') : null,
    recIsOwn: !!rec?.own,
    health: g.health,
  };
});
console.log('before:', JSON.stringify(out));
await page.waitForTimeout(1500);
const after = await page.evaluate(() => {
  const g = window.__game;
  const rec = g.targets?._ownRec?.();
  const tears = [];
  for (const [k, v] of Object.entries(rec?._tears ?? {})) {
    if (v) tears.push(`${k}:${v.length}t/${v.reduce((a, t) => a + t.n, 0)}r`);
  }
  let biggest = 0;
  for (const v of Object.values(rec?._tears ?? {})) {
    if (v) for (const t of v) biggest = Math.max(biggest, t.r);
  }
  if (g.foot) g.foot.active = window.__wasFoot;
  return {
    health: g.health,
    marks: (rec?._marks ?? []).filter((m) => !m.dropped).length,
    tears, biggestHoleCm: +(biggest * 200).toFixed(1),
    holesInShader: rec?._holeSet?.uniforms.uTearCount.value ?? 0,
  };
});
console.log('after 12 rounds:', JSON.stringify(after));
await browser.close();
