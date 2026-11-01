using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// THE TWO CALL SITES src/planes.js has for src/propeller.js, which this
    /// port was missing entirely. Flight/PropellerSystem.cs is a faithful,
    /// probe-verified port of the whole detector — detection, named-split,
    /// hub-hinted, built-from-scratch, cut mesh, repivot, finish, spin, blur
    /// disc — and before this file its ONLY production caller was the three
    /// wind-tunnel fans (Props/WarehouseCraneFill.cs). So on the player's
    /// aeroplane and all 35 parked ones no propeller was ever detected, cut,
    /// repivoted, tagged, spun or blur-disced: 15 propeller-driven machines
    /// flew around Tbilisi with their blades nailed solid to the nose, which
    /// is the exact bug src/propeller.js:1-32 was written to fix.
    ///
    /// Ported here, in JS order:
    ///
    ///  1. src/planes.js:803-812 — ONCE PER AIRCRAFT TYPE, at model load, on
    ///     the cached model every clone comes from:
    ///         tagPropellers(ready, nose, def.props ?? 1,
    ///                       def.propMake ? {make} :
    ///                       def.propHubs ? {hubs, radius, depth} : null)
    ///     `nose` is where THIS FILE's nose points: (0,0,-1) rotated by the
    ///     INVERSE of the def's yaw/roll orientation fix, because the fix is
    ///     applied to a wrapper AROUND the model and half these files are
    ///     stored tail-first or sideways. <see cref="FileNose"/> does that.
    ///     PropellerSystem.TagPropellers ports the dispatch and hands back
    ///     plain data; the step the JS does INSIDE splitNamed/repivotNamed/
    ///     hintedPropellers — create the real pivot ('propeller_split' /
    ///     'propeller_hub' / 'propeller_built'), reparent or build the blade
    ///     meshes onto it, write the cut geometry back — is <see cref="Build"/>.
    ///
    ///  2. src/planes.js:1593 — ONCE PER BUILT WRAPPER:
    ///         wrapper.userData.propellers = finishPropellers(wrapper, (0,0,-1))
    ///     i.e. PropellerSystem.FinishPropeller per pivot + the blur-disc quad
    ///     + BladesExcludingDisc. Also in <see cref="Build"/>: unlike the JS,
    ///     this port has no per-TYPE cached model that clones share, so the
    ///     cut and the finish happen in one pass per instance (the detection
    ///     RESULT is cached per plane id instead — see <see cref="Plan"/>).
    ///
    /// Spin (src/game.js:2585 `spinPropellers(this.plane, this.sound.rpm *
    /// 2700, dt)`) is <see cref="PlanePropellers"/>, the per-plane component
    /// this file attaches nothing of its own — the two build call sites
    /// (Flight/PlaneHallFill.cs for the 35 parked machines, Flight/
    /// PlaneController.Init for the one being flown) attach it.
    ///
    /// ======================= NOSE AXIS AND HANDEDNESS =======================
    /// The MODEL frame's straightened nose is (0,0,-1) in Unity, not
    /// Vector3.forward — the FBX bridge mirrors X, not Z, so these files are
    /// stored exactly as the GLB had them. Measured on all 35 airframes; see
    /// FileNose below and Flight/FlightModel.cs's second handedness block.
    /// Nothing else in this file touches a cross product — PropellerSystem.
    /// BasisOf already carries the one swap this area needs.
    /// PropHubs/PropMake.Hub are (r, u, f) SCALARS along the measured basis,
    /// not raw axes, so they are reflection-invariant and are used verbatim,
    /// the same way Props/WarehouseCraneFill.cs already uses PropMake.Hub.
    ///
    /// Every vertex this file hands PropellerSystem is baked into the PLANE
    /// ROOT's own local space (planeRoot.worldToLocalMatrix * mesh
    /// localToWorldMatrix), which is pre-scale when the root carries def.Scale
    /// — matching the JS, whose tagPropellers runs on the raw cached model
    /// before makeGameplayPlane applies any scale.
    ///
    /// ===================== WHY THE MESHES ARE CLONED FIRST ==================
    /// src/propeller.js's closeUp() REWRITES the source mesh's index buffer to
    /// take the blade triangles out, so the blades exist exactly once. In
    /// Three.js that mutates a geometry the loader owns. In Unity the same
    /// write would mutate an IMPORTED ASSET's Mesh — shared by every instance
    /// of that FBX and serialised back over the asset. So any mesh that loses
    /// triangles is Object.Instantiate'd first and the clone assigned to the
    /// MeshFilter. Nothing on disk is touched.
    /// </summary>
    public static class PlaneProps
    {
        /// <summary>src/propeller.js:513/:845 — the cut-out pivot's name.</summary>
        public const string PivotSplit = "propeller_split";
        /// <summary>src/propeller.js:765 — a NAMED propeller re-pivoted onto its real hub.</summary>
        public const string PivotHub = "propeller_hub";
        /// <summary>src/propeller.js:921 — built from scratch (the P-51 ships a bare spinner).</summary>
        public const string PivotBuilt = "propeller_built";

        // ---------------------------------------------------------------
        // ---- the per-TYPE cache (src/planes.js:777 `cache.has(def.id)`) ----
        // ---------------------------------------------------------------
        /// <summary>What detection found for one aircraft TYPE, in a form that
        /// can be replayed onto another instance of the same FBX without
        /// re-running the search. The JS caches the whole cut MODEL and clones
        /// it (src/planes.js:777-815); this port instantiates each plane from
        /// the same prefab instead, so what is cached is the ANSWER: which
        /// triangles of which (MeshFilter, submesh) are the blades, and where
        /// the hub is. Keyed off the deterministic order of
        /// GetComponentsInChildren&lt;MeshFilter&gt;, which is identical across
        /// instances of one prefab.</summary>
        public sealed class Plan
        {
            public List<PropPlan> Props = new();
            /// <summary>(meshFilter index, submesh) -> the triangles that survive the cut.</summary>
            public Dictionary<(int mf, int sub), int[]> Kept = new();
            public string How = "none";   // named / split / make / hubs / search / none
            public float Millis;
        }

        public sealed class PropPlan
        {
            public Kind Which;
            public Vector3 Hub;          // plane-root-local
            public float Radius;
            public int BladeCount;
            public bool NeedsRepivot;
            /// <summary>Kind.Named only — the named node, as a '/'-joined path
            /// below the plane root (stable across instances of one prefab).</summary>
            public string NamedPath;
            /// <summary>Kind.Cut only — one entry per source (MeshFilter,
            /// submesh) the blades came out of, with that part's triangles.</summary>
            public List<((int mf, int sub) src, int[] tris)> Parts = new();
        }

        public enum Kind { Cut, Named, Built }

        static readonly Dictionary<string, Plan> _plans = new();
        static readonly Dictionary<int, Texture2D> _discTex = new();

        /// <summary>The cached detection answer for a plane id, or null if that
        /// type has not been detected yet. Read-only; for probes.</summary>
        public static Plan CachedPlan(string id)
            => id != null && _plans.TryGetValue(id, out var p) ? p : null;

        /// <summary>Drop the per-type cache (an asset re-import or a probe that
        /// wants a cold measurement).</summary>
        public static void ClearCache() { _plans.Clear(); }

        /// <summary>One line of raw measurement per aeroplane, for a probe that
        /// needs to know WHY a detection came out the way it did rather than
        /// just how many it found: the model's own extents in (r, u, f), the
        /// nose axis used, how much geometry was gathered, how many named
        /// candidates survived the parent-chain filter, and — for the four
        /// hinted machines — how many triangles each hinted cylinder actually
        /// catches. A hint that catches nothing is the one failure mode
        /// src/propeller.js:1009-1011 warns about out loud ("propeller hint
        /// found nothing — falling back to the search").</summary>
        public static string Diagnose(GameObject planeRoot, PlaneDef def)
        {
            if (planeRoot == null || def == null) return "diag: null";
            var root = planeRoot.transform;
            Vector3 nose = FileNose(def);
            var basis = PropellerSystem.BasisOf(nose);
            var mfs = MeshFiltersOf(root);
            var entries = BakeAll(root, mfs);
            var named = NamedCandidates(root, mfs);

            int tris = 0, verts = 0;
            float rmin = float.MaxValue, rmax = float.MinValue;
            float umin = float.MaxValue, umax = float.MinValue;
            float fmin = float.MaxValue, fmax = float.MinValue;
            foreach (var e in entries)
            {
                tris += e.Entry.TriCount;
                verts += e.Entry.Positions.Length;
                foreach (var p in e.Entry.Positions)
                {
                    float pr = Vector3.Dot(p, basis.R), pu = Vector3.Dot(p, basis.U), pf = Vector3.Dot(p, basis.F);
                    if (pr < rmin) rmin = pr; if (pr > rmax) rmax = pr;
                    if (pu < umin) umin = pu; if (pu > umax) umax = pu;
                    if (pf < fmin) fmin = pf; if (pf > fmax) fmax = pf;
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"id={def.Id} nose=({nose.x:F2},{nose.y:F2},{nose.z:F2}) ");
            sb.Append($"meshes={mfs.Count} entries={entries.Count} tris={tris} verts={verts} named={named.Count} ");
            sb.Append($"r=[{rmin:F2},{rmax:F2}] u=[{umin:F2},{umax:F2}] f=[{fmin:F2},{fmax:F2}] ");
            sb.Append($"natural=rot{planeRoot.transform.localRotation.eulerAngles} scale{planeRoot.transform.localScale} ");
            if (def.PropHubs != null && def.PropHubs.Length > 0)
            {
                float rad = def.PropRadius * 1.04f;
                float depth = def.PropDepth > 0f ? def.PropDepth : def.PropRadius * 0.35f;
                sb.Append($"hintRad={rad:F2} hintDepth={depth:F2} hubTris=[");
                for (int hi = 0; hi < def.PropHubs.Length; hi++)
                {
                    var h = def.PropHubs[hi];
                    sb.Append(hi > 0 ? "," : "").Append(CountIn(entries, basis, h.x, h.y, h.z, rad, depth));
                }
                // The same four cylinders with f mirrored and with r mirrored.
                // A hinted machine that catches nothing where it should and
                // plenty at -f has had its nose axis inverted somewhere between
                // the GLB those numbers were measured on (tools/_probe_hub.mjs)
                // and the FBX Unity imports; same for r/starboard. Printing all
                // three settles which, instead of guessing at a reflection.
                sb.Append("] hubTrisNegF=[");
                for (int hi = 0; hi < def.PropHubs.Length; hi++)
                {
                    var h = def.PropHubs[hi];
                    sb.Append(hi > 0 ? "," : "").Append(CountIn(entries, basis, h.x, h.y, -h.z, rad, depth));
                }
                sb.Append("] hubTrisNegR=[");
                for (int hi = 0; hi < def.PropHubs.Length; hi++)
                {
                    var h = def.PropHubs[hi];
                    sb.Append(hi > 0 ? "," : "").Append(CountIn(entries, basis, -h.x, h.y, h.z, rad, depth));
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        static int CountIn(List<(MeshEntry Entry, (int mf, int sub) Src)> entries, PropBasis basis,
            float hr, float hu, float hf, float rad, float depth)
        {
            int n = 0;
            foreach (var e in entries)
                for (int t = 0; t < e.Entry.TriCount; t++)
                {
                    Vector3 sum = Vector3.zero;
                    for (int j = 0; j < 3; j++) sum += e.Entry.Positions[e.Entry.Tri[t * 3 + j]];
                    var v = sum / 3f;
                    if (Mathf.Abs(Vector3.Dot(v, basis.F) - hf) > depth) continue;
                    float dr = Vector3.Dot(v, basis.R) - hr, du = Vector3.Dot(v, basis.U) - hu;
                    if (dr * dr + du * du > rad * rad) continue;
                    n++;
                }
            return n;
        }

        // ---------------------------------------------------------------
        // ---- src/planes.js:803-808 — where THIS FILE's nose points ----
        // ---------------------------------------------------------------
        /// <summary>src/planes.js:803-808. The orientation fix (def.yaw/
        /// def.roll) is applied to a wrapper AROUND the model, not to the
        /// model, so the raw file may well be stored tail-first or sideways
        /// and a detector measuring "thin along the nose axis" would be
        /// measuring across the wingspan. The nose axis wanted is therefore
        /// the STRAIGHTENED nose rotated by the INVERSE of the fix.
        ///
        /// The straightened nose is -Z here as well (see the measurement
        /// below), and the fix's two angles go through PlaneDef.YawUnity and
        /// PlaneDef.RollUnity, both negated under the measured X mirror,
        /// exactly as PlaneCatalog.cs's own handedness header prescribes. That
        /// header also records that no def in the table sets both Yaw and Roll
        /// at once, so THREE's XYZ Euler order and Unity's ZXY order cannot
        /// disagree here.
        ///
        /// The fix IS applied to a wrapper now (Flight/PlaneModelFrame's
        /// `model_file` node, 2026-10-30) — but <see cref="Build"/> is handed
        /// that very node as its root, so the frame it searches is still the
        /// FILE frame and this is still the right nose axis for detection AND
        /// for the spin axis in step 2. Nothing in this file changed when the
        /// straightening landed, by construction: PlanePropellers.ModelRoot
        /// resolves to `model_file`, whose local space is exactly the space
        /// Build used while the model was parented at identity. That is the
        /// point of the chain having three nodes — collapsing it would have
        /// moved the hinted hubs out from under their own hand-measured
        /// coordinates, and PROPSUM would have fallen off 31/31.
        ///
        /// ======== THE STRAIGHTENED NOSE IS -Z HERE, AND IT IS MEASURED ========
        /// Flight/FlightModel.cs's handedness header says "Three.js forward =
        /// -Z, Unity forward = +Z; only forward's label flips", i.e. the
        /// model-local frame is Z-mirrored by the import. For the 35 aeroplane
        /// assets that is NOT what happened, and the difference is the whole
        /// hinted-propeller path. Measured, not reasoned, on the real FBX
        /// (Editor/Probes/PlanePropsProbe.cs's PROPSDIAG line counts the
        /// triangles each hinted cylinder catches, at the hint's own f and at
        /// -f):
        ///
        ///     c130       hubTris [0,0,0,0]          hubTrisNegF [1687,1687,1688,1688]
        ///     yc130      hubTris [0,0,0,0]          hubTrisNegF [ 515, 513, 524, 516]
        ///     lancaster  hubTris [718,719,0,0]      hubTrisNegF [2152,2153,2075,2073]
        ///     cessna310  hubTris [32,32]            hubTrisNegF [ 467, 466]
        ///
        /// Four models, fourteen hub hints measured off the GLBs with
        /// tools/_probe_hub.mjs, every one of them found at MINUS the hinted
        /// nose coordinate. Solving that against each model's own yaw fix
        /// (cessna310 has yaw = -pi/2, the other three have none) gives one
        /// consistent answer: the glTF -> Blender -> FBX -> Unity bridge
        /// (tools/glb_batch_fbx.py, `axis_forward='-Z', axis_up='Y'`) spends
        /// its one required handedness flip on **X**, not Z. So a model-local
        /// (x, y, z) in the GLB arrives in Unity as (-x, y, z): Z is NOT
        /// mirrored, and the JS's straightened nose (0,0,-1) is still (0,0,-1)
        /// here — these airframes are stored pointing along Unity's -Z.
        ///
        /// CONFIRMED INDEPENDENTLY, 2026-10-30, and the hinted-hub argument
        /// above no longer has to carry it alone. Editor/Probes/
        /// PlanePropsProbe.Axis measures all 35 airframes from a signal that
        /// owes nothing to a propeller hint — the horizontal stabiliser, which
        /// is 25-45% of the wingspan and sits in the last tenth of the length,
        /// so the WIDEST end slab of the bounding box is the tail. It returns
        /// nose = -Z for all 27 defs with no orientation fix, +Z for the four
        /// `yaw = PI` "stored tail-first" defs, +X for the two `yaw = -PI/2`
        /// "stored nose-west" defs, and the two off-axis yaws' nearest axis —
        /// i.e. exactly the set src/planes.js:826-836 straightens, with the
        /// straightened nose at -Z. See Flight/FlightModel.cs's second
        /// handedness block for the table and for the P-51 spinner constant
        /// that pins it without a tolerance argument.
        ///
        /// Under an X mirror a rotation about Y NEGATES (as PlaneDef.YawUnity
        /// already did) and so does a rotation about Z. PlaneDef.RollUnity now
        /// negates as well and is the single owner of that sign; this method
        /// reads it rather than negating Roll a second time of its own.
        ///
        /// CONSEQUENCE FOR OTHER FILES: Props/WarehouseCraneFill.
        /// BuildPropellers passed `planeGo.transform.forward` (+Z) as the slung
        /// P-51's nose and set its pivot to PropMake.Hub's raw (r,u,f) triple,
        /// both of which assume +Z is the nose — which put the Mustang's hub
        /// 4.95 m BEHIND the cockpit and spun it the wrong way. Verified
        /// against the measurement above and FIXED there, 2026-10-30
        /// (PropellerSystem.FromRuf for the hub, -transform.forward for the
        /// nose), proven by WarehouseCraneFillProbe's CRANEFILL_PROP
        /// pivotLocalPos line.</summary>
        public static Vector3 FileNose(PlaneDef def)
        {
            if (def == null) return -Vector3.forward;
            // MIRROR X ONCE, NOT TWICE. Both angles come from PlaneDef's own
            // YawUnity/RollUnity now; this used to negate Roll locally
            // (`-def.Roll`) while PlaneDef.RollUnity kept it, so the project
            // held the same mirror decision in two places with two different
            // answers. PlaneCatalog.cs owns it — see its note on why an X
            // mirror negates a rotation about Z.
            var fix = Quaternion.Euler(0f, def.YawUnity * Mathf.Rad2Deg, def.RollUnity * Mathf.Rad2Deg);
            return (Quaternion.Inverse(fix) * new Vector3(0f, 0f, -1f)).normalized;
        }

        // ---------------------------------------------------------------
        // ---- the entry point ----
        // ---------------------------------------------------------------
        /// <summary>Detect (or replay the cached detection for) this type's
        /// propellers, cut/repivot/build them onto real pivot Transforms under
        /// `planeRoot`, then finish each one: spin axis, blur disc, blade list.
        /// Returns the live instances for <see cref="PlanePropellers"/> to
        /// spin. Safe on a jet (returns empty) and idempotent (a plane that
        /// already carries pivots is left alone).</summary>
        public static List<PropellerSystem.PropellerInstance> Build(GameObject planeRoot, PlaneDef def)
        {
            var live = new List<PropellerSystem.PropellerInstance>();
            if (planeRoot == null || def == null) return live;
            // src/planes.js:806 `if (def.prop)` — a jet has nothing to look
            // for and the search is not free.
            if (!def.Prop) return live;
            if (FindPivots(planeRoot.transform).Count > 0) return live; // already built

            var root = planeRoot.transform;
            Vector3 nose = FileNose(def);
            var mfs = MeshFiltersOf(root);

            if (!_plans.TryGetValue(def.Id, out var plan))
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                plan = Detect(root, def, nose, mfs);
                plan.Millis = (float)t0.Elapsed.TotalMilliseconds;
                _plans[def.Id] = plan;
            }

            // ---- apply the cut (src/propeller.js:410-422 closeUp) ----
            // `originals` carries the UNCUT mesh per MeshFilter, so CutOut can
            // still read the positions and triangle numbers the plan was built
            // against however the two steps are ordered — the Unity-side
            // equivalent of src/propeller.js:82-88's "every cut reads this
            // snapshot; the mesh itself is rebuilt once, at the end".
            var originals = ApplyKept(mfs, plan.Kept);

            // ---- materialise each propeller ----
            foreach (var pp in plan.Props)
            {
                Transform pivot = Materialise(root, mfs, originals, nose, pp);
                if (pivot == null) continue;

                // SRC/PROPELLER.JS TAGS AT DETECTION, NOT AT FINISH
                // (:515, :769, :851, :893, :927, :1021 all set
                // userData.isPropeller the moment the pivot exists, and
                // src/planedamage.js:147 `spins()` reads that field, not
                // anything finishPropellers wrote). PropellerSystem.
                // FinishPropeller attaches Combat.SpinningPartTag itself —
                // before its own alreadyFinished early-return — but it is
                // attached HERE as well, at detection time, so a future
                // detect-without-finish path can never leave a blade
                // untagged. Both sites guard against a double AddComponent,
                // so this is belt and braces, not a duplicate component.
                if (pivot.GetComponent<TbilisiFly.Combat.SpinningPartTag>() == null)
                    pivot.gameObject.AddComponent<TbilisiFly.Combat.SpinningPartTag>();

                // src/planes.js:1593 `finishPropellers(wrapper, (0,0,-1))`.
                // FinishPropeller wants the nose as a WORLD direction (the JS
                // passes a wrapper-space vector and the wrapper is still at
                // identity when it runs; this port's planes are already placed
                // and rotated by the time the hall builds them, so it is
                // transformed properly rather than relying on that accident).
                var inst = PropellerSystem.FinishPropeller(pivot, root.TransformDirection(nose), pp.Radius);
                if (inst.HasDisc) AttachDisc(pivot, inst, pp.BladeCount);
                inst.Blades = PropellerSystem.BladesExcludingDisc(pivot, inst.Disc);
                // SpinPropellers' own contract: "caller is expected to have
                // already given this renderer its OWN material instance" — a
                // cut blade shares the airframe's material, and fading it
                // would fade the whole fuselage with it.
                // Assigned through sharedMaterial, not `r.material`: the clone
                // is already unique (that is the point), and the `material`
                // accessor is what makes Unity log "Instantiating material
                // due to calling renderer.material during edit mode" on every
                // edit-mode scene load. The CLONE STAYS — it is the per-blade
                // instance SpinPropellers' fade needs.
                foreach (var r in inst.Blades)
                    if (r != null && r.sharedMaterial != null)
                        r.sharedMaterial = new Material(r.sharedMaterial) { name = r.sharedMaterial.name + "_blade" };
                live.Add(inst);
            }
            return live;
        }

        /// <summary>Every propeller pivot already under a plane — the Unity
        /// analogue of src/propeller.js:1045's `wrapper.traverse(o =&gt;
        /// o.userData.isPropeller)`. Reads the real tag, not the name.</summary>
        public static List<Transform> FindPivots(Transform root)
        {
            var outp = new List<Transform>();
            if (root == null) return outp;
            foreach (var tag in root.GetComponentsInChildren<TbilisiFly.Combat.SpinningPartTag>(true))
                outp.Add(tag.transform);
            return outp;
        }

        // ---------------------------------------------------------------
        // ---- detection ----
        // ---------------------------------------------------------------
        static Plan Detect(Transform root, PlaneDef def, Vector3 nose, List<MeshFilter> mfs)
        {
            var plan = new Plan();

            // src/propeller.js:861-867 — a NAME IS A HINT. PROP_NAME_RX is
            // already ported once (PlaneCockpitMeasurement.PropNameRx, cited
            // to src/planes.js:742); reused rather than written twice.
            var named = NamedCandidates(root, mfs);
            var entries = BakeAll(root, mfs);

            var hint = def.PropMake != null
                ? new PropHint { Make = def.PropMake }
                : (def.PropHubs != null && def.PropHubs.Length > 0
                    ? new PropHint { Hubs = def.PropHubs, Radius = def.PropRadius, Depth = def.PropDepth }
                    : null);

            int want = Mathf.Max(1, def.Props);
            var cand = named.Select(n => n.Cand).ToList();
            var res = PropellerSystem.TagPropellers(cand, entries.Select(e => e.Entry).ToList(), nose, want, hint);

            plan.How = res.UsedNamed ? (res.Props.Any(p => p.Parts.Count > 0) ? "split" : "named")
                : res.UsedHintMake ? "make"
                : res.UsedHintHubs ? "hubs"
                : res.UsedSearch ? "search" : "none";

            // TagResult's named path walks `named` in order and emits one
            // DetectedPropeller per candidate (PropellerSystem.cs's own
            // foreach), so index i pairs with named[i]. The split/hinted/
            // search paths emit cut parts instead and never read it.
            bool namedTrust = res.UsedNamed && res.Props.All(p => p.Parts.Count == 0);

            for (int i = 0; i < res.Props.Count; i++)
            {
                var d = res.Props[i];
                var pp = new PropPlan
                {
                    Hub = d.Hub, Radius = d.Radius, BladeCount = d.BladeCount,
                    NeedsRepivot = d.NeedsRepivot,
                };
                if (res.UsedHintMake) pp.Which = Kind.Built;
                else if (namedTrust && i < named.Count) { pp.Which = Kind.Named; pp.NamedPath = named[i].Path; }
                else
                {
                    pp.Which = Kind.Cut;
                    foreach (var (entry, tris) in d.Parts)
                    {
                        var src = SourceOf(entry, entries, named);
                        if (src.mf < 0) continue;
                        pp.Parts.Add((src, tris.ToArray()));
                    }
                    if (pp.Parts.Count == 0) continue;
                }
                plan.Props.Add(pp);
            }

            // src/propeller.js:410-422 closeUp, for whichever entry set the
            // winning path actually cut from. Recorded in the plan so a second
            // instance of the same FBX gets the same index buffer without
            // re-running the search.
            foreach (var e in entries) RecordKept(plan, e.Src, e.Entry);
            foreach (var n in named)
                if (n.Cand.Entry != null) RecordKept(plan, n.Src, n.Cand.Entry);

            return plan;
        }

        static void RecordKept(Plan plan, (int mf, int sub) src, MeshEntry entry)
        {
            if (entry == null || entry.Taken.Count == 0) return;
            plan.Kept[src] = PropellerSystem.CloseUp(entry);
        }

        static (int mf, int sub) SourceOf(MeshEntry entry,
            List<(MeshEntry Entry, (int mf, int sub) Src)> entries,
            List<(PropellerSystem.NamedCandidate Cand, string Path, (int mf, int sub) Src)> named)
        {
            foreach (var e in entries) if (ReferenceEquals(e.Entry, entry)) return e.Src;
            foreach (var n in named) if (ReferenceEquals(n.Cand.Entry, entry)) return n.Src;
            return (-1, -1);
        }

        // ---------------------------------------------------------------
        // ---- gathering the geometry (src/propeller.js:61-74 gatherMeshes) ----
        // ---------------------------------------------------------------
        static List<MeshFilter> MeshFiltersOf(Transform root)
            => root.GetComponentsInChildren<MeshFilter>(true).ToList();

        /// <summary>src/propeller.js:63-72. Every mesh that could hold
        /// propeller geometry, measured in ONE space (the plane root's), one
        /// MeshEntry per (MeshFilter, submesh) — a submesh is the Unity
        /// analogue of the JS's "one merged mesh per material called Object_7".
        /// Skips what the JS skips: a skinned mesh (a rigged prop turns on its
        /// bone — Unity's SkinnedMeshRenderer, which has no MeshFilter, so it
        /// is excluded by construction), the cockpit lining
        /// (userData.interior), and additionally the box shell and the cockpit
        /// kit, neither of which exists in the JS at tagPropellers time.</summary>
        static List<(MeshEntry Entry, (int mf, int sub) Src)> BakeAll(Transform root, List<MeshFilter> mfs)
        {
            var outp = new List<(MeshEntry, (int, int))>();
            Matrix4x4 inv = root.worldToLocalMatrix;
            for (int i = 0; i < mfs.Count; i++)
            {
                var mf = mfs[i];
                if (!Usable(mf, root)) continue;
                var mesh = mf.sharedMesh;
                var verts = mesh.vertices;
                var norms = mesh.normals;
                var uvs = mesh.uv;
                var cols = mesh.colors;
                Matrix4x4 toModel = inv * mf.transform.localToWorldMatrix;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tri = mesh.GetTriangles(s);
                    if (tri.Length < 3) continue;
                    outp.Add((MeshEntry.Bake(verts, norms.Length == verts.Length ? norms : null,
                        uvs.Length == verts.Length ? uvs : null,
                        cols.Length == verts.Length ? cols : null,
                        tri, toModel, mf), (i, s)));
                }
            }
            return outp;
        }

        static bool Usable(MeshFilter mf, Transform root)
        {
            if (mf == null || mf.sharedMesh == null) return false;
            if (mf.GetComponent<MeshRenderer>() == null) return false;
            for (Transform p = mf.transform; p != null; p = p.parent)
            {
                // src/propeller.js:67 `if (o.userData.interior) return;` — the
                // Unity marker for that is the cockpit_interior child
                // src/planes.js:770 creates (PlaneCockpitMeasurement.cs:310
                // reads the same one).
                if (p.name == "cockpit_interior" || p.name == "plane_shell") return false;
                if (p.GetComponent<TbilisiFly.Combat.PlaneShellTag>() != null) return false;
                if (p.GetComponent<TbilisiFly.Combat.SpinningPartTag>() != null) return false;
                if (p.name.StartsWith("cockpit_")) return false;
                if (p == root) break;
            }
            return true;
        }

        /// <summary>src/propeller.js:865-884 — the nodes the model NAMED, with
        /// the parent-chain filter the JS applies ("a tail bone under its own
        /// blade" turns the blades twice; only the top of each chain counts).
        /// A named node with no mesh of its own is the JS's `o.isBone` case:
        /// the one rigged aeroplane names its propeller BONES, which know
        /// their own axis already and are never cut or repivoted.</summary>
        static List<(PropellerSystem.NamedCandidate Cand, string Path, (int mf, int sub) Src)>
            NamedCandidates(Transform root, List<MeshFilter> mfs)
        {
            var outp = new List<(PropellerSystem.NamedCandidate, string, (int, int))>();
            Matrix4x4 inv = root.worldToLocalMatrix;
            var rx = PlaneCockpitMeasurement.PropNameRx;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || !rx.IsMatch(t.name)) continue;
                bool nested = false;
                for (Transform p = t.parent; p != null && p != root; p = p.parent)
                    if (rx.IsMatch(p.name)) { nested = true; break; }
                if (nested) continue;                                 // :879-884
                if (t.GetComponentInParent<TbilisiFly.Combat.SpinningPartTag>() != null) continue;

                var mf = t.GetComponent<MeshFilter>();
                bool isBone = mf == null || mf.sharedMesh == null || t.GetComponent<MeshRenderer>() == null;
                var cand = new PropellerSystem.NamedCandidate
                {
                    IsBone = isBone,
                    OriginInRoot = inv.MultiplyPoint3x4(t.position),
                };
                (int, int) src = (-1, -1);
                if (!isBone)
                {
                    var mesh = mf.sharedMesh;
                    var verts = mesh.vertices;
                    var norms = mesh.normals;
                    var uvs = mesh.uv;
                    var cols = mesh.colors;
                    // ONE entry for the whole node, every submesh concatenated:
                    // the JS's `o.geometry` is a single index buffer however many
                    // materials the mesh carries, and splitNamed/repivotNamed both
                    // measure that whole buffer (src/propeller.js:487-492, :731-737).
                    cand.Entry = MeshEntry.Bake(verts,
                        norms.Length == verts.Length ? norms : null,
                        uvs.Length == verts.Length ? uvs : null,
                        cols.Length == verts.Length ? cols : null,
                        mesh.triangles, inv * t.localToWorldMatrix, mf);
                    cand.Size = ExtentOf(cand.Entry);
                    int idx = mfs.IndexOf(mf);
                    src = (idx, -1);                                   // -1: all submeshes at once
                }
                else
                {
                    cand.Size = SubtreeExtent(t, inv);
                }
                outp.Add((cand, PathOf(root, t), src));
            }
            return outp;
        }

        /// <summary>src/propeller.js:887-889's `new Box3().setFromObject(o,
        /// true).getSize()` — but measured in the PLANE ROOT's axes rather
        /// than world ones. The JS gets away with world space because its
        /// wrapper is still at identity when tagPropellers runs; this port's
        /// planes are already placed and yawed inside a rotated hall, where a
        /// world AABB is a different box.</summary>
        static Vector3 ExtentOf(MeshEntry e)
        {
            if (e?.Positions == null || e.Positions.Length == 0) return Vector3.zero;
            Vector3 lo = e.Positions[0], hi = e.Positions[0];
            foreach (var p in e.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            return hi - lo;
        }

        static Vector3 SubtreeExtent(Transform t, Matrix4x4 inv)
        {
            bool any = false;
            Vector3 lo = Vector3.zero, hi = Vector3.zero;
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = inv * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var p = m.MultiplyPoint3x4(v);
                    if (!any) { lo = hi = p; any = true; }
                    else { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                }
            }
            foreach (var smr in t.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                Matrix4x4 m = inv * smr.transform.localToWorldMatrix;
                foreach (var v in smr.sharedMesh.vertices)
                {
                    var p = m.MultiplyPoint3x4(v);
                    if (!any) { lo = hi = p; any = true; }
                    else { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                }
            }
            return any ? hi - lo : Vector3.zero;
        }

        static string PathOf(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (Transform p = t; p != null && p != root; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ---------------------------------------------------------------
        // ---- turning a plan into real objects ----
        // ---------------------------------------------------------------
        static Transform Materialise(Transform root, List<MeshFilter> mfs,
            Dictionary<MeshFilter, Mesh> originals, Vector3 nose, PropPlan pp)
        {
            switch (pp.Which)
            {
                case Kind.Built: return BuildFromScratch(root, nose, pp);
                case Kind.Named: return Repivot(root, pp);
                default: return CutOut(root, mfs, originals, pp);
            }
        }

        /// <summary>src/propeller.js:917-930 — "No propeller in the file at
        /// all: build one where the spinner is." The blades are modelled in
        /// their own XY plane and stood up to face along the nose
        /// (:923-925 `blades.quaternion.setFromUnitVectors((0,0,1), f)`);
        /// the pivot itself carries only the hub translation, the same
        /// convention Props/WarehouseCraneFill.BuildPropellers uses for the
        /// slung Mustang.</summary>
        static Transform BuildFromScratch(Transform root, Vector3 nose, PropPlan pp)
        {
            int blades = pp.BladeCount > 0 ? pp.BladeCount : 4;
            var pivot = new GameObject(PivotBuilt).transform;
            pivot.SetParent(root, false);
            pivot.localPosition = pp.Hub;
            pivot.localRotation = Quaternion.identity;

            var go = new GameObject("propeller_blades");
            go.transform.SetParent(pivot, false);
            // src/propeller.js:923-925 — "built in its own XY plane; stand it
            // up to face along the nose". The pivot carries only the hub
            // translation, so `nose` is already the pivot-local nose axis.
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, nose);
            go.AddComponent<MeshFilter>().sharedMesh = PropellerSystem.BuildBladesMesh(pp.Radius, blades);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BladeMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;  // src/propeller.js:707 `mesh.castShadow = true`
            return pivot;
        }

        /// <summary>src/propeller.js:762-772 — whatever origin the model
        /// shipped with is a bounding-box centre or a median, and neither is
        /// the hub of an odd-bladed propeller (the Spitfire turned about a
        /// point a quarter of a radius above its spinner). Fixed by
        /// REPARENTING, not by moving geometry: a pivot goes at the fitted
        /// hub and the node is attached into it without moving a millimetre
        /// on screen (Three's `group.attach(o)` == SetParent(.., true)).
        /// NeedsRepivot false means it already turns on its hub — left exactly
        /// where it is (:755-761), and the node itself becomes the pivot.</summary>
        static Transform Repivot(Transform root, PropPlan pp)
        {
            var node = FindByPath(root, pp.NamedPath);
            if (node == null) return null;
            if (!pp.NeedsRepivot) return node;
            var pivot = new GameObject(PivotHub).transform;
            pivot.SetParent(root, false);
            pivot.localPosition = pp.Hub;
            pivot.localRotation = Quaternion.identity;
            node.SetParent(pivot, true);        // worldPositionStays
            return pivot;
        }

        /// <summary>src/propeller.js:344-405 extract + :509-520 / :844-856 /
        /// :1013-1024 — one child mesh per source (MeshFilter, submesh) the
        /// blades came out of, hub-centred, keeping that source's own
        /// material (`Array.isArray(mesh.material) ? mesh.material[0] :
        /// mesh.material`).</summary>
        static Transform CutOut(Transform root, List<MeshFilter> mfs,
            Dictionary<MeshFilter, Mesh> originals, PropPlan pp)
        {
            // Re-bake only the meshes this propeller actually came out of. The
            // plan holds triangle numbers into the ORIGINAL index buffer, which
            // ApplyKept has already rewritten by the time we run — so the
            // positions and the index buffer are read out of `originals`, the
            // uncut snapshot it kept for exactly this.
            var pivot = new GameObject(PivotSplit).transform;
            pivot.SetParent(root, false);
            pivot.localPosition = pp.Hub;
            pivot.localRotation = Quaternion.identity;

            Matrix4x4 inv = root.worldToLocalMatrix;
            int made = 0;
            foreach (var (src, tris) in pp.Parts)
            {
                if (src.mf < 0 || src.mf >= mfs.Count) continue;
                var mf = mfs[src.mf];
                if (mf == null || mf.sharedMesh == null) continue;
                var source = originals.TryGetValue(mf, out var orig) ? orig : mf.sharedMesh;
                var verts = source.vertices;
                var norms = source.normals;
                var uvs = source.uv;
                var cols = source.colors;
                int[] buffer = src.sub < 0 ? source.triangles : source.GetTriangles(src.sub);
                var entry = MeshEntry.Bake(verts,
                    norms.Length == verts.Length ? norms : null,
                    uvs.Length == verts.Length ? uvs : null,
                    cols.Length == verts.Length ? cols : null,
                    buffer, inv * mf.transform.localToWorldMatrix, mf);
                var parts = new List<(MeshEntry, List<int>)> { (entry, tris.ToList()) };
                var mesh = PropellerSystem.BuildCutMesh(parts, pp.Hub);
                if (mesh.vertexCount == 0) continue;

                var go = new GameObject("propeller_blade");
                go.transform.SetParent(pivot, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var srcMr = mf.GetComponent<MeshRenderer>();
                var mats = srcMr != null ? srcMr.sharedMaterials : null;
                Material mat = null;
                if (mats != null && mats.Length > 0)
                    mat = mats[Mathf.Clamp(src.sub < 0 ? 0 : src.sub, 0, mats.Length - 1)];
                mr.sharedMaterial = mat != null ? mat : BladeMaterial();
                if (srcMr != null) mr.shadowCastingMode = srcMr.shadowCastingMode;
                made++;
            }
            if (made == 0) { Discard(pivot.gameObject); return null; }
            return pivot;
        }

        static Transform FindByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var t = root.Find(path);
            if (t != null) return t;
            // A repivot moved the node under a 'propeller_hub' parent on an
            // earlier pass; fall back to a name search.
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            foreach (var c in root.GetComponentsInChildren<Transform>(true))
                if (c.name == leaf) return c;
            return null;
        }

        // ---------------------------------------------------------------
        // ---- the cut, written back (src/propeller.js:410-422 closeUp) ----
        // ---------------------------------------------------------------
        static void Discard(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        }

        /// <summary>src/propeller.js:410-422 closeUp, written back into the
        /// real meshes. Returns the UNCUT original per MeshFilter it touched,
        /// which CutOut reads its positions and triangle numbers out of.</summary>
        static Dictionary<MeshFilter, Mesh> ApplyKept(List<MeshFilter> mfs,
            Dictionary<(int mf, int sub), int[]> kept)
        {
            var originals = new Dictionary<MeshFilter, Mesh>();
            if (kept.Count == 0) return originals;
            foreach (var group in kept.GroupBy(k => k.Key.mf))
            {
                int i = group.Key;
                if (i < 0 || i >= mfs.Count) continue;
                var mf = mfs[i];
                if (mf == null || mf.sharedMesh == null) continue;

                // NEVER mutate the imported asset's Mesh — see the class
                // header. Clone once, keep the original for CutOut to read.
                var original = mf.sharedMesh;
                originals[mf] = original;
                var clone = Object.Instantiate(original);
                clone.name = original.name + "_cut";
                foreach (var kv in group)
                {
                    int sub = kv.Key.sub;
                    if (sub < 0)
                    {
                        // the whole-node entry (a named mesh) — one buffer
                        clone.triangles = kv.Value;
                        break;
                    }
                    if (sub < clone.subMeshCount) clone.SetTriangles(kv.Value, sub, false);
                }
                clone.RecalculateBounds();
                mf.sharedMesh = clone;
            }
            return originals;
        }

        // ---------------------------------------------------------------
        // ---- the blur disc (src/propeller.js:424-472, :1061-1072) ----
        // ---------------------------------------------------------------
        static void AttachDisc(Transform pivot, PropellerSystem.PropellerInstance inst, int bladeCount)
        {
            // src/propeller.js:1060 — `p.userData.bladeCount ?? 3`, used ONLY
            // to pick the texture's streak count (PropellerSystem.
            // FinishPropeller's own closing note says the caller must apply
            // this default itself).
            int blades = bladeCount > 0 ? bladeCount : 3;
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "propeller_disc";
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            go.transform.SetParent(pivot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = inst.DiscLocalRotation;
            // src/propeller.js:1061 `blurDisc(radius * 1.02, blades)` is a
            // RADIUS; Unity's unit Quad is 1x1, so the scale is the diameter.
            go.transform.localScale = Vector3.one * (inst.Radius * 2f * 1.02f);

            var mat = new Material(Shader.Find("TbilisiFly/PropDisc"))
            { name = "propeller_disc_mat", color = new Color(0f, 0f, 0f, 0f) };
            mat.SetTexture("_MainTex", DiscTexture(blades));
            var mr = go.GetComponent<Renderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.enabled = false;              // src/propeller.js:470 `mesh.visible = false`
            inst.Disc = go.transform;
            inst.DiscMaterial = mat;
        }

        static Texture2D DiscTexture(int blades)
        {
            if (_discTex.TryGetValue(blades, out var t) && t != null) return t;
            t = PropellerSystem.BuildBlurDiscTexture(blades);
            t.name = $"prop_blur_{blades}";
            _discTex[blades] = t;
            return t;
        }

        /// <summary>src/propeller.js:705-706's own blade material — vertex
        /// colours, roughness 0.42, metalness 0.55. Only reached by the
        /// built-from-scratch path and as a last-resort fallback for a cut
        /// blade whose source renderer has no material at all.</summary>
        static Material BladeMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "propeller_blade_mat" };
            var c = new Color(0x15 / 255f, 0x16 / 255f, 0x1a / 255f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - 0.42f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.55f);
            return m;
        }
    }
}
