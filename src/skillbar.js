// WoW-style action bar, bottom-center: 10 square slots with painted SVG
// ability icons in the Air Gangs neon style. Slot 1 = machine gun, slot 2 =
// rockets, the other 8 are empty sockets for future skills. Select with 1–0
// or click; Space fires whatever is selected. Rockets show a WoW cooldown
// sweep (conic gradient) while reloading.

// ---- ability icons: hand-drawn SVG, WoW composition (dark vignette, hero
// object on a diagonal, hot highlight) in cyan/gold/magenta cyberpunk palette.
const GUN_ICON = `
<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <radialGradient id="gi-bg" cx="0.42" cy="0.36" r="0.85">
      <stop offset="0" stop-color="#14324e"/>
      <stop offset="0.55" stop-color="#0a1526"/>
      <stop offset="1" stop-color="#04070f"/>
    </radialGradient>
    <linearGradient id="gi-steel" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#aeeaff"/>
      <stop offset="0.45" stop-color="#3f9ecc"/>
      <stop offset="1" stop-color="#10344e"/>
    </linearGradient>
    <linearGradient id="gi-dark" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#35506a"/>
      <stop offset="1" stop-color="#0c1e30"/>
    </linearGradient>
    <radialGradient id="gi-flash" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#fffbe8"/>
      <stop offset="0.4" stop-color="#ffd257"/>
      <stop offset="1" stop-color="#ff8a3e" stop-opacity="0"/>
    </radialGradient>
    <filter id="gi-glow" x="-60%" y="-60%" width="220%" height="220%">
      <feGaussianBlur stdDeviation="1.7"/>
    </filter>
  </defs>
  <rect width="64" height="64" fill="url(#gi-bg)"/>
  <!-- tracer streaks behind the gun -->
  <g stroke="#ffd257" stroke-linecap="round" filter="url(#gi-glow)" opacity="0.9">
    <line x1="30" y1="34" x2="57" y2="15" stroke-width="2.4"/>
    <line x1="36" y1="42" x2="60" y2="26" stroke-width="1.6" opacity="0.7"/>
    <line x1="26" y1="26" x2="46" y2="11" stroke-width="1.3" opacity="0.55"/>
  </g>
  <g transform="rotate(-16 30 38)">
    <!-- under-barrel + main barrel -->
    <rect x="27" y="40.5" width="15" height="2.6" rx="1.2" fill="url(#gi-dark)"/>
    <rect x="26" y="35.2" width="22" height="4.4" rx="1.6" fill="url(#gi-steel)"/>
    <rect x="46" y="33.6" width="4.6" height="7.6" rx="1.4" fill="url(#gi-dark)" stroke="#7ceaff" stroke-opacity="0.5" stroke-width="0.7"/>
    <!-- receiver -->
    <rect x="7" y="31.5" width="21" height="12.5" rx="2" fill="url(#gi-dark)" stroke="#7ceaff" stroke-opacity="0.45" stroke-width="0.8"/>
    <rect x="9.5" y="28.6" width="14" height="3.4" rx="1.4" fill="url(#gi-steel)"/>
    <!-- grip + trigger -->
    <path d="M13 44 L12 53 L17.5 53 L18.5 44 Z" fill="url(#gi-dark)" stroke="#7ceaff" stroke-opacity="0.35" stroke-width="0.7"/>
    <path d="M22 44 q3 1.5 6 0.5" stroke="#7ceaff" stroke-opacity="0.6" stroke-width="1" fill="none"/>
    <!-- ammo drum -->
    <circle cx="26" cy="47" r="6" fill="url(#gi-dark)" stroke="#7ceaff" stroke-opacity="0.55" stroke-width="0.9"/>
    <circle cx="26" cy="47" r="2.2" fill="#0a1626" stroke="#ffd257" stroke-opacity="0.8" stroke-width="0.8"/>
    <!-- rivets -->
    <g fill="#9fe8ff" opacity="0.8">
      <circle cx="11" cy="34" r="0.9"/><circle cx="24" cy="34" r="0.9"/><circle cx="11" cy="41" r="0.9"/>
    </g>
  </g>
  <!-- muzzle blast -->
  <g transform="translate(53 30)">
    <circle r="7.5" fill="url(#gi-flash)" filter="url(#gi-glow)"/>
    <path d="M0 -8 L1.8 -1.8 L8 0 L1.8 1.8 L0 8 L-1.8 1.8 L-8 0 L-1.8 -1.8 Z" fill="#fff3c4"/>
  </g>
  <!-- falling casings -->
  <g fill="#ffd257" opacity="0.85">
    <rect x="20" y="55" width="1.8" height="3.4" rx="0.8" transform="rotate(24 21 56)"/>
    <rect x="27" y="57" width="1.8" height="3.4" rx="0.8" transform="rotate(-18 28 58)"/>
  </g>
</svg>`;

