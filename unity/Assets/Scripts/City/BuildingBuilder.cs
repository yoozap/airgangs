using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Extrudes the 193,146 OSM footprints into chunked meshes.
    ///
    /// Ported from the massing part of src/citymap.js _buildBuildings. Chunked
    /// per 1 km cell exactly as the JS does (citymap.js:253 CHUNK = 1000) so the
    /// chunk identity carries over — per-building HP, the 10 crumble stages and
    /// the carved blast holes all index into chunk vertex ranges, so the chunking
    /// is part of the data model, not just a culling trick.
    ///
    /// EXTRUDE OVER THE WHOLE FOOTPRINT, NOT THE CENTROID. The base of every
    /// wall is pinned to the LOWEST ground height under the ring, and the ring
    /// is skirted down to the terrain. Using the centroid height was the
    /// "buildings under grass" bug: on any slope the uphill corner floats and
    /// the downhill corner buries.
    ///
    /// REAL OSM FACTS, not guesses (docs/QUALITY_BAR.md). BuildingFacts supplies,
    /// for 17,425 buildings: the storey count (which BEATS the GHSL raster height —
    /// citymap.js:4606, h = levels * 3.1 + 0.9), the roof:shape (13 kinds, so a
    /// gabled roof is gabled instead of a flat fan), building:material (11 kinds,
    /// each its own submesh so they can wear different materials) and
    /// building:colour. A uniform grey box city is a REGRESSION even when every
    /// coordinate is right.
    ///
    /// HANDEDNESS: footprint vertices arrive in the game frame (X=west) and are
    /// mirrored here, in the same pass that reverses winding.
    /// </summary>
    public static class BuildingBuilder
    {
        // ---- landmark exclusion rings (src/landmarks.js:9-42 EXCLUSIONS,
        // consumed at citymap.js:4601) -------------------------------------
        // Real landmark/metro/mall/airport models replace the OSM extrusion
        // at these 31 spots. Before this fix BuildingBuilder had NO exclusion
        // check at all — LandmarkPlacer.cs's own header left this exact TODO
        // ("Not consumed here — just the data, for whoever extends
        // BuildingBuilder with the exclusion"). The result was a generic
        // destructible grey box generated on top of every landmark footprint
        // (TV tower, Sameba, Parliament, Freedom Monument, ...): visible
        // double geometry, AND a real gameplay defect — a player could
        // already "destroy" what should be an immune landmark, because the
        // thing actually carrying HP there was this proxy building, not the
        // landmark model (which has no HP pathway of its own yet; see
        // Core/CombatWorldAdapter.cs's DamageHit, "statue — nothing with HP
        // here"). Skipping the footprint here is the half of the fix that
        // belongs in City/*; see LandmarkTag.cs for the other half.
        static readonly (float gx, float gz, float r2)[] ExclGame = BuildExclGame();

        static (float gx, float gz, float r2)[] BuildExclGame()
        {
            var ex = LandmarkPlacer.Exclusions;
            var result = new (float, float, float)[ex.Length];
            for (int i = 0; i < ex.Length; i++)
            {
                // GeoFrame.FromLatLon returns UNITY frame (X=east); this file's
                // footprint math (f.Flat) is still in GAME frame (X=west) at this
                // point in the pipeline — negate X back, same as GeoFrame's own
                // doc comment describes for the inverse direction.
                var u = GeoFrame.FromLatLon(ex[i].Lat, ex[i].Lon);
                result[i] = (-u.x, u.z, ex[i].R * ex[i].R);
            }
            return result;
        }

        // =====================================================================
        // ---- RING CENTROID: double, and recovered from the DECIMETRES --------
        //
        // citymap.js:4593-4600 is the footprint centroid every downstream
        // decision keys off — the OSM facts match (BuildingFacts.At, hard 9 m
        // threshold), the kit claim, the wall bucket, the photo-facade lookup:
        //
        //     let cx = 0, cz = 0;
        //     for (let i = 0; i < n; i++) { cx += b[1+i*2] / 10; cz += b[2+i*2] / 10; }
        //     cx /= n; cz /= n;
        //
        // `b[...]` are DECIMETRE integers and every term of that sum is a
        // DOUBLE. This file summed `f.Flat[]` (already-narrowed float32 metres)
        // into a FLOAT accumulator, which is the same bug twice:
        //
        //   1. the accumulator. Coordinates reach |x| ~264 km in this shared
        //      frame (six city bboxes), where float32 resolves 1.6 cm. Summing
        //      a 100-vertex ring of such terms drifts up to the better part of
        //      a metre — the RiverSheet shoelace lesson exactly (244,112 terms,
        //      368 m2 drift), and a 1 m centroid error against a 9 m match
        //      radius silently moves buildings off their own OSM record.
        //   2. the source. Data/CityData.ReadFootprints stores `dm / 10f`, so
        //      each coordinate had already lost up to 1.6 cm before the sum —
        //      "precision has to be carried from the SOURCE, not restored at
        //      the point of comparison". The decimetre value itself IS exact in
        //      float32 (integers below 2^24 are), so RingM recovers it: the
        //      stored float is within 0.0156 m = 0.156 dm of the integer, far
        //      inside the 0.5 dm a round needs, and `dm / 10.0` is then the
        //      exact double the JS compares.
        //
        // MEASURED EFFECT ON TBILISI: NONE, and that is worth writing down so
        // the next reader does not re-derive it. BuildingQualityProbe prints the
        // same withFacts=17545 / coloured=37 / claims=194869 before and after
        // this change, because Tbilisi's own coordinates only reach |x| ~18 km,
        // where float32 still resolves 2 mm — a ten-vertex ring drifts well
        // under a millimetre and never flips a 9 m threshold. The float32
        // argument only bites at the far edge of the SHARED frame (batumi/
        // kutaisi/gori/rustavi/kazbegi reach ~264 km, where the resolution is
        // 1.6 cm against this project's 2 cm standard) — those cities are not
        // playable today but their data deliberately stays, so the chain is
        // kept double here rather than left as a trap for whoever turns them on.
        // The 2026-10-27 drift this was first suspected of is NOT precision:
        // it is the landmark-exclusion skip above finally being implemented
        // (143 footprints, the same 143 BuildPlayableScene.cs's LandmarkRenderer
        // note cites) while tools/_probe_buildingquality.py still models no
        // exclusions at all. The C# is the correct side there.
        // =====================================================================
        static double RingM(float storedMetres)
            => System.Math.Round((double)storedMetres * 10.0) / 10.0;

        /// <summary>citymap.js:4601 — true if a GAME-frame point falls inside any
        /// landmark exclusion circle.</summary>
        static bool InLandmarkExclusion(float gameX, float gameZ)
        {
            for (int i = 0; i < ExclGame.Length; i++)
            {
                float dx = gameX - ExclGame[i].gx, dz = gameZ - ExclGame[i].gz;
                if (dx * dx + dz * dz < ExclGame[i].r2) return true;
            }
            return false;
        }

        public const float ChunkSize = 1000f; // m — matches citymap.js CHUNK

        public class Chunk
        {
            public int Cx, Cz;
            public Mesh Mesh;
            /// <summary>Per-building vertex range, so damage can address one building.</summary>
            public List<(int start, int count)> Ranges = new List<(int, int)>();
            public List<float> Heights = new List<float>();
            /// <summary>Material index per building, indexing BuildingFacts.Materials.</summary>
            public List<int> MaterialIdx = new List<int>();
            public List<string> RoofShape = new List<string>();

            // ---- hole-carving / window-query inputs, parallel to Ranges -----------
            // Core/CombatWorldAdapter.cs's PunchHole/BlastWindows/StripPropsAt need a
            // way from a RaycastHit back to the ORIGINAL CityData.Buildings index and
            // the raw footprint ring that was actually extruded (Combat/BreakWall.cs's
            // BuildShell wants that ring directly — see its own class header on why it
            // cannot read a shared-ring mesh snapshot the way breakwall.js does).
            // Everything here is a PLAIN array-backed list (ints/floats), so it bakes
            // onto Core/CombatWorldAdapter.cs's CombatBuildingChunk as flat
            // int[]/float[] fields and survives a scene save exactly like
            // Ranges/Heights already do.
            /// <summary>The ORIGINAL CityData.Buildings index this entry came from
            /// (BuildRich's `e.Bi` / Build's own loop index `bi` — a part-expanded
            /// building contributes several entries sharing one value here).</summary>
            public List<int> BuildingIndex = new List<int>();
            /// <summary>World Y the wall quads actually start/end at for this entry —
            /// NOT reconstructable from Heights alone (BuildRich's boxBase/topY use a
            /// slope-aware formula, not simply baseY+height), so captured directly at
            /// the point the wall quads themselves are emitted.</summary>
            public List<float> WallBase = new List<float>();
            public List<float> WallTop = new List<float>();
            /// <summary>Single representative wall tint for Combat/BreakWall.cs's shell
            /// (which paints one colour per cell, not a top/bottom gradient) — the
            /// brighter of the two wall colours for a gradient-tinted building, or the
            /// photo-facade's white placeholder for a photo building.</summary>
            public List<Color32> ShellTint = new List<Color32>();
            /// <summary>Which wall submesh (index into this chunk's Mesh.subMeshCount /
            /// SubmeshNames, and so into the GameObject's Renderer.sharedMaterials) this
            /// entry's wall quads were written to — so a carved shell can borrow the
            /// SAME real material instead of a flat placeholder. -1 for a photo-facade
            /// building (its "wall material" is the per-photo submesh, not a
            /// WallMaterials bucket) or for a plain Build() chunk that never buckets.</summary>
            public List<int> ShellWallBucket = new List<int>();
            /// <summary>Concatenated x,z pairs (GAME frame, metres — same convention as
            /// CityData.Footprint.Flat/BuildingWallInput.FootprintFlatGameFrame) for
            /// EVERY entry's footprint ring, back to back. Index into it via
            /// FootprintOffset/FootprintCount below (point units, not float units).</summary>
            public List<float> FootprintFlat = new List<float>();
            public List<int> FootprintOffset = new List<int>();
            public List<int> FootprintCount = new List<int>();

            /// <summary>Appends one entry's bookkeeping in lockstep with a
            /// `Ranges.Add(...)` call — keeps every parallel list the same length as
            /// Ranges without repeating this at each of the two call sites.</summary>
            public void AddBuildingFacts(int buildingIndex, float wallBase, float wallTop,
                Color32 shellTint, int shellWallBucket, float[] footprintGameFrame, int vertexCount)
            {
                BuildingIndex.Add(buildingIndex);
                WallBase.Add(wallBase);
                WallTop.Add(wallTop);
                ShellTint.Add(shellTint);
                ShellWallBucket.Add(shellWallBucket);
                int offset = FootprintFlat.Count / 2;
                for (int v = 0; v < vertexCount; v++)
                {
                    FootprintFlat.Add(footprintGameFrame[v * 2]);
                    FootprintFlat.Add(footprintGameFrame[v * 2 + 1]);
                }
                FootprintOffset.Add(offset);
                FootprintCount.Add(vertexCount);
            }

            // ---- additive: populated ONLY by BuildRich (WallMaterials/BuildingKit
            // /FacadeRelief quality pass), parallel to Ranges. Build() (above)
            // leaves these empty, exactly as it always has. ----
            /// <summary>Per-building wall-material submesh bucket (WallMaterials).</summary>
            public List<int> WallBucket = new List<int>();
            /// <summary>Per-building roof-material submesh bucket (WallMaterials).</summary>
            public List<int> RoofBucketIdx = new List<int>();
            /// <summary>One label per Mesh submesh, in submesh order — e.g.
            /// "wall:style0", "wall:brick_red", "wall:loggia:b171611",
            /// "roof:roof_metal" — for a later material-wiring pass to resolve
            /// against the imported Assets/Resources/Art/Textures files.</summary>
            public List<string> SubmeshNames = new List<string>();
        }

        public static List<Chunk> Build(CityData city, int maxBuildings = int.MaxValue,
                                        BuildingFacts facts = null)
        {
            facts ??= BuildingFacts.Load();
            int nFacts = 0, nPitched = 0, nColoured = 0, nRealHeight = 0;
            var byCell = new Dictionary<long, List<int>>();
            int n = Mathf.Min(city.Buildings.Count, maxBuildings);

            for (int i = 0; i < n; i++)
            {
                var f = city.Buildings[i];
                // cell from the first vertex; a footprint is far smaller than 1 km
                int cx = Mathf.FloorToInt(f.Flat[0] / ChunkSize);
                int cz = Mathf.FloorToInt(f.Flat[1] / ChunkSize);
                long key = (long)cx * 100003 + cz;
                if (!byCell.TryGetValue(key, out var list))
                    byCell[key] = list = new List<int>();
                list.Add(i);
            }

            var chunks = new List<Chunk>(byCell.Count);
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var cols  = new List<Color32>();
            var tris  = new List<int>();

            foreach (var kv in byCell)
            {
                verts.Clear(); norms.Clear(); cols.Clear(); tris.Clear();
                var chunk = new Chunk();

                foreach (int bi in kv.Value)
                {
                    var f = city.Buildings[bi];
                    int vc = f.VertexCount;
                    if (vc < 3) continue;

                    int start = verts.Count;

                    // centroid in the GAME frame, for the OSM facts lookup.
                    // DOUBLE accumulator — see Ring Centroid note below.
                    double dcx = 0.0, dcz = 0.0;
                    for (int v = 0; v < vc; v++) { dcx += RingM(f.Flat[v * 2]); dcz += RingM(f.Flat[v * 2 + 1]); }
                    dcx /= vc; dcz /= vc;
                    float gcx = (float)dcx, gcz = (float)dcz;
                    // citymap.js:4601 — a real landmark model stands here instead
                    if (InLandmarkExclusion(gcx, gcz)) continue;
                    var fact = facts.At(dcx, dcz);
                    if (fact.HasValue) nFacts++;

                    // REAL storeys beat the raster height (citymap.js:4606)
                    float height = f.Height;
                    if (fact.HasValue && fact.Value.HeightOrZero > 0f)
                    {
                        height = fact.Value.HeightOrZero;
                        nRealHeight++;
                    }

                    bool pitched = fact.HasValue && facts.IsPitched(fact.Value, vc);
                    if (pitched) nPitched++;

                    Color32 tint = new Color32(190, 185, 178, 255);
                    if (fact.HasValue && fact.Value.HasColour) { tint = fact.Value.Colour; nColoured++; }

                    // lowest ground under the whole ring — never the centroid
                    float baseY = float.MaxValue;
                    for (int v = 0; v < vc; v++)
                    {
                        float gy = city.Terrain.HeightAtGame(f.Flat[v * 2], f.Flat[v * 2 + 1]);
                        if (gy < baseY) baseY = gy;
                    }
                    float topY = baseY + Mathf.Max(2f, height);

                    // ---- walls: a quad per edge, skirted to the ground ----
                    for (int v = 0; v < vc; v++)
                    {
                        int w = (v + 1) % vc;
                        float ax = -f.Flat[v * 2], az = f.Flat[v * 2 + 1]; // mirror X
                        float bx = -f.Flat[w * 2], bz = f.Flat[w * 2 + 1];

                        var p0 = new Vector3(ax, baseY - 6f, az); // skirt below grade
                        var p1 = new Vector3(bx, baseY - 6f, bz);
                        var p2 = new Vector3(bx, topY, bz);
                        var p3 = new Vector3(ax, topY, az);

                        var nrm = Vector3.Cross(p1 - p0, p3 - p0).normalized;
                        int b = verts.Count;
                        verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                        norms.Add(nrm); norms.Add(nrm); norms.Add(nrm); norms.Add(nrm);
                        cols.Add(tint); cols.Add(tint); cols.Add(tint); cols.Add(tint);
                        tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                        tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                    }

                    // ---- roof ----
                    // A fan off the ring centroid. When OSM maps a pitched
                    // roof:shape the apex is RAISED, which is what turns a flat
                    // grey lid into a gabled/hipped/pyramidal roof. Pitch height
                    // scales with the footprint so a big hall does not get a
                    // needle; the JS caps pitching at 16 corners for the same
                    // reason (citymap.js:4820).
                    float ccx = 0f, ccz = 0f;
                    for (int v = 0; v < vc; v++) { ccx += -f.Flat[v * 2]; ccz += f.Flat[v * 2 + 1]; }
                    ccx /= vc; ccz /= vc;

                    float apex = topY;
                    if (pitched)
                    {
                        // mean radius of the ring -> a believable pitch
                        float r = 0f;
                        for (int v = 0; v < vc; v++)
                        {
                            float dx = -f.Flat[v * 2] - ccx, dz = f.Flat[v * 2 + 1] - ccz;
                            r += Mathf.Sqrt(dx * dx + dz * dz);
                        }
                        r /= vc;
                        string shape = facts.ShapeOf(fact.Value);
                        float k = shape switch
                        {
                            "pyramidal" => 0.9f,
                            "dome" or "onion" or "round" => 0.8f,
                            "gabled" or "half-hipped" => 0.55f,
                            "hipped" => 0.5f,
                            "mansard" or "gambrel" => 0.4f,
                            _ => 0.45f,
                        };
                        apex = topY + Mathf.Clamp(r * k, 1.2f, 14f);
                    }

                    int centre = verts.Count;
                    verts.Add(new Vector3(ccx, apex, ccz));
                    norms.Add(Vector3.up); cols.Add(tint);
                    int ring = verts.Count;
                    for (int v = 0; v < vc; v++)
                    {
                        verts.Add(new Vector3(-f.Flat[v * 2], topY, f.Flat[v * 2 + 1]));
                        norms.Add(Vector3.up); cols.Add(tint);
                    }
                    for (int v = 0; v < vc; v++)
                    {
                        tris.Add(centre);
                        tris.Add(ring + v);
                        tris.Add(ring + (v + 1) % vc);
                    }

                    chunk.Ranges.Add((start, verts.Count - start));
                    chunk.Heights.Add(height);
                    chunk.MaterialIdx.Add(fact.HasValue ? fact.Value.MaterialIdx : 0);
                    chunk.RoofShape.Add(fact.HasValue ? facts.ShapeOf(fact.Value) : "");
                    chunk.AddBuildingFacts(bi, baseY, topY, tint, shellWallBucket: -1,
                        footprintGameFrame: f.Flat, vertexCount: vc);
                }

                if (verts.Count == 0) continue;

                // one flip for the whole chunk, since every vertex was mirrored
                var tarr = tris.ToArray();
                GeoFrame.FlipWinding(tarr);

                var mesh = new Mesh { name = $"bld_{kv.Key}" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetColors(cols);
                mesh.triangles = tarr;
                mesh.RecalculateBounds();
                chunk.Mesh = mesh;
                chunks.Add(chunk);
            }
            Debug.Log($"BLD chunks={chunks.Count} withFacts={nFacts} realHeight={nRealHeight} " +
                      $"pitched={nPitched} coloured={nColoured}");
            return chunks;
        }

        // ====================================================================
        // BuildRich — the quality pass (docs/QUALITY_BAR.md). ADDITIVE: Build()
        // above is untouched and still gives the old single-material grey-box
        // result for any existing caller. BuildRich wires in:
        //   WallMaterials  — district-aware style chain + 11 real material
        //                    submeshes instead of one flat vertex-coloured mesh
        //   BuildingKit    — panel/apartment/shed/house/outbuilding kit models
        //                    claim a footprint and SKIP its extrusion entirely
        //   BuildingParts  — OSM building:part volumes replace a flat slab with
        //                    real stepped massing before any of the above runs
        //   FacadeRelief   — balconies, 3D window frames, cornices, glass-tower
        //                    mullions/bands
        // ====================================================================

        public class RichResult
        {
            public List<Chunk> Chunks = new List<Chunk>();
            public List<BuildingKit.PanelPlacement> Panels = new List<BuildingKit.PanelPlacement>();
            public List<BuildingKit.HousePlacement> Houses = new List<BuildingKit.HousePlacement>();
            public List<BuildingKit.OutPlacement> Outs = new List<BuildingKit.OutPlacement>();
            public List<BuildingKit.AptPlacement> Apts = new List<BuildingKit.AptPlacement>();
            public List<BuildingKit.ShedPlacement> Sheds = new List<BuildingKit.ShedPlacement>();
            public FacadeRelief.Items Relief = new FacadeRelief.Items();
            /// <summary>
            /// The OTHER six facade/roof-clutter layers — AC units, satellite
            /// dishes, downpipes, entrance canopies, flat-roof parapets and
            /// roof props (City/FacadeProps.cs; citymap.js:4990-5207 and
            /// :5356-5407). docs/open_items.json `facade-props-pitched`: these
            /// were NOT here, and their only producer was
            /// CityLoader.BuildFacadePropsForEveryBuilding, which could not see
            /// this loop's per-building state and hardcoded
            /// <c>pitchedFinal = false</c> — documented there as "an
            /// OVER-placement on the city's real pitched-roof stock". Wiring
            /// THAT into the renderer would have stood roof vents and parapet
            /// lips on gabled cottage roofs.
            ///
            /// It belongs in this loop and nowhere else, because the roof-
            /// clutter gate is `!pitched` (citymap.js:5356) and `pitched` is
            /// resolved HERE, from style.StyleForcesPitch overridden by the OSM
            /// roof:shape (citymap.js:4818-4821, the 13 roof kinds in
            /// roofs.json) — and because the dish loop has to replay the exact
            /// (i, k, f) hash walk FacadeRelief.BuildFacade just made, so the
            /// two calls must see the same contour, the same centroid, the same
            /// baseY and the same Flags object. One call, next to that one.
            /// </summary>
            public FacadeProps.Items Props = new FacadeProps.Items();
            public int NExtruded, NFacts, NRealHeight, NPitched, NColoured, NPartsExpanded;
            /// <summary>Extruded buildings the roof-clutter gate refused for
            /// being PITCHED and tall enough to have qualified otherwise
            /// (citymap.js:5356 `!pitched && h >= 10`) — see the call site.</summary>
            public int NRoofClutterSkippedPitched;
            /// <summary>Real-photograph facades (PhotoFacades.cs) — buildings whose
            /// walls were routed to a per-photo submesh instead of a WallMaterials
            /// bucket. NOT the same number as PhotoFacades' assignment count (609):
            /// a footprint that ALSO claims a kit model (panel/house/out/apt/shed)
            /// `continue`s before this is ever checked (citymap.js:4661-4746 — every
            /// kit-claim branch exits before `const pb` at :4746), so this is strictly
            /// smaller. Measured by reimplementing the whole decision chain in Node
            /// over the live production data (city.json/levels.json/parts.json/
            /// malls.json/kit_meta.json): 273, cross-checked against this exact
            /// method's own live log (see docs/open_items.json `photo-render-count-drift`
            /// and CityRenderProbe.cs's assert_photoFacades comment).</summary>
            public int NPhotoBuildings;
            /// <summary>Footprints skipped entirely because a real Blender mall model
            /// stands there instead (citymap.js:4591 `this._mallBids?.has(bi)`) —
            /// counted for visibility; these never reach TryClaim OR the photo check.</summary>
            public int NMallSkipped;
            public PhotoFacades.Result Photo;
            // building-level DECISION counts (terrain-independent — comparable
            // 1:1 against tools/_probe_buildingquality.py's "relief" line,
            // unlike Relief.Windows/.Balconies above which are actual PLACED
            // item instances and depend on TerrainSampler.HeightAtGame too).
            public int NWantWindows, NWantBalconies, NWantWoodBalc, NWantCornice;
            /// <summary>Per-building wall submesh label ("style0".."style3", or a
            /// walls.json/loggia name) -> count, for the extruded (non-kit) buildings.</summary>
            public Dictionary<string, int> WallBucketCounts = new Dictionary<string, int>();
            /// <summary>OSM roof:shape name -> count (only buildings with a mapped shape).</summary>
            public Dictionary<string, int> RoofShapeCounts = new Dictionary<string, int>();
        }

        static string WallBucketName(int wb, WallMaterials.Catalog cat)
        {
            if (wb < 4) return $"wall:style{wb}"; // painted, untextured (windows/loggia-default/glass/old-Tbilisi)
            if (wb < cat.LoggiaBase) return $"wall:{cat.WallNames[wb - 4]}";
            int li = wb - cat.LoggiaBase;
            return (li >= 0 && li < cat.LoggiaKeys.Length) ? $"wall:loggia:{cat.LoggiaKeys[li]}" : $"wall:loggia:{li}";
        }

        /// <summary>
        /// citymap.js:841-846 `this._mallBids = new Set(this._mallList.map((e) =>
        /// e.bid))` — the bare building-index set a real Blender mall model
        /// (MallPlacer.cs/MallRenderer.cs) stands on top of, straight off
        /// malls.json's own "bid" field (no filtering: 1:1 with the JSON, same
        /// as MallPlacer.Build's placement count). Loaded directly rather than
        /// through MallPlacer.Build to avoid that method's terrain/sign-anchor
        /// work for what is here just a skip set.
        /// </summary>
        static HashSet<int> LoadMallBids(string path = null)
        {
            path ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "malls.json");
            var set = new HashSet<int>();
            if (!File.Exists(path)) return set;
            try
            {
                var arr = JArray.Parse(File.ReadAllText(path));
                foreach (var e in arr) set.Add(e["bid"].Value<int>());
            }
            catch (Exception ex) { Debug.LogWarning($"BLDRICH malls.json unreadable for mallBids skip ({ex.Message})"); }
            return set;
        }

        /// <summary>
        /// Ported from the massing+material+relief part of src/citymap.js
        /// _buildBuildings (citymap.js:4061-5562). Everything that method does
        /// for photo facades (`pb`) is OUT OF SCOPE here — that is the other
        /// agent's exclusive per-building photo system; this method always
        /// behaves as if `pb` were null, exactly like a building that lost the
        /// exclusive photo draw and fell back to a real wall-material tile.
        ///
        /// `maxBuildings` caps the EXPANDED entry count (a building with parts
        /// contributes several), not the raw footprint count — a difference
        /// from Build() above that only matters for the dev/test knob.
        /// </summary>
        public static RichResult BuildRich(CityData city, BuildingFacts facts = null, BuildingKit.Catalog kit = null,
                                            BuildingParts parts = null, WallMaterials.Catalog wallCat = null,
                                            PhotoFacades.Result photo = null, HashSet<int> mallBids = null,
                                            int maxBuildings = int.MaxValue)
        {
            facts ??= BuildingFacts.Load();
            kit ??= BuildingKit.Catalog.Load();
            parts ??= BuildingParts.Load();
            wallCat ??= WallMaterials.Catalog.Load();
            mallBids ??= LoadMallBids();
            if (photo == null)
            {
                // Real-photograph facades (PhotoFacades.cs, verified separately by
                // the agent that ported it). A winning building's walls are routed
                // to a per-photo submesh below INSTEAD of a WallMaterials bucket —
                // the citymap.js `pb` branch this method's own class doc says it
                // otherwise deliberately skips.
                var manifest = PhotoFacades.LoadManifest(PhotoFacades.DefaultManifestPath);
                var footprints = new List<float[]>(city.Buildings.Count);
                for (int bi0 = 0; bi0 < city.Buildings.Count; bi0++) footprints.Add(city.Buildings[bi0].Flat);
                photo = PhotoFacades.Assign(manifest, footprints);
            }

            var result = new RichResult { Photo = photo };
            var expanded = parts.Expand(city);
            result.NPartsExpanded = expanded.Count - city.Buildings.Count;
            int total = Mathf.Min(expanded.Count, maxBuildings);

            var byCell = new Dictionary<long, List<int>>();
            for (int ei = 0; ei < total; ei++)
            {
                var e = expanded[ei];
                if (e.Flat.Length < 6) continue;
                int cx0 = Mathf.FloorToInt(e.Flat[0] / ChunkSize);
                int cz0 = Mathf.FloorToInt(e.Flat[1] / ChunkSize);
                long key = (long)cx0 * 100003 + cz0;
                if (!byCell.TryGetValue(key, out var list)) byCell[key] = list = new List<int>();
                list.Add(ei);
            }

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var cols = new List<Color32>();
            var uvs = new List<Vector2>();
            var wallTris = new Dictionary<int, List<int>>();
            var roofTris = new Dictionary<int, List<int>>();
            var pbTris = new Dictionary<string, List<int>>(); // photo-facade walls, keyed by ManifestEntry.Key
            Func<float, float, float> heightAt = city.Terrain.HeightAtGame;

            foreach (var kv in byCell)
            {
                verts.Clear(); norms.Clear(); cols.Clear(); uvs.Clear();
                wallTris.Clear(); roofTris.Clear(); pbTris.Clear();
                var chunk = new Chunk();

                foreach (int ei in kv.Value)
                {
                    var e = expanded[ei];
                    int vc = e.Flat.Length / 2;
                    if (vc < 3) continue;
                    int bi = e.Bi;
                    // citymap.js:4591 — a real Blender mall model stands here instead
                    // (MUST be checked before everything below, exactly like the JS:
                    // a mall footprint never reaches TryClaim OR the photo check).
                    if (mallBids.Contains(bi)) { result.NMallSkipped++; continue; }

                    // centroid in the GAME frame (unmirrored) — matches BuildingFacts/BuildingKit/WallMaterials convention
                    // DOUBLE accumulator — see the Ring Centroid note below.
                    double dcx = 0.0, dcz = 0.0;
                    for (int v = 0; v < vc; v++) { dcx += RingM(e.Flat[v * 2]); dcz += RingM(e.Flat[v * 2 + 1]); }
                    dcx /= vc; dcz /= vc;
                    float gcx = (float)dcx, gcz = (float)dcz;
                    // citymap.js:4601 — a real landmark model stands here instead
                    if (InLandmarkExclusion(gcx, gcz)) continue;

                    var fact = facts.At(dcx, dcz);
                    if (fact.HasValue) result.NFacts++;

                    float h = e.Height;
                    if (fact.HasValue && fact.Value.HeightOrZero > 0f) { h = fact.Value.HeightOrZero; result.NRealHeight++; }

                    // ground under the WHOLE ring, never the centroid (citymap.js:4596-4625)
                    float gc = heightAt(gcx, gcz);
                    float gmin = gc, gmax = gc;
                    for (int v = 0; v < vc; v++)
                    {
                        float gy = heightAt(e.Flat[v * 2], e.Flat[v * 2 + 1]);
                        if (gy < gmin) gmin = gy;
                        if (gy > gmax) gmax = gy;
                    }
                    float baseFloor = gc - 2f + e.Off; // `off`: a part's min_height
                    float boxBase = e.Off > 0f ? baseFloor : baseFloor - Mathf.Min(gc - gmin, 20f);
                    float topY = gc + h + Mathf.Min(gmax - gc, 20f);
                    float propBaseY = Mathf.Max(baseFloor, gmax - 2f); // real models stand ON the ground

                    // Signed shoelace area in DOUBLE precision — citymap.js:4649
                    // `Math.abs(THREE.ShapeUtils.area(contour))` is a plain JS Number
                    // (float64); BuildingKit.PolygonArea accumulates in `float` over
                    // ABSOLUTE game-frame coordinates, and this one shared frame covers
                    // Tbilisi AND Batumi/Kutaisi/Gori/Rustavi/Kazbegi (city.json meta.cities),
                    // so |x|,|z| reach ~264 km for the far cities. Each shoelace term
                    // (coord * coord) is then ~1e10, and float32's ~7 significant digits
                    // give an absolute rounding error of the same order as the TRUE area —
                    // catastrophic cancellation, not a rounding nuance. Measured against
                    // the real data: 48,456 of 193,146 buildings have a float32 area error
                    // over 50 m^2 (some over 4,000 m^2), enough to flip the cottage
                    // (40<area<260) / historic (area<900) / old-town (area<500) / default
                    // (area<320) threshold tests that WallMaterials.Classify and
                    // FacadeRelief.DecideEarly key off, which is what put a handful of
                    // buildings in the wrong wallBucket (tools/_probe_buildingquality.py's
                    // wallbucket_* counts). Recomputed here so Classify/IsCottage/
                    // DecideEarly/ResolveWallBucket (and BuildingKit.TryClaim, which takes
                    // this same value as a parameter) see the identical number the JS and
                    // its Python oracle do; only the final narrow to `float` loses precision,
                    // and that happens AFTER the cancellation-prone subtraction, not before.
                    double areaD = 0.0;
                    for (int v = 0, pv = vc - 1; v < vc; pv = v++)
                        areaD += (double)e.Flat[pv * 2] * e.Flat[v * 2 + 1] - (double)e.Flat[v * 2] * e.Flat[pv * 2 + 1];
                    float area = (float)System.Math.Abs(areaD * 0.5);
                    var district = WallMaterials.DistrictAt(gcx, gcz);
                    bool inOldTown = WallMaterials.InOldTown(gcx, gcz, district);
                    bool cottage = WallMaterials.IsCottage(area, h, inOldTown);

                    // ---- REAL MODEL instead of an extruded box (citymap.js:4661-4739) ----
                    var claim = BuildingKit.TryClaim(kit, district, cottage, e.Flat, vc, area, h, gcx, gcz, propBaseY, bi,
                        out var panelP, out var houseP, out var outP, out var aptP, out var shedP);
                    if (claim != BuildingKit.Claim.None)
                    {
                        switch (claim)
                        {
                            case BuildingKit.Claim.Panel: result.Panels.Add(panelP); break;
                            case BuildingKit.Claim.House: result.Houses.Add(houseP); break;
                            case BuildingKit.Claim.Out: result.Outs.Add(outP); break;
                            case BuildingKit.Claim.Apt: result.Apts.Add(aptP); break;
                            case BuildingKit.Claim.Shed: result.Sheds.Add(shedP); break;
                        }
                        continue; // NO extrusion for this footprint
                    }
                    result.NExtruded++;

                    // ---- style chain: which Tbilisi building is this? (citymap.js:4715-4766) ----
                    var style = WallMaterials.Classify(bi, gcx, gcz, h, vc, area);

                    // OSM roof:shape beats the style chain's own guess (citymap.js:4818-4821)
                    bool pitched = style.StyleForcesPitch;
                    if (fact.HasValue)
                    {
                        string shape = facts.ShapeOf(fact.Value);
                        if (!string.IsNullOrEmpty(shape)) pitched = facts.IsPitched(fact.Value, vc);
                    }
                    if (pitched) result.NPitched++;

                    // ---- real-photograph facade (PhotoFacades.cs)? A winning
                    // building's walls are routed to a per-photo submesh INSTEAD of
                    // a WallMaterials bucket, and the procedural facade relief
                    // (windows/balconies) is skipped — the photo already shows real
                    // windows. The roof is unaffected: a photo is a facade cut only.
                    bool hasPhoto = photo.AssignedByBuilding.TryGetValue(bi, out var photoEntry);

                    int wallBucket = -1; // sentinel: -1 == "photo facade, not a WallMaterials bucket"
                    FacadeRelief.Flags relief = default;
                    Color32 roofColor;
                    bool metalRoof;
                    int roofBucket;
                    int buildingStart;
                    // Single representative tint for Combat/BreakWall.cs's carved shell
                    // (see Chunk.ShellTint's own doc) — white for a photo facade (the
                    // photo supplies colour), the brighter wall colour otherwise.
                    Color32 shellTint = new Color32(255, 255, 255, 255);

                    if (hasPhoto)
                    {
                        result.NPhotoBuildings++;
                        // wantWindows only tints the WALL colour (discarded for a
                        // photo building) — roofColor/metalRoof are the only outputs needed.
                        WallMaterials.ComputeColors(bi, style, false, pitched, fact,
                            out _, out _, out roofColor, out metalRoof);
                        if (fact.HasValue && fact.Value.HasColour) result.NColoured++;
                        roofBucket = WallMaterials.ResolveRoofBucket(bi, metalRoof, wallCat);

                        // PhotoFacades.PlanUV: anchor always 0 (no random per-building
                        // phase — "photos must anchor at the real corner"); vBottom
                        // always 0 (never the -2/3 dirty band, which would wrap the
                        // photo's far edge onto the base); vTop = h/3 like every style.
                        var plan = PhotoFacades.PlanUV(photoEntry, h);
                        var usP = new float[vc + 1];
                        usP[0] = plan.UAnchor;
                        float perimP = 0f;
                        for (int i = 0; i < vc; i++)
                        {
                            int j = (i + 1) % vc;
                            float dx = e.Flat[j * 2] - e.Flat[i * 2], dz = e.Flat[j * 2 + 1] - e.Flat[i * 2 + 1];
                            perimP += Mathf.Sqrt(dx * dx + dz * dz);
                            usP[i + 1] = plan.UAnchor + perimP / 3f;
                        }

                        buildingStart = verts.Count;
                        if (!pbTris.TryGetValue(photoEntry.Key, out var pTarget)) pbTris[photoEntry.Key] = pTarget = new List<int>();
                        var photoTint = new Color32(255, 255, 255, 255); // the photo itself supplies colour

                        for (int i = 0; i < vc; i++)
                        {
                            int j = (i + 1) % vc;
                            float ax = -e.Flat[i * 2], az = e.Flat[i * 2 + 1]; // mirror X
                            float bx = -e.Flat[j * 2], bz = e.Flat[j * 2 + 1];

                            var p0 = new Vector3(ax, boxBase, az);
                            var p1 = new Vector3(bx, boxBase, bz);
                            var p2 = new Vector3(bx, topY, bz);
                            var p3 = new Vector3(ax, topY, az);
                            var nrm = Vector3.Cross(p1 - p0, p3 - p0).normalized;

                            int b = verts.Count;
                            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                            norms.Add(nrm); norms.Add(nrm); norms.Add(nrm); norms.Add(nrm);
                            cols.Add(photoTint); cols.Add(photoTint); cols.Add(photoTint); cols.Add(photoTint);
                            uvs.Add(new Vector2(usP[i], plan.VBottom)); uvs.Add(new Vector2(usP[j], plan.VBottom));
                            uvs.Add(new Vector2(usP[j], plan.VTop)); uvs.Add(new Vector2(usP[i], plan.VTop));

                            pTarget.Add(b); pTarget.Add(b + 1); pTarget.Add(b + 2);
                            pTarget.Add(b); pTarget.Add(b + 2); pTarget.Add(b + 3);
                        }
                    }
                    else
                    {
                        // ---- facade relief decision + wall material bucket (interdependent — see FacadeRelief.DecideLate) ----
                        relief = FacadeRelief.DecideEarly(bi, style, h, area, cottage, pitched);
                        if (relief.WantWindows) result.NWantWindows++;
                        if (relief.WantBalconies) result.NWantBalconies++;
                        if (relief.WantWoodBalc) result.NWantWoodBalc++;
                        if (relief.WantCornice) result.NWantCornice++;
                        string osmMaterial = fact.HasValue ? facts.MaterialOf(fact.Value) : null;
                        wallBucket = WallMaterials.ResolveWallBucket(bi, style, wallCat, relief.WantWindows, osmMaterial);
                        FacadeRelief.DecideLate(bi, style, wallBucket, area, ref relief);

                        WallMaterials.ComputeColors(bi, style, relief.WantWindows, pitched, fact,
                            out var wallTop, out var wallBottom, out roofColor, out metalRoof);
                        if (fact.HasValue && fact.Value.HasColour) result.NColoured++;
                        roofBucket = WallMaterials.ResolveRoofBucket(bi, metalRoof, wallCat);
                        shellTint = wallTop;

                        // perimeter-cell U array, shared in spirit with FacadeRelief's window loop (citymap.js:4895-4905)
                        float uP = Mathf.Floor((float)(WallMaterials.Hash(bi + 8111) * 8));
                        var us = new float[vc + 1];
                        us[0] = uP;
                        float perim = 0f;
                        for (int i = 0; i < vc; i++)
                        {
                            int j = (i + 1) % vc;
                            float dx = e.Flat[j * 2] - e.Flat[i * 2], dz = e.Flat[j * 2 + 1] - e.Flat[i * 2 + 1];
                            perim += Mathf.Sqrt(dx * dx + dz * dz);
                            us[i + 1] = uP + perim / 3f;
                        }
                        float vTop = h / 3f;
                        float vBot = (!relief.WantWindows && style.Style != 2) ? 0f : -2f / 3f;

                        buildingStart = verts.Count;
                        if (!wallTris.TryGetValue(wallBucket, out var wTarget)) wallTris[wallBucket] = wTarget = new List<int>();

                        // ---- walls: a quad per edge (own 4 verts — flat shaded, matches Build() above) ----
                        for (int i = 0; i < vc; i++)
                        {
                            int j = (i + 1) % vc;
                            float ax = -e.Flat[i * 2], az = e.Flat[i * 2 + 1]; // mirror X
                            float bx = -e.Flat[j * 2], bz = e.Flat[j * 2 + 1];

                            var p0 = new Vector3(ax, boxBase, az);
                            var p1 = new Vector3(bx, boxBase, bz);
                            var p2 = new Vector3(bx, topY, bz);
                            var p3 = new Vector3(ax, topY, az);
                            var nrm = Vector3.Cross(p1 - p0, p3 - p0).normalized;

                            int b = verts.Count;
                            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                            norms.Add(nrm); norms.Add(nrm); norms.Add(nrm); norms.Add(nrm);
                            cols.Add(wallBottom); cols.Add(wallBottom); cols.Add(wallTop); cols.Add(wallTop);
                            uvs.Add(new Vector2(us[i], vBot)); uvs.Add(new Vector2(us[j], vBot));
                            uvs.Add(new Vector2(us[j], vTop)); uvs.Add(new Vector2(us[i], vTop));

                            wTarget.Add(b); wTarget.Add(b + 1); wTarget.Add(b + 2);
                            wTarget.Add(b); wTarget.Add(b + 2); wTarget.Add(b + 3);
                        }
                    }

                    // ---- roof fan (same apex/pitch math as Build() above) ----
                    float apex = topY;
                    if (pitched)
                    {
                        float r = 0f;
                        for (int v = 0; v < vc; v++)
                        {
                            float dx = -e.Flat[v * 2] - (-gcx), dz = e.Flat[v * 2 + 1] - gcz;
                            r += Mathf.Sqrt(dx * dx + dz * dz);
                        }
                        r /= vc;
                        string shape = fact.HasValue ? facts.ShapeOf(fact.Value) : "";
                        float k = shape switch
                        {
                            "pyramidal" => 0.9f,
                            "dome" or "onion" or "round" => 0.8f,
                            "gabled" or "half-hipped" => 0.55f,
                            "hipped" => 0.5f,
                            "mansard" or "gambrel" => 0.4f,
                            _ => 0.45f,
                        };
                        apex = topY + Mathf.Clamp(r * k, 1.2f, 14f);
                    }

                    if (!roofTris.TryGetValue(roofBucket, out var rTarget)) roofTris[roofBucket] = rTarget = new List<int>();
                    int centre = verts.Count;
                    verts.Add(new Vector3(-gcx, apex, gcz));
                    norms.Add(Vector3.up); cols.Add(roofColor); uvs.Add(new Vector2(gcx / 3f, gcz / 3f));
                    int ring = verts.Count;
                    for (int v = 0; v < vc; v++)
                    {
                        verts.Add(new Vector3(-e.Flat[v * 2], topY, e.Flat[v * 2 + 1]));
                        norms.Add(Vector3.up); cols.Add(roofColor);
                        uvs.Add(new Vector2(e.Flat[v * 2] / 3f, e.Flat[v * 2 + 1] / 3f));
                    }
                    for (int v = 0; v < vc; v++)
                    {
                        rTarget.Add(centre);
                        rTarget.Add(ring + v);
                        rTarget.Add(ring + (v + 1) % vc);
                    }

                    chunk.Ranges.Add((buildingStart, verts.Count - buildingStart));
                    chunk.Heights.Add(h);
                    chunk.MaterialIdx.Add(fact.HasValue ? fact.Value.MaterialIdx : 0);
                    chunk.RoofShape.Add(fact.HasValue ? facts.ShapeOf(fact.Value) : "");
                    chunk.WallBucket.Add(wallBucket);
                    chunk.RoofBucketIdx.Add(roofBucket);
                    // e.Flat is the ring ACTUALLY extruded (a parts-expanded sub-footprint
                    // for a stepped tower, not necessarily city.Buildings[bi].Flat) — the
                    // same ring BreakWall.BuildShell must cut cells from to match the
                    // geometry a hole carves into.
                    chunk.AddBuildingFacts(bi, boxBase, topY, shellTint, wallBucket,
                        footprintGameFrame: e.Flat, vertexCount: vc);

                    // tallies for BuildingQualityProbe / tools/_probe_buildingquality.py parity
                    string shapeTally = fact.HasValue ? facts.ShapeOf(fact.Value) : "";
                    if (!string.IsNullOrEmpty(shapeTally))
                    {
                        result.RoofShapeCounts.TryGetValue(shapeTally, out int sc);
                        result.RoofShapeCounts[shapeTally] = sc + 1;
                    }
                    if (hasPhoto)
                    {
                        result.WallBucketCounts.TryGetValue("photo", out int pc);
                        result.WallBucketCounts["photo"] = pc + 1;
                    }
                    else
                    {
                        string wbLabel = WallBucketName(wallBucket, wallCat);
                        result.WallBucketCounts.TryGetValue(wbLabel, out int wc);
                        result.WallBucketCounts[wbLabel] = wc + 1;
                    }

                    // ---- 3D facade relief: balconies, window frames, cornices, glass bands ----
                    // (skipped for a photo building — the photo already shows real windows)
                    if (!hasPhoto)
                    {
                        FacadeRelief.BuildFacade(bi, e.Flat, vc, gcx, gcz, boxBase, topY, cottage, relief, heightAt, result.Relief);
                        if (style.Style == 2) FacadeRelief.BuildGlassRelief(e.Flat, vc, gcx, gcz, boxBase, topY, h, result.Relief);
                    }

                    // ---- the other six layers: AC/dish/downpipe/entrance
                    // canopy (citymap.js:4990-5207) + flat-roof parapets and
                    // roof clutter (citymap.js:5356-5407). SAME arguments
                    // FacadeRelief.BuildFacade just got, plus the REAL `pitched`
                    // this loop resolved — see RichResult.Props.
                    //
                    // OUTSIDE the `!hasPhoto` arm, with roofOnly instead: the
                    // facade half is withheld from a photo building (this
                    // method's standing rule, and citymap.js's own `!pb` gates
                    // on the entrance at :5136 and the AC at :5157), but the
                    // ROOF block at citymap.js:5356 is not inside the `pb`
                    // branch at all — a photo facade is a wall cut and the flat
                    // slab above it still wears its parapet lip and its vents.
                    // The JS's pb-specific window/AC relief
                    // (citymap.js:5168-5205) remains the one piece neither
                    // FacadeProps.cs nor this call ports, as that file says.
                    FacadeProps.BuildFacadeProps(bi, e.Flat, vc, gcx, gcz, boxBase, topY, h, area, cottage,
                        style, pitched, relief, heightAt, result.Props, roofOnly: hasPhoto);
                    // THE NUMBER docs/open_items.json `facade-props-pitched`
                    // turns on: every extruded building that citymap.js:5356's
                    // `!pitched && h >= 10` gate REFUSES. With the old
                    // `pitchedFinal = false` each of these would have grown a
                    // parapet lip per wall segment and a shaft/tank/vent cluster
                    // on a gabled roof. It is the measure of the over-placement
                    // that is now not shipped.
                    if (pitched && h >= 10f) result.NRoofClutterSkippedPitched++;
                }

                if (verts.Count == 0) continue;

                var wallKeys = new List<int>(wallTris.Keys); wallKeys.Sort();
                var roofKeys = new List<int>(roofTris.Keys); roofKeys.Sort();
                var pbKeys = new List<string>(pbTris.Keys); pbKeys.Sort();
                if (wallKeys.Count == 0 && roofKeys.Count == 0 && pbKeys.Count == 0) continue;

                var mesh = new Mesh { name = $"bldrich_{kv.Key}" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetColors(cols);
                mesh.SetUVs(0, uvs);
                mesh.subMeshCount = wallKeys.Count + roofKeys.Count + pbKeys.Count;

                int sub = 0;
                foreach (int wb in wallKeys)
                {
                    var arr = wallTris[wb].ToArray();
                    GeoFrame.FlipWinding(arr);
                    mesh.SetTriangles(arr, sub);
                    chunk.SubmeshNames.Add(WallBucketName(wb, wallCat));
                    sub++;
                }
                foreach (int rb in roofKeys)
                {
                    var arr = roofTris[rb].ToArray();
                    GeoFrame.FlipWinding(arr);
                    mesh.SetTriangles(arr, sub);
                    string name = (rb >= 0 && rb < wallCat.RoofNames.Length) ? wallCat.RoofNames[rb] : rb.ToString();
                    chunk.SubmeshNames.Add($"roof:{name}");
                    sub++;
                }
                foreach (string pk in pbKeys)
                {
                    var arr = pbTris[pk].ToArray();
                    GeoFrame.FlipWinding(arr);
                    mesh.SetTriangles(arr, sub);
                    chunk.SubmeshNames.Add($"pb:{pk}");
                    sub++;
                }
                mesh.RecalculateBounds();
                chunk.Mesh = mesh;
                result.Chunks.Add(chunk);
            }

            Debug.Log($"BLDRICH chunks={result.Chunks.Count} extruded={result.NExtruded} " +
                      $"panel={result.Panels.Count} house={result.Houses.Count} out={result.Outs.Count} " +
                      $"apt={result.Apts.Count} shed={result.Sheds.Count} partsExpanded={result.NPartsExpanded} " +
                      $"photo={result.NPhotoBuildings} mallSkipped={result.NMallSkipped} " +
                      $"withFacts={result.NFacts} realHeight={result.NRealHeight} pitched={result.NPitched} coloured={result.NColoured} " +
                      $"balconies={result.Relief.Balconies.Count} windows={result.Relief.Windows.Count} " +
                      $"cornices={result.Relief.Cornices.Count} grilles={result.Relief.Grilles.Count} " +
                      $"glassMullions={result.Relief.GlassMullions.Count} glassBands={result.Relief.GlassBands.Count}");
            // docs/open_items.json `facade-props-pitched` — the six layers, and
            // the figure the whole row turns on: how many of the extruded
            // buildings were PITCHED and therefore got no parapet and no roof
            // clutter. `roofPitchedSkipped` is the over-placement that the old
            // `pitchedFinal = false` would have shipped.
            Debug.Log($"BLDPROPS ac={result.Props.Acs.Count} dish={result.Props.Dishes.Count} " +
                      $"drainpipe={result.Props.Drainpipes.Count} entrance={result.Props.Entrances.Count} " +
                      $"parapet={result.Props.Parapets.Count} roofProp={result.Props.RoofProps.Count} " +
                      $"pitched={result.NPitched} extruded={result.NExtruded} " +
                      $"roofClutterSkippedPitched={result.NRoofClutterSkippedPitched}");
            return result;
        }
    }
}
