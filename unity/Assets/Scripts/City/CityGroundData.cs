using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE ONE SHARED CRITICAL-PATH BUILD the three new ground renderers
    /// (<see cref="RoadNetworkRenderer"/>, <see cref="RiverRenderer"/>,
    /// <see cref="GrassRenderer"/>) read, plus the refined-cell set
    /// Editor/BuildPlayableScene.cs must hand TerrainBuilder.Build.
    ///
    /// WHY IT EXISTS. Every other scenery renderer in City/ calls
    /// <c>CityData.Load()</c> for itself (MetroRenderer.cs:141,
    /// ParkRenderer.cs:163, VegetationRenderer.cs:199, StreetFurnitureRenderer
    /// .cs:164, ...) because each one needs only the geo header and the terrain
    /// sampler — cheap, independent, and nothing downstream of one renderer
    /// cares what another's sampler says. The ROAD/RIVER/GRASS layer is the
    /// first in this port where that is not true, for two reasons:
    ///
    ///  1. COST. The inputs are the whole of CityLoader.RunCritical's data
    ///     half — the merged road net, the carved river, the planned tunnel
    ///     cuts, 193k footprints for TunnelCuts.BuildingIndex. Running that
    ///     three times, once per renderer, would triple the single most
    ///     expensive non-building stage in the load.
    ///  2. CORRECTNESS, which is the real reason. `TerrainSampler.GroundCut`
    ///     is how a planned tunnel trench reaches every later height read
    ///     (CityLoader.cs:234-246's own note). It is hooked onto ONE sampler
    ///     instance. Three renderers with three private CityData.Load()s =
    ///     three samplers, of which at most one is cut, so the road deck, the
    ///     river sheet, the grass sheet and the cut-terrain patch would each
    ///     be draped on a DIFFERENT surface. That is precisely the
    ///     bilinear-vs-triangle class of defect the project record calls
    ///     "roads under grass" (docs: Roads via QGIS 2026-08), re-created
    ///     structurally instead of arithmetically.
    ///
    /// So this file owns one <see cref="CityLoader.Context"/>, built once per
    /// domain, in CityLoader.RunCritical's own stage order and with its own
    /// citymap.js line citations carried across. It is NOT a new pipeline: it
    /// calls the same already-ported, probe-verified builders, in the same
    /// order, and duplicates none of their logic.
    ///
    /// WHAT IT DELIBERATELY SKIPS from RunCritical, and why:
    ///   * "buildings" (BuildingBuilder.BuildRich) — City/CityRenderer.cs
    ///     already does exactly that in its own EnsureBuilt, and the result is
    ///     ~1.4M submesh instances no ground renderer reads. Footprints are
    ///     still loaded (buildings: true) because TunnelCuts.BuildingIndex
    ///     needs them.
    ///   * "terrainMesh" (TerrainBuilder.Build) — Editor/BuildPlayableScene.cs
    ///     bakes those tiles to .asset files with their colliders and
    ///     CombatTerrainTag; rebuilding them here would be a second, colliderless
    ///     copy of the ground.
    ///   * "spawn"/"ready" — scene-assembly state, not geometry.
    ///
    /// NOT A MonoBehaviour: this is a static data cache, same shape as
    /// CityMaterials.cs and GeneratedChildren.cs, so the project's
    /// one-MonoBehaviour-per-file rule does not apply to it.
    /// </summary>
    public static class CityGroundData
    {
        static CityLoader.Context _ctx;
        static bool _building;

        /// <summary>Milliseconds the one shared build took, for the renderers'
        /// own count lines (and so a load log says where the time went).</summary>
        public static long BuildMs { get; private set; }

        /// <summary>True once <see cref="Shared"/> has completed a build.</summary>
        public static bool Built => _ctx != null;

        /// <summary>
        /// The shared context, built on first call. Returns null only if
        /// CityData.Load itself fails (no StreamingAssets) — every individual
        /// stage is wrapped, so one bad stage leaves the rest usable, exactly
        /// like CityLoader.Stage's own try/catch.
        /// </summary>
        public static CityLoader.Context Shared()
        {
            if (_ctx != null) return _ctx;
            if (_building)
            {
                // Re-entrancy guard. Three renderers' OnEnable fire back to
                // back inside one scene load; none of them is re-entrant into
                // this, but a builder that threw mid-way must not be able to
                // restart the whole pipeline from inside itself.
                Debug.LogWarning("CITYGROUND re-entered while building — returning the partial context");
                return _ctx;
            }
            _building = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ctx = new CityLoader.Context();
            try
            {
                Build(ctx);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                _building = false;
                sw.Stop();
                BuildMs = sw.ElapsedMilliseconds;
            }
            _ctx = ctx;
            // BEFORE the first log line that could trigger an allocation, and
            // before any caller can touch a mesh — see the PINNING note below.
            PinAll(ctx);
            Debug.Log($"CITYGROUND built ms={BuildMs} " +
                      $"roads={(ctx.Roads?.Roads?.Count ?? 0)} bridges={(ctx.Roads?.Bridges?.Count ?? 0)} " +
                      $"tunnels={(ctx.Roads?.Tunnels?.Count ?? 0)} junctions={(ctx.Roads?.Junctions?.Count ?? 0)} " +
                      $"riverBanks={(ctx.River?.Banks?.Count ?? 0)} cuts={(ctx.TunnelPlan?.Cuts?.Count ?? 0)} " +
                      $"refinedCells={RefinedCells().Count} stageErrors={ctx.StageErrors.Count}");
            Debug.Log($"CITYGROUND stagems {StageMsLine(ctx)}");
            foreach (var kv in ctx.StageErrors)
                Debug.LogWarning($"CITYGROUND stage_error stage={kv.Key} msg={kv.Value}");
            return _ctx;
        }

        /// <summary>
        /// Drop the cache so the next <see cref="Shared"/> rebuilds. Called by
        /// nothing in a normal load (the context is immutable once built and a
        /// domain reload clears the static anyway); exists so a probe can force
        /// a second, independent build and compare.
        /// </summary>
        public static void Reset()
        {
            _ctx = null;
            _refined = null;
            _surfaces = null;
            _cutTerrain = null;
            _cutTerrainBuilt = false;
            BuildMs = 0;
            SurfacesMs = 0;
            StageMs.Clear();
        }

        /// <summary>Milliseconds <see cref="Surfaces"/> spent tiling the four
        /// road ribbons (ChunkedSurfaceBuilder.Split) — see StageMs.</summary>
        public static long SurfacesMs { get; private set; }

        static HashSet<int> _refined;

        /// <summary>
        /// The 60 m terrain cells a river bank or a tunnel cutting passes
        /// through, which must be rebuilt at 5 m instead
        /// (CutTerrainBuilder.RefinedCells; citymap.js:3916-3930's `refine`
        /// set). PUBLIC AND A ONE-LINER ON PURPOSE: this is the argument
        /// Editor/BuildPlayableScene.cs's <c>TerrainBuilder.Build(city.Terrain)</c>
        /// is missing, and the exclusion must be driven by the SAME set
        /// <see cref="GrassRenderer"/> rebuilds, or the ground either
        /// double-surfaces (bank cells drawn twice, z-fighting along the whole
        /// Mtkvari) or opens a hole.
        /// </summary>
        public static HashSet<int> RefinedCells()
        {
            if (_refined != null) return _refined;
            var ctx = Shared();
            _refined = CutTerrainBuilder.RefinedCells(ctx?.City?.Terrain, ctx?.River, ctx?.TunnelPlan)
                       ?? new HashSet<int>();
            return _refined;
        }

        // =====================================================================
        // PINNING — the defect that cost this pass its first real load, and it
        // is NOT a C# lifetime problem.
        //
        // Measured, 2026-10-30, in the middle of one load:
        //   Unloading 5057 unused Assets / (55.3 MB). Loaded Objects now: 30003.
        // and after it RiverSheet's 157,547-vertex sheet, every one of
        // GrassCover's 669 chunk meshes, the junction apron, both bridge-span
        // meshes, all four real-bridge meshes and all four tunnel meshes read
        // as null, while the four road-deck meshes (read a moment earlier)
        // were still alive. RIVER logged `sheet=0 sheetVerts=0` 8 ms after the
        // same build had logged `sheet verts=157547`.
        //
        // Unity's "unload unused assets" sweep decides what is USED by walking
        // the scene and the asset database, NOT by walking managed references.
        // A Mesh created with `new Mesh()` and held only by a static C# field
        // is, to that sweep, garbage — and allocating ~13,000 meshes for the
        // road tiles is exactly the memory pressure that triggers it. So a
        // shared data cache of runtime meshes CANNOT work without saying so,
        // and HideFlags.DontUnloadUnusedAsset is how you say it.
        //
        // DontSaveInEditor/DontSaveInBuild come along deliberately: everything
        // here is regenerated by a renderer's OnEnable on every load, so it
        // must never be written into Game.unity — the same rule
        // Editor/BuildPlayableScene.cs's strip step enforces from the other
        // side, and the reason that scene once reached 1.78 GB.
        // =====================================================================
        const HideFlags Pinned = HideFlags.DontUnloadUnusedAsset
                               | HideFlags.DontSaveInEditor
                               | HideFlags.DontSaveInBuild;

        /// <summary>Marks one runtime asset as owned by this cache. Safe on
        /// null and on an imported asset (an AssetDatabase-owned object is
        /// never unloaded out from under us anyway, and its flags are left
        /// alone).</summary>
        public static T Pin<T>(T o) where T : UnityEngine.Object
        {
            if (o == null) return o;
            if (ArtAssets.IsPersistent(o)) return o;
            o.hideFlags |= Pinned;
            return o;
        }

        static void PinAll(CityLoader.Context ctx)
        {
            if (ctx == null) return;
            Pin(ctx.RoadDeck.Asphalt); Pin(ctx.RoadDeck.Sidewalk);
            Pin(ctx.RoadDeck.Verge); Pin(ctx.RoadDeck.Markings);
            Pin(ctx.Junctions);
            Pin(ctx.Spans.Deck); Pin(ctx.Spans.Structure);
            Pin(ctx.RealBridges.Concrete); Pin(ctx.RealBridges.Stone);
            Pin(ctx.RealBridges.Rail); Pin(ctx.RealBridges.Stain);
            Pin(ctx.Tunnels.Concrete); Pin(ctx.Tunnels.Road);
            Pin(ctx.Tunnels.Tile); Pin(ctx.Tunnels.Line);
            // BOTH river meshes. The fill is a runtime Mesh with no asset
            // behind it exactly like the sheet, so leaving it out of the pin
            // list is the "sheet mesh is null although the carve produced
            // banks" defect this method exists to prevent, one mesh over.
            if (ctx.River != null) { Pin(ctx.River.Mesh); Pin(ctx.River.FillMesh); }
            if (ctx.Water != null) Pin(ctx.Water.Mesh);
            if (ctx.Grass != null) foreach (var c in ctx.Grass.Chunks) Pin(c.Mesh);
        }

        // =====================================================================
        // DERIVED GEOMETRY, cached here rather than in the renderers.
        //
        // Both of these are citymap.js's own steps, not rendering: the four
        // road ribbons go through `_addChunkedSurface` (citymap.js:6542-6596,
        // ported as ChunkedSurfaceBuilder — "PURE DATA/MESH, no GameObject per
        // tile is created here") and the refined cells go through
        // `_buildCutTerrain` (citymap.js:4007-4057).
        //
        // They live in the CACHE and not in RoadNetworkRenderer/GrassRenderer
        // for a lifetime reason the pinning note above explains the other half
        // of: if a renderer owned ~13,000 per-build tile meshes, its clear
        // would have to destroy them, and GeneratedChildren.Clear's disposal
        // cannot tell a mesh the renderer made from a mesh it borrowed out of
        // this cache — it would take the shared river sheet with it on the
        // first rebuild. Owning everything here means every renderer can clear
        // with disposeRuntimeAssets: false (MetroRenderer's documented case:
        // "a STATIC cache shares the objects across builds"), nothing leaks
        // per rebuild, and a rebuild is nearly free.
        // =====================================================================

        public sealed class RoadSurfaces
        {
            public List<ChunkedSurfaceBuilder.SurfaceTile> Asphalt = new List<ChunkedSurfaceBuilder.SurfaceTile>();
            public List<ChunkedSurfaceBuilder.SurfaceTile> Sidewalk = new List<ChunkedSurfaceBuilder.SurfaceTile>();
            public List<ChunkedSurfaceBuilder.SurfaceTile> Verge = new List<ChunkedSurfaceBuilder.SurfaceTile>();
            public List<ChunkedSurfaceBuilder.SurfaceTile> Markings = new List<ChunkedSurfaceBuilder.SurfaceTile>();
            public int Total => Asphalt.Count + Sidewalk.Count + Verge.Count + Markings.Count;
        }

        static RoadSurfaces _surfaces;

        /// <summary>
        /// The four tiled road surfaces. THE WORK NOW HAPPENS IN THE "roads"
        /// BUILD STAGE (see Build's own note): each whole-city ribbon is tiled
        /// the moment RoadBuilder hands it over and released before the next
        /// one is built, so the four 19.5 M-vertex ribbons are never alive
        /// together. This accessor is therefore a lookup — it only has to make
        /// sure the shared build has run.
        ///
        /// citymap.js:6808/6828/6849/6865 are the four `_addChunkedSurface`
        /// call sites; only :6808 (asphalt) is collidable+ground.
        /// </summary>
        public static RoadSurfaces Surfaces()
        {
            if (_surfaces != null) return _surfaces;
            Shared();                       // fills _surfaces in its "roads" stage
            return _surfaces ??= new RoadSurfaces();
        }

        /// <summary>Triangles in the whole-city asphalt ribbon, captured in the
        /// "roads" stage before the ribbon mesh is released. The ROADS count
        /// line's `asphaltTris`.</summary>
        public static int AsphaltTris { get; private set; }

        /// <summary>
        /// One whole-city ribbon -> its 1500 m culling tiles. The ribbon is
        /// DESTROYED inside the split (ChunkedSurfaceBuilder's `releaseWhole`),
        /// as soon as its arrays have been read and before the tiles are
        /// allocated: 666.7 MB of mesh that nothing draws, collides with or
        /// reads once <see cref="AsphaltTris"/> is captured
        /// (`grep -rn RoadDeck` over Assets finds no other consumer). It never
        /// went away on its own because PinAll flags it
        /// HideFlags.DontUnloadUnusedAsset — a pin has to be paired with a
        /// release; Resources.UnloadUnusedAssets was measured leaving
        /// meshes=15,744 untouched.
        /// </summary>
        static List<ChunkedSurfaceBuilder.SurfaceTile> Tile(Mesh whole, bool collidable)
        {
            var tiles = new List<ChunkedSurfaceBuilder.SurfaceTile>();
            if (whole == null || whole.vertexCount == 0) return tiles;
            // An imported asset is never this cache's to destroy (Pin's own
            // rule); a ribbon is always a runtime mesh, so this is a guard and
            // not a case.
            bool release = !ArtAssets.IsPersistent(whole);
            tiles = ChunkedSurfaceBuilder.Split(whole, ChunkedSurfaceBuilder.DefaultTileSize,
                                                collidable, collidable, release);
            foreach (var t in tiles)
            {
                Pin(t.Mesh);
                // A tile that carries no collider and that nothing reads back
                // does not need its CPU-side vertex copy once it is uploaded:
                // sidewalk + verge + markings are 8,548 of the 13,339 tiles
                // and 14.85 M of the 19.52 M vertices. The asphalt is left
                // readable here and frozen by RoadNetworkRenderer AFTER its
                // MeshCollider has cooked.
                if (!collidable && t.Mesh != null) t.Mesh.UploadMeshData(true);
            }
            return tiles;
        }

        static Mesh _cutTerrain;
        static bool _cutTerrainBuilt;

        /// <summary>citymap.js:4007-4057 `_buildCutTerrain` — the
        /// <see cref="RefinedCells"/> rebuilt at 5 m. Null when there is
        /// nothing to build (the JS's own early return).</summary>
        public static Mesh CutTerrainMesh()
        {
            if (_cutTerrainBuilt) return _cutTerrain;
            _cutTerrainBuilt = true;
            var ctx = Shared();
            var cells = RefinedCells();
            if (ctx?.City?.Terrain == null || cells.Count == 0) return null;
            var colors = TerrainBuilder.ComputeVertexColors(ctx.City.Terrain);
            FixDryBankTint(ctx.City.Terrain, ctx.River, cells, colors);
            _cutTerrain = Pin(CutTerrainBuilder.Build(ctx.City.Terrain, cells,
                colors, HeightAt(ctx)));
            return _cutTerrain;
        }

        /// <summary>
        /// docs/open_items.json `river-bank-blue-wedge`. ComputeVertexColors
        /// paints a vertex from the single ground-class SAMPLE at that grid
        /// point (citymap.js:3896-3899: `cl === 2 -&gt; water`), and
        /// CutTerrainBuilder.Build spreads that ONE colour across the whole
        /// 60 m bank cell it refines (citymap.js:4020-4022's "reuse the
        /// parent cell's tint" — ported faithfully, not a port deviation).
        /// A bank cell is, by RefinedCells' own definition, one whose 4
        /// corners DISAGREE on being inside RiverSheet's fine 12 m carve
        /// mask; when the one grid vertex ComputeVertexColors sampled reads
        /// class==2 (water) but the cell's own CENTRE sits outside that mask
        /// — the quay-side sliver neither river_sheet nor river_fill ever
        /// draws water over — the uniform tint shows as bare, blue-tinted
        /// ground. Measured: river_fill alone cut the top-down frame's
        /// class-2-blue pixels from 161,251 to 8,044; RBCUTOFF attributed
        /// 7,776 of those 8,044 to cut_terrain specifically (8,044 with it
        /// enabled, 268 without). Recolour exactly those vertices with the
        /// same dry/high elevation gradient ComputeVertexColors itself uses
        /// for a non-water sample, so a genuinely dry bank cell stops
        /// reading as water while every cell RiverSheet actually covers is
        /// untouched — this never changes a wet cell's tint, only a dry
        /// one's. `colors` is this call's own fresh array (CutTerrainMesh's
        /// only reader), so mutating it in place is safe.
        /// </summary>
        static void FixDryBankTint(TerrainSampler t, RiverSheet river, HashSet<int> cells, Color[] colors)
        {
            if (river == null || t?.Classes == null) return;
            float minE = float.MaxValue, maxE = float.MinValue;
            for (int i = 0; i < t.Heights.Length; i++)
            {
                float y = t.Heights[i];
                if (y < minE) minE = y;
                if (y > maxE) maxE = y;
            }
            float span = maxE - minE + 1f;
            Color dry = GroundLin(TerrainBuilder.DryHex), high = GroundLin(TerrainBuilder.HighHex);

            foreach (var cell in cells)
            {
                if (cell < 0 || cell >= t.Classes.Length || t.Classes[cell] != 2) continue;
                int r = cell / t.W, c = cell - r * t.W;
                // THE SAME POINT the colour sample itself came from —
                // ComputeVertexColors reads Classes[cell]/Heights[cell] at
                // this grid vertex (r,c)'s own corner, not the cell centre.
                // Testing InRiverMask at the centre instead (first attempt)
                // left the RBCUTOFF pale-blue count UNCHANGED (8,044 -> 8,067,
                // noise) because a straddling bank cell's centre often still
                // reads "in mask" even when its corner — the coarse 60 m
                // class raster's own sample point, "water if the river
                // merely touches it" per citymap.js's own comment — does not
                // match the fine 12 m mask there. Matching the sample point
                // exactly is what makes this comparison meaningful.
                float gx = t.MinX + c * t.Step;
                float gz = t.MinZ + r * t.Step;
                if (river.InRiverMask(gx, gz)) continue; // genuinely wet -- leave the water tint

                float k = (t.Heights[cell] - minE) / span;
                Color baseCol = new Color(
                    dry.r + (high.r - dry.r) * k,
                    dry.g + (high.g - dry.g) * k,
                    dry.b + (high.b - dry.b) * k);
                // citymap.js:3905's per-vertex hash tint, same double-precision
                // argument rule as TerrainBuilder.ComputeVertexColors (JsMath
                // doc on sin()*43758.5453 fract hashes: see unity SKILL.md).
                double hv = System.Math.Sin((double)c * 12.9898 + (double)r * 78.233) * 43758.5453;
                float n = 0.92f + 0.08f * (float)(hv - System.Math.Floor(hv));
                colors[cell] = new Color(baseCol.r * n, baseCol.g * n, baseCol.b * n);
            }
        }

        static Color GroundLin(string hex)
        {
            var c = WallMaterials.HexToLinear255(hex);
            return new Color(c[0] / 255f, c[1] / 255f, c[2] / 255f);
        }

        /// <summary>The height function every ground mesh in this layer must
        /// be draped with — RiverSheet.HeightAt when a river exists, else the
        /// terrain's own, matching CutTerrainBuilder.Build's documented
        /// contract and citymap.js's single `this.heightAt(x, z)`.</summary>
        public static Func<float, float, float> HeightAt(CityLoader.Context ctx)
        {
            if (ctx?.City?.Terrain == null) return null;
            var terrain = ctx.City.Terrain;
            var river = ctx.River;
            if (river == null) return terrain.HeightAtGame;
            return (x, z) => river.HeightAt(x, z);
        }

        // =====================================================================
        // The build — CityLoader.RunCritical's stage order, minus the three
        // stages listed in the class doc. Stage names and citymap.js citations
        // are carried across verbatim so the two orders can be diffed by eye.
        // =====================================================================
        static void Build(CityLoader.Context ctx)
        {
            Stage(ctx, "cityData", () =>
            {
                // buildings: true — NOT for geometry (CityRenderer owns that),
                // but because TunnelCuts.BuildingIndex.Build takes the raw
                // footprint list and TunnelBuilder.Build needs that index to
                // decide bore-vs-cut-and-cover.
                ctx.City = CityData.Load(buildings: true); // citymap.js:480-494
            });
            if (ctx.City == null) return;

            Stage(ctx, "roadNet", () =>
            {
                var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
                RoadNet net = null;
                try { net = RoadBuilder.LoadRoadNet(); }
                catch { /* no roadnet.json.gz — legacy lists stand, citymap.js:539-541 */ }
                ctx.RoadNet = net;
                ctx.Roads = net != null
                    ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                    : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };
            });

            Stage(ctx, "river", () =>
            {
                // citymap.js:583-724 + :880 — one combined call in this port,
                // see CityLoader's "river" stage for why.
                ctx.River = RiverSheet.Build(ctx.City?.Terrain);
            });

            Stage(ctx, "tunnelGround", () =>
            {
                ctx.TunnelGroundOverride = TunnelCuts.GroundOverride.Load(); // citymap.js:569-582
            });

            Stage(ctx, "tunnelCuts", () =>
            {
                ctx.BuildingIndex = TunnelCuts.BuildingIndex.Build(ctx.City.Buildings); // citymap.js:725
                ctx.TunnelPlan = TunnelCuts.Build(
                    ctx.Roads?.Tunnels ?? new List<RoadEntry>(),
                    ctx.Roads?.Roads ?? new List<RoadEntry>(),
                    ctx.City.Terrain.HeightAtGame, ctx.BuildingIndex, ctx.TunnelGroundOverride); // citymap.js:726

                // AFTER Build, never before — Build() is handed the UNCUT
                // sampler as its own input, so hooking first would feed cut
                // heights back into the planner that produces the cuts.
                // citymap.js sets `_cutFlag` at :3645, after planning, and
                // heightAt only consults the cuts once it is on (:3226).
                // Verbatim from CityLoader.cs:234-246.
                if (ctx.TunnelPlan != null && ctx.TunnelPlan.HasCuts)
                {
                    var cuts = ctx.TunnelPlan;
                    ctx.City.Terrain.GroundCut = (x, z, y) => cuts.CutAt(x, z, y);
                }
            });

            Stage(ctx, "roads", () =>
            {
                if (ctx.Roads == null) return;
                // citymap.js:871 + :6808/6828/6849/6865 in ONE pass. The four
                // _addChunkedSurface call sites used to run in a separate
                // Surfaces() later, which meant the four whole-city ribbons
                // stayed alive in between: +1.03 GB of Unity's reserved
                // high-water here and +0.76 GB again there, measured per stage.
                // Streaming them (RoadBuilder's `sink`, see its own note) tiles
                // each ribbon the moment it exists and lets it go before the
                // next is built, so the peak holds one ribbon, not four.
                var s = new RoadSurfaces();
                long freed = 0;
                var swTile = new System.Diagnostics.Stopwatch();
                ctx.RoadDeck = RoadBuilder.BuildRoadDeck(ctx.City.Terrain, ctx.Roads.Roads, ctx.Roads.Meta,
                    (layer, mesh) =>
                    {
                        if (mesh == null) return;
                        bool asphalt = layer == "asphalt";
                        // THE COUNT LINE'S NUMBER, taken before the ribbon goes
                        // (RoadNetworkRenderer's ROADS asphaltTris=2318682).
                        if (asphalt) AsphaltTris = (int)(mesh.GetIndexCount(0) / 3);
                        try { freed += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(mesh); } catch { }
                        swTile.Start();
                        // citymap.js:6808 is the only collidable+ground one.
                        var tiles = Tile(mesh, asphalt);
                        swTile.Stop();
                        switch (layer)
                        {
                            case "asphalt": s.Asphalt = tiles; break;
                            case "sidewalk": s.Sidewalk = tiles; break;
                            case "verge": s.Verge = tiles; break;
                            case "markings": s.Markings = tiles; break;
                        }
                    });
                _surfaces = s;
                SurfacesMs = swTile.ElapsedMilliseconds;
                Debug.Log($"CITYGROUND ribbons_released_mb={freed / (1024.0 * 1024.0):F1}");
                Debug.Log($"CITYGROUND surfaces asphalt={s.Asphalt.Count} sidewalk={s.Sidewalk.Count} " +
                          $"verge={s.Verge.Count} markings={s.Markings.Count} ms={SurfacesMs}");
                // The parity proof for the packed tile layout: the worst
                // angular error the SNorm8 normals introduce over every road
                // vertex in the city. A frame's mean RGB cannot isolate this
                // while other agents are rebuilding the river in the same
                // tree; this number can.
                Debug.Log($"CITYGROUND tile_layout packed=true " +
                          $"verts={ChunkedSurfaceBuilder.PackedVerts} " +
                          $"maxNormalErrDeg={ChunkedSurfaceBuilder.MaxNormalErrDeg:F4}");
                if (Environment.GetEnvironmentVariable("TF_MEMCENSUS") == "1")
                    TbilisiFly.Tools.MemoryCensus.Totals("stage_surfaces");
            });

            Stage(ctx, "junctions", () =>
            {
                if (ctx.Roads != null)
                    ctx.Junctions = RoadBuilder.BuildJunctions(ctx.City.Terrain, ctx.Roads.Junctions); // citymap.js:873
            });

            Stage(ctx, "bridgeSpans", () =>
            {
                if (ctx.Roads == null) return;
                ctx.RealBridgeZones = BridgeBuilder.RealBridgeZones(ctx.City); // citymap.js:874
                ctx.Spans = BridgeBuilder.BuildSpans(ctx.City.Terrain, ctx.Roads.Bridges,
                    ctx.Roads.BridgeMeta, ctx.Roads.Roads, ctx.RealBridgeZones); // citymap.js:874
            });

            Stage(ctx, "realBridges", () =>
            {
                ctx.RealBridges = BridgeBuilder.BuildRealBridges(ctx.City); // citymap.js:875
            });

            Stage(ctx, "tunnels", () =>
            {
                if (ctx.BuildingIndex != null)
                    ctx.Tunnels = TunnelBuilder.Build(ctx.TunnelPlan, ctx.City.Terrain, ctx.BuildingIndex); // citymap.js:876
            });

            Stage(ctx, "water", () =>
            {
                ctx.Water = WaterBodies.Build(ctx.City.Terrain); // citymap.js:878
            });

            Stage(ctx, "grass", () =>
            {
                // CityLoader's own "grass" stage passes (Terrain, River) only.
                // The cut plan IS passed here, because citymap.js:8321 reuses
                // the SAME refine set the terrain mesh used
                // (`const refine = this._cutFlag ? this._refinedCells(g) : null`)
                // and GrassCover.Build's own doc asks for exactly this
                // argument. Without it the grass sheet keeps 60 m quads over a
                // tunnel trench the terrain beneath it has already dropped.
                ctx.Grass = GrassCover.Build(ctx.City.Terrain, ctx.River, ctx.TunnelPlan);
            });

            Stage(ctx, "infrastructure", () =>
            {
                // CityLoader.BuildInfrastructure (CityLoader.cs:587-595), which
                // is private — these are its four lines, in its order.
                var rails = ctx.RealBridges.Rails ?? new List<BridgeRail>();
                ctx.InfraEmbankment = InfraBuilder.BuildRiverEmbankment(ctx.City.Terrain, ctx.River?.Banks, rails);
                ctx.InfraBridgeFurniture = InfraBuilder.BuildBridgeFurniture(rails, ctx.RealBridges.Fascia, ctx.RealBridges.Lamps);
                ctx.InfraTunnelHeadwalls = InfraBuilder.BuildTunnelHeadwalls(ctx.City.Terrain, ctx.Tunnels.Mouths);
                if (ctx.Roads != null)
                    ctx.InfraStreetFurniture = InfraBuilder.BuildStreetFurniture(ctx.City.Terrain, ctx.Roads.Roads);
            });

            // LAST, because the groups fill across the stages above (the river
            // in "river", the cut terrain and the grass in "grass", the spans
            // in "bridgeSpans"). One MESHPACK line per group: the vertices it
            // packed, the worst angular error the SNorm8 normals introduced,
            // and what that saved. See City/MeshPack.cs.
            MeshPack.Log();
        }

        /// <summary>CityLoader.Stage's own shape (CityLoader.cs:156-166): record
        /// the order, catch and record per stage so one failure cannot cost the
        /// rest of the layer.</summary>
        static void Stage(CityLoader.Context ctx, string name, Action run)
        {
            ctx.StageOrder.Add(name);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { run(); }
            catch (Exception e)
            {
                ctx.StageErrors[name] = e.Message;
                Debug.LogException(e);
            }
            sw.Stop();
            StageMs[name] = sw.ElapsedMilliseconds;
            // One allocator line per stage, gated so a normal run never pays
            // for it. native_reserved is what sets the process footprint (see
            // Tools/MemoryCensus.Totals) and this is the only way to see which
            // stage raises it.
            if (Environment.GetEnvironmentVariable("TF_MEMCENSUS") == "1")
                TbilisiFly.Tools.MemoryCensus.Totals("stage_" + name);
        }

        /// <summary>Per-stage milliseconds of the one shared build, in build
        /// order — docs/open_items.json `ground-load-time` asks for "ms per
        /// stage", and the whole-layer `ms=` number cannot say which of the
        /// thirteen stages owns the 5.9 s.</summary>
        public static readonly System.Collections.Generic.Dictionary<string, long> StageMs =
            new System.Collections.Generic.Dictionary<string, long>();

        /// <summary>`stage=ms` pairs in build order, for one log line.</summary>
        public static string StageMsLine(CityLoader.Context ctx)
        {
            if (ctx == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var s in ctx.StageOrder)
            {
                StageMs.TryGetValue(s, out long ms);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s).Append('=').Append(ms);
            }
            return sb.ToString();
        }
    }
}
