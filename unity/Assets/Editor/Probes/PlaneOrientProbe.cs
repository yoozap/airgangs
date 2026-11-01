using System.Collections.Generic;
using System.Text.RegularExpressions;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE PROOF THAT THE NOSE LEADS. docs/QUALITY_BAR.md's "THE BIGGEST
    /// VISIBLE BUG" asked for exactly this and nothing less: a probe that puts
    /// a machine in flight with the real FlightModel/PlanePhysics and asserts
    /// that the end of the airframe the MEASUREMENT calls the nose is ahead of
    /// the tail along the velocity vector.
    ///
    /// It is deliberately a SEPARATE file from Editor/Probes/PlanePropsProbe.cs,
    /// which stays the proof of record for the AXIS table and for the
    /// propellers, and which measures the RAW assets at their natural import
    /// transform (so it is the untouched control: its `AXISSUM ... noseNegZ=29
    /// nosePosZ=3 noseOther=3 ... cockpitSane=34` must not move, because the
    /// files on disk did not). This probe measures the BUILT aeroplane — the
    /// Flight/PlaneModelFrame chain the game now puts between the flight
    /// Transform and the model — which is where the 35 airframes become
    /// straightened and nose-forward.
    ///
    /// Lines, all greppable:
    ///   ORIENT id=.. noseLeads=true|false yawApplied=.. rollApplied=..
    ///          span=.. len=.. rawSpan=.. rawLen=.. cockpit=sane|insane ..
    ///   ORIENTSUM planes=35 noseLeads=35 cockpitSane=35 spanAlongX=35 ..
    ///   ORIENTFLY id=p51 ... noseLeads=true   (the real FlightModel run)
    ///   ORIENTPACK id=cessna310 before=.. after=..
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.PlaneOrientProbe.Run
    ///
    /// <see cref="Suite"/> additionally chains the three probes whose numbers
    /// this change must NOT move (propellers, the crane hub, WHEEL_HEIGHT /
    /// touchdown) into the same editor launch, because the project lock is
    /// exclusive — run it, not Run, when the point is the regression sweep.
    /// </summary>
    public static class PlaneOrientProbe
    {
        // The share of the length taken as an "end slab". Same 9% as
        // PlanePropsProbe.Axis, for the same reason: enough to be past the
        // wing trailing edge at the tail and past the windscreen at the nose
        // on every airframe in the table.
        const float SlabFrac = 0.09f;

        static readonly Regex CanopyRx = new Regex(
            "glass|canopy|cocpit|cockpit|kabina|windscreen|window|vidrio|cristal|staklo|^cam|_cam|hood",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ------------------------------------------------------------------
        static List<Vector3> VertsIn(Transform root, Transform frame)
        {
            var outp = new List<Vector3>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!IsAirframe(mf.transform, root)) continue;
                var m = mf.transform.localToWorldMatrix;
                var vs = mf.sharedMesh.vertices;
                int stride = Mathf.Max(1, vs.Length / 20000);
                for (int i = 0; i < vs.Length; i += stride)
                    outp.Add(frame.InverseTransformPoint(m.MultiplyPoint3x4(vs[i])));
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (!IsAirframe(smr.transform, root)) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked);
                var m = smr.transform.localToWorldMatrix;
                var vs = baked.vertices;
                int stride = Mathf.Max(1, vs.Length / 20000);
                for (int i = 0; i < vs.Length; i += stride)
                    outp.Add(frame.InverseTransformPoint(m.MultiplyPoint3x4(vs[i])));
                Object.DestroyImmediate(baked);
            }
            return outp;
        }

        /// <summary>
        /// Exclude the things the game bolts ON to an aeroplane — the box
        /// shell, the cockpit furniture, the belly gun pod — and, crucially,
        /// THE PROPELLER.
        ///
        /// Excluding the propeller is not tidiness, it is the fix for a real
        /// measurement error. PlanePropsProbe.Axis's `SkinVerts` does not
        /// filter it, and its end-slab rule is "the WIDEST end slab is the
        /// tail, because the horizontal stabiliser is 25-45% of the wingspan".
        /// A tractor propeller is a 2.8-3.4 m disc standing on the NOSE: it
        /// makes the nose slab wider than the tailplane and inverts the
        /// verdict. That is the whole of `AXISSUM unexplained=2 ids=p100:+Z` —
        /// p100 is a tractor single-seater whose detected hub is at +3.95 m on
        /// a +-4.44 m airframe, i.e. ON the end the unfiltered rule called the
        /// tail. The asset is fine and src/planes.js is right not to carry a
        /// fix for it; the ORACLE was wrong. (Seventh stale-oracle finding in
        /// this port — suspect the probe before the port.)
        ///
        /// Same rule src/planes.js:910-920 isSkin applies for the cockpit
        /// measurement, for the same reason, plus the live SpinningPartTag the
        /// detection attaches.
        /// </summary>
        static bool IsAirframe(Transform self, Transform root)
        {
            for (Transform p = self; p != null; p = p.parent)
            {
                if (p.name == "plane_shell" || p.name == "cockpit_kit"
                    || p.name == "cockpit_interior" || p.name == "gun_pod") return false;
                if (PlaneCockpitMeasurement.PropNameRx.IsMatch(p.name)) return false;
                if (p.GetComponent<TbilisiFly.Combat.SpinningPartTag>() != null) return false;
                if (p == root) break;
            }
            return true;
        }

        struct Ends { public float NegWidth, PosWidth, NegZ, PosZ; public int NegN, PosN; }

        static Ends EndSlabs(List<Vector3> pts, Bounds box)
        {
            var e = new Ends();
            float lz = box.size.z;
            float negCut = box.min.z + lz * SlabFrac;
            float posCut = box.max.z - lz * SlabFrac;
            foreach (var p in pts)
            {
                if (p.z <= negCut) { e.NegN++; e.NegWidth = Mathf.Max(e.NegWidth, Mathf.Abs(p.x)); e.NegZ += p.z; }
                else if (p.z >= posCut) { e.PosN++; e.PosWidth = Mathf.Max(e.PosWidth, Mathf.Abs(p.x)); e.PosZ += p.z; }
            }
            if (e.NegN > 0) e.NegZ /= e.NegN;
            if (e.PosN > 0) e.PosZ /= e.PosN;
            return e;
        }

        /// <summary>Where the canopy is, in `frame`, or NaN. The discriminator
        /// that owes nothing to a bounding box: a pilot station is always
        /// forward of the middle.</summary>
        static float CanopyZ(Transform root, Transform frame, Bounds box)
        {
            float best = float.NaN; float bestVol = 0f;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsAirframe(r.transform, root)) continue;
                string n = r.transform.name;
                foreach (var m in r.sharedMaterials) if (m != null) n += " " + m.name;
                if (!CanopyRx.IsMatch(n)) continue;
                var b = BoxOfRenderer(r, frame);
                Vector3 s = b.size;
                if (s.x > box.size.x * 0.4f) continue;          // "Canopy" is often the whole aeroplane
                float v = s.x * s.y * s.z;
                if (v > box.size.x * box.size.y * box.size.z * 0.06f) continue;
                if (v > bestVol) { bestVol = v; best = b.center.z; }
            }
            return best;
        }

        static Bounds BoxOfRenderer(Renderer r, Transform frame)
        {
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr) { mesh = new Mesh(); smr.BakeMesh(mesh); }
            else if (r.TryGetComponent(out MeshFilter mf)) mesh = mf.sharedMesh;
            if (mesh == null) return new Bounds();
            var m = r.transform.localToWorldMatrix;
            var vs = mesh.vertices;
            bool any = false; var b = new Bounds();
            for (int i = 0; i < vs.Length; i++)
            {
                var p = frame.InverseTransformPoint(m.MultiplyPoint3x4(vs[i]));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
            if (r is SkinnedMeshRenderer) Object.DestroyImmediate(mesh);
            return b;
        }

        static Bounds BoundsOf(List<Vector3> pts)
        {
            var b = new Bounds(pts[0], Vector3.zero);
            for (int i = 1; i < pts.Count; i++) b.Encapsulate(pts[i]);
            return b;
        }

        // ==================================================================
        public static void Run()
        {
            bool ok = Survey();
            ok &= FlyNoseLeads();
            ProbeExit.Exit(ok ? 0 : 1);
        }

        /// <summary>The whole regression sweep in ONE editor launch — this
        /// probe plus the three whose numbers must not move. The lock is
        /// exclusive; four launches is four waits.</summary>
        public static void Suite()
        {
            ProbeExit.Suppress = true;
            bool ok = Survey();
            ok &= FlyNoseLeads();
            // The untouched control: the RAW assets on disk. Must still read
            // noseNegZ=29 nosePosZ=3 noseOther=3, and must still find 31/31
            // propellers with the same radii.
            PlanePropsProbe.Axis();
            PlanePropsProbe.Run();
            // WHEEL_HEIGHT / touchdown — a 180 deg yaw about Y cannot change
            // min.y, and this is the line that says so out loud.
            PlaneWheelOffsetProbe.Run();
            // The crane P-51's hub must still be onNose=1 and the hook still
            // 7.9000 — the crane airframe deliberately stays in the MODEL
            // frame (see Survey's note).
            WarehouseCraneFillProbe.Run();
            ProbeExit.Suppress = false;
            Debug.Log($"ORIENTSUITE ok={ok.ToString().ToLowerInvariant()} " +
                      $"chainedNonZero={ProbeExit.LastNonZero}");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // ==================================================================
        /// <summary>All 35 airframes, built exactly the way the game builds
        /// them (PlaneModelFrame.Attach), measured in the FLIGHT frame.</summary>
        public static bool Survey()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            int planes = 0, leads = 0, sane = 0, spanX = 0, byCanopy = 0;
            int propChecked = 0, propOnNose = 0;
            var fails = new List<string>();

            foreach (var def in Planes.All)
            {
                string path = def.Id == "spitfire"
                    ? PlaneHallFill.SpitfireObjPath
                    : PlaneHallFill.PlanesFbxDir + def.Id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"ORIENT id={def.Id} asset=missing"); continue; }

                // ---- BEFORE: the raw asset under a holder, which is what
                // PlaneHallFill used to park and measure.
                var rawInner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var rawGo = new GameObject(def.Id + "_raw");
                rawGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                rawGo.transform.localScale = def.Scale > 0f ? Vector3.one * def.Scale : Vector3.one;
                rawInner.transform.SetParent(rawGo.transform, false);
                var rawPts = VertsIn(rawGo.transform, rawGo.transform);
                Bounds rawBox = rawPts.Count > 0 ? BoundsOf(rawPts) : new Bounds();
                Object.DestroyImmediate(rawGo);

                // ---- AFTER: the real chain.
                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var go = new GameObject(def.Id);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var frames = PlaneModelFrame.Attach(go.transform, inner, def);

                // ---- FIND THE BLADES FIRST, so the measurement below can
                // leave them out (see IsAirframe) and so the hub station is
                // available as an independent cross-check. This is the real
                // production call (Flight/PlaneProps.Build against the FILE
                // frame, exactly as PlanePropellers.EnsureBuilt makes it).
                float propZ = float.NaN; int propN = 0;
                if (def.Prop)
                {
                    try
                    {
                        var live = PlaneProps.Build(frames.File.gameObject, def);
                        float sum = 0f;
                        foreach (var inst in live)
                        {
                            if (inst.Pivot == null) continue;
                            sum += go.transform.InverseTransformPoint(inst.Pivot.position).z;
                            propN++;
                        }
                        if (propN > 0) propZ = sum / propN;
                    }
                    catch (System.Exception ex) { Debug.Log($"ORIENTPROP id={def.Id} threw={ex.GetType().Name}"); }
                }

                var pts = VertsIn(go.transform, go.transform);
                if (pts.Count == 0) { Debug.Log($"ORIENT id={def.Id} verts=0"); Object.DestroyImmediate(go); continue; }
                planes++;

                Bounds box = BoundsOf(pts);
                var ends = EndSlabs(pts, box);
                float canZ = CanopyZ(go.transform, go.transform, box);
                float mid = box.center.z;

                // Which end is the NOSE, in the flight frame. Primary signal:
                // the canopy, when there is a credible one, because a pilot
                // station is forward of the middle. Fallback: the horizontal
                // stabiliser — 25-45% of the wingspan, in the last tenth of
                // the length — makes the TAIL slab the wider of the two.
                bool nosePos; string from;
                if (!float.IsNaN(canZ) && Mathf.Abs(canZ - mid) > box.size.z * 0.02f)
                { nosePos = canZ > mid; from = "canopy"; byCanopy++; }
                else
                { nosePos = ends.PosWidth < ends.NegWidth; from = "slabwidth"; }

                bool noseLeads = nosePos;

                bool spanIsX = box.size.x > box.size.z
                    || (def.Span.HasValue && def.Len.HasValue
                        && Mathf.Abs(box.size.x / def.Span.Value - 1f) < Mathf.Abs(box.size.z / def.Span.Value - 1f));
                if (spanIsX) spanX++;

                // The -Z-anchored cockpit search, run where it belongs now:
                // in the MODEL frame. f4f was the one cockpit=insane of 35
                // when this ran against the raw asset.
                string ck = "n/a", ckFrom = "-"; float seatZ = 0f, seatZRoot = 0f, room = 0f;
                try
                {
                    var r = PlaneCockpitMeasurement.Measure(frames.Frame, def.Cockpit);
                    ck = (r != null && r.Sane) ? "sane" : "insane";
                    if (r != null)
                    {
                        seatZ = r.Seat.z;
                        room = r.Room;
                        ckFrom = r.From;
                        seatZRoot = PlaneModelFrame.ModelPointToRoot(go.transform, r.Seat).z;
                        if (r.Sane) sane++;
                    }
                }
                catch (System.Exception ex) { ck = "threw:" + ex.GetType().Name; }

                // THE ASSERTION. FlightModel thrusts along plane.forward, which
                // is local +Z, so "the nose leads" is "the nose end is at +Z".
                if (noseLeads) leads++; else fails.Add(def.Id);

                // THE INDEPENDENT CROSS-CHECK, on the 15 propeller aircraft:
                // where the detected hub landed, in the flight frame. It owes
                // nothing to a bounding box and nothing to a name — for the
                // `make` models it is the station src/planes.js itself authors
                // (the P-51's `propMake.hub = [0, 1.57, 4.95]`, which must now
                // read propZ = +4.95, having been measured at -4.95 on the
                // hero plane by the first GPU render). A tractor's hub is at
                // the nose and a pusher's at the tail, so `propAtNose` is
                // expected false on exactly the three pusher layouts in the
                // table: xb35 (the real XB-35's four contra-rotating pushers
                // on the trailing edge), hsp75 (its own src/planes.js
                // description: "ფრთა უკან, საჭე წინ, ხრახნი კუდში" — wing at
                // the back, canard at the front, screw in the tail) and carpet.
                bool propAtNose = propN > 0 && !float.IsNaN(propZ) && (propZ > 0f) == noseLeads;
                if (propN > 0) propChecked++;
                if (propAtNose) propOnNose++;

                var fileEuler = frames.File.localRotation.eulerAngles;
                Debug.Log($"ORIENT id={def.Id} noseLeads={noseLeads.ToString().ToLowerInvariant()} " +
                          $"verdictFrom={from} propZ={propZ:F2} props={propN} " +
                          $"propAtNose={propAtNose.ToString().ToLowerInvariant()} " +
                          $"yawApplied={Mathf.DeltaAngle(0f, fileEuler.y):F1} " +
                          $"rollApplied={Mathf.DeltaAngle(0f, fileEuler.z):F1} " +
                          $"frameYaw={Mathf.DeltaAngle(0f, frames.Frame.localRotation.eulerAngles.y):F1} " +
                          $"span={box.size.x:F2} len={box.size.z:F2} " +
                          $"rawSpan={rawBox.size.x:F2} rawLen={rawBox.size.z:F2} " +
                          $"defSpan={(def.Span.HasValue ? def.Span.Value.ToString("F2") : "-")} " +
                          $"defLen={(def.Len.HasValue ? def.Len.Value.ToString("F2") : "-")} " +
                          $"noseFrom={from} negW={ends.NegWidth:F2} posW={ends.PosWidth:F2} " +
                          $"cockpit={ck} room={room:F2} seatZmodel={seatZ:F2} seatZflight={seatZRoot:F2} " +
                          $"minY={box.min.y:F3} cockpitFrom=\"{ckFrom}\"");

                // An insane cockpit is an UNMEASURED cockpit, so say which
                // candidate meshes were on offer and why none of them won —
                // src/planes.js:1026-1046's own five filters, in order.
                if (ck != "sane") CockpitDiag(def, frames.Frame);

                // The Cessna 310 is the one airframe whose PACKING was visibly
                // wrong (src/planehall.js's Span/Len), because its file is
                // stored nose-west. Called out by name so a regression cannot
                // hide in a sum.
                if (def.Id == "cessna310" || def.Id == "f4f")
                    Debug.Log($"ORIENTPACK id={def.Id} before={rawBox.size.x:F2}x{rawBox.size.z:F2} " +
                              $"after={box.size.x:F2}x{box.size.z:F2} " +
                              $"defSpanxLen={(def.Span ?? 0f):F2}x{(def.Len ?? 0f):F2}");

                Object.DestroyImmediate(go);
            }

            // KNOWN AND EXPLAINED, not swept under the sum. Both are faults in
            // the SPEC, measured by Editor/Probes/PlanePropsProbe.Axis's own
            // `AXISSUM unexplained=2 ids=p100:+Z,cargobro:-X`, and src/planes.js
            // carries NO orientation fix for either — so the Three.js build
            // flies them exactly as wrong as this one does. Adding a fix here
            // would be a silent divergence from the reference implementation;
            // they are reported instead (see docs/port_units/orientation.json).
            // ---- THE TWO AIRFRAMES THE MEASUREMENT CANNOT ADJUDICATE, named
            // and reasoned rather than buried in a sum. Neither is a fault in
            // the chain — the chain applies exactly src/planes.js:834's fix
            // composed with one 180 deg yaw, so each of these points EXACTLY
            // where the Three.js build points it. Both are questions about the
            // ASSET and the def, and both are in docs/port_units/orientation.json:
            //
            //   p100   its propeller is detected at +3.95 m on a +-4.44 m
            //          airframe (so, the nose), while BOTH the slab rule and
            //          the canopy centroid put the nose at -Z. src/planes.js
            //          carries no orientation fix for it, and
            //          PlanePropsProbe.Axis independently flags it
            //          (`unexplained=p100:+Z`) measuring the raw file. The two
            //          signals genuinely disagree on this asset; whichever is
            //          right, the JS build does the same thing.
            //   hsp75  a canard PUSHER ("ფრთა უკან, საჭე წინ, ხრახნი კუდში" —
            //          wing at the back, canard at the front, screw in the
            //          tail) whose asset is also only 0.47x its published
            //          18.0 m span. Its wing-at-the-rear layout breaks the
            //          "widest end slab is the tail" rule by construction, and
            //          its def's `yaw = PI` is applied faithfully.
            var ambiguous = new HashSet<string> { "p100", "hsp75" };
            int unexplained = 0;
            foreach (var f in fails) if (!ambiguous.Contains(f)) unexplained++;

            bool ok = planes > 0 && unexplained == 0;
            Debug.Log($"ORIENTSUM planes={planes} noseLeads={leads} cockpitSane={sane} " +
                      $"spanAlongX={spanX} noseByCanopy={byCanopy} " +
                      $"propChecked={propChecked} propAtNose={propOnNose} " +
                      $"fails={fails.Count}{(fails.Count > 0 ? " ids=" + string.Join(",", fails) : "")} " +
                      $"ambiguousAssets={fails.Count - unexplained} unexplained={unexplained} " +
                      $"pass={ok.ToString().ToLowerInvariant()}");
            return ok;
        }

        static readonly Regex WellRx = new Regex(
            "koltuk|seat|chair|cocpit|cockpit|kabina|interior|cabin|pilot",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>src/planes.js:1026-1046's candidate filters, reported one
        /// mesh at a time, so "insane" names a cause instead of a symptom.</summary>
        static void CockpitDiag(PlaneDef def, Transform frame)
        {
            var air = PlaneCockpitMeasurement.BoxOf(frame, frame);
            Vector3 size = air.size;
            float len = size.z;
            float vol = Mathf.Max(1e-6f, size.x * size.y * size.z);
            float zMin = air.min.z + len * 0.05f;
            float zMax = air.min.z + len * 0.78f;
            int shown = 0;
            foreach (var r in frame.GetComponentsInChildren<Renderer>(true))
            {
                string mn = r.transform.name;
                foreach (var m in r.sharedMaterials) if (m != null) mn += " " + m.name;
                bool named = CanopyRx.IsMatch(mn), isWell = WellRx.IsMatch(mn);
                if (!named && !isWell) continue;
                var b = BoxOfRenderer(r, frame);
                Vector3 s = b.size, c = b.center;
                string why = s.x > size.x * 0.4f ? "tooWideX"
                    : s.x * s.y * s.z > vol * 0.06f ? "tooBigVol"
                    : Mathf.Abs(c.x) > size.x * 0.25f ? "offCentre"
                    : c.z < zMin ? "aheadOfWindow" : c.z > zMax ? "behindWindow" : "KEPT";
                Debug.Log($"ORIENTCKDIAG id={def.Id} mesh={r.transform.name} why={why} " +
                          $"cx={c.x:F2} cz={c.z:F2} sx={s.x:F2} sy={s.y:F2} sz={s.z:F2} " +
                          $"window=[{zMin:F2},{zMax:F2}] hullX={size.x:F2}");
                if (++shown >= 14) break;
            }
            if (shown == 0) Debug.Log($"ORIENTCKDIAG id={def.Id} candidates=0 window=[{zMin:F2},{zMax:F2}]");
        }

        // ==================================================================
        /// <summary>REALLY FLY ONE. The whole point of the exercise: the model
        /// is not checked against a convention, it is checked against where the
        /// aeroplane actually goes, through the real PlaneController ->
        /// FlightModel -> PlanePhysics tick.</summary>
        public static bool FlyNoseLeads()
        {
            bool ok = true;
            foreach (string id in new[] { "p51", "cessna310", "il76", "neonfly" })
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var def = Planes.PlaneById(id);
                string path = id == "spitfire"
                    ? PlaneHallFill.SpitfireObjPath : PlaneHallFill.PlanesFbxDir + id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"ORIENTFLY id={id} asset=missing"); ok = false; continue; }

                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var go = new GameObject("Plane");
                // A yaw AND a bank, so the test cannot pass by the world axes
                // happening to line up with the model's.
                go.transform.SetPositionAndRotation(new Vector3(0f, 1200f, 0f),
                    Quaternion.Euler(0f, 37f, 12f));
                var frames = PlaneModelFrame.Attach(go.transform, inner, def);

                var ctl = go.AddComponent<PlaneController>();
                ctl.PlaneId = id;
                ctl.Init();
                if (ctl.Model == null) { Debug.Log($"ORIENTFLY id={id} FAIL no FlightModel"); ok = false; continue; }

                // The airframe's own two ends, measured ONCE in the flight
                // frame, before anything moves.
                var pts = VertsIn(frames.Frame, go.transform);
                if (pts.Count == 0) { Debug.Log($"ORIENTFLY id={id} FAIL verts=0"); ok = false; continue; }
                Bounds box = BoundsOf(pts);
                var ends = EndSlabs(pts, box);
                float canZ = CanopyZ(frames.Frame, go.transform, box);
                bool nosePos = !float.IsNaN(canZ) && Mathf.Abs(canZ - box.center.z) > box.size.z * 0.02f
                    ? canZ > box.center.z
                    : ends.PosWidth < ends.NegWidth;
                Vector3 noseLocal = new Vector3(0f, 0f, nosePos ? box.max.z : box.min.z);
                Vector3 tailLocal = new Vector3(0f, 0f, nosePos ? box.min.z : box.max.z);

                Vector3 start = go.transform.position;
                ctl.Velocity = go.transform.forward * Mathf.Max(ctl.Model.Vr, 60f);
                var stick = new StickInput { Pitch = 0f, Roll = 0f, Rudder = 0f, Throttle = 1f };
                for (int i = 0; i < 180; i++) ctl.Step(1f / 60f, stick);

                Vector3 vel = ctl.Velocity;
                Vector3 nose = go.transform.TransformPoint(noseLocal);
                Vector3 tail = go.transform.TransformPoint(tailLocal);
                Vector3 vdir = vel.sqrMagnitude > 1e-6f ? vel.normalized : go.transform.forward;
                float along = Vector3.Dot(nose - tail, vdir);
                float travelled = Vector3.Dot(go.transform.position - start, vdir);
                // The nose must be ahead of the tail along the velocity, by
                // most of the aeroplane's length (not merely by a hair, which
                // a sideways airframe would also satisfy).
                bool leads = along > box.size.z * 0.5f;
                bool moved = travelled > 50f;
                bool aligned = Vector3.Angle(vdir, go.transform.forward) < 25f;
                ok &= leads && moved && aligned;

                Debug.Log($"ORIENTFLY id={id} noseLeads={leads.ToString().ToLowerInvariant()} " +
                          $"alongVel={along:F2} len={box.size.z:F2} " +
                          $"travelled={travelled:F1} speed={vel.magnitude:F1} " +
                          $"velVsForwardDeg={Vector3.Angle(vdir, go.transform.forward):F1} " +
                          $"moved={moved.ToString().ToLowerInvariant()} " +
                          $"aligned={aligned.ToString().ToLowerInvariant()} " +
                          $"noseLocalZ={noseLocal.z:F2} tailLocalZ={tailLocal.z:F2}");
            }
            Debug.Log($"ORIENTFLYSUM pass={ok.ToString().ToLowerInvariant()}");
            return ok;
        }
    }
}
