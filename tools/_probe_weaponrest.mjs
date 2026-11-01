// The JS side of the "weapon rest" port check — the small, genuinely-missing
// pieces found while closing out guns.js/shooting.js/weapons.js/pickups.js/
// rangelane.js's remaining gaps (manifest units already covered the rest of
// those files; see docs/port_units/weaponrest.json for exactly what this
// closes). Node-only, no browser/DOM needed.
//
//   node tools/_probe_weaponrest.mjs
import * as THREE from 'three';
import { aimAcross, WEAPON_DEFS } from '../src/weapons.js';
import { CARTRIDGES } from '../src/ballistics.js';

const f = (v, n = 6) => Number(v).toFixed(n);

// ============================================================================
// src/shooting.js:366-371 — the pistol's own muzzle flash, scaled by how much
// powder the cartridge actually burns (NOT art — a 9x18 flash is visibly
// smaller than a 9x19 one). FLASH_T (:31) is the duration. Not exported as a
// standalone function (inlined in Pistol.fire(), which touches THREE.Sprite
// at construction) — transcribed here and cited, same convention
// tools/_probe_range.mjs's scoreOpenRing() already uses for papertargets.js's
// own inlined hit().
// ============================================================================
const FLASH_T = 0.045; // src/shooting.js:31
console.log(`FLASHDUR seconds=${f(FLASH_T, 4)}`);

function flashScale(cart, rng) {
  return Math.min(0.8, 0.26 + 0.2 * (cart.energy / 580)) * (0.8 + rng * 0.5);
}
for (const id of ['9x18', '9x19']) {
  const cart = CARTRIDGES[id];
  // rng pinned at the formula's own two extremes (0 and 1) so the result is
  // deterministic on both sides — Math.random()/UnityEngine.Random.value
  // themselves are never compared, only the formula they feed.
  console.log(`FLASHSCALE cart=${id} rngMin=${f(flashScale(cart, 0), 6)} rngMax=${f(flashScale(cart, 1), 6)} energyJ=${f(cart.energy, 3)}`);
}

// ============================================================================
// src/guns.js:600 g.glass.burstList(blown, 0.8, 3) — the belly gun's own
// blown-pane burst power/max-shard-budget, now actually wired through
// MachineGun.OnGlassBurst (Combat/Guns.cs) instead of a dead-stub comment.
// ============================================================================
console.log(`GLASSBURST power=${f(0.8, 3)} max=${f(3, 3)}`);

// ============================================================================
// src/pickups.js:77-80 _card()'s caliber line — inlined inside a DOM-building
// method, transcribed and cited (same convention as above). Georgian text
// kept byte-for-byte; a skeleton mismatch here would mean the port's string
// literal drifted.
// ============================================================================
function caliberLine(cart) {
  return cart
    ? `${cart.label} · ${cart.mass} გ · ${cart.v0} მ/წმ · ${Math.round(cart.energy)} ჯ`
    : 'აღჭურვილობა — ჩაიცვამს';
}
for (const id of ['9x18', '9x19']) {
  console.log(`PICKCAL cart=${id} text=${caliberLine(CARTRIDGES[id])}`);
}
console.log(`PICKCAL cart=gear text=${caliberLine(null)}`);

// ============================================================================
// src/rangelane.js:407-408 — the control card's hint line, by direction.
// Pure text selection, inlined in RangeLanes.update() (a DOM-writing method);
// transcribed and cited. `dir` itself is the one numeric value per line.
// ============================================================================
function hintText(dir) {
  return dir > 0 ? 'მიდის შორს…' : dir < 0 ? 'ბრუნდება…' : 'F შორს · G ახლოს';
}
for (const dir of [1, -1, 0]) {
  console.log(`HINT dir=${dir} text=${hintText(dir)}`);
}

// ============================================================================
// src/weapons.js:491 aimAcross — real import, run on a synthetic two-armed
// rig (same shoulder geometry tools/_probe_range.mjs's RIFLEHOLD_SYNTH check
// uses, so a reviewer who already knows that rig recognizes this one).
// ============================================================================
function makeArm(root, side) {
  const s = side === 'L' ? -1 : 1;
  const upper = new THREE.Bone();
  upper.name = `upperarm_${side === 'L' ? 'l' : 'r'}`;
  upper.position.set(0.20 * s, 1.40, 0.0);
  root.add(upper);
  const fore = new THREE.Bone();
  fore.name = `lowerarm_${side === 'L' ? 'l' : 'r'}`;
  fore.position.set(0.02 * s, -0.27, 0.05);
  upper.add(fore);
  return { upper, fore };
}
const root = new THREE.Object3D();
root.name = 'Root';
makeArm(root, 'R');
makeArm(root, 'L');
root.updateMatrixWorld(true);
const rigStub = {}; // see tools/_probe_range.mjs's own note on why `{}` not `null`

for (const [label, ax, az] of [['ahead', 0, -1], ['right', 1, 0], ['left', -1, 0]]) {
  const aim = new THREE.Vector3(ax, 0, az);
  const v = aimAcross(rigStub, root, aim);
  console.log(`AIMACROSS dir=${label} value=${f(v, 6)}`);
}
