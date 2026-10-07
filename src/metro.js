import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';

// Real Tbilisi metro: all 23 stations at their OSM entrance positions with
// Blender pavilions (tools/build_metro_assets.py) — the grand-arch type for
// the old downtown stations, the glass-hall type elsewhere — plus stair
// portals and M totems at every mapped subway entrance, and a name plate on
// each pavilion fascia.

const lambert = (c, extra = {}) => new THREE.MeshLambertMaterial({ color: c, ...extra });
const TEAL = '#00a99d'; // Tbilisi Transport teal (buses); the metro M is RED
const RED = '#d3251c';
const MAT = {
  stone: lambert(0xd7c7a4),
  concrete: lambert(0xd0ccc2),
  glass: new THREE.MeshStandardMaterial({ color: 0x41606f, roughness: 0.2, metalness: 0.45 }),
  frame: lambert(0x3a3f45),
  steps: lambert(0x8f8b84),
  pylon: lambert(0x2e3338),
  signBack: lambert(0xe8e4da),
  plateBack: lambert(0x22262b),
  cream: lambert(0xd8ccb2),
  ashlar: lambert(0xa9a196),
  winband: lambert(0x2b3138),
  clock: new THREE.MeshBasicMaterial({ color: 0xf4f2ec }),
  frame2: lambert(0x3a3f45),
  stone2: lambert(0xcbbd9d),
  concrete2: lambert(0xd0ccc2),
  roof: lambert(0x3c4046),
};

// the real Tbilisi metro M: wide splayed legs, deep center notch — drawn as
// a polygon traced from station photos, not a font glyph
function drawM(g, x, y, w, h, color) {
  const P = [
    [0.0, 1.0], [0.34, 0.0], [0.5, 0.34], [0.66, 0.0], [1.0, 1.0],
    [0.78, 1.0], [0.585, 0.44], [0.5, 0.72], [0.415, 0.44], [0.22, 1.0],
  ];
  g.fillStyle = color;
  g.beginPath();
  P.forEach(([px, py], i) => (i ? g.lineTo(x + px * w, y + py * h) : g.moveTo(x + px * w, y + py * h)));
  g.closePath();
  g.fill();
}

function mSignTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = '#f2efe8';
  g.fillRect(0, 0, 128, 128);
  drawM(g, 14, 18, 100, 74, RED);
  g.fillStyle = '#2a2e33';
  g.font = 'bold 25px sans-serif';
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText('მეტრო', 64, 111);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return new THREE.MeshBasicMaterial({ map: t });
}

function namePlate(ka, en) {
  const c = document.createElement('canvas');
  c.width = 1024;
  c.height = 112;
  const g = c.getContext('2d');
  g.fillStyle = '#22262b';
  g.fillRect(0, 0, 1024, 112);
  drawM(g, 24, 20, 92, 72, RED);
  g.fillStyle = '#ffffff';
  g.textAlign = 'left';
  g.font = 'bold 46px sans-serif';
  g.fillText(ka || en, 150, 36);
  g.font = '34px sans-serif';
  g.fillText((en || '').toUpperCase(), 150, 84);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return new THREE.MeshBasicMaterial({ map: t });
}

