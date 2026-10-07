// Real shops: every OSM business POI (pois2.json — coords + REAL names) is
// snapped to the nearest building wall and gets a ground-floor shopfront:
// the kit's black-alu glass module, an awning for food places, a fuel canopy
// for gas stations — and a SignPool that paints the shop's ACTUAL name
// (Georgian) on the fascia whenever the camera is near. Far away the fascia
// reads as a colored strip; close up it says "ნიკორა" / "აფთიაქი PSP" / the
// real cafe name. One pooled canvas per visible sign — the whole city's
// 13k shops cost ~200 small textures at any moment.
import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { BUILDERS } from './landmarks.js';

// category -> fascia color + fallback label (used when the POI has no name)
const CAT_STYLE = {
  supermarket: ['#1c7a3c', 'სუპერმარკეტი'],
  minimarket: ['#2a8a4a', 'მარკეტი'],
  bakery: ['#b5722a', 'თონე • პური'],
  butcher: ['#a03028', 'ხორცი'],
  grocer: ['#4a8a2a', 'ბოსტნეული'],
  clothes: ['#6a4a8a', 'ტანსაცმელი'],
  beauty: ['#b04a7a', 'სილამაზის სალონი'],
  electronics: ['#2a5a9a', 'ტექნიკა'],
  hardware: ['#8a6a2a', 'სამშენებლო'],
  furniture: ['#7a5a3a', 'ავეჯი'],
  florist: ['#3a9a5a', 'ყვავილები'],
  books: ['#5a4a9a', 'წიგნები'],
  jewelry: ['#9a7a1a', 'საიუველირო'],
  alcohol: ['#7a2a3a', 'ღვინო • სასმელი'],
  auto: ['#3a4a5a', 'ავტონაწილები'],
  kiosk: ['#5a6a4a', 'ჯიხური'],
  pet: ['#4a8a7a', 'ზოომაღაზია'],
  toys: ['#c05a8a', 'სათამაშოები'],
  sports: ['#2a7a9a', 'სპორტი'],
  optician: ['#4a6a8a', 'ოპტიკა'],
  gift: ['#a05a2a', 'საჩუქრები'],
  shop: ['#c08a30', 'მაღაზია'],
  cafe: ['#8a5a34', 'კაფე'],
  restaurant: ['#b43a2a', 'რესტორანი'],
  fastfood: ['#c4442a', 'სწრაფი კვება'],
  bar: ['#5a3a6a', 'ბარი'],
  bank: ['#1f5fa8', 'ბანკი'],
  pharmacy: ['#2a9d5c', 'აფთიაქი'],
  post: ['#c42a2a', 'ფოსტა'],
  vet: ['#3a8a6a', 'ვეტკლინიკა'],
  cinema: ['#6a2a5a', 'კინო'],
  hotel: ['#3a4d80', 'სასტუმრო'],
  mcdonalds: ['#c8102e', 'მაკდონალდსი'],
  fuel: ['#d23c2a', 'ბენზინგასამართი'],
  market: ['#8a6a1a', 'ბაზარი'],
  // civic: institutional plates, not shop fascias (styled in SignPool.draw)
  school: ['#f4f1e8', 'საჯარო სკოლა'],
  kindergarten: ['#f4e8ec', 'საბავშვო ბაღი'],
  university: ['#f4f1e8', 'უნივერსიტეტი'],
  hospital: ['#ffffff', 'საავადმყოფო'],
  clinic: ['#ffffff', 'კლინიკა'],
  police: ['#1a3a6e', 'პოლიცია'],
  fire_station: ['#a02a1a', 'სახანძრო'],
  library: ['#f4f1e8', 'ბიბლიოთეკა'],
  theatre: ['#6a2a5a', 'თეატრი'],
  // any named building that is not a business: engraved stone plate
  named: ['#2b2723', ''],
};
const AWNING_CATS = new Set(['cafe', 'restaurant', 'bar', 'bakery', 'fastfood']);
const CIVIC_CATS = new Set(['school', 'kindergarten', 'university', 'hospital',
  'clinic', 'police', 'fire_station', 'library', 'theatre', 'post']);
// these get the full glass shopfront module (civic buildings get plates and
// crosses instead, malls their own Blender models)
const MODULE_CATS = new Set(Object.keys(CAT_STYLE)
  .filter((c) => !['fuel', 'hotel', 'market', 'named'].includes(c) && !CIVIC_CATS.has(c)));

