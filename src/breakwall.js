import * as THREE from 'three';

// LOCAL destruction: a warhead takes a BITE out of the wall it hits — a hole
// through a flat facade, a corner sheared clean off if you hit the corner —
// instead of the whole building merely getting shorter and sootier.
//
// The city's buildings are extruded prisms: ONE quad per facade, base ring to
// roofline. There is no geometry near an impact to break, so the first time a
// building takes a warhead we swap its walls for a TESSELLATED SHELL: same
// footprint, same wall material, same UVs — but cut into ~2 m cells. Damage
// is then just "drop the cells inside the blast sphere", which is what makes
// the hole follow the real shape of the hit: flat wall, corner, roofline.
//
// Behind the shell sits a dark LINING (the gutted interior) and a stack of
// FLOOR SLABS. The lining loses a smaller disc than the outer wall, so a hole
// reads with real wall thickness at its rim and you look through it into a
// black, slab-layered interior instead of straight out the far side.

const CELL_W = 1.5; // m — target facade cell width (finer = raggeder holes)
const CELL_H = 1.6; // m — target facade cell height
const MAX_CELLS = 7000; // per building, so a 100 m block can't explode the count
const LINING_IN = 0.42; // m — wall thickness the lining sits behind the facade
const SLAB_STEP = 3.2; // m — floor-to-floor

