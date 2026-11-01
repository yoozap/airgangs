using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// DID THE PROPELLER PIPELINE ACTUALLY RUN, ON THE REAL SHIPPED ASSETS?
    /// The Unity-side counterpart of tools/_probe_props.mjs ("Did the
    /// propeller detector actually find a propeller on every aeroplane that
    /// has one?"), printed in the same one-row-per-aeroplane shape so the two
    /// can be diffed by id.
    ///
    /// This is deliberately NOT another test of PropellerSystem's maths —
    /// Editor/Probes/FlightRestProbe.cs already exercises TagPropellers
    /// against synthetic data, and that is exactly the kind of probe
    /// .claude/skills/unity/SKILL.md warns proves "a CALCULATION, not that it
    /// is REACHED". Every row below comes from instantiating the REAL
    /// Assets/Resources/Art/Planes/&lt;id&gt;.fbx the way Flight/PlaneHallFill.cs does,
    /// running the production path (PlanePropellers.EnsureBuilt ->
    /// PlaneProps.Build), and then reading real components off the real
    /// GameObject: how many pivots carry Combat.SpinningPartTag, how many
    /// have a blur-disc child, how many blade renderers came out.
    ///
    /// PROPS id=... want=N found=N tagged=N discs=N blades=N how=... r=...
    /// PROPSDIAG id=... (raw measurement, for a row that disagrees)
    /// PROPSUM planes=N found=N want=N missing=...
    /// </summary>
    public static class PlanePropsProbe
    {
        public static void Run()
        {
            PlaneProps.ClearCache();
            int planes = 0, found = 0, want = 0;
            var missing = new List<string>();

            foreach (var def in Planes.All)
            {
                if (!def.Prop) continue;
                planes++;
                want += Mathf.Max(1, def.Props);

                string path = def.Id == "spitfire"
                    ? PlaneHallFill.SpitfireObjPath
                    : PlaneHallFill.PlanesFbxDir + def.Id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"PROPS id={def.Id} asset=missing"); missing.Add(def.Id); continue; }

                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                // THE NATURAL IMPORT TRANSFORM, read BEFORE anything resets it
                // — .claude/skills/unity/SKILL.md's "never reset an FBX's
                // natural import transform to identity" (crane_machine.fbx
                // imports at scale 100 / rotation (0,180,180)). Printed for
                // every airframe so the question is settled by measurement
                // rather than assumed per-asset: t28.fbx is the one that does
                // NOT import at identity.
                Debug.Log($"PROPSNAT id={def.Id} naturalRot={inner.transform.localRotation.eulerAngles} " +
                          $"naturalScale={inner.transform.localScale} defScale={def.Scale}");

                // Exactly PlaneHallFill's own holder-plus-prefab shape, so a
                // row here means the same thing a parked machine does.
                var go = new GameObject(def.Id);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = def.Scale > 0f ? Vector3.one * def.Scale : Vector3.one;
                inner.transform.SetParent(go.transform, false);

                string diag = PlaneProps.Diagnose(go, def);

                var comp = go.AddComponent<PlanePropellers>();
                comp.PlaneId = def.Id;
                comp.EnsureBuilt();

                // Real components on a real object — not a count this probe computed.
                int tagged = go.GetComponentsInChildren<TbilisiFly.Combat.SpinningPartTag>(true).Length;
                int discs = 0, blades = 0;
                var radii = new List<string>();
                foreach (var inst in comp.Props)
                {
                    if (inst == null) continue;
                    if (inst.Disc != null) discs++;
                    blades += inst.Blades != null ? inst.Blades.Length : 0;
                    radii.Add(inst.Radius.ToString("F2"));
                }
                var plan = PlaneProps.CachedPlan(def.Id);
                found += comp.Count;
                if (comp.Count < Mathf.Max(1, def.Props)) missing.Add($"{def.Id}:{comp.Count}/{Mathf.Max(1, def.Props)}");

                Debug.Log($"PROPS id={def.Id} want={Mathf.Max(1, def.Props)} found={comp.Count} " +
                          $"tagged={tagged} discs={discs} blades={blades} " +
                          $"how={(plan != null ? plan.How : "?")} r=[{string.Join(" ", radii)}] " +
                          $"ms={(plan != null ? plan.Millis : 0f):F0}");
                Debug.Log($"PROPSDIAG {diag}");

                Object.DestroyImmediate(go);
            }

            Debug.Log($"PROPSUM planes={planes} found={found} want={want} " +
                      $"missing={missing.Count}{(missing.Count > 0 ? " ids=" + string.Join(",", missing) : "")}");
        }

        // =================================================================
        // WHICH WAY DO THE AIRFRAMES POINT? Measured on all 35 real assets.
        // =================================================================
        /// <summary>
        /// The fleet-wide settlement of the question Flight/PlaneProps.cs's
        /// FileNose answered for four hinted models: does an aeroplane FBX
        /// arrive in Unity nose-forward (+Z, as Flight/FlightModel.cs's
        /// handedness header assumes) or nose-aft (-Z, as the GLB had it)?
        ///
        /// Measured, not reasoned, and from a signal that owes nothing to the
        /// propeller hints: **the fin**. Every end of an airframe's horizontal
        /// bounding box is one of four things — a wingtip, a wingtip, the nose
        /// or the TAIL — and the tail is the only one that reaches for the top
        /// of the box, because that is where the vertical stabiliser is. So for
        /// each of the four ends this prints the outer slab's height and width,
        /// picks the tallest as the tail, and reports the opposite end as the
        /// nose. The fuselage axis falls out of the same answer, and the
        /// remaining horizontal axis is the span.
        ///
        /// Three independent cross-checks ride along on each row, so a wrong
        /// call cannot look right:
        ///   * defSpan/defLen — the REAL published figures in PlaneCatalog.cs.
        ///     The airframes are at real span, so the measured extent along the
        ///     span axis must match defSpan and the one along the fuselage axis
        ///     must match defLen.
        ///   * cockpit=  — PlaneCockpitMeasurement.Measure, whose search window
        ///     is src/planes.js:1019-1020's `air.min.z + len*0.05 .. 0.78`, i.e.
        ///     measured FROM THE -Z END. It can only come back sane on a model
        ///     whose nose is at -Z; a +Z-nosed model would have it hunting for a
        ///     canopy in the tail cone.
        ///   * fileNose= — what Flight/PlaneProps.FileNose believes, so the
        ///     propeller path and this measurement are visibly the same claim.
        ///
        /// AXIS id=... noseAlong=-Z spanAlong=X ...
        /// AXISDIAG id=... (the four end slabs, raw)
        /// AXISSUM planes=N negZ=N posZ=N other=N cockpitSane=N yawDefs=...
        /// </summary>
        /// <summary>Both halves in ONE editor launch (the project lock is
        /// exclusive and shared with live agents — see .claude/skills/unity/
        /// SKILL.md). Axis() settles which way the airframes point; Run()
        /// re-measures the propeller pipeline against that answer, so the
        /// PROPSDIAG hub counts and the AXIS rows can be read as one
        /// statement instead of two runs apart.</summary>
        public static void AxisAndProps()
        {
            ProbeExit.Suppress = true;
            Axis();
            Run();
            ProbeExit.Suppress = false;
        }

        public static void Axis()
        {
            int planes = 0, negZ = 0, posZ = 0, other = 0, sane = 0, spanX = 0;
            var yawDefs = new List<string>();
            var mismatch = new List<string>();

            foreach (var def in Planes.All)
            {
                string path = def.Id == "spitfire"
                    ? PlaneHallFill.SpitfireObjPath
                    : PlaneHallFill.PlanesFbxDir + def.Id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"AXIS id={def.Id} asset=missing"); continue; }

                // EXACTLY PlaneHallFill's holder-plus-prefab shape: the prefab
                // keeps its NATURAL import transform as its local one (t28.fbx
                // imports at rotation (270.02,0,0) / scale 100) and the holder
                // carries only def.Scale. Measuring the prefab root directly
                // would throw the natural transform away, which is the trap
                // .claude/skills/unity/SKILL.md names.
                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var go = new GameObject(def.Id);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = def.Scale > 0f ? Vector3.one * def.Scale : Vector3.one;
                inner.transform.SetParent(go.transform, false);

                // MEASURED IN THE HOLDER'S LOCAL SPACE, i.e. BEFORE def.Scale
                // (which the holder carries) and AFTER the natural import
                // transform (which the prefab carries). That is deliberate —
                // it is the frame PlaneProps/PropellerSystem work in, and it
                // is what makes t28's rot(270,0,0)/scale-100 import show up in
                // the numbers (12.20 m measured against a published 12.22).
                // The one consequence to remember when reading a row:
                // `spitfire` is the only def with a non-unit Scale (0.34,
                // src/planes.js:74 "33-unit OBJ -> ~11 m wingspan"), so its
                // sx=33.12 / span/def=2.95 is NOT an oversized asset — 33.12 *
                // 0.34 = 11.26 against a published 11.23. Everything else in
                // the table is Scale = 1 and reads directly.
                var pts = SkinVerts(go.transform);
                if (pts.Count == 0)
                {
                    Debug.Log($"AXIS id={def.Id} verts=0");
                    Object.DestroyImmediate(go);
                    continue;
                }
                planes++;

                Vector3 mn = pts[0], mx = pts[0];
                foreach (var v in pts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
                Vector3 size = mx - mn;

                // The four horizontal ends: (axis 0 = X, axis 2 = Z) x (-, +).
                // Outer 9% slab of that axis, which is enough to be past the
                // wing trailing edge at the tail and past the windscreen at the
                // nose on every airframe in the table.
                var endH = new float[4];
                var endW = new float[4];
                var endN = new int[4];
                var endYc = new float[4];
                var endPc = new float[4];
                var endAx = new float[4];   // mean coordinate ALONG the axis — the slab's station
                for (int e = 0; e < 4; e++)
                {
                    int ax = e < 2 ? 0 : 2;          // 0,1 -> X ; 2,3 -> Z
                    bool neg = (e % 2) == 0;
                    int perp = ax == 0 ? 2 : 0;
                    float slab = size[ax] * 0.09f;
                    float lim = neg ? mn[ax] + slab : mx[ax] - slab;
                    float ylo = float.MaxValue, yhi = float.MinValue;
                    float plo = float.MaxValue, phi = float.MinValue;
                    double ys = 0, ps = 0, axs = 0;
                    int n = 0;
                    foreach (var v in pts)
                    {
                        if (neg ? v[ax] > lim : v[ax] < lim) continue;
                        n++;
                        if (v.y < ylo) ylo = v.y; if (v.y > yhi) yhi = v.y;
                        if (v[perp] < plo) plo = v[perp]; if (v[perp] > phi) phi = v[perp];
                        ys += v.y; ps += v[perp]; axs += v[ax];
                    }
                    endH[e] = n > 0 ? yhi - ylo : 0f;
                    endW[e] = n > 0 ? phi - plo : 0f;
                    endN[e] = n;
                    endYc[e] = n > 0 ? (float)(ys / n) : 0f;
                    endPc[e] = n > 0 ? (float)(ps / n) : 0f;
                    endAx[e] = n > 0 ? (float)(axs / n) : 0f;
                }

                // TWO SEPARATE QUESTIONS, and conflating them cost two
                // iterations of this probe. (1) WHICH horizontal axis is the
                // fuselage? (2) which END of it is the nose?
                //
                // (1) THE FUSELAGE AXIS, from the published figures. These
                // airframes are at REAL span (docs: "airframes at REAL span"),
                // so the axis whose measured extent matches def.Len rather
                // than def.Span is the fuselage. That beats "the longer axis":
                // the Lancaster is 31.1 m across and 21.2 m long, and the
                // XB-35 is 52 m across and 16 m long. For the 7 defs with no
                // published figures, fall back to the end-slab width
                // ASYMMETRY: the two wingtip slabs are near-identical by
                // construction (f104 measures 5.26 / 5.26) while nose and tail
                // differ by a factor of several (f104: 0.09 / 3.53), so the
                // axis with the larger max/min width ratio is the fuselage.
                int lenAx;
                string axFrom;
                if (def.Span.HasValue && def.Len.HasValue && def.Span.Value > 0f && def.Len.Value > 0f)
                {
                    float zAsLen = Mathf.Abs(size.z / def.Len.Value - 1f) + Mathf.Abs(size.x / def.Span.Value - 1f);
                    float xAsLen = Mathf.Abs(size.x / def.Len.Value - 1f) + Mathf.Abs(size.z / def.Span.Value - 1f);
                    lenAx = zAsLen <= xAsLen ? 2 : 0;
                    axFrom = "published";
                }
                else
                {
                    float rx = Mathf.Max(endW[0], endW[1]) / Mathf.Max(1e-4f, Mathf.Min(endW[0], endW[1]));
                    float rz = Mathf.Max(endW[2], endW[3]) / Mathf.Max(1e-4f, Mathf.Min(endW[2], endW[3]));
                    lenAx = rz >= rx ? 2 : 0;
                    axFrom = "asymmetry";
                }
                int spanAx = lenAx == 0 ? 2 : 0;
                int eNeg = lenAx == 0 ? 0 : 2, ePos = lenAx == 0 ? 1 : 3;

                // (2) WHICH END IS THE NOSE — the cockpit, which is what the
                // brief's own rule names and the only signal here that no
                // other part of the aeroplane imitates. The canopy/glass mesh
                // is matched by the same name set
                // Flight/PlaneCockpitMeasurement.cs's CanopyRx uses (that field
                // is private, so it is restated rather than reached into), and
                // its centroid is compared against the airframe's mid-station:
                // on every aeroplane in this table the cockpit is forward of
                // centre, so the nose is the end it leans toward.
                //
                // The fallback, where no mesh is named like a canopy, is the
                // NARROWER end slab = the nose (the horizontal stabiliser is
                // 25-45% of the wingspan and sits inside the last tenth of the
                // length; measured tail slabs land on published tailplane
                // spans — il76 17.47 m against a published 17.4, p51 3.88
                // against 3.9). That rule alone is fooled by a propeller disc
                // at the nose, which is exactly why the canopy is tried first.
                float canCen = 0f; int canN = 0;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!CanopyRx.IsMatch(r.name)) continue;
                    var c = go.transform.InverseTransformPoint(r.bounds.center);
                    canCen += c[lenAx]; canN++;
                }
                float mid = (mn[lenAx] + mx[lenAx]) * 0.5f;
                bool noseNeg;
                string noseFrom;
                if (canN > 0 && Mathf.Abs(canCen / canN - mid) > size[lenAx] * 0.02f)
                { noseNeg = canCen / canN < mid; noseFrom = $"canopy({canN})"; }
                else
                { noseNeg = endW[eNeg] < endW[ePos]; noseFrom = "slabwidth"; }

                string noseAlong = (noseNeg ? "-" : "+") + (lenAx == 0 ? "X" : "Z");
                string spanAlong = spanAx == 0 ? "X" : "Z";

                if (noseAlong == "-Z") negZ++; else if (noseAlong == "+Z") posZ++; else other++;
                if (spanAlong == "X") spanX++;

                // Cross-check 1: the real published figures.
                float mSpan = size[spanAx], mLen = size[lenAx];
                string fit = "";
                if (def.Span.HasValue) fit += $" span/def={mSpan / def.Span.Value:F2}";
                if (def.Len.HasValue) fit += $" len/def={mLen / def.Len.Value:F2}";

                // Cross-check 2: the -Z-anchored cockpit search.
                string ck = "n/a"; float seatZ = 0f;
                try
                {
                    var r = PlaneCockpitMeasurement.Measure(go.transform, def.Cockpit);
                    ck = (r != null && r.Sane) ? "sane" : "insane";
                    if (r != null) { seatZ = r.Seat.z; if (r.Sane) sane++; }
                }
                catch (System.Exception ex) { ck = "threw:" + ex.GetType().Name; }

                // Cross-check 3: what the propeller path already believes.
                Vector3 fn = PlaneProps.FileNose(def);

                if (def.Yaw != 0f || def.Roll != 0f)
                    yawDefs.Add($"{def.Id}(yaw={def.Yaw:F3},roll={def.Roll:F3})");
                if (noseAlong != "-Z" && def.Yaw == 0f && def.Roll == 0f)
                    mismatch.Add($"{def.Id}:{noseAlong}");

                Debug.Log($"AXIS id={def.Id} noseAlong={noseAlong} spanAlong={spanAlong} " +
                          $"axFrom={axFrom} noseFrom={noseFrom} " +
                          $"sx={size.x:F2} sy={size.y:F2} sz={size.z:F2}" +
                          $" defSpan={(def.Span.HasValue ? def.Span.Value.ToString("F2") : "-")}" +
                          $" defLen={(def.Len.HasValue ? def.Len.Value.ToString("F2") : "-")}{fit}" +
                          $" cockpit={ck} seatZ={seatZ:F2}" +
                          $" fileNose=({fn.x:F2},{fn.y:F2},{fn.z:F2}) defYaw={def.Yaw:F3} defRoll={def.Roll:F3}");
                Debug.Log($"AXISDIAG id={def.Id} verts={pts.Count} " +
                          $"minZ={mn.z:F2} maxZ={mx.z:F2} minX={mn.x:F2} maxX={mx.x:F2} " +
                          $"endNegX=h{endH[0]:F2}/w{endW[0]:F2}/n{endN[0]} " +
                          $"endPosX=h{endH[1]:F2}/w{endW[1]:F2}/n{endN[1]} " +
                          $"endNegZ=h{endH[2]:F2}/w{endW[2]:F2}/n{endN[2]} " +
                          $"endPosZ=h{endH[3]:F2}/w{endW[3]:F2}/n{endN[3]} " +
                          $"nat=rot{inner.transform.localRotation.eulerAngles}/scale{inner.transform.localScale}");
                // The slab CENTROIDS, which is what lets a row be checked
                // against a number from the asset pipeline rather than against
                // a tolerance: src/planes.js:239 authors the P-51's propeller
                // hub at (r, u, f) = (0, 1.57, 4.95) — model-local metres,
                // measured on the GLB — and the aeroplane ships a bare spinner
                // there and no blades. So the P-51's nose slab must come back
                // centred on x~0, y~1.57 and |z|~4.95, and WHICH SIGN that z
                // has is the whole handedness question, answered by a constant
                // the runtime had no way to know.
                Debug.Log($"AXISCEN id={def.Id} " +
                          $"negX=(ax{endAx[0]:F2},y{endYc[0]:F2},p{endPc[0]:F2}) " +
                          $"posX=(ax{endAx[1]:F2},y{endYc[1]:F2},p{endPc[1]:F2}) " +
                          $"negZ=(ax{endAx[2]:F2},y{endYc[2]:F2},p{endPc[2]:F2}) " +
                          $"posZ=(ax{endAx[3]:F2},y{endYc[3]:F2},p{endPc[3]:F2})");

                Object.DestroyImmediate(go);
            }

            Debug.Log($"AXISSUM planes={planes} noseNegZ={negZ} nosePosZ={posZ} noseOther={other} " +
                      $"spanAlongX={spanX} cockpitSane={sane} " +
                      $"unexplained={mismatch.Count}{(mismatch.Count > 0 ? " ids=" + string.Join(",", mismatch) : "")} " +
                      $"orientationFixDefs={yawDefs.Count} [{string.Join(" ", yawDefs)}]");
        }

        /// <summary>The same name set Flight/PlaneCockpitMeasurement.cs's
        /// CanopyRx matches (src/planes.js:1033's own list — these models come
        /// from a dozen authors in as many languages, which is why it is this
        /// long). That field is private, and widening a production member for
        /// a probe is the thing AssemblyInfo.cs's InternalsVisibleTo exists to
        /// avoid, so it is restated here instead; if the production list ever
        /// changes, this is a second copy to update.</summary>
        static readonly System.Text.RegularExpressions.Regex CanopyRx =
            new System.Text.RegularExpressions.Regex(
                "glass|canopy|cocpit|cockpit|kabina|windscreen|window|vidrio|cristal|staklo|^cam|_cam|hood",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Every renderable vertex under `root`, in root-local space.
        /// Static meshes through their MeshFilter, rigged ones through the
        /// SkinnedMeshRenderer's shared mesh (neonfly and carpet are rigged —
        /// src/planes.js:816-819).</summary>
        static List<Vector3> SkinVerts(Transform root)
        {
            var outp = new List<Vector3>();
            var w2l = root.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = w2l * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) outp.Add(m.MultiplyPoint3x4(v));
            }
            foreach (var sk in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sk.sharedMesh == null) continue;
                // BakeMesh, not sharedMesh.vertices: a skinned mesh's stored
                // vertices are in BIND space, which for these two rigged
                // models is not the renderer's own space. The bake applies the
                // current (bind) pose and hands back the renderer's local
                // space, which composing with its transform then puts in
                // root-local space like everything else.
                var baked = new Mesh();
                sk.BakeMesh(baked);
                var m = w2l * sk.transform.localToWorldMatrix;
                foreach (var v in baked.vertices) outp.Add(m.MultiplyPoint3x4(v));
                Object.DestroyImmediate(baked);
            }
            return outp;
        }
    }
}
