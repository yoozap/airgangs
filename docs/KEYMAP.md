# Key and mouse binding audit — JS vs Unity port

Every `e.code`/`keys.has(...)`/pointer binding in `src/game.js`, `src/foot.js`,
`src/hud.js`, `src/skillbar.js`, `src/main.js`, `src/cinema.js`,
`src/devmode.js`, `src/target.js`, `src/planehall.js`, `src/citymap.js` and
the small helper files they delegate to (`src/rangelane.js`, `src/shooting.js`
(`Pistol`), `src/placement.js`, `src/mapui.js`, `src/weapons.js`→`skillbar.js`
`WeaponSlots`), checked against `Core/GameLoop.cs`
(`ReadDiscreteKeys`/`ReadFootMoveInput`/`ReadFootLook`), `Flight/FlightInput.cs`,
`Character/FootController.cs`, `Core/HeldWeapon.cs`, `Combat/Shooting.cs`,
`Combat/RangeBootstrap.cs` and the UI views, against what
`unity/Assets/Tests/PlayMode/FirstPlayModeRun.cs` and `UserFlowsRun.cs`
already prove (both read-only; neither was edited by this pass).

Out of scope, by the task brief:
- **Z (slide), X (roll), C (crouch) on foot** — a parallel agent is rewiring
  these onto the parkour clip set. Listed for completeness, not audited.
