// WHAT A DENT LOOKS LIKE. The same aeroplane from the same camera, with and
// without the crash -- which is the only way to see a dent at all: crumpled
// metal is obvious beside the clean panel and easy to miss on its own.
//
// The pairs are shot in that order on purpose. The machine is crashed first,
// the camera is aimed at the crushed vertices THEMSELVES (dents.js keeps how
// much metal has been taken out of each one, so the crater is found rather
// than guessed at), the frame is taken -- and then the metal is beaten out
// with dents.repair() and the same frame is taken again. Nothing moves
// between the two but the skin.
//
//   1. a wall at 28 m/s head on     -- the player's nose folds
//   2. another aeroplane at 26 m/s  -- BOTH machines carry it
//
//   node tools/_shot_dents.mjs [plane] [other]     SHOT_DIR=... to redirect
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const ME = process.argv[2] || 'spitfire';
const THEM = process.argv[3] || 'f4f';

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30, null, { timeout: 420000 });

await page.evaluate(() => {
  const THREE = window.__THREE;
  // WHERE THE CRATER IS. dents.js keeps, per vertex, how much metal has been
  // crushed out of it; the middle of what moved is the middle of the dent.
  window.__dentAt = (root) => {
    const g = window.__game;
    const v = new THREE.Vector3();
    const at = new THREE.Vector3();
    let n = 0;
    let deepest = 0;
    for (const mesh of g.dents._parts(root)) {
      const st = g.dents.state.get(mesh.geometry);
      if (!st) continue;
      const P = mesh.geometry.attributes.position.array;
      mesh.updateWorldMatrix(true, false);
      for (let i = 0; i < st.acc.length; i++) {
        if (st.acc[i] <= 1e-4) continue;
        n++;
        // the DEEPEST vertex, not the average of them: an airframe can carry
        // several craters at once and the middle of those is thin air
        if (st.acc[i] <= deepest) continue;
        deepest = st.acc[i];
        at.copy(v.fromArray(P, i * 3)).applyMatrix4(mesh.matrixWorld);
      }
    }
    return n ? { at: at.toArray(), verts: n, deep: deepest } : null;
  };
  for (const id of ['hud', 'hud-controls', 'skillbar', 'targetframe', 'combat-hud', 'credits']) {
    const el = document.getElementById(id);
    if (el) el.style.display = 'none';   // the HUD is not the subject
  }
});

