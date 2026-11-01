using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TbilisiFly.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verification for Core/GameLoop.cs's Snapshot()/Loadout()/SaveState() —
    /// src/game.js:983-1100 `snapshot()`/`_loadout()`/`saveState()`, the
    /// per-hero spawn-place persistence. Opens the real Assets/Scenes/Game.unity
    /// (same fixture PlayableProbe.cs uses), runs PlayableSceneBoot for real so
    /// ActivePlane/Rig/HeldWeapon/LoadoutProvider all exist, then drives
    /// SaveState() directly and reads the RESOLVED JObject back off
    /// game.Hero.StateOf(hid) — not a hand-built expectation, the actual
    /// stored row HeroLink.Save() wrote.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///     -executeMethod TbilisiFly.Probes.SaveStateProbe.Run
    /// </summary>
    public static class SaveStateProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string Hid = "probe_hero";

        // Mode's setter is private (GameLoop.cs:262) — this probe needs to
        // force the Crashed branch without routing through EnterCrashed()'s
        // full side-effect chain (camera shake, hull/crash audio, a
        // CrashFactory call needing a built breakup rig), so it writes the
        // backing property directly, same reflection-access precedent this
        // project's probes already use for Awake() (see unity skill doc).
        static void SetMode(GameLoop game, GameMode mode)
        {
            typeof(GameLoop).GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance)
                .SetValue(game, mode);
        }

        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                // the note explains what a FAILURE means, so it prints only on
                // one (see Editor/Probes/UiWireProbe.cs's own Check).
                string line = $"{(cond ? "PASS" : "FAIL")} {key}{(!cond && note != null ? $" ({note})" : "")}";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"{key}={value}");

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);
            if (game == null) { Debug.Log("SAVESTATEPROBE RESULT=FAIL"); ProbeExit.Exit(1); return; }

            var sceneBoot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            try { sceneBoot?.Run(); } catch (Exception e) { Debug.LogException(e); }

            Check("started", game.Started);
            Check("planeExists", game.ActivePlane != null);
            Check("loadoutProviderWired", game.LoadoutProvider != null,
                "CombatWorldAdapter.Install() should have wired this alongside IsArmedProvider");

            game.Hero = new HeroLink { Username = "probe_user" };
            game.ActiveHeroId = Hid;

            // ---- live snapshot: force a known plane/camera/foot state, then
            // save and read back the REAL stored row. ----
            game.ActivePlane.transform.position = new Vector3(111.25f, 22.5f, -333.75f);
            game.ActivePlane.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            game.ActivePlane.Velocity = new Vector3(10f, 2f, -5f);
            game.ActivePlane.Airborne = true;
            game.ActivePlane.Speed = 47.5f;
            game.Health = 63.4f;
            if (game.MainCamera != null) game.MainCamera.transform.position = new Vector3(5f, 6f, 7f);

            game.SaveState(true, "probe_live");
            var live = game.Hero.StateOf(Hid);
            Check("live_state_saved", live != null);
            if (live != null)
            {
                Fact("LIVE_POS", live["pos"]);
                Fact("LIVE_AIRBORNE", live["airborne"]);
                Fact("LIVE_SPEED", live["speed"]);
                Fact("LIVE_HEALTH", live["health"]);
                Fact("LIVE_ONFOOT", live["onFoot"]);
                Fact("LIVE_PLANE", live["plane"]);
                Fact("LIVE_CITY", live["city"]);
                Fact("LIVE_GUNS", live["guns"]);
                Fact("LIVE_GEAR", live["gear"]);

                var pos = (JArray)live["pos"];
                Check("live_pos_x", pos != null && Mathf.Approximately((float)pos[0], 111.25f));
                Check("live_pos_y", pos != null && Mathf.Approximately((float)pos[1], 22.5f));
                Check("live_pos_z", pos != null && Mathf.Approximately((float)pos[2], -333.75f));
                Check("live_airborne_true", live["airborne"]?.Value<bool>() == true);
                Check("live_speed", Mathf.Approximately(live["speed"]?.Value<float>() ?? -1f, 47.5f));
                // game.js:1000 Math.round(63.4) == 63 (round toward +infinity on
                // the .5 case does not apply here, but JsMath.Round is what the
                // port uses everywhere this value is produced).
                Check("live_health_rounded", live["health"]?.Value<int>() == 63);
                Check("live_onfoot_false", live["onFoot"]?.Value<bool>() == false);
                Check("live_city_tbilisi", live["city"]?.Value<string>() == "tbilisi");
                Check("live_has_quat", live["quat"] is JArray qa && qa.Count == 4);
                Check("live_has_vel", live["vel"] is JArray va && va.Count == 3);
                Check("live_has_cam", live["cam"] is JArray ca && ca.Count == 3);
                Check("live_no_foot_fields_when_flying", live["foot"] == null && live["footYaw"] == null,
                    "game.js:1011-1012 only sets foot/footYaw when onFoot");
            }

            // ---- dedup: an immediate second autosave with nothing changed
            // must not rewrite the row (game.js:1079 `if (!force && mark ===
            // this._savedMark) return`). Observed indirectly: force=false
            // with the exact same state must leave the stored JObject
            // reference-equal (HeroLink.Save() always creates fresh "at"
            // bookkeeping when it actually runs, so an unchanged object
            // means SaveState's own early-out fired, not Save() losing a
            // race). ----
            var beforeDedup = game.Hero.StateOf(Hid);
            game.SaveState(false, "probe_dedup");
            var afterDedup = game.Hero.StateOf(Hid);
            Check("dedup_skips_identical_save", ReferenceEquals(beforeDedup, afterDedup));

            // A real change (position moved) must NOT be swallowed by dedup.
            game.ActivePlane.transform.position += new Vector3(50f, 0f, 0f);
            game.SaveState(false, "probe_change");
            var afterChange = game.Hero.StateOf(Hid);
            Check("change_is_not_deduped", !ReferenceEquals(afterDedup, afterChange));

            // ---- dead branch: Crashed -> snapshot() returns null, but
            // whatever the personage OWNS survives its death as its own row
            // (game.js:1057-1072), with no position — this scene's hero
            // actually wears a starting "belt" (HeldWeapon's default), so
            // this is the non-empty arm of the JS's own
            // "guns.length || gear.length ? {...} : null" gate. ----
            SetMode(game, GameMode.Crashed);
            Check("crashed_is_set", game.Crashed);
            game.SaveState(true, "probe_dead_nonempty_kit");
            var deadKit = game.Hero.StateOf(Hid);
            Fact("DEAD_KIT_STATE", deadKit?.ToString(Newtonsoft.Json.Formatting.None) ?? "null(deleted)");
            Check("dead_kit_survives_death", deadKit != null && deadKit["pos"] == null,
                "game.js:1060-1066 — the spawn place goes (no pos field), what's owned does not");
            Check("dead_kit_has_gear", deadKit?["gear"] is JArray dg && dg.Count > 0 && dg[0].Value<string>() == "belt");
            Check("dead_kit_has_city", deadKit?["city"]?.Value<string>() == "tbilisi");

            // Genuinely-empty kit: the JS's "guns.length || gear.length ?
            // {...} : null" gate (game.js:1072) must still delete when there
            // is truly nothing owned. Exercised directly against Loadout()'s
            // own empty case rather than against this scene's non-empty
            // HeldWeapon (unequipping it is out of this probe's scope),
            // using the exact same SaveState code path with the provider
            // swapped out for one that reports nothing carried.
            game.LoadoutProvider = () => (Array.Empty<(int, string)>(), null);
            // Force a mark change: Loadout() output flipped from
            // "guns=[],gear=[belt]" to "guns=[],gear=[]", so the dedup key
            // changes and SaveState will not just early-out on `_savedMark`.
            game.SaveState(true, "probe_dead_empty_kit");
            var deadEmpty = game.Hero.StateOf(Hid);
            Fact("DEAD_EMPTY_KIT_STATE", deadEmpty == null ? "null(deleted)" : deadEmpty.ToString(Newtonsoft.Json.Formatting.None));
            Check("dead_empty_kit_deletes_row", deadEmpty == null,
                "game.js:1072 `kit.guns.length || kit.gear.length ? {...} : null` — empty kit deletes, not an empty object");

            Debug.Log(allPass ? "SaveStateProbe: ALL PASS" : "SaveStateProbe: SOME FAILED");
            ProbeExit.Exit(allPass ? 0 : 1);
        }
    }
}
