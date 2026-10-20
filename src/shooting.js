import * as THREE from 'three';
import { CARTRIDGES } from './ballistics.js';
import { gunReport, magOut, magIn, slideRelease, dryFire, akMagOut, akMagIn, akRack } from './audiofx.js';
const CUE_FN = { magOut, magIn, slide: slideRelease, akMagOut, akMagIn, akRack };

// Pistol fire for the personage on foot.
//
// GTA's scheme: a drawn weapon always shows a reticle and can be fired from the
// hip; holding the right mouse button goes to AIM -- the camera comes in over
// the right shoulder, the reticle tightens, the hero stops sprinting and turns
// to face where the camera looks. Left mouse fires, one shot per click, because
// a pistol is semi-automatic.
//
// What leaves the barrel is a REAL ROUND, not a ray: see ballistics.js. The
// cartridge comes from the weapon in your hand, so the Makarov and the Beretta
// do not shoot the same -- different holes, different reach, different recoil,
// different noise.
//
// Ammunition is unlimited, as asked -- but the MAGAZINE is real. It holds what
// the weapon really holds (8 for the PM, 15 for the 92FS), the count is on the
// weapon's icon in the bar, and when it runs out you reload: R does it on
// demand, and pulling the trigger on an empty gun does it for you. The reload
// takes exactly as long as the animation takes, because it IS the animation --
// see src/gunanim.js.
const RANGE = 220;        // m — how far the aim ray looks for something to aim AT
const HIP_SPREAD = 0.045; // rad -- fired from the hip, deliberately loose
const AIM_SPREAD = 0.004; // rad -- aimed
const BLOOM = 0.011;      // rad added per shot while the muzzle is still climbing
const BLOOM_MAX = 0.05;
const BLOOM_COOL = 0.055; // rad/s the cone settles back down
const FLASH_T = 0.045;    // s the muzzle flash stays lit
const AIM_TOGGLE_MS = 320; // a double click is one toggle, not two
const CAM_BACK = 8;       // m — the chase camera sits this far behind the hero,
                          // so the look ray needs that much extra reach
const ZERO = 25;          // m — pistol sights are zeroed at 25 m, so the barrel
                          // is laid a hair above the line of sight
const RECOIL_BACK = 14;   // 1/s — how fast the sights come back down
const KICK_BACK = 9;      // 1/s — how fast the weapon itself settles
// ---- PUBG's shooting ---------------------------------------------------------
// Three things PUBG does that a plain "kick the camera up and bring it back"
// does not, and that are most of what makes its guns feel like guns:
//   RECOIL CONTROL  the sights climb with every round and only part of that
//                   comes back by itself; the rest is the shooter pulling the
//                   mouse down against the burst. A burst left alone walks up
//                   the wall and wanders sideways (a random walk, not jitter).
//   THE CONE        first shot of a burst is as accurate as the weapon gets,
//                   then it opens; moving opens it, crouching closes it.
//   SWAY            down the sights the view wanders slowly -- more on the
//                   move, more with a long gun -- so standing still and
//                   crouching are worth something.
// plus a camera shake and the weapon jumping back into the hands on the shot.
const BURST_GAP = 0.32;   // s — longer than this between rounds and the burst is over
const MOVE_SPREAD = 0.032; // rad the cone opens by at a full run
const CROUCH_CONE = 0.7;  // x the cone when ducked
const SWAY_AMP = 0.0022;  // rad of aimed wander, standing still
const SWAY_MOVE = 2.4;    // x more at a full run
const SHAKE_AMP = 0.006;  // rad of camera shake on a shot
const SHAKE_TAU = 0.06;   // s it takes to die away
const BLIND_SPREAD = 0.075; // rad the cone opens by when the arm is round a corner unsighted
const KICKB_BACK = 20;    // 1/s — the weapon coming back forward into the hold

