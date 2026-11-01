using System.Linq;
using System.Reflection;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves FootWeapons.cs reproduces src/foot.js:2125-3051's constant table
    /// (the draw/stow/sling timings and elbow hints) and that every method the
    /// port brief named is present. tools/_probe_footweapons.mjs prints the
    /// same facts regexed straight out of the real src/foot.js and src/weapons.js
    /// — the two outputs are diffed by hand (no network/game dependency on
    /// either side, so there is nothing to disagree about except a transcription
    /// mistake).
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FootWeaponsProbe.Run
    /// </summary>
    public static class FootWeaponsProbe
    {
        public static void Run()
        {
            Debug.Log($"DRAW_REACH={FootWeapons.DrawReach}");
            Debug.Log($"DRAW_GRIP={FootWeapons.DrawGrip}");
            Debug.Log($"DRAW_LIFT={FootWeapons.DrawLift}");
            Debug.Log($"DRAW_PRESENT={FootWeapons.DrawPresent}");
            Debug.Log($"DRAW_DUR={FootWeapons.DrawDur:F6}");
            Debug.Log($"SLING_REACH={FootWeapons.SlingReach}");
            Debug.Log($"SLING_GRIP={FootWeapons.SlingGrip}");
            Debug.Log($"SLING_LIFT={FootWeapons.SlingLift}");
            Debug.Log($"SLING_PRESENT={FootWeapons.SlingPresent}");
            Debug.Log($"SLING_DUR={FootWeapons.SlingDur:F6}");
            Debug.Log($"SLING_STOW={FootWeapons.SlingStow}");
            Debug.Log($"SLING_OUT={FootWeapons.SlingOut}");
            Debug.Log($"SLING_UP={FootWeapons.SlingUp}");
            Debug.Log($"SLING_WAY_OUT={FootWeapons.SlingWayOut}");
            Debug.Log($"SLING_WAY_UP={FootWeapons.SlingWayUp}");
            Debug.Log($"SLING_STAND_FWD={FootWeapons.SlingStandFwd}");
            Debug.Log($"FOOT_FOV={FootWeapons.FootFov}");
            Debug.Log($"RIFLE_CARRY_DIP={FootWeapons.RifleDip}");
            Debug.Log($"MUZZLE_DIP={TbilisiFly.Combat.Weapons.MuzzleDip}");

            LogHint("HINT_REACH", FootWeapons.HintReach);
            LogHint("HINT_LIFT", FootWeapons.HintLift);
            LogHint("HINT_CARRY", FootWeapons.HintCarry);
            LogHint("HINT_SLING", FootWeapons.HintSling);
            LogHint("HINT_RIFLE", FootWeapons.HintRifle);

            // Method-presence check — every name the port brief cited by line
            // number, located via reflection (bypasses accessibility, so this
            // finds private/internal members same as public ones).
            var t = typeof(FootWeapons);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string[] wanted =
            {
                "ToHolster", "SeatInPocket", "ClearBody", "BackSocket", "ToBack",
                "FromBack", "FromHolster", "DrawWeapon", "RifleSide", "GunHand",
                "AimOnDraw", "Holster", "StartStow", "StartDraw", "UpdateDraw",
                "FinishDraw", "UpdateStow", "UpdateSettle", "MoveArm",
                "SlingWaypoint", "SlingStand", "GunReachEnv",
            };
            var found = wanted.Where(n => t.GetMethod(n, flags) != null).ToArray();
            var missing = wanted.Except(found).ToArray();
            Debug.Log($"METHODS_FOUND={found.Length}/{wanted.Length}");
            if (missing.Length > 0) Debug.Log($"METHODS_MISSING={string.Join(",", missing)}");

            ProbeExit.Exit(0);
        }

        static void LogHint(string name, Vector3 h)
        {
            Debug.Log($"{name}_UP={h.x}");
            Debug.Log($"{name}_RIGHT={h.y}");
            Debug.Log($"{name}_FWD={h.z}");
        }
    }
}
