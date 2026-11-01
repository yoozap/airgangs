using System;
using UnityEditor;
using UnityEngine;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_cinema.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.CinemaProbe.Run -logFile /dev/stdout
    // Prints one KEY=value line per fact; tools/_probe_cinema.mjs reads src/
    // cinema.js's own text for the same facts and the two are compared by
    // tools/unity_port_check.py.
    public static class CinemaProbe
    {
        sealed class FakeActor : IReelActor
        {
            public Vector3 Position { get; set; } = new Vector3(0f, 0f, -3f);
            public float Yaw { get; set; }
            public float CamYaw { get; set; }
            public bool Grounded { get; set; } = true;
            public bool HasWeaponDrawn { get; set; }
            public bool Climbing { get; set; }
            public bool Flipping { get; set; }
            public bool InCover { get; set; }
            public ReelKeys LastKeys;
            public float LastYawRate, LastPitch;
            public Vector3? LastAimDir, LastAimPoint;

            public void SetKeys(ReelKeys keys) => LastKeys = keys;
            public void SetLookYawRate(float radPerSec) => LastYawRate = radPerSec;
            public void SetLookPitch(float rad) => LastPitch = rad;
            public void SetDirectorAim(Vector3? d, Vector3? p) { LastAimDir = d; LastAimPoint = p; }
            public void Draw(bool snap) { HasWeaponDrawn = true; }
            public bool Fire() => true;
            public void Roll(float targetYaw) { Yaw = targetYaw; }
            public void Slide() { }
            public bool TryClimb() => false;
            public void StartFlip(float fallMeters, float yaw) { Flipping = true; }
            public bool ToggleCover() { InCover = !InCover; return InCover; }
            public void SetAiming(bool on) { }
            public bool Reload() => true;
            public void ArmRollLand() { }
            public bool StartRollIfNotRolling(float yaw) { Yaw = yaw; return true; }
        }

        static CinemaDirector BuildRangeDirector(out FakeActor actor)
        {
            actor = new FakeActor();
            var d = new CinemaDirector { Actor = actor };
            d.Stage = new StageSetup
            {
                O = Vector3.zero, Fwd = new Vector3(0, 0, -1), Right = StageFrame.RightOfForward(new Vector3(0, 0, -1)),
                Y = 0f, Yaw = 0f, Behind = 6f, Wall = 1.2f, WallTop = 2.2f, Cubicle = 2.2f, Counter = 7f, Pitch = 1.67f,
                HasCubicle = true, Row = null,
            };
            d.Targets.Add(new CardTarget { AimAt = new Vector3(0f, 1.1f, 11f) });
            d.Targets.Add(new CardTarget { AimAt = new Vector3(3.3f, 1.1f, 12.4f) });
            d.Targets.Add(new CardTarget { AimAt = new Vector3(6.6f, 1.1f, 9.4f) });
            return d;
        }

        public static void Run()
        {
            Debug.Log($"CINEMA_SCENE_COUNT={CinemaScenes.All.Length}");
            for (int i = 0; i < CinemaScenes.All.Length; i++)
            {
                var s = CinemaScenes.All[i];
                Debug.Log($"CINEMA_SCENE_{i}_ID={s.Id}");
                Debug.Log($"CINEMA_SCENE_{i}_DUR={s.Dur:F3}");
                Debug.Log($"CINEMA_SCENE_{i}_FLIGHT={s.Flight.ToString().ToLowerInvariant()}");
                Debug.Log($"CINEMA_SCENE_{i}_GUN={s.Gun ?? "null"}");
                Debug.Log($"CINEMA_SCENE_{i}_SHOTS={s.Shots.Length}");
                Debug.Log($"CINEMA_SCENE_{i}_BEATS={s.Beats.Length}");
            }

            // ---- the camera, driven end to end through Start/Tick/PostTick ----
            var d = BuildRangeDirector(out var actor);
            var camGo = new GameObject("CinemaProbeCamera");
            var cam = camGo.AddComponent<Camera>();
            d.Start(0);
            Debug.Log($"CINEMA_ACTIVE_AFTER_START={d.Active.ToString().ToLowerInvariant()}");

            float[] sampleT = { 0.10f, 1.60f, 2.20f, 3.00f, 7.10f };
            foreach (float target in sampleT)
            {
                while (d.T < target) d.Tick(Mathf.Min(0.016f, target - d.T));
                d.PostTick(0f, cam);
                string tag = target.ToString("F2").Replace(".", "_");
                Debug.Log($"CINEMA_RANGE_T{tag}_CAMX={cam.transform.position.x:F4}");
                Debug.Log($"CINEMA_RANGE_T{tag}_CAMY={cam.transform.position.y:F4}");
                Debug.Log($"CINEMA_RANGE_T{tag}_CAMZ={cam.transform.position.z:F4}");
                Debug.Log($"CINEMA_RANGE_T{tag}_FOV={cam.fieldOfView:F3}");
            }
            d.Stop();
            Debug.Log($"CINEMA_ACTIVE_AFTER_STOP={d.Active.ToString().ToLowerInvariant()}");
            UnityEngine.Object.DestroyImmediate(camGo);

            // ---- the flight scene's scripted physics — cinema.js:1250-1328 ----
            var flightStage = CinemaFlight.BuildStage(new Vector3(100f, 0f, 200f), 0.3f, (x, z) => 50f);
            var fly = CinemaFlight.ResetToRunway(flightStage);
            Debug.Log($"CINEMA_FLY_RESET_Y={fly.Y:F4}");
            Debug.Log($"CINEMA_FLY_RESET_AIR={fly.Air.ToString().ToLowerInvariant()}");
            var timing = CinemaFlight.Timing.Default;
            float[] flyT = { 1.0f, 2.0f, 3.4f, 5.9f, 6.15f, 6.3f, 8.0f };
            float prevT = 0f;
            foreach (float ft in flyT)
            {
                CinemaFlight.Step(ref fly, ft, ft - prevT, timing, flightStage,
                    (x, z, fromY, range) => 50f, (x, z) => 40f);
                prevT = ft;
                string tag = ft.ToString("F2").Replace(".", "_");
                Debug.Log($"CINEMA_FLY_T{tag}_AIR={fly.Air.ToString().ToLowerInvariant()}");
                Debug.Log($"CINEMA_FLY_T{tag}_INCITY={fly.InCity.ToString().ToLowerInvariant()}");
                Debug.Log($"CINEMA_FLY_T{tag}_Y={fly.Y:F4}");
                Debug.Log($"CINEMA_FLY_T{tag}_V={fly.V:F4}");
                Debug.Log($"CINEMA_FLY_T{tag}_RPM={fly.Rpm:F4}");
                var (pos, rot, vel) = CinemaFlight.Apply(fly);
                Debug.Log($"CINEMA_FLY_T{tag}_POSY={pos.y:F4}");
                Debug.Log($"CINEMA_FLY_T{tag}_VELMAG={vel.magnitude:F4}");
            }

            // ---- orientation sanity: level flight nose matches velocity dir ----
            var q = CinemaFlight.ComposeOrientation(0.4f, 0f, 0f);
            Vector3 fwd = q * Vector3.forward;
            Vector3 expectFwd = new Vector3(-Mathf.Sin(0.4f), 0f, -Mathf.Cos(0.4f));
            Debug.Log($"CINEMA_ORIENT_FWD_DOT={Vector3.Dot(fwd.normalized, expectFwd.normalized):F6}");

            Debug.Log("CINEMA_PROBE_DONE=1");
        }
    }
}
