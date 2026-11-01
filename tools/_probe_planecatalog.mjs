// The JS side of the PlaneCatalog port check. Imports the REAL src/planes.js
// table and derived-stat functions and prints one line per aircraft, in the
// same format unity/Assets/Editor/Probes/PlaneCatalogProbe.cs prints from the
// ported C# table — so the two can be diffed with tools/unity_port_check.py.
//
// Covers: the full PLANES table (every field PlaneCatalog.cs ports) plus the
// nine derived functions named in the porting brief: planeRole/isTankPlane,
// planeStamina, planeColMult, planeFuel, planeSpecSheet, planeMass,
// planeTopSpeed, planeCrashDmg.
//
//   node tools/_probe_planecatalog.mjs
import {
  PLANES, planeById, planeRole, isTankPlane, planeStamina, planeColMult,
  planeFuel, planeSpecSheet, planeMass, planeTopSpeed, planeCrashDmg,
} from '../src/planes.js';

const f = (v, n = 4) => (v === undefined || v === null ? 'null' : Number(v).toFixed(n));

// unity_port_check.py groups rows by (id, "rho0" in kv) -- give the two
// one-off lines below their own fake "id" so they don't collide with each
// other (neither carries a real plane id) or with any PLANE/SPEC line.
console.log(`COUNT id=_count n=${PLANES.length}`);

for (const def of PLANES) {
  console.log(
    `PLANE id=${def.id} role=${planeRole(def)} tank=${isTankPlane(def) ? 1 : 0} `
    + `mass=${f(planeMass(def), 2)} stamina=${planeStamina(def)} `
    + `colMult=${f(planeColMult(def), 5)} fuel=${f(planeFuel(def), 2)} `
    + `topSpeed=${f(planeTopSpeed(def), 2)} crashDmg=${planeCrashDmg(def)} `
    + `scale=${f(def.scale, 5)} maxSpeed=${f(def.maxSpeed, 3)} `
    + `yaw=${f(def.yaw ?? 0, 6)} roll=${f(def.roll ?? 0, 6)} `
    + `tw=${f(def.tw, 4)} gBreak=${f(def.gBreak, 4)} clMax=${f(def.clMax, 4)} `
    + `prop=${def.prop ? 1 : 0} props=${def.props ?? 1} `
    + `stat0=${def.stats[0]} stat1=${def.stats[1]} stat2=${def.stats[2]} stat3=${def.stats[3]}`
  );
  // spec sheet string, separately — it is text, not a number, and is diffed
  // with a plain string-equality check by unity_port_check.py
  // `id=${def.id}_spec`, NOT `id=${def.id}`: unity_port_check.py groups rows
  // by (id, "rho0" in kv), and a SPEC line sharing the PLANE line's id would
  // collide onto the SAME row — the last line read wins the dict entry, so
  // the SPEC line's sparse {id, sheet} would silently overwrite (and discard)
  // the PLANE line's ~20 numeric fields from the comparison. Caught by
  // actually running the check (`compared 3 values` instead of ~700) rather
  // than trusting the format by eye.
  console.log(`SPEC id=${def.id}_spec sheet=${planeSpecSheet(def)}`);
}

// planeById's fallback and a couple of spot checks the table itself can't
// exercise (unknown id -> PLANES[0]; isTankPlane on a TANK vs a DPS id).
console.log(`LOOKUP id=_lookup unknown=${planeById('does-not-exist').id} `
  + `an225tank=${isTankPlane('an225') ? 1 : 0} spitfiretank=${isTankPlane('spitfire') ? 1 : 0}`);

// SPEC lines carry Georgian prose with spaces and middots, which
// unity_port_check.py's `(\w+)=([^\s]+)` line parser cannot tokenize (by
// design — it is a NUMERIC relative-tolerance differ, not a text differ).
// Compare those lines with a plain exact-string diff instead, e.g.:
//   diff <(grep '^SPEC ' js.txt) <(grep '^SPEC ' cs.txt)
// A byte-exact Georgian string either matches or it does not; no tolerance
// is meaningful there.
