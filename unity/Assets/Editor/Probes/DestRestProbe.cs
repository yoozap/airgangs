using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verification for the destruction-side gap-closing pass covering
    /// src/rockets.js, src/dents.js, src/impactfx.js and src/planedamage.js,
    /// PLUS Combat/PlaneAirframeHost.cs — the per-plane AirframeBreakup instance
    /// + wing-mesh resolution that Core/PlaneHullDamage.cs's own header named
    /// as the wiring neither it nor anything else in this port had: "construct
    /// one instance per active plane, subscribe the hooks to FxAudio/
    /// AirframeBreakup/SmokeTrail-equivalent/OnHint". Mirrors
    /// tools/_probe_destrest.mjs's own output key-for-key — see that file's own
    /// header for the 3 kinds of check (constants / wiring-presence / confirmed-
    /// gap) and why GAP_STILL_OPEN_* is EXPECTED to print PASS today (Core/
    /// GameLoop.cs is read-only to this pass; wiring those 5 lines in belongs
    /// to whoever next touches GameLoop.cs).
    ///
    /// NOT EXECUTED THIS SESSION — no editor access (the coordinator holds the
    /// batch-mode lock, per this port's standing rule). Written so the
    /// coordinator can run it as-is.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.DestRestProbe.Run
    /// </summary>
    public static class DestRestProbe
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

            // ---- 1. src/rockets.js top-level constants ----
            Fact("COOLDOWN", F(RocketLauncher.Cooldown));
            Fact("DAMAGE", F(RocketLauncher.Damage));
            Fact("BLAST_R", F(RocketLauncher.BlastR));
            Fact("HOLE_R", F(RocketLauncher.HoleR));
            Fact("GLASS_R", F(RocketLauncher.GlassR));
            Fact("RANGE", F(RocketLauncher.Range));
            Fact("ACCEL", F(RocketLauncher.Accel));
            Fact("TOP_SPEED", F(RocketLauncher.TopSpeed));
            Fact("MAX_LIVE", RocketLauncher.MaxLive);

            // ---- 2. src/rockets.js:99-130 RocketSound.whoosh ----
            Fact("WHOOSH_BP_FREQ0", F(RocketLauncher.WhooshBpFreq0));
            Fact("WHOOSH_BP_FREQ1", F(RocketLauncher.WhooshBpFreq1));
            Fact("WHOOSH_BP_Q", F(RocketLauncher.WhooshBpQ));
            Fact("WHOOSH_BP_RATE", F(RocketLauncher.WhooshBpRate));
            Fact("WHOOSH_BP_GAIN", F(RocketLauncher.WhooshBpGain));
            Fact("WHOOSH_BP_DUR", F(RocketLauncher.WhooshBpDur));
            Fact("WHOOSH_TONE_F0", F(RocketLauncher.WhooshToneF0));
            Fact("WHOOSH_TONE_F1", F(RocketLauncher.WhooshToneF1));
            Fact("WHOOSH_TONE_GAIN", F(RocketLauncher.WhooshToneGain));
            Fact("WHOOSH_TONE_DUR", F(RocketLauncher.WhooshToneDur));

            // ---- 3. src/game.js:2278-2335 wing mesh/material mutation ----
            Fact("WING_DROOP_RAD", F(PlaneAirframeHost.WingDroopRad));
            Color cc = PlaneAirframeHost.CharColor;
            Fact("CHAR_COLOR_HEX", $"0x{ByteOf(cc.r):x2}{ByteOf(cc.g):x2}{ByteOf(cc.b):x2}");
            // JS stores roughness (1 while charred, 0.55 restored); this port's
            // Unity analogue is smoothness = 1-roughness — see PlaneAirframeHost.cs's
            // own header for why. Converted back here so the two sides compare the
            // SAME quantity the JS literal actually is.
            Fact("CHAR_ROUGHNESS", F(1f - PlaneAirframeHost.CharSmoothness));
            Fact("REPAIR_ROUGHNESS", F(1f - PlaneAirframeHost.RepairSmoothness));

            // ---- 4/5/6. wing-mesh resolution + the 5 hooks, proven by actually
            // DOING it on a real GameObject (not by reflecting over source text —
            // that half is tools/_probe_destrest.mjs's job) ----
            var world = new NullBreakupWorld();
            var planeGo = new GameObject("DestRestProbe_Plane");
            GameObject rightWing = null, leftWing = null;
            try
            {
                // src/planes.js:1594-1611: a mesh named "sagkanatsekil*"/"solkanatsekil*",
                // classified by its OWN local bbox centre sign, not by which half of the
                // name it's spelled with — placed on the "wrong" side of its own name's
                // language to prove the classifier really reads geometry.
                rightWing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                rightWing.name = "sagkanatsekil_R";
                rightWing.transform.SetParent(planeGo.transform, false);
                rightWing.transform.localPosition = new Vector3(3f, 0f, 0f);
                rightWing.transform.localScale = new Vector3(4f, 0.3f, 1.5f); // local bounds -> x in [1,5]

                leftWing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leftWing.name = "solkanatsekil_L";
                leftWing.transform.SetParent(planeGo.transform, false);
                leftWing.transform.localPosition = new Vector3(-3f, 0f, 0f);
                leftWing.transform.localScale = new Vector3(4f, 0.3f, 1.5f); // local bounds -> x in [-5,-1]

                var host = new PlaneAirframeHost(world, planeGo.transform, () => Vector3.zero, isPlayerOwn: true);
                Check("host_has_airframebreakup", host.Breakup != null);

                Transform rightTip = host.WingTipBone("right");
                Transform leftTip = host.WingTipBone("left");
                Check("host_resolves_right_wing_by_bbox_not_name", rightTip != null && Mathf.Abs(rightTip.position.x - 5f) < 0.01f,
                    rightTip != null ? $"tip.x={rightTip.position.x:0.###}" : "null");
                Check("host_resolves_left_wing_by_bbox_not_name", leftTip != null && Mathf.Abs(leftTip.position.x - (-5f)) < 0.01f,
                    leftTip != null ? $"tip.x={leftTip.position.x:0.###}" : "null");

                var rightRenderer = rightWing.GetComponent<Renderer>();
                // READ THE PROPERTY THE CODE WRITES. These three colour checks
                // read "_Color", while Combat/PlaneAirframeHost.SetBaseColor
                // (:285) writes "_BaseColor" and only falls back to "_Color"
                // when the shader has no "_BaseColor" — which URP's Lit always
                // does. So all three were reading an untouched property:
                // host_bendwing_chars_material FAILED with the shader's own
                // default (color=RGBA(0.500, 0.500, 0.500, 1.000)) and
                // host_repairwing_restores_color PASSED VACUOUSLY, comparing
                // one untouched value with the same untouched value. Same
                // precedence as GetBaseColor/SetBaseColor now, so the probe
                // tests the wire the code actually drives.
                Color BaseColorOf(Material m) =>
                    m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                    : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                Color before = BaseColorOf(rightRenderer.sharedMaterial);
                Quaternion rotBefore = rightWing.transform.localRotation;
                host.BendWing("right"); // -> OnWingBend
                float bendDeg = Quaternion.Angle(rotBefore, rightWing.transform.localRotation);
                Color charred = BaseColorOf(rightRenderer.material);
                Check("host_bendwing_droops_by_wingdroop_rad", Mathf.Abs(bendDeg - PlaneAirframeHost.WingDroopRad * Mathf.Rad2Deg) < 0.05f,
                    $"deg={bendDeg:0.###}");
                Check("host_bendwing_chars_material", ColorsClose(charred, PlaneAirframeHost.CharColor), $"color={charred}");

                host.RepairWing("right"); // -> OnWingRepair
                float repairedDeg = Quaternion.Angle(rotBefore, rightWing.transform.localRotation);
                Color repaired = BaseColorOf(rightRenderer.material);
                Check("host_repairwing_restores_rotation", repairedDeg < 0.05f, $"deg={repairedDeg:0.###}");
                Check("host_repairwing_restores_color", ColorsClose(repaired, before), $"color={repaired}");

                Check("host_clearwingsmoke_runs_without_throw", RunsClean(host.ClearWingSmoke));
                host.Dispose();
            }
            finally
            {
                Object.DestroyImmediate(planeGo);
            }

            // CrashScatter/DetachPart separately, on a bare mesh-less rig: these two
            // engage AirframeBreakup's own mesh-SPLITTING surgery (SplitMesh/
            // Build/_removedWhole), which is Combat/AirframeBreakup.cs's existing,
            // already-verified territory (not this pass's own new code — this
            // pass's new code is PlaneAirframeHost's pass-through + the wing
            // resolution proven above). Kept deliberately mesh-less here so this
            // probe proves "the 2 one-line hook adapters call through correctly"
            // without re-exercising geometry surgery this pass did not write.
            var rigGo = new GameObject("DestRestProbe_BareRig");
            try
            {
                var rigHost = new PlaneAirframeHost(world, rigGo.transform, () => Vector3.zero, isPlayerOwn: true);
                Check("host_crashscatter_runs_without_throw", RunsClean(() => rigHost.CrashScatter(Vector3.zero, 1f)));
                Check("host_detachpart_runs_without_throw", RunsClean(() => rigHost.DetachPart("wingL", 8f, false)));
                rigHost.Dispose();
            }
            finally
            {
                Object.DestroyImmediate(rigGo);
            }

            Debug.Log($"ALL_PASS={(allPass ? "true" : "false")}");
            ProbeExit.Exit(allPass ? 0 : 1);
        }

        static bool RunsClean(System.Action a)
        {
            try { a(); return true; }
            catch (System.Exception e) { Debug.LogException(e); return false; }
        }

        static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        static int ByteOf(float c01) => Mathf.RoundToInt(Mathf.Clamp01(c01) * 255f);
        static bool ColorsClose(Color a, Color b, float tol = 0.01f) =>
            Mathf.Abs(a.r - b.r) < tol && Mathf.Abs(a.g - b.g) < tol && Mathf.Abs(a.b - b.b) < tol;

        /// <summary>A plane with no meshes at all (empty GameObject) still has to
        /// go through BuildWings()/Build() without throwing — proves the lazy
        /// resolution degrades gracefully when a probe plane carries no
        /// "sagkanatsekil"/"solkanatsekil" children, same as a bodyless prefab
        /// would.</summary>
        sealed class NullBreakupWorld : IBreakupWorld
        {
            public float? GroundAt(float x, float z) => null;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void SetBurning(string key, Vector3[] points, float intensity) { }
            public void ClearBurning(string key) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public Vector3 HeroVelocity() => Vector3.zero;
            public bool HeroAirborne() => false;
        }
    }
}