// Build the tessellated replacement for one building's walls.
// rec.orig must hold the PRISTINE {p, c, u} snapshot of its chunk vertices:
// ring0 (base, n verts), ring1 (roofline, n), roof ring (n) [+ ridge verts].
export function buildShell(rec, wallMat, linMat) {
  const n = Math.floor((rec.v1 - rec.v0) / 3);
  if (n < 3) return null;
  const P = rec.orig.p;
  const C = rec.orig.c; // Uint8 0-255, straight off the chunk attribute
  const U = rec.orig.u;
  const base = rec.base;
  const top = rec.top;
  const h = Math.max(2, top - base);

  // how fine can we cut this building without blowing the triangle budget?
  let perim = 0;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    perim += Math.hypot(P[j * 3] - P[i * 3], P[j * 3 + 2] - P[i * 3 + 2]);
  }
  const want = (perim / CELL_W) * (h / CELL_H);
  const grow = want > MAX_CELLS ? Math.sqrt(want / MAX_CELLS) : 1;
  const cw = CELL_W * grow;
  const chh = CELL_H * grow;

  const pos = [];
  const col = [];
  const uv = [];
  const cells = []; // { x, y, z, v } — center + first vertex of the quad
  const lPos = [];
  const lCol = [];
  const lCells = [];

  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    const x0 = P[i * 3];
    const z0 = P[i * 3 + 2];
    const x1 = P[j * 3];
    const z1 = P[j * 3 + 2];
    const len = Math.hypot(x1 - x0, z1 - z0);
    if (len < 0.05) continue;
    // wall UVs run along the perimeter: u = meters / 3, cumulative. The
    // closing edge wraps back to vertex 0, whose u is the START of the run.
    const u0 = U[i * 2];
    const u1 = j === 0 ? u0 + len / 3 : U[j * 2];
    const vB = U[i * 2 + 1];
    const vT = U[(n + i) * 2 + 1];
    // inward normal (toward the centroid) for the lining offset
    let nx = -(z1 - z0) / len;
    let nz = (x1 - x0) / len;
    const mx = (x0 + x1) / 2;
    const mz = (z0 + z1) / 2;
    if ((mx - rec.cx) * nx + (mz - rec.cz) * nz > 0) {
      nx = -nx;
      nz = -nz;
    }
    const cols = Math.max(1, Math.min(28, Math.round(len / cw)));
    const rows = Math.max(1, Math.min(28, Math.round(h / chh)));
    const v0 = pos.length / 3;
    const lv0 = lPos.length / 3;
    for (let r = 0; r <= rows; r++) {
      const fy = r / rows;
      const y = base + h * fy;
      for (let c = 0; c <= cols; c++) {
        const fx = c / cols;
        const x = x0 + (x1 - x0) * fx;
        const z = z0 + (z1 - z0) * fx;
        pos.push(x, y, z);
        uv.push(u0 + (u1 - u0) * fx, vB + (vT - vB) * fy);
        // vertical color ramp from the grimy base ring to the wall color
        for (let k = 0; k < 3; k++) {
          const a = C[i * 3 + k] / 255;
          const b = C[(n + i) * 3 + k] / 255;
          col.push(a + (b - a) * fy);
        }
        // lining: same grid pushed inside, charred concrete. Baked dark
        // (the material is unlit) with a touch of variation so the interior
        // never reads as one flat black card.
        lPos.push(x + nx * LINING_IN, y, z + nz * LINING_IN);
        const d = 0.006 + 0.005 * (((c * 7 + r * 13) % 5) / 5);
        lCol.push(d, d * 0.95, d * 0.88);
      }
    }
    const stride = cols + 1;
    for (let r = 0; r < rows; r++) {
      for (let c = 0; c < cols; c++) {
        const a = v0 + r * stride + c;
        const fx = (c + 0.5) / cols;
        const fy = (r + 0.5) / rows;
        cells.push({
          x: x0 + (x1 - x0) * fx,
          y: base + h * fy,
          z: z0 + (z1 - z0) * fx,
          // per-cell bite factor: the blast sphere eats a ragged edge instead
          // of a stencil-clean circle. Deterministic, so a reloaded hole from
          // the damage DB comes back with exactly the same torn outline.
          k: 0.72 + 0.5 * (((r * 7919 + c * 104729 + i * 1299721) % 97) / 97),
          a,
          b: a + 1,
          c: a + stride + 1,
          d: a + stride,
        });
        const la = lv0 + r * stride + c;
        lCells.push({
          x: x0 + (x1 - x0) * fx + nx * LINING_IN,
          y: base + h * fy,
          z: z0 + (z1 - z0) * fx + nz * LINING_IN,
          k: 0.8 + 0.4 * (((r * 6151 + c * 24593 + i * 393241) % 89) / 89),
          a: la,
          b: la + 1,
          c: la + stride + 1,
          d: la + stride,
        });
      }
    }
  }
  if (!cells.length) return null;

  // ---- floor slabs: what you actually see through a blasted-open wall ----
  const contour = [];
  for (let i = 0; i < n; i++) {
    // pull the slab edge in so it never pokes through the facade
    const dx = P[i * 3] - rec.cx;
    const dz = P[i * 3 + 2] - rec.cz;
    const l = Math.hypot(dx, dz) || 1;
    const s = Math.max(0, l - LINING_IN - 0.15) / l;
    contour.push(new THREE.Vector2(rec.cx + dx * s, rec.cz + dz * s));
  }
  let tris = [];
  try {
    tris = THREE.ShapeUtils.triangulateShape(contour, []);
  } catch {
    tris = [];
  }
  if (!tris.length) for (let i = 1; i < n - 1; i++) tris.push([0, i, i + 1]);
  const slabTop = top - 0.6;
  let floor = 0;
  for (let y = base + SLAB_STEP; y < slabTop; y += SLAB_STEP) {
    const s0 = lPos.length / 3;
    // slabs a shade lighter than the walls: that contrast is what makes the
    // interior read as FLOORS stacked inside a room, not as a black void
    const g = 0.02 + 0.006 * (floor++ % 3);
    for (const p of contour) {
      lPos.push(p.x, y, p.y);
      lCol.push(g, g * 0.97, g * 0.92);
    }
    for (const t of tris) lCells.push({ slab: [s0 + t[0], s0 + t[1], s0 + t[2]] });
  }

  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
  geo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(col), 3));
  geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
  geo.setIndex(new THREE.BufferAttribute(new Uint32Array(cells.length * 6), 1));
  const mesh = new THREE.Mesh(geo, wallMat);
  mesh.castShadow = true;
  mesh.receiveShadow = true;
  mesh.matrixAutoUpdate = false;
  mesh.frustumCulled = false;

  const lGeo = new THREE.BufferGeometry();
  lGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(lPos), 3));
  lGeo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(lCol), 3));
  // 6 index slots per entry, slabs included, so carve() can address cell k at
  // a fixed offset and simply degenerate what the blast took
  lGeo.setIndex(new THREE.BufferAttribute(new Uint32Array(lCells.length * 6), 1));
  const lining = new THREE.Mesh(lGeo, linMat);
  lining.matrixAutoUpdate = false;
  lining.frustumCulled = false;
  lining.receiveShadow = true;

  const shell = {
    mesh,
    lining,
    cells,
    lCells,
    orig: {
      p: new Float32Array(pos),
      c: new Float32Array(col),
      lp: new Float32Array(lPos),
    },
    count: pos.length / 3,
    lCount: lPos.length / 3,
    holes: [],
  };
  carve(shell); // no holes yet — just fills both index buffers
  return shell;
}

