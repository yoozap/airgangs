using System;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verifies this pass's two deliverables: the shooting-range wiring
    /// (Combat/RangeBootstrap.cs's three closed call sites) and the fire.js
    /// bucket analysis (which has no Unity-side runtime fact to check — see
    /// tools/_probe_rangefire.mjs's own FIRELINES section for that half).
    ///
    /// Same convention as Assets/Editor/Probes/PlayableProbe.cs: PASS/FAIL
    /// lines plus KEY=value facts, never enters Play mode, nothing saved.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.RangeFireProbe.Run
    ///
    /// No baked "warehouse_range" exists in any committed scene yet (grepped
    /// — zero hits; Editor/BuildAirportDetail.cs's bake has not been run and
    /// saved against one). Rather than report a false PASS by skipping the
    /// one thing most worth checking (does AdoptBaked actually read a baked
    /// hierarchy correctly), this probe BUILDS one itself — the same names
    /// BuildAirportDetail.cs produces ("warehouse_range"/"range_template"/
    /// "range_doors"/"range_gate"/"range_paper_root"/"range_carrier"/
    /// "range_paper"/"range_counter_blocks"/"range_counter_block") — under a
    /// deliberately non-zero world POSITION (not the origin a naive "read
    /// world Z directly" implementation would get right by accident), so
    /// AdoptBaked's ZCornersLocal/InverseTransformPoint reconstruction is
    /// genuinely exercised. The RANGEMATH numbers below use the EXACT same
    /// synthetic scenario tools/_probe_rangefire.mjs reimplements in plain JS
    /// (same counterCenterZ/envMinZ/envMaxZ/carrierZ) so the two can be
    /// diffed for an exact match.
    ///
    /// Deliberately NOT rotated: each corner conversion through
    /// InverseTransformPoint is exact, but Renderer/Collider.bounds is
    /// ITSELF a world AABB, which already over-estimates a ROTATED box's
    /// true extent before any conversion happens — a known property of
    /// AABBs, not a bug in AdoptBaked, and one AirportDetail.RangeFiringEnd's
    /// own original editor-time measurement shares (it also reads
    /// Renderer.bounds, just always unrotated at measurement time). A
    /// translated-only holder proves the position handling is real without
    /// that separate, already-true approximation swamping an exact-match
    /// check in noise that has nothing to do with this pass's wiring.
    /// </summary>
    public static class RangeFireProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

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
            void Info(string key, bool cond, string note = null)
            {
                // Like Check, but does not affect allPass — for facts about
                // scene/game-loop health that are outside this pass's scope
                // (other agents' in-flight work) and would otherwise make this
                // probe report a false negative for something it didn't touch.
                string line = $"{(cond ? "INFO-OK" : "INFO-FAIL")} {key}";
                if (note != null) line += $" ({note})";
                Debug.Log(line);
            }

            // Mirrors RangeBootstrap.cs:353-363's PRIVATE `ZCornersLocal` helper
            // exactly (same 8-corner InverseTransformPoint scan). It cannot be
            // called directly — it is `static` but not `public`, and probes live
            // in a separate assembly (Assembly-CSharp-Editor) with no
            // InternalsVisibleTo grant for it — so this duplicates the algorithm
            // rather than widening production-code visibility just for a probe.
            float[] ZCornersLocalMirror(Transform frame, Bounds worldBounds)
            {
                Vector3 c = worldBounds.center, e = worldBounds.extents;
                var zs = new float[8];
                int i = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            zs[i++] = frame.InverseTransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z)).z;
                return zs;
            }

            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }
            Check("sceneLoads", openOk && scene.IsValid());

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);

            // ---- build the synthetic baked hierarchy -------------------------
            const float counterCenterZ = 3.2f, envMinZ = 1.0f, envMaxZ = 14.5f, carrierZ = 9.0f;
            GameObject holder = null, sheetGo = null, backdropGo = null;
            BoxCollider counterCol = null;
            RangeBootstrap.Range range = null;
            bool buildThrew = false; string buildMsg = null;
            try
            {
                holder = new GameObject("warehouse_range");
                // non-zero world position — see class header for why this is
                // translation-only (not rotated).
                holder.transform.position = new Vector3(37.5f, 2f, -12f);

                var tpl = new GameObject("range_template").transform;
                tpl.SetParent(holder.transform, false);

                var doorsRoot = new GameObject("range_doors").transform;
                doorsRoot.SetParent(tpl, false);
                var gate = new GameObject("range_gate").transform;
                gate.SetParent(doorsRoot, false);
                gate.localPosition = new Vector3(2f, 0f, counterCenterZ);

                var paperRoot = new GameObject("range_paper_root").transform;
                paperRoot.SetParent(tpl, false);
                var carrier = new GameObject("range_carrier").transform;
                carrier.SetParent(paperRoot, false);
                carrier.localPosition = new Vector3(0f, 0f, carrierZ);
                var paperPivot = new GameObject("range_paper").transform;
                paperPivot.SetParent(carrier, false);
                paperPivot.localPosition = new Vector3(0f, 1.0f, 0f);
                sheetGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sheetGo.name = "TargetPaper_probe";
                sheetGo.transform.SetParent(paperPivot, false);
                sheetGo.transform.localScale = new Vector3(0.6f, 0.9f, 1f);

                var blocksRoot = new GameObject("range_counter_blocks").transform;
                blocksRoot.SetParent(tpl, false);
                var block = new GameObject("range_counter_block").transform;
                block.SetParent(blocksRoot, false);
                block.localPosition = new Vector3(0f, 0f, counterCenterZ);
                var col = block.gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(4f, 1f, 0.6f);
                counterCol = col;

                // the solid backdrop — a direct child of range_template (NOT
                // under doors/paper), spanning local z [envMinZ, envMaxZ]
                // exactly, mirroring what FlattenProp leaves behind.
                var backdrop = GameObject.CreatePrimitive(PrimitiveType.Cube);
                backdrop.name = "range_solid_probe";
                backdrop.transform.SetParent(tpl, false);
                backdrop.transform.localPosition = new Vector3(0f, 1f, (envMinZ + envMaxZ) / 2f);
                backdrop.transform.localScale = new Vector3(6f, 2f, envMaxZ - envMinZ);
                backdropGo = backdrop;

                range = RangeBootstrap.AdoptBaked(holder.transform);
            }
            catch (Exception e) { buildThrew = true; buildMsg = e.Message; Debug.LogException(e); }
            Check("syntheticBuildNoThrow", !buildThrew, buildMsg);
            Check("adoptBakedReturnedRange", range != null);

            if (range != null)
            {
                Fact("LANE_COUNT", range.Lanes.Lanes.Count);
                Fact("DOOR_COUNT", range.Doors.Count);
                Fact("SHEET_COUNT", range.Sheets.Count);
                Check("oneLaneAdopted", range.Lanes.Lanes.Count == 1, $"count={range.Lanes.Lanes.Count}");
                Check("oneDoorAdopted", range.Doors.Count == 1, $"count={range.Doors.Count}");
                Check("oneSheetAdopted", range.Sheets.Count == 1, $"count={range.Sheets.Count}");

                // firingZ/endZ themselves (RangeBootstrap.AdoptBaked's own
                // RangeBootstrap.cs:281-309) are LOCAL variables inside that
                // method — never stored on Range/Lane — so they cannot be read
                // back off the real call above. RangeBootstrap.cs is off-limits
                // to edit for mere instrumentation (only for a proven port bug),
                // so this reconstructs them the same way
                // tools/_probe_rangefire.mjs's RANGEMATH section reconstructs
                // AirportDetail.RangeFiringEnd from source citations: same
                // algorithm (AdoptBaked:281-309 / ZCornersLocal:353-363), same
                // synthetic objects this probe already built (counterCol's
                // BoxCollider bounds; backdropGo's Renderer bounds — the ONLY
                // renderer under `holder` outside doors/paper in this synthetic
                // scene, so it alone reproduces AdoptBaked's envB encapsulation
                // exactly), independent of AdoptBaked's own private internals.
                if (counterCol != null && backdropGo != null)
                {
                    var backdropRenderer = backdropGo.GetComponent<Renderer>();
                    Check("backdropHasRenderer", backdropRenderer != null);
                    if (backdropRenderer != null)
                    {
                        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                        foreach (float z in ZCornersLocalMirror(holder.transform, counterCol.bounds))
                        { lo = Mathf.Min(lo, z); hi = Mathf.Max(hi, z); }
                        float firingZProbe = (lo + hi) / 2f;

                        float elo = float.PositiveInfinity, ehi = float.NegativeInfinity;
                        foreach (float z in ZCornersLocalMirror(holder.transform, backdropRenderer.bounds))
                        { elo = Mathf.Min(elo, z); ehi = Mathf.Max(ehi, z); }
                        float endZProbe = Mathf.Abs(ehi - firingZProbe) > Mathf.Abs(elo - firingZProbe) ? ehi : elo;

                        // paired against tools/_probe_rangefire.mjs's
                        // RANGEMATH id=firingZ / id=endZ lines — same synthetic
                        // inputs, exact match expected.
                        Fact("RANGEMATH id=firingZ value", firingZProbe.ToString("F3"));
                        Fact("RANGEMATH id=endZ value", endZProbe.ToString("F3"));
                        Check("firingZMatchesExpected", Mathf.Abs(firingZProbe - counterCenterZ) < 0.001f,
                            $"got={firingZProbe:F3} expected={counterCenterZ:F3}");
                        Check("endZMatchesExpected", Mathf.Abs(endZProbe - envMaxZ) < 0.001f,
                            $"got={endZProbe:F3} expected={envMaxZ:F3}");
                    }
                }

                if (range.Lanes.Lanes.Count == 1)
                {
                    var lane = range.Lanes.Lanes[0];
                    // paired against tools/_probe_rangefire.mjs's RANGEMATH lines —
                    // same ids, same synthetic inputs, exact match expected (no
                    // rotation in play here — see class header).
                    Fact("RANGEMATH id=lane_away value", lane.Away);
                    Fact("RANGEMATH id=lane_rangeM value", lane.RangeM.ToString("F3"));
                    Fact("RANGEMATH id=lane_min value", lane.Min.ToString("F3"));
                    Fact("RANGEMATH id=lane_max value", lane.Max.ToString("F3"));
                    Check("laneAwayMatchesExpected", lane.Away == 1, $"got={lane.Away} expected=1");
                    Check("laneRangeMMatchesExpected", Mathf.Abs(lane.RangeM - 5.8f) < 0.001f, $"got={lane.RangeM:F3} expected=5.800");
                    Check("laneMinMatchesExpected", Mathf.Abs(lane.Min + 5.8f) < 0.001f, $"got={lane.Min:F3} expected=-5.800");
                    Check("laneMaxMatchesExpected", Mathf.Abs(lane.Max - 5.15f) < 0.001f, $"got={lane.Max:F3} expected=5.150");
                }

                // ---- scoring actually happens (the point of the whole exercise) ----
                if (range.Sheets.Count == 1 && sheetGo != null)
                {
                    var card = range.Targets.Adopt(sheetGo.transform); // already adopted by AdoptBaked; idempotent re-fetch
                    float scoreBefore = card?.Score ?? -1f;
                    var tag = sheetGo.GetComponent<PaperCardTag>();
                    Check("sheetHasPaperCardTag", tag != null);
                    Check("sheetHasCollider", sheetGo.GetComponent<Collider>() != null);

                    // Direct scoring call — RaycastHit.collider has NO public setter
                    // in Unity's API (confirmed against this project's own
                    // Editor/Probes/BallisticsProbe.cs, which hit the exact same
                    // limitation and documented leaving .collider null), so a real
                    // Physics.Raycast is the only way to exercise
                    // RangeBootstrap.TryRouteHit's hit.collider.GetComponent<...>()
                    // step end to end — attempted below, best-effort, non-fatal.
                    // What IS fully exercised here without that limitation is the
                    // actual payoff: PaperTargets.Hit/RangeLanes.Hit scoring a shot
                    // on the adopted sheet, which is what TryRouteHit calls once it
                    // has found the tag.
                    var shot = range.Targets.Hit(sheetGo.transform, sheetGo.transform.position, Vector3.forward, 9f);
                    Check("directHitScored", shot != null);
                    float scoreAfter = range.Targets.Adopt(sheetGo.transform)?.Score ?? -1f;
                    Check("scoreIncreased", scoreAfter > scoreBefore, $"before={scoreBefore} after={scoreAfter}");

                    bool laneHit = range.Lanes.Hit(sheetGo.transform, sheetGo.transform.position, 1f);
                    Check("laneHitRegistered", laneHit);

                    // best-effort real raycast through TryRouteHitAll — see note above.
                    try
                    {
                        Physics.SyncTransforms();
                        bool gotHit = Physics.Raycast(sheetGo.transform.position + Vector3.back * 2f, Vector3.forward, out var rh, 10f);
                        if (gotHit && rh.collider != null)
                        {
                            bool routed = RangeBootstrap.TryRouteHitAll(rh, Vector3.forward, 9f);
                            Fact("PHYSICS_RAYCAST_ROUTED", routed.ToString().ToLowerInvariant());
                        }
                        else
                        {
                            Debug.Log("INFO physicsRaycastInconclusive (no hit outside Play mode — expected per BallisticsProbe.cs's own documented limitation; direct-call scoring above is the real coverage)");
                        }
                    }
                    catch (Exception e) { Debug.Log($"INFO physicsRaycastThrew ({e.Message})"); }
                }
            }

            // ---- RangeBootstrap statics usable standalone, no GameLoop needed ----
            bool discoverThrew = false;
            try { RangeBootstrap.DiscoverAndAdoptAll(); }
            catch (Exception e) { discoverThrew = true; Debug.LogException(e); }
            Check("discoverAndAdoptAllNoThrow", !discoverThrew);

            bool tickThrew = false;
            try { for (int i = 0; i < 60; i++) RangeBootstrap.TickAll(1f / 60f, new Vector3(37.5f, 2f, -3f), false); }
            catch (Exception e) { tickThrew = true; Debug.LogException(e); }
            Check("tickAllNoThrow", !tickThrew);

            // ---- optional, informational: the full GameLoop smoke test. This
            // exercises OTHER agents' in-flight scene wiring too (PlayableSceneBoot
            // etc. is not run here, unlike PlayableProbe.cs), so a failure in this
            // block alone does not fail this probe — see Info() above. ----
            if (game != null)
            {
                int errorCount = 0;
                void OnLog(string condition, string stackTrace, LogType type)
                { if (type == LogType.Exception || type == LogType.Error) errorCount++; }
                Application.logMessageReceived += OnLog;
                bool threw = false; string threwMsg = null;
                try
                {
                    game.BeginSession();
                    for (int i = 0; i < 120; i++) game.Tick(1f / 60f);
                }
                catch (Exception e) { threw = true; threwMsg = e.Message; }
                Application.logMessageReceived -= OnLog;
                Fact("GAMELOOP_TICK_LOGGED_ERROR_COUNT", errorCount);
                Info("gameLoopWithRangeNoThrow", !threw, threwMsg);
                Fact("RANGEBOOTSTRAP_ALL_COUNT_AFTER_BEGINSESSION", RangeBootstrap.All.Count);
            }

            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);

            Debug.Log(allPass ? "RANGEFIREPROBE RESULT=PASS" : "RANGEFIREPROBE RESULT=FAIL");
            ProbeExit.Exit(allPass ? 0 : 1);
        }
    }
}
