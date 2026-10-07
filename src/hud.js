// Combat HUD — a real fighter head-up display drawn over the 3D view:
// pitch ladder in true degrees (rungs lie on the world horizon, whatever the
// camera does), flight-path marker on the actual velocity vector, heading
// tape, bank scale, gun reticle with live boresight range, and the airdata
// column (speed / altitude / radar alt / G / AoA / Mach) with STALL and VNE
// warnings. Replaces the plain crosshair; guns.js hides its DOM reticle
// while this is alive.
//
// Every mark is drawn twice — a dark halo pass under the cyan pass — so the
// symbology stays readable over the white sky and the pale city haze alike.

import * as THREE from 'three';

const D = 420; // boresight projection distance, m — matches the gun crosshair
const CYAN = 'rgba(140, 240, 255, 0.62)';
const CYAN_DIM = 'rgba(130, 235, 255, 0.42)';
const MAG = 'rgba(255, 82, 150, 0.8)';
const HALO = 'rgba(4, 20, 30, 0.35)';
const MONO = '"JetBrains Mono", monospace';

export class CombatHUD {
  constructor(game) {
    this.game = game;
    document.getElementById('combathud')?.remove();
    this.canvas = document.createElement('canvas');
    this.canvas.id = 'combathud';
    game.ui.hud.prepend(this.canvas); // first child: everything else stacks above
    this.ctx = this.canvas.getContext('2d');
    this.range = null; // boresight ray hit distance, sampled on a slow tick
    this._rangeT = 0;
    this._t = 0;
    this._v = new THREE.Vector3();
    this._v2 = new THREE.Vector3();
    this._vp = new THREE.Vector3(); // _proj scratch — NEVER shared with callers
    // more scratch — every mark used to allocate its own Vector3s, ~60 a frame
    // of pure garbage for the collector to sweep up mid-flight
    this._s1 = new THREE.Vector3();
    this._s2 = new THREE.Vector3();
    this._s3 = new THREE.Vector3();
    this._s4 = new THREE.Vector3();
    this._up = new THREE.Vector3(0, 1, 0);
    this.visible = true;
  }

  // Walking around is not flying: the ladder, tapes, airdata and gun reticle
  // all read the PLANE's attitude, so on foot they are meaningless. Hidden
  // rather than frozen, and update() bails out so nothing is drawn either.
  setVisible(on) {
    this.visible = on;
    this.canvas.style.display = on ? '' : 'none';
  }

  // world point -> screen px, null when behind the camera
  _proj(v, W, H) {
    const p = this._vp.copy(v).project(this.game.camera);
    if (p.z > 1) return null;
    return [(p.x * 0.5 + 0.5) * W, (-p.y * 0.5 + 0.5) * H];
  }

  // ---- two-pass primitives: dark halo under, color over ----

  _line(x1, y1, x2, y2, color, width = 0.9, dash = null) {
    const ctx = this.ctx;
    if (dash) ctx.setLineDash(dash);
    for (const [c, w] of [[HALO, width + 1.4], [color, width]]) {
      ctx.strokeStyle = c;
      ctx.lineWidth = w;
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.stroke();
    }
    if (dash) ctx.setLineDash([]);
  }

  _circle(x, y, r, color, width = 1.0, fill = false) {
    const ctx = this.ctx;
    for (const [c, w] of [[HALO, width + 1.4], [color, width]]) {
      ctx.strokeStyle = c;
      ctx.lineWidth = w;
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      if (fill && c === color) {
        ctx.fillStyle = c;
        ctx.fill();
      } else ctx.stroke();
    }
  }

  _rect(x, y, w, h, color, width = 0.9) {
    const ctx = this.ctx;
    for (const [c, lw] of [[HALO, width + 1.4], [color, width]]) {
      ctx.strokeStyle = c;
      ctx.lineWidth = lw;
      ctx.strokeRect(x, y, w, h);
    }
  }

