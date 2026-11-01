# The quality bar

**User requirement, 2026-10-26: *"i need minimum same quality like we had before."***

The Unity build must match the Three.js build feature for feature, or beat it.
Never worse. This file turns that into a checklist, so parity is testable rather
than a matter of opinion. A system is not "ported" until its boxes are ticked.

The grey extruded boxes baked into `Assets/Scenes/Tbilisi.unity` on 2026-10-26 are
a **verification scaffold**, not a deliverable. They proved the footprints, heights
and orientation are correct. They are below the bar and must be replaced.

## Two different things: DECIDED vs RENDERED

A box being ticked below means the *decision* is ported — which kit a footprint
gets, which wall material, which roof shape, where the balconies go. It does NOT
mean it is on screen. Those are separate layers, and right now the second one does
not exist:

- **Decided** (data computed, probe-verified against the JS): kit choice for
  167,168 of 193,146 buildings, 24 wall buckets, 2,003 stepped-massing footprints,
  11,251 window/5,335 balcony/1,461 gallery/1,677 cornice placements.
- **RENDERED, as of 2026-10-26** — `CityMaterials.cs` + `CityRenderer.cs` +
  `BuildCityRender.cs`, verified by `CityRenderProbe`:
  - **169,150 buildings resolve to a real kit mesh**, 0 fallbacks, 0 missing variants
  - **282 materials created, 0 missing textures**
  - **1,557 draw calls [STALE 2026-10-30: the live figure is 329 draw calls per frame at spawn (CityDrawProbe), from 781,149 batches of 1.86 instances; see the batch-explosion section]** for 1,454,707 submesh instances across 245 batches, via
    `Graphics.RenderMeshInstanced` — no GameObject per building
  - 2,485 part volumes replacing flat boxes; 500,019 windows [STALE 2026-10-30: computed, NOT rendered -- zero production consumers of .Relief; see the facade-relief section], 160,000 balconies,
    41,785 cornices, 30,846 glass bands placed
- **RESOLVED 2026-11-01 — 273 of 609 is CORRECT, and matches the JS. The earlier
  "319" was ALSO never measured and was itself wrong** (docs/open_items.json
  `photo-render-count-drift`: the live bake rendered 276 against a 319 assertion,
  and "nobody knows which number the JS gives" — not a port bug, but the constant
  itself had never been computed). In src/citymap.js each of the five kit-claim
  branches (panel 4661, house 4687, out 4701, apt ~4715, shed 4731) ends in its own
  `continue;`, and `const pb = pbOf.get(bi) || null;` is declared ONCE at line 4746
  — *after* all five. So a kit-claimed footprint never reaches the photo lookup.
  There is even a developer comment at the shed test noting that someone tried to
  check `pb` inside a claim branch and it threw a temporal-dead-zone error, so it
  was left unconsulted. **A kit claim beats a photo in the shipped Three.js game**,
  probably by accident, but really. On top of that, citymap.js:4591 skips a
  footprint ENTIRELY (never reaches TryClaim or the photo check) when a real
  Blender mall model stands there (`this._mallBids?.has(bi)`) — BuildingBuilder.cs
  had no equivalent skip, so up to 42 mall footprints were leaking into the kit-claim
  and photo counts. Fixed (BuildingBuilder.cs: `LoadMallBids`/`NMallSkipped`). The
  true number was found by reimplementing the whole decision chain (pbOf exclusive
  assignment, DISTRICTS/cottage, parts expansion, the mallBids skip, all five kit
  fits against the live kit_meta.json) in Node over the real production data; before
  the mallBids fix it gave 276, matching the then-live BLDRICH log exactly — proof
  the rest of the chain was already faithful. With the fix on both sides: 273.
  CityRenderProbe's assertion is now 273. The earlier "609" figure was an assumption
  written into an agent brief, not a measured target.
  If showing more real photographs is wanted, that is an IMPROVEMENT OVER PARITY
  and should be decided deliberately — not a port fix.
- Still missing: no normal/roughness maps exist on disk for the 31 wall/roof tiles
  (only albedo was ever baked), the 4 painted styles and the loggia buckets fall back
  to a flat colour because URP's stock Lit does not sample vertex colour, and there
  is no credits UI to carry the CC BY/BY-SA attribution — it is only logged.

## Buildings — what the Three.js build actually does

- [x] 193,146 real OSM footprints, extruded over the WHOLE footprint (not the centroid)
- [x] real heights (GHSL ANBH raster)
- [x] **real storey counts** — `levels.json`, 17,369 buildings get a real height (DECIDED)
- [x] **real roof shapes** — 13 kinds from OSM `roof:shape`; 5,603 pitched (DECIDED;
      roof GEOMETRY is still a single-apex fan, not a real gable ridge line)
- [x] **real wall materials** — 24 buckets resolved across the 25,981 extruded
      buildings, OSM `building:material` beating the random family pick (DECIDED;
      no URP Material is bound to the submeshes yet)
- [x] **OSM 3D building parts** — `parts.json`, real massing replacing flat boxes on
      482 buildings (the data has grown from 479; a hotel tower over a podium is ONE
      outline and must not extrude as one slab). VERIFIED REACHED 2026-11-01 (PartsProbe):
      `PARTS buildingsWithParts=482 partsMeshesBuilt=2485 slabSuppressed=482`,
      `PARTS_RUNTIME caller=CityRenderer.EnsureBuilt scene=Game.unity reached=true`,
      tower bi=34386 parts=65 maxHeightM=74.40 identical to a Node re-implementation
      of citymap.js:4538-4584 over the same JSON
- [x] **modelled geometry, not texture tricks** — 167,168 of 193,146 (86.5%) claim a
      real kit: house 103,946 · apt 36,751 · out 15,642 · shed 12,290 · panel 539.
      All 5 kit FBX measured, none missing (DECIDED; **not instanced yet**)
- [x] **real photo facades** — `textures/pb/`, exclusively assigned (one photo dresses
      exactly ONE building; matching every building within 28 m cloned facades onto a
      dozen neighbours, which is the most visible kind of repetition in a dense block).
      VERIFIED 2026-11-01 (PhotoFacadeProbe): `PHOTO assigned=609 duplicates=0`,
      `PHOTO_RUNTIME caller=BuildPlayableScene.Run->BuildingBuilder.BuildRich bakesInto=Assets/Scenes/Game.unity`
      (the bake IS the shipped path; CityRenderer.EnsureBuilt frees its chunks).
      CLOSED 2026-11-01: 273 photo buildings render, matching a faithful Node
      reimplementation of the JS decision chain over the real data (including the
      mallBids skip BuildingBuilder.cs was missing) — see docs/open_items.json
      `photo-render-count-drift`
- [x] **wall materials from real Tbilisi photo cuts** for buildings that lose the
      exclusive photo draw. VERIFIED 2026-11-01 (MaterialProbe): `WALLCUT buckets=24 bound=24`,
      `MATP id=assert_tileMapsComplete albedo=31 normal=31 mgs=31 result=PASS` — the
      "no URP Material is bound to the submeshes yet" note above was stale
- [x] **3D window frames** — 11,251 decided, with windowless plaster for the rest
- [x] **shopfronts** — 14,728 named POIs, wall-snapped, with SignPool real-name signs.
      VERIFIED 2026-11-01 (ShopfrontRenderProbe + CitySignsProbe):
      `SHOPFRONTS pois=14728 wallSnapped=14470 modules=21444 awnings=2741 canopies=321
      crosses=1232 signs=14490 mcdonaldsPads=1` — matches a Node reimplementation of
      src/shopfronts.js:99-137's own flatten/grid/brand-override logic over the shared
      `pois2.json`/`named.json`/`brands.json` (`TOTAL_POIS=14728`, exact);
      `SFR id=kit winShopPresent=true acPresent=true` (`fallbackBox=False` — the real
      "win_shop" kit submesh renders, not a procedural placeholder);
      `SIGNS textured=58 nullTexture=0` (every currently-enabled sign slot has a real
      painted texture, not a grey quad); one named shop, independently cross-checked:
      `SFR id=named_shop cat=electronics name="სელფი" buildingId=41103
      wallDistanceM=18.710`, identical building id/distance/category (brand-book
      override bank→electronics included) to the Node oracle run over the same files.
      `SHOPFRONTS_RUNTIME caller=BuildPlayableScene.Run (AddComponent<ShopfrontRenderer>,
      BuildPlayableScene.cs:163) scene=Game.unity` — guid
      `3a298630047024ad8b855ea66235c9b6` appears in the saved scene and
      `CSCHECK shopfrontRendererInScene=PASS` / `SSPOOL active=192 slots=192` after a
      real `EditorSceneManager.OpenScene` + `PlayableSceneBoot.Run()` (CitySignsProbe,
      `CITYSIGNS RESULT=PASS`). GAP, pre-existing and unrelated to this row: the
      police-shield lightbox's "112"/"პოლიცია" text is not rasterised (shape/colour
      correct); shop-glass collapse tagging has no Unity consumer yet, consistent with
      the rest of the facade-prop system.
- [x] **damage** — per-building HP, 10 crumble stages via chunk vertex ranges, rocket
      blast holes carved into facades (corner hits take the corner), breaking glass.
      VERIFIED REACHED 2026-11-01 (DamageReachProbe, driven through the real
      MachineGun.Update / RocketLauncher.Update in Game.unity, first hit on a pristine chunk):
      `DMG weapon=gun hpBefore=358.00 hpAfter=316.00 stageBefore=0 stageAfter=1 vertsMoved=21 panesBroken=1`,
      `DMG weapon=rocket hpBefore=469.00 hpAfter=209.00 stageAfter=4 holeCarved=true cornerTaken=true panesBroken=28`,
      `DAMAGEREACHPROBE RESULT=PASS`. Persistence is a separate user decision.

## Everything else, in one line each

- **Terrain** — half-res 60 m grid, triangle sampling, real water colour, PBR roads
  with real OSM widths/lanes/layers, tunnel cuttings carved into the terrain
- **Roads** — real widths, lanes and layers; granite kerbs; structural spans;
  Rustaveli included (OSM tags it `highway=construction` — the pipeline fix)
- **River** — Mtkvari with real quay/balustrade kit, ashlar, dual-normal water
- **Trees** — real chestnut, instanced, swaying in a vertex shader (height-squared,
  AFTER instancing), one shared breeze
