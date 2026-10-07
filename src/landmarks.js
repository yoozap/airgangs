import * as THREE from 'three';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { newGltfLoader } from './gltf.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

// Real Tbilisi/Georgia landmarks, hand-modeled at their true coordinates,
// plus the Black Sea with ships and real McDonald's locations from OSM.

export const EXCLUSIONS = [
  { lat: 41.69575, lon: 44.78499, r: 45 },  // TV tower (way 437940450)
  { lat: 41.69754, lon: 44.81654, r: 150 }, // Sameba cathedral + walled park (way 585352693)
  { lat: 41.70392, lon: 44.79369, r: 60 },  // Radisson Iveria (way 1299930496)
  { lat: 41.77056, lon: 44.81026, r: 70 },  // Chronicle of Georgia (way 707446533)
  { lat: 41.65596, lon: 41.63934, r: 30 },  // Alphabet Tower (way 405852444)
  { lat: 41.6541, lon: 41.63674, r: 35 },   // Ferris wheel (node 6822566652)
  { lat: 41.70112, lon: 44.79626, r: 50 },  // Opera House (way 59151604)
  { lat: 41.69667, lon: 44.79805, r: 100 }, // Parliament complex (OSM rel 1020718)
  { lat: 41.72285, lon: 44.78966, r: 120 }, // Dinamo Arena (rel 17823425)
  { lat: 41.70747, lon: 44.78555, r: 45 },  // Concert Hall (OSM rel 18129586)
  { lat: 41.68787, lon: 44.80894, r: 90 },  // Narikala fortress (way 9820020)
  { lat: 41.69489, lon: 44.78652, r: 30 },  // Funicular upper station (way 25699467)
  { lat: 41.69015, lon: 44.8113, r: 35 },   // Metekhi church (way 1064270003)
  { lat: 41.6876, lon: 44.8111, r: 70 },    // Abanotubani sulfur baths
  { lat: 41.69916, lon: 44.7983, r: 45 },   // Tbilisi Marriott (way 7108262)
  { lat: 41.70245, lon: 44.79467, r: 50 },  // Biltmore tower (way 19590999)
  { lat: 41.68885, lon: 44.82317, r: 65 },  // Sheraton Metechi Palace (way 20990671)
  { lat: 41.70525, lon: 44.80045, r: 25 },  // Radisson RED (node 11358652556)
  { lat: 41.83838, lon: 44.73355, r: 45 },  // Jvari monastery hilltop (way 1497018777)
  { lat: 41.699024, lon: 44.806656, r: 80 }, // Public Service Hall (way 1066392731)
  { lat: 41.69339, lon: 44.8015, r: 22 },   // Freedom Monument circle (way 1018836829)
  { lat: 41.69032, lon: 44.81073, r: 14 },  // Gorgasali statue footprint (way 1062990549)
  { lat: 41.68812, lon: 44.8046, r: 14 },   // Kartlis Deda footprint (way 1063062536)
  { lat: 41.70389, lon: 44.78993, r: 12 },  // Rustaveli monument footprint (way 1066637619)
  { lat: 41.69775, lon: 44.79818, r: 10 },  // writers pair footprint (way 1066626116)
  { lat: 41.69525, lon: 44.8, r: 62 },      // Galleria Tbilisi (real model in metro.js)
  { lat: 41.70418, lon: 44.78971, r: 26 },  // Rustaveli metro pavilion (real model in metro.js)
  { lat: 41.72254, lon: 44.79777, r: 46 },  // Station Square terminal (real model in metro.js)
  { lat: 41.669742, lon: 44.963939, r: 150 }, // TBS main terminal (real model in airport.js)
  { lat: 41.674056, lon: 44.958342, r: 70 },  // TBS 1952 VIP terminal (airport.js)
  { lat: 41.666319, lon: 44.965312, r: 48 },  // TBS maintenance hangar (airport.js)
  { lat: 41.680858, lon: 44.944375, r: 28 },  // TBS north hangar (airport.js)
];

