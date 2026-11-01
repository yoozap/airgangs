using TbilisiFly.City;
using TbilisiFly.Props;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// STANDALONE bake entry point for the warehouse interior — the three work
    /// tables, the three ammo crates, the two pistols + the rifle, and the two
    /// shooting ranges (src/airport.js:500-977).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.EditorTools.BuildAirportDetail.Run
    ///        -logFile /dev/stdout
    ///
    /// IT IS NO LONGER THE IMPLEMENTATION, 2026-10-30. Every builder used to
    /// live here as a PRIVATE STATIC, which is exactly why the whole feature
    /// was in no scene the game ever loads: this script operates on whatever
    /// scene happens to be open and never saves it, and
    /// Editor/BuildPlayableScene.cs's `stripped_generated` step would have
    /// deleted the output of an inline call before SaveScene anyway. The
    /// builders therefore moved to Props/WarehouseInteriorFill.cs — an
    /// [ExecuteAlways] scene component whose OnEnable -> EnsureBuilt rebuilds
    /// the interior on every scene LOAD, the same contract
    /// Flight/ShedSceneFill.cs (the warehouse itself, its crane and the plane
    /// hall) and City/CityRenderer.cs follow. See that file's header for the
    /// build-order and handedness notes.
    ///
    /// This method is kept because it is still the cheapest way to exercise the
    /// interior on its own, with no city data and no Game.unity: it finds a
    /// "warehouse" GameObject if the open scene has one (reading its real size
    /// off its own renderer bounds, in its own local space) and otherwise
    /// synthesizes an empty stand-in with
    /// AirportDetail.EstimateWarehouseSizeFallback(). Both paths then call the
    /// ONE shared builder, so there is no second copy to drift.
    /// </summary>
    public static class BuildAirportDetail
    {
        public static void Run()
        {
            try
            {
                RunInner();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"AIRPORTDETAIL_FAIL {e}");
                EditorApplication.Exit(1);
            }
        }

        static void RunInner()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ---- find or synthesize the warehouse parent + its size -------
            var warehouse = GameObject.Find("warehouse");
            AirportDetail.ShedSize size;
            bool synthesized = false;
            if (warehouse != null)
            {
                size = WarehouseInteriorFill.MeasureWarehouseLocal(warehouse.transform);
                Debug.Log($"AIRPORTDETAIL warehouse=found width={size.Width:F2} height={size.Height:F2} length={size.Length:F2}");
            }
            else
            {
                warehouse = new GameObject("warehouse");
                size = AirportDetail.EstimateWarehouseSizeFallback();
                synthesized = true;
                Debug.Log($"AIRPORTDETAIL warehouse=synthesized width={size.Width:F2} height={size.Height:F2} length={size.Length:F2}");
            }

            var res = WarehouseInteriorFill.Build(warehouse.transform, size);

            Debug.Log($"AIRPORTDETAIL_DONE synthesized={(synthesized ? 1 : 0)} tables={res.Tables} " +
                      $"crates={res.Crates} guns={res.Guns} ranges={res.Ranges} ms={sw.ElapsedMilliseconds}");
        }
    }
}
