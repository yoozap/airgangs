import * as THREE from 'three';

// The rocket blast: everything that happens in the first second of a warhead
// hitting a wall, composed in one call.
//   - a VOLUMETRIC fireball (boiling noise sphere, not a sprite) that inflates,
//     cools from white-hot to sooty red and hands over to black smoke
//   - a real point light, so the facades around the hit go orange for a beat
//   - the pressure ring skimming the wall + a dust jet fired along it
//   - a charred crater decal with spalled concrete around the rim
//   - staggered secondary fireballs so the blast ROLLS instead of popping
// Smoke, embers and the dust plume are FireFX's job — BlastFX drives them.

const FB_MAX = 7; // concurrent fireballs
const RING_MAX = 6;
const CRATER_MAX = 300;

const NOISE3 = `
  float h3(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
  float n3(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
      mix(mix(h3(i), h3(i + vec3(1, 0, 0)), f.x),
          mix(h3(i + vec3(0, 1, 0)), h3(i + vec3(1, 1, 0)), f.x), f.y),
      mix(mix(h3(i + vec3(0, 0, 1)), h3(i + vec3(1, 0, 1)), f.x),
          mix(h3(i + vec3(0, 1, 1)), h3(i + vec3(1, 1, 1)), f.x), f.y),
      f.z);
  }
  float fbm3(vec3 p) {
    float v = 0.0;
    float a = 0.55;
    for (int k = 0; k < 4; k++) {
      v += a * n3(p);
      p = p * 2.11 + vec3(9.7, 3.1, 7.3);
      a *= 0.5;
    }
    return v;
  }`;

// Alpha-blended, NOT additive: an additive ball over a bright Tbilisi sky
// washes out to white cream. Alpha keeps the orange saturated and lets the
// dark soot lobes read against it.
function makeFireballMaterial() {
  return new THREE.ShaderMaterial({
    transparent: true,
    depthWrite: false,
    side: THREE.FrontSide,
    uniforms: { uT: { value: 0 }, uSeed: { value: 0 } },
    vertexShader: `
      uniform float uT;
      uniform float uSeed;
      varying float vD;
      ${NOISE3}
      void main() {
        // the surface BOILS: noise displaces along the normals and churns
        // upward as the ball climbs, which is what makes it read as burning
        // gas instead of an inflating balloon
        // two scales of churn: big lobes rolling over each other plus a
        // finer break-up, so the silhouette tears instead of ballooning
        float d = fbm3(normal * 2.6 + vec3(uSeed) + vec3(0.0, -uT * 2.2, 0.0));
        float fine = fbm3(normal * 7.5 + vec3(uSeed * 1.7) + vec3(0.0, -uT * 3.4, 0.0));
        d = d * 0.78 + fine * 0.34;
        vD = d;
        vec3 p = position * (1.0 + (d - 0.5) * (0.95 + 0.55 * uT));
        gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
      }`,
    fragmentShader: `
      uniform float uT;
      varying float vD;
      void main() {
        // Blackbody cool-down keyed to the noise. Biased COOL on purpose:
        // with ACES tone mapping anything near white desaturates, so a
        // generously hot ramp turns the whole ball into cream. Only a small
        // core in the first instants is allowed past orange.
        float heat = clamp(vD * 1.3 - uT * 1.5 - 0.06, 0.0, 1.0);
        vec3 c = mix(vec3(0.05, 0.032, 0.026), vec3(0.55, 0.06, 0.004), smoothstep(0.02, 0.34, heat));
        c = mix(c, vec3(0.93, 0.26, 0.015), smoothstep(0.34, 0.6, heat));
        c = mix(c, vec3(1.0, 0.58, 0.09), smoothstep(0.6, 0.85, heat));
        c = mix(c, vec3(1.0, 0.88, 0.52), smoothstep(0.88, 1.0, heat) * max(0.0, 1.0 - uT * 3.0));
        // soot mottling on the cooling side — the black smoke being born
        float soot = smoothstep(0.35, 0.0, heat) * smoothstep(0.25, 0.9, uT);
        c = mix(c, vec3(0.08, 0.075, 0.07), soot * 0.85);
        // the noise CARVES the silhouette: ragged burning lobes, no clean ball
        float body = smoothstep(0.30, 0.52, vD + 0.30 * (1.0 - uT));
        float a = min(pow(1.0 - uT, 1.55) * body * 3.0, 0.99);
        a *= 1.0 - smoothstep(0.55, 0.9, uT);
        if (a < 0.02) discard;
        gl_FragColor = vec4(c, a);
      }`,
  });
}

