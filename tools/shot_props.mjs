// Contact sheet: every propeller aircraft, seen from the front quarter with
// its propellers TURNED 40 degrees off rest. Anything that was cut out by
// mistake -- a slice of nacelle, half a cowling -- swings out of the airframe
// and is impossible to miss.
//   node tools/shot_props.mjs [id ...]
import { chromium } from 'playwright';
import fs from 'fs';

const only = process.argv.slice(2);
const OUT = process.env.OUT || '/tmp/props_sheet';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 520, height: 420 } });
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.addInitScript((spin) => { window.__wantSpin = spin; }, !!process.env.SPIN);
await page.goto('http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 60000 });

const ids = await page.evaluate(async (only) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const { spinPropellers } = await import('/src/propeller.js');
  const host = document.createElement('div');
  host.style.cssText = 'position:fixed;inset:0;z-index:99999;background:#101216';
  document.body.appendChild(host);
  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setSize(520, 420);
  host.appendChild(renderer.domElement);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x101216);
  scene.add(new THREE.HemisphereLight(0xcfe3ff, 0x303038, 2.0));
  const key = new THREE.DirectionalLight(0xffffff, 2.2);
  key.position.set(3, 5, 6);
  scene.add(key);
  const cam = new THREE.PerspectiveCamera(32, 520 / 420, 0.1, 2000);
  window.__shot = { renderer, scene, cam, THREE, loadPlane, makeGameplayPlane, spinPropellers, host };
  window.__spin = !!window.__wantSpin;
  return PLANES.filter((p) => p.prop && (!only.length || only.includes(p.id))).map((p) => p.id);
}, only);

for (const id of ids) {
  await page.evaluate(async (id) => {
    const s = window.__shot;
    const { THREE } = s;
    if (s.current) s.scene.remove(s.current);
    const { PLANES } = await import('/src/planes.js');
    const def = PLANES.find((p) => p.id === id);
    const plane = s.makeGameplayPlane(await s.loadPlane(id), def);
    s.scene.add(plane);
    s.current = plane;
    // SPIN=1 spins them at cruise revs instead, so the blur disc shows and
    // its size can be checked against the blades it replaces.
    if (window.__spin) s.spinPropellers(plane, 2200, 0.004);
    else s.spinPropellers(plane, 90, 40 * Math.PI / 180 / ((90 / 60) * Math.PI * 2));
    const box = new THREE.Box3().setFromObject(plane, true);
    const c = box.getCenter(new THREE.Vector3());
    const d = box.getSize(new THREE.Vector3()).length();
    // front quarter, slightly above: the view a propeller shows itself in
    s.cam.position.set(c.x + d * 0.42, c.y + d * 0.24, c.z - d * 0.72);
    s.cam.lookAt(c);
    s.renderer.render(s.scene, s.cam);
  }, id);
  await page.waitForTimeout(120);
  // The game's own canvas is first in the DOM and invisible; grab the
  // renderer's own output as a data URL instead of hunting for an element.
  const data = await page.evaluate(() => window.__shot.renderer.domElement.toDataURL('image/png'));
  fs.writeFileSync(`${OUT}/${id}.png`, Buffer.from(data.split(',')[1], 'base64'));
  console.log('shot', id);
}
await browser.close();
