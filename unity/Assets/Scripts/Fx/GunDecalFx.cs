using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders the BELLY GUN's own two decal layers — src/guns.js:388-389's
    /// `this.holes` / `this.cracks`, built there as two `DecalLayer(scene,
    /// makeHoleTexture(), 0.52, 1600)` / `(…, makeCrackTexture(), 1.25, 400)`.
    ///
    /// Combat/Guns.cs already simulates both (`MachineGun.Holes`/`.Cracks`, the
    /// same Combat/ImpactFx.cs DecalLayer the JS reuses for both) and
    /// Combat/GunDecalTextures.cs already PAINTS both textures
    /// (src/guns.js:90-171 makeHoleTexture/makeCrackTexture, probe-verified
    /// per-pixel against tools/_probe_damagetex.mjs). Nothing joined them: the
    /// textures had no material, the layers had no renderer, and
    /// Combat/Guns.cs's own header said so ("wiring either onto a real
    /// renderer/material is still VFX territory, not done here"). So a round
    /// into a painted-on window stamped a crack star that was never drawn.
    /// This file is that join, modelled line-for-line on Fx/ImpactDecalFx.cs —
    /// one Material per layer built ONCE in the constructor (never one per
    /// shot), the same Shaders/ImpactDecal.shader, the same
    /// Fx/InstancedSpriteBatch submit, and the same pure RESUBMIT every frame
    /// because src/guns.js's DecalLayer has no life/time field either: a mark
    /// is drawn once and stays until its ring-buffer slot is reused.
    ///
    /// OWNED BY MachineGun (a plain field + a Render call in its own Update),
    /// exactly as MachineGun already owns Fx/MuzzleFlashFx.cs and
    /// Fx/ImpactParticleFx.cs, so no Core/* wiring is needed: the gun is
    /// already ticked every frame by Core/CombatWorldAdapter.cs's
    /// GunTickable.
    /// </summary>
    public sealed class GunDecalFx
    {
        public readonly Material HoleMat, CrackMat;

        public GunDecalFx()
        {
            var sh = Shader.Find("TbilisiFly/ImpactDecal");
            HoleMat = BuildMat(sh, GunDecalTextures.BuildHole());   // src/guns.js:388
            CrackMat = BuildMat(sh, GunDecalTextures.BuildCrack()); // src/guns.js:389
        }

        static Material BuildMat(Shader sh, Texture2D tex)
        {
            if (sh == null) return null;
            var m = new Material(sh) { enableInstancing = true };
            m.SetTexture("_MainTex", tex);
            return m;
        }

        /// <summary>Live stamps submitted on the last <see cref="Render"/> —
        /// instrumentation, so a probe can prove this ran instead of assuming it
        /// (see .claude/skills/unity/SKILL.md, "a probe camera only sees what
        /// ticked").</summary>
        public int DrawnHoles { get; private set; }
        public int DrawnCracks { get; private set; }

        static readonly Vector4 WhiteTint = new Vector4(1f, 1f, 1f, 1f);
        Matrix4x4[] _mScratch = new Matrix4x4[InstancedSpriteBatch.MaxPerBatch];
        Vector4[] _tScratch = new Vector4[InstancedSpriteBatch.MaxPerBatch];

        int DrawLayer(DecalLayer layer, Material mat)
        {
            if (mat == null || layer == null) return 0;
            int count = layer.Count;
            if (count <= 0) return 0;
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
            return n;
        }

        /// <summary>Call once per frame from the gun's own Update, after it has
        /// stamped anything this tick — same "simulate then render" convention
        /// as Fx/ImpactDecalFx.Render.</summary>
        public void Render(MachineGun gun)
        {
            if (gun == null) return;
            DrawnHoles = DrawLayer(gun.Holes, HoleMat);
            DrawnCracks = DrawLayer(gun.Cracks, CrackMat);
        }

        public void Dispose()
        {
            Kill(HoleMat);
            Kill(CrackMat);
        }

        static void Kill(Material m)
        {
            if (m == null) return;
            if (m.mainTexture != null) UnityEngine.Object.DestroyImmediate(m.mainTexture);
            UnityEngine.Object.DestroyImmediate(m);
        }
    }
}
