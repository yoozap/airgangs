import * as THREE from 'three';

// FIND THE PROPELLER.
//
// src/game.js has spun `plane.userData.propellers` since the first version and
// src/planes.js fills that list by looking for meshes named "propeller*". Of
// the sixteen propeller-driven aircraft in the game, THREE were ever named
// that way: the rest came off Sketchfab as one merged mesh per material called
// "Object_7", so the list came back empty and sixteen aeroplanes flew around
// Tbilisi with the blades nailed solid to the nose.
//
// Renaming does not help, because on most of these the blades are not a
// separate object at all -- they are a few hundred triangles inside a mesh
// that also holds the engine, the spinner and half the fuselage. So the
// geometry is SPLIT OUT here, at load time, on the cached model, once per
// aircraft type. Doing it in the browser rather than rewriting the GLBs means
// the shipped assets are never touched: nothing can come back with its
// textures re-encoded or its normals re-split, and a plane added tomorrow gets
// a turning propeller without anyone running a tool.
//
// WHAT A PROPELLER LOOKS LIKE, geometrically, measured along the aeroplane's
// own axes (f = where the nose points, r = starboard, u = up):
//   - THIN along f. A blade is 10-20 cm of pitch in a 3 m disc.
//   - RADIALLY LONG, and roughly as tall as it is wide: it sweeps a circle.
//   - it comes in a SET of 2 to 6 shells sharing one hub and one station along
//     f -- which is the part that actually separates a propeller from a flap,
//     an aileron or an undercarriage door, all of which are also thin flat
//     pieces hanging off an aeroplane.
//   - NOTHING IS IN FRONT OF IT. A propeller is the first thing the air meets
//     (or, on a pusher like the XB-35, the last). A flap always has a wing in
//     front of it. This is the test that does the real work.

// `?props=debug` in the URL prints why each candidate disc was turned down --
// the table of near-misses is the only way to tune this on a new model.
const DEBUG = typeof location !== 'undefined' && /props=debug/.test(location.search);

const WELD = 0.002;       // m — vertices closer than this are the same point
const MIN_TRIS = 6;       // a shell smaller than this is a screw head

// ---- union-find over welded vertices ---------------------------------------
function makeDSU(n) {
  const p = new Int32Array(n);
  for (let i = 0; i < n; i++) p[i] = i;
  const find = (x) => {
    while (p[x] !== x) { p[x] = p[p[x]]; x = p[x]; }
    return x;
  };
  return {
    find,
    union(a, b) {
      a = find(a); b = find(b);
      if (a !== b) p[a] = b;
    },
  };
}

// Every mesh that could hold propeller geometry, with its triangles already
// expressed in the MODEL's frame — the shells of one propeller are routinely
// spread across two meshes (blades in one material, spinner in another), so
// they all have to be measured in the same space to be clustered together.
function gatherMeshes(root) {
  const out = [];
  root.updateMatrixWorld(true);
  const inv = new THREE.Matrix4().copy(root.matrixWorld).invert();
  root.traverse((o) => {
    if (!o.isMesh || o.isSkinnedMesh) return;      // a rigged prop turns on its bone
    if (o.userData.interior) return;               // the cockpit lining is not a blade
    const g = o.geometry;
    if (!g?.attributes?.position) return;
    const toModel = new THREE.Matrix4().multiplyMatrices(inv, o.matrixWorld);
    out.push(makeEntry(o, g, toModel));
  });
  return out;
}

// One mesh, measured ONCE. `tri` is a snapshot of the original triangle list:
// cutting the first propeller out of a mesh rewrites that mesh's index, and on
// a four-engine aeroplane the second, third and fourth cuts would then be
// reading triangle numbers that no longer mean what they meant -- which is
// exactly how the XB-35's flying wing ended up with NaN vertices in it. Every
// cut reads this snapshot; the mesh itself is rebuilt once, at the end.
function makeEntry(mesh, geo, toModel) {
  const idx = geo.index;
  const n = (idx ? idx.count : geo.attributes.position.count) / 3;
  const tri = new Uint32Array(n * 3);
  for (let t = 0; t < n * 3; t++) tri[t] = idx ? idx.getX(t) : t;
  return { mesh, geo, toModel, tri, taken: new Set() };
}

// ---- the shells -------------------------------------------------------------
// One connected piece of surface: its triangles, and its extents along the
// aeroplane's axes.
function shellsOf(entry, basis) {
  const { geo, toModel } = entry;
  const pos = geo.attributes.position;
  const idx = geo.index;
  const triCount = entry.tri.length / 3;
  if (triCount < MIN_TRIS) return [];

  // Weld by position: these models come through Draco with their vertices
  // split along every UV seam, so without this a single blade arrives as
  // thirty unrelated slivers and nothing ever forms a shell. (A P-51 went
  // from 17861 "loose parts" to 173 real ones once welded.)
  const key = new Map();
  const weld = new Int32Array(pos.count);
  const v = new THREE.Vector3();
  const pts = [];                                 // model-space position per weld id
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i).applyMatrix4(toModel);
    const k = `${Math.round(v.x / WELD)},${Math.round(v.y / WELD)},${Math.round(v.z / WELD)}`;
    let id = key.get(k);
    if (id === undefined) {
      id = pts.length;
      key.set(k, id);
      pts.push(v.clone());
    }
    weld[i] = id;
  }
  const dsu = makeDSU(pts.length);
  const at = (t, j) => entry.tri[t * 3 + j];
  for (let t = 0; t < triCount; t++) {
    const a = weld[at(t, 0)], b = weld[at(t, 1)], c = weld[at(t, 2)];
    dsu.union(a, b);
    dsu.union(b, c);
  }

  const groups = new Map();
  for (let t = 0; t < triCount; t++) {
    const root = dsu.find(weld[at(t, 0)]);
    let g = groups.get(root);
    if (!g) groups.set(root, (g = []));
    g.push(t);
  }

  const { f, r, u } = basis;
  const out = [];
  for (const tris of groups.values()) {
    if (tris.length < MIN_TRIS) continue;
    let fmin = Infinity, fmax = -Infinity, rmin = Infinity, rmax = -Infinity,
      umin = Infinity, umax = -Infinity;
    for (const t of tris) {
      for (let j = 0; j < 3; j++) {
        const p = pts[weld[at(t, j)]];
        const pf = p.dot(f), pr = p.dot(r), pu = p.dot(u);
        if (pf < fmin) fmin = pf; if (pf > fmax) fmax = pf;
        if (pr < rmin) rmin = pr; if (pr > rmax) rmax = pr;
        if (pu < umin) umin = pu; if (pu > umax) umax = pu;
      }
    }
    out.push({
      entry, tris,
      fmin, fmax, rmin, rmax, umin, umax,
      df: fmax - fmin, dr: rmax - rmin, du: umax - umin,
      cf: (fmin + fmax) / 2, cr: (rmin + rmax) / 2, cu: (umin + umax) / 2,
    });
  }
  return out;
}

