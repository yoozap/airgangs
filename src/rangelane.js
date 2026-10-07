import * as THREE from 'three';
import { railMotor, railClunk } from './audiofx.js';

// THE TARGET CARRIER, and the box of buttons that drives it.
//
// Every indoor range works the same way: the target hangs from a trolley on an
// overhead rail, and a control box at the firing point sends it out to whatever
// distance you want and brings it back to be read or changed. That is the thing
// this adds -- walk up to a lane and the controls appear; hold F (or the up
// arrow on the card) and the target runs away down the lane, hold G (or the
// down arrow) and it comes back to you, all the way in to 0 m, with the motor
// running the whole time and the hanger clunking into its end stop when it
// gets there. The motor is a real recording and it rides on the carrier, so it
// goes quiet and dull as the target leaves: see railMotor in src/audiofx.js.
//
// The carriers themselves are built in src/airport.js: each lane's hanger and
// its sheet are pulled out of the range's baked furniture into a group of their
// own, which is what lets one lane move while the other nineteen do not.
const REACH = 3;          // m — how close you stand before the controls appear
// A commercial retriever's "fast" setting, not its slow one: 11 m of lane in
// about three and a half seconds instead of nine. Waiting out the slow one
// between strings is the single most tedious thing about a range.
const SPEED = 3.4;        // m/s — a brisk jog, which is what the fast setting is
const ACCEL = 7.5;        // m/s² — a geared motor, still not a slingshot
const NEAR_STOP = 0;      // m — it comes all the way in, to 0 m at the firing line
const END_PAD = 0.35;     // m — and how near the backstop it will go
const TAP = 4;            // m — a tap (rather than a hold) sends it this far
const RATE = 0.65;        // s — a press shorter than this counts as a tap

// ---- the sheet, which is paper ---------------------------------------------
// A target hangs from its top edge off a hanger that is about to be yanked
// down a lane at walking-to-jogging pace, and paper does not go quietly: it
// lags behind when the carrier takes off, trails back against the air for as
// long as it is moving, flutters in its own draught, and swings forward and
// settles when the carrier stops. All of that is one pendulum whose "down" is
// moved by the carrier's acceleration and by the air it is being dragged
// through.
const G = 9.81;
const PAPER_DROP = 0.30;  // m — centre of the sheet below the edge it hangs from
// Damping ratio. Paper is light and the air gets a lot of purchase on a flat
// sheet, so it overshoots once or twice and is done -- not six times like a
// pendulum on a wire. Set where the launch peaks just past its own
// equilibrium (37 deg under the drive's 7.5 m/s2) instead of into the clamp.
const PAPER_ZETA = 0.5;
// m/s² of backward push per (m/s)² of speed. Set so a sheet at the full 3.4 m/s
// trails by about ten degrees, which is what a target on a moving carrier does.
const PAPER_DRAG = 0.15;
const PAPER_AMAX = 26;    // m/s² — see _sway: a dead stop is otherwise infinite
// A sanity backstop, not a limiter: the honest equilibrium under the drive's
// 7.5 m/s² is atan(7.5/9.81) = 37°, so anything near that has to be reachable
// or the launch is being clipped rather than simulated.
const PAPER_MAX = 0.85;   // rad — past this it is not hanging any more
const PAPER_FLUT = 0.035; // rad — the flutter of the sheet in its own draught

export class RangeLanes {
  constructor(game) {
    this.game = game;
    this.lanes = [];
    this.near = null;      // the lane the hero is standing at
    this._el = null;
    this._v = new THREE.Vector3();
    this._dir = 0;         // -1 coming back, +1 going out, 0 stopped
    this._held = 0;        // which way the button is being held
    this._since = 0;       // how long it has been held, for tap vs hold
    this._goal = null;     // where a tap is sending it
    this._motor = null;
  }

