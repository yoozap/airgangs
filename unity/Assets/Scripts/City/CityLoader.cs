using System;
using System.Collections;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:476-931 `CityMap.load()` — the ORCHESTRATOR.
    /// Every builder under City/ (TerrainBuilder, BuildingBuilder, RoadBuilder,
    /// BridgeBuilder, InfraBuilder, RiverSheet, WaterBodies, GrassCover,
    /// WorshipPlacer, TunnelBuilder/TunnelCuts, ParkBuilder, MetroBuilder,
    /// MallPlacer, ShopfrontBuilder, LandmarkPlacer, AirportBuilder) already
    /// exists as an isolated, independently-verified `Build()`/`Load()` entry
    /// point with nobody calling it in sequence. This file is that sequence.
    ///
    /// ORDER IS LOAD-BEARING (see the port brief): a hidden browser tab killed
    /// the JS loader mid-flight once, which is why citymap.js reaches `ready`
    /// (playable: terrain + buildings + roads + tunnels + water, everything
    /// you need to not fall through the world) BEFORE a single tree, bench or
    /// landmark exists, and only then streams the rest in as background
    /// "scenery" stages. This file reproduces exactly that two-phase shape —
    /// <see cref="RunCritical"/> then <see cref="RunScenery"/> — and records
    /// the real call order into <see cref="Context.StageOrder"/> so a probe
    /// can prove it, not just read this file's own source and hope.
    ///
    /// PURE DATA/ORCHESTRATION — no GameObject, no Instantiate. Every stage
    /// below calls a builder's existing static entry point and stores the
    /// result on <see cref="Context"/>; nothing here constructs a scene object.
    /// Like GameLoop.cs (the sibling orchestrator for per-frame play), this
    /// file is itself the exception to "PURE DATA ... classes" that an
    /// orchestrator earns by definition — it is still GameObject-free.
    /// </summary>
    public static class CityLoader
    {
        // =====================================================================
        // Stage name lists — probe-readable PROOF of order, not just a claim.
        // CityLoadProbe.cs reads ctx.StageOrder (the REAL call order a run
        // produced) and diffs it against these constants.
        // =====================================================================

        /// <summary>citymap.js:476-888, up to and including "READY — playable"
        /// (citymap.js:889). One entry per distinct unit of work in that
        /// method, in its exact source order; see each stage method's own doc
        /// for the citymap.js line range it replaces.</summary>
        public static readonly string[] CriticalStageOrder =
        {
            "cityData",       // citymap.js:480-509 fetch + meta + terrain + buildings
            "roadNet",        // citymap.js:519-541 fetchRoadNet + mergeRoadNet
            "river",          // citymap.js:583-724 river.json + carve data (RiverSheet.Build does data+mesh together here)
            "tunnelGround",   // citymap.js:569-582 tunnel_ground.json measured overrides
            "tunnelCuts",     // citymap.js:725-726 _indexBuildings + _planTunnelCuts
            "terrainMesh",    // citymap.js:730 _buildTerrainMesh
            "buildingCatalogs", // citymap.js:744-851 parts/levels/roofs/loggia/walls/malls/kit fetches
            "buildings",      // citymap.js:852 _buildBuildings
            "spawn",          // citymap.js:856-868 _computeSpawn / exact spawnLatLon
            "roads",          // citymap.js:871 _buildRoads
            "junctions",      // citymap.js:873 buildJunctions
            "bridgeSpans",    // citymap.js:874 buildSpans
            "realBridges",    // citymap.js:875 buildRealBridges
            "tunnels",        // citymap.js:876 _buildTunnels
            "water",          // citymap.js:878 _buildWater (lakes)
            "riverSheet",     // citymap.js:880 _buildRiverSheet
            "ready",          // citymap.js:888-889 this.ready = true
        };

        /// <summary>citymap.js:895-910's `scenery` array, same order, same
        /// names (the JS logs `+ ${name}`) — one per frame, non-fatal on
        /// failure (citymap.js:911-921 `try { await fn() } catch`).</summary>
        public static readonly string[] SceneryStageOrder =
        {
            "trees", "rocks", "grass", "streetlights", "road signs", "worship",
            "landmarks", "parks", "metro", "airport", "plane hall", "malls",
            "infrastructure", "shopfronts",
        };

        /// <summary>citymap.js:930 `setTimeout(() => this._buildGroves()...,
        /// 2500)` — deferred even further than the scenery loop, so it never
        /// competes with the game being playable.</summary>
        public const string GrovesStage = "groves";

        /// <summary>Stage names with NO ported builder in City/ as of this
        /// pass. Each still appears in <see cref="SceneryStageOrder"/> (the
        /// ORDER must stay honest even where the BUILD is missing) but
        /// <see cref="RunScenery"/> records it as a no-op and lists it here
        /// rather than silently skipping it — see GameLoop.cs's own
        /// "WHAT IS STUBBED" precedent for the same honesty pattern.</summary>
        // Empty at last: every scenery stage now has a real renderer. Kept as a
    // field because callers still read it, and because the next unported
    // stage should be recorded here rather than left as a silent no-op.
    public static readonly string[] UnportedStages = { };

        // =====================================================================
        // Context — everything a later stage needs from an earlier one.
        // =====================================================================
        public sealed class Context
        {
            public string StreamingRoot = System.IO.Path.Combine(Application.streamingAssetsPath, "tbilisi");
            public int MaxBuildings = int.MaxValue;

            public readonly List<string> StageOrder = new List<string>();
            public readonly Dictionary<string, string> StageErrors = new Dictionary<string, string>();

            public CityData City;
            public RoadNet RoadNet;
            public MergedRoads Roads;
            public RiverSheet River;
            public TunnelCuts.GroundOverride TunnelGroundOverride;
            public TunnelCuts.BuildingIndex BuildingIndex;
            public TunnelCuts.Plan TunnelPlan;
            public TunnelBuildResult Tunnels;

            public BuildingFacts Facts;
            public BuildingKit.Catalog Kit;
            public BuildingParts Parts;
            public WallMaterials.Catalog WallCat;
            public BuildingBuilder.RichResult Rich;
            public FacadeProps.Items PropsItems = new FacadeProps.Items();
            public int FacadePropsBuildingsProcessed, FacadePropsBuildingsFailed, FacadePropsKitClaimsSkipped;

            public Vector3 Spawn;
            public float SpawnYawUnityDeg;

            public RoadDeckResult RoadDeck;
            public Mesh Junctions;
            public SpansResult Spans;
            public RealBridgesResult RealBridges;
            public List<RealBridgeZone> RealBridgeZones;

            public WaterBodies Water;
            public GrassCover Grass;
            public WorshipPlacer Worship;
            public ParkBuilder Parks;
            public MetroBuilder Metro;
            public MallPlacer.Result Malls;
            public ShopfrontBuilder Shopfronts;
            // Scenery RENDER results (2026-10-27). These stages were literal
            // no-ops until now — see the UnportedStages note above.
            public List<StreetFurnitureRenderer.Batch> RoadSignBatches;
            public List<StreetFurnitureRenderer.Batch> StreetlightBatches;
            public PlaneHallRenderer.Result PlaneHall;
            public AirportBuilder.AirportJson Airport;
            public AirportBuilder.SpawnInfo AirportSpawn;
            public readonly List<(Vector3 pos, float yawUnityDeg, string name)> Landmarks = new List<(Vector3, float, string)>();
            public List<LandmarkPlacer.InstancedStatue> StreetStatues;
            public List<LandmarkPlacer.InstancedStatue> Busts;
            public (List<FurniturePlacement> items, InfraCounts counts) InfraEmbankment;
            public (List<FurniturePlacement> items, InfraCounts counts) InfraBridgeFurniture;
            public (List<FurniturePlacement> items, InfraCounts counts) InfraTunnelHeadwalls;
            public (List<FurniturePlacement> items, InfraCounts counts) InfraStreetFurniture;

            public bool Ready;
        }

        static void Stage(Context ctx, string name, Action onStage, Action run)
        {
            ctx.StageOrder.Add(name);
            onStage?.Invoke();
            try { run(); }
            catch (Exception e)
            {
                ctx.StageErrors[name] = e.Message;
                Debug.LogException(e);
            }
        }

        // =====================================================================
        // THE CRITICAL PATH — citymap.js:476-889. Blocking (no `ready` flag
        // flips until this returns), same as the JS up to its own `this.ready
        // = true`.
        // =====================================================================
        public static IEnumerator RunCritical(Context ctx, Action<string> onProgress = null, Action<string> onStage = null)
        {
            onProgress?.Invoke("იტვირთება რუკის მონაცემები…"); // citymap.js:477

            Stage(ctx, "cityData", () => onStage?.Invoke("cityData"), () =>
            {
                ctx.City = CityData.Load(buildings: true); // citymap.js:480-494, 730 (terrain mesh is TerrainBuilder's own stage below)
            });
            yield return null; // citymap.js:492/516/729/855 `await nextFrame()`

            Stage(ctx, "roadNet", () => onStage?.Invoke("roadNet"), () =>
            {
                var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
                RoadNet net = null;
                try { net = RoadBuilder.LoadRoadNet(); } catch { /* no roadnet.json.gz — legacy lists stand, citymap.js:539-541 */ }
                ctx.RoadNet = net;
                ctx.Roads = net != null
                    ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                    : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };
            });
            yield return null;

            Stage(ctx, "river", () => onStage?.Invoke("river"), () =>
            {
                // RiverSheet.Build both reads river.json AND builds the sheet
                // mesh in one call in this port (unlike the JS, which loads the
                // carve data here at citymap.js:583-724 and only builds the
                // visible mesh much later at citymap.js:880 _buildRiverSheet).
                // Called here so TerrainBuilder below can see the carve; the
                // mesh itself is unused until the "riverSheet" stage re-reads
                // ctx.River — recomputing is wasted work, not a correctness gap.
                ctx.River = RiverSheet.Build(ctx.City?.Terrain);
            });
            yield return null;

            Stage(ctx, "tunnelGround", () => onStage?.Invoke("tunnelGround"), () =>
            {
                ctx.TunnelGroundOverride = TunnelCuts.GroundOverride.Load(); // citymap.js:569-582, missing file = null override
            });
            yield return null;

            Stage(ctx, "tunnelCuts", () => onStage?.Invoke("tunnelCuts"), () =>
            {
                if (ctx.City == null) return;
                ctx.BuildingIndex = TunnelCuts.BuildingIndex.Build(ctx.City.Buildings); // citymap.js:725 _indexBuildings
                ctx.TunnelPlan = TunnelCuts.Build(ctx.Roads?.Tunnels ?? new List<RoadEntry>(),
                    ctx.Roads?.Roads ?? new List<RoadEntry>(),
                    ctx.City.Terrain.HeightAtGame, ctx.BuildingIndex, ctx.TunnelGroundOverride); // citymap.js:726

                // Hook the planned cuts into the ground sampler, so every later
                // height read — roads, props, footsteps, the plane's ground
                // probe — sees the trench instead of the hill that used to be
                // there. TunnelCuts.Plan.CutAt's own wiring note asks for
                // exactly this, and until now nothing assigned it: the cuts
                // were planned and the geometry built, but the GROUND still
                // read as uncut, which is the "roads under grass" bug class.
                //
                // ORDER MATTERS, and it is why this sits AFTER Build rather
                // than beside the sampler's construction. Build() is handed
                // `Terrain.HeightAtGame` as its own input; assigning GroundCut
                // before it ran would feed cut heights back into the planner
                // that produces the cuts. src/citymap.js does the same thing in
                // the same order — `_cutFlag` is set at citymap.js:3645, after
                // planning, and heightAt only consults the cuts once it is on
                // (citymap.js:3226). Build() already calls plan.Finish(), so
                // the lookup hash exists by this line.
                if (ctx.TunnelPlan != null && ctx.TunnelPlan.HasCuts)
                {
                    var cuts = ctx.TunnelPlan;
                    // Never raise the surface, only lower it into a hole — the
                    // `cy < y` test at citymap.js:3226-3229. TerrainSampler
                    // applies that comparison itself; returning the candidate
                    // unconditionally here would let a batter blend lift ground.
                    ctx.City.Terrain.GroundCut = (x, z, y) => cuts.CutAt(x, z, y);
                }
            });
            yield return null;

            Stage(ctx, "terrainMesh", () => onStage?.Invoke("terrainMesh"), () =>
            {
                onProgress?.Invoke("იქმნება რელიეფი…"); // citymap.js:728
                if (ctx.City?.Terrain != null) TerrainBuilder.Build(ctx.City.Terrain); // citymap.js:730
            });
            yield return null;

            Stage(ctx, "buildingCatalogs", () => onStage?.Invoke("buildingCatalogs"), () =>
            {
                // citymap.js:744-851 parts.json / levels.json / roofs.json /
                // loggia_tiles.json / walls.json / malls.json / the 5 kit GLBs.
                ctx.Facts = BuildingFacts.Load();
                ctx.Kit = BuildingKit.Catalog.Load();
                ctx.Parts = BuildingParts.Load();
                ctx.WallCat = WallMaterials.Catalog.Load();
            });
            yield return null;

            Stage(ctx, "buildings", () => onStage?.Invoke("buildings"), () =>
            {
                if (ctx.City == null) return;
                onProgress?.Invoke("შენდება სახლები…"); // citymap.js:4950-ish progress text, see BuildRich's own per-chunk progress
                ctx.Rich = BuildingBuilder.BuildRich(ctx.City, ctx.Facts, ctx.Kit, ctx.Parts, ctx.WallCat, null, ctx.MaxBuildings); // citymap.js:852
                // The six FacadeProps layers come straight off BuildRich now —
                // docs/open_items.json `facade-props-pitched`, closed 2026-10-31.
                // This stage used to re-derive them itself (see the deleted
                // BuildFacadePropsForEveryBuilding) because BuildRich did not
                // surface its per-building state; that re-derivation had to
                // hardcode `pitchedFinal = false`, which it documented as an
                // over-placement, and would have put parapets and roof vents on
                // gabled roofs. BuildRich owns the call now, one line after the
                // FacadeRelief.BuildFacade it has to stay in hash-step with.
                ctx.PropsItems = ctx.Rich.Props;
                ctx.FacadePropsBuildingsProcessed = ctx.Rich.NExtruded - ctx.Rich.NPhotoBuildings;
                ctx.FacadePropsBuildingsFailed = 0;
                ctx.FacadePropsKitClaimsSkipped = ctx.Rich.Panels.Count + ctx.Rich.Houses.Count
                    + ctx.Rich.Outs.Count + ctx.Rich.Apts.Count + ctx.Rich.Sheds.Count;
            });
            yield return null;

            Stage(ctx, "spawn", () => onStage?.Invoke("spawn"), () =>
            {
                onProgress?.Invoke("იგება გზები და მდინარე…"); // citymap.js:854
                // citymap.js:856-868. The real road-snapping half of
                // `_computeSpawn` (citymap.js:_computeSpawn, not in this port's
                // file list) is NOT ported — this stage only reproduces the
                // "exact spawn" branch (spawnLatLon.yaw given, e.g. the TBS
                // runway spawn AirportBuilder.Get31LSpawn already computes).
                // A caller wanting the default in-town spawn must supply its
                // own road-nearest point until that function is ported.
                if (ctx.City != null)
                {
                    ctx.Spawn = GeoFrame.FromLatLon(ctx.City.OriginLat, ctx.City.OriginLon);
                    ctx.SpawnYawUnityDeg = 0f;
                }
            });
            yield return null;

            Stage(ctx, "roads", () => onStage?.Invoke("roads"), () =>
            {
                if (ctx.City?.Terrain != null && ctx.Roads != null)
                    ctx.RoadDeck = RoadBuilder.BuildRoadDeck(ctx.City.Terrain, ctx.Roads.Roads, ctx.Roads.Meta); // citymap.js:871
            });
            yield return null;

            Stage(ctx, "junctions", () => onStage?.Invoke("junctions"), () =>
            {
                if (ctx.City?.Terrain != null && ctx.Roads != null)
                    ctx.Junctions = RoadBuilder.BuildJunctions(ctx.City.Terrain, ctx.Roads.Junctions); // citymap.js:873
            });
            yield return null;

            Stage(ctx, "bridgeSpans", () => onStage?.Invoke("bridgeSpans"), () =>
            {
                if (ctx.City?.Terrain == null || ctx.Roads == null) return;
                ctx.RealBridgeZones = BridgeBuilder.RealBridgeZones(ctx.City); // citymap.js:874 realBridgeZones(this)
                ctx.Spans = BridgeBuilder.BuildSpans(ctx.City.Terrain, ctx.Roads.Bridges, ctx.Roads.BridgeMeta, ctx.Roads.Roads, ctx.RealBridgeZones); // citymap.js:874 buildSpans
            });
            yield return null;

            Stage(ctx, "realBridges", () => onStage?.Invoke("realBridges"), () =>
            {
                if (ctx.City != null) ctx.RealBridges = BridgeBuilder.BuildRealBridges(ctx.City); // citymap.js:875
            });
            yield return null;

            Stage(ctx, "tunnels", () => onStage?.Invoke("tunnels"), () =>
            {
                if (ctx.City?.Terrain != null && ctx.BuildingIndex != null)
                    ctx.Tunnels = TunnelBuilder.Build(ctx.TunnelPlan, ctx.City.Terrain, ctx.BuildingIndex); // citymap.js:876
            });
            yield return null;

            Stage(ctx, "water", () => onStage?.Invoke("water"), () =>
            {
                onProgress?.Invoke("წყალი…");
                if (ctx.City?.Terrain != null) ctx.Water = WaterBodies.Build(ctx.City.Terrain); // citymap.js:878
            });
            yield return null;

            Stage(ctx, "riverSheet", () => onStage?.Invoke("riverSheet"), () =>
            {
                // re-reads/-builds nothing new — ctx.River already holds the
                // Mesh+Banks from the "river" stage above (see that stage's
                // own doc for why RiverSheet.Build is a single combined call
                // in this port). Named as its own stage purely so StageOrder
                // matches citymap.js:880's own phase('river sheet + banks') 1:1.
            });
            yield return null;

            Stage(ctx, "ready", () => onStage?.Invoke("ready"), () =>
            {
                ctx.Ready = true; // citymap.js:888
            });
        }

        // The 90-line BuildFacadePropsForEveryBuilding that stood here is
        // GONE (2026-10-31, docs/open_items.json `facade-props-pitched`). It
        // re-derived centroid/height/style/claim per footprint from public APIs
        // in order to call FacadeProps.BuildFacadeProps at all, and documented
        // three approximations while doing it — the load-bearing one being
        // `pitchedFinal = false`, "an OVER-placement on the city's real
        // pitched-roof stock". Its own doc named the replacement: "A future
        // pass that threads the real per-building values through (by having
        // BuildingBuilder.BuildRich itself call FacadeProps once it is allowed
        // to) replaces this approximation with the exact one at zero extra
        // cost — this is a one-line call in that per-building loop once that
        // file is back in scope." That is exactly what happened; the stage
        // above now reads ctx.Rich.Props.

        // =====================================================================
        // SCENERY — citymap.js:895-927. One stage per (simulated) frame,
        // never blocks `ready`, a failure here is logged and the loop moves on
        // (citymap.js:915-919).
        // =====================================================================
        public static IEnumerator RunScenery(Context ctx, Action<string> onStage = null)
        {
            foreach (string name in SceneryStageOrder)
            {
                yield return null; // citymap.js:914 `await nextFrame()` — one layer per frame
                Stage(ctx, name, () => onStage?.Invoke(name), () => RunSceneryStage(ctx, name));
            }
        }

        static void RunSceneryStage(Context ctx, string name)
        {
            switch (name)
            {
                case "trees": case "rocks":
                    // One call builds trees AND groves AND rocks: groves have no
                    // stage of their own because the JS defers them via its own
                    // setTimeout, outside the scenery array (citymap.js:930).
                    VegetationRenderer.Render(ctx);
                    break;
                case "streetlights":
                    // 1,137 real lamps. NOTE: no `Light` component is created,
                    // deliberately — citymap.js uses a plain emissive material
                    // (0xfff2c4 / emissive 0xffd070 x1.4), never a THREE.PointLight,
                    // and a Light per lamp would reintroduce the lights-hash shader
                    // storm that caused the shooting/crash freeze (a new PointLight
                    // recompiles EVERY material). Probe asserts 0 lights before and
                    // 0 after, independent of lamp count.
                    ctx.StreetlightBatches = StreetFurnitureRenderer.RenderStreetlights(ctx);
                    break;
                case "road signs":
                    ctx.RoadSignBatches = StreetFurnitureRenderer.RenderSigns(ctx);
                    break;
                case "plane hall":
                    // The one deliberate exception to "no GameObject per object" in
                    // this switch: 1 hall + 35 real airframes is what
                    // src/planehall.js itself builds, not the thousands-of-props
                    // case that rule exists for. All 35 sit under one
                    // `planehall_fleet` switch driven by PlaneHallVisibility.
                    ctx.PlaneHall = PlaneHallRenderer.Render(ctx);
                    break;
                case "grass":
                    if (ctx.City?.Terrain != null) ctx.Grass = GrassCover.Build(ctx.City.Terrain, ctx.River);
                    break;
                case "worship":
                    if (ctx.City?.Terrain != null) ctx.Worship = WorshipPlacer.Build(ctx.City.Terrain);
                    break;
                case "landmarks":
                    BuildLandmarks(ctx);
                    break;
                case "parks":
                    ctx.Parks = ParkBuilder.Build(ctx.StreamingRoot, ctx.City?.Terrain);
                    ParkRenderer.Render(ctx);
                    break;
                case "metro":
                    ctx.Metro = MetroBuilder.Build(ctx.StreamingRoot, ctx.City?.Terrain);
                    MetroRenderer.Render(ctx);
                    break;
                case "airport":
                    BuildAirport(ctx);
                    break;
                case "malls":
                    ctx.Malls = MallPlacer.Build(ctx.StreamingRoot, ctx.City?.Terrain);
                    MallRenderer.Render(ctx);
                    break;
                case "infrastructure":
                    BuildInfrastructure(ctx);
                    break;
                case "shopfronts":
                    if (ctx.City != null) ctx.Shopfronts = ShopfrontBuilder.Build(ctx.StreamingRoot, ctx.City);
                    // GAP CLOSED 2026-10-27: ctx.Shopfronts used to be computed
                    // and read by nothing. City/ShopfrontRenderer.cs is the
                    // consumer. NOTE this stage only runs in the Editor bake
                    // (RunScenery has no runtime caller), so the SCENE also
                    // carries a ShopfrontRenderer component — see
                    // Editor/BuildCityRender.cs and Editor/BuildPlayableScene.cs.
                    ShopfrontRenderer.Render(ctx);
                    // (The long GAP note that used to sit here is gone: it listed
                    // the three missing pieces — chunk-instancing the modules onto
                    // facade_kit's win_shop submesh, turning AwningMesh/CanopyMesh
                    // into real renderers, and constructing + ticking SignPool — and
                    // all three are now in City/ShopfrontRenderer.cs, verified at the
                    // component level, not just by a static call. Still open and
                    // deliberately NOT faked: the awning draws one striped material
                    // instead of the JS's two alternating colours (AwningMesh is
                    // already merged with no per-item slot left), sign/police glyph
                    // rasterisation (ShopSignTextures.cs's own pre-existing gap —
                    // the real Georgian name DOES reach the painted plate, only the
                    // glyphs are missing), _tagFacadeProps collapse/shatter tagging
                    // (no Unity consumer exists yet anywhere), and the McDonald's
                    // standalone pad at shopfronts.js:346-353.
                    //
                    // NOT a gap, despite looking like one: facade_kit only contains
                    // "ac", so modules draw a procedural fallback box — which is what
                    // src/citymap.js:6171 does too (geoOf falls back to a
                    // BoxGeometry). The JS asset has the same single mesh. See
                    // docs/QUALITY_BAR.md's "NON-FINDING" section before chasing it.
                    break;
            }
        }

        static void BuildLandmarks(Context ctx)
        {
            if (ctx.City?.Terrain == null) return;
            foreach (var def in LandmarkPlacer.BuiltLandmarks)
            {
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, ctx.City.Terrain);
                ctx.Landmarks.Add((pos, yawDeg, def.Name));
            }
            foreach (var def in LandmarkPlacer.ProceduralLandmarks)
            {
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, ctx.City.Terrain);
                ctx.Landmarks.Add((pos, yawDeg, def.Name));
                LandmarkPlacer.BuildProcedural(def.Key); // mesh/tint pairs — consumed by a rendering layer, not stored here
            }
            ctx.StreetStatues = LandmarkPlacer.PlaceStreetStatues(ctx.City.Terrain);
            ctx.Busts = LandmarkPlacer.PlaceBusts(ctx.City.Terrain);
        }

        static void BuildAirport(Context ctx)
        {
            ctx.Airport = AirportBuilder.Load();
            if (ctx.Airport == null || ctx.City?.Terrain == null) return;
            var terrain = ctx.City.Terrain;
            AirportBuilder.PlaceModels(ctx.Airport, terrain);
            AirportBuilder.PlaceWindsocks(ctx.Airport, terrain);
            AirportBuilder.BuildTaxiwayAsphalt(ctx.Airport, terrain);
            AirportBuilder.BuildTaxiwayCenterline(ctx.Airport, terrain);
            AirportBuilder.BuildRunwayMain(ctx.Airport, terrain, out _);
            AirportBuilder.BuildRunwayCrosswind(ctx.Airport, terrain);
            AirportBuilder.BuildAprons(ctx.Airport, terrain);
            AirportBuilder.BuildRunwayMarkings(ctx.Airport, terrain);
            AirportBuilder.BuildHelipadMarkings(ctx.Airport, terrain);
            AirportBuilder.PlaceApproachLights(ctx.Airport, terrain);
            ctx.AirportSpawn = AirportBuilder.Get31LSpawn(ctx.Airport, terrain);
        }

        static void BuildInfrastructure(Context ctx)
        {
            if (ctx.City?.Terrain == null) return;
            var rails = ctx.RealBridges.Rails ?? new List<BridgeRail>();
            ctx.InfraEmbankment = InfraBuilder.BuildRiverEmbankment(ctx.City.Terrain, ctx.River?.Banks, rails);
            ctx.InfraBridgeFurniture = InfraBuilder.BuildBridgeFurniture(rails, ctx.RealBridges.Fascia, ctx.RealBridges.Lamps);
            ctx.InfraTunnelHeadwalls = InfraBuilder.BuildTunnelHeadwalls(ctx.City.Terrain, ctx.Tunnels.Mouths);
            if (ctx.Roads != null) ctx.InfraStreetFurniture = InfraBuilder.BuildStreetFurniture(ctx.City.Terrain, ctx.Roads.Roads);
        }

        // =====================================================================
        // Convenience: both phases back to back, exactly as citymap.js:911-927
        // kicks the scenery loop off as soon as `ready` flips (not awaited by
        // the caller — but a caller that DOES want it sequential, e.g. a
        // probe, uses this).
        // =====================================================================
        public static IEnumerator RunAll(Context ctx, Action<string> onProgress = null, Action<string> onStage = null)
        {
            foreach (var _ in Iterate(RunCritical(ctx, onProgress, onStage))) yield return null;
            foreach (var _ in Iterate(RunScenery(ctx, onStage))) yield return null;
        }

        static IEnumerable<object> Iterate(IEnumerator e) { while (e.MoveNext()) yield return e.Current; }
    }
}