const ROCKET_ICON = `
<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <radialGradient id="ri-bg" cx="0.62" cy="0.32" r="0.9">
      <stop offset="0" stop-color="#3a1638"/>
      <stop offset="0.5" stop-color="#141026"/>
      <stop offset="1" stop-color="#04070f"/>
    </radialGradient>
    <linearGradient id="ri-body" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#aeeaff"/>
      <stop offset="0.5" stop-color="#4aa8d6"/>
      <stop offset="1" stop-color="#123a54"/>
    </linearGradient>
    <linearGradient id="ri-nose" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#ff9ec4"/>
      <stop offset="1" stop-color="#ff3e8a"/>
    </linearGradient>
    <linearGradient id="ri-flame" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#fffbe8"/>
      <stop offset="0.35" stop-color="#ffd257"/>
      <stop offset="0.75" stop-color="#ff3e8a"/>
      <stop offset="1" stop-color="#ff3e8a" stop-opacity="0"/>
    </linearGradient>
    <filter id="ri-glow" x="-60%" y="-60%" width="220%" height="220%">
      <feGaussianBlur stdDeviation="1.8"/>
    </filter>
  </defs>
  <rect width="64" height="64" fill="url(#ri-bg)"/>
  <!-- speed streaks -->
  <g stroke="#7ceaff" stroke-linecap="round" opacity="0.55">
    <line x1="8" y1="52" x2="20" y2="40" stroke-width="1.4"/>
    <line x1="5" y1="42" x2="13" y2="34" stroke-width="1.1"/>
    <line x1="16" y1="59" x2="26" y2="49" stroke-width="1.1"/>
  </g>
  <g transform="rotate(45 32 32)">
    <!-- exhaust flame -->
    <path d="M32 42 C 28.4 48 28.8 54 32 61 C 35.2 54 35.6 48 32 42 Z" fill="url(#ri-flame)" filter="url(#ri-glow)"/>
    <path d="M32 42 C 30.4 46.5 30.6 50.5 32 55 C 33.4 50.5 33.6 46.5 32 42 Z" fill="#fff6d8"/>
    <!-- fins -->
    <path d="M28 33 L21.5 42.5 L28 40.5 Z" fill="url(#ri-nose)" stroke="#ff9ec4" stroke-opacity="0.5" stroke-width="0.6"/>
    <path d="M36 33 L42.5 42.5 L36 40.5 Z" fill="url(#ri-nose)" stroke="#ff9ec4" stroke-opacity="0.5" stroke-width="0.6"/>
    <!-- body -->
    <rect x="28" y="13" width="8" height="29" rx="3.6" fill="url(#ri-body)" stroke="#9fe8ff" stroke-opacity="0.5" stroke-width="0.7"/>
    <!-- nose cone -->
    <path d="M28 16 Q32 2.5 36 16 L36 19 L28 19 Z" fill="url(#ri-nose)"/>
    <!-- gold band + porthole -->
    <rect x="28" y="20.5" width="8" height="2" fill="#ffd257" opacity="0.95"/>
    <circle cx="32" cy="27.5" r="1.9" fill="#0a1626" stroke="#9fe8ff" stroke-width="0.8"/>
    <!-- body highlight -->
    <line x1="30" y1="15" x2="30" y2="40" stroke="#e6faff" stroke-opacity="0.75" stroke-width="0.9"/>
    <!-- nozzle -->
    <path d="M29 42 L28.2 44.5 L35.8 44.5 L35 42 Z" fill="#0c1e30" stroke="#7ceaff" stroke-opacity="0.5" stroke-width="0.6"/>
  </g>
</svg>`;

const SLOT_KEYS = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'];
const CODE_TO_SLOT = Object.fromEntries(
  SLOT_KEYS.map((k, i) => [`Digit${k}`, i])
);

