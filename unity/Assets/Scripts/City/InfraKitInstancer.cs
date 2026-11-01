using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDERED HALF of InfraBuilder.cs — the consumer its own class doc
    /// asks for ("A later consumer finds the child Transform named `Kind`
    /// inside the imported kit prefab ... and instantiates/batches it — this
    /// file only computes WHERE"). Port of src/infra.js:113-123's `place()`
    /// helper plus its nineteen call sites' per-kind draw distances.
    ///
    /// NO GAMEOBJECT PER PIECE (hard project rule, and the JS instances all of
    /// this): every placement becomes a Matrix4x4 in a
    /// <see cref="Batch"/>, bucketed by ChunkedInstancer.Place at the kind's
    /// OWN chunk size and submitted with Graphics.RenderMeshInstanced. The
    /// kerb layer alone is tens of thousands of pieces.
    ///
    /// THE TWO KITS, AND WHY THE ORDER MATTERS. src/infra.js:98-111 loads
    /// infra_kit.glb, then loads embankment_kit.glb and does
    /// <c>Object.assign(kit, emb)</c> — so where both kits define a name
    /// ("quay", "quay_step") the EMBANKMENT one wins, and only the embankment
    /// kit gets <c>textureKit(emb, embankmentMaterials())</c>. Both halves are
    /// reproduced in <see cref="Resolve"/>: infra_kit is indexed first,
    /// embankment_kit overwrites, and a piece that came from the embankment
    /// kit has its "emb_wall"/"emb_stone" material SLOTS swapped through
    /// City/InfraMaterials.For (the real ambientCG ashlar and granite), while
    /// an infra_kit piece keeps the kit's own material, exactly the JS's
    /// <c>mats[m?.name] ?? m</c> fallback.
    ///
    /// HANDEDNESS: kit meshes arrive PRE-MIRRORED from the FBX importer
    /// (RoadBuilder.cs's class note, proved by AxisProbe), so only the
    /// placement transform is converted — and InfraBuilder.Make has already
    /// done that with GeoFrame.ToUnity/YawToUnity. Nothing here touches X or
    /// winding again; doing so would mirror the kit twice.
    ///
    /// NOT A MonoBehaviour: a static helper shared by
    /// <see cref="RoadNetworkRenderer"/> and <see cref="RiverRenderer"/>, so
    /// the one-MonoBehaviour-per-file rule does not apply. <see cref="Batch"/>
    /// and <see cref="Submit"/> live here rather than being copy-pasted into
    /// each renderer the way CityRenderer/ParkRenderer/LandmarkRenderer/
    /// StreetFurnitureRenderer each copied their own.
    /// </summary>
    public static class InfraKitInstancer
    {
        public const int MaxInstancesPerCall = 1023; // Graphics.RenderMeshInstanced's own ceiling

        public const string InfraKitPath = "Assets/Resources/Art/Props/infra_kit.fbx";
        public const string EmbankmentKitPath = "Assets/Resources/Art/Props/embankment_kit.fbx";

        /// <summary>One instanced submission unit. Same fields CityRenderer.
        /// Batch/LandmarkRenderer.Batch/StreetFurnitureRenderer.Batch each
        /// declare privately; declared ONCE here and reused by the three new
        /// ground renderers.</summary>
        public struct Batch
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
            public float MaxDist;   // src/infra.js place()'s 3rd argument
            public float Cy;
            public bool CastShadow; // src/infra.js place()'s 5th argument
        }

        /// <summary>
        /// src/infra.js's nineteen <c>place(name, items, maxDist, chunkM,
        /// shadow)</c> call sites, one row per kit piece. The defaults in the
        /// JS signature (infra.js:113) are chunkM = 1000, shadow = false.
        /// A Kind with no row here is drawn with those defaults and reported,
        /// never silently dropped.
        /// </summary>
        public struct KindSpec { public float MaxDist, ChunkM; public bool Shadow; }

        static readonly Dictionary<string, KindSpec> Specs = new Dictionary<string, KindSpec>
        {
            // ---- river embankment, infra.js:134-140 / :227-229 ----
            { "cliff",           new KindSpec { MaxDist = 2600f, ChunkM = 1000f, Shadow = false } }, // :134
            { "quay",            new KindSpec { MaxDist = 2600f, ChunkM = 1000f, Shadow = false } }, // :138
            { "quay_step",       new KindSpec { MaxDist = 2600f, ChunkM = 1000f, Shadow = false } }, // :139
            { "riprap",          new KindSpec { MaxDist = 1600f, ChunkM = 1000f, Shadow = false } }, // :140
            // the parapet run: infra.js:227 `place(name, list, 800, 600, true)`
            // for every balustrade name bridgeRailNear() can return, plus the
            // 'bal_stone' default.
            { "bal_stone",       new KindSpec { MaxDist = 800f,  ChunkM = 600f,  Shadow = true  } }, // :227
            { "bal_iron",        new KindSpec { MaxDist = 800f,  ChunkM = 600f,  Shadow = true  } }, // :227
            { "bal_steel",       new KindSpec { MaxDist = 800f,  ChunkM = 600f,  Shadow = true  } }, // :227
            { "bal_spindle",     new KindSpec { MaxDist = 800f,  ChunkM = 600f,  Shadow = true  } }, // :227
            { "bal_pier",        new KindSpec { MaxDist = 800f,  ChunkM = 600f,  Shadow = true  } }, // :228
            { "quay_lamp",       new KindSpec { MaxDist = 1100f, ChunkM = 800f,  Shadow = true  } }, // :229
            // ---- bridge furniture, infra.js:239-240 ----
            { "deck_fascia",     new KindSpec { MaxDist = 1600f, ChunkM = 1000f, Shadow = false } }, // :239
            { "lamp_candelabra", new KindSpec { MaxDist = 1800f, ChunkM = 1000f, Shadow = true  } }, // :240
            // ---- tunnel headwalls, infra.js:249/:276 ----
            { "tunnel_portal",   new KindSpec { MaxDist = 1800f, ChunkM = 1000f, Shadow = true  } }, // :249
            { "underpass_wall",  new KindSpec { MaxDist = 900f,  ChunkM = 1000f, Shadow = true  } }, // :276
            // ---- street furniture, infra.js:413-421 ----
            { "kerb",            new KindSpec { MaxDist = 420f,  ChunkM = 400f,  Shadow = false } }, // :413
            { "kerb_ramp",       new KindSpec { MaxDist = 700f,  ChunkM = 500f,  Shadow = false } }, // :414
            { "crossing",        new KindSpec { MaxDist = 700f,  ChunkM = 1000f, Shadow = false } }, // :415
            { "trafficlight",    new KindSpec { MaxDist = 900f,  ChunkM = 1000f, Shadow = true  } }, // :416
            { "busstop",         new KindSpec { MaxDist = 800f,  ChunkM = 1000f, Shadow = true  } }, // :417
            { "signpost",        new KindSpec { MaxDist = 600f,  ChunkM = 1000f, Shadow = false } }, // :418
            { "guardrail",       new KindSpec { MaxDist = 550f,  ChunkM = 800f,  Shadow = false } }, // :419
            { "manhole",         new KindSpec { MaxDist = 300f,  ChunkM = 1000f, Shadow = false } }, // :420
            { "pylon_lamp",      new KindSpec { MaxDist = 1400f, ChunkM = 1500f, Shadow = true  } }, // :421
        };

        public static KindSpec SpecFor(string kind) =>
            Specs.TryGetValue(kind, out var s) ? s
                : new KindSpec { MaxDist = 0f, ChunkM = 1000f, Shadow = false }; // infra.js:113 defaults

        /// <summary>How many kit names this file knows a draw distance for —
        /// a probe can pair it with the Kind set InfraBuilder emits.</summary>
        public static int SpecCount => Specs.Count;

        // =====================================================================
        // Kit resolution
        // =====================================================================

        /// <summary>One kit piece: its submeshes, each with the material that
        /// submesh renders with, and the piece's own transform inside the kit
        /// FBX (merged into the instance matrix, the way the JS bakes
        /// `src.matrixWorld` into `k.geo`).</summary>
        public struct Piece
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public Matrix4x4 Local;
        }

        static Dictionary<string, List<Piece>> _kit;

        /// <summary>Kit names that resolved, for a count line.</summary>
        public static int ResolvedKindCount => _kit?.Count ?? 0;

        /// <summary>Kit names a placement asked for and the FBXs do not
        /// contain. Reported, never faked — StreetFurnitureRenderer's own
        /// "asset genuinely missing — report, don't fake it" rule.</summary>
        public static readonly List<string> MissingKinds = new List<string>();

        /// <summary>Drops the cached kit index (an FBX re-import between two
        /// builds in one editor session would otherwise hand out dead
        /// references).</summary>
        public static void ResetKit() { _kit = null; MissingKinds.Clear(); }

        static Dictionary<string, List<Piece>> Kit()
        {
            if (_kit != null) return _kit;
            _kit = new Dictionary<string, List<Piece>>();
            // infra.js:98 loadKit() first ...
            Index(InfraKitPath, embankment: false);
            // ... then infra.js:106-108 `Object.assign(kit, emb)` — the
            // embankment kit OVERWRITES any name both kits define.
            Index(EmbankmentKitPath, embankment: true);
            return _kit;
        }

        static void Index(string path, bool embankment)
        {
            var root = ArtAssets.Load<GameObject>(path);
            if (root == null) { Debug.LogWarning($"INFRAKIT missing fbx={path}"); return; }
            var rootInv = root.transform.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                var slots = mr.sharedMaterials;
                var pieces = new List<Piece>();
                Matrix4x4 local = rootInv * mf.transform.localToWorldMatrix;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                {
                    var mat = s < slots.Length ? slots[s]
                            : (slots.Length > 0 ? slots[slots.Length - 1] : null);
                    // infra.js:87-93 textureKit — swap by MATERIAL name, and
                    // only on the embankment kit (infra.js:107).
                    if (embankment && mat != null)
                    {
                        var swap = InfraMaterials.For(mat.name);
                        if (swap != null) mat = swap;
                    }
                    if (mat == null) continue;
                    mat.enableInstancing = true;
                    pieces.Add(new Piece { Mesh = mf.sharedMesh, Submesh = s, Material = mat, Local = local });
                }
                if (pieces.Count == 0) continue;
                // The glTF/FBX name convention the kit build scripts use is
                // one joined object per kit piece, so the MeshFilter's own
                // GameObject name IS the Kind InfraBuilder emits. Indexed by
                // both that and the mesh datablock name (tools/
                // build_embankment_kit.py:243 sets `ob.data.name = name` too,
                // and the FBX importer has been seen to rename one or the
                // other).
                _kit[mf.gameObject.name] = pieces;
                if (!_kit.ContainsKey(mf.sharedMesh.name)) _kit[mf.sharedMesh.name] = pieces;
            }
        }

        /// <summary>The pieces for one kit name, or null (recorded in
        /// <see cref="MissingKinds"/>).</summary>
        public static List<Piece> Resolve(string kind)
        {
            var kit = Kit();
            if (kit.TryGetValue(kind, out var p)) return p;
            if (!MissingKinds.Contains(kind)) MissingKinds.Add(kind);
            return null;
        }

        // =====================================================================
        // Placement -> batches
        // =====================================================================

        /// <summary>
        /// src/infra.js:113-123 `place()`, for a whole mixed placement list:
        /// groups by Kind, resolves each kind's kit piece, buckets at that
        /// kind's own chunkM and emits one <see cref="Batch"/> per
        /// (chunk x submesh). <paramref name="placed"/> counts the placements
        /// that found a kit piece.
        /// </summary>
        public static List<Batch> Build(List<FurniturePlacement> items, out int placed, out int dropped)
        {
            var batches = new List<Batch>();
            placed = 0;
            dropped = 0;
            if (items == null || items.Count == 0) return batches;

            var byKind = new Dictionary<string, List<FurniturePlacement>>();
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Kind)) continue;
                if (!byKind.TryGetValue(it.Kind, out var list)) byKind[it.Kind] = list = new List<FurniturePlacement>();
                list.Add(it);
            }

            foreach (var kv in byKind)
            {
                var pieces = Resolve(kv.Key);
                if (pieces == null) { dropped += kv.Value.Count; continue; }
                var spec = SpecFor(kv.Key);
                var chunks = ChunkedInstancer.Place(kv.Value,
                    it => it.Position,
                    it => Matrix4x4.TRS(it.Position,
                                        Quaternion.AngleAxis(it.YawUnity * Mathf.Rad2Deg, Vector3.up),
                                        it.Scale == Vector3.zero ? Vector3.one : it.Scale),
                    spec.ChunkM, spec.Shadow, spec.MaxDist, 0f);
                foreach (var c in chunks)
                {
                    foreach (var piece in pieces)
                    {
                        var mats = new Matrix4x4[c.Matrices.Length];
                        for (int i = 0; i < mats.Length; i++) mats[i] = c.Matrices[i] * piece.Local;
                        batches.Add(new Batch
                        {
                            Mesh = piece.Mesh,
                            Submesh = piece.Submesh,
                            Material = piece.Material,
                            Matrices = mats,
                            Bounds = c.Bounds,
                            MaxDist = spec.MaxDist,
                            Cy = c.Cy,
                            CastShadow = spec.Shadow,
                        });
                    }
                }
                placed += kv.Value.Count;
            }
            return batches;
        }

        // =====================================================================
        // Submission — one copy, shared by the three ground renderers
        // =====================================================================

        /// <summary>Builds the CityCulling partition for a batch list, one
        /// entry per batch in the same index order (CityRenderer.Configure's
        /// own wiring) and enables GPU instancing on every material — without
        /// which Graphics.RenderMeshInstanced silently draws nothing.</summary>
        public static CityCulling Partition(List<Batch> batches, out int instances, out int drawCalls)
        {
            var culling = new CityCulling();
            instances = 0;
            drawCalls = 0;
            if (batches == null) return culling;
            foreach (var b in batches)
            {
                if (b.Material != null) b.Material.enableInstancing = true;
                int n = b.Matrices?.Length ?? 0;
                instances += n;
                drawCalls += Mathf.Max(1, Mathf.CeilToInt(n / (float)MaxInstancesPerCall));
                culling.Add(new ChunkedInstancer.Chunk
                {
                    Cx = b.Bounds.center.x,
                    Cz = b.Bounds.center.z,
                    Cy = b.Cy,
                    MaxDist = b.MaxDist,
                    LodD2 = 0f,
                    ShadowD2 = b.CastShadow ? ChunkedInstancer.ShadowDist * ChunkedInstancer.ShadowDist : 0f,
                    CastShadow = b.CastShadow,
                });
            }
            return culling;
        }

        /// <summary>One frame's submission. Returns the draw calls issued, so
        /// a caller can print a per-frame figure a render probe can read (the
        /// skill's "a probe camera only sees what ticked" rule).</summary>
        public static int Submit(List<Batch> batches, CityCulling culling, Vector3 camPos, float dt, int layer)
        {
            if (batches == null || batches.Count == 0) return 0;
            culling?.Tick(camPos, dt);
            int calls = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                var b = batches[i];
                if (b.Mesh == null || b.Material == null || b.Matrices == null || b.Matrices.Length == 0) continue;
                bool hi = true;
                if (culling != null && i < culling.Chunks.Count) hi = culling.Chunks[i].HiOn;
                if (!hi) continue;
                var rp = new RenderParams(b.Material)
                {
                    shadowCastingMode = b.CastShadow ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                    worldBounds = b.Bounds,
                    layer = layer,
                };
                for (int offset = 0; offset < b.Matrices.Length; offset += MaxInstancesPerCall)
                {
                    int n = Mathf.Min(MaxInstancesPerCall, b.Matrices.Length - offset);
                    Graphics.RenderMeshInstanced(rp, b.Mesh, b.Submesh, b.Matrices, n, offset);
                    calls++;
                }
            }
            return calls;
        }
    }
}
