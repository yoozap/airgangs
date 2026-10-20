import * as THREE from 'three';
import { planeStamina, planeRole, planeTopSpeed, planeCrashDmg, planeMass, planeSpecSheet } from './planes.js';

// WOW-STYLE TARGETING.
//
// "on click of plane i need see hp and model of plane, targets must be like
// wow. in right of our hp card, card of target, image hp name"
//
// Click an aeroplane and it becomes your TARGET: a second card appears to the
// right of your own hull card (index.html #healthbar / #targetframe) carrying
// the machine's picture, its name and class, and its hull bar — exactly the
// shape of a World of Warcraft target frame, which is a portrait, a name and
// a health bar and nothing else.
//
// WHAT IS TARGETABLE. Every aeroplane in the game: the 37 parked in the plane
// hall (src/planehall.js registers each one as it is placed) and the player's
// own machine. The pick rides on the collider the hall already bakes for each
// airframe, so a click is one raycast against the SAME spatially-hashed set
// the bullets use — nothing extra in the scene, and a plane behind a wall is
// not clickable because the wall is in that set too.
//
// HULL POINTS. A parked machine carries its own pool (planes.js planeStamina);
// the player's own card reads the live hull in game.health. Rounds and rockets
// that land on an airframe come through damagePlane() below, so the bar you
// are looking at is the bar you are shooting at.
//
// Keys: click = target what is under the cursor (or the crosshair, when a
// weapon has the pointer locked), Tab = the nearest machine in front of you,
// Esc = drop the target.

const PICK_RANGE = 700;   // m — how far a click reaches
const CYCLE_RANGE = 500;  // m — what Tab will pick up
const CYCLE_DOT = 0.25;   // Tab only takes what is roughly in front of you
const WHEEL = 1.7;        // m — plane origin above its wheels (game.js WHEEL_HEIGHT)
const DOWN = new THREE.Vector3(0, -1, 0);

export class Targets {
  constructor(game) {
    this.game = game;
    this.list = [];        // every registered machine
    this.rec = null;       // what is targeted right now
    this.ray = new THREE.Raycaster();
    this.ray.firstHitOnly = true;  // three-mesh-bvh: the colliders carry BVHs
    this._ndc = new THREE.Vector2();
    this._v = new THREE.Vector3();
    this._fwd = new THREE.Vector3();
    this._down = new THREE.Vector3();
    this._hitT = 0;        // the frame flashes for a moment when it takes a hit
    this._key = null;      // what the DOM is showing, so it is written once
    this.ring = null;      // the selection circle on the ground under the target

    const $ = (id) => document.getElementById(id);
    this.el = $('targetframe');
    this.ui = {
      img: $('tf-portrait'), name: $('tf-name'), sub: $('tf-sub'),
      role: $('tf-role'), fill: $('tf-fill'), hp: $('tf-hp'), dist: $('tf-dist'),
      stam: $('tf-stam'), speed: $('tf-speed'), ram: $('tf-ram'), mass: $('tf-mass'),
    };
    // clicking the card itself must not fall through to the canvas (which
    // would start a camera drag under the player's cursor)
    this.el?.addEventListener('pointerdown', (e) => e.stopPropagation());
  }

  // ---- the registry --------------------------------------------------------

  // A parked aeroplane. `collider` is the invisible box the hall bakes for it:
  // it is what a click and a bullet both hit, so the record hangs off it.
  register(wrapper, def, collider = null) {
    const maxHp = planeStamina(def);
    const rec = { def, wrapper, collider, maxHp, hp: maxHp, dead: false };
    if (collider) collider.userData.planeRec = rec;
    this.list.push(rec);
    return rec;
  }

  // ...and off it again, when the hero climbs into it and it stops being a
  // parked aeroplane. Anything pointing at it goes too: a target card left
  // showing the machine you are now flying is a card showing yourself.
  unregister(rec) {
    const i = this.list.indexOf(rec);
    if (i >= 0) this.list.splice(i, 1);
    if (rec?.collider) delete rec.collider.userData.planeRec;
    if (this.rec === rec) this.clear();
    return i >= 0;
  }

  // The player's own machine, as a target. Rebuilt if the aeroplane changes.
  _ownRec() {
    const g = this.game;
    if (!this._own || this._own.wrapper !== g.plane) {
      this._own = { def: g.def, wrapper: g.plane, own: true, maxHp: g.maxHealth ?? 1000 };
    }
    return this._own;
  }

  hpOf(rec) { return rec.own ? Math.max(0, Math.round(this.game.health)) : rec.hp; }

