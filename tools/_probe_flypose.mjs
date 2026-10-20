// DID THE HANDS AND FEET ACTUALLY LAND ON THE CONTROLS?
//   MODEL=banana PLANE=spitfire node tools/_probe_flypose.mjs
//
// For each personage in each plane: where the seat pan ended up, where the
// stick/throttle/pedal grips are, where the hand and foot bones actually got
// to, and how far short each one fell. A limb that misses by more than a few
// centimetres is a limb sticking out of the aeroplane.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const MODELS = (process.env.MODEL || 'banana,astronaut,anime,elephant,cyberfem').split(',');
const PLANES = (process.env.PLANE || 'spitfire,f15,aerobatic,a7,cessna310,b737').split(',');

const browser = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport:{width:900,height:620} });
page.on('pageerror',(e)=>console.log('  [err]',e.message));
page.on('console',(m)=>{ if(m.type()==='error') console.log('  [con]',m.text().slice(0,180)); });
await enterGame(page, { user:'shotbot', pass:'shotbot123' });
await page.waitForFunction(()=>window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3500);

const rows = await page.evaluate(async ({ MODELS, PLANES }) => {
  const g = window.__game, THREE = window.__THREE;
  const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
  await g.foot.ready;
  g.renderer.setAnimationLoop(null);
  const out = [];
  for (const id of PLANES) {
    const def = planeById(id);
    const next = makeGameplayPlane(await loadPlane(id), def);
    next.position.copy(g.plane.position);
    next.quaternion.identity();
    next.userData.id = id;
    g.scene.remove(g.plane); g.plane = next; g.scene.add(next);
    g.halfSpan = next.userData.halfSpan;
    g.foot.seated = false;
    await new Promise((r)=>setTimeout(r, 500));   // the kit loads async
    for (const m of MODELS) {
      g.foot.leaveSeat();
      g.foot.rig?.obj.parent?.remove(g.foot.rig.obj);
      g.foot._modelId = m;
      await g.foot._loadBody();
      g.airborne = false; g.speed = 0; g.vel.set(0,0,0);
      g.plane.quaternion.identity(); g.plane.updateMatrixWorld(true);
      g.foot.sitInPlane();
      for (let i=0;i<40;i++) g.foot.updateSeated(1/60);
      g.plane.updateMatrixWorld(true);
      const rig = g.foot.rig;
      const c = g.plane.userData.cockpit;
      const parts = g.plane.userData.controls;
      const W = (o)=> o ? o.getWorldPosition(new THREE.Vector3()) : null;
      const gripOf = (p)=> p?.userData?.grip ? W(p.userData.grip) : null;
      const handR = rig._handPoint?.('R'), handL = rig._handPoint?.('L');
      const legs = rig._legRig?.();
      const d = (a,b)=> (a&&b) ? +a.distanceTo(b).toFixed(3) : null;
      // the LEG GATE's own inputs, and where the shin actually ends up: the
      // foot bone is not the contact point on a body whose boot is parked
      const legInfo = (side) => {
        const L = legs?.[side]; if (!L?.thigh) return null;
        L.thigh.updateWorldMatrix(true, false);
        const H = new THREE.Vector3().setFromMatrixPosition(L.thigh.matrixWorld);
        const tgt = gripOf(side === 'L' ? parts?.pedalL : parts?.pedalR);
        if (!tgt) return null;
        L.calf.updateWorldMatrix(true, false);
        const K = new THREE.Vector3().setFromMatrixPosition(L.calf.matrixWorld);
        const q = new THREE.Quaternion(); const sc = new THREE.Vector3();
        L.calf.matrixWorld.decompose(new THREE.Vector3(), q, sc);
        const tip = L.ankle
          ? new THREE.Vector3().setFromMatrixPosition((L.ankle.updateWorldMatrix(true,false), L.ankle.matrixWorld))
          : K.clone().add(L.calfAxis.clone().applyQuaternion(q).multiplyScalar(L.l2));
        const len = L.l1 + L.l2;
        const dist = H.distanceTo(tgt);
        return { d:+dist.toFixed(3), len:+len.toFixed(3),
          w:+Math.max(0,Math.min(1,(len*1.03-dist)/(len*0.07))).toFixed(2),
          miss:+tip.distanceTo(tgt).toFixed(3) };
      };
      // how much of the body is outside the hull: the local box vs the hull
      const lb = rig._localBox();
      const rp = rig.obj.position;
      // the same thing measured the other way: a world Box3, with the plane
      // level so the two frames differ only by a translation
      const wb = new THREE.Box3().setFromObject(rig.obj, true);
      const planeY = g.plane.position.y;
      // ...and what each mesh under the rig contributes, to find a liar
      const big = [];
      { const bb = new THREE.Box3();
        rig.obj.traverse((o)=>{
          if (o.isSkinnedMesh) { o.skeleton.update(); o.computeBoundingBox(); bb.copy(o.boundingBox).applyMatrix4(o.matrixWorld); }
          else if (o.isMesh) { o.geometry.computeBoundingBox(); bb.copy(o.geometry.boundingBox).applyMatrix4(o.matrixWorld); }
          else return;
          big.push({ n:(o.name||'?').slice(0,24), skinned:!!o.isSkinnedMesh, vis:o.visible,
            top:+(bb.max.y-planeY).toFixed(2), bot:+(bb.min.y-planeY).toFixed(2) });
        });
        big.sort((a,b)=>b.top-a.top); }
      out.push({ plane:id, model:m,
        seatY:+c.seat.y.toFixed(2), bodyY:+rp.y.toFixed(2),
        hullBot:+c.bot.toFixed(2), ceil:Number.isFinite(c.ceil)?+c.ceil.toFixed(2):null,
        crownAt:+(rp.y+lb.max.y).toFixed(2), lowAt:+(rp.y+lb.min.y).toFixed(2),
        worldCrown:+(wb.max.y-g.plane.position.y).toFixed(2), worldLow:+(wb.min.y-g.plane.position.y).toFixed(2),
        tallest: big.slice(0,3), lowest: big.slice(-2),
        stick: d(W(handR), gripOf(parts?.stick)),
        throttle: d(W(handL), gripOf(parts?.throttle)),
        legR: legInfo('R'), legL: legInfo('L'),
        arm:+(rig.climbMetrics().armLen ?? 0).toFixed(2),
        leg:+(rig.climbMetrics().legLen ?? 0).toFixed(2),
      });
    }
  }
  return out;
}, { MODELS, PLANES });

