using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    // Port of src/shed.js's remainder — everything ShedLayout.cs's header says
    // it did NOT port: loading warehouse.fbx, centring+scaling it, cutting the
    // roller door out of both gable ends, building the pad/ramp meshes from
    // ShedLayout's numbers, and registering plain Unity colliders. ShedLayout.cs
    // itself (siting trig, LevelPad, Ramps, FloorTileUvScale) is NOT modified —
    // this file only calls into it.
    //
    // EDITOR-TIME ONLY, BY NATURE, NOT BY CHOICE OF FOLDER. This sits under
    // Scripts/Flight (next to ShedLayout.cs, per the task brief) rather than
    // Assets/Editor, but every public entry point needs AssetDatabase (to find
    // warehouse.fbx) and PrefabUtility (to instantiate it the way
    // BuildFlyableScene.cs's established pattern does) — both editor-only APIs.
    // That is correct: this is a one-shot BAKE step that produces a saved scene,
    // never something the shipped game calls at runtime (mirroring why
    // PlaneCatalog.cs's header says the equivalent JS runtime-mesh-measurement
    // functions are out of scope for now). Everything is wrapped in
    // #if UNITY_EDITOR so the file still compiles, as a no-op, in a player build.
    //
    // ================================= HANDEDNESS =================================
    // Two different rules apply to two different kinds of geometry here, and
    // mixing them up is the whole risk in this file:
    //
    //  1. The IMPORTED warehouse.fbx mesh (the walls/roof the door is cut out
    //     of) is already in Unity's own, correctly-mirrored local space — the
    //     FBX importer did that work once, at import time (see GeoFrame.cs's
    //     "VERIFIED 2026-10-26 against glTFast" note; the FBX importer applies
    //     the same mirror). The door-cut vertex test below (CutDoors) works
    //     entirely in that already-correct space and needs NO extra flip —
    //     exactly what the task brief says.
    //
    //  2. The PAD and RAMP meshes are NEW geometry this file builds by hand
    //     from ShedLayout's game-frame math (halfW/halfL/padDepth/K/zNear/zFar
    //     are all plain game-frame numbers, same sign convention as the JS).
    //     These are authored UNMIRRORED, in the JS's own local numbers — x
    //     across the width, z along the length, both centred on 0 exactly like
    //     src/shed.js's BoxGeometry calls — then mirrored and flipped exactly
    //     ONCE at the end, by BuildMirroredMesh, the SAME convention
    //     LandmarkPlacer.cs's MakeMesh uses (negate vertex.x, then
    //     GeoFrame.FlipWinding on the triangle array). Normals are recomputed
    //     from the final mirrored geometry (Mesh.RecalculateNormals) rather
    //     than mirrored by hand, which also makes the sheared ramp's normals
    //     correct for free.
    //
    //  3. Every CHILD POSITION placed in the shed-root's local space from
    //     game-frame math — the pad/ramp GameObjects are at (0, y, z) so this
    //     never bites them here (negating 0 is 0), but PlaneHallFill.cs's
    //     parked-plane positions are the case where it matters: a local point
    //     (x, y, z) built from game-frame math must become (-x, y, z), NOT
    //     just flipped-winding, because a point has no winding to flip. See
    //     that file's header for the derivation.
    //
    //  4. The shed ROOT's own world placement — position via GeoFrame.ToUnity,
    //     yaw via GeoFrame.YawToUnity — happens ONCE, at the very end of
    //     Build(), after every child (inner/pad/ramps) has already been built
    //     in the shed-local frame described above.
    public static class ShedFill
    {
        public const string WarehousePrefabPath = "Assets/Resources/Art/Props/warehouse.fbx";

        // src/shed.js:35 CONCRETE / src/planehall.js:55 FLOOR, as plain colours
        // for a fallback material when the caller passes none.
        public static readonly Color WarehouseConcrete = HexColor(0xd6d3ca);
        public static readonly Color HangarGrey = HexColor(0x9b9d99);

        /// <summary>src/shed.js's returned group + shell, plus the inert `park`
        /// data the warehouse call carries (src/shed.js:245-249) for whoever
        /// wires up src/game.js _parkInShed's Unity equivalent — NOT consumed
        /// here, just carried.</summary>
        public struct ShedBuildResult
        {
            public GameObject Root;
            public ShedLayout.ShedShell Shell;
            public Vector3? Park;            // shed-local metres, JS convention (unmirrored) — inert
            public float? ParkYawLocalUnity; // already converted, if Park was supplied

            // Bake-time diagnostics for ShedFillProbe.cs / tools/_probe_shedfill.mjs
            // — not consumed by anything else, just handed back so the probe
            // doesn't need InternalsVisibleTo into private helpers.
            public int SolidTrianglesBefore, SolidTrianglesAfter; // src/shed.js:87-122, summed across every door-tested mesh
            public float PadTop, PadDepth;                        // src/shed.js:195-196, for cross-check against ShedLayout.LevelPad
            public int RampsBuilt;                                // how many of the 2 gable ends actually got a ramp
        }

        /// <summary>src/shed.js's buildShed called for the warehouse (the
        /// player's base) — BASE (x3) scale, apron 7, rampLen 14, shadows on
        /// the pad, no floor-tile rescale (src/shed.js:46-51's defaults).</summary>
        public static ShedBuildResult BuildWarehouse(
            Transform parent, PlaneHallLayout.GeoMeta meta, Func<float, float, float?> heightAtGame,
            string name = "warehouse", float apron = 7f, float rampLen = 14f,
            Material material = null, Vector3? park = null, float parkYawLocalGame = 0f,
            string landmarkLabel = "საწყობი") // src/airport.js:386's literal buildShed({..., label:'საწყობი'}) call
        {
            var (px, pz) = ShedLayout.SiteFromLatLon(ShedLayout.ShedLat, ShedLayout.ShedLon, meta); // src/shed.js:54-57
            material ??= DefaultMaterial(WarehouseConcrete);
            return Build(parent, px, pz, ShedLayout.ShedYaw, new Vector3(3f, 3f, 3f), heightAtGame,
                name, apron, rampLen, padShadow: true, floorTile: 0f, material, park, parkYawLocalGame, landmarkLabel);
        }

        /// <summary>src/planehall.js:120-128's buildShed call for the plane
        /// hall — SCALE (x16/x7.5/x9.3), apron/rampLen from PlaneHallLayout,
        /// no pad shadow (under a roof), floorTile=12, and NO `park` (src/
        /// planehall.js:127's comment: giving the hall a park would make the
        /// 🛬 teleport start parking the player's machine in the museum).
        /// `pxGame`/`pzGame`/`yawGame` are PlaneHallLayout.Site(meta, SHED_YAW)'s
        /// raw-JS-sign output — the caller (the Part 3 bake script) computes
        /// that once and hands it straight in.</summary>
        public static ShedBuildResult BuildPlaneHall(
            Transform parent, float pxGame, float pzGame, float yawGame,
            Func<float, float, float?> heightAtGame, string name = "planehall", Material material = null,
            string landmarkLabel = "ავიაანგარი") // src/planehall.js:122's literal buildShed({..., label:'ავიაანგარი'}) call
        {
            material ??= DefaultMaterial(HangarGrey);
            return Build(parent, pxGame, pzGame, yawGame, PlaneHallLayout.Scale, heightAtGame,
                name, PlaneHallLayout.Apron, PlaneHallLayout.RampLen,
                padShadow: false, floorTile: 12f, material, park: null, parkYawLocalGame: 0f, landmarkLabel);
        }

        // =====================================================================
        static ShedBuildResult Build(
            Transform parent, float pxGame, float pzGame, float yawGame, Vector3 scale,
            Func<float, float, float?> heightAtGame,
            string name, float apron, float rampLen, bool padShadow, float floorTile,
            Material material, Vector3? park, float parkYawLocalGame, string landmarkLabel = null)
        {
            var prefab = ArtAssets.Load<GameObject>(WarehousePrefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"ShedFill: missing {WarehousePrefabPath}");

            // src/shed.js:62-63 `g`/`name` — stays at local identity until the
            // very end (:234-235): "g still sits at the origin: world ==
            // shed-local" (:87), which is exactly what the door-cut test below
            // relies on.
            var root = new GameObject(name);
            if (parent != null)
            {
                root.transform.SetParent(parent, false);
                // The raw-bounds measurement below relies on "world == local"
                // while root/innerGo are still at identity (src/shed.js:87's
                // own comment). That only holds if every ANCESTOR is also
                // unrotated — true for this project's actual callers (the
                // Part 3 bake script parents directly under the scene root),
                // but worth a loud warning if that ever changes rather than a
                // silently wrong pad/door-cut.
                if (parent.rotation != Quaternion.identity)
                    Debug.LogWarning($"ShedFill.Build({name}): parent '{parent.name}' has a non-identity " +
                        "rotation — the bounds/door-cut math assumes an unrotated ancestor chain and will be wrong.");
            }

            // src/shed.js:64, :74-75 `inner` — the GLB's own node matrices
            // already stand it up correctly (see ShedLayout.cs's header and
            // src/shed.js:18-22); we only ever centre/scale it, never rotate it.
            var innerGo = (GameObject)ArtAssets.Instantiate(prefab, root.transform);
            innerGo.name = "inner";
            innerGo.transform.localPosition = Vector3.zero;
            innerGo.transform.localRotation = Quaternion.identity;
            innerGo.transform.localScale = Vector3.one; // raw bounds first, scale applied after centring

            var renderers = innerGo.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                throw new InvalidOperationException($"ShedFill: {WarehousePrefabPath} instantiated with no renderers");
            foreach (var r in renderers) // src/shed.js:66-69
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }

            // src/shed.js:70 `new THREE.Box3().setFromObject(inner)` — valid
            // here because root/innerGo are BOTH still at local identity, so a
            // combined Renderer.bounds (world AABB) equals the model's own raw,
            // unscaled, unpositioned local bounds.
            Bounds box = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) box.Encapsulate(renderers[i].bounds);
            Vector3 c = box.center;
            // src/shed.js:74-75 — centre on its own footprint, sit the bottom
            // on y=0, IN THE SCALED FRAME (so a non-uniform stretch survives).
            innerGo.transform.localPosition = new Vector3(-c.x * scale.x, -box.min.y * scale.y, -c.z * scale.z);
            innerGo.transform.localScale = scale;

            var doorScaled = ShedLayout.ScaledDoor(scale); // src/shed.js:82-86
            var solids = new List<MeshFilter>();
            CutDoors(root.transform, innerGo, doorScaled, solids, out int trisBefore, out int trisAfter); // src/shed.js:87-122

            Vector3 size = Vector3.Scale(box.size, scale); // src/shed.js:124

            if (floorTile > 0f) ApplyFloorTile(innerGo, size, floorTile); // src/shed.js:127-157

            float halfW = size.x / 2f + apron; // src/shed.js:163
            float halfL = size.z / 2f + apron; // src/shed.js:164
            var padResult = ShedLayout.LevelPad(pxGame, pzGame, halfW, halfL, yawGame, heightAtGame); // src/shed.js:163-196

            // The pad gets its OWN BoxCollider inside BuildPadGameObject, not a
            // MeshCollider off the `solids` list below.
            var padGo = BuildPadGameObject(root.transform, halfW, padResult.PadDepth, halfL, material, padShadow); // :197-207

            var ramps = ShedLayout.Ramps(pxGame, pzGame, halfL, rampLen, yawGame, padResult.PadTop, doorScaled.x, heightAtGame); // :209-232
            int rampsBuilt = 0;
            foreach (var r in ramps)
            {
                if (!r.Needed) continue;
                var rampGo = BuildRampGameObject(root.transform, r, material);
                solids.Add(rampGo.GetComponent<MeshFilter>());
                rampsBuilt++;
            }

            // src/shed.js:234-235 — the ONE place position/yaw cross from
            // game-frame math into a real Unity Transform.
            root.transform.localPosition = GeoFrame.ToUnity(pxGame, padResult.PadTop, pzGame);
            root.transform.localRotation = Quaternion.Euler(0f, GeoFrame.YawToUnity(yawGame) * Mathf.Rad2Deg, 0f);

            // Physics: plain Unity Colliders ARE the collision registry in this
            // port (no citymap._addCollidable equivalent exists — src/shed.js:
            // 251-262 is explicitly City territory this port does not touch).
            // The shed shell (walls/roof, now door-cut) genuinely needs a
            // MeshCollider — it is a concave barrel-vault with two open bays,
            // and a BoxCollider would seal the doorways shut.
            foreach (var mf in solids)
            {
                if (mf == null) continue; // the pad placeholder above
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }

            var shell = new ShedLayout.ShedShell
            {
                Width = size.x, Length = size.z, Height = size.y,
                DoorHalfW = doorScaled.x, DoorH = doorScaled.y, DoorZ = doorScaled.z,
            };

            // src/shed.js:258-262's _addCollidable bookkeeping is superseded —
            // plain Unity Colliders (above) already give "can stand on it /
            // can't walk through it" for free, no separate registry needed.
            //
            // src/shed.js:268 `if (label) citymap.registerLandmark?.(g, label,
            // {pose:false, immune:true})` DOES have a live Unity counterpart
            // now (City/LandmarkTag.cs — see that file's own header, which
            // names this exact call site). Both real callers (BuildWarehouse/
            // BuildPlaneHall above) pass their JS-literal label, so this fires
            // for both the player's warehouse AND the plane-hall museum,
            // exactly matching src/airport.js:386 / src/planehall.js:122.
            // pose:false -> NoPose=true (a stray round must never tilt the
            // 152 m shed and drop one end into the ground); immune:true ->
            // Immune=true (the player's base/the collection "may not be shot
            // down or crashed into rubble" — shed.js's own comment).
            if (!string.IsNullOrEmpty(landmarkLabel))
            {
                var tag = root.AddComponent<LandmarkTag>();
                tag.LandmarkName = landmarkLabel;
                tag.Immune = true;
                tag.NoPose = true;
                // citymap.js:1793 maxHp = clamp(900 + h*30 + footprint*0.2, 1200, 6000),
                // measured off the real extruded size computed above (same
                // formula City/LandmarkRenderer.cs's AttachLandmarkTag uses).
                float h = Mathf.Max(1f, size.y);
                float footprint = Mathf.Max(1f, size.x * size.z);
                tag.MaxHp = Mathf.Clamp(900f + h * 30f + footprint * 0.2f, 1200f, 6000f);
            }

            return new ShedBuildResult
            {
                Root = root, Shell = shell,
                Park = park,
                ParkYawLocalUnity = park.HasValue ? GeoFrame.YawToUnity(parkYawLocalGame) : (float?)null,
                SolidTrianglesBefore = trisBefore, SolidTrianglesAfter = trisAfter,
                PadTop = padResult.PadTop, PadDepth = padResult.PadDepth,
                RampsBuilt = rampsBuilt,
            };
        }

        /// <summary>
        /// Self-contained reachability check for the src/shed.js:268
        /// registerLandmark port added above — callable with
        /// <c>-executeMethod TbilisiFly.Flight.ShedFill.ProbeLandmarkTag</c>.
        /// Builds both real callers (warehouse + plane hall) with the same
        /// synthetic meta/height function ShedFillProbe.cs uses, and proves
        /// City/LandmarkTag.cs is actually attached with Immune=true/NoPose=true
        /// — not just that the code compiles.
        /// </summary>
        public static void ProbeLandmarkTag()
        {
            var meta = new PlaneHallLayout.GeoMeta
            {
                OriginLat = GeoFrame.Lat0, OriginLon = GeoFrame.Lon0,
                MLat = GeoFrame.MetresPerLat, MLon = GeoFrame.MetresPerLon,
            };
            float? HeightAt(float x, float z) => 100f + x * 0.01f + Mathf.Sin(z * 0.003f) * 3f;

            var wh = BuildWarehouse(null, meta, HeightAt);
            var whTag = wh.Root.GetComponent<LandmarkTag>();
            Debug.Log($"SHEDTAG id=warehouse hasTag={(whTag != null ? 1 : 0)} " +
                      $"name={whTag?.LandmarkName} immune={(whTag?.Immune ?? false).ToString().ToLowerInvariant()} " +
                      $"noPose={(whTag?.NoPose ?? false).ToString().ToLowerInvariant()} maxHp={whTag?.MaxHp:F1}");
            UnityEngine.Object.DestroyImmediate(wh.Root);

            var (hx, hz, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw);
            var hall = BuildPlaneHall(null, hx, hz, ShedLayout.ShedYaw, HeightAt);
            var hallTag = hall.Root.GetComponent<LandmarkTag>();
            Debug.Log($"SHEDTAG id=hall hasTag={(hallTag != null ? 1 : 0)} " +
                      $"name={hallTag?.LandmarkName} immune={(hallTag?.Immune ?? false).ToString().ToLowerInvariant()} " +
                      $"noPose={(hallTag?.NoPose ?? false).ToString().ToLowerInvariant()} maxHp={hallTag?.MaxHp:F1}");
            UnityEngine.Object.DestroyImmediate(hall.Root);
        }

        // ---- the door cut: src/shed.js:87-122 ------------------------------
        static void CutDoors(Transform shedRoot, GameObject inner, Vector3 door, List<MeshFilter> solidsOut,
            out int trisBefore, out int trisAfter)
        {
            trisBefore = 0; trisAfter = 0;
            foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
            {
                var rend = mf.GetComponent<Renderer>();
                // src/shed.js:93 — strip lights (the "Emissive" material) are
                // decor, not something the player can bump into.
                if (!(rend != null && AnyMaterialNamed(rend.sharedMaterials, "Emissive")))
                    solidsOut.Add(mf);

                var shared = mf.sharedMesh;
                if (shared == null) continue;
                var verts = shared.vertices;
                var tris = shared.triangles;
                var t = mf.transform;
                var inDoor = new bool[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    // src/shed.js:105-110: toShed = inverse(g.world) * o.world.
                    // g (shedRoot) is still at local identity here, so this is
                    // just "this mesh's local vertex, in the shed-root's local
                    // space" — no GeoFrame conversion involved: the imported
                    // FBX is already in Unity's own correct convention (see
                    // this file's header, point 1).
                    Vector3 world = t.TransformPoint(verts[i]);
                    Vector3 local = shedRoot.InverseTransformPoint(world);
                    inDoor[i] = Mathf.Abs(local.x) <= door.x && local.y <= door.y && Mathf.Abs(local.z) >= door.z;
                }
                var keep = new List<int>(tris.Length);
                for (int k = 0; k < tris.Length; k += 3)
                {
                    // src/shed.js:117 — only whole triangles fully inside the
                    // panel go; the wall around the opening shares vertices
                    // with it and must stay.
                    if (inDoor[tris[k]] && inDoor[tris[k + 1]] && inDoor[tris[k + 2]]) continue;
                    keep.Add(tris[k]); keep.Add(tris[k + 1]); keep.Add(tris[k + 2]);
                }
                trisBefore += tris.Length / 3;
                trisAfter += keep.Count / 3;
                if (keep.Count != tris.Length)
                {
                    // A PER-RENDERER CLONE — Unity's own equivalent of
                    // src/shed.js:100-103's manual `geo.clone()` guard. Needed
                    // because the warehouse and the plane hall both instantiate
                    // the SAME warehouse.fbx prefab asset: without this, cutting
                    // one shed's door would corrupt (or double-cut) the other's.
                    //
                    // CLONED EXPLICITLY, not through `mf.mesh`. The getter does
                    // the same thing, but outside Play mode Unity answers it
                    // with an ERROR ("Instantiating a mesh due to calling
                    // MeshFilter.mesh during edit mode. This will leak meshes"),
                    // and this builder runs from ShedSceneFill.OnEnable on every
                    // edit-mode scene load. Same clone, same lifetime, no error.
                    var cut = UnityEngine.Object.Instantiate(shared);
                    cut.name = shared.name + "_cut";
                    mf.sharedMesh = cut;
                    cut.triangles = keep.ToArray();
                    cut.RecalculateBounds();
                }
            }
        }

        static bool AnyMaterialNamed(Material[] mats, string substr)
        {
            if (mats == null) return false;
            foreach (var m in mats)
                if (m != null && m.name.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---- floor-tile UV rescale: src/shed.js:127-157 --------------------
        static readonly string[] TileableTextureProps =
            { "_BaseMap", "_MainTex", "_BumpMap", "_NormalMap", "_MetallicGlossMap", "_OcclusionMap" };

        static void ApplyFloorTile(GameObject inner, Vector3 size, float floorTile)
        {
            float rx = size.x / floorTile, rz = size.z / floorTile; // src/shed.js:140-141, == ShedLayout.FloorTileUvScale
            foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
            {
                var rend = mf.GetComponent<Renderer>();
                var srcMat = rend != null ? rend.sharedMaterial : null;
                // src/shed.js:136-137 — only the model's own "WetConcrete" floor
                // quad gets rescaled. URP/FBX material names vary (suffixes,
                // "(Instance)", etc.), so this is a Contains rather than the
                // JS's exact regex test, deliberately looser.
                if (srcMat == null || !Regex.IsMatch(srcMat.name, "concrete", RegexOptions.IgnoreCase)) continue;

                if (mf.sharedMesh == null) continue;
                var mesh = UnityEngine.Object.Instantiate(mf.sharedMesh); // per-instance clone, see CutDoors
                mesh.name = mf.sharedMesh.name + "_tiled";
                mf.sharedMesh = mesh;
                var uv = mesh.uv;
                for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uv[i].x * rx, uv[i].y * rz); // :142
                mesh.uv = uv;

                // per-instance clone — "the other shed keeps the stretched one"
                // (:145). Explicit, for the same edit-mode-leak reason CutDoors
                // spells out: `rend.material`'s GETTER logs an error outside
                // Play mode, and this runs on every edit-mode scene load.
                var mat = new Material(srcMat) { name = srcMat.name + "_tiled" };
                rend.sharedMaterial = mat;
                foreach (var prop in TileableTextureProps) // :146-153
                {
                    if (!mat.HasProperty(prop)) continue;
                    if (mat.GetTexture(prop) is Texture2D src)
                    {
                        var clone = UnityEngine.Object.Instantiate(src); // :148 c[k] = c[k].clone()
                        clone.wrapMode = TextureWrapMode.Repeat;          // :149-150
                        mat.SetTexture(prop, clone);
                    }
                }
            }
        }

        // ---- the pad: src/shed.js:159-207 -----------------------------------
        static GameObject BuildPadGameObject(Transform root, float halfW, float padDepth, float halfL,
            Material material, bool padShadow)
        {
            var go = new GameObject("pad");
            go.transform.SetParent(root, false);
            // src/shed.js:197-199 — a box, top face pinned to the group's own
            // origin (y=0 == padTop once the root is positioned).
            var mesh = BuildBoxMesh(new Vector3(0f, -padDepth / 2f, 0f), new Vector3(halfW, padDepth / 2f, halfL), "shed_pad");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.receiveShadows = padShadow; // :206 — off for the hall, on for the warehouse
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            // BoxCollider, not MeshCollider: the pad is a plain flat slab, and
            // Unity's native box primitive fits it exactly for free.
            var bc = go.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, -padDepth / 2f, 0f);
            bc.size = new Vector3(halfW * 2f, padDepth, halfL * 2f);
            return go;
        }

        // ---- a ramp: src/shed.js:209-232 ------------------------------------
        static GameObject BuildRampGameObject(Transform root, ShedLayout.RampResult r, Material material)
        {
            var go = new GameObject(r.ZNear + r.ZFar >= 0f ? "ramp_pos" : "ramp_neg");
            go.transform.SetParent(root, false);

            var V = new List<Vector3>(); var T = new List<int>(); var UV = new List<Vector2>();
            // src/shed.js:224-225: BoxGeometry(RAMP_HALF*2, depth, rampLen) then
            // .translate(0,-depth/2,(zNear+zFar)/2) — built directly centred
            // there instead (one fewer step, same result: a box spanning
            // [zNear,zFar] regardless of which is larger).
            AddBox(V, T, UV, new Vector3(0f, -r.Depth / 2f, (r.ZNear + r.ZFar) / 2f),
                new Vector3(r.RampHalf, r.Depth / 2f, Mathf.Abs(r.ZFar - r.ZNear) / 2f));
            for (int i = 0; i < V.Count; i++)
            {
                var p = V[i];
                p.y += r.K * p.z;     // src/shed.js:226 — the shear matrix (y' = y + k*z)
                p.y -= r.K * r.ZNear; // src/shed.js:227 — pin the top edge to the slab
                V[i] = p;
            }
            var mesh = BuildMirroredMesh(V, T, UV, "shed_ramp");

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.receiveShadows = true; // :229
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            // MeshCollider, not a BoxCollider approximation: the ramp is a
            // sheared (trapezoidal) box, and a straight BoxCollider would
            // either float above the slope or poke through it at one end.
            // It stays a simple convex shape, so mark it convex (cheaper).
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
            return go;
        }

        // ---- shared box/mirror helpers --------------------------------------
        static void AddBox(List<Vector3> V, List<int> T, List<Vector2> UV, Vector3 c, Vector3 h)
        {
            float x0 = c.x - h.x, x1 = c.x + h.x, y0 = c.y - h.y, y1 = c.y + h.y, z0 = c.z - h.z, z1 = c.z + h.z;
            void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(cc); V.Add(d);
                UV.Add(new Vector2(0, 0)); UV.Add(new Vector2(1, 0)); UV.Add(new Vector2(1, 1)); UV.Add(new Vector2(0, 1));
                T.Add(i); T.Add(i + 1); T.Add(i + 2);
                T.Add(i); T.Add(i + 2); T.Add(i + 3);
            }
            // Standard CCW-outward winding for a RIGHT-HANDED frame (matching
            // three.js BoxGeometry's own convention) — authored unmirrored, on
            // purpose; BuildMirroredMesh corrects it for Unity exactly once.
            Quad(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1)); // +Y
            Quad(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0)); // -Y
            Quad(new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0)); // +X
            Quad(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1)); // -X
            Quad(new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1)); // +Z
            Quad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0)); // -Z
        }

        static Mesh BuildBoxMesh(Vector3 center, Vector3 half, string name)
        {
            var V = new List<Vector3>(); var T = new List<int>(); var UV = new List<Vector2>();
            AddBox(V, T, UV, center, half);
            return BuildMirroredMesh(V, T, UV, name);
        }

        /// <summary>Mirrors X and flips winding ONCE for the whole mesh — every
        /// box/ramp above is authored unmirrored, in the JS's own local
        /// numbers, exactly the convention LandmarkPlacer.cs's MakeMesh uses
        /// (negate vertex.x, then GeoFrame.FlipWinding). Normals are recomputed
        /// from the final, already-mirrored geometry rather than mirrored by
        /// hand, which also makes the sheared ramp's normals correct for
        /// free.</summary>
        static Mesh BuildMirroredMesh(List<Vector3> verts, List<int> tris, List<Vector2> uvs, string name)
        {
            var mv = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++) mv[i] = new Vector3(-verts[i].x, verts[i].y, verts[i].z);
            var mt = tris.ToArray();
            GeoFrame.FlipWinding(mt);
            var mesh = new Mesh { name = name };
            mesh.indexFormat = mv.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(mv);
            mesh.SetUVs(0, uvs);
            mesh.triangles = mt;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material DefaultMaterial(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "ShedFillDefault" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            return m;
        }

        static Color HexColor(int hex) =>
            new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);
    }
}
