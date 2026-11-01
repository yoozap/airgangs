using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TbilisiFly.City;
using TbilisiFly.Core;
using TbilisiFly.Data;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE GPU PROOF for the three rendering rows of docs/open_items.json:
    /// `water-envmap`, `shadow-dist-gate` and `kit-impostor`. All three are
    /// LOOK changes, so none of them can be settled by a -nographics probe —
    /// and all three have a before/after that this runs IN ONE LOAD, against
    /// the same scene, the same camera and the same frame, because the project
    /// lock is shared with four other agents.
    ///
    ///   Unity -batchmode -quit -projectPath unity        (NO -nographics)
    ///         -executeMethod TbilisiFly.Probes.RenderingItemsProbe.Run
    ///         -logFile ...          TF_SHOTS=&lt;dir&gt;
    ///
    /// WHY IT IS ITS OWN FILE and not three more methods on
    /// Editor/Probes/RenderShotProbe.cs: that file is the project's shared
    /// frame harness and several agents are in it this week. This duplicates
    /// its scene-load / boot / tick / readback scaffold on purpose — the
    /// duplication is cheap and a merge conflict in the one probe everyone
    /// needs is not.
    ///
    /// THE MEASUREMENT THAT MAKES THE WATER ROW HONEST. "The river reads
    /// bright blue at a grazing angle" is a claim about the WATER's pixels, so
    /// the water's pixels have to be identified before anything is averaged.
    /// <see cref="WaterSite"/> renders a MASK pass first — the water material
    /// temporarily painted magenta with its reflection off, which nothing else
    /// in this project ever is — and then renders the same camera at
    /// envMapIntensity 1.0 and at the JS's 0.3 and reports mean luminance, the
    /// blue ratio and the bright-pixel share over that mask alone. A frame
    /// diff was the obvious shortcut and is wrong: the ripple phase and the
    /// wind sway advance between two manual Camera.Render calls, so a diff
    /// would be measuring the trees.
    ///
    /// ONE RENDERER PER TICK, BY HAND. LateUpdate is not delivered outside
    /// play mode, so every Graphics.RenderMeshInstanced layer in City/ submits
    /// nothing unless it is ticked — and a ticker that THROWS leaves its layer
    /// silently absent from the frame (.claude/skills/unity/SKILL.md). Every
    /// throw is printed.
    /// </summary>
    public static class RenderingItemsProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const int W = 1600, H = 900;

        static string _out;
        static Camera _cam;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out))
                _out = Path.Combine(Path.GetTempPath(), "tf_rendering_shots");
            Directory.CreateDirectory(_out);
            Debug.Log($"RENDERITEMS out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"colorSpace={QualitySettings.activeColorSpace}");

            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("RENDERITEMS id=nogpu msg=needs a run WITHOUT -nographics");
                Debug.Log("RENDERITEMS RESULT=FAIL");
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
                catch (Exception e) { Debug.Log($"RENDERITEMS id=bootthrew msg={e.Message}"); Debug.LogException(e); }

                _cam = game != null && game.MainCamera != null ? game.MainCamera : Camera.main;
                if (_cam == null)
                {
                    var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                    if (all.Length > 0) _cam = all[0];
                }
                Check("cameraExists", _cam != null);
                if (_cam == null) { Done(); return; }
                _cam.enabled = true;

                var env = UnityEngine.Object.FindFirstObjectByType<SceneEnvironmentSetup>();
                Check("sceneEnvironmentSetupExists", env != null);
                if (env != null) { env.Cam = _cam; env.Setup(); }

                CollectTickers();
                StartRecorders();
                ReportState();

                WaterPhase();
                ShadowPhase(game);
                ImpostorPhase();
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"RENDERITEMS id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        // ==================================================================
        // what is actually on screen — the "is the gate open" lines
        // ==================================================================
        static void ReportState()
        {
            ShaderReport("Assets/Shaders/CityWater.shader");
            ShaderReport("Assets/Shaders/CityImpostor.shader");
            var water = GroundMaterials.Water();
            Debug.Log($"RISTATE waterShader={(water != null ? water.shader.name : "null")} " +
                      $"waterCustom={GroundMaterials.WaterUsesCustomShader.ToString().ToLowerInvariant()} " +
                      $"hasEnvProp={(water != null && water.HasProperty(EnvProp)).ToString().ToLowerInvariant()} " +
                      $"env={(water != null && water.HasProperty(EnvProp) ? water.GetFloat(EnvProp) : -1f):F3} " +
                      $"jsEnv={WaterBodies.EnvMapIntensity:F3}");
            Check("waterUsesCustomShader", GroundMaterials.WaterUsesCustomShader,
                  "no TbilisiFly/CityWater -> there is no reflection gate in the picture at all");
            Check("waterEnvIsJsValue",
                  water != null && water.HasProperty(EnvProp) &&
                  Mathf.Abs(water.GetFloat(EnvProp) - WaterBodies.EnvMapIntensity) < 1e-4f);

            var cr = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            Check("cityRendererExists", cr != null);
            if (cr != null)
            {
                var r = cr.LastReport;
                Debug.Log($"RISTATE impostorBaked={(r != null && r.ImpostorBaked).ToString().ToLowerInvariant()} " +
                          $"variants={r?.ImpostorVariants} batches={r?.ImpostorBatches} " +
                          $"atlas={r?.ImpostorAtlasW}x{r?.ImpostorAtlasH} " +
                          $"meanAlpha={(r != null ? r.ImpostorMeanAlpha : -1f):F3} " +
                          $"bakeMs={(r != null ? r.ImpostorBakeMs : -1f):F1}");
                Check("impostorAtlasBaked", r != null && r.ImpostorBaked && r.ImpostorBatches > 0,
                      "no render-texture bake -> every far chunk is still the untextured box");
            }
            // THE KIT'S REAL SIZE. KITIMPOSTORVIEW came back with every kit
            // variant measuring ~0.2 m (block_2: 0.24 x 0.14 x 0.17) where
            // public/models/panel_block.glb's own accessor bounds say 24.03 x
            // 17.23 x 11.43 m — a factor of 100, which is the FBX
            // centimetre-unit conversion Unity bakes into the MESH. Whether
            // that matters depends ENTIRELY on where the compensating 100
            // lives: on the prefab's transform (in which case
            // CityRenderer.ResolveVariant, which takes `mf.sharedMesh` and
            // nothing else, is dropping it) or nowhere. Print both.
            KitScaleReport("Assets/Resources/Art/Props/panel_block.fbx", "block_4");
            KitScaleReport("Assets/Resources/Art/Props/houses.fbx", "house_e");

            // THE ATLAS ITSELF. A white impostor box and an empty atlas look
            // identical from the renderer's side, and so do "the texture was
            // never bound" and "the UVs miss it" — so write the atlas out and
            // read the material the draw actually uses.
            var bake = KitImpostorBake.Last;
            if (bake != null && bake.Atlas != null)
            {
                try
                {
                    var png = bake.Atlas.EncodeToPNG();
                    File.WriteAllBytes(Path.Combine(_out, "kit_impostor_atlas.png"), png);
                    var px = bake.Atlas.GetPixels32();
                    long r = 0, g = 0, bl = 0, a = 0; int opaque = 0;
                    for (int i = 0; i < px.Length; i++)
                    { r += px[i].r; g += px[i].g; bl += px[i].b; a += px[i].a; if (px[i].a > 89) opaque++; }
                    Debug.Log($"RIATLAS size={bake.Atlas.width}x{bake.Atlas.height} " +
                              $"meanRGBA={(double)r / px.Length:F1},{(double)g / px.Length:F1}," +
                              $"{(double)bl / px.Length:F1},{(double)a / px.Length:F1} " +
                              $"opaquePct={(100.0 * opaque / px.Length):F1} " +
                              $"mips={bake.Atlas.mipmapCount} format={bake.Atlas.format} " +
                              $"file={Path.Combine(_out, "kit_impostor_atlas.png")}");
                    Check("atlasHasContent", opaque > px.Length / 20, "the atlas is empty -> the bake drew nothing");
                }
                catch (Exception e) { Debug.Log($"RIATLAS id=failed msg={e.Message}"); }
                Debug.Log($"RIATLAS material={(bake.Material != null ? bake.Material.name : "null")} " +
                          $"shader={(bake.Material != null ? bake.Material.shader.name : "null")} " +
                          $"baseMapBound={(bake.Material != null && bake.Material.GetTexture("_BaseMap") != null).ToString().ToLowerInvariant()} " +
                          $"cutoff={(bake.Material != null ? bake.Material.GetFloat("_Cutoff") : -1f):F2} " +
                          $"boxes={bake.Boxes.Count}");
            }
            else Debug.Log("RIATLAS id=absent note=KitImpostorBake.Last is null or its atlas was destroyed");

            // ...and the material the RENDERER holds, which is the one that
            // draws. A batch whose ImpostorMaterials[0] has no _BaseMap draws a
            // WHITE box, because an unbound sampler reads as white.
            if (cr != null) ImpostorMaterialReport(cr);

            var ri = UnityEngine.Object.FindFirstObjectByType<RiverRenderer>();
            Debug.Log($"RISTATE riverRenderer={(ri != null).ToString().ToLowerInvariant()} " +
                      $"sheetBuilt={(ri != null && ri.LastReport != null && ri.LastReport.SheetBuilt).ToString().ToLowerInvariant()} " +
                      $"sheetTris={(ri != null && ri.LastReport != null ? ri.LastReport.SheetTris : -1)}");
        }

        /// <summary>The impostor material and mesh of the first kit batch that
        /// has one, read off the live renderer's own batch list by reflection —
        /// the thing the draw call will use, not the thing the bake returned.</summary>
        static void ImpostorMaterialReport(CityRenderer cr)
        {
            var fi = typeof(CityRenderer).GetField("_batches", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = fi?.GetValue(cr) as System.Collections.IList;
            if (list == null) { Debug.Log("RIIMPMAT id=no_batches"); return; }
            for (int i = 0; i < list.Count; i++)
            {
                var b = (CityRenderer.Batch)list[i];
                if (b.ImpostorMaterials == null || b.ImpostorMaterials.Length == 0 || b.ImpostorMaterials[0] == null) continue;
                var m = b.ImpostorMaterials[0];
                var tex = m.GetTexture("_BaseMap");
                var im = b.ImpostorMesh;
                var uv = im != null ? im.uv : null;
                Debug.Log($"RIIMPMAT batch={i} material={m.name} shader={m.shader.name} " +
                          $"baseMap={(tex != null ? tex.name + " " + ((Texture2D)tex).width + "x" + ((Texture2D)tex).height : "NULL")} " +
                          $"instancing={m.enableInstancing.ToString().ToLowerInvariant()} " +
                          $"impostorMesh={(im != null ? im.name : "null")} verts={(im != null ? im.vertexCount : -1)} " +
                          $"uvCount={(uv != null ? uv.Length : -1)} " +
                          $"uv0={(uv != null && uv.Length > 0 ? uv[0].ToString("F4") : "n/a")} " +
                          $"uv3={(uv != null && uv.Length > 3 ? uv[3].ToString("F4") : "n/a")} " +
                          $"plainMesh={(b.PlainImpostorMesh != null ? b.PlainImpostorMesh.name : "null")} " +
                          $"plainUv={(b.PlainImpostorMesh != null ? b.PlainImpostorMesh.uv.Length : -1)}");
                Check("impostorBaseMapBound", tex != null, "an unbound _BaseMap samples as WHITE — that is the white-box look");
                Check("impostorMeshHasUvs", uv != null && uv.Length == im.vertexCount && im.vertexCount == 24,
                      $"verts={(im != null ? im.vertexCount : -1)} uv={(uv != null ? uv.Length : -1)}");
                return;
            }
            Debug.Log("RIIMPMAT id=no_batch_with_impostor_material");
        }

        /// <summary>The imported prefab's transform chain next to its mesh's
        /// own local bounds, for one named kit variant. `worldSize` is what a
        /// plain Instantiate of the prefab would measure on the ground;
        /// `meshSize` is what City/CityRenderer draws, because it instances
        /// `mf.sharedMesh` with a TRS built from (pos, yaw, footprint scale)
        /// and no model scale at all. If those two disagree by 100 the kit is
        /// being drawn at 1/100 size.</summary>
        static void KitScaleReport(string path, string variant)
        {
            var root = ArtAssets.Load<GameObject>(path);
            if (root == null) { Debug.Log($"RIKIT path={path} loaded=false"); return; }
            Debug.Log($"RIKIT path={path} rootLocalScale={root.transform.localScale.ToString("F4")}");
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.gameObject.name != variant && mf.sharedMesh.name != variant) continue;
                var ls = mf.transform.lossyScale;
                var b = mf.sharedMesh.bounds.size;
                Debug.Log($"RIKIT variant={variant} meshSize={b.ToString("F3")} lossyScale={ls.ToString("F3")} " +
                          $"worldSize={(new Vector3(b.x * ls.x, b.y * ls.y, b.z * ls.z)).ToString("F2")} " +
                          $"parent={(mf.transform.parent != null ? mf.transform.parent.name : "none")}");
                return;
            }
            Debug.Log($"RIKIT path={path} variant={variant} notFound=true");
        }

        /// <summary>Every compile message of one of THIS pass's two new
        /// shaders, by name. A shader that fails to compile is still found by
        /// Shader.Find, still makes a Material and just draws magenta
        /// (Shaders/CityWall.shader's header records exactly that defect
        /// sitting undetected in this project for weeks), so the fallbacks in
        /// GroundMaterials.WaterShader/KitImpostorBake.ImpostorShader would
        /// quietly hide a typo as "the feature is off". Print the reason.</summary>
        static void ShaderReport(string path)
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (sh == null) { Debug.Log($"RISHADER path={path} loaded=false"); _allPass = false; return; }
            bool err = ShaderUtil.ShaderHasError(sh);
            int n = ShaderUtil.GetShaderMessageCount(sh);
            Debug.Log($"RISHADER path={path} name={sh.name} hasError={err.ToString().ToLowerInvariant()} " +
                      $"supported={sh.isSupported.ToString().ToLowerInvariant()} messages={n}");
            if (n > 0)
                foreach (var m in ShaderUtil.GetShaderMessages(sh))
                    Debug.Log($"RISHADERMSG path={path} severity={m.severity} line={m.line} msg={m.message} {m.messageDetails}");
            Check($"shaderCompiles_{System.IO.Path.GetFileNameWithoutExtension(path)}", !err && sh.isSupported);
        }

        // ==================================================================
        // 1. water-envmap
        // ==================================================================
        const string EnvProp = "_EnvMapIntensity";

        static void WaterPhase()
        {
            var mat = GroundMaterials.Water();
            if (mat == null || !mat.HasProperty(EnvProp))
            {
                Check("waterPhase", false, "water material has no _EnvMapIntensity to A/B");
                return;
            }

            // A real point ON the sheet, not a guessed lat/lon: walk the river
            // mesh's own vertices for the one nearest Old Town, and take a
            // second vertex ~300 m away as the heading, so the grazing camera
            // looks ALONG the water rather than across it.
            if (!FindRiverLook(out Vector3 surface, out Vector3 along))
            {
                Check("waterPhase", false, "no river_sheet mesh in the scene");
                return;
            }
            Debug.Log($"RIWATER site surface={surface.ToString("F1")} along={along.ToString("F3")}");

            // "3 m over the water looking along it toward the sun": a grazing
            // reflection needs the sun AHEAD of the camera, which is the
            // condition citymap.js:7321-7325's white-mirror-sheet comment is
            // about, so the river heading is flipped if it points away.
            Vector3 sun = SunDirection();
            if (Vector3.Dot(along, -sun) < 0f) along = -along;
            Vector3 camPos = surface + Vector3.up * 3f;
            Vector3 lookAt = surface + along * 420f + Vector3.up * 2.4f; // grazing, a shade above level

            WaterSite("grazing", mat, camPos, lookAt);

            // ...and the view that is ALREADY the right olive and must stay so.
            WaterSite("top", mat, surface + Vector3.up * 140f, surface + along * 30f);

            // ---- WHAT ELSE IS BLUE DOWN THERE -----------------------------
            // The mask pass above showed the broad pale-blue sheet in the
            // grazing frame is NOT the water material (it stayed blue while
            // the water went magenta). The remaining candidate is the carved
            // river BED: City/TerrainBuilder paints ground class 2 with
            // TerrainBuilder.WaterHex, and its own class doc calls that "a
            // blue that never actually renders since WaterBodies/RiverSheet
            // draw a real surface on top". One render with the Terrain root
            // switched off settles which it is.
            TerrainOffDiagnostic(camPos, lookAt);

            mat.SetFloat(EnvProp, WaterBodies.EnvMapIntensity); // leave it as the game ships it
        }

        /// <summary>
        /// One camera, three renders: a MASK, then env 1.0, then env 0.3.
        ///
        /// WHY A MASK PASS AND NOT A FRAME DIFF. Diffing the two env renders
        /// looks like a free water segmentation, and it is not: the water's own
        /// ripple phase (RiverRenderer.Tick drives
        /// GroundMaterials.DriveWaterRipple off realtimeSinceStartup) and every
        /// `_Time`-driven shader in the frame — the wind sway on the trees —
        /// advance BETWEEN two manual Camera.Render calls, so a diff mask
        /// contains the banks as well as the river and the numbers would be
        /// measuring the wind. Instead the water material is temporarily
        /// painted MAGENTA with its reflection off: nothing else in this
        /// project is magenta (Editor/Probes/RenderShotProbe.FrameStats treats
        /// magenta as the error-material signature precisely because it never
        /// occurs), so the mask is exact, deterministic and camera-specific.
        /// The ripple is also pinned to a fixed `uWaterTime` for all three
        /// renders (see <see cref="FrozenWaterTime"/>) so the three frames
        /// differ in nothing but the property under test.
        /// </summary>
        static void WaterSite(string tag, Material mat, Vector3 pos, Vector3 look)
        {
            Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;

            // ---- 1. the mask -----------------------------------------------
            mat.SetFloat(EnvProp, 0f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1f, 0f, 1f, baseColor.a));
            var maskFrame = Render(pos, look, W, H, Warmup);
            SavePng($"river_{tag}_mask");
            var mask = new bool[maskFrame.Px.Length];
            int waterPx = 0;
            for (int i = 0; i < maskFrame.Px.Length; i++)
            {
                var c = maskFrame.Px[i];
                bool m = c.r > 50 && c.b > 50 && c.g * 2 < (c.r + c.b) * 0.35f;
                mask[i] = m;
                if (m) waterPx++;
            }
            Free();
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);

            double pct = 100.0 * waterPx / mask.Length;
            Debug.Log($"RIWATERMASK tag={tag} waterPx={waterPx} waterPct={pct:F2}");
            Check($"waterInFrame_{tag}", pct > 1.0,
                  $"only {pct:F2}% of the frame is water -> this camera proves nothing about the river");
            if (waterPx == 0) return;

            // ---- 2/3. the same frame at env 1.0 and at the JS's 0.3 --------
            var hi = MaskedStats($"river_{tag}_env100", mat, 1.0f, pos, look, mask, waterPx);
            var lo = MaskedStats($"river_{tag}", mat, WaterBodies.EnvMapIntensity, pos, look, mask, waterPx);

            Debug.Log($"RIWATER tag={tag} " +
                      $"lumEnv100={hi.lum:F1} lumEnv030={lo.lum:F1} deltaLum={(hi.lum - lo.lum):F1} " +
                      $"blueRatioEnv100={hi.blue:F4} blueRatioEnv030={lo.blue:F4} " +
                      $"brightPctEnv100={hi.brightPct:F2} brightPctEnv030={lo.brightPct:F2} " +
                      $"rgbEnv100={hi.r:F1},{hi.g:F1},{hi.b:F1} rgbEnv030={lo.r:F1},{lo.g:F1},{lo.b:F1}");

            if (tag == "grazing")
            {
                // The gate must DARKEN the grazing water, pull it back off blue
                // and stop it reading as a bright sheet.
                Check("grazingWaterIsDarker", lo.lum < hi.lum - 2.0, $"lum {hi.lum:F1} -> {lo.lum:F1}");
                Check("grazingWaterNotBluer", lo.blue <= hi.blue + 1e-4, $"blueRatio {hi.blue:F4} -> {lo.blue:F4}");
                Check("grazingWaterNotASheet", lo.brightPct < 20.0, $"brightPct={lo.brightPct:F2}");
                // ...and it must still be WATER, not a flat painted plane: the
                // olive 0x566440 at this light level sits well above black.
                Check("grazingWaterNotBlack", lo.lum > 8.0, $"lum={lo.lum:F1}");
            }
            else
            {
                // The top-down view was already right, so the fresnel-weighted
                // gate must barely touch it. 0.5 of 255 is below a quantisation
                // step on screen; the real assertion is the RATIO against the
                // grazing delta, which the report line above carries.
                Check("topDownWaterBarelyMoves", hi.lum - lo.lum < 14.0,
                      $"deltaLum={(hi.lum - lo.lum):F1} (grazing must move much more than this)");
            }
        }

        /// <summary>Renders the grazing river frame once with the scene's
        /// `Terrain` root disabled, and reports how much of the pale blue goes
        /// with it. Diagnostic only — it asserts nothing, because the terrain
        /// is not this pass's to change; it exists so the finding is a
        /// measurement and not an inference from one screenshot.</summary>
        static void TerrainOffDiagnostic(Vector3 pos, Vector3 look)
        {
            var terrain = GameObject.Find("Terrain");
            if (terrain == null) { Debug.Log("RITERRAIN id=absent name=Terrain"); return; }
            var rends = terrain.GetComponentsInChildren<MeshRenderer>(true);
            int on = 0;
            foreach (var r in rends) if (r.enabled) { r.enabled = false; on++; }
            var f = Render(pos, look, W, H, Warmup);
            SavePng("river_grazing_terrain_off");
            int pale = CountPaleBlue(f);
            Free();
            foreach (var r in rends) r.enabled = true;

            var f2 = Render(pos, look, W, H, Warmup);
            int pale2 = CountPaleBlue(f2);
            Free();
            Debug.Log($"RITERRAIN tiles={rends.Length} disabled={on} " +
                      $"paleBluePxTerrainOff={pale} paleBluePxTerrainOn={pale2} " +
                      $"waterHex={TerrainBuilder.WaterHex} " +
                      $"note=if-the-count-collapses-the-pale-blue-river-is-the-CARVED-BED-not-the-water-material");
        }

        /// <summary>Pixels that read as the pale blue the grazing river frame
        /// is full of: clearly blue-dominant and bright. Deliberately not the
        /// water mask — the point is to count what is NOT the water.</summary>
        static int CountPaleBlue(Frame f)
        {
            int n = 0;
            for (int i = 0; i < f.Px.Length; i++)
            {
                var c = f.Px[i];
                if (c.b > 120 && c.b > c.g + 20 && c.g > c.r + 10) n++;
            }
            return n;
        }

        struct WaterStat { public double lum, blue, brightPct, r, g, b; }

        static WaterStat MaskedStats(string id, Material mat, float env, Vector3 pos, Vector3 look,
            bool[] mask, int waterPx)
        {
            mat.SetFloat(EnvProp, env);
            var f = Render(pos, look, W, H, Warmup);
            SavePng(id);
            FrameStats(id, f);
            double lum = 0, blue = 0, rs = 0, gs = 0, bs = 0;
            int bright = 0;
            for (int i = 0; i < f.Px.Length; i++)
            {
                if (!mask[i]) continue;
                var c = f.Px[i];
                double l = Lum(c);
                lum += l; blue += Blue(c);
                rs += c.r; gs += c.g; bs += c.b;
                if (l > 215) bright++;
            }
            Free();
            Debug.Log($"RISHOT id={id} env={env:F2} cam={pos.ToString("F1")} look={look.ToString("F1")}");
            return new WaterStat
            {
                lum = lum / waterPx, blue = blue / waterPx,
                brightPct = 100.0 * bright / waterPx,
                r = rs / waterPx, g = gs / waterPx, b = bs / waterPx,
            };
        }

        static double Lum(Color32 c) => 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
        static double Blue(Color32 c) { double s = (double)c.r + c.g + c.b; return s < 1 ? 0 : c.b / s; }

        /// <summary>A point on the river surface near Old Town plus the local
        /// downstream heading, both taken off the sheet's own geometry.</summary>
        static bool FindRiverLook(out Vector3 surface, out Vector3 along)
        {
            surface = Vector3.zero; along = Vector3.forward;
            var go = GameObject.Find("river_sheet");
            var mf = go != null ? go.GetComponent<MeshFilter>() : null;
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) return false;

            Vector3 target = GeoFrame.FromLatLon(41.6950, 44.8060); // Mtkvari at the Baratashvili bridge
            var verts = mesh.vertices;
            var xf = go.transform.localToWorldMatrix;
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = xf.MultiplyPoint3x4(verts[i]);
                float d = (w.x - target.x) * (w.x - target.x) + (w.z - target.z) * (w.z - target.z);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) return false;
            surface = xf.MultiplyPoint3x4(verts[best]);

            // the heading: the vertex 200-320 m away whose own neighbourhood is
            // still water, i.e. simply the farthest vertex inside that annulus.
            int far = -1; float farD = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = xf.MultiplyPoint3x4(verts[i]);
                float d = (w.x - surface.x) * (w.x - surface.x) + (w.z - surface.z) * (w.z - surface.z);
                if (d > 320f * 320f) continue;
                if (d > farD) { farD = d; far = i; }
            }
            if (far >= 0 && farD > 60f * 60f)
            {
                Vector3 w = xf.MultiplyPoint3x4(verts[far]);
                along = new Vector3(w.x - surface.x, 0f, w.z - surface.z).normalized;
            }
            Debug.Log($"RIWATER sheetVerts={verts.Length} nearestDist={Mathf.Sqrt(bestD):F1} " +
                      $"headingDist={Mathf.Sqrt(farD):F1} surfaceY={surface.y:F2}");
            return true;
        }

        static Vector3 SunDirection()
        {
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l != null && l.type == LightType.Directional && l.enabled)
                {
                    var d = l.transform.forward; d.y = 0f;
                    if (d.sqrMagnitude > 1e-4f) return d.normalized;
                }
            return Vector3.forward;
        }

        // ==================================================================
        // 2. shadow-dist-gate
        // ==================================================================
        static void ShadowPhase(GameLoop game)
        {
            var cr = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            if (cr == null) { Check("shadowPhase", false, "no CityRenderer"); return; }

            var sites = new List<(string id, Vector3 pos, Vector3 look)>();
            var plane = game != null ? game.ActivePlane : null;
            if (plane != null)
            {
                var pt = plane.transform;
                sites.Add(("spawn", pt.position - pt.forward * 26f + Vector3.up * 9f,
                                    pt.position + pt.forward * 60f + Vector3.up * 2f));
            }
            Vector3 narikala = GeoFrame.FromLatLon(41.6880, 44.8090);
            float gy = GroundY(narikala);
            sites.Add(("oldtown", new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f),
                                  new Vector3(narikala.x, gy + 40f, narikala.z)));

            foreach (var (id, pos, look) in sites)
            {
                var off = ShadowMeasure(cr, id, pos, look, gate: false);
                var on = ShadowMeasure(cr, id, pos, look, gate: true);
                Debug.Log($"RISHADOW id={id} " +
                          $"shadowBatchesOff={off.batches} shadowBatchesOn={on.batches} " +
                          $"shadowInstancesOff={off.instances} shadowInstancesOn={on.instances} " +
                          $"castersOff={off.casters} castersOn={on.casters} " +
                          $"trisOff={off.tris} trisOn={on.tris} " +
                          $"msOff={off.ms:F1} msOn={on.ms:F1} " +
                          $"frameBatches={on.frameBatches} shadowDist={ChunkedInstancer.ShadowDist:F0}");
                Check($"shadowGateCuts_{id}", on.batches < off.batches,
                      $"{off.batches} -> {on.batches} shadow-casting batches");
                Check($"shadowGateKeepsNearOnes_{id}", on.batches > 0,
                      "every shadow gone would be a look REGRESSION, not the gate");
            }
            CityRenderer.ShadowDistGate = true;
        }

        struct ShadowStat { public int batches, instances, frameBatches; public long casters, tris; public float ms; }

        static ShadowStat ShadowMeasure(CityRenderer cr, string id, Vector3 pos, Vector3 look, bool gate)
        {
            CityRenderer.ShadowDistGate = gate;
            var sw = Stopwatch.StartNew();
            var f = Render(pos, look, W, H, Warmup);
            sw.Stop();
            SavePng($"{id}_shadowgate_{(gate ? "on" : "off")}");
            FrameStats($"{id}_shadow_{(gate ? "on" : "off")}", f);
            var st = new ShadowStat
            {
                batches = cr.FrameShadowBatches,
                instances = cr.FrameShadowInstances,
                frameBatches = cr.FrameBatches,
                casters = Recorded("Shadow Casters Count"),
                tris = Recorded("Triangles Count"),
                // 9 rendered frames per Render() call; the per-frame figure is
                // what a perf comparison can be read against.
                ms = (float)sw.Elapsed.TotalMilliseconds / (Warmup + 1f),
            };
            Free();
            return st;
        }

        // ==================================================================
        // 3. kit-impostor
        // ==================================================================
        static void ImpostorPhase()
        {
            var cr = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            if (cr == null) { Check("impostorPhase", false, "no CityRenderer"); return; }
            Vector3 narikala = GeoFrame.FromLatLon(41.6880, 44.8090);
            float gy = GroundY(narikala);

            // ---- the frame the open item asks for: Old Town from 300 m ----
            Vector3 pos = new Vector3(narikala.x + 420f, gy + 300f, narikala.z + 260f);
            Vector3 look = new Vector3(narikala.x, gy + 40f, narikala.z);

            // AND THE REASON IT IS FORCED. At 300 m the chunks that have
            // genuinely crossed KIT_LOD_DIST are all outside this frustum: the
            // first run of this probe rendered baked and unbaked at this exact
            // camera and TWO pixels of 1,440,000 differed. A frame that cannot
            // tell the two apart is not evidence either way, so the comparison
            // uses citymap.js:259-262's own tool and forces every visible kit
            // chunk to its far LOD. `oldtown_impostor_hi` is the same camera
            // with nothing forced — the full-detail reference the impostor has
            // to stand in for.
            var hiRef = ImpostorMeasure(cr, "oldtown_impostor_hi", pos, look, baked: true, force: false);
            var plain = ImpostorMeasure(cr, "oldtown_impostor_box", pos, look, baked: false, force: true);
            var baked = ImpostorMeasure(cr, "oldtown_impostor_baked", pos, look, baked: true, force: true);
            Report("oldtown", hiRef, plain, baked);

            // ---- is the ATLAS MATERIAL the thing on screen? ---------------
            // A white impostor box has two readings: the baked material never
            // reached the draw, or it did and its texture sampled white.
            // FrameImpostorBakedBatches settles the first; painting the atlas
            // material RED for one frame settles the second — if the boxes go
            // red, the material IS live and the TEXTURE is the problem; if
            // they stay white, something else is drawing them.
            var bakeRes = KitImpostorBake.Last;
            if (bakeRes?.Material != null)
            {
                var keepColor = bakeRes.Material.GetColor("_BaseColor");
                var keepTex = bakeRes.Material.GetTexture("_BaseMap");
                bakeRes.Material.SetColor("_BaseColor", Color.red);
                var red = ImpostorMeasure(cr, "oldtown_impostor_red", pos, look, baked: true, force: true);
                bakeRes.Material.SetColor("_BaseColor", keepColor);
                Debug.Log($"RIIMPDIAG id=redtest bakedBatches={red.bakedBatches} " +
                          $"redDiffVsBakedPx={DiffPx(red.id, baked.id)} " +
                          $"baseMapStillBound={(keepTex != null).ToString().ToLowerInvariant()} " +
                          $"baseMapST={bakeRes.Material.GetVector("_BaseMap_ST").ToString("F3")}");
            }

            // ---- and the one that needs no forcing: 900 m up --------------
            // Everything in frame is past KIT_LOD_DIST in true 3D distance, so
            // this is what the far LOD looks like in the game, unassisted.
            // ONE shot, no A/B, no assertion. Measured twice: at 900 m the
            // 368 chunks in the LO state are not the ones in this frustum
            // (bakedMaterialBatchesBaked=368 and the frame still moves by 4
            // pixels of 1,440,000 when the atlas is swapped out), so the only
            // honest thing this camera gives is a picture of the far LOD in
            // the game's own cull state. The assertion lives on the forced
            // Old Town pair above, where the impostors demonstrably fill the
            // frame. It is also the most expensive frame in this probe
            // (~11 s each in the editor) on a machine three agents share.
            Vector3 hiPos = new Vector3(narikala.x + 600f, gy + 900f, narikala.z + 400f);
            var baked2 = ImpostorMeasure(cr, "high_impostor_baked", hiPos, look, baked: true, force: false);
            Debug.Log($"RIIMPOSTOR id=high900 lodChunks={baked2.lo} buckets={baked2.buckets} " +
                      $"variance={baked2.variance:F1} draws={baked2.draws} ms={baked2.ms:F1} " +
                      $"impostorBatches={baked2.impostorBatches} bakedMaterialBatches={baked2.bakedBatches} " +
                      $"impostorInstances={baked2.impostorInstances} note=record_only_no_assertion");

            CityRenderer.UseBakedImpostor = true;
            CityRenderer.ForceImpostor = false;
        }

        /// <param name="hiRef">The same camera at FULL detail, where such a
        /// frame exists. At 900 m there is none: every visible chunk is past
        /// KIT_LOD_DIST on its own, which is the point of that camera.</param>
        static void Report(string id, ImpStat? hiRef, ImpStat plain, ImpStat baked)
        {
            var h = hiRef ?? baked;
            Debug.Log($"RIIMPOSTOR id={id} lodChunks={baked.lo} hasHiRef={(hiRef != null).ToString().ToLowerInvariant()} " +
                      $"bucketsHi={h.buckets} bucketsBox={plain.buckets} bucketsBaked={baked.buckets} " +
                      $"varianceHi={h.variance:F1} varianceBox={plain.variance:F1} varianceBaked={baked.variance:F1} " +
                      $"drawsHi={h.draws} drawsBox={plain.draws} drawsBaked={baked.draws} " +
                      $"msHi={h.ms:F1} msBox={plain.ms:F1} msBaked={baked.ms:F1} " +
                      $"impostorBatchesHi={h.impostorBatches} impostorBatchesBaked={baked.impostorBatches} " +
                      $"bakedMaterialBatchesHi={h.bakedBatches} bakedMaterialBatchesBox={plain.bakedBatches} " +
                      $"bakedMaterialBatchesBaked={baked.bakedBatches} " +
                      $"impostorInstancesBaked={baked.impostorInstances} " +
                      $"diffBoxVsBakedPx={DiffPx(plain.id, baked.id)}");
            // A BAKED albedo carries more distinct colours than a flat slab:
            // that is the whole look difference, and it is a pixel fact, not a
            // counter ("Prove tint with pixels, never with counters" —
            // Shaders/CityKit.shader's history).
            // Only assertable where the far LOD is actually ON SCREEN. The
            // 900 m camera has 368 chunks in the LO state city-wide and still
            // renders byte-identically with and without the bake, because the
            // ones in its frustum are not those — `impostorBatches` is the
            // number that says so, and the assertion follows it rather than
            // the city-wide count.
            if (baked.impostorBatches > 0)
            {
                // The gate first: a frame where no batch used the ATLAS
                // material cannot say anything about the bake, and that is
                // exactly the shape two earlier runs of this probe took (the
                // plain box mesh had been collected by the unload sweep, so
                // both arms of the A/B drew the same wrong thing).
                Check($"bakedMaterialReachedTheDraw_{id}", baked.bakedBatches > 0,
                      $"bakedMaterialBatches={baked.bakedBatches} of impostorBatches={baked.impostorBatches}");
                Check($"plainArmUsedNoAtlas_{id}", plain.bakedBatches == 0,
                      $"the A/B's plain arm drew {plain.bakedBatches} atlas batches");
                Check($"bakedImpostorAddsDetail_{id}", baked.buckets > plain.buckets,
                      $"colour buckets {plain.buckets} -> {baked.buckets}");
            }
            else
                Debug.Log($"RIIMPOSTOR id={id} note=no_impostor_batch_in_frustum_so_no_assertion");
            // ...and the far LOD must be CHEAPER than the full model, or the
            // switch is costing triangles for nothing.
            if (hiRef != null)
                Check($"impostorIsCheaper_{id}", baked.draws <= hiRef.Value.draws,
                      $"draws {hiRef.Value.draws} -> {baked.draws}");
        }

        /// <summary>Pixels that differ between two saved frames. The project's
        /// own lesson from this probe's first run: two frames whose mean RGB,
        /// variance and bucket count all agree to the printed precision can
        /// still be two DIFFERENT pictures — or, as it turned out, the same
        /// one. Count the pixels.</summary>
        static long DiffPx(string aId, string bId)
        {
            try
            {
                var a = LoadPng(aId); var b = LoadPng(bId);
                if (a == null || b == null || a.Length != b.Length) return -1;
                long n = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                    if (d > 6) n++;
                }
                return n;
            }
            catch (Exception e) { Debug.Log($"RIIMPOSTOR id=diff_failed msg={e.Message}"); return -1; }
        }

        static Color32[] LoadPng(string id)
        {
            string file = Path.Combine(_out, id + ".png");
            if (!File.Exists(file)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            t.LoadImage(File.ReadAllBytes(file));
            var px = t.GetPixels32();
            UnityEngine.Object.DestroyImmediate(t);
            return px;
        }

        struct ImpStat
        {
            public int lo, buckets, draws, impostorBatches, impostorInstances, bakedBatches;
            public double variance; public float ms; public string id;
        }

        static ImpStat ImpostorMeasure(CityRenderer cr, string id, Vector3 pos, Vector3 look, bool baked, bool force)
        {
            CityRenderer.UseBakedImpostor = baked;
            CityRenderer.ForceImpostor = force;
            var sw = Stopwatch.StartNew();
            // 4 warm-up frames, not 8. CityCulling sweeps in CullSlices=4
            // slices and a camera move over 500 m forces one unsliced full
            // sweep anyway (CityCulling.CullFullMoveSq), so the partition is
            // settled well inside this — and with the kit drawn at its real
            // size these frames are seconds each, not milliseconds.
            var f = Render(pos, look, W, H, ImpostorWarmup);
            sw.Stop();
            SavePng(id);
            var (buckets, variance) = FrameStats(id, f);
            int lo = 0;
            var cull = CullingOf(cr);
            if (cull != null) lo = cull.VisibleLoCount;
            Free();
            return new ImpStat
            {
                id = id, lo = lo, buckets = buckets, variance = variance,
                draws = cr.FrameDrawCalls,
                impostorBatches = cr.FrameImpostorBatches,
                impostorInstances = cr.FrameImpostorInstances,
                bakedBatches = cr.FrameImpostorBakedBatches,
                ms = (float)sw.Elapsed.TotalMilliseconds / (ImpostorWarmup + 1f),
            };
        }

        const int ImpostorWarmup = 4;

        static CityCulling CullingOf(CityRenderer cr)
        {
            var fi = typeof(CityRenderer).GetField("_culling", BindingFlags.Instance | BindingFlags.NonPublic);
            return fi?.GetValue(cr) as CityCulling;
        }

        // ==================================================================
        // scaffold
        // ==================================================================
        static float GroundY(Vector3 unityPos)
        {
            var city = CityData.Load(buildings: false);
            return (city == null || city.Terrain == null) ? 0f : city.Terrain.HeightAt(unityPos.x, unityPos.z);
        }

        /// <summary>Rendered frames per shot. FOUR, not eight: CityCulling
        /// sweeps in CullSlices=4 slices and any camera move over 500 m forces
        /// one unsliced full sweep (CityCulling.CullFullMoveSq), so the
        /// partition is settled inside this — and an Old Town frame with the
        /// building kit at its real size is seconds of wall clock each, on a
        /// machine three other agents are sharing.</summary>
        const int Warmup = 4;

        sealed class Frame { public Color32[] Px; public int W, H; }

        static Texture2D _lastTex;
        static RenderTexture _lastRt;

        static Frame Render(Vector3 pos, Vector3 look, int w, int h, int warmup)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1 };
            var dir = look - pos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, Vector3.up));
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            for (int i = 0; i <= warmup; i++) { TickRenderers(); PinWaterRipple(); _cam.Render(); }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = active;
            _cam.targetTexture = prev;
            _lastTex = tex; _lastRt = rt;
            return new Frame { Px = tex.GetPixels32(), W = w, H = h };
        }

        /// <summary>RiverRenderer.Tick drives the ripple off
        /// realtimeSinceStartup, so two renders a second apart carry different
        /// normals and the masked means would wobble for a reason that has
        /// nothing to do with the property under test. Pin it after every tick:
        /// one fixed `uWaterTime` for every frame this probe takes.</summary>
        const float FrozenWaterTime = 12.345f;

        static void PinWaterRipple()
        {
            var m = GroundMaterials.Water();
            if (m == null) return;
            float env = m.HasProperty(EnvProp) ? m.GetFloat(EnvProp) : -1f;
            GroundMaterials.DriveWaterRipple(m, FrozenWaterTime);
            if (env >= 0f) m.SetFloat(EnvProp, env); // DriveWaterRipple must never move the gate
        }

        static void SavePng(string id)
        {
            if (_lastTex == null) return;
            string file = Path.Combine(_out, id + ".png");
            File.WriteAllBytes(file, _lastTex.EncodeToPNG());
            Debug.Log($"RIPNG file={file}");
        }

        static void Free()
        {
            if (_lastTex != null) { UnityEngine.Object.DestroyImmediate(_lastTex); _lastTex = null; }
            if (_lastRt != null) { _lastRt.Release(); UnityEngine.Object.DestroyImmediate(_lastRt); _lastRt = null; }
        }

        /// <summary>RenderShotProbe.FrameStats's blank-frame guard, returning
        /// the two numbers this probe compares between renders.</summary>
        static (int buckets, double variance) FrameStats(string id, Frame f)
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
                if (seen.Count < 8192) seen.Add((c.r >> 3) << 10 | (c.g >> 3) << 5 | (c.b >> 3));
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
            Debug.Log($"RIFRAME id={id} meanRGB={mr:F1},{mg:F1},{mb:F1} variance={var0:F1} buckets={seen.Count} " +
                      $"magentaPct={(100.0 * magenta / n):F3} blownWhitePct={(100.0 * blown / n):F2} " +
                      $"blackPct={(100.0 * dark / n):F2}");
            Check($"frameNotBlank_{id}", seen.Count > 40 && var0 > 25.0, $"buckets={seen.Count} var={var0:F1}");
            Check($"noErrorMagenta_{id}", magenta * 1000L < n, $"magentaPx={magenta}");
            return (seen.Count, var0);
        }

        // ---- the per-frame instanced renderers ----------------------------
        static readonly string[] RendererTypeNames =
        {
            "TbilisiFly.City.RoadNetworkRenderer", "TbilisiFly.City.RiverRenderer", "TbilisiFly.City.GrassRenderer",
            "TbilisiFly.City.CityRenderer", "TbilisiFly.City.ParkRenderer",
            "TbilisiFly.City.VegetationRenderer", "TbilisiFly.City.ShopfrontRenderer",
            "TbilisiFly.City.StreetFurnitureRenderer", "TbilisiFly.City.LandmarkRenderer",
            "TbilisiFly.City.MetroRenderer", "TbilisiFly.City.MallRenderer",
            "TbilisiFly.City.WindsockRenderer", "TbilisiFly.City.PlaneHallRenderer",
        };

        sealed class Ticker { public Component C; public string Name; public MethodInfo Tick, LateUpdate; }
        static readonly List<Ticker> Tickers = new List<Ticker>();

        static void CollectTickers()
        {
            Tickers.Clear();
            foreach (var tn in RendererTypeNames)
            {
                var type = FindType(tn);
                if (type == null) { Debug.Log($"RITICKER id=missingtype name={tn}"); continue; }
                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                if (found == null) continue;
                foreach (var o in found)
                {
                    if (!(o is Component c)) continue;
                    var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null)
                            ?? type.GetMethod("TickFrame", BindingFlags.Instance | BindingFlags.Public,
                                   null, new[] { typeof(Vector3), typeof(float) }, null);
                    var lu = type.GetMethod("LateUpdate",
                                   BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                   null, Type.EmptyTypes, null);
                    Tickers.Add(new Ticker { C = c, Name = type.Name, Tick = tick, LateUpdate = lu });
                }
            }
            Debug.Log($"RITICKER id=total n={Tickers.Count}");
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
                    else t.LateUpdate?.Invoke(t.C, null);
                }
                catch (Exception e)
                {
                    // A throwing ticker leaves its whole layer ABSENT from the
                    // frame and nothing else says so.
                    Debug.Log($"RITICKER id=threw type={t.Name} msg={e.Message}");
                    _allPass = false;
                }
            }
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

        static readonly string[] RecorderNames =
        { "Draw Calls Count", "SetPass Calls Count", "Triangles Count", "Shadow Casters Count" };
        static readonly Dictionary<string, Unity.Profiling.ProfilerRecorder> Recs =
            new Dictionary<string, Unity.Profiling.ProfilerRecorder>();

        static void StartRecorders()
        {
            UnityEngine.Profiling.Profiler.enabled = true;
            foreach (var n in RecorderNames)
            {
                try { Recs[n] = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, n); }
                catch (Exception e) { Debug.Log($"RIRECORDER id=failed name={n} msg={e.Message}"); }
            }
            var valid = new List<string>();
            foreach (var kv in Recs) if (kv.Value.Valid) valid.Add(kv.Key);
            Debug.Log($"RIRECORDER id=started valid={string.Join("|", valid)}");
        }

        static long Recorded(string name)
        {
            if (!Recs.TryGetValue(name, out var r)) return -1;
            try { return r.Valid ? r.LastValue : -1; } catch { return -1; }
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"RICHECK {key}={(cond ? "PASS" : "FAIL")}";
            if (note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Done()
        {
            Debug.Log($"RENDERITEMS wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "RENDERITEMS RESULT=PASS" : "RENDERITEMS RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }
    }
}
