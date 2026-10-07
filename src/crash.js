import * as THREE from 'three';

// Cinematic crash sequence, GTA-style but physically staged:
//  1. impact — fireball + flash + debris burst, the hull chars instantly
//  2. tumble — the burning wreck keeps its momentum, falls and spins,
//     trailing fire and black smoke (brief slow-mo for drama)
//  3. ground hit — bounce, second fireball, dust plume
//  4. rest — persistent flames on the wreck via FireFX, then WASTED.
// The whole thing is owned by Game: created in _explode(), updated from
// _tick() while crashed, disposed in resetPlane().

// Real-ish gravity for the tumbling hull; the torn-off airframe parts
// (breakup.js) fall at true 9.81 with aerodynamic flutter.
const GRAVITY = 14;
const DEBRIS_N = 16;

const FIREBALL_GLSL = `
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
    for (int k = 0; k < 3; k++) {
      v += a * n3(p);
      p = p * 2.13 + vec3(9.7, 3.1, 7.3);
      a *= 0.5;
    }
    return v;
  }`;

function makeFireballMaterial() {
  // Normal blending, NOT additive: additive over a bright sky washes the
  // whole ball to white — alpha blending keeps the orange saturated.
  return new THREE.ShaderMaterial({
    transparent: true,
    depthWrite: false,
    uniforms: { uT: { value: 0 }, uSeed: { value: Math.random() * 20 } },
    vertexShader: `
      uniform float uT;
      uniform float uSeed;
      varying float vD;
      ${FIREBALL_GLSL}
      void main() {
        // boiling surface: noise pushed along the normals, churning upward
        float d = fbm3(normal * 2.2 + vec3(uSeed) + vec3(0.0, -uT * 1.6, 0.0));
        vD = d;
        vec3 p = position * (1.0 + (d - 0.5) * 0.95);
        gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
      }`,
    fragmentShader: `
      uniform float uT;
      varying float vD;
      void main() {
        // blackbody cool-down: deep red rim -> orange body -> a white-hot
        // core that only survives the first instants of the blast
        float heat = clamp(vD * 1.3 - uT * 1.15 + 0.25, 0.0, 1.0);
        vec3 c = mix(vec3(0.22, 0.012, 0.001), vec3(0.95, 0.16, 0.006), smoothstep(0.1, 0.62, heat));
        c = mix(c, vec3(1.0, 0.5, 0.1), smoothstep(0.8, 1.0, heat) * (1.0 - uT));
        // the noise CARVES the edge — ragged burning lobes, not a clean ball.
        // Nearly opaque body: a see-through fireball over a bright sky reads
        // as pale cream, an opaque one keeps its orange.
        float body = smoothstep(0.14, 0.45, vD + 0.3 * (1.0 - uT));
        // steep late fade with a hard cutoff by 3/4 life: the ball must hand
        // over to the black smoke, not linger as a glassy dome over the city
        float a = min(pow(1.0 - uT, 2.2) * body * 2.2, 0.97);
        a *= 1.0 - smoothstep(0.5, 0.75, uT);
        if (a < 0.02) discard;
        gl_FragColor = vec4(c, a);
      }`,
  });
}