  // Called once per carrier as the range is built. `firingZ` is where the
  // firing line sits in the range unit's own frame; the carrier is parked at
  // the far end, which is where its travel ends.
  adopt(carrier, { firingZ = 0, endZ = null, axis = 'z' } = {}) {
    if (!carrier || this.lanes.some((l) => l.obj === carrier)) return null;
    carrier.updateWorldMatrix(true, false);
    const col = axis === 'x' ? 0 : 2;
    // the rail, in the world, pointing DOWN the lane (away from the firing line)
    const rail = new THREE.Vector3().setFromMatrixColumn(carrier.matrixWorld, col).normalize();
    const home = new THREE.Vector3().setFromMatrixPosition(carrier.matrixWorld);
    // WHERE THE TARGET IS, NOT WHERE ITS GROUP IS. A carrier group is built by
    // `new THREE.Group()` and filled with `attach()` (see src/airport.js), and
    // attach leaves the group where it was made -- at the unit's origin -- while
    // the meshes keep their own offsets inside it. So every lane in a unit had
    // the same transform, every lane worked out the same firing point, and
    // "the lane you are standing at" always came out as whichever one the loop
    // reached first: you walked up to lane 7 and lane 1's target drove away.
    // The lane's station has to be measured from what is INSIDE the group.
    const box = new THREE.Box3().setFromObject(carrier);
    const spot = box.isEmpty() ? home.clone() : box.getCenter(new THREE.Vector3());
    // firingZ/endZ are given in the unit's own frame, so the station is read
    // there too -- the two range units are offset from each other in the world.
    const local = (carrier.parent ? carrier.parent.worldToLocal(spot.clone()) : spot.clone())[axis];
    const away = Math.sign(local - firingZ) || 1;
    rail.multiplyScalar(away);
    // WHERE IT CAN GO. All the way in to the firing line -- 0 m, the card
    // right in front of you to read and change, which is what the carrier is
    // for -- and out to the backstop at the far end of the unit, which is the
    // length of the lane and nothing to do with where the model happened to
    // park the carrier.
    const parked = Math.abs(local - firingZ);
    const depth = endZ === null ? parked : Math.abs(endZ - firingZ);
    const back = Math.max(0, parked - NEAR_STOP);
    // the sheets hanging on this carrier, each on its own pivot at its top edge
    const papers = [];
    carrier.traverse((o) => { if (o.name === 'range_paper') papers.push(o); });
    const lane = {
      obj: carrier, rail, home, axis, at: 0,  // `at` is metres travelled from home
      // `away` turns travel along the rail back into the unit's own +Z, which
      // is the axis the sheet swings about.
      away, papers, th: 0, thv: 0, vz: 0, ft: 0,
      min: -back, max: Math.max(0, depth - END_PAD - parked), range: parked,
      // `home` still drives the motion -- the group origin is what we move --
      // but the firing point is measured back from the TARGET, so each lane
      // gets its own, one lane's width apart from its neighbours'.
      stand: spot.clone().addScaledVector(rail, -parked),
    };
    this.lanes.push(lane);
    return lane;
  }

  // The distance a shooter would call it: firing line to target, in metres.
  _distance(lane) {
    return Math.max(0, lane.range + lane.at);
  }