- **Parks** — 563 real parks with true path networks + the Blender furniture kit
- **Planes** — 36 at REAL span, cockpit interiors fitting a 1.8 m pilot, hoods that
  open, propellers with a blur disc driven by RPM, real CC0 engine audio as a
  start/idle/run/shutdown state machine
- **Plane damage** — holes in the airframe's own frame, 4-variant atlas, a fragment
  shader that DISCARDS pixels inside each tear so you see through them, tears that
  grow and saw a wing off along a connected run
- **Characters** — 4 playable bodies at 1.8 m, real run cycles, AC-style climbing,
  GTA cover, per-joint arm springs, footsteps read off the foot bones and classified
  per surface
- **Guns** — animated Beretta/AKM with real clips, belt holster seated by measurement,
  real CC0 recordings per role, ballistics with drag/drop/penetration/ricochet
- **UI** — the cyberpunk theme, Georgian only, HUD thin and low-opacity
- **Perf** — the JS build reached 94 fps at real retina. Unity must not be slower.

## How this gets checked

Every box needs a probe, not a screenshot. The JS build was verified by ~400
`tools/_probe_*.mjs` probes reading real numbers out of the running game; carry that
across (see `.claude/skills/unity/SKILL.md`). For buildings specifically:
`tools/_probe_realholes.mjs` proved holes by reading rendered pixels before and after
on all 35 planes — that is the standard of proof to match.

## BLOCKER: no Georgian font asset exists — every Georgian glyph will be a tofu box

Confirmed 2026-10-27 by two independent agents and checked directly:

- `find unity/Assets -iname "*.ttf" -o -iname "*.otf" -o -iname "*.woff*"` → **nothing**.
- The web build ships no font file either. `src/style.css:25` is
  `font-family: "Noto Sans Georgian", "Jost", -apple-system, ...` — the BROWSER
  supplies the glyphs from the OS. That is why the Three.js version looks right.
- Unity has no equivalent fallback: a UI Toolkit / TMP font asset must physically
  contain the Mkhedruli range or the text renders as empty boxes.
- `Core/CyberTheme.cs` already names `FontGeorgian` and flags this project-wide.
- This machine has no Georgian-capable font installed either, so there is nothing
  local to point at.

EVERY string in this game is Georgian by an explicit user requirement, with no
English fallback — so this single missing asset makes the entire UI unreadable,
regardless of how correctly the views are ported. `UI/Character/CharacterView.cs`,
`HudView`, `SettingsView`, `CreditsView` and the zone-name slam are all affected.

It is NOT a code or markup problem. The remaining step is to import one
Mkhedruli-range font and point each USS's `-unity-font-definition` at it. Noto
Sans Georgian is SIL OFL 1.1, so it is redistributable inside the game — but
WHICH font ships is the user's call (licensing, branding), and fetching it is an
outward action, so it is being surfaced rather than done unilaterally.

## TRACKED: building footprints are stored float32 from JSON-parse time

Found by the 2026-10-27 precision sweep, PROVEN against the real
`public/tbilisi/city.json.gz`, deliberately NOT fixed yet.

`Data/CityData.cs:150` reads every footprint coordinate with
`Convert.ToSingle(jr.Value)`, so `Footprint.Flat` is `float[]`. Measured on all
**2,132,928 real coordinates**:

    max |coord|                                   264,514 m  (the shared frame spans six CITY BBOXES in the data (mapmeta.json: tbilisi/batumi/kutaisi/gori/rustavi/kazbegi) and |x| reaches ~264 km, even though ONLY TBILISI IS PLAYABLE today — the others were locked cards and those screens are deleted (src/main.js:634), but the user has said they are coming later, so the far-field data stays and must not be bbox-filtered away)
    worst float32 round-trip error on a real coord    12.5 mm
    the position audit's established accuracy bar     20 mm

So **storage alone is inside the bar** — 12.5 mm < 2 cm. What is outside parity
is what the ARITHMETIC then does with it. `City/FacadeRelief.cs:129-184`'s
window-placement perimeter walk (ported from `citymap.js:4893-4907,5064-5080`,
which is double throughout) differs city-wide:

    float64   3,291,565 window columns   849,135 wall segments (len>=5)
    float32   3,291,406                  849,117
    delta           -159  (0.005%)             -18

**Why it was not fixed on the spot:** widening only the arithmetic recovers
nothing — the coordinates are already rounded at parse time, which the sweep
proved directly by reproducing the float32 numbers exactly with double math on
pre-rounded input. This is the same lesson as the river: precision must be
carried from the SOURCE. The real fix is `Footprint.Flat` -> `double[]`, which
is read by **14 files** across City/, Core/ and Combat/. Memory cost is
negligible (~8.5 MB for 2.1M coordinates).

**Recommendation: do it, but as one scheduled mechanical pass after the current
wave lands** — not mid-flight while five agents are editing City/. It is a
change of type at one field plus a narrowing cast at each use site, and the risk
is entirely in doing it piecemeal while others hold the same files.

Do not quietly drop this on the grounds that 0.005% is invisible. It is a real,
measured divergence from the reference implementation, and the bar for this port
is parity or better.

## NOTE: the character-preview drag math exists twice

`UI/Character/CharacterView.cs`'s `Bind()` (around L190-207) re-implements the
same drag-delta arithmetic as `Core/CharacterPreviewRig.cs`, then calls
`_preview.Drag(dx)`. This is NOT a double-billed claim — CharacterView's own
citation list correctly omits `main.js:323-350`, which is CharacterPreviewRig's
— so the ledger is right. It is plain maintenance debt: two copies of one
formula, and the next person to tune preview dragging will change one of them.

Found by the main.js citation audit, 2026-10-27. Low priority, but write it
down rather than rediscover it.

## [CLOSED 2026-10-27] #2 was: the city's scenery had no renderer

ALL of it now renders, each verified through the real `OnEnable`/`EnsureBuilt`
component path and not just a static call:

    shopfronts  21,444 instanced modules, 2,741 awnings, 1,232 lightboxes, 192-slot SignPool
    vegetation  496,835 instances (trees/groves/rocks); 31 draw calls near, 0 at 80 km
    parks       116 mesh objects / 14.19M verts + 583 instanced batches; 3 ferris wheels, 4 zoo elephants
    metro       106 placements across 23 stations, all 23 yaws checked off the real Transform
    malls       42 of 42
    streetlights 1,137 lamps, 808 signs, 285 batches / 3,082 instances, 0 real Light components
    plane hall  35 airframes under one visibility switch

`CityLoader.UnportedStages` is now EMPTY and `not_built` is 0 lines (was 246).
Still honestly absent, reported not faked: park benches, flowerbed rims,
carousel rides, zoo giraffes (no asset on disk), and sign/fascia glyph
rasterisation.

### The original finding, kept for the lesson

Established 2026-10-27 by direct grep, not inference. This is the one thing the
percentage does not tell you, and it changes what "89.7% ported" means in
practice.

`City/CityLoader.cs`'s scenery pass BUILDS the data and then **nothing consumes
it**. Counting real consumers in `unity/Assets/Scripts` (excluding the field
declaration, its own assignment and comments):

    ctx.Parks        0 consumers      563 real parks, full path networks
    ctx.Malls        0               42 Blender malls on OSM footprints
    ctx.Metro        0               23 stations with real entrance pavilions
    ctx.Grass        0
    ctx.Worship      0
    ctx.Shopfronts   0 (the 2 hits are a gap comment)   13k named POIs + signs

And six stages have no builder at all, honestly recorded in
`CityLoader.UnportedStages`: **trees, rocks, streetlights, road signs, plane
hall, groves**.

**Why the ledger does not show this.** The percentage measures JS lines
REPLACED, and the placement/geometry math for parks, malls, metro and
shopfronts IS ported and probe-verified — correctly counted. What is missing is
the layer that instantiates it: chunk-instancing onto kit meshes, turning
awnings/canopies into instanced batches, and constructing + ticking `SignPool`.
`SignPool.cs` itself is a correct port whose only instantiation sites in the
whole project are two Editor probes (`grep -rn "new SignPool"` finds nothing in
`Scripts/`). For shopfronts alone the missing pass is `src/shopfronts.js:240-370`
(131 lines).

**So a player today would see** terrain, river, roads, bridges, tunnels,
buildings, landmarks and the airport — and no trees, no shop signs, no park
furniture, no street lighting. That is a grey-box city, which
docs/QUALITY_BAR.md's own first rule rejects.

**This is the top priority for visible parity**, ahead of any remaining
line-count work. It is also the clearest case in the project of why a line
percentage and a quality bar are different instruments: both numbers can be
honest while the game looks wrong.

## [CLOSED 2026-10-27] #1 was: no body is a Humanoid avatar

FIXED AND PROVEN. banana/eggplant/toad are now `animationType: 3` with 22/22/19
human bones mapped; the elephant is correctly still Generic (it is `Procedural`,
Idle only). `Editor/Probes/AvatarGateProbe.cs` reads the real shipped assets:

    GATE id=banana    IsHumanoid=true  AvatarValid=true  FootPosesValid=true  ShoulderPocketL/R=true
    GATE id=eggplant  IsHumanoid=true  AvatarValid=true  FootPosesValid=true  ShoulderPocketL/R=true
    GATE id=toad      IsHumanoid=true  AvatarValid=true  FootPosesValid=true  ShoulderPocketL/R=true
    GATE id=elephant  IsHumanoid=false ...                Procedural=true

So the arm poses, belt/holster seating, seat fit, shoulder pocket, torso twist
and the climb hand placement are LIVE for the first time. Re-ran the character
probes afterwards (FootFinal/FootBelt/PlaneChar/CharCombat) — all run clean with
the gate open, so nothing was depending on it being shut.

KEEP AvatarGateProbe IN THE SUITE. It is the only thing standing between this
and a silent regression: a re-import or an FBX replacement resets
`animationType` and every pose system goes quietly inert again with no error.

### The original finding, kept for the lesson

Found 2026-10-27 while verifying an unrelated port. Verified directly:

    grep -rl "animationType: 3" unity/Assets            ->  0 files
    animationType values across all 176 model .meta     ->  176x "animationType: 2" (Generic)
    banana/eggplant/elephant/toad .fbx.meta boneName    ->  0 each (empty human mapping)

