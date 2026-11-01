# TbilisiFly → Unity: what moved, what is left

_Generated 2026-11-01 22:49 by tools/port_status_md.py. Numbers come from the port manifest, the unit claims and docs/open_items.json; do not edit by hand._

## 1. The one-line answer

**46,888 of 48,889 budgeted JS lines are ported (95.9%), 45,101 of them probe-verified; 2,001 lines remain, which the audit (docs/BUDGET_AUDIT.md) found to be mostly budget artefact, not behaviour.** The C# side is 111,446 lines in 399 files. Every box of the quality bar (section 3) is checked with a live proof. Tracker: 150 fixed · 8 open · 7 decisions · 2 deferred.

Lines are not the whole story: the port's worst bug class is FINISHED code that never runs (no caller, not in Game.unity, dead delegate) and formulas that are right in a right-handed frame and wrong in Unity's left-handed one. Sections 4–6 are about those.

## 2. What moved, per system

| system | JS lines | ported | verified | left | % | what the rest is |
|---|---:|---:|---:|---:|---:|---|
| city | 13,361 | 13,038 | 12,557 | 323 | 97.6 | 8552-line generator plus instanced-rendering perf work from scratch — the Three.js "detach chunks from the scene graph" win has no Unity ana |
| combat | 10,834 | 10,593 | 10,244 | 241 | 97.8 | Ballistics, damage decals and tear geometry — lots of surface math, all probe-checkable. |
| character | 9,343 | 8,893 | 8,872 | 450 | 95.2 | Bone-level pose work: every layer must write in LateUpdate after the Animator, and never rebase off its own output. |
| core | 8,736 | 8,030 | 7,414 | 706 | 91.9 | The game loop, world hooks and startup sequence: the integration hub everything else plugs into. |
| flight | 4,632 | 4,489 | 4,489 | 143 | 96.9 | Hand-integrated aerodynamics. Ports almost line-for-line; the handedness mirror is the only real hazard. |
| ui | 1,983 | 1,845 | 1,525 | 138 | 93.0 | UI Toolkit with USS carrying the existing CSS theme. Georgian strings throughout. |

Units: 292 C# units claim JS coverage; 243 carry a probe-verified parity proof, 49 do not (most are Unity-only infrastructure that ports no JS line: scene assembly, loaders, shaders, UI plumbing — see port_units.json `evidence`).

## 3. Feature checklist (docs/QUALITY_BAR.md)

- [x] real storey counts
- [x] real roof shapes
- [x] real wall materials
- [x] OSM 3D building parts
- [x] modelled geometry, not texture tricks
- [x] real photo facades
- [x] wall materials from real Tbilisi photo cuts
- [x] 3D window frames
- [x] shopfronts
- [x] damage

Every checked row names the probe line that proved it REACHED in Assets/Scenes/Game.unity (the only booting scene), not just computed.

## 4. Still open (docs/open_items.json)

