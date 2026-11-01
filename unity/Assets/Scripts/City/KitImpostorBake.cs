using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TbilisiFly.Core;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE BAKE HALF of src/citymap.js:8035-8158 — `_bakeViews` +
    /// `_kitImposter`, the thing docs/open_items.json `kit-impostor` says is
    /// missing ("the kit impostor is an untextured box - no _bakeViews
    /// render-texture bake (needs a live renderer)").
    ///
    /// City/ImposterBaker.cs already ports the PURE half of this and says so
    /// in its own header: the camera framing (`ComputeViewCamera`), the box
    /// face reorder (`KitImposterBox`) and the GPU capture call
    /// (`RenderView`) were all written and verified, and `RenderView` had
    /// exactly one caller — City/FacadeKitBuilder.cs — which needs a
    /// `bakeCamera` nobody ever handed it. The kit's own far LOD never went
    /// near it: City/CityRenderer.BuildImpostorBox builds an 8-vertex box with
    /// NO UVs and draws it with the kit's own material, so it samples one
    /// texel of the kit albedo and reads as a flat slab. This file is the
    /// live-renderer half that was waiting for an Editor/GPU session.
    ///
    /// WHAT THE JS BAKES, and what this reproduces:
    ///
    ///   * TWO orthographic views per kit variant — `['front', 'top']`
    ///     (citymap.js:8130) — at "~8 px per metre, capped" to 8..128 px
    ///     (citymap.js:8081-8082), rendered from PURE ALBEDO materials
    ///     ("Basic materials = pure albedo capture; scene lighting is applied
    ///     later by the imposter quad's own Lambert material",
    ///     citymap.js:8051-8053).
    ///   * A 12-triangle box at the variant's own local bounds wearing those
    ///     two textures: "the front view on the four sides, the top view on
    ///     the roof" (citymap.js:8124-8126). "The sides all share the FRONT
    ///     bake rather than baking a separate side view: at the distance this
    ///     switches in, the difference is a texel" (citymap.js:8135-8136).
    ///
    /// TWO DELIBERATE DIFFERENCES FROM THE JS, both to Unity's advantage:
    ///
    ///   1. ONE ATLAS, not two textures per variant. The JS can afford a
    ///      CanvasTexture per view because three.js binds a texture per
    ///      material and the kit has ~40 variants; here every impostor box in
    ///      the city shares ONE material, so the far LOD costs one SetPass for
    ///      the whole kit instead of one per variant. The atlas rect is baked
    ///      into the box's UVs (<see cref="BuildBox"/>), which is why
    ///      _BaseMap_ST stays identity in Shaders/CityImpostor.shader.
    ///   2. ONE SUBMESH, not the JS's two groups. citymap.js:8139-8152
    ///      reorders the box index buffer into walls-then-roof specifically so
    ///      three.js issues 2 draws instead of 6, because the two views are
    ///      two TEXTURES. With both views in one atlas there is nothing to
    ///      split: 1 draw. (City/ImposterBaker.KitImposterBox keeps the JS's
    ///      2-group form and is still what Editor/Probes/CityGeoProbe.cs
    ///      verifies against the JS; this is the atlas variant of the same box.)
    ///
    /// HEADLESS IS A NO-OP, not a failure. `Camera.Render()` draws nothing
    /// under -nographics, so <see cref="Build"/> returns null when there is no
    /// graphics device and City/CityRenderer falls back to its existing
    /// untextured box — which is what every -nographics probe in this project
    /// has always measured. The bake only ever happens where it can be seen.
    /// </summary>
    public static class KitImpostorBake
    {
        /// <summary>SceneShaderWarmup.RigOrigin's reasoning, verbatim: far from
        /// any playable ground so the rig can never appear in a game camera.
        /// The bake camera's culling mask is <see cref="ImposterBaker"/>'s own
        /// dedicated layer as well, so this is belt and braces.</summary>
        static readonly Vector3 RigOrigin = new Vector3(0f, 240000f, 0f);

        public const int MaxAtlasSize = 2048;
        /// <summary>Texels between packed views. The atlas carries mipmaps (an
        /// impostor is 100-200 px tall on screen at 700 m, so no mips means
        /// crawling aliasing), and a mip chain bleeds across a 2 px gutter.</summary>
        public const int AtlasPadding = 6;

        public sealed class Result
        {
            public Texture2D Atlas;
            public Material Material;
            /// <summary>kit mesh -> its textured impostor box, in the kit
            /// mesh's own local frame so it shares the hi mesh's instance
            /// transforms exactly (citymap.js:8004-8006).</summary>
            public readonly Dictionary<Mesh, Mesh> Boxes = new Dictionary<Mesh, Mesh>();
            public int AtlasW, AtlasH, Variants, Views;
            public float BakeMs;
            /// <summary>Mean alpha coverage of the baked views, 0..1. The
            /// alpha IS the silhouette (citymap.js:8130's alphaTest 0.35 is
            /// what makes a pitched roof read as a roof and not a slab), so a
            /// coverage of exactly 1.0 everywhere means the capture lost the
            /// cutout and the boxes are solid — worth knowing from the log
            /// rather than from a frame.</summary>
            public float MeanAlpha;
        }

        /// <summary>The one baked atlas for the whole city, cached for the
        /// session: the bake costs ~80 render-texture reads and the kit meshes
        /// never change. CityRenderer.EnsureBuilt can run several times (every
        /// scene load, and the double-draw regression harness), and re-baking
        /// each time would leak an atlas per run.</summary>
        static Result _cached;
        static int _cachedKey;

        /// <summary>The most recent bake, for a probe that needs to look at
        /// the atlas itself (Editor/Probes/RenderingItemsProbe writes it out as
        /// a PNG: "white impostor boxes" and "the atlas is empty" are the same
        /// picture from the renderer's side, and only the atlas tells them
        /// apart).</summary>
        public static Result Last => _cached;

        public static void Reset() { _cached = null; _cachedKey = 0; }

        /// <summary>
        /// Bake every distinct kit variant's front+top albedo into one atlas
        /// and build its textured impostor box. Returns null when there is no
        /// graphics device (-nographics), when Shaders/CityImpostor.shader is
        /// missing or broken, or when nothing could be captured — in every
        /// case the caller keeps its existing untextured box.
        /// </summary>
        public static Result Build(IReadOnlyList<(Mesh mesh, Material[] mats, Matrix4x4 model)> kits)
        {
            if (kits == null || kits.Count == 0) return null;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log("KITIMPOSTOR id=skipped reason=nographics " +
                          "note=Camera.Render-draws-nothing-without-a-GPU-so-the-untextured-box-stands");
                return null;
            }
            var shader = ImpostorShader();
            if (shader == null)
            {
                Debug.LogWarning("KITIMPOSTOR id=skipped reason=no_shader name=TbilisiFly/CityImpostor");
                return null;
            }

            // The kit meshes are shared assets; their identity is the cache
            // key — plus the model matrix, because the box geometry and the
            // bake framing are both derived from it (a probe that toggles
            // CityRenderer.ApplyModelTransform and rebuilds would otherwise
            // get the previous pass's atlas and boxes).
            int key = kits.Count;
            for (int i = 0; i < kits.Count; i++)
                if (kits[i].mesh != null)
                    key = key * 31 + kits[i].mesh.name.GetHashCode() ^ kits[i].model.GetHashCode();
            if (_cached != null && _cachedKey == key && _cached.Atlas != null) return _cached;

            var sw = Stopwatch.StartNew();
            GameObject rig = null;
            var captureMats = new Dictionary<Material, Material>();
            var views = new List<Texture2D>();
            var owners = new List<(Mesh mesh, bool front)>();
            double alphaSum = 0;
            int alphaN = 0;
            try
            {
                rig = BuildRig(out Camera cam, out MeshRenderer stage);
                foreach (var (mesh, mats, model) in kits)
                {
                    if (mesh == null) continue;
                    // STAND IT UP FIRST. The kit meshes are Z-up and 1/100
                    // scale; their import transform is what makes them an
                    // upright building in metres (City/CityRenderer.
                    // ResolveVariant). `front` and `top` are only meaningful
                    // against THAT frame — against the raw mesh they are each
                    // other.
                    var box = CityRenderer.TransformBounds(mesh.bounds, model);
                    stage.transform.SetPositionAndRotation(model.GetPosition(), model.rotation);
                    stage.transform.localScale = model.lossyScale;
                    var capture = CaptureMaterials(mats, mesh.subMeshCount, captureMats);
                    for (int v = 0; v < 2; v++)
                    {
                        bool front = v == 0;
                        var vf = ImposterBaker.ComputeViewCamera(front, box);
                        var tex = ImposterBaker.RenderView(cam, vf, mesh, capture, stage);
                        if (tex == null) continue;
                        views.Add(tex);
                        owners.Add((mesh, front));
                        float a = MeanAlphaOf(tex);
                        alphaSum += a;
                        alphaN++;
                        // Per-variant, because an atlas that comes out far
                        // smaller than the sum of its views means the views
                        // themselves are tiny, and the only way to tell a
                        // "~8 px per metre" bake of a 12 m block from the same
                        // bake of a 1.2 m one is to print the metres.
                        Debug.Log($"KITIMPOSTORVIEW mesh={mesh.name} view={(front ? "front" : "top")} " +
                                  $"sizeM={box.size.x:F2}x{box.size.y:F2}x{box.size.z:F2} " +
                                  $"rawMeshSize={mesh.bounds.size.x:F3}x{mesh.bounds.size.y:F3}x{mesh.bounds.size.z:F3} " +
                                  $"modelScale={model.lossyScale.ToString("F2")} " +
                                  $"px={vf.PixelW}x{vf.PixelH} meanAlpha={a:F3} subMeshes={mesh.subMeshCount}");
                    }
                }

                if (views.Count == 0)
                {
                    Debug.LogWarning("KITIMPOSTOR id=skipped reason=no_views_captured");
                    return null;
                }

                var atlas = Pack(views, out Rect[] rects);
                if (atlas == null) { Debug.LogWarning("KITIMPOSTOR id=skipped reason=pack_failed"); return null; }
                CityGroundData.Pin(atlas);   // see GroundMaterials.Make's PINNING note

                var res = new Result
                {
                    Atlas = atlas,
                    AtlasW = atlas.width,
                    AtlasH = atlas.height,
                    Views = views.Count,
                    MeanAlpha = alphaN > 0 ? (float)(alphaSum / alphaN) : 0f,
                };

                var frontRect = new Dictionary<Mesh, Rect>();
                var topRect = new Dictionary<Mesh, Rect>();
                for (int i = 0; i < owners.Count && i < rects.Length; i++)
                {
                    if (owners[i].front) frontRect[owners[i].mesh] = rects[i];
                    else topRect[owners[i].mesh] = rects[i];
                }
                foreach (var (mesh, _, model) in kits)
                {
                    if (mesh == null || !frontRect.TryGetValue(mesh, out var fr)) continue;
                    // citymap.js:8132 `b.tex.top ?? b.tex.front` — a variant
                    // whose top view failed falls back to the front bake.
                    var tr = topRect.TryGetValue(mesh, out var t) ? t : fr;
                    var boxMesh = BuildBox(CityRenderer.TransformBounds(mesh.bounds, model), fr, tr, model.inverse);
                    CityGroundData.Pin(boxMesh);
                    res.Boxes[mesh] = boxMesh;
                }
                res.Variants = res.Boxes.Count;

                var mat = new Material(shader) { name = "kit_impostor" };
                mat.SetTexture("_BaseMap", atlas);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetColor("_InstTint", Color.white); // the per-instance value overrides this
                mat.SetFloat("_Cutoff", 0.35f);         // citymap.js:8130 alphaTest
                mat.enableInstancing = true;            // HARD RULE for RenderMeshInstanced
                CityGroundData.Pin(mat);
                res.Material = mat;

                sw.Stop();
                res.BakeMs = (float)sw.Elapsed.TotalMilliseconds;
                Debug.Log($"KITIMPOSTOR id=baked variants={res.Variants} views={res.Views} " +
                          $"atlas={res.AtlasW}x{res.AtlasH} meanAlpha={res.MeanAlpha:F3} " +
                          $"bakeMs={res.BakeMs:F1} shader={shader.name} {ArtAssets.Tag}");
                _cached = res;
                _cachedKey = key;
                return res;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"KITIMPOSTOR id=failed msg={e.Message}");
                Debug.LogException(e);
                return null;
            }
            finally
            {
                foreach (var t in views) Destroy(t);
                foreach (var kv in captureMats) Destroy(kv.Value);
                if (rig != null) Destroy(rig);
            }
        }

        /// <summary>
        /// A deterministic shelf packer, written out rather than calling
        /// Texture2D.PackTextures.
        ///
        /// WHY NOT PackTextures. The first run of this bake used it and came
        /// back with a 128x128 atlas for 60 views whose own dimensions sum to
        /// roughly 210,000 texels (KITIMPOSTORVIEW prints each one) — i.e. it
        /// silently scaled every view down to about 16x16, which is a
        /// different feature from the one under test and would have shipped a
        /// smeared impostor that still passed every counter. This packer
        /// NEVER rescales: it grows the atlas to the next power of two until
        /// everything fits at its baked size, and reports its own size, so a
        /// bake that does not fit is a visible number rather than a quiet
        /// blur.
        ///
        /// Rows sorted tallest-first is the standard shelf heuristic; with 60
        /// views of at most 128x128 (ImposterBaker.MaxPx) the worst case is a
        /// 1024x1024 atlas, which is one small texture for the entire far LOD
        /// of a 167,000-building city.
        /// </summary>
        static Texture2D Pack(List<Texture2D> views, out Rect[] rects)
        {
            rects = new Rect[views.Count];
            int pad = AtlasPadding;
            int step = pad * 2;   // each view owns its OWN gutter — see below
            var order = new List<int>(views.Count);
            for (int i = 0; i < views.Count; i++) order.Add(i);
            order.Sort((a, b) => views[b].height.CompareTo(views[a].height));

            for (int size = 128; size <= MaxAtlasSize; size *= 2)
            {
                var place = new RectInt[views.Count];
                int x = pad, y = pad, rowH = 0;
                bool ok = true;
                foreach (int i in order)
                {
                    int w = views[i].width, h = views[i].height;
                    if (x + w + pad > size) { x = pad; y += rowH + step; rowH = 0; }
                    if (y + h + pad > size) { ok = false; break; }
                    place[i] = new RectInt(x, y, w, h);
                    x += w + step;
                    if (h > rowH) rowH = h;
                }
                if (!ok) continue;

                var atlas = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
                { name = "kit_impostor_atlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                var canvas = new Color32[size * size];  // transparent black everywhere first
                for (int i = 0; i < views.Count; i++)
                {
                    var r = place[i];
                    var src = views[i].GetPixels32();
                    int w = r.width, h = r.height;
                    // EDGE-CLAMPED padding, not a transparent gutter. The atlas
                    // carries a mip chain (an impostor is a couple of hundred
                    // pixels tall at 700 m, and no mips means it crawls), and a
                    // transparent gutter would pull ALPHA down along every view's
                    // border as the chain halves — which, through
                    // Shaders/CityImpostor.shader's alphaTest 0.35, eats the
                    // silhouette's edge at exactly the distances the impostor
                    // exists for. Replicating the border instead keeps both the
                    // colour and the alpha stable. Each view owns its own
                    // `pad`-wide ring (the x/y step above is 2*pad), so no two
                    // views ever write the same gutter texel.
                    for (int py = -pad; py < h + pad; py++)
                    {
                        int sy = Mathf.Clamp(py, 0, h - 1);
                        int ay = r.y + py;
                        if (ay < 0 || ay >= size) continue;
                        for (int px = -pad; px < w + pad; px++)
                        {
                            int sx = Mathf.Clamp(px, 0, w - 1);
                            int ax = r.x + px;
                            if (ax < 0 || ax >= size) continue;
                            canvas[ay * size + ax] = src[sy * w + sx];
                        }
                    }
                    rects[i] = new Rect(r.x / (float)size, r.y / (float)size,
                                        w / (float)size, h / (float)size);
                }
                atlas.SetPixels32(canvas);
                atlas.Apply(true, false);               // true = build the mip chain
                return atlas;
            }
            return null;
        }

        internal static Shader ImpostorShader()
        {
            if (GraphicsSettings.currentRenderPipeline == null) return null;
            var sh = Shader.Find("TbilisiFly/CityImpostor");
            if (sh == null) return null;
            if (ArtAssets.ShaderHasError(sh)) return null;
            return sh;
        }

        // ------------------------------------------------------------- rig

        /// <summary>Core/SceneShaderWarmup.EnsureRig's hidden-camera pattern:
        /// a DontSave GameObject 240 km up carrying one disabled orthographic
        /// camera. ImposterBaker.RenderView drives everything else about it
        /// (position, extents, clip planes, culling mask, target texture) and
        /// parents its one-off bake mesh on ImposterBaker.BakeLayer.</summary>
        static GameObject BuildRig(out Camera cam, out MeshRenderer stage)
        {
            var root = new GameObject("kit_impostor_bake") { hideFlags = HideFlags.DontSave };
            root.transform.position = RigOrigin;
            var camGo = new GameObject("kit_impostor_bake_cam") { hideFlags = HideFlags.DontSave };
            camGo.transform.SetParent(root.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;              // explicit Render() only, never in the game's loop
            cam.orthographic = true;
            cam.useOcclusionCulling = false;
            cam.allowMSAA = false;
            cam.allowHDR = false;
            cam.depth = -2000f;
            var ucd = camGo.AddComponent<UniversalAdditionalCameraData>();
            ucd.renderType = CameraRenderType.Base;
            ucd.renderPostProcessing = false; // a tonemapped albedo bake is not an albedo bake
            ucd.renderShadows = false;
            ucd.requiresColorOption = CameraOverrideOption.Off;
            ucd.requiresDepthOption = CameraOverrideOption.Off;
            ucd.volumeLayerMask = 0;

            // ONE reusable stage object instead of ImposterBaker.RenderView's
            // create-and-DestroyImmediate per view — see that method's `reuse`
            // parameter doc for both reasons (80 immediate destroys inside a
            // scene load, and a deferred Destroy leaving the previous kit on
            // the bake layer for every later view of the same pass). It sits at
            // the WORLD ORIGIN, not under the rig: ImposterBaker.ComputeViewCamera
            // frames the mesh in its own LOCAL space, so the geometry and the
            // camera must share that frame.
            var stageGo = new GameObject("kit_impostor_bake_stage") { hideFlags = HideFlags.DontSave };
            stageGo.layer = ImposterBaker.BakeLayer;
            stageGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            // Parented so the `finally` that destroys the rig takes this with
            // it; worldPositionStays keeps it at the origin, where the
            // local-frame camera framing expects the geometry.
            stageGo.transform.SetParent(root.transform, worldPositionStays: true);
            stageGo.AddComponent<MeshFilter>();
            stage = stageGo.AddComponent<MeshRenderer>();
            stage.shadowCastingMode = ShadowCastingMode.Off;
            stage.receiveShadows = false;
            stage.enabled = false;
            return root;
        }

        /// <summary>citymap.js:8050-8059 — "Basic materials = pure albedo
        /// capture". URP/Unlit is MeshBasicMaterial: albedo map x colour, no
        /// lights, no tonemap, no shadows. The kit's hash tint is deliberately
        /// NOT baked in: it rides per instance on the impostor exactly as it
        /// does on the hi mesh (citymap.js:8008 `lodMesh.instanceColor =
        /// mesh.instanceColor`), so one bake serves every building.</summary>
        static Material[] CaptureMaterials(Material[] src, int subMeshCount, Dictionary<Material, Material> cache)
        {
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            int n = Mathf.Max(1, subMeshCount);
            var outMats = new Material[n];
            for (int s = 0; s < n; s++)
            {
                Material from = (src != null && src.Length > 0) ? src[Mathf.Min(s, src.Length - 1)] : null;
                if (from == null || unlit == null) { outMats[s] = from; continue; }
                if (cache.TryGetValue(from, out var hit)) { outMats[s] = hit; continue; }
                var m = new Material(unlit) { name = $"{from.name}_albedo_capture" };
                Color c =
                    from.HasProperty("_BaseColor") ? from.GetColor("_BaseColor") :
                    from.HasProperty("_Color") ? from.GetColor("_Color") : Color.white;
                c.a = 1f;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                Texture tex = null;
                foreach (var pn in new[] { "_BaseMap", "_MainTex" })
                    if (from.HasProperty(pn) && from.GetTexture(pn) != null) { tex = from.GetTexture(pn); break; }
                if (tex != null)
                {
                    m.SetTexture("_BaseMap", tex);
                    m.SetTextureScale("_BaseMap", from.HasProperty("_BaseMap") ? from.GetTextureScale("_BaseMap") : Vector2.one);
                    m.SetTextureOffset("_BaseMap", from.HasProperty("_BaseMap") ? from.GetTextureOffset("_BaseMap") : Vector2.zero);
                }
                cache[from] = m;
                outMats[s] = m;
            }
            return outMats;
        }

        static float MeanAlphaOf(Texture2D tex)
        {
            var px = tex.GetPixels32();
            if (px.Length == 0) return 0f;
            long a = 0;
            for (int i = 0; i < px.Length; i++) a += px[i].a;
            return a / (255f * px.Length);
        }

        // ------------------------------------------------------------- box

        /// <summary>
        /// citymap.js:8133-8152's box, with the atlas rects baked into its UVs.
        ///
        /// 24 VERTICES, NOT 8: per-face UVs need per-face vertices, and that is
        /// the whole reason City/CityRenderer.BuildImpostorBox's shared-corner
        /// box could never carry a texture. The corner identity, the face set
        /// and the triangle winding are lifted from that already-drawn box
        /// unchanged (corner bit 0 = +X, bit 1 = +Y, bit 2 = +Z; authored
        /// directly in UNITY space off an already-mirrored mesh's bounds, so no
        /// GeoFrame.FlipWinding — same note BuildImpostorBox carries).
        ///
        /// UVs: the four WALLS take the front bake (citymap.js:8135-8136 "the
        /// sides all share the FRONT bake... at the distance this switches in,
        /// the difference is a texel"), the roof and floor take the top bake.
        /// Each face maps its own extent to the rect's full 0..1, and v=0 is
        /// the world bottom on a wall — ReadPixels' origin is bottom-left and
        /// so is Unity's v. The roof's own rotation within its rect is not
        /// pinned to a compass direction: at 700 m a roof is a dozen pixels of
        /// gravel, which is the same argument the JS makes for the walls.
        /// </summary>
        internal static Mesh BuildBox(Bounds uprightBounds, Rect front, Rect top, Matrix4x4 toMeshLocal)
        {
            Vector3 c = uprightBounds.center, e = uprightBounds.extents;

            // face -> (4 corner codes in local order, is this face a wall?)
            var faces = new (int[] corners, bool wall, int[] tris)[]
            {
                (new[] { 0, 1, 2, 3 }, true,  new[] { 0, 2, 1, 1, 2, 3 }), // -Z
                (new[] { 4, 5, 6, 7 }, true,  new[] { 0, 1, 2, 1, 3, 2 }), // +Z
                (new[] { 0, 1, 4, 5 }, false, new[] { 0, 1, 2, 1, 3, 2 }), // -Y (floor)
                (new[] { 2, 6, 3, 7 }, false, new[] { 0, 1, 2, 2, 1, 3 }), // +Y (roof)
                (new[] { 0, 4, 2, 6 }, true,  new[] { 0, 1, 2, 2, 1, 3 }), // -X
                (new[] { 1, 3, 5, 7 }, true,  new[] { 0, 1, 2, 1, 3, 2 }), // +X
            };

            var verts = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var tris = new List<int>(36);
            foreach (var (corners, wall, ft) in faces)
            {
                int baseV = verts.Count;
                var rect = wall ? front : top;
                // ±X walls span Z x Y; ±Z walls span X x Y; ±Y spans X x Z.
                // Distinguish ±X from ±Z by whether all four corners share bit 0.
                int b0 = corners[0] & 1;
                bool sideFace = wall && (corners[1] & 1) == b0 && (corners[2] & 1) == b0 && (corners[3] & 1) == b0;
                foreach (int code in corners)
                {
                    float sx = (code & 1) != 0 ? 1f : -1f;
                    float sy = (code & 2) != 0 ? 1f : -1f;
                    float sz = (code & 4) != 0 ? 1f : -1f;
                    verts.Add(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));
                    float u, v;
                    if (!wall) { u = (sx + 1f) * 0.5f; v = (sz + 1f) * 0.5f; }
                    else if (sideFace) { u = (sz + 1f) * 0.5f; v = (sy + 1f) * 0.5f; }
                    else { u = (sx + 1f) * 0.5f; v = (sy + 1f) * 0.5f; }
                    uvs.Add(new Vector2(rect.x + u * rect.width, rect.y + v * rect.height));
                }
                for (int k = 0; k < ft.Length; k++) tris.Add(baseV + ft[k]);
            }

            // ...and back into the mesh's own (Z-up, 1/100) frame, so the box
            // rides the batch's instance matrices (placement * model) exactly
            // as the hi mesh does — City/CityRenderer.BuildImpostorBox does
            // the same with the same matrix.
            for (int i = 0; i < verts.Count; i++) verts[i] = toMeshLocal.MultiplyPoint3x4(verts[i]);
            var m = new Mesh { name = "kit_impostor_baked" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
