using System;
using System.Linq;
using TbilisiFly.Core;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves docs/open_items.json's `pickat-nearest-only` is closed:
    /// GameLoop.TargetRegistry.PickAt used ONE Physics.Raycast and accepted only
    /// the nearest hit, so anything standing between the camera and a machine
    /// made the click miss. src/target.js:118's
    /// `this.ray.intersectObjects([g.plane, ...near], true)` tests only the
    /// CANDIDATE meshes, so the crate/fan/wall in front is not in the JS's hit
    /// list at all and the machine behind it still gets picked.
    ///
    /// The test is the real warehouse: this probe loads Assets/Scenes/Game.unity,
    /// runs the real PlayableSceneBoot, and for a registered parked machine
    /// SEARCHES for a camera stance whose nearest collider is NOT the machine
    /// (the crates stand in front of the ranges, the three fans in front of the
    /// slung P-51). It reports the hit chain it found and asserts the pick landed
    /// on the plane anyway. If the live scene happens to offer no obstructed
    /// stance, it plants its own box collider on the line of sight so the walk is
    /// exercised either way — and the obstructed-nearest fact is printed, so a
    /// vacuous pass (nothing in front, nothing proved) is visible in the log.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.TargetPickThroughProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class TargetPickThroughProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                string line = $"{(cond ? "PASS" : "FAIL")} {key}";
                if (!cond && note != null) line += $" ({note})";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"RINGWIRE {key} {value}");
            void Done()
            {
                Debug.Log(allPass ? "TARGETPICKTHROUGH RESULT=PASS" : "TARGETPICKTHROUGH RESULT=FAIL");
                ProbeExit.Exit(allPass ? 0 : 1);
            }

            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }
            Check("sceneLoads", openOk && scene.IsValid());
            if (!openOk || !scene.IsValid()) { Done(); return; }

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);
            if (game == null) { Done(); return; }
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            try { boot?.Run(); } catch (Exception e) { Debug.LogException(e); }
            for (int i = 0; i < 10; i++) game.Tick(1f / 60f);

            var reg = game.Targets;
            var cam = game.MainCamera;
            Fact("id=pickRange", $"m={GameLoop.TargetRegistry.PickRange:F0} jsPickRange=700");
            Check("pickRangeMatchesJs", Mathf.Abs(GameLoop.TargetRegistry.PickRange - 700f) < 0.001f,
                "src/target.js:31 PICK_RANGE = 700");

            var parked = reg.List.Where(e => !e.IsOwn && e.Wrapper != null).ToList();
            Fact("id=registry", $"entries={reg.List.Count} parked={parked.Count}");
            Check("registryFilled", parked.Count > 0 && cam != null);
            if (parked.Count == 0 || cam == null) { Done(); return; }

            // =================================================================
            // Hunt the live scene for a stance where something REAL and
            // UNREGISTERED stands in front of a registered machine: 16 headings
            // x 6 stand-offs per machine, and every hit nearer than the machine
            // must resolve to no registry entry (another parked machine in the
            // way is not this bug — picking THAT one is the right answer).
            // =================================================================
            var centre = new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f);
            GameLoop.TargetRegistry.Entry chosen = null;
            string chain = "none";
            string blocker = "none";
            float blockerD = 0f, planeD = 0f;
            bool planted = false;
            GameObject plantedGo = null;

            foreach (var e in parked)
            {
                Vector3 at = e.Wrapper.position;
                for (int h = 0; h < 16 && chosen == null; h++)
                {
                    float ang = h * Mathf.PI * 0.125f;
                    foreach (float back in new[] { 40f, 30f, 22f, 16f, 11f, 8f })
                    {
                        cam.transform.position = at + new Vector3(Mathf.Cos(ang) * back, 1.7f, Mathf.Sin(ang) * back);
                        cam.transform.rotation = Quaternion.LookRotation((at - cam.transform.position).normalized, Vector3.up);
                        var ray = cam.ScreenPointToRay(centre);
                        var all = Physics.RaycastAll(ray, GameLoop.TargetRegistry.PickRange, ~0,
                                                     QueryTriggerInteraction.Ignore)
                                         .OrderBy(x => x.distance).ToArray();
                        if (all.Length == 0) continue;
                        // The first hit that belongs to THIS machine...
                        int planeIdx = -1;
                        for (int i = 0; i < all.Length; i++)
                            if (all[i].collider != null && all[i].collider.transform.IsChildOf(e.Wrapper)) { planeIdx = i; break; }
                        if (planeIdx <= 0) continue;   // not seen, or seen FIRST (nothing in the way)
                        // ...and every hit in front of it must be UNREGISTERED.
                        // The fleet is parked 2 m apart, so the first stance
                        // found this way had another MACHINE in front, and
                        // selecting that one is the correct answer — asserting
                        // otherwise is what made this probe fail on a right
                        // result (RINGWIRE id=stance blocker=cessna310@2.0).
                        bool allUnregistered = true;
                        for (int i = 0; i < planeIdx; i++)
                            if (reg.EntryOf(all[i].collider) != null) { allUnregistered = false; break; }
                        if (!allUnregistered) continue;
                        chosen = e;
                        blocker = all[0].collider.name;
                        blockerD = all[0].distance;
                        planeD = all[planeIdx].distance;
                        chain = string.Join("|", all.Take(4).Select(x =>
                            $"{x.collider.name}@{x.distance:F1}" +
                            (reg.EntryOf(x.collider) != null ? "(target)" : "")));
                        break;
                    }
                }
                if (chosen != null) break;
            }

            // Nothing obstructed in the live scene: plant an obstacle so the walk
            // is still exercised (and say so in the log — see the class header).
            if (chosen == null)
            {
                chosen = parked[0];
                Vector3 at = chosen.Wrapper.position;
                cam.transform.position = at + new Vector3(0f, 1.7f, -26f);
                cam.transform.rotation = Quaternion.LookRotation((at - cam.transform.position).normalized, Vector3.up);
                plantedGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                plantedGo.name = "PickThroughObstacle";
                plantedGo.transform.position = cam.transform.position + cam.transform.forward * 6f;
                plantedGo.transform.localScale = new Vector3(4f, 4f, 0.5f);
                planted = true;
                var ray = cam.ScreenPointToRay(centre);
                var all = Physics.RaycastAll(ray, GameLoop.TargetRegistry.PickRange, ~0, QueryTriggerInteraction.Ignore)
                                 .OrderBy(x => x.distance).ToArray();
                int planeIdx = -1;
                for (int i = 0; i < all.Length; i++)
                    if (all[i].collider != null && all[i].collider.transform.IsChildOf(chosen.Wrapper)) { planeIdx = i; break; }
                if (all.Length > 0) { blocker = all[0].collider.name; blockerD = all[0].distance; }
                if (planeIdx >= 0) planeD = all[planeIdx].distance;
                chain = string.Join("|", all.Take(4).Select(x =>
                    $"{x.collider.name}@{x.distance:F1}" +
                    (reg.EntryOf(x.collider) != null ? "(target)" : "")));
            }

            Fact("id=stance", $"target={chosen.Id} planted={planted.ToString().ToLowerInvariant()} " +
                 $"blocker={blocker}@{blockerD:F1} planeAt={planeD:F1}");
            Fact("id=hits", chain);
            Check("obstacleIsNearest", blockerD > 0f && planeD > blockerD + 0.01f,
                "nothing stands between the camera and the machine, so this run proves nothing");

            // ---- THE CLICK ---------------------------------------------------
            reg.Clear();
            bool picked = reg.PickAt(cam, centre);
            string got = reg.Current != null ? reg.Current.Id : "none";
            Fact("id=clickThrough", $"picked={picked.ToString().ToLowerInvariant()} current={got} want={chosen.Id} " +
                 $"blocker={blocker} blockerD={blockerD:F1} planeD={planeD:F1}");
            Check("clickThroughSelectsPlane", picked && ReferenceEquals(reg.Current, chosen),
                $"the click stopped on {blocker} — PickAt is still nearest-hit-only");

            // ---- and a click at NOTHING still returns false (target.js:120) --
            reg.Clear();
            cam.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward); // at the sky
            bool sky = reg.PickAt(cam, centre);
            Fact("id=sky", $"picked={sky.ToString().ToLowerInvariant()} current={(reg.Current != null ? reg.Current.Id : "none")}");
            Check("skyPicksNothing", !sky && reg.Current == null,
                "target.js:120-122 — a click on the sky selects nothing and is still a camera drag");

            if (plantedGo != null) UnityEngine.Object.DestroyImmediate(plantedGo);
            Done();
        }
    }
}
