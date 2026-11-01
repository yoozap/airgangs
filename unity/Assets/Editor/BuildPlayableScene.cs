using System.Collections.Generic;
using System.IO;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Builds Assets/Scenes/Game.unity: the REAL thing — the rich city (the
    /// same TerrainBuilder/BuildingBuilder.BuildRich/CityRenderer pipeline
    /// Editor/BuildCityRender.cs already proved out, run again here rather than
    /// calling that file's own Run() so the Chunk objects whose Mesh goes into
    /// this scene are the SAME Chunk objects CombatBuildingChunk reads its
    /// Ranges/Heights off — no risk of a reloaded scene's serialized mesh
    /// drifting out of step with freshly-computed damage-addressing data),
    /// a flyable plane at the Delisi spawn (the data origin, same convention
    /// Editor/BuildFlyableScene.cs already uses), a hero body prefab reference,
    /// GameLoop + Boot wired, and the UI Toolkit panel attached (via
    /// Core/CombatWorldAdapter.cs's Install(), called at runtime by
    /// PlayableSceneBoot below — see that file's own header for why the
    /// wiring itself cannot be baked into the saved scene).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildPlayableScene.Run
    /// </summary>
    public static class BuildPlayableScene
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string MeshDir = "Assets/Generated/GamePlayable";
        const string MatDir = "Assets/Settings";
        const string PlaneFbxPath = "Assets/Resources/Art/Planes/p51.fbx";
        const string BodyFbxPath = "Assets/Resources/Art/Characters/banana.fbx";
        const string PlaneId = "p51";
        const string BodyId = "banana";
        const string CockpitKitPath = "Assets/Resources/Art/Props/cockpit.fbx"; // tools/build_cockpit.py output, pre-converted

        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CityMaterials.Reset();

            // NewScene destroys every unsaved object — open/build BEFORE anything
            // else exists (the exact BuildCityScene.cs/BuildCityRender.cs gotcha).
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var city = CityData.Load();
            Debug.Log($"GAMESCENE loaded city data ms={sw.ElapsedMilliseconds}");

            // PAIRED with the RoadNetworkRenderer/RiverRenderer/GrassRenderer entries in
            // the scenery array below -- never one without the other. The 2,746 river-
            // bank and tunnel cells are re-emitted at 5 m by GrassRenderer's cut terrain;
            // without this exclusion they draw TWICE (coarse + patch) and z-fight the
            // length of the Mtkvari; without the renderers they would be real holes.
            var terrainTiles = TerrainBuilder.Build(city.Terrain, CityGroundData.RefinedCells());
            Debug.Log($"GAMESCENE terrain tiles={terrainTiles.Count} ms={sw.ElapsedMilliseconds}");

            var facts = BuildingFacts.Load();
            var kit = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, facts, kit, parts, wallCat);
            Debug.Log($"GAMESCENE chunks={rich.Chunks.Count} kitClaims={rich.Panels.Count + rich.Houses.Count + rich.Outs.Count + rich.Apts.Count + rich.Sheds.Count} ms={sw.ElapsedMilliseconds}");

            // TbilisiFly/CityWall, NOT URP/Lit: stock URP/Lit does not read mesh.colors at
            // all, and TerrainBuilder.ComputeVertexColors (citymap.js:3883-3915) paints every
            // terrain vertex with the ground-class palette (dry #9c9273 / high #8a9464 /
            // green #5a9a3c / water #4d7fb5). On URP/Lit all of it was discarded and the
            // ground drew as one flat olive. CityWall multiplies IN.color.rgb onto the
            // albedo (CityWall.shader:176) and is SRP-Batcher compatible; white base so
            // the vertex palette IS the colour.
            var groundMat = MakeMat("PlayableGround", Color.white, 0.9f, "TbilisiFly/CityWall");

            // Core/SceneEnvironmentSetup.cs owns the sun, hemisphere, sky, fog,
            // tonemapping, shadow quality and environment reflections — it is the
            // port of src/game.js:242-339 `_setupScene`, probe-verified 33/33
            // against the real URP asset and Volume profile.
            //
            // It had ZERO production callers until now: this builder hand-rolled
            // its own Light with its own guessed intensity/rotation, so none of
            // that ported setup ran, and the game shipped without ACES
            // tonemapping or the measured 160 m shadow distance. The hand-built
            // sun below is replaced rather than kept, so there is exactly one
            // owner of the scene's lighting.
            // Left at the scene root deliberately: `bRoot` ("Buildings") is not
            // declared until later, and the lighting rig is not a child of the
            // city geometry anyway.
            var envGo = new GameObject("SceneEnvironment");
            var env = envGo.AddComponent<TbilisiFly.Core.SceneEnvironmentSetup>();
            env.Setup();
            Debug.Log("GAMESCENE sceneEnvironment=1 (sun/sky/fog/tonemap/shadows/reflections)");

            AssetDatabase.StartAssetEditing();

            // ---- terrain, WITH a collider this time (BuildCityRender.cs's own
            // tiles never got one — see Core/CombatWorldAdapter.cs's class header) ----
            var tRoot = new GameObject("Terrain").transform;
            foreach (var t in terrainTiles)
            {
                var go = Attach($"tile_{t.Col}_{t.Row}", t.Mesh, new[] { groundMat }, tRoot);
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = t.Mesh;
                go.AddComponent<CombatTerrainTag>();
            }

            // ---- building chunks, WITH a collider + CombatBuildingChunk ----
            var bRoot = new GameObject("Buildings").transform;
            int ci = 0;
            foreach (var c in rich.Chunks)
            {
                var mats = new Material[Mathf.Max(1, c.SubmeshNames.Count)];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = i < c.SubmeshNames.Count ? CityMaterials.Get(c.SubmeshNames[i], wallCat) : groundMat;
                var go = Attach($"chunk_{ci++}", c.Mesh, mats, bRoot);
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = c.Mesh;
                var dmg = go.AddComponent<CombatBuildingChunk>();
                int n = c.Ranges.Count;
                dmg.RangeStart = new int[n];
                dmg.RangeCount = new int[n];
                for (int i = 0; i < n; i++) { dmg.RangeStart[i] = c.Ranges[i].start; dmg.RangeCount[i] = c.Ranges[i].count; }
                dmg.Heights = c.Heights.ToArray();
                // Hole-carving/window-query inputs (Core/CombatWorldAdapter.cs's
                // PunchHole/BlastWindows/StripPropsAt/NearWindow) — plain
                // int[]/float[] bakes, same convention as RangeStart/RangeCount/
                // Heights above, aligned 1:1 with those by construction
                // (BuildingBuilder.Chunk.AddBuildingFacts is called in lockstep
                // with every Ranges.Add()).
                dmg.BuildingIndex = c.BuildingIndex.ToArray();
                dmg.WallBase = c.WallBase.ToArray();
                dmg.WallTop = c.WallTop.ToArray();
                dmg.ShellTint = c.ShellTint.ToArray();
                dmg.ShellWallBucket = c.ShellWallBucket.ToArray();
                dmg.FootprintFlat = c.FootprintFlat.ToArray();
                dmg.FootprintOffset = c.FootprintOffset.ToArray();
                dmg.FootprintCount = c.FootprintCount.ToArray();
            }
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();

            var kitGo = new GameObject("CityKitRenderer");
            kitGo.transform.SetParent(bRoot, false);
            kitGo.AddComponent<CityRenderer>();

            // Shopfronts: 14,728 named POIs -> 21,444 chunk-instanced glass
            // modules, 2,741 awnings, 321 canopies, 1,232 pharmacy/police
            // lightboxes and a 192-slot pooled SignPool carrying the real
            // Georgian business names. Same contract as CityKitRenderer above:
            // nothing is baked, OnEnable -> EnsureBuilt regenerates from the
            // data pipeline on every scene load. Needed in the SCENE and not
            // just in CityLoader's "shopfronts" stage, because
            // CityLoader.RunScenery has no runtime caller at all (only
            // Editor/Probes/CityLoadProbe.cs), so a stage-only wiring would
            // render during the bake and never in the game.
            var shopGo = new GameObject("ShopfrontKitRenderer");
            shopGo.transform.SetParent(bRoot, false);
            shopGo.AddComponent<TbilisiFly.City.ShopfrontRenderer>();

            // The rest of the scenery render layer, all following the
            // CityKitRenderer contract above: nothing baked, OnEnable ->
            // EnsureBuilt regenerates from the live data pipeline on every
            // scene load. They must live in the SCENE and not only in
            // CityLoader's stages, because CityLoader.RunScenery has NO runtime
            // caller (only Editor/Probes/CityLoadProbe.cs), so a stage-only
            // wiring renders during the bake and never in the game.
            var sceneryRoots = new List<GameObject> { kitGo, shopGo };
            foreach (var (nm, add) in new (string, System.Action<GameObject>)[]
            {
                ("VegetationRenderer", g => g.AddComponent<TbilisiFly.City.VegetationRenderer>()),
                ("ParkKitRenderer",    g => g.AddComponent<TbilisiFly.City.ParkRenderer>()),
                ("MetroRenderer",      g => g.AddComponent<TbilisiFly.City.MetroRenderer>()),
                ("MallRenderer",       g => g.AddComponent<TbilisiFly.City.MallRenderer>()),
                ("StreetFurnitureRenderer", g => g.AddComponent<TbilisiFly.City.StreetFurnitureRenderer>()),
                // The 11 built landmarks + 10 procedural + 10 hero statues + 298
                // instanced street statues/busts, and the LandmarkTag that
                // CombatWorldAdapter.IsLandmark looks for. It attaches the tag to
                // the 21 NAMED landmarks only, matching landmarks.js:1439-1448 (the
                // hero-statue place() calls pass no name, so they get none).
                // Without this line the renderer had no caller at all and
                // IsLandmark always answered no -- which is how 143 buildings came
                // to stand as destructible grey boxes under real landmarks.
                ("LandmarkRenderer",   g => g.AddComponent<TbilisiFly.City.LandmarkRenderer>()),
                // The windsocks at TBS (src/airport.js:1015-1058). Core/Wind.cs's
                // WindsockDriver was DEAD_UNREFERENCED because nothing ever
                // Instantiate()d windsock.fbx: AirportBuilder.PlaceWindsocks' only
                // caller is CityLoader.BuildAirport, and RunScenery has no runtime
                // caller -- the same stage-only wiring this list's own header warns
                // about. The renderer both places the prop and ticks the driver off
                // the ONE shared Wind state GameLoop already advances.
                ("WindsockRenderer",   g => g.AddComponent<TbilisiFly.City.WindsockRenderer>()),
                // The ground layer: roads/bridges/tunnels/infra (RoadBuilder, BridgeBuilder,
                // TunnelBuilder, TunnelCuts, InfraBuilder), the Mtkvari + lakes + embankment
                // kit (RiverSheet, WaterBodies), and the grass sheet + cut terrain
                // (GrassCover, CutTerrainBuilder). All probe-verified for weeks and drawn
                // NOWHERE until 2026-10-30: their only driver was CityLoader's stages,
                // which have no runtime caller. Same OnEnable -> EnsureBuilt contract as
                // every sibling above; nothing baked (scene stayed 11.6 MiB); PAIRED with
                // the TerrainBuilder exclusion at the top of this method.
                ("RoadNetworkRenderer", g => g.AddComponent<TbilisiFly.City.RoadNetworkRenderer>()),
                ("RiverRenderer",       g => g.AddComponent<TbilisiFly.City.RiverRenderer>()),
                ("GrassRenderer",       g => g.AddComponent<TbilisiFly.City.GrassRenderer>()),
            })
            {
                var go = new GameObject(nm);
                go.transform.SetParent(bRoot, false);
                add(go);
                sceneryRoots.Add(go);
            }

            Debug.Log($"GAMESCENE materials created={CityMaterials.CreatedCount} missingTextures={CityMaterials.MissingTextures.Count}");

            // ---- the warehouse (the player's base), its crane/wind tunnel, and
            // the plane hall with the parked fleet ----
            //
            // Until 2026-10-28 these ran in NO scene the game ever loads:
            // Editor/BuildPlaneHallScene.cs calls them, but it bakes
            // Assets/Scenes/PlaneHall.unity, which nothing loads -- so the whole
            // warehouse/crane/wind-tunnel feature and all 35 parked aircraft were
            // probe-verified and unreachable in play. They were then called INLINE
            // here, which fixed the reachability and cost ~1,400 serialised
            // GameObjects (plus their runtime Meshes/Materials) in Game.unity.
            //
            // They are now a SCENE COMPONENT instead -- Flight/ShedSceneFill.cs,
            // whose OnEnable calls exactly these three fills -- so the scene
            // stores a component and the geometry is built at load, the contract
            // City/CityRenderer.cs states and every scenery renderer above
            // follows. The three GAMESCENE warehouse=/crane=/planehall= lines
            // moved into that component verbatim and are therefore printed on
            // every LOAD as well as during this bake (AddComponent fires
            // [ExecuteAlways] OnEnable synchronously, right here).
            //
            // Once WarehouseCraneFill.Build has run, Props/WarehouseFanSwitch and
            // Props/WarehouseCraneSlingRig are self-attached MonoBehaviours that
            // Unity's own Update/LateUpdate ticks, and Props/WindRider.Tick
            // finally has a caller -- unchanged by the move, because AddComponent
            // at runtime delivers Awake synchronously.
            var shedGo = new GameObject("Sheds");
            // EnsureBuilt() explicitly rather than trusting AddComponent to
            // deliver [ExecuteAlways] OnEnable in edit mode: it does, but the
            // three GAMESCENE lines are this bake's own acceptance check, and an
            // acceptance check must not depend on an editor lifecycle detail.
            // EnsureBuilt is idempotent (its own `_built` guard), so if OnEnable
            // already ran this is a no-op — the same contract
            // CityRenderer.EnsureBuilt documents.
            shedGo.AddComponent<ShedSceneFill>().EnsureBuilt();
            sceneryRoots.Add(shedGo);

            // ---- the warehouse INTERIOR: the two shooting ranges, the three
            // 9x18 ammo crates, the three work tables and the pistols/rifle as
            // ground loot (src/airport.js:500-977).
            //
            // Until 2026-10-30 none of this was in ANY scene the game loads:
            // every builder was a PRIVATE STATIC inside
            // Editor/BuildAirportDetail.cs, a standalone bake that never saves
            // -- and whose output the stripped_generated step below would have
            // deleted even if this script had called it inline. So
            // RangeBootstrap.DiscoverAndAdoptAll() returned adoptedRanges=0 and
            // the lane card, the scored ISSF sheets and the pickup cards had
            // nothing to show. Props/WarehouseInteriorFill.cs is the scene
            // component that fixes it, same OnEnable -> EnsureBuilt contract as
            // ShedSceneFill above; it calls ShedSceneFill.EnsureBuilt() itself
            // rather than relying on OnEnable order, so AFTER shedGo here is
            // belt and braces, not the mechanism.
            var interiorGo = new GameObject("WarehouseInterior");
            interiorGo.transform.SetParent(bRoot, false);
            interiorGo.AddComponent<TbilisiFly.Props.WarehouseInteriorFill>().EnsureBuilt();
            sceneryRoots.Add(interiorGo);


            // ---- plane at the Delisi spawn (data origin == Unity (0,0), same
            // convention Editor/BuildFlyableScene.cs already uses) ----
            // THE DATA ORIGIN IS NOT THE SPAWN, and this baked pose is only a
            // placeholder now. The origin is Delisi, in the middle of a block,
            // so a plane left here sat INSIDE a building (a chunk_18 surface at
            // y 494.64 passed through the airframe; scratchpad/shots/
            // heroplane_top.png showed a roof). src/citymap.js:6484-6531 snaps
            // the spawn onto the nearest avenue at RUNTIME, which is now ported
            // end to end — City/SpawnFinder.cs via Core/SpawnResolver.cs, read
            // by Core/GameLoop.ResetPlane's PlaceOnSpawn. So this is where the
            // aeroplane and the chase camera STAND IN THE SAVED FILE, and
            // BeginSession moves both before the first frame is drawn.
            // Deliberately NOT replaced by the computed spot: baking the answer
            // is what the JS does not do.
            float groundY = city.Terrain.HeightAt(0f, 0f);
            Vector3 spawnPos = new Vector3(0f, groundY + 15f, 0f);

            var planeFbx = AssetDatabase.LoadAssetAtPath<GameObject>(PlaneFbxPath);
            var planeRoot = new GameObject("Plane");
            planeRoot.transform.SetPositionAndRotation(spawnPos, Quaternion.identity);
            var controller = planeRoot.AddComponent<PlaneController>();
            controller.PlaneId = PlaneId;
            var flightInput = planeRoot.AddComponent<FlightInput>();
            if (planeFbx != null)
            {
                // THE T-28 TRAP, AND THE TAIL-FIRST BUG, IN THE SAME TWO LINES.
                // This used to instantiate the FBX straight under the flight
                // root and then force localPosition/localRotation to
                // zero/identity. Both halves were wrong:
                //   * it DESTROYED the asset's natural import transform
                //     (p51.fbx is identity so nothing showed, but t28.fbx
                //     imports at rot (270.02,0,0) / scale 100 — the documented
                //     hard rule in .claude/skills/unity/SKILL.md), and
                //   * identity leaves the model's nose on -Z while
                //     Flight/FlightModel.cs thrusts along +Z, so the P-51 flew
                //     TAIL-FIRST, with its spinner measured at
                //     hubLocalToPlane z = -4.95 behind the pilot.
                // Flight/PlaneModelFrame.Attach composes instead: def.Scale,
                // src/planes.js:834's per-def orientation fix and the single
                // 180 deg yaw, with the import transform left alone.
                var model = (GameObject)PrefabUtility.InstantiatePrefab(planeFbx);
                PlaneModelFrame.Attach(planeRoot.transform, model, Planes.PlaneById(PlaneId));
            }
            else
            {
                Debug.LogWarning($"GAMESCENE missing plane FBX at {PlaneFbxPath}");
            }

            // real P-51 facts (src/planes.js's own row) for PlaneDamageRecord —
            // the per-plane hull/structural record Combat/PlaneDamage.cs reads.
            var rec = planeRoot.AddComponent<PlaneDamageRecord>();
            rec.Size = 9.1f; rec.HalfSpan = 5.64f; rec.NoseLen = 2.6f;
            rec.MaxHullPoints = 420f; rec.HullPoints = 420f;
            rec.DisplayName = "P-51 მუსტანგი";
            rec.IsPlayerOwn = true;

            // ---- cockpit bake: PlaneCockpitBaker.Bake() was fully built
            // (PlaneCockpitMeasurement.Measure + PlaneCockpitKit.FitCockpitKit/
            // BuildPlaneShell/HingeHood) but NOTHING called it anywhere in the
            // project — not even for the player's own aeroplane, the one
            // machine a pilot actually sits in. This is that call. Replaces
            // the old hand-authored 3.4x1.7x6.6 box (a guess) with the real,
            // measured box-shell BuildPlaneShell derives from the P-51's own
            // skin mesh, plus the real furniture kit, hinged controls and
            // opening hood that FootBoarding needs to seat a pilot and solve
            // hands/feet onto the stick/throttle/pedals.
            var cockpitKitPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CockpitKitPath);
            if (cockpitKitPrefab == null)
                Debug.LogWarning($"GAMESCENE missing cockpit kit at {CockpitKitPath} — plane will measure but carry no furniture/controls.");
            var baker = planeRoot.AddComponent<PlaneCockpitBaker>();
            var cockpitRig = PlaneCockpitBaker.Bake(planeRoot, Planes.PlaneById(PlaneId), cockpitKitPrefab,
                flyable: true, out var measuredCockpit);
            Debug.Log($"GAMESCENE cockpit baked={measuredCockpit != null} halfSpan={cockpitRig.HalfSpanValue:F2} " +
                $"shell={(cockpitRig.ShellValue != null ? cockpitRig.ShellValue.GetComponents<BoxCollider>().Length : 0)} " +
                $"kit={(cockpitRig.CockpitKitValue != null)} controls={(cockpitRig.ControlsValue != null)}");

            // ---- chase camera ----
            var camGo = new GameObject("Flight Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 1f;    // project gotcha — see BuildCityScene.cs
            cam.farClipPlane = 12000f; // the city is ~20 km across
            camGo.AddComponent<AudioListener>();
            var chase = camGo.AddComponent<FlightCamera>();
            chase.Plane = controller;
            camGo.transform.position = spawnPos + new Vector3(0f, 7f, -25f);
            camGo.transform.LookAt(spawnPos);

            // ---- hero body prefab reference (the CharacterRig wrapper itself is
            // built at runtime by PlayableSceneBoot/Boot.SpawnHeroBody — see
            // Core/CombatWorldAdapter.cs's own header on why this can't be baked) ----
            var bodyFbx = AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbxPath);
            if (bodyFbx == null) Debug.LogWarning($"GAMESCENE missing body FBX at {BodyFbxPath}");

            // ---- GameLoop ----
            var gameGo = new GameObject("GameLoop");
            var game = gameGo.AddComponent<GameLoop>();
            game.ActivePlane = controller;
            game.FlightCam = chase;
            game.StickInput_ = flightInput;
            game.MainCamera = cam;
            // env.Setup() ran ~240 lines above, BEFORE this camera existed, so
            // `env.Cam` was null and the CAMERA half of SceneEnvironmentSetup
            // (fov/near/far + UniversalAdditionalCameraData + post) was skipped:
            // the SAVED scene carried no UniversalAdditionalCameraData and kept
            // fov 60 / far 12000 in its YAML. Runtime Awake() re-runs Setup()
            // against Camera.main so the game was right and only the bake was
            // wrong — fixed here rather than left as a difference between the
            // baked scene and the played one. (Reported by the exposure pass.)
            env.Cam = cam; env.Setup();
            game.BodyPrefabToSpawn = bodyFbx;
            game.BodyId = BodyId;
            // STALE AS OF THE ShedSceneFill CHANGE, and corrected rather than
            // left to mislead: this scene DOES now have a modelled shed and
            // plane hall (Flight/ShedSceneFill.cs builds both at load). What is
            // still missing is the warehouse's PARK SPOT — ShedFill's
            // ShedBuildResult.Park/ParkYawLocalUnity carry src/shed.js:245-249's
            // real park pose but its own header marks them "inert, just
            // carried", and nothing reads them. So H's park-in-shed toggle still
            // returns to the plane's own spawn pose instead of parking inside
            // the warehouse (src/game.js _parkInShed). Wiring that means reading
            // ShedSceneFill.Warehouse.Park here, which needs the warehouse to
            // exist before GameLoop is configured — it does, the component is
            // built above — but it is a behaviour change in Core/GameLoop's
            // contract, not a scene-size fix, so it is reported, not smuggled in.
            game.ShedSpawnPos = spawnPos;
            game.ShedSpawnRot = Quaternion.identity;

            // ---- Boot (the real login->roster->fly flow, wired for when a
            // login UI/server is reachable; PlayableSceneBoot below reaches the
            // same end state without needing either) ----
            var bootGo = new GameObject("Boot");
            var boot = bootGo.AddComponent<Boot>();
            boot.Game = game;
            boot.MainCamera = cam;
            boot.BodyIds = new List<string> { BodyId };
            boot.BodyPrefabs = new List<GameObject> { bodyFbx };
            boot.PlaneControllers = new List<PlaneController> { controller };

            // ---- the no-login entry point ----
            var playableBootGo = new GameObject("PlayableSceneBoot");
            var playableBoot = playableBootGo.AddComponent<PlayableSceneBoot>();
            playableBoot.Game = game;

            // ---- DO NOT SERIALISE WHAT IS REGENERATED AT LOAD ----------------
            //
            // This is the whole 1.7 GB. Every renderer above is [ExecuteAlways]
            // with OnEnable -> EnsureBuilt, so AddComponent fires it right here,
            // during the bake, and the GameObjects + runtime Meshes/Materials/
            // Texture2Ds it produces were then written into Game.unity by
            // SaveScene below. A per-class byte census of the 1,866,677,217-byte
            // scene found 95.2% of it in just 210 embedded Meshes, and 1.756 GB
            // of that in THREE of them -- ParkBuilder's merged flower beds
            // flower_ursinia_0 (851 MB), flower_gazania_0 (570 MB) and
            // celandine_01_0 (334 MB) -- with another 71 MB in 74 runtime
            // Texture2Ds (MetroRenderer's Georgian station plates,
            // ParkCourtTextures' five court sheets, SignPool's sign atlas).
            //
            // None of it was ever USED: a load re-runs every OnEnable, which
            // rebuilds all of it from the data pipeline regardless. Reading the
            // 1.7 GB cost 6,055.9 ms of Deserialize and 2.50 GB of peak load
            // memory, for geometry the renderers then built again anyway.
            //
            // WORSE THAN WASTE, and this is why the strip is a correctness fix
            // and not only a size fix: only ShopfrontRenderer's ClearChildren()
            // walks real Transform children. ParkRenderer's and
            // LandmarkRenderer's clear a private `_children` List that a scene
            // load leaves EMPTY, and MetroRenderer/MallRenderer/
            // StreetFurnitureRenderer have no clear at all -- so the
            // deserialised copies survived and the fresh build was added
            // alongside them. Measured: the "Buildings" root held 2,735
            // descendants after a load of the baked scene and holds 1,771 after
            // a load of this one. ~964 scenery objects, and all three giant
            // flower meshes, were being rendered TWICE, co-located.
            //
            // Stripping it here is not a new rule; it is the rule those
            // renderers' own class headers already state ("nothing here is
            // baked into the scene file"). The bake keeps running them -- their
            // logged counts above are the bake's evidence that they WORK -- and
            // only their output is dropped before the file is written. The city
            // chunk/terrain meshes are NOT affected: Attach() writes those to
            // real .asset files under Assets/Generated/GamePlayable and the
            // scene references them by guid, which is deliberate (see this
            // class's header on keeping damage addressing in step).
            int strippedRoots = 0, strippedObjects = 0;
            foreach (var root in sceneryRoots)
            {
                if (root == null) continue;
                strippedRoots++;
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    var child = root.transform.GetChild(i).gameObject;
                    strippedObjects += child.GetComponentsInChildren<Transform>(true).Length;
                    UnityEngine.Object.DestroyImmediate(child);
                }
            }
            Debug.Log($"GAMESCENE stripped_generated roots={strippedRoots} objects={strippedObjects} " +
                      "(regenerated by each renderer's OnEnable on every load)");

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool already = false;
            foreach (var s in scenes) if (s.path == ScenePath) { already = true; break; }
            if (!already) scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            AssetDatabase.SaveAssets();
            Debug.Log($"GAMESCENE saved={ScenePath} totalMs={sw.ElapsedMilliseconds}");
            SceneSizeReport.ReportSize(); // SCENESIZE bytes= -- the after figure, in the bake's own log
            EditorApplication.Exit(0);
        }

        static GameObject Attach(string name, Mesh mesh, Material[] mats, Transform parent)
        {
            AssetDatabase.CreateAsset(mesh, $"{MeshDir}/{name}.asset");
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            return go;
        }

        static Material MakeMat(string name, Color c, float rough, string shaderName = null)
        {
            var path = $"{MatDir}/{name}.mat";
            var shader = shaderName != null ? Shader.Find(shaderName) : null;
            if (shader == null)
                shader = GraphicsSettings.currentRenderPipeline != null
                    ? Shader.Find("Universal Render Pipeline/Lit")
                    : Shader.Find("Standard");
            var m = new Material(shader) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 1f - rough);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
