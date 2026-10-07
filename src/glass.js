import * as THREE from 'three';
import { glassBreak } from './audiofx.js';

// Breaking glass, end to end. A window dies in four beats:
//   1. the pane instance is zeroed out of its facade layer (CityMap)
//   2. a cloud of REAL tumbling shards sprays out of the hole — instanced
//      quads lit by the scene HDR, so they flash as they spin and fall
//   3. a blown-out pane decal is stamped where the glass used to be, so the
//      facade keeps a black hole with glass teeth instead of a bare wall
//   4. the shatter is heard, with a tail of shards raining on the pavement
// A machine-gun round takes one pane; a rocket takes a whole facade at once.

const SHARD_MAX = 900; // tumbling glass pieces in flight
const SPARK_MAX = 900; // additive glints riding the burst
const PANE_MAX = 700; // blown-out window decals
const GRAVITY = 19; // glass falls hard — it reads as heavy, not as confetti

// One irregular 4-gon: real shards are never rectangles.
function makeShardGeo() {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(new Float32Array([
    0.0, 0.55, 0,
    -0.42, -0.10, 0,
    -0.12, -0.58, 0,
    0.40, -0.28, 0,
  ]), 3));
  g.setIndex([0, 1, 2, 0, 2, 3]);
  g.computeVertexNormals();
  return g;
}

// The hole left behind: dark interior, broken glass teeth still in the frame,
// soot licking up the wall above it.
function makeBrokenPaneTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  // dark room behind the missing glass
  const room = g.createLinearGradient(0, 8, 0, 120);
  room.addColorStop(0, 'rgba(6,6,8,0.96)');
  room.addColorStop(0.6, 'rgba(14,13,15,0.92)');
  room.addColorStop(1, 'rgba(26,22,20,0.86)');
  g.fillStyle = room;
  g.beginPath();
  // ragged opening — not a clean rectangle
  g.moveTo(12, 10);
  for (let k = 0; k <= 24; k++) {
    const t = k / 24;
    const e = 8 + Math.random() * 7;
    if (t < 0.25) g.lineTo(10 + t * 4 * 108, e);
    else if (t < 0.5) g.lineTo(128 - e, 10 + (t - 0.25) * 4 * 108);
    else if (t < 0.75) g.lineTo(118 - (t - 0.5) * 4 * 108, 128 - e);
    else g.lineTo(e, 118 - (t - 0.75) * 4 * 108);
  }
  g.closePath();
  g.fill();
  // glass teeth clinging to the frame, catching the light
  for (let k = 0; k < 34; k++) {
    const side = k % 4;
    const t = Math.random();
    let x; let y; let dx; let dy;
    if (side === 0) { x = 8 + t * 112; y = 12; dx = 0; dy = 1; }
    else if (side === 1) { x = 116; y = 8 + t * 112; dx = -1; dy = 0; }
    else if (side === 2) { x = 8 + t * 112; y = 116; dx = 0; dy = -1; }
    else { x = 12; y = 8 + t * 112; dx = 1; dy = 0; }
    const len = 6 + Math.random() * 20;
    const w = 4 + Math.random() * 10;
    g.fillStyle = `rgba(214,236,248,${0.35 + Math.random() * 0.4})`;
    g.beginPath();
    g.moveTo(x - dy * w * 0.5, y - dx * w * 0.5);
    g.lineTo(x + dy * w * 0.5, y + dx * w * 0.5);
    g.lineTo(x + dx * len + (Math.random() - 0.5) * 6, y + dy * len + (Math.random() - 0.5) * 6);
    g.closePath();
    g.fill();
  }
  // soot around the opening — the fire behind it stained the frame
  const soot = g.createRadialGradient(64, 64, 40, 64, 64, 74);
  soot.addColorStop(0, 'rgba(20,16,14,0)');
  soot.addColorStop(1, 'rgba(20,16,14,0.55)');
  g.fillStyle = soot;
  g.fillRect(0, 0, 128, 128);
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 4;
  return t;
}

