using TbilisiFly.UI.Target;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Ground truth for TargetRingView.cs / TargetRingMath — the WoW selection
    /// ring target.js:256-298 builds (TargetCardView.cs's own header lists this
    /// as the one thing NOT ported until now). Exercises the pure math
    /// directly (no scene/GameObject needed for most of it) plus one real
    /// Mesh build, so this runs standalone under -batchmode -nographics.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.EditorTools.TargetRingProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class TargetRingProbe
    {
        /// <summary>Standalone entry point (exits the editor on completion).
        /// DetailProbe.cs calls <see cref="Print"/> directly instead, so
        /// several probes' output can share one editor process/log.</summary>
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            // RingRadius: target.js:317 Math.max(3, (halfSpan ?? 6) * 1.06)
            foreach (var (id, halfSpan) in new (string, float?)[]
                     { ("p51", 5.6f), ("an225", 44.2f), ("nullDefault", null), ("tiny", 1.0f) })
            {
                float r = TargetRingMath.RingRadius(halfSpan);
                Debug.Log($"RINGRADIUS_{id} halfSpan={(halfSpan.HasValue ? halfSpan.Value.ToString("F2") : "null")} r={r:F4}");
            }

            // RingBandWidth: target.js:270 clamp(r*0.06, 0.35, 1.6)
            foreach (float r in new[] { 3f, 10f, 20f, 50f, 100f })
                Debug.Log($"BANDWIDTH_r{r:F0} r={r:F2} w={TargetRingMath.RingBandWidth(r):F4}");

            // NeedsRebuild: target.js:268 abs(cached-new) > 0.05
            Debug.Log($"NEEDSREBUILD_small {TargetRingMath.NeedsRebuild(5.60f, 5.61f)}");  // false
            Debug.Log($"NEEDSREBUILD_big {TargetRingMath.NeedsRebuild(5.60f, 6.00f)}");    // true

            // ComputeRingY: target.js:296 (floor ? floor.point.y : at.y - WHEEL) + 0.1
            // RingBandWidth/NeedsRebuild/PulseOpacity were implemented in
            // TargetRingView.cs but never PRINTED, so tools/unity_port_check.py
            // paired 0 of their values and the unit read "verified" on nothing.
            // Key shapes match tools/_probe_targetring.mjs exactly.
            foreach (float r in new[] { 3f, 10f, 20f, 50f, 100f })
                Debug.Log($"BANDWIDTH_r{r:F0} r={r:F2} w={TargetRingMath.RingBandWidth(r):F4}");
            Debug.Log($"NEEDSREBUILD_small {TargetRingMath.NeedsRebuild(5.6f, 5.61f).ToString().ToLowerInvariant()}");
            Debug.Log($"NEEDSREBUILD_big {TargetRingMath.NeedsRebuild(5.6f, 6.0f).ToString().ToLowerInvariant()}");
            Debug.Log($"PULSE_dead {TargetRingMath.PulseOpacity(true, 7.3f):F4}");
            foreach (float t in new[] { 0f, 0.5f, 1.0f, 2.0f })
                Debug.Log($"PULSE_t{t:F1} {TargetRingMath.PulseOpacity(false, t):F4}");

            Debug.Log($"RINGY_withFloor {TargetRingMath.ComputeRingY(440.3f, 445.0f):F4}"); // 440.4
            Debug.Log($"RINGY_noFloor {TargetRingMath.ComputeRingY(null, 445.0f):F4}");     // 445.0-1.7+0.1=443.4

            // PulseOpacity: target.js:319 dead ? 0.25 : 0.45 + 0.25*sin(t*3)
            Debug.Log($"PULSE_dead {TargetRingMath.PulseOpacity(true, 7.3f):F4}");
            foreach (float t in new[] { 0f, 0.5f, 1.0f, 2.0f })
                Debug.Log($"PULSE_t{t:F1} {TargetRingMath.PulseOpacity(false, t):F4}");

            // RingColor: target.js:320
            var alive = TargetRingMath.RingColor(false);
            var dead = TargetRingMath.RingColor(true);
            Debug.Log($"RINGMESH_shape segments=72 expectedVerts={(72 + 1) * 2} expectedTriIndices={72 * 6}");
            Debug.Log($"RINGCOLOR_alive r={alive.r} g={alive.g} b={alive.b}"); // 3f d6 ff
            Debug.Log($"RINGCOLOR_dead r={dead.r} g={dead.g} b={dead.b}");     // ff 55 66

            // BuildRingMesh: real Mesh, check vertex/triangle counts and radii
            // against a hand re-derivation (segments=72 -> 73*2=146 verts,
            // 72*6=432 triangle indices).
            foreach (float r in new[] { 5.6f, 44.2f })
            {
                var mesh = TargetRingMath.BuildRingMesh(r);
                var b = mesh.bounds;
                Debug.Log($"RINGMESH_r{r:F1} vertCount={mesh.vertexCount} triIndices={mesh.triangles.Length} " +
                          $"boundsSize={b.size:F3} expectedOuterDiameter={(r * 2f):F3}");
            }

            // TargetRingView end-to-end: a real GameObject, Show/Hide via
            // Update(), own-machine and fleet-hidden short-circuits.
            var view = new TargetRingView(null);
            var snapOwn = new RingSnapshot { IsOwn = true, FleetVisible = true, WorldPos = Vector3.zero, Dead = false };
            view.Update(snapOwn, 0f);
            Debug.Log($"VIEW_ownHidden activeSelf={view.Root.activeSelf}"); // expect False

            var snapHiddenFleet = new RingSnapshot { IsOwn = false, FleetVisible = false, WorldPos = Vector3.zero, Dead = false };
            view.Update(snapHiddenFleet, 0f);
            Debug.Log($"VIEW_fleetHidden activeSelf={view.Root.activeSelf}"); // expect False

            var snapLive = new RingSnapshot
            {
                IsOwn = false, FleetVisible = true, WorldPos = new Vector3(100f, 445f, -200f),
                HalfSpan = 11.2f, ProbedFloorY = 444.5f, Dead = false,
            };
            view.Update(snapLive, 1.0f);
            Debug.Log($"VIEW_live activeSelf={view.Root.activeSelf} pos={view.Root.transform.position:F3} " +
                      $"meshVerts={view.Root.GetComponent<MeshFilter>().sharedMesh.vertexCount}");

            view.Dispose();
            Debug.Log($"VIEW_disposed rootNull={(view.Root == null)}");
        }
    }
}
