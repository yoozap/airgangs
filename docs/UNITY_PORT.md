# TbilisiFly → Unity port plan

Target: Unity **6000.6.7f1** (Unity 6 LTS), **URP**, Apple-silicon editor.
Goals driving it: Steam/desktop, console + mobile, better lighting, multiplayer.

URP not HDRP — HDRP has no mobile or Switch support, and the console/mobile goal is
non-negotiable. URP still delivers most of the lighting win over the Three.js build.

## What carries over

| Layer | Carries | Notes |
|---|---|---|
| Data pipeline — 85 Python tools, OSM/Overpass fetchers, Blender builders | **100%** | Engine-agnostic. `build_map.py` emits `city.json.gz`, not meshes |
| Assets — 232 MB: 260 GLB, 742 textures, 204 WAV | **100%** | glTFast for props/planes; FBX for the 24 humanoid characters |
| City data — 15 MB `city.json`, roadnet, parks, river, 180k addresses, levels | **100%** | Plain JSON; no re-fetch, no re-derive |
| Backend — `server/index.js`, 501 lines + Mongo | **100%** | Unity does HTTP/WS; zero server changes |
| Game code — 53,277 lines JS | **0%** | Hand rewrite to C# |
| Shaders — 12 files hooking `onBeforeCompile` into Three's shader chunks | **0%** | HLSL / Shader Graph rewrite |

### Rig audit (measured — only the 4 bodies actually in play)

`RACES` in `src/characters.js` ships **4 playable bodies**, not 39:

| Body | Race | Bones | Humanoid | Clips | Unity rig |
|---|---|---|---|---|---|
| `banana` ბანანი | ხილ-ბოსტნეული | 22 | **18/18** | 2 | Humanoid, auto-map |
| `eggplant` ბადრიჯანი | ხილ-ბოსტნეული | 22 | **18/18** | 2 | Humanoid, auto-map |
| `toad` სოკო-კაცი | ხილ-ბოსტნეული | 22 | **18/18** | 2 | Humanoid, auto-map |
| `elephant` სპილო | სათამაშოები | 21 | 0/18 | 1 (Idle) | **Generic** |

The three fruit/veg bodies share an identical 22-bone UE-named rig
(`pelvis, spine_01..03, clavicle_l, upperarm_l, lowerarm_l, hand_l, neck_01, Head`) —
exactly what Unity's Avatar auto-mapper recognises, so **one animation set drives all
three**. Verified end to end: `banana.glb` → FBX, 22 bones, 18/18 landmarks, 2 actions.

The elephant ships only an Idle and gets a procedural run from `CharacterRig` today —
it is not humanoid, so that procedural run must be ported as-is.

`PERSONAGES` lists 13 and the folder holds 39; the rest exist only so already-saved
heroes keep the body they were created with. **Do not port them.** If saved-hero
compatibility doesn't need to survive the engine change, the other 35 models are dead
weight — that's 13 MB of `public/models/personages` mostly unused.

`WALK_SET`'s 8 directional walk cycles, with per-clip root-motion speeds read from
`moves.json`, become a single 2D blend tree — Unity blends directional locomotion
natively, so that hand-built compass indexing goes away.

## Coordinate frame — SETTLED

The game frame (`tools/build_map.py to_local`) is **X=west, Y=up, Z=north, metres,
right-handed**. Unity is left-handed. The port **mirrors X**:

    x_unity = -x_game      y, z unchanged
    ORIGIN  = 41.7252, 44.7458 (Delisi, Vazha-Pshavela — the spawn)
    M_LAT   = 111067.92066361613     M_LON = 83206.33860686835

Unity's frame becomes **X=east, Y=up, Z=north** — what Cesium for Unity and Mapbox
use, and it puts north on +Z so a compass heading of 0 is Unity's forward.

Mirroring one axis is not free. Three consequences, and missing any one gives you a
city that looks *almost* right:

1. **Every stored X flips sign.** Done at load, not by regenerating `city.json`, so
   the Three.js build keeps running off the same data for the length of the port.
2. **Every yaw flips sign.** Footprint-derived orientations self-correct (their
   vertices are mirrored too). Hand-tuned constants do not — there are 27:
   `planes.js` ×2 (−0.658, −0.702), `characters.js` ×2 (anime 180, ghost −90),
   `metro.json` ×23.
3. **Every generated mesh's triangle winding reverses** — or normals point inward
   and backface culling eats the city. One shared `FlipWinding`, not per generator.

Implemented in `unity/Assets/Scripts/Data/GeoFrame.cs`.

**VERIFIED** 2026-10-26 against glTFast 6.20.0 (`Assets/Editor/Probes/AxisProbe.cs`
+ `axis_probe.glb`): **glTFast negates X**, the same axis `GeoFrame` mirrors, and
leaves Y and Z alone. Every X-offset node flipped sign; the Y and Z arms passed
through untouched. Imported props and the generated city therefore agree, and nothing
had to flip. Re-run `AxisProbe` if glTFast is ever upgraded.

## Done already (not blocked on Unity)

