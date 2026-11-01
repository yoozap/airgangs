using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TbilisiFly.Core
{
    /// <summary>
    /// src/game.js:1436-1443 `_precompileShaders` — "Compile every scene
    /// material up front (and again after the async layers — parks, metro,
    /// landmarks — land). Without this the first flight into the center hit a
    /// multi-second shader-compile hitch as new materials scrolled into view."
    ///
    /// THE SPEC IS SCENE-SCOPED. `renderer.compileAsync(this.scene,
    /// this.camera)` walks THAT SCENE's materials and compiles the program each
    /// one needs for THAT camera. It is not "compile every shader in the
    /// project", and the difference is not academic:
    /// <c>Shader.WarmupAllShaders()</c> — what this port used until
    /// 2026-10-31 — produced 13 of the play-mode run's 15 logged error lines,
    /// measured:
    ///
    ///   4x Assert: State comes from an incompatible keyword space
    ///   4x Assert: Keyword state size mismatch
    ///   5x Metal: Error creating pipeline state (Hidden/Sentis/TopP, Cast,
    ///             ActivationInt, ReduceIndices, IsInfNaN)
    ///
    /// with the native stack keywords::LocalKeywordState::
    /// ReportIncompatibleKeywordSpace -> ::Remove -> ShaderLab::ShaderState::
    /// FindVariantToUse -> WarmupShadersImpl -> WarmupAllShaders. Unity's own
    /// all-shaders warm-up carries ONE shader's local keyword state into the
    /// NEXT shader's variant lookup (the spaces it names hold the SAME 73
    /// keywords in a different ORDER — 'Universal Render Pipeline/Lit' vs
    /// 'TbilisiFly/CityWall' vs 'TbilisiFly/VegetationRock'), and it also drags
    /// in URP's error fallbacks and the whole Sentis package, whose
    /// integer-output compute kernels simply cannot be warmed as a graphics
    /// pass. None of that is this game's shaders and none of it is in the JS's
    /// scope.
    ///
    /// WHAT THIS DOES INSTEAD — the thing a warm-up has to actually do, which
    /// is to make the driver create the pipeline state before the player sees
    /// it, by DRAWING it:
    ///
    ///   1. Collect the Materials really in use: every
    ///      `Renderer.sharedMaterials` in the loaded scene (that IS the JS's
    ///      scene traversal) UNION every <see cref="IWarmupMaterials"/>
    ///      implementor's batch materials. The second half is mandatory here
    ///      and has no JS counterpart, because this port draws the city,
    ///      vegetation, street furniture, shopfronts, parks, landmarks and the
    ///      infra kit with `Graphics.RenderMeshInstanced` out of plain fields —
    ///      no Renderer component holds those materials, so a scene traversal
    ///      sees NONE of the geometry that covers most of the screen.
    ///   2. Group them by VARIANT, not by material. A pipeline state is per
    ///      (shader, enabled keywords, render state); two shopfront signs that
    ///      differ only in their texture share one program, so one draw warms
    ///      both. That is what turns thousands of materials into tens of draws.
    ///   3. Draw one representative of each variant, once, into a 64x64
    ///      RenderTexture through a camera that copies the game camera's lens,
    ///      HDR, MSAA, culling mask and URP post/shadow settings — so the
    ///      variants compiled are the ones the game will ask for. Materials
    ///      flagged `enableInstancing` additionally get one
    ///      `Graphics.RenderMeshInstanced` submission aimed at that camera,
    ///      because INSTANCING_ON is a different variant and it is the one the
    ///      city kit draws with.
    ///   4. ONE VARIANT PER FRAME, not all of them in one frame. See
    ///      <see cref="Run"/>: all-at-once measured WORSE than the
    ///      WarmupAllShaders stall it replaced, and "no frame stalls" is the
    ///      JS's own stated goal.
    ///   5. Three passes, at the JS's own times (0 / +20 s / +45 s), because
    ///      the scene GROWS — that is the whole reason game.js:1443 has two
    ///      setTimeouts. Pass 2 and 3 only draw variants pass 1 did not see
    ///      (measured: 35 then 3).
    ///
    /// Not Shader.WarmupAllShaders, not a ShaderVariantCollection: a real draw
    /// is the only form that cannot name a variant the game never uses, and the
    /// only one that needs no hand-written keyword list (which this project has
    /// no honest source for — see the note this replaces).
    /// </summary>
    public static class SceneShaderWarmup
    {
        /// <summary>Far from any playable ground, so the rig's own geometry can
        /// never appear in a game camera and nothing of the city can ever
        /// appear in the warm-up camera's 100 m frustum.</summary>
        static readonly Vector3 RigOrigin = new Vector3(0f, 200000f, 0f);

        static GameObject _root;
        static Camera _cam;
        static RenderTexture _rt;
        static Mesh _tri;
        /// <summary>variant key -> the slot already built for it.</summary>
        static readonly Dictionary<string, Transform> _slots = new Dictionary<string, Transform>();
        /// <summary>Variant keys already DRAWN (and therefore compiled). A pass
        /// only ever draws what is not in here, which is why pass 2 costs 3
        /// frames and not 35.</summary>
        static readonly HashSet<string> _warmed = new HashSet<string>();
        /// <summary>This pass's queue: the variants to draw, one per frame.</summary>
        static readonly List<string> _queue = new List<string>();
        static readonly Dictionary<string, Material> _rep = new Dictionary<string, Material>();
        static readonly Matrix4x4[] _one = new Matrix4x4[1];

        public static int LastMaterials { get; private set; }
        public static int LastShaders { get; private set; }
        public static int LastVariants { get; private set; }
        public static float LastMs { get; private set; }
        /// <summary>Variants drawn for the first time by the most recent pass
        /// (pass 2/3 are the deferred scenery layers landing).</summary>
        public static int LastNewVariants { get; private set; }
        public static int LastInstanced { get; private set; }

        // =====================================================================

        /// <summary>
        /// One warm-up pass: ONE VARIANT PER FRAME.
        ///
        /// MEASURED, and the reason this is not a single frame: drawing all 35
        /// variants in one frame put the whole Metal pipeline-state compile into
        /// that frame and the play-mode run read FIRST_300_MAX_MS=6289 — WORSE
        /// than the 4694 ms Shader.WarmupAllShaders() stall it replaced. A
        /// warm-up that trades one 4.7 s hitch for one 6.3 s hitch has not done
        /// its job, and src/game.js:1443's own comment is explicit about the
        /// shape it wants: "compileAsync uses KHR_parallel_shader_compile: no
        /// frame stalls." Unity has no async material compile, but spreading the
        /// queue one variant per frame is the same bargain — the work still
        /// happens before the player flies into the centre, and no single frame
        /// carries more than one variant's compile.
        /// </summary>
        public static IEnumerator Run(Camera gameCam, int pass)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int mats = 0, shaders = 0, variants = 0, fresh = 0, instanced = 0;
            bool armed = false;

            try
            {
                EnsureRig(gameCam);

                var list = Collect();
                mats = list.Count;

                var shaderSet = new HashSet<Shader>();
                _rep.Clear();
                foreach (var m in list)
                {
                    shaderSet.Add(m.shader);
                    string key = VariantKey(m);
                    // Prefer an instancing-enabled representative: it gets both
                    // submissions (plain + INSTANCING_ON) out of one slot.
                    if (!_rep.TryGetValue(key, out var have) || (!have.enableInstancing && m.enableInstancing))
                        _rep[key] = m;
                }
                shaders = shaderSet.Count;
                variants = _rep.Count;

                EnsureSlots(_rep);

                _queue.Clear();
                foreach (var kv in _rep)
                {
                    if (_warmed.Contains(kv.Key)) continue;
                    _queue.Add(kv.Key);
                    if (kv.Value.enableInstancing) instanced++;
                }
                fresh = _queue.Count;

                if (fresh > 0)
                {
                    _root.SetActive(true);
                    _cam.enabled = true;
                    armed = true;
                }
            }
            catch (Exception e)
            {
                // game.js:1440's own `.catch(() => {})` — a failed warm-up is
                // not worth a frame, let alone an error.
                Debug.LogWarning($"SceneShaderWarmup: {e.Message}");
            }

            if (armed)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    Arm(_queue[i], true);      // show this variant, submit its instanced twin
                    yield return null;         // the frame that compiles it
                    Arm(_queue[i], false);
                    _warmed.Add(_queue[i]);
                }
            }

            try
            {
                if (_cam != null) _cam.enabled = false;
                if (_root != null) _root.SetActive(false);
            }
            catch (Exception) { }

            LastMaterials = mats; LastShaders = shaders; LastVariants = variants;
            LastNewVariants = fresh; LastInstanced = instanced;
            LastMs = (float)sw.Elapsed.TotalMilliseconds;
            Debug.Log($"SHADERWARMUP materials={mats} shaders={shaders} variants={variants} " +
                      $"new={fresh} instanced={instanced} frames={(armed ? fresh : 0)} " +
                      $"ms={LastMs.ToString("0.#", CultureInfo.InvariantCulture)} pass={pass}");
        }

        /// <summary>Show (or hide) exactly one variant's slot, and on show also
        /// submit its INSTANCING_ON twin for the same frame. Never throws into
        /// the caller: a material that cannot instance is not an error.</summary>
        static void Arm(string key, bool on)
        {
            try
            {
                if (!_slots.TryGetValue(key, out var slot) || slot == null) return;
                slot.gameObject.SetActive(on);
                if (!on || _cam == null || _tri == null) return;
                if (!_rep.TryGetValue(key, out var mat) || mat == null || !mat.enableInstancing) return;

                // INSTANCING_ON is a separate variant, and it is the one
                // CityRenderer/VegetationRenderer/... draw the city with. A
                // MeshRenderer slot can never compile it.
                int layer = SlotLayer(_cam.cullingMask);
                var at = RigOrigin + new Vector3(0.1f, 0f, _cam.nearClipPlane + 0.5f);
                _one[0] = Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * 0.02f);
                var rp = new RenderParams(mat)
                {
                    camera = _cam,              // this draw belongs to the warm-up pass only
                    layer = layer,
                    // RenderParams.worldBounds defaults to an EMPTY bounds at the
                    // world origin, which culls the draw outright — the rig is
                    // 200 km up.
                    worldBounds = new Bounds(at, Vector3.one * 8f),
                    receiveShadows = true,
                    shadowCastingMode = ShadowCastingMode.On,
                };
                Graphics.RenderMeshInstanced(rp, _tri, 0, _one, 1);
            }
            catch (Exception) { }
        }

        /// <summary>Releases the rig after the last pass — ~tens of tiny
        /// GameObjects, a Mesh and a 64x64 RenderTexture.</summary>
        public static void Dispose()
        {
            _slots.Clear();
            _warmed.Clear();
            _queue.Clear();
            _rep.Clear();
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            if (_tri != null) { Destroy(_tri); _tri = null; }
            if (_root != null) { Destroy(_root); _root = null; }
            _cam = null;
        }

        static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        // ------------------------------------------------------------ collect

        /// <summary>The JS's `this.scene` traversal, plus the instanced half it
        /// has no counterpart for.</summary>
        static List<Material> Collect()
        {
            var seen = new HashSet<Material>();
            var list = new List<Material>();

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (_root != null && r.transform.IsChildOf(_root.transform)) continue; // our own slots
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                for (int s = 0; s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m != null && m.shader != null && seen.Add(m)) list.Add(m);
                }
            }

            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (!(behaviours[i] is IWarmupMaterials w)) continue;
                try
                {
                    var src = w.WarmupMaterials();
                    if (src == null) continue;
                    foreach (var m in src)
                        if (m != null && m.shader != null && seen.Add(m)) list.Add(m);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"SceneShaderWarmup: {behaviours[i].GetType().Name}.WarmupMaterials: {e.Message}");
                }
            }
            return list;
        }

        /// <summary>What actually decides a pipeline state: the program
        /// (shader + enabled local keywords) and the fixed-function render state
        /// URP's Lit/Unlit shaders drive from MATERIAL properties — so an
        /// alpha-blended or double-sided material is its own variant even on the
        /// same shader and keyword set.</summary>
        static string VariantKey(Material m)
        {
            var kw = m.shaderKeywords;
            if (kw != null && kw.Length > 1) Array.Sort(kw, StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder(96);
            sb.Append(m.shader.name).Append('|');
            if (kw != null) for (int i = 0; i < kw.Length; i++) sb.Append(kw[i]).Append(' ');
            sb.Append('|').Append(m.renderQueue);
            AppendProp(sb, m, "_Cull");
            AppendProp(sb, m, "_SrcBlend");
            AppendProp(sb, m, "_DstBlend");
            AppendProp(sb, m, "_ZWrite");
            AppendProp(sb, m, "_ZTest");
            return sb.ToString();
        }

        static void AppendProp(System.Text.StringBuilder sb, Material m, string name)
        {
            sb.Append('|');
            if (m.HasProperty(name)) sb.Append(m.GetFloat(name).ToString("0.#", CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- rig

        static void EnsureRig(Camera gameCam)
        {
            if (_root == null)
            {
                _root = new GameObject("shader_warmup") { hideFlags = HideFlags.DontSave };
                _root.transform.position = RigOrigin;
                _tri = BuildRichTri();
                _rt = new RenderTexture(64, 64, 24) { name = "shader_warmup_rt" };

                var camGo = new GameObject("warmup_cam") { hideFlags = HideFlags.DontSave };
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.targetTexture = _rt;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Color.black;
                _cam.useOcclusionCulling = false;
                _cam.depth = -1000f;                 // before the game camera, never composited over it
                var ucd = camGo.AddComponent<UniversalAdditionalCameraData>();
                ucd.renderType = CameraRenderType.Base;
                ucd.volumeLayerMask = 1;
            }

            // "with the game camera's settings" — the variants a draw compiles
            // depend on HDR, MSAA, shadows and the post stack, so copy them
            // rather than warming a configuration the game never renders.
            if (gameCam != null && _cam != null)
            {
                _cam.fieldOfView = gameCam.fieldOfView;
                _cam.nearClipPlane = Mathf.Max(0.01f, gameCam.nearClipPlane);
                _cam.farClipPlane = _cam.nearClipPlane + 200f;
                _cam.allowHDR = gameCam.allowHDR;
                _cam.allowMSAA = gameCam.allowMSAA;
                _cam.cullingMask = gameCam.cullingMask;
                var src = gameCam.GetComponent<UniversalAdditionalCameraData>();
                var dst = _cam.GetComponent<UniversalAdditionalCameraData>();
                if (src != null && dst != null)
                {
                    dst.renderPostProcessing = src.renderPostProcessing;
                    dst.antialiasing = src.antialiasing;
                    dst.renderShadows = src.renderShadows;
                }
            }
        }

        /// <summary>Lowest layer the camera actually renders — the slots must be
        /// in `cullingMask` or the draw never happens and the warm-up silently
        /// compiles nothing (the unity skill's "a probe camera only sees what
        /// ticked" shape).</summary>
        static int SlotLayer(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        /// <summary>One INACTIVE slot per variant; <see cref="Arm"/> shows one
        /// at a time. Rebuilt only for variants never seen before.</summary>
        static void EnsureSlots(Dictionary<string, Material> byVariant)
        {
            int layer = SlotLayer(_cam.cullingMask);
            int i = _slots.Count;
            foreach (var kv in byVariant)
            {
                if (_slots.TryGetValue(kv.Key, out var t) && t != null)
                {
                    // same variant, possibly a different material instance —
                    // the program is the same, so keep the slot as it is.
                    continue;
                }
                var go = new GameObject("w") { hideFlags = HideFlags.DontSave, layer = layer };
                go.transform.SetParent(_root.transform, false);
                // Staggered in depth so no slot can be rejected before its
                // pipeline state is bound; all inside near..far.
                go.transform.localPosition = new Vector3(0f, 0f, _cam.nearClipPlane + 1f + (i % 128) * 0.5f);
                go.transform.localScale = Vector3.one * 0.02f;
                go.AddComponent<MeshFilter>().sharedMesh = _tri;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = kv.Value;
                mr.shadowCastingMode = ShadowCastingMode.On;   // warms the shadow-caster pass too
                mr.receiveShadows = true;
                go.SetActive(false);        // Arm() turns exactly one on per frame
                _slots[kv.Key] = go.transform;
                i++;
            }
        }

        /// <summary>One triangle carrying EVERY vertex channel a shader in this
        /// project reads (position/normal/tangent/colour/uv0-uv3). A pipeline
        /// state on Metal is per vertex declaration, so a position-only quad
        /// would warm a layout no real mesh here uses.</summary>
        static Mesh BuildRichTri()
        {
            var mesh = new Mesh { name = "shader_warmup_tri", hideFlags = HideFlags.DontSave };
            var v = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0f, 0.5f, 0f) };
            mesh.SetVertices(new List<Vector3>(v));
            mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTangents(new List<Vector4> { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) });
            mesh.SetColors(new List<Color> { Color.white, Color.white, Color.white });
            var uv = new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1) };
            mesh.SetUVs(0, uv); mesh.SetUVs(1, uv); mesh.SetUVs(2, uv); mesh.SetUVs(3, uv);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }
    }
}
