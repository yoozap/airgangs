// Ground truth for Core/EngineSound.cs's RPM state machine against
// src/sound.js (500 lines). Exercises ignite() -> starting -> running only:
// shutdown() short-circuits straight to state='off' whenever `this.ctx` is
// null (src/sound.js:262), and ctx only exists after start() builds a real
// WebAudio AudioContext — not reachable from plain node without mocking
// `window`. The starting/running curves do NOT depend on ctx (the early
// `if (!this.ctx) return;` in update() sits AFTER the rev/state-machine math),
// so they run here exactly as they would in a browser.
//
//   node tools/_probe_sound.mjs
import { EngineSound } from '../src/sound.js';

const DEFS = [
  ['prop', { prop: true, tw: 2.4, maxSpeed: 300 }],
  ['jet', { prop: false, tw: 3.1, maxSpeed: 400 }],
  // afterburner voice: tw >= 2.6 && maxSpeed >= 260 (src/sound.js:58) -- still
  // the JET profile's timing (idle/spoolUp/spoolDown/startDur), only the bank
  // role picked differs, which this probe cannot observe without ctx anyway.
  ['jetAB', { prop: false, tw: 3.5, maxSpeed: 420 }],
];
const f = (n) => n.toFixed(6);
const CHECKPOINTS = [0, 30, 90, 180, 300, 420]; // frames at 60 fps

for (const [id, def] of DEFS) {
  const e = new EngineSound();
  e.setPlane(def);
  e.ignite();
  const dt = 1 / 60;
  let frame = 0;
  for (const target of CHECKPOINTS) {
    while (frame < target) {
      // throttle steps up once, mid-run -- exercises both the starting curve
      // (which ignores throttle entirely) and the running target formula.
      const throttle = frame < 200 ? 0.5 : 1.0;
      e.update(dt, { speed: 100, throttle, airborne: true });
      frame++;
    }
    console.log(`SOUNDM id=${id}_f${target} state=${e.state} rpm=${f(e.rpm)}`);
  }
}