// Rewrite both index buffers, dropping every cell inside a blast sphere.
// The lining loses a smaller disc than the facade, so the rim of the hole
// shows real wall thickness and the interior stays enclosed.
export function carve(shell, holes = shell.holes) {
  // Removed cells are DEGENERATED in place (all three indices equal), never
  // compacted: the BVH indexes this very array, and compacting would leave
  // every raycast hitting the wrong triangle. A degenerate triangle can't be
  // hit and refit() shrinks its bounds to a point.
  const idx = shell.mesh.geometry.index.array;
  let live = 0;
  for (let k = 0; k < shell.cells.length; k++) {
    const c = shell.cells[k];
    const o = k * 6;
    let gone = false;
    for (const hl of holes) {
      const dx = c.x - hl.x;
      const dy = c.y - hl.y;
      const dz = c.z - hl.z;
      const rr = hl.r * c.k;
      if (dx * dx + dy * dy + dz * dz < rr * rr) { gone = true; break; }
    }
    if (gone) {
      idx.fill(c.a, o, o + 6);
      continue;
    }
    live++;
    idx[o] = c.a; idx[o + 1] = c.b; idx[o + 2] = c.c;
    idx[o + 3] = c.a; idx[o + 4] = c.c; idx[o + 5] = c.d;
  }
  shell.mesh.geometry.index.needsUpdate = true;

  const lIdx = shell.lining.geometry.index.array;
  for (let k = 0; k < shell.lCells.length; k++) {
    const c = shell.lCells[k];
    const o = k * 6;
    if (c.slab) {
      lIdx[o] = c.slab[0]; lIdx[o + 1] = c.slab[1]; lIdx[o + 2] = c.slab[2];
      lIdx.fill(c.slab[0], o + 3, o + 6);
      continue;
    }
    let gone = false;
    for (const hl of holes) {
      const rr = hl.r * 0.62 * c.k; // the inner skin loses less than the facade
      const dx = c.x - hl.x;
      const dy = c.y - hl.y;
      const dz = c.z - hl.z;
      if (dx * dx + dy * dy + dz * dz < rr * rr) { gone = true; break; }
    }
    if (gone) {
      lIdx.fill(c.a, o, o + 6);
      continue;
    }
    lIdx[o] = c.a; lIdx[o + 1] = c.b; lIdx[o + 2] = c.c;
    lIdx[o + 3] = c.a; lIdx[o + 4] = c.c; lIdx[o + 5] = c.d;
  }
  shell.lining.geometry.index.needsUpdate = true;
  shell.live = live;
}

// Same damage math the intact shells use (see CityMap._writeBuildingVerts):
// kh squashes height, jit buckles, dark soots, lean shears the whole thing.
export function writeShellVerts(rec, kh, jit, dark, lean, phase = 0) {
  const s = rec.shell;
  if (!s) return;
  const hgt = Math.max(1, rec.top - rec.base);
  const fr = (v) => {
    const q = Math.sin(v) * 43758.5453;
    return q - Math.floor(q);
  };
  const posA = s.mesh.geometry.attributes.position;
  const colA = s.mesh.geometry.attributes.color;
  for (let v = 0; v < s.count; v++) {
    const o = v * 3;
    const relY = Math.max(0, s.orig.p[o + 1] - rec.base);
    const t = Math.min(1, relY / hgt);
    const a = jit * (0.35 + 0.65 * t);
    const sh = relY * lean;
    posA.array[o] = s.orig.p[o] + (fr(v * 12.9898 + phase * 7.31) - 0.5) * a + sh * rec.leanX;
    posA.array[o + 1] = rec.base + relY * kh + (fr(v * 78.233 + phase * 3.17) - 0.5) * a * 0.5;
    posA.array[o + 2] = s.orig.p[o + 2] + (fr(v * 39.425 + phase * 5.53) - 0.5) * a + sh * rec.leanZ;
    colA.array[o] = s.orig.c[o] * dark;
    colA.array[o + 1] = s.orig.c[o + 1] * dark;
    colA.array[o + 2] = s.orig.c[o + 2] * dark;
  }
  posA.needsUpdate = true;
  colA.needsUpdate = true;
  const lp = s.lining.geometry.attributes.position;
  for (let v = 0; v < s.lCount; v++) {
    const o = v * 3;
    const relY = Math.max(0, s.orig.lp[o + 1] - rec.base);
    const sh = relY * lean;
    lp.array[o] = s.orig.lp[o] + sh * rec.leanX;
    lp.array[o + 1] = rec.base + relY * kh;
    lp.array[o + 2] = s.orig.lp[o + 2] + sh * rec.leanZ;
  }
  lp.needsUpdate = true;
}
