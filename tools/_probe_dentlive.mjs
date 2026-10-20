// THE PLAYER'S OWN EXPERIENCE. Not a staged contact in clear air: this takes
// off, flies into a parked machine in the hall under the game's own flight
// model and its own loop, and then looks at the result through the CHASE
// CAMERA -- the view the pilot actually has. Every call into the dent system
// is logged, so if nothing is bent the log says which gate turned it away.
//   node tools/_probe_dentlive.mjs [closing m/s]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const SPEED = Number(process.argv[2] || 45);

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 2 });
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text().slice(0, 160)); });
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30, null, { timeout: 420000 });

// log every way in
await page.evaluate(() => {
  const g = window.__game;
  window.__log = [];
  const crush = g.dents.crush.bind(g.dents);
  g.dents.crush = (root, point, into, energy, opts) => {
    const ok = crush(root, point, into, energy, opts);
    window.__log.push({ call: 'crush', who: root === g.plane ? 'PLAYER' : (root.name || 'them'),
      E: Math.round(energy), ok, queued: g.dents.queue.length });
    return ok;
  };
  const dent = g.dents._dent.bind(g.dents);
  g.dents._dent = (job, mesh) => {
    const r = dent(job, mesh);
    window.__log.push({ call: '_dent', who: job.root === g.plane ? 'PLAYER' : 'them',
      mesh: mesh.name.slice(0, 16), ok: r, parent: !!mesh.parent,
      R: +job.R.toFixed(2), D: +job.D.toFixed(2) });
    return r;
  };
  const near = g.dents._near.bind(g.dents);
  g.dents._near = (root, point, R) => {
    const r = near(root, point, R);
    window.__log.push({ call: '_near', who: root === g.plane ? 'PLAYER' : 'them', found: r.length,
      parts: g.dents._parts(root).length });
    return r;
  };
  const skin = g.dents._findSkin.bind(g.dents);
  g.dents._findSkin = (meshes, point, reach) => {
    const r = skin(meshes, point, reach);
    window.__log.push({ call: '_findSkin', found: !!r, mesh: r?.mesh?.name?.slice(0, 16) ?? null });
    return r;
  };
  const ram = g.planephys.ram.bind(g.planephys);
  g.planephys.ram = (rec, hit, d, sp, part) => {
    const r = ram(rec, hit, d, sp, part);
    if (r) window.__target = rec;          // the machine actually struck

    window.__log.push({ call: 'ram', them: rec.def?.id, sp: +sp.toFixed(1), part: part ?? 'nose', handled: r });
    return r;
  };
  const touch = g._touchWall.bind(g);
  g._touchWall = (dt) => {
    const r = touch(dt);
    if (r) window.__log.push({ call: '_touchWall', hit: true });
    return r;
  };
});

// FLY IT IN. The plane is lined up on a parked machine and then left alone:
// the game's own loop integrates it, so this is the contact the player gets.
const setup = await page.evaluate(async ({ sp }) => {
  const g = window.__game; const THREE = window.__THREE;
  g.resetPlane();
  await new Promise((r) => setTimeout(r, 300));
  // the parked machine nearest the hall door, as a pilot would meet it
  const t = g.targets.list.find((r) => r.def.id === 'f4f') ?? g.targets.list[0];
  const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
  const dir = new THREE.Vector3(0, 0, 1).applyQuaternion(t.wrapper.getWorldQuaternion(new THREE.Quaternion()));
  const start = tp.clone().addScaledVector(dir, 120).setY(tp.y + 2);
  const v = tp.clone().sub(start).normalize().multiplyScalar(sp);
  g.plane.position.copy(start);
  g._prevPos.copy(start);
  g.plane.quaternion.setFromRotationMatrix(
    new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));
  g.airborne = true;
  g.vel.copy(v);
  g.speed = sp;
  g.health = g.maxHealth;
  window.__target = t;
  return { them: t.def.id, from: start.toArray(), at: tp.toArray() };
}, { sp: SPEED });
console.log(`flying a ${await page.evaluate(() => window.__game.def.id)} into the parked ${setup.them} at ${SPEED} m/s`);