// ---- clustering -------------------------------------------------------------
// Blades of one propeller sit at the same station along the nose axis and
// within a disc of each other. Two shells join when both are true.
function clusterBlades(blades) {
  const dsu = makeDSU(blades.length);
  for (let i = 0; i < blades.length; i++) {
    for (let j = i + 1; j < blades.length; j++) {
      const a = blades[i], b = blades[j];
      const reach = Math.max(a.dr, a.du, b.dr, b.du);
      // same station: the pitch of a blade set spreads them a little along f,
      // never more than the hub is deep
      if (Math.abs(a.cf - b.cf) > Math.max(0.5, reach * 0.45)) continue;
      const d = Math.hypot(a.cr - b.cr, a.cu - b.cu);
      if (d > reach * 1.35) continue;
      dsu.union(i, j);
    }
  }
  const by = new Map();
  blades.forEach((b, i) => {
    const k = dsu.find(i);
    let g = by.get(k);
    if (!g) by.set(k, (g = []));
    g.push(b);
  });
  return [...by.values()].map((set) => {
    const box = {
      fmin: Math.min(...set.map((s) => s.fmin)), fmax: Math.max(...set.map((s) => s.fmax)),
      rmin: Math.min(...set.map((s) => s.rmin)), rmax: Math.max(...set.map((s) => s.rmax)),
      umin: Math.min(...set.map((s) => s.umin)), umax: Math.max(...set.map((s) => s.umax)),
    };
    return {
      set,
      ...box,
      df: box.fmax - box.fmin, dr: box.rmax - box.rmin, du: box.umax - box.umin,
      cf: (box.fmin + box.fmax) / 2, cr: (box.rmin + box.rmax) / 2,
      cu: (box.umin + box.umax) / 2,
      tris: set.reduce((n, s) => n + s.tris.length, 0),
    };
  });
}

// IS ANYTHING IN FRONT OF IT? Sweep every triangle centroid in the model and
// count what lies inside the cluster's own disc, ahead of it and behind it. A
// propeller has an empty half: a tractor has nothing ahead, a pusher nothing
// behind. A flap has a whole wing in front of it and fails.
function clearance(cl, centroids, span) {
  const rad = Math.max(cl.dr, cl.du) / 2;
  let ahead = 0, behind = 0;
  // Ignore the hub's own immediate neighbourhood: the spinner and the engine
  // front are inches away from the blades by construction.
  const skin = Math.max(0.25, rad * 0.2);
  for (let i = 0; i < centroids.length; i += 3) {
    const pr = centroids[i], pu = centroids[i + 1], pf = centroids[i + 2];
    if (Math.hypot(pr - cl.cr, pu - cl.cu) > rad * 0.85) continue;
    if (pf > cl.fmax + skin) ahead++;
    else if (pf < cl.fmin - skin) behind++;
  }
  return { ahead, behind };
}

function scoreCluster(cl, span, centroids, want = 1, why = null) {
  const D = Math.max(cl.dr, cl.du);
  // How big a propeller is RELATIVE TO THE WINGSPAN depends on how many of
  // them there are. A single-engine fighter's airscrew is a third of its span;
  // a Lancaster's is an eighth and an XB-35's a eleventh, because the span
  // grew and the propeller did not. One fixed lower bound threw away every
  // four-engine aeroplane in the game.
  const lo = want > 1 ? 0.055 : 0.10;
  if (D < span * lo || D > span * 0.60) {
    why?.push(`size ${D.toFixed(1)}m = ${(D / span).toFixed(3)} of span`);
    return null;
  }
  // A disc, not a stick: a propeller sweeps as tall as it is wide. A single
  // blade frozen vertical would fail this, which is correct — one blade is not
  // a propeller, and a model that ships one is not one we can spin honestly.
  const circ = Math.min(cl.dr, cl.du) / D;
  if (circ < 0.45) { why?.push(`not round: ${circ.toFixed(2)}`); return null; }
  // Thin along the nose axis.
  if (cl.df > D * 0.5) {
    why?.push(`too deep: ${cl.df.toFixed(2)}m in a ${D.toFixed(1)}m disc`);
    return null;
  }
  const { ahead, behind } = clearance(cl, centroids, span);
  const total = centroids.length / 3;
  const open = Math.min(ahead, behind) / Math.max(1, total);
  // One side has to be essentially empty. 0.4% of the model's triangles inside
  // the disc on the clear side is the slack for an aerial wire or a gun barrel
  // poking through, which several of these models do have.
  if (open > 0.004) {
    why?.push(`not clear: ${ahead} ahead / ${behind} behind of ${total}`);
    return null;
  }
  const pusher = behind < ahead;
  return {
    ...cl, pusher,
    score: circ * 2 + (1 - cl.df / (D * 0.5)) + (cl.set.length >= 2 ? 0.6 : 0),
  };
}