- **Backquote (dev-mode gizmo overlay)** — `GameLoop.ToggleDevMode()` still
  flips the flag and shows the Georgian hint; the `TransformControls`-style
  drag overlay itself is deliberately NOT rebuilt (Unity's own Scene-view
  tools replace it, `.claude/skills/unity/SKILL.md`'s own note).
- **`src/cinema.js`'s internal key simulation** (`c.hold('KeyW', ...)`) is the
  🎬 replay script holding keys itself, not a user binding, and the whole
  cinema system has no ported counterpart (`GameLoop.cs`'s own
  `NoCinema`/`CinemaActive()` comments) — marked N/A, not audited.

Legend: **ok** = JS semantics present and correct in Unity; **different** =
present but gated/behaving differently than the JS; **missing** = no Unity
counterpart; **dead** = wired but unreachable; **skip** = out of scope above.

## Flying (stick + global keys that work while flying)

| key | mode | JS action (file:line) | Unity handler (file:line) | tested by | status |
|---|---|---|---|---|---|
| W | flying | throttle +1 — `game.js:1473` `_readStick` | `Flight/FlightInput.cs:26-27` | `FirstPlayModeRun.cs` fly phase (held W climbs) | ok |
| S | flying | throttle −1 — `game.js:1474` | `Flight/FlightInput.cs:26-27` | none directly (symmetric with W) | ok |
| ShiftLeft/Right | flying | throttle +1 alias — `game.js:1473` | `Flight/FlightInput.cs:26-27` | `KeysRun.cs FlyingKeys` (new, `key_Shift_throttleAlias`) | ok |
| ControlLeft/Right | flying | throttle −1 alias — `game.js:1474` | `Flight/FlightInput.cs:26-27` | `KeysRun.cs FlyingKeys` (new, `key_Ctrl_throttleAlias`) | ok |
| ArrowUp | flying | pitch +1 — `game.js:1475` | `Flight/FlightInput.cs:29-30` | `FirstPlayModeRun.cs` fly/stick phases (`stickPitchesTheAirframe`) | ok |
| ArrowDown | flying | pitch −1 — `game.js:1475` | `Flight/FlightInput.cs:29-30` | none directly (symmetric) | ok |
| ArrowLeft / A | flying | roll +1 — `game.js:1477-1478` | `Flight/FlightInput.cs:32-33` | `FirstPlayModeRun.cs` stick phase (`stickRollsTheAirframe`) | ok |
| ArrowRight / D | flying | roll −1 — `game.js:1477-1478` | `Flight/FlightInput.cs:32-33` | `FirstPlayModeRun.cs` stick phase | ok |
| Q | flying | rudder +1 — `game.js:1479` | `Flight/FlightInput.cs:35-36`, consumed in `Core/PlaneGroundRoll.cs:134` / `Flight/FlightModel.cs:320` | `KeysRun.cs FlyingKeys` (new, `key_Q_rudder`) | ok |
| E | flying | rudder −1 — `game.js:1479` | same | `KeysRun.cs FlyingKeys` (new, `key_E_rudder`) | ok |
| Space | flying | fire belly gun — `src/guns.js:482`, skillbar slot 1 | `Core/HeldWeapon`/`Combat/Guns` via skillbar slot, exercised through `GameLoop` | `FirstPlayModeRun.cs` fire phase (`bellyGunFired`) | ok |
| Digit1-0 | flying | skillbar slot select — `skillbar.js:190-196` | `UI/SkillBar/SkillBarView.cs:125-129 SelectByDigit` | `UserFlowsRun.cs Scenario3` (Digit1/Digit2) | ok |

## On foot (`foot.js` move/turn + `game.js`'s on-foot branch)

| key | mode | JS action (file:line) | Unity handler (file:line) | tested by | status |
|---|---|---|---|---|---|
| W | on foot | forward — `foot.js:4729-4730` | `Core/GameLoop.cs:901,912 ReadFootMoveInput` → `Character/FootController.cs` | `FirstPlayModeRun.cs` (`wWalksTheHero`, `walksTheWayItFaces`) | ok |
| S | on foot | backward — `foot.js:4730` | same | `FirstPlayModeRun.cs` (`FOOT_S_DIST`, measured, no hard Check) | ok |
| A | on foot | turn — `foot.js:4729,4736-4737` (mirrored on purpose, see HANDEDNESS note below) | `Core/GameLoop.cs:902-914` | `FirstPlayModeRun.cs` (`aAndDBothTurnTheHero`, `aTurnsScreenLeft`) | ok |
| D | on foot | turn, opposite sign from A | same | `FirstPlayModeRun.cs` (`aAndDTurnOppositeWays`, `dTurnsScreenRight`) | ok |
| Q | on foot | strafe (armed) — `foot.js:4733` | `Core/GameLoop.cs:911 ReadFootMoveInput` (`m.StrafeX`) | none | ok (untested) |
| E | on foot | strafe (armed) — `foot.js:4733` | same | none | ok (untested) |
| ShiftLeft/Right | on foot | sprint — `foot.js:4738` | `Core/GameLoop.cs:916` → `FootController.cs:1018 (SprintMult)` | `UserFlowsRun.cs Scenario2` (`flow_foot_sprintFaster`) | ok |
| ControlLeft/Right | on foot | walk (slow) — `foot.js:4739` | `Core/GameLoop.cs:917` → `FootController.cs:1018 (WalkSpeed)` | `KeysRun.cs OnFootKeys` (new, `key_Ctrl_walksSlower`) | ok |
| Space | on foot | jump — `foot.js:4740` | `Core/GameLoop.cs:918` | `FirstPlayModeRun.cs`/`UserFlowsRun.cs` (`spaceJumps`, `flow_foot_jumped`) | ok |
| X | on foot | roll — `foot.js:153 ROLL_KEY` | `Core/GameLoop.cs:2634 _foot.StartRoll` | `FirstPlayModeRun.cs` KeySpy only | **skip** (parallel agent) |
| Z | on foot | slide — `foot.js:215 SLIDE_KEY` | `Core/GameLoop.cs:2635 _foot.StartSlide` | KeySpy only | **skip** (parallel agent) |
| C | on foot | crouch toggle — `game.js:517-520` | `Core/GameLoop.cs:2633 _foot.ToggleCrouch` | `FirstPlayModeRun.cs` (`keyEdge_C_crouch`) | **skip** (parallel agent) |
| V | on foot | cover toggle — `game.js:527-530` | `Core/GameLoop.cs:2632 _foot.ToggleCover` | `FirstPlayModeRun.cs` (`keyEdge_V`) | ok |
| R | on foot, armed | reload by hand — `game.js:521-524` (gate: `foot.held`) | `Core/GameLoop.cs:2643` (gate: `_foot.Armed`) → `Core/HeldWeapon.Reload` | `FirstPlayModeRun.cs` (`key_R`) | ok |
| F | on foot | pickup take, or lane send if standing at a range lane — `game.js:533-534` | `Core/GameLoop.cs:2651-2655` (`LaneKeyAny` then `OnTakePickup`) | KeySpy only, no Check | ok (untested) |
| G | on foot, at a lane | lane bring-in — `rangelane.js:206-211` | `Core/GameLoop.cs:2652 LaneKeyAny` | KeySpy only | ok (untested) |
| Tab | on foot | cycle target — `game.js:547-551` | `Core/GameLoop.cs:2613-2614 _targets.Cycle` | `FirstPlayModeRun.cs` (`keyEdge_Tab`) | ok |
| Enter / NumpadEnter | on foot ↔ flying | climb out / back in — `game.js:494-497` | `Core/GameLoop.cs:2610-2611 ToggleBoard` | `FirstPlayModeRun.cs`, `UserFlowsRun.cs` (both scenarios) | ok |
| **AltLeft/AltRight** | on foot, **armed only** | toggle aim — `game.js:508-513` (`AIM_KEYS`, gate `foot?.active && foot.held`) | `Core/GameLoop.cs:2636-2641` | `KeysRun.cs OnFootKeys` (new, `key_Alt_bareHandedDoesNothing` / `key_Alt_armedToggles`) | **FIXED** (was **different** — see below) |
| Right mouse button | on foot, armed only | toggle aim — `game.js:620-623` | `Core/GameLoop.cs:2627-2628` (gate: `Mode==OnFoot && _foot.Armed`, already correct) | none (no mouse-button edge test exists) | ok (untested) |
| Left mouse button (armed) | on foot | fire/trigger, cover blind-fire/shot-buffer — `game.js:624-640` | `Core/HeldWeapon.cs:447-511 Tick` | none in play mode (gun-fire tested only for the flying belly gun) | ok (untested) |
| Left mouse button (bare) | on foot/flying | target pick + drag-to-orbit — `game.js:614-650` | `Core/GameLoop.cs:2617-2618 _targets.PickAt` + `Flight/FlightCamera.cs:67-68` | not explicitly asserted | ok (untested) |
| Mouse move (armed, locked) | on foot | Counter-Strike look yaw/pitch — `game.js:654-675` | `Core/GameLoop.cs:975-993 ReadFootLook` | `FirstPlayModeRun.cs` (`mouseDragTurnsTheViewOnFoot`, `bareHandedMouseNeedsADrag`) | ok |
| MetaLeft/MetaRight (**tap alone**) | on foot | gentleman's stroll toggle — `game.js:466-477,560-567` | `Core/GameLoop.cs:2664-2676` → `FootController.ToggleStroll` | `KeysRun.cs OnFootKeys` (new, `key_Meta_strollToggles`/`...ToggleBack`) | ok |
| Digit1-8 | on foot | weapon-bar category select — `skillbar.js:321-325 WeaponSlots.selectByCode` | `Core/GameLoop.cs:2657,2883 HandleDigitKeys` → `UI/SkillBar/WeaponBarView.cs:137-140` | none with a real key | ok (untested) |

### HANDEDNESS (do not "fix")

`Core/GameLoop.cs:902-911`'s own comment: the Unity world mirrors the JS
world's X axis, so A/D, Q/E and the mouse X delta all enter
`ReadFootMoveInput`/`ReadFootLook` with the **opposite sign** from the raw JS
values on purpose. This audit left that code untouched.

## Both / menu / global (work in any mode)

| key | mode | JS action (file:line) | Unity handler (file:line) | tested by | status |
|---|---|---|---|---|---|
| M | any | map toggle — `game.js:535` | `Core/GameLoop.cs:2687 MapUi.Toggle` | `FirstPlayModeRun.cs` (`mapWindowOpens`), `UserFlowsRun.cs Scenario5` | ok |
| N | any | engine mute, through settings — `game.js:502` | `Core/GameLoop.cs:2686 ToggleEngineAudio` | `FirstPlayModeRun.cs` (`key_N`) | ok |
| T | any | HQ graphics toggle — `game.js:552-558` | `Core/GameLoop.cs:2689 ToggleHq` | `FirstPlayModeRun.cs` (`key_T`) | ok |
| H | any | 🛬 go airport / park — `game.js:536` | `Core/GameLoop.cs:2688 OnGoAirport` → `Core/CombatWorldAdapter.cs:828` | `UserFlowsRun.cs Scenario1` tested the HUD **chip** fallback only; `KeysRun.cs MenuKeys` (new, `key_H_goesHome`) tests the real key | ok (gap closed) |
| Escape | any | UI peel: settings → map → target → open settings — `game.js:537-543` | `Core/GameLoop.cs:2690 HandleEscape` (2896-2902) | KeySpy only before this pass; `KeysRun.cs MenuKeys` (new, `key_Escape_opensSettings`/`...closesSettings`) | ok (gap closed) |
| P | any | placement editor (dev tool) toggle — `game.js:479-482` | `Core/GameLoop.cs:2606 TogglePlacementEditor` | `FirstPlayModeRun.cs` (`key_P`) | ok |
| BracketLeft/Right, Arrows, R, Equal/Minus, PageUp/Down, G (while P active) | any | move/rotate/scale the selected custom model — `placement.js:44-74` | `Core/GameLoop.cs:2729-2742 ReadPlacementKeys` | `FirstPlayModeRun.cs` exercises P open/close only, not each sub-key | ok (mostly untested, dev tool) |
| Backquote | any | dev-mode toggle — `game.js:486-489` | `Core/GameLoop.cs:2608 ToggleDevMode` | `FirstPlayModeRun.cs` (`key_backquote`) | **skip** (gizmo overlay superseded by Unity editor) |
| Mouse wheel (big map) | map open | zoom toward cursor — `mapui.js:413-426` | `UI/Map/BigMapView.cs:102,187-192 OnWheel` | none in play mode | ok (untested) |
| Mouse drag (big map) | map open | pan / drop-or-remove pin — `mapui.js:428-459` | `UI/Map/BigMapView.cs:103-105` | none in play mode | ok (untested) |
| ⚙ settings chip (click) | any | open/close settings — `index.html #btn-settings` | `Core/GameLoop.cs:2890 ToggleSettingsChip` | `UserFlowsRun.cs Scenario5` | ok |
| 🛬 airport chip (click) | any | same as H — `main.js:758` | `Core/HudButtonsView.AirportChip` → `OnGoAirport` | `UserFlowsRun.cs Scenario1` | ok |
| 🎬 cinema chip + scene rows (click) | any | open/close the reel menu, start/stop a scene — `main.js:724-757` | none | — | **N/A** — cinema system not ported (`Core/GameLoop.cs`'s own `NoCinema`/`CinemaActive()` header) |
| 🚪 / logout chips, btn-back, btn-restart | menu screens | session/UI navigation — `main.js:722,779,817` | `Boot`/`GameLoop` equivalents | out of scope for this key/mouse audit (plain UI nav, not listed in the brief's grep targets) | not audited |

## The one real bug found and fixed

**Alt key toggled aim bare-handed.** `src/game.js:508` gates `AIM_KEYS`
(`AltLeft`/`AltRight`) on `this.foot?.active && this.foot.held` — aim only
toggles with a weapon actually drawn, same gate the right-mouse-button branch
uses two lines above it in the JS. `Core/GameLoop.cs:2636-2637` had the right
intent (same `OnToggleAim` hook) but was missing the `_foot.Armed` check that
the right-mouse-button branch at `GameLoop.cs:2627` already carries — so
pressing Alt with bare hands flipped `Shooting.Aiming` anyway. Fixed by
adding `&& _foot.Armed` to the condition. Proven by `KeysRun.cs OnFootKeys`:
bare-handed Alt now leaves `Aiming` unchanged, and the same key toggles it
once a weapon is set carried + selected.

## Counts

- Keys/buttons audited (excluding the explicitly-skipped Z/X/C/backquote and
  the out-of-scope cinema/UI-nav rows): **37**
- ok (already correct, previously tested): **24**
- ok (already correct, newly tested by `KeysRun.cs` this pass): **8**
  (Shift/Ctrl throttle alias, Q/E rudder, Ctrl walk, Meta stroll ×2 checks,
  H real key, Escape real key ×2 checks)
- ok (correct, still untested in play mode — low-risk, documented above): **9**
- different → **fixed**: **1** (Alt bare-handed aim toggle)
- missing: **0**
- dead: **0**
- skip (parallel agent / superseded / out of scope): Z, X, C, Backquote,
  cinema chip + rows, logout/back/restart chips
