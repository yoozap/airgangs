import * as THREE from 'three';

// Real Tbilisi road infrastructure, from tools/qgis_roads.py (QGIS/GDAL over
// the OSM planet extract).  The country-wide lists in city.json.gz stay as they
// are; inside the Tbilisi box they are REPLACED by this network, which carries
// what the old 12-entry width table threw away:
//
//   w      real carriageway width (width= tag, else lanes x lane width)
//   kind   0 road · 1 bridge · 2 tunnel · 3 building passage
//   layer  the OSM layer tag — who crosses over whom at an interchange
//   lanes  drives the painted markings
//
// The old pipeline guessed all of this, which is why a 21 m avenue was drawn
// 13 m wide, why courtyard archways grew concrete portals, and why a flyover
// had to be invented by looking for "some road that crosses a tunnel".

export const KIND = { ROAD: 0, BRIDGE: 1, TUNNEL: 2, PASSAGE: 3 };

export async function fetchRoadNet(url) {
  const res = await fetch(url);
  if (!res.ok) return null;
  const buf = await res.arrayBuffer();
  const bytes = new Uint8Array(buf);
  let text;
  if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
    const ds = new DecompressionStream('gzip');
    text = await new Response(new Blob([buf]).stream().pipeThrough(ds)).text();
  } else {
    text = new TextDecoder().decode(bytes);
  }
  return JSON.parse(text);
}

// A road entry in the legacy city.json format: [width_m, x_dm, z_dm, …].
function legacy(entry) {
  const out = [entry[0] / 10];
  for (let i = 7; i < entry.length; i++) out.push(entry[i]);
  return out;
}

// Everything the renderer needs to know about one way, keyed by its index in
// the merged road list (so kerbs, trees and lights keep working off the same
// array they always did).
export function mergeRoadNet(city, net) {
  const [s, w, n, e] = net.meta.bbox;
  const { origin, mlat, mlon } = net.meta;
  const xW = -(e - origin.lon) * mlon; // east lon -> smallest x (X is west)
  const xE = -(w - origin.lon) * mlon;
  const zS = (s - origin.lat) * mlat;
  const zN = (n - origin.lat) * mlat;
  // ogr2ogr -spat selects every way that INTERSECTS the box, so the legacy
  // list has to drop exactly those: any vertex inside = the network owns it.
  // Testing the midpoint instead double-draws every way that straddles the
  // edge, at two different widths, which shows as a fringed seam.
  const inCity = (entry, stride) => {
    for (let i = stride; i < entry.length; i += 2) {
      const x = entry[i] / 10;
      const z = entry[i + 1] / 10;
      if (x > xW && x < xE && z > zS && z < zN) return true;
    }
    return false;
  };

  const roads = (city.roads ?? []).filter((r) => !inCity(r, 1));
  const bridges = (city.bridges ?? []).filter((r) => !inCity(r, 1));
  const tunnels = (city.tunnels ?? []).filter((r) => !inCity(r, 1));
  const meta = new Map(); // index in `roads` -> the real attributes
  const bridgeMeta = new Map();
  const tunnelMeta = new Map();

  for (const entry of net.roads) {
    const kind = entry[1];
    const leg = legacy(entry);
    const rec = {
      w: entry[0] / 10, kind, layer: entry[2], cls: entry[3],
      lanes: entry[4], oneway: !!entry[5], roundabout: !!entry[6],
    };
    if (kind === KIND.BRIDGE) {
      bridgeMeta.set(bridges.length, rec);
      bridges.push(leg);
      continue;
    }
    if (kind === KIND.TUNNEL) {
      tunnelMeta.set(tunnels.length, rec);
      tunnels.push(leg);
      continue;
    }
    // at-grade roads and building passages both drape on the terrain
    meta.set(roads.length, rec);
    roads.push(leg);
  }
  return { roads, bridges, tunnels, meta, bridgeMeta, tunnelMeta,
           junctions: net.junctions ?? [] };
}