const label = (text, w, h, bg, fg, size = 48) => {
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const g = c.getContext('2d');
  g.fillStyle = bg;
  g.fillRect(0, 0, w, h);
  g.fillStyle = fg;
  g.font = `bold ${size}px sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText(text, w / 2, h / 2 + 2);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return t;
};

const lambert = (color, extra = {}) => new THREE.MeshLambertMaterial({ color, ...extra });
const GOLD = new THREE.MeshStandardMaterial({ color: 0xd9b13b, metalness: 0.85, roughness: 0.25 });
const WHITE = lambert(0xe9e6dd);
const CREAM = lambert(0xe6dcc4);
const STEEL = lambert(0xd8dde2);
const RED = lambert(0xb43a2a);

function box(w, h, d, mat, x = 0, y = 0, z = 0) {
  const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
  m.position.set(x, y, z);
  m.castShadow = true;
  return m;
}

function cyl(r1, r2, h, mat, x = 0, y = 0, z = 0, seg = 10) {
  const m = new THREE.Mesh(new THREE.CylinderGeometry(r1, r2, h, seg), mat);
  m.position.set(x, y, z);
  m.castShadow = true;
  return m;
}

// --- landmark builders ------------------------------------------------------

// Tbilisi TV Broadcasting Tower (1972, 274.5 m), modeled from photos: a
// weathered-orange segmented steel tube, three white tubular legs leaning on
// it below the bulbous windowed observation pod, then a white/rust striped
// antenna mast full of small dipole stubs, mini platforms and a needle tip.
function tvTower() {
  const g = new THREE.Group();
  const RUST = lambert(0xa4562e);
  const RUST_D = lambert(0x84492a);

  // central segmented tube with flange rings, ground → pod
  g.add(cyl(2.3, 3.5, 142, RUST, 0, 71, 0, 14));
  for (let i = 1; i <= 6; i++) {
    const y = i * 21;
    g.add(cyl(3.75 - (y / 142) * 1.25, 3.75 - (y / 142) * 1.25, 0.9, RUST_D, 0, y, 0, 14));
  }

  // three white tubular legs leaning onto the shaft just below the pod
  const up = new THREE.Vector3(0, 1, 0);
  for (let i = 0; i < 3; i++) {
    const a = (i / 3) * Math.PI * 2 + 0.4;
    const gx = Math.cos(a) * 31;
    const gz = Math.sin(a) * 31;
    const tx = Math.cos(a) * 2.6;
    const tz = Math.sin(a) * 2.6;
    const dir = new THREE.Vector3(tx - gx, 139, tz - gz);
    const leg = new THREE.Mesh(new THREE.CylinderGeometry(1.3, 1.8, dir.length() + 4, 10), WHITE);
    leg.position.set((gx + tx) / 2, 69.5, (gz + tz) / 2);
    leg.quaternion.setFromUnitVectors(up, dir.normalize());
    leg.castShadow = true;
    g.add(leg);
    // thin service braces tying the leg back to the tube
    for (const [by, f] of [[38, 0.71], [76, 0.42], [106, 0.16]]) {
      const bx = Math.cos(a) * 31 * f;
      const bz = Math.sin(a) * 31 * f;
      const bd = new THREE.Vector3(bx - Math.cos(a) * 3, 0, bz - Math.sin(a) * 3);
      const brace = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.16, bd.length(), 5), STEEL);
      brace.position.set((bx + Math.cos(a) * 3) / 2, by, (bz + Math.sin(a) * 3) / 2);
      brace.quaternion.setFromUnitVectors(up, bd.normalize());
      g.add(brace);
    }
  }

  // observation pod: inverted-cone bowl, windowed band, railed deck, cap
  g.add(cyl(9.2, 2.9, 10, WHITE, 0, 147, 0, 18)); // bowl
  const winC = document.createElement('canvas');
  winC.width = 128;
  winC.height = 32;
  const wg = winC.getContext('2d');
  wg.fillStyle = '#e9e6dd';
  wg.fillRect(0, 0, 128, 32);
  wg.fillStyle = '#232a2f';
  for (let x = 6; x < 128; x += 16) wg.fillRect(x, 8, 11, 17); // window row
  const winTex = new THREE.CanvasTexture(winC);
  winTex.colorSpace = THREE.SRGBColorSpace;
  winTex.wrapS = THREE.RepeatWrapping;
  winTex.repeat.set(10, 1);
  g.add(cyl(9.45, 9.45, 4.6, lambert(0xffffff, { map: winTex }), 0, 154.3, 0, 18));
  g.add(cyl(9.7, 9.7, 0.7, WHITE, 0, 156.9, 0, 18)); // deck lip
  const rail = new THREE.Mesh(new THREE.TorusGeometry(9.55, 0.09, 5, 24), STEEL);
  rail.rotation.x = Math.PI / 2;
  rail.position.y = 158.3;
  g.add(rail);
  g.add(cyl(2.1, 9.2, 3.4, WHITE, 0, 159, 0, 18)); // conic cap to the mast

  // striped mast: white/rust segments tapering to the needle
  const stripes = [
    [161, 15, 1.9, WHITE], [176, 14, 1.7, RUST], [190, 14, 1.55, WHITE],
    [204, 14, 1.4, RUST], [218, 14, 1.25, WHITE], [232, 13, 1.05, RUST],
    [245, 13, 0.85, WHITE], [258, 9, 0.6, RUST],
  ];
  for (const [y0, h, r, mat] of stripes) g.add(cyl(r * 0.92, r, h, mat, 0, y0 + h / 2, 0, 12));
  for (const [py, pr] of [[190.5, 3.1], [232.5, 2.4]]) {
    g.add(cyl(pr, pr, 0.45, STEEL, 0, py, 0, 12)); // service platforms
    const pRail = new THREE.Mesh(new THREE.TorusGeometry(pr - 0.15, 0.07, 5, 20), STEEL);
    pRail.rotation.x = Math.PI / 2;
    pRail.position.y = py + 1;
    g.add(pRail);
  }
  g.add(cyl(0.16, 0.42, 12, WHITE, 0, 273, 0, 8)); // needle
  g.add(cyl(0.05, 0.14, 6, RUST, 0, 281, 0, 6));

  // small dipole/antenna stubs peppered over the mast — one merged mesh
  const stubs = [];
  for (let k = 0; k < 64; k++) {
    const y = 162 + ((k * 37) % 100) + (k % 3);
    const a = k * 2.39996; // golden angle spiral around the mast
    const r = 2.4 - (y - 162) / 100;
    const len = 1.6 + ((k * 13) % 10) / 6;
    const s = new THREE.CylinderGeometry(0.07, 0.07, len, 4);
    s.rotateZ(Math.PI / 2);
    s.rotateY(-a);
    s.translate(Math.cos(a) * (r + len / 2), y, Math.sin(a) * (r + len / 2));
    stubs.push(s);
    if (k % 5 === 0) {
      const v = new THREE.CylinderGeometry(0.06, 0.06, 2.4, 4);
      v.translate(Math.cos(a) * (r + len), y + 0.8, Math.sin(a) * (r + len));
      stubs.push(v); // vertical dipole at the stub tip
    }
  }
  const stubMesh = new THREE.Mesh(mergeGeometries(stubs), STEEL);
  stubMesh.castShadow = true;
  g.add(stubMesh);
  return g;
}

// Tsminda Sameba cathedral, modeled from photos and clad in REAL textures cut
// from a courtyard photograph (tools/fetch_facades.py → sameba_stone /
// sameba_arches / sameba_cross): stepped cross plan, gabled arms with stone
// pediments and carved-cross decals, arcaded drum, faceted gold dome to the
// true ~95 m, plus the courtyard bell tower. Dark ribbed metal roofs.
function sameba() {
  const g = new THREE.Group();

  // real-photo materials load in and replace the plain tints; registry keeps
  // per-surface UV repeats (texture clones share one image)
  const pend = { stone: [], arch: [], cross: [] };
  const reg = (kind, rx, ry, tint) => {
    const m = lambert(tint);
    pend[kind].push({ m, rx, ry });
    return m;
  };
  try {
    const tl = new THREE.TextureLoader();
    const hook = (file, kind) =>
      tl.load(file, (t) => {
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.anisotropy = 8;
        for (const { m, rx, ry } of pend[kind]) {
          const c = t.clone();
          c.needsUpdate = true;
          c.repeat.set(rx, ry);
          m.map = c;
          m.color.set(0xffffff);
          m.needsUpdate = true;
        }
      });
    hook('textures/facades/sameba_stone.jpg', 'stone');
    hook('textures/facades/sameba_arches.jpg', 'arch');
    hook('textures/facades/sameba_cross.jpg', 'cross');
  } catch {
    /* Node OBJ export — keep plain stone tints */
  }
  const stone = (rx, ry, tint = 0xd9c8a4) => reg('stone', rx, ry, tint);
  const arches = (len) => reg('arch', len / 26, 1, 0xcdbb97); // photo band ≈ 26 m
  const sBox = (w, h, d, x, y, z, tint) => box(w, h, d, stone(Math.max(w, d) / 10, h / 8, tint), x, y, z);

  // dark ribbed standing-seam metal, as in the photos
  const roofC = document.createElement('canvas');
  roofC.width = roofC.height = 64;
  const rg = roofC.getContext('2d');
  rg.fillStyle = '#49525c'; // slate blue-grey, as in the photos
  rg.fillRect(0, 0, 64, 64);
  rg.fillStyle = '#5a636e';
  for (let x = 0; x < 64; x += 8) rg.fillRect(x, 0, 2, 64);
  const roofT = new THREE.CanvasTexture(roofC);
  roofT.wrapS = roofT.wrapT = THREE.RepeatWrapping;
  roofT.colorSpace = THREE.SRGBColorSpace;
  const roofMat = (rx, ry) => {
    const t = roofT.clone();
    t.needsUpdate = true;
    t.repeat.set(rx, ry);
    return lambert(0xffffff, { map: t });
  };

  // gable: two sloped ribbed planes over a stone pediment prism (ridge ∥ X)
  const gable = (len, wid, rise, x, y, z, alongZ = false) => {
    const grp = new THREE.Group();
    const sh = new THREE.Shape();
    sh.moveTo(-wid / 2, 0);
    sh.lineTo(wid / 2, 0);
    sh.lineTo(0, rise);
    sh.closePath();
    const ped = new THREE.ExtrudeGeometry(sh, { depth: len, bevelEnabled: false });
    ped.translate(0, 0, -len / 2);
    ped.rotateY(Math.PI / 2);
    const pm = new THREE.Mesh(ped, stone(wid / 6, rise / 5));
    pm.castShadow = true;
    grp.add(pm);
    const slope = Math.hypot(wid / 2, rise) + 0.9;
    const ang = Math.atan2(rise, wid / 2);
    for (const s of [-1, 1]) {
      const p = box(len + 1.4, 0.55, slope, roofMat((len + 1.4) / 4, slope / 4), 0, 0, 0);
      p.rotation.x = s * ang;
      p.position.set(0, rise / 2 + 0.4, (s * wid) / 4);
      grp.add(p);
    }
    grp.position.set(x, y, z);
    if (alongZ) grp.rotation.y = Math.PI / 2;
    return grp;
  };
  const goldCross = (x, y, z, s = 1) => {
    const grp = new THREE.Group();
    grp.add(cyl(0.16 * s, 0.16 * s, 3 * s, GOLD, 0, 1.5 * s, 0, 6));
    grp.add(box(1.7 * s, 0.34 * s, 0.34 * s, GOLD, 0, 2 * s, 0));
    grp.position.set(x, y, z);
    return grp;
  };

  // grand portal texture: carved arch rings + dark opening + wooden doors,
  // drawn over the photo's stone tone — the big church doors
  const portalC = document.createElement('canvas');
  portalC.width = 128;
  portalC.height = 160;
  const pg = portalC.getContext('2d');
  pg.fillStyle = '#d6c5a1';
  pg.fillRect(0, 0, 128, 160);
  pg.fillStyle = '#c2b08a';
  pg.beginPath();
  pg.arc(64, 66, 52, Math.PI, 0);
  pg.rect(12, 66, 104, 94);
  pg.fill();
  pg.fillStyle = '#26201a'; // arch opening
  pg.beginPath();
  pg.arc(64, 66, 40, Math.PI, 0);
  pg.rect(24, 66, 80, 94);
  pg.fill();
  pg.fillStyle = '#5c3b22'; // wooden double door
  pg.fillRect(34, 86, 60, 74);
  pg.fillStyle = '#452a17';
  pg.fillRect(62, 86, 4, 74);
  for (let x = 40; x < 94; x += 9) pg.fillRect(x, 92, 2, 62);
  const portalT = new THREE.CanvasTexture(portalC);
  portalT.colorSpace = THREE.SRGBColorSpace;
  const portal = (w, h, x, y, z, ry) => {
    const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h), lambert(0xffffff, { map: portalT }));
    m.position.set(x, y, z);
    m.rotation.y = ry;
    return m;
  };

  // --- the cathedral: a cascade of gabled tiers up to the gold dome -------
  const cath = new THREE.Group();
  // tier 1 — broad base with corner chapels
  cath.add(sBox(62, 15, 50, 0, 7.5, 0));
  cath.add(box(62.4, 3.2, 50.4, arches(62), 0, 13.2, 0));
  for (const [cx, cz] of [[-24, -19], [24, -19], [-24, 19], [24, 19]]) {
    cath.add(sBox(14, 19, 12, cx, 9.5, cz));
    cath.add(gable(12, 14, 3.6, cx, 19, cz, true));
  }
  // tier 2 — main cross arms
  cath.add(sBox(56, 18, 34, 0, 24, 0));
  cath.add(sBox(34, 18, 56, 0, 24, 0));
  cath.add(box(56.4, 3, 34.4, arches(56), 0, 31.2, 0));
  cath.add(box(34.4, 3, 56.4, arches(34), 0, 31.6, 0));
  cath.add(gable(56, 34, 8, 0, 33, 0));
  cath.add(gable(56, 34, 8, 0, 33, 0, true));
  // tier 3 — upper cross
  cath.add(sBox(40, 17, 24, 0, 41.5, 0));
  cath.add(sBox(24, 17, 40, 0, 41.5, 0));
  cath.add(box(40.4, 2.8, 24.4, arches(40), 0, 48.4, 0));
  cath.add(box(24.4, 2.8, 40.4, arches(24), 0, 48.8, 0));
  cath.add(gable(40, 24, 7, 0, 50, 0));
  cath.add(gable(40, 24, 7, 0, 50, 0, true));
  // crossing, tall arcaded drum, fluted gold dome, main cross (≈100 m)
  cath.add(sBox(20, 8, 20, 0, 54, 0));
  cath.add(cyl(8.8, 8.8, 18, arches(55), 0, 67, 0, 16));
  cath.add(cyl(9.4, 9.4, 1.1, stone(4, 0.4), 0, 76.6, 0, 16));
  cath.add(cyl(0.45, 9.4, 12, GOLD, 0, 82.7, 0, 16));
  cath.add(goldCross(0, 88.7, 0, 2.2));
  for (const [ex, ez] of [[28, 0], [-28, 0], [0, 28], [0, -28]]) cath.add(goldCross(ex, 41.3, ez, 1));
  for (const [ex, ez] of [[20, 0], [-20, 0], [0, 20], [0, -20]]) cath.add(goldCross(ex, 57.3, ez, 0.8));
  // carved cross-medallion decals on the main pediments
  for (const [px, pz, ry] of [[28.4, 0, Math.PI / 2], [-28.4, 0, -Math.PI / 2], [0, 28.4, 0], [0, -28.4, Math.PI]]) {
    const decal = new THREE.Mesh(new THREE.PlaneGeometry(6, 6), reg('cross', 1, 1, 0xd9c8a4));
    decal.position.set(px, 36, pz);
    decal.rotation.y = ry;
    cath.add(decal);
  }
  // the big west doors facing the stairs + three side portals
  cath.add(portal(15, 15, -31.35, 7.5, 0, -Math.PI / 2));
  cath.add(portal(9, 10, 31.35, 5, 0, Math.PI / 2));
  cath.add(portal(9, 10, 0, 5, 25.35, 0));
  cath.add(portal(9, 10, 0, 5, -25.35, Math.PI));
  cath.position.y = 6;
  g.add(cath);

  // --- granite terrace, balustrade, grand west stairs, candelabra lamps ---
  const granite = (rx, ry) => stone(rx, ry, 0xa8a6a1);
  g.add(box(150, 6, 120, granite(24, 1), -6, 3, 0));
  const balu = lambert(0xd8d5cd);
  g.add(box(150, 1.3, 0.8, balu, -6, 6.6, -59.6));
  g.add(box(150, 1.3, 0.8, balu, -6, 6.6, 59.6));
  g.add(box(0.8, 1.3, 120, balu, 68.6, 6.6, 0));
  g.add(box(0.8, 1.3, 33, balu, -80.6, 6.6, -43));
  g.add(box(0.8, 1.3, 33, balu, -80.6, 6.6, 43));
  for (let i = 0; i < 8; i++) {
    const sh = 6 - i * 0.72;
    g.add(box(2.2, sh, 54, granite(1, 1), -82 - i * 2.2, sh / 2, 0)); // grand stairs
  }
  g.add(box(46, 1.6, 70, granite(8, 1), -122, 0.8, 0)); // lower plaza
  // white multi-globe candelabra lamps, merged into two meshes
  const poleGeos = [];
  const globeGeos = [];
  const LAMPS = [[-86, 27, 0], [-86, -27, 0], [-98, 27, 0], [-98, -27, 0], [-112, 27, 0], [-112, -27, 0],
    [-40, 52, 6], [-40, -52, 6], [10, 52, 6], [10, -52, 6], [56, 52, 6], [56, -52, 6], [-70, 0, 6]];
  for (const [lx, lz, by] of LAMPS) {
    const p = new THREE.CylinderGeometry(0.14, 0.2, 7, 6);
    p.translate(lx, by + 3.5, lz);
    poleGeos.push(p);
    const spots = [[0, 7.6, 0], [0.95, 6.7, 0], [-0.95, 6.7, 0], [0, 6.7, 0.95], [0, 6.7, -0.95]];
    for (const [ox, oy, oz] of spots) {
      const s = new THREE.SphereGeometry(0.42, 6, 5);
      s.translate(lx + ox, by + oy, lz + oz);
      globeGeos.push(s);
    }
  }
  const poles = new THREE.Mesh(mergeGeometries(poleGeos), lambert(0xe8e5da));
  const globes = new THREE.Mesh(mergeGeometries(globeGeos), lambert(0xfdf6e0));
  g.add(poles);
  g.add(globes);

  // --- tall three-tier bell tower beside the stairs ---
  const bt = new THREE.Group();
  bt.add(sBox(11, 5, 11, 0, 2.5, 0, 0xbdb6a9));
  bt.add(sBox(8.6, 12, 8.6, 0, 11, 0));
  bt.add(box(8.9, 5, 8.9, arches(9), 0, 19.5, 0)); // open arched belfry
  bt.add(sBox(9.6, 1, 9.6, 0, 22.5, 0));
  bt.add(box(7.4, 4.4, 7.4, arches(7), 0, 25.2, 0)); // upper arcade
  bt.add(sBox(8.2, 0.9, 8.2, 0, 27.8, 0));
  bt.add(cyl(0.15, 5.4, 7, stone(3, 1), 0, 31.7, 0, 8)); // stone spire
  bt.add(goldCross(0, 35.4, 0, 1.1));
  bt.position.set(-96, 0, -36);
  g.add(bt);

  // --- crenellated enclosure wall with the arched west gatehouse ---
  const wallM = () => stone(30, 1, 0xcabd9f);
  g.add(box(222, 4.6, 1.4, wallM(), -30, 2.3, -84));
  g.add(box(222, 4.6, 1.4, wallM(), -30, 2.3, 84));
  g.add(box(1.4, 4.6, 168, wallM(), 80, 2.3, 0));
  g.add(box(1.4, 4.6, 76, wallM(), -140, 2.3, -46));
  g.add(box(1.4, 4.6, 76, wallM(), -140, 2.3, 46));
  for (const [wx, wz] of [[-140, -84], [-140, 84], [80, -84], [80, 84]]) {
    g.add(cyl(2.4, 2.7, 7, stone(3, 1), wx, 3.5, wz, 8)); // corner turrets
  }
  g.add(sBox(5, 10, 5, -140, 5, -8.5)); // gate towers
  g.add(sBox(5, 10, 5, -140, 5, 8.5));
  g.add(box(2, 3.2, 12, stone(2, 0.4), -140, 8.6, 0)); // gate span
  g.add(portal(8, 8.6, -141.2, 4.3, 0, -Math.PI / 2)); // gate arch
  g.add(cyl(1.9, 1.9, 2, stone(2, 0.3), -140, 11.2, 0, 10));
  g.add(cyl(0.2, 2.2, 2.4, GOLD, -140, 13.4, 0, 10));
  g.add(goldCross(-140, 14.6, 0, 0.7));

  // --- park: lawns and columnar cypresses ---
  const lawn = lambert(0x50663a);
  g.add(box(190, 0.24, 20, lawn, -25, 0.12, -72));
  g.add(box(190, 0.24, 20, lawn, -25, 0.12, 72));
  g.add(box(34, 0.24, 56, lawn, -122, 0.12, -50));
  g.add(box(34, 0.24, 56, lawn, -122, 0.12, 50));
  const cypGeos = [];
  for (let i = 0; i < 34; i++) {
    const row = i % 2 ? -1 : 1;
    const cx = i < 20 ? -60 + (i >> 1) * 13 : -128 + ((i - 20) >> 1) * 9;
    const cz = i < 20 ? row * 70 : row * (34 + ((i - 20) % 4) * 12);
    const c = new THREE.ConeGeometry(1.5, 9, 6);
    c.translate(cx, 4.5, cz);
    cypGeos.push(c);
  }
  const cyps = new THREE.Mesh(mergeGeometries(cypGeos), lambert(0x2f4a28));
  cyps.castShadow = true;
  g.add(cyps);
  return g;
}

// Radisson Blu Iveria — the real 1967 slab: a white eggcrate balcony lattice
// (open ex-apartment balconies) over a recessed blue-glass core, slender
// proportions, white end walls, rooftop sign. Built as real geometry the way
// the Parliament colonnade was; no usable frontal photo exists on Commons.
function radisson() {
  const g = new THREE.Group();
  const WHITE_L = lambert(0xf0efe9);
  const FLOORS = 18;
  const FH = 3.6; // floor pitch of the lattice
  const H0 = 6; // lattice starts above the lobby
  const W = 30;
  const D = 15.5;
  const HTOT = FLOORS * FH;

  // recessed blue-glass core
  const glassCore = new THREE.MeshStandardMaterial({ color: 0x3a607a, roughness: 0.18, metalness: 0.5 });
  g.add(box(W, HTOT + 2, D, glassCore, 0, H0 + HTOT / 2, 0));

  // white eggcrate lattice on both long faces
  const cells = [];
  const BAYS = 8;
  for (const s of [-1, 1]) {
    const zf = s * (D / 2 + 0.6);
    for (let f = 0; f <= FLOORS; f++) {
      const slab = new THREE.BoxGeometry(W + 1.4, 0.38, 1.7);
      slab.translate(0, H0 + f * FH, zf);
      cells.push(slab);
    }
    for (let b = 0; b <= BAYS; b++) {
      const fin = new THREE.BoxGeometry(0.32, HTOT, 1.35);
      fin.translate(-W / 2 + b * (W / BAYS), H0 + HTOT / 2, zf);
      cells.push(fin);
    }
  }
  const lattice = new THREE.Mesh(mergeGeometries(cells), WHITE_L);
  lattice.castShadow = true;
  g.add(lattice);

  // solid white end walls with a thin window slit
  for (const s of [-1, 1]) {
    g.add(box(2.4, HTOT + 2, D + 1.6, WHITE_L, s * (W / 2 + 1), H0 + HTOT / 2, 0));
    g.add(box(0.4, HTOT, 3.4, glassCore, s * (W / 2 + 2.3), H0 + HTOT / 2, 0));
  }

  // crown + rooftop sign, glass lobby podium
  g.add(box(W + 3.5, 2.4, D + 3.5, WHITE_L, 0, H0 + HTOT + 1.2, 0));
  // FrontSide + a flipped twin so the text reads correctly from both sides
  // (a single DoubleSide plane mirrors the lettering on its back face).
  const signMat = new THREE.MeshBasicMaterial({ map: label('RADISSON BLU', 512, 76, '#10305c', '#ffffff'), side: THREE.FrontSide });
  for (const s of [1, -1]) {
    const sign = new THREE.Mesh(new THREE.PlaneGeometry(26, 3.8), signMat);
    sign.position.set(0, H0 + HTOT + 4.6, s * 0.05);
    sign.rotation.y = s === 1 ? 0 : Math.PI;
    g.add(sign);
  }
  g.add(box(W + 6, 6, D + 6, new THREE.MeshStandardMaterial({ color: 0x2e4356, roughness: 0.25, metalness: 0.45 }), 0, 3, 0));
  return g;
}

function gridTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = '#a8bcc9';
  g.fillRect(0, 0, 128, 128);
  g.fillStyle = 'rgba(255,255,255,0.85)';
  for (let i = 0; i < 128; i += 16) {
    g.fillRect(i, 0, 3, 128);
    g.fillRect(0, i, 128, 3);
  }
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(6, 8);
  return t;
}

function peaceBridge() {
  const g = new THREE.Group();
  g.add(box(150, 1.2, 7, STEEL, 0, 8, 0)); // deck over the river
  const shellGeo = new THREE.CylinderGeometry(11, 11, 140, 18, 1, true, 0, Math.PI);
  const shell = new THREE.Mesh(
    shellGeo,
    new THREE.MeshLambertMaterial({ color: 0xdfeaf2, transparent: true, opacity: 0.55, side: THREE.DoubleSide })
  );
  shell.rotation.z = Math.PI / 2;
  shell.rotation.y = Math.PI / 2;
  shell.position.y = 12;
  g.add(shell);
  for (let x = -66; x <= 66; x += 12) {
    const rib = new THREE.Mesh(new THREE.TorusGeometry(11, 0.32, 6, 12, Math.PI), STEEL);
    rib.rotation.y = Math.PI / 2;
    rib.position.set(x, 12, 0);
    g.add(rib);
  }
  return g;
}

function chronicle() {
  const g = new THREE.Group();
  g.add(box(64, 5, 46, lambert(0x8d857a), 0, 2.5, 0));
  const bronze = lambert(0x4a4238);
  for (let i = 0; i < 4; i++) {
    for (let j = 0; j < 4; j++) {
      const h = 26 + ((i * 7 + j * 5) % 9);
      g.add(cyl(2.6, 3.1, h, bronze, -21 + i * 14, 5 + h / 2, -15 + j * 10, 8));
    }
  }
  return g;
}

function mcdonalds() {
  const g = new THREE.Group();
  // real Marjanishvili McDonald's cuts (Commons, M. Fieber CC BY-SA 2.0):
  // sandstone wall, golden-arches box, vertical Georgian მაკდონალდსი sign
  const wallMat = lambert(0xd9d2c6);
  const mMat = new THREE.MeshBasicMaterial({ map: label('M', 128, 128, '#c8102e', '#ffcc00', 110), side: THREE.DoubleSide });
  const signMat = new THREE.MeshBasicMaterial({ color: 0xc8102e, side: THREE.DoubleSide });
  try {
    const tl = new THREE.TextureLoader();
    const hook = (file, mat, rx, ry) =>
      tl.load(file, (t) => {
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.anisotropy = 8;
        if (rx) t.repeat.set(rx, ry);
        mat.map = t;
        mat.color.set(0xffffff);
        mat.needsUpdate = true;
      });
    hook('textures/facades/mcd_stone.jpg', wallMat, 5, 0.92);
    hook('textures/facades/mcd_arches.jpg', mMat);
    hook('textures/facades/mcd_sign.jpg', signMat);
  } catch {
    /* Node OBJ export — plain tints remain */
  }
  g.add(box(15, 5.5, 12, wallMat, 0, 2.75, 0));
  g.add(box(15.8, 1.4, 12.8, lambert(0x8c2f24), 0, 6.2, 0)); // red fascia
  const m = new THREE.Mesh(new THREE.PlaneGeometry(4.6, 4.6), mMat);
  m.position.set(0, 11.5, 0);
  g.add(m);
  g.add(cyl(0.3, 0.3, 9, STEEL, 0, 4.5, 0, 6));
  const sign = new THREE.Mesh(new THREE.PlaneGeometry(1.3, 7.2), signMat);
  sign.position.set(7.9, 4.6, 5.6);
  sign.rotation.y = Math.PI / 2;
  g.add(sign);
  return g;
}

// Tbilisi Opera — Moorish revival, striped cream/terracotta walls on Rustaveli.
function opera() {
  const g = new THREE.Group();
  // canvas stripes stay as the offline fallback; the real photo of the
  // Moorish facade (Commons, V. Shioshvili CC BY-SA 4.0) swaps in on load
  const walls = new THREE.MeshLambertMaterial({ color: 0xffffff, map: makeStripeTexture() });
  const archMat = new THREE.MeshLambertMaterial({ color: 0xe8ddc2, side: THREE.DoubleSide });
  try {
    const tl = new THREE.TextureLoader();
    tl.load('textures/facades/opera_stripes.jpg', (t) => {
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.colorSpace = THREE.SRGBColorSpace;
      t.anisotropy = 8;
      t.repeat.set(52 / 6, 16 / 6); // photo tile spans 2 bays x 2 floors (6 m)
      walls.map = t;
      walls.needsUpdate = true;
    });
    tl.load('textures/facades/opera_arch.jpg', (t) => {
      t.colorSpace = THREE.SRGBColorSpace;
      t.anisotropy = 8;
      archMat.map = t;
      archMat.color.set(0xffffff);
      archMat.needsUpdate = true;
    });
  } catch {
    /* Node OBJ export — canvas stripes remain */
  }
  g.add(box(52, 16, 26, walls, 0, 8, 0));
  g.add(box(20, 19, 30, walls, 0, 9.5, 0)); // taller entrance block
  g.add(box(54, 2, 28, lambert(0x7a4a38), 0, 17, 0)); // cornice
  for (const s of [-1, 1]) g.add(cyl(2.6, 2.6, 21, walls, s * 24, 10.5, 11, 8)); // corner turrets
  // the real carved entrance arch as a decal on both long faces
  for (const s of [-1, 1]) {
    const arch = new THREE.Mesh(new THREE.PlaneGeometry(12, 18.5), archMat);
    arch.position.set(0, 9.4, s * 15.15);
    if (s < 0) arch.rotation.y = Math.PI;
    g.add(arch);
  }
  return g;
}

function makeStripeTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  for (let y = 0; y < 64; y += 16) {
    g.fillStyle = y % 32 ? '#b3664a' : '#e8ddc2';
    g.fillRect(0, y, 64, 16);
  }
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(6, 3);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

// Parliament of Georgia — the colonnaded front on Rustaveli.
// Parliament of Georgia — the Rustaveli front rebuilt from a real photo:
// the giant arched colonnade, sculpted pediment and tuff stone are textures
// cut from the photograph (tools/fetch_facades.py → parl_*).
function parliament() {
  const g = new THREE.Group();
  const mats = { arcade: [], pediment: [], stone: [] };
  const reg = (kind, rx, ry, ox = 0, tint = 0xcfab72) => {
    const m = lambert(tint);
    mats[kind].push({ m, rx, ry, ox });
    return m;
  };
  try {
    const tl = new THREE.TextureLoader();
    const hook = (file, kind) =>
      tl.load(file, (t) => {
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.anisotropy = 8;
        for (const { m, rx, ry, ox } of mats[kind]) {
          const c = t.clone();
          c.needsUpdate = true;
          c.repeat.set(rx, ry);
          c.offset.set(ox, 0);
          m.map = c;
          m.color.set(kind === 'stone' ? 0xf1e3c8 : 0xffffff); // keep tuff warmth
          m.needsUpdate = true;
        }
      });
    hook('textures/facades/parl_arcade.jpg', 'arcade');
    hook('textures/facades/parl_pediment.jpg', 'pediment');
    hook('textures/facades/parl_stone.jpg', 'stone');
  } catch {
    /* Node OBJ export — plain tuff tints */
  }
  const stone = (rx, ry) => reg('stone', rx, ry);
  const sBox = (w, h, d, x, y, z) => box(w, h, d, stone(Math.max(w, d) / 8, h / 12), x, y, z);

  // stepped stylobate up from the avenue
  g.add(sBox(104, 2.2, 58, 0, 1.1, 0));
  g.add(sBox(100, 2.2, 54, 0, 3.3, 1));
  g.add(sBox(96, 2.2, 50, 0, 5.5, 2));

  // main block with the tuff walls (deep rear wings like the real complex)
  g.add(sBox(94, 21, 58, 0, 17, -11));
  g.add(sBox(98, 2.6, 62, 0, 28.6, -11)); // crowning cornice
  g.add(box(94, 1.4, 58, lambert(0x7d776c), 0, 30.2, -11)); // flat roof

  // the famous giant arcade along Rustaveli: REAL free-standing columns in
  // front of a recessed wall that keeps the photo arches behind them
  const backWall = new THREE.Mesh(
    new THREE.BoxGeometry(90, 20, 2),
    [stone(1, 1), stone(1, 1), stone(8, 0.3), stone(8, 0.3), reg('arcade', 14, 1), stone(12, 2)]
  );
  backWall.position.set(0, 16.6, 16.6);
  backWall.receiveShadow = true;
  g.add(backWall);
  const colGeos = [];
  for (let i = 0; i < 15; i++) {
    const cx = -45 + 6.43 * i;
    const shaft = new THREE.CylinderGeometry(0.95, 1.1, 15.6, 10);
    shaft.translate(cx, 14.4, 21);
    const base = new THREE.BoxGeometry(2.8, 1.2, 2.8);
    base.translate(cx, 7, 21);
    const cap = new THREE.BoxGeometry(2.6, 1, 2.6);
    cap.translate(cx, 22.6, 21);
    colGeos.push(shaft.toNonIndexed(), base.toNonIndexed(), cap.toNonIndexed());
  }
  const columns = new THREE.Mesh(mergeGeometries(colGeos), stone(0.35, 0.5));
  columns.castShadow = true;
  g.add(columns);
  g.add(sBox(92, 3.4, 6.5, 0, 25, 19.6)); // entablature over the colonnade

  // central bay rising behind the colonnade to carry the pediment
  g.add(sBox(36, 23, 3, 0, 18.1, 18));
  const pedShape = new THREE.Shape();
  pedShape.moveTo(-19, 0);
  pedShape.lineTo(19, 0);
  pedShape.lineTo(0, 7.2);
  pedShape.closePath();
  const pedGeo = new THREE.ExtrudeGeometry(pedShape, { depth: 2.6, bevelEnabled: false });
  const pedMat = reg('pediment', 1 / 38, 1 / 7.2, 0.5);
  const ped = new THREE.Mesh(pedGeo, pedMat);
  ped.position.set(0, 29.6, 20.6);
  ped.castShadow = true;
  g.add(ped);

  // flags over the pediment
  const flagC = document.createElement('canvas');
  flagC.width = 64;
  flagC.height = 40;
  const fg = flagC.getContext('2d');
  fg.fillStyle = '#f4f2ec';
  fg.fillRect(0, 0, 64, 40);
  fg.fillStyle = '#c8342c';
  fg.fillRect(27, 0, 10, 40);
  fg.fillRect(0, 15, 64, 10);
  for (const [cx, cy] of [[12, 7], [50, 7], [12, 31], [50, 31]]) {
    fg.fillRect(cx - 4, cy - 1.5, 8, 3);
    fg.fillRect(cx - 1.5, cy - 4, 3, 8);
  }
  const flagT = new THREE.CanvasTexture(flagC);
  flagT.colorSpace = THREE.SRGBColorSpace;
  for (const fx of [-8, 0, 8]) {
    g.add(cyl(0.09, 0.12, 7, STEEL, fx, 39.9, 20, 5));
    const fl = new THREE.Mesh(new THREE.PlaneGeometry(3.4, 2.1), new THREE.MeshBasicMaterial({ map: flagT, side: THREE.DoubleSide }));
    fl.position.set(fx + 1.75, 42, 20);
    g.add(fl);
  }
  return g;
}

// Dinamo Arena — the big oval bowl.
function dinamo() {
  const g = new THREE.Group();
  const bowl = new THREE.Mesh(new THREE.TorusGeometry(82, 20, 8, 26), lambert(0xcfc8ba));
  bowl.rotation.x = Math.PI / 2;
  bowl.scale.set(1.15, 1, 1);
  bowl.position.y = 12;
  bowl.castShadow = true;
  g.add(bowl);
  const pitch = new THREE.Mesh(new THREE.CircleGeometry(62, 24), lambert(0x3f7a3c));
  pitch.rotation.x = -Math.PI / 2;
  pitch.scale.x = 1.2;
  pitch.position.y = 2.5;
  g.add(pitch);
  for (let i = 0; i < 4; i++) {
    const a = (i / 4) * Math.PI * 2 + Math.PI / 4;
    g.add(cyl(0.9, 1.2, 46, STEEL, Math.cos(a) * 105, 23, Math.sin(a) * 90, 6));
  }
  return g;
}

// Tbilisi State Concert Hall (Philharmonic, 1971, renovated): glazed round
// lobby behind dark slender columns, projecting white terrace ring carrying
// the venue sign, and the flared teal curtain-wall drum above — the drum
// glazing is a real photo tile (tools/fetch_facades.py → phil_glass, photo
// by Max Benidze CC BY-SA 2.0). +z faces the Melikishvili/Kostava junction.
function philharmonic() {
  const g = new THREE.Group();
  const SEG = 48;
  const DARK = lambert(0x2f343a);
  const SILVER = lambert(0xdadfe0);
  const CONCRETE = lambert(0xd3cfc6);

  // teal curtain-wall drum material — photo texture swapped in when loaded
  const drumMat = new THREE.MeshStandardMaterial({ color: 0x7fa3b8, roughness: 0.35, metalness: 0.2 });
  const clerMat = new THREE.MeshStandardMaterial({ color: 0x8fa9ba, roughness: 0.4, metalness: 0.15 });
  const lobbyMat = new THREE.MeshStandardMaterial({ color: 0xa9c2cf, roughness: 0.4, metalness: 0.15 });
  try {
    const tl = new THREE.TextureLoader();
    tl.load('textures/facades/phil_glass.jpg', (t) => {
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.colorSpace = THREE.SRGBColorSpace;
      t.anisotropy = 8;
      for (const [m, rx, ry] of [[drumMat, 20, 3], [clerMat, 20, 1], [lobbyMat, 16, 2]]) {
        const c = t.clone();
        c.needsUpdate = true;
        c.repeat.set(rx, ry);
        m.map = c;
        m.color.set(0xffffff);
        m.needsUpdate = true;
      }
    });
  } catch {
    /* Node OBJ export — plain glass tints */
  }

  // plaza podium + steps toward the junction
  g.add(cyl(33, 34.5, 1.2, lambert(0x9a968e), 0, 0.6, 0, SEG));
  g.add(box(26, 0.55, 10, lambert(0xa8a49b), 0, 0.85, 32));
  g.add(box(28, 0.55, 6, lambert(0xa8a49b), 0, 0.3, 36));

  // glazed round lobby recessed behind 22 dark slender columns
  g.add(cyl(22.5, 22.5, 7.6, lobbyMat, 0, 5, 0, SEG));
  const colGeos = [];
  for (let i = 0; i < 22; i++) {
    const a = (i / 22) * Math.PI * 2;
    const c = new THREE.CylinderGeometry(0.42, 0.46, 8.2, 8);
    c.translate(Math.cos(a) * 24.2, 5.3, Math.sin(a) * 24.2);
    colGeos.push(c.toNonIndexed());
  }
  const cols = new THREE.Mesh(mergeGeometries(colGeos), DARK);
  cols.castShadow = true;
  g.add(cols);

  // projecting white terrace ring; its fascia carries the venue name
  g.add(cyl(26.8, 26.8, 1.1, WHITE, 0, 10, 0, SEG));
  const signC = document.createElement('canvas');
  signC.width = 2048;
  signC.height = 96;
  const sg = signC.getContext('2d');
  sg.fillStyle = '#22272d';
  sg.fillRect(0, 0, 2048, 96);
  sg.fillStyle = '#f2f3f4';
  sg.font = 'bold 44px sans-serif';
  sg.textAlign = 'center';
  sg.textBaseline = 'middle';
  sg.fillText('თბილისის საკონცერტო დარბაზი', 512, 50);
  sg.font = 'bold 40px sans-serif';
  sg.fillText('TBILISI CONCERT HALL', 1536, 50);
  const signT = new THREE.CanvasTexture(signC);
  signT.colorSpace = THREE.SRGBColorSpace;
  signT.wrapS = THREE.RepeatWrapping;
  signT.repeat.set(2, 1);
  signT.anisotropy = 8;
  const signBand = new THREE.Mesh(
    new THREE.CylinderGeometry(26.9, 26.9, 1.9, SEG, 1, true),
    new THREE.MeshLambertMaterial({ map: signT, side: THREE.DoubleSide })
  );
  signBand.position.y = 11.5;
  g.add(signBand);
  g.add(cyl(26.8, 26.8, 0.6, WHITE, 0, 12.7, 0, SEG));
  // open terrace walkway behind the parapet, then the clerestory row
  g.add(cyl(23.6, 23.6, 2.6, clerMat, 0, 14.2, 0, SEG));
  g.add(cyl(24.6, 24.6, 0.9, SILVER, 0, 15.8, 0, SEG));

  // the flared teal glass drum (top wider than bottom, like the real hall)
  const drum = new THREE.Mesh(new THREE.CylinderGeometry(27, 24.6, 13, SEG), drumMat);
  drum.position.y = 22.7;
  drum.castShadow = true;
  g.add(drum);
  // big LED screen hung left of the entrance bearing
  const led = new THREE.Mesh(
    new THREE.CylinderGeometry(27.4, 27.4, 6.5, 12, 1, true, Math.PI * 0.62, 0.52),
    new THREE.MeshBasicMaterial({ color: 0x0b0d10, side: THREE.DoubleSide })
  );
  led.position.y = 23.2;
  g.add(led);

  // smooth silver roof rim + shallow cap, service masts
  g.add(cyl(27.9, 27.6, 1.7, SILVER, 0, 30, 0, SEG));
  g.add(cyl(9, 27.2, 2.7, SILVER, 0, 32.1, 0, SEG));
  g.add(cyl(0.12, 0.16, 6, STEEL, 4, 36, -3, 6));
  g.add(cyl(0.12, 0.16, 4.5, STEEL, -7, 35.2, 2, 6));

  // rear stage block and lower wings stretching up the parcel (NW)
  g.add(box(30, 16, 22, CONCRETE, 0, 9.2, -32));
  g.add(box(48, 10.5, 26, CONCRETE, -5, 6.4, -52));
  g.add(box(30, 7.5, 18, CONCRETE, 8, 4.9, -70));
  g.add(box(52, 1, 30, lambert(0x8f8b83), -3, 12, -52)); // wing roof slabs
  g.add(box(32, 0.8, 20, lambert(0x8f8b83), 8, 9, -70));

  // the bronze Muse and a fountain basin on the entrance plaza
  const muse = new THREE.Group();
  const PATINA = lambert(0x5f8f7a);
  muse.add(box(2.6, 3.8, 2.6, lambert(0x555a5f), 0, 1.9, 0));
  muse.add(cyl(0.55, 0.95, 4.6, PATINA, 0, 6, 0, 8));
  const head = new THREE.Mesh(new THREE.SphereGeometry(0.55, 8, 6), PATINA);
  head.position.set(0, 8.7, 0);
  muse.add(head);
  for (const s of [-1, 1]) {
    const arm = cyl(0.16, 0.2, 2.6, PATINA, s * 1.1, 7.9, 0, 6);
    arm.rotation.z = s * 0.9;
    muse.add(arm);
  }
  muse.position.set(14, 1.2, 31);
  g.add(muse);
  const basin = cyl(5.5, 6, 0.7, lambert(0x8d9296), -10, 1.55, 30, 20);
  g.add(basin);
  const water = new THREE.Mesh(
    new THREE.CircleGeometry(5.2, 20),
    new THREE.MeshStandardMaterial({ color: 0x3f6f8f, roughness: 0.15, metalness: 0.3 })
  );
  water.rotation.x = -Math.PI / 2;
  water.position.set(-10, 1.95, 30);
  g.add(water);
  return g;
}

// Batumi: Alphabet Tower — lattice cone with the glass sphere on top.
function alphabetTower() {
  const g = new THREE.Group();
  const lattice = new THREE.MeshLambertMaterial({ color: 0xcfd6dc, map: gridTexture(), transparent: true, opacity: 0.85, side: THREE.DoubleSide });
  const cone = new THREE.Mesh(new THREE.CylinderGeometry(6, 17, 100, 14, 1, true), lattice);
  cone.position.y = 50;
  g.add(cone);
  const ball = new THREE.Mesh(
    new THREE.SphereGeometry(10, 14, 10),
    new THREE.MeshLambertMaterial({ color: 0xbcd6e8, transparent: true, opacity: 0.75 })
  );
  ball.position.y = 108;
  ball.castShadow = true;
  g.add(ball);
  g.add(cyl(0.5, 0.5, 14, STEEL, 0, 122, 0, 6));
  return g;
}

// Batumi: the boulevard Ferris wheel.
function ferrisWheel() {
  const g = new THREE.Group();
  const R = 26;
  const rim = new THREE.Mesh(new THREE.TorusGeometry(R, 0.9, 8, 28), STEEL);
  rim.position.y = R + 6;
  g.add(rim);
  for (let i = 0; i < 12; i++) {
    const a = (i / 12) * Math.PI * 2;
    const spoke = cyl(0.35, 0.35, R * 2, STEEL, 0, R + 6, 0, 6);
    spoke.rotation.z = a;
    g.add(spoke);
    const cab = box(2.4, 2.8, 2.4, lambert(i % 2 ? 0xb43a2a : 0x3c6db0), Math.cos(a) * R, R + 6 + Math.sin(a) * R - 1.4, 0);
    g.add(cab);
  }
  for (const s of [-1, 1]) {
    const leg = cyl(1.1, 1.6, R + 8, STEEL, s * 10, (R + 6) / 2, 0, 8);
    leg.rotation.z = s * 0.32;
    g.add(leg);
  }
  return g;
}

function ship(kind) {
  const g = new THREE.Group();
  const hull = lambert(kind === 'tanker' ? 0x7a2f26 : 0x2e4560);
  g.add(box(150, 12, 24, hull, 0, 2, 0));
  const bow = box(24, 12, 24, hull, 87, 2, 0);
  bow.scale.z = 0.55;
  g.add(bow);
  g.add(box(18, 16, 20, WHITE, -58, 16, 0)); // superstructure
  g.add(cyl(2.2, 2.6, 8, lambert(0xc8b23c), -58, 28, 0, 8));
  if (kind === 'container') {
    const colors = [0xb0453a, 0x3f7a4d, 0x3c5f8f, 0xb08430];
    for (let r = 0; r < 5; r++) {
      for (let s = 0; s < 2; s++) {
        g.add(box(22, 5, 9, lambert(colors[(r + s) % 4]), -30 + r * 24, 10.5 + (s % 2) * 0, -5 + s * 10));
      }
    }
  } else {
    g.add(cyl(1, 1, 26, lambert(0x888078), 10, 9, 0, 6));
    g.add(box(120, 1.5, 18, lambert(0x6e5a4c), 5, 8.5, 0));
  }
  return g;
}

// Individual model builders, also used by tools/export-obj.mjs to write .obj files.
// Shared helper: Lambert material that swaps in a real facade photo on load
// (tools/fetch_facades.py cuts; tint stays as the offline/Node fallback).
function photoMat(file, rx, ry, tint = 0xcfc4ae) {
  const m = lambert(tint);
  try {
    new THREE.TextureLoader().load(`textures/facades/${file}`, (t) => {
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.colorSpace = THREE.SRGBColorSpace;
      t.anisotropy = 8;
      t.repeat.set(rx, ry);
      m.map = t;
      m.color.set(0xffffff);
      m.needsUpdate = true;
    });
  } catch {
    /* Node OBJ export — tint remains */
  }
  return m;
}

const TILE_ROOF = lambert(0x9a5a44); // Georgian red clay tile

// module-level gold cross (sameba has its own local copy of the same shape)
function goldCross(x, y, z, s = 1) {
  const grp = new THREE.Group();
  grp.add(cyl(0.16 * s, 0.16 * s, 3 * s, GOLD, 0, 1.5 * s, 0, 6));
  grp.add(box(1.7 * s, 0.34 * s, 0.34 * s, GOLD, 0, 2 * s, 0));
  grp.position.set(x, y, z);
  return grp;
}

// Narikala fortress — crenellated brick walls + towers on the old-town ridge
// (way 9820020, walls run along bearing ~130; real photo brick).
function narikala() {
  const g = new THREE.Group();
  const brick = lambert(0x9a7a5c); // flat masonry tint: photo tiles zebra on thin boxes
  const brickT = photoMat('oldbrick.jpg', 6, 4, 0x9a6f52); // cylinders wrap cleanly
  const wall = (len, x, z, ry) => {
    const grp = new THREE.Group();
    grp.add(box(len, 9, 2.6, brick, 0, 4.5, 0));
    for (let i = 0; i < len / 4; i++) grp.add(box(1.9, 1.6, 2.8, brick, -len / 2 + 2 + i * 4, 9.8, 0)); // merlons
    grp.position.set(x, 0, z);
    grp.rotation.y = ry;
    g.add(grp);
    return grp;
  };
  wall(75, 0, 18, 0.12);
  wall(70, -6, -16, -0.18);
  wall(40, 42, 4, 1.35);
  wall(40, -46, 2, 1.5);
  for (const [tx, tz, r, h] of [[40, 20, 5.5, 14], [-42, 18, 5, 13], [40, -14, 5, 13], [-44, -14, 4.6, 12], [0, 20, 4.4, 12]]) {
    g.add(cyl(r, r + 0.6, h, brickT, tx, h / 2, tz, 10));
    g.add(cyl(r + 0.9, r + 0.9, 1.6, brickT, tx, h + 0.8, tz, 10)); // parapet ring
  }
  // St Nicholas church inside the walls (rebuilt 1997): cross plan + tent dome
  const stone = photoMat('sameba_stone.jpg', 3, 2, 0xd9c8a4);
  g.add(box(14, 7, 9, stone, 0, 3.5, 2));
  g.add(box(9, 7, 14, stone, 0, 3.5, 2));
  g.add(cyl(3.2, 3.2, 5, stone, 0, 9.5, 2, 12));
  g.add(cyl(0.2, 3.4, 4.4, TILE_ROOF, 0, 14.2, 2, 12));
  g.add(goldCross(0, 16.8, 2, 0.9));
  return g;
}

// Mtatsminda funicular upper station — the 3-arch loggia hall (way 25699467),
// real arcade photo on the city-facing front.
function funicularStation() {
  const g = new THREE.Group();
  const arcade = photoMat('funicular_arcade.jpg', 1, 1, 0xd9cdb4);
  const stone = lambert(0xd6cab0);
  g.add(box(26, 14, 15, stone, 0, 7, 0));
  const front = new THREE.Mesh(new THREE.PlaneGeometry(24, 13.2), arcade);
  front.position.set(0, 7.1, 7.6);
  g.add(front);
  g.add(box(28, 1.4, 17, lambert(0xbfb49a), 0, 14.7, 0)); // cornice
  g.add(box(12, 5, 9, stone, 0, 17.8, -2)); // upper machinery hall
  g.add(box(13, 0.9, 10, lambert(0xbfb49a), 0, 20.6, -2));
  return g;
}

// Metekhi church on its cliff (way 1064270003) — the Vakhtang Gorgasali
// statue beside it is now its own real-size builder (gorgasali()).
function metekhi() {
  const g = new THREE.Group();
  const stone = photoMat('sameba_stone.jpg', 3, 2, 0xd3c3a2);
  g.add(box(16, 8, 11, stone, 0, 4, 0));
  g.add(box(11, 8, 16, stone, 0, 4, 0));
  g.add(cyl(3.6, 3.6, 6.5, photoMat('sameba_arches.jpg', 2.2, 1, 0xcdbb97), 0, 11.2, 0, 12));
  g.add(cyl(0.2, 3.8, 5.2, TILE_ROOF, 0, 17, 0, 12));
  g.add(goldCross(0, 20.2, 0, 1));
  return g;
}

// Svetitskhoveli cathedral, Mtskheta (way 20593299) — elongated cross basilica
// + tall drum, inside the crenellated defensive wall (way 17883853).
function svetitskhoveli() {
  const g = new THREE.Group();
  const stone = photoMat('sameba_stone.jpg', 5, 2.4, 0xd9c8a4);
  const arch = photoMat('sameba_arches.jpg', 2.6, 1, 0xcdbb97);
  g.add(box(52, 13, 22, stone, 0, 6.5, 0)); // nave
  g.add(box(24, 15, 30, stone, 4, 7.5, 0)); // transept
  const gableRoof = (len, wid, rise, x, y, z) => {
    for (const s of [-1, 1]) {
      const p = box(len, 0.5, Math.hypot(wid / 2, rise) + 0.5, TILE_ROOF, 0, 0, 0);
      p.rotation.x = s * Math.atan2(rise, wid / 2);
      p.position.set(x, y + rise / 2, z + (s * wid) / 4);
      g.add(p);
    }
  };
  gableRoof(52, 22, 6, 0, 13, 0);
  g.add(cyl(6.2, 6.2, 9, arch, 4, 19, 0, 12)); // drum
  g.add(cyl(0.25, 6.6, 7.5, TILE_ROOF, 4, 27.2, 0, 12)); // cone
  g.add(goldCross(4, 31.8, 0, 1.2));
  // defensive wall with corner towers (flat tint — photo zebra-stripes thin boxes)
  const brick = lambert(0xa08163);
  const W = 120;
  const D = 118;
  for (const [w, d, x, z] of [[W, 2.2, 0, D / 2], [W, 2.2, 0, -D / 2], [2.2, D, W / 2, 0], [2.2, D, -W / 2, 0]]) {
    g.add(box(w, 6.5, d, brick, x, 3.25, z));
  }
  for (const [x, z] of [[W / 2, D / 2], [-W / 2, D / 2], [W / 2, -D / 2], [-W / 2, -D / 2]]) {
    g.add(cyl(4.4, 4.8, 9.5, brick, x, 4.75, z, 10));
    g.add(cyl(0.2, 5, 3.6, TILE_ROOF, x, 11.2, z, 10));
  }
  return g;
}

// Jvari monastery (rel/way 1497018777) — the 6th-c tetraconch on its hilltop
// over Mtskheta, weathered ashlar from the real photo, plus the ruined tower.
function jvari() {
  const g = new THREE.Group();
  const stone = photoMat('jvari_stone.jpg', 3, 2, 0xc4a982);
  g.add(box(21, 8, 15, stone, 0, 4, 0));
  g.add(box(15, 8, 21, stone, 0, 4, 0));
  for (const [ax, az] of [[10, 0], [-10, 0], [0, 10], [0, -10]]) {
    g.add(cyl(4.2, 4.2, 7.5, stone, ax * 0.72, 3.75, az * 0.72, 10)); // apses
    g.add(cyl(0.2, 4.4, 2.6, TILE_ROOF, ax * 0.72, 8.6, az * 0.72, 10));
  }
  g.add(cyl(4.6, 4.9, 6, stone, 0, 10.8, 0, 8)); // octagonal drum
  g.add(cyl(0.25, 5, 4.6, TILE_ROOF, 0, 16, 0, 8));
  g.add(goldCross(0, 19.4, 0, 1));
  g.add(box(9, 12, 8, lambert(0xa5876a), -16, 6, -2)); // ruined tower stub
  return g;
}

// Abanotubani sulfur baths — the brick beehive dome field by the gorge
// (Royal/Chreli/Erekle baths; real bath-wall brick).
function baths() {
  const g = new THREE.Group();
  const brick = photoMat('oldbrick.jpg', 4, 2, 0x9c7355);
  const domeMat = photoMat('oldbrick.jpg', 4, 2, 0xa87f5e);
  g.add(box(52, 4.5, 34, brick, 0, 2.25, 0)); // half-buried bath hall block
  const domes = [[-18, -8, 5.5], [-6, -9, 6.5], [7, -8, 5.8], [18, -6, 4.8], [-12, 6, 5], [2, 7, 6], [14, 7, 5.2]];
  for (const [dx, dz, r] of domes) {
    const dome = new THREE.Mesh(new THREE.SphereGeometry(r, 14, 9, 0, Math.PI * 2, 0, Math.PI / 2), domeMat);
    dome.position.set(dx, 4.5, dz);
    dome.castShadow = true;
    g.add(dome);
    g.add(cyl(1.1, 1.3, 1.8, brick, dx, 4.5 + r + 0.6, dz, 8)); // skylight lantern
  }
  return g;
}

// Tbilisi Marriott, Rustaveli (way 7108262) — white classical block with the
// curved corner, real facade band photo.
function marriott() {
  const g = new THREE.Group();
  const fac = photoMat('marriott_facade.jpg', 4.5, 3.2, 0xdfdad0);
  g.add(box(56, 25, 19, fac, 0, 12.5, 0));
  g.add(box(19, 25, 42, fac, 20, 12.5, -14));
  g.add(cyl(10.5, 10.5, 25, fac, 24, 12.5, 8, 16)); // curved corner
  g.add(box(58, 1.6, 21, lambert(0xc9c2b4), 0, 25.9, 0)); // cornice
  const sign = new THREE.Mesh(
    new THREE.PlaneGeometry(17, 1.8),
    new THREE.MeshBasicMaterial({ map: label('TBILISI MARRIOTT', 512, 54, '#efece4', '#4a4a4a'), side: THREE.DoubleSide })
  );
  sign.position.set(24, 15.5, 18.2);
  g.add(sign);
  return g;
}

// The Biltmore Tbilisi — the mirrored glass blade over Rustaveli (way 19590999).
function biltmore() {
  const g = new THREE.Group();
  // Real curtain-wall texture (same tile as the generic glass towers, one
  // repeat = 7 bays x 8 floors = 21 x 24 m): the old untextured mirror-teal
  // material rendered the tower as a featureless solid slab from any distance.
  const glass = photoMat('glass.jpg', 34 / 21, 104 / 24, 0x51707f);
  const WHITE_L = lambert(0xeceae4);
  g.add(box(34, 104, 21, glass, 0, 58, 0));
  g.add(box(3, 108, 23, WHITE_L, -18, 56, 0)); // white service edge
  g.add(box(35, 4, 22, WHITE_L, 0, 112, 0)); // crown
  g.add(box(40, 8, 26, lambert(0xd9cfc0), 0, 4, 0)); // stone podium (old IMELI front)
  return g;
}

// Sheraton Grand Metechi Palace — the stepped tan atrium block (way 20990671).
function sheratonMetechi() {
  const g = new THREE.Group();
  const tan = lambert(0xc9b394);
  const glassBand = new THREE.MeshStandardMaterial({ color: 0x3e5a66, roughness: 0.2, metalness: 0.5 });
  g.add(box(58, 12, 48, tan, 0, 6, 0));
  g.add(box(46, 10, 38, tan, 0, 17, -2));
  g.add(box(34, 9, 28, tan, 0, 26.5, -4));
  g.add(box(22, 8, 20, glassBand, 0, 35, -6)); // atrium crown
  for (const y of [12.6, 22.6, 31.4]) g.add(box(40, 1.1, 32, glassBand, 0, y, -3));
  return g;
}

// Radisson RED (node 11358652556) — compact dark block with the red brand bar.
function radissonRed() {
  const g = new THREE.Group();
  const dark = new THREE.MeshStandardMaterial({ color: 0x3c3f45, roughness: 0.35, metalness: 0.3 });
  g.add(box(26, 34, 18, dark, 0, 17, 0));
  const sign = new THREE.Mesh(
    new THREE.PlaneGeometry(12, 2.2),
    new THREE.MeshBasicMaterial({ map: label('RADISSON RED', 384, 60, '#c8102e', '#ffffff'), side: THREE.DoubleSide })
  );
  sign.position.set(0, 32, 9.2);
  g.add(sign);
  return g;
}

// --- statues (real monuments at documented sizes, coords from OSM) ----------
// Heights: Freedom Monument 35.6 m total (node 10905076907, 5.6 m gilded
// St George); Kartlis Deda 20 m (wikidata Q1147179 P2048); Gorgasali ~13 m
// figure over its stone pedestal; Saint Nino column 19 m (node 1560445093).

const BRONZE = lambert(0x5a4a38); // dark statue bronze
const PATINA = lambert(0x4f7a68); // weathered green bronze
const GRANITE = lambert(0x8e8a80);
const ALUMINUM = new THREE.MeshStandardMaterial({ color: 0xc9ccd1, metalness: 0.75, roughness: 0.35 });
// statues rebuilt 2026-08 (tools/build_statues2.py) carry a COLOR_0 patina
// mask (bronze high points / verdigris crevices) — white base + vertexColors
const BRONZE_V = new THREE.MeshStandardMaterial({ color: 0xfff6ea, metalness: 0.45, roughness: 0.55, vertexColors: true });

// Blender-sculpted statue figures (tools/build_statues.py → models/statues/):
// each GLB is normalized to height 1 with the base at y=0, so scale = the real
// figure height in meters. Equestrians face +x, standing figures face +z.
function statueGLB(file, mat, h, parent, y = 0, spots = [[0, 0, 0]]) {
  // mat = null keeps the GLB's own materials (photogrammetry scans with
  // baked photo textures — gorgasali/saakadze by Badri Gogia, CC-BY).
  try {
    newGltfLoader().load(`models/statues/${file}.glb`, (g2) => {
      g2.scene.updateMatrixWorld(true);
      for (const [x, z, ry] of spots) {
        const m = g2.scene.clone(true);
        m.traverse((o) => {
          if (o.isMesh) {
            if (mat) o.material = mat;
            o.castShadow = true;
          }
        });
        m.scale.setScalar(h);
        m.position.set(x, y, z);
        m.rotation.y = ry ?? 0;
        parent.add(m);
      }
    });
  } catch {
    /* Node OBJ export — sculpted figures are browser-only */
  }
}

// Freedom Monument, Liberty Square (way 1018836829, unveiled 2006): granite
// column with the 5.6 m gold St George slaying the dragon — 35.6 m overall.
function freedomMonument() {
  const g = new THREE.Group();
  const granite = lambert(0x7d7268);
  g.add(cyl(6.8, 7.4, 1.0, granite, 0, 0.5, 0, 20)); // stepped round base
  g.add(cyl(5.4, 6.2, 1.0, granite, 0, 1.5, 0, 20));
  g.add(cyl(3.2, 4.4, 2.6, granite, 0, 3.3, 0, 16)); // pedestal drum
  g.add(cyl(1.35, 1.8, 23.5, granite, 0, 16.35, 0, 16)); // the column
  g.add(cyl(2.2, 1.5, 1.2, granite, 0, 28.7, 0, 16)); // capital
  g.add(box(4.4, 0.7, 4.4, granite, 0, 29.65, 0)); // abacus platform
  // the 5.6 m gilded St George slaying the dragon, sculpted in Blender
  statueGLB('george', GOLD, 5.6, g, 30);
  return g;
}

// Kartlis Deda / Mother of Georgia on the Sololaki ridge (way 1063062536):
// the 20 m aluminum figure, sword in the right hand, wine bowl in the left.
function kartlisDeda() {
  const g = new THREE.Group();
  g.add(box(5.2, 1.4, 5.2, GRANITE, 0, 0.7, 0)); // ridge plinth
  // the 18.6 m aluminum figure: sword in the right hand, wine bowl in the left
  statueGLB('kartlis_deda', ALUMINUM, 18.6, g, 1.4);
  return g;
}

// Vakhtang Gorgasali equestrian by Metekhi (way 1062990549, Amashukeli 1967):
// the REAL monument — drone photogrammetry scan (Badri Gogia, CC-BY) with its
// photo-textured bronze and stone pedestal baked in.
function gorgasali() {
  const g = new THREE.Group();
  g.add(box(7.5, 2, 5.5, GRANITE, 0, 1, 0)); // cliff terrace slab
  statueGLB('gorgasali', null, 11, g, 2); // scan incl. pedestal → 13 m total
  return g;
}

// Shota Rustaveli at the metro square (way 1066637619, Merabishvili 1965):
// robed bronze poet on the red granite pedestal, ~9.5 m overall.
function rustaveliMonument() {
  const g = new THREE.Group();
  const redGranite = lambert(0x7e463a);
  g.add(box(4.6, 0.9, 4.6, redGranite, 0, 0.45, 0));
  g.add(box(2.7, 4.0, 2.7, redGranite, 0, 2.9, 0)); // pedestal
  statueGLB('rustaveli', BRONZE_V, 4.6, g, 4.9); // poet: cap, scroll, cloak
  return g;
}

// King David the Builder at the Dighomi city gate (node 1392061407,
// Berdzenishvili 1997): equestrian bronze on the tall stone pedestal, ~15 m.
function davidBuilder() {
  const g = new THREE.Group();
  g.add(box(8, 1.2, 6, GRANITE, 0, 0.6, 0));
  g.add(box(4.6, 5.4, 3.4, lambert(0x9a938a), 0, 3.9, 0)); // tall pedestal
  statueGLB('david', BRONZE_V, 8.4, g, 6.6); // ≈ 15 m overall
  return g;
}

// Saint Nino monument beside it (node 1560445093, height=19): the saint with
// her grapevine cross atop a slender stone column.
function saintNino() {
  const g = new THREE.Group();
  const stone = lambert(0xb3aa9c);
  g.add(cyl(2.6, 3.2, 1.2, stone, 0, 0.6, 0, 12));
  g.add(cyl(0.85, 1.25, 12.5, stone, 0, 7.45, 0, 12)); // column
  g.add(cyl(1.35, 0.95, 0.8, stone, 0, 14.1, 0, 12)); // capital
  statueGLB('nino', BRONZE_V, 4.5, g, 14.5); // saint, cross raised aloft → 19 m
  return g;
}

// Giorgi Saakadze equestrian on his square in Didube (node 1111316631):
// the REAL monument — textured photogrammetry scan (Badri Gogia, CC-BY),
// pedestal base baked into the scan.
function saakadze() {
  const g = new THREE.Group();
  g.add(box(6, 1, 4.5, GRANITE, 0, 0.5, 0)); // square plinth slab
  statueGLB('saakadze', null, 9, g, 1); // scan incl. its stone pedestal
  return g;
}

// Vazha-Pshavela at the head of his avenue (node 1213786131, 1961).
function vazhaPshavela() {
  const g = new THREE.Group();
  g.add(box(4.4, 0.8, 4.4, GRANITE, 0, 0.4, 0));
  g.add(box(2.6, 2.8, 2.6, GRANITE, 0, 2.2, 0));
  statueGLB('vazha', BRONZE_V, 4.5, g, 3.6); // chokha + burka, offered palm
  return g;
}

// Ilia Chavchavadze & Akaki Tsereteli in front of Gymnasium #1 on Rustaveli
// (way 1066626116, 1958) — the famous pair on one low granite base.
function writersPair() {
  const g = new THREE.Group();
  g.add(box(6.2, 1.8, 3.2, GRANITE, 0, 0.9, 0));
  statueGLB('writer_ilia', BRONZE_V, 3.5, g, 1.8, [[-1.25, 0, 0.08]]);
  statueGLB('writer_akaki', BRONZE_V, 3.5, g, 1.8, [[1.25, 0, -0.08]]);
  return g;
}

// King Pharnavaz I near Europe Square (node 5579769621).
function pharnavaz() {
  const g = new THREE.Group();
  g.add(box(4, 0.8, 4, GRANITE, 0, 0.4, 0));
  g.add(box(2.4, 2.6, 2.4, GRANITE, 0, 2.1, 0));
  statueGLB('pharnavaz', BRONZE_V, 5.4, g, 3.4); // alphabet scroll at the chest
  return g;
}

export const BUILDERS = { tvTower, sameba, radisson, peaceBridge, chronicle, mcdonalds, alphabetTower, ferrisWheel, ship, opera, parliament, dinamo, philharmonic, narikala, funicularStation, metekhi, svetitskhoveli, jvari, baths, marriott, biltmore, sheratonMetechi, radissonRed, freedomMonument, kartlisDeda, gorgasali, rustaveliMonument, davidBuilder, saintNino, saakadze, vazhaPshavela, writersPair, pharnavaz };

// --- placement --------------------------------------------------------------

// Tbilisi Public Service Hall (იუსტიციის სახლი) — Fuksas, 2012, on the left
// bank beside Dedaena Park. Blender: tools/build_justice_house.py. The model
// is authored in the game's own frame on OSM way 1066392731's footprint, so it
// is placed with no yaw; the eleven petals are what carry the silhouette.
function justiceHouse() {
  const g = new THREE.Group();
  newGltfLoader().load('models/justice_house.glb', (gltf) => {
    gltf.scene.traverse((o) => {
      if (!o.isMesh) return;
      o.castShadow = true;
      o.receiveShadow = true;
      const ms = Array.isArray(o.material) ? o.material : [o.material];
      for (const m of ms) {
        // the glazing is authored transparent; keep it from sorting in front
        // of the petals it sits under
        if (m && m.transparent) m.depthWrite = false;
        if (m) m.envMapIntensity = 0.6;
      }
    });
    g.add(gltf.scene);
  }, undefined, (e) => console.warn('justice house failed:', e?.message ?? e));
  return g;
}

export function addLandmarks(citymap) {
  const { meta, group } = citymap;
  const toLocal = (lat, lon) => ({
    x: -(lon - meta.origin.lon) * meta.mlon,
    z: (lat - meta.origin.lat) * meta.mlat,
  });
  const place = (obj, lat, lon, yaw = 0, yOff = 0, name = null) => {
    const p = toLocal(lat, lon);
    obj.position.set(p.x, citymap.heightAt(p.x, p.z) + yOff, p.z);
    obj.rotation.y = yaw;
    group.add(obj);
    // a named landmark is shootable/destructible; statues stay monuments
    if (name) citymap.registerLandmark(obj, name);
  };

  // Coordinates below are OSM footprint centroids (ways/relations noted), not
  // eyeballed — audit 2026-07-28 found several off by 100 m–3 km.
  place(tvTower(), 41.69575, 44.78499, 0, 0, 'ტელეანძა'); // way 437940450, Mtatsminda ridge
  // way 585352693; long axis E–W, grand stairs face west → model stairs (−x) flipped
  place(sameba(), 41.69754, 44.81654, 3.14, 0, 'სამების საკათედრო ტაძარი');
  // way 1299930496 — Rose Revolution Sq, WEST bank of the Mtkvari; slab along 135°
  place(radisson(), 41.70392, 44.79369, -0.78, 0, 'რადისონ ბლუ ივერია');
  // way 707446533 — hill above the Tbilisi Sea; pillar rows along 169°
  place(chronicle(), 41.77056, 44.81026, -1.38, 0, 'საქართველოს მატიანე');
  place(alphabetTower(), 41.65596, 41.63934, 0, 0, 'ანბანის კოშკი'); // way 405852444
  place(ferrisWheel(), 41.6541, 41.63674, 1.2, 0, 'ეშმაკის ბორბალი'); // node 6822566652, boulevard pier
  // way 59151604; long axis along Rustaveli bearing 47°
  place(opera(), 41.70112, 44.79626, 0.75, 0, 'ოპერისა და ბალეტის თეატრი');
  // way 1066392731 — footprint centroid; the GLB already carries the bearing
  place(justiceHouse(), 41.699024, 44.806656, 0, 0, 'იუსტიციის სახლი');
  // OSM relation 1020718 (საქართველოს პარლამენტი): the complex sits SW of
  // Rustaveli, colonnade facing NE over the avenue
  place(parliament(), 41.69667, 44.79805, -1.1, 0, 'საქართველოს პარლამენტი');
  place(dinamo(), 41.72285, 44.78966, -0.88, 0, 'დინამო არენა'); // rel 17823425, long axis 141°
  // OSM relation 18129586: drum at the SE of the parcel, entrance facing
  // the Melikishvili/Kostava junction (bearing ~123° → yaw −2.15)
  place(philharmonic(), 41.70731, 44.78601, -2.15, 0, 'ფილარმონია');
  // 2026-07-29 wave: every known building real (OSM ids in EXCLUSIONS/comments)
  place(narikala(), 41.68787, 44.80894, -0.7, 0, 'ნარიყალას ციხე'); // way 9820020, walls along 130°
  place(funicularStation(), 41.69489, 44.78652, -1.48, 0, 'ფუნიკულიორის სადგური'); // way 25699467, arcade faces the city
  place(metekhi(), 41.69015, 44.8113, 0, 0, 'მეტეხის ტაძარი'); // way 1064270003
  place(svetitskhoveli(), 41.84227, 44.72088, 0, 0, 'სვეტიცხოველი'); // way 20593299, Mtskheta
  place(jvari(), 41.83838, 44.73355, 0, 0, 'ჯვრის მონასტერი'); // way 1497018777, hilltop
  place(baths(), 41.6876, 44.8111, 0, 0, 'აბანოთუბანი'); // Abanotubani dome field
  place(marriott(), 41.69916, 44.7983, 0.7, 0, 'მარიოტი'); // way 7108262, Rustaveli 13
  place(biltmore(), 41.70245, 44.79467, 0.75, 0, 'ბილტმორი'); // way 19590999
  place(sheratonMetechi(), 41.68885, 44.82317, -1.55, 0, 'შერატონ მეტეხი'); // way 20990671
  place(radissonRed(), 41.70525, 44.80045, -1.0, 0, 'რადისონ რედი'); // node 11358652556

  // 2026-07-30 wave: every major statue real-size at its OSM position.
  // Equestrian builders face +x (yaw = −(bearing+90°)); standing ones face +z
  // (yaw = −bearing), same convention as the philharmonic entrance.
  place(freedomMonument(), 41.69339, 44.80150, -0.79); // way 1018836829, Liberty Sq
  place(kartlisDeda(), 41.68812, 44.80460, -1.13); // way 1063062536, faces ENE over old town
  place(gorgasali(), 41.69032, 44.81073, -0.35); // way 1062990549, faces NW over the Mtkvari
  place(rustaveliMonument(), 41.70389, 44.78993, -2.36); // way 1066637619, metro square
  place(davidBuilder(), 41.76157, 44.76860, -1.05); // node 1392061407, Dighomi city gate
  place(saintNino(), 41.76249, 44.76732, 0.52); // node 1560445093, 19 m column
  place(saakadze(), 41.7245, 44.7765, 1.57); // node 1111316631, Didube square
  place(vazhaPshavela(), 41.72384, 44.72901, -1.75); // node 1213786131
  place(writersPair(), 41.69775, 44.79818, -0.7); // way 1066626116, Gymnasium #1
  place(pharnavaz(), 41.68883, 44.82045, 1.05); // node 5579769621, Europe Sq

  // The COMPLETE OSM statue inventory (Overpass audit 2026-07-30): every
  // statue/sculpture/monument and every bust in greater Tbilisi, minus the ten
  // hand-modeled heroes and the Chronicle complex. Instanced layers with
  // sculpted pose variants (street0-4.glb) and bust variants (bust0-2.glb),
  // assigned deterministically by point index.
  const STREET_STATUES = STREET_STATUE_POINTS;
  const BUSTS = BUST_POINTS;
  // chunk-culled instancing (only nearby statues render — perf)
  const q = new THREE.Quaternion();
  const one = new THREE.Vector3(1, 1, 1);
  const placeInstanced = (pts, geo, mat) => {
    const items = pts.map(([lat, lon]) => toLocal(lat, lon));
    citymap._placeChunkedInstanced(
      geo,
      mat,
      items,
      (m, p) => {
        q.setFromEuler(new THREE.Euler(0, (p.x * 7.3 + p.z) % (Math.PI * 2), 0));
        m.compose(new THREE.Vector3(p.x, citymap.heightAt(p.x, p.z), p.z), q, one);
      },
      1500,
      false,
      2200
    );
  };
  const glbLayer = (file, pts, scale, y, mat) => {
    try {
      newGltfLoader().load(`models/statues/${file}`, (g2) => {
        g2.scene.updateMatrixWorld(true);
        let src = null;
        g2.scene.traverse((o) => {
          if (o.isMesh && !src) src = o;
        });
        const geo = src.geometry.clone().applyMatrix4(src.matrixWorld);
        geo.scale(scale, scale, scale);
        geo.translate(0, y, 0);
        placeInstanced(pts, geo, mat);
      });
    } catch {
      /* offline */
    }
  };
  // statues: 1.5 m plinth + 3.2 m figure (upscaled from life-size to read
  // from the air). Five sculpted pose variants, assigned deterministically.
  const pick = (pts, k, n) => pts.filter((_, i) => i % n === k);
  const plinthGeo = new THREE.BoxGeometry(1.7, 1.5, 1.7);
  plinthGeo.translate(0, 0.75, 0);
  placeInstanced(STREET_STATUES, plinthGeo, GRANITE);
  const streetMat = new THREE.MeshLambertMaterial({ color: 0xcbb89a, vertexColors: true });
  for (let k = 0; k < 5; k++) glbLayer(`street${k}.glb`, pick(STREET_STATUES, k, 5), 3.2, 1.5, streetMat);
  // busts: slender 1.6 m pillar + 2 m bust, three sculpted variants
  const pillarGeo = new THREE.BoxGeometry(1.0, 1.6, 1.0);
  pillarGeo.translate(0, 0.8, 0);
  placeInstanced(BUSTS, pillarGeo, GRANITE);
  const bustMat = new THREE.MeshLambertMaterial({ color: 0xbfae92, vertexColors: true });
  for (let k = 0; k < 3; k++) glbLayer(`bust${k}.glb`, pick(BUSTS, k, 3), 2.0, 1.6, bustMat);
}

// Category storefronts: awning color + sign text. Ground-floor style.
const BIZ = {
  bank: { color: 0x1f5fa8, text: 'BANK', standalone: false },
  restaurant: { color: 0xb43a2a, text: 'რესტორანი', standalone: false },
  cafe: { color: 0x8a5a34, text: 'CAFE', standalone: false },
  pharmacy: { color: 0x2a9d5c, text: 'APTIAqI +', standalone: false },
  hotel: { color: 0x3a4d80, text: 'HOTEL', standalone: false },
  shop: { color: 0xc08a30, text: 'MARKET', standalone: false },
  fuel: { color: 0xd23c2a, text: 'GAS', standalone: true },
};

function addStorefronts(citymap, pois) {
  const m4 = new THREE.Matrix4();
  const q = new THREE.Quaternion();
  const e = new THREE.Euler();
  const one = new THREE.Vector3(1, 1, 1);

  for (const [cat, cfg] of Object.entries(BIZ)) {
    const pts = pois[cat] || [];
    if (!pts.length) continue;

    if (cfg.standalone) {
      // gas stations: a flat canopy on two posts + a sign
      const canopyGeo = new THREE.BoxGeometry(11, 0.6, 7);
      canopyGeo.translate(0, 5.2, 0);
      const canopy = new THREE.InstancedMesh(canopyGeo, lambert(0xe8e8e8), pts.length);
      const postGeo = new THREE.BoxGeometry(0.5, 5.2, 0.5);
      postGeo.translate(0, 2.6, 0);
      const posts = new THREE.InstancedMesh(postGeo, lambert(cfg.color), pts.length * 2);
      canopy.castShadow = true;
      pts.forEach(([x, z], k) => {
        const y = citymap.heightAt(x, z);
        m4.compose(new THREE.Vector3(x, y, z), q, one);
        canopy.setMatrixAt(k, m4);
        m4.compose(new THREE.Vector3(x - 4, y, z), q, one);
        posts.setMatrixAt(k * 2, m4);
        m4.compose(new THREE.Vector3(x + 4, y, z), q, one);
        posts.setMatrixAt(k * 2 + 1, m4);
      });
      canopy.instanceMatrix.needsUpdate = true;
      posts.instanceMatrix.needsUpdate = true;
      group_add(citymap, canopy, posts);
      continue;
    }

    // storefront: colored awning + a sign board with the category name
    const awnGeo = new THREE.BoxGeometry(6.5, 0.5, 2.2);
    awnGeo.translate(0, 4, 1.1);
    const awning = new THREE.InstancedMesh(awnGeo, lambert(cfg.color), pts.length);
    awning.castShadow = true;
    const signGeo = new THREE.PlaneGeometry(6.2, 1.5);
    signGeo.translate(0, 5.1, 0.1);
    const signMat = new THREE.MeshBasicMaterial({
      map: label(cfg.text, 512, 128, `#${cfg.color.toString(16).padStart(6, '0')}`, '#ffffff', 60),
      side: THREE.DoubleSide,
    });
    const sign = new THREE.InstancedMesh(signGeo, signMat, pts.length);

    pts.forEach(([x, z], k) => {
      const y = citymap.heightAt(x, z);
      e.set(0, (x * 7.3 + z) % (Math.PI * 2), 0); // deterministic facing
      q.setFromEuler(e);
      m4.compose(new THREE.Vector3(x, y, z), q, one);
      awning.setMatrixAt(k, m4);
      sign.setMatrixAt(k, m4);
    });
    awning.instanceMatrix.needsUpdate = true;
    sign.instanceMatrix.needsUpdate = true;
    group_add(citymap, awning, sign);
  }
}

