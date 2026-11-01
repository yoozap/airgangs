using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// The before/after of the CityRenderer batch-key fix and the facade-relief
    /// wiring, measured in ONE run so neither side can drift.
    ///
    /// Editor/Probes/CityDrawProbe.cs (another pass's file, not edited) measured
    /// the defect: 781,149 batches of 1.86 instances, 734 materials, 329 draw
    /// calls submitted at the 31L spawn. This probe builds the kit BOTH ways
    /// against identical data and an identical camera — `forceQuantisedTint:
    /// true` reproduces the old material-per-tint-bucket key, false takes the
    /// new per-instance-tint path — and then adds the facade relief, which had
    /// no renderer at all. Three arms, five stations' worth of numbers, one
    /// Unity launch:
    ///
    ///   CITYKIT_ARM id=quantised   the old key (tint in the batch key)
    ///   CITYKIT_ARM id=instanced   per-instance tint, kit only
    ///   CITYKIT_ARM id=withrelief  per-instance tint + the facade relief
    ///   CITYKIT_FRAME              what Submit() would actually issue, per arm
    ///                              and per station, through the same gate the
    ///                              game uses (draw:false, counting only)
    ///   CITYKIT_LAYERS             relief instances submitted, per layer
    ///   CITYKIT_GATE               the "is it REACHED" assertions (see below)
    ///   CITYKIT_SHADERS            every project shader's compile state
    ///
    /// THE GATE ASSERTIONS exist because .claude/skills/unity/SKILL.md's most
    /// expensive lesson is that a probe proves a CALCULATION, not that the
    /// calculation is reached. Three things here can silently not happen:
    ///   1. Shaders/CityKit.shader fails to compile -> CityRenderer falls back
    ///      to the old quantised path and every number above looks fine. So
    ///      `ResolveReport.InstancedTint` is asserted TRUE on the SHIPPED
    ///      shader asset, not on a fixture.
    ///   2. `Graphics.RenderMeshInstanced` has no generic per-instance-data
    ///      overload on this Unity version -> it would not compile, but the
    ///      overload's resolved signatures are printed so the mechanism is on
    ///      the record. TintPixels() is the PIXEL proof that the tint arrives;
    ///      a successful draw is not one.
    ///   3. The relief items exist but no batch carries them (a kit mesh that
    ///      fails to resolve, an empty variant bucket). So the layer counts are
    ///      read back off the BATCHES, never off `rich.Relief`.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.CityKitInstanceProbe.Run
    /// </summary>
    public static class CityKitInstanceProbe
    {
        struct Arm
        {
            public string Id;
            public List<CityRenderer.Batch> Batches;
            public CityRenderer.ResolveReport Report;
            public CityRenderer Comp;
        }

        /// <summary>
        /// Every shader in Assets/Shaders and whether it COMPILES — the two the
        /// first GPU render of the port found broken (PlaneHull's `1h` half
        /// literal, which turned every aeroplane magenta on its first hit, and
        /// GlassGlint's `float3 * float2`) were both invisible to ~400 probes.
        /// `ShaderUtil.ShaderHasError` is the only headless way to catch this:
        /// a shader that fails to compile is still found by Shader.Find, still
        /// makes a Material, and just draws magenta.
        ///
        /// Split out of Run() so it can be run on its own in seconds — which is
        /// what makes a NEGATIVE CONTROL affordable: drop a deliberately broken
        /// shader into Assets/Shaders, run this, and it must report
        /// withError=1 naming it. Without that control a `withError=0` line
        /// from a -nographics batch run proves nothing, which is this project's
        /// single most expensive documented failure mode.
        ///
        ///   Unity ... -executeMethod TbilisiFly.Probes.CityKitInstanceProbe.Shaders
        /// </summary>
        public static void Shaders()
        {
            ScanShaders();
            ProbeExit.Exit(0);
        }

        static int ScanShaders()
        {
            int shBad = 0;
            var badNames = new List<string>();
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" });
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (sh == null) continue;
                if (ShaderUtil.ShaderHasError(sh)) { shBad++; badNames.Add(System.IO.Path.GetFileName(path)); }
            }
            badNames.Sort();
            Debug.Log($"CITYKIT_SHADERS checked={guids.Length} " +
                      $"withError={shBad} bad={(badNames.Count == 0 ? "none" : string.Join(",", badNames))}");
            return shBad;
        }

        /// <summary>
        /// ISOLATES a throw out of CityRenderer.LateUpdate. RenderShotProbe's
        /// ticker reported `TICKER id=threw type=CityRenderer msg=Exception has
        /// been thrown by the target of an invocation` — a reflected invoke
        /// wraps the real exception, so the message says nothing. This issues
        /// ONE real draw of ONE kit batch (the per-instance-data overload) and
        /// ONE real draw of ONE facade batch (the plain matrix overload) with
        /// the inner exception printed, which is the difference between "the
        /// instancing layout contract is wrong" and "something else in
        /// LateUpdate". Needs a REAL graphics device: run WITHOUT -nographics.
        ///
        ///   Unity -batchmode -quit -projectPath unity \
        ///         -executeMethod TbilisiFly.Probes.CityKitInstanceProbe.Draw
        /// </summary>
        public static void Draw()
        {
            var city = CityData.Load();
            var kitCat = BuildingKit.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, BuildingFacts.Load(), kitCat,
                BuildingParts.Load(), WallMaterials.Catalog.Load());
            var report = new CityRenderer.ResolveReport();
            var kitBatches = CityRenderer.BuildKitBatches(rich, kitCat, report);
            var facadeBatches = CityRenderer.BuildFacadeBatches(rich, report);

            var camGo = new GameObject("DrawProbeCam");
            var cam = camGo.AddComponent<Camera>();
            var ot = GeoFrame.FromLatLon(41.6885, 44.8095);
            camGo.transform.position = new Vector3(ot.x, (city?.Terrain != null ? city.Terrain.HeightAt(ot.x, ot.z) : 0f) + 60f, ot.z);

            void One(string id, CityRenderer.Batch b, bool useTints)
            {
                try
                {
                    int n = Mathf.Min(CityRenderer.MaxInstancesPerCall, b.InstanceCount);
                    MaterialPropertyBlock mpb = null;
                    if (useTints && b.Tints != null)
                    {
                        var arr = new Vector4[n];
                        System.Array.Copy(b.Tints, 0, arr, 0, n);
                        mpb = new MaterialPropertyBlock();
                        mpb.SetVectorArray(CityRenderer.TintProp, arr);
                    }
                    var rp = new RenderParams(b.Materials[0])
                    {
                        worldBounds = b.Bounds,
                        shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On,
                        receiveShadows = true,
                        matProps = mpb,
                    };
                    Graphics.RenderMeshInstanced(rp, b.Mesh, 0, b.Matrices, n, 0);
                    Debug.Log($"CITYKIT_DRAW id={id} ok=true n={n} mesh={b.Mesh.name} sub={b.Mesh.subMeshCount} " +
                              $"mat={b.Materials[0].name} shader={b.Materials[0].shader.name} " +
                              $"instancing={b.Materials[0].enableInstancing.ToString().ToLowerInvariant()}");
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    Debug.Log($"CITYKIT_DRAW id={id} ok=false type={inner.GetType().Name} msg={inner.Message}");
                }
            }

            var kb = kitBatches.FirstOrDefault(b => b.Tints != null && b.Tints.Length > 0);
            if (kb.Mesh != null) One("kit_tintprops", kb, true);
            else Debug.Log("CITYKIT_DRAW id=kit_tintprops ok=false msg=no_batch_with_tints");
            if (kb.Mesh != null) One("kit_matrixonly", kb, false);
            var fb = facadeBatches.FirstOrDefault(b => b.Matrices != null && b.Matrices.Length > 0);
            if (fb.Mesh != null) One("facade_matrixonly", fb, false);
            else Debug.Log("CITYKIT_DRAW id=facade_matrixonly ok=false msg=no_facade_batch");

            // and the whole LateUpdate path, the way RenderShotProbe ticks it
            var go = new GameObject("CityKitDrawProbe");
            var cr = go.AddComponent<CityRenderer>();
            var all = new List<CityRenderer.Batch>(kitBatches);
            all.AddRange(facadeBatches);
            cr.Configure(all);
            try
            {
                cr.TickCulling(camGo.transform.position, 0.016f);
                var (b2, d2) = cr.Submit(cam, draw: true);
                Debug.Log($"CITYKIT_DRAW id=submit_all ok=true batches={b2} draws={d2}");
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Debug.Log($"CITYKIT_DRAW id=submit_all ok=false type={inner.GetType().Name} msg={inner.Message}");
            }
            ProbeExit.Exit(0);
        }

        /// <summary>
        /// PIXEL PROOF that the per-instance tint reaches the shader. The draw
        /// SUCCEEDING proves nothing about the colour: if Unity matched the
        /// trailing instance fields by name and found none, or ignored them, the
        /// kit would simply draw at its material colour with no per-building
        /// variation at all -- a subtle look regression and not an error.
        ///
        /// So: ONE instanced batch of a real kit mesh, three instances, tints
        /// RED / GREEN / BLUE, drawn to a RenderTexture by an orthographic
        /// camera under flat white ambient (no Light object is created -- the
        /// lights-hash shader storm is a documented perf trap in this project),
        /// and the mean colour of the left / middle / right third read back. If
        /// the tint is live the three thirds come back red-dominant,
        /// green-dominant and blue-dominant. If the frame is black the test says
        /// INCONCLUSIVE rather than passing.
        ///
        /// Needs a REAL graphics device: run WITHOUT -nographics.
        /// </summary>
        public static void TintPixels()
        {
            var kitCat = BuildingKit.Catalog.Load();
            var city = CityData.Load();
            var rich = BuildingBuilder.BuildRich(city, BuildingFacts.Load(), kitCat,
                BuildingParts.Load(), WallMaterials.Catalog.Load(), null, 20000);
            var report = new CityRenderer.ResolveReport();
            var batches = CityRenderer.BuildKitBatches(rich, kitCat, report);
            var proto = batches.FirstOrDefault(b => b.Tints != null && b.Tints.Length > 0);
            if (proto.Mesh == null) { Debug.Log("CITYKIT_TINT verdict=INCONCLUSIVE msg=no_tinted_batch"); ProbeExit.Exit(1); return; }

            var mesh = proto.Mesh;
            var mat = proto.Materials[0];
            Bounds lb = mesh.bounds;
            float step = Mathf.Max(lb.size.x, lb.size.z) * 1.6f;

            // Columns 0..2: ONE instanced draw of three instances whose tints are
            // RED / GREEN / BLUE, carried in a property-block vector array —
            // the mechanism under test.
            // Column 3: a CONTROL — one instance drawn with a clone material
            // whose _InstTint uniform is YELLOW and no property block at all.
            // The control separates "the shader never reads _InstTint" from
            // "the per-instance delivery does not arrive", which is the
            // distinction the first run of this probe could not make.
            var tints = new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1), new Vector4(0, 0, 1, 1) };
            var mats3 = new Matrix4x4[3];
            for (int i = 0; i < 3; i++)
                mats3[i] = Matrix4x4.TRS(new Vector3((i - 1.5f) * step, 0f, 0f), Quaternion.identity, Vector3.one);
            var ctrlMat = new Material(mat) { name = mat.name + "_ctrl" };
            ctrlMat.SetColor("_InstTint", new Color(1f, 1f, 0f, 1f));
            ctrlMat.enableInstancing = true;
            var mats1 = new[] { Matrix4x4.TRS(new Vector3(1.5f * step, 0f, 0f), Quaternion.identity, Vector3.one) };

            bool oldFog = RenderSettings.fog;
            var oldAmbMode = RenderSettings.ambientMode;
            var oldAmb = RenderSettings.ambientLight;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;
            RenderSettings.ambientIntensity = 1f;

            var rt = new RenderTexture(256, 64, 24, RenderTextureFormat.ARGB32) { useMipMap = false };
            var camGo = new GameObject("TintProbeCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            // fit four columns across a 4:1 frame AND the mesh's full height
            cam.orthographicSize = Mathf.Max(lb.size.y * 0.6f, (step * 2f + lb.size.x * 0.5f) / 4f);
            cam.transform.position = new Vector3(0f, lb.center.y, -step * 6f);
            cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rt;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = step * 30f;

            var mpb = new MaterialPropertyBlock();
            mpb.SetVectorArray(CityRenderer.TintProp, tints);
            var bounds = new Bounds(Vector3.zero, Vector3.one * step * 10f);
            for (int sm = 0; sm < Mathf.Max(1, mesh.subMeshCount); sm++)
            {
                Graphics.RenderMeshInstanced(new RenderParams(mat)
                {
                    camera = cam, worldBounds = bounds, matProps = mpb,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false,
                }, mesh, sm, mats3, 3, 0);
                Graphics.RenderMeshInstanced(new RenderParams(ctrlMat)
                {
                    camera = cam, worldBounds = bounds,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false,
                }, mesh, sm, mats1, 1, 0);
            }
            cam.Render();

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var px = tex.GetPixels();
            var sum = new Vector3[4];
            var cnt = new int[4];
            int nonBlack = 0;
            for (int y = 0; y < rt.height; y++)
                for (int x = 0; x < rt.width; x++)
                {
                    var c = px[y * rt.width + x];
                    if (c.r + c.g + c.b < 0.02f) continue;
                    nonBlack++;
                    int col = Mathf.Clamp(x * 4 / rt.width, 0, 3);
                    sum[col] += new Vector3(c.r, c.g, c.b);
                    cnt[col]++;
                }

            string Dominant(int i)
            {
                if (cnt[i] < 20) return "empty";
                var m = sum[i] / cnt[i];
                if (m.x > m.y * 1.4f + 0.02f && m.x > m.z * 1.4f + 0.02f) return "red";
                if (m.y > m.x * 1.4f + 0.02f && m.y > m.z * 1.4f + 0.02f) return "green";
                if (m.z > m.x * 1.4f + 0.02f && m.z > m.y * 1.4f + 0.02f) return "blue";
                if (m.x > m.z * 1.4f + 0.02f && m.y > m.z * 1.4f + 0.02f) return "yellow";
                return "neutral";
            }
            var dom = new string[4];
            for (int i = 0; i < 4; i++)
            {
                dom[i] = Dominant(i);
                var m = cnt[i] > 0 ? sum[i] / cnt[i] : Vector3.zero;
                Debug.Log($"CITYKIT_TINT col={i} px={cnt[i]} r={m.x:F3} g={m.y:F3} b={m.z:F3} dominant={dom[i]}");
            }
            string verdict = nonBlack < 60 ? "INCONCLUSIVE"
                : (dom[0] == "red" && dom[1] == "green" && dom[2] == "blue") ? "PASS" : "FAIL";
            var mbc = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            Debug.Log($"CITYKIT_TINT verdict={verdict} control={dom[3]} nonBlackPx={nonBlack} mesh={mesh.name} " +
                      $"sub={mesh.subMeshCount} mat={mat.name} shader={mat.shader.name} " +
                      $"matBaseColor=({mbc.r:F3},{mbc.g:F3},{mbc.b:F3})");

            cam.targetTexture = null;
            RenderSettings.fog = oldFog;
            RenderSettings.ambientMode = oldAmbMode;
            RenderSettings.ambientLight = oldAmb;
            ProbeExit.Exit(verdict == "PASS" ? 0 : 1);
        }

        public static void Run()
        {
            int shBad = ScanShaders();

            // ---- the per-instance-data overload, on the record ----
            var mi = typeof(Graphics).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "RenderMeshInstanced" && m.IsGenericMethodDefinition)
                .ToList();
            foreach (var m in mi)
                Debug.Log($"CITYKIT_OVERLOAD sig=({string.Join(",", m.GetParameters().Select(q => q.ParameterType.Name))})");
            Debug.Log($"CITYKIT_OVERLOAD genericCount={mi.Count} " +
                      $"matrixBytes={System.Runtime.InteropServices.Marshal.SizeOf<Matrix4x4>()}");

            // ---- data, once, shared by every arm ----
            var city = CityData.Load();
            var facts = BuildingFacts.Load();
            var kitCat = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, facts, kitCat, parts, wallCat);
            Debug.Log($"CITYKIT_RELIEF_ITEMS windows={rich.Relief.Windows.Count} balconies={rich.Relief.Balconies.Count} " +
                      $"cornices={rich.Relief.Cornices.Count} grilles={rich.Relief.Grilles.Count} " +
                      $"glassMullions={rich.Relief.GlassMullions.Count} glassBands={rich.Relief.GlassBands.Count}");

            var arms = new List<Arm>();
            foreach (bool quantised in new[] { true, false })
            {
                var report = new CityRenderer.ResolveReport();
                var batches = CityRenderer.BuildKitBatches(rich, kitCat, report, forceQuantisedTint: quantised);
                arms.Add(new Arm { Id = quantised ? "quantised" : "instanced", Batches = batches, Report = report });
            }
            {
                var report = new CityRenderer.ResolveReport();
                var batches = CityRenderer.BuildKitBatches(rich, kitCat, report);
                batches.AddRange(CityRenderer.BuildFacadeBatches(rich, report));
                arms.Add(new Arm { Id = "withrelief", Batches = batches, Report = report });
            }

            for (int i = 0; i < arms.Count; i++)
            {
                var a = arms[i];
                var go = new GameObject("CityKitInstanceProbe_" + a.Id);
                a.Comp = go.AddComponent<CityRenderer>();
                a.Comp.Configure(a.Batches);
                arms[i] = a;

                var meshes = new HashSet<Mesh>();
                var mats = new HashSet<Material>();
                var cells = new HashSet<(int, int)>();
                long instBytes = 0, matBytes = 0;
                int withInstances = 0;
                foreach (var b in a.Batches)
                {
                    if (b.Mesh != null) meshes.Add(b.Mesh);
                    if (b.Materials != null) foreach (var m in b.Materials) if (m != null) mats.Add(m);
                    cells.Add((Mathf.FloorToInt(b.Bounds.center.x / 700f), Mathf.FloorToInt(b.Bounds.center.z / 700f)));
                    matBytes += (long)(b.Matrices?.Length ?? 0) * 64L;
                    if (b.Tints != null) { instBytes += (long)b.Tints.Length * 16L; withInstances++; }
                }
                // Batch is a struct in a List<Batch>: 5 refs + Bounds(24) + 3
                // floats = 88 bytes on a 64-bit runtime, and CityCulling.VisChunk
                // is a CLASS holding a Chunk (two refs + 6 floats + a bool) --
                // ~120 bytes with the object header. The point of the figure is
                // the RATIO between the arms, not the constant.
                long headerBytes = (long)a.Batches.Count * (88L + 120L);
                Debug.Log($"CITYKIT_ARM id={a.Id} batches={a.Batches.Count} submeshInstances={a.Comp.TotalInstances} " +
                          $"drawCallsTotal={a.Comp.TotalDrawCalls} meshes={meshes.Count} materials={mats.Count} " +
                          $"cells700={cells.Count} batchesWithTints={withInstances} " +
                          $"instancedTint={a.Report.InstancedTint.ToString().ToLowerInvariant()}");
                Debug.Log($"CITYKIT_MEM id={a.Id} headerMiB={headerBytes / 1048576.0:F1} " +
                          $"matricesMiB={matBytes / 1048576.0:F1} instanceDataMiB={instBytes / 1048576.0:F1} " +
                          $"totalMiB={(headerBytes + matBytes + instBytes) / 1048576.0:F1}");
            }

            // ---- the relief layers, read back off the BATCHES ----
            var relief = arms[2];
            var layers = relief.Report.FacadeLayers.Select(kv => $"{kv.Key}:{kv.Value}").OrderBy(x => x).ToList();
            Debug.Log($"CITYKIT_LAYERS batches={relief.Report.FacadeBatches} instances={relief.Report.FacadeInstances} " +
                      $"layers={(layers.Count == 0 ? "NONE" : string.Join(" ", layers))}");

            // ---- per-frame submissions, same gate the game uses ----
            var stations = new List<(string name, Vector3 pos)>();
            var ap = AirportBuilder.Load();
            if (ap != null && city?.Terrain != null)
            {
                var spawn = AirportBuilder.Get31LSpawn(ap, city.Terrain);
                stations.Add(("spawn31L", spawn.Pos + new Vector3(0f, 12f, 0f)));
                stations.Add(("spawn_plus800m", spawn.Pos + new Vector3(0f, 812f, 0f)));
            }
            else
            {
                stations.Add(("origin_fallback", new Vector3(0f, 200f, 0f)));
            }
            // Old Town: src/landmarks.js's own historic-district centre
            // (WallMaterials' first `historic` district, 41.6885/44.8095) at
            // rooftop height — the station the first GPU render shot.
            var ot = GeoFrame.FromLatLon(41.6885, 44.8095);
            float otY = city?.Terrain != null ? city.Terrain.HeightAt(ot.x, ot.z) : 0f;
            stations.Add(("oldtown", new Vector3(ot.x, otY + 60f, ot.z)));

            var camGo = new GameObject("CityKitProbeCam");
            var cam = camGo.AddComponent<Camera>();
            foreach (var (name, pos) in stations)
            {
                camGo.transform.position = pos;
                foreach (var a in arms)
                {
                    a.Comp.TickCulling(pos, 0.016f);
                    var (fb, fd) = a.Comp.Submit(cam, draw: false);
                    Debug.Log($"CITYKIT_FRAME id={a.Id} where={name} camX={pos.x:F1} camY={pos.y:F1} camZ={pos.z:F1} " +
                              $"batchesSubmitted={fb} drawCalls={fd} culled={a.Comp.BatchCount - fb}");
                }
            }

            // ---- the gate: is any of this REACHED on the shipped assets ----
            bool gateShader = arms[1].Report.InstancedTint;
            bool gateInstData = arms[1].Batches.Any(b => b.Tints != null && b.Tints.Length > 0);
            bool gateNoTintMats = arms[1].Batches.All(b => b.Materials == null || b.Materials.All(m => m == null || !m.name.Contains("_tint")));
            bool gateRelief = relief.Report.FacadeInstances > 0;
            bool gateShaders = shBad == 0;
            bool pass = gateShader && gateInstData && gateNoTintMats && gateRelief && gateShaders;
            Debug.Log($"CITYKIT_GATE instancedTintTaken={gateShader.ToString().ToLowerInvariant()} " +
                      $"tintArraysBuilt={gateInstData.ToString().ToLowerInvariant()} " +
                      $"noQuantisedMaterialsLeft={gateNoTintMats.ToString().ToLowerInvariant()} " +
                      $"reliefSubmitted={gateRelief.ToString().ToLowerInvariant()} " +
                      $"allShadersCompile={gateShaders.ToString().ToLowerInvariant()} " +
                      $"verdict={(pass ? "PASS" : "FAIL")}");
            ProbeExit.Exit(pass ? 0 : 1);
        }
    }
}
