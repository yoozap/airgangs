using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Builds Assets/Scenes/PlaneHall.unity: the plane-hall shed (ShedFill.
    /// BuildPlaneHall) with all 35 aeroplanes parked in it (PlaneHallFill.Fill),
    /// plus — if that succeeds — the player's warehouse (ShedFill.BuildWarehouse)
    /// next door, exactly like src/planehall.js's addPlaneHall/_fill and
    /// src/shed.js's two buildShed call sites.
    ///
    /// Builds into a BRAND NEW scene file rather than opening Tbilisi.unity or
    /// Fly.unity (several other agents are editing those / their backing
    /// systems concurrently) — following BuildCityScene.cs's own established
    /// "NewScene destroys every unsaved object, so build the fresh scene
    /// first" ordering.
    ///
    /// Deliberately does NOT register the new scene in EditorBuildSettings —
    /// that is project-wide shared state (EditorBuildSettings.asset) several
    /// concurrent agents could be touching; the scene file existing on disk is
    /// enough for this task ("park the fleet"), and whoever wires the final
    /// build list can add it in one line later.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildPlaneHallScene.Run
    /// </summary>
    public static class BuildPlaneHallScene
    {
        const string OutScenePath = "Assets/Scenes/PlaneHall.unity";

        public static void Run()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int exitCode = 0;
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Debug.Log($"PLANEHALL newscene ms={sw.ElapsedMilliseconds}");

                // buildings:false — only the terrain sampler + the lat/lon
                // origin/metres-per-degree are needed here, same shortcut
                // BuildFlyableScene.cs takes.
                var city = CityData.Load(null, buildings: false);
                Debug.Log($"PLANEHALL cityloaded ms={sw.ElapsedMilliseconds}");

                var meta = new PlaneHallLayout.GeoMeta
                {
                    OriginLat = city.OriginLat, OriginLon = city.OriginLon,
                    MLat = city.MLat, MLon = city.MLon,
                };
                System.Func<float, float, float?> heightAtGame = (x, z) => city.Terrain.HeightAtGame(x, z);

                // ---- the plane hall ----
                var (hx, hz, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw); // src/planehall.js:85-99
                var hall = ShedFill.BuildPlaneHall(null, hx, hz, ShedLayout.ShedYaw, heightAtGame);
                Debug.Log($"PLANEHALL shell_w={hall.Shell.Width:F1} shell_l={hall.Shell.Length:F1} " +
                    $"shell_h={hall.Shell.Height:F1} pos_x={hall.Root.transform.position.x:F1} " +
                    $"pos_z={hall.Root.transform.position.z:F1} tris_before={hall.SolidTrianglesBefore} " +
                    $"tris_after={hall.SolidTrianglesAfter} pad_top={hall.PadTop:F2} pad_depth={hall.PadDepth:F2} " +
                    $"ramps_built={hall.RampsBuilt} ms={sw.ElapsedMilliseconds}");

                var fill = PlaneHallFill.Fill(hall);
                Debug.Log($"PLANEHALL parked={fill.Parked.Count} failed={fill.Failed.Count} " +
                    $"tanks={fill.TankCount} dps={fill.DpsCount} " +
                    $"tankdepth={fill.TankDepth:F1} dpsdepth={fill.DpsDepth:F1} " +
                    $"totaldepth={(fill.TankDepth + fill.DpsDepth):F1} ms={sw.ElapsedMilliseconds}");
                foreach (var id in fill.Failed)
                    Debug.LogWarning($"PLANEHALL plane_failed id={id}");
                if (fill.Parked.Count != 35)
                    Debug.LogWarning($"PLANEHALL parked count {fill.Parked.Count} != 35 expected");

                // ---- the warehouse, secondary (a sibling agent's airport-
                // interior furniture bake looks for a GameObject named
                // "warehouse") ----
                try
                {
                    var wh = ShedFill.BuildWarehouse(null, meta, heightAtGame);
                    Debug.Log($"PLANEHALL warehouse_built=1 shell_w={wh.Shell.Width:F1} " +
                        $"shell_l={wh.Shell.Length:F1} ms={sw.ElapsedMilliseconds}");

                    // src/crane.js's buildWarehouseCrane, called from airport.js:498 in
                    // the original. A BAKE call, not a scene component: once this runs,
                    // Props/WarehouseFanSwitch and Props/WarehouseCraneSlingRig are
                    // self-attached MonoBehaviours that Unity's own Update loop ticks,
                    // and WarehouseFanSwitch locates the live GameLoop itself. This is
                    // also what finally gives Props/WindRider.Tick a caller — it had
                    // ZERO call sites anywhere in the project until now, i.e. the whole
                    // wind-tunnel ride was correct, tested and unreachable.
                    var crane = TbilisiFly.Props.WarehouseCraneFill.Build(wh);
                    Debug.Log($"PLANEHALL crane_built={(crane.Built ? 1 : 0)} ms={sw.ElapsedMilliseconds}");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"PLANEHALL warehouse_built=0 reason={e.Message}");
                }

                var sunGo = new GameObject("Sun");
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.2f;
                sunGo.transform.rotation = Quaternion.Euler(46f, -38f, 0f);

                EditorSceneManager.SaveScene(scene, OutScenePath);
                AssetDatabase.SaveAssets();
                Debug.Log($"PLANEHALL saved={OutScenePath} totalMs={sw.ElapsedMilliseconds}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PLANEHALL FAILED: {e}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }
    }
}
