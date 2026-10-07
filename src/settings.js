// In-game settings window (Esc / ⚙) in the Air Gangs style.
// Tabs: graphics / sound / controls. Persists to localStorage.

const $ = (id) => document.getElementById(id);

const QUALITY = {
  low: { pixelRatio: 0.75, hq: false },
  med: { pixelRatio: 1.0, hq: false },
  high: { pixelRatio: 1.5, hq: true },
  ultra: { pixelRatio: Math.min(2, window.devicePixelRatio || 1.5), hq: true },
};

const DEFAULTS = {
  quality: 'ultra', // game boots at max graphics
  engine: true,  // the aeroplane makes a noise unless you turn it off
  volume: 70,
  minimap: true,
  hints: true,
  soundChosen: false, // has the player actually picked, or is this the default?
  side: 'right',    // which shoulder the weapon camera sits over (Counter-Strike's hand switch)
  aimOnDraw: true,  // a drawn weapon comes up aimed
};

export class SettingsUI {
  // game may be null — the start menu opens the same window before any flight;
  // Game.attach()es itself later and the saved state gets applied.
  constructor(game = null) {
    this.game = game;
    this.win = $('settingswin');
    this.visible = false;

    this.state = { ...DEFAULTS };
    try {
      Object.assign(this.state, JSON.parse(localStorage.getItem('tf-settings') || '{}'));
    } catch {}
    // SOUND IS NOT FORCED OFF ANY MORE. It used to be, every session, "because
    // the audio context needs a user gesture" -- but by the time a flight
    // starts the player has been through a login, a menu, a hangar and a plane
    // card, so the gesture happened long ago (src/audiofx.js leans on the same
    // fact for the weapons). Forcing it off meant the engine was silent until
    // somebody found the N key, which is not what an aeroplane does.
    //
    // AND THE OLD VALUE HAS TO BE THROWN AWAY. That forced `false` was SAVED
    // with the rest of the settings, so every existing player has
    // `engine: false` sitting in localStorage -- a bug's residue, not a
    // preference, and changing the default does nothing for them. Anyone
    // without the marker below gets the sound back once; after that their own
    // choice is theirs and is kept.
    if (!this.state.soundChosen) this.state.engine = DEFAULTS.engine;

    this._bind();
    this._applyAll();
    this._sync();
  }

  attach(game) {
    this.game = game;
    this._applyAll();
    this._sync();
  }

  open() {
    this.visible = true;
    this.win.classList.remove('hidden');
    this._sync();
  }

  close() {
    this.visible = false;
    this.win.classList.add('hidden');
  }

  _save() {
    try {
      localStorage.setItem('tf-settings', JSON.stringify(this.state));
    } catch {}
  }

  _bind() {
    $('btn-settings').addEventListener('click', () => (this.visible ? this.close() : this.open()));
    $('settings-close').addEventListener('click', () => this.close());
    this.win.addEventListener('mousedown', (e) => {
      if (e.target === this.win) this.close(); // click outside the frame
    });

    // tabs
    this.win.querySelectorAll('.sw-tab').forEach((t) =>
      t.addEventListener('click', () => {
        this.win.querySelectorAll('.sw-tab').forEach((o) => o.classList.toggle('active', o === t));
        for (const key of ['gfx', 'snd', 'ctl'])
          $(`sw-${key}`).classList.toggle('hidden', key !== t.dataset.tab);
      })
    );

    // graphics
    $('set-quality')
      .querySelectorAll('button')
      .forEach((b) =>
        b.addEventListener('click', () => {
          this.state.quality = b.dataset.v;
          this._applyQuality(true); // chosen by hand: pin it, no auto-drop
          this._save();
          this._sync();
        })
      );
    $('set-fullscreen').addEventListener('click', () => {
      if (document.fullscreenElement) document.exitFullscreen();
      else document.documentElement.requestFullscreen?.();
    });
    document.addEventListener('fullscreenchange', () => this._sync());

    // sound
    this._toggle('set-engine', 'engine', () => {
      this.state.soundChosen = true;   // a real choice: the migration leaves it alone
      this._applyEngine();
    });
    $('set-volume').addEventListener('input', (e) => {
      this.state.volume = +e.target.value;
      this._applyVolume();
      this._save();
      this._sync();
    });

    // controls
    this._toggle('set-minimap', 'minimap', () => this._applyHudBits());
    this._toggle('set-hints', 'hints', () => this._applyHudBits());
    $('set-side')
      ?.querySelectorAll('button')
      .forEach((b) =>
        b.addEventListener('click', () => {
          this.state.side = b.dataset.v;
          this._save();
          this._sync();
        })
      );
    if ($('set-aimdraw')) this._toggle('set-aimdraw', 'aimOnDraw', () => {});
  }

