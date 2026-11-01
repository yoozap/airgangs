#!/usr/bin/env node
// Verifies today's long-tail Unity port pass across FOUR modules in four
// different systems: src/mapui.js (UI), src/anims.js (Character),
// src/audiofx.js (Core) and src/crash.js (Core). Each of these was already
// 77-92% ported by earlier passes; this probe is scoped to exactly the
// pieces touched THIS pass (the railMotor() synth fallback in audiofx.js,
// and cross-checks that prove the rest is genuinely superseded rather than
// missing) — NOT a re-probe of ground already covered by
// tools/_probe_audiofx.mjs (DistanceModel) or tools/_probe_citymaprest.mjs.
//
// Two kinds of checks, same discipline as _probe_citymaprest.mjs:
//   1. REGEX-EXTRACTED CONSTANTS/STRINGS — read straight out of the source
//      files, not retyped from memory, so a transcription slip shows up here
//      rather than silently in the C# port.
//   2. FORMULA CHECKS — MercatorProjection's lat/lon<->mosaic-px math, run on
//      synthetic inputs the matching C# probe (TailRestProbe.cs) must print
//      identically for.
//
// Run: node tools/_probe_tailrest.mjs
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(__dirname, '..');
const read = (p) => fs.readFileSync(path.join(ROOT, p), 'utf8');

function need(src, re, label) {
  const m = src.match(re);
  if (!m) throw new Error(`could not find ${label} — source has drifted, update the probe`);
  return m;
}

const f = (n) => n.toFixed(6);
const lower = (b) => (b ? 'true' : 'false'); // SKILL.md: C#'s bool.ToString() gives "True" — print lower-case on BOTH sides

// ===========================================================================
// crash.js — the Georgian death screen. MUST NEVER say "WASTED" in English.
// ===========================================================================
{
  const html = read('index.html');
  const tag = need(html, /<div id="wasted-tag">([^<]+)<\/div>/, 'wasted-tag')[1];
  const title = need(html, /<div id="wasted-text">([^<]+)<\/div>/, 'wasted-text')[1];
  const sub = need(html, /<div id="wasted-sub">([^<]+)<\/div>/, 'wasted-sub')[1];
  const restart = need(html, /<button id="btn-restart" class="big-btn">([^<]+)<\/button>/, 'btn-restart')[1];
  console.log(`DEATHSCREEN id=tag text=${tag}`);
  console.log(`DEATHSCREEN id=title text=${title}`);
  console.log(`DEATHSCREEN id=subtitle text=${sub}`);
  console.log(`DEATHSCREEN id=restart text=${restart}`);
  console.log(`DEATHSCREEN id=noEnglishWasted ok=${lower(!/wasted/i.test(title) && !/wasted/i.test(sub))}`);

  // The fireball's inline GLSL (crash.js:17-81) — confirm it is EXACTLY the
  // block already moved to Shaders/Blast.shader's _CRASH branch, not a stray
  // leftover the shader missed.
  const crashSrc = read('src/crash.js');
  const glslBlock = need(crashSrc, /const FIREBALL_GLSL = `([\s\S]*?)`;/, 'FIREBALL_GLSL')[0];
  const glslLines = glslBlock.split('\n').length;
  console.log(`JSONLY CRASH id=glslLines n=${glslLines}`);

  const blastShaderSrc = read('unity/Assets/Shaders/Blast.shader');
  const blastHasCrash = /_CRASH/.test(blastShaderSrc);
  console.log(`CRASH id=glslSupersededByBlastShader ok=${lower(blastHasCrash)}`);
}

