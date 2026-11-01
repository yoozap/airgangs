using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TbilisiFly.Core;
using TbilisiFly.Data;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE COLOUR PROBE. Everything outdoors in the port's first rendered
    /// frames was blown white (oldtown mean RGB 234.7/231.2/211.3, 10.4 % of
    /// the frame exactly 255,255,255; landmarks 45.7 %). This probe is the
    /// pixel proof for the fix, and it deliberately does NOT read flags:
    /// `renderPostProcessing == true` is what the earlier attempt already had
    /// when it produced a BYTE-IDENTICAL frame.
    ///
    /// It renders the same three views as RenderShotProbe (same camera
    /// placement, copied — that file is another agent's and is not edited) in
    /// three configurations and diffs the pixels:
    ///
    ///   &lt;id&gt;_linear.png           HDR render target, post ON   <- the game
    ///   &lt;id&gt;_linear_ldrrt.png     LDR sRGB target, post ON      <- like-for-like
    ///                                  with the old shots, which used an
    ///                                  ARGB32 target
    ///   oldtown_linear_notonemap.png  HDR target, ACES override OFF
    ///
    /// WHY TWO TARGET FORMATS. URP sizes its INTERMEDIATE colour buffer from
    /// the camera's target texture when there is one — "the intermediate color
    /// buffer inherits the external texture's format instead of using the
    /// configured internal rendering format. This is incorrect but kept for
    /// backward compatibility" (UniversalRenderPipelineCore.cs:1595-1602,
    /// CreateBackbufferInfo + CreateRenderTextureDescriptor). An ARGB32 target
    /// therefore clamps the scene to 1.0 BEFORE the tonemapper ever sees it,
    /// so ACES cannot recover a highlight that was already crushed. The real
    /// game renders to the backbuffer, which URP builds with
    /// MakeRenderTextureGraphicsFormat(isHdrEnabled: true) — so the HDR target
    /// is the honest one and the LDR one exists only to be comparable with the
    /// frames that are already on disk.
    ///
    /// AND WHY THE PNG IS ENCODED BY HAND. `cameraData.requireSrgbConversion`
    /// is `targetTexture == null &amp;&amp; ...` (UniversalCameraData.cs:276-287), so
    /// when a target texture is set URP writes LINEAR values and leaves the
    /// sRGB encode to the target's own format. A float target has no sRGB
    /// encode, so this probe does it in C# and writes the bytes it measured.
    ///
    /// Run WITH a GPU (drop -nographics):
    ///   Unity -batchmode -quit -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.LookShotProbe.Run -logFile ...
    /// </summary>
    public static class LookShotProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string OutDefault =
            "/private/tmp/claude-501/-Users-wazzap-tbilisifly/0f5488ea-9a85-4533-ad67-e5fe68c33649/scratchpad/shots";

        const int W = 1920, H = 1080, Warmup = 8;

        static string _out;
        static Camera _cam;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);

            Debug.Log($"LOOK out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"colorSpace={QualitySettings.activeColorSpace}");
            Check("colorSpaceLinear", QualitySettings.activeColorSpace == ColorSpace.Linear,
                  "URP needs Linear for an ACES tonemap to mean anything");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log("LOOK id=nogpu msg=needs a run WITHOUT -nographics");
                Debug.Log("LOOK RESULT=FAIL");
                ProbeExit.Exit(1);
                return;
            }

            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }

                PipelineFacts();

                var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                Check("playableSceneBootExists", boot != null);
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"LOOK id=bootthrew msg={e.Message}"); Debug.LogException(e); }

                _cam = game != null ? game.MainCamera : null;
                if (_cam == null) _cam = Camera.main;
                if (_cam == null)
                {
                    var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                    if (all.Length > 0) _cam = all[0];
                }
                Check("cameraExists", _cam != null);
                if (_cam == null) { Done(); return; }
                _cam.enabled = true;
                _cam.allowHDR = true;
                // Every City renderer culls against Camera.main; without the tag
                // they fall into "no camera -> never cull" and submit the whole
                // city, which is not what the game draws.
                if (_cam.tag != "MainCamera") { try { _cam.tag = "MainCamera"; } catch { } }

                EnvironmentSetup();
                CameraFacts("after_setup");
                CollectTickers();

                Shots(game);
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"LOOK id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        static void Done()
        {
            Debug.Log($"LOOK wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "LOOK RESULT=PASS" : "LOOK RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"LOOKCHECK {key}={(cond ? "PASS" : "FAIL")}";
            if (note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        // ------------------------------------------------------------------
        // the three gates, read off the LIVE pipeline objects
        // ------------------------------------------------------------------
        static void PipelineFacts()
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            Check("urpAssetActive", urp != null);
            if (urp == null) return;

            // gate 1: UniversalRenderer.cs:318 postProcessEnabled = data.postProcessData != null
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
                "Assets/Settings/TbilisiFly_Renderer.asset");
            bool ppData = rendererData != null && rendererData.postProcessData != null;
            Debug.Log($"LOOKPOST id=rendererdata asset={(rendererData != null).ToString().ToLowerInvariant()} " +
                      $"postProcessData={(rendererData != null && rendererData.postProcessData != null ? rendererData.postProcessData.name : "null")}");
            Check("rendererHasPostProcessData", ppData,
                  "null here makes UniversalRenderer drop EVERY post pass, silently");

            // gate 3: the volume stack URP boots with
            var gdp = VolumeManager.instance != null ? VolumeManager.instance.globalDefaultProfile : null;
            Debug.Log($"LOOKPOST id=volumemanager globalDefaultProfile={(gdp != null ? gdp.name : "null")} " +
                      $"urpAssetVolumeProfile={(urp.volumeProfile != null ? urp.volumeProfile.name : "null")}");

            Debug.Log($"LOOKPOST id=urp hdr={urp.supportsHDR.ToString().ToLowerInvariant()} " +
                      $"msaa={urp.msaaSampleCount} shadowDistance={urp.shadowDistance:F0} " +
                      $"cascades={urp.shadowCascadeCount} colorGradingMode={urp.colorGradingMode} " +
                      $"lutSize={urp.colorGradingLutSize}");
        }

        static void CameraFacts(string when)
        {
            var ucd = _cam.GetComponent<UniversalAdditionalCameraData>();
            Debug.Log($"LOOKPOST id=camera when={when} " +
                      $"hasCameraData={(ucd != null).ToString().ToLowerInvariant()} " +
                      $"renderPostProcessing={(ucd != null ? ucd.renderPostProcessing.ToString().ToLowerInvariant() : "n/a")} " +
                      $"antialiasing={(ucd != null ? ucd.antialiasing.ToString() : "n/a")} " +
                      $"volumeLayerMask={(ucd != null ? ucd.volumeLayerMask.value.ToString() : "n/a")} " +
                      $"allowHDR={_cam.allowHDR.ToString().ToLowerInvariant()} " +
                      $"fov={_cam.fieldOfView:F1} near={_cam.nearClipPlane:F2} far={_cam.farClipPlane:F0}");
            Debug.Log($"LOOKENV id=scene fog={RenderSettings.fog.ToString().ToLowerInvariant()} " +
                      $"fogMode={RenderSettings.fogMode} fogDensity={RenderSettings.fogDensity:F6} " +
                      $"fogColor={RenderSettings.fogColor} " +
                      $"ambientMode={RenderSettings.ambientMode} ambientIntensity={RenderSettings.ambientIntensity:F2} " +
                      $"sky={RenderSettings.ambientSkyColor} ground={RenderSettings.ambientGroundColor} " +
                      $"reflectionMode={RenderSettings.defaultReflectionMode} " +
                      $"customReflection={(RenderSettings.customReflection != null ? RenderSettings.customReflection.name : "null")} " +
                      $"skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")} " +
                      $"shadowDistance={QualitySettings.shadowDistance:F0}");
        }

        static SceneEnvironmentSetup _env;

        static void EnvironmentSetup()
        {
            CameraFacts("before_setup");
            _env = UnityEngine.Object.FindFirstObjectByType<SceneEnvironmentSetup>();
            Check("sceneEnvironmentSetupExists", _env != null);
            if (_env == null) return;
            _env.Cam = _cam;
            _env.Setup();

            var ucd = _cam.GetComponent<UniversalAdditionalCameraData>();
            Check("cameraRunsPostProcessing", ucd != null && ucd.renderPostProcessing,
                  "gate 2 - UniversalRenderPipeline.cs:1952");
            var vol = _env.transform.Find(SceneEnvironmentSetup.VolumeObjectName);
            var v = vol != null ? vol.GetComponent<Volume>() : null;
            Check("sceneVolumeExists", v != null && v.isGlobal && v.sharedProfile != null,
                  "gate 3 - a global Volume in the scene (URP's own default profile also covers this)");
            if (v != null && v.sharedProfile != null)
            {
                v.sharedProfile.TryGet<Tonemapping>(out var tm);
                v.sharedProfile.TryGet<ColorAdjustments>(out var ca);
                v.sharedProfile.TryGet<Bloom>(out var bl);
                Debug.Log($"LOOKPOST id=profile name={v.sharedProfile.name} " +
                          $"tonemapping={(tm != null ? tm.mode.value.ToString() : "missing")} " +
                          $"tonemapActive={(tm != null ? tm.active.ToString().ToLowerInvariant() : "n/a")} " +
                          $"postExposure={(ca != null ? ca.postExposure.value.ToString("F4") : "n/a")} " +
                          $"bloomIntensity={(bl != null ? bl.intensity.value.ToString("F3") : "n/a")}");
                Check("acesTonemapOnProfile", tm != null && tm.active && tm.mode.value == TonemappingMode.ACES,
                      "game.js:266 THREE.ACESFilmicToneMapping");
            }
            if (_env.Sun != null)
                Debug.Log($"LOOKENV id=sun intensity={_env.Sun.intensity:F2} color={_env.Sun.color} " +
                          $"shadows={_env.Sun.shadows} strength={_env.Sun.shadowStrength:F2} " +
                          $"fwd={_env.Sun.transform.forward.ToString("F3")}");
        }

        // ------------------------------------------------------------------
        // per-frame instanced submission (no LateUpdate in edit mode)
        // ------------------------------------------------------------------
        static readonly string[] RendererTypeNames =
        {
            "TbilisiFly.City.CityRenderer", "TbilisiFly.City.ParkRenderer",
            "TbilisiFly.City.VegetationRenderer", "TbilisiFly.City.ShopfrontRenderer",
            "TbilisiFly.City.StreetFurnitureRenderer", "TbilisiFly.City.LandmarkRenderer",
            "TbilisiFly.City.MetroRenderer", "TbilisiFly.City.MallRenderer",
            "TbilisiFly.City.WindsockRenderer", "TbilisiFly.City.PlaneHallRenderer",
        };

        sealed class Ticker { public Component C; public string Name; public MethodInfo Tick, Late; }
        static readonly List<Ticker> Tickers = new List<Ticker>();

        static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full, false);
                if (t != null) return t;
            }
            return null;
        }

        static void CollectTickers()
        {
            Tickers.Clear();
            foreach (var tn in RendererTypeNames)
            {
                var type = FindType(tn);
                if (type == null) continue;
                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                if (found == null) continue;
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
                    Tickers.Add(new Ticker { C = c, Name = type.Name, Tick = tick, Late = lu });
                }
            }
            Debug.Log($"LOOK id=tickers n={Tickers.Count}");
            Check("instancedRenderersFound", Tickers.Count > 0);
        }

        static void TickRenderers()
        {
            Vector3 p = _cam.transform.position;
            foreach (var t in Tickers)
            {
                try
                {
                    if (t.Tick != null) t.Tick.Invoke(t.C, new object[] { p, 0.5f });
                    else t.Late?.Invoke(t.C, null);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // render + hand-rolled sRGB encode
        // ------------------------------------------------------------------
        static Color32[] _lastPx;

        static float ToSrgb(float c)
        {
            if (float.IsNaN(c)) return 0f;
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        static Color32[] Render(Vector3 pos, Vector3 look, Vector3 up, bool hdrTarget)
        {
            RenderTexture rt;
            if (hdrTarget)
            {
                var desc = new RenderTextureDescriptor(W, H, GraphicsFormat.R16G16B16A16_SFloat,
                                                       GraphicsFormat.D24_UNorm_S8_UInt)
                { msaaSamples = 1, useMipMap = false, autoGenerateMips = false };
                rt = new RenderTexture(desc);
            }
            else
            {
                rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { antiAliasing = 1 };
            }
            rt.Create();

            var dir = look - pos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, up));
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            for (int i = 0; i <= Warmup; i++) { TickRenderers(); _cam.Render(); }

            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var px = new Color32[W * H];
            if (hdrTarget)
            {
                // linear floats straight out of URP's final blit (no sRGB
                // encode on a float target - UniversalCameraData.cs:276)
                var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply(false);
                var src = tex.GetPixels();
                for (int i = 0; i < src.Length; i++)
                    px[i] = new Color32((byte)Mathf.RoundToInt(ToSrgb(src[i].r) * 255f),
                                        (byte)Mathf.RoundToInt(ToSrgb(src[i].g) * 255f),
                                        (byte)Mathf.RoundToInt(ToSrgb(src[i].b) * 255f), 255);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else
            {
                // the sRGB target already holds encoded bytes
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply(false);
                px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                UnityEngine.Object.DestroyImmediate(tex);
            }
            RenderTexture.active = active;
            _cam.targetTexture = prev;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            _lastPx = px;
            return px;
        }

        static void Save(string id)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(_lastPx);
            tex.Apply(false);
            File.WriteAllBytes(Path.Combine(_out, id + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        /// <summary>mean RGB, % pure 255,255,255, % >=250 all channels, % near
        /// black and the distinct-colour count - the same shape the blown-out
        /// frames were measured with, so the before/after rows compare.</summary>
        static void FrameStats(string id, Color32[] px)
        {
            long rs = 0, gs = 0, bs = 0;
            int white = 0, near = 0, dark = 0, magenta = 0;
            var seen = new HashSet<int>();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                rs += c.r; gs += c.g; bs += c.b;
                if (c.r == 255 && c.g == 255 && c.b == 255) white++;
                if (c.r >= 250 && c.g >= 250 && c.b >= 250) near++;
                if (c.r < 12 && c.g < 12 && c.b < 12) dark++;
                if (c.r > 220 && c.b > 220 && c.g < 60) magenta++;
                if (seen.Count < 300000) seen.Add((c.r << 16) | (c.g << 8) | c.b);
            }
            int n = px.Length;
            Debug.Log($"LOOKSTATS id={id} meanR={rs / (double)n:F1} meanG={gs / (double)n:F1} " +
                      $"meanB={bs / (double)n:F1} pctWhite={100.0 * white / n:F2} " +
                      $"pctNearWhite={100.0 * near / n:F2} pctDark={100.0 * dark / n:F2} " +
                      $"magenta={magenta} distinctColours={seen.Count}");
            // The "_nopost" frames are the CONTROL arm - they are SUPPOSED to
            // be blown, that is the whole point of rendering them - so the
            // exposure assertion does not apply to them.
            if (!id.EndsWith("_nopost"))
                Check($"notBlownOut_{id}", 100.0 * white / n < 2.0,
                      "pure-white pixels are a clipped exposure, not a sky");
            Check($"notFlat_{id}", seen.Count > 2000, "a near-single-colour frame is a blank render");
        }

        static double MeanAbsDiff(Color32[] a, Color32[] b)
        {
            long s = 0;
            for (int i = 0; i < a.Length; i++)
                s += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b);
            return s / (double)(a.Length * 3);
        }

        // ------------------------------------------------------------------
        static void Shots(GameLoop game)
        {
            var city = CityData.Load(buildings: false);
            Func<float, float, float> ground = (ux, uz) =>
                (city == null || city.Terrain == null) ? 0f : city.Terrain.HeightAt(ux, uz);

            // --- the three views, camera placement copied verbatim from
            //     Editor/Probes/RenderShotProbe.cs:599-661 so the frames are
            //     comparable with the ones already on disk ---
            Vector3 narikala = GeoFrame.FromLatLon(41.6880, 44.8090);
            float gy = ground(narikala.x, narikala.z);
            Vector3 oldtownPos = new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f);
            Vector3 oldtownLook = new Vector3(narikala.x, gy + 40f, narikala.z);

            var views = new List<(string id, Vector3 pos, Vector3 look, Vector3 up)>
            {
                ("oldtown", oldtownPos, oldtownLook, Vector3.up),
            };

            var shed = UnityEngine.Object.FindFirstObjectByType<TbilisiFly.Flight.ShedSceneFill>();
            var hall = GameObject.Find("planehall");
            if (hall != null)
            {
                float hl = shed != null ? shed.Hall.Shell.Length : 220f;
                var t = hall.transform;
                views.Add(("planehall",
                           t.position + t.forward * (-hl * 0.46f) + Vector3.up * 11f,
                           t.position + t.forward * (hl * 0.40f) + Vector3.up * 4f, Vector3.up));
            }
            else Check("planehallShot", false, "no GameObject named planehall");

            var tags = UnityEngine.Object.FindObjectsByType<TbilisiFly.City.LandmarkTag>(FindObjectsSortMode.None);
            TbilisiFly.City.LandmarkTag pick = null;
            foreach (var t in tags)
                if ((t.LandmarkName ?? "").Contains("იუსტიცი")) { pick = t; break; }
            if (pick == null && tags.Length > 0) pick = tags[0];
            if (pick != null)
            {
                var b = WorldBounds(pick.gameObject);
                Vector3 ctr = b.HasValue ? b.Value.center : pick.transform.position;
                Vector3 ext = b.HasValue ? b.Value.extents : new Vector3(20f, 20f, 20f);
                float d = Mathf.Max(45f, ext.magnitude * 1.7f);
                views.Add(("landmarks",
                           ctr + new Vector3(d * 0.75f, Mathf.Max(22f, ext.y * 1.6f), d * 0.66f),
                           ctr, Vector3.up));
            }
            else Check("landmarkShot", false, "no LandmarkTag in the scene");

            foreach (var v in views)
            {
                var sw = Stopwatch.StartNew();
                var hdr = Render(v.pos, v.look, v.up, true);
                Save(v.id + "_linear");
                sw.Stop();
                Debug.Log($"LOOKSHOT file={Path.Combine(_out, v.id + "_linear.png")} target=hdr " +
                          $"cam=({v.pos.x:F1},{v.pos.y:F1},{v.pos.z:F1}) ms={sw.ElapsedMilliseconds}");
                FrameStats(v.id + "_linear", hdr);

                var ldr = Render(v.pos, v.look, v.up, false);
                Save(v.id + "_linear_ldrrt");
                Debug.Log($"LOOKSHOT file={Path.Combine(_out, v.id + "_linear_ldrrt.png")} target=ldr_srgb");
                FrameStats(v.id + "_linear_ldrrt", ldr);
                Debug.Log($"LOOKDIFF id={v.id} hdrTargetVsLdrTarget meanAbs={MeanAbsDiff(hdr, ldr):F2}");
            }

            // --- THE PROOF: the same frame with the camera's post stack
            //     switched OFF. This is the right lever and toggling the ACES
            //     override on the profile is the WRONG one: VolumeManager's
            //     default-profile state is built once, in
            //     CheckDefaultVolumeState/ApplyDefaultProfile
            //     (VolumeManager.cs:538-561), and `component.active` is only
            //     read THERE, so flipping it mid-session leaves the stack's
            //     default state still carrying ACES and produces a
            //     byte-identical frame whether post runs or not. (It did -
            //     meanAbs 0.00 - and that measured NOTHING.)
            //     renderPostProcessing is read per frame
            //     (UniversalRenderPipeline.cs:1952), so this A/B moves pixels
            //     if and only if the post stack executes. ---
            var liveUcd = _cam.GetComponent<UniversalAdditionalCameraData>();
            if (liveUcd == null) { Check("postABPossible", false, "no UniversalAdditionalCameraData"); return; }

            // the live renderer's own gate 1, read off the constructed
            // ScriptableRenderer rather than off the asset
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset != null)
            {
                var renderer = urpAsset.GetRenderer(0);
                var pi = renderer?.GetType().GetProperty("postProcessEnabled",
                             BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object v = null;
                try { v = pi?.GetValue(renderer); } catch { }
                Debug.Log($"LOOKPOST id=liverenderer type={renderer?.GetType().Name} " +
                          $"postProcessEnabled={(v != null ? v.ToString().ToLowerInvariant() : "unreadable")}");
            }

            foreach (var v in views)
            {
                liveUcd.renderPostProcessing = true;
                var on = Render(v.pos, v.look, v.up, true);
                liveUcd.renderPostProcessing = false;
                var off = Render(v.pos, v.look, v.up, true);
                Save(v.id + "_linear_nopost");
                FrameStats(v.id + "_linear_nopost", off);
                liveUcd.renderPostProcessing = true;
                double d2 = MeanAbsDiff(on, off);
                Debug.Log($"LOOKDIFF id={v.id} postOnVsOff meanAbs={d2:F2}");
                Check($"postChangesPixels_{v.id}", d2 > 1.0,
                      "if turning the camera's post stack off changes nothing, it was never executing");
            }
        }

        static Bounds? WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            bool any = false; var b = new Bounds();
            foreach (var r in rs)
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b : (Bounds?)null;
        }
    }
}
