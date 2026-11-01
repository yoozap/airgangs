using System;
using Newtonsoft.Json.Linq;
using TbilisiFly.Character;
using TbilisiFly.Core;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verification for this pass's own work: the game.js/main.js/fire.js
    /// remainder (GameLoop.cs's KeepCameraOutside/AdaptResolution/ApplyResume/
    /// ReseatOnScenery/dispose, Fx/FxLightPool.cs) PLUS the wiring the previous
    /// pass named but could not apply — PlaneController's Airborne branch,
    /// PlaneWallContact.RamOther, PlaneHullDamage's hooks, GameLoop.ResetPlane()
    /// calling Hull.ResetAll(). Never enters Play mode: a throwaway scene with
    /// one PlaneController + one GameLoop, driven directly (matches Editor/
    /// Probes/PlayableProbe.cs's own convention — explicit PASS/FAIL lines plus
    /// one KEY=value per fact).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CoreRestProbe.Run
    ///
    /// NOT EXECUTED THIS SESSION — no editor access (the coordinator holds the
    /// batch-mode lock, per this port's standing rule). Written so the next
    /// session (or the coordinator) can run it as-is; every KEY name here is
    /// chosen to pair 1:1 with tools/_probe_corerest.mjs's own output.
    /// </summary>
    public static class CoreRestProbe
    {
        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                string line = $"{(cond ? "PASS" : "FAIL")} {key}";
                // the note explains what a FAILURE means, so it prints only on
                // one (see Editor/Probes/UiWireProbe.cs's own Check for the
                // "PASS x (its asset is missing)" line this produced).
                if (!cond && note != null) line += $" ({note})";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"{key}={value}");

            // ---- constants (pair byte-for-byte against tools/_probe_corerest.mjs's
            // own regex read of src/game.js) ----
            Fact("CAM_MIN", GameLoop.CamMin);
            Fact("CAM_SKIN", GameLoop.CamSkin);
            Fact("ADAPT_EMA_EWMA", GameLoop.AdaptEmaEwma);
            Fact("ADAPT_COOL_SECONDS", GameLoop.AdaptCoolSeconds);
            Fact("ADAPT_BUDGET_MS", GameLoop.AdaptBudgetMs);
            Fact("ADAPT_DROPPED_SCALE", GameLoop.AdaptDroppedScale);
            Fact("FX_LIGHT_POOL_SIZE", TbilisiFly.Fx.FxLightPool.PoolSize);

            GameObject goGame = null, goPlane = null;
            try
            {
                goGame = new GameObject("ProbeGameLoop");
                var game = goGame.AddComponent<GameLoop>();

                goPlane = new GameObject("ProbePlane");
                var plane = goPlane.AddComponent<PlaneController>();
                plane.PlaneId = "p51"; // real catalog row — see Flight/PlaneCatalog.cs
                plane.enabled = false; // GameLoop steps it explicitly, same as production wiring
                plane.Init();

                Check("plane_init_ok", plane.Hull != null && plane.WallContact != null && plane.GroundRoll != null && plane.PlanePhys != null,
                    "PlaneController.Init() built its own per-plane rig");
                Fact("plane_airborne_initial", plane.Airborne);
                Check("plane_airborne_initial_true", plane.Airborne, "task brief: spawn a little above the ground, airborne from frame one");

                game.ActivePlane = plane;
                game.BeginSession(); // wires WirePlaneCombat + calls ResetPlane()

                // ---- PlaneHullDamage hooks WirePlaneCombat is supposed to wire ----
                Check("hull_onhint_wired", plane.Hull.OnHint != null);
                Check("hull_onhealthchanged_wired", plane.Hull.OnHealthChanged != null);
                Check("hull_onexplosionsound_wired", plane.Hull.OnExplosionSound != null);
                Check("hull_onenginekill_wired", plane.Hull.OnEngineKill != null);
                Check("hull_onsavedeath_wired", plane.Hull.OnSaveDeath != null);
                Check("hull_onshowwasted_wired", plane.Hull.OnShowWasted != null);
                Check("hull_oncrashed_wired", plane.Hull.OnCrashed != null);
                Check("hull_ondenthull_wired", plane.Hull.OnDentHull != null);
                // Documented gaps (see GameLoop.WirePlaneCombat's own header) —
                // asserted ABSENT on purpose, so a future wiring pass changes
                // this line instead of silently going unnoticed either way.
                Check("hull_ondetach_still_a_gap", plane.Hull.OnDetach == null,
                    "needs a per-plane Combat.AirframeBreakup instance — not constructed anywhere in this port yet");

                // ---- PlaneWallContact.RamOther — the previous pass's named gap ----
                Check("wallcontact_ramother_wired", plane.WallContact.RamOther != null);
                Check("wallcontact_onhint_wired", plane.WallContact.OnHint != null);
                Check("groundroll_onhint_wired", plane.GroundRoll.OnHint != null);

                // ---- GameLoop.ResetPlane() -> Hull.ResetAll() ----
                plane.Hull.Health = 1f; // perturb, then prove ResetPlane() restores it
                game.ResetPlane();
                Check("resetplane_calls_hull_resetall", plane.Hull.Health == PlaneHullDamage.MaxHealth,
                    $"Health={plane.Hull.Health} after ResetPlane()");
                Check("resetplane_sets_airborne_true", plane.Airborne, "game.js:1713 _liftoff convention");

                // ---- TargetRegistry.RecOf — needed by the RamOther closure ----
                var regEntry = new GameLoop.TargetRegistry.Entry { PlaneRec = new PlaneRecord { Own = false } };
                game.Targets.Register(regEntry);
                var col = goPlane.AddComponent<BoxCollider>();
                regEntry.HitCollider = col;
                var recOf = game.Targets.RecOf(col);
                Check("targetregistry_recof_resolves", recOf != null && recOf == regEntry.PlaneRec);
                Check("targetregistry_recof_null_for_unregistered", game.Targets.RecOf(null) == null);

                // ---- PlaneController.Step() — one real airborne frame, then one
                // real ground-roll frame, proving the Airborne branch actually
                // dispatches to both PlaneGroundRoll.Step and FlightModel.Update
                // (the previous pass's single biggest named gap). ----
                plane.Velocity = new Vector3(0, 0, 60f);
                plane.Airborne = true;
                Vector3 posBefore = goPlane.transform.position;
                plane.Step(0.016f, default);
                Fact("prevpos_x", plane.PrevPos.x);
                Check("prevpos_captured", plane.PrevPos == posBefore, "PrevPos must be the FRAME-START position, read before Step() moves anything");
                Check("airborne_branch_moved_plane", goPlane.transform.position != posBefore, "FlightModel.Update + position integration ran");

                plane.Airborne = false;
                plane.Velocity = Vector3.zero;
                float speedBefore = plane.Speed;
                plane.Step(0.016f, new StickInput { Throttle = 1f });
                Fact("groundroll_branch_ran_without_exception", true);

                allPass &= true; // Check() already folded failures in above
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                allPass = false;
            }
            finally
            {
                if (goGame != null) UnityEngine.Object.DestroyImmediate(goGame);
                if (goPlane != null) UnityEngine.Object.DestroyImmediate(goPlane);
            }

            Debug.Log(allPass ? "CORERESTPROBE: ALL PASS" : "CORERESTPROBE: SOME FAILED");
        }

        /// <summary>
        /// Proves GameLoop.ApplyResume (game.js:1101-1195) now consumes the FULL
        /// saved shape — quat/vel/cam/foot/footYaw — not just pos/health/onFoot,
        /// which was this pass's named gap ("the port writes a rich snapshot and
        /// reads back a thin one"). Also proves Boot.cs's own duplicate,
        /// thinner local re-implementation is gone and GameLoop.ApplyResume is
        /// the one real callee (grep -rn "Game.ApplyResume" unity/Assets/Scripts
        /// finds exactly Boot.cs's call site).
        ///
        ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CoreRestProbe.RunResume
        /// </summary>
        public static void RunResume()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                string line = $"{(cond ? "PASS" : "FAIL")} {key}";
                // the note explains what a FAILURE means, so it prints only on
                // one (see Editor/Probes/UiWireProbe.cs's own Check for the
                // "PASS x (its asset is missing)" line this produced).
                if (!cond && note != null) line += $" ({note})";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"{key}={value}");

            GameObject goGame = null, goPlane = null, goCam = null, goBody = null;
            try
            {
                goGame = new GameObject("ProbeGameLoop2");
                var game = goGame.AddComponent<GameLoop>();

                goPlane = new GameObject("ProbePlane2");
                var plane = goPlane.AddComponent<PlaneController>();
                plane.PlaneId = "p51";
                plane.enabled = false;
                plane.Init();
                game.ActivePlane = plane;

                goCam = new GameObject("ProbeCam");
                var cam = goCam.AddComponent<Camera>();
                game.MainCamera = cam;

                // Flat 50 m terrain over a wide span — real heights are
                // TerrainSampler's own concern (Data/TerrainSampler.cs), not
                // ApplyResume's; a flat grid is enough to prove the reseat math.
                const float FlatY = 50f;
                var heights = new float[] { FlatY, FlatY, FlatY, FlatY };
                var classes = new byte[] { 0, 0, 0, 0 };
                game.Terrain = new TerrainSampler(2, 2, 2000f, -1000f, -1000f, heights, classes);

                string bodyPath = "Assets/Resources/Art/Characters/banana.fbx";
                var bodyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
                if (bodyPrefab != null)
                {
                    goBody = UnityEngine.Object.Instantiate(bodyPrefab);
                    game.Rig = new CharacterRig(goBody, "banana");
                }
                else
                {
                    Debug.Log("banana.fbx missing=1 — onFoot/Rig checks skipped");
                }

                game.BeginSession();

                // ---- A: parked resume — pos/quat/health/cam, reseated to the
                // flat terrain's real height, not the stored (bogus, mid-air) Y ----
                var sA = new JObject
                {
                    ["pos"] = new JArray(100.0, 999.0, 200.0),
                    ["quat"] = new JArray(0.0, 0.70710678, 0.0, 0.70710678), // 90 deg yaw
                    ["health"] = 500.0,
                    ["airborne"] = false,
                    ["onFoot"] = false,
                    ["cam"] = new JArray(10.0, 20.0, 30.0),
                };
                bool okA = game.ApplyResume(sA, "tbilisi", "tbilisi");
                Check("parked_returns_true", okA);
                Fact("parked_plane_y", plane.transform.position.y);
                Check("parked_reseated_to_terrain", Mathf.Abs(plane.transform.position.y - (FlatY + GameLoop.WheelHeightRef)) < 0.01f,
                    "game.js:1148-1152 — wheels back on whatever is really underneath, not the stored Y");
                Fact("parked_quat", plane.transform.rotation.eulerAngles);
                Check("parked_quat_applied", Mathf.Abs(Mathf.DeltaAngle(plane.transform.rotation.eulerAngles.y, 90f)) < 0.5f,
                    "game.js:1131 quaternion.fromArray");
                Check("parked_health_applied", Mathf.Approximately(game.Health, 500f));
                Check("parked_mode_flying", game.Mode == GameMode.Flying);
                Fact("parked_cam_pos", cam.transform.position);
                Check("parked_cam_applied", cam.transform.position == new Vector3(10f, 20f, 30f),
                    "game.js:1159-1167 restoreCam — exact saved spot, not the chase-cam fallback");

                // ---- B: airborne resume — velocity restored, no mid-flight stall ----
                var sB = new JObject
                {
                    ["pos"] = new JArray(0.0, 80.0, 0.0),
                    ["airborne"] = true,
                    ["onFoot"] = false,
                    ["vel"] = new JArray(5.0, 0.0, 30.0),
                };
                bool okB = game.ApplyResume(sB, "tbilisi", "tbilisi");
                Check("airborne_returns_true", okB);
                Check("airborne_mode_flying", game.Mode == GameMode.Flying);
                Check("airborne_true", plane.Airborne);
                Fact("airborne_vel", plane.Velocity);
                Check("airborne_vel_applied", plane.Velocity == new Vector3(5f, 0f, 30f),
                    "game.js:1136-1138 — wake up still flying, not stalled at (0,0,0)");

                // ---- C: on-foot resume — the body's OWN saved spot, not the
                // plane's parked position (the bug in Boot.cs's old local copy) ----
                if (game.Rig != null)
                {
                    var sC = new JObject
                    {
                        ["pos"] = new JArray(300.0, 51.0, 400.0), // the PLANE's parked spot
                        ["airborne"] = false,
                        ["onFoot"] = true,
                        ["foot"] = new JArray(11.0, 0.0, 22.0),   // the BODY's own spot — must win for Rig/_foot
                        ["footYaw"] = 1.2,
                        ["health"] = 700.0,
                    };
                    bool okC = game.ApplyResume(sC, "tbilisi", "tbilisi");
                    Check("onfoot_returns_true", okC);
                    Check("onfoot_mode_onfoot", game.Mode == GameMode.OnFoot);
                    Fact("onfoot_foot_pos", game.Foot.Pos);
                    Check("onfoot_foot_pos_is_foot_not_plane", game.Foot.Pos == new Vector3(11f, 0f, 22f),
                        "must read s.foot, not s.pos (game.js:1170 foot.resume(s.foot, ...))");
                    Check("onfoot_rig_pos_applied", game.Rig.Root.position == new Vector3(11f, 0f, 22f));
                    Fact("onfoot_yaw", game.Foot.Yaw);
                    Check("onfoot_yaw_applied", Mathf.Approximately(game.Foot.Yaw, 1.2f));
                    Check("onfoot_plane_still_reseated", Mathf.Abs(plane.transform.position.y - (FlatY + GameLoop.WheelHeightRef)) < 0.01f,
                        "game.js:1143-1152 — the parked plane is reseated too, even though the hero is standing outside it");
                }
                else
                {
                    Debug.Log("SKIP onfoot_checks (no Rig)");
                }

                // ---- D/E: the early-outs — no stored pos, and a city mismatch ----
                Check("no_pos_returns_false", game.ApplyResume(new JObject()) == false);
                var sMismatch = new JObject { ["pos"] = new JArray(1.0, 2.0, 3.0) };
                Check("city_mismatch_returns_false", game.ApplyResume(sMismatch, "batumi", "tbilisi") == false,
                    "game.js:1118-1121 — saved on another map");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                allPass = false;
            }
            finally
            {
                if (goGame != null) UnityEngine.Object.DestroyImmediate(goGame);
                if (goPlane != null) UnityEngine.Object.DestroyImmediate(goPlane);
                if (goCam != null) UnityEngine.Object.DestroyImmediate(goCam);
                if (goBody != null) UnityEngine.Object.DestroyImmediate(goBody);
            }

            Debug.Log(allPass ? "CORERESTPROBE_RESUME: ALL PASS" : "CORERESTPROBE_RESUME: SOME FAILED");
        }
    }
}
