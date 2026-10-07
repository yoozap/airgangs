import * as THREE from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { newGltfLoader } from './gltf.js';
import { makeWindy } from './wind.js';
import { computeBoundsTree, disposeBoundsTree, acceleratedRaycast } from 'three-mesh-bvh';
import { EXCLUSIONS, addLandmarks } from './landmarks.js';
import { addParks } from './parks.js';
import { addMetro } from './metro.js';
import { addAirport } from './airport.js';
import { buildShopfronts } from './shopfronts.js';
import { addMalls } from './malls.js';
import { addInfrastructure } from './infra.js';
import { realBridgeZones, buildRealBridges } from './bridges.js';
import { buildShell, carve, writeShellVerts } from './breakwall.js';
import { fetchRoadNet, mergeRoadNet, buildJunctions, buildSpans } from './roads.js';

// Pull one instanceable {geometry, material} out of a loaded GLB scene.
// The Kenney models keep their color in a texture atlas that doesn't survive
// the geometry merge, so instead of chasing it we paint each vertex by height:
// brown trunk below trunkFrac, green canopy above (with a little variation).
function extractGeoMat(root, trunkFrac = 0.32) {
  root.updateMatrixWorld(true);
  const geos = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    geos.push(g);
  });
  const geometry = geos.length > 1 ? mergeGeometries(geos) : geos[0];
  const pos = geometry.getAttribute('position');
  geometry.computeBoundingBox();
  const bb = geometry.boundingBox;
  const cut = bb.min.y + (bb.max.y - bb.min.y) * trunkFrac;
  const trunk = new THREE.Color(0x6a4d32).convertSRGBToLinear();
  const leafA = new THREE.Color(0x35692e).convertSRGBToLinear();
  const leafB = new THREE.Color(0x4f7d3a).convertSRGBToLinear();
  const col = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) {
    const y = pos.getY(i);
    if (y < cut) {
      col[i * 3] = trunk.r; col[i * 3 + 1] = trunk.g; col[i * 3 + 2] = trunk.b;
    } else {
      const t = fract(Math.sin((pos.getX(i) + pos.getZ(i)) * 9.7) * 43758.5);
      col[i * 3] = leafA.r + (leafB.r - leafA.r) * t;
      col[i * 3 + 1] = leafA.g + (leafB.g - leafA.g) * t;
      col[i * 3 + 2] = leafA.b + (leafB.b - leafA.b) * t;
    }
  }
  geometry.setAttribute('color', new THREE.BufferAttribute(col, 3));
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true }) };
}

// Like extractGeoMat but KEEPS the model's real colors (bakes each mesh's
// material base color into vertex colors). For props like signs where the
// actual colors matter, not a green/brown tree gradient.
function extractModel(root) {
  root.updateMatrixWorld(true);
  const geos = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const count = src.getAttribute('position').count;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    const base = (o.material && o.material.color ? o.material.color.clone() : new THREE.Color(0xcccccc));
    const vc = src.getAttribute('color');
    const col = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      col[i * 3] = base.r * (vc ? vc.getX(i) : 1);
      col[i * 3 + 1] = base.g * (vc ? vc.getY(i) : 1);
      col[i * 3 + 2] = base.b * (vc ? vc.getZ(i) : 1);
    }
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    geos.push(g);
  });
  const geometry = geos.length > 1 ? mergeGeometries(geos) : geos[0];
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true }) };
}

// Keep a Kenney model's real colours by baking each mesh's material base colour
// (and any vertex colour / atlas map) — for apartment blocks where the modeled
// windows must stay dark against light walls.
function extractTextured(root) {
  root.updateMatrixWorld(true);
  const geos = [];
  let map = null;
  root.traverse((o) => {
    if (!o.isMesh) return;
    if (o.material && o.material.map) map = o.material.map;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const count = src.getAttribute('position').count;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    g.setAttribute('uv', src.getAttribute('uv') || new THREE.BufferAttribute(new Float32Array(count * 2), 2));
    const base = o.material && o.material.color ? o.material.color.clone() : new THREE.Color(0xcccccc);
    const vc = src.getAttribute('color');
    const col = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      col[i * 3] = base.r * (vc ? vc.getX(i) : 1);
      col[i * 3 + 1] = base.g * (vc ? vc.getY(i) : 1);
      col[i * 3 + 2] = base.b * (vc ? vc.getZ(i) : 1);
    }
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    geos.push(g);
  });
  const geometry = geos.length > 1 ? mergeGeometries(geos) : geos[0];
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, map: map || null, flatShading: true }) };
}

// A rock/stone model: merged geometry with a stone-grey vertex colour, lighter
// on top faces so it reads as lit stone. No atlas needed.
function extractRock(root) {
  root.updateMatrixWorld(true);
  const geos = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    geos.push(g);
  });
  const geometry = geos.length > 1 ? mergeGeometries(geos) : geos[0];
  const pos = geometry.getAttribute('position');
  const nor = geometry.getAttribute('normal');
  const top = new THREE.Color(0x8f887c).convertSRGBToLinear();
  const side = new THREE.Color(0x6e675d).convertSRGBToLinear();
  const col = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) {
    const t = THREE.MathUtils.clamp(nor.getY(i) * 0.5 + 0.5, 0, 1);
    col[i * 3] = side.r + (top.r - side.r) * t;
    col[i * 3 + 1] = side.g + (top.g - side.g) * t;
    col[i * 3 + 2] = side.b + (top.b - side.b) * t;
  }
  geometry.setAttribute('color', new THREE.BufferAttribute(col, 3));
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true }) };
}

// Repaint a city-pack model into the map's palette while keeping its shape
// detail: each vertex keeps its original brightness (windows and trim stay
// darker), but the hue becomes the given wall colour; up-facing faces get the
// roof colour. Keeps the city's old tan/cream look with the new geometry.
function retintModel(root, wallHex, roofHex) {
  root.updateMatrixWorld(true);
  const geos = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const count = src.getAttribute('position').count;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    const base = o.material?.color ?? new THREE.Color(0xffffff);
    const vc = src.getAttribute('color');
    const wall = new THREE.Color(wallHex).convertSRGBToLinear();
    const roof = new THREE.Color(roofHex).convertSRGBToLinear();
    const nor = g.getAttribute('normal');
    const col = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      const r = base.r * (vc ? vc.getX(i) : 1);
      const gr = base.g * (vc ? vc.getY(i) : 1);
      const b = base.b * (vc ? vc.getZ(i) : 1);
      const lum = 0.3 * r + 0.59 * gr + 0.11 * b;
      const c = nor.getY(i) > 0.6 ? roof : wall;
      // dark glass stays dark; everything else is the wall tone shaded by the
      // model's own brightness so facade detail survives the retint
      const k = lum < 0.02 ? 0.25 : Math.min(1.25, 0.55 + 1.1 * lum);
      col[i * 3] = Math.min(1, c.r * k);
      col[i * 3 + 1] = Math.min(1, c.g * k);
      col[i * 3 + 2] = Math.min(1, c.b * k);
    }
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    geos.push(g);
  });
  const geometry = darkenBase(geos.length > 1 ? mergeGeometries(geos) : geos[0]);
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true }) };
}

// Grounding gradient for house models: darken the vertex colours of the lowest
// wall band so cottages read as weathered plaster sitting on the ground, not a
// uniform cream box (colours interpolate up the wall → soft grime gradient).
function darkenBase(geometry, frac = 0.18, k = 0.72) {
  geometry.computeBoundingBox();
  const bb = geometry.boundingBox;
  const bandY = bb.min.y + frac * (bb.max.y - bb.min.y);
  const pos = geometry.getAttribute('position');
  const col = geometry.getAttribute('color');
  for (let i = 0; i < pos.count; i++) {
    if (pos.getY(i) < bandY) col.setXYZ(i, col.getX(i) * k, col.getY(i) * k, col.getZ(i) * k);
  }
  return geometry;
}

// A Kenney house model coloured by surface orientation: up-facing = roof (given
// colour), down-facing = eaves/trim, the rest = cream wall. Reliable regardless
// of the model's texture atlas.
function extractHouse(root, roofHex) {
  root.updateMatrixWorld(true);
  const geos = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const src = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
    src.applyMatrix4(o.matrixWorld);
    if (!src.getAttribute('normal')) src.computeVertexNormals();
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', src.getAttribute('position'));
    g.setAttribute('normal', src.getAttribute('normal'));
    geos.push(g);
  });
  const geometry = geos.length > 1 ? mergeGeometries(geos) : geos[0];
  const pos = geometry.getAttribute('position');
  const nor = geometry.getAttribute('normal');
  const roof = new THREE.Color(roofHex).convertSRGBToLinear();
  const wall = new THREE.Color(0xdccfb6).convertSRGBToLinear();
  const trim = new THREE.Color(0x9a8f7c).convertSRGBToLinear();
  const col = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) {
    const ny = nor.getY(i);
    const c = ny > 0.45 ? roof : ny < -0.3 ? trim : wall;
    col[i * 3] = c.r;
    col[i * 3 + 1] = c.g;
    col[i * 3 + 2] = c.b;
  }
  geometry.setAttribute('color', new THREE.BufferAttribute(col, 3));
  darkenBase(geometry);
  return { geometry, material: new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true }) };
}

// Fast BVH raycasts for terrain + building collision.
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
THREE.Mesh.prototype.raycast = acceleratedRaycast;

// Chunk visibility is swept in this many slices, one slice per frame.
const CULL_SLICES = 4;

const CHUNK = 1000; // m — buildings are merged per 1 km cell for culling
const HOUSE_STYLES = 21; // number of Kenney house models in public/models/houses/
const APT_STYLES = 14; // Kenney commercial building models for apartment blocks

const hexToRgb = (hex) => {
  const c = new THREE.Color(hex).convertSRGBToLinear();
  return [c.r * 255, c.g * 255, c.b * 255];
};
const pal = (list) => list.map(hexToRgb);

// Tbilisi building styles, picked by size/height/district.
const WARM_WALLS = pal(['#e6d8bd', '#dcc9a6', '#d8cab4', '#cdb693', '#e0d2c4', '#c9b394', '#d5c3ab']); // old-town plaster
const HOUSE_WALLS = pal(['#ded4c2', '#d3c6ae', '#c8b89e', '#cfc5b8', '#c0ab8d', '#d8c8b0']);
const PANEL_WALLS = pal(['#c9c2b1', '#bdb6a6', '#b2ac9f', '#cec7b5', '#a9a39a', '#c2b6a0', '#b5a78e']); // soviet blocks
const GLASS_WALLS = pal(['#8fa3b5', '#7e93a6', '#9fb1c0', '#8898ab']); // modern towers
const FLAT_ROOFS = pal(['#77716a', '#6e6963', '#807a70', '#8a7f72']);
const RED_ROOFS = pal(['#8a4632', '#93503a', '#7c3f30', '#5f5a55', '#9c5a41']); // rust metal / zinc
const DARK_ROOFS = pal(['#4f555c', '#454b52']);
// Suburban house roofs are corrugated sheet metal: mostly rust-red, then
// galvanised zinc, painted green and blue-grey. Rust is weighted by repeats
// (a slate-dark palette turned whole neighbourhoods black from the air).
const COTTAGE_ROOFS = pal(['#8a4632', '#93503a', '#9c5a41', '#7c3f30', '#a35f44',
  '#8a4632', '#93503a', '#9c5a41', '#96938c', '#8d8f8a', '#5e7a58', '#5a6f84']);

// Old Tbilisi center in local frame (X=west, Z=north from the Delisi origin).
const OLD_TOWN = { x: -5089, z: -3910, r: 1700 };

// Facade cells tiled 1 window / 3 m, 1 floor / 3 m. Vertex colors tint them.
// Three styles drawn from real Tbilisi architecture: plain plaster windows,
// Soviet panel blocks with loggia balconies, and modern glass curtain walls.
function makeTexture(draw, size = 256) {
  const c = document.createElement('canvas');
  c.width = size;
  c.height = size;
  draw(c.getContext('2d'), size);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  return t;
}

// Draw one realistic window with frame, sill, and a sky-reflecting glass pane.
function drawWindow(g, x, y, w, h) {
  g.fillStyle = 'rgba(0,0,0,0.22)';
  g.fillRect(x - 4, y - 4, w + 8, 5); // lintel shadow
  g.fillStyle = '#c9c2b4';
  g.fillRect(x - 3, y + h, w + 6, 6); // sill
  g.fillStyle = '#2a3138';
  g.fillRect(x, y, w, h); // dark reveal / frame
  const grd = g.createLinearGradient(x, y, x, y + h);
  grd.addColorStop(0, '#8fb0cc');
  grd.addColorStop(0.5, '#5f7f9c');
  grd.addColorStop(1, '#43596e');
  g.fillStyle = grd;
  g.fillRect(x + 3, y + 3, w - 6, h - 6); // glass
  g.fillStyle = '#e9e4d8';
  g.fillRect(x + w / 2 - 1.5, y, 3, h); // vertical mullion
  g.fillRect(x, y + h / 2 - 1.5, w, 3); // horizontal mullion
}

function makeFacadeTextures() {
  // Soviet/apartment window wall: two window bays per panel, concrete seams.
  const windowTex = makeTexture((g, S) => {
    g.fillStyle = '#efece4';
    g.fillRect(0, 0, S, S);
    g.strokeStyle = 'rgba(0,0,0,0.10)'; // precast-panel seams
    g.lineWidth = 2;
    g.strokeRect(1, 1, S - 2, S - 2);
    drawWindow(g, 34, 46, 76, 150);
    drawWindow(g, 146, 46, 76, 150);
    g.fillStyle = 'rgba(0,0,0,0.10)';
    g.fillRect(0, S - 6, S, 6); // floor slab line
  });

  // Soviet loggia balcony: recessed glazed door + concrete rail every floor.
  const balconyTex = makeTexture((g, S) => {
    g.fillStyle = '#eae5db';
    g.fillRect(0, 0, S, S);
    drawWindow(g, 30, 30, 60, 96); // window beside the loggia
    g.fillStyle = '#3a4048'; // loggia door glazing
    g.fillRect(150, 26, 72, 104);
    const gd = g.createLinearGradient(150, 26, 150, 130);
    gd.addColorStop(0, 'rgba(150,180,205,0.6)');
    gd.addColorStop(1, 'rgba(70,95,115,0.5)');
    g.fillStyle = gd;
    g.fillRect(154, 30, 64, 96);
    // concrete balcony slab + railing across the lower floor
    g.fillStyle = 'rgba(0,0,0,0.25)';
    g.fillRect(20, 150, S - 40, 96);
    g.fillStyle = '#d7d1c4';
    g.fillRect(14, 150, S - 28, 12); // slab
    g.fillStyle = '#c4beb0';
    g.fillRect(18, 172, S - 36, 62); // rail panel
    g.fillStyle = 'rgba(0,0,0,0.16)';
    for (let x = 26; x < S - 26; x += 22) g.fillRect(x, 176, 5, 54); // balusters
    g.fillStyle = 'rgba(0,0,0,0.10)';
    g.fillRect(0, S - 6, S, 6);
  });

  const glassTex = makeTexture((g) => {
    g.fillStyle = '#54687c';
    g.fillRect(0, 0, 128, 128);
    const sky = g.createLinearGradient(0, 0, 90, 128);
    sky.addColorStop(0, 'rgba(190,215,235,0.5)');
    sky.addColorStop(1, 'rgba(190,215,235,0.05)');
    g.fillStyle = sky;
    g.fillRect(0, 0, 128, 128); // sky reflection
    g.fillStyle = 'rgba(20,26,33,0.55)';
    for (let y = 0; y < 128; y += 64) g.fillRect(0, y, 128, 4); // floor bands
    g.fillStyle = 'rgba(230,235,240,0.35)';
    for (let x = 0; x < 128; x += 32) g.fillRect(x, 0, 2, 128); // mullions
  });

  // Old Tbilisi: plaster wall with the iconic carved wooden balcony/gallery
  // overhanging each floor, tall shuttered windows.
  const oldTbilisiTex = makeTexture((g) => {
    g.fillStyle = '#e7d9c0';
    g.fillRect(0, 0, 128, 128);
    g.fillStyle = '#3a2f24'; // window opening
    g.fillRect(46, 16, 38, 46);
    g.fillStyle = '#7a5638'; // shutters
    g.fillRect(46, 16, 8, 46);
    g.fillRect(76, 16, 8, 46);
    g.fillStyle = '#e7d9c0';
    g.fillRect(64, 16, 3, 46); // window frame divider
    // carved wooden balcony band across the lower half of each floor
    g.fillStyle = 'rgba(0,0,0,0.25)';
    g.fillRect(8, 74, 112, 40); // recess shadow behind the gallery
    g.fillStyle = '#8a5e3b'; // wooden balcony floor + rail
    g.fillRect(4, 74, 120, 7);
    g.fillStyle = '#a9764a';
    g.fillRect(6, 100, 116, 14); // rail panel
    g.fillStyle = '#6d4a2e';
    for (let x = 10; x < 120; x += 9) g.fillRect(x, 82, 3, 30); // turned balusters
    g.fillStyle = '#7a5638';
    g.fillRect(4, 112, 120, 4); // bottom trim
  });

  // Windowless plaster for buildings that carry REAL 3D window frames — any
  // painted grid would double the kit's, so these walls stay plain: subtle
  // stucco mottling + a floor slab shadow line, tinted by vertex colors.
  const plainTex = makeTexture((g, S) => {
    g.fillStyle = '#efece4';
    g.fillRect(0, 0, S, S);
    for (let i = 0; i < 260; i++) {
      // deterministic speckle — faint stucco grain, no recognizable features
      const rx = ((i * 2654435761) >>> 8) % S;
      const ry = ((i * 40503 + 977) * 69069 >>> 9) % S;
      const rs = 2 + ((i * 7919) % 5);
      g.fillStyle = i % 2 ? 'rgba(0,0,0,0.028)' : 'rgba(255,255,255,0.05)';
      g.fillRect(rx, ry, rs, rs);
    }
    g.strokeStyle = 'rgba(0,0,0,0.05)'; // barely-there panel seams
    g.lineWidth = 2;
    g.strokeRect(1, 1, S - 2, S - 2);
    g.fillStyle = 'rgba(0,0,0,0.09)';
    g.fillRect(0, S - 6, S, 6); // floor slab line
  });

  return [windowTex, balconyTex, glassTex, oldTbilisiTex, plainTex];
}

// How far a tunnel cutting slopes back to grade outside its retaining wall.
// m — how far the bed sits below the measured water surface. The Mtkvari runs
// a few metres deep through the city; all this has to do is keep the ground
// under the sheet so the water reaches the quay.
const RIVER_DEPTH = 3.5;
const CUT_BATTER = 7;

export class CityMap {
  constructor(scene, game = null) {
    this.scene = scene;
    // The world is built long before most systems exist, so this is kept as a
    // back-reference rather than passed down: the scenery builders need to hand
    // things to the game (the range hands its paper to the target scorer).
    this.game = game;
    this.group = new THREE.Group();
    this.scene.add(this.group);
    this.collidables = []; // terrain + building chunks, for raycasts
    this.chunkMeshes = []; // building chunks, distance-culled each frame
    this.buildingMeshes = []; // chunk meshes carrying per-building damage records
    this.propMeshes = []; // instanced facade layers, stripped when a building dies
    this._refitQueue = new Set(); // chunk BVHs to rebuild after geometry crumbled
    this._savedDmg = this._loadSavedDamage(); // bi → hp, survives reloads
    this._dmgDirty = new Set(); // keys not yet pushed to the damage API
    this._collapses = []; // buildings currently falling (animated in update)
    this.fx = null; // FireFX — window fires + smoke, assigned by Game
    // bullet-only targets: instanced churches/mosques/houses + landmark models.
    // NOT in collidables — the plane's flight physics ignores them, as before.
    this.shootables = [];
    this.landmarkRecs = [];
    this.ready = false;
    this.attribution = '© OpenStreetMap contributors';
  }

  async load(url, onProgress = () => {}, spawnLatLon = null) {
    onProgress('იტვირთება რუკის მონაცემები…');
    // authoritative building HP from the damage DB, before any chunk is built
    await this._fetchRemoteDamage();
    const res = await fetch(url);
    if (!res.ok) throw new Error(`map fetch failed: HTTP ${res.status}`);
    const buf = await res.arrayBuffer();
    const bytes = new Uint8Array(buf);
    let text;
    if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
      const ds = new DecompressionStream('gzip');
      text = await new Response(new Blob([buf]).stream().pipeThrough(ds)).text();
    } else {
      text = new TextDecoder().decode(bytes); // server already un-gzipped it
    }
    onProgress('მუშავდება ქალაქი…');
    await nextFrame();
    const city = JSON.parse(text);
    this.meta = city.meta;
    this.attribution = city.meta.attribution || this.attribution;

    // Phase timing. The load is CPU-bound (the 15 MB city file arrives in
    // under 200 ms), so knowing WHICH build step costs what is the only way to
    // make it faster without guessing.
    const _t0 = performance.now();
    let _tPrev = _t0;
    const phase = (name) => {
      const now = performance.now();
      console.log(`load ${((now - _t0) / 1000).toFixed(1)}s  +${Math.round(now - _tPrev)
        .toString().padStart(5)}ms  ${name}`);
      _tPrev = now;
    };
    this._phase = phase;
    this._buildTerrainSampler(city.terrain);
    this.detailGrids = [];
    if (city.details) {
      for (const d of Object.values(city.details)) this._addDetailGrid(d);
    }
    if (city.country) {
      onProgress('იქმნება საქართველოს რელიეფი…');
      await nextFrame();
      this._buildCountry(city.country);
    }
    // Real Tbilisi road infrastructure (tools/qgis_roads.py). Loaded HERE,
    // before any ground is built: the tunnel cuttings it defines are part of
    // the terrain, and everything downstream reads them through heightAt.
    let roads = city.roads;
    let bridges = city.bridges ?? [];
    let tunnels = city.tunnels ?? [];
    this.roadMeta = null;
    try {
      const net = await fetchRoadNet('tbilisi/roadnet.json.gz');
      if (net) {
        const m = mergeRoadNet(city, net);
        roads = m.roads;
        bridges = m.bridges;
        tunnels = m.tunnels;
        this.roadMeta = m.meta;
        this.bridgeMeta = m.bridgeMeta;
        this.tunnelMeta = m.tunnelMeta;
        this._junctions = m.junctions;
        console.log(`roadnet: ${net.roads.length} real ways, ${m.junctions.length} junctions`);
      }
    } catch (e) {
      console.error('roadnet failed, using the country lists:', e);
    }
    // Where does the Mtkvari run between BUILT embankments? Where a street runs
    // along the bank -- that is what a quay is for. Out at the reservoir and up
    // the tributary streams the bank is natural, and giving those a granite
    // balustrade and candelabra lamps was flatly wrong.
    {
      const CELL = 70;
      const near = new Set();
      for (const road of roads) {
        if (road[0] < 6) continue; // tracks and alleys do not make an embankment
        const n = (road.length - 1) / 2;
        for (let i = 0; i < n; i++) {
          const cx = Math.floor(road[1 + i * 2] / 10 / CELL);
          const cz = Math.floor(road[2 + i * 2] / 10 / CELL);
          for (let ox = -1; ox <= 1; ox++) {
            for (let oz = -1; oz <= 1; oz++) near.add((cx + ox) * 100003 + (cz + oz));
          }
        }
      }
      this._roadNear = near;
      this._roadNearCell = CELL;
    }
    // The REAL Mtkvari (tools/fetch_river.py): full-precision OSM water
    // polygons plus the centreline carrying a MEASURED water-surface height
    // every 150 m (Google Elevation API). The sheet used to be traced from the
    // 30 m water-class raster and levelled by a percentile of nearby terrain,
    // which put it metres too high in places -- that is the water lying across
    // the embankment roads.
    // Real surface over the city's road tunnels (tools/fetch_tunnels.py).
    // The 30 m DEM smooths Tbilisi's ridges: over the Noe Zhordania tunnel it
    // reports 410-412 m where the ground really is 419-428 m, so the cover a
    // bore is sized by came out ~10 m short and the vault collapsed to its
    // 4.5 m minimum under the whole Old Town.
    this._tunnelGround = null;
    try {
      const tg = await fetch('tbilisi/tunnel_ground.json');
      if (tg.ok) {
        const td = await tg.json();
        this._tunnelGround = { cell: td.cell, g: new Map(Object.entries(td.ground)) };
        console.log(`tunnel ground: ${this._tunnelGround.g.size} measured cells`);
      }
    } catch { /* no tunnel_ground.json — the DEM stands */ }
    this._river = null;
    const rd_roads = roads;
    try {
      const rr = await fetch('tbilisi/river.json');
      if (rr.ok) {
        const rd = await rr.json();
        const centre = (rd.centre || []).filter((c) => c[2] != null);
        if (centre.length > 1) {
          // hash the stations so the level lookup is O(1) per sample
          const CELL = 400;
          const hash = new Map();
          centre.forEach((c, i) => {
            const cx = Math.floor(c[0] / CELL);
            const cz = Math.floor(c[1] / CELL);
            for (let ox = -1; ox <= 1; ox++) {
              for (let oz = -1; oz <= 1; oz++) {
                const k = (cx + ox) * 100003 + (cz + oz);
                if (!hash.has(k)) hash.set(k, []);
                hash.get(k).push(i);
              }
            }
          });
          // Rasterise the REAL river polygons into a coarse cell set. The
          // measured level must only speak for water the river actually
          // covers: keyed on "within 450 m of the centreline" it was also
          // claiming roads in the valley beside it, and drowning them under
          // as much as 11 m of sheet.
          const MCELL = 12;
          const mask = new Set();
          const cellsRC = [];
          const key = (cx, cz) => cx * 100003 + cz;
          const paint = (ring) => {
            let minz = Infinity;
            let maxz = -Infinity;
            for (const [, z] of ring) {
              if (z < minz) minz = z;
              if (z > maxz) maxz = z;
            }
            const r0 = Math.floor(minz / MCELL);
            const r1 = Math.floor(maxz / MCELL);
            for (let r = r0; r <= r1; r++) {
              const zc = r * MCELL + MCELL / 2;
              const xs = [];
              for (let i = 0; i < ring.length; i++) {
                const a = ring[i];
                const b2 = ring[(i + 1) % ring.length];
                if ((a[1] > zc) === (b2[1] > zc)) continue;
                xs.push(a[0] + (b2[0] - a[0]) * (zc - a[1]) / (b2[1] - a[1]));
              }
              xs.sort((m1, m2) => m1 - m2);
              for (let k = 0; k + 1 < xs.length; k += 2) {
                const c0 = Math.floor(xs[k] / MCELL);
                const c1 = Math.floor(xs[k + 1] / MCELL);
                // keep (c, r) as well: the packed key cannot be decoded back
                // for negative cells, and almost every cell here is negative
                for (let c = c0; c <= c1; c++) {
                  const kk = key(c, r);
                  if (!mask.has(kk)) { mask.add(kk); cellsRC.push(c, r); }
                }
              }
            }
          };
          for (const poly of rd.polys || []) {
            if (poly.kind !== 'river') continue; // lakes keep their own level
            if (poly.outer && poly.outer.length > 3) paint(poly.outer);
          }
          // Level per mask cell, so the carve below is two hash lookups.
          const levelOf = new Map();
          const nearestLevel = (x, z) => {
            const b = hash.get(Math.floor(x / CELL) * 100003 + Math.floor(z / CELL));
            if (!b) return null;
            let bd = Infinity;
            let v = null;
            for (const i of b) {
              for (const j of [i - 1, i]) {
                const a = centre[j];
                const c = centre[j + 1];
                if (!a || !c) continue;
                const ex = c[0] - a[0];
                const ez = c[1] - a[1];
                const l2 = ex * ex + ez * ez;
                if (l2 < 1e-6) continue;
                let t = ((x - a[0]) * ex + (z - a[1]) * ez) / l2;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                const d2 = (x - (a[0] + ex * t)) ** 2 + (z - (a[1] + ez * t)) ** 2;
                if (d2 < bd) { bd = d2; v = a[2] + (c[2] - a[2]) * t; }
              }
            }
            return v;
          };
          for (let i = 0; i < cellsRC.length; i += 2) {
            const c = cellsRC[i];
            const r = cellsRC[i + 1];
            const lv = nearestLevel(c * MCELL + MCELL / 2, r * MCELL + MCELL / 2);
            if (lv !== null) levelOf.set(key(c, r), lv);
          }
          // Do NOT carve under a road. OSM's riverbank polygon overlaps a
          // few riverside tracks, and since roads drape on heightAt the carve
          // pulled them down into the bed and drowned them. Punch the road
          // surface back out of the carve; the sheet still knows the level
          // there, it just no longer digs the ground away.
          let spared = 0;
          for (const road of rd_roads) {
            // Just the carriageway: the 12 m mask cells already add slack,
            // and a wide verge spared cells right at the bank line, which put
            // the dry margin back between the quay and the water.
            const half = road[0] / 2 + 1;
            const n = (road.length - 1) / 2;
            for (let i = 0; i < n - 1; i++) {
              const x1 = road[1 + i * 2] / 10;
              const z1 = road[2 + i * 2] / 10;
              const x2 = road[3 + i * 2] / 10;
              const z2 = road[4 + i * 2] / 10;
              const len = Math.hypot(x2 - x1, z2 - z1);
              if (len < 1e-3) continue;
              const steps = Math.ceil(len / (MCELL / 2));
              for (let k2 = 0; k2 <= steps; k2++) {
                const px = x1 + (x2 - x1) * (k2 / steps);
                const pz = z1 + (z2 - z1) * (k2 / steps);
                if (!mask.has(key(Math.floor(px / MCELL), Math.floor(pz / MCELL)))) continue;
                const rad = Math.ceil(half / MCELL);
                for (let ox = -rad; ox <= rad; ox++) {
                  for (let oz = -rad; oz <= rad; oz++) {
                    const kk = key(Math.floor(px / MCELL) + ox, Math.floor(pz / MCELL) + oz);
                    if (levelOf.delete(kk)) spared++;
                  }
                }
              }
            }
          }
          this._river = { centre, hash, cell: CELL, mask, mcell: MCELL,
            levelOf, polys: rd.polys || [] };
          this._riverCarve = levelOf.size > 0;
          if (spared) console.log(`river: ${spared} cells left uncarved under roads`);
          console.log(`river: ${centre.length} measured stations `
            + `${centre[0][2].toFixed(1)} m -> ${centre[centre.length - 1][2].toFixed(1)} m, `
            + `${this._river.polys.length} OSM water polygons, `
            + `${mask.size} river cells (${(mask.size * MCELL * MCELL / 1e4).toFixed(0)} ha), `
            + `${levelOf.size} carved`);
        }
      }
    } catch { /* no river.json — the old percentile level stands */ }
    this._indexBuildings(city.buildings ?? []);
    this._planTunnelCuts(tunnels, roads);
    phase('tunnel cuts');
    onProgress('იქმნება რელიეფი…');
    await nextFrame();
    this._buildTerrainMesh(city.terrain);
    phase('terrain mesh');
    if (city.details) {
      for (const d of Object.values(city.details)) {
        this._buildDetailMesh(d);
        await nextFrame();
      }
    }
    // OSM building:part volumes (tools/extract_parts.py) — the real stepped
    // massing of complex buildings. A hotel tower over a podium is ONE outline
    // in city.json and extrudes as a flat box; 479 Tbilisi buildings have
    // parts at different heights. Where a footprint has parts, the parts are
    // extruded INSTEAD of the box (Simple 3D Buildings convention).
    this._parts = null;
    try {
      const pr = await fetch('tbilisi/parts.json');
      if (pr.ok) {
        const pd = await pr.json();
        const g = new Map();
        for (const it of pd.items) {
          const n = (it.length - 2) / 2;
          let cx = 0;
          let cz = 0;
          for (let i = 0; i < n; i++) { cx += it[2 + i * 2]; cz += it[3 + i * 2]; }
          cx = cx / n / 10;
          cz = cz / n / 10;
          const k = `${Math.round(cx / 60)}_${Math.round(cz / 60)}`;
          if (!g.has(k)) g.set(k, []);
          g.get(k).push({ it, cx, cz });
        }
        this._parts = g;
      }
    } catch { /* no parts.json — plain extrusions */ }
    // REAL per-building facts from OSM (tools/extract_levels.py): storey count
    // for 17k buildings, plus roof shape / wall material / wall colour where
    // mapped. Heights otherwise come from the GHSL satellite raster, which is
    // a good average but never knows a building is exactly 9 storeys.
    this._facts = null;
    try {
      const fr = await fetch('tbilisi/levels.json');
      if (fr.ok) {
        const fd = await fr.json();
        const g = new Map();
        for (const it of fd.items) {
          const k = `${Math.round(it[0] / 100)}_${Math.round(it[1] / 100)}`;
          if (!g.has(k)) g.set(k, []);
          g.get(k).push(it);
        }
        this._facts = { grid: g, shapes: fd.shapes, materials: fd.materials };
      }
    } catch { /* no levels.json — GHSL heights stand */ }
    // roof-material tiles (tools/build_roof_materials.py): one bucket per tile,
    // grouped metal (pitched) / flat, picked per building by hash. Roofs are
    // what a flight game shows most — one shared sheet-metal texture made a
    // whole district repeat.
    this._roofNames = ['roof_flat', 'roof_metal'];
    this._roofFams = { flat: [0], metal: [1] };
    this._roofMan = null;
    try {
      const rr = await fetch('textures/roofs/roofs.json');
      if (rr.ok) {
        const rm = await rr.json();
        const names = Object.keys(rm);
        if (names.length) {
          this._roofMan = rm;
          this._roofNames = names;
          const fams = { flat: [], metal: [] };
          names.forEach((n, i) => (fams[rm[n].family] ?? fams.flat).push(i));
          if (fams.flat.length && fams.metal.length) this._roofFams = fams;
          else this._roofNames = ['roof_flat', 'roof_metal'];
        }
      }
    } catch { /* offline — the two photo roofs stay */ }
    // Soviet-block facade variants. mats[1] used to be picked per 1 km CHUNK
    // from four textures, so a whole district repeated one facade — and
    // style-1 housing is a THIRD of the city's wall area. These become real
    // per-building buckets instead (tools/cache/loggia_tiles.json, curated
    // from the pb photo set).
    this._loggiaKeys = [];
    try {
      const lr = await fetch('tbilisi/loggia_tiles.json');
      if (lr.ok) this._loggiaKeys = (await lr.json()).map((e) => e.key).filter(Boolean);
    } catch { /* none curated yet — the four classic variants still rotate */ }
    // NOTE: this must be set HERE, not where wallMats is built — that runs
    // after the building loop, and an undefined base made the bucket index
    // NaN, which threw inside the loop and hung the loading screen.
    this._loggiaBase = 0; // filled in once _wallNames is known, just below
    // wall-material tiles: names + families must be known before the buildings
    // are built (they decide the bucket count and per-building material)
    this._wallNames = ['brick_red', 'brick_dark', 'plaster_decay', 'plaster_yellow',
      'plaster_pink', 'panel_concrete'];
    this._wallFams = { brick: [4, 5], plaster: [6, 7, 8], panel: [9], stone: [] };
    try {
      const wr = await fetch('textures/walls/walls.json');
      if (wr.ok) {
        const wm = await wr.json();
        const names = Object.keys(wm);
        if (names.length) {
          this._wallNames = names;
          const fams = { brick: [], plaster: [], panel: [], stone: [] };
          names.forEach((n, i) => {
            const f = wm[n].family || (n.split('_')[0] === 'panel' ? 'panel' : 'plaster');
            (fams[f] ?? fams.plaster).push(4 + i);
          });
          this._wallFams = fams;
        }
      }
    } catch { /* offline — defaults above */ }
    this._loggiaBase = 4 + this._wallNames.length;
    // real malls replace their OSM extrusions — know the bids before building
    try {
      const mr = await fetch('tbilisi/malls.json');
      if (mr.ok) {
        this._mallList = await mr.json();
        this._mallBids = new Set(this._mallList.map((e) => e.bid));
      }
    } catch { /* no malls.json — extrusions stay */ }
    await this._loadPanelKit();   // must precede the loop: it decides the skip
    await this._loadHouseKit();
    await this._loadShedKit();
    await this._loadAptKit();
    await this._loadOutKit();
    await this._buildBuildings(city.buildings, onProgress);
    phase('buildings');
    onProgress('იგება გზები და მდინარე…');
    await nextFrame();
    let near = { x: 0, z: 0 };
    if (spawnLatLon) {
      near = {
        x: -(spawnLatLon.lon - this.meta.origin.lon) * this.meta.mlon,
        z: (spawnLatLon.lat - this.meta.origin.lat) * this.meta.mlat,
      };
    }
    if (spawnLatLon?.yaw !== undefined) {
      // exact spawn (e.g. on the TBS runway) — no snapping to a road
      this.spawn = { x: near.x, z: near.z, yaw: spawnLatLon.yaw };
    } else {
      this._computeSpawn(roads, near);
    }
    this._roadsRef = roads; // shot scripts stand the camera on a real road
    this._bridgesRef = bridges;
    this._buildRoads(roads);
    phase('roads');
    buildJunctions(this, this._junctions);
    buildSpans(this, bridges, this.bridgeMeta, roads, realBridgeZones(this));
    buildRealBridges(this);
    this._buildTunnels();
    phase('tunnels');
    this._buildWater(city.water);
    phase('water (lakes)');
    this._buildRiverSheet();
    phase('river sheet + banks');
    // ---- playable NOW; the scenery keeps arriving -------------------------
    // Ground, buildings, roads, tunnels and water are everything you need to
    // take off and not fall through the world. Trees, rocks, grass, signs and
    // the hand-built landmarks are decoration: blocking `ready` on them kept
    // the loading screen up for seconds with a perfectly flyable city sitting
    // behind it. They now build across the following frames instead.
    this.ready = true;
    phase('READY — playable');

    const scenery = [
      ['trees', () => this._buildTrees(roads)],
      ['rocks', () => this._buildRocks()],
      ['grass', () => this._buildGrass()],
      ['streetlights', () => this._buildStreetlights(roads)],
      ['road signs', () => this._buildRoadSigns(roads)],
      ['worship', () => this._buildWorship(city.churches ?? [], city.mosques ?? [])],
      ['landmarks', () => addLandmarks(this)],
      ['parks', () => addParks(this)],
      ['metro', () => addMetro(this)],
      ['airport', () => addAirport(this)],
      ['malls', () => addMalls(this)],
      ['infrastructure', () => addInfrastructure(this, roads)],
      ['shopfronts', () => buildShopfronts(this, city.buildings)],
    ];
    (async () => {
      for (const [name, fn] of scenery) {
        if (this.disposed) return;
        await nextFrame();            // one layer per frame: no long stall
        try {
          await fn();
        } catch (e) {
          console.error(`${name} failed:`, e);
        }
        phase(`+ ${name}`);
      }
      // Everything that can carry weight now exists. The hero was put back on
      // its feet at `ready`, BEFORE the airport slab, the mall floors or the
      // metro pavilions were built -- so anyone resuming on top of one of them
      // was stood on the bare terrain underneath it. Re-seat now.
      this._onSceneryReady?.();
    })();
    // Heavier detail-grove chestnuts load LAST, in the background, only after
    // the game is playable — so their download never stalls anything.
    setTimeout(() => this._buildGroves().catch((e) => console.warn('groves skipped:', e)), 2500);
  }

  // Every church in Georgia gets the real Georgian silhouette: stone cross
  // plan, drum, conical dome — instanced, one draw call for the whole country.
  _buildWorship(churches, mosques) {
    const colored = (geo, hex) => {
      // Non-index everything so indexed primitives (box/cyl/cone) and
      // non-indexed ExtrudeGeometry (gable) can merge into one template.
      const g2 = geo.index ? geo.toNonIndexed() : geo;
      const c = new THREE.Color(hex).convertSRGBToLinear();
      const n = g2.attributes.position.count;
      const arr = new Float32Array(n * 3);
      for (let i = 0; i < n; i++) arr.set([c.r, c.g, c.b], i * 3);
      g2.setAttribute('color', new THREE.BufferAttribute(arr, 3));
      return g2;
    };
    const T = (geo, x, y, z, rx = 0, rz = 0, ry = 0) => {
      if (rx) geo.rotateX(rx);
      if (rz) geo.rotateZ(rz);
      if (ry) geo.rotateY(ry);
      geo.translate(x, y, z);
      return geo;
    };
    // Pitched gable roof: triangular prism, ridge along Z, base at y=0.
    const gable = (w, h, len) => {
      const s = new THREE.Shape();
      s.moveTo(-w / 2, 0);
      s.lineTo(w / 2, 0);
      s.lineTo(0, h);
      s.closePath();
      const geo = new THREE.ExtrudeGeometry(s, { depth: len, bevelEnabled: false });
      geo.translate(0, 0, -len / 2);
      return geo;
    };
    const STONE = '#d9d0ba';  // Georgian tuff/limestone
    const ROOF = '#a3a8ae';   // silver-grey seamed metal (Sioni photos)
    const TENT = '#959ca4';
    const WIN = '#2b333d';    // dark window recess
    const GOLD = '#d9b13b';

    // World-planar UVs (1 unit per 3 m, same convention as the city walls)
    // so the photo stone tiles at true scale on every box face.
    const uvPlanar = (geo) => {
      const p = geo.attributes.position;
      const nrm = geo.attributes.normal;
      const uv = new Float32Array(p.count * 2);
      for (let i = 0; i < p.count; i++) {
        const nx = Math.abs(nrm.getX(i));
        const ny = Math.abs(nrm.getY(i));
        const nz = Math.abs(nrm.getZ(i));
        let u;
        let v;
        if (ny >= nx && ny >= nz) {
          u = p.getX(i) / 3;
          v = p.getZ(i) / 3;
        } else if (nx >= nz) {
          u = p.getZ(i) / 3;
          v = p.getY(i) / 3;
        } else {
          u = p.getX(i) / 3;
          v = p.getY(i) / 3;
        }
        uv[i * 2] = u;
        uv[i * 2 + 1] = v;
      }
      geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
      return geo;
    };

    // A real Georgian Orthodox church: stone cross-plan with gabled arms, a
    // 12-sided drum ringed by real carved arched windows, and the signature
    // pointed conical "tent" dome topped by a cross. One merged template,
    // instanced; walls and drum wear photo stone cut from Sioni Cathedral
    // (tools/fetch_facades.py → church_stone / church_arches).
    const stoneParts = [
      // cross-plan walls (nave along X, transept along Z) + crossing tower
      colored(uvPlanar(T(new THREE.BoxGeometry(20, 7.5, 9), 0, 3.75, 0)), STONE),
      colored(uvPlanar(T(new THREE.BoxGeometry(9, 7.5, 16), 0, 3.75, 0)), STONE),
      colored(uvPlanar(T(new THREE.BoxGeometry(9.5, 11.5, 9.5), 0, 5.75, 0)), STONE),
    ];
    // walled churchyard ringing the whole template (like every real Tbilisi
    // church ground): low tuff wall, west gate gap flanked by posts
    for (let k = 0; k < 16; k++) {
      const a = (k / 16) * Math.PI * 2;
      if (k === 8) continue; // gate opening toward the west door
      stoneParts.push(colored(uvPlanar(T(new THREE.BoxGeometry(5.95, 1.7, 0.5), Math.cos(a) * 15, 0.85, Math.sin(a) * 15, 0, 0, -(a + Math.PI / 2))), STONE));
    }
    for (const s of [-1, 1]) {
      const a = Math.PI + s * (Math.PI / 16);
      stoneParts.push(colored(uvPlanar(T(new THREE.BoxGeometry(1.1, 2.5, 1.1), Math.cos(a) * 15, 1.25, Math.sin(a) * 15)), STONE));
    }
    // the drum band carries the carved arched-window photo around it
    const archParts = [
      colored(T(new THREE.CylinderGeometry(3.4, 3.6, 6.5, 12, 1, true), 0, 14.75, 0), STONE),
    ];
    const colorParts = [
      // gabled roofs on each arm
      colored(T(gable(9, 3.6, 20), 0, 7.5, 0, 0, 0, Math.PI / 2), ROOF),
      colored(T(gable(9, 3.6, 16), 0, 7.5, 0), ROOF),
      colored(T(new THREE.CylinderGeometry(4, 4, 0.7, 12), 0, 18.2, 0), ROOF), // cornice
      colored(T(new THREE.CylinderGeometry(3.7, 3.7, 0.4, 12), 0, 11.4, 0), ROOF), // drum base ring
      colored(T(new THREE.ConeGeometry(3.9, 8.5, 12), 0, 22.8, 0), TENT),
      colored(T(new THREE.BoxGeometry(0.3, 2.6, 0.3), 0, 28.3, 0), GOLD),
      colored(T(new THREE.BoxGeometry(1.6, 0.3, 0.3), 0, 28.6, 0), GOLD),
    ];
    // tall arched windows: recessed dark slots on the arms (the drum's come
    // from the photo band)
    for (const [wx, wz, ry] of [[10.1, 0, Math.PI / 2], [-10.1, 0, Math.PI / 2], [0, 8.1, 0], [0, -8.1, 0]]) {
      colorParts.push(colored(T(new THREE.BoxGeometry(1.1, 3.4, 0.5), wx, 4.5, wz, 0, 0, ry), WIN));
    }
    // yard: paved 16-gon terrace and a ring of cypresses inside the wall
    colorParts.push(colored(T(new THREE.CylinderGeometry(14.8, 14.8, 0.9, 16), 0, 0.45, 0), '#b7b2a6'));
    for (const [deg, r, h] of [[28, 12, 6], [64, 11.5, 5], [116, 12.5, 6.5], [152, 11.8, 5],
      [206, 12, 6], [244, 12.4, 5.5], [296, 11.6, 6], [332, 12.2, 5]]) {
      const a = (deg / 180) * Math.PI;
      colorParts.push(colored(T(new THREE.ConeGeometry(1.35, h, 7), Math.cos(a) * r, h / 2 + 0.3, Math.sin(a) * r), '#2e5230'));
    }
    const churchGeo = mergeGeometries([
      mergeGeometries(stoneParts),
      mergeGeometries(archParts),
      mergeGeometries(colorParts),
    ], true); // three groups → [stoneMat, archMat, vertex-color mat]
    churchGeo.computeBoundingSphere();

    const mosqueGeo = mergeGeometries([
      colored(T(new THREE.BoxGeometry(16, 8, 16), 0, 4, 0), '#e8e2d4'),
      colored(T(new THREE.CylinderGeometry(6.8, 7.2, 1.5, 16), 0, 8.5, 0), '#d8d2c4'), // dome base
      colored(T(new THREE.SphereGeometry(6.6, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), 0, 9, 0), '#3f7a6a'),
      colored(T(new THREE.ConeGeometry(0.5, 2, 8), 0, 16.5, 0), GOLD), // dome finial
      colored(T(new THREE.CylinderGeometry(1.1, 1.4, 22, 10), 9.5, 11, 9.5), '#e8e2d4'), // minaret
      colored(T(new THREE.CylinderGeometry(1.5, 1.5, 1.2, 10), 9.5, 20, 9.5), '#d8d2c4'), // balcony
      colored(T(new THREE.ConeGeometry(1.6, 3.5, 10), 9.5, 23.5, 9.5), '#3f7a6a'),
    ]);
    mosqueGeo.computeBoundingSphere();
    const mat = new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true });
    // photo-stone materials: plain tuff tints until the textures stream in
    const stoneMat = new THREE.MeshLambertMaterial({ color: 0xd9d0ba, flatShading: true });
    const archMat = new THREE.MeshLambertMaterial({ color: 0xd9d0ba });
    const tl = new THREE.TextureLoader();
    const hookTex = (file, m2, rx, ry) =>
      tl.load(file, (t) => {
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.anisotropy = 4;
        t.repeat.set(rx, ry);
        m2.map = t;
        m2.color.set(0xffffff);
        m2.needsUpdate = true;
      });
    // stone: 1 tile per bays*3 m (planar church UVs are 1 unit per 3 m);
    // arches: ~2.5 carved bays per tile → 4 wraps ring the 12-gon drum
    hookTex('textures/facades/church_stone.jpg', stoneMat, 1 / 1.5, 1 / 1.2);
    hookTex('textures/facades/church_arches.jpg', archMat, 4, 1);
    const churchMats = [stoneMat, archMat, mat];
    const q = new THREE.Quaternion(); // churches face east already (nave on X axis)
    // hand-modeled landmarks (Sameba complex…) replace the generic instance
    const excl = EXCLUSIONS.map((e) => ({
      x: -(e.lon - this.meta.origin.lon) * this.meta.mlon,
      z: (e.lat - this.meta.origin.lat) * this.meta.mlat,
      r2: e.r * e.r,
    }));
    for (const [rawList, geo, kind] of [[churches, churchGeo, 'church'], [mosques, mosqueGeo, 'mosque']]) {
      // chunked like buildings so only nearby churches render — one country-wide
      // InstancedMesh drew every church in Georgia (main + shadow pass) each frame
      const list = rawList
        .filter(([xd, zd]) => !excl.some((e) => (xd / 10 - e.x) ** 2 + (zd / 10 - e.z) ** 2 < e.r2))
        .map(([xd, zd, area]) => ({
          x: xd / 10,
          z: zd / 10,
          s: THREE.MathUtils.clamp(Math.sqrt(area) / 16, 0.7, 2.4),
        }));
      if (!list.length) continue;
      const sv = new THREE.Vector3();
      this._placeChunkedInstanced(geo, kind === 'church' ? churchMats : mat, list, (m2, it) => {
        sv.set(it.s, it.s, it.s);
        m2.compose(new THREE.Vector3(it.x, this.heightAt(it.x, it.z) - 0.3, it.z), q, sv);
      }, 2000, true, 0, null, {
        kind,
        label: kind === 'church' ? 'ეკლესია' : 'მეჩეთი',
        hp: (it) => 620 + 420 * it.s, // stone: tougher than a house
      });
    }
  }

  // ---- world interface used by Game -----------------------------------------

  // Is a cull sweep worth running this frame? A chunk's state can only flip
  // when the camera MOVES, so standing still (or sitting in a menu) costs
  // nothing at all; a jump across the map -- the map screen, a respawn --
  // forces one unsliced full sweep so nothing is left attached behind you.
  _cullDue(camPos) {
    const p = this._cullPos ||= new THREE.Vector3(Infinity, 0, 0);
    const d2 = (camPos.x - p.x) ** 2 + (camPos.y - p.y) ** 2 + (camPos.z - p.z) ** 2;
    if (d2 > 250000) this._cullFull = true;        // 500 m: teleport
    else if (d2 < 4 && !this._cullFull) return false; // 2 m: nothing can flip
    p.copy(camPos);
    return true;
  }

  update(camPos, dt = 0.016) {
    if (this._waterTime) {
      // drives the two counter-drifting ripple layers in the water shader
      this._waterTime.value = performance.now() / 1000;
    } else if (this.waterMat?.normalMap) {
      const t = performance.now() / 1000;
      this.waterMat.normalMap.offset.set(t * 0.012, t * 0.008);
    }
    if (this._collapses.length) this._updateCollapses(dt);
    if (this._debris?.length) this._updateDebris(dt);
    if (camPos) this.shopSigns?.update(camPos, dt); // real-name shop signs near the camera
    // Only draw building chunks near the camera — the rest is hidden so the GPU
    // isn't rendering all of Georgia every frame. View distance grows with
    // altitude so the skyline is still there when you fly high.
    if (camPos && this.chunkMeshes.length && this._cullDue(camPos)) {
      const alt = Math.max(0, camPos.y - (this.heightAt(camPos.x, camPos.z) || 0));
      // `cullDist`: a hard override, in metres. The buildings have no imposter
      // — inside the radius they draw in full — so from altitude this one
      // number IS the frame time: 3.9 km of city over town is 111M triangles
      // in 2700 draw calls, and three's per-draw JS alone is then 100 ms+.
      // The cinema flypast sets it and raises the haze to hide the edge.
      const dist = this.cullDist || 3500 + alt * 3; // m
      const d2 = dist * dist;
      // `propScale`: the same argument for the PROPS. Balconies, air
      // conditioners, shopfronts, kerbs and signs carry generous radii —
      // 1000-1400 m — because on foot they ARE the city. From 142 m up they
      // are two or three pixels, and every chunk of them is a draw call.
      // Flying over town, 485 of the 582 attached chunks were props.
      const ps = this.propScale || 1;
      const ps2 = ps * ps;
      // Chunks live OUTSIDE the scene graph and only in-range ones get
      // attached. Merely keeping 80k hidden meshes as scene children cost
      // ~half the frame: the renderer, the shadow pass and N8AO each walk
      // every child (updateMatrixWorld + traverse) whether visible or not.
      const g = this.group;
      // ...but TESTING all 57k records every frame was itself 2.9 ms. The
      // sweep is sliced: each record is visited once every CULL_SLICES
      // frames, which at 60 fps is 66 ms of staleness -- a few metres of
      // travel against a 3500 m cull radius, invisible. _cullDue() decides
      // whether to run at all and which slice.
      const slices = this._cullFull ? 1 : CULL_SLICES;
      const list = this.chunkMeshes;
      const start = this._cullFull ? 0 : (this._cullSlice ??= 0);
      for (let i = start; i < list.length; i += slices) {
        const c = list[i];
        // grass and other short-range props carry their own tighter cull radius
        const md2 = c.maxDist ? c.maxDist * c.maxDist * ps2 : d2;
        const h2 = (camPos.x - c.cx) ** 2 + (camPos.z - c.cz) ** 2;
        const vis = h2 < md2;
        let hi = vis;
        let lo = false;
        if (c.lodD2) {
          // LOD switch on true 3D distance — flying high over a chunk should
          // show the cheap imposter, not thousands of full-detail frames
          const dy = camPos.y - c.cy;
          // the imposter band shrinks with the radius, so the props that are
          // still in range still hand over to their baked quad before they go
          const near = h2 + dy * dy < c.lodD2 * ps2;
          hi = vis && near;
          lo = vis && !near && c.lodMesh !== null;
        }
        if (hi !== c.hiOn) {
          c.hiOn = hi;
          if (hi) g.add(c.mesh);
          else g.remove(c.mesh);
        }
        if (lo !== c.loOn) {
          c.loOn = lo;
          if (lo) g.add(c.lodMesh);
          else g.remove(c.lodMesh);
        }
      }
      this._cullSlice = ((this._cullSlice ?? 0) + 1) % CULL_SLICES;
      this._cullFull = false;
    }
    // Rebuild the collision BVH of chunks whose buildings crumbled — one per
    // frame, so a strafing run over a dense district never hitches.
    if (this._refitQueue.size) {
      const mesh = this._refitQueue.values().next().value;
      this._refitQueue.delete(mesh);
      mesh.geometry.boundsTree?.refit();
    }
  }

  // Spatial hash over the collidable meshes: physics raycasts are short (a
  // wall sweep, wheels, a wingtip) or vertical, so testing every collidable in
  // the country — a matrix invert per mesh per ray — was ~8% of the frame.
  // 3×3 cells of 1500 m around the query point cover every ray the game casts.
  // Add one mesh to the collision set WITHOUT invalidating the spatial hash:
  // blast shells appear mid-flight, and rebuilding the hash over every
  // collidable in the country for each new one is a visible hitch.
  _addCollidable(mesh) {
    this.collidables.push(mesh);
    if (!this._colHash) return; // not built yet — it'll pick this up
    const CELL = 1500;
    if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
    const box = mesh.geometry.boundingBox.clone().applyMatrix4(mesh.matrixWorld);
    for (let ix = Math.floor(box.min.x / CELL); ix <= Math.floor(box.max.x / CELL); ix++) {
      for (let iz = Math.floor(box.min.z / CELL); iz <= Math.floor(box.max.z / CELL); iz++) {
        const k = `${ix}_${iz}`;
        let cell = this._colHash.get(k);
        if (!cell) this._colHash.set(k, (cell = []));
        cell.push(mesh);
      }
    }
    this._colN = this.collidables.length; // stays in sync, no rebuild
    this._colCache = null;                // cached cell lists now miss this mesh
  }

  // The inverse, for a prop that moves (src/devmode.js): the hash keys a mesh
  // by the cells its box covered when it was added, so a moved mesh has to
  // come out of those and go back in under its new box.
  _removeCollidable(mesh) {
    const i = this.collidables.indexOf(mesh);
    if (i >= 0) this.collidables.splice(i, 1);
    if (this._colHash) {
      for (const cell of this._colHash.values()) {
        const j = cell.indexOf(mesh);
        if (j >= 0) cell.splice(j, 1);
      }
    }
    this._colN = this.collidables.length;
    this._colCache = null;
  }

  // `rings` is how many 1500 m cells out from the query cell to include. The
  // default 3x3 covers every ray the physics casts (a wall sweep, wheels, a
  // foot probe) and the 1100 m guns; a longer ray MUST ask for more rings or
  // it silently misses what is out there -- the HUD's 3000 m boresight needs
  // rings 2. The result depends only on (rings, cell), so it is memoised:
  // foot.js alone asks six times a frame and the hero does not leave its cell.
  collidablesNear(x, z, rings = 1) {
    const CELL = 1500;
    if (this._colN !== this.collidables.length) {
      this._colN = this.collidables.length;
      this._colHash = new Map();
      this._colCache = null;
      this._colStamp = 0;
      const box = new THREE.Box3();
      for (const m of this.collidables) {
        if (!m.geometry.boundingBox) m.geometry.computeBoundingBox();
        box.copy(m.geometry.boundingBox).applyMatrix4(m.matrixWorld);
        const x0 = Math.floor(box.min.x / CELL);
        const x1 = Math.floor(box.max.x / CELL);
        const z0 = Math.floor(box.min.z / CELL);
        const z1 = Math.floor(box.max.z / CELL);
        for (let ix = x0; ix <= x1; ix++) {
          for (let iz = z0; iz <= z1; iz++) {
            const k = `${ix}_${iz}`;
            let cell = this._colHash.get(k);
            if (!cell) this._colHash.set(k, (cell = []));
            cell.push(m);
          }
        }
      }
    }
    const cx = Math.floor(x / CELL);
    const cz = Math.floor(z / CELL);
    const key = `${rings}:${cx}_${cz}`;
    const cached = (this._colCache ||= new Map()).get(key);
    if (cached) return cached;
    const stamp = ++this._colStamp;
    const out = [];
    for (let ix = cx - rings; ix <= cx + rings; ix++) {
      for (let iz = cz - rings; iz <= cz + rings; iz++) {
        const cell = this._colHash.get(`${ix}_${iz}`);
        if (!cell) continue;
        for (const m of cell) {
          if (m.userData._colStamp === stamp) continue; // wide meshes span cells
          m.userData._colStamp = stamp;
          out.push(m);
        }
      }
    }
    // Keyed by cell, so it stays correct until the collidable set itself
    // changes -- _addCollidable/_removeCollidable drop it.
    if (this._colCache.size > 64) this._colCache.clear();
    this._colCache.set(key, out);
    return out;
  }

  // ---- building damage ------------------------------------------------------
  // Every extruded OSM building carries hit points. Machine-gun fire walks it
  // through 10 visual "prototypes": stage 0 pristine → 9 a charred rubble
  // mound. The merged chunk geometry is rewritten in place — the building
  // loses height stage by stage, walls buckle with deterministic per-vertex
  // jitter, and soot darkens the vertex colors. Damage persists in
  // localStorage, so the city stays broken between flights.

  static DMG_KEY = 'tbfly_building_damage_v1';
  static HOLE_KEY = 'tbfly_building_holes_v1';
  // Rebuilding a tessellated shell costs a few thousand triangles and a BVH
  // build. In a shared city the hole list only grows, so cap how many are
  // restored at load — beyond this the building keeps its plain facade.
  static MAX_RESTORED_SHELLS = 150;
  static STAGE_H = [1, 0.97, 0.94, 0.9, 0.85, 0.79, 0.72, 0.63, 0.52, 0.12];

  _loadSavedDamage() {
    try {
      this._savedHoles = JSON.parse(localStorage.getItem(CityMap.HOLE_KEY)) || {};
    } catch {
      this._savedHoles = {};
    }
    try {
      return JSON.parse(localStorage.getItem(CityMap.DMG_KEY)) || {};
    } catch {
      return {};
    }
  }

  // Pull the authoritative damage map from the API (Express + Mongo). Merged
  // with localStorage by "more damage wins", so offline sessions aren't lost —
  // any offline-only damage is marked dirty and pushed back up.
  async _fetchRemoteDamage() {
    try {
      const ctl = new AbortController();
      const t = setTimeout(() => ctl.abort(), 2500);
      const res = await fetch('/api/damage', { signal: ctl.signal });
      clearTimeout(t);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const remote = await res.json();
      // rows arrive as { hp, holes } (older servers sent a bare hp number)
      for (const [key, row] of Object.entries(remote)) {
        const hp = typeof row === 'number' ? row : row.hp;
        const local = this._savedDmg[key];
        if (local === undefined || hp < local) this._savedDmg[key] = hp;
        // holes are shared damage: everyone flies the same broken city, so
        // the union of what the server and this browser know wins
        const rHoles = typeof row === 'number' ? null : row.holes;
        if (rHoles?.length) {
          const mine = this._savedHoles[key] || [];
          const seen = new Set(mine.map((h) => `${h[0]}_${h[1]}_${h[2]}`));
          const merged = mine.slice();
          for (const h of rHoles) {
            const k = `${h[0]}_${h[1]}_${h[2]}`;
            if (!seen.has(k)) {
              seen.add(k);
              merged.push(h);
            }
          }
          if (merged.length !== mine.length) this._savedHoles[key] = merged.slice(-40);
        }
      }
      for (const key of Object.keys(this._savedDmg)) {
        const row = remote[key];
        const rhp = typeof row === 'number' ? row : row?.hp;
        const rn = typeof row === 'number' ? 0 : (row?.holes?.length ?? 0);
        if (rhp === undefined || rhp > this._savedDmg[key]
          || rn < (this._savedHoles[key]?.length ?? 0)) this._dmgDirty.add(key);
      }
      if (this._dmgDirty.size) this._scheduleDamageSave();
      console.log(`damage DB: ${Object.keys(remote).length} buildings loaded, ${this._dmgDirty.size} to sync up`);
    } catch (e) {
      console.warn('damage API unreachable, using localStorage only:', e.message);
    }
  }

  // `rec` is optional: when given, the DB row also carries the real street
  // address and position of what was hit, so the damage table reads like a
  // list of places rather than a list of ids.
  _recordDamage(key, hp, rec = null) {
    this._savedDmg[key] = hp;
    this._dmgDirty.add(String(key));
    if (rec) {
      const addr = rec.cx !== undefined ? this.addressOf(rec) : (rec.addr || rec.label || rec.name);
      if (addr) {
        this._addrOf ??= {};
        this._addrOf[key] = {
          addr,
          x: Math.round((rec.cx ?? rec.x) * 10) / 10,
          z: Math.round((rec.cz ?? rec.z) * 10) / 10,
        };
      }
    }
    this._scheduleDamageSave();
  }

  // Blast holes ride the same store: the city is shared, so a wall someone
  // else blew open is open for everybody.
  _recordHoles(rec) {
    if (!rec.shell) return;
    const r1 = (v) => Math.round(v * 10) / 10;
    // the row carries hp too — never let a hole-only save write hp 0 and
    // resurrect the building as a rubble mound on the next flight
    if (this._savedDmg[rec.bi] === undefined) this._savedDmg[rec.bi] = rec.hp;
    this._savedHoles[rec.bi] = rec.shell.holes.map((h) => [r1(h.x), r1(h.y), r1(h.z), r1(h.r)]);
    // the DB row carries the real street address of what got hit
    this._addrOf ??= {};
    const addr = this.addressOf(rec);
    if (addr) this._addrOf[rec.bi] = { addr, x: r1(rec.cx), z: r1(rec.cz) };
    this._dmgDirty.add(String(rec.bi));
    this._scheduleDamageSave();
  }

  _scheduleDamageSave() {
    clearTimeout(this._dmgSaveT);
    this._dmgSaveT = setTimeout(() => {
      try {
        localStorage.setItem(CityMap.DMG_KEY, JSON.stringify(this._savedDmg));
        localStorage.setItem(CityMap.HOLE_KEY, JSON.stringify(this._savedHoles));
      } catch { /* storage full/blocked — damage just won't persist */ }
      this._pushDirtyDamage();
    }, 1200);
  }

  async _pushDirtyDamage() {
    if (!this._dmgDirty.size || this._dmgPushing) return;
    const keys = [...this._dmgDirty];
    this._dmgDirty.clear();
    this._dmgPushing = true;
    const damage = {};
    for (const k of keys) {
      const hp = this._savedDmg[k];
      if (hp === undefined) continue; // holes only, hp unknown — nothing to say
      const row = { hp };
      const holes = this._savedHoles?.[k];
      if (holes?.length) row.holes = holes;
      const a = this._addrOf?.[k];
      if (a) {
        row.addr = a.addr;
        row.x = a.x;
        row.z = a.z;
      }
      damage[k] = row;
    }
    try {
      const res = await fetch('/api/damage', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ damage }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      this._dmgPushing = false;
      // damage landed while we were pushing — flush it too
      if (this._dmgDirty.size) this._scheduleDamageSave();
    } catch {
      // API down — keep keys dirty; they ride along with the next save
      // (and localStorage already has them, so nothing is lost)
      for (const k of keys) this._dmgDirty.add(k);
      this._dmgPushing = false;
    }
  }

  _stageOf(rec) {
    // 0..8 damage looks + 9 = collapsed: ten states, new building → broken
    return rec.hp <= 0 ? 9 : Math.min(8, Math.floor((1 - rec.hp / rec.maxHp) * 9));
  }

  // Hit face → building record, via binary search over the chunk's contiguous
  // per-building vertex ranges.
  buildingFromFace(mesh, faceIndex) {
    const blds = mesh.userData.buildings;
    const index = mesh.geometry.index;
    if (!blds || !blds.length || !index || faceIndex === undefined) return null;
    const vid = index.array[faceIndex * 3];
    let lo = 0;
    let hi = blds.length - 1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      const r = blds[mid];
      if (vid < r.v0) hi = mid - 1;
      else if (vid >= r.v1) lo = mid + 1;
      else return r;
    }
    return null;
  }

  // Apply bullet damage to whatever a ray hit. Returns null for non-building
  // surfaces (terrain, bridges…), otherwise info for the shooter's FX/UI.
  // `shellRec` is passed when the ray hit a building's blast-damaged shell
  // (breakwall.js) instead of its chunk — same building, different mesh.
  damageBuildingHit(hit, dmg, shellRec = null) {
    const rec = shellRec
      ?? (hit.object.userData?.buildings ? this.buildingFromFace(hit.object, hit.faceIndex) : null);
    if (!rec) return null;
    if (!rec.chunk) rec.chunk = shellRec ? null : hit.object;
    const mesh = rec.chunk ?? hit.object;
    const wasDead = rec.hp <= 0;
    let changed = false;
    if (!wasDead) {
      rec.hp = Math.max(0, rec.hp - dmg);
      this._recordDamage(rec.bi, rec.hp, rec);
      const stage = this._stageOf(rec);
      changed = stage !== rec.stage;
      if (changed) {
        const prevStage = rec.stage;
        rec.stage = stage;
        if (stage === 9) {
          // the killing shot: the building FALLS — animated collapse inside a
          // dust avalanche (see _updateCollapses), not an instant swap
          this._startCollapse(mesh, rec, prevStage);
        } else {
          this._applyBuildingStage(mesh, rec);
          this._refitQueue.add(mesh);
          this._updateBurning(rec); // more stages = more windows on fire
          // windows/balconies/roof props above the sunken roofline go with it
          // — the glass among them SHATTERS on the way out
          this._stripFacadeProps([rec], rec.base + (rec.top - rec.base) * CityMap.STAGE_H[stage] - 1, true);
          if (stage >= 6) this._shedDebris(rec, stage); // chunks spall off the walls
          if (this.fx && stage >= 3) this.onShake?.(0.1, rec.cx, rec.cz);
        }
      }
    }
    return {
      rec,
      stage: rec.stage,
      changed,
      destroyed: rec.hp <= 0,
      hpFrac: rec.hp / rec.maxHp,
      addr: this.addressOf(rec),
      cx: rec.cx,
      cz: rec.cz,
      topNow: rec.base + (rec.top - rec.base) * CityMap.STAGE_H[rec.stage],
      span: Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz),
    };
  }

  // One entry point for every bullet: merged-chunk buildings, instanced
  // churches/mosques/houses (per-instance damage), and landmark models.
  damageHit(hit, dmg) {
    // a blast-damaged building is drawn by its own shell mesh — same record
    if (hit.object.userData?.shellOf) return this.damageBuildingHit(hit, dmg, hit.object.userData.shellOf);
    if (hit.object.userData?.buildings) return this.damageBuildingHit(hit, dmg);
    if (hit.object.userData?.destr && hit.instanceId !== undefined) {
      return this._damageInstanced(hit.object, hit.instanceId, dmg);
    }
    let o = hit.object;
    while (o) {
      if (o.userData?.landmarkRec) return this._damageLandmark(o.userData.landmarkRec, dmg);
      o = o.parent;
    }
    return null;
  }

  // ---- instanced destructibles (churches, mosques, suburban houses) --------
  // Damage recomposes the instance matrix from a pristine snapshot: squash Y,
  // tilt, sink — plus instance-color soot and fires. The template geometry's
  // BVH stays valid (it's local-space), so bullets and stages agree for free.

  static I_STAGE_H = [1, 0.975, 0.95, 0.92, 0.88, 0.83, 0.77, 0.69, 0.58, 0.13];

  _damageInstanced(mesh, id, dmg) {
    const rec = mesh.userData.destr.recs[id];
    if (!rec) return null;
    const wasDead = rec.hp <= 0;
    let changed = false;
    if (!wasDead) {
      rec.hp = Math.max(0, rec.hp - dmg);
      this._recordDamage(rec.key, rec.hp, rec);
      const stage = this._stageOf(rec);
      changed = stage !== rec.stage;
      if (changed) {
        const prevStage = rec.stage;
        rec.stage = stage;
        if (stage === 9) {
          this.fx?.clearBurning(rec.key);
          this._collapses.push({
            type: 'inst', mesh, id, rec, t: 0,
            dur: 1.4 + rec.h * 0.03,
            fromKh: CityMap.I_STAGE_H[prevStage],
            dustAcc: 0,
          });
          if (this.fx) {
            this.fx.explosion(rec.x, rec.y + rec.h * 0.5, rec.z, Math.max(4, rec.r * 0.8));
            this.fx.plume(rec.x, rec.y, rec.z, rec.r * 0.9, rec.h * 0.6);
            this.onShake?.(0.35, rec.x, rec.z);
            }
        } else {
          this._applyInstStage(mesh, id, rec);
          this._updateInstBurning(rec);
        }
      }
    }
    if (!rec.addr && this._addrGrid) {
      rec.addr = this.addressOf({ cx: rec.x, cz: rec.z, addr: null }) || rec.label;
    }
    return {
      rec,
      stage: rec.stage,
      changed,
      destroyed: rec.hp <= 0,
      hpFrac: rec.hp / rec.maxHp,
      addr: rec.addr || rec.label,
      cx: rec.x,
      cz: rec.z,
      topNow: rec.y + rec.h * CityMap.I_STAGE_H[rec.stage],
      span: rec.r * 2,
    };
  }

  _writeInstMatrix(mesh, id, rec, kh, tilt, wobble = 0) {
    if (!rec.origM) {
      rec.origM = new Float32Array(16);
      for (let i = 0; i < 16; i++) rec.origM[i] = mesh.instanceMatrix.array[id * 16 + i];
      const a = ((rec.x * 13.37 + rec.z * 7.7) % 6.283);
      rec.tiltX = Math.cos(a);
      rec.tiltZ = Math.sin(a);
    }
    const M = this._im4 ?? (this._im4 = new THREE.Matrix4());
    const R = this._im4b ?? (this._im4b = new THREE.Matrix4());
    const E = this._ieul ?? (this._ieul = new THREE.Euler());
    M.fromArray(rec.origM);
    E.set(rec.tiltX * tilt + wobble, 0, rec.tiltZ * tilt - wobble);
    R.makeRotationFromEuler(E);
    M.multiply(R);
    R.makeScale(1, kh, 1);
    M.multiply(R);
    mesh.setMatrixAt(id, M);
    mesh.instanceMatrix.needsUpdate = true;
  }

  _applyInstStage(mesh, id, rec) {
    const s = rec.stage;
    this._writeInstMatrix(mesh, id, rec, CityMap.I_STAGE_H[s], s === 9 ? 0.1 : s * 0.02);
    // instance-color soot (multiplies the template's vertex colors)
    if (!rec.origC) {
      rec.origC = new THREE.Color(1, 1, 1);
      if (mesh.instanceColor) mesh.getColorAt(id, rec.origC);
    }
    const dark = 1 - 0.085 * s;
    const c = this._icol ?? (this._icol = new THREE.Color());
    c.copy(rec.origC).multiplyScalar(dark);
    mesh.setColorAt(id, c);
    mesh.instanceColor.needsUpdate = true;
  }

  _updateInstBurning(rec) {
    const nH = rec.hot ? rec.hot.length / 3 : 0;
    if (!this.fx || rec.stage > 8 || (rec.stage < 1 && !nH)) {
      this.fx?.clearBurning(rec.key);
      return;
    }
    const kh = CityMap.I_STAGE_H[rec.stage];
    const n = rec.stage ? Math.min(8, 2 + rec.stage) : 0;
    const pts = new Float32Array((n + nH) * 3);
    for (let k = 0; k < n; k++) {
      const a = (k / n) * Math.PI * 2 + rec.x;
      pts[k * 3] = rec.x + Math.cos(a) * rec.r * 0.55;
      pts[k * 3 + 1] = rec.y + rec.h * kh * (0.35 + 0.45 * ((k * 2.7 + rec.z) % 1));
      pts[k * 3 + 2] = rec.z + Math.sin(a) * rec.r * 0.55;
    }
    for (let k = 0; k < nH; k++) {
      pts[(n + k) * 3] = rec.hot[k * 3];
      pts[(n + k) * 3 + 1] = rec.hot[k * 3 + 1];
      pts[(n + k) * 3 + 2] = rec.hot[k * 3 + 2];
    }
    this.fx.setBurning(rec.key, pts, Math.max(rec.stage / 9, nH ? 0.85 : 0), nH ? 1.7 : 1);
  }

  _finishInstCollapse(mesh, id, rec) {
    this._applyInstStage(mesh, id, rec); // stage 9: squashed to a low ruin
    this._spawnRubble({
      cx: rec.x, cz: rec.z, base: rec.y,
      top: rec.y + rec.h,
      minx: rec.x - rec.r, maxx: rec.x + rec.r,
      minz: rec.z - rec.r, maxz: rec.z + rec.r,
    });
    if (this.fx) {
      this.fx.plume(rec.x, rec.y, rec.z, rec.r * 1.1, rec.h * 0.4);
      this.fx.wave(rec.x, rec.y, rec.z, rec.r * 2);
      this.fx.stampScorch(rec.x, rec.y, rec.z, rec.r * 3.2);
      this.onShake?.(0.6, rec.x, rec.z);
      const pts = new Float32Array([rec.x, rec.y + rec.h * 0.13 + 1, rec.z]);
      this.fx.setSmolder(rec.key, pts);
      this.fx.setBurning(`${rec.key}:fire`, pts, 1, 2.2); // the ruin burns
    }
  }

  // Register per-instance damage records on a chunk-instanced layer. Called
  // by _placeChunkedInstanced when a `destr` option is given.
  _registerInstDestr(mesh, arr, destr) {
    if (!mesh.geometry.boundsTree) mesh.geometry.computeBoundsTree();
    if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
    const bb = mesh.geometry.boundingBox;
    const recs = arr.map((it) => {
      const sc = it.s ?? 1;
      const h = (bb.max.y - bb.min.y) * sc;
      const r = Math.max(2, ((bb.max.x - bb.min.x + bb.max.z - bb.min.z) / 4) * sc);
      return {
        key: `${destr.kind[0].toUpperCase()}:${Math.round(it.x)}:${Math.round(it.z)}`,
        label: destr.label,
        x: it.x,
        z: it.z,
        y: this.heightAt(it.x, it.z) ?? 0,
        h,
        r,
        hp: 0,
        maxHp: Math.round(destr.hp(it, h, r)),
        stage: 0,
        origM: null,
        origC: null,
        addr: null,
      };
    });
    for (const rec of recs) rec.hp = rec.maxHp;
    mesh.userData.destr = { recs };
    this.shootables.push(mesh);
    // restore persisted damage for this layer
    recs.forEach((rec, id) => {
      const saved = this._savedDmg[rec.key];
      if (saved === undefined || saved >= rec.maxHp) return;
      rec.hp = saved;
      rec.stage = this._stageOf(rec);
      this._applyInstStage(mesh, id, rec);
      if (rec.stage === 9) this._finishInstCollapse(mesh, id, rec);
      else this._updateInstBurning(rec);
    });
  }

  // ---- landmark damage ------------------------------------------------------
  // Hand-modeled landmarks (Sameba, opera, hotels…) are damaged as a whole:
  // the model squashes and tilts, materials char, fires climb it, and the
  // killing shot drops it into a dust plume like everything else.

  registerLandmark(root, name, { pose = true, immune = false } = {}) {
    const box = new THREE.Box3().setFromObject(root);
    if (box.isEmpty()) return;
    const size = box.getSize(new THREE.Vector3());
    const rec = {
      key: `L:${name}`,
      name,
      root,
      x: root.position.x,
      z: root.position.z,
      y: box.min.y,
      h: size.y,
      r: Math.max(4, (size.x + size.z) / 4),
      hp: 0,
      // landmarks are the toughest things in the city — Sameba does not fall
      // to a pair of rockets
      maxHp: Math.round(THREE.MathUtils.clamp(900 + size.y * 30 + size.x * size.z * 0.2, 1200, 6000)),
      stage: 0,
      matsCloned: false,
      // Indestructible: shots, rockets and crashes bounce off it and it never
      // takes a damage stage. For places the game needs to keep standing --
      // the warehouse is the player's own base.
      immune,
    };
    rec.hp = rec.maxHp;
    // pose: false — damage chars this one but never bends it. The lean-and-sag
    // pose is built for towers; on a long low building (the 152 m airport
    // warehouse) even 3° drops one end ~3 m, so the floor sinks under the
    // terrain and you end up looking at grass from inside the building.
    rec.noPose = pose === false;
    root.userData.landmarkRec = rec;
    this.landmarkRecs.push(rec);
    this.shootables.push(root);
    // An immune landmark must also SHED any wreck it is carrying from before
    // it was protected -- the warehouse was already recorded at stage 9
    // (collapsed), and immunity alone would have left it a permanent ruin.
    if (immune) {
      delete this._savedDmg[rec.key];
      delete this._savedHoles?.[rec.key];
      this._scheduleDamageSave();
    }
    const saved = this._savedDmg[rec.key];
    if (saved !== undefined && saved < rec.maxHp) {
      rec.hp = saved;
      rec.stage = this._stageOf(rec);
      this._applyLandmarkStage(rec);
      if (rec.stage === 9) this._finishLandmarkCollapse(rec);
      else this._updateLandmarkBurning(rec);
    }
  }

  _damageLandmark(rec, dmg) {
    if (rec.immune) return null;    // nothing marks it, nothing brings it down
    const wasDead = rec.hp <= 0;
    let changed = false;
    if (!wasDead) {
      rec.hp = Math.max(0, rec.hp - dmg);
      this._recordDamage(rec.key, rec.hp, rec);
      const stage = this._stageOf(rec);
      changed = stage !== rec.stage;
      if (changed) {
        const prevStage = rec.stage;
        rec.stage = stage;
        if (stage === 9) {
          this.fx?.clearBurning(rec.key);
          this._collapses.push({
            type: 'landmark', rec, t: 0,
            dur: 2.0 + rec.h * 0.025,
            fromKh: CityMap.I_STAGE_H[prevStage],
            dustAcc: 0,
          });
          if (this.fx) {
            this.fx.explosion(rec.x, rec.y + rec.h * 0.5, rec.z, Math.max(6, rec.r * 0.7));
            this.fx.plume(rec.x, rec.y, rec.z, rec.r * 0.7, rec.h * 0.6);
            this.onShake?.(0.5, rec.x, rec.z);
          }
        } else {
          this._applyLandmarkStage(rec);
          this._updateLandmarkBurning(rec);
        }
      }
    }
    return {
      rec,
      stage: rec.stage,
      changed,
      destroyed: rec.hp <= 0,
      hpFrac: rec.hp / rec.maxHp,
      addr: rec.name,
      cx: rec.x,
      cz: rec.z,
      topNow: rec.y + rec.h * CityMap.I_STAGE_H[rec.stage],
      span: rec.r * 2,
    };
  }

  _writeLandmarkPose(rec, kh, tilt, wobble = 0) {
    if (rec.tiltX === undefined) {
      const a = ((rec.x * 13.37 + rec.z * 7.7) % 6.283);
      rec.tiltX = Math.cos(a);
      rec.tiltZ = Math.sin(a);
      rec.origRotX = rec.root.rotation.x;
      rec.origRotZ = rec.root.rotation.z;
      rec.origScaleY = rec.root.scale.y;
    }
    rec.root.scale.y = rec.origScaleY * kh;
    rec.root.rotation.x = rec.origRotX + rec.tiltX * tilt + wobble;
    rec.root.rotation.z = rec.origRotZ + rec.tiltZ * tilt - wobble;
    rec.root.updateMatrixWorld(true);
  }

  _applyLandmarkStage(rec) {
    const s = rec.stage;
    // noPose landmarks keep their shape right up to the collapse itself (the
    // collapse animation drives _writeLandmarkPose on its own).
    if (!rec.noPose) this._writeLandmarkPose(rec, CityMap.I_STAGE_H[s], s === 9 ? 0.06 : s * 0.012);
    // char the materials — clone once so shared/cached materials stay clean
    if (!rec.matsCloned) {
      rec.matsCloned = true;
      rec.root.traverse((o) => {
        if (!o.isMesh) return;
        o.material = Array.isArray(o.material)
          ? o.material.map((m) => m.clone())
          : o.material.clone();
        const mats = Array.isArray(o.material) ? o.material : [o.material];
        for (const m of mats) {
          if (m.color) m.userData.origColor = m.color.getHex();
        }
      });
    }
    const dark = 1 - 0.085 * s;
    rec.root.traverse((o) => {
      if (!o.isMesh) return;
      const mats = Array.isArray(o.material) ? o.material : [o.material];
      for (const m of mats) {
        if (m.color && m.userData.origColor !== undefined) {
          m.color.setHex(m.userData.origColor).multiplyScalar(dark);
        }
      }
    });
  }

  _updateLandmarkBurning(rec) {
    const nH = rec.hot ? rec.hot.length / 3 : 0;
    if (!this.fx || rec.stage > 8 || (rec.stage < 1 && !nH)) {
      this.fx?.clearBurning(rec.key);
      return;
    }
    const kh = CityMap.I_STAGE_H[rec.stage];
    const n = rec.stage ? Math.min(12, 2 + rec.stage) : 0;
    const pts = new Float32Array((n + nH) * 3);
    for (let k = 0; k < n; k++) {
      const a = (k / n) * Math.PI * 2 + rec.x;
      pts[k * 3] = rec.x + Math.cos(a) * rec.r * 0.5;
      pts[k * 3 + 1] = rec.y + rec.h * kh * (0.25 + 0.55 * ((k * 2.7 + rec.z) % 1));
      pts[k * 3 + 2] = rec.z + Math.sin(a) * rec.r * 0.5;
    }
    for (let k = 0; k < nH; k++) {
      pts[(n + k) * 3] = rec.hot[k * 3];
      pts[(n + k) * 3 + 1] = rec.hot[k * 3 + 1];
      pts[(n + k) * 3 + 2] = rec.hot[k * 3 + 2];
    }
    this.fx.setBurning(rec.key, pts, Math.max(rec.stage / 9, nH ? 0.85 : 0), nH ? 1.7 : 1);
  }

  _finishLandmarkCollapse(rec) {
    this._applyLandmarkStage(rec);
    this._spawnRubble({
      cx: rec.x, cz: rec.z, base: rec.y,
      top: rec.y + rec.h,
      minx: rec.x - rec.r, maxx: rec.x + rec.r,
      minz: rec.z - rec.r, maxz: rec.z + rec.r,
    });
    if (this.fx) {
      this.fx.plume(rec.x, rec.y, rec.z, rec.r, rec.h * 0.4);
      this.fx.wave(rec.x, rec.y, rec.z, rec.r * 1.8);
      this.fx.stampScorch(rec.x, rec.y, rec.z, rec.r * 3);
      this.onShake?.(0.8, rec.x, rec.z);
      const pts = new Float32Array([rec.x, rec.y + rec.h * 0.13 + 1, rec.z]);
      this.fx.setSmolder(rec.key, pts);
      this.fx.setBurning(`${rec.key}:fire`, pts, 1, 2.6); // the ruin burns
    }
  }

  // The vertex writer every damage look goes through. kh scales height, jit
  // buckles walls, dark soots the paint, lean shears the whole shell sideways
  // (structural failure), phase re-rolls the jitter pattern — advancing it
  // every few frames during a collapse makes the building visibly shudder.
  _writeBuildingVerts(mesh, rec, kh, jit, dark, lean, phase = 0) {
    const posA = mesh.geometry.attributes.position;
    const colA = mesh.geometry.attributes.color;
    // copy pristine vertices once, on first damage — stages recompute from it
    if (!rec.orig) this._snapshot(mesh, rec);
    if (rec.leanX === undefined) {
      const a = (((rec.bi * 2654435761) >>> 0) % 1000) / 1000 * Math.PI * 2;
      rec.leanX = Math.cos(a);
      rec.leanZ = Math.sin(a);
    }
    // A blast-damaged building draws from its tessellated shell; the chunk
    // keeps only the roof, so both have to move together. Must come AFTER
    // leanX exists — the shear multiplies by it, and undefined poisons every
    // shell vertex with NaN.
    if (rec.shell) {
      writeShellVerts(rec, kh, jit, dark, lean, phase);
      // refit NOW, not on the queue: the very next shot at this wall is
      // raycast against this tree, and a stale one lets rounds pass through
      // geometry the player can plainly see. A few thousand triangles.
      rec.shell.mesh.geometry.boundsTree?.refit();
    }
    const hgt = Math.max(1, rec.top - rec.base);
    const fr = (n) => {
      const q = Math.sin(n) * 43758.5453;
      return q - Math.floor(q);
    };
    for (let v = rec.v0; v < rec.v1; v++) {
      const o = (v - rec.v0) * 3;
      const relY = Math.max(0, rec.orig.p[o + 1] - rec.base);
      const t = Math.min(1, relY / hgt); // 0 at the base, 1 at the roofline
      const a = jit * (0.35 + 0.65 * t); // upper floors crumble hardest
      const sh = relY * lean; // lean shear grows with height
      posA.array[v * 3] = rec.orig.p[o] + (fr(v * 12.9898 + phase * 7.31) - 0.5) * a + sh * rec.leanX;
      posA.array[v * 3 + 1] = rec.base + relY * kh + (fr(v * 78.233 + phase * 3.17) - 0.5) * a * 0.5;
      posA.array[v * 3 + 2] = rec.orig.p[o + 2] + (fr(v * 39.425 + phase * 5.53) - 0.5) * a + sh * rec.leanZ;
      colA.array[v * 3] = rec.orig.c[o] * dark;
      colA.array[v * 3 + 1] = rec.orig.c[o + 1] * dark;
      colA.array[v * 3 + 2] = rec.orig.c[o + 2] * dark;
    }
    posA.needsUpdate = true;
    colA.needsUpdate = true;
  }

  _applyBuildingStage(mesh, rec) {
    const s = rec.stage;
    this._writeBuildingVerts(
      mesh, rec,
      CityMap.STAGE_H[s],
      s === 9 ? 1.3 : s * 0.12, // buckle amplitude, m
      1 - 0.08 * s, // soot: stage 9 → 28% brightness
      s === 9 ? 0.05 : s * 0.028 // lean: a stage-8 shell tips visibly
    );
  }

  // ---- window fires ---------------------------------------------------------
  // Window cells recomputed from the pristine bottom ring (same 3.2 m grid the
  // facade props use) — flame emitters snap to real windows, not random air.

  _windowAnchors(rec) {
    if (rec.win) return rec.win;
    if (!rec.orig) return (rec.win = new Float32Array(0));
    const total = rec.v1 - rec.v0;
    const n = total % 3 === 1 ? (total - 1) / 3 : total / 3; // 3n (+1 if pitched apex)
    const h = rec.top - rec.base;
    const nF = Math.max(1, Math.floor(h / 3.2));
    const out = [];
    for (let i = 0; i < n && out.length < 3 * 64; i++) {
      const j = (i + 1) % n;
      const x1 = rec.orig.p[i * 3];
      const z1 = rec.orig.p[i * 3 + 2];
      const ex = rec.orig.p[j * 3] - x1;
      const ez = rec.orig.p[j * 3 + 2] - z1;
      const len = Math.hypot(ex, ez);
      if (len < 4) continue;
      let nx = -ez / len;
      let nz = ex / len;
      const mx = x1 + ex / 2;
      const mz = z1 + ez / 2;
      if ((mx - rec.cx) * nx + (mz - rec.cz) * nz < 0) {
        nx = -nx;
        nz = -nz;
      }
      const bays = Math.floor(len / 3.4);
      for (let k = 0; k < bays && out.length < 3 * 64; k++) {
        const t = (k + 0.5) / bays;
        const wx = x1 + ex * t + nx * 0.5;
        const wz = z1 + ez * t + nz * 0.5;
        const gy = this.heightAt(wx, wz);
        for (let f = 1; f < nF && out.length < 3 * 64; f++) {
          const wy = rec.base + 2 + f * 3.2 - 0.4;
          if (gy !== null && wy < gy + 0.8) continue; // buried on the slope
          out.push(wx, wy, wz);
        }
      }
    }
    rec.win = new Float32Array(out);
    return rec.win;
  }

  // Did this round land ON THE GLASS?
  //
  // nearWindow's 1.7 m ball is right for a 12.7 mm round or a warhead, which
  // takes the whole panel out whatever part of it they arrive on. For a 9 mm
  // round it is far too generous: window cells sit on a 3.4 x 3.2 m grid, so a
  // 1.7 m ball covers nearly the entire facade and every pistol shot into solid
  // brick came back as "that was a window". This is the actual opening -- about
  // 1.3 m wide and 1.6 m tall, which is what a Tbilisi window is.
  //
  // The anchors are parked 0.5 m PROUD of the wall (see _windowAnchors), and
  // the hit is on the wall plane, so the horizontal radius carries that offset.
  windowAt(mesh, rec, p, rh = 0.82, rv = 0.8) {
    if (rec.v0 === undefined) return false;
    if (!rec.orig && mesh?.geometry?.attributes?.position) {
      rec.orig = {
        p: mesh.geometry.attributes.position.array.slice(rec.v0 * 3, rec.v1 * 3),
        c: mesh.geometry.attributes.color.array.slice(rec.v0 * 3, rec.v1 * 3),
      };
    }
    const ax = this._windowAnchors(rec);
    const kh = CityMap.STAGE_H[rec.stage];   // anchors are pristine; walls crumble
    const rh2 = rh * rh;
    for (let i = 0; i < ax.length; i += 3) {
      const dx = ax[i] - p.x;
      const dz = ax[i + 2] - p.z;
      if (dx * dx + dz * dz > rh2) continue;
      const dy = rec.base + (ax[i + 1] - rec.base) * kh - p.y;
      if (Math.abs(dy) < rv) return true;
    }
    return false;
  }

  // Did this bullet land on a window pane? Window cells live on the 3.2 m
  // facade grid — a hit within ~1.7 m of a cell center counts as glass.
  nearWindow(mesh, rec, p) {
    if (rec.v0 === undefined) return false; // instanced/landmark recs — no cell grid
    if (!rec.orig && mesh.geometry?.attributes?.position) {
      rec.orig = {
        p: mesh.geometry.attributes.position.array.slice(rec.v0 * 3, rec.v1 * 3),
        c: mesh.geometry.attributes.color.array.slice(rec.v0 * 3, rec.v1 * 3),
      };
    }
    const ax = this._windowAnchors(rec);
    const r2 = 1.7 * 1.7;
    const kh = CityMap.STAGE_H[rec.stage]; // anchors are pristine; walls crumble
    for (let i = 0; i < ax.length; i += 3) {
      const dx = ax[i] - p.x;
      const dy = rec.base + (ax[i + 1] - rec.base) * kh - p.y;
      const dz = ax[i + 2] - p.z;
      if (dx * dx + dy * dy + dz * dz < r2) return true;
    }
    return false;
  }

  _updateBurning(rec) {
    if (!this.fx) return;
    const all = this._windowAnchors(rec);
    const nA = all.length / 3;
    const hot = rec.hot; // extra anchors punched into the hole by a warhead
    const nH = hot ? hot.length / 3 : 0;
    if ((!nA && !nH) || (rec.stage < 1 && !nH)) {
      this.fx.clearBurning(rec.bi);
      return;
    }
    // deterministic subset that only ever GROWS with stage — a window that
    // caught fire stays on fire
    let want = nA ? Math.min(nA, 28, 2 + Math.ceil(nA * rec.stage * 0.11)) : 0;
    let order = Array.from({ length: nA }, (_, i) => i);
    if (nH) {
      // A warhead sets fire to the ROOM IT HIT, not to the whole block: take
      // the windows nearest the blast instead of a subset spread over every
      // facade. Fire spreads from where it started, like it should.
      let hx = 0;
      let hy = 0;
      let hz = 0;
      for (let k = 0; k < nH; k++) {
        hx += hot[k * 3];
        hy += hot[k * 3 + 1];
        hz += hot[k * 3 + 2];
      }
      hx /= nH; hy /= nH; hz /= nH;
      const d2 = (i) => (all[i * 3] - hx) ** 2 + (all[i * 3 + 1] - hy) ** 2 + (all[i * 3 + 2] - hz) ** 2;
      order.sort((a, b) => d2(a) - d2(b));
      want = Math.min(nA, 4 + rec.stage * 2);
      // …and only the ones actually around the hole
      while (want > 0 && d2(order[want - 1]) > 26 * 26) want--;
    } else {
      order = order.sort((a, b) =>
        (((a * 2654435761 + rec.bi) >>> 0) % 9973) - (((b * 2654435761 + rec.bi) >>> 0) % 9973));
    }
    const sub = new Float32Array((want + nH) * 3);
    const kh = CityMap.STAGE_H[rec.stage]; // ride the crumbling roofline down
    for (let k = 0; k < want; k++) {
      sub[k * 3] = all[order[k] * 3];
      sub[k * 3 + 1] = rec.base + (all[order[k] * 3 + 1] - rec.base) * kh;
      sub[k * 3 + 2] = all[order[k] * 3 + 2];
    }
    for (let k = 0; k < nH; k++) {
      sub[(want + k) * 3] = hot[k * 3];
      sub[(want + k) * 3 + 1] = rec.base + (hot[k * 3 + 1] - rec.base) * kh;
      sub[(want + k) * 3 + 2] = hot[k * 3 + 2];
    }
    // a rocket hole burns hotter and with much bigger flames than window fires
    this.fx.setBurning(rec.bi, sub, Math.max(rec.stage / 9, nH ? 0.85 : 0), nH ? 1.7 : 1);
  }

  // A warhead detonating against a facade leaves a fire burning in the hole,
  // whatever the building's damage stage. Works for merged-chunk buildings,
  // instanced churches/houses and landmark models alike.
  igniteBlast(res, p, radius = 5) {
    const rec = res?.rec;
    if (!rec || !this.fx || rec.stage === 9) return;
    const n = 5;
    const hot = new Float32Array(n * 3);
    for (let k = 0; k < n; k++) {
      const a = (k / n) * Math.PI * 2;
      hot[k * 3] = p.x + Math.cos(a) * radius * 0.45 * Math.random();
      hot[k * 3 + 1] = p.y + (Math.random() - 0.35) * radius * 0.6;
      hot[k * 3 + 2] = p.z + Math.sin(a) * radius * 0.45 * Math.random();
    }
    // a second hit on the same building adds to the blaze, up to 15 anchors
    if (rec.hot && rec.hot.length < 45) {
      const merged = new Float32Array(rec.hot.length + hot.length);
      merged.set(rec.hot);
      merged.set(hot, rec.hot.length);
      rec.hot = merged;
    } else if (!rec.hot) {
      rec.hot = hot;
    }
    if (rec.v0 !== undefined) this._updateBurning(rec);
    else if (rec.root) this._updateLandmarkBurning(rec);
    else if (rec.key !== undefined) this._updateInstBurning(rec);
  }

  // ---- local blast damage ---------------------------------------------------
  // See breakwall.js: the first warhead a building takes swaps its one-quad
  // facades for a ~2 m tessellated shell, and every hole after that is just
  // "drop the cells inside the blast sphere". Holes are world-space spheres in
  // the building's PRISTINE frame, so they survive stage changes — and they
  // ride along to the damage DB, because everyone flies the same broken city.

  _snapshot(mesh, rec) {
    if (rec.orig?.u) return rec.orig;
    const a = mesh.geometry.attributes;
    rec.orig = {
      p: rec.orig?.p ?? a.position.array.slice(rec.v0 * 3, rec.v1 * 3),
      c: rec.orig?.c ?? a.color.array.slice(rec.v0 * 3, rec.v1 * 3),
      u: a.uv ? a.uv.array.slice(rec.v0 * 2, rec.v1 * 2) : null,
    };
    return rec.orig;
  }

  // Where does each building's geometry actually live in its chunk's index
  // buffer? One pass over the chunk answers it for every building at once,
  // and it's exact — walls are the triangles whose vertices are all in the
  // two wall rings, everything else is roof. (Reverse-engineering it from
  // bucket offsets is fragile: pb photo facades and loggia variants shuffle
  // the blocks around.) Built lazily: only chunks that take damage pay.
  _triRanges(mesh) {
    if (mesh.userData.triRanges) return mesh.userData.triRanges;
    const map = new Map();
    const idx = mesh.geometry.index.array;
    const tris = idx.length / 3;
    for (let f = 0; f < tris; f++) {
      const rec = this.buildingFromFace(mesh, f);
      if (!rec) continue;
      let e = map.get(rec.bi);
      if (!e) map.set(rec.bi, (e = { w0: Infinity, w1: -1, r0: Infinity, r1: -1 }));
      const wallTop = rec.v0 + Math.floor((rec.v1 - rec.v0) / 3) * 2;
      const i = f * 3;
      const wall = idx[i] < wallTop && idx[i + 1] < wallTop && idx[i + 2] < wallTop;
      if (wall) {
        if (i < e.w0) e.w0 = i;
        if (i + 3 > e.w1) e.w1 = i + 3;
      } else {
        if (i < e.r0) e.r0 = i;
        if (i + 3 > e.r1) e.r1 = i + 3;
      }
    }
    mesh.userData.triRanges = map;
    return map;
  }

  _ensureShell(rec) {
    if (rec.shell !== undefined) return rec.shell;
    const mesh = rec.chunk;
    rec.shell = null;
    if (!mesh || rec.wallMat === undefined || rec.v0 === undefined) return null;
    const orig = this._snapshot(mesh, rec);
    if (!orig.u) return null;
    const wallMat = Array.isArray(mesh.material) ? mesh.material[rec.wallMat] : mesh.material;
    if (!wallMat) return null;
    if (!this._linMat) {
      // Gutted interior. UNLIT on purpose: a lit interior catches the same
      // sun as the facade and reads as a bright white slab through the hole —
      // the one thing a blown-open building must never look like. Baked-dark
      // vertex colors keep it a black room from every angle.
      this._linMat = new THREE.MeshBasicMaterial({
        vertexColors: true, side: THREE.DoubleSide, fog: true,
      });
    }
    const shell = buildShell(rec, wallMat, this._linMat);
    if (!shell) return null;
    rec.shell = shell;
    // the original facade quads step aside — degenerate, not deleted, so the
    // chunk's index layout (and its BVH) stays exactly as long as it was
    const range = this._triRanges(mesh).get(rec.bi);
    rec.tri = range;
    const idx = mesh.geometry.index.array;
    for (let i = range?.w0 ?? 0; i < (range?.w1 ?? 0); i += 3) {
      idx[i + 1] = idx[i];
      idx[i + 2] = idx[i];
    }
    mesh.geometry.index.needsUpdate = true;
    this._refitQueue.add(mesh);
    shell.mesh.userData.shellOf = rec;
    shell.mesh.geometry.computeBoundingBox();
    shell.mesh.geometry.computeBoundsTree();
    this.group.add(shell.mesh, shell.lining);
    this._addCollidable(shell.mesh); // bullets, rockets and the plane
    if (rec.stage) this._applyBuildingStage(mesh, rec); // catch up to the damage
    return shell;
  }

  // Tear a hole where the warhead landed. Hitting a corner takes the corner
  // off, because the cells that leave are simply the ones inside the sphere.
  punchHole(rec, p, radius, save = true) {
    if (!rec || rec.stage === 9) return null;
    const shell = this._ensureShell(rec);
    if (!shell) return null;
    // holes are stored in pristine space — the shell may already be squashed
    const kh = CityMap.STAGE_H[rec.stage] || 1;
    const hole = {
      x: p.x,
      y: Math.min(rec.base + (p.y - rec.base) / kh, rec.top + radius * 0.4),
      z: p.z,
      r: radius,
    };
    shell.holes.push(hole);
    if (shell.holes.length > 40) shell.holes.shift();
    carve(shell);
    this._carveRoof(rec, hole);
    shell.mesh.geometry.boundsTree?.refit();
    if (save) this._recordHoles(rec);
    return hole;
  }

  // A hole that reaches the roofline takes the roof deck with it.
  _carveRoof(rec, hole) {
    const mesh = rec.chunk;
    const range = rec.tri;
    if (!mesh || !range || range.r1 < 0 || hole.y < rec.top - hole.r) return;
    const idx = mesh.geometry.index.array;
    const P = rec.orig.p;
    const r2 = (hole.r * 1.15) ** 2;
    let cut = false;
    for (let i = range.r0; i < range.r1; i += 3) {
      const a = idx[i];
      const b = idx[i + 1];
      const c = idx[i + 2];
      if (a === b) continue; // already gone
      let x = 0;
      let y = 0;
      let z = 0;
      let bad = false;
      for (const v of [a, b, c]) {
        const o = (v - rec.v0) * 3;
        if (o < 0 || o >= P.length) { bad = true; break; }
        x += P[o];
        y += P[o + 1];
        z += P[o + 2];
      }
      if (bad) continue;
      x /= 3; y /= 3; z /= 3;
      if ((x - hole.x) ** 2 + (y - hole.y) ** 2 + (z - hole.z) ** 2 > r2) continue;
      idx[i + 1] = a;
      idx[i + 2] = a;
      cut = true;
    }
    if (cut) {
      mesh.geometry.index.needsUpdate = true;
      this._refitQueue.add(mesh);
    }
  }

  // Everything bolted to the wall inside the blast — balconies, AC boxes,
  // shopfronts, signs — leaves with it. Glass panes are reported back so the
  // caller can spray shards.
  stripPropsAt(px, py, pz, radius) {
    const glass = [];
    const r2 = radius * radius;
    for (const p of this.propMeshes) {
      if ((p.cx - px) ** 2 + (p.cz - pz) ** 2 > (p.r + radius) ** 2) continue;
      const arr = p.mesh.instanceMatrix.array;
      let touched = false;
      for (let i = 0; i < p.mesh.count; i++) {
        const o = i * 16;
        const x = arr[o + 12];
        const y = arr[o + 13];
        const z = arr[o + 14];
        if (x === 0 && y === 0 && z === 0) continue;
        if ((x - px) ** 2 + (y - py) ** 2 + (z - pz) ** 2 > r2) continue;
        if (p.kind === 'glass') glass.push(x, y, z, arr[o + 8], arr[o + 10]);
        arr.fill(0, o, o + 16);
        touched = true;
      }
      if (touched) p.mesh.instanceMatrix.needsUpdate = true;
    }
    return glass;
  }

  // ---- collapse -------------------------------------------------------------

  _startCollapse(mesh, rec, prevStage) {
    this._collapses.push({
      mesh,
      rec,
      t: 0,
      dur: 2.2 + Math.min(1.3, (rec.top - rec.base) * 0.03), // tall = longer fall
      fromKh: CityMap.STAGE_H[prevStage],
      fromDark: 1 - 0.08 * prevStage,
      fromLean: prevStage * 0.028,
      dustAcc: 0,
    });
    // Windows, balconies and roof clutter can't ride the falling shell (they
    // live in shared instanced layers) — strip them NOW, masked by the blast.
    // Every pane on the building blows out at once: a curtain of glass.
    this._stripFacadeProps([rec], -Infinity, true);
    // window fires die with the shell; the fireball + dust take over
    if (this.fx) {
      this.fx.clearBurning(rec.bi);
      const span = Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz);
      const h = rec.top - rec.base;
      this.fx.explosion(rec.cx, rec.base + h * 0.55, rec.cz, Math.max(6, span * 0.45));
      this.onBlast?.(rec.cx, rec.base + h * 0.55, rec.cz, Math.max(8, span * 0.5));
      this.fx.plume(rec.cx, rec.base, rec.cz, span * 0.4, h * 0.5);
      this.onShake?.(0.45, rec.cx, rec.cz);
      this._launchDebris(rec, 8 + Math.round(span / 5), rec.base + h * 0.7);
    }
  }

  _updateCollapses(dt) {
    for (let i = this._collapses.length - 1; i >= 0; i--) {
      const c = this._collapses[i];
      c.t += dt;
      const p = Math.min(1, c.t / c.dur);
      const fall = p * p; // gravity: slow shear first, then it drops

      if (c.type === 'inst' || c.type === 'landmark') {
        // instanced church/house or landmark model: squash + tilt + wobble
        const khI = c.fromKh + (0.13 - c.fromKh) * fall;
        const tilt = 0.02 + 0.06 * p;
        const wobble = Math.sin(c.t * 31) * 0.03 * (1 - p);
        if (c.type === 'inst') this._writeInstMatrix(c.mesh, c.id, c.rec, khI, tilt, wobble);
        else this._writeLandmarkPose(c.rec, khI, tilt, wobble);
        c.dustAcc += dt;
        if (this.fx && c.dustAcc > 0.1) {
          c.dustAcc = 0;
          const r = c.rec;
          const topY = r.y + r.h * khI;
          for (let k = 0; k < 4; k++) {
            const a = Math.random() * Math.PI * 2;
            this.fx.spawnSmoke(r.x + Math.cos(a) * r.r * 0.6, topY, r.z + Math.sin(a) * r.r * 0.6, {
              size: 8 + Math.random() * 6,
              dark: 0.3,
              dur: 3.5,
              vx: Math.cos(a) * 2.5,
              vz: Math.sin(a) * 2.5,
              vy: 1.2,
              spread: 2,
            });
          }
        }
        if (p >= 1) {
          this._collapses.splice(i, 1);
          if (c.type === 'inst') this._finishInstCollapse(c.mesh, c.id, c.rec);
          else this._finishLandmarkCollapse(c.rec);
        }
        continue;
      }

      const kh = c.fromKh + (0.12 - c.fromKh) * fall;
      this._writeBuildingVerts(
        c.mesh, c.rec,
        kh,
        0.35 + 1.05 * p, // buckle hard on the way down
        c.fromDark + (0.28 - c.fromDark) * p,
        c.fromLean + 0.09 * p, // tips into the fall
        1 + Math.floor(c.t * 22) // re-rolled jitter every ~45 ms = shudder
      );
      // dust pours off every floor line as the shell drops through it —
      // a churning collar that hugs the roofline all the way down
      // secondary blasts tearing through the shell as it pancakes: gas, fuel,
      // whatever was inside — plus chunks spalling off the failing walls
      c.fireAcc = (c.fireAcc ?? 0) + dt;
      if (this.fx && c.fireAcc > 0.3 && p < 0.9) {
        c.fireAcc = 0;
        const r = c.rec;
        const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
        const topY = r.base + (r.top - r.base) * kh;
        const a = Math.random() * Math.PI * 2;
        const rr = span * (0.15 + Math.random() * 0.3);
        const by = r.base + (topY - r.base) * Math.random();
        this.fx.explosion(r.cx + Math.cos(a) * rr, by, r.cz + Math.sin(a) * rr, 2.5 + span * 0.12);
        this.onBlast?.(r.cx + Math.cos(a) * rr, by, r.cz + Math.sin(a) * rr, 3 + span * 0.1);
        this._launchDebris(r, 3, by);
      }
      c.dustAcc += dt;
      if (this.fx && c.dustAcc > 0.055) {
        c.dustAcc = 0;
        const r = c.rec;
        const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
        const topY = r.base + (r.top - r.base) * kh;
        for (let k = 0; k < 13; k++) {
          const a = Math.random() * Math.PI * 2;
          const rr = span * (0.42 + Math.random() * 0.2);
          this.fx.spawnSmoke(r.cx + Math.cos(a) * rr, topY + Math.random() * 2, r.cz + Math.sin(a) * rr, {
            size: 11 + Math.random() * 8,
            dark: 0.3,
            dur: 4,
            vx: Math.cos(a) * 3,
            vz: Math.sin(a) * 3,
            vy: 1.2,
            spread: 2.5,
          });
        }
      }
      if (p >= 1) {
        this._collapses.splice(i, 1);
        this._finishCollapse(c.mesh, c.rec);
      }
    }
  }

  _finishCollapse(mesh, rec) {
    this._applyBuildingStage(mesh, rec); // settle on the canonical rubble mound
    this._refitQueue.add(mesh);
    this._stripFacadeProps([rec]);
    this._spawnRubble(rec);
    if (this.fx) {
      this.fx.clearBurning(rec.bi);
      const span = Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz);
      const gy = this.heightAt(rec.cx, rec.cz) ?? rec.base;
      // ground slam: dust wall + expanding shockwave ring + permanent scorch
      this.fx.plume(rec.cx, rec.base, rec.cz, span * 0.75, (rec.top - rec.base) * 0.4);
      this.fx.wave(rec.cx, gy, rec.cz, span);
      this.fx.stampScorch(rec.cx, gy, rec.cz, span * 2.1);
      this.onBlast?.(rec.cx, gy + Math.max(3, span * 0.15), rec.cz, Math.max(7, span * 0.42));
      const mound = this._moundAnchors(rec);
      this.fx.setSmolder(rec.bi, mound);
      // the wreck BURNS: big ragged flames over the mound, not a polite wisp
      this.fx.setBurning(`${rec.bi}:fire`, mound, 1, 2.4);
      this.onShake?.(1.0, rec.cx, rec.cz);
      this._launchDebris(rec, 6 + Math.round(span / 6), rec.base + 2);
    }
  }

  // Concrete chunks blown off the building — ballistic, tumbling, they land
  // and STAY as part of the debris field.
  _launchDebris(rec, n, fromY) {
    this._initRubble().then(() => {
      if (!this._rubbleLayers?.length) return;
      const span = Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz);
      for (let k = 0; k < n; k++) {
        const layer = this._rubbleLayers[(Math.random() * this._rubbleLayers.length) | 0];
        if (layer.count >= 4000) continue;
        const a = Math.random() * Math.PI * 2;
        const rr = span * 0.3 * Math.random();
        const speed = 4 + Math.random() * 8;
        (this._debris ?? (this._debris = [])).push({
          layer,
          id: layer.count++,
          x: rec.cx + Math.cos(a) * rr,
          y: fromY + (Math.random() - 0.3) * 3,
          z: rec.cz + Math.sin(a) * rr,
          vx: Math.cos(a) * speed,
          vy: 2 + Math.random() * 6,
          vz: Math.sin(a) * speed,
          rx: Math.random() * Math.PI * 2,
          rz: Math.random() * Math.PI * 2,
          wx: (Math.random() - 0.5) * 9, // tumble rates
          wz: (Math.random() - 0.5) * 9,
          s: 0.8 + Math.random() * 1.3,
          bounced: false,
          hot: 0,
        });
      }
    });
  }

  // Chunks torn OUT OF A WALL by a warhead: they fly along the blast cone,
  // the hot ones trail smoke and embers all the way down, and every one of
  // them stays on the pavement where it lands.
  blastDebris(x, y, z, dir, n = 9) {
    this._initRubble().then(() => {
      if (!this._rubbleLayers?.length) return;
      for (let k = 0; k < n; k++) {
        const layer = this._rubbleLayers[(Math.random() * this._rubbleLayers.length) | 0];
        if (layer.count >= 4000) continue;
        const speed = 10 + Math.random() * 22;
        const a = Math.random() * Math.PI * 2;
        const spread = 0.55 + Math.random() * 0.8; // wide cone off the facade
        (this._debris ?? (this._debris = [])).push({
          layer,
          id: layer.count++,
          x: x + (Math.random() - 0.5) * 2,
          y: y + (Math.random() - 0.5) * 2,
          z: z + (Math.random() - 0.5) * 2,
          vx: (dir.x + Math.cos(a) * spread) * speed,
          vy: (dir.y + 0.35 + Math.random() * 0.6) * speed * 0.55,
          vz: (dir.z + Math.sin(a) * spread) * speed,
          rx: Math.random() * Math.PI * 2,
          rz: Math.random() * Math.PI * 2,
          wx: (Math.random() - 0.5) * 16,
          wz: (Math.random() - 0.5) * 16,
          s: 0.7 + Math.random() * 1.6,
          bounced: false,
          hot: k % 3 === 0 ? 0.01 : 0, // a third of them come out burning
        });
      }
    });
  }

  _updateDebris(dt) {
    const m = this._im4 ?? (this._im4 = new THREE.Matrix4());
    const q = this._dq ?? (this._dq = new THREE.Quaternion());
    const e = this._deul ?? (this._deul = new THREE.Euler());
    const v = this._dv3 ?? (this._dv3 = new THREE.Vector3());
    const s = this._ds3 ?? (this._ds3 = new THREE.Vector3());
    for (let i = this._debris.length - 1; i >= 0; i--) {
      const d = this._debris[i];
      d.vy -= 16 * dt; // heavier than real — video-game gravity reads better
      d.x += d.vx * dt;
      d.y += d.vy * dt;
      d.z += d.vz * dt;
      d.rx += d.wx * dt;
      d.rz += d.wz * dt;
      // burning chunks smoke the whole way down — the readable signature of
      // a wall that was blown apart rather than knocked over
      if (d.hot && this.fx) {
        d.hot -= dt;
        if (d.hot <= 0) {
          d.hot = 0.05;
          this.fx.spawnSmoke(d.x, d.y, d.z, { size: 2.6, dark: 0.6, dur: 1.9, vy: 1.4, spread: 0.5 });
          if (Math.random() < 0.5) this.fx.spawnEmber(d.x, d.y, d.z);
        }
      }
      const gy = (this.heightAt(d.x, d.z) ?? 0) + 0.15;
      let settled = false;
      if (d.y <= gy && d.vy < 0) {
        d.y = gy;
        if (!d.bounced && Math.abs(d.vy) > 3) {
          d.bounced = true; // one hard bounce, then it skids to rest
          d.vy = -d.vy * 0.3;
          d.vx *= 0.5;
          d.vz *= 0.5;
          this.fx?.spawnSmoke(d.x, gy + 0.4, d.z, { size: 3, dark: 0.28, dur: 1.6, vy: 1 });
        } else {
          settled = true;
        }
      }
      e.set(d.rx, 0, d.rz);
      q.setFromEuler(e);
      s.setScalar(d.s);
      m.compose(v.set(d.x, d.y, d.z), q, s);
      d.layer.setMatrixAt(d.id, m);
      // partial upload: only this instance's 16 floats, not the whole
      // 4000-slot buffer × 6 layers every frame
      d.layer.instanceMatrix.addUpdateRange(d.id * 16, 16);
      d.layer.instanceMatrix.needsUpdate = true;
      if (settled) this._debris.splice(i, 1); // final matrix stays in the layer
    }
  }

  _moundAnchors(rec) {
    const span = Math.max(4, Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz));
    const pts = [];
    const n = Math.min(8, 3 + Math.round(span / 8));
    for (let k = 0; k < n; k++) {
      const a = (k / n) * Math.PI * 2 + rec.bi;
      const r = span * 0.25 * Math.abs(Math.sin(k * 2.7 + rec.bi));
      pts.push(
        rec.cx + Math.cos(a) * r,
        rec.base + (rec.top - rec.base) * 0.12 + 1,
        rec.cz + Math.sin(a) * r
      );
    }
    return new Float32Array(pts);
  }

  // ---- rubble ---------------------------------------------------------------
  // Blender debris kit (tools/build_rubble_kit.py): six vertex-colored chunks
  // instanced into global layers, scattered over collapsed footprints.

  _initRubble() {
    if (this._rubblePromise) return this._rubblePromise;
    this._rubblePromise = new Promise((resolve) => {
      newGltfLoader().load(
        'models/rubble_kit.glb',
        (gltf) => {
          const layers = [];
          gltf.scene.updateMatrixWorld(true);
          gltf.scene.traverse((o) => {
            if (!o.isMesh) return;
            const geo = o.geometry.clone().applyMatrix4(o.matrixWorld);
            const mat = new THREE.MeshLambertMaterial({
              vertexColors: !!geo.getAttribute('color'),
              flatShading: true,
            });
            const inst = new THREE.InstancedMesh(geo, mat, 4000);
            inst.count = 0;
            inst.receiveShadow = true;
            inst.frustumCulled = false; // grows city-wide; tri cost stays tiny
            this.group.add(inst);
            layers.push(inst);
          });
          this._rubbleLayers = layers;
          resolve();
        },
        undefined,
        (e) => {
          console.warn('rubble kit skipped:', e);
          resolve();
        }
      );
    });
    return this._rubblePromise;
  }

  async _spawnRubble(rec) {
    await this._initRubble();
    if (!this._rubbleLayers?.length) return;
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const e = new THREE.Euler();
    const s = new THREE.Vector3();
    const pos = new THREE.Vector3();
    const w = rec.maxx - rec.minx;
    const d = rec.maxz - rec.minz;
    const span = Math.max(w, d);
    const n = Math.round(THREE.MathUtils.clamp((w * d) / 14, 10, 46));
    for (let k = 0; k < n; k++) {
      const layer = this._rubbleLayers[(Math.random() * this._rubbleLayers.length) | 0];
      if (layer.count >= 4000) continue;
      // mostly on the footprint, some thrown into a halo around it
      const fx = rec.minx - 2.5 + Math.random() * (w + 5);
      const fz = rec.minz - 2.5 + Math.random() * (d + 5);
      const gy = this.heightAt(fx, fz) ?? rec.base;
      const dc = Math.hypot(fx - rec.cx, fz - rec.cz);
      const mound = Math.max(0, (rec.top - rec.base) * 0.09 * (1 - dc / (span * 0.7)));
      e.set((Math.random() - 0.5) * 0.6, Math.random() * Math.PI * 2, (Math.random() - 0.5) * 0.6);
      q.setFromEuler(e);
      const sc = 0.9 + Math.random() * 1.7;
      s.set(sc, sc * (0.8 + Math.random() * 0.4), sc);
      m.compose(pos.set(fx, gy + mound, fz), q, s);
      const id = layer.count++;
      layer.setMatrixAt(id, m);
      layer.instanceMatrix.addUpdateRange(id * 16, 16);
      layer.instanceMatrix.needsUpdate = true;
    }
  }

  // Heavy damage (stages 6-8): chunks spall off the walls onto the pavement.
  _shedDebris(rec, stage) {
    if ((rec.shedAt ?? 0) >= stage) return;
    rec.shedAt = stage;
    this._initRubble().then(() => {
      if (!this._rubbleLayers?.length) return;
      const m = new THREE.Matrix4();
      const q = new THREE.Quaternion();
      const e = new THREE.Euler();
      const s = new THREE.Vector3();
      const pos = new THREE.Vector3();
      const w = rec.maxx - rec.minx;
      const d = rec.maxz - rec.minz;
      for (let k = 0; k < 4; k++) {
        const layer = this._rubbleLayers[(Math.random() * this._rubbleLayers.length) | 0];
        if (layer.count >= 4000) continue;
        // just outside a random wall
        const side = Math.random() < 0.5;
        const fx = side ? (Math.random() < 0.5 ? rec.minx - 1.2 : rec.maxx + 1.2) : rec.minx + Math.random() * w;
        const fz = side ? rec.minz + Math.random() * d : (Math.random() < 0.5 ? rec.minz - 1.2 : rec.maxz + 1.2);
        const gy = this.heightAt(fx, fz) ?? rec.base;
        e.set((Math.random() - 0.5) * 0.5, Math.random() * Math.PI * 2, (Math.random() - 0.5) * 0.5);
        q.setFromEuler(e);
        const sc = 0.6 + Math.random() * 0.9;
        s.set(sc, sc, sc);
        m.compose(pos.set(fx, gy, fz), q, s);
        const id = layer.count++;
        layer.setMatrixAt(id, m);
        layer.instanceMatrix.addUpdateRange(id * 16, 16);
        layer.instanceMatrix.needsUpdate = true;
      }
    });
  }

  // Hide the instanced facade props (windows, balconies, parapets, roof
  // clutter…) that would float in mid-air once their building crumbles: zero
  // out every instance inside the footprint. `cutY` strips only props ABOVE
  // that height — used at stages 1-8 so windows vanish with the sinking
  // roofline while the intact lower floors keep theirs.
  // `shatter` reports every GLASS pane it removes to onWindowsBlown, so the
  // windows visibly explode instead of quietly vanishing when a building
  // crumbles a stage or goes down.
  _stripFacadeProps(recs, cutY = -Infinity, shatter = false) {
    if (!recs.length) return;
    const blown = shatter ? [] : null;
    for (const p of this.propMeshes) {
      const near = recs.filter(
        (r) => (r.cx - p.cx) ** 2 + (r.cz - p.cz) ** 2 <
          (p.r + Math.max(r.maxx - r.minx, r.maxz - r.minz)) ** 2
      );
      if (!near.length) continue;
      const glass = shatter && p.kind === 'glass';
      const arr = p.mesh.instanceMatrix.array;
      let touched = false;
      for (let i = 0; i < p.mesh.count; i++) {
        const x = arr[i * 16 + 12];
        const y = arr[i * 16 + 13];
        const z = arr[i * 16 + 14];
        for (const r of near) {
          if (x < r.minx - 0.7 || x > r.maxx + 0.7 || z < r.minz - 0.7 || z > r.maxz + 0.7) continue;
          if (y < r.base - 8 || y > r.top + 6 || y < cutY) continue;
          // facing normal = the instance's local +Z (props are rotY only)
          if (glass && blown.length < 5 * 260) blown.push(x, y, z, arr[i * 16 + 8], arr[i * 16 + 10]);
          arr.fill(0, i * 16, i * 16 + 16); // degenerate matrix = invisible
          touched = true;
          break;
        }
      }
      if (touched) p.mesh.instanceMatrix.needsUpdate = true;
    }
    if (blown?.length) this.onWindowsBlown?.(blown, true);
  }

  // Blow every pane inside a blast sphere out of its facade layer and hand
  // the list back so the caller can spray shards and stamp the holes.
  // Returns a flat [x, y, z, nx, nz, ...] array, nearest pane first.
  blastWindows(px, py, pz, radius) {
    const out = [];
    const r2 = radius * radius;
    for (const p of this.propMeshes) {
      if (p.kind !== 'glass') continue;
      if ((p.cx - px) ** 2 + (p.cz - pz) ** 2 > (p.r + radius) ** 2) continue;
      const arr = p.mesh.instanceMatrix.array;
      let touched = false;
      for (let i = 0; i < p.mesh.count; i++) {
        const o = i * 16;
        const x = arr[o + 12];
        const y = arr[o + 13];
        const z = arr[o + 14];
        if (x === 0 && y === 0 && z === 0) continue; // already stripped
        const d2 = (x - px) ** 2 + (y - py) ** 2 + (z - pz) ** 2;
        if (d2 > r2) continue;
        out.push({ x, y, z, nx: arr[o + 8], nz: arr[o + 10], d2 });
        arr.fill(0, o, o + 16);
        touched = true;
      }
      if (touched) p.mesh.instanceMatrix.needsUpdate = true;
    }
    if (!out.length) return out;
    out.sort((a, b) => a.d2 - b.d2);
    const flat = [];
    for (const w of out) flat.push(w.x, w.y, w.z, w.nx, w.nz);
    return flat;
  }

  // Re-strip prop layers registered AFTER _applySavedDamage ran (shopfronts
  // load ~1.2 s post-ready): without this, a building collapsed in a saved
  // game would regrow floating shop modules/awnings/lightboxes on its ruins.
  _restripDamaged() {
    const collapsed = [];
    for (const mesh of this.buildingMeshes) {
      for (const rec of mesh.userData.buildings) {
        if (!rec.stage) continue;
        if (rec.stage === 9) collapsed.push(rec);
        else this._stripFacadeProps([rec], rec.base + (rec.top - rec.base) * CityMap.STAGE_H[rec.stage] - 1);
      }
    }
    if (collapsed.length) this._stripFacadeProps(collapsed);
  }

  // Saved damage is stored as absolute HP, so raising the HP table would make
  // every already-damaged building look far worse than the player left it (a
  // stage-4 block reading as a stage-8 ruin with its windows stripped). Once,
  // per browser, the old values are rescaled by the ratio of the old formula
  // to the new one and pushed back to the shared DB.
  static DMG_SCALE_KEY = 'tbfly_dmg_scale';
  static DMG_SCALE = '2';

  _migrateDamageScale(recs) {
    if (localStorage.getItem(CityMap.DMG_SCALE_KEY) === CityMap.DMG_SCALE) return;
    let n = 0;
    for (const rec of recs) {
      const hp = this._savedDmg[rec.bi];
      if (hp === undefined) continue;
      const h = rec.top - rec.base;
      const area = Math.max(1, (rec.maxx - rec.minx) * (rec.maxz - rec.minz) * 0.8);
      const oldMax = Math.round(THREE.MathUtils.clamp(60 + h * 8 + area * 0.1, 100, 800));
      const scaled = Math.round(THREE.MathUtils.clamp((hp / oldMax) * rec.maxHp, 0, rec.maxHp));
      if (scaled === hp) continue;
      this._savedDmg[rec.bi] = scaled;
      this._dmgDirty.add(String(rec.bi));
      n++;
    }
    localStorage.setItem(CityMap.DMG_SCALE_KEY, CityMap.DMG_SCALE);
    if (n) {
      console.log(`damage rescaled to the new HP table on ${n} buildings`);
      this._scheduleDamageSave();
    }
  }

  _applySavedDamage() {
    if (!Object.keys(this._savedDmg).length && !Object.keys(this._savedHoles ?? {}).length) return;
    this._migrateDamageScale(this.buildingMeshes.flatMap((m) => m.userData.buildings));
    const collapsed = [];
    let holed = 0;
    let skipped = 0; // holed buildings past the shell budget (see below)
    for (const mesh of this.buildingMeshes) {
      for (const rec of mesh.userData.buildings) {
        // blast holes from this browser or from anyone else's flight
        const holes = this._savedHoles?.[rec.bi];
        if (holes?.length && holed < CityMap.MAX_RESTORED_SHELLS) {
          rec.chunk = mesh;
          for (const h of holes) this.punchHole(rec, { x: h[0], y: h[1], z: h[2] }, h[3], false);
          if (rec.shell) {
            holed++;
            for (const h of holes) this.stripPropsAt(h[0], h[1], h[2], h[3]);
          }
        } else if (holes?.length) {
          skipped++;
        }
        const hp = this._savedDmg[rec.bi];
        if (hp === undefined || hp >= rec.maxHp) continue;
        rec.chunk = mesh;
        rec.hp = hp;
        rec.stage = this._stageOf(rec);
        this._applyBuildingStage(mesh, rec);
        if (rec.stage === 9) {
          collapsed.push(rec);
          this._spawnRubble(rec);
          if (this.fx) {
            this.fx.setSmolder(rec.bi, this._moundAnchors(rec));
            const gy = this.heightAt(rec.cx, rec.cz) ?? rec.base;
            this.fx.stampScorch(rec.cx, gy, rec.cz, Math.max(rec.maxx - rec.minx, rec.maxz - rec.minz) * 2.1);
          }
        } else {
          this._updateBurning(rec); // damaged buildings keep burning
          // strip window/roof props above the restored (sunken) roofline
          this._stripFacadeProps([rec], rec.base + (rec.top - rec.base) * CityMap.STAGE_H[rec.stage] - 1);
        }
        this._refitQueue.add(mesh);
      }
    }
    this._stripFacadeProps(collapsed);
    console.log(`restored damage on ${Object.keys(this._savedDmg).length} buildings `
      + `(${collapsed.length} collapsed, ${holed} blasted open`
      + `${skipped ? `, ${skipped} holed buildings past the ${CityMap.MAX_RESTORED_SHELLS}-shell budget` : ''})`);
  }

  // ---- real street addresses ------------------------------------------------
  // tools/extract_addresses.py → addresses.json.gz: OSM addr:street +
  // addr:housenumber (fallback: building name, nearest named street) matched
  // to damage records by centroid at hit time.

  async _loadAddresses() {
    try {
      const res = await fetch('tbilisi/addresses.json.gz');
      if (!res.ok) return;
      const buf = await res.arrayBuffer();
      const bytes = new Uint8Array(buf);
      let text;
      if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
        const ds = new DecompressionStream('gzip');
        text = await new Response(new Blob([buf]).stream().pipeThrough(ds)).text();
      } else {
        text = new TextDecoder().decode(bytes);
      }
      const data = JSON.parse(text);
      // spatial hash: 8 m cells over building centroids (coords arrive in dm)
      const grid = new Map();
      for (const [xd, zd, si, num] of data.items) {
        const x = xd / 10;
        const z = zd / 10;
        const k = `${Math.round(x / 8)}_${Math.round(z / 8)}`;
        if (!grid.has(k)) grid.set(k, []);
        grid.get(k).push({ x, z, label: num ? `${data.streets[si]} ${num}` : data.streets[si] });
      }
      this._addrGrid = grid;
      console.log(`addresses: ${data.items.length} buildings, ${data.streets.length} streets`);
    } catch (e) {
      console.warn('addresses skipped:', e);
    }
  }

  addressOf(rec) {
    if (rec.addr !== null) return rec.addr;
    if (!this._addrGrid) return '';
    const gi = Math.round(rec.cx / 8);
    const gj = Math.round(rec.cz / 8);
    let best = '';
    let bestD = 12 * 12; // centroids of the same building match within cm
    for (let di = -1; di <= 1; di++) {
      for (let dj = -1; dj <= 1; dj++) {
        for (const e of this._addrGrid.get(`${gi + di}_${gj + dj}`) || []) {
          const d = (e.x - rec.cx) ** 2 + (e.z - rec.cz) ** 2;
          if (d < bestD) {
            bestD = d;
            best = e.label;
          }
        }
      }
    }
    rec.addr = best; // cache, '' when nothing matched
    return best;
  }

  setResolution() {}

  dispose() {
    this.group.traverse((o) => {
      if (o.geometry) {
        o.geometry.disposeBoundsTree?.();
        o.geometry.dispose();
      }
      if (o.material) o.material.dispose();
    });
    // chunk meshes out of attach range aren't group children — free them too
    for (const c of this.chunkMeshes) {
      for (const m of [c.mesh, c.lodMesh]) {
        if (!m || m.parent) continue;
        m.geometry.disposeBoundsTree?.();
        m.geometry.dispose();
        const mats = Array.isArray(m.material) ? m.material : [m.material];
        for (const mm of mats) mm.dispose();
      }
    }
    this.scene.remove(this.group);
    this._bakeR?.dispose();
    this._bakeR = null;
  }

  // ---- terrain -------------------------------------------------------------

  _buildTerrainSampler(t) {
    const raw = base64ToBytes(t.data);
    const full = new Uint16Array(raw.buffer, raw.byteOffset, raw.byteLength / 2);
    // Sample and render on the same half-res grid so roads/buildings sit
    // exactly on the visible surface.
    const w2 = Math.floor((t.w - 1) / 2) + 1;
    const h2 = Math.floor((t.h - 1) / 2) + 1;
    const heights = new Float32Array(w2 * h2);
    const classes = new Uint8Array(w2 * h2);
    const cls = t.classes ? base64ToBytes(t.classes) : null;
    for (let r = 0; r < h2; r++) {
      for (let c = 0; c < w2; c++) {
        const fr = Math.min(r * 2, t.h - 1);
        const fc = Math.min(c * 2, t.w - 1);
        heights[r * w2 + c] = full[fr * t.w + fc] / 10;
        if (cls) classes[r * w2 + c] = cls[fr * t.w + fc];
      }
    }
    this.grid = { w: w2, h: h2, step: t.step * 2, minx: t.minx, minz: t.minz, heights, classes };
    this._carveWater();
  }

  // Flatten and sink river/water cells so the tinted channel reads as calm
  // water instead of bumpy terrain.
  _carveWater() {
    const g = this.grid;
    for (let pass = 0; pass < 2; pass++) {
      const src = g.heights.slice();
      for (let r = 1; r < g.h - 1; r++) {
        for (let c = 1; c < g.w - 1; c++) {
          const i = r * g.w + c;
          if (g.classes[i] !== 2) continue;
          const nb = (src[i - 1] + src[i + 1] + src[i - g.w] + src[i + g.w]) / 4;
          g.heights[i] = Math.min(src[i], nb) - (pass === 0 ? 1.2 : 0.2);
        }
      }
    }
    // Sink every water cell to its local channel minimum: the polygon water
    // surface interpolates between sampled vertices, so any bed bump left
    // above that plane would poke through as a green island mid-river.
    {
      const src = g.heights.slice();
      for (let r = 0; r < g.h; r++) {
        for (let c = 0; c < g.w; c++) {
          const i = r * g.w + c;
          if (g.classes[i] !== 2) continue;
          let m = src[i];
          for (let dr = -2; dr <= 2; dr++) {
            for (let dc = -2; dc <= 2; dc++) {
              const rr = r + dr;
              const cc = c + dc;
              if (rr < 0 || cc < 0 || rr >= g.h || cc >= g.w) continue;
              m = Math.min(m, src[rr * g.w + cc]);
            }
          }
          g.heights[i] = m - 1.6;
        }
      }
    }
  }

  // Raise the terrain relief OVER each tunnel so the passage runs UNDERGROUND
  // with the mountain intact on top, while leaving the two mouths open at road
  // level. Runs on the height grid BEFORE the terrain mesh is built, so both
  // the visible relief and its collision get the cover. Where a tunnel crosses
  // flat ground this reads as the low hill/embankment a real tunnel bores under.
  _coverTunnels(tunnels) {
    const g = this.grid;
    const TUBE_H = 14; // clear tube height (floor → crown)
    const COVER = 3; // rock kept above the crown
    const MOUTH = 22; // metres of open approach at each end
    for (const tn of tunnels) {
      const n = (tn.length - 1) / 2;
      if (n < 2) continue;
      const hw = Math.max(9, tn[0] / 2 + 3);
      const pad = hw + 6;
      const px = [], pz = [], ps = [];
      let L = 0;
      for (let i = 0; i < n; i++) {
        const x = tn[1 + i * 2] / 10, z = tn[2 + i * 2] / 10;
        if (i > 0) L += Math.hypot(x - px[i - 1], z - pz[i - 1]);
        px.push(x); pz.push(z); ps.push(L);
      }
      if (L < 8) continue;
      const y0 = this._sampleGrid(g, px[0], pz[0]);
      const y1 = this._sampleGrid(g, px[n - 1], pz[n - 1]);
      let minx = Infinity, maxx = -Infinity, minz = Infinity, maxz = -Infinity;
      for (let i = 0; i < n; i++) { minx = Math.min(minx, px[i]); maxx = Math.max(maxx, px[i]); minz = Math.min(minz, pz[i]); maxz = Math.max(maxz, pz[i]); }
      const c0 = Math.max(0, Math.floor((minx - pad - g.minx) / g.step));
      const c1 = Math.min(g.w - 1, Math.ceil((maxx + pad - g.minx) / g.step));
      const r0 = Math.max(0, Math.floor((minz - pad - g.minz) / g.step));
      const r1 = Math.min(g.h - 1, Math.ceil((maxz + pad - g.minz) / g.step));
      for (let r = r0; r <= r1; r++) {
        for (let c = c0; c <= c1; c++) {
          const wx = g.minx + c * g.step, wz = g.minz + r * g.step;
          let bd = Infinity, bs = 0;
          for (let i = 0; i < n - 1; i++) {
            const ax = px[i], az = pz[i], dx = px[i + 1] - ax, dz = pz[i + 1] - az;
            const l2 = dx * dx + dz * dz || 1;
            let t = ((wx - ax) * dx + (wz - az) * dz) / l2; t = Math.max(0, Math.min(1, t));
            const d = Math.hypot(wx - (ax + dx * t), wz - (az + dz * t));
            if (d < bd) { bd = d; bs = ps[i] + t * (ps[i + 1] - ps[i]); }
          }
          if (bd > pad) continue;
          const floor = y0 + (y1 - y0) * (bs / L);
          const crown = floor + TUBE_H + COVER;
          const f = Math.min(1, Math.min(bs, L - bs) / MOUTH); // 0 at mouths → 1 inside
          const side = Math.max(0, 1 - Math.max(0, bd - hw) / 6); // blend to real terrain sideways
          const idx = r * g.w + c;
          const target = crown * f * side + g.heights[idx] * (1 - f * side);
          if (target > g.heights[idx]) g.heights[idx] = target;
        }
      }
    }
  }

  _sampleGrid(g, x, z) {
    const fx = THREE.MathUtils.clamp((x - g.minx) / g.step, 0, g.w - 1.001);
    const fz = THREE.MathUtils.clamp((z - g.minz) / g.step, 0, g.h - 1.001);
    const c = Math.floor(fx);
    const r = Math.floor(fz);
    const dx = fx - c;
    const dz = fz - r;
    const i = r * g.w + c;
    const h00 = g.heights[i];
    const h10 = g.heights[i + 1];
    const h01 = g.heights[i + g.w];
    const h11 = g.heights[i + g.w + 1];
    // Sample the TRIANGLE the terrain mesh actually draws, not the bilinear
    // patch. Every grid mesh here is indexed (a, a+w, a+1)(a+1, a+w, a+w+1),
    // so the cell is split on the h10..h01 diagonal. Bilinear differs from
    // that plane by dx*dz*(h00+h11-h10-h01) — on a 60 m saddle cell in the
    // Tbilisi hills that is metres, and it is negative exactly where the
    // ground bulges: the road ribbon was laid 0.8 m over a surface that is
    // not the one on screen, so the grass swallowed the asphalt (2.3 % of
    // city road samples, up to 3.9 m deep). Same plane in, same plane out.
    if (dx + dz <= 1) return h00 + dx * (h10 - h00) + dz * (h01 - h00);
    return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11);
  }

  _inGrid(g, x, z) {
    return (
      x >= g.minx && x <= g.minx + (g.w - 1) * g.step &&
      z >= g.minz && z <= g.minz + (g.h - 1) * g.step
    );
  }

  // What KIND of ground is at this spot: 0 bare earth, 1 grass/woods, 2 water.
  // The terrain mesh is one object wearing one material, so a bullet landing on
  // it has no other way to know whether it just hit a lawn, a dirt slope or the
  // bed of the Mtkvari (see ballistics.js classifySurface).
  groundClassAt(x, z) {
    const g = this.grid;
    if (!g?.classes) return 0;
    const c = Math.round((x - g.minx) / g.step);
    const r = Math.round((z - g.minz) / g.step);
    if (r < 0 || c < 0 || r >= g.h || c >= g.w) return 0;
    return g.classes[r * g.w + c];
  }

  heightAt(x, z) {
    let y;
    if (this._inGrid(this.grid, x, z)) y = this._sampleGrid(this.grid, x, z);
    else {
      let hit = null;
      for (const g of this.detailGrids ?? []) {
        if (this._inGrid(g, x, z)) { hit = g; break; }
      }
      y = hit ? this._sampleGrid(hit, x, z)
        : this.countryGrid ? this._sampleGrid(this.countryGrid, x, z)
          : this._sampleGrid(this.grid, x, z);
    }
    // A road that dives under a junction is a hole in the GROUND, not a pipe
    // laid on the lawn. The cut is analytic (see _planTunnelCuts) rather than
    // baked into the 60 m grid, so the trench has real edges — and because it
    // lives in heightAt, the approach road, its kerbs, its markings and the
    // props beside it all follow it down without knowing it exists.
    if (this._cutFlag) {
      const cy = this._cutAt(x, z, y);
      if (cy !== null && cy < y) return cy;
    }
    // The channel. The 30 m DEM renders a 60-100 m river with 5 m walls as a
    // broad ramp, so inside the real OSM outline the ground sat ABOVE the
    // measured water surface and the sheet stopped tens of metres short of the
    // quay -- the water was not by the bank. Drop the bed below the surface
    // wherever the river actually is, and it fills to the wall.
    if (this._riverCarve) {
      const R = this._river;
      const k = Math.floor(x / R.mcell) * 100003 + Math.floor(z / R.mcell);
      const lv = R.levelOf.get(k);
      if (lv !== undefined) {
        const bed = lv - RIVER_DEPTH;
        if (bed < y) return bed;
      }
    }
    return y;
  }

  // ---- flying through a bore ------------------------------------------------
  // A bore is a hole THROUGH a hill, but heightAt still reports the hillside
  // above it (everything on the surface has to drape on that). So the barrels
  // get their own lookup: inside one, the ground under the plane is the tunnel
  // floor. Without this, entering a portal reads as flying into the mountain.

  _indexBores() {
    this._boreHash = new Map();
    this._boreCell = 60;
    if (!this._boreTubes?.length) return;
    for (const t of this._boreTubes) {
      for (let i = 0; i < t.pts.length - 1; i++) {
        const seg = {
          x1: t.pts[i][0], z1: t.pts[i][1], y1: t.y[i],
          x2: t.pts[i + 1][0], z2: t.pts[i + 1][1], y2: t.y[i + 1],
          r: t.r, h: t.h,
        };
        const pad = t.r + 6;
        const cx0 = Math.floor((Math.min(seg.x1, seg.x2) - pad) / this._boreCell);
        const cx1 = Math.floor((Math.max(seg.x1, seg.x2) + pad) / this._boreCell);
        const cz0 = Math.floor((Math.min(seg.z1, seg.z2) - pad) / this._boreCell);
        const cz1 = Math.floor((Math.max(seg.z1, seg.z2) + pad) / this._boreCell);
        for (let cx = cx0; cx <= cx1; cx++) {
          for (let cz = cz0; cz <= cz1; cz++) {
            const k = cx * 100003 + cz;
            let arr = this._boreHash.get(k);
            if (!arr) this._boreHash.set(k, (arr = []));
            arr.push(seg);
          }
        }
      }
    }
  }

  // Floor height if (x, z, y) is inside a tunnel barrel, otherwise null.
  boreFloorAt(x, z, y) {
    if (!this._boreHash?.size) return null;
    const c = this._boreCell;
    const arr = this._boreHash.get(Math.floor(x / c) * 100003 + Math.floor(z / c));
    if (!arr) return null;
    for (const s of arr) {
      const dx = s.x2 - s.x1;
      const dz = s.z2 - s.z1;
      const l2 = dx * dx + dz * dz || 1;
      let t = ((x - s.x1) * dx + (z - s.z1) * dz) / l2;
      t = Math.max(0, Math.min(1, t));
      const px = s.x1 + dx * t;
      const pz = s.z1 + dz * t;
      if ((x - px) ** 2 + (z - pz) ** 2 > s.r * s.r) continue;
      const floor = s.y1 + (s.y2 - s.y1) * t;
      // only while the aircraft is actually in the barrel: above the crown it
      // is over the hill, and the hill is what it must not fly into
      if (y === undefined || (y > floor - 4 && y < floor + s.h + 2)) return floor;
    }
    return null;
  }

  // ---- tunnels: cuttings, portals and cut-and-cover lids -------------------

  // OSM gives a tunnel no depth, no portal and no ramps — just `tunnel=yes` on
  // a way. So the shape is derived here: every mouth gets an approach cutting
  // ramped down at a driveable gradient, following the REAL road that connects
  // to it (the way's own straight extension ploughs through whatever is next
  // to it). A short flat one is then covered by a cut-and-cover lid and the
  // street crosses over the top; a long one, or one that climbs into relief,
  // is a bore, and the ground between the portals is left alone.
  // Coarse "is there a building here" test, from the raw footprint list —
  // available long before _buildBuildings has extruded anything.
  _indexBuildings(buildings) {
    this._bldHash = new Map();
    this._bldCell = 50;
    for (const b of buildings) {
      const n = (b.length - 1) / 2;
      if (n < 3) continue;
      let sx = 0;
      let sz = 0;
      for (let i = 0; i < n; i++) {
        sx += b[1 + i * 2];
        sz += b[2 + i * 2];
      }
      const x = sx / n / 10;
      const z = sz / n / 10;
      const k = Math.floor(x / 50) * 100003 + Math.floor(z / 50);
      let arr = this._bldHash.get(k);
      if (!arr) this._bldHash.set(k, (arr = []));
      arr.push(x, z);
    }
  }

  _buildingNear(x, z, r) {
    if (!this._bldHash) return false;
    const c = this._bldCell;
    const r2 = r * r;
    for (let cx = Math.floor((x - r) / c); cx <= Math.floor((x + r) / c); cx++) {
      for (let cz = Math.floor((z - r) / c); cz <= Math.floor((z + r) / c); cz++) {
        const arr = this._bldHash.get(cx * 100003 + cz);
        if (!arr) continue;
        for (let i = 0; i < arr.length; i += 2) {
          if ((arr[i] - x) ** 2 + (arr[i + 1] - z) ** 2 < r2) return true;
        }
      }
    }
    return false;
  }

  _planTunnelCuts(tunnels, roads) {
    const DEPTH = 5.4; // headroom under a cut-and-cover lid
    // A BORE is driven deeper: the cover over the crown is what limits how
    // big the barrel can be, and these are meant to be flown through, not
    // just driven. 13 m of cover buys an ~11 m vault under a 2 m roof.
    const BORE_DEPTH = 11;
    // The bores are meant to be FLOWN, not just driven: the real Noe Zhordania
    // tunnel is 9 m wide with 4.8 m of clearance and no jet fits, so the
    // barrel is deliberately oversized. The portals stay at their real place
    // and height; only the section inside the hill is generous.
    // Enough for the TALLEST vault the builder can choose (12 m) plus its
    // roof and a margin -- at 11 m a 10.8 m vault surfaced by a few
    // centimetres in exactly the places the hill is thinnest.
    const FLY_COVER = 14;
    const GRADE = 0.07; // approach ramp gradient
    // a bore drops further, so its approach takes a steeper grade — otherwise
    // the cutting outside the portal is a 170 m canyon across the district
    const BORE_GRADE = 0.11;
    const RAMP = DEPTH / GRADE; // ~77 m
    this._cuts = [];
    this._lids = [];
    this._bores = [];
    // index road endpoints so a ramp can follow the street it belongs to
    const ends = new Map();
    const key = (x, z) => `${Math.round(x * 2)}_${Math.round(z * 2)}`;
    for (const road of roads) {
      const n = (road.length - 1) / 2;
      if (n < 2) continue;
      for (const i of [0, n - 1]) {
        const k = key(road[1 + i * 2] / 10, road[2 + i * 2] / 10);
        let b = ends.get(k);
        if (!b) ends.set(k, (b = []));
        b.push({ road, head: i === 0 });
      }
    }
    // walk `len` metres of real street away from a portal
    const walk = (x, z, dx, dz, len) => {
      const out = [];
      const cands = ends.get(key(x, z)) ?? [];
      let best = null;
      for (const c of cands) {
        const n = (c.road.length - 1) / 2;
        const j = c.head ? 1 : n - 2;
        const nx = c.road[1 + j * 2] / 10 - x;
        const nz = c.road[2 + j * 2] / 10 - z;
        const l = Math.hypot(nx, nz) || 1;
        const dot = (nx / l) * dx + (nz / l) * dz;
        if (dot > 0.4 && (!best || dot > best.dot)) best = { c, dot };
      }
      if (best) {
        const { road, head } = best.c;
        const n = (road.length - 1) / 2;
        let acc = 0;
        let px = x;
        let pz = z;
        for (let k = 1; k < n && acc < len; k++) {
          const i = head ? k : n - 1 - k;
          const qx = road[1 + i * 2] / 10;
          const qz = road[2 + i * 2] / 10;
          acc += Math.hypot(qx - px, qz - pz);
          out.push([qx, qz]);
          px = qx;
          pz = qz;
        }
        if (acc >= len * 0.6) return out; // good enough coverage
      }
      // no street to follow: fall back to the mouth's own heading
      const steps = 4;
      for (let k = 1; k <= steps; k++) out.push([x + dx * (len * k) / steps, z + dz * (len * k) / steps]);
      return out;
    };

    // ---- group the ways into STRUCTURES ----------------------------------
    // OSM splits a dual-carriageway underpass into one way per direction, and
    // an interchange into a way per ramp. Cutting each of them its own trench
    // dug a spider of parallel ditches with retaining walls standing between
    // them — the field of white slabs across Heroes Square. One structure gets
    // one corridor, as wide as the ways it carries.
    const ways = [];
    for (const tn of tunnels) {
      const n = (tn.length - 1) / 2;
      if (n < 2) continue;
      const width = tn[0];
      const pts = [];
      for (let i = 0; i < n; i++) pts.push([tn[1 + i * 2] / 10, tn[2 + i * 2] / 10]);
      let L = 0;
      for (let i = 1; i < n; i++) L += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
      // A 3.4 m way 12 m long under a Sololaki block is an archway, not a
      // tunnel: 55 of the 124 are that. They stay at grade.
      if (width < 5 && L < 40) continue;
      if (L < 12) continue;
      ways.push({ pts, width, L, n });
    }
    ways.sort((a, b) => b.L - a.L);
    const taken = new Set();
    const groups = [];
    const distToPath = (x, z, path) => {
      let best = Infinity;
      for (let i = 0; i < path.length - 1; i++) {
        const ex = path[i + 1][0] - path[i][0];
        const ez = path[i + 1][1] - path[i][1];
        const l2 = ex * ex + ez * ez;
        let t = l2 > 1e-6 ? ((x - path[i][0]) * ex + (z - path[i][1]) * ez) / l2 : 0;
        t = Math.max(0, Math.min(1, t));
        const dx = x - (path[i][0] + ex * t);
        const dz = z - (path[i][1] + ez * t);
        best = Math.min(best, Math.hypot(dx, dz));
      }
      return best;
    };
    for (let i = 0; i < ways.length; i++) {
      if (taken.has(i)) continue;
      taken.add(i);
      const spine = ways[i];
      let spread = spine.width / 2;
      for (let j = i + 1; j < ways.length; j++) {
        if (taken.has(j)) continue;
        let far = 0;
        for (const p of ways[j].pts) far = Math.max(far, distToPath(p[0], p[1], spine.pts));
        if (far > 26) continue; // a different structure
        taken.add(j);
        spread = Math.max(spread, far + ways[j].width / 2);
      }
      groups.push({ spine, spread });
    }

    for (const grp of groups) {
      const { pts, width, L, n } = grp.spine;
      const y0 = this.heightAt(pts[0][0], pts[0][1]);
      const y1 = this.heightAt(pts[n - 1][0], pts[n - 1][1]);
      let rise = 0;
      for (let i = 0; i < n; i++) {
        rise = Math.max(rise, this.heightAt(pts[i][0], pts[i][1]) - (y0 + (y1 - y0) * (i / (n - 1))));
      }
      const hwGuess = Math.min(16, Math.max(grp.spread + 1.4, width / 2 + 1.4, 5.5));
      // Bore or cut-and-cover is decided by ONE thing: is there ground above
      // it? `rise` is the relief over the straight mouth-to-mouth line, so a
      // real hillside bore (Avlabari, 12.4 m) separates cleanly from an urban
      // dip under a junction (Heroes Square, 0 m). The old test asked for 16 m
      // on a 60 m DEM and never fired; asking for LENGTH instead was worse —
      // it put mountain portals on the lawn at Heroes Square.
      // Whatever the DEM says, you cannot cut-and-cover a trench through a
      // block of flats. If OSM has buildings standing over the middle of the
      // tunnel, it is bored — this is the check that keeps a 300 m canyon
      // from being dug through Avlabari.
      let roofed = false;
      for (let i = 1; i < n - 1 && !roofed; i++) {
        const f = i / (n - 1);
        if (f < 0.2 || f > 0.8) continue;
        roofed = this._buildingNear(pts[i][0], pts[i][1], hwGuess + 5);
      }
      // A tunnel driven along the FLANK of a hill has no rise mouth-to-mouth
      // and no buildings on top, but covering it means retaining twelve metres
      // of hillside on the uphill side and the road ends up in a canyon. The
      // ground beside it is the test the profile along it cannot make.
      let sideHill = false;
      for (let i = 1; i < n - 1 && !sideHill; i++) {
        const f = i / (n - 1);
        if (f < 0.25 || f > 0.75) continue;
        const dx = pts[i + 1][0] - pts[i - 1][0];
        const dz = pts[i + 1][1] - pts[i - 1][1];
        const l = Math.hypot(dx, dz) || 1;
        const nx = -dz / l;
        const nz = dx / l;
        const d = hwGuess + CUT_BATTER;
        const mid = this.heightAt(pts[i][0], pts[i][1]);
        const up = Math.max(this.heightAt(pts[i][0] + nx * d, pts[i][1] + nz * d),
          this.heightAt(pts[i][0] - nx * d, pts[i][1] - nz * d));
        if (up - mid > 5) sideHill = true;
      }
      const bore = rise > 6 || roofed || sideHill || L > 260;
      const hw = Math.min(16, Math.max(grp.spread + 1.4, width / 2 + 1.4, 5.5));
      // the barrel and its headwall belong to the carriageway, not to the
      // full width of the cutting the whole interchange shares
      const bhw = Math.min(11, Math.max(width / 2 + 1.4, 5.0));

      // approach ramps at both mouths, in corridor order A-end -> B-end
      const dA = [pts[0][0] - pts[1][0], pts[0][1] - pts[1][1]];
      const lA = Math.hypot(dA[0], dA[1]) || 1;
      const dB = [pts[n - 1][0] - pts[n - 2][0], pts[n - 1][1] - pts[n - 2][1]];
      const lB = Math.hypot(dB[0], dB[1]) || 1;
      // a bore digs deeper, so its approach cutting has to run further out to
      // reach the portal at a driveable gradient
      const depth = bore ? BORE_DEPTH : DEPTH;
      const ramp = depth / (bore ? BORE_GRADE : GRADE);
      const rampA = walk(pts[0][0], pts[0][1], dA[0] / lA, dA[1] / lA, ramp);
      const rampB = walk(pts[n - 1][0], pts[n - 1][1], dB[0] / lB, dB[1] / lB, ramp);
      const corridor = [...rampA.slice().reverse(), ...pts, ...rampB];
      const sA = rampA.length; // index of mouth A in the corridor
      const sB = sA + n - 1; // index of mouth B

      // arc length and a smoothed ground profile along the corridor
      const run = [0];
      for (let i = 1; i < corridor.length; i++) {
        run.push(run[i - 1] + Math.hypot(corridor[i][0] - corridor[i - 1][0], corridor[i][1] - corridor[i - 1][1]));
      }
      // The floor is planned against the RENDERED ground, not the measured
      // one: the portal has to meet the street that is actually drawn outside
      // it, and the vault has to stay buried under the terrain the player can
      // see. (Planning against the measured surface, which is up to 16 m above
      // the DEM over the Old Town, left 9 m vaults poking out of the hill.)
      const gnd = corridor.map((p) => this.heightAt(p[0], p[1]));
      // The measured surface is kept separately, and only decides how big a
      // vault the real hill could take -- see the tube builder.
      const gmeas = corridor.map((p, i) => {
        const T = this._tunnelGround;
        if (!T) return gnd[i];
        const k = `${Math.floor(p[0] / T.cell)}_${Math.floor(p[1] / T.cell)}`;
        return T.g.get(k) ?? gnd[i];
      });
      const sm = gnd.slice();
      for (let pass = 0; pass < 2; pass++) {
        for (let i = 1; i < sm.length - 1; i++) sm[i] = (sm[i - 1] + 2 * sm[i] + sm[i + 1]) / 4;
      }
      // A CUT runs at a fixed depth under the surface and ramps out to grade
      // over its approaches. A BORE does not: it is a road under a hill, so it
      // runs roughly level between its portals and the hill rises over it.
      // Treating a bore like a cut put its portal 11 m below the street --
      // Noe Zhordania's mouth on the embankment was a pit, not a portal -- and
      // made the cover a constant 11 m everywhere, so the vault could never be
      // more than 4.5 m and no bore was ever flyable.
      const floor = bore
        ? (() => {
          const yA = sm[sA];
          const yB = sm[sB];
          const span = (run[sB] - run[sA]) || 1;
          return corridor.map((p, i) => {
            if (i < sA || i > sB) return sm[i];       // approach sits at grade
            const t = (run[i] - run[sA]) / span;
            const line = yA + (yB - yA) * t;
            // Stay buried inside the hill, but let the portals reach grade:
            // the clearance fades in over the first 40 m from each mouth.
            // FLY_COVER is what a flyable barrel needs overhead (a 9 m vault
            // plus 2 m of roof) -- under a thin spot the road dips rather than
            // the tunnel shrinking, which is what a real tunnel does too.
            const inFromEnd = Math.min(run[i] - run[sA], run[sB] - run[i]);
            const w = Math.min(1, inFromEnd / 40);
            // bury against the RAW ground as well as the smoothed profile:
            // smoothing lifts the surface out of dips, and the crown then
            // surfaced in exactly those dips
            const top = Math.min(sm[i], gnd[i]);
            return Math.min(line, top - FLY_COVER * w);
          });
        })()
        : corridor.map((p, i) => {
          // full depth from mouth to mouth, ramping out over the approaches
          let d = depth;
          if (i < sA) d = depth * (run[i] / (run[sA] || 1));
          else if (i > sB) d = depth * (1 - (run[i] - run[sB]) / ((run[run.length - 1] - run[sB]) || 1));
          return sm[i] - Math.max(0, d);
        });

      for (let i = 0; i < corridor.length - 1; i++) {
        // a bore is hidden by whatever it runs under: cut only the approaches
        if (bore && i >= sA && i < sB) continue;
        this._cuts.push({
          x1: corridor[i][0], z1: corridor[i][1], y1: floor[i],
          x2: corridor[i + 1][0], z2: corridor[i + 1][1], y2: floor[i + 1],
          hw,
        });
      }
      const rec = {
        corr: corridor,
        corrY: floor,
        pts: corridor.slice(sA, sB + 1),
        y: floor.slice(sA, sB + 1),
        gy: sm.slice(sA, sB + 1),
        gm: gmeas.slice(sA, sB + 1),
        hw, bhw, width, bore,
      };
      if (bore) this._bores.push(rec);
      else this._lids.push(rec);
    }

    // occupancy flags so heightAt can bail out in one lookup
    this._cutHash = new Map();
    const CELL = 40;
    for (const c of this._cuts) {
      const r = c.hw + CUT_BATTER + 4;
      const x0 = Math.floor((Math.min(c.x1, c.x2) - r) / CELL);
      const x1 = Math.floor((Math.max(c.x1, c.x2) + r) / CELL);
      const z0 = Math.floor((Math.min(c.z1, c.z2) - r) / CELL);
      const z1 = Math.floor((Math.max(c.z1, c.z2) + r) / CELL);
      for (let cx = x0; cx <= x1; cx++) {
        for (let cz = z0; cz <= z1; cz++) {
          const k = cx * 100003 + cz;
          let b = this._cutHash.get(k);
          if (!b) this._cutHash.set(k, (b = []));
          b.push(c);
        }
      }
    }
    this._cutCell = CELL;
    this._cutFlag = this._cuts.length > 0;
    console.log(`tunnel cuts: ${this._cuts.length} trench segments, ${this._lids.length} underpass lids, ${this._bores.length} bores`);
  }

  // Is (x, z) inside a tunnel corridor -- a bore barrel or the open cutting
  // that leads into one? The river bank knows nothing about tunnels, so where
  // a portal opens onto the embankment the quay wall and its balustrade were
  // built straight across the mouth: at the Gorgasali bore the nearest quay
  // stood 15 m from the exit with its coping at 402 m over a tunnel floor at
  // 391.8 m, which walls the tunnel up.
  _inTunnelCorridor(x, z, margin = 10) {
    for (const t of this._boreTubes || []) {
      const r = t.r + margin;
      for (let i = 0; i < t.pts.length - 1; i++) {
        const [x1, z1] = t.pts[i];
        const [x2, z2] = t.pts[i + 1];
        const ex = x2 - x1;
        const ez = z2 - z1;
        const l2 = ex * ex + ez * ez;
        if (l2 < 1e-6) continue;
        let u = ((x - x1) * ex + (z - z1) * ez) / l2;
        u = u < 0 ? 0 : u > 1 ? 1 : u;
        const d2 = (x - (x1 + ex * u)) ** 2 + (z - (z1 + ez * u)) ** 2;
        if (d2 < r * r) return true;
      }
    }
    for (const c of this._cuts || []) {
      const r = c.hw + CUT_BATTER + margin;
      const ex = c.x2 - c.x1;
      const ez = c.z2 - c.z1;
      const l2 = ex * ex + ez * ez;
      if (l2 < 1e-6) continue;
      let u = ((x - c.x1) * ex + (z - c.z1) * ez) / l2;
      u = u < 0 ? 0 : u > 1 ? 1 : u;
      const d2 = (x - (c.x1 + ex * u)) ** 2 + (z - (c.z1 + ez * u)) ** 2;
      if (d2 < r * r) return true;
    }
    return false;
  }

  // Floor height if (x, z) is inside a trench, else null.
  //
  // Outside the retaining wall the cut BATTERS back to grade over CUT_BATTER
  // metres instead of stepping vertically. A vertical step sampled on the 5 m
  // refined grid turned into a fan of near-vertical triangles that read as a
  // field of shards; the wall hides the batter from inside the trench, which
  // is how a real cutting is built anyway.
  _cutAt(x, z, base) {
    const b = this._cutHash.get(Math.floor(x / this._cutCell) * 100003 + Math.floor(z / this._cutCell));
    if (!b) return null;
    let best = null;
    for (const c of b) {
      const ex = c.x2 - c.x1;
      const ez = c.z2 - c.z1;
      const l2 = ex * ex + ez * ez;
      if (l2 < 1e-6) continue;
      // CLAMP, don't skip: on the outside of a bend the point falls past both
      // neighbouring segments, and skipping left a pillar of untouched ground
      // standing in the middle of the cutting. Clamping rounds the join.
      let t = ((x - c.x1) * ex + (z - c.z1) * ez) / l2;
      t = t < 0 ? 0 : t > 1 ? 1 : t;
      const px = c.x1 + ex * t;
      const pz = c.z1 + ez * t;
      const d2 = (x - px) ** 2 + (z - pz) ** 2;
      const outer = c.hw + CUT_BATTER;
      if (d2 > outer * outer) continue;
      const floor = c.y1 + (c.y2 - c.y1) * t;
      let y = floor;
      const d = Math.sqrt(d2);
      if (d > c.hw && base !== undefined) {
        y = floor + (base - floor) * ((d - c.hw) / CUT_BATTER);
      }
      if (best === null || y < best) best = y;
    }
    return best;
  }

  _decodeGrid(t) {
    const raw = base64ToBytes(t.data);
    const full = new Uint16Array(raw.buffer, raw.byteOffset, raw.byteLength / 2);
    const heights = new Float32Array(full.length);
    // DEM value 0 is the Black Sea — sink it so the sea surface never z-fights.
    for (let i = 0; i < full.length; i++) heights[i] = full[i] === 0 ? -4 : full[i] / 10;
    return { w: t.w, h: t.h, step: t.step, minx: t.minx, minz: t.minz, heights };
  }

  _addDetailGrid(d) {
    d.grid = this._decodeGrid(d.terrain);
    this.detailGrids.push(d.grid);
  }

  // Sharp satellite-textured terrain patch for one city.
  _buildDetailMesh(d) {
    const g = d.grid;
    const sat = d.satellite;
    const pos = new Float32Array(g.w * g.h * 3);
    const uv = new Float32Array(g.w * g.h * 2);
    for (let r = 0; r < g.h; r++) {
      for (let c = 0; c < g.w; c++) {
        const i = r * g.w + c;
        const x = g.minx + c * g.step;
        const z = g.minz + r * g.step;
        pos[i * 3] = x;
        pos[i * 3 + 1] = g.heights[i];
        pos[i * 3 + 2] = z;
        const lat = this.meta.origin.lat + z / this.meta.mlat;
        const lon = this.meta.origin.lon - x / this.meta.mlon;
        const nt = 2 ** sat.z;
        const xt = ((lon + 180) / 360) * nt;
        const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
        uv[i * 2] = (xt - sat.x0) / sat.cols;
        uv[i * 2 + 1] = 1 - (yt - sat.y0) / sat.rows;
      }
    }
    const idx = new Uint32Array((g.w - 1) * (g.h - 1) * 6);
    let k = 0;
    for (let r = 0; r < g.h - 1; r++) {
      for (let c = 0; c < g.w - 1; c++) {
        const a = r * g.w + c;
        idx[k++] = a; idx[k++] = a + g.w; idx[k++] = a + 1;
        idx[k++] = a + 1; idx[k++] = a + g.w; idx[k++] = a + g.w + 1;
      }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    geo.setIndex(new THREE.BufferAttribute(idx, 1));
    geo.computeVertexNormals();
    geo.computeBoundsTree();
    const mat = new THREE.MeshLambertMaterial({ color: 0x8f8a70, polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1 });
    new THREE.TextureLoader().load(`tbilisi/${d.file}`, (tex) => {
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.anisotropy = 8;
      mat.map = tex;
      mat.color.set(0xffffff);
      mat.needsUpdate = true;
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.receiveShadow = true;
    mesh.matrixAutoUpdate = false;
    mesh.userData.isTerrain = true;
    this.group.add(mesh);
    this.collidables.push(mesh);
  }

  // ---- whole-Georgia terrain ------------------------------------------------

  _buildCountry(country) {
    this.countryGrid = this._decodeGrid(country.terrain);
    const heights = this.countryGrid.heights;

    // Render decimated (DEC×), skipping quads inside the detailed Tbilisi area
    // (covered by the sharper 60 m mesh). DEC=2 keeps the country mesh from
    // overlapping / washing out the city at low altitude; the satellite drape
    // already carries the mountain detail.
    const g = this.countryGrid;
    const DEC = 2;
    const w2 = Math.floor((g.w - 1) / DEC) + 1;
    const h2 = Math.floor((g.h - 1) / DEC) + 1;
    const pos = new Float32Array(w2 * h2 * 3);
    const uv = new Float32Array(w2 * h2 * 2);
    const sat = country.satellite;
    for (let r = 0; r < h2; r++) {
      for (let c = 0; c < w2; c++) {
        const i = r * w2 + c;
        const fc = Math.min(c * DEC, g.w - 1);
        const fr = Math.min(r * DEC, g.h - 1);
        const x = g.minx + fc * g.step;
        const z = g.minz + fr * g.step;
        pos[i * 3] = x;
        pos[i * 3 + 1] = g.heights[fr * g.w + fc];
        pos[i * 3 + 2] = z;
        const lat = this.meta.origin.lat + z / this.meta.mlat;
        const lon = this.meta.origin.lon - x / this.meta.mlon;
        const nt = 2 ** sat.z;
        const xt = ((lon + 180) / 360) * nt;
        const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
        uv[i * 2] = (xt - sat.x0) / sat.cols;
        uv[i * 2 + 1] = 1 - (yt - sat.y0) / sat.rows;
      }
    }
    const idx = [];
    const margin = DEC * g.step; // shave a cell so edges tuck under the detail mesh
    const dg = this.grid;
    const dminx = dg.minx + margin;
    const dmaxx = dg.minx + (dg.w - 1) * dg.step - margin;
    const dminz = dg.minz + margin;
    const dmaxz = dg.minz + (dg.h - 1) * dg.step - margin;
    for (let r = 0; r < h2 - 1; r++) {
      for (let c = 0; c < w2 - 1; c++) {
        const x = g.minx + c * DEC * g.step;
        const z = g.minz + r * DEC * g.step;
        const x2 = x + DEC * g.step;
        const z2 = z + DEC * g.step;
        if (x > dminx && x2 < dmaxx && z > dminz && z2 < dmaxz) continue; // covered by detail mesh
        const a = r * w2 + c;
        idx.push(a, a + w2, a + 1, a + 1, a + w2, a + w2 + 1);
      }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    geo.computeVertexNormals();
    const mat = new THREE.MeshLambertMaterial({ color: 0x8f8a70 });
    new THREE.TextureLoader().load('tbilisi/georgia-sat.jpg', (tex) => {
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.anisotropy = 8;
      mat.map = tex;
      mat.color.set(0xffffff);
      mat.needsUpdate = true;
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.receiveShadow = true;
    mesh.matrixAutoUpdate = false;
    this.group.add(mesh);
  }

  // Map a local vertex to its pixel in the satellite mosaic (mercator tiles).
  _satUV(x, z) {
    const s = this.meta.satellite;
    const lat = this.meta.origin.lat + z / this.meta.mlat;
    const lon = this.meta.origin.lon - x / this.meta.mlon;
    const nt = 2 ** s.z;
    const xt = ((lon + 180) / 360) * nt;
    const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
    return [(xt - s.x0) / s.cols, 1 - (yt - s.y0) / s.rows];
  }

  _buildTerrainMesh(t) {
    const g = this.grid;
    const pos = new Float32Array(g.w * g.h * 3);
    const col = new Uint8Array(g.w * g.h * 3);
    const uv = this.meta.satellite ? new Float32Array(g.w * g.h * 2) : null;
    const dry = hexToRgb('#9c9273');
    const high = hexToRgb('#8a9464');
    const green = hexToRgb('#5a9a3c'); // grassy park green
    const water = hexToRgb('#4d7fb5');
    let minE = Infinity;
    let maxE = -Infinity;
    for (const h of g.heights) {
      if (h < minE) minE = h;
      if (h > maxE) maxE = h;
    }
    for (let r = 0; r < g.h; r++) {
      for (let c = 0; c < g.w; c++) {
        const i = r * g.w + c;
        const y = g.heights[i];
        pos[i * 3] = g.minx + c * g.step;
        pos[i * 3 + 1] = y;
        pos[i * 3 + 2] = g.minz + r * g.step;
        const cl = g.classes[i];
        let base;
        if (cl === 2) base = water;
        else if (cl === 1) base = green;
        else {
          const k = (y - minE) / (maxE - minE + 1);
          base = [dry[0] + (high[0] - dry[0]) * k, dry[1] + (high[1] - dry[1]) * k, dry[2] + (high[2] - dry[2]) * k];
        }
        // Cheap deterministic per-vertex tint so large areas don't look flat.
        const n = 0.92 + 0.08 * fract(Math.sin(c * 12.9898 + r * 78.233) * 43758.5453);
        col[i * 3] = base[0] * n;
        col[i * 3 + 1] = base[1] * n;
        col[i * 3 + 2] = base[2] * n;
        if (uv) {
          const [u, v] = this._satUV(pos[i * 3], pos[i * 3 + 2]);
          uv[i * 2] = u;
          uv[i * 2 + 1] = v;
        }
      }
    }
    // A 60 m cell cannot hold a 20 m trench, so the handful of cells a tunnel
    // cutting passes through are dropped from the regular sheet and rebuilt at
    // 5 m by _buildCutTerrain — that is what gives the cut real edges instead
    // of a soft dimple smeared over three grid points.
    const refine = (this._cutFlag || this._riverCarve) ? this._refinedCells(g) : null;
    const idx = new Uint32Array((g.w - 1) * (g.h - 1) * 6);
    let k = 0;
    for (let r = 0; r < g.h - 1; r++) {
      for (let c = 0; c < g.w - 1; c++) {
        if (refine && refine.has(r * g.w + c)) continue;
        const a = r * g.w + c;
        idx[k++] = a; idx[k++] = a + g.w; idx[k++] = a + 1;
        idx[k++] = a + 1; idx[k++] = a + g.w; idx[k++] = a + g.w + 1;
      }
    }
    this._terrainCol = col; // the refined cut cells copy their parent's tint
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('color', new THREE.BufferAttribute(col, 3, true));
    if (uv) geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    geo.setIndex(new THREE.BufferAttribute(idx.subarray(0, k), 1));
    geo.computeVertexNormals();
    geo.computeBoundsTree();
    const mat = new THREE.MeshLambertMaterial({ vertexColors: true });
    if (this.meta.satellite) {
      // Swap painted ground for the real satellite photo once it arrives.
      new THREE.TextureLoader().load('tbilisi/satellite.jpg', (tex) => {
        tex.colorSpace = THREE.SRGBColorSpace;
        tex.anisotropy = 8;
        mat.map = tex;
        mat.vertexColors = false;
        mat.color.set(0xffffff);
        mat.needsUpdate = true;
      });
    }
    if (refine) this._buildCutTerrain(g, refine, mat);
    const mesh = new THREE.Mesh(geo, mat);
    mesh.receiveShadow = true;
    mesh.matrixAutoUpdate = false;
    mesh.userData.isTerrain = true;
    this.group.add(mesh);
    this.collidables.push(mesh);
    this.terrainMesh = mesh;
  }

  // Grid cells a tunnel cutting passes through, as `r * w + c`.
  _refinedCells(g) {
    const cells = new Set();
    // Where the carved channel meets grade. Only the BANK cells: the bed
    // inside is flat and entirely under water, so subdividing it would cost
    // hundreds of thousands of triangles to show nothing. And only along the
    // built embankment -- out at the reservoir there is no quay to meet.
    if (this._riverCarve) {
      const R = this._river;
      const inRiver = (x, z) => R.levelOf.has(
        Math.floor(x / R.mcell) * 100003 + Math.floor(z / R.mcell));
      let n = 0;
      for (let r = 0; r < g.h - 1; r++) {
        for (let c = 0; c < g.w - 1; c++) {
          const x0 = g.minx + c * g.step;
          const z0 = g.minz + r * g.step;
          const a = inRiver(x0, z0);
          if (a === inRiver(x0 + g.step, z0)
            && a === inRiver(x0, z0 + g.step)
            && a === inRiver(x0 + g.step, z0 + g.step)) continue; // not a bank cell
          if (this._roadNear && !this._roadNear.has(
            Math.floor((x0 + g.step / 2) / this._roadNearCell) * 100003
            + Math.floor((z0 + g.step / 2) / this._roadNearCell))) continue;
          cells.add(r * g.w + c);
          n++;
        }
      }
      console.log(`river: ${n} bank cells refined to ${(g.step / 12).toFixed(1)} m`);
    }
    for (const c of this._cuts) {
      const r = c.hw + CUT_BATTER + g.step; // margin so the seam is never open
      const x0 = Math.min(c.x1, c.x2) - r;
      const x1 = Math.max(c.x1, c.x2) + r;
      const z0 = Math.min(c.z1, c.z2) - r;
      const z1 = Math.max(c.z1, c.z2) + r;
      const c0 = Math.max(0, Math.floor((x0 - g.minx) / g.step));
      const c1 = Math.min(g.w - 2, Math.floor((x1 - g.minx) / g.step));
      const r0 = Math.max(0, Math.floor((z0 - g.minz) / g.step));
      const r1 = Math.min(g.h - 2, Math.floor((z1 - g.minz) / g.step));
      for (let rr = r0; rr <= r1; rr++) {
        for (let cc = c0; cc <= c1; cc++) cells.add(rr * g.w + cc);
      }
    }
    return cells;
  }

  // The dropped cells, rebuilt at 5 m so the trench has walls you can see.
  _buildCutTerrain(g, cells, mat) {
    const N = 12; // 60 m / 12 = 5 m sub-quads
    const pos = [];
    const col = [];
    const uv = this.meta.satellite ? [] : null;
    const idx = [];
    for (const cell of cells) {
      const r = Math.floor(cell / g.w);
      const c = cell - r * g.w;
      const bx = g.minx + c * g.step;
      const bz = g.minz + r * g.step;
      const base = pos.length / 3;
      // reuse the parent cell's tint: once the satellite photo lands the
      // vertex colours stop mattering anyway
      const src = (r * g.w + c) * 3;
      for (let j = 0; j <= N; j++) {
        for (let i = 0; i <= N; i++) {
          const x = bx + (i / N) * g.step;
          const z = bz + (j / N) * g.step;
          pos.push(x, this.heightAt(x, z), z);
          col.push(this._terrainCol[src] / 255, this._terrainCol[src + 1] / 255, this._terrainCol[src + 2] / 255);
          if (uv) {
            const [u, v] = this._satUV(x, z);
            uv.push(u, v);
          }
        }
      }
      for (let j = 0; j < N; j++) {
        for (let i = 0; i < N; i++) {
          const a = base + j * (N + 1) + i;
          idx.push(a, a + N + 1, a + 1, a + 1, a + N + 1, a + N + 2);
        }
      }
    }
    if (!idx.length) return;
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    geo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(col), 3));
    if (uv) geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    geo.computeVertexNormals();
    geo.computeBoundsTree();
    const mesh = new THREE.Mesh(geo, mat);
    mesh.receiveShadow = true;
    mesh.matrixAutoUpdate = false;
    mesh.userData.isTerrain = true;
    this.group.add(mesh);
    this.collidables.push(mesh);
    console.log(`cut terrain: ${cells.size} cells refined to ${N}x${N}`);
  }

  // ---- buildings -----------------------------------------------------------

  async _buildBuildings(buildings, onProgress) {
    const chunks = new Map();
    // Kept empty: suburban houses are extruded on their real OSM footprints
    // now (see `cottage` below). _buildHouses stays for reference / rollback.
    const houses = [];
    const total = buildings.length;
    // Per-building REAL photo facades: buildings matched to a reviewed Commons
    // photo each get their own texture (textures/pb/, built by
    // tools/build_pb_textures.py from agent-reviewed cuts).
    let pbGrid = null;
    try {
      const res = await fetch('textures/pb/manifest.json');
      if (res.ok) {
        const man = await res.json();
        const pbArtists = new Set(
          Object.values(man).map((m) => (m.credit || '').split(',')[0]).filter(Boolean),
        );
        this.attribution += ` · ${Object.keys(man).length} building photos by ${pbArtists.size} Wikimedia Commons photographers (CC BY/BY-SA/CC0 — full list: textures/pb/CREDITS.txt)`;
        pbGrid = new Map();
        for (const [key, m] of Object.entries(man)) {
          const p = {
            key,
            ...m,
            x: -(m.lon - this.meta.origin.lon) * this.meta.mlon,
            z: (m.lat - this.meta.origin.lat) * this.meta.mlat,
          };
          const gk = `${Math.round(p.x / 30)}_${Math.round(p.z / 30)}`;
          if (!pbGrid.has(gk)) pbGrid.set(gk, []);
          pbGrid.get(gk).push(p);
        }
      }
    } catch {
      /* no per-building texture set yet */
    }
    // EXCLUSIVE photo assignment: one photo dresses exactly ONE building.
    // Matching every building within 28 m of a photo put the same facade on
    // up to a DOZEN neighbours (1532 buildings wearing 586 photos — 86 % of
    // them cloned), which is the most visible kind of repetition in a dense
    // block. Instead: score every (photo, building) pair inside the radius,
    // take them cheapest-first, and let each side be claimed once. Buildings
    // that lose out fall back to a real photo-cut wall material, which is
    // still a real Tbilisi surface — just not that specific house.
    const pbOf = new Map(); // bid -> manifest entry
    if (pbGrid) {
      const bGrid = new Map();
      const cent = new Array(total);
      for (let bi = 0; bi < total; bi++) {
        const b = buildings[bi];
        const n = (b.length - 1) / 2;
        if (n < 3) continue;
        let x = 0;
        let z = 0;
        for (let i = 0; i < n; i++) { x += b[1 + i * 2] / 10; z += b[2 + i * 2] / 10; }
        x /= n; z /= n;
        cent[bi] = [x, z];
        const gk = `${Math.round(x / 30)}_${Math.round(z / 30)}`;
        if (!bGrid.has(gk)) bGrid.set(gk, []);
        bGrid.get(gk).push(bi);
      }
      const pairs = [];
      for (const list of pbGrid.values()) {
        for (const p of list) {
          const gi = Math.round(p.x / 30);
          const gj = Math.round(p.z / 30);
          for (let di = -1; di <= 1; di++) {
            for (let dj = -1; dj <= 1; dj++) {
              for (const bi of bGrid.get(`${gi + di}_${gj + dj}`) || []) {
                const c = cent[bi];
                const d = (p.x - c[0]) ** 2 + (p.z - c[1]) ** 2;
                if (d < 28 * 28) pairs.push([d, bi, p]);
              }
            }
          }
        }
      }
      pairs.sort((a, b) => a[0] - b[0]);
      const usedPhoto = new Set();
      for (const [, bi, p] of pairs) {
        if (pbOf.has(bi) || usedPhoto.has(p.key)) continue;
        pbOf.set(bi, p);
        usedPhoto.add(p.key);
      }
      console.log(`pb facades: ${pbOf.size} buildings matched 1:1 to ${usedPhoto.size} photos`);
    }
    const pbMats = new Map(); // texture key -> shared material
    const pbMaterial = (m) => {
      if (!pbMats.has(m.key)) {
        const mat = new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true, side: THREE.DoubleSide });
        try {
          new THREE.TextureLoader().load(`textures/pb/${m.file}`, (t) => {
            // 200 of these live on the GPU at once — 512 px is plenty at flight
            // distance and keeps texture memory ~4× smaller
            const img = t.image;
            const MAX = 512;
            if (img && Math.max(img.width, img.height) > MAX) {
              const k = MAX / Math.max(img.width, img.height);
              const c = document.createElement('canvas');
              c.width = Math.max(1, Math.round(img.width * k));
              c.height = Math.max(1, Math.round(img.height * k));
              c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
              t.image = c;
            }
            t.wrapS = t.wrapT = THREE.RepeatWrapping;
            t.colorSpace = THREE.SRGBColorSpace;
            t.anisotropy = 4;
            t.repeat.set(1 / m.bays, 1 / m.floors);
            mat.map = t;
            mat.needsUpdate = true;
          });
        } catch {
          /* offline */
        }
        pbMats.set(m.key, mat);
      }
      return pbMats.get(m.key);
    };
    // Real landmark models replace the OSM extrusions at these spots.
    const excl = EXCLUSIONS.map((e) => ({
      x: -(e.lon - this.meta.origin.lon) * this.meta.mlon,
      z: (e.lat - this.meta.origin.lat) * this.meta.mlat,
      r2: e.r * e.r,
    }));
    const hash = (x) => {
      let v = (x * 2654435761) % 4294967296;
      v = ((v >>> 13) ^ v) * 1274126177;
      return ((v >>> 16) % 1000) / 1000;
    };

    // Districts of Tbilisi wear different facades: ornate XIX-century houses
    // in the historic center (Kala, Sololaki, Avlabari, Aghmashenebeli),
    // classical plaster in Vake/Vera, and mass panel blocks in the Soviet
    // microdistricts (Gldani, Varketili, Saburtalo, Mukhiani…).
    const toXZ = (lat, lon) => ({
      x: -(lon - this.meta.origin.lon) * this.meta.mlon,
      z: (lat - this.meta.origin.lat) * this.meta.mlat,
    });
    const DISTRICTS = [
      ['historic', 41.6885, 44.8095, 800], // Kala / Old Town
      ['historic', 41.6875, 44.7965, 750], // Sololaki
      ['historic', 41.695, 44.79, 650], // lower Mtatsminda / Rustaveli
      ['historic', 41.7055, 44.794, 1100], // Chugureti / Aghmashenebeli ave
      ['historic', 41.696, 44.818, 700], // Avlabari
      ['classic', 41.706, 44.783, 800], // Vera
      ['classic', 41.7095, 44.758, 1300], // Vake
      ['soviet', 41.733, 44.751, 1800], // Saburtalo
      ['soviet', 41.794, 44.811, 2000], // Gldani
      ['soviet', 41.792, 44.78, 1300], // Mukhiani
      ['soviet', 41.786, 44.837, 1200], // Temka
      ['soviet', 41.706, 44.907, 1800], // Varketili
      ['soviet', 41.716, 44.876, 1200], // Vazisubani
      ['soviet', 41.685, 44.856, 1600], // Isani / Samgori
      ['soviet', 41.786, 44.752, 1800], // Didi Dighomi
      ['soviet', 41.721, 44.713, 1300], // Nutsubidze plateau
    ].map(([kind, lat, lon, r]) => ({ kind, ...toXZ(lat, lon), r2: r * r }));
    const districtAt = (x, z) => {
      for (const d of DISTRICTS) if ((x - d.x) ** 2 + (z - d.z) ** 2 < d.r2) return d.kind;
      return null;
    };

    // Footprints claimed by a real modelled panel block (see _panelKit).
    const panelItems = [];
    // Does this footprint read as a panel slab? Returns the placement if so.
    // The orientation comes from the LONGEST EDGE, which for a rectangle is
    // exact; everything else is measured in that frame.
    const PANEL_SEC = 12.0;    // m, must match SECTION_W in the Blender script
    const PANEL_DEPTH = 11.4;  // m, must match DEPTH
    const panelFit = (poly) => {
      const m = poly.length;
      if (m < 4 || m > 6) return null;     // a slab is a rectangle, not an L
      let bestLen = 0;
      let ang = 0;
      for (let i = 0; i < m; i++) {
        const a = poly[i];
        const b2 = poly[(i + 1) % m];
        const d = Math.hypot(b2.x - a.x, b2.y - a.y);
        if (d > bestLen) { bestLen = d; ang = Math.atan2(b2.y - a.y, b2.x - a.x); }
      }
      // measure the footprint in the frame of that edge
      const ca = Math.cos(-ang);
      const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      const len = u1 - u0;
      const dep = v1 - v0;
      if (dep < 8.5 || dep > 15.5) return null;      // not a slab depth
      if (len < 21 || len > 78) return null;         // 2 to 6 sections
      // the rectangle test: a real rectangle fills its own bounding box
      if (Math.abs(THREE.ShapeUtils.area(poly)) < len * dep * 0.82) return null;
      const sections = Math.max(2, Math.min(6, Math.round(len / PANEL_SEC)));
      return { ang, sections };
    };

    // `?audit` — record what happened to EVERY building so nothing can hide:
    // which ones got a real model, which stayed a photo-textured extrusion,
    // and for the extrusions, why they were not claimed. Off by default; it
    // costs a row per building.
    const AUDIT = new URLSearchParams(location.search).has('audit');
    const auditRows = AUDIT ? [] : null;
    let _obbL = 0;
    let _obbD = 0;
    const obbOf = (poly) => {
      let bl = 0; let ang = 0;
      for (let i = 0; i < poly.length; i++) {
        const a = poly[i];
        const b = poly[(i + 1) % poly.length];
        const d = Math.hypot(b.x - a.x, b.y - a.y);
        if (d > bl) { bl = d; ang = Math.atan2(b.y - a.y, b.x - a.x); }
      }
      const ca = Math.cos(-ang); const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      _obbL = u1 - u0; _obbD = v1 - v0;
    };
    const audit = (x, z, area, h, n, got, why) => {
      if (auditRows) auditRows.push({ x: Math.round(x), z: Math.round(z), area: Math.round(area), h: +h.toFixed(1), n, got, why, L: +_obbL.toFixed(1), D: +_obbD.toFixed(1) });
    };

    // Footprints claimed by a garage / shed / kiosk / hut.
    const outItems = [];
    const outFit = (poly) => {
      const kit = this._outKit;
      if (!kit) return null;
      const m = poly.length;
      if (m < 4 || m > 12) return null;
      let bl = 0; let ang = 0;
      for (let i = 0; i < m; i++) {
        const a = poly[i];
        const b2 = poly[(i + 1) % m];
        const d = Math.hypot(b2.x - a.x, b2.y - a.y);
        if (d > bl) { bl = d; ang = Math.atan2(b2.y - a.y, b2.x - a.x); }
      }
      const ca = Math.cos(-ang);
      const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      const len = u1 - u0;
      const dep = v1 - v0;
      if (len < 1.8 || dep < 1.4 || len > 18 || dep > 9) return null;
      let best = -1;
      let bestErr = Infinity;
      let bx = 1; let bz = 1;
      for (let k = 0; k < kit.length; k++) {
        const e = kit[k];
        const sx = len / e.W;
        const sz = dep / e.D;
        if (sx < 0.5 || sx > 2.2 || sz < 0.5 || sz > 2.2) continue;
        const err = Math.abs(Math.log(sx)) + Math.abs(Math.log(sz));
        if (err < bestErr) { bestErr = err; best = k; bx = sx; bz = sz; }
      }
      if (best < 0) return null;
      return { ang, k: best, sx: bx, sz: bz };
    };

    // Footprints claimed by a real modelled apartment block.
    const aptItems = [];
    // Pick by STOREY COUNT first, then fit the plan. An apartment block is
    // never scaled vertically — that would stretch the floor heights and the
    // whole thing stops reading as a building — so the variant whose real
    // height is closest wins, and only the plan is stretched.
    const aptFit = (poly, area, h) => {
      const kit = this._aptKit;
      if (!kit) return null;
      const m = poly.length;
      if (m < 4 || m > 14) return null;
      let bestLen = 0;
      let ang = 0;
      for (let i = 0; i < m; i++) {
        const a = poly[i];
        const b2 = poly[(i + 1) % m];
        const d = Math.hypot(b2.x - a.x, b2.y - a.y);
        if (d > bestLen) { bestLen = d; ang = Math.atan2(b2.y - a.y, b2.x - a.x); }
      }
      const ca = Math.cos(-ang);
      const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      const len = u1 - u0;
      const dep = v1 - v0;
      if (len < 8 || dep < 5.5 || len > 52 || dep > 30) return null;
      if (len / dep > 4.0) return null;
      if (Math.abs(THREE.ShapeUtils.area(poly)) < len * dep * 0.64) return null;
      let best = -1;
      let bestErr = Infinity;
      let bx = 1; let bz = 1;
      for (let k = 0; k < kit.length; k++) {
        const e = kit[k];
        const sx = len / e.W;
        const sz = dep / e.D;
        if (sx < 0.48 || sx > 2.2 || sz < 0.48 || sz > 2.0) continue;
        const aniso = Math.max(sx / sz, sz / sx);
        if (aniso > 2.1) continue;
        // storey mismatch dominates the score: a 5-storey model on a 9-storey
        // footprint is a worse lie than a plan stretched 20%
        const dh = Math.abs(e.H - h) / Math.max(6, h);
        const err = dh * 3.0 + Math.abs(1 - sx) + Math.abs(1 - sz) + (aniso - 1);
        if (err < bestErr) { bestErr = err; best = k; bx = sx; bz = sz; }
      }
      if (best < 0) return null;
      return { ang, k: best, sx: bx, sz: bz };
    };

    // Footprints claimed by a real modelled shed / hangar.
    const shedItems = [];
    const shedFit = (poly, area, h) => {
      const kit = this._shedKit;
      if (!kit) return null;
      const m = poly.length;
      if (m < 4 || m > 14) return null;
      let bestLen = 0;
      let ang = 0;
      for (let i = 0; i < m; i++) {
        const a = poly[i];
        const b2 = poly[(i + 1) % m];
        const d = Math.hypot(b2.x - a.x, b2.y - a.y);
        if (d > bestLen) { bestLen = d; ang = Math.atan2(b2.y - a.y, b2.x - a.x); }
      }
      const ca = Math.cos(-ang);
      const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      const len = u1 - u0;
      const dep = v1 - v0;
      if (len < 12 || dep < 6.5) return null;
      if (len / dep > 6.5) return null;
      // a shed is a box: reject anything that does not fill its bounding box
      if (Math.abs(THREE.ShapeUtils.area(poly)) < len * dep * 0.60) return null;
      let best = -1;
      let bestErr = Infinity;
      let bx = 1; let bz = 1;
      for (let k = 0; k < kit.length; k++) {
        const e = kit[k];
        const sx = len / e.W;
        const sz = dep / e.D;
        // sheds take hard stretching far better than houses -- the ribs just
        // get wider -- but keep the two axes within half an order of each other
        if (sx < 0.35 || sx > 3.6 || sz < 0.35 || sz > 3.6) continue;
        const aniso = Math.max(sx / sz, sz / sx);
        if (aniso > 2.8) continue;
        const err = Math.abs(Math.log(sx)) + Math.abs(Math.log(sz)) + (aniso - 1);
        if (err < bestErr) { bestErr = err; best = k; bx = sx; bz = sz; }
      }
      if (best < 0) return null;
      // height from the real OSM height, clamped so a shed never looks like a tower
      const sy = Math.max(0.55, Math.min(2.0, h / kit[best].H));
      return { ang, k: best, sx: bx, sz: bz, sy };
    };

    // Footprints claimed by a real modelled house (see _houseKit).
    const houseItems = [];
    // Pick the kit house whose footprint is closest, allowing a modest uniform
    // scale — houses genuinely vary in size, and 0.82-1.3x keeps window and
    // door proportions believable where stretching one model would not.
    const houseFit = (poly, area) => {
      const kit = this._houseKit;
      if (!kit) return null;
      // Up to 16 corners: a real house footprint is often L-shaped or has a
      // porch bump, and rejecting those left 784 of them as photo boxes. The
      // oriented bounding box is a fine stand-in for a house that size.
      const m = poly.length;
      if (m < 4 || m > 16) return null;
      let bestLen = 0;
      let ang = 0;
      for (let i = 0; i < m; i++) {
        const a = poly[i];
        const b2 = poly[(i + 1) % m];
        const d = Math.hypot(b2.x - a.x, b2.y - a.y);
        if (d > bestLen) { bestLen = d; ang = Math.atan2(b2.y - a.y, b2.x - a.x); }
      }
      const ca = Math.cos(-ang);
      const sa = Math.sin(-ang);
      let u0 = Infinity; let u1 = -Infinity; let v0 = Infinity; let v1 = -Infinity;
      for (const pt of poly) {
        const u = pt.x * ca - pt.y * sa;
        const v = pt.x * sa + pt.y * ca;
        if (u < u0) u0 = u; if (u > u1) u1 = u;
        if (v < v0) v0 = v; if (v > v1) v1 = v;
      }
      const len = u1 - u0;
      const dep = v1 - v0;
      if (auditRows) window.__hfRej = window.__hfRej || [];
      const rej = (why) => {
        if (auditRows) window.__hfRej.push({ len: +len.toFixed(1), dep: +dep.toFixed(1), m, why });
        return null;
      };
      if (len < 5 || dep < 3.2 || len > 34 || dep > 18) return rej('extent');
      if (len / dep > 4.2) return rej('aspect');  // a long thin shed is not a house
      // Scale X and Z INDEPENDENTLY. Demanding a near-exact uniform match
      // rejected 688 of the 1149 unclaimed buildings around the airport --
      // three quarters of the district stayed a photo box because no kit
      // house happened to be the right proportion. A house stretched 15% on
      // one axis is invisible; a photograph of someone else's wall is not.
      let best = -1;
      let bestErr = Infinity;
      let bx = 1;
      let bz = 1;
      for (let k = 0; k < kit.length; k++) {
        const e = kit[k];
        const sx = len / e.W;
        const sz = dep / e.D;
        if (sx < 0.55 || sx > 1.95 || sz < 0.55 || sz > 1.95) continue;
        // keep the distortion visible-but-fine: a house stretched half again
        // on one axis still reads as a house; a photo box never does
        const aniso = Math.max(sx / sz, sz / sx);
        if (aniso > 1.8) continue;
        const err = Math.abs(1 - sx) + Math.abs(1 - sz) + (aniso - 1) * 1.5;
        if (err < bestErr) { bestErr = err; best = k; bx = sx; bz = sz; }
      }
      if (best < 0) return rej('no variant within scale/aniso limits');
      return { ang, k: best, sx: bx, sz: bz };
    };

    // Real 3D relief so blocks aren't flat boxes: instanced concrete balconies
    // + AC units placed on actual facade planes (chunked + distance-culled).
    const balconyItems = [];
    const acItems = [];
    const windowItems = []; // real recessed window frames (Blender kit)
    const corniceItems = []; // XIX-century roofline moldings
    const drainItems = []; // galvanized downpipes at building corners
    const entranceItems = []; // recessed doorways + canopy slabs
    let pbWinCount = 0; // pb relief frames get their own budget (see below)
    const grilleItems = []; // ground-floor window grilles
    const dishItems = []; // satellite dishes beside balconies
    const parapetItems = []; // flat-roof edge lips (aerial silhouette)
    const roofPropItems = []; // shaft/tank/vent/AC clutter on flat roofs
    // glass towers: corner mullion strips + horizontal slab bands + crown —
    // their curtain-wall texture mip-blurs at range, so the SILHOUETTE must
    // carry the floor grid (long 2000 m cull, towers read from across town)
    const glassMullionItems = [];
    const glassBandItems = [];

    // match a building centroid to its OSM facts (10 m grid, 9 m radius)
    const factsAt = (x, z) => {
      if (!this._facts) return null;
      const gi = Math.round(x / 10);
      const gj = Math.round(z / 10);
      let best = null;
      let bd = 9; // metres — these are centroid-to-centroid, so keep it tight
      // (90 here let a building 90 m away borrow its neighbour's storey count)
      for (let di = -1; di <= 1; di++) {
        for (let dj = -1; dj <= 1; dj++) {
          for (const it of this._facts.grid.get(`${gi + di}_${gj + dj}`) || []) {
            const d = Math.hypot(it[0] / 10 - x, it[1] / 10 - z);
            if (d < bd) { bd = d; best = it; }
          }
        }
      }
      return best;
    };
    // Expand OSM building:part volumes: a footprint that carries parts is
    // built FROM THE PARTS (Simple 3D Buildings), so a tower over a podium
    // gets its real stepped silhouette instead of one flat box. Parts keep
    // the parent's bi, so style, colours and damage stay per-building.
    const expanded = [];
    {
      let nParts = 0;
      for (let bi = 0; bi < total; bi++) {
        const b = buildings[bi];
        const n = (b.length - 1) / 2;
        if (n < 3) { expanded.push({ b, bi, off: 0 }); continue; }
        let minx = Infinity; let maxx = -Infinity; let minz = Infinity; let maxz = -Infinity;
        let cx = 0; let cz = 0;
        for (let i = 0; i < n; i++) {
          const x = b[1 + i * 2] / 10;
          const z = b[2 + i * 2] / 10;
          cx += x; cz += z;
          if (x < minx) minx = x;
          if (x > maxx) maxx = x;
          if (z < minz) minz = z;
          if (z > maxz) maxz = z;
        }
        cx /= n; cz /= n;
        let mine = null;
        if (this._parts) {
          const gi = Math.round(cx / 60);
          const gj = Math.round(cz / 60);
          for (let di = -1; di <= 1; di++) {
            for (let dj = -1; dj <= 1; dj++) {
              for (const q of this._parts.get(`${gi + di}_${gj + dj}`) || []) {
                if (q.cx >= minx && q.cx <= maxx && q.cz >= minz && q.cz <= maxz) {
                  (mine ??= []).push(q.it);
                }
              }
            }
          }
        }
        // only worth it when the parts actually differ in height — a single
        // part is just the same box again
        const stepped = mine && mine.length > 1 &&
          new Set(mine.map((it) => Math.round(it[0] / 50))).size > 1;
        if (!stepped) { expanded.push({ b, bi, off: 0 }); continue; }
        for (const it of mine) {
          const pb2 = [it[0]];
          for (let i = 2; i < it.length; i++) pb2.push(it[i]);
          expanded.push({ b: pb2, bi, off: it[1] / 10 });
        }
        nParts += mine.length;
      }
      if (nParts) console.log(`OSM 3D parts: ${nParts} volumes replace flat boxes`);
    }
    let nFacts = 0;
    for (let ei = 0; ei < expanded.length; ei++) {
      const { b, bi, off } = expanded[ei];
      let h = b[0] / 10;
      const n = (b.length - 1) / 2;
      if (n < 3) continue;
      if (this._mallBids?.has(bi)) continue; // real Blender mall stands here

      let cx = 0;
      let cz = 0;
      for (let i = 0; i < n; i++) {
        cx += b[1 + i * 2] / 10;
        cz += b[2 + i * 2] / 10;
      }
      cx /= n;
      cz /= n;
      if (excl.some((e) => (cx - e.x) ** 2 + (cz - e.z) ** 2 < e.r2)) continue;
      // OSM ground truth wins over the satellite height estimate
      const facts = factsAt(cx, cz);
      if (facts) {
        nFacts++;
        if (facts[2] > 0) h = facts[2] * 3.1 + 0.9; // storeys -> metres + parapet
      }
      // THE GROUND UNDER A FOOTPRINT IS NOT ONE NUMBER.
      //
      // This used to extrude every building between heightAt(CENTROID) - 2 and
      // heightAt(centroid) + h. On the flat that is right; on a Tbilisi
      // hillside it is not, because the uphill corner of the same outline is
      // metres above the centroid and the wall there is buried that deep.
      // Measured over the built city: 10,630 of 25,814 buildings had a wall
      // more than a metre under the surface, 4,416 more than two -- and 175
      // had their ROOF below the ground, which is a building you cannot see at
      // all. ("sometimes buildings are under grass. dont do it")
      //
      // So the box spans the ground its own outline actually covers: the floor
      // goes under the LOWEST point, so nothing is left floating over a slope,
      // and the roof stands its full height over the HIGHEST, so nothing is
      // swallowed. Capped at 20 m of each, because a handful of these sit on
      // cliff edges where the 60 m DEM cell is a ramp through the rock and the
      // honest reading is 30 m -- a cottage is better slightly buried than
      // standing on a thirty-metre plinth.
      const gc = this.heightAt(cx, cz);
      let gmin = gc;
      let gmax = gc;
      for (let i = 0; i < n; i++) {
        const gy = this.heightAt(b[1 + i * 2] / 10, b[2 + i * 2] / 10);
        if (gy < gmin) gmin = gy;
        if (gy > gmax) gmax = gy;
      }
      // `off` is a part's min_height: a tower that starts on a podium roof
      const base = gc - 2 + off;
      // The anchor for everything the FACADE carries -- window rows, balconies,
      // shopfronts, downpipes -- stays on the centroid: those are laid out
      // floor by floor from the front door, and dropping them with the skirt
      // would walk the whole elevation down the hill.
      const boxBase = off > 0 ? base : base - Math.min(gc - gmin, 20);
      const top = gc + h + Math.min(gmax - gc, 20);
      // Real 3D models stand ON the ground rather than being extruded through
      // it, so they only need the burial taken off them.
      const propBase = Math.max(base, gmax - 2);

      // --- style: which Tbilisi building is this? ---
      const contour = [];
      for (let i = 0; i < n; i++) contour.push(new THREE.Vector2(b[1 + i * 2] / 10, b[2 + i * 2] / 10));
      const area = Math.abs(THREE.ShapeUtils.area(contour));
      if (auditRows) obbOf(contour);
      const district = districtAt(cx, cz);
      const inOldTown =
        district === 'historic' || (cx - OLD_TOWN.x) ** 2 + (cz - OLD_TOWN.z) ** 2 < OLD_TOWN.r ** 2;

      // ---- REAL MODEL instead of an extruded box -------------------------
      // A Soviet 5-storey slab is not a shape that needs guessing: it is a
      // ~11.4 m deep rectangle, 24-72 m long, 5 storeys high, and it was
      // assembled from 12 m sections. Where the footprint says that is what
      // this is, the prism is skipped entirely and a real modelled block
      // (tools/build_panel_block.py) is placed on the footprint instead.
      if (this._panelKit && district === 'soviet' && h >= 13 && h <= 19.5) {
        const fit = panelFit(contour);
        if (fit) {
          panelItems.push({ x: cx, z: cz + 0, y: propBase, ang: fit.ang, sections: fit.sections, bi });
          audit(cx, cz, area, h, n, 'panel_block', `${fit.sections} sections`);
          continue;   // NO extrusion for this building
        }
      }

      // Small suburban houses get a real 3D house MODEL (Kenney kit) instead of
      // an extruded box — orient to the footprint, style by hash. Old-town keeps
      // its plaster/red-roof extrusions.
      // area cap 220 (was 340): ~18 m footprints are small apartment blocks,
      // not cottages — they get the extruded facade treatment below instead.
      // Suburban houses used to be swapped for one of 21 Kenney models scaled
      // to sqrt(area) — 24 000 buildings wearing 21 shapes, none of them the
      // house's REAL outline. They are extruded like everything else now:
      // their true footprint, a gabled roof, real wall photos and kit windows
      // and doors, so every house in Tbilisi is its own building.
      const cottage = !inOldTown && h < 13 && area > 40 && area < 260;
      const pitchedForced = false;   // reserved: OSM roof:shape overrides

      // ---- REAL HOUSE MODEL instead of a photo-textured prism ------------
      // Everything outside the named districts (the airport, Orkhevi,
      // Ponichala, every village edge) landed here wearing a tiled photo and
      // kit boxes for its windows and door. A house is a shape we can model.
      if (this._houseKit && cottage && !pitchedForced) {
        const hf = houseFit(contour, area);
        if (hf) {
          houseItems.push({ x: cx, z: cz, y: propBase, ang: hf.ang, k: hf.k, sx: hf.sx, sz: hf.sz, bi });
          audit(cx, cz, area, h, n, 'house', `${this._houseKit[hf.k].name} ${hf.sx.toFixed(2)}x${hf.sz.toFixed(2)}`);
          continue;   // NO extrusion for this building
        }
      }

      // ---- REAL GARAGE / SHED / KIOSK / HUT ------------------------------
      // First of the model tests: a 7-40 m² footprint is a garage or a shed
      // whatever the height estimate claims. Those heights are WRONG on small
      // footprints -- the audit found 4x4 m buildings reported as 12 m tall --
      // so the model's own height is used and `h` is ignored entirely.
      if (this._outKit && area <= 46) {
        const of = outFit(contour);
        if (of) {
          outItems.push({ x: cx, z: cz, y: propBase, ang: of.ang, k: of.k,
            sx: of.sx, sz: of.sz, bi });
          audit(cx, cz, area, h, n, 'outbuilding', this._outKit[of.k].name);
          continue;
        }
      }

      // ---- REAL APARTMENT BLOCK -----------------------------------------
      // Must come BEFORE the shed test: a 400 m² building 15 m tall is a
      // block of flats, and the shed fitter would happily claim it as a
      // warehouse.
      if (this._aptKit && !cottage && h >= 8.5 && h <= 34 && area >= 120 && area <= 1600) {
        const af = aptFit(contour, area, h);
        if (af) {
          aptItems.push({ x: cx, z: cz, y: propBase, ang: af.ang, k: af.k,
            sx: af.sx, sz: af.sz, bi });
          audit(cx, cz, area, h, n, 'apartment', `${this._aptKit[af.k].name} ${af.sx.toFixed(2)}x${af.sz.toFixed(2)}`);
          continue;
        }
      }

      // ---- REAL SHED / HANGAR -------------------------------------------
      // Too big for a house, too short for a block: the warehouses that the
      // airport audit showed were the biggest fakes left in the district
      // (the largest is 40,602 m²).
      // (no `pb` test here: it is declared below, and reading it from this
      // point is a temporal-dead-zone throw that killed the whole world build)
      if (this._shedKit && area >= 260 && h <= 22 && !cottage) {
        const sf = shedFit(contour, area, h);
        if (sf) {
          shedItems.push({ x: cx, z: cz, y: propBase, ang: sf.ang, k: sf.k,
            sx: sf.sx, sy: sf.sy, sz: sf.sz, bi });
          audit(cx, cz, area, h, n, 'shed', `${this._shedKit[sf.k].name} ${sf.sx.toFixed(2)}x${sf.sz.toFixed(2)}`);
          continue;   // NO extrusion
        }
      }

      const key = `${Math.floor(cx / CHUNK)}_${Math.floor(cz / CHUNK)}`;
      let ch = chunks.get(key);
      // wallIdx buckets: 0-3 painted facade styles, 4-9 real wall-material
      // variants for buildings that carry 3D kit windows (see WALL_VARIANTS)
      if (!ch) chunks.set(key, (ch = { pos: [], col: [], uv: [], wallIdx: Array.from({ length: 4 + this._wallNames.length + (this._loggiaKeys?.length || 0) }, () => []), roofIdx: Array.from({ length: this._roofNames.length }, () => []), pbIdx: new Map(), bld: [] }));
      const pb = pbOf.get(bi) || null; // THIS building's own photo, or none
      const r1 = hash(bi + 1);
      const r2 = hash(bi + 77777);
      const shade = 0.86 + 0.18 * hash(bi + 424242); // per-building brightness
      // Per-building hue cast: two buildings sharing a tile + palette slot
      // could still clone — a gentle warm/cool tilt (±5.5% per channel, hue
      // wheel phase from the building id) makes every facade its own.
      const hueA = hash(bi + 515151) * 6.283;
      const hueT = [
        1 + 0.055 * Math.sin(hueA),
        1 + 0.055 * Math.sin(hueA + 2.094),
        1 + 0.055 * Math.sin(hueA + 4.189),
      ];

      let wall;
      let roof;
      let pitched = false;
      let style = 0; // 0 windows, 1 loggia balconies, 2 glass, 3 old-Tbilisi wooden balconies
      // In Soviet microdistricts 16-storey towers are panel, not glass.
      const glassMin = district === 'soviet' ? 60 : 45;
      if (h >= glassMin) {
        wall = GLASS_WALLS[(bi * 7919) % GLASS_WALLS.length];
        roof = DARK_ROOFS[(bi * 104729) % DARK_ROOFS.length];
        style = 2;
      } else if (cottage) {
        // A 40-260 m² footprint under 13 m is a house wherever it stands —
        // this must outrank the district rules below, or a 100 m² cottage in
        // Gldani would be dressed as a panel block with loggia balconies.
        wall = HOUSE_WALLS[(bi * 7919) % HOUSE_WALLS.length];
        roof = COTTAGE_ROOFS[(bi * 104729) % COTTAGE_ROOFS.length];
        pitched = n <= 14 && r2 < 0.93; // a few flat-roofed infills for variety
        style = 0;
      } else if (district === 'historic' && area < 900 && h < 30) {
        wall = WARM_WALLS[(bi * 7919) % WARM_WALLS.length];
        roof = RED_ROOFS[(bi * 104729) % RED_ROOFS.length];
        pitched = n <= 10 && h < 15;
        style = 3; // ornate XIX-century facades of the historic center
      } else if (district === 'soviet' && h >= 10) {
        wall = PANEL_WALLS[(bi * 7919) % PANEL_WALLS.length];
        roof = FLAT_ROOFS[(bi * 104729) % FLAT_ROOFS.length];
        style = r1 < 0.88 ? 1 : 0; // microdistricts are wall-to-wall loggia blocks
      } else if (district === 'classic' && h >= 8 && h < 45) {
        wall = HOUSE_WALLS[(bi * 7919) % HOUSE_WALLS.length];
        roof = r2 < 0.4 ? RED_ROOFS[(bi * 104729) % RED_ROOFS.length] : FLAT_ROOFS[(bi * 104729) % FLAT_ROOFS.length];
        style = r1 < 0.8 ? 0 : 3; // Vake/Vera classical plaster
      } else if (h >= 13) {
        wall = PANEL_WALLS[(bi * 7919) % PANEL_WALLS.length];
        roof = FLAT_ROOFS[(bi * 104729) % FLAT_ROOFS.length];
        style = r1 < 0.75 ? 1 : 0; // most Soviet blocks wear loggia balconies
      } else if (inOldTown && area < 500) {
        wall = WARM_WALLS[(bi * 7919) % WARM_WALLS.length];
        roof = RED_ROOFS[(bi * 104729) % RED_ROOFS.length];
        pitched = n <= 10;
        style = 3; // carved wooden balconies of old Tbilisi
      } else if (area < 320 && r1 < 0.85) {
        wall = HOUSE_WALLS[(bi * 7919) % HOUSE_WALLS.length];
        roof = r2 < 0.7 ? RED_ROOFS[(bi * 104729) % RED_ROOFS.length] : FLAT_ROOFS[(bi * 104729) % FLAT_ROOFS.length];
        pitched = r2 < 0.7 && n <= 10;
      } else {
        wall = PANEL_WALLS[(bi * 7919) % PANEL_WALLS.length];
        roof = FLAT_ROOFS[(bi * 104729) % FLAT_ROOFS.length];
      }
      if (auditRows) {
        // why did no real model claim this one?
        let why;
        if (h >= glassMin) why = `too tall for a model (${h.toFixed(0)} m, glass tower)`;
        else if (district === 'soviet' && h > 19.5) why = `soviet TOWER ${h.toFixed(0)} m — no tower model yet`;
        else if (cottage) why = `cottage but no kit house fits (shape/size)`;
        else if (area >= 260) why = `too big for a house (${Math.round(area)} m²), too short for a block`;
        else why = `${Math.round(area)} m², ${h.toFixed(0)} m — no model covers this`;
        audit(cx, cz, area, h, n, `extruded:style${style}`, why);
      }

      // OSM ground truth: a mapped roof:shape beats the style-chain guess
      if (facts && this._facts.shapes[facts[3]]) {
        const shape = this._facts.shapes[facts[3]];
        pitched = !(shape === 'flat' || shape === 'skillion') && n <= 16;
      }

      // 3D facade relief decisions — made BEFORE the wall vertices are pushed
      // so the wall material bucket AND the vertex tint can depend on them.
      const wantBalconies =
        (style === 1 || (style === 0 && district === 'soviet')) && h >= 10 && !pb && hash(bi + 999) < 0.45;
      // old-Tbilisi wooden galleries live alongside the window frames
      const wantWoodBalc = style === 3 && !pb && h >= 8 && hash(bi + 999) < 0.4;
      const nFloors = Math.min(16, Math.floor(h / 3.2));
      // Real window geometry (Blender kit): plaster/old-Tbilisi facades get 3D
      // frames and a real-material wall (buckets 4..9, with a subtle window
      // grid BAKED into the texture at the same 3 m cells) so the painted
      // canvas grid can't double the kit's. Loggia and glass towers skip
      // (their reliefs are balconies / curtain wall), pb photos keep their own
      // grid. h < 51: nFloors caps at 16, taller walls would go bare up top.
      // NOT gated on the window instance budget: past the cap the baked grid
      // still reads as fenestration, so no district turns into bare boxes.
      // cottages are 1-2 storeys: nFloors can be 1, so they need their own
      // gate (a single window row still sells the house)
      const wantWindows =
        (style === 0 || style === 3) && !pb && !wantBalconies &&
        h >= 5 && h < 51 && (nFloors >= 2 || (cottage && h >= 4));

      // Real photo facades and real wall-material tiles carry their own color
      // — those walls keep only a whisper of the palette tint (plus the
      // per-building shade), painted facades keep the full pastel tint.
      const lift = (c, f = 0.55) => Math.min(255, (c + (255 - c) * f) * shade);
      const lf = wantWindows ? 0.82 : 0.55;
      const wallC = pb ? [246, 246, 246] : [
        Math.min(255, lift(wall[0], lf) * hueT[0]),
        Math.min(255, lift(wall[1], lf) * hueT[1]),
        Math.min(255, lift(wall[2], lf) * hueT[2]),
      ];
      if (style === 2) {
        // per-tower glass tint: teal / blue / grey families + brightness swing
        // so a skyline of curtain-wall towers doesn't repeat one color
        const gf = hash(bi + 3111);
        const gTint = gf < 0.35 ? [0.86, 1.0, 0.99] : gf < 0.7 ? [0.87, 0.94, 1.06] : [0.98, 0.98, 0.97];
        const gAmp = 0.86 + 0.26 * hash(bi + 3222);
        for (let c = 0; c < 3; c++) wallC[c] = Math.min(255, wallC[c] * gTint[c] * gAmp);
      }
      // OSM ground truth: a mapped building:colour is the real paint on that
      // wall. Tint only 60 % of the way — the photo tile must still read as a
      // material. Must run BEFORE the vertex colours are pushed below.
      if (facts && facts[5] && !pb && /^#[0-9a-f]{6}$/i.test(facts[5])) {
        const col = facts[5];
        const mix = (c, t) => Math.min(255, c * 0.4 + (t * 0.55 + 255 * 0.45) * 0.6);
        wallC[0] = mix(wallC[0], parseInt(col.slice(1, 3), 16));
        wallC[1] = mix(wallC[1], parseInt(col.slice(3, 5), 16));
        wallC[2] = mix(wallC[2], parseInt(col.slice(5, 7), 16));
      }
      const dirty = [wallC[0] * 0.72, wallC[1] * 0.72, wallC[2] * 0.72]; // street-level grime
      // roofs are textured photos too; the bright steel sheet needs the full
      // red/rust tint, the midtone concrete needs lifting to read from the air
      const metalRoof = pitched || RED_ROOFS.includes(roof); // red/rust = sheet metal
      const rl = metalRoof ? 0.06 : 0.28;
      // Roofs are what the player actually looks at from a plane, and four
      // palette entries over a whole district read as a repeating checker.
      // A gentler version of the wall hue cast (±3.5%) plus the per-building
      // shade breaks that up without turning the skyline into confetti.
      const rh = 0.63; // damp the wall cast
      const roofC = [
        Math.min(255, lift(roof[0], rl) * (1 + (hueT[0] - 1) * rh)),
        Math.min(255, lift(roof[1], rl) * (1 + (hueT[1] - 1) * rh)),
        Math.min(255, lift(roof[2], rl) * (1 + (hueT[2] - 1) * rh)),
      ];

      const v0 = ch.pos.length / 3;

      // Walls: bottom ring (grimy, below-ground) + top ring, windows via UV.
      // u = perimeter meters / 3 (a window every 3 m), v = floors (3 m each).
      // Per-building texture phase: start each facade at its OWN bay column
      // (integer 3 m cell offset — kit frames and baked window grids stay
      // cell-aligned, but neighbours sharing a tile stop repeating
      // pixel-for-pixel). pb photos must anchor at the real corner: no shift.
      let perim = 0;
      const uP = pb ? 0 : Math.floor(hash(bi + 8111) * 8);
      const us = [uP];
      for (let i = 0; i < n; i++) {
        const j = (i + 1) % n;
        perim += Math.hypot(
          b[1 + j * 2] / 10 - b[1 + i * 2] / 10,
          b[2 + j * 2] / 10 - b[2 + i * 2] / 10
        );
        us.push(uP + perim / 3);
      }
      const vTop = h / 3;
      // Photo-textured walls (per-building pb photos + facade buckets 0/1/3)
      // must NOT run v below 0 on the buried bottom ring: with repeat
      // 1/floors the negative band wraps into the photo's opposite edge
      // (roofline/sky cross-fade) and smears it across the building base.
      // Those walls start at v=0 — the grime tint alone marks the base band.
      // Painted canvas, glass and the wall-material buckets 4-9 keep -2/3:
      // their tiles are seamless (and 4-9 bake-aligns frames to that origin).
      const vBot = pb || (!wantWindows && style !== 2) ? 0 : -2 / 3;
      for (let ring = 0; ring < 2; ring++) {
        const y = ring === 0 ? boxBase : top;
        const c = ring === 0 ? dirty : wallC;
        const v = ring === 0 ? vBot : vTop;
        for (let i = 0; i < n; i++) {
          ch.pos.push(b[1 + i * 2] / 10, y, b[2 + i * 2] / 10);
          ch.col.push(c[0], c[1], c[2]);
          ch.uv.push(us[i], v);
        }
      }
      // Kit-window buildings get a REAL windowless wall material (photo-cut
      // brick/plaster/panel tiles, buckets 4..9) instead of flat stucco:
      // 4 brick_red, 5 brick_dark, 6 plaster_decay, 7 plaster_yellow,
      // 8 plaster_pink, 9 panel_concrete — one variant per building, the
      // per-building vertex tint still multiplies on top for variety.
      // Real wall material: pick a FAMILY by district, then a specific tile
      // within that family by a second hash — so two neighbours that both
      // read as "brick" still wear different brickwork. Families come from
      // walls.json, so adding tiles widens the variety with no code change.
      let wallBucket = style;
      // Soviet housing: one real facade per BUILDING out of the curated set
      if (style === 1 && this._loggiaKeys.length) {
        wallBucket = this._loggiaBase + Math.floor(hash(bi + 91) * this._loggiaKeys.length)
          % this._loggiaKeys.length;
      }
      if (wantWindows) {
        const wv = hash(bi + 61);
        let fam;
        if (wv < 0.14) fam = 'brick'; // brick pockets (Chugureti/rail district)
        else if (style === 3 || district === 'historic') fam = wv < 0.62 ? 'plaster' : 'stone';
        else if (district === 'soviet') fam = wv < 0.68 ? 'panel' : 'plaster';
        else if (district === 'classic') fam = wv < 0.66 ? 'plaster' : wv < 0.84 ? 'stone' : 'panel';
        else fam = wv < 0.5 ? 'panel' : wv < 0.86 ? 'plaster' : 'stone';
        let pool = this._wallFams[fam];
        if (!pool?.length) pool = this._wallFams.plaster; // family not cut yet
        if (!pool?.length) pool = [4];
        wallBucket = pool[Math.floor(hash(bi + 62) * pool.length) % pool.length];
      }
      // OSM ground truth: real wall material picks the tile family, and a
      // mapped building:colour tints the photo toward the actual paint (only
      // 60 % of the way — the tile must still read as a material, not paint)
      if (facts) {
        const mat = this._facts.materials[facts[4]] || '';
        if (mat && wantWindows) {
          const fam = mat === 'brick' ? 'brick'
            : (mat === 'stone' || mat === 'sandstone' || mat === 'tuff') ? 'stone'
              : (mat === 'concrete' || mat === 'cement_block') ? 'panel'
                : mat === 'plaster' ? 'plaster' : null;
          const pool = fam && this._wallFams[fam]?.length ? this._wallFams[fam] : null;
          if (pool) {
            wallBucket = pool[Math.floor(hash(bi + 62) * pool.length) % pool.length];
          }
        }
      }
      let wallTarget = ch.wallIdx[wallBucket];
      if (pb) {
        if (!ch.pbIdx.has(pb.key)) ch.pbIdx.set(pb.key, { m: pb, idx: [] });
        wallTarget = ch.pbIdx.get(pb.key).idx;
      }
      for (let i = 0; i < n; i++) {
        const j = (i + 1) % n;
        wallTarget.push(v0 + i, v0 + j, v0 + n + j, v0 + i, v0 + n + j, v0 + n + i);
      }

      // 3D facade relief: protruding balconies on Soviet blocks, AC boxes on
      // most walls — makes buildings read as volumes, not textured boxes.
      const wantCornice =
        (style === 3 || (district === 'classic' && style === 0)) && !pitched && corniceItems.length < 60000;
      // Photo-facade buildings get 3D window frames + AC + drainpipes so the
      // photo reads as a volume, not wallpaper. floors === 1 tiles are
      // shopfront strips whose grid wouldn't align — leave those flat.
      const pbRelief = !!pb && pb.floors > 1 && h >= 5;
      if ((wantBalconies || wantWoodBalc || wantWindows || wantCornice || pbRelief || (!pb && h >= 6 && acItems.length < 50000)) && (nFloors >= 2 || cottage)) {
        // ---- kit variants: ONE consistent look per building, variety between
        // buildings (tools/FACADE_KIT_TYPOLOGY.md archetypes) ----
        const vHash = hash(bi + 81);
        let winVariant = 'window';
        if (style === 3 || district === 'historic') winVariant = vHash < 0.6 ? 'win_old' : 'window';
        else if (district === 'classic') winVariant = vHash < 0.5 ? 'win_stalin' : 'win_pvc';
        else if (district === 'soviet') winVariant = vHash < 0.55 ? 'window' : 'win_pvc';
        // brick walls (buckets 4/5) wear the W5 sash with brick hood + shutters
        if (wallBucket === 4 || wallBucket === 5) winVariant = 'win_brick';
        const shopGround = area > 400 && hash(bi + 271) < 0.35; // street-level shopfronts
        const balcVariant = (style === 3 || district === 'historic') ? 'balc_wood'
          : hash(bi + 433) < 0.5 ? 'balcony'
            : hash(bi + 577) < 0.25 ? 'balc_glazed'
              : hash(bi + 601) < 0.45 ? (hash(bi + 613) < 0.3 ? 'balc_loggia_p' : 'balc_loggia')
                : 'balc_metal';
        // Vake-Vera classic + restored old town: wrought-iron juliet guards
        // under some tall windows (B5 of the typology)
        const wantJuliet = (district === 'classic' || style === 3) &&
          (winVariant === 'win_stalin' || winVariant === 'win_old') && hash(bi + 719) < 0.5;
        const wantGrille = (district === 'soviet' || district === 'classic') && hash(bi + 357) < 0.3;
        let entSeg = -1; // the longest wall gets the entrance door
        let entBest = 0;
        for (let i = 0; i < n; i++) {
          const j = (i + 1) % n;
          const l2 = (b[1 + j * 2] - b[1 + i * 2]) ** 2 + (b[2 + j * 2] - b[2 + i * 2]) ** 2;
          if (l2 > entBest) { entBest = l2; entSeg = i; }
        }
        let pipeCount = 0;
        for (let i = 0; i < n; i++) {
          const j = (i + 1) % n;
          const x1 = b[1 + i * 2] / 10;
          const z1 = b[2 + i * 2] / 10;
          const ex = b[1 + j * 2] / 10 - x1;
          const ez = b[2 + j * 2] / 10 - z1;
          const len = Math.hypot(ex, ez);
          if (len < 5) continue;
          let nx = -ez / len;
          let nz = ex / len;
          const mx = x1 + ex / 2;
          const mz = z1 + ez / 2;
          if ((mx - cx) * nx + (mz - cz) * nz < 0) { nx = -nx; nz = -nz; } // face outward
          const ang = Math.atan2(nx, nz);
          const bays2 = Math.floor(len / 3.4);
          if ((wantBalconies || wantWoodBalc) && balconyItems.length < 160000) {
            for (let k = 0; k < bays2; k++) {
              if (hash(bi * 131 + i * 17 + k) < 0.45) continue;
              const t = (k + 0.5) / bays2;
              const wx = x1 + ex * t + nx * 0.08;
              const wz = z1 + ez * t + nz * 0.08;
              const gy = this.heightAt(wx, wz); // slope: skip buried floors
              for (let f = 1; f < nFloors; f += 2) {
                if (balconyItems.length >= 160000) break;
                const by = base + 2 + f * 3.2 + 0.4;
                if (by < gy + 0.4) continue;
                balconyItems.push({ x: wx, z: wz, y: by, ang, variant: balcVariant });
                if (dishItems.length < 30000 && hash(bi * 131 + i * 17 + k * 7 + f + 40009) < 0.15) {
                  const sgn = t < 0.5 ? 1 : -1; // beside the balcony, toward the wall middle
                  dishItems.push({
                    x: wx + (ex / len) * 1.55 * sgn + nx * 0.06,
                    z: wz + (ez / len) * 1.55 * sgn + nz * 0.06,
                    y: by + 1.2,
                    ang,
                  });
                }
              }
            }
          }
          if (wantWindows && windowItems.length < 500000) {
            // Instance budget fairness: instead of cutting whole late-order
            // districts off at the cap, THIN per-building as it approaches —
            // every 2nd upper floor keeps its 3D frame, the baked texture
            // grid fills the gaps. Ground+1st floors always keep frames.
            const thin = windowItems.length > 360000 ? 2 : 1;
            // texture u runs in 3 m cells around the WHOLE perimeter (us[]),
            // so frames must sit at global cell centers, not per-segment ones
            const u0 = us[i];
            const u1 = us[i + 1]; // us has n+1 entries; [i+1] is this segment's end
            for (let k = Math.ceil(u0 + 0.5) - 0.5; k + 0.5 <= u1; k += 1) {
              const t = ((k - u0) * 3) / len;
              if (t < 0.02 || t > 0.98) continue; // keep off the corners
              const wx = x1 + ex * t + nx * 0.04;
              const wz = z1 + ez * t + nz * 0.04;
              // On slopes the terrain at the wall can sit meters above `base`
              // (buildings extrude from 2 m below their centroid ground) —
              // don't hang window frames inside the hillside.
              const gy = this.heightAt(wx, wz);
              // cottages: nFloors is 1, so walk one row past it and let the
              // roofline guard below decide what actually fits
              const fMax = cottage ? Math.max(1, nFloors) : nFloors - 1;
              for (let f = 1; f <= fMax; f++) {
                if (thin > 1 && f > 1 && (f + bi) % thin) continue;
                const wy = base + f * 3 + 1.5;
                if (wy + 1.1 > top) break; // would poke through the eaves
                if (wy < gy + 0.8) continue; // cell (mostly) below ground
                windowItems.push({
                  x: wx,
                  z: wz,
                  y: wy,
                  ang,
                  variant: f === 1 && shopGround ? 'win_shop' : winVariant,
                });
                if (f === 1 && wantGrille && !shopGround && grilleItems.length < 60000) {
                  grilleItems.push({ x: wx + nx * 0.02, z: wz + nz * 0.02, y: base + 4.5, ang });
                }
                if (wantJuliet && f >= 2 && !(f === 1 && shopGround) &&
                  balconyItems.length < 160000 && hash(bi * 131 + i * 17 + k * 3 + f) < 0.35) {
                  balconyItems.push({
                    x: wx + nx * 0.03, z: wz + nz * 0.03, y: wy - 1.15, ang, variant: 'balc_juliet',
                  });
                }
              }
            }
          }
          if (wantCornice && corniceItems.length < 60000) {
            const segs = Math.max(1, Math.round(len / 3));
            for (let k = 0; k < segs; k++) {
              const t = ((k + 0.5) * len) / segs / len;
              corniceItems.push({
                x: x1 + ex * t + nx * 0.02,
                z: z1 + ez * t + nz * 0.02,
                y: top - 0.15,
                ang,
                sx: len / segs / 3, // stretch the 3 m molding to the segment
              });
            }
          }
          if (pipeCount < 2 && drainItems.length < 80000 && len > 7 && hash(bi * 31 + i * 7 + 5) < 0.6) {
            const tt = Math.min(0.45, 1.2 / len); // hug the corner of the wall
            const px = x1 + ex * tt + nx * 0.13;
            const pz = z1 + ez * tt + nz * 0.13;
            // slopes: start the pipe at the REAL ground under this corner so it
            // neither dangles in the air nor drowns in the hillside
            const gy = this.heightAt(px, pz);
            if (gy <= base + h * 0.5 && Math.abs(gy - base) <= 6) {
              const py = Math.min(base + 2, gy) - 0.3;
              drainItems.push({
                x: px,
                z: pz,
                y: py,
                ang,
                sy: Math.max(1, (base + 2 + h - py) / 3), // stretch the 3 m section to the full facade
              });
              pipeCount++;
            }
          }
          if (i === entSeg && !pb && (h >= 6 || cottage) && entranceItems.length < 60000) {
            // snap to a 3 m texture-cell EDGE so the canopy dodges the window frames
            const kEdge = Math.round((us[i] + us[i + 1]) / 2);
            const t = THREE.MathUtils.clamp(((kEdge - us[i]) * 3) / len, 0.12, 0.88);
            const px = x1 + ex * t + nx * 0.06;
            const pz = z1 + ez * t + nz * 0.06;
            // Doorways live on the TERRAIN, not at the extrusion base: on
            // slopes the ground at the wall can be meters above/below `base`.
            // Skip the door where the facade is buried in the hillside.
            const gy = this.heightAt(px, pz);
            if (gy <= base + h * 0.5 && Math.abs(gy - base) <= 6) {
              entranceItems.push({
                x: px, z: pz, y: gy + 0.05, ang,
                // historic/classic stock keeps carved wooden double doors;
                // Soviet blocks keep the concrete-canopy panel entrance
                variant: (style === 3 || district === 'historic' ||
                  (district === 'classic' && hash(bi + 833) < 0.7) ||
                  (cottage && hash(bi + 833) < 0.45)) ? 'ent_old' : 'entrance',
              });
            }
          }
          if (i === 0 && !pb && h >= 6 && acItems.length < 50000 && hash(bi + 31337) < 0.35) {
            for (let u = 0; u < 2 && bays2 > 1; u++) {
              const t = (Math.floor(hash(bi * 7 + u * 313) * bays2) + 0.5) / bays2;
              const f = 1 + Math.floor(hash(bi * 13 + u * 71) * Math.max(1, nFloors - 1));
              const px = x1 + ex * t + nx * 0.1;
              const pz = z1 + ez * t + nz * 0.1;
              const py = base + 2 + f * 3.2 - 1.1;
              if (py < this.heightAt(px, pz) + 0.5) continue; // buried on slope
              acItems.push({ x: px, z: pz, y: py, ang });
            }
          }
          // pb photo facades: the photo already paints windows on the global
          // 3 m cell grid (u = perimeter/3, repeat 1/bays), so thin 3D frames
          // at those same cells give the flat wallpaper real relief — the same
          // trick as the baked-grid material walls. No balconies (the photo
          // may show its own), no ground floor (doors/shops in the photo).
          // Own budget: only ~280 pb buildings exist, and sharing the tail of
          // the 500 k window pool starved every pb building built late.
          if (pbRelief && pbWinCount < 40000) {
            const u0 = us[i];
            const u1 = us[i + 1];
            for (let k = Math.ceil(u0 + 0.5) - 0.5; k + 0.5 <= u1; k += 1) {
              const t = ((k - u0) * 3) / len;
              if (t < 0.02 || t > 0.98) continue; // keep off the corners
              const wx = x1 + ex * t + nx * 0.03;
              const wz = z1 + ez * t + nz * 0.03;
              const gy = this.heightAt(wx, wz);
              for (let f = 1; f < nFloors; f++) {
                const wy = base + f * 3 + 1.5;
                if (wy < gy + 0.8) continue; // buried on slope
                windowItems.push({ x: wx, z: wz, y: wy, ang, variant: 'window' });
                pbWinCount++;
              }
            }
            // a few AC boxes under random painted windows sell the depth
            if (i === entSeg && acItems.length < 50000) {
              const nAC = 2 + Math.floor(hash(bi + 911) * 3);
              for (let u = 0; u < nAC; u++) {
                const kc = Math.ceil(u0 + 0.5) - 0.5 + Math.floor(hash(bi * 17 + u * 131) * Math.max(1, u1 - u0 - 1));
                const t = THREE.MathUtils.clamp(((kc - u0) * 3) / len, 0.04, 0.96);
                const f = 1 + Math.floor(hash(bi * 13 + u * 71) * Math.max(1, nFloors - 1));
                const px = x1 + ex * t + nx * 0.1;
                const pz = z1 + ez * t + nz * 0.1;
                const py = base + f * 3 + 0.55; // just below the painted sill
                if (py < this.heightAt(px, pz) + 0.5) continue;
                acItems.push({ x: px, z: pz, y: py, ang });
              }
            }
          }
        }
      }

      // Glass curtain-wall relief: a bare textured box mip-blurs into a solid
      // slab from the air, so towers get real 3D structure — full-height
      // corner mullion strips, dark aluminum slab bands every 3 floors on
      // walls >= 8 m, and a crown band capping the parapet line.
      if (style === 2 && glassBandItems.length + glassMullionItems.length < 40000) {
        for (let i = 0; i < n; i++) {
          const iPrev = (i + n - 1) % n;
          const j = (i + 1) % n;
          const px = b[1 + i * 2] / 10;
          const pz = b[2 + i * 2] / 10;
          const ax = px - b[1 + iPrev * 2] / 10;
          const az = pz - b[2 + iPrev * 2] / 10;
          const ex = b[1 + j * 2] / 10 - px;
          const ez = b[2 + j * 2] / 10 - pz;
          const la = Math.hypot(ax, az);
          const le = Math.hypot(ex, ez);
          if (!la || !le) continue;
          // corner strip only at REAL corners (footprints are dense polylines)
          const turn = Math.atan2((ax * ez - az * ex) / (la * le), (ax * ex + az * ez) / (la * le));
          if (Math.abs(turn) > 0.5) {
            let ox = px - cx;
            let oz = pz - cz;
            const ol = Math.hypot(ox, oz) || 1;
            glassMullionItems.push({
              x: px + (ox / ol) * 0.1,
              z: pz + (oz / ol) * 0.1,
              y: base,
              ang: Math.atan2(ex, ez),
              sy: (h + 2) / 3, // stretch the 3 m strip to the full facade
            });
          }
          if (le < 8) continue; // slab bands only on real wall runs
          let nx = -ez / le;
          let nz = ex / le;
          const mx = px + ex / 2;
          const mz = pz + ez / 2;
          if ((mx - cx) * nx + (mz - cz) * nz < 0) { nx = -nx; nz = -nz; } // face outward
          const ang = Math.atan2(nx, nz);
          const bx = mx + nx * 0.02;
          const bz = mz + nz * 0.02;
          for (let fy = 9; fy < h - 2; fy += 9) { // one band every 3 floors
            glassBandItems.push({ x: bx, z: bz, y: base + 2 + fy, ang, sx: le / 3 });
          }
          // crown: a taller band right under the roof line
          glassBandItems.push({ x: bx, z: bz, y: top - 0.55, ang, sx: le / 3, sy: 2.6 });
        }
      }

      // Roof ring — planar world UVs (1 unit = 3 m) onto the roof photos.
      const r0 = ch.pos.length / 3;
      // pick a specific roof tile inside the right family, per building
      const rPool = this._roofFams[metalRoof ? 'metal' : 'flat'];
      const rIdx = ch.roofIdx[rPool[Math.floor(hash(bi + 4242) * rPool.length) % rPool.length]];
      for (let i = 0; i < n; i++) {
        ch.pos.push(b[1 + i * 2] / 10, top, b[2 + i * 2] / 10);
        ch.col.push(roofC[0], roofC[1], roofC[2]);
        ch.uv.push(b[1 + i * 2] / 30, b[2 + i * 2] / 30);
      }
      if (pitched) {
        // GABLE roof: a ridge LINE along the footprint's long axis, not a
        // single centroid apex — a tent on a 6x14 m house reads as a pyramid,
        // real Tbilisi houses have a ridge with two slopes and gable ends.
        // Each wall edge fans to the nearer ridge end; where the choice flips
        // between neighbouring edges, one triangle closes the seam.
        const rise = THREE.MathUtils.clamp(1.8 + Math.sqrt(area) * 0.09, 2, 4.5);
        let bestE = 0;
        let ax = 1;
        let az = 0;
        for (let i = 0; i < n; i++) {
          const j = (i + 1) % n;
          const dx = b[1 + j * 2] / 10 - b[1 + i * 2] / 10;
          const dz = b[2 + j * 2] / 10 - b[2 + i * 2] / 10;
          const l2 = dx * dx + dz * dz;
          if (l2 > bestE) { bestE = l2; const l = Math.sqrt(l2); ax = dx / l; az = dz / l; }
        }
        let tmin = Infinity;
        let tmax = -Infinity;
        for (let i = 0; i < n; i++) {
          const t = (b[1 + i * 2] / 10 - cx) * ax + (b[2 + i * 2] / 10 - cz) * az;
          if (t < tmin) tmin = t;
          if (t > tmax) tmax = t;
        }
        // ridge stops short of the gable walls so the end slopes are hipped
        // just a little — a full-length ridge leaves vertical gable triangles
        // the wall geometry doesn't have
        const half = Math.max(0, (tmax - tmin) / 2 - Math.min(2.5, (tmax - tmin) * 0.18));
        const rv = ch.pos.length / 3;
        for (const s of [-1, 1]) {
          ch.pos.push(cx + ax * half * s, top + rise, cz + az * half * s);
          ch.col.push(roofC[0], roofC[1], roofC[2]);
          ch.uv.push((cx + ax * half * s) / 30, (cz + az * half * s) / 30);
        }
        const pickRidge = (i) => {
          const j = (i + 1) % n;
          const mt = ((b[1 + i * 2] + b[1 + j * 2]) / 20 - cx) * ax
            + ((b[2 + i * 2] + b[2 + j * 2]) / 20 - cz) * az;
          return mt < 0 ? 0 : 1;
        };
        let prev = pickRidge(n - 1);
        for (let i = 0; i < n; i++) {
          const j = (i + 1) % n;
          const cur = pickRidge(i);
          if (cur !== prev) rIdx.push(rv + prev, r0 + i, rv + cur); // seam closer
          rIdx.push(r0 + i, r0 + j, rv + cur);
          prev = cur;
        }
      } else {
        let tris;
        try {
          tris = THREE.ShapeUtils.triangulateShape(contour, []);
        } catch {
          tris = [];
        }
        if (!tris.length) {
          for (let i = 1; i < n - 1; i++) tris.push([0, i, i + 1]); // degenerate footprint: fan
        }
        for (const t of tris) rIdx.push(r0 + t[0], r0 + t[1], r0 + t[2]);
      }

      // Damage bookkeeping: this building owns the contiguous vertex range
      // [v0, vEnd) of its chunk, so a bullet's hit face maps straight back to
      // the building (see buildingFromFace). HP scales with size; stage 0-9
      // walks it from pristine to a rubble mound.
      {
        let minx = Infinity, maxx = -Infinity, minz = Infinity, maxz = -Infinity;
        for (const p of contour) {
          if (p.x < minx) minx = p.x;
          if (p.x > maxx) maxx = p.x;
          if (p.y < minz) minz = p.y;
          if (p.y > maxz) maxz = p.y;
        }
        // HP scales with the real MASS of the thing (height × footprint): a
        // rocket (260) must blow a hole in a building, not delete it. A small
        // house still goes down in two, a Soviet block takes five, a tower
        // more than ten — which is what makes the hole damage worth having.
        const maxHp = Math.round(THREE.MathUtils.clamp(180 + h * 14 + area * 0.35, 280, 3200));
        ch.bld.push({
          bi, v0, v1: ch.pos.length / 3, base: boxBase, top, cx, cz,
          minx, maxx, minz, maxz,
          hp: maxHp, maxHp, stage: 0, orig: null, addr: null,
          // blast-hole bookkeeping: which material this facade wears, so the
          // tessellated shell that replaces it renders identically
          wallBucket: pb ? -1 : wallBucket, pbKey: pb ? pb.key : null,
        });
      }

      // Roof clutter: flat roofs are most of what the plane sees — parapet
      // lips along every edge + shaft/tank/vent boxes so they stop reading
      // as bare gray slabs from the air (long 1500 m cull in the prop layer).
      if (!pitched && h >= 10) {
        if (parapetItems.length < 70000) {
          for (let i = 0; i < n; i++) {
            const j = (i + 1) % n;
            const px1 = b[1 + i * 2] / 10;
            const pz1 = b[2 + i * 2] / 10;
            const pex = b[1 + j * 2] / 10 - px1;
            const pez = b[2 + j * 2] / 10 - pz1;
            const plen = Math.hypot(pex, pez);
            if (plen < 2.5) continue;
            // one stretched lip per wall segment (plain profile stretches fine)
            parapetItems.push({
              x: px1 + pex / 2,
              z: pz1 + pez / 2,
              y: top,
              ang: Math.atan2(-pez / plen, pex / plen),
              sx: plen / 3,
            });
          }
        }
        if (roofPropItems.length < 50000) {
          const rh1 = hash(bi + 90001);
          const rr = Math.sqrt(area) * 0.22; // stay well inside the footprint
          if (h >= 20) {
            roofPropItems.push({
              x: cx + (rh1 - 0.5) * rr, z: cz + (hash(bi + 90002) - 0.5) * rr,
              y: top, ang: rh1 * 6.28, variant: 'roof_box',
            });
          }
          if (rh1 < 0.55) {
            roofPropItems.push({
              x: cx + (hash(bi + 90003) - 0.5) * rr * 1.6,
              z: cz + (hash(bi + 90004) - 0.5) * rr * 1.6,
              y: top, ang: hash(bi + 90012) * 6.28, variant: 'roof_tank',
            });
          }
          const nv = 2 + Math.floor(hash(bi + 90005) * 3);
          for (let v2 = 0; v2 < nv; v2++) {
            roofPropItems.push({
              x: cx + (hash(bi * 3 + v2 * 17 + 90006) - 0.5) * rr * 2,
              z: cz + (hash(bi * 5 + v2 * 29 + 90007) - 0.5) * rr * 2,
              y: top, ang: hash(bi + v2 + 90013) * 6.28, variant: 'roof_vent',
            });
          }
          if (hash(bi + 90008) < 0.4) {
            roofPropItems.push({ // roof AC cluster unit
              x: cx + (hash(bi + 90009) - 0.5) * rr * 2,
              z: cz + (hash(bi + 90010) - 0.5) * rr * 2,
              y: top + 0.25, ang: hash(bi + 90011) * 6.28, variant: 'ac',
            });
          }
        }
      }

      if (ei % 4000 === 3999) {
        onProgress(`შენდება სახლები… ${Math.round((ei / expanded.length) * 100)}%`);
        await nextFrame();
      }
    }

    const facades = makeFacadeTextures();
    // Wall materials: 0-3 painted facade styles, then ONE bucket per real
    // photo-cut tile in walls.json (brick / plaster / stone / panel families).
    // Every real-material bucket starts on the plain stucco canvas
    // (facades[4]) and swaps to its photo tile once _loadWallMaterials runs.
    const NLOG = this._loggiaKeys.length; // extra per-building loggia buckets
    const wallMats = Array.from({ length: 4 + this._wallNames.length + NLOG }, (_, i) =>
      new THREE.MeshLambertMaterial({
        map: facades[Math.min(i, 4)],
        vertexColors: true,
        flatShading: true,
        side: THREE.DoubleSide,
      })
    );
    // One material per roof tile (tools/build_roof_materials.py). The first
    // flat and first metal tile keep the old photo textures as a fallback so
    // the game still looks right with roofs.json missing.
    const roofMats = this._roofNames.map(() => new THREE.MeshLambertMaterial({
      vertexColors: true,
      flatShading: true,
      side: THREE.DoubleSide,
    }));
    const roofFlatMat = roofMats[this._roofFams.flat[0]];
    const roofMetalMat = roofMats[this._roofFams.metal[0]];
    this._loadRoofMaterials(roofMats);
    // Soviet-block material variants so neighbouring chunks alternate real
    // facade photos: panel2 (tan loggias), tower16 (grey high-rise), khrush5
    const loggiaMatB = wallMats[1].clone();
    const loggiaMatC = wallMats[1].clone();
    const loggiaMatD = wallMats[1].clone();
    const oldTbilisiMatB = wallMats[3].clone(); // ornate Aghmashenebeli band
    this._loadRealFacades(wallMats, [loggiaMatB, loggiaMatC, loggiaMatD], oldTbilisiMatB, roofFlatMat, roofMetalMat);
    this._loadWallMaterials(wallMats);
    this._loadLoggiaFacades(wallMats);
    const NW = wallMats.length; // number of wall styles
    let done = 0;
    for (const [key, ch] of chunks) {
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(ch.pos), 3));
      geo.setAttribute('color', new THREE.BufferAttribute(new Uint8Array(ch.col), 3, true));
      geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(ch.uv), 2));
      let total = ch.roofIdx.reduce((a, r) => a + r.length, 0);
      for (let s = 0; s < NW; s++) total += ch.wallIdx[s].length;
      for (const e of ch.pbIdx.values()) total += e.idx.length;
      const idx = new Uint32Array(total);
      let off = 0;
      const pbOff = new Map();
      // Skip EMPTY buckets: with 24 wall tiles + 16 loggia variants + roofs a
      // chunk has ~50 possible groups and most are unused in any given
      // kilometre — an empty group is a wasted draw call per chunk per frame.
      for (let s = 0; s < NW; s++) {
        const len = ch.wallIdx[s].length;
        if (!len) continue;
        idx.set(ch.wallIdx[s], off);
        geo.addGroup(off, len, s);
        off += len;
      }
      for (let r = 0; r < ch.roofIdx.length; r++) {
        const len = ch.roofIdx[r].length;
        if (!len) continue;
        idx.set(ch.roofIdx[r], off);
        geo.addGroup(off, len, NW + r);
        off += len;
      }
      const mats = [...wallMats, ...roofMats];
      // per-building photo facades: one extra group + material per texture
      for (const e of ch.pbIdx.values()) {
        idx.set(e.idx, off);
        geo.addGroup(off, e.idx.length, mats.length);
        pbOff.set(e.m.key, { off, mat: mats.length });
        mats.push(pbMaterial(e.m));
        off += e.idx.length;
      }
      // remember which material each building's facade wears — the blast
      // shell (breakwall.js) has to render in the very same one. Where its
      // triangles ended up is worked out on demand (see _triRanges), because
      // bucket offsets alone can't survive the pb/loggia material swaps.
      for (const r of ch.bld) {
        const pbo = r.pbKey !== null ? pbOff.get(r.pbKey) : null;
        r.wallMat = pbo ? pbo.mat : r.wallBucket;
      }
      geo.setIndex(new THREE.BufferAttribute(idx, 1));
      geo.computeBoundsTree();
      const [kx0, kz0] = key.split('_').map(Number);
      mats[1] = [wallMats[1], loggiaMatB, loggiaMatC, loggiaMatD][(kx0 * 3 + kz0 * 5) & 3];
      if ((kx0 * 7 + kz0) & 1) mats[3] = oldTbilisiMatB;
      const mesh = new THREE.Mesh(geo, mats);
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      mesh.matrixAutoUpdate = false;
      mesh.userData.buildings = ch.bld; // sorted by v0 — see buildingFromFace
      this.buildingMeshes.push(mesh);
      this.collidables.push(mesh);
      // record chunk center for distance culling ("only show what's near me");
      // update() attaches in-range chunks to the scene (raycasts don't need
      // scene membership — chunk transforms are identity, world coords baked)
      const [kx, kz] = key.split('_').map(Number);
      this.chunkMeshes.push({ mesh, cx: kx * CHUNK + CHUNK / 2, cz: kz * CHUNK + CHUNK / 2, maxDist: 0, lodD2: 0, cy: 0, lodMesh: null, hiOn: false, loOn: false });
      if (++done % 40 === 0) {
        onProgress(`შენდება უბნები… ${done}/${chunks.size}`);
        await nextFrame();
      }
    }
    if (nFacts) console.log(`OSM facts applied to ${nFacts} buildings (real storeys / roof shape / material / colour)`);
    onProgress('იდგმება საცხოვრებელი სახლები…');
    await nextFrame();
    await this._buildHouses(houses);
    await this._buildFacadeProps(balconyItems, acItems, windowItems, corniceItems,
      drainItems, entranceItems, grilleItems, dishItems, parapetItems, roofPropItems,
      glassMullionItems, glassBandItems);
    this._placePanelBlocks(panelItems);
    this._placeHouses(houseItems);
    this._placeSheds(shedItems);
    this._placeApts(aptItems);
    this._placeOut(outItems);
    if (auditRows) {
      this._bldAudit = auditRows;
      console.log(`building audit: ${auditRows.length} rows (?audit)`);
    }
    this._applySavedDamage(); // after props: collapsed buildings strip their props too
    this._loadAddresses(); // real street addresses, in the background
  }

  // ---- real modelled outbuildings ------------------------------------------
  // tools/build_outbuilding.py. Garages, sheds, kiosks, transformer huts: the
  // 7-40 m2 footprints that were the single biggest remaining bucket of
  // photo-textured prisms once the audit measured them.
  async _loadOutKit() {
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/outbuildings.glb', res, undefined, rej));
      gltf.scene.updateMatrixWorld(true);
      const parts = new Map();
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      const kit = [];
      for (const [name, meshes] of parts) {
        if (!/^(garage|shed_yard|kiosk|hut_)/.test(name)) continue;
        const geos = [];
        const mats = [];
        for (const o of meshes) {
          const g = o.geometry.clone();
          g.applyMatrix4(o.matrixWorld);
          geos.push(g);
          mats.push(o.material);
        }
        const merged = mergeGeometries(geos, true);
        if (!merged) continue;
        merged.computeBoundingBox();
        const bb = merged.boundingBox;
        const c = bb.getCenter(new THREE.Vector3());
        merged.translate(-c.x, -bb.min.y, -c.z);
        merged.computeBoundingBox();
        merged.computeBoundingSphere();
        for (const m of mats) {
        // NO multiplyScalar here: instanceColor MULTIPLIES the material
        // colour, so dimming the material and then tinting each instance
        // darkened everything twice (0.62 x 0.6 = 0.37) and the district came
        // out mud. The per-instance tint below carries the whole value.
          m.metalness = Math.min(m.metalness ?? 0, 0.35);
        }
        kit.push({ name, geo: merged, mats,
          W: merged.boundingBox.max.x - merged.boundingBox.min.x,
          D: merged.boundingBox.max.z - merged.boundingBox.min.z,
          H: merged.boundingBox.max.y - merged.boundingBox.min.y });
      }
      if (kit.length) {
        this._outKit = kit.sort((a, b) => a.W * a.D - b.W * b.D);
        console.log(`outbuilding kit: ${kit.map((k) => k.name).join(' ')}`);
      }
    } catch (e) {
      console.warn('outbuilding kit missing — garages stay extruded:', e.message);
    }
  }

  _placeOut(items) {
    if (!this._outKit || !items.length) return;
    const by = new Map();
    for (const it of items) {
      if (!by.has(it.k)) by.set(it.k, []);
      by.get(it.k).push(it);
    }
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const sv = new THREE.Vector3();
    let placed = 0;
    for (const [ki, list] of by) {
      const e = this._outKit[ki];
      // short range: a 4 m garage is invisible past a couple of hundred metres
      this._placeChunkedInstanced(e.geo, e.mats, list, (m, it) => {
        q.setFromAxisAngle(up, -it.ang);
        sv.set(it.sx, 1, it.sz);
        m.compose(new THREE.Vector3(it.x, it.y, it.z), q, sv);
      }, 800, true, 700, (c, it) => {
        const h = ((it.bi * 2654435761) % 1024) / 1024;
        c.setHSL(0.08 + 0.3 * h, 0.03 + 0.06 * h, 0.58 + 0.12 * h);
      });
      placed += list.length;
    }
    this._outPlacements = items;
    console.log(`real outbuildings: ${placed} footprints`);
  }

  // ---- real modelled apartment blocks --------------------------------------
  // tools/build_apartment.py. Claims the 4-9 storey blocks: the airport audit's
  // "small but tall" (150) and "mid block 260-800 m2" (95) buckets.
  async _loadAptKit() {
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/apartments.glb', res, undefined, rej));
      gltf.scene.updateMatrixWorld(true);
      const parts = new Map();
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      const kit = [];
      for (const [name, meshes] of parts) {
        if (!/^apt_/.test(name)) continue;
        const geos = [];
        const mats = [];
        for (const o of meshes) {
          const g = o.geometry.clone();
          g.applyMatrix4(o.matrixWorld);
          geos.push(g);
          mats.push(o.material);
        }
        const merged = mergeGeometries(geos, true);
        if (!merged) continue;
        merged.computeBoundingBox();
        const bb = merged.boundingBox;
        const c = bb.getCenter(new THREE.Vector3());
        merged.translate(-c.x, -bb.min.y, -c.z);
        merged.computeBoundingBox();
        merged.computeBoundingSphere();
        for (const m of mats) {
        // NO multiplyScalar here: instanceColor MULTIPLIES the material
        // colour, so dimming the material and then tinting each instance
        // darkened everything twice (0.62 x 0.6 = 0.37) and the district came
        // out mud. The per-instance tint below carries the whole value.
          m.metalness = Math.min(m.metalness ?? 0, 0.3);
        }
        kit.push({ name, geo: merged, mats,
          W: merged.boundingBox.max.x - merged.boundingBox.min.x,
          D: merged.boundingBox.max.z - merged.boundingBox.min.z,
          H: merged.boundingBox.max.y - merged.boundingBox.min.y });
      }
      if (kit.length) {
        this._aptKit = kit;
        console.log(`apartment kit: ${kit.map((k) => `${k.name}(${k.H.toFixed(0)}m)`).join(' ')}`);
      }
    } catch (e) {
      console.warn('apartment kit missing — blocks stay extruded:', e.message);
    }
  }

  _placeApts(items) {
    if (!this._aptKit || !items.length) return;
    const by = new Map();
    for (const it of items) {
      if (!by.has(it.k)) by.set(it.k, []);
      by.get(it.k).push(it);
    }
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const sv = new THREE.Vector3();
    let placed = 0;
    for (const [ki, list] of by) {
      const e = this._aptKit[ki];
      this._placeChunkedInstanced(e.geo, e.mats, list, (m, it) => {
        q.setFromAxisAngle(up, -it.ang);
        sv.set(it.sx, 1, it.sz);   // NEVER scale an apartment vertically:
        m.compose(new THREE.Vector3(it.x, it.y, it.z), q, sv);
      }, 1500, true, 2600, (c, it) => {
        const h = ((it.bi * 2654435761) % 1024) / 1024;
        const v = ((it.bi * 193) % 256) / 256;
        c.setHSL(0.05 + 0.12 * h, 0.05 + 0.12 * v, 0.58 + 0.13 * (1 - v));
      });
      for (const it of list) {
        const geo = new THREE.BoxGeometry(e.W * it.sx * 0.92, e.H, e.D * it.sz * 0.92);
        const mesh = new THREE.Mesh(geo, this._invisibleMat ??= new THREE.MeshBasicMaterial({ visible: false }));
        mesh.position.set(it.x, it.y + e.H / 2, it.z);
        mesh.rotation.y = -it.ang;
        mesh.updateMatrixWorld(true);
        mesh.userData.aptBlock = true;
        this._addCollidable(mesh);
      }
      placed += list.length;
    }
    this._aptPlacements = items;
    console.log(`real apartment blocks: ${placed} footprints`);
  }

  // ---- real modelled industrial sheds --------------------------------------
  // tools/build_shed.py. Claims everything too big to be a house and too
  // short to be a block: the 800-40,000 m2 warehouses and hangars that the
  // airport audit found still wearing a tiled photo.
  async _loadShedKit() {
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/sheds.glb', res, undefined, rej));
      gltf.scene.updateMatrixWorld(true);
      const parts = new Map();
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      const kit = [];
      for (const [name, meshes] of parts) {
        if (!/^(shed_|hangar)/.test(name)) continue;
        const geos = [];
        const mats = [];
        for (const o of meshes) {
          const g = o.geometry.clone();
          g.applyMatrix4(o.matrixWorld);
          geos.push(g);
          mats.push(o.material);
        }
        const merged = mergeGeometries(geos, true);
        if (!merged) continue;
        merged.computeBoundingBox();
        const bb = merged.boundingBox;
        const c = bb.getCenter(new THREE.Vector3());
        merged.translate(-c.x, -bb.min.y, -c.z);
        merged.computeBoundingBox();
        merged.computeBoundingSphere();
        for (const m of mats) {
        // NO multiplyScalar here: instanceColor MULTIPLIES the material
        // colour, so dimming the material and then tinting each instance
        // darkened everything twice (0.62 x 0.6 = 0.37) and the district came
        // out mud. The per-instance tint below carries the whole value.
          m.metalness = Math.min(m.metalness ?? 0, 0.35);
        }
        kit.push({ name, geo: merged, mats,
          W: merged.boundingBox.max.x - merged.boundingBox.min.x,
          D: merged.boundingBox.max.z - merged.boundingBox.min.z,
          H: merged.boundingBox.max.y - merged.boundingBox.min.y });
      }
      if (kit.length) {
        this._shedKit = kit.sort((a, b) => a.W * a.D - b.W * b.D);
        console.log(`shed kit: ${kit.map((k) => k.name).join(' ')}`);
      }
    } catch (e) {
      console.warn('shed kit missing — warehouses stay extruded:', e.message);
    }
  }

  _placeSheds(items) {
    if (!this._shedKit || !items.length) return;
    const by = new Map();
    for (const it of items) {
      if (!by.has(it.k)) by.set(it.k, []);
      by.get(it.k).push(it);
    }
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const sv = new THREE.Vector3();
    let placed = 0;
    for (const [ki, list] of by) {
      const e = this._shedKit[ki];
      this._placeChunkedInstanced(e.geo, e.mats, list, (m, it) => {
        q.setFromAxisAngle(up, -it.ang);
        sv.set(it.sx, it.sy, it.sz);
        m.compose(new THREE.Vector3(it.x, it.y, it.z), q, sv);
      }, 2000, true, 3200, (c, it) => {
        // industrial paint: pale grey-blue, grey-green, dirty cream
        const h = ((it.bi * 2654435761) % 1024) / 1024;
        c.setHSL(0.45 + 0.16 * h, 0.03 + 0.07 * h, 0.60 + 0.1 * (1 - h));
      });
      for (const it of list) {
        const geo = new THREE.BoxGeometry(e.W * it.sx * 0.94, e.H * it.sy, e.D * it.sz * 0.94);
        const mesh = new THREE.Mesh(geo, this._invisibleMat ??= new THREE.MeshBasicMaterial({ visible: false }));
        mesh.position.set(it.x, it.y + e.H * it.sy / 2, it.z);
        mesh.rotation.y = -it.ang;
        mesh.updateMatrixWorld(true);
        mesh.userData.shedBlock = true;
        this._addCollidable(mesh);
      }
      placed += list.length;
    }
    this._shedPlacements = items;
    console.log(`real sheds/hangars: ${placed} footprints`);
  }

  // ---- real modelled suburban houses --------------------------------------
  // tools/build_house.py. These claim the `cottage` class: the 1-2 storey
  // houses that fill the airport district, Orkhevi, Ponichala and every
  // village edge, which otherwise wear a tiled Wikimedia photo and
  // facade-kit boxes for windows and doors.
  async _loadHouseKit() {
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/houses.glb', res, undefined, rej));
      gltf.scene.updateMatrixWorld(true);
      const parts = new Map();
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      const kit = [];
      for (const [name, meshes] of parts) {
        if (!/^house_/.test(name)) continue;
        const geos = [];
        const mats = [];
        for (const o of meshes) {
          const g = o.geometry.clone();
          g.applyMatrix4(o.matrixWorld);
          geos.push(g);
          mats.push(o.material);
        }
        const merged = mergeGeometries(geos, true);
        if (!merged) continue;
        merged.computeBoundingBox();
        const bb = merged.boundingBox;
        const c = bb.getCenter(new THREE.Vector3());
        merged.translate(-c.x, -bb.min.y, -c.z);
        merged.computeBoundingBox();
        merged.computeBoundingSphere();
        for (const m of mats) {
        // NO multiplyScalar here: instanceColor MULTIPLIES the material
        // colour, so dimming the material and then tinting each instance
        // darkened everything twice (0.62 x 0.6 = 0.37) and the district came
        // out mud. The per-instance tint below carries the whole value.
          m.metalness = Math.min(m.metalness ?? 0, 0.3);
        }
        // the roof overhang makes the mesh wider than the house: measure the
        // FOOTPRINT from the plinth, not from the bounding box
        kit.push({ name, geo: merged, mats,
          W: merged.boundingBox.max.x - merged.boundingBox.min.x,
          D: merged.boundingBox.max.z - merged.boundingBox.min.z });
      }
      if (kit.length) {
        this._houseKit = kit.sort((a, b) => a.W * a.D - b.W * b.D);
        console.log(`house kit: ${kit.map((k) => k.name).join(' ')}`);
      }
    } catch (e) {
      console.warn('house kit missing — cottages stay extruded:', e.message);
    }
  }

  _placeHouses(items) {
    if (!this._houseKit || !items.length) return;
    const byKit = new Map();
    for (const it of items) {
      if (!byKit.has(it.k)) byKit.set(it.k, []);
      byKit.get(it.k).push(it);
    }
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const sv = new THREE.Vector3();
    let placed = 0;
    for (const [ki, list] of byKit) {
      const e = this._houseKit[ki];
      this._placeChunkedInstanced(e.geo, e.mats, list, (m, it) => {
        q.setFromAxisAngle(up, -it.ang);
        sv.set(it.sx, (it.sx + it.sz) / 2, it.sz);   // height follows the mean
        m.compose(new THREE.Vector3(it.x, it.y, it.z), q, sv);
      }, 1200, true, 1500, (c, it) => {
        // village render colours: cream, ochre, pale green, washed pink
        const h = ((it.bi * 2654435761) % 1024) / 1024;
        const v = ((it.bi * 97) % 256) / 256;
        c.setHSL(0.03 + 0.22 * h, 0.07 + 0.14 * v, 0.60 + 0.12 * (1 - v));
      });
      for (const it of list) {
        const geo = new THREE.BoxGeometry(e.W * it.sx * 0.86, 7.5, e.D * it.sz * 0.86);
        const mesh = new THREE.Mesh(geo, this._invisibleMat ??= new THREE.MeshBasicMaterial({ visible: false }));
        mesh.position.set(it.x, it.y + 3.75, it.z);
        mesh.rotation.y = -it.ang;
        mesh.updateMatrixWorld(true);
        mesh.userData.houseBlock = true;
        this._addCollidable(mesh);
      }
      placed += list.length;
    }
    this._housePlacements = items;
    console.log(`real houses: ${placed} footprints across ${byKit.size} models`);
  }

  // ---- real modelled panel blocks -----------------------------------------
  // tools/build_panel_block.py exports block_2 .. block_6: the same
  // khrushchevka at 2-6 sections, so a real OSM footprint gets a real
  // building of roughly its real length instead of a stretched box.
  // Must be loaded BEFORE _buildBuildings runs, because the building loop
  // checks this._panelKit to decide whether to skip its extrusion.
  async _loadPanelKit() {
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/panel_block.glb', res, undefined, rej));
      gltf.scene.updateMatrixWorld(true);
      // glTF splits a multi-material mesh into one primitive per material, so
      // each block arrives as several meshes. Merge them back with groups or
      // an instance renders as a single flat-coloured slab.
      const parts = new Map();
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      const kit = new Map();
      for (const [name, meshes] of parts) {
        const mm = /^block_(\d)/.exec(name);
        if (!mm) continue;
        const geos = [];
        const mats = [];
        for (const o of meshes) {
          const g = o.geometry.clone();
          // the variants are laid out side by side in the glb; bake the
          // object transform in and re-centre on the block itself
          g.applyMatrix4(o.matrixWorld);
          geos.push(g);
          mats.push(o.material);
        }
        const merged = mergeGeometries(geos, true);
        if (!merged) continue;
        merged.computeBoundingBox();
        const c = merged.boundingBox.getCenter(new THREE.Vector3());
        // centre in X/Z, keep the ground at y=0
        merged.translate(-c.x, -merged.boundingBox.min.y, -c.z);
        merged.computeBoundingSphere();
        for (const m of mats) {
          m.metalness = Math.min(m.metalness ?? 0, 0.25);
          // The palette is authored for a neutral Blender render. This scene
          // runs a 2.7 sun + a 2.0 hemisphere + an HDR environment through
          // ACES, which blew the light concrete out to white plastic -- the
          // blocks read brighter than the sky. Bring the albedo down to where
          // the rest of the city sits.
          // (see the note in _loadHouseKit: no double-dim with instanceColor)
          m.vertexColors = false;
        }
        kit.set(+mm[1], { geo: merged, mats });
      }
      if (kit.size) {
        this._panelKit = kit;
        console.log(`panel block kit: ${[...kit.keys()].sort().map((k) => `block_${k}`).join(' ')}`);
      }
    } catch (e) {
      console.warn('panel block kit missing — Soviet blocks stay extruded:', e.message);
    }
  }

  // Place the claimed footprints, chunked and distance-culled like every
  // other layer. One InstancedMesh per (variant, chunk).
  _placePanelBlocks(items) {
    if (!this._panelKit || !items.length) return;
    const byVariant = new Map();
    for (const it of items) {
      if (!byVariant.has(it.sections)) byVariant.set(it.sections, []);
      byVariant.get(it.sections).push(it);
    }
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const one = new THREE.Vector3(1, 1, 1);
    let placed = 0;
    for (const [sections, list] of byVariant) {
      const entry = this._panelKit.get(sections);
      if (!entry) continue;
      this._placeChunkedInstanced(entry.geo, entry.mats, list, (m, it) => {
        // the model's +x runs along the street frontage; the footprint's long
        // edge angle is measured in XZ, where +angle turns the other way
        q.setFromAxisAngle(up, -it.ang);
        m.compose(new THREE.Vector3(it.x, it.y, it.z), q, one);
      }, 1500, true, 2600, (c, it) => {
        // Per-instance tint. These blocks ARE repetitive in life, but 539
        // identical copies read as a copy-paste rather than a district --
        // instance colour is the cheapest way studios break that up, and it
        // costs one float3 per building. Real Tbilisi panels were repainted
        // in whatever was going: ochre, pale green, pink, dirty cream.
        const hv = ((it.bi * 2654435761) % 1024) / 1024;
        const warm = ((it.bi * 40503) % 512) / 512;
        c.setHSL(0.07 + 0.10 * hv, 0.05 + 0.12 * warm, 0.56 + 0.14 * (1 - warm));
      });
      placed += list.length;
      // Solid to fly into: one box collider per block. The extrusion that
      // used to provide collision here was skipped, and a district you can
      // fly straight through is worse than an ugly one.
      for (const it of list) {
        this._addPanelCollider(it, sections);
      }
    }
    this._panelPlacements = items;   // kept for the probes / dev mode
    console.log(`real panel blocks: ${placed} footprints (${[...byVariant.keys()].sort().join('/')} sections)`);
  }

  _addPanelCollider(it, sections) {
    const L = sections * 12.0;
    const D = 11.4;
    const H = 15.9;
    const geo = new THREE.BoxGeometry(L, H, D);
    const mesh = new THREE.Mesh(geo, this._invisibleMat ??= new THREE.MeshBasicMaterial({ visible: false }));
    mesh.position.set(it.x, it.y + H / 2, it.z);
    mesh.rotation.y = -it.ang;
    mesh.updateMatrixWorld(true);   // never parented, so this must be explicit
    mesh.userData.panelBlock = true;
    this._addCollidable(mesh);
  }

  // Instanced 3D facade relief: concrete balcony slabs + rails, AC units.
  // Chunked and distance-culled like every other prop layer.
  async _buildFacadeProps(balconies, acs, windows = [], cornices = [],
    drains = [], entrances = [], grilles = [], dishes = [], parapets = [], roofProps = [],
    glassMullions = [], glassBands = []) {
    // Blender-modeled detail kit (tools/build_facade_kit.py). Fallback boxes
    // keep the old look if the glb is missing.
    this._tagFacadeProps = true; // register these layers for collapse-stripping
    const kit = {};
    try {
      const gltf = await new Promise((res, rej) =>
        newGltfLoader().load('models/facade_kit.glb', res, undefined, rej));
      // multi-material Blender objects arrive as one mesh PER material — merge
      // them back per base name or instances render as a single white slab
      const parts = new Map();
      gltf.scene.updateMatrixWorld(true);
      gltf.scene.traverse((o) => {
        if (!o.isMesh) return;
        const base = o.name.replace(/[._]\d+$/, '');
        if (!parts.has(base)) parts.set(base, []);
        parts.get(base).push(o);
      });
      for (const [base, meshes] of parts) {
        if (meshes.length === 1) {
          kit[base] = meshes[0];
        } else {
          const geos = meshes.map((o) => {
            const g = o.geometry.clone().applyMatrix4(o.matrixWorld);
            delete g.attributes.uv; // cubes need no UVs; merging requires identical sets
            return g;
          });
          const merged = mergeGeometries(geos, true);
          kit[base] = new THREE.Mesh(merged, meshes.map((o) => o.material));
        }
      }
    } catch (e) { console.warn('facade kit load failed, using fallback boxes:', e); }
    console.log('facade kit objects:', Object.keys(kit).map((k) =>
      `${k}(${kit[k].geometry.index ? kit[k].geometry.index.count / 3 : '?'}tri,${Array.isArray(kit[k].material) ? kit[k].material.length : 1}mat)`).join(' '));
    const geoOf = (name, fallback) => (kit[name] ? kit[name].geometry : fallback());
    const matOf = (name, color) => {
      if (kit[name]) {
        const m = kit[name].material;
        (Array.isArray(m) ? m : [m]).forEach((mm) => { mm.metalness = 0; mm.roughness = 0.9; });
        return m;
      }
      return new THREE.MeshLambertMaterial({ color });
    };
    // `lodable`: wall-mounted layers swap to a baked front-quad imposter past
    // ~250 m of TRUE 3D distance — the color read (dark glass, light frame)
    // survives, the 100-500-tri kit geometry doesn't have to. Only worth it
    // when the kit geometry is actually heavy; cheap box fallbacks stay as-is.
    const put = (geo, mat, items, maxDist, chunkM = 1000, lodable = false, kind = 'prop') => {
      this._propKind = kind;
      if (!items.length) return;
      const tris = geo.index ? geo.index.count / 3 : (geo.attributes.position?.count ?? 0) / 3;
      const lod = lodable && tris > 60
        ? { dist: 250 + chunkM * 0.72, ...this._facadeImposter(geo, mat) }
        : null;
      this._placeChunkedInstanced(geo, mat, items, (m, it) => {
        m.makeRotationY(it.ang);
        if (it.sx || it.sy) m.scale(new THREE.Vector3(it.sx || 1, it.sy || 1, 1));
        m.setPosition(it.x, it.y, it.z);
      }, chunkM, false, maxDist, null, null, lod?.geo ? lod : null);
    };
    // items carry a per-building `variant` kit name; bucket them so each
    // variant becomes its own instanced layer (missing variants fall back
    // to the base window/balcony geometry)
    const byVariant = (items, def) => {
      const m = new Map();
      for (const it of items) {
        const v = it.variant && kit[it.variant] ? it.variant : def;
        if (!m.has(v)) m.set(v, []);
        m.get(v).push(it);
      }
      return m;
    };

    const balcFallback = () => {
      const slab = new THREE.BoxGeometry(2.7, 0.14, 1.15);
      slab.translate(0, 0, 0.58);
      const rail = new THREE.BoxGeometry(2.7, 0.85, 0.07);
      rail.translate(0, 0.49, 1.12);
      return mergeGeometries([slab, rail]);
    };
    for (const [v, list] of byVariant(balconies, 'balcony'))
      // was 1100: mid-distance flats turned to bare slabs — the long reach is
      // affordable now because far chunks draw the baked imposter quad
      put(geoOf(v, balcFallback), matOf(v, 0xc5bfb2), list, 1400, 500, true);
    put(geoOf('ac', () => {
      const g = new THREE.BoxGeometry(0.55, 0.5, 0.42);
      g.translate(0, 0, 0.26);
      return g;
    }), matOf('ac', 0xdcdcd6), acs, 650, 500, true);
    // windows are the heavy layer — small chunks so only the streets around
    // the plane pay for them. 900 m cull (was 520): at street level the
    // residential blocks 400-800 m down the avenue must still show frames,
    // not bare slabs — chunked frustum culling keeps the draw count sane.
    for (const [v, list] of byVariant(windows, 'window'))
      put(geoOf(v, () => new THREE.BoxGeometry(1.5, 1.6, 0.1)),
        matOf(v, 0xe8e4dc), list, v === 'win_shop' ? 700 : 900, 500, true, 'glass');
    put(geoOf('cornice', () => {
      const g = new THREE.BoxGeometry(3, 0.2, 0.28);
      g.translate(0, 0, 0.14);
      return g;
    }), matOf('cornice', 0xd8d2c2), cornices, 900);
    put(geoOf('drainpipe', () => {
      const g = new THREE.BoxGeometry(0.1, 3, 0.1);
      g.translate(0, 1.5, 0.15);
      return g;
    }), matOf('drainpipe', 0x9aa0a3), drains, 500);
    for (const [v, list] of byVariant(entrances, 'entrance'))
      put(geoOf(v, () => {
        const g = new THREE.BoxGeometry(1.4, 2.2, 0.12);
        g.translate(0, 1.1, 0.06);
        return g;
      }), matOf(v, 0x3a3a3c), list, 500, 500, true);
    put(geoOf('grille', () => {
      const g = new THREE.BoxGeometry(1.2, 1.4, 0.05);
      g.translate(0, 0, 0.16);
      return g;
    }), matOf('grille', 0x2a2a2a), grilles, 400, 500, true);
    put(geoOf('dish', () => {
      const g = new THREE.BoxGeometry(0.9, 0.9, 0.06);
      g.translate(0, 0, 0.4);
      return g;
    }), matOf('dish', 0xb9bdc0), dishes, 500, 500, true);
    // roof layers: the whole point is reading from the AIR — long 1500 m cull
    put(geoOf('parapet', () => {
      const g = new THREE.BoxGeometry(3, 0.55, 0.22);
      g.translate(0, 0.28, 0);
      return g;
    }), matOf('parapet', 0xaba599), parapets, 1500);
    for (const [v, list] of byVariant(roofProps, 'roof_vent')) {
      put(geoOf(v, () => {
        const g = new THREE.BoxGeometry(1.2, 1.2, 1.2);
        g.translate(0, 0.6, 0);
        return g;
      }), matOf(v, 0x9d978c), list, 1500);
    }
    // glass-tower relief: dark aluminum, LONGEST cull — towers anchor the
    // skyline and must keep their floor grid even from across the valley
    const aluMat = new THREE.MeshLambertMaterial({ color: 0x2e3338 });
    put((() => {
      const g = new THREE.BoxGeometry(0.35, 3, 0.35); // sy stretches to full height
      g.translate(0, 1.5, 0);
      return g;
    })(), aluMat, glassMullions, 2000);
    put((() => {
      const g = new THREE.BoxGeometry(3, 0.45, 0.3); // sx stretches along the wall
      g.translate(0, 0, 0.15);
      return g;
    })(), aluMat, glassBands, 2000);
    const winCounts = [...byVariant(windows, 'window')].map(([v, l]) => `${v}:${l.length}`).join(' ');
    const balcCounts = [...byVariant(balconies, 'balcony')].map(([v, l]) => `${v}:${l.length}`).join(' ');
    console.log(`facade props: ${balconies.length} balconies (${balcCounts}), ${acs.length} AC, ` +
      `${windows.length} windows (${winCounts}), ${cornices.length} cornice segs, ` +
      `${drains.length} drainpipes, ${entrances.length} entrances, ${grilles.length} grilles, ` +
      `${dishes.length} dishes, ${parapets.length} parapets, ${roofProps.length} roof props, ` +
      `${glassMullions.length} glass mullions, ${glassBands.length} glass bands`);
    this._tagFacadeProps = false;
    this._propKind = 'prop';
  }

  // Swap the drawn canvas facades for photos of real Tbilisi buildings
  // (tools/fetch_facades.py → public/textures/facades/). Walls have UVs of
  // u = one 3 m window bay, v = one 3 m floor, so each texture declares how
  // many bays/floors one repeat spans and texture.repeat maps it to meters.
  // If the fetch fails the canvas facades simply stay on.
  async _loadRealFacades(wallMats, loggiaVariants, oldTbilisiMatB, roofFlatMat, roofMetalMat) {
    try {
      const res = await fetch('textures/facades/facades.json');
      if (!res.ok) return;
      const man = await res.json();
      const loader = new THREE.TextureLoader();
      const apply = (mat, kind, aniso = 8) => {
        const m = man[kind];
        if (!m || !mat) return;
        loader.load(`textures/facades/${m.file}`, (t) => {
          t.wrapS = t.wrapT = THREE.RepeatWrapping;
          t.colorSpace = THREE.SRGBColorSpace;
          t.anisotropy = aniso;
          t.repeat.set(1 / m.bays, 1 / m.floors);
          mat.map = t;
          mat.needsUpdate = true;
        });
      };
      apply(wallMats[0], 'plaster');
      // 16× anisotropy on panel/loggia/glass: these walls are seen at grazing
      // angles down long avenues (and towers from km away) — at 8× the window
      // grid smears into a flat slab long before the cull
      apply(wallMats[1], 'panel', 16);
      apply(wallMats[2], 'glass', 16);
      apply(wallMats[3], 'oldtbilisi');
      apply(loggiaVariants[0], 'panel2', 16);
      apply(loggiaVariants[1], 'tower16', 16);
      apply(loggiaVariants[2], 'khrush5', 16);
      apply(oldTbilisiMatB, 'oldtbilisi2');
      // roofs.json (build_roof_materials.py) owns every roof bucket now,
      // photo tiles included — applying them here too would race that loader
      if (!this._roofMan) {
        apply(roofFlatMat, 'roof_flat');
        apply(roofMetalMat, 'roof_metal');
      }
      this.attribution += ' · facades: Wikimedia Commons (S. Arunoprayote CC BY 4.0, V. Argenberg CC BY 4.0, Kober CC BY-SA 4.0, Yuriy75 CC BY-SA 4.0, M. Benidze CC BY-SA 2.0, D. Delso CC BY-SA 4.0, A. Milligan CC BY 2.0, Andriabenia CC0), ambientCG CC0 · statue scans: Gorgasali & Saakadze monuments by Badri Gogia CC BY 4.0 (sketchfab.com/BadriGogia), threedscans.com (no restrictions), noe-3d.at CC0 · airport warehouse by Nicholas-3D CC BY 4.0 (sketchfab.com/Nicholas01) · wooden table by Prateek.Sharma CC BY 4.0 (sketchfab.com/Prateek.Sharma) · Rustaveli metro photo: Wikimedia Commons CC BY-SA 2.0';
    } catch {
      /* offline — procedural facades remain */
    }
  }

  // Real windowless wall materials (photo-cut brick/plaster/panel tiles,
  // tools/build_wall_materials.py) for the kit-window buckets 4..9. Wall UV
  // convention: 1 u-unit = 3 m of perimeter, 1 v-unit = 3 m of height, so a
  // tile that covers scale_m meters repeats 3/scale_m times per unit (v uses
  // the image aspect so the tile stays square in world meters).
  // Procedural roof tiles (corrugated / standing-seam / patched metal,
  // bitumen felt, concrete screed). Colour still comes from the per-building
  // vertex tint, so these are near-greyscale and only carry the pattern.
  async _loadRoofMaterials(roofMats) {
    const man = this._roofMan;
    if (!man) return; // no roofs.json — _loadRealFacades keeps the photo pair
    const loader = new THREE.TextureLoader();
    this._roofNames.forEach((name, i) => {
      const m = man[name];
      const mat = roofMats[i];
      if (!m || !mat) return;
      loader.load(`textures/roofs/${m.file}`, (t) => {
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
        t.colorSpace = THREE.SRGBColorSpace;
        t.anisotropy = 8;
        // roof UVs are planar world coords at 1 unit per 3 m
        const r = 3 / m.scale_m;
        t.repeat.set(r, r);
        mat.map = t;
        mat.needsUpdate = true;
      });
    });
  }

  // Curated Soviet-block facades from the per-building photo set, promoted to
  // shared buckets so style-1 housing varies BUILDING BY BUILDING instead of
  // per square kilometre. They reuse the pb textures already on disk (already
  // rectified, seamless and credited) — nothing new is downloaded.
  async _loadLoggiaFacades(wallMats) {
    const keys = this._loggiaKeys;
    if (!keys?.length) return;
    try {
      const res = await fetch('textures/pb/manifest.json');
      if (!res.ok) return;
      const man = await res.json();
      const loader = new THREE.TextureLoader();
      keys.forEach((key, i) => {
        const m = man[key];
        const mat = wallMats[this._loggiaBase + i];
        if (!m || !mat) return;
        loader.load(`textures/pb/${m.file}`, (t) => {
          t.wrapS = t.wrapT = THREE.RepeatWrapping;
          t.colorSpace = THREE.SRGBColorSpace;
          t.anisotropy = 16; // seen at grazing angles down long avenues
          t.repeat.set(1 / (m.bays || 3), 1 / (m.floors || 3));
          mat.map = t;
          mat.needsUpdate = true;
        });
      });
      console.log(`loggia facades: ${keys.length} real Soviet-block textures, per building`);
    } catch { /* offline */ }
  }

  async _loadWallMaterials(wallMats) {
    // Tile order comes from walls.json itself (see CityMap.wallTileNames,
    // fetched before the materials are built) so new tiles need no code change.
    const NAMES = this._wallNames ?? ['brick_red', 'brick_dark', 'plaster_decay',
      'plaster_yellow', 'plaster_pink', 'panel_concrete'];
    try {
      const res = await fetch('textures/walls/walls.json');
      if (!res.ok) return;
      const man = await res.json();
      const loader = new THREE.TextureLoader();
      NAMES.forEach((name, i) => {
        const m = man[name];
        const mat = wallMats[4 + i];
        if (!m || !mat) return;
        loader.load(`textures/walls/${m.file}`, (t) => {
          t.wrapS = t.wrapT = THREE.RepeatWrapping;
          t.colorSpace = THREE.SRGBColorSpace;
          t.anisotropy = 8;
          const aspect = t.image && t.image.width ? t.image.height / t.image.width : 1;
          t.repeat.set(3 / m.scale_m, 3 / (m.scale_m * aspect));
          mat.map = t;
          mat.needsUpdate = true;
        });
      });
      this.attribution += ' · wall materials: Wikimedia Commons photos (Giftzwerg 88, T. Monto, Erekle K, A. Jones, D. Osipov, S. Arunoprayote — textures/walls/CREDITS.txt)';
    } catch {
      /* offline — the plain stucco canvas stays */
    }
  }

  // Real Kenney apartment/office models, each stretched to a footprint's oriented
  // bounding box. Chunked + distance-culled like the houses.
  async _buildApartments(apts) {
    if (!apts.length) return;
    const loader = newGltfLoader();
    // same muted facade tones the map had before the city pack
    const aptWalls = [
      0xb5a58e, 0x8a7a64, 0x9a8f72, 0xa8a29a, 0x9c6b52, 0xbfb6a4, 0x8f8474,
      0xa89a86, 0x93887a, 0xb0a48c, 0x86796a, 0x9d9486, 0xa79b80, 0x8d8172,
    ];
    const models = await Promise.all(
      Array.from({ length: APT_STYLES }, (_, i) =>
        new Promise((res) =>
          loader.load(
            `models/citypack/apt-${i}.glb`,
            (g) => res(retintModel(g.scene, aptWalls[i % aptWalls.length], 0x6f6a62)),
            undefined,
            () =>
              loader.load(`models/apartments/apt-${i}.glb`, (g) => res(extractTextured(g.scene)), undefined, () => res(null))
          )
        )
      )
    );
    for (let s = 0; s < models.length; s++) {
      const model = models[s];
      if (!model) continue;
      model.geometry.computeBoundingBox();
      const bb = model.geometry.boundingBox;
      const mw = bb.max.x - bb.min.x || 1;
      const md = bb.max.z - bb.min.z || 1;
      const mh = bb.max.y - bb.min.y || 1;
      const list = apts.filter((a) => a.style === s);
      if (!list.length) continue;
      const q = new THREE.Quaternion();
      const e = new THREE.Euler();
      const sv = new THREE.Vector3();
      this._placeChunkedInstanced(model.geometry, model.material, list, (m, a) => {
        e.set(0, a.angle, 0);
        q.setFromEuler(e);
        sv.set(a.width / mw, a.height / mh, a.depth / md);
        m.compose(new THREE.Vector3(a.x, this.heightAt(a.x, a.z) - 0.5, a.z), q, sv);
      });
    }
  }

  // Place real Kenney house models on the collected small footprints, 8 styles
  // (each with its own roof color), scaled to the footprint and turned to it.
  async _buildHouses(houses) {
    if (!houses.length) return;
    // 21 Kenney house models, each with its own roof colour (red/terracotta/
    // grey/brown/green/slate variants) for street-to-street variety.
    const roofPalette = [
      0x9c4b34, 0xb0603a, 0x6f6a60, 0x7a5a3f, 0x4f6d4a, 0x556070, 0x8a4636, 0x5f564c,
      0xa85a3e, 0x7d5236, 0x8a8276, 0x5a6b52, 0x6b4a30, 0x97533a, 0x4a5560, 0x746b5e,
      0xb06844, 0x635a4e, 0x8f4e38, 0x50604f, 0x7a6a58,
    ];
    const roofs = roofPalette.slice(0, HOUSE_STYLES);
    const loader = newGltfLoader();
    const models = await Promise.all(
      roofs.map((hex, i) =>
        new Promise((res) =>
          loader.load(
            `models/citypack/house-${i}.glb`,
            (g) => res(retintModel(g.scene, 0xdccfb6, hex)),
            undefined,
            () =>
              loader.load(`models/houses/house-${i}.glb`, (g) => res(extractHouse(g.scene, hex)), undefined, () => res(null))
          )
        )
      )
    );
    const q = new THREE.Quaternion();
    const e = new THREE.Euler();
    const sv = new THREE.Vector3();
    // Per-house painted-plaster variation: instance-colour multipliers kept
    // close to white so the shared roof/trim vertex colours only shift subtly
    // while walls pick up distinct warm plaster tones house to house.
    const tints = [
      [1.0, 1.0, 1.0], // untinted cream
      [1.04, 0.99, 0.88], // warm sand
      [1.0, 0.92, 0.78], // tan plaster
      [0.93, 0.9, 0.82], // grey-beige
      [1.05, 0.95, 0.8], // ochre
      [0.9, 0.84, 0.74], // weathered khaki
      [1.03, 0.94, 0.9], // pink plaster
      [0.86, 0.84, 0.79], // faded grey
    ];
    for (let s = 0; s < models.length; s++) {
      const model = models[s];
      if (!model) continue;
      model.geometry.computeBoundingBox();
      const bb = model.geometry.boundingBox;
      const mw = Math.max(bb.max.x - bb.min.x, bb.max.z - bb.min.z) || 1;
      const list = houses.filter((hh) => hh.style === s);
      if (!list.length) continue;
      this._placeChunkedInstanced(model.geometry, model.material, list, (m, hh) => {
        const sc = Math.max(0.6, hh.size / mw) * 1.15;
        hh.s = sc; // damage records need the final scale for height/radius
        e.set(0, hh.angle, 0);
        q.setFromEuler(e);
        sv.set(sc, sc, sc);
        m.compose(new THREE.Vector3(hh.x, this.heightAt(hh.x, hh.z) - 0.3, hh.z), q, sv);
      }, 2000, false, 0, (c, hh) => {
        const t = tints[Math.abs(Math.round(hh.x * 7.3 + hh.z * 3.1)) % tints.length];
        c.setRGB(t[0], t[1], t[2]);
      }, {
        kind: 'house',
        label: 'სახლი',
        hp: (hh) => 300 + hh.size * 9, // a suburban house: two rockets
      });
    }
  }

  // Snap the spawn onto the nearest avenue so the takeoff roll starts on the
  // actual street, not in the courtyard next to it.
  _computeSpawn(roads, near = { x: 0, z: 0 }) {
    const defaultForward = { x: Math.sin(1.24), z: -Math.cos(1.24) }; // legacy yaw -1.24
    const search = (minWidth) => {
      let best = null;
      for (const road of roads) {
        if (road[0] < minWidth) continue;
        const n = (road.length - 1) / 2;
        for (let i = 0; i < n - 1; i++) {
          const x1 = road[1 + i * 2] / 10;
          const z1 = road[2 + i * 2] / 10;
          const x2 = road[3 + i * 2] / 10;
          const z2 = road[4 + i * 2] / 10;
          const dx = x2 - x1;
          const dz = z2 - z1;
          const len2 = dx * dx + dz * dz;
          if (len2 < 1) continue;
          const t = THREE.MathUtils.clamp(
            -((x1 - near.x) * dx + (z1 - near.z) * dz) / len2,
            0,
            1
          );
          const px = x1 + dx * t;
          const pz = z1 + dz * t;
          const d2 = (px - near.x) ** 2 + (pz - near.z) ** 2;
          if (!best || d2 < best.d2) best = { d2, px, pz, dx, dz };
        }
        if (best && best.d2 < 4) break; // already on a road
      }
      return best;
    };
    let best = search(8); // avenues first…
    if (!best || best.d2 > 3000 * 3000) best = search(0) ?? best; // …any street in small towns
    if (!best) {
      this.spawn = null;
      return;
    }
    const len = Math.hypot(best.dx, best.dz);
    let fx = best.dx / len;
    let fz = best.dz / len;
    // Point the plane down whichever direction of the road is closest to the
    // legacy heading (WSW along Vazha-Pshavela).
    if (fx * defaultForward.x + fz * defaultForward.z < 0) {
      fx = -fx;
      fz = -fz;
    }
    this.spawn = { x: best.px, z: best.pz, yaw: Math.atan2(-fx, -fz) };
  }

  // ---- roads ---------------------------------------------------------------

  // A city-wide ribbon mesh (roads, sidewalks…) drawn as ONE object costs its
  // full ~4M triangles every frame from everywhere. Split it into km-sized
  // tiles registered as regular cullable chunks: frustum + distance culling
  // then work exactly like they do for buildings. Normals are computed on the
  // whole mesh FIRST so tile borders don't show lighting seams.
  // `uv` may be null for a flat-coloured surface (road markings): 5.2 M
  // vertices of texture coordinates nobody samples is 41 MB of nothing.
  _addChunkedSurface(pos, uv, idx, mat, { tile = 1500, collidable = false, ground = false } = {}) {
    const whole = new THREE.BufferGeometry();
    whole.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    if (uv) whole.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
    whole.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    whole.computeVertexNormals();
    const P = whole.getAttribute('position').array;
    const U = uv ? whole.getAttribute('uv').array : null;
    const N = whole.getAttribute('normal').array;
    const I = whole.getIndex().array;
    const tiles = new Map();
    for (let t = 0; t < I.length; t += 3) {
      const a = I[t] * 3;
      const key = `${Math.floor(P[a] / tile)}_${Math.floor(P[a + 2] / tile)}`;
      let b = tiles.get(key);
      if (!b) tiles.set(key, (b = []));
      b.push(I[t], I[t + 1], I[t + 2]);
    }
    for (const [key, tri] of tiles) {
      const remap = new Map();
      const p2 = [];
      const u2 = [];
      const n2 = [];
      const i2 = [];
      for (const vi of tri) {
        let m = remap.get(vi);
        if (m === undefined) {
          m = remap.size;
          remap.set(vi, m);
          p2.push(P[vi * 3], P[vi * 3 + 1], P[vi * 3 + 2]);
          if (U) u2.push(U[vi * 2], U[vi * 2 + 1]);
          n2.push(N[vi * 3], N[vi * 3 + 1], N[vi * 3 + 2]);
        }
        i2.push(m);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(p2), 3));
      if (U) g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(u2), 2));
      g.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(n2), 3));
      g.setIndex(new THREE.BufferAttribute(new Uint32Array(i2), 1));
      const mesh = new THREE.Mesh(g, mat);
      mesh.receiveShadow = true;
      mesh.matrixAutoUpdate = false;
      mesh.userData.isTerrain = ground;
      const [kx, kz] = key.split('_').map(Number);
      this.chunkMeshes.push({ mesh, cx: kx * tile + tile / 2, cz: kz * tile + tile / 2, maxDist: 0, lodD2: 0, cy: 0, lodMesh: null, hiOn: false, loOn: false });
      if (collidable) {
        g.computeBoundsTree();
        this.collidables.push(mesh);
      }
      // update() attaches in-range tiles to the scene
    }
    whole.dispose();
  }

  _buildRoads(roads) {
    const pos = [];
    const uv = [];
    const idx = [];
    // real sidewalks (trotuars) flanking the roadway, slightly below the
    // asphalt so crossing roads hide them at intersections
    const sPos = [];
    const sUv = [];
    const sIdx = [];
    const SW_MAX = 3; // trotuar on an avenue, m (a side street gets 1.8)
    // Paved concrete verge outside the trotuar. It used to run 5 m wide along
    // EVERY road: with the real network that is a 16 m concrete apron down
    // both sides of every residential street — 280 MB of it, and the pale
    // bands that made the city read like a car park. Avenues only now.
    const vPos = [];
    const vUv = [];
    const vIdx = [];
    const VW = 3; // verge width, m
    // painted markings — real lines, driven by the real lane count, instead of
    // one dashed stripe baked into the asphalt texture and stretched across
    // whatever width the road happened to be
    const mPos = [];
    const mIdx = [];
    const a = new THREE.Vector2();
    const TEX_M = 9; // asphalt texture covers ~9 m
    for (let ri = 0; ri < roads.length; ri++) {
      const road = roads[ri];
      const w = road[0] / 2;
      // Alleys, driveways and parking aisles (service roads, ~3.4 m) get no
      // trotuar and no concrete verge — flanking 26,000 of them would ring
      // every courtyard in the city with pavement.
      const flanked = road[0] >= 5.5;
      const SW = road[0] >= 9 ? SW_MAX : 1.8;
      const verged = road[0] >= 12; // avenues and trunk roads only
      const rm = this.roadMeta?.get(ri);
      const lanes = rm?.lanes || Math.max(1, Math.round((road[0] - 1) / 3.3));
      const oneway = rm?.oneway ?? false;
      // Paint only what really carries markings: tertiary and up inside the
      // city, trunk/motorway out in the country. Marking every residential
      // lane and courtyard drive as well cost 10.1 M triangles — and Tbilisi
      // side streets are bare tarmac anyway.
      const marked = road[0] >= 7.5 && (rm ? true : road[0] >= 16);
      const n = (road.length - 1) / 2;
      let dist = 0;
      for (let i = 0; i < n - 1; i++) {
        const x1 = road[1 + i * 2] / 10;
        const z1 = road[2 + i * 2] / 10;
        const x2 = road[3 + i * 2] / 10;
        const z2 = road[4 + i * 2] / 10;
        a.set(x2 - x1, z2 - z1);
        const len = a.length();
        if (len < 0.01) continue;
        a.divideScalar(len);
        const px = -a.y * w;
        const pz = a.x * w;
        // subdivide long segments so the ribbon follows the terrain
        const steps = Math.max(1, Math.ceil(len / 14));
        for (let s = 0; s < steps; s++) {
          const t0 = s / steps;
          const t1 = (s + 1) / steps;
          const ax = x1 + (x2 - x1) * t0;
          const az = z1 + (z2 - z1) * t0;
          const bx = x1 + (x2 - x1) * t1;
          const bz = z1 + (z2 - z1) * t1;
          const segLen = len / steps;
          const v0 = pos.length / 3;
          pos.push(
            ax - px, this.heightAt(ax - px, az - pz) + 0.8, az - pz,
            ax + px, this.heightAt(ax + px, az + pz) + 0.8, az + pz
          );
          pos.push(
            bx - px, this.heightAt(bx - px, bz - pz) + 0.8, bz - pz,
            bx + px, this.heightAt(bx + px, bz + pz) + 0.8, bz + pz
          );
          // METRIC uv across the width too: with u fixed at 0..1 the asphalt
          // grain (and the lines that used to be painted into it) stretched to
          // whatever the road was wide, so a 21 m avenue wore 21 m dashes.
          const uW = (2 * w) / TEX_M;
          uv.push(0, dist / TEX_M, uW, dist / TEX_M);
          uv.push(0, (dist + segLen) / TEX_M, uW, (dist + segLen) / TEX_M);
          idx.push(v0, v0 + 1, v0 + 2, v0 + 1, v0 + 3, v0 + 2);
          // sidewalk ribbons just outside each curb
          if (flanked) for (const sgn of [-1, 1]) {
            const ox = (px / w) * SW * sgn;
            const oz = (pz / w) * SW * sgn;
            const ix = px * sgn;
            const iz = pz * sgn;
            const sv = sPos.length / 3;
            sPos.push(
              ax + ix, this.heightAt(ax + ix, az + iz) + 0.75, az + iz,
              ax + ix + ox, this.heightAt(ax + ix + ox, az + iz + oz) + 0.75, az + iz + oz,
              bx + ix, this.heightAt(bx + ix, bz + iz) + 0.75, bz + iz,
              bx + ix + ox, this.heightAt(bx + ix + ox, bz + iz + oz) + 0.75, bz + iz + oz
            );
            sUv.push(0, dist / TEX_M, SW / TEX_M, dist / TEX_M, 0, (dist + segLen) / TEX_M, SW / TEX_M, (dist + segLen) / TEX_M);
            sIdx.push(sv, sv + 1, sv + 2, sv + 1, sv + 3, sv + 2);
            // verge quad, from the sidewalk's outer edge outwards
            const wx = ix + ox; // sidewalk outer edge offset
            const wz = iz + oz;
            const vx = (px / w) * VW * sgn;
            const vz = (pz / w) * VW * sgn;
            if (!verged) continue;
            const vv = vPos.length / 3;
            vPos.push(
              ax + wx, this.heightAt(ax + wx, az + wz) + 0.7, az + wz,
              ax + wx + vx, this.heightAt(ax + wx + vx, az + wz + vz) + 0.7, az + wz + vz,
              bx + wx, this.heightAt(bx + wx, bz + wz) + 0.7, bz + wz,
              bx + wx + vx, this.heightAt(bx + wx + vx, bz + wz + vz) + 0.7, bz + wz + vz
            );
            vUv.push(0, dist / TEX_M, VW / TEX_M, dist / TEX_M, 0, (dist + segLen) / TEX_M, VW / TEX_M, (dist + segLen) / TEX_M);
            vIdx.push(vv, vv + 1, vv + 2, vv + 1, vv + 3, vv + 2);
          }
          // ---- painted markings, laid as real geometry -------------------
          // Offsets across the carriageway, in metres from the centre:
          // a solid line inside each kerb, then a divider on every lane
          // boundary — dashed between lanes running the same way, and a
          // double solid line down the middle of a two-way road.
          if (marked) {
            const ux = px / w; // unit normal across the road
            const uz = pz / w;
            const stripe = (off, halfW, dashed) => {
              if (dashed) {
                // 3 m of paint every 9 m, positioned along the whole road so
                // the dashes stay in step across segment joins
                const D = 9;
                let d0 = Math.ceil(dist / D) * D;
                for (; d0 + 3 <= dist + segLen; d0 += D) {
                  const f0 = (d0 - dist) / segLen;
                  const f1 = (d0 + 3 - dist) / segLen;
                  markQuad(f0, f1, off, halfW);
                }
                return;
              }
              markQuad(0, 1, off, halfW);
            };
            const markQuad = (f0, f1, off, halfW) => {
              const mx0 = ax + (bx - ax) * f0 + ux * off;
              const mz0 = az + (bz - az) * f0 + uz * off;
              const mx1 = ax + (bx - ax) * f1 + ux * off;
              const mz1 = az + (bz - az) * f1 + uz * off;
              const ox = ux * halfW;
              const oz = uz * halfW;
              const v = mPos.length / 3;
              mPos.push(
                mx0 - ox, this.heightAt(mx0 - ox, mz0 - oz) + 0.83, mz0 - oz,
                mx0 + ox, this.heightAt(mx0 + ox, mz0 + oz) + 0.83, mz0 + oz,
                mx1 - ox, this.heightAt(mx1 - ox, mz1 - oz) + 0.83, mz1 - oz,
                mx1 + ox, this.heightAt(mx1 + ox, mz1 + oz) + 0.83, mz1 + oz
              );
              mIdx.push(v, v + 1, v + 2, v + 1, v + 3, v + 2);
            };
            const edge = w - 0.45;
            stripe(-edge, 0.08, false);
            stripe(edge, 0.08, false);
            const lw = (w * 2) / Math.max(1, lanes); // metres per lane
            if (!oneway && lanes >= 2) {
              // centre of a two-way road: two solid lines 30 cm apart
              stripe(-0.18, 0.07, false);
              stripe(0.18, 0.07, false);
            }
            for (let k = 1; k < lanes; k++) {
              const off = -w + k * lw;
              if (!oneway && Math.abs(off) < 0.9) continue; // that is the centre
              stripe(off, 0.07, true);
            }
          }
          dist += segLen;
        }
      }
    }
    // Plain asphalt, tiled by the metre. The lines used to be painted into
    // this canvas at fixed pixel columns, which only lands on the centre and
    // the kerbs while u runs 0..1 across the road; every marking is real
    // geometry now (see the markings pass above), so the texture is just the
    // wearing course.
    const tld = new THREE.TextureLoader();
    const asphalt = tld.load('textures/asphalt.jpg');
    asphalt.wrapS = asphalt.wrapT = THREE.RepeatWrapping;
    asphalt.colorSpace = THREE.SRGBColorSpace;
    asphalt.anisotropy = 8;
    this.asphaltTex = asphalt;
    // PBR wearing course (ambientCG Asphalt025C). Lambert ignored
    // scene.environment entirely — the HDR sky lit the water and nothing
    // else — so every road read as flat grey felt. The normal map gives the
    // chip texture, the roughness map at a DIFFERENT repeat breaks the 9 m
    // colour tiling with large-scale wear you can see from altitude.
    const asphaltNor = tld.load('textures/asphalt_nor.jpg');
    asphaltNor.wrapS = asphaltNor.wrapT = THREE.RepeatWrapping;
    asphaltNor.anisotropy = 8;
    asphaltNor.repeat.set(0.83, 0.83); // ~10.8 m — decorrelated from the colour
    const asphaltRough = tld.load('textures/asphalt_rough.jpg');
    asphaltRough.wrapS = asphaltRough.wrapT = THREE.RepeatWrapping;
    asphaltRough.repeat.set(0.21, 0.21); // ~43 m wear patches
    const mat = new THREE.MeshStandardMaterial({
      map: asphalt,
      normalMap: asphaltNor,
      normalScale: new THREE.Vector2(0.7, 0.7),
      roughnessMap: asphaltRough,
      roughness: 1.0,
      metalness: 0,
      envMapIntensity: 0.45,
      color: 0xaaadb2, // Standard + env reads brighter than the old Lambert did

      polygonOffset: true,
      polygonOffsetFactor: -2,
      polygonOffsetUnits: -2,
      side: THREE.DoubleSide,
    });
    mat.name = 'asphalt'; // bullets read this (see ballistics.js)
    this.roadMat = mat; // shared by junction aprons, bridge decks, tunnel floors
    // road tiles stay collidable — the deck is 0.8 m proud, wheels rest on it
    this._addChunkedSurface(pos, uv, idx, mat, { collidable: true, ground: true });

    // sidewalk tiles — concrete
    if (sPos.length) {
      const sTex = new THREE.TextureLoader().load('textures/facades/roof_flat.jpg');
      sTex.wrapS = sTex.wrapT = THREE.RepeatWrapping;
      sTex.colorSpace = THREE.SRGBColorSpace;
      sTex.anisotropy = 8;
      const sMat = new THREE.MeshStandardMaterial({
        name: 'concrete',
        map: sTex,
        color: 0xd3cec4,
        roughness: 0.93,
        metalness: 0,
        envMapIntensity: 0.45,
        polygonOffset: true,
        polygonOffsetFactor: -1,
        polygonOffsetUnits: -1,
        side: THREE.DoubleSide,
      });
      this._addChunkedSurface(sPos, sUv, sIdx, sMat);
    }

    // concrete verge tiles — darker than the sidewalk so the curb still reads
    if (vPos.length) {
      const vTex = new THREE.TextureLoader().load('textures/facades/roof_flat.jpg');
      vTex.wrapS = vTex.wrapT = THREE.RepeatWrapping;
      vTex.colorSpace = THREE.SRGBColorSpace;
      vTex.anisotropy = 8;
      const vMat = new THREE.MeshStandardMaterial({
        name: 'concrete',
        map: vTex,
        color: 0xb7b2a8,
        roughness: 0.96,
        metalness: 0,
        envMapIntensity: 0.4,
        polygonOffset: true,
        polygonOffsetFactor: -1,
        polygonOffsetUnits: -1,
        side: THREE.DoubleSide,
      });
      this._addChunkedSurface(vPos, vUv, vIdx, vMat);
    }

    // painted markings — thermoplastic white, laid 3 cm over the asphalt
    if (mPos.length) {
      const mMat = new THREE.MeshStandardMaterial({
        color: 0xe8e6dc,
        emissive: 0x2a2a26,
        roughness: 0.55, // thermoplastic paint is smoother than the asphalt
        metalness: 0,
        envMapIntensity: 0.5,
        polygonOffset: true,
        polygonOffsetFactor: -4,
        polygonOffsetUnits: -4,
        side: THREE.DoubleSide,
      });
      this._addChunkedSurface(mPos, null, mIdx, mMat);
      console.log(`road markings: ${(mIdx.length / 3) | 0} tris`);
    }
  }

  // ---- bridges & tunnels ---------------------------------------------------


  // Tunnels, built to the cutting planned in _planTunnelCuts.
  //
  // Three real structures, not one guess:
  //   · every mouth stands in an approach CUTTING with concrete retaining
  //     walls, so the road visibly drops below grade instead of a portal ring
  //     lying on the lawn;
  //   · a short flat tunnel is CUT AND COVER — the trench gets a concrete lid
  //     at street level and the crossing street simply runs over the top;
  //   · a long one, or one that climbs into relief, is a BORE: a portal
  //     headwall at each mouth and untouched ground between them.
  _buildTunnels() {
    const conc = { pos: [], uv: [], idx: [] };
    const road = { pos: [], uv: [], idx: [] };
    const tile = { pos: [], uv: [], idx: [] }; // white tile cladding band (real tunnels)
    const line = { pos: [], uv: [], idx: [] }; // painted edge lines on the tunnel road
    const quad = (b, a1, b1, c1, d1, uvs) => {
      const v = b.pos.length / 3;
      b.pos.push(...a1, ...b1, ...c1, ...d1);
      b.idx.push(v, v + 1, v + 2, v, v + 2, v + 3);
      if (uvs) b.uv.push(...uvs);
    };
    this._tunnelMouths = [];

    // --- retaining walls down both sides of every cutting -----------------
    let run = 0;
    for (const c of this._cuts) {
      const ex = c.x2 - c.x1;
      const ez = c.z2 - c.z1;
      const len = Math.hypot(ex, ez);
      if (len < 0.5) continue;
      const dx = ex / len;
      const dz = ez / len;
      const nx = -dz;
      const nz = dx;
      // Pave the whole cutting floor. The carriageway ribbon is only as wide
      // as the road, so between it and the wall the raw ground showed — a
      // strip of lawn running along the bottom of a concrete trench.
      const fw = c.hw - 0.35;
      const cut1 = this._sampleGrid(this.grid, c.x1, c.z1) - c.y1;
      const cut2 = this._sampleGrid(this.grid, c.x2, c.z2) - c.y2;
      if (Math.max(cut1, cut2) < 0.8) { run += len; continue; } // still street
      quad(conc,
        [c.x1 - nx * fw, c.y1 + 0.74, c.z1 - nz * fw],
        [c.x1 + nx * fw, c.y1 + 0.74, c.z1 + nz * fw],
        [c.x2 + nx * fw, c.y2 + 0.74, c.z2 + nz * fw],
        [c.x2 - nx * fw, c.y2 + 0.74, c.z2 - nz * fw],
        [0, run / 4, (2 * fw) / 4, run / 4, (2 * fw) / 4, (run + len) / 4, 0, (run + len) / 4]);
      for (const sg of [-1, 1]) {
        const ax = c.x1 + nx * c.hw * sg;
        const az = c.z1 + nz * c.hw * sg;
        const bx = c.x2 + nx * c.hw * sg;
        const bz = c.z2 + nz * c.hw * sg;
        // The wall runs from the trench floor up THROUGH the ground surface
        // and 0.9 m past it, so it never leaves a gap where the refined
        // terrain meets it, and reads as the parapet a real underpass has.
        const gA = this._sampleGrid(this.grid, ax, az) + 0.9;
        const gB = this._sampleGrid(this.grid, bx, bz) + 0.9;
        if (gA - c.y1 < 0.8 && gB - c.y2 < 0.8) continue; // still at grade
        // Where two cuttings meet — a ramp joining the trench it feeds, two
        // structures crossing — the wall would stand across the open road.
        // If the ground just OUTSIDE this wall is itself cut away, there is
        // nothing to retain here.
        const mx = (ax + bx) / 2 + nx * 1.5 * sg;
        const mz = (az + bz) / 2 + nz * 1.5 * sg;
        const other = this._cutAt(mx, mz, this._sampleGrid(this.grid, mx, mz));
        if (other !== null && other < (c.y1 + c.y2) / 2 + 0.4) continue;
        quad(conc, [ax, c.y1, az], [bx, c.y2, bz], [bx, gB, bz], [ax, gA, az],
          [run / 4, c.y1 / 4, (run + len) / 4, c.y2 / 4,
            (run + len) / 4, gB / 4, run / 4, gA / 4]);
        // coping: a flat cap so the wall has thickness from above
        const ox = nx * 0.45 * sg;
        const oz = nz * 0.45 * sg;
        quad(conc, [ax, gA, az], [bx, gB, bz], [bx + ox, gB, bz + oz], [ax + ox, gA, az + oz],
          [run / 4, 0, (run + len) / 4, 0, (run + len) / 4, 0.12, run / 4, 0.12]);
      }
      run += len;
    }

    // --- cut-and-cover lids, portal fascias, and the road underneath ------
    // Strip luminaires collected along every covered passage — a real
    // underpass is LIT; without these the interiors were evenly-grey pipes
    // with no light source at all.
    const strips = [];
    const stripAlong = (P, yAt, step, margin) => {
      const cum = [0];
      for (let i = 1; i < P.length; i++) {
        cum.push(cum[i - 1] + Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]));
      }
      const total = cum[P.length - 1];
      for (let s = margin; s < total - margin; s += step) {
        let i = 0;
        while (i < P.length - 2 && cum[i + 1] < s) i++;
        const t = (s - cum[i]) / Math.max(0.01, cum[i + 1] - cum[i]);
        const x = P[i][0] + (P[i + 1][0] - P[i][0]) * t;
        const z = P[i][1] + (P[i + 1][1] - P[i][1]) * t;
        strips.push({
          x, z, y: yAt(i, t),
          ang: Math.atan2(P[i + 1][0] - P[i][0], P[i + 1][1] - P[i][1]),
        });
      }
      return cum;
    };
    for (const lid of this._lids) {
      const P = lid.pts;
      stripAlong(P, (i, t) => lid.gy[i] + (lid.gy[i + 1] - lid.gy[i]) * t + 0.16, 12, 5);
      for (let i = 0; i < P.length - 1; i++) {
        const ex = P[i + 1][0] - P[i][0];
        const ez = P[i + 1][1] - P[i][1];
        const len = Math.hypot(ex, ez) || 1;
        const nx = -ez / len;
        const nz = ex / len;
        const h = lid.hw;
        // roof slab at street level (the road above is laid 0.8 m proud, so
        // this sits just under the tarmac that crosses it)
        quad(conc,
          [P[i][0] - nx * h, lid.gy[i] + 0.78, P[i][1] - nz * h],
          [P[i][0] + nx * h, lid.gy[i] + 0.78, P[i][1] + nz * h],
          [P[i + 1][0] + nx * h, lid.gy[i + 1] + 0.78, P[i + 1][1] + nz * h],
          [P[i + 1][0] - nx * h, lid.gy[i + 1] + 0.78, P[i + 1][1] - nz * h],
          [0, 0, (2 * h) / 4, 0, (2 * h) / 4, len / 4, 0, len / 4]);
        // soffit, 40 cm under the slab — what a driver sees inside
        quad(conc,
          [P[i][0] - nx * h, lid.gy[i] + 0.38, P[i][1] - nz * h],
          [P[i + 1][0] - nx * h, lid.gy[i + 1] + 0.38, P[i + 1][1] - nz * h],
          [P[i + 1][0] + nx * h, lid.gy[i + 1] + 0.38, P[i + 1][1] + nz * h],
          [P[i][0] + nx * h, lid.gy[i] + 0.38, P[i][1] + nz * h],
          [0, 0, len / 4, 0, len / 4, (2 * h) / 4, 0, (2 * h) / 4]);
        // carriageway on the trench floor
        quad(road,
          [P[i][0] - nx * (h - 1.2), lid.y[i] + 0.06, P[i][1] - nz * (h - 1.2)],
          [P[i][0] + nx * (h - 1.2), lid.y[i] + 0.06, P[i][1] + nz * (h - 1.2)],
          [P[i + 1][0] + nx * (h - 1.2), lid.y[i + 1] + 0.06, P[i + 1][1] + nz * (h - 1.2)],
          [P[i + 1][0] - nx * (h - 1.2), lid.y[i + 1] + 0.06, P[i + 1][1] - nz * (h - 1.2)],
          [0, 0, (2 * h) / 9, 0, (2 * h) / 9, len / 9, 0, len / 9]);
      }
      // portal fascia across each mouth: the band of concrete over the hole
      for (const [i, j] of [[0, 1], [P.length - 1, P.length - 2]]) {
        const ex = P[i][0] - P[j][0];
        const ez = P[i][1] - P[j][1];
        const l = Math.hypot(ex, ez) || 1;
        const dx = ex / l;
        const dz = ez / l;
        const nx = -dz;
        const nz = dx;
        const h = lid.hw;
        const y = lid.gy[i];
        quad(conc,
          [P[i][0] - nx * h + dx * 0.5, y + 0.38, P[i][1] - nz * h + dz * 0.5],
          [P[i][0] + nx * h + dx * 0.5, y + 0.38, P[i][1] + nz * h + dz * 0.5],
          [P[i][0] + nx * h + dx * 0.5, y + 1.5, P[i][1] + nz * h + dz * 0.5],
          [P[i][0] - nx * h + dx * 0.5, y + 1.5, P[i][1] - nz * h + dz * 0.5],
          [0, 0, (2 * h) / 4, 0, (2 * h) / 4, 0.3, 0, 0.3]);
      }
    }

    // --- bores: a real barrel from portal to portal, big enough to FLY -----
    // The old bore was a 13 m stub at each mouth with solid hill in between,
    // so a tunnel was scenery. These are driven the whole way through and
    // sized for an aircraft, clamped by the cover overhead so the vault never
    // pops out of the hillside it runs under.
    this._boreTubes = [];
    for (const b of this._bores) {
      const P = b.pts;
      const Y = b.y;
      const G = b.gy;
      // one radius for the whole bore — a barrel that pinches mid-hill reads
      // as a mistake, and the tightest cover is what you have to fit through.
      // A building overhead reaches ~2 m below its ground with foundations:
      // the vault must pass UNDER that, or whole facades hang inside the
      // tube (the old-town bores all run beneath blocks).
      // Cover is what the barrel has to fit under -- measured INSIDE the
      // hill. At a portal ground minus floor is zero by definition (that is
      // what a portal is), so taking the min over every station reported ~0 m
      // of cover for every bore in the city and collapsed H to its 4.5 m
      // floor: the Noe Zhordania tunnel under the Old Town got a 4.5 m vault
      // with 25 m of rock over its middle, and nothing was ever `flyable`.
      // A bore is driven at ONE section from portal to portal, so size it by a
      // representative cover, not the thinnest point: the cover necessarily
      // tapers to zero at each mouth, and taking the minimum sized every
      // barrel as if it were all portal. Median over the interior stations.
      const PORTAL_CLEAR = 30; // m of approach to ignore at each end
      let acc = 0;
      const dist = [0];
      for (let i = 1; i < P.length; i++) {
        acc += Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]);
        dist.push(acc);
      }
      const covers = [];
      for (let i = 0; i < P.length; i++) {
        if (dist[i] < PORTAL_CLEAR || acc - dist[i] < PORTAL_CLEAR) continue;
        const bld = this._buildingNear(P[i][0], P[i][1], 18) ? 5 : 0;
        // whichever surface is higher: a real hill that the DEM flattens can
        // still justify a taller vault, and the floor is already buried
        const top = Math.max(G[i], b.gm ? b.gm[i] : G[i]);
        covers.push(top - Y[i] - bld);
      }
      if (!covers.length) {
        for (let i = 0; i < P.length; i++) covers.push(G[i] - Y[i]);
      }
      covers.sort((a2, b2) => a2 - b2);
      const cover = covers[Math.floor(covers.length / 2)];
      // Oversized on purpose (see FLY_COVER): a real road bore is ~9 m wide
      // with 4.8 m clearance. The floor above is dug to keep FLY_COVER over
      // the crown, so the vault fits inside the hill everywhere but the
      // portals, which stay at grade.
      const R = THREE.MathUtils.clamp(Math.max(b.bhw * 1.7, 14), 6, 14);
      const H = THREE.MathUtils.clamp(cover - 2, 9, 12); // vault rise under the roof
      const flyable = R >= 8.5 && H >= 7;
      const SEG = 12;
      // luminaires along the crown + arc length per centreline point (the V
      // texture coordinate used to be the point INDEX, so the concrete
      // stretched or squashed with the centreline spacing)
      const cum = stripAlong(P, (i, t) =>
        Y[i] + (Y[i + 1] - Y[i]) * t + 0.4 + H - 0.55, 15, 8);
      const nAt = (i) => {
        const j = Math.min(P.length - 1, i + 1);
        const k = Math.max(0, i - 1);
        const dx = P[j][0] - P[k][0];
        const dz = P[j][1] - P[k][1];
        const l = Math.hypot(dx, dz) || 1;
        return [-dz / l, dx / l];
      };
      // REAL road-tunnel section, not a pipe: vertical side walls with a
      // white-tile cladding band up to the spring line, an elliptical crown
      // above, raised walkways with a kerb face along both walls, and painted
      // edge lines on the carriageway. (See tools/ARCHITECT_PROMPT.md.)
      const W = R - 0.6; // clear half-width inside the lining
      const SPRING = Math.min(3.0, H * 0.45); // wall height to the crown springing
      for (let i = 0; i < P.length - 1; i++) {
        const [nx0, nz0] = nAt(i);
        const [nx1, nz1] = nAt(i + 1);
        // point at lateral offset `off` and height `y` over the floor datum
        const pt = (ii, off, y) => {
          const nx = ii === i ? nx0 : nx1;
          const nz = ii === i ? nz0 : nz1;
          return [P[ii][0] + nx * off, Y[ii] + y, P[ii][1] + nz * off];
        };
        const u0 = cum[i];
        const u1 = cum[i + 1];
        // tiled vertical walls, walkway top -> spring line (20 cm tiles: 1.6 m repeat)
        for (const sg of [-1, 1]) {
          quad(tile,
            pt(i, W * sg, 0.41), pt(i, W * sg, 0.4 + SPRING),
            pt(i + 1, W * sg, 0.4 + SPRING), pt(i + 1, W * sg, 0.41),
            [u0 / 1.6, 0.41 / 1.6, u0 / 1.6, (0.4 + SPRING) / 1.6,
              u1 / 1.6, (0.4 + SPRING) / 1.6, u1 / 1.6, 0.41 / 1.6]);
        }
        // elliptical crown from wall top to wall top (crest stays at Y+0.4+H)
        const CSEG = 10;
        for (let s = 0; s < CSEG; s++) {
          const a0 = (s / CSEG) * Math.PI;
          const a1 = ((s + 1) / CSEG) * Math.PI;
          const x0 = Math.cos(a0) * W;
          const y0 = 0.4 + SPRING + Math.sin(a0) * (H - SPRING);
          const x1 = Math.cos(a1) * W;
          const y1 = 0.4 + SPRING + Math.sin(a1) * (H - SPRING);
          quad(conc,
            pt(i, x0, y0), pt(i, x1, y1),
            pt(i + 1, x1, y1), pt(i + 1, x0, y0),
            [s / 3, u0 / 4, (s + 1) / 3, u0 / 4,
              (s + 1) / 3, u1 / 4, s / 3, u1 / 4]);
        }
        // raised walkways: top slab + kerb face toward the road
        for (const sg of [-1, 1]) {
          quad(conc,
            pt(i, W * sg, 0.41), pt(i, (W - 0.9) * sg, 0.41),
            pt(i + 1, (W - 0.9) * sg, 0.41), pt(i + 1, W * sg, 0.41),
            [0, u0 / 4, 0.225, u0 / 4, 0.225, u1 / 4, 0, u1 / 4]);
          quad(conc,
            pt(i, (W - 0.9) * sg, 0.41), pt(i, (W - 0.9) * sg, 0.06),
            pt(i + 1, (W - 0.9) * sg, 0.06), pt(i + 1, (W - 0.9) * sg, 0.41),
            [0, u0 / 4, 0.0875, u0 / 4, 0.0875, u1 / 4, 0, u1 / 4]);
        }
        // carriageway, tucked under the walkway kerbs
        quad(road,
          pt(i, -(W - 0.85), 0.06), pt(i, W - 0.85, 0.06),
          pt(i + 1, W - 0.85, 0.06), pt(i + 1, -(W - 0.85), 0.06),
          [0, u0 / 9, (2 * (W - 0.85)) / 9, u0 / 9,
            (2 * (W - 0.85)) / 9, u1 / 9, 0, u1 / 9]);
        // painted edge lines along both kerbs
        for (const sg of [-1, 1]) {
          quad(line,
            pt(i, (W - 1.2) * sg, 0.1), pt(i, (W - 1.05) * sg, 0.1),
            pt(i + 1, (W - 1.05) * sg, 0.1), pt(i + 1, (W - 1.2) * sg, 0.1),
            [0, 0, 1, 0, 1, 1, 0, 1]);
        }
      }
      // the corridor the flight model has to know about (see boreFloorAt):
      // inside it the "ground" is the tunnel floor, not the hill on top
      this._boreTubes.push({ pts: P, y: Y, r: R, h: H, flyable });
      for (const [i, j] of [[0, 1], [P.length - 1, P.length - 2]]) {
        const ex = P[i][0] - P[j][0];
        const ez = P[i][1] - P[j][1];
        const l = Math.hypot(ex, ez) || 1;
        this._tunnelMouths.push({
          x: P[i][0], z: P[i][1], y: Y[i],
          ang: Math.atan2(-ex / l, -ez / l), // face OUT of the bore
          s: R / 6, hill: true, hw: R, dx: ex / l, dz: ez / l,
        });
      }
    }
    this._indexBores();

    const tl = new THREE.TextureLoader();
    const ct = tl.load('textures/concrete.jpg');
    ct.wrapS = ct.wrapT = THREE.RepeatWrapping;
    ct.colorSpace = THREE.SRGBColorSpace;
    ct.anisotropy = 8;
    const mk = (b, mat, collide = true) => {
      if (!b.idx.length) return;
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(b.pos), 3));
      g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(b.uv), 2));
      g.setIndex(new THREE.BufferAttribute(new Uint32Array(b.idx), 1));
      g.computeVertexNormals();
      const m = new THREE.Mesh(g, mat);
      m.receiveShadow = true;
      m.castShadow = true;
      m.matrixAutoUpdate = false;
      this.group.add(m);
      if (collide) {
        g.computeBoundsTree();
        this.collidables.push(m);
      }
    };
    const ctNor = tl.load('textures/concrete_nor.jpg');
    ctNor.wrapS = ctNor.wrapT = THREE.RepeatWrapping;
    const ctRough = tl.load('textures/concrete_rough.jpg');
    ctRough.wrapS = ctRough.wrapT = THREE.RepeatWrapping;
    mk(conc, new THREE.MeshStandardMaterial({
      map: ct, normalMap: ctNor, roughnessMap: ctRough, roughness: 1,
      metalness: 0, envMapIntensity: 0.3,
      // faint self-illumination so a bore interior is never pitch black
      // between the strip luminaires
      color: 0xb2aea6, emissive: 0x161512, side: THREE.DoubleSide }));
    mk(road, new THREE.MeshStandardMaterial({
      map: this.asphaltTex ?? ct, roughness: 0.95, metalness: 0,
      envMapIntensity: 0.25,
      color: 0xb9bcc0, emissive: 0x242426, side: THREE.DoubleSide }));
    // white 20 cm tile cladding (ambientCG Tiles107) — the band every real
    // road tunnel carries; slight self-glow so the luminaires read on it
    const tt = tl.load('textures/tunnel_tiles.jpg');
    tt.wrapS = tt.wrapT = THREE.RepeatWrapping;
    tt.colorSpace = THREE.SRGBColorSpace;
    tt.anisotropy = 8;
    const ttN = tl.load('textures/tunnel_tiles_nor.jpg');
    ttN.wrapS = ttN.wrapT = THREE.RepeatWrapping;
    const ttR = tl.load('textures/tunnel_tiles_rough.jpg');
    ttR.wrapS = ttR.wrapT = THREE.RepeatWrapping;
    mk(tile, new THREE.MeshStandardMaterial({
      map: tt, normalMap: ttN, roughnessMap: ttR, roughness: 1,
      metalness: 0, envMapIntensity: 0.5,
      color: 0xdedcd6, emissive: 0x232320, side: THREE.DoubleSide }));
    mk(line, new THREE.MeshStandardMaterial({
      color: 0xe8e6dc, emissive: 0x35342e, roughness: 0.55, metalness: 0,
      polygonOffset: true, polygonOffsetFactor: -3, polygonOffsetUnits: -3,
      side: THREE.DoubleSide }), false);

    // --- strip luminaires: one instanced emissive bar every ~15 m ---------
    // HDR colour (> 1) so the bloom pass picks them up and they GLOW — the
    // cheapest possible tunnel lighting, no real lights involved.
    if (strips.length) {
      const g = new THREE.BoxGeometry(0.5, 0.12, 3.2);
      const m = new THREE.MeshBasicMaterial({ color: new THREE.Color(2.6, 2.35, 1.85) });
      const inst = new THREE.InstancedMesh(g, m, strips.length);
      const mtx = new THREE.Matrix4();
      const q = new THREE.Quaternion();
      const sv = new THREE.Vector3(1, 1, 1);
      strips.forEach((st, i) => {
        q.setFromAxisAngle(new THREE.Vector3(0, 1, 0), st.ang);
        mtx.compose(new THREE.Vector3(st.x, st.y, st.z), q, sv);
        inst.setMatrixAt(i, mtx);
      });
      inst.instanceMatrix.needsUpdate = true;
      inst.matrixAutoUpdate = false;
      this.group.add(inst);
      console.log(`tunnel luminaires: ${strips.length}`);
    }
  }

  // ---- water ---------------------------------------------------------------

  _buildWater(polys) {
    // Still bodies (Tbilisi Sea, Lisi, Turtle Lake…) get a flat reflective
    // surface at their median bank height from the OSM polygon. Rivers are
    // built separately from the carved channel itself (_buildRiverSheet) —
    // the stitched OSM riverbank rings are unreliable (self-intersections).
    const pos = [];
    const idx = [];
    for (const flat of polys) {
      const n = flat.length / 2;
      if (n < 3 || n > 20000) continue;
      const contour = [];
      for (let i = 0; i < n; i++) contour.push(new THREE.Vector2(flat[i * 2] / 10, flat[i * 2 + 1] / 10));
      if (Math.abs(THREE.ShapeUtils.area(contour)) < 5000) continue;
      let tris;
      try {
        tris = THREE.ShapeUtils.triangulateShape(contour, []);
      } catch {
        continue;
      }
      if (!tris.length) continue;
      // SHAPE test, not just slope: a flat reach of the Mtkvari (Rike, the
      // Metekhi bend) has a bank spread under 5 m, so it slipped through as a
      // "lake" and was drawn as one enormous flat polygon lying across the
      // park and the bank — the single ugliest thing on the river. A lake is
      // compact; a river reach is long and thin. 4*pi*A/P^2 is 1 for a circle.
      let per = 0;
      for (let i = 0; i < n; i++) {
        const a = contour[i];
        const b2 = contour[(i + 1) % n];
        per += Math.hypot(b2.x - a.x, b2.y - a.y);
      }
      const areaAbs = Math.abs(THREE.ShapeUtils.area(contour));
      if (per > 0 && (4 * Math.PI * areaAbs) / (per * per) < 0.22) continue; // river
      const banks = contour.map((p) => this.heightAt(p.x, p.y)).sort((a, b) => a - b);
      const spread = banks[banks.length - 1] - banks[0];
      if (spread > 5) continue; // sloping water = river, handled by the sheet
      const level = banks[Math.floor(banks.length / 2)] + 0.3;
      const v0 = pos.length / 3;
      for (const p of contour) pos.push(p.x, level, p.y);
      for (const t of tris) idx.push(v0 + t[0], v0 + t[1], v0 + t[2]);
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    // world-space UVs for the rippling normal map
    const wuv = new Float32Array((pos.length / 3) * 2);
    for (let i = 0; i < pos.length / 3; i++) {
      wuv[i * 2] = pos[i * 3] / 55;
      wuv[i * 2 + 1] = pos[i * 3 + 2] / 55;
    }
    geo.setAttribute('uv', new THREE.BufferAttribute(wuv, 2));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    geo.computeVertexNormals();
    const waterNormals = new THREE.TextureLoader().load('textures/waternormals.jpg');
    waterNormals.wrapS = waterNormals.wrapT = THREE.RepeatWrapping;
    // Standard material: sky reflections via scene.environment + animated ripples.
    // Colour taken from photographs of the real river (Commons "Mtkvari River
    // Cruise" set + "Mtkvari Cliff Tbilisi"): the Mtkvari is a PALE OLIVE-JADE
    // green, opaque like pea soup — not blue, and not the dark grey-teal I
    // guessed at first. Roughness 0.42 keeps it matte: at 0.12 it mirrored the
    // sky and read as cartoon blue from the air.
    const mat = new THREE.MeshStandardMaterial({
      color: 0x566440, // darker than the photo olive: reflections add light back
      // 0.34: physical fresnel keeps the from-the-air view matte olive (2 %
      // normal-incidence reflectance), while a low pass over the water gets
      // real grazing-angle sky/bank reflections. The old 0.12 experiment
      // failed because envMapIntensity was high and there was ONE scrolling
      // normal layer — a flat mirror; two counter-drifting layers (below)
      // break the mirror into ripple glints instead. The hazy-white sky also
      // means grazing reflections wash pale — envMapIntensity stays modest.
      // 0.5/0.3: the sky here is hazy WHITE — at grazing angles fresnel goes
      // to 1 and any glossier setting turns the whole river into a white
      // mirror sheet from a low pass (re-learned 2026-09; the original
      // 0.52/0.3 had already converged on this — don't chase glossy water)
      roughness: 0.5,
      metalness: 0.0,
      envMapIntensity: 0.3,
      // near-opaque: at 0.92 the pale carved bed (painted with the water
      // class colour) bled through and turned the whole river milky
      transparent: true,
      opacity: 0.97,
      normalMap: waterNormals,
      normalScale: new THREE.Vector2(0.9, 0.9),
      side: THREE.DoubleSide,
    });
    // Two ripple layers at different scales drifting in DIFFERENT directions:
    // the single scrolling normal map read as one pattern translating over an
    // obviously static sheet. uTime drives both; see update().
    this._waterTime = { value: 0 };
    mat.onBeforeCompile = (sh) => {
      sh.uniforms.uWaterTime = this._waterTime;
      sh.fragmentShader = sh.fragmentShader
        .replace('#include <normalmap_pars_fragment>',
          '#include <normalmap_pars_fragment>\nuniform float uWaterTime;')
        .replace('#include <normal_fragment_maps>', `
        #ifdef USE_NORMALMAP_TANGENTSPACE
          vec2 wuvA = vNormalMapUv + vec2(uWaterTime * 0.014, uWaterTime * 0.009);
          vec2 wuvB = vNormalMapUv * 2.6 + vec2(-uWaterTime * 0.011, uWaterTime * 0.017);
          vec3 wnA = texture2D( normalMap, wuvA ).xyz * 2.0 - 1.0;
          vec3 wnB = texture2D( normalMap, wuvB ).xyz * 2.0 - 1.0;
          vec3 mapN = normalize( vec3( wnA.xy + wnB.xy * 0.65, wnA.z * wnB.z ) );
          mapN.xy *= normalScale;
          normal = normalize( tbn * mapN );
        #endif
        `);
    };
    mat.name = 'water';
    this.waterMat = mat;
    const mesh = new THREE.Mesh(geo, mat);
    mesh.matrixAutoUpdate = false;
    this.group.add(mesh);
  }

  // The Mtkvari (and the other streams) as one smooth sheet that hugs the
  // carved channel: trace the water-class region's boundary on the terrain
  // grid (marching-squares style, water kept on the left), Chaikin-smooth the
  // staircase away, triangulate with islands as holes, then subdivide and
  // sample the true water level at every vertex so the surface slopes
  // downstream and meets the banks with a real shoreline.
  _buildRiverSheet() {
    if (!this.waterMat) return;
    const g = this.grid;
    const W = g.w;
    const H = g.h;
    const wat = (r, c) => r >= 0 && c >= 0 && r < H && c < W && g.classes[r * W + c] === 2;
    // directed boundary edges between lattice corners, water on the left
    const out = new Map(); // "i_j" -> [[i2,j2],...]
    const push = (i1, j1, i2, j2) => {
      const k = `${i1}_${j1}`;
      let a = out.get(k);
      if (!a) out.set(k, (a = []));
      a.push([i2, j2]);
    };
    for (let r = 0; r < H; r++) {
      for (let c = 0; c < W; c++) {
        if (!wat(r, c)) continue;
        if (!wat(r - 1, c)) push(c, r, c + 1, r);
        if (!wat(r + 1, c)) push(c + 1, r + 1, c, r + 1);
        if (!wat(r, c - 1)) push(c, r + 1, c, r);
        if (!wat(r, c + 1)) push(c + 1, r, c + 1, r + 1);
      }
    }
    const loops = [];
    for (const [start, ends] of out) {
      while (ends.length) {
        const [si, sj] = start.split('_').map(Number);
        let [ci, cj] = ends.pop();
        let pi = si;
        let pj = sj;
        const loop = [[si, sj]];
        let guard = 0;
        while ((ci !== si || cj !== sj) && guard++ < 200000) {
          loop.push([ci, cj]);
          const cands = out.get(`${ci}_${cj}`);
          if (!cands || !cands.length) break;
          let pick = 0;
          if (cands.length > 1) {
            // saddle: prefer the sharpest left turn to keep hugging the region
            const dx = ci - pi;
            const dj = cj - pj;
            let best = -9;
            cands.forEach(([ni, nj], k2) => {
              const cross = dx * (nj - cj) - dj * (ni - ci);
              if (cross > best) {
                best = cross;
                pick = k2;
              }
            });
          }
          const [ni, nj] = cands.splice(pick, 1)[0];
          pi = ci;
          pj = cj;
          ci = ni;
          cj = nj;
        }
        if (loop.length >= 4 && ci === si && cj === sj) loops.push(loop);
      }
    }
    // corners -> world, two rounds of corner cutting to melt the pixel steps
    const toWorld = ([i, j]) => [g.minx + i * g.step, g.minz + j * g.step];
    const rings = [];
    const addRing = (pts) => {
      const v2 = pts.map(([x, z]) => new THREE.Vector2(x, z));
      const area = THREE.ShapeUtils.area(v2);
      if (Math.abs(area) < 400) return;
      const banks = [];
      for (let k = 0; k < v2.length; k += Math.max(1, v2.length >> 5)) {
        banks.push(this.heightAt(v2[k].x, v2[k].y));
      }
      banks.sort((a, b) => a - b);
      rings.push({
        v2, area,
        bbox: new THREE.Box2().setFromPoints(v2),
        spread: banks[banks.length - 1] - banks[0],
      });
    };
    // The REAL outlines, when we have them. Marching squares over the 30 m
    // class grid calls a cell water if the river merely touches it, so the
    // traced channel is up to a cell too wide on EACH side and its shape is a
    // smoothed staircase. OSM has the actual bank line, and because the bank
    // walk below reads these same rings, the quay follows the real bank too.
    if (this._river?.polys?.length) {
      for (const poly of this._river.polys) {
        if (!poly.outer || poly.outer.length < 4) continue;
        const out = poly.outer.map(([x, z]) => new THREE.Vector2(x, z));
        // outers must wind positive here, holes negative
        const outer = THREE.ShapeUtils.area(out) > 0
          ? poly.outer : [...poly.outer].reverse();
        addRing(outer);
        for (const h of poly.holes || []) {
          if (h.length < 4) continue;
          const hv = h.map(([x, z]) => new THREE.Vector2(x, z));
          addRing(THREE.ShapeUtils.area(hv) < 0 ? h : [...h].reverse());
        }
      }
      console.log(`water: ${rings.length} rings from the real OSM outlines`);
    }
    for (const loop of this._river?.polys?.length ? [] : loops) {
      let pts = loop.map(toWorld);
      for (let pass = 0; pass < 2; pass++) {
        const sm = [];
        for (let k = 0; k < pts.length; k++) {
          const p = pts[k];
          const q = pts[(k + 1) % pts.length];
          sm.push([p[0] * 0.75 + q[0] * 0.25, p[1] * 0.75 + q[1] * 0.25]);
          sm.push([p[0] * 0.25 + q[0] * 0.75, p[1] * 0.25 + q[1] * 0.75]);
        }
        pts = sm;
      }
      if (Math.abs(THREE.ShapeUtils.area(pts.map(([x, z]) => new THREE.Vector2(x, z))))
        < g.step * g.step * 2.5) continue;
      addRing(pts);
    }
    // group island holes (opposite winding) under their containing outers;
    // near-flat outers are the lakes — their polygon surface already exists
    const outers = rings.filter((r) => r.area > 0);
    const holes = rings.filter((r) => r.area <= 0);
    // Measured water surface on the Mtkvari centreline, or null away from it.
    // The river runs 462 m where it enters the map down to 337 m where it
    // leaves, and the profile is forced non-increasing downstream, so this can
    // never put water above ground it has already passed.
    const measuredLevel = (x, z) => {
      const R = this._river;
      if (!R) return null;
      const b = R.hash.get(Math.floor(x / R.cell) * 100003 + Math.floor(z / R.cell));
      if (!b) return null;
      // nearest point on the centreline POLYLINE, not the nearest station:
      // at 150 m spacing the stations alone quantise the surface into steps
      let bd = Infinity;
      let lvl = null;
      for (const i of b) {
        for (const j of [i - 1, i]) {
          const a = R.centre[j];
          const c = R.centre[j + 1];
          if (!a || !c) continue;
          const ex = c[0] - a[0];
          const ez = c[1] - a[1];
          const l2 = ex * ex + ez * ez;
          if (l2 < 1e-6) continue;
          let t = ((x - a[0]) * ex + (z - a[1]) * ez) / l2;
          t = t < 0 ? 0 : t > 1 ? 1 : t;
          const d2 = (x - (a[0] + ex * t)) ** 2 + (z - (a[1] + ez * t)) ** 2;
          if (d2 < bd) { bd = d2; lvl = a[2] + (c[2] - a[2]) * t; }
        }
      }
      if (lvl === null) return null;
      // Only speak where the river really is. A generous radius round the
      // centreline also claimed the valley floor beside it; the OSM polygon
      // knows the actual channel, with one cell of slack for the bank itself.
      const m = R.mask;
      const cx = Math.floor(x / R.mcell);
      const cz = Math.floor(z / R.mcell);
      for (let ox = -1; ox <= 1; ox++) {
        for (let oz = -1; oz <= 1; oz++) {
          if (m.has((cx + ox) * 100003 + (cz + oz))) return lvl;
        }
      }
      return null;
    };
    // tools/check_river.mjs asks the game itself rather than keeping a copy
    this._riverLevelAt = measuredLevel;
    const levelAt = (x, z) => {
      const measured = measuredLevel(x, z);
      if (measured !== null) return measured;
      // Was: the MINIMUM terrain height in a 35-75 m ring. The channel is
      // carved unevenly — the true course deep, the over-marked margins
      // shallow — so a min-based level sat BELOW those margins and left a
      // pale dry riverbed on show (which is what "the river is ugly" actually
      // was; hiding every water mesh left the patch untouched). Take a low
      // PERCENTILE instead, so the surface fills the channel to its banks.
      const samples = [this.heightAt(x, z)];
      for (const rr of [35, 75]) {
        for (let k = 0; k < 6; k++) {
          const a = (k / 6) * Math.PI * 2;
          samples.push(this.heightAt(x + Math.cos(a) * rr, z + Math.sin(a) * rr));
        }
      }
      samples.sort((p1, p2) => p1 - p2);
      let m = samples[Math.floor(samples.length * 0.35)];
      // 1.9 m over the lowest sampled bank. Do NOT lower this: at 0.35 the
      // sheet sank BELOW the carved bed and what you saw was the terrain
      // itself, painted with the water vertex colour — a flat pale polygon
      // that no amount of fiddling with the sheet could change.
      return m + 0.7;
    };
    // The water CLASS grid is 60 m, and a cell counts as water if the river
    // merely touches it — so the traced region is up to a cell wider than the
    // real channel on each side and the sheet spilled flat across Rike park
    // and the Metekhi bank. Shrink each ring inward before triangulating: the
    // BANKS are still placed on the true outline, so what opens up between
    // them and the water is a gravel toe, which is what a real river has.
    // NO ring offsetting. A fixed inward offset of a staircase-traced boundary
    // self-intersects wherever the channel narrows or bends, and the
    // triangulation then explodes into the field of jagged shards visible in
    // the player's screenshot at Avlabari. If the channel needs narrowing it
    // has to be done on the CLASS RASTER in build_map.py, not on the ring.
    const pos = [];
    const idx = [];
    for (const o of outers) {
      // Flat reaches used to be handed to _buildWater as "lakes", but that
      // draws ONE level across the whole polygon — and since the shape test
      // there now (correctly) refuses river-shaped polygons, those reaches
      // ended up with no water at all and you saw the carved bed. The sheet
      // slopes downstream, so let it take every region that is not a genuine
      // compact lake; _buildWater still owns the Tbilisi Sea, Lisi, Turtle.
      if (o.spread <= 4 && o.bbox.max.x - o.bbox.min.x < 900
        && o.bbox.max.y - o.bbox.min.y < 900) continue;
      const myHoles = holes.filter((h2) => o.bbox.containsBox(h2.bbox)).map((h2) => h2.v2);
      let tris;
      try {
        tris = THREE.ShapeUtils.triangulateShape(o.v2, myHoles);
      } catch {
        continue;
      }
      const verts = [o.v2, ...myHoles].flat().map((p) => [p.x, p.y]);
      let faces = tris.map((t) => [t[0], t[1], t[2]]);
      for (let pass = 0; pass < 2; pass++) {
        const mids = new Map();
        const midOf = (a, b) => {
          const key = a < b ? `${a}_${b}` : `${b}_${a}`;
          let m = mids.get(key);
          if (m === undefined) {
            m = verts.length;
            verts.push([(verts[a][0] + verts[b][0]) / 2, (verts[a][1] + verts[b][1]) / 2]);
            mids.set(key, m);
          }
          return m;
        };
        const next = [];
        for (const [a, b, c] of faces) {
          const ab = midOf(a, b);
          const bc = midOf(b, c);
          const ca = midOf(c, a);
          next.push([a, ab, ca], [ab, b, bc], [ca, bc, c], [ab, bc, ca]);
        }
        faces = next;
      }
      const v0 = pos.length / 3;
      for (const [x, z] of verts) pos.push(x, levelAt(x, z), z);
      for (const f of faces) idx.push(v0 + f[0], v0 + f[1], v0 + f[2]);
    }
    // The bank: grass ran straight into the water. Real Tbilisi keeps the
    // Mtkvari between built embankments through the whole centre, so walk the
    // same smoothed rings and place a quay wall wherever the bank rises fast,
    // tumbled riprap where it doesn't.
    this._riverBanks = [];
    let flipped = 0;
    for (const o of outers) {
      if (o.spread <= 4 && o.bbox.max.x - o.bbox.min.x < 900
        && o.bbox.max.y - o.bbox.min.y < 900) continue; // genuine lake
      for (const ring of [o.v2, ...holes.filter((h2) => o.bbox.containsBox(h2.bbox)).map((h2) => h2.v2)]) {
        const n = ring.length;
        if (n < 8) continue;
        // Walk the ring at a true 3 m step -- one kit piece per step. The
        // smoothed ring points are 15 m apart on average and up to 60 m, so
        // emitting one piece per EDGE left the quay as a dashed line of 3 m
        // blocks with 12 m holes between them, which is what made the river
        // read as unbuilt. Long edges get subdivided; the leftover carries
        // into the next edge so the joints stay flush around corners.
        const STEP = 3.0;
        let acc = 0; // distance already covered since the last piece
        for (let k = 0; k < n; k++) {
          const a = ring[k];
          const b2 = ring[(k + 1) % n];
          const ex = b2.x - a.x;
          const ez = b2.y - a.y;
          const len = Math.hypot(ex, ez);
          if (len < 1e-3) continue;
          // Outward normal: the ring is supposed to keep water on the left,
          // so the LAND side is to the right of the direction of travel. That
          // holds for the outer rings but NOT for the island rings inside
          // them, whose winding is reversed -- on those the wall was built
          // facing inland. Trust the terrain instead: the river is whichever
          // side is lower. Decided per bay, just below.
          for (let t = STEP - acc; t <= len; t += STEP) {
            const f = t / len;
            const mx = a.x + ex * f;
            const mz = a.y + ez * f;
            let nx = ez / len;
            let nz = -ex / len;
            const hLand = this.heightAt(mx + nx * 10, mz + nz * 10);
            const hWater = this.heightAt(mx - nx * 10, mz - nz * 10);
            if (hLand != null && hWater != null && hLand < hWater) {
              nx = -nx; // this ring runs the other way round; the land is -n
              nz = -nz;
              flipped++;
            }
            const onRiver = measuredLevel(mx, mz) !== null;
            const streetHere = this._roadNear
              ? this._roadNear.has(Math.floor(mx / this._roadNearCell) * 100003
                + Math.floor(mz / this._roadNearCell))
              : true;
            const wl = levelAt(mx, mz);
            const bankH = this.heightAt(mx + nx * 6, mz + nz * 6);
            const rise = bankH - wl;
            if (rise < 0.4) continue; // flat wash — nothing to hold back
            // The DEM is far coarser than the wall it is supposed to describe:
            // a real 5 m quay arrives as a 25 m ramp, so measuring the bank
            // only 6 m inland reported "gentle slope" along embankments that
            // are walled in every photograph. Take the rise over a 22 m
            // baseline as well, and let either one call it a built bank.
            const farH = this.heightAt(mx + nx * 22, mz + nz * 22);
            const riseFar = farH == null ? rise : farH - wl;
            this._riverBanks.push({
              // the bank still exists here, but nothing may be BUILT on it
              clear: this._inTunnelCorridor(mx, mz),
              x: mx + nx * 0.35,
              z: mz + nz * 0.35,
              // Top of the wall = the walkway behind it. On the coarse DEM the
              // point 6 m inland is still half way down the ramp, which left
              // the wall barely clearing the water and buried in the slope, so
              // take whichever of the two baselines is higher.
              y: wl + Math.min(9.0, Math.max(1.2, Math.max(rise, riseFar))),
              ang: Math.atan2(-nx, -nz), // face the water
              // Three real bank types, in the proportions the photos show:
              // a rock gorge through the old town, built quays downtown, and
              // loose stone where the bank barely rises.
              // Thresholds are against the MEASURED water surface now, and
              // the real bank stands 7-14 m above it through the centre, so
              // the old "over 6 m means rock gorge" turned the whole built
              // embankment into cliff. A real Mtkvari cliff is the Old Town
              // gorge under Metekhi, which is 15 m and up.
              // A built quay only where the real river runs past a real
              // street. Everywhere else the bank is rock or loose stone.
              kind: rise > 15 ? 'cliff'
                : (onRiver && streetHere && (rise > 1.6 || riseFar > 1.5)) ? 'wall'
                  : 'rip',
              // the piece is authored 4.2 m tall, so scale it to the drop it
              // has to cover and add a little so the toe stays buried
              sy: rise > 15 ? Math.min(2.4, rise / 10)
                : Math.min(2.4, Math.max(0.6, (Math.max(rise, riseFar) + 1.2) / 4.2)),
            });
          }
          acc = (acc + len) % STEP;
        }
      }
    }
    console.log(`river banks: ${this._riverBanks.length} bays, ${flipped} faced inland before the terrain test`);
    // The wall / riprap / cliff choice is made per 3 m bay from the local bank
    // rise, and that rise wobbles across the thresholds from bay to bay. Along
    // an obviously built embankment that broke the quay -- and the parapet
    // standing on it -- into six-metre dashes. Take the majority over a 21 m
    // window so a stretch of bank reads as ONE kind of bank.
    {
      const bank = this._riverBanks;
      const W = 3; // bays either side
      const raw = bank.map((b) => b.kind);
      for (let i = 0; i < bank.length; i++) {
        const tally = { wall: 0, rip: 0, cliff: 0 };
        for (let k = -W; k <= W; k++) {
          const j = i + k;
          if (j < 0 || j >= bank.length) continue;
          // consecutive bays are 3 m apart; anything further is another ring
          const d = Math.hypot(bank[j].x - bank[i].x, bank[j].z - bank[i].z);
          if (d > 4.5 * Math.abs(k) + 2) continue;
          tally[raw[j]]++;
        }
        let best = raw[i];
        for (const k of ['wall', 'rip', 'cliff']) {
          if (tally[k] > tally[best]) best = k;
        }
        bank[i].kind = best;
      }
    }
    if (!idx.length) return;
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    const wuv = new Float32Array((pos.length / 3) * 2);
    for (let i = 0; i < pos.length / 3; i++) {
      wuv[i * 2] = pos[i * 3] / 55;
      wuv[i * 2 + 1] = pos[i * 3 + 2] / 55;
    }
    geo.setAttribute('uv', new THREE.BufferAttribute(wuv, 2));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    geo.computeVertexNormals();
    const mesh = new THREE.Mesh(geo, this.waterMat);
    mesh.matrixAutoUpdate = false;
    this.group.add(mesh);
  }

  // ---- trees ---------------------------------------------------------------

  async _buildTrees(roads = []) {
    const g = this.grid;
    const rnd = (i, s) => fract(Math.sin(i * 127.1 + s * 311.7) * 43758.5453);

    // Real photo-scanned chestnut (9k tris, WEBP leaf/bark textures + true leaf
    // alpha — no fake tint). Placed in two distance-culled layers so only the
    // trees near the plane are ever drawn:
    //   • street trees — one every ~26 m along residential..tertiary avenues,
    //     which is where the player actually sees them up close;
    //   • forest fill  — a sparse scatter across wooded cells (these trees are
    //     ~30x heavier than the old Kenney ones, so density drops to match).
    const street = [];
    const STREET_CAP = 45000;
    let idc = 0;
    for (const road of roads) {
      if (street.length >= STREET_CAP) break;
      const w = road[0];
      if (w < 6 || w > 13) continue; // residential..tertiary
      const n = (road.length - 1) / 2;
      let acc = 0;
      for (let s = 0; s < n - 1 && street.length < STREET_CAP; s++) {
        const x1 = road[1 + s * 2] / 10;
        const z1 = road[2 + s * 2] / 10;
        const x2 = road[3 + s * 2] / 10;
        const z2 = road[4 + s * 2] / 10;
        const len = Math.hypot(x2 - x1, z2 - z1);
        if (len < 1) continue;
        const nx = -(z2 - z1) / len;
        const nz = (x2 - x1) / len;
        const off = w / 2 + 2.5;
        acc += len;
        while (acc > 26 && street.length < STREET_CAP) {
          acc -= 26;
          const t = 1 - acc / len;
          for (const sgn of [-1, 1]) {
            const x = x1 + (x2 - x1) * t + nx * off * sgn;
            const z = z1 + (z2 - z1) * t + nz * off * sgn;
            street.push({ x, z, i: 900000 + idc++ });
          }
        }
      }
    }

    const forest = [];
    const FOREST_CAP = 70000;
    const TARGET_M2 = 240; // ~1 tree per 240 m² of woodland (was 110)
    const cells = [];
    for (let r = 0; r < g.h; r++)
      for (let c = 0; c < g.w; c++)
        if (g.classes[r * g.w + c] === 1) cells.push(r * g.w + c);
    const capPer = Math.max(1, Math.floor(FOREST_CAP / Math.max(1, cells.length)));
    const perCell = Math.min(Math.round((g.step * g.step) / TARGET_M2), capPer);
    for (const i of cells) {
      if (forest.length >= FOREST_CAP) break;
      const r = Math.floor(i / g.w);
      const c = i % g.w;
      const cx0 = g.minx + c * g.step;
      const cz0 = g.minz + r * g.step;
      for (let k = 0; k < perCell && forest.length < FOREST_CAP; k++) {
        const id = i * 131 + k;
        forest.push({ x: cx0 + rnd(id, 2) * g.step, z: cz0 + rnd(id, 3) * g.step, i: id });
      }
    }

    const parts = await this._loadTree('models/trees_new/chestnut.glb', 11);
    if (!parts) return;
    this._instanceTrees(parts, street, rnd, 300, 430);
    this._instanceTrees(parts, forest, rnd, 300, 620);
  }

  // Load a real tree GLB and return per-material {geo, mat} parts, scaled so the
  // whole tree stands ~targetH m tall with its base at y=0. Keeps the model's
  // own WEBP textures and colours (leaf cards cut out via alphaTest).
  async _loadTree(url, targetH) {
    const scene = await new Promise((res) =>
      newGltfLoader().load(url, (gltf) => res(gltf.scene), undefined, () => res(null)));
    if (!scene) return null;
    scene.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(scene);
    const s = targetH / ((box.max.y - box.min.y) || 1);
    const parts = [];
    scene.traverse((o) => {
      if (!o.isMesh) return;
      const geo = o.geometry.clone();
      geo.applyMatrix4(o.matrixWorld);
      geo.scale(s, s, s);
      if (!geo.getAttribute('normal')) geo.computeVertexNormals();
      const m0 = Array.isArray(o.material) ? o.material[0] : o.material;
      // Leaf cards export as alphaMode BLEND (transparent) — for instanced
      // foliage that sorts badly, so cut them out with alphaTest instead.
      const cutout = m0.alphaTest > 0 ? m0.alphaTest : (m0.transparent && m0.map ? 0.5 : 0);
      const mat = new THREE.MeshLambertMaterial({
        map: m0.map || null,
        color: 0xffffff, // multiply by white → the texture's real colours show
        side: m0.side ?? THREE.FrontSide,
        alphaTest: cutout,
        transparent: false,
      });
      if (mat.map) mat.map.colorSpace = THREE.SRGBColorSpace;
      // BARK OR LEAF? A trunk is stiff and a canopy is not, and the two come
      // out of the same GLB as separate materials. The cut-out one is the
      // leaf cards; it gets the shiver as well as the lean.
      makeWindy(mat, cutout > 0
        ? { bend: 0.0030, flutter: 0.0045 }
        : { bend: 0.0014, flutter: 0 });
      parts.push({ geo, mat });
    });
    return parts;
  }

  // Instance every part of a tree across the given points (shared transform per
  // point: ground-snapped, random yaw, ±size variety), chunked + distance-culled.
  _instanceTrees(parts, pts, rnd, chunkM, maxDist) {
    if (!pts.length) return;
    const e = new THREE.Euler();
    const q = new THREE.Quaternion();
    const sv = new THREE.Vector3();
    // Past ~300 m of true distance a full tree (4k tris) and its baked cross
    // imposter (6 tris) are the same pixels — the first part carries the
    // imposter for the whole tree, the rest simply stop past the switch.
    const lodDist = 300 + chunkM * 0.72;
    const imp = this._treeImposter(parts);
    parts.forEach(({ geo, mat }, pi) => {
      const lod = imp ? (pi === 0 ? { dist: lodDist, geo: imp.geo, mat: imp.mat } : { dist: lodDist }) : null;
      this._placeChunkedInstanced(geo, mat, pts, (m, p) => {
        const s = 0.8 + rnd(p.i, 4) * 0.5;
        e.set(0, rnd(p.i, 8) * Math.PI * 2, 0);
        q.setFromEuler(e);
        sv.set(s, s * (0.9 + rnd(p.i, 5) * 0.25), s);
        m.compose(new THREE.Vector3(p.x, this.heightAt(p.x, p.z) - 0.2, p.z), q, sv);
      }, chunkM, false, maxDist, null, null, lod);
    });
  }

  // Load a solid-model prop GLB (playground, lamp…) and return per-material
  // {geo, mat} parts, recentered on X/Z with base at y=0 and scaled to ~targetH
  // metres. Keeps each material's own colour / texture (no tint) — for the
  // user's park kit. Textured (leaf-like) parts get alphaTest; the rest keep
  // their base colour or baked vertex colours.
  async _loadProp(url, targetH) {
    const scene = await new Promise((res) =>
      newGltfLoader().load(url, (gltf) => res(gltf.scene), undefined, () => res(null)));
    if (!scene) return null;
    scene.updateMatrixWorld(true);
    const geos = [];
    scene.traverse((o) => {
      if (!o.isMesh) return;
      const g = o.geometry.clone();
      g.applyMatrix4(o.matrixWorld);
      if (!g.getAttribute('normal')) g.computeVertexNormals();
      geos.push({ g, m0: Array.isArray(o.material) ? o.material[0] : o.material });
    });
    if (!geos.length) return null;
    const box = new THREE.Box3();
    for (const { g } of geos) { g.computeBoundingBox(); box.union(g.boundingBox); }
    const size = new THREE.Vector3(); box.getSize(size);
    const ctr = new THREE.Vector3(); box.getCenter(ctr);
    // targetH>0 scales to that height; 0/undefined keeps native (real-world) scale
    const s = targetH ? targetH / (size.y || 1) : 1;
    const parts = [];
    for (const { g, m0 } of geos) {
      g.translate(-ctr.x, -box.min.y, -ctr.z); // centre X/Z, base on the ground
      g.scale(s, s, s);
      const hasVC = !!g.getAttribute('color');
      const cutout = m0.alphaTest > 0 ? m0.alphaTest : (m0.transparent && m0.map ? 0.5 : 0);
      const mat = new THREE.MeshLambertMaterial({
        map: m0.map || null,
        color: m0.map || hasVC ? 0xffffff : (m0.color ? m0.color.getHex() : 0xb0b0b0),
        vertexColors: hasVC,
        side: m0.side ?? THREE.FrontSide,
        alphaTest: cutout,
      });
      if (mat.map) mat.map.colorSpace = THREE.SRGBColorSpace;
      parts.push({ geo: g, mat });
    }
    return parts;
  }

  // Bucket instances into ~2 km chunks and register each as distance-cullable,
  // so faraway trees/houses aren't drawn every frame ("only what's near me").
  // `lod` = { dist, geo?, mat? }: past `dist` (TRUE 3D distance, so altitude
  // counts) the chunk swaps to the baked imposter mesh — same silhouette and
  // colors at a few triangles — or simply hides if no imposter geo is given.
  _placeChunkedInstanced(geometry, material, items, makeMatrix, chunkM = 2000, castShadow = false, maxDist = 0, makeColor = null, destr = null, lod = null) {
    const buckets = new Map();
    for (const it of items) {
      const key = `${Math.floor(it.x / chunkM)}_${Math.floor(it.z / chunkM)}`;
      let arr = buckets.get(key);
      if (!arr) buckets.set(key, (arr = []));
      arr.push(it);
    }
    const m = new THREE.Matrix4();
    const c = new THREE.Color();
    for (const [key, arr] of buckets) {
      const mesh = new THREE.InstancedMesh(geometry, material, arr.length);
      mesh.castShadow = castShadow;
      mesh.matrixAutoUpdate = false;
      arr.forEach((it, k) => {
        makeMatrix(m, it);
        mesh.setMatrixAt(k, m);
        if (makeColor) {
          makeColor(c, it);
          mesh.setColorAt(k, c);
        }
      });
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
      // instance-aware bounds (r180 API) → frustum culling works per chunk, so
      // props behind the camera cost nothing (distance culling still applies)
      mesh.computeBoundingSphere();
      const [kx, kz] = key.split('_').map(Number);
      // NOTE: keep this record's shape identical to every other chunkMeshes
      // push — the per-frame cull loop goes megamorphic (≈5× slower over 80k
      // records) if some records lack fields.
      const rec = { mesh, cx: kx * chunkM + chunkM / 2, cz: kz * chunkM + chunkM / 2, maxDist, lodD2: 0, cy: 0, lodMesh: null, hiOn: false, loOn: false };
      if (lod?.dist) {
        rec.lodD2 = lod.dist * lod.dist;
        // average instance height → the LOD switch uses real 3D distance
        const A = mesh.instanceMatrix.array;
        let cy = 0;
        for (let k = 0; k < arr.length; k++) cy += A[k * 16 + 13];
        rec.cy = cy / arr.length;
        if (lod.geo) {
          // imposter shares the SAME instanceMatrix attribute — one buffer,
          // and per-instance strips (collapsed buildings) hit both for free
          const lodMesh = new THREE.InstancedMesh(lod.geo, lod.mat, arr.length);
          lodMesh.instanceMatrix = mesh.instanceMatrix;
          if (mesh.instanceColor) lodMesh.instanceColor = mesh.instanceColor;
          lodMesh.castShadow = false;
          lodMesh.matrixAutoUpdate = false;
          lodMesh.computeBoundingSphere();
          rec.lodMesh = lodMesh;
        }
      }
      this.chunkMeshes.push(rec);
      // facade layers (windows, balconies…) get stripped per-instance when
      // their building collapses — remember them with the chunk cell radius
      if (this._tagFacadeProps) {
        this.propMeshes.push({
          mesh,
          cx: kx * chunkM + chunkM / 2,
          cz: kz * chunkM + chunkM / 2,
          r: chunkM * 0.72,
          // 'glass' layers (windows, shopfronts) additionally SHATTER: blasts
          // blow the panes out and leave a broken-window decal behind
          kind: this._propKind || 'prop',
        });
      }
      if (destr) this._registerInstDestr(mesh, arr, destr); // shootable layer
      // NOT added to the scene here — update() attaches it when in range
    }
  }

  // ---- baked imposters ------------------------------------------------------
  // Render a kit object's albedo to a tiny texture once, then draw far-away
  // instances as a single lit quad with that texture: the color read (dark
  // glass, light frame, concrete slab) stays identical at a fraction of the
  // triangles. This is what keeps the long draw distances affordable.

  _bakeViews(parts, views) {
    if (!this._bakeR) {
      this._bakeR = new THREE.WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true });
      this._bakeR.setClearColor(0x000000, 0);
      this._bakeR.outputColorSpace = THREE.SRGBColorSpace;
      this._bakeR.toneMapping = THREE.NoToneMapping;
    }
    const scene = new THREE.Scene();
    const box = new THREE.Box3();
    for (const { geo, mat } of parts) {
      // Basic materials = pure albedo capture; scene lighting is applied later
      // by the imposter quad's own Lambert material, like every neighbor mesh.
      const mats = (Array.isArray(mat) ? mat : [mat]).map((mm) => new THREE.MeshBasicMaterial({
        color: mm.color ? mm.color.clone() : 0xffffff,
        map: mm.map || null,
        side: THREE.DoubleSide,
        alphaTest: mm.alphaTest || 0,
        vertexColors: !!mm.vertexColors,
      }));
      scene.add(new THREE.Mesh(geo, Array.isArray(mat) ? mats : mats[0]));
      geo.computeBoundingBox();
      box.union(geo.boundingBox);
    }
    const size = box.getSize(new THREE.Vector3());
    const ctr = box.getCenter(new THREE.Vector3());
    const out = { box, size, ctr, tex: {} };
    for (const view of views) {
      const front = view === 'front';
      const cw = size.x;
      const ch = front ? size.y : size.z;
      const cam = new THREE.OrthographicCamera(-cw / 2, cw / 2, ch / 2, -ch / 2, 0.01, (front ? size.z : size.y) + 2);
      if (front) {
        cam.position.set(ctr.x, ctr.y, box.max.z + 1);
        cam.lookAt(ctr.x, ctr.y, box.min.z);
      } else {
        cam.position.set(ctr.x, box.max.y + 1, ctr.z);
        cam.up.set(0, 0, -1);
        cam.lookAt(ctr.x, box.min.y, ctr.z);
      }
      // ~8 px per metre, capped — these read from hundreds of metres away
      const px = Math.max(8, Math.min(128, Math.round(cw * 8)));
      const py = Math.max(8, Math.min(128, Math.round(ch * 8)));
      this._bakeR.setSize(px, py, false);
      this._bakeR.render(scene, cam);
      const c = document.createElement('canvas');
      c.width = px;
      c.height = py;
      c.getContext('2d').drawImage(this._bakeR.domElement, 0, 0);
      const tex = new THREE.CanvasTexture(c);
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.anisotropy = 4;
      out.tex[view] = tex;
    }
    return out;
  }

  // Wall-mounted facade kit (windows, balconies, ACs…): one front-baked quad
  // sitting at the object's front plane, facing out of the facade like the
  // real geometry does.
  _facadeImposter(geo, mat) {
    try {
      const b = this._bakeViews([{ geo, mat }], ['front']);
      const quad = new THREE.PlaneGeometry(b.size.x, b.size.y);
      quad.translate(b.ctr.x, b.ctr.y, b.box.max.z);
      return {
        geo: quad,
        mat: new THREE.MeshLambertMaterial({ map: b.tex.front, alphaTest: 0.35 }),
      };
    } catch (e) {
      console.warn('facade imposter bake failed:', e);
      return null;
    }
  }

  // Tree imposter: classic X-cross of two side-view quads plus a horizontal
  // canopy quad baked from above, so groves keep their read from the air too.
  _treeImposter(parts) {
    if (parts._imposter !== undefined) return parts._imposter;
    try {
      const b = this._bakeViews(parts, ['front', 'top']);
      const q1 = new THREE.PlaneGeometry(b.size.x, b.size.y);
      const q2 = q1.clone().rotateY(Math.PI / 2);
      for (const q of [q1, q2]) q.translate(b.ctr.x, b.ctr.y, b.ctr.z);
      const side = mergeGeometries([q1, q2]);
      const top = new THREE.PlaneGeometry(b.size.x, b.size.z);
      top.rotateX(-Math.PI / 2);
      top.translate(b.ctr.x, b.box.min.y + b.size.y * 0.62, b.ctr.z);
      const sideMat = new THREE.MeshLambertMaterial({ map: b.tex.front, alphaTest: 0.35, side: THREE.DoubleSide });
      const topMat = new THREE.MeshLambertMaterial({ map: b.tex.top, alphaTest: 0.35, side: THREE.DoubleSide });
      parts._imposter = { geo: mergeGeometries([side, top], true), mat: [sideMat, topMat] };
    } catch (e) {
      console.warn('tree imposter bake failed:', e);
      parts._imposter = null;
    }
    return parts._imposter;
  }

  // Close-up detail groves: the heavier hero chestnut (40k) dotted sparsely
  // through the woods and short-culled so the visible triangle count stays low
  // where the player is looking. Replaces the old 32 MB trees9.obj groves.
  async _buildGroves() {
    const g = this.grid;
    const rnd = (i, s) => fract(Math.sin(i * 45.3 + s * 88.1) * 3571.7);
    const hero = await this._loadTree('models/trees_new/chestnut_hero.glb', 13);
    if (!hero) return;

    // Subsample wooded cells so groves are spaced out; ±size variety per spot.
    const spots = [];
    const CAP = 1400; // heavy model — keep the total modest
    let placed = 0;
    for (let r = 0; r < g.h && placed < CAP; r++) {
      for (let c = 0; c < g.w && placed < CAP; c++) {
        if (g.classes[r * g.w + c] !== 1) continue;
        if (rnd(r * g.w + c, 1) > 0.35) continue;
        const x = g.minx + c * g.step + (rnd(placed, 2) - 0.5) * g.step;
        const z = g.minz + r * g.step + (rnd(placed, 3) - 0.5) * g.step;
        spots.push({ x, z, i: placed });
        placed++;
      }
    }
    this._instanceTrees(hero, spots, rnd, 250, 400);
  }

  // Scatter real Kenney rock/stone models through the wooded/green areas so the
  // forests have boulders and stones, not just trees. Chunked + distance-culled.
  async _buildRocks() {
    const g = this.grid;
    const rnd = (i, s) => fract(Math.sin(i * 78.233 + s * 12.9898) * 43758.5453);
    const loader = newGltfLoader();
    const N = 8;
    const models = await Promise.all(
      Array.from({ length: N }, (_, i) =>
        new Promise((res) => loader.load(`models/rocks/rock-${i}.glb`, (gl) => res(extractRock(gl.scene)), undefined, () => res(null)))
      )
    );
    const buckets = Array.from({ length: N }, () => []);
    let id = 0;
    const CAP = 40000;
    for (let r = 0; r < g.h && id < CAP; r++) {
      for (let c = 0; c < g.w && id < CAP; c++) {
        if (g.classes[r * g.w + c] !== 1) continue; // woods/green only
        if (rnd(r * g.w + c, 1) > 0.35) continue; // ~1 rock per 3 wooded cells
        const x = g.minx + c * g.step + (rnd(id, 2) - 0.5) * g.step;
        const z = g.minz + r * g.step + (rnd(id, 3) - 0.5) * g.step;
        buckets[id % N].push({ x, z, id });
        id++;
      }
    }
    const q = new THREE.Quaternion();
    const e = new THREE.Euler();
    const sv = new THREE.Vector3();
    for (let s = 0; s < N; s++) {
      const model = models[s];
      const list = buckets[s];
      if (!model || !list.length) continue;
      model.geometry.computeBoundingBox();
      const h0 = model.geometry.boundingBox.max.y - model.geometry.boundingBox.min.y || 1;
      const base = 3.2 / h0; // ~3 m boulders
      this._placeChunkedInstanced(
        model.geometry,
        model.material,
        list,
        (m, it) => {
          const sc = base * (0.5 + rnd(it.id, 4) * 1.3);
          e.set(0, rnd(it.id, 5) * Math.PI * 2, 0);
          q.setFromEuler(e);
          sv.set(sc, sc * (0.7 + rnd(it.id, 6) * 0.6), sc);
          m.compose(new THREE.Vector3(it.x, this.heightAt(it.x, it.z) - 0.2, it.z), q, sv);
        },
        1500,
        false,
        1200
      );
    }
  }

  // Real Poly Haven ground textures over the Tbilisi terrain: aerial grass on
  // parks/flat land, rock on the steep hillsides (Mtatsminda, the ridges).
  // Overlay quads, chunked per 1 km and distance-culled.
  _buildGrass() {
    const g = this.grid;
    // Concrete near roads is NOT painted here any more — 60 m grid cells made
    // the whole city grey. The paved strip flanking each sidewalk is a narrow
    // ribbon built in _buildRoads(); the open ground stays grass/rock.
    const load = (file, srgb) => {
      const t = new THREE.TextureLoader().load(`textures/${file}`);
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.anisotropy = 8;
      if (srgb) t.colorSpace = THREE.SRGBColorSpace;
      return t;
    };
    const grassMat = new THREE.MeshStandardMaterial({
      map: load('ground_grass.jpg', true),
      normalMap: load('ground_grass_nor.jpg'),
      roughness: 1.0,
      metalness: 0.0,
      envMapIntensity: 0.25, // keep the HDR from over-brightening the ground
      vertexColors: true, // green parks vs dry golden hills, like real Tbilisi
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -1,
    });
    // Real Tbilisi is semi-arid: lush green near water/parks, dry golden-brown
    // on the open hills. The grass photo is dark to begin with — tints darker
    // than this crush the texture detail into flat mud (8-bit output can't
    // hold the remaining contrast), which read as untextured "bare ground".
    const lush = new THREE.Color(0x8ab663).convertSRGBToLinear();
    const dry = new THREE.Color(0xd2bb79).convertSRGBToLinear();
    const dryness = (x, z) => {
      const n =
        Math.sin(x * 0.00042 + 1.3) * 0.5 +
        Math.sin(z * 0.00037 - 0.7) * 0.3 +
        Math.sin((x + z) * 0.00025) * 0.2;
      return THREE.MathUtils.clamp(0.5 + n * 0.6, 0, 1);
    };
    const rockMat = new THREE.MeshStandardMaterial({
      map: load('ground_rock.jpg', true),
      color: 0x9c8f7c, // warm rock, not bright grey
      roughness: 1.0,
      metalness: 0.0,
      envMapIntensity: 0.2,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -1,
    });
    const chunkM = 1000;
    const U = 0.28; // ~3.5 m per tile so grass detail reads at street level
    // key -> { grass:{pos,uv,idx}, rock:{...} }
    const buckets = new Map();
    const bkOf = (key, kind) => {
      let b = buckets.get(key);
      if (!b)
        buckets.set(key, (b = {
          grass: { pos: [], uv: [], idx: [], col: [] },
          rock: { pos: [], uv: [], idx: [] },
        }));
      return b[kind];
    };
    // The grass sheet is a SECOND ground surface, built straight off the 60 m
    // grid — so it happily laid a lawn across a 5 m deep tunnel cutting and
    // hid the whole trench. Cut cells are rebuilt here at the same 5 m as the
    // terrain under them.
    const refine = this._cutFlag ? this._refinedCells(g) : null;
    const SUB = 12;
    for (let r = 0; r < g.h - 1; r++) {
      for (let c = 0; c < g.w - 1; c++) {
        const cls = g.classes[r * g.w + c];
        if (cls === 2) continue; // water handled elsewhere
        const i = r * g.w + c;
        if (refine && refine.has(i)) {
          const x0 = g.minx + c * g.step;
          const z0 = g.minz + r * g.step;
          const bk = bkOf(`${Math.floor(x0 / chunkM)}_${Math.floor(z0 / chunkM)}`, 'grass');
          const d = cls === 1 ? dryness(x0, z0) * 0.45 : dryness(x0, z0);
          const cr = lush.r + (dry.r - lush.r) * d;
          const cg = lush.g + (dry.g - lush.g) * d;
          const cb = lush.b + (dry.b - lush.b) * d;
          const base = bk.pos.length / 3;
          for (let sj = 0; sj <= SUB; sj++) {
            for (let si = 0; si <= SUB; si++) {
              const px = x0 + (si / SUB) * g.step;
              const pz = z0 + (sj / SUB) * g.step;
              bk.pos.push(px, this.heightAt(px, pz) + 0.2, pz);
              bk.uv.push(px * U, pz * U);
              bk.col.push(cr, cg, cb);
            }
          }
          for (let sj = 0; sj < SUB; sj++) {
            for (let si = 0; si < SUB; si++) {
              const a = base + sj * (SUB + 1) + si;
              bk.idx.push(a, a + SUB + 1, a + 1, a + 1, a + SUB + 1, a + SUB + 2);
            }
          }
          continue;
        }
        const y00 = g.heights[i];
        const y10 = g.heights[i + 1];
        const y01 = g.heights[i + g.w];
        const y11 = g.heights[i + g.w + 1];
        const relief = Math.max(y00, y10, y01, y11) - Math.min(y00, y10, y01, y11);
        const avgY = (y00 + y10 + y01 + y11) / 4;
        // Rock only on genuinely steep, high mountainsides — not on the odd
        // bumpy cell near a building (which produced scattered grey squares).
        const steep = relief > g.step * 1.1 && avgY > 620;
        const kind = steep ? 'rock' : 'grass';
        const x = g.minx + c * g.step;
        const z = g.minz + r * g.step;
        const x2 = x + g.step;
        const z2 = z + g.step;
        const bk = bkOf(`${Math.floor(x / chunkM)}_${Math.floor(z / chunkM)}`, kind);
        const v0 = bk.pos.length / 3;
        bk.pos.push(x, y00 + 0.2, z, x2, y10 + 0.2, z, x2, y11 + 0.2, z2, x, y01 + 0.2, z2);
        const s = U;
        bk.uv.push(x * s, z * s, x2 * s, z * s, x2 * s, z2 * s, x * s, z2 * s);
        // split each cell along the SAME diagonal as the terrain mesh — the
        // opposite split diverges by meters mid-cell on steep ground, letting
        // the bare satellite terrain poke through as flat pale blobs
        bk.idx.push(v0, v0 + 3, v0 + 1, v0 + 1, v0 + 3, v0 + 2);
        if (kind === 'grass') {
          // parks (class 1) stay greener; open grass dries out with the noise
          for (const [px, pz] of [[x, z], [x2, z], [x2, z2], [x, z2]]) {
            const d = cls === 1 ? dryness(px, pz) * 0.45 : dryness(px, pz);
            // per-vertex hash on top: the dryness noise has a ~2.5 km
            // wavelength, so without this, courtyards are one flat colour
            const n2 = 0.88 + 0.24 * fract(Math.sin(px * 12.9898 + pz * 78.233) * 43758.5453);
            bk.col.push(
              (lush.r + (dry.r - lush.r) * d) * n2,
              (lush.g + (dry.g - lush.g) * d) * n2,
              (lush.b + (dry.b - lush.b) * d) * n2
            );
          }
        }
      }
    }
    for (const [key, b] of buckets) {
      const [kx, kz] = key.split('_').map(Number);
      // Wider cull than buildings so ground is always present before the city
      // fades in — avoids bare ground squares popping at the view edge.
      const center = { cx: kx * chunkM + chunkM / 2, cz: kz * chunkM + chunkM / 2, maxDist: 4500 };
      for (const [kind, mat] of [['grass', grassMat], ['rock', rockMat]]) {
        const bk = b[kind];
        if (!bk.pos.length) continue;
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(bk.pos), 3));
        geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(bk.uv), 2));
        if (kind === 'grass') geo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(bk.col), 3));
        geo.setIndex(new THREE.BufferAttribute(new Uint32Array(bk.idx), 1));
        geo.computeVertexNormals();
        const mesh = new THREE.Mesh(geo, mat);
        mesh.matrixAutoUpdate = false;
        this.chunkMeshes.push({ mesh, ...center, lodD2: 0, cy: 0, lodMesh: null, hiOn: false, loOn: false });
      }
    }
  }

  // Real CC0 Kenney highway signs at the start of major roads, turned to face
  // along the road. Instanced (one draw call).
  async _buildRoadSigns(roads) {
    const model = await new Promise((res) => {
      newGltfLoader().load('models/signs/sign-highway.glb', (g) => res(extractModel(g.scene)), undefined, () => res(null));
    });
    if (!model) return;
    model.geometry.computeBoundingBox();
    const bb = model.geometry.boundingBox;
    const scale = 5 / ((bb.max.y - bb.min.y) || 1); // ~5 m — smaller, less dominant

    const placements = [];
    for (const road of roads) {
      if (road[0] < 16) continue; // trunk/motorway only (not every city avenue)
      const n = (road.length - 1) / 2;
      if (n < 2) continue;
      // a sign near each end of the road, off to the roadside
      for (const [ai, bi] of [[0, 1], [n - 1, n - 2]]) {
        const ax = road[1 + ai * 2] / 10;
        const az = road[2 + ai * 2] / 10;
        const bx = road[1 + bi * 2] / 10;
        const bz = road[2 + bi * 2] / 10;
        const len = Math.hypot(bx - ax, bz - az);
        if (len < 8) continue;
        const dx = (bx - ax) / len;
        const dz = (bz - az) / len;
        const off = road[0] / 2 + 2;
        const x = ax - dz * off;
        const z = az + dx * off;
        placements.push({ x, y: this.heightAt(x, z), z, yaw: Math.atan2(dx, dz) });
      }
      if (placements.length > 4000) break; // bound the count
    }
    if (!placements.length) return;
    const mesh = new THREE.InstancedMesh(model.geometry, model.material, placements.length);
    mesh.castShadow = true;
    mesh.frustumCulled = false;
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const e = new THREE.Euler();
    const sv = new THREE.Vector3(scale, scale, scale);
    placements.forEach((p, k) => {
      e.set(0, p.yaw, 0);
      q.setFromEuler(e);
      m.compose(new THREE.Vector3(p.x, p.y, p.z), q, sv);
      mesh.setMatrixAt(k, m);
    });
    mesh.instanceMatrix.needsUpdate = true;
    this.group.add(mesh);
  }

  // Streetlights along the main avenues: a pole with a glowing lamp head.
  _buildStreetlights(roads) {
    const poles = [];
    const lamps = [];
    const rnd = (x) => fract(Math.sin(x * 12.9898) * 43758.5453);
    let acc = 0;
    const CAP = 14000; // hard cap — 184k poles was the main source of lag
    for (const road of roads) {
      if (poles.length >= CAP) break;
      if (road[0] < 13) continue; // primary/trunk/motorway only
      const n = (road.length - 1) / 2;
      const half = road[0] / 2 + 1.5;
      for (let i = 0; i < n - 1; i++) {
        const x1 = road[1 + i * 2] / 10;
        const z1 = road[2 + i * 2] / 10;
        const x2 = road[3 + i * 2] / 10;
        const z2 = road[4 + i * 2] / 10;
        const len = Math.hypot(x2 - x1, z2 - z1);
        if (len < 1) continue;
        const nx = -(z2 - z1) / len;
        const nz = (x2 - x1) / len;
        acc += len;
        while (acc > 70 && poles.length < CAP) {
          acc -= 70;
          const t = 1 - acc / len;
          const side = rnd(x1 + i) > 0.5 ? 1 : -1;
          const x = x1 + (x2 - x1) * t + nx * half * side;
          const z = z1 + (z2 - z1) * t + nz * half * side;
          const y = this.heightAt(x, z);
          poles.push({ x, y, z, dx: -nx * side, dz: -nz * side });
          lamps.push({ x: x + -nx * side * 1.6, y: y + 8, z: z + -nz * side * 1.6 });
        }
      }
    }
    if (!poles.length) return;
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const one = new THREE.Vector3(1, 1, 1);
    // pole
    const poleGeo = new THREE.CylinderGeometry(0.16, 0.22, 8.5, 6);
    poleGeo.translate(0, 4.25, 0);
    const poleMesh = new THREE.InstancedMesh(poleGeo, new THREE.MeshLambertMaterial({ color: 0x3a3d42 }), poles.length);
    poleMesh.castShadow = true;
    // arm + lamp head (glowing)
    const lampGeo = new THREE.BoxGeometry(1.1, 0.5, 0.7);
    const lampMesh = new THREE.InstancedMesh(
      lampGeo,
      new THREE.MeshStandardMaterial({ color: 0xfff2c4, emissive: 0xffd070, emissiveIntensity: 1.4 }),
      lamps.length
    );
    poles.forEach((p, k) => {
      m.compose(new THREE.Vector3(p.x, p.y, p.z), q, one);
      poleMesh.setMatrixAt(k, m);
    });
    lamps.forEach((l, k) => {
      m.compose(new THREE.Vector3(l.x, l.y, l.z), q, one);
      lampMesh.setMatrixAt(k, m);
    });
    poleMesh.instanceMatrix.needsUpdate = true;
    lampMesh.instanceMatrix.needsUpdate = true;
    this.group.add(poleMesh);
    this.group.add(lampMesh);
  }
}

// ---- helpers ---------------------------------------------------------------

const fract = (x) => x - Math.floor(x);
// rAF freezes in a hidden tab — fall back to a MessageChannel macrotask
// (immune to background timer throttling) so the city keeps building at
// full speed instead of stalling the loader forever.
const nextFrame = () =>
  new Promise((r) => {
    if (document.hidden) {
      const ch = new MessageChannel();
      ch.port1.onmessage = () => r();
      ch.port2.postMessage(0);
    } else requestAnimationFrame(r);
  });

function base64ToBytes(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