// blue lettering on a light band (Marjanishvili/Avlabari friezes)
function bluePlate(text, bg = '#d8ccb2', ratio = 8) {
  const c = document.createElement('canvas');
  c.width = 1024;
  c.height = Math.round(1024 / ratio);
  const g = c.getContext('2d');
  g.fillStyle = bg;
  g.fillRect(0, 0, c.width, c.height);
  g.fillStyle = '#243f8f';
  g.font = `bold ${Math.round(c.height * 0.62)}px sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText(text, c.width / 2, c.height * 0.54);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return new THREE.MeshBasicMaterial({ map: t });
}

// white station-terminal band: red M + blue name + secondary line
function terminalPlate(en, ka) {
  const c = document.createElement('canvas');
  c.width = 2048;
  c.height = 240;
  const g = c.getContext('2d');
  g.fillStyle = '#eceae2';
  g.fillRect(0, 0, 2048, 240);
  drawM(g, 150, 40, 150, 150, RED);
  g.fillStyle = '#243f8f';
  g.textAlign = 'left';
  g.textBaseline = 'middle';
  g.font = 'bold 92px sans-serif';
  g.fillText(en.toUpperCase(), 380, 96);
  g.font = 'bold 64px sans-serif';
  g.fillText(ka, 380, 176);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return new THREE.MeshBasicMaterial({ map: t });
}

// hero-station placements (satellite-fit; yaw iterated via screenshots)
const SQR_POS = [-4324.2, -295.4, 1.22];
const MARJ_POS = [-4249.3, -1733.8, 2.4];
const AVLA_POS = [-5827.8, -3658.6, 2.4];

// Rustaveli pavilion (photo-modeled), front facing SW over its plaza
const RUSTAVELI_POS = [-3653.6, -2334.6, -0.96];

// Galleria Tbilisi center (satellite-fit), long axis along Rustaveli
const GALLERIA_POS = [-4500.7, -3330.7, -1.13];

function loadParts(file) {
  return new Promise((res) => {
    newGltfLoader().load(
      `models/metro/${file}`,
      (g) => {
        g.scene.updateMatrixWorld(true);
        const parts = {};
        g.scene.traverse((o) => {
          if (o.isMesh) parts[o.name.replace(/\.\d+$/, '')] = o.geometry.clone().applyMatrix4(o.matrixWorld);
        });
        res(parts);
      },
      undefined,
      () => res(null)
    );
  });
}

export async function addMetro(citymap) {
  let data;
  try {
    const r = await fetch('tbilisi/metro.json');
    if (!r.ok) return;
    data = await r.json();
  } catch {
    return;
  }
  const { group } = citymap;
  const h = (x, z) => citymap.heightAt(x, z);
  const [arch, hall, entr, totem, galleria, rustaveli, marj, avla, sqr] = await Promise.all(
    ['pavilion_arch', 'pavilion_hall', 'entrance', 'totem', 'galleria', 'rustaveli',
     'marjanishvili', 'avlabari', 'station_square'].map((n) => loadParts(`${n}.glb`))
  );
  const mSign = mSignTexture();

  const buildKit = (parts, x, z, yaw, extra = {}) => {
    if (!parts) return null;
    const g = new THREE.Group();
    for (const [name, geo] of Object.entries(parts)) {
      const mat = extra[name] ?? MAT[name] ?? MAT.concrete;
      // canvas-textured signs/plates: box-face UVs come out rotated from the
      // GLB, so put the texture on a clean plane over the box's front face
      if ((name === 'sign' || name === 'fascia') && mat.map) {
        geo.computeBoundingBox();
        const bb = geo.boundingBox;
        const back = new THREE.Mesh(geo, name === 'sign' ? MAT.signBack : MAT.plateBack);
        back.castShadow = true;
        g.add(back);
        const face = new THREE.Mesh(
          new THREE.PlaneGeometry(bb.max.x - bb.min.x, bb.max.y - bb.min.y),
          mat
        );
        face.position.set((bb.max.x + bb.min.x) / 2, (bb.max.y + bb.min.y) / 2, bb.max.z + 0.04);
        g.add(face);
        continue;
      }
      const m = new THREE.Mesh(geo, mat);
      m.castShadow = true;
      m.receiveShadow = true;
      g.add(m);
    }
    g.position.set(x, h(x, z), z);
    g.rotation.y = yaw;
    group.add(g);
    return g;
  };

  for (const s of data.stations) {
    const plate = namePlate(s.ka, s.en);
    if (s.en === 'Liberty Square') {
      // the whole Galleria mall (photo-modeled) with its metro front — the
      // generic extruded block there is excluded in landmarks.js
      buildKit(galleria, ...GALLERIA_POS, {
        fascia: plate,
        sign: mSign,
        glass: MAT.glass,
        concrete2: MAT.concrete,
        stone2: MAT.stone,
        roof: lambert(0x3c4046),
      });
      for (const [ex, ez] of s.entrances) {
        buildKit(entr, ex, ez, Math.atan2(s.x - ex, s.z - ez));
      }
      continue;
    }
    if (s.en === 'Marjanishvili') {
      buildKit(marj, ...MARJ_POS, { fascia: bluePlate(s.ka || 'მარჯანიშვილი', '#d8ccb2', 12) });
      buildKit(totem, MARJ_POS[0] + 13, MARJ_POS[1], MARJ_POS[2], { sign: mSign });
      for (const [ex, ez] of s.entrances) buildKit(entr, ex, ez, Math.atan2(s.x - ex, s.z - ez));
      continue;
    }
    if (s.en === 'Avlabari') {
      buildKit(avla, ...AVLA_POS, { fascia: bluePlate(s.ka || 'ავლაბარი', '#d8ccb2', 7), sign: mSign });
      buildKit(totem, AVLA_POS[0] + 12, AVLA_POS[1], AVLA_POS[2], { sign: mSign });
      for (const [ex, ez] of s.entrances) buildKit(entr, ex, ez, Math.atan2(s.x - ex, s.z - ez));
      continue;
    }
    if (s.en === 'Station Square-1' || s.en === 'Station Square') {
      buildKit(sqr, ...SQR_POS, { fascia: terminalPlate('Station Square', s.ka || 'სადგურის მოედანი'), sign: mSign });
      buildKit(totem, SQR_POS[0] + 30, SQR_POS[1] - 6, SQR_POS[2], { sign: mSign });
      for (const [ex, ez] of s.entrances) buildKit(entr, ex, ez, Math.atan2(s.x - ex, s.z - ez));
      continue;
    }
    if (s.en === 'Rustaveli') {
      // photo-modeled pavilion: relief mural, slab roof, blue italic letters
      const blue = document.createElement('canvas');
      blue.width = 1024;
      blue.height = 140;
      const bg = blue.getContext('2d');
      bg.fillStyle = '#efece4';
      bg.fillRect(0, 0, 1024, 140);
      bg.fillStyle = '#2b3f9e';
      bg.font = 'bold italic 104px sans-serif';
      bg.textAlign = 'center';
      bg.textBaseline = 'middle';
      bg.fillText(s.ka || 'რუსთაველი', 512, 74);
      const bt = new THREE.CanvasTexture(blue);
      bt.colorSpace = THREE.SRGBColorSpace;
      bt.anisotropy = 4;
      // the real carved mural + red-M glass doors, cut from a CC photo
      const photo = (file, tint) => {
        const m = new THREE.MeshLambertMaterial({ color: tint });
        try {
          new THREE.TextureLoader().load(`textures/facades/${file}`, (t) => {
            t.colorSpace = THREE.SRGBColorSpace;
            t.anisotropy = 8;
            m.map = t;
            m.color.set(0xffffff);
            m.needsUpdate = true;
          });
        } catch {
          /* offline */
        }
        return m;
      };
      buildKit(rustaveli, ...RUSTAVELI_POS, {
        fascia: new THREE.MeshBasicMaterial({ map: bt }),
        sign: mSign,
        relief: photo('rustaveli_relief.jpg', 0xcabb9d),
        glass: photo('rustaveli_doors.jpg', 0x2e3338),
        hedge: new THREE.MeshLambertMaterial({ color: 0x3d6329 }),
      });
      buildKit(totem, RUSTAVELI_POS[0] - 16, RUSTAVELI_POS[1] - 6, RUSTAVELI_POS[2], { sign: mSign });
      for (const [ex, ez] of s.entrances) buildKit(entr, ex, ez, Math.atan2(s.x - ex, s.z - ez));
      continue;
    }
    if (s.pav === 'none') {
      // the real station building is already in the map (OSM footprint) —
      // add the stair portal, the M totem and a name board beside it
      buildKit(entr, s.x, s.z, s.yaw);
      const board = new THREE.Mesh(new THREE.PlaneGeometry(7, 0.9), plate);
      const bx = s.x + Math.sin(s.yaw) * 4.5;
      const bz = s.z + Math.cos(s.yaw) * 4.5;
      board.position.set(bx, h(bx, bz) + 2.6, bz);
      board.rotation.y = s.yaw;
      const posts = new THREE.Group();
      posts.add(board);
      group.add(posts);
    } else {
      const parts = s.pav === 'arch' ? arch : hall;
      buildKit(parts, s.x, s.z, s.yaw, { fascia: plate, sign: mSign });
    }
    // M totem beside the pavilion door
    buildKit(totem, s.x + Math.cos(s.yaw) * 8, s.z - Math.sin(s.yaw) * 8, s.yaw, { sign: mSign });
    // stair portals + totems at every other mapped entrance
    for (const [ex, ez] of s.entrances) {
      const eyaw = Math.atan2(s.x - ex, s.z - ez);
      buildKit(entr, ex, ez, eyaw);
      buildKit(totem, ex + Math.cos(eyaw) * 3.4, ez - Math.sin(eyaw) * 3.4, eyaw, { sign: mSign });
    }
  }
}
