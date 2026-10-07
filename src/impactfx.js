import * as THREE from 'three';
import {
  hitConcrete, hitMetal, hitSoft, hitWood, hitWater, hitGlassPane,
  ricochetWhine, casingDrop,
} from './audiofx.js';
import { classifySurface, surfaceOf } from './ballistics.js';

// ---------------------------------------------------------------------------
// WHAT A BULLET LEAVES BEHIND
// ---------------------------------------------------------------------------
// ballistics.js decides what happened; this file is everything you see and
// hear afterwards. One place, so the pistol and the plane's gun mark the world
// the same way and only the numbers differ.
//
// The decals are LAYERED, which is how shooters that hold up to a close look
// do it (Battlefield, Max Payne 3, RDR2):
//
//   * a soft spall halo — the patch of plaster/paint knocked off around the
//     strike, a palm's width for a 9 mm round, tinted to the material
//   * a small crisp pit on top of it — THE HOLE, the bullet's own 9 mm, drawn
//     at a scale where 9 mm is actually 9 mm
//   * stretched into an ellipse when the round came in at an angle, because a
//     glancing hit leaves a slit, not a dot
//   * a long smear instead, when the round skipped off
//   * a ragged exit hole on the far face of anything it went through
//
// Nothing here is 50 cm wide. A bullet hole you can see from across the street
// is the single thing that makes a shooter look fake up close.

// ---- decal layers ---------------------------------------------------------
// Ring-buffered InstancedMesh, per-instance NON-UNIFORM scale (so holes can be
// elliptical) and per-instance colour (so one layer serves brick, concrete,
// wood and steel).
class DecalLayer {
  constructor(scene, texture, max, { lift = 0.006, order = 0, opacity = 1 } = {}) {
    this.max = max;
    this.lift = lift;
    this.cursor = 0;
    this.mesh = new THREE.InstancedMesh(
      new THREE.PlaneGeometry(1, 1),
      new THREE.MeshBasicMaterial({
        map: texture,
        transparent: true,
        opacity,
        depthWrite: false,
        polygonOffset: true,
        polygonOffsetFactor: -6 - order * 2,
        polygonOffsetUnits: -6 - order * 2,
        side: THREE.DoubleSide,
      }),
      max
    );
    this.mesh.count = 0;
    this.mesh.renderOrder = 2 + order;
    this.mesh.frustumCulled = false;
    // start every instance black-free: instanceColor defaults to white
    this.mesh.instanceColor = new THREE.InstancedBufferAttribute(new Float32Array(max * 3).fill(1), 3);
    scene.add(this.mesh);
    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._p = new THREE.Vector3();
    this._s = new THREE.Vector3();
    this._x = new THREE.Vector3();
    this._y = new THREE.Vector3();
    this._z = new THREE.Vector3();
    this._b = new THREE.Matrix4();
    this._c = new THREE.Color();
  }

  // `along` is the in-plane direction the stretch runs in (the incoming flight
  // path flattened onto the surface). Without it the stamp is spun at random,
  // which is right for a square-on hit.
  stamp(point, normal, w, h, { along = null, tint = null, slide = 0 } = {}) {
    const i = this.cursor;
    this.cursor = (this.cursor + 1) % this.max;
    this._z.copy(normal).normalize();
    if (along) {
      // project the flight path into the surface plane
      this._x.copy(along).addScaledVector(this._z, -along.dot(this._z));
      if (this._x.lengthSq() < 1e-8) this._x.set(1, 0, 0);
      this._x.normalize();
    } else {
      const a = Math.random() * Math.PI * 2;
      this._x.set(Math.cos(a), Math.sin(a), 0);
      this._x.addScaledVector(this._z, -this._x.dot(this._z));
      if (this._x.lengthSq() < 1e-8) this._x.set(this._z.y, -this._z.x, 0);
      this._x.normalize();
    }
    this._y.crossVectors(this._z, this._x).normalize();
    this._b.makeBasis(this._x, this._y, this._z);
    this._q.setFromRotationMatrix(this._b);
    this._p.copy(point).addScaledVector(this._z, this.lift);
    if (slide) this._p.addScaledVector(this._x, slide * w);
    this._m.compose(this._p, this._q, this._s.set(w, h, 1));
    this.mesh.setMatrixAt(i, this._m);
    if (tint !== null) {
      this._c.set(tint);
      this.mesh.setColorAt(i, this._c);
      this.mesh.instanceColor.needsUpdate = true;
    }
    this.mesh.count = Math.max(this.mesh.count, i + 1);
    this.mesh.instanceMatrix.needsUpdate = true;
  }

  dispose() {
    this.mesh.parent?.remove(this.mesh);
    this.mesh.geometry.dispose();
    this.mesh.material.map?.dispose();
    this.mesh.material.dispose();
  }
}

