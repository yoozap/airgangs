using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders src/impactfx.js's DecalLayer — the pit/spall/exit/scuff/star bullet
    /// marks Combat/ImpactFx.cs's ImpactFx (DecalLayer/DecalStamp/DecalGeometry)
    /// already simulates as pure pose/size data (src/impactfx.js:76-160's
    /// constructor/stamp, minus the THREE.InstancedMesh/PlaneGeometry/
    /// MeshBasicMaterial construction that constructor also does, src/impactfx.js:
    /// 81-110 — explicitly left to "whoever wires the renderer" per
    /// Combat/ImpactFx.cs's own DecalLayer/DecalStamp doc comments). Five layers,
    /// five textures (Combat/ImpactDecalTextures.cs, src/impactfx.js:168-418), one
    /// Material each, built ONCE in the constructor — the same "never one
    /// Material per shot" house rule every other Fx consumer in this project
    /// follows (see Fx/MuzzleFlashFx.cs's own header for why that matters here).
    ///
    /// Decals do not fade: src/impactfx.js's DecalLayer has no life/time field at
    /// all (a mark is drawn once and stays until something else overwrites that
    /// ring-buffer slot), so unlike MuzzleFlashFx/RocketTrailFx/BulletTrailFx this
    /// is a pure one-shot RESUBMIT — every live slot is re-drawn straight off
    /// DecalLayer.Slots every frame, there is no per-instance simulation step of
    /// its own to run first (ImpactFx.Update(dt) only steps Debris/Sparks/
    /// Casings; decals need no dt at all).
    /// </summary>
    public sealed class ImpactDecalFx
    {
        public readonly Material SpallMat, PitMat, ExitMat, ScuffMat, StarMat;

        public ImpactDecalFx()
        {
            var sh = Shader.Find("TbilisiFly/ImpactDecal");
            SpallMat = BuildMat(sh, ImpactDecalTextures.BuildSpall());
            PitMat = BuildMat(sh, ImpactDecalTextures.BuildPit());
            ExitMat = BuildMat(sh, ImpactDecalTextures.BuildExit());
            ScuffMat = BuildMat(sh, ImpactDecalTextures.BuildScuff());
            StarMat = BuildMat(sh, ImpactDecalTextures.BuildStar());
        }

        static Material BuildMat(Shader sh, Texture2D tex)
        {
            if (sh == null) return null;
            var m = new Material(sh) { enableInstancing = true };
            m.SetTexture("_MainTex", tex);
            return m;
        }

        static readonly Vector4 WhiteTint = new Vector4(1f, 1f, 1f, 1f);
        Matrix4x4[] _mScratch = new Matrix4x4[InstancedSpriteBatch.MaxPerBatch];
        Vector4[] _tScratch = new Vector4[InstancedSpriteBatch.MaxPerBatch];

        void DrawLayer(DecalLayer layer, Material mat)
        {
            if (mat == null || layer == null) return;
            int count = layer.Count;
            if (count <= 0) return;
            if (_mScratch.Length < count)
            {
                _mScratch = new Matrix4x4[count];
                _tScratch = new Vector4[count];
            }
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                var s = layer.Slots[i];
                if (!s.Live) continue;
                _mScratch[n] = Matrix4x4.TRS(s.Position, s.Rotation, s.Scale);
                _tScratch[n] = s.Tint.HasValue
                    ? new Vector4(s.Tint.Value.r / 255f, s.Tint.Value.g / 255f, s.Tint.Value.b / 255f, 1f)
                    : WhiteTint;
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, mat, _mScratch, _tScratch, null, n);
        }

        /// <summary>Call once per frame — after Combat/ImpactFx.cs's ImpactFx.Update(dt)
        /// has run, same "simulate then render" convention as Fx/BlastRenderer.cs's
        /// own Render().</summary>
        public void Render(ImpactFx fx)
        {
            if (fx == null) return;
            DrawLayer(fx.Spall, SpallMat);
            DrawLayer(fx.Pits, PitMat);
            DrawLayer(fx.Exits, ExitMat);
            DrawLayer(fx.Scuffs, ScuffMat);
            DrawLayer(fx.Stars, StarMat);
        }

        public void Dispose()
        {
            Kill(SpallMat);
            Kill(PitMat);
            Kill(ExitMat);
            Kill(ScuffMat);
            Kill(StarMat);
        }

        static void Kill(Material m)
        {
            if (m == null) return;
            if (m.mainTexture != null) UnityEngine.Object.DestroyImmediate(m.mainTexture);
            UnityEngine.Object.DestroyImmediate(m);
        }
    }
}
