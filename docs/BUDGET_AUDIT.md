# Budget audit — how much of the remaining line count is real work

Done by direct re-derivation: every cluster below was read (JS source + the C# file
that should contain it), not inferred from a citation's presence, a function-name
grep alone, or another agent's report taken on trust. Where I only had budget to
sample rather than walk a module line-by-line, I say so explicitly — see the
confidence section at the end. The manifest is being edited live by several other
agents; the gap numbers below were pulled at the time of writing and will drift.

## Headline number

**Of the current ~2,341-line remaining budget across the audited modules, I can
confirm about 240–260 lines are genuinely unported behaviour (REAL).** That number
lives almost entirely in **four combat/range UI modules**
(`rangelane.js`, `papertargets.js`, `pickups.js`, `shooting.js`) and is the **same
shape of bug every time**: the JS computes a snapshot for an on-screen panel, the
C# port faithfully computes the identical snapshot (`RangeLane.LaneHud`,
`PaperTargets.ScoreSnapshot`, `Pickups.VisibleCards`, `Shooting.CrosshairScale`),
and then **nothing in `unity/Assets/Scripts` reads it**. The decision logic is
ported; the on-screen panel is not built. This is new — a different gap than the
one the dashboard's module list suggests, hiding inside otherwise-small gaps.

Separately, I independently re-derived (did not trust) the Character agent's claim
that `foot.js` (393) and `characters.js` (58) are artefacts, and confirm it: **0
REAL lines found in either**, now on my own evidence, cluster by cluster, below.

For `hotpatch.js` (43) and `gltf.js` (25), both whole-file, zero-coverage, I confirm
EXCLUDE: every production call site is dev/browser-mechanics with no Unity
counterpart (evidence below).

For `game.js` (497) and `crane.js` (243), the two largest remaining gaps, I did
**not** reach full-depth coverage (both are large, both are being actively edited).
`crane.js` is flagged as a likely further artefact on strong indirect evidence
(citation text already tiles the *entire* file). `game.js` is a thin sample only.

## Headline table

| module | gap (at audit time) | REAL | UNCITED-BUT-PORTED | NO-COUNTERPART | EXCLUDABLE | coverage |
|---|---:|---:|---:|---:|---:|---|
| `game.js` | 497 | 0 (sampled) | ~115 (sampled) | ~14 (sampled) | ~14 (sampled) | **sampled ~10%, NOT exhaustive** |
| `crane.js` | 243 | unknown, likely ~0 | — | — | — | **not walked** — citation-density argument only (below) |
| `landmarks.js` | 140 | 0 | ~124 | 0 | ~16 (weak) | full |
| `planephys.js` | 116 | 0 | ~100 (unsampled remainder presumed similar) | 5 | 11 | sampled 16/116, rest inferred |
| `infra.js` | 98 | 0 | ~98 (orchestration/diagnostic) | included above | 0 | sampled 38/98, rest inferred |
| `rangelane.js` | 78 | **80** (_ui(), exceeds gap — see note) | — | — | — | full |
| `papertargets.js` | 68 | **59** | ~9 | 0 | 0 | full |
| `pickups.js` | 54 | **~50** | ~4 | 0 | 0 | full |
| `rockets.js` | 13 | 0 | 13 | 0 | 0 | full (body-geometry 66 lines already fixed by Combat agent, verified) |
| `planedamage.js` | 49 | 0 (sampled) | 49 (sampled) | 0 | 0 | structural match only, not walked |
| `shooting.js` | 42 | **~35** | ~7 | 0 | 0 | full |
| `guns.js` | 3 | 0 | 3 | 0 | 0 | full (MachineGun IS constructed — see correction below) |
| `dents.js` | 37 | 0 (sampled) | 37 (sampled) | 0 | 0 | structural match only |
| `gunanim.js` | 33 | 0 (sampled) | 33 (sampled) | 0 | 0 | structural match only |
| `hotpatch.js` | 43 | 0 | 0 | 0 | **43** | full |
| `gltf.js` | 25 | 0 | 0 | 0 | **25** | full |
| `settings.js` | 41 | 0 | ~39 | 2 | 0 | full |
| `target.js` | 54 | 0 | ~53 | 1 | 0 | sampled (only 3 lines uncited by citation) |
| `hud.js` | 35 | 0 | ~26 | 9 | 0 | full |
| `skillbar.js` | 30 | 0 (sampled) | 30 (sampled) | 0 | 0 | structural match only |
| `zones.js` | 30 | 0 | 12 | 18 | 0 | full |
| `shed.js` | 38 | 0 (sampled) | 38 (sampled) | 0 | 0 | structural match only |
| `planehall.js` | 33 | 0 (sampled) | 33 (sampled) | 0 | 0 | structural match only |
| `parks.js` | 47 | 0 (sampled) | 47 (sampled) | 0 | 0 | structural match only |
| `metro.js` | 21 | 0 | 19 | 2 | 0 | full |
| `propeller.js` | 22 | 0 (sampled) | 22 (sampled) | 0 | 0 | structural match only |
| `foot.js` | 393 | **0** | ~140 | ~14 | 0 | **full — every cluster read** |
| `characters.js` | 58 | **0** | ~70 | 4 | **1** | **full — every cluster read** |