export class Pistol {
  constructor(game) {
    this.game = game;
    this._cool = 0;
    this._flash = 0;
    this._bloom = 0;
    this._recoil = 0;   // rad of muzzle rise currently fed into the camera
    this._kick = 0;     // rad the weapon model itself is still rotated back by
    this.ammo = null;   // rounds left in the magazine (null = not drawn yet)
    this._reload = 0;   // s left of the reload now running
    this._fire = 0;     // s left of the shot's animation on the arms
    this._fireFrom = 0;
    this._reloadFrom = 0;
    this._cues = [];    // reload noises still to play, soonest last
    this._trigger = false;   // the mouse button is down: an automatic keeps firing
    this._burst = 0;         // rounds in the current burst
    this._lastShot = -1e9;   // s, when the last round went
    this._drift = 0;         // rad/shot the sideways wander is currently walking
    this._kickB = 0;         // m the weapon is still back into the hands
    this.rock = 0;           // rad the torso is still rocked back by the shots (rifles)
    this._shake = 0;         // 0..1 of the shot's camera shake left
    this._t = 0;
    this.swayYaw = 0;        // rad -- the aimed wander + the shake, read by the camera
    this.swayPitch = 0;
    this.aiming = false;
    this._ray = new THREE.Raycaster();
    this._ray.firstHitOnly = true;
    this._v = new THREE.Vector3();
    this._dir = new THREE.Vector3();
    this._camDir = new THREE.Vector3();
    this._aim = new THREE.Vector3();
    this._ndc = new THREE.Vector2();
    this._up = new THREE.Vector3();
    this._right = new THREE.Vector3();

    // muzzle flash: a small additive sprite parked on the weapon
    const tex = flashTexture();
    this.flash = new THREE.Sprite(new THREE.SpriteMaterial({
      map: tex, color: 0xffd9a0, blending: THREE.AdditiveBlending,
      depthWrite: false, transparent: true, toneMapped: false,
    }));
    this.flash.scale.setScalar(0.5);
    this.flash.visible = false;
    game.scene.add(this.flash);

    const hud = game.ui.hud ?? document.body;
    this.el = document.createElement('div');
    this.el.id = 'reticle';
    this.el.innerHTML = '<i></i><i></i><i></i><i></i><b></b>';
    this.el.style.display = 'none';
    hud.appendChild(this.el);
    if (!document.getElementById('reticle-css')) {
      const st = document.createElement('style');
      st.id = 'reticle-css';
      st.textContent = `
/* Parked ON THE MOUSE: game.js writes left/top in canvas pixels every time the
   pointer moves, and the -20px margin centres the cross on that point. No
   transition on left/top -- the crosshair must not lag the pointer. */
#reticle{position:absolute;left:50%;top:50%;width:40px;height:40px;margin:-20px 0 0 -20px;
  pointer-events:none;z-index:38;transition:transform .09s ease-out}
/* Each tick is white with its own dark outline, so it reads against a sunlit
   wall and against the shadow under the roof. A pale hairline with a soft
   shadow disappeared completely on the warehouse's light concrete. */
#reticle i{position:absolute;background:#fff;border:1px solid rgba(0,0,0,.9);
  box-shadow:0 0 6px rgba(0,0,0,.7)}
#reticle i:nth-child(1){left:50%;top:0;width:2px;height:10px;margin-left:-2px}
#reticle i:nth-child(2){left:50%;bottom:0;width:2px;height:10px;margin-left:-2px}
#reticle i:nth-child(3){top:50%;left:0;height:2px;width:10px;margin-top:-2px}
#reticle i:nth-child(4){top:50%;right:0;height:2px;width:10px;margin-top:-2px}
#reticle b{position:absolute;left:50%;top:50%;width:3px;height:3px;margin:-2.5px 0 0 -2.5px;
  border-radius:50%;background:#fff;border:1px solid rgba(0,0,0,.9);
  box-shadow:0 0 6px rgba(0,0,0,.7)}
#reticle.aim{transform:scale(.6)}
#reticle.aim b{background:#ff5a4d;border-color:rgba(0,0,0,.95)}`;
      document.head.appendChild(st);
    }
  }

  // The cartridge in the weapon currently in the hand. Everything ballistic
  // hangs off this: hole size, reach, damage, recoil, rate, report.
  get cart() {
    const id = this.game.foot?.held?.cartridge;
    return CARTRIDGES[id] ?? CARTRIDGES['9x19'];
  }

  // The crosshair is pinned to the middle of the screen (game.centreAim), where
  // Counter-Strike keeps it; this just writes that position.
  moveReticle(x, y) {
    this.el.style.left = `${x}px`;
    this.el.style.top = `${y}px`;
  }

