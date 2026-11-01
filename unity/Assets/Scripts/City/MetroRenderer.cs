using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The RENDERED half of MetroBuilder.cs — src/metro.js's buildKit()/
    /// addMetro() (metro.js:153-306). MetroBuilder only DECIDES positions,
    /// yaws and kit "Kind"s; until this file nothing in the project ever
    /// Instantiate()'d a single pavilion (docs/QUALITY_BAR.md "THE BIGGEST
    /// REAL GAP").
    ///
    /// ASSET BRIDGE CLOSED HERE: MetroBuilder.cs's own class doc flagged a
    /// real gap — the pavilion GLBs (models_raw/metro/*.glb) had never been
    /// converted to FBX/imported, unlike malls.fbx and the house/apt/shed
    /// kits. This pass ran the project's existing, already-proven bridge
    /// tool (unchanged):
    ///     blender -b --python tools/glb_batch_fbx.py -- models_raw/metro unity/Assets/Resources/Art/Metro
    /// producing 10 FBX files at Assets/Resources/Art/Metro/*.fbx (avlabari, entrance,
    /// galleria, liberty[unused — metro.js never references it], marjanishvili,
    /// pavilion_arch, pavilion_hall, rustaveli, station_square, totem). A
    /// Blender dry-import of each source GLB (not guessed, read back) confirmed
    /// every mesh child name inside survives the round trip unchanged and
    /// matches metro.js's MAT dictionary keys exactly:
    ///   pavilion_arch  {fascia,frame,glass,sign,steps,stone,stone2}
    ///   pavilion_hall  {concrete,fascia,frame,frame2,glass,sign,steps}
    ///   entrance       {concrete,frame,glass,steps}
    ///   totem          {pylon,sign}
    ///   galleria       {concrete,concrete2,fascia,frame,glass,roof,sign,steps}
    ///   rustaveli      {concrete,fascia,frame,glass,hedge,relief,sign,steps,stone}
    ///   marjanishvili  {ashlar,cream,fascia,frame,glass,steps}
    ///   avlabari       {cream,cream.001,fascia,frame,glass,hedge,sign,steps}
    ///   station_square {clock,concrete,fascia,glass,sign,winband}
    ///
    /// SIMPLIFICATION vs metro.js, reported not hidden: metro.js:176-190
    /// overlays a SEPARATE textured plane in front of the "sign"/"fascia"
    /// mesh's local bounding box (dodging a box-UV rotation quirk in the
    /// GLB). This port instead assigns the generated texture DIRECTLY as
    /// that submesh's own material — identical visible content (the M
    /// roundel / plate background), no bounding-box-in-FBX-local-space
    /// guess, one fewer moving part. The Georgian/English STATION NAME TEXT
    /// itself is MetroSignage.cs's own pre-existing, documented gap (no
    /// Mkhedruli TMP_FontAsset anywhere in the project — confirmed again
    /// here, unchanged) and is not re-solved by this file. The Rustaveli
    /// pavilion's bespoke italic caption (metro.js:240-253) gets the same
    /// blank-plate stand-in as every other station's fascia for the same
    /// reason; its REAL photo textures (the carved-relief mural and the
    /// red-M glass doors) ARE used, because those assets already exist at
    /// Assets/Resources/Art/Textures/facades/rustaveli_{relief,doors}.jpg.
    ///
    /// HANDEDNESS: every <see cref="MetroBuilder.Placement"/> handed in is
    /// already fully resolved — GeoFrame.ToUnity position, GeoFrame.
    /// YawToUnity yaw, including the 23 hand-tuned metro.json yaws and the 5
    /// hero yaws (MetroBuilder.cs's own class doc names this exact file in
    /// GeoFrame.cs's "27 hand-tuned constants" list). This file only ever
    /// consumes those two already-negated numbers as a rigid TRS; it never
    /// re-derives or re-negates anything itself. The kit MESHES need no
    /// GeoFrame.FlipWinding call here: they are a static FBX asset,
    /// self-corrected by the Blender GLB-&gt;FBX bridge exactly as
    /// MallPlacer.cs's own doc describes for malls.fbx — FlipWinding is for
    /// geometry BUILT IN C# FROM game-frame data, which this file never does
    /// (the one mesh it does build, the free-standing name board, is a plain
    /// centered quad placed by the same already-negated TRS).
    ///
    /// NO INSTANCING. 23 stations resolve to roughly 150 Placements total
    /// (see probe output) — two orders of magnitude under where Graphics.
    /// RenderMeshInstanced/BatchRendererGroup pays for itself, and each
    /// placement is a multi-submesh KIT object, not one shared mesh (the
    /// thing instancing batches on). Plain Instantiate — one GameObject per
    /// placement — is simpler, correct, and exactly the allowance docs/
    /// QUALITY_BAR.md's brief gives: "23 stations... small enough that
    /// individual objects may be fine."
    ///
    /// RUNTIME CITY GENERATION: this component never bakes anything to disk.
    /// <see cref="EnsureBuilt"/> re-reads metro.json and re-resolves the kit
    /// FBX every time the scene loads (mirrors CityRenderer.EnsureBuilt's own
    /// shape exactly), so a changed metro.json yaw can never silently drift
    /// out of sync with what is on screen.
    /// </summary>
    [ExecuteAlways]
    public class MetroRenderer : MonoBehaviour
    {
        List<GameObject> _spawned;
        public int SpawnedCount => _spawned?.Count ?? 0;
        public int ResolvedCount { get; private set; }
        public int FallbackCount { get; private set; }
        public readonly List<string> MissingKinds = new List<string>();
        public MetroBuilder Builder { get; private set; }

        void OnEnable() => EnsureBuilt();

        /// <summary>
        /// REGRESSION HARNESS for the double-draw defect (docs/port_units/
        /// cityfix.json; Editor/SceneryRebuildCheck.cs is the only caller).
        /// Puts this component back into the state a SCENE LOAD hands it —
        /// build guard cleared — and runs
        /// EnsureBuilt again. Any generated Transform children are left in
        /// place on purpose: that is exactly the condition a load of a scene
        /// with baked children creates, and the condition under which a clear
        /// that trusts a private list (or that does not exist) builds a second
        /// co-located copy of everything. With the clear walking real children,
        /// the child count after this call must EQUAL the count before it.
        /// </summary>
        internal void RebuildForCheck()
        {
            _spawned = null;
            EnsureBuilt();
        }

        /// <summary>Regenerate from the live data pipeline if nothing is built
        /// yet. Safe to call repeatedly (no-ops once built) — same contract as
        /// CityRenderer.EnsureBuilt.</summary>
        public void EnsureBuilt()
        {
            if (_spawned != null) return;
            // FIXED 2026-10-30 (docs/port_units/cityfix.json): this renderer had
            // NO clear at all. _spawned is a private field, so a scene load
            // leaves it null -> this guard passes -> BuildInto adds a full
            // second set of station pavilions on top of any that the scene file
            // had baked under this transform, co-located and double-drawn.
            // Walking real Transform children (ShopfrontRenderer's pattern) is
            // the only clear that can see them.
            //
            // disposeRuntimeAssets: FALSE, deliberately. Every Material this
            // renderer puts on a child comes from the static _matCache, and the
            // name-board Mesh from the static _nameBoardMesh — built once per
            // domain and shared across builds, so there is nothing per-build to
            // reclaim here and destroying them would hand the next build dead
            // references. (Both caches are now destroyed-object-tolerant anyway,
            // see Flat()/NameBoardMesh(), so a future caller cannot create that
            // bug by flipping this flag.)
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _spawned = new List<GameObject>();
            var city = CityData.Load();
            Builder = MetroBuilder.Build(terrain: city?.Terrain);
            BuildInto(Builder, transform, _spawned, out var resolved, out var fallback, MissingKinds);
            ResolvedCount = resolved;
            FallbackCount = fallback;
            Debug.Log($"METRORENDERER stations={Builder.StationCount} entrances={Builder.EntranceCount} " +
                      $"totems={Builder.TotemCount} portalOnly={Builder.PortalOnlyCount} " +
                      $"placements={Builder.Placements.Count} spawned={_spawned.Count} " +
                      $"resolved={resolved} fallback={fallback} {ArtAssets.Tag}");
        }

        /// <summary>Scene-free entry point: builds straight off an already-
        /// computed <see cref="CityLoader.Context"/> (e.g. the Editor bake, or
        /// a probe with no live scene/MonoBehaviour). Returns null off-Editor
        /// or with no ctx.Metro.</summary>
        public static GameObject Render(CityLoader.Context ctx, Transform parent = null)
        {
            var mb = ctx?.Metro;
            if (mb == null) return null;
            var root = parent != null ? parent : new GameObject("MetroStations").transform;
            var spawned = new List<GameObject>();
            BuildInto(mb, root, spawned, out _, out _, new List<string>());
            return root.gameObject;
        }

        public const string ArtRoot = "Assets/Resources/Art/Metro";

        static readonly Dictionary<string, string> KindFile = new Dictionary<string, string>
        {
            { "pavilion_arch", "pavilion_arch" }, { "pavilion_hall", "pavilion_hall" },
            { "entrance", "entrance" }, { "totem", "totem" }, { "galleria", "galleria" },
            { "rustaveli", "rustaveli" }, { "marjanishvili", "marjanishvili" },
            { "avlabari", "avlabari" }, { "station_square", "station_square" },
        };

        static Dictionary<string, GameObject> _prefabCache;
        static Dictionary<string, Material> _matCache;

        static GameObject LoadPrefab(string kind)
        {
            _prefabCache ??= new Dictionary<string, GameObject>();
            if (_prefabCache.TryGetValue(kind, out var hit)) return hit;
            GameObject go = null;
            if (KindFile.TryGetValue(kind, out var file))
                go = ArtAssets.Load<GameObject>($"{ArtRoot}/{file}.fbx");
            _prefabCache[kind] = go;
            return go;
        }

        static Color HexColor(string hex)
        {
            uint v = Convert.ToUInt32(hex, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        static Material Flat(string key, Color c, float metallic = 0f, float smoothness = 0.15f)
        {
            _matCache ??= new Dictionary<string, Material>();
            // `&& hit != null`: Unity's == operator reports a DESTROYED object as
            // null, and a static cache that survives a domain-local rebuild must
            // not hand back a corpse. Without it, any future caller that lets
            // GeneratedChildren dispose this renderer's materials would get
            // invisible/pink stations with no error (ParkCourtTextures._cache
            // already guards this way).
            if (_matCache.TryGetValue(key, out var hit) && hit != null) return hit;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = key };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); else m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            _matCache[key] = m;
            return m;
        }

        static Material TexMat(string key, Texture2D tex)
        {
            _matCache ??= new Dictionary<string, Material>();
            // `&& hit != null`: Unity's == operator reports a DESTROYED object as
            // null, and a static cache that survives a domain-local rebuild must
            // not hand back a corpse. Without it, any future caller that lets
            // GeneratedChildren dispose this renderer's materials would get
            // invisible/pink stations with no error (ParkCourtTextures._cache
            // already guards this way).
            if (_matCache.TryGetValue(key, out var hit) && hit != null) return hit;
            var m = MetroSignage.BuildMaterial(tex);
            m.name = key;
            _matCache[key] = m;
            return m;
        }

        static Material PhotoMat(string key, string assetPath, Color fallbackTint)
        {
            _matCache ??= new Dictionary<string, Material>();
            // `&& hit != null`: Unity's == operator reports a DESTROYED object as
            // null, and a static cache that survives a domain-local rebuild must
            // not hand back a corpse. Without it, any future caller that lets
            // GeneratedChildren dispose this renderer's materials would get
            // invisible/pink stations with no error (ParkCourtTextures._cache
            // already guards this way).
            if (_matCache.TryGetValue(key, out var hit) && hit != null) return hit;
            var tex = ArtAssets.Load<Texture2D>(assetPath);
            Material m;
            if (tex != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                m = new Material(shader) { name = key };
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            }
            else
            {
                m = Flat(key, fallbackTint);
            }
            _matCache[key] = m;
            return m;
        }

        // src/metro.js:10-30 MAT table — exact hex values.
        static Material Stone() => Flat("metro_stone", HexColor("d7c7a4"));
        static Material Concrete() => Flat("metro_concrete", HexColor("d0ccc2"));
        static Material Glass() => Flat("metro_glass", HexColor("41606f"), 0.45f, 0.8f); // metalness .45, roughness .2 -> smoothness .8
        static Material Frame() => Flat("metro_frame", HexColor("3a3f45"));
        static Material Steps() => Flat("metro_steps", HexColor("8f8b84"));
        static Material Pylon() => Flat("metro_pylon", HexColor("2e3338"));
        static Material Cream() => Flat("metro_cream", HexColor("d8ccb2"));
        static Material Ashlar() => Flat("metro_ashlar", HexColor("a9a196"));
        static Material Winband() => Flat("metro_winband", HexColor("2b3138"));
        static Material Clock() => Flat("metro_clock", HexColor("f4f2ec"));
        static Material Frame2() => Flat("metro_frame2", HexColor("3a3f45"));
        static Material Stone2Default() => Flat("metro_stone2", HexColor("cbbd9d"));
        static Material Concrete2Default() => Flat("metro_concrete2", HexColor("d0ccc2"));
        static Material Roof() => Flat("metro_roof", HexColor("3c4046"));

        static Material SignMaterial() => TexMat("metro_sign_m", MetroSignage.MSignTexture());

        // src/metro.js:308's own loop body reads `const plate = namePlate(s.ka,
        // s.en);` ONCE PER STATION — every station gets its own baked plate.
        // Caching here is keyed on (ka,en), not a fixed string, or every
        // station would share one texture (the pre-text-painting state of
        // this file, when a blank M-only plate made that harmless).
        static Material NamePlateMaterial(string ka, string en) =>
            TexMat($"metro_name_plate_{ka}_{en}", MetroSignage.BuildNamePlate(ka, en));

        // metro.js:221/227 marjanishvili/avlabari fascia: `s.ka || 'default'`.
        static Material BluePlateMaterial(string text, string bgHex, int ratio) =>
            TexMat($"metro_blue_{bgHex}_{ratio}_{text}", MetroSignage.BuildBluePlateBackground(text, bgHex, ratio));

        // metro.js:233 station_square fascia: terminalPlate('Station Square', s.ka || 'default').
        static Material TerminalPlateMaterial(string ka) =>
            TexMat($"metro_terminal_plate_{ka}", MetroSignage.BuildTerminalPlate("Station Square", ka));

        /// <summary>
        /// Per-(kind, part-name) material resolver, mirroring metro.js's
        /// buildKit `extra[name] ?? MAT[name] ?? MAT.concrete` chain
        /// (metro.js:174) exactly — INCLUDING its one real quirk: a part with
        /// no MAT entry and no override for its station (avlabari's "hedge",
        /// metro.js:226-229 never overrides it) silently falls back to
        /// concrete grey, not green. Only Rustaveli's hedge (metro.js:275)
        /// gets the explicit green override. Port the constant, not the
        /// "nicer" behaviour.
        /// </summary>
        static Material ResolvePart(string kind, string partName, string ka, string en)
        {
            switch (kind)
            {
                case "galleria": // metro.js:207-213
                    if (partName == "fascia") return NamePlateMaterial(ka, en);
                    if (partName == "sign") return SignMaterial();
                    if (partName == "stone2") return Stone();     // override -> MAT.stone
                    if (partName == "concrete2") return Concrete(); // override -> MAT.concrete (same value as its own default)
                    if (partName == "roof") return Roof();
                    break;
                case "marjanishvili": // metro.js:221 bluePlate(s.ka || 'მარჯანიშვილი', '#d8ccb2', 12)
                    if (partName == "fascia") return BluePlateMaterial(string.IsNullOrEmpty(ka) ? "მარჯანიშვილი" : ka, "d8ccb2", 12);
                    break;
                case "avlabari": // metro.js:227 bluePlate(s.ka || 'ავლაბარი', '#d8ccb2', 7)
                    if (partName == "fascia") return BluePlateMaterial(string.IsNullOrEmpty(ka) ? "ავლაბარი" : ka, "d8ccb2", 7);
                    if (partName == "sign") return SignMaterial();
                    if (partName == "hedge") return Concrete(); // JS quirk: no override, no MAT.hedge -> MAT.concrete
                    break;
                case "station_square": // metro.js:233 terminalPlate('Station Square', s.ka || 'სადგურის მოედანი')
                    if (partName == "fascia") return TerminalPlateMaterial(string.IsNullOrEmpty(ka) ? "სადგურის მოედანი" : ka);
                    if (partName == "sign") return SignMaterial();
                    break;
                case "rustaveli": // metro.js:270-276
                    // real italic bespoke caption is a documented simplification
                    // (MetroSignage.cs class header) — real name, upright face.
                    if (partName == "fascia") return NamePlateMaterial(string.IsNullOrEmpty(ka) ? "რუსთაველი" : ka, en);
                    if (partName == "sign") return SignMaterial();
                    if (partName == "relief")
                        return PhotoMat("metro_relief", "Assets/Resources/Art/Textures/facades/rustaveli_relief.jpg", HexColor("cabb9d"));
                    if (partName == "glass")
                        return PhotoMat("metro_rustaveli_doors", "Assets/Resources/Art/Textures/facades/rustaveli_doors.jpg", HexColor("2e3338"));
                    if (partName == "hedge") return Flat("metro_hedge_green", HexColor("3d6329")); // explicit override
                    break;
                case "pavilion_arch":
                case "pavilion_hall": // metro.js:293-295 generic stations
                    if (partName == "fascia") return NamePlateMaterial(ka, en);
                    if (partName == "sign") return SignMaterial();
                    break;
                case "totem": // metro.js:222,228,234,277,298,303 — totem's "sign" is ALWAYS overridden with mSign
                    if (partName == "sign") return SignMaterial();
                    if (partName == "pylon") return Pylon();
                    break;
            }
            // MAT table defaults (metro.js:13-30), MAT.concrete for anything unlisted.
            switch (partName)
            {
                case "stone": return Stone();
                case "concrete": return Concrete();
                case "glass": return Glass();
                case "frame": return Frame();
                case "steps": return Steps();
                case "pylon": return Pylon();
                case "cream": return Cream();
                case "ashlar": return Ashlar();
                case "winband": return Winband();
                case "clock": return Clock();
                case "frame2": return Frame2();
                case "stone2": return Stone2Default();
                case "concrete2": return Concrete2Default();
                case "roof": return Roof();
                default: return Concrete();
            }
        }

        static Mesh _nameBoardMesh;
        // NOT `??=`: C#'s null-coalescing tests for a C# null reference, which a
        // DESTROYED UnityEngine.Object is not, so `??=` would keep returning a
        // destroyed Mesh forever. `== null` goes through Unity's own operator,
        // which reports a destroyed object as null. metro.js:285 PlaneGeometry(7,0.9).
        static Mesh NameBoardMesh()
        {
            if (_nameBoardMesh == null) _nameBoardMesh = BuildPlaneMesh(7f, 0.9f);
            return _nameBoardMesh;
        }

        static Mesh BuildPlaneMesh(float w, float h)
        {
            var m = new Mesh { name = $"metro_plane_{w}x{h}" };
            float hw = w * 0.5f, hh = h * 0.5f;
            m.SetVertices(new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f),
            });
            // U MIRRORED — the station name board carries real Georgian text,
            // and a three.js-layout texture quad (u 0..1 with local +X) reads
            // right-to-left in Unity's left-handed frame whatever its yaw.
            // Same defect, same fix, same proof as ShopfrontRenderer.QuadMesh
            // (shop-sign-text-mirrored); its header has the derivation.
            float u0 = ShopfrontRenderer.MirrorSignUv ? 1f : 0f, u1 = ShopfrontRenderer.MirrorSignUv ? 0f : 1f;
            m.SetUVs(0, new List<Vector2> { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, 1), new Vector2(u0, 1) });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static readonly Regex TrailingIndex = new Regex(@"[._]\d+$");
        static string StripSuffix(string s) => string.IsNullOrEmpty(s) ? s : TrailingIndex.Replace(s, "");

        static void ResolveKitMaterials(GameObject instance, string kind, string ka, string en)
        {
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                string part = StripSuffix(mr.gameObject.name);
                mr.sharedMaterial = ResolvePart(kind, part, ka, en);
            }
        }

        /// <summary>Instantiates every Placement in <paramref name="mb"/> under
        /// <paramref name="parent"/>. Returns resolved/fallback counts and the
        /// distinct Kinds that had no matching FBX (reported, never hidden).</summary>
        public static void BuildInto(MetroBuilder mb, Transform parent, List<GameObject> outSpawned,
            out int resolved, out int fallback, List<string> missingKinds)
        {
            resolved = 0;
            fallback = 0;
            if (mb == null) return;

            foreach (var pl in mb.Placements)
            {
                GameObject go;
                if (pl.Kind == "name_board")
                {
                    go = new GameObject("metro_name_board");
                    go.AddComponent<MeshFilter>().sharedMesh = NameBoardMesh();
                    // metro.js:281-287 — the s.pav==='none' board uses the SAME
                    // `plate = namePlate(s.ka, s.en)` texture computed at the
                    // top of the station loop as every other generic fascia.
                    go.AddComponent<MeshRenderer>().sharedMaterial = NamePlateMaterial(pl.Ka, pl.En);
                    resolved++;
                }
                else
                {
                    var prefab = LoadPrefab(pl.Kind);
                    if (prefab == null)
                    {
                        fallback++;
                        if (!missingKinds.Contains(pl.Kind)) missingKinds.Add(pl.Kind);
                        continue;
                    }
                    go = UnityEngine.Object.Instantiate(prefab);
                    go.name = $"metro_{pl.Kind}";
                    ResolveKitMaterials(go, pl.Kind, pl.Ka, pl.En);
                    resolved++;
                }
                go.transform.SetParent(parent, false);
                go.transform.position = pl.Position;
                go.transform.rotation = Quaternion.AngleAxis(pl.YawUnity * Mathf.Rad2Deg, Vector3.up);
                outSpawned.Add(go);
            }
        }
    }
}
