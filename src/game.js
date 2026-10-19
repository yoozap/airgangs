import * as THREE from 'three';
import { Sky } from 'three/addons/objects/Sky.js';
import { RGBELoader } from 'three/addons/loaders/RGBELoader.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { N8AOPass } from 'n8ao';
import { CityMap } from './citymap.js';
import { MapUI } from './mapui.js';
import { EngineSound } from './sound.js';
import { SettingsUI } from './settings.js';
import { PlacementEditor } from './placement.js';
import { DevMode } from './devmode.js';
import { SmokeTrail } from './smoke.js';
import { MachineGun } from './guns.js';
import { RocketLauncher } from './rockets.js';
import { SkillBar, WeaponSlots } from './skillbar.js';
import { Pickups } from './pickups.js';
import { setHoodOpen } from './planes.js';
import { spinPropellers } from './propeller.js';
import { updateWind } from './wind.js';
import { Pistol } from './shooting.js';
import { GunAnim, PISTOL_SPEC, RIFLE_SPEC } from './gunanim.js';
import { Footsteps } from './footsteps.js';
import { BulletSim } from './ballistics.js';
import { ImpactFX } from './impactfx.js';
import { PaperTargets } from './papertargets.js';
import { RangeLanes } from './rangelane.js';

// Held down to aim on foot, alongside the right mouse button.
//
// NOT Cmd: Cmd+W closes the tab and Cmd+T/N open new ones, and the browser
// handles those above the page -- preventDefault cannot stop them, so holding
// Cmd to aim and pressing W to run shut the game down. Not Ctrl either: that is
// already "walk" for the personage. Option/Alt is free in both respects.
const AIM_KEYS = new Set(['AltLeft', 'AltRight']);

// shadow box half-size: tight on the ground, wider with altitude
const SHADOW_NEAR_M = 160;
const SHADOW_FAR_M = 700;
import { FireFX } from './fire.js';
import { BlastFX } from './blast.js';
import { GlassFX } from './glass.js';
import { hitConcrete, collapseRumble, samples } from './audiofx.js';
import { CrashSequence } from './crash.js';
import { FlightModel } from './aero.js';
import { PlaneBreakup } from './breakup.js';
import { MapBoundary } from './boundary.js';
import { Zones } from './zones.js';
import { CombatHUD } from './hud.js';
import { OnFoot } from './foot.js';
import { Cinema } from './cinema.js';

// ვაჟა-ფშაველას გამზირი, საბურთალო — spawn on the avenue near Delisi.
const SPAWN = { lat: 41.7252, lon: 44.7458 };
// Local frame: X = west, Z = north, Y = up.
// The avenue runs west-southwest from Delisi.
const SPAWN_YAW = -1.24;

// Realistic flight dynamics (aero.js): stalls, G-limits and top speed come
// from each plane's real mass/thrust/wing — these are just game-side limits.
const MAX_SPEED = 170;     // m/s — fallback only; FOV scaling reference
const WHEEL_HEIGHT = 1.7;  // m — plane origin above the street while rolling
const REVERSE_SPEED = 14;  // m/s max backing up on the ground (~50 km/h)
// The camera clamp (see _keepCameraOutside): how close it may be dollied in
// before it gives up, and how far it stays off whatever it found.
const CAM_MIN = 1.4;   // m
const CAM_SKIN = 0.45; // m
const _camSubject = new THREE.Vector3();
const _camDir = new THREE.Vector3();

const MAX_HEALTH = 1000;   // hull points — the HUD bar; 0 means destroyed
const MOUSE_SENS = 0.005; // rad per pixel of mouse movement (~0.29°)
const G = 9.81;

export class Game {
  constructor({ container, plane, def, ui, spawnLatLon, maxSpeed, settingsUI, character, resume, onSave, cityId }) {
    this.spawnLatLon = spawnLatLon ?? SPAWN;
    // Where this hero stopped last time (net.js) — position, heading, camera
    // and whether they were out of the plane on foot. Applied once the city
    // is on the ground; null means a fresh start at the city spawn.
    this.resume = resume ?? null;
    this.onSave = onSave ?? null; // hands a snapshot to the hero socket
    this.cityId = cityId ?? 'tbilisi';
    this._saveTimer = 0;
    this._savedMark = null; // last snapshot pushed, to skip identical ones
    this.container = container;
    this.plane = plane;
    this.def = def;
    this.ui = ui; // { speed, alt, loading, loadingText, crash, error, errorText, hud, attribution, hint }
    this.maxSpeed = maxSpeed ?? MAX_SPEED;
    this.halfSpan = plane.userData.halfSpan ?? 4.5;
    // Chase camera backs off for big airframes (C-17, RQ-180) so they fit in frame.
    this.camScale = Math.max(1, (plane.userData.size ?? 11) / 13);

    this.keys = new Set();
    this.speed = 0; // scalar: ground-roll speed, or |vel| in the air (HUD, FX)
    this.vel = new THREE.Vector3(); // world-space velocity — the real state
    this.rates = new THREE.Vector3(); // body rotation rates (pitch, yaw, roll)
    // Structural integrity per part: 1 intact, 0.65 damaged, 0 torn off.
    this.struct = { wingL: 1, wingR: 1, tail: 1, gear: 1 };
    this._gOverT = 0; // seconds spent past breaking G
    this._flutterT = 0; // seconds spent past VNE
    this.health = MAX_HEALTH;
    this.wingDmg = { left: false, right: false };
    this._smokeTimer = 0;
    this._wingHitCooldown = 0;
    this._hitCooldown = 0; // seconds between hull-damage ticks while scraping
    // Distance from the plane's origin (center) to the nose tip — the swept
    // wall check counts contact from here, so "touch" means the nose visually
    // meeting the facade, not an invisible wall meters ahead.
    this.noseLen = Math.max(3, (plane.userData.size ?? 11) * 0.35);
    this._prevPos = new THREE.Vector3();
    this.groundY = null;
    this.airborne = false;
    this.started = false;
    this.crashed = false;
    this.disposed = false;
    this.lastGroundDistance = 0;
    this.streetY = null;

    this.clock = new THREE.Clock();
    this.raycaster = new THREE.Raycaster();
    this.raycaster.firstHitOnly = true;

    this._setupScene();
    this._setupWorld();
    this._setupInput();
    this.mapui = new MapUI();
    this.zones = new Zones();
    this.mapui.zones = this.zones; // the map highlights the district you're in
    this.boundary = new MapBoundary(this.scene);
    this._pinBeams = new THREE.Group(); // gold light pillars at map pins
    this.scene.add(this._pinBeams);
    this.sound = new EngineSound();
    this.sound.setPlane(def);  // which engine this airframe has: piston or turbine
    // Reuse the menu's SettingsUI if one was passed in — a fresh instance per
    // Game would stack duplicate DOM listeners on every return to the menu.
    this.settings = settingsUI ?? new SettingsUI(this);
    if (settingsUI) this.settings.attach(this);
    this.smoke = new SmokeTrail(this.scene);
    this.placement = new PlacementEditor(this);
    this.dev = new DevMode(this); // ` — gizmo editor for registered props (src/devmode.js)
    this.skillbar = new SkillBar(this);
    // the personage's own eight slots — hidden until you climb out
    this.weapons = new WeaponSlots(this);
    // Worn gear by id (the belt, and whatever follows it). Not a weapon slot:
    // gear has no trigger, it just goes on and changes what the body can do --
    // a belt is where a holstered pistol lives.
    this.gear = new Map();
    this.pickups = new Pickups(this); // ground loot + the PUBG-style take card
    // Real bullets in flight, and everything they leave behind. One sim and one
    // impact layer for the whole game, so the pistol in your hand and the gun
    // under the plane mark the city the same way (see ballistics.js).
    // Real gun and impact recordings, fetched in the background. The game is
    // already past a dozen clicks by the time it gets here, so the AudioContext
    // is allowed to exist; until the bank lands, audiofx falls back to synthesis.
    samples.load();
    this.bullets = new BulletSim(this);
    this.paper = new PaperTargets(this);  // scoring faces on the range's cards
    this.lanes = new RangeLanes(this);    // and the carriers those cards ride on
    this.impacts = new ImpactFX(this);
    // A round arriving runs a lot of other people's code -- decals, glass,
    // building damage, the scoring faces on the range -- and it runs it from
    // inside the bullet sim's own step loop. One throw in any of it used to
    // abandon the rest of that frame's rounds mid-flight: they were never
    // stepped and never retired, so they piled up against the live cap and
    // every frame after did more work than the last. That is what a stutter
    // while shooting turned out to be. Impacts are now fenced off: a handler
    // that fails loses its own effect and nothing else, and says so once.
    this.bullets.onImpact = (info) => {
      try {
        this.impacts.resolve(info);
      } catch (e) {
        if (!this._impactFailed) {
          this._impactFailed = true;
          console.error('impact handler failed (further ones are silent):', e);
        }
      }
    };
    this.pistol = new Pistol(this);   // on-foot shooting, reticle and aim mode
    // The pistol animation off the Sketchfab viewmodel, which poses the hero's
    // own arms. Loaded in the background -- the game runs without it, the arms
    // just fall back to the IK poses until it lands.
    // One arms clip per kind of weapon in the hand; `gunAnim` is whichever the
    // held weapon wants (the pistol's by default), so everything that reads it
    // -- the reload, the shot, the draw -- follows the weapon.
    this.footsteps = new Footsteps(this);   // the hero's own feet, by gait and by ground
    this.gunAnims = { pistol: new GunAnim(this, PISTOL_SPEC), rifle: new GunAnim(this, RIFLE_SPEC) };
    for (const ga of Object.values(this.gunAnims)) ga.load();
    Object.defineProperty(this, 'gunAnim', {
      get: () => (this.foot?.held?.kind === 'rifle' ? this.gunAnims.rifle : this.gunAnims.pistol),
    });
    this.guns = new MachineGun(this);
    this.rockets = new RocketLauncher(this);
    this.combatHud = new CombatHUD(this);
    this.aero = new FlightModel(plane, def ?? { maxSpeed: this.maxSpeed, tw: 0.38 });
    this.breakup = new PlaneBreakup(this);
    // WoW-style personage: Enter climbs out of a parked plane and runs on foot.
    this.foot = new OnFoot(this, character);
    // The 🎬 button: a looping cinematic, shot like a film. It drives the hero
    // through the same roll, slide and trigger the keys drive, and takes the
    // camera and the clock off the game while it runs.
    this.cinema = new Cinema(this);

    this.resetPlane();
    this._onResize = () => this._resize();
    window.addEventListener('resize', this._onResize);
    this.renderer.setAnimationLoop(() => this._tick());
  }

