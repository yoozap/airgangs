// THE SEAT, WITH THE TARGETS DRAWN. Same framing as tools/shot_cockpit.mjs,
// but with a coloured ball on every point the pose is solved onto -- red the
// stick grip, blue the throttle knob, green the two pedal pads, yellow the
// hand bones, white the shin tips -- and the aeroplane's skin turned to
// wireframe so you can see what is inside it and what is not.
//   MODEL=cyberfem PLANE=spitfire node tools/_probe_seatdebug.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.SHOT_DIR || '/tmp/seatdebug';
fs.mkdirSync(DIR, { recursive: true });
const MODELS = (process.env.MODEL || 'cyberfem').split(',');
const PLANES = (process.env.PLANE || 'spitfire').split(',');

const browser = await chromium.launch({ args:['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport:{width:1100,height:760} });
page.on('pageerror',(e)=>console.log('  [err]',e.message));
await enterGame(page, { user:'shotbot', pass:'shotbot123' });
await page.waitForFunction(()=>window.__game?.started,null,{timeout:420000});
await page.waitForTimeout(3500);

for (const id of PLANES) for (const m of MODELS) {
  const info = await page.evaluate(async ({ id, m }) => {
    const g = window.__game, THREE = window.__THREE;
    const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
    await g.foot.ready;
    if (g.plane.userData.id !== id) {
      const next = makeGameplayPlane(await loadPlane(id), planeById(id));
      next.position.copy(g.plane.position); next.quaternion.identity(); next.userData.id = id;
      g.scene.remove(g.plane); g.plane = next; g.scene.add(next);
      g.halfSpan = next.userData.halfSpan; g.foot.seated = false;
      await new Promise((r)=>setTimeout(r,500));
    }
    if (g.foot._modelId !== m) {
      g.foot.leaveSeat(); g.foot.rig?.obj.parent?.remove(g.foot.rig.obj);
      g.foot._modelId = m; await g.foot._loadBody();
    }
    g.airborne = false; g.speed = 0; g.vel.set(0,0,0);
    g.plane.quaternion.identity(); g.plane.updateMatrixWorld(true);
    g.foot.sitInPlane();
    g.renderer.setAnimationLoop(null);
    for (let i=0;i<40;i++) g.foot.updateSeated(1/60);
    g.plane.updateMatrixWorld(true);
    const rig = g.foot.rig, parts = g.plane.userData.controls;
    for (const o of (window.__dbg ?? [])) o.parent?.remove(o);
    window.__dbg = [];
    if (window.__WIRE === false) { window.__ball = () => {}; }
    const ball = (p, col, r=0.045) => { if(!p || window.__WIRE === false) return;
      const o = new THREE.Mesh(new THREE.SphereGeometry(r,12,8),
        new THREE.MeshBasicMaterial({color:col, depthTest:false}));
      o.renderOrder = 999; o.position.copy(p); g.scene.add(o); window.__dbg.push(o); };
    const W=(o)=> o ? o.getWorldPosition(new THREE.Vector3()) : null;
    ball(W(parts?.stick?.userData?.grip), 0xff2020);
    ball(W(parts?.throttle?.userData?.grip), 0x3060ff);
    ball(W(parts?.pedalL?.userData?.grip), 0x20ff40);
    ball(W(parts?.pedalR?.userData?.grip), 0x20ff40);
    ball(W(rig._handPoint?.('R')), 0xffe020, 0.035);
    ball(W(rig._handPoint?.('L')), 0xff8000, 0.035);
    const legs = rig._legRig?.();
    for (const s of ['L','R']) ball(W(legs?.[s]?.ankle ?? legs?.[s]?.foot), 0xffffff, 0.03);
    // the aeroplane in wireframe, so the skin stops hiding the answer
    // (WIRE=0 renders it shaded instead, for judging how it actually looks)
    window.__wire = [];
    if (window.__WIRE !== false) {
      // THE PILOT IS NOT PART OF THE AEROPLANE. It hangs off the wrapper while
      // seated, so a plain traverse wireframes the body as well and the one
      // thing the picture exists to show disappears into the mesh.
      const inRig = (o) => { for (let p = o; p; p = p.parent) if (p === rig.obj) return true; return false; };
      g.plane.traverse((o)=>{ if(!o.isMesh || inRig(o)) return;
        const ms = Array.isArray(o.material)?o.material:[o.material];
        for (const mm of ms) if (mm && !mm.wireframe) { mm.wireframe = true; window.__wire.push(mm); } });
    }
    const hidden = [];
    for (const o of g.scene.children) {
      if (o === g.plane || o === rig.obj || o.isLight || window.__dbg.includes(o)) continue;
      if (o.visible) { hidden.push(o); o.visible = false; }
    }
    window.__restore = () => { for (const o of hidden) o.visible = true;
      for (const mm of window.__wire) mm.wireframe = false;
      for (const o of window.__dbg) o.parent?.remove(o); };
    g.scene.background = new THREE.Color(0x14181f); g.scene.fog = null;
    document.getElementById('hud')?.classList.add('hidden');
    return { id, m };
  }, { id, m });
  for (const [name, dir, zoom] of [['front',[0.45,0.5,-0.85],0.19],['left',[-1,0.3,-0.25],0.20],['over',[0.25,1,0.1],0.19],['far',[0.9,0.35,-0.5],0.75]]) {
    await page.evaluate(({dir,zoom})=>{
      const g = window.__game, THREE = window.__THREE;
      const c = g.plane.userData.cockpit;
      const box = new THREE.Box3().setFromObject(g.plane, true);
      const r = box.getSize(new THREE.Vector3()).length()*0.5;
      const at = g.plane.localToWorld(new THREE.Vector3(c.seat.x, (c.deck ?? 1)-0.35, c.seat.z));
      g.camera.position.copy(at).add(new THREE.Vector3(...dir).normalize().multiplyScalar(r*zoom));
      g.camera.lookAt(at); g.camera.updateMatrixWorld();
      g.renderer.render(g.scene, g.camera);
    },{dir,zoom});
    await page.screenshot({ path: `${DIR}/${id}_${m}_${name}.png` });
  }
  await page.evaluate(()=>{ window.__restore?.(); document.getElementById('hud')?.classList.remove('hidden'); });
  console.log(`${id} / ${m}`);
}
console.log('frames in', DIR);
await browser.close();
