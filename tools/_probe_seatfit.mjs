// DOES EVERY PERSONAGE ACTUALLY SIT IN EVERY AEROPLANE?
//   node tools/_probe_seatfit.mjs
//
// Two tables and a verdict. The cockpit table is what findCockpit() measured
// off each airframe (tools/_probe_cockpit.mjs prints it too, with the span);
// the body table is what each personage is once it is FOLDED INTO A SEAT --
// how far its crown stands over its own hips, how wide its shoulders are, how
// far its arms and legs reach. The fit matrix is then arithmetic: a body fits
// a cockpit when its seated crown clears the canopy and its shoulders clear
// the sills, and it can FLY it when the stick and the pedals are inside the
// reach of those arms and legs.
//
// Reach targets are the cockpit kit's own furniture (tools/build_cockpit.py,
// Blender +Y forward/+Z up -> glTF x, z, -y), quoted from the seat pan:
import { chromium } from 'playwright';

const KIT = {
  stick: [0, 0.335, -0.327],      // stick_grip   — right hand
  throttle: [-0.30, 0.205, -0.215], // thr_knob   — left hand
  pedalR: [0.135, -0.058, -0.948],  // pedal_pad_1
  pedalL: [-0.135, -0.058, -0.948],
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('  [con]', m.text()); });
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });

const data = await page.evaluate(async (KIT) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const { PERSONAGES, makeCharacter, HERO_HEIGHT } = await import('/src/characters.js');

  // ---- the bodies, folded into a seat --------------------------------------
  const bodies = [];
  for (const p of PERSONAGES) {
    try {
      const rig = await makeCharacter(p.id);
      const holder = new THREE.Group();
      holder.add(rig.obj);
      // IDLE, NOT THE BIND POSE. Without a clip asserted the skeleton is the
      // T-pose it was rigged in, and every width this measures is an arm span:
      // the frog came out 2.29 m "across the shoulders".
      rig.play('Idle', 0);
      rig.update(0.5);
      rig.update(0.016);
      const met = rig.climbMetrics();
      const hip = rig.setSeated(1);
      rig.obj.position.y = -(hip ?? 0);        // hips on the pan, as foot.js seats it
      holder.updateMatrixWorld(true);
      // the seated body, in SEAT space (origin = the pan)
      const box = new THREE.Box3();
      const b = new THREE.Box3();
      const above = new THREE.Box3();          // only what is out over the sill
      rig.obj.traverse((o) => {
        if (o.isSkinnedMesh) { o.skeleton.update(); o.computeBoundingBox(); b.copy(o.boundingBox).applyMatrix4(o.matrixWorld); }
        else if (o.isMesh) { o.geometry.computeBoundingBox(); b.copy(o.geometry.boundingBox).applyMatrix4(o.matrixWorld); }
        else return;
        box.union(b);
        if (b.max.y > 0.1) above.union(b);
      });
      // where the hands and the shoulders ended up, seated
      const pt = (bone) => { if (!bone) return null; bone.updateWorldMatrix(true, false);
        const v = new THREE.Vector3().setFromMatrixPosition(bone.matrixWorld); return [v.x, v.y, v.z]; };
      const arms = rig._rollRig();
      const legs = rig._legRig();
      const handR = pt(rig._handPoint('R')) ?? pt(arms?.R?.hand);
      const shR = pt(arms?.R?.upper);
      const shL = pt(arms?.L?.upper);
      const hipR = pt(legs?.R?.thigh);
      const footR = pt(legs?.R?.foot ?? legs?.R?.ankle);
      bodies.push({
        id: p.id, name: p.name,
        bodyH: +rig.bodyH.toFixed(3),
        hip: +(hip ?? 0).toFixed(3),
        crown: +box.max.y.toFixed(3),           // over the pan
        low: +box.min.y.toFixed(3),
        halfW: +(Math.max(Math.abs(box.max.x), Math.abs(box.min.x))).toFixed(3),
        aboveHalfW: +(Math.max(Math.abs(above.max.x), Math.abs(above.min.x))).toFixed(3),
        aboveTop: +above.max.y.toFixed(3),
        fore: +box.min.z.toFixed(3),            // how far forward the knees/feet go
        back: +box.max.z.toFixed(3),
        armLen: +(met.armLen ?? 0).toFixed(3),
        legLen: +(met.legLen ?? 0).toFixed(3),
        shoulderHalf: +(met.shoulderHalf ?? 0).toFixed(3),
        hands: handR, shR, shL, hipR, footR,
        hasLegs: !!rig._crouchRig(), hasArms: !!arms,
      });
      holder.remove(rig.obj);
    } catch (e) { bodies.push({ id: p.id, error: e.message }); }
  }

  // ---- the cockpits ---------------------------------------------------------
  const planes = [];
  for (const def of PLANES) {
    try {
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      const c = plane.userData.cockpit;
      plane.updateMatrixWorld(true);
      const air = new THREE.Box3().setFromObject(plane, true);
      const size = air.getSize(new THREE.Vector3());
      planes.push({
        id: def.id, role: def.role,
        span: +(plane.userData.halfSpan * 2).toFixed(2),
        len: +size.z.toFixed(2),
        grow: c ? +c.grow.toFixed(3) : 1,
        seatY: c ? +c.seat.y.toFixed(3) : null,
        seatZ: c ? +c.seat.z.toFixed(3) : null,
        // how far back along the fuselage the station landed, 0 = nose
        stationFrac: c ? +((c.seat.z - air.min.z) / Math.max(1e-6, size.z)).toFixed(3) : null,
        top: c ? +c.top.toFixed(3) : null,
        deck: c ? +c.deck.toFixed(3) : null,
        floor: c ? +c.floor.toFixed(3) : null,
        room: c ? +c.room.toFixed(3) : null,
        ceil: c ? (Number.isFinite(c.ceil) ? +c.ceil.toFixed(3) : null) : null,
        halfW: c ? +c.halfW.toFixed(3) : null,
        closed: c ? c.closed : null,
        from: c?.from ?? '-',
      });
    } catch (e) { planes.push({ id: def.id, error: e.message }); }
  }
  return { bodies, planes, KIT, HERO_HEIGHT };
}, KIT);

