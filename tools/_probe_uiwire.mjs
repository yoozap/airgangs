// Static-source counterpart to unity/Assets/Editor/Probes/UiWireProbe.cs —
// same discipline as tools/_probe_combatrest.mjs: print KEY names straight
// off the REAL JS/HTML text (regex against actual literals, never retyped
// from memory) so they can be diffed one-for-one against the C# probe's
// Check()/Fact() keys of the SAME NAME. This project's own gotcha, stated in
// the port brief: "a GR prefix cost one probe 40 of its 47 lines" — so every
// key below is spelled EXACTLY as UiWireProbe.cs prints it, no prefix.
//
// WHAT THIS PROVES, AND WHAT IT DOESN'T: UiWireProbe.cs asserts the UNITY
// WIRING is live (MapUi non-null, Pistol.ClipTime/OnPlayClip assigned, the
// arms clips actually loaded, Settings can actually open Credits) by
// loading the real scene and ticking it. The three real bugs this pass fixed
// (GameLoop.MapUi never constructed, GunAnim/RifleHolds never called from
// HeldWeapon.cs, CreditsView never opened from Settings) have NO JS-side
// failure to reproduce — the JS reference always did these correctly. So
// this script's job is the other half of the proof: showing the JS
// reference actually DOES each of these things, which is the behaviour the
// Unity wiring now needs to match. A PASS here is "the JS source still does
// this the way the port assumed"; the matching PASS in UiWireProbe.cs is
// "and now so does the live Unity scene".
//
// Run: node tools/_probe_uiwire.mjs
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const read = (p) => readFileSync(join(root, p), 'utf8');

const game = read('src/game.js');
const shooting = read('src/shooting.js');
const gunanim = read('src/gunanim.js');
const html = read('index.html');
const landmarks = read('src/landmarks.js');
const qualityBar = read('docs/QUALITY_BAR.md');

let allPass = true;
// `note` is what a FAILURE would mean, so it prints ONLY on a failure. It used
// to print on a PASS as well, which is the defect this pass was sent to fix on
// the C# side (UiWireProbe.cs printed "PASS armsPistolReady (WeaponAnimLoader
// could not find ...pistol_anim.fbx)" and was read three times as a report that
// the asset was missing) — and the moment an asset-existence Check was added
// here, this side produced the same misleading line. Each note's own citation
// is in the comment above its call, where a reader looks anyway.
function Check(key, cond, note) {
  const line = `JSONLY ${cond ? 'PASS' : 'FAIL'} ${key}${!cond && note ? ` (${note})` : ''}`;
  console.log(line);
  if (!cond) allPass = false;
}
function Fact(key, value) { console.log(`${key}=${value}`); }

// =============================================================================
// ---- 1. MapUi — src/game.js:141 `this.mapui = new MapUI()`, index.html's
// #minimap/#mapwin/#mapwin-body/#bigmap-canvas — the JS side that UiWireProbe
// .cs's mapUiAssigned/mapUiWindowExists/mapUiMiniExists are proving Unity now
// matches (GameLoop.MapUi was never constructed before this pass).
// =============================================================================
Check('mapUiAssigned', /this\.mapui\s*=\s*new MapUI\(\)/.test(game),
  'src/game.js:141 — the JS always builds one unconditionally at startup');
Check('mapUiWindowExists', /id="mapwin"/.test(html) && /id="mapwin-body"/.test(html));
Check('mapUiMiniExists', /id="minimap"/.test(html));
Check('mapUiBigMapExists', /id="bigmap-canvas"/.test(html));