// Charred blast crater: black core, spalled concrete rim, radial cracks.
function makeCraterTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const g = c.getContext('2d');
  g.translate(128, 128);
  // pulverized halo
  const halo = g.createRadialGradient(0, 0, 20, 0, 0, 126);
  halo.addColorStop(0, 'rgba(18,15,13,0.95)');
  halo.addColorStop(0.4, 'rgba(30,26,23,0.7)');
  halo.addColorStop(0.75, 'rgba(52,46,40,0.32)');
  halo.addColorStop(1, 'rgba(60,54,48,0)');
  g.fillStyle = halo;
  g.beginPath();
  g.arc(0, 0, 126, 0, 7);
  g.fill();
  // radial cracks running out of the hole
  for (let k = 0; k < 22; k++) {
    const a = Math.random() * Math.PI * 2;
    const len = 45 + Math.random() * 75;
    g.strokeStyle = `rgba(12,10,9,${0.35 + Math.random() * 0.45})`;
    g.lineWidth = 0.8 + Math.random() * 2.2;
    g.beginPath();
    g.moveTo(Math.cos(a) * 14, Math.sin(a) * 14);
    let x = Math.cos(a) * len;
    let y = Math.sin(a) * len;
    g.lineTo(x * 0.5 + (Math.random() - 0.5) * 22, y * 0.5 + (Math.random() - 0.5) * 22);
    g.lineTo(x, y);
    g.stroke();
  }
  // spalled chips: bright concrete showing through the char
  for (let k = 0; k < 90; k++) {
    const a = Math.random() * Math.PI * 2;
    const r = 26 + Math.random() * 88;
    g.fillStyle = `rgba(196,188,174,${0.1 + Math.random() * 0.35})`;
    g.beginPath();
    g.arc(Math.cos(a) * r, Math.sin(a) * r, 1.5 + Math.random() * 5, 0, 7);
    g.fill();
  }
  // the hole itself — near-black, ragged
  g.fillStyle = 'rgba(5,4,4,0.97)';
  g.beginPath();
  for (let k = 0; k <= 26; k++) {
    const a = (k / 26) * Math.PI * 2;
    const r = 26 + Math.sin(a * 3.7) * 5 + Math.random() * 7;
    if (k === 0) g.moveTo(Math.cos(a) * r, Math.sin(a) * r);
    else g.lineTo(Math.cos(a) * r, Math.sin(a) * r);
  }
  g.closePath();
  g.fill();
  const t = new THREE.CanvasTexture(c);
  t.anisotropy = 4;
  return t;
}

