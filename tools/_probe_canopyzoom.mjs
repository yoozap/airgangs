// THE FORWARD FUSELAGE, CLOSE UP, ON A ONE-METRE RULER.
//   node tools/_probe_canopyzoom.mjs [id ...]
//
// The companion to _probe_sideruler: that one frames the whole aeroplane and
// is good enough to catch a pilot sitting on a fin, this one frames the front
// two thirds of the fuselage so the canopy's own station can be READ OFF and
// written into a def as `cockpit: { z }`. Ticks are every metre of the model's
// own z, and the green ball still marks the -Z end (the nose the game flies).
import { chromium } from 'playwright';
import fs from 'node:fs';

const DIR = process.env.SHOT_DIR || '/tmp/canopyzoom';
fs.mkdirSync(DIR, { recursive: true });
const only = process.argv.slice(2);
const W = 1600; const H = 520;

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: W, height: H } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });
await page.evaluate(async () => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  window.__T = THREE;
  const cv = document.createElement('canvas');
  cv.style.cssText = 'position:fixed;inset:0;width:100vw;height:100vh;z-index:99999';
  document.body.appendChild(cv);
  const r = new THREE.WebGLRenderer({ canvas: cv, antialias: true });
  r.setPixelRatio(1); r.setSize(window.innerWidth, window.innerHeight, false);
  r.outputColorSpace = THREE.SRGBColorSpace;
  const sc = new THREE.Scene();
  sc.background = new THREE.Color(0x0d1117);
  sc.add(new THREE.HemisphereLight(0xffffff, 0x50566a, 2.6));
  const d = new THREE.DirectionalLight(0xffffff, 2.2); d.position.set(-3, 4, 2); sc.add(d);
  const d2 = new THREE.DirectionalLight(0xffffff, 1.3); d2.position.set(3, 2, -3); sc.add(d2);
  window.__rs = { r, sc };
});
const ids = await page.evaluate(async (only) => {
  const { PLANES } = await import('/src/planes.js');
  return (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES).map((p) => p.id);
}, only);

const out = [];
for (const id of ids) {
  const info = await page.evaluate(async (id) => {
    const THREE = window.__T;
    const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
    const { r, sc } = window.__rs;
    if (window.__cur) sc.remove(window.__cur);
    const plane = makeGameplayPlane(await loadPlane(id), planeById(id));
    sc.add(plane); window.__cur = plane;
    plane.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(plane, true);
    const s = box.getSize(new THREE.Vector3());
    const c = plane.userData.cockpit;
    // the forward 62% of the length, and only as much height as a fuselage has
    const zA = box.min.z; const zB = box.min.z + s.z * 0.62;
    const cz = (zA + zB) / 2; const hz = (zB - zA) / 2 * 1.02;
    const aspect = window.innerWidth / window.innerHeight;
    const hy = hz / aspect;
    const cy = (c ? c.deck : box.min.y + s.y * 0.4);
    const cam = new THREE.OrthographicCamera(-hz, hz, hy, -hy, 0.1, s.length() * 6);
    cam.position.set(box.max.x + s.length(), cy, cz);
    cam.lookAt(box.max.x, cy, cz);
    cam.updateMatrixWorld();
    for (const m of (window.__marks ?? [])) sc.remove(m);
    window.__marks = [];
    const ball = (y, z, col) => { const m = new THREE.Mesh(new THREE.SphereGeometry(hz * 0.018, 12, 8),
      new THREE.MeshBasicMaterial({ color: col })); m.position.set(0, y, z); sc.add(m); window.__marks.push(m); return m; };
    if (c) ball(c.seat.y, c.seat.z, 0xff2020);
    ball(cy, zA + s.z * 0.012, 0x30ff40);
    r.render(sc, cam);
    return { id, z0: cz - hz, z1: cz + hz, y0: cy - hy, y1: cy + hy, len: +s.z.toFixed(2),
      seatZ: c ? +c.seat.z.toFixed(2) : null, seatY: c ? +c.seat.y.toFixed(2) : null,
      deck: c ? +c.deck.toFixed(2) : null };
  }, id);
  await page.screenshot({ path: `${DIR}/${id}.png` });
  out.push(info);
  console.log(`${id.padEnd(11)} frame z ${info.z0.toFixed(1)} .. ${info.z1.toFixed(1)}   seat z${info.seatZ}`);
}
fs.writeFileSync(`${DIR}/frames.json`, JSON.stringify(out, null, 1));
await browser.close();