const streetMatch = landmarks.match(/STREET_STATUE_POINTS = \[([\s\S]*?)\n\];/);
const bustMatch = landmarks.match(/BUST_POINTS = \[([\s\S]*?)\n\];/);
const streetCount = streetMatch ? (streetMatch[1].match(/\[/g) || []).length : 0;
const bustCount = bustMatch ? (bustMatch[1].match(/\[/g) || []).length : 0;
Fact('MAPUI_STREET_STATUE_COUNT', streetCount);
Fact('MAPUI_BUST_COUNT', bustCount);
Check('mapUiStreetStatuesPopulated', streetCount > 0,
  'src/landmarks.js STREET_STATUE_POINTS — BigMapView.StreetStatues was hardcoded empty before this pass');
Check('mapUiBustsPopulated', bustCount > 0);

// =============================================================================
// ---- 2. HeldWeapon — src/shooting.js's g.gunAnim?.clipTime/.play call
// sites (the JS's own version of the hook Combat/Shooting.cs's Pistol.
// ClipTime/OnPlayClip were left unassigned against), src/game.js:201's
// unconditional construction of BOTH arms clips at startup.
// =============================================================================
Check('gunClipTimeHookAssigned', /g\.gunAnim\?\.clipTime\(name\)/.test(shooting),
  'src/shooting.js:247 — the JS reads the clip duration through this exact call every reload');
Check('gunOnPlayClipHookAssigned', /g\.gunAnim\?\.play\(name/.test(shooting));
const pistolBuilt = /pistol:\s*new GunAnim\(this,\s*PISTOL_SPEC\)/.test(game);
const rifleBuilt = /rifle:\s*new GunAnim\(this,\s*RIFLE_SPEC\)/.test(game);
Check('armsPistolReady', pistolBuilt,
  'src/game.js:201 — this.gunAnims.pistol is built unconditionally, same as Unity\'s _armsPistol now is');
Check('armsRifleReady', rifleBuilt);

// ---- THE ARMS RIGS' OWN ASSETS, as PAIRED NUMERIC facts --------------------
// Every Check above is JSONLY, so before this the whole pair compared TWO
// values (the two map counts) — under tools/unity_port_check.py's own
// MIN_VALUES floor of 4, i.e. it was reporting nothing. These four facts are
// the same claim on both sides and ARE comparable: the arms reference rig
// exists and carries a clip. On this side that is the GLB src/gunanim.js's own
// spec names, under public/; on the C# side it is the imported FBX plus a
// non-preview AnimationClip in it (Assets/Art/Props/{pistol_anim,akm_arms}.fbx,
// via tools/glb_to_fbx.py), and GunAnim.Ready read back off the live scene.
//
// This is the assertion that was missing. UiWireProbe.cs used to print
// "PASS armsPistolReady (WeaponAnimLoader could not find …pistol_anim.fbx +
// its AnimationClip)" — its FAIL note on a PASS line — and nothing anywhere
// actually checked the file.
const specUrl = (re) => { const m = gunanim.match(re); return m ? m[1] : null; };
const pistolUrl = specUrl(/name: 'pistol', url: '([^']+)'/);
const rifleUrl = specUrl(/name: 'rifle', url: '([^']+)'/);
const assetOk = (url) => (url && existsSync(join(root, 'public', url)) ? 1 : 0);
Fact('ARMS_PISTOL_READY', pistolBuilt && assetOk(pistolUrl) ? 1 : 0);
Fact('ARMS_RIFLE_READY', rifleBuilt && assetOk(rifleUrl) ? 1 : 0);
Fact('ARMS_PISTOL_ASSET', assetOk(pistolUrl));
Fact('ARMS_RIFLE_ASSET', assetOk(rifleUrl));
Check('armsPistolAssetOnDisk', assetOk(pistolUrl) === 1, `public/${pistolUrl} is missing`);
Check('armsRifleAssetOnDisk', assetOk(rifleUrl) === 1, `public/${rifleUrl} is missing`);

// =============================================================================
// ---- 3. Settings -> Credits — the JS reference has NO credits UI at all
// (confirmed: no "credits"/"Credits" anywhere in src/settings.js or
// index.html — this is a Unity-only addition docs/QUALITY_BAR.md calls for).
// So there is no JS behaviour to match; this just proves the requirement
// this pass answered is still the one on record, not a fabricated one.
// =============================================================================
Check('settingsHasCredits', /no credits UI to carry the CC BY\/BY-SA attribution/.test(qualityBar),
  'Unity-only addition — src/settings.js has no credits screen at all; see SettingsView.EnsureCredits()');

// JSONLY: this side's own verdict. UiWireProbe.cs prints its own
// `UIWIREPROBE RESULT=...`, a DIFFERENT key, so an unprefixed line here is a
// js-only line, and tools/unity_port_check.py counts one of those as a FAILURE
// (a cs-only leftover is only a WARN). That is what it did the moment the four
// ARMS_* facts above lifted this pair over its MIN_VALUES floor and the check
// got far enough to report anything at all.
console.log(allPass ? 'JSONLY UIWIREPROBE_JS RESULT=PASS' : 'JSONLY UIWIREPROBE_JS RESULT=FAIL');
process.exitCode = allPass ? 0 : 1;
