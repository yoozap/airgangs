// Every personage must stand the same, human height, and the hand weapons must
// be real-world size. Prints the measured world-space size of each.
//   Run:  GAME_URL=http://localhost:5173/ node tools/check_sizes.mjs
import { chromium } from 'playwright';
const URL = process.env.GAME_URL || 'http://localhost:5173/';
const b = await chromium.launch({ headless: true });
const page = await b.newPage();
await page.goto(URL);
await page.waitForTimeout(1500);

const out = await page.evaluate(async () => {
  const chars = await import('/src/characters.js');
  const { makeCharacter, PERSONAGES, HERO_HEIGHT } = chars;

  // Measure without importing three: walk the meshes, push each vertex through
  // its own world matrix using the Vector3 that the model itself provides.
  const measure = (root) => {
    root.updateWorldMatrix(true, true);
    const V = root.position.constructor;
    const v = new V();
    let lo = [Infinity, Infinity, Infinity];
    let hi = [-Infinity, -Infinity, -Infinity];
    // Must match characters.js measureBox(): for a SkinnedMesh the raw position
    // attribute is the BIND pose and matrixWorld carries no bone deformation,
    // so reading vertices directly measures the wrong body entirely.
    // ALL EIGHT corners. Transforming only min and max is correct for a pure
    // translation/scale but wrong the moment a sub-object is rotated (a weapon
    // parented to a hand bone, say) -- that is what made the astronaut read
    // 1.969 m when the game had it at exactly 1.80.
    const eat = (bb, m) => {
      const corners = [];
      for (const x of [bb.min.x, bb.max.x]) {
        for (const y of [bb.min.y, bb.max.y]) {
          for (const z of [bb.min.z, bb.max.z]) corners.push([x, y, z]);
        }
      }
      for (const c of corners) {
        v.set(c[0], c[1], c[2]).applyMatrix4(m);
        const q = [v.x, v.y, v.z];
        for (let k = 0; k < 3; k++) {
          if (q[k] < lo[k]) lo[k] = q[k];
          if (q[k] > hi[k]) hi[k] = q[k];
        }
      }
    };
    root.traverse((o) => {
      if (o.isSkinnedMesh) {
        o.skeleton.update();
        o.computeBoundingBox();
        eat(o.boundingBox, o.matrixWorld);
      } else if (o.isMesh && o.geometry) {
        o.geometry.computeBoundingBox();
        eat(o.geometry.boundingBox, o.matrixWorld);
      }
    });
    return [hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]];
  };

  const rows = [];
  for (const p of PERSONAGES) {
    const rig = await makeCharacter(p.id);
    const s = measure(rig.obj);
    rows.push({ id: p.id, h: +s[1].toFixed(3), w: +s[0].toFixed(2), d: +s[2].toFixed(2) });
  }

  const guns = [];
  const probe = await makeCharacter(PERSONAGES[0].id); // a rig to borrow a loader from
  const { GLTFLoader } = await import('/node_modules/.vite/deps/three_addons_loaders_GLTFLoader__js.js');
  for (const id of ['makarov', 'beretta']) {
    const g = await new GLTFLoader().loadAsync(`/models/${id}.glb`);
    const s = measure(g.scene);
    guns.push({ id, mm: s.map((x) => Math.round(x * 1000)) });
  }
  return { target: HERO_HEIGHT, rows, guns };
});

console.log(`target hero height: ${out.target} m\n`);
let bad = 0;
for (const r of out.rows) {
  const off = r.h - out.target;
  if (Math.abs(off) > 0.02) bad++;
  console.log(`${Math.abs(off) > 0.02 ? 'OFF ' : 'ok  '} ${r.id.padEnd(14)} ${r.h.toFixed(3)} m`
    + ` (${off >= 0 ? '+' : ''}${off.toFixed(3)})   ${r.w} x ${r.d} m footprint`);
}
console.log(`\n${out.rows.length - bad}/${out.rows.length} personages at ${out.target} m\n`);
for (const g of out.guns) {
  // X must be the barrel (longest), Y the height (slide up), Z the width.
  // Get Y and Z the wrong way round and the pistol lies on its side in the
  // hand -- the hero aims the flat of the slide at whatever it shoots.
  const [x, y, z] = g.mm;
  const ok = x > y && y > z;
  console.log(`${ok ? 'ok  ' : 'OFF '} gun ${g.id.padEnd(10)} barrel ${x} mm, height ${y} mm, width ${z} mm`);
}
await b.close();
