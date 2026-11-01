// DID ANYTHING BUT THE CRANE MOVE? The Mustang, the three fans, the board and
// the cables are all placed off the crane's hook, so swapping the machine
// moves them unless the new hook lands where the old one did. The harbour
// crane's numbers are written in here to compare against: everything but the
// crane itself must come out the same.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('console', (m) => { if (/warehouse: (crane|p-51|P-51|\d+ fan)/i.test(m.text())) console.log(' [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = 0; window.__game?.scene?.traverse((o) => { if (/^warehouse_(crane|fan_3)$/.test(o.name)) ok++; });
  return ok >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(3500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const out = [];
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const v = new T.Vector3();
  const localBox = (root) => {
    const b = new T.Box3();
    root.traverse((o) => {
      if (!o.isMesh || !o.geometry) return;
      o.geometry.computeBoundingBox();
      const bb = o.geometry.boundingBox;
      const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        b.expandByPoint(v.applyMatrix4(m));
      }
    });
    return b;
  };
  // what the harbour crane left behind, in shed-local metres
  const WAS = {
    'plane p51': [-5.4, 5.3, 25.7, 37.0, 3.1, 6.8],
    'crane_slings': [-1.5, 1.3, 29.4, 33.3, 6.2, 7.9],
  };
  const row = (name, b, was) => {
    const got = [b.min.x, b.max.x, b.min.z, b.max.z, b.min.y, b.max.y];
    const off = was ? Math.max(...got.map((n, i) => Math.abs(n - was[i]))) : null;
    out.push(`  ${name.padEnd(18)} x${b.min.x.toFixed(1).padStart(6)}..${b.max.x.toFixed(1).padStart(6)}`
      + `  z${b.min.z.toFixed(1).padStart(6)}..${b.max.z.toFixed(1).padStart(6)}`
      + `  y${b.min.y.toFixed(1).padStart(6)}..${b.max.y.toFixed(1).padStart(6)}`
      + (was ? `   moved ${off.toFixed(2)} m from the harbour crane's scene${off < 0.6 ? '' : '  <-- MOVED'}` : ''));
  };
  out.push('-- the shed, in its own metres --');
  for (const c of wh.children) {
    if (!/crane|fan|board|cable|socket|p51|plane/i.test(c.name || '')) continue;
    const b = localBox(c);
    if (b.isEmpty()) continue;
    row(c.name || c.type, b, null);
  }
  // the pieces that hang off the crane group
  let crane = null; g.scene.traverse((o) => { if (!crane && o.name === 'warehouse_crane') crane = o; });
  // WHAT HANGS OFF THE CRANE, found by what it IS rather than by being a child
  // of the group: the aeroplane and its strops now live inside a `crane_swing`
  // pivot at the hook (the wind-tunnel effect swings them as a pendulum), so
  // walking the group's own children finds the pivot and not the machine.
  out.push('-- what hangs off the crane --');
  const hung = [];
  crane.traverse((o) => {
    if (o === crane) return;
    if (o.userData?.planeId) hung.push([`plane ${o.userData.planeId}`, o]);
    else if (/slings|shell|flow|swing/.test(o.name || '')) hung.push([o.name, o]);
  });
  for (const [nm, o] of hung) {
    const b = localBox(o);
    if (b.isEmpty()) continue;
    row(nm, b, WAS[nm]);
  }
  // roof clearance over the machine
  const box = localBox(crane);
  out.push(`-- crane ${(box.max.x - box.min.x).toFixed(1)} x ${(box.max.z - box.min.z).toFixed(1)} x `
    + `${(box.max.y - box.min.y).toFixed(1)} m, top at ${box.max.y.toFixed(2)} m --`);
  const d = wh.userData.shedShell;
  for (const x of [-4.3, 0, 4.3]) {
    const r = new T.Raycaster();
    const p = new T.Vector3(x, 0.5, box.max.z - 4).applyMatrix4(wh.matrixWorld);
    r.set(p, new T.Vector3(0, 1, 0)); r.far = 30;
    const roof = r.intersectObject(wh, true).filter((h) => h.point.y - p.y > 3);
    out.push(`   roof at shed-x ${x}: ${roof.length ? (roof[0].point.y - p.y + 0.5).toFixed(2) + ' m' : 'open'}`);
  }
  return out.join('\n');
}));
await browser.close();
