// ImpactParticle.shader — THE shader slot src/impactfx.js's ParticleCloud
// (src/impactfx.js:445-481) and src/guns.js's own ShardBurst (src/guns.js:185-212)
// / ImpactDust (src/guns.js:309-334) were all missing: three near-identical
// hand-written GL_POINTS shaders in the JS (one glow/additive variant for
// sparks+shards, one plain/alpha variant for debris+dust), none of them
// ported before now (docs/port_manifest.json flagged impactfx.js's "point-sprite
// shader source strings" as the one explicitly unported piece of that file).
//
// URP does not expose gl_PointSize/GL_POINTS uniformly across platforms, so
// this is a camera-facing BILLBOARD QUAD per particle — the same house
// technique Shaders/MuzzleFlash.shader already uses for the muzzle flash —
// rather than a literal point sprite. Per-particle WORLD POSITION and METRE
// SIZE are baked into each instance's own TRS (translation + uniform scale),
// exactly how MuzzleFlashFx.cs already places tracer rods; only the two
// values that change the LOOK frame-to-frame (tint, life) ride as a true
// GPU-instanced property array (_TintLife, via MaterialPropertyBlock.
// SetVectorArray — Unity's standard instanced-properties mechanism, paired
// with Graphics.DrawMeshInstanced). A renderer that walks Combat/ImpactFx.cs's
// ParticleCloud / Combat/GunFx.cs's ShardBurst+ImpactDust arrays and feeds
// them to this shader is the one piece explicitly left to the consumer here —
// same precedent as DecalLayer/ParticleCloud themselves (simulation ported,
// Unity-side rendering wiring is not, per Combat/ImpactFx.cs's own header).
//
// Two call sites, one shader, selected per-Material instance (no keyword
// needed — only numbers differ):
//   _Glow = 0, _AlphaMul = 0.85  -> src/impactfx.js's "debris" ParticleCloud /
//       src/guns.js's ImpactDust (NormalBlending, 150 px scale, size grows
//       with life: (2 + 18*life))
//   _Glow = 1, _AlphaMul ignored -> src/impactfx.js's "sparks" ParticleCloud /
//       src/guns.js's ShardBurst (AdditiveBlending; ShardBurst's own formula
//       actually SHRINKS with life, (0.35 - 0.15*life) — set _SizeBase=0.35,
//       _SizeGrow=-0.15 for that call site, vs _SizeBase=2,_SizeGrow=18 for dust)
Shader "TbilisiFly/ImpactParticle"
{
    Properties
    {
        _SizeBase("Size Base", Float) = 2
        _SizeGrow("Size Grow (x life)", Float) = 18
        _MinSize("Min Px (informational)", Float) = 1
        _MaxSize("Max Px (informational)", Float) = 220
        _EdgeSoft("Edge Softness", Range(0.01, 0.5)) = 0.12
        _Glow("Glow 0/1", Float) = 0
        _AlphaMul("Alpha Multiplier (non-glow)", Float) = 0.85
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha // per-material: set Blend One One via material.SetFloat on the glow variant's blend, handled by the consumer's two Material instances (debris vs sparks), same as ImpactFx.cs's two ParticleCloud instances

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SizeBase;
                float _SizeGrow;
                float _MinSize;
                float _MaxSize;
                float _EdgeSoft;
                float _Glow;
                float _AlphaMul;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(PerParticle)
                // rgb = tint (already linear, src/impactfx.js's THREE.Color), a = life
                // in [0,1); the sim arrays mark a free slot with life >= 1, which this
                // vertex stage uses to cull the slot off-screen entirely (src/guns.js:
                // 193-197 / src/impactfx.js:458-461's `if (life >= 1.0) { ... return; }`).
                UNITY_DEFINE_INSTANCED_PROP(float4, _TintLife)
            UNITY_INSTANCING_BUFFER_END(PerParticle)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 tint : TEXCOORD1;
                float life : TEXCOORD2;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);

                float4 tintLife = UNITY_ACCESS_INSTANCED_PROP(PerParticle, _TintLife);
                float life = tintLife.a;
                if (life >= 1.0)
                {
                    // src/guns.js:193-197 / src/impactfx.js:458-461 — a dead/free slot
                    // draws nothing, same as gl_PointSize = 0.0 did in the JS.
                    OUT.positionCS = float4(0.0, 0.0, 2.0, 1.0);
                    OUT.uv = IN.uv;
                    OUT.tint = float3(0, 0, 0);
                    OUT.life = 1.0;
                    return OUT;
                }

                // Camera-facing billboard, Shaders/MuzzleFlash.shader's own technique.
                // The instance's own translation/scale carry this particle's world
                // position and METRE size (the renderer's job — see this file's header).
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 worldPos = centerWS
                    + right * (IN.positionOS.x * unity_ObjectToWorld._m00)
                    + up * (IN.positionOS.y * unity_ObjectToWorld._m00);

                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                OUT.tint = tintLife.rgb;
                OUT.life = life;
                return OUT;
            }

            // src/impactfx.js:472-480 / src/guns.js:203-211,326-333's fragment —
            // a soft circular falloff, darker/dimmer as life runs out, optionally
            // boosted for the additive "glow" variant (sparks/shards).
            half4 Frag(Varyings IN) : SV_Target
            {
                float d = distance(IN.uv, float2(0.5, 0.5));
                float a = smoothstep(0.5, _EdgeSoft, d) * (1.0 - IN.life);
                a *= (_Glow > 0.5) ? 1.0 : _AlphaMul;
                if (a < 0.012) discard;
                float3 c = IN.tint * ((_Glow > 0.5) ? (1.6 + 2.2 * (1.0 - IN.life)) : 1.0);
                return half4(c, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