  // ---- picking ------------------------------------------------------------

  // A click. Returns the record it landed on, or null — the caller carries on
  // either way, so a click on the sky is still a camera drag.
  pickAt(ev) {
    const g = this.game;
    const canvas = g.renderer.domElement;
    const r = canvas.getBoundingClientRect();
    // POINTER LOCKED (a weapon is out and the cursor is nailed to the middle
    // of the screen): clientX/Y are frozen wherever the lock caught them, so
    // the pick comes off the centre — the same place the shot does.
    const locked = document.pointerLockElement === canvas;
    const x = locked ? r.width / 2 : ev.clientX - r.left;
    const y = locked ? r.height / 2 : ev.clientY - r.top;
    this._ndc.set((x / r.width) * 2 - 1, -(y / r.height) * 2 + 1);
    this.ray.setFromCamera(this._ndc, g.camera);
    this.ray.far = PICK_RANGE;
    const near = g.world?.collidablesNear
      ? g.world.collidablesNear(g.camera.position.x, g.camera.position.z)
      : (g.world?.collidables ?? []);
    const hits = this.ray.intersectObjects(g.plane ? [g.plane, ...near] : near, true);
    this.ray.far = Infinity;
    const rec = hits.length ? this._recOf(hits[0].object) : null;
    if (rec) this.set(rec);
    return rec;
  }

  // Tab: the nearest machine in front of the camera, and again for the next
  // one out. Nothing in front means nothing targeted, as in WoW.
  cycle() {
    const g = this.game;
    const eye = g.camera.position;
    g.camera.getWorldDirection(this._fwd);
    const near = [];
    for (const rec of this.list) {
      if (!rec.wrapper?.parent) continue;        // not in the world (yet)
      rec.wrapper.getWorldPosition(this._v);
      const d = this._v.distanceTo(eye);
      if (d > CYCLE_RANGE) continue;
      if (this._v.sub(eye).normalize().dot(this._fwd) < CYCLE_DOT) continue;
      near.push({ rec, d });
    }
    near.sort((a, b) => a.d - b.d);
    const order = near.map((e) => e.rec);
    if (!order.length) { this.clear(); return null; }
    const i = order.indexOf(this.rec);
    const next = order[(i + 1) % order.length];
    this.set(next);
    return next;
  }

  // Which machine a ray hit, for anyone outside: the plane physics asks this
  // of every swept contact, because an aeroplane and a wall are no longer the
  // same kind of thing to fly into (src/planephys.js).
  recOf(obj) { return this._recOf(obj); }

  // Which machine a ray hit: the hall's colliders carry their record, and the
  // player's own aeroplane is recognised by its root (the ray walks into the
  // cockpit kit and the pilot, so the parents are walked up).
  _recOf(obj) {
    let o = obj;
    while (o) {
      if (o.userData?.planeRec) return o.userData.planeRec;
      if (o === this.game.plane) return this._ownRec();
      o = o.parent;
    }
    return null;
  }

  // ---- the frame ----------------------------------------------------------

  set(rec) {
    if (!rec) { this.clear(); return; }
    this.rec = rec;
    this._key = null;        // force a full write
    this.el?.classList.remove('hidden');
    this._paint();
  }

  clear() {
    this.rec = null;
    this._key = null;
    this.el?.classList.add('hidden');
    if (this.ring) this.ring.visible = false;
  }