`CharacterRig.cs:144` sets `IsHumanoid = Anim.avatar.isHuman`, which is therefore
**false for every body in the project**. That flag is an early-return gate on:

    CharacterRig.cs:336   torso twist          CharacterRig.cs:412, :465, :607
    FootPoses.cs:156      the whole pose rig returns Valid = false
    ShoulderPocket.cs:71  returns empty
    FootBoarding.cs:974, :1028, :1058          seat / boarding poses
    RigSeatExtras.cs:138  hips lookup
    FootWeapons.cs:550    arm bones

So the arm poses, the belt and holster seating, the seat fit, the shoulder
pocket, the torso twist and the climb's hand placement all compile, are all
probe-verified, and **none of them do anything at runtime**. `HandsOnEdge` and
`SetWingGrip` are no-ops for every body today.

**This is an asset-import setting, not a code bug, and the intent was always
Humanoid** — `.claude/skills/unity/SKILL.md:285` says the three humanoid bodies
go through FBX via `tools/glb_to_fbx.py` precisely "so Unity's Avatar
auto-mapper works". The mapping was simply never applied. One import fix
(`ModelImporter.animationType = Human` + a bone mapping, for banana/eggplant/
toad; the elephant is deliberately `Procedural`) switches a large amount of
already-written, already-verified code from dead to live.

**Why ~50 probes missed it.** They construct a rig and exercise the pure
functions, so they prove the MATHS is right; none of them asserts that the gate
upstream is open. Eight probes mention `IsHumanoid` and none fails on it. That
is the sharpest lesson of this port so far: **a probe proves a calculation, not
that the calculation is reached.** Pair every gated system with one assertion
that its gate is actually open on the real shipped asset.

Ranks above the scenery-renderer gap below it: that gap is work not yet done,
this is finished work that silently does nothing.

## NON-FINDING, do not chase: facade_kit.fbx having only "ac" is CORRECT

A render-layer agent reported, correctly, that `unity/Assets/Art/Props/facade_kit.fbx`
contains exactly one mesh named `ac` — no `win_shop`, `window`, `balcony`,
`cornice`, `grille`, `dish`, `drainpipe`, `entrance`, `parapet` or `roof_vent` —
and concluded `FacadeKitBuilder`/`FacadeProps` are "silently running on fallback
boxes". The first half is true. The conclusion is not, and acting on it would
waste a Blender authoring task.

Checked directly:

    public/models/facade_kit.glb  (the THREE.js source asset)  meshes: ['ac']
    unity/Assets/Art/Props/facade_kit.fbx                      tokens: ['ac']

The JS-side asset has the same single mesh, so the FBX conversion lost nothing.
And `src/citymap.js:6170-6182` shows the original's intent explicitly: every
variant is fetched via `geoOf(v, () => new THREE.BoxGeometry(1.5, 1.6, 0.1))`
— a PROCEDURAL BOX FALLBACK. The shipped Three.js game draws boxes for
`win_shop`, `window`, `cornice` and `drainpipe` as well. Only `ac` was ever
modelled.

So a fallback box in C# is **parity, not a regression**. Authoring the ten
missing meshes would be a quality IMPROVEMENT over the original — legitimate to
want, but it is new art work, not a port defect, and it must not be filed as one.

**Pattern worth noting:** this is the third confident "real finding" from an
agent today that did not survive a check — after one reporting
`UI/Character/CharacterView.cs` had zero manifest mentions (it had a 210-line
entry) and one reporting `addStorefronts()` missing from the exclusions (already
there). All three read code rather than running the one command that would have
settled it. Verify a finding against the JS source before escalating it,
especially when the fix would be expensive.

## OPEN: the on-foot HUD chips and the return-to-plane prompt are unwired

Checked directly 2026-10-27, correcting a looser earlier framing. The on-foot
HUD is NOT "hidden by design" as a whole — two different things were conflated:

- `g.combatHud.setVisible(false)` (src/foot.js:3783) hides only the COMBAT HUD —
  pitch ladder, tapes, airdata, gun reticle. Those read the PLANE's attitude, so
  hiding them on foot is correct and `HudView.SetVisible` already does it.
- The chips are separate and ARE live on foot in the original
  (src/foot.js:6242-6252): `g.ui.speed` = ground speed in km/h, `g.ui.alt` = 0
  ("you're the altitude now"), and the compass dial rotates to the derived
  heading. Those have no Unity sink being fed yet.

FIXED while here: `MapUi.Update`'s on-foot heading. The JS derives
`hRad = atan2(-f.x, f.z)` from the facing vector `(-sin y, 0, -cos y)`, which is
**pi - yaw**; GameLoop was passing `_foot.Yaw` RAW. They agree only at
yaw = pi/2 and are a full 180 degrees apart at yaw = 0, so the on-foot minimap
arrow pointed backwards. The flying branch beside it was always right because
`transform.eulerAngles.y` already IS a heading — only the on-foot branch mixed
the two conventions. Now routed through the ported, probe-covered
`FootController.CompassHeadingDeg`.

STILL OPEN, and deliberately not half-wired:

1. The speed/alt/compass chips need a UI sink. `HudData` has no Speed/Alt field;
   the chips are a separate element from the combat-HUD canvas.
2. `FootBoarding.NearOwnPlaneHint()` is ported (the Georgian prompt
   "Enter — თვითმფრინავში დაბრუნება ✈") but **never fires, because nothing in
   `unity/Assets/Scripts` ever constructs a `FootBoarding`** — `grep -rn "new
   FootBoarding"` finds only its nested TYPES referenced from
   `Flight/PlaneCockpitKit.cs` and `PlaneCockpitMeasurement.cs`. Wiring it means
   deciding who owns that instance, which runs straight into the documented
   `FootWeaponFrame` vs `Core/HeldWeapon` two-orchestrator hazard. Left for a
   pass that can reconcile that, rather than constructing a second owner here.

## THE PORT'S SIGNATURE FAILURE: correct systems that nothing constructs

Not a one-off. Measured 2026-10-27 by `grep -rn "new <Class>(" unity/Assets/Scripts`
— the whole project, production code only:

    Fx/RocketTrailFx.cs      0 callers      Fx/SmokeTrailFx.cs    0 callers
    Fx/MuzzleFlashFx.cs      0 callers      Fx/BlastRenderer.cs   0 callers
    Props/WindRider.cs       0 callers  (fixed 2026-10-27 — WarehouseFanSwitch now ticks it)
    City/SignPool.cs         probes only (fixed — ShopfrontRenderer constructs it)
    Character/FootBoarding   0 callers  (deliberately NOT wired: GameLoop already owns boarding)
    Character/FootWeaponFrame 0 callers (deliberately NOT wired: HeldWeapon is the live owner)

Every one of these is correct, several are probe-verified, and none of them ran.
`WindRider` was the clearest case: the entire wind-tunnel ride — slung Mustang,
three aimed fans, ride/blown/fly stages — complete, tested, unreachable.

**Why the ledger cannot see it.** Coverage counts JS lines REPLACED, and these
files do replace their lines. A port can reach 100% with nothing wired. The
percentage is not wrong; it is answering a different question than "does the
game do this".

**The check that finds it, and it costs five seconds:**

    grep -rn "new <Class>(" unity/Assets/Scripts   # production only, not Editor/

Zero hits, or hits only under `Assets/Editor/`, means the system is dead however
good it is. Run it for every leaf system before calling an area done. Same test
in loader form: `CityLoader.RunCritical`/`RunScenery`/`RunAll` have zero runtime
callers, so anything wired only into a `RunSceneryStage` case executes in the
Editor bake and never in the game.

**Still unwired and worth wiring, in rough value order:**
1. `Fx/MuzzleFlashFx` — the pistol/belly-gun flash. `Shooting.cs` already
   computes `FlashVisible`/`FlashScale`/`FlashRotation` for it.
2. `Fx/BulletTrailFx` (new) — needs 2 lines where `BulletSim` is built, in
   `Core/CombatWorldAdapter.cs`.
3. `Fx/RocketTrailFx` / `SmokeTrailFx` — rocket and crash smoke.
4. `Fx/BlastRenderer` — the rocket/crash fireball.

## HALF-FIXED: the 1.7 m wheel offset the JS creates at RUNTIME

`src/planes.js:1563-1564` normalises every airframe when it is built:

    const drop = wbox.min.y + 1.7;   // WHEEL_HEIGHT
    group.position.y -= drop;

So in the JS, every plane's belly ends up at local y = **-1.7** by construction,
which is what makes `WHEEL_HEIGHT = 1.7` a safe constant everywhere downstream.
**The port never replicated that drop**, and the raw FBX pivots do not have it
baked in — measured on 8 real assets: p51/f15/b737/an225/cessna310/tu144 all
`min.y = 0.000`, mig21 `0.061`, spitfire `0.469`.

**FIXED — the plane hall.** `Flight/PlaneHallFill.cs` placed each plane at
`localPosition.y = PlaneHallLayout.Wheel` (1.7), so all 35 parked aircraft were
**floating ~1.7 m above the hangar floor** — the baked scene genuinely read
`localY=1.7000` for every one. Now placed at `-LocalBounds.min.y`, using the box
already measured two blocks earlier, which puts the belly exactly on the floor:
the same net result the JS reaches (belly -1.7 + wrapper +1.7 = 0) without
assuming a normalisation step that does not happen here. Verified:
`PlaneHallFillProbe` now reports 20 planes at `localY=0.0000` and the rest offset
by their own measured belly, `parked=35 failed=0`.

**STILL WRONG — the flying plane's ground contact.** `Flight/PlanePhysics.cs:539`
computes `_ground = wrapper.position.y - Wheel` on the same false assumption, so
touchdown/ground-roll height is off by whatever that airframe's real belly offset
is (up to 1.7 m, and WRONG IN THE OTHER DIRECTION for spitfire/mig21). Not fixed
here because it is cross-cutting: `PlaneGroundRoll.cs`, `PlaneGroundProbe.cs`,
`Core/GameLoop.cs` and `Core/CombatWorldAdapter.cs` all reference `Wheel` too,
and getting it wrong means planes sinking into or hovering over the runway.

**The clean fix shape** (do NOT re-derive the JS's runtime drop — that would
double-apply once the hall is already correct): replace the fixed `Wheel`
constant with the per-plane measurement `-PlaneCockpitMeasurement.BoxOf(w, w).min.y`
wherever it is used for PLACEMENT or GROUND CONTACT. The cockpit baker already
computes exactly that number, so there is nothing new to measure. Needs a probe
that lands a plane and asserts the wheels touch, per airframe — that is the piece
of work, not the edit.

