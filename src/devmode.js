import * as THREE from 'three';
import { TransformControls } from 'three/addons/controls/TransformControls.js';

// DEV MODE: move things with a gizmo, in the running game.
//
// Press ` (backquote) and the world becomes an editor: click any registered
// prop and a three.js TransformControls gizmo lands on it -- drag the arrows
// to move it, switch to the rings to turn it, the cubes to scale it. The
// panel reads out where it is in the frame the CODE uses (its parent's local
// coordinates -- shed-local for anything in the warehouse) and in the world,
// with lat/lon, and copies a ready-to-paste snippet. Every drag is saved to
// localStorage and put back on reload, so a prop stays where you left it
// while you walk around and look at it; Reset drops the saved spot and
// returns the prop to where the code put it.
//
// Props opt in with game.dev.register(obj, id, { onMove }) -- onMove is where
// a prop re-indexes its colliders, because the collision hash keys a mesh
// by where it WAS (see citymap._addCollidable). Props whose colliders are
// baked into a world-space soup (the tables, the ranges) must not register:
// the picture would move and the wall would stay.
//
// Walking is untouched: WASD still drives the hero, drag on empty floor still
// orbits the camera. Only a drag that STARTS on the gizmo belongs to it.
const KEY = 'tf-dev-overrides';
const MODES = [['translate', '✥ გადატანა'], ['rotate', '↻ ბრუნვა'], ['scale', '⤢ ზომა']];
const SNAP = { t: 0.5, r: THREE.MathUtils.degToRad(15), s: 0.25 };
const CLICK_PX = 4; // a press that travels less than this is a click, not a drag