  // Everything the card shows, written only when it has changed — this runs
  // every frame and the DOM is the one thing in here that is not free.
  _paint() {
    const rec = this.rec;
    if (!rec || !this.el) return;
    const def = rec.def ?? {};
    const hp = this.hpOf(rec);
    const max = rec.maxHp || 1;
    const from = this.game.foot?.active ? this.game.foot.pos : this.game.plane.position;
    rec.wrapper.getWorldPosition(this._v);
    const dist = Math.round(this._v.distanceTo(from));
    const key = `${def.id}|${hp}|${dist}|${this._hitT > 0 ? 1 : 0}`;
    if (key === this._key) return;
    const fresh = !this._key || !this._key.startsWith(`${def.id}|`);
    this._key = key;
    const u = this.ui;
    if (fresh) {
      if (u.img) {
        u.img.src = def.thumb ?? '';
        u.img.alt = def.name ?? '';
      }
      if (u.name) u.name.textContent = def.name ?? 'უცნობი';
      if (u.sub) {
        u.sub.textContent = rec.own
          ? `${def.code ?? ''} · შენი მანქანა`
          : [def.code, def.cls].filter(Boolean).join(' · ');
      }
      if (u.role) {
        const tank = planeRole(def) === 'TANK';
        u.role.textContent = tank ? '🛡 TANK' : '⚔ DPS';
        u.role.classList.toggle('tank', tank);
      }
      // WHAT THE MACHINE IS MADE OF. "planes must have stamina, speed, damage
      // when crash" -- hull pool, real top speed, what ramming with it does,
      // and the weight all three of those come out of. Written with the rest
      // of the fresh card: for a given aeroplane they never change.
      const pool = rec.own ? (this.game.maxHealth ?? 1000) : rec.maxHp;
      if (u.stam) u.stam.textContent = pool;
      if (u.speed) u.speed.textContent = `${planeTopSpeed(def)} კმ/სთ`;
      if (u.ram) u.ram.textContent = planeCrashDmg(def);
      // in tonnes throughout: it is the number the whole collision model turns
      // on, and "0.8 ტ" fits the row where "810 კგ" wrapped
      const kg = planeMass(def);
      if (u.mass) {
        u.mass.textContent = kg >= 10000 ? `${Math.round(kg / 1000)} ტ` : `${(kg / 1000).toFixed(1)} ტ`;
      }
      // ...and the rest of the real data -- span, length, weights, ceiling,
      // climb, crew, first flight -- on the card itself, where there is room
      // for it: hover it.
      this.el.title = planeSpecSheet(def);
    }
    const p = THREE.MathUtils.clamp(hp / max, 0, 1);
    if (u.fill) {
      u.fill.style.width = `${p * 100}%`;
      // WoW reads the bar by colour: green while it is healthy, orange on the
      // way down, red when the machine is nearly out of hull.
      u.fill.style.background = p > 0.55
        ? 'linear-gradient(90deg, #1fb562, #4af09a)'
        : p > 0.25
          ? 'linear-gradient(90deg, #d08a1a, #ffc44d)'
          : 'linear-gradient(90deg, #c8232f, #ff6a5a)';
    }
    if (u.hp) u.hp.textContent = rec.dead ? 'განადგურებული' : `${hp} / ${max}`;
    if (u.dist) u.dist.textContent = `${dist} მ`;
    this.el.classList.toggle('dead', !!rec.dead);
    this.el.classList.toggle('hit', this._hitT > 0);
  }

  // The circle on the floor under the target, WoW's selection ring. One unlit
  // ring, no light and no shadow -- and its geometry is rebuilt whenever the
  // radius changes, because the band has to be a fixed number of METRES wide:
  // a ratio that reads on a 44 m An-225 is a hairline under a Spitfire.
  _ensureRing(r) {
    if (!this.ring) {
      const ring = new THREE.Mesh(undefined, new THREE.MeshBasicMaterial({
        color: 0x3fd6ff, transparent: true, opacity: 0.6,
        side: THREE.DoubleSide, depthWrite: false,
      }));
      ring.name = 'target_ring';
      ring.frustumCulled = false;
      ring.renderOrder = 4;
      this.game.scene.add(ring);
      this.ring = ring;
    }
    if (Math.abs((this._ringR ?? 0) - r) > 0.05) {
      this._ringR = r;
      const w = THREE.MathUtils.clamp(r * 0.06, 0.35, 1.6); // band width, metres
      const geo = new THREE.RingGeometry(r - w, r, 72);
      geo.rotateX(-Math.PI / 2);
      this.ring.geometry?.dispose();
      this.ring.geometry = geo;
    }
    return this.ring;
  }

  // WHAT THE RING STANDS ON. A parked machine rests its wheels on a floor
  // that is NOT at plane-origin-minus-wheel-height -- hangar slabs are proud
  // of the ground they sit on and the aprons roll -- and a ring buried under
  // the floor is a ring nobody sees. Probe straight down for the real surface
  // once per target (they are parked: the answer never changes) and skip the
  // aeroplanes' own colliders on the way.
  _ringY(rec, at) {
    if (rec.ringY !== undefined) return rec.ringY;
    const g = this.game;
    const near = g.world?.collidablesNear
      ? g.world.collidablesNear(at.x, at.z)
      : (g.world?.collidables ?? []);
    this.ray.set(this._down.set(at.x, at.y + 1, at.z), DOWN);
    this.ray.far = 14;
    const hits = this.ray.intersectObjects(near, true);
    this.ray.far = Infinity;
    const floor = hits.find((h) => !h.object.userData?.planeRec);
    rec.ringY = (floor ? floor.point.y : at.y - WHEEL) + 0.1;
    return rec.ringY;
  }