export class CrashSequence {
  constructor(game, hitNormal = null) {
    this.game = game;
    this.t = 0;
    this.timeScale = 0.45; // slow-mo at impact, ramps back to 1 in update()
    this.phase = 'tumble';
    this.bounced = false;
    this.wastedIn = null;
    this._trailAcc = 0;

    const plane = game.plane;
    this.size = plane.userData.size ?? 12;
    this.groundEst =
      game._groundAt(plane.position.x, plane.position.z) ??
      plane.position.y - game.lastGroundDistance;

    // Wreck momentum: the hull keeps part of its true velocity VECTOR (the
    // flight path, not the nose); a wall smash kicks it back along the
    // surface normal instead of through the facade.
    const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(plane.quaternion);
    this.vel =
      game.airborne && game.vel.lengthSq() > 4
        ? game.vel.clone().multiplyScalar(0.45)
        : fwd.multiplyScalar(Math.max(8, Math.abs(game.speed) * 0.4));
    if (hitNormal) {
      const n = new THREE.Vector3(hitNormal.x, hitNormal.y, hitNormal.z).normalize();
      this.vel.reflect(n).multiplyScalar(0.35);
    }
    this.vel.y = Math.min(this.vel.y, 4);
    this.angVel = new THREE.Vector3(
      (Math.random() - 0.5) * 5,
      (Math.random() - 0.5) * 3,
      (Math.random() - 0.5) * 6
    );

    this.group = new THREE.Group();
    game.scene.add(this.group);
    this.fireballs = [];
    this._sph = new THREE.IcosahedronGeometry(1, 3);

    // Fixed cinematic vantage point (GTA death cam): back and off to the side
    // of the impact, clear of the fireball — the camera settles there and
    // watches the wreck fall instead of chasing it through the flames.
    const back = game.camera.position.clone().sub(plane.position);
    back.y = 0;
    if (back.lengthSq() < 1) back.set(0, 0, 1);
    back.normalize();
    const side = new THREE.Vector3(-back.z, 0, back.x).multiplyScalar(
      (Math.random() < 0.5 ? -1 : 1) * 13 * game.camScale
    );
    this.camAnchor = plane.position
      .clone()
      .addScaledVector(back, 32 * game.camScale)
      .add(side)
      .add(new THREE.Vector3(0, 9 * game.camScale, 0));
    this.camAnchor.y = Math.max(this.camAnchor.y, this.groundEst + 4);

    this._charPlane();
    this._spawnDebris();
    this._boom(plane.position, 1);
  }

  // ---- staging ----

