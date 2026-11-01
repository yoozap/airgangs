# Orphan audit — 2026-10-27

One deliberate pass over the whole of `unity/Assets/Scripts/**/*.cs` (220 files,
378 non-interface/enum types) looking for the port's signature failure:
correct, often probe-verified code that nothing constructs, AddComponents,
ticks, or subscribes to. Ten instances of this were found previously, one
accident at a time (see `docs/QUALITY_BAR.md`'s "signature failure" section).
This pass found the rest in one sitting.

## Method

1. Enumerated every `class`/`struct` declaration (namespace-level) across
   `Assets/Scripts/**`, tagging each as `MonoBehaviour`-derived, `VisualElement`-
   derived (UI Toolkit), `static`, or plain.
2. For each type, searched **all of `Assets/Scripts/**` (production) and
   `Assets/Editor/**` (bakers + probes) separately** for:
   - `new (Qualified\.)*TypeName(` / `[` / `{` / `<` — construction, qualified
     or not (`new Fx.BulletTrailFx()` counts).
   - `AddComponent<(Qualified\.)*TypeName>` / `AddComponent(typeof(...))`.
   - `TypeName\.` — a static-factory or static-method call site (`CityData.Load()`,
     `Biquad.Design()`), because this codebase's dominant idiom for "construct
     a system" is a static `Load`/`Build` factory, not a bare `new` at the call
     site that reads the system's own name.
3. For `MonoBehaviour`s, additionally grepped each type's `.cs.meta` guid
   against the one scene that is actually the game: **`Assets/Scenes/Game.unity`**
   is the only one of the four committed scenes carrying `Boot`/`GameLoop`/
   `PlayableSceneBoot` — `Tbilisi.unity` has zero custom scripts and
   `Fly.unity`/`Hangar.unity` carry only early flight-test scaffolding. (Build
   settings list `Tbilisi` first, `Game` third — see the build-order finding
   below.)
4. Classified each type LIVE / PROBE_ONLY (constructed only under
   `Assets/Editor/**`) / DEAD (`MonoBehaviour`, zero reach anywhere) /
   DEAD_UNREFERENCED (plain type, zero reach anywhere, not even by name) /
   REFERENCED_NOT_CONSTRUCTED (named elsewhere — e.g. as a field type — but
   never built, usually a DTO built by `JsonConvert.DeserializeObject<T>`
   reflection, which no grep for `new T(` will ever find).
5. Manually audited every non-LIVE hit by hand (this is the expensive part —
   see false positives below) before trusting it, then read the surrounding
   code to find what the player actually loses.

### What the first two passes got wrong (false-positive modes), fixed in order

- **Qualified construction.** `new Fx.BulletTrailFx()` does not match `new
  BulletTrailFx(`. Fixed by allowing an optional `(Ident\.)*` prefix in both
  the `new` and `AddComponent<>` patterns.
- **Self-file construction never counted.** The biggest one. A class built
  and used entirely within the same file as its already-reachable owner (e.g.
  `CombatWorldAdapter.cs` building `new FxSink(...)` on its own line 462, or
  `GunAnim.cs`'s own `ClipCut` struct used 47 times inside that one file) was
  being reported DEAD because the search excluded `p != file`. Fixed by
  counting self-file construction too — it resolved ~30 of ~40 first-pass
  "orphans," none of which were real.
- **Static-factory idiom.** `CityData.Load()`, `WaterBodies.Build()`,
  `GrassCover.Build()` etc. construct their own type internally and hand back
  the result; the call site never writes `new CityData(`. Fixed by treating
  any `TypeName.Member` call from production as liveness evidence, which is
  how `CityData`/`TerrainSampler` — both extremely live, loaded by a dozen
  files — stopped reading as "probe-only."
- **`GetComponent<T>()` is not evidence `T` was ever attached.** The reverse
  direction of the AddComponent check: `SpinningPartTag` is *read* (`
  GetComponent<SpinningPartTag>() != null`) in two places but *written*
  nowhere — that pattern reads as "wired" on a naive grep for the type name
  and is in fact the actual bug (see table).

### Known false-negative modes of this method (for the guard and for future readers)

- **JSON-deserialized DTOs** (`JsonConvert.DeserializeObject<T>`) are built by
  reflection; `new T(` never appears. Mitigated by the `TypeName.Member`
  fallback (catches the call site that deserializes it) but a DTO used only
  as, say, a dictionary value type with no `.Member` access anywhere outside
  its own file can still misreport. Manually verified every hit in this
  category (`Core/NetClient.cs`'s response classes) by hand.
- **Delegate/event wiring is invisible to a construction grep.** The single
  biggest real finding in this pass (`GameLoop.OnHint` below) is not a missing
  constructor at all — the type exists and runs fine, it's a public
  `Action<string>` field that gets **written** from six call sites and
  **read** (subscribed, `+=`) from **zero**. No construction-based method
  catches this class of bug; it was found by grep for `OnHint\s*=` vs
  `OnHint\s*\+=` by hand, prompted by `HudView.cs`'s own comment that hint
  text is "outside this phase's scope." The guard script below checks a
  short, named list of these (not a general scan — see its docstring).
- **Editor-bake-only construction is not automatically a bug.** Many
  `PROBE_ONLY` hits (`RoadMeta`, `BridgeRail`, `PlaneAudioDef`, `CardTarget`,
  `MetroRenderer`, `MallRenderer`, `VegetationRenderer`, `SceneEnvironmentSetup`...)
  are constructed exclusively by `Assets/Editor/Build*.cs` scene bakers. That
  is this project's actual architecture — static city content is baked once
  into `Game.unity` by an Editor menu command, not regenerated at runtime
  (`docs/UNITY_PORT.md`/the unity skill's own "keep runtime city generation"
  rule is about *damage* state, not geometry authoring). This is a bug only
  if the **baked scene on disk doesn't actually contain the component** — see
  the staleness finding below, which is exactly that case for one of them.
- **A type can be "live" by this method's own standard and still never run**,
  if its one call site is itself unreached — I did not chase transitive
  reachability past 2-3 hops for every hit (e.g. "is `CombatWorldAdapter.Install`
  itself called" was checked by hand per finding, not automated).

## Scene build-order finding (not a script-level orphan, but the same shape)

`ProjectSettings/EditorBuildSettings.asset` lists scenes in this order:
`Tbilisi.unity` (0), `Fly.unity` (1), `Game.unity` (2). `Tbilisi.unity`
contains zero custom MonoBehaviours — it is an early-phase rendering-only
scene. A real Player build loads build-index 0 at startup with no code
anywhere calling `SceneManager.LoadScene`, so **a built player would open to
a scene with no `Boot`/`GameLoop`/`PlayableSceneBoot` at all.** This has no
consequence for the Editor `-executeMethod`/Play-mode workflow this project
actually uses (whoever presses Play has `Game.unity` open), but it means this
project is not buildable-and-runnable as a Player today without first
reordering Build Settings or adding a boot scene that loads `Game.unity`.
Flagging because it is the scene-level version of "correct content nothing
reaches."

## Scene staleness finding

`Assets/Scenes/Game.unity` (last modified 13:30) is **older than its own
baker** (`Assets/Editor/BuildPlayableScene.cs`, 19:04) **and older than
`SceneEnvironmentSetup.cs`** (20:38), which that baker adds via
`envGo.AddComponent<SceneEnvironmentSetup>()`. Grepping `SceneEnvironmentSetup`'s
guid against `Game.unity` directly returns **zero** hits. So the fix for the
original "no ACES tonemapping / no fog / no shadow distance" finding is
correctly coded and wired into the *baker*, but the **currently-committed
scene predates it and does not have it** — anyone running the game off today's
`Game.unity` without first re-running `BuildPlayableScene` still gets the
original bug. This is expected background churn with 4 other agents editing
the same scene-adjacent files live right now, not a code defect — but it means
**"is the component wired" and "is it in the scene on disk" are different
questions**, and only re-running the bake answers the second one. Recorded
here so it isn't mistaken for a regression later; no action taken (Unity
runs are expensive and the project lock is shared this session).

## Findings, ranked by what the player loses

| # | System | File(s) | Status | What's lost |
|---|--------|---------|--------|--------------|
| 1 | `GameLoop.OnHint` has **zero subscribers** | `Core/GameLoop.cs:220` (field); written from `PlaneHullDamage.cs`, `PlaneWallContact.cs`, `PlaneGroundRoll` (via `OnHint` assignment), `FootController` (via `_foot.OnHint`), `CombatWorldAdapter.cs` (airport/warehouse hints), `GameLoop.cs` itself (death, spawn-restore, enter-plane prompts) | **DEAD PIPE** — written 15+ times, read 0 | Every Georgian contextual prompt in the game: "✈ თვითმფრინავი შორსაა — მიუახლოვდი და დააჭირე Enter", the warehouse/airport return hints, the spawn-restore message, **and the death screen title** (`OnHint?.Invoke(DeathScreenText.Title)` at `GameLoop.cs:401,739`). None of these text lines ever reach a screen. `UI/Hud/HudView.cs`'s own header comment says the Georgian hint-text/healthbar-label copy is "outside this phase's scope" — confirming no consumer was ever built. |
| 2 | `CrashSequence.Hooks.ShowWasted` never assigned at all | `Core/CrashSequence.cs:58` (field), `:457,459` (invoked `?.Invoke()`); built at `Core/CombatWorldAdapter.cs:674-686` (`BuildCrash`'s `CrashHooks` object initializer sets `GroundAt`/`SpawnEmber`/`SpawnSmoke`/`Plume`/`SetBurning`/`ClearBurning` but not `ShowWasted`) | **DEAD, and silent** (`?.Invoke()` on null just no-ops) | A second, independent path to the same death screen, also dead — so a plane crash's own completion never shows WASTED-equivalent even if #1 is fixed later; `PlaneHullDamage.OnShowWasted` is the only one that even reaches the (also-dead) `OnHint` pipe. Two bugs stacked on the same feature. |
| 3 | The entire Cinema subsystem — 10 files, 2221 lines, probe-verified | `Cinema/CinemaFlight.cs`, `CinemaLetterboxMath.cs`, `CinemaLights.cs`, `CinemaRangeMath.cs`, `CinemaReel.cs` (521 lines), `CinemaScenes.cs`, `CinemaStage.cs`, `CinemaTypes.cs`; `UI/Hud/CinemaMenuView.cs`; `UI/Cinema/CinemaLetterboxView.cs` | **PROBE_ONLY** — `Editor/Probes/CinemaProbe.cs` and `CinemaLetterboxProbe.cs` construct and verify it; grepping every `Cinema*` type name outside the `Cinema/`+`UI/Hud`+`UI/Cinema` folders returns **zero** hits in `Core/GameLoop.cs`, `Boot.cs`, `CombatWorldAdapter.cs` | The entire 🎬 cinematic-camera feature (scene dropdown, letterboxing, the crates/climb/backflip/reload stunt reel from `src/cinema.js`) — fully ported, fully probe-verified, structurally disconnected. `GameLoop.cs` is explicit and honest about this at 5 separate comments: `// src/cinema.js is not ported anywhere ... cinema.active is never true`. This is the textbook shape of the project's signature failure, just not yet on the list. |
| 4 | `City/LandmarkTag` never attached | `City/LandmarkTag.cs` | **DEAD** (MonoBehaviour, zero `AddComponent`, zero scene hits) | Its own header already says this precisely: *"ATTACH THIS wherever a landmark/statue/shed GameObject is instantiated... the still-open 'which component goes in which scene' question."* Until something calls `AddComponent<LandmarkTag>()` on landmark/statue/shed objects, `CombatWorldAdapter.IsLandmark(Collider)` (which does `GetComponentInParent<LandmarkTag>()`) always returns false — landmarks take damage and collapse like any grey building, immunity/no-pose/MaxHp never applied. |
| 5 | `Combat/PlaneDamage.SpinningPartTag` never attached | `Combat/PlaneDamage.cs:92` (declared, `GetComponent` read at `:481`); `Combat/CrashDents.cs:211` (second read site) | **DEAD** (MonoBehaviour tag, zero `AddComponent` anywhere, including `Flight/PropellerSystem.cs` which detects the propellers this tag exists to mark) | `Spins(Transform)` (PlaneDamage.cs) always returns `false`; `CrashDents.Parts()`'s skip-propeller-meshes-from-denting logic never skips anything. Propeller/spinning-part meshes get treated as ordinary static panels for both damage classification and crash denting — a real, silent misclassification on every propeller-driven aircraft, not merely an unused tag. |
| 6 | Building damage never reaches the server; `src/net.js`'s damage persistence has no caller | `Core/NetClient.cs:304-333` (`GetDamage`/`PutDamage`/`DeleteDamage`/`GetAddresses`/`GetAddressCount`/`ImportAddresses`/`Health` — all `public static`, all correct); `City/BuildingDamage.cs:500-556` (`RemoteRow`/`MergeRemoteDamage` — correct "more damage wins" merge logic, zero callers anywhere, not even a probe) | **DEAD_UNREFERENCED** (NetClient methods) / **PROBE_ONLY→actually zero** (`MergeRemoteDamage`) | Per-building HP/holes exist only in memory for the session (`CombatBuildingChunk`'s own fields) and are gone on restart — no save, no load, no cross-session persistence, no merge with other players' damage. The real-address toast lookup (`City/BuildingDamage.cs:557-632`'s `AddressIndex`, a *local* StreamingAssets read, unaffected) is unrelated and fine; it's specifically the **server round-trip** that's wired nowhere. Memory note "damage DB in docker" describes a server feature the Unity client never talks to. |
| 7 | `OnHitToast` events have zero subscribers | `Combat/Guns.cs:107,265`, `Combat/Rockets.cs:93,240`, `Combat/ImpactFx.cs:342,415` (all declare + invoke `event Action<DamageResult> OnHitToast`); `Core/PlaneRaycastWorld.cs:58` (`ShowBuildingToastHook`, defaults to a no-op lambda, never reassigned) | **DEAD PIPE** (events invoked, zero `+=` anywhere in `Assets/Scripts/**` or `Assets/Editor/**`) | Every gun/rocket/building hit's "what did I just hit" toast (`Combat/Guns.cs`'s own comment: *"GunToast/[...] is ported as [...] pure logic [...] no DOM, so a UI/HUD consumer calls it"*) — the consumer was never built. Same shape as #1/#2: correct text-formatting logic (`GunToast.Format`), zero display. |
| 8 | `Core/PlacementEditor` never constructed | `Core/PlacementEditor.cs` (`PlacementEditor`, `CustomModelEntry`, `CustomModelInstance`) | **DEAD_UNREFERENCED** | The in-game photogrammetry-model nudge tool (port of `src/placement.js`) has no caller anywhere, **and** there is no `custom_models.json` on disk and no loader class that would turn its rows into `CustomModelInstance`s in the first place — wiring this needs a new loading pipeline, not just a constructor call, so it's reported rather than fixed (see below). |
| 9 | `Core/HeroLink.HeroLinkBehaviour` unused | `Core/HeroLink.cs:350-357` | **DEAD**, but **intentionally** — its own doc comment says *"Optional thin MonoBehaviour shim... Not required."* `GameLoop.cs` already calls `Hero.Tick(dt)` directly (`:1017`) and implements the equivalent of `OnApplicationFocus`/`OnDestroy` itself (`:1510,1564`). Attaching `HeroLinkBehaviour` on top would **double-tick** `HeroLink.Tick` every frame — exactly the two-orchestrator hazard the unity skill warns about. **Recommend deleting the class or leaving it exactly as-is; do not wire it.** |

### Everything else checked and confirmed fine (no entry needed)

`WindRider.Tick` (ticked by `WarehouseFanSwitch`), `City/SignPool` (constructed
by `ShopfrontRenderer`), `Combat/RangeLane.Key`/`ReleaseAll` (called from
`GameLoop.cs:1569,1807`), `ZoneBuilder.ZoneTracker` (ticked at
`GameLoop.cs:996`), `UI/Map/PinBeams`, `UI/Map/BigMapView`/`MapWindowView`/
`MiniMapView`/`MapUiRoot`, `Core/CombatWorldAdapter`/`FxSink`, `Data/CityData`,
`Data/TerrainSampler`, all `Fx/*` trail/flash/blast renderers (now constructed
— a different, currently-active agent pass; verified live as of this audit,
not reported further to avoid overlapping that work).

## What I wired myself

**Nothing.** Every finding above needs either a new UI element (#1, #2, #7),
a cross-file persistence-format decision with another agent's file
(`City/BuildingDamage.cs`, #6), a design decision about how a whole input
mode interacts with `GameMode` (#3, Cinema), a new data-loading pipeline that
doesn't exist yet (#8), or touches a file I don't own (`City/LandmarkTag.cs`
#4, `Combat/PlaneDamage.cs`/`Flight/PropellerSystem.cs` #5). None of them is
"exactly one obvious owner already ticks every frame, one line, no judgement
call" — the bar the brief sets for a same-pass fix. Forcing a fix under that
bar risked exactly the two-orchestrator class of bug this project already has
four deliberate examples of, so I held back and report precise edits instead.
`HeroLinkBehaviour` (#9) is the one case I actively **declined** to wire,
because wiring it is actively wrong (see above).

## Exact edits, for whoever owns each file

- **#1/#7 (hint + hit-toast display), `Core/*` + a UI consumer:** add one
  `Label` (or reuse `HudChipsView`'s chip style) bound to `GameLoop.OnHint`
  via `game.OnHint += text => label.text = text` with a short fade/clear
  timer, in whatever file already owns the HUD's non-flight-instrument chips
  (`UI/Hud/HudChipsView.cs` looks like the natural home — it already has a
  `Tick(HudChipsView.Data)` cadence to piggyback a hint-and-clear state onto).
  Then in `Combat/Guns.cs`/`Rockets.cs`/`ImpactFx.cs`, subscribe `OnHitToast +=
  res => game.OnHint?.Invoke(GunToast.Format(res))` (format helper already
  exists per `Combat/Shooting.cs:226`'s own comment) — one new line per file,
  in `CombatWorldAdapter.Install()` where `OnHitToast` is otherwise unwired,
  and similarly `PlaneRaycastWorld.ShowBuildingToastHook = res =>
  game.OnHint?.Invoke(...)`.
- **#2 (CrashSequence death hook):** in `Core/CombatWorldAdapter.cs`'s
  `BuildCrash`, add `ShowWasted = () => game.OnHint?.Invoke(DeathScreenText.Title)`
  to the `CrashHooks` object initializer (same line shape as the five hooks
  already there) — once #1's consumer exists, this one line closes the loop
  for the crash-sequence path specifically.
- **#3 (Cinema):** needs an owner decision, not a patch — whoever takes it
  should decide how cinema mode interacts with `GameMode.OnFoot`/`Flying` and
  input capture before wiring `CinemaMenuView`/`CinemaDirector` into
  `CombatWorldAdapter.InstallUi()`/`GameLoop`. Flagging for triage rather than
  guessing that interaction.
- **#4 (LandmarkTag):** `City/BuildingBuilder.cs` (or wherever
  `LandmarkPlacer.BuiltLandmarks`/`ProceduralLandmarks`/`HeroStatues` turn
  into real GameObjects, plus `Props/WarehouseCrane.cs`'s shed per the class's
  own comment) needs `go.AddComponent<LandmarkTag>()` with `LandmarkName`/
  `Immune`/`NoPose`/`MaxHp` filled from the same data `registerLandmark`
  read in the JS. Not mine to touch (City/*).
- **#5 (SpinningPartTag):** `Flight/PropellerSystem.cs`'s propeller-detection
  result (wherever it separates blades into their own GameObject/Transform)
  needs `AddComponent<SpinningPartTag>()` on that transform. Not mine to touch
  (Flight/Combat boundary).
- **#6 (damage persistence):** needs a schema decision — what key
  `MergeRemoteDamage`'s `Dictionary<string, RemoteRow>` uses has to match
  whatever `CombatBuildingChunk` can cheaply serialize per chunk, and that's a
  judgement call I didn't want to guess at risk of writing a mismatched key
  to the live server DB. Flagging with the exact call sites
  (`NetClient.GetDamage`/`PutDamage` + `BuildingDamage.MergeRemoteDamage`) for
  whoever picks it up.
- **#8 (PlacementEditor):** needs a `custom_models.json` loader built first
  (there is none) before `new PlacementEditor(...)` has anything to hand it;
  likely low priority since no `custom_models.json` exists in this project
  today.
- **#9 (HeroLinkBehaviour):** no edit recommended; leave unwired, or delete if
  it is bothering a coverage metric. Do not `AddComponent` it.

## The guard: `tools/_test_no_orphans.py`

Applies this audit's method (steps 1-3 above) automatically, exits non-zero on
any type outside a small allowlist that comes back DEAD/DEAD_UNREFERENCED/
PROBE_ONLY. The allowlist is exactly: the four documented dead duplicates
(`FootWeapons`, `FootBelt`, `FootBoarding`, `FootWeaponFrame`, plus their
nested helper types), `HeroLinkBehaviour` (intentionally unwired, see #9), and
the editor-bake-only city/cinema/audio/physics plain data types this audit
individually verified are fine (`RoadMeta`, `BridgeRail`, `BridgeFascia`,
`BridgeLamp`, `CardTarget`, `StageSetup`, `PlaneAudioDef`, `CollapseState`,
`CollapseAnim`, and siblings in the same files) — see the script's own
`ALLOW` list and docstring for the exact names and the reasoning per group.

**False-positive modes, documented in the script's docstring, honestly:**
1. A DTO built only by `JsonConvert.DeserializeObject<T>` and never named
   with a `.` after it anywhere outside its own file will misreport as dead —
   the script cannot see reflection-based construction. Verified by hand for
   every current NetClient DTO; a *new* one would need manual verification
   too, or an explicit allowlist entry.
2. A type used only inside its own already-reachable file, where that file's
   own outer type is itself unreachable (a transitively-dead file), will read
   as "self-constructed, therefore live" even though the whole chain is dead.
   The script does not chase reachability past the direct caller; it is a
   single-hop check deliberately, to stay fast and legible. (This pass found
   the Cinema subsystem's dead CHAIN by hand for exactly this reason — the
   automated single-hop check alone would have called every `Cinema*` type
   "live" because they construct each other inside `Cinema/`.)
3. **Delegate/event non-subscription (the `OnHint`/`OnHitToast` class of bug,
   findings #1/#2/#7) is NOT covered by this guard at all** — it is a
   different shape of orphan (a live field with writers and no readers, not
   an unconstructed type) and needs its own check. Not attempted here because
   a generic version of it is noisy (plenty of `Action`/`event` fields are
   legitimately write-only until a later phase wires them, e.g. mid-port). A
   named, short list of the ones found this pass is worth a second, narrow
   guard if this recurs.
4. A one-scene-only check (`Game.unity`) will not see a `MonoBehaviour` that
   is deliberately scene-specific to `Fly.unity`/`Tbilisi.unity`/`Hangar.unity`
   and misreport it dead. None exist today outside the three `Fly.unity`
   flight-test components, which this pass verified by hand are legacy
   scaffolding, not live content — but a future scene-specific component
   would need the scene list in the script extended.
