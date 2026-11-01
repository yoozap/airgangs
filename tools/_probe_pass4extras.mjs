// Ground truth for the three units yesterday's wiring left unpaired:
//   Combat.GunPod            vs src/guns.js:393-417
//   Fx.RocketBodyFx          vs src/rockets.js:147-187
//   RigSeatExtras.SetStrafe  vs src/characters.js:570-579 + src/foot.js:6035-6057
//
// Pure node — none of these three touch the DOM, the renderer or any asset, so
// a browser probe would add nothing. Every geometric literal below is READ OUT
// OF src/*.js by regex (see `lit()`), never retyped: two hand-typed probe
// literals went stale in this project in two days (exclusions=31,
// METRO_GEORGIAN_FONT_ASSET_FOUND=false) and a third was found by this very
// pass (tools/_probe_tunnels.py's legacy width /10).
//
// FRAME. All three JS objects live in a MODEL-LOCAL frame whose forward is
// Three's -Z; Unity's is +Z, and the port's established rule (Combat/Rockets.cs
// Fire(), Combat/Guns.cs GunPod's header) is to negate that local z. X is also
// mirrored (Assets/Scripts/Data/GeoFrame.cs: game X=west, Unity X=east). So the
// lines below print the UNITY-frame value, computed here as the documented flip
// of the JS literal, and say so per line. A mismatch therefore means the port
// disagrees about the geometry, not about the convention.
//
//   node tools/_probe_pass4extras.mjs
import fs from 'node:fs';

const f = (v, n = 6) => Number(v).toFixed(n);
const guns = fs.readFileSync('src/guns.js', 'utf8');
const rockets = fs.readFileSync('src/rockets.js', 'utf8');
const chars = fs.readFileSync('src/characters.js', 'utf8');
const foot = fs.readFileSync('src/foot.js', 'utf8');

function one(src, re, what) {
  const m = src.match(re);
  if (!m) throw new Error(`could not read ${what} out of the source — the probe must not guess`);
  return m;
}

