import * as THREE from 'three';

// Structural breakup: tears REAL pieces off the airframe. The plane's actual
// render geometry is cut along structural lines — wings at the fuselage side,
// the tail at the rear third, the nose section — by classifying every
// triangle's centroid in plane-local space. Works identically on the
// many-mesh Spitfire/F-104 and the single-mesh C-17: no named nodes needed,
// and the torn pieces keep their original materials and textures.
//
// A detached part becomes a ballistic rigid body: it keeps the aircraft's
// velocity plus a tear impulse, tumbles (wings flutter — big area, low mass),
// bounces off the terrain, and can burn where the fuel was. resetPlane()
// restores every cut via restore().

const G = 9.81;

export class PlaneBreakup {
  constructor(game) {
    this.game = game;
    this.gone = new Set();
    this.parts = [];        // flying/settled wreckage bodies
    this._splits = [];      // {mesh, origGeo} — geometry swaps to undo
    this._removedWhole = []; // {obj, parent} — whole meshes to re-add
    this._built = false;
    this._m4 = new THREE.Matrix4();
    this._v = new THREE.Vector3();
  }

  // Structural cut lines in wrapper-local space, from the real dimensions.
  _build() {
    if (this._built) return;
    this._built = true;
    const plane = this.game.plane;
    plane.updateMatrixWorld(true);
    this._wrapperInv = new THREE.Matrix4().copy(plane.matrixWorld).invert();

    // Wrapper-space bbox: transform each mesh bbox corner by relMatrix.
    const box = new THREE.Box3();
    const tmp = new THREE.Vector3();
    plane.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      o.geometry.computeBoundingBox();
      const rel = new THREE.Matrix4().multiplyMatrices(this._wrapperInv, o.matrixWorld);
      const bb = o.geometry.boundingBox;
      for (let i = 0; i < 8; i++) {
        tmp.set(
          i & 1 ? bb.max.x : bb.min.x,
          i & 2 ? bb.max.y : bb.min.y,
          i & 4 ? bb.max.z : bb.min.z
        ).applyMatrix4(rel);
        box.expandByPoint(tmp);
      }
    });
    const size = this.game.plane.userData.size ?? 11;
    // Wings snap at the fuselage side; tails break at the rear third.
    this.fusHalf = THREE.MathUtils.clamp(size * 0.1, 0.5, 3.5);
    this.tailZ = box.max.z - (box.max.z - box.min.z) * 0.3;
    this.noseZ = box.min.z + (box.max.z - box.min.z) * 0.16;
    this.regions = {
      wingL: (p) => p.x < -this.fusHalf && p.z < this.tailZ,
      wingR: (p) => p.x > this.fusHalf && p.z < this.tailZ,
      tail: (p) => p.z > this.tailZ,
      nose: (p) => p.z < this.noseZ,
    };
  }

  has(part) {
    return !this.gone.has(part);
  }

  // Tear a structural part off the airframe. impulse (world, m/s) is added to
  // the aircraft's velocity for the piece; burn attaches a fire + smoke trail.
  detach(part, { impulse = null, spin = 5, burn = false } = {}) {
    if (this.gone.has(part)) return false;
    this._build();
    const game = this.game;
    const plane = game.plane;
    plane.updateMatrixWorld(true);
    this._wrapperInv.copy(plane.matrixWorld).invert();
    const region = this.regions[part];

    // Collect the torn-off render pieces (mesh-local geometry + rel transform).
    const pieces = [];
    const bbox = new THREE.Box3(); // of the part, wrapper space
    const cV = new THREE.Vector3();
    const rel = new THREE.Matrix4();
    const meshes = [];
    plane.traverse((o) => {
      if (o.isMesh && o.geometry?.attributes?.position) meshes.push(o);
    });

    for (const mesh of meshes) {
      rel.multiplyMatrices(this._wrapperInv, mesh.matrixWorld);
      const { inside, outside, insideCount, total } = splitByRegion(mesh.geometry, rel, region, cV, bbox);
      if (!insideCount) continue;
      if (insideCount === total) {
        // Whole mesh lives in the region (a flap, a gear leg, a bomb).
        this._removedWhole.push({ obj: mesh, parent: mesh.parent });
        pieces.push({ geo: null, mesh, rel: rel.clone() });
        mesh.parent.remove(mesh);
      } else {
        this._splits.push({ mesh, origGeo: mesh.geometry });
        const inGeo = buildSubGeometry(mesh.geometry, inside);
        mesh.geometry = buildSubGeometry(mesh.geometry, outside);
        pieces.push({ geo: inGeo, mesh, rel: rel.clone() });
      }
    }
    if (!pieces.length) return false;
    this.gone.add(part);

    // Assemble the wreck piece around its own center of mass.
    const center = bbox.getCenter(new THREE.Vector3());
    const group = new THREE.Group();
    const centerInv = new THREE.Matrix4().makeTranslation(-center.x, -center.y, -center.z);
    for (const p of pieces) {
      const m = p.geo
        ? new THREE.Mesh(p.geo, p.mesh.material)
        : p.mesh; // whole meshes are re-parented into the wreck group
      m.castShadow = true;
      m.matrixAutoUpdate = false;
      m.matrix.multiplyMatrices(centerInv, p.rel);
      group.add(m);
    }
    group.position.copy(plane.localToWorld(center.clone()));
    group.quaternion.copy(plane.getWorldQuaternion(new THREE.Quaternion()));
    game.scene.add(group);

    const span = bbox.getSize(new THREE.Vector3());
    const radius = Math.max(0.6, Math.min(span.x, span.y, span.z) * 0.4);
    const isWing = part === 'wingL' || part === 'wingR';
    const vel = (game.airborne ? game.vel.clone() : new THREE.Vector3())
      .add(impulse ?? new THREE.Vector3((Math.random() - 0.5) * 6, 3, (Math.random() - 0.5) * 6));
    this.parts.push({
      key: `breakup:${part}:${Math.random().toString(36).slice(2)}`,
      group,
      vel,
      angVel: new THREE.Vector3().randomDirection().multiplyScalar(spin * (0.5 + Math.random())),
      // Wings flutter down like sheet metal; dense chunks drop like stones.
      dragK: isWing ? 0.05 : 0.012,
      radius,
      burn,
      alive: true,
      bounces: 0,
      groundY: null,
      groundT: 0,
      trailT: 0,
      geos: pieces.filter((p) => p.geo).map((p) => p.geo),
    });

    // The tear itself: sparks and a puff of debris at the stump.
    const stump = plane.localToWorld(center.clone());
    for (let i = 0; i < 12; i++) game.fx.spawnEmber(stump.x, stump.y, stump.z);
    game.fx.spawnSmoke(stump.x, stump.y, stump.z, { size: 4, dark: 0.7, dur: 1.8, vy: 1.5, spread: 1.2 });
    return true;
  }

  // Full crash: the airframe comes apart along every structural line, pieces
  // thrown outward with the wreck's momentum. The bare fuselage stays on the
  // plane object for CrashSequence to tumble.
  crashScatter(baseVel, severity = 1) {
    const throwV = (s) =>
      baseVel
        .clone()
        .multiplyScalar(0.35 + Math.random() * 0.3)
        .add(new THREE.Vector3((Math.random() - 0.5) * 14 * s, 5 + Math.random() * 9 * s, (Math.random() - 0.5) * 14 * s));
    this.detach('wingL', { impulse: throwV(severity), spin: 7, burn: true });
    this.detach('wingR', { impulse: throwV(severity), spin: 7, burn: true });
    this.detach('tail', { impulse: throwV(severity * 0.8), spin: 5, burn: false });
    if (severity > 0.85 && Math.random() < 0.6) {
      this.detach('nose', { impulse: throwV(severity), spin: 6, burn: true });
    }
  }

  update(dt) {
    for (const p of this.parts) {
      if (!p.alive) continue;
      // Gravity vs quadratic air drag — wings reach a low terminal velocity
      // and see-saw down; heavy sections just fall.
      const v = p.vel.length();
      p.vel.y -= G * dt;
      if (v > 0.01) p.vel.addScaledVector(p.vel, -Math.min(0.9, p.dragK * v) * dt);
      p.group.position.addScaledVector(p.vel, dt);
      p.group.rotateX(p.angVel.x * dt);
      p.group.rotateY(p.angVel.y * dt);
      p.group.rotateZ(p.angVel.z * dt);

      if (p.burn) {
        p.trailT += dt;
        if (p.trailT > 0.06) {
          p.trailT = 0;
          const c = p.group.position;
          this.game.fx.spawnEmber(c.x, c.y, c.z);
          if (Math.random() < 0.6) {
            this.game.fx.spawnSmoke(c.x, c.y + 0.5, c.z, {
              size: 3.5 + Math.random() * 2.5,
              dark: 0.85,
              dur: 2.6,
              vy: 1.2,
              spread: p.radius * 0.5,
            });
          }
        }
      }

      // Terrain contact (ground height cached briefly — few parts, cheap).
      p.groundT -= dt;
      if (p.groundY === null || p.groundT <= 0) {
        p.groundT = 0.2;
        p.groundY = this.game._groundAt(p.group.position.x, p.group.position.z) ?? p.groundY ?? -1e3;
      }
      if (p.group.position.y - p.radius <= p.groundY) {
        p.group.position.y = p.groundY + p.radius;
        p.bounces++;
        p.vel.y = Math.abs(p.vel.y) * 0.28;
        p.vel.x *= 0.5;
        p.vel.z *= 0.5;
        p.angVel.multiplyScalar(0.45);
        if (p.bounces >= 2 || p.vel.length() < 3) {
          p.alive = false;
          p.vel.set(0, 0, 0);
          if (p.burn) {
            // The piece keeps burning where it came to rest.
            const c = p.group.position;
            this.game.fx.setBurning(
              p.key,
              new Float32Array([c.x, c.y + 0.3, c.z, c.x + p.radius * 0.7, c.y, c.z, c.x - p.radius * 0.5, c.y + 0.5, c.z]),
              0.7
            );
          }
          this.game.fx.plume(p.group.position.x, p.groundY, p.group.position.z, p.radius * 2.5, 2);
        }
      }
    }
  }

  // Undo everything — the plane gets its wings back on respawn.
  restore() {
    // Reverse order: a mesh split twice (wing, then tail) chains geometries —
    // unwinding backwards lands on the true original without disposing it.
    for (let i = this._splits.length - 1; i >= 0; i--) {
      const { mesh, origGeo } = this._splits[i];
      mesh.geometry.dispose();
      mesh.geometry = origGeo;
    }
    this._splits.length = 0;
    for (const { obj, parent } of this._removedWhole) {
      obj.matrixAutoUpdate = true;
      parent.add(obj);
    }
    this._removedWhole.length = 0;
    for (const p of this.parts) {
      this.game.fx.clearBurning(p.key);
      for (const g of p.geos) g.dispose();
      p.group.parent?.remove(p.group);
    }
    this.parts.length = 0;
    this.gone.clear();
  }

  dispose() {
    this.restore();
  }
}