// ---------------------------------------------------------------------------
// Junction aprons: at a real node where 3+ streets meet, the tarmac is one
// continuous plate. Drawn as ribbons the streets notch into each other and the
// grass shows through the gaps — the thing that made every Tbilisi crossroads
// read as painted stripes on a lawn.
export function buildJunctions(citymap, junctions) {
  if (!junctions?.length) return;
  const pos = [];
  const uv = [];
  const idx = [];
  const SEG = 10;
  let n = 0;
  for (const [xd, zd, rd, layer] of junctions) {
    if (layer !== 0) continue; // an elevated node is paved by its own deck
    const x = xd / 10;
    const z = zd / 10;
    const r = Math.min(26, rd / 10);
    if (r < 3.5) continue;
    const y = citymap.heightAt(x, z) + 0.78; // just under the road ribbon
    const c = pos.length / 3;
    pos.push(x, y, z);
    // WORLD-space UVs at the ribbons' 9 m metric — a radial 0..1 disc UV put
    // one texture repeat across a 26 m apron next to a 9 m-repeat street,
    // which is exactly the "visible road transition" seam
    uv.push(x / 9, z / 9);
    for (let i = 0; i <= SEG; i++) {
      const a = (i / SEG) * Math.PI * 2;
      const px = x + Math.cos(a) * r;
      const pz = z + Math.sin(a) * r;
      pos.push(px, citymap.heightAt(px, pz) + 0.78, pz);
      uv.push(px / 9, pz / 9);
    }
    for (let i = 0; i < SEG; i++) idx.push(c, c + 1 + i, c + 2 + i);
    n++;
  }
  if (!pos.length) return;
  // the EXACT road material (same colour, same maps) with a weaker offset so
  // the ribbon still wins where they overlap — any tint difference between
  // apron and ribbon draws the apron's outline on every crossroads
  const mat = citymap.roadMat ? citymap.roadMat.clone() : new THREE.MeshLambertMaterial({
    map: citymap.asphaltTex,
    side: THREE.DoubleSide,
  });
  mat.polygonOffset = true;
  mat.polygonOffsetFactor = -1.5;
  mat.polygonOffsetUnits = -1.5;
  citymap._addChunkedSurface(pos, uv, idx, mat, { collidable: false });
  return n;
}

// ---------------------------------------------------------------------------
// SPANS — real bridge structure.
//
// OSM tags a bridge but never its height, so the old builder laid the deck at
// ground + 0.35 m and dropped a solid wall from the deck edge to the ground on
// both sides. Over a road that reads as a white wedge with a hole punched in
// it, which is exactly what a flyover is not.
//
// What the data DOES say is `layer`: a flyover is layer 1 and the street it
// crosses is layer 0. So the deck profile is solved from the crossings — it
// must clear every lower-layer road it passes over by the real gauge (5.5 m)
// and the river by 4 m — then eased back to grade at the abutments at a
// driveable gradient. Below the deck goes an actual structure: edge fascia,
// a girder box, pier columns on caps, and abutment walls at the ends.

const CLEAR_ROAD = 5.6; // m of headroom a Georgian road bridge gives a street
const CLEAR_WATER = 4.0;
const MAX_GRADE = 0.055; // approach ramps: 5.5 %
const GIRDER = 1.6; // structural depth under the deck slab
const FASCIA = 0.75; // edge beam depth

// Uniform grid over every at-grade road segment, for the crossing test.
function segmentIndex(roads, cell = 60) {
  const g = new Map();
  for (const road of roads) {
    const n = (road.length - 1) / 2;
    for (let i = 0; i < n - 1; i++) {
      const x1 = road[1 + i * 2] / 10, z1 = road[2 + i * 2] / 10;
      const x2 = road[3 + i * 2] / 10, z2 = road[4 + i * 2] / 10;
      const seg = [x1, z1, x2, z2, road[0]];
      const cx0 = Math.floor(Math.min(x1, x2) / cell), cx1 = Math.floor(Math.max(x1, x2) / cell);
      const cz0 = Math.floor(Math.min(z1, z2) / cell), cz1 = Math.floor(Math.max(z1, z2) / cell);
      if ((cx1 - cx0) * (cz1 - cz0) > 400) continue; // absurdly long segment
      for (let cx = cx0; cx <= cx1; cx++) {
        for (let cz = cz0; cz <= cz1; cz++) {
          const k = cx * 100003 + cz;
          let b = g.get(k);
          if (!b) g.set(k, (b = []));
          b.push(seg);
        }
      }
    }
  }
  return { g, cell };
}