export class BlastFX {
  // fx: FireFX (smoke/embers/dust), heightAt: (x,z) => groundY | null
  constructor(scene, fx, heightAt = null) {
    this.scene = scene;
    this.fx = fx;
    this.heightAt = heightAt;
    this._pending = [];
    this._t = 0;

    // ---- fireball pool ----
    // Cloned ShaderMaterials share ONE compiled program (three caches by
    // source + defines), so the pool costs a single shader compile.
    const sph = new THREE.IcosahedronGeometry(1, 3);
    this._sph = sph;
    this.balls = [];
    for (let i = 0; i < FB_MAX; i++) {
      const mesh = new THREE.Mesh(sph, makeFireballMaterial());
      mesh.visible = false;
      mesh.frustumCulled = false;
      scene.add(mesh);
      this.balls.push({ mesh, t: 0, dur: 1, R: 1, rise: 3, live: false });
    }
    this.bCursor = 0;

    // ---- flash lights ----
    // Created ONCE and never added/removed: changing the light count forces
    // three to recompile every material in the city (a full-second hitch).
    // They idle at intensity 0 and get driven by flash().
    this.lights = [];
    for (let i = 0; i < 2; i++) {
      const l = new THREE.PointLight(0xff7a30, 0, 420, 2);
      l.position.set(0, -10000, 0);
      scene.add(l);
      this.lights.push({ l, t: 0, dur: 0, peak: 0 });
    }
    this.lCursor = 0;

    // ---- pressure rings skimming the surface ----
    this._ringGeo = new THREE.RingGeometry(0.78, 1, 56);
    this._ringMat = new THREE.MeshBasicMaterial({
      color: 0xffe8c8,
      transparent: true,
      depthWrite: false,
      side: THREE.DoubleSide,
      blending: THREE.AdditiveBlending,
      toneMapped: false,
    });
    this.rings = [];
    for (let i = 0; i < RING_MAX; i++) {
      const m = new THREE.Mesh(this._ringGeo, this._ringMat.clone());
      m.visible = false;
      m.frustumCulled = false;
      scene.add(m);
      this.rings.push({ mesh: m, t: 0, dur: 1, R: 1, live: false });
    }
    this.rCursor = 0;

    // ---- crater decals ----
    this.craterMat = new THREE.MeshBasicMaterial({
      map: makeCraterTexture(),
      transparent: true,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -5,
      side: THREE.DoubleSide,
      opacity: 0.95,
      toneMapped: false,
    });
    this.craters = new THREE.InstancedMesh(new THREE.PlaneGeometry(1, 1), this.craterMat, CRATER_MAX);
    this.craters.count = 0;
    this.craters.frustumCulled = false;
    scene.add(this.craters);
    this.cCursor = 0;

    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._v = new THREE.Vector3();
    this._s = new THREE.Vector3();
    this._z = new THREE.Vector3(0, 0, 1);
  }

  // ---- primitives -----------------------------------------------------------

  fireball(x, y, z, R, { dur = 1.15, rise = 3.5 } = {}) {
    const b = this.balls[this.bCursor];
    this.bCursor = (this.bCursor + 1) % FB_MAX;
    b.mesh.position.set(x, y, z);
    b.mesh.scale.setScalar(R * 0.35);
    b.mesh.material.uniforms.uT.value = 0;
    b.mesh.material.uniforms.uSeed.value = Math.random() * 30;
    b.mesh.visible = true;
    b.t = 0;
    b.dur = dur;
    b.R = R;
    b.rise = rise;
    b.live = true;
  }

  flash(x, y, z, power = 1, dur = 0.5) {
    const e = this.lights[this.lCursor];
    this.lCursor = (this.lCursor + 1) % this.lights.length;
    e.l.position.set(x, y, z);
    e.peak = 5200 * power;
    e.l.intensity = e.peak;
    e.l.distance = 160 + 220 * power;
    e.t = 0;
    e.dur = dur;
  }

  // Expanding ring lying against a surface (normal = surface normal).
  ring(x, y, z, nx, ny, nz, R, dur = 0.45) {
    const r = this.rings[this.rCursor];
    this.rCursor = (this.rCursor + 1) % RING_MAX;
    r.mesh.position.set(x + nx * 0.5, y + ny * 0.5, z + nz * 0.5);
    this._q.setFromUnitVectors(this._z, this._v.set(nx, ny, nz).normalize());
    r.mesh.quaternion.copy(this._q);
    r.mesh.scale.setScalar(R * 0.2);
    r.mesh.material.opacity = 0.4; // a hint of shock, not a white dinner plate
    r.mesh.visible = true;
    r.t = 0;
    r.dur = dur;
    r.R = R;
    r.live = true;
  }