// ---- WHERE THE HUB ACTUALLY IS ----------------------------------------------
//
// The pivot used to be the cluster's bounding-box centre, and a propeller is
// the one shape that is not centred on its own box. Three blades at rest sit
// at 90/210/330 degrees: the box runs -0.87R..+0.87R across and -0.5R..+R up,
// so its centre is a QUARTER OF THE RADIUS above the hub and the blades orbit
// that point instead of turning on it. The user saw it at once --
// "rotation of propeller is not from center".
//
// The hub falls straight out of what a propeller IS: N identical blades,
// equally spaced about one axis. Each blade is a rotation of the others about
// the hub, so their areas are equal and their centroids sit on a circle about
// it — and the centroid of the whole set is therefore the hub EXACTLY, for any
// blade shape and any number of blades from two up. The spinner is
// rotationally symmetric too, so it only strengthens the answer.
//
// (The first version of this fitted a circle to the blade tips. It is correct
// in principle and fragile in practice: it needs the tips found by angular
// binning about a guess that is itself off-hub, and the binning fails on wide
// blades, on contra-rotating pairs and wherever the hub body fills every
// direction. The centroid needs no guess, no bins and no thresholds.)
//
// AREA-weighted, not vertex-averaged: these models carry far more vertices
// near the root than out at the tip, so an unweighted average sits well inside
// the hub of its own accord.
function hubOfTris(each) {
  let cr = 0, cu = 0, cf = 0, w = 0;
  const a = new THREE.Vector3();
  const b = new THREE.Vector3();
  const c = new THREE.Vector3();
  const ab = new THREE.Vector3();
  const ac = new THREE.Vector3();
  const n = new THREE.Vector3();
  each((p0, p1, p2) => {
    a.copy(p0); b.copy(p1); c.copy(p2);
    const area = n.crossVectors(ab.subVectors(b, a), ac.subVectors(c, a)).length() / 2;
    if (!(area > 0)) return;
    cr += (a.x + b.x + c.x) / 3 * area;
    cu += (a.y + b.y + c.y) / 3 * area;
    cf += (a.z + b.z + c.z) / 3 * area;
    w += area;
  });
  return w > 0 ? { r: cr / w, u: cu / w, f: cf / w } : null;
}

// The hub of a cut cluster, in the aeroplane's own (r, u, f) axes.
function hubOfCluster(set, basis) {
  const { f, r, u } = basis;
  const v = new THREE.Vector3();
  const p = [new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()];
  return hubOfTris((emit) => {
    for (const shell of set) {
      const { entry, tris } = shell;
      const pos = entry.geo.attributes.position;
      for (const t of tris) {
        for (let j = 0; j < 3; j++) {
          v.fromBufferAttribute(pos, entry.tri[t * 3 + j]).applyMatrix4(entry.toModel);
          p[j].set(v.dot(r), v.dot(u), v.dot(f));
        }
        emit(p[0], p[1], p[2]);
      }
    }
  });
}

// How far out the blades reach from a hub — the blur disc's radius.
function reachFrom(set, basis, hub) {
  const { r, u } = basis;
  const v = new THREE.Vector3();
  let far = 0;
  for (const shell of set) {
    const pos = shell.entry.geo.attributes.position;
    for (const t of shell.tris) {
      for (let j = 0; j < 3; j++) {
        v.fromBufferAttribute(pos, shell.entry.tri[t * 3 + j]).applyMatrix4(shell.entry.toModel);
        far = Math.max(far, Math.hypot(v.dot(r) - hub.r, v.dot(u) - hub.u));
      }
    }
  }
  return far;
}

// ---- cutting it out ---------------------------------------------------------
// Build a compact geometry from the cluster's triangles and take those same
// triangles out of the meshes they came from, so the blades exist exactly once.
function extract(cluster, basis, hub) {
  const byMesh = new Map();
  for (const shell of cluster.set) {
    let g = byMesh.get(shell.entry);
    if (!g) byMesh.set(shell.entry, (g = []));
    g.push(...shell.tris);
  }
  const parts = [];
  for (const [entry, tris] of byMesh) {
    const { mesh, geo, toModel } = entry;
    const at = (t, j) => entry.tri[t * 3 + j];
    for (const t of tris) entry.taken.add(t);

    // the new geometry: only the vertices these triangles use, with the hub
    // moved to the origin so the mesh turns about its own centre
    const remap = new Map();
    const srcIdx = [];
    const newIdx = [];
    for (const t of tris) {
      for (let j = 0; j < 3; j++) {
        const vi = at(t, j);
        let ni = remap.get(vi);
        if (ni === undefined) {
          ni = srcIdx.length;
          remap.set(vi, ni);
          srcIdx.push(vi);
        }
        newIdx.push(ni);
      }
    }
    const sub = new THREE.BufferGeometry();
    for (const name of Object.keys(geo.attributes)) {
      const a = geo.attributes[name];
      const it = a.itemSize;
      const dst = new Float32Array(srcIdx.length * it);
      // getComponent, NOT a.array[i * itemSize + c]. These GLBs come through
      // Draco and gltf-transform, so their attributes are routinely
      // INTERLEAVED (one array, several attributes, a stride that is not the
      // item size) and QUANTIZED (int16 normals that have to be denormalized
      // to mean anything). Indexing the raw array assumes neither is true; on
      // the XB-35 it read the wrong words and wrote NaN positions into the
      // flying wing, which is a far worse bug than a propeller that does not
      // turn. getComponent knows about both.
      for (let i = 0; i < srcIdx.length; i++) {
        for (let c = 0; c < it; c++) dst[i * it + c] = a.getComponent(srcIdx[i], c);
      }
      sub.setAttribute(name, new THREE.BufferAttribute(dst, it));
    }
    sub.setIndex(newIdx);
    // into the model's frame, then hub to the origin
    sub.applyMatrix4(toModel);
    sub.translate(-hub.x, -hub.y, -hub.z);
    sub.computeBoundingSphere();

    const mat = Array.isArray(mesh.material) ? mesh.material[0] : mesh.material;
    const blade = new THREE.Mesh(sub, mat);
    blade.castShadow = mesh.castShadow;
    blade.receiveShadow = mesh.receiveShadow;
    parts.push(blade);
  }
  return parts;
}

// Take every cut triangle out of the meshes they came from, so the blades
// exist exactly once. Done in one pass after ALL the propellers are built --
// see makeEntry for why.
function closeUp(entries) {
  for (const e of entries) {
    if (!e.taken.size) continue;
    const kept = [];
    for (let t = 0; t < e.tri.length / 3; t++) {
      if (e.taken.has(t)) continue;
      kept.push(e.tri[t * 3], e.tri[t * 3 + 1], e.tri[t * 3 + 2]);
    }
    e.geo.setIndex(kept);
    e.geo.computeBoundingSphere();
    e.geo.computeBoundingBox();
  }
}

