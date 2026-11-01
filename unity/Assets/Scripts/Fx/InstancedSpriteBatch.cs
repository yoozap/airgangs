using UnityEngine;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Shared GPU-instancing helper for every billboard-quad VFX system in this
    /// port's scope (Shaders/ImpactSprite.shader, Shaders/Fireball.shader,
    /// Shaders/MuzzleFlash.shader's flash quad): one static unit quad mesh
    /// (local range -0.5..0.5, UV 0..1 — the exact convention
    /// Shaders/GlassGlint.shader already established) plus a batched
    /// Graphics.DrawMeshInstanced call, since that API caps a single call at
    /// 1023 instances — fire.js alone can have up to S_MAX=4200 live smoke
    /// particles (src/fire.js:15), so a naive single call would silently drop
    /// the rest.
    ///
    /// Every system in this scope creates its Material(s) ONCE (at system
    /// construction) and reuses them every frame/every particle — never one
    /// Material per spawn — which is the Unity-side answer to the JS's own
    /// worst perf bug (a new PointLight per shot forcing a full shader
    /// recompile, "the shooting freeze"). URP's fixed light loop has no such
    /// recompile hazard for real Lights either, so BlastRenderer/MuzzleFlashFx
    /// are free to use real Lights without the same caution — the caution
    /// here is specifically about Materials/Shaders.
    /// </summary>
    public static class InstancedSpriteBatch
    {
        public const int MaxPerBatch = 1023; // Graphics.DrawMeshInstanced's own ceiling

        static Mesh _quad;
        public static Mesh UnitQuad => _quad != null ? _quad : (_quad = BuildQuad());

        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "Fx_UnitQuad" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            // Billboards move freely in view space; a tight object-space bound
            // would get wrongly culled the instant the camera isn't looking
            // straight at the (arbitrary) instance origin.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int PropsAId = Shader.PropertyToID("_PropsA");

        /// <summary>
        /// Draws up to `count` instances of `mesh` with `mat`, batching at
        /// MaxPerBatch. `tint`/`propsA` feed the shaders' per-instance
        /// UNITY_INSTANCING_BUFFER slots (_Tint: rgb=tint, a=life; _PropsA:
        /// x=seed, y=dark/intensity, zw unused) — pass null for either if the
        /// bound shader doesn't read it (e.g. Shaders/Fireball.shader only
        /// reads _PropsA).
        /// </summary>
        public static void Draw(Mesh mesh, Material mat, Matrix4x4[] matrices, Vector4[] tint, Vector4[] propsA, int count)
        {
            if (count <= 0 || mat == null) return;
            var mpb = new MaterialPropertyBlock();
            int at = 0;
            while (at < count)
            {
                int n = Mathf.Min(MaxPerBatch, count - at);
                var batchM = Slice(matrices, at, n);
                mpb.Clear();
                if (tint != null) mpb.SetVectorArray(TintId, Slice(tint, at, n));
                if (propsA != null) mpb.SetVectorArray(PropsAId, Slice(propsA, at, n));
                Graphics.DrawMeshInstanced(mesh, 0, mat, batchM, n, mpb,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false);
                at += n;
            }
        }

        // Graphics.DrawMeshInstanced / SetVectorArray both want arrays sized
        // exactly to the batch — a tiny copy, called at most 5x/frame even at
        // fire.js's biggest pool (S_MAX=4200 / 1023 ~= 5 batches).
        static Matrix4x4[] Slice(Matrix4x4[] src, int at, int n)
        {
            var o = new Matrix4x4[n];
            System.Array.Copy(src, at, o, 0, n);
            return o;
        }
        static Vector4[] Slice(Vector4[] src, int at, int n)
        {
            var o = new Vector4[n];
            System.Array.Copy(src, at, o, 0, n);
            return o;
        }
    }
}
