using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/ShopfrontRenderer.cs — the render layer that closes
    /// City/CityLoader.cs's own "shopfronts" GAP comment (ctx.Shopfronts
    /// computed, nothing reads it). Three kinds of evidence, in order:
    ///
    ///  1. REAL DATA facts (POI/module/awning/cross/sign counts from the
    ///     actual city) and the facade_kit.fbx "win_shop" gap this task found.
    ///  2. BOTH wirings actually running: the static Render(ctx) entry point
    ///     AND a from-scratch AddComponent&lt;ShopfrontRenderer&gt;() ->
    ///     OnEnable() -> EnsureBuilt() path (the gap the coordinator flagged:
    ///     a static call alone is not proof the component path works).
    ///  3. REAL DRAW SUBMISSION: TickFrame (the exact method LateUpdate calls)
    ///     invoked directly, reporting live Graphics.RenderMeshInstanced
    ///     attempt/success/instance counts, SignPool.Update having actually
    ///     run and repainted a slot, and a read-back, non-empty painted
    ///     texture (DamageTextures.Analyze on a real Texture2D, not a
    ///     separately-computed guess).
    ///
    /// Numeric facts paired against tools/_probe_shopfrontrender.mjs (same
    /// KEY/shape, diffed by tools/unity_port_check.py): the chunk-bucketing
    /// formula (citymap.js:7956-7960, shared by every instanced layer in the
    /// city, exercised here for shopfront modules/crosses specifically) over
    /// a fixed synthetic item set, and the stripe/cross canvas-paint pixel
    /// values (src/shopfronts.js:261-272 `stripes()`, 289-316 `boxKind`/
    /// `crossMats`) at two sample points each. Everything else here (kit
    /// resolution, component wiring, draw-call submission) has no Three.js
    /// counterpart to diff against — same documented exemption
    /// CityRenderProbe.cs already uses for CityRenderer.cs — so those print
    /// as explicit PASS/FAIL assertions instead.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ShopfrontRenderProbe.Run
    /// </summary>
    public static class ShopfrontRenderProbe
    {
        public static void Run()
        {
            var city = CityData.Load();
            var sf = ShopfrontBuilder.Build(null, city);

            // ---- 1. real data facts ----
            Debug.Log($"SFR id=data pois={sf.PoiCount} names={sf.NameCount} wallSnapped={sf.WallSnappedCount} " +
                      $"modules={sf.Modules.Count} awnings={sf.Awnings.Count} canopies={sf.Canopies.Count} " +
                      $"crosses={sf.Crosses.Count} signs={sf.Signs.Count} mcdonaldsPads={sf.McdonaldsPads.Count}");

            var (kitMesh, _) = ResolveKitPartForProbe("win_shop", "Assets/Resources/Art/Props/facade_kit.fbx");
            var (acMesh, _) = ResolveKitPartForProbe("ac", "Assets/Resources/Art/Props/facade_kit.fbx");
            Debug.Log($"SFR id=kit winShopPresent={B(kitMesh != null)} acPresent={B(acMesh != null)}");

            // ---- 2a. static Render(ctx) path, reusing the already-computed sf ----
            var ctx = new CityLoader.Context { Shopfronts = sf };
            var comp = ShopfrontRenderer.Render(ctx);
            bool staticOk = comp != null && comp.Built;
            Debug.Log($"SFR id=static_render built={B(staticOk)} moduleBatches={comp?.ModuleBatchCount ?? 0} " +
                      $"moduleInstances={comp?.ModuleInstanceTotal ?? 0} crossBatches={comp?.CrossBatchCount ?? 0} " +
                      $"crossInstances={comp?.CrossInstanceTotal ?? 0} awningBuilt={B(comp?.AwningBuilt ?? false)} " +
                      $"canopyBuilt={B(comp?.CanopyBuilt ?? false)} signSlots={comp?.SignSlotCount ?? 0} " +
                      $"usedFallbackBox={B(comp?.ModuleUsedFallbackBox ?? false)}");

            // ---- 2b. the COMPONENT path from scratch: a fresh GameObject,
            // AddComponent (which synchronously fires OnEnable -> EnsureBuilt,
            // reloading CityData + ShopfrontBuilder.Build ON ITS OWN, no ctx
            // supplied) — the exact path a real scene drives when BuildCityRender
            // .cs/BuildPlayableScene.cs add this component the way they add
            // CityKitRenderer. This is independent proof the component works
            // without anyone handing it a pre-built result.
            var freshGo = new GameObject("ShopfrontRenderer_ComponentProbe");
            var comp2 = freshGo.AddComponent<ShopfrontRenderer>();
            bool componentOk = comp2.Built;
            Debug.Log($"SFR id=component_path built={B(componentOk)} moduleBatches={comp2.ModuleBatchCount} " +
                      $"moduleInstances={comp2.ModuleInstanceTotal} crossBatches={comp2.CrossBatchCount} " +
                      $"crossInstances={comp2.CrossInstanceTotal} awningBuilt={B(comp2.AwningBuilt)} " +
                      $"canopyBuilt={B(comp2.CanopyBuilt)} signSlots={comp2.SignSlotCount}");

            // ---- 3. real per-frame submission, via comp (reused sf) ----
            Vector3 camPos = sf.Signs.Count > 0 ? sf.Signs[0].Position : Vector3.zero;
            comp.TickFrame(camPos, 0.016f);
            Debug.Log($"SFR id=frame attempted={comp.LastDrawCallsAttempted} succeeded={comp.LastDrawCallsSucceeded} " +
                      $"instancesSubmitted={comp.LastInstancesSubmitted} visibleModuleBatches={comp.LastVisibleModuleBatches} " +
                      $"visibleCrossBatches={comp.LastVisibleCrossBatches} signPoolUpdated={B(comp.LastSignPoolUpdated)} " +
                      $"exception={(string.IsNullOrEmpty(comp.LastDrawException) ? "none" : comp.LastDrawException)}");

            int activeAfter = comp.SignPoolActiveCount;
            bool paintedOne = comp.LastPaintedSlot >= 0 && comp.LastPaintedTexture != null;
            Debug.Log($"SFR id=signpool activeAfterTick={activeAfter} lastPaintedSlot={comp.LastPaintedSlot} painted={B(paintedOne)}");

            if (paintedOne)
            {
                var f = TbilisiFly.Combat.DamageTextures.Analyze(comp.LastPaintedTexture);
                Debug.Log($"SFR id=signtex w={f.W} h={f.H} opaqueFrac={f.OpaqueFrac:F4} alphaZeroFrac={f.AlphaZeroFrac:F4} " +
                          $"meanR={f.MeanR:F4} meanG={f.MeanG:F4} meanB={f.MeanB:F4}");
            }

            // ---- SIGNS textured=<n> nullTexture=<n> — docs/QUALITY_BAR.md's
            // own phrasing for this row's failure mode ("a sign with a null
            // texture is a grey quad"). One TickFrame only reassigns the slots
            // that moved since the pool's initial all-empty state (the pool
            // starts with every slot hidden, so the FIRST tick anywhere
            // assigns up to 192 at once) — tick once more from the same spot
            // so every slot the pool wants on is actually painted, then walk
            // every slot GameObject the real renderer built (sign_slots/
            // sign_slot_i, ShopfrontRenderer.BuildSignPool) and read its own
            // Material.mainTexture back, the same real-object path
            // CitySignsProbe.ShopSignShot already uses to find a slot. ----
            comp.TickFrame(camPos, 0.6f);
            int signTextured = 0, signNullTexture = 0, signEnabled = 0;
            var slotsRoot = comp.transform.Find("sign_slots");
            if (slotsRoot != null)
            {
                for (int i = 0; i < slotsRoot.childCount; i++)
                {
                    var mr = slotsRoot.GetChild(i).GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    signEnabled++;
                    if (mr.sharedMaterial != null && mr.sharedMaterial.mainTexture != null) signTextured++;
                    else signNullTexture++;
                }
            }
            Debug.Log($"SIGNS textured={signTextured} nullTexture={signNullTexture} enabledSlots={signEnabled} poolSize={comp.SignSlotCount}");

            // ---- one named shop, its sign text, and the wall it snapped to
            // (building id = city.Buildings' own array index — the only id
            // either side of this pipeline carries, see PoiRec.WallBi's own
            // doc) + distance in metres — the per-shop evidence the task asks
            // for, read off the REAL ShopfrontBuilder result, not recomputed. ----
            for (int i = 0; i < sf.Signs.Count; i++)
            {
                var s = sf.Signs[i];
                if (s.BuildingId < 0 || string.IsNullOrEmpty(s.Name) || s.Cat == "named") continue;
                Debug.Log($"SFR id=named_shop signIdx={i} cat={s.Cat} name=\"{s.Name}\" " +
                          $"buildingId={s.BuildingId} wallDistanceM={s.WallDistanceM:F3} " +
                          $"posUnity={s.Position.x:F2},{s.Position.y:F2},{s.Position.z:F2} yawUnity={s.YawUnity:F5}");
                break;
            }

            // ---- materials actually carry enableInstancing (hard requirement
            // for Graphics.RenderMeshInstanced to draw anything at all) ----
            bool modInst = true, crossInst = true;
            foreach (var b in comp.ModuleBatchesForProbe) if (b.Material != null && !b.Material.enableInstancing) modInst = false;
            foreach (var b in comp.CrossBatchesForProbe) if (b.Material != null && !b.Material.enableInstancing) crossInst = false;
            Debug.Log($"SFR id=materials moduleEnableInstancing={B(modInst)} crossEnableInstancing={B(crossInst)} " +
                      $"moduleBatchesNonEmpty={B(comp.ModuleBatchCount > 0)} crossBatchesNonEmpty={B(comp.CrossBatchCount > 0)}");

            // =================================================================
            // PAIRED NUMERIC FACTS vs tools/_probe_shopfrontrender.mjs
            // =================================================================

            // ---- chunk-bucketing formula (citymap.js:7956-7960), 5 synthetic
            // points fed through the REAL ChunkedInstancer.Place via this file's
            // own BuildModuleBatches (chunkM=500)/BuildCrossBatches (chunkM=800,
            // kind 0 only so every item lands in the SAME kind-bucket pass). ----
            var pts = new (float x, float z)[] { (0, 0), (100, 50), (600, 0), (0, 600), (900, 900) };

            sf.Modules.Clear();
            foreach (var p in pts) sf.Modules.Add(new ShopfrontBuilder.ModuleItem { Position = new Vector3(p.x, 0f, p.z), YawUnity = 0f, NormalUnity = Vector3.forward });
            sf.Crosses.Clear();
            foreach (var p in pts) sf.Crosses.Add(new ShopfrontBuilder.CrossItem { Position = new Vector3(p.x, 0f, p.z), YawUnity = 0f, Kind = 0 });
            // keep awnings/canopies/signs as-is (sf.AwningMesh/CanopyMesh are already baked Meshes,
            // independent of Modules/Crosses, and signs need the real SignSpot list to stay sane).
            comp.BuildFrom(sf);

            ReportBucketing("500", comp.ModuleBatchesForProbe, comp.ModuleInstanceTotal);
            ReportBucketing("800", comp.CrossBatchesForProbe, comp.CrossInstanceTotal);

            // ---- stripe texture pixels (src/shopfronts.js:261-272 stripes()) ----
            var awningMat = comp.AwningMaterialForProbe;
            if (awningMat != null && awningMat.mainTexture is Texture2D stripeTex)
            {
                Color c2 = stripeTex.GetPixel(2, 0); // inside a stripe band (x%8<4) -> c2 (#efe8dc)
                Color c1 = stripeTex.GetPixel(6, 0); // outside a band -> c1 (#b43a2a)
                Debug.Log($"SFR id=stripe_tex x2r={c2.r:F4} x2g={c2.g:F4} x2b={c2.b:F4} x6r={c1.r:F4} x6g={c1.g:F4} x6b={c1.b:F4}");
            }
            else
            {
                Debug.Log("SFR id=stripe_tex NOTE_awning_texture_unavailable=1");
            }

            // ---- cross plate pixels (src/shopfronts.js:289-316 boxKind/crossMats) ----
            foreach (var b in comp.CrossBatchesForProbe)
            {
                if (b.Material == null || !(b.Material.mainTexture is Texture2D ctex)) continue;
                string kindName = b.Material.name.Replace("_mat", "");
                int kind = kindName == "pharmacy_cross" ? 0 : kindName == "medical_cross" ? 1 : 2;
                Color center = ctex.GetPixel(64, 64); // inside both bars for every kind
                Color corner = ctex.GetPixel(4, 4);   // outside both bars
                Debug.Log($"SFR id=cross_tex kind={kind} centerR={center.r:F4} centerG={center.g:F4} centerB={center.b:F4} " +
                          $"cornerR={corner.r:F4} cornerG={corner.g:F4} cornerB={corner.b:F4}");
                break; // only kind 0 exists in this synthetic pass (see sf.Crosses.Clear() above) — one line is enough
            }

            // ---- explicit PASS/FAIL assertions (no JS counterpart — CityRenderProbe's own precedent) ----
            bool passStatic = staticOk && (comp.ModuleBatchCount > 0 || sf.Modules.Count == 0);
            bool passComponent = componentOk;
            bool passDraw = comp.LastDrawCallsAttempted > 0 && comp.LastDrawCallsAttempted == comp.LastDrawCallsSucceeded;
            bool passSign = comp.LastSignPoolUpdated && paintedOne;
            bool passMaterials = modInst && crossInst;
            Debug.Log($"SFR id=assert_staticRender result={PF(passStatic)}");
            Debug.Log($"SFR id=assert_componentPath result={PF(passComponent)}");
            Debug.Log($"SFR id=assert_realDrawSubmission result={PF(passDraw)}");
            Debug.Log($"SFR id=assert_signPoolRendered result={PF(passSign)}");
            Debug.Log($"SFR id=assert_materialsInstancingEnabled result={PF(passMaterials)}");
            Debug.Log($"SFR id=assert_overall result={PF(passStatic && passComponent && passDraw && passSign && passMaterials)}");

            ProbeExit.Exit(0);
        }

        static void ReportBucketing(string chunkM, IReadOnlyList<ShopfrontRenderer.Batch> batches, int totalInstances)
        {
            int min = int.MaxValue, max = 0;
            foreach (var b in batches)
            {
                int n = b.Matrices?.Length ?? 0;
                if (n < min) min = n;
                if (n > max) max = n;
            }
            if (batches.Count == 0) min = 0;
            Debug.Log($"SFR id=chunk_bucket chunkM={chunkM} buckets={batches.Count} minSize={min} maxSize={max} totalInstances={totalInstances}");
        }

        static (Mesh mesh, Material[] mats) ResolveKitPartForProbe(string wantName, string fbxPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null) return (null, null);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.gameObject.name == wantName || mf.sharedMesh.name == wantName)
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    return (mf.sharedMesh, mr != null ? mr.sharedMaterials : null);
                }
            }
            return (null, null);
        }

        static string B(bool v) => v ? "true" : "false";
        static string PF(bool v) => v ? "PASS" : "FAIL";
    }
}
