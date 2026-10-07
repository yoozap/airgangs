import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { weaponById, gearById, isGear, loadWeapon } from './weapons.js';
import { CARTRIDGES } from './ballistics.js';

// Ground loot, PUBG-style.
//
// Anything in the world tagged `userData.pickup = { id, name, slot, thumb }` is
// loot. Walk within RANGE on foot and a card appears naming the weapon and
// showing a picture of it rendered from the model itself (tools/import_pistols.py
// writes public/models/thumbs/<id>.png). F — or a click on the card — takes it
// into its weapon slot. Picking up a second weapon for a slot that is already
// full THROWS THE OLD ONE DOWN at your feet, where it is loot again.
const RANGE = 2.6;        // m — how close you must be for the card to show
const DROP_FORWARD = 0.8; // m — where a swapped-out weapon lands
const SPIN = 0.9;         // rad/s — the slow turn that says "this is loot"
const BOB = 0.035;        // m — gentle hover, so loot reads against clutter
const MAX_CARDS = 4;      // a bench with more than this on it shows the nearest four
const FOCUS_STICK = 0.15; // m — the lit card keeps the light unless something is this much closer

export class Pickups {
  constructor(game) {
    this.game = game;
    this.items = [];     // { obj, def, homeY, phase }
    this.near = null;
    this._t = 0;
    this._scanned = false;

    // ONE CARD PER THING IN REACH, stacked: standing at a bench with three
    // guns on it shows three cards, nearest on top and lit. F takes the lit
    // one; a click takes whichever card was clicked.
    const hud = game.ui.hud ?? document.body;
    this.el = document.createElement('div');
    this.el.id = 'pickup-cards';
    this.el.style.display = 'none';
    hud.appendChild(this.el);
    this._cards = new Map();   // item -> its card element, built once
    this._shown = '';          // which items are on screen, in which order

    if (!document.getElementById('pickup-css')) {
      const st = document.createElement('style');
      st.id = 'pickup-css';
      st.textContent = `
#pickup-cards{position:absolute;left:50%;bottom:21%;transform:translateX(-50%);
  display:flex;flex-direction:column;align-items:stretch;gap:7px;
  font-family:inherit;pointer-events:none;z-index:40}
#pickup-cards .pk-card{display:flex;align-items:center;gap:12px;padding:8px 16px 8px 10px;
  background:linear-gradient(180deg,rgba(6,16,24,.82),rgba(4,10,16,.9));
  border:1px solid rgba(90,220,255,.28);border-radius:10px;cursor:pointer;
  box-shadow:0 0 14px rgba(0,180,255,.12),inset 0 0 18px rgba(0,120,180,.08);
  pointer-events:auto;opacity:.78;transition:opacity .12s,border-color .12s,box-shadow .12s}
#pickup-cards .pk-card.focus{opacity:1;border-color:rgba(90,220,255,.6);
  box-shadow:0 0 22px rgba(0,180,255,.22),inset 0 0 18px rgba(0,120,180,.12)}
#pickup-cards .pk-img{width:64px;height:64px;object-fit:contain;
  filter:drop-shadow(0 2px 6px rgba(0,0,0,.6))}
#pickup-cards .pk-card.focus .pk-img{width:72px;height:72px}
#pickup-cards .pk-name{color:#dff6ff;font-size:15px;letter-spacing:.4px;
  text-shadow:0 0 8px rgba(90,220,255,.5)}
#pickup-cards .pk-cal{color:#8fe3b0;font-size:11.5px;margin-top:3px;letter-spacing:.3px;
  text-shadow:0 0 6px rgba(80,230,150,.35)}
#pickup-cards .pk-take{color:#7fd4ef;font-size:12px;margin-top:4px;opacity:.9}
#pickup-cards .pk-card:not(.focus) .pk-take{opacity:.55}
#pickup-cards .pk-take b{display:inline-block;min-width:18px;padding:1px 5px;
  margin-right:4px;border:1px solid rgba(90,220,255,.6);border-radius:4px;
  background:rgba(90,220,255,.12);color:#eaffff}`;
      document.head.appendChild(st);
    }
  }