## Dead delegates — a third shape of "finished code that never runs" (2026-10-27)

The orphan audit (`docs/ORPHANS.md`, guard `tools/_test_no_orphans.py`) found a
failure shape neither the port percentage nor a `new X(` grep can see: an event
that is **invoked many times and subscribed zero times**.

- **`GameLoop.OnHint` (GameLoop.cs:220) has 0 subscribers** against 15+ invoke
  sites. Everything it carries is dropped in play: every Georgian contextual
  prompt, the airport messages, the spawn-restore line, the whole
  `PlaneHullDamage` set — and **the death-screen title** (`GameLoop.cs:739`,
  `:401`). So death currently shows nothing, which breaks the standing
  "Georgian death screen, never English WASTED" requirement.
- **`CrashSequence.Hooks.ShowWasted` (CrashSequence.cs:58) is never assigned**,
  invoked at `:457` and `:459` — the second being a safety valve commented
  "never leave the player with no restart". Two independently dead routes to
  the same screen.
- **`OnHitToast`** (`Guns.cs`/`Rockets.cs`/`ImpactFx.cs`) and
  **`PlaneRaycastWorld.ShowBuildingToastHook`** — invoked, never subscribed.
  The real-address hit toast does nothing.
- **`Combat/PlaneDamage.cs:92`'s `SpinningPartTag` is never attached** (read at
  `:481` and `CrashDents.cs:211`). Not merely dead: propellers are
  damage-classified and crash-dented as static panels.

**The test to add for every event you declare:** `grep -rn "<Event> *+=" ` must
be non-zero, and for a `Hooks`/initializer-style field, the field must appear
on the left of an `=` outside its own declaration. `Editor/Probes/CoreRestProbe.cs:80`
asserts `plane.Hull.OnShowWasted != null` and **passes** — a probe proving a
wire that leads nowhere.

**And the scene test:** `Assets/Scenes/Game.unity` is the ONLY scene that boots
the game; `Fly.unity`, `Hangar.unity` and `Tbilisi.unity` are legacy
scaffolding. A MonoBehaviour whose guid has no hit in `Game.unity` does not
exist at runtime. `City/LandmarkRenderer.cs` is exactly that case: it attaches
`LandmarkTag` correctly (`:309-311`, called at `:189`/`:232`, with the right
JS-faithful split that excludes the statue pedestal pair) and has **zero
references anywhere and no hit in Game.unity**. `Game.unity` also predates
`SceneEnvironmentSetup.cs`, so the ACES/SSAO/bloom fix is coded but not baked.

The scheduling consequence is unchanged and worth repeating: fixing any of
these moves the ported percentage by **zero**, because the JS lines are already
replaced. Treat the headline hours as a floor.

## [CLOSED 2026-10-27] The plane hall, warehouse, crane and landmarks were in NO scene

`Assets/Scenes/Game.unity` is the only scene that boots the game (`Fly.unity`,
`Hangar.unity`, `Tbilisi.unity` are legacy scaffolding). It was baked at 13:30
while `Editor/BuildPlayableScene.cs` was last changed at 19:04, and it called
none of `ShedFill.BuildWarehouse`, `WarehouseCraneFill.Build`,
`PlaneHallFill.Fill` or `City.LandmarkRenderer`. So the whole
warehouse/crane/wind-tunnel/slung-P-51 feature, **all 35 parked aircraft**, and
every landmark + `LandmarkTag` were probe-verified and unreachable in play.

The trap: `Editor/BuildPlaneHallScene.cs` DOES call them, which is why greps for
a call site came back satisfied — but it bakes `Assets/Scenes/PlaneHall.unity`,
which nothing ever loads. A stale header comment in
`Props/WarehouseCraneFill.cs` pointed future passes at that file as "the call
site to add to", and has been corrected.

Now wired into `BuildPlayableScene.cs` and rebaked, verified from the bake log
and by grepping each script's `.meta` guid against the scene:

    GAMESCENE warehouse=1 shell_w=48.0 shell_l=138.8
    GAMESCENE crane=1 scale=1.000 hook_local_y=7.9000 cable_pts=164
    GAMESCENE planehall=1 parked=35 failed=0 tanks=15 dps=20
    LandmarkRenderer / ParkRenderer / MetroRenderer / MallRenderer /
    VegetationRenderer / ShopfrontRenderer / StreetFurnitureRenderer /
    SceneEnvironmentSetup  -> all present in Game.unity

`hook_local_y=7.9000` is the documented correct hook height, so the crane's old
~100x scale error has not come back.

**OPEN, and a real risk: the rebaked scene is 1.7 GB.** The fleet and the shed
kit are serialised as scene objects, which is how `BuildPlaneHallScene.cs`
always did it, but at that size scene load time needs measuring before this can
be called done. The alternative is `City/CityRenderer.cs`'s contract — nothing
baked, `OnEnable()` -> `EnsureBuilt()` regenerates on load — which is exactly
what the scenery renderers use and what keeps ~1M `Matrix4x4` out of a
serialised field. Treat the parity fix as landed and the size as unresolved.

## [FIXED 2026-10-27] 9 of 35 planes carried a wrong mass or fuel figure

`Flight/PlaneCatalog.cs` used `Mathf.Round`/`Mathf.RoundToInt` in float where
`src/planes.js` uses `Math.round` in double. JS rounds a .5 tie UP; `Mathf.Round`
rounds to EVEN. The derived figures land on exact ties often:

- `Mass = empty + 0.5*(mtow-empty)` ties whenever the useful load is odd --
  wrong for b58, c130, f15, lancaster, xb35 (off by 1 kg each).
- `Fuel = 0.25*(mtow-empty)` ties whenever `(mtow-empty) % 4 == 2` -- wrong for
  f117, mig21, mig21nk, p47. p47 is exactly 850.5: JS 851, Unity gave 850.

All five rounding sites in the file now use `Data.JsMath.Round` **in double**
(stamina, fuel x2, top speed, crash damage), not just the two that were
reported -- every `Mathf.Round*` standing against a JS `Math.round` is the same
bug. Still open: 3 of 35 SPEC display strings show tonnage off by 0.1 t (f104,
aerobatic, mig21nk), a separate float32 formatting tie on an exact X50 kg value.

## [NON-FINDING 2026-10-27] landmark exclusions 31 vs 32

`tools/_probe_landmarks.py` printed `exclusions=31` as a **hand-typed string
literal** that was never computed from the data. `src/landmarks.js`'s real
`EXCLUSIONS` array has 32 entries and `LandmarkPlacer.cs:64-98` matches all 32
field-for-field in the same order. `LandmarkProbe.cs` had reported 32 correctly
all along. The probe now regexes the counts out of the live JS source instead of
hard-coding them. The feared "143 buildings uncovered" regression does not
exist -- but note this is the second stale hand-typed probe literal found in two
days (the other: `METRO_GEORGIAN_FONT_ASSET_FOUND=false`). **Suspect the probe
before the port.**

## [FIXED 2026-10-28] The >4 GB park mesh was DEAD duplicate work, not a missing instancing path

My own diagnosis was wrong in an instructive way. Seeing

    Mesh 'playset_0' data size is too large (>4GB) ... ParkBuilder:BuildPlacedAsset

I concluded the park furniture needed routing through `ChunkedInstancer` the way
the 46,627 trees are. It already was. `City/ParkRenderer.cs:184-188` renders
exactly these five categories through `PlaceScatter()` -> `ChunkedInstancer.Place`
-> `Graphics.RenderMeshInstanced`, reading the same `pb.Lamps`/`Fountains`/
`PlaysetSpots`/`SlideSpots`/`MonkeySpots` Item lists the trees use.

`ParkBuilder.Build()` was ALSO merging every placement of those five into single
meshes via `Mesh.CombineMeshes`, and the resulting `LampMeshes`/`FountainMeshes`/
`PlaysetMeshes`/`SlideMeshes`/`MonkeyMeshes` fields had **zero consumers** except
one probe's count log. So it was dead, duplicated work that happened to breach a
hard engine limit: playset is 175,772 verts x 627 placements = ~110M verts.

Fixed by DELETING the five call sites and their fields outright rather than
nulling them, so a future read is a compile error and not a silent gap. Proven
by rebake: the warning printed twice before (once per material submesh) and zero
times after, with the `PARKS` counts byte-identical (563 parks, 46627 trees, 627
playsets, 5111 lamps) and `PARKRENDER batches=583 instances=11805 drawCalls=583`
unchanged before and after -- because the merged meshes were never on the render
path at all.

**[SUPERSEDED 2026-10-30 -- flowers are now instanced too, see below.] Left merged at the time, justified by measurement:** the three
flower types (~325 placements each, bounded by `beds`=974) still use
`CombineMeshes`, because `ParkRenderer.AttachCombined` is their real consumer --
they are genuinely used merged. Measured source verts: parklamp 4,811,
fountain 40,910, playset 175,772, slide 19,974, monkeybar 825,
flower_gazania 13,559, flower_ursinia 21,064, celandine_01 8,497; the flower
combines land at ~2.8-6.8M verts, nowhere near the cap. Courts (5) and the zoo
(4) never used this path.

**The rule this violated, worth stating generally:** anything `ParkRenderer`'s
chunked instancing already renders must never ALSO be `CombineMeshes`'d in
`ParkBuilder`. And more generally -- when a "missing implementation" is
diagnosed from a stack trace, check first whether a sibling file already does it
correctly. Two of today's findings were duplicate-owner bugs, not absent code.


## [FIXED 2026-10-30] The 1.78 GB scene was THREE FLOWER MESHES, not the fleet

A per-class byte census of `Game.unity` (parsing `--- !u!<classid>` boundaries):
95.2% was 210 `Mesh` documents, and **1.756 GB of the 1.78 GB was three of
them** -- `flower_ursinia_0` 851 MB, `flower_gazania_0` 570 MB, `celandine_01_0`
334 MB -- `ParkBuilder.BuildPlacedAsset` merging every flower placement into
one mesh. The warehouse/crane/fleet I had blamed was ~25 MB, 1.3%.