| id | area | owner | what |
|---|---|---|---|
| persistence | Core | user (decision) | Building-damage server persistence - plan written, deliberately NOT coded: three key spaces and an unresolved hole-coordinate frame would corrupt shared data |
| cinema | Cinema | user (deferred) | Cinema reel subsystem (10 files, 2,221 lines, probe-verified) deliberately not wired |
| asset-spans | Assets | user (open) | hsp75.glb ships at 47% of its published span and its yaw=pi disagrees with every measurement; cargobro at 76% - asset questions, not port questions |
| latent-pi | Rendering | user (decision) | three.js BRDF_Lambert divides diffuse by pi; URP LightingLambert does not - so the verbatim sun 2.7 / hemisphere 2.0 are ~3.14x hotter in Unity. Nothing clips now, but frames read a touch hot. Changing it is a DECISION ( |
| tornado-cockpit | Spec | user (decision) | tornado.fbx has no mesh named canopy/glass/cockpit/seat/window, so findCockpit has nothing to pick (JS too); its old 'sane' was read across a 37.7-degree-skewed airframe. Parity fix is a one-line cockpit:{z~-5} override  |
| kits-js-side | Assets | user (decision) | The shipped Three.js build has the same damaged kits (public/models/{infra_kit,embankment_kit,facade_kit}.glb, 1 node each): no quays/balustrades/kerbs/bus stops/lamp columns and 21,444 shopfront boxes in the ORIGINAL. F |
| preview-body-material-parity | UI | user (deferred) | The hero preview renders but the body is a flat-shaded grey-blue faceted blob with one facet blown white (ui_character.png 14:26) - the JS personage screen shows the real coloured bodies (src/characters.js RACES[].bodies |
| resolution-drop-unsubscribed | Perf | user (decision) | GameLoop.OnResolutionDrop (game.js:2639-2652 adaptive resolution) is still unsubscribed (baselined); now that ApplyQuality is the one writer of renderScale, wiring it is one line - but it would move render scale mid-sess |
| perf-retina-gap | Perf | unassigned (perf agent stood down) (open) | At the JS bar's own pixel count (docs/QUALITY_BAR.md:97's 94 fps was measured at viewport 1400x900 x deviceScaleFactor 2 = 2800x1800 = 5.04 Mpx) the port renders ~56 fps mean (17.7 ms) vs 94 fps (10.6 ms) - 1.67x slower, |
| ultra-renderscale-mapping | Rendering | user (decision) | UI/Settings/SettingsState.cs:67 maps the 'ultra' preset to URP renderScale 2.0, but settings.js:10 ultra = pixelRatio min(2, devicePixelRatio) on a CSS-sized canvas = exactly the display's physical pixels; Unity's Screen |
| harness-text-input | Probes | unassigned (open) | InputSystem.QueueTextEvent characters do not reach the focused TextField in the batch harness (click, focus and button clicks work): arm1_typingLands=false. Probably the InputForUI text leg (InputEventPartialProvider) ra |
| saved-collapse-restore-fx | Combat | unassigned (open) | citymap.js:2943-2946 - the SAVED-damage restore path re-stamps the scorch + smolder for a building already collapsed in a previous session; no C# counterpart to that restore exists to hang it on (damage persistence is it |
| kit-impostor-ab-flaky | Probes | unassigned (open) | RenderingItemsSuite's Old Town impostor A/B (forced LOD, baked vs greybox) is flaky: 2 of 7 runs show the baked impostor statistically indistinguishable from full detail (variance 7424.8 vs 7417.9, box 8312.2, 85,646 px  |
| memory-budget-3gb | Perf | agent: memory (open) | USER TARGET: the game must stay at 2-3 GB. First pass (memory agent 2026-10-31): native allocated 2.16 -> 0.86 GB, native RESERVED (the peak the OS footprint is made of; Unity never returns reserved pages) 3.11 -> 2.11 G |
| river-bank-blue-wedge | City | unassigned (open) | Residual 8,044 px (0.56%) of the top-down river frame still reads pale blue: a wedge of carved bed between the water and the quay face where the carve mask ends (bottom-right of rb_grazing.png; visible at one bank in rb_ |
| sign-colormap-missing | Assets | user (decision) | Road signs render untextured: Props/signs/Textures/colormap.png is missing from the repo and from git history (StreetFurnitureRenderer.cs:71 documents it); the live JS build 404s the same file - an asset question, not a  |
| hands-inside-body-clips | Character | agent: hand-clearance (fable fork) (open) | USER 2026-11-01 22:50 (22:46 build, apple): 'u need check position of hands, sometimes they are inside body and i dont see them when i am moving… check in all'. The Humanoid clips were authored for a slim human; the frui |

## 5. What was found and fixed on 2026-11-01 (the Sonnet wave)

- **bldrich-chunk-duplicate** — CityRenderer.ReleaseRichChunkMeshes: the damage vertex ranges address the BAKED chunk_<n>.asset meshes (CombatBuildingChunk.EnsureInit reads its own MeshFilter.sharedMesh; BuildPlayableScene writes them with AssetDatabase.CreateAsset), so CityRenderer's runtim
- **sign-highway-scale** — ResolveSignModel returns mf.transform.localToWorldMatrix (the kits' root-relative idiom is IDENTITY on a single-node FBX - a silent no-op); the upright TransformBounds feeds SignScale; TRS * model at placement; ApplyModelTransform toggle
- **relief-floating-off-kit** — no change
- **shop-sign-text-mirrored** — u -> 1-u in ShopfrontRenderer.QuadMesh (+ MetroRenderer, MallRenderer, LandmarkSignage quads); the V flip at upload in ShopSignTextures.Paint and MallRenderer.MallSignTexture; ShopSignTextureProbe analyses the paint buffer so its 14 SHOPSIGN lines stay byte-id
- **qbar-parts-reached** — No code change; new proof probe unity/Assets/Editor/Probes/PartsProbe.cs
- **qbar-facades-verified** — No behaviour change; probe lines added to PhotoFacadeProbe.cs (PHOTO assigned/duplicates, PHOTO_RUNTIME) and MaterialProbe.cs (WALLCUT buckets/bound)
- **photo-render-count-drift** — The JS gives 273, not 319 (the probe's constant was never computed). A faithful Node reimplementation of citymap.js's chain (pbOf exclusive draw, districts, parts expansion, the 5 kit fits, mallBids skip) over the real data reproduced the live C# 276 exactly W
- **qbar-shopfronts-verified** — Debug-only fields PoiRec.WallBi + SignSpot.BuildingId/WallDistanceM in ShopfrontBuilder.cs; SIGNS textured/nullTexture + named-shop lines in ShopfrontRenderProbe.cs; QUALITY_BAR row checked
- **qbar-damage-reached** — No production change; new proof probe unity/Assets/Editor/Probes/DamageReachProbe.cs
- **hero-model-turn-missing** — CharacterRig.cs:140 Model.localRotation = Euler(0,180,0) (the JS turn); FirstPlayModeRun now asserts walksTheWayItFaces (face·move > 0.7) and walksTheWayTheToesPoint (ball_l-foot_l · move > 0.5); new CharFacingProbe prints TOES/FACING per body. The lineup prov
- **foot-lateral-handedness** — GameLoop.ReadFootMoveInput: A/D and Q/E enter with the opposite sign; the mouse X line uses += (JS -=). FirstPlayModeRun asserts aTurnsScreenLeft (model.forward moves toward Cross(forward, up)) and dTurnsScreenRight.
- **keymap-audit** — GameLoop.cs ~2636: Alt branch gated on _foot.Armed like the RMB branch; docs/KEYMAP.md (table: key, mode, JS action, Unity handler, tested-by, status); new Tests/PlayMode/KeysRun.cs (FlyingKeys/OnFootKeys/MenuKeys)
- **crash-fx-light-pool-unwired** — CombatWorldAdapter.cs:938-939 assigns both hooks to GameLoop.TakeFxLight/FreeFxLight; Probes/CrashFxLightProbe regexes the real file for the assignment and drives TakeFxLight 9x asserting 3 distinct lights (round-robin); game.js:606-613 centreAim filed as an e
- **landmarks-rest-walk** — Citations added in LandmarkPlacer.cs / LandmarkRenderer.cs; LandmarksRestProbe + tools/_probe_landmarksrest.mjs oracle; docs/port_units/landmarks_rest.json merged; 8 exclusions filed
- **misc-rest-walk** — PlanePhysics.cs sparks (JsMath.Round count); Weapons.ChestForward + HeldWeapon reload pose; WarehouseFanSwitch.HandleBKey in Update; stale NOT PORTED comments fixed; docs/port_units/misc_rest.json merged (infra 98→0 free, planedamage 96→1, weapons 3→0, crane 2
- **parkour-clips-first-wiring** — Character/ParkourClipLibrary.cs, ParkourClipPlayer.cs; FootController StartRoll/StartSlide/ToggleCrouch + climb-owns-frame; FootClimb MantleClipActive/HangClipActive; Tests/PlayMode/ParkourRun.cs (headless-safe: no WaitForEndOfFrame, real-time bounded loops)
- **parkour-moves-full-set** — Character/ParkourMoves.cs (+Probe 159 face ladder/top/depth, WallAhead 200), ParkourClipPlayer.cs, FootController.cs (PlayMove 304, Launch 317, StartWallPlant 326, StartKnockdown 342, TickOwnedFrame 354, X 611, Z 646, landings 899, wall 1175, jump 1186, climb 
- **mantle-keys-index** — FootPoses.cs rows completed
- **landing-step-window-skip** — FootController landings 899 / midair 1610
- **range-registry-stale-pivots** — Combat/RangeBootstrap.cs registry reset; chain19 gates on the <test-run> element's own result
- **cover-shoot-purchased-path** — Assets/ThirdParty/CoverShoot + Resources/ThirdParty/CoverShoot; Character/CoverShootHero.cs, CoverShootInput.cs (pack reads our Input System), CoverShootPickups.cs; Combat/CoverShootBridge.cs; UI/Hud/CoverShootAmmoView.cs; GameLoop.StageOnFootPurchased + UsePu

## 6. Rules every agent must apply (learned the hard way)

- **Reached, not computed.** A probe that calls a pure function proves the maths. Pair it with one assertion that the gate is open on the shipped asset / scene / delegate (`grep -rn "new X"` finding only probes is a red flag).
- **Handedness.** The Unity world is the JS world mirrored in X (GeoFrame) and the FBX bridge keeps X, so the on-foot yaw maths is consistently mirrored; only the player's lateral INPUT must not be (GameLoop.ReadFootMoveInput, mouse X). The model child carries the JS's 180° turn (CharacterRig.cs:140). Flight uses Unity transforms and degrees (Rad2Deg).
- **Bridged assets have no textures.** Every GLB→FBX body/plane/tree arrives extensionless/WebP: decode from the GLB and bind by material name (plane_liveries.py, tree_textures.py, CharacterTexturePostprocessor).
- **Doubles.** JS has no float32; narrow only where a Vector3 demands it, never before a comparison (parks, river ear-clip).
- **One editor at a time** on this 16 GB Mac: the mkdir mutex + "no editor alive" loop, in a BACKGROUND script with log polling (a tool call silent for 600 s kills the agent). Compile-check before leaving a file; grep every caller when a signature changes.
- **Georgian UI only.** No English strings on screen; the death screen is never "WASTED".

## 7. Next

1. Parkour Animation Set (bought, downloaded 13:37) onto the hero: Z slide/vault, X roll, C crouch/let go, climb/mantle/ledge/landings as real Humanoid clips (agent in flight).
2. Key audit: every JS binding vs Unity with a KeysRun play-mode proof (agent in flight); user-flow scenarios (airport chip, climb out, take any plane, shoot, crash, UI) (agent in flight).
3. perf-retina-gap on a quiet machine; memory census of the INSTALLED build (target 2–3 GB).
4. Decisions owned by the user: damage persistence, cinema, hero body colours, asset spans.
