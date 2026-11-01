// Verifies Unity City/SignPool.cs (src/shopfronts.js:378-652 SignPool) plus
// the decisive wall-snap-normal test for City/ShopfrontBuilder.cs
// (src/shopfronts.js:139-185). src/shopfronts.js exports nothing usable
// headless (buildShopfronts is async, fetches JSON, touches document/canvas),
// so every number below is a cited REPLICATION of the JS algorithm run
// against the SAME synthetic inputs unity/Assets/Editor/Probes/
// ShopCharProbe.cs feeds the real ported C# (SignPool.cs + ShopfrontBuilder's
// wall-snap loop). Key names and shapes are kept IDENTICAL between the two
// files on purpose — see .claude/skills/unity/SKILL.md and this task's own
// "a GR prefix cost one probe 40 of its 47 lines" warning.
//
//   node tools/_probe_shopchar.mjs

const f = (v, n = 4) => Number(v).toFixed(n);

// =============================================================================
// ---- 1. THE DECISIVE TEST — wall-snap normal, mirrored into Unity ---------
// Replicates src/shopfronts.js:142-185 for one synthetic 4-vertex building
// and one POI sitting just outside its +X wall, then applies the SAME mirror
// ShopfrontBuilder.cs applies (NormalUnity = (-nx,0,nz), YawUnity =
// GeoFrame.YawToUnity(wallAng) = -wallAng), then asks: does a sign authored
// facing local +Z (Unity's own "forward", same convention
// src/shopfronts.js's `m.makeRotationY(ang)` relies on for local +Z in
// Three.js) end up facing the SAME direction as the outward wall normal?
// =============================================================================
{
  // A 10x10 square, metres, arbitrary game-frame origin. Winding does not
  // matter -- the flip-toward-outside check below corrects either winding.
  const verts = [[0, 0], [10, 0], [10, 10], [0, 10]];
  const poi = { x: 10.5, z: 5, maxD: 2.0, d: 1e9, wall: null };
  const n = verts.length;
  let cx = 0, cz = 0;
  for (const [x, z] of verts) { cx += x; cz += z; }
  cx /= n; cz /= n;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    const [x1, z1] = verts[i];
    const ex = verts[j][0] - x1, ez = verts[j][1] - z1;
    const len = Math.hypot(ex, ez);
    const mx = x1 + ex / 2, mz = z1 + ez / 2;
    let t = ((poi.x - x1) * ex + (poi.z - z1) * ez) / (len * len);
    t = Math.max(0.08, Math.min(0.92, t));
    const px = x1 + ex * t, pz = z1 + ez * t;
    const d = Math.hypot(poi.x - px, poi.z - pz);
    if (d < poi.maxD && d < poi.d) {
      let nx = -ez / len, nz = ex / len;
      if ((mx - cx) * nx + (mz - cz) * nz < 0) { nx = -nx; nz = -nz; }
      poi.d = d;
      poi.wall = { x: px + nx * 0.1, z: pz + nz * 0.1, nx, nz, ang: Math.atan2(nx, nz), len };
    }
  }
  const w = poi.wall;
  console.log(`SHOPCHAR_WALL_NX=${f(w.nx)}`);
  console.log(`SHOPCHAR_WALL_NZ=${f(w.nz)}`);
  console.log(`SHOPCHAR_WALL_ANG=${f(w.ang)}`);

  // ShopfrontBuilder.cs:358-359 — the mirror.
  const normalUnityX = -w.nx, normalUnityZ = w.nz;
  const yawUnity = -w.ang; // GeoFrame.YawToUnity
  console.log(`SHOPCHAR_WALL_UNITY_NORMAL_X=${f(normalUnityX)}`);
  console.log(`SHOPCHAR_WALL_UNITY_NORMAL_Z=${f(normalUnityZ)}`);
  console.log(`SHOPCHAR_WALL_UNITY_YAW=${f(yawUnity)}`);

  // Unity convention (verified against Quaternion.Euler(0,90,0)*Vector3.forward
  // == Vector3.right): local +Z after a yaw rotation of `yawUnity` radians
  // lands at world (sin(yawUnity), 0, cos(yawUnity)).
  const fwdX = Math.sin(yawUnity), fwdZ = Math.cos(yawUnity);
  console.log(`SHOPCHAR_WALL_UNITY_FORWARD_X=${f(fwdX)}`);
  console.log(`SHOPCHAR_WALL_UNITY_FORWARD_Z=${f(fwdZ)}`);
  const dot = fwdX * normalUnityX + fwdZ * normalUnityZ;
  console.log(`SHOPCHAR_WALL_DOT=${f(dot)}`); // must be > 0 (and ~1 by construction)
}

