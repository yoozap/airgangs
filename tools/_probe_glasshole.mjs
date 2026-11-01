// The JS side of the breaking-glass / carved-blast-hole port check
// (Core/CombatWorldAdapter.cs's NearWindow/BlastWindows/PunchHole/StripPropsAt
// in the Unity port). Paired with unity/Assets/Editor/Probes/GlassHoleProbe.cs,
// which prints the SAME KEY=value lines from the C# side against the SAME
// synthetic footprints/numeric literals used here — diffed by eye (or with
// tools/unity_port_check.py-style grepping): identical key names and shapes,
// not a delta.
//
// Plain Node, no THREE.js / browser: src/citymap.js's `_windowAnchors` (the
// position math behind nearWindow/windowAt/blastWindows' anchor grid) is a
// pure function of a footprint ring + base/top + a heightAt callback — no
// scene graph needed. It is PRIVATE on the CityMap class (never exported), so
// it is transcribed verbatim here with its own source lines cited, the same
// established convention tools/_probe_damage.mjs uses for planedamage.js's
// tearRadius/bridgeOf. The AUGMENTATION (keeping nx,nz, which the real method
// computes then discards) matches Core/CombatWorldAdapter.ComputeWindowAnchors
// on the C# side — see that method's own doc comment for why it exists as a
// separate duplicate instead of calling City/BuildingDamage.WindowAnchors.
//
// blastWindows itself (src/citymap.js:2839-2866) scans `this.propMeshes` (the
// real instanced glass-pane prop layer) — a system this Unity port's adapter
// does not have wired to a chunk/building index yet (see CombatWorldAdapter.
// cs's class header). Its candidate-gather/SORT/FLATTEN shape is ported
// EXACTLY, just fed from the window-anchor grid above instead of propMeshes —
// the identical substitution CombatWorldAdapter.BlastWindows/StripPropsAt make
// on the C# side, which is why both sides can be compared key-for-key.
//
// Chosen heights (12.0 m / 9.0 m, not 12.8 m / 9.6 m) are deliberately clear of
// the h/3.2 floor-boundary — see JSMATH notes elsewhere in this project on
// float32-vs-float64 rounding at an exact tie; this probe sidesteps the
// question entirely rather than relying on both languages tying the same way.
//
//   node tools/_probe_glasshole.mjs

const f = (v, n = 6) => Number(v).toFixed(n);

// ---- src/citymap.js:2027-2066 _windowAnchors, transcribed verbatim and
// augmented to also keep the per-edge facade normal (nx,nz) — identical
// position math, nothing about the anchor GRID itself changed. ----
function windowAnchorsWithNormal(ringXZ, n, baseY, top, cx, cz, heightAt) {
  const h = top - baseY;
  const nF = Math.max(1, Math.floor(h / 3.2));
  const pos = [];
  const nrm = [];
  for (let i = 0; i < n && pos.length < 3 * 64; i++) {
    const j = (i + 1) % n;
    const x1 = ringXZ[i * 2];
    const z1 = ringXZ[i * 2 + 1];
    const ex = ringXZ[j * 2] - x1;
    const ez = ringXZ[j * 2 + 1] - z1;
    const len = Math.hypot(ex, ez);
    if (len < 4) continue;
    let nx = -ez / len;
    let nz = ex / len;
    const mx = x1 + ex / 2;
    const mz = z1 + ez / 2;
    if ((mx - cx) * nx + (mz - cz) * nz < 0) {
      nx = -nx;
      nz = -nz;
    }
    const bays = Math.floor(len / 3.4);
    if (bays <= 0) continue;
    for (let k = 0; k < bays && pos.length < 3 * 64; k++) {
      const t = (k + 0.5) / bays;
      const wx = x1 + ex * t + nx * 0.5;
      const wz = z1 + ez * t + nz * 0.5;
      const gy = heightAt(wx, wz);
      for (let fl = 1; fl < nF && pos.length < 3 * 64; fl++) {
        const wy = baseY + 2 + fl * 3.2 - 0.4;
        if (gy !== null && wy < gy + 0.8) continue; // buried on the slope
        pos.push(wx, wy, wz);
        nrm.push(nx, nz);
      }
    }
  }
  return { pos, nrm };
}

// ---- src/citymap.js:2839-2866 blastWindows' own candidate/SORT/FLATTEN
// shape, transcribed; `broken[]` stands in for the real method's permanent
// `arr.fill(0, o, o+16)` pane removal (src/citymap.js:2371/2856). ----
function blastWindows(anchors, broken, px, py, pz, radius) {
  const out = [];
  const r2 = radius * radius;
  const count = anchors.pos.length / 3;
  for (let k = 0; k < count; k++) {
    if (broken[k]) continue;
    const x = anchors.pos[k * 3];
    const y = anchors.pos[k * 3 + 1];
    const z = anchors.pos[k * 3 + 2];
    const d2 = (x - px) ** 2 + (y - py) ** 2 + (z - pz) ** 2;
    if (d2 > r2) continue;
    out.push({ x, y, z, nx: anchors.nrm[k * 2], nz: anchors.nrm[k * 2 + 1], d2 });
    broken[k] = true;
  }
  if (!out.length) return [];
  out.sort((a, b) => a.d2 - b.d2);
  const flat = [];
  for (const w of out) flat.push(w.x, w.y, w.z, w.nx, w.nz);
  return flat;
}

