using System;
using System.Collections.Generic;
using System.Diagnostics;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Two of the three City rows in docs/open_items.json, measured on a REAL
    /// Assets/Scenes/Game.unity load rather than on a constructed fixture —
    /// because both are about what the LOAD costs and what the LOADED scene's
    /// physics world contains, neither of which a pure-function probe can see
    /// (.claude/skills/unity/SKILL.md: "A probe proves a CALCULATION, not that
    /// the calculation is REACHED").
    ///
    ///   `ground-load-time`  — opens the scene with a stopwatch and reprints
    ///     every stage figure the ground layer now logs (CityData.ParseCount/
    ///     ParseMs, CityGroundData.StageMs, RoadNetworkRenderer.Report's
    ///     groundData/tile/attach/collider/instance split) on ONE grep-able
    ///     line, plus the road counts that must not move.
    ///
    ///   `collidable-flags`  — the row asks either to push
    ///     SurfaceTile.Collidable/Ground into a collidable set, or to prove
    ///     with evidence that the MeshColliders + CombatTerrainTag already
    ///     give every JS consumer of `world.collidables` the same answer.
    ///     This is that evidence, one line per consumer: each JS call site of
    ///     `collidablesNear` has a C# counterpart that asks Unity's broadphase
    ///     the same question, so the test is that the broadphase returns the
    ///     ROAD DECK for a point over asphalt, returns it tagged as terrain,
    ///     and returns NOTHING road-shaped for a sidewalk/verge/marking tile
    ///     (the three layers citymap.js:6828/6849/6865 deliberately leave out
    ///     of `collidables`).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///     -executeMethod TbilisiFly.Probes.GroundLoadProbe.Run
    /// </summary>
    public static class GroundLoadProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        static bool _all = true;

        static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return $"(threw: {e.GetType().Name})"; }
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"{(cond ? "PASS" : "FAIL")} {key}";
            if (!cond && note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _all = false;
        }

        public static void Run()
        {
            var wall = Stopwatch.StartNew();
            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }
            long openMs = wall.ElapsedMilliseconds;
            Check("sceneLoads", openOk && scene.IsValid());
            if (!openOk || !scene.IsValid()) { Verdict(); return; }

            Debug.Log($"GROUNDLOAD sceneOpenMs={openMs} cityParseCount={CityData.ParseCount} " +
                      $"cityParseMs={CityData.ParseMs}");

            // ---- ground-load-time ------------------------------------------
            var roads = UnityEngine.Object.FindFirstObjectByType<RoadNetworkRenderer>();
            Check("roadRendererInScene", roads != null,
                  "RoadNetworkRenderer is not in Game.unity — the ground layer never runs");
            if (roads != null)
            {
                roads.EnsureBuilt(); // idempotent; OnEnable already ran on load
                var r = roads.LastReport;
                Debug.Log($"GROUNDLOAD roads segs={r.RoadSegs} bridges={r.BridgeSegs} tunnels={r.TunnelSegs} " +
                          $"junctions={r.Junctions} asphaltTiles={r.AsphaltTiles} sidewalkTiles={r.SidewalkTiles} " +
                          $"vergeTiles={r.VergeTiles} markingTiles={r.MarkingTiles} surfaces={r.Surfaces} " +
                          $"collidableTiles={r.CollidableTiles} gameObjects={r.GameObjects}");
                Debug.Log($"GROUNDLOAD roadms groundData={r.GroundDataMs} tile={r.TileMs} attach={r.AttachMs} " +
                          $"collider={r.ColliderMs} instance={r.InstanceMs}");
            }
            Debug.Log($"GROUNDLOAD citygroundMs={CityGroundData.BuildMs} surfacesMs={CityGroundData.SurfacesMs}");
            var stageLine = new List<string>();
            foreach (var kv in CityGroundData.StageMs) stageLine.Add($"{kv.Key}={kv.Value}");
            stageLine.Sort();
            Debug.Log($"GROUNDLOAD citygroundstages {string.Join(" ", stageLine)}");

            // One parse of city.json.gz per PATH, not per caller — the cache in
            // Data/CityData.cs. Eleven parses / 6,487 ms before.
            Check("cityJsonParsedOnce", CityData.ParseCount <= 2,
                  $"city.json.gz was parsed {CityData.ParseCount}x for {CityData.ParseMs} ms");

            // ---- facade-props-pitched --------------------------------------
            var cityR = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            Check("cityRendererInScene", cityR != null);
            var rep = cityR?.LastReport;
            if (rep != null)
            {
                var want = new[] { "ac", "dish", "drainpipe", "entrance", "parapet", "roof_prop" };
                var line = new List<string>();
                bool allPresent = true;
                foreach (string k in want)
                {
                    rep.FacadeLayers.TryGetValue(k, out int c);
                    line.Add($"{k}:{c}");
                    if (c <= 0) allPresent = false;
                }
                Debug.Log($"GROUNDLOAD facadeprops {string.Join(" ", line)} " +
                          $"facadeInstances={rep.FacadeInstances} facadeBatches={rep.FacadeBatches}");
                Check("facadePropsLayersDrawn", allPresent,
                      "a FacadeProps layer reached CITYFACADE with zero instances");
            }

            // ---- collidable-flags ------------------------------------------
            CollidableFlags(roads);

            Verdict();
        }

        /// <summary>
        /// One assertion per JS consumer of `world.collidables` that can see a
        /// ROAD tile. The JS pushes only the asphalt deck, the junction aprons,
        /// the generic bridge decks and the tunnel road floor into
        /// `this.collidables` (src/citymap.js:6590 under
        /// `_addChunkedSurface`'s `collidable` flag, plus RoadNetworkRenderer's
        /// four `collidable: true` Single() call sites); every consumer then
        /// reaches them through `collidablesNear(x, z)` and a THREE.Raycaster.
        /// On the Unity side those same tiles carry a MeshCollider, every
        /// consumer's mask is `~0`, and Physics.RaycastAll IS the broadphase —
        /// so the equivalent test is that the same query returns the same
        /// surface.
        /// </summary>
        static void CollidableFlags(RoadNetworkRenderer roads)
        {
            if (roads == null) return;

            // A point over the real asphalt deck: take a collider the renderer
            // built and use its own bounds centre, so this cannot drift with
            // the data (no hand-typed literal — SKILL.md's "5 stale hand-typed
            // probe literals in 2 days").
            MeshCollider asphalt = null;
            var asphaltRoot = roads.transform.Find("asphalt");
            if (asphaltRoot != null)
                for (int i = 0; i < asphaltRoot.childCount && asphalt == null; i++)
                {
                    var mc = asphaltRoot.GetChild(i).GetComponent<MeshCollider>();
                    // the biggest tile, so a vertical ray through its centre is
                    // very unlikely to miss the ribbon inside the tile bounds
                    if (mc != null && mc.sharedMesh != null && mc.sharedMesh.vertexCount > 20000) asphalt = mc;
                }
            if (asphalt == null && asphaltRoot != null)
                for (int i = 0; i < asphaltRoot.childCount && asphalt == null; i++)
                    asphalt = asphaltRoot.GetChild(i).GetComponent<MeshCollider>();

            Check("asphaltTileHasCollider", asphalt != null,
                  "no MeshCollider under the asphalt root — citymap.js:6808 `{collidable:true}`");
            if (asphalt == null) return;

            // Aim at a real triangle: a vertex of the tile mesh, not the tile's
            // bounds centre (a 1500 m tile can be mostly empty).
            var verts = asphalt.sharedMesh.vertices;
            Vector3 target = asphalt.transform.TransformPoint(verts[verts.Length / 2]);
            Vector3 origin = target + Vector3.up * 60f;
            Debug.Log($"COLLIDABLE target=({target.x:F1},{target.y:F1},{target.z:F1}) " +
                      $"tile={asphalt.name} tileVerts={verts.Length}");

            bool HitsRoad(RaycastHit[] hits, out RaycastHit road)
            {
                road = default;
                foreach (var h in hits)
                    if (h.collider == asphalt) { road = h; return true; }
                return false;
            }

            // --- consumer 1: the plane's ground probe / street sample.
            // src/game.js:1286 `_sampleStreetY` and :1339/:1371
            // `_topSurfaceBelow` sweep `collidablesNear` downward; the port is
            // Core/PlaneRaycastWorld.DownwardHits (Core/PlaneGroundProbe.cs's
            // IGroundProbeWorld), src/planephys.js:873 the same set again.
            var world = new PlaneRaycastWorld();
            var pts = world.DownwardHits(target.x, target.z, target.y + 60f);
            bool groundProbeFound = false;
            foreach (var p in pts) if (Mathf.Abs(p.y - target.y) < 1.5f) groundProbeFound = true;
            Check("consumer_planeGroundProbe", groundProbeFound,
                  $"PlaneRaycastWorld.DownwardHits returned {pts.Count} points, none on the deck");

            // --- consumer 2: "is this terrain?" — citymap.js:6585
            // `mesh.userData.isTerrain = ground`, read by the foot/plane ground
            // probes and Combat/Ballistics. The port is CombatTerrainTag.
            var downAll = Physics.RaycastAll(origin, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(downAll, (a, b) => a.distance.CompareTo(b.distance));
            Check("consumer_isTerrainFlag", HitsRoad(downAll, out var roadHit) && world.IsTerrain(roadHit.collider),
                  "the asphalt tile is not tagged CombatTerrainTag — citymap.js:6585 ground=true");

            // --- consumer 3: the hero's ground probe. src/foot.js:4781-4806
            // `_probeGround` reads `collidablesNear`; the port is
            // Character/FootController.ProbeGround, which is PRIVATE and whose
            // ray geometry is its own (origin = fromY + 1.4, dist = 1.4 +
            // reach). Driven by reflection rather than re-cast by hand here,
            // because a probe that re-implements the query proves the probe,
            // not the consumer — and the first version of this check did cast
            // its own 200 m ray from 60 m up, which found whatever stands over
            // the street and read as a failure.
            float footY = float.NaN;
            try
            {
                // FootController is a plain class, not a MonoBehaviour, and
                // ProbeGround reads only GroundMask (field initializer `~0`)
                // and Physics — so a bare instance exercises the real method.
                var foot = new TbilisiFly.Character.FootController();
                var mi = typeof(TbilisiFly.Character.FootController)
                    .GetMethod("ProbeGround", System.Reflection.BindingFlags.Instance
                                            | System.Reflection.BindingFlags.NonPublic);
                // boxing a Nullable<T> that HasValue yields a boxed T, so a
                // non-null return here IS the tuple.
                var got = mi?.Invoke(foot, new object[] { target.x, target.z, target.y, 0.6f });
                if (got != null) footY = ((ValueTuple<float, Vector3>)got).Item1;
            }
            catch (Exception e) { Debug.LogWarning($"COLLIDABLE footProbe threw: {e.Message}"); }
            Debug.Log($"COLLIDABLE footProbeY={footY:F2} deckY={target.y:F2}");
            Check("consumer_footGroundProbe", !float.IsNaN(footY) && Mathf.Abs(footY - target.y) < 0.5f,
                  "FootController.ProbeGround found no ground on the road deck");

            // --- consumer 4: the footstep surface and every bullet strike.
            // src/footsteps.js:58-78 `_surfaceAt` and src/ballistics.js:380-432
            // `classifySurface` share one classifier, and its terrain branch
            // (ballistics.js:416-425) is the ONE place the JS consults the road
            // flag: "Road tiles are tagged as terrain too (the wheels rest on
            // them), so the shared asphalt material is the tell." The port is
            // Combat/SurfaceClassifier + IBallisticsWorld.IsRoadSurface, which
            // was hardcoded `=> false` in Core/CombatWorldAdapter.cs.
            var terrainSampler = CityGroundData.Shared()?.City?.Terrain;
            var adapter = new CombatWorldAdapter(terrainSampler,
                                                 UnityEngine.Object.FindFirstObjectByType<GameLoop>());
            bool gotRoad = HitsRoad(downAll, out var clsHit);
            string roadClass = gotRoad ? Safe(() => TbilisiFly.Combat.SurfaceClassifier
                                                 .Classify(clsHit, adapter, clsHit.point)) : "(no hit)";
            Debug.Log($"COLLIDABLE isRoadSurface={adapter.IsRoadSurface(asphalt).ToString().ToLowerInvariant()} " +
                      $"roadClass={roadClass}");
            Check("consumer_isRoadSurfaceFlag", adapter.IsRoadSurface(asphalt),
                  "IBallisticsWorld.IsRoadSurface is false for the asphalt deck — ballistics.js:420");
            Check("consumer_surfaceIsAsphalt", roadClass == "asphalt",
                  $"the road deck classifies as '{roadClass}', not asphalt");

            // and the same flag must NOT fire on bare terrain, or every patch
            // of grass in the city would sound and spark like tarmac.
            var tRoot = GameObject.Find("Terrain");
            Collider terrainTile = null;
            if (tRoot != null)
                for (int i = 0; i < tRoot.transform.childCount && terrainTile == null; i++)
                    terrainTile = tRoot.transform.GetChild(i).GetComponent<Collider>();
            Debug.Log($"COLLIDABLE terrainTile={(terrainTile != null ? terrainTile.name : "none")} " +
                      $"isRoadSurface={(terrainTile != null ? adapter.IsRoadSurface(terrainTile).ToString().ToLowerInvariant() : "n/a")}");
            Check("roadFlagNotOnBareTerrain", terrainTile != null && !adapter.IsRoadSurface(terrainTile),
                  "a plain terrain tile reads as a road surface");

            // --- consumer 5: bullets. src/guns.js:515, src/shooting.js:296,
            // src/rockets.js:227 and src/ballistics all intersect
            // `collidablesNear`; the ports are Combat/Guns.cs:207,
            // Combat/Ballistics.cs:389/:520, Combat/Rockets.cs with
            // TargetMask = ~0.
            var flat = (Quaternion.AngleAxis(20f, Vector3.up) * Vector3.forward);
            var shotFrom = target + Vector3.up * 1.4f - flat * 25f;
            var shotDir = (target + Vector3.up * 0.02f - shotFrom).normalized;
            var shotHits = Physics.RaycastAll(shotFrom, shotDir, 60f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(shotHits, (a, b) => a.distance.CompareTo(b.distance));
            Check("consumer_bullets", HitsRoad(shotHits, out _),
                  $"a grazing 25 m shot at the deck found {shotHits.Length} colliders, none the road");

            // --- consumer 6: the HUD boresight / target pick. src/hud.js:232
            // asks `collidablesNear(..., 2)` (two rings = a long ray) and
            // src/target.js:115/:288 the same set; the ports are
            // Core/GameLoop.cs:1655/:2717 and Combat/TargetRegistry.cs, all
            // Physics.RaycastAll with mask ~0 over a long range.
            var boreFrom = target + Vector3.up * 900f;
            var boreHits = Physics.RaycastAll(boreFrom, Vector3.down, 3000f, ~0, QueryTriggerInteraction.Ignore);
            Check("consumer_boresight", HitsRoad(boreHits, out _),
                  "a 3000 m boresight ray did not reach the road deck");

            // --- the OTHER half of the flag: the three layers the JS keeps OUT
            // of `collidables` must have NO collider, or a wheel would rest on
            // a kerbstone verge and a bullet would stop on a paint stripe.
            int nonCollidableWithCollider = 0, nonCollidableTiles = 0;
            foreach (string layer in new[] { "sidewalk", "verge", "markings" })
            {
                var root = roads.transform.Find(layer);
                if (root == null) continue;
                for (int i = 0; i < root.childCount; i++)
                {
                    nonCollidableTiles++;
                    if (root.GetChild(i).GetComponent<Collider>() != null) nonCollidableWithCollider++;
                }
            }
            Debug.Log($"COLLIDABLE nonCollidableTiles={nonCollidableTiles} withCollider={nonCollidableWithCollider}");
            Check("nonCollidableLayersHaveNoCollider", nonCollidableWithCollider == 0,
                  $"{nonCollidableWithCollider} sidewalk/verge/marking tiles carry a collider");

            // --- and the terrain flag must be exclusive to `ground: true`
            // tiles: citymap.js passes ground=false for bridge structures and
            // tunnel linings, which must not read as walkable terrain.
            var structure = roads.transform.Find("bridge_structure");
            Check("groundFlagNotOnStructure",
                  structure == null || structure.GetComponent<CombatTerrainTag>() == null,
                  "bridge_structure is tagged as terrain — citymap.js:874 ground=false");
        }

        static void Verdict()
        {
            Debug.Log($"GROUNDLOADPROBE RESULT={(_all ? "PASS" : "FAIL")}");
            ProbeExit.Exit(_all ? 0 : 1);
        }
    }
}
