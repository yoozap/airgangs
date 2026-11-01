// Counterpart to unity/Assets/Editor/Probes/WindGunArmProbe.cs — verifies
// crane.js:1733-1807's gun-arm preservation now has a real Unity port
// (Props/WindRider.cs's ResolveGunArmBones + the save/restore straddling
// SetPose/HandsOnEdge in Tick()'s fly branch).
//
// WHAT IS COMPARABLE HERE, and what is not:
//   * `gunSide(foot)` (crane.js:981-982) is PURE logic — no rig, no plane,
//     no frame loop — so it is replicated verbatim below and printed as
//     GUNSIDE, paired line-for-line against the C# probe's own replica of
//     the exact same boolean (NOT a call into WindRider.ResolveGunArmBones
//     itself — that needs a live Transform hierarchy Node has none of; this
//     only re-derives the SIDE it would pick, which is pure).
//   * The actual drift measurement (does the gun hand bone hold steady
//     through off->ride->blown->fly while HandsOnEdge repeatedly tries to
//     write it) needs a REAL loaded, posed skeleton — a loaded GLTF body +
//     an AnimationMixer this probe does not construct, the SAME "Unity-only"
//     honesty tools/_probe_character.mjs already establishes for
//     CharCombatProbe's per-vertex shoulder-pocket fit (see that probe's own
//     header note). Printed here as CSONLY mirrors of the Unity side's own
//     CSONLY lines, for a human reading this file side by side with the
//     Unity log — unity_port_check.py never pairs them (by design: a CSONLY
//     line is counted, never compared).
//
//   node tools/_probe_windgunarm.mjs

// crane.js:981-982, verbatim:
//   const gunSide = (foot) => (foot?.held && isRifle(foot.held)
//     && foot._heldSide === 'L' ? 'L' : 'R');
function gunSide(held, isRifle, heldSide) {
  return (held && isRifle && heldSide === 'L') ? 'L' : 'R';
}

const cases = [
  ['R', true, true, 'R'],     // rifle, slung right
  ['L', true, true, 'L'],     // rifle, slung left
  ['NONE', false, false, 'R'], // nothing held
];

// Numeric encoding so this line carries real comparable VALUES (0/1), not
// just letters — heldN/rifleN/sideInN are the inputs, sideOutN is
// gunSide()'s own output, all 0=false/R, 1=true/L.
for (const [id, held, isRifle, heldSide] of cases) {
  const side = gunSide(held, isRifle, heldSide);
  console.log(`GUNSIDE id=${id} heldN=${held ? 1 : 0} rifleN=${isRifle ? 1 : 0} ` +
    `sideInN=${heldSide === 'L' ? 1 : 0} sideOutN=${side === 'L' ? 1 : 0}`);
}

// crane.js:1746-1752's gunArm array: empty when nothing is held, else
// upper+fore+hand = 3 (rigArm's chain plus the hand itself). Paired against
// WindRider.ResolveGunArmBones' own resolvedCount/RESOLVECOUNT lines — see
// WindGunArmProbe.cs's header for why n=3 is only true when a body's bone
// names actually match every regex (empirically true for banana.fbx, not
// something this pure prediction can know on its own).
for (const [id, held] of cases) {
  console.log(`RESOLVECOUNT id=${id} n=${held ? 3 : 0}`);
}

// The drift/mode-machine facts (WindGunArmProbe.cs's CSONLY WINDGUNARM
// lines) are NOT reproduced here at all — a one-sided fact belongs on the
// side that can actually produce it, and JS has no loaded skeleton to read
// one off. unity_port_check.py counts the Unity side's CSONLY lines without
// ever trying to pair them; read the Unity log directly for those.
