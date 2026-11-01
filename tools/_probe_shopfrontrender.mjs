// Verifies City/ShopfrontRenderer.cs — the render layer that closes
// City/CityLoader.cs's own "shopfronts" GAP comment (the render layer for
// src/shopfronts.js:240-376). Most of what that file does (chunk-instancing
// onto a Unity FBX kit, Graphics.RenderMeshInstanced, MeshFilter/MeshRenderer
// attachment, a MonoBehaviour's OnEnable/LateUpdate) has no Three.js
// counterpart to diff against — same documented exemption
// unity/Assets/Editor/Probes/CityRenderProbe.cs already uses for
// City/CityRenderer.cs (there is no InstancedMesh-vs-RenderMeshInstanced
// parity to check; Three.js IS the InstancedMesh).
//
// What DOES have a precise JS reference, and is checked numerically here
// against ShopfrontRenderProbe.cs's matching `SFR id=...` lines:
//
//   1. The chunk-bucketing formula src/citymap.js:7956-7960
//      `${Math.floor(x/chunkM)}_${Math.floor(z/chunkM)}` — shared by every
//      instanced layer in the city, exercised here for the SAME 5 synthetic
//      points the Unity probe feeds through ChunkedInstancer.Place for
//      shopfront modules (chunkM=500, src/shopfronts.js:255) and crosses
//      (chunkM=800, src/shopfronts.js:324).
//   2. The pixel values src/shopfronts.js:261-272 `stripes()` and 289-316
//      `boxKind`/`crossMats` paint, at two sample points each — deterministic
//      fillRect containment, computed directly (no DOM canvas needed, same
//      "replicate the algorithm" precedent tools/_probe_shopchar.mjs already
//      uses for this same source file).
//
//   node tools/_probe_shopfrontrender.mjs

const f = (v, n = 4) => Number(v).toFixed(n);

// =============================================================================
// 1. chunk bucketing — src/citymap.js:7956-7960, replicated exactly.
// =============================================================================
const pts = [[0, 0], [100, 50], [600, 0], [0, 600], [900, 900]];

function bucket(chunkM) {
  const buckets = new Map();
  for (const [x, z] of pts) {
    const k = `${Math.floor(x / chunkM)}_${Math.floor(z / chunkM)}`;
    if (!buckets.has(k)) buckets.set(k, []);
    buckets.get(k).push([x, z]);
  }
  const sizes = [...buckets.values()].map((v) => v.length);
  const total = sizes.reduce((a, b) => a + b, 0);
  return { buckets: buckets.size, minSize: Math.min(...sizes), maxSize: Math.max(...sizes), total };
}

for (const chunkM of [500, 800]) {
  const r = bucket(chunkM);
  console.log(`SFR id=chunk_bucket chunkM=${chunkM} buckets=${r.buckets} minSize=${r.minSize} maxSize=${r.maxSize} totalInstances=${r.total}`);
}

// =============================================================================
// 2a. stripe texture — src/shopfronts.js:261-272 `stripes(c1, c2)`.
//     g.fillStyle=c1; fillRect(0,0,64,8); g.fillStyle=c2;
//     for (i=0;i<8;i++) fillRect(i*8, 0, 4, 8);
//     -> for any x, c2 covers [i*8, i*8+4) for every i, i.e. (x % 8) < 4.
// =============================================================================
{
  const c1 = [0xb4 / 255, 0x3a / 255, 0x2a / 255]; // src/shopfronts.js:274 '#b43a2a'
  const c2 = [0xef / 255, 0xe8 / 255, 0xdc / 255]; // '#efe8dc'
  const at = (x) => ((x % 8) < 4 ? c2 : c1);
  const x2 = at(2), x6 = at(6);
  console.log(`SFR id=stripe_tex x2r=${f(x2[0])} x2g=${f(x2[1])} x2b=${f(x2[2])} x6r=${f(x6[0])} x6g=${f(x6[1])} x6b=${f(x6[2])}`);
}

// =============================================================================
// 2b. cross/police plate — src/shopfronts.js:289-316 `boxKind`/`crossMats`.
//     fillStyle=bg; fillRect(0,0,128,128); fillStyle=mark;
//     fillRect(48,16,32,96); fillRect(16,48,96,32);  // a plus sign
//     Sample (64,64): inside BOTH rects (x in [48,80)&[16,112), y in
//     [16,112)&[48,80)) -> mark colour on every kind. Sample (4,4): outside
//     both -> bg colour. Only kind 0 (pharmacy, green cross on white) is
//     checked here — ShopfrontRenderProbe.cs's synthetic pass only
//     populates kind 0 (see its own comment), matching this single line.
// =============================================================================
{
  const bg = [1, 1, 1];                          // '#ffffff', src/shopfronts.js:299
  const mark = [0x1c / 255, 0x9a / 255, 0x4c / 255]; // '#1c9a4c', :300 (pharmacy green)
  console.log(`SFR id=cross_tex kind=0 centerR=${f(mark[0])} centerG=${f(mark[1])} centerB=${f(mark[2])} `
    + `cornerR=${f(bg[0])} cornerG=${f(bg[1])} cornerB=${f(bg[2])}`);
}

// =============================================================================
// NOTE lines — facts this side legitimately cannot know / cannot check
// (counted by tools/unity_port_check.py, never paired — see its own header).
// =============================================================================
console.log('NOTE this probe has no way to open the Unity editor or read the real facade_kit.fbx asset; the '
  + '"win_shop missing, only ac present" finding was verified directly with a headless Blender import (this '
  + 'task\'s report), not re-derived here.');
console.log('NOTE Graphics.RenderMeshInstanced / MonoBehaviour OnEnable+LateUpdate / MeshFilter+MeshRenderer '
  + 'have no Three.js counterpart to diff against (Three.js IS the instancing API here) — same exemption '
  + 'CityRenderProbe.cs already documents for CityRenderer.cs.');