**Root cause, not in any brief:** every scenery renderer is `[ExecuteAlways]`,
so `AddComponent` in `BuildPlayableScene` fired `OnEnable -> EnsureBuilt`
DURING THE BAKE and `SaveScene` serialised the runtime Meshes/Materials/
Texture2Ds they produced. Their class headers said "nothing here is baked into
the scene file." That was false. The bake now strips generated children of all
nine renderer roots before saving (`GAMESCENE stripped_generated roots=9`), and
the sheds/fleet build at load through `Flight/ShedSceneFill` (`OnEnable ->
EnsureBuilt`, same contract).

    Game.unity     1,866,677,217 B (1780.2 MiB)  ->  12,125,111 B (11.6 MiB)
    Deserialize    6,055.9 ms                     ->  90.0 ms
    peak memory    2.50 GB                        ->  1.64 GB
    embedded Mesh / Texture2D   210 / 74          ->  0 / 0

**And everything was rendered TWICE.** Only `ShopfrontRenderer.ClearChildren()`
walked real Transform children; `ParkRenderer`/`LandmarkRenderer` cleared a
private `_children` List that a scene load leaves EMPTY, and `Metro`/`Mall`/
`StreetFurniture` had no clear at all -- so each load added a fresh build on top
of the deserialised one (~964 duplicate objects, all three flower meshes
included). Fixed at source with `City/GeneratedChildren.Clear`, proven by a
load-twice harness (`Editor/SceneryRebuildCheck.cs`: 8 renderers, pass1 ==
pass2 for all) that was shown to FAIL on a negative control (restoring the old
clear gave `pass1=224 pass2=448`).

**The flowers are now instanced** through `ChunkedInstancer.Place` like
everything else: 14,190,000 static verts -> 184,117 (the 14,005,503 removed is
exactly 325x13,559 + 325x21,064 + 324x8,497); `PARKRENDER` 583 -> 759 potential
draws, but only **7** real `RenderMeshInstanced` calls a frame at a 700 m cull
(`parks.js:407`), against 14 M verts that were previously submitted every frame
because the merged meshes' bounds spanned the whole city. `chunkM` stays 1500,
`parks.js`'s own constant.

**Also fixed: park furniture cast shadows the original never did.** `parks.js:
344-357 inst()` passes `castShadow=false` (`citymap.js:7940`); `ParkRenderer`
passed `true` on all 12,779 instances. Now `false` -- parity, and a shadow-pass
win.

**The 35 parked planes had no `PlaneDamageRecord`**, so the registry that
`CombatWorldAdapter.Install()` DISCOVERS (`FindObjectsByType`) never saw them:
not clickable, not Tab-able, no damage, **not boardable**. `PlaneHallFill` now
attaches one per plane with `MaxHullPoints = PlaneStamina(def)` in double
(`planehall.js:290`, `target.js:69`): `damageRecords=35/35
hullPointsTotal=62855`.

**Still open from this pass:** the whole scenery layer and the sheds/fleet load
art through `AssetDatabase` under `#if UNITY_EDITOR` -- **a player build builds
none of it.** One change (Addressables or Resources) across the eight renderers
plus `ShedFill`/`PlaneHallFill`/`WarehouseCraneFill`. Runtime objects not hung
off a child (instanced batch materials, plinth meshes, tint clones, the SignPool
atlas) still leak per rebuild. `CITYRENDERER drawCalls=781149` for the building
kit is the largest draw figure in the log and nobody has measured it this week
(the perf memory records the per-group draw cliff and a twice-reverted fix).

## [FIXED 2026-10-30] The game was SILENT: `EngineSound` was never constructed

`GameLoop.Engine` was read in nine places (`Ignite`/`Shutdown`/`Kill`/`Canopy`/
`Belt`/`Touchdown`/`Explosion`/`Update`, plus `CombatWorldAdapter`) and assigned
by nothing in `Assets/Scripts` -- `grep -rn "new EngineSound"` found exactly one
hit, `Editor/Probes/EngineProbe.cs:30`. Every call was a null-conditional
no-op. The whole `src/sound.js` start/idle/run/shutdown state machine over the
real CC0 aero-engine recordings -- a full wave of the user's work -- produced
nothing. It hid because `GameLoop.cs`'s citation `game.js:1383-1436` covers
`_findGround` INCLUDING `1411-1419`, where the JS builds and starts the sound:
**the lines were billed, the behaviour was absent.** Now built in
`BeginSession()` per `game.js:1411-1419` (`GameLoop.cs:391`), behind the same
settings gate, with `ResumeRunning()` for a session resumed airborne. The
settings engine toggle and volume slider (`SettingsView.EngineToggled` /
`VolumeChanged`) also had zero subscribers; wired per `settings.js:175-186`.

This is the eighteenth "finished and unreachable" system, and the clearest
statement yet of the measurement failure: a citation range is a CLAIM about
lines, and a claim can be true of the lines and false of the behaviour.

## [FIXED 2026-10-30] Building HP was a third formula nobody wrote down; the crumble ran a stage ahead

`CombatBuildingChunk.MaxHp` was `220 + h*26` -- not the JS (`citymap.js:5344`
`round(clamp(180 + h*14 + area*0.35, 280, 3200))`, `area` = `|ShapeUtils.area|`
of the footprint) and not the pre-migration table either (`BuildingDamage.OldMaxHp`
= `clamp(60+h*8+area*0.1, 100, 800)`). Now the JS formula with a double
shoelace. And `_stageOf` was `floor((1-frac)*10)` capped at 9 where the JS is
`hp<=0 ? 9 : min(8, floor((1-hp/maxHp)*9))` -- so a building with 5% hull left
read `floor(9.5)=9` and **collapsed with HP remaining**, and every stage above
~6 ran one ahead. No probe asserted either number. **Still open:** `PaintStage`
sinks the roofline on a LINEAR curve where the JS uses the measured table
`STAGE_H = [1,.97,.94,.9,.85,.79,.72,.63,.52,.12]` (`citymap.js:1350`) --
assigned, with the collapse-FX publishers (`game.js:427/429`, no C# collapse
path raises anything to subscribe to).

## [FIXED 2026-10-30] `FootPlaneProbe` targeted every plane EXCEPT the right one

Wired at last (both consumers: `FootController.ProbeGround` and `FootCover`'s
wall probe via `Cover.ExtraTargets`), but the GameLoop side handed over *the
nearest registry plane that is NOT `ActivePlane`* -- every machine except the
one the JS means. `foot.js:4751-4753` is `const p = g.plane`: the hero's OWN
plane, the one he climbed out of, the single machine deliberately kept out of
the shared collision set. Every other plane is already in it (`planehall.js:
332`, `game.js:856-862`). Now `_foot.NearbyPlane = ActivePlane?.transform`.
Also settled: `CoverMask`/`GroundMask` are both `~0` and assigned nowhere, so the
extra-target pass is a no-op fallback today and correct to keep.

## [OPEN -- DO NOT WRITE] Building-damage persistence: two decisions first

The plan is written (Core agent report, 2026-10-30) and deliberately NOT coded.
Beyond "it writes to the live shared Mongo", two things would have corrupted
other players' data irreversibly:

1. **The key space is THREE spaces, not one.** `String(rec.bi)` is only the
   extruded-building case; the same dictionary holds kit-prop destructibles
   keyed `"<KIND[0]>:<round(x)>:<round(z)>"` (`citymap.js:1746`) and landmarks
   keyed `"L:<name>"` (`:1786`). Whether Unity owns the other two is a product
   decision.
2. **The hole coordinates' FRAME is unresolved and the DB is shared with the
   live JS client.** `_recordHoles` stores `[x,y,z,r]` in the game frame and
   never says so. Unity mirrors X. A Unity client writing un-mirrored X puts
   every hole on the **wrong side of every building** for JS players, and vice
   versa. Mirror on the way out and the way in -- settled before any `PutDamage`.