function crossesAt(index, x1, z1, x2, z2) {
  const { g, cell } = index;
  const out = [];
  const cx0 = Math.floor(Math.min(x1, x2) / cell), cx1 = Math.floor(Math.max(x1, x2) / cell);
  const cz0 = Math.floor(Math.min(z1, z2) / cell), cz1 = Math.floor(Math.max(z1, z2) / cell);
  for (let cx = cx0; cx <= cx1; cx++) {
    for (let cz = cz0; cz <= cz1; cz++) {
      const b = g.get(cx * 100003 + cz);
      if (!b) continue;
      for (const [ax, az, bx, bz, w] of b) {
        const r1 = x2 - x1, r2 = z2 - z1, s1 = bx - ax, s2 = bz - az;
        const den = r1 * s2 - r2 * s1;
        if (Math.abs(den) < 1e-9) continue;
        const t = ((ax - x1) * s2 - (az - z1) * s1) / den;
        const u = ((ax - x1) * r2 - (az - z1) * r1) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) continue;
        out.push({ t, w });
      }
    }
  }
  return out;
}

export function buildSpans(citymap, bridges, bridgeMeta, roads, zones) {
  const index = segmentIndex(roads);
  const deck = { pos: [], uv: [], idx: [] }; // asphalt carriageway
  const conc = { pos: [], uv: [], idx: [] }; // structure
  const quad = (buf, a, b, c, d, uvs) => {
    const v = buf.pos.length / 3;
    buf.pos.push(...a, ...b, ...c, ...d);
    buf.idx.push(v, v + 1, v + 2, v, v + 2, v + 3);
    if (uvs) buf.uv.push(...uvs);
  };
  // a box column between two heights, given centre + half sizes along/across
  const column = (x, z, y0, y1, dx, dz, hx, hz) => {
    const c = [
      [x + dx * hx - dz * hz, z + dz * hx + dx * hz],
      [x + dx * hx + dz * hz, z + dz * hx - dx * hz],
      [x - dx * hx + dz * hz, z - dz * hx - dx * hz],
      [x - dx * hx - dz * hz, z - dz * hx + dx * hz],
    ];
    for (let i = 0; i < 4; i++) {
      const [ax, az] = c[i];
      const [bx, bz] = c[(i + 1) % 4];
      const wd = Math.hypot(bx - ax, bz - az);
      quad(conc, [ax, y0, az], [bx, y0, bz], [bx, y1, bz], [ax, y1, az],
        [0, y0 / 3, wd / 3, y0 / 3, wd / 3, y1 / 3, 0, y1 / 3]);
    }
    // cap the top so a column never shows a hollow end from the air
    quad(conc, [c[0][0], y1, c[0][1]], [c[1][0], y1, c[1][1]],
      [c[2][0], y1, c[2][1]], [c[3][0], y1, c[3][1]], [0, 0, 1, 0, 1, 1, 0, 1]);
  };

  let nStruct = 0;
  let nPier = 0;
  for (let bi = 0; bi < bridges.length; bi++) {
    const br = bridges[bi];
    const n = (br.length - 1) / 2;
    if (n < 2) continue;
    const rm = bridgeMeta?.get(bi);
    const hw = br[0] / 2 + 0.9; // structure is wider than the carriageway
    const pts = [];
    for (let i = 0; i < n; i++) pts.push([br[1 + i * 2] / 10, br[2 + i * 2] / 10]);
    const cum = [0];
    for (let i = 1; i < n; i++) {
      cum.push(cum[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
    }
    const total = cum[n - 1];
    if (total < 6) continue;
    const [mx, mz] = pts[n >> 1];
    const zone = zones.find((zn) => (mx - zn.x) ** 2 + (mz - zn.z) ** 2 < zn.r2) ?? null;
    const bespoke = zone !== null;

    // --- deck profile ----------------------------------------------------
    // Abutments sit on the ground the approach road arrives at; every
    // crossing the span passes OVER pushes the deck up by the clearance
    // gauge, and the ramp between eases at a driveable gradient.
    const ground = (x, z) => citymap.heightAt(x, z);
    // +0.8 is the height the road ribbons are laid at, so an at-grade bridge
    // way (most of the country list: culverts, causeways) joins the street
    // flush instead of stepping 45 cm down onto a raised plate.
    const yA = ground(pts[0][0], pts[0][1]) + 0.8;
    const yB = ground(pts[n - 1][0], pts[n - 1][1]) + 0.8;
    const cons = []; // {s, y} the deck must reach
    const layer = rm?.layer ?? 0;
    for (let i = 0; i < n - 1; i++) {
      const [x1, z1] = pts[i];
      const [x2, z2] = pts[i + 1];
      const segLen = cum[i + 1] - cum[i];
      if (segLen < 0.5) continue;
      // only a span that OSM says is above the street below has to clear it
      if (layer <= 0) continue;
      for (const c of crossesAt(index, x1, z1, x2, z2)) {
        const s = cum[i] + segLen * c.t;
        if (s < 4 || s > total - 4) continue; // that is the abutment, not a crossing
        const x = x1 + (x2 - x1) * c.t;
        const z = z1 + (z2 - z1) * c.t;
        cons.push({ s, y: ground(x, z) + CLEAR_ROAD });
      }
    }
    const profile = (s) => {
      let y = yA + (yB - yA) * (s / total);
      for (const c of cons) y = Math.max(y, c.y - Math.abs(s - c.s) * MAX_GRADE);
      return y;
    };

    // --- deck slab + structure ------------------------------------------
    const STEP = 6;
    let pierAcc = 1e9; // force a pier at the first eligible station
    for (let i = 0; i < n - 1; i++) {
      const [x1, z1] = pts[i];
      const [x2, z2] = pts[i + 1];
      const segLen = cum[i + 1] - cum[i];
      if (segLen < 0.01) continue;
      const dx = (x2 - x1) / segLen;
      const dz = (z2 - z1) / segLen;
      const px = -dz;
      const pz = dx;
      const steps = Math.max(1, Math.ceil(segLen / STEP));
      for (let s = 0; s < steps; s++) {
        const s0 = cum[i] + (segLen * s) / steps;
        const s1 = cum[i] + (segLen * (s + 1)) / steps;
        const mk = (sv) => {
          const t = (sv - cum[i]) / segLen;
          const x = x1 + (x2 - x1) * t;
          const z = z1 + (z2 - z1) * t;
          const gy = ground(x, z);
          const yGen = Math.max(profile(sv), gy + 0.8);
          if (!zone) return { x, z, gy, y: yGen };
          // On a real bridge the carriageway must ride the REAL deck line —
          // laid at ground level it disappeared under the structure and the
          // bridge read as fascia + footways with no roadway. 12 cm below
          // the footway datum = a real kerb reveal; blended to the generic
          // profile over the last 15 m so the approach street joins flush.
          const tz = THREE.MathUtils.clamp(
            ((x - zone.x0) * (zone.x1 - zone.x0) + (z - zone.z0) * (zone.z1 - zone.z0)) /
              (zone.L * zone.L), 0, 1);
          const yDeck = Math.max(
            zone.yA + (zone.yB - zone.yA) * tz, gy + 0.35) - 0.12;
          const blend = Math.min(1, Math.min(sv, total - sv) / 15);
          return { x, z, gy, y: yGen + (Math.max(yDeck, gy + 0.8) - yGen) * blend };
        };
        const A = mk(s0);
        const B = mk(s1);
        // carriageway
        quad(deck,
          [A.x - px * hw, A.y, A.z - pz * hw], [A.x + px * hw, A.y, A.z + pz * hw],
          [B.x + px * hw, B.y, B.z + pz * hw], [B.x - px * hw, B.y, B.z - pz * hw],
          [0, s0 / 9, (2 * hw) / 9, s0 / 9, (2 * hw) / 9, s1 / 9, 0, s1 / 9]);
        if (bespoke) continue; // src/bridges.js owns these spans

        const clearA = A.y - A.gy;
        const clearB = B.y - B.gy;
        const high = Math.max(clearA, clearB) > 1.2;
        // A parapet only belongs where there is something to fall off. Built
        // on every bridge way it walled both sides of every at-grade culvert
        // in Georgia — long white kerbs standing in open grass.
        if (!high) continue;
        for (const sg of [-1, 1]) {
          const ex = px * hw * sg;
          const ez = pz * hw * sg;
          // parapet — the barrier a driver sees
          quad(conc,
            [A.x + ex, A.y, A.z + ez], [B.x + ex, B.y, B.z + ez],
            [B.x + ex, B.y + 1.05, B.z + ez], [A.x + ex, A.y + 1.05, A.z + ez],
            [s0 / 3, 0, s1 / 3, 0, s1 / 3, 0.35, s0 / 3, 0.35]);
          // edge fascia beam: the deck has real thickness seen from below
          quad(conc,
            [A.x + ex, A.y - FASCIA, A.z + ez], [B.x + ex, B.y - FASCIA, B.z + ez],
            [B.x + ex, B.y, B.z + ez], [A.x + ex, A.y, A.z + ez],
            [s0 / 3, 0, s1 / 3, 0, s1 / 3, 0.25, s0 / 3, 0.25]);
        }
        nStruct++;
        // girder box under the slab, inset from the fascia
        const gi = hw * 0.62;
        for (const sg of [-1, 1]) {
          const ex = px * gi * sg;
          const ez = pz * gi * sg;
          quad(conc,
            [A.x + ex, A.y - FASCIA - GIRDER, A.z + ez], [B.x + ex, B.y - FASCIA - GIRDER, B.z + ez],
            [B.x + ex, B.y - FASCIA, B.z + ez], [A.x + ex, A.y - FASCIA, A.z + ez],
            [s0 / 3, 0, s1 / 3, 0, s1 / 3, 0.5, s0 / 3, 0.5]);
        }
        // soffit — the underside you see from the street below
        quad(conc,
          [A.x - px * gi, A.y - FASCIA - GIRDER, A.z - pz * gi],
          [A.x + px * gi, A.y - FASCIA - GIRDER, A.z + pz * gi],
          [B.x + px * gi, B.y - FASCIA - GIRDER, B.z + pz * gi],
          [B.x - px * gi, B.y - FASCIA - GIRDER, B.z - pz * gi],
          [0, s0 / 3, (2 * gi) / 3, s0 / 3, (2 * gi) / 3, s1 / 3, 0, s1 / 3]);
        // piers on a cap beam, every ~28 m of elevated deck
        pierAcc += segLen / steps;
        if (pierAcc > 28 && clearA > 3) {
          pierAcc = 0;
          nPier++;
          const yTop = A.y - FASCIA - GIRDER;
          // cap beam across the deck width
          column(A.x, A.z, yTop - 0.9, yTop, dx, dz, 1.1, hw * 0.72);
          // two columns down to the ground
          for (const sg of [-1, 1]) {
            const cx = A.x + px * hw * 0.45 * sg;
            const cz = A.z + pz * hw * 0.45 * sg;
            const gy = ground(cx, cz);
            if (yTop - 0.9 - gy < 1.5) continue;
            column(cx, cz, gy - 1.5, yTop - 0.9, dx, dz, 0.85, 0.85);
          }
        }
      }
    }
    if (bespoke) continue;
    // --- abutments: the wall that retains the embankment at each end -----
    for (const [ei, si] of [[0, 1], [n - 1, n - 2]]) {
      const [ax, az] = pts[ei];
      const [bx, bz] = pts[si];
      const l = Math.hypot(bx - ax, bz - az) || 1;
      const dx = (bx - ax) / l;
      const dz = (bz - az) / l;
      const s = ei === 0 ? 0 : total;
      const y = Math.max(profile(s), ground(ax, az) + 0.35);
      const gy = ground(ax, az);
      if (y - gy < 1.6) continue;
      // set the wall a little INTO the span so it reads as the bridge seat
      column(ax + dx * 1.2, az + dz * 1.2, gy - 2, y - 0.25, dx, dz, 1.2, hw);
    }
  }

  const tl = new THREE.TextureLoader();
  const tex = (f) => {
    const t = tl.load(f);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 8;
    return t;
  };
  const mk = (buf, mat, collide) => {
    if (!buf.idx.length) return null;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(buf.pos), 3));
    g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(buf.uv), 2));
    g.setIndex(new THREE.BufferAttribute(new Uint32Array(buf.idx), 1));
    g.computeVertexNormals();
    const m = new THREE.Mesh(g, mat);
    m.castShadow = true;
    m.receiveShadow = true;
    m.matrixAutoUpdate = false;
    citymap.group.add(m);
    if (collide) {
      g.computeBoundsTree();
      citymap.collidables.push(m);
    }
    return m;
  };
  const deckMat = citymap.roadMat ? citymap.roadMat.clone() : new THREE.MeshLambertMaterial({
    map: citymap.asphaltTex, color: 0xb9bcc0, side: THREE.DoubleSide });
  deckMat.polygonOffset = false;
  const deckMesh = mk(deck, deckMat, true);
  // decks are the one surface the ground probe must IGNORE from below —
  // tagged so future tweaks can tell structure from carriageway
  if (deckMesh) deckMesh.userData.deck = true;
  // data maps (normal/roughness) stay linear — tex() would tag them sRGB
  const dtex = (f) => {
    const t = tl.load(f);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.anisotropy = 8;
    return t;
  };
  mk(conc, new THREE.MeshStandardMaterial({
    map: tex('textures/concrete.jpg'),
    normalMap: dtex('textures/concrete_nor.jpg'),
    roughnessMap: dtex('textures/concrete_rough.jpg'),
    roughness: 1, metalness: 0, envMapIntensity: 0.35,
    color: 0xbdb9b1, side: THREE.DoubleSide }), true);
  console.log(`spans: ${bridges.length} bridge ways, ${nStruct} elevated bays, ${nPier} piers`);
}
