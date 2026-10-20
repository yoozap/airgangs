// The cockpit kit, part by part: where each piece sits and where its grip is.
//   node tools/_probe_kit.mjs
import { chromium } from 'playwright';
const browser = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal'] });
const page = await browser.newPage({ viewport:{width:800,height:600} });
page.on('pageerror',(e)=>console.log('[err]',e.message));
page.on('console',(m)=>{ if(m.type()==='error'||m.type()==='warning') console.log('[con]',m.text().slice(0,200)); });
await page.route('**/@vite/client',(r)=>r.fulfill({contentType:'application/javascript',body:'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n'}));
await page.goto('http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character',{timeout:30000});
const r = await page.evaluate(async () => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { loadCockpitKit, CONTROLS, loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
  const kit = await loadCockpitKit();
  const out = { parts: [], controls: null, expect: {} };
  for (const k of Object.keys(CONTROLS)) {
    if (CONTROLS[k].isVector3) out.expect[k] = CONTROLS[k].toArray().map((n)=>+n.toFixed(3));
  }
  kit.updateMatrixWorld(true);
  kit.traverse((o) => {
    if (!o.isMesh && o.type !== 'Object3D' && o.type !== 'Group') return;
    const b = new THREE.Box3().setFromObject(o, true);
    out.parts.push({ name: o.name, type: o.type, isMesh: !!o.isMesh,
      pos: o.position.toArray().map((n)=>+n.toFixed(3)),
      parent: o.parent?.name ?? null,
      min: b.isEmpty()?null:b.min.toArray().map((n)=>+n.toFixed(2)),
      max: b.isEmpty()?null:b.max.toArray().map((n)=>+n.toFixed(2)) });
  });
  // and through a real plane
  const p = makeGameplayPlane(await loadPlane('spitfire'), planeById('spitfire'));
  await new Promise((res)=>setTimeout(res,400));
  p.updateMatrixWorld(true);
  const c = p.userData.controls;
  out.controls = c ? Object.fromEntries(Object.entries(c).map(([k,v])=>[k, v ? {
    pos: v.position.toArray().map((n)=>+n.toFixed(3)),
    gripWorld: v.userData.grip ? v.userData.grip.getWorldPosition(new THREE.Vector3()).toArray().map((n)=>+n.toFixed(3)) : null,
  } : null])) : null;
  out.seat = p.userData.cockpit.seat.toArray().map((n)=>+n.toFixed(3));
  out.kitPos = p.userData.cockpitKit?.position.toArray().map((n)=>+n.toFixed(3)) ?? null;
  return out;
});
console.log('expected offsets from the pan:', JSON.stringify(r.expect));
console.log('\nkit parts:');
for (const p of r.parts) console.log(`  ${(p.name||'(none)').padEnd(22)} ${p.type.padEnd(12)} parent=${String(p.parent).padEnd(16)} pos=${JSON.stringify(p.pos)} box=${JSON.stringify(p.min)}..${JSON.stringify(p.max)}`);
console.log('\nspitfire seat', r.seat, 'kit at', r.kitPos);
console.log('controls:', JSON.stringify(r.controls, null, 1));
await browser.close();