export class SkillBar {
  constructor(game) {
    this.game = game;
    this.slots = [
      { id: 'gun', name: 'ტყვიამფრქვევი — სწრაფი ცეცხლი', icon: GUN_ICON },
      { id: 'rocket', name: 'რაკეტები — ანგრევს შენობებს', icon: ROCKET_ICON },
      null, null, null, null, null, null, null, null,
    ];
    this.index = 0;
    this._cdShown = new Array(10).fill(0);

    const hud = game.ui.hud ?? document.body;
    this.el = document.createElement('div');
    this.el.id = 'actionbar';
    this.el.innerHTML = '<i class="ab-cap l"></i><div class="ab-slots"></div><i class="ab-cap r"></i>';
    const row = this.el.querySelector('.ab-slots');
    this.slotEls = this.slots.map((s, i) => {
      const d = document.createElement('div');
      d.className = 'ab-slot' + (s ? '' : ' empty');
      d.dataset.i = i;
      if (s) {
        d.title = s.name;
        d.innerHTML = `${s.icon}<span class="ab-key">${SLOT_KEYS[i]}</span><div class="ab-cd"></div>`;
      } else {
        d.title = 'ცარიელი სლოტი';
        d.innerHTML = `<span class="ab-key">${SLOT_KEYS[i]}</span>`;
      }
      d.addEventListener('pointerdown', (e) => {
        e.stopPropagation();
        this.select(i);
      });
      row.appendChild(d);
      return d;
    });
    hud.appendChild(this.el);
    this.select(0);
  }

  get current() {
    return this.slots[this.index]?.id ?? null;
  }

  select(i) {
    if (!this.slots[i]) {
      // empty socket: WoW-style refusal shake
      const el = this.slotEls[i];
      el.classList.remove('deny');
      void el.offsetWidth; // restart the animation
      el.classList.add('deny');
      return;
    }
    this.index = i;
    this.slotEls.forEach((el, k) => el.classList.toggle('sel', k === i));
    // crosshair tint follows the weapon (rockets are magenta)
    this.game.ui.hud?.classList.toggle('weapon-rocket', this.current === 'rocket');
  }

  // Digit1..Digit0 → slot select. Returns true when the key was consumed.
  selectByCode(code) {
    const i = CODE_TO_SLOT[code];
    if (i === undefined) return false;
    this.select(i);
    return true;
  }

  // frac: 1 = just fired, 0 = ready. Drawn as the classic WoW radial sweep.
  setCooldown(i, frac) {
    const el = this.slotEls[i]?.querySelector('.ab-cd');
    if (!el) return;
    const prev = this._cdShown[i];
    this._cdShown[i] = frac;
    if (frac <= 0) {
      el.style.display = 'none';
      if (prev > 0) {
        // ready flash when the sweep completes
        const s = this.slotEls[i];
        s.classList.remove('ready');
        void s.offsetWidth;
        s.classList.add('ready');
      }
      return;
    }
    el.style.display = 'block';
    el.style.setProperty('--cd', `${(frac * 360).toFixed(1)}deg`);
  }

  // brief hot flash on the slot when its ability fires
  pulse(i) {
    const el = this.slotEls[i];
    if (!el) return;
    el.classList.remove('fire');
    void el.offsetWidth;
    el.classList.add('fire');
  }

  // On foot there is no plane to fire, so the action bar has nothing to offer:
  // guns.js and rockets.js already refuse to fire while foot.active, this just
  // stops the bar sitting there implying otherwise.
  setVisible(on) {
    this.el.style.display = on ? '' : 'none';
  }

  dispose() {
    this.el.remove();
  }
}


// ---------------------------------------------------------------------------
// On-foot weapon slots.
//
// GTA's wheel is eight CATEGORIES, not eight guns: you always have the slots,
// they just fill up as you find things. These are those eight, all empty --
// the personage carries nothing yet, and picking a weapon up later only has to
// drop it into the matching slot rather than invent a UI.
const REPRESS_MS = 140; // one physical press must not toggle twice

const WEAPON_SLOTS = [
  { id: 'melee', name: 'ხელჩართული — ჯერ არაფერია', icon: '🔪' },
  { id: 'pistol', name: 'პისტოლეტი — ჯერ არაფერია', icon: '🔫' },
  { id: 'smg', name: 'ავტომატი — ჯერ არაფერია', icon: '🌀' },
  { id: 'shotgun', name: 'ხელჩართული თოფი — ჯერ არაფერია', icon: '💥' },
  { id: 'rifle', name: 'ავტომატური შაშხანა — ჯერ არაფერია', icon: '🎯' },
  { id: 'sniper', name: 'სნაიპერი — ჯერ არაფერია', icon: '🔭' },
  { id: 'heavy', name: 'მძიმე იარაღი — ჯერ არაფერია', icon: '🚀' },
  { id: 'thrown', name: 'სასროლი — ჯერ არაფერია', icon: '💣' },
];