  _text(str, x, y, color, font = `10px ${MONO}`, align = 'center') {
    const ctx = this.ctx;
    ctx.font = font;
    ctx.textAlign = align;
    ctx.lineJoin = 'round';
    ctx.lineWidth = 2;
    ctx.strokeStyle = HALO;
    ctx.strokeText(str, x, y);
    ctx.fillStyle = color;
    ctx.fillText(str, x, y);
  }

  _poly(pts, color) {
    const ctx = this.ctx;
    ctx.beginPath();
    pts.forEach(([px, py], i) => (i ? ctx.lineTo(px, py) : ctx.moveTo(px, py)));
    ctx.closePath();
    ctx.lineJoin = 'round';
    ctx.lineWidth = 2;
    ctx.strokeStyle = HALO;
    ctx.stroke();
    ctx.fillStyle = color;
    ctx.fill();
  }

  // ---- frame ----

  update(dt) {
    const g = this.game;
    // Nothing to draw: bail BEFORE the canvas work. On foot this is hidden
    // (display:none), and clearing a 2800x1800 retina canvas for an invisible
    // element was pure cost every frame.
    if (!this.visible || !g.started || g.crashed) {
      if (this._dirty) { this.ctx.setTransform(1, 0, 0, 1, 0, 0); this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height); this._dirty = false; }
      return;
    }
    this._dirty = true;
    const W = g.container.clientWidth;
    const H = g.container.clientHeight;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (this.canvas.width !== W * dpr || this.canvas.height !== H * dpr) {
      this.canvas.width = W * dpr;
      this.canvas.height = H * dpr;
      this.canvas.style.width = `${W}px`;
      this.canvas.style.height = `${H}px`;
    }
    const ctx = this.ctx;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W, H);
    this._t += dt;

    const pos = g.plane.position;
    const fwd = this._v.set(0, 0, -1).applyQuaternion(g.plane.quaternion);
    const right = this._v2.set(1, 0, 0).applyQuaternion(g.plane.quaternion);
    const up = this._s1.set(0, 1, 0).applyQuaternion(g.plane.quaternion);
    const pitchDeg = Math.asin(THREE.MathUtils.clamp(fwd.y, -1, 1)) * THREE.MathUtils.RAD2DEG;
    const bankDeg = Math.atan2(-right.y, up.y) * THREE.MathUtils.RAD2DEG;
    let hdg = Math.atan2(-fwd.x, fwd.z) * THREE.MathUtils.RAD2DEG;
    if (hdg < 0) hdg += 360;

    ctx.textBaseline = 'middle';
    const bore = this._proj(this._s2.copy(fwd).multiplyScalar(D).add(pos), W, H);

    this._ladder(W, H, pos, fwd, pitchDeg);
    if (bore) {
      this._reticle(bore, dt);
      this._bankScale(bore, bankDeg);
    }
    this._fpm(W, H, pos);
    this._headingTape(W, hdg);
    this._airdata(W, H, pitchDeg, bankDeg);
    ctx.textBaseline = 'alphabetic';
  }

  // ---- pitch ladder: 5° rungs, world-true (parallel to the real horizon) ----
  _ladder(W, H, pos, fwd, pitchDeg) {
    const fh = this._s3.set(fwd.x, 0, fwd.z);
    if (fh.lengthSq() < 1e-6) return; // pointing straight up/down
    fh.normalize();
    const east = this._s4.crossVectors(fh, this._up).normalize();
    const upW = this._up;
    const c = this._lc ||= new THREE.Vector3();
    const e = this._le ||= new THREE.Vector3();
    for (let L = -90; L <= 90; L += 5) {
      if (Math.abs(L - pitchDeg) > 24) continue;
      const major = L % 10 === 0;
      const rad = (L * Math.PI) / 180;
      c.copy(fh).multiplyScalar(Math.cos(rad)).addScaledVector(upW, Math.sin(rad))
        .multiplyScalar(D).add(pos);
      const half = L === 0 ? 118 : major ? 46 : 24; // half-length, m at D
      const gap = L === 0 ? 44 : 22; // the middle stays clear for the reticle
      for (const side of [-1, 1]) {
        const a = this._proj(e.copy(c).addScaledVector(east, side * gap), W, H);
        const b = this._proj(e.copy(c).addScaledVector(east, side * half), W, H);
        if (!a || !b) continue;
        // below-horizon rungs are dashed, like the real ladder
        this._line(a[0], a[1], b[0], b[1], CYAN_DIM, L === 0 ? 1.1 : 0.8, L < 0 ? [6, 5] : null);
        if (major && L !== 0) {
          this._text(`${L}`, b[0] + (side < 0 ? -14 : 14), b[1], CYAN_DIM);
        }
      }
    }
  }

  // ---- gun/rocket reticle with mil ring and live range ----
  _reticle([x, y], dt) {
    const g = this.game;
    const rocket = (g.skillbar?.current ?? 'gun') === 'rocket';
    const col = rocket ? MAG : CYAN;
    const R = 26;
    this._circle(x, y, R, col);
    for (let i = 0; i < 8; i++) {
      const a = (i * Math.PI) / 4;
      const inner = R - (i % 2 ? 4 : 7);
      this._line(
        x + Math.cos(a) * R, y + Math.sin(a) * R,
        x + Math.cos(a) * inner, y + Math.sin(a) * inner,
        col, 1.0
      );
    }
    this._circle(x, y, 1.8, col, 1, true); // center pipper

    // boresight range — what the guns would actually hit (slow 0.15 s sample)
    this._rangeT -= dt;
    if (this._rangeT <= 0) {
      this._rangeT = 0.15;
      const fwd = this._v.set(0, 0, -1).applyQuaternion(g.plane.quaternion);
      g.raycaster.set(g.plane.position, fwd);
      g.raycaster.far = 3000;
      // Spatially prefiltered. Handing this ray EVERY collidable in the
      // country cost 13.5 ms in one go -- a 0.15 s hitch you could feel as a
      // stutter while flying; the same cast against the hash cells is 0.2 ms.
      // rings 2 = 5x5 cells of 1500 m, which covers the full 3000 m ray.
      const near = g.world.collidablesNear
        ? g.world.collidablesNear(g.plane.position.x, g.plane.position.z, 2)
        : g.world.collidables;
      const hits = g.raycaster.intersectObjects(near, true);
      g.raycaster.far = Infinity;
      this.range = hits.length ? hits[0].distance : null;
    }
    this._text(this.range ? `RNG ${Math.round(this.range)}` : 'RNG ---', x, y + R + 12, col);
  }

  // ---- bank scale: arc under the boresight, ticks at 0/±10/±20/±30/±45 ----
  _bankScale([x, y], bankDeg) {
    const R = 64;
    for (const t of [-45, -30, -20, -10, 0, 10, 20, 30, 45]) {
      const a = Math.PI / 2 + (t * Math.PI) / 180;
      const len = t === 0 ? 9 : t % 30 === 0 ? 7 : 4;
      this._line(
        x + Math.cos(a) * R, y + Math.sin(a) * R,
        x + Math.cos(a) * (R + len), y + Math.sin(a) * (R + len),
        CYAN_DIM, 0.8
      );
    }
    const a = Math.PI / 2 + (THREE.MathUtils.clamp(bankDeg, -50, 50) * Math.PI) / 180;
    this._poly(
      [
        [x + Math.cos(a) * (R - 2), y + Math.sin(a) * (R - 2)],
        [x + Math.cos(a - 0.06) * (R - 10), y + Math.sin(a - 0.06) * (R - 10)],
        [x + Math.cos(a + 0.06) * (R - 10), y + Math.sin(a + 0.06) * (R - 10)],
      ],
      CYAN
    );
  }

  // ---- flight-path marker: where the plane is actually going ----
  _fpm(W, H, pos) {
    const g = this.game;
    if (!g.airborne || g.vel.lengthSq() < 100) return;
    const p = this._proj((this._fv ||= new THREE.Vector3()).copy(g.vel).normalize().multiplyScalar(D).add(pos), W, H);
    if (!p) return;
    const [x, y] = p;
    this._circle(x, y, 6, CYAN);
    this._line(x - 15, y, x - 6, y, CYAN, 1.0);
    this._line(x + 6, y, x + 15, y, CYAN, 1.0);
    this._line(x, y - 6, x, y - 12, CYAN, 1.0);
  }

  // ---- heading tape: ±25° window, ticks every 5°, boxed readout ----
  _headingTape(W, hdg) {
    const cx = W / 2;
    const y = 30;
    const pxPerDeg = 5.4;
    const first = Math.ceil((hdg - 25) / 5) * 5;
    for (let a = first; a <= hdg + 25; a += 5) {
      const x = cx + (a - hdg) * pxPerDeg;
      const norm = ((a % 360) + 360) % 360;
      const major = norm % 10 === 0;
      this._line(x, y, x, y + (major ? 8 : 4), CYAN_DIM, 0.8);
      if (norm % 30 === 0) this._text(String(norm).padStart(3, '0'), x, y - 8, CYAN_DIM);
    }
    this._poly([[cx, y + 12], [cx - 5, y + 19], [cx + 5, y + 19]], CYAN);
    this._rect(cx - 24, y + 22, 48, 17, CYAN);
    this._text(`${String(Math.round(hdg) % 360).padStart(3, '0')}°`, cx, y + 31, CYAN, `bold 12px ${MONO}`);
  }

  // ---- airdata columns + warnings ----
  _airdata(W, H, pitchDeg, bankDeg) {
    const g = this.game;
    const a = g.aero;
    const cx = W / 2;
    const cy = H / 2;
    const lx = cx - 285;
    const rx = cx + 285;
    const V = g.airborne ? g.vel.length() : Math.abs(g.speed);

    // left: speed box (km/h) + AoA / G / Mach column
    this._rect(lx - 46, cy - 12, 76, 24, CYAN);
    this._text(`${Math.round(V * 3.6)}`, lx + 22, cy + 1, CYAN, `bold 15px ${MONO}`, 'right');
    this._text('KMH', lx + 22, cy + 20, CYAN, `9px ${MONO}`, 'right');
    const aoa = g.airborne ? a.alpha * THREE.MathUtils.RAD2DEG : 0;
    this._text(`α ${aoa.toFixed(1)}°`, lx + 22, cy + 44, CYAN_DIM, `11px ${MONO}`, 'right');
    this._text(`G ${(g.airborne ? a.n : 1).toFixed(1)}`, lx + 22, cy + 60, CYAN_DIM, `11px ${MONO}`, 'right');
    this._text(`M ${(V / 340).toFixed(2)}`, lx + 22, cy + 76, CYAN_DIM, `11px ${MONO}`, 'right');

    // right: altitude box (m MSL) + radar alt + vertical speed + attitude
    this._rect(rx - 30, cy - 12, 76, 24, CYAN);
    this._text(`${Math.round(g.plane.position.y)}`, rx - 22, cy + 1, CYAN, `bold 15px ${MONO}`, 'left');
    this._text('ALT M', rx - 22, cy + 20, CYAN, `9px ${MONO}`, 'left');
    this._text(`R ${Math.round(g.lastGroundDistance)}`, rx - 22, cy + 44, CYAN_DIM, `11px ${MONO}`, 'left');
    const vs = g.airborne ? g.vel.y : 0;
    this._text(`${vs >= 0 ? '↑' : '↓'} ${Math.abs(vs).toFixed(0)} m/s`, rx - 22, cy + 60, CYAN_DIM, `11px ${MONO}`, 'left');
    this._text(`θ ${pitchDeg.toFixed(0)}° φ ${bankDeg.toFixed(0)}°`, rx - 22, cy + 76, CYAN_DIM, `11px ${MONO}`, 'left');

    // warnings, flashing like they mean it
    if (Math.sin(this._t * 10) > 0 && g.airborne) {
      if (a.stalled) this._text('STALL', cx, cy - 92, MAG, `bold 16px ${MONO}`);
      else if (a.overspeed > 0) this._text('VNE', cx, cy - 92, MAG, `bold 16px ${MONO}`);
    }
  }

  dispose() {
    this.canvas.remove();
  }
}
