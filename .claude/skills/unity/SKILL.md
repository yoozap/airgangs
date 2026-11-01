---
name: unity
description: Work on the TbilisiFly Unity port — build, run and inspect the Unity 6 project headlessly, port a system from the Three.js source, or verify a port against the original. Use whenever the task touches unity/, a .cs file, the Unity editor, an Asset import, URP, a Prefab or Scene, or asks to port/migrate/translate any src/*.js system to C#. Covers the coordinate-frame rules that every port must obey.
---

# TbilisiFly → Unity

The port of a 53k-line Three.js flight game over real Tbilisi to Unity 6 / URP.
Full plan and phase order: `docs/UNITY_PORT.md`. Read it before starting a phase.

## The project

    Editor      /Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity
    Project     unity/
    Pipeline    URP (NOT HDRP — HDRP has no mobile or Switch support)
    Source      src/*.js is the reference implementation. It WORKS. Port it,
                don't redesign it.

Script folders mirror the port phases: `Assets/Scripts/{City,Flight,Character,Combat,UI,Core,Data}`.

## Run it headlessly — never ask the user to click

Every check in this project is scriptable. Do not hand the user a manual test.

    U=/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity

    # compile + import, exit non-zero on error
    "$U" -batchmode -quit -nographics -projectPath unity \
         -logFile /dev/stdout | grep -E "error CS|Compilation failed|Exception"

    # run a static method (the workhorse — see Probes below)
    "$U" -batchmode -quit -nographics -projectPath unity \
         -executeMethod TbilisiFly.Probes.Whatever -logFile /dev/stdout

Batch mode holds a project lock. If a call hangs or reports the project is already
open, the previous editor is still alive: `pkill -f "Unity.app/Contents/MacOS/Unity"`.

First import of 232 MB of art takes minutes — run it in the background, don't sit
on a 2-minute foreground timeout.

## ONE MonoBehaviour PER FILE, NAMED AFTER IT — or AddComponent silently half-works

`Core/CombatWorldAdapter.cs` declared THREE MonoBehaviours (`CombatTerrainTag`,
`CombatBuildingChunk`, `PlayableSceneBoot`) in a file named after a fourth,
non-MonoBehaviour class. Unity cannot bind a script ASSET to a MonoBehaviour
whose file is not named after it, so `AddComponent<T>()` produced components
that serialised with `m_Script: {fileID: 187830060}` and **no guid**, falling
back to `m_EditorClassIdentifier`.

The failure does not look like this at all. What you see is several unrelated
systems all null at once: a freshly-built scene that demonstrably contained a
GameObject named `PlayableSceneBoot` AND 605 chunks' worth of baked
`RangeStart` data — the YAML all present — while
`FindFirstObjectByType<PlayableSceneBoot>()` returned null, `Install()` never
ran, and `CombatSystems`, `CrashFactory`, the hero body and the UI came back
missing TOGETHER. It reads like four bugs. It is one.

It also intermittently "works": a component `AddComponent`'d in the same editor
session is live in memory, so a probe run that builds the scene and inspects it
without a domain reload can pass, and the same probe fails after a fresh launch
loads the saved scene. That flip-flop is the tell, and it got misattributed to a
stale scene once before the real cause was found.

How to check: grep the scene for the declaring file's guid. Zero references
while the serialised FIELDS are present is this bug exactly.

## A lazy-init accessor that falls back silently is a first-hit-only bug

`CombatBuildingChunk` derives a building's centroid, span and top from its mesh
inside `EnsureInit()`. Four accessors returned a FALLBACK (`transform.position`,
span 8 m) when that had not run yet, while their siblings `StageOf`/`HpFrac`
called `EnsureInit()` first. `CollectPanes` gates on centroid+span, so against
an untouched chunk it measured from (0,0) with span 8 and skipped the building:
**the first rocket to hit a pristine building broke no glass, and every hit after
it worked**, because something else had initialised the chunk by then.

Nothing crashes, nothing logs, and it only misbehaves once per object — so it
survives playtesting. If a class has an `EnsureInit()`, EVERY accessor that
reads derived state must call it. A probe is what found this (`BlastWindows`
returned 0 panes where its JS counterpart returned 10); reading the code did
not, and neither did the agent's own Node reimplementation, because the JS has
no lazy-init step to forget.

## TWO held-weapon orchestrators exist and both are inert — reconcile before wiring

`Core/HeldWeapon.cs` and `Character/FootWeaponFrame.cs` are INDEPENDENT
orchestrators over the same `RifleHolds` / `GunPoseBlend` methods, built by
different passes against different held-weapon models (`HeldWeapon`'s own, and
`Character/FootWeapons.cs`'s). `FootWeaponFrame.Tick()` is called from nowhere —
`FootController.cs` has zero references to it.

There is no bug today *because* it is inert. But wiring `FootWeaponFrame` into
`FootController`'s LateUpdate without first reconciling the two models will have
both driving the same arm bones and the same weapon transform in the same frame,
and the symptom will be a twitching or doubled arm pose that looks like a spring
or a pose-layer-order bug rather than a duplicated owner.

Decide which one owns the arms before wiring either. Do not wire both.

## Two trivia that silently make a probe verify NOTHING

Both have now cost real work, and both LOOK fine:

- **Print booleans lower-case.** C#'s `bool.ToString()` gives `True`; JavaScript
  prints `true`. The casing alone stops the line pairing, so the comparison
  skips it and reports no mismatch. Use
  `b.ToString().ToLowerInvariant()`. (Cost: 4 lines on Crane3Probe, 2 on
  CityMapRestProbe.)
- **The two sides' keys and field lists must match EXACTLY.** One extra field,
  or a prefix like `GR `, changes the line's shape and it pairs with nothing —
  `GameRestProbe` compared 7 of its 47 values for exactly this reason and read
  as "verified". Put derived extras on their own separate key instead.

When a probe reports "unmatched lines" or compares far fewer values than it
prints, assume a shape mismatch before assuming a port bug.

## Probes, not screenshots

The JS build was verified by ~400 `tools/_probe_*.mjs` Playwright probes that read
real numbers out of the running game. Keep that discipline: a port is "done" when a
probe proves it matches, not when it looks right.

Put editor-side checks in `unity/Assets/Editor/Probes/` as static methods and call
them with `-executeMethod`. Print one `KEY=value` line per fact and grep it. A probe
that reads mesh vertices, bone positions or physics state beats a screenshot.

### The project lock does not always say "already open"

With several agents sharing the exclusive lock, a blocked run often produces NO
diagnostic at all: the log stops right after "Successfully changed project path
to: ...", ends with `[ExitDontLaunchBugReporter] Exiting without the bug
reporter. Application will exit with return code 1`, and is about 1.3 KB. There
is no "another instance", no "lock", no `error CS`, no Exception. A retry loop
that greps for "already open|lock" treats this as a real failure and gives up.

Detect it by SIZE and by the absence of a verdict, not by a message:

    unity_run() {            # usage: unity_run <FullMethodName|""> <logfile>
      for i in $(seq 1 40); do
        if [ -z "$1" ]; then
          "$U" -batchmode -quit -nographics -projectPath unity -logFile "$2" >/dev/null 2>&1 && return 0
        else
          "$U" -batchmode -quit -nographics -projectPath unity -executeMethod "$1" -logFile "$2" >/dev/null 2>&1 && return 0
        fi
        # a real compile failure NAMES errors; a lock produces a stub log
        grep -qE "error CS" "$2" 2>/dev/null && return 1
        if [ "$(wc -c < "$2" 2>/dev/null || echo 0)" -lt 20000 ]; then
          sleep 40; continue          # blocked, not broken — wait it out
        fi
        return 1
      done
      echo "gave up waiting for the Unity lock"; return 1
    }

And never `pkill -f Unity` to jump the queue when others are working: it kills
their in-flight runs. One agent in this project was flagged for exactly that.

### Never reset an FBX's NATURAL import transform to identity

`crane_machine.fbx` imports with scale **(100,100,100)** and rotation
**(0,180,180)**; `fan.fbx` likewise; `p51.fbx` is scale 1 / identity. Two
builders captured a fresh Transform and set scale to one and rotation to
identity before measuring — which built the entire crane and all three fans
**~100x too small and facing the wrong way**. `ScaleToFit` happily reported
`scale=1.00` on a mesh that measured 18 cm tall instead of 18 m, so nothing
looked like an error anywhere.

Capture `naturalRot`/`naturalScale` from the imported prefab and COMPOSE with
them; never discard them. Per-asset, so check each FBX rather than assuming.

**The proof pattern worth copying:** after the fix the measured hook height came
out to exactly **7.9000 m**, matching `tools/build_crane_machine.py`'s own
`--hook 7.9` build constant — a number from the authoring tool that the runtime
had no way to know. Before the fix it read `(-0.12, 0.10, 0.04)`. When a port
can be checked against a constant from the asset PIPELINE, that beats any
tolerance argument.

### A probe can hit the same "never reached" trap it exists to catch

`AddComponent<T>()` from an `-executeMethod` script does **not** run `Awake()` —
there is no scene tick to deliver it. So a probe that added a component and then
called `Update()` five times was hitting `if (!_initialized) return;` on every
single call and silently proving nothing. Force it with an `InvokeAwake()`
reflection call, and be suspicious of any probe whose numbers never change.

### A probe proves a CALCULATION, not that the calculation is REACHED

The most expensive lesson of this port. `CharacterRig.cs:144` sets
`IsHumanoid = Anim.avatar.isHuman`, and **no model in this project is a Humanoid
avatar** — `grep -rl "animationType: 3" unity/Assets` returns 0 files, all 176
model `.meta` files say `animationType: 2` (Generic), and the four character
FBXs have an empty bone mapping. `IsHumanoid` is an early-return gate on the
torso twist, the whole pose rig, the shoulder pocket, the seat and boarding
poses, the hips lookup and the arm bones. So the arm poses, the belt and holster
seating, the seat fit and the climb's hand placement all compile, are all
probe-verified, and **none of them run**.

Around fifty probe pairs missed this. Eight of them even mention `IsHumanoid`
and not one fails on it, because they construct a rig and exercise the pure
functions — which proves the maths, and says nothing about whether the maths is
reached in the real game.

**So: pair every gated system with one assertion that its gate is actually open
on the real shipped asset.** Cheap forms of that assertion:

- a flag a system early-returns on: assert it is true on the SHIPPED asset, not
  on a fixture you built
- a renderer: assert instances were actually submitted and the renderer is
  enabled — a count you computed is not evidence anything was drawn
- a per-frame tick: assert the thing that calls it exists (`grep -rn "new X"`
  finding only probes is a red flag, not a pass)
- a loader stage: assert it has a RUNTIME caller. `CityLoader.RunCritical`/
  `RunScenery`/`RunAll` have none; only an Editor probe calls them, so anything
  added to `RunSceneryStage` runs in the bake and never in the game.

Corollary for porting: when a C# file's claim cites a module ZERO times, treat
the claim as unproven regardless of its size. `grep -c "<module>.js:" <file>`
against a trusted sibling is a five-second test that has now caught a 700-line
over-claim (`CombatWorldAdapter.cs`, 0 citations vs `GameLoop.cs`'s 102) and a
210-line one derived by subtraction.

## Coordinate frame — obey this or the city mirrors

`Assets/Scripts/Data/GeoFrame.cs` is the single source of truth. The game frame is
**X=west, Y=up, Z=north, right-handed**; Unity is **X=east, Y=up, Z=north,
left-handed**. The port mirrors X.

Every port that touches positions, rotations or generated geometry MUST:

1. Convert positions through `GeoFrame.ToUnity` / `FromLatLon` — never read a raw
   X out of `city.json` into a Unity transform.
2. Negate hand-tuned yaws through `GeoFrame.YawToUnity`. Footprint-derived
   orientations self-correct; the 27 hand-tuned constants do not
   (`planes.js` ×2, `characters.js` ×2, `metro.json` ×23).
3. Call `GeoFrame.FlipWinding` on every mesh built in C# from game-frame data, or
   normals point inward and backface culling eats the geometry.

A mirrored city still looks almost right. That is why it is dangerous.

## The quality bar — parity or better, never worse

**`docs/QUALITY_BAR.md` is the acceptance checklist. Read it before calling anything
done.** The user's requirement: *"i need minimum same quality like we had before."*

The Three.js build is not a rough draft — it is a finished game with real OSM data,
modelled geometry, real photographs, real audio and measured physics. A port that
renders grey extruded boxes is BELOW the bar even if the geometry is correct. Say so
plainly rather than presenting a scaffold as progress.

Specifically, for buildings: real storey counts (`levels.json`, 17,425 buildings),
real roof shapes (13 kinds), real wall materials (11 kinds), OSM 3D parts
(`parts.json`, 479 buildings), the existing Blender kit (`panel_block.glb` has REAL
cut window openings), exclusively-assigned photo facades, shopfronts with real names,
and working damage. Reuse the Blender builders in `tools/build_*.py` — they are
Python and engine-agnostic, so they still work.

## Unity 6 API traps already hit

- **`Object.GetInstanceID()` is obsolete** (CS0619) — use `GetEntityId()`. And the
  `EntityId -> int` implicit cast is ALSO obsolete, so do not assign it to an `int`.
  For a dictionary key, key on the object REFERENCE instead (a `Mesh` is a reference
  type with reference equality); for a debug string, `GetEntityId()` interpolates
  fine. Hit three times so far.
- **Probes live in `Assembly-CSharp-Editor`**, a different assembly from the game
  code, so an `internal` member is invisible to its own probe (CS1061, confusingly
  worded as "does not contain a definition for"). `Assets/Scripts/AssemblyInfo.cs`
  grants access with `InternalsVisibleTo`; do not widen members to public for this.
- **A local function may not capture an enclosing `ref`/`out` parameter** (CS1628).
  Give the local function its own `ref` parameters and pass them through.
- **A lat/lon must be `double`, never `float`.** float32 holds ~7 significant decimal
  digits, one short of a Tbilisi coordinate: 41.659490 rounds to 41.659488678, which
  is **0.147 m** on the ground. It silently moved the shed keep-out box and all 298
  street-statue positions (up to 0.212 m), against a project standard of 2 cm.
  Convert to float only AFTER subtracting the origin.
- **A namespace-level type is not a member of a sibling class.** `RiverBank`,
  `DeathScreenText` and `RailMotorVoice` all live at namespace level next to a class,
  so `InfraBuilder.RiverBank` / `CrashSequence.DeathScreenText` / `Core.Audio.X` do
  not compile. Hit three times.
- **`??=` on a `T?` yields `T`**, so there is no `.Value` left to take.

## JavaScript numeric semantics — three bugs a probe caught, not a reviewer

These all look correct in both languages. None would be found by reading the code,
and each one silently changed what the game draws. `Assets/Scripts/Data/JsMath.cs`
holds the fixes; use it wherever the JS line says `Math.round` or `>>> 0` AND the
result is load-bearing (a count, a loop bound, a grid index, a seed, an array
length). Display text does not matter.

- **`Mathf.RoundToInt` is NOT `Math.round`.** Unity's rounds a .5 tie to the nearest
  EVEN number; JavaScript's always rounds a tie toward +infinity. `Math.round(2.5)`
  is 3, `Mathf.RoundToInt(2.5f)` is 2. A 20 m building divides to exactly 2.5 in
  `3 + round(span / 8)`, so a collapsed building got 5 smoulder anchors where the
  game gives 6 — and the last anchor moved, because the angle is `k/n`. Use
  `JsMath.Round`.
- **`x >>> 0` does not translate to a `uint` literal.** C# promotes `int * uint` to
  **long** and never wraps, so `(bi * 2654435761u) % 1000u` yields 327 where JS —
  truncating to 32 bits first — yields 143. Every building then leaned on a
  different axis (cos 0.6228 vs -0.4652) and dragged its damaged vertices sideways.
  Use `JsMath.ToUint32((long)a * b)`. Knuth's 2654435761 is above `int.MaxValue`, so
  any hash using it is exposed.
- **The `sin(n) * 43758.5453` fract hash must be computed in `double`.** `q` reaches
  ~43758, where float32's resolution (~0.004) is coarser than the fractional part
  that IS the hash. At one sample float64 gave 0.943384 and float32 gave 0.474609 —
  a different random number, moving a damaged vertex 1.15 m. The ARGUMENT must be
  double too. It is worse for world-space inputs: the shared frame spans six CITY BBOXES in the data (mapmeta.json: tbilisi/batumi/kutaisi/gori/rustavi/kazbegi) and |x| reaches ~264 km, even though ONLY TBILISI IS PLAYABLE today — the others were locked cards and those screens are deleted (src/main.js:634), but the user has said they are coming later, so the far-field data stays and must not be bbox-filtered away,
  so `px * 12.9898` reaches ~3.4e6 where float32 cannot resolve 1.0 and the grass
  tint degenerates into bands. See `City/BuildingDamage.Fr`.

Note how this one was found: of the three axes `WriteBuildingVerts` writes, Y agreed
to ~0.003 while X and Z did not. A plausible-looking port passed inspection on one
axis. That is the argument for probes over review.

### float32 vs float64: it bites LOOP BOUNDS, not just hashes

The documented `sin(n)*43758.5453` fract-hash trap is one case of a bigger rule:
**JS has no float32. Every `Math.*` and every arithmetic op in `src/*.js` is a
double.** Narrowing any of it to `float` before a BRANCH or a THRESHOLD compare
can change what the game builds.

The case that proved it (parks, 2026-10-27): `ParkBuilder.Build`'s path-furniture
walk ported `len = Mathf.Sqrt(...)` and `for (float d = 0; d < len; d += 2f)` as
`float`, where `src/parks.js:171-215` has `Math.hypot` and a double `d`. On a
scattered handful of 13,354 real path segments the last iteration flipped, which
nudged the four shared accumulators (`accT/accB/accL/accF`, thresholds
15/27/33/140) across their limits a different number of times city-wide:

    trees   js 46627  cs 46737   (+110)
    benches js  5419  cs  5420   (+1)
    lamps   js  5111  cs  5112   (+1)
    beds    js   974  cs   973   (-1)

Note the shape: **small, mixed-sign, and only on counts gated by an accumulator.**
`pathSegs` matched exactly and `pathLen` drifted 0.25 m over 128 km — inside
tolerance. A port that only checked totals would have passed.

The diagnosis technique is worth copying: before touching the C#, reimplement the
loop in Node twice — once plain, once with every value through `Math.fround` —
and see which arm reproduces which side's numbers. Both arms matched exactly,
which turned "probably precision" into proof and named the file to fix.

Rule: keep the whole chain `double` and narrow to `float` only where a
`Vector3`/`GeoFrame` call demands it. Never narrow upstream of a comparison.

**The river case, same day, different system — and a trap inside the fix.**
`RiverSheet`'s ear-clipping triangulator disagreed with the Python reference by
12 verts / 16 tris citywide. It localised to exactly ONE of 76 rings (i=6, the
densest — 27 holes bridged into a 2399-point outer), where one near-zero cross
product in the is-ear test flipped sign in float32 and did not in double. One
stray triangle, doubled through two subdivision passes, is the whole delta.

**Widening only the ARITHMETIC did not fix it.** Making `Cross`/`IsReflex`/
`PointInTri` compute in `double` while still storing points in `Vector2` left
ring 6 wrong, because the coordinates had already been rounded to float32 at
JSON-parse time — there was nothing left to recover. The fix was a `Vec2D`
double struct carried from `ReadRing` all the way through triangulation,
narrowing to `float` once at mesh-write time. **Precision has to be carried from
the SOURCE, not restored at the point of comparison.**

Separately and independently, the same file summed 244,112 shoelace terms into a
`float` accumulator and drifted 368 m². Treat a count bug and an area bug as two
causes until proven otherwise; fixing one does not fix the other.

A probe that prints only TOTALS cannot find either of these. Print per-ring or
per-segment diagnostics on both sides and diff them — that is what turned "12
verts somewhere in a 157k-vertex mesh" into "ring 6, one triangle".

## Porting rules

- **The JS is the spec.** Port behaviour, including the odd-looking constants — they
  were measured. `// src/foo.js:123` comments on the ported block pay for themselves.
- **Keep the hand-rolled physics.** `planephys`/`aero`/`ballistics` integrate by hand.
  Do not replace them with Rigidbody or Unity's drag — the feel is tuned
  ("hot arcade thrust, realistic breaking only").
- **Keep runtime city generation.** Do not bake Tbilisi to static meshes: per-building
  HP, the 10 crumble stages and carved blast holes index into chunk vertex ranges.
- **No GameObject per building.** The Three.js "detach chunks from the scene graph"
  perf win has no Unity analogue. Use `BatchRendererGroup` / instanced draws.
- **Bone writes go in `LateUpdate`,** after the Animator. Never rebase a pose off
  its own output (that was the `qSame` / `freezePose` class of bug).
- **Assets:** glTFast reads the 221 static GLB directly. Only the 3 humanoid bodies
  go through FBX (`tools/glb_to_fbx.py`) so Unity's Avatar auto-mapper works.

## The roster is 4 bodies

`RACES[].bodies` in `src/characters.js`, not the 39 GLBs on disk:
`banana`, `eggplant`, `toad` (სოკო-კაცი — the "mushroom"), and `elephant` (toys race,
not humanoid, Idle only, procedural run). The other 35 exist so saved heroes keep
their body; do not port them.

### DPS/TANK are banned on the PERSONAGE screen only — they are CORRECT on planes

I got this wrong and repeated it in roughly ten agent briefs as a blanket "banned
on screen: DPS, TANK, WASTED". The real rule, from `src/characters.js:23-25`:

> The English DPS/TANK tag is gone from the screen ("remove tank dps strings
> man"); TANK and DPS now live where they decide something, **on the PLANES**
> (see planes.js planeRole).

And `src/target.js:213` renders exactly that: `u.role.textContent = tank ?
'🛡 TANK' : '⚔ DPS'` on the plane target card. `planehall.js` splits the hall
by the same property — 16 TANK machines one side, 21 DPS the other — because
"TANK and DPS are what the MACHINE is, not what the pilot is".

So:
- **Hero/personage UI** — Georgian role names only. No DPS/TANK. This is what the
  user asked for.
- **Plane UI** (target card, hall layout, plane cards) — `🛡 TANK` / `⚔ DPS` is
  the intended design. Do NOT "fix" it.
- **`WASTED`** stays banned everywhere: the death screen is Georgian, and an
  English "WASTED" was explicitly rejected.

A ui/city pass caught this and declined to touch the plane card, which was right.
Had an agent trusted my brief instead, it would have deleted a deliberate label.

## Georgian text

All UI strings are Georgian and must stay Georgian — there is no English fallback and
the user has rejected English strings before. Any font asset needs the full Mkhedruli
range or the HUD renders as boxes.

## Three ways finished code fails to run (check all three)

A probe proves a CALCULATION, not that the calculation is REACHED. Thirteen-plus
complete, probe-verified systems in this port had no caller. The checks:

1. **No constructor:** `grep -rn "new <Class>(" unity/Assets/Scripts` — zero
   hits, or hits only under `Assets/Editor/`, means dead. Beware three false
   positives the audit had to fix: qualified construction (`new Fx.BulletTrailFx()`),
   construction inside the class's own file, and the static-factory idiom
   (`CityData.Load()`).
2. **Not in the scene:** `Assets/Scenes/Game.unity` is the **only** scene that
   boots the game — `Fly.unity`, `Hangar.unity`, `Tbilisi.unity` are legacy.
   Grep the script's `.meta` guid against `Game.unity`; no hit means it does not
   exist at runtime. (`City/LandmarkRenderer.cs` is correct code that fails
   exactly here.)
3. **Dead delegate** — the shape greps miss: an event invoked many times and
   subscribed zero times. `GameLoop.OnHint` had 15+ invokes and 0 `+=`, silently
   dropping every Georgian hint and the death-screen title. For a `Hooks`-style
   field, assert the field appears left of an `=` outside its declaration;
   `CrashSequence.Hooks.ShowWasted` was never assigned. A probe can assert a
   hook is non-null and pass while the hook leads nowhere
   (`CoreRestProbe.cs:80`).

`tools/_test_no_orphans.py` guards #1 and #2 (378 types, 84 allowlisted). It does
**not** check #3, and its transitive reachability is single-hop — which is why it
would have called the whole orphaned `Cinema/*` subsystem live. Full method and
false-positive modes: `docs/ORPHANS.md`.

Also: `CityLoader.RunCritical`/`RunScenery`/`RunAll` have no runtime caller at all
(only an Editor probe). The pattern that runs in the game is `City/CityRenderer.cs`:
`OnEnable()` -> `EnsureBuilt()`. Any new renderer must follow it.

## Open the editor. Probes cannot see a class of bug that scene load shows in seconds

~400 probes, five parallel agents and three guards all passed while
`Assets/Scenes/Game.unity` contained none of the plane hall, warehouse, crane or
landmarks, and while `ParkBuilder` was building a mesh **over Unity's 4 GB
limit**. Both were obvious within 90 seconds of actually loading the scene:

    Loaded scene 'Assets/Scenes/Game.unity'
    Mesh 'playset_0' data size is too large (>4GB), the mesh might not work properly
        TbilisiFly.City.ParkBuilder:BuildPlacedAsset (ParkBuilder.cs:775)

A probe proves a calculation. It does not allocate the mesh, load the scene, or
exercise a renderer's `OnEnable`. So after wiring anything into the scene, do a
real load and read the log:

    U=/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity
    "$U" -batchmode -quit -nographics -projectPath unity \
         -executeMethod TbilisiFly.EditorTools.BuildPlayableScene.Run -logFile <log>
    grep -aE "data size is too large|Exception|error CS" <log>

and grep the `PARKS`/`SHOPFRONTS`/`GAMESCENE` lines to confirm the renderers ran
with the right counts. To open the GUI editor (it takes the exclusive lock, so
stop agents first, and quit it with `kill -TERM`, never `-9`):

    open -a "$(dirname $(dirname $(dirname $U)))" --args -projectPath <abs project path>

Editor log goes to `~/Library/Logs/Unity/Editor.log` until the project opens,
then moves to `<project>/Logs/Editor.log`.

**`BuildPlacedAsset` is the shape to watch for:** merging every placement of an
asset into ONE mesh. The city has 627 playsets, 5,111 park lamps, 46,627 trees.
Trees were fine because they go through `ChunkedInstancer.Place`; the furniture
never got routed through it. Any "combine N copies into one mesh" is a port
deviation here — the JS instanced this geometry.

## Unity Hub will not list a project record it did not write

`~/Library/Application Support/UnityHub/projects-v1.json` can hold a correct,
complete entry (right path, right `version`, written before the Hub started) and
Hub 3.22 still shows nothing, because a hand-written record is thinner than one
it creates itself. Do not debug this from the outside: have the user click
**Add ▾ → Add project from disk**. Back the JSON up first — the Hub rewrites that
file from memory on quit.

## UI Toolkit: a `--` inside a UXML comment imports as an EMPTY asset with NO error

`<!-- ... var(--bg0) ... -->` is illegal XML (`--` may not appear inside a
comment). `AssetDatabase.LoadAssetAtPath<VisualTreeAsset>` returns a NON-NULL
asset, `CloneTree` yields 0 elements, the import log says nothing, and the only
symptom is an anonymous NullReferenceException at whichever field dereferences
first. Pre-flight every `.uxml` for free:

    python3 -c "import xml.etree.ElementTree as ET; ET.parse('unity/Assets/UI/Shell/Shell.uxml')"

and have the view throw ONE message naming every element the UXML failed to
yield. CSS custom properties (`--x`) are the usual culprit.

## Views must Unbind from long-lived events, or the dead one keeps handling them

`Boot` lives the whole session; a disposed `CharacterView` stayed subscribed to
`Boot.ScreenChanged` and handled it with a destroyed preview camera behind it --
`MissingReferenceException: 'UnityEngine.Camera' has been destroyed` FROM THE
OLD VIEW while the new one worked. Reads like a camera bug; is a subscription
lifetime bug. Every view that subscribes to an outliving publisher needs an
`Unbind()` called from its owner's `Dispose`. Outside Play mode use
`DestroyImmediate` or the runtime `UIDocument` leaks and two live panels make
`FindAnyObjectByType<UIDocument>()` a coin toss.

## The two reachability guards must be read TOGETHER

`_test_no_dead_events.py` counts a `+=` anywhere under `Assets/Scripts` as a
live subscription -- including one inside a type that `_test_no_orphans.py`
says nothing constructs. `Boot.ScreenChanged/RosterChanged/AuthError` were
never baselined as dead because `CharacterView.Bind()` subscribed them, and
`CharacterView` was PROBE_ONLY. A green dead-events guard + a PROBE_ONLY
subscriber = a dead event.

## `RenderMeshInstanced<T>` does NOT carry custom per-instance data — use a MaterialPropertyBlock array

`Graphics.RenderMeshInstanced<T>(rp, mesh, sub, T[] data)` resolves `T`'s
fields BY NAME against the built-ins only (`objectToWorld`, `prevObjectToWorld`,
`renderingLayerMask`). A struct `{Matrix4x4 objectToWorld; Vector4 Tint;}`
throws `ArgumentException: Field passed in is not a marshaled member` — visible
only as `TICKER id=threw type=CityRenderer` in a render probe, with the
renderer's frame counters stuck at 0. Renaming the field to the shader property
(`_InstTint`) makes the draw SUCCEED and the data still never arrives: a
three-instance pixel test read back 0.8/0.8/0.8 for RED/GREEN/BLUE. That would
have shipped 169,004 flat grey buildings with every counter better than before,
because the kit FBX materials are neutral and the hash tint is the kit's ONLY
colour. The working mechanism: `UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)`
in the shader + a `MaterialPropertyBlock` **vector array**
(`SetVectorArray("_InstTint", …)`) through `RenderParams.matProps`, built once
per batch and cached. Prove tint with pixels (`CITYKIT_TINT verdict=PASS`),
never with counters. Keep `_BaseColor` in `UnityPerMaterial` on the chunk-mesh
shader (`CityWall`) or it loses SRP-Batcher compatibility — that is the path
with the 17→4.4 ms history; the kit got its own `CityKit.shader`.

## A probe camera only sees what ticked — check the ticker threw

In a batch render, every `Graphics.RenderMeshInstanced` renderer must be ticked
by hand (`LateUpdate` is not delivered outside play mode). If one ticker throws,
its layer is simply ABSENT from the frame and nothing else says so: the first
Old Town screenshots had `FrameDrawCalls=0` for the whole building kit and read
as "the city renders" because the chunk meshes did. Print a `TICKER id=…
type=… threw` line per renderer and treat any throw as an empty frame.
