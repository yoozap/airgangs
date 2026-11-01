using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves FlightModel.cs reproduces src/aero.js. Prints the derived airframe
    /// numbers for known planes; tools/_probe_aero.mjs prints the same from the JS
    /// and the two are diffed. A port is done when the numbers match, not when it
    /// looks right.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FlightProbe.Run
    /// </summary>
    public static class FlightProbe
    {
        struct Case { public string Id; public AeroPlaneDef Def; public float Size, HalfSpan; }

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var cases = new[]
            {
                new Case { Id = "spitfire", Size = 11.23f,  HalfSpan = 5.615f,
                           Def = new AeroPlaneDef { Prop = true,  ClMax = 1.6f, GBreak = 9f,   Tw = 2.4f, MaxSpeed = 300f } },
                new Case { Id = "tornado",  Size = 16.72f,  HalfSpan = 6.955f,
                           Def = new AeroPlaneDef { Prop = false, ClMax = 1.7f, GBreak = 7.5f, Tw = 3.1f, MaxSpeed = 400f } },
                new Case { Id = "su33",     Size = 21.19f,  HalfSpan = 7.35f,
                           Def = new AeroPlaneDef { Prop = false, ClMax = 1.9f, GBreak = 9f,   Tw = 3.5f, MaxSpeed = 420f } },
                new Case { Id = "mig21nk",  Size = 14.1f,   HalfSpan = 3.575f,
                           Def = new AeroPlaneDef { Prop = false, ClMax = 1.3f, GBreak = 8.5f, Tw = 3.4f, MaxSpeed = 410f } },
            };

            foreach (var c in cases)
            {
                var m = new FlightModel(c.Def, c.Size, c.HalfSpan);
                Debug.Log($"AERO id={c.Id} mass={m.Mass:F3} S={m.S:F4} kInd={m.KInduced:F6} " +
                          $"cd0={m.Cd0:F4} alphaStall={m.AlphaStall:F6} thrust0={m.Thrust0:F2} " +
                          $"vStall={m.VStall:F4} vr={m.Vr:F4} vne={m.Vne:F3} " +
                          $"rollMax={m.RollMax:F5} pitchMax={m.PitchMax:F5} yawMax={m.YawMax:F5} " +
                          $"tau={m.Tau:F5} nAuth={m.NAuth:F4}");

                // rho/thrust lapse at a few altitudes
                Debug.Log($"AERO id={c.Id} rho0={m.Rho(0f):F6} rho2k={m.Rho(2000f):F6} " +
                          $"thr0={m.ThrustAt(100f, 0f):F2} thr2k={m.ThrustAt(100f, 2000f):F2} " +
                          $"vcap0={m.VCapAt(0f):F3} vcap2k={m.VCapAt(2000f):F3}");
            }
        }
    }
}
