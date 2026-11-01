using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PlaneCatalog.cs reproduces src/planes.js's PLANES table and its
    /// nine derived-stat functions. tools/_probe_planecatalog.mjs prints the
    /// same lines from the real JS module; diff with tools/unity_port_check.py
    /// for the PLANE/COUNT/LOOKUP lines (numeric, tolerance-comparable).
    ///
    /// The SPEC lines (planeSpecSheet — Georgian prose with spaces and middots)
    /// are NOT parseable by unity_port_check.py's `(\w+)=([^\s]+)` line format
    /// — compare those with a plain exact-string diff instead:
    ///   diff <(grep '^SPEC ' js.txt) <(grep '^SPEC ' cs.txt)
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlaneCatalogProbe.Run
    /// </summary>
    public static class PlaneCatalogProbe
    {
        /// <summary>Standalone entry point — prints then exits. Kept for
        /// `-executeMethod TbilisiFly.Probes.PlaneCatalogProbe.Run` callers.</summary>
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        /// <summary>Body only, no Exit — so an aggregator can chain this with
        /// sibling probes in one Unity launch (same split as DamageProbe/
        /// BridgeProbe/UiProbe/etc. — see VerifyPass*Probe.cs).</summary>
        public static void Print()
        {
            Debug.Log($"COUNT id=_count n={Planes.All.Length}");

            foreach (var def in Planes.All)
            {
                var role = Planes.PlaneRoleOf(def);
                bool tank = Planes.IsTankPlane(def);
                float mass = Planes.PlaneMass(def);
                int stamina = Planes.PlaneStamina(def);
                float colMult = Planes.PlaneColMult(def);
                float fuel = Planes.PlaneFuel(def);
                float topSpeed = Planes.PlaneTopSpeed(def);
                int crashDmg = Planes.PlaneCrashDmg(def);

                Debug.Log(
                    $"PLANE id={def.Id} role={role} tank={(tank ? 1 : 0)} " +
                    $"mass={mass:F2} stamina={stamina} " +
                    $"colMult={colMult:F5} fuel={fuel:F2} " +
                    $"topSpeed={topSpeed:F2} crashDmg={crashDmg} " +
                    $"scale={def.Scale:F5} maxSpeed={def.MaxSpeed:F3} " +
                    $"yaw={def.Yaw:F6} roll={def.Roll:F6} " +
                    $"tw={def.Tw:F4} gBreak={def.GBreak:F4} clMax={def.ClMax:F4} " +
                    $"prop={(def.Prop ? 1 : 0)} props={def.Props} " +
                    $"stat0={def.Stats[0]} stat1={def.Stats[1]} stat2={def.Stats[2]} stat3={def.Stats[3]}");

                // id={def.Id}_spec, not {def.Id} — see the JS probe's comment:
                // sharing the PLANE line's id collides in unity_port_check.py's
                // grouping key and silently discards the numeric row.
                Debug.Log($"SPEC id={def.Id}_spec sheet={Planes.PlaneSpecSheet(def)}");
            }

            var unknown = Planes.PlaneById("does-not-exist");
            Debug.Log($"LOOKUP id=_lookup unknown={unknown.Id} " +
                      $"an225tank={(Planes.IsTankPlane("an225") ? 1 : 0)} " +
                      $"spitfiretank={(Planes.IsTankPlane("spitfire") ? 1 : 0)}");
        }
    }
}