// ---- the textures ---------------------------------------------------------
// Drawn so the FEATURE fills the canvas: the quad is then sized in metres by
// the caller and a 9 mm hole comes out 9 mm across.

// The hole itself: a dark pit with a bright crushed lip and a lead smear.
// Deliberately irregular — a drilled circle reads as a sticker.
function pitTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 96;
  const g = c.getContext('2d');
  const R = 30;
  g.translate(48, 48);
  // the crushed, lightened lip of material pushed out around the hole
  g.beginPath();
  for (let k = 0; k <= 28; k++) {
    const a = (k / 28) * Math.PI * 2;
    const r = R * (1.28 + Math.random() * 0.3);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(a) * r, Math.sin(a) * r);
  }
  g.closePath();
  const lip = g.createRadialGradient(0, 0, R * 0.8, 0, 0, R * 1.55);
  lip.addColorStop(0, 'rgba(226,220,208,0.55)');
  lip.addColorStop(1, 'rgba(226,220,208,0)');
  g.fillStyle = lip;
  g.fill();
  // the pit — black in the middle because you are looking into a hole
  g.beginPath();
  for (let k = 0; k <= 24; k++) {
    const a = (k / 24) * Math.PI * 2;
    const r = R * (0.86 + Math.random() * 0.18);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(a) * r, Math.sin(a) * r);
  }
  g.closePath();
  const pit = g.createRadialGradient(-R * 0.1, -R * 0.1, 0, 0, 0, R);
  pit.addColorStop(0, 'rgba(4,4,5,1)');
  pit.addColorStop(0.62, 'rgba(14,13,13,0.98)');
  pit.addColorStop(0.9, 'rgba(38,34,31,0.9)');
  pit.addColorStop(1, 'rgba(52,47,42,0.35)');
  g.fillStyle = pit;
  g.fill();
  // grey copper-and-lead wash the jacket leaves on the rim
  g.strokeStyle = 'rgba(120,112,100,0.45)';
  g.lineWidth = 2.2;
  g.stroke();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 8;
  return t;
}

// The spall halo: the patch of plaster, paint and dust the strike knocks off.
// Soft, mottled, no hard edge, with a few radial chips.
function spallTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  g.translate(64, 64);
  // base bloom of pulverised material
  const base = g.createRadialGradient(0, 0, 4, 0, 0, 60);
  base.addColorStop(0, 'rgba(255,255,255,0.72)');
  base.addColorStop(0.3, 'rgba(255,255,255,0.34)');
  base.addColorStop(0.68, 'rgba(255,255,255,0.12)');
  base.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = base;
  g.beginPath();
  g.arc(0, 0, 62, 0, 7);
  g.fill();
  // flaked chips, denser near the middle
  for (let k = 0; k < 90; k++) {
    const a = Math.random() * Math.PI * 2;
    const r = 6 + Math.pow(Math.random(), 0.6) * 52;
    g.fillStyle = `rgba(255,255,255,${0.1 + Math.random() * 0.3})`;
    g.beginPath();
    g.ellipse(Math.cos(a) * r, Math.sin(a) * r,
      1 + Math.random() * 3.5, 0.8 + Math.random() * 2.2, a, 0, 7);
    g.fill();
  }
  // short radial cracks running out of the strike
  for (let k = 0; k < 7; k++) {
    const a = Math.random() * Math.PI * 2;
    const len = 18 + Math.random() * 34;
    g.strokeStyle = `rgba(90,86,80,${0.18 + Math.random() * 0.2})`;
    g.lineWidth = 0.8 + Math.random();
    g.beginPath();
    g.moveTo(Math.cos(a) * 7, Math.sin(a) * 7);
    g.lineTo(Math.cos(a + 0.12) * len, Math.sin(a + 0.12) * len);
    g.stroke();
  }
  // a shadow immediately around the hole, so the pit sits in a dish
  const dish = g.createRadialGradient(0, 0, 2, 0, 0, 18);
  dish.addColorStop(0, 'rgba(40,36,32,0.55)');
  dish.addColorStop(1, 'rgba(40,36,32,0)');
  g.fillStyle = dish;
  g.beginPath();
  g.arc(0, 0, 18, 0, 7);
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 8;
  return t;
}