Also: rows are ABSOLUTE hp against the maxHp table, so the wrong HP table above
would have committed values no client could interpret, with only two repair
paths (wipe everyone's city, or a second `DMG_SCALE` migration).

## [FIXED 2026-10-30] Plane damage marks are DRAWN now -- and the renderer's first run exposed nothing, the engine's did

`Fx/PlaneMarkFx.cs` + `Shaders/PlaneMark.shader` render all five `PlaneMarkLayer`s
as instanced quads on `ImpactDecalFx`'s model, owned by `PlaneDamage` as the JS
owns them (`planedamage.js:523-528`), reached through the existing
`planeDamage.Update(dt)` tick -- zero Core edits. Per-layer state verbatim:
`offFactor = -8 - 2*order`, `queue = 3006 + order`, 2x2 atlas of four via
`_UvScale` + per-instance uv. Proven SUBMITTED through a real Refine->Trace->
Stamp->Update->Render on a real MeshCollider: round 1 `holes=1 exits=1`, round
2 same spot `tears=1` (the hole becomes a tear), canopy `craze=1`. Marks ride
the airframe (`moved=0.000000` after a 137/11/-42 m move), fleet-off hides them
(`on=4 off=0 back=4`). **Not proven: the pixels** -- `-nographics` cannot
render; `tools/_probe_realholes.mjs`'s before/after pixel read on all 35 planes
is still the bar. Open: the craze atlas costs **4.7 s of a 5.3 s CPU bake**
(staggered one layer per frame for now; paint on a worker thread in
`DamageDecals.DrawCraze`); no mipmaps on the atlases (`DamageTextures.ToTexture
mipChain:false`); `Exit()` never fires on a single-shell part because Unity
mesh raycasts ignore backfaces; atlas variant is per-slot not per-alloc.

## [FIXED 2026-10-30] Un-orphaning `EngineSound` ran its construction for the first time -- and it crashed

The fix for "the game was silent" (wiring `EngineSound`) made `Start()` execute
for the first time in the project's life, and it held two latent faults no
probe had ever reached: filters added BEFORE sources ("Add required component
of type AudioSource first" -> null -> NRE, with `_built = true` set at the TOP
of the method so `Update()` dereferenced a null `_idleSrc` every frame, 122
errors in 120 ticks), and `AudioSettings.outputSampleRate` read from the audio
thread in `OnAudioFilterRead` (main-thread-only; 598 throws). `EngineProbe`
drives the whole RPM state machine **without ever calling `Start()`** ("the
math runs identically either way"), so 100% of the rig construction had no test.
Fixed: sources before filters, `_built` set at the END, sample rate cached on
the main thread. `CoreRestProbe`: SOME FAILED -> ALL PASS; 121 -> 1 logged
errors across 120 real ticks.

**The rule this adds:** un-orphaning a probe-verified system is the moment its
untested CONSTRUCTION path runs for the first time. It needs a run-and-read-the-
log, not a green probe. Apply it to every orphan wired from here on.

## [OPEN 2026-10-30] The one remaining logged error on a real load: a map texture over 8192 px

`UI/Map/MapUiRoot.cs:72 LoadTexture` -> `Texture has out of range width (got
9984 max supported 8192)`. A satellite map image is 9984 px wide; Unity's hard
limit is 8192. The JS drew it on a canvas with no such limit. Downscale or tile
-- and check `mapmeta.json` / the Mercator math for any dependence on the image's
pixel dimensions before changing them.

## [FIXED 2026-10-30] The orphan guard's comment stripper ate 339 lines of real code

My own fix (strip comments so a filename in a doc comment cannot flip a type
LIVE) ran a block-comment regex BEFORE the line-comment one, so any `//` comment
containing `/*` -- `Core/*`, `src/*.js`, everywhere here -- opened a block that
swallowed code to the next `*/`: 339 lines, 21 of them in
`Core/CombatWorldAdapter.cs`, the main wiring file, so the guard was latently
blind exactly where it matters and reported `PlaneMarkFx` PROBE_ONLY the moment
it was wired. The other regex order fails on `/* see http://x */`. Replaced with
a single left-to-right scanner that handles `//`, `/* */`, `"..."`, `'...'` and
`@"..."` in order of appearance and blanks string contents. Found by the agent
whose construction line it ate.

## [FIXED 2026-10-30] Real aeroplanes have propellers now -- 31 of 31, every radius matching the JS

The whole `propeller.js` pipeline was a faithful port whose only production
caller was the three wind-tunnel fans. Now `Flight/PlaneProps.Build`
(`planes.js:803-812`, once per type, cached) and `Flight/PlanePropellers`
(`LateUpdate` -> `SpinPropellers(rpm = Engine.Rpm * 2700)`, `game.js:2585`) run
for the player's plane (`PlaneController.Init`) and all 35 parked ones
(`PlaneHallFill`). `PlanePropsProbe` vs `tools/_probe_props.mjs` on the real
FBX files: 15 propeller-driven planes, **31/31 found, every radius equal** to
the JS (spitfire 4.51, p47 **2.60 -- the two blades modelled out of the disc
are intact**, lancaster 2.23 2.22 2.19 2.21 in hub order). `SpinningPartTag` is
attached at detection AND at finish, before any round can hit. Blades now fade
behind the disc (URP Lit opaque ignores alpha; `IsFadeable`/`MakeFadeable` per
`propeller.js:1115-1121`). Crane P-51 regression check byte-identical.

**Two real bugs found by the measurement:**

1. **`PlaneHallFill` was destroying each FBX's natural import transform** --
   `SetPositionAndRotation(zero, identity)` + `localScale = def.Scale` on the
   prefab root, the exact trap the skill documents. 34 of 35 import at identity
   and did not care; **`t28.fbx` imports at rotation (270.02, 0, 0) / scale 100**,
   so the T-28 was parked **12 cm across and lying on its back**, which is why
   its propeller search found nothing. Nothing warned: the floor-fit check only
   fires on a plane that is too WIDE. Fixed with a holder GameObject carrying
   the placement; `parked=35 failed=0` unchanged.
2. **14 hinted hubs on four models were found at MINUS the hinted nose.** The
   one consistent explanation is that the glTF->Blender->FBX bridge
   (`tools/glb_batch_fbx.py`, `axis_forward='-Z'`) spends its single
   handedness flip on X, not Z -- so these airframes point along Unity's **-Z**
   and `FlightModel.cs`'s "+Z forward" header does not hold for the assets.
   `PlaneProps.FileNose` derives the nose on that basis (20/31 -> 31/31).
   **Being audited across all 35 planes now**, including what compensates for
   the flying plane and whether the unused `YawUnity`/`RollUnity` are a bug.

**And one instruction of mine that was wrong:** I told the agent to "fix" the
`PROPTAG hint_make hx=0.1 vs -0.1` mismatch as an inconsistency. It checked and
showed the C# is RIGHT: a hub from a hint is an external hand-measured constant
that MUST flip under the X mirror (the 27-yaws rule), while geometry-derived
hubs project onto and multiply back by the same basis so their negations cancel.
Proof: lancaster's hubs come back in hub order on both sides; a wrong sign would
swap the pairs. The probe comparison now mirrors that one row, with the reason
inline. Reported, not fixed: the crane P-51's `BuildPropellers` uses a raw hub
triple and a +Z nose, putting the hub 4.95 m behind the cockpit; and
`_bakeCollider`/`_dropCollider` (`planephys.js:561-590`) are unported, so
wreck-vs-wreck collisions degrade to hitting the ground. Both assigned.


## [FIXED 2026-10-30] Collapse FX, the crumble curve, the windsock -- and the 781k-draws question answered

`game.js:427/429`'s collapse publishers now exist (`Core/CollapseFxHooks`,
ASSIGNED not `+=` at `CombatWorldAdapter.cs:538` so a domain reload cannot
stack a second fireball), raised at the JS's own sites (`citymap.js:1552`,
`:2392-2405`, `:2462-2524`); and a building now has a real collapse ANIMATION
(`CollapseAnim` un-orphaned) instead of snapping to rubble. `PaintStage` runs on
`STAGE_H = [1,.97,.94,.9,.85,.79,.72,.63,.52,.12]` with `citymap.js:1969-2012`'s
buckle/lean/soot line for line: `roofIdeal` exact at all ten states, apex `vy`
to the 7th digit (float32 mesh storage). The windsock had never existed in the
scene (its only placer ran in a bake nobody loads); `City/WindsockRenderer`
builds it at load and reads the SAME `Wind` state as the tree-sway shader.

**`CITYRENDERER drawCalls=781149` is a TOTAL, not a frame.** `CityDrawProbe`,
through the same gate the game uses: **329 draw calls per frame at spawn**, 182
at +800 m. Culling works. The old "1,557 draw calls / 245 batches" figure had NO
spatial culling, so on submitted draws the port is better. What is 3,188x worse
is the BATCH COUNT: the key is `variant x 700 m cell x quantised tint
(TintSteps=24) x submesh`, so with ~3.8 placements per (mesh, cell) almost every
instance is its own batch -- 1.86 instances/batch, 734 materials, ~110 MB of
2-element arrays swept every `LateUpdate`. The JS uses one per-instance colour
attribute and one draw per material. Deliberately NOT fixed in that pass (the
per-group-draws fix regressed old town and was reverted twice); assigned now
with measurement required before and after.

## [OPEN 2026-10-30] The facade relief is computed and thrown away

`BuildingBuilder.BuildRich` computes 500,019 windows, 160,000 balconies, 41,785
cornices, glass mullions/bands, ACs, dishes, drainpipes, entrances, parapets and
roof props on every build, and **nothing in production consumes `.Relief`** --
only two probes. `FacadeKitBuilder`, the renderer for it, is dead. This document's
"windows placed" was true only in the DECIDED sense. The biggest remaining City
gap; it must land together with the batching fix above, because it would add
hundreds of thousands of instances into the renderer whose batching is already
the port's largest open perf risk.

## [NON-FINDING 2026-10-30] The "-280 building claims regression" was the oracle