  crater(x, y, z, nx, ny, nz, size) {
    const i = this.cCursor;
    this.cCursor = (this.cCursor + 1) % CRATER_MAX;
    this._q.setFromUnitVectors(this._z, this._v.set(nx, ny, nz).normalize());
    this._q.multiply(new THREE.Quaternion().setFromAxisAngle(this._z, Math.random() * Math.PI * 2));
    this._m.compose(
      new THREE.Vector3(x + nx * 0.09, y + ny * 0.09, z + nz * 0.09),
      this._q,
      this._s.set(size, size, 1)
    );
    this.craters.setMatrixAt(i, this._m);
    this.craters.count = Math.max(this.craters.count, i + 1);
    this.craters.instanceMatrix.needsUpdate = true;
  }

  // ---- the composed blast ---------------------------------------------------

  // point: world hit point. normal: surface normal (may be null — air burst).
  // r: blast radius in meters. ground: hit the dirt, not a wall.
  explode(point, normal, { r = 8, ground = false, marks = true } = {}) {
    const { x, y, z } = point;
    const n = normal ? this._v.set(normal.x, normal.y, normal.z).normalize().clone() : null;
    const fx = this.fx;

    // core fireball, pushed a touch off the wall so it doesn't clip inside
    const ox = n ? n.x * r * 0.3 : 0;
    const oy = n ? n.y * r * 0.3 : 0;
    const oz = n ? n.z * r * 0.3 : 0;
    this.fireball(x + ox, y + oy + r * 0.15, z + oz, r, { dur: 1.25, rise: 3.5 });
    this.flash(x + ox, y + oy + r * 0.3, z + oz, Math.min(2.2, r / 6), 0.55);

    // secondaries: the burning gas rolls out along the wall and up the face
    for (let k = 0; k < 3; k++) {
      const a = Math.random() * Math.PI * 2;
      const spread = r * (0.5 + Math.random() * 0.6);
      this._after(0.06 + k * 0.09, () => {
        this.fireball(
          x + ox + Math.cos(a) * spread * (n ? 1 - Math.abs(n.x) * 0.4 : 1),
          y + oy + r * (0.25 + Math.random() * 0.7),
          z + oz + Math.sin(a) * spread * (n ? 1 - Math.abs(n.z) * 0.4 : 1),
          r * (0.45 + Math.random() * 0.35),
          { dur: 1.0 + Math.random() * 0.5, rise: 4.5 }
        );
      });
    }

    // pressure ring skimming the surface + a ground ring if we're low
    if (n) this.ring(x, y, z, n.x, n.y, n.z, r * 1.7, 0.32);
    const gy = this.heightAt ? this.heightAt(x, z) : null;
    // the base surge only exists if the blast actually touched the street
    if (gy !== null && gy !== undefined && (ground || y - gy < 7)) {
      fx?.wave(x, gy, z, r * 0.9);
    }

    // hot fireball core → embers everywhere
    for (let k = 0; k < 26; k++) fx?.spawnEmber(x + ox, y + oy + Math.random() * r * 0.6, z + oz);

    // black blast smoke: a fast column plus a jet fired ALONG the wall, which
    // is what a contact detonation actually does
    for (let k = 0; k < 16; k++) {
      fx?.spawnSmoke(x + ox, y + oy + Math.random() * r * 0.6, z + oz, {
        size: r * (0.8 + Math.random() * 0.7),
        dark: 0.9,
        dur: 4.5,
        vy: 5 + Math.random() * 6,
        spread: r * 0.5,
      });
    }
    if (n) {
      // tangent frame on the wall: dust sprays sideways and up the facade
      const t1 = new THREE.Vector3(-n.z, 0, n.x).normalize();
      const t2 = new THREE.Vector3().crossVectors(n, t1).normalize();
      for (let k = 0; k < 20; k++) {
        const a = Math.random() * Math.PI * 2;
        const sp = 7 + Math.random() * 13;
        const dx = t1.x * Math.cos(a) + t2.x * Math.sin(a);
        const dy = t1.y * Math.cos(a) + t2.y * Math.sin(a);
        const dz = t1.z * Math.cos(a) + t2.z * Math.sin(a);
        fx?.spawnSmoke(x + n.x * 0.6, y + n.y * 0.6, z + n.z * 0.6, {
          size: 7 + Math.random() * 9,
          dark: 0.35 + Math.random() * 0.25,
          dur: 3.6,
          vx: dx * sp + n.x * 3,
          vy: dy * sp + 2.5,
          vz: dz * sp + n.z * 3,
          spread: 1.5,
        });
      }
      if (marks) this.crater(x, y, z, n.x, n.y, n.z, r * 0.75);
    }

    // late soot: the fireball's smoke keeps climbing after the light dies
    this._after(0.45, () => {
      for (let k = 0; k < 12; k++) {
        fx?.spawnSmoke(x + ox, y + oy + r * (0.4 + Math.random()), z + oz, {
          size: r * (1.1 + Math.random() * 0.8),
          dark: 0.93,
          dur: 6.5,
          vy: 3 + Math.random() * 3,
          spread: r * 0.7,
        });
      }
    });
  }