  _setupScene() {
    // NO canvas MSAA. With the EffectComposer on (the default), the scene is
    // rendered into N8AO's own non-multisampled target and only the final
    // fullscreen quad ever touches the default framebuffer -- a quad has no
    // edges, so 4x MSAA antialiased NOTHING while still allocating a
    // multisampled colour+depth backbuffer (160 MB at 2800x1800) and
    // resolving it every frame. Measured: see tools/perf_msaa.mjs.
    // It DOES antialias in fast mode, where there is no composer and
    // renderer.render() draws the scene straight to the canvas -- so the saved
    // quality preset decides. (Switching quality mid-session does not move
    // this; a context attribute is fixed when the context is made.)
    // `?msaa` forces it on for an A/B.
    let fastMode = false;
    try {
      const q = JSON.parse(localStorage.getItem('tf-settings') || '{}').quality;
      fastMode = q === 'low' || q === 'med';
    } catch { /* no saved settings — the default is ultra, which uses the composer */ }
    const wantMSAA = fastMode || new URLSearchParams(location.search).has('msaa');
    this.renderer = new THREE.WebGLRenderer({ antialias: wantMSAA, powerPreference: 'high-performance' });
    // Full retina by default (max graphics). If lag is SUSTAINED, drop once to
    // 1.5× (still sharp) and stay there — one-way, so the scale never pumps
    // up/down (each switch reallocates every FBO = a visible stutter).
    this._resScale = Math.min(2, Math.max(1.5, window.devicePixelRatio));
    this._perf = { ema: 16, cool: 10, dropped: false };
    this.renderer.setPixelRatio(this._resScale);
    this.renderer.setSize(this.container.clientWidth, this.container.clientHeight);
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    // 0.95: at 1.05 the bright stucco/panel facades sat on the ACES shoulder —
    // whites clipped together and street scenes read as one pale wash
    this.renderer.toneMappingExposure = 0.95;
    this.renderer.shadowMap.enabled = true; // max graphics by default; T switches to fast mode
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap; // hard PCF read as pasted-on cutouts
    this.container.appendChild(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.scene.fog = new THREE.FogExp2(0xaec4d8, 0.00006); // was 8e-5: mid-range flats lost their window grids in haze

    this.camera = new THREE.PerspectiveCamera(
      62,
      this.container.clientWidth / this.container.clientHeight,
      1,
      60000
    );

    // Physical sky: late-morning sun from the southeast.
    const sky = new Sky();
    sky.scale.setScalar(450000);
    const su = sky.material.uniforms;
    su.turbidity.value = 6;
    su.rayleigh.value = 1.6;
    su.mieCoefficient.value = 0.004;
    su.mieDirectionalG.value = 0.8;
    const elev = THREE.MathUtils.degToRad(48);
    const azim = THREE.MathUtils.degToRad(135);
    this.sunDir = new THREE.Vector3().setFromSphericalCoords(1, Math.PI / 2 - elev, azim);
    su.sunPosition.value.copy(this.sunDir);

    // Bake the sky into an environment map (water reflections, ambient tint).
    const pmrem = new THREE.PMREMGenerator(this.renderer);
    const envScene = new THREE.Scene();
    envScene.add(sky);
    this.scene.environment = pmrem.fromScene(envScene).texture;
    this.scene.add(sky); // fromScene removed it from envScene's parent
    this.sky = sky; // must follow the camera — Batumi is 260 km from the origin

    // Upgrade the environment to a real HDR (Poly Haven) once it loads — richer,
    // photographically-lit reflections and ambient than the procedural sky.
    new RGBELoader().load('textures/env.hdr', (hdr) => {
      hdr.mapping = THREE.EquirectangularReflectionMapping;
      this.scene.environment = pmrem.fromEquirectangular(hdr).texture;
      hdr.dispose();
    });

    const sun = new THREE.DirectionalLight(0xfff0dc, 2.7);
    sun.position.copy(this.sunDir).multiplyScalar(2500);
    sun.castShadow = true;
    sun.shadow.intensity = 0.82; // let some ambient into the shadows
    // 4096 over a 700 m box ≈ 0.34 m/texel (was 1 m): kerbs, railings and
    // tunnel portals actually cast now, which grounds the roads to the city
    // 2048, not 4096. Measured on the real city: 4096 over a 700 m box is
    // 0.34 m per texel and costs 10.8 ms a frame; 2048 over a 160 m box is
    // 0.16 m per texel -- SHARPER -- and runs at 39.7 fps against 26.2. A
    // shadow map spent on empty ground is wasted twice: once in fill and once
    // in resolution.
    sun.shadow.mapSize.set(2048, 2048);
    const S = SHADOW_NEAR_M; // half-size now adapts to altitude, see _tick
    sun.shadow.camera.left = -S;
    sun.shadow.camera.right = S;
    sun.shadow.camera.top = S;
    sun.shadow.camera.bottom = -S;
    sun.shadow.camera.near = 1;
    sun.shadow.camera.far = 6000;
    sun.shadow.bias = -0.0004;
    sun.shadow.normalBias = 2;
    this.scene.add(sun);
    this.scene.add(sun.target);
    this.sun = sun;
    this.scene.add(new THREE.HemisphereLight(0xbdd8ff, 0x77664e, 2.0));

    // ---- effect lights: a POOL, created here and never added or removed ----
    // three bakes the light counts into every program's cache key, so a single
    // PointLight appearing mid-game invalidates the shader of EVERY material on
    // screen: measured 36 programs recompiled in one frame, a 0.8 s freeze, each
    // time a crash fireball lit up. They exist before renderer.compileAsync()
    // pre-warms the city, so the warm-up covers the real light count and nothing
    // ever recompiles. Borrow one with takeFxLight(); it idles at intensity 0,
    // parked far below the map. blast.js keeps its own pool for the same reason.
    this.fxLights = [];
    for (let i = 0; i < 3; i++) {
      const l = new THREE.PointLight(0xff8033, 0, 1, 1.8);
      l.position.set(0, -10000, 0);
      this.scene.add(l);
      this.fxLights.push(l);
    }
    this._fxLightCursor = 0;

    this.scene.add(this.plane);
    this.plane.traverse((o) => {
      if (o.isMesh) o.castShadow = true;
    });

    // Max graphics by default (user request): AO + bloom + shadows on.
    // T drops to fast mode on weaker machines.
    this.hq = true;
    this.composer = null;
    this._buildComposer = () => {
      const w = this.container.clientWidth;
      const h = this.container.clientHeight;
      const composer = new EffectComposer(this.renderer);
      // NO RenderPass here: N8AOPass REPLACES it (renders the scene itself —
      // see n8ao README). Having both rendered the whole city twice per frame.
      const ao = new N8AOPass(this.scene, this.camera, w, h);
      // 4 m, not 18: world-scale AO darkened whole street canyons instead of
      // creases — crease-scale AO is what seats objects on the ground
      ao.configuration.aoRadius = 4;
      ao.configuration.distanceFalloff = 1;
      ao.configuration.intensity = 2.2;
      ao.configuration.halfRes = true;
      // TRANSPARENCY-AWARE AO RENDERS THE WHOLE SCENE TWICE MORE.
      //
      // N8AO's `renderTransparency` walks the scene, hides everything opaque
      // and renders it again into a full-res half-float target -- once for
      // transparent-without-depth-write and once for with -- so that AO does
      // not darken glass. Measured over the centre of Tbilisi at 120 m, that
      // is **1,494 extra draw calls and 83M extra triangles every frame**:
      // 6,455 draws / 281M tris with it, 2,828 / 112M without. Draws halved,
      // triangles down 60%, for an effect nobody can see on 163 transparent
      // materials in a city of seventeen thousand buildings.
      //
      // (It measured nearly free the first time because the rig was sitting at
      // an empty corner of the map where the extra renders had nothing to
      // draw. Measure post-processing where the geometry IS.)
      ao.configuration.transparencyAware = false;
      composer.addPass(ao);
      // Bloom at QUARTER res. It is a soft glow (strength 0.14) that gets blurred
      // to mush anyway, so the extra pixels buy nothing -- measured 69.9 -> 75.1
      // fps for no visible difference.
      const bloom = new UnrealBloomPass(new THREE.Vector2(w / 4, h / 4), 0.14, 0.5, 0.9);
      const bloomSetSize = bloom.setSize.bind(bloom);
      bloom.setSize = (bw, bh) => bloomSetSize(bw / 4, bh / 4);
      composer.addPass(bloom);
      composer.addPass(new OutputPass());
      return composer;
    };
    this.composer = this._buildComposer(); // built now: HQ is the default

    // Red warning grid projected on the ground when flying dangerously low.
    this.warnGrid = new THREE.GridHelper(240, 24, 0xff1a10, 0xff1a10);
    this.warnGrid.material.transparent = true;
    this.warnGrid.material.opacity = 0;
    this.warnGrid.material.depthWrite = false;
    this.warnGrid.visible = false;
    this.scene.add(this.warnGrid);
  }

  _setupWorld() {
    this.fx = new FireFX(this.scene); // building fires + collapse smoke
    this.world = new CityMap(this.scene, this);
    this.world.fx = this.fx;
    const groundAt = (x, z) => this.world.heightAt(x, z);
    // warhead blasts (fireball + flash light + crater) and breaking glass
    this.blast = new BlastFX(this.scene, this.fx, groundAt);
    this.glass = new GlassFX(this.scene, groundAt);
    // CityMap blows panes out of its facade layers whenever a building
    // crumbles a stage or falls — turn that list into real breaking glass
    this.world.onWindowsBlown = (list) => this.glass.burstList(list, 1.3, 60);
    // secondary fireballs during a collapse
    this.world.onBlast = (x, y, z, r) => this.blast.explode({ x, y, z }, null, { r, marks: false });
    // collapses rattle the camera — strength falls off with distance
    this._shakeAmp = 0;
    this.world.onShake = (s, x, z) => {
      const d = Math.hypot(x - this.plane.position.x, z - this.plane.position.z);
      this._shakeAmp = Math.min(1.6, this._shakeAmp + s * THREE.MathUtils.clamp(1.15 - d / 550, 0, 1));
    };
    this.world
      .load(
        'tbilisi/city.json.gz',
        (msg) => {
          if (this.ui.loadingText) this.ui.loadingText.textContent = msg;
        },
        this.spawnLatLon
      )
      .catch((e) => {
        console.error(e);
        this._showError(
          'ვერ ჩაიტვირთა ქალაქის რუკა (public/tbilisi/city.json.gz). გაუშვი: python3 tools/build_map.py'
        );
      });
  }

  _setupInput() {
    this._onKeyDown = (e) => {
      if (e.code.startsWith('Arrow') || e.code === 'Space') e.preventDefault();
      // THE FILM TAKES THE KEYBOARD. Nothing is driveable while the cinematic
      // runs -- it is holding the keys itself -- so anything at all ends it.
      if (this.cinema?.active) {
        if (!e.repeat) this.cinema.stop();
        return;
      }
      // Keep the browser's own chords out of the game while it is running:
      // Cmd+1..9 jumps browser tabs and collides with the weapon slots, and
      // Option+letter would type accented characters. Reload is left alone on
      // purpose -- it is wanted far too often here.
      if ((e.metaKey || e.altKey) && this.started && e.code !== 'KeyR') e.preventDefault();
      // CMD, TAPPED ALONE, IS THE GENTLEMAN'S WALK (foot.js toggleStroll). The
      // toggle fires on the key coming UP, and only if nothing else was pressed
      // in between -- which is what makes it safe to hang a gait on this key at
      // all: Cmd+W closes the browser tab and Cmd+Q quits Chrome, neither of
      // which a page is allowed to stop, so Cmd must never be a key you HOLD
      // while you drive the hero. It is read here, above every early return
      // below, so a chord that goes somewhere else still counts as a chord.
      // Cmd+Tab macOS takes before the page sees the Tab -- but it takes the
      // focus with it, and a blur is not a key-up, so that toggles nothing.
      this._metaSolo = e.code === 'MetaLeft' || e.code === 'MetaRight'
        ? (e.repeat ? this._metaSolo : true)
        : false;
      // Placement editor swallows movement keys while open.
      if (e.code === 'KeyP') {
        this.placement.toggle();
        return;
      }
      if (this.placement.active && this.placement.handleKey(e.code, e.shiftKey)) return;
      // DEV MODE: ` opens the gizmo editor; while it is open it gets first
      // call on Esc (deselect, then close) and [ ] (cycle the props).
      if (e.code === 'Backquote') {
        if (!e.repeat) this.dev.toggle();
        return;
      }
      if (this.dev.active && this.dev.handleKey(e)) return;
      if (this.foot?.active) {
        if (this.weapons.selectByCode(e.code)) return; // 1–8 pick a weapon slot
      } else if (this.skillbar.selectByCode(e.code)) return; // 1–0 pick a skill
      if (e.code === 'Enter' || e.code === 'NumpadEnter') {
        this.foot.toggle(); // climb out of / back into the plane
        return;
      }
      this.keys.add(e.code);
      // N mutes and unmutes. Through the settings, so the stored preference,
      // the audio and the toggle in the window all move together -- flipping
      // the AudioContext behind its back is what let them drift apart.
      if (e.code === 'KeyN') this.settings?.setEngine(!this.sound.ctx);
      // R used to re-park the plane here. It teleported the machine to the
      // city spawn, DRAGGED the player out of whatever they were doing into the
      // cockpit, and saved that as the hero's spot -- so one stray keypress
      // moved you to the airport and every later reload put you back there.
      // The crash screen's "კიდევ ერთხელ" button is the only way to reset now.
      if (AIM_KEYS.has(e.code) && this.foot?.active && this.foot.held) {
        // TOGGLE, not hold: one press goes to aim, the next comes out of it.
        // `e.repeat` matters -- a held key fires keydown over and over, which
        // would flip the mode dozens of times a second.
        if (!e.repeat) this.pistol.toggleAim();
        return;
      }
      // C — crouch, GTA San Andreas's duck key. Toggle, and e.repeat matters:
      // a held key would flap it dozens of times a second.
      if (e.code === 'KeyC' && this.foot?.active) {
        if (!e.repeat) this.foot.toggleCrouch();
        return;
      }
      if (e.code === 'KeyR' && this.foot?.active && this.foot.held) {
        if (!e.repeat) this.pistol.reload();
        return;
      }
      // V — COVER, GTA's: back to the nearest wall, and again to leave it
      // (foot.js toggleCover). A toggle, and e.repeat matters here too.
      if (e.code === 'KeyV' && this.foot?.active) {
        if (!e.repeat) this.foot.toggleCover();
        return;
      }
      // F SENDS THE TARGET OUT, G BRINGS IT IN, but only while standing at a
      // firing point -- everywhere else F is still "pick that weapon up".
      if (this.foot?.active && this.lanes.key(e.code, true)) return;
      if (e.code === 'KeyF' && this.foot?.active) this.pickups.take();
      if (e.code === 'KeyM') this.mapui.toggle();
      if (e.code === 'KeyH' && !e.repeat) this.goAirport();   // 🛬 same as the HUD button
      if (e.code === 'Escape') {
        // Esc peels UI layers: settings → map → open settings
        if (this.settings.visible) this.settings.close();
        else if (this.mapui.bigVisible) this.mapui.toggle();
        else this.settings.open();
      }
      if (e.code === 'KeyT') {
        this.hq = !this.hq;
        if (this.hq && !this.composer) this.composer = this._buildComposer();
        this.renderer.shadowMap.enabled = this.hq;
        this.renderer.shadowMap.needsUpdate = true;
        this._setHint(this.hq ? 'HQ ✓ (ჩრდილები+AO) — T გამორთვა' : 'სწრაფი რეჟიმი — T ლამაზი გრაფიკა');
      }
    };
    this._onKeyUp = (e) => {
      this.keys.delete(e.code);
      this.lanes?.key(e.code, false);   // F/G: the carrier stops when you do
      if (e.code === 'MetaLeft' || e.code === 'MetaRight') {
        const solo = this._metaSolo;
        this._metaSolo = false;
        if (solo && this.foot?.active) this.foot.toggleStroll();
      }
    };
    // Aim is a toggle, so it survives a keyup; but if the window goes away
    // entirely, drop it rather than leave the player zoomed in on return.
    this._onBlur = () => {
      this.pistol?.setAiming(false);
      this.foot?.coverBlind?.(false);
      this.lanes?.releaseAll();     // a blur is a key-up for the carrier

      this._metaSolo = false;   // a blur is not a key-up: it toggles nothing
    };
    window.addEventListener('keydown', this._onKeyDown);
    window.addEventListener('keyup', this._onKeyUp);
    window.addEventListener('blur', this._onBlur);

    // Drag-to-look: orbit the chase camera around the plane; releasing the
    // button lets it ease back behind the plane.
    this.look = { dragging: false, yaw: 0, pitch: 0 };
    // Where the mouse is, in canvas pixels and in NDC — the aim, while a
    // weapon is out. Starts dead centre so a freshly drawn gun points ahead.
    this.aim = { x: 0, y: 0, ndcX: 0, ndcY: 0 };
    const canvas = this.renderer.domElement;
    // Pointer lock is what makes mouse-look feel like a shooter instead of a
    // drag: the cursor disappears, movement keeps coming however far you push,
    // and `unadjustedMovement` asks the OS for raw counts with no acceleration.
    // Esc releases it (the browser's own rule) and a click grabs it again.
    this._lockPointer = () => {
      if (document.pointerLockElement === canvas) return;
      if (this.dev?.active) return; // the editor keeps the cursor
      try {
        const r = canvas.requestPointerLock({ unadjustedMovement: true });
        if (r?.catch) r.catch(() => canvas.requestPointerLock());
      } catch {
        try { canvas.requestPointerLock(); } catch { /* denied — loose mouse still works */ }
      }
    };
    this._unlockPointer = () => {
      if (document.pointerLockElement === canvas) document.exitPointerLock();
    };
    this.centreAim = () => {
      const r = canvas.getBoundingClientRect();
      this.aim.x = r.width / 2;
      this.aim.y = r.height / 2;
      this.aim.ndcX = 0;
      this.aim.ndcY = 0;
      this.pistol?.moveReticle(this.aim.x, this.aim.y);
    };
    this._onPointerDown = (e) => {
      if (this.cinema?.active) return;   // the film is not interactive
      if (this.dev?.hot) return;         // a press on the dev gizmo is the gizmo's
      // On foot with a weapon drawn the mouse is GTA's: left fires, right aims.
      // Unarmed it stays drag-to-orbit, so walking around is unchanged.
      const armed = this.foot?.active && this.foot.held;
      if (armed && e.button === 2) {
        this.pistol.toggleAim();   // toggle, same as the key
        return;
      }
      if (armed && e.button === 0) {
        this._lockPointer(); // a click is a gesture: grab the mouse back
        // BEHIND COVER the trigger is the blind fire: the arm goes round the
        // corner (or up over the wall) and shoots unsighted for as long as the
        // button is down (foot.js coverBlind). Leaning out, it is a shot.
        if (this.foot._cover && !this.foot.coverCanFire()) {
          // ...and a pull while still leaning out is kept until the gun is clear
          if (this.pistol.aiming) this.foot.coverShot();
          else this.foot.coverBlind(true);
          return;
        }
        this.pistol.trigger(true);   // held down, an automatic keeps going
        this.pistol.fire();
        return;
      }
      if (e.button !== 0) return;
      this.look.dragging = true;
      canvas.setPointerCapture(e.pointerId);
    };
    this._onContextMenu = (e) => {
      if (this.foot?.active && this.foot.held) e.preventDefault(); // right-drag aims
    };
    this._onPointerMove = (e) => {
      // COUNTER-STRIKE AIMING with a weapon out: the crosshair is nailed to the
      // centre of the screen and the MOVEMENT of the mouse swings the view --
      // left, right, up, down -- with the personage turning under it. The
      // pointer is locked (see _lockPointer), so the mouse never runs out of
      // screen the way a loose cursor does. Bare handed it is still drag to
      // orbit, so walking around is unchanged.
      const armed = this.foot?.active && this.foot.held;
      if (!armed && !this.look.dragging) return;
      // the dev editor needs a loose mouse: only a drag orbits while it is open
      if (this.dev?.active && !this.look.dragging) return;
      // Esc drops the lock and opens settings: don't swing the view while the
      // player is crossing the screen to click something.
      if (this.settings?.visible || this.mapui?.bigVisible) return;
      this.look.yaw -= e.movementX * MOUSE_SENS;
      // Vertical, with a weapon out: push the mouse FORWARD and the aim goes
      // UP, pull it back and the aim goes down — Counter-Strike's way round,
      // not inverted. The plane's drag-orbit keeps its old sense, so flying is
      // unchanged.
      const dy = e.movementY * MOUSE_SENS;
      this.look.pitch = THREE.MathUtils.clamp(this.look.pitch + (armed ? dy : -dy), -1.2, 1.2);
    };
    this._onPointerUp = () => {
      this.look.dragging = false;
      this.pistol?.trigger(false);
      this.foot?.coverBlind?.(false);
    };
    canvas.addEventListener('pointerdown', this._onPointerDown);
    canvas.addEventListener('pointermove', this._onPointerMove);
    canvas.addEventListener('pointerup', this._onPointerUp);
    canvas.addEventListener('contextmenu', this._onContextMenu);
    canvas.addEventListener('pointercancel', this._onPointerUp);
  }

  // HOW MUCH ENGINE IS ACTUALLY TURNING, 0..1.
  //
  // Idle revs make a little thrust -- a real aeroplane taxis on idle and
  // creeps forward on the brakes -- and the rest comes in with the revs. This
  // is why a cold start is something you wait through instead of a key you
  // press, and why a turbine's spool lag is a thing you fly around.
  //
  // A plane whose engine has been shut down does not stop flying: it becomes a
  // glider, which aero.js already models perfectly well with zero thrust.
  powerFrac() {
    const cfg = this.sound?.rpm ?? 0;
    if (!this.sound || this.sound.state === 'off') return 0;
    // revs below idle are the starter dragging it round: no useful thrust yet
    return THREE.MathUtils.clamp((cfg - 0.08) / 0.92, 0, 1);
  }

  // Contact. Called when the pilot is in the seat, and when the game starts
  // with them already there.
  igniteEngine() {
    if (this.crashed) return;
    this.sound.setPlane(this.def);
    this.sound.ignite();
  }

  stopEngine() { this.sound.shutdown(); }

  // IS THE SEAT EMPTY? Every "start the engine" below asks this instead of
  // reading foot.active, because foot.active lies for the first second of a
  // session: a hero saved on the apron is OUT from the first frame, but the
  // body is a download and does not stand up until it lands, so the synchronous
  // ignites ran first and left the aeroplane turning over beside a pilot who
  // was walking away from it.
  seatEmpty() {
    return !!(this.foot?.active || this._resumeOnFoot);
  }

  resetPlane() {
    if (this.foot?.active) this.foot._board(); // R teleports the plane — take the pilot along
    this.crashed = false;
    this.airborne = false;
    this.crash?.dispose();
    this.crash = null;
    this._wastedShown = false;
    this.container.classList.remove('wasted');
    this.ui.crash.classList.add('hidden');
    this.speed = 0;
    this.vel.set(0, 0, 0);
    this.rates.set(0, 0, 0);
    this.breakup.restore(); // wings/tail back on, wreckage cleaned up
    this.struct.wingL = this.struct.wingR = this.struct.tail = this.struct.gear = 1;
    this._gOverT = 0;
    this._flutterT = 0;
    this.health = MAX_HEALTH;
    this._wingHitCooldown = 0;
    this._hitCooldown = 0;
    this._repairWings();
    this._updateHealthBar();
    const sp = this.world?.spawn ?? { x: 0, z: 0, yaw: SPAWN_YAW };
    const y = this.groundY !== null ? this.groundY + WHEEL_HEIGHT : 1200;
    this.streetY = this.groundY;
    this.plane.position.set(sp.x, y, sp.z);
    this.plane.quaternion.setFromEuler(new THREE.Euler(0, sp.yaw, 0));
    this._setHint(this._takeoffHint());
    this._snapCamera();
    // CONTACT AGAIN. The crash cut the engine (_explode -> sound.kill), and a
    // pilot sitting in a fresh aeroplane is a pilot who starts it. Without
    // this, a crash restart handed back a machine that would not move and gave
    // no reason why.
    if (!this.seatEmpty()) this.igniteEngine();
    if (this.started) this.saveState(true, 'plane reset'); // crash restart / fresh hero
  }

  // PARK THE AEROPLANE INSIDE THE WAREHOUSE.
  //
  // The 🛬 teleport puts the hero in the shed by the weapon benches, and the
  // user wants the machine to come with them: "when i click teleport in
  // warehouse i need my plane must be in warhouse too". The shed is 48 x 139 m
  // with an open 24.8 x 9.15 m bay at each gable end, so most of the hangar
  // fits through the door and stands on the slab with room to spare.
  //
  // Returns false when it does not fit -- an An-225 is 88 m across a 24.8 m
  // doorway -- and the caller falls back to the stand outside. Lying about it
  // would mean a wing through a wall.
  _parkInShed() {
    let shed = null;
    this.scene.traverse((o) => { if (!shed && o.userData?.shed) shed = o; });
    if (!shed) return false;                 // the airport has not built yet
    const d = shed.userData.shed;

    // Does it go through the bay? Off the AIRFRAME's own measurements
    // (src/planes.js records them when it builds the gameplay wrapper), not a
    // fresh Box3 of `this.plane`: by now the wrapper also carries the box shell
    // you climb on, the cockpit kit and the pilot, and a Box3 over that lot
    // reports a Spitfire as 276 m across -- which refused every aeroplane.
    const u = this.plane.userData;
    const span = Math.max(u.halfSpan * 2, u.size ?? 0);
    const tall = u.height ?? 0;
    if (!span || span > d.doorHalfW * 2 - 2 || tall > d.doorH - 0.6) return false;

    shed.updateMatrixWorld(true);
    const at = shed.localToWorld(d.park.clone());
    // The slab is a collidable, so the usual ground probe finds its top -- but
    // probe from INSIDE the shed (just under the roof), or the ray starting
    // over the building reads the roof as the floor.
    const slab = shed.getWorldPosition(new THREE.Vector3()).y;
    const gy = this._groundAt(at.x, at.z, slab + 1.5, 2) ?? slab;

    this.airborne = false;
    this.speed = 0;
    this.vel.set(0, 0, 0);
    this.rates.set(0, 0, 0);
    this.plane.position.set(at.x, gy + this._wheelH(), at.z);
    // Nose along the shed's own -Z, out through the near bay. The plane's nose
    // is its -Z too, so the heading is simply the shed's.
    const yaw = new THREE.Euler().setFromQuaternion(
      shed.getWorldQuaternion(new THREE.Quaternion()), 'YXZ').y;
    this.plane.quaternion.setFromEuler(new THREE.Euler(0, yaw + d.parkYawLocal, 0));
    this.groundY = gy;
    this.streetY = gy;
    this._prevPos.copy(this.plane.position);
    return true;
  }

  // 🛬 BACK TO THE AIRPORT, from anywhere (the HUD button, or H). On foot the
  // hero is stood in the warehouse by the weapon benches, facing them; in the
  // cockpit the plane is parked back on its stand. Either way the plane ends
  // up on its stand unless it is already parked near it, so there is always a
  // machine to fly from. Saved at once -- this IS the hero's spot now.
  goAirport() {
    if (!this.started || this.crashed || this.disposed) return false;
    if (this.cinema?.active) this.cinema.stop();
    const sp = this.world?.spawn ?? { x: 0, z: 0, yaw: SPAWN_YAW };
    const onFoot = !!this.foot?.active;
    // The aeroplane ALWAYS comes along now -- into the shed if it fits through
    // the bay, onto its stand outside if it does not. It used to be left where
    // it was whenever the hero was already on foot near the stand, which is
    // how you could teleport to the warehouse and find no machine in it.
    const inside = this._parkInShed();
    if (!inside) {
      const h = this.world.heightAt(sp.x, sp.z);
      const gy = this._groundAt(sp.x, sp.z, (h ?? this.plane.position.y) + 1, 3) ?? this.groundY;
      this.airborne = false;
      this.speed = 0;
      this.vel.set(0, 0, 0);
      this.plane.position.set(sp.x, gy + this._wheelH(), sp.z);
      this.plane.quaternion.setFromEuler(new THREE.Euler(0, sp.yaw ?? SPAWN_YAW, 0));
      this.groundY = gy;
      this.streetY = gy;
      this._prevPos.copy(this.plane.position);
    }
    if (!onFoot) {
      this._setHint(this._takeoffHint());
      this._snapCamera();
    }
    if (onFoot && this.foot.rig) {
      // the warehouse's first bench (the pistols), from the inside
      let wh = null, table = null;
      this.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
      if (wh) wh.traverse((o) => { if (!table && o.name === 'warehouse_table') table = o; });
      if (table) {
        table.updateWorldMatrix(true, false);
        const tp = new THREE.Vector3().setFromMatrixPosition(table.matrixWorld);
        // the benches run along the -X wall of the warehouse: inside is its +X
        const inward = new THREE.Vector3(1, 0, 0)
          .applyQuaternion(wh.getWorldQuaternion(new THREE.Quaternion())).setY(0).normalize();
        const px = tp.x + inward.x * 2.4, pz = tp.z + inward.z * 2.4;
        const gy = this._groundAt(px, pz, tp.y + 1.5, 3);
        this.pistol?.setAiming(false);
        this.foot._stand(px, (gy ?? tp.y) + 0.02, pz, Math.atan2(inward.x, inward.z));
      } else {
        // the warehouse is not built yet: beside the plane on its stand
        this.foot.active = true;
        this.foot._exitPlane(true); // teleported to the airport: no climb-down
      }
    }
    // Say where the machine actually is: inside with you, or out on the stand
    // because it is too big for the doorway.
    this._setHint('🛬 აეროპორტი — '
      + (onFoot ? 'საწყობში ხარ, იარაღების მაგიდებთან' : 'თვითმფრინავში ხარ')
      + (inside ? ' · თვითმფრინავი საწყობშია'
        : ' · თვითმფრინავი სადგომზეა (კარში ვერ ეტევა)'));
    this.saveState(true, 'airport');
    return true;
  }

  // ---- per-hero spawn place -------------------------------------------------
  // Everything the world needs to put this personage back exactly as it was.
  // Snapshots ride the hero socket (net.js → server /ws) every few seconds.
  snapshot() {
    if (!this.started || this.crashed) return null; // a wreck is no spawn place
    const r = (v) => Math.round(v * 100) / 100;
    const p = this.plane.position;
    const q = this.plane.quaternion;
    const c = this.camera.position;
    const onFoot = !!this.foot?.active;
    return {
      pos: [r(p.x), r(p.y), r(p.z)],
      quat: [r(q.x), r(q.y), r(q.z), r(q.w)],
      vel: [r(this.vel.x), r(this.vel.y), r(this.vel.z)],
      cam: [r(c.x), r(c.y), r(c.z)],
      airborne: this.airborne,
      speed: r(this.speed),
      health: Math.round(this.health),
      onFoot,
      foot: onFoot ? [r(this.foot.pos.x), r(this.foot.pos.y), r(this.foot.pos.z)] : undefined,
      footYaw: onFoot ? r(this.foot.yaw) : undefined,
      plane: this.def?.id,
      city: this.cityId,
      ...this._loadout(),                        // guns + gear: what it OWNS
      place: this.zones?.current?.name ?? undefined, // district, for the roster screen
    };
  }

  // WHAT THIS PERSONAGE OWNS — just the ids and which slot they sit in; the
  // model, name and picture are looked up from WEAPON_DEFS on the way back in,
  // so the saved state stays tiny.
  //
  // The kit is read through here rather than straight off the weapon bar
  // because the bar is EMPTY for the first seconds of a session: restoring a
  // weapon means downloading its model (the AKM is 3.6 MB), and the 2.5 s
  // autosave used to fire into that window and write `guns: []` over the real
  // loadout -- the hero lost everything it owned just by logging in on a slow
  // load. Anything the resume has not finished loading, or whose model failed
  // to arrive at all, is still OWNED and is reported as such; a slot that has
  // since been filled for real always wins over the pending entry.
  _loadout() {
    const guns = [];
    const taken = new Set();
    (this.weapons?.carried ?? []).forEach((w, i) => {
      if (!w?.id || w.kind === 'gear') return;
      guns.push({ s: i, id: w.id });
      taken.add(i);
    });
    for (const g of this._ownedPending ?? []) {
      if (!taken.has(g.s)) { guns.push(g); taken.add(g.s); }
    }
    guns.sort((a, b) => a.s - b.s);
    const gear = new Set(this.gear?.keys() ?? []);
    for (const id of this._gearPending ?? []) gear.add(id);
    return { guns, gear: [...gear] };
  }

  // Push the snapshot up. Quiet by default: nothing is sent while the hero
  // stands still in the same state, so parking the plane costs no traffic.
  saveState(force = false, why = 'autosave') {
    if (!this.onSave || this._resuming) return;
    // The cinematic stands the hero somewhere he never walked, in a body that is
    // not his, holding a pistol that is a prop. None of that is his spot.
    if (this.cinema?.active) return;
    // The hero had a saved spot and we could not put it back (the body never
    // loaded). We are sitting in the plane, which is NOT where the player left
    // the personage -- so refuse to autosave over the stored spot. Only a
    // deliberate act (climbing out, boarding, R, dying) may replace it.
    if (this._resumeFailed && !force) return;
    if (this._resumeFailed) this._resumeFailed = false;
    const snap = this.snapshot();
    if (!snap) {
      // A session that never got going has nothing to say about this hero --
      // and must not speak for it. Backing out of the loading screen used to
      // land here and DELETE the stored row, spot and weapons together.
      if (!this.started) return;
      // Dead: the spawn place goes, so the next flight starts at the city
      // spawn -- but WHAT THE PERSONAGE OWNS SURVIVES ITS DEATH. Wiping the
      // whole row here is why a crash followed by a reload left the hero with
      // empty slots: the guns were passengers on the spot. They are saved on
      // their own now, with no position, and come back on the next login.
      const kit = this._loadout();
      const mark = `dead|${kit.guns.map((g) => `${g.s}:${g.id}`).join(',')}|${kit.gear.join(',')}`;
      if (this._savedMark !== mark) {
        this._savedMark = mark;
        this.onSave(kit.guns.length || kit.gear.length
          ? { ...kit, city: this.cityId }
          : null);
      }
      return;
    }
    const ref = snap.onFoot ? snap.foot : snap.pos;
    const mark = `${snap.onFoot ? 'f' : 'p'}${snap.airborne ? 'a' : 'g'}`
      + `${Math.round(ref[0])}_${Math.round(ref[1])}_${Math.round(ref[2])}_${snap.health}`
      + `|${snap.guns.map((g) => `${g.s}:${g.id}`).join(',')}`  // picking a gun up IS a change
      + `|${snap.gear.join(',')}`;                               // and so is putting a belt on
    if (!force && mark === this._savedMark) return;
    // Say out loud what is being WRITTEN whenever the mode flips or the save is
    // deliberate (climbing out, boarding, R, leaving the page). Restores were
    // already logged; without this half you cannot tell a bad save from a bad
    // restore, and they look identical from the player's seat.
    const wasOnFoot = this._savedMark && this._savedMark[0] === 'f';
    if (force || wasOnFoot !== snap.onFoot) {
      const at = (snap.onFoot ? snap.foot : snap.pos).map((v) => Math.round(v)).join(', ');
      console.log(`hero spot: SAVING ${snap.onFoot ? 'ON FOOT' : 'in the plane'} at ${at}`
        + ` [${why}]`);
    }
    this._savedMark = mark;
    this.onSave(snap);
  }

  // Put the hero back exactly where it left off. Called once, the moment the
  // city is walkable — before that there is no ground under the saved spot.
  // Returns false when there is nothing to restore (fresh hero, other city),
  // and the caller falls back to the normal city spawn.
  _applyResume() {
    // `resume` may be a getter. The city takes ~13 s to build, and the hero
    // socket usually answers inside that window -- reading the spot NOW rather
    // than at startFlight() time picks up a snapshot that arrived from the
    // server after the menu, instead of falling back to the city spawn.
    const s = typeof this.resume === 'function' ? this.resume() : this.resume;
    this.resume = null;
    const age = s?.at ? `${Math.round((Date.now() - s.at) / 1000)}s old` : 'no timestamp';
    // THE KIT COMES BACK EVEN WHEN THE SPOT DOES NOT. A personage carries its
    // guns across death, across maps and across a spot that could not be used
    // -- so the loadout is put back FIRST, before any of the reasons to give
    // up on the position below.
    this._restoreLoadout(s);
    if (!Array.isArray(s?.pos) || s.pos.length < 3) {
      console.log(`hero spot: none stored — starting at the ${this.cityId} spawn`);
      return false;
    }
    if (s.city && s.city !== this.cityId) {
      console.log(`hero spot: stored on "${s.city}", playing "${this.cityId}" — spawn`);
      return false; // saved on another map
    }
    console.log(`hero spot: restoring ${s.onFoot ? 'ON FOOT' : 'in the plane'}`
      + ` at ${(s.onFoot ? s.foot : s.pos).map((v) => Math.round(v)).join(', ')} (${age})`);
    this._resuming = true; // the restore itself must not overwrite the save
    // ...AND THE SEAT IS ALREADY EMPTY if that is where the hero was left.
    // Known before resetPlane() so nothing below ever lights the engine.
    this._resumeOnFoot = !!(s.onFoot && Array.isArray(s.foot) && s.foot.length >= 3);
    this.resetPlane(); // clean airframe and full tank, then move it back
    const [px, py, pz] = s.pos;
    this.plane.position.set(px, py, pz);
    if (Array.isArray(s.quat) && s.quat.length === 4) this.plane.quaternion.fromArray(s.quat);
    const gy = this._groundAt(px, pz, py + 2);
    if (s.airborne) {
      // Mid-flight logout: the hero drops back into the air still flying, at
      // the same speed and heading — otherwise you'd wake up in a stall.
      this.airborne = true;
      this.vel.fromArray(Array.isArray(s.vel) && s.vel.length >= 3 ? s.vel : [0, 0, 0]);
      this.speed = this.vel.length();
      this.groundY = gy ?? this.groundY;
      this.streetY = gy;
      this.lastGroundDistance = gy !== null ? Math.max(0, py - gy) : 100;
    } else {
      // Parked: re-seat the wheels on whatever it was standing on (street,
      // runway, or a roof it landed on).
      this.airborne = false;
      this.speed = 0;
      this.vel.set(0, 0, 0);
      if (gy !== null) {
        this.plane.position.y = gy + this._wheelH();
        this.groundY = gy;
        this.streetY = gy;
      }
    }
    if (Number.isFinite(s.health)) {
      this.health = THREE.MathUtils.clamp(s.health, 1, MAX_HEALTH);
      this._updateHealthBar();
    }
    this._prevPos.copy(this.plane.position);
    this._snapCamera();
    // The camera itself is part of the spot — same angle, same distance. It
    // goes back last, after the personage is on its feet (standing up snaps
    // the camera behind it), and waits for the body to finish loading.
    const restoreCam = () => {
      if (this.disposed || !Array.isArray(s.cam) || s.cam.length < 3) return;
      this.camera.position.fromArray(s.cam);
      this.camera.lookAt(this.foot?.active ? this.foot.pos : this.plane.position);
    };
    // Standing outside the plane when the session ended: climb back out, on
    // the very spot the personage was standing.
    if (s.onFoot && Array.isArray(s.foot) && s.foot.length >= 3) {
      // foot.resume() is ASYNC -- it waits for the personage body to load.
      // `_resuming` has to stay up for that whole window: the 2.5 s autosave
      // runs on the very first frames, and a save taken before the hero is on
      // its feet writes an onFoot:false snapshot of the PARKED PLANE over the
      // real spot. That is how walking off and reloading put you back in the
      // cockpit at the airport.
      this.foot.resume(s.foot, s.footYaw ?? 0)
        .then(restoreCam)
        .catch((e) => console.error('on-foot resume threw:', e))
        .then(() => {
          this._resuming = false;
          this._resumeOnFoot = false;
          this._savedMark = null;
          if (this.foot.active) return;
          this._resumeFailed = true;
          console.error('hero was saved on foot but the body never stood up — '
            + 'the stored spot is frozen so it is not overwritten');
        });
    } else {
      restoreCam();
      this._resuming = false;
      this._savedMark = null;
    }
    return true;
  }

  // Hand the personage back everything it owns: the weapons into their slots,
  // the gear onto the body. Both are downloads, so this runs in the
  // background -- and for as long as it does, `_ownedPending` keeps the saved
  // list ALIVE in every snapshot, so an autosave landing mid-load cannot
  // write an empty loadout over it. Whatever never arrives (a 404, a dropped
  // connection) stays owned too and is tried again on the next login, rather
  // than being quietly forgotten because one request failed.
  _restoreLoadout(s) {
    const guns = (Array.isArray(s?.guns) ? s.guns : [])
      .filter((g) => g && typeof g.id === 'string')
      .map((g) => ({ s: Number.isInteger(g.s) ? g.s : 0, id: g.id }));
    const gear = (Array.isArray(s?.gear) ? s.gear : [])
      .filter((id) => typeof id === 'string' && id);
    if (!guns.length && !gear.length) return;
    this._ownedPending = guns;
    this._gearPending = gear;
    this.pickups?.restore(guns, gear)
      .then(({ guns: lostGuns = [], gear: lostGear = [] } = {}) => {
        this._ownedPending = lostGuns;
        this._gearPending = lostGear;
      })
      .catch((e) => {
        // The kit is still the hero's — keep the saved list as it was.
        console.error('loadout restore threw:', e);
      });
  }

  // Put whoever is active back on whatever is under them NOW. Only ever lifts
  // — nothing is dropped — so a hero standing on real ground is untouched.
  _reseatOnScenery() {
    if (this.disposed || !this.started || this.crashed) return;
    if (this.foot?.active && this.foot.rig) {
      const p = this.foot.pos;
      const gy = this._groundAt(p.x, p.z, p.y + 2.5, 2.5, this.foot._planeTargets?.());
      if (gy !== null && gy > p.y + 0.05) {
        p.y = gy + 0.02;
        this.foot.vy = 0;
        this.foot.grounded = true;
      }
      return;
    }
    if (!this.airborne) {
      const p = this.plane.position;
      const gy = this._groundAt(p.x, p.z, p.y + 2.5, 2.5);
      if (gy !== null && gy + this._wheelH() > p.y + 0.05) {
        p.y = gy + this._wheelH();
        this.groundY = gy;
        this.streetY = gy;
      }
    }
  }

  // The real rotation speed of the selected plane, so the player knows when
  // the machine will actually fly (an F-104 needs ~320 km/h, a Spitfire ~150).
  _takeoffHint() {
    return `W ან Shift — გაზი · S — უკუსვლა · ${Math.round(this.aero.vr * 3.6)}+ კმ/სთ-ზე ↑ — აფრენა ✈ · Q/E — მართვა`;
  }

  // THE CAMERA NEVER GOES INSIDE ANYTHING. Every camera in this game is a
  // point a few metres behind whatever it is watching -- the plane's chase,
  // the on-foot chase, the cover spring, the climb, the boarding shots -- and
  // any of them can end up with a wall, a wing or a whole fuselage between it
  // and its subject. The user put it plainly: "my camera never must go inside
  // of any item man, this is ugly", with a screenshot taken from inside the
  // aeroplane's own skin, reading its decals backwards.
  //
  // One clamp at the END of the frame catches all of them, because it works on
  // the finished camera: cast from the subject out along the view ray and stop
  // short of the first surface. Moving the camera along THAT ray is a dolly --
  // the subject stays where it was in frame, the view just gets closer.
  //
  // The aeroplane's own shell is tested even when it is not in the collision
  // set. Its faces point outwards, so a ray that starts INSIDE it (the camera
  // watching a seated pilot) passes straight out and nothing is clamped --
  // which is exactly right, and is why this cannot shove the flying camera
  // into the cockpit.
  _keepCameraOutside() {
    if (!this.started || this.cinema?.active) return;
    const subject = _camSubject;
    if (this.foot?.active) subject.set(this.foot.pos.x, this.foot.pos.y + 1.4, this.foot.pos.z);
    else subject.copy(this.plane.position);
    const dir = _camDir.subVectors(this.camera.position, subject);
    const len = dir.length();
    if (len < CAM_MIN) return;
    dir.divideScalar(len);
    const ray = (this._camRay ??= new THREE.Raycaster());
    ray.set(subject, dir);
    ray.near = 0;
    ray.far = len;
    const list = this.world.collidablesNear
      ? this.world.collidablesNear(subject.x, subject.z)
      : (this.world.collidables ?? []);
    let hit = ray.intersectObjects(list, true)[0];
    // The aeroplane's own shell only matters when the hero is OUT of it and
    // the machine is between the two. Testing it while the camera is watching
    // a seated pilot hits it at range zero -- the shell is a merged pile of
    // overlapping boxes and the plane's origin sits on one of their faces --
    // and the flight camera collapses onto the nose.
    const shell = this.foot?.active ? this.plane?.userData?.shell : null;
    if (shell) {
      const h = ray.intersectObject(shell, true)[0];
      if (h && (!hit || h.distance < hit.distance)) hit = h;
    }
    // A hit on top of the subject means the subject is embedded in something,
    // not that the camera is behind it: there is nothing useful to clamp to.
    if (!hit || hit.distance < 0.5) return;
    const d = Math.max(CAM_MIN, hit.distance - CAM_SKIN);
    if (d < len - 0.01) this.camera.position.copy(subject).addScaledVector(dir, d);
  }

  _wheelH() {
    return this.struct.gear ? WHEEL_HEIGHT : 1.15; // collapsed gear: on the belly
  }

  _setHint(text) {
    if (this.ui.hint && this.ui.hint.textContent !== text) this.ui.hint.textContent = text;
  }

  _snapCamera() {
    const offset = new THREE.Vector3(0, 7, 26).applyQuaternion(this.plane.quaternion);
    this.camera.position.copy(this.plane.position).add(offset);
    this.camera.lookAt(this.plane.position);
  }

  // First surface straight below refY + a small margin: streets, relief and
  // building roofs alike. Casting from just above the plane (not from the sky)
  // keeps bridge decks overhead from counting as the floor; if nothing is
  // found (coarse tiles refined above the wheels) retry from higher up.
  // maxAbove caps how far ABOVE refY a hit may sit and still count as ground:
  // under a flyover the gap is narrower than the 2.5 m ray start, so without
  // the cap the deck overhead was always "the floor" and the plane teleported
  // on top of it — the reason nothing could fly under a bridge or into an
  // underpass.
  _topSurfaceBelow(x, z, refY, maxAbove = Infinity, extra = null) {
    const down = new THREE.Vector3(0, -1, 0);
    const near = extra && extra.length
      ? this.world.collidablesNear(x, z).concat(extra)
      : this.world.collidablesNear(x, z);
    const lim = refY + maxAbove;
    const pick = (hits) => {
      for (const h of hits) if (h.point.y <= lim) return h.point.y;
      return null;
    };
    this.raycaster.set(new THREE.Vector3(x, refY + 2.5, z), down);
    let y = pick(this.raycaster.intersectObjects(near, true));
    if (y === null) {
      this.raycaster.set(new THREE.Vector3(x, refY + 60, z), down);
      y = pick(this.raycaster.intersectObjects(near, true));
    }
    return y;
  }

  // Ground height at (x, z). Roofs count as ground — you can touch down,
  // roll and park on top of a building. refY is the ACTOR's height (the
  // plane by default; foot.js passes the walker's own feet) — ground is
  // always judged relative to who is asking, never to where the plane sits.
  // `extra` adds objects that are not part of the world's collision set --
  // on foot that is the PLANE, so the personage can stand on the wing and
  // walk along the spine instead of through them.
  _groundAt(x, z, refY = this.plane.position.y, maxAbove, extra = null) {
    // Inside a tunnel barrel the ground is the tunnel FLOOR — heightAt reports
    // the hillside overhead, which would read as flying into the mountain.
    const bore = this.world.boreFloorAt?.(x, z, refY);
    if (bore !== null && bore !== undefined) return bore + 0.45;
    const h = this.world.heightAt(x, z);
    if (h === null) {
      // Google tiles: raycast straight down from high above.
      this.raycaster.set(new THREE.Vector3(x, 6000, z), new THREE.Vector3(0, -1, 0));
      const hits = this.raycaster.intersectObjects(this.world.collidablesNear(x, z), true);
      return hits.length ? hits[0].point.y : null;
    }
    const terr = h + 0.45; // analytic terrain + road thickness
    // A surface counts as ground only at/below the actor — plus however far
    // a fast sink can punch past it in one frame (so a hard landing can't
    // fall through the deck it just hit).
    if (maxAbove === undefined) maxAbove = 0.6 + Math.max(0, -this.vel.y) * 0.07;
    const surf = this._topSurfaceBelow(x, z, refY, maxAbove, extra);
    return surf !== null && surf > terr ? surf : terr;
  }

  _findGround() {
    if (this.world.ready === false) return; // OSM city still loading
    const sp = this.world?.spawn ?? { x: 0, z: 0 };
    const y = this._groundAt(sp.x, sp.z);
    if (y !== null) {
      this.groundY = y;
      this.started = true;
      this.ui.loading.classList.add('hidden');
      this.ui.hud.classList.remove('hidden');
      // A hero that already has a spawn place of its own picks up exactly
      // where it stopped; everyone else starts on the city's spawn road.
      if (!this._applyResume()) this.resetPlane();
      // THE PILOT IS IN THE COCKPIT FROM THE FIRST FRAME. Boarding is not the
      // only way into a plane -- a session starts with the hero already
      // sitting in one -- and the body is a download, so this waits for it.
      // A resume that stands the hero up instead wins: _stand() empties the
      // seat, whichever order the two land in.
      this.foot.ready?.then(() => {
        if (!this.disposed && !this.foot.active) this.foot.sitInPlane();
      });
      // CONTACT. A session that starts in the cockpit starts with a cold
      // engine, so the first thing you hear is the starter and the first
      // thing you see is the propeller coming round -- the engine has to be
      // running before the aeroplane will move. The exception is resuming
      // IN THE AIR: an aeroplane that was already flying does not get to
      // fall out of the sky while its starter turns over.
      // THE SOUND ITSELF. Started here rather than left to the N key: the
      // AudioContext needs a user gesture and it has had a dozen by now.
      if (this.settings?.state?.engine !== false) this.sound.start();
      if (!this.seatEmpty()) {
        if (this.airborne) {
          this.sound.setPlane(this.def);
          this.sound.state = 'running';
          this.sound.running = true;
          this.sound.rpm = 0.7;
        } else {
          this.igniteEngine();
        }
      }
      // The deferred scenery (airport, malls, metro…) builds after the world
      // is playable, and some of it is ground you can stand on. Re-seat once
      // it is all there, or a hero that logged out on the warehouse slab
      // wakes up on the grass beneath it.
      this.world._onSceneryReady = () => this._reseatOnScenery();
      this._updateAttribution();
      this._precompileShaders();
    }
  }

  // Compile every scene material up front (and again after the async layers —
  // parks, metro, landmarks — land). Without this the first flight into the
  // center hit a multi-second shader-compile hitch as new materials scrolled
  // into view. compileAsync uses KHR_parallel_shader_compile: no frame stalls.
  _precompileShaders() {
    if (new URLSearchParams(location.search).has('nocompile')) return;
    const compile = () => {
      if (this.disposed) return;
      this.renderer.compileAsync(this.scene, this.camera).catch(() => {});
    };
    compile();
    this._compileTimers = [setTimeout(compile, 20000), setTimeout(compile, 45000)];
  }

  _sampleStreetY() {
    const p = this.plane.position;
    // Rolling INSIDE a bore: the street is the tunnel floor. heightAt gives
    // the hill overhead and "roof beats street" then teleported the rolling
    // plane up through the rock onto the hilltop.
    const bore = this.world.boreFloorAt?.(p.x, p.z, p.y);
    if (bore !== null && bore !== undefined) {
      this.streetY = bore + 0.45;
      return this.streetY;
    }
    const h = this.world.heightAt(p.x, p.z);
    const terr = h !== null ? h + 0.45 : null;
    // Rolling: the street is under the wheels, never overhead — 1.5 m of
    // step-up allowance, except in Google-tiles mode (terr === null) where
    // coarse tiles legitimately refine to above the wheels.
    const surf = this._topSurfaceBelow(p.x, p.z, p.y, terr === null ? Infinity : 1.5);
    // A roof under the wheels beats the street below it.
    const y = surf !== null && (terr === null || surf > terr) ? surf : terr;
    if (y !== null) this.streetY = y;
    return this.streetY;
  }

  // GTA SA controls: W/Shift gas, S/Ctrl brake, ↑/↓ pitch, ←/→ or A/D roll, Q/E rudder.
  _readStick() {
    const k = this.keys;
    return {
      throttle:
        (k.has('KeyW') || k.has('ShiftLeft') || k.has('ShiftRight') ? 1 : 0) -
        (k.has('KeyS') || k.has('ControlLeft') || k.has('ControlRight') ? 1 : 0),
      pitch: (k.has('ArrowUp') ? 1 : 0) - (k.has('ArrowDown') ? 1 : 0),
      roll:
        (k.has('ArrowLeft') || k.has('KeyA') ? 1 : 0) -
        (k.has('ArrowRight') || k.has('KeyD') ? 1 : 0),
      rudder: (k.has('KeyQ') ? 1 : 0) - (k.has('KeyE') ? 1 : 0),
    };
  }

  _flightUpdate(dt) {
    const s = this._readStick();
    if (this._hitCooldown > 0) this._hitCooldown -= dt;

    if (this.airborne) {
      // Real aerodynamics: aero.js integrates this.vel (a world-space vector —
      // the flight path and the nose are separate things) and rotates the
      // plane with rate dynamics, weathervane stability and stall behavior.
      this.aero.update(dt, s, this.plane, this.vel, this.rates, this.struct,
        this.lastGroundDistance, this.powerFrac());
      this.speed = this.vel.length();

      // The airframe is a structure with limits: over-G rips wings off,
      // past-VNE flutter shakes the tail apart.
      this._structuralStress(dt);
      if (this.crashed) return;

      this._prevPos.copy(this.plane.position);
      this.plane.position.addScaledVector(this.vel, dt);

      // Touch physics: swept wall contact over the distance actually flown
      // this frame. A graze costs HP and deflects you along the facade; a
      // head-on into a facade at speed is simply fatal.
      if (this._touchWall(dt) && this.crashed) return;

      // Ground contact, checked every frame so the terrain can't be tunneled
      // through between the 0.15 s collision ticks. What happens next depends
      // on sink rate and attitude — grease it on, bounce, collapse the gear,
      // dig a wingtip in, or dig a crater.
      const gY = this._groundAt(this.plane.position.x, this.plane.position.z);
      if (gY !== null && this.plane.position.y <= gY + this._wheelH()) {
        this._touchdown(gY);
        if (this.crashed) return;
      } else {
        // Fell through a seam and ended up under the world (falling, nothing
        // below within reach, yet terrain above says we're underground): pop
        // back to the surface. Tunnel fly-throughs are safe — inside a tube
        // the road IS right below, so this never fires there.
        const hTer = this.world.heightAt(this.plane.position.x, this.plane.position.z);
        if (
          hTer !== null &&
          this.plane.position.y < hTer - 2 &&
          this.vel.y < 0 &&
          // inside a bore "under the terrain" is exactly where you belong —
          // the old 4 m probe missed the floor when flying the upper half of
          // the barrel and CATAPULTED the plane out through the hill
          this.world.boreFloorAt?.(
            this.plane.position.x, this.plane.position.z, this.plane.position.y
          ) == null &&
          !this._surfaceUnder(this.plane.position)
        ) {
          this.plane.position.y = hTer + 0.45 + this._wheelH();
          this._touchdown(hTer + 0.45);
          if (this.crashed) return;
        }
      }
    } else {
      // Ground roll — real thrust against brakes, rolling friction and
      // aerodynamic drag; gravity acts along the slope.
      const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
      // Holding S past a standstill backs the plane up like a car (game
      // convenience — tugs exist).
      const reversing = s.throttle < 0 && this.speed <= 0;
      // Thrust comes out of the ENGINE, not out of the key. Press W on a cold
      // aeroplane and nothing happens until the starter has done its work and
      // the revs have come up -- and on a turbine that spool is slow enough to
      // plan around, which is the whole character of taxiing a jet.
      const thrust =
        s.throttle > 0
          ? (this.aero.thrustAt(Math.max(this.speed, 0), this.plane.position.y)
             / this.aero.mass) * this.powerFrac()
          : 0;
      // 1.5 G (was 0.55): S must STOP the plane, arcade-style — a 150 km/h
      // rollout ends in ~3 s instead of coasting half the avenue.
      const braking = s.throttle < 0 && this.speed > 0.5 ? 1.5 * G : 0;
      const rolling = (this.struct.gear ? 0.012 : 0.07) * G * Math.sign(this.speed);
      const qDyn = 0.5 * this.aero.rho(this.plane.position.y) * this.speed * this.speed;
      const aeroDrag = ((qDyn * this.aero.S * this.aero.cd0) / this.aero.mass) * Math.sign(this.speed);
      const drive = reversing ? -6 : thrust;
      let v = this.speed + (drive - braking - rolling - aeroDrag - forward.y * G) * dt;
      // Don't creep backwards downhill unless S is actually held.
      if (v < 0 && this.speed >= 0 && s.throttle >= 0) v = 0;
      this.speed = THREE.MathUtils.clamp(v, -REVERSE_SPEED, this.maxSpeed * 2);

      // Nosewheel steering fades into rudder authority as speed builds;
      // mirrors while backing up, like a car in reverse.
      if (Math.abs(this.speed) > 1) {
        const steer = THREE.MathUtils.clamp(0.9 - Math.abs(this.speed) * 0.008, 0.25, 0.9);
        this.plane.rotateY((s.rudder + s.roll * 0.5) * steer * Math.sign(this.speed) * dt);
      }

      // Belly slide with collapsed gear: sparks and grinding metal.
      if (!this.struct.gear && Math.abs(this.speed) > 6) {
        this._applyDamage(4 * dt);
        if (this.crashed) return;
        if (Math.random() < 0.5) {
          const p = this.plane.position;
          this.fx.spawnEmber(p.x, p.y - 0.6, p.z);
        }
      }

      // Rotation past the type's real Vr — collapsed gear included: a belly
      // plane grinds, sparks, but still gets to leave the ground (the player
      // must always be able to fly out of a bad landing).
      const canRotate = this.speed > this.aero.vr * 0.92;
      if (s.pitch > 0 && canRotate) {
        this.plane.rotateX(s.pitch * 0.55 * dt);
      }

      this._prevPos.copy(this.plane.position);
      this.plane.position.addScaledVector(forward, this.speed * dt);

      // Taxi bump: rolling into a wall costs a little HP and stops the roll.
      if (this._touchWall(dt) && this.crashed) return;

      const streetY = this._sampleStreetY();
      if (streetY !== null) {
        const floorY = streetY + this._wheelH();
        const climbing = s.pitch > 0 && canRotate;
        if (climbing) {
          if (this.plane.position.y < floorY) this.plane.position.y = floorY;
          if (this.plane.position.y - floorY > 2.5) {
            this._liftoff(forward);
          }
        } else if (floorY < this.plane.position.y - 3 && Math.abs(this.speed) > 4) {
          // The ground fell away under the wheels — off a crest or an edge the
          // plane lofts into the air instead of gluing to the drop. A PARKED
          // plane never lofts: a bad sample frame must not un-ground it.
          this._liftoff(forward);
        } else {
          // Settle the wheels onto the ground and align the whole plane to the
          // relief: nose pitches to the slope ahead, wings bank to the ground
          // under each wingtip — riding a mountainside tilts the plane with it.
          // This also corrects the spawn height as coarse early tiles refine.
          this.plane.position.y += (floorY - this.plane.position.y) * Math.min(1, 12 * dt);
          // The faster you ride, the quicker the plane must take the terrain's
          // shape — otherwise the relief feels smoothed-out and floaty.
          const alignT = Math.min(1, (5 + Math.abs(this.speed) * 0.1) * dt);
          // Samples far off the current floor are a roof edge or a neighboring
          // building, not relief — ignore them instead of tilting into them.
          const sane = (y) => (y !== null && Math.abs(y - streetY) < 4 ? y : null);
          const aheadDist = 6;
          const aheadY = sane(
            this._groundAt(
              this.plane.position.x + forward.x * aheadDist,
              this.plane.position.z + forward.z * aheadDist
            )
          );
          const slopePitch = aheadY !== null ? Math.atan2(aheadY - streetY, aheadDist) : 0;

          const right = new THREE.Vector3(1, 0, 0).applyQuaternion(this.plane.quaternion);
          const wingDist = this.halfSpan; // half the current plane's wingspan
          const yLeft = sane(
            this._groundAt(
              this.plane.position.x - right.x * wingDist,
              this.plane.position.z - right.z * wingDist
            )
          );
          const yRight = sane(
            this._groundAt(
              this.plane.position.x + right.x * wingDist,
              this.plane.position.z + right.z * wingDist
            )
          );
          const slopeRoll =
            yLeft !== null && yRight !== null ? Math.atan2(yRight - yLeft, 2 * wingDist) : 0;

          const e = new THREE.Euler().setFromQuaternion(this.plane.quaternion, 'YXZ');
          e.x = THREE.MathUtils.lerp(e.x, slopePitch, alignT);
          e.z = THREE.MathUtils.lerp(e.z, slopeRoll, alignT);
          this.plane.quaternion.setFromEuler(e);
          // Hard floor: a parked/rolling plane can NEVER sink below the
          // street, whatever the sampling did this frame.
          if (this.plane.position.y < floorY - 0.25) this.plane.position.y = floorY;
        }
      }
    }

    // The world ends at the city bbox: the holo wall stops the plane cold
    // and you slide along it instead of leaving the map.
    if (this.boundary.clamp(this.plane.position, this.airborne ? this.vel : null)) {
      if (this.airborne) this.speed = this.vel.length();
      else this.speed *= 0.5;
      this._setHint('⚠ რუკის საზღვარი — ქალაქის გარეთ ვერ გაფრინდები!');
    }


    // GTA-style lagging chase camera with speed FOV kick.
    if (!this.look.dragging && (this.look.yaw !== 0 || this.look.pitch !== 0)) {
      // Ease the free-look back to straight ahead once the drag ends.
      const back = 1 - Math.exp(-6 * dt);
      this.look.yaw *= 1 - back;
      this.look.pitch *= 1 - back;
      if (Math.abs(this.look.yaw) < 0.001) this.look.yaw = 0;
      if (Math.abs(this.look.pitch) < 0.001) this.look.pitch = 0;
    }
    const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
    const camQuat = this.plane.quaternion
      .clone()
      .multiply(
        new THREE.Quaternion().setFromEuler(
          new THREE.Euler(this.look.pitch, this.look.yaw, 0, 'YXZ')
        )
      );
    const camTarget = new THREE.Vector3(0, 6.8 * this.camScale, 25 * this.camScale)
      .applyQuaternion(camQuat)
      .add(this.plane.position);
    const t = 1 - Math.exp(-(this.look.dragging ? 12 : 4.2) * dt);
    this.camera.position.lerp(camTarget, t);
    // While orbiting, shrink the look-ahead so the plane stays centered.
    const aheadDist = 22 * this.camScale * Math.max(0, Math.cos(this.look.yaw) * Math.cos(this.look.pitch));
    this.camera.lookAt(
      new THREE.Vector3().copy(this.plane.position).addScaledVector(forward, aheadDist)
    );
    const fov = 62 + 14 * (this.speed / this.maxSpeed) ** 2;
    if (Math.abs(fov - this.camera.fov) > 0.05) {
      this.camera.fov = fov;
      this.camera.updateProjectionMatrix();
    }
  }

  // Wheels leave the ground: hand the scalar roll speed to the vector world.
  _liftoff(forward) {
    this.airborne = true;
    this.vel.copy(forward).multiplyScalar(this.speed);
    this.rates.set(0, 0, 0);
    this._setHint('↑/↓ — სიმაღლე · ←/→ — გადახრა · Q/E — საჭე · Space — ტყვიამფრქვევი');
  }

  // Flying feedback only — speed and hard turns NEVER break the plane
  // (user rule: wings come off on buildings, not on the throttle). A little
  // shake past the red line and in a stall is just seasoning.
  _structuralStress(dt) {
    const a = this.aero;
    if (a.overspeed > 0) {
      this._shakeAmp = Math.min(0.7, this._shakeAmp + dt * 1.6 * a.overspeed);
    }
    if (a.stalled) {
      this._shakeAmp = Math.min(0.8, this._shakeAmp + dt * 1.6);
      this._setHint('⚠ STALL — ფრთამ ამწევი ძალა დაკარგა! დახარე ცხვირი, მოუმატე გაზი');
    }
  }

  // Wheels (or belly) meet the ground while flying. Sink rate and attitude
  // decide between a landing, a bounce, collapsed gear, a wing strike, and
  // a crater — like the real thing.
  _touchdown(gY) {
    const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(this.plane.quaternion);
    const sink = Math.max(0, -this.vel.y);
    // Level attitude is a landing posture — a flat drop (braked to zero and
    // falling like a leaf) SITS on the street or a roof instead of cratering.
    const level = Math.abs(fwd.y) < 0.35 && Math.abs(right.y) < 0.35;

    // Only a nose-first spear or a tumbling slam is unsurvivable.
    if ((fwd.y < -0.45 && this.speed > 60) || (sink > 10 && !level)) {
      this._explode();
      return;
    }

    // A banked touchdown digs the low wingtip into the street — if that wing
    // is still on the plane. A wingless belly-lander glides in flat and free.
    if (Math.abs(right.y) > 0.28 && this.speed > 25) {
      const side = right.y < 0 ? 'right' : 'left';
      if (this.struct[side === 'left' ? 'wingL' : 'wingR'] > 0) {
        if (this.speed > 55) this._shearWing(side, 'ფრთამ მიწას გაჰკრა');
        else this._damageWing(side, 250);
        if (this.crashed) return;
      }
    }

    this.plane.position.y = gY + this._wheelH();

    // Hard landing: the gear absorbs it — up to a point. Beyond, it folds,
    // and the plane bounces off its own compressed structure. A LEVEL drop
    // is forgiven most of it: heavy thud, maybe folded gear, but you sit.
    if (sink > 6.2) {
      this._applyDamage(level ? Math.min((sink - 5) * 18, 330) : (sink - 4) * 75);
      if (this.crashed) return;
      if (this.struct.gear && sink > (level ? 9 : 7)) this._collapseGear();
      this._shakeAmp = Math.min(1.5, this._shakeAmp + sink * 0.12);
      if (!level || sink > 12) {
        this.vel.y = Math.min(sink * 0.32, 10);
        return; // still airborne, coming down again
      }
      this._setHint('💥 მძიმე დაჯდომა!');
    } else if (sink > 3.4) {
      this._applyDamage((sink - 3) * 50);
      if (this.crashed) return;
      this._setHint('💥 მძიმე დაჯდომა!');
    }

    // WHEELS ON. A real light aeroplane arriving on a runway -- a chirp and a
    // rumble, loud in proportion to how hard it was put down. Only when there
    // is something to land ON: the gear.
    if (this.struct.gear && Math.abs(this.speed) > 8) {
      this.sound.touchdown(THREE.MathUtils.clamp(sink / 5, 0.25, 1));
    }

    // Rolling: carry the along-track speed onto the wheels.
    const hv = this.vel.clone();
    hv.y = 0;
    this.speed = hv.length() * Math.sign(hv.dot(fwd) || 1);
    this.rates.set(0, 0, 0);
    this.vel.set(0, 0, 0);
    this._land(gY);
  }

  _collapseGear() {
    this.struct.gear = 0;
    this._applyDamage(90);
    this.sound.explosion(0.3);
    this._setHint('💥 შასი ჩაიმტვრა — მუცელზე ხარ, მაგრამ აფრენა მაინც შეგიძლია!');
  }

  // A wing tears clean off the airframe — real geometry, real consequences:
  // the piece tumbles away burning and the plane rolls violently after it.
  _shearWing(side, msg) {
    const part = side === 'left' ? 'wingL' : 'wingR';
    if (!this.struct[part]) return;
    this.struct[part] = 0;
    this.wingDmg[side] = true; // smoke pours from the stump
    this.breakup.detach(part, { spin: 8, burn: Math.random() < 0.6 });
    this.sound.explosion(0.5);
    this._shakeAmp = Math.min(1.6, this._shakeAmp + 0.9);
    this._applyDamage(280);
    if (!this.crashed) this._setHint(`💥 ${msg}!`);
  }

  _shearTail(msg) {
    if (!this.struct.tail) return;
    this.struct.tail = 0;
    this.breakup.detach('tail', { spin: 6, burn: false });
    this.sound.explosion(0.5);
    this._shakeAmp = Math.min(1.6, this._shakeAmp + 0.9);
    this._applyDamage(240);
    if (!this.crashed) this._setHint(`💥 ${msg} — მართვა დაიკარგა!`);
  }

  // The building feels the plane too: contact chips real HP off the building
  // (same crumble system as the guns — 150 m/s head-on ≈ a rocket's worth),
  // with concrete dust, sparks, the address toast and a camera kick scaled
  // to how hard the metal met the wall.
  _buildingSmash(hit, headOn, sp) {
    const dmg = Math.round(sp * (0.3 + 1.7 * headOn));
    // Only real mesh hits can chip a building; synthetic terrain contacts
    // (bare relief under a dragging wingtip) just get the dust and sparks.
    const res = hit.object ? this.world.damageHit?.(hit, dmg) : null;
    const p = hit.point;
    const vol = this.sound.volume ?? 0.7;
    if (res) {
      this.guns._showToast(res);
      // the plane is basically on top of the wall — near-zero delay, all crack
      const d = this.camera.position.distanceTo(p);
      hitConcrete(d, vol);
      if (res.changed) collapseRumble(d, vol, res.destroyed);
      // scraping a facade rips the glass out of it
      const blown = this.world.blastWindows?.(p.x, p.y, p.z, 3 + headOn * 5);
      if (blown?.length) this.glass.burstList(blown, 1.1, 10);
    }
    this.guns.dust.burst(p, Math.min(28, 8 + Math.round(sp / 12)), 1.2 + headOn * 1.6);
    const nEmber = Math.min(16, 4 + Math.round((sp * headOn) / 12));
    for (let i = 0; i < nEmber; i++) this.fx.spawnEmber(p.x, p.y, p.z);
    this.fx.spawnSmoke(p.x, p.y, p.z, {
      size: 3 + headOn * 5,
      dark: 0.7,
      dur: 2,
      vy: 1.5,
      spread: 1.5,
    });
    this._shakeAmp = Math.min(1.6, this._shakeAmp + 0.25 + headOn * 0.9);
  }

  // First surface within 4 m straight below a point (roof, terrace, hill).
  _surfaceUnder(p) {
    this.raycaster.set(new THREE.Vector3(p.x, p.y + 1.2, p.z), new THREE.Vector3(0, -1, 0));
    this.raycaster.far = 4;
    const hits = this.raycaster.intersectObjects(this.world.collidablesNear(p.x, p.z), true);
    this.raycaster.far = Infinity;
    return hits.length ? hits[0] : null;
  }

  // Swept wall contact, cast from the pre-move position over the distance
  // flown this frame plus the nose length — so contact happens when the nose
  // visually touches the facade, never meters away from it. A graze costs
  // hull HP, sheds the into-wall part of the velocity and steers the nose
  // along the facade so you scrape past; a real head-on at speed is fatal.
  _touchWall(dt) {
    const sp = this.airborne ? this.vel.length() : Math.abs(this.speed);
    if (sp < 3) return false;
    const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
    const dir = this.airborne
      ? this.vel.clone().divideScalar(sp)
      : this.speed >= 0
        ? forward
        : forward.clone().negate();
    this.raycaster.set(this._prevPos, dir);
    this.raycaster.far = sp * dt + this.noseLen;
    let hits = this.raycaster.intersectObjects(this.world.collidablesNear(this._prevPos.x, this._prevPos.z), true);
    this.raycaster.far = Infinity;
    // Inside a tunnel barrel the TERRAIN is not a wall. The bore is a hole
    // through the hill but the heightfield has no hole in it, so a few metres
    // past the portal the hillside stands square across the tunnel and this
    // sweep read it as a facade — you could not fly in. _groundAt already
    // makes the same exception via boreFloorAt; the barrel's own walls, the
    // portal and everything built are still solid.
    if (hits.length && this.world.boreFloorAt?.(
      this._prevPos.x, this._prevPos.z, this._prevPos.y) != null) {
      hits = hits.filter((h) => !h.object?.userData?.isTerrain);
    }
    if (!hits.length) return false;

    const hit = hits[0];
    let n = hit.face
      ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld)
      : null;
    if (n && n.dot(dir) > 0) n = n.clone().negate(); // backface: point at us
    if (n && n.y > 0.45) return false; // upward-facing = relief/roof, not a wall
    // A bridge soffit or tunnel vault OVERHEAD is a ceiling, not a facade.
    // A shallow graze ducks under it with scraped paint instead of detonating
    // like a building strike; only a genuinely steep climb into the underside
    // falls through to the normal (fatal) wall handling below.
    if (n && n.y < -0.4 && this.airborne && dir.y < 0.35) {
      this.plane.position.copy(this._prevPos);
      this.plane.position.y -= 0.25;
      if (this.vel.y > 0) this.vel.y = 0;
      if (this._hitCooldown <= 0 && sp > 12) {
        this._hitCooldown = 0.4;
        this._applyDamage(sp * 0.7, n);
        this.fx.spawnEmber(hit.point.x, hit.point.y, hit.point.z);
        this.guns.dust.burst(hit.point, 4, 0.8);
        if (this.crashed) return true;
        this._setHint('💥 ჭერს შეეხე — კორპუსი დაზიანდა!');
      }
      return true;
    }
    const headOn = n ? THREE.MathUtils.clamp(-dir.dot(n), 0, 1) : 1;

    // Flying square into a facade at speed: the plane punches THROUGH the
    // building — the whole structure takes it and comes down — and the
    // burning wreck carries its momentum out the far side, already dead.
    if (this.airborne && headOn > 0.7 && sp > 34) {
      this._buildingSmash(hit, headOn, sp);
      // Total demolition: a full airframe at speed levels the building.
      if (hit.object) this.world.damageHit?.(hit, Math.round(sp * 20));
      this._explode(null); // no wall bounce — the wreck flies on through
      return true;
    }

    if (this._hitCooldown <= 0) {
      this._hitCooldown = 0.4;
      this._buildingSmash(hit, headOn, sp);
      // A shallow 100 m/s graze ≈ 200 HP; anything steeper escalates fast.
      this._applyDamage(sp * (1.2 + 5.5 * headOn), n);
      if (this.crashed) return true;
      this._setHint('💥 შენობას შეეხე — კორპუსი დაზიანდა!');
    } else {
      // Still scraping inside the cooldown: keep the sparks flying.
      this.fx.spawnEmber(hit.point.x, hit.point.y, hit.point.z);
      if (Math.random() < 0.4) this.guns.dust.burst(hit.point, 3, 0.7);
    }

    if (n) {
      // Back out of the wall and shed the into-wall part of the velocity.
      const out = new THREE.Vector3(n.x, 0, n.z);
      if (out.lengthSq() < 1e-4) out.copy(n);
      else out.normalize();
      this.plane.position.copy(this._prevPos).addScaledVector(out, 0.6);
      if (this.airborne) {
        const into = this.vel.dot(out);
        if (into < 0) this.vel.addScaledVector(out, -into * 1.05);
        this.vel.multiplyScalar(1 - 0.45 * headOn); // scrape friction
        this.speed = this.vel.length();
        if (headOn < 0.97) {
          // steer the nose along the facade — scrape past it, not into it
          const slide = dir.clone().addScaledVector(n, headOn).normalize();
          this.plane.quaternion.premultiply(
            new THREE.Quaternion().setFromUnitVectors(dir, slide)
          );
        }
      } else {
        this.speed *= 1 - 0.7 * headOn;
      }
    } else {
      this.plane.position.copy(this._prevPos);
      if (this.airborne) this.vel.multiplyScalar(0.2);
      this.speed *= 0.2;
    }
    if (!this.airborne) this.speed *= 0.3; // ground bump mostly stops the roll
    return true;
  }