  // ---- the control box ----------------------------------------------------
  _ui() {
    if (this._el) return this._el;
    const hud = this.game.ui?.hud ?? document.body;
    if (!hud) return null;
    // THE SAME CARD THE GROUND LOOT USES. Walking up to a weapon puts a card on
    // the screen with its picture and the key that takes it; walking up to a
    // firing point should feel like the same game, so this is that card with
    // the lane's controls in it instead of a pistol -- same frame, same glow,
    // same place on the screen, one notch higher so the two can both be up.
    if (!document.getElementById('lane-ctl-css')) {
      const st = document.createElement('style');
      st.id = 'lane-ctl-css';
      st.textContent = `
/* SMALL. This sits in the middle of the screen at the one moment the player
   is looking down a lane at a target, so it is a strip of controls and a
   number, not a panel: roughly half the area it had. */
#lane-ctl{position:absolute;left:50%;bottom:30%;transform:translateX(-50%);
  display:none;align-items:center;gap:7px;padding:5px 9px 5px 6px;
  background:linear-gradient(180deg,rgba(6,16,24,.82),rgba(4,10,16,.9));
  border:1px solid rgba(90,220,255,.42);border-radius:7px;
  box-shadow:0 0 14px rgba(0,180,255,.16),inset 0 0 12px rgba(0,120,180,.1);
  font-family:inherit;pointer-events:auto;z-index:40;user-select:none}
#lane-ctl .lc-keys{display:flex;flex-direction:column;gap:3px}
#lane-ctl button{width:30px;height:21px;cursor:pointer;display:flex;gap:3px;
  align-items:center;justify-content:center;font-size:11px;line-height:1;
  border:1px solid rgba(90,220,255,.5);border-radius:3px;
  background:rgba(90,220,255,.12);color:#eaffff;
  transition:background .08s,color .08s,border-color .08s,box-shadow .08s}
#lane-ctl button b{font-size:10px;font-weight:700;opacity:.75}
#lane-ctl button:hover{background:rgba(90,220,255,.26)}
/* PRESSED IS GREEN, and it stays green while the carrier is still running */
#lane-ctl button.on,#lane-ctl button:active{background:#19d67a;color:#04210f;
  border-color:#8ff0c0;box-shadow:0 0 11px rgba(25,214,122,.6)}
#lane-ctl button.on b{opacity:1}
#lane-ctl .lc-dist{color:#8fe3b0;font-size:14px;font-weight:700;
  letter-spacing:.3px;text-shadow:0 0 5px rgba(80,230,150,.35)}
#lane-ctl .lc-dist small{font-size:9.5px;font-weight:400;opacity:.85}
#lane-ctl .lc-hint{color:#7fd4ef;font-size:9.5px;margin-top:1px;opacity:.85;
  white-space:nowrap}`;
      document.head.appendChild(st);
    }
    const el = document.createElement('div');
    el.id = 'lane-ctl';
    // The key is written ON the button it does, which is the only place a
    // player looks for it. The "სამიზნის მართვა" heading is gone -- a card
    // with a distance and two arrows on it at a firing point is not something
    // that needs to announce what it is.
    el.innerHTML = `<div class="lc-keys">
  <button id="lc-out" title="სამიზნე შორს (F)">▲<b>F</b></button>
  <button id="lc-in" title="სამიზნე ახლოს (G)">▼<b>G</b></button>
</div>
<div class="lc-text">
  <div class="lc-dist" id="lc-dist">–</div>
  <div class="lc-hint" id="lc-hint">F შორს · G ახლოს</div>
</div>`;
    el.style.display = 'none';
    hud.appendChild(el);
    const bind = (id, way) => {
      const b = el.querySelector(id);
      const down = (e) => { e.stopPropagation(); e.preventDefault(); this._press(way); };
      const up = (e) => { e.stopPropagation(); this._release(way); };
      b.addEventListener('pointerdown', down);
      b.addEventListener('pointerup', up);
      b.addEventListener('pointerleave', up);
      b.addEventListener('pointercancel', up);
    };
    bind('#lc-out', 1);
    bind('#lc-in', -1);
    this._el = el;
    return el;
  }

  // F SENDS IT OUT, G BRINGS IT IN. The card is there to be clicked, but
  // clicking means letting go of the mouse you are aiming with, and a real
  // controller is a box of buttons under your hand at the firing point.
  // Returns true when it took the key, so game.js can stop: F is otherwise
  // "pick the weapon up", and at a firing point the carrier wins.
  // The keys work whenever you are standing at a lane, including while the
  // card is hidden for aiming -- sending the target away without coming out
  // of the sights is the point of having them on the keyboard.
  key(code, down) {
    const way = code === 'KeyF' ? 1 : code === 'KeyG' ? -1 : 0;
    if (!way || !this.near) return false;
    if (down) { if (this._held !== way) this._press(way); } else this._release(way);
    return true;
  }

  // The window went away with F or G still down: let go, or the carrier keeps
  // running with nobody holding the button.
  releaseAll() { if (this._held) this._release(this._held); }

