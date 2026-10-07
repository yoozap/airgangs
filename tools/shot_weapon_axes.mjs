// Renders every weapon on its own against a flat background with +Y marked, so
// the authored orientation is unmistakable: barrel must point RIGHT (+X), grip
// must hang DOWN. Catches a pistol that would be held sideways or upside down.
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_weapon_axes.mjs
import { chromium } from 'playwright';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const b = await chromium.launch({ headless: true,
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 900, height: 380 } });
await p.goto(process.env.GAME_URL || 'http://localhost:5173/');
await p.waitForTimeout(1200);
await p.evaluate(async () => {
  const THREE = await import('/node_modules/.vite/deps/three.js');
  const { GLTFLoader } = await import('/node_modules/.vite/deps/three_addons_loaders_GLTFLoader__js.js');
  document.body.innerHTML = '';
  const r = new THREE.WebGLRenderer({ antialias: true });
  r.setSize(900, 380); document.body.appendChild(r.domElement);
  const sc = new THREE.Scene(); sc.background = new THREE.Color(0xdddddd);
  sc.add(new THREE.AmbientLight(0xffffff, 2.2));
  const d = new THREE.DirectionalLight(0xffffff, 2.0); d.position.set(1, 2, 3); sc.add(d);
  const cam = new THREE.PerspectiveCamera(32, 900 / 380, 0.01, 10);
  cam.position.set(0.1, 0, 1.25); cam.up.set(0, 1, 0); cam.lookAt(0.1, 0.02, 0);
  const L = new GLTFLoader();
  const at = [-0.18, 0.22];
  const ids = ['makarov', 'beretta'];
  for (let i = 0; i < ids.length; i++) {
    const g = await L.loadAsync(`/models/${ids[i]}.glb?v=${Date.now()}`);
    g.scene.position.set(at[i], 0, 0); sc.add(g.scene);
    const m = new THREE.Mesh(new THREE.SphereGeometry(0.007),
      new THREE.MeshBasicMaterial({ color: 0x00aa00 }));
    m.position.set(at[i], 0.14, 0); sc.add(m);   // +Y marker: this is UP
  }
  r.render(sc, cam);
});
await p.waitForTimeout(500);
await p.screenshot({ path: DIR + '/both_guns_axes.png' });
await b.close();