  _checkCollision() {
    // one prefiltered list serves every ray this tick — they all start within
    // a wingspan of the plane and reach well under the hash's 1.5 km cells
    const near = this.world.collidablesNear(this.plane.position.x, this.plane.position.z);
    this.raycaster.set(this.plane.position, new THREE.Vector3(0, -1, 0));
    let down = this.raycaster.intersectObjects(near, true);
    if (!down.length) {
      // Outside the detailed area (open Georgia): analytic terrain distance.
      const h = this.world.heightAt(this.plane.position.x, this.plane.position.z);
      if (h !== null) {
        down = [{ distance: this.plane.position.y - h, point: { y: h } }];
      }
    }
    if (down.length) this.lastGroundDistance = down[0].distance;

    const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
    // While backing up, the wall check looks behind the tail instead. In the
    // air the plane travels along its VELOCITY, not its nose — sweep there.
    const backing = !this.airborne && this.speed < 0;
    const moveDir =
      this.airborne && this.vel.lengthSq() > 1
        ? this.vel.clone().normalize()
        : backing
          ? forward.clone().negate()
          : forward;
    const rayOrigin = this.airborne
      ? this.plane.position
      : this.plane.position.clone().add(new THREE.Vector3(0, 2, 0));
    this.raycaster.set(rayOrigin, moveDir);
    // The ray must cover the distance flown between 0.15 s collision ticks,
    // or a fast plane tunnels straight through a building wall undetected.
    this.raycaster.far = this.airborne ? Math.max(12, this.speed * 0.2) : 8;
    const ahead = this.raycaster.intersectObjects(near, true);
    this.raycaster.far = Infinity;

    const hitAhead = ahead.length > 0 && (this.airborne || Math.abs(this.speed) > 5);
    if (hitAhead) {
      // Walls are the swept _touchWall check's job (per-frame, true contact
      // distance). This anticipation ray only decides what rising TERRAIN
      // ahead means: touch down on it, or crater into it.
      const hit = ahead[0];
      const n = hit.face
        ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld)
        : null;
      const isRelief = n !== null && n.y > 0.45;
      if (isRelief && this.airborne && !backing) {
        this._touchdown(this._sampleStreetY() ?? hit.point.y);
      }
      return;
    }

