import * as THREE from 'three';

// Real Mtkvari bridges, built to their OSM man_made=bridge footprints
// (tools/cache/bridges_tbilisi.json → oriented-bbox axis endpoints).
// kind: 'girder' = deep straight fascia beams (Baratashvili-style box girder),
// 'arch' = concrete fascia scalloped by arched spans, 'stone' = the same
// scallop clad in tuff ashlar (Dry Bridge). The generic bridge pass keeps
// laying the asphalt deck (and its collision); this pass replaces its plain
// parapets/piers with the real structure, so parapets/piers/pylons are
// suppressed inside these zones.
export const REAL_BRIDGES = [
  { name: 'Metekhi', p0: [41.690128, 44.809558], p1: [41.69057, 44.810505], w: 26.6, kind: 'arch', spans: 2 },
  { name: 'Baratashvili', p0: [41.696794, 44.807473], p1: [41.69694, 44.809062], w: 26.1, kind: 'girder', spans: 3 },
  { name: 'Dochanashvili (Saarbrücken)', p0: [41.701564, 44.803616], p1: [41.702442, 44.804471], w: 27.7, kind: 'girder', spans: 3 },
  { name: 'Galaktion Tabidze', p0: [41.707459, 44.791928], p1: [41.708058, 44.793437], w: 26.4, kind: 'arch', spans: 3 },
  { name: 'Queen Tamar', p0: [41.71662, 44.786657], p1: [41.717168, 44.788037], w: 26.5, kind: 'arch', spans: 3 },
  { name: 'Vakhushti Bagrationi', p0: [41.725022, 44.780619], p1: [41.725886, 44.7831], w: 28.9, kind: 'girder', spans: 5 },
  { name: 'Shota Shalikashvili', p0: [41.753542, 44.778274], p1: [41.752734, 44.776837], w: 27.9, kind: 'girder', spans: 3 },
  { name: 'Giorgi Mindeli', p0: [41.788937, 44.786868], p1: [41.783844, 44.785908], w: 49.6, kind: 'girder', spans: 12 },
  { name: 'Samasi Aragveli', p0: [41.682441, 44.826539], p1: [41.683579, 44.826753], w: 21.5, kind: 'girder', spans: 3 },
  { name: 'Dimitri Gulia', p0: [41.676268, 44.836318], p1: [41.676424, 44.837394], w: 26.6, kind: 'girder', spans: 2 },
  { name: 'Dry Bridge', p0: [41.700336, 44.802427], p1: [41.700609, 44.802694], w: 14.3, kind: 'stone', spans: 3 },
];

// Suppression zones for the generic pass, in local map coordinates. Each
// zone also carries the REAL deck line (the same bank-anchored profile
// buildRealBridges draws), so the generic pass can lay its collidable
// asphalt ON the real bridge instead of at ground level underneath it —
// which is why some bridges had structure but no visible roadway.
export function realBridgeZones(citymap) {
  const meta = citymap.meta;
  return REAL_BRIDGES.map((b) => {
    const x0 = -(b.p0[1] - meta.origin.lon) * meta.mlon;
    const z0 = (b.p0[0] - meta.origin.lat) * meta.mlat;
    const x1 = -(b.p1[1] - meta.origin.lon) * meta.mlon;
    const z1 = (b.p1[0] - meta.origin.lat) * meta.mlat;
    const L = Math.hypot(x1 - x0, z1 - z0) || 1;
    const dx = (x1 - x0) / L;
    const dz = (z1 - z0) / L;
    // identical anchoring to buildRealBridges: bank height just beyond the span
    const bank = (x, z, sx, sz) => Math.max(
      citymap.heightAt(x, z),
      citymap.heightAt(x + sx * 14, z + sz * 14),
      citymap.heightAt(x + sx * 26, z + sz * 26)
    );
    const yA = bank(x0, z0, -dx, -dz) + 0.9;
    const yB = bank(x1, z1, dx, dz) + 0.9;
    const r = L / 2 + 25;
    return { x: (x0 + x1) / 2, z: (z0 + z1) / 2, r2: r * r, x0, z0, x1, z1, L, yA, yB };
  });
}

