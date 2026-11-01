// The JS side of the PlaneHallFill port check
// (unity/Assets/Scripts/Flight/PlaneHallFill.cs).
//
// WHAT CAN AND CANNOT BE GROUND-TRUTHED HERE, HONESTLY:
//
//   - The 35-plane roster and the TANK/DPS role split come straight from
//     src/planes.js's real, exported PLANES table and isTankPlane() — real
//     ground truth, imported and called directly, no replication.
//   - The ACTUAL packing/placement (which rank, which strip, which exact
//     world position each plane lands at) depends on each airframe's REAL
//     measured bounding box after makeGameplayPlane() builds it — runtime
//     mesh measurement against a loaded Three.js model, which plain node
//     cannot do (the same documented limitation PlaneCatalog.cs's header
//     gives for loadPlane()/makeGameplayPlane() being out of this port's
//     scope). So this script does NOT attempt a fake end-to-end replication
//     of the real hall fill with invented spans/lens — tools/
//     _probe_planehalllayout.mjs already exercises Pack()/RankDepths()/
//     PlaceRanks() faithfully on a representative subset; this script's job
//     is the piece THAT is real ground truth: the roster and the role split,
//     which PlaneHallFillProbe.cs's SUMMARY line should match exactly
//     (tanks=15, dps=20, total=35) regardless of what the real FBX assets
//     measure.
//
//   node tools/_probe_planehallfill.mjs
import { PLANES, isTankPlane } from '../src/planes.js';

const tanks = PLANES.filter(isTankPlane);
const dps = PLANES.filter((p) => !isTankPlane(p));

console.log(`ROLES id=_roles total=${PLANES.length} tanks=${tanks.length} dps=${dps.length}`);

for (const p of PLANES) {
  console.log(`ROSTER id=${p.id} tank=${isTankPlane(p) ? 1 : 0}`);
}

// Sanity the id list itself against the Unity asset inventory this port's
// brief hands over: every id except 'spitfire' should have an FBX at
// Assets/Art/Planes/<id>.fbx, and spitfire ships as Assets/Art/Props/
// spitfire.obj instead. This script has no filesystem access to the unity/
// tree by design (it is the JS-ground-truth half, not an asset auditor), so
// it only prints the expected id set for a human/PlaneHallFillProbe.cs's
// `FAILED id=...` lines to diff against.
const nonSpitfireCount = PLANES.filter((p) => p.id !== 'spitfire').length;
console.log(`ASSETS id=_assets expectFbx=${nonSpitfireCount} expectObj=1 expectTotal=${PLANES.length}`);