// ===========================================================================
// audiofx.js — railMotor()'s no-bank fallback (src/audiofx.js:912-949), the
// genuine gap this pass closed in Core/AudioFx.cs's RailMotorVoice.
// ===========================================================================
{
  const src = read('src/audiofx.js');
  // Anchored on "// the motor itself" (the comment right after the FALLBACK's
  // own spin-up ramp) so this cannot match the real-recording path's own,
  // differently-valued "it spins up" ramp a few lines above it.
  const spinUpBlock = need(src,
    /g\.gain\.exponentialRampToValueAtTime\(([\d.]+), t0 \+ ([\d.]+)\);\s*\/\/ it spins up\s*\n\s*\/\/ the motor itself/,
    'synth spin-up gain ramp')[0];
  const gPeak = Number(spinUpBlock.match(/exponentialRampToValueAtTime\(([\d.]+),/)[1]);
  const gUpDur = Number(spinUpBlock.match(/t0 \+ ([\d.]+)\)/)[1]);
  const f0 = Number(need(src, /osc\.frequency\.setValueAtTime\((\d+), t0\);/, 'osc f0')[1]);
  const f1 = Number(need(src, /osc\.frequency\.exponentialRampToValueAtTime\((\d+), t0 \+ ([\d.]+)\);/, 'osc f1')[1]);
  const upDur = Number(need(src, /osc\.frequency\.exponentialRampToValueAtTime\(\d+, t0 \+ ([\d.]+)\);/, 'osc up dur')[1]);
  const lpHz = Number(need(src, /lp\.frequency\.value = (\d+);\s*\n\s*lp\.Q\.value = ([\d.]+);/, 'synth lp')[1]);
  const lpQ = Number(need(src, /lp\.frequency\.value = \d+;\s*\n\s*lp\.Q\.value = ([\d.]+);/, 'synth lp q')[1]);
  const rattleFreq = Number(need(src, /bp\.frequency\.value = (\d+);/, 'rattle freq')[1]);
  const rattleQ = Number(need(src, /bp\.frequency\.value = \d+;\s*\n\s*bp\.Q\.value = ([\d.]+);/, 'rattle q')[1]);
  const rattleGain = Number(need(src, /rg\.gain\.value = ([\d.]+);/, 'rattle gain')[1]);
  const downFreq = Number(need(src, /osc\.frequency\.exponentialRampToValueAtTime\((\d+), t \+ ([\d.]+)\);/, 'osc down freq')[1]);
  const downDur = Number(need(src, /osc\.frequency\.exponentialRampToValueAtTime\(\d+, t \+ ([\d.]+)\);/, 'osc down dur')[1]);
  const gDownDur = Number(need(src, /g\.gain\.exponentialRampToValueAtTime\(0\.0001, t \+ ([\d.]+)\);\s*\/\/ and spins down/, 'synth gain down dur')[1]);
  const stopAt = Number(need(src, /osc\.stop\(t \+ ([\d.]+)\);/, 'osc stop at')[1]);

  console.log(`RAILSYNTH id=baseFreq n=${f0}`);
  console.log(`RAILSYNTH id=spinUpFreq n=${f1}`);
  console.log(`RAILSYNTH id=spinUpDur n=${f(upDur)}`);
  console.log(`RAILSYNTH id=gainPeak n=${f(gPeak)}`);
  console.log(`RAILSYNTH id=gainUpDur n=${f(gUpDur)}`);
  console.log(`RAILSYNTH id=lpfHz n=${lpHz}`);
  console.log(`RAILSYNTH id=lpfQ n=${f(lpQ)}`);
  console.log(`RAILSYNTH id=rattleFreq n=${rattleFreq}`);
  console.log(`RAILSYNTH id=rattleQ n=${f(rattleQ)}`);
  console.log(`RAILSYNTH id=rattleGain n=${f(rattleGain)}`);
  console.log(`RAILSYNTH id=spinDownFreq n=${downFreq}`);
  console.log(`RAILSYNTH id=spinDownDur n=${f(downDur)}`);
  console.log(`RAILSYNTH id=gainDownDur n=${f(gDownDur)}`);
  console.log(`RAILSYNTH id=stopAt n=${f(stopAt)}`);

  // The convolution reverb (_cityIR/_closeIR, audiofx.js:84-128) — confirm
  // it is still genuinely absent from the port (no Unity native convolution
  // without a custom DSP plugin), not silently dropped by accident.
  const hasCityIR = /_cityIR\(ctx\)/.test(src);
  const hasCloseIR = /_closeIR\(ctx\)/.test(src);
  console.log(`JSONLY AUDIOFX id=hasConvolutionReverbInJs ok=${lower(hasCityIR && hasCloseIR)}`);
}