export class DevMode {
  constructor(game) {
    this.game = game;
    this.active = false;
    this.items = [];     // { obj, id, onMove, home: {p,r,s}, saved }
    this.selected = null;
    this.ctl = null;
    this.helper = null;
    this.dragging = false;
    this.snap = false;
    this._ray = new THREE.Raycaster();
    this._ndc = new THREE.Vector2();
    this._v = new THREE.Vector3();
    this._q = new THREE.Quaternion();
    this._e = new THREE.Euler(0, 0, 0, 'YXZ');
    this._down = null;
    try { this.saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { this.saved = {}; }
    this._onDown = (e) => {
      if (e.button !== 0) return;
      this._down = { x: e.clientX, y: e.clientY, hot: this.hot };
    };
    this._onUp = (e) => {
      const d = this._down;
      this._down = null;
      if (!d || d.hot || e.button !== 0) return;
      if (Math.hypot(e.clientX - d.x, e.clientY - d.y) > CLICK_PX) return; // that was an orbit
      this._pick(e);
    };
    this._buildPanel();
  }

  // the pointer is on the gizmo, or dragging it: the game must leave it alone
  get hot() {
    return !!(this.ctl && (this.ctl.dragging || this.ctl.axis));
  }

  // ---- registry -------------------------------------------------------------
  register(obj, id, opts = {}) {
    if (this.items.some((it) => it.obj === obj)) return obj;
    const it = { obj, id, onMove: opts.onMove ?? null, home: snapshot(obj), saved: false };
    this.items.push(it);
    obj.userData.devId = id;
    const ov = this.saved[id];
    if (ov) {
      apply(obj, ov);
      obj.updateMatrixWorld(true);
      it.onMove?.(obj);
      it.saved = true;
      console.log(`dev: ${id} placed from its saved override`, JSON.stringify(ov));
    }
    if (this.active) this._render();
    return obj;
  }

  // ---- on / off ---------------------------------------------------------------
  toggle() { if (this.active) this.close(); else this.open(); }

  open() {
    if (this.active) return;
    this._ensure();
    this.active = true;
    this.game._unlockPointer?.();
    const canvas = this.game.renderer.domElement;
    canvas.addEventListener('pointerdown', this._onDown);
    canvas.addEventListener('pointerup', this._onUp);
    this.panel.style.display = 'block';
    this.game._setHint?.('🛠 DEV — დააწკაპუნე ობიექტზე და გადაათრიე · ` გამოსვლა');
    this._render();
  }

  close() {
    if (!this.active) return;
    this.deselect();
    this.active = false;
    const canvas = this.game.renderer.domElement;
    canvas.removeEventListener('pointerdown', this._onDown);
    canvas.removeEventListener('pointerup', this._onUp);
    this.panel.style.display = 'none';
  }

  _ensure() {
    if (this.ctl) return;
    const g = this.game;
    this.ctl = new TransformControls(g.camera, g.renderer.domElement);
    this.ctl.setSize(0.9);
    this.helper = this.ctl.getHelper();
    this.helper.name = 'dev_gizmo';
    this.helper.traverse((o) => { o.userData.noCollide = true; });
    g.scene.add(this.helper);
    this.ctl.addEventListener('dragging-changed', (e) => {
      this.dragging = e.value;
      if (!e.value) this._commit();   // a drag ended: this is the moment to save
    });
    this.ctl.addEventListener('objectChange', () => this._renderValues());
  }

  // ---- selection ----------------------------------------------------------------
  select(target) {
    const it = typeof target === 'string'
      ? this.items.find((i) => i.id === target)
      : this._itemOf(target);
    if (!it) return false;
    this._ensure();
    this.selected = it;
    this.ctl.attach(it.obj);
    this._render();
    return true;
  }

  deselect() {
    this.ctl?.detach();
    this.selected = null;
    if (this.active) this._render();
  }

  cycle(dir) {
    if (!this.items.length) return;
    const i = this.selected ? this.items.indexOf(this.selected) : -1;
    this.select(this.items[(i + dir + this.items.length) % this.items.length].obj);
  }

  _itemOf(obj) {
    for (let o = obj; o; o = o.parent) {
      const it = this.items.find((i) => i.obj === o);
      if (it) return it;
    }
    return null;
  }

  _pick(e) {
    const g = this.game;
    const r = g.renderer.domElement.getBoundingClientRect();
    this._ndc.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
    this._ray.setFromCamera(this._ndc, g.camera);
    const hits = this._ray.intersectObjects(this.items.map((i) => i.obj), true);
    if (hits.length) this.select(hits[0].object);
    else this.deselect();
  }

  // ---- keys (called from the game's keydown while active) ------------------------
  handleKey(e) {
    if (!this.active) return false;
    switch (e.code) {
      case 'Escape':
        if (this.selected) this.deselect(); else this.close();
        return true;
      case 'BracketRight': this.cycle(1); return true;
      case 'BracketLeft': this.cycle(-1); return true;
      default: return false;
    }
  }

  // ---- gizmo options ---------------------------------------------------------------
  setMode(m) { this._ensure(); this.ctl.setMode(m); this._render(); }

  setSnap(on) {
    this._ensure();
    this.snap = on;
    this.ctl.setTranslationSnap(on ? SNAP.t : null);
    this.ctl.setRotationSnap(on ? SNAP.r : null);
    this.ctl.setScaleSnap(on ? SNAP.s : null);
    this._render();
  }

  toggleSpace() {
    this._ensure();
    this.ctl.setSpace(this.ctl.space === 'world' ? 'local' : 'world');
    this._render();
  }

  // ---- the numbers ------------------------------------------------------------------
  // Every drag end lands here: re-index the prop's colliders, remember the
  // spot, and say where it went in the console (that line is the deliverable
  // when the spot is to go into the code).
  _commit() {
    const it = this.selected;
    if (!it) return;
    it.obj.updateMatrixWorld(true);
    it.onMove?.(it.obj);
    this.saved[it.id] = snapshot(it.obj);
    it.saved = true;
    this._store();
    console.log(`dev: ${it.id} →\n${this.snippet(it)}`);
    this._renderValues();
  }

  reset(it = this.selected) {
    if (!it) return;
    apply(it.obj, it.home);
    it.obj.updateMatrixWorld(true);
    it.onMove?.(it.obj);
    delete this.saved[it.id];
    it.saved = false;
    this._store();
    this._render();
  }

  _store() {
    try { localStorage.setItem(KEY, JSON.stringify(this.saved)); } catch { /* private mode */ }
  }

  world(it) {
    const o = it.obj;
    const p = o.getWorldPosition(this._v).clone();
    this._e.setFromQuaternion(o.getWorldQuaternion(this._q));
    const meta = this.game.world?.meta;
    const ll = meta ? {
      lat: meta.origin.lat + p.z / meta.mlat,
      lon: meta.origin.lon - p.x / meta.mlon,
    } : null;
    return { p, yaw: this._e.y, ll };
  }

  snippet(it) {
    const o = it.obj;
    const w = this.world(it);
    const f = (n, d = 2) => (Math.abs(n) < 5e-5 ? 0 : n).toFixed(d);
    const lines = [
      `// ${it.id}  (parent: ${o.parent?.name || 'scene'})`,
      `position.set(${f(o.position.x)}, ${f(o.position.y)}, ${f(o.position.z)});`,
      `rotation.set(${f(o.rotation.x, 4)}, ${f(o.rotation.y, 4)}, ${f(o.rotation.z, 4)});`,
      `scale.set(${f(o.scale.x)}, ${f(o.scale.y)}, ${f(o.scale.z)});`,
      `// world x=${f(w.p.x)} y=${f(w.p.y)} z=${f(w.p.z)} yaw=${f(w.yaw, 3)}`
        + (w.ll ? `  lat=${w.ll.lat.toFixed(6)} lon=${w.ll.lon.toFixed(6)}` : ''),
    ];
    return lines.join('\n');
  }

  copy() {
    const it = this.selected;
    if (!it) return;
    const text = this.snippet(it);
    navigator.clipboard?.writeText(text).catch(() => {});
    console.log(text);
    this.game._setHint?.('📋 დაკოპირდა — კონსოლშიც არის');
  }

  // ---- panel -------------------------------------------------------------------------
  _buildPanel() {
    const el = document.createElement('div');
    el.id = 'dev-panel';
    el.style.display = 'none';
    el.addEventListener('pointerdown', (e) => e.stopPropagation());
    el.addEventListener('keydown', (e) => e.stopPropagation());
    document.body.appendChild(el);
    this.panel = el;
    if (!document.getElementById('dev-css')) {
      const st = document.createElement('style');
      st.id = 'dev-css';
      st.textContent = `
#dev-panel{position:absolute;right:18px;top:236px;z-index:46;width:300px;padding:12px 14px;
  background:var(--panel,rgba(8,13,24,.86));border:1px solid var(--line,rgba(63,214,255,.28));
  border-radius:10px;color:var(--text,#d7ecf7);font:12.5px/1.45 var(--mono,ui-monospace,Menlo,monospace);
  box-shadow:0 0 22px rgba(0,180,255,.16);pointer-events:auto;user-select:none}
#dev-panel h4{margin:0 0 8px;font:600 13px/1.2 var(--display,sans-serif);letter-spacing:.6px;color:var(--cyan,#3fd6ff)}
#dev-panel .dv-list{display:flex;flex-wrap:wrap;gap:5px;margin-bottom:8px}
#dev-panel button{padding:4px 9px;border:1px solid var(--line,rgba(63,214,255,.28));border-radius:6px;
  background:rgba(63,214,255,.08);color:var(--text,#d7ecf7);font:inherit;cursor:pointer}
#dev-panel button:hover{background:rgba(63,214,255,.18)}
#dev-panel button.on{background:rgba(63,214,255,.3);border-color:var(--cyan,#3fd6ff);color:#fff}
#dev-panel .dv-row{display:flex;flex-wrap:wrap;gap:5px;margin:6px 0}
#dev-panel .dv-vals{margin:6px 0;padding:6px 8px;background:rgba(0,0,0,.28);border-radius:6px;white-space:pre;font-size:12px}
#dev-panel .dv-k{color:var(--dim,#6d8296)}
#dev-panel .dv-badge{float:right;color:var(--gold,#ffd257);font-size:11px}
#dev-panel .dv-help{color:var(--dim,#6d8296);font-size:11px;margin-top:6px;line-height:1.5}`;
      document.head.appendChild(st);
    }
  }

  _render() {
    if (!this.active) return;
    const it = this.selected;
    const mode = this.ctl?.mode ?? 'translate';
    const space = this.ctl?.space ?? 'world';
    const list = this.items.length
      ? this.items.map((i) => `<button data-id="${i.id}" class="${i === it ? 'on' : ''}">${i.id}${i.saved ? ' •' : ''}</button>`).join('')
      : '<span class="dv-k">ჯერ არაფერია რეგისტრირებული</span>';
    this.panel.innerHTML = `<h4>🛠 DEV — ობიექტების გადატანა</h4>
<div class="dv-list">${list}</div>
${it ? `<div><b>${it.id}</b> <span class="dv-k">← ${it.obj.parent?.name || 'scene'}</span>${it.saved ? '<span class="dv-badge">შენახულია •</span>' : ''}</div>
<div class="dv-row">${MODES.map(([m, l]) => `<button data-mode="${m}" class="${m === mode ? 'on' : ''}">${l}</button>`).join('')}</div>
<div class="dv-row"><button data-act="snap" class="${this.snap ? 'on' : ''}">⌗ snap</button>
<button data-act="space">${space === 'world' ? '🌐 world' : '📦 local'}</button>
<button data-act="copy">📋 კოპირება</button><button data-act="reset">↺ reset</button></div>
<div class="dv-vals"></div>` : '<div class="dv-k">დააწკაპუნე ობიექტზე, ან აირჩიე ზემოთ</div>'}
<div class="dv-help">გადაათრიე ისრები/რგოლები/კუბები · [ ] შემდეგი/წინა · Esc მოხსნა · \` დახურვა<br>ყოველი გადატანა ინახება და გადატვირთვისას რჩება; reset აბრუნებს კოდის ადგილზე</div>`;
    this.panel.querySelectorAll('[data-id]').forEach((b) => { b.onclick = () => this.select(b.dataset.id); });
    this.panel.querySelectorAll('[data-mode]').forEach((b) => { b.onclick = () => this.setMode(b.dataset.mode); });
    const act = { snap: () => this.setSnap(!this.snap), space: () => this.toggleSpace(), copy: () => this.copy(), reset: () => this.reset() };
    this.panel.querySelectorAll('[data-act]').forEach((b) => { b.onclick = act[b.dataset.act]; });
    this._renderValues();
  }

  _renderValues() {
    const it = this.selected;
    const box = this.panel.querySelector('.dv-vals');
    if (!it || !box) return;
    const o = it.obj;
    const w = this.world(it);
    const f = (n, d = 2) => (Math.abs(n) < 5e-5 ? 0 : n).toFixed(d);
    const deg = (r) => f(THREE.MathUtils.radToDeg(r), 1) + '°';
    box.innerHTML = `<span class="dv-k">local  </span>x ${f(o.position.x)}  y ${f(o.position.y)}  z ${f(o.position.z)}
<span class="dv-k">rot    </span>${deg(o.rotation.x)} ${deg(o.rotation.y)} ${deg(o.rotation.z)}  <span class="dv-k">(y=${f(o.rotation.y, 4)})</span>
<span class="dv-k">scale  </span>${f(o.scale.x)} ${f(o.scale.y)} ${f(o.scale.z)}
<span class="dv-k">world  </span>x ${f(w.p.x, 1)}  y ${f(w.p.y, 2)}  z ${f(w.p.z, 1)}  yaw ${deg(w.yaw)}
${w.ll ? `<span class="dv-k">latlon </span>${w.ll.lat.toFixed(6)}, ${w.ll.lon.toFixed(6)}` : ''}`;
  }

  dispose() {
    this.close();
    if (this.ctl) {
      this.ctl.dispose();
      this.game.scene.remove(this.helper);
      this.ctl = null;
      this.helper = null;
    }
    this.panel.remove();
  }
}

function snapshot(o) {
  return { p: o.position.toArray(), r: [o.rotation.x, o.rotation.y, o.rotation.z], s: o.scale.toArray() };
}

function apply(o, t) {
  if (t.p) o.position.fromArray(t.p);
  if (t.r) o.rotation.set(t.r[0], t.r[1], t.r[2]);
  if (t.s) o.scale.fromArray(t.s);
}