// The far side of something the round went through: bigger than the entry,
// ragged, with the material blown OUTWARD into a petalled lip.
function exitTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 96;
  const g = c.getContext('2d');
  g.translate(48, 48);
  const R = 26;
  // torn petals pushed out by the round
  for (let k = 0; k < 11; k++) {
    const a = (k / 11) * Math.PI * 2 + Math.random() * 0.3;
    const len = R * (1.2 + Math.random() * 0.9);
    g.fillStyle = `rgba(225,217,203,${0.3 + Math.random() * 0.35})`;
    g.beginPath();
    g.moveTo(Math.cos(a - 0.26) * R * 0.8, Math.sin(a - 0.26) * R * 0.8);
    g.lineTo(Math.cos(a) * len, Math.sin(a) * len);
    g.lineTo(Math.cos(a + 0.26) * R * 0.8, Math.sin(a + 0.26) * R * 0.8);
    g.closePath();
    g.fill();
  }
  g.beginPath();
  for (let k = 0; k <= 22; k++) {
    const a = (k / 22) * Math.PI * 2;
    const r = R * (0.78 + Math.random() * 0.34);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(a) * r, Math.sin(a) * r);
  }
  g.closePath();
  const pit = g.createRadialGradient(0, 0, 0, 0, 0, R);
  pit.addColorStop(0, 'rgba(5,5,6,1)');
  pit.addColorStop(0.8, 'rgba(20,18,17,0.95)');
  pit.addColorStop(1, 'rgba(44,40,36,0.4)');
  g.fillStyle = pit;
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 8;
  return t;
}

// A ricochet mark: the round arrived almost flat and PLOUGHED before leaving.
// Drawn as a streak running left to right, deepest where it bit, fading out
// where it lifted off; the caller scales it long and thin along the flight path.
function scuffTexture() {
  const c = document.createElement('canvas');
  c.width = 160;
  c.height = 64;
  const g = c.getContext('2d');
  const y = 32;
  // the ploughed groove: it bites at x~34 and lifts away to the right
  for (let k = 0; k < 170; k++) {
    const t = Math.pow(Math.random(), 0.7);
    const x = 30 + t * 124;
    const w = (1 - t) * 7 + 1.2;
    g.fillStyle = `rgba(${22 + t * 40},${20 + t * 38},${18 + t * 34},${0.5 * (1 - t * 0.8)})`;
    g.beginPath();
    g.ellipse(x, y + (Math.random() - 0.5) * w * 1.6, 2 + Math.random() * 5, w * 0.5, 0, 0, 7);
    g.fill();
  }
  // bright pulverised material thrown forward and sideways
  for (let k = 0; k < 150; k++) {
    const t = Math.pow(Math.random(), 0.5);
    const x = 32 + t * 126;
    g.fillStyle = `rgba(240,234,222,${0.08 + Math.random() * 0.26 * (1 - t * 0.6)})`;
    g.beginPath();
    g.ellipse(x, y + (Math.random() - 0.5) * (6 + t * 26),
      1 + Math.random() * 3, 0.7 + Math.random() * 1.6, 0, 0, 7);
    g.fill();
  }
  // the bite itself — a short dark gouge where it first touched
  const bite = g.createRadialGradient(34, y, 0, 34, y, 13);
  bite.addColorStop(0, 'rgba(6,6,7,0.9)');
  bite.addColorStop(1, 'rgba(6,6,7,0)');
  g.fillStyle = bite;
  g.beginPath();
  g.ellipse(34, y, 13, 7, 0, 0, 7);
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 8;
  return t;
}

// A bullet through a pane. Real glass does not make a snowflake: a few long
// radial cracks run out from the hole at UNEVEN angles and uneven lengths, a
// couple of lateral cracks join them, and there is a small ring of crushed
// white glass round the hole itself. Thin lines, low contrast, no symmetry --
// a tidy star reads as a decal, a ragged one reads as damage.
function starTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 192;
  const g = c.getContext('2d');
  g.translate(96, 96);
  g.lineCap = 'round';
  const R = 86;
  // radial cracks: 7-10 of them, each wandering, each a different length
  const n = 7 + ((Math.random() * 4) | 0);
  const ends = [];
  let a = Math.random() * Math.PI * 2;
  for (let k = 0; k < n; k++) {
    a += (Math.PI * 2) / n * (0.55 + Math.random() * 0.9);
    const len = R * (0.35 + Math.random() * 0.62);
    g.strokeStyle = `rgba(242,250,255,${0.3 + Math.random() * 0.33})`;
    g.lineWidth = 0.7 + Math.random() * 0.9;
    g.beginPath();
    g.moveTo(Math.cos(a) * 5, Math.sin(a) * 5);
    let x = Math.cos(a) * 5;
    let y = Math.sin(a) * 5;
    const steps = 3 + ((Math.random() * 3) | 0);
    let aa = a;
    for (let q = 1; q <= steps; q++) {
      aa += (Math.random() - 0.5) * 0.3;        // the crack wanders as it runs
      const r = 5 + (len - 5) * (q / steps);
      x = Math.cos(aa) * r;
      y = Math.sin(aa) * r;
      g.lineTo(x, y);
    }
    g.stroke();
    ends.push([x, y, aa, len]);
  }
  // lateral cracks joining neighbouring radials part of the way out -- this is
  // what makes broken glass read as a WEB rather than a star
  for (let k = 0; k < ends.length; k++) {
    const [, , a1, l1] = ends[k];
    const [, , a2, l2] = ends[(k + 1) % ends.length];
    if (Math.random() < 0.35) continue;
    const t = 0.3 + Math.random() * 0.45;
    g.strokeStyle = `rgba(232,244,252,${0.16 + Math.random() * 0.2})`;
    g.lineWidth = 0.6;
    g.beginPath();
    g.moveTo(Math.cos(a1) * l1 * t, Math.sin(a1) * l1 * t);
    const mt = t * (0.88 + Math.random() * 0.2);
    g.quadraticCurveTo(
      Math.cos((a1 + a2) / 2) * l1 * mt * 0.8, Math.sin((a1 + a2) / 2) * l1 * mt * 0.8,
      Math.cos(a2) * l2 * t, Math.sin(a2) * l2 * t
    );
    g.stroke();
  }
  // the crushed white cone right around the hole, then the hole
  const cone = g.createRadialGradient(0, 0, 1.5, 0, 0, 11);
  cone.addColorStop(0, 'rgba(246,252,255,0.8)');
  cone.addColorStop(0.55, 'rgba(240,249,255,0.35)');
  cone.addColorStop(1, 'rgba(240,249,255,0)');
  g.fillStyle = cone;
  g.beginPath();
  g.arc(0, 0, 11, 0, 7);
  g.fill();
  g.fillStyle = 'rgba(10,12,15,0.92)';
  g.beginPath();
  for (let k = 0; k <= 12; k++) {
    const t = (k / 12) * Math.PI * 2;
    const r = 4.2 * (0.82 + Math.random() * 0.36);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(t) * r, Math.sin(t) * r);
  }
  g.closePath();
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 8;
  return t;
}

