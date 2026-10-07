import * as THREE from 'three';
import { footstep, landing } from './audiofx.js';
import { classifySurface } from './ballistics.js';

// FOOTSTEPS, read off the feet.
//
// A step is a sound at the moment a foot lands, so the feet are watched: each
// foot bone's height over the hero's own ground, and the step fires on the
// frame it comes down through CONTACT_H after having been up past LIFT_H.
// That ties the sound to the animation by construction -- the slow walk, the
// run, the sprint, the crouched creep and the stroll all run the same clip at
// different rates, and the steps follow whatever rate the feet are actually
// moving at. Bodies with no legs to read (the rigless ones, which hop) get a
// cadence from their speed instead.
//
// What it sounds like is decided by the GROUND: one ray straight down per
// step, classified the way a bullet strike is (ballistics.classifySurface),
// so concrete, asphalt, grass, soil, metal, wood and water each have their own
// recordings (public/audio/guns/step_*, tools/fetch_gun_audio.py) -- and by the
// GAIT: a crouch is "silence mode" and barely whispers, a walk is a soft heel
// and toe, a run is the full slap of a shoe, a sprint heavier still.
const CONTACT_H = 0.075;   // m above the ground a foot counts as down
const LIFT_H = 0.14;       // m it has to rise before the next contact counts
const MIN_SPEED = 0.3;     // m/s — shuffling on the spot is not a step
const CADENCE = { crouch: 1.6, walk: 1.9, run: 2.9, sprint: 3.4 };   // steps/s, the hop bodies
const SURF_RECHECK = 0.5;  // s between ground checks while moving

const ROLE = {
  concrete: 'step_concrete', asphalt: 'step_concrete', masonry: 'step_concrete', stone: 'step_concrete',
  glass: 'step_concrete', grass: 'step_grass', foliage: 'step_grass', soil: 'step_dirt',
  metal: 'step_metal', wood: 'step_wood', water: 'step_water',
};

const _v = new THREE.Vector3();
const _down = new THREE.Vector3(0, -1, 0);

export class Footsteps {
  constructor(game) {
    this.game = game;
    this._ray = new THREE.Raycaster();
    this._feet = null;
    this._rigFor = null;
    this._state = { L: 'up', R: 'up' };
    this._phase = 0;
    this._alt = 'L';
    this._surface = 'concrete';
    this._surfT = 0;
    this.log = null;        // a probe sets [] here and gets every step appended
  }

  _gait(foot) {
    const s = Math.abs(foot.speed);
    if (foot.crouched) return 'crouch';
    if (foot._stroll || s < 2.6) return 'walk';
    return s > foot.runSpeed * 1.25 ? 'sprint' : 'run';
  }

  // The ground under the hero, as a surface name. One ray, cached for half a
  // second: the terrain class says grass, soil or water where nothing is
  // built; anything built -- a road tile, a roof, the apron, a bridge deck --
  // is read off what the ray hits, exactly as a bullet strike is.
  _surfaceAt(foot) {
    const g = this.game;
    const p = foot.pos;
    const w = g.world;
    if (w?.boreFloorAt?.(p.x, p.z, p.y) != null) return 'concrete';
    const near = w?.collidablesNear ? w.collidablesNear(p.x, p.z) : (w?.collidables ?? []);
    this._ray.set(_v.set(p.x, p.y + 1.2, p.z), _down);
    this._ray.far = 2.5;
    const hits = this._ray.intersectObjects(near, true);
    this._ray.far = Infinity;
    for (const h of hits) {
      if (h.point.y > p.y + 0.35) continue;
      return classifySurface(h, w);
    }
    const cl = w?.groundClassAt?.(p.x, p.z) ?? 0;
    return cl === 2 ? 'water' : cl === 1 ? 'grass' : 'soil';
  }

  _reset() {
    this._state.L = this._state.R = 'up';
    this._phase = 0.6;
  }

  update(dt, foot) {
    const rig = foot?.rig;
    if (!rig || !foot.active) return;
    const speed = Math.abs(foot.speed);
    if (!foot.grounded || foot._climb || foot._roll || foot._slide || foot._dive || speed < MIN_SPEED) {
      this._reset();
      return;
    }
    const gait = this._gait(foot);
    this._surfT -= dt;
    if (this._surfT <= 0) { this._surface = this._surfaceAt(foot); this._surfT = SURF_RECHECK; }
    if (this._rigFor !== rig) {
      this._feet = rig._legPair?.() ?? null;
      this._rigFor = rig;
      this._reset();
    }
    if (this._feet) {
      for (const side of ['L', 'R']) {
        const b = this._feet[side];
        if (!b) continue;
        b.updateWorldMatrix(true, false);
        const h = _v.setFromMatrixPosition(b.matrixWorld).y - foot.pos.y;
        const st = this._state[side];
        if (st === 'up' && h < CONTACT_H) { this._state[side] = 'down'; this._step(foot, gait, side); }
        else if (st === 'down' && h > LIFT_H) this._state[side] = 'up';
      }
    } else {
      // no feet to read: a cadence from the speed, alternating
      this._phase += dt * CADENCE[gait] * Math.min(1.3, Math.max(0.6, speed / (gait === 'walk' ? 1.9 : foot.runSpeed)));
      if (this._phase >= 1) {
        this._phase -= 1;
        this._alt = this._alt === 'L' ? 'R' : 'L';
        this._step(foot, gait, this._alt);
      }
    }
  }

  _step(foot, gait, side) {
    const role = ROLE[this._surface] ?? 'step_concrete';
    this.log?.push({ t: performance.now(), side, gait, surface: this._surface, role });
    footstep(role, this._surface, gait, this.game.sound?.volume ?? 0.7);
  }

  // Coming down off a jump or a drop: both feet at once, and harder the
  // further it was.
  land(foot, drop = 0) {
    this._surface = this._surfaceAt(foot);
    this._surfT = SURF_RECHECK;
    const role = ROLE[this._surface] ?? 'step_concrete';
    this.log?.push({ t: performance.now(), side: 'both', gait: 'land', surface: this._surface, role, drop });
    landing(role, this._surface, Math.min(1, 0.45 + drop * 0.25), this.game.sound?.volume ?? 0.7);
    this._reset();
    this._state.L = this._state.R = 'down';
  }
}
