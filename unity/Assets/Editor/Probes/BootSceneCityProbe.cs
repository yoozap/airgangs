using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using TbilisiFly.City;
using TbilisiFly.Core;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proof for the road / river / grass wiring — the layer that was
    /// probe-verified and drawn NOWHERE because `grep -c RoadBuilder` /
    /// `RiverSheet` / `GrassCover` in both scene builders was 0 and
    /// CityLoader's stages have no runtime caller.
    ///
    /// It exists because of a bootstrapping problem, not a preference:
    /// Editor/BuildPlayableScene.cs belongs to another agent this pass, so the
    /// three new renderers are not in the saved Game.unity yet. This probe does
    /// what the one line in that file will do — AddComponent + EnsureBuilt on a
    /// LOADED Game.unity, never saved — so the count lines and the PIXELS can
    /// be read before the scene builder changes.
    ///
    /// Three things it also settles, all asked for in the same brief:
    ///   * LandmarkRenderer submitted 0 draws per frame from everywhere. Ticked
    ///     here, with its per-frame counter printed, after the 1500 m chunking
    ///     fix (see LandmarkRenderer.StatueMaxDist's own header).
    ///   * whether the paper-target lanes, the three ammo crates and the crane
    ///     boom are in Game.unity at all — a census of what actually stands
    ///     inside the warehouse, by name.
    ///   * that the eight builders' own probes still pass, run in THIS launch
    ///     through ProbeExit.Suppress rather than costing eight more editor
    ///     locks.
    ///
    /// Run WITH a GPU for the pixels (drop -nographics):
    ///   Unity -batchmode -quit -projectPath unity \
    ///     -executeMethod TbilisiFly.Probes.BootSceneCityProbe.Run -logFile ...
    /// It degrades honestly without one: every count line still prints, the
    /// SHOT lines say nogpu.
    /// </summary>
    public static class BootSceneCityProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string OutDefault =
            "/private/tmp/claude-501/-Users-wazzap-tbilisifly/0f5488ea-9a85-4533-ad67-e5fe68c33649/scratchpad/shots";

        static string _out;
        static Camera _cam;
        /// <summary>The pixels of the frame <see cref="Shot"/> rendered last,
        /// so two shots can be differenced (see the water A/B).</summary>
        static Color32[] _lastPixels;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        /// <summary>The builders' own probes, re-run in this launch.</summary>
        static readonly string[] BuilderProbes =
        {
            "RoadProbe", "CityRoadsProbe", "BridgeProbe", "TunnelProbe",
            "RiverProbe", "CityInfraProbe", "InfraFurnitureProbe", "StreetFurnitureProbe",
        };

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);
            Debug.Log($"BOOTCITY out={_out} device={SystemInfo.graphicsDeviceType}");

            try
            {
                BuilderProbeSweep();
                SceneWork();
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"BOOTCITY id=exception msg={e.Message}");
                Debug.LogException(e);
            }

            Debug.Log($"BOOTCITY RESULT={(_allPass ? "PASS" : "FAIL")} wallMs={Wall.ElapsedMilliseconds}");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        // =====================================================================
        // 1. the builders' own probes, in this launch (VerifyPass4Probe's shape)
        // =====================================================================
        static void BuilderProbeSweep()
        {
            bool prev = ProbeExit.Suppress;
            ProbeExit.Suppress = true;
            var asm = typeof(BootSceneCityProbe).Assembly;
            int ok = 0, bad = 0, missing = 0;
            foreach (var n in BuilderProbes)
            {
                var t = asm.GetType("TbilisiFly.Probes." + n);
                var m = t?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) { missing++; Debug.Log($"BUILDERPROBE missing={n}"); continue; }
                ProbeExit.LastNonZero = 0;
                try
                {
                    m.Invoke(null, null);
                    if (ProbeExit.LastNonZero != 0) { bad++; Debug.Log($"BUILDERPROBE fail={n} code={ProbeExit.LastNonZero}"); }
                    else { ok++; Debug.Log($"BUILDERPROBE pass={n}"); }
                }
                catch (Exception e)
                {
                    bad++;
                    Debug.Log($"BUILDERPROBE threw={n} msg={(e.InnerException ?? e).Message}");
                }
            }
            ProbeExit.Suppress = prev;
            Debug.Log($"BUILDERPROBE total pass={ok} fail={bad} missing={missing}");
            Check("builderProbesStillPass", bad == 0);
        }

        // =====================================================================
        // 2. the real scene
        // =====================================================================
        static void SceneWork()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());
            if (!scene.IsValid()) return;

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            try { boot?.Run(); }
            catch (Exception e) { Debug.Log($"BOOTCITY id=bootthrew msg={e.Message}"); }

            // ---- the three new renderers, exactly as the scene builder will
            // add them: a GameObject under the same "Buildings" root every
            // other scenery renderer sits under, AddComponent, EnsureBuilt. ---
            var bRoot = GameObject.Find("Buildings");
            Check("buildingsRootExists", bRoot != null);
            Transform parent = bRoot != null ? bRoot.transform : null;

            var roadGo = NewChild("RoadNetworkRenderer", parent);
            var roads = roadGo.AddComponent<RoadNetworkRenderer>();
            roads.EnsureBuilt();

            var riverGo = NewChild("RiverRenderer", parent);
            var river = riverGo.AddComponent<RiverRenderer>();
            river.EnsureBuilt();

            var grassGo = NewChild("GrassRenderer", parent);
            var grass = grassGo.AddComponent<GrassRenderer>();
            grass.EnsureBuilt();

            Check("roadsBuilt", roads.LastReport != null && roads.LastReport.Surfaces > 0,
                  $"surfaces={(roads.LastReport?.Surfaces ?? -1)}");
            Check("riverSheetBuilt", river.LastReport != null && river.LastReport.SheetBuilt);
            Check("riverBanksPlaced", (river.LastReport?.FurniturePlaced ?? 0) > 0,
                  $"placed={(river.LastReport?.FurniturePlaced ?? -1)}");
            Check("grassChunksBuilt", (grass.LastReport?.Chunks ?? 0) > 0);
            Check("bridgesDrawn", roads.LastReport != null && roads.LastReport.SpanBays + roads.LastReport.RealBridges > 0);

            // ---- the double-draw regression: rebuilding with the children
            // still in place must not change the child count (GeneratedChildren
            // .cs's own contract). ---------------------------------------------
            int before = CountDescendants(roadGo) + CountDescendants(riverGo) + CountDescendants(grassGo);
            roads.RebuildForCheck(); river.RebuildForCheck(); grass.RebuildForCheck();
            int after = CountDescendants(roadGo) + CountDescendants(riverGo) + CountDescendants(grassGo);
            Debug.Log($"REBUILD before={before} after={after}");
            Check("noDoubleDraw", before == after, $"{before} -> {after}");

            // ---- THE PAIRED TERRAIN CHANGE ----------------------------------
            // Editor/BuildPlayableScene.cs:56 calls
            // TerrainBuilder.Build(city.Terrain) with no exclusion, so the 60 m
            // cells a river bank or a tunnel trench passes through are drawn
            // BOTH coarse (that call) and refined (GrassRenderer's cut_terrain
            // patch). The one-line fix is to pass
            // CityGroundData.RefinedCells(). It cannot be made here — that file
            // is another agent's this pass — so it is applied IN MEMORY, to the
            // live tile meshes, which is what the saved scene will look like
            // once the line lands. Reported either way.
            TerrainExclusion();

            // ---- landmarks: the 0-draws row --------------------------------
            var lr = UnityEngine.Object.FindFirstObjectByType<LandmarkRenderer>();
            Check("landmarkRendererInScene", lr != null);

            // ---- warehouse census ------------------------------------------
            WarehouseCensus();

            // ---- pixels ----------------------------------------------------
            _cam = PickCamera(game);
            Check("cameraExists", _cam != null);
            if (_cam != null)
            {
                EnvironmentSetup();
                Shots(roads, river, grass, lr);
            }
        }

        static GameObject NewChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        static int CountDescendants(GameObject go) =>
            go == null ? 0 : go.GetComponentsInChildren<Transform>(true).Length;

        // =====================================================================
        static void TerrainExclusion()
        {
            var cells = CityGroundData.RefinedCells();
            var ctx = CityGroundData.Shared();
            var tRoot = GameObject.Find("Terrain");
            if (tRoot == null || ctx?.City?.Terrain == null)
            {
                Debug.Log($"TERRAINEXCL id=skipped terrainRoot={(tRoot != null)} cells={cells.Count}");
                return;
            }

            int baked = tRoot.transform.childCount;
            var sw = Stopwatch.StartNew();
            var tiles = TerrainBuilder.Build(ctx.City.Terrain, cells);
            sw.Stop();

            var byName = new Dictionary<string, Transform>();
            for (int i = 0; i < tRoot.transform.childCount; i++)
            {
                var c = tRoot.transform.GetChild(i);
                byName[c.name] = c;
            }
            int swapped = 0, unmatched = 0;
            long trisBefore = 0, trisAfter = 0;
            foreach (var t in tiles)
            {
                if (!byName.TryGetValue($"tile_{t.Col}_{t.Row}", out var go)) { unmatched++; continue; }
                var mf = go.GetComponent<MeshFilter>();
                var mc = go.GetComponent<MeshCollider>();
                if (mf != null && mf.sharedMesh != null) trisBefore += mf.sharedMesh.GetIndexCount(0) / 3;
                if (mf != null) mf.sharedMesh = t.Mesh;
                if (mc != null) mc.sharedMesh = t.Mesh;
                trisAfter += t.Mesh.GetIndexCount(0) / 3;
                swapped++;
            }
            Debug.Log($"TERRAINEXCL bakedTiles={baked} rebuilt={tiles.Count} swapped={swapped} " +
                      $"unmatched={unmatched} excludedCells={cells.Count} " +
                      $"trisBefore={trisBefore} trisAfter={trisAfter} ms={sw.ElapsedMilliseconds}");
            Check("terrainExclusionApplies", swapped > 0 && unmatched == 0);
            Check("terrainExclusionRemovesTriangles", cells.Count == 0 || trisAfter < trisBefore,
                  $"{trisBefore} -> {trisAfter}");
        }

        // =====================================================================
        static void WarehouseCensus()
        {
            var shed = UnityEngine.Object.FindFirstObjectByType<TbilisiFly.Flight.ShedSceneFill>();
            Check("shedSceneFillExists", shed != null);
            if (shed == null) return;

            // Every Transform under the Sheds root, counted by name prefix.
            var all = shed.GetComponentsInChildren<Transform>(true);
            int crane = 0, craneBoom = 0, ammo = 0, range = 0, paper = 0, gate = 0, fan = 0, table = 0, gun = 0;
            var craneNames = new List<string>();
            foreach (var t in all)
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("crane")) { crane++; if (craneNames.Count < 12) craneNames.Add(t.name); }
                if (n.Contains("boom") || n.Contains("jib")) craneBoom++;
                if (n.Contains("ammo")) ammo++;
                if (n.Contains("range")) range++;
                if (n.Contains("paper")) paper++;
                if (n.Contains("gate")) gate++;
                if (n.Contains("fan")) fan++;
                if (n.Contains("table")) table++;
                if (n.Contains("makarov") || n.Contains("beretta") || n.Contains("akm")) gun++;
            }
            // RangeBootstrap.DiscoverAndAdoptAll is what GameLoop:404 runs; if
            // the geometry is absent it adopts nothing, which is the actual
            // verdict wanted here.
            int adopted = -1;
            try
            {
                TbilisiFly.Combat.RangeBootstrap.DiscoverAndAdoptAll();
                adopted = TbilisiFly.Combat.RangeBootstrap.All.Count;
            }
            catch (Exception e) { Debug.Log($"WAREHOUSE id=adoptthrew msg={e.Message}"); }

            Debug.Log($"WAREHOUSE descendants={all.Length} crane={crane} boomOrJib={craneBoom} " +
                      $"ammo={ammo} rangeNodes={range} paperNodes={paper} gateNodes={gate} fans={fan} " +
                      $"tables={table} guns={gun} adoptedRanges={adopted}");
            Debug.Log($"WAREHOUSE craneNames={string.Join("|", craneNames)}");
        }

        // =====================================================================
        static Camera PickCamera(GameLoop game)
        {
            if (game != null && game.MainCamera != null) return game.MainCamera;
            if (Camera.main != null) return Camera.main;
            return UnityEngine.Object.FindFirstObjectByType<Camera>();
        }

        static void EnvironmentSetup()
        {
            var env = UnityEngine.Object.FindFirstObjectByType<SceneEnvironmentSetup>();
            if (env == null) { Debug.Log("BOOTCITY id=noenv"); return; }
            try { env.Cam = _cam; env.Setup(); }
            catch (Exception e) { Debug.Log($"BOOTCITY id=envthrew msg={e.Message}"); }
            Debug.Log($"BOOTCITY env fov={_cam.fieldOfView:F1} near={_cam.nearClipPlane:F2} far={_cam.farClipPlane:F0}");
        }

        /// <summary>Every renderer that submits through
        /// Graphics.RenderMeshInstanced has to be ticked BY HAND in a batch
        /// render — LateUpdate is not delivered outside play mode — or its
        /// layer is simply absent from the frame and nothing says so
        /// (.claude/skills/unity/SKILL.md, "A probe camera only sees what
        /// ticked"). One TICKER line per renderer, and a throw reads as a
        /// throw, not as a pass.</summary>
        static readonly string[] TickTypes =
        {
            "TbilisiFly.City.CityRenderer", "TbilisiFly.City.ShopfrontRenderer",
            "TbilisiFly.City.VegetationRenderer", "TbilisiFly.City.ParkRenderer",
            "TbilisiFly.City.MetroRenderer", "TbilisiFly.City.MallRenderer",
            "TbilisiFly.City.StreetFurnitureRenderer", "TbilisiFly.City.LandmarkRenderer",
            "TbilisiFly.City.RoadNetworkRenderer", "TbilisiFly.City.RiverRenderer",
            "TbilisiFly.City.GrassRenderer",
        };

        class Ticker { public Component C; public string Name; public MethodInfo Tick; public MethodInfo Late; }
        static readonly List<Ticker> Tickers = new List<Ticker>();

        static void CollectTickers()
        {
            Tickers.Clear();
            foreach (var tn in TickTypes)
            {
                Type type = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { type = a.GetType(tn, false); if (type != null) break; }
                if (type == null) { Debug.Log($"TICKER id=missingtype name={tn}"); continue; }
                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                if (found == null || found.Length == 0) { Debug.Log($"TICKER id=noinstances type={type.Name}"); continue; }
                foreach (var o in found)
                {
                    if (!(o is Component c)) continue;
                    var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null)
                            ?? type.GetMethod("TickFrame", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null);
                    var late = type.GetMethod("LateUpdate",
                                   BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                   null, Type.EmptyTypes, null);
                    Tickers.Add(new Ticker { C = c, Name = type.Name, Tick = tick, Late = late });
                    Debug.Log($"TICKER id=found type={type.Name} go={c.gameObject.name} " +
                              $"hasTick={(tick != null).ToString().ToLowerInvariant()}");
                }
            }
            Debug.Log($"TICKER id=total n={Tickers.Count}");
        }

        static void TickAll(Vector3 camPos)
        {
            foreach (var t in Tickers)
            {
                try
                {
                    if (t.Tick != null) t.Tick.Invoke(t.C, new object[] { camPos, 0.5f });
                    else t.Late?.Invoke(t.C, null);
                }
                catch (Exception e)
                {
                    _allPass = false;
                    Debug.Log($"TICKER id=threw type={t.Name} msg={(e.InnerException ?? e).Message}");
                }
            }
        }

        static void Shots(RoadNetworkRenderer roads, RiverRenderer river, GrassRenderer grass, LandmarkRenderer lr)
        {
            CollectTickers();
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("SHOT id=nogpu msg=run without -nographics for pixels");
                // Still prove the per-frame submission counters move.
                TickAll(GeoFrame.FromLatLon(41.6880, 44.8090) + Vector3.up * 300f);
                DrawCounts("nogpu", roads, river, grass, lr);
                return;
            }

            var city = CityGroundData.Shared()?.City;
            Func<float, float, float> ground = (ux, uz) =>
                city?.Terrain == null ? 0f : city.Terrain.HeightAt(ux, uz);

            // The SAME framing RenderShotProbe uses for "oldtown", so the two
            // PNGs are directly comparable: this is the frame the Mtkvari was
            // missing from.
            Vector3 narikala = GeoFrame.FromLatLon(41.6880, 44.8090);
            float gy = ground(narikala.x, narikala.z);
            Vector3 otCam = new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f);
            Vector3 otLook = new Vector3(narikala.x, gy + 40f, narikala.z);

            // THE A/B. An olive-pixel count on its own cannot tell the river
            // from the terrain's own grey-green, so the river is proved the way
            // tools/_probe_realholes.mjs proves a hole: render the SAME frame
            // with the water surfaces switched off, then on, and read the
            // difference. Anything else in the frame is identical between the
            // two, so the delta IS the Mtkvari.
            SetWaterActive(river, false);
            Shot("oldtown_nowater", otCam, otLook, roads, river, grass, lr);
            var off = _lastPixels;
            SetWaterActive(river, true);
            Shot("oldtown_city", otCam, otLook, roads, river, grass, lr);
            var on = _lastPixels;
            int changed = DiffPixels(off, on, 24);
            float pct = off == null || off.Length == 0 ? 0f : 100f * changed / off.Length;
            Debug.Log($"WATERAB changedPx={changed} pct={pct:F3} of={off?.Length ?? 0}");
            // A COLOUR COUNT CANNOT DO THIS JOB and a first version that tried
            // was wrong twice over: before the grass layer landed the terrain's
            // own grey-green already scored 125k "olive" pixels with no river
            // drawn at all, and after it landed the grass scored 616k, swamping
            // the river's own contribution entirely. The only thing that
            // isolates one layer is the layer itself: render the identical
            // frame with the water surfaces off and on and count the pixels
            // that MOVED. Everything else in the two frames is the same object
            // at the same place under the same light.
            Check("riverCrossesOldTownFrame", pct > 1f, $"changed {pct:F3}% of the frame");

            // A low pass straight up the river at Metekhi/Baratashvili: the one
            // framing where the water surface, the quay walls and a bridge deck
            // are all in shot at once.
            Vector3 metekhi = GeoFrame.FromLatLon(41.6905, 44.8095);
            float my = ground(metekhi.x, metekhi.z);
            Shot("river_city", new Vector3(metekhi.x + 60f, my + 70f, metekhi.z - 420f),
                               new Vector3(metekhi.x, my + 5f, metekhi.z + 200f), roads, river, grass, lr);

            // Rustaveli — the avenue OSM tags highway=construction, which the
            // pipeline includes on purpose (docs: Roads & tunnels 2026-08b).
            Vector3 rustaveli = GeoFrame.FromLatLon(41.6977, 44.7982);
            float ry = ground(rustaveli.x, rustaveli.z);
            Shot("rustaveli_city", new Vector3(rustaveli.x + 150f, ry + 120f, rustaveli.z - 150f),
                                   new Vector3(rustaveli.x, ry + 5f, rustaveli.z), roads, river, grass, lr);

            var shed = UnityEngine.Object.FindFirstObjectByType<TbilisiFly.Flight.ShedSceneFill>();
            if (shed != null)
            {
                var b = Extent(shed.gameObject);
                if (b.HasValue)
                {
                    var c = b.Value.center;
                    float d = Mathf.Max(b.Value.extents.x, b.Value.extents.z) * 2.1f;
                    Shot("warehouse_city", c + new Vector3(d, b.Value.extents.y * 1.4f + 10f, d * 0.6f), c,
                         roads, river, grass, lr);
                }
            }
        }

        static Bounds? Extent(GameObject go)
        {
            bool any = false;
            Bounds b = default;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? b : (Bounds?)null;
        }

        /// <summary>Pixels whose colour moved by more than
        /// <paramref name="tol"/> on any channel between two renders of the
        /// same frame.</summary>
        static int DiffPixels(Color32[] a, Color32[] b, int tol)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;
            int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int dr = a[i].r - b[i].r, dg = a[i].g - b[i].g, db = a[i].b - b[i].b;
                if (dr < 0) dr = -dr; if (dg < 0) dg = -dg; if (db < 0) db = -db;
                if (dr > tol || dg > tol || db > tol) n++;
            }
            return n;
        }

        /// <summary>Switches just the water SURFACES (river sheet + lakes) off
        /// and on; the embankment kit and everything else stay put, so the A/B
        /// isolates the water and nothing else.</summary>
        static void SetWaterActive(RiverRenderer river, bool on)
        {
            if (river == null) return;
            foreach (var t in river.GetComponentsInChildren<Transform>(true))
                if (t.name == "river_sheet" || t.name == "water_bodies") t.gameObject.SetActive(on);
        }

        static int Shot(string id, Vector3 pos, Vector3 look,
            RoadNetworkRenderer roads, RiverRenderer river, GrassRenderer grass, LandmarkRenderer lr)
        {
            const int W = 1920, H = 1080, Warmup = 8;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1 };
            var dir = look - pos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, Vector3.up));
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            var sw = Stopwatch.StartNew();
            // Warm-up frames matter here and not only for exposure: CityCulling
            // sweeps a QUARTER of the chunks per call (citymap.js:1173's slices),
            // so a single tick leaves three quarters of every instanced layer
            // switched off.
            for (int i = 0; i <= Warmup; i++) { TickAll(pos); _cam.Render(); }
            sw.Stop();

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = active;
            _cam.targetTexture = prev;

            string file = Path.Combine(_out, id + ".png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Debug.Log($"SHOT file={file} cam=({pos.x:F1},{pos.y:F1},{pos.z:F1}) ms={sw.ElapsedMilliseconds}");
            _lastPixels = tex.GetPixels32();
            int olive = WaterPixels(id, tex);
            DrawCounts(id, roads, river, grass, lr);
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            return olive;
        }

        /// <summary>
        /// THE PIXEL PROOF the brief asks for: "The Mtkvari must cross the Old
        /// Town frame". A count line can be right while the frame is empty, so
        /// the water is proved the way tools/_probe_realholes.mjs proves a hole
        /// — by reading the rendered pixels. WaterBodies.WaterColor is a pale
        /// olive-jade (0x566440) nothing else in this frame is: terrain is grey-
        /// green (0.44,0.47,0.38) but far less saturated toward green-over-blue,
        /// and asphalt/concrete are neutral. The test is deliberately loose on
        /// brightness (tonemapping, fog and the sun all move it) and strict on
        /// HUE: green above both red and blue, and red above blue.
        /// </summary>
        static int WaterPixels(string id, Texture2D tex)
        {
            var px = tex.GetPixels32();
            int olive = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                if (p.g <= p.r || p.g <= p.b) continue;   // must be green-dominant
                if (p.r <= p.b) continue;                 // olive, not teal/blue
                if (p.g < 40 || p.g > 210) continue;      // not black, not blown out
                int gr = p.g - p.r, rb = p.r - p.b;
                if (gr < 6 || rb < 6) continue;           // not merely desaturated grey
                olive++;
            }
            float pct = 100f * olive / px.Length;
            Debug.Log($"WATERPX id={id} olivePx={olive} pct={pct:F3}");
            return olive;
        }

        static void DrawCounts(string id, RoadNetworkRenderer roads, RiverRenderer river,
            GrassRenderer grass, LandmarkRenderer lr)
        {
            Debug.Log($"DRAWS id={id} " +
                      $"roadsBatches={(roads != null ? roads.BatchCount : -1)} " +
                      $"roadsTickCalls={(roads != null ? roads.LastTickDrawCalls : -1)} " +
                      $"riverBatches={(river != null ? river.BatchCount : -1)} " +
                      $"riverTickCalls={(river != null ? river.LastTickDrawCalls : -1)} " +
                      $"grassChunks={(grass != null ? grass.ChunkCount : -1)} " +
                      $"grassVisible={(grass != null ? grass.VisibleChunks : -1)} " +
                      $"landmarkBatches={(lr != null ? lr.BatchCount : -1)} " +
                      $"landmarkInstances={(lr != null ? lr.TotalInstances : -1)} " +
                      $"landmarkTickCalls={(lr != null ? lr.LastTickDrawCalls : -1)}");
            // Only the CITY framings. The warehouse sits ~20 km from the
            // nearest street statue, so zero submissions there is the 2,200 m
            // cull (src/landmarks.js:1472) doing its job, not a regression —
            // asserting it would bake the very bug this pass fixed into the
            // probe from the other side.
            if (lr != null && id.EndsWith("_city") && !id.StartsWith("warehouse"))
                Check($"landmarksSubmit_{id}", lr.LastTickDrawCalls > 0,
                      $"instances={lr.TotalInstances} batches={lr.BatchCount}");
        }

        // =====================================================================
        static void Check(string id, bool ok, string note = null)
        {
            if (!ok) _allPass = false;
            Debug.Log($"BOOTCITY id={id} verdict={(ok ? "PASS" : "FAIL")}" +
                      (string.IsNullOrEmpty(note) ? "" : $" note={note}"));
        }
    }
}