- `unity/` project skeleton: `ProjectVersion.txt` pinned to 6000.6.7f1, URP +
  Input System + Cinemachine + Animation Rigging + Newtonsoft in `manifest.json`,
  OpenUPM scoped registry for glTFast, Unity `.gitignore`, LFS `.gitattributes`
- `tools/glb_to_fbx.py` — and the 3 humanoid bodies converted:
  `banana.fbx` / `eggplant.fbx` / `toad.fbx`, 22 bones, 2 actions each
- `tools/build_axis_probe.py` → `axis_probe.glb`, the handedness calibration asset
- `Assets/Scripts/Data/GeoFrame.cs` — the coordinate conversion above
- **Project created and building.** Unity 6000.6.7f1 opens `unity/` clean, 10/10
  packages resolved by the editor itself (URP 17.6.0, Input System 1.20.1,
  Cinemachine 6.6.0, Animation Rigging 6.6.0, Newtonsoft 3.2.2, glTFast 6.20.0,
  Draco 5.5.0, Collections 6.6.0, Burst 2.0.0, Mathematics 1.4.0) via
  `Assets/Editor/SetupPackages.cs`. Do not hand-pin package versions — that was
  wrong twice; let the package manager resolve.
- **1,138 assets imported** (`tools/unity_import_assets.sh`): 26 city data files to
  StreamingAssets, 67 planes, 738 textures, 212 audio, props/landmarks/statues/
  parks/houses. City JSON goes to StreamingAssets deliberately — runtime File IO,
  not Unity asset import.
- **Axis convention verified** — see above. `Assets/Editor/Probes/AxisProbe.cs`.
- **`src/aero.js` ported** → `Assets/Scripts/Flight/FlightModel.cs` (311 lines),
  and **proven** against the JS: `tools/_probe_aero.mjs` and
  `Assets/Editor/Probes/FlightProbe.cs` print the same fields for 4 planes;
  `tools/unity_port_check.py` compares 80 derived values with a relative tolerance
  (C# float32 vs JS float64 makes an exact diff useless). Worst difference 1.03e-6.

### Handedness, as actually derived for the body-axis math

Three body axes are forward −Z, up +Y, right +X; Unity's are forward **+Z**, up +Y,
right +X. Right and up are the same; only forward's label flips. From that:

- **Local airflow:** the JS's `-vLocal.z` ("airspeed along the nose") becomes
  `+vLocal.z`, so `alpha`/`beta` drop the minus on z.
- **Rotations are component-identical** between the engines — coordinate handedness
  and rotation handedness flip together and cancel. But because forward flips, the
  physical effect on the nose inverts for **pitch and yaw** and does **not** for
  **roll** (the wing effect is +X → +Y in both). The banked-turn rotation about world
  up is a yaw, so it negates too.
- **Cross products are component-identical but geometrically mirrored.** The JS lift
  direction `cross(right, velDir)` yields +Y in Three and **−Y** in Unity, so the
  operands must be swapped: `Cross(velDir, right)`. This one is easy to miss and
  would invert lift.

## Decisions (2026-10-26)

- **Parallel**: 4 agents on independent systems. Claude keeps **city + character**
  (critical path, hardest, most handedness risk); agents take **flight**, **combat**,
  **ui**, **core**.
- **EDITOR LOCK**: Unity batch mode takes an exclusive project lock, so only ONE
  process may run the editor on `unity/` at a time. Agents therefore write C# plus a
  probe plus the JS-side ground-truth script, and **never launch the editor**.
  Verification runs in batched passes by whoever owns the lock.
- **Reimplement, don't port**, where Unity has the feature natively:
  `characters`+`anims` (3,294) → Avatar retargeting + a 2D blend tree;
  `fire`/`smoke`/`glass` (1,151) → VFX Graph; `mapui` (778) → UI Toolkit;
  `devmode` (357) → the editor's own gizmos.
- **Bake the static city**: terrain, roads, parks and landmark placement bake to
  meshes via the existing Blender pipeline. Buildings STAY runtime-generated —
  per-building HP, the 10 crumble stages and carved blast holes index into chunk
  vertex ranges.
- **Nothing is cut.** The content tooling (cinema reel, crane + wind tunnel,
  shooting range, plane hall) stays in scope.

## Phase plan

Ordered so the city generator — the one piece that could force a design change —
is proven in phase 2, not month 8.

### Phase 0 · Project skeleton
Unity 6 LTS + URP, Mac/Windows/iOS/Android build modules, glTFast (+ Draco add-on,
`public/draco/` is already in use), Input System, Cinemachine. Git LFS for `public/`.
Settle metres-per-unit (currently 1:1 real metres — keep it).

### Phase 1 · Asset bridge
- glTFast for the 221 static GLB (planes, buildings, props) — no re-export
- Blender → FBX for **3 files** (banana, eggplant, toad) so Avatar auto-map works
  (script proven: `import_scene.gltf` → `export_scene.fbx`, `axis_forward=-Z, axis_up=Y,
  add_leaf_bones=False`). The elephant stays a Generic rig — GLB via glTFast is fine
- 204 WAV import as-is; rebuild the per-surface/per-role take banks as AudioClip sets
- 742 textures: set sRGB vs linear per use, or normals and roughness will be wrong

### Phase 2 · City generator — 14,415 lines  ← biggest, and the risk
`citymap.js` alone is 8,552 lines with 88 geometry-construction sites. Port to C#
`Mesh`, reading the same `city.json`. Then roads, bridges, parks, metro, malls,
shopfronts, landmarks, airport, zones, boundary.

**Keep runtime generation, do not bake.** Per-building HP, the 10 crumble stages and
the carved blast holes all index into chunk vertex ranges — baking to static meshes
kills the destruction, which is a centrepiece of this game.

The "detach chunks from the scene graph" perf win has **no Unity analogue**. Unity's
answer is `BatchRendererGroup` / `RenderMeshInstanced` with no GameObject per building.
Budget real time here; this perf work is from scratch.

### Phase 3 · Flight — 4,654 lines  ← easiest
`planephys`, `aero`, `planes`, `propeller`, `planehall`, `shed`. Hand-rolled integrator,
so it's near-mechanical math translation. **Do not** switch to Unity Rigidbody aero —
keep the existing integrator, including the real empty/MTOW masses and the stamina card.

### Phase 4 · Character — 9,891 lines  ← highest risk
`foot.js` is 6,458 lines: climb, cover, gaits, arm springs, footsteps, per-frame bone
writes. Unity path: Animator + Animation Rigging, manual bone writes in `LateUpdate`
*after* the Animator has run.

Shrunk by the 4-body roster: most of `characters.js` is a 39-body loader, per-model yaw
table, auto-rig and runtime retarget. For 3 identical humanoid rigs + 1 Generic
elephant, Unity's Avatar system and a 2D blend tree replace the bulk of
`characters.js` + `anims.js` (**3,294 lines**). The real work is `foot.js` — climb,
cover, gaits, arm springs — which is body-agnostic and ports in full.

### Phase 5 · Combat + damage — 10,939 lines
`guns`, `ballistics`, `weapons`, `shooting`, `gunanim`, `rockets`, `blast`, `impactfx`,
`planedamage`, `dents`, `glass`, `breakwall`, `breakup`, `papertargets`, `rangelane`,
`pickups`. The math ports; the shaders do not. Hardest: the fragment shader that
discards pixels inside every tear, the 4-variant hole atlas, and `surfaceFit`.

### Phase 6 · UI — 2,105 lines
`hud`, `mapui`, `skillbar`, `settings`, `target`. Use **UI Toolkit** — its USS is
CSS-like, so the cyberpunk theme's styling partly survives. Georgian font needs an
explicit TMP/UI Toolkit font asset with the full Mkhedruli range.

### Phase 7 · Core + net — 11,273 lines
`game`, `main`, `net`, `sound`, `audiofx`, `placement`, `devmode`, `wind`, `fire`,
`smoke`, `crash`, `cinema`, `crane`. `devmode`'s gizmo editor is free in Unity (the
editor *is* the gizmo), so that's 357 lines dropped.