// =============================================================================
// ---- 2. SignPool pooling algorithm — REPLICATED src/shopfronts.js:383-651 -
// =============================================================================
{
  const POOL = 192, RANGE = 460, CELL = 250;
  const spots = [];
  for (let i = 0; i < 250; i++) spots.push({ x: i * 10, z: 0 });

  const grid = new Map();
  const key = (gi, gj) => `${gi}_${gj}`;
  for (let i = 0; i < spots.length; i++) {
    const gi = Math.floor(spots[i].x / CELL), gj = Math.floor(spots[i].z / CELL);
    const k = key(gi, gj);
    if (!grid.has(k)) grid.set(k, []);
    grid.get(k).push(i);
  }
  const slotSpot = new Array(POOL).fill(-1);
  const spotSlot = new Map();
  let cool = 0;

  function update(camX, camZ, dt) {
    cool -= dt;
    if (cool > 0) return { throttled: true, assigned: 0, freed: 0 };
    cool = 0.55;
    const ci = Math.floor(camX / CELL), cj = Math.floor(camZ / CELL);
    const reach = Math.ceil(RANGE / CELL);
    const near = [];
    for (let di = -reach; di <= reach; di++) {
      for (let dj = -reach; dj <= reach; dj++) {
        const cell = grid.get(key(ci + di, cj + dj));
        if (!cell) continue;
        for (const si of cell) {
          const s = spots[si];
          const d2 = (s.x - camX) ** 2 + (s.z - camZ) ** 2;
          if (d2 < RANGE * RANGE) near.push([d2, si]);
        }
      }
    }
    near.sort((a, b) => a[0] - b[0]);
    const want = new Set();
    for (let i = 0; i < Math.min(POOL, near.length); i++) want.add(near[i][1]);
    const free = [];
    let freed = 0;
    for (let slot = 0; slot < POOL; slot++) {
      const si = slotSpot[slot];
      if (si < 0) { free.push(slot); continue; }
      if (!want.has(si)) {
        spotSlot.delete(si);
        slotSpot[slot] = -1;
        free.push(slot);
        freed++;
      } else {
        want.delete(si);
      }
    }
    let assigned = 0;
    for (const si of want) {
      const slot = free.pop();
      if (slot === undefined) break;
      slotSpot[slot] = si;
      spotSlot.set(si, slot);
      assigned++;
    }
    return { throttled: false, assigned, freed };
  }

  const r1 = update(1000, 0, 1.0);
  console.log(`SHOPCHAR_POOL_CALL1_ASSIGNED=${r1.assigned}`);
  console.log(`SHOPCHAR_POOL_CALL1_FREED=${r1.freed}`);

  const r2 = update(1200, 0, 1.0);
  console.log(`SHOPCHAR_POOL_CALL2_ASSIGNED=${r2.assigned}`);
  console.log(`SHOPCHAR_POOL_CALL2_FREED=${r2.freed}`);
  const active = slotSpot.filter((s) => s >= 0).length;
  console.log(`SHOPCHAR_POOL_CALL2_ACTIVE=${active}`);

  const r3 = update(1200, 0, 0.1); // inside the 0.55s cooldown set by call 2
  console.log(`SHOPCHAR_POOL_THROTTLED=${r3.throttled ? 1 : 0}`);
}