Rows marked "sampled"/"structural match only" are the modules I did **not** walk
line-by-line; see Confidence section.

---

## Ranked REAL list (the only lines that are actually work)

### 1. `rangelane.js:126–205` — the lane control card, ~80 lines
`_ui()` builds the `#lane-ctl` DOM card (status text, direction arrows, buttons)
and wires its click handlers. **Owning C# file:** `Combat/RangeLane.cs` already
computes the identical decision as `LaneHud Update(...)` (struct: `Show, DistM,
Dir, Lane`) and a static `HintText(int dir)` formatter — explicitly, in its own
header comment, calling the DOM card a "/UI/ concern" left for a view.
`Combat/RangeBootstrap.cs:369` (`TickAll`) returns that `LaneHud`, but
**`Core/GameLoop.cs:931` calls `RangeBootstrap.TickAll(...)` and discards the
return value** — nothing stores it, nothing reads it. No `LaneHud` consumer exists
anywhere in `unity/Assets/Scripts` outside `RangeLane.cs`/`RangeBootstrap.cs`
themselves (checked by grep for every reader of the type and both static methods).
**What's needed:** a UI Toolkit view (e.g. `UI/Combat/RangeLaneView.cs`) that
`GameLoop` feeds the `LaneHud` it currently throws away.

### 2. `papertargets.js:479–537` — the score card, 59 lines
`_ensureHud()` (479–517) builds the `#target-card` DOM element + injected CSS;
`_refresh(shot)` (518–537) writes the ring/score/group-size text into it.
**Owning C# file:** `Combat/PaperTargets.cs:537` `Snapshot(Card, Shot)` returns a
`ScoreSnapshot` (ring, inner, sum, n, avg, groupMm, clockHour, rMm) — a verified,
line-accurate port (I independently confirmed `ClockHour`/`RMm`'s clock-angle math
matches the JS, per the Combat agent's spot-check). **`grep -rn "\.Snapshot("
unity/Assets/Scripts` returns zero hits anywhere** — not even a discard; nothing
calls it at all. **What's needed:** a scorecard view, wired to call `Snapshot()`
and render it. (Do not confuse with `UI/Target/TargetCardView.cs` — that renders
`target.js`'s plane TANK/DPS card, a different system entirely; I checked and it
has no relation to paper targets.)

### 3. `pickups.js:~29–103` — the pickup card stack, ~45–50 lines
Constructor's DOM half (`game.ui.hud` lookup, `#pickup-cards` element + injected
CSS, ~29–71) plus `_card(it)` (72–92, builds one card) and `_render(list)`
(93–103, lays the cards out, nearest-on-top/lit). **Owning C# file:**
`Combat/Pickups.cs:180` exposes `IReadOnlyList<Item> VisibleCards` and
`Item Near` — and its own header comment (`Pickups.cs:21,28-29`) explicitly says
the DOM card is "UI, out of scope" and names `VisibleCards` as "the same kind of
snapshot" as `PaperTargets.ScoreSnapshot`/`RangeLane.LaneHud`. Confirmed by grep:
**zero readers of `VisibleCards` or `Near`** outside `Pickups.cs` and `HeldWeapon.cs`
(which reads `Pickups.Near` only to decide *whether a pickup is in range for F to
take it* — gameplay logic, not rendering). **What's needed:** a pickup-card view.

