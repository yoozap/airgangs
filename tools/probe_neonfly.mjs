import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

// neonfly is banked in its GLB: try each roll and see which one lands the
// wings flat. carpet is nearly black, so render it with a flat bright
// material to read its silhouette.
const OUT = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/planes';
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(2500);

const data = await page.evaluate(async () => {
  const THREE = window.__THREE; // the app's instance — a second copy breaks Box3/matrix interop
  const mod = await import('/src/planes.js');
  const CELL = 380;
  const r = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  r.setSize(CELL, CELL, false);
  r.setClearColor(0x10131a, 1);
  const scene = new THREE.Scene();
  scene.add(new THREE.HemisphereLight(0xffffff, 0x404040, 3));
  const cam = new THREE.PerspectiveCamera(38, 1, 0.1, 4000);
  const cases = [
    { id: 'neonfly', roll: 0, label: 'neonfly roll=0' },
    { id: 'neonfly', roll: Math.PI / 2, label: 'neonfly roll=+90' },
    { id: 'neonfly', roll: -Math.PI / 2, label: 'neonfly roll=-90' },
    { id: 'carpet', roll: 0, label: 'carpet (bright)' },
  ];
  const sheet = document.createElement('canvas');
  sheet.width = CELL * 2;
  sheet.height = CELL * 2 * 2; // top view + front view per case
  const ctx = sheet.getContext('2d');
  ctx.fillStyle = '#10131a';
  ctx.fillRect(0, 0, sheet.width, sheet.height);
  for (let i = 0; i < cases.length; i++) {
    const cs = cases[i];
    const def = mod.PLANES.find((p) => p.id === cs.id);
    const savedRoll = def.roll;
    def.roll = cs.roll || undefined;
    const model = await mod.loadPlane(cs.id);
    def.roll = savedRoll;
    if (cs.id === 'carpet') {
      model.traverse((o) => {
        if (o.isMesh) o.material = new THREE.MeshLambertMaterial({ color: 0xbfc6d2 });
      });
    }
    const holder = new THREE.Group();
    holder.add(model);
    scene.add(holder);
    const box = new THREE.Box3().setFromObject(model);
    model.position.sub(box.getCenter(new THREE.Vector3()));
    const size = box.getSize(new THREE.Vector3());
    const R = Math.max(size.x, size.y, size.z) * 0.62;
    const d = R / Math.tan((38 * Math.PI) / 360);
    const col = i % 2;
    const row = Math.floor(i / 2);
    // top view
    cam.position.set(0, d, 0.001);
    cam.up.set(0, 0, -1);
    cam.lookAt(0, 0, 0);
    cam.updateProjectionMatrix();
    r.render(scene, cam);
    ctx.drawImage(r.domElement, col * CELL, row * CELL * 2);
    // front view (camera at -Z looking back): wings must be HORIZONTAL
    cam.position.set(0, 0, -d);
    cam.up.set(0, 1, 0);
    cam.lookAt(0, 0, 0);
    cam.updateProjectionMatrix();
    r.render(scene, cam);
    ctx.drawImage(r.domElement, col * CELL, row * CELL * 2 + CELL);
    ctx.fillStyle = '#8ff';
    ctx.font = 'bold 16px monospace';
    ctx.fillText(`${cs.label} — TOP`, col * CELL + 8, row * CELL * 2 + 20);
    ctx.fillText('FRONT (wings must be level)', col * CELL + 8, row * CELL * 2 + CELL + 20);
    scene.remove(holder);
  }
  r.dispose();
  return sheet.toDataURL('image/png');
});
writeFileSync(`${OUT}/neonfly_test.png`, Buffer.from(data.split(',')[1], 'base64'));
console.log(`${OUT}/neonfly_test.png`);
await browser.close();