// =============================================================================
// ---- 3. draw()/drawBrand() decision logic — REPLICATED 508-606 ------------
// =============================================================================
{
  const CAT_STYLE = {
    hospital: ['#ffffff', 'საავადმყოფო'],
    school: ['#f4f1e8', 'საჯარო სკოლა'],
    pharmacy: ['#2a9d5c', 'აფთიაქი'],
    named: ['#2b2723', ''],
    supermarket: ['#1c7a3c', 'სუპერმარკეტი'],
  };
  const CIVIC_CATS = new Set(['school', 'kindergarten', 'university', 'hospital',
    'clinic', 'police', 'fire_station', 'library', 'theatre', 'post']);

  function computeDraw(spot) {
    const [bg, fallback] = CAT_STYLE[spot.cat] ?? ['#555', ''];
    const name = spot.name ?? fallback;
    if (spot.cat === 'named') return { text: name, bg, fg: '#d6b66e', named: true, civic: false, lightBg: false };
    const civic = CIVIC_CATS.has(spot.cat);
    const lightBg = (civic && bg.startsWith('#f')) || bg === '#ffffff';
    const fg = lightBg ? '#1a3a6e' : '#ffffff';
    return { text: name, bg, fg, named: false, civic, lightBg };
  }
  function computeDrawBrand(b) {
    const bg = b.bg || '#333';
    const fg = b.fg || '#fff';
    const text = b.wordmark || ''; // spot.name is never set in the real game — see header note
    const hasMark = b.mark && b.mark !== 'none';
    return {
      text, bg, fg, hasBrand: true,
      markKind: hasMark ? b.mark : 'none',
      markColor: hasMark ? (b.accent || fg) : null,
      markAccent: hasMark ? bg : null,
      scriptLatin: b.script === 'latin',
    };
  }

  const A = computeDraw({ cat: 'hospital' });
  console.log(`SHOPCHAR_DRAW_A_BG=${A.bg}`);
  console.log(`SHOPCHAR_DRAW_A_FG=${A.fg}`);
  console.log(`SHOPCHAR_DRAW_A_CIVIC=${A.civic ? 1 : 0}`);
  console.log(`SHOPCHAR_DRAW_A_LIGHTBG=${A.lightBg ? 1 : 0}`);

  const B = computeDraw({ cat: 'school' });
  console.log(`SHOPCHAR_DRAW_B_LIGHTBG=${B.lightBg ? 1 : 0}`);
  console.log(`SHOPCHAR_DRAW_B_FG=${B.fg}`);

  const C = computeDraw({ cat: 'pharmacy' });
  console.log(`SHOPCHAR_DRAW_C_CIVIC=${C.civic ? 1 : 0}`);
  console.log(`SHOPCHAR_DRAW_C_LIGHTBG=${C.lightBg ? 1 : 0}`);
  console.log(`SHOPCHAR_DRAW_C_FG=${C.fg}`);

  const D = computeDraw({ cat: 'named', name: 'საჯარო რეესტრის სააგენტო' });
  console.log(`SHOPCHAR_DRAW_D_NAMED=${D.named ? 1 : 0}`);
  console.log(`SHOPCHAR_DRAW_D_FG=${D.fg}`);
  console.log(`SHOPCHAR_DRAW_D_TEXT=${D.text}`);

  const E = computeDrawBrand({
    bg: '#00563f', fg: '#ffffff', wordmark: 'ნიკორა',
    mark: 'leaf', accent: '#ffd200', script: 'georgian',
  });
  console.log(`SHOPCHAR_DRAW_E_TEXT=${E.text}`);
  console.log(`SHOPCHAR_DRAW_E_MARKKIND=${E.markKind}`);
  console.log(`SHOPCHAR_DRAW_E_MARKCOLOR=${E.markColor}`);
  console.log(`SHOPCHAR_DRAW_E_MARKACCENT=${E.markAccent}`);
  console.log(`SHOPCHAR_DRAW_E_SCRIPTLATIN=${E.scriptLatin ? 1 : 0}`);

  const F = computeDrawBrand({ bg: '#333', fg: '#fff', mark: 'none' });
  console.log(`SHOPCHAR_DRAW_F_TEXT_EMPTY=${F.text === '' ? 1 : 0}`);
}