// Build the real structures. citymap provides meta/heightAt/group.
export function buildRealBridges(citymap) {
  const { meta } = citymap;
  // Kit pieces per bridge, filled below and placed by src/infra.js once the
  // Blender kit is loaded. Modelled from photographs of the real bridges:
  // Metekhi/Baratashvili carry an ornate cast-iron balustrade with candelabra
  // lamp columns; Dry Bridge a plain steel rail; the stone arch bridges
  // (Queen Tamar, Galaktioni) a dark rail over a dentilled fascia.
  citymap._bridgeRails = [];
  citymap._bridgeLamps = [];
  citymap._bridgeFascia = [];
  const conc = { pos: [], uv: [], idx: [] };
  const stone = { pos: [], uv: [], idx: [] };
  const rail = { pos: [], idx: [] };
  const stain = { pos: [], idx: [] }; // dark waterline band around the piers
  const quad = (buf, a, b, c, d, uvs) => {
    const v0 = buf.pos.length / 3;
    buf.pos.push(...a, ...b, ...c, ...d);
    buf.idx.push(v0, v0 + 1, v0 + 2, v0, v0 + 2, v0 + 3);
    if (buf.uv && uvs) buf.uv.push(...uvs);
  };

  const ORNATE = new Set(['Metekhi', 'Baratashvili', 'Dochanashvili (Saarbrücken)',
    'Galaktion Tabidze', 'Queen Tamar']);
  for (const br of REAL_BRIDGES) {
    const x0 = -(br.p0[1] - meta.origin.lon) * meta.mlon;
    const z0 = (br.p0[0] - meta.origin.lat) * meta.mlat;
    const x1 = -(br.p1[1] - meta.origin.lon) * meta.mlon;
    const z1 = (br.p1[0] - meta.origin.lat) * meta.mlat;
    const L = Math.hypot(x1 - x0, z1 - z0);
    if (L < 20) continue;
    const dx = (x1 - x0) / L;
    const dz = (z1 - z0) / L;
    const half = br.w / 2 + 0.85;
    const buf = br.kind === 'stone' ? stone : conc;
    // anchor deck heights on the banks just beyond the span (the footprint
    // endpoints can sit on a low quay or over the water itself)
    const bank = (x, z, sx, sz) => Math.max(
      citymap.heightAt(x, z),
      citymap.heightAt(x + sx * 14, z + sz * 14),
      citymap.heightAt(x + sx * 26, z + sz * 26)
    );
    const yA = bank(x0, z0, -dx, -dz) + 0.9;
    const yB = bank(x1, z1, dx, dz) + 0.9;
    const at = (t) => {
      const x = x0 + (x1 - x0) * t;
      const z = z0 + (z1 - z0) * t;
      const yDeck = Math.max(yA + (yB - yA) * t, citymap.heightAt(x, z) + 0.35);
      return [x, z, yDeck];
    };
    // FULL-WIDTH DECK SLAB. The asphalt ribbon comes from the OSM road, whose
    // width is the carriageway only — on a 26 m bridge that left a bare strip
    // of sky between the tarmac and the parapet on both sides. Real Tbilisi
    // bridges carry a wide footway there, so pave the whole deck just under
    // the road surface: the asphalt covers the middle, this shows as pavement
    // either side.
    {
      const nx = -dz;
      const nz = dx;
      const STEP = 6;
      for (let t = 0; t < L; t += STEP) {
        const t2 = Math.min(L, t + STEP);
        const p1 = at(t / L);
        const p2 = at(t2 / L);
        const y1 = p1[2] - 0.06;
        const y2 = p2[2] - 0.06;
        // Only the OUTER strips: a full-width slab buried the asphalt ribbon
        // and turned the bridge into a blank white plate. The carriageway is
        // roughly the middle 62 % of a Tbilisi road bridge; the rest is footway.
        const inner = half * 0.62;
        for (const sgn of [1, -1]) {
          quad(conc,
            [p1[0] + nx * half * sgn, y1, p1[1] + nz * half * sgn],
            [p2[0] + nx * half * sgn, y2, p2[1] + nz * half * sgn],
            [p2[0] + nx * inner * sgn, y2, p2[1] + nz * inner * sgn],
            [p1[0] + nx * inner * sgn, y1, p1[1] + nz * inner * sgn],
            [t / 6, 0, t2 / 6, 0, t2 / 6, (half - inner) / 6, t / 6, (half - inner) / 6]);
        }
      }
    }
    // Sample both deck edges for the kit: real balustrade + lamp columns
    // instead of the flat extruded rail strip.
    {
      const nx = -dz;
      const nz = dx;
      const railName = ORNATE.has(br.name) ? 'bal_iron'
        : br.kind === 'stone' ? 'bal_steel' : 'bal_steel';
      const ang = Math.atan2(dx, dz) + Math.PI / 2; // pieces run along X
      for (let t = 1.5; t < L - 1.5; t += 3) {
        const p = at(t / L);
        for (const side of [1, -1]) {
          citymap._bridgeRails.push({
            name: railName,
            x: p[0] + nx * half * side,
            z: p[1] + nz * half * side,
            y: p[2],
            ang: ang + (side > 0 ? 0 : Math.PI),
          });
          if (ORNATE.has(br.name)) {
            citymap._bridgeFascia.push({
              x: p[0] + nx * (half + 0.15) * side,
              z: p[1] + nz * (half + 0.15) * side,
              y: p[2],
              ang: ang + (side > 0 ? 0 : Math.PI),
            });
          }
        }
      }
      if (ORNATE.has(br.name)) {
        const nLamp = Math.max(2, Math.round(L / 34));
        for (let k = 0; k <= nLamp; k++) {
          const p = at(k / nLamp);
          for (const side of [1, -1]) {
            citymap._bridgeLamps.push({
              x: p[0] + nx * (half - 0.5) * side,
              z: p[1] + nz * (half - 0.5) * side,
              y: p[2],
              ang: ang,
            });
          }
        }
      }
    }
    const spanLen = L / br.spans;
    const archDepth = br.kind === 'girder' ? 0 : Math.min(9, spanLen / 5);
    const baseDepth = br.kind === 'girder' ? 3.4 : 2.2;
    const railH = br.kind === 'stone' ? 0 : 1.1;

    // fascia walls along both deck edges; bottom edge scalloped by the arches
    const steps = Math.max(8, Math.ceil(L / 4));
    for (const s of [-1, 1]) {
      const ox = -dz * half * s;
      const oz = dx * half * s;
      for (let i = 0; i < steps; i++) {
        const t0 = i / steps;
        const t1 = (i + 1) / steps;
        const mk = (t) => {
          const [x, z, yDeck] = at(t);
          const u = ((t * L) % spanLen) / spanLen;
          const dip = archDepth * (1 - Math.sin(Math.PI * u));
          const g = citymap.heightAt(x + ox, z + oz);
          const yBot = Math.max(yDeck - baseDepth - dip, g + 0.2, yDeck - 14);
          return [x + ox, z + oz, yDeck + 1.2, yBot];
        };
        const [ax, az, aTop, aBot] = mk(t0);
        const [bx, bz, bTop, bBot] = mk(t1);
        quad(buf, [ax, aBot, az], [ax, aTop, az], [bx, bTop, bz], [bx, bBot, bz],
          [t0 * L / 3, aBot / 3, t0 * L / 3, aTop / 3, t1 * L / 3, bTop / 3, t1 * L / 3, bBot / 3]);
      }
      // railing / stone parapet cap on top of the fascia
      for (let i = 0; i < steps; i++) {
        const t0 = i / steps;
        const t1 = (i + 1) / steps;
        const [ax, az, ay] = at(t0);
        const [bx, bz, by] = at(t1);
        if (railH) {
          quad(rail, [ax + ox, ay + 1.2, az + oz], [ax + ox, ay + 1.2 + railH, az + oz],
            [bx + ox, by + 1.2 + railH, bz + oz], [bx + ox, by + 1.2, bz + oz]);
        } else {
          // stone parapet: low thick wall (both faces)
          for (const f of [-0.35, 0.35]) {
            quad(stone, [ax + ox - dz * f, ay + 1.1, az + oz + dx * f], [ax + ox - dz * f, ay + 2.1, az + oz + dx * f],
              [bx + ox - dz * f, by + 2.1, bz + oz + dx * f], [bx + ox - dz * f, by + 1.1, bz + oz + dx * f],
              [t0 * L / 3, 0, t0 * L / 3, 0.33, t1 * L / 3, 0.33, t1 * L / 3, 0]);
          }
        }
      }
    }

    // Real UNDERSIDE between the two fascias: an arch bridge gets a curved
    // barrel vault (the scallop used to be a silhouette cut into a flat
    // wall — from the water there was no vault at all), a girder bridge a
    // flat box-girder soffit. Collidable: flying through the arch is a real
    // fly-through, and the soffit overhead is a ceiling, not a facade.
    {
      const nx = -dz;
      const nz = dx;
      const aSteps = Math.max(12, Math.ceil(L / 3));
      for (let i = 0; i < aSteps; i++) {
        const t0 = i / aSteps;
        const t1 = (i + 1) / aSteps;
        const mkV = (t) => {
          const [x, z, yDeck] = at(t);
          const u = ((t * L) % spanLen) / spanLen;
          const dip = archDepth * (1 - Math.sin(Math.PI * u));
          return [x, z, Math.max(yDeck - baseDepth - dip, yDeck - 14)];
        };
        const [ax, az, ay] = mkV(t0);
        const [bx, bz, by] = mkV(t1);
        quad(buf,
          [ax - nx * half, ay, az - nz * half],
          [ax + nx * half, ay, az + nz * half],
          [bx + nx * half, by, bz + nz * half],
          [bx - nx * half, by, bz - nz * half],
          [0, t0 * L / 3, (2 * half) / 3, t0 * L / 3,
            (2 * half) / 3, t1 * L / 3, 0, t1 * L / 3]);
      }
    }

    // piers with rounded cutwaters at the span boundaries
    for (let k = 1; k < br.spans; k++) {
      const t = k / br.spans;
      const [x, z, yDeck] = at(t);
      const gy = citymap.heightAt(x, z);
      if (yDeck - gy < 2.5) continue;
      const pw = br.w / 2 - 0.5;
      const th = 1.4;
      // wall across the deck width (two faces + ends)
      const corners = [
        [x - dz * -pw - dx * th, z + dx * -pw - dz * th],
        [x - dz * -pw + dx * th, z + dx * -pw + dz * th],
        [x - dz * pw + dx * th, z + dx * pw + dz * th],
        [x - dz * pw - dx * th, z + dx * pw - dz * th],
      ];
      const yTop = yDeck - 1.2;
      const yBot = gy - 2;
      for (let e = 0; e < 4; e++) {
        const [cx1, cz1] = corners[e];
        const [cx2, cz2] = corners[(e + 1) % 4];
        const w2 = Math.hypot(cx2 - cx1, cz2 - cz1);
        quad(buf, [cx1, yBot, cz1], [cx1, yTop, cz1], [cx2, yTop, cz2], [cx2, yBot, cz2],
          [0, yBot / 3, 0, yTop / 3, w2 / 3, yTop / 3, w2 / 3, yBot / 3]);
      }
      // waterline staining: the dark wet band every river pier really has —
      // concrete that reads as untouched by the water it stands in is a big
      // part of why the bridges looked like renderings
      {
        const sc = corners.map(([cx, cz]) => [x + (cx - x) * 1.03, z + (cz - z) * 1.03]);
        const sy0 = gy + 1.3;
        const sy1 = gy + 3.4;
        for (let e = 0; e < 4; e++) {
          const [cx1, cz1] = sc[e];
          const [cx2, cz2] = sc[(e + 1) % 4];
          quad(stain, [cx1, sy0, cz1], [cx1, sy1, cz1], [cx2, sy1, cz2], [cx2, sy0, cz2]);
        }
      }
      // cutwater prisms at both wall ends (approximated as 4-sided cones)
      for (const s of [-1, 1]) {
        const nx = x - dz * (pw + 1.2) * s;
        const nz = z + dx * (pw + 1.2) * s;
        quad(buf, [nx - th, yBot, nz], [nx - th, yTop, nz], [nx, yTop, nz + th], [nx, yBot, nz + th],
          [0, yBot / 3, 0, yTop / 3, 1, yTop / 3, 1, yBot / 3]);
        quad(buf, [nx, yBot, nz + th], [nx, yTop, nz + th], [nx + th, yTop, nz], [nx + th, yBot, nz],
          [0, yBot / 3, 0, yTop / 3, 1, yTop / 3, 1, yBot / 3]);
        quad(buf, [nx + th, yBot, nz], [nx + th, yTop, nz], [nx, yTop, nz - th], [nx, yBot, nz - th],
          [0, yBot / 3, 0, yTop / 3, 1, yTop / 3, 1, yBot / 3]);
        quad(buf, [nx, yBot, nz - th], [nx, yTop, nz - th], [nx - th, yTop, nz], [nx - th, yBot, nz],
          [0, yBot / 3, 0, yTop / 3, 1, yTop / 3, 1, yBot / 3]);
      }
    }
  }

  const meshes = [];
  const mkMesh = (buf, mat, collide) => {
    if (!buf.idx.length) return;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(buf.pos), 3));
    if (buf.uv) g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(buf.uv), 2));
    g.setIndex(new THREE.BufferAttribute(new Uint32Array(buf.idx), 1));
    g.computeVertexNormals();
    const m = new THREE.Mesh(g, mat);
    m.castShadow = true;
    m.receiveShadow = true;
    m.matrixAutoUpdate = false;
    citymap.group.add(m);
    // Piers, fascia and footways are solid structure: you can land on the
    // footway, and flying into a pier is a real hit — but flying UNDER the
    // deck is free air (game.js treats overhead soffits as ceilings now).
    if (collide) {
      g.computeBoundsTree();
      citymap._addCollidable ? citymap._addCollidable(m) : citymap.collidables.push(m);
    }
    meshes.push(m);
  };
  const tl = new THREE.TextureLoader();
  const tex = (file) => {
    const t = tl.load(file);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 4;
    return t;
  };
  const dtex = (file) => { // linear data maps — never sRGB
    const t = tl.load(file);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.anisotropy = 4;
    return t;
  };
  mkMesh(conc, new THREE.MeshStandardMaterial({
    map: tex('textures/concrete.jpg'),
    normalMap: dtex('textures/concrete_nor.jpg'),
    roughnessMap: dtex('textures/concrete_rough.jpg'),
    roughness: 1, metalness: 0, envMapIntensity: 0.35,
    color: 0xc8c4bb, side: THREE.DoubleSide,
  }), true);
  mkMesh(stone, new THREE.MeshStandardMaterial({
    map: tex('textures/facades/church_stone.jpg'),
    roughness: 0.9, metalness: 0, envMapIntensity: 0.35,
    color: 0xe7ddc8, side: THREE.DoubleSide,
  }), true);
  mkMesh(rail, new THREE.MeshStandardMaterial({
    color: 0x8f979e, roughness: 0.45, metalness: 0.7, side: THREE.DoubleSide }));
  mkMesh(stain, new THREE.MeshLambertMaterial({
    color: 0x413c33, transparent: true, opacity: 0.5,
    polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
    side: THREE.DoubleSide }));
  return meshes;
}
