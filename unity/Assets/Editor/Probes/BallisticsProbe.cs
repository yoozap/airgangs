using System;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Assets/Scripts/Combat/Ballistics.cs reproduces src/ballistics.js.
    /// tools/_probe_ballistics.mjs prints the same keys from the real JS module;
    /// tools/unity_port_check.py diffs the two with a relative tolerance.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.BallisticsProbe.Run
    ///
    /// The RES section drives BulletSim.Resolve() directly with a synthetic
    /// RaycastHit (point/normal are settable; .collider is deliberately left
    /// null — the stub IBallisticsWorld below never looks at it, matching how
    /// the JS probe forces the surface through hit.object.userData.surface
    /// instead of a material-name match). NextRandom is swapped for the same
    /// mulberry32 generator the JS probe seeds Math.random with, so the
    /// STOCHASTIC ricochet/perforation branches are diffed exactly, not just
    /// the deterministic formulas around them.
    /// </summary>
    public static class BallisticsProbe
    {
        // Identical generator to tools/_probe_ballistics.mjs's mulberry32(seed).
        static Func<float> Mulberry32(uint seed)
        {
            uint a = seed;
            return () =>
            {
                unchecked
                {
                    a = a + 0x6D2B79F5u;
                    uint t = a;
                    t = (t ^ (t >> 15)) * (1u | t);
                    t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;
                    return (float)(((t ^ (t >> 14))) / 4294967296.0);
                }
            };
        }

        sealed class FixedSurfaceWorld : IBallisticsWorld
        {
            public string Tag;
            public string ExplicitSurfaceTag(Collider hit) => Tag;
            public bool TryBuildingFacade(Collider hit, Vector3 point, out bool isWindow)
            { isWindow = false; return false; }
            public bool IsDestructible(Collider hit) => false;
            public bool IsLandmark(Collider hit) => false;
            public bool IsTerrain(Collider hit) => false;
            public bool IsRoadSurface(Collider hit) => false;
            public int GroundClassAt(float x, float z) => 0;
        }

        public static void Run()
        {
            DumpCartridges();
            DumpSurfaces();
            Trajectory("9x19", new Vector3(0, 0, 1));
            Trajectory("7.62x39", new Vector3(0, -0.05f, 1f).normalized);

            ResolveCase("ricochet_metal_a", 1, "metal", 0.10f, 400f, 300f);
            ResolveCase("ricochet_metal_b", 7, "metal", 0.10f, 400f, 300f);
            ResolveCase("ricochet_concrete", 2, "concrete", 0.15f, 420f, 320f);
            ResolveCase("perforate_wood", 3, "wood", Mathf.PI / 2f, 300f, 280f);
            ResolveCase("perforate_asphalt", 4, "asphalt", Mathf.PI / 2f, 350f, 300f);
            ResolveCase("lodge_masonry", 5, "masonry", Mathf.PI / 2f, 250f, 260f);
            ResolveCase("lodge_stone", 6, "stone", Mathf.PI / 2f, 400f, 320f);
            ResolveCase("lodge_water", 8, "water", Mathf.PI / 2f, 300f, 280f);
            ResolveCase("plane_skin", 9, "skin", Mathf.PI / 2f, 69f, 300f);
            ResolveCase("plane_skin_through", 10, "skin", Mathf.PI / 2f, 580f, 381f);
            ResolveCase("no_ricochet_glass", 11, "glass", 0.05f, 200f, 250f);

            ProbeExit.Exit(0);
        }

        static void DumpCartridges()
        {
            foreach (var kv in Cartridges.ById)
            {
                var c = kv.Value;
                Debug.Log($"CART id={c.Id} bulletMm={c.BulletMm:F6} caseMm={c.CaseMm:F6} massG={c.MassG:F6} " +
                    $"v0={c.V0:F6} recoil={c.Recoil:F6} recoilH={c.RecoilH:F6} climb={c.Climb:F6} " +
                    $"recover={c.Recover:F6} kickBack={c.KickBack:F6} rock={c.Rock:F6} bloom={c.Bloom:F6} " +
                    $"sway={c.Sway:F6} rate={c.Rate:F6} mag={c.Mag} auto={(c.Auto ? "true" : "false")} " +
                    $"areaM2={c.AreaM2:F9} areaCm2={c.AreaCm2:F6} massKg={c.MassKg:F6} k={c.K:F9} " +
                    $"energy={c.Energy:F4} supersonic={(c.Supersonic ? "true" : "false")}");
            }
            Debug.Log($"HPJ value={Cartridges.HpPerJoule:F10}");
        }

        static void DumpSurfaces()
        {
            foreach (var kv in Surfaces.ById)
            {
                var s = kv.Value;
                Debug.Log($"SURF id={s.Id} resist={s.Resist:F6} thick={s.Thick:F6} graze={s.Graze:F6} " +
                    $"spall={s.Spall:F6} hole={s.Hole:F6} pit={s.Pit:F6} sparks={s.Sparks:F6} " +
                    $"hull={s.Hull:F6} noHole={(s.NoHole ? "true" : "false")} liquid={(s.Liquid ? "true" : "false")} " +
                    $"splinter={(s.Splinter ? "true" : "false")} plane={(s.Plane ? "true" : "false")}");
            }
        }

        static void Trajectory(string id, Vector3 dir)
        {
            var sim = new BulletSim { TargetMask = 0 }; // no layers -> Physics.Raycast never hits: pure free flight
            var cart = Cartridges.ById[id];
            sim.Spawn(cart, new Vector3(0, 50, 0), dir);
            int[] checkpoints = { 1, 5, 10, 20, 30 };
            int ci = 0;
            for (int i = 1; i <= 30; i++)
            {
                sim.Update(1f / 60f);
                if (ci < checkpoints.Length && checkpoints[ci] == i)
                {
                    ci++;
                    if (sim.Live.Count == 0) { Debug.Log($"TRAJ cart={id} step={i} dead=true"); continue; }
                    var b = sim.Live[0];
                    Debug.Log($"TRAJ cart={id} step={i} px={b.Pos.x:F6} py={b.Pos.y:F6} pz={b.Pos.z:F6} " +
                        $"vx={b.Vel.x:F6} vy={b.Vel.y:F6} vz={b.Vel.z:F6} dist={b.Dist:F6}");
                }
            }
        }

        static void ResolveCase(string name, uint seed, string surface, float grazeAngle, float energy, float speed)
        {
            var sim = new BulletSim { World = new FixedSurfaceWorld { Tag = surface }, NextRandom = Mulberry32(seed) };
            var cart = Cartridges.ById["9x19"];
            float dirY = -Mathf.Sin(grazeAngle);
            float horiz = Mathf.Sqrt(Mathf.Max(0f, 1f - dirY * dirY));
            Vector3 dir = new Vector3(horiz, dirY, 0f).normalized;

            RaycastHit hit = default;
            hit.point = new Vector3(5, 2, 5);
            hit.normal = new Vector3(0, 1, 0);

            var bullet = new Bullet { Cart = cart, Pos = new Vector3(5, 2, 5), Vel = dir * speed, Segs = 0, Dist = 50f, Trail = 0f };
            ImpactInfo info = null;
            sim.OnImpact = i => info = i;
            bool alive = sim.Resolve(bullet, hit, dir, energy, speed);

            Debug.Log($"RES case={name} alive={(alive ? "true" : "false")} ricochet={(info.Ricochet ? "true" : "false")} " +
                $"through={(info.Through ? "true" : "false")} lodged={(info.Lodged ? "true" : "false")} damage={info.Damage} " +
                $"holeW={info.HoleW:F7} holeL={info.HoleL:F7} graze={info.Graze:F7} depth={info.Depth:F7} " +
                $"exitEnergy={info.ExitEnergy:F4} vx={bullet.Vel.x:F6} vy={bullet.Vel.y:F6} vz={bullet.Vel.z:F6} " +
                $"px={bullet.Pos.x:F6} py={bullet.Pos.y:F6} pz={bullet.Pos.z:F6}");
        }
    }
}
