import * as THREE from 'three';
import { fxAudio, glassBreak, collapseRumble } from './audiofx.js';
import { CARTRIDGES } from './ballistics.js';

// Belly-mounted machine gun — hold Space to fire. Hitscan rounds with visual
// tracers: the raycast happens at the muzzle, the damage lands when the
// tracer arrives at the wall. Buildings take damage through CityMap's
// 10-stage crumble system; the hit toast shows the real street address.
//
// What it LEAVES is shared with the pistol (impactfx.js), so the city is marked
// consistently whichever gun did it — a 12.7 mm hole in a hand-sized patch of
// knocked-off material, read off the surface it landed on.
const RATE = 12; // rounds per second
const RANGE = 1100; // m
const BULLET_SPEED = 850; // m/s
const DAMAGE = 14; // hp per round (buildings carry 280–3200 hp)
// ...and AGAINST AN AEROPLANE, which is the one thing a 12.7 mm belt gun was
// actually built to shoot at. A building absorbs a heavy round into 30 cm of
// masonry; an airframe does not, so the belly gun is the real anti-aircraft
// weapon in the game -- about 21 rounds, a second and a half of fire, to
// empty a Spitfire's 987 hull points. Still an order below ramming one
// (1 462 points, a one-shot kill: planes.js planeCrashDmg), which is the
// order the two have to stay in.
const PLANE_DAMAGE = 48;
// The gun's cone. 6 mrad put a burst a metre and a half apart at 250 m, which
// is wider than a heavy machine gun really shoots (2-4 mrad) and, more to the
// point, wider than anything can be cut with: rounds that land half a metre
// apart have unbroken metal between them (src/planedamage.js). 3 mrad is both
// the honest figure and the one that lets a strafing burst walk a line.
const SPREAD = 0.003; // rad
const MAX_TRACERS = 48;
const CART = CARTRIDGES['12.7x108']; // the round in the belt

// Ring-buffered decal layer: bullet holes / glass cracks stamped flat onto
// whatever surface was hit, oriented by the hit normal. Oldest decal is
// recycled when the buffer wraps — no cleanup pass needed.
class DecalLayer {
  constructor(scene, texture, size, max) {
    this.max = max;
    this.cursor = 0;
    this.mesh = new THREE.InstancedMesh(
      new THREE.PlaneGeometry(size, size),
      new THREE.MeshBasicMaterial({
        map: texture,
        transparent: true,
        depthWrite: false,
        polygonOffset: true,
        polygonOffsetFactor: -4,
        side: THREE.DoubleSide,
      }),
      max
    );
    this.mesh.count = 0;
    this.mesh.frustumCulled = false;
    scene.add(this.mesh);
    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._qs = new THREE.Quaternion();
    this._z = new THREE.Vector3(0, 0, 1);
    this._s = new THREE.Vector3();
  }