`citymap.js:4601` skips any footprint inside a landmark exclusion ring; that
landed in the C# on 10-27 (the 143 grey-box landmarks fix). `tools/
_probe_buildingquality.py` models no exclusions at all, so the correct C# reads
as a regression. 143 footprints fall inside `landmarks.js:9-42`'s rings, ~43-59
with OSM facts; `BuildingParts.Expand` turns one into ~2 volumes, hence -280.
The `BuildingFacts.At` float-vs-double lead was chased and widened to double:
**it changed no number** -- Tbilisi's |x| <= 18 km resolves 2 mm in float32, so
a ten-vertex ring cannot flip a 9 m threshold. Sixth stale-oracle finding.

## [FIXED 2026-10-30] The rifle "sign flip" was in the probe; GunPod tapers; eight probes lying on PASS lines

`RangeProbe.cs` fed `applyRifleHold` a Three.js aim `(0,0,-1)` -- forward in
Three, BACKWARD in Unity. Cover/reload derive their forward from `cross(up,
right)` (operands swapped in C#, correctly) and so never read the bad aim; aim/
carry did, came out mirrored, and "agreed" with the JS's raw Z. `RifleHolds.cs`
is unchanged. The pair now runs the JS's own synthetic rig on both sides in the
body's frame: **106 values, worst 0.000e+00** (was 0 values compared).
`Guns.cs`'s pod housing taper was reversed and its barrel taper collapsed to a
mean: now 0.200/0.160 and 0.055/0.050 per `guns.js:398-399`, 83 values. The
arms FBX were never missing: a `Check()` helper printed its FAILURE note on PASS
lines in **eight** probes. `GunAnim`'s no-aim fallback was `(0,0,-1)` retyped --
the arms posed as if the hero faced backwards. `DestRestProbe` asserted `_Color`
while the code writes `_BaseColor`, passing vacuously.

## [OPEN -- THE BIGGEST VISIBLE BUG] The flown plane points its tail along the thrust vector

Settled by measurement across all 35 airframes at their natural import transform
(`PlanePropsProbe.Axis`): `noseNegZ=29 nosePosZ=3 noseOther=3` -- exactly the
set `planes.js:826-836` straightens (yaw pi "stored tail-first", yaw -pi/2
"stored nose-west", two off-axis), **with the straightened nose still at -Z**.
The glTF->Blender->FBX bridge mirrors X, not Z, so the "forward-flip rule" does
not apply to these files. Two tolerance-free proofs: the post-fix hinted hubs
invert the pre-fix triangle counts exactly (`c130 hubTris=[1687,...]
hubTrisNegF=[0,...]`), and the P-51's authored spinner constant `f=4.95` lands
on the end the measurement calls the nose.

**Nothing compensates.** `PlaneDef.YawUnity`/`RollUnity` have zero Transform
consumers; no 180-degree yaw exists on any plane model. `FlightModel` thrusts
along `+Z` while the P-51's geometry, seat (`seatZ=-1.48`), canopy hinge,
`PlaneCockpitMeasurement`'s search window, the propeller spin axis and
`PlanePhysics.cs:573`'s nose sample all live at `-Z`. So the player flies the
Mustang tail-first, 4 airframes are tail-first in the hall, 2 sideways (which
also corrupts `PlaneHallFill`'s `Span`/`Len` packing), tornado and tu144 are
~40 degrees askew, and neonfly is rolled 90 degrees onto a wing (the JS comment:
"flew INVERTED before the correct sign was found"). `f4f`'s `cockpit=insane`
(1 of 35) is this bug in the seat bake.

**Deliberately NOT patched piecemeal.** Rotating the model child alone moves it
out from under every -Z-anchored measurement above -- the "mirror X twice"
failure. The fix is one pass: compose `YawUnity`/`RollUnity` + one 180-degree
yaw onto the model child AND hand the model frame to the seat baker, the canopy
hinge, the box shell, the spin axis and the hall packing in the same change,
re-bake 35 cockpits + 35 colliders, and prove it with a probe that flies one
machine and asserts the nose LEADS. `FlightModel.cs`'s header now documents the
flight-frame/model-frame boundary; `RollUnity` is `-Roll` (an X mirror negates a
roll), zero consumers so no behaviour change yet. Also: `Editor/
BuildPlayableScene.cs:246-247` and `BuildFlyableScene.cs:85-86` still reset the
player model child to identity -- the T-28 trap.

## [FIXED 2026-10-30] Crane P-51 propeller on the nose; wreck colliders ported

`WarehouseCraneFill.BuildPropellers` assigned the raw hub triple and a `+Z`
nose: hub at `(0,1.57,+4.95)`, 4.95 m aft of the spinner. Now `(0,1.57,-4.95)`,
`onNose=1`, hook `7.9000` unchanged. `planephys.js:560-591` `_bakeCollider`/
`_dropCollider` ported; `LooseAirframeActorRef` is finally written
(`PlanePhysics.cs:1004`). Premise corrected by measurement: in the JS too,
`planeRec?.body` is null at every moment a ray can hit it, so wreck-vs-wreck
already resolved to the world actor -- the port is now correct by construction
rather than by the absence of a writer.

## [FIXED 2026-10-30] There was no login screen -- the game booted straight into flight

`Boot` was in `Game.unity` and fully wired, but `grep -rn "new CharacterView"`
returned nothing in the whole project, every event the screens bind to had zero
reachable `+=`, and `SetScreen(BootScreen.Login)` invoked a null delegate --
while an enabled `PlayableSceneBoot` in the same scene ran `Install` +
`BeginSession` from `Start()`. The user's shell (login -> personages -> ▶
შესვლა flies; start menu and map pages deleted 2026-10-19; ⚙ in the personage
head) did not exist. Now `UI/Shell/ShellUi` + `ShellView` (`index.html:50-104`,
every Georgian string verbatim and sourced; no DPS/TANK/WASTED, asserted),
built in `Boot.Awake`, ticked in `LateUpdate` (bone writes), destroyed BEFORE
the game's panel is created (two live `UIDocument`s make `FindAnyObjectByType`
a coin toss). `PlayableSceneBoot` is stood down in `Awake` unless `-nologin`.
`HeroScreenProbe` drives the shipped scene login -> Character (roster 2, all 4
bodies preview) -> Enter (`GAME_STARTED=true`, 120 ticks, 0 errors) -> back ->
logout: **46 PASS, 0 FAIL**. Orphans 13 -> 7.

Two construction-path bugs surfaced on first run, as the un-orphaning rule
predicts: a `--` inside a UXML comment imported as an EMPTY asset with no error
(skill updated), and disposed views stayed subscribed to `Boot`'s events and
threw from behind the live ones (`Unbind()` added). Open: `#loading-overlay`
is not ported (`Boot.HintChanged` dead); the scene has ONE `PlaneController` so
every hero gets the p51 regardless of starter plane; the preview rig's two
`DirectionalLight`s perturb the lights hash.

## [FIXED 2026-10-30] The outdoor white-out: three gates, and the lights were never the problem

`oldtown.png` was 10.4% pure white (16% at >=250), `landmarks.png` 45.7%. Three
independent gates, all shut:

1. **`Assets/Settings/TbilisiFly_Renderer.asset` had `postProcessData: {fileID: 0}`.**
   `UniversalRenderer.cs:318` is `postProcessEnabled = data.postProcessData != null`
   -- with it null the RENDERER drops every post pass (tonemap, bloom, LUT) with
   no warning, regardless of camera or Volume. The asset was created by script,
   not via Create > Rendering > URP Universal Renderer, which assigns the default.
   This is why forcing `renderPostProcessing` + a Volume produced a byte-identical
   frame: gates 2 and 3 opened while gate 1 was shut.
2. **The camera had no `UniversalAdditionalCameraData`** -- URP hard-codes
   `postProcessEnabled = false` in that branch. `SceneEnvironmentSetup.
   SetupPostProcessing()` adds it (post on, AA None per `game.js:243-259`).
3. **Gamma colour space** (`m_ActiveColorSpace: 0`) with Three.js's LINEAR light
   values: sRGB albedo was lit as if linear, so a 0.44 ground read as 0.44 not
   0.16 and the city sat ~pi above white before anything could tonemap it. Now
   Linear. 798 texture metas checked: 736 sRGB albedo, 62 linear (`*_n`, `*_mgs`)
   -- none mis-tagged, reimport ~1 min.

The scene `Volume` was a red herring: URP 17 boots `VolumeManager` from
`URPDefaultVolumeProfileSettings`, which already pointed at
`DefaultVolumeProfile.asset`. **Measured light values UNCHANGED** (sun 2.70,
hemisphere 2.00, fog 0.000060, shadow 160 m, ACES, postExposure log2(0.95)).

**Proof in pixels, with a control arm:** oldtown 234/231/211 -> **142/141/116,
0.00% white**; landmarks 45.7% -> **0.00%**; toggling only `renderPostProcessing`
back off re-blows every view by ~24 levels -- that is ACES, measured. Trap
recorded in `LookShotProbe.cs`: toggling `Tonemapping.active` on the profile
measured 0.00 difference and nearly read as "post is dead" -- `VolumeManager`
reads `active` only when it rebuilds the default state, not per frame.

**Open decision, not a bug:** three.js `BRDF_Lambert` divides by pi, URP's
Lambert does not, so the verbatim 2.7/2.0 are ~3.14x hotter here. Nothing clips
now; the frames read a touch hot. Deviating from measured values needs a
side-by-side with the JS build and the user's call.

## [FIXED 2026-10-30] The batch explosion, the facade relief, and both broken shaders

`CityRenderer` batches **781,149 -> 17,175** (one batch per mesh x 700 m cell;
tint left the key, submeshes moved inside `Submit`), materials 734 -> 43,
per-frame draws at Old Town **3,306 -> 553** (6x), spawn 329 -> 206, kit memory
28 -> 16 MiB — proven draw-neutral by rebuilding the old key in the same run
(781,149 exactly). Tint is per-instance through a `MaterialPropertyBlock` vector
array into `Shaders/CityKit.shader` (`UNITY_DEFINE_INSTANCED_PROP`), pixel-
proven RGB; `CityWall.shader` byte-identical (SRP Batcher). **The facade relief
is DRAWN**: `CITYFACADE batches=1287 instances=742,561` (500,008 windows,
160,000 balconies, 41,528 cornices, mullions, bands, grilles), +55 draws at Old
Town — affordable only because the batching landed first. All six layers draw
the procedural fallback box, and so does the JS (`facade_kit.glb` has one node).
`PlaneHull.shader` (three bare-half literals) and `GlassGlint.shader` compile;
under real graphics `realHoleSeeThrough=PASS` — **the metal really goes now** —
and `noErrorMagenta=PASS` at 8 stations, with a negative control.

**The near-miss, recorded in the skill:** `RenderMeshInstanced<T>` with a tint
field throws, and renamed to the shader property it draws with NO tint — every
counter better, 169,004 grey buildings. Caught by a three-pixel RGB test.

**Caveat on the first screenshots:** `CityRenderer.LateUpdate` threw on that
call during the GPU pass, so `oldtown.png`/`landmarks.png` contained terrain and
chunk meshes but **no instanced kit**. Re-shot now that it renders.

**Answered:** river, roads, embankment, bridges, tunnels, infra, water, grass,
cut terrain, road signs/streetlights have **no builder in the boot scene** —
`grep -c` in both scene builders is 0 for all of them; only `CityLoader`'s
stages drive them and those have no runtime caller. Terrain is one flat
material (`BuildPlayableScene.cs:66`). Vegetation at ~600 instances/frame is the
JS's own radii (`citymap.js:7823-7824`), not a bug.

## [FIXED 2026-10-30] The plane flies nose-first -- one chain, every anchored site, flown to prove it

`Flight/PlaneModelFrame` (new): `planeRoot` (FLIGHT frame, nose +Z, where
`FlightModel`, `Guns.MuzzleLocal z=+3.65` and the rocket pylon already lived) >
`model_frame` (180 degrees about Y = `planes.js`'s `wrapper`, nose -Z; measure
HERE) > `model_scale` (`def.Scale`, below the frame because a node's own scale is
absent from its own local space) > `model_file` (`YawUnity`/`RollUnity` =
`planes.js:834`'s `fixed`, with the recentre pre-rotated into its localPosition
so its origin stays the FILE origin -- the literal transcription moved the
Cessna's hand-measured hubs off their geometry, 31 -> 29 propellers, caught) >
`model` at its NATURAL import transform. Every -Z-anchored site was handed the
frame in the same change: cockpit baker/kit/measurement, the five ground-contact
stations in `PlanePhysics`, `AirframeBreakup`'s regions (where **wingL/wingR had
been swapped**), `PlaneDamage`'s cut line and plume, the hall packing (Cessna
310 9.88x11.20 -> 11.20x9.88). Marks (`CrashDents`/`PlaneMarkFx`) verified
frame-agnostic, not assumed.

Proof: `ORIENTSUM noseLeads=33/35` (the two are asset questions: `p100` has no
fix in the spec and its propeller sits where the slab rule says tail; `hsp75` is
a canard pusher at 0.47x its published span), and **four planes flown 180 frames
through the real `PlaneController -> FlightModel -> PlanePhysics`** with the nose
leading the velocity vector (`velVsForwardDeg=0.4`). Two numbers the runtime
could not know: the P-51's authored spinner `f=4.95` now measures `propZ=+4.95`;
`f4f`'s `cockpit:{z:-1.8}` override lands at `seatZmodel=-1.80` exactly (it was
the one insane cockpit of 35). Unchanged: `PROPSUM 31/31`, `AXISSUM` (raw
assets), crane hub `-4.950 onNose=1`, `AIRBOX spitfire bellyOffset=-0.469`,
`TOUCHDOWN 100.000` -- a 180-degree yaw about Y cannot change `min.y`. The
identity reset in both scene builders is gone (it was both the T-28 trap and
the tail-first bug). `GameLoop.SwapToPlane` now refuses a loose or dead machine
(`game.js:779`).

