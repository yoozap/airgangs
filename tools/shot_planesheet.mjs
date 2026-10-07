import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

// Contact sheet of every plane seen from straight above, screen-up = -Z.
// A correctly authored model points its NOSE UP in its cell; anything
// pointing down, left or right is rotated and needs a yaw fix.
const OUT = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/planes';
const URL = process.env.URL || 'http://localhost:5174';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(2500);

const sheets = await page.evaluate(async () => {
  const THREE = window.__THREE; // the app's instance — a second copy breaks Box3/matrix interop
  const mod = await import('/src/planes.js');
  const CELL = 250;
  const COLS = 5;
  const PER_SHEET = 20;
  const r = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  r.setSize(CELL, CELL, false);
  r.setClearColor(0x10131a, 1);
  const scene = new THREE.Scene();
  scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x30323a, 2.4));
  const sun = new THREE.DirectionalLight(0xffffff, 2.2);
  sun.position.set(1, 2, 1);
  scene.add(sun);
  const cam = new THREE.PerspectiveCamera(38, 1, 0.1, 4000);

  const out = [];
  const ids = mod.PLANES.map((p) => p.id);
  for (let s = 0; s * PER_SHEET < ids.length; s++) {
    const batch = ids.slice(s * PER_SHEET, (s + 1) * PER_SHEET);
    const rows = Math.ceil(batch.length / COLS);
    const sheet = document.createElement('canvas');
    sheet.width = COLS * CELL;
    sheet.height = rows * CELL;
    const ctx = sheet.getContext('2d');
    ctx.fillStyle = '#10131a';
    ctx.fillRect(0, 0, sheet.width, sheet.height);
    for (let i = 0; i < batch.length; i++) {
      const model = await mod.loadPlane(batch[i]);
      const holder = new THREE.Group();
      holder.add(model);
      scene.add(holder);
      const box = new THREE.Box3().setFromObject(model);
      const c = box.getCenter(new THREE.Vector3());
      const size = box.getSize(new THREE.Vector3());
      model.position.sub(c);
      const R = Math.max(size.x, size.z) * 0.72;
      // straight down, screen-up = -Z, so a correct nose points UP
      cam.position.set(0, R / Math.tan((38 * Math.PI) / 360) + size.y, 0.001);
      cam.up.set(0, 0, -1);
      cam.lookAt(0, 0, 0);
      cam.updateProjectionMatrix();
      r.render(scene, cam);
      const x = (i % COLS) * CELL;
      const y = Math.floor(i / COLS) * CELL;
      ctx.drawImage(r.domElement, x, y);
      ctx.strokeStyle = '#2a3550';
      ctx.strokeRect(x + 0.5, y + 0.5, CELL - 1, CELL - 1);
      ctx.fillStyle = '#8ff';
      ctx.font = 'bold 17px monospace';
      ctx.fillText(batch[i], x + 8, y + 22);
      // an arrow showing where the nose SHOULD point
      ctx.strokeStyle = '#2f6';
      ctx.beginPath();
      ctx.moveTo(x + CELL - 20, y + CELL - 12);
      ctx.lineTo(x + CELL - 20, y + CELL - 40);
      ctx.moveTo(x + CELL - 26, y + CELL - 32);
      ctx.lineTo(x + CELL - 20, y + CELL - 40);
      ctx.lineTo(x + CELL - 14, y + CELL - 32);
      ctx.stroke();
      scene.remove(holder);
    }
    out.push(sheet.toDataURL('image/png'));
  }
  r.dispose();
  return out;
});

sheets.forEach((d, i) => {
  writeFileSync(`${OUT}/sheet${i}.png`, Buffer.from(d.split(',')[1], 'base64'));
  console.log(`${OUT}/sheet${i}.png`);
});
await browser.close();
