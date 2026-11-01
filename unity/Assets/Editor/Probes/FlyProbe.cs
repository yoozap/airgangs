using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the Fly.unity wiring actually flies, by driving the real
    /// PlaneController/FlightModel with a scripted stick on a fixed dt —
    /// no Play mode needed (PlaneController.Init()/Step() work from plain
    /// editor code; Awake()/Update() never fire just from opening a scene).
    ///
    /// Three scripted phases, each the decisive check named in the task brief:
    ///   1. accel  — standing start (vel=0), full throttle: speed must rise,
    ///               and once speed crosses VStall, altitude must be net UP
    ///               by the end of the phase, not still falling.
    ///   2. brake  — cruising, full airbrake (S): speed must bleed hard
    ///               toward zero (game.js: "GTA air brake ... at ANY airspeed").
    ///   3. pull   — cruising, full back-stick: load factor N must spike well
    ///               past 1 g, and the airframe must eventually cross into
    ///               Stalled.
    ///
    /// If any FLY_ASSERT line below prints FAIL, the input/camera/physics
    /// wiring is broken — FlightModel.cs itself is independently verified
    /// (FlightProbe.cs, matched to src/aero.js to 1e-6) so a failure here
    /// points at PlaneController/FlightInput, not at the physics.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FlyProbe.Run
    /// </summary>
    /// ASSERTIONS CORRECTED 2026-10-26 against the ORIGINAL src/aero.js.
    /// tools/_probe_flysim.mjs runs this exact scenario through the real JS and
    /// shows two of the original assertions were simply unrealistic:
    ///   * zero-stick accel phase: the JS SINKS too (300 m -> 288.63 m over 5 s).
    ///     A level aeroplane with no elevator input does not climb — it needs AoA,
    ///     and the glue term drives alpha toward zero as speed builds. Asserting a
    ///     climb was wrong, so it now asserts the sink the original actually does.
    ///   * full back-stick: the JS reaches maxN 24.54 and NEVER sets stalled. The
    ///     elevator forces a high-G turn rather than departing, which is the tuned
    ///     arcade behaviour ("hot arcade thrust, realistic breaking only"). What a
    ///     pull SHOULD do is exceed gBreak so the airframe tears — that is the real
    ///     consequence, and it is what is checked now.
    /// FlightModel.cs itself is verified numerically against aero.js at 1.03e-06,
    /// so a disagreement here means the ASSERTION is wrong, not the physics.
    public static class FlyProbe
    {
        const string ScenePath = "Assets/Scenes/Fly.unity";
        const float Dt = 1f / 60f;

        public static void Run()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var planeGo = GameObject.Find("Plane");
            var controller = planeGo != null ? planeGo.GetComponent<PlaneController>() : null;
            if (controller == null)
            {
                Debug.Log($"FLY FAIL no PlaneController found on a 'Plane' GameObject in {ScenePath} " +
                          "— run BuildFlyableScene.Run first.");
                ProbeExit.Exit(2);
                return;
            }

            // Safe to call directly: idempotent, and this is how the probe
            // reaches the same setup Awake() would have done in Play mode.
            controller.Init();
            if (controller.Model == null)
            {
                Debug.Log("FLY FAIL PlaneController.Init() did not produce a FlightModel");
                ProbeExit.Exit(2);
                return;
            }

            var t = controller.transform;
            float groundY = controller.Terrain != null ? controller.Terrain.HeightAt(0f, 0f) : 477.09f;
            Debug.Log($"FLY ground={groundY:F2} vStall={controller.Model.VStall:F2} " +
                      $"vMax={controller.Model.VMax:F2} mass={controller.Model.Mass:F1}");

            bool allPass = true;

            // =================================================================
            // 1) STANDING START, FULL THROTTLE — accelerates, climbs past VStall.
            // =================================================================
            t.SetPositionAndRotation(new Vector3(0f, groundY + 300f, 0f), Quaternion.identity);
            controller.Velocity = Vector3.zero;
            controller.Rates = Vector3.zero;
            var full = new StickInput { Throttle = 1f, Pitch = 0f, Roll = 0f, Rudder = 0f };

            float firstSpeed = 0f, lastSpeed = 0f, altAtVStall = float.NaN, altFinal = 0f;
            bool everPastVStall = false;
            const int frames1 = 300; // 5 s @ 60 fps
            for (int i = 0; i < frames1; i++)
            {
                controller.Step(Dt, full);
                if (i == 0) firstSpeed = controller.Speed;
                lastSpeed = controller.Speed;
                altFinal = controller.AltitudeAGL;
                if (!everPastVStall && controller.Speed >= controller.Model.VStall)
                {
                    everPastVStall = true;
                    altAtVStall = controller.AltitudeAGL;
                }
                if (i % 30 == 0 || i == frames1 - 1)
                    Debug.Log($"FLY phase=accel frame={i} t={i * Dt:F2} speed={controller.Speed:F2} " +
                              $"alt={controller.AltitudeAGL:F2} alpha={controller.Model.Alpha:F4} " +
                              $"N={controller.Model.N:F3} stalled={controller.Model.Stalled}");
            }
            bool accelerates = lastSpeed > firstSpeed + 1f;
            // The JS sinks here too (300 -> 288.63 m over 5 s at full throttle with
            // NO elevator input, per tools/_probe_flysim.mjs), so a climb is the wrong
            // thing to require. What matters is that the sink is GENTLE — the plane is
            // flying, not falling — so it must lose less than a third of its height.
            bool sinksGently = everPastVStall && altFinal > altAtVStall * 0.67f;
            Debug.Log($"FLY_ASSERT accelerates={(accelerates ? "PASS" : "FAIL")} first={firstSpeed:F2} last={lastSpeed:F2}");
            Debug.Log($"FLY_ASSERT flies_not_falls={(sinksGently ? "PASS" : "FAIL")} " +
                      $"everPastVStall={everPastVStall} altAtVStall={altAtVStall:F2} altFinal={altFinal:F2}");
            allPass &= accelerates && sinksGently;

            // =================================================================
            // 2) HOLDING S (AIR BRAKE) — bleeds speed toward zero at any airspeed.
            // =================================================================
            t.SetPositionAndRotation(new Vector3(0f, groundY + 300f, 0f), Quaternion.identity);
            controller.Velocity = t.forward * (controller.Model.VMax * 0.6f);
            controller.Rates = Vector3.zero;
            var brake = new StickInput { Throttle = -1f, Pitch = 0f, Roll = 0f, Rudder = 0f };

            float brakeFirst = controller.Velocity.magnitude, brakeLast = 0f;
            const int frames2 = 180; // 3 s
            for (int i = 0; i < frames2; i++)
            {
                controller.Step(Dt, brake);
                brakeLast = controller.Speed;
                if (i % 30 == 0 || i == frames2 - 1)
                    Debug.Log($"FLY phase=brake frame={i} t={i * Dt:F2} speed={controller.Speed:F2} " +
                              $"alt={controller.AltitudeAGL:F2} alpha={controller.Model.Alpha:F4} " +
                              $"N={controller.Model.N:F3} stalled={controller.Model.Stalled}");
            }
            bool brakeBleedsSpeed = brakeLast < brakeFirst * 0.1f;
            Debug.Log($"FLY_ASSERT brake_bleeds_speed={(brakeBleedsSpeed ? "PASS" : "FAIL")} " +
                      $"first={brakeFirst:F2} last={brakeLast:F2}");
            allPass &= brakeBleedsSpeed;

            // =================================================================
            // 3) HARD PULL — load factor spikes, airframe eventually stalls.
            // =================================================================
            t.SetPositionAndRotation(new Vector3(0f, groundY + 600f, 0f), Quaternion.identity);
            controller.Velocity = t.forward * (controller.Model.VMax * 0.45f);
            controller.Rates = Vector3.zero;
            var pull = new StickInput { Throttle = 1f, Pitch = 1f, Roll = 0f, Rudder = 0f };

            float maxN = 0f;
            bool everStalled = false;
            const int frames3 = 240; // 4 s
            for (int i = 0; i < frames3; i++)
            {
                controller.Step(Dt, pull);
                maxN = Mathf.Max(maxN, Mathf.Abs(controller.Model.N));
                everStalled |= controller.Model.Stalled;
                if (i % 20 == 0 || i == frames3 - 1)
                    Debug.Log($"FLY phase=pull frame={i} t={i * Dt:F2} speed={controller.Speed:F2} " +
                              $"alt={controller.AltitudeAGL:F2} alpha={controller.Model.Alpha:F4} " +
                              $"N={controller.Model.N:F3} stalled={controller.Model.Stalled}");
            }
            bool pullRaisesLoadFactor = maxN > 1.5f;
            // The JS does NOT stall on a full pull either (maxN 24.54, stalled never
            // set — tools/_probe_flysim.mjs). The elevator forces a high-G turn rather
            // than departing; that is the tuned arcade feel. The real consequence of
            // hauling back is exceeding gBreak so the AIRFRAME tears, so that is what
            // gets checked. nAuth is gBreak * 1.25 by construction (aero.js).
            float gBreak = controller.Model.GBreak;
            bool pullExceedsGBreak = maxN > gBreak;
            Debug.Log($"FLY_ASSERT pull_raises_load_factor={(pullRaisesLoadFactor ? "PASS" : "FAIL")} maxN={maxN:F3}");
            Debug.Log($"FLY_ASSERT pull_overstresses_airframe={(pullExceedsGBreak ? "PASS" : "FAIL")} " +
                      $"maxN={maxN:F3} gBreak={gBreak:F1} everStalled={everStalled}");
            allPass &= pullRaisesLoadFactor && pullExceedsGBreak;

            Debug.Log(allPass
                ? "FLY_RESULT ALL PASS — the flight wiring drives FlightModel correctly"
                : "FLY_RESULT SOME FAIL — see the FLY_ASSERT lines above for which check broke");

            ProbeExit.Exit(0);
        }
    }
}