  // The card for one item: its picture, its name, what it is chambered for,
  // and how to take it. Built once per item and kept.
  _card(it) {
    let el = this._cards.get(it);
    if (el) return el;
    el = document.createElement('div');
    el.className = 'pk-card';
    const c = CARTRIDGES[it.def.cartridge];
    const cal = c
      ? `${c.label} · ${c.mass} გ · ${c.v0} მ/წმ · ${Math.round(c.energy)} ჯ`
      : isGear(it.def) ? 'აღჭურვილობა — ჩაიცვამს' : '';
    el.innerHTML = `<img class="pk-img" alt="" src="${it.def.thumb ?? ''}"><div class="pk-text">`
      + `<div class="pk-name"></div><div class="pk-cal"></div>`
      + '<div class="pk-take"><b>F</b> — აღება</div></div>';
    el.querySelector('.pk-name').textContent = it.def.name;
    el.querySelector('.pk-cal').textContent = cal;
    el.addEventListener('pointerdown', (e) => { e.stopPropagation(); this.take(it); });
    this._cards.set(it, el);
    return el;
  }

  // Lay the cards out for what is in reach, lit one first. Only when the set
  // or the order has changed -- the bob and the spin never touch the DOM.
  _render(list) {
    const key = list.map((it) => it.obj.id).join(',');
    if (key === this._shown) return;
    this._shown = key;
    this.el.replaceChildren(...list.map((it) => this._card(it)));
    list.forEach((it, i) => this._cards.get(it).classList.toggle('focus', i === 0));
    this.el.style.display = list.length ? 'flex' : 'none';
  }

  // Loot is placed by the scenery builders, which finish well after the world
  // is playable, so the scene is swept once on the first update that finds it.
  scan() {
    this.items.length = 0;
    this.game.scene.traverse((o) => {
      const d = o.userData?.pickup;
      if (d && o.parent) this.items.push({ obj: o, def: d, homeY: o.position.y, phase: Math.random() * 6.28 });
    });
    this._scanned = this.items.length > 0;
    return this.items.length;
  }

  // Loot that arrives AFTER the sweep. The rifle is a 3.6 MB download and the
  // pistols are a few hundred kB, so the bench had been swept -- and the
  // sweep runs once -- before the rifle was on it: no card, ever. The scenery
  // builders hand each piece over as it lands.
  add(obj) {
    const d = obj?.userData?.pickup;
    if (!d || !obj.parent) return false;
    if (this.items.some((it) => it.obj === obj)) return true;
    this.items.push({ obj, def: d, homeY: obj.position.y, phase: Math.random() * 6.28 });
    this._scanned = true;
    return true;
  }

  // A weapon already in hand being thrown back down. It always carries its own
  // Object3D (set when it was picked up), so nothing has to be re-loaded.
  _drop(def, at, yaw) {
    const place = (obj) => {
      obj.userData.pickup = def;
      const fwd = new THREE.Vector3(-Math.sin(yaw), 0, -Math.cos(yaw));
      obj.position.copy(at).addScaledVector(fwd, DROP_FORWARD);
      const gy = this.game._groundAt(obj.position.x, obj.position.z, obj.position.y + 1.5, 1.5);
      obj.position.y = (gy ?? obj.position.y) + 0.02;
      obj.rotation.set(0, yaw + Math.PI / 2, 0);
      this.game.scene.add(obj);
      this.items.push({ obj, def, homeY: obj.position.y, phase: 0 });
    };
    if (def.obj) place(def.obj);
  }

  update(dt, heroPos) {
    if (!this._scanned) this.scan();
    this._t += dt;
    const near = [];
    for (const it of this.items) {
      it.obj.rotation.y += SPIN * dt;
      it.obj.position.y = it.homeY + Math.sin(this._t * 2 + it.phase) * BOB;
      const d = it.obj.position.distanceTo
        ? it.obj.getWorldPosition(_v).distanceTo(heroPos) : Infinity;
      if (d < RANGE) near.push({ d, it });
    }
    near.sort((a, b) => a.d - b.d);
    // The lit card is the nearest -- with a little stickiness, so two guns at
    // the same distance do not swap the light between them as you breathe.
    let focus = near[0]?.it ?? null;
    const cur = near.find((n) => n.it === this.near);
    if (cur && focus !== this.near && cur.d < near[0].d + FOCUS_STICK) focus = this.near;
    this.near = focus;
    const list = near.slice(0, MAX_CARDS).map((n) => n.it);
    if (focus && list[0] !== focus) {
      const i = list.indexOf(focus);
      if (i > 0) list.splice(i, 1);
      list.unshift(focus);
      if (list.length > MAX_CARDS) list.length = MAX_CARDS;
    }
    this._render(list);
  }