console.log('plane      body        seat   bodyY  hullBot ceil   crown   low |  stick  thr  | right leg: hip->pedal / leg length, plant weight, how far the shin tip missed | arm');
for (const r of rows) {
  const f = (n,w=6)=>String(n===null?'-':n).padStart(w);
  const bad = [];
  if (r.lowAt !== null && r.hullBot !== null && r.lowAt < r.hullBot - 0.05) bad.push('THROUGH THE BELLY');
  if (r.ceil !== null && r.crownAt > r.ceil + 0.03) bad.push('HEAD THROUGH THE HOOD');
  if (r.stick > 0.12) bad.push('hand off the stick');
  if ((r.legR?.miss ?? 0) > 0.12 || (r.legL?.miss ?? 0) > 0.12) bad.push('feet off the pedals');
  if (Math.abs(r.worldCrown - r.crownAt) > 0.08) console.log(`    !! ${r.plane}/${r.model} localBox crown ${r.crownAt} vs world ${r.worldCrown} | tallest ${JSON.stringify(r.tallest)} lowest ${JSON.stringify(r.lowest)}`);
  console.log(`${r.plane.padEnd(10)} ${r.model.padEnd(10)} ${f(r.seatY)} ${f(r.bodyY)} ${f(r.hullBot)} ${f(r.ceil)} ${f(r.crownAt)} ${f(r.lowAt)} |`
    + `${f(r.stick)}${f(r.throttle)} | leg d=${f(r.legR?.d,5)}/len${f(r.legR?.len,5)} w=${f(r.legR?.w,4)} miss=${f(r.legR?.miss,5)} |${f(r.arm,5)}  ${bad.join(' · ')}`);
}
await browser.close();
