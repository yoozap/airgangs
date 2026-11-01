using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders src/impactfx.js's ParticleCloud (Combat/ImpactFx.cs's Debris/Sparks)
    /// AND src/guns.js's ShardBurst/ImpactDust (Combat/GunFx.cs) — the three
    /// "soft circular particle" GL_POINTS shaders in this project's remaining
    /// scope, none of which ever got a Unity renderer (confirmed by grep before
    /// this pass: zero "new ImpactParticleFx" anywhere).
    ///
    /// MECHANISM CHOICE for the handed-over blocker ("Shaders/ImpactParticle.shader's
    /// instanced property is _TintLife, Fx/InstancedSpriteBatch.cs's shared Draw()
    /// hardcodes _Tint/_PropsA"): neither "extend the shared helper" nor "write a
    /// bespoke MaterialPropertyBlock path". Shaders/ImpactSprite.shader ALREADY
    /// exists, is ALREADY proven in production (Fx/FireSystem.cs's embers/smoke/
    /// flashes/glows all render through it today — Editor/Probes/
    /// CombatFxWireProbe's SMOKETRAIL line already proves Shader.Find resolves it),
    /// and its own header EXPLICITLY lists all three sites this class renders —
    /// "guns.js:189-211 ShardBurst", "guns.js:312-333 ImpactDust",
    /// "impactfx.js:450-480 ParticleCloud (Combat/ImpactFx.cs's Debris/Sparks)" —
    /// as cases its "default (no keyword)" fragment mode already covers. Its
    /// instanced property IS named `_Tint` (rgb = per-instance tint, a = life,
    /// 0..1) — the exact name AND semantics Fx/InstancedSpriteBatch.cs's Draw()
    /// already hardcodes and already documents in its own header. So
    /// Shaders/ImpactParticle.shader is an orphaned, never-wired duplicate of a
    /// case ImpactSprite.shader already generalises (built by an earlier pass
    /// before ImpactSprite.shader's consolidation existed, per its own docs/
    /// port_manifest.json claim) — this class renders through ImpactSprite.shader
    /// instead, with ZERO changes to InstancedSpriteBatch.cs, zero changes to any
    /// OTHER existing Fx consumer (Fx/FireSystem.cs, Fx/ImpactDecalFx.cs,
    /// Fx/RocketTrailFx/SmokeTrailFx), and zero new shader assets. (ImpactParticle.
    /// shader itself is left untouched — deleting it is a different agent's
    /// manifest bookkeeping, not this pass's call; see this pass's port_units note.)
    ///
    /// SIZE: camera-facing billboards carry their size as the instance's own
    /// uniform SCALE (Shaders/GlassGlint.shader's house technique) rather than a
    /// screen-space gl_PointSize — URP has no portable point-size primitive, same
    /// reasoning as every other Fx shader in this project. Combat/ImpactFx.cs's
    /// ParticleCloud.Size really is a METRES quantity (every Emit() call site
    /// passes `cal`-derived sizes, e.g. Strike()'s `size: cal * 1.8f`), so its
    /// grow-with-life curve (impactfx.js:457 `psize * (0.45 + 1.6*life)`) is
    /// ported EXACTLY, verbatim. Combat/GunFx.cs's ShardBurst/ImpactDust never
    /// carried a metres value in the JS at all (guns.js:198/321's own formulas
    /// are bare screen-pixel numbers with no physical-size input) — same
    /// documented precedent as Fx/FireSystem.cs's embers/glows (docs/port_units/
    /// fx.json: "a stated judgement-call base size carrying the JS's own
    /// growth/flicker SHAPE exactly"): each curve's SHAPE is kept exact,
    /// normalised to 1x at life=0, and scaled by a named judgement-call base.
    /// </summary>
    public sealed class ImpactParticleFx
    {
        // guns.js has no metres value for either of these two curves — see this
        // class's header. Chosen to read as a believable shard/dust-seed scale.
        public const float ShardSizeBase = 0.07f; // m, at life=0 (glinting glass shard)
        public const float DustSizeBase = 0.05f;  // m, at life=0 (a dust-puff seed)

        public readonly Material DebrisMat, SparksMat, ShardMat, DustMat;

        public ImpactParticleFx()
        {
            var sh = Shader.Find("TbilisiFly/ImpactSprite");
            // impactfx.js:472-480 ParticleCloud, glow=false (Combat/ImpactFx.cs's
            // Debris): smoothstep(0.5,0.12,d)*(1-life)*0.85, per-instance tint,
            // no colour boost, normal blend.
            DebrisMat = BuildMat(sh, edgeHi: 0.12f, alphaMul: 0.85f, glowBase: 1f, glowLife: 0f,
                tintMix: false, hardEdge: false, additive: false);
            // impactfx.js:472-480 ParticleCloud, glow=true (Combat/ImpactFx.cs's
            // Sparks): smoothstep(0.5,0.05,d)*(1-life) (no *0.85), tint *
            // (1.6 + 2.2*(1-life)), additive.
            SparksMat = BuildMat(sh, edgeHi: 0.05f, alphaMul: 1f, glowBase: 1.6f, glowLife: 2.2f,
                tintMix: false, hardEdge: false, additive: true);
            // guns.js:189-211 ShardBurst — a FIXED colour (0.75,0.92,1.05)*1.5 (no
            // per-instance tint array in the JS at all), hard circular cutoff,
            // *0.9 alpha, additive. ColorA==ColorB bakes the one constant colour
            // through _TINTMIX; _GlowParams=(1,0) avoids double-multiplying the
            // 1.5 already baked into the colour.
            ShardMat = BuildMat(sh, edgeHi: 0f, alphaMul: 0.9f, glowBase: 1f, glowLife: 0f,
                tintMix: true, hardEdge: true, additive: true,
                colorA: new Color(1.125f, 1.38f, 1.575f), colorB: new Color(1.125f, 1.38f, 1.575f));
            // guns.js:300-377 ImpactDust — smoothstep(0.5,0.1,d)*(1-life)*0.7,
            // colour a LIFE-lerp (never a per-instance tint in the JS), normal blend.
            DustMat = BuildMat(sh, edgeHi: 0.1f, alphaMul: 0.7f, glowBase: 1f, glowLife: 0f,
                tintMix: true, hardEdge: false, additive: false,
                colorA: new Color(0.36f, 0.33f, 0.29f), colorB: new Color(0.66f, 0.61f, 0.53f));
        }

        static Material BuildMat(Shader sh, float edgeHi, float alphaMul, float glowBase, float glowLife,
            bool tintMix, bool hardEdge, bool additive, Color? colorA = null, Color? colorB = null)
        {
            if (sh == null) return null;
            var m = new Material(sh) { enableInstancing = true };
            m.SetFloat("_EdgeLo", 0.5f);
            m.SetFloat("_EdgeHi", edgeHi);
            m.SetFloat("_AlphaMul", alphaMul);
            m.SetVector("_GlowParams", new Vector4(glowBase, glowLife, 0f, 0f));
            if (colorA.HasValue) m.SetColor("_ColorA", colorA.Value);
            if (colorB.HasValue) m.SetColor("_ColorB", colorB.Value);
            if (tintMix) m.EnableKeyword("_TINTMIX"); else m.DisableKeyword("_TINTMIX");
            if (hardEdge) m.EnableKeyword("_HARDEDGE"); else m.DisableKeyword("_HARDEDGE");
            m.DisableKeyword("_FLAT");
            // THREE.AdditiveBlending (SrcAlpha, One) vs NormalBlending (SrcAlpha,
            // OneMinusSrcAlpha) — Shaders/ImpactSprite.shader's _SrcBlend/_DstBlend,
            // same convention Fx/FireSystem.cs's MakeSpriteMat already established.
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One
                                                      : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            return m;
        }

        Matrix4x4[] _m = new Matrix4x4[InstancedSpriteBatch.MaxPerBatch];
        Vector4[] _t = new Vector4[InstancedSpriteBatch.MaxPerBatch];

        void Ensure(int n)
        {
            if (_m.Length < n) { _m = new Matrix4x4[n]; _t = new Vector4[n]; }
        }

        // impactfx.js:457 — ParticleCloud's own vertex-stage size curve, ported
        // exactly (psize already carries real metres — see this class's header).
        static float CloudSize(float psize, float life) => psize * (0.45f + 1.6f * life);

        void DrawCloud(ParticleCloud cloud, Material mat)
        {
            if (mat == null || cloud == null) return;
            Ensure(cloud.Max);
            int n = 0;
            for (int i = 0; i < cloud.Max; i++)
            {
                float life = cloud.Life[i];
                if (life >= 1f) continue; // impactfx.js:458-461 — a dead slot draws nothing
                float s = CloudSize(cloud.Size[i], life);
                _m[n] = Matrix4x4.TRS(cloud.Pos[i], Quaternion.identity, Vector3.one * s);
                var c = cloud.Tint[i];
                _t[n] = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, life);
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, mat, _m, _t, null, n);
        }

        // guns.js:198 ShardBurst's own curve, SHAPE preserved (normalised to 1x at
        // life=0), scaled by a named judgement-call base — see this class's header.
        static float ShardSize(float life) => ShardSizeBase * (0.35f - 0.15f * life) / 0.35f;
        // guns.js:321 ImpactDust's own curve, same treatment.
        static float DustSize(float life) => DustSizeBase * (2f + 18f * life) / 2f;

        void DrawShards(ShardBurst shards, Material mat)
        {
            if (mat == null || shards == null) return;
            Ensure(ShardBurst.Max);
            int n = 0;
            for (int i = 0; i < ShardBurst.Max; i++)
            {
                float life = shards.Life[i];
                if (life >= 1f) continue;
                float s = ShardSize(life);
                _m[n] = Matrix4x4.TRS(shards.Pos[i], Quaternion.identity, Vector3.one * s);
                _t[n] = new Vector4(0f, 0f, 0f, life); // _TINTMIX ignores rgb; alpha still drives (1-life)
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, mat, _m, _t, null, n);
        }

        void DrawDust(ImpactDust dust, Material mat)
        {
            if (mat == null || dust == null) return;
            Ensure(ImpactDust.Max);
            int n = 0;
            for (int i = 0; i < ImpactDust.Max; i++)
            {
                float life = dust.Life[i];
                if (life >= 1f) continue;
                float s = DustSize(life);
                _m[n] = Matrix4x4.TRS(dust.Pos[i], Quaternion.identity, Vector3.one * s);
                _t[n] = new Vector4(0f, 0f, 0f, life);
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, mat, _m, _t, null, n);
        }

        /// <summary>Call once per frame after Combat/ImpactFx.cs's ImpactFx.Update(dt)
        /// has run (Debris/Sparks already stepped) — same "simulate then render"
        /// convention as every other Fx consumer here.</summary>
        public void Render(ImpactFx fx)
        {
            if (fx == null) return;
            DrawCloud(fx.Debris, DebrisMat);
            DrawCloud(fx.Sparks, SparksMat);
        }

        /// <summary>Call once per frame after Combat/GunFx.cs's ShardBurst/ImpactDust
        /// Update(dt) has run (Combat/Guns.cs's MachineGun.Update already does this).</summary>
        public void Render(ShardBurst shards, ImpactDust dust)
        {
            DrawShards(shards, ShardMat);
            DrawDust(dust, DustMat);
        }

        public void Dispose()
        {
            Kill(DebrisMat); Kill(SparksMat); Kill(ShardMat); Kill(DustMat);
        }

        static void Kill(Material m)
        {
            if (m == null) return;
            UnityEngine.Object.DestroyImmediate(m);
        }
    }
}