// ---- particles ------------------------------------------------------------
// One system, used for every kind of debris: concrete dust, brick powder, wood
// splinters, dirt, torn leaves, water droplets, sparks. Per-particle colour,
// size in METRES, per-particle gravity and drag, so a dust puff hangs and a
// droplet falls.
const P_MAX = 1400;

class ParticleCloud {
  constructor(scene, { max = P_MAX, blending = THREE.NormalBlending, glow = false } = {}) {
    this.max = max;
    this.cursor = 0;
    this.pos = new Float32Array(max * 3);
    this.vel = new Float32Array(max * 3);
    this.tint = new Float32Array(max * 3);
    this.life = new Float32Array(max).fill(1);
    this.size = new Float32Array(max);
    this.grav = new Float32Array(max);
    this.drag = new Float32Array(max);
    this.span = new Float32Array(max).fill(1);
    this.geo = new THREE.BufferGeometry();
    this.geo.setAttribute('position', new THREE.BufferAttribute(this.pos, 3));
    this.geo.setAttribute('life', new THREE.BufferAttribute(this.life, 1));
    this.geo.setAttribute('tint', new THREE.BufferAttribute(this.tint, 3));
    this.geo.setAttribute('psize', new THREE.BufferAttribute(this.size, 1));
    this.uPx = { value: 600 };
    this.mat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      blending,
      uniforms: { uPx: this.uPx },
      vertexShader: `
        attribute float life;
        attribute float psize;
        attribute vec3 tint;
        uniform float uPx;
        varying float vLife;
        varying vec3 vTint;
        void main() {
          if (life >= 1.0) {
            gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
            gl_PointSize = 0.0;
            return;
          }
          vLife = life;
          vTint = tint;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          // size in METRES: a 9 mm strike throws a puff the width of a hand,
          // not the width of a car
          float m = psize * (0.45 + life * 1.6);
          gl_PointSize = clamp(m * uPx / max(0.25, -mv.z), 1.0, 220.0);
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        varying vec3 vTint;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5));
          float a = smoothstep(0.5, ${glow ? '0.05' : '0.12'}, d) * (1.0 - vLife);
          if (a < 0.012) discard;
          gl_FragColor = vec4(vTint ${glow ? '* (1.6 + 2.2 * (1.0 - vLife))' : ''}, a${glow ? '' : ' * 0.85'});
        }`,
    });
    this.points = new THREE.Points(this.geo, this.mat);
    this.points.frustumCulled = false;
    this.points.renderOrder = 5;
    scene.add(this.points);
    this._c = new THREE.Color();
  }

  emit(p, n, {
    tint = 0x9c968c, speed = 3, spread = 0.1, up = 1, size = 0.05, life = 0.8,
    grav = 6, drag = 2.5, dir = null, cone = 1,
  } = {}) {
    this._c.set(tint);
    for (let k = 0; k < n; k++) {
      const i = this.cursor;
      this.cursor = (this.cursor + 1) % this.max;
      this.pos[i * 3] = p.x + (Math.random() - 0.5) * spread;
      this.pos[i * 3 + 1] = p.y + (Math.random() - 0.5) * spread;
      this.pos[i * 3 + 2] = p.z + (Math.random() - 0.5) * spread;
      // a cone around `dir` (the surface normal, or the ricochet path)
      let vx = (Math.random() - 0.5) * 2;
      let vy = (Math.random() - 0.5) * 2;
      let vz = (Math.random() - 0.5) * 2;
      if (dir) {
        vx = dir.x * cone + vx * 0.8;
        vy = dir.y * cone + vy * 0.8;
        vz = dir.z * cone + vz * 0.8;
      }
      const s = speed * (0.35 + Math.random() * 0.9);
      const l = Math.hypot(vx, vy, vz) || 1;
      this.vel[i * 3] = (vx / l) * s;
      this.vel[i * 3 + 1] = (vy / l) * s + up * (0.4 + Math.random() * 0.8);
      this.vel[i * 3 + 2] = (vz / l) * s;
      this.tint[i * 3] = this._c.r;
      this.tint[i * 3 + 1] = this._c.g;
      this.tint[i * 3 + 2] = this._c.b;
      this.size[i] = size * (0.5 + Math.random());
      this.grav[i] = grav;
      this.drag[i] = drag;
      this.span[i] = life * (0.7 + Math.random() * 0.6);
      this.life[i] = 0.001;
    }
  }

  update(dt, pxPerUnit) {
    if (pxPerUnit) this.uPx.value = pxPerUnit;
    let alive = false;
    for (let i = 0; i < this.max; i++) {
      if (this.life[i] >= 1) continue;
      alive = true;
      this.life[i] = Math.min(1, this.life[i] + dt / this.span[i]);
      const d = Math.exp(-this.drag[i] * dt);
      this.vel[i * 3] *= d;
      this.vel[i * 3 + 1] = this.vel[i * 3 + 1] * d - this.grav[i] * dt;
      this.vel[i * 3 + 2] *= d;
      this.pos[i * 3] += this.vel[i * 3] * dt;
      this.pos[i * 3 + 1] += this.vel[i * 3 + 1] * dt;
      this.pos[i * 3 + 2] += this.vel[i * 3 + 2] * dt;
    }
    if (alive || this._was) {
      this.geo.attributes.position.needsUpdate = true;
      this.geo.attributes.life.needsUpdate = true;
      this.geo.attributes.tint.needsUpdate = true;
      this.geo.attributes.psize.needsUpdate = true;
    }
    this._was = alive;
  }

  dispose() {
    this.points.parent?.remove(this.points);
    this.geo.dispose();
    this.mat.dispose();
  }
}

