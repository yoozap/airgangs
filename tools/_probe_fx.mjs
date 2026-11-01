// Pure-math reimplementation of the VFX curves in src/fire.js, src/blast.js
// and src/crash.js — the Unity port's Shaders/Fireball.shader,
// Shaders/ImpactSprite.shader, Shaders/Blast.shader and Scripts/Fx/FireSystem.cs
// carry these same formulas (cited inline, both sides). No THREE/Playwright
// dependency on purpose, same convention as tools/_probe_buildingquality.py:
// these are deterministic CPU curves, not something that needs a running page
// or a GPU readback to sample.
//
//   node tools/_probe_fx.mjs
//
// Printed KEY=value lines are diffed against Assets/Editor/Probes/FxProbe.cs's
// output (run once the verifier has editor access) via tools/unity_port_check.py.
// Every line gets a distinct textual `id` per that checker's own requirement
// (it blanks standalone numbers but keeps digits inside identifiers).

function smoothstep(lo, hi, x) {
  const t = Math.max(0, Math.min(1, (x - lo) / (hi - lo)));
  return t * t * (3 - 2 * t);
}
const pid = (v) => Math.round(v * 100);
const SAMPLES = [0, 0.25, 0.5, 0.75, 1];

// ---- constants (src/fire.js:12-16, src/smoke.js:5-6, src/rockets.js:22-23,251,
// src/blast.js:13-15, src/guns.js:13-18,32,41) ----
console.log(`FXCONST id=fireconst fqmax=900 emax=700 elife=1.3000 smax=4200 flmax=24 gmax=1024`);
console.log(`FXCONST id=smokeconst max=140 life=1.9000`);
console.log(`FXCONST id=rockettrailconst max=360 life=2.4000 puff=0.0160`);
console.log(`FXCONST id=blastconst fbmax=7 ringmax=6 cratermax=300`);
console.log(`FXCONST id=gunconst rate=12.0000 range=1100.0000 bulletspeed=850.0000 maxtracers=48 flashdur=0.0450`);

// ---- src/blast.js:400 fireball scale(p) = R*(0.35 + 0.85*p^0.45), R=1 ----
for (const p of SAMPLES) {
  const scale = 0.35 + 0.85 * Math.pow(p, 0.45);
  console.log(`FXCURVE id=blastball_${pid(p)} p=${p.toFixed(2)} scale=${scale.toFixed(6)}`);
}
// ---- src/blast.js:425 ring scale(p) = R*(0.2 + 1.1*p^0.6), R=1 ----
for (const p of SAMPLES) {
  const scale = 0.2 + 1.1 * Math.pow(p, 0.6);
  console.log(`FXCURVE id=blastring_${pid(p)} p=${p.toFixed(2)} scale=${scale.toFixed(6)}`);
}
// ---- src/blast.js:411-414 flash envelope, dur=0.5, sampled at fixed t=p*dur ----
for (const p of SAMPLES) {
  const t = p * 0.5;
  const f = (1 - p) * (1 - p) * (0.75 + 0.25 * Math.sin(t * 47));
  console.log(`FXCURVE id=blastflash_${pid(p)} p=${p.toFixed(2)} f=${f.toFixed(6)}`);
}

// ---- src/crash.js:60-78 fireball heat/alpha, vD fixed at 0.4, t swept 0..1 ----
for (const t of SAMPLES) {
  const vD = 0.4;
  const heat = Math.max(0, Math.min(1, vD * 1.3 - t * 1.15 + 0.25));
  const body = smoothstep(0.14, 0.45, vD + 0.3 * (1 - t));
  let a = Math.min(Math.pow(1 - t, 2.2) * body * 2.2, 0.97);
  a *= 1 - smoothstep(0.5, 0.75, t);
  console.log(`FXCURVE id=crashfire_${pid(t)} t=${t.toFixed(2)} heat=${heat.toFixed(6)} alpha=${a.toFixed(6)}`);
}

// ---- src/fire.js:147 ember pointSize shape: (0.5 - 0.3*life)/0.5 == 1 - 0.6*life ----
for (const life of SAMPLES) {
  const shape = 1 - 0.6 * life;
  console.log(`FXCURVE id=embersize_${pid(life)} life=${life.toFixed(2)} shape=${shape.toFixed(6)}`);
}
// ---- src/fire.js:214 smoke growth: 0.35 + 0.65*life ----
for (const life of SAMPLES) {
  const grow = 0.35 + 0.65 * life;
  console.log(`FXCURVE id=smokegrow_${pid(life)} life=${life.toFixed(2)} grow=${grow.toFixed(6)}`);
}
// ---- src/fire.js:277 flash swell: 0.6 + 1.1*life ----
for (const life of SAMPLES) {
  const swell = 0.6 + 1.1 * life;
  console.log(`FXCURVE id=flashswell_${pid(life)} life=${life.toFixed(2)} swell=${swell.toFixed(6)}`);
}
// ---- src/impactfx.js:468 ParticleCloud size: 0.45 + life*1.6 ----
for (const life of SAMPLES) {
  const m = 0.45 + life * 1.6;
  console.log(`FXCURVE id=partcloudsize_${pid(life)} life=${life.toFixed(2)} m=${m.toFixed(6)}`);
}