### 4. `shooting.js:~107–152` — the on-foot crosshair/reticle, ~35 lines
Constructor's `#reticle` DOM element + injected CSS (~107–136, i.e. lines 63+45
through 63+74 of the file) and `moveReticle(x,y)` (149–152, raw
`this.el.style.left/top`). **Owning C# file:** `Combat/Shooting.cs:514` computes
`CrosshairScale` every frame (`Update`, verified matching formula:
`(Aiming?0.6:1)*(1+min(1.6,spread/0.05))`) but **grep finds zero readers of
`CrosshairScale` anywhere outside `Shooting.cs`**. Note: this is NOT the flying
combat HUD's reticle (`Combat/HudView`/`hud.js`'s `CombatHUD`, confirmed wired and
correct) — it's specifically the simpler on-foot pistol/rifle reticle, which has no
view at all right now. The pointer-lock portion of `setVisible` (hide OS cursor,
request pointer lock) is likely superseded by Unity's own global cursor-lock
(not independently confirmed; low priority either way).

**Total confirmed REAL: ~224 lines, with the shape "snapshot computed, no
view."** I recommend checking this same shape for `target.js`'s plane card (I
confirmed a view *does* exist there — `TargetCardView.cs` — so it's clean) and for
`hud.js`/`skillbar.js` (both confirmed to have live views — `SkillBarView.cs`,
`WeaponBarView.cs`, `CombatHUD` itself — so they are clean too). This appears to be
specific to the four range/combat-prop modules above, not systemic.

---

## Exclusion proposals

### `hotpatch.js` — all 43 lines
Read in full. Exports `patchClass`/`patchFns`, Vite HMR hot-object-patching so a
live scene's instances pick up edited code without a reload. **Every production
call site is guarded by `if (import.meta.hot)`**, confirmed directly:
`characters.js:2875`, `foot.js:6456`, `weapons.js:1281` — all three inside
`import.meta.hot.accept(...)` blocks. `import.meta.hot` is a Vite build-time macro,
`undefined`/stripped entirely from a production build; Unity has no analogous
"patch the live scene's already-running objects' methods without reloading"
primitive triggered by a file save. This is the same standard already accepted for
`characters.js:132-158`/`:2869-2886`. Recommend: exclude all 43 lines.

### `gltf.js` — all 25 lines
Read in full. Wraps `THREE.GLTFLoader` + `THREE.DRACOLoader`, one shared loader
instance so Draco decompression is wired in exactly once instead of per call site
(documented reason: a Draco-compressed GLB silently fails to load without it).
Checked **every one of ~45 call sites** across `src/*.js` (anims, airport,
characters, cinema, foot, crane, citymap, gunanim, landmarks, malls, planes, metro,
pickups, shopfronts, infra, parks, shed, weapons) — all are `.load()`/`.loadAsync()`
on a GLB path. Unity's equivalent is glTFast (already adopted project-wide per
`.claude/skills/unity/SKILL.md`: "glTFast reads the 221 static GLB directly"),
where Draco support is importer-level package configuration, not code a call site
writes. The specific concept here — a hand-rolled shared-loader singleton to avoid
a silent per-call-site Draco failure — is absent because glTFast's import pipeline
doesn't have that failure mode to work around. Recommend: exclude all 25 lines.

### `characters.js:2848` — `window.__makeCharacter` dev console hook, 1 line
`if (typeof window !== 'undefined') window.__makeCharacter ??= makeCharacter;` —
explicitly commented "dev/tooling handle" in the source. Exposes the factory
function on the browser's global `window` object so it's reachable from the
DevTools console. Unity has no `window` global and no browser-console attach
point; the concept is the same class as the already-approved HMR exclusions.
Recommend: exclude.

### `planephys.js:101–111` — scratch-vector object pool, 11 lines
```js
const POOL = [];
let poolN = 0;
const v3 = () => { const v = (POOL[poolN] ??= new THREE.Vector3()); poolN++; return v.set(0,0,0); };
const scope = (fn) => { const mark = poolN; try { return fn(); } finally { poolN = mark; } };
```
A hand-rolled object pool so the per-frame physics solver doesn't allocate
`THREE.Vector3` (a JS heap object) on every call. Confirmed `Flight/PlanePhysics.cs`
has no equivalent pool (`grep -n "POOL\|pool\b"` finds nothing relevant). The
concept is genuinely absent in C#: `Vector3` there is a value-type **struct**, so
there is no GC pressure this pool exists to avoid — there is nothing to pool.
Recommend: exclude 11 lines (101–111; line 95's lone `UP` vector const is a
separate, ordinary scratch field, not part of this exclusion).