  _toggle(id, key, apply) {
    $(id).addEventListener('click', () => {
      this.state[key] = !this.state[key];
      apply();
      this._save();
      this._sync();
    });
  }

  // ---- apply ----------------------------------------------------------------

  _applyAll() {
    this._applyQuality();
    this._applyVolume();
    this._applyHudBits();
  }

  // `manual` = the player clicked a quality button. Only THEN is the render
  // scale pinned. Booting at the default 'ultra' used to pin it too, which
  // silently killed Game._adaptResolution -- the one-way safety valve that
  // drops a retina Mac from 2x to 1.5x when frames keep missing 50 fps. On a
  // 2800x1800 buffer the game is fill-bound, so the valve mattered most
  // exactly where it was disabled.
  _applyQuality(manual = false) {
    const q = QUALITY[this.state.quality] ?? QUALITY.ultra;
    const g = this.game;
    if (!g) return; // opened from the start menu — applied on Game attach()
    if (manual) g._perf.dropped = true; // manual choice wins over the auto-drop
    g.renderer.setPixelRatio(q.pixelRatio);
    g.composer?.setPixelRatio(q.pixelRatio);
    g.hq = q.hq;
    if (g.hq && !g.composer) g.composer = g._buildComposer();
    g.renderer.shadowMap.enabled = q.hq;
    g.renderer.shadowMap.needsUpdate = true;
  }

  _applyEngine() {
    if (!this.game) return;
    const s = this.game.sound;
    if (this.state.engine) {
      s.start();
      this._applyVolume();
    } else if (s.ctx) s.dispose();
  }

  _applyVolume() {
    this.game?.sound.setVolume(this.state.volume / 100);
  }

  // The N key, and anything else that turns the sound on or off outside this
  // window. Keeps the stored state, the audio and the toggle in step -- which
  // is the thing _sync() used to do by guessing, and got wrong.
  setEngine(on) {
    this.state.engine = !!on;
    this.state.soundChosen = true;
    this._applyEngine();
    this._save();
    this._sync();
  }

  _applyHudBits() {
    $('minimap').style.display = this.state.minimap ? '' : 'none';
    $('hud-controls').style.display = this.state.hints ? '' : 'none';
  }

  // ---- reflect state in the UI ----------------------------------------------

  _sync() {
    $('set-quality')
      .querySelectorAll('button')
      .forEach((b) => b.classList.toggle('active', b.dataset.v === this.state.quality));
    $('set-fullscreen').classList.toggle('on', !!document.fullscreenElement);
    // REFLECT, DO NOT REWRITE. This line used to be
    //   this.state.engine = !!this.game?.sound.ctx
    // "because the N key can flip it too" -- and _sync() runs from attach(),
    // BEFORE anything has started the audio, so it set engine back to false
    // every single time. Game._findGround then read that false and never
    // started the sound: the whole aeroplane was silent and no setting the
    // player could see said why. The N key keeps the state in step itself now
    // (Game.toggleEngineSound).
    $('set-engine').classList.toggle('on', !!this.state.engine);
    const vol = $('set-volume');
    vol.value = this.state.volume;
    vol.style.setProperty('--fill', `${this.state.volume}%`);
    $('set-vol-num').textContent = `${this.state.volume}%`;
    $('set-minimap').classList.toggle('on', this.state.minimap);
    $('set-hints').classList.toggle('on', this.state.hints);
    $('set-side')
      ?.querySelectorAll('button')
      .forEach((b) => b.classList.toggle('active', b.dataset.v === (this.state.side ?? 'right')));
    $('set-aimdraw')?.classList.toggle('on', this.state.aimOnDraw !== false);
  }
}
