using System.Collections.Generic;
using System.Text.RegularExpressions;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/airport.js:372-977 — the WAREHOUSE INTERIOR: three work
    /// tables, the ammo crates, and the pistols/rifle laid out on the tables.
    /// This is pure data/mesh-utility, no scene dependency, no GameObject
    /// instantiation of its own (that is Editor/BuildAirportDetail.cs's job,
    /// mirroring how AirportBuilder.cs itself splits "layout math" from
    /// "the caller builds GameObjects") — EXCEPT <see cref="FlattenProp"/>,
    /// which genuinely needs to walk a Transform hierarchy and therefore
    /// takes one, like TerrainBuilder/BuildingBuilder's mesh-building helpers
    /// already do elsewhere in this port.
    ///
    /// SCOPE — covered: flattenProp (airport.js:422-455), the three tables'
    /// spot math (airport.js:500-549), the ammo-crate offset math
    /// (airport.js:752-819), the gun-layout math (airport.js:913-971), and —
    /// added in this pass — the shooting-range mesh surgery (airport.js:564-
    /// 738, 820-911; the ammo crates nested inside that same GLB-load callback
    /// at :739-819 were already covered above and are not duplicated), as
    /// Section E below: GLB-node classification by name (gates / paper-carrier
    /// lanes / counter slabs), the gate hinge-pivot rig, the carrier/paper
    /// hinge-pivot rig, firingZ/endZ, the counter-fill TRIANGLE-MERGE algorithm,
    /// and the pitch used to park two mirrored range units side by side.
    /// NOT covered: the warehouse crane (airport.js:498, src/crane.js, a
    /// wholly separate ~500-line file not owned by any unit in this task) and
    /// the per-frame gate/paper swing animator (airport.js:861-906), ported
    /// separately as the pure-math <see cref="RangeDoorsPaper"/> class
    /// (City/RangeDoorsPaper.cs) since it is driven every frame rather than
    /// built once — see that file's own header for the integration seam.
    ///
    /// INTEGRATION SEAM WITH ShedFill.cs (sibling unit, src/shed.js port):
    /// this file and BuildAirportDetail.cs never call into ShedFill directly
    /// — they only need the warehouse's OWN measured size (<see cref="ShedSize"/>),
    /// which BuildAirportDetail.cs obtains either by reading an existing
    /// "warehouse" GameObject's bounds in the scene, or via
    /// <see cref="EstimateWarehouseSizeFallback"/> below when no such object
    /// exists yet. Once ShedFill.cs exposes a real ShedShell-like struct
    /// (width/height/length), its caller should prefer that over both paths
    /// here — see BuildAirportDetail.cs's own header for exactly where.
    ///
    /// HANDEDNESS: every number in this file is a SHED-LOCAL offset (airport.js's
    /// own x across the width / z along the length / y up from the slab), to be
    /// applied as a Transform.localPosition/localRotation on a GameObject parented
    /// under the warehouse's own (already correctly oriented) Transform. Per the
    /// task brief's explicit instruction, shed-local offsets are NOT lat/lon and
    /// are NOT separately mirrored here — "anything you build already-placed as a
    /// child of an existing (correctly-oriented) shed Transform needs no additional
    /// flip." All constants below are therefore carried over from airport.js
    /// VERBATIM, unnegated, to be read directly as Unity local-space numbers.
    /// This is stated as the explicit, documented assumption for this port unit;
    /// the final sign convention should be re-verified once ShedFill.cs lands and
    /// the real warehouse GameObject can be inspected in the editor (something
    /// this agent cannot do — see the final report).
    /// </summary>
    public static class AirportDetail
    {
        // =====================================================================
        // A. flattenProp — airport.js:422-455. Collapses a many-mesh prop (the
        // table is 154 meshes) into one Mesh per distinct Material, using
        // Unity's native Mesh.CombineMeshes(mergeSubMeshes:true) in place of
        // three.js's mergeGeometries. Editor-time tooling only (the brief: "this
        // is editor-time tooling ... performance of the merge itself does not
        // matter") — safe to call from a batch-mode bake script.
        //
        // Each CombineInstance's transform is baked relative to `root`'s OWN
        // local frame (root.worldToLocalMatrix * child.localToWorldMatrix),
        // exactly matching airport.js:430's `geo.applyMatrix4(o.matrixWorld)`
        // on a freshly-loaded, not-yet-parented glTF scene (whose matrixWorld
        // IS its local frame, because it has no parent yet) — so the returned
        // container sits in exactly the asset's own authored local space,
        // ready for the caller to scale/offset/clone the way airport.js's
        // `src.clone(true)` does afterward.
        //
        // GOTCHA (documented, not hit personally — found by inspecting the
        // .meta files before writing this): the FBX importer for these prop
        // kits ships with `isReadable: 0` by default (Assets/Resources/Art/Props/
        // wooden_table.fbx.meta etc.), and Mesh.CombineMeshes requires CPU-
        // readable source meshes. The caller MUST force isReadable=true (and
        // reimport) on any FBX it intends to flatten BEFORE instantiating it —
        // done once, in BuildAirportDetail.cs, not here (this file has no
        // AssetDatabase/ModelImporter access by design, so it stays usable at
        // runtime too, not just in the editor).
        // =====================================================================
        public static GameObject FlattenProp(Transform root)
        {
            var byMat = new Dictionary<Material, List<CombineInstance>>();
            Matrix4x4 rootInv = root.worldToLocalMatrix;

            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterial == null) continue;
                var mat = mr.sharedMaterial;
                if (!byMat.TryGetValue(mat, out var list))
                    byMat[mat] = list = new List<CombineInstance>();
                list.Add(new CombineInstance
                {
                    mesh = mf.sharedMesh,
                    transform = rootInv * mf.transform.localToWorldMatrix,
                });
            }
            // SkinnedMeshRenderers are intentionally not walked: airport.js's
            // own flattenProp only ever sees o.isMesh (never o.isSkinnedMesh)
            // for the two static prop kits (wooden_table/shooting_range) this
            // is used on — same scope as the JS.

            if (byMat.Count == 0) return root.gameObject; // nothing merged — airport.js:454

            var container = new GameObject(root.name + "_flat");
            int meshes = 0;
            foreach (var kv in byMat)
            {
                var combined = new Mesh
                {
                    name = root.name + "_" + kv.Key.name,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                };
                combined.CombineMeshes(kv.Value.ToArray(), true, true);
                var sub = new GameObject(kv.Key.name);
                sub.transform.SetParent(container.transform, false); // identity local
                var smf = sub.AddComponent<MeshFilter>();
                smf.sharedMesh = combined;
                var smr = sub.AddComponent<MeshRenderer>();
                smr.sharedMaterial = kv.Key;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                smr.receiveShadows = true;
                meshes++;
            }
            return meshes > 0 ? container : root.gameObject;
        }

        // =====================================================================
        // ShedSize — the warehouse's own measurements in shed-local metres
        // (x across the width, z along the length, y up from the slab), the
        // Unity analogue of airport.js:403's `d = g.userData.shedShell`.
        // =====================================================================
        public struct ShedSize
        {
            public float Width, Height, Length;
        }

        /// <summary>
        /// Self-contained fallback for <see cref="ShedSize"/> with NO scene
        /// dependency at all — usable from a probe with nothing loaded. Computed
        /// from constants already public and cross-checked elsewhere in this
        /// port: PlaneHallLayout.ModelW/ModelL (16.0 / 46.3, the SAME
        /// warehouse.glb footprint in "model units", PlaneHallLayout.cs:61) and
        /// the base model height, which ShedLayoutProbe.cs:29 already pins at
        /// 5.0 m (`unscaled = new Vector3(16.0f, 5.0f, 46.3f)`) — so this file
        /// is not guessing a new number, it is reusing one already verified
        /// against src/shed.js's own "ships at 16 x 46 x 5 m" comment (shed.js:7).
        /// ShedLayout.Base (=3) is the warehouse's own scale factor
        /// (airport.js:385 `scale: 3`). Result: 48 x 15 x 138.9 m.
        ///
        /// Prefer a real measurement (an actual "warehouse" GameObject's
        /// renderer bounds, or — once it exists — ShedFill's own ShedShell)
        /// over this fallback whenever one is available; see this file's
        /// class header and BuildAirportDetail.cs for the seam.
        /// </summary>
        public static ShedSize EstimateWarehouseSizeFallback()
        {
            const float modelH = 5f; // ShedLayoutProbe.cs:29, shed.js:7
            // ShedLayout.cs's own `Base` (=3f, shed.js:30) is a PRIVATE const on
            // that class (sibling-owned file, not touched here) — mirrored as a
            // literal rather than widening its visibility. airport.js:385 passes
            // `scale: 3` explicitly for the warehouse, matching it.
            const float scale = 3f;
            return new ShedSize
            {
                Width = PlaneHallLayout.ModelW * scale,
                Height = modelH * scale,
                Length = PlaneHallLayout.ModelL * scale,
            };
        }

        // =====================================================================
        // B. The three work tables — airport.js:500-549.
        // =====================================================================
        public const float TableH = 1.2f;  // airport.js:511 TABLE_H
        public const float TableGap = 0.4f; // airport.js:520 GAP
        public const float WallClear = 4.5f; // airport.js:515-516

        public struct TableLayout
        {
            public float Scale;              // tsc = TABLE_H / rawBounds.size.y
            public Vector3 ModelLocalOffset;  // offset of the merged mesh inside its holder
            public float TableLength;         // tlen, scaled
            public Vector3[] Spots;           // 3 shed-local positions (x, 0, z); yaw is always 0
        }

        /// <summary>airport.js:508-521. `rawBounds` is the flattened table
        /// prop's own bounds (<see cref="FlattenProp"/>'s output, measured at
        /// identity — airport.js's `tb = new THREE.Box3().setFromObject(src)`).</summary>
        public static TableLayout ComputeTableLayout(ShedSize size, Bounds rawBounds,
            float tableH = TableH, float gap = TableGap, float wallClear = WallClear)
        {
            float rawH = rawBounds.size.y;
            float tsc = tableH / (Mathf.Abs(rawH) > 1e-6f ? rawH : 1f);
            Vector3 tc = rawBounds.center;
            float tlen = rawBounds.size.z * tsc;
            float inX = size.Width / 2f - wallClear;
            float inZ = size.Length / 2f - wallClear;

            var spots = new Vector3[3];
            for (int i = 0; i < 3; i++)
                spots[i] = new Vector3(-inX, 0f, -inZ + i * (tlen + gap));

            return new TableLayout
            {
                Scale = tsc,
                ModelLocalOffset = new Vector3(-tc.x * tsc, -rawBounds.min.y * tsc, -tc.z * tsc),
                TableLength = tlen,
                Spots = spots,
            };
        }

        // =====================================================================
        // C. The ammo crates — airport.js:752-819.
        // =====================================================================
        public const float AmmoScale = 5f;                                  // airport.js:753
        public static readonly Vector3 AmmoSize = new Vector3(0.97f, 0.40f, 0.44f); // airport.js:754
        public static readonly Vector3 AmmoBase = new Vector3(5.21f, 0f, -15.11f);  // airport.js:755
        public const float AmmoYaw = -0.0629f;                               // airport.js:756

        public struct CrateSpot
        {
            public string Id;
            public Vector3 Pos;   // shed-local
            public float YawRad;
        }

        /// <summary>airport.js:757-766 — fully self-contained from the
        /// constants above, no measurement needed.</summary>
        public static CrateSpot[] ComputeCrateSpots()
        {
            float w = AmmoSize.x * AmmoScale; // airport.js:758
            float h = AmmoSize.y * AmmoScale; // airport.js:759
            // crate-local +X expressed in shed space — airport.js:760
            Vector3 right = new Vector3(Mathf.Cos(AmmoYaw), 0f, -Mathf.Sin(AmmoYaw));
            Vector3 next = AmmoBase + right * w; // airport.js:761

            return new[]
            {
                new CrateSpot { Id = "ammo_box",       Pos = AmmoBase, YawRad = AmmoYaw },
                new CrateSpot { Id = "ammo_box_pair",  Pos = next, YawRad = AmmoYaw },
                new CrateSpot { Id = "ammo_box_stack", Pos = new Vector3(next.x, h, next.z), YawRad = AmmoYaw },
            };
        }

        // =====================================================================
        // D. Pistols + rifle on the tables — airport.js:913-971.
        // =====================================================================
        public struct GunSpot
        {
            public string Id;
            public float X, Z;   // shed-local
            public float RyRad;  // airport.js's `ry`
            public bool IsRifle;
        }

        /// <summary>airport.js:919, 937-942. `table0Spot`/`table1Spot` are
        /// <see cref="TableLayout.Spots"/>[0]/[1] (fx,fz / ax,az in the JS).</summary>
        public static GunSpot[] ComputeGunLayout(Vector3 table0Spot, Vector3 table1Spot)
        {
            float fx = table0Spot.x, fz = table0Spot.z;
            float ax = table1Spot.x, az = table1Spot.z;
            return new[]
            {
                new GunSpot { Id = "makarov", X = fx - 0.22f, Z = fz - 0.12f, RyRad = 0.35f,  IsRifle = false },
                new GunSpot { Id = "beretta", X = fx + 0.22f, Z = fz + 0.14f, RyRad = -0.55f, IsRifle = false },
                new GunSpot { Id = "akm",     X = ax + 0.02f, Z = az + 0.05f, RyRad = Mathf.PI / 2f + 0.06f, IsRifle = true },
            };
        }

        /// <summary>airport.js:948 — pistol local position, laid flat on the table.</summary>
        public static Vector3 PistolLocalPos(GunSpot g) => new Vector3(g.X, TableH, g.Z);

        /// <summary>airport.js:949 — pistol local rotation (yaw only).</summary>
        public static Quaternion PistolLocalRot(GunSpot g) => Quaternion.Euler(0f, g.RyRad * Mathf.Rad2Deg, 0f);

        /// <summary>airport.js:959 — half the rifle's own measured depth (its
        /// local-Z extent at identity transform, scale already applied) plus
        /// the 4 mm clearance, added onto TABLE_H. `depthZ` = box.max.z -
        /// box.min.z, measured by the caller (needs a loaded mesh — editor
        /// territory, see BuildAirportDetail.cs).</summary>
        public static float RifleLiftY(float depthZ) => TableH + depthZ * 0.5f + 0.004f;

        /// <summary>airport.js:958 — `gun.rotation.set(-Math.PI/2, ry, 0, 'YXZ')`.
        /// THREE's Euler order 'YXZ' composes as finalRotation = Ry(y) * Rx(x) * Rz(z)
        /// (z=0 here), i.e. first roll the rifle onto its side about its own X axis,
        /// then yaw it to lie along the bench. Ported as the equivalent quaternion
        /// composition rather than relying on any engine's own Euler-order default,
        /// since those orders are NOT guaranteed to match between three.js and Unity
        /// — composing explicitly from named axis rotations sidesteps that entirely.
        /// Per this file's header, local-space numbers are carried over unmirrored.</summary>
        public static Quaternion RifleLocalRot(float ryRad) =>
            Quaternion.AngleAxis(ryRad * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis(-90f, Vector3.right);

        /// <summary>airport.js:959 full local position.</summary>
        public static Vector3 RifleLocalPos(GunSpot g, float depthZ) => new Vector3(g.X, RifleLiftY(depthZ), g.Z);

        // =====================================================================
        // E. The shooting range — airport.js:564-738, 820-911. The single
        // hardest piece in the file (its own prior-pass note): a loaded GLB's
        // nodes are classified by NAME into three behaviours (gates that
        // swing, paper that tears, everything-else that is solid), two of
        // which need a hinge pivot built from the node's own measured bounds,
        // and the counter slabs need their TRIANGLES read directly to find
        // the gaps between firing points — a flat quad has no vertex at a
        // gap, so sorting corners cannot find it.
        //
        // HOUSE STYLE: like FlattenProp above, these take real Transforms and
        // mutate/measure them (the mesh surgery genuinely needs a live scene
        // graph) — the GameObject INSTANTIATION (loading shooting_range.fbx,
        // calling these in order, final collider wiring, Debug.Log lines) is
        // Editor/BuildAirportDetail.cs's job, exactly mirroring how Section A
        // splits "layout math" from "the caller builds GameObjects".
        //
        // ORDER DEPENDENCY (airport.js's own order, preserved): classify ->
        // build gate pivots -> build carriers -> build paper pivots ->
        // RangeFiringEnd/ComputeRangePitch (measured AFTER doors/paper leave
        // the scene root, airport.js:649-660/681-683, so they see only the
        // counters + static furniture that is left) -> BuildCounterBlocks ->
        // FlattenProp(sceneRoot) (airport.js:724, now that doors/paper/counter
        // readings are done) -> assemble the template -> clone for the second
        // range holder. BuildAirportDetail.cs follows this exact order.
        //
        // HANDEDNESS: same as this file's own header — every number here is a
        // PROP-LOCAL measurement off the shooting_range.fbx's own authored
        // axes (its lane runs along its own Z, its width along its own X),
        // never a lat/lon or a game-frame coordinate, so none of it is
        // mirrored. The asset is read at identity (freshly instantiated,
        // unparented) exactly as airport.js's own `rg.scene` is fresh out of
        // the GLTFLoader before anything reparents it — the caller must keep
        // that invariant (do not pre-parent/pre-rotate the loaded prefab
        // instance before calling ClassifyRangeMeshes).
        // =====================================================================

        /// <summary>airport.js:584 — lowercase, strip everything but [a-z0-9].</summary>
        static string RangeKey(string n) => Regex.Replace((n ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        /// <summary>airport.js:586,595 — "SR_Divider_Door_dup_4.Shape_0" -&gt;
        /// "SR_Divider_Door_dup_4" (split on "shape", case-insensitive, keep
        /// the part before it).</summary>
        static string RangeGroupId(string n) => Regex.Split(n ?? "", "shape", RegexOptions.IgnoreCase)[0];

        public struct LanePart { public string Lane; public bool IsPaper; public Transform Mesh; }

        public sealed class RangeClassified
        {
            public readonly Dictionary<string, List<Transform>> Gates = new Dictionary<string, List<Transform>>();
            public readonly List<LanePart> LaneParts = new List<LanePart>();
            public readonly List<Transform> Counters = new List<Transform>();
        }

        /// <summary>airport.js:581-601 — walks every mesh under `sceneRoot` and
        /// buckets it by name. Pure read: nothing is reparented yet.</summary>
        public static RangeClassified ClassifyRangeMeshes(Transform sceneRoot)
        {
            var result = new RangeClassified();
            foreach (var mf in sceneRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                var t = mf.transform;
                string n = t.name ?? "";
                string key = RangeKey(n);
                if (key.StartsWith("srdividerdoor"))
                {
                    string gate = RangeGroupId(n);
                    if (!result.Gates.TryGetValue(gate, out var list))
                        result.Gates[gate] = list = new List<Transform>();
                    list.Add(t);
                }
                else if (key.Contains("targetpaper") || key.Contains("targetholder"))
                {
                    result.LaneParts.Add(new LanePart
                    {
                        Lane = RangeGroupId(n),
                        IsPaper = key.Contains("targetpaper"),
                        Mesh = t,
                    });
                }
                else if (key.Contains("rangedividershapecounter"))
                {
                    result.Counters.Add(t);
                }
            }
            return result;
        }

        static Bounds EncapsulateB(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        /// <summary>Combined world Renderer bounds of a set of Transforms, or
        /// null if none carry a Renderer.</summary>
        static Bounds? WorldBoundsOf(IEnumerable<Transform> ts)
        {
            Bounds? b = null;
            foreach (var t in ts)
            {
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                b = b.HasValue ? EncapsulateB(b.Value, r.bounds) : r.bounds;
            }
            return b;
        }

        /// <summary>airport.js:619-632 — one hinge pivot per gate, planted on
        /// its own leading (min-X) edge at y=0 (the slab), its meshes reparented
        /// under it so rotation.y alone swings the whole door. `doorsRoot` is
        /// expected at WORLD identity (a fresh, unparented Transform) when this
        /// runs, matching airport.js:574's fresh `new THREE.Group()` — see this
        /// section's header. Returns the pivots, one per gate, in no particular
        /// order (airport.js iterates a Map, same non-determinism).</summary>
        public static List<Transform> BuildGatePivots(Dictionary<string, List<Transform>> gates, Transform doorsRoot)
        {
            var pivots = new List<Transform>();
            foreach (var kv in gates)
            {
                var box = WorldBoundsOf(kv.Value);
                if (!box.HasValue) continue;
                var b = box.Value;
                var pivotGo = new GameObject("range_gate");
                // airport.js:627 pivot.position.set(box.min.x, 0, (box.min.z+box.max.z)/2)
                pivotGo.transform.position = new Vector3(b.min.x, 0f, (b.min.z + b.max.z) / 2f);
                // airport.js:628 doorsRoot.add(pivot) — doorsRoot sits at world
                // identity here, so "keep local as set" and "keep world as set"
                // agree; worldPositionStays:true is used so this stays correct
                // even if a caller parents doorsRoot before calling this.
                pivotGo.transform.SetParent(doorsRoot, true);
                foreach (var m in kv.Value)
                    m.SetParent(pivotGo.transform, true); // airport.js:630 pivot.attach(m)
                pivots.Add(pivotGo.transform);
            }
            return pivots;
        }

        /// <summary>airport.js:602-617 — every lane part goes under `paperRoot`
        /// first (airport.js:602, the half of `loose` that carries a lane id),
        /// then one "range_carrier" group is made per distinct lane id and the
        /// parts for that lane reparented into it. Returns lane id -&gt; carrier.</summary>
        public static Dictionary<string, Transform> BuildCarriers(List<LanePart> laneParts, Transform paperRoot)
        {
            foreach (var lp in laneParts)
                lp.Mesh.SetParent(paperRoot, true); // airport.js:602 paperRoot.attach(o)

            var carriers = new Dictionary<string, Transform>();
            foreach (var lp in laneParts)
            {
                if (!carriers.TryGetValue(lp.Lane, out var carrier))
                {
                    var cgo = new GameObject("range_carrier");
                    // airport.js:610-612: fresh Group() at local (0,0,0), added
                    // to paperRoot with .add() (keep-local semantics) — a plain
                    // SetParent(paperRoot, false) on a just-created (world-
                    // origin) GameObject reproduces that exactly.
                    cgo.transform.SetParent(paperRoot, false);
                    carriers[lp.Lane] = carrier = cgo.transform;
                }
                lp.Mesh.SetParent(carrier, true); // airport.js:616 c.attach(o)
            }
            return carriers;
        }

        /// <summary>airport.js:634-648 — paper hangs from its TOP edge: one
        /// hinge pivot per sheet, planted at (midX, box.max.y, midZ) of that
        /// sheet's own world bounds, parented under its carrier.</summary>
        public static List<Transform> BuildPaperPivots(Dictionary<string, Transform> carriers, List<LanePart> laneParts)
        {
            var pivots = new List<Transform>();
            foreach (var lp in laneParts)
            {
                if (!lp.IsPaper) continue;
                var r = lp.Mesh.GetComponent<Renderer>();
                if (r == null) continue;
                var b = r.bounds;
                var pivotGo = new GameObject("range_paper");
                // airport.js:643 pivot.position.set((min.x+max.x)/2, max.y, (min.z+max.z)/2)
                pivotGo.transform.position = new Vector3((b.min.x + b.max.x) / 2f, b.max.y, (b.min.z + b.max.z) / 2f);
                if (!carriers.TryGetValue(lp.Lane, out var carrier)) continue;
                pivotGo.transform.SetParent(carrier, true); // airport.js:644 carrier.add(pivot)
                lp.Mesh.SetParent(pivotGo.transform, true); // airport.js:646 pivot.attach(sheet)
                pivots.Add(pivotGo.transform);
            }
            return pivots;
        }

        /// <summary>airport.js:649-660. Must be called AFTER doors/paper have
        /// left `sceneRoot` (see this section's "order dependency" note) —
        /// `sceneRoot` at this point contains only the counters and the solid
        /// furniture, exactly like the JS's post-reparent `scene`.</summary>
        public static (float firingZ, float endZ) RangeFiringEnd(List<Transform> counters, Transform sceneRoot)
        {
            var fireBox = WorldBoundsOf(counters);
            float firingZ = fireBox.HasValue ? (fireBox.Value.min.z + fireBox.Value.max.z) / 2f : 0f;

            Bounds? envZ = null;
            foreach (var r in sceneRoot.GetComponentsInChildren<Renderer>(true))
                envZ = envZ.HasValue ? EncapsulateB(envZ.Value, r.bounds) : r.bounds;

            float endZ = firingZ;
            if (envZ.HasValue)
            {
                float dMax = Mathf.Abs(envZ.Value.max.z - firingZ);
                float dMin = Mathf.Abs(envZ.Value.min.z - firingZ);
                endZ = dMax > dMin ? envZ.Value.max.z : envZ.Value.min.z; // airport.js:659-660
            }
            return (firingZ, endZ);
        }

        /// <summary>airport.js:681-683 — PITCH = the range unit's own measured
        /// width (its env box's X size) plus 0.6 m shoulder clearance. Called
        /// at the same point in the pipeline as <see cref="RangeFiringEnd"/>
        /// (same reason: before the counter-fill widens the first/last block
        /// past the unit's own edge, or the widened blocks would push the two
        /// units apart and reopen the gap between them — airport.js's own
        /// comment at :679-680).</summary>
        public static float ComputeRangePitch(Transform sceneRoot)
        {
            Bounds? b = null;
            foreach (var r in sceneRoot.GetComponentsInChildren<Renderer>(true))
                b = b.HasValue ? EncapsulateB(b.Value, r.bounds) : r.bounds;
            float rsx = b?.size.x ?? 0f;
            return rsx + 0.6f;
        }

        public struct CounterBlock { public Vector3 Center; public Vector3 Size; }

        /// <summary>airport.js:662-720 — the triangle-merge counter fill.
        /// Reads each counter slab's own TRIANGLES in world space (a flat quad
        /// has no vertex at a gap, so sorting corners cannot find one — see
        /// the JS's own long comment at :667-670), sorts by their world min-X,
        /// merges touching triangles into per-counter segments, widens the
        /// first/last segment out to +/-pitch/2 so the firing line runs end to
        /// end across the 60 cm slot between two range units, then keeps only
        /// segments wide/deep enough to be worth a collider (:713).</summary>
        public static List<CounterBlock> BuildCounterBlocks(List<Transform> counters, float pitch)
        {
            var blocks = new List<CounterBlock>();
            foreach (var c in counters)
            {
                var mf = c.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                Vector3[] verts = mesh.vertices;      // local space
                int[] tris = mesh.triangles;
                Matrix4x4 m = c.localToWorldMatrix;

                var triBoxes = new List<Bounds>(tris.Length / 3);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 v0 = m.MultiplyPoint3x4(verts[tris[i]]);
                    Vector3 v1 = m.MultiplyPoint3x4(verts[tris[i + 1]]);
                    Vector3 v2 = m.MultiplyPoint3x4(verts[tris[i + 2]]);
                    var tb = new Bounds(v0, Vector3.zero);
                    tb.Encapsulate(v1);
                    tb.Encapsulate(v2);
                    triBoxes.Add(tb);
                }
                triBoxes.Sort((a, b) => a.min.x.CompareTo(b.min.x)); // airport.js:700

                var segs = new List<Bounds>();
                foreach (var t in triBoxes)
                {
                    if (segs.Count > 0)
                    {
                        var last = segs[segs.Count - 1];
                        if (t.min.x <= last.max.x + 0.05f) // airport.js:704 — same counter
                        {
                            last.Encapsulate(t);
                            segs[segs.Count - 1] = last;
                            continue;
                        }
                    }
                    segs.Add(t); // airport.js:705 — across a gateway
                }

                if (segs.Count > 0)
                {
                    // airport.js:708-709 — widen the first/last segment to the
                    // unit's own +/-pitch/2 edge (the SAME segment when there is
                    // only one, both edits applied in sequence, exactly as the
                    // JS does when segs.length === 1).
                    var first = segs[0];
                    first.SetMinMax(new Vector3(Mathf.Min(first.min.x, -pitch / 2f), first.min.y, first.min.z), first.max);
                    segs[0] = first;
                    int lastIdx = segs.Count - 1;
                    var lastSeg = segs[lastIdx];
                    lastSeg.SetMinMax(lastSeg.min, new Vector3(Mathf.Max(lastSeg.max.x, pitch / 2f), lastSeg.max.y, lastSeg.max.z));
                    segs[lastIdx] = lastSeg;
                }

                foreach (var b in segs)
                {
                    var size = b.size;
                    if (size.x < 0.3f || size.z < 0.15f) continue; // airport.js:713
                    blocks.Add(new CounterBlock
                    {
                        Center = new Vector3((b.min.x + b.max.x) / 2f, b.max.y / 2f, (b.min.z + b.max.z) / 2f),
                        Size = new Vector3(size.x, b.max.y, size.z), // airport.js:714 — box 0..max.y tall
                    });
                }
            }
            return blocks;
        }
    }
}
