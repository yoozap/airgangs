using System.Reflection;
using TbilisiFly.Core;
using TbilisiFly.Combat;
using TbilisiFly.Flight;
using TbilisiFly.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verifies this pass's own work on the game.js coverage audit:
    ///   1. Core/CombatWorldAdapter.TryParkInShed now zeroes Airborne/Speed
    ///      (src/game.js:908-909) alongside the Velocity/Rates it already
    ///      zeroed — invoked by REFLECTION since it is a private static
    ///      method, so this calls the real compiled method, not a
    ///      reimplementation.
    ///   2. The same two fields on GoAirportOrShed's "doesn't fit the bay"
    ///      outside-stand fallback branch (src/game.js:942-943) — exercised
    ///      the same way, with the plane placed far from ShedSpawnPos and the
    ///      scene carrying no GameObject named "warehouse", so the method
    ///      takes exactly that branch and never touches AirportBuilder/the
    ///      runway branch (unrelated to this pass's change).
    ///   3. UI/Hud/HudChipsView — the #hud speed/alt tiles (src/game.js:
    ///      2337-2338, src/foot.js:6242-6244), new this pass.
    ///
    /// Never enters Play mode — same convention as Editor/Probes/CoreRestProbe.cs.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CoreGameProbe.Run
    /// </summary>
    public static class CoreGameProbe
    {
        static MethodInfo M(string name) =>
            typeof(CombatWorldAdapter).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

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

            GameObject goPlane = null, goWarehouse = null, goGame = null;
            try
            {
                goPlane = new GameObject("ProbePlane");
                var rec = goPlane.AddComponent<PlaneDamageRecord>(); // defaults: HalfSpan=6, Size=11 -> span=12
                var plane = goPlane.AddComponent<PlaneController>();
                plane.PlaneId = "p51";
                plane.enabled = false;
                plane.Init();

                // Deliberately NOT named "warehouse" — GoAirportOrShed's
                // GameObject.Find("warehouse") (src/game.js:886/958) must miss it,
                // same as the JS's own "the airport has not built yet" degrade.
                goWarehouse = new GameObject("ProbeWarehouse");
                goWarehouse.transform.position = new Vector3(500f, 0f, 500f);

                goGame = new GameObject("ProbeGameLoop");
                var game = goGame.AddComponent<GameLoop>();
                // GoAirportOrShed's own first line is `if (ActivePlane == null ||
                // Terrain == null) return;` — a null Terrain would make case 3
                // below a silent no-op that still reads as a PASS (Airborne/Speed
                // never touched because the method never ran at all, not because
                // it ran and correctly skipped them). A huge flat 2x2 grid covers
                // every position this probe uses without needing real city data.
                game.Terrain = new TbilisiFly.Data.TerrainSampler(2, 2, 100000f, -100000f, -100000f,
                    new float[] { 0f, 0f, 0f, 0f }, new byte[] { 0, 0, 0, 0 });

                var tryPark = M("TryParkInShed"); // (GameLoop, PlaneController, GameObject) -> bool

                // ---- case 1: fits the bay (span 12 < door 22.8, tall 0 < 8.55) ----
                plane.Airborne = true;
                plane.Speed = 42f;
                plane.Velocity = new Vector3(1f, 2f, 3f);
                plane.Rates = new Vector3(4f, 5f, 6f);
                bool parked = (bool)tryPark.Invoke(null, new object[] { game, plane, goWarehouse });
                Check("parkinshed_fits", parked, "span=12 < door 22.8, tall=0 < 8.55");
                Check("parkinshed_airborne_false", plane.Airborne == false, "src/game.js:908 this.airborne = false");
                Check("parkinshed_speed_zero", plane.Speed == 0f, "src/game.js:909 this.speed = 0");
                Check("parkinshed_vel_zero", plane.Velocity == Vector3.zero);
                Check("parkinshed_rates_zero", plane.Rates == Vector3.zero);

                // ---- case 2: does not fit (span 100 > door 22.8) — early return,
                // must NOT touch Airborne/Speed (proves the fix sits after the fit
                // check, exactly like the JS's own early
                // `if (!span || span > ...) return false;`) ----
                rec.HalfSpan = 50f; // span = 100
                plane.Airborne = true;
                plane.Speed = 77f;
                bool parked2 = (bool)tryPark.Invoke(null, new object[] { game, plane, goWarehouse });
                Check("parkinshed_refuses_oversize", parked2 == false);
                Check("parkinshed_refusal_leaves_airborne", plane.Airborne == true,
                    "an early return must not have reached the new Airborne/Speed lines");
                Check("parkinshed_refusal_leaves_speed", plane.Speed == 77f);

                // ---- case 3: GoAirportOrShed's outside-the-bay fallback branch
                // (src/game.js:942-943). Plane placed 2800 m from the default
                // ShedSpawnPos (Vector3.zero) so distToShed > 250 and the method
                // skips the near-shed/runway branch entirely; no "warehouse"
                // GameObject exists, so `inside` is forced false and this lands
                // in exactly the fallback block this pass edited.
                game.ActivePlane = plane;
                game.DebugForceMode(GameMode.Flying); // Mode defaults to Flying anyway; explicit for clarity
                plane.transform.position = new Vector3(2800f, 0f, 0f);
                plane.Airborne = true;
                plane.Speed = 88f;
                var goAirportOrShed = M("GoAirportOrShed");
                goAirportOrShed.Invoke(null, new object[] { game });
                Check("goairport_outside_airborne_false", plane.Airborne == false, "src/game.js:942");
                Check("goairport_outside_speed_zero", plane.Speed == 0f, "src/game.js:943");
            }
            finally
            {
                if (goPlane != null) Object.DestroyImmediate(goPlane);
                if (goWarehouse != null) Object.DestroyImmediate(goWarehouse);
                if (goGame != null) Object.DestroyImmediate(goGame);
            }

            // ---- UI/Hud/HudChipsView — src/game.js:2337-2338 / src/foot.js:6242-6244 ----
            var chips = new HudChipsView();
            chips.Tick(new HudChipsView.Data { Started = false });
            Check("chips_hidden_before_start", chips.style.display == DisplayStyle.None,
                "src/game.js:1391 — #hud is hidden until Started flips once");

            chips.Tick(new HudChipsView.Data { Started = true, SpeedKmh = 194.4f, AltM = 77.6f });
            Check("chips_visible_after_start", chips.style.display == DisplayStyle.Flex);
            var speedTile = chips.hierarchy[0];
            var altTile = chips.hierarchy[1];
            string speedTxt = ((Label)speedTile.hierarchy[0]).text;
            string altTxt = ((Label)altTile.hierarchy[0]).text;
            Debug.Log($"chips_speed_text={speedTxt} chips_alt_text={altTxt}");
            Check("chips_speed_rounds", speedTxt == "194", "Mathf.RoundToInt(194.4)=194");
            Check("chips_alt_rounds", altTxt == "78", "Mathf.RoundToInt(77.6)=78");

            Debug.Log(allPass ? "CORE_GAME_PROBE_ALL_PASS" : "CORE_GAME_PROBE_SOME_FAILED");
        }
    }
}
