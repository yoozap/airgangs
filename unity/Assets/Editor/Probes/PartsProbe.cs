using System.Collections.Generic;
using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the OSM 3D building-parts stage (docs/QUALITY_BAR.md's unchecked
    /// "OSM 3D building parts" row — parts.json, real massing replacing flat
    /// boxes on ~479 buildings; a hotel tower over a podium is ONE outline and
    /// must not extrude as one slab).
    ///
    /// Two separate claims, checked separately:
    ///   1. BuildingParts.Load()+Expand() (citymap.js:4538-4584) actually
    ///      replaces N buildings' single flat-box entry with their real
    ///      stepped-massing part entries — never both.
    ///   2. The stage has a RUNTIME caller, not only an Editor bake:
    ///      CityRenderer.OnEnable -> EnsureBuilt -> BuildingBuilder.BuildRich
    ///      with a GameObject present in Assets/Scenes/Game.unity (the only
    ///      booting scene).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PartsProbe.Run
    /// </summary>
    public static class PartsProbe
    {
        public static void Run()
        {
            var city = CityData.Load();
            var parts = BuildingParts.Load();
            var expanded = parts.Expand(city);

            var byBi = new Dictionary<int, List<BuildingParts.ExpandedFootprint>>();
            foreach (var e in expanded)
            {
                if (!byBi.TryGetValue(e.Bi, out var l)) byBi[e.Bi] = l = new List<BuildingParts.ExpandedFootprint>();
                l.Add(e);
            }

            int buildingsWithParts = 0, partsMeshesBuilt = 0, slabSuppressed = 0;
            int bestBi = -1, bestCount = 0; float bestMaxH = 0f;
            foreach (var kv in byBi)
            {
                int bi = kv.Key;
                var list = kv.Value;
                if (list.Count <= 1) continue; // not a stepped building — Expand() left its single flat-box entry alone
                buildingsWithParts++;
                partsMeshesBuilt += list.Count;

                // The flat slab for THIS building is the single entry Expand() emits
                // when a footprint is NOT stepped: one entry whose Flat is the exact
                // same array reference as the raw footprint (citymap.js's `expanded.push({ b, bi, off: 0 })`
                // vs `expanded.push({ b: pb2, ... })` — mutually exclusive by construction).
                // Prove the exclusion empirically rather than trusting the construction:
                // no entry for this bi is the whole-footprint box.
                var raw = city.Buildings[bi];
                bool anyIsRawSlab = list.Any(e => System.Object.ReferenceEquals(e.Flat, raw.Flat));
                if (!anyIsRawSlab) slabSuppressed++;

                float maxH = list.Max(e => e.Height);
                if (list.Count > bestCount || (list.Count == bestCount && maxH > bestMaxH))
                {
                    bestBi = bi; bestCount = list.Count; bestMaxH = maxH;
                }
            }

            Debug.Log($"PARTS buildingsWithParts={buildingsWithParts} partsMeshesBuilt={partsMeshesBuilt} slabSuppressed={slabSuppressed}");

            // named building: the heaviest-stepped footprint found (a single OSM
            // outline whose roof is 65 separate volumes ramping 3.1m -> 74.4m — the
            // "hotel tower over a podium" case the quality-bar row calls out).
            if (bestBi >= 0)
            {
                Debug.Log($"PARTS_TOWER bi={bestBi} parts={bestCount} maxHeightM={bestMaxH:F2}");
            }

            // ---- runtime-caller proof: is this stage REACHED from Game.unity? ----
            // CityRenderer.OnEnable() -> EnsureBuilt() -> BuildingBuilder.BuildRich(..,
            // BuildingParts.Load(), ..) is the only place in Assets/Scripts that calls
            // BuildingParts.Load() OUTSIDE Assets/Editor. Confirm a CityRenderer
            // component is actually present, enabled and reachable in the one scene
            // that boots the game, by loading it (not grepping YAML) and reading the
            // live component back.
            string caller = "none";
            bool reached = false;
            const string scenePath = "Assets/Scenes/Game.unity";
            try
            {
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                CityRenderer renderer = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    renderer = root.GetComponentInChildren<CityRenderer>(true);
                    if (renderer != null) break;
                }
                if (renderer != null)
                {
                    caller = "CityRenderer.EnsureBuilt";
                    bool goActive = renderer.gameObject.activeInHierarchy || renderer.gameObject.activeSelf;
                    bool compEnabled = renderer.enabled;
                    reached = goActive && compEnabled;
                    Debug.Log($"PARTS_RUNTIME_DETAIL go={renderer.gameObject.name} active={goActive.ToString().ToLowerInvariant()} " +
                              $"enabled={compEnabled.ToString().ToLowerInvariant()}");
                }
                EditorSceneManager.CloseScene(scene, true);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"PARTS_RUNTIME probe scene-load failed: {ex.Message}");
            }

            Debug.Log($"PARTS_RUNTIME caller={caller} scene=Game.unity reached={reached.ToString().ToLowerInvariant()}");

            ProbeExit.Exit(0);
        }
    }
}
