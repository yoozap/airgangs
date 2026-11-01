using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TbilisiFly.Combat;
using TbilisiFly.EditorTools;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE FLEET'S LIVERIES, AND THE THREE MACHINES THAT COULD NOT TAKE A HIT.
    ///
    /// Two jobs, one editor launch, because the project lock is exclusive:
    ///
    ///   Run()     forces all 68 plane models through
    ///             Editor/PlaneLiveryPostprocessor and reads back, off the
    ///             IMPORTED asset, how many materials each aeroplane has and
    ///             how many of them actually carry an albedo map. One
    ///             `LIVERY id=... textures=N albedo=true|false` line per
    ///             machine. This is the "assert the gate is open on the SHIPPED
    ///             asset" check the skill asks for: a map file full of correct
    ///             paths proves nothing about what Unity imported.
    ///
    ///   Damage()  opens Assets/Scenes/Game.unity, lets ShedSceneFill park the
    ///             fleet, and walks Combat/PlaneDamage.Refine over the SAME
    ///             stations RenderShotProbe's AimAt uses -- printing what each
    ///             station hit instead of only whether one did. That is what
    ///             turns "carpet: no skin under the shell" into a cause.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.PlaneLiveryProbe.Suite
    /// </summary>
    public static class PlaneLiveryProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        static bool _allPass = true;

        public static void Suite()
        {
            ProbeExit.Suppress = true;
            Run();
            Damage();
            int worst = ProbeExit.LastNonZero;
            ProbeExit.Suppress = false;
            Debug.Log($"LIVERYSUITE worstCode={worst}");
            EditorApplication.Exit(worst);
        }

        /// <summary>The verification launch: the edit-mode error count that
        /// Task 2 is measured by, this probe's own damage walk, and the
        /// orientation sweep that must not have moved. One lock, three
        /// verdicts. PlaneOrientProbe.Suite exits the editor itself, so it is
        /// last.</summary>
        public static void Verify()
        {
            ProbeExit.Suppress = true;
            HeroScreenProbe.Run();
            Damage();
            ProbeExit.Suppress = false;
            PlaneOrientProbe.Suite();
        }

        /// <summary>The GPU launch: the project's own pixel probe (planehall.png
        /// and the REALHOLE table) followed by this one's livery A/B, in ONE
        /// editor session. Drop -nographics.</summary>
        public static void GpuSuite()
        {
            ProbeExit.Suppress = true;
            RenderShotProbe.Run();
            Shots();
            int worst = ProbeExit.LastNonZero;
            ProbeExit.Suppress = false;
            Debug.Log($"LIVERYGPUSUITE worstCode={worst}");
            EditorApplication.Exit(worst);
        }

        // =====================================================================
        // 1. the liveries
        // =====================================================================
        public static void Run()
        {
            _allPass = true;

            // The source PNGs and the map are plain assets; make sure the
            // database has them before a model asks for them by path.
            AssetDatabase.ImportAsset(PlaneLiveryPostprocessor.TexRoot.TrimEnd('/'),
                ImportAssetOptions.ImportRecursive);
            int mapped = PlaneLiveryPostprocessor.Map().Count;
            Debug.Log($"LIVERY id=map planes={mapped} " +
                      $"exists={File.Exists(PlaneLiveryPostprocessor.MapPath).ToString().ToLowerInvariant()}");
            Check("liveryMapLoaded", mapped > 0, $"{PlaneLiveryPostprocessor.MapPath}");

            // Every model the postprocessor handles, forced through it. Plain
            // ForceUpdate rather than an AssetPostprocessor version bump, which
            // would reimport all 232 MB of art in the project.
            var models = new List<string>();
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Resources/Art/Planes" }))
                models.Add(AssetDatabase.GUIDToAssetPath(g));
            models.Add(PlaneLiveryPostprocessor.SpitfireObj);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in models.Distinct())
                    AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();
            Debug.Log($"LIVERY id=reimport models={models.Distinct().Count()}");

            int textured = 0, white = 0, totalTex = 0;
            foreach (var def in Planes.All)
            {
                string path = def.Id == "spitfire"
                    ? PlaneLiveryPostprocessor.SpitfireObj
                    : "Assets/Resources/Art/Planes/" + def.Id + ".fbx";
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                {
                    Debug.Log($"LIVERY id={def.Id} textures=0 albedo=false note=no_asset");
                    white++; _allPass = false;
                    continue;
                }

                var mats = new HashSet<Material>();
                foreach (var r in asset.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null) mats.Add(m);

                var tex = new HashSet<Texture>();
                int alb = 0, nrm = 0, mg = 0, emi = 0, clear = 0, coloured = 0;
                foreach (var m in mats)
                {
                    Texture T(string p) => m.HasProperty(p) ? m.GetTexture(p) : null;
                    var a = T("_BaseMap") ?? T("_MainTex");
                    if (a != null) { alb++; tex.Add(a); }
                    var n = T("_BumpMap"); if (n != null) { nrm++; tex.Add(n); }
                    var g = T("_MetallicGlossMap"); if (g != null) { mg++; tex.Add(g); }
                    var e = T("_EmissionMap"); if (e != null) { emi++; tex.Add(e); }
                    if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) clear++;
                    // "not white": a base colour that is not within a hair of
                    // (1,1,1). The untextured airframes (f104, kawasaki, rq180)
                    // have nothing else, so this is their whole livery.
                    if (m.HasProperty("_BaseColor"))
                    {
                        var c = m.GetVector("_BaseColor");
                        if (Mathf.Abs(c.x - 1f) + Mathf.Abs(c.y - 1f) + Mathf.Abs(c.z - 1f) > 0.03f) coloured++;
                    }
                }
                bool ok = alb > 0 || coloured > 0;
                if (ok) textured++; else white++;
                totalTex += tex.Count;
                Debug.Log($"LIVERY id={def.Id} materials={mats.Count} textures={tex.Count} " +
                          $"albedo={(alb > 0).ToString().ToLowerInvariant()} albedoMats={alb} " +
                          $"normal={nrm} metalRough={mg} emissive={emi} transparent={clear} " +
                          $"tintedMats={coloured} ok={ok.ToString().ToLowerInvariant()}");
            }

            Debug.Log($"LIVERY id=total planes={Planes.All.Length} coloured={textured} white={white} textures={totalTex}");
            Check("everyPlaneHasALivery", white == 0, $"white={white}");
            Debug.Log(_allPass ? "PLANELIVERY RESULT=PASS" : "PLANELIVERY RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        // =====================================================================
        // 2. the three machines REALHOLE could not mark
        // =====================================================================
        /// <summary>Walks the same stations RenderShotProbe.AimAt walks, but
        /// prints the per-station verdict and the part list, which is what a
        /// bare "skip=no_skin_under_the_shell" cannot give.</summary>
        public static void Damage()
        {
            bool pass = true;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.Log("PLANEHIT RESULT=FAIL (no scene)"); ProbeExit.Exit(1); return; }
            var fleet = GameObject.Find("planehall_fleet");
            if (fleet == null) { Debug.Log("PLANEHIT RESULT=FAIL (no planehall_fleet)"); ProbeExit.Exit(1); return; }

            var pd = new PlaneDamage();
            int hitOk = 0, hitFail = 0;
            for (int i = 0; i < fleet.transform.childCount; i++)
            {
                var pt = fleet.transform.GetChild(i);
                string pid = pt.gameObject.name;
                var rec = pt.GetComponent<PlaneDamageRecord>();
                var box = pt.GetComponent<BoxCollider>();
                if (rec == null || box == null)
                {
                    Debug.Log($"PLANEHIT id={pid} skip=no_record_or_box"); hitFail++; pass = false; continue;
                }
                pd.Watch(rec);

                var bb = WorldBounds(pt.gameObject);
                if (!bb.HasValue) { Debug.Log($"PLANEHIT id={pid} skip=no_renderers"); hitFail++; pass = false; continue; }

                // THE TWO BOXES THAT MUST AGREE. `bb` is the union of
                // Renderer.bounds (what the probe aims by, and what Unity
                // culls by); the BoxCollider was sized from
                // PlaneCockpitMeasurement.BoxOf, a vertex-precise walk with
                // skinned meshes BAKED. A SkinnedMeshRenderer's bounds come
                // from its import-time localBounds through its root bone and
                // are a different number entirely when the two disagree -- so
                // print both rather than guess which lies.
                Bounds bw = new Bounds(pt.TransformPoint(box.center),
                    Vector3.Scale(box.size, AbsScale(pt.lossyScale)));
                Debug.Log($"PLANEBOX id={pid} rbounds_c={bb.Value.center.ToString("F2")} " +
                          $"rbounds_s={bb.Value.size.ToString("F2")} " +
                          $"box_c={bw.center.ToString("F2")} box_s={bw.size.ToString("F2")} " +
                          $"vert_s={VertexBounds(pt).size.ToString("F2")}");

                // the part list, the way PlaneDamage classified it
                int skin = 0, glass = 0, spin = 0, noMesh = 0, renderers = 0, skinned = 0;
                foreach (var r in pt.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    if (r.name == "plane_shell" || r.name.StartsWith("cockpit_")) continue;
                    renderers++;
                    if (r is SkinnedMeshRenderer) skinned++;
                    var mf = r.GetComponent<MeshFilter>();
                    if (!(r is SkinnedMeshRenderer) && (mf == null || mf.sharedMesh == null)) noMesh++;
                }

                int stationsTried = 0, boxHits = 0, refined = 0, occluded = 0;
                string firstPart = null;
                RaycastHit firstHit = default; bool haveFirst = false;
                foreach (var s in Stations(bb.Value, pt))
                {
                    stationsTried++;
                    var ray = new Ray(new Vector3(s.x, bb.Value.max.y + 40f, s.z), Vector3.down);
                    if (!box.Raycast(ray, out RaycastHit crude, 300f)) continue;
                    boxHits++;
                    var r = pd.Refine(crude, Vector3.down);
                    if (r == null) continue;
                    if (r.Value.collider == box) continue;
                    refined++;
                    if (firstPart == null) { firstPart = r.Value.collider.name; firstHit = r.Value; haveFirst = true; }
                    // IS THE HIT PANEL THE ONE A CAMERA ABOVE WOULD SEE? That
                    // is what REALHOLE's changed=0 is really asking: a hole
                    // punched in a panel with another panel over it changes no
                    // pixel. Shoot a second ray from just above the hit point.
                    // THE PROBE CAMERA'S OWN QUESTION, asked exactly the way
                    // RenderShotProbe.HolePixelProof asks it: it puts the eye
                    // at hit + (normal + up*1.5).normalized * 1.9 and looks
                    // back. If anything of this aeroplane -- INCLUDING THE HIT
                    // PANEL ITSELF -- is between the eye and the hit point,
                    // the camera is inside the airframe and a decal on the far
                    // face of the skin it is looking through cannot change a
                    // pixel. The hit collider must be in this test: Unity mesh
                    // raycasts ignore backfaces, so a downward ray passes
                    // straight through a top skin whose winding faces away and
                    // lands on the INSIDE of the same mesh further down.
                    Vector3 p = r.Value.point;
                    Vector3 n2 = r.Value.normal;
                    if (Vector3.Dot(n2, Vector3.up) < 0f) n2 = -n2;
                    Vector3 eye2 = p + (n2 + Vector3.up * 1.5f).normalized * 1.9f;
                    Vector3 dir2 = (p - eye2).normalized;
                    float want = Vector3.Distance(eye2, p);
                    bool covered = false;
                    foreach (var other in pt.GetComponentsInChildren<MeshCollider>(true))
                    {
                        // The hit panel is excluded: a ray aimed AT a surface
                        // hits that surface, so including it makes every
                        // aeroplane read as occluded. That also means this
                        // counter cannot see the case where a panel hides its
                        // OWN far face, which is what h6k and carpet look like.
                        if (other == null || other == r.Value.collider) continue;
                        if (other.Raycast(new Ray(eye2, dir2), out RaycastHit oh, want - 0.02f))
                        { covered = true; break; }
                    }
                    if (covered) occluded++;
                }
                SkinMatrix(pid, pt);
                if (haveFirst && (pid == "h6k" || pid == "carpet" || pid == "a7" || pid == "b737"))
                    MarkDump(pd, rec, pt, pid, firstHit);
                var parts = PartsCount(pd, rec, out skin, out glass, out spin);
                bool ok = refined > occluded;
                if (ok) hitOk++; else { hitFail++; pass = false; }
                Debug.Log($"PLANEHIT id={pid} renderers={renderers} skinnedRenderers={skinned} noMesh={noMesh} " +
                          $"parts={parts} skin={skin} glass={glass} spin={spin} " +
                          $"stations={stationsTried} boxHits={boxHits} refined={refined} occluded={occluded} " +
                          $"part={firstPart ?? "null"} ok={ok.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"PLANEHIT id=total ok={hitOk} fail={hitFail}");
            pd.Dispose();
            Debug.Log(pass ? "PLANEHIT RESULT=PASS" : "PLANEHIT RESULT=FAIL");
            ProbeExit.Exit(pass ? 0 : 1);
        }

        static int PartsCount(PlaneDamage pd, PlaneDamageRecord rec, out int skin, out int glass, out int spin)
        {
            skin = glass = spin = 0;
            var cache = rec.PartsCache;
            if (cache == null) return 0;
            skin = cache.Skin.Count; glass = cache.Glass.Count;
            foreach (var p in cache.All) if (p.Spin) spin++;
            return cache.All.Count;
        }

        /// <summary>RenderShotProbe.AimAt's own station list, verbatim.</summary>
        static IEnumerable<Vector3> Stations(Bounds bb, Transform pt)
        {
            bool wide = bb.extents.x >= bb.extents.z;
            float[] fr = { 0.40f, -0.40f, 0.25f, -0.25f, 0.55f, -0.55f, 0.10f, -0.10f, 0f };
            foreach (float f in fr)
                yield return new Vector3(bb.center.x + (wide ? bb.extents.x * f : 0f), 0f,
                                         bb.center.z + (wide ? 0f : bb.extents.z * f));
            var big = new List<Renderer>();
            foreach (var r in pt.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (r.name == "plane_shell" || r.name.StartsWith("cockpit_")) continue;
                if (r.bounds.size.magnitude > 1.2f) big.Add(r);
            }
            big.Sort((a, b) => b.bounds.size.sqrMagnitude.CompareTo(a.bounds.size.sqrMagnitude));
            for (int i = 0; i < big.Count && i < 8; i++)
                yield return new Vector3(big[i].bounds.center.x, 0f, big[i].bounds.center.z);
        }

        // =====================================================================
        // 3. the pixels — an A/B on the SAME frame
        // =====================================================================
        /// <summary>THE LIVERY, PROVEN IN PIXELS. Needs a GPU, so run it
        /// WITHOUT -nographics:
        ///
        ///   Unity -batchmode -quit -projectPath unity \
        ///         -executeMethod TbilisiFly.Probes.PlaneLiveryProbe.Shots
        ///
        /// A count of assigned textures is a calculation; this renders each
        /// parked machine twice from the same camera — once as imported, once
        /// with every `_BaseMap` detached and `_BaseColor` forced white, which
        /// is exactly the state the whole fleet was in — and counts CHROMATIC
        /// pixels (max channel minus min channel above a threshold). Grey and
        /// white have none by definition, so the "before" number is the
        /// untextured fleet's own score.</summary>
        public static void Shots()
        {
            bool pass = true;
            string outDir = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(outDir))
                outDir = "/private/tmp/claude-501/-Users-wazzap-tbilisifly/" +
                         "0f5488ea-9a85-4533-ad67-e5fe68c33649/scratchpad/shots";
            Directory.CreateDirectory(Path.Combine(outDir, "livery"));
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            { Debug.Log("LIVERYSHOT id=nogpu"); ProbeExit.Exit(1); return; }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.Log("LIVERYSHOT RESULT=FAIL (no scene)"); ProbeExit.Exit(1); return; }
            var fleet = GameObject.Find("planehall_fleet");
            var cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (fleet == null || cam == null)
            { Debug.Log("LIVERYSHOT RESULT=FAIL (no fleet or camera)"); ProbeExit.Exit(1); return; }
            var env = UnityEngine.Object.FindFirstObjectByType<TbilisiFly.Core.SceneEnvironmentSetup>();
            if (env != null) { env.Cam = cam; env.Setup(); }

            int better = 0, worse = 0;
            for (int i = 0; i < fleet.transform.childCount; i++)
            {
                var pt = fleet.transform.GetChild(i);
                string pid = pt.gameObject.name;
                var bb = WorldBounds(pt.gameObject);
                if (!bb.HasValue) continue;
                Vector3 c = bb.Value.center;
                // CLAMPED. Framing the whole aeroplane put the camera 100 m
                // from the An-225 -- outside the hall, inside a wall, with the
                // machine not in frame at all (movedPx=0 for exactly the three
                // biggest: an225, b767, awacs). A close-up of part of a big
                // airframe is a better A/B than a wall.
                float d = Mathf.Clamp(bb.Value.size.magnitude * 0.75f, 6f, 26f);
                Vector3 eye = c + new Vector3(0.55f, 0.42f, 0.72f).normalized * d;

                var afterPx = Render(cam, eye, c, 480, 360);
                int after = Chromatic(afterPx, outDir, "livery/" + pid + "_livery.png");

                // the SAME frame with the liveries taken off
                var saved = new List<(Material m, Texture t, Vector4 col)>();
                foreach (var r in pt.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || !m.HasProperty("_BaseMap")) continue;
                        saved.Add((m, m.GetTexture("_BaseMap"),
                                   m.HasProperty("_BaseColor") ? m.GetVector("_BaseColor") : Vector4.one));
                        m.SetTexture("_BaseMap", null);
                        if (m.HasProperty("_BaseColor")) m.SetVector("_BaseColor", Vector4.one);
                    }
                var beforePx = Render(cam, eye, c, 480, 360);
                int before = Chromatic(beforePx, outDir, "livery/" + pid + "_white.png");
                // The decisive number is not the chroma (a hangar's sky, floor
                // and neighbours are coloured in BOTH frames, which swamps a
                // grey jet): it is how many pixels the albedo maps CHANGE.
                int moved = 0;
                for (int k = 0; k < afterPx.Length && k < beforePx.Length; k++)
                {
                    var x = afterPx[k]; var y = beforePx[k];
                    if (Mathf.Abs(x.r - y.r) + Mathf.Abs(x.g - y.g) + Mathf.Abs(x.b - y.b) > 8) moved++;
                }
                foreach (var (m, t, col) in saved)
                {
                    m.SetTexture("_BaseMap", t);
                    if (m.HasProperty("_BaseColor")) m.SetVector("_BaseColor", col);
                }

                bool ok = moved >= 300;
                if (ok) better++; else { worse++; pass = false; }
                Debug.Log($"LIVERYPIX id={pid} movedPx={moved} colouredAfter={after} colouredBefore={before} " +
                          $"ok={ok.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"LIVERYPIX id=total better={better} same={worse}");
            Debug.Log(pass ? "LIVERYSHOT RESULT=PASS" : "LIVERYSHOT RESULT=FAIL");
            ProbeExit.Exit(pass ? 0 : 1);
        }

        static Color32[] Render(Camera cam, Vector3 pos, Vector3 look, int w, int h)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { antiAliasing = 1 };
            cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((look - pos).normalized, Vector3.up));
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.Render();
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            RenderTexture.active = active;
            cam.targetTexture = prev;
            _lastPng = tex.EncodeToPNG();
            var px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            return px;
        }

        static byte[] _lastPng;

        static int Chromatic(Color32[] px, string outDir, string rel)
        {
            if (_lastPng != null) File.WriteAllBytes(Path.Combine(outDir, rel), _lastPng);
            int n = 0;
            foreach (var c in px)
            {
                int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                int min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                if (max - min > 14) n++;
            }
            return n;
        }

        /// <summary>WHERE DOES THE MARK ACTUALLY GO? REALHOLE reports
        /// `holesDrawn` climbing by one for h6k and carpet while not a single
        /// pixel of a 512x512 frame centred on the hit point changes, which can
        /// only mean the mark is drawn somewhere else. This traces a real round
        /// and prints each live mark's pose against the hit point, in both
        /// candidate frames, with two aeroplanes that DO pass as the control.
        /// </summary>
        static void MarkDump(PlaneDamage pd, PlaneDamageRecord rec, Transform pt, string pid, RaycastHit hit)
        {
            string surf = pd.Trace(hit, hit.point, hit.normal, Vector3.down, Cartridges.Para9x19);
            pd.Update(0.016f);
            Debug.Log($"MARKDUMP id={pid} surface={surf ?? "null"} hit={hit.point.ToString("F3")} " +
                      $"n={hit.normal.ToString("F3")} planePos={pt.position.ToString("F2")} " +
                      $"planeScale={pt.lossyScale.ToString("F3")} tears={CountTearsOf(rec)}");
            DumpLayer(pid, "holes", pd.Holes, pt, hit.point);
            DumpLayer(pid, "tears", pd.Tears, pt, hit.point);
            DumpLayer(pid, "exits", pd.Exits, pt, hit.point);
        }

        static int CountTearsOf(PlaneDamageRecord rec)
        {
            int n = 0;
            foreach (var kv in rec.Tears) n += kv.Value != null ? kv.Value.Count : 0;
            return n;
        }

        static void DumpLayer(string pid, string name, PlaneMarkLayer layer, Transform pt, Vector3 hit)
        {
            if (layer == null) return;
            for (int i = 0; i < layer.Max && i < 64; i++)
            {
                if (!layer.Live[i]) continue;
                var m = layer.Pose[i];
                Vector3 raw = m.GetColumn(3);
                Vector3 viaPlane = pt.localToWorldMatrix.MultiplyPoint3x4(raw);
                Vector3 scale = new Vector3(
                    ((Vector3)m.GetColumn(0)).magnitude,
                    ((Vector3)m.GetColumn(1)).magnitude,
                    ((Vector3)m.GetColumn(2)).magnitude);
                Debug.Log($"MARKPOSE id={pid} layer={name} i={i} raw={raw.ToString("F3")} " +
                          $"dRaw={Vector3.Distance(raw, hit):F3} viaPlane={viaPlane.ToString("F3")} " +
                          $"dViaPlane={Vector3.Distance(viaPlane, hit):F3} size={scale.ToString("F4")}");
            }
        }

        static Vector3 AbsScale(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));

        /// <summary>WHICH MATRIX PUTS A BAKED SKINNED MESH IN THE WORLD. Unity's
        /// own `Renderer.bounds` is the control -- it is what the camera culls
        /// and draws by, so it is the size the aeroplane visibly is. Everything
        /// else here is a candidate for the matrix that
        /// PlaneCockpitMeasurement.WorldVertices should multiply BakeMesh's
        /// output by. One line per skinned renderer; nothing is printed for the
        /// 33 aeroplanes that have none.</summary>
        static void SkinMatrix(string pid, Transform pt)
        {
            foreach (var smr in pt.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh(); smr.BakeMesh(baked);
                var bakedScaled = new Mesh(); smr.BakeMesh(bakedScaled, true);
                var root = smr.rootBone != null ? smr.rootBone : smr.transform;
                Debug.Log($"SKINMX id={pid} r={smr.name} " +
                          $"unity={smr.bounds.size.ToString("F3")} " +
                          $"localB={smr.localBounds.size.ToString("F3")} " +
                          $"bakedLocal={Size(baked, Matrix4x4.identity).ToString("F3")} " +
                          $"viaSmr={Size(baked, smr.transform.localToWorldMatrix).ToString("F3")} " +
                          $"viaRoot={Size(baked, root.localToWorldMatrix).ToString("F3")} " +
                          $"viaSmrScaled={Size(bakedScaled, smr.transform.localToWorldMatrix).ToString("F3")} " +
                          $"smrScale={smr.transform.lossyScale.ToString("F4")} " +
                          $"rootBone={(smr.rootBone != null ? smr.rootBone.name : "null")} " +
                          $"rootScale={root.lossyScale.ToString("F4")}");
                UnityEngine.Object.DestroyImmediate(baked);
                UnityEngine.Object.DestroyImmediate(bakedScaled);
            }
        }

        static Vector3 Size(Mesh m, Matrix4x4 mx)
        {
            var v = m.vertices;
            if (v.Length == 0) return Vector3.zero;
            var b = new Bounds(mx.MultiplyPoint3x4(v[0]), Vector3.zero);
            for (int i = 1; i < v.Length; i++) b.Encapsulate(mx.MultiplyPoint3x4(v[i]));
            return b.size;
        }

        /// <summary>The world AABB of the POSED vertices — the ground truth the
        /// BoxCollider was built from.</summary>
        static Bounds VertexBounds(Transform pt)
        {
            Bounds b = default; bool any = false;
            foreach (var r in pt.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r.name == "plane_shell" || r.name.StartsWith("cockpit_")) continue;
                Mesh mesh = null; Matrix4x4 m = r.transform.localToWorldMatrix; bool temp = false;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                { mesh = new Mesh(); smr.BakeMesh(mesh, true); temp = true; }  // useScale — see PlaneCockpitMeasurement
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;
                foreach (var v in mesh.vertices)
                {
                    var w = m.MultiplyPoint3x4(v);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
                if (temp) UnityEngine.Object.DestroyImmediate(mesh);
            }
            return b;
        }

        static Bounds? WorldBounds(GameObject go)
        {
            Bounds b = default; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r.name == "plane_shell" || r.name.StartsWith("cockpit_")) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b : (Bounds?)null;
        }

        static void Check(string key, bool cond, string note = null)
        {
            Debug.Log($"LIVERYCHECK {key}={(cond ? "PASS" : "FAIL")}" + (note != null ? $" ({note})" : ""));
            if (!cond) _allPass = false;
        }
    }
}