    // The nose is clear — but a wingtip may still catch a wall. At speed the
    // wall wins: the wing tears off the airframe and tumbles away. Slower,
    // it bends and chars but stays on.
    if (this._wingHitCooldown > 0) {
      this._wingHitCooldown -= 0.15; // collision tick interval
    } else if (Math.abs(this.speed) > 8) {
      const dir = backing ? forward.clone().negate() : forward;
      for (const side of ['left', 'right']) {
        if (!this.struct[side === 'left' ? 'wingL' : 'wingR']) continue; // already gone
        this.raycaster.set(this._wingTip(side), dir);
        // Just over the distance flown per 0.15 s tick — a longer ray shears
        // wings off buildings you'd actually have cleared.
        this.raycaster.far = Math.max(2.5, Math.abs(this.speed) * 0.17);
        const hits = this.raycaster.intersectObjects(near, true);
        this.raycaster.far = Infinity;
        if (!hits.length) continue;
        const wn = hits[0].face
          ? hits[0].face.normal.clone().transformDirection(hits[0].object.matrixWorld)
          : null;
        if (wn !== null && wn.y > 0.45) continue; // relief sloping up, not a wall
        this._buildingSmash(hits[0], 0.6, Math.abs(this.speed));
        if (Math.abs(this.speed) > 55) this._shearWing(side, 'ფრთა კედელს მოხვდა და მოწყდა');
        else this._damageWing(side, 200 + Math.abs(this.speed) * 2.5);
        if (this.crashed) return;
        // Glance off: bleed speed, yaw the nose away from the wall.
        this.speed *= 0.6;
        if (this.airborne) this.vel.multiplyScalar(0.6);
        this.plane.rotateY(side === 'right' ? 0.3 : -0.3);
        if (wn) {
          this.plane.position.addScaledVector(new THREE.Vector3(wn.x, 0, wn.z).normalize(), 2);
        }
        this._wingHitCooldown = 0.8;
        break;
      }
    }

