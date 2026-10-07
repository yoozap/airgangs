import * as THREE from 'three';

// Lightweight one-draw-call smoke: a ring buffer of points that spawn at the
// damaged wing, drift up and fade out. Dead particles keep life = 1 → alpha 0.
const MAX = 140;
const LIFETIME = 1.9; // seconds

export class SmokeTrail {
  constructor(scene) {
    this.positions = new Float32Array(MAX * 3);
    this.life = new Float32Array(MAX).fill(1);
    this.vel = new Float32Array(MAX * 3);
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
          vLife = life;
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          gl_PointSize = (2.0 + life * 26.0) * (170.0 / max(1.0, -mv.z));
          gl_Position = projectionMatrix * mv;
        }`,
      fragmentShader: `
        varying float vLife;
        void main() {
          float d = distance(gl_PointCoord, vec2(0.5));
          float a = smoothstep(0.5, 0.12, d) * (1.0 - vLife) * 0.55;
          if (a < 0.01) discard;
          gl_FragColor = vec4(mix(vec3(0.13), vec3(0.42), vLife), a);
        }`,
    });

    this.points = new THREE.Points(this.geo, this.mat);
    this.points.frustumCulled = false;
    scene.add(this.points);
  }

  spawn(pos) {
    const i = this.cursor;
    this.cursor = (this.cursor + 1) % MAX;
    this.positions[i * 3] = pos.x;
    this.positions[i * 3 + 1] = pos.y;
    this.positions[i * 3 + 2] = pos.z;
    this.life[i] = 0.001;
    this.vel[i * 3] = (Math.random() - 0.5) * 2;
    this.vel[i * 3 + 1] = 2 + Math.random() * 2;
    this.vel[i * 3 + 2] = (Math.random() - 0.5) * 2;
  }

  update(dt) {
    for (let i = 0; i < MAX; i++) {
      if (this.life[i] >= 1) continue;
      this.life[i] = Math.min(1, this.life[i] + dt / LIFETIME);
      this.positions[i * 3] += this.vel[i * 3] * dt;
      this.positions[i * 3 + 1] += this.vel[i * 3 + 1] * dt;
      this.positions[i * 3 + 2] += this.vel[i * 3 + 2] * dt;
    }
    this.geo.attributes.position.needsUpdate = true;
    this.geo.attributes.life.needsUpdate = true;
  }

  clear() {
    this.life.fill(1);
    this.geo.attributes.life.needsUpdate = true;
  }

  dispose() {
    this.points.parent?.remove(this.points);
    this.geo.dispose();
    this.mat.dispose();
  }
}
