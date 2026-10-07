import * as THREE from 'three';
import { fxAudio, detonation, debrisRain, collapseRumble } from './audiofx.js';

// Wing-mounted rocket launcher — skill 2 on the action bar. One heavy rocket
// per trigger pull with a WoW-style cooldown, alternating left/right
// hardpoints. Rockets fly as real projectiles with an engine glow and a smoke
// trail, and hit buildings ~30× harder than a machine-gun round — the
// demolition tool. Explosion visuals reuse the gun's dust/decals plus
// FireFX's big flash.
const COOLDOWN = 1.5; // s between rockets
const DAMAGE = 260; // hp — buildings carry 100–800
const BLAST_R = 6.5; // m — fireball / crater scale
const HOLE_R = 3.1; // m — radius of wall actually torn out of the facade
const GLASS_R = 22; // m — every window this close to the hit blows out
const RANGE = 1600; // m before a dud self-destructs
const ACCEL = 340; // m/s² motor burn
const TOP_SPEED = 430; // m/s
const MAX_LIVE = 6;

// Dense white-gray propellant trail — its own ring buffer so wing-damage
// smoke and rocket exhaust never fight over particles.
const TRAIL_MAX = 360;
const TRAIL_LIFE = 2.4;

class RocketTrail {
  constructor(scene) {
    this.positions = new Float32Array(TRAIL_MAX * 3);
    this.life = new Float32Array(TRAIL_MAX).fill(1);
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
          gl_PointSize = (1.5 + life * 20.0) * (170.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5));
          float a = smoothstep(0.5, 0.1, d) * (1.0 - vLife) * 0.5;
          if (a < 0.01) discard;
          gl_FragColor = vec4(mix(vec3(0.92, 0.9, 0.86), vec3(0.55, 0.55, 0.58), vLife), a);
        }`,
    });
    this.points = new THREE.Points(this.geo, this.mat);
    this.points.frustumCulled = false;
    scene.add(this.points);
  }

  spawn(pos) {
    const i = this.cursor;
    this.cursor = (this.cursor + 1) % TRAIL_MAX;
    this.positions[i * 3] = pos.x + (Math.random() - 0.5) * 0.3;
    this.positions[i * 3 + 1] = pos.y + (Math.random() - 0.5) * 0.3;
    this.positions[i * 3 + 2] = pos.z + (Math.random() - 0.5) * 0.3;
    this.life[i] = 0.001;
  }

  update(dt) {
    let alive = false;
    for (let i = 0; i < TRAIL_MAX; i++) {
      if (this.life[i] >= 1) continue;
      alive = true;
      this.life[i] = Math.min(1, this.life[i] + dt / TRAIL_LIFE);
      this.positions[i * 3 + 1] += 1.2 * dt; // exhaust drifts up as it cools
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

// Launch whoosh — this one happens at the wing, so it plays dry. The
// detonation downrange goes through audiofx.js, where distance becomes
// propagation delay, air absorption and the street's own echo.
class RocketSound {
  whoosh(vol) {
    if (!fxAudio.ensure()) return;
    const ctx = fxAudio.ctx;
    const t = ctx.currentTime;
    // motor ignition: a bandpass sweeping up as the rocket accelerates away
    const src = ctx.createBufferSource();
    src.buffer = fxAudio.noise;
    src.playbackRate.value = 0.8;
    const bp = ctx.createBiquadFilter();
    bp.type = 'bandpass';
    bp.Q.value = 0.9;
    bp.frequency.setValueAtTime(300, t);
    bp.frequency.exponentialRampToValueAtTime(2400, t + 0.45);
    const g = ctx.createGain();
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(0.6 * vol, t + 0.06);
    g.gain.exponentialRampToValueAtTime(0.001, t + 0.7);
    src.connect(bp).connect(g).connect(fxAudio.master);
    src.start(t);
    src.stop(t + 0.75);
    // the hard bang of the launch tube
    const o = ctx.createOscillator();
    o.type = 'triangle';
    o.frequency.setValueAtTime(150, t);
    o.frequency.exponentialRampToValueAtTime(48, t + 0.12);
    const og = ctx.createGain();
    og.gain.setValueAtTime(0.35 * vol, t);
    og.gain.exponentialRampToValueAtTime(0.001, t + 0.15);
    o.connect(og).connect(fxAudio.master);
    o.start(t);
    o.stop(t + 0.16);
  }

  dispose() {}
}

export class RocketLauncher {
  constructor(game) {
    this.game = game;
    this._cool = 0;
    this._side = 1; // alternate wing hardpoints
    this.live = [];
    this.sound = new RocketSound();
    this.trail = new RocketTrail(game.scene);
    this.raycaster = new THREE.Raycaster();
    this.raycaster.firstHitOnly = true;
    this._v = new THREE.Vector3();

    // --- rocket template: body + magenta nose + fins + engine glow sprite ---
    const tmpl = new THREE.Group();
    const bodyMat = new THREE.MeshStandardMaterial({ color: 0x9fb6c4, metalness: 0.6, roughness: 0.35 });
    const noseMat = new THREE.MeshStandardMaterial({ color: 0xd42e6e, metalness: 0.3, roughness: 0.45 });
    const body = new THREE.Mesh(new THREE.CylinderGeometry(0.13, 0.13, 1.5, 10), bodyMat);
    body.rotation.x = Math.PI / 2;
    tmpl.add(body);
    const nose = new THREE.Mesh(new THREE.ConeGeometry(0.13, 0.42, 10), noseMat);
    nose.rotation.x = -Math.PI / 2;
    nose.position.z = -0.96;
    tmpl.add(nose);
    for (let k = 0; k < 4; k++) {
      const fin = new THREE.Mesh(new THREE.BoxGeometry(0.02, 0.3, 0.34), noseMat);
      const a = (k / 4) * Math.PI * 2 + Math.PI / 4;
      fin.position.set(Math.cos(a) * 0.2, Math.sin(a) * 0.2, 0.62);
      fin.rotation.z = a + Math.PI / 2;
      tmpl.add(fin);
    }
    const c = document.createElement('canvas');
    c.width = c.height = 64;
    const g2 = c.getContext('2d');
    const grad = g2.createRadialGradient(32, 32, 2, 32, 32, 30);
    grad.addColorStop(0, 'rgba(255,250,230,1)');
    grad.addColorStop(0.4, 'rgba(255,190,90,0.9)');
    grad.addColorStop(0.75, 'rgba(255,62,138,0.4)');
    grad.addColorStop(1, 'rgba(255,62,138,0)');
    g2.fillStyle = grad;
    g2.fillRect(0, 0, 64, 64);
    this.glowMat = new THREE.SpriteMaterial({
      map: new THREE.CanvasTexture(c),
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
      toneMapped: false,
    });
    const glow = new THREE.Sprite(this.glowMat);
    glow.scale.setScalar(1.5);
    glow.position.z = 0.95;
    tmpl.add(glow);
    this.tmpl = tmpl;
  }

  get cooldownFrac() {
    return Math.max(0, this._cool / COOLDOWN);
  }

  update(dt) {
    const g = this.game;
    this._cool -= dt;
    g.skillbar?.setCooldown(1, this.cooldownFrac);
    const firing =
      g.started && !g.crashed && !g.placement?.active && !g.foot?.active &&
      g.skillbar?.current === 'rocket' && g.keys.has('Space');
    if (firing && this._cool <= 0 && this.live.length < MAX_LIVE) {
      this._cool = COOLDOWN;
      this._fire();
    }
    this._updateRockets(dt);
    this.trail.update(dt);
  }

  _fire() {
    const g = this.game;
    this._side = -this._side;
    const pos = g.plane.localToWorld(new THREE.Vector3(this._side * 1.7, -0.55, -0.9));
    const dir = new THREE.Vector3(0, 0, -1).applyQuaternion(g.plane.quaternion).normalize();
    const r = this.tmpl.clone();
    r.position.copy(pos);
    r.quaternion.copy(g.plane.quaternion);
    g.scene.add(r);
    // inherit the plane's speed so the launcher never overtakes its own shot
    this.live.push({ obj: r, dir, speed: Math.max(g.speed + 50, 130), dist: 0, puff: 0 });
    this.sound.whoosh(g.sound.volume ?? 0.7);
    g.skillbar?.pulse(1);
  }

  // Swept per frame for every live rocket — the spatial prefilter matters:
  // testing all ~5k collidable tiles cost a matrix invert each, per rocket.
  _targets(x, z) {
    const g = this.game;
    const near = g.world.collidablesNear ? g.world.collidablesNear(x, z) : g.world.collidables;
    return g.world.shootables?.length ? near.concat(g.world.shootables) : near;
  }

  _updateRockets(dt) {
    for (let i = this.live.length - 1; i >= 0; i--) {
      const rk = this.live[i];
      rk.speed = Math.min(TOP_SPEED, rk.speed + ACCEL * dt);
      const step = rk.speed * dt;
      // swept hit test: nothing thinner than one frame's travel gets tunneled
      this.raycaster.set(rk.obj.position, rk.dir);
      this.raycaster.far = step + 1.5;
      const hits = this.raycaster.intersectObjects(this._targets(rk.obj.position.x, rk.obj.position.z), true);
      this.raycaster.far = Infinity;
      if (hits.length) {
        this._impact(rk, hits[0]);
        this._remove(i);
        continue;
      }
      rk.obj.position.addScaledVector(rk.dir, step);
      rk.dist += step;
      rk.puff += dt;
      if (rk.puff > 0.016) {
        rk.puff = 0;
        this.trail.spawn(this._v.copy(rk.obj.position).addScaledVector(rk.dir, -1.0));
      }
      if (rk.dist > RANGE) this._remove(i); // dud — burned out
    }
  }

  _remove(i) {
    const rk = this.live[i];
    rk.obj.parent?.remove(rk.obj);
    this.live.splice(i, 1);
  }

  _impact(rk, hit) {
    const g = this.game;
    const p = hit.point;
    const vol = g.sound.volume ?? 0.7;
    // Blast axis: the surface normal, flipped to face the shooter (instanced
    // and merged meshes happily report the back face of a wall).
    let n = g.guns._hitNormal(hit);
    if (!n) n = rk.dir.clone().negate();
    if (n.dot(rk.dir) > 0) n.negate();

    // The damage lands FIRST: if this is the killing shot the collapse takes
    // over (it blows the whole facade's glass itself) and our blast just
    // dresses the opening act.
    const res = g.world.damageHit?.(hit, DAMAGE);

    // ---- the detonation: fireball, flash, pressure ring, crater, soot ----
    // NOTE: no fx.explosion() here — its additive glow sprites bleach the
    // whole hit to cream over a bright sky. The volumetric fireball IS the
    // explosion now, and it keeps its orange.
    g.blast?.explode(p, n, { r: BLAST_R });
    g.guns.dust.burst(p, 54, 6.5);
    g.guns.holes.stamp(p, n, 6);
    g.world.onShake?.(0.85, p.x, p.z);
    // the bang travels: close up it's a crack you feel, from a kilometer out
    // it's a dull thump followed by echoes off the ridges
    const dist = g.camera.position.distanceTo(p);
    detonation(dist, vol, 1);
    debrisRain(dist, vol, res ? 1.4 : 0.8); // masonry clattering down after

    // ---- the wall itself: a real hole, right where it hit ----
    // Cells inside the blast sphere leave the facade, so the damage takes the
    // shape of what you actually hit — a hole in the middle of a wall, a
    // sheared-off corner if you put it on the corner.
    const holeR = HOLE_R * (0.85 + Math.random() * 0.3);
    const punched = res?.rec ? g.world.punchHole?.(res.rec, p, holeR) : null;
    // everything bolted on inside the hole goes with it (glass comes back)
    const ripped = punched ? g.world.stripPropsAt?.(p.x, p.y, p.z, holeR * 1.1) : null;
    if (ripped?.length) g.glass?.burstList(ripped, 1.9);

    // ---- glass: every pane inside the blast sphere leaves its frame ----
    const blown = g.world.blastWindows?.(p.x, p.y, p.z, GLASS_R);
    if (blown?.length) g.glass?.burstList(blown, 1.7);

    // chunks torn out of whatever was hit
    g.world.blastDebris?.(p.x, p.y, p.z, n, res ? 12 : 7);
    if (!res) return; // ground / bridge / statue — bang, crater, dirt

    // ---- building: a fire is left burning in the hole ----
    g.world.igniteBlast?.(res, p, BLAST_R);
    g.guns._showToast(res);
    if (res.changed) {
      collapseRumble(dist, vol, res.destroyed);
      if (!res.destroyed) {
        // the shell dropped a whole stage: dust sheets off the new roofline
        const plume = this._v.set(res.cx, res.topNow, res.cz).clone();
        g.guns.dust.burst(plume, 34, Math.max(4, res.span * 0.6));
        g.fx?.plume(res.cx, res.topNow - 2, res.cz, res.span * 0.35, 4);
      }
    }
  }

  dispose() {
    for (const rk of this.live) rk.obj.parent?.remove(rk.obj);
    this.live.length = 0;
    this.tmpl.traverse((o) => {
      if (o.isMesh) {
        o.geometry.dispose();
        o.material.dispose();
      }
    });
    this.glowMat.map?.dispose();
    this.glowMat.dispose();
    this.trail.dispose();
    this.sound.dispose();
  }
}
