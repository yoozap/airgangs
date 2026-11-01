using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE FIRST RENDERED FRAMES OF THE UNITY PORT.
    ///
    /// Every verification in this project so far ran -nographics: ~400 probes,
    /// five regression sweeps and three reachability guards all passed while the
    /// scene was 1.78 GB, the game was silent, the fleet unboardable and the
    /// propellers absent. A probe proves a calculation. This one opens
    /// Assets/Scenes/Game.unity WITH a GPU, points the scene's own camera at a
    /// handful of places, renders into a RenderTexture and writes real PNGs,
    /// then does in Unity the pixel proof tools/_probe_realholes.mjs does in the
    /// JS build: read the rendered pixels, punch a hole, read them again.
    ///
    /// Run it WITH graphics (drop -nographics, keep -batchmode -quit):
    ///   Unity -batchmode -quit -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.RenderShotProbe.Run -logFile ...
    ///
    /// Output directory: $TF_SHOTS, else OutDefault below.
    ///
    /// TWO THINGS AN EDIT-MODE RENDER DOES NOT GET FOR FREE, BOTH HANDLED HERE:
    ///   * Awake/Start are never delivered, so Core/SceneEnvironmentSetup's
    ///     camera+sun+fog+sky setup has not run. Setup() is public and
    ///     idempotent; it is called explicitly, and the camera's before/after
    ///     values are logged.
    ///   * LateUpdate is never delivered, so EVERY Graphics.RenderMeshInstanced
    ///     renderer in City/ (the building kit, trees, parks, shopfronts, street
    ///     furniture, landmarks) submits NOTHING. Without this the frames show
    ///     only the baked scene GameObjects and read as "the city has no trees".
    ///     Each one is ticked by hand before every render, through its own
    ///     public Tick/TickFrame where it has one and by reflection on
    ///     LateUpdate where it does not — and their own per-frame draw counters
    ///     are read back afterwards, which is what settles whether
    ///     CITYRENDERER drawCalls=781149 is a per-frame figure or a total.
    ///
    /// WHAT IT DOES NOT DO: it never saves the scene. Materials are patched in
    /// memory by HoleSet.Patch (that is the feature under test) and MeshColliders
    /// are added by PlaneDamage.EnsureColliders; both die with the editor.
    /// </summary>
    public static class RenderShotProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string OutDefault =
            "/private/tmp/claude-501/-Users-wazzap-tbilisifly/0f5488ea-9a85-4533-ad67-e5fe68c33649/scratchpad/shots";

        static string _out;
        static Camera _cam;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        // ------------------------------------------------------------------
        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);
            Directory.CreateDirectory(Path.Combine(_out, "holes"));
            UnityEngine.Profiling.Profiler.enabled = true;
            Debug.Log($"RENDERSHOT out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"colorSpace={QualitySettings.activeColorSpace}");

            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("RENDERSHOT id=nogpu msg=graphics device is Null - needs a run WITHOUT -nographics");
                Debug.Log("RENDERSHOT RESULT=FAIL");
                ProbeExit.Exit(1);
                return;
            }

            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }

                var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
                Check("gameLoopExists", game != null);
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                Check("playableSceneBootExists", boot != null);
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"RENDERSHOT id=bootthrew msg={e.Message}"); Debug.LogException(e); }

                ShaderReport();
                PostProcessingReport();
                SceneFacts(game);
                _cam = PickCamera(game);
                Check("cameraExists", _cam != null);
                if (_cam == null) { Done(); return; }
                EnvironmentSetup();
                CollectTickers();
                StartRecorders();

                HeroPlaneReport(game);
                Shots(game);
                HolePixelProof(game);
                PropellerPixels(game);
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"RENDERSHOT id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        /// <summary>
        /// HOLES ONLY — the same scene load/boot/camera/environment as
        /// <see cref="Run"/>, then nothing but <see cref="HolePixelProof"/>.
        /// Run() renders 1920x1080 city shots and the propeller pass first,
        /// which is minutes per iteration when the thing under investigation is
        /// two airframes out of 35. `TF_HOLE_ONLY` (comma-separated plane ids)
        /// narrows the fleet further; unset means all of them.
        ///
        ///   Unity -batchmode -quit -projectPath unity (NO -nographics)
        ///        -executeMethod TbilisiFly.Probes.RenderShotProbe.Holes
        /// </summary>
        public static void Holes()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);
            Directory.CreateDirectory(Path.Combine(_out, "holes"));
            Debug.Log($"RENDERSHOT out={_out} device={SystemInfo.graphicsDeviceType} mode=holesOnly " +
                      $"only={Environment.GetEnvironmentVariable("TF_HOLE_ONLY") ?? "all"}");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("RENDERSHOT id=nogpu msg=graphics device is Null - needs a run WITHOUT -nographics");
                Debug.Log("RENDERSHOT RESULT=FAIL");
                ProbeExit.Exit(1);
                return;
            }
            // TF_NO_SRPBATCH=1 - a one-switch test of whether the hull shader's
            // tear state survives SRP batching. Every member of a shader's
            // UnityPerMaterial CBUFFER needs a matching entry in the Properties
            // block for the SRP Batcher to upload it, and ShaderLab has no
            // matrix property type, so PlaneHull's `_TearInv` could not have one.
            if (Environment.GetEnvironmentVariable("TF_NO_SRPBATCH") == "1")
            {
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = false;
                Debug.Log("RENDERSHOT id=srpbatch useScriptableRenderPipelineBatching=false");
            }
            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }
                var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"RENDERSHOT id=bootthrew msg={e.Message}"); Debug.LogException(e); }
                _cam = PickCamera(game);
                Check("cameraExists", _cam != null);
                if (_cam == null) { Done(); return; }
                EnvironmentSetup();
                CollectTickers();
                HolePixelProof(game);
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"RENDERSHOT id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        /// <summary>
        /// WAREHOUSE INTERIOR ONLY — the same scene load/boot/camera/environment
        /// as <see cref="Run"/>, then nothing but a handful of close shots of
        /// the two shooting ranges, the ammo crates and the three work tables.
        /// Added 2026-10-30 to PROVE the natural-import-transform fix in
        /// Props/WarehouseInteriorFill.cs with pixels: Run()'s own
        /// `warehouse_range` camera stands 9 m up at the crane end, and the
        /// range unit's own suspended ceiling (kept on purpose —
        /// tools/import_range.py:8-10 "the range's own CEILING is kept, because
        /// it is what the lane lights and the target rail hang from") then fills
        /// the frame as a flat roof, hiding the lanes, the cards, the crates and
        /// the tables behind it. These shots go UNDER it and along the hall.
        ///
        ///   TF_SHOTS=... Unity -batchmode -quit -projectPath unity
        ///        -executeMethod TbilisiFly.Probes.RenderShotProbe.WarehouseInterior
        /// </summary>
        public static void WarehouseInterior()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);
            Debug.Log($"RENDERSHOT out={_out} device={SystemInfo.graphicsDeviceType} mode=warehouseInterior");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("RENDERSHOT id=nogpu msg=graphics device is Null - needs a run WITHOUT -nographics");
                Debug.Log("RENDERSHOT RESULT=FAIL");
                ProbeExit.Exit(1);
                return;
            }
            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }
                var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"RENDERSHOT id=bootthrew msg={e.Message}"); Debug.LogException(e); }
                _cam = PickCamera(game);
                Check("cameraExists", _cam != null);
                if (_cam == null) { Done(); return; }
                EnvironmentSetup();
                CollectTickers();

                var wh = GameObject.Find("warehouse");
                if (wh == null) { Check("warehouseExists", false, "no GameObject named warehouse"); Done(); return; }
                var t = wh.transform;
                // shed-local -> world, the shed root being unrotated-scale 1:
                // x across the 48 m span, z along the 138 m length, y off the slab.
                Vector3 L(float x, float y, float z) => t.TransformPoint(new Vector3(x, y, z));

                // 1. EYE HEIGHT, down the lanes of the near range (its own
                //    shed-local x is -pitch/2 = -8.83, airport.js:733).
                Shot("wh_int_lanes", L(-8.83f, 1.7f, -12.5f), L(-8.83f, 1.4f, 6f));
                // 2. the firing line of the far range across the 60 cm slot
                Shot("wh_int_firingline", L(2.0f, 1.9f, -14f), L(8.83f, 1.2f, -4f));
                // 3. the three 9x18 crates at the user's dev spot (shed-local
                //    x=-5.21/-10.05, z=-15.1 after the GeoFrame mirror).
                Shot("wh_int_crates", L(-7.6f, 3.0f, -24f), L(-7.6f, 1.0f, -15f));
                // 4. the three work tables + the two pistols and the AKM on
                //    them (shed-local x=+19.5, z=-65/-62/-59).
                Shot("wh_int_tables", L(14.0f, 2.6f, -68f), L(19.5f, 1.1f, -62f));
                // 5. one close pass over the first table: makarov + beretta.
                Shot("wh_int_bench", L(18.2f, 1.75f, -66.6f), L(19.6f, 1.22f, -65.0f));
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"RENDERSHOT id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        /// <summary>
        /// SCALE SCAN (no GPU needed). The hull shader tests
        /// `distance(tearPosLocal, t.xyz) &lt; t.w` (Shaders/PlaneHull.shader) where
        /// `t.xyz` is the opening's position in the AIRFRAME'S OWN LOCAL FRAME
        /// (PlaneDamage.cs:807 `w.InverseTransformPoint`) while `t.w` is a radius
        /// in METRES (PlaneDamage.cs:865 TearRadius, a calibre times a constant).
        /// A local distance is a world distance divided by the root's scale, so
        /// the opening a plane actually loses is `R * lossyScale` wide. This
        /// prints every parked machine's scale so "which planes can show a hole
        /// at all" can be read off directly.
        ///
        ///   Unity -batchmode -quit -nographics -projectPath unity
        ///        -executeMethod TbilisiFly.Probes.RenderShotProbe.Scales
        /// </summary>
        public static void Scales()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.Log("PLANESCALE RESULT=FAIL"); ProbeExit.Exit(1); return; }
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            try { boot?.Run(); } catch (Exception e) { Debug.LogException(e); }
            var fleet = GameObject.Find("planehall_fleet");
            if (fleet == null) { Debug.Log("PLANESCALE id=setup fleet=missing"); ProbeExit.Exit(1); return; }
            // a 9x19 round's first opening, the smallest thing the pixel proof uses
            float r9 = PlaneDamage.TearRadius(0.009f, 1);
            float r127 = PlaneDamage.TearRadius(0.0127f, 3);
            int small = 0;
            for (int i = 0; i < fleet.transform.childCount; i++)
            {
                var t = fleet.transform.GetChild(i);
                Vector3 ls = t.lossyScale;
                float sc = (ls.x + ls.y + ls.z) / 3f;
                if (sc < 0.5f) small++;
                Debug.Log($"PLANESCALE id={t.gameObject.name} lossy=({ls.x:F4},{ls.y:F4},{ls.z:F4}) " +
                          $"localScale=({t.localScale.x:F4},{t.localScale.y:F4},{t.localScale.z:F4}) " +
                          $"holeR9mm_m={r9:F4} effectiveR9mm_m={(r9 * sc):F5} effectiveR127mm_m={(r127 * sc):F5}");
            }
            Debug.Log($"PLANESCALE id=total planes={fleet.transform.childCount} belowHalfScale={small}");
            ProbeExit.Exit(0);
        }

        static void Done()
        {
            Debug.Log($"RENDERSHOT wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "RENDERSHOT RESULT=PASS" : "RENDERSHOT RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"RSCHECK {key}={(cond ? "PASS" : "FAIL")}";
            if (note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        // ==================================================================
        // every shader this project ships: does it actually compile?
        // ==================================================================
        static void ShaderReport()
        {
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });
            int bad = 0, n = 0;
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (sh == null) continue;
                n++;
                bool err = ShaderUtil.ShaderHasError(sh);
                int msgs = ShaderUtil.GetShaderMessageCount(sh);
                if (err || !sh.isSupported) bad++;
                Debug.Log($"SHADER name={sh.name} supported={sh.isSupported.ToString().ToLowerInvariant()} " +
                          $"hasErrors={err.ToString().ToLowerInvariant()} messages={msgs} path={path}");
                if (msgs > 0)
                    foreach (var m in ShaderUtil.GetShaderMessages(sh))
                        Debug.Log($"SHADERMSG shader={sh.name} severity={m.severity} line={m.line} msg={m.message}");
            }
            Debug.Log($"SHADER id=total shaders={n} broken={bad}");
            Check("allProjectShadersCompile", bad == 0, $"broken={bad}");
        }

        /// <summary>game.js:365-406's ACES tonemap + bloom live on a URP Volume.
        /// A camera with renderPostProcessing off, or a scene with no Volume,
        /// renders the raw values - which is what a blown-out white frame looks
        /// like.</summary>
        static void PostProcessingReport()
        {
            var volType = FindType("UnityEngine.Rendering.Volume");
            int vols = 0;
            if (volType != null)
            {
                var found = UnityEngine.Object.FindObjectsByType(volType, FindObjectsSortMode.None) ?? Array.Empty<UnityEngine.Object>();
                vols = found.Length;
                foreach (var v in found)
                {
                    object prof = null;
                    try { prof = volType.GetProperty("profile")?.GetValue(v); } catch { }
                    var isGlobal = volType.GetField("isGlobal");
                    Debug.Log($"POST id=volume go={((Component)v).gameObject.name} " +
                              $"global={(isGlobal != null ? isGlobal.GetValue(v) : null)} " +
                              $"profile={(prof != null ? prof.ToString() : "null")}");
                }
            }
            bool defProf = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/Resources/DefaultVolumeProfile.asset") != null;
            Debug.Log($"POST id=volumes count={vols} volumeTypeFound={(volType != null).ToString().ToLowerInvariant()} " +
                      $"defaultProfileAsset={defProf.ToString().ToLowerInvariant()}");
            Debug.Log($"POST id=pipeline default={(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.name : "null")} " +
                      $"quality={(QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.name : "null")}");
        }

        static void CameraPostFacts(string when)
        {
            var t = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            string post = "n/a", aa = "n/a", vol = "n/a";
            if (t != null && _cam != null)
            {
                var c = _cam.GetComponent(t);
                if (c != null)
                {
                    try { post = t.GetProperty("renderPostProcessing")?.GetValue(c)?.ToString().ToLowerInvariant(); } catch { }
                    try { aa = t.GetProperty("antialiasing")?.GetValue(c)?.ToString(); } catch { }
                    try { vol = t.GetProperty("volumeLayerMask")?.GetValue(c)?.ToString(); } catch { }
                }
                else post = "no-UniversalAdditionalCameraData";
            }
            Debug.Log($"POST id=camera when={when} renderPostProcessing={post} antialiasing={aa} volumeLayerMask={vol} " +
                      $"fov={_cam.fieldOfView:F1} near={_cam.nearClipPlane:F2} far={_cam.farClipPlane:F0} " +
                      $"tag={_cam.tag} isMain={(Camera.main == _cam).ToString().ToLowerInvariant()} " +
                      $"fog={RenderSettings.fog.ToString().ToLowerInvariant()} fogDensity={RenderSettings.fogDensity:F6} " +
                      $"ambientMode={RenderSettings.ambientMode} ambientIntensity={RenderSettings.ambientIntensity:F2} " +
                      $"skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")}");
        }

        static void EnvironmentSetup()
        {
            CameraPostFacts("before_setup");
            var env = UnityEngine.Object.FindFirstObjectByType<SceneEnvironmentSetup>();
            Check("sceneEnvironmentSetupExists", env != null);
            if (env != null)
            {
                // Awake() is never delivered outside play mode, so the real
                // game.js:242-339 setup has not run on this camera. Setup() is
                // public and idempotent precisely for this. No light is added:
                // it reuses the scene's own sun object.
                env.Cam = _cam;
                env.Setup();
                if (env.Sun != null)
                    Debug.Log($"POST id=sun intensity={env.Sun.intensity:F2} color={env.Sun.color} " +
                              $"shadows={env.Sun.shadows} strength={env.Sun.shadowStrength:F2} " +
                              $"fwd={env.Sun.transform.forward.ToString("F3")}");
            }
            CameraPostFacts("after_setup");
            // game.js:365-406's ACES tonemap + bloom + SSAO are configured on
            // Assets/Resources/DefaultVolumeProfile.asset, but a camera only runs the post
            // stack when its UniversalAdditionalCameraData says so. No component,
            // no post.
            var ucd = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            bool postOn = false;
            if (ucd != null)
            {
                var c = _cam.GetComponent(ucd);
                if (c != null)
                    try { postOn = (bool)ucd.GetProperty("renderPostProcessing").GetValue(c); } catch { }
            }
            Check("cameraRunsPostProcessing", postOn,
                "the ACES tonemap/bloom/SSAO on DefaultVolumeProfile.asset only run when the camera asks for them");
            if (_cam.tag != "MainCamera")
            {
                // Every City renderer's LateUpdate culls against Camera.main;
                // without this they would all fall into their "no camera ->
                // never cull" branch and submit the WHOLE city every frame,
                // which would make the draw numbers below meaningless.
                try { _cam.tag = "MainCamera"; Debug.Log("POST id=camera_tagged_main"); } catch { }
            }
        }

        // ==================================================================
        // the per-frame instanced renderers (no LateUpdate in edit mode)
        // ==================================================================
        static readonly string[] RendererTypeNames =
        {
            "TbilisiFly.City.RoadNetworkRenderer", "TbilisiFly.City.RiverRenderer", "TbilisiFly.City.GrassRenderer",
            "TbilisiFly.City.CityRenderer", "TbilisiFly.City.ParkRenderer",
            "TbilisiFly.City.VegetationRenderer", "TbilisiFly.City.ShopfrontRenderer",
            "TbilisiFly.City.StreetFurnitureRenderer", "TbilisiFly.City.LandmarkRenderer",
            "TbilisiFly.City.MetroRenderer", "TbilisiFly.City.MallRenderer",
            "TbilisiFly.City.WindsockRenderer", "TbilisiFly.City.PlaneHallRenderer",
        };

        sealed class Ticker
        {
            public Component C;
            public string Name;
            public MethodInfo Tick;       // (Vector3, float)
            public MethodInfo LateUpdate; // ()
        }

        static readonly List<Ticker> Tickers = new List<Ticker>();

        static void CollectTickers()
        {
            Tickers.Clear();
            foreach (var tn in RendererTypeNames)
            {
                var type = FindType(tn);
                if (type == null) { Debug.Log($"TICKER id=missingtype name={tn}"); continue; }
                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                if (found == null) { Debug.Log($"TICKER id=noinstances type={type.Name}"); continue; }
                foreach (var o in found)
                {
                    var c = o as Component;
                    if (c == null) continue;
                    var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null)
                            ?? type.GetMethod("TickFrame", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null);
                    var lu = type.GetMethod("LateUpdate",
                                   BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                   null, Type.EmptyTypes, null);
                    Tickers.Add(new Ticker { C = c, Name = type.Name, Tick = tick, LateUpdate = lu });
                    Debug.Log($"TICKER id=found type={type.Name} go={c.gameObject.name} " +
                              $"hasTick={(tick != null).ToString().ToLowerInvariant()} " +
                              $"hasLateUpdate={(lu != null).ToString().ToLowerInvariant()}");
                }
            }
            Debug.Log($"TICKER id=total n={Tickers.Count}");
            Check("instancedRenderersFound", Tickers.Count > 0);
        }

        static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full, false);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>One frame's worth of instanced submission.</summary>
        static void TickRenderers()
        {
            Vector3 p = _cam.transform.position;
            foreach (var t in Tickers)
            {
                try
                {
                    if (t.Tick != null) t.Tick.Invoke(t.C, new object[] { p, 0.5f });
                    else t.LateUpdate?.Invoke(t.C, null);
                }
                catch (Exception e) { Debug.Log($"TICKER id=threw type={t.Name} msg={e.Message}"); }
            }
        }

        static void RendererDraws(string id)
        {
            string[] props = { "FrameBatches", "FrameDrawCalls", "LastTickDrawCalls", "LastTickInstances",
                               "DrawCallsIssuedLastTick", "InstancesSubmittedLastTick",
                               "BatchCount", "TotalDrawCalls", "TotalInstances" };
            foreach (var t in Tickers)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"RDRAW id={id} type={t.Name}");
                var type = t.C.GetType();
                bool any = false;
                foreach (var pn in props)
                {
                    var pi = type.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public);
                    if (pi == null) continue;
                    object v = null;
                    try { v = pi.GetValue(t.C); } catch { }
                    sb.Append($" {pn}={v}");
                    any = true;
                }
                if (any) Debug.Log(sb.ToString());
            }
        }

        // ==================================================================
        // Real GPU counters. UnityStats is fed only by the editor's own repaint
        // loop and reads 0 for a manual Camera.Render, so the per-frame numbers
        // come from ProfilerRecorder instead - and UnityStats is printed too, so
        // its zeros are on the record rather than quietly dropped.
        // ==================================================================
        static readonly string[] RecorderNames =
        {
            "Draw Calls Count", "SetPass Calls Count", "Total Batches Count",
            "Triangles Count", "Vertices Count", "Shadow Casters Count",
            "Render Textures Count",
        };
        static readonly List<string> RecNames = new List<string>();
        static readonly List<Unity.Profiling.ProfilerRecorder> Recs = new List<Unity.Profiling.ProfilerRecorder>();

        static void StartRecorders()
        {
            foreach (var n in RecorderNames)
            {
                try
                {
                    var r = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, n);
                    RecNames.Add(n); Recs.Add(r);
                }
                catch (Exception e) { Debug.Log($"RECORDER id=failed name={n} msg={e.Message}"); }
            }
            var valid = new List<string>();
            for (int i = 0; i < Recs.Count; i++) if (Recs[i].Valid) valid.Add(RecNames[i]);
            Debug.Log($"RECORDER id=started n={Recs.Count} valid={string.Join("|", valid)}");
        }

        static void Stats(string id)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"STATS id={id}");
            for (int i = 0; i < Recs.Count; i++)
            {
                long v = -1;
                try { if (Recs[i].Valid) v = Recs[i].LastValue; } catch { }
                sb.Append($" {RecNames[i].Replace(' ', '_')}={v}");
            }
            sb.Append($" UnityStats.drawCalls={UnityStats.drawCalls}");
            sb.Append($" UnityStats.setPassCalls={UnityStats.setPassCalls}");
            sb.Append($" UnityStats.triangles={UnityStats.triangles}");
            sb.Append($" UnityStats.visibleSkinnedMeshes={UnityStats.visibleSkinnedMeshes}");
            Debug.Log(sb.ToString());
        }

        // ==================================================================
        // what the scene actually contains after OnEnable -> EnsureBuilt
        // ==================================================================
        static void SceneFacts(GameLoop game)
        {
            var shed = UnityEngine.Object.FindFirstObjectByType<ShedSceneFill>();
            Debug.Log($"SCENEFACT shedSceneFill={(shed != null).ToString().ToLowerInvariant()} " +
                      $"built={(shed != null && shed.Built).ToString().ToLowerInvariant()} " +
                      $"parked={(shed != null && shed.Fleet.Parked != null ? shed.Fleet.Parked.Count : -1)}");

            var fleet = GameObject.Find("planehall_fleet");
            Debug.Log($"SCENEFACT planehall_fleet={(fleet != null).ToString().ToLowerInvariant()} " +
                      $"active={(fleet != null && fleet.activeInHierarchy).ToString().ToLowerInvariant()} " +
                      $"children={(fleet != null ? fleet.transform.childCount : -1)}");

            var tags = UnityEngine.Object.FindObjectsByType<LandmarkTag>(FindObjectsSortMode.None);
            var recs = UnityEngine.Object.FindObjectsByType<PlaneDamageRecord>(FindObjectsSortMode.None);
            Debug.Log($"SCENEFACT landmarkTags={tags.Length} planeDamageRecords={recs.Length} " +
                      $"renderers={UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length} " +
                      $"lights={UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length}");
        }

        static void HeroPlaneReport(GameLoop game)
        {
            var plane = game != null ? game.ActivePlane : null;
            if (plane == null) { Check("activePlaneExists", false); return; }
            var t = plane.transform;
            var bb = WorldBounds(plane.gameObject);
            Debug.Log($"HERO id=plane go={plane.gameObject.name} pos={t.position.ToString("F2")} " +
                      $"localScale={t.localScale.ToString("F4")} lossyScale={t.lossyScale.ToString("F4")} " +
                      $"boundsSize={(bb.HasValue ? bb.Value.size.ToString("F2") : "none")} children={t.childCount}");
            int shown = 0;
            foreach (var r in plane.GetComponentsInChildren<Renderer>(true))
            {
                if (shown++ >= 25) break;
                Debug.Log($"HERO id=renderer name={r.name} enabled={r.enabled.ToString().ToLowerInvariant()} " +
                          $"size={r.bounds.size.ToString("F2")} lossy={r.transform.lossyScale.ToString("F3")} " +
                          $"shader={(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "null")}");
            }
            var cityH = CityData.Load(buildings: false);
            float g = (cityH != null && cityH.Terrain != null) ? cityH.Terrain.HeightAt(t.position.x, t.position.z) : float.NaN;
            Debug.Log($"HERO id=ground terrainHeightAtSpawn={g:F2} planeY={t.position.y:F2} aboveGround={(t.position.y - g):F2} " +
                      $"boundsMinY={(bb.HasValue ? bb.Value.min.y : -1f):F2} boundsMaxY={(bb.HasValue ? bb.Value.max.y : -1f):F2}");
            // What is directly above the aeroplane? A hit here means it is under
            // something, which is exactly what a probe can never see and a
            // rendered frame shows at once.
            var ups = Physics.RaycastAll(t.position + Vector3.up * 0.2f, Vector3.up, 60f);
            Debug.Log($"HERO id=above n={ups.Length}");
            foreach (var u in ups)
                Debug.Log($"HERO id=above_hit collider={u.collider.name} root={u.collider.transform.root.name} dist={u.distance:F2} y={u.point.y:F2}");
            var downs = Physics.RaycastAll(t.position + Vector3.up * 60f, Vector3.down, 80f);
            Debug.Log($"HERO id=below n={downs.Length}");
            foreach (var d in downs)
                Debug.Log($"HERO id=below_hit collider={d.collider.name} root={d.collider.transform.root.name} y={d.point.y:F2}");
            Check("heroPlaneIsAeroplaneSized",
                bb.HasValue && Mathf.Max(bb.Value.size.x, bb.Value.size.z) > 5f,
                bb.HasValue ? $"size={bb.Value.size.ToString("F2")}" : "no renderers");
        }

        static Camera PickCamera(GameLoop game)
        {
            Camera cam = game != null ? game.MainCamera : null;
            if (cam == null) cam = Camera.main;
            if (cam == null)
            {
                var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                if (all.Length > 0) cam = all[0];
            }
            if (cam != null) cam.enabled = true;
            return cam;
        }

        // ==================================================================
        // the render helper
        // ==================================================================
        sealed class Frame
        {
            public Color32[] Px;
            public int W, H;
            public Color32 At(int x, int y) => Px[y * W + x];
        }

        static Texture2D _lastTex;
        static RenderTexture _lastRt;

        /// <summary>Render the scene's own camera into an offscreen target and
        /// read it back. `warmup` extra frames let the per-frame culling
        /// partitions settle (they sweep in slices) and let URP's post stack
        /// build its history before the frame that is kept.</summary>
        static Vector3 _upHint = Vector3.up;

        static Frame Render(Vector3 pos, Vector3 look, int w, int h, int warmup = 1, Action extra = null)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1 };
            var dir = look - pos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, _upHint));
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            for (int i = 0; i <= warmup; i++)
            {
                extra?.Invoke();
                TickRenderers();
                _cam.Render();
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = active;
            _cam.targetTexture = prev;

            _lastTex = tex;
            _lastRt = rt;
            return new Frame { Px = tex.GetPixels32(), W = w, H = h };
        }

        static void Save(string file)
        {
            if (_lastTex == null) return;
            File.WriteAllBytes(file, _lastTex.EncodeToPNG());
        }

        static void Free()
        {
            if (_lastTex != null) { UnityEngine.Object.DestroyImmediate(_lastTex); _lastTex = null; }
            if (_lastRt != null) { _lastRt.Release(); UnityEngine.Object.DestroyImmediate(_lastRt); _lastRt = null; }
        }

        static int Magenta(Frame f)
        {
            int m = 0;
            for (int i = 0; i < f.Px.Length; i++)
            {
                var c = f.Px[i];
                if (c.r > 220 && c.b > 220 && c.g < 60) m++;
            }
            return m;
        }

        /// <summary>A frame that is one flat colour is a BLANK render, and a probe
        /// that passes on one is the project's known false-pass shape. Also counts
        /// Unity's error-material magenta (1,0,1) - a missing or failed shader -
        /// and pure white, which is what a blown-out exposure looks like.</summary>
        static void FrameStats(string id, Frame f, bool assert = true)
        {
            long rs = 0, gs = 0, bs = 0;
            int magenta = 0, dark = 0, blown = 0;
            var seen = new HashSet<int>();
            for (int i = 0; i < f.Px.Length; i++)
            {
                var c = f.Px[i];
                rs += c.r; gs += c.g; bs += c.b;
                if (c.r > 220 && c.b > 220 && c.g < 60) magenta++;
                if (c.r < 8 && c.g < 8 && c.b < 8) dark++;
                if (c.r > 250 && c.g > 250 && c.b > 250) blown++;
                if (seen.Count < 4096) seen.Add((c.r >> 3) << 10 | (c.g >> 3) << 5 | (c.b >> 3));
            }
            int n = f.Px.Length;
            double mr = (double)rs / n, mg = (double)gs / n, mb = (double)bs / n;
            double var0 = 0;
            for (int i = 0; i < f.Px.Length; i += 7)
            {
                var c = f.Px[i];
                var0 += (c.r - mr) * (c.r - mr) + (c.g - mg) * (c.g - mg) + (c.b - mb) * (c.b - mb);
            }
            var0 /= (n / 7 + 1);
            Debug.Log($"FRAME id={id} meanRGB={mr:F1},{mg:F1},{mb:F1} variance={var0:F1} buckets={seen.Count} " +
                      $"magentaPx={magenta} magentaPct={(100.0 * magenta / n):F3} " +
                      $"blownWhitePct={(100.0 * blown / n):F2} blackPct={(100.0 * dark / n):F2}");
            if (!assert) return;
            Check($"frameNotBlank_{id}", seen.Count > 40 && var0 > 25.0, $"buckets={seen.Count} var={var0:F1}");
            Check($"noErrorMagenta_{id}", magenta * 1000L < n, $"magentaPx={magenta}");
        }

        // ==================================================================
        // part 1 - the views
        // ==================================================================
        static void Shots(GameLoop game)
        {
            var city = CityData.Load(buildings: false);
            Func<float, float, float> ground = (ux, uz) =>
                (city == null || city.Terrain == null) ? 0f : city.Terrain.HeightAt(ux, uz);

            var plane = game != null ? game.ActivePlane : null;
            if (plane != null)
            {
                var pt = plane.transform;
                Vector3 p = pt.position, fwd = pt.forward;
                Shot("spawn", p - fwd * 26f + Vector3.up * 9f, p + fwd * 60f + Vector3.up * 2f);
                // ...and a close-up, because a chase shot cannot tell a small
                // aeroplane from a distant one.
                Shot("heroplane", p + pt.right * 14f + Vector3.up * 4f, p);
                // straight down from 55 m: the one framing that cannot hide an
                // aeroplane behind, under or inside anything.
                Shot("heroplane_top", p + Vector3.up * 55f, p, pt.forward);
            }
            else Check("spawnShot", false, "no ActivePlane");

            Vector3 narikala = GeoFrame.FromLatLon(41.6880, 44.8090);
            float gy = ground(narikala.x, narikala.z);
            Shot("oldtown", new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f),
                            new Vector3(narikala.x, gy + 40f, narikala.z));
            Debug.Log($"SITE id=narikala unity={narikala.ToString("F1")} groundY={gy:F1}");

            var shed = UnityEngine.Object.FindFirstObjectByType<ShedSceneFill>();
            var hall = GameObject.Find("planehall");
            if (hall != null)
            {
                float hl = shed != null ? shed.Hall.Shell.Length : 220f;
                var t = hall.transform;
                Shot("planehall", t.position + t.forward * (-hl * 0.46f) + Vector3.up * 11f,
                                  t.position + t.forward * (hl * 0.40f) + Vector3.up * 4f);
                Debug.Log($"SITE id=planehall pos={t.position.ToString("F1")} length={hl:F1}");
            }
            else Check("planehallShot", false, "no GameObject named planehall");

            var crane = GameObject.Find("warehouse_crane");
            var wh = GameObject.Find("warehouse");
            if (wh != null)
            {
                float wl = shed != null ? shed.Warehouse.Shell.Length : 80f;
                var t = wh.transform;
                // framed on the crane end, where the crane, the slung P-51, the
                // three fans and the range all are.
                Vector3 target = crane != null ? crane.transform.position + Vector3.up * 4f
                                               : t.position + t.forward * (wl * 0.30f);
                Shot("warehouse", target - t.forward * 42f + Vector3.up * 7f, target);
                // The INTERIOR is in the other direction: airport.js:733 puts the
                // two ranges at the shed's MIDDLE (shed-local z=0), the crates at
                // z=-15 and the tables at z=-60 (the AIRPORTDETAIL_* lines) — all
                // behind the camera above, which stands ~2 m from the middle
                // looking at the crane end. That is why warehouse.png showed no
                // lane, crate or table while the log said they were built
                // (2026-10-30). Stand at the crane end and look back down the hall.
                Shot("warehouse_range", t.position + t.forward * 34f + Vector3.up * 9f,
                                        t.position - t.forward * 14f + Vector3.up * 1.5f);
                Debug.Log($"SITE id=warehouse pos={t.position.ToString("F1")} length={wl:F1} " +
                          $"crane={(crane != null).ToString().ToLowerInvariant()} " +
                          $"cranePos={(crane != null ? crane.transform.position.ToString("F1") : "n/a")}");
                var hub = GameObject.Find("propeller_0");
                Debug.Log($"SITE id=slung_hub found={(hub != null).ToString().ToLowerInvariant()} " +
                          $"pos={(hub != null ? hub.transform.position.ToString("F2") : "n/a")}");
                var fan1 = GameObject.Find("warehouse_fan_1");
                Debug.Log($"SITE id=fans fan1={(fan1 != null).ToString().ToLowerInvariant()} " +
                          $"pos={(fan1 != null ? fan1.transform.position.ToString("F1") : "n/a")}");
            }
            else Check("warehouseShot", false, "no GameObject named warehouse");

            var tags = UnityEngine.Object.FindObjectsByType<LandmarkTag>(FindObjectsSortMode.None);
            LandmarkTag pick = null;
            foreach (var t in tags)
                if ((t.LandmarkName ?? "").Contains("იუსტიცი")) { pick = t; break; }
            if (pick == null && tags.Length > 0) pick = tags[0];
            if (pick != null)
            {
                var b = WorldBounds(pick.gameObject);
                Vector3 ctr = b.HasValue ? b.Value.center : pick.transform.position;
                Vector3 ext = b.HasValue ? b.Value.extents : new Vector3(20f, 20f, 20f);
                float d = Mathf.Max(45f, ext.magnitude * 1.7f);
                Shot("landmarks", ctr + new Vector3(d * 0.75f, Mathf.Max(22f, ext.y * 1.6f), d * 0.66f), ctr);
                Debug.Log($"SITE id=landmark name={pick.LandmarkName} center={ctr.ToString("F1")} extents={ext.ToString("F1")}");
            }
            else Check("landmarkShot", false, "no LandmarkTag in the scene at all");

            // ---- the same two frames with the post stack FORCED ON ----
            // Not how the scene is configured: this adds the missing
            // UniversalAdditionalCameraData and a global Volume carrying
            // Assets/Resources/DefaultVolumeProfile.asset, purely so the report can show
            // what the ACES tonemap is worth. Both are torn down afterwards and
            // the scene is never saved.
            if (ForcePostOn())
            {
                Shot("oldtown_post", new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f),
                                     new Vector3(narikala.x, gy + 40f, narikala.z));
                if (hall != null)
                {
                    float hl2 = shed != null ? shed.Hall.Shell.Length : 220f;
                    var t2 = hall.transform;
                    Shot("planehall_post", t2.position + t2.forward * (-hl2 * 0.46f) + Vector3.up * 11f,
                                           t2.position + t2.forward * (hl2 * 0.40f) + Vector3.up * 4f);
                }
                ForcePostOff();
            }
        }

        static GameObject _probeVolume;

        static bool ForcePostOn()
        {
            var ucd = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            var volType = FindType("UnityEngine.Rendering.Volume");
            if (ucd == null || volType == null) { Debug.Log("POST id=force unavailable"); return false; }
            var c = _cam.GetComponent(ucd) ?? _cam.gameObject.AddComponent(ucd);
            try { ucd.GetProperty("renderPostProcessing").SetValue(c, true); }
            catch (Exception e) { Debug.Log($"POST id=force_failed msg={e.Message}"); return false; }

            var profile = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/Resources/DefaultVolumeProfile.asset");
            _probeVolume = new GameObject("probe_volume");
            var v = _probeVolume.AddComponent(volType);
            bool okGlobal = SetMember(volType, v, "isGlobal", true);
            bool okPrio = SetMember(volType, v, "priority", 100f);
            bool okProf = profile != null && (SetMember(volType, v, "sharedProfile", profile)
                                           || SetMember(volType, v, "profile", profile));
            Debug.Log($"POST id=force_volume isGlobal={okGlobal.ToString().ToLowerInvariant()} " +
                      $"priority={okPrio.ToString().ToLowerInvariant()} profile={okProf.ToString().ToLowerInvariant()}");
            Debug.Log($"POST id=forced_on profile={(profile != null ? profile.name : "null")}");
            return true;
        }

        static bool SetMember(Type t, object o, string name, object val)
        {
            try
            {
                var pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (pi != null && pi.CanWrite) { pi.SetValue(o, val); return true; }
                var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (fi != null) { fi.SetValue(o, val); return true; }
            }
            catch (Exception e) { Debug.Log($"POST id=setmember_failed name={name} msg={e.Message}"); }
            return false;
        }

        static void ForcePostOff()
        {
            var ucd = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            if (ucd != null)
            {
                var c = _cam.GetComponent(ucd);
                if (c != null) try { ucd.GetProperty("renderPostProcessing").SetValue(c, false); } catch { }
            }
            if (_probeVolume != null) { UnityEngine.Object.DestroyImmediate(_probeVolume); _probeVolume = null; }
        }

        static Bounds? WorldBounds(GameObject go)
        {
            Bounds b = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any ? b : (Bounds?)null;
        }

        static void Shot(string id, Vector3 pos, Vector3 look) { Shot(id, pos, look, Vector3.up); }

        static void Shot(string id, Vector3 pos, Vector3 look, Vector3 up)
        {
            _upHint = up;
            var sw = Stopwatch.StartNew();
            var f = Render(pos, look, 1920, 1080, 8);
            sw.Stop();
            string file = Path.Combine(_out, id + ".png");
            Save(file);
            Debug.Log($"SHOT file={file} cam=({pos.x:F1},{pos.y:F1},{pos.z:F1}) " +
                      $"look=({look.x:F1},{look.y:F1},{look.z:F1}) ms={sw.ElapsedMilliseconds}");
            Stats(id);
            RendererDraws(id);
            FrameStats(id, f);
            Free();
            _upHint = Vector3.up;
        }

        // ==================================================================
        // part 2 - the pixel proof (tools/_probe_realholes.mjs, in Unity)
        // ==================================================================
        const int HoleW = 512, HoleH = 512;

        /// <summary>Rendered frames per hole measurement (passed to Render's
        /// `warmup`). One extra frame was tried while chasing the order-dependent
        /// h6k failure and made no difference — the cause was a shader property
        /// that was never declared, not a late texture upload (see
        /// Shaders/PlaneHull.shader's Properties block).</summary>
        const int HoleWarmup = 1;

        static void HolePixelProof(GameLoop game)
        {
            var fleet = GameObject.Find("planehall_fleet");
            if (fleet == null) { Check("fleetForHoles", false, "no planehall_fleet"); return; }
            Debug.Log($"REALHOLE id=setup fleetActive={fleet.activeInHierarchy.ToString().ToLowerInvariant()} " +
                      $"planes={fleet.transform.childCount} " +
                      "note=PlaneHallVisibility-is-not-ExecuteAlways-so-its-distance-switch-never-fires-in-edit-mode");

            var pd = new PlaneDamage { CameraPosition = () => _cam.transform.position };
            pd.MarkFx.EnsureBuilt(true);

            var planes = new List<Transform>();
            string only = Environment.GetEnvironmentVariable("TF_HOLE_ONLY");
            var onlySet = string.IsNullOrEmpty(only) ? null
                : new HashSet<string>(only.Split(',', StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i < fleet.transform.childCount; i++)
            {
                var ch = fleet.transform.GetChild(i);
                if (onlySet != null && !onlySet.Contains(ch.gameObject.name)) continue;
                planes.Add(ch);
            }

            int pass = 0, fail = 0, seeThroughPlanes = 0, magentaPlanes = 0;
            var pistol = Cartridges.Para9x19;
            var heavy = Cartridges.Heavy127x108;
            bool savedTopLevel = false;

            foreach (var pt in planes)
            {
                if (Wall.Elapsed.TotalMinutes > 25)
                { Debug.Log($"REALHOLE id=budget stoppedAfter={pass + fail}"); break; }

                string pid = pt.gameObject.name;
                var rec = pt.GetComponent<PlaneDamageRecord>();
                if (rec == null) { Debug.Log($"REALHOLE id={pid} skip=no_PlaneDamageRecord"); fail++; continue; }
                pd.Watch(rec);
                var box = pt.GetComponent<BoxCollider>();
                if (box == null) { Debug.Log($"REALHOLE id={pid} skip=no_BoxCollider"); fail++; continue; }
                var wb = WorldBounds(pt.gameObject);
                if (!wb.HasValue) { Debug.Log($"REALHOLE id={pid} skip=no_renderers"); fail++; continue; }
                Bounds bb = wb.Value;

                RaycastHit refined;
                string why;
                if (!AimAt(pd, box, pt, bb, out refined, out why, false))
                {
                    int meshes = 0;
                    foreach (var r in pt.GetComponentsInChildren<MeshRenderer>(true))
                        if (r.enabled && r.name != "plane_shell" && !r.name.StartsWith("cockpit_")) meshes++;
                    Debug.Log($"REALHOLE id={pid} skip={why} candidateRenderers={meshes}");
                    fail++; continue;
                }

                var hitRenderer = refined.collider.GetComponent<Renderer>();
                Vector3 pnt = refined.point;
                Vector3 nrm = refined.normal;
                if (Vector3.Dot(nrm, Vector3.up) < 0f) nrm = -nrm;
                Vector3 approach = (nrm + Vector3.up * 1.5f).normalized;
                Vector3 eye = pnt + approach * 1.9f;
                for (int k = 0; k < 4; k++)
                {
                    RaycastHit los;
                    if (Physics.Raycast(eye, (pnt - eye).normalized, out los, 1.9f) &&
                        los.collider != refined.collider && los.collider.transform.root != pt.root)
                        eye = pnt + approach * (2.4f + k * 0.6f);
                    else break;
                }
                float dist = Vector3.Distance(eye, pnt);
                float pxPerM = HoleH / (2f * dist * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                int radiusPx = Mathf.Max(8, Mathf.RoundToInt(pxPerM * 0.18f));

                // WHO IS IN FRONT OF THE HIT PIXEL. The LOS loop above can only
                // see PHYSICS colliders, and a parked machine has exactly one
                // (the BoxCollider shell on its root) plus whatever
                // PlaneDamage.EnsureColliders just added — and it then EXCLUDES
                // everything sharing this plane's root, so a panel of the SAME
                // airframe standing between the camera and the hit never counts
                // as an obstruction. Rendering does not care about colliders at
                // all: it is renderer-vs-renderer. Refine has already put a
                // MeshCollider on every classified part (PlaneDamage.cs:596
                // EnsureColliders), so the same question can be asked exactly,
                // per part, and printed.
                string frontPart = "none"; float frontDist = -1f; int occluders = 0;
                var pcache = rec.PartsCache;
                if (pcache != null)
                {
                    Vector3 dirToHit = (pnt - eye).normalized;
                    float reach = dist + 0.02f;
                    float nearest = float.MaxValue;
                    foreach (var cp in pcache.All)
                    {
                        var mc = cp.RaycastCollider;
                        if (mc == null || !mc.enabled) continue;
                        if (!mc.Raycast(new Ray(eye, dirToHit), out RaycastHit ph, reach)) continue;
                        if (ph.distance < nearest) { nearest = ph.distance; frontPart = cp.R.name; frontDist = ph.distance; }
                        // a part of this same airframe strictly in front of the
                        // hit point, and not the panel that was hit
                        if (ph.distance < dist - 0.03f && !ReferenceEquals(cp.R, hitRenderer)) occluders++;
                    }
                }
                // EVERY part along the line of sight, not just the nearest and not
                // just the ones strictly in front: a GLB often carries a second
                // surface a few millimetres from the first, and a coincident
                // panel repaints exactly the pixels a hole just opened.
                if (pcache != null)
                {
                    Vector3 dirToHit2 = (pnt - eye).normalized;
                    var chain = new List<(float d, string n, bool hull)>();
                    foreach (var cp in pcache.All)
                    {
                        var mc = cp.RaycastCollider;
                        if (mc == null || !mc.enabled) continue;
                        if (!mc.Raycast(new Ray(eye, dirToHit2), out RaycastHit ph, dist + 0.8f)) continue;
                        bool hull = false;
                        foreach (var m in cp.R.sharedMaterials)
                            if (m != null && m.shader != null && m.shader.name == "TbilisiFly/PlaneHull") hull = true;
                        chain.Add((ph.distance, cp.R.name, hull));
                    }
                    chain.Sort((a, b) => a.d.CompareTo(b.d));
                    var cs = new System.Text.StringBuilder();
                    for (int ci = 0; ci < chain.Count && ci < 8; ci++)
                        cs.Append($" [{chain[ci].n}@{chain[ci].d:F3}{(chain[ci].hull ? "+hull" : "")}]");
                    Debug.Log($"HOLECHAIN id={pid} hitDist={dist:F3} parts={chain.Count}{cs}");
                }

                Debug.Log($"HOLEDIAG id={pid} part={refined.collider.name} dist={dist:F2} " +
                          $"rawNormal=({refined.normal.x:F2},{refined.normal.y:F2},{refined.normal.z:F2}) " +
                          $"frontPart={frontPart} frontDist={frontDist:F2} occluders={occluders} " +
                          $"eye=({eye.x:F2},{eye.y:F2},{eye.z:F2}) pnt=({pnt.x:F2},{pnt.y:F2},{pnt.z:F2}) " +
                          $"nearClip={_cam.nearClipPlane:F2} radiusPx={radiusPx}");

                // "behind": the same frame with the hit panel's renderer off -
                // the ground truth for see-through.
                Frame behind = null;
                if (hitRenderer != null)
                {
                    hitRenderer.enabled = false;
                    behind = Render(eye, pnt, HoleW, HoleH, HoleWarmup);
                    Save(Path.Combine(_out, "holes", pid + "_behind.png"));
                    Free();
                    hitRenderer.enabled = true;
                }

                // A DECOY ROUND, deliberately off-camera on the far side of the
                // airframe: HoleSet.Patch swaps every skin material onto Shader
                // "TbilisiFly/PlaneHull" the first time a machine is hit, so a
                // "before" taken on the unpatched material would differ from the
                // "after" over the WHOLE panel and the hole would be lost in it.
                // The decoy pays that cost before the camera looks.
                RaycastHit far;
                string ignore;
                bool decoy = AimAt(pd, box, pt, bb, out far, out ignore, true);
                if (decoy) pd.Trace(far, far.point, far.normal, Vector3.down, pistol);
                pd.Update(0.016f);

                Frame before = Render(eye, pnt, HoleW, HoleH, HoleWarmup, () => pd.Update(0.016f));
                Save(Path.Combine(_out, "holes", pid + "_before.png"));
                if (!savedTopLevel) Save(Path.Combine(_out, "hole_before.png"));
                Free();

                // DOES THE PANEL THAT WAS HIT PAINT THE CENTRE OF THIS FRAME AT
                // ALL? `behind` above was taken BEFORE HoleSet.Patch swapped the
                // skin materials, so before-vs-behind would mix the patch in.
                // This one is taken on the same patched materials as `before`,
                // so a near-zero count means the hit panel contributes no pixels
                // where the hole is going to be drawn — i.e. the camera is not
                // looking at the panel it shot, and no hole could ever show.
                int coverage = -1;
                Frame behindPatched = null;
                if (hitRenderer != null)
                {
                    hitRenderer.enabled = false;
                    behindPatched = Render(eye, pnt, HoleW, HoleH, HoleWarmup, () => pd.Update(0.016f));
                    Free();
                    hitRenderer.enabled = true;
                    coverage = Changed(before, behindPatched, 0, radiusPx);
                }

                // ROUND 1 - 9x19, the smallest thing that can make a mark.
                string surf1 = pd.Trace(refined, refined.point, refined.normal, Vector3.down, pistol);
                int tears1 = CountTears(rec);
                Frame after = Render(eye, pnt, HoleW, HoleH, HoleWarmup, () => pd.Update(0.016f));
                Save(Path.Combine(_out, "holes", pid + "_after.png"));
                if (!savedTopLevel) Save(Path.Combine(_out, "hole_after.png"));
                Free();

                // ROUND 2 - the SAME SPOT: "damage is TEARS that grow when you
                // hit the same place, not one decal per round".
                pd.Trace(refined, refined.point, refined.normal, Vector3.down, pistol);
                int tears2 = CountTears(rec);
                // ROUND 3 - 12.7 into the same opening, which is what opens it
                // wide enough to see through at a real camera distance.
                pd.Trace(refined, refined.point, refined.normal, Vector3.down, heavy);
                int tears3 = CountTears(rec);
                Frame tear = Render(eye, pnt, HoleW, HoleH, HoleWarmup, () => pd.Update(0.016f));
                Save(Path.Combine(_out, "holes", pid + "_tear.png"));
                if (!savedTopLevel) { Save(Path.Combine(_out, "tear_after.png")); savedTopLevel = true; }
                Free();

                // WHERE did the frame change, if not at the centre? If the mark
                // was drawn but landed somewhere else (or on the far side of the
                // airframe), a whole-frame count with a centroid says so, where
                // a centre-only count just reads zero.
                int fullChanged; int fcx, fcy;
                FullChanged(before, tear, out fullChanged, out fcx, out fcy);
                Debug.Log($"HOLEDIAG id={pid} coverage={coverage} fullChanged={fullChanged} " +
                          $"centroidPx=({fcx},{fcy}) framePx={HoleW}x{HoleH}");

                // WHAT WAS ACTUALLY SUBMITTED. PlaneMarkFx.DrawLayer hands
                // `layer.Pose[i]` straight to InstancedSpriteBatch.Draw, so these
                // ARE the world matrices of the quads that were drawn. A
                // non-zero `drawn` count with no pixel change means the quads
                // are somewhere else, or have no area — both visible here.
                // THE SHADER'S OWN STATE on the panel that was hit. The tear
                // that makes the metal go is not a decal: Shaders/PlaneHull.shader
                // discards every fragment whose airframe-local position is within
                // `t.w` of an opening in `_TearTex`, over the renderer's own slice
                // `_TearLo.._TearHi` (plus the panel-less `_TearAll` range). A
                // `drawn` count proves a QUAD was submitted; only these numbers
                // prove the HOLE has a shader slot that covers the hit pixel.
                DumpHullState(pid, rec, hitRenderer, pnt);
                DumpMarks(pid, "holes", pd.Holes, pnt, eye);
                DumpMarks(pid, "tears", pd.Tears, pnt, eye);

                // THE PIXELS THEMSELVES at the middle of the frame. "changed=0"
                // cannot tell "nothing was drawn" from "what was drawn looks the
                // same as what it replaced" (the comparison's own threshold is 8
                // summed over RGB); these four colours can.
                Debug.Log($"HOLEPX id={pid} centre before={Hex(before, 0, 0)} after={Hex(after, 0, 0)} " +
                          $"tear={Hex(tear, 0, 0)} behind={(behindPatched == null ? "none" : Hex(behindPatched, 0, 0))} " +
                          $"off4 before={Hex(before, 4, 0)} tear={Hex(tear, 4, 0)} " +
                          $"off12 before={Hex(before, 12, 0)} tear={Hex(tear, 12, 0)}");

                // LOCALISE THE SHADER'S OWN FRAME (TF_HOLE_FORCE=1). The hull
                // shader discards a fragment when `distance(tearPos, t.xyz) <
                // t.w`, with tearPos = _TearInv * positionWS. Writing ONE
                // opening into slot 0 and announcing it through `_TearAll` (which
                // every fragment tests) turns that equation into a measurement:
                // an infinite radius says whether the loop runs and the texture
                // is bound at all; a 2 m radius at the record-local hit point
                // says whether the shader agrees with the CPU about where the
                // hit is; the same at the raw WORLD point says whether _TearInv
                // arrived. Every count is over the same 40 px disc as the real
                // proof, so they are directly comparable.
                if (Environment.GetEnvironmentVariable("TF_HOLE_FORCE") == "1" && rec.Holes != null && hitRenderer != null)
                {
                    var hs2 = rec.Holes;
                    Vector3 lp2 = hs2.TearInv.MultiplyPoint3x4(pnt);
                    Vector3 rl2 = hitRenderer.transform.InverseTransformPoint(pnt);
                    var px2 = hs2.Tex.GetPixels();
                    foreach (var m in hitRenderer.sharedMaterials)
                    {
                        if (m == null) continue;
                        m.SetTexture("_TearTex", hs2.Tex);
                        m.SetMatrix("_TearInv", hs2.TearInv);
                        m.SetFloat("_TearAll", 1f);   // float, not SetInteger — see the log below
                        m.SetInteger("_TearAll", 1);
                    }
                    int Try(string what, Vector3 at, float r)
                    {
                        px2[0] = new Color(at.x, at.y, at.z, r);
                        hs2.Tex.SetPixels(px2);
                        hs2.Tex.Apply(false, false);
                        var fr = Render(eye, pnt, HoleW, HoleH, HoleWarmup, () => pd.Update(0.016f));
                        int ch = Changed(before, fr, 0, radiusPx);
                        string hex = Hex(fr, 0, 0);
                        // WHERE the discarded region landed, in pixels off the
                        // frame centre: at pxPerM this converts straight into how
                        // far the shader's own tearPos is from the CPU's idea of
                        // the hit, and in which screen direction.
                        FullChanged(before, fr, out int fn, out int fx, out int fy);
                        Free();
                        float offM = fn > 0 ? Mathf.Sqrt((fx - HoleW / 2f) * (fx - HoleW / 2f) + (fy - HoleH / 2f) * (fy - HoleH / 2f)) / pxPerM : -1f;
                        Debug.Log($"HOLEFORCE id={pid} probe={what} at=({at.x:F3},{at.y:F3},{at.z:F3}) r={r:F3} " +
                                  $"changed={ch} discOfPx={Mathf.RoundToInt(Mathf.PI * radiusPx * radiusPx)} centre={hex} " +
                                  $"beforeCentre={Hex(before, 0, 0)} fullChanged={fn} centroidPx=({fx},{fy}) " +
                                  $"centroidOffM={offM:F3} pxPerM={pxPerM:F1}");
                        return ch;
                    }
                    // WHICH WAY is the shader's own tearPos displaced? A small
                    // sphere walked along each record-local axis (and along the
                    // hit normal, the one direction a surface/mesh mismatch
                    // would move it) names the direction, and the direction
                    // usually names the cause.
                    Try("recordLocal15cm", lp2, 0.15f);
                    Vector3 ln2 = hs2.TearInv.MultiplyVector(nrm).normalized;
                    foreach (float o in new[] { 0.35f, -0.35f })
                    {
                        Try($"offX{o:+0.00;-0.00}", lp2 + new Vector3(o, 0f, 0f), 0.15f);
                        Try($"offY{o:+0.00;-0.00}", lp2 + new Vector3(0f, o, 0f), 0.15f);
                        Try($"offZ{o:+0.00;-0.00}", lp2 + new Vector3(0f, 0f, o), 0.15f);
                        Try($"offN{o:+0.00;-0.00}", lp2 + ln2 * o, 0.15f);
                    }
                    bool skinned = hitRenderer is SkinnedMeshRenderer;
                    var mfh = hitRenderer.GetComponent<MeshFilter>();
                    Debug.Log($"HOLEFORCE id={pid} hitRenderer={hitRenderer.name} skinned={skinned.ToString().ToLowerInvariant()} " +
                              $"lossyScale=({hitRenderer.transform.lossyScale.x:F3},{hitRenderer.transform.lossyScale.y:F3},{hitRenderer.transform.lossyScale.z:F3}) " +
                              $"recordLocal=({lp2.x:F3},{lp2.y:F3},{lp2.z:F3}) rendererLocal=({rl2.x:F3},{rl2.y:F3},{rl2.z:F3}) " +
                              $"mesh={(mfh != null && mfh.sharedMesh != null ? mfh.sharedMesh.name : "none")} " +
                              $"colliderMeshVerts={((refined.collider as MeshCollider)?.sharedMesh != null ? ((MeshCollider)refined.collider).sharedMesh.vertexCount : -1)} " +
                              $"rendererBounds={hitRenderer.bounds.size:F3} colliderBounds={refined.collider.bounds.size:F3}");
                    // put the real data back so the pass/fail numbers below are honest
                    SyncHolesForProbe(pd, rec);
                }

                int changed1 = Changed(before, after, 0, radiusPx);
                int changed3 = Changed(before, tear, 0, radiusPx);
                // the control: the same comparison in an annulus well OUTSIDE
                // the hole. A real hole is local; a material that changed its
                // whole look is not.
                int control = Changed(before, tear, radiusPx * 2, radiusPx * 3);
                int seeThrough = behind == null ? -1 : SeeThrough(before, tear, behind, radiusPx);
                int magBefore = Magenta(before), magAfter = Magenta(tear);

                bool okHole = changed1 >= 15;
                if (okHole) pass++; else fail++;
                if (seeThrough > 4) seeThroughPlanes++;
                if (magAfter > magBefore + 500) magentaPlanes++;

                var holeSet = rec.Holes;
                Debug.Log($"REALHOLE id={pid} part={refined.collider.name} surface={surf1 ?? "null"} " +
                          $"changed={changed1} changedTear={changed3} control={control} radius_px={radiusPx} " +
                          $"seeThrough={seeThrough} tears={tears1}/{tears2}/{tears3} " +
                          $"magentaBefore={magBefore} magentaAfter={magAfter} " +
                          $"patched={(holeSet != null ? holeSet.Done : -1)} patchFailed={(holeSet != null ? holeSet.Failed : -1)} " +
                          $"drawn={pd.MarkFx.DrawnTotal} holesDrawn={pd.MarkFx.DrawnHoles} tearsDrawn={pd.MarkFx.DrawnTears} " +
                          $"dist={dist:F2} pass={okHole.ToString().ToLowerInvariant()}");
            }

            Debug.Log($"REALHOLE id=total pass={pass} fail={fail} seeThroughPlanes={seeThroughPlanes} magentaPlanes={magentaPlanes}");
            Check("realHolePixelsChanged", pass > 0 && fail == 0, $"pass={pass} fail={fail}");
            Check("realHoleSeeThrough", seeThroughPlanes > 0, $"planes={seeThroughPlanes}");
            Check("airframeDoesNotGoMagentaWhenHit", magentaPlanes == 0, $"planes={magentaPlanes}");
            pd.Dispose();
        }

        /// <summary>Find a real skin hit on this airframe through the real
        /// Refine path: a crude shell hit first (that IS how a round arrives),
        /// then Refine onto the actual panel. Walks stations along the widest
        /// axis, then falls back to dropping a ray onto the centre of each of
        /// the biggest renderers in turn - a flat or odd-shaped machine has
        /// nothing under a wing station.</summary>
        static bool AimAt(PlaneDamage pd, BoxCollider box, Transform pt, Bounds bb,
                          out RaycastHit refined, out string why, bool preferFar)
        {
            refined = default; why = "no_skin_under_the_shell";
            var stations = new List<Vector3>();
            bool wide = bb.extents.x >= bb.extents.z;
            float[] fr = preferFar ? new[] { -0.62f, -0.5f, 0.62f, 0.5f }
                                   : new[] { 0.40f, -0.40f, 0.25f, -0.25f, 0.55f, -0.55f, 0.10f, -0.10f, 0f };
            foreach (float f in fr)
                stations.Add(new Vector3(bb.center.x + (wide ? bb.extents.x * f : 0f), 0f,
                                         bb.center.z + (wide ? 0f : bb.extents.z * f)));
            var big = new List<Renderer>();
            foreach (var r in pt.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (r.name == "plane_shell" || r.name.StartsWith("cockpit_")) continue;
                if (r.bounds.size.magnitude > 1.2f) big.Add(r);
            }
            big.Sort((a, b) => b.bounds.size.sqrMagnitude.CompareTo(a.bounds.size.sqrMagnitude));
            for (int i = 0; i < big.Count && i < 8; i++)
                stations.Add(new Vector3(big[i].bounds.center.x, 0f, big[i].bounds.center.z));

            foreach (var s in stations)
            {
                var ray = new Ray(new Vector3(s.x, bb.max.y + 40f, s.z), Vector3.down);
                RaycastHit crude;
                if (!box.Raycast(ray, out crude, 300f)) continue;
                var r = pd.Refine(crude, Vector3.down);
                if (r == null) continue;
                if (r.Value.collider == box) continue;
                refined = r.Value; why = null; return true;
            }
            return false;
        }

        static int CountTears(PlaneDamageRecord rec)
        {
            int n = 0;
            foreach (var kv in rec.Tears) n += kv.Value != null ? kv.Value.Count : 0;
            return n;
        }

        /// <summary>Re-run the real HoleSet write after the forced experiment
        /// above scribbled on slot 0, so the pass/fail numbers that follow are
        /// measured against the game's own data. PlaneDamage.SyncHoles is
        /// private; the public way to reach it is one more round, which also
        /// restores `_TearAll`/the per-renderer ranges from the real tears.</summary>
        static void SyncHolesForProbe(PlaneDamage pd, PlaneDamageRecord rec)
        {
            var set = rec.Holes;
            if (set == null) return;
            var all = new List<PlaneTear>();
            foreach (var l in rec.Tears.Values) if (l != null) all.AddRange(l);
            all.AddRange(rec.Exits);
            set.Write(all, rec.transform.worldToLocalMatrix);
        }

        static string Hex(Frame f, int dx, int dy)
        {
            var p = f.At(Mathf.Clamp(f.W / 2 + dx, 0, f.W - 1), Mathf.Clamp(f.H / 2 + dy, 0, f.H - 1));
            return $"{p.r:x2}{p.g:x2}{p.b:x2}";
        }

        static void DumpHullState(string pid, PlaneDamageRecord rec, Renderer hit, Vector3 pnt)
        {
            var hs = rec.Holes;
            if (hs == null || hit == null) { Debug.Log($"HOLEHULL id={pid} holeSet={(hs == null ? "null" : "ok")} hitRenderer={(hit == null ? "null" : hit.name)}"); return; }
            Vector3 lp = hs.TearInv.MultiplyPoint3x4(pnt);
            var sb = new System.Text.StringBuilder();
            int lo = -1, hi = -1;
            foreach (var m in hit.sharedMaterials)
            {
                if (m == null) { sb.Append(" [null]"); continue; }
                bool isHull = m.shader != null && m.shader.name == "TbilisiFly/PlaneHull";
                int mlo = m.HasProperty("_TearLo") ? m.GetInt("_TearLo") : -1;
                int mhi = m.HasProperty("_TearHi") ? m.GetInt("_TearHi") : -1;
                int mall = m.HasProperty("_TearAll") ? m.GetInt("_TearAll") : -1;
                bool tex = m.HasProperty("_TearTex") && m.GetTexture("_TearTex") != null;
                if (isHull) { lo = mlo; hi = mhi; }
                sb.Append($" [{m.name} shader={(m.shader != null ? m.shader.name : "null")} hull={isHull.ToString().ToLowerInvariant()} " +
                          $"lo={mlo} hi={mhi} all={mall} tearTex={tex.ToString().ToLowerInvariant()}]");
            }
            // which slots, if any, cover the hit pixel
            var px = hs.Tex.GetPixels();
            int covering = 0; float bestD = -1f, bestR = -1f; int bestSlot = -1;
            int scanHi = Mathf.Max(hi, hs.TearAll);
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a <= 0f) continue;
                bool inRange = (i < hs.TearAll) || (lo >= 0 && i >= lo && i < hi);
                float d = Vector3.Distance(new Vector3(px[i].r, px[i].g, px[i].b), lp);
                if (bestD < 0f || d < bestD) { bestD = d; bestR = px[i].a; bestSlot = i; }
                if (inRange && d < px[i].a) covering++;
            }
            Debug.Log($"HOLEHULL id={pid} localHit=({lp.x:F3},{lp.y:F3},{lp.z:F3}) tearAll={hs.TearAll} " +
                      $"tearCount={hs.TearCount} lo={lo} hi={hi} slotsCoveringHit={covering} " +
                      $"nearestSlot={bestSlot} nearestDist={bestD:F4} nearestR={bestR:F4} scanHi={scanHi}" + sb);
        }

        static void DumpMarks(string pid, string what, PlaneMarkLayer layer, Vector3 pnt, Vector3 eye)
        {
            if (layer == null) { Debug.Log($"HOLEMARK id={pid} layer={what} missing=true"); return; }
            int live = 0, near = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < layer.Count; i++)
            {
                if (!layer.Live[i]) continue;
                live++;
                var m = layer.Pose[i];
                Vector3 p = m.GetColumn(3);
                float d = Vector3.Distance(p, pnt);
                if (d > 50f) continue;
                near++;
                Vector3 sc = new Vector3(((Vector3)m.GetColumn(0)).magnitude,
                                         ((Vector3)m.GetColumn(1)).magnitude,
                                         ((Vector3)m.GetColumn(2)).magnitude);
                Vector3 nrm = ((Vector3)m.GetColumn(2)).normalized;
                // SIGNED offset towards the camera: positive is in front of the
                // surface (visible), negative is buried inside the panel and
                // depth-tested away.
                float toward = Vector3.Dot(p - pnt, (eye - pnt).normalized);
                var tint = layer.Tint[i];
                if (near <= 6)
                    sb.Append($" [{i} d={d:F3} toward={toward:F4} sc=({sc.x:F3},{sc.y:F3},{sc.z:F3}) " +
                              $"n=({nrm.x:F2},{nrm.y:F2},{nrm.z:F2}) tint={(tint.HasValue ? $"{tint.Value.r},{tint.Value.g},{tint.Value.b},{tint.Value.a}" : "white")}]");
            }
            Debug.Log($"HOLEMARK id={pid} layer={what} live={live} within50m={near}{sb}");
        }

        /// <summary>Whole-frame version of <see cref="Changed"/>, with the
        /// centroid of the changed pixels — so "nothing changed at the hole"
        /// can be told apart from "something changed 200 px away".</summary>
        static void FullChanged(Frame a, Frame b, out int n, out int cx, out int cy)
        {
            n = 0; long sx = 0, sy = 0;
            for (int y = 0; y < a.H; y++)
                for (int x = 0; x < a.W; x++)
                {
                    var p = a.At(x, y); var q = b.At(x, y);
                    if (Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b) <= 8) continue;
                    n++; sx += x; sy += y;
                }
            cx = n > 0 ? (int)(sx / n) : -1;
            cy = n > 0 ? (int)(sy / n) : -1;
        }

        static int Changed(Frame a, Frame b, int rInner, int rOuter)
        {
            int cx = a.W / 2, cy = a.H / 2, n = 0;
            int ri2 = rInner * rInner, ro2 = rOuter * rOuter;
            for (int y = Math.Max(0, cy - rOuter); y < Math.Min(a.H, cy + rOuter); y++)
                for (int x = Math.Max(0, cx - rOuter); x < Math.Min(a.W, cx + rOuter); x++)
                {
                    int dx = x - cx, dy = y - cy, d2 = dx * dx + dy * dy;
                    if (d2 > ro2 || d2 < ri2) continue;
                    var p = a.At(x, y); var q = b.At(x, y);
                    if (Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b) > 8) n++;
                }
            return n;
        }

        /// <summary>THE METAL REALLY GOES: a pixel that was skin (clearly
        /// different from what is behind the panel) and now matches what is
        /// behind the panel. That is seeing THROUGH the wing, not a decal
        /// painted on it.</summary>
        static int SeeThrough(Frame before, Frame after, Frame behind, int radiusPx)
        {
            int cx = before.W / 2, cy = before.H / 2, n = 0, r2 = radiusPx * radiusPx;
            for (int y = Math.Max(0, cy - radiusPx); y < Math.Min(before.H, cy + radiusPx); y++)
                for (int x = Math.Max(0, cx - radiusPx); x < Math.Min(before.W, cx + radiusPx); x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    var b0 = before.At(x, y); var a1 = after.At(x, y); var bg = behind.At(x, y);
                    int wasSkin = Math.Abs(b0.r - bg.r) + Math.Abs(b0.g - bg.g) + Math.Abs(b0.b - bg.b);
                    int nowBg = Math.Abs(a1.r - bg.r) + Math.Abs(a1.g - bg.g) + Math.Abs(a1.b - bg.b);
                    if (wasSkin > 24 && nowBg < 24) n++;
                }
            return n;
        }

        // ==================================================================
        // part 3 - the propeller and its blur disc, in pixels
        // ==================================================================
        static void PropellerPixels(GameLoop game)
        {
            var plane = game != null ? game.ActivePlane : null;
            if (plane == null) { Check("propellerShot", false, "no ActivePlane"); return; }
            var pp = plane.GetComponentInChildren<PlanePropellers>(true);
            if (pp == null) { Check("propellerComponent", false, "ActivePlane has no PlanePropellers"); return; }
            pp.EnsureBuilt();
            Debug.Log($"PROPPIX id=setup plane={plane.gameObject.name} planeId={pp.PlaneId} propellers={pp.Count}");
            Check("propellerCount", pp.Count > 0, $"count={pp.Count}");
            if (pp.Count == 0) return;

            // Rpm has a private setter and LateUpdate is never delivered outside
            // play mode. The backing value is set to 0.70 - exactly what
            // EngineSound.ResumeRunning writes (game.js:1413-1417) - and
            // LateUpdate is invoked by reflection, so the real
            // EngineRpm() -> Engine.Rpm * 2700 chain runs. 0.70*2700 = 1890
            // crank rpm, past PropellerSystem.BlurFull (1100): full disc.
            float set = 0.70f;
            bool rpmSet = false;
            if (game.Engine != null)
            {
                try { typeof(EngineSound).GetProperty("Rpm").SetValue(game.Engine, set); rpmSet = true; }
                catch { }
                if (!rpmSet)
                {
                    var fld = typeof(EngineSound).GetField("<Rpm>k__BackingField",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fld != null) { fld.SetValue(game.Engine, set); rpmSet = true; }
                }
            }
            var lu = typeof(PlanePropellers).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 4; i++) { if (lu != null) lu.Invoke(pp, null); }
            Debug.Log($"PROPPIX id=rpm set={set:F2} rpmSet={rpmSet.ToString().ToLowerInvariant()} " +
                      $"engineRpm={(game.Engine != null ? game.Engine.Rpm : -1f):F3} componentRpm={pp.Rpm:F0}");
            Check("rpmWritable", rpmSet);
            Check("propellerSpun", pp.Rpm > 1000f, $"rpm={pp.Rpm:F0}");

            var discs = new List<Renderer>();
            var blades = new List<Renderer>();
            Vector3 hub = Vector3.zero;
            float radius = 0f;
            foreach (var inst in pp.Props)
            {
                var dr = inst.Disc != null ? inst.Disc.GetComponent<Renderer>() : null;
                if (dr != null) discs.Add(dr);
                if (inst.Radius > radius) { radius = inst.Radius; hub = inst.Pivot.position; }
                if (inst.Blades != null) foreach (var b in inst.Blades) if (b != null) blades.Add(b);
                Debug.Log($"PROPPIX id=prop pivot={inst.Pivot.name} hasDisc={inst.HasDisc.ToString().ToLowerInvariant()} " +
                          $"discEnabled={(dr != null && dr.enabled).ToString().ToLowerInvariant()} " +
                          $"discWorldSize={(dr != null ? dr.bounds.size.ToString("F2") : "n/a")} " +
                          $"discAlpha={(inst.DiscMaterial != null ? inst.DiscMaterial.color.a : -1f):F3} " +
                          $"radius={inst.Radius:F2} blades={(inst.Blades != null ? inst.Blades.Length : 0)} " +
                          $"hub={inst.Pivot.position.ToString("F2")} pivotLossy={inst.Pivot.lossyScale.ToString("F4")}");
            }
            if (radius <= 0f) { radius = 1.5f; hub = pp.Props[0].Pivot.position; }
            Check("discRendererOn", discs.Count > 0 && discs[0].enabled, $"discs={discs.Count}");

            // Frame the disc by its RENDERED size, not by the radius it claims.
            float discSize = discs.Count > 0 ? Mathf.Max(discs[0].bounds.size.x, discs[0].bounds.size.y) : radius * 2f;
            var bbAll = WorldBounds(plane.gameObject);
            Vector3 ctr = bbAll.HasValue ? bbAll.Value.center : plane.transform.position;
            Vector3 outward = hub - ctr; outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) outward = plane.transform.forward;
            outward.Normalize();
            float stand = Mathf.Max(_cam.nearClipPlane + 0.4f, discSize * 3.2f);
            Debug.Log($"PROPPIX id=frame planeBoundsMin={(bbAll.HasValue ? bbAll.Value.min.ToString("F2") : "n/a")} " +
                      $"planeBoundsMax={(bbAll.HasValue ? bbAll.Value.max.ToString("F2") : "n/a")} " +
                      $"hubLocalToPlane={plane.transform.InverseTransformPoint(hub).ToString("F2")} " +
                      $"planeForward={plane.transform.forward.ToString("F2")} outward={outward.ToString("F2")}");
            Vector3 eye = hub + outward * stand + Vector3.up * (discSize * 0.15f);

            var asDrawn = Render(eye, hub, HoleW, HoleH, 1);
            Save(Path.Combine(_out, "propeller.png"));
            Debug.Log($"SHOT file={Path.Combine(_out, "propeller.png")} cam=({eye.x:F2},{eye.y:F2},{eye.z:F2}) " +
                      $"look=({hub.x:F2},{hub.y:F2},{hub.z:F2}) ms=0 standoff={stand:F2} discWorldSize={discSize:F2}");
            Stats("propeller");
            FrameStats("propeller", asDrawn, false);
            Free();

            foreach (var r in discs) r.enabled = false;
            var noDisc = Render(eye, hub, HoleW, HoleH, 1);
            Save(Path.Combine(_out, "propeller_nodisc.png"));
            Free();
            foreach (var r in blades) r.enabled = false;
            var noProp = Render(eye, hub, HoleW, HoleH, 1);
            Save(Path.Combine(_out, "propeller_noprop.png"));
            Free();
            foreach (var r in discs) r.enabled = true;
            foreach (var r in blades) r.enabled = true;

            int discPx = DiffAll(asDrawn, noDisc);
            int bladePx = DiffAll(noDisc, noProp);
            int propPx = DiffAll(asDrawn, noProp);
            Debug.Log($"PROPPIX id=pixels discPx={discPx} bladePx={bladePx} propellerPx={propPx} " +
                      $"frame={HoleW}x{HoleH} discCount={discs.Count} bladeCount={blades.Count}");
            Check("discPixelsVisible", discPx > 200, $"discPx={discPx}");
            Check("bladePixelsVisible", bladePx > 50, $"bladePx={bladePx}");
        }

        static int DiffAll(Frame a, Frame b)
        {
            int n = 0;
            for (int i = 0; i < a.Px.Length; i++)
            {
                var p = a.Px[i]; var q = b.Px[i];
                if (Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b) > 8) n++;
            }
            return n;
        }
    }
}