  // World direction out of the camera through the crosshair — i.e. straight
  // down the middle of the screen. The shot uses it, so what the crosshair
  // covers is what gets hit.
  //
  // A CINEMATIC AIMS FOR ITSELF. There is no crosshair during one and the camera
  // is not on the hero's shoulder -- it is on the floor across the stage -- so
  // the director hands over the line instead (src/cinema.js). Everything else
  // reads the aim through here, which is why the arms go where the round goes.
  aimDir(out = this._camDir) {
    const g = this.game;
    if (g.cinema?.active && g.cinema.aim) return out.copy(g.cinema.aim);
    // BLIND FIRE from cover: the arm round the corner points where the wall
    // says, not where the crosshair does (foot.js sets this while it is out)
    if (g.foot?.aimOverride) return out.copy(g.foot.aimOverride);
    this._ray.setFromCamera(this._ndc.set(g.aim?.ndcX ?? 0, g.aim?.ndcY ?? 0), g.camera);
    return out.copy(this._ray.ray.direction);
  }

  setVisible(on) {
    this.el.style.display = on ? '' : 'none';
    // Drawing a weapon grabs the mouse: crosshair centred, pointer locked, arrow
    // gone. Holstering gives it back. Drawing happens on a key or a click, so
    // the lock request always has the user gesture the browser demands.
    const canvas = this.game.renderer?.domElement;
    if (canvas) canvas.style.cursor = on ? 'none' : '';
    if (on) {
      this.game.centreAim?.();
      this.game._lockPointer?.();
    } else {
      this.game._unlockPointer?.();
    }
    if (!on) {
      this.aiming = false;
      this.el.classList.remove('aim');
      this.flash.visible = false;
      this._bloom = 0;
    }
  }

  setAiming(on) {
    if (this.aiming === on) return;
    this.aiming = on;
    this.el.classList.toggle('aim', on);
    if (!on) this.el.style.transform = '';
  }

  // Flip aim on or off. Debounced, because a DOUBLE CLICK delivers two presses
  // and a naive toggle would flip twice and land back where it started -- so
  // double-clicking would appear to do nothing at all.
  toggleAim() {
    const now = performance.now();
    if (now - (this._toggledAt ?? -1e9) < AIM_TOGGLE_MS) return;
    this._toggledAt = now;
    this.setAiming(!this.aiming);
  }

  // How many the magazine holds, and whether a reload is running.
  get magSize() { return this.cart.mag || 0; }
  get reloading() { return this._reload > 0; }

  // HOW MUCH OF THE RELOAD ANIMATION IS SHOWING, 0..1. The carry and the aim
  // are the game's own pose solvers and stay that way -- the clip is laid over
  // the top for the magazine change alone, and it fades in and out so the move
  // grows out of the hold instead of snapping on. foot.js reads this.
  get reloadBlend() {
    if (this._reload <= 0) return 0;
    const IN = 0.18;   // s to pick the move up
    const OUT = 0.24;  // s to put the hands back on the gun
    return Math.min(1, (this._reloadFrom - this._reload) / IN, this._reload / OUT);
  }

  // HOW MUCH OF THE SHOT'S ANIMATION IS SHOWING, 0..1 -- the clip's recoil
  // laid over the hold for the half second after the trigger, in fast and
  // out slow, the way reloadBlend does it. foot.js reads this.
  get fireBlend() {
    if (this._fire <= 0) return 0;
    const IN = 0.03;
    const OUT = 0.16;
    return Math.min(1, (this._fireFrom - this._fire) / IN, this._fire / OUT);
  }

  // RELOAD. On R, or on its own when you pull the trigger with nothing left.
  // Two animations: the slide stays closed on a magazine swapped while a round
  // is still chambered, and locks back -- and has to be released -- when the
  // gun has been run dry. That is a second of difference, so shooting the
  // magazine completely empty costs you.
  reload(auto = false) {
    const g = this.game;
    if (!g.foot?.active || !g.foot.held) return false;
    const mag = this.magSize;
    if (!mag || this._reload > 0 || this.ammo >= mag) return false;
    const dry = this.ammo <= 0;
    const name = dry ? 'reloadEmpty' : 'reload';
    const t = g.gunAnim?.clipTime(name) || (dry ? 2.95 : 1.98);
    this._reload = t;
    this._reloadFrom = t;
    this._reloadName = name;   // foot.js: only the empty one ends with a rack
    // WHERE THE NOISES GO. Read off the clip itself: the magazine bone leaves
    // the gun a sixth of the way in and is back by two thirds, and the empty
    // reload ends with the slide being released at the very end of it. Stored
    // as a fraction of the clip so the sound follows the animation if the cut
    // points in gunanim.js ever move.
    // ...and per weapon kind: an AK's magazine rocks out early and the rack
    // comes at the very end of its empty reload (gunanim.js specs).
    const cues = g.gunAnim?.spec?.cues?.[name];
    this._cues = cues
      ? cues.map(([at, fn]) => [at, CUE_FN[fn] ?? magIn])
      : dry ? [[0.93, slideRelease], [0.60, magIn], [0.15, magOut]] : [[0.67, magIn], [0.17, magOut]];
    g.gunAnim?.play(name, { force: true });
    g.foot.held.obj?.userData?.anim?.play(name, { force: true });   // the magazine leaves the gun
    this._fire = 0;
    if (!auto) g._setHint?.(dry ? '🔄 გადატენვა — ჩამკეტი უკან' : '🔄 გადატენვა');
    return true;
  }

