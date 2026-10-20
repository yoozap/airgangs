// WHAT IS ACTUALLY AT THE FRONT OF THIS MODEL?
//   node tools/_probe_nosegeo.mjs [id ...]
//
// findCockpit measures the hull on a narrow band down the centreline, and when
// that band finds nothing the pilot ends up somewhere surprising. This prints,
// per aeroplane: how far back the centreline geometry actually starts, what is
// out at the -Z end and how wide it is, and how deep the hull is at the cockpit
// station at three band widths. It is what showed that the 767's fuselage sits
// metres off x = 0, and that the hull under the Extra 330's cockpit is 31 cm
// deeper than a single-station reading claimed.
import { chromium } from 'playwright';
const browser = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal'] });
const page = await browser.newPage({ viewport:{width:800,height:600} });
page.on('pageerror',(e)=>console.log('[err]',e.message));
await page.route('**/@vite/client',(r)=>r.fulfill({contentType:'application/javascript',body:'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n'}));
await page.goto('http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character',{timeout:30000});
const r = await page.evaluate(async (only) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
  const out = [];
  for (const id of (only.length ? only : ['spitfire'])) {
    const p = makeGameplayPlane(await loadPlane(id), planeById(id));
    p.updateMatrixWorld(true);
    const air = new THREE.Box3().setFromObject(p, true);
    const size = air.getSize(new THREE.Vector3());
    const band = Math.max(0.3, size.x*0.06);
    const v = new THREE.Vector3();
    // the extreme -Z 6% of the box: what is actually there, and how wide
    const zCut = air.min.z + size.z*0.06;
    let n=0, minx=1e9, maxx=-1e9, onCl=0, names=new Set();
    let clMinZ = 1e9;
    p.traverse((o)=>{
      if(!o.isMesh||!o.geometry?.attributes?.position||o.userData.interior) return;
      const pos=o.geometry.attributes.position;
      const st=Math.max(1,Math.floor(pos.count/40000));
      for(let i=0;i<pos.count;i+=st){
        v.fromBufferAttribute(pos,i).applyMatrix4(o.matrixWorld);
        if(Math.abs(v.x)<=band && v.z<clMinZ) clMinZ=v.z;
        if(v.z>zCut) continue;
        n++; if(v.x<minx)minx=v.x; if(v.x>maxx)maxx=v.x;
        if(Math.abs(v.x)<=band){onCl++; names.add(o.name.slice(0,28));}
      }
    });
    // the hull's top and bottom AT THE COCKPIT STATION, at three band widths
    const c0 = p.userData.cockpit;
    const depths = [];
    if (c0) for (const mul of [1, 2, 4]) {
      let top=-1e9, bot=1e9, n=0;
      p.traverse((o)=>{
        if(!o.isMesh||!o.geometry?.attributes?.position) return;
        if(o.name==='plane_shell') return;
        for(let q=o;q;q=q.parent){ if(q.userData.interior||q.userData.isPropeller) return;
          if(/^cockpit_/.test(q.name||'')) return; }   // the FURNITURE is not the hull
        const pos=o.geometry.attributes.position;
        const st=Math.max(1,Math.floor(pos.count/40000));
        for(let i=0;i<pos.count;i+=st){
          v.fromBufferAttribute(pos,i).applyMatrix4(o.matrixWorld);
          if(Math.abs(v.z-c0.seat.z)>size.z*0.03) continue;
          if(Math.abs(v.x-c0.seat.x)>band*mul) continue;
          n++; if(v.y>top)top=v.y; if(v.y<bot)bot=v.y;
        }
      });
      depths.push({band:+(band*mul).toFixed(2), n, top:+top.toFixed(2), bot:+bot.toFixed(2)});
    }
    // what is actually out at the -Z end, by mesh
    const per = new Map();
    p.traverse((o)=>{
      if(!o.isMesh||!o.geometry?.attributes?.position) return;
      const bb=new THREE.Box3().setFromObject(o,true);
      per.set(o.name||'(unnamed)', {z:[+bb.min.z.toFixed(1),+bb.max.z.toFixed(1)],
        x:[+bb.min.x.toFixed(1),+bb.max.x.toFixed(1)], y:[+bb.min.y.toFixed(1),+bb.max.y.toFixed(1)],
        n:o.geometry.attributes.position.count, interior:!!o.userData.interior});
    });
    const atEnd=[...per.entries()].filter(([,b])=>b.z[0]<air.min.z+size.z*0.08).slice(0,12);
    const c = p.userData.cockpit;
    out.push({id, depths, cockpitBot:c0?+c0.bot.toFixed(2):null, ceil:c0&&Number.isFinite(c0.ceil)?+c0.ceil.toFixed(2):null, atEnd, zmin:+air.min.z.toFixed(2), len:+size.z.toFixed(2), band:+band.toFixed(2),
      noseVerts:n, noseX:[+minx.toFixed(2),+maxx.toFixed(2)], onCentreline:onCl,
      centrelineStartsAt:+clMinZ.toFixed(2), who:[...names].slice(0,3),
      seatZ:c?+c.seat.z.toFixed(2):null, from:c?.from});
  }
  return out;
}, process.argv.slice(2));
for (const o of r) console.log(JSON.stringify(o));
await browser.close();