// ---- src/citymap.js:2102-2120 nearWindow, transcribed (the 1.7 m ball test). ----
function nearWindowAt(anchors, broken, p) {
  const r2 = 1.7 * 1.7;
  const count = anchors.pos.length / 3;
  for (let k = 0; k < count; k++) {
    if (broken[k]) continue;
    const dx = anchors.pos[k * 3] - p.x;
    const dy = anchors.pos[k * 3 + 1] - p.y;
    const dz = anchors.pos[k * 3 + 2] - p.z;
    if (dx * dx + dy * dy + dz * dz < r2) return true;
  }
  return false;
}

function mirrorRing(fpGame) {
  const out = [];
  for (let i = 0; i < fpGame.length; i += 2) out.push(-fpGame[i], fpGame[i + 1]);
  return out;
}

// ---- same 20x12 m rectangle GlassHoleProbe.cs's WindowAnchorFacts uses ----
function winanchFacts() {
  const fpGame = [0, 0, 20, 0, 20, 12, 0, 12];
  const baseY = 0;
  const top = 12.0;
  const ring = mirrorRing(fpGame);
  const cx = -10;
  const cz = 6; // unity-mirrored centroid of the rectangle above
  const anchors = windowAnchorsWithNormal(ring, 4, baseY, top, cx, cz, () => null);
  const count = anchors.pos.length / 3;
  const strideOk = anchors.nrm.length === count * 2;
  console.log(`WINANCH id=rect20x12 count=${count} nrmLen=${anchors.nrm.length} strideOk=${strideOk ? 1 : 0}`);
  if (count > 0) {
    const nlen = Math.hypot(anchors.nrm[0], anchors.nrm[1]);
    console.log(`WINANCH id=rect20x12_first x=${f(anchors.pos[0], 4)} y=${f(anchors.pos[1], 4)} z=${f(anchors.pos[2], 4)} nx=${f(anchors.nrm[0], 4)} nz=${f(anchors.nrm[1], 4)} nlen=${f(nlen, 4)}`);
  }
}

// NOTE: there is no JS-side TRIRANGE line. That check is specific to how
// THIS Unity port's own City/BuildingBuilder.cs lays out a building's wall
// vertices (unshared per-edge quads) versus how City/BuildingDamage.cs's
// ported BuildTriRanges formula assumes they are laid out (citymap.js's own
// shared-ring mesh) — a Unity-only structural bug with no JS counterpart to
// pair against. See GlassHoleProbe.cs's own class header. Verified standalone
// in the C# probe only; not claimed here.

// ---- same 40x10 m wall GlassHoleProbe.cs's BlastOrderFacts uses ----
function blastOrderFacts() {
  const fpGame = [0, 0, 40, 0, 40, 10, 0, 10];
  const baseY = 0;
  const top = 9.0;
  const ring = mirrorRing(fpGame);
  const cx = -20;
  const cz = 5; // approximate mesh-average centroid (see GlassHoleProbe.cs's own note)
  const anchors = windowAnchorsWithNormal(ring, 4, baseY, top, cx, cz, () => null);
  const broken = new Array(anchors.pos.length / 3).fill(false);

  const bx = -20;
  const by = 4.8;
  const bz = 0;
  const radius = 12;
  const blown = blastWindows(anchors, broken, bx, by, bz, radius);
  const strideOk = blown.length % 5 === 0;
  const paneCount = blown.length / 5;
  let nearestFirst = true;
  let prevD2 = -1;
  for (let i = 0; i < paneCount; i++) {
    const o = i * 5;
    const dx = blown[o] - bx;
    const dy = blown[o + 1] - by;
    const dz = blown[o + 2] - bz;
    const d2 = dx * dx + dy * dy + dz * dz;
    if (d2 + 1e-4 < prevD2) nearestFirst = false;
    prevD2 = d2;
  }
  console.log(`BLASTWIN id=wall40x10 len=${blown.length} strideOk=${strideOk ? 1 : 0} paneCount=${paneCount} any=${paneCount > 0 ? 1 : 0} nearestFirst=${nearestFirst ? 1 : 0}`);

  // the SAME blast again: every pane it already broke must be gone for good.
  const again = blastWindows(anchors, broken, bx, by, bz, radius);
  console.log(`BLASTWIN id=wall40x10_restrip len=${again.length} consumedForGood=${again.length === 0 ? 1 : 0}`);

  const nearBroken = paneCount > 0 && nearWindowAt(anchors, broken, { x: blown[0], y: blown[1], z: blown[2] });

  const fpGame2 = [200, 0, 220, 0, 220, 10, 200, 10];
  const ring2 = mirrorRing(fpGame2);
  const anchors2 = windowAnchorsWithNormal(ring2, 4, baseY, top, -210, 5, () => null);
  const broken2 = new Array(anchors2.pos.length / 3).fill(false);
  const nearUntouched = anchors2.pos.length > 0
    && nearWindowAt(anchors2, broken2, { x: anchors2.pos[0], y: anchors2.pos[1], z: anchors2.pos[2] });
  console.log(`BLASTWIN id=nearwindow nearBrokenPaneIsFalse=${!nearBroken ? 1 : 0} nearUntouchedPaneIsTrue=${nearUntouched ? 1 : 0}`);
}

winanchFacts();
blastOrderFacts();
