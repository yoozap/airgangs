import { chromium } from 'playwright';
const b = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
for (const body of ['banana', 'astronaut']) {
  const p = await b.newPage({ viewport: { width: 400, height: 400 } });
  p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
  p.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE', m.text()); });
  await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
  await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });
  await p.evaluate(() => { window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30); });
  const out = await p.evaluate(() => {
    const rig = window.__rig; const T = window.__THREE;
    const nm = (b) => b ? b.name : null;
    const r3 = (v) => [+v.x.toFixed(3), +v.y.toFixed(3), +v.z.toFixed(3)];
    const met = rig.climbMetrics();
    const legs = rig._legRig();
    const li = legs && Object.fromEntries(['L','R'].map((s) => [s, { thigh: nm(legs[s].thigh), calf: nm(legs[s].calf), ankle: nm(legs[s].ankle), foot: nm(legs[s].foot), toe: nm(legs[s].toe), parked: legs[s].parked, l1: +legs[s].l1.toFixed(3), l2: +legs[s].l2.toFixed(3), calfAxis: r3(legs[s].calfAxis) }]));
    const hp = { L: nm(rig._handPoint('L')), R: nm(rig._handPoint('R')) };
    // snapshot the whole skeleton, pose the legs + plant + settle, then release and compare
    const bones = []; rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    const snap = () => bones.map((b) => [b.position.clone(), b.quaternion.clone()]);
    const s0 = snap();
    rig.obj.updateMatrixWorld(true);
    // a plant 0.35 m ahead of the right hip and 0.3 m up the "wall", knee to the right
    const hip = new T.Vector3().setFromMatrixPosition(legs.R.thigh.matrixWorld);
    const plant = hip.clone().add(new T.Vector3(0.1, -0.45, -0.3));
    const kneeDir = new T.Vector3(0.8, 0.5, -0.25).normalize();
    rig.setPullPose(1, 0.5, 'R');
    const kneeKeyed = new T.Vector3().setFromMatrixPosition((legs.R.calf.updateWorldMatrix(true,false), legs.R.calf.matrixWorld));
    const reach = rig.plantFoot('R', plant, kneeDir, new T.Vector3(0, 0.85, -0.5).normalize(), 1);
    rig.settleFeet();
    rig.obj.updateMatrixWorld(true);
    const knee = new T.Vector3().setFromMatrixPosition(legs.R.calf.matrixWorld);
    const shinEnd = legs.R.ankle ? new T.Vector3().setFromMatrixPosition(legs.R.ankle.matrixWorld) : null;
    const footW = legs.R.foot ? new T.Vector3().setFromMatrixPosition(legs.R.foot.matrixWorld) : null;
    const hookR = rig.setHandHook('R', new T.Vector3(0, -1, 0), 1);
    rig.setPullPose(0);   // release everything
    const s1 = snap();
    let maxDq = 0, maxDp = 0, worst = null;
    bones.forEach((b, i) => { const dq = s0[i][1].angleTo(s1[i][1]); const dp = s0[i][0].distanceTo(s1[i][0]); if (dq > maxDq) { maxDq = dq; worst = b.name; } maxDp = Math.max(maxDp, dp); });
    return { met, legs: li, sign: legs?.sign, ankleOff: legs?.ankleOff, hp, reach: +reach.toFixed(3), plant: r3(plant), hip: r3(hip), kneeKeyed: r3(kneeKeyed), knee: r3(knee), shinEnd: shinEnd && r3(shinEnd), footW: footW && r3(footW), hookR, release: { maxDq: +maxDq.toFixed(5), maxDp: +maxDp.toFixed(5), worst }, parkedSave: rig._parkedSave };
  });
  console.log('=====', body);
  console.log(JSON.stringify(out, null, 1).replace(/\n\s*/g, ' '));
  await p.close();
}
await b.close();