  // The trigger held or let go. A semi-automatic fires once per press (the
  // press itself calls fire()); an automatic keeps firing from update() for as
  // long as this is down.
  trigger(down) { this._trigger = !!down; }

  // One trigger pull.
  fire() {
    const g = this.game;
    if (!g.foot?.active || !g.foot.held) return false;
    const cart = this.cart;
    if (this._reload > 0) return false;           // both hands are busy
    if (this._cool > 0) return false;
    // IN COVER the weapon only fires once it is actually out from behind the
    // wall -- leaning round the corner, up over a low wall, or the blind arm
    // round the edge (foot.js). Behind the brick the trigger does nothing.
    if (g.foot?._cover && !g.foot.coverCanFire()) return false;
    // Click on an empty magazine: GTA reloads for you rather than making you
    // find the key, and the shot is simply lost -- but the hammer falls on
    // nothing first, and you hear it.
    if (this.magSize && this.ammo <= 0) {
      dryFire(g.sound?.volume ?? 0.7);
      this._trigger = false;                      // an automatic does not auto-cycle a reload
      this.reload(true);
      return false;
    }
    this._cool = cart.rate;

    const near = g.world.collidablesNear
      ? g.world.collidablesNear(g.camera.position.x, g.camera.position.z)
      : g.world.collidables;
    let targets = g.world.shootables?.length ? near.concat(g.world.shootables) : near;
    // ...and the aeroplane the hero is standing beside, which is not in that
    // set on purpose (game.ownPlaneTargets explains why). Without it a round
    // fired at your own machine from five paces went straight through it.
    const own = g.ownPlaneTargets?.();
    if (own) targets = targets.concat(own);

    // WHERE THE CROSSHAIR IS. The ray goes out of the camera through the
    // cursor, so the shot lands on whatever the crosshair is covering; then it
    // is re-aimed FROM THE MUZZLE at that point, because the barrel sits half
    // a metre off the camera's line and firing parallel to it misses
    // everything close by exactly that offset.
    this.aimDir(this._camDir);
    // A director names the point outright -- a film's rounds land where the cut
    // needs them to, not where a crosshair happens to be sitting.
    let aimPoint = g.cinema?.active ? g.cinema.aimPoint : null;
    // (blind: straight down the arm's line, there is no sight picture)
    const blind = !!g.foot?.aimOverride;
    if (!aimPoint && blind) aimPoint = this._aim.copy(this._muzzle()).addScaledVector(this._camDir, RANGE);
    if (!aimPoint) {
      this._ray.setFromCamera(this._ndc.set(g.aim?.ndcX ?? 0, g.aim?.ndcY ?? 0), g.camera);
      this._ray.far = RANGE + CAM_BACK;
      const seen = this._ray.intersectObjects(targets, true)[0];
      this._ray.far = Infinity;
      aimPoint = seen
        ? seen.point
        : this._aim.copy(this._ray.ray.origin).addScaledVector(this._camDir, RANGE + CAM_BACK);
    }

    const origin = this._muzzle();
    this._dir.subVectors(aimPoint, origin).normalize();

    // ZERO. The sights sit above the bore and are set so point of aim meets
    // point of impact at 25 m, which means the barrel is laid very slightly
    // UP -- about 0.05 deg for a 9 mm. Without this every shot lands low,
    // because the round starts falling the instant it leaves the muzzle.
    const drop = (9.81 * ZERO) / (2 * cart.v0 * cart.v0);
    this._dir.y += drop;

    // SPREAD. A cone, not a plane: aimed it is a few mrad, from the hip it is
    // wide, and it BLOOMS with every shot while the muzzle is still coming back
    // down -- which is why emptying the magazine as fast as you can click is
    // worse than shooting at a pace the sights can keep up with. Moving opens
    // it, ducking closes it: the first round of a burst from a crouch is the
    // most accurate shot the weapon has.
    const foot = g.foot;
    const moving = foot ? Math.min(1, Math.abs(foot.speed ?? 0) / (foot.runSpeed || 6.5)) : 0;
    const stance = foot?.crouched ? CROUCH_CONE : 1;
    const spread = ((this.aiming ? AIM_SPREAD : HIP_SPREAD) + this._bloom + MOVE_SPREAD * moving) * stance
      + (blind ? BLIND_SPREAD : 0);
    const a = Math.random() * Math.PI * 2;
    const r = Math.sqrt(Math.random()) * spread;
    this._up.set(0, 1, 0);
    this._right.crossVectors(this._dir, this._up).normalize();
    this._up.crossVectors(this._right, this._dir).normalize();
    this._dir.addScaledVector(this._right, Math.cos(a) * r)
      .addScaledVector(this._up, Math.sin(a) * r)
      .normalize();
    this._bloom = Math.min(BLOOM_MAX, this._bloom + (cart.bloom ?? BLOOM));
    // where in the burst this round is
    if (this._t - this._lastShot > BURST_GAP) { this._burst = 0; this._drift = 0; }
    this._lastShot = this._t;
    this._burst++;

    // Away it goes, as an object with mass that now has to fly there.
    g.bullets?.spawn(cart, origin, this._dir, targets, { from: 'pistol' });

    // Muzzle flash, scaled by how much powder the cartridge actually burns.
    this.flash.position.copy(origin);
    this.flash.visible = true;
    this.flash.material.rotation = Math.random() * Math.PI * 2;
    this.flash.scale.setScalar(Math.min(0.8, 0.26 + 0.2 * (cart.energy / 580)) * (0.8 + Math.random() * 0.5));
    this._flash = FLASH_T;

    // Powder smoke drifting off the muzzle, and the case out of the port.
    const fx = g.impacts;
    if (fx) {
      fx.debris.emit(origin, 4, {
        tint: 0x8e8b86, dir: this._dir, cone: 0.7, speed: 1.6, size: 0.05,
        up: 0.5, life: 0.55, grav: -0.6, drag: 3.4, spread: 0.02,
      });
      this._ejectCase(cart);
    }
    gunReport(cart, g.sound?.volume ?? 0.7);

    // RECOIL. With the Beretta clip loaded, THE CLIP IS THE RECOIL: its own
    // arm jolt and muzzle rise go over the hold (fireBlend, foot.js) and its
    // slide cycles on the gun. Stacking the game's procedural kick on top --
    // the weapon snapped back in the hand, the camera yanked up and jittered
    // sideways, the arms re-solved to the jumped aim -- read as the whole
    // hero vibrating on every shot. So with the clip playing the weapon kick
    // and the yaw jitter are off, and the camera climb is a feel, not a
    // throw. Without the clip (no animation file) the old recoil stands in.
    const clipShot = !!g.gunAnim?.ready;
    // THE PATTERN. Rounds 2-5 of a burst kick hardest (`climb`), then it
    // settles into a sideways walk: the drift is a random walk per shot, so a
    // burst leans one way for a while and then the other, the way a real
    // string of fire does, rather than jittering about the mean.
    const n = this._burst;
    const pat = n >= 2 && n <= 5 ? (cart.climb ?? 1) : 1;
    const kick = cart.recoil * pat * (this.aiming ? 0.85 : 1) * (g.foot?.crouched ? 0.8 : 1);
    this._drift = THREE.MathUtils.clamp(this._drift + (Math.random() - 0.5) * (cart.recoilH ?? 0) * 1.6,
      -(cart.recoilH ?? 0) * 1.5, (cart.recoilH ?? 0) * 1.5);
    const side = n === 1 ? 0 : this._drift;
    // the view goes up, and only `recover` of that comes back on its own
    this._recoil += kick * (cart.recover ?? 0.75);
    g.look.pitch = THREE.MathUtils.clamp(g.look.pitch + kick, -1.2, 1.2);
    g.look.yaw += side;                      // consumed by the body's turn next frame
    this._kickB += cart.kickBack ?? 0.012;   // back into the hands
    this.rock = Math.min(0.14, this.rock + (cart.rock ?? 0));   // ...and the body takes the rest
    this._shake = 1;
    if (!clipShot) this._kick += kick * 2.6;

    // One out of the magazine.
    if (this.magSize) this.ammo = Math.max(0, this.ammo - 1);
    // THE SHOT, SEEN. The clip's own recoil on the arms -- two-handed down the
    // sights, one-handed from the hip -- and on the gun the slide cycling,
    // or on the last round going back and staying there, which is what tells
    // you the magazine is empty before the counter does.
    const last = !!this.magSize && this.ammo === 0;
    const ga = g.gunAnim;
    if (ga?.ready) {
      const arms = last ? 'fireLast' : this.aiming ? 'aimFire' : 'hipFire';
      ga.play(arms, { force: true, fade: 0.03 });
      this._fire = this._fireFrom = ga.clipTime(arms) || 0.5;
    }
    g.foot.held.obj?.userData?.anim?.play(last ? 'fireLast' : 'fire', { force: true, fade: 0.02 });
    return true;
  }