    // Banked flying drags a wingtip across ANY surface — roof, road, hillside,
    // riverbank: the surface wins. Sparks skitter along it; at speed the wing
    // tears off.
    if (this.airborne && this._wingHitCooldown <= 0 && Math.abs(this.speed) > 12) {
      for (const side of ['left', 'right']) {
        if (!this.struct[side === 'left' ? 'wingL' : 'wingR']) continue;
        const tip = this._wingTip(side);
        const under = this._surfaceUnder(tip); // mesh surfaces: roads, roofs
        // Ground relative to the TIP: inside a bore this is the tunnel floor
        // (not the hill overhead), and a soffit/vault above the tip is never
        // a surface the wing is dragging on — flying under a bridge or
        // through a tunnel must not shear the wings.
        const g = this._groundAt(tip.x, tip.z, tip.y, 0.3);
        const surfY = Math.max(
          under && under.point.y <= tip.y + 0.3 ? under.point.y : -Infinity,
          g !== null ? g : -Infinity
        );
        if (surfY === -Infinity || tip.y > surfY + 0.25) continue;
        // A mesh hit can chip a building; bare terrain just sprays dust.
        const hitLike =
          under && under.point.y >= surfY - 0.01
            ? under
            : { point: new THREE.Vector3(tip.x, surfY, tip.z) };
        this._buildingSmash(hitLike, 0.35, Math.abs(this.speed));
        if (Math.abs(this.speed) > 50) {
          this._shearWing(side, 'ფრთამ სახურავს გაჰკრა');
        } else {
          this._damageWing(side, 180);
          // the surface kicks the low wing back up
          this.plane.rotateZ(side === 'right' ? 0.18 : -0.18);
        }
        if (this.crashed) return;
        this._wingHitCooldown = 0.8;
        break;
      }
    }