// A propeller at speed is not a set of blades, it is a disc you can see
// through -- and a game that just spins the blades at the real rate shows you
// a strobe instead, because sixty frames a second cannot sample 2400 rpm.
// So the blades fade out as the revs come up and this takes their place,
// exactly the way GTA does it. Painted rather than textured: a radial ramp
// with the blade count beaten into it, so it reads as a turning disc and not
// as a sheet of grey.
function blurDisc(radius, blades = 3) {
  const S = 256;
  const c = document.createElement('canvas');
  c.width = c.height = S;
  const x = c.getContext('2d');
  const img = x.createImageData(S, S);
  const d = img.data;
  for (let j = 0; j < S; j++) {
    for (let i = 0; i < S; i++) {
      const u = (i / S) * 2 - 1, v = (j / S) * 2 - 1;
      const rr = Math.hypot(u, v);
      let a = 0;
      if (rr < 1) {
        // thickest where the blade has most chord (a third of the way out),
        // fading to nothing at the tip and clear of the hub
        const band = Math.exp(-((rr - 0.45) ** 2) / 0.16);
        const edge = Math.min(1, (1 - rr) * 6);
        // a faint rotational streak so the disc has grain and does not look
        // like a flat decal
        const ang = Math.atan2(v, u);
        const streak = 0.75 + 0.25 * Math.sin(ang * blades * 2 + rr * 9);
        a = band * edge * streak * 150;
      }
      const o = (j * S + i) * 4;
      d[o] = d[o + 1] = d[o + 2] = 40;
      d[o + 3] = a;
    }
  }
  x.putImageData(img, 0, 0);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const mesh = new THREE.Mesh(
    new THREE.CircleGeometry(radius, 36),
    new THREE.MeshBasicMaterial({
      map: tex, transparent: true, opacity: 0, depthWrite: false,
      side: THREE.DoubleSide, toneMapped: false,
    })
  );
  mesh.renderOrder = 2;
  mesh.visible = false;
  return mesh;
}

// The aeroplane's own axes from the direction its nose points.
function basisOf(nose) {
  const f = nose.clone().normalize();
  const u = new THREE.Vector3(0, 1, 0);
  if (Math.abs(f.dot(u)) > 0.9) u.set(0, 0, 1);
  const r = new THREE.Vector3().crossVectors(f, u).normalize();
  u.crossVectors(r, f).normalize();
  return { f, r, u };
}

// Cut a mesh the modeller NAMED as a propeller into one pivot per set of
// blades. Used when the name is trustworthy but the mesh holds more than one.
function splitNamed(root, named, basis, want) {
  const entries = named
    .filter((o) => o.isMesh && !o.isSkinnedMesh && o.geometry?.attributes?.position)
    .map((mesh) => {
      root.updateMatrixWorld(true);
      const toModel = new THREE.Matrix4().copy(root.matrixWorld).invert()
        .multiply(mesh.matrixWorld);
      return makeEntry(mesh, mesh.geometry, toModel);
    });
  if (!entries.length) return [];
  const shells = [];
  for (const e of entries) shells.push(...shellsOf(e, basis));
  const clusters = clusterBlades(shells)
    .filter((c) => Math.max(c.dr, c.du) > 0.1)
    .sort((a, b) => b.tris - a.tris)
    .slice(0, Math.max(1, want));
  if (clusters.length < 2) return [];             // nothing was actually shared
  const { f, r, u } = basis;
  const out = [];
  for (const cl of clusters) {
    const h = hubOfCluster(cl.set, basis) ?? { r: cl.cr, u: cl.cu, f: cl.cf };
    const hub = new THREE.Vector3()
      .addScaledVector(r, h.r).addScaledVector(u, h.u).addScaledVector(f, cl.cf);
    const parts = extract(cl, basis, hub);
    if (!parts.length) continue;
    const pivot = new THREE.Group();
    pivot.name = 'propeller_split';
    pivot.position.copy(hub);
    for (const p of parts) pivot.add(p);
    pivot.userData.isPropeller = true;
    pivot.userData.bladeCount = cl.set.length;
    pivot.userData.radius = reachFrom(cl.set, basis, h);
    root.add(pivot);
    out.push(pivot);
  }
  closeUp(entries);
  // the emptied original is no longer a propeller, it is an empty mesh
  for (const o of named) delete o.userData.isPropeller;
  return out;
}

// ---- second pass: blades WELDED into the airframe ---------------------------
//
// Four of these models have their propellers modelled but joined to the rest
// of the aeroplane: the Lancaster, both Hercules and the Cessna 310 are one
// continuous surface from the spinner to the tail, so the shell pass above
// hands back a single "part" the size of the whole aircraft and finds nothing.
//
// What is still true of the blades is that there is AIR BEHIND THEM. Look
// straight down the nose axis through a narrow column: on a propeller you hit
// the blade, then a gap, then the nacelle. On a wing leading edge, a cowling
// or a tailplane you hit surface and keep hitting surface. So the geometry is
// read column by column, everything in front of the first real gap is pulled
// out, and what that leaves is clustered and scored exactly like the shells.
const GAP = 0.28;       // m of clear air that marks a detached front
// How deep the detached piece may be, as a fraction of the wingspan. A blade
// has almost no depth; a nose cone or a nacelle front has plenty, and letting
// one in drags the whole cluster's depth past what a disc is allowed.
const BLADE_DEEP = 0.035;

