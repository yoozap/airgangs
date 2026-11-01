using System.Collections.Generic;
using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the citymap.js:6075-6241/6485-6596/7756-7940/8041-8186/8424-8528
    /// slice (FacadeKitLoader/FacadeKitBuilder/FacadeBoxBuilder/SpawnFinder/
    /// ChunkedSurfaceBuilder/VegetationLoader/ImposterBaker/RoadSignsStreetlights)
    /// against the real numbers tools/_probe_citygeo.mjs reads straight out of
    /// src/citymap.js. Every KEY printed here has a matching line from the Node
    /// probe — diff them.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityGeoProbe.Run
    /// </summary>
    public static class CityGeoProbe
    {
        public static void Run()
        {
            // ---- 1. constants, read straight off the internal/public fields ----
            Debug.Log($"SPAWN_LEGACY_YAW={SpawnFinder.LegacyYawMagnitude:F6}");
            Debug.Log($"SPAWN_AVENUE_MINW={SpawnFinder.AvenueMinWidth:F0}");
            Debug.Log($"SPAWN_ALREADY_ON_D2={SpawnFinder.AlreadyOnRoadD2:F0}");
            Debug.Log($"SPAWN_GIVEUP_D2={SpawnFinder.AvenueSearchGiveUpD2:F0}");
            Debug.Log($"SURF_DEFAULT_TILE={ChunkedSurfaceBuilder.DefaultTileSize:F0}");
            Debug.Log($"SIGN_MIN_WIDTH={RoadSignsStreetlights.SignMinRoadWidth:F0}");
            Debug.Log($"SIGN_TARGET_H={RoadSignsStreetlights.SignTargetHeight:F0}");
            Debug.Log($"POLE_MIN_WIDTH={RoadSignsStreetlights.PoleMinRoadWidth:F0}");
            Debug.Log($"POLE_SPACING={RoadSignsStreetlights.PoleSpacing:F0}");
            Debug.Log($"POLE_CAP={RoadSignsStreetlights.PoleCap}");
            Debug.Log($"LAMP_ARM={RoadSignsStreetlights.LampArmReach:F6}");
            Debug.Log($"LAMP_HEIGHT={RoadSignsStreetlights.LampHeight:F0}");
            Debug.Log($"POLE_DIMS={Num(RoadSignsStreetlights.PoleTopRadius)},{Num(RoadSignsStreetlights.PoleBottomRadius)},{Num(RoadSignsStreetlights.PoleHeight)},{RoadSignsStreetlights.PoleSides}");
            Debug.Log($"LAMP_DIMS={Num(RoadSignsStreetlights.LampSx)},{Num(RoadSignsStreetlights.LampSy)},{Num(RoadSignsStreetlights.LampSz)}");
            Debug.Log($"IMPOSTER_PXMIN={ImposterBaker.MinPx}");
            Debug.Log($"IMPOSTER_PXMAX={ImposterBaker.MaxPx}");
            Debug.Log($"TREE_IMPOSTER_TOPFRAC={ImposterBaker.TreeTopYFrac:F6}");
            Debug.Log($"LOD_TRI_THRESHOLD={FacadeKitBuilder.LodTriThreshold}");
            Debug.Log($"LOD_BASE_DIST={FacadeKitBuilder.LodBaseDist:F0}");
            Debug.Log($"LOD_CHUNK_FRAC={FacadeKitBuilder.LodChunkFrac:F6}");
            Debug.Log($"TREE_STREET_CAP={VegetationLoader.StreetCap}");
            Debug.Log($"TREE_FOREST_CAP={VegetationLoader.ForestCap}");
            Debug.Log($"TREE_TARGET_M2={VegetationLoader.TargetM2PerTree:F0}");
            Debug.Log($"TREE_STREET_SPACING={VegetationLoader.StreetSpacing:F0}");
            Debug.Log($"TREE_STREET_OFFSET_PAD={VegetationLoader.StreetOffsetPad:F6}");

            // ---- 2. SpawnFinder.Compute on the same synthetic 2-road network ----
            {
                var avenue = new RoadEntry { Width = 10f, Flat = new float[] { -20f, -50f, 20f, 50f } };
                var sideStreet = new RoadEntry { Width = 4f, Flat = new float[] { 20f, -10f, 20f, 10f } };
                var result = SpawnFinder.Compute(new List<RoadEntry> { avenue, sideStreet }, 3f, 6f);
                Debug.Log($"SPAWN_RESULT={result.X:F6},{result.Z:F6},{result.YawGame:F6}");
            }

            // ---- 3. the two fract-hash forms, same arguments as the Node probe ----
            Debug.Log($"RND2_SMALL={VegetationLoader.Rnd(900123, 4):F10}");
            Debug.Log($"RND2_WORLDSCALE={VegetationLoader.Rnd(264137.5, 8):F10}");
            Debug.Log($"RND1_WORLDSCALE={RoadSignsStreetlights.Rnd12_9898(264137.5):F10}");

            // ---- 4. ImposterBaker.ComputeViewCamera, same synthetic box ---------
            {
                var box = new Bounds();
                box.SetMinMax(new Vector3(7f, 1f, -22f), new Vector3(13f, 10f, -18f));
                var front = ImposterBaker.ComputeViewCamera(true, box);
                var top = ImposterBaker.ComputeViewCamera(false, box);
                Debug.Log($"VIEWCAM_FRONT={front.Position.x:F6},{front.Position.y:F6},{front.Position.z:F6}|{front.LookAt.x:F6},{front.LookAt.y:F6},{front.LookAt.z:F6}|{front.PixelW},{front.PixelH}");
                Debug.Log($"VIEWCAM_TOP={top.Position.x:F6},{top.Position.y:F6},{top.Position.z:F6}|{top.LookAt.x:F6},{top.LookAt.y:F6},{top.LookAt.z:F6}|{top.PixelW},{top.PixelH}");
            }

            // ---- 5. ChunkedSurfaceBuilder.Split on a real synthetic 3-tri mesh --
            {
                float[] xs = { -15f, -5f, 5f };
                var verts = new List<Vector3>();
                var tris = new List<int>();
                foreach (float x in xs)
                {
                    int b = verts.Count;
                    verts.Add(new Vector3(x, 0f, 0f));
                    verts.Add(new Vector3(x + 1f, 0f, 0f));
                    verts.Add(new Vector3(x, 0f, 1f));
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                }
                var mesh = new Mesh { name = "surf_probe" };
                mesh.SetVertices(verts);
                mesh.triangles = tris.ToArray();
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var tiles = ChunkedSurfaceBuilder.Split(mesh, 10f);
                var centres = tiles.Select(t => t.Cx).OrderBy(c => c).ToArray();
                Debug.Log($"SURF_BUCKET_CENTRES={string.Join(",", centres.Select(c => c.ToString("F6")))}");
            }

            // ---- 6. ImposterBaker.KitImposterBox, real shipped reorder ----------
            {
                Debug.Log($"KITIMPOSTER_ORDER={string.Join(",", ImposterBaker.KitFaceOrder)}");
                var box = new Bounds();
                box.SetMinMax(new Vector3(-1f, 0f, -1f), new Vector3(1f, 2f, 1f));
                var mesh = ImposterBaker.KitImposterBox(box);
                Debug.Log($"KITIMPOSTER_WALL_INDICES={mesh.GetTriangles(0).Length}");
                Debug.Log($"KITIMPOSTER_ROOF_INDICES={mesh.GetTriangles(1).Length}");
            }
        }

        // Invariant-culture, trailing-zero-free formatting so a float const like
        // 0.16f prints "0.16" here and in the regex-extracted JS text, not
        // "0.160000" or a comma-decimal under some other culture.
        static string Num(float v) => v.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);
    }
}