// let the game fly it -- no hand-stepping
await page.waitForFunction(() => window.__log.some((l) => l.call === 'ram') || window.__game.crashed,
  null, { timeout: 20000 }).catch(() => {});
await page.waitForTimeout(1500);

const out = await page.evaluate(() => {
  const g = window.__game; const THREE = window.__THREE;
  const look = (root) => {
    let verts = 0;
    let deep = 0;
    let panels = 0;
    let vcol = 0;
    let darkest = 1;
    for (const mesh of g.dents._parts(root)) {
      const st = g.dents.state.get(mesh.geometry);
      if (!st) continue;
      let n = 0;
      for (let i = 0; i < st.acc.length; i++) if (st.acc[i] > 1e-4) { n++; deep = Math.max(deep, st.acc[i]); }
      if (!n) continue;
      panels++; verts += n;
      const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
      if (mats.some((m) => m?.vertexColors)) vcol++;
      const col = mesh.geometry.attributes.color;
      const C = col?.array;
      if (C) for (let i = 0; i < C.length; i++) darkest = Math.min(darkest, C[i]);
      window.__paint = {
        mat: mats.map((m) => `${m.type}/vc=${m.vertexColors}/op=${m.opacity}`).join(','),
        colCount: col?.count ?? 0, posCount: mesh.geometry.attributes.position.count,
        meshName: mesh.name.slice(0, 20),
        // what the crushed vertices average
        avg: (() => {
          if (!C) return null;
          let sum = 0; let k = 0;
          for (let i = 0; i < st.acc.length; i++) {
            if (st.acc[i] <= 1e-4) continue;
            sum += C[i * 3]; k++;
          }
          return k ? +(sum / k).toFixed(3) : null;
        })(),
      };
    }
    return { panels, verts, deep: +deep.toFixed(3), vcol, darkest: +darkest.toFixed(2) };
  };
  return {
    log: window.__log,
    crashed: g.crashed, hp: Math.round(g.health),
    them: { id: window.__target.def?.id, hp: window.__target.hp,
      dead: !!window.__target.dead, loose: !!window.__target.loose },
    mine: look(g.plane), theirs: look(window.__target.wrapper),
    queue: g.dents.queue.length,
    paint: window.__paint,
  };
});
console.log(JSON.stringify(out.log, null, 1));
console.log(`we ${out.crashed ? 'CRASHED' : `${out.hp} hp`} · the ${out.them.id} ${out.them.dead ? 'DESTROYED' : `${out.them.hp} hp`}`
  + ` · queue ${out.queue}`);
const say = (w, d) => `  ${w}: ${d.panels} panel(s), ${d.verts} vertices crushed, ${d.deep} m deep`
  + ` · ${d.vcol} material(s) on vertex colours, darkest ${d.darkest}`;
console.log(say('our metal  ', out.mine));
console.log(say('their metal', out.theirs));
console.log('paint:', JSON.stringify(out.paint));

// WHAT THE PILOT SEES: the game's own camera, untouched.
await page.screenshot({ path: `${OUT}/dentlive_chase.png` });
await page.evaluate(() => {
  const g = window.__game; const THREE = window.__THREE;
  g.renderer.setAnimationLoop(null);
  const t = window.__target.wrapper;
  t.updateWorldMatrix(true, true);
  const at = new THREE.Box3().setFromObject(t).getCenter(new THREE.Vector3());
  const d = Math.max(9, (t.userData.size ?? 12) * 0.9);
  g.camera.position.copy(at).add(new THREE.Vector3(0.8, 0.45, 0.8).normalize().multiplyScalar(d));
  g.camera.lookAt(at);
  g.camera.updateMatrixWorld();
  for (const id of ['hud', 'hud-controls', 'skillbar', 'targetframe', 'combat-hud', 'credits']) {
    const el = document.getElementById(id);
    if (el) el.style.display = 'none';
  }
  g.renderer.render(g.scene, g.camera);
});
await page.screenshot({ path: `${OUT}/dentlive_struck.png` });
console.log(`chase view in ${OUT}/dentlive_chase.png`);
console.log(errs.length ? `page errors: ${[...new Set(errs)].slice(0, 5).join(' | ')}` : 'no page errors');
await browser.close();