  stamp(point, normal, scale = 1) {
    const i = this.cursor;
    this.cursor = (this.cursor + 1) % this.max;
    this._q.setFromUnitVectors(this._z, normal);
    this._qs.setFromAxisAngle(this._z, Math.random() * Math.PI * 2);
    this._q.multiply(this._qs); // random spin so no two stamps look alike
    this._s.setScalar(scale * (0.8 + Math.random() * 0.5));
    this._m.compose(
      // lift off the surface a touch so the decal never z-fights the wall
      new THREE.Vector3().copy(point).addScaledVector(normal, 0.06),
      this._q,
      this._s
    );
    this.mesh.setMatrixAt(i, this._m);
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

// Painted decal textures — a chipped bullet pockmark and a radial glass crack.
function makeHoleTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  // chipped plaster ring — bright enough to read on grimy facades
  for (let k = 0; k < 26; k++) {
    const a = Math.random() * Math.PI * 2;
    const r = 10 + Math.random() * 14;
    g.fillStyle = `rgba(232,226,214,${0.3 + Math.random() * 0.3})`;
    g.beginPath();
    g.arc(32 + Math.cos(a) * r, 32 + Math.sin(a) * r, 2 + Math.random() * 3.5, 0, 7);
    g.fill();
  }
  // dark irregular pit
  const grad = g.createRadialGradient(32, 32, 1, 32, 32, 13);
  grad.addColorStop(0, 'rgba(12,10,9,0.95)');
  grad.addColorStop(0.55, 'rgba(28,24,22,0.75)');
  grad.addColorStop(1, 'rgba(40,36,32,0)');
  g.fillStyle = grad;
  g.beginPath();
  g.arc(32, 32, 13, 0, 7);
  g.fill();
  for (let k = 0; k < 7; k++) {
    const a = Math.random() * Math.PI * 2;
    g.strokeStyle = 'rgba(20,17,15,0.55)';
    g.lineWidth = 1 + Math.random();
    g.beginPath();
    g.moveTo(32 + Math.cos(a) * 6, 32 + Math.sin(a) * 6);
    g.lineTo(32 + Math.cos(a) * (14 + Math.random() * 8), 32 + Math.sin(a) * (14 + Math.random() * 8));
    g.stroke();
  }
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 4;
  return t;
}

function makeCrackTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 96;
  const g = c.getContext('2d');
  g.translate(48, 48);
  // radial shatter cracks with side branches
  const rays = 9;
  for (let k = 0; k < rays; k++) {
    const a = (k / rays) * Math.PI * 2 + Math.random() * 0.5;
    const len = 26 + Math.random() * 18;
    g.strokeStyle = `rgba(235,245,255,${0.5 + Math.random() * 0.3})`;
    g.lineWidth = 1.4;
    g.beginPath();
    g.moveTo(Math.cos(a) * 3, Math.sin(a) * 3);
    let x = Math.cos(a) * len;
    let y = Math.sin(a) * len;
    g.lineTo(x * 0.55 + (Math.random() - 0.5) * 6, y * 0.55 + (Math.random() - 0.5) * 6);
    g.lineTo(x, y);
    g.stroke();
    // branch
    g.lineWidth = 0.8;
    g.beginPath();
    g.moveTo(x * 0.55, y * 0.55);
    g.lineTo(x * 0.55 + Math.cos(a + 0.9) * 9, y * 0.55 + Math.sin(a + 0.9) * 9);
    g.stroke();
  }
  // concentric stress rings
  for (const r of [8, 15]) {
    g.strokeStyle = 'rgba(225,238,250,0.4)';
    g.lineWidth = 0.9;
    g.beginPath();
    g.arc(0, 0, r + Math.random() * 2, 0, 7);
    g.stroke();
  }
  // dark impact center
  const grad = g.createRadialGradient(0, 0, 0, 0, 0, 5);
  grad.addColorStop(0, 'rgba(10,12,14,0.9)');
  grad.addColorStop(1, 'rgba(10,12,14,0)');
  g.fillStyle = grad;
  g.beginPath();
  g.arc(0, 0, 5, 0, 7);
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 4;
  return t;
}

// Glinting glass shards that spray from a broken window and fall fast.
const SHARD_MAX = 260;

