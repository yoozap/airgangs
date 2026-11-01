using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves BuildingBuilder.BuildRich (WallMaterials + BuildingKit +
    /// BuildingParts + FacadeRelief — docs/QUALITY_BAR.md's building checklist)
    /// against tools/_probe_buildingquality.py, which computes the same facts
    /// straight from city.json.gz/levels.json/parts.json/kit_meta.json/
    /// walls.json/roofs.json with no Unity involved.
    ///
    /// Both sides print `BQ id=<name> key=value ...` lines with a DISTINCT
    /// textual id per line (tools/unity_port_check.py blanks only STANDALONE
    /// numbers; digits inside an id like `wallbucket_style0` or
    /// `roofshape_half-hipped` are kept, so lines pair by id, not by position).
    ///
    /// SCOPE NOTE: the "relief" line counts are the DECISION flags
    /// (FacadeRelief.DecideEarly — terrain-independent: WantWindows/
    /// WantBalconies/WantWoodBalc/WantCornice are building-level yes/no calls
    /// that only need height/area/district/cottage). They are NOT the same
    /// number as result.Relief.Windows/.Balconies.Count, which are actual
    /// PLACED item instances gated additionally by TerrainSampler.HeightAtGame
    /// (a building can want windows and still place zero if every floor
    /// happens to read as buried on a slope). The Python probe does not
    /// reimplement TerrainSampler, so only the decision-flag counts are
    /// cross-checked here — the per-building "did it want the feature" call,
    /// which is what the style chain and kit-claim logic actually decide.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.BuildingQualityProbe.Run
    /// </summary>
    public static class BuildingQualityProbe
    {
        public static void Run()
        {
            var city = CityData.Load();
            var facts = BuildingFacts.Load();
            var kit = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();

            var result = BuildingBuilder.BuildRich(city, facts, kit, parts, wallCat);

            Debug.Log($"BQ id=totals buildings={city.Buildings.Count} withFacts={result.NFacts} " +
                      $"pitched={result.NPitched} coloured={result.NColoured}");
            Debug.Log($"BQ id=parts expandedExtra={result.NPartsExpanded} partsItems={parts.Count}");
            Debug.Log($"BQ id=claims panel={result.Panels.Count} house={result.Houses.Count} " +
                      $"out={result.Outs.Count} apt={result.Apts.Count} shed={result.Sheds.Count} " +
                      $"extruded={result.NExtruded}");
            Debug.Log($"BQ id=relief wantWindows={result.NWantWindows} wantBalconies={result.NWantBalconies} " +
                      $"wantWoodBalc={result.NWantWoodBalc} wantCornice={result.NWantCornice}");

            // per-roof-shape (13 OSM kinds) — only buildings with a mapped shape
            var shapeKeys = new System.Collections.Generic.List<string>(result.RoofShapeCounts.Keys);
            shapeKeys.Sort();
            foreach (var shape in shapeKeys)
                Debug.Log($"BQ id=roofshape_{shape} count={result.RoofShapeCounts[shape]}");

            // per-wall-material bucket (11 OSM materials + 4 painted styles + loggia)
            // — strip the "wall:" mesh-submesh-naming prefix so the id matches
            // the Python probe's bare label (style0, brick_red, loggia:<key>, ...)
            var wbKeys = new System.Collections.Generic.List<string>(result.WallBucketCounts.Keys);
            wbKeys.Sort();
            foreach (var wb in wbKeys)
            {
                string label = wb.StartsWith("wall:") ? wb.Substring(5) : wb;
                Debug.Log($"BQ id=wallbucket_{label} count={result.WallBucketCounts[wb]}");
            }

            // item-instance counts (terrain-dependent — informative, not cross-checked 1:1, see class header)
            Debug.Log($"BQ id=itemcounts balconies={result.Relief.Balconies.Count} windows={result.Relief.Windows.Count} " +
                      $"cornices={result.Relief.Cornices.Count} grilles={result.Relief.Grilles.Count} " +
                      $"glassMullions={result.Relief.GlassMullions.Count} glassBands={result.Relief.GlassBands.Count}");

            ProbeExit.Exit(0);
        }
    }
}