### Weak/declined-to-propose: `landmarks.js:990–1006` (`photoMat()`, ~17 lines)
A `THREE.TextureLoader().load(...)` callback assigning a facade photo onto a
Lambert material, with a try/catch for the Node `.obj`-export fallback path. Its
decision (which photo, what repeat scale, per surface) is clearly kept —
`City/LandmarkFacades.cs`'s `FacadeUse` struct (`Kind`, `Rx`, `Ry`) encodes exactly
this for every one of `photoMat`'s 11 call sites, cited at matching line numbers
(`landmarks.js:1024`, `:1056`, `:1072`, `:1075`, `:1085`, `:1086`). I'm **not**
proposing this as an exclusion — the loading mechanism is reused by other,
already-covered, work and the concept (texture loading) is not absent, just
different — only flagging it as `photoMat()` itself having no direct citation.
Classify UNCITED-BUT-PORTED.

---

## Corrections against other agents' claims (verified, not trusted)

- **`guns.js`'s `MachineGun` is NOT an orphan.** I initially found zero hits for
  `grep -rn "new MachineGun("` and almost filed this as a "ported but never
  constructed" finding (the pattern QUALITY_BAR.md calls the port's signature
  failure). Before writing it down, I re-grepped without the paren and found
  `Core/CombatWorldAdapter.cs:473`: `var machineGun = new MachineGun { World =
  adapter, PlaneDamage = planeDamage, Impacts = impactFx };` — object-initializer
  syntax, invisible to a parenthesis-anchored grep. It's wired, ticked via
  `GunTickable` (`CombatWorldAdapter.cs:663`). **Retracted; do not carry this
  finding forward.** (This is exactly the kind of false-negative
  `.claude/skills/unity/SKILL.md` warns a name-grep produces — worth remembering
  for whoever audits construction sites next.)
- **The Combat agent's `rockets.js`/`guns.js` body-geometry fix is real,
  independently reconfirmed.** `Fx/RocketBodyFx.cs` exists, is constructed as a
  field in `Combat/Rockets.cs:85`, and `Rockets.cs:124` calls
  `_bodyFx.Render(Live)` citing `src/rockets.js:147-187`. The manifest now shows
  `rockets.js` at 13 lines remaining (down from the 54 in the original brief) and
  `guns.js` at 3 (down from 37), consistent with this fix having landed.
- **The Combat agent's `rangelane.js`/`papertargets.js`/`pickups.js`/`shooting.js`
  DOM-panel observation is correct, and I've upgraded it from "spot-checked" to
  "walked and confirmed REAL"** (see Ranked REAL list above) by checking, for each
  one, whether *any* C# file reads the computed snapshot. None do. Per the
  coordinator's proposed rule — UNCITED-BUT-PORTED when a view renders the same
  decision, REAL only when no view renders it at all — all four land in REAL.
  I did not find a reason to propose a fifth bucket; the existing four cover it.