// ---- spent brass ----------------------------------------------------------
// A real detail that costs almost nothing and sells the weapon: the case the
// gun just ejected, at its real size (9x18 and 9x19 mean an 18 and a 19 mm
// case), spinning out of the port, bouncing off the pavement, and lying there.
const CASE_MAX = 36;
const CASE_REST = 18;   // s a case lies on the ground before it is recycled

class Casings {
  constructor(game) {
    this.game = game;
    this.list = [];
    const geo = new THREE.CylinderGeometry(0.0048, 0.0045, 1, 7, 1, false);
    geo.translate(0, 0.5, 0);   // length runs 0..1 along +Y, scaled per case
    this.mesh = new THREE.InstancedMesh(
      geo,
      // Brass that still reads as brass in a shaded street. At metalness 0.85
      // a 2 cm case takes almost all its colour from the environment, and down
      // between the buildings that is nearly black -- the cases were on the
      // pavement, correctly placed, and invisible. Half-metal with a warm base
      // colour and a whisper of emissive keeps them glinting in shadow.
      new THREE.MeshStandardMaterial({
        color: 0xe0ad4e,
        metalness: 0.45,
        roughness: 0.3,
        emissive: 0x3a2808,
        envMapIntensity: 1.6,
      }),
      CASE_MAX
    );
    this.mesh.count = 0;
    this.mesh.frustumCulled = false;
    game.scene.add(this.mesh);
    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._e = new THREE.Euler();
    this._s = new THREE.Vector3();
  }

  eject(pos, vel, lengthMm) {
    if (this.list.length >= CASE_MAX) this.list.shift();
    // Where the pavement is, measured once: a case is thrown two metres and the
    // ground under it does not change, so probing it every frame for every case
    // buys nothing (and in Google-tiles mode it is a raycast).
    const gy = this.game._groundAt?.(pos.x, pos.z, pos.y + 1.6, 1.8);
    this.list.push({
      gy: gy ?? null,
      px: pos.x,
      pz: pos.z,
      p: pos.clone(),
      v: vel.clone(),
      rot: new THREE.Euler(Math.random() * 6.28, Math.random() * 6.28, Math.random() * 6.28),
      spin: new THREE.Vector3(
        (Math.random() - 0.5) * 34, (Math.random() - 0.5) * 24, (Math.random() - 0.5) * 34
      ),
      len: lengthMm / 1000,
      rest: 0,
      bounces: 0,
      t: 0,
    });
  }