  // ---- the sheet, which is paper -----------------------------------------
  // One damped pendulum per carrier, hung from the top edge of its sheet. What
  // moves it is where "down" has gone: accelerate the hanger and the sheet is
  // left behind, run it at speed and the air holds it back, stop it and the
  // sheet keeps going and swings through. A bullet is the same pendulum given
  // a shove (see `hit`).
  //
  // Angle convention: the pivot sits at the sheet's top edge with no rotation
  // of its own, so `rotation.x` is the swing ALONG the lane in the range's own
  // frame -- which is why none of this has to care how the warehouse is
  // turned in the world. A positive angle takes the bottom of the sheet
  // toward -Z, which is where it goes when the carrier accelerates toward +Z.
  _sway(lane, dt) {
    if (!lane.papers?.length) return;
    const vz = (lane.v ?? 0) * lane.away;        // speed along the unit's own +Z
    // A motor that stops dead inside one frame is an acceleration of a couple
    // of hundred m/s², and taken at face value it throws the sheet flat on its
    // back. Cap it at what a geared drive can really do to its load.
    const az = THREE.MathUtils.clamp((vz - lane.vz) / Math.max(1e-4, dt),
      -PAPER_AMAX, PAPER_AMAX);
    lane.vz = vz;
    // inertia plus the air it is being dragged through, both pushing the same
    // way: backwards along the direction of travel
    const aEff = az + PAPER_DRAG * vz * Math.abs(vz);
    let want = Math.atan(aEff / G);
    // and the flutter, which is what tells you it is paper and not a board.
    // Two incommensurate frequencies, because one is a windscreen wiper.
    if (Math.abs(vz) > 0.1) {
      lane.ft += dt;
      const f = Math.min(1, (vz * vz) / 6);
      want += PAPER_FLUT * f * (Math.sin(lane.ft * 17.3) * 0.6
        + Math.sin(lane.ft * 27.7) * 0.4);
    }
    const w2 = G / PAPER_DROP;
    const w = Math.sqrt(w2);
    lane.thv += (-w2 * (lane.th - want) - 2 * PAPER_ZETA * w * lane.thv) * dt;
    lane.th = THREE.MathUtils.clamp(lane.th + lane.thv * dt, -PAPER_MAX, PAPER_MAX);
    if (Math.abs(lane.th) >= PAPER_MAX) lane.thv = 0;   // it hit the stop
    // asleep: park it exactly, so the shared updater in src/airport.js can skip
    if (!vz && Math.abs(lane.th) < 2e-4 && Math.abs(lane.thv) < 2e-3) {
      lane.th = 0;
      lane.thv = 0;
    }
    for (const p of lane.papers) p.userData.sway = lane.th;
  }

  // A ROUND THROUGH THE SHEET. Paper weighs almost nothing and a bullet goes
  // straight through it, so what a hit leaves behind is not a push -- it is
  // the little the paper manages to take out of the round on the way past.
  // `at` is the world point that was hit: a shot low on the sheet has more
  // leverage on the pivot than one through the top edge, which is exactly how
  // a hanging target behaves.
  hit(object, at, power = 1) {
    for (const lane of this.lanes) {
      if (!lane.papers?.length) continue;
      for (const p of lane.papers) {
        let o = object;
        while (o && o !== p) o = o.parent;
        if (!o) continue;
        let arm = 1;
        if (at) {
          p.updateWorldMatrix(true, false);
          const drop = Math.abs(this._v.setFromMatrixPosition(p.matrixWorld).y - at.y);
          arm = THREE.MathUtils.clamp(drop / PAPER_DROP, 0.15, 1.8);
        }
        // Toward the shooter is -Z in the unit's frame when the firing line is
        // behind the target, which `away` is exactly the sign of: a round
        // arriving from the firing point pushes the sheet further away.
        lane.thv += -lane.away * 2.6 * power * arm;
        lane.ft += 0.37;      // and knock the flutter out of step
        return true;
      }
    }
    return false;
  }

  // ---- driving it --------------------------------------------------------
  _press(way) {
    if (!this.near) return;
    this._held = way;
    this._since = 0;
    this._dir = way;
    this._goal = null;
    this._sound(true);
  }

  // Let go early and it was a TAP: the carrier carries on to a round number of
  // metres and stops there by itself, which is how a real controller's preset
  // buttons behave. Let go after a while and it stops where it is.
  _release(way) {
    if (this._held !== way) return;
    this._held = 0;
    if (this._since < RATE && this.near) {
      this._goal = THREE.MathUtils.clamp(this.near.at + way * TAP,
        this.near.min, this.near.max);
    } else {
      this._stop();
    }
  }

  _stop(clunk = false) {
    const d = this._soundDist();
    this._dir = 0;
    this._goal = null;
    this._sound(false);
    if (clunk) railClunk(this.game.sound?.volume ?? 0.7, d);
  }