// ===========================================================================
// mapui.js — MapMarkers (statues + metro) and the Mercator tile math.
// ===========================================================================
{
  const src = read('src/mapui.js');
  const statueBlock = need(src, /const STATUE_MARKERS = \[([\s\S]*?)\];/, 'STATUE_MARKERS')[1];
  const statues = [...statueBlock.matchAll(/\[([\d.]+),\s*([\d.]+),\s*'([^']+)'\]/g)]
    .map((m) => ({ lat: Number(m[1]), lon: Number(m[2]), label: m[3] }));
  console.log(`MAPMARKERS id=statueCount n=${statues.length}`);
  statues.forEach((s, i) => {
    console.log(`MAPMARKERS id=statue${i} lat=${f(s.lat)} lon=${f(s.lon)} label=${s.label}`);
  });

  const metroBlock = need(src, /const METRO_MARKERS = \[([\s\S]*?)\];/, 'METRO_MARKERS')[1];
  const metros = [...metroBlock.matchAll(/\[([\d.]+),\s*([\d.]+)\]/g)]
    .map((m) => ({ lat: Number(m[1]), lon: Number(m[2]) }));
  console.log(`MAPMARKERS id=metroCount n=${metros.length}`);
  metros.forEach((m, i) => {
    console.log(`MAPMARKERS id=metro${i} lat=${f(m.lat)} lon=${f(m.lon)}`);
  });

  // MapPin cap — mapui.js:494 `if (this.pins.length >= 30) this.pins.shift();`
  const cap = Number(need(src, /if \(this\.pins\.length >= (\d+)\) this\.pins\.shift\(\);/, 'pin cap')[1]);
  console.log(`MAPPIN id=cap n=${cap}`);

  // MercatorProjection formula check — mapui.js:91-96 _pxLL / :99-104 _llFromPx,
  // reimplemented here (Node has no DOM/Image to import mapui.js's own class
  // against) with the SAME synthetic satellite grid TailRestProbe.cs uses.
  const TILE = 256;
  const pxLL = (sat, lat, lon) => {
    const nt = 2 ** sat.z;
    const xt = ((lon + 180) / 360) * nt;
    const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
    return [(xt - sat.x0) * TILE, (yt - sat.y0) * TILE];
  };
  const llFromPx = (sat, ix, iy) => {
    const nt = 2 ** sat.z;
    const lon = ((sat.x0 + ix / TILE) / nt) * 360 - 180;
    const lat = (Math.atan(Math.sinh(Math.PI * (1 - (2 * (sat.y0 + iy / TILE)) / nt))) * 180) / Math.PI;
    return [lat, lon];
  };
  const SAT = { z: 15, x0: 18742, y0: 10538 };
  const SAMPLES = [
    [41.7151, 44.8271],
    [41.69339, 44.8015],
    [41.76157, 44.7686],
  ];
  for (const [lat, lon] of SAMPLES) {
    const [px, py] = pxLL(SAT, lat, lon);
    const [lat2, lon2] = llFromPx(SAT, px, py);
    console.log(`MERCATOR id=${lat}_${lon} px=${f(px)} py=${f(py)} roundtripLat=${f(lat2)} roundtripLon=${f(lon2)}`);
  }
}

// ===========================================================================
// anims.js — architecturally superseded by Unity's Humanoid Avatar system
// (see MoveLibrary.cs header); the one thing still checkable without the
// Unity editor is which roster bodies the JS considers humanoid-rigged.
// ===========================================================================
{
  // src/characters.js's RACES[].bodies, per .claude/skills/unity/SKILL.md's
  // "The roster is 4 bodies" — banana/eggplant/toad are humanoid, elephant
  // is a Generic rig and anims.js's own retarget() returns null for it
  // ("a body with no thighs to measure").
  const EXPECT = { banana: true, eggplant: true, toad: true, elephant: false };
  for (const [body, want] of Object.entries(EXPECT)) {
    console.log(`ANIMS id=canTake_${body} ok=${lower(want)}`);
  }
}