export class WeaponSlots {
  constructor(game) {
    this.game = game;
    this.index = -1;                 // nothing drawn: bare hands
    this.carried = new Array(WEAPON_SLOTS.length).fill(null);
    const hud = game.ui.hud ?? document.body;
    this.el = document.createElement('div');
    this.el.id = 'weaponbar';
    this.el.innerHTML = '<i class="ab-cap l"></i><div class="ab-slots"></div><i class="ab-cap r"></i>';
    const row = this.el.querySelector('.ab-slots');
    this.slotEls = WEAPON_SLOTS.map((w, i) => {
      const d = document.createElement('div');
      d.className = 'ab-slot empty';
      d.dataset.i = i;
      d.title = w.name;
      d.innerHTML = `<span class="ab-wicon">${w.icon}</span>`
        + `<span class="ab-key">${i + 1}</span>`
        + '<span class="ab-ammo"></span>';
      d.addEventListener('pointerdown', (e) => { e.stopPropagation(); this.select(i); });
      row.appendChild(d);
      return d;
    });
    hud.appendChild(this.el);
    this.setVisible(false);          // the plane is flying; this belongs on foot
  }

  get current() {
    return this.carried[this.index] ?? null;
  }

  // TOGGLE. Pressing the slot you are already on puts the weapon away again --
  // the bar is "drawn / holstered", not just a highlight.
  //
  // A toggle is only safe if one physical press cannot reach here twice (key
  // auto-repeat, or a pointerdown that also raises a key event), so repeats
  // inside REPRESS_MS are swallowed.
  select(i) {
    if (i < 0 || i >= WEAPON_SLOTS.length) return false;
    const now = performance.now();
    if (i === this._lastI && now - (this._lastT ?? -1e9) < REPRESS_MS) return true;
    this._lastI = i;
    this._lastT = now;

    this.index = this.index === i ? -1 : i;
    this.slotEls.forEach((el, k) => el.classList.toggle('sel', k === this.index));
    const w = WEAPON_SLOTS[i];
    const held = this.carried[i];
    if (this.index === -1) {
      this.game.foot?.holster?.();
      if (held) this.game._setHint?.(`${held.name} — დამალულია`);
    } else if (held) {
      this.game.foot?.drawWeapon?.(held);
      this.game._setHint?.(`🔫 ${held.name} — ხელშია`);
    } else {
      this.game.foot?.holster?.();
      this.game._setHint?.(`${w.icon} ${w.name}`);
    }
    return true;
  }

  selectByCode(code) {
    const i = ['Digit1', 'Digit2', 'Digit3', 'Digit4',
      'Digit5', 'Digit6', 'Digit7', 'Digit8'].indexOf(code);
    return i < 0 ? false : this.select(i);
  }

  // Put a weapon in a slot (or null to empty it). The slot shows the weapon's
  // own rendered picture instead of the category emoji, so the bar reads like
  // GTA's: a drawn slot is a weapon you are actually carrying.
  setCarried(i, weapon) {
    if (i < 0 || i >= WEAPON_SLOTS.length) return;
    this.carried[i] = weapon ?? null;
    const el = this.slotEls[i];
    const icon = el.querySelector('.ab-wicon');
    if (weapon) {
      el.classList.remove('empty');
      el.title = weapon.name;
      icon.innerHTML = weapon.thumb
        ? `<img src="${weapon.thumb}" alt="" style="width:78%;height:78%;object-fit:contain">`
        : WEAPON_SLOTS[i].icon;
    } else {
      el.classList.add('empty');
      el.title = WEAPON_SLOTS[i].name;
      icon.textContent = WEAPON_SLOTS[i].icon;
    }
  }

  // HOW MANY ROUNDS ARE LEFT, on the weapon's own icon -- the count GTA puts
  // next to the gun. It goes on the slot the weapon is actually drawn from, so
  // the number and the picture are the same weapon, and it clears off every
  // other slot. Shooting.js calls this every frame; the DOM is only touched
  // when the text or the colour actually changes.
  setAmmo(n, max, reloading = false) {
    const i = this.index;
    const txt = i < 0 || n == null || !max ? '' : reloading ? '· · ·' : `${n}`;
    const key = `${i}|${txt}|${n <= 0 && !reloading ? 'e' : ''}`;
    if (key === this._ammoKey) return;
    this._ammoKey = key;
    this.slotEls.forEach((el, k) => {
      const a = el.querySelector('.ab-ammo');
      if (!a) return;
      a.textContent = k === i ? txt : '';
      a.classList.toggle('low', k === i && !reloading && max > 0 && n <= Math.max(1, max * 0.25));
      a.classList.toggle('out', k === i && !reloading && n <= 0);
    });
  }

  setVisible(on) {
    this.el.style.display = on ? '' : 'none';
  }

  dispose() {
    this.el.remove();
  }
}