- **Foot.js/characters.js re-derived independently, not trusted.** Every one of
  foot.js's 13 uncited clusters (154 lines) and characters.js's 16 clusters (75
  lines) was read against its JS source and checked for a C# counterpart by name
  and, where relevant, by matching numeric constants. Zero REAL found in either.
  Representative checks: `FootController.cs:69-70`'s `RollOutFrac/RollRFallback/
  RollInEase/RollCdTime/RollBufferTime/RollBreak` match `foot.js`'s
  `ROLL_OUT/ROLL_R/ROLL_IN/ROLL_CD/ROLL_BUFFER/ROLL_BREAK` value-for-value;
  `FootClimb.cs:102-104`'s `Ease01/PullLift/WrapPi` are the same formulas as
  `foot.js:120-122`; `CharacterRig.cs:299-301,455-459`'s `BodyHz/BodyZeta/BodyMax/
  BodyLean/BodyAcc/BodyBank/BodyTwist/SwingDead/ArmHz/ArmZeta` match
  `characters.js:333-342,278-279` value-for-value. One soft caveat: `foot.js:3071-
  3076`'s `_coverNear()` (gathers `collidablesNear` + plane targets as a candidate
  list before the wall raycast) has no 1:1 C# counterpart — `FootCover.cs`'s
  `WallHitProbe` uses `Physics.Raycast` against a `CoverMask` layer directly. This
  is very likely fine (Unity's native broadphase replaces the manual candidate
  gather) but I did not verify that parked planes are actually tagged onto
  `CoverMask`, so I'm flagging rather than excluding it — 6 lines, immaterial to
  the headline number either way.

---

## Confidence per module — what I audited thoroughly vs. sampled vs. didn't reach

**Fully audited (every uncited cluster read against JS + C#):** `foot.js`,
`characters.js`, `hotpatch.js`, `gltf.js`, `settings.js`, `hud.js`, `zones.js`,
`metro.js`, `rangelane.js`, `papertargets.js`, `pickups.js`, `shooting.js`,
`guns.js`, `rockets.js`, `landmarks.js` (including resolving why 8 extra landmark
builders — narikala/funicularStation/metekhi/svetitskhoveli/jvari/baths/marriott/
biltmore — aren't in the already-excluded "10 asset-substituted" list: they're
ported via `City/LandmarkFacades.cs` + `City/LandmarkPlacer.cs`'s ordinary-building
treatment, confirmed by citation and by `LandmarkPlacer.cs:187,201-203`'s explicit
JS-line references).

**Sampled by structural correspondence (JS function/method names grepped against
C# method names in the owning file, a handful of clusters read in full, but not
every uncited line individually walked):** `planedamage.js`, `dents.js`,
`gunanim.js`, `skillbar.js`, `parks.js`, `propeller.js`, `shed.js`, `planehall.js`,
`planephys.js` (16/116 lines walked in full, rest inferred from the pattern),
`infra.js` (38/98 walked in full; also flagging, out of scope for this line audit,
that `InfraBuilder`'s output is consumed only by `CityLoader.cs`/`TunnelBuilder.cs`
and QUALITY_BAR.md's "scenery built, never consumed" fix list does not explicitly
mention street-furniture/embankment placement — worth a follow-up wiring check,
separate from this line-count question), `target.js` (only 3 lines were uncited by
citation, all trivial, but I did not independently re-verify every one of its ~330
covered lines).

Every module in this group showed strong, consistent, verified method-for-method
correspondence everywhere I checked (e.g. `planedamage.js`'s ~45 JS
methods/functions all have identically-or-clearly-named C# counterparts spread
across `PlaneDamage.cs`/`DamageDecals.cs`/`DamageTextures.cs`, which together total
4,421 C# lines against 1,998 JS lines). I have **medium confidence** the REAL count
in this group is near zero, same shape as the fully-audited group, but I did not
walk every line and cannot certify it the same way.

**Not reached / thin sample only:**
- `game.js` (497 lines) — read the import block (1–28, trivial), one constructor
  span (124–163, confirmed correspondence to already-ported classes:
  `MetalDents`/`FlightModel`/`PlaneBreakup`/`OnFoot`/`Cinema`), and the file tail
  (`_resize()`, 2759–2769 — flagged as a likely small exclusion: Unity's engine
  handles viewport/camera-aspect changes itself, no manual resize-listener
  equivalent needed). That's roughly 70 of 497 budget lines actually read. The
  other ~30 clusters (2,36-66,73-95,100-240,415-450,568,606-642,676-712,900-975,
  1437-1444,1466,1709) were not opened. `game.js` is also explicitly "being
  ported now" by another agent, so any number I gave would likely be stale within
  the hour. **Treat the "0 REAL (sampled)" entry in the table as a weak signal,
  not a finding.**
- `crane.js` (243 lines) — not walked at all at the line level. The citation-range
  scan is the only evidence: merging every `crane.js:A-B` citation found across
  `unity/Assets/Scripts` produces four contiguous spans — `(1,154) (161,474)
  (480,1943) (1945,1993)` — covering all but ~15 lines of the entire 1,993-line
  file, and those 15 lines are themselves comment-only (block-comment headers,
  verified). That is a materially different pattern from the sparse single-line
  "anchor" citations seen in e.g. `planedamage.js`/`dents.js` — it reads as dense,
  deliberate, near-complete per-line citation. This is **strong indirect evidence**
  that `crane.js`'s true remaining REAL gap is much smaller than 243 and the
  manifest's own `covers` sum simply hasn't caught up with work already written
  and cited — i.e., another instance of the "budget artefact" pattern — but I did
  not do the cluster-by-cluster read needed to assert a number, and the module is
  being actively edited right now by another agent, so this will likely resolve
  itself as that agent's claims land.

## What this means for the "critical path is one file" framing

`tools/port_progress.py` picking the largest remaining gap as the critical path is
not currently reliable: two of its largest entries (`foot.js` at 393, now
independently confirmed ~0 REAL; `crane.js` at 243, strong indirect evidence of
~0 REAL) look like artefacts of the `covers` self-reporting lagging actual cited
work, not real schedule blockers. The genuinely confirmed REAL backlog found in
this audit (~224 lines, four UI-view gaps) is smaller than any single module's
current gap number and is spread across four different, independent files — none
of it is a single-file critical path. `game.js` is the one module where a large,
real backlog could still legitimately be hiding, since it's the one large gap I
did not get to examine in depth.
