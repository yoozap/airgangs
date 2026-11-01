#!/usr/bin/env node
// Prints the JS/CSS-side ground truth for the Unity UI port
// (unity/Assets/Editor/Probes/UiProbe.cs prints the C#-side numbers in the
// same KEY=value shape). Source of truth: index.html + the 5 ported JS
// files' own id/text strings, and src/style.css's :root custom properties.
//
// This does NOT try to byte-match element-for-element against the C# probe
// (the JS builds some elements at runtime with no static id= in the HTML at
// all — e.g. skillbar.js:146 `this.el.id = 'actionbar'`). Instead it dumps
// every id, every Georgian string, and every :root token found in the
// source, so each string the Unity side carries can be checked against this
// list (a transcription error shows up as a UiProbe STR line with no match
// here), and the two sides' TOKEN values can be diffed directly.
//
// Run: node tools/_probe_ui.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(path.join(ROOT, p), 'utf8');
const lines = (text) => text.split('\n');
// 1-based, inclusive, matching each UXML's own "Port of index.html:A-B"
// header comment (Assets/UI/Target/TargetCard.uxml, .../Map/MapWindow.uxml,
// .../Settings/Settings.uxml).
const slice = (text, from, to) => lines(text).slice(from - 1, to).join('\n');

// index.html is a single page with MANY panels (login, character select, the
// HUD proper, the wasted screen, ...) and only THREE of them are ported to
// Unity so far: the target frame, the map window and the settings window.
// Scanning the WHOLE file for Georgian text compares against panels that
// have no Unity counterpart yet (e.g. the HUD's own button tooltips/cinema
// menu sit at index.html:197-238, between the target frame and the map
// window, and are untouched by this port) — that is a real scope gap, not a
// probe bug, so this probe must not claim to cover it. Scoped to exactly the
// ranges the three ported UXML files themselves cite.
const indexHtmlFull = read('index.html');
const INDEX_HTML_PORTED =
  slice(indexHtmlFull, 177, 196) + '\n' + // TargetCard.uxml
  slice(indexHtmlFull, 242, 258) + '\n' + // MapWindow.uxml
  slice(indexHtmlFull, 279, 391);          // Settings.uxml

// Strip `//` line comments before scanning for Georgian text: a comment is
// documentation, not a rendered UI string, and target.js:225 has one
// ("...the row where \"810 კგ\" wrapped", describing a FORMER display that
// the real code no longer uses — TargetCardView.cs correctly never prints
// "კგ") that would otherwise demand a Unity-side match for text nobody ever
// shows. Safe here (none of these 5 files has "//" inside a string literal).
const stripLineComments = (text) => text.replace(/\/\/[^\n]*/g, '');

const FILES = {
  'index.html': INDEX_HTML_PORTED,
  'hud.js': stripLineComments(read('src/hud.js')),
  'skillbar.js': stripLineComments(read('src/skillbar.js')),
  'settings.js': stripLineComments(read('src/settings.js')),
  'target.js': stripLineComments(read('src/target.js')),
  'mapui.js': stripLineComments(read('src/mapui.js')),
};
const STYLE = read('src/style.css');

const idSeen = new Set();
const ID_PATTERNS = [/\bid="([^"]+)"/g, /\bid='([^']+)'/g, /\.id\s*=\s*'([^']+)'/g, /\.id\s*=\s*"([^"]+)"/g];

let okFiles = 0;
for (const [name, text] of Object.entries(FILES)) {
  okFiles++;
  for (const re of ID_PATTERNS) {
    for (const m of text.matchAll(re)) {
      const key = `${name}|${m[1]}`;
      if (idSeen.has(key)) continue;
      idSeen.add(key);
      console.log(`ID file=${name} name=${m[1]}`);
    }
  }
}

// Georgian text: any run of >=2 Mkhedruli characters (U+10A0-U+10FF), plus
// adjoining spaces/punctuation/latin-digits so a whole phrase prints as one
// line instead of being chopped at every space.
const GEORGIAN_RUN = /[Ⴀ-ჿ][Ⴀ-ჿ0-9 .,·—!?()%/\-–„“"]*[Ⴀ-ჿ0-9)]/gu;
const strSeen = new Set();
let strCount = 0;
for (const [name, text] of Object.entries(FILES)) {
  for (const m of text.matchAll(GEORGIAN_RUN)) {
    const v = m[0].trim();
    if (v.length < 2) continue;
    const key = `${name}|${v}`;
    if (strSeen.has(key)) continue;
    strSeen.add(key);
    console.log(`STR file=${name} i=${strCount++} text=${v}`);
  }
}

// :root custom properties — src/style.css:3-17 and :1328.
const ROOT_BLOCK = /:root\s*\{([^}]*)\}/g;
let tokenCount = 0;
for (const m of STYLE.matchAll(ROOT_BLOCK)) {
  for (const prop of m[1].matchAll(/(--[a-zA-Z0-9-]+)\s*:\s*([^;]+);/g)) {
    console.log(`TOKEN file=style.css name=${prop[1]} value=${prop[2].trim()}`);
    tokenCount++;
  }
}

console.log(`UIPROBE_JS_SUMMARY files=${okFiles} ids=${idSeen.size} strings=${strSeen.size} tokens=${tokenCount}`);