  _after(delay, fn) {
    this._pending.push({ t: delay, fn });
  }

  update(dt) {
    this._t += dt;
    for (let i = this._pending.length - 1; i >= 0; i--) {
      const p = this._pending[i];
      p.t -= dt;
      if (p.t <= 0) {
        this._pending.splice(i, 1);
        p.fn();
      }
    }

    for (const b of this.balls) {
      if (!b.live) continue;
      b.t += dt;
      const p = Math.min(1, b.t / b.dur);
      b.mesh.material.uniforms.uT.value = p;
      // fast inflate then coast — real fireballs reach full size early
      b.mesh.scale.setScalar(b.R * (0.35 + 0.85 * Math.pow(p, 0.45)));
      b.mesh.position.y += b.rise * dt * (0.4 + p);
      if (p >= 1) {
        b.live = false;
        b.mesh.visible = false;
      }
    }

    for (const e of this.lights) {
      if (e.l.intensity <= 0) continue;
      e.t += dt;
      const p = Math.min(1, e.t / e.dur);
      // flicker while it burns, then die out
      const f = (1 - p) * (1 - p) * (0.75 + 0.25 * Math.sin(this._t * 47));
      e.l.intensity = e.peak * Math.max(0, f);
      if (p >= 1) {
        e.l.intensity = 0;
        e.l.position.y = -10000;
      }
    }

    for (const r of this.rings) {
      if (!r.live) continue;
      r.t += dt;
      const p = Math.min(1, r.t / r.dur);
      r.mesh.scale.setScalar(r.R * (0.2 + 1.1 * Math.pow(p, 0.6)));
      r.mesh.material.opacity = 0.4 * (1 - p) * (1 - p);
      if (p >= 1) {
        r.live = false;
        r.mesh.visible = false;
      }
    }
  }

  dispose() {
    for (const b of this.balls) {
      b.mesh.parent?.remove(b.mesh);
      b.mesh.material.dispose();
    }
    this._sph.dispose();
    for (const e of this.lights) e.l.parent?.remove(e.l);
    for (const r of this.rings) {
      r.mesh.parent?.remove(r.mesh);
      r.mesh.material.dispose();
    }
    this._ringGeo.dispose();
    this._ringMat.dispose();
    this.craters.parent?.remove(this.craters);
    this.craters.geometry.dispose();
    this.craterMat.map?.dispose();
    this.craterMat.dispose();
  }
}
