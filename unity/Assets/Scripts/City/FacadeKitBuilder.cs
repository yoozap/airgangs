using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of citymap.js:6119-6234 — the actual GEOMETRY PLACEMENT half of
    /// `_buildFacadeProps` (the GLB-part merge at the top of that function is
    /// FacadeKitLoader.cs; the balcony/window/AC/.../roof-clutter DECISION
    /// data that feeds this is FacadeRelief.cs + FacadeProps.cs, already
    /// ported and not edited here).
    ///
    /// Wires FacadeRelief.Items/FacadeProps.Items (balconies, windows,
    /// cornices, grilles, glass mullions/bands, ACs, dishes, drainpipes,
    /// entrances, parapets, roof props — the twelve kinds citymap.js:6075-6077
    /// takes as parameters) through FacadeKitLoader's merged kit parts (or a
    /// procedural fallback box when the kit is missing that name) into
    /// ChunkedInstancer.Place, mirroring `_buildFacadeProps`'s own `put`/
    /// `byVariant` helpers (citymap.js:6124-6148) exactly.
    ///
    /// PURE DATA — no GameObject, no Instantiate. Every item's Pos/AngUnity is
    /// ALREADY in Unity space (FacadeRelief/FacadeProps's own documented
    /// convention), so no GeoFrame call is needed here — only
    /// Matrix4x4.TRS composition, same boundary ChunkedInstancer.Place draws.
    /// </summary>
    public static class FacadeKitBuilder
    {
        // =====================================================================
        // RenderSpecs for the six kinds FacadeProps.cs did NOT already define
        // (that file covers ac/dish/drainpipe/entrance/parapet/roof_vent —
        // see its own class doc). Same struct, same convention, just the other
        // half of citymap.js:6124-6224's twelve `put(...)` calls.
        // =====================================================================

        public static readonly FacadeProps.RenderSpec Balcony = new FacadeProps.RenderSpec
        {
            Kit = "balcony", FallbackSize = default, FallbackOffset = default,
            FallbackColor = Hex(0xc5bfb2), MaxDist = 1400f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6150-6160 — fallback is slab+rail merged, built specially below

        public static readonly FacadeProps.RenderSpec Cornice = new FacadeProps.RenderSpec
        {
            Kit = "cornice", FallbackSize = new Vector3(3f, 0.2f, 0.28f), FallbackOffset = new Vector3(0f, 0f, 0.14f),
            FallbackColor = Hex(0xd8d2c2), MaxDist = 900f, ChunkM = 1000f, Lodable = false,
        }; // citymap.js:6173-6177

        public static readonly FacadeProps.RenderSpec Grille = new FacadeProps.RenderSpec
        {
            Kit = "grille", FallbackSize = new Vector3(1.2f, 1.4f, 0.05f), FallbackOffset = new Vector3(0f, 0f, 0.16f),
            FallbackColor = Hex(0x2a2a2a), MaxDist = 400f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6189-6193

        public static readonly FacadeProps.RenderSpec WindowDefault = new FacadeProps.RenderSpec
        {
            Kit = "window", FallbackSize = new Vector3(1.5f, 1.6f, 0.1f), FallbackOffset = Vector3.zero,
            FallbackColor = Hex(0xe8e4dc), MaxDist = 900f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6170-6172 — "win_shop" variant uses MaxDist 700 instead, handled in BuildWindows

        internal const float WinShopMaxDist = 700f;

        // Glass-tower relief: dark aluminum, shared material, no kit lookup in
        // the JS either (citymap.js:6212-6224 builds these two box geometries
        // directly, never calling geoOf/matOf) — so these two have no `Kit` name.
        internal static readonly Vector3 GlassMullionSize = new Vector3(0.35f, 3f, 0.35f);
        internal static readonly Vector3 GlassMullionOffset = new Vector3(0f, 1.5f, 0f);
        internal static readonly Vector3 GlassBandSize = new Vector3(3f, 0.45f, 0.3f);
        internal static readonly Vector3 GlassBandOffset = new Vector3(0f, 0f, 0.15f);
        internal const float GlassMaxDist = 2000f; // citymap.js:6219/6224
        internal const int GlassChunkM = 1000;      // put()'s own chunkM default — no third arg passed
        internal static readonly Color32 AluColor = Hex(0x2e3338); // citymap.js:6214

        static Color32 Hex(int rgb) => new Color32((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff), 255);

        // citymap.js:6128-6129 `lodable && tris > 60 ? { dist: 250 + chunkM * 0.72, ... } : null`
        internal const int LodTriThreshold = 60;
        internal const float LodBaseDist = 250f;
        internal const float LodChunkFrac = 0.72f;

        // =====================================================================
        // core `put`: citymap.js:6124-6135
        // =====================================================================

        public struct PutResult
        {
            public List<ChunkedInstancer.Chunk> Chunks;
            public Mesh Geo;
            public Material[] Mats;
            /// <summary>citymap.js:6128-6130: only built when Lodable AND the
            /// geometry is heavier than 60 tris. Null otherwise — the chunk's
            /// LodD2 is then 0, same "no LOD for this layer" convention
            /// ChunkedInstancer.Chunk already documents.</summary>
            public Mesh ImposterGeo;
        }

        /// <summary>citymap.js:6124-6135 `put`. `geo`/`mats` is whatever
        /// FacadeKitLoader found for this spec's Kit name, or the fallback the
        /// caller built when it was missing. `bakeCamera` is optional — pass
        /// null to skip the imposter bake (still gets the right LodD2 budget
        /// decision, just no swap texture; see ImposterBaker.cs's own
        /// unverified-without-a-live-renderer note).</summary>
        public static PutResult Put<T>(
            IReadOnlyList<T> items, Func<T, Vector3> posOf, Func<T, float> angOf,
            Func<T, float> sxOf, Func<T, float> syOf,
            Mesh geo, Material[] mats, float maxDist, float chunkM, bool lodable,
            Camera bakeCamera = null, Matrix4x4? model = null)
        {
            var result = new PutResult { Chunks = new List<ChunkedInstancer.Chunk>(), Geo = geo, Mats = mats };
            if (items.Count == 0 || geo == null) return result;

            int tris = geo.triangles.Length / 3;
            float lodDist = 0f;
            if (lodable && tris > LodTriThreshold)
            {
                // NOTE for whoever wires the bake: `geo` and `geo.bounds` here
                // are the RAW imported mesh, still in the kit FBX's 1/100 Z-up
                // frame when `model` is non-identity. ImposterBaker would need
                // the same matrix applied to both the bake camera AND the
                // rendered geometry. Production passes bakeCamera: null
                // (CityRenderer.BuildFacadeBatches's "NO IMPOSTER BAKE" note),
                // so this branch is unreached today and is left untouched
                // rather than half-converted.
                lodDist = LodBaseDist + chunkM * LodChunkFrac; // citymap.js:6129
                if (bakeCamera != null)
                {
                    Bounds b = geo.bounds;
                    var vf = ImposterBaker.ComputeViewCamera(front: true, b);
                    var tex = ImposterBaker.RenderView(bakeCamera, vf, geo, mats);
                    if (tex != null) result.ImposterGeo = ImposterBaker.FacadeImposterQuad(b);
                }
            }

            // THE KIT PART'S OWN IMPORT TRANSFORM — FacadeKitLoader.KitPart.Model.
            // Identity for the procedural fallback boxes (FacadeBoxBuilder
            // already builds those in metres, upright), the FBX node's
            // scale-100 / rotate-(-90,0,0) for a real kit part. Composed on the
            // RIGHT so it runs BEFORE the placement, i.e. in the part's own
            // local frame — the same order City/CityRenderer.cs:1057 uses for
            // the building kit, and the order citymap.js gets for free because
            // its GLB geometry is already metres and Y-up.
            Matrix4x4 m0 = model ?? Matrix4x4.identity;
            bool hasModel = m0 != Matrix4x4.identity;

            Matrix4x4 MakeMatrix(T it)
            {
                float sx = sxOf != null ? sxOf(it) : 0f;
                float sy = syOf != null ? syOf(it) : 0f;
                Vector3 scale = new Vector3(sx != 0f ? sx : 1f, sy != 0f ? sy : 1f, 1f); // citymap.js:6133 `it.sx||1, it.sy||1`
                var trs = Matrix4x4.TRS(posOf(it), Quaternion.Euler(0f, angOf(it) * Mathf.Rad2Deg, 0f), scale);
                return hasModel ? trs * m0 : trs;
            }

            result.Chunks = ChunkedInstancer.Place(items, posOf, MakeMatrix, chunkM, false, maxDist, lodDist);
            return result;
        }

        /// <summary>citymap.js:6111-6112 `geoOf`/`matOf`, plus the part's own
        /// import matrix (FacadeKitLoader.KitPart.Model) the GLB does not need
        /// and the FBX does. A fallback box is already metres and upright, so
        /// it resolves to identity.</summary>
        static (Mesh geo, Material[] mats, Matrix4x4 model) Resolve(string kitName,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit,
            Func<Mesh> fallbackBuilder, Material fallbackMat)
        {
            if (kit != null && kitName != null && kit.TryGetValue(kitName, out var part) && part.Mesh != null)
            {
                KitResolved++;
                return (part.Mesh, part.Materials, part.Model);
            }
            KitFellBack++;
            return (fallbackBuilder(), new[] { fallbackMat }, Matrix4x4.identity);
        }

        /// <summary>How many of the twelve layers' variant buckets found a real
        /// kit part and how many fell back to a procedural box, since the last
        /// <see cref="ResetResolveCounters"/>. Read by the probe: a kit scale
        /// fix proves nothing if every layer was drawing a fallback box.</summary>
        internal static int KitResolved, KitFellBack;
        internal static void ResetResolveCounters() { KitResolved = 0; KitFellBack = 0; }

        static Dictionary<string, List<T>> ByVariant<T>(IReadOnlyList<T> items, Func<T, string> variantOf, string def,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit)
        {
            var m = new Dictionary<string, List<T>>();
            foreach (var it in items)
            {
                string v = variantOf(it);
                string key = (!string.IsNullOrEmpty(v) && kit != null && kit.ContainsKey(v)) ? v : def;
                if (!m.TryGetValue(key, out var list)) m[key] = list = new List<T>();
                list.Add(it);
            }
            return m;
        }

        // =====================================================================
        // the twelve call sites: citymap.js:6157-6224
        // =====================================================================

        static Mesh BalconyFallback() // citymap.js:6150-6156
        {
            Mesh slab = FacadeBoxBuilder.Box(new Vector3(2.7f, 0.14f, 1.15f), new Vector3(0f, 0f, 0.58f), "balc_slab");
            Mesh rail = FacadeBoxBuilder.Box(new Vector3(2.7f, 0.85f, 0.07f), new Vector3(0f, 0.49f, 1.12f), "balc_rail");
            return MergeTwo(slab, rail);
        }

        static Mesh MergeTwo(Mesh a, Mesh b)
        {
            var combine = new[]
            {
                new CombineInstance { mesh = a, transform = Matrix4x4.identity },
                new CombineInstance { mesh = b, transform = Matrix4x4.identity },
            };
            var m = new Mesh { name = "merged" };
            m.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true);
            return m;
        }

        public static List<PutResult> BuildBalconies(IReadOnlyList<FacadeRelief.BalconyItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            var results = new List<PutResult>();
            foreach (var kv in ByVariant(items, it => it.Variant, "balcony", kit))
            {
                var (geo, mats, model) = Resolve(kv.Key, kit, BalconyFallback, new Material(litShader) { color = (Color)Balcony.FallbackColor });
                results.Add(Put(kv.Value, it => it.Pos, it => it.AngUnity, null, null, geo, mats, Balcony.MaxDist, Balcony.ChunkM, Balcony.Lodable, bakeCamera, model));
            }
            return results;
        }

        public static List<PutResult> BuildWindows(IReadOnlyList<FacadeRelief.WindowItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            var results = new List<PutResult>();
            foreach (var kv in ByVariant(items, it => it.Variant, "window", kit))
            {
                float maxDist = kv.Key == "win_shop" ? WinShopMaxDist : WindowDefault.MaxDist;
                Mesh Fallback() => FacadeBoxBuilder.Box(WindowDefault.FallbackSize, Vector3.zero, "window_fallback");
                var (geo, mats, model) = Resolve(kv.Key, kit, Fallback, new Material(litShader) { color = (Color)WindowDefault.FallbackColor });
                results.Add(Put(kv.Value, it => it.Pos, it => it.AngUnity, null, null, geo, mats, maxDist, WindowDefault.ChunkM, WindowDefault.Lodable, bakeCamera, model));
            }
            return results;
        }

        public static PutResult BuildCornices(IReadOnlyList<FacadeRelief.CorniceItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(Cornice.FallbackSize, Cornice.FallbackOffset, "cornice_fallback");
            var (geo, mats, model) = Resolve(Cornice.Kit, kit, Fallback, new Material(litShader) { color = (Color)Cornice.FallbackColor });
            // citymap.js:6177 Sx stretches the cornice to the wall segment length
            return Put(items, it => it.Pos, it => it.AngUnity, it => it.Sx, null, geo, mats, Cornice.MaxDist, Cornice.ChunkM, Cornice.Lodable, bakeCamera, model);
        }

        public static PutResult BuildGrilles(IReadOnlyList<FacadeRelief.GrilleItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(Grille.FallbackSize, Grille.FallbackOffset, "grille_fallback");
            var (geo, mats, model) = Resolve(Grille.Kit, kit, Fallback, new Material(litShader) { color = (Color)Grille.FallbackColor });
            return Put(items, it => it.Pos, it => it.AngUnity, null, null, geo, mats, Grille.MaxDist, Grille.ChunkM, Grille.Lodable, bakeCamera, model);
        }

        public static PutResult BuildAcs(IReadOnlyList<FacadeProps.AcItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.Ac.FallbackSize, FacadeProps.Ac.FallbackOffset, "ac_fallback");
            var (geo, mats, model) = Resolve(FacadeProps.Ac.Kit, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.Ac.FallbackColor });
            return Put(items, it => it.Pos, it => it.AngUnity, null, null, geo, mats, FacadeProps.Ac.MaxDist, FacadeProps.Ac.ChunkM, FacadeProps.Ac.Lodable, bakeCamera, model);
        }

        public static PutResult BuildDishes(IReadOnlyList<FacadeProps.DishItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.Dish.FallbackSize, FacadeProps.Dish.FallbackOffset, "dish_fallback");
            var (geo, mats, model) = Resolve(FacadeProps.Dish.Kit, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.Dish.FallbackColor });
            return Put(items, it => it.Pos, it => it.AngUnity, null, null, geo, mats, FacadeProps.Dish.MaxDist, FacadeProps.Dish.ChunkM, FacadeProps.Dish.Lodable, bakeCamera, model);
        }

        public static PutResult BuildDrainpipes(IReadOnlyList<FacadeProps.DrainpipeItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.Drainpipe.FallbackSize, FacadeProps.Drainpipe.FallbackOffset, "drain_fallback");
            var (geo, mats, model) = Resolve(FacadeProps.Drainpipe.Kit, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.Drainpipe.FallbackColor });
            return Put(items, it => it.Pos, it => it.AngUnity, null, it => it.Sy, geo, mats, FacadeProps.Drainpipe.MaxDist, FacadeProps.Drainpipe.ChunkM, FacadeProps.Drainpipe.Lodable, bakeCamera, model);
        }

        public static List<PutResult> BuildEntrances(IReadOnlyList<FacadeProps.EntranceItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            var results = new List<PutResult>();
            foreach (var kv in ByVariant(items, it => it.Variant, "entrance", kit))
            {
                Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.Entrance.FallbackSize, FacadeProps.Entrance.FallbackOffset, "entrance_fallback");
                var (geo, mats, model) = Resolve(kv.Key, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.Entrance.FallbackColor });
                results.Add(Put(kv.Value, it => it.Pos, it => it.AngUnity, null, null, geo, mats, FacadeProps.Entrance.MaxDist, FacadeProps.Entrance.ChunkM, FacadeProps.Entrance.Lodable, bakeCamera, model));
            }
            return results;
        }

        public static PutResult BuildParapets(IReadOnlyList<FacadeProps.ParapetItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.Parapet.FallbackSize, FacadeProps.Parapet.FallbackOffset, "parapet_fallback");
            var (geo, mats, model) = Resolve(FacadeProps.Parapet.Kit, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.Parapet.FallbackColor });
            return Put(items, it => it.Pos, it => it.AngUnity, it => it.Sx, null, geo, mats, FacadeProps.Parapet.MaxDist, FacadeProps.Parapet.ChunkM, FacadeProps.Parapet.Lodable, bakeCamera, model);
        }

        public static List<PutResult> BuildRoofProps(IReadOnlyList<FacadeProps.RoofPropItem> items,
            IReadOnlyDictionary<string, FacadeKitLoader.KitPart> kit, Shader litShader, Camera bakeCamera = null)
        {
            var results = new List<PutResult>();
            foreach (var kv in ByVariant(items, it => it.Variant, "roof_vent", kit))
            {
                Mesh Fallback() => FacadeBoxBuilder.Box(FacadeProps.RoofVent.FallbackSize, FacadeProps.RoofVent.FallbackOffset, "roofprop_fallback");
                var (geo, mats, model) = Resolve(kv.Key, kit, Fallback, new Material(litShader) { color = (Color)FacadeProps.RoofVent.FallbackColor });
                results.Add(Put(kv.Value, it => it.Pos, it => it.AngUnity, null, null, geo, mats, FacadeProps.RoofVent.MaxDist, FacadeProps.RoofVent.ChunkM, FacadeProps.RoofVent.Lodable, bakeCamera, model));
            }
            return results;
        }

        public static PutResult BuildGlassMullions(IReadOnlyList<FacadeRelief.GlassMullionItem> items, Shader litShader)
        {
            // citymap.js:6214-6219 — always procedural, no kit lookup, no imposter
            Mesh geo = FacadeBoxBuilder.Box(GlassMullionSize, GlassMullionOffset, "glass_mullion");
            Material mat = new Material(litShader) { color = (Color)AluColor };
            return Put(items, it => it.Pos, it => it.AngUnity, null, it => it.Sy, geo, new[] { mat }, GlassMaxDist, GlassChunkM, false);
        }

        public static PutResult BuildGlassBands(IReadOnlyList<FacadeRelief.GlassBandItem> items, Shader litShader)
        {
            // citymap.js:6220-6224
            Mesh geo = FacadeBoxBuilder.Box(GlassBandSize, GlassBandOffset, "glass_band");
            Material mat = new Material(litShader) { color = (Color)AluColor };
            return Put(items, it => it.Pos, it => it.AngUnity, it => it.Sx, it => it.Sy, geo, new[] { mat }, GlassMaxDist, GlassChunkM, false);
        }
    }
}
