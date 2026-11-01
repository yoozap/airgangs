// ImpactSprite.shader — ONE generic camera-facing point-sprite shader covering
// every "soft circular particle" GL_POINTS ShaderMaterial in the remaining
// scope, instead of eight near-duplicate .shader assets. Consolidation is
// deliberate: all of the sources below differ only in (a) which TWO colours
// they lerp between, (b) which axis they lerp by (particle LIFE, or distance
// from the sprite's own centre), (c) a soft/hard edge, and (d) a brightness
// multiplier — so one parametrised shader with 4 small keyworded fragment
// branches (the 2 genuinely distinct shapes get their own keyword; everything
// else is the shared default) reproduces all of them exactly, citing its own
// source lines, rather than inventing a shared look:
//
//   default (no keyword)      — soft/hard circular falloff, colour from
//                                EITHER a per-instance tint OR a life-lerp:
//     · src/smoke.js:22-38        SmokeTrail (plane-crash smoke puffs)
//     · src/rockets.js:36-57      RocketTrail (propellant trail)
//     · src/guns.js:189-211       ShardBurst (glass shards, additive, hard edge)
//     · src/guns.js:312-333       ImpactDust (bullet-strike dust)
//     · src/impactfx.js:450-480   ParticleCloud (Combat/ImpactFx.cs's Debris/Sparks)
//   _FX_EMBER                  — src/fire.js:135-159  (embers riding the heat column)
//   _FX_GLOW                   — src/fire.js:349-372  (persistent per-window fire glow)
//   _FX_FLASH                  — src/fire.js:260-292  (explosion flash burst)
//   _FX_SMOKE                  — src/fire.js:189-242  (real sprite-atlas smoke plumes)
//
// _FLAT (orthogonal to the above — a VERTEX-stage toggle) swaps the camera-
// facing billboard for a plain oriented quad, for the handful of effects in
// this scope that are bare MeshBasicMaterial in the JS (no GLSL at all) but
// still want a simple, KNOWN-CORRECT transparent shader rather than the
// stock URP Lit/Unlit's surface-type/blend-mode keyword machinery (which
// needs its keywords, render queue AND blend state switched together in
// lockstep to actually go transparent — see Core/CrashSequence.cs's own
// MakeMat() comment for this exact, previously-hit pitfall in this project):
// src/fire.js:298-330's shockwave rings/scorch decals and src/blast.js:187-
// 220's pressure rings/craters all use this mode from Scripts/Fx/FireSystem.cs
// and Scripts/Fx/BlastRenderer.cs, each with its own simple per-draw colour.
//
// Billboarding is the same camera-facing-quad technique Shaders/GlassGlint.shader
// established for this house (view-space right/up vectors, GPU instancing via
// Graphics.DrawMeshInstanced over a unit quad — see Scripts/Fx/InstancedSpriteBatch.cs)
// in place of WebGL's gl_PointSize trick, which HLSL/URP has no portable
// equivalent for.
Shader "TbilisiFly/ImpactSprite"
{
    Properties
    {
        _ColorA("Color A (life=0 / centre)", Color) = (1,1,1,1)
        _ColorB("Color B (life=1 / rim)", Color) = (0.3,0.3,0.3,1)
        _AlphaMul("Alpha Multiplier", Float) = 0.7
        _EdgeLo("Soft Edge Lo", Float) = 0.5
        _EdgeHi("Soft Edge Hi", Float) = 0.12
        _GlowParams("Glow Params (base, lifeTerm)", Vector) = (1,0,0,0)
        _MainTex("Sprite / Atlas (optional)", 2D) = "white" {}
        _UseTex("Use Texture (0/1)", Float) = 0
        [Toggle(_TINTMIX)] _TintMix("Mix ColorA/B by life instead of instance tint", Float) = 0
        [Toggle(_HARDEDGE)] _HardEdge("Hard circular cutoff (shards)", Float) = 0
        [Toggle(_FLAT)] _Flat("Flat (non-billboarded) decal mode", Float) = 0
        _SrcBlend("Src Blend", Float) = 5    // SrcAlpha
        _DstBlend("Dst Blend", Float) = 6    // OneMinusSrcAlpha (set to 1/One for additive materials)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _TINTMIX
            #pragma shader_feature_local _HARDEDGE
            #pragma shader_feature_local _FLAT
            #pragma shader_feature_local_fragment _ _FX_EMBER _FX_GLOW _FX_FLASH _FX_SMOKE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _ColorA, _ColorB;
                float _AlphaMul, _EdgeLo, _EdgeHi, _UseTex;
                float4 _GlowParams;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION; // unit quad, local range -0.5..0.5
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 tint : TEXCOORD1;  // rgb = instance tint, a = life
                float3 propsB : TEXCOORD2; // x = seed, y = dark, z = view distance (vFade input)
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)    // rgb = per-instance tint, a = life (0..1)
                UNITY_DEFINE_INSTANCED_PROP(float4, _PropsA)  // x = seed, y = dark (smoke dust<->soot), zw unused
            UNITY_INSTANCING_BUFFER_END(Props)

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                OUT.tint = UNITY_ACCESS_INSTANCED_PROP(Props, _Tint);
                float4 propsA = UNITY_ACCESS_INSTANCED_PROP(Props, _PropsA);

                float3 worldPos;
                #if defined(_FLAT)
                    // Flat (non-billboarded) decal mode: a real oriented quad lying
                    // against a surface — Scripts/Fx/FireSystem.cs's shockwave rings
                    // and scorch decals, Scripts/Fx/BlastRenderer.cs's craters (all
                    // plain MeshBasicMaterial in the JS, src/fire.js:298-330,
                    // src/blast.js:187-220 — no camera-facing here, the caller's own
                    // transform IS the pose).
                    worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                #else
                    // Camera-facing billboard quad — Shaders/GlassGlint.shader's technique.
                    float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                    float3 right = UNITY_MATRIX_V[0].xyz;
                    float3 up = UNITY_MATRIX_V[1].xyz;
                    float3 scale = float3(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21), 1);
                // scale per axis: a float3 basis cannot be multiplied by a float2,
                // and truncating to .x would silently drop the vertical scale.
                    worldPos = centerWS + (right * (IN.positionOS.x * scale.x) + up * (IN.positionOS.y * scale.y));
                #endif
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                // view-space distance, for FX_SMOKE's vFade (src/fire.js:210-213); harmless
                // elsewhere (unused unless _FX_SMOKE is enabled).
                float3 posVS = TransformWorldToView(worldPos);
                OUT.propsB = float3(propsA.x, propsA.y, max(1.0, -posVS.z));
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                #if defined(_FLAT)
                    // Flat decal: a plain solid-alpha quad, no falloff shape, no
                    // camera-facing math — the colour/opacity IS the caller's own
                    // per-draw _Tint (see the Vert-stage note above).
                    half a0 = IN.tint.a;
                    if (a0 < 0.004) discard;
                    return half4(IN.tint.rgb, a0);
                #endif

                float life = IN.tint.a;
                float seed = IN.propsB.x;
                float dark = IN.propsB.y;
                float viewDist = IN.propsB.z;
                float2 uv = IN.uv;
                float d = distance(uv, float2(0.5, 0.5)) * 2.0;

                #if defined(_FX_EMBER)
                    // src/fire.js:150-159 — hot spark riding the heat column.
                    if (d > 1.0) discard;
                    float3 c = lerp(float3(1.0, 0.85, 0.4), float3(1.0, 0.25, 0.02), life);
                    float a = (1.0 - d) * (1.0 - life);
                    if (a < 0.02) discard;
                    return half4(c * 2.0, a);
                #elif defined(_FX_GLOW)
                    // src/fire.js:361-372 — persistent flicker-driven window glow.
                    if (d > 1.0) discard;
                    float3 c = lerp(_ColorA.rgb, _ColorB.rgb, smoothstep(0.0, 0.8, d));
                    float a = (1.0 - d * d) * 0.8;
                    if (a < 0.015) discard;
                    return half4(c * 1.5, a);
                #elif defined(_FX_FLASH)
                    // src/fire.js:281-292 — big soft additive explosion burst.
                    half4 tex = _UseTex > 0.5 ? SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) : half4(1, 1, 1, 1);
                    float mask = _UseTex > 0.5 ? 1.0 : saturate(1.0 - d);
                    float a = tex.a * mask * (1.0 - life) * (1.0 - life);
                    if (a < 0.015) discard;
                    float3 c = lerp(float3(1.0, 0.95, 0.8), float3(1.0, 0.45, 0.08), smoothstep(0.05, 0.55, life));
                    c = lerp(c, float3(0.6, 0.16, 0.03), smoothstep(0.55, 1.0, life));
                    return half4(c * 2.2 * (_UseTex > 0.5 ? tex.r : 1.0), a);
                #elif defined(_FX_SMOKE)
                    // src/fire.js:218-242 — real sprite-atlas smoke, slow tumble + 2x2 atlas pick.
                    float ang = seed * 6.283 + _Time.y * 0.35 * (frac(seed * 3.7) - 0.5) * 2.0;
                    float ca = cos(ang), sa = sin(ang);
                    float2 p = uv - 0.5;
                    float2 rp = float2(p.x * ca - p.y * sa, p.x * sa + p.y * ca) * 0.98 + 0.5;
                    rp = saturate(rp);
                    float vFade = smoothstep(5.0, 18.0, viewDist);
                    half4 tex;
                    if (_UseTex > 0.5)
                    {
                        float2 cell = float2(fmod(floor(seed * 4.0), 2.0), fmod(floor(seed * 2.0), 2.0));
                        tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, (rp + cell) * 0.5);
                    }
                    else
                    {
                        tex = half4(1, 1, 1, saturate(1.0 - d)); // procedural soft puff when no atlas is bound
                    }
                    float a = tex.a * (1.0 - life) * (0.5 + 0.4 * smoothstep(0.0, 0.1, life)) * vFade;
                    if (a < 0.02) discard;
                    float3 dust = float3(0.55, 0.5, 0.43);
                    float3 soot = float3(0.10, 0.095, 0.09);
                    float3 c = lerp(dust, soot, dark) * (1.0 - life * 0.2) * (0.75 + 0.5 * tex.r);
                    return half4(c, a);
                #else
                    // Shared default: smoke.js/rockets.js/guns.js's ShardBurst+ImpactDust/
                    // impactfx.js's ParticleCloud — a soft (or, for shards, hard-cut)
                    // circular sprite, coloured either by the caller's per-instance tint
                    // (impactfx.js:472-480's vTint) or by a life lerp between two material
                    // colours (smoke.js/rockets.js/guns.js's 2-stop gradients).
                    #if defined(_HARDEDGE)
                        if (d > 1.0) discard;
                        float a = (1.0 - d) * (1.0 - life) * _AlphaMul;
                    #else
                        float a = smoothstep(_EdgeLo, _EdgeHi, d) * (1.0 - life) * _AlphaMul;
                    #endif
                    if (a < 0.01) discard;
                    #if defined(_TINTMIX)
                        float3 c = lerp(_ColorA.rgb, _ColorB.rgb, life);
                    #else
                        float3 c = IN.tint.rgb;
                    #endif
                    c *= (_GlowParams.x + _GlowParams.y * (1.0 - life));
                    return half4(c, a);
                #endif
            }
            ENDHLSL
        }
    }
    FallBack Off
}