// ---- geometry surgery ----------------------------------------------------

// Partition a geometry's triangles by whether their centroid (taken through
// rel into wrapper space) is inside the region. Returns triangle index lists
// and grows `bbox` by the inside triangles. Handles indexed and non-indexed,
// single- and multi-material (group) geometries.
function splitByRegion(geo, rel, region, cV, bbox) {
  const pos = geo.attributes.position;
  const index = geo.index;
  const triCount = (index ? index.count : pos.count) / 3;
  const inside = [];
  const outside = [];
  const a = new THREE.Vector3();
  const vertexAt = (t, k) => (index ? index.getX(t * 3 + k) : t * 3 + k);
  for (let t = 0; t < triCount; t++) {
    cV.set(0, 0, 0);
    for (let k = 0; k < 3; k++) {
      const vi = vertexAt(t, k);
      cV.x += pos.getX(vi);
      cV.y += pos.getY(vi);
      cV.z += pos.getZ(vi);
    }
    cV.multiplyScalar(1 / 3).applyMatrix4(rel);
    if (region(cV)) {
      inside.push(t);
      for (let k = 0; k < 3; k++) {
        const vi = vertexAt(t, k);
        a.set(pos.getX(vi), pos.getY(vi), pos.getZ(vi)).applyMatrix4(rel);
        bbox.expandByPoint(a);
      }
    } else {
      outside.push(t);
    }
  }
  return { inside, outside, insideCount: inside.length, total: triCount };
}