export async function buildShopfronts(citymap, buildings) {
  const res = await fetch('tbilisi/pois2.json');
  if (!res.ok) { console.warn('pois2.json missing — no shopfronts'); return; }
  const { names, pois } = await res.json();
  // Named OSM buildings (palaces, theatres, ministries, heritage houses) get
  // their real name on the wall too — the same wall-snap + SignPool path,
  // just an engraved-plate style instead of a lit shop fascia.
  try {
    const nres = await fetch('tbilisi/named.json');
    if (nres.ok) {
      const nd = await nres.json();
      const base = names.length;
      names.push(...nd.names);
      pois.named = nd.pois.named.map(([x, z, ni, a]) => [x, z, ni + base, a]);
    }
  } catch { /* no named.json — shops only */ }

  // brand books are loaded further down, but the category overrides they
  // carry must be known before the POIs are bucketed
  let catOverride = new Map();
  try {
    const br0 = await fetch('tbilisi/brands.json');
    if (br0.ok) {
      for (const b of await br0.json()) if (b.cat) catOverride.set(b.name_ka, b.cat);
    }
  } catch { /* no brand book */ }

  // ---- flatten + grid-index the POIs (40 m cells) ----
  const all = [];
  const grid = new Map();
  const gk = (x, z) => `${Math.floor(x / 80)}_${Math.floor(z / 80)}`;
  // business POIs first, then named buildings — so a named building that is
  // ALSO a business (hotel, theatre) keeps the business sign and is skipped
  const cats = Object.entries(pois).sort((a, b) => (a[0] === 'named') - (b[0] === 'named'));
  for (const [cat0, pts] of cats) {
    if (!CAT_STYLE[cat0]) continue;
    for (const [x, z, ni, area] of pts) {
      // A brand book may override the OSM category: SOLO is tagged as a
      // clinic but is Bank of Georgia's lounge, Mozaika as a bar but sells
      // cosmetics, Gama as generic fuel but is CNG. The chain knows better.
      const bcat = ni >= 0 ? catOverride.get(names[ni]) : null;
      const cat = (bcat && CAT_STYLE[bcat]) ? bcat : cat0;
      if (cat === 'named') {
        const k = gk(x, z);
        let dup = false;
        for (let di = -1; di <= 1 && !dup; di++) {
          for (let dj = -1; dj <= 1 && !dup; dj++) {
            for (const p of grid.get(`${Math.floor(x / 80) + di}_${Math.floor(z / 80) + dj}`) || []) {
              if (p.cat !== 'named' && (p.x - x) ** 2 + (p.z - z) ** 2 < 30 * 30) { dup = true; break; }
            }
          }
        }
        if (dup) continue;
        void k;
      }
      // area-mapped POIs (schools, hospitals) sit at the BUILDING CENTROID —
      // on a big footprint that is 40+ m from its own wall, so the snap
      // radius must grow with the footprint. Point POIs keep a tight 30 m.
      const maxD = Math.min(85, 30 + Math.sqrt(area || 0) * 0.55);
      const p = { x, z, ni, cat, maxD, wall: null, d: 1e9 };
      all.push(p);
      const k = gk(x, z);
      if (!grid.has(k)) grid.set(k, []);
      grid.get(k).push(p);
    }
  }

  // ---- snap each POI to the nearest building wall segment ----
  // walk every building footprint once; for each outer segment ask the POI
  // grid who lives within 30 m — O(buildings) with tiny constant.
  for (let bi = 0; bi < buildings.length; bi++) {
    const b = buildings[bi];
    const h = b[0] / 10;
    if (h < 3) continue;
    const n = (b.length - 1) / 2;
    let cx = 0;
    let cz = 0;
    for (let i = 0; i < n; i++) { cx += b[1 + i * 2] / 10; cz += b[2 + i * 2] / 10; }
    cx /= n; cz /= n;
    for (let i = 0; i < n; i++) {
      const j = (i + 1) % n;
      const x1 = b[1 + i * 2] / 10;
      const z1 = b[2 + i * 2] / 10;
      const ex = b[1 + j * 2] / 10 - x1;
      const ez = b[2 + j * 2] / 10 - z1;
      const len = Math.hypot(ex, ez);
      if (len < 3.5) continue;
      const mx = x1 + ex / 2;
      const mz = z1 + ez / 2;
      const ci = Math.floor(mx / 80);
      const cj = Math.floor(mz / 80);
      for (let di = -1; di <= 1; di++) {
        for (let dj = -1; dj <= 1; dj++) {
          const cell = grid.get(`${ci + di}_${cj + dj}`);
          if (!cell) continue;
          for (const p of cell) {
            // point-to-segment distance + projection
            let t = ((p.x - x1) * ex + (p.z - z1) * ez) / (len * len);
            t = Math.max(0.08, Math.min(0.92, t));
            const px = x1 + ex * t;
            const pz = z1 + ez * t;
            const d = Math.hypot(p.x - px, p.z - pz);
            if (d < p.maxD && d < p.d) {
              let nx = -ez / len;
              let nz = ex / len;
              if ((mx - cx) * nx + (mz - cz) * nz < 0) { nx = -nx; nz = -nz; }
              p.d = d;
              p.wall = { x: px + nx * 0.1, z: pz + nz * 0.1, nx, nz, ang: Math.atan2(nx, nz), len };
            }
          }
        }
      }
    }
  }

  // ---- load the shopfront glass module from the facade kit ----
  let shopGeo = null;
  let shopMat = null;
  try {
    const gltf = await new Promise((ok, bad) =>
      newGltfLoader().load('models/facade_kit.glb', ok, undefined, bad));
    const parts = [];
    gltf.scene.updateMatrixWorld(true);
    gltf.scene.traverse((o) => {
      if (o.isMesh && o.name.replace(/[._]\d+$/, '') === 'win_shop') parts.push(o);
    });
    if (parts.length) {
      const geos = parts.map((o) => {
        const g = o.geometry.clone().applyMatrix4(o.matrixWorld);
        delete g.attributes.uv;
        return g;
      });
      shopGeo = parts.length === 1 ? geos[0] : mergeGeometries(geos, true);
      shopMat = parts.length === 1 ? parts[0].material : parts.map((o) => o.material);
      (Array.isArray(shopMat) ? shopMat : [shopMat]).forEach((m) => { m.metalness = 0; m.roughness = 0.85; });
    }
  } catch (e) { console.warn('shopfront kit load failed:', e); }

  // ---- build the instanced layers ----
  const moduleItems = [];
  const awningItems = [];
  const canopyItems = [];
  const crossItems = []; // pharmacy/red-cross/police lightboxes
  const signSpots = []; // fed to the SignPool
  for (const p of all) {
    const st = CAT_STYLE[p.cat];
    if (p.cat === 'fuel') {
      const gy = citymap.heightAt(p.x, p.z);
      canopyItems.push({ x: p.x, z: p.z, y: gy, ang: (p.x * 7.3 + p.z) % 6.28 });
      signSpots.push({ x: p.x, z: p.z, y: gy + 4.55, ang: (p.x * 7.3 + p.z) % 6.28, cat: p.cat, ni: p.ni });
      continue;
    }
    if (!p.wall) continue; // courtyard/parcel POIs with no street wall
    const gy = citymap.heightAt(p.wall.x, p.wall.z);
    const w = p.wall;
    if (MODULE_CATS.has(p.cat)) {
      moduleItems.push({ x: w.x, z: w.z, y: gy + 2.5, ang: w.ang });
      if (w.len >= 9.5) { // wide frontage: a second module beside the first
        moduleItems.push({
          x: w.x + w.nz * 3.05, z: w.z - w.nx * 3.05, y: gy + 2.5, ang: w.ang,
        });
      }
    }
    if (AWNING_CATS.has(p.cat)) {
      awningItems.push({ x: w.x + w.nx * 0.2, z: w.z + w.nz * 0.2, y: gy + 3.0, ang: w.ang, cat: p.cat });
    }
    // pharmacy / medical / police get a perpendicular lightbox beside the sign
    if (p.cat === 'pharmacy' || p.cat === 'hospital' || p.cat === 'clinic' || p.cat === 'police') {
      crossItems.push({
        x: w.x + w.nx * 0.62 + w.nz * 2.2, z: w.z + w.nz * 0.62 - w.nx * 2.2,
        y: gy + 4.1, ang: w.ang + Math.PI / 2,
        kind: p.cat === 'pharmacy' ? 0 : p.cat === 'police' ? 2 : 1,
      });
    }
    signSpots.push({ x: w.x + w.nx * 0.22, z: w.z + w.nz * 0.22, y: gy + 3.2, ang: w.ang, cat: p.cat, ni: p.ni });
  }

  citymap._tagFacadeProps = true; // shopfronts die with their building
  if (shopGeo) {
    citymap._propKind = 'glass'; // shop windows shatter like any other pane
    citymap._placeChunkedInstanced(shopGeo, shopMat, moduleItems, (m, it) => {
      m.makeRotationY(it.ang);
      m.setPosition(it.x, it.y, it.z);
    }, 500, false, 700);
    citymap._propKind = 'prop';
  }
  // striped awnings, two cloth colors alternating per spot
  const awnGeo = new THREE.BoxGeometry(4.6, 0.14, 1.5);
  awnGeo.translate(0, 0, 0.78);
  const stripes = (c1, c2) => {
    const c = document.createElement('canvas');
    c.width = 64; c.height = 8;
    const g = c.getContext('2d');
    g.fillStyle = c1; g.fillRect(0, 0, 64, 8);
    g.fillStyle = c2;
    for (let i = 0; i < 8; i++) g.fillRect(i * 8, 0, 4, 8);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    return t;
  };
  const awnMats = [
    new THREE.MeshLambertMaterial({ map: stripes('#b43a2a', '#efe8dc') }),
    new THREE.MeshLambertMaterial({ map: stripes('#2a6a4a', '#efe8dc') }),
  ];
  for (let v = 0; v < 2; v++) {
    const list = awningItems.filter((it, k) => k % 2 === v);
    citymap._placeChunkedInstanced(awnGeo, awnMats[v], list, (m, it) => {
      m.makeRotationY(it.ang);
      // slight downward pitch so the cloth reads as an awning, not a shelf
      const tilt = new THREE.Matrix4().makeRotationX(0.28);
      m.multiply(tilt);
      m.setPosition(it.x, it.y, it.z);
    }, 500, false, 550);
  }
  // perpendicular lightboxes: green pharmacy cross / red medical cross /
  // blue POLICE box — double-sided canvas faces on a slim box
  const boxKind = (draw) => {
    const c = document.createElement('canvas');
    c.width = 128; c.height = 128;
    draw(c.getContext('2d'));
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return new THREE.MeshBasicMaterial({ map: t, side: THREE.DoubleSide });
  };
  const crossMats = [
    boxKind((g) => { // green pharmacy cross
      g.fillStyle = '#ffffff'; g.fillRect(0, 0, 128, 128);
      g.fillStyle = '#1c9a4c';
      g.fillRect(48, 16, 32, 96); g.fillRect(16, 48, 96, 32);
    }),
    boxKind((g) => { // red medical cross
      g.fillStyle = '#ffffff'; g.fillRect(0, 0, 128, 128);
      g.fillStyle = '#c42a2a';
      g.fillRect(48, 16, 32, 96); g.fillRect(16, 48, 96, 32);
    }),
    boxKind((g) => { // police shield
      g.fillStyle = '#1a3a6e'; g.fillRect(0, 0, 128, 128);
      g.fillStyle = '#ffffff';
      g.font = 'bold 34px sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText('112', 64, 44);
      g.font = 'bold 22px "Noto Sans Georgian", sans-serif';
      g.fillText('პოლიცია', 64, 86);
    }),
  ];
  const crossGeo = new THREE.PlaneGeometry(1.1, 1.1);
  for (let kind = 0; kind < 3; kind++) {
    const list = crossItems.filter((it) => it.kind === kind);
    if (!list.length) continue;
    citymap._placeChunkedInstanced(crossGeo, crossMats[kind], list, (m, it) => {
      m.makeRotationY(it.ang);
      m.setPosition(it.x, it.y, it.z);
    }, 800, false, 700);
  }
  citymap._tagFacadeProps = false;

  // fuel canopies stand on their own — not building-bound
  if (canopyItems.length) {
    const roofG = new THREE.BoxGeometry(11, 0.6, 7);
    roofG.translate(0, 5.2, 0);
    const postG = new THREE.BoxGeometry(0.5, 5.2, 0.5);
    postG.translate(0, 2.6, 0);
    const post2 = postG.clone().translate(4, 0, 0);
    const post1 = postG.clone().translate(-4, 0, 0);
    const canopyGeo = mergeGeometries([roofG, post1, post2]);
    citymap._placeChunkedInstanced(canopyGeo, new THREE.MeshLambertMaterial({ color: 0xe8e8e8 }),
      canopyItems, (m, it) => {
        m.makeRotationY(it.ang);
        m.setPosition(it.x, it.y, it.z);
      }, 1500, false, 1600);
  }

  // McDonald's pad sites (no street wall = standalone drive-through lot):
  // the real branded building with the Marjanishvili photo textures.
  for (const p of all) {
    if (p.cat !== 'mcdonalds' || p.wall) continue;
    const m = BUILDERS.mcdonalds();
    m.position.set(p.x, citymap.heightAt(p.x, p.z), p.z);
    m.rotation.y = (p.x * 3.1 + p.z) % 6.28;
    citymap.group.add(m);
    citymap.registerLandmark(m, 'მაკდონალდსი');
  }

  // Real brand books (tools/cache/brands*.json → public/tbilisi/brands.json):
  // SPAR's green, Aversi's red cross, Bank of Georgia's orange — a chain's
  // actual fascia colours and wordmark instead of a generic category plate.
  let brands = null;
  try {
    const br = await fetch('tbilisi/brands.json');
    if (br.ok) {
      brands = new Map();
      for (const b of await br.json()) {
        brands.set(b.name_ka, b);
        if (b.brand) brands.set(b.brand.toLowerCase(), b);
      }
    }
  } catch { /* no brand book — category colours stay */ }

  citymap.shopSigns = new SignPool(citymap, signSpots, names, brands);
  // saved-damage restore ran before these layers existed — strip them from
  // any building that is already crumbled or collapsed
  citymap._restripDamaged?.();
  console.log(`shopfronts: ${moduleItems.length} modules, ${awningItems.length} awnings, `
    + `${canopyItems.length} fuel canopies, ${signSpots.length} sign spots (${names.length} names)`);
}

