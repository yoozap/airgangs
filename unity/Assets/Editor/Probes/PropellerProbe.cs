using System.Collections.Generic;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PropellerSystem.cs reproduces src/propeller.js's EXPORTED
    /// functions (tagPropellers, spinPropellers) on synthetic geometry built
    /// from plain vertex/triangle arrays — see tools/_probe_propeller.mjs for
    /// why (no THREE.BoxGeometry/Unity-primitive-specific triangulation to
    /// disagree about; the box/blade generators here are line-for-line the
    /// same arithmetic as that script's).
    ///
    /// NOTE ON THE PUSHER4 CASE and bladeCount: this test's 4-blade rig places
    /// blade root corners at exactly-coincident points for blades 180 degrees
    /// apart (an artifact of the symmetric box geometry chosen, not a general
    /// property of real aircraft), so ShellsOf's vertex welding (WELD = 2mm)
    /// merges opposite-angle blade pairs into one shell BEFORE clustering ever
    /// runs. The real src/propeller.js does the same thing to the same input
    /// (see the JS probe's printed `found`/`blades` for this case) — this is
    /// exercising the welding pathway faithfully, not a bug to "fix".
    ///
    /// NOTE ON HANDEDNESS: BasisOf's two cross products are swapped per
    /// FlightModel.cs's rule (see PropellerSystem.cs's file header, point 3).
    /// This probe CANNOT prove that swap was the right call: hub/radius/
    /// bladeCount/pusher are all computed by projecting onto (r,u,f) and then
    /// reconstructing with the SAME basis, so the round-trip is self-
    /// consistent regardless of which way r/u point — the swap would only
    /// become observable if some caller compared r/u against an INDEPENDENT
    /// reference (e.g. "is this the left or right wing"), which
    /// tagPropellers/spinPropellers never do. The swap is kept because it is
    /// the analytically correct derivation, not because this probe falsifies
    /// its absence — see the port's final report for the honest version of
    /// this caveat.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PropellerProbe.Run
    /// </summary>
    public static class PropellerProbe
    {
        // ---- deterministic box: 8 verts, 12 tris, explicit winding ----
        // IDENTICAL corner order and triangle list to tools/_probe_propeller.mjs's
        // boxVerts/BOX_TRIS, so there is no library-specific triangulation to
        // disagree about.
        static Vector3[] BoxVerts(float hx, float hy, float hz) => new[]
        {
            new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
            new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz),
        };
        static readonly int[] BoxTris =
        {
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 5, 1, 0, 4, 5,
            3, 2, 6, 3, 6, 7,
            0, 3, 7, 0, 7, 4,
            1, 5, 6, 1, 6, 2,
        };

        static void AddBox(List<Vector3> verts, List<int> tris, Vector3 c, float hx, float hy, float hz)
        {
            int baseI = verts.Count;
            foreach (var v in BoxVerts(hx, hy, hz)) verts.Add(v + c);
            foreach (var i in BoxTris) tris.Add(baseI + i);
        }

        // A blade: a box LW x LL x LT with one end at the hub (spans local Y
        // in [0, LL]), rotated by `angle` about Z directly on the vertex
        // coordinates — same construction as the JS probe's addBlade.
        static void AddBlade(List<Vector3> verts, List<int> tris, Vector3 hub, float angle, float lw, float ll, float lt)
        {
            int baseI = verts.Count;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            foreach (var v0 in BoxVerts(lw / 2f, ll / 2f, lt / 2f))
            {
                float x = v0.x, y = v0.y + ll / 2f;
                float xr = x * c - y * s, yr = x * s + y * c;
                verts.Add(new Vector3(hub.x + xr, hub.y + yr, hub.z + v0.z));
            }
            foreach (var i in BoxTris) tris.Add(baseI + i);
        }

        static List<MeshEntry> BuildRig(Vector3 hub, int n, bool pusher, out Vector3 noseOut)
        {
            noseOut = new Vector3(0, 0, -1);
            const float ll = 1.0f, lw = 0.15f, lt = 0.08f;

            var bladeV = new List<Vector3>(); var bladeT = new List<int>();
            for (int i = 0; i < n; i++) AddBlade(bladeV, bladeT, hub, (i / (float)n) * Mathf.PI * 2f, lw, ll, lt);
            var bladeEntry = new MeshEntry { Positions = bladeV.ToArray(), Tri = bladeT.ToArray() };

            var fusV = new List<Vector3>(); var fusT = new List<int>();
            float fz = pusher ? hub.z - 3.15f : hub.z + 3.15f;
            AddBox(fusV, fusT, new Vector3(hub.x, hub.y, fz), 0.35f, 0.35f, 2.85f);
            var fusEntry = new MeshEntry { Positions = fusV.ToArray(), Tri = fusT.ToArray() };

            return new List<MeshEntry> { bladeEntry, fusEntry };
        }

        public static void Run()
        {
            var cases = new (string id, Vector3 hub, int n, bool pusher)[]
            {
                ("tractor3", new Vector3(0, 0, 0), 3, false),
                ("tractor2", new Vector3(0, 0, 0), 2, false),
                ("pusher4", new Vector3(0, 0, 0), 4, true),
                ("offsetHub3", new Vector3(1.5f, 0.8f, -4.2f), 3, false),
            };

            foreach (var (id, hub, n, pusher) in cases)
            {
                var entries = BuildRig(hub, n, pusher, out var nose);
                var found = PropellerSystem.SearchForPropellers(entries, nose, 1);
                if (found.Count == 0)
                {
                    Debug.Log($"PROP id={id} found=0");
                    continue;
                }
                var p = found[0];
                Debug.Log($"PROP id={id} found={found.Count} " +
                          $"hubX={p.Hub.x:F5} hubY={p.Hub.y:F5} hubZ={p.Hub.z:F5} " +
                          $"radius={p.Radius:F5} blades={p.BladeCount}");
            }

            // ---- FinishPropeller: radius fallback, size guard, disc
            // orientation, idempotency (src/propeller.js:1042-1081) ----
            // `pivot` here is a REAL Transform with a real child Renderer,
            // unlike the data-only MeshEntry rigs above -- see
            // PropellerSystem.FinishPropeller's own doc comment for why that
            // is exactly the case this method is meant for.
            {
                // deliberately not axis-aligned and not antiparallel to +Z,
                // so the disc quaternion (case E) has one unique answer and
                // isn't exercising a 180-degree degenerate edge case that
                // THREE's/Unity's setFromUnitVectors/FromToRotation could
                // legitimately resolve differently.
                var nose2 = new Vector3(0.3f, 0.1f, -0.9f).normalized;

                // A: no radius given (0) -> falls back to the pivot's own
                // rendered bounds. A 3 x 0.2 x 1 box's largest axis is 3, so
                // the fallback radius is 1.5.
                {
                    var pivotGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pivotGO.transform.position = Vector3.zero;
                    pivotGO.transform.rotation = Quaternion.identity;
                    pivotGO.transform.localScale = new Vector3(3f, 0.2f, 1f);
                    var inst = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0f);
                    var blades = PropellerSystem.BladesExcludingDisc(pivotGO.transform, inst.Disc);
                    Debug.Log($"FINISH id=bboxFallback hasDisc={inst.HasDisc.ToString().ToLowerInvariant()} " +
                              $"radius={inst.Radius:F5} blades={blades.Length}");
                    Object.DestroyImmediate(pivotGO);
                }

                // B: the fallback box itself is tiny -> largest axis 0.08m,
                // radius 0.04m, under the 0.05m guard -> no disc.
                {
                    var pivotGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pivotGO.transform.position = Vector3.zero;
                    pivotGO.transform.rotation = Quaternion.identity;
                    pivotGO.transform.localScale = new Vector3(0.08f, 0.01f, 0.01f);
                    var inst = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0f);
                    Debug.Log($"FINISH id=bboxTooSmall hasDisc={inst.HasDisc.ToString().ToLowerInvariant()}");
                    Object.DestroyImmediate(pivotGO);
                }

                // C: radius already explicitly given, small -> the fallback
                // is SKIPPED entirely; a big box here must not rescue it.
                {
                    var pivotGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pivotGO.transform.position = Vector3.zero;
                    pivotGO.transform.rotation = Quaternion.identity;
                    pivotGO.transform.localScale = new Vector3(5f, 5f, 5f);
                    var inst = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0.02f);
                    Debug.Log($"FINISH id=explicitTooSmall hasDisc={inst.HasDisc.ToString().ToLowerInvariant()}");
                    Object.DestroyImmediate(pivotGO);
                }

                // D: idempotency -- a second call with alreadyFinished=true
                // must not redo the radius/disc work (the caller is expected
                // to keep using the FIRST call's instance, same as the JS
                // leaving p.userData.radius alone on its own early `continue`).
                {
                    var pivotGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pivotGO.transform.position = Vector3.zero;
                    pivotGO.transform.rotation = Quaternion.identity;
                    pivotGO.transform.localScale = new Vector3(3f, 0.2f, 1f);
                    var inst1 = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0f);
                    float firstRadius = inst1.Radius;
                    var inst2 = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0f, alreadyFinished: true);
                    bool secondSkipped = !inst2.HasDisc;
                    Debug.Log($"FINISH id=idempotent firstRadius={firstRadius:F5} " +
                              $"secondSkipped={secondSkipped.ToString().ToLowerInvariant()}");
                    Object.DestroyImmediate(pivotGO);
                }

                // E: the disc's local orientation -- +Z maps onto the
                // measured spin axis.
                {
                    var pivotGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pivotGO.transform.position = Vector3.zero;
                    pivotGO.transform.rotation = Quaternion.identity;
                    pivotGO.transform.localScale = new Vector3(3f, 0.2f, 1f);
                    var inst = PropellerSystem.FinishPropeller(pivotGO.transform, nose2, 0f);
                    var d = inst.DiscLocalRotation;
                    Debug.Log($"FINISH id=discQuat qx={d.x:F6} qy={d.y:F6} qz={d.z:F6} qw={d.w:F6}");
                    Object.DestroyImmediate(pivotGO);
                }
            }

            // ---- spinPropellers equivalent: pure rotation/opacity math ----
            {
                var pivotGO = new GameObject("prop_pivot");
                var pivot = pivotGO.transform;
                var inst = new PropellerSystem.PropellerInstance
                {
                    Pivot = pivot,
                    SpinAxisLocal = pivot.InverseTransformDirection(new Vector3(0, 0, -1)).normalized,
                };
                var props = new List<PropellerSystem.PropellerInstance> { inst };
                var steps = new (float rpm, float dt)[] { (1200, 1 / 60f), (1200, 1 / 60f), (2400, 1 / 60f), (0, 1 / 60f), (700, 1 / 30f) };
                foreach (var (rpm, dt) in steps) PropellerSystem.SpinPropellers(props, rpm, dt);
                var e = pivot.localRotation.eulerAngles;
                // report as radians, wrapped to (-pi, pi], matching THREE's Euler().z convention
                float ez = e.z * Mathf.Deg2Rad;
                if (ez > Mathf.PI) ez -= 2f * Mathf.PI;
                var q = pivot.localRotation;
                // id=_spin: see the JS probe's comment — a line with no id
                // collides with every other id-less line under
                // unity_port_check.py's grouping key.
                Debug.Log($"SPIN id=_spin steps={steps.Length} eulerZ={ez:F6} " +
                          $"qx={q.x:F6} qy={q.y:F6} qz={q.z:F6} qw={q.w:F6}");
                Object.DestroyImmediate(pivotGO);
            }

            // ---- SpinPropellers: the disc must blink hidden below
            // BlurStart and show again past it (src/propeller.js:1109
            // `disc.visible = blur > 0.01;`) — this is the line that was
            // computed into a local `visible` bool and never applied to the
            // real Transform; see PropellerSystem.cs's SpinPropellers fix.
            {
                var planeGO = new GameObject("plane");
                var pivotGO = new GameObject("prop_pivot");
                pivotGO.transform.SetParent(planeGO.transform);
                var discGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
                discGO.transform.SetParent(pivotGO.transform);
                var discRenderer = discGO.GetComponent<Renderer>();
                var discMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                discRenderer.material = discMat;
                discRenderer.enabled = false;

                var inst = new PropellerSystem.PropellerInstance
                {
                    Pivot = pivotGO.transform,
                    SpinAxisLocal = new Vector3(0, 0, -1),
                    Disc = discGO.transform,
                    DiscMaterial = discMat,
                };
                var props = new List<PropellerSystem.PropellerInstance> { inst };

                PropellerSystem.SpinPropellers(props, 300f, 1f / 60f);   // below BlurStart (420) -- stays hidden
                Debug.Log($"SPIN id=discLowRpm visible={discRenderer.enabled.ToString().ToLowerInvariant()} " +
                          $"opacity={discMat.color.a:F6}");

                PropellerSystem.SpinPropellers(props, 1400f, 1f / 60f);  // past BlurFull (1100) -- fully shown
                Debug.Log($"SPIN id=discHighRpm visible={discRenderer.enabled.ToString().ToLowerInvariant()} " +
                          $"opacity={discMat.color.a:F6}");

                Object.DestroyImmediate(planeGO);
                Object.DestroyImmediate(discMat);
            }

            // ---- blur fraction at a few rpm ----
            int bi = 0;
            foreach (var rpm in new float[] { 0, 300, 420, 700, 1100, 1400 })
            {
                float blur = Mathf.Clamp01((rpm - PropellerSystem.BlurStart) / (PropellerSystem.BlurFull - PropellerSystem.BlurStart));
                Debug.Log($"BLUR id=blur{bi++} rpm={rpm} blur={blur:F6}");
            }

            // ---- blur-disc per-pixel formula ----
            const int S = 256;
            int pi = 0;
            foreach (var (i, j, blades) in new (int, int, int)[] { (128, 128, 3), (200, 128, 3), (230, 60, 4), (10, 10, 3), (128, 40, 2) })
            {
                float a = PropellerSystem.BlurDiscAlphaAt(i, j, S, blades);
                Debug.Log($"PIXEL id=px{pi++} i={i} j={j} blades={blades} a={a:F6}");
            }

            ProbeExit.Exit(0);
        }
    }
}