// ===========================================================================
// GunPod — src/guns.js:393-417
// ===========================================================================
// muzzleLocal, relative to the PLANE: `this.muzzleLocal = new THREE.Vector3(0, -0.95, -3.65)`
{
  const m = one(guns, /muzzleLocal\s*=\s*new THREE\.Vector3\(\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\s*\)/, 'guns.js muzzleLocal');
  const [x, y, z] = [+m[1], +m[2], +m[3]];
  console.log(`GUNPOD_MUZZLE_LOCAL x=${f(-x)} y=${f(y)} z=${f(-z)}`); // x,z flipped
  // MuzzleWorld: the plane at a known pose. Three does pos + R*local; with a
  // yaw-only rotation that is the 2x2 rotation about Y, which is identical in
  // both engines once x and z are mirrored together (a mirror of BOTH axes is
  // a rotation by pi, so a yaw-only basis survives it unchanged apart from the
  // sign of the yaw itself — hence the negated yaw below, GeoFrame.YawToUnity).
  const px = -1200.5, py = 640.25, pz = -800.75, yawJs = 0.6;  // game frame
  const ux = -px, uz = -pz, uyaw = -yawJs;                     // Unity frame
  const lx = -x, lz = -z;
  const wx = ux + (Math.cos(uyaw) * lx + Math.sin(uyaw) * lz);
  const wy = py + y;
  const wz = uz + (-Math.sin(uyaw) * lx + Math.cos(uyaw) * lz);
  console.log(`GUNPOD_MUZZLE_WORLD x=${f(wx, 4)} y=${f(wy, 4)} z=${f(wz, 4)}`);
}
// pod.position.set(0, -0.95, -1.55)
{
  const m = one(guns, /pod\.position\.set\(\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\s*\)/, 'guns.js pod.position');
  console.log(`GUNPOD_POD_LOCAL x=${f(-m[1])} y=${f(m[2])} z=${f(-m[3])}`);
}
// The three cylinders. THREE.CylinderGeometry(radiusTop, radiusBottom, height,
// sides) builds along +Y with radiusTop at +Y; `rotation.x = Math.PI / 2` maps
// +Y -> +Z (R_x(+90) sends (0,1,0) to (0,0,1)). Three's forward is -Z, so the
// cylinder's TOP ends up AFT and its BOTTOM faces the MUZZLE. That is the fact
// the two sides must agree on, so it is printed as muzzle/aft radii rather
// than as top/bottom.
const cyl = (name, re) => {
  const m = one(guns, re, `guns.js ${name} cylinder`);
  return { rTop: +m[1], rBot: +m[2], len: +m[3], sides: +m[4] };
};
{
  const body = cyl('body', /const body = new THREE\.Mesh\(new THREE\.CylinderGeometry\(([\d.]+),\s*([\d.]+),\s*([\d.]+),\s*(\d+)\)/);
  console.log(`GUNPOD_BODY rMuzzle=${f(body.rBot)} rAft=${f(body.rTop)} len=${f(body.len)} sides=${body.sides}`);
  const barrel = cyl('barrel', /const barrel = new THREE\.Mesh\(new THREE\.CylinderGeometry\(([\d.]+),\s*([\d.]+),\s*([\d.]+),\s*(\d+)\)/);
  console.log(`GUNPOD_BARREL rMuzzle=${f(barrel.rBot)} rAft=${f(barrel.rTop)} len=${f(barrel.len)} sides=${barrel.sides}`);
  const brake = cyl('brake', /const brake = new THREE\.Mesh\(new THREE\.CylinderGeometry\(([\d.]+),\s*([\d.]+),\s*([\d.]+),\s*(\d+)\)/);
  console.log(`GUNPOD_BRAKE rMuzzle=${f(brake.rBot)} rAft=${f(brake.rTop)} len=${f(brake.len)} sides=${brake.sides}`);
  // the housing taper's two ends in pod-local Unity z: +-len/2
  console.log(`GUNPOD_BODY_ENDS zAft=${f(-body.len / 2)} zMuzzle=${f(body.len / 2)}`);
}
// barrel/brake z, and the two struts
{
  const bz = one(guns, /barrel\.position\.z = (-?[\d.]+)/, 'guns.js barrel.position.z');
  const kz = one(guns, /brake\.position\.z = (-?[\d.]+)/, 'guns.js brake.position.z');
  console.log(`GUNPOD_BARREL_Z z=${f(-bz[1])}`);
  console.log(`GUNPOD_BRAKE_Z z=${f(-kz[1])}`);
  const dz = one(guns, /for \(const dz of \[(-?[\d.]+), (-?[\d.]+)\]\)/, 'guns.js strut dz list');
  const box = one(guns, /new THREE\.BoxGeometry\(([\d.]+), ([\d.]+), ([\d.]+)\), metal\);\s*\n\s*strut\.position\.set\(\s*(-?[\d.]+),\s*([\d.]+),\s*dz\s*\)/, 'guns.js strut box');
  console.log(`GUNPOD_STRUT w=${f(box[1])} h=${f(box[2])} d=${f(box[3])} y=${f(box[5])}`);
  console.log(`GUNPOD_STRUT_Z z0=${f(-dz[1])} z1=${f(-dz[2])}`);
}

// ===========================================================================
// RocketBodyFx — src/rockets.js:147-187
// ===========================================================================
{
  const b = one(rockets, /const body = new THREE\.Mesh\(new THREE\.CylinderGeometry\(([\d.]+),\s*([\d.]+),\s*([\d.]+),\s*(\d+)\)/, 'rockets.js body');
  console.log(`ROCKET_BODY r=${f(+b[1])} len=${f(+b[3])} sides=${+b[4]}`);
  console.log(`ROCKET_BODY_ENDS zTail=${f(-(+b[3]) / 2)} zNoseJoin=${f(+b[3] / 2)}`);

  const nose = one(rockets, /const nose = new THREE\.Mesh\(new THREE\.ConeGeometry\(([\d.]+),\s*([\d.]+),\s*(\d+)\)/, 'rockets.js nose cone');
  const noseZ = one(rockets, /nose\.position\.z = (-?[\d.]+)/, 'rockets.js nose.position.z');
  const noseR = +nose[1], noseLen = +nose[2];
  // ConeGeometry's tip is at +Y, half-height h/2; `rotation.x = -Math.PI/2`
  // sends (0, h/2, 0) to (0, 0, -h/2), so in Three the tip is at
  // position.z - len/2 (further FORWARD) and the base at position.z + len/2.
  const tipJs = +noseZ[1] - noseLen / 2, baseJs = +noseZ[1] + noseLen / 2;
  console.log(`ROCKET_NOSE r=${f(noseR)} len=${f(noseLen)} sides=${+nose[3]}`);
  console.log(`ROCKET_NOSE_Z zTip=${f(-tipJs)} zBase=${f(-baseJs)}`);

  const finBox = one(rockets, /const fin = new THREE\.Mesh\(new THREE\.BoxGeometry\(([\d.]+), ([\d.]+), ([\d.]+)\)/, 'rockets.js fin box');
  const finPos = one(rockets, /fin\.position\.set\(Math\.cos\(a\) \* ([\d.]+), Math\.sin\(a\) \* ([\d.]+), ([\d.]+)\)/, 'rockets.js fin position');
  const finR = +finPos[1], finZ = +finPos[3];
  // rotation.z = a + PI/2 puts the box's local +Y along -radial and its local
  // +X along +tangential, so the 0.3 dimension is RADIAL and 0.02 TANGENTIAL.
  console.log(`ROCKET_FIN n=4 radius=${f(finR)} z=${f(-finZ)} radial=${f(+finBox[2])} tangential=${f(+finBox[1])} depth=${f(+finBox[3])}`);
  for (let k = 0; k < 4; k++) {
    const a = (k / 4) * Math.PI * 2 + Math.PI / 4;
    console.log(`ROCKET_FIN_${k} angDeg=${f(a * 180 / Math.PI, 4)} x=${f(Math.cos(a) * finR, 5)} y=${f(Math.sin(a) * finR, 5)} z=${f(-finZ, 5)}`);
  }
  const glowZ = one(rockets, /glow\.position\.z = ([\d.]+)/, 'rockets.js glow.position.z');
  const glowS = one(rockets, /glow\.scale\.setScalar\(([\d.]+)\)/, 'rockets.js glow scale');
  console.log(`ROCKET_GLOW z=${f(-glowZ[1])} scale=${f(+glowS[1])}`);
}

// ===========================================================================
// RigSeatExtras.SetStrafe — characters.js:570-579, driven as foot.js:6035-6057
// ===========================================================================
{
  const rate = one(chars, /this\._strafeY \+= d \* Math\.min\(1, (\d+) \* dt\)/, 'characters.js strafe rate');
  console.log(`STRAFE_RATE k=${f(+rate[1], 1)}`);
  // the blend table, read out of foot.js's own line
  const sb = one(foot, /let sa = 0, sb = armed \? \(isRifle\(this\.held\) \? ([\d.]+) : ([\d.]+)\) : (\d+)/, 'foot.js sb table');
  const cover = one(foot, /const COVER_SHUFFLE = ([\d.]+)/, 'foot.js COVER_SHUFFLE');
  console.log(`STRAFE_BLEND rifle=${f(+sb[1], 3)} pistol=${f(+sb[2], 3)} bare=${f(+sb[3], 3)} cover=${f(+cover[1], 3)}`);

  const K = +rate[1];
  function setStrafe(st, angle, dt, blend) {
    const want = (Number.isFinite(angle) ? angle : 0) * blend;
    let d = want - st.y;
    while (d > Math.PI) d -= 2 * Math.PI;
    while (d < -Math.PI) d += 2 * Math.PI;
    st.y += d * Math.min(1, K * dt);
    return st.y;
  }
  // one ramp per blend, 20 frames at 1/60 s toward a 0.9 rad strafe
  for (const [tag, blend] of [['bare', +sb[3]], ['pistol', +sb[2]], ['rifle', +sb[1]], ['cover', +cover[1]]]) {
    const st = { y: 0 };
    let y = 0;
    for (let i = 0; i < 20; i++) y = setStrafe(st, 0.9, 1 / 60, blend);
    console.log(`STRAFE_RAMP id=${tag} blend=${f(blend, 3)} y20=${f(y)}`);
  }
  // the wrap: a target just the other side of +-pi must take the SHORT arc
  {
    const st = { y: 3.0 };
    const y = setStrafe(st, -3.0, 1 / 60, 1);
    console.log(`STRAFE_WRAP from=${f(3.0)} to=${f(-3.0)} y1=${f(y)}`);
  }
  // a walk clip running passes blend 0 — the offset must ease back to centre
  {
    const st = { y: 0 };
    for (let i = 0; i < 20; i++) setStrafe(st, 0.9, 1 / 60, 1);
    let y = st.y;
    for (let i = 0; i < 20; i++) y = setStrafe(st, 0.9, 1 / 60, 0);
    console.log(`STRAFE_WALK_RECENTRE y40=${f(y)}`);
  }
  // NaN in, centre out (the Number.isFinite guard)
  {
    const st = { y: 0.4 };
    const y = setStrafe(st, NaN, 1 / 60, 1);
    console.log(`STRAFE_NAN_GUARD y1=${f(y)}`);
  }
}
