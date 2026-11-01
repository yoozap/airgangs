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
    /// The three docs/open_items.json CITY rows of 2026-10-31:
    /// `sign-highway-scale`, `relief-floating-off-kit`, `shop-sign-text-mirrored`.
    ///
    /// ITS OWN FILE for the same reason FacadeRiverProbe.cs is its own file —
    /// the render scaffold (open Game.unity, run PlayableSceneBoot, tick every
    /// Graphics.RenderMeshInstanced renderer BY HAND because LateUpdate is
    /// never delivered outside play mode, render into a RenderTexture, read the
    /// pixels back) is duplicated on purpose rather than shared, so one row's
    /// probe cannot break another's.
    ///
    /// Run WITH graphics (drop -nographics, keep -batchmode -quit):
    ///   TF_SHOTS=&lt;dir&gt; Unity -batchmode -quit -projectPath unity \
    ///     -executeMethod TbilisiFly.Probes.CitySignsProbe.Run -logFile ...
    ///
    /// TWO BEFORE-ARM ENV FLAGS, because an in-run A/B of an instanced layer is
    /// invalid in batch mode (Graphics.RenderMeshInstanced submissions are not
    /// consumed by a manual Camera.Render, so the previous arm's draws are
    /// still live and the two frames come back byte-identical):
    ///   TF_SIGN_NOMODEL=1  StreetFurnitureRenderer.ApplyModelTransform = false
    ///   TF_SIGN_NOFLIP=1   ShopfrontRenderer.MirrorSignUv      = false
    /// Both cameras are derived from data NEITHER fix moves (the sign
    /// PLACEMENT list, which is pure road maths, and a sign slot's own
    /// transform), so the two processes shoot the identical frame.
    ///
    /// TF_SIGN_NOFLIP REVERTS ONLY HALF of the shop-sign fix. The shipped
    /// defect was a 180 degree rotation — a U mirror (handedness, reverted by
    /// this flag) AND a V mirror (ShopSignTextures.Paint uploading a
    /// canvas-row-0-is-TOP buffer straight into a Texture2D whose row 0 is the
    /// BOTTOM, which has no flag and is simply fixed). The true before-frame is
    /// therefore the one shot before that second fix landed
    /// (scratchpad c3/b5/shopsign_clear_before.png, "Archive" rotated 180);
    /// this flag alone now yields the U-mirror half only.
    /// </summary>
    public static class CitySignsProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string SignFbx = "Assets/Resources/Art/Props/signs/sign-highway.fbx";
        const int W = 1600, H = 900;
        const int Warmup = 4;

        /// <summary>public/models/signs/sign-highway.glb's own POSITION
        /// accessor bounds: y 0 .. 0.8221249580383301 m. The JS scales against
        /// exactly this number (citymap.js:8429-8431, after extractModel()
        /// bakes each node's matrixWorld into the geometry at citymap.js:68),
        /// so it is the pipeline constant this port has no other way to know —
        /// the KIND of cross-check the crane's 7.9 m hook was.</summary>
        const float GlbModelHeight = 0.8221250f;

        static string _out;
        static Camera _cam;
        static bool _allPass = true;
        static readonly Stopwatch Wall = Stopwatch.StartNew();

        public static void Run()
        {
            _out = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(_out)) _out = Path.Combine(Path.GetTempPath(), "tf_city_signs");
            Directory.CreateDirectory(_out);

            bool noModel = Environment.GetEnvironmentVariable("TF_SIGN_NOMODEL") == "1";
            bool noFlip = Environment.GetEnvironmentVariable("TF_SIGN_NOFLIP") == "1";
            StreetFurnitureRenderer.ApplyModelTransform = !noModel;   // BEFORE the scene opens
            ShopfrontRenderer.MirrorSignUv = !noFlip;

            bool gpu = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            Debug.Log($"CITYSIGNS out={_out} device={SystemInfo.graphicsDeviceType} " +
                      $"applySignModel={(!noModel).ToString().ToLowerInvariant()} " +
                      $"mirrorSignUv={(!noFlip).ToString().ToLowerInvariant()}");
            if (!gpu) Debug.Log("CITYSIGNS id=nogpu msg=numbers only (run WITHOUT -nographics for the frames)");

            try
            {
                SignAssetFacts();          // needs no scene at all — lands in the log first

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check("sceneLoads", scene.IsValid());
                if (!scene.IsValid()) { Done(); return; }

                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                try { boot?.Run(); }
                catch (Exception e) { Debug.Log($"CITYSIGNS id=bootthrew msg={e.Message}"); Debug.LogException(e); }

                _cam = Camera.main;
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

                SignBatchFacts(gpu);       // 1. sign-highway-scale
                ReliefFloatFacts();        // 2. relief-floating-off-kit
                if (gpu) ShopSignShot();   // 3. shop-sign-text-mirrored
            }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"CITYSIGNS id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Done();
        }

        // ==================================================================
        // 1. sign-highway-scale — the ASSET's own numbers
        // ==================================================================
        static void SignAssetFacts()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(SignFbx);
            Check("signFbxImports", root != null, SignFbx);
            if (root == null) return;

            var mfs = root.GetComponentsInChildren<MeshFilter>(true);
            Debug.Log($"SIGNROOT nodes={mfs.Length} rootScale={root.transform.localScale.ToString("F3")} " +
                      $"rootEuler={root.transform.localRotation.eulerAngles.ToString("F1")}");

            foreach (var mf in mfs)
            {
                if (mf.sharedMesh == null) continue;
                Bounds raw = mf.sharedMesh.bounds;
                // The FULL chain out of mesh space — this asset's MeshFilter is
                // on the prefab ROOT, so the root-relative matrix the building/
                // facade kits use is identity here. See
                // StreetFurnitureRenderer.ResolveSignModel.
                Matrix4x4 model = mf.transform.localToWorldMatrix;
                Bounds world = CityRenderer.TransformBounds(raw, model);
                Vector3 ls = mf.transform.lossyScale;
                Vector3 eu = mf.transform.localRotation.eulerAngles;
                bool onRoot = mf.transform == root.transform;
                Debug.Log($"SIGNPART name={mf.gameObject.name} mesh={mf.sharedMesh.name} onRoot={onRoot.ToString().ToLowerInvariant()} " +
                          $"rawSize={raw.size.x:F5},{raw.size.y:F5},{raw.size.z:F5} " +
                          $"lossyScale={ls.x:F1},{ls.y:F1},{ls.z:F1} euler={eu.x:F1},{eu.y:F1},{eu.z:F1} " +
                          $"modelSize={world.size.x:F4},{world.size.y:F4},{world.size.z:F4} " +
                          $"modelMinY={world.min.y:F4} zTallerThanY={(raw.size.z > raw.size.y).ToString().ToLowerInvariant()}");

                // What SignScale is fed, and what the sign therefore measures.
                float scaleBuggy = RoadSignsStreetlights.SignScale(raw.size.y);
                float scaleFixed = RoadSignsStreetlights.SignScale(world.size.y);
                Debug.Log($"SIGNSCALE rawY={raw.size.y:F5} modelY={world.size.y:F4} glbY={GlbModelHeight:F4} " +
                          $"scaleFromRawY={scaleBuggy:F3} scaleFromModelY={scaleFixed:F3} " +
                          $"heightBefore={(raw.size.y * scaleBuggy):F3} " +
                          $"heightAfter={(world.size.y * scaleFixed):F3} " +
                          $"worldSizeBefore={(raw.size.x * scaleBuggy):F3},{(raw.size.y * scaleBuggy):F3},{(raw.size.z * scaleBuggy):F3} " +
                          $"worldSizeAfter={(world.size.x * scaleFixed):F3},{(world.size.y * scaleFixed):F3},{(world.size.z * scaleFixed):F3}");

                // The pipeline cross-check: the composed model's height must be
                // the GLB's own 0.8221 m, which nothing in the FBX import path
                // could know. 1 mm.
                Check("signModelHeightMatchesGlb", Mathf.Abs(world.size.y - GlbModelHeight) < 0.001f,
                      $"modelY={world.size.y:F4} glbY={GlbModelHeight:F4}");
                // ...and the model stands ON y=0 like the GLB does (bb.min.y == 0),
                // so the placement's terrain height is still the sign's foot.
                Check("signModelSitsOnZero", Mathf.Abs(world.min.y) < 0.002f, $"minY={world.min.y:F4}");
                break;  // one mesh node in this asset; the loop is the audit
            }
        }

        // ==================================================================
        // 1b. sign-highway-scale — what the RENDERER built, and the frame
        // ==================================================================
        static void SignBatchFacts(bool gpu)
        {
            var sfr = UnityEngine.Object.FindFirstObjectByType<StreetFurnitureRenderer>();
            Check("streetFurnitureInScene", sfr != null);
            if (sfr == null) return;
            sfr.EnsureBuilt();
            var rep = sfr.LastReport;
            Debug.Log($"SIGNCOUNTS applyModel={StreetFurnitureRenderer.ApplyModelTransform.ToString().ToLowerInvariant()} " +
                      $"signs={rep?.SignPlacements ?? -1} signBatches={rep?.SignBatches ?? -1} " +
                      $"poles={rep?.PolePlacements ?? -1} lamps={rep?.LampPlacements ?? -1} " +
                      $"batches={sfr.BatchCount} instances={sfr.TotalInstances} drawCalls={sfr.TotalDrawCalls}");

            // The measured metre size of ONE real road sign, off the instance
            // matrix the renderer actually built (not a recomputation).
            var batches = sfr.BatchesForProbe;
            Matrix4x4 firstSign = Matrix4x4.identity;
            Mesh signMesh = null;
            if (batches != null)
                foreach (var b in batches)
                {
                    if (b.Mesh == null || b.Matrices == null || b.Matrices.Length == 0) continue;
                    if (b.Mesh.name.IndexOf("sign", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    signMesh = b.Mesh; firstSign = b.Matrices[0]; break;
                }
            if (signMesh != null)
            {
                Bounds wb = CityRenderer.TransformBounds(signMesh.bounds, firstSign);
                Debug.Log($"SIGNWORLD mesh={signMesh.name} worldSize={wb.size.x:F3},{wb.size.y:F3},{wb.size.z:F3} " +
                          $"footY={wb.min.y:F2} centre={wb.center.ToString("F1")}");
                if (StreetFurnitureRenderer.ApplyModelTransform)
                    Check("signIsFiveMetresTall", Mathf.Abs(wb.size.y - 5f) < 0.15f, $"height={wb.size.y:F3}");
            }
            else Check("signBatchFound", false);

            // The frame: 15 m from a real sign on a trunk avenue. The camera
            // comes from the PLACEMENT list, which is pure road maths and
            // identical in both arms.
            if (!gpu) return;
            var city = CityData.Load(buildings: false);
            MergedRoads roads;
            var (lr, lb, lt) = RoadBuilder.LoadLegacyLists();
            RoadNet net = null;
            try { net = RoadBuilder.LoadRoadNet(); } catch { }
            roads = net != null ? RoadBuilder.MergeRoadNet(lr, lb, lt, net)
                                : new MergedRoads { Roads = lr, Bridges = lb, Tunnels = lt };
            var placements = RoadSignsStreetlights.BuildRoadSignPlacements(roads.Roads, city.Terrain.HeightAtGame);
            Debug.Log($"SIGNPLACE n={placements.Count}");
            if (placements.Count == 0) { Check("signPlacements", false); return; }

            // Vazha-Pshavela / Saburtalo, the same corner of town the facade
            // rows were shot at.
            Vector3 target = GeoFrame.FromLatLon(41.7272, 44.7490);
            // XZ ONLY: GeoFrame.FromLatLon returns y=0 and everything in this
            // city sits 400-500 m up, so a 3-D distance here is dominated by
            // the terrain height and picks a sign half a kilometre away.
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < placements.Count; i++)
            {
                Vector3 p = GeoFrame.ToUnity(placements[i].X, placements[i].Y, placements[i].Z);
                float d = (p.x - target.x) * (p.x - target.x) + (p.z - target.z) * (p.z - target.z);
                if (d < bd) { bd = d; best = i; }
            }
            var s = placements[best];
            Vector3 pos = GeoFrame.ToUnity(s.X, s.Y, s.Z);
            float yawU = GeoFrame.YawToUnity(s.YawGame);
            // The sign's own facing (+Z after the placement rotation), so the
            // camera stands in FRONT of the board, 15 m out and 3.5 m up.
            Vector3 face = new Vector3(Mathf.Sin(yawU), 0f, Mathf.Cos(yawU));
            Vector3 camPos = pos + face * 15f + Vector3.up * 3.5f;
            Vector3 look = pos + Vector3.up * 2.5f;
            string tag = StreetFurnitureRenderer.ApplyModelTransform ? "after" : "before";
            Debug.Log($"SIGNSHOT id=sign_avenue_{tag} signIdx={best} distFromTarget={Mathf.Sqrt(bd):F0} " +
                      $"sign={pos.ToString("F1")} cam={camPos.ToString("F1")} look={look.ToString("F1")}");
            var f = Render(camPos, look);
            SavePng($"sign_avenue_{tag}");
            FrameStats($"sign_avenue_{tag}", f);
            Free();
        }

        // ==================================================================
        // 2. relief-floating-off-kit — is any relief instance off a wall?
        // ==================================================================
        /// <summary>
        /// THE ROW'S OWN TEST, as asked: a short ray along each relief
        /// instance's own normal, counting the instances with NO wall within
        /// 0.5 m. FacadeRiverProbe's FKINSET already measures how deep a relief
        /// piece sits in the wall it hits — but it only counts rays that HIT,
        /// so an instance standing in mid-air is invisible to it. This counts
        /// the misses, which is the whole question.
        /// </summary>
        static void ReliefFloatFacts()
        {
            var cr = UnityEngine.Object.FindFirstObjectByType<CityRenderer>();
            Check("cityRendererInScene", cr != null);
            if (cr == null) return;
            cr.EnsureBuilt();

            var kitRoot = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Art/Props/facade_kit.fbx");
            var kitMeshes = new HashSet<Mesh>();
            if (kitRoot != null)
                foreach (var kv in FacadeKitLoader.GroupByBaseName(kitRoot.transform))
                    if (kv.Value.Mesh != null) kitMeshes.Add(kv.Value.Mesh);
            Debug.Log($"RFKIT parts={kitMeshes.Count}");

            var rep = cr.LastReport;
            if (rep != null)
            {
                var keys = new List<string>(rep.FacadeLayers.Keys);
                keys.Sort();
                var sb = new System.Text.StringBuilder();
                foreach (var k in keys) sb.Append($" {k}:{rep.FacadeLayers[k]}");
                Debug.Log($"RFLAYERS batches={rep.FacadeBatches} instances={rep.FacadeInstances}{sb}");
            }

            // Two sites, the ones the row was raised from.
            ReliefFloatAt(cr, kitMeshes, "saburtalo", GeoFrame.FromLatLon(41.7272, 44.7490));
            ReliefFloatAt(cr, kitMeshes, "oldtown", GeoFrame.FromLatLon(41.6917, 44.8087));
        }

        static void ReliefFloatAt(CityRenderer cr, HashSet<Mesh> kitMeshes, string id, Vector3 target)
        {
            var batches = cr.BatchesForProbe;
            if (batches == null) return;
            var panels = cr.PanelColliders ?? new List<Bounds>();
            int sampled = 0, hit = 0, deep = 0, floating = 0, inPanelKit = 0;
            double sumGap = 0;
            string firstFloat = "none";
            // Per-kit-part breakdown of the misses. A parapet or a roof vent
            // has no wall behind it BY CONSTRUCTION (citymap.js:5369/5382-5403
            // puts them on the flat roof, not on an elevation), so a horizontal
            // ray from one must miss — and without this split a correct roof
            // layer reads as "floating relief".
            var floatBy = new Dictionary<string, int>();
            var seenBy = new Dictionary<string, int>();
            foreach (var b in batches)
            {
                if (b.Mesh == null || b.Matrices == null || !kitMeshes.Contains(b.Mesh)) continue;
                for (int i = 0; i < b.Matrices.Length; i++)
                {
                    Vector3 ip = b.Matrices[i].GetColumn(3);
                    // XZ ONLY — FromLatLon's y is 0 and the city stands at
                    // 400-500 m, so a 3-D test matches NOTHING.
                    float dx = ip.x - target.x, dz = ip.z - target.z;
                    if (dx * dx + dz * dz > 250f * 250f) continue;
                    Vector3 n = FacadeNormal(b.Matrices[i]);
                    sampled++;
                    seenBy.TryGetValue(b.Mesh.name, out int sn); seenBy[b.Mesh.name] = sn + 1;
                    // From 0.5 m OUTSIDE the instance, straight back along the
                    // normal: a wall within 0.5 m behind the instance is a hit
                    // at <= 1.0 m. Anything else is a piece hanging in the air.
                    if (Physics.Raycast(ip + n * 0.5f, -n, out var wh, 1.0f))
                    {
                        hit++; sumGap += (wh.distance - 0.5f);
                    }
                    else if (Physics.Raycast(ip + n * 0.5f, -n, out _, 2.5f))
                    {
                        // A wall 0.5-2.0 m behind the instance origin: the deep
                        // balcony kit parts (balc_glazed 1.08 m deep, balc_wood
                        // 1.31 m) are anchored at the balcony, not at the
                        // elevation. Counted apart so the strict 0.5 m figure
                        // is not read as "hanging in mid-air".
                        deep++;
                    }
                    else
                    {
                        floating++;
                        floatBy.TryGetValue(b.Mesh.name, out int fn); floatBy[b.Mesh.name] = fn + 1;
                        // THE ROW'S OWN HYPOTHESIS, tested directly: is this
                        // piece standing on a KIT building? CityRenderer.
                        // PanelColliders is one AABB per panel-block placement
                        // (citymap.js:6060-6071), i.e. exactly the buildings
                        // the JS skips relief on.
                        foreach (var pc in panels) if (pc.Contains(ip)) { inPanelKit++; break; }
                        if (firstFloat == "none")
                            firstFloat = $"{b.Mesh.name}@{ip.ToString("F1")}";
                    }
                }
            }
            Debug.Log($"RFFLOAT id={id} sampledWithin250m={sampled} wallWithin0_5m={hit} " +
                      $"noWallWithin0_5m={floating} floatPct={(sampled > 0 ? 100.0 * floating / sampled : 0.0):F2} " +
                      $"wallWithin2m={deep} meanGapM={(hit > 0 ? sumGap / hit : 0.0):F3} " +
                      $"floatingInsideAPanelKitBox={inPanelKit} firstFloating={firstFloat}");
            {
                var names = new List<string>(seenBy.Keys);
                names.Sort();
                var sb = new System.Text.StringBuilder();
                int wallFloat = 0, wallSeen = 0;
                foreach (var nm in names)
                {
                    floatBy.TryGetValue(nm, out int fn);
                    sb.Append($" {nm}:{fn}/{seenBy[nm]}");
                    bool roofLayer = nm.StartsWith("parapet") || nm.StartsWith("roof_");
                    if (!roofLayer) { wallFloat += fn; wallSeen += seenBy[nm]; }
                }
                Debug.Log($"RFFLOATBY id={id}{sb}");
                Debug.Log($"RFWALLFLOAT id={id} wallLayerSampled={wallSeen} wallLayerNoWall={wallFloat} " +
                          $"wallFloatPct={(wallSeen > 0 ? 100.0 * wallFloat / wallSeen : 0.0):F2}");
            }
        }

        /// <summary>FacadeRiverProbe.FacadeNormal, verbatim — the kit model's
        /// Rx(-90) puts the part's facing on the matrix's UP column, so take
        /// whichever of the two candidate columns is actually horizontal.</summary>
        static Vector3 FacadeNormal(Matrix4x4 m)
        {
            Vector3 fromUp = m.MultiplyVector(Vector3.up);
            Vector3 a = new Vector3(-fromUp.x, 0f, -fromUp.z);
            Vector3 fromFwd = m.MultiplyVector(Vector3.forward);
            Vector3 b = new Vector3(fromFwd.x, 0f, fromFwd.z);
            Vector3 pick = a.sqrMagnitude >= b.sqrMagnitude ? a : b;
            return pick.sqrMagnitude > 1e-9f ? pick.normalized : Vector3.forward;
        }

        // ==================================================================
        // 3. shop-sign-text-mirrored — the awning, read close up
        // ==================================================================
        static void ShopSignShot()
        {
            var sr = UnityEngine.Object.FindFirstObjectByType<ShopfrontRenderer>();
            Check("shopfrontRendererInScene", sr != null);
            if (sr == null) return;
            sr.EnsureBuilt();

            // Old Town, the same block facade_oldtown_*.png was shot on. Tick
            // the renderer from there so SignPool assigns its 192 slots around
            // that point (src/citymap.js:1140's own per-frame call).
            Vector3 target = GeoFrame.FromLatLon(41.6917, 44.8087);
            for (int i = 0; i < 6; i++) sr.TickFrame(target, 0.5f);
            Debug.Log($"SSPOOL active={sr.SignPoolActiveCount} slots={sr.SignSlotCount} " +
                      $"updated={sr.LastSignPoolUpdated.ToString().ToLowerInvariant()}");

            // The nearest ENABLED sign slot to the target.
            Transform bestXf = null; float bd = float.MaxValue;
            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (!mr.enabled || !mr.gameObject.name.StartsWith("sign_slot_")) continue;
                float d = (mr.transform.position - target).sqrMagnitude;
                if (d < bd) { bd = d; bestXf = mr.transform; }
            }
            Check("signSlotEnabled", bestXf != null);
            if (bestXf == null) return;

            // THE NUMBER BEHIND THE PICTURE: the quad's own +X against the
            // viewer's right. A texture-space quad whose local +X lands on the
            // viewer's LEFT draws every glyph right-to-left; see
            // ShopfrontRenderer.QuadMesh's header.
            Vector3 quadX = bestXf.right;
            Vector3 quadZ = bestXf.forward;
            Vector3 viewFwd = -quadZ;                                  // viewer looks back at the plate
            Vector3 viewRight = Vector3.Cross(Vector3.up, viewFwd).normalized;
            float dot = Vector3.Dot(quadX.normalized, viewRight);
            var mfq = bestXf.GetComponent<MeshFilter>();
            var uvs = mfq != null && mfq.sharedMesh != null ? mfq.sharedMesh.uv : null;
            // u at the vertex on local -X (vertex 0) and on local +X (vertex 1)
            string uPair = (uvs != null && uvs.Length >= 2) ? $"{uvs[0].x:F0}->{uvs[1].x:F0}" : "n/a";
            // Which side of the screen the texture's u=0 edge lands on.
            // Vertex 0 is at local -X, vertex 1 at local +X, so u=0 sits at
            // local -X when uvs[0].x < uvs[1].x and at local +X otherwise;
            // dot(+localX, viewerRight) is `dot`, dot(-localX, viewerRight) is
            // -dot. +1 = u=0 drawn on the viewer's RIGHT (text MIRRORED),
            // -1 = on the viewer's left (reads forwards).
            float uZeroSide = (uvs != null && uvs.Length >= 2) ? (uvs[0].x < uvs[1].x ? -dot : dot) : 0f;
            Debug.Log($"SSAXIS slot={bestXf.name} quadLocalXDotViewerRight={dot:F3} " +
                      $"uAcrossLocalX={uPair} uZeroOnViewerRight={uZeroSide:F3} " +
                      $"mirrorSignUv={ShopfrontRenderer.MirrorSignUv.ToString().ToLowerInvariant()}");
            Check("signTextReadsForwards", uZeroSide < 0f,
                  $"uZeroOnViewerRight={uZeroSide:F3} (positive == the plate's left edge drawn on the right)");

            // Slightly above and looking down: the shopfront's own awning slab
            // stands in front of and below the plate and clips its lower half
            // from a dead-level camera.
            Vector3 pos = bestXf.position;
            Vector3 camPos = pos + quadZ * 4.0f + Vector3.up * 1.5f;
            string tag = ShopfrontRenderer.MirrorSignUv ? "after" : "before";
            Debug.Log($"SSSHOT id=shopsign_{tag} sign={pos.ToString("F1")} cam={camPos.ToString("F1")}");
            var f = Render(camPos, pos);
            SavePng($"shopsign_{tag}");
            FrameStats($"shopsign_{tag}", f);
            Free();

            // THE LEGIBLE FRAME. The shopfront's own awning slab stands 20 cm
            // further out than the sign plate and 20 cm lower, so from every
            // angle that sees the plate square-on it clips the bottom half of
            // every glyph — readable enough to see that the word ORDER flipped,
            // not enough to read the name. The awning is hidden for this one
            // shot (nothing is saved; the scene is never written) so the plate
            // can be photographed whole.
            {
                var hidden = new List<MeshRenderer>();
                foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                {
                    if (!mr.enabled) continue;
                    string nm = mr.gameObject.name;
                    if (nm != "awnings" && nm != "fuel_canopies") continue;
                    mr.enabled = false; hidden.Add(mr);
                }
                Vector3 clearCam = pos + quadZ * 3.2f;
                var fc = Render(clearCam, pos);
                SavePng($"shopsign_clear_{tag}");
                FrameStats($"shopsign_clear_{tag}", fc);
                Free();
                foreach (var mr in hidden) mr.enabled = true;
                Debug.Log($"SSCLEAR id=shopsign_clear_{tag} awningRenderersHidden={hidden.Count} cam={clearCam.ToString("F1")}");
            }

            // ...and the wide Old Town frame the row was raised from, so the
            // awning can be compared in its own context too.
            Vector3 wideCam = new Vector3(5172.7f, 408.1f, -3702.2f);
            Vector3 wideLook = new Vector3(5149.7f, 406.6f, -3712.0f);
            var fw = Render(wideCam, wideLook);
            SavePng($"shopsign_oldtown_{tag}");
            FrameStats($"shopsign_oldtown_{tag}", fw);
            Free();
        }

        // ==================================================================
        // scaffold (duplicated on purpose — see class doc)
        // ==================================================================
        sealed class Frame { public Color32[] Px; public int W, H; }
        static Texture2D _lastTex;
        static RenderTexture _lastRt;

        static Frame Render(Vector3 pos, Vector3 look)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1 };
            var dir = look - pos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, Vector3.up));
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            for (int i = 0; i <= Warmup; i++) { TickRenderers(); _cam.Render(); }

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = active;
            _cam.targetTexture = prev;
            _lastTex = tex; _lastRt = rt;
            return new Frame { Px = tex.GetPixels32(), W = W, H = H };
        }

        static void SavePng(string id)
        {
            if (_lastTex == null) return;
            string file = Path.Combine(_out, id + ".png");
            File.WriteAllBytes(file, _lastTex.EncodeToPNG());
            Debug.Log($"CSPNG file={file}");
        }

        static void Free()
        {
            if (_lastTex != null) { UnityEngine.Object.DestroyImmediate(_lastTex); _lastTex = null; }
            if (_lastRt != null) { _lastRt.Release(); UnityEngine.Object.DestroyImmediate(_lastRt); _lastRt = null; }
        }

        static void FrameStats(string id, Frame f)
        {
            long rs = 0, gs = 0, bs = 0;
            var seen = new HashSet<int>();
            for (int i = 0; i < f.Px.Length; i++)
            {
                var c = f.Px[i];
                rs += c.r; gs += c.g; bs += c.b;
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
            Debug.Log($"CSFRAME id={id} meanRGB={mr:F1},{mg:F1},{mb:F1} variance={var0:F1} buckets={seen.Count}");
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
                if (type == null) { Debug.Log($"CSTICKER id=missingtype name={tn}"); continue; }
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
            Debug.Log($"CSTICKER id=total n={Tickers.Count}");
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
                    Debug.Log($"CSTICKER id=threw type={t.Name} msg={e.Message}");
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
            string line = $"CSCHECK {key}={(cond ? "PASS" : "FAIL")}";
            if (note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Done()
        {
            Debug.Log($"CITYSIGNS wallSeconds={Wall.Elapsed.TotalSeconds:F1}");
            Debug.Log(_allPass ? "CITYSIGNS RESULT=PASS" : "CITYSIGNS RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }
    }
}