function group_add(citymap, ...objs) {
  for (const o of objs) {
    o.frustumCulled = false;
    citymap.group.add(o);
  }
}

// The COMPLETE OSM statue inventory (Overpass audit 2026-07-30): every
// statue/sculpture/monument and every bust in greater Tbilisi, minus the ten
// hand-modeled heroes and the Chronicle complex. Module-scope + exported so
// the map UI (mapui.js) can draw them too.
export const STREET_STATUE_POINTS = [
  [41.70732, 44.77498], [41.73986, 44.77016], [41.69068, 44.80771], [41.70997, 44.76782],
  [41.83903, 44.71074], [41.70735, 44.79164], [41.82956, 44.73537], [41.69733, 44.80988],
  [41.70739, 44.7873], [41.69084, 44.80846], [41.7397, 44.77946], [41.7269, 44.79948],
  [41.69693, 44.79897], [41.70128, 44.7958], [41.7019, 44.79506], [41.70888, 44.77946],
  [41.6855, 44.89179], [41.68981, 44.80887], [41.69608, 44.79694], [41.70411, 44.79889],
  [41.69186, 44.81563], [41.68677, 44.80362], [41.68881, 44.8124], [41.68066, 44.84147],
  [41.69635, 44.80654], [41.6962, 44.80654], [41.69645, 44.80622], [41.69856, 44.79873],
  [41.69628, 44.79353], [41.71886, 44.79013], [41.7188, 44.79029], [41.72219, 44.78677],
  [41.70814, 44.80015], [41.70817, 44.79977], [41.70775, 44.80024], [41.70772, 44.79971],
  [41.72155, 44.74529], [41.69623, 44.80459], [41.69646, 44.80679], [41.69969, 44.80169],
  [41.75806, 44.77355], [41.68979, 44.80891], [41.69612, 44.80328], [41.6982, 44.79916],
  [41.71009, 44.80463], [41.70808, 44.76954], [41.72217, 44.74576], [41.69568, 44.80708],
  [41.69417, 44.80647], [41.69206, 44.80744], [41.70142, 44.80237], [41.70128, 44.80049],
  [41.69721, 44.70034], [41.71031, 44.76509], [41.71172, 44.7561], [41.71063, 44.76892],
  [41.69244, 44.81953], [41.82835, 44.72577], [41.72399, 44.80054], [41.70813, 44.79328],
  [41.70794, 44.79343], [41.70757, 44.79189], [41.70737, 44.79203], [41.69172, 44.81119],
  [41.70389, 44.79232], [41.69892, 44.80166], [41.68678, 44.80143], [41.70875, 44.78128],
  [41.7123, 44.77235], [41.67571, 44.82923], [41.74801, 44.79078], [41.70722, 44.77502],
  [41.70141, 44.75292], [41.69471, 44.78391], [41.70802, 44.7748], [41.68661, 44.84053],
  [41.72852, 44.72929], [41.72417, 44.73875], [41.71182, 44.79248], [41.71697, 44.72515],
  [41.72668, 44.75425], [41.71226, 44.78187], [41.72341, 44.70219], [41.72482, 44.75403],
  [41.71117, 44.78452], [41.69377, 44.7774], [41.70606, 44.72583], [41.7032, 44.79224],
  [41.69879, 44.79963], [41.7015, 44.75426], [41.69027, 44.80735], [41.69831, 44.80955],
  [41.70996, 44.80156], [41.72609, 44.72342], [41.73322, 44.7728], [41.84339, 44.71891],
  [41.71512, 44.79455], [41.69816, 44.80047], [41.70115, 44.79549], [41.70064, 44.79618],
  [41.70003, 44.79684], [41.69598, 44.78863], [41.69617, 44.78856], [41.70956, 44.77928],
  [41.71042, 44.77936], [41.77693, 44.80626], [41.71485, 44.7954], [41.70273, 44.80459],
  [41.71103, 44.75145], [41.71103, 44.75161], [41.69026, 44.80729], [41.69025, 44.80732],
  [41.72534, 44.79372], [41.69602, 44.80653], [41.69605, 44.80653], [41.69608, 44.80653],
  [41.69612, 44.80653], [41.69622, 44.80654], [41.69625, 44.80654], [41.69629, 44.80654],
  [41.69631, 44.80654], [41.68479, 44.80603], [41.69703, 44.81738], [41.70986, 44.76854],
  [41.70937, 44.76867], [41.70725, 44.85509], [41.70857, 44.80005], [41.71251, 44.79642],
  [41.69225, 44.81499], [41.71107, 44.74683], [41.71069, 44.77], [41.71965, 44.77861],
  [41.7412, 44.77782], [41.74203, 44.77821], [41.70996, 44.76856], [41.72232, 44.77813],
  [41.74759, 44.78927], [41.72635, 44.76559], [41.70717, 44.77579], [41.77497, 44.76827],
  [41.67109, 44.85888], [41.69376, 44.92382], [41.7875, 44.74893], [41.6987, 44.80135],
  [41.69601, 44.81027], [41.72982, 44.79685], [41.6908, 44.83017], [41.69159, 44.87005],
  [41.70254, 44.79393], [41.69999, 44.79689], [41.69982, 44.79709], [41.69978, 44.79714],
  [41.69976, 44.79716], [41.70036, 44.79637], [41.83842, 44.70389], [41.84084, 44.71469],
  [41.70006, 44.79617], [41.7414, 44.83279], [41.68551, 44.82048], [41.6884, 44.81087],
  [41.68647, 44.81958], [41.68584, 44.86357], [41.71209, 44.75153], [41.70362, 44.74461],
  [41.69843, 44.79971], [41.7021, 44.7937], [41.70221, 44.79357], [41.70071, 44.79539],
  [41.70093, 44.79511], [41.70187, 44.79397], [41.70053, 44.79561], [41.70081, 44.79527],
  [41.70041, 44.79575], [41.7003, 44.79589], [41.69984, 44.79647], [41.69874, 44.79957],
  [41.77765, 44.80479], [41.71673, 44.79255], [41.71664, 44.7923], [41.71661, 44.79232],
  [41.68213, 44.8241], [41.72437, 44.77776], [41.75878, 44.77441], [41.69841, 44.80666],
  [41.72223, 44.77208], [41.68817, 44.79051], [41.69151, 44.87026], [41.71905, 44.80926],
  [41.70459, 44.7807], [41.71313, 44.78249], [41.68819, 44.81075], [41.72153, 44.78883],
  [41.72279, 44.79211], [41.70057, 44.804], [41.69797, 44.80003], [41.69903, 44.80043],
  [41.69293, 44.80158], [41.69293, 44.80147], [41.70732, 44.78626], [41.78802, 44.76552],
  [41.72068, 44.71416], [41.70698, 44.85571], [41.70644, 44.75153], [41.70841, 44.75153],
  [41.71768, 44.87665], [41.7172, 44.87702], [41.68328, 44.82341]
];
export const BUST_POINTS = [
  [41.69058, 44.8074], [41.69672, 44.80069], [41.71432, 44.794], [41.71452, 44.79387],
  [41.69458, 44.80197], [41.71814, 44.79148], [41.68887, 44.81116], [41.69369, 44.80432],
  [41.69854, 44.8001], [41.68925, 44.83389], [41.69861, 44.79847], [41.69856, 44.79989],
  [41.69604, 44.80366], [41.70804, 44.78244], [41.70131, 44.7957], [41.70129, 44.79567],
  [41.68858, 44.81648], [41.72527, 44.73887], [41.70908, 44.78158], [41.70783, 44.78633],
  [41.70967, 44.78005], [41.74841, 44.79062], [41.69659, 44.80078], [41.70714, 44.77345],
  [41.72756, 44.73171], [41.72505, 44.71512], [41.70722, 44.80246], [41.70889, 44.79507],
  [41.70953, 44.79436], [41.7124, 44.78192], [41.72724, 44.75696], [41.70716, 44.77494],
  [41.71082, 44.7617], [41.70099, 44.80778], [41.71234, 44.7817], [41.7222, 44.77804],
  [41.70752, 44.79114], [41.70064, 44.79625], [41.72229, 44.72947], [41.70698, 44.76548],
  [41.6919, 44.79964], [41.70949, 44.77924], [41.7094, 44.77921], [41.70946, 44.77895],
  [41.70953, 44.77891], [41.7096, 44.77889], [41.70965, 44.77929], [41.70967, 44.77929],
  [41.70978, 44.77932], [41.7099, 44.77935], [41.70993, 44.77936], [41.70998, 44.77937],
  [41.71006, 44.77938], [41.71011, 44.7794], [41.71015, 44.77941], [41.71023, 44.77942],
  [41.71031, 44.77942], [41.71062, 44.77938], [41.71017, 44.77906], [41.71022, 44.77906],
  [41.70748, 44.78498], [41.71729, 44.79182], [41.68481, 44.80598], [41.68472, 44.80599],
  [41.68474, 44.80599], [41.75039, 44.78993], [41.69549, 44.80167], [41.7542, 44.77694],
  [41.65432, 44.65781], [41.69252, 44.8139], [41.71915, 44.77915], [41.72597, 44.77414],
  [41.71583, 44.79454], [41.71099, 44.74122], [41.70977, 44.76883], [41.70637, 44.79934],
  [41.71247, 44.74948], [41.71843, 44.77885], [41.69246, 44.82338], [41.70094, 44.79649],
  [41.77774, 44.80664], [41.69709, 44.81503], [41.69722, 44.81527], [41.69716, 44.81526],
  [41.71035, 44.77941], [41.70385, 44.7445], [41.69536, 44.80716], [41.6635, 44.70554],
  [41.72172, 44.78919], [41.68649, 44.81888], [41.68877, 44.81187]
];