## Traps to carry over

1. **Handedness — the #1 trap.** Three.js is right-handed Y-up; Unity is **left-handed**
   Y-up. Every `to_local()` lat/lon conversion, every yaw convention and every
   per-model yaw correction (9 planes + 8 personages were straightened by hand) must be
   re-derived. Get this wrong and the entire city is silently mirrored — every landmark
   in the wrong place, and it will look almost right.
2. `heightAt` must use the **triangle** sampler, not bilinear. The bilinear version was
   the "roads under grass" bug.
3. Extrude buildings over the **whole footprint**, not the centroid — that was
   "buildings under grass".
4. Bone writes must never rebase off their own output (the `qSame` / `freezePose`
   lessons). Unity's explicit layer order makes this easier, but the trap is the same.
5. Decals must be **fitted** to what they hit (4-corner probe) or they hang over thin
   edges and read as holes in mid-air. Unity's decal projectors have this same problem.
6. A hole drawn at its real 9 mm is invisible in play — tears open at 3 calibres.
7. **A win:** the Three.js lights-hash shader storm (a new PointLight recompiles every
   material) does not exist in URP's fixed light loop. Muzzle flashes and explosions
   get cheaper for free.

## Effort

Realistically **6–12 months solo** to parity: 53k lines plus 23 shader integrations.
The subtle things — seat pose freezing, quaternion drift, decal fitting, the lean
spring during climbs — are bugs that get rediscovered in a new engine's frame order.

The assets, the real-world data, the Blender pipeline and the audio — the genuinely
expensive and irreplaceable work — survive the port intact.

## Blocked on you

- **Build modules.** The editor has only MacStandalone + WebGL. Add **Windows**
  (Steam), **iOS** and **Android** through Unity Hub when you want those targets —
  Mac standalone is enough to develop against until then.
- Confirm whether the Three.js build keeps shipping in parallel during the port.