function detachedFront(entries, basis, span, pusher) {
  const { f, r, u } = basis;
  const cell = Math.max(0.12, span / 220);
  const cells = new Map();
  const v = new THREE.Vector3();
  entries.forEach((e, ei) => {
    const pos = e.geo.attributes.position;
    const n = e.tri.length / 3;
    for (let t = 0; t < n; t++) {
      let x = 0, y = 0, z = 0;
      for (let j = 0; j < 3; j++) {
        v.fromBufferAttribute(pos, e.tri[t * 3 + j]).applyMatrix4(e.toModel);
        x += v.x; y += v.y; z += v.z;
      }
      v.set(x / 3, y / 3, z / 3);
      const pr = v.dot(r), pu = v.dot(u), pf = v.dot(f) * (pusher ? -1 : 1);
      const k = `${Math.round(pr / cell)},${Math.round(pu / cell)}`;
      let c = cells.get(k);
      if (!c) cells.set(k, (c = []));
      c.push({ ei, t, pr, pu, pf });
    }
  });

  const front = [];
  for (const list of cells.values()) {
    if (list.length < 2) continue;
    list.sort((a, b) => b.pf - a.pf);
    let cut = -1;
    for (let i = 1; i < list.length; i++) {
      if (list[i - 1].pf - list[i].pf > GAP) { cut = i; break; }
    }
    if (cut < 0) continue;                          // solid all the way back
    if (list[0].pf - list[cut - 1].pf > Math.max(0.35, span * BLADE_DEEP)) continue;
    for (let i = 0; i < cut; i++) front.push(list[i]);
  }
  if (DEBUG) {
    console.log(`  detached-front (${pusher ? 'pusher' : 'tractor'}):`
      + ` ${front.length} tris loose out of ${cells.size} columns`);
  }
  if (front.length < MIN_TRIS) return [];

  // connected components of what came loose, so one blade is one piece
  const grid = new Map();
  front.forEach((p, i) => {
    const k = `${Math.round(p.pr / cell)},${Math.round(p.pu / cell)},${Math.round(p.pf / cell)}`;
    let g = grid.get(k);
    if (!g) grid.set(k, (g = []));
    g.push(i);
  });
  const dsu = makeDSU(front.length);
  for (const [k, ids] of grid) {
    const [a, b, c] = k.split(',').map(Number);
    for (let i = 1; i < ids.length; i++) dsu.union(ids[0], ids[i]);
    for (let da = -1; da <= 1; da++) {
      for (let db = -1; db <= 1; db++) {
        for (let dc = -1; dc <= 1; dc++) {
          if (!da && !db && !dc) continue;
          const o = grid.get(`${a + da},${b + db},${c + dc}`);
          if (o) dsu.union(ids[0], o[0]);
        }
      }
    }
  }

  // one pseudo-shell per (component, source mesh) — extract() cuts per mesh
  const byKey = new Map();
  front.forEach((p, i) => {
    const k = `${dsu.find(i)}|${p.ei}`;
    let g = byKey.get(k);
    if (!g) byKey.set(k, (g = []));
    g.push(p);
  });
  const sign = pusher ? -1 : 1;
  const out = [];
  for (const g of byKey.values()) {
    if (g.length < MIN_TRIS) continue;
    const fs = g.map((p) => p.pf * sign), rs = g.map((p) => p.pr), us = g.map((p) => p.pu);
    const box = {
      fmin: Math.min(...fs), fmax: Math.max(...fs),
      rmin: Math.min(...rs), rmax: Math.max(...rs),
      umin: Math.min(...us), umax: Math.max(...us),
    };
    out.push({
      entry: entries[g[0].ei], tris: g.map((p) => p.t), ...box,
      df: box.fmax - box.fmin, dr: box.rmax - box.rmin, du: box.umax - box.umin,
      cf: (box.fmin + box.fmax) / 2, cr: (box.rmin + box.rmax) / 2,
      cu: (box.umin + box.umax) / 2,
    });
  }
  if (DEBUG) {
    const big = out.filter((o) => Math.max(o.dr, o.du) > span * 0.02)
      .sort((a, b) => b.cf - a.cf);
    console.log('  detached pieces:', big.slice(0, 30).map((o) =>
      `${Math.max(o.dr, o.du).toFixed(1)}m@(${o.cr.toFixed(1)},${o.cu.toFixed(1)},${o.cf.toFixed(1)})`).join(' '));
  }
  return out;
}

