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
    /// THE TWO CITY ROWS of docs/open_items.json this pass owns —
    /// `facade-kit-scale` and `river-blue-channel`. Both are LOOK defects, so
    /// neither can be settled by a -nographics run:
    ///
    ///   TF_SHOTS=&lt;dir&gt; Unity -batchmode -quit -projectPath unity
    ///         -executeMethod TbilisiFly.Probes.FacadeRiverProbe.Run
    ///         -logFile ...                        (NO -nographics)
    ///
    /// ITS OWN FILE, not more methods on Editor/Probes/RenderShotProbe.cs or
    /// RenderingItemsProbe.cs: those are other agents' shared harnesses this
    /// week and a merge conflict in the one probe everyone needs is expensive
    /// (RenderingItemsProbe.cs's own class doc says the same about
    /// RenderShotProbe). The scene-load/boot/tick/readback scaffold is
    /// deliberately duplicated.
    ///
    /// WHAT IT PROVES, AND HOW:
    ///
    /// 1. facade-kit-scale. `FKPART` prints, per node of facade_kit.fbx, the
    ///    RAW mesh bounds, the node's own lossyScale/euler, and the world size
    ///    after FacadeKitLoader.KitPart.Model. The raw numbers are 1/100 of
    ///    metres with the HEIGHT in Z (the Z-up tell), and the composed ones
    ///    are the metres tools/build_facade_kit.py authored — a constant from
    ///    the asset pipeline the runtime has no other way to know. Then
    ///    `FKSHOT` renders ONE camera 25 m from a real relief instance twice,
    ///    with FacadeKitLoader.ApplyModelTransform off and on, in one load.
    ///
    /// 2. river-blue-channel. `RBATTR` attributes the channel's blue pixels to
    ///    a RENDERER by painting one candidate group at a time magenta through
    ///    a MaterialPropertyBlock (nothing in this project is ever magenta, so
    ///    the mask is exact) — the step the previous pass's water-only mask
    ///    could not take, because it could only paint the water material.
    ///    `RBBED` then prints the terrain's own height against the river's
    ///    measured water level at real stations, before and after
    ///    City/TerrainGridOps.CarveWater — the citymap.js:3068 call this port
    ///    never made.
    ///
    /// ONE RENDERER PER TICK, BY HAND, and every throw printed: LateUpdate is
    /// not delivered outside play mode, and a ticker that throws leaves its
    /// whole layer silently ABSENT from the frame
    /// (.claude/skills/unity/SKILL.md).
    /// </summary>
    public static class FacadeRiverProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const int W = 1600, H = 900;
        const int Warmup = 4;
        const float FrozenWaterTime = 12.345f;

        static string _out;
        static Camera _cam;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = Path.Combine(Path.GetTempPath(), "tf_facade_river");
            Directory.CreateDirectory(_out);
            Debug.Log($"FACADERIVER out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"colorSpace={QualitySettings.activeColorSpace}");

            bool gpu = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (!gpu) Debug.Log("FACADERIVER id=nogpu msg=numbers only, no frames (run WITHOUT -nographics for the shots)");

            // THE COUNT ARM. With TF_FACADE_NOMODEL=1 the scene's own
            // CityRenderer.EnsureBuilt runs with the kit import transform OFF,
            // i.e. exactly the code this pass found, and logs its own
            // CITYFACADE line — which is how "batches/instances/layers
            // byte-identical" is checked against the SAME terrain and the SAME
            // data, instead of against a baseline from a different day. The run
            // then stops: no shots, no river work, nothing else to pay for.
            bool noModel = Environment.GetEnvironmentVariable("TF_FACADE_NOMODEL") == "1";
            Debug.Log($"FACADERIVER applyModelArm={(!noModel).ToString().ToLowerInvariant()}");

            try
            {
                // The FBX measurement needs no scene at all — do it first so it
                // still lands in the log if the scene load goes wrong.
                KitAssetFacts();
                PutCountArms();
                FacadeKitLoader.ApplyModelTransform = !noModel;   // BEFORE the scene opens

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }

                var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"FACADERIVER id=bootthrew msg={e.Message}"); Debug.LogException(e); }

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
                if (env != null) { env.Cam = _cam; env.Setup(); }

                CollectTickers();
                if (noModel)
                {
                    // The BEFORE arm: the same cameras, in a SEPARATE PROCESS.
                    // An in-run A/B of this layer does not work and the numbers
                    // look like a pass — see FacadeShots's own note on
                    // Graphics.RenderMeshInstanced in batch mode.
                    if (gpu) FacadeShots();
                    Debug.Log("FACADERIVER id=countarm_done");
                    Done();
                    return;
                }

                RiverBedNumbers();          // -nographics-safe
                if (gpu)
                {
                    FacadeShots();
                    RiverShots();
                }
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"FACADERIVER id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        // ==================================================================
        // 1. facade-kit-scale — the ASSET's own numbers
        // ==================================================================
        const string KitPath = "Assets/Resources/Art/Props/facade_kit.fbx";

        static void KitAssetFacts()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(KitPath);
            Check("facadeKitImports", root != null, KitPath);
            if (root == null) return;

            var mfs = root.GetComponentsInChildren<MeshFilter>(true);
            Debug.Log($"FKROOT nodes={mfs.Length} rootScale={root.transform.localScale.ToString("F3")} " +
                      $"rootEuler={root.transform.localRotation.eulerAngles.ToString("F1")}");

            Matrix4x4 w2l = root.transform.worldToLocalMatrix;
            int zUp = 0, tiny = 0;
            foreach (var mf in mfs)
            {
                if (mf.sharedMesh == null) continue;
                Bounds raw = mf.sharedMesh.bounds;
                Matrix4x4 model = w2l * mf.transform.localToWorldMatrix;
                Bounds world = CityRenderer.TransformBounds(raw, model);
                Vector3 ls = mf.transform.lossyScale;
                Vector3 eu = mf.transform.localRotation.eulerAngles;
                // The Z-up tell: the node's tallest raw axis is Z, and the
                // composed one is Y.
                bool zTall = raw.size.z > raw.size.y;
                if (zTall) zUp++;
                if (raw.size.magnitude < 0.2f) tiny++;
                Debug.Log($"FKPART name={mf.gameObject.name} mesh={mf.sharedMesh.name} " +
                          $"rawSize={raw.size.x:F4},{raw.size.y:F4},{raw.size.z:F4} " +
                          $"lossyScale={ls.x:F1},{ls.y:F1},{ls.z:F1} euler={eu.x:F1},{eu.y:F1},{eu.z:F1} " +
                          $"worldSize={world.size.x:F3},{world.size.y:F3},{world.size.z:F3} " +
                          $"worldCentre={world.center.x:F3},{world.center.y:F3},{world.center.z:F3} " +
                          $"zTall={zTall.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"FKASSET nodes={mfs.Length} rawZTallerThanY={zUp} rawUnder20cm={tiny}");

            // the loader's own view of the same asset, both arms
            foreach (bool apply in new[] { false, true })
            {
                FacadeKitLoader.ApplyModelTransform = apply;
                var kit = FacadeKitLoader.GroupByBaseName(root.transform);
                var names = new List<string>(kit.Keys);
                names.Sort();
                var sb = new System.Text.StringBuilder();
                foreach (var n in names)
                {
                    var p = kit[n];
                    Bounds b = CityRenderer.TransformBounds(p.Mesh.bounds, p.Model);
                    sb.Append($" {n}={b.size.x:F2}x{b.size.y:F2}x{b.size.z:F2}");
                }
                Debug.Log($"FKKIT applyModel={apply.ToString().ToLowerInvariant()} parts={kit.Count}{sb}");
            }
            FacadeKitLoader.ApplyModelTransform = true;   // Run() sets the real arm straight after
        }

        /// <summary>
        /// DOES THE FIX MOVE ANY COUNT? FacadeKitBuilder.Put partitions by the
        /// instance TRANSLATION and the mesh's triangle count, and the kit
        /// model matrix is a pure rotate+scale with a zero translation column —
        /// so chunks, instances, bounds, Cy and the LOD budget must come out
        /// bit-for-bit the same in both arms. Argued that way it is a claim;
        /// here it is run, on the real `window` kit mesh, through the real
        /// Put/ChunkedInstancer path.
        /// </summary>
        static void PutCountArms()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(KitPath);
            if (root == null) return;
            FacadeKitLoader.ApplyModelTransform = true;
            var kit = FacadeKitLoader.GroupByBaseName(root.transform);
            if (!kit.TryGetValue("window", out var part) || part.Mesh == null) return;

            var items = new List<Vector4>(5000);   // x,y,z,ang — a stand-in item
            var rnd = new System.Random(12345);
            for (int i = 0; i < 5000; i++)
                items.Add(new Vector4((float)(rnd.NextDouble() * 4000 - 2000), (float)(rnd.NextDouble() * 40),
                                      (float)(rnd.NextDouble() * 4000 - 2000), (float)(rnd.NextDouble() * 6.28)));

            string Describe(Matrix4x4? model)
            {
                var pr = FacadeKitBuilder.Put(items, it => new Vector3(it.x, it.y, it.z), it => it.w,
                    null, null, part.Mesh, part.Materials,
                    FacadeKitBuilder.WindowDefault.MaxDist, FacadeKitBuilder.WindowDefault.ChunkM,
                    FacadeKitBuilder.WindowDefault.Lodable, null, model);
                int inst = 0; double bx = 0, by = 0, cy = 0, lod = 0;
                foreach (var c in pr.Chunks)
                {
                    inst += c.Matrices.Length;
                    bx += c.Bounds.center.x; by += c.Bounds.size.y; cy += c.Cy; lod += c.LodD2;
                }
                return $"chunks={pr.Chunks.Count} instances={inst} sumBoundsCx={bx:F3} " +
                       $"sumBoundsSizeY={by:F3} sumCy={cy:F3} sumLodD2={lod:F1}";
            }
            Debug.Log($"FKPUTARM applyModel=false {Describe(Matrix4x4.identity)}");
            Debug.Log($"FKPUTARM applyModel=true  {Describe(part.Model)}");
        }

        // ==================================================================
        // 1b. the frame: one real relief instance, 25 m away, both arms
        // ==================================================================
        static void FacadeShots()
        {
            var cr = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            Check("cityRendererInScene", cr != null);
            if (cr == null) return;
            cr.EnsureBuilt();

            // The model matrices the batches were built with, keyed by the kit
            // mesh they were built FROM (reference equality — a Mesh is a
            // reference type, and the kit dictionary holds the very instances
            // CityRenderer.BuildFacadeBatches handed to ChunkedInstancer).
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(KitPath);
            var models = new Dictionary<Mesh, Matrix4x4>();
            if (root != null)
            {
                // The lookup always wants the REAL model matrices, whichever
                // arm the scene was built in — and the arm flag must survive
                // this, or the before arm labels its own frames "after".
                bool arm = FacadeKitLoader.ApplyModelTransform;
                FacadeKitLoader.ApplyModelTransform = true;
                foreach (var kv in FacadeKitLoader.GroupByBaseName(root.transform))
                    if (kv.Value.Mesh != null && kv.Value.Model != Matrix4x4.identity)
                        models[kv.Value.Mesh] = kv.Value.Model;
                FacadeKitLoader.ApplyModelTransform = arm;
            }
            Debug.Log($"FKMODELS kitPartsWithTransform={models.Count}");

            // Saburtalo panel blocks (Vazha-Pshavela) and Old Town, as the row
            // asks. The site is the nearest REAL window/balcony instance to the
            // target, so the camera can never end up pointing at empty ground.
            TryFacadeSite(cr, models, "facade_saburtalo", GeoFrame.FromLatLon(41.7272, 44.7490));
            TryFacadeSite(cr, models, "facade_oldtown", GeoFrame.FromLatLon(41.6917, 44.8087));
        }

        /// <summary>
        /// WHY A LAYER IS NOT IN THE FRAME. Two renders settle it:
        ///
        ///   1. the per-batch CULL state of the facade batches nearest the
        ///      camera, read from CityRenderer's own partition, plus their
        ///      material/shader/instancing flags — a batch that is culled, has
        ///      no material, or has instancing off never reaches the GPU and
        ///      nothing in the frame says which;
        ///   2. the same camera with every BAKED chunk_* MeshRenderer
        ///      disabled. Whatever is still on screen is instanced, so this
        ///      says, in pixels, whether the buildings in the shot are the
        ///      baked chunk meshes or the instanced kit — the question that
        ///      decides whether "the relief is invisible" is a cull bug or a
        ///      look bug.
        /// </summary>
        static void FacadeDrawDiag(CityRenderer cr, Dictionary<Mesh, Matrix4x4> models,
            string id, Vector3 camPos, Vector3 look)
        {
            var batches = cr.BatchesForProbe;
            var cull = cr.CullingForProbe;
            if (batches == null) return;
            int printed = 0, facadeBatches = 0, facadeVisible = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                var b = batches[i];
                if (b.Mesh == null || !models.ContainsKey(b.Mesh)) continue;
                facadeBatches++;
                bool hi = true, lo = false;
                if (cull != null && i < cull.Chunks.Count) { hi = cull.Chunks[i].HiOn; lo = cull.Chunks[i].LoOn; }
                if (hi) facadeVisible++;
                float d = Vector2.Distance(new Vector2(b.Bounds.center.x, b.Bounds.center.z),
                                           new Vector2(camPos.x, camPos.z));
                if (d > 400f || printed >= 8) continue;
                printed++;
                var m0 = (b.Materials != null && b.Materials.Length > 0) ? b.Materials[0] : null;
                Debug.Log($"FKBATCH id={id} i={i} mesh={b.Mesh.name} subs={b.Mesh.subMeshCount} " +
                          $"instances={(b.Matrices != null ? b.Matrices.Length : 0)} distXZ={d:F0} " +
                          $"hi={hi.ToString().ToLowerInvariant()} lo={lo.ToString().ToLowerInvariant()} " +
                          $"maxDist={b.MaxDist:F0} lodDist={b.LodDist:F0} " +
                          $"boundsC={b.Bounds.center.ToString("F0")} boundsSz={b.Bounds.size.ToString("F0")} " +
                          $"mat={(m0 != null ? m0.name : "null")} shader={(m0 != null ? m0.shader.name : "null")} " +
                          $"instancing={(m0 != null ? m0.enableInstancing : false).ToString().ToLowerInvariant()} " +
                          $"queue={(m0 != null ? m0.renderQueue : -1)}");
            }
            Debug.Log($"FKBATCHSUM id={id} facadeBatches={facadeBatches} hiOn={facadeVisible} " +
                      $"frameBatches={cr.FrameBatches} frameDraws={cr.FrameDrawCalls}");

            // ---- 2. baked chunk_* off: what is left is instanced ------------
            var baked = new List<MeshRenderer>();
            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                if (mr.enabled && mr.gameObject.name.StartsWith("chunk_")) baked.Add(mr);
            if (baked.Count == 0) { Debug.Log($"FKBAKED id={id} chunkRenderers=0"); return; }
            foreach (var mr in baked) mr.enabled = false;
            var f = Render(camPos, look, W, H, Warmup);
            SavePng($"{id}_bakedoff");
            long sum = 0; int sky = 0;
            for (int i = 0; i < f.Px.Length; i++)
            {
                var c = f.Px[i];
                sum += c.r + c.g + c.b;
                if (c.b > 120 && c.b > c.g + 20 && c.g > c.r + 10) sky++;
            }
            Free();
            foreach (var mr in baked) mr.enabled = true;
            Debug.Log($"FKBAKED id={id} chunkRenderers={baked.Count} meanRGBsum={(sum / (double)f.Px.Length):F1} skyLikePx={sky}");

            // ---- 3. is the relief ON SCREEN AT ALL -------------------------
            // The same magenta mask that identified the river's blue, pointed
            // at the facade layers' own materials. Counters said 151 facade
            // batches passed the cull and the scale flip moved no pixels at
            // all, which are only compatible if the layer reaches the GPU and
            // contributes nothing — so count its pixels directly instead of
            // inferring them.
            var mats = new List<Material>();
            var saved = new List<Color>();
            foreach (var b in batches)
            {
                if (b.Mesh == null || !models.ContainsKey(b.Mesh) || b.Materials == null) continue;
                foreach (var m in b.Materials)
                {
                    if (m == null || mats.Contains(m) || !m.HasProperty("_BaseColor")) continue;
                    mats.Add(m); saved.Add(m.GetColor("_BaseColor"));
                }
            }
            if (mats.Count == 0) { Debug.Log($"FKMASK id={id} materials=0"); return; }
            for (int i = 0; i < mats.Count; i++) mats[i].SetColor("_BaseColor", Color.magenta);
            var fm = Render(camPos, look, W, H, Warmup);
            SavePng($"{id}_reliefmask");
            int mag = 0;
            for (int i = 0; i < fm.Px.Length; i++)
            {
                var c = fm.Px[i];
                if (c.r > 120 && c.b > 120 && c.g * 2 < (c.r + c.b) * 0.6f) mag++;
            }
            Free();
            for (int i = 0; i < mats.Count; i++) mats[i].SetColor("_BaseColor", saved[i]);
            Debug.Log($"FKMASK id={id} materials={mats.Count} magentaPx={mag} " +
                      $"magentaPct={(100.0 * mag / (W * H)):F3}");
            Check($"reliefOnScreen_{id}", mag > 2000,
                  $"magentaPx={mag} — the facade relief submits draws and paints nothing");
        }

        /// <summary>
        /// Flip the built batches between the BEFORE and AFTER arms without
        /// rebuilding anything: every facade instance matrix is
        /// `placement * model`, so right-multiplying by `model.inverse` is
        /// exactly the state this code was in before the fix, and
        /// right-multiplying by `model` puts it back.
        ///
        /// A REBUILD WAS THE OBVIOUS WAY AND IS THE WRONG ONE: CityRenderer
        /// .RebuildForCheck re-runs BuildRich over 193k footprints, and the
        /// row needs four frames. This touches only the Matrix4x4[]s, which is
        /// precisely the thing under test, and leaves the chunk partition, the
        /// bounds and the culling untouched so the two frames differ in
        /// nothing else.
        /// </summary>
        static int FlipFacadeArm(CityRenderer cr, Dictionary<Mesh, Matrix4x4> models, bool toAfter)
        {
            int touched = 0;
            var batches = cr.BatchesForProbe;
            if (batches == null) return 0;
            foreach (var b in batches)
            {
                if (b.Mesh == null || b.Matrices == null) continue;
                if (!models.TryGetValue(b.Mesh, out var model)) continue;
                Matrix4x4 m = toAfter ? model : model.inverse;
                for (int i = 0; i < b.Matrices.Length; i++) b.Matrices[i] = b.Matrices[i] * m;
                touched += b.Matrices.Length;
            }
            return touched;
        }

        static void TryFacadeSite(CityRenderer cr, Dictionary<Mesh, Matrix4x4> models, string id, Vector3 target)
        {
            if (!NearestReliefInstance(cr, target, out Vector3 pos, out Vector3 normal, out string mesh, out float dist))
            {
                Check($"reliefNear_{id}", false, "no facade-relief instance found near the target");
                return;
            }
            Debug.Log($"FKSITE id={id} target={target.ToString("F0")} instance={pos.ToString("F1")} " +
                      $"mesh={mesh} distFromTarget={dist:F0}");

            // 25 m out along the facade normal, a little above the instance.
            // The normal's SIGN is checked against the geometry, not trusted:
            // a camera on the blind side of the wall renders the same frame in
            // both arms and reads as "the fix does nothing".
            Vector3 camPos = pos + normal * 25f + Vector3.up * 3f;
            if (Physics.Raycast(camPos, (pos - camPos).normalized, out var hit, 60f) && hit.distance < 20f)
            {
                Debug.Log($"FKFLIP id={id} outwardNormalFlipped firstHit={hit.distance:F1} collider={hit.collider.name}");
                normal = -normal;
                camPos = pos + normal * 25f + Vector3.up * 3f;
            }
            Vector3 look = pos + Vector3.up * 1.5f;
            int withinFrame = 0;
            foreach (var b in cr.BatchesForProbe)
            {
                if (b.Mesh == null || b.Matrices == null) continue;
                if (!models.ContainsKey(b.Mesh)) continue;
                for (int i = 0; i < b.Matrices.Length; i++)
                    if ((((Vector3)b.Matrices[i].GetColumn(3)) - camPos).sqrMagnitude < 60f * 60f) withinFrame++;
            }
            Debug.Log($"FKNEARCAM id={id} reliefWithin60mOfCamera={withinFrame} " +
                      $"cameraMainIsProbeCam={(Camera.main == _cam).ToString().ToLowerInvariant()} " +
                      $"normal={normal.ToString("F2")}");
            FacadeDrawDiag(cr, models, id, camPos, look);

            // HOW DEEP IS THE RELIEF IN THE WALL. A ray fired at the facade
            // from 2 m outside finds the baked chunk's own surface; the
            // difference against the instance's own position says whether a
            // kit piece stands PROUD of the wall (visible) or is buried in it
            // (drawn, correct, and invisible behind an opaque chunk mesh).
            // Nothing else in the log distinguishes those two.
            {
                int probed = 0, buried = 0; float sum = 0f;
                foreach (var b in cr.BatchesForProbe)
                {
                    if (b.Mesh == null || b.Matrices == null || !models.ContainsKey(b.Mesh)) continue;
                    for (int i = 0; i < b.Matrices.Length && probed < 24; i++)
                    {
                        Vector3 ip = b.Matrices[i].GetColumn(3);
                        if ((ip - camPos).sqrMagnitude > 40f * 40f) continue;
                        Vector3 n2 = FacadeNormal(b.Matrices[i]);
                        if (!Physics.Raycast(ip + n2 * 2f, -n2, out var wh, 4f)) continue;
                        float proud = 2f - wh.distance;  // >0: wall is OUTSIDE the instance
                        probed++; sum += proud;
                        if (proud > 0.02f) buried++;
                        if (probed <= 6)
                            Debug.Log($"FKINSET id={id} mesh={b.Mesh.name} inst={ip.ToString("F1")} " +
                                      $"wallOutsideInstanceBy={proud:F3} hit={wh.collider.gameObject.name}");
                    }
                }
                Debug.Log($"FKINSETSUM id={id} probed={probed} buried={buried} " +
                          $"meanWallOutsideBy={(probed > 0 ? sum / probed : 0f):F3}");
            }

            // ONE FRAME, ONE ARM. An in-run flip of the instance matrices
            // was the obvious A/B and it is WRONG here, measured:
            // Graphics.RenderMeshInstanced submissions are not consumed by a
            // manual Camera.Render() in batch mode, so the previous arm's
            // draws are still live when the next one renders and the two
            // frames come back byte-identical (changedPx=0) even while the
            // relief demonstrably paints 49,365 px of the same frame
            // (FKMASK). The arms are therefore shot from the SAME camera in
            // two SEPARATE PROCESSES — TF_FACADE_NOMODEL=1 for the before —
            // and the camera is derived from data the fix does not move (the
            // instance TRANSLATIONS and FacadeNormal, which is arm-independent).
            string tag = FacadeKitLoader.ApplyModelTransform ? "after" : "before";
            var f = Render(camPos, look, W, H, Warmup);
            SavePng($"{id}_{tag}");
            FrameStats($"{id}_{tag}", f);
            Debug.Log($"FKSHOT id={id}_{tag} applyModel={FacadeKitLoader.ApplyModelTransform.ToString().ToLowerInvariant()} " +
                      $"cam={camPos.ToString("F1")} look={look.ToString("F1")} " +
                      $"frameBatches={cr.FrameBatches} frameDraws={cr.FrameDrawCalls}");
            Free();

            // ...and the same camera with the baked chunk meshes off, where
            // the relief layer is the only thing left standing.
            {
                var baked = new List<MeshRenderer>();
                foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                    if (mr.enabled && mr.gameObject.name.StartsWith("chunk_")) baked.Add(mr);
                foreach (var mr in baked) mr.enabled = false;
                var fr = Render(camPos, look, W, H, Warmup);
                SavePng($"{id}_relief_{tag}");
                FrameStats($"{id}_relief_{tag}", fr);
                Free();
                foreach (var mr in baked) mr.enabled = true;
                Debug.Log($"FKRELIEFSHOT id={id}_relief_{tag} chunkRenderersOff={baked.Count}");
            }
        }


        /// <summary>
        /// The wall normal of a relief instance, in EITHER arm. With the kit
        /// model matrix composed in, the part's own +Z (the face direction) has
        /// been turned onto +Y by the FBX node's Rx(-90), so the normal comes
        /// out of the matrix's UP column negated; without it, +Z is still +Z.
        /// Taking whichever column is actually HORIZONTAL gives the same world
        /// direction both ways — which is what lets the two arms be shot from
        /// one camera in two different processes.
        /// </summary>
        static Vector3 FacadeNormal(Matrix4x4 m)
        {
            Vector3 fromUp = m.MultiplyVector(Vector3.up);
            Vector3 a = new Vector3(-fromUp.x, 0f, -fromUp.z);
            Vector3 fromFwd = m.MultiplyVector(Vector3.forward);
            Vector3 b = new Vector3(fromFwd.x, 0f, fromFwd.z);
            Vector3 pick = a.sqrMagnitude >= b.sqrMagnitude ? a : b;
            return pick.sqrMagnitude > 1e-9f ? pick.normalized : Vector3.forward;
        }

        /// <summary>
        /// A facade-relief instance near the target that is on a REAL,
        /// POPULATED wall, plus that wall's outward normal
        /// (<see cref="FacadeNormal"/>). "Populated" matters: the nearest
        /// single instance can be one stray window on a blank gable, so this
        /// takes the nearest one with at least <paramref name="minNeighbours"/>
        /// siblings within 12 m and the camera always faces a grid of openings.
        /// </summary>
        static bool NearestReliefInstance(CityRenderer cr, Vector3 target,
            out Vector3 pos, out Vector3 normal, out string meshName, out float dist,
            int minNeighbours = 20)
        {
            pos = Vector3.zero; normal = Vector3.forward; meshName = null; dist = 0f;
            var batches = cr.BatchesForProbe;
            if (batches == null) return false;

            // every relief instance within 250 m of the target, with its matrix
            var near = new List<(Vector3 p, Vector3 n, string mesh)>();
            foreach (var b in batches)
            {
                string nm = b.Mesh != null ? b.Mesh.name : null;
                if (nm == null || b.Matrices == null) continue;
                if (!(nm.StartsWith("window") || nm.StartsWith("win_") || nm.StartsWith("balc")))
                    continue;
                for (int i = 0; i < b.Matrices.Length; i++)
                {
                    Vector3 t = b.Matrices[i].GetColumn(3);
                    float dx = t.x - target.x, dz = t.z - target.z;
                    if (dx * dx + dz * dz > 250f * 250f) continue;
                    near.Add((t, FacadeNormal(b.Matrices[i]), nm));
                }
            }
            Debug.Log($"FKNEAR target={target.ToString("F0")} reliefWithin250m={near.Count}");
            if (near.Count == 0) return false;

            int bestIdx = -1; float best = float.MaxValue;
            for (int i = 0; i < near.Count; i++)
            {
                float dx = near[i].p.x - target.x, dz = near[i].p.z - target.z;
                float d = dx * dx + dz * dz;
                if (d >= best) continue;
                int nb = 0;
                for (int j = 0; j < near.Count && nb < minNeighbours; j++)
                {
                    if (j == i) continue;
                    if ((near[j].p - near[i].p).sqrMagnitude < 144f) nb++;
                }
                if (nb < minNeighbours) continue;
                best = d; bestIdx = i;
            }
            if (bestIdx < 0) return false;
            pos = near[bestIdx].p; normal = near[bestIdx].n; meshName = near[bestIdx].mesh;
            dist = Mathf.Sqrt(best);
            return true;
        }

        // ==================================================================
        // 2. river-blue-channel — the numbers
        // ==================================================================

        /// <summary>
        /// The terrain's own surface against the river's measured water level,
        /// at real mask cells, BEFORE and AFTER City/TerrainGridOps.CarveWater.
        /// citymap.js:3068 calls _carveWater at the end of _buildTerrainSampler,
        /// so in the JS no water-class cell can poke through the sheet; this
        /// prints how many do here.
        /// </summary>
        static void RiverBedNumbers()
        {
            var ctx = CityGroundData.Shared();
            var terrain = ctx?.City?.Terrain;
            var river = ctx?.River;
            Check("riverContext", terrain != null && river != null);
            if (terrain == null || river == null) return;

            Debug.Log($"RBGRID w={terrain.W} h={terrain.H} step={terrain.Step:F1} " +
                      $"minx={terrain.MinX:F0} minz={terrain.MinZ:F0}");

            // THE OTHER ARM, parsed independently with citymap.js:3068's carve
            // switched off — never `CarveWater(clone-of-live)`, which would
            // DOUBLE-carve once the fix is in and read as a 3 m improvement
            // that is really a 3 m error. `raw` is the port exactly as it was;
            // `terrain` is what the game now ships.
            TerrainSampler raw = null;
            try
            {
                CityData.ClearCache();
                TerrainSampler.CarveWaterOnLoad = false;
                raw = CityData.Load(buildings: false)?.Terrain;
            }
            finally
            {
                TerrainSampler.CarveWaterOnLoad = true;
                CityData.ClearCache();
            }
            if (raw == null) { Check("uncarvedArm", false); return; }

            int n = 0, pokeRaw = 0, pokeLive = 0;
            double sumRaw = 0, sumLive = 0;
            float worstRaw = float.MinValue;
            int waterCells = 0;
            // Walk the grid's water-class vertices; only those the river carve
            // mask actually claims have a measured level to compare against.
            for (int r = 0; r < terrain.H; r++)
            for (int c = 0; c < terrain.W; c++)
            {
                if (terrain.Classes == null || terrain.Classes[r * terrain.W + c] != 2) continue;
                waterCells++;
                float x = terrain.MinX + c * terrain.Step;
                float z = terrain.MinZ + r * terrain.Step;
                if (!river.InRiverMask(x, z)) continue;
                var lvl = river.MeasuredLevelAt(x, z);
                if (!lvl.HasValue) continue;
                float hRaw = raw.Heights[r * terrain.W + c];
                float hLive = terrain.Heights[r * terrain.W + c];
                n++;
                sumRaw += hRaw - lvl.Value;
                sumLive += hLive - lvl.Value;
                if (hRaw > lvl.Value) pokeRaw++;
                if (hLive > lvl.Value) pokeLive++;
                if (hRaw - lvl.Value > worstRaw) worstRaw = hRaw - lvl.Value;
            }
            // "aboveWater" = bed vertices standing PROUD of the measured water
            // surface, i.e. the ones that punch through the river sheet and
            // show TerrainBuilder.WaterHex instead of the water material.
            Debug.Log($"RBBED waterClassVerts={waterCells} measuredRiverVerts={n} " +
                      $"uncarvedAboveWater={pokeRaw} liveAboveWater={pokeLive} " +
                      $"meanUncarved={(n > 0 ? sumRaw / n : 0):F2} meanLive={(n > 0 ? sumLive / n : 0):F2} " +
                      $"worstUncarved={worstRaw:F2} " +
                      $"carveWaterOnLoad={TerrainSampler.CarveWaterOnLoad.ToString().ToLowerInvariant()}");

            // A readable transect at the Old Town camera site, 15 m steps.
            Vector3 u = GeoFrame.FromLatLon(41.6950, 44.8060);
            float gx = -u.x, gz = u.z;   // GeoFrame mirrors X; the grid is game-frame
            for (int k = -10; k <= 10; k++)
            {
                float x = gx + k * 15f, z = gz;
                var lvl = river.MeasuredLevelAt(x, z);
                Debug.Log($"RBTRANSECT d={k * 15} x={x:F0} z={z:F0} " +
                          $"inMask={river.InRiverMask(x, z).ToString().ToLowerInvariant()} " +
                          $"uncarved={raw.HeightAtGame(x, z):F2} live={terrain.HeightAtGame(x, z):F2} " +
                          $"level={(lvl.HasValue ? lvl.Value : float.NaN):F2} " +
                          $"riverHeightAt={river.HeightAt(x, z):F2}");
            }
        }

        // ==================================================================
        // 2b. the river frames + which renderer owns the blue
        // ==================================================================
        static void RiverShots()
        {
            if (!FindRiverLook(out Vector3 surface, out Vector3 along))
            {
                Check("riverSheetInScene", false, "no river_sheet mesh");
                return;
            }
            Vector3 sun = SunDirection();
            if (Vector3.Dot(along, -sun) < 0f) along = -along;
            Vector3 grazePos = surface + Vector3.up * 3f;
            Vector3 grazeLook = surface + along * 420f + Vector3.up * 2.4f;
            Vector3 topPos = surface + Vector3.up * 140f;
            Vector3 topLook = surface + along * 30f;
            Debug.Log($"RBSITE surface={surface.ToString("F1")} along={along.ToString("F3")} " +
                      $"grazeCam={grazePos.ToString("F1")} grazeLook={grazeLook.ToString("F1")} " +
                      $"topCam={topPos.ToString("F1")} topLook={topLook.ToString("F1")}");

            RiverCrossSection(surface, along);

            var gz = Render(grazePos, grazeLook, W, H, Warmup);
            SavePng("rb_grazing");
            FrameStats("rb_grazing", gz);
            int paleGraze = CountPaleBlue(gz);
            Free();

            var tp = Render(topPos, topLook, W, H, Warmup);
            SavePng("rb_top");
            FrameStats("rb_top", tp);
            int paleTop = CountPaleBlue(tp);
            Free();
            Debug.Log($"RBPALE grazing={paleGraze} top={paleTop} of={W * H} " +
                      $"waterHex={TerrainBuilder.WaterHex}");
            // THE ROW'S OWN TEST, in pixels: the channel must stop reading as
            // the ground palette's class-2 blue. 161,251 px of 1,440,000
            // (11.2%) before the fill, measured at this same camera.
            Check("channelNotBedBlue", paleTop < 30000,
                  $"paleBluePx={paleTop} top-down — the carved bed is still the river");
            // ...and it must be WATER that replaced it, not nothing.
            var fillGo = GameObject.Find("river_fill");
            Check("riverFillInScene", fillGo != null, "RiverSheet.FillMesh never reached the scene");

            Attribute("grazing", grazePos, grazeLook, paleGraze);
            Attribute("top", topPos, topLook, paleTop);

            // WHAT IS UNDER THE BLUE. The attribution says the channel's blue
            // belongs to GrassRenderer's cut-terrain patch; switching that one
            // renderer off says what the frame shows in its place — the water
            // material (so the patch was simply ON TOP of a sheet that is
            // there) or bare ground (so there is no sheet over the channel at
            // all). Those are different bugs and the mask pass cannot tell
            // them apart.
            var cut = GameObject.Find("cut_terrain");
            var cutMr = cut != null ? cut.GetComponent<MeshRenderer>() : null;
            if (cutMr != null)
            {
                cutMr.enabled = false;
                var f = Render(topPos, topLook, W, H, Warmup);
                SavePng("rb_top_cutoff");
                int pale = CountPaleBlue(f);
                long rs = 0, gs = 0, bs = 0;
                for (int i = 0; i < f.Px.Length; i++) { rs += f.Px[i].r; gs += f.Px[i].g; bs += f.Px[i].b; }
                Free();
                cutMr.enabled = true;
                Debug.Log($"RBCUTOFF paleBlueWithCutTerrain={paleTop} paleBlueWithout={pale} " +
                          $"meanRGB={(rs / (double)(W * H)):F1},{(gs / (double)(W * H)):F1},{(bs / (double)(W * H)):F1}");
            }
        }

        /// <summary>
        /// ACROSS the river, 6 m at a time: is there sheet geometry over this
        /// point at all, how high is it, how high is the ground under it, and
        /// what ground class is painted there. A single "the channel is blue"
        /// screenshot cannot separate "the bed stands above the water" from
        /// "the water sheet does not reach here"; these numbers can.
        ///
        /// The sheet mesh is in UNITY space (RiverSheet mirrors X once at the
        /// end); the terrain grid and the carve mask are in the GAME frame, so
        /// every lookup below mirrors back.
        /// </summary>
        static void RiverCrossSection(Vector3 surface, Vector3 along)
        {
            var ctx = CityGroundData.Shared();
            var terrain = ctx?.City?.Terrain;
            var river = ctx?.River;
            var go = GameObject.Find("river_sheet");
            var mesh = go != null ? go.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (terrain == null || river == null || mesh == null) return;
            var verts = mesh.vertices;
            var xf = go.transform.localToWorldMatrix;

            // The sheet's TRIANGLES, in world space, once — a nearest-VERTEX
            // distance says nothing about coverage here: two subdivision
            // passes over a 2,399-point ring leave interior triangles tens of
            // metres across, so mid-channel the nearest vertex is 29 m away on
            // a surface that covers the point perfectly well. Only a
            // point-in-triangle test answers "is there water over this spot".
            var tri = mesh.triangles;
            var wv = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) wv[i] = xf.MultiplyPoint3x4(verts[i]);

            bool SheetOver(Vector3 p, out float y)
            {
                y = 0f;
                for (int t = 0; t < tri.Length; t += 3)
                {
                    Vector3 a = wv[tri[t]], b = wv[tri[t + 1]], c = wv[tri[t + 2]];
                    float minx = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxx = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                    if (p.x < minx || p.x > maxx) continue;
                    float minz = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxz = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                    if (p.z < minz || p.z > maxz) continue;
                    float d1 = (p.x - b.x) * (a.z - b.z) - (a.x - b.x) * (p.z - b.z);
                    float d2 = (p.x - c.x) * (b.z - c.z) - (b.x - c.x) * (p.z - c.z);
                    float d3 = (p.x - a.x) * (c.z - a.z) - (c.x - a.x) * (p.z - a.z);
                    bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                    bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                    if (neg && pos) continue;
                    y = (a.y + b.y + c.y) / 3f;
                    return true;
                }
                return false;
            }

            Vector3 perp = Vector3.Cross(along.normalized, Vector3.up).normalized;
            for (int k = -15; k <= 15; k++)
            {
                Vector3 p = surface + perp * (k * 6f);
                bool covered = SheetOver(p, out float sheetOverY);
                // ...and WHAT IS ACTUALLY ON TOP. The terrain tiles and the
                // cut-terrain patch both carry MeshColliders, so one ray says
                // which surface a pixel of the channel is, and at what height,
                // without inferring it from a colour.
                string hitName = "none"; float hitY = float.NaN;
                if (Physics.Raycast(new Vector3(p.x, p.y + 150f, p.z), Vector3.down, out var rh, 400f))
                { hitName = rh.collider.gameObject.name; hitY = rh.point.y; }
                float best = float.MaxValue, bestY = 0f;
                for (int i = 0; i < wv.Length; i++)
                {
                    Vector3 w = wv[i];
                    float d = (w.x - p.x) * (w.x - p.x) + (w.z - p.z) * (w.z - p.z);
                    if (d < best) { best = d; bestY = w.y; }
                }
                float gx = -p.x, gz = p.z;              // mirror back to the game frame
                int c = Mathf.Clamp(Mathf.RoundToInt((gx - terrain.MinX) / terrain.Step), 0, terrain.W - 1);
                int r = Mathf.Clamp(Mathf.RoundToInt((gz - terrain.MinZ) / terrain.Step), 0, terrain.H - 1);
                byte cls = terrain.Classes != null ? terrain.Classes[r * terrain.W + c] : (byte)0;
                var lvl = river.MeasuredLevelAt(gx, gz);
                Debug.Log($"RBCROSS d={k * 6} unity=({p.x:F0},{p.z:F0}) cls={cls} " +
                          $"inMask={river.InRiverMask(gx, gz).ToString().ToLowerInvariant()} " +
                          $"sheetOver={covered.ToString().ToLowerInvariant()} sheetOverY={sheetOverY:F2} " +
                          $"sheetVertDist={Mathf.Sqrt(best):F1} sheetY={bestY:F2} " +
                          $"topSurface={hitName} topY={hitY:F2} " +
                          $"bed={river.HeightAt(gx, gz):F2} terrain={terrain.HeightAtGame(gx, gz):F2} " +
                          $"level={(lvl.HasValue ? lvl.Value : float.NaN):F2}");
            }
        }

        /// <summary>
        /// WHICH RENDERER the channel's blue pixels belong to. One candidate
        /// group at a time is painted magenta through a MaterialPropertyBlock —
        /// per-RENDERER, so shared materials are untouched and the groups
        /// cannot contaminate each other — and the magenta is counted. Nothing
        /// in this project is ever magenta (RenderShotProbe.FrameStats treats
        /// it as the error-material signature precisely because it never
        /// occurs), so the attribution is exact.
        ///
        /// The previous pass could only paint the WATER material, which proved
        /// the blue is not water and could not say what it is.
        /// </summary>
        static void Attribute(string tag, Vector3 pos, Vector3 look, int paleBefore)
        {
            foreach (var g in CandidateGroups())
            {
                if (g.Value.Count == 0) { Debug.Log($"RBATTR tag={tag} group={g.Key} renderers=0"); continue; }
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", Color.magenta);
                mpb.SetColor("_Color", Color.magenta);
                foreach (var r in g.Value) r.SetPropertyBlock(mpb);

                var f = Render(pos, look, W, H, Warmup);
                int mag = 0, pale = 0;
                for (int i = 0; i < f.Px.Length; i++)
                {
                    var c = f.Px[i];
                    if (c.r > 150 && c.b > 150 && c.g * 2 < (c.r + c.b) * 0.5f) mag++;
                    if (c.b > 120 && c.b > c.g + 20 && c.g > c.r + 10) pale++;
                }
                if (mag > f.Px.Length / 200) SavePng($"rb_{tag}_{g.Key}");
                Free();
                foreach (var r in g.Value) r.SetPropertyBlock(null);

                Debug.Log($"RBATTR tag={tag} group={g.Key} renderers={g.Value.Count} " +
                          $"magentaPx={mag} magentaPct={(100.0 * mag / (W * H)):F2} " +
                          $"paleBluePx={pale} paleBlueBefore={paleBefore} " +
                          $"paleBlueClaimed={paleBefore - pale}");
            }
        }

        /// <summary>The candidates the row names: the 49 coarse Terrain tiles,
        /// GrassRenderer's 5 m cut-terrain patch, its grass/rock ground
        /// overlay, the river sheet and the still-water bodies.</summary>
        static Dictionary<string, List<MeshRenderer>> CandidateGroups()
        {
            var g = new Dictionary<string, List<MeshRenderer>>
            {
                { "terrain_tiles", new List<MeshRenderer>() },
                { "cut_terrain", new List<MeshRenderer>() },
                { "grass_cover", new List<MeshRenderer>() },
                { "river_sheet", new List<MeshRenderer>() },
                { "river_fill", new List<MeshRenderer>() },
                { "water_bodies", new List<MeshRenderer>() },
                { "other", new List<MeshRenderer>() },
            };
            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                string n = mr.gameObject.name;
                if (n.StartsWith("tile_")) g["terrain_tiles"].Add(mr);
                else if (n == "cut_terrain") g["cut_terrain"].Add(mr);
                else if (n.StartsWith("grass_") || n.StartsWith("rock_")) g["grass_cover"].Add(mr);
                else if (n == "river_sheet") g["river_sheet"].Add(mr);
                else if (n == "river_fill") g["river_fill"].Add(mr);
                else if (n == "water_bodies") g["water_bodies"].Add(mr);
            }
            g.Remove("other");
            return g;
        }

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

        static Vector3 SunDirection()
        {
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) return l.transform.forward;
            return Vector3.down;
        }

        static bool FindRiverLook(out Vector3 surface, out Vector3 along)
        {
            surface = Vector3.zero; along = Vector3.forward;
            var go = GameObject.Find("river_sheet");
            var mf = go != null ? go.GetComponent<MeshFilter>() : null;
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) return false;

            Vector3 target = GeoFrame.FromLatLon(41.6950, 44.8060);
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
            // ...and then the LOCAL CENTROID of the sheet around it. The
            // nearest vertex to a hand-picked lat/lon lands on the BANK of the
            // polygon (measured: 164 m from the guessed Baratashvili point,
            // 5 m inside the east edge), which puts the camera over the quay
            // and makes the frame a picture of the bank, not the river.
            {
                Vector3 sum = Vector3.zero; int n = 0;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = xf.MultiplyPoint3x4(verts[i]);
                    if ((w - surface).sqrMagnitude < 60f * 60f) { sum += w; n++; }
                }
                if (n > 0) { Vector3 c = sum / n; surface = new Vector3(c.x, surface.y, c.z); }
            }

            int far = -1; float farD = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = xf.MultiplyPoint3x4(verts[i]);
                float d = (w - surface).sqrMagnitude;
                if (d > 200f * 200f && d < 320f * 320f && d > farD) { farD = d; far = i; }
            }
            if (far >= 0)
            {
                Vector3 w = xf.MultiplyPoint3x4(verts[far]) - surface;
                w.y = 0f;
                if (w.sqrMagnitude > 1e-3f) along = w.normalized;
            }
            return true;
        }

        // ==================================================================
        // scaffold (duplicated on purpose — see class doc)
        // ==================================================================
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

        static void PinWaterRipple()
        {
            var m = GroundMaterials.Water();
            if (m == null) return;
            float env = m.HasProperty("_EnvMapIntensity") ? m.GetFloat("_EnvMapIntensity") : -1f;
            GroundMaterials.DriveWaterRipple(m, FrozenWaterTime);
            if (env >= 0f) m.SetFloat("_EnvMapIntensity", env);
        }

        static void SavePng(string id)
        {
            if (_lastTex == null) return;
            string file = Path.Combine(_out, id + ".png");
            File.WriteAllBytes(file, _lastTex.EncodeToPNG());
            Debug.Log($"FRPNG file={file}");
        }

        static void Free()
        {
            if (_lastTex != null) { UnityEngine.Object.DestroyImmediate(_lastTex); _lastTex = null; }
            if (_lastRt != null) { _lastRt.Release(); UnityEngine.Object.DestroyImmediate(_lastRt); _lastRt = null; }
        }

        static void FrameStats(string id, Frame f)
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
            Debug.Log($"FRFRAME id={id} meanRGB={mr:F1},{mg:F1},{mb:F1} variance={var0:F1} buckets={seen.Count} " +
                      $"magentaPct={(100.0 * magenta / n):F3} blownWhitePct={(100.0 * blown / n):F2} " +
                      $"blackPct={(100.0 * dark / n):F2}");
            Check($"frameNotBlank_{id}", seen.Count > 40 && var0 > 25.0, $"buckets={seen.Count} var={var0:F1}");
        }

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
                if (type == null) { Debug.Log($"FRTICKER id=missingtype name={tn}"); continue; }
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
            Debug.Log($"FRTICKER id=total n={Tickers.Count}");
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
                    Debug.Log($"FRTICKER id=threw type={t.Name} msg={e.Message}");
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

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"FRCHECK {key}={(cond ? "PASS" : "FAIL")}";
            if (note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Done()
        {
            Debug.Log($"FACADERIVER wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "FACADERIVER RESULT=PASS" : "FACADERIVER RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }
    }
}
