// EVERY AEROPLANE FROM THE PORT SIDE, ON A RULER.
//   node tools/_probe_sideruler.mjs [id ...]
//
// findCockpit has to guess the pilot's station on the models that name
// nothing, and the guesses land on cowlings, cargo roofs and fins. An
// orthographic side view with the model's own z printed under it is what
// turns "about a quarter back" into a number to write into the def.
// The current seat is drawn as a red cross; the frame is exactly the
// aircraft's bounding box, so Python can rule it afterwards.
import { chromium } from 'playwright';
import fs from 'node:fs';

const DIR = process.env.SHOT_DIR || '/tmp/sideruler';
fs.mkdirSync(DIR, { recursive: true });
const only = process.argv.slice(2);
const W = 1400; const H = 420;

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
  r.setPixelRatio(1);
  r.setSize(window.innerWidth, window.innerHeight, false);
  r.outputColorSpace = THREE.SRGBColorSpace;
  const sc = new THREE.Scene();
  sc.background = new THREE.Color(0x11161c);
  sc.add(new THREE.HemisphereLight(0xffffff, 0x404050, 2.4));
  const d = new THREE.DirectionalLight(0xffffff, 2.0); d.position.set(-3, 4, 2); sc.add(d);
  const d2 = new THREE.DirectionalLight(0xffffff, 1.2); d2.position.set(3, 2, -3); sc.add(d2);
  window.__rs = { r, sc };
});

const out = [];
const ids = await page.evaluate(async (only) => {
  const { PLANES } = await import('/src/planes.js');
  return (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES).map((p) => p.id);
}, only);

for (const id of ids) {
  const info = await page.evaluate(async (id) => {
    const THREE = window.__T;
    const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
    const { r, sc } = window.__rs;
    if (window.__cur) { sc.remove(window.__cur); }
    const plane = makeGameplayPlane(await loadPlane(id), planeById(id));
    sc.add(plane);
    window.__cur = plane;
    plane.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(plane, true);
    const s = box.getSize(new THREE.Vector3());
    const ctr = box.getCenter(new THREE.Vector3());
    const aspect = window.innerWidth / window.innerHeight;
    // the frame is the box, padded to the viewport's aspect: pixel <-> z is
    // then linear and the ruler below is exact
    let hz = s.z / 2; let hy = s.y / 2;
    if (hz / hy < aspect) hz = hy * aspect; else hy = hz / aspect;
    hz *= 1.02; hy *= 1.02;
    const cam = new THREE.OrthographicCamera(-hz, hz, hy, -hy, 0.1, s.length() * 4);
    cam.position.set(ctr.x + s.length(), ctr.y, ctr.z);
    cam.lookAt(ctr);
    cam.updateMatrixWorld();
    // MARKERS IN THE SCENE, not drawn on afterwards: a red ball on the seat
    // and a GREEN one a quarter of the length behind the -Z end, so which way
    // round the frame is cannot be got wrong by reasoning about handedness.
    const c = plane.userData.cockpit;
    for (const m of (window.__marks ?? [])) sc.remove(m);
    window.__marks = [];
    const ball = (x, y, z, col, rad) => {
      const m = new THREE.Mesh(new THREE.SphereGeometry(rad, 12, 8),
        new THREE.MeshBasicMaterial({ color: col }));
      m.position.set(x, y, z); sc.add(m); window.__marks.push(m); return m;
    };
    const rad = s.length() * 0.012;
    if (c) ball(0, c.seat.y, c.seat.z, 0xff2020, rad);
    ball(0, ctr.y, box.min.z + s.z * 0.02, 0x30ff40, rad);       // GREEN = the -Z end
    ball(0, ctr.y, box.max.z - s.z * 0.02, 0x4090ff, rad);       // BLUE  = the +Z end
    r.render(sc, cam);
    return { id, z0: ctr.z - hz, z1: ctr.z + hz, y0: ctr.y - hy, y1: ctr.y + hy,
      len: +s.z.toFixed(2), span: +(plane.userData.halfSpan * 2).toFixed(2),
      seatZ: c ? +c.seat.z.toFixed(2) : null, seatY: c ? +c.seat.y.toFixed(2) : null,
      deck: c ? +c.deck.toFixed(2) : null, top: c ? +c.top.toFixed(2) : null };
  }, id);
  await page.screenshot({ path: `${DIR}/${id}.png` });
  out.push(info);
  console.log(`${id.padEnd(11)} len ${String(info.len).padStart(6)}  frame z ${info.z0.toFixed(2)} .. ${info.z1.toFixed(2)}  seat z${info.seatZ} y${info.seatY}`);
}
fs.writeFileSync(`${DIR}/frames.json`, JSON.stringify(out, null, 1));
console.log(`\n${out.length} frames in ${DIR}`);
await browser.close();