const f = (n, w = 6, d = 2) => String(n === null || n === undefined ? '-' : (typeof n === 'number' ? n.toFixed(d) : n)).padStart(w);

console.log('\n==== THE BODIES, FOLDED INTO A SEAT ====');
console.log('body           stand   hip-over-soles  crown>pan  shoulders  arm    leg   knees-fwd  legs/arms');
for (const b of data.bodies) {
  if (b.error) { console.log(`${b.id.padEnd(14)} ERROR ${b.error}`); continue; }
  console.log(`${b.id.padEnd(14)}${f(b.bodyH)}m  ${f(b.hip)} m        ${f(b.crown)} m   ${f(b.halfW * 2)} m ${f(b.armLen)} ${f(b.legLen)}  ${f(-b.fore)} m    `
    + `${b.hasLegs ? 'legs' : '----'} ${b.hasArms ? 'arms' : '----'}`);
}

// what the WORST body needs — the aircraft have to hold all of them
const ok = data.bodies.filter((b) => !b.error);
const need = {
  crown: Math.max(...ok.map((b) => b.crown)),
  wide: Math.max(...ok.map((b) => b.aboveHalfW)) * 2,
  arm: Math.min(...ok.filter((b) => b.armLen > 0).map((b) => b.armLen)),
  leg: Math.min(...ok.filter((b) => b.legLen > 0).map((b) => b.legLen)),
};
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);
console.log(`\nworst case: crown ${need.crown.toFixed(2)} m over the pan, shoulders ${need.wide.toFixed(2)} m across,`
  + ` shortest arm ${need.arm.toFixed(2)} m, shortest leg ${need.leg.toFixed(2)} m`);

console.log('\n==== CAN THOSE ARMS AND LEGS WORK THE CONTROLS? ====');
console.log('(reach measured from the SEATED shoulder/hip to the kit\'s own stick, throttle and pedals)');
console.log('body            stick   vs arm   throttle vs arm   pedal   vs leg');
for (const b of data.bodies) {
  if (b.error || !b.shR) continue;
  const ds = b.shR ? dist(b.shR, KIT.stick) : null;
  const dt = b.shL ? dist(b.shL, KIT.throttle) : null;
  const dp = b.hipR ? dist(b.hipR, KIT.pedalR) : null;
  const flag = (d, l) => d === null ? '' : d > l * 0.985 ? '  OUT OF REACH' : '';
  console.log(`${b.id.padEnd(14)} ${f(ds)} m ${f(b.armLen)} m   ${f(dt)} m ${f(b.armLen)} m  ${f(dp)} m ${f(b.legLen)} m`
    + flag(ds, b.armLen) + flag(dt, b.armLen) + flag(dp, b.legLen));
}

console.log('\n==== THE COCKPITS ====');
console.log('plane         span    len  grow   station  seat y   deck   floor   room   ceil  halfW  closed  found in');
for (const p of data.planes) {
  if (p.error) { console.log(`${p.id.padEnd(12)} ERROR ${p.error}`); continue; }
  console.log(`${p.id.padEnd(12)}${f(p.span)} ${f(p.len)} ${f(p.grow)}  ${f(p.stationFrac)}  ${f(p.seatY)} ${f(p.deck)} ${f(p.floor)} ${f(p.room)} ${f(p.ceil)} ${f(p.halfW)}  ${p.closed ? 'closed' : 'open  '}  ${p.from}`);
}

console.log('\nThe FIT itself -- head under the glass, nothing under the belly, hands on');
console.log('the stick and feet on the pedals -- is measured per body per aeroplane by');
console.log('tools/_probe_flypose.mjs, which poses them for real and reads the result.');
await browser.close();