export class GlassFX {
  // heightAt: (x, z) => groundY | null — shards land on the real terrain.
  constructor(scene, heightAt = null) {
    this.heightAt = heightAt;
    this.volume = 0.7; // kept in sync with the game's master volume
    this.listener = new THREE.Vector3(); // camera position, for hit distance

    // ---- tumbling shards ----
    // MeshStandard + the scene HDR environment: every spin catches a
    // different reflection, which is exactly what sells falling glass.
    this.shardMat = new THREE.MeshStandardMaterial({
      color: 0xc6dfef,
      metalness: 0.25,
      roughness: 0.06,
      transparent: true,
      opacity: 0.62,
      side: THREE.DoubleSide,
      depthWrite: false,
      envMapIntensity: 1.6,
    });
    this.shards = new THREE.InstancedMesh(makeShardGeo(), this.shardMat, SHARD_MAX);
    this.shards.count = 0;
    this.shards.frustumCulled = false;
    scene.add(this.shards);
    // one live record per instance slot; a slot is stolen when the ring wraps
    this.slots = new Array(SHARD_MAX).fill(null);
    this.sCursor = 0;
    this._live = 0;

    // ---- glints: additive sparks that ride the first instant of the burst ----
    this.kPos = new Float32Array(SPARK_MAX * 3);
    this.kLife = new Float32Array(SPARK_MAX).fill(1);
    this.kVel = new Float32Array(SPARK_MAX * 3);
    this.kCursor = 0;
    this.kGeo = new THREE.BufferGeometry();
    this.kGeo.setAttribute('position', new THREE.BufferAttribute(this.kPos, 3));
    this.kGeo.setAttribute('life', new THREE.BufferAttribute(this.kLife, 1));
    this.kMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      vertexShader: `
        attribute float life;
        varying float vLife;
        void main() {
          if (life >= 1.0) {
            gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
            gl_PointSize = 0.0;
            return;
          }
          vLife = life;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          gl_PointSize = (0.5 - 0.35 * life) * (330.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5)) * 2.0;
          if (d > 1.0) discard;
          float a = (1.0 - d) * (1.0 - vLife);
          if (a < 0.02) discard;
          gl_FragColor = vec4(vec3(0.85, 0.96, 1.1) * 2.2, a);
        }`,
    });
    this.glints = new THREE.Points(this.kGeo, this.kMat);
    this.glints.frustumCulled = false;
    scene.add(this.glints);

    // ---- blown-out pane decals ----
    this.paneMat = new THREE.MeshBasicMaterial({
      map: makeBrokenPaneTexture(),
      transparent: true,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -6,
      side: THREE.DoubleSide,
      toneMapped: false,
    });
    this.panes = new THREE.InstancedMesh(new THREE.PlaneGeometry(1, 1), this.paneMat, PANE_MAX);
    this.panes.count = 0;
    this.panes.frustumCulled = false;
    scene.add(this.panes);
    this.pCursor = 0;

    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._e = new THREE.Euler();
    this._v = new THREE.Vector3();
    this._s = new THREE.Vector3();
    this._zero = new THREE.Matrix4().scale(new THREE.Vector3(0, 0, 0));
  }

  // A pane bursts: shards spray along the facade normal, glints flash.
  // `power` scales speed and count (rocket blast vs single bullet).
  shatter(x, y, z, nx, nz, count = 14, power = 1) {
    for (let k = 0; k < count; k++) {
      const i = this.sCursor;
      this.sCursor = (this.sCursor + 1) % SHARD_MAX;
      if (!this.slots[i]) this._live++;
      const out = (1.5 + Math.random() * 5) * power;
      this.slots[i] = {
        x: x + (Math.random() - 0.5) * 1.4,
        y: y + (Math.random() - 0.5) * 1.6,
        z: z + (Math.random() - 0.5) * 1.4,
        vx: nx * out + (Math.random() - 0.5) * 3.5 * power,
        vy: (0.5 + Math.random() * 3.5) * power,
        vz: nz * out + (Math.random() - 0.5) * 3.5 * power,
        rx: Math.random() * 6.283,
        ry: Math.random() * 6.283,
        rz: Math.random() * 6.283,
        wx: (Math.random() - 0.5) * 14,
        wy: (Math.random() - 0.5) * 14,
        wz: (Math.random() - 0.5) * 14,
        s: (0.22 + Math.random() * 0.42) * (0.8 + power * 0.3),
        t: 0,
        dur: 3.2 + Math.random() * 2.2,
        rest: 0,
      };
      if (i + 1 > this.shards.count) this.shards.count = i + 1;
    }
    const glints = Math.min(26, 6 + count);
    for (let k = 0; k < glints; k++) {
      const i = this.kCursor;
      this.kCursor = (this.kCursor + 1) % SPARK_MAX;
      this.kPos[i * 3] = x + (Math.random() - 0.5) * 1.6;
      this.kPos[i * 3 + 1] = y + (Math.random() - 0.5) * 1.8;
      this.kPos[i * 3 + 2] = z + (Math.random() - 0.5) * 1.6;
      this.kLife[i] = 0.001;
      this.kVel[i * 3] = nx * (2 + Math.random() * 7) * power + (Math.random() - 0.5) * 4;
      this.kVel[i * 3 + 1] = 1 + Math.random() * 4;
      this.kVel[i * 3 + 2] = nz * (2 + Math.random() * 7) * power + (Math.random() - 0.5) * 4;
    }
  }

  // The black hole the glass leaves in the facade.
  stampPane(x, y, z, nx, nz, w = 1.7, h = 1.9) {
    const i = this.pCursor;
    this.pCursor = (this.pCursor + 1) % PANE_MAX;
    this._e.set(0, Math.atan2(nx, nz), 0);
    this._q.setFromEuler(this._e);
    this._m.compose(
      this._v.set(x + nx * 0.07, y, z + nz * 0.07),
      this._q,
      this._s.set(w * (0.9 + Math.random() * 0.2), h * (0.9 + Math.random() * 0.2), 1)
    );
    this.panes.setMatrixAt(i, this._m);
    this.panes.count = Math.max(this.panes.count, i + 1);
    this.panes.instanceMatrix.needsUpdate = true;
  }

  // Everything a dead window does at once.
  burstPane(x, y, z, nx, nz, power = 1) {
    this.shatter(x, y, z, nx, nz, power > 1 ? 12 : 8, power);
    this.stampPane(x, y, z, nx, nz);
  }

  // Blow a whole list of windows: CityMap hands back a flat
  // [x, y, z, nx, nz, ...] array of the panes it just stripped.
  burstList(list, power = 1.4, max = 46) {
    const n = Math.min(list.length / 5, max);
    // one shatter per event, not per pane — 40 overlapping bursts is noise
    const now = performance.now();
    if (now - (this._sndT ?? -1e9) > 90) {
      this._sndT = now;
      const d = this.listener.distanceTo(this._v.set(list[0], list[1], list[2]));
      glassBreak(d, this.volume, list.length > 60);
    }
    // spend the shard budget on the panes nearest the blast: the list comes
    // in distance order from CityMap
    for (let i = 0; i < n; i++) {
      const o = i * 5;
      this.burstPane(list[o], list[o + 1], list[o + 2], list[o + 3], list[o + 4], power);
    }
    // panes past the shard budget still lose their glass — decal only
    for (let i = n; i < list.length / 5; i++) {
      const o = i * 5;
      this.stampPane(list[o], list[o + 1], list[o + 2], list[o + 3], list[o + 4]);
    }
    return n;
  }

  update(dt) {
    // shards: ballistic, tumbling, then they lie on the pavement and fade
    if (this._live) {
      let live = 0;
      for (let i = 0; i < this.shards.count; i++) {
        const s = this.slots[i];
        if (!s) continue;
        s.t += dt;
        if (s.t >= s.dur) {
          this.slots[i] = null;
          this.shards.setMatrixAt(i, this._zero);
          continue;
        }
        live++;
        if (!s.rest) {
          s.vy -= GRAVITY * dt;
          s.x += s.vx * dt;
          s.y += s.vy * dt;
          s.z += s.vz * dt;
          s.rx += s.wx * dt;
          s.ry += s.wy * dt;
          s.rz += s.wz * dt;
          const gy = this.heightAt ? this.heightAt(s.x, s.z) : null;
          if (gy !== null && gy !== undefined && s.y <= gy + 0.05 && s.vy < 0) {
            // one skittering bounce, then it lies flat and glitters
            if (Math.abs(s.vy) > 4) {
              s.y = gy + 0.05;
              s.vy = -s.vy * 0.25;
              s.vx *= 0.55;
              s.vz *= 0.55;
              s.wx *= 0.4;
              s.wz *= 0.4;
            } else {
              s.y = gy + 0.04;
              s.rest = 1;
              s.rx = -Math.PI / 2;
              s.rz = 0;
              s.dur = Math.min(s.dur, s.t + 2.4); // pavement glass fades out
            }
          }
        }
        // shrink away over the last stretch instead of popping
        const fade = Math.min(1, (s.dur - s.t) / 0.6);
        this._e.set(s.rx, s.ry, s.rz);
        this._q.setFromEuler(this._e);
        this._m.compose(
          this._v.set(s.x, s.y, s.z),
          this._q,
          this._s.setScalar(s.s * fade)
        );
        this.shards.setMatrixAt(i, this._m);
      }
      this._live = live;
      this.shards.instanceMatrix.needsUpdate = true;
      if (!live) this.shards.count = 0;
    }

    // glints
    let alive = false;
    for (let i = 0; i < SPARK_MAX; i++) {
      if (this.kLife[i] >= 1) continue;
      alive = true;
      this.kLife[i] = Math.min(1, this.kLife[i] + dt / 0.55);
      this.kVel[i * 3 + 1] -= GRAVITY * dt;
      this.kPos[i * 3] += this.kVel[i * 3] * dt;
      this.kPos[i * 3 + 1] += this.kVel[i * 3 + 1] * dt;
      this.kPos[i * 3 + 2] += this.kVel[i * 3 + 2] * dt;
    }
    if (alive || this._kWasAlive) {
      this.kGeo.attributes.position.needsUpdate = true;
      this.kGeo.attributes.life.needsUpdate = true;
    }
    this._kWasAlive = alive;
  }

  dispose() {
    for (const o of [this.shards, this.glints, this.panes]) o.parent?.remove(o);
    this.shards.geometry.dispose();
    this.shardMat.dispose();
    this.kGeo.dispose();
    this.kMat.dispose();
    this.panes.geometry.dispose();
    this.paneMat.map?.dispose();
    this.paneMat.dispose();
  }
}
