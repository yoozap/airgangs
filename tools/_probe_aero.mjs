// The JS side of the FlightModel port check. Builds aero.js's FlightModel for the
// same planes as unity/Assets/Editor/Probes/FlightProbe.cs and prints the same
// fields in the same format, so the two can be diffed line for line.
import { FlightModel } from '../src/aero.js';

const CASES = [
  ['spitfire', 11.23, 5.615,  { prop: true,  clMax: 1.6, gBreak: 9,   tw: 2.4, maxSpeed: 300 }],
  ['tornado',  16.72, 6.955,  { prop: false, clMax: 1.7, gBreak: 7.5, tw: 3.1, maxSpeed: 400 }],
  ['su33',     21.19, 7.35,   { prop: false, clMax: 1.9, gBreak: 9,   tw: 3.5, maxSpeed: 420 }],
  ['mig21nk',  14.1,  3.575,  { prop: false, clMax: 1.3, gBreak: 8.5, tw: 3.4, maxSpeed: 410 }],
];

const f = (v, n) => v.toFixed(n);

for (const [id, size, halfSpan, def] of CASES) {
  const plane = { userData: { size, halfSpan }, position: { y: 0 }, quaternion: null };
  const m = new FlightModel(plane, def);
  console.log(
    `AERO id=${id} mass=${f(m.mass,3)} S=${f(m.S,4)} kInd=${f(m.kInduced,6)} ` +
    `cd0=${f(m.cd0,4)} alphaStall=${f(m.alphaStall,6)} thrust0=${f(m.thrust0,2)} ` +
    `vStall=${f(m.vStall,4)} vr=${f(m.vr,4)} vne=${f(m.vne,3)} ` +
    `rollMax=${f(m.rollMax,5)} pitchMax=${f(m.pitchMax,5)} yawMax=${f(m.yawMax,5)} ` +
    `tau=${f(m.tau,5)} nAuth=${f(m.nAuth,4)}`);
  console.log(
    `AERO id=${id} rho0=${f(m.rho(0),6)} rho2k=${f(m.rho(2000),6)} ` +
    `thr0=${f(m.thrustAt(100,0),2)} thr2k=${f(m.thrustAt(100,2000),2)} ` +
    `vcap0=${f(m.vCapAt(0),3)} vcap2k=${f(m.vCapAt(2000),3)}`);
}
