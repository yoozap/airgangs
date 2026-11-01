#!/usr/bin/env node
// Reads the same facts GameLoopProbe.cs prints, but out of src/game.js's own
// TEXT (no browser, no running game — the Unity port is barred from running
// the editor and this is the JS-side counterpart of an editor probe, so a
// static-text read is the honest equivalent). Prints KEY=value lines for
// tools/unity_port_check.py to pair against GameLoopProbe.cs's output.
//
//   node tools/_probe_gameloop.mjs > /tmp/gameloop_js.txt
//   (Unity side, via unity_import_assets.sh-style log capture) > /tmp/gameloop_cs.txt
//   python3 tools/unity_port_check.py /tmp/gameloop_js.txt /tmp/gameloop_cs.txt
//
// Each line's id is a distinct name (CONST_WHEELHEIGHT, KEYBIND_ENTER, ...) —
// never a bare index — so unity_port_check.py's "blank standalone numbers"
// skeleton never collides two different facts (see that file's own header).
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const gamejs = readFileSync(join(here, '..', 'src', 'game.js'), 'utf8');
const footjs = readFileSync(join(here, '..', 'src', 'foot.js'), 'utf8');

function constNum(name) {
  const m = gamejs.match(new RegExp(`const ${name}\\s*=\\s*(-?[\\d.]+)`));
  return m ? m[1] : null;
}

function has(text, needle) {
  return text.includes(needle) ? 1 : 0;
}

const lines = [];
const push = (k, v) => lines.push(`${k}=${v}`);

// ---- constants GameLoop.cs itself also hard-codes (game.js:67-76) ---------
// NOT included here: SPAWN_YAW (spawn-point selection reads world.spawn —
// GameLoop.cs does not port the city's own spawn-point lookup), CAM_MIN/
// CAM_SKIN (the `_keepCameraOutside` dolly-clamp — not ported, see the port
// report), MOUSE_SENS (that constant lives in FlightCamera.cs, not this file,
// and is that port's own concern). Only constants GameLoop.cs truly owns are
// cross-checked here.
for (const name of ['WHEEL_HEIGHT', 'REVERSE_SPEED', 'MAX_HEALTH']) {
  const v = constNum(name);
  if (v !== null) push(`CONST_${name.replace(/_/g, '')}`, v);
}

// ---- key bindings GameLoop.KeyBindings claims (game.js:452-559) -----------
// Presence of the literal e.code string this file's handler reacts to.
push('KEYBIND_ENTER', has(gamejs, "e.code === 'Enter'"));
push('KEYBIND_NUMPADENTER', has(gamejs, "e.code === 'NumpadEnter'"));
push('KEYBIND_TAB', has(gamejs, "e.code === 'Tab'"));
push('KEYBIND_KEYV', has(gamejs, "e.code === 'KeyV'"));
push('KEYBIND_KEYC', has(gamejs, "e.code === 'KeyC'"));
push('KEYBIND_KEYF', has(gamejs, "e.code === 'KeyF'"));
push('KEYBIND_KEYM', has(gamejs, "e.code === 'KeyM'"));
push('KEYBIND_KEYH', has(gamejs, "e.code === 'KeyH'"));
push('KEYBIND_ESCAPE', has(gamejs, "e.code === 'Escape'"));
push('KEYBIND_METALEFT', has(gamejs, "'MetaLeft'"));
push('KEYBIND_ALTKEYS', /AIM_KEYS\s*=\s*new Set\(\['AltLeft', 'AltRight'\]\)/.test(gamejs) ? 1 : 0);
// X/Z (roll/slide) are NOT discrete handlers in game.js's own _setupInput —
// foot.js reads them itself off the held-keys set every frame (its own
// FootHint string lists them: "X — გადაგორება · Z — სრიალი"). Recorded
// honestly as a foot.js fact, not a game.js _setupInput fact.
push('KEYBIND_FOOTHINT_ROLLSLIDE', /გადაგორება.*სრიალი|X\s*—/.test(footjs) ? 1 : 0);

// ---- _tick()'s own stage order (game.js:2352-2637) -------------------------
// Distinctive, line-unique substrings, checked by position so the ORDER is
// the thing under test, not just presence.
const tickStart = gamejs.indexOf('_tick() {');
const tickBody = gamejs.slice(tickStart, gamejs.indexOf('\n  _adaptResolution'));
const markers = [
  ['groundProbe', 'this._findGround()'],
  ['onFoot', 'this.foot.update(dt)'],
  ['flightUpdate', 'this._flightUpdate(dt)'],
  ['crashed', 'this.crash.update(dt)'],
  ['smokeAndCombat', 'this.planephys.update(fdt)'],
  ['warnGrid', 'this.warnGrid.material.opacity'],
  ['boundary', 'this.boundary.update('],
  ['autosave', 'this.saveState()'],
  ['engineAndHood', 'spinPropellers('],
  ['windAndWorld', 'updateWind(dt)'],
  ['hudAndTargets', 'this.targets.update(dt)'],
];
const positions = markers.map(([name, needle]) => [name, tickBody.indexOf(needle)]).filter(([, i]) => i >= 0);
const orderedNames = [...positions].sort((a, b) => a[1] - b[1]).map(([n]) => n);
push('STAGEORDERCOUNT', orderedNames.length);
console.error('# STAGEORDER_SEQUENCE(js)=' + orderedNames.join(','));
// Not fed to the numeric checker (word sequences aren't numbers), but this is
// the actual proof the ORDER matches, for a human reading the probe's output:
// compare this to GameLoopProbe's "REALSTAGEORDER_FLYING" Debug.Log line.

// ---- Boot.cs-side facts, out of src/main.js --------------------------------
const mainjs = readFileSync(join(here, '..', 'src', 'main.js'), 'utf8');
const maxHeroesM = mainjs.match(/MAX_HEROES\s*=\s*(\d+)/);
if (maxHeroesM) push('BOOT_MAXHEROES', maxHeroesM[1]);
push('BOOT_AUTOSTART_REMEMBERED', /autostart.*localStorage|localStorage.*autostart/i.test(mainjs) ? 1 : 0); // must be 0 — main.js:821-824 says it must NOT be remembered

for (const l of lines) console.log(l);