  update(dt) {
    if (this._hitT > 0) this._hitT = Math.max(0, this._hitT - dt);
    const rec = this.rec;
    if (!rec) return;
    // the machine went away (back to the menu, a fresh aeroplane): drop it
    if (!rec.wrapper || (!rec.own && !rec.wrapper.parent)) { this.clear(); return; }
    this._paint();
    // Your own machine gets no ring — it would hang in the air under you while
    // you fly. A parked one gets the circle on the hangar floor.
    if (rec.own) { if (this.ring) this.ring.visible = false; return; }
    // The hall switches its whole fleet off beyond 480 m (planehall.js
    // SHOW_DIST). Keep the card -- WoW holds a target you have flown away
    // from -- but take the ring off the floor of an empty hangar.
    let vis = true;
    for (let o = rec.wrapper; o && vis; o = o.parent) if (!o.visible) vis = false;
    if (!vis) { if (this.ring) this.ring.visible = false; return; }
    rec.wrapper.getWorldPosition(this._v);
    const ring = this._ensureRing(Math.max(3, (rec.wrapper.userData.halfSpan ?? 6) * 1.06));
    ring.position.set(this._v.x, this._ringY(rec, this._v), this._v.z);
    ring.material.opacity = rec.dead ? 0.25 : 0.45 + 0.25 * Math.sin(this.game.clock.elapsedTime * 3);
    ring.material.color.setHex(rec.dead ? 0xff5566 : 0x3fd6ff);
    ring.visible = true;
  }

  // ---- damage -------------------------------------------------------------

  // A round or a rocket landed on a parked airframe. Called from CityMap's
  // damageHit, which hands the hit straight over when the collider carries a
  // plane record — and hands back null, so every caller treats the hit the way
  // it treats a statue: sparks and a bang, no address, no collapse.
  // `fx: false` means the caller is going to stage the destruction itself --
  // src/planephys.js does, because a machine knocked out of the hall burns on
  // the way down and detonates where it lands, not in the hangar it left.
  damagePlane(rec, dmg, { fx = true } = {}) {
    if (!rec || rec.dead) return null;
    // Rounded: the collision solver works in joules and hands over fractions,
    // and the card is not going to read "407.2045941386468 / 1019".
    // YOUR OWN MACHINE has no hull pool of its own -- its bar IS game.health
    // (hpOf reads it), and `rec.hp` does not exist, so the parked fleet's
    // arithmetic would quietly write NaN into it. It is floored at 1 as well:
    // standing beside your own aeroplane and emptying a magazine into it
    // should chew it up and show every hole, not drop the death screen on
    // you while you are on foot.
    if (rec.own) {
      this.game.health = Math.max(1, this.game.health - dmg);
      this.game._updateHealthBar?.();
    } else {
      rec.hp = Math.max(0, Math.round(rec.hp - dmg));
    }
    this._hitT = 0.3;
    // A machine that has been hurt smokes out of its engine bay and burns once
    // it is nearly gone -- whatever hurt it, a bullet or a rocket or another
    // aeroplane flown into it (src/planedamage.js).
    this.game.planeDamage?.watch(rec);
    // Shooting a machine IS targeting it: the bar you just moved is the bar
    // that comes up, without the player having to click first.
    if (this.rec !== rec) this.set(rec);
    else this._key = null;
    if (!rec.own && rec.hp <= 0) this._kill(rec, fx);
    this._paint();
    return rec;
  }

  // The killing shot. The airframe stays where it is -- it is a parked museum
  // piece, not a flying machine that can fall -- but it goes up, and the card
  // reads განადგურებული from here on.
  _kill(rec, fx = true) {
    rec.dead = true;
    if (!fx) return;
    const g = this.game;
    rec.wrapper.getWorldPosition(this._v);
    const r = Math.max(4, (rec.wrapper.userData.halfSpan ?? 6) * 0.8);
    g.fx?.explosion(this._v.x, this._v.y + 1, this._v.z, r);
    g.fx?.plume(this._v.x, this._v.y - WHEEL, this._v.z, r * 0.7, r * 1.4);
    g.world?.onShake?.(0.35, this._v.x, this._v.z);
  }

  dispose() {
    this.clear();
    if (this.ring) {
      this.game.scene.remove(this.ring);
      this.ring.geometry?.dispose();
      this.ring.material.dispose();
      this.ring = null;
    }
    this.list.length = 0;
  }
}