// THE LOOP OWNS THE CAMERA (tools/_shot_ram.mjs): stop it, frame by hand,
// render, shoot, start it again -- or every frame is the view from the chase
// camera. The aim is a world point and the eye sits off the machine's own
// shoulder, so a pair taken either side of a repair lines up exactly.
const shoot = async (name, at, dist, side = 1) => {
  await page.evaluate(({ at, dist, side }) => {
    const g = window.__game; const THREE = window.__THREE;
    g.renderer.setAnimationLoop(null);
    const t = window.__subject ?? g.plane;
    t.updateWorldMatrix(true, true);
    const target = new THREE.Vector3(...at);
    // LOOK AT IT FROM OUTSIDE. A fixed camera off the machine's shoulder
    // shoots straight through the fuselage whenever the crater happens to be
    // on the other side of it, and the two frames of the pair then differ by
    // nothing at all. The eye goes out along the line from the aeroplane's
    // own centre through the dent, which is the one direction that cannot be
    // looking at the back of it, tipped up a little so the light rakes.
    const mid = new THREE.Box3().setFromObject(t).getCenter(new THREE.Vector3());
    const out = target.clone().sub(mid);
    if (out.lengthSq() < 1e-4) out.set(side, 0, 0);
    out.normalize();
    // ...and swung round the machine's own axis and lifted, because straight
    // down the outward normal of a nose dent is dead ahead of the spinner,
    // which is the one thing standing in front of it.
    const q = t.getWorldQuaternion(new THREE.Quaternion());
    const eye = out.multiplyScalar(0.75)
      .addScaledVector(new THREE.Vector3(side, 0, 0).applyQuaternion(q), 0.55)
      .addScaledVector(new THREE.Vector3(0, 1, 0), 0.5)
      .normalize();
    g.camera.position.copy(target).addScaledVector(eye, dist);
    g.camera.lookAt(target);
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { at, dist, side });
  await page.screenshot({ path: `${OUT}/dent_${name}.png` });
};

const thaw = () => page.evaluate(() => {
  const g = window.__game;
  g.renderer.setAnimationLoop(() => g._tick());
});

const stage = async (me) => page.evaluate(async (me) => {
  const g = window.__game;
  g.renderer.setAnimationLoop(() => g._tick());
  g.resetPlane();
  await new Promise((r) => setTimeout(r, 200));
  if (g.def.id !== me) {
    const rec = g.targets.list.find((r) => r.def.id === me && !r.dead && !r.loose);
    if (rec) g.swapToPlane(rec);
    await new Promise((r) => setTimeout(r, 300));
  }
  g.health = g.maxHealth;
  g._updateHealthBar();
  window.__subject = g.plane;
  return { id: g.def.id, nose: g.noseLen, size: g.plane.userData.size };
}, me);

const info = await stage(ME);
console.log(`flying the ${info.id} (${info.size.toFixed(1)} m, nose ${info.nose.toFixed(1)} m out front)`);

// ---- 1. straight into a facade -------------------------------------------
const wall = await page.evaluate(() => {
  const g = window.__game; const THREE = window.__THREE;
  g.airborne = true;
  g.plane.quaternion.identity();
  g.plane.updateMatrixWorld(true);
  // A FACADE TO HIT, stood up in front of the machine: as far as the swept
  // contact, the solver and the dent are concerned it is a building.
  const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(g.plane.quaternion);
  const box = new THREE.Mesh(
    new THREE.BoxGeometry(40, 30, 4),
    new THREE.MeshStandardMaterial({ color: 0x8a8278, roughness: 0.9 })
  );
  box.position.copy(g.plane.position).addScaledVector(fwd, 30);
  box.quaternion.copy(g.plane.quaternion);
  box.updateMatrixWorld(true);
  box.geometry.computeBoundsTree?.();
  g.scene.add(box);
  if (g.world._addCollidable) g.world._addCollidable(box);
  else g.world.collidables.push(box);
  // Flown at by hand, one 60 Hz step at a time, through the game's own sweep.
  // 28 m/s and not a metre more: past 34 a square-on arrival at a building is
  // the fatal branch in game._touchWall, the airframe comes apart and what is
  // left to photograph is a fireball.
  const sp = 28;
  const v = fwd.clone().multiplyScalar(sp);
  const dt = 1 / 60;
  let hit = false;
  for (let i = 0; i < 120 && !hit; i++) {
    g._prevPos.copy(g.plane.position);
    g.plane.position.addScaledVector(v, dt);
    g.vel.copy(v);
    g.speed = sp;
    hit = g._touchWall(dt);
  }
  for (let i = 0; i < 20; i++) g.dents.update();
  g.vel.set(0, 0, 0);
  g.speed = 0;
  g.scene.remove(box);
  g.world._removeCollidable?.(box);
  return { hit, hp: Math.round(g.health), crashed: g.crashed, dent: window.__dentAt(g.plane) };
});
console.log(`wall at 28 m/s: ${wall.hit ? 'contact' : 'NO CONTACT'}, ${wall.hp} hp,`
  + ` ${wall.dent ? `${wall.dent.verts} vertices crushed` : 'NO DENT'}`);
if (wall.dent) {
  await shoot('1_wall_after', wall.dent.at, 2.2);
  await page.evaluate(() => window.__game.dents.repair(window.__game.plane));
  await shoot('1_wall_before', wall.dent.at, 2.2);
  await thaw();
}

// ---- 2. another aeroplane ------------------------------------------------
await stage(ME);
const ram = await page.evaluate(async ({ them }) => {
  const g = window.__game; const THREE = window.__THREE;
  const t = g.targets.list.find((r) => r.def.id === them) ?? g.targets.list[0];
  t.hp = t.maxHp; t.dead = false;
  if (t.collider) {
    g.world._removeCollidable(t.collider);
    delete t.collider.userData.planeRec;
    t.collider = null;
  }
  g.scene.attach(t.wrapper);
  t.wrapper.matrixAutoUpdate = true;
  t.wrapper.position.y += 400;             // clear air over the hall
  t.wrapper.updateMatrixWorld(true);
  g.planephys._bakeCollider(t);
  const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
  const start = tp.clone().add(new THREE.Vector3(0, 0, 45));
  const sp = 26;
  const v = tp.clone().sub(start).normalize().multiplyScalar(sp);
  g.plane.position.copy(start);
  g._prevPos.copy(start);
  g.plane.quaternion.setFromRotationMatrix(
    new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));
  g.airborne = true;
  const dt = 1 / 60;
  let hit = false;
  for (let i = 0; i < 200 && !hit; i++) {
    g._prevPos.copy(g.plane.position);
    g.plane.position.addScaledVector(v, dt);
    g.vel.copy(v);
    g.speed = sp;
    hit = g._touchWall(dt);
  }
  for (let i = 0; i < 20; i++) g.dents.update();
  g.vel.set(0, 0, 0);
  g.speed = 0;
  window.__them = t;
  return {
    hit, them: t.def.id, hp: Math.round(g.health), theirHp: t.hp, dead: !!t.dead,
    mine: window.__dentAt(g.plane), theirs: window.__dentAt(t.wrapper),
  };
}, { them: THEM });
console.log(`nose to nose with the ${ram.them} at 26 m/s: ${ram.hit ? 'contact' : 'NO CONTACT'},`
  + ` we ${ram.hp} hp / they ${ram.theirHp} hp${ram.dead ? ' (destroyed)' : ''}`);
console.log(`   ours:   ${ram.mine ? `${ram.mine.verts} vertices crushed` : 'NO DENT'}`);
console.log(`   theirs: ${ram.theirs ? `${ram.theirs.verts} vertices crushed` : 'NO DENT'}`);

if (ram.mine) {
  // the other machine is nose to nose with ours and squarely in the way
  await page.evaluate(() => {
    window.__them.wrapper.position.y -= 120;
    window.__them.wrapper.updateMatrixWorld(true);
  });
  await shoot('2_mine_after', ram.mine.at, 2.2);
  await page.evaluate(() => window.__game.dents.repair(window.__game.plane));
  await shoot('2_mine_before', ram.mine.at, 2.2);
}
if (ram.theirs) {
  await page.evaluate(() => {
    const g = window.__game;
    window.__them.wrapper.position.y += 120;    // back where it was hit
    window.__them.wrapper.updateMatrixWorld(true);
    g.plane.position.y -= 120;                  // ...and ours out of the way
    g.plane.updateMatrixWorld(true);
    window.__subject = window.__them.wrapper;
  });
  await shoot('2_theirs_after', ram.theirs.at, 2.2, -1);
  await page.evaluate(() => window.__game.dents.repair(window.__them.wrapper));
  await shoot('2_theirs_before', ram.theirs.at, 2.2, -1);
}
await thaw();

console.log(`frames in ${OUT}`);
await browser.close();
