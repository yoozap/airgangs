// WHICH WAY ROUND IS THIS AEROPLANE? Six orthographic views of one machine in
// the game's own frame, each labelled with the direction it is taken from, and
// a green ball on the -Z end -- which is the end the game flies as the NOSE.
// An aeroplane whose nose is not on the green ball needs a `yaw` (or `roll`)
// in its def.
//   node tools/_probe_orient.mjs neonfly
import { chromium } from 'playwright';
import fs from 'node:fs';

const DIR = process.env.SHOT_DIR || '/tmp/orient';
fs.mkdirSync(DIR, { recursive: true });
const only = process.argv.slice(2);
const W = 900; const H = 640;

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
  sc.background = new THREE.Color(0x0f141b);
  sc.add(new THREE.HemisphereLight(0xffffff, 0x5a6070, 2.6));
  const d = new THREE.DirectionalLight(0xffffff, 2.2); d.position.set(-3, 4, 2); sc.add(d);
  const d2 = new THREE.DirectionalLight(0xffffff, 1.3); d2.position.set(3, 2, -3); sc.add(d2);
  window.__rs = { r, sc };
});

// [name, camera direction, what you should be looking at]
const VIEWS = process.env.SHEET
  ? [['side', [-1, 0.10, 0]]]
  : [
    ['1-from-the-left',  [-1, 0, 0],    'port side: the NOSE should be on the LEFT of this frame'],
    ['2-from-the-right', [1, 0, 0],     'starboard: the NOSE should be on the RIGHT'],
    ['3-from-above',     [0, 1, 0.001], 'plan: the NOSE should be at the TOP'],
    ['4-from-the-front', [0, 0, -1],    'head-on: you are looking at the NOSE'],
    ['5-from-behind',    [0, 0, 1],     'tail-on: you are looking up the jet pipe'],
    ['6-three-quarter',  [-0.8, 0.45, -0.7], 'front three-quarter'],
  ];
const ids = await (async () => {
  const page2 = page;
  return page2.evaluate(async (only) => {
    const { PLANES } = await import('/src/planes.js');
    return (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES).map((p) => p.id);
  }, only);
})();
for (const id of ids) {
  const info = await page.evaluate(async (id) => {
    const THREE = window.__T;
    const { loadPlane, makeGameplayPlane, planeById } = await import('/src/planes.js');
    const { sc } = window.__rs;
    if (window.__cur) sc.remove(window.__cur);
    const def = planeById(id);
    const plane = makeGameplayPlane(await loadPlane(id), def);
    sc.add(plane); window.__cur = plane;
    plane.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(plane, true);
    window.__box = box;
    for (const m of (window.__marks ?? [])) sc.remove(m);
    window.__marks = [];
    // GROUND. An aeroplane the right way up stands its wheels on it; one on
    // its back floats over it with its undercarriage in the air.
    if (!window.__ground) {
      const g = new THREE.Mesh(new THREE.PlaneGeometry(600, 600),
        new THREE.MeshBasicMaterial({ color: 0x263040 }));
      g.rotation.x = -Math.PI / 2;
      const grid = new THREE.GridHelper(600, 60, 0x4a5a70, 0x35414f);
      sc.add(g); sc.add(grid); window.__ground = g; window.__grid = grid;
    }
    window.__ground.position.y = -1.7;   // game.js WHEEL_HEIGHT
    window.__grid.position.y = -1.69;
    const s = box.getSize(new THREE.Vector3());
    const ball = (p, col) => { const m = new THREE.Mesh(
      new THREE.SphereGeometry(s.length() * 0.018, 12, 8),
      new THREE.MeshBasicMaterial({ color: col }));
      m.position.copy(p); sc.add(m); window.__marks.push(m); };
    const c = box.getCenter(new THREE.Vector3());
    ball(new THREE.Vector3(c.x, c.y, box.min.z - s.z * 0.04), 0x30ff40);  // -Z: the NOSE
    ball(new THREE.Vector3(c.x, box.max.y + s.y * 0.06, c.z), 0x4090ff);  // +Y: UP
    return { id, yaw: +(def.yaw ?? 0).toFixed(3), roll: +(def.roll ?? 0).toFixed(3),
      size: [+s.x.toFixed(2), +s.y.toFixed(2), +s.z.toFixed(2)] };
  }, id);
  console.log(`${id}: yaw ${info.yaw}, roll ${info.roll}, box ${info.size.join(' x ')} m (x across, y up, z along)`);
  for (const [name, dir] of VIEWS) {
    await page.evaluate(({ dir }) => {
      const THREE = window.__T; const { r, sc } = window.__rs;
      const box = window.__box;
      const s = box.getSize(new THREE.Vector3());
      const c = box.getCenter(new THREE.Vector3());
      const aspect = window.innerWidth / window.innerHeight;
      const rad = s.length() * 0.52;
      const cam = new THREE.OrthographicCamera(-rad * aspect, rad * aspect, rad, -rad, 0.1, s.length() * 8);
      cam.up.set(0, 1, 0);
      if (Math.abs(dir[1]) > 0.9) cam.up.set(0, 0, -1);  // plan view: nose toward the bottom
      cam.position.copy(c).add(new THREE.Vector3(...dir).normalize().multiplyScalar(s.length() * 2));
      cam.lookAt(c); cam.updateMatrixWorld();
      r.render(sc, cam);
    }, { dir });
    await page.screenshot({ path: `${DIR}/${id}_${name}.png` });
  }
}
console.log('frames in', DIR);
await browser.close();
