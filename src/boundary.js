// Map border — the world ends at the Tbilisi bbox, Air-Gangs style.
// Four holographic neon-blue grid walls stand on the edge; they're invisible
// from downtown, fade in as you fly toward them and physically refuse to let
// the plane through (position clamp + the outward velocity is shed, so you
// slide along the wall instead of sticking to it).

import * as THREE from 'three';

const WALL_BOTTOM = -300;
const WALL_TOP = 5200;
const V_STEP = 500; // m between vertical grid lines
const H_STEP = 420; // m between horizontal grid lines
const FADE_DIST = 2600; // wall becomes visible this far out
const MARGIN = 60; // the plane stops this far inside the wall

export class MapBoundary {
  constructor(scene) {
    this.box = null;
    this.walls = [];
    this.group = new THREE.Group();
    scene.add(this.group);

    fetch('tbilisi/mapmeta.json')
      .then((r) => (r.ok ? r.json() : null))
      .then((m) => {
        if (!m?.cities?.tbilisi) return;
        const [latMin, lonMin, latMax, lonMax] = m.cities.tbilisi;
        const xa = -(lonMax - m.origin.lon) * m.mlon;
        const xb = -(lonMin - m.origin.lon) * m.mlon;
        this.box = {
          minX: Math.min(xa, xb),
          maxX: Math.max(xa, xb),
          minZ: (latMin - m.origin.lat) * m.mlat,
          maxZ: (latMax - m.origin.lat) * m.mlat,
        };
        this._build();
      })
      .catch(() => {});
  }

  _build() {
    const { minX, maxX, minZ, maxZ } = this.box;
    const sides = [
      { a: [minX, minZ], b: [maxX, minZ], axis: 'z', at: minZ },
      { a: [minX, maxZ], b: [maxX, maxZ], axis: 'z', at: maxZ },
      { a: [minX, minZ], b: [minX, maxZ], axis: 'x', at: minX },
      { a: [maxX, minZ], b: [maxX, maxZ], axis: 'x', at: maxX },
    ];
    for (const s of sides) {
      const pts = [];
      const len = Math.hypot(s.b[0] - s.a[0], s.b[1] - s.a[1]);
      const nV = Math.max(2, Math.round(len / V_STEP));
      for (let i = 0; i <= nV; i++) {
        const t = i / nV;
        const x = s.a[0] + (s.b[0] - s.a[0]) * t;
        const z = s.a[1] + (s.b[1] - s.a[1]) * t;
        pts.push(x, WALL_BOTTOM, z, x, WALL_TOP, z);
      }
      const nH = Math.round((WALL_TOP - WALL_BOTTOM) / H_STEP);
      for (let j = 0; j <= nH; j++) {
        const y = WALL_BOTTOM + ((WALL_TOP - WALL_BOTTOM) * j) / nH;
        pts.push(s.a[0], y, s.a[1], s.b[0], y, s.b[1]);
      }
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      const mat = new THREE.LineBasicMaterial({
        color: 0x3fd6ff,
        transparent: true,
        opacity: 0,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        fog: false,
      });
      const line = new THREE.LineSegments(geo, mat);
      line.visible = false;
      line.renderOrder = 5;
      this.group.add(line);
      this.walls.push({ line, mat, axis: s.axis, at: s.at });
    }
  }

  // Fade each wall in by proximity, with a slow holo pulse.
  update(pos, time) {
    if (!this.box) return;
    const pulse = 0.8 + 0.2 * Math.sin(time * 2.4);
    for (const w of this.walls) {
      const d = Math.abs((w.axis === 'x' ? pos.x : pos.z) - w.at);
      const k = Math.max(0, 1 - d / FADE_DIST);
      w.mat.opacity = k * k * 0.6 * pulse;
      w.line.visible = w.mat.opacity > 0.012;
    }
  }

  // Keep a point inside the box. Mutates pos (and vel when given): the
  // outward velocity component dies, the along-wall part survives.
  clamp(pos, vel = null) {
    if (!this.box) return false;
    let hit = false;
    if (pos.x < this.box.minX + MARGIN) {
      pos.x = this.box.minX + MARGIN;
      if (vel && vel.x < 0) vel.x = 0;
      hit = true;
    } else if (pos.x > this.box.maxX - MARGIN) {
      pos.x = this.box.maxX - MARGIN;
      if (vel && vel.x > 0) vel.x = 0;
      hit = true;
    }
    if (pos.z < this.box.minZ + MARGIN) {
      pos.z = this.box.minZ + MARGIN;
      if (vel && vel.z < 0) vel.z = 0;
      hit = true;
    } else if (pos.z > this.box.maxZ - MARGIN) {
      pos.z = this.box.maxZ - MARGIN;
      if (vel && vel.z > 0) vel.z = 0;
      hit = true;
    }
    return hit;
  }

  dispose() {
    for (const w of this.walls) {
      w.line.geometry.dispose();
      w.mat.dispose();
    }
    this.group.parent?.remove(this.group);
  }
}