  // The spent case, out of the ejection port on the right of the slide, up and
  // back -- which is where a 92FS throws brass.
  _ejectCase(cart) {
    const held = this.game.foot?.held?.obj;
    if (!held) return;
    held.updateWorldMatrix(true, false);
    const port = this._port();
    // the weapon's own axes: +X is the muzzle, +Y the top of the slide, +Z right
    const right = this._v.set(0, 0, 1).transformDirection(held.matrixWorld).clone();
    const up = this._v.set(0, 1, 0).transformDirection(held.matrixWorld).clone();
    const back = this._v.set(-1, 0, 0).transformDirection(held.matrixWorld).clone();
    const vel = right.multiplyScalar(2.2 + Math.random() * 1.1)
      .addScaledVector(up, 1.9 + Math.random() * 0.9)
      .addScaledVector(back, 0.5 + Math.random() * 0.6);
    this.game.impacts.casings.eject(port, vel, cart.case);
  }

  // Where the barrel ends, in world space. Measured off the model's own
  // bounding box (weapons.js records it when the gun is first drawn), so each
  // weapon's flash and bullet start at ITS muzzle and not at a guessed offset.
  _muzzle() {
    const held = this.game.foot?.held;
    const obj = held?.obj;
    if (!obj) return this.game.camera.position.clone();
    obj.updateWorldMatrix(true, false);
    const m = held._muzzle ?? { x: 0.16, y: 0, z: 0 };
    return this._v.set(m.x, m.y, m.z).applyMatrix4(obj.matrixWorld).clone();
  }

