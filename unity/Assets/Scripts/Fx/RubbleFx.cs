using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.City;
using TbilisiFly.Core;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// The six instanced rubble layers — src/citymap.js:2675-2724
    /// (<c>_initRubble</c> / <c>_rubbleGrew</c>). tools/build_rubble_kit.py bakes
    /// six low-poly vertex-coloured debris chunks (rb_slab / rb_wall / rb_lump /
    /// rb_bricks / rb_beam / rb_rebar, ~1.6k tris total, origins at the base
    /// centre, real metre scale) into ONE model; the JS turns each named mesh
    /// into a <c>THREE.InstancedMesh(geo, mat, 4000)</c> with <c>count = 0</c>
    /// and fills the pools as buildings come down.
    ///
    /// THIS IS THE RENDERER City/DebrisField.cs's own header asks for, verbatim:
    /// "A caller with the real instanced layers turns each Particle /
    /// ScatterPiece into a TRS matrix (see the JS's own `m.compose`: position,
    /// Euler(rx,0,rz) as rotation, uniform scale) and uploads it." Until now
    /// nothing in the port loaded rubble_kit at all, which is why DebrisField —
    /// a complete, probe-verified physics model — had no caller and no
    /// aeroplane-crashed building ever shed a single chunk.
    ///
    /// ONE LAYER, NOT SIX — AND THAT IS PARITY, NOT A PORT BUG. The builder
    /// script makes six pieces, but the asset the three.js game actually ships,
    /// public/models/rubble_kit.glb (2,964 bytes, Draco), contains exactly ONE
    /// node and ONE mesh: `rb_beam`. Read its glTF JSON — meshes ["rb_beam"],
    /// nodes ["rb_beam"] — and that is what `_initRubble`'s traverse has been
    /// building a single InstancedMesh from all along. (Most likely the same
    /// gltf-transform lossiness already on record for the building kit; it is
    /// glTF-Transform v4.5.1 output.) So RUBBLEFX logging `layers=1` means the
    /// port matches the shipped asset. Re-exporting the kit from
    /// tools/build_rubble_kit.py without the lossy pass would give both the JS
    /// and this file their six shapes with no code change here — it is reported
    /// as an ASSET follow-up rather than papered over with a different number.
    ///
    /// POOL SEMANTICS, matching the JS exactly. A slot is RESERVED once
    /// (<see cref="Reserve"/> -> the JS's <c>id: layer.count++</c>), rewritten
    /// every frame while the piece is in flight, and then simply left alone:
    /// "final matrix stays in the layer" (citymap.js:2644), which is what makes
    /// the settled debris permanent without a second renderer for it. The pools
    /// never shrink and are capped at <see cref="DebrisField.MaxPerLayer"/>
    /// (4000), the JS's own ceiling — <c>if (layer.count >= 4000) continue</c>.
    ///
    /// CULLING. The JS computes a bounding sphere as the pool grows and turns
    /// culling OFF entirely while anything is moving, "because a stale sphere
    /// would cull it out of the air" (citymap.js:2645-2654). Here each layer
    /// keeps a growing world Bounds and hands it to <c>RenderParams.worldBounds</c>
    /// — same intent, and because the Bounds is encapsulated on every write
    /// (not debounced to a microtask), there is no stale-sphere window to
    /// special-case.
    ///
    /// MATERIAL. The kit is vertex-coloured, flat-shaded concrete/plaster/brick
    /// (<c>MeshLambertMaterial { vertexColors: true, flatShading: true }</c>).
    /// Shaders/CityWall.shader is the project's one URP shader that multiplies
    /// mesh vertex COLOR into albedo and supports GPU instancing, so it is used
    /// whenever the imported mesh really carries a colour stream. When it does
    /// NOT (an FBX import that dropped vertex colours), falling through to that
    /// shader would risk BLACK rubble — the exact trap Shaders/CityKit.shader's
    /// header documents ("a missing vertex stream does not reliably read as
    /// white") — so the fallback is stock URP/Lit tinted with the kit's own
    /// weathered-concrete value from tools/build_rubble_kit.py's CONCRETE
    /// constant. <see cref="VertexColoured"/> reports which path ran.
    /// </summary>
    public sealed class RubbleFx
    {
        public const string KitPath = "Assets/Resources/Art/Props/rubble_kit.fbx";

        /// <summary>tools/build_rubble_kit.py's CONCRETE = (0.42, 0.41, 0.38) —
        /// the kit's own weathered-grey value, used only on the no-vertex-colour
        /// fallback path.</summary>
        static readonly Color Concrete = new Color(0.42f, 0.41f, 0.38f);

        sealed class Layer
        {
            public Mesh Mesh;
            public Material Mat;
            public Matrix4x4 Local;     // the kit object's own transform, baked per-instance (the JS's geo.applyMatrix4(o.matrixWorld))
            public Matrix4x4[] M;
            public int Count;
            public Bounds Bounds;
            public bool HasBounds;
        }

        readonly List<Layer> _layers = new List<Layer>();

        public int LayerCount => _layers.Count;
        public bool Ready => _layers.Count > 0;
        public bool VertexColoured { get; private set; }

        /// <summary>Instances submitted on the last <see cref="Render"/> — the
        /// "assert instances were actually submitted" evidence the SKILL asks
        /// for, rather than a count this class computed itself.</summary>
        public int DrawnInstances { get; private set; }
        public int DrawCalls { get; private set; }

        /// <summary>Total reserved slots across all six layers (the JS's
        /// sum of `layer.count`).</summary>
        public int Reserved
        {
            get { int n = 0; foreach (var l in _layers) n += l.Count; return n; }
        }

        /// <summary>citymap.js:2675-2707 _initRubble. Idempotent; a missing kit
        /// logs once and leaves <see cref="Ready"/> false, exactly like the JS's
        /// own loader error branch ("rubble kit skipped") — the collapse then
        /// runs with no debris instead of throwing.</summary>
        public bool Load()
        {
            if (_layers.Count > 0) return true;
            var root = ArtAssets.Load<GameObject>(KitPath);
            if (root == null)
            {
                Debug.LogWarning($"RubbleFx: {KitPath} not found — rubble kit skipped (citymap.js:2710)");
                return false;
            }
            var wallShader = Shader.Find("TbilisiFly/CityWall");
            var litShader = GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                bool coloured = mesh.colors32 != null && mesh.colors32.Length == mesh.vertexCount;
                var shader = (coloured && wallShader != null) ? wallShader : litShader;
                if (shader == null) continue;
                var mat = new Material(shader) { name = $"rubble_{mesh.name}", enableInstancing = true };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", coloured ? Color.white : Concrete);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.08f); // raw fractured concrete
                _layers.Add(new Layer
                {
                    Mesh = mesh,
                    Mat = mat,
                    // NEVER reset an imported model's natural transform to identity
                    // (SKILL.md) — the kit's own object transform is COMPOSED into
                    // every instance here, which is what geo.applyMatrix4(o.matrixWorld)
                    // does once in the JS.
                    Local = mf.transform.localToWorldMatrix,
                    M = new Matrix4x4[DebrisField.MaxPerLayer],
                });
                VertexColoured |= coloured;
            }
            Debug.Log($"RUBBLEFX layers={_layers.Count} vertexColoured={VertexColoured.ToString().ToLowerInvariant()}");
            return _layers.Count > 0;
        }

        /// <summary>citymap.js:2533-2535 — pick a layer at random, and skip the
        /// piece entirely when that layer's 4000-slot pool is full. Returns the
        /// (layer, slot) pair to write, or null when there is no room.</summary>
        public (int layer, int slot)? Reserve(System.Random rnd)
        {
            if (_layers.Count == 0) return null;
            int li = (int)(rnd.NextDouble() * _layers.Count);
            if (li >= _layers.Count) li = _layers.Count - 1;
            var l = _layers[li];
            if (l.Count >= DebrisField.MaxPerLayer) return null; // citymap.js:2535
            int slot = l.Count++;
            return (li, slot);
        }

        /// <summary>citymap.js:2637-2642 — `m.compose(pos, Euler(rx, 0, rz),
        /// scale)` then `setMatrixAt(id, m)`. `scaleY` differs from `scaleXZ`
        /// only for the scattered (settled) pieces, which the JS composes as
        /// `Vector3(sc, scaleY, sc)`.</summary>
        public void SetPiece(int layer, int slot, Vector3 pos, float rotX, float rotY, float rotZ,
            float scaleXZ, float scaleY)
        {
            if (layer < 0 || layer >= _layers.Count) return;
            var l = _layers[layer];
            if (slot < 0 || slot >= l.Count) return;
            var trs = Matrix4x4.TRS(pos, Quaternion.Euler(rotX * Mathf.Rad2Deg, rotY * Mathf.Rad2Deg, rotZ * Mathf.Rad2Deg),
                new Vector3(scaleXZ, scaleY, scaleXZ));
            l.M[slot] = trs * l.Local;
            var b = new Bounds(pos, new Vector3(4f, 4f, 4f) * Mathf.Max(1f, scaleXZ));
            if (!l.HasBounds) { l.Bounds = b; l.HasBounds = true; }
            else l.Bounds.Encapsulate(b);
        }

        /// <summary>One draw per (layer, 1023-instance slice) — the same
        /// Graphics.RenderMeshInstanced ceiling City/CityRenderer.cs slices at.
        /// Call once per frame after the physics step.</summary>
        public void Render(int layerIndex = 0)
        {
            DrawnInstances = 0;
            DrawCalls = 0;
            for (int i = 0; i < _layers.Count; i++)
            {
                var l = _layers[i];
                if (l.Mesh == null || l.Mat == null || l.Count == 0) continue;
                int subs = Mathf.Max(1, l.Mesh.subMeshCount);
                for (int s = 0; s < subs; s++)
                {
                    var rp = new RenderParams(l.Mat)
                    {
                        shadowCastingMode = ShadowCastingMode.Off, // citymap.js:2700 sets receiveShadow only
                        receiveShadows = true,
                        worldBounds = l.HasBounds ? l.Bounds : new Bounds(Vector3.zero, Vector3.one * 1e5f),
                        layer = layerIndex,
                    };
                    for (int offset = 0; offset < l.Count; offset += 1023)
                    {
                        int n = Mathf.Min(1023, l.Count - offset);
                        Graphics.RenderMeshInstanced(rp, l.Mesh, s, l.M, n, offset);
                        DrawCalls++;
                        if (s == 0) DrawnInstances += n;
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var l in _layers)
                if (l.Mat != null) Object.DestroyImmediate(l.Mat);
            _layers.Clear();
        }
    }
}
