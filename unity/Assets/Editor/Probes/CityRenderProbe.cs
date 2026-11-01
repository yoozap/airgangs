using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the RENDERED half of docs/QUALITY_BAR.md's building checklist —
    /// CityMaterials (texture binding) and CityRenderer (kit instancing) — the
    /// layer that sits on top of the already-verified DECIDED data
    /// (BuildingKit/WallMaterials/FacadeRelief/PhotoFacades, all probed
    /// separately by BuildingQualityProbe and their own oracles).
    ///
    /// There is no JS counterpart to diff against (this layer has no Three.js
    /// equivalent — citymap.js draws with InstancedMesh directly, Unity needs
    /// its own instancing layer), so this is a standalone check with explicit
    /// PASS/FAIL assertions rather than a cross-engine parity diff.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityRenderProbe.Run
    /// </summary>
    public static class CityRenderProbe
    {
        // The expected count is 273, NOT 609 and NOT the earlier-guessed 319. 609 photos
        // ARE assigned (PhotoFacades.Assign, exclusive draw), but a footprint that also
        // claims a kit model never reaches the photo lookup — each of the five kit-claim
        // branches in src/citymap.js `continue`s before `const pb` is declared at line
        // 4746 — AND a footprint wearing a real Blender mall model (citymap.js:4591
        // `this._mallBids?.has(bi)`) never reaches TryClaim or the photo check either.
        //
        // 319 was never actually computed from the real data — nobody had run the JS's
        // own decision chain over it. 273 comes from reimplementing that whole chain
        // (pbOf exclusive assignment, DISTRICTS/cottage, parts expansion, the mallBids
        // skip, and all five kit fits with the live unity/Assets/StreamingAssets/tbilisi/
        // kit_meta.json dimensions) in Node over the live production data — city.json.gz,
        // levels.json, parts.json, malls.json, textures/pb/manifest.json — 2026-11-01.
        // Before BuildingBuilder.cs gained its own mallBids skip (added the same day —
        // citymap.js:4591 had no C# equivalent), the Node reimplementation without that
        // skip gave 276, matching this method's own live BLDRICH log EXACTLY at the time
        // (photo=276) — proof the rest of the chain (fits, gates, catalogs, pbOf) was
        // already faithful and the mallBids gap was the one real bug. With the skip
        // applied on both sides the number is 273 (see docs/open_items.json
        // `photo-render-count-drift`).
        public static void Run()
        {
            CityMaterials.Reset();

            var city = CityData.Load();
            var facts = BuildingFacts.Load();
            var kit = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();

            var rich = BuildingBuilder.BuildRich(city, facts, kit, parts, wallCat);

            // ---- materials: resolve every submesh label BuildRich actually emitted ----
            var distinctLabels = new HashSet<string>();
            int submeshTotal = 0;
            foreach (var c in rich.Chunks)
            {
                foreach (var label in c.SubmeshNames)
                {
                    distinctLabels.Add(label);
                    CityMaterials.Get(label, wallCat); // populate the cache + MissingTextures
                    submeshTotal++;
                }
            }
            Debug.Log($"CRP id=materials distinctLabels={distinctLabels.Count} submeshInstancesAcrossChunks={submeshTotal} " +
                      $"created={CityMaterials.CreatedCount} missing={CityMaterials.MissingTextures.Count}");
            foreach (var miss in CityMaterials.MissingTextures)
                Debug.Log($"CRP MISSING_TEXTURE name={miss}");

            // ---- kit instancing: resolve every BuildingKit placement to a real FBX sub-mesh ----
            var report = new CityRenderer.ResolveReport();
            var batches = CityRenderer.BuildKitBatches(rich, kit, report);

            long totalInstanceDraws = 0, totalDrawCalls = 0;
            int minInstances = int.MaxValue, maxInstances = 0;
            foreach (var b in batches)
            {
                int n = b.Matrices?.Length ?? 0;
                totalInstanceDraws += n;
                totalDrawCalls += Mathf.Max(1, Mathf.CeilToInt(n / (float)CityRenderer.MaxInstancesPerCall));
                if (n < minInstances) minInstances = n;
                if (n > maxInstances) maxInstances = n;
            }
            int kitClaims = rich.Panels.Count + rich.Houses.Count + rich.Outs.Count + rich.Apts.Count + rich.Sheds.Count;
            Debug.Log($"CRP id=kit claims={kitClaims} resolvedBuildings={report.ResolvedInstances} " +
                      $"fallbackBuildings={report.FallbackInstances} missingVariants={report.MissingVariants.Count}");
            foreach (var mv in report.MissingVariants)
                Debug.Log($"CRP MISSING_KIT_VARIANT name={mv}");
            Debug.Log($"CRP id=batches count={batches.Count} totalSubmeshInstances={totalInstanceDraws} " +
                      $"minPerBatch={(batches.Count > 0 ? minInstances : 0)} maxPerBatch={maxInstances} " +
                      $"drawCalls={totalDrawCalls}");

            // ---- real-photograph facades (PhotoFacades.cs, verified separately) ----
            int photoAssigned = rich.Photo?.AssignedByBuilding.Count ?? 0;
            int photoUsedKeys = rich.Photo?.UsedPhotoKeys.Count ?? 0;
            Debug.Log($"CRP id=photo buildings={rich.NPhotoBuildings} assigned={photoAssigned} usedKeys={photoUsedKeys} " +
                      $"mallSkipped={rich.NMallSkipped} attribution=\"{rich.Photo?.Attribution}\"");

            // ---- building totals, for the "box vs real mesh" headline the brief asks for ----
            int totalBuildings = city.Buildings.Count;
            int boxExtruded = rich.NExtruded - rich.NPhotoBuildings; // extruded AND not a photo-wrapped box
            Debug.Log($"CRP id=totals buildings={totalBuildings} kitResolved={report.ResolvedInstances} " +
                      $"photoFacade={rich.NPhotoBuildings} plainBox={boxExtruded} kitFallenBackToBox={report.FallbackInstances}");

            // ---- explicit PASS/FAIL assertions (the brief's own bar) ----
            bool passKit = report.ResolvedInstances > 150000;
            bool passMat = CityMaterials.CreatedCount > 20;
            bool passMissing = CityMaterials.MissingTextures.Count == 0;
            bool passPhoto = rich.NPhotoBuildings == 273;
            Debug.Log($"CRP id=assert_kitInstances value={report.ResolvedInstances} need>150000 result={(passKit ? "PASS" : "FAIL")}");
            Debug.Log($"CRP id=assert_materialsCreated value={CityMaterials.CreatedCount} need>20 result={(passMat ? "PASS" : "FAIL")}");
            Debug.Log($"CRP id=assert_noMissingTextures value={CityMaterials.MissingTextures.Count} need=0 result={(passMissing ? "PASS" : "FAIL")}");
            Debug.Log($"CRP id=assert_photoFacades value={rich.NPhotoBuildings} need=273 result={(passPhoto ? "PASS" : "FAIL")}");
            Debug.Log($"CRP id=assert_overall result={((passKit && passMat && passMissing && passPhoto) ? "PASS" : "FAIL")}");

            ProbeExit.Exit(0);
        }
    }
}