  _port() {
    const held = this.game.foot?.held;
    const obj = held?.obj;
    if (!obj) return this.game.camera.position.clone();
    const p = held._port ?? { x: 0.02, y: 0.05, z: 0.012 };
    return this._v.set(p.x, p.y, p.z).applyMatrix4(obj.matrixWorld).clone();
  }

  update(dt) {
    this._t += dt;
    this._magazine(dt);
    if (this._cool > 0) this._cool -= dt;
    // AUTOMATIC: the button is still down and the action has cycled
    if (this._trigger && this.cart.auto && this._cool <= 0 && this._reload <= 0
      && this.game.foot?.held) this.fire();
    if (this._fire > 0) this._fire = Math.max(0, this._fire - dt);
    if (this._flash > 0) {
      this._flash -= dt;
      if (this._flash <= 0) this.flash.visible = false;
    }
    // the cone settles, the sights come back down, the weapon drops back level
    if (this._bloom > 0) this._bloom = Math.max(0, this._bloom - BLOOM_COOL * dt);
    if (this._recoil > 0.00002) {
      // a rifle's sights come back slower than a pistol's: it is heavier
      const back = this._recoil * (1 - Math.exp(-RECOIL_BACK * (this.cart.auto ? 0.6 : 1) * dt));
      this._recoil -= back;
      this.game.look.pitch -= back;
    } else {
      this._recoil = 0;
    }
    // THE WEAPON INTO THE HANDS: foot.js has seated it for this frame, so a
    // push back along its own bore here is what survives to the draw
    if (this.rock > 0.0005) this.rock *= Math.exp(-11 * dt); else this.rock = 0;
    if (this._kickB > 0.0005) {
      const obj = this.game.foot?.held?.obj;
      if (obj) obj.translateX(-this._kickB);
      this._kickB *= Math.exp(-KICKB_BACK * dt);
    } else {
      this._kickB = 0;
    }
    // SWAY AND SHAKE, as offsets the camera adds to where it looks -- so the
    // crosshair, the arms and the round all go where the view has wandered.
    const foot = this.game.foot;
    const armed = !!foot?.held;
    const moving = foot ? Math.min(1, Math.abs(foot.speed ?? 0) / (foot.runSpeed || 6.5)) : 0;
    const amp = armed && this.aiming
      ? SWAY_AMP * (this.cart.sway ?? 1) * (1 + SWAY_MOVE * moving) * (foot?.crouched ? 0.55 : 1) : 0;
    const t = this._t;
    const wantY = amp * (Math.sin(t * 0.9) * 0.7 + Math.sin(t * 2.3 + 1.1) * 0.3);
    const wantP = amp * (Math.sin(t * 1.4 + 0.6) * 0.6 + Math.sin(t * 3.1) * 0.4);
    const e = 1 - Math.exp(-6 * dt);
    let sy = this.swayYaw + (wantY - this.swayYaw) * e;
    let sp = this.swayPitch + (wantP - this.swayPitch) * e;
    if (this._shake > 0.01) {
      sy += (Math.random() - 0.5) * SHAKE_AMP * this._shake;
      sp += (Math.random() - 0.5) * SHAKE_AMP * this._shake;
      this._shake *= Math.exp(-dt / SHAKE_TAU);
    } else {
      this._shake = 0;
    }
    this.swayYaw = sy;
    this.swayPitch = sp;
    // the crosshair opens with the cone: what you see is what you get
    if (armed) {
      const spread = (this.aiming ? AIM_SPREAD : HIP_SPREAD) + this._bloom + MOVE_SPREAD * moving;
      const k = (this.aiming ? 0.6 : 1) * (1 + Math.min(1.6, spread / 0.05));
      this.el.style.transform = `scale(${k.toFixed(3)})`;
    }
    if (this._kick > 0.00002) {
      this._kick *= Math.exp(-KICK_BACK * dt);
      // foot.js re-seats the weapon every frame BEFORE this runs, so adding the
      // kick here is what survives to the draw
      const obj = this.game.foot?.held?.obj;
      if (obj) obj.rotateZ(this._kick);
    } else {
      this._kick = 0;
    }
  }

