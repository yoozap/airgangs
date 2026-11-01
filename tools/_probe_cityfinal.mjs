// Master verification entrypoint for this task's 3-module pass (shopfronts.js,
// landmarks.js, airport.js — docs/port_units/cityfinal.json). Delegates to the
// two feature probes this pass actually wrote new code for (each already
// independently runnable and documented in its own header), then prints a
// short, honest status line for airport.js, where this pass found no new
// code to write — see the final report for why.
//
// Mirrors unity/Assets/Editor/Probes/CityFinalProbe.cs's own dispatch, same
// key names, same shapes (booleans lower-case on both sides).
//
// Run: node tools/_probe_cityfinal.mjs

import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));

function run(script) {
  console.log(`\n--- ${script} ---`);
  const out = execFileSync('node', [path.join(here, script)], { encoding: 'utf8' });
  process.stdout.write(out);
}

run('_probe_shopsigntex.mjs');
run('_probe_landmarkfacades.mjs');

// airport.js: no new file this pass. See the final report for the full
// evidence trail; short version printed here so a grep for CITYFINAL still
// finds a line for every module this task named.
console.log(`\n--- airport.js ---`);
console.log('JSONLY CITYFINAL module=airport.js newFiles=0 reason="measured_gap_is_stale_manifest_arithmetic_not_real_unported_behavior"');