  update(dt) {
    const g = this.game;
    for (let i = this.list.length - 1; i >= 0; i--) {
      const c = this.list[i];
      c.t += dt;
      if (c.rest > 0) {
        c.rest -= dt;
        if (c.rest <= 0) { this.list.splice(i, 1); continue; }
        continue;
      }
      c.v.y -= 19 * dt;
      c.p.addScaledVector(c.v, dt);
      // Re-probe once it has travelled far enough for the ground to have
      // changed under it. Probing every frame is wasteful (in Google-tiles mode
      // it is a raycast); probing only at the muzzle buried the case 9 cm into
      // the kerb whenever it landed on a slope a couple of metres away.
      if ((c.p.x - c.px) ** 2 + (c.p.z - c.pz) ** 2 > 0.36) {
        c.px = c.p.x;
        c.pz = c.p.z;
        const gy = g._groundAt?.(c.p.x, c.p.z, c.p.y + 1.6, 1.8);
        if (gy !== null && gy !== undefined) c.gy = gy;
      }
      c.rot.x += c.spin.x * dt;
      c.rot.y += c.spin.y * dt;
      c.rot.z += c.spin.z * dt;
      if (c.gy !== null && c.p.y <= c.gy + 0.006) {
        c.p.y = c.gy + 0.006;
        if (c.bounces === 0) casingDrop(g.camera.position.distanceTo(c.p), g.sound?.volume ?? 0.7);
        c.bounces++;
        if (c.bounces > 2 || c.v.length() < 0.7) {
          // settled: lie flat on the ground and wait to be recycled
          c.rot.set(Math.PI / 2, Math.random() * 6.28, 0);
          c.spin.set(0, 0, 0);
          c.v.set(0, 0, 0);
          c.rest = CASE_REST;
        } else {
          c.v.y = Math.abs(c.v.y) * 0.34;
          c.v.x *= 0.6;
          c.v.z *= 0.6;
          c.spin.multiplyScalar(0.5);
        }
      } else if (c.t > 6) {
        this.list.splice(i, 1);   // thrown off a roof — gone
      }
    }
    // Written in a second pass: splicing above shuffles the indices, and
    // writing as we went left the last case drawn twice for a frame.
    for (let i = 0; i < this.list.length; i++) {
      const c = this.list[i];
      this._q.setFromEuler(c.rot);
      this._m.compose(c.p, this._q, this._s.set(1, c.len, 1));
      this.mesh.setMatrixAt(i, this._m);
    }
    this.mesh.count = this.list.length;
    if (this.mesh.count || this._was) this.mesh.instanceMatrix.needsUpdate = true;
    this._was = this.mesh.count > 0;
  }

  dispose() {
    this.mesh.parent?.remove(this.mesh);
    this.mesh.geometry.dispose();
    this.mesh.material.dispose();
    this.list.length = 0;
  }
}

// ---------------------------------------------------------------------------
// ImpactFX — the one place a ballistic event becomes damage, marks, debris
// and a noise.
// ---------------------------------------------------------------------------
export class ImpactFX {
  constructor(game) {
    this.game = game;
    const s = game.scene;
    this.spall = new DecalLayer(s, spallTexture(), 1400, { lift: 0.005, order: 0, opacity: 0.85 });
    this.pits = new DecalLayer(s, pitTexture(), 1400, { lift: 0.008, order: 1 });
    this.exits = new DecalLayer(s, exitTexture(), 500, { lift: 0.006, order: 1 });
    this.scuffs = new DecalLayer(s, scuffTexture(), 400, { lift: 0.006, order: 1, opacity: 0.9 });
    this.stars = new DecalLayer(s, starTexture(), 500, { lift: 0.006, order: 1 });
    this.debris = new ParticleCloud(s, { max: 1400 });
    this.sparks = new ParticleCloud(s, { max: 420, blending: THREE.AdditiveBlending, glow: true });
    this.casings = new Casings(game);
    // how many rounds a given pane has taken — annealed glass survives a hole
    // or two before the whole sheet gives up and falls out
    this._paneHits = new Map();
    this._v = new THREE.Vector3();
    this._v2 = new THREE.Vector3();
  }