Open, as spec data not code: `tornado` has no canopy/glass/cockpit mesh at all,
so its cockpit cannot be found on either side -- a `cockpit:{z~-5}` override in
`src/planes.js` is the parity fix and is the user's call.

## [FIXED 2026-10-30] The player's plane spawned INSIDE A BUILDING, and three more Core gaps

`City/SpawnFinder.cs` — a complete port of `src/citymap.js:6484-6531`'s
clear-spot search on ვაჟა-ფშაველა — had **no runtime caller at all**
(`grep -rn SpawnFinder unity/Assets/Scripts` → nothing; only
`Editor/Probes/CityGeoProbe.cs`). So the aeroplane stayed where the scene baker
put it, at the DATA ORIGIN, which is Delisi **mid-block**: plane y 492.09,
bounds 490.94–495.70, and a downward ray hitting `chunk_18 root=Buildings` at
**494.64** — a building surface through the airframe on frame one.
`Core/SpawnResolver.cs` is the caller now (the same merged road-net load
`VegetationRenderer`/`StreetFurnitureRenderer` already do at runtime), and
`GameLoop.ResetPlane` gained `PlaceOnSpawn` — `src/game.js:747-751`, which this
port simply did not have: `ResetPlane` reset the airframe and the camera and
never moved the plane.

Measured on the shipped scene: `SPAWN pos=(-4.30, 477.46, 30.40) clear=true
road=22.2m roads=56259`, the ray below hitting only `tile_5_3/Terrain`,
`HERO id=above n=0`, and `scratchpad/shots/heroplane_top.png` showing the
aeroplane in the open with its own shadow instead of a roof.

**The yaw is NOT just `GeoFrame.YawToUnity`, and that is worth recording.**
`SpawnFinder` returns `atan2(-fx,-fz)` — the rotation for an object whose nose
is its own **-Z**, which is what `src/planes.js` straightens every airframe to.
This port's `planeRoot` is **nose +Z** because `Flight/PlaneModelFrame.cs`
absorbed those 180 degrees in `model_frame`. A literal `YawToUnity` points the
plane down the road BACKWARDS (ENE, not WSW). The right transform is
`yawUnity = atan2(-noseGameX, noseGameZ) = PI - yawGame`, and the probe checks
it as geometry (`dot=1.0000` against the mirrored road heading,
`legacyDot=0.9847` for `citymap.js:6524-6528`'s WSW side), never as a number.

Two further parity bugs fell out of actually placing the plane: `ResetPlane` set
`Airborne = true` citing `_liftoff` (game.js:1713) when `resetPlane` itself is
`this.airborne = false` (**game.js:727** — a reset plane is PARKED, which is why
the next JS line is `_setHint(this._takeoffHint())`); and the hero's body was
left behind at the origin, inside the same building, because
`Boot.SpawnHeroBody` runs before `BeginSession`.

Closed in the same pass:

- **`#loading-overlay` is ported** (`UI/Common/LoadingOverlayView.cs`,
  `index.html:154-159` + `style.css:189-220`), built on the GAME panel because
  the hint fires *after* `SetScreen(Game)` has disposed the shell's. Georgian
  verbatim, no English fallback. `Boot.HintChanged` — 15 invokes, zero
  subscribers — comes **off** `docs/dead_events_baseline.txt`; the guard prints
  `FIXED (drop from the baseline): Boot.HintChanged`.
- **Every hero now gets their own starter plane.** The scene has ONE
  `PlaneController`, so `FirstOrDefault(...) ?? FirstOrDefault()` handed
  everyone the p51 and the whole `StarterPlaneFor` chain decided nothing.
  `Core/StarterPlaneSkin.cs` re-skins that one controller (model, cockpit,
  shell, damage record) and `PlaneController.ReInit` rebuilds the flight rig —
  not 35 flight rigs in the scene for one flyable machine. `STARTER` veggie →
  spitfire, toys → b58, each measuring its OWN belly offset (-0.159 / 4.410).
- **The preview rig's two `DirectionalLight`s are out of the scene's light
  set.** URP has no per-light culling mask, but it raises
  `beginCameraRendering` *before* it culls for that camera — so both lights live
  disabled and are switched on only while the preview camera renders. The
  measured 2.4 / 1.2 and the cyan holo rim are kept, not traded away.
  `dirEnabled=1` before the menu, with it open, on the personage screen,
  reopened and after logout.

`Editor/Probes/CoreSpawnProbe.cs` proves all four in one launch (the lock is
exclusive), driving the real login → personage → Enter flow once per race.
**RESULT=PASS, 0 errors, 60 real ticks clean.**

## [FIXED 2026-10-30] The plane spawns on the avenue, nose-first — and `ResetPlane` never moved the plane

`SpawnFinder` had no runtime caller; `GameLoop.ResetPlane` reset the airframe
and camera and left the plane at the raw origin, inside `chunk_18` at Delisi.
`game.js:747-751` (place on the found spot) had NO counterpart. Now
`Core/SpawnResolver` loads the merged road net at runtime and `PlaceOnSpawn()`
puts the P-51 on ვაჟა-ფშაველა: `SPAWN pos=(-4.30,477.46,30.40) clear=true
road=22.2m`, the ray below hits `tile_5_3 root=Terrain`. GPU frame
`heroplane.png`: the Mustang in the open, gear down, propeller on the nose,
casting its own shadow. **The spawn yaw is NOT plain `YawToUnity`**: the finder
returns a rotation for a -Z-nosed object and `planeRoot` is +Z after
`PlaneModelFrame`, so `yawUnity = PI - yawGame`, derived as a vector and checked
as geometry (dot 1.0000) -- `YawToUnity` alone pointed the plane down the road
backwards. Two parity bugs fell out: `ResetPlane` set `Airborne=true` where
`game.js:727` sets false; the hero body was abandoned at the origin. A resumed
hero is not overridden (ApplyResume writes the save over ResetPlane, the JS
order). Also: `#loading-overlay` ported (`იტვირთება თბილისი…`, `ბრუნდები იქ,
სადაც გაჩერდი…` verbatim), `Boot.HintChanged` live; the starter plane is per
role (spitfire / b58) by re-skinning the one controller (`StarterPlaneSkin`);
the preview rig's two lights are enabled only inside the preview camera's
`beginCameraRendering` -- materials 1510 before and after menu open/close.

## [FIXED 2026-10-30] The river, roads, bridges, tunnels, embankment and grass are drawn

Every one of `RoadBuilder`, `BridgeBuilder`, `TunnelBuilder`, `TunnelCuts`,
`InfraBuilder`, `RiverSheet`, `WaterBodies`, `GrassCover`, `CutTerrainBuilder`
was probe-verified for weeks and in NO scene the game loads -- their only driver
was `CityLoader`'s stages, which have no runtime caller. Now
`City/RoadNetworkRenderer`, `RiverRenderer`, `GrassRenderer` (OnEnable ->
EnsureBuilt; nothing baked, scene +27 KB), wired by the coordinator into
`BuildPlayableScene` PAIRED with `TerrainBuilder.Build(terrain,
CityGroundData.RefinedCells())` so the 2,746 river-bank/tunnel cells are not
drawn twice. Zero builder lines changed; all 8 builder probe pairs re-run in
one launch, 8/8 PASS.

    ROADS segs=56259 bridges=1771 tunnels=262 asphaltTiles=4791 realBridges=11 bores=68
          luminaires=3918 furniture=84615 furnitureDropped=0
    RIVER sheetVerts=157547 banks=47457 quay=19769 lakes=9 furniture=71415
    GRASS chunks=669 groundQuads=445362 cutTris=790848

Proof is an A/B pixel diff, not a colour count (a colour count was tried and
wrong twice): the Old Town frame with water off vs on differs on 63,818 px
(3.08%), mean (44,56,45) -- the Mtkvari's own olive. Far-field data untouched
(4,791 asphalt tiles, not ~170).

**Also found:** stock URP/Lit does not read `mesh.colors`, so
`TerrainBuilder.ComputeVertexColors` (the ground-class palette,
`citymap.js:3883-3915`) was discarded and the ground drew flat olive --
`PlayableGround` now uses `TbilisiFly/CityWall`. `LandmarkRenderer`'s 0 draws
were a batch whose Bounds spanned the city, culled against the centroid of all
207 statues (0 -> 40 draws at Old Town). Unity's mid-load unload sweep
destroyed runtime meshes held only by a static field (`RIVER sheet=0` 8 ms
after building) -- `HideFlags.DontUnloadUnusedAsset`, and every renderer now
counts `meshesLost`.

## [OPEN -- BOTH SIDES] Three kits were palette-joined to one node, in the JS build too

`public/models/infra_kit.glb` has 1 node (`models_raw/`: 23), `embankment_kit`
1 (5), `facade_kit` 1 (24) -- an early `tools/compress_models.sh` run before
its `--join false --palette false --prune-attributes false` guard. Every
`kit[name]` lookup fails on BOTH sides: **the shipped Three.js build has no
quays, balustrades, kerbs, crossings, bus stops, portals or lamp columns
either** (`src/infra.js:115` bails on the same lookup), and
`facade_kit`'s missing `win_shop` -- the 21,444 shopfront fallback boxes that
this document earlier called a NON-finding "because the JS does the same" -- is
the same damage. Parity with a damaged asset is still a damaged asset. Unity:
infra + embankment re-converted from `models_raw/` (`furnitureDropped 84615 ->
0`); facade_kit being re-converted. JS: re-run `compress_models.sh` over
`models_raw/{infra_kit,embankment_kit,facade_kit}.glb` -- the user's call,
since the JS build is retired.

**Addendum (facade kit):** the intact `facade_kit.fbx` (24 meshes) is in. Bake:
`SHOPFRONTRENDERER modules=21444 (582 batches, fallbackBox=False)` -- the
21,444 fallback boxes are gone and the `no 'win_shop' submesh` warning with
them. Invariants unchanged (`warehouse=1`, `crane hook 7.9000`, `planehall
parked=35 failed=0`, 11.6 MiB). The JS-side repair is a separate decision row.
