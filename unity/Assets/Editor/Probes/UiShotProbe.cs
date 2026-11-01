using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TbilisiFly.Core;
using TbilisiFly.UI.Common;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE UI COLOUR PROBE. The project went Gamma -> Linear
    /// (ProjectSettings.asset m_ActiveColorSpace: 1) to fix the outdoor
    /// white-out, with every measured light value unchanged — and the UI has
    /// never been looked at since. The argument that it is safe ("UI Toolkit
    /// does its own gamma handling and screen-space-overlay panels composite
    /// after post") is REASONING. This file is the measurement.
    ///
    ///   Unity -batchmode -quit -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.UiShotProbe.Run -logFile ...
    ///
    /// (drop -nographics — it renders)
    ///
    /// WHAT IT DOES
    ///   1. Drives the REAL shell on the REAL Game.unity through
    ///      Login -> Character -> the world, exactly the way
    ///      Editor/Probes/HeroScreenProbe.cs does (Awake/Start by reflection,
    ///      no simulated clicks: VisualElement.SendEvent on a panel-less
    ///      element does nothing, so the handlers' own entry points are
    ///      called).
    ///   2. Renders the LIVE panel at 1920x1080 to ui_login.png,
    ///      ui_character.png and ui_hud.png (the last composited over the
    ///      scene camera's own frame).
    ///   3. Measures every CyberTheme colour AS RENDERED against its hex and
    ///      prints one UICOLOR line per colour.
    ///   4. Asserts every character that is actually ON SCREEN resolves in
    ///      the shipped font chain — a tofu box and a glyph look equally
    ///      "rendered" to a pixel diff, which is FontProbe.cs's own point.
    ///
    /// ================= HOW A UI TOOLKIT PANEL IS CAPTURED =================
    /// A screen-space-overlay panel is NOT drawn by Camera.Render() into a
    /// target texture. URP draws overlay UI only when
    /// UniversalCameraData.rendersOverlayUI is true, and that is
    /// `SupportedRenderingFeatures.active.rendersUIOverlay &amp;&amp; resolveToScreen`,
    /// where resolveToScreen is `targetTexture == null &amp;&amp; ...`
    /// (UniversalCameraData.cs:307,384). So the moment a probe points the
    /// camera at a RenderTexture, the UI drops out of the frame — silently,
    /// and with every "UI is installed" flag still true.
    ///
    /// The capture therefore goes through the PANEL instead:
    /// PanelSettings.targetTexture is set to our RenderTexture, which makes it
    /// an OFFSCREEN panel, and the repaint is driven by hand because edit mode
    /// has no player loop. Two paths are tried IN ORDER and the one that
    /// actually produced pixels is printed as UIRENDER path=... :
    ///   A. UnityEngine.UIElements.UIElementsRuntimeUtility's internal
    ///      UpdatePanels() + RepaintPanels(onlyOffscreen:true) /
    ///      RenderOffscreenPanels() statics, by reflection.
    ///   B. the panel object's own Update() + Repaint(Event) (BaseRuntimePanel
    ///      is internal), by reflection.
    /// Neither is public API; both are measured rather than trusted, by
    /// counting non-transparent pixels in the target afterwards.
    ///
    /// ================= WHY THE TARGET IS AN sRGB ARGB32 RT =================
    /// The question is "does #3fd6ff still arrive at the display as #3fd6ff",
    /// so the capture must end where the real game ends: an sRGB-encoded
    /// 8-bit backbuffer. RenderTextureReadWrite.sRGB gives exactly that, and
    /// GetPixels32 then hands back the final display bytes, directly
    /// comparable with ColorUtility's parse of the hex. (LookShotProbe.cs
    /// needs an HDR float target and a hand-rolled sRGB encode for the SCENE
    /// because ACES has to see unclamped values; the UI has no tonemapper in
    /// front of it, so that machinery would only add a conversion of its own.)
    /// </summary>
    public static class UiShotProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string OutDefault =
            "/private/tmp/claude-501/-Users-wazzap-tbilisifly/0f5488ea-9a85-4533-ad67-e5fe68c33649/scratchpad/shots";
        const int W = 1920, H = 1080;

        static string _out;
        static bool _allPass = true;
        static string _phase = "start";
        static readonly List<string> Errors = new List<string>();
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        /// <summary>Same single exemption HeroScreenProbe.cs documents and
        /// counts separately: Object.Destroy is illegal outside Play mode and
        /// Unity answers it with an ERROR log, which CharacterPreviewRig's
        /// correct body-swap code triggers. Counted and printed, never
        /// dropped.</summary>
        const string EditModeDestroy = "Destroy may not be called from edit mode";
        static readonly List<string> EditModeDestroys = new List<string>();

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = OutDefault;
            Directory.CreateDirectory(_out);

            Application.logMessageReceived += OnLog;
            Debug.Log($"UISHOT out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"colorSpace={QualitySettings.activeColorSpace}");
            Check("colorSpaceLinear", QualitySettings.activeColorSpace == ColorSpace.Linear,
                  "this probe exists because the project switched to Linear");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log("UISHOT id=nogpu msg=needs a run WITHOUT -nographics");
                _allPass = false;
                Finish();
                return;
            }

            try { Body(); }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"FAIL probeThrew ({_phase}: {e.GetType().Name}: {e.Message})");
                Debug.Log(e.StackTrace);
            }
            Application.logMessageReceived -= OnLog;
            Finish();
        }

        static void Finish()
        {
            foreach (var kv in PanelRt)
            {
                if (kv.Key != null) kv.Key.targetTexture = null;
                if (kv.Value != null) { kv.Value.Release(); UnityEngine.Object.DestroyImmediate(kv.Value); }
            }
            PanelRt.Clear();

            Debug.Log($"UISHOT_ERROR_COUNT={Errors.Count}");
            for (int i = 0; i < Errors.Count; i++) Debug.Log($"UISHOT_ERROR_{i}={Errors[i]}");
            Debug.Log($"UISHOT_EDITMODE_DESTROY_COUNT={EditModeDestroys.Count}");
            Debug.Log($"UISHOT wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "UISHOTPROBE RESULT=PASS" : "UISHOTPROBE RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string line = $"[{_phase}] {type}: {condition}";
            if (condition != null && condition.Contains(EditModeDestroy)) EditModeDestroys.Add(line);
            else Errors.Add(line);
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = cond ? $"PASS {key}" : $"FAIL {key}";
            if (!cond && note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Fact(string key, object v) => Debug.Log($"{key}={v}");

        // ==================================================================
        static void Body()
        {
            // ---------------------------------------------------------- open
            _phase = "open";
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());
            if (!scene.IsValid()) return;

            var boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            var game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            Check("bootInScene", boot != null);
            Check("gameLoopInScene", game != null);
            if (boot == null || game == null) return;

            TokenStore.ClearToken();

            // --------------------------------------------------------- login
            _phase = "login";
            Invoke(boot, "Awake");
            var shell = boot.ShellUi;
            Check("shellBuilt", shell != null);
            if (shell == null) return;
            Invoke(boot, "Start");
            Fact("SCREEN_AFTER_BOOT", boot.Screen);
            Check("startsOnLogin", boot.Screen == BootScreen.Login);

            var shellDoc = FindDoc("Shell UI Panel");
            Check("shellPanelFound", shellDoc != null);
            if (shellDoc == null) return;
            PanelFacts("shell", shellDoc);
            PinPanel(shellDoc);

            // THE WRAPPER-COLLAPSE MEASUREMENT, with a control arm. See
            // UI/Common/UiBootstrap.FillPanel's header: a C# view that clones
            // an inset:0 UXML root is an extra level the DOM never had, and
            // without an explicit absolute/inset-0 on the WRAPPER every
            // full-bleed background and every vertical centring silently
            // collapses to zero height while the text still draws.
            LayoutPanels(shellDoc);
            StyleSheetAssetFacts();
            StyleFacts("shell", shell.Shell, "shell-root", "shell-root");
            StyleFacts("character", shell.Character, "hangar", "hangar");
            RectFacts("live", shell.Shell, "shell-root", "shell-backdrop", "screen-login",
                      "login-panel", "chrome", "shell-screens");
            WrapperCollapseControl(shellDoc);
            var backdrop = shell.Shell.Q("shell-backdrop");
            Check("shellBackdropFillsScreen",
                  backdrop != null && backdrop.worldBound.width > 1900f && backdrop.worldBound.height > 1060f,
                  backdrop == null ? "no #shell-backdrop" : $"backdrop is {backdrop.worldBound.width:F0}x{backdrop.worldBound.height:F0}");

            // The colour measurement happens on the LOGIN panel, because the
            // shell carries every theme token (the HUD's own palette is
            // deliberately NOT the theme's — HudView.cs:79).
            MeasureColours(shellDoc);
            MeasureThemeOnPanel("shell", shellDoc);
            GlyphCheck("login", shellDoc);
            ShootUi("ui_login", shellDoc, null);

            // ----------------------------------------------------- character
            _phase = "character";
            var user = new PublicUser
            {
                username = "probepilot", level = 7, stars = 3, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    new CharacterDef { hid = "h-ui-1", name = "ვეფხია", race = "veggie", model = "eggplant" },
                    new CharacterDef { hid = "h-ui-2", name = "სპილო", race = "toys", model = "elephant" },
                },
            };
            Invoke(boot, "SetUser", user);
            boot.OpenCharacter();
            Fact("SCREEN_AFTER_LOGIN", boot.Screen);
            Check("characterScreenOpen", boot.Screen == BootScreen.Character);
            // WHERE IS THE PREVIEW BODY, before anything ticks? The rig's own
            // camera lives at UI/Shell/ShellUi.PreviewWorldY (-5000), so a body
            // under the rig must be down there too. Sampled before and after
            // the tick loop so "it was never parented" and "a tick moved it"
            // are told apart instead of guessed.
            BodyPos("afterOpenCharacter", shell.Preview);
            // The preview RenderTexture is sized from the UI tile that shows
            // it, so the panel must have laid out BEFORE the rig builds it —
            // otherwise it is created 0x0 and the render graph throws.
            LayoutPanels(shellDoc);
            // main.js:313-316's rAF loop — the preview rig needs ticks before
            // its body is posed, and the preview camera needs a Render() of
            // its own (edit mode runs neither).
            BodyPos("beforeTicks", shell.Preview);
            for (int i = 0; i < 20; i++) shell.Tick(1f / 60f);
            BodyPos("afterTicks", shell.Preview);
            LayoutPanels(shellDoc);
            // CharacterView.ResizePreview is normally reached by the panel
            // SCHEDULER (schedule.Execute, CharacterView.cs:266) and by
            // GeometryChangedEvent — neither of which runs in edit mode, so
            // the preview RenderTexture stays at the 1x1 it was created with
            // and URP's render graph then throws on the half-res textures it
            // derives from a 1x1 target. In the real game both paths fire.
            InvokeIfPresent(shell.Character, "ResizePreview");
            LayoutPanels(shellDoc);
            var previewImg = shell.Character != null ? shell.Character.Q("char-preview") : null;
            var previewRt = shell.Preview != null ? shell.Preview.Target : null;
            Debug.Log($"UIPREVIEW imgRect={(previewImg != null ? previewImg.worldBound.ToString() : "<missing>")} " +
                      $"rt={(previewRt != null ? previewRt.width + "x" + previewRt.height : "<null>")} " +
                      $"imgTexture={(previewImg is UnityEngine.UIElements.Image im && im.image != null ? im.image.name : "<null>")}");
            Check("heroPreviewHasRealSize", previewRt != null && previewRt.width >= 64 && previewRt.height >= 64,
                  "the personage screen's 1:1 HOLO SCAN tile is a 1x1 texture");
            LayoutPanels(shellDoc);
            TickPreviewCameras();
            PreviewRigFacts(shell.Preview);
            RectFacts("character", shell.Character, "hangar", "hangar-head");
            var hangar = shell.Character != null ? shell.Character.Q("hangar") : null;
            Check("characterUssApplied",
                  hangar != null && hangar.resolvedStyle.paddingTop > 70f && hangar.resolvedStyle.paddingLeft > 40f,
                  hangar == null ? "no #hangar"
                      : $".hangar's own `padding: 74px 44px 44px` resolved to {hangar.resolvedStyle.paddingTop:F0}/"
                        + $"{hangar.resolvedStyle.paddingLeft:F0} — Character.uss did not import");
            MeasureThemeOnPanel("shellCharacter", shellDoc);
            GlyphCheck("character", shellDoc);
            ShootUi("ui_character", shellDoc, null);

            // ---------------------------------------------------------- game
            _phase = "enterWorld";
            int before = Errors.Count;
            boot.EnterWorldWithSelectedHero();
            Fact("SCREEN_AFTER_ENTER", boot.Screen);
            Check("enterGoesToGame", boot.Screen == BootScreen.Game);
            Fact("ENTER_WORLD_ERRORS", Errors.Count - before);

            var gameDoc = FindDoc("UI Panel");
            Check("gamePanelFound", gameDoc != null, "CombatWorldAdapter.InstallUi did not run");
            if (gameDoc == null) return;
            PanelFacts("game", gameDoc);
            PinPanel(gameDoc);

            _phase = "tick";
            for (int i = 0; i < 90; i++) game.Tick(1f / 60f);
            Fact("GAME_STARTED", game.Started);
            Fact("TICK_ERRORS", Errors.Count - before);

            _phase = "hudContent";
            DriveHudContent(game, gameDoc);
            // NO further game.Tick here on purpose: StageCombatAndFx would call
            // RangeLaneUi.Tick with an empty LaneHud and hide the lane card
            // again. Layout for the capture comes from the panel's own update
            // pass inside RenderPanel/LayoutPanels, not from a game frame.
            MeasureThemeOnPanel("game", gameDoc);
            UssVerdict();
            GlyphCheck("hud", gameDoc);

            _phase = "shoot";
            var sceneFrame = ShootScene(game);
            ShootUi("ui_hud", gameDoc, sceneFrame);

            _phase = "fontchain";
            // The font assets are rebuilt/reimported like every other asset
            // when the colour space changes, so the chain is re-asserted here
            // in the same launch rather than taken on trust.
            try { FontProbe.Run(); }
            catch (Exception e) { Debug.Log($"UIFONT id=fontprobeThrew msg={e.Message}"); }
        }

        // ==================================================================
        // panels
        // ==================================================================
        static UIDocument FindDoc(string goName)
        {
            foreach (var d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include))
                if (d != null && d.gameObject != null && d.gameObject.name == goName) return d;
            return null;
        }

        static void PanelFacts(string id, UIDocument doc)
        {
            var ps = doc.panelSettings;
            Debug.Log($"UIPANEL id={id} panelSettings={(ps != null).ToString().ToLowerInvariant()} " +
                      $"scaleMode={(ps != null ? ps.scaleMode.ToString() : "n/a")} " +
                      $"scale={(ps != null ? ps.scale.ToString("F2") : "n/a")} " +
                      $"clearColor={(ps != null ? ps.clearColor.ToString().ToLowerInvariant() : "n/a")} " +
                      $"colorClearValue={(ps != null ? ps.colorClearValue.ToString() : "n/a")} " +
                      $"textSettings={(ps != null && ps.textSettings != null ? ps.textSettings.name : "NULL")} " +
                      $"root={(doc.rootVisualElement != null).ToString().ToLowerInvariant()} " +
                      $"children={(doc.rootVisualElement != null ? doc.rootVisualElement.childCount : -1)}");
            Check($"panelHasSettings_{id}", ps != null);
            Check($"panelConstantPixelSize_{id}", ps == null || ps.scaleMode == PanelScaleMode.ConstantPixelSize,
                  "UiBootstrap.cs's panel-scale trap — every px constant in the views depends on this");
            Check($"panelHasFontChain_{id}", ps != null && ps.textSettings != null,
                  "no PanelTextSettings == Unity's default face == no Mkhedruli at all");
            Check($"panelHasChildren_{id}", doc.rootVisualElement != null && doc.rootVisualElement.childCount > 0);

            // The capture depends on the panel OBJECT existing and being in
            // UIElementsRuntimeUtility's own list — AddComponent in
            // -executeMethod does not deliver OnEnable, so this is the gate
            // that would make the whole render silently produce an empty
            // texture. Printed, not assumed.
            var panelObj = doc.rootVisualElement != null ? doc.rootVisualElement.panel : null;
            int registered = -1;
            var rtu = FindType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            if (rtu != null)
            {
                foreach (var m in rtu.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "GetSortedPlayerPanels" || m.GetParameters().Length != 0) continue;
                    try
                    {
                        if (m.Invoke(null, null) is System.Collections.ICollection col) registered = col.Count;
                    }
                    catch { }
                    break;
                }
            }
            Debug.Log($"UIPANEL id={id} panelObject={(panelObj != null).ToString().ToLowerInvariant()} " +
                      $"runtimeUtilityType={(rtu != null).ToString().ToLowerInvariant()} registeredPanels={registered}");
            Check($"panelObjectExists_{id}", panelObj != null,
                  "no IPanel means nothing can be repainted into a texture");
        }

        // ==================================================================
        // the offscreen-panel capture
        // ==================================================================
        static string _renderPath = "untried";
        static Color32[] _lastUi;

        static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full, false);
                if (t != null) return t;
            }
            return null;
        }

        static bool CallStatic(Type t, string name)
        {
            if (t == null) return false;
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                object[] args;
                if (ps.Length == 0) args = null;
                else if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) args = new object[] { true };
                else continue;
                try { m.Invoke(null, args); return true; }
                catch (Exception e)
                {
                    Debug.Log($"UIRENDER id=callThrew method={name} msg={(e.InnerException ?? e).Message}");
                    return false;
                }
            }
            return false;
        }

        static bool CallInstance(object target, string name, object[] args, Type[] sig)
        {
            if (target == null) return false;
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo m = sig == null
                    ? t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                  null, Type.EmptyTypes, null)
                    : t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                  null, sig, null);
                if (m == null) continue;
                try { m.Invoke(target, args); return true; }
                catch (Exception e)
                {
                    Debug.Log($"UIRENDER id=callThrew method={name} msg={(e.InnerException ?? e).Message}");
                    return false;
                }
            }
            return false;
        }

        /// <summary>Repaints the panel into whatever target it currently has.
        /// `path` selects which mechanism to try; returns whether the call
        /// chain reported success (the real verdict is the pixel count).</summary>
        static bool DriveRepaint(UIDocument doc, string path)
        {
            var root = doc.rootVisualElement;
            if (path == "runtimeUtility")
            {
                var t = FindType("UnityEngine.UIElements.UIElementsRuntimeUtility");
                if (t == null) { Debug.Log("UIRENDER id=noRuntimeUtilityType"); return false; }
                bool any = false;
                any |= CallStatic(t, "UpdatePanels");
                any |= CallStatic(t, "RepaintPanels");
                any |= CallStatic(t, "RenderOffscreenPanels");
                return any;
            }
            if (path == "panelRepaint")
            {
                var panel = root != null ? root.panel : null;
                if (panel == null) { Debug.Log("UIRENDER id=noPanelObject"); return false; }
                bool any = CallInstance(panel, "Update", null, null);
                var ev = new Event { type = EventType.Repaint };
                any |= CallInstance(panel, "Repaint", new object[] { ev }, new[] { typeof(Event) });
                return any;
            }
            return false;
        }

        static int NonTransparent(Color32[] px)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i++) if (px[i].a > 2) n++;
            return n;
        }

        /// <summary>Renders the panel to an sRGB 8-bit target — see the class
        /// header on why that format and not a float one.</summary>
        static readonly Dictionary<PanelSettings, RenderTexture> PanelRt =
            new Dictionary<PanelSettings, RenderTexture>();

        /// <summary>Pins the panel to a 1920x1080 target and LEAVES IT THERE
        /// for the rest of the probe.
        ///
        /// This is not an optimisation. A panel with no target texture lays
        /// out at Screen.width x Screen.height, and in batch mode that is
        /// 640x480 — so worldBound and the rendered pixels would be in two
        /// different coordinate spaces, and every rect this probe prints or
        /// samples would be wrong by 3x. Setting it once, before anything is
        /// measured, makes "what was laid out" and "what was captured" the
        /// same frame by construction.</summary>
        static RenderTexture PinPanel(UIDocument doc)
        {
            var ps = doc.panelSettings;
            if (ps == null) return null;
            if (PanelRt.TryGetValue(ps, out var have) && have != null) return have;

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1, name = "uishot_panel" };
            rt.Create();
            // transparent clear so the HUD can be composited over the scene;
            // UI Toolkit blends SrcAlpha/OneMinusSrcAlpha against it, so what
            // lands in the target is premultiplied (see Composite()).
            ps.clearColor = true;
            ps.colorClearValue = new Color(0f, 0f, 0f, 0f);
            ps.targetTexture = rt;
            PanelRt[ps] = rt;
            LayoutPanels(doc);
            var root = doc.rootVisualElement;
            Debug.Log($"UIPANEL id=pinned target={W}x{H} rootRect={(root != null ? root.worldBound.ToString() : "null")}");
            return rt;
        }

        static Color32[] RenderPanel(UIDocument doc)
        {
            var rt = PinPanel(doc);
            if (rt == null) return null;

            Color32[] px = null;
            foreach (var path in new[] { "runtimeUtility", "panelRepaint" })
            {
                bool called = DriveRepaint(doc, path);
                px = ReadBack(rt);
                int ink = NonTransparent(px);
                Debug.Log($"UIRENDER path={path} called={called.ToString().ToLowerInvariant()} nonTransparentPx={ink}");
                if (ink > 2000) { _renderPath = path; break; }
            }
            _lastUi = px;
            return px;
        }

        static Color32[] ReadBack(RenderTexture rt)
        {
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            var px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.active = active;
            return px;
        }

        /// <summary>GetPixels32 is bottom-up; every rect this probe samples and
        /// every PNG it writes is top-down, so one flip is done once, here.</summary>
        static Color32[] FlipY(Color32[] px, int w, int h)
        {
            var o = new Color32[px.Length];
            for (int y = 0; y < h; y++)
                Array.Copy(px, y * w, o, (h - 1 - y) * w, w);
            return o;
        }

        static void ShootUi(string id, UIDocument doc, Color32[] sceneFrame)
        {
            var sw = Stopwatch.StartNew();
            var ui = RenderPanel(doc);
            if (ui == null) { Check($"shot_{id}", false, "panel render returned nothing"); return; }
            int ink = NonTransparent(ui);
            var frame = sceneFrame != null ? Composite(ui, sceneFrame) : OverBlack(ui);
            string file = Path.Combine(_out, id + ".png");
            // NO FlipY here. ReadPixels hands back BOTTOM-UP rows, which is the
            // orientation Texture2D.SetPixels32 + EncodeToPNG expect, so the
            // PNG comes out the right way up from the raw buffer. FlipY exists
            // only for SAMPLING, because worldBound is top-down. Flipping both
            // was the first run's mistake and it produced three mirrored
            // screenshots that still passed every numeric check.
            SavePng(file, frame);
            sw.Stop();
            Debug.Log($"UISHOT file={file} path={_renderPath} nonTransparentPx={ink} " +
                      $"composited={(sceneFrame != null).ToString().ToLowerInvariant()} ms={sw.ElapsedMilliseconds}");
            FrameStats(id, frame);
            Check($"shot_{id}", ink > 20000, $"only {ink} non-transparent pixels — the panel drew (almost) nothing");
        }

        static void SavePng(string path, Color32[] px)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        static float ToLin(byte b)
        {
            float c = b / 255f;
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        static byte ToSrgbByte(float lin)
        {
            if (float.IsNaN(lin)) return 0;
            lin = Mathf.Clamp01(lin);
            float s = lin <= 0.0031308f ? lin * 12.92f : 1.055f * Mathf.Pow(lin, 1f / 2.4f) - 0.055f;
            return (byte)Mathf.RoundToInt(s * 255f);
        }

        /// <summary>UI over the scene. The panel target holds premultiplied
        /// values (cleared to 0,0,0,0 then blended SrcAlpha/InvSrcAlpha), so
        /// the composite is `ui + scene*(1-a)` — done in LINEAR, which is what
        /// the real backbuffer blend does now that the project is Linear.</summary>
        static Color32[] Composite(Color32[] ui, Color32[] scene)
        {
            var o = new Color32[ui.Length];
            for (int i = 0; i < ui.Length; i++)
            {
                float a = ui[i].a / 255f;
                o[i] = new Color32(
                    ToSrgbByte(ToLin(ui[i].r) + ToLin(scene[i].r) * (1f - a)),
                    ToSrgbByte(ToLin(ui[i].g) + ToLin(scene[i].g) * (1f - a)),
                    ToSrgbByte(ToLin(ui[i].b) + ToLin(scene[i].b) * (1f - a)), 255);
            }
            return o;
        }

        static Color32[] OverBlack(Color32[] ui)
        {
            var o = new Color32[ui.Length];
            for (int i = 0; i < ui.Length; i++) o[i] = new Color32(ui[i].r, ui[i].g, ui[i].b, 255);
            return o;
        }

        static void FrameStats(string id, Color32[] px)
        {
            long rs = 0, gs = 0, bs = 0;
            int white = 0, dark = 0, magenta = 0;
            var seen = new HashSet<int>();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                rs += c.r; gs += c.g; bs += c.b;
                if (c.r >= 250 && c.g >= 250 && c.b >= 250) white++;
                if (c.r < 12 && c.g < 12 && c.b < 12) dark++;
                if (c.r > 220 && c.b > 220 && c.g < 60) magenta++;
                if (seen.Count < 300000) seen.Add((c.r << 16) | (c.g << 8) | c.b);
            }
            int n = px.Length;
            Debug.Log($"UISTATS id={id} meanR={rs / (double)n:F1} meanG={gs / (double)n:F1} meanB={bs / (double)n:F1} " +
                      $"pctNearWhite={100.0 * white / n:F2} pctDark={100.0 * dark / n:F2} " +
                      $"magenta={magenta} distinctColours={seen.Count}");
            // CyberTheme.Hex returns magenta on a parse failure ("loud failure
            // beats a silent wrong color") and a missing shader draws magenta
            // too, so a wall of it is a real signal, not an aesthetic note.
            Check($"noMagentaWall_{id}", magenta < n / 100, $"{magenta} magenta pixels");
            Check($"notFlat_{id}", seen.Count > 200, "a near-single-colour frame means nothing drew");
        }

        // ==================================================================
        // THE COLOUR MEASUREMENT
        // ==================================================================
        sealed class Swatch
        {
            public string Name, Hex;
            public Color C;
            public VisualElement El;
        }

        static readonly (string name, string hex)[] Opaque =
        {
            ("bg0",   "#05070d"),
            ("bg1",   "#0a0f1a"),
            ("panel", "#080d18"),
            ("cyan",  "#3fd6ff"),
            ("cyan2", "#7ceaff"),
            ("mag",   "#ff3e8a"),
            ("gold",  "#ffd257"),
            ("text",  "#d7ecf7"),
            ("dim",   "#6d8296"),
            ("halo",  "#04141e"),
        };

        // name, hex, alpha — the theme tokens that are NOT opaque. Measured
        // over an opaque bg0 backing so the expected value is computable.
        static readonly (string name, string hex, float a)[] Alpha =
        {
            ("panel_a86",   "#080d18", 0.86f),
            ("line_a28",    "#3fd6ff", 0.28f),
            ("line_dim_a14", "#3fd6ff", 0.14f),
            ("halo_a35",    "#04141e", 0.35f),
        };

        const int Sw = 48, Sh = 28, SwRowY = 0;

        /// <summary>Paints a measurement strip onto the LIVE panel, renders,
        /// samples each swatch's centre pixel and removes the strip again, so
        /// the shipped PNG is clean. This is the measurement that isolates the
        /// colour-space question from every USS/layout question: the input is
        /// literally CyberTheme.Hex and the output is the display byte.</summary>
        static void MeasureColours(UIDocument doc)
        {
            var root = doc.rootVisualElement;
            var strip = new VisualElement { name = "__uicolor_strip", pickingMode = PickingMode.Ignore };
            strip.style.position = Position.Absolute;
            strip.style.left = 0; strip.style.top = SwRowY;
            strip.style.flexDirection = FlexDirection.Column;

            var row1 = new VisualElement { pickingMode = PickingMode.Ignore };
            row1.style.flexDirection = FlexDirection.Row;
            var row2 = new VisualElement { pickingMode = PickingMode.Ignore };
            row2.style.flexDirection = FlexDirection.Row;
            strip.Add(row1);
            strip.Add(row2);

            var opaque = new List<Swatch>();
            foreach (var (name, hex) in Opaque)
            {
                var c = CyberTheme.Hex(hex);
                c.a = 1f;
                var el = new VisualElement { pickingMode = PickingMode.Ignore };
                el.style.width = Sw; el.style.height = Sh;
                el.style.backgroundColor = c;
                row1.Add(el);
                opaque.Add(new Swatch { Name = name, Hex = hex, C = c, El = el });
            }

            var alpha = new List<Swatch>();
            foreach (var (name, hex, a) in Alpha)
            {
                // an opaque bg0 backing, then the real token on top of it
                var back = new VisualElement { pickingMode = PickingMode.Ignore };
                back.style.width = Sw; back.style.height = Sh;
                back.style.backgroundColor = CyberTheme.Hex("#05070d");
                var el = new VisualElement { pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                el.style.left = 0; el.style.top = 0; el.style.width = Sw; el.style.height = Sh;
                el.style.backgroundColor = CyberTheme.Hex(hex, a);
                back.Add(el);
                row2.Add(back);
                alpha.Add(new Swatch { Name = name, Hex = hex, C = CyberTheme.Hex(hex, a), El = back });
            }

            root.Add(strip);               // last child -> painted on top
            var px = RenderPanel(doc);
            if (px == null)
            {
                strip.RemoveFromHierarchy();
                Check("colourStripRendered", false, "panel render returned nothing");
                return;
            }
            var frame = FlipY(px, W, H);

            int worst = 0; string worstName = "-";
            foreach (var s in opaque)
            {
                var r = s.El.worldBound;
                var got = SampleCentre(frame, r);
                var want = new Color32((byte)Mathf.RoundToInt(s.C.r * 255f),
                                       (byte)Mathf.RoundToInt(s.C.g * 255f),
                                       (byte)Mathf.RoundToInt(s.C.b * 255f), 255);
                int d = MaxChannelDelta(got, want);
                Debug.Log($"UICOLOR name={s.Name} hex={s.Hex} rendered=({got.r},{got.g},{got.b}) " +
                          $"expected=({want.r},{want.g},{want.b}) delta={d} " +
                          $"rect=({r.x:F0},{r.y:F0},{r.width:F0},{r.height:F0})");
                if (d > worst) { worst = d; worstName = s.Name; }
            }
            Fact("UICOLOR_WORST_DELTA", worst);
            Fact("UICOLOR_WORST_NAME", worstName);
            // 2 levels of 255 is the rounding floor of an 8-bit sRGB round
            // trip. A gamma mix-up is not a near miss: #3fd6ff written into a
            // linear buffer and then sRGB-encoded comes out ~#8ceaff, i.e.
            // tens of levels, and the reverse is just as loud.
            Check("themeColoursRenderAtTheirHex", worst <= 2,
                  $"worst={worst} on {worstName} — see the UICOLOR lines; a double or missing " +
                  "sRGB conversion shows up here as tens of levels");

            var bg0 = CyberTheme.Hex("#05070d");
            foreach (var s in alpha)
            {
                var r = s.El.worldBound;
                var got = SampleCentre(frame, r);
                // the two candidate blend spaces, both printed, so the answer
                // is read off the numbers instead of assumed
                var lin = new Color32(
                    ToSrgbByte(SrgbToLin(s.C.r) * s.C.a + SrgbToLin(bg0.r) * (1f - s.C.a)),
                    ToSrgbByte(SrgbToLin(s.C.g) * s.C.a + SrgbToLin(bg0.g) * (1f - s.C.a)),
                    ToSrgbByte(SrgbToLin(s.C.b) * s.C.a + SrgbToLin(bg0.b) * (1f - s.C.a)), 255);
                var srgb = new Color32(
                    (byte)Mathf.RoundToInt((s.C.r * s.C.a + bg0.r * (1f - s.C.a)) * 255f),
                    (byte)Mathf.RoundToInt((s.C.g * s.C.a + bg0.g * (1f - s.C.a)) * 255f),
                    (byte)Mathf.RoundToInt((s.C.b * s.C.a + bg0.b * (1f - s.C.a)) * 255f), 255);
                Debug.Log($"UICOLORA name={s.Name} hex={s.Hex} alpha={s.C.a:F2} " +
                          $"rendered=({got.r},{got.g},{got.b}) " +
                          $"expectedLinearBlend=({lin.r},{lin.g},{lin.b}) deltaLinear={MaxChannelDelta(got, lin)} " +
                          $"expectedSrgbBlend=({srgb.r},{srgb.g},{srgb.b}) deltaSrgb={MaxChannelDelta(got, srgb)}");
            }

            strip.RemoveFromHierarchy();
        }

        static float SrgbToLin(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        static Color32 SampleCentre(Color32[] frame, Rect r)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt(r.center.x), 0, W - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(r.center.y), 0, H - 1);
            return frame[y * W + x];
        }

        static int MaxChannelDelta(Color32 a, Color32 b) =>
            Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

        /// <summary>The same question asked of REAL elements that the views
        /// authored themselves, rather than of a probe-made swatch: walk the
        /// live tree, and for every element with an opaque background big
        /// enough to sample, compare its resolved style against the pixel
        /// actually on screen at its top-left inset (a point a child's own
        /// padding keeps clear).</summary>
        static int _ussSampledTotal, _ussBadTotal;

        static void MeasureThemeOnPanel(string id, UIDocument doc)
        {
            var px = RenderPanel(doc);
            if (px == null) return;
            var frame = FlipY(px, W, H);
            int checkedN = 0, bad = 0;
            foreach (var el in Walk(doc.rootVisualElement))
            {
                var bg = el.resolvedStyle.backgroundColor;
                if (bg.a < 0.995f) continue;
                var r = el.worldBound;
                if (r.width < 6f || r.height < 6f) continue;
                if (el.resolvedStyle.display == DisplayStyle.None) continue;
                int x = Mathf.Clamp(Mathf.RoundToInt(r.x + 3f), 0, W - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(r.y + 3f), 0, H - 1);
                var got = frame[y * W + x];
                var want = new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(bg.r) * 255f),
                                       (byte)Mathf.RoundToInt(Mathf.Clamp01(bg.g) * 255f),
                                       (byte)Mathf.RoundToInt(Mathf.Clamp01(bg.b) * 255f), 255);
                int d = MaxChannelDelta(got, want);
                checkedN++;
                if (d > 8) { bad++; if (bad <= 12) Debug.Log($"UICOLORUSS panel={id} name={(string.IsNullOrEmpty(el.name) ? el.GetType().Name : el.name)} " +
                        $"authored=({want.r},{want.g},{want.b}) rendered=({got.r},{got.g},{got.b}) delta={d}"); }
                if (checkedN > 400) break;
            }
            Debug.Log($"UICOLORUSS panel={id} sampled={checkedN} offByMoreThan8={bad}");
            _ussSampledTotal += checkedN;
            _ussBadTotal += bad;
        }

        /// <summary>The verdict over both panels. Split from the per-panel pass
        /// because the game panel is almost entirely Painter2D-drawn (HudView
        /// and friends paint into generateVisualContent and leave
        /// background-color transparent), so it legitimately offers very few
        /// opaque backgrounds to sample — and "sampled=0" passing would be
        /// exactly this project's known false-pass shape.</summary>
        static void UssVerdict()
        {
            Fact("UICOLORUSS_SAMPLED_TOTAL", _ussSampledTotal);
            Fact("UICOLORUSS_OFF_BY_MORE_THAN_8_TOTAL", _ussBadTotal);
            Check("ussBackgroundsSampled", _ussSampledTotal > 0,
                  "nothing was compared, so this check proved nothing");
            // An element can legitimately differ: something opaque may be
            // drawn OVER its top-left inset. So the per-element lines are
            // reported and only a wholesale mismatch is a failure.
            Check("ussBackgroundsMostlyMatch", _ussSampledTotal > 0 && _ussBadTotal * 4 < _ussSampledTotal,
                  $"{_ussBadTotal} of {_ussSampledTotal} sampled elements are off by more than 8 levels");
        }

        static IEnumerable<VisualElement> Walk(VisualElement e)
        {
            if (e == null) yield break;
            foreach (var c in e.Children())
            {
                yield return c;
                foreach (var g in Walk(c)) yield return g;
            }
        }

        // ==================================================================
        // GLYPHS — is anything on screen a tofu box?
        // ==================================================================
        /// <summary>A layout-only pass. resolvedStyle (and therefore
        /// worldBound and the display test below) is NaN/default until the
        /// panel has updated at least once, and edit mode has no player loop
        /// to do it.</summary>
        static void LayoutPanels(UIDocument doc)
        {
            var t = FindType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            if (t != null) CallStatic(t, "UpdatePanels");
            var panel = doc.rootVisualElement != null ? doc.rootVisualElement.panel : null;
            if (panel != null) CallInstance(panel, "Update", null, null);
        }

        /// <summary>EVERY stylesheet in Assets/UI, loaded as an ASSET and
        /// asked how many rules it actually has.
        ///
        /// A .uss that fails to import does NOT disappear and does NOT throw:
        /// AssetDatabase still returns a StyleSheet with the right name, the
        /// view still attaches it, and every rule in it is simply gone. That
        /// is how Character.uss shipped empty for three days
        /// (`letter-spacing: 0.06em` — USS has no em unit). `rules` and
        /// `complexSelectors` are internal, hence the reflection.</summary>
        static void StyleSheetAssetFacts()
        {
            string[] paths =
            {
                "Assets/Resources/UI/Theme/CyberTheme.uss", "Assets/Resources/UI/Shell/Shell.uss",
                "Assets/Resources/UI/Character/Character.uss", "Assets/Resources/UI/Settings/Settings.uss",
                "Assets/Resources/UI/Target/TargetCard.uss", "Assets/Resources/UI/Credits/Credits.uss",
                "Assets/Resources/UI/Map/Map.uss", "Assets/Resources/UI/SkillBar/ActionBar.uss",
                "Assets/Resources/UI/Cinema/CinemaOverlay.uss",
            };
            int empty = 0;
            foreach (var path in paths)
            {
                var ss = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                int rules = -1, selectors = -1;
                if (ss != null)
                {
                    rules = ArrayLen(ss, "rules");
                    selectors = ArrayLen(ss, "complexSelectors");
                }
                bool bad = ss == null || rules <= 0;
                if (bad) empty++;
                Debug.Log($"UISHEET path={path} loaded={(ss != null).ToString().ToLowerInvariant()} " +
                          $"rules={rules} complexSelectors={selectors}");
            }
            Check("everyStyleSheetImported", empty == 0,
                  $"{empty} of {paths.Length} .uss files import to a stylesheet with no rules — "
                  + "that view renders completely unstyled with no error anywhere");
        }

        static int ArrayLen(object target, string member)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var pr = t.GetProperty(member, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object v = null;
                try { v = pr?.GetValue(target); } catch { }
                if (v == null)
                {
                    var f = t.GetField(member, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? t.GetField("m_" + char.ToUpperInvariant(member[0]) + member.Substring(1),
                                       BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    try { v = f?.GetValue(target); } catch { }
                }
                if (v is Array a) return a.Length;
            }
            return -1;
        }

        /// <summary>Is the view's own stylesheet actually ON it, and did its
        /// rules reach the tree? `<Style src=...>` at a UXML's root attaches to
        /// whatever CloneTree was given, and a stylesheet that silently failed
        /// to attach produces a screen that is all there and completely
        /// unstyled — which is exactly what the first ui_character.png
        /// showed.</summary>
        static void StyleFacts(string label, VisualElement view, string probeChild, string probeClass)
        {
            if (view == null) { Debug.Log($"UISTYLE arm={label} view=<null>"); return; }
            var names = new List<string>();
            for (int i = 0; i < view.styleSheets.count; i++)
            {
                var ss = view.styleSheets[i];
                names.Add(ss != null ? ss.name : "<null>");
            }
            var el = probeChild != null ? view.Q(probeChild) : null;
            string resolved = "<missing>";
            if (el != null)
            {
                var rs = el.resolvedStyle;
                resolved = $"padTop={rs.paddingTop:F0} padLeft={rs.paddingLeft:F0} bg={rs.backgroundColor} " +
                           $"classes={string.Join("|", el.GetClasses())}";
            }
            Debug.Log($"UISTYLE arm={label} sheets={view.styleSheets.count} [{string.Join(", ", names)}] " +
                      $"probe={probeChild} expectClass={probeClass} {resolved}");
        }

        static void RectFacts(string label, VisualElement root, params string[] names)
        {
            if (root == null) return;
            var r0 = root.worldBound;
            Debug.Log($"UISHOTRECT arm={label} name=<wrapper> rect=({r0.x:F0},{r0.y:F0},{r0.width:F0},{r0.height:F0})");
            foreach (var n in names)
            {
                var el = root.Q(n);
                if (el == null) { Debug.Log($"UISHOTRECT arm={label} name={n} rect=<missing>"); continue; }
                var r = el.worldBound;
                Debug.Log($"UISHOTRECT arm={label} name={n} rect=({r.x:F0},{r.y:F0},{r.width:F0},{r.height:F0})");
            }
        }

        /// <summary>The control arm for the wrapper-collapse finding: the SAME
        /// UXML cloned into a bare VisualElement that does NOT get
        /// UiBootstrap.FillPanel, on the SAME panel, measured and removed
        /// again. Without it, "the backdrop fills the screen" would only prove
        /// that it fills the screen today, not that the wrapper styling is
        /// what makes it.</summary>
        static void WrapperCollapseControl(UIDocument doc)
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Shell/Shell.uxml");
            if (uxml == null) { Debug.Log("UISHOTRECT arm=control msg=Shell.uxml did not load"); return; }
            var bare = new VisualElement { name = "__wrapper_control", pickingMode = PickingMode.Ignore };
            uxml.CloneTree(bare);
            doc.rootVisualElement.Add(bare);
            LayoutPanels(doc);
            RectFacts("control_noFillPanel", bare, "shell-root", "shell-backdrop", "screen-login");
            var br = bare.Q("shell-backdrop");
            Check("controlArmCollapses", br != null && br.worldBound.height < 1f,
                  br == null ? "no #shell-backdrop in the control clone"
                             : $"the unstyled wrapper did NOT collapse ({br.worldBound.height:F0} high) — " +
                               "then FillPanel is not what fixes this and the finding is wrong");
            bare.RemoveFromHierarchy();
            LayoutPanels(doc);
        }

        static void GlyphCheck(string id, UIDocument doc)
        {
            LayoutPanels(doc);
            var ps = doc.panelSettings;
            var pts = ps != null ? ps.textSettings : null;
            var chain = new List<FontAsset>();
            if (pts != null)
            {
                if (pts.defaultFontAsset != null) chain.Add(pts.defaultFontAsset);
                if (pts.defaultFontAsset?.fallbackFontAssetTable != null)
                    chain.AddRange(pts.defaultFontAsset.fallbackFontAssetTable);
                if (pts.fallbackFontAssets != null) chain.AddRange(pts.fallbackFontAssets);
            }
            Fact($"UIGLYPH_CHAIN_{id}", chain.Count);

            var cps = new SortedDictionary<int, int>();
            int texts = 0, georgianTexts = 0;
            foreach (var el in Walk(doc.rootVisualElement))
            {
                // fully qualified: UnityEngine.TextCore.Text (needed for
                // FontAsset below) has a TextElement of its own.
                string s = el is UnityEngine.UIElements.TextElement te ? te.text : null;
                if (string.IsNullOrEmpty(s)) continue;
                if (el.resolvedStyle.display == DisplayStyle.None) continue;
                texts++;
                bool geo = false;
                for (int i = 0; i < s.Length; i++)
                {
                    int cp = char.ConvertToUtf32(s, i);
                    if (char.IsHighSurrogate(s[i])) i++;
                    if (cp >= 0x10A0 && cp <= 0x10FF) geo = true;
                    if (cp < 0x20) continue;
                    cps.TryGetValue(cp, out int c); cps[cp] = c + 1;
                }
                if (geo) georgianTexts++;
            }

            int missing = 0;
            var missed = new List<string>();
            foreach (var kv in cps)
            {
                bool ok = false;
                foreach (var fa in chain)
                {
                    if (fa == null) continue;
                    // tryAddCharacter: the atlases are DYNAMIC, so a glyph the
                    // face owns but has not rasterised yet answers false until
                    // it is asked to add it (FontProbe.cs's own note).
                    if (fa.HasCharacter((uint)kv.Key, searchFallbacks: true, tryAddCharacter: true)) { ok = true; break; }
                }
                if (!ok) { missing++; if (missed.Count < 40) missed.Add($"U+{kv.Key:X4}"); }
            }
            Debug.Log($"UIGLYPH id={id} textElements={texts} georgianElements={georgianTexts} " +
                      $"codepointsInFrame={cps.Count} unresolvable={missing} " +
                      $"missing={string.Join(",", missed)}");
            Check($"everyGlyphInFrameResolves_{id}", missing == 0,
                  $"{missing} codepoints on screen have no glyph in the chain — those are tofu boxes");
            Check($"georgianTextInFrame_{id}", georgianTexts > 0,
                  "no Georgian label on this screen at all, which cannot be right for this game");
        }

        // ==================================================================
        // content for the HUD shot
        // ==================================================================
        static void DriveHudContent(GameLoop game, UIDocument doc)
        {
            // the hint line — src/game.js:1317's own Georgian prompt
            Live("HintLineView", game.HintLine != null);
            game.OnHint?.Invoke("💥 შენობას შეეხე — კორპუსი დაზიანდა!");

            // the real-address hit toast, through the ported formatter
            Live("HitToastView", game.HitToast != null);
            game.HitToast?.Show(new TbilisiFly.Combat.GunToast.Formatted
            {
                Line = "🏢 რუსთაველის გამზირი 12",
                Pct = 58,
                BarColor0 = new Color32(0xff, 0xb0, 0x2e, 255),
                BarColor1 = new Color32(0xff, 0xd2, 0x57, 255),
            });

            // the range lane card. The live one only self-installs at the
            // range (GameLoop.EnsureRangeLaneUi runs inside the combat stage),
            // so if this frame has none the view is built and parented here —
            // and SAID SO, rather than quietly producing a screenshot of
            // something the game never reached.
            bool laneLive = game.RangeLaneUi != null;
            Live("RangeLaneView", laneLive);
            if (!laneLive)
            {
                game.RangeLaneUi = new TbilisiFly.UI.Combat.RangeLaneView();
                doc.rootVisualElement.Add(game.RangeLaneUi);
            }
            game.RangeLaneUi.Tick(new TbilisiFly.Combat.RangeLanes.LaneHud
            {
                Show = true, DistM = 12.5f, Dir = 0,
                Lane = new TbilisiFly.Combat.RangeLanes.Lane(),
            });

            // the on-foot crosshair. It belongs to Core/HeldWeapon and only
            // exists while a weapon is drawn on foot; the HUD shot is a FLYING
            // frame, so the same forced-and-labelled rule applies.
            // HeldWeapon is a PLAIN class, not a MonoBehaviour, so it cannot be
            // found with FindAnyObjectByType; CombatWorldAdapter registers it
            // into GameLoop.CombatSystems (it is an ITickable), which is the
            // only live handle on it.
            HeldWeapon held = null;
            foreach (var t in game.CombatSystems) if (t is HeldWeapon hw) { held = hw; break; }
            Fact("HELD_WEAPON_FOUND", held != null);
            var reticle = held != null ? held.ReticleUi : null;
            Live("ReticleView", reticle != null);
            if (reticle == null)
            {
                reticle = new TbilisiFly.UI.Combat.ReticleView();
                doc.rootVisualElement.Add(reticle);
            }
            reticle.Tick(true, false, 1f);

            // the target card — Tab in the real game (game.js:547-551)
            Live("TargetCardView", game.TargetCard != null);
            var reg = game.Targets;
            if (reg != null && reg.Current == null && game.ActivePlane != null)
            {
                var pl = game.ActivePlane;
                reg.Register(new GameLoop.TargetRegistry.Entry
                {
                    Wrapper = pl.transform, Id = pl.PlaneId, Name = pl.PlaneId,
                    Sub = "DPS", RoleIsTank = false, IsOwn = false,
                    HpGetter = () => 72, MaxHp = 100,
                });
                reg.Cycle(game.MainCamera);
            }
            Fact("TARGET_CURRENT", reg?.Current != null ? reg.Current.Id : "<null>");
        }

        static void Live(string view, bool live) =>
            Debug.Log($"UIVIEW name={view} live={live.ToString().ToLowerInvariant()} " +
                      $"forced={(!live).ToString().ToLowerInvariant()}");

        // ==================================================================
        // the scene behind the HUD — the camera placement/ticking pattern is
        // Editor/Probes/LookShotProbe.cs's (that file is another agent's and
        // is not edited; only its shape is reused).
        // ==================================================================
        static readonly string[] RendererTypeNames =
        {
            "TbilisiFly.City.CityRenderer", "TbilisiFly.City.ParkRenderer",
            "TbilisiFly.City.VegetationRenderer", "TbilisiFly.City.ShopfrontRenderer",
            "TbilisiFly.City.StreetFurnitureRenderer", "TbilisiFly.City.LandmarkRenderer",
            "TbilisiFly.City.MetroRenderer", "TbilisiFly.City.MallRenderer",
            "TbilisiFly.City.WindsockRenderer", "TbilisiFly.City.PlaneHallRenderer",
        };

        static Color32[] ShootScene(GameLoop game)
        {
            var cam = game.MainCamera != null ? game.MainCamera : Camera.main;
            if (cam == null)
            {
                var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                if (all.Length > 0) cam = all[0];
            }
            Check("sceneCameraExists", cam != null);
            if (cam == null) return null;

            cam.enabled = true;
            cam.allowHDR = true;
            if (cam.tag != "MainCamera") { try { cam.tag = "MainCamera"; } catch { } }

            var env = UnityEngine.Object.FindFirstObjectByType<SceneEnvironmentSetup>();
            if (env != null) { env.Cam = cam; env.Setup(); }
            var ucd = cam.GetComponent<UniversalAdditionalCameraData>();
            Debug.Log($"UISHOTSCENE cam={cam.name} pos={cam.transform.position.ToString("F1")} " +
                      $"hasCameraData={(ucd != null).ToString().ToLowerInvariant()} " +
                      $"renderPostProcessing={(ucd != null ? ucd.renderPostProcessing.ToString().ToLowerInvariant() : "n/a")}");

            var tickers = new List<(Component c, MethodInfo tick, MethodInfo late)>();
            foreach (var tn in RendererTypeNames)
            {
                var type = FindType(tn);
                if (type == null) continue;
                // Several of these ported renderers are PLAIN classes, not
                // components (PlaneHallRenderer is one), and FindObjectsByType
                // answers that with a logged ERROR and a null return — which
                // then NREs the foreach. Gate on the constraint instead.
                if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                if (found == null) continue;
                foreach (var o in found)
                {
                    if (!(o is Component c)) continue;
                    var tk = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public,
                                 null, new[] { typeof(Vector3), typeof(float) }, null)
                          ?? type.GetMethod("TickFrame", BindingFlags.Instance | BindingFlags.Public,
                                 null, new[] { typeof(Vector3), typeof(float) }, null);
                    var lu = type.GetMethod("LateUpdate",
                                 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                 null, Type.EmptyTypes, null);
                    tickers.Add((c, tk, lu));
                }
            }
            Fact("UISHOT_SCENE_TICKERS", tickers.Count);

            var desc = new RenderTextureDescriptor(W, H, GraphicsFormat.R16G16B16A16_SFloat,
                                                   GraphicsFormat.D24_UNorm_S8_UInt)
            { msaaSamples = 1, useMipMap = false, autoGenerateMips = false };
            var rt = new RenderTexture(desc);
            rt.Create();
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            for (int i = 0; i <= 8; i++)
            {
                Vector3 p = cam.transform.position;
                foreach (var t in tickers)
                {
                    try
                    {
                        if (t.tick != null) t.tick.Invoke(t.c, new object[] { p, 0.5f });
                        else t.late?.Invoke(t.c, null);
                    }
                    catch { }
                }
                cam.Render();
            }

            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            var src = tex.GetPixels();
            var px = new Color32[W * H];
            // a float target has no sRGB encode of its own
            // (UniversalCameraData.cs:276 requireSrgbConversion), so it is done
            // here — same reasoning as LookShotProbe.cs's header.
            for (int i = 0; i < src.Length; i++)
                px[i] = new Color32(ToSrgbByte(src[i].r), ToSrgbByte(src[i].g), ToSrgbByte(src[i].b), 255);
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.active = active;
            cam.targetTexture = prev;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            FrameStats("scene_behind_hud", px);
            return px;
        }

        /// <summary>Does the HOLO SCAN tile actually contain a body? The
        /// RenderTexture being the right size and bound to the Image proves
        /// only that the plumbing is there; this reads the texture back and
        /// counts non-black pixels, which is the difference between "the
        /// preview works" and "the preview is a correctly sized black
        /// rectangle".</summary>
        static void PreviewRigFacts(object rig)
        {
            if (rig == null) { Debug.Log("UIPREVIEW rig=<null>"); return; }
            var rt = PrivateMember<RenderTexture>(rig, "Target");
            var cam = PrivateMember<Camera>(rig, "_camera");
            var body = PrivateMember<GameObject>(rig, "_currentBody");
            Debug.Log($"UIPREVIEW cam={(cam != null ? cam.name : "<null>")} " +
                      $"enabled={(cam != null && cam.enabled).ToString().ToLowerInvariant()} " +
                      $"clear={(cam != null ? cam.clearFlags.ToString() : "n/a")} " +
                      $"bg={(cam != null ? cam.backgroundColor.ToString() : "n/a")} " +
                      $"camPos={(cam != null ? cam.transform.position.ToString("F1") : "n/a")} " +
                      $"mask={(cam != null ? cam.cullingMask.ToString() : "n/a")} " +
                      $"body={(body != null ? body.name : "<null>")} " +
                      $"bodyPos={(body != null ? body.transform.position.ToString("F1") : "n/a")} " +
                      $"bodyLayer={(body != null ? body.layer.ToString() : "n/a")} " +
                      $"bodyActive={(body != null ? body.activeInHierarchy.ToString().ToLowerInvariant() : "n/a")}");
            if (rt == null) return;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            var px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.active = active;
            int lit = 0;
            for (int i = 0; i < px.Length; i++) if (px[i].r > 16 || px[i].g > 16 || px[i].b > 16) lit++;
            Debug.Log($"UIPREVIEW rtPixels={px.Length} litPixels={lit} pctLit={100.0 * lit / px.Length:F2}");
            Check("heroPreviewHasAnImage", lit > px.Length / 200,
                  $"the HOLO SCAN tile is a correctly sized but {(100.0 * lit / px.Length):F2}% lit rectangle");
        }

        static void BodyPos(string when, object rig)
        {
            if (rig == null) { Debug.Log($"UIPREVIEWPOS when={when} rig=<null>"); return; }
            var body = PrivateMember<GameObject>(rig, "_currentBody");
            if (body == null) { Debug.Log($"UIPREVIEWPOS when={when} body=<null>"); return; }
            var t = body.transform;
            Debug.Log($"UIPREVIEWPOS when={when} body={body.name} world={t.position.ToString("F2")} " +
                      $"local={t.localPosition.ToString("F2")} " +
                      $"parent={(t.parent != null ? t.parent.name : "<none>")} " +
                      $"parentWorld={(t.parent != null ? t.parent.position.ToString("F2") : "n/a")}");
        }

        static T PrivateMember<T>(object target, string name) where T : class
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(target) as T;
                var pr = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (pr != null) { try { return pr.GetValue(target) as T; } catch { return null; } }
            }
            return null;
        }

        static void TickPreviewCameras()
        {
            // The personage preview is a camera rendering into a RenderTexture
            // that the UI samples; in edit mode nothing renders it, so the tile
            // would be blank/stale. Render every camera that has a target
            // texture and is NOT the game camera.
            int n = 0, skipped = 0;
            foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (c == null || c.targetTexture == null) continue;
                // A preview RenderTexture is sized from the UI tile it fills,
                // so before the panel's first layout it can be 0x0 — and
                // rendering into that is an "Explicit size mode ... width or
                // height at zero" exception out of the render graph, not a
                // no-op. LayoutPanels runs first; this is the backstop.
                if (c.targetTexture.width < 8 || c.targetTexture.height < 8) { skipped++; continue; }
                try { c.Render(); n++; } catch (Exception e) { Debug.Log($"UISHOT id=previewCamThrew cam={c.name} msg={e.Message}"); }
            }
            Fact("UISHOT_PREVIEW_CAMERAS_SKIPPED_ZERO_SIZE", skipped);
            Fact("UISHOT_PREVIEW_CAMERAS_RENDERED", n);
        }

        // ==================================================================
        static void InvokeIfPresent(object target, string method)
        {
            if (target == null) return;
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (m == null) { Debug.Log($"UISHOT id=noMethod method={method}"); return; }
            try { m.Invoke(target, null); }
            catch (Exception e) { Debug.Log($"UISHOT id=invokeThrew method={method} msg={(e.InnerException ?? e).Message}"); }
        }

        static void Invoke(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Check($"reflection_{method}", false, "method not found"); return; }
            m.Invoke(target, args);
        }
    }
}
