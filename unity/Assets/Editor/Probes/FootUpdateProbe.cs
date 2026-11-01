using UnityEditor;
using UnityEngine;
using TbilisiFly.Character;
using TbilisiFly.Combat;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Counterpart to tools/_probe_footupdate.mjs for the Unity port of
    /// src/foot.js's update(dt) (5475-6274) and _weaponFrame (6275-6441).
    ///
    /// Most of update()'s own body is ALREADY ported in
    /// Character/FootController.cs (not touched by this pass). The two units
    /// this pass actually added are Character/FootWeaponFrame.cs
    /// (_weaponFrame's orchestration) and Character/FootPlaneProbe.cs
    /// (_planeTargets, ported but not yet wired into FootController's
    /// Physics-layer-based probes — see that file's own header). Both expose
    /// their magic numbers as `internal const`, readable here through
    /// AssemblyInfo.cs's InternalsVisibleTo("Assembly-CSharp-Editor").
    ///
    /// This probe checks the same PURE MATH the .mjs counterpart does — no
    /// scene, no rig, no GameObject required — so every line here should
    /// pair with an identically-named/shaped line from the .mjs output.
    /// Bone-posing output (the actual arm/weapon solve) is NOT checked here;
    /// that needs a live rig and is out of this probe's reach the same way
    /// it is out of the .mjs's reach.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.FootUpdateProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class FootUpdateProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            Debug.Log($"FOOTUPDATE const=DrawSettle value={FootWeaponFrame.DrawSettle}");
            Debug.Log($"FOOTUPDATE const=DrawSettleAmp value={FootWeaponFrame.DrawSettleAmp}");
            Debug.Log($"FOOTUPDATE const=MuzzleDip value={Weapons.MuzzleDip}");
            Debug.Log($"FOOTUPDATE const=RifleDip value={FootWeapons.RifleDip}");
            Debug.Log($"FOOTUPDATE const=PlaneRangeSq value={FootPlaneProbe.RangeSq}");
            Debug.Log($"FOOTUPDATE const=PlaneMeshRefresh value={FootPlaneProbe.MeshRefresh}");

            // ---- foot.js:6392-6397 the draw-settle muzzle rock ----
            foreach (float t in new[] { 0f, 0.03f, 0.09f, 0.12f, 0.18f, 0.24f, 0.29f })
            {
                float v = DipExtra(t);
                Debug.Log($"FOOTUPDATE fn=dipExtra t={t} value={v:F8}");
            }

            // ---- foot.js:6344-6346 the recoil/reload blend `rw` ----
            (bool rifle, string side, float rb, float fb)[] rwCases =
            {
                (false, "R", 0f, 0f), (false, "R", 0.6f, 0.2f), (false, "R", 0.1f, 0.9f),
                (true, "R", 0f, 0.8f), (true, "L", 0f, 0.8f), (true, "R", 0.5f, 0.5f),
            };
            foreach (var (rifle, side, rb, fb) in rwCases)
            {
                string id = $"{(rifle ? "rifle" : "pistol")}_{side}_rb{rb}_fb{fb}";
                float v = Rw(rifle, side, rb, fb);
                Debug.Log($"FOOTUPDATE fn=rw id={id} rifle={(rifle ? "true" : "false")} side={side} reloadBlend={rb} fireBlend={fb} value={v:F8}");
            }

            // ---- foot.js:6435 the cover-hold attenuation `cwHold` ----
            (float cw, float rb)[] cwCases = { (0f, 0f), (1f, 0f), (0.5f, 0.5f), (1f, 1f), (0.8f, 0.2f) };
            foreach (var (cw, rb) in cwCases)
            {
                string id = $"cw{cw}_rb{rb}";
                Debug.Log($"FOOTUPDATE fn=cwHold id={id} cw={cw} reloadBlend={rb} value={(cw * (1f - rb)):F8}");
            }

            // ---- foot.js:4751-4779 _planeTargets' distance gate ----
            var probe = new FootPlaneProbe();
            (float hx, float hz, float px, float pz)[] rangeCases =
            {
                (0f, 0f, 0f, 0f), (0f, 0f, 29f, 0f), (0f, 0f, 31f, 0f),
                (0f, 0f, 20f, 20f), (10f, 10f, 10f, 40f),
            };
            foreach (var (hx, hz, px, pz) in rangeCases)
            {
                string id = $"h{hx}_{hz}_p{px}_{pz}";
                bool inRange = InRange(hx, hz, px, pz);
                Debug.Log($"FOOTUPDATE fn=planeInRange id={id} heroX={hx} heroZ={hz} planeX={px} planeZ={pz} value={(inRange ? "true" : "false")}");
            }
        }

        // Mirrors FootWeaponFrame.Tick's own dipExtra computation exactly
        // (foot.js:6392-6397) — kept here rather than exposing a static
        // helper on FootWeaponFrame, since this probe is read-only review,
        // not a reason to grow that class's public surface.
        static float DipExtra(float presentT)
        {
            if (presentT < 0f) return 0f;
            if (presentT > FootWeaponFrame.DrawSettle) return 0f; // _presentT would reset to -1
            return -FootWeaponFrame.DrawSettleAmp * Mathf.Sin(2f * Mathf.PI * presentT / 0.24f)
                * Mathf.Exp(-presentT / 0.09f);
        }

        static float Rw(bool rifle, string side, float reloadBlend, float fireBlend)
        {
            if (rifle && side == "L") return 0f;
            if (rifle) return fireBlend * 0.45f;
            return Mathf.Max(reloadBlend, fireBlend);
        }

        // Mirrors FootPlaneProbe.Colliders' own gate (foot.js:4751-4757)
        // without needing a live Transform/Collider — pure distance math.
        static bool InRange(float heroX, float heroZ, float planeX, float planeZ)
        {
            float dx = planeX - heroX, dz = planeZ - heroZ;
            return (dx * dx + dz * dz) <= FootPlaneProbe.RangeSq;
        }
    }
}