  // F / click: into the slot. A slot that already holds something throws that
  // weapon down first -- one weapon per slot, exactly like the request.
  take(which = null) {
    const it = which ?? this.near;
    if (!it || !this.items.includes(it)) return false;

    // GEAR is not a weapon: no slot, no draw, it just goes ON. Taking the belt
    // also gives a pistol the hero is already carrying somewhere to live.
    if (isGear(it.def)) {
      this.items.splice(this.items.indexOf(it), 1);
      it.obj.parent?.remove(it.obj);
      this._forget(it);
      const worn = { ...it.def, obj: it.obj };
      this.game.gear.set(worn.id, worn);
      this.game.foot?.wearGear?.(worn);
      this.game._setHint?.(`🩳 ${worn.name} — ჩაცმულია`);
      this.game.saveState?.(true, 'put gear on');
      return true;
    }

    const w = this.game.weapons;
    const slot = it.def.slot ?? 1;
    const old = w?.carried?.[slot] ?? null;

    this.items.splice(this.items.indexOf(it), 1);
    it.obj.parent?.remove(it.obj);
    this._forget(it);

    const held = { ...it.def, obj: it.obj };
    w?.setCarried(slot, held);
    w?.select(slot);

    if (old) {
      const p = this.game.foot?.pos ?? this.game.plane.position;
      this._drop(old, p.clone(), this.game.foot?.yaw ?? 0);
      this.game._setHint?.(`🔫 ${held.name} — აღებულია · ${old.name} დაგდებულია`);
    } else {
      this.game._setHint?.(`🔫 ${held.name} — აღებულია (სლოტი ${slot + 1})`);
    }
    // Straight to the hero's record, like putting gear on. The 2.5 s autosave
    // would have caught it, but not if the next thing that happens is a crash
    // -- and what you own should be written the moment you own it.
    this.game.saveState?.(true, 'took a weapon');
    return true;
  }

  // Give a personage back the weapons its saved state says it owns. The state
  // stores ids only, so the models are loaded here and dropped straight into
  // their slots -- owned, not lying on the floor.
  //
  // Returns what did NOT make it: { guns, gear }. The game keeps those in the
  // saved loadout (game.js `_ownedPending`) so one failed download never costs
  // the personage a weapon it owns -- it is simply tried again next time.
  async restore(guns, gear) {
    // In parallel: the AKM is 3.6 MB and a pistol a few hundred kB, and one
    // after the other the rifle held the whole loadout up for seconds.
    const want = (Array.isArray(guns) ? guns : []).filter((g) => weaponById(g.id));
    const lostGuns = [];
    await Promise.all(want.map(async (g) => {
      const def = weaponById(g.id);
      try {
        const obj = await loadWeapon(def);
        this.game.weapons?.setCarried(g.s ?? def.slot, { ...def, obj });
      } catch (e) {
        console.warn(`owned weapon ${g.id} failed to load:`, e?.message ?? e);
        lostGuns.push(g);
      }
    }));
    const n = want.length - lostGuns.length;
    // Worn gear. It goes on AFTER the weapons, so the belt finds the pistol
    // already in its slot and the holster is filled on the way in rather than
    // staying empty until the player next draws and puts away.
    let m = 0;
    const lostGear = [];
    for (const id of Array.isArray(gear) ? gear : []) {
      const def = gearById(id);
      if (!def) continue; // an id this build no longer has: genuinely gone
      this.game.gear.set(id, { ...def });
      if (await this.game.foot?.wearGear?.(def)) m++;
      else lostGear.push(id);
    }
    if (n || m || lostGuns.length) {
      console.log(`hero loadout: ${n} owned weapon(s), ${m} worn`
        + (lostGuns.length ? ` — ${lostGuns.map((g) => g.id).join(', ')} did not load (still owned)` : ''));
    }
    return { guns: lostGuns, gear: lostGear };
  }

  // An item that is no longer loot: its card goes, and the stack is rebuilt
  // on the next update from whatever is still in reach.
  _forget(it) {
    this._cards.get(it)?.remove();
    this._cards.delete(it);
    if (this.near === it) this.near = null;
    this._shown = '';
    this.el.style.display = this.el.children.length ? 'flex' : 'none';
  }

  setVisible(on) {
    if (!on) { this.el.style.display = 'none'; this.el.replaceChildren(); this._shown = ''; this.near = null; }
  }

  dispose() {
    this.el.remove();
    this.items.length = 0;
  }
}

const _v = new THREE.Vector3();
