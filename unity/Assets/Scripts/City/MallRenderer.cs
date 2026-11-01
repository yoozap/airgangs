using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The RENDERED half of MallPlacer.cs — src/malls.js's addMalls()
    /// (malls.js:33-88). MallPlacer only DECIDES each mall's body position and
    /// its recomputed sign-plate anchor; until this file nothing in the
    /// project ever Instantiate()'d a single mall (docs/QUALITY_BAR.md "THE
    /// BIGGEST REAL GAP"; ctx.Malls had zero consumers).
    ///
    /// ASSET ALREADY IMPORTED (no bridge step needed, unlike Metro):
    /// Assets/Resources/Art/Props/malls.fbx exists, one child object per mall named
    /// "mall_&lt;bid&gt;", exactly as MallPlacer.cs's own class doc says.
    ///
    /// HANDEDNESS: MallPlacer.Placement.Position is already GeoFrame.ToUnity'd
    /// and carries NO rotation (malls.js never sets root.rotation.y — the
    /// footprint shape is baked into the mesh and self-corrects through the
    /// Blender GLB-&gt;FBX bridge, MallPlacer.cs's own doc). This file applies
    /// Quaternion.identity to the body for exactly that reason. The sign
    /// plate's yaw (SignYawUnity) IS a hand-tuned-style object rotation (it
    /// spins an independently-placed plane, not a mesh whose own vertices
    /// were mirrored) and arrives already through GeoFrame.YawToUnity from
    /// MallPlacer.ComputeSignAnchor — consumed here as-is, never re-negated.
    ///
    /// malls.js:70-80 paints the real Georgian mall name onto the sign plate
    /// via label() (malls.js:10-31, a Canvas 2D text render) — now painted
    /// here too (SignMaterialFor/MallSignTexture below), via City/
    /// SignTextRenderer.cs's Mkhedruli font-chain rasteriser (the gap this
    /// file's own doc, and MetroSignage.cs's, previously reported missing;
    /// closed project-wide 2026-10-27). MINIMAL, ADDITIVE CHANGE ONLY: this
    /// file's existing structure, ownership and everything else about it is
    /// unchanged — two new static helpers plus the one-line swap of
    /// SignBaseMaterial() for SignMaterialFor(p.Name) at the sign's
    /// MeshRenderer. The plate geometry (size = min(19, 3+name.length*0.85) x
    /// 2.1, malls.js:74) and its real anchor/yaw are unchanged, built and
    /// placed correctly as before.
    ///
    /// malls.js:61-64's material pass IS fully ported here: `metalness =
    /// min(metalness,0.4)`, `roughness = max(roughness,0.3)` i.e.
    /// `smoothness = 1-roughness &lt;= 0.7`, applied to a CLONE of each
    /// resolved submesh's material (never the shared FBX asset material in
    /// place — two different mall bids could otherwise share one Material
    /// instance and stomp each other's clamp).
    ///
    /// NO INSTANCING. 42 malls, each a unique architecturally-distinct mesh —
    /// there is no shared (mesh,material) key for instancing to batch on, and
    /// 42 GameObjects is nowhere near where BatchRendererGroup pays for
    /// itself. Plain Instantiate matches docs/QUALITY_BAR.md's own allowance
    /// ("42 malls... small enough that individual objects may be fine").
    ///
    /// RUNTIME CITY GENERATION: <see cref="EnsureBuilt"/> re-reads malls.json
    /// and re-resolves the FBX children every time the scene loads, exactly
    /// like CityRenderer.EnsureBuilt and MetroRenderer.EnsureBuilt — nothing
    /// here is baked.
    /// </summary>
    [ExecuteAlways]
    public class MallRenderer : MonoBehaviour
    {
        List<GameObject> _spawned;
        public int SpawnedCount => _spawned?.Count ?? 0;
        public int ResolvedCount { get; private set; }
        public int FallbackCount { get; private set; }
        public MallPlacer.Result Result { get; private set; }

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

        public void EnsureBuilt()
        {
            if (_spawned != null) return;
            // FIXED 2026-10-30 (docs/port_units/cityfix.json): this renderer had
            // NO clear at all. _spawned is private, so a scene load leaves it
            // null, this guard passes, and BuildInto adds a second copy of all
            // 42 malls plus their 42 sign plates on top of whatever the scene
            // file had baked under this transform. Only a walk of real Transform
            // children can see those (ShopfrontRenderer's pattern).
            //
            // disposeRuntimeAssets defaults to true here and should: every
            // Material, Mesh and Texture2D this renderer puts on a child is
            // built PER BUILD and shared with nothing — ClampMaterials clones
            // the FBX materials per mall (deliberately, see its doc),
            // BuildPlaneMesh makes a fresh quad per sign, and SignMaterialFor
            // rasterises a fresh 1024x128 RGBA Texture2D per name. That is
            // ~42 materials + 42 meshes + 42 x 512 KB of texture leaked per
            // rebuild otherwise. The imported malls.fbx meshes/materials the
            // clones were made FROM are persistent, so GeneratedChildren skips
            // them.
            GeneratedChildren.Clear(transform);
            _spawned = new List<GameObject>();
            var city = CityData.Load();
            Result = MallPlacer.Build(terrain: city?.Terrain);
            BuildInto(Result, transform, _spawned, out var resolved, out var fallback);
            ResolvedCount = resolved;
            FallbackCount = fallback;
            Debug.Log($"MALLRENDERER malls={Result.Malls.Count}/{Result.TotalInJson} " +
                      $"spawned={_spawned.Count} resolved={resolved} fallback={fallback}");
        }

        /// <summary>Scene-free entry point: builds straight off an already-
        /// computed <see cref="CityLoader.Context"/> (e.g. the Editor bake, or
        /// a probe with no live scene/MonoBehaviour). Returns null off-Editor
        /// or with no malls resolved.</summary>
        public static GameObject Render(CityLoader.Context ctx, Transform parent = null)
        {
            if (ctx?.Malls.Malls == null) return null;
            var root = parent != null ? parent : new GameObject("Malls").transform;
            var spawned = new List<GameObject>();
            BuildInto(ctx.Malls, root, spawned, out _, out _);
            return root.gameObject;
        }

        public const string FbxPath = "Assets/Resources/Art/Props/malls.fbx";

        static GameObject _fbxRootCache;
        static GameObject FbxRoot() => _fbxRootCache ??= ArtAssets.Load<GameObject>(FbxPath);

        static Dictionary<string, Transform> _childByName;
        static Transform FindChild(string name)
        {
            var root = FbxRoot();
            if (root == null) return null;
            if (_childByName == null)
            {
                _childByName = new Dictionary<string, Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (!_childByName.ContainsKey(t.name)) _childByName[t.name] = t;
            }
            return _childByName.TryGetValue(name, out var hit) ? hit : null;
        }

        static Material _signMat;
        static Material SignBaseMaterial()
        {
            if (_signMat != null) return _signMat;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _signMat = new Material(shader) { name = "mall_sign_plate" };
            var c = new Color32(0x14, 0x18, 0x1d, 255); // malls.js:75 label() background
            if (_signMat.HasProperty("_BaseColor")) _signMat.SetColor("_BaseColor", c);
            else _signMat.SetColor("_Color", c);
            return _signMat;
        }

        // =====================================================================
        // MINIMAL ADDITION (file still owned by MallRenderer.cs's original
        // author — kept to these two small static helpers): the REAL mall
        // name on the entrance sign, closing this file's own documented gap
        // ("the FACE is a flat colour stand-in... with no text, the same
        // honest-gap choice MetroSignage.cs made") now that a Mkhedruli font
        // chain exists (Assets/Editor/BuildFontAssets.cs). Reuses City/
        // SignTextRenderer.cs — the same text rasteriser ShopSignTextures.cs
        // and MetroSignage.cs use — rather than a third text toolkit.
        // Port of malls.js:10-31 label(text,w,h,bg,fg,size=48), called at
        // malls.js:75 as label(e.name,1024,128,'#14181d','#f2f4f6',72).
        // =====================================================================
        static Material SignMaterialFor(string name)
        {
            var tex = MallSignTexture(name ?? "");
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = $"mall_sign_plate_{name}" };
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            else m.mainTexture = tex;
            return m;
        }

        // internal (not private) so SignTextInkProbe.cs can rasterise the
        // exact same texture the real sign uses and read its pixels back —
        // AssemblyInfo.cs grants Assembly-CSharp-Editor access.
        internal static Texture2D MallSignTexture(string text)
        {
            const int w = 1024, h = 128;
            var bg = new Color32(0x14, 0x18, 0x1d, 255); // malls.js:14 fillStyle=bg
            var fg = new Color32(0xf2, 0xf4, 0xf6, 255); // malls.js:16 fillStyle=fg
            var buf = new Color32[w * h];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;
            // malls.js:17-22 size=48->72 here (the call's own `size` arg),
            // shrink by 4 while measureText(text).width > w-30 && s>18.
            float size = 72f;
            while (SignTextRenderer.MeasureWidth(text, size, true) > (w - 30) && size > 18f) size -= 4f;
            // malls.js:23-24 textAlign=center/textBaseline=middle, fillText(text, w/2, h/2+2).
            SignTextRenderer.DrawText(buf, w, h, text, w * 0.5f, h * 0.5f + 2f, size, true, fg, "center");
            // ROW FLIP, CANVAS -> TEXTURE. This comment used to say "malls.js
            // never flips (no canvas-to-Unity row flip needed here — this
            // buffer is consumed the same 'row == canvas y' way
            // ShopSignTextures.cs's own Paint() leaves its buffer, with no flip
            // applied either)". The precedent it cited was itself the bug:
            // SignTextRenderer.DrawText writes in the JS canvas's row-0-is-TOP
            // convention and Texture2D.SetPixels32 reads index 0 as the
            // BOTTOM-left, so both plates were stored upside down.
            // MetroSignage.ToTexture (:128-142) and LandmarkSignage (:119-129)
            // each flip ONCE on upload and say why; this is the same flip.
            var flipped = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    flipped[(h - 1 - y) * w + x] = buf[y * w + x];
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            t.SetPixels32(flipped);
            // makeNoLongerReadable IN A PLAYER ONLY. A readable Texture2D keeps a
            // full CPU copy as well as the GPU one — measured, a 2048x240 RGBA32
            // plate reports 3.75 MB, exactly twice its pixels, and the 425
            // runtime-baked readable textures were costing 182 MB. These are
            // one-shot bakes, so the copy is pure cost in the game; it has to
            // stay in the EDITOR because Editor/Probes/SignTextInkProbe and
            // LandmarkSignageProbe prove the Georgian text by reading the pixels
            // back (GetPixels32), and a non-readable texture would make those
            // probes throw instead of verify.
            t.Apply(false, !Application.isEditor);
            return t;
        }

        static Mesh BuildPlaneMesh(float w, float h)
        {
            var m = new Mesh { name = $"mall_sign_plane_{w:0.0}x{h}" };
            float hw = w * 0.5f, hh = h * 0.5f;
            m.SetVertices(new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f),
            });
            // U MIRRORED — the real Georgian mall name is painted onto this
            // texture-space quad, and a three.js-layout quad (u 0..1 with local
            // +X) reads right-to-left in Unity's left-handed frame whatever its
            // yaw. See ShopfrontRenderer.QuadMesh's header for the derivation
            // (shop-sign-text-mirrored).
            float u0 = ShopfrontRenderer.MirrorSignUv ? 1f : 0f, u1 = ShopfrontRenderer.MirrorSignUv ? 0f : 1f;
            m.SetUVs(0, new List<Vector2> { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, 1), new Vector2(u0, 1) });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Clones and clamps a mall body's materials — malls.js:
        /// 61-64. Clones first: several bids could otherwise share one FBX
        /// material instance and stomp each other's clamp.</summary>
        static void ClampMaterials(GameObject instance)
        {
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var src = mr.sharedMaterials;
                var cloned = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    if (src[i] == null) { cloned[i] = null; continue; }
                    var m = new Material(src[i]);
                    if (m.HasProperty("_Metallic"))
                        m.SetFloat("_Metallic", Mathf.Min(m.GetFloat("_Metallic"), 0.4f)); // metalness = min(metalness,0.4)
                    if (m.HasProperty("_Smoothness"))
                        m.SetFloat("_Smoothness", Mathf.Min(m.GetFloat("_Smoothness"), 0.7f)); // roughness=max(roughness,0.3) -> smoothness<=0.7
                    cloned[i] = m;
                }
                mr.sharedMaterials = cloned;
            }
        }

        /// <summary>Instantiates every <see cref="MallPlacer.Placement"/> in
        /// <paramref name="result"/> under <paramref name="parent"/> — the
        /// real mall body plus its entrance sign plate. Returns resolved
        /// (found a "mall_{bid}" child) / fallback (bid missing from the FBX,
        /// reported not hidden) counts.</summary>
        public static void BuildInto(MallPlacer.Result result, Transform parent, List<GameObject> outSpawned,
            out int resolved, out int fallback)
        {
            resolved = 0;
            fallback = 0;
            if (result.Malls == null) return;

            foreach (var p in result.Malls)
            {
                var child = FindChild(p.ChildName);
                if (child == null) { fallback++; continue; }

                var go = UnityEngine.Object.Instantiate(child.gameObject);
                go.name = $"mall_{p.Bid}";
                ClampMaterials(go);
                go.transform.SetParent(parent, false);
                go.transform.position = p.Position;
                go.transform.rotation = Quaternion.identity; // malls.js: no root.rotation.y anywhere
                outSpawned.Add(go);

                // Entrance sign plate — real name text is the same documented
                // Georgian-font-asset gap MetroSignage.cs already reported.
                // Size/position/yaw are the real, computed values.
                var signGo = new GameObject($"mall_{p.Bid}_sign");
                signGo.AddComponent<MeshFilter>().sharedMesh = BuildPlaneMesh(p.SignWidth, 2.1f); // malls.js:74
                // malls.js:75 label(e.name, ...) — real name text (see
                // SignMaterialFor's own header for scope/attribution).
                signGo.AddComponent<MeshRenderer>().sharedMaterial = SignMaterialFor(p.Name);
                signGo.transform.SetParent(parent, false);
                signGo.transform.position = p.SignPosition;
                signGo.transform.rotation = Quaternion.AngleAxis(p.SignYawUnity * Mathf.Rad2Deg, Vector3.up);
                outSpawned.Add(signGo);

                resolved++;
            }
        }
    }
}
