using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Flight/PlanePhysics.cs's PlanePhysicsSystem.Sweep casts its
    /// anti-tunneling ray from the body's PREVIOUS position (src/planephys.js
    /// :871 `this.ray.set(b.prev, dir);`), not its current one. Before the
    /// fix this read `World.RaycastWorld(b.Obj.position, dir, maxDist, ...)`
    /// — the CURRENT position, i.e. where Step() had ALREADY moved the body
    /// to earlier in the same call — which defeats the whole point of a
    /// sweep: a wall that sat between last frame's spot and this frame's spot
    /// is now BEHIND the body relative to its direction of travel, so a ray
    /// cast forward from the current position can never find it.
    ///
    /// This probe does not need real collider geometry to prove the bug: it
    /// hands PlanePhysicsSystem a StubWorld whose RaycastWorld implementation
    /// does nothing BUT record the `origin` it was called with, then compares
    /// that recorded origin against the body's own Prev (pre-move) and
    /// Obj.position (post-move) read right after the same Update(dt) call —
    /// LooseAirframeActor.Step() sets `_prevPos = Obj.position;` and only
    /// THEN moves `Obj.position`, before calling Sweep (see that method), so
    /// by the time this probe reads both fields back, Prev is frozen at the
    /// pre-move spot and Obj.position already holds the moved one.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.SweepOriginProbe.Run
    /// </summary>
    public static class SweepOriginProbe
    {
        static string LBool(bool b) => b ? "true" : "false";

        sealed class CaptureWorld : IPlanePhysicsWorld
        {
            public Vector3? CapturedOrigin;
            public Vector3 CapturedDir;
            public float CapturedMaxDist;
            public int CallCount;

            public float? GroundAt(float x, float z, float refY, float maxDist) => -100000f; // never lands mid-probe
            public float CameraDistanceTo(Vector3 point) => 50f;
            public float SoundVolume() => 0.7f;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void Explosion(Vector3 at, float radius) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public void Wave(Vector3 at, float radius) { }
            public void StampScorch(Vector3 at, float radius) { }
            public void OnShake(float power, float x, float z) { }
            public void DustBurst(Vector3 at, int count, float radius) { }
            public bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed)
            { destroyed = false; return false; }
            public void SetCollidersEnabled(Transform wrapper, bool enabled_) { }

            public bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit)
            {
                CallCount++;
                CapturedOrigin = origin;
                CapturedDir = dir;
                CapturedMaxDist = maxDist;
                hit = default;
                return false; // no real geometry needed — only the ARGUMENTS matter for this probe
            }
        }

        const float Dt = 1f / 60f;

        public static void Run()
        {
            var world = new CaptureWorld();
            var sys = new PlanePhysicsSystem(world);

            var wrapperGo = new GameObject("sweepOriginWreck");
            // Fast enough (V > Sweep's 5 m/s gate, src/planephys.js:867-868
            // `if (V < 5) return;`) and a long enough step that prevY and the
            // post-move position are unmistakably different points, the way
            // a tumbling multi-tonne wreck's real per-substep displacement is.
            wrapperGo.transform.position = new Vector3(0f, 500f, 0f);

            var rec = new PlaneRecord
            {
                Wrapper = wrapperGo.transform,
                Def = new PlaneDef { Mass = 3400f },
                Dead = false,
                Span = 11f, Len = 9f, Height = 3f, NoseLen = 2.6f,
            };
            var body = sys.MakeLoose(rec);
            body.Vel = new Vector3(0f, 0f, 200f); // straight down +Z, fast

            Vector3 posBefore = body.Obj.position;
            sys.Update(Dt);
            Vector3 prevAfter = body.Prev;         // frozen at Step()'s pre-move position
            Vector3 posAfter = body.Obj.position;  // moved this step

            bool moved = (posAfter - posBefore).sqrMagnitude > 0.01f;
            bool prevMatchesPreMove = (prevAfter - posBefore).sqrMagnitude < 1e-6f;
            bool originCaptured = world.CapturedOrigin.HasValue;
            Vector3 origin = world.CapturedOrigin ?? Vector3.positiveInfinity;
            float distOriginToPrev = (origin - prevAfter).magnitude;
            float distOriginToPost = (origin - posAfter).magnitude;
            bool originMatchesPrev = distOriginToPrev < 1e-4f;
            bool originMatchesPost = distOriginToPost < 1e-4f;

            Debug.Log($"SWEEPORIGIN rayCalls={world.CallCount} moved={LBool(moved)} prevMatchesPreMove={LBool(prevMatchesPreMove)} "
                + $"originCaptured={LBool(originCaptured)} origin=({origin.x:F6},{origin.y:F6},{origin.z:F6}) "
                + $"prev=({prevAfter.x:F6},{prevAfter.y:F6},{prevAfter.z:F6}) post=({posAfter.x:F6},{posAfter.y:F6},{posAfter.z:F6}) "
                + $"distOriginToPrev={distOriginToPrev:F6} distOriginToPost={distOriginToPost:F6} "
                + $"originMatchesPrev={LBool(originMatchesPrev)} originMatchesPost={LBool(originMatchesPost)}");

            UnityEngine.Object.DestroyImmediate(wrapperGo);
            ProbeExit.Exit(0);
        }
    }
}