    // Backup floor check between per-frame ground tests.
    if (this.airborne && down.length && down[0].distance < 3) {
      this._touchdown(down[0].point.y);
    }
  }

  _land(groundY) {
    this.airborne = false;
    this.streetY = groundY;
    this.plane.position.y = this.streetY + this._wheelH();
    // Level the wings and mostly level the nose so the ground roll starts
    // clean even after a stall drop.
    const e = new THREE.Euler().setFromQuaternion(this.plane.quaternion, 'YXZ');
    e.z = 0;
    e.x = THREE.MathUtils.clamp(e.x, -0.12, 0.12);
    this.plane.quaternion.setFromEuler(e);
    this._setHint(this._takeoffHint());
  }

  _updateHealthBar() {
    if (!this.ui.healthFill) return;
    const p = THREE.MathUtils.clamp(this.health / MAX_HEALTH, 0, 1);
    this.ui.healthFill.style.width = `${p * 100}%`;
    this.ui.healthFill.style.background =
      p > 0.55
        ? 'linear-gradient(90deg, #29c6ff, #55f2ff)'
        : p > 0.25
          ? 'linear-gradient(90deg, #ffb02e, #ffd257)'
          : 'linear-gradient(90deg, #ff2e55, #ff5e8a)';
    this.ui.healthFill.style.boxShadow =
      p > 0.55
        ? '0 0 12px rgba(63, 214, 255, 0.6)'
        : p > 0.25
          ? '0 0 12px rgba(255, 210, 87, 0.6)'
          : '0 0 12px rgba(255, 62, 138, 0.7)';
    if (this.ui.healthNum) this.ui.healthNum.textContent = Math.round(this.health);
  }

  _applyDamage(dmg, hitNormal = null) {
    this.health = Math.max(0, this.health - dmg);
    this._updateHealthBar();
    if (this.health <= 0) this._explode(hitNormal);
  }

  // Total destruction: the airframe comes apart along its structural lines —
  // real wings, tail, sometimes the nose tear off and scatter burning — then
  // the cinematic crash sequence tumbles the bare fuselage to the ground.
  _explode(hitNormal = null) {
    if (this.crashed) return;
    this.health = 0;
    this._updateHealthBar();
    this.crashed = true;
    const baseVel = this.airborne
      ? this.vel.clone()
      : new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion).multiplyScalar(this.speed);
    const severity = THREE.MathUtils.clamp(baseVel.length() / 110, 0.5, 1.3);
    this.breakup.crashScatter(baseVel, severity);
    this.struct.wingL = this.struct.wingR = this.struct.tail = 0;
    this.crash = new CrashSequence(this, hitNormal);
    // The flight loop -- and with it the engine's own update -- stops here, so
    // the engine has to be cut explicitly or its loops run on under the wreck.
    this.sound.kill();
    this.sound.explosion(1);
    this.saveState(true, 'death'); // the hero died — its saved spot goes with it
  }

  // GTA-style death screen: the world drains to grayscale and WASTED slams in.
  _showWasted() {
    if (this._wastedShown) return;
    this._wastedShown = true;
    this.container.classList.add('wasted');
    this.ui.crash.classList.remove('hidden');
  }

  // A wingtip sheared off on a wall: bend and char the wing, start the smoke,
  // and from now on the plane flies lopsided (see _flightUpdate).
  _damageWing(side, dmg) {
    this._applyDamage(dmg);
    if (this.crashed || this.wingDmg[side]) return;
    this.wingDmg[side] = true;
    // A bent wing carries less lift and drags — the plane pulls toward it.
    const part = side === 'left' ? 'wingL' : 'wingR';
    this.struct[part] = Math.min(this.struct[part], 0.65);
    const wing = this.plane.userData.wings?.[side];
    if (wing) {
      wing.mesh.userData.origRotZ ??= wing.mesh.rotation.z;
      wing.mesh.rotation.z += side === 'right' ? -0.16 : 0.16; // tip droops
      const mats = Array.isArray(wing.mesh.material) ? wing.mesh.material : [wing.mesh.material];
      for (const m of mats) {
        m.userData.origColor ??= m.color.getHex();
        m.color.setHex(0x2e2c26); // charred
        m.roughness = 1;
      }
    }
    this._setHint(
      side === 'right' ? '💥 მარჯვენა ფრთა დაზიანდა — მართვა ჭირს!' : '💥 მარცხენა ფრთა დაზიანდა — მართვა ჭირს!'
    );
  }

  _repairWings() {
    for (const side of ['left', 'right']) {
      if (!this.wingDmg[side]) continue;
      this.wingDmg[side] = false;
      const wing = this.plane.userData.wings?.[side];
      if (!wing) continue;
      if (wing.mesh.userData.origRotZ !== undefined) {
        wing.mesh.rotation.z = wing.mesh.userData.origRotZ;
      }
      const mats = Array.isArray(wing.mesh.material) ? wing.mesh.material : [wing.mesh.material];
      for (const m of mats) {
        if (m.userData.origColor !== undefined) m.color.setHex(m.userData.origColor);
        m.roughness = 0.55;
      }
    }
    this.smoke.clear();
  }

  // Smoke pours from the tip of a damaged wing — or from the torn stump if
  // the wing itself is already tumbling somewhere over Saburtalo.
  _wingSmokePos(side) {
    const tip = this._wingTip(side);
    const part = side === 'left' ? 'wingL' : 'wingR';
    return this.struct[part] === 0 ? this.plane.position.clone().lerp(tip, 0.3) : tip;
  }

  // World position of a wingtip — the smoke emitter and clip-ray anchor.
  _wingTip(side) {
    const wing = this.plane.userData.wings?.[side];
    if (wing) return this.plane.localToWorld(wing.tip.clone());
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(this.plane.quaternion);
    const d = this.halfSpan * 0.93;
    return this.plane.position.clone().addScaledVector(right, side === 'right' ? d : -d);
  }

  _updateHud() {
    this.ui.speed.textContent = Math.round(Math.abs(this.speed) * 3.6);
    this.ui.alt.textContent = Math.round(this.lastGroundDistance);

    // Compass: heading clockwise from north (+Z north, -X east).
    const f = new THREE.Vector3(0, 0, -1).applyQuaternion(this.plane.quaternion);
    const hRad = Math.atan2(-f.x, f.z); // 0 = north, clockwise
    let h = Math.atan2(-f.x, f.z) * THREE.MathUtils.RAD2DEG;
    if (h < 0) h += 360;
    if (this.ui.compassDial) {
      this.ui.compassDial.style.transform = `rotate(${-h}deg)`;
      this.ui.compassDeg.textContent = `${Math.round(h)}°`;
    }
    this.mapui.update(this.plane.position.x, this.plane.position.z, hRad);
  }

  _tick() {
    if (this.disposed) return;
    // DETERMINISTIC CAPTURE. The clock is normally real time capped at 50 ms,
    // which means a slow frame advances the world by less than it took — and
    // a RECORDING of that is non-uniform: the cheap shots race past and the
    // expensive ones linger, however carefully the encoder retimes the file.
    // tools/record_reel.mjs sets __fixedDt so every rendered frame is the
    // same slice of film, whatever the page manages to draw it in.
    const real = Math.min(this.clock.getDelta(), 0.05);
    const dt = window.__fixedDt || real;

    if (!this.started) {
      this._findGround();
    } else if (this.cinema?.active) {
      // THE REEL OWNS THE FRAME. The world runs on the film's clock (slow motion
      // is a world that slows down, not an edit that does), the hero is driven
      // by the script, and the camera is written afterwards -- the chase cam has
      // already had its say inside foot.update and is overruled here.
      const sdt = dt * this.cinema.timeScale;
      this.cinema.update(dt);
      // scene 3 flies the aeroplane with the hero sitting in it, so the body
      // is driven as a PILOT; the others have him on his feet
      if (this.foot.active) this.foot.update(sdt);
      else this.foot.updatePilot(sdt);
      this.pistol.update(sdt);
      for (const ga of Object.values(this.gunAnims)) ga.update(sdt);
      this.cinema.postUpdate(dt);
    } else if (!this.crashed && this.foot.active) {
      // On foot: the personage runs the show; the plane sits parked.
      this.foot.update(dt);
      this.pickups.update(dt, this.foot.pos);
      this.footsteps.update(dt, this.foot);
      this.pistol.update(dt);
      for (const ga of Object.values(this.gunAnims)) ga.update(dt);   // the clips that pose the hero's arms
      this._collisionTimer = (this._collisionTimer ?? 0) + dt;
      if (this._collisionTimer > 0.15) {
        this._collisionTimer = 0;
        this._updateAttribution();
        this.zones.update(this.foot.pos.x, this.foot.pos.z);
        if (this.mapui.pinsDirty) this._syncPinBeams();
      }
    } else if (!this.crashed) {
      // SOMEBODY IS HALFWAY OUT OF THE COCKPIT. The aeroplane is parked for
      // the duration, whatever the engine is doing: it idles forward at about
      // 0.1 m/s, and the climb-out is planned once in world space from the
      // seat, the sill and the wing — so a machine that creeps while the
      // pilot is climbing down leaves them stepping onto where the wing used
      // to be. The user saw the obvious half of it: "when i am goin out of
      // plain, plain goin".
      if (this.foot._boardSeq || this.foot._boardAfter) {
        this.speed = 0;
        this.vel.set(0, 0, 0);
        this.rates?.set(0, 0, 0);
      } else {
        this._flightUpdate(dt);
      }
      this.foot.updatePilot(dt); // the pilot: sitting there, or climbing out
      this._collisionTimer = (this._collisionTimer ?? 0) + dt;
      if (this._collisionTimer > 0.15) {
        this._collisionTimer = 0;
        this._checkCollision();
        this._updateAttribution();
        this.zones.update(this.plane.position.x, this.plane.position.z);
        if (this.mapui.pinsDirty) this._syncPinBeams();
      }
      this._updateHud();
    } else if (this.crash) {
      // Crash cinematic: the wreck tumbles, burns and settles; the sequence
      // also drives the camera and triggers the WASTED overlay.
      this.crash.update(dt);
    }

    // Smoke keeps pouring from damaged wings — and from the wreck after a
    // total crash — even while the physics is frozen. During the crash
    // cinematic every effect runs on the sequence's slow-mo clock.
    if (this.started) {
      const fdt = dt * (this.crash?.timeScale ?? this.cinema?.timeScale ?? 1);
      this._smokeTimer += fdt;
      if (this._smokeTimer > 0.05) {
        this._smokeTimer = 0;
        if (this.crashed) {
          this.smoke.spawn(this.plane.position);
        } else {
          if (this.wingDmg.left) this.smoke.spawn(this._wingSmokePos('left'));
          if (this.wingDmg.right) this.smoke.spawn(this._wingSmokePos('right'));
        }
      }
      this.smoke.update(fdt);
      this.breakup.update(fdt); // torn-off parts keep falling during the cinematic
      this.guns.update(fdt);
      this.rockets.update(fdt);
      this.bullets.update(fdt);   // rounds still flying
      this.impacts.update(fdt);   // dust, sparks, brass on the pavement
      this.paper.update(fdt);     // the score card fades out after the last shot
      // the lane controls belong to the firing point the PERSONAGE is standing
      // at, so they are driven by its position and not by the camera's
      this.lanes.update(fdt, this.foot?.active ? this.foot.pos : null);
      this.fx.update(fdt, this.camera.position);
      this.blast.update(fdt);
      this.glass.volume = this.sound.volume ?? 0.7;
      this.glass.listener.copy(this.camera.position);
      this.glass.update(fdt);
    }

    if (this.started) {
      const groundRefY = this.airborne
        ? this.plane.position.y - this.lastGroundDistance
        : this.streetY;

      // Crash-warning grid: only a faint pulse when about to hit the ground in
      // the air (below 10 m). No longer a constant red floor at low altitude.
      //
      // NEVER in a reel. The films drive the aeroplane themselves and the
      // flight branch that maintains lastGroundDistance does not run, so the
      // distance sits at whatever it was when the plane was parked — nearly
      // zero — and the moment the flypast lifts off, `airborne` turns this on
      // and leaves a red grid hanging at the aeroplane's own altitude for the
      // rest of the film. It is an instrument, not scenery.
      if (groundRefY !== null && this.airborne && this.lastGroundDistance < 10
          && !this.cinema?.active) {
        const strength = THREE.MathUtils.clamp((10 - this.lastGroundDistance) / 10, 0, 1);
        const pulse = 0.5 + 0.5 * Math.sin(this.clock.elapsedTime * 9);
        this.warnGrid.material.opacity = strength * 0.35 * pulse;
        this.warnGrid.visible = this.warnGrid.material.opacity > 0.03;
        // Snap to a 10 m world raster so the lines stay put while the plane
        // flies over them — that's what makes height and speed readable.
        this.warnGrid.position.set(
          Math.round(this.plane.position.x / 10) * 10,
          groundRefY + 0.6,
          Math.round(this.plane.position.z / 10) * 10
        );
      } else {
        this.warnGrid.visible = false;
      }
    }

    this.sky.position.copy(this.camera.position);
    this.boundary.update(this.plane.position, this.clock.elapsedTime);

    // Shadow box follows the player (snapped to a grid so edges don't shimmer)
    // — the plane while flying, the personage while on foot.
    const ref = this.foot?.active ? this.foot.pos : this.plane.position;
    const sx = Math.round(ref.x / 50) * 50;
    const sz = Math.round(ref.z / 50) * 50;
    this.sun.target.position.set(sx, 0, sz);
    this.sun.position.set(sx, 0, sz).addScaledVector(this.sunDir, 2500);

    // The shadow box follows the altitude: tight and sharp down among the
    // buildings, wide enough not to lose distant shadows when you climb. One
    // box for both would be either blurry on foot or empty from the air.
    const agl = Math.max(0, ref.y - (this.streetY ?? this.groundY ?? 0));
    const want = THREE.MathUtils.clamp(
      SHADOW_NEAR_M + agl * 0.9, SHADOW_NEAR_M, SHADOW_FAR_M
    );
    const cam = this.sun.shadow.camera;
    if (Math.abs(want - cam.right) > want * 0.12) {   // resize in coarse steps
      cam.left = -want; cam.right = want;
      cam.top = want; cam.bottom = -want;
      cam.updateProjectionMatrix();
    }

    // Refresh the shadow map every other frame. Nothing in view moves fast
    // enough at this scale for the stale frame to show, and it is the single
    // cheapest few ms in the budget.
    this._shadowTick = (this._shadowTick ?? 0) + 1;
    this.renderer.shadowMap.autoUpdate = false;
    this.renderer.shadowMap.needsUpdate = (this._shadowTick & 1) === 0;

    // impact shake: brief random camera jolt, decaying fast
    if (this._shakeAmp > 0.004) {
      const a = this._shakeAmp;
      this.camera.position.x += (Math.random() - 0.5) * a * 1.1;
      this.camera.position.y += (Math.random() - 0.5) * a * 0.8;
      this.camera.position.z += (Math.random() - 0.5) * a * 1.1;
      this._shakeAmp *= Math.exp(-3.2 * dt);
    }

    // Stream the hero's spot to the socket a couple of times a minute.
    if (this.started) {
      this._saveTimer += dt;
      if (this._saveTimer > 2.5) {
        this._saveTimer = 0;
        this.saveState();
      }
    }

    // THE ENGINE, and the propeller it turns. EVERY FRAME, whatever the player
    // is doing.
    //
    // It used to live at the end of _flightUpdate, which is skipped while the
    // hero is on foot, mid-climb, or dead -- so shutting the engine down on
    // the way out of the cockpit set the state and then nothing ever faded the
    // loops: the aeroplane kept running while you walked away from it. ("when
    // u are goin out of plane turn of engine".) The engine belongs to the
    // aeroplane, not to the flight branch.
    //
    // The old code before that spun the blades off AIRSPEED -- a parked plane
    // had a dead propeller, a dive had one screaming, and the throttle did
    // nothing to either. It is the other way round: the engine has revs, the
    // revs turn the propeller AND make the thrust, and airspeed is the
    // consequence.
    {
      // The stick only reaches the engine from the cockpit: on foot W is a
      // pair of legs, not a throttle.
      const flying = this.started && !this.crashed && !this.foot?.active
        && !this.foot?._boardSeq && !this.foot?._boardAfter;
      this.sound.update(dt, {
        speed: flying ? Math.abs(this.speed) : 0,
        throttle: flying ? this._readStick().throttle : 0,
        airborne: flying && this.airborne,
      });
      // AND IT STAYS OFF. `flying` only takes the throttle away from a pilot
      // who is not in the seat -- the engine itself would idle on for ever
      // under a hero standing on the apron. Nobody in the cockpit and nobody
      // climbing into it means the mixture is out, whichever way the hero got
      // out: the climb down the wing, a teleport, a cinema cue, or a session
      // that resumed on foot. ("when i am goin out of plane always turn off
      // engine".) shutdown() is idempotent, so this is a guarantee, not a
      // second shutdown -- the climb's own one still runs the blades down
      // while the pilot is stepping past them.
      if (this.started && !this.crashed && this.foot?.active
        && !this.foot._boardSeq && !this.foot._boardAfter
        && (this.sound.state === 'running' || this.sound.state === 'starting')) {
        this.stopEngine();
      }
      // 0..1 of max revs -> real crankshaft rpm, for the blur threshold. A
      // piston aero engine idles near 700 and redlines around 2700.
      spinPropellers(this.plane, this.sound.rpm * 2700, dt);
    }

    // THE HOOD. Up only while someone is getting in or out of the cockpit —
    // it swings open as the personage climbs on and comes down behind them
    // either way, including when they leave ("when u are goin out of plane u
    // need to close hood"). Eased, because a canopy is heavy.
    if (this.plane?.userData?.hood) {
      const want = this.foot?._boardSeq || this.foot?._boardAfter ? 1 : 0;
      this._hoodT = (this._hoodT ?? want) + (want - (this._hoodT ?? want)) * Math.min(1, dt * 3.4);
      setHoodOpen(this.plane, this._hoodT);
      // AND YOU HEAR IT: the latch and slide of a real fighter canopy as it
      // starts moving, and a second, harder knock as it seats. Both fire ONCE
      // per movement, and only after the hood has actually been up -- a plane
      // sitting closed since the first frame has nothing to announce.
      if (this._hoodWant !== want) {
        if (this._hoodWant !== undefined) this.sound.canopy(want > 0);
        this._hoodWant = want;
        if (want > 0) this._hoodSeated = false;
      }
      if (want === 0 && !this._hoodSeated && this._hoodT < 0.06) {
        this._hoodSeated = true;
        this.sound.canopy(false);
      }
    }
    // Under a closed canopy the engine is behind a firewall and a sheet of
    // perspex. Open it, or climb out, and the top end comes back. The hood
    // position drives it, so what you hear matches what you can see over the
    // rail -- which is what the user asked for: sounds for sitting inside it.
    // Only on a real change: setTargetAtTime every frame piles up automation
    // events on the filter for nothing.
    const muffle = this.foot?.active ? 0 : 1 - (this._hoodT ?? 0);
    if (Math.abs(muffle - (this._muffle ?? -1)) > 0.02) {
      this._muffle = muffle;
      this.sound.setMuffle(muffle);
    }

    updateWind(dt);   // one breeze, read by every tree and the windsock
    this._keepCameraOutside();
    this.camera.updateMatrixWorld();
    this.combatHud.update(dt);
    this.world.update(this.camera.position, dt);
    // Props that react to the PERSONAGE rather than to the camera (the range's
    // lane gates). The camera trails 6 m behind, so it cannot drive these.
    if (this.world.propUpdaters?.length) {
      const actor = this.foot?.active ? this.foot.pos : this.plane.position;
      for (const u of this.world.propUpdaters) u(actor, dt);
    }
    if (this.hq && this.composer) this.composer.render();
    else this.renderer.render(this.scene, this.camera);
    if (this.started && this.world.ready) this._adaptResolution(dt);
  }

  // One-way safety valve: if frames consistently miss ~50 fps at full retina,
  // drop once to 1.5× and stay. Never oscillates, never goes blurry-low.
  _adaptResolution(dt) {
    const p = this._perf;
    if (p.dropped || this._resScale <= 1.5) return;
    p.ema += (Math.min(dt, 0.1) * 1000 - p.ema) * 0.05;
    p.cool -= dt;
    if (p.cool > 0 || p.ema <= 20) return;
    p.dropped = true;
    this._resScale = 1.5;
    this.renderer.setPixelRatio(1.5);
    this.composer?.setPixelRatio(1.5);
    console.info(`render scale → 1.5x for this session (frame was ~${p.ema.toFixed(0)} ms)`);
  }

  // Next effect light from the pool, round robin. NEVER `new PointLight` in a
  // gameplay path -- see the pool's note in _setupScene().
  takeFxLight(color, intensity, distance, decay = 1.8) {
    const l = this.fxLights[this._fxLightCursor++ % this.fxLights.length];
    l.color.setHex(color);
    l.intensity = intensity;
    l.distance = distance;
    l.decay = decay;
    return l;
  }

  // Hand one back: parked and dark, so the next crash starts clean.
  freeFxLight(l) {
    if (!l) return;
    l.intensity = 0;
    l.position.set(0, -10000, 0);
  }

  // Rebuild the gold beacon pillars whenever the pin set changes — a pin on
  // the map is a light column in the world you can actually fly to.
  _syncPinBeams() {
    this.mapui.pinsDirty = false;
    for (const beam of [...this._pinBeams.children]) {
      this._pinBeams.remove(beam);
      beam.geometry.dispose();
      beam.material.dispose();
    }
    for (const p of this.mapui.pinsWorld()) {
      const geo = new THREE.CylinderGeometry(5, 8, 2600, 10, 1, true);
      const mat = new THREE.MeshBasicMaterial({
        color: 0xffd257,
        transparent: true,
        opacity: 0.16,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        side: THREE.DoubleSide,
        fog: false,
      });
      const beam = new THREE.Mesh(geo, mat);
      const groundY = this.world.heightAt?.(p.x, p.z) ?? 0;
      beam.position.set(p.x, groundY + 1300, p.z);
      beam.renderOrder = 4;
      this._pinBeams.add(beam);
    }
  }

  _updateAttribution() {
    const a = this.world.attribution;
    this.ui.attribution.textContent = typeof a === 'function' ? this.world.attribution() : a;
  }

  _showError(message) {
    this.ui.errorText.textContent = message;
    this.ui.error.classList.remove('hidden');
    this.ui.loading.classList.add('hidden');
  }

  dispose() {
    this.saveState(true, 'back to menu'); // keep the exact spot we left on
    this.disposed = true;
    for (const t of this._compileTimers ?? []) clearTimeout(t);
    this.renderer.setAnimationLoop(null);
    window.removeEventListener('resize', this._onResize);
    window.removeEventListener('keydown', this._onKeyDown);
    window.removeEventListener('keyup', this._onKeyUp);
    window.removeEventListener('blur', this._onBlur);
    this.dev?.dispose();
    const canvas = this.renderer.domElement;
    canvas.removeEventListener('pointerdown', this._onPointerDown);
    canvas.removeEventListener('pointermove', this._onPointerMove);
    canvas.removeEventListener('pointerup', this._onPointerUp);
    canvas.removeEventListener('contextmenu', this._onContextMenu);
    canvas.removeEventListener('pointercancel', this._onPointerUp);
    this.crash?.dispose();
    this.crash = null;
    this.container.classList.remove('wasted');
    this.cinema.dispose();
    this.foot.dispose();
    this.sound.dispose();
    this.smoke.dispose();
    this.breakup.dispose();
    this.guns.dispose();
    this.rockets.dispose();
    this.skillbar.dispose();
    this.weapons.dispose();
    this.pickups.dispose();
    this.pistol.dispose();
    for (const ga of Object.values(this.gunAnims)) ga.dispose();
    this.bullets.dispose();
    this.impacts.dispose();
    this.paper.dispose();
    this.lanes.dispose();
    this.combatHud.dispose();
    this.boundary.dispose();
    this.fx.dispose();
    this.blast.dispose();
    this.glass.dispose();
    this.world.dispose();
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }

  _resize() {
    const w = this.container.clientWidth;
    const h = this.container.clientHeight;
    this.centreAim?.(); // the crosshair is positioned in pixels — re-centre it
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(w, h);
    this.composer?.setSize(w, h);
    this.world.setResolution(this.camera, this.renderer);
  }
}