// ---- when the model never had one -------------------------------------------
//
// The P-51 is a 120,000-triangle Mustang with a spinner and NO BLADES: the
// model simply does not contain a propeller, so there is nothing to find and
// nothing to cut out. A Mustang parked with a bare spinner is wrong in a way
// anybody can see, so one gets built — four blades of the real article's size
// (3.40 m across, a Hamilton Standard), rooted at the spinner the model does
// have.
//
// Lofted rather than extruded: a propeller blade is an aerofoil that changes
// chord, thickness AND pitch along its length — flat at the tip, screwed
// round nearly 25 degrees at the root, because the root is moving far slower
// through the air than the tip is. A flat plate rotated about its axis reads
// as a paddle; this reads as a propeller even stationary.
function buildBlades(radius, count = 4) {
  const STN = 8;      // stations along the blade
  const hubR = radius * 0.1;
  const pos = [], norm = [], col = [], idx = [];
  const tipCol = new THREE.Color(0xd8bb2a);   // the yellow tip stripe
  const bodyCol = new THREE.Color(0x15161a);
  const c = new THREE.Color();
  for (let b = 0; b < count; b++) {
    const roll = (b / count) * Math.PI * 2;
    const base = pos.length / 3;
    for (let i = 0; i < STN; i++) {
      const t = i / (STN - 1);
      const r = hubR + (radius - hubR) * t;
      // chord: widest a third of the way out, tapering to almost nothing
      const chord = radius * (0.17 + 0.09 * Math.sin(t * Math.PI) - 0.13 * t * t);
      const thick = chord * (0.16 - 0.09 * t);
      // pitch: steep at the root, flat at the tip
      const pitch = (24 - 19 * t) * Math.PI / 180;
      c.copy(t > 0.86 ? tipCol : bodyCol);
      // four corners of a thin lens, in the blade's own (chord, thickness)
      // plane, then rolled to this blade's position round the hub
      for (const [cq, tq] of [[0.5, 0], [0, 0.5], [-0.5, 0], [0, -0.5]]) {
        const x = cq * chord * Math.cos(pitch) - tq * thick * Math.sin(pitch);
        const z = cq * chord * Math.sin(pitch) + tq * thick * Math.cos(pitch);
        // the blade points along +Y before being rolled about Z
        const px = x * Math.cos(roll) - r * Math.sin(roll);
        const py = x * Math.sin(roll) + r * Math.cos(roll);
        pos.push(px, py, z);
        const n = new THREE.Vector3(px, py, z * 4).normalize();
        norm.push(n.x, n.y, n.z);
        col.push(c.r, c.g, c.b);
      }
    }
    for (let i = 0; i < STN - 1; i++) {
      for (let k = 0; k < 4; k++) {
        const a = base + i * 4 + k, a2 = base + i * 4 + ((k + 1) % 4);
        idx.push(a, a2, a + 4, a2, a2 + 4, a + 4);
      }
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(norm, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.setIndex(idx);
  g.computeVertexNormals();
  const mesh = new THREE.Mesh(g, new THREE.MeshStandardMaterial({
    vertexColors: true, roughness: 0.42, metalness: 0.55,
  }));
  mesh.castShadow = true;
  return mesh;
}

// A NAMED PROPELLER'S OWN PIVOT IS USUALLY WRONG TOO.
//
// Whatever origin the model shipped with -- and whatever
// origin_set(ORIGIN_GEOMETRY) the Blender pipeline gave it -- is a bounding
// box centre or a median, and neither is the hub of an odd-bladed propeller.
// The Spitfire turned about a point a quarter of a radius above its spinner.
//
// Fixed by REPARENTING rather than by moving geometry: a Group is put at the
// fitted hub and `attach()` takes the mesh into it without moving it a
// millimetre on screen. The geometry is shared with every other clone of this
// aircraft, so translating it would shift the propeller once per copy.
function repivotNamed(root, found, basis) {
  const { f, r, u } = basis;
  const out = [];
  root.updateMatrixWorld(true);
  const inv = new THREE.Matrix4().copy(root.matrixWorld).invert();
  for (const o of found) {
    if (!o.isMesh || o.isSkinnedMesh) { out.push(o); continue; } // a bone knows its own axis
    const geo = o.geometry;
    const pos = geo?.attributes?.position;
    if (!pos) { out.push(o); continue; }
    const toModel = new THREE.Matrix4().multiplyMatrices(inv, o.matrixWorld);
    const idx = o.geometry.index;
    const nTri = (idx ? idx.count : pos.count) / 3;
    const v = new THREE.Vector3();
    const p3 = [new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()];
    const h = hubOfTris((emit) => {
      for (let t = 0; t < nTri; t++) {
        for (let j = 0; j < 3; j++) {
          v.fromBufferAttribute(pos, idx ? idx.getX(t * 3 + j) : t * 3 + j)
            .applyMatrix4(toModel);
          p3[j].set(v.dot(r), v.dot(u), v.dot(f));
        }
        emit(p3[0], p3[1], p3[2]);
      }
    });
    if (!h) { out.push(o); continue; }
    const hub = new THREE.Vector3()
      .addScaledVector(r, h.r).addScaledVector(u, h.u).addScaledVector(f, h.f);
    let reach = 0;
    for (let i = 0; i < pos.count; i++) {
      v.fromBufferAttribute(pos, i).applyMatrix4(toModel);
      reach = Math.max(reach, Math.hypot(v.dot(r) - h.r, v.dot(u) - h.u));
    }
    // already turning on its hub? leave it where it is
    const here = o.getWorldPosition(new THREE.Vector3()).applyMatrix4(inv);
    if (here.distanceTo(hub) < Math.max(0.02, reach * 0.02)) {
      o.userData.radius = reach;
      out.push(o);
      continue;
    }
    const pivot = new THREE.Group();
    pivot.name = 'propeller_hub';
    pivot.position.copy(hub);
    root.add(pivot);
    pivot.updateMatrixWorld(true);
    pivot.attach(o);                       // keeps it exactly where it looks
    delete o.userData.isPropeller;
    pivot.userData.isPropeller = true;
    pivot.userData.radius = reach;
    pivot.userData.blades = [o];
    out.push(pivot);
  }
  return out;
}

// ---- told where to look -----------------------------------------------------
//
// Four of these models defeat both passes above: the Lancaster, both Hercules
// and the Cessna 310 have their blades modelled as part of one continuous
// surface that runs from the spinner to the tail, with no shell boundary and
// no column of clear air wide enough to read. There is no honest heuristic
// left -- so those four are TOLD, in their plane def, where their propellers
// are. The numbers are measured off each model's own geometry with
// tools/_probe_hub.mjs, the same way `yaw` and `cockpit` overrides are
// measured: the probe prints the aeroplane station by station, the propellers
// stand out as the geometry that reaches furthest forward, and the hub and
// radius are read off it.
//
// Coordinates are the aeroplane's own: r starboard, u up, f toward the nose,
// in the raw model's frame before the orientation fix.
function hintedPropellers(root, entries, basis, hint) {
  const { f, r, u } = basis;
  const v = new THREE.Vector3();
  const hubs = hint.hubs.map((h) => ({ r: h[0], u: h[1], f: h[2], tris: new Map() }));
  const rad = hint.radius * 1.04;
  const depth = hint.depth ?? hint.radius * 0.35;
  const gather = () => {
    for (const h of hubs) h.tris = new Map();
    for (const e of entries) {
      const pos = e.geo.attributes.position;
      const n = e.tri.length / 3;
      for (let t = 0; t < n; t++) {
        let x = 0, y = 0, z = 0;
        for (let j = 0; j < 3; j++) {
          v.fromBufferAttribute(pos, e.tri[t * 3 + j]).applyMatrix4(e.toModel);
          x += v.x; y += v.y; z += v.z;
        }
        v.set(x / 3, y / 3, z / 3);
        const pf = v.dot(f);
        for (const h of hubs) {
          if (Math.abs(pf - h.f) > depth) continue;
          if (Math.hypot(v.dot(r) - h.r, v.dot(u) - h.u) > rad) continue;
          let g = h.tris.get(e);
          if (!g) h.tris.set(e, (g = []));
          g.push(t);
          break;
        }
      }
    }
  };
  // TWICE ROUND. The cylinder is centred on the hinted hub, so if the hint is
  // a few centimetres out the blades pointing away from the error get their
  // tips clipped — which biases the centroid toward the error that caused it.
  // Cut, find the real hub from what came out, then cut again about THAT. One
  // correction is enough: it took the Cessna from 22% off to under 2%.
  gather();
  for (const h of hubs) {
    const set = [...h.tris.entries()].map(([entry, tris]) => ({ entry, tris }));
    if (!set.length) continue;
    const c = hubOfCluster(set, basis);
    if (c) { h.r = c.r; h.u = c.u; }
  }
  gather();
  const out = [];
  for (const h of hubs) {
    const set = [...h.tris.entries()].map(([entry, tris]) => ({ entry, tris }));
    if (!set.length) continue;
    // The hint says WHERE TO LOOK, not where the hub is. Once the blades are
    // in hand their own centroid gives it exactly, so the measured numbers
    // only have to be good enough to catch the right propeller.
    const c = hubOfCluster(set, basis) ?? { r: h.r, u: h.u, f: h.f };
    const hub = new THREE.Vector3()
      .addScaledVector(r, c.r).addScaledVector(u, c.u).addScaledVector(f, h.f);
    const parts = extract({ set }, basis, hub);
    if (!parts.length) continue;
    const pivot = new THREE.Group();
    pivot.name = 'propeller_split';
    pivot.position.copy(hub);
    for (const p of parts) pivot.add(p);
    pivot.userData.isPropeller = true;
    pivot.userData.bladeCount = 3;
    pivot.userData.radius = reachFrom(set, basis, c);
    root.add(pivot);
    out.push(pivot);
  }
  closeUp(entries);
  return out;
}

// ---- the entry point --------------------------------------------------------
// `nose` is where this model's nose points IN ITS OWN FRAME: the orientation
// fix (def.yaw/def.roll in src/planes.js) is applied to a wrapper around the
// model, so the raw file may well be stored tail-first or sideways.
export function tagPropellers(root, nose, want = 1, hint = null) {
  let found = [];
  root.traverse((o) => { if (o.userData.isPropeller) found.push(o); });

  // A NAME IS A HINT, NOT AN ANSWER.
  //
  //  - The one rigged aeroplane names its propeller BONES, and names their
  //    tail bones too ("Propeller.L_04" and "Propeller.L_end_069"): turning
  //    both turns the blades twice. Only the top of each chain counts.
  //  - The Cessna 310 names ONE mesh "Prop1" and both of its propellers are
  //    inside it, 2.7 m apart. Spun about its own centre that is not a
  //    propeller turning, it is two of them orbiting the fuselage.
  // So a named set is kept only when it holds enough separate, plausible
  // propellers; otherwise the names are dropped and the geometry decides.
  found = found.filter((o) => {
    for (let p = o.parent; p && p !== root; p = p.parent) {
      if (p.userData.isPropeller) return false;   // a tail bone under its own blade
    }
    return true;
  });
  if (found.length >= want) {
    const bones = found.some((o) => o.isBone);
    const sane = bones || found.every((o) => {
      const d = new THREE.Box3().setFromObject(o, true).getSize(new THREE.Vector3());
      const a = [d.x, d.y, d.z].sort((x, y) => y - x);
      return a[1] > a[0] * 0.5;                   // a disc, not a pair of them
    });
    if (sane) {
      root.traverse((o) => { o.userData.isPropeller = found.includes(o); });
      return repivotNamed(root, found, basisOf(nose));
    }
    if (DEBUG) console.log('  named propellers are not discs; splitting them');
    // THE NAME WAS RIGHT, THE MESH IS SHARED. The Cessna 310 calls one mesh
    // "Prop1" and keeps BOTH of its propellers in it, 3.9 m apart. There is
    // nothing to search for here -- the modeller already said this is the
    // propeller -- so the mesh is simply cut along its own shells and each
    // group of blades becomes a propeller of its own. No clearance test: the
    // name is the evidence.
    const split = splitNamed(root, found, basisOf(nose), want);
    if (split.length) return split;
  }
  // start clean: whatever the names claimed is about to be cut out properly
  root.traverse((o) => { delete o.userData.isPropeller; });

  const basis = basisOf(nose);
  const { f, r, u } = basis;

  const entries = gatherMeshes(root);
  if (!entries.length) return [];

  // No propeller in the file at all: build one where the spinner is.
  if (hint?.make) {
    const m = hint.make;
    const hub = new THREE.Vector3()
      .addScaledVector(r, m.hub[0]).addScaledVector(u, m.hub[1]).addScaledVector(f, m.hub[2]);
    const pivot = new THREE.Group();
    pivot.name = 'propeller_built';
    pivot.position.copy(hub);
    const blades = buildBlades(m.radius, m.blades ?? 4);
    // built in its own XY plane; stand it up to face along the nose
    blades.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), f);
    pivot.add(blades);
    pivot.userData.isPropeller = true;
    pivot.userData.bladeCount = m.blades ?? 4;
    pivot.userData.radius = m.radius;
    root.add(pivot);
    return [pivot];
  }

  // Told where they are? Then there is nothing to search for.
  if (hint?.hubs?.length) {
    const props = hintedPropellers(root, entries, basis, hint);
    if (props.length) return props;
    console.warn('propeller hint found nothing — falling back to the search');
  }

  const box = new THREE.Box3().setFromObject(root, true);
  const size = box.getSize(new THREE.Vector3());
  const span = Math.max(size.x, size.z);

  const shells = [];
  for (const e of entries) shells.push(...shellsOf(e, basis));
  if (!shells.length) return [];

  // every triangle centroid, in (r, u, f) — the evidence for "is anything in
  // front of this", gathered once
  const centroids = [];
  const v = new THREE.Vector3();
  for (const e of entries) {
    const pos = e.geo.attributes.position;
    const idx = e.geo.index;
    const n = (idx ? idx.count : pos.count) / 3;
    for (let t = 0; t < n; t++) {
      let cx = 0, cy = 0, cz = 0;
      for (let j = 0; j < 3; j++) {
        v.fromBufferAttribute(pos, idx ? idx.getX(t * 3 + j) : t * 3 + j)
          .applyMatrix4(e.toModel);
        cx += v.x; cy += v.y; cz += v.z;
      }
      v.set(cx / 3, cy / 3, cz / 3);
      centroids.push(v.dot(r), v.dot(u), v.dot(f));
    }
  }
  const cent = Float32Array.from(centroids);

  const blades = shells.filter((s) => {
    const radial = Math.max(s.dr, s.du);
    return radial > span * 0.03 && radial < span * 0.6 && s.df < radial * 0.65;
  });
  const rejected = [];
  let shellSet = clusterBlades(blades);
  // Nothing plausible came out of the shells? The blades are welded on. Read
  // the aeroplane the other way — both ways round, because the XB-35 is not
  // the only machine in here that pushes rather than pulls.
  if (!shellSet.some((c) => scoreCluster(c, span, cent, want))) {
    for (const pusher of [false, true]) {
      const alt = clusterBlades(detachedFront(entries, basis, span, pusher));
      if (DEBUG) {
        console.log('  welded-pass clusters:', alt.map((c) => {
          const why = [];
          const ok = scoreCluster(c, span, cent, want, why);
          return `${Math.max(c.dr, c.du).toFixed(1)}m ${ok ? 'OK' : why[0]}`;
        }).slice(0, 10).join(' | '));
      }
      if (alt.some((c) => scoreCluster(c, span, cent, want))) { shellSet = alt; break; }
    }
  }
  const clusters = shellSet
    .map((c) => {
      const why = [];
      const ok = scoreCluster(c, span, cent, want, why);
      if (!ok && why.length) {
        rejected.push(`${Math.max(c.dr, c.du).toFixed(1)}m @`
          + `(${c.cr.toFixed(1)},${c.cu.toFixed(1)},${c.cf.toFixed(1)}) ${why[0]}`);
      }
      return ok;
    })
    .filter(Boolean)
    .sort((a, b) => b.score - a.score)
    .slice(0, Math.max(1, want));
  if (!clusters.length) {
    if (DEBUG) console.log('  no propeller; closest rejects:', rejected.slice(0, 8));
    return [];
  }

  const props = [];
  for (const cl of clusters) {
    const h = hubOfCluster(cl.set, basis) ?? { r: cl.cr, u: cl.cu, f: cl.cf };
    const hub = new THREE.Vector3()
      .addScaledVector(r, h.r).addScaledVector(u, h.u).addScaledVector(f, cl.cf);
    const parts = extract(cl, basis, hub);
    if (!parts.length) continue;
    const pivot = new THREE.Group();
    pivot.name = 'propeller_split';
    pivot.position.copy(hub);
    for (const p of parts) pivot.add(p);
    pivot.userData.isPropeller = true;
    pivot.userData.bladeCount = cl.set.length;
    pivot.userData.radius = reachFrom(cl.set, basis, h);
    root.add(pivot);
    props.push(pivot);
  }
  closeUp(entries);
  return props;
}

// ---- finishing every propeller, however it was found ------------------------
// Three kinds end up in the list: meshes the model named itself, BONES in the
// one rigged aircraft, and the groups cut out above. They do not share a
// convention for which way is "round", so the spin axis is MEASURED rather
// than assumed: whatever direction the aeroplane's nose points, expressed in
// the propeller's own local frame. Rotating about that axis leaves the axis
// where it is, so it stays right for as long as the plane exists.
//
// The old code turned every propeller about its local Z. That is only correct
// for a mesh the Blender pipeline re-pivoted on purpose; on a bone it is a
// wobble, and on anything else it is a guess.
export function finishPropellers(wrapper, nose = new THREE.Vector3(0, 0, -1)) {
  const props = [];
  wrapper.updateMatrixWorld(true);
  wrapper.traverse((o) => { if (o.userData.isPropeller) props.push(o); });
  const q = new THREE.Quaternion();
  for (const p of props) {
    p.getWorldQuaternion(q);
    p.userData.spinAxis = nose.clone().applyQuaternion(q.invert()).normalize();
    if (p.userData.disc) continue;
    // How big a disc to draw: the propeller's own extent across the nose axis.
    let radius = p.userData.radius ?? 0;
    if (!radius) {
      const box = new THREE.Box3().setFromObject(p, true);
      const size = box.getSize(new THREE.Vector3());
      const across = [size.x, size.y, size.z].sort((a, b) => b - a);
      radius = across[0] / 2;
    }
    if (!(radius > 0.05)) continue;
    const blades = p.userData.bladeCount ?? 3;
    const disc = blurDisc(radius * 1.02, blades);
    // CircleGeometry is drawn in XY; turn it to face along the nose. `nose` is
    // in the WRAPPER's frame and the disc hangs off the propeller, so it has
    // to be expressed in the propeller's frame too.
    disc.quaternion.setFromUnitVectors(
      new THREE.Vector3(0, 0, 1), p.userData.spinAxis
    );
    // Centre it on the hub rather than on the blades' bounding box: a
    // two-blade propeller caught at an angle has an off-centre box, and a disc
    // drawn there sits proud of the spinner.
    p.add(disc);
    p.userData.disc = disc;
    p.userData.radius = radius;
    if (!p.userData.blades) {
      const meshes = [];
      p.traverse((o) => { if (o.isMesh && o !== disc) meshes.push(o); });
      p.userData.blades = meshes;
    }
  }
  return props;
}

// How fast and how solid. `rpm` is the engine's, 0 at a stop.
const BLUR_START = 420;   // rpm — the blades begin to smear
const BLUR_FULL = 1100;   // rpm — nothing left but the disc
const DISC_DRIFT = 3.2;   // rad/s the blur disc itself turns at, whatever the revs
const DISC_Z = new THREE.Vector3(0, 0, 1);

export function spinPropellers(plane, rpm, dt) {
  const props = plane?.userData?.propellers;
  if (!props?.length) return;
  const w = (rpm / 60) * Math.PI * 2;             // rad/s
  const blur = THREE.MathUtils.clamp(
    (rpm - BLUR_START) / (BLUR_FULL - BLUR_START), 0, 1
  );
  for (const p of props) {
    const axis = p.userData.spinAxis;
    if (axis) p.rotateOnAxis(axis, w * dt);
    else p.rotation.z += w * dt;                  // model-named props: local Z
    const disc = p.userData.disc;
    if (!disc) continue;
    // THE DISC MUST NOT TURN WITH THE BLADES. It hangs off the propeller, so
    // left alone it inherits the full 2700 rpm and its painted streaks strobe
    // exactly the way the blades do -- which is the entire thing it exists to
    // hide. Counter-rotate it back to a slow drift: a real propeller disc
    // shimmers, it does not flicker. (Its local +Z IS the spin axis: that is
    // how it was oriented.)
    disc.rotateOnAxis(DISC_Z, (DISC_DRIFT - w) * dt);
    disc.visible = blur > 0.01;
    disc.material.opacity = blur * 0.85;
    // The blades do not vanish — a real propeller at speed still shows a faint
    // ghost of them, and a disc with nothing behind it reads as a decal.
    for (const b of p.userData.blades ?? []) {
      const want = 1 - blur * 0.72;
      if (!b.material.transparent && want < 0.999) {
        b.material = b.material.clone();
        b.material.transparent = true;
        b.material.depthWrite = true;
      }
      if (b.material.transparent) b.material.opacity = want;
    }
  }
}
