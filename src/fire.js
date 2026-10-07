import * as THREE from 'three';

// Fire + smoke for burning buildings:
//  - flames: PERSISTENT billboard quads with a domain-warped fbm shader —
//    licking, curling film-like fire (no sprite sheets needed), one per
//    burning window, fed to the bloom pass by additive over-brightness
//  - embers: glowing sparks that ride the heat column above each flame
//  - smoke: soft dark puffs — window smoke, the collapse dust avalanche, and
//    the endless smolder over rubble mounds
// Emitters are per-building anchor lists managed by CityMap.

const FQ_MAX = 900; // flame quads (persistent, rebuilt when burning set changes)
const E_MAX = 700; // embers
const E_LIFE = 1.3; // s
const S_MAX = 4200;

const FIRE_NOISE = `
  float fhash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
  }
  float fnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
      mix(fhash(i), fhash(i + vec2(1, 0)), f.x),
      mix(fhash(i + vec2(0, 1)), fhash(i + vec2(1, 1)), f.x), f.y);
  }
  float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.55;
    for (int k = 0; k < 3; k++) {
      v += a * fnoise(p);
      p = p * 2.13 + vec2(11.3, 7.7);
      a *= 0.5;
    }
    return v;
  }`;

export class FireFX {
  constructor(scene) {
    this.burning = new Map(); // key → { anchors: Float32Array [x,y,z...], intensity, acc }
    this.smolder = new Map(); // key → { anchors, acc }
    this._time = 0;

    // ---- flames: instanced billboards with procedural fire ----
    const quad = new THREE.PlaneGeometry(1, 1);
    quad.setAttribute('aSeed', new THREE.InstancedBufferAttribute(new Float32Array(FQ_MAX), 1));
    quad.setAttribute('aInt', new THREE.InstancedBufferAttribute(new Float32Array(FQ_MAX), 1));
    this.fMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      // NOT additive: over Tbilisi's bright hazy sky an additive flame washes
      // out to pale cream — it reads as paper, not fire. Alpha blending keeps
      // the blackbody ramp saturated, and the bloom pass still catches the
      // over-bright core.
      uniforms: { uTime: { value: 0 } },
      vertexShader: `
        attribute float aSeed;
        attribute float aInt;
        uniform float uTime;
        varying vec2 vUv;
        varying float vSeed;
        varying float vInt;
        void main() {
          vUv = uv;
          vSeed = aSeed;
          vInt = aInt;
          // spherical billboard pivoted at the flame BASE, breathing slightly
          vec4 center = modelViewMatrix * instanceMatrix * vec4(0.0, 0.0, 0.0, 1.0);
          float s = length(vec3(instanceMatrix[0][0], instanceMatrix[1][0], instanceMatrix[2][0]));
          float w = s * (1.0 + 0.07 * sin(uTime * 11.0 + aSeed * 31.0));
          float h = s * 2.1 * (1.0 + 0.11 * sin(uTime * 8.7 + aSeed * 17.0));
          vec3 pos = center.xyz + vec3(position.x * w, (position.y + 0.5) * h, 0.0);
          gl_Position = projectionMatrix * vec4(pos, 1.0);
        }`,
      fragmentShader: `
        uniform float uTime;
        varying vec2 vUv;
        varying float vSeed;
        varying float vInt;
        ${FIRE_NOISE}
        void main() {
          float t = uTime * (1.05 + fract(vSeed * 7.31) * 0.4) + vSeed * 39.0;
          // rising, domain-warped turbulence — one warp channel is enough and
          // costs half the noise fetches of a full 2D warp
          vec2 q = vec2(vUv.x * 2.6 + vSeed * 13.0, vUv.y * 3.1 - t * 2.1);
          float w = fbm(q + vec2(0.0, t * 0.35));
          float n = fbm(q + vec2(w * 1.1, w * 0.8));
          // teardrop body: wide at the base, narrowing toward the tip
          float cx = abs(vUv.x - 0.5);
          float body = 1.0 - smoothstep(0.02, 0.5 - 0.3 * vUv.y, cx);
          body *= smoothstep(0.0, 0.06, vUv.y) * (1.0 - smoothstep(0.6, 1.0, vUv.y));
          // the turbulence EATS the flame — this is what makes ragged licking
          // tongues instead of a solid cone; it bites harder toward the tip
          float f = body * 1.5 - (1.0 - n) * (0.55 + 0.85 * vUv.y);
          f *= 0.8 + 0.4 * vInt;
          float a = smoothstep(0.03, 0.32, f);
          if (a < 0.02) discard;
          // normalize the density before shading: f runs past 1.4 at the base
          // and an un-normalized ramp paints the whole lower flame white
          f = clamp(f * 0.72, 0.0, 1.0);
          // blackbody ramp keyed to flame intensity: dark red tips → orange →
          // a yellow-white core that only survives deep inside the base
          vec3 c = vec3(0.52, 0.04, 0.004);
          c = mix(c, vec3(0.94, 0.24, 0.015), smoothstep(0.10, 0.42, f));
          c = mix(c, vec3(1.0, 0.55, 0.06), smoothstep(0.5, 0.8, f));
          // the yellow-white core lives only in the deepest part of the base
          c = mix(c, vec3(1.0, 0.86, 0.45), smoothstep(0.9, 1.0, f));
          // the ragged edges are thin, cooling gas — they must not read as a
          // solid membrane, so alpha falls off with heat as well as with shape
          gl_FragColor = vec4(c, a * (0.55 + 0.45 * smoothstep(0.05, 0.5, f)));
        }`,
    });
    this.flames = new THREE.InstancedMesh(quad, this.fMat, FQ_MAX);
    this.flames.count = 0;
    this.flames.frustumCulled = false;
    scene.add(this.flames);

    // ---- embers: hot sparks riding the heat column ----
    this.ePos = new Float32Array(E_MAX * 3);
    this.eLife = new Float32Array(E_MAX).fill(1);
    this.eSeed = new Float32Array(E_MAX);
    this.eVel = new Float32Array(E_MAX * 3);
    this.eCursor = 0;
    const eGeo = (this.eGeo = new THREE.BufferGeometry());
    eGeo.setAttribute('position', new THREE.BufferAttribute(this.ePos, 3));
    eGeo.setAttribute('life', new THREE.BufferAttribute(this.eLife, 1));
    eGeo.setAttribute('seed', new THREE.BufferAttribute(this.eSeed, 1));
    this.eMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      uniforms: { uTime: { value: 0 } },
      vertexShader: `
        attribute float life;
        attribute float seed;
        varying float vLife;
        void main() {
          if (life >= 1.0) {
            gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
            gl_PointSize = 0.0;
            return;
          }
          vLife = life;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          gl_PointSize = (0.5 - 0.3 * life) * (330.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5)) * 2.0;
          if (d > 1.0) discard;
          vec3 c = mix(vec3(1.0, 0.85, 0.4), vec3(1.0, 0.25, 0.02), vLife);
          float a = (1.0 - d) * (1.0 - vLife);
          if (a < 0.02) discard;
          gl_FragColor = vec4(c * 2.0, a);
        }`,
    });
    this.embers = new THREE.Points(eGeo, this.eMat);
    this.embers.frustumCulled = false;
    scene.add(this.embers);

    // ---- smoke ----
    this.sPos = new Float32Array(S_MAX * 3);
    this.sLife = new Float32Array(S_MAX).fill(1);
    this.sSeed = new Float32Array(S_MAX);
    this.sSize = new Float32Array(S_MAX); // world meters at full growth
    this.sDark = new Float32Array(S_MAX); // 0 dust-tan … 1 sooty black
    this.sVel = new Float32Array(S_MAX * 3);
    this.sDur = new Float32Array(S_MAX).fill(3); // per-particle lifetime, s
    this.sCursor = 0;
    const sGeo = (this.sGeo = new THREE.BufferGeometry());
    sGeo.setAttribute('position', new THREE.BufferAttribute(this.sPos, 3));
    sGeo.setAttribute('life', new THREE.BufferAttribute(this.sLife, 1));
    sGeo.setAttribute('seed', new THREE.BufferAttribute(this.sSeed, 1));
    sGeo.setAttribute('psize', new THREE.BufferAttribute(this.sSize, 1));
    sGeo.setAttribute('dark', new THREE.BufferAttribute(this.sDark, 1));
    // real sprite smoke (Kenney particle pack, CC0): a 2×2 atlas of soft
    // puff variants picked per particle, spun slowly as they rise — this is
    // what makes plumes read volumetric instead of like flat discs
    const smokeTex = new THREE.TextureLoader().load('textures/fx/smoke_atlas.png');
    smokeTex.colorSpace = THREE.NoColorSpace;
    this.sMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      uniforms: { uTime: { value: 0 }, uMap: { value: smokeTex } },
      vertexShader: `
        attribute float life;
        attribute float seed;
        attribute float psize;
        attribute float dark;
        varying float vLife;
        varying float vSeed;
        varying float vDark;
        varying float vFade;
        void main() {
          // dead particles must not rasterize — a full-size quad discarded
          // per-fragment is pure fill-rate waste at 4200 slots
          if (life >= 1.0) {
            gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
            gl_PointSize = 0.0;
            return;
          }
          vLife = life;
          vSeed = seed;
          vDark = dark;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          float dist = max(1.0, -mv.z);
          // fade + shrink puffs the camera flies through — kills the
          // stacked-fullscreen-quad overdraw inside a dust cloud
          vFade = smoothstep(5.0, 18.0, dist);
          float grow = 0.35 + 0.65 * life; // puffs expand as they rise
          gl_PointSize = min(psize * grow * mix(0.4, 1.0, vFade) * (240.0 / dist), 380.0);
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        uniform sampler2D uMap;
        uniform float uTime;
        varying float vLife;
        varying float vSeed;
        varying float vDark;
        varying float vFade;
        void main() {
          // slow tumble around the center, direction alternating by seed
          float ang = vSeed * 6.283 + uTime * 0.35 * (fract(vSeed * 3.7) - 0.5) * 2.0;
          float ca = cos(ang);
          float sa = sin(ang);
          vec2 p = gl_PointCoord - 0.5;
          vec2 rp = vec2(p.x * ca - p.y * sa, p.x * sa + p.y * ca) * 0.98 + 0.5;
          rp = clamp(rp, 0.0, 1.0);
          // 2x2 atlas variant by seed
          vec2 cell = vec2(mod(floor(vSeed * 4.0), 2.0), mod(floor(vSeed * 2.0), 2.0));
          vec4 tex = texture2D(uMap, (rp + cell) * 0.5);
          float a = tex.a * (1.0 - vLife) * (0.5 + 0.4 * smoothstep(0.0, 0.1, vLife)) * vFade;
          if (a < 0.02) discard;
          vec3 dust = vec3(0.55, 0.5, 0.43);
          vec3 soot = vec3(0.10, 0.095, 0.09);
          vec3 c = mix(dust, soot, vDark) * (1.0 - vLife * 0.2) * (0.75 + 0.5 * tex.r);
          gl_FragColor = vec4(c, a);
        }`,
    });
    this.smoke = new THREE.Points(sGeo, this.sMat);
    this.smoke.frustumCulled = false;
    scene.add(this.smoke);

    // ---- explosion flashes: big soft additive bursts (glow sprite) ----
    const glowTex = new THREE.TextureLoader().load('textures/fx/glow.png');
    const FL_MAX = 24;
    this.flPos = new Float32Array(FL_MAX * 3);
    this.flLife = new Float32Array(FL_MAX).fill(1);
    this.flSize = new Float32Array(FL_MAX);
    this.flMax = FL_MAX;
    this.flCursor = 0;
    const flGeo = (this.flGeo = new THREE.BufferGeometry());
    flGeo.setAttribute('position', new THREE.BufferAttribute(this.flPos, 3));
    flGeo.setAttribute('life', new THREE.BufferAttribute(this.flLife, 1));
    flGeo.setAttribute('psize', new THREE.BufferAttribute(this.flSize, 1));
    this.flMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      uniforms: { uMap: { value: glowTex } },
      vertexShader: `
        attribute float life;
        attribute float psize;
        varying float vLife;
        void main() {
          if (life >= 1.0) {
            gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
            gl_PointSize = 0.0;
            return;
          }
          vLife = life;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          float swell = 0.6 + 1.1 * life; // fireball inflates as it fades
          gl_PointSize = min(psize * swell * (240.0 / max(1.0, -mv.z)), 420.0);
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        uniform sampler2D uMap;
        varying float vLife;
        void main() {
          vec4 tex = texture2D(uMap, gl_PointCoord);
          float a = tex.a * (1.0 - vLife) * (1.0 - vLife);
          if (a < 0.015) discard;
          // white-hot → orange → smoky red as it dies
          vec3 c = mix(vec3(1.0, 0.95, 0.8), vec3(1.0, 0.45, 0.08), smoothstep(0.05, 0.55, vLife));
          c = mix(c, vec3(0.6, 0.16, 0.03), smoothstep(0.55, 1.0, vLife));
          gl_FragColor = vec4(c * 2.2 * tex.r, a);
        }`,
    });
    this.flashes = new THREE.Points(flGeo, this.flMat);
    this.flashes.frustumCulled = false;
    scene.add(this.flashes);

    // ---- shockwave rings: expanding dust ring hugging the ground ----
    this._waves = [];
    this._waveGeo = new THREE.RingGeometry(0.72, 1, 48);
    this._waveGeo.rotateX(-Math.PI / 2);
    this._waveMat = new THREE.MeshBasicMaterial({
      color: 0xd8cdba,
      transparent: true,
      depthWrite: false,
      side: THREE.DoubleSide,
    });
    this._scene = scene;

    // ---- ground scorch decals under collapsed buildings ----
    const scorchTex = new THREE.TextureLoader().load('textures/fx/scorch.png');
    const sc = new THREE.PlaneGeometry(1, 1);
    sc.rotateX(-Math.PI / 2);
    this.scorch = new THREE.InstancedMesh(
      sc,
      new THREE.MeshBasicMaterial({
        map: scorchTex,
        transparent: true,
        depthWrite: false,
        polygonOffset: true,
        polygonOffsetFactor: -3,
        color: 0x2a2624,
        opacity: 0.85,
      }),
      220
    );
    this.scorch.count = 0;
    this.scorch.frustumCulled = false;
    scene.add(this.scorch);
    this._scorchCursor = 0;

    // ---- persistent window glows ----
    // One always-on flickering glow per burning window, so a burning building
    // reads as ON FIRE from any distance — the particle licks add motion on
    // top. Rebuilt whenever the burning set changes.
    const G_MAX = 1024;
    this.gPos = new Float32Array(G_MAX * 3);
    this.gSeed = new Float32Array(G_MAX);
    this.gCount = 0;
    const gGeo = (this.gGeo = new THREE.BufferGeometry());
    gGeo.setAttribute('position', new THREE.BufferAttribute(this.gPos, 3));
    gGeo.setAttribute('seed', new THREE.BufferAttribute(this.gSeed, 1));
    gGeo.setDrawRange(0, 0);
    this.gMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      uniforms: { uTime: { value: 0 } },
      vertexShader: `
        attribute float seed;
        uniform float uTime;
        varying float vSeed;
        void main() {
          vSeed = seed;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          float flick = 0.75 + 0.25 * sin(uTime * 13.0 + seed * 51.0)
                              * sin(uTime * 7.3 + seed * 13.0);
          gl_PointSize = 3.6 * flick * (330.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vSeed;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5)) * 2.0;
          if (d > 1.0) discard;
          vec3 core = vec3(1.0, 0.85, 0.4);
          vec3 rim = vec3(1.0, 0.3, 0.05);
          vec3 c = mix(core, rim, smoothstep(0.0, 0.8, d));
          float a = (1.0 - d * d) * 0.8;
          if (a < 0.015) discard;
          gl_FragColor = vec4(c * 1.5, a);
        }`,
    });
    this.glows = new THREE.Points(gGeo, this.gMat);
    this.glows.frustumCulled = false;
    scene.add(this.glows);
  }

  _rebuildGlows() {
    let n = 0;
    // one persistent flame quad + one distance glow per burning window
    const m = this._gm4 ?? (this._gm4 = new THREE.Matrix4());
    const seedA = this.flames.geometry.getAttribute('aSeed');
    const intA = this.flames.geometry.getAttribute('aInt');
    for (const [, em] of this.burning) {
      const ax = em.anchors;
      for (let i = 0; i < ax.length / 3 && n < Math.min(1024, FQ_MAX); i++, n++) {
        this.gPos[n * 3] = ax[i * 3];
        this.gPos[n * 3 + 1] = ax[i * 3 + 1];
        this.gPos[n * 3 + 2] = ax[i * 3 + 2];
        this.gSeed[n] = (n * 7.13) % 10;
        // flame quad: uniform scale encodes the flame size (m at the base)
        const s = (1.15 + em.intensity * 0.75 + ((n * 2.7) % 1) * 0.35) * (em.scale ?? 1);
        m.makeScale(s, s, s);
        m.setPosition(ax[i * 3], ax[i * 3 + 1] - 0.5, ax[i * 3 + 2]);
        this.flames.setMatrixAt(n, m);
        seedA.array[n] = (n * 3.71) % 10;
        intA.array[n] = em.intensity;
      }
    }
    this.gCount = n;
    this.flames.count = n;
    this.flames.instanceMatrix.needsUpdate = true;
    seedA.needsUpdate = true;
    intA.needsUpdate = true;
    this.gGeo.setDrawRange(0, n);
    this.gGeo.attributes.position.needsUpdate = true;
    this.gGeo.attributes.seed.needsUpdate = true;
  }

  // ---- emitter management (CityMap drives these) ----

  // `scale` sizes the flame quads: window fires are ~1 m licks, a rocket hole
  // or a burning rubble mound gets a 2-3 m blaze.
  setBurning(bi, anchors, intensity, scale = 1) {
    if (!anchors || !anchors.length) this.burning.delete(bi);
    else {
      this.burning.delete(bi); // re-insert = newest
      this.burning.set(bi, { anchors, intensity, scale, acc: 0 });
      // every damaged building burns forever — cap the set (drop the OLDEST
      // fires) so a long strafing session can't grow into a slideshow
      while (this.burning.size > 16) {
        this.burning.delete(this.burning.keys().next().value);
      }
    }
    this._rebuildGlows();
  }

  clearBurning(bi) {
    this.burning.delete(bi);
    this._rebuildGlows();
  }

  setSmolder(bi, anchors) {
    if (anchors && anchors.length) {
      this.smolder.delete(bi);
      this.smolder.set(bi, { anchors, acc: 0 });
      while (this.smolder.size > 18) {
        this.smolder.delete(this.smolder.keys().next().value);
      }
    } else this.smolder.delete(bi);
  }

  // ---- direct spawns ----

  spawnEmber(x, y, z) {
    const i = this.eCursor;
    this.eCursor = (this.eCursor + 1) % E_MAX;
    this.ePos[i * 3] = x + (Math.random() - 0.5) * 0.6;
    this.ePos[i * 3 + 1] = y + Math.random() * 0.8;
    this.ePos[i * 3 + 2] = z + (Math.random() - 0.5) * 0.6;
    this.eLife[i] = 0.001;
    this.eSeed[i] = Math.random() * 10;
    this.eVel[i * 3] = (Math.random() - 0.5) * 1.4;
    this.eVel[i * 3 + 1] = 2.5 + Math.random() * 2.5;
    this.eVel[i * 3 + 2] = (Math.random() - 0.5) * 1.4;
    this.eGeo.attributes.seed.needsUpdate = true; // static after spawn
  }

  spawnSmoke(x, y, z, { size = 5, dark = 0.75, dur = 3.2, vx = 0, vy = 2.2, vz = 0, spread = 0.8 } = {}) {
    const i = this.sCursor;
    this.sCursor = (this.sCursor + 1) % S_MAX;
    this.sPos[i * 3] = x + (Math.random() - 0.5) * spread;
    this.sPos[i * 3 + 1] = y + (Math.random() - 0.5) * spread * 0.5;
    this.sPos[i * 3 + 2] = z + (Math.random() - 0.5) * spread;
    this.sLife[i] = 0.001;
    this.sSeed[i] = Math.random() * 10;
    this.sSize[i] = size * (0.75 + Math.random() * 0.5);
    this.sDark[i] = dark;
    this.sDur[i] = dur * (0.8 + Math.random() * 0.4);
    this.sVel[i * 3] = vx + (Math.random() - 0.5) * 1.2;
    this.sVel[i * 3 + 1] = vy * (0.75 + Math.random() * 0.5);
    this.sVel[i * 3 + 2] = vz + (Math.random() - 0.5) * 1.2;
    // static after spawn — uploaded once here, never streamed per-frame
    this.sGeo.attributes.seed.needsUpdate = true;
    this.sGeo.attributes.psize.needsUpdate = true;
    this.sGeo.attributes.dark.needsUpdate = true;
  }

  // The kill-shot fireball: layered hot flashes + an ember spray. The dust
  // and smoke that follow come from plume()/collapse dust.
  explosion(x, y, z, r) {
    for (let k = 0; k < 5; k++) {
      const i = this.flCursor;
      this.flCursor = (this.flCursor + 1) % this.flMax;
      this.flPos[i * 3] = x + (Math.random() - 0.5) * r * 0.8;
      this.flPos[i * 3 + 1] = y + (Math.random() - 0.3) * r * 0.7;
      this.flPos[i * 3 + 2] = z + (Math.random() - 0.5) * r * 0.8;
      this.flLife[i] = 0.001 + k * 0.04; // staggered so the fireball rolls
      this.flSize[i] = r * (1.6 + Math.random() * 1.2);
    }
    this.flGeo.attributes.psize.needsUpdate = true; // static after spawn
    for (let k = 0; k < 14; k++) this.spawnEmber(x, y + r * 0.3, z);
    // black blast smoke shooting upward, faster than fire smoke
    for (let k = 0; k < 10; k++) {
      this.spawnSmoke(x, y + Math.random() * r * 0.5, z, {
        size: r * 0.9 + Math.random() * r * 0.5,
        dark: 0.85,
        dur: 3,
        vy: 5 + Math.random() * 4,
        spread: r * 0.5,
      });
    }
  }

  // Expanding dust ring hugging the ground — the demolition base surge edge.
  wave(x, groundY, z, r) {
    if (this._waves.length > 6) return;
    const mesh = new THREE.Mesh(this._waveGeo, this._waveMat.clone());
    mesh.position.set(x, groundY + 0.4, z);
    mesh.scale.setScalar(r * 0.4);
    mesh.material.opacity = 0.55;
    this._scene.add(mesh);
    this._waves.push({ mesh, t: 0, dur: 1.1, r });
  }

  // Permanent dark burn mark on the ground under a collapsed building.
  stampScorch(x, groundY, z, size) {
    const i = this._scorchCursor;
    this._scorchCursor = (this._scorchCursor + 1) % 220;
    const m = this._gm4 ?? (this._gm4 = new THREE.Matrix4());
    const q = new THREE.Quaternion().setFromAxisAngle(
      new THREE.Vector3(0, 1, 0), Math.random() * Math.PI * 2);
    m.compose(
      new THREE.Vector3(x, groundY + 0.22, z),
      q,
      new THREE.Vector3(size, 1, size)
    );
    this.scorch.setMatrixAt(i, m);
    this.scorch.count = Math.max(this.scorch.count, i + 1);
    this.scorch.instanceMatrix.needsUpdate = true;
  }

  // Collapse dust avalanche — a REAL demolition cloud: dense ground surge
  // rolling outward, a boiling column where the shell stood, and a huge slow
  // canopy of dust that hangs over the site long after the fall.
  plume(cx, baseY, cz, radius, height) {
    // ground surge: a thick ring of fat puffs rolling outward
    const n = Math.min(240, 80 + Math.round(radius * 10));
    for (let k = 0; k < n; k++) {
      const a = Math.random() * Math.PI * 2;
      const r = radius * (0.25 + Math.random() * 0.85);
      this.spawnSmoke(cx + Math.cos(a) * r, baseY + 1 + Math.random() * 4, cz + Math.sin(a) * r, {
        size: 18 + Math.random() * 16,
        dark: 0.25 + Math.random() * 0.2, // concrete dust, not soot
        dur: 6.5,
        vx: Math.cos(a) * (3.5 + Math.random() * 5), // rolls OUTWARD like base surge
        vz: Math.sin(a) * (3.5 + Math.random() * 5),
        vy: 1.6,
        spread: 3.5,
      });
    }
    // boiling column climbing where the building stood
    for (let k = 0; k < 80; k++) {
      this.spawnSmoke(cx, baseY + Math.random() * (height + 6), cz, {
        size: 18 + Math.random() * 16,
        dark: 0.35,
        dur: 7,
        vy: 3 + Math.random() * 4,
        spread: radius * 0.8,
      });
    }
    // lingering canopy: giant, slow, near-still puffs that hang for ~10 s so
    // the site stays buried in haze like a real house collapse
    for (let k = 0; k < 55; k++) {
      const a = Math.random() * Math.PI * 2;
      const r = radius * Math.random() * 1.1;
      this.spawnSmoke(cx + Math.cos(a) * r, baseY + 2 + Math.random() * (height * 0.6 + 4), cz + Math.sin(a) * r, {
        size: 26 + Math.random() * 20,
        dark: 0.24,
        dur: 10.5,
        vy: 0.55,
        vx: (Math.random() - 0.5) * 0.8,
        vz: (Math.random() - 0.5) * 0.8,
        spread: 4,
      });
    }
  }

  update(dt, camPos) {
    this._time += dt;
    this.fMat.uniforms.uTime.value = this._time;
    this.gMat.uniforms.uTime.value = this._time;
    this.eMat.uniforms.uTime.value = this._time;

    // emitters — only pay for what's near the camera. Flame quads are
    // persistent; each burning window just feeds embers + black smoke.
    const NEAR2 = 1400 * 1400;
    for (const [, em] of this.burning) {
      const ax = em.anchors;
      const d2 = (ax[0] - camPos.x) ** 2 + (ax[2] - camPos.z) ** 2;
      if (d2 > NEAR2) continue;
      const nA = ax.length / 3;
      const sc = em.scale ?? 1;
      em.acc += dt * nA * (1.6 + 2.2 * em.intensity) * sc;
      while (em.acc >= 1) {
        em.acc -= 1;
        const a = (Math.random() * nA) | 0;
        this.spawnEmber(ax[a * 3], ax[a * 3 + 1] + 1.4 * sc, ax[a * 3 + 2]);
        if (Math.random() < 0.4) {
          this.spawnSmoke(ax[a * 3], ax[a * 3 + 1] + 1.8 * sc, ax[a * 3 + 2], {
            size: (5.5 + em.intensity * 4) * sc,
            dark: 0.82,
            dur: 3.8 + sc,
            vy: 2.8 + sc,
          });
        }
      }
    }
    for (const [, em] of this.smolder) {
      const ax = em.anchors;
      const d2 = (ax[0] - camPos.x) ** 2 + (ax[2] - camPos.z) ** 2;
      if (d2 > NEAR2) continue;
      em.acc += dt * (ax.length / 3) * 1.1;
      while (em.acc >= 1) {
        em.acc -= 1;
        const a = ((Math.random() * ax.length) / 3) | 0;
        this.spawnSmoke(ax[a * 3], ax[a * 3 + 1], ax[a * 3 + 2], {
          size: 6.5,
          dark: 0.55,
          dur: 4.5,
          vy: 1.6,
          spread: 2.5,
        });
      }
    }

    // flashes swell and die fast
    let flAlive = false;
    for (let i = 0; i < this.flMax; i++) {
      if (this.flLife[i] >= 1) continue;
      this.flLife[i] = Math.min(1, this.flLife[i] + dt / 0.55);
      this.flPos[i * 3 + 1] += 3.5 * dt; // fireball lifts
      flAlive = true;
    }
    if (flAlive || this._flWasAlive) {
      this.flGeo.attributes.position.needsUpdate = true;
      this.flGeo.attributes.life.needsUpdate = true;
    }
    this._flWasAlive = flAlive;

    // shockwave rings expand and fade
    for (let i = this._waves.length - 1; i >= 0; i--) {
      const w = this._waves[i];
      w.t += dt;
      const p = Math.min(1, w.t / w.dur);
      w.mesh.scale.setScalar(w.r * (0.4 + 2.6 * p));
      w.mesh.material.opacity = 0.55 * (1 - p) * (1 - p);
      if (p >= 1) {
        this._scene.remove(w.mesh);
        w.mesh.material.dispose();
        this._waves.splice(i, 1);
      }
    }

    // particle integration — embers sway in the heat column as they rise.
    // Idle systems cost NOTHING: no loop, no GPU re-upload. Only position and
    // life stream per-frame; seed/size/dark upload once at spawn time.
    let eAlive = false;
    for (let i = 0; i < E_MAX; i++) {
      if (this.eLife[i] >= 1) continue;
      eAlive = true;
      this.eLife[i] = Math.min(1, this.eLife[i] + dt / E_LIFE);
      this.eVel[i * 3] += Math.sin(this._time * 7 + this.eSeed[i] * 9) * 3 * dt;
      this.eVel[i * 3 + 2] += Math.cos(this._time * 6 + this.eSeed[i] * 7) * 3 * dt;
      this.ePos[i * 3] += this.eVel[i * 3] * dt;
      this.ePos[i * 3 + 1] += this.eVel[i * 3 + 1] * dt;
      this.ePos[i * 3 + 2] += this.eVel[i * 3 + 2] * dt;
    }
    if (eAlive || this._eWasAlive) {
      this.eGeo.attributes.position.needsUpdate = true;
      this.eGeo.attributes.life.needsUpdate = true;
    }
    this._eWasAlive = eAlive;

    let sAlive = false;
    for (let i = 0; i < S_MAX; i++) {
      if (this.sLife[i] >= 1) continue;
      sAlive = true;
      this.sLife[i] = Math.min(1, this.sLife[i] + dt / this.sDur[i]);
      // base-surge drag: outward speed dies off, lift stays
      this.sVel[i * 3] *= 1 - 0.55 * dt;
      this.sVel[i * 3 + 2] *= 1 - 0.55 * dt;
      this.sPos[i * 3] += this.sVel[i * 3] * dt;
      this.sPos[i * 3 + 1] += this.sVel[i * 3 + 1] * dt;
      this.sPos[i * 3 + 2] += this.sVel[i * 3 + 2] * dt;
    }
    if (sAlive || this._sWasAlive) {
      this.sGeo.attributes.position.needsUpdate = true;
      this.sGeo.attributes.life.needsUpdate = true;
    }
    this._sWasAlive = sAlive;
  }

  dispose() {
    for (const w of this._waves) {
      this._scene.remove(w.mesh);
      w.mesh.material.dispose();
    }
    this.flashes.parent?.remove(this.flashes);
    this.flGeo.dispose();
    this.flMat.dispose();
    this.scorch.parent?.remove(this.scorch);
    this.scorch.geometry.dispose();
    this.scorch.material.dispose();
    this._waveGeo.dispose();
    for (const p of [this.flames, this.smoke, this.glows, this.embers]) p.parent?.remove(p);
    this.flames.geometry.dispose();
    this.fMat.dispose();
    this.sGeo.dispose();
    this.sMat.dispose();
    this.gGeo.dispose();
    this.gMat.dispose();
    this.eGeo.dispose();
    this.eMat.dispose();
  }
}
