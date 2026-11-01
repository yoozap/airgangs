using System;
using System.Linq;
using TbilisiFly.Core;
using TbilisiFly.Combat;
using TbilisiFly.UI.Target;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the WoW floor selection ring (src/target.js:256-322) is REACHED,
    /// not merely computable. TargetRingProbe.cs already proves the maths; it
    /// was also the only thing in the project that ever constructed a
    /// <see cref="TargetRingView"/>, which is why tools/_test_no_orphans.py
    /// listed the view as PROBE_ONLY and the circle under a targeted parked
    /// aeroplane was never drawn in the game.
    ///
    /// This probe loads Assets/Scenes/Game.unity (the only scene that boots the
    /// game), runs the real PlayableSceneBoot, ticks the real GameLoop, selects
    /// a parked machine through the game's OWN selection paths (Tab ->
    /// GameLoop.cs:2094 `_targets.Cycle(MainCamera)`, click ->
    /// GameLoop.cs:2098 `_targets.PickAt(...)`), and then reads the live ring
    /// back: renderer enabled, sitting on the hangar floor under the machine,
    /// and sized to that machine's own half-span per target.js:317.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.TargetRingWireProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class TargetRingWireProbe
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
                Debug.Log(allPass ? "TARGETRINGWIRE RESULT=PASS" : "TARGETRINGWIRE RESULT=FAIL");
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
            Check("playableSceneBootExists", boot != null);
            try { boot?.Run(); } catch (Exception e) { Debug.LogException(e); }

            // The ring self-installs from StageHudAndTargets' EnsureTargetRing()
            // on the first tick (GameLoop.cs — same one-shot shape as
            // EnsureHintLine/EnsureHitToast).
            for (int i = 0; i < 10; i++) game.Tick(1f / 60f);

            Check("targetRingInstalled", game.TargetRing != null,
                "GameLoop.EnsureTargetRing() never ran — nothing constructs TargetRingView");
            if (game.TargetRing == null) { Done(); return; }

            // ---- the fleet is registered (CombatWorldAdapter.cs:615) ----------
            var reg = game.Targets;
            int total = reg.List.Count;
            int withSpan = reg.List.Count(e => e.HalfSpan.HasValue);
            int parked = reg.List.Count(e => !e.IsOwn && e.Wrapper != null);
            Fact("id=registry", $"entries={total} parked={parked} withHalfSpan={withSpan}");
            Check("registryFilled", parked > 0, "no parked machine is in the live registry");
            Check("halfSpanCarried", withSpan == total && total > 0,
                $"{total - withSpan} entries have no halfSpan -> every ring would be 6.36 m");
            if (parked == 0) { Done(); return; }

            // =================================================================
            // ---- SELECT A MACHINE THE WAY THE GAME DOES ----------------------
            // =================================================================
            // Tab: GameLoop.cs:2094 `_targets.Cycle(MainCamera)`. Round-robin,
            // so cycle until it lands on a parked (not own) machine.
            GameLoop.TargetRegistry.Entry picked = null;
            for (int i = 0; i < total + 2 && picked == null; i++)
            {
                reg.Cycle(game.MainCamera);
                var c = reg.Current;
                if (c != null && !c.IsOwn && c.Wrapper != null) picked = c;
            }
            Check("tabSelectsParkedPlane", picked != null);
            if (picked == null) { Done(); return; }
            Fact("id=tab", $"target={picked.Id} halfSpan={(picked.HalfSpan.HasValue ? picked.HalfSpan.Value.ToString("F2") : "null")}");

            // Click: GameLoop.cs:2098 `_targets.PickAt(cam, pointer)`. Stand the
            // camera 40 m out from the machine, aim at it, pick through screen
            // centre. Reported as a fact as well as asserted, because it is the
            // one path that needs edit-mode Physics queries to answer.
            var cam = game.MainCamera;
            bool clickPicked = false;
            if (cam != null)
            {
                Fact("id=cam", $"pixelW={cam.pixelWidth} pixelH={cam.pixelHeight}");
                Vector3 at = picked.Wrapper.position;
                foreach (float back in new[] { 40f, 20f, 12f })
                {
                    cam.transform.position = at + new Vector3(0f, 3f, -back);
                    cam.transform.rotation = Quaternion.LookRotation((at - cam.transform.position).normalized, Vector3.up);
                    reg.Clear();
                    bool ok = reg.PickAt(cam, new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f));
                    // What the single Physics.Raycast inside PickAt actually met
                    // first. target.js:115-122 walks ALL sorted hits; the nested
                    // registry's PickAt takes only the nearest one, so anything
                    // in front of the machine (a shed wall, a fan, a crate)
                    // makes the click miss where the JS would still hit.
                    var ray = cam.ScreenPointToRay(new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f));
                    var all = Physics.RaycastAll(ray, 2000f, ~0, QueryTriggerInteraction.Ignore)
                                     .OrderBy(h => h.distance).Take(3).ToArray();
                    string chain = string.Join("|", all.Select(h =>
                        $"{h.collider.name}@{h.distance:F1}{(h.collider.GetComponentInParent<PlaneDamageRecord>() != null ? "(plane)" : "")}"));
                    Fact("id=clickTry", $"backM={back:F0} picked={ok.ToString().ToLowerInvariant()} hits={chain}");
                    if (ok) { clickPicked = true; break; }
                }
            }
            Fact("id=click", $"picked={clickPicked.ToString().ToLowerInvariant()} current={(reg.Current != null ? reg.Current.Id : "none")}");
            // Put the Tab pick back so the ring assertions below read one known
            // machine whether or not the click path answered.
            if (reg.Current == null || reg.Current.IsOwn) reg.Clear();
            while (!ReferenceEquals(reg.Current, picked))
            {
                reg.Cycle(game.MainCamera);
                if (reg.Current == null) break;
            }
            Check("selectionHeld", ReferenceEquals(reg.Current, picked));

            // =================================================================
            // ---- THE RING IS DRAWN -------------------------------------------
            // =================================================================
            game.Tick(1f / 60f);
            var root = game.TargetRing.Root;
            var mr = root != null ? root.GetComponent<MeshRenderer>() : null;
            var mf = root != null ? root.GetComponent<MeshFilter>() : null;
            Check("ringRootExists", root != null && mr != null && mf != null);
            if (root == null || mr == null || mf == null) { Done(); return; }

            Fact("id=renderer", $"active={root.activeInHierarchy.ToString().ToLowerInvariant()} " +
                 $"enabled={mr.enabled.ToString().ToLowerInvariant()} " +
                 $"hasMesh={(mf.sharedMesh != null).ToString().ToLowerInvariant()} " +
                 $"mat={(mr.sharedMaterial != null ? mr.sharedMaterial.shader.name : "none")}");
            Check("ringRendererEnabled", root.activeInHierarchy && mr.enabled && mf.sharedMesh != null,
                "the ring exists but is switched off / has no geometry");

            // ---- under the machine, on the floor -----------------------------
            Vector3 planePos = picked.Wrapper.position;
            Vector3 ringPos = root.transform.position;
            float dxz = Vector2.Distance(new Vector2(planePos.x, planePos.z), new Vector2(ringPos.x, ringPos.z));
            Fact("id=place", $"planeY={planePos.y:F3} ringY={ringPos.y:F3} dXZ={dxz:F4}");
            Check("ringUnderPlane", dxz < 0.01f, $"dXZ={dxz:F3} m");

            // Re-measure the floor independently of the game's own probe —
            // target.js:287-294's ray, written out here so the two numbers can
            // disagree in the log rather than agreeing by construction.
            var hits = Physics.RaycastAll(new Vector3(planePos.x, planePos.y + 1f, planePos.z),
                                          Vector3.down, 14f, ~0, QueryTriggerInteraction.Ignore);
            float? floorY = null; string floorName = "none"; float bestD = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                if (h.collider.GetComponentInParent<PlaneDamageRecord>() != null) continue;
                if (h.distance >= bestD) continue;
                bestD = h.distance; floorY = h.point.y; floorName = h.collider.name;
            }
            Fact("id=floor", $"hits={hits.Length} floor={floorName} " +
                 $"floorY={(floorY.HasValue ? floorY.Value.ToString("F3") : "null")} " +
                 $"gameProbeRingY={(picked.RingY.HasValue ? picked.RingY.Value.ToString("F3") : "null")}");
            float floorRef = floorY ?? (planePos.y - TargetRingMath.WheelHeight);
            float aboveFloor = ringPos.y - floorRef;
            Fact("id=height", $"aboveFloorM={aboveFloor:F4} expected=0.1000");
            Check("ringOnFloor", Mathf.Abs(aboveFloor) <= 0.5f,
                $"the ring is {aboveFloor:F2} m off the floor it is supposed to lie on");
            Check("ringAtJsOffset", Mathf.Abs(aboveFloor - 0.1f) < 0.01f,
                $"target.js:296 puts it floor+0.1; measured floor+{aboveFloor:F3}");

            // ---- sized per target.js:317 -------------------------------------
            float wantR = TargetRingMath.RingRadius(picked.HalfSpan);
            float wantW = TargetRingMath.RingBandWidth(wantR);
            var b = mf.sharedMesh.bounds;
            float gotR = Mathf.Max(b.extents.x, b.extents.z);
            Fact("id=size", $"halfSpan={(picked.HalfSpan ?? -1f):F3} wantR={wantR:F4} meshOuterR={gotR:F4} bandW={wantW:F4} " +
                 $"verts={mf.sharedMesh.vertexCount} triIndices={mf.sharedMesh.triangles.Length}");
            Check("ringRadiusPerSpec", Mathf.Abs(gotR - wantR) < 0.01f, $"want {wantR:F3} got {gotR:F3}");
            Check("ringMeshShape", mf.sharedMesh.vertexCount == 146 && mf.sharedMesh.triangles.Length == 432,
                "target.js:271 RingGeometry(...,72) -> 73*2 verts, 72*6 indices");

            // ---- your own machine never gets one (target.js:309) -------------
            var own = reg.List.FirstOrDefault(e => e.IsOwn);
            if (own != null)
            {
                reg.Clear();
                while (!ReferenceEquals(reg.Current, own)) { reg.Cycle(game.MainCamera); if (reg.Current == null) break; }
                game.Tick(1f / 60f);
                Fact("id=own", $"active={root.activeInHierarchy.ToString().ToLowerInvariant()}");
                Check("ownMachineHasNoRing", !root.activeInHierarchy, "target.js:309 — a ring would hang under you in the air");
            }
            else Fact("id=own", "active=absent");

            // ---- nothing targeted -> no ring (target.js:303) -----------------
            reg.Clear();
            game.Tick(1f / 60f);
            Fact("id=cleared", $"active={root.activeInHierarchy.ToString().ToLowerInvariant()}");
            Check("clearedHidesRing", !root.activeInHierarchy);

            Done();
        }
    }
}
