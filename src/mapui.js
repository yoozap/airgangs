// GTA-style rotating circular minimap + WoW-style map window on M.
// The window map shows Tbilisi only (the sharp city mosaic); it can be
// dragged by its title bar, resized from the corner, or maximized.

import { STREET_STATUE_POINTS, BUST_POINTS } from './landmarks.js';

const TILE = 256;
const MIN_W = 360;
const MIN_H = 280;

export class MapUI {
  constructor() {
    this.mini = document.getElementById('minimap');
    this.mctx = this.mini.getContext('2d');
    this.win = document.getElementById('mapwin');
    this.body = document.getElementById('mapwin-body');
    this.bigCanvas = document.getElementById('bigmap-canvas');
    this.bctx = this.bigCanvas.getContext('2d');

    this.meta = null;
    this.imgCity = null;
    this.imgGeo = null; // minimap fallback outside the city mosaic
    this.bigVisible = false;
    this.isFull = false;
    this.rect = null; // windowed {x,y,w,h}
    this.zoom = 0; // exponent over fit-to-window scale, 0..4
    this.cx = null; // view center, city-image px
    this.cy = null;
    this.pos = { x: 0, z: 0, heading: 0 };
    this.zones = null; // set by Game — highlights the district you're in
    this.districts = null; // [{name, tag, rings:[[lat,lon]...]}]
    this.subzones = null; // neighborhood polygons, same shape
    this._distPx = null; // rings/centroids precomputed in city-image px
    this._subPx = null;

    // player waypoint pins, [{lat, lon}], persisted across sessions
    this.pins = [];
    this.pinsDirty = true; // Game syncs the in-world beams off this flag
    try {
      const pins = JSON.parse(localStorage.getItem('tf-pins') || '[]');
      if (Array.isArray(pins)) this.pins = pins.filter((p) => p && isFinite(p.lat) && isFinite(p.lon));
    } catch {}

    try {
      const saved = JSON.parse(localStorage.getItem('tf-mapwin') || 'null');
      if (saved && saved.w >= MIN_W && saved.h >= MIN_H) this.rect = saved;
    } catch {}

    fetch('tbilisi/mapmeta.json')
      .then((r) => (r.ok ? r.json() : null))
      .then((m) => {
        if (!m) return;
        this.meta = m;
        this.imgCity = new Image();
        this.imgCity.src = 'tbilisi/satellite.jpg';
        this.imgGeo = new Image();
        this.imgGeo.src = 'tbilisi/georgia-sat.jpg';
      })
      .catch(() => {});

    fetch('tbilisi/districts.json')
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (d?.length) this.districts = d;
      })
      .catch(() => {});
    fetch('tbilisi/subzones.json')
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (d?.length) this.subzones = d;
      })
      .catch(() => {});

    this._bindWindowControls();
    this._bindMapControls();
  }

  // ---- coordinate helpers ---------------------------------------------------

  _latlon(x, z) {
    const m = this.meta;
    return [m.origin.lat + z / m.mlat, m.origin.lon - x / m.mlon];
  }

  // pixel position of a world point inside a mosaic image
  _px(sat, x, z) {
    const [lat, lon] = this._latlon(x, z);
    return this._pxLL(sat, lat, lon);
  }

  _pxLL(sat, lat, lon) {
    const nt = 2 ** sat.z;
    const xt = ((lon + 180) / 360) * nt;
    const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
    return [(xt - sat.x0) * TILE, (yt - sat.y0) * TILE];
  }

  // inverse: mosaic-image px back to lat/lon (pin placement)
  _llFromPx(sat, ix, iy) {
    const nt = 2 ** sat.z;
    const lon = ((sat.x0 + ix / TILE) / nt) * 360 - 180;
    const lat = (Math.atan(Math.sinh(Math.PI * (1 - (2 * (sat.y0 + iy / TILE)) / nt))) * 180) / Math.PI;
    return [lat, lon];
  }

  // pins in world coords for the in-scene beacons
  pinsWorld() {
    if (!this.meta) return [];
    const m = this.meta;
    return this.pins.map((p) => ({
      x: -(p.lon - m.origin.lon) * m.mlon,
      z: (p.lat - m.origin.lat) * m.mlat,
    }));
  }

  _savePins() {
    this.pinsDirty = true;
    try {
      localStorage.setItem('tf-pins', JSON.stringify(this.pins));
    } catch {}
  }

  _metersPerPx(sat) {
    const lat = this.meta.origin.lat;
    return (156543.03 * Math.cos((lat * Math.PI) / 180)) / 2 ** sat.z;
  }

  _inCityImage(x, z) {
    const s = this.meta.satellite;
    const [px, py] = this._px(s, x, z);
    const margin = 40;
    return px > margin && py > margin && px < s.cols * TILE - margin && py < s.rows * TILE - margin;
  }

  // ---- minimap --------------------------------------------------------------

  update(x, z, heading) {
    this.pos = { x, z, heading };
    if (!this.meta || !this.imgGeo?.complete) return;
    const ctx = this.mctx;
    const W = this.mini.width;
    const R = W / 2;
    ctx.clearRect(0, 0, W, W);

    const useCity = this.imgCity?.complete && this._inCityImage(x, z);
    const sat = useCity ? this.meta.satellite : this.meta.country.satellite;
    const img = useCity ? this.imgCity : this.imgGeo;
    const mpp = this._metersPerPx(sat);
    const radiusM = 1500; // world radius shown in the circle
    const srcR = radiusM / mpp;
    const [px, py] = this._px(sat, x, z);

    ctx.save();
    ctx.beginPath();
    ctx.arc(R, R, R - 3, 0, Math.PI * 2);
    ctx.clip();
    ctx.translate(R, R);
    ctx.rotate(-heading); // GTA: the map turns, the player arrow points up
    const scale = (R - 3) / srcR;
    ctx.scale(scale, scale);
    ctx.drawImage(img, px - srcR, py - srcR, srcR * 2, srcR * 2, -srcR, -srcR, srcR * 2, srcR * 2);
    ctx.restore();

    // player arrow (fixed, pointing up)
    ctx.save();
    ctx.translate(R, R);
    ctx.fillStyle = '#7ceaff';
    ctx.strokeStyle = '#03202c';
    ctx.lineWidth = 2;
    ctx.shadowColor = '#3fd6ff';
    ctx.shadowBlur = 8;
    ctx.beginPath();
    ctx.moveTo(0, -10);
    ctx.lineTo(7, 8);
    ctx.lineTo(0, 4);
    ctx.lineTo(-7, 8);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.restore();

    // waypoint pins — gold dots that stick to the rim when out of range
    if (this.pins.length) {
      const cos = Math.cos(-heading);
      const sin = Math.sin(-heading);
      for (const p of this.pins) {
        const [qx, qy] = this._pxLL(sat, p.lat, p.lon);
        const dx = (qx - px) * scale;
        const dy = (qy - py) * scale;
        let sx = dx * cos - dy * sin;
        let sy = dx * sin + dy * cos;
        const d = Math.hypot(sx, sy);
        const lim = R - 13;
        const clamped = d > lim;
        if (clamped) {
          sx *= lim / d;
          sy *= lim / d;
        }
        ctx.fillStyle = '#ffd257';
        ctx.strokeStyle = '#4a3a10';
        ctx.lineWidth = 1.5;
        ctx.shadowColor = '#ffd257';
        if (clamped) {
          // out of range: a small glowing dot stuck to the rim
          ctx.beginPath();
          ctx.arc(R + sx, R + sy, 3.5, 0, Math.PI * 2);
          ctx.shadowBlur = 5;
          ctx.fill();
          ctx.shadowBlur = 0;
          ctx.stroke();
        } else {
          // in range: the same teardrop pin as on the big map
          const mx = R + sx;
          const my = R + sy;
          ctx.beginPath();
          ctx.moveTo(mx, my);
          ctx.arc(mx, my - 8, 5.5, Math.PI / 2 + 0.55, Math.PI / 2 - 0.55, false);
          ctx.closePath();
          ctx.shadowBlur = 9;
          ctx.fill();
          ctx.shadowBlur = 0;
          ctx.stroke();
          ctx.beginPath();
          ctx.arc(mx, my - 8, 2, 0, Math.PI * 2);
          ctx.fillStyle = '#3a2c08';
          ctx.fill();
        }
      }
    }

    // north marker on the rim
    const nx = R - (R - 12) * Math.sin(heading);
    const ny = R - (R - 12) * Math.cos(heading);
    ctx.fillStyle = '#ff3e8a';
    ctx.font = 'bold 13px "JetBrains Mono", monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('N', nx, ny);

    if (this.bigVisible) this._drawBig();
  }

  // ---- map window (WoW-style) ----------------------------------------------

  toggle() {
    this.bigVisible = !this.bigVisible;
    this.win.classList.toggle('hidden', !this.bigVisible);
    if (this.bigVisible) {
      if (!this.rect) this.rect = this._defaultRect();
      this._clampRect();
      this._applyRect();
      this._resizeCanvas();
      this.zoom = 0;
      this._centerOnPlayer();
      this._drawBig();
    }
  }

  _defaultRect() {
    const w = Math.max(MIN_W, Math.round(window.innerWidth * 0.62));
    const h = Math.max(MIN_H, Math.round(window.innerHeight * 0.7));
    return { x: Math.round((window.innerWidth - w) / 2), y: Math.round((window.innerHeight - h) / 2), w, h };
  }

  _clampRect() {
    const r = this.rect;
    r.w = Math.min(Math.max(r.w, MIN_W), window.innerWidth);
    r.h = Math.min(Math.max(r.h, MIN_H), window.innerHeight);
    r.x = Math.min(Math.max(r.x, 0), window.innerWidth - Math.min(r.w, 120));
    r.y = Math.min(Math.max(r.y, 0), window.innerHeight - 60);
  }

  _applyRect() {
    this.win.classList.toggle('full', this.isFull);
    if (!this.isFull) {
      const r = this.rect;
      this.win.style.left = `${r.x}px`;
      this.win.style.top = `${r.y}px`;
      this.win.style.width = `${r.w}px`;
      this.win.style.height = `${r.h}px`;
    }
    try {
      localStorage.setItem('tf-mapwin', JSON.stringify(this.rect));
    } catch {}
  }

  _resizeCanvas() {
    this.bigCanvas.width = this.body.clientWidth;
    this.bigCanvas.height = this.body.clientHeight;
  }

  _bindWindowControls() {
    const title = document.getElementById('mapwin-title');
    const handle = document.getElementById('mapwin-resize');
    document.getElementById('mapwin-close').addEventListener('click', () => {
      if (this.bigVisible) this.toggle();
    });
    document.getElementById('mapwin-full').addEventListener('click', () => {
      this.isFull = !this.isFull;
      this._applyRect();
      this._resizeCanvas();
      this._drawBig();
    });

    // drag the window by its title bar
    let drag = null;
    title.addEventListener('mousedown', (e) => {
      if (this.isFull || e.target.closest('button')) return;
      drag = { mx: e.clientX, my: e.clientY, x: this.rect.x, y: this.rect.y };
      e.preventDefault();
    });
    // resize from the corner handle
    let resz = null;
    handle.addEventListener('mousedown', (e) => {
      resz = { mx: e.clientX, my: e.clientY, w: this.rect.w, h: this.rect.h };
      e.preventDefault();
    });
    window.addEventListener('mousemove', (e) => {
      if (drag) {
        this.rect.x = drag.x + e.clientX - drag.mx;
        this.rect.y = drag.y + e.clientY - drag.my;
        this._clampRect();
        this._applyRect();
      } else if (resz) {
        this.rect.w = resz.w + e.clientX - resz.mx;
        this.rect.h = resz.h + e.clientY - resz.my;
        this._clampRect();
        this._applyRect();
        this._resizeCanvas();
        this._drawBig();
      }
    });
    window.addEventListener('mouseup', () => {
      drag = null;
      resz = null;
    });

    window.addEventListener('resize', () => {
      if (!this.bigVisible) return;
      this._clampRect();
      this._applyRect();
      this._resizeCanvas();
      this._drawBig();
    });
  }

  // ---- Tbilisi map content --------------------------------------------------

  _citySat() {
    return this.meta?.satellite ?? null;
  }

  // zone rings converted to city-image px once, labels at ring centroids
  _toPx(list) {
    const c = this._citySat();
    return list.map((d) => {
      const rings = d.rings.map((ring) => ring.map(([lat, lon]) => this._pxLL(c, lat, lon)));
      let best = rings[0];
      for (const r of rings) if (r.length > best.length) best = r;
      let lx = 0;
      let ly = 0;
      for (const [x, y] of best) {
        lx += x;
        ly += y;
      }
      return { name: d.name, rings, lx: lx / best.length, ly: ly / best.length };
    });
  }

  _distCache() {
    if (!this._distPx && this.districts && this.meta) this._distPx = this._toPx(this.districts);
    return this._distPx;
  }

  _subCache() {
    if (!this._subPx && this.subzones && this.meta) this._subPx = this._toPx(this.subzones);
    return this._subPx;
  }

  _fitScale() {
    const c = this._citySat();
    if (!c) return 0;
    return Math.min(this.bigCanvas.width / (c.cols * TILE), this.bigCanvas.height / (c.rows * TILE));
  }

  _scale() {
    return this._fitScale() * 2 ** this.zoom;
  }

  _clampCenter() {
    const c = this._citySat();
    if (!c) return;
    const s = this._scale();
    const gw = c.cols * TILE;
    const gh = c.rows * TILE;
    const vw = this.bigCanvas.width / s;
    const vh = this.bigCanvas.height / s;
    this.cx = gw <= vw ? gw / 2 : Math.min(Math.max(this.cx, vw / 2), gw - vw / 2);
    this.cy = gh <= vh ? gh / 2 : Math.min(Math.max(this.cy, vh / 2), gh - vh / 2);
  }

  _centerOnPlayer() {
    const c = this._citySat();
    if (!c) return;
    const [px, py] = this._px(c, this.pos.x, this.pos.z);
    this.cx = Math.min(Math.max(px, 0), c.cols * TILE);
    this.cy = Math.min(Math.max(py, 0), c.rows * TILE);
    this._clampCenter();
  }

  _bindMapControls() {
    // zoom toward the cursor
    this.bigCanvas.addEventListener('wheel', (e) => {
      e.preventDefault();
      const prev = this._scale();
      if (!prev) return;
      this.zoom = Math.max(0, Math.min(4, this.zoom - Math.sign(e.deltaY) * 0.25));
      const s = this._scale();
      const r = this.bigCanvas.getBoundingClientRect();
      const mx = e.clientX - r.left - this.bigCanvas.width / 2;
      const my = e.clientY - r.top - this.bigCanvas.height / 2;
      this.cx += mx / prev - mx / s;
      this.cy += my / prev - my / s;
      this._clampCenter();
      this._drawBig();
    });
    // pan — or, if the mouse never really moved, a click that drops/removes a pin
    let drag = null;
    this.bigCanvas.addEventListener('mousedown', (e) => {
      if (e.button !== 0) return;
      drag = { x: e.clientX, y: e.clientY, moved: 0 };
      e.preventDefault();
    });
    window.addEventListener('mouseup', (e) => {
      if (drag && this.bigVisible && drag.moved < 5) this._clickPin(e);
      drag = null;
    });
    window.addEventListener('mousemove', (e) => {
      if (!drag || !this.bigVisible) return;
      const s = this._scale();
      if (!s) return;
      drag.moved += Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y);
      this.cx -= (e.clientX - drag.x) / s;
      this.cy -= (e.clientY - drag.y) / s;
      drag = { x: e.clientX, y: e.clientY, moved: drag.moved };
      this._clampCenter();
      this._drawBig();
    });
    // right-click always removes the nearest pin
    this.bigCanvas.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const i = this._pinAt(e, 22);
      if (i >= 0) {
        this.pins.splice(i, 1);
        this._savePins();
        this._drawBig();
      }
    });
  }

  // screen-px position of pin i inside the big map
  _pinScreen(i) {
    const c = this._citySat();
    const s = this._scale();
    const [qx, qy] = this._pxLL(c, this.pins[i].lat, this.pins[i].lon);
    return [this.bigCanvas.width / 2 + (qx - this.cx) * s, this.bigCanvas.height / 2 + (qy - this.cy) * s];
  }

  _pinAt(e, radius) {
    if (!this.meta) return -1;
    const r = this.bigCanvas.getBoundingClientRect();
    const mx = e.clientX - r.left;
    const my = e.clientY - r.top;
    for (let i = 0; i < this.pins.length; i++) {
      const [sx, sy] = this._pinScreen(i);
      if (Math.hypot(mx - sx, my - sy - 9) < radius) return i; // -9: marker head sits above the anchor
    }
    return -1;
  }

  // click: on a pin → remove it, on empty map → drop a new one
  _clickPin(e) {
    if (!this.meta) return;
    const hit = this._pinAt(e, 14);
    if (hit >= 0) {
      this.pins.splice(hit, 1);
    } else {
      const c = this._citySat();
      const s = this._scale();
      const r = this.bigCanvas.getBoundingClientRect();
      const ix = this.cx + (e.clientX - r.left - this.bigCanvas.width / 2) / s;
      const iy = this.cy + (e.clientY - r.top - this.bigCanvas.height / 2) / s;
      if (ix < 0 || iy < 0 || ix > c.cols * TILE || iy > c.rows * TILE) return; // off the mosaic
      if (this.pins.length >= 30) this.pins.shift();
      const [lat, lon] = this._llFromPx(c, ix, iy);
      this.pins.push({ lat, lon });
    }
    this._savePins();
    this._drawBig();
  }

  _drawBig() {
    const ctx = this.bctx;
    const W = this.bigCanvas.width;
    const H = this.bigCanvas.height;
    ctx.fillStyle = '#04070d';
    ctx.fillRect(0, 0, W, H);
    if (!this.meta || !this.imgCity?.complete) return;

    const c = this._citySat();
    const s = this._scale();
    const gw = c.cols * TILE;
    const gh = c.rows * TILE;
    if (this.cx === null) this._centerOnPlayer();
    const ox = W / 2 - this.cx * s;
    const oy = H / 2 - this.cy * s;

    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(this.imgCity, ox, oy, gw * s, gh * s);

    // cold holo tint over the satellite photo
    ctx.fillStyle = 'rgba(10, 24, 46, 0.22)';
    ctx.fillRect(ox, oy, gw * s, gh * s);
    ctx.strokeStyle = 'rgba(63, 214, 255, 0.5)';
    ctx.lineWidth = 1;
    ctx.strokeRect(ox + 0.5, oy + 0.5, gw * s - 1, gh * s - 1);

    // hard map boundary (the holo wall in the world) — dashed cyan frame
    const bb = this.meta.cities?.tbilisi;
    if (bb) {
      const [xa, ya] = this._pxLL(c, bb[2], bb[1]); // NW corner
      const [xb, yb] = this._pxLL(c, bb[0], bb[3]); // SE corner
      ctx.strokeStyle = 'rgba(63, 214, 255, 0.85)';
      ctx.lineWidth = 1.5;
      ctx.setLineDash([9, 6]);
      ctx.shadowColor = '#3fd6ff';
      ctx.shadowBlur = 8;
      ctx.strokeRect(ox + xa * s, oy + ya * s, (xb - xa) * s, (yb - ya) * s);
      ctx.shadowBlur = 0;
      ctx.setLineDash([]);
    }

    // neighborhood subzones — the fine-grained layer, under the raion lines
    const subs = this._subCache();
    const curSub = this.zones?.current?.name;
    if (subs && this.zoom >= 0.5) {
      ctx.lineWidth = 1;
      for (const d of subs) {
        ctx.beginPath();
        for (const ring of d.rings) {
          ring.forEach(([x, y], i) =>
            i ? ctx.lineTo(ox + x * s, oy + y * s) : ctx.moveTo(ox + x * s, oy + y * s)
          );
          ctx.closePath();
        }
        if (d.name === curSub) {
          ctx.fillStyle = 'rgba(63, 214, 255, 0.13)';
          ctx.fill();
        }
        ctx.strokeStyle = d.name === curSub ? 'rgba(124, 234, 255, 0.9)' : 'rgba(63, 214, 255, 0.28)';
        ctx.stroke();
      }
      if (this.zoom >= 1.25) {
        ctx.font = '10px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        for (const d of subs) {
          const mx = ox + d.lx * s;
          const my = oy + d.ly * s;
          if (mx < -60 || my < -20 || mx > W + 60 || my > H + 20) continue;
          ctx.fillStyle = 'rgba(0, 8, 14, 0.75)';
          ctx.fillText(d.name, mx + 1, my + 1);
          ctx.fillStyle = d.name === curSub ? '#aef2ff' : 'rgba(150, 225, 250, 0.68)';
          ctx.fillText(d.name, mx, my);
        }
      }
    }

    // district boundaries — the city carved into its real raioni
    const dist = this._distCache();
    if (dist) {
      const cur = this.zones?.currentDistrict?.name;
      ctx.lineWidth = 1.2;
      ctx.setLineDash([5, 4]);
      for (const d of dist) {
        ctx.beginPath();
        for (const ring of d.rings) {
          ring.forEach(([x, y], i) =>
            i ? ctx.lineTo(ox + x * s, oy + y * s) : ctx.moveTo(ox + x * s, oy + y * s)
          );
          ctx.closePath();
        }
        if (d.name === cur && !(subs && this.zoom >= 0.5)) {
          ctx.fillStyle = 'rgba(63, 214, 255, 0.08)';
          ctx.fill();
        }
        ctx.strokeStyle = d.name === cur ? 'rgba(124, 234, 255, 0.95)' : 'rgba(63, 214, 255, 0.5)';
        ctx.stroke();
      }
      ctx.setLineDash([]);
      // raion names fade back once the neighborhood labels take over
      const zoomedIn = this.zoom >= 1.25;
      ctx.font = `bold ${zoomedIn ? 16 : this.zoom >= 1 ? 14 : 11}px "JetBrains Mono", monospace`;
      ctx.textAlign = 'center';
      for (const d of dist) {
        const mx = ox + d.lx * s;
        const my = oy + d.ly * s;
        ctx.fillStyle = zoomedIn ? 'rgba(0, 8, 14, 0.4)' : 'rgba(0, 8, 14, 0.8)';
        ctx.fillText(d.name, mx + 1, my + 1);
        ctx.fillStyle = d.name === cur ? `rgba(124, 234, 255, ${zoomedIn ? 0.4 : 1})` : `rgba(63, 214, 255, ${zoomedIn ? 0.3 : 0.75})`;
        ctx.fillText(d.name, mx, my);
      }
    }

    // every street statue and bust from the OSM inventory — small gold dots
    if (this.zoom >= 0.75) {
      ctx.fillStyle = '#ffd257';
      ctx.strokeStyle = 'rgba(74, 58, 16, 0.9)';
      ctx.lineWidth = 1;
      for (const [lat, lon] of STREET_STATUE_POINTS) {
        const x = -(lon - this.meta.origin.lon) * this.meta.mlon;
        const z = (lat - this.meta.origin.lat) * this.meta.mlat;
        const [sx, sy] = this._px(c, x, z);
        ctx.beginPath();
        ctx.arc(ox + sx * s, oy + sy * s, 2.5, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
      }
    }
    if (this.zoom >= 1.5) {
      ctx.fillStyle = 'rgba(255, 210, 87, 0.75)';
      for (const [lat, lon] of BUST_POINTS) {
        const x = -(lon - this.meta.origin.lon) * this.meta.mlon;
        const z = (lat - this.meta.origin.lat) * this.meta.mlat;
        const [sx, sy] = this._px(c, x, z);
        ctx.fillRect(ox + sx * s - 1.5, oy + sy * s - 1.5, 3, 3);
      }
    }

    // gold markers for the ten hand-modeled heroes (bigger, labeled)
    for (const [lat, lon, label] of STATUE_MARKERS) {
      const x = -(lon - this.meta.origin.lon) * this.meta.mlon;
      const z = (lat - this.meta.origin.lat) * this.meta.mlat;
      const [sx, sy] = this._px(c, x, z);
      const mx = ox + sx * s;
      const my = oy + sy * s;
      ctx.fillStyle = '#ffd257';
      ctx.strokeStyle = '#4a3a10';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(mx, my, 4, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      if (this.zoom >= 1) {
        ctx.font = 'bold 11px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        ctx.fillStyle = 'rgba(0,0,0,0.7)';
        ctx.fillText(label, mx + 1, my - 8 + 1);
        ctx.fillStyle = '#ffd257';
        ctx.fillText(label, mx, my - 8);
      }
    }

    // cyan M squares for the metro stations
    if (this.zoom >= 0.5) {
      for (const [lat, lon] of METRO_MARKERS) {
        const x = -(lon - this.meta.origin.lon) * this.meta.mlon;
        const z = (lat - this.meta.origin.lat) * this.meta.mlat;
        const [sx, sy] = this._px(c, x, z);
        const mx = ox + sx * s;
        const my = oy + sy * s;
        ctx.fillStyle = '#0e6f7e';
        ctx.fillRect(mx - 5, my - 5, 10, 10);
        ctx.strokeStyle = '#3fd6ff';
        ctx.lineWidth = 1;
        ctx.strokeRect(mx - 5, my - 5, 10, 10);
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 9px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText('M', mx, my + 0.5);
        ctx.textBaseline = 'alphabetic';
      }
    }

    // waypoint pins — gold teardrop markers, click to place/remove
    for (const p of this.pins) {
      const [qx, qy] = this._pxLL(c, p.lat, p.lon);
      const mx = ox + qx * s;
      const my = oy + qy * s;
      ctx.beginPath();
      ctx.moveTo(mx, my);
      ctx.arc(mx, my - 9, 6, Math.PI / 2 + 0.55, Math.PI / 2 - 0.55, false);
      ctx.closePath();
      ctx.fillStyle = '#ffd257';
      ctx.strokeStyle = '#4a3a10';
      ctx.lineWidth = 1.5;
      ctx.shadowColor = '#ffd257';
      ctx.shadowBlur = 10;
      ctx.fill();
      ctx.shadowBlur = 0;
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(mx, my - 9, 2.2, 0, Math.PI * 2);
      ctx.fillStyle = '#3a2c08';
      ctx.fill();
    }

    // player blip (clamped to the city edge and dimmed when off-grid)
    let [px, py] = this._px(c, this.pos.x, this.pos.z);
    const off = px < 0 || py < 0 || px > gw || py > gh;
    px = Math.min(Math.max(px, 0), gw);
    py = Math.min(Math.max(py, 0), gh);
    ctx.save();
    ctx.translate(ox + px * s, oy + py * s);
    ctx.rotate(this.pos.heading);
    ctx.globalAlpha = off ? 0.45 : 1;
    ctx.fillStyle = '#7ceaff';
    ctx.strokeStyle = '#03202c';
    ctx.lineWidth = 2;
    ctx.shadowColor = '#3fd6ff';
    ctx.shadowBlur = 12;
    ctx.beginPath();
    ctx.moveTo(0, -11);
    ctx.lineTo(8, 9);
    ctx.lineTo(0, 4);
    ctx.lineTo(-8, 9);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.restore();
    if (off) {
      ctx.font = 'bold 10px "JetBrains Mono", monospace';
      ctx.textAlign = 'center';
      ctx.fillStyle = '#ff3e8a';
      ctx.fillText('OFF-GRID', ox + px * s, oy + py * s - 16);
    }
  }
}

