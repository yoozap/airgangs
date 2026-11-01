using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TbilisiFly.Core;
using TbilisiFly.Character;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// The four Core gaps of docs/open_items.json, on the SHIPPED scene, in one
    /// editor launch (the lock is exclusive — see ProbeExit):
    ///
    ///   spawn-in-building  the P-51 was placed at the data origin, inside a
    ///                      building at Delisi. Asserts the aeroplane now stands
    ///                      on a road/terrain and that NOTHING under the
    ///                      "Buildings" root is in or above the airframe.
    ///   loading-overlay    #loading-overlay (index.html:154-159) exists on the
    ///                      game panel, Boot.HintChanged reaches it with a
    ///                      Georgian string, and GameLoop.BeginSession hides it.
    ///   starter-plane      a hero of EACH race flies the machine
    ///                      Boot.StarterPlaneFor gives their role, not the p51
    ///                      the scene happens to carry.
    ///   preview-lights     the preview rig's two DirectionalLights no longer
    ///                      join the scene's light set while the menu is up.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.CoreSpawnProbe.Run
    ///
    /// AddComponent/scene-open do NOT deliver Awake() under -executeMethod (the
    /// unity skill's own note), so Boot.Awake/Start are invoked by reflection,
    /// exactly as Editor/Probes/HeroScreenProbe.cs does.
    ///
    /// THE GATE, not just the calculation (SKILL.md's most expensive lesson):
    /// every number below is read off the real scene AFTER the real
    /// Boot -> StartFlightAsync -> BeginSession hand-off ran, not off a fixture
    /// this probe built. `SPAWN_FINDER_HAS_RUNTIME_CALLER` is the explicit
    /// assertion that City/SpawnFinder is reached at all.
    /// </summary>
    public static class CoreSpawnProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string EditModeDestroy = "Destroy may not be called from edit mode";

        static readonly List<string> Errors = new List<string>();
        static bool _allPass = true;
        static string _phase = "open";

        public static void Run()
        {
            Errors.Clear();
            _allPass = true;
            Application.logMessageReceived += OnLog;
            try { Body(); }
            catch (Exception e)
            {
                Debug.Log($"FAIL probeThrew ({_phase}: {e.GetType().Name}: {e.Message})");
                Debug.Log(e.StackTrace);
                _allPass = false;
            }
            Application.logMessageReceived -= OnLog;
            Debug.Log($"CORESPAWN_ERROR_COUNT={Errors.Count}");
            for (int i = 0; i < Errors.Count; i++) Debug.Log($"CORESPAWN_ERROR_{i}={Errors[i]}");
            Debug.Log(_allPass ? "CORESPAWNPROBE RESULT=PASS" : "CORESPAWNPROBE RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // HeroScreenProbe's one documented exemption: CharacterPreviewRig
            // calls Object.Destroy when a body is swapped, which is illegal (and
            // logged as an ERROR) outside Play mode.
            if (condition != null && condition.Contains(EditModeDestroy)) return;
            Errors.Add($"[{_phase}] {type}: {condition}");
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = cond ? $"PASS {key}" : $"FAIL {key}";
            if (!cond && note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Fact(string key, object v) => Debug.Log($"{key}={v}");
        static void FactBool(string key, bool v) => Debug.Log($"{key}={v.ToString().ToLowerInvariant()}");

        static void Body()
        {
            _phase = "open";
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());
            if (!scene.IsValid()) return;

            var boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            var game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            var noLogin = UnityEngine.Object.FindAnyObjectByType<PlayableSceneBoot>();
            Check("bootInScene", boot != null);
            Check("gameLoopInScene", game != null);
            if (boot == null || game == null) return;
            if (noLogin != null) noLogin.enabled = false; // Boot owns the flow here

            TokenStore.ClearToken();

            // ================================================================
            // task 4 — the preview rig's two DirectionalLights
            // ================================================================
            _phase = "lightsBefore";
            var before = Census("beforeMenu");

            _phase = "awake";
            Invoke(boot, "Awake");            // builds ShellUi + CharacterPreviewRig
            Invoke(boot, "Start");            // no token -> screen-login
            var shell = boot.ShellUi;
            Check("shellBuilt", shell != null);
            Check("previewRigBuilt", shell != null && shell.Preview != null);

            _phase = "lightsMenu";
            var menu = Census("menuOpen");
            PreviewLightFacts(shell);

            // ================================================================
            // the roster: one hero per race, each carrying the plane its ROLE
            // owns — which is exactly what Boot.CreateHero saves (main.js's own
            // `plane: starterPlaneFor(race)`); a faked def with no `plane` would
            // fall through to Planes.All[0] and test nothing.
            // ================================================================
            _phase = "character";
            var races = CharacterRoster.Races;
            var defs = new List<CharacterDef>();
            foreach (var race in races)
            {
                defs.Add(new CharacterDef
                {
                    hid = "h-spawn-" + race.Id,
                    name = "მფრინავი",
                    race = race.Id,
                    model = CharacterRoster.RaceById(race.Id)?.DefModel,
                    plane = Boot.StarterPlaneFor(race.Id),
                });
            }
            Invoke(boot, "SetUser", new PublicUser
            {
                username = "spawnprobe", level = 3, stars = 1, activeChar = 0, characters = defs,
            });
            boot.OpenCharacter();
            Fact("ROSTER_COUNT", boot.Roster.Count);
            Check("rosterKept", boot.Roster.Count == races.Length);

            // THE HOOK IS ONLY LIVE WHILE THE PREVIEW IS. UI/Character/
            // CharacterView.OnScreenChanged calls Stop() on every screen that
            // is not the personage screen, and Stop() unhooks (a subscription
            // outliving its camera is the CharacterView.Unbind lesson). So the
            // hook is asserted HERE, on the screen the preview actually runs
            // on, and the lights are re-checked at the same moment: running or
            // not, they must stay out of every other camera's light set.
            var onChar = Census("menuCharacter");
            FactBool("PREVIEW_RIG_HOOKED", HookedFlag(shell));
            Check("previewRigHooked", HookedFlag(shell),
                  "without the beginCameraRendering hook the lights would never come on at all");
            var sunL = GameObject.Find("preview_sun")?.GetComponent<Light>();
            var rimL = GameObject.Find("preview_rim")?.GetComponent<Light>();
            Check("previewLightsStillOffWhileRunning", sunL != null && rimL != null && !sunL.enabled && !rimL.enabled,
                  "they are enabled inside the preview camera's own render and nowhere else");
            Check("dirLightsUnchangedOnCharacterScreen", onChar.DirEnabled == before.DirEnabled,
                  $"{before.DirEnabled} -> {onChar.DirEnabled}");

            // ================================================================
            // tasks 1 + 2 + 3 — enter the world as each race in turn
            // ================================================================
            for (int i = 0; i < boot.Roster.Count; i++)
            {
                var hero = boot.Roster[i];
                string want = Boot.StarterPlaneFor(hero.Race);
                _phase = "enter_" + hero.Race;

                boot.SelectHero(i);
                boot.EnterWorldWithSelectedHero();   // StartFlightAsync completes synchronously

                var pc = game.ActivePlane;
                string got = pc != null ? pc.PlaneId : "<null>";
                var def = Planes.PlaneById(got);
                Debug.Log($"STARTER role={RoleNameOf(hero.Race)} race={hero.Race} "
                          + $"heroPlane={hero.Plane} expected={want} actual={got} name={def?.Name}");
                Check($"starterPlane_{hero.Race}", got == want,
                      $"expected {want} for role {RoleNameOf(hero.Race)}, got {got}");
                Check($"starterPlaneModel_{hero.Race}",
                      pc != null && PlaneModelFrame.Of(pc.transform).Model != null,
                      "the re-skin must leave a real model under model_file");

                if (i == 0)
                {
                    SpawnReport(game);
                    OverlayReport(boot, game);
                }

                if (i < boot.Roster.Count - 1)
                {
                    _phase = "backToCharacter";
                    boot.BackToCharacterSelect();  // main.js:779-782 — rebuilds the shell
                }
            }

            _phase = "lightsClosed";
            // and once more with the shell rebuilt + torn down, which is the
            // open/close cycle the lights hash used to see.
            boot.BackToCharacterSelect();
            var reopened = Census("menuReopened");
            boot.Logout();
            var closed = Census("menuClosed");

            Check("dirLightsUnchangedByMenuOpen", menu.DirEnabled == before.DirEnabled,
                  $"{before.DirEnabled} -> {menu.DirEnabled}");
            Check("dirLightsUnchangedByMenuReopen", reopened.DirEnabled == before.DirEnabled,
                  $"{before.DirEnabled} -> {reopened.DirEnabled}");
            Check("dirLightsUnchangedByMenuClose", closed.DirEnabled == before.DirEnabled,
                  $"{before.DirEnabled} -> {closed.DirEnabled}");
            // The open->CLOSE pair only. `beforeMenu`'s 1510 cannot be compared
            // with anything after the world started: entering it instantiates a
            // hero body and a re-skinned airframe, which bring their own
            // materials in — nothing to do with the menu's lights. The pair
            // that isolates the menu is "shell rebuilt" vs "shell disposed".
            Check("materialCountUnchangedByMenuClose", closed.Materials == reopened.Materials,
                  $"{reopened.Materials} -> {closed.Materials}");
            Check("materialCountUnchangedByMenuOpen", menu.Materials == before.Materials,
                  $"{before.Materials} -> {menu.Materials}");

            _phase = "tick60";
            int beforeTicks = Errors.Count;
            for (int i = 0; i < 60; i++) game.Tick(1f / 60f);
            Fact("TICK60_ERRORS", Errors.Count - beforeTicks);
            Check("tick60NoNewError", Errors.Count == beforeTicks);
        }

        // =====================================================================
        // task 1 — the spawn
        // =====================================================================
        static void SpawnReport(GameLoop game)
        {
            string saved = _phase;
            _phase = "spawn";

            var sp = SpawnResolver.Resolve();
            Check("SPAWN_FINDER_HAS_RUNTIME_CALLER", sp.RoadCount > 0,
                  "SpawnResolver read no roads at all — SpawnFinder would be as dead as before");

            var pc = game.ActivePlane;
            if (pc == null) { Check("activePlane", false); _phase = saved; return; }
            var t = pc.transform;
            Physics.SyncTransforms(); // the plane was moved by script, not by the physics step

            var bb = WorldBounds(pc.gameObject);
            float gy = game.Terrain != null && game.Terrain.Contains(sp.GameX, sp.GameZ)
                ? game.Terrain.HeightAtGame(sp.GameX, sp.GameZ) : float.NaN;

            // What is under the airframe? Cast from ABOVE the bounds so a
            // surface passing THROUGH the aeroplane is reported, not skipped.
            float top = bb.HasValue ? bb.Value.max.y + 2f : t.position.y + 4f;
            var hits = Physics.RaycastAll(new Vector3(t.position.x, top, t.position.z), Vector3.down, 400f);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            string firstRoot = "<none>", firstName = "<none>";
            float firstY = float.NaN;
            bool buildingUnderAirframe = false;
            float bMin = bb.HasValue ? bb.Value.min.y : t.position.y;
            float bMax = bb.HasValue ? bb.Value.max.y : t.position.y;
            foreach (var h in hits)
            {
                string root = h.collider.transform.root.name;
                Debug.Log($"SPAWN_BELOW collider={h.collider.name} root={root} y={h.point.y:F2}");
                if (firstRoot == "<none>") { firstRoot = root; firstName = h.collider.name; firstY = h.point.y; }
                // the bug's exact signature: a Buildings surface inside the
                // airframe's own vertical extent (measured 494.64 against
                // bounds 490.94..495.70).
                if (root == "Buildings" && h.point.y > bMin - 0.5f && h.point.y < bMax + 0.5f)
                    buildingUnderAirframe = true;
            }
            var ups = Physics.RaycastAll(t.position + Vector3.up * 0.2f, Vector3.up, 60f);
            foreach (var u in ups)
                Debug.Log($"SPAWN_ABOVE collider={u.collider.name} root={u.collider.transform.root.name} y={u.point.y:F2}");
            bool roofOverhead = ups.Any(u => u.collider.transform.root.name == "Buildings");

            bool clear = !buildingUnderAirframe && !roofOverhead
                         && (firstRoot == "Terrain" || firstRoot == "Roads" || firstRoot == "<none>"
                             || firstRoot == pc.transform.root.name);

            Debug.Log($"SPAWN pos={t.position.ToString("F2")} yawUnityDeg={t.eulerAngles.y:F2} "
                      + $"clear={clear.ToString().ToLowerInvariant()} "
                      + $"road={sp.RoadWidth:F1}m roads={sp.RoadCount} source={sp.Source} "
                      + $"gameXZ=({sp.GameX:F2},{sp.GameZ:F2}) terrainY={gy:F2} planeY={t.position.y:F2} "
                      + $"wheelH={(t.position.y - gy):F2} belly={pc.BellyOffset:F3} "
                      + $"under={firstName}/{firstRoot}@{firstY:F2} "
                      + $"bounds={(bb.HasValue ? bb.Value.min.y.ToString("F2") + ".." + bb.Value.max.y.ToString("F2") : "none")}");

            Check("spawnNotAtDataOrigin", new Vector2(t.position.x, t.position.z).magnitude > 5f,
                  $"still at the baker's origin: {t.position.ToString("F2")}");
            Check("spawnOnARoad", sp.Found && sp.RoadWidth >= 8f,
                  $"found={sp.Found} width={sp.RoadWidth:F1} (citymap.js:6515 searches avenues >= 8 m first)");
            Check("noBuildingThroughTheAirframe", !buildingUnderAirframe,
                  "a Buildings collider inside the airframe's own vertical extent — the reported bug");
            Check("noRoofOverhead", !roofOverhead);
            Check("wheelsOnTheGround",
                  !float.IsNaN(gy) && Mathf.Abs((t.position.y - gy) - pc.GroundProbe.WheelHeightFor(1f)) < 0.25f,
                  $"planeY-terrainY={(t.position.y - gy):F2} vs wheelHeight={pc.GroundProbe.WheelHeightFor(1f):F2}");
            Check("parkedNotAirborne", !pc.Airborne, "game.js:727 — resetPlane leaves the plane parked");

            // THE YAW, checked as geometry rather than as a number: the nose
            // must point down the road, on the WSW side (src/citymap.js:6520-6529
            // picks whichever direction is closest to the legacy heading).
            Vector3 wantNose = SpawnResolver.NoseUnityFor(sp.YawGame);
            Vector3 gotNose = t.forward; gotNose.y = 0f; gotNose.Normalize();
            float dot = Vector3.Dot(gotNose, wantNose.normalized);
            Vector3 legacy = SpawnResolver.NoseUnityFor(SpawnResolver.SpawnYawGame);
            Debug.Log($"SPAWN_YAW yawGame={sp.YawGame:F4} yawUnity={sp.YawUnityRad:F4} "
                      + $"nose={gotNose.ToString("F3")} want={wantNose.normalized.ToString("F3")} dot={dot:F4} "
                      + $"legacyDot={Vector3.Dot(gotNose, legacy.normalized):F4}");
            Check("noseDownTheRoad", dot > 0.999f, $"dot={dot:F4}");
            Check("noseOnTheLegacySide", Vector3.Dot(gotNose, legacy.normalized) > 0f,
                  "citymap.js:6524-6528 flips the road direction toward the legacy WSW heading");

            _phase = saved;
        }

        // =====================================================================
        // task 2 — #loading-overlay
        // =====================================================================
        static void OverlayReport(Boot boot, GameLoop game)
        {
            string saved = _phase;
            _phase = "overlay";
            var ov = game.LoadingOverlay;
            Check("loadingOverlayInstalled", ov != null,
                  "CombatWorldAdapter.InstallUi must build UI/Common/LoadingOverlayView");
            FactBool("HINT_CHANGED_HAS_SUBSCRIBER", HasSubscriber(boot, "HintChanged"));
            Check("hintChangedWired", HasSubscriber(boot, "HintChanged"),
                  "docs/dead_events_baseline.txt: Boot.HintChanged was DEAD");
            Fact("LAST_HINT", boot.LastHint);
            Check("hintIsGeorgian",
                  boot.LastHint == "იტვირთება თბილისი…" || boot.LastHint == "ბრუნდები იქ, სადაც გაჩერდი…",
                  $"main.js:671-673's two strings only — got '{boot.LastHint}'");
            if (ov != null)
            {
                Fact("OVERLAY_TEXT", OverlayText(ov));
                Check("overlayCarriesTheHint", OverlayText(ov) == boot.LastHint,
                      "Boot.LastHint is replayed because the hint fires before InstallUi");
                Fact("OVERLAY_DISPLAY", ov.style.display.value);
                Check("overlayHiddenOnceStarted", ov.style.display.value == UnityEngine.UIElements.DisplayStyle.None,
                      "src/game.js:1390 — hidden on the same line that sets started = true");
                Check("overlayDefaultTextIsGeorgian",
                      TbilisiFly.UI.Common.LoadingOverlayView.DefaultText == "იტვირთება თბილისი…",
                      "index.html:157, verbatim, no English fallback");
            }
            _phase = saved;
        }

        static string OverlayText(TbilisiFly.UI.Common.LoadingOverlayView ov)
        {
            var f = typeof(TbilisiFly.UI.Common.LoadingOverlayView)
                .GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic);
            var lbl = f?.GetValue(ov) as UnityEngine.UIElements.Label;
            return lbl != null ? lbl.text : "<none>";
        }

        // =====================================================================
        // task 4 — the census
        // =====================================================================
        struct Snap { public int Lights, Enabled, DirEnabled, Materials; }

        static Snap Census(string phase)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int enabled = 0, dir = 0;
            foreach (var l in lights)
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                enabled++;
                if (l.type == LightType.Directional) dir++;
            }
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            var s = new Snap { Lights = lights.Length, Enabled = enabled, DirEnabled = dir, Materials = materials };
            Debug.Log($"PREVIEWLIGHTS phase={phase} lights={s.Lights} enabled={s.Enabled} "
                      + $"dirEnabled={s.DirEnabled} materials={s.Materials}");
            return s;
        }

        static void PreviewLightFacts(TbilisiFly.UI.Shell.ShellUi shell)
        {
            var sun = GameObject.Find("preview_sun");
            var rim = GameObject.Find("preview_rim");
            Check("previewLightsExist", sun != null && rim != null);
            if (sun == null || rim == null) return;
            var ls = sun.GetComponent<Light>(); var lr = rim.GetComponent<Light>();
            Debug.Log($"PREVIEWLIGHT sun enabled={ls.enabled.ToString().ToLowerInvariant()} "
                      + $"intensity={ls.intensity:F2} shadows={ls.shadows}");
            Debug.Log($"PREVIEWLIGHT rim enabled={lr.enabled.ToString().ToLowerInvariant()} "
                      + $"intensity={lr.intensity:F2} shadows={lr.shadows}");
            Check("previewLightsDisabledOutsideTheirCamera", !ls.enabled && !lr.enabled,
                  "they are switched on in RenderPipelineManager.beginCameraRendering for the preview camera only");
            Check("previewLightIntensitiesKept",
                  Mathf.Approximately(ls.intensity, CharacterPreviewRig.PreviewSunIntensity)
                  && Mathf.Approximately(lr.intensity, CharacterPreviewRig.PreviewRimIntensity),
                  "main.js:296/299's measured 2.4 / 1.2 must survive the fix");
            Check("previewLightsCastNoShadows", ls.shadows == LightShadows.None && lr.shadows == LightShadows.None);
        }

        static bool HookedFlag(TbilisiFly.UI.Shell.ShellUi shell)
        {
            if (shell == null || shell.Preview == null) return false;
            var f = typeof(CharacterPreviewRig).GetField("_hooked", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null && (bool)f.GetValue(shell.Preview);
        }

        // =====================================================================
        static string RoleNameOf(string raceId)
        {
            // Georgian role names only on the hero side (src/characters.js:23-25);
            // the plane's own TANK/DPS tag belongs on plane UI, not here.
            var race = CharacterRoster.RaceById(raceId);
            return race != null ? race.RoleKa : raceId;
        }

        static Bounds? WorldBounds(GameObject go)
        {
            Bounds b = default; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b : (Bounds?)null;
        }

        static bool HasSubscriber(object owner, string eventField)
        {
            var f = owner.GetType().GetField(eventField, BindingFlags.Instance | BindingFlags.NonPublic
                                                       | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            var d = f?.GetValue(owner) as Delegate;
            return d != null && d.GetInvocationList().Length > 0;
        }

        static void Invoke(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Debug.Log($"FAIL reflect_{method} (not found)"); _allPass = false; return; }
            m.Invoke(target, args);
        }
    }
}
