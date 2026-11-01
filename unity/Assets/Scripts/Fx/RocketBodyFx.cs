using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Port of src/rockets.js:147-187's rocket template (body cylinder, nose
    /// cone, 4 fins, engine-glow sprite) — PREVIOUSLY filed as "parked for a
    /// missing asset". That was wrong: neither src/rockets.js nor the rest of
    /// this scope ever loads a model for it — it is built entirely from
    /// `THREE.CylinderGeometry`/`ConeGeometry`/`BoxGeometry` primitives plus a
    /// canvas-gradient sprite, the same kind of thing City/ParkBuilder.cs
    /// already ports with its own primitive builders. So this is a direct,
    /// from-scratch Unity port via Combat/ProceduralPropMesh.cs, not an
    /// asset stand-in.
    ///
    /// Before this file, a live rocket in Unity had NO visible body at all —
    /// only Combat/Rockets.cs's own RocketTrailFx puffs trailed behind an
    /// invisible point. Every rocket shape below is drawn with
    /// `Graphics.DrawMesh` (MaxLive=6, same "no GameObject per instance"
    /// discipline as Fx/BlastRenderer.cs's fireballs), never a per-rocket
    /// GameObject.
    ///
    /// HANDEDNESS: the JS group's own local frame has the nose at z=-0.96 and
    /// the fins/glow at the tail (z=+0.62/+0.95) — i.e. "nose" is the JS's
    /// NEGATIVE local Z. This project's established forward-flip rule (Three's
    /// -Z-forward is Unity's +Z-forward — see Combat/Rockets.cs's own Fire()
    /// comment, "the hardpoint's local z (-0.9) becomes +0.9") puts the nose
    /// at Unity's POSITIVE Z instead, which is exactly what makes
    /// `Quaternion.LookRotation(rk.Dir)` (whose local +Z aligns with `Dir`,
    /// the direction of travel) point the nose the right way with no extra
    /// correction. Every length below is authored directly in that convention
    /// rather than carrying the JS's signs over field-by-field.
    ///
    /// The engine-glow sprite is a DOCUMENTED SIMPLIFICATION, same category as
    /// Combat/Rockets.cs's own whoosh-sound note: Shaders/ImpactSprite.shader's
    /// only two keyword modes that sample a bound texture (_FX_FLASH/_FX_SMOKE)
    /// are each shaped for a different effect, and adding a third keyword to
    /// sample src/rockets.js:166-172's exact 4-stop gradient would be writing
    /// new shader code, out of this port's "math, not shaders" scope. The
    /// shared default (no-keyword) mode already used by RocketTrailFx's own
    /// puffs — per-instance tint colour, procedural radial falloff — reads as
    /// the same warm glowing blob at the rocket's own small screen size.
    /// </summary>
    public sealed class RocketBodyFx
    {
        // ---- src/rockets.js:158 body ----
        const float BodyR = 0.13f, BodyLen = 1.5f;
        const int BodySides = 10;
        // ---- src/rockets.js:161 nose ---- base joins the body's +Z end (0.75),
        // tip extends 0.42 further to +1.17.
        const float NoseLen = 0.42f;
        const float NoseCenterZ = 0.75f + NoseLen / 2f;
        // ---- src/rockets.js:168-174 fins ---- tail end (negative Z, opposite
        // the nose), radius 0.2, box 0.3(radial) x 0.02(tangential) x 0.34(deep).
        const int FinCount = 4;
        const float FinRadius = 0.2f;
        const float FinZ = -0.62f;
        const float FinRadial = 0.3f, FinTangent = 0.02f, FinDepth = 0.34f;
        // ---- src/rockets.js:179-184 glow sprite ---- further back than the fins.
        const float GlowZ = -0.95f;
        const float GlowScale = 1.5f;

        public Mesh BodyMesh, MagentaMesh; // magenta = nose + fins, src/rockets.js:157 noseMat reused for both
        public Material BodyMat, MagentaMat, GlowMat;

        public RocketBodyFx()
        {
            BodyMesh = ProceduralPropMesh.Combine(
                new[] { ProceduralPropMesh.Cylinder(BodyR, BodyR, BodyLen, BodySides) }, "Fx_RocketBody");

            var pieces = new List<(Vector3[], int[])>
            {
                ProceduralPropMesh.Cylinder(BodyR, 0f, NoseLen, BodySides, NoseCenterZ), // nose cone, tip at +Z
            };
            for (int k = 0; k < FinCount; k++)
            {
                float a = (float)k / FinCount * Mathf.PI * 2f + Mathf.PI / 4f; // :171
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                Vector3 perp = new Vector3(-dir.y, dir.x, 0f);
                Vector3 center = dir * FinRadius + new Vector3(0, 0, FinZ);
                pieces.Add(ProceduralPropMesh.OrientedBox(center, dir * (FinRadial / 2f), perp * (FinTangent / 2f), new Vector3(0, 0, FinDepth / 2f)));
            }
            MagentaMesh = ProceduralPropMesh.Combine(pieces, "Fx_RocketNoseFins");

            BodyMat = MakeMetal(new Color(0x9f / 255f, 0xb6 / 255f, 0xc4 / 255f), 0.6f, 0.35f); // :149
            MagentaMat = MakeMetal(new Color(0xd4 / 255f, 0x2e / 255f, 0x6e / 255f), 0.3f, 0.45f); // :150

            var sh = Shader.Find("TbilisiFly/ImpactSprite");
            if (sh != null)
            {
                GlowMat = new Material(sh) { enableInstancing = true };
                GlowMat.SetFloat("_AlphaMul", 0.85f);
                GlowMat.SetFloat("_EdgeLo", 0.5f);
                GlowMat.SetFloat("_EdgeHi", 0.05f);
                GlowMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One); // additive, :174-177
            }
        }

        static Material MakeMetal(Color c, float metallic, float roughness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "Fx_RocketMetal" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - roughness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 1f - roughness);
            return m;
        }

        readonly Matrix4x4[] _glowScratch = new Matrix4x4[16]; // MaxLive=6, plenty of headroom

        /// <summary>Call once per frame, after RocketLauncher's own Update() has
        /// moved every live rocket.</summary>
        public void Render(IReadOnlyList<RocketLauncher.Rocket> live)
        {
            if (live.Count == 0) return;
            for (int i = 0; i < live.Count; i++)
            {
                var rk = live[i];
                Quaternion rot = rk.Dir.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(rk.Dir) : Quaternion.identity;
                var m = Matrix4x4.TRS(rk.Pos, rot, Vector3.one);
                Graphics.DrawMesh(BodyMesh, m, BodyMat, 0);
                Graphics.DrawMesh(MagentaMesh, m, MagentaMat, 0);
                if (GlowMat != null && i < _glowScratch.Length)
                {
                    Vector3 glowPos = rk.Pos + rot * new Vector3(0, 0, GlowZ);
                    _glowScratch[i] = Matrix4x4.TRS(glowPos, Quaternion.identity, Vector3.one * GlowScale);
                }
            }
            if (GlowMat != null)
            {
                int n = Mathf.Min(live.Count, _glowScratch.Length);
                // life=0 (tint.a) keeps the glow fully opaque every frame (no
                // fade) — a warm orange instance tint standing in for the JS's
                // 4-stop gradient, see this class's header.
                for (int i = 0; i < n; i++)
                    Graphics.DrawMesh(InstancedSpriteBatch.UnitQuad, _glowScratch[i], GlowMat, 0, null, 0, BuildMpb());
            }
        }

        MaterialPropertyBlock _mpb;
        MaterialPropertyBlock BuildMpb()
        {
            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetVector("_Tint", new Vector4(1f, 0.6f, 0.35f, 0f));
            return _mpb;
        }

        public void Dispose()
        {
            if (BodyMesh != null) UnityEngine.Object.Destroy(BodyMesh);
            if (MagentaMesh != null) UnityEngine.Object.Destroy(MagentaMesh);
            if (BodyMat != null) UnityEngine.Object.Destroy(BodyMat);
            if (MagentaMat != null) UnityEngine.Object.Destroy(MagentaMat);
            if (GlowMat != null) UnityEngine.Object.Destroy(GlowMat);
        }
    }
}
