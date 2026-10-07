import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

// Two views per plane so orientation is unambiguous:
//   TOP  (screen-up = -Z): a correct nose points UP
//   SIDE (camera at +X)   : a correct nose points RIGHT
const OUT = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/planes';
const URL = process.env.URL || 'http://localhost:5174';
const IDS = process.env.IDS ? process.env.IDS.split(',') : null; // null = every plane
const TAG = process.env.TAG || 'zoom';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(2500);

await page.evaluate((c) => { window.__cell = c; }, Number(process.env.CELL || 300));
const sheets = await page.evaluate(async (idsIn) => {
  const THREE = window.__THREE; // the app's instance — a second copy breaks Box3/matrix interop
  const mod = await import('/src/planes.js');
  const ids = idsIn ?? mod.PLANES.map((p) => p.id);
  const CELL = Number(window.__cell || 300);
  const PER_SHEET = 6; // rows per sheet (2 cells each)
  const r = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  r.setSize(CELL, CELL, false);
  r.setClearColor(0x10131a, 1);
  const scene = new THREE.Scene();
  scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x30323a, 2.6));
  const sun = new THREE.DirectionalLight(0xffffff, 2.4);
  sun.position.set(1, 2, 1);
  scene.add(sun);
  const cam = new THREE.PerspectiveCamera(38, 1, 0.1, 4000);
  const out = [];
  for (let s = 0; s * PER_SHEET < ids.length; s++) {
    const batch = ids.slice(s * PER_SHEET, (s + 1) * PER_SHEET);
    const sheet = document.createElement('canvas');
    sheet.width = CELL * 2 + 120;
    sheet.height = CELL * batch.length;
    const ctx = sheet.getContext('2d');
    ctx.fillStyle = '#10131a';
    ctx.fillRect(0, 0, sheet.width, sheet.height);
    for (let i = 0; i < batch.length; i++) {
      const model = await mod.loadPlane(batch[i]);
      const holder = new THREE.Group();
      holder.add(model);
      scene.add(holder);
      const box = new THREE.Box3().setFromObject(model);
      model.position.sub(box.getCenter(new THREE.Vector3()));
      const size = box.getSize(new THREE.Vector3());
      const R = Math.max(size.x, size.y, size.z) * 0.62;
      const d = R / Math.tan((38 * Math.PI) / 360);
      // reference marker: a red dart sitting where the NOSE should point
      const mk = new THREE.Mesh(
        new THREE.ConeGeometry(R * 0.07, R * 0.22, 12),
        new THREE.MeshBasicMaterial({ color: 0xff2a4a })
      );
      mk.rotation.x = -Math.PI / 2; // cone tip toward -Z
      mk.position.set(0, 0, -R * 1.12);
      holder.add(mk);
      const y = i * CELL;
      // top view: nose must point UP
      cam.position.set(0, d, 0.001);
      cam.up.set(0, 0, -1);
      cam.lookAt(0, 0, 0);
      cam.updateProjectionMatrix();
      r.render(scene, cam);
      ctx.drawImage(r.domElement, 120, y);
      // side view from +X: nose must point RIGHT
      cam.position.set(d, 0, 0);
      cam.up.set(0, 1, 0);
      cam.lookAt(0, 0, 0);
      cam.updateProjectionMatrix();
      r.render(scene, cam);
      ctx.drawImage(r.domElement, 120 + CELL, y);
      ctx.fillStyle = '#8ff';
      ctx.font = 'bold 18px monospace';
      ctx.fillText(batch[i], 8, y + 30);
      ctx.fillStyle = '#597';
      ctx.font = '13px monospace';
      ctx.fillText('top:', 8, y + 60);
      ctx.fillText('nose UP', 8, y + 78);
      ctx.fillText('side:', 8, y + 110);
      ctx.fillText('nose RIGHT', 8, y + 128);
      ctx.strokeStyle = '#2a3550';
      ctx.strokeRect(120.5, y + 0.5, CELL - 1, CELL - 1);
      ctx.strokeRect(120.5 + CELL, y + 0.5, CELL - 1, CELL - 1);
      scene.remove(holder);
    }
    out.push(sheet.toDataURL('image/png'));
  }
  r.dispose();
  return out;
}, IDS);

sheets.forEach((d, i) => {
  writeFileSync(`${OUT}/${TAG}${i}.png`, Buffer.from(d.split(',')[1], 'base64'));
  console.log(`${OUT}/${TAG}${i}.png`);
});
await browser.close();