// the ten hand-modeled monuments (same coords as landmarks.js placements)
const STATUE_MARKERS = [
  [41.69339, 44.8015, 'წმ. გიორგი'],
  [41.68812, 44.8046, 'ქართლის დედა'],
  [41.69032, 44.81073, 'გორგასალი'],
  [41.70389, 44.78993, 'რუსთაველი'],
  [41.76157, 44.7686, 'დავით აღმაშენებელი'],
  [41.76249, 44.76732, 'წმ. ნინო'],
  [41.7245, 44.7765, 'სააკაძე'],
  [41.72384, 44.72901, 'ვაჟა-ფშაველა'],
  [41.69775, 44.79818, 'წერეთელი და ჭავჭავაძე'],
  [41.68883, 44.82045, 'ფარნავაზი'],
];

const METRO_MARKERS = [
  [41.68770, 44.82705],
  [41.68647, 44.84088],
  [41.70418, 44.78971],
  [41.69496, 44.80014],
  [41.73262, 44.79700],
  [41.70983, 44.79754],
  [41.72245, 44.71778],
  [41.79157, 44.81565],
  [41.78357, 44.79949],
  [41.77510, 44.79539],
  [41.74264, 44.78461],
  [41.68544, 44.85533],
  [41.71989, 44.77835],
  [41.72709, 44.76319],
  [41.72552, 44.74457],
  [41.72424, 44.73093],
  [41.72707, 44.78800],
  [41.74978, 44.77946],
  [41.76551, 44.79023],
  [41.69250, 44.81513],
  [41.72198, 44.79790],
  [41.72252, 44.79608],
  [41.69129, 44.87094],
];
