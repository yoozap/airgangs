using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves LandmarkPlacer.cs and AirportBuilder.cs place things at the real
    /// Tbilisi locations and not mirrored — modelled directly on
    /// CityPlaceProbe.cs's method (terrain height at the TRUE position vs the
    /// X-MIRRORED one, checked against real topography), extended to every
    /// named landmark + hero statue this port covers, plus the airport's main
    /// runway thresholds and the 31L spawn.
    ///
    /// Expected real elevations (docs porting brief, 2026-10-26), reproduced by
    /// tools/_probe_landmarks.py against city.json.gz direct from Python before
    /// this probe ever ran in the editor:
    ///   tv_tower ~752 m, sameba ~451 m, radisson(Rustaveli/Iveria valley) ~438 m,
    ///   chronicle ~632 m, delisi_spawn(origin) ~477 m.
    /// If this probe's h_true values disagree, the PLACEMENT is wrong — do not
    /// adjust the expectation.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.LandmarkProbe.Run
    /// </summary>
    public static class LandmarkProbe
    {
        // English identity slugs for the Georgian-named defs below, in the
        // SAME order as BuiltLandmarks/HeroStatues are declared (verified
        // against landmarks.js's own place() call order — see
        // LandmarkPlacer.cs:111-136,668-737) and matching
        // tools/_probe_landmarks.py's NAMED list byte for byte. The defs
        // themselves carry no English name (LandmarkDef.Name is Georgian,
        // byte for byte, never translate) — this id exists ONLY so this
        // probe's LANDMARK lines pair against the JS/Python ground truth
        // under tools/unity_port_check.py's skeleton-matching (a line keyed
        // only on Georgian text can never usefully diff against one keyed on
        // English — see that script's own header on why keys must agree).
        // ProceduralLandmarks already has an English Key field, but it is
        // GAME DATA (BuildProcedural's switch dispatches on its exact
        // camelCase spelling) — redeclared here in snake_case instead of
        // reusing/renaming it, so this probe never touches LandmarkPlacer.cs.
        static readonly string[] BuiltIds =
        {
            "tv_tower", "sameba", "radisson", "chronicle", "alphabet_tower",
            "ferris_wheel", "opera", "justice_house", "parliament", "dinamo", "philharmonic",
        };
        static readonly string[] ProceduralIds =
        {
            "narikala", "funicular_station", "metekhi", "svetitskhoveli", "jvari",
            "baths", "marriott", "biltmore", "sheraton_metechi", "radisson_red",
        };
        static readonly string[] HeroIds =
        {
            "freedom_monument", "kartlis_deda", "gorgasali", "rustaveli_monument",
            "david_builder", "saint_nino", "saakadze", "vazha_pshavela", "writers_pair", "pharnavaz",
        };

        /// <summary>Standalone entry point — prints then exits.</summary>
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        /// <summary>Body only, no Exit — so an aggregator can chain this with
        /// sibling probes in one Unity launch (same split as DamageProbe/
        /// BridgeProbe/UiProbe/PlaneCatalogProbe/etc. — see VerifyPass*Probe.cs).</summary>
        public static void Print()
        {
            var city = CityData.Load();
            var t = city.Terrain;
            Debug.Log($"LANDMARK_COUNT built={LandmarkPlacer.BuiltLandmarks.Length} " +
                      $"procedural={LandmarkPlacer.ProceduralLandmarks.Length} " +
                      $"heroStatues={LandmarkPlacer.HeroStatues.Length} " +
                      $"exclusions={LandmarkPlacer.Exclusions.Length} " +
                      $"streetStatues={LandmarkPlacer.StreetStatuePoints.GetLength(0)} " +
                      $"busts={LandmarkPlacer.BustPoints.GetLength(0)}");

            // Origin sanity point — tools/_probe_landmarks.py's own
            // delisi_spawn line (the data origin, expected ~477 m).
            {
                float hOrigin = t.HeightAt(0f, 0f);
                Debug.Log($"LANDMARK id=delisi_spawn kind=origin x=0.0 z=0.0 " +
                          $"h_true={hOrigin:F1} h_mirrored={hOrigin:F1} yaw_deg=0.0");
            }

            for (int bi = 0; bi < LandmarkPlacer.BuiltLandmarks.Length; bi++)
            {
                var def = LandmarkPlacer.BuiltLandmarks[bi];
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, t);
                float hMirrored = t.HeightAt(-pos.x, pos.z);
                Debug.Log($"LANDMARK id={BuiltIds[bi]} kind=built x={pos.x:F1} z={pos.z:F1} " +
                          $"h_true={pos.y:F1} h_mirrored={hMirrored:F1} yaw_deg={yawDeg:F1}");
                Debug.Log($"LANDMARK_MESH id={BuiltIds[bi]} asset={def.AssetPath} ref={def.JsRef}");
            }

            for (int pi = 0; pi < LandmarkPlacer.ProceduralLandmarks.Length; pi++)
            {
                var def = LandmarkPlacer.ProceduralLandmarks[pi];
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, t);
                float hMirrored = t.HeightAt(-pos.x, pos.z);
                var meshes = LandmarkPlacer.BuildProcedural(def.Key);
                int tris = 0;
                foreach (var (mesh, _) in meshes) tris += mesh.triangles.Length / 3;
                Debug.Log($"LANDMARK id={ProceduralIds[pi]} kind=procedural x={pos.x:F1} z={pos.z:F1} " +
                          $"h_true={pos.y:F1} h_mirrored={hMirrored:F1} yaw_deg={yawDeg:F1}");
                Debug.Log($"LANDMARK_MESH id={ProceduralIds[pi]} key={def.Key} ref={def.JsRef} " +
                          $"buckets={meshes.Count} tris={tris}");
            }

            for (int hi = 0; hi < LandmarkPlacer.HeroStatues.Length; hi++)
            {
                var def = LandmarkPlacer.HeroStatues[hi];
                var (pedestalPos, pedestalYawDeg, spotPos, spotYaw) = LandmarkPlacer.PlaceWorld(def, t);
                float hMirrored = t.HeightAt(-pedestalPos.x, pedestalPos.z);
                var pedestalMesh = LandmarkPlacer.BuildPedestal(def);
                Debug.Log($"LANDMARK id={HeroIds[hi]} kind=statue x={pedestalPos.x:F1} z={pedestalPos.z:F1} " +
                          $"h_true={pedestalPos.y:F1} h_mirrored={hMirrored:F1} yaw_deg={pedestalYawDeg:F1}");
                Debug.Log($"LANDMARK_MESH id={HeroIds[hi]} ref={def.JsRef} " +
                          $"pedestal_tris={pedestalMesh.triangles.Length / 3} spots={def.Spots.Length}");
                for (int i = 0; i < def.Spots.Length; i++)
                    Debug.Log($"LANDMARK_SPOT id={HeroIds[hi]} file={def.Spots[i].File} " +
                              $"x={spotPos[i].x:F1} y={spotPos[i].y:F1} z={spotPos[i].z:F1} " +
                              $"scale={def.Spots[i].Scale:F2} yaw_deg={spotYaw[i]:F1} tint={def.Spots[i].Tint}");
            }

            // instanced street statues / busts — spot-check first/last + a mid point
            var street = LandmarkPlacer.PlaceStreetStatues(t);
            var busts = LandmarkPlacer.PlaceBusts(t);
            foreach (int i in new[] { 0, street.Count / 2, street.Count - 1 })
                Debug.Log($"LANDMARK_STREET i={i} x={street[i].Pos.x:F1} y={street[i].Pos.y:F1} " +
                          $"z={street[i].Pos.z:F1} yaw_deg={street[i].YawDeg:F1} variant={street[i].Variant}");
            foreach (int i in new[] { 0, busts.Count / 2, busts.Count - 1 })
                Debug.Log($"LANDMARK_BUST i={i} x={busts[i].Pos.x:F1} y={busts[i].Pos.y:F1} " +
                          $"z={busts[i].Pos.z:F1} yaw_deg={busts[i].YawDeg:F1} variant={busts[i].Variant}");

            // ---- airport: runway thresholds + 31L spawn --------------------
            var ap = AirportBuilder.Load();
            AirportBuilder.BuildRunwayMain(ap, t, out var main);
            Vector3 A = GeoFrame.FromLatLon(main.Pts[0][0], main.Pts[0][1]);
            Vector3 B = GeoFrame.FromLatLon(main.Pts[main.Pts.Length - 1][0], main.Pts[main.Pts.Length - 1][1]);
            Debug.Log($"AIRPORT runway_ref={main.Ref} length_m={Vector3.Distance(A, B):F1} " +
                      $"A_13R_NW=({A.x:F1},{A.z:F1}) h={t.HeightAt(A.x, A.z):F1} " +
                      $"B_31L_SE=({B.x:F1},{B.z:F1}) h={t.HeightAt(B.x, B.z):F1}");
            var spawn = AirportBuilder.Get31LSpawn(ap, t);
            Debug.Log($"AIRPORT spawn_31L x={spawn.Pos.x:F1} y={spawn.Pos.y:F1} z={spawn.Pos.z:F1} " +
                      $"yaw_deg={spawn.YawDeg:F1}");

            var models = AirportBuilder.PlaceModels(ap, t);
            Debug.Log($"AIRPORT models={models.Count}");
            var marks = AirportBuilder.BuildRunwayMarkings(ap, t);
            var aprons = AirportBuilder.BuildAprons(ap, t);
            var taxiA = AirportBuilder.BuildTaxiwayAsphalt(ap, t);
            Debug.Log($"AIRPORT runway_markings_tris={marks.triangles.Length / 3} " +
                      $"apron_tris={aprons.triangles.Length / 3} taxiway_tris={taxiA.triangles.Length / 3}");
        }
    }
}