  // ---- the entry point ---------------------------------------------------
  resolve(info) {
    const g = this.game;
    const { S, surface, hit, point, normal, dir } = info;
    const dist = g.camera.position.distanceTo(point);
    const vol = g.sound?.volume ?? 0.7;
    const cal = info.cart.bullet / 1000;      // the bullet's diameter, in metres

    // Damage goes in first: the city hands back which building this was, which
    // is what the address toast and the window logic need.
    const res = g.world.damageHit?.(hit, info.damage) ?? null;

    // PAPER OWNS ITS OWN HOLE. A target card takes dozens of them and each one
    // has to be in the right place to a millimetre to be worth anything, so it
    // paints them into its own face instead of taking a decal quad each
    // (src/papertargets.js) -- and the round carries straight on to the
    // backstop, as it does through real paper.
    if (surface === 'paper') {
      g.paper?.hit(hit.object, point, dir, info.cart, hit.uv ?? null);
      // ...AND THE SHEET SWINGS. The card records where the round went; the
      // paper it is printed on is a thing hanging off a hanger, and a hanging
      // target moves when you put a round through it. Scaled by the round's
      // momentum, because that is what there is to give -- the bullet goes
      // straight through and the sheet keeps the little it managed to take.
      const c = info.cart;
      g.lanes?.hit(hit.object, point, c ? (c.mass * c.v0) / 2000 : 1);
      this.debris.emit(point, 4, {
        tint: 0xe8e4da, dir: this._v.copy(dir), cone: 1.1, speed: 1.8,
        size: cal * 1.6, up: 0.4, life: 0.5, grav: 4,
      });
      this._sound('soft', dist, vol, info);
      return null;
    }
    if (surface === 'glass') this._glass(info, res, dist, vol);
    else if (S.liquid) this._water(info, dist, vol);
    else if (info.ricochet) this._ricochet(info, cal, dist, vol);
    else this._strike(info, cal, dist, vol, res);

    // The far side of anything it went through gets its own, rougher hole.
    if (info.through && info.exit && !S.noHole && surface !== 'glass') {
      this.exits.stamp(info.exit, this._v.copy(normal).negate(), cal * 1.7, cal * 1.7,
        { tint: S.dust.tint });
      this.debris.emit(info.exit, Math.ceil(S.dust.n * 0.6), {
        tint: S.dust.tint, dir: this._v2.copy(dir), cone: 1.4,
        speed: S.dust.speed * 1.2, size: cal * 1.4, up: 0.3, life: 0.5, grav: 9,
      });
    }

    // Building damage stages: the stage puff and the address toast are the
    // plane gun's, reused so the pistol reports hits the same way.
    if (res) {
      g.guns?._showToast?.(res);
      if (res.changed && !res.destroyed) {
        this.debris.emit(this._v.set(res.cx, res.topNow, res.cz),
          16, { tint: 0x9a9388, speed: 4, spread: Math.max(2, res.span * 0.4),
            size: 0.9, life: 1.8, grav: 3, up: 1.5 });
      }
    }
    return res;
  }

  // A hit that did NOT come through the sim. The gun under the plane is still
  // hitscan with a visual tracer, and it has its own damage and collapse logic,
  // so it comes in here for the marks, the debris and the right noise only --
  // which is how a 12.7 mm round stops leaving the same crater a rocket does.
  markHit(hit, point, normal, dir, cart, { sound = true } = {}) {
    const surface = classifySurface(hit, this.game.world);
    const S = surfaceOf(surface);
    const cal = cart.bullet / 1000;
    const cosI = Math.max(0.05, Math.min(1, Math.abs(normal.dot(dir))));
    const info = {
      S, surface, point, normal, dir, cart,
      energy: cart.energy, graze: Math.asin(cosI), through: false, exit: null,
      holeW: cal * S.hole,
      holeL: (cal * S.hole) / cosI,
    };
    const dist = this.game.camera.position.distanceTo(point);
    const vol = sound ? (this.game.sound?.volume ?? 0.7) : 0;
    if (S.liquid) this._water(info, dist, vol);
    else this._strike(info, cal, dist, vol, null);
    return surface;
  }

  // A round that bit and stayed, or punched through: pit + spall + dust.
  _strike(info, cal, dist, vol, res) {
    const { S, point, normal, dir, surface } = info;
    if (!S.noHole) {
      // The spall patch first, tinted to the material, then THE HOLE on top of
      // it at the bullet's own diameter — stretched along the flight path when
      // the round came in at a slant.
      const halo = cal * S.spall * (0.8 + Math.random() * 0.45);
      this.spall.stamp(point, normal, halo, halo * (0.8 + Math.random() * 0.4),
        { tint: S.dust.tint, along: null });
      this.pits.stamp(point, normal, info.holeL * 1.7, info.holeW * 1.7,
        { along: dir, tint: surface === 'metal' ? 0xc9ccd2 : 0xffffff });
    }
    const d = S.dust;
    this.debris.emit(point, d.n, {
      tint: d.tint, dir: normal, cone: 0.9, speed: d.speed,
      size: cal * (S.splinter ? 2.6 : 1.8), up: d.up ?? 0.6,
      life: S.splinter ? 0.7 : 0.85, grav: S.splinter ? 11 : 6, spread: cal * 2,
    });
    if (S.sparks) {
      this.sparks.emit(point, S.sparks, {
        tint: 0xffcf7a, dir: normal, cone: 0.7, speed: 7, size: 0.012,
        life: 0.3, grav: 16, drag: 1.2, up: 0.4,
      });
    }
    this._sound(S.sound, dist, vol, info);
  }

