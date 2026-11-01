// The same scenario unity/Assets/Editor/Probes/FlyProbe.cs runs, but through the
// ORIGINAL src/aero.js. This settles whether the C# port behaves differently or
// the probe's EXPECTATION is unrealistic — a level aeroplane with no elevator
// input should sink, so "climbs with zero stick" may simply be a wrong assertion.
import * as THREE from 'three';
import { FlightModel } from '../src/aero.js';

const def = { prop: true, clMax: 1.65, gBreak: 10, tw: 2.6, maxSpeed: 210 };
const plane = new THREE.Object3D();
plane.userData.size = 9.83; plane.userData.halfSpan = 5.64;
plane.position.set(0, 300, 0);
const m = new FlightModel(plane, def);
console.log(`JSFLY vStall=${m.vStall.toFixed(2)} mass=${m.mass.toFixed(0)} S=${m.S.toFixed(2)} nAuth=${m.nAuth.toFixed(2)}`);

const vel = new THREE.Vector3(0, 0, 0);
const rates = new THREE.Vector3(0, 0, 0);
const struct = { wingL: 1, wingR: 1, tail: 1 };
const dt = 1 / 60;

let stick = { pitch: 0, roll: 0, rudder: 0, throttle: 1 };
for (let f = 0; f <= 300; f++) {
  if (f % 60 === 0) {
    console.log(`JSFLY phase=accel frame=${f} speed=${vel.length().toFixed(2)} ` +
                `alt=${plane.position.y.toFixed(2)} alpha=${m.alpha.toFixed(4)} ` +
                `N=${m.n.toFixed(3)} stalled=${m.stalled}`);
  }
  m.update(dt, stick, plane, vel, rates, struct, plane.position.y, 1);
  plane.position.addScaledVector(vel, dt);
}
const altAfterAccel = plane.position.y;

stick = { pitch: 1, roll: 0, rudder: 0, throttle: 1 };
let maxN = 0, everStalled = false;
for (let f = 0; f < 240; f++) {
  m.update(dt, stick, plane, vel, rates, struct, plane.position.y, 1);
  plane.position.addScaledVector(vel, dt);
  maxN = Math.max(maxN, Math.abs(m.n));
  if (m.stalled) everStalled = true;
}
console.log(`JSFLY accel_endAlt=${altAfterAccel.toFixed(2)} sank=${(300 - altAfterAccel).toFixed(2)}`);
console.log(`JSFLY pull maxN=${maxN.toFixed(3)} everStalled=${everStalled}`);