// Rebuild a non-indexed geometry from a triangle subset, preserving material
// groups so multi-material meshes keep their paint.
function buildSubGeometry(geo, tris) {
  const index = geo.index;
  const srcAttrs = {};
  for (const name of ['position', 'normal', 'uv', 'color']) {
    if (geo.attributes[name]) srcAttrs[name] = geo.attributes[name];
  }
  const out = new THREE.BufferGeometry();
  const arrays = {};
  for (const name in srcAttrs) {
    arrays[name] = new Float32Array(tris.length * 3 * srcAttrs[name].itemSize);
  }
  // Sort triangles by their material group so output groups stay contiguous.
  const groups = geo.groups?.length ? geo.groups : [{ start: 0, count: Infinity, materialIndex: 0 }];
  const matOf = (t) => {
    const v = t * 3;
    for (const g of groups) if (v >= g.start && v < g.start + g.count) return g.materialIndex ?? 0;
    return 0;
  };
  const sorted = tris.slice().sort((t1, t2) => matOf(t1) - matOf(t2));

  let w = 0;
  let runMat = null;
  let runStart = 0;
  for (const t of sorted) {
    const m = matOf(t);
    if (m !== runMat) {
      if (runMat !== null) out.addGroup(runStart, w * 3 - runStart, runMat);
      runMat = m;
      runStart = w * 3;
    }
    for (let k = 0; k < 3; k++) {
      const vi = index ? index.getX(t * 3 + k) : t * 3 + k;
      for (const name in srcAttrs) {
        const attr = srcAttrs[name];
        const sz = attr.itemSize;
        for (let c = 0; c < sz; c++) {
          arrays[name][w * sz + c] = attr.getComponent(vi, c);
        }
      }
      w++;
    }
  }
  if (runMat !== null) out.addGroup(runStart, w * 3 - runStart, runMat);
  for (const name in arrays) {
    out.setAttribute(name, new THREE.BufferAttribute(arrays[name], srcAttrs[name].itemSize));
  }
  if (!srcAttrs.normal) out.computeVertexNormals();
  return out;
}