  // Skipped off: a long ploughed smear, dust thrown FORWARD along the new
  // path, and the whine of a round tumbling away down the street.
  _ricochet(info, cal, dist, vol) {
    const { S, point, normal, dir } = info;
    const len = cal * (14 + 26 * (1 - info.graze / Math.max(0.05, S.graze)));
    // the texture's bite sits ~0.29 of the way along the groove, so slide the
    // stamp forward to put that bite exactly where the round touched
    this.scuffs.stamp(point, normal, len, cal * 2.6,
      { along: dir, tint: S.dust.tint, slide: 0.29 });
    this.debris.emit(point, Math.ceil(S.dust.n * 0.8), {
      tint: S.dust.tint, dir: this._v.copy(dir).reflect(normal), cone: 1.6,
      speed: S.dust.speed * 1.5, size: cal * 1.6, up: 0.3, life: 0.6, grav: 7,
    });
    if (S.sparks || S.sound === 'concrete') {
      this.sparks.emit(point, S.sparks ? S.sparks : 5, {
        tint: 0xffd089, dir: this._v.copy(dir).reflect(normal), cone: 1.8,
        speed: 9, size: 0.01, life: 0.28, grav: 16, drag: 1,
      });
    }
    this._sound(S.sound, dist, vol, info);
    ricochetWhine(dist, vol, info.energy);
  }

  // Into the pane. One round leaves a hole and a crack star; the pane itself
  // only gives up and falls out after it has taken a few.
  _glass(info, res, dist, vol) {
    const g = this.game;
    const { point, normal, cart } = info;
    const cal = cart.bullet / 1000;
    const web = cal * (11 + Math.random() * 5);
    this.stars.stamp(point, normal, web, web * (0.85 + Math.random() * 0.3),
      { tint: 0xffffff });
    this.debris.emit(point, 7, {
      tint: 0xd8eef8, dir: this._v.copy(info.dir), cone: 1.3, speed: 3.4,
      size: cal * 1.2, up: 0.2, life: 0.55, grav: 15,
    });
    this.sparks.emit(point, 8, {
      tint: 0xbfe6ff, dir: this._v.copy(info.dir), cone: 1.2, speed: 4.5,
      size: 0.008, life: 0.45, grav: 17, drag: 0.8,
    });
    hitGlassPane(dist, vol);
    // Three holes in one sheet and it comes out of the frame for good.
    const key = `${Math.round(point.x * 1.6)},${Math.round(point.y * 1.6)},${Math.round(point.z * 1.6)}`;
    const n = (this._paneHits.get(key) ?? 0) + 1;
    this._paneHits.set(key, n);
    if (this._paneHits.size > 400) this._paneHits.clear();
    if (n >= 3) {
      const blown = g.world.blastWindows?.(point.x, point.y, point.z, 0.9);
      if (blown?.length) g.glass?.burstList(blown, 0.7, 2);
      this._paneHits.delete(key);
    }
  }

  // Into the river: a column of droplets straight up, no hole, no bounce.
  _water(info, dist, vol) {
    const { point, S } = info;
    this.debris.emit(point, S.dust.n, {
      tint: S.dust.tint, dir: this._v.set(0, 1, 0), cone: 1.4, speed: 3.5,
      size: 0.05, up: S.dust.up ?? 4, life: 0.75, grav: 15, drag: 0.6, spread: 0.08,
    });
    this.sparks.emit(point, 6, {
      tint: 0xdff2ff, dir: this._v.set(0, 1, 0), cone: 1.6, speed: 4,
      size: 0.02, life: 0.5, grav: 16, drag: 0.6,
    });
    if (vol) hitWater(dist, vol);
  }

  _sound(kind, dist, vol, info) {
    if (!vol) return;
    // A round that is nearly spent lands with a tap, not a crack.
    const v = vol * Math.min(1, 0.35 + info.energy / 420);
    if (kind === 'metal') hitMetal(dist, v);
    else if (kind === 'wood') hitWood(dist, v);
    else if (kind === 'soft') hitSoft(dist, v);
    else if (kind === 'glass') hitGlassPane(dist, v);
    else if (kind === 'water') hitWater(dist, v);
    else hitConcrete(dist, v);
  }

  update(dt) {
    const g = this.game;
    // Points are sized in metres, so the shader needs pixels-per-metre at one
    // metre: half the canvas height over tan(half the vertical fov).
    const h = g.renderer?.domElement?.height ?? 1080;
    const px = (h * 0.5) / Math.tan((g.camera.fov * Math.PI) / 360);
    this.debris.update(dt, px);
    this.sparks.update(dt, px);
    this.casings.update(dt);
  }

  dispose() {
    this.spall.dispose();
    this.pits.dispose();
    this.exits.dispose();
    this.scuffs.dispose();
    this.stars.dispose();
    this.debris.dispose();
    this.sparks.dispose();
    this.casings.dispose();
    this._paneHits.clear();
  }
}