  // ---- the magazine -------------------------------------------------------
  // Drawing a weapon hands you a full one, and swapping weapons swaps the
  // count with it -- the Makarov's 8 and the Beretta's 15 are not the same
  // magazine, so the count is tied to the cartridge in the hand.
  _magazine(dt) {
    const g = this.game;
    const held = g.foot?.held;
    const id = held ? held.cartridge : null;
    if (id !== this._lastCart) {
      this._lastCart = id;
      this.ammo = id ? this.magSize : null;
      this._reload = 0;
      this._fire = 0;
      this._cues.length = 0;   // or the old weapon's magazine lands in the new one
    }
    if (this._reload > 0) {
      this._reload = Math.max(0, this._reload - dt);
      const done = 1 - this._reload / this._reloadFrom;   // 0..1 through the move
      // Cues are held soonest-last so each one is a pop off the end.
      while (this._cues.length && done >= this._cues[this._cues.length - 1][0]) {
        this._cues.pop()[1](g.sound?.volume ?? 0.7);
      }
      if (this._reload === 0) this.ammo = this.magSize;
    }
    g.weapons?.setAmmo(this.ammo, this.magSize, this._reload > 0);
  }

  dispose() {
    this.el.remove();
    this.flash.material.map?.dispose();
    this.flash.material.dispose();
    this.flash.parent?.remove(this.flash);
  }
}

function flashTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const x = c.getContext('2d');
  const grd = x.createRadialGradient(32, 32, 0, 32, 32, 32);
  grd.addColorStop(0, 'rgba(255,255,230,1)');
  grd.addColorStop(0.35, 'rgba(255,200,110,0.75)');
  grd.addColorStop(1, 'rgba(255,150,60,0)');
  x.fillStyle = grd;
  x.fillRect(0, 0, 64, 64);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
