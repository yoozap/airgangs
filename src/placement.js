import * as THREE from 'three';

// In-game placement editor for user-scanned models (photogrammetry .obj/.glb).
// Press P to enter; adjust the selected model live; download the updated
// custom_models.json to save. No command-line trial-and-error.
export class PlacementEditor {
  constructor(game) {
    this.game = game;
    this.active = false;
    this.index = 0;
    this._buildPanel();
  }

  get models() {
    return this.game.world?.customModels ?? [];
  }

  toggle() {
    if (!this.models.length) {
      this._flash('No custom models. Add one with tools/add_model.py first.');
      return;
    }
    this.active = !this.active;
    this.panel.style.display = this.active ? 'block' : 'none';
    if (this.active) {
      this._render();
      this._flyTo();
    }
  }

  // Park the plane above the selected model so you can see what you're placing.
  _flyTo() {
    const m = this.models[this.index];
    if (!m) return;
    const g = this.game;
    const pos = m.obj.position;
    g.airborne = true;
    g.speed = 0;
    g.plane.position.set(pos.x + 40, pos.y + 60, pos.z + 40);
    g.plane.lookAt(pos.x, pos.y, pos.z);
  }

  // called from the input handler while active; returns true if it consumed the key
  handleKey(code, shift) {
    if (!this.active || !this.models.length) return false;
    const m = this.models[this.index];
    const e = m.entry;
    const world = this.game.world;
    const step = shift ? 20 : 3; // meters
    const origin = world.meta.origin;
    const dLat = step / world.meta.mlat;
    const dLon = step / world.meta.mlon;
    let handled = true;
    switch (code) {
      case 'BracketRight': this.index = (this.index + 1) % this.models.length; this._flyTo(); break;
      case 'BracketLeft': this.index = (this.index - 1 + this.models.length) % this.models.length; this._flyTo(); break;
      case 'ArrowUp': e.lat += dLat; break;      // north
      case 'ArrowDown': e.lat -= dLat; break;
      case 'ArrowLeft': e.lon -= dLon; break;    // west
      case 'ArrowRight': e.lon += dLon; break;
      case 'KeyR': e.yaw = (e.yaw ?? 0) + (shift ? -0.1 : 0.1); break;
      case 'Equal': case 'NumpadAdd': e.scale = (e.scale ?? 1) * 1.06; break;
      case 'Minus': case 'NumpadSubtract': e.scale = (e.scale ?? 1) / 1.06; break;
      case 'PageUp': e.yOffset = (e.yOffset ?? 0) + step; break;
      case 'PageDown': e.yOffset = (e.yOffset ?? 0) - step; break;
      case 'KeyG': e.yOffset = 0; break; // snap to ground
      default: handled = false;
    }
    if (handled) {
      this._apply(m);
      this._render();
    }
    return handled;
  }

  _apply(m) {
    const world = this.game.world;
    const e = m.entry;
    const x = -(e.lon - world.meta.origin.lon) * world.meta.mlon;
    const z = (e.lat - world.meta.origin.lat) * world.meta.mlat;
    m.obj.position.set(x, world.heightAt(x, z) + (e.yOffset ?? 0), z);
    m.obj.rotation.y = e.yaw ?? 0;
    m.obj.scale.setScalar(e.scale ?? 1);
  }

  _buildPanel() {
    const el = document.createElement('div');
    el.id = 'placement-panel';
    el.style.cssText =
      'position:absolute;top:90px;left:18px;z-index:45;display:none;background:rgba(12,18,32,0.9);' +
      'color:#eef4fb;padding:14px 16px;border-radius:10px;font:13px/1.5 sans-serif;max-width:320px;';
    document.body.appendChild(el);
    this.panel = el;
  }

  _render() {
    const m = this.models[this.index];
    const e = m.entry;
    this.panel.innerHTML =
      `<b>📐 მოდელის განთავსება (${this.index + 1}/${this.models.length})</b><br>` +
      `<b>${e.file}</b><br>` +
      `lat ${e.lat.toFixed(5)} · lon ${e.lon.toFixed(5)}<br>` +
      `yaw ${(e.yaw ?? 0).toFixed(2)} · scale ${(e.scale ?? 1).toFixed(2)} · y ${(e.yOffset ?? 0).toFixed(1)}m<br>` +
      `<hr style="opacity:.2">` +
      `[ ] მოდელის არჩევა · ↑↓←→ გადატანა (Shift=სწრაფად)<br>` +
      `R ბრუნვა · +/− ზომა · PgUp/Dn სიმაღლე · G მიწაზე<br>` +
      `<button id="pl-save" style="margin-top:8px;padding:6px 12px;border:0;border-radius:6px;` +
      `background:#ffb84d;color:#222;font-weight:bold;cursor:pointer">💾 შენახვა (download json)</button>`;
    this.panel.querySelector('#pl-save').onclick = () => this._download();
  }

  _download() {
    const data = this.models.map((m) => m.entry);
    const blob = new Blob([JSON.stringify(data, null, 1)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'custom_models.json';
    a.click();
    this._flash('Saved. Move it to public/tbilisi/custom_models.json');
  }

  _flash(msg) {
    const f = document.createElement('div');
    f.textContent = msg;
    f.style.cssText =
      'position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);z-index:60;' +
      'background:rgba(12,18,32,0.92);color:#fff;padding:12px 20px;border-radius:8px;font:14px sans-serif';
    document.body.appendChild(f);
    setTimeout(() => f.remove(), 3200);
  }
}