  // How far away the noise is being made. The motor is ON THE CARRIER, so it
  // is as far off as the target is -- right in your face at 0 m, eleven metres
  // down the lane at the backstop. It was a flat 2.5 m before, which is why
  // the carrier used to sound exactly the same wherever it had got to.
  _soundDist() {
    return this.near ? Math.max(1, this._distance(this.near)) : 2.5;
  }

  _sound(on) {
    if (on && !this._motor) {
      this._motor = railMotor(this.game.sound?.volume ?? 0.7, this._soundDist());
    } else if (!on && this._motor) { this._motor.stop(); this._motor = null; }
  }

  // ---- the frame ---------------------------------------------------------
  // `actor` is the personage's own position: the controls belong to the lane you
  // are standing at, not to the one the camera happens to be nearest.
  update(dt, actor) {
    if (!this.lanes.length) return;
    // EVERY lane's sheet, not just the one being driven: walk away from a
    // carrier mid-run and its target still has to finish swinging.
    for (const l of this.lanes) this._sway(l, dt);
    if (!actor) return;
    // the nearest firing point, if the hero is at one at all
    // ON THE FLOOR, NOT ON THE RAIL. The carrier hangs about two metres up, so
    // its rail point is that far above the shooter's head: measured in three
    // dimensions, a man standing right at the firing point is already most of
    // the way out of reach. Only the horizontal distance means anything here.
    let best = null;
    let bestD = REACH * REACH;
    for (const l of this.lanes) {
      const dx = l.stand.x - actor.x;
      const dz = l.stand.z - actor.z;
      const d = dx * dx + dz * dz;
      if (d < bestD) { bestD = d; best = l; }
    }
    if (best !== this.near) {
      if (this._dir) this._stop();       // walked away mid-travel
      this.near = best;
      this._held = 0;
    }
    const el = this._ui();
    if (!el) return;
    // DOWN THE SIGHTS, THE CARD GOES. It sits dead centre, which is exactly
    // where the target is once you bring the gun up. The carrier keeps
    // running and F/G keep driving it -- only the card is out of the way.
    const show = best && !this.game.pistol?.aiming;
    if (!show) {
      if (el.style.display !== 'none') el.style.display = 'none';
      if (!best) return;
    } else if (el.style.display !== 'flex') el.style.display = 'flex';

    if (this._held) this._since += dt;
    const lane = best;
    let dir = this._dir;
    if (this._goal !== null) dir = Math.sign(this._goal - lane.at) || 0;
    if (dir) {
      // a geared motor: it takes a moment to get going and the same to stop
      lane.v = THREE.MathUtils.clamp((lane.v ?? 0) + dir * ACCEL * dt, -SPEED, SPEED);
      let step = lane.v * dt;
      if (this._goal !== null) {
        // ease into the preset rather than overshooting it
        const left = this._goal - lane.at;
        if (Math.abs(step) > Math.abs(left)) step = left;
      }
      const want = THREE.MathUtils.clamp(lane.at + step, lane.min, lane.max);
      const hitEnd = want !== lane.at + step;
      lane.at = want;
      lane.obj.position.copy(lane.home).addScaledVector(lane.rail, lane.at);
      lane.obj.parent?.worldToLocal(lane.obj.position);
      // the motor goes with it: re-aim the sound at wherever the target now is
      this._motor?.aim?.(this._soundDist());
      if (hitEnd) { lane.v = 0; this._stop(true); }
      else if (this._goal !== null && Math.abs(this._goal - lane.at) < 1e-4) {
        lane.v = 0;
        this._stop(true);
      }
    } else if (lane.v) lane.v = 0;

    // the readout, and the arrow that is working lights up
    const d = this._distance(lane);
    el.querySelector('#lc-dist').innerHTML = `${d.toFixed(1)} <small>მ</small>`;
    el.querySelector('#lc-hint').textContent = dir > 0 ? 'მიდის შორს…'
      : dir < 0 ? 'ბრუნდება…' : 'F შორს · G ახლოს';
    el.querySelector('#lc-out').classList.toggle('on', dir > 0);
    el.querySelector('#lc-in').classList.toggle('on', dir < 0);
  }

  dispose() {
    this._sound(false);
    this._el?.remove();
    this._el = null;
    this.lanes.length = 0;
  }
}