class ShardBurst {
  constructor(scene) {
    this.pos = new Float32Array(SHARD_MAX * 3);
    this.life = new Float32Array(SHARD_MAX).fill(1);
    this.vel = new Float32Array(SHARD_MAX * 3);
    this.cursor = 0;
    this.geo = new THREE.BufferGeometry();
    this.geo.setAttribute('position', new THREE.BufferAttribute(this.pos, 3));
    this.geo.setAttribute('life', new THREE.BufferAttribute(this.life, 1));
    this.mat = new THREE.ShaderMaterial({
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
          gl_PointSize = (0.35 - 0.15 * life) * (330.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5)) * 2.0;
          if (d > 1.0) discard;
          float a = (1.0 - d) * (1.0 - vLife) * 0.9;
          if (a < 0.02) discard;
          gl_FragColor = vec4(vec3(0.75, 0.92, 1.05) * 1.5, a);
        }`,
    });
    this.points = new THREE.Points(this.geo, this.mat);
    this.points.frustumCulled = false;
    scene.add(this.points);
  }

  burst(pos, normal) {
    for (let k = 0; k < 16; k++) {
      const i = this.cursor;
      this.cursor = (this.cursor + 1) % SHARD_MAX;
      this.pos[i * 3] = pos.x;
      this.pos[i * 3 + 1] = pos.y;
      this.pos[i * 3 + 2] = pos.z;
      this.life[i] = 0.001;
      // spray out of the pane, then gravity takes them
      this.vel[i * 3] = normal.x * (2 + Math.random() * 4) + (Math.random() - 0.5) * 3;
      this.vel[i * 3 + 1] = 1 + Math.random() * 2.5;
      this.vel[i * 3 + 2] = normal.z * (2 + Math.random() * 4) + (Math.random() - 0.5) * 3;
    }
  }

  update(dt) {
    let alive = false;
    for (let i = 0; i < SHARD_MAX; i++) {
      if (this.life[i] >= 1) continue;
      alive = true;
      this.life[i] = Math.min(1, this.life[i] + dt / 0.8);
      this.vel[i * 3 + 1] -= 16 * dt; // glass falls hard
      this.pos[i * 3] += this.vel[i * 3] * dt;
      this.pos[i * 3 + 1] += this.vel[i * 3 + 1] * dt;
      this.pos[i * 3 + 2] += this.vel[i * 3 + 2] * dt;
    }
    if (alive || this._wasAlive) {
      this.geo.attributes.position.needsUpdate = true;
      this.geo.attributes.life.needsUpdate = true;
    }
    this._wasAlive = alive;
  }

  dispose() {
    this.points.parent?.remove(this.points);
    this.geo.dispose();
    this.mat.dispose();
  }
}

// The muzzle: this one is ON the plane, so it plays dry and immediate — no
// propagation delay, no reverb send. Everything DOWNRANGE (impacts, glass,
// collapses) goes through audiofx.js instead, where distance is modeled.
class GunSound {
  shoot(vol) {
    if (!fxAudio.ensure()) return;
    const ctx = fxAudio.ctx;
    const t = ctx.currentTime;
    // breech report: a hard noise crack…
    const src = ctx.createBufferSource();
    src.buffer = fxAudio.noise;
    const bp = ctx.createBiquadFilter();
    bp.type = 'bandpass';
    bp.frequency.value = 850 + Math.random() * 400;
    bp.Q.value = 0.7;
    const g = ctx.createGain();
    g.gain.setValueAtTime(0.55 * vol, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + 0.09);
    src.connect(bp).connect(g).connect(fxAudio.master);
    src.start(t, Math.random() * 0.4);
    src.stop(t + 0.1);
    // …over the mechanical thump of the action
    const o = ctx.createOscillator();
    o.type = 'square';
    o.frequency.setValueAtTime(185, t);
    o.frequency.exponentialRampToValueAtTime(65, t + 0.05);
    const og = ctx.createGain();
    og.gain.setValueAtTime(0.3 * vol, t);
    og.gain.exponentialRampToValueAtTime(0.001, t + 0.06);
    o.connect(og).connect(fxAudio.master);
    o.start(t);
    o.stop(t + 0.07);
  }

  dispose() {}
}

// One-draw-call impact dust: concrete puffs at bullet strikes, big plumes
// when a damage stage crumbles. Same ring-buffer trick as SmokeTrail.
const DUST_MAX = 420;
const DUST_LIFE = 0.9;

class ImpactDust {
  constructor(scene) {
    this.positions = new Float32Array(DUST_MAX * 3);
    this.life = new Float32Array(DUST_MAX).fill(1);
    this.vel = new Float32Array(DUST_MAX * 3);
    this.cursor = 0;
    this.geo = new THREE.BufferGeometry();
    this.geo.setAttribute('position', new THREE.BufferAttribute(this.positions, 3));
    this.geo.setAttribute('life', new THREE.BufferAttribute(this.life, 1));
    this.mat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
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
          gl_PointSize = (2.0 + life * 18.0) * (150.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5));
          float a = smoothstep(0.5, 0.1, d) * (1.0 - vLife) * 0.7;
          if (a < 0.01) discard;
          gl_FragColor = vec4(mix(vec3(0.36, 0.33, 0.29), vec3(0.66, 0.61, 0.53), vLife), a);
        }`,
    });
    this.points = new THREE.Points(this.geo, this.mat);
    this.points.frustumCulled = false;
    scene.add(this.points);
  }

  burst(pos, n, spread) {
    for (let k = 0; k < n; k++) {
      const i = this.cursor;
      this.cursor = (this.cursor + 1) % DUST_MAX;
      this.positions[i * 3] = pos.x + (Math.random() - 0.5) * spread;
      this.positions[i * 3 + 1] = pos.y + (Math.random() - 0.5) * spread * 0.5;
      this.positions[i * 3 + 2] = pos.z + (Math.random() - 0.5) * spread;
      this.life[i] = 0.001 + Math.random() * 0.25;
      this.vel[i * 3] = (Math.random() - 0.5) * 6;
      this.vel[i * 3 + 1] = 1.5 + Math.random() * 4;
      this.vel[i * 3 + 2] = (Math.random() - 0.5) * 6;
    }
  }

  update(dt) {
    let alive = false;
    for (let i = 0; i < DUST_MAX; i++) {
      if (this.life[i] >= 1) continue;
      alive = true;
      this.life[i] = Math.min(1, this.life[i] + dt / DUST_LIFE);
      this.vel[i * 3 + 1] -= 5 * dt; // dust settles
      this.positions[i * 3] += this.vel[i * 3] * dt;
      this.positions[i * 3 + 1] += this.vel[i * 3 + 1] * dt;
      this.positions[i * 3 + 2] += this.vel[i * 3 + 2] * dt;
    }
    if (alive || this._wasAlive) {
      this.geo.attributes.position.needsUpdate = true;
      this.geo.attributes.life.needsUpdate = true;
    }
    this._wasAlive = alive;
  }

  dispose() {
    this.points.parent?.remove(this.points);
    this.geo.dispose();
    this.mat.dispose();
  }
}