// Pooled real-name fascia signs: a fixed set of small canvas planes that
// re-attach to whichever sign spots are near the camera. Redraw only on
// reassignment — flying down a street costs a handful of 512x64 canvases.
const POOL = 192;
const RANGE = 460;
class SignPool {
  constructor(citymap, spots, names, brands = null) {
    this.citymap = citymap;
    this.names = names;
    this.brands = brands;
    this.spots = spots;
    this.grid = new Map();
    for (let i = 0; i < spots.length; i++) {
      const s = spots[i];
      const k = `${Math.floor(s.x / 250)}_${Math.floor(s.z / 250)}`;
      if (!this.grid.has(k)) this.grid.set(k, []);
      this.grid.get(k).push(i);
    }
    this.meshes = [];
    this.slotSpot = new Int32Array(POOL).fill(-1);
    this.spotSlot = new Map();
    this.cool = 0;
    const geo = new THREE.PlaneGeometry(5.2, 0.82);
    for (let i = 0; i < POOL; i++) {
      const c = document.createElement('canvas');
      c.width = 512; c.height = 80;
      const tex = new THREE.CanvasTexture(c);
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.anisotropy = 4;
      const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ map: tex }));
      mesh.visible = false;
      mesh.matrixAutoUpdate = false;
      citymap.group.add(mesh);
      this.meshes.push(mesh);
    }
  }

  // The emblem beside the wordmark, drawn from the brand book's `mark` field.
  // Chains are recognised by their colour + shape long before the text is
  // legible, so this is what stops a SPAR reading as a generic green plate.
  static drawMark(g, kind, x, y, r, color, accent) {
    g.save();
    g.translate(x, y);
    g.fillStyle = color;
    g.strokeStyle = color;
    g.lineWidth = Math.max(2, r * 0.22);
    switch (kind) {
      case 'cross':
        g.fillRect(-r * 0.28, -r, r * 0.56, r * 2);
        g.fillRect(-r, -r * 0.28, r * 2, r * 0.56);
        break;
      case 'fir':
        for (let i = 0; i < 3; i++) {
          const t = -r + i * r * 0.62;
          const w = r * (0.55 + i * 0.28);
          g.beginPath();
          g.moveTo(0, t);
          g.lineTo(w, t + r * 0.78);
          g.lineTo(-w, t + r * 0.78);
          g.closePath();
          g.fill();
        }
        g.fillStyle = accent || color;
        g.fillRect(-r * 0.14, r * 0.72, r * 0.28, r * 0.34);
        break;
      case 'leaf':
        g.beginPath();
        g.moveTo(0, r);
        g.quadraticCurveTo(r * 1.1, r * 0.1, 0, -r);
        g.quadraticCurveTo(-r * 1.1, r * 0.1, 0, r);
        g.fill();
        break;
      case 'drop':
        g.beginPath();
        g.moveTo(0, -r);
        g.quadraticCurveTo(r * 0.95, r * 0.2, 0, r);
        g.quadraticCurveTo(-r * 0.95, r * 0.2, 0, -r);
        g.fill();
        break;
      case 'circle':
        g.beginPath();
        g.arc(0, 0, r * 0.92, 0, 6.283);
        g.fill();
        if (accent) {
          g.fillStyle = accent;
          g.beginPath();
          g.arc(0, 0, r * 0.5, 0, 6.283);
          g.fill();
        }
        break;
      case 'chevron':
        for (let i = 0; i < 2; i++) {
          g.beginPath();
          g.moveTo(-r + i * r * 0.85, -r);
          g.lineTo(r * 0.1 + i * r * 0.85, 0);
          g.lineTo(-r + i * r * 0.85, r);
          g.stroke();
          if (accent) g.strokeStyle = accent;
        }
        break;
      case 'arch':
        g.beginPath();
        g.arc(-r * 0.42, r * 0.5, r * 0.55, Math.PI, 0);
        g.arc(r * 0.42, r * 0.5, r * 0.55, Math.PI, 0);
        g.lineWidth = r * 0.34;
        g.stroke();
        break;
      case 'star':
        g.beginPath();
        for (let i = 0; i < 10; i++) {
          const a = (i * Math.PI) / 5 - Math.PI / 2;
          const rr = i % 2 ? r * 0.45 : r;
          g[i ? 'lineTo' : 'moveTo'](Math.cos(a) * rr, Math.sin(a) * rr);
        }
        g.closePath();
        g.fill();
        break;
      case 'square':
        g.fillRect(-r * 0.85, -r * 0.85, r * 1.7, r * 1.7);
        if (accent) {
          g.fillStyle = accent;
          g.fillRect(-r * 0.4, -r * 0.4, r * 0.8, r * 0.8);
        }
        break;
      default:
        break;
    }
    g.restore();
  }

  drawBrand(slot, spot, b) {
    const mesh = this.meshes[slot];
    const c = mesh.material.map.image;
    const g = c.getContext('2d');
    g.fillStyle = b.bg || '#333';
    g.fillRect(0, 0, 512, 80);
    // lit fascia: a lighter top edge and a shadowed lower lip
    const grad = g.createLinearGradient(0, 0, 0, 80);
    grad.addColorStop(0, 'rgba(255,255,255,0.16)');
    grad.addColorStop(0.45, 'rgba(255,255,255,0)');
    grad.addColorStop(1, 'rgba(0,0,0,0.20)');
    g.fillStyle = grad;
    g.fillRect(0, 0, 512, 80);
    const text = b.wordmark || spot.name || '';
    const hasMark = b.mark && b.mark !== 'none';
    if (hasMark) SignPool.drawMark(g, b.mark, 52, 40, 26, b.accent || b.fg || '#fff', b.bg);
    g.fillStyle = b.fg || '#fff';
    const left = hasMark ? 96 : 24;
    let size = 44;
    const fam = b.script === 'latin' ? 'Arial, sans-serif' : '"Noto Sans Georgian", sans-serif';
    g.font = `800 ${size}px ${fam}`;
    while (g.measureText(text).width > 512 - left - 24 && size > 16) {
      size -= 3;
      g.font = `800 ${size}px ${fam}`;
    }
    g.textAlign = 'left';
    g.textBaseline = 'middle';
    g.fillText(text, left, 42);
    mesh.material.map.needsUpdate = true;
    const m = mesh.matrix;
    m.makeRotationY(spot.ang);
    m.setPosition(spot.x, spot.y, spot.z);
    mesh.visible = true;
  }

  draw(slot, spot) {
    const mesh = this.meshes[slot];
    const c = mesh.material.map.image;
    const g = c.getContext('2d');
    const nm = spot.ni >= 0 ? this.names[spot.ni] : null;
    const brand = nm && this.brands
      ? (this.brands.get(nm) || this.brands.get(nm.toLowerCase()))
      : null;
    if (brand) return this.drawBrand(slot, spot, brand);
    const [bg, fallback] = CAT_STYLE[spot.cat] ?? ['#555', ''];
    const name = spot.ni >= 0 ? this.names[spot.ni] : fallback;
    if (spot.cat === 'named') { // engraved stone plate, gold lettering
      g.fillStyle = bg;
      g.fillRect(0, 0, 512, 80);
      g.strokeStyle = 'rgba(214,182,110,0.75)';
      g.lineWidth = 2;
      g.strokeRect(9, 9, 494, 62);
      g.fillStyle = '#d6b66e';
      let s2 = 34;
      g.font = `600 ${s2}px "Noto Sans Georgian", serif`;
      while (g.measureText(name).width > 470 && s2 > 15) {
        s2 -= 3;
        g.font = `600 ${s2}px "Noto Sans Georgian", serif`;
      }
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(name, 256, 41);
      mesh.material.map.needsUpdate = true;
      const mm = mesh.matrix;
      mm.makeRotationY(spot.ang);
      mm.setPosition(spot.x, spot.y, spot.z);
      mesh.visible = true;
      return;
    }
    const civic = CIVIC_CATS.has(spot.cat);
    const lightBg = civic && bg.startsWith('#f') || bg === '#ffffff';
    g.fillStyle = bg;
    g.fillRect(0, 0, 512, 80);
    if (civic) {
      // institutional plate: thin double frame instead of the lit strip
      g.strokeStyle = lightBg ? '#1a3a6e' : 'rgba(255,255,255,0.8)';
      g.lineWidth = 3;
      g.strokeRect(6, 6, 500, 68);
      g.strokeRect(12, 12, 488, 56);
    } else {
      g.fillStyle = 'rgba(255,255,255,0.14)';
      g.fillRect(0, 66, 512, 14); // lit underside strip
    }
    g.fillStyle = lightBg ? '#1a3a6e' : '#ffffff';
    let size = 40;
    g.font = `600 ${size}px "Noto Sans Georgian", sans-serif`;
    while (g.measureText(name).width > 488 && size > 17) {
      size -= 3;
      g.font = `600 ${size}px "Noto Sans Georgian", sans-serif`;
    }
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(name, 256, 40);
    mesh.material.map.needsUpdate = true;
    const m = mesh.matrix;
    m.makeRotationY(spot.ang);
    m.setPosition(spot.x, spot.y, spot.z);
    mesh.visible = true;
  }

  update(camPos, dt = 0.016) {
    this.cool -= dt;
    if (this.cool > 0) return;
    this.cool = 0.55;
    const ci = Math.floor(camPos.x / 250);
    const cj = Math.floor(camPos.z / 250);
    const near = [];
    const reach = Math.ceil(RANGE / 250);
    for (let di = -reach; di <= reach; di++) {
      for (let dj = -reach; dj <= reach; dj++) {
        const cell = this.grid.get(`${ci + di}_${cj + dj}`);
        if (!cell) continue;
        for (const si of cell) {
          const s = this.spots[si];
          const d2 = (s.x - camPos.x) ** 2 + (s.z - camPos.z) ** 2;
          if (d2 < RANGE * RANGE) near.push([d2, si]);
        }
      }
    }
    near.sort((a, b) => a[0] - b[0]);
    const want = new Set();
    for (let i = 0; i < Math.min(POOL, near.length); i++) want.add(near[i][1]);
    // free slots whose spot fell out of range
    const free = [];
    for (let slot = 0; slot < POOL; slot++) {
      const si = this.slotSpot[slot];
      if (si < 0) { free.push(slot); continue; }
      if (!want.has(si)) {
        this.spotSlot.delete(si);
        this.slotSpot[slot] = -1;
        this.meshes[slot].visible = false;
        free.push(slot);
      } else {
        want.delete(si); // already shown
      }
    }
    for (const si of want) {
      const slot = free.pop();
      if (slot === undefined) break;
      this.slotSpot[slot] = si;
      this.spotSlot.set(si, slot);
      this.draw(slot, this.spots[si]);
    }
  }
}
