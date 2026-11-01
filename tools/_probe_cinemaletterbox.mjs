// Counterpart to unity/Assets/Editor/Probes/CinemaLetterboxProbe.cs — verifies
// the cinema letterbox chrome (src/cinema.js:1887-1958 `_showOverlay`/
// `_bars`/`_hideOverlay`/`dispose`), specifically:
//   1. `_bars()`'s bar-height arithmetic (cinema.js:1940-1945), at the same
//      window sizes the C# probe uses, INCLUDING two sizes chosen to land
//      exactly on a .5 rounding tie (JS's Math.round always rounds a .5 tie
//      toward +Infinity; Unity's Mathf.RoundToInt rounds to the nearest EVEN
//      integer instead — this is the whole reason the port must use
//      Data.JsMath.Round, not Mathf.RoundToInt).
//   2. The literal ESC hint string (cinema.js:1927), as the ground truth the
//      Unity-side UXML must carry verbatim.
//
// Pure arithmetic + a literal string — no browser needed, same as
// tools/_probe_crane3.mjs's own AdvanceOnWing section.
//
//   node tools/_probe_cinemaletterbox.mjs

function bars(w, h) {
  // cinema.js:1943, transcribed verbatim:
  //   const bar = Math.max(0, Math.round((h - w / 2.39) / 2));
  return Math.max(0, Math.round((h - w / 2.39) / 2));
}

const sizes = [
  ['1920x1080', 1920, 1080],
  ['2560x1440', 2560, 1440],
  ['1366x768', 1366, 768],
  ['3440x1440', 3440, 1440],
  // w=2390 makes w/2.39 == 1000.0 exactly, so these two land EXACTLY on a
  // .5 tie: h=1001 -> (1001-1000)/2 = 0.5, h=1005 -> (1005-1000)/2 = 2.5.
  ['tie0p5', 2390, 1001],
  ['tie2p5', 2390, 1005],
];

for (const [id, w, h] of sizes) {
  console.log(`BARPX id=${id} px=${bars(w, h)}`);
}

// cinema.js:1927 `<i class="ch">ESC</i>` — literal source string.
console.log('HINT id=text str=ESC');