export class MachineGun {
  constructor(game) {
    this.game = game;
    this._cool = 0;
    this._flashT = 0;
    this.live = []; // in-flight tracers
    this.sound = new GunSound();
    this.dust = new ImpactDust(game.scene);
    this.shards = new ShardBurst(game.scene);
    this.holes = new DecalLayer(game.scene, makeHoleTexture(), 0.52, 1600);
    this.cracks = new DecalLayer(game.scene, makeCrackTexture(), 1.25, 400);
    this.raycaster = new THREE.Raycaster();
    this.raycaster.firstHitOnly = true;

    // --- the pod under the fuselage ---
    const pod = new THREE.Group();
    const metal = new THREE.MeshStandardMaterial({ color: 0x22252a, metalness: 0.75, roughness: 0.4 });
    const body = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.2, 1.5, 12), metal);
    body.rotation.x = Math.PI / 2;
    pod.add(body);
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.055, 1.35, 10), metal);
    barrel.rotation.x = Math.PI / 2;
    barrel.position.z = -1.3;
    pod.add(barrel);
    const brake = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.09, 0.32, 10), metal);
    brake.rotation.x = Math.PI / 2;
    brake.position.z = -1.92;
    pod.add(brake);
    for (const dz of [-0.35, 0.45]) {
      const strut = new THREE.Mesh(new THREE.BoxGeometry(0.09, 0.42, 0.22), metal);
      strut.position.set(0, 0.32, dz);
      pod.add(strut);
    }
    pod.position.set(0, -0.95, -1.55);
    pod.traverse((o) => {
      if (o.isMesh) o.castShadow = true;
    });
    game.plane.add(pod);
    this.pod = pod;
    this.muzzleLocal = new THREE.Vector3(0, -0.95, -3.65);

    // --- muzzle flash sprite ---
    const c = document.createElement('canvas');
    c.width = c.height = 64;
    const g2 = c.getContext('2d');
    const grad = g2.createRadialGradient(32, 32, 2, 32, 32, 30);
    grad.addColorStop(0, 'rgba(255,255,235,1)');
    grad.addColorStop(0.35, 'rgba(255,200,90,0.85)');
    grad.addColorStop(1, 'rgba(255,140,40,0)');
    g2.fillStyle = grad;
    g2.fillRect(0, 0, 64, 64);
    this.flash = new THREE.Sprite(new THREE.SpriteMaterial({
      map: new THREE.CanvasTexture(c),
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      toneMapped: false,
    }));
    this.flash.position.copy(this.muzzleLocal);
    this.flash.visible = false;
    game.plane.add(this.flash);

    // --- tracers: one InstancedMesh of glowing rods ---
    this.tracers = new THREE.InstancedMesh(
      new THREE.BoxGeometry(0.09, 0.09, 4.2),
      new THREE.MeshBasicMaterial({
        color: 0xffce6a,
        transparent: true,
        opacity: 0.95,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        toneMapped: false,
      }),
      MAX_TRACERS
    );
    this.tracers.frustumCulled = false;
    this.tracers.count = 0;
    game.scene.add(this.tracers);

    // --- crosshair + hit toast (address + building HP) ---
    const hud = game.ui.hud ?? document.body;
    this.cross = document.createElement('div');
    this.cross.id = 'crosshair';
    hud.appendChild(this.cross);
    this.toast = document.createElement('div');
    this.toast.id = 'hit-toast';
    this.toast.className = 'hidden';
    this.toast.innerHTML = '<div id="ht-addr"></div><div id="ht-track"><div id="ht-fill"></div></div>';
    hud.appendChild(this.toast);
    this._toastT = 0;

    this._v = new THREE.Vector3();
    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._zAxis = new THREE.Vector3(0, 0, 1);
    this._one = new THREE.Vector3(1, 1, 1);
  }

  update(dt) {
    const g = this.game;
    this._cool -= dt;
    const firing =
      g.started && !g.crashed && !g.placement?.active && !g.foot?.active &&
      (g.skillbar?.current ?? 'gun') === 'gun' && g.keys.has('Space');
    if (firing && this._cool <= 0) {
      this._cool = 1 / RATE;
      this._fire();
    }
    if (this._flashT > 0) {
      this._flashT -= dt;
      if (this._flashT <= 0) this.flash.visible = false;
    }
    this._updateTracers(dt);
    this.dust.update(dt);
    this.shards.update(dt);
    this._updateCrosshair();
    if (this._toastT > 0) {
      this._toastT -= dt;
      if (this._toastT <= 0) this.toast.classList.add('hidden');
    }
  }

  _fire() {
    const g = this.game;
    const muzzle = g.plane.localToWorld(this.muzzleLocal.clone());
    const dir = this._v.set(0, 0, -1).applyQuaternion(g.plane.quaternion).clone();
    dir.x += (Math.random() - 0.5) * SPREAD * 2;
    dir.y += (Math.random() - 0.5) * SPREAD * 2;
    dir.z += (Math.random() - 0.5) * SPREAD * 2;
    dir.normalize();

    // hitscan now, damage on tracer arrival. Targets: everything the plane
    // collides with (spatially prefiltered — the 3×3 hash cells cover the
    // full 1100 m range) PLUS the bullet-only set (churches, houses…).
    this.raycaster.set(muzzle, dir);
    this.raycaster.far = RANGE;
    const near = g.world.collidablesNear
      ? g.world.collidablesNear(muzzle.x, muzzle.z)
      : g.world.collidables;
    const targets = g.world.shootables?.length ? near.concat(g.world.shootables) : near;
    const hits = this.raycaster.intersectObjects(targets, true);
    this.raycaster.far = Infinity;
    const hit = hits.length ? hits[0] : null;
    if (this.live.length >= MAX_TRACERS) this.live.shift();
    this.live.push({ pos: muzzle, dir, dist: 0, maxDist: hit ? hit.distance : RANGE, hit });

    this.flash.visible = true;
    this._flashT = 0.045;
    this.flash.material.rotation = Math.random() * Math.PI * 2;
    this.flash.scale.setScalar(1.1 + Math.random() * 0.9);
    this.sound.shoot(g.sound.volume ?? 0.7);
    g.skillbar?.pulse(0);
  }

  _updateTracers(dt) {
    for (let i = this.live.length - 1; i >= 0; i--) {
      const tr = this.live[i];
      tr.dist += BULLET_SPEED * dt;
      if (tr.dist >= tr.maxDist) {
        this._impact(tr);
        this.live.splice(i, 1);
      }
    }
    let n = 0;
    for (const tr of this.live) {
      const p = this._v.copy(tr.pos).addScaledVector(tr.dir, tr.dist);
      this._q.setFromUnitVectors(this._zAxis, tr.dir);
      this._m.compose(p, this._q, this._one);
      this.tracers.setMatrixAt(n++, this._m);
    }
    this.tracers.count = n;
    if (n > 0 || this._hadTracers) this.tracers.instanceMatrix.needsUpdate = true;
    this._hadTracers = n > 0;
  }

  // World-space surface normal of a hit — instanced meshes report face
  // normals in template space, so fold the instance matrix in too.
  _hitNormal(hit) {
    if (!hit.face) return null;
    const n = hit.face.normal.clone();
    if (hit.instanceId !== undefined && hit.object.isInstancedMesh) {
      const m = new THREE.Matrix4();
      hit.object.getMatrixAt(hit.instanceId, m);
      n.transformDirection(m.clone().premultiply(hit.object.matrixWorld));
    } else {
      n.transformDirection(hit.object.matrixWorld);
    }
    return n;
  }

  _impact(tr) {
    if (!tr.hit) return; // flew out to max range
    const g = this.game;
    // AN AEROPLANE'S COLLIDER IS A CRATE. The gun hits the box shell the hall
    // bakes for each machine, which is a foot proud of the skin in places --
    // re-cast onto the real airframe so a 12.7 mm round marks an aeroplane
    // where it actually struck it (src/planedamage.js). `null` = the crate
    // lied and the round went through a gap in it, so nothing happened.
    const real = g.planeDamage?.refine(tr.hit, tr.dir);
    if (real === null) return;
    if (real) tr.hit = real;
    const res = g.world.damageHit?.(tr.hit, tr.hit.planeRec ? PLANE_DAMAGE : DAMAGE);
    const n = this._hitNormal(tr.hit) ?? tr.dir.clone().negate();
    const vol = g.sound.volume ?? 0.7;
    // how far the sound has to travel back to the cockpit — audiofx turns
    // this into propagation delay, air absorption and reverb send
    const dist = g.camera.position.distanceTo(tr.hit.point);
    // window pane hit → glass shatters. If a real 3D pane sits there it is
    // knocked out of the facade for good (tumbling shards + a black hole);
    // painted-on windows just take the crack star. A 12.7 mm round takes the
    // whole panel, unlike a pistol round, which only leaves a hole in it.
    const pane = res?.rec && g.world.nearWindow?.(tr.hit.object, res.rec, tr.hit.point);
    if (pane) {
      const p = tr.hit.point;
      const blown = g.world.blastWindows?.(p.x, p.y, p.z, 1.9);
      if (blown?.length) {
        g.glass.burstList(blown, 0.8, 3); // GlassFX plays the shatter itself
      } else {
        this.shards.burst(p, n);
        this.cracks.stamp(p, n);
        glassBreak(dist, vol);
      }
    } else {
      // Marks, debris and the material's own noise — plaster dust off a facade,
      // sparks off steel, a splash in the Mtkvari, dirt off a hillside.
      g.impacts?.markHit(tr.hit, tr.hit.point, n, tr.dir, CART);
    }
    if (!res) return;
    this._showToast(res);
    if (res.changed) {
      // collapse smoke/dust is FireFX's job now — guns adds the stage puff
      // only while the building still stands, plus the rumble
      if (!res.destroyed) {
        const plume = this._v.set(res.cx, res.topNow, res.cz).clone();
        this.dust.burst(plume, 24, Math.max(3, res.span * 0.55));
      }
      collapseRumble(dist, vol, res.destroyed);
    }
  }

  _showToast(res) {
    const addr = res.addr || 'უცნობი შენობა';
    const pct = Math.round(res.hpFrac * 100);
    this.toast.querySelector('#ht-addr').textContent = res.destroyed
      ? `💥 ${addr} — დანგრეულია!`
      : `🏢 ${addr}`;
    const fill = this.toast.querySelector('#ht-fill');
    fill.style.width = `${pct}%`;
    fill.style.background =
      pct > 55 ? 'linear-gradient(90deg,#29c6ff,#55f2ff)'
        : pct > 25 ? 'linear-gradient(90deg,#ffb02e,#ffd257)'
          : 'linear-gradient(90deg,#ff2e55,#ff5e8a)';
    this.toast.classList.remove('hidden');
    this._toastT = 2.2;
  }

  _updateCrosshair() {
    const g = this.game;
    // the combat HUD draws the reticle itself — the plain crosshair retires
    if (g.combatHud || !g.started || g.crashed) {
      this.cross.style.display = 'none';
      return;
    }
    const fwd = this._v.set(0, 0, -1).applyQuaternion(g.plane.quaternion);
    const p = fwd.multiplyScalar(420).add(g.plane.position).project(g.camera);
    if (p.z > 1) {
      this.cross.style.display = 'none';
      return;
    }
    this.cross.style.display = 'block';
    this.cross.style.left = `${(p.x * 0.5 + 0.5) * g.container.clientWidth}px`;
    this.cross.style.top = `${(-p.y * 0.5 + 0.5) * g.container.clientHeight}px`;
  }

  dispose() {
    this.game.plane.remove(this.pod);
    this.game.plane.remove(this.flash);
    this.tracers.parent?.remove(this.tracers);
    this.tracers.geometry.dispose();
    this.tracers.material.dispose();
    this.flash.material.map.dispose();
    this.flash.material.dispose();
    this.dust.dispose();
    this.shards.dispose();
    this.holes.dispose();
    this.cracks.dispose();
    this.sound.dispose();
    this.cross.remove();
    this.toast.remove();
  }
}