  _charPlane() {
    // The hull is engulfed by the fireball — char every material. Original
    // colors are restored in dispose() (resetPlane).
    this._matBackup = [];
    const seen = new Set();
    this.game.plane.traverse((o) => {
      if (!o.isMesh) return;
      for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
        if (seen.has(m)) continue;
        seen.add(m);
        this._matBackup.push([m, m.color.getHex(), m.roughness]);
        m.color.setHex(0x211e1a);
        m.roughness = 1;
      }
    });
  }

  _spawnDebris() {
    const plane = this.game.plane;
    const s = this.size / 11; // chunk scale relative to a Spitfire
    this.debris = [];
    const mats = [
      new THREE.MeshStandardMaterial({ color: 0x2a2622, roughness: 1 }),
      new THREE.MeshStandardMaterial({ color: 0x4a443c, roughness: 0.9 }),
    ];
    this._debrisMats = mats;
    for (let i = 0; i < DEBRIS_N; i++) {
      const geo = new THREE.TetrahedronGeometry((0.25 + Math.random() * 0.6) * s, 0);
      const mesh = new THREE.Mesh(geo, mats[i % mats.length]);
      mesh.position.copy(plane.position);
      mesh.castShadow = true;
      const a = Math.random() * Math.PI * 2;
      const up = Math.random();
      const sp = 9 + Math.random() * 22;
      const vel = new THREE.Vector3(Math.cos(a) * (1 - up), up, Math.sin(a) * (1 - up))
        .normalize()
        .multiplyScalar(sp)
        .addScaledVector(this.vel, 0.4);
      this.group.add(mesh);
      this.debris.push({
        mesh,
        vel,
        angVel: new THREE.Vector3().randomDirection().multiplyScalar(4 + Math.random() * 8),
        alive: true,
        hot: i < 8, // half the chunks trail fire/smoke
        restT: 0,
      });
    }
  }

  _boom(pos, scale) {
    const R = this.size * 0.85 * scale;
    const mesh = new THREE.Mesh(this._sph, makeFireballMaterial());
    mesh.position.copy(pos);
    mesh.scale.setScalar(R * 0.3);
    // BORROWED from the Game's permanent pool, never constructed here: a new
    // PointLight changes the scene's light count, which invalidates the shader
    // cache key of every material on screen. Measured: 36 programs recompiled
    // in the fireball's first frame = a 0.8 s freeze in the middle of the
    // crash cinematic. See Game._setupScene().
    const light = this.game.takeFxLight(0xff8033, 3500 * scale * scale, R * 18, 1.8);
    light.position.copy(pos).add(new THREE.Vector3(0, R * 0.4, 0));
    this.group.add(mesh);   // the light stays in the scene, it is not ours
    this.fireballs.push({ mesh, light, t: 0, dur: 0.95, R, scale });

    // ember shrapnel + a thick black mushroom rolling out of the blast
    const fx = this.game.fx;
    const n = Math.round(45 * scale);
    for (let i = 0; i < n; i++) fx.spawnEmber(pos.x, pos.y, pos.z);
    for (let i = 0; i < Math.round(40 * scale); i++) {
      const a = Math.random() * Math.PI * 2;
      fx.spawnSmoke(pos.x, pos.y + Math.random() * R * 0.8, pos.z, {
        size: (9 + Math.random() * 9) * scale,
        dark: 0.88,
        dur: 5.5,
        vx: Math.cos(a) * 2.5,
        vz: Math.sin(a) * 2.5,
        vy: 4.5,
        spread: R * 0.5,
      });
    }
  }

  _settle(gY) {
    const plane = this.game.plane;
    this.phase = 'burning';
    plane.position.y = gY + 1.0; // belly in the dirt, gear gone
    // Mostly flat, but visibly wrong: a broken machine, not a parked one.
    const e = new THREE.Euler().setFromQuaternion(plane.quaternion, 'YXZ');
    e.x = THREE.MathUtils.clamp(e.x * 0.3, -0.2, 0.2) + 0.06;
    e.z = THREE.MathUtils.clamp(e.z * 0.3, -0.3, 0.3) + (Math.random() < 0.5 ? -0.14 : 0.14);
    plane.quaternion.setFromEuler(e);

    // Persistent licking flames along the fuselage (FireFX billboards).
    const anchors = [];
    for (const zf of [-0.32, -0.12, 0.08, 0.26]) {
      const p = plane.localToWorld(
        new THREE.Vector3((Math.random() - 0.5) * this.size * 0.12, 0.8, zf * this.size)
      );
      anchors.push(p.x, p.y, p.z);
    }
    this.game.fx.setBurning('crash:plane', new Float32Array(anchors), 1.0);
    this.game.fx.plume(plane.position.x, gY, plane.position.z, this.size * 0.6, 4);

    // Dolly the death cam in around the final wreck for the WASTED shot.
    const toCam = this.game.camera.position.clone().sub(plane.position);
    toCam.y = 0;
    if (toCam.lengthSq() < 1) toCam.set(0, 0, 1);
    toCam.normalize();
    this.camAnchor = plane.position
      .clone()
      .addScaledVector(toCam, 26 * this.game.camScale)
      .add(new THREE.Vector3(0, 8 * this.game.camScale, 0));
    this.wastedIn = 1.1;
  }

  // ---- per-frame ----

  update(dt) {
    this.t += dt;
    // Slow-mo through the first second of the disaster, then back to speed.
    this.timeScale = this.t < 1.0 ? 0.45 : Math.min(1, 0.45 + (this.t - 1.0) * 0.9);
    const sdt = dt * this.timeScale;
    const game = this.game;
    const plane = game.plane;

    if (this.phase === 'tumble') {
      this.vel.y -= GRAVITY * sdt;
      this.vel.multiplyScalar(1 - 0.12 * sdt);
      plane.position.addScaledVector(this.vel, sdt);
      const av = this.angVel;
      plane.rotateX(av.x * sdt);
      plane.rotateY(av.y * sdt);
      plane.rotateZ(av.z * sdt);

      // Fire trail pouring off the falling wreck.
      this._trailAcc += sdt;
      if (this._trailAcc > 0.03) {
        this._trailAcc = 0;
        const p = plane.position;
        game.fx.spawnEmber(p.x, p.y, p.z);
        game.fx.spawnSmoke(p.x, p.y + 1, p.z, {
          size: 6 + Math.random() * 4,
          dark: 0.85,
          dur: 3.5,
          vy: 1.6,
          spread: this.size * 0.18,
        });
      }

      const gY = game._groundAt(plane.position.x, plane.position.z) ?? this.groundEst;
      if (plane.position.y <= gY + 1.4) {
        if (!this.bounced && this.vel.length() > 14) {
          this.bounced = true;
          plane.position.y = gY + 1.4;
          this.vel.y = Math.abs(this.vel.y) * 0.3;
          this.vel.x *= 0.45;
          this.vel.z *= 0.45;
          this.angVel.multiplyScalar(0.55);
          this._boom(plane.position, 0.55);
          game.sound.explosion(0.55);
          game.fx.plume(plane.position.x, gY, plane.position.z, this.size * 0.7, 5);
        } else {
          this._settle(gY);
        }
      }
    } else if (this.wastedIn !== null) {
      this.wastedIn -= dt;
      if (this.wastedIn <= 0) {
        this.wastedIn = null;
        game._showWasted();
      }
    }
    // Safety valve: never leave the player without the restart button.
    if (this.t > 6) game._showWasted();

    this._updateDebris(sdt);
    this._updateFireballs(sdt);
    this._updateCamera(dt);
  }

  _updateDebris(sdt) {
    for (const d of this.debris) {
      if (!d.alive) continue;
      d.vel.y -= GRAVITY * sdt;
      d.mesh.position.addScaledVector(d.vel, sdt);
      d.mesh.rotateX(d.angVel.x * sdt);
      d.mesh.rotateY(d.angVel.y * sdt);
      d.mesh.rotateZ(d.angVel.z * sdt);
      if (d.hot && Math.random() < 0.35) {
        const p = d.mesh.position;
        this.game.fx.spawnEmber(p.x, p.y, p.z);
        if (Math.random() < 0.3) {
          this.game.fx.spawnSmoke(p.x, p.y, p.z, { size: 2.5, dark: 0.8, dur: 1.6, vy: 0.8, spread: 0.3 });
        }
      }
      if (d.mesh.position.y <= this.groundEst + 0.3) {
        d.mesh.position.y = this.groundEst + 0.3;
        d.vel.y = Math.abs(d.vel.y) * 0.35;
        d.vel.x *= 0.55;
        d.vel.z *= 0.55;
        d.angVel.multiplyScalar(0.5);
        if (d.vel.length() < 2.5) d.alive = false;
      }
    }
  }

  _updateFireballs(sdt) {
    for (const f of this.fireballs) {
      if (f.t >= 1) continue;
      f.t = Math.min(1, f.t + sdt / f.dur);
      f.mesh.material.uniforms.uT.value = f.t;
      // fast initial blast, then the slow roll upward as it mushrooms
      f.mesh.scale.setScalar(f.R * (0.3 + 0.7 * Math.pow(f.t, 0.42)));
      f.mesh.position.y += 3.5 * sdt;
      f.light.intensity = 3500 * f.scale * f.scale * (1 - f.t) * (1 - f.t);
      if (f.t >= 1) {
        f.mesh.visible = false;
        f.light.intensity = 0;
      }
    }
  }

  _updateCamera(dt) {
    // Ease into the fixed vantage point and track the falling wreck from
    // there; a hard shake right at impact that dies off exponentially.
    const game = this.game;
    const cam = game.camera;
    const p = game.plane.position;
    cam.position.lerp(this.camAnchor, 1 - Math.exp(-2.0 * dt));
    const shake = Math.exp(-2.4 * this.t) * 2.2;
    cam.lookAt(
      p.x + (Math.random() - 0.5) * shake,
      p.y + 2 + (Math.random() - 0.5) * shake,
      p.z + (Math.random() - 0.5) * shake
    );
  }

  dispose() {
    for (const [m, hex, rough] of this._matBackup) {
      m.color.setHex(hex);
      m.roughness = rough;
    }
    this.game.fx.clearBurning('crash:plane');
    for (const d of this.debris) d.mesh.geometry.dispose();
    for (const f of this.fireballs) {
      f.mesh.material.dispose();
      this.game.freeFxLight(f.light);   // pooled — hand it back dark
    }
    for (const m of this._debrisMats) m.dispose();
    this._sph.dispose();
    this.group.parent?.remove(this.group);
  }
}
