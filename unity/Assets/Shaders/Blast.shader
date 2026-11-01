// Blast.shader — THE boiling fireball: a 3D-noise-displaced icosahedron, not
// a sprite, not a flat tint. Covers two sources that share one formula family:
//
//   default (no keyword)  — src/blast.js:41-92 makeFireballMaterial — the
//                            rocket/building blast fireball pool (Combat/Blast.cs's
//                            BlastFx.Balls[], FbMax=7).
//   _CRASH                — src/crash.js:17-81 makeFireballMaterial — the plane
//                            crash fireball, reused here rather than re-
//                            duplicated (Core/CrashSequence.cs's ApplyFireballLook,
//                            the one method of that file this port may touch,
//                            wires this shader in). Same formula shape, its own
//                            measured constants (fewer octaves, no soot mottling,
//                            a steeper late fade) — selected by keyword rather
//                            than forked into a second file, since it is
//                            genuinely the same shader with different tuning,
//                            not a different effect.
//
// THE FIREBALL BOILS (quality bar): the surface is displaced along its own
// normals by two scales of 3D fbm turbulence rolling upward over time — big
// lobes plus a finer break-up — so the silhouette TEARS instead of inflating
// like a balloon.
//
// ALPHA-BLENDED, NOT ADDITIVE (quality bar / a recorded bug): an additive ball
// over Tbilisi's bright sky washes out to white cream under ACES tonemapping;
// alpha blending is what keeps the orange saturated. `Cull Front` matches the
// JS's `side: THREE.FrontSide` (both files) exactly.
//
// Pooled, not per-shot: Combat/Blast.cs's BlastFx owns a fixed pool of 7
// Fireball records (FbMax) and Scripts/Fx/BlastRenderer.cs creates exactly 7
// Material instances of this shader ONCE, round-robined the same way the pool
// itself is — never one Material per explosion.
Shader "TbilisiFly/Blast"
{
    Properties
    {
        _T("Progress (0..1)", Range(0,1)) = 0
        _Seed("Seed", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Front // src/blast.js:48 / src/crash.js — side: THREE.FrontSide
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _CRASH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _T;
                float _Seed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float vD : TEXCOORD0; };

            // ---- src/blast.js:17-39 NOISE3 / src/crash.js:17-39 FIREBALL_GLSL — identical hash/noise, ported once ----
            float H3(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }
            float N3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(H3(i), H3(i + float3(1, 0, 0)), f.x),
                         lerp(H3(i + float3(0, 1, 0)), H3(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(H3(i + float3(0, 0, 1)), H3(i + float3(1, 0, 1)), f.x),
                         lerp(H3(i + float3(0, 1, 1)), H3(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }
            float Fbm3(float3 p)
            {
                float v = 0.0;
                float a = 0.55;
                #if defined(_CRASH)
                    const int OCT = 3; // src/crash.js:33
                #else
                    const int OCT = 4; // src/blast.js:33
                #endif
                UNITY_UNROLL
                for (int k = 0; k < OCT; k++)
                {
                    v += a * N3(p);
                    #if defined(_CRASH)
                        p = p * 2.13 + float3(9.7, 3.1, 7.3); // src/crash.js:35
                    #else
                        p = p * 2.11 + float3(9.7, 3.1, 7.3); // src/blast.js:35
                    #endif
                    a *= 0.5;
                }
                return v;
            }

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                float3 n = normalize(IN.normalOS);
                #if defined(_CRASH)
                    // src/crash.js:55-57 — one noise channel, boiling upward.
                    float d = Fbm3(n * 2.2 + float3(_Seed, _Seed, _Seed) + float3(0.0, -_T * 1.6, 0.0));
                    OUT.vD = d;
                    float3 p = IN.positionOS.xyz * (1.0 + (d - 0.5) * 0.95);
                #else
                    // src/blast.js:60-65 — two scales of churn: big lobes plus a finer break-up.
                    float d = Fbm3(n * 2.6 + float3(_Seed, _Seed, _Seed) + float3(0.0, -_T * 2.2, 0.0));
                    float fine = Fbm3(n * 7.5 + float3(_Seed * 1.7, _Seed * 1.7, _Seed * 1.7) + float3(0.0, -_T * 3.4, 0.0));
                    d = d * 0.78 + fine * 0.34;
                    OUT.vD = d;
                    float3 p = IN.positionOS.xyz * (1.0 + (d - 0.5) * (0.95 + 0.55 * _T));
                #endif
                OUT.positionCS = TransformObjectToHClip(p);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float vD = IN.vD;
                #if defined(_CRASH)
                    // src/crash.js:66-78 — deep red rim -> orange body -> a white-hot core
                    // that only survives the first instants; steep late fade with a hard
                    // 3/4-life cutoff so the ball hands over to black smoke cleanly.
                    float heat = saturate(vD * 1.3 - _T * 1.15 + 0.25);
                    float3 c = lerp(float3(0.22, 0.012, 0.001), float3(0.95, 0.16, 0.006), smoothstep(0.1, 0.62, heat));
                    c = lerp(c, float3(1.0, 0.5, 0.1), smoothstep(0.8, 1.0, heat) * (1.0 - _T));
                    float body = smoothstep(0.14, 0.45, vD + 0.3 * (1.0 - _T));
                    float a = min(pow(1.0 - _T, 2.2) * body * 2.2, 0.97);
                    a *= 1.0 - smoothstep(0.5, 0.75, _T);
                #else
                    // src/blast.js:76-87 — biased COOL on purpose (ACES desaturates near
                    // white): only a small core in the first instants is allowed past
                    // orange, plus soot mottling on the cooling side as black smoke is born.
                    float heat = clamp(vD * 1.3 - _T * 1.5 - 0.06, 0.0, 1.0);
                    float3 c = lerp(float3(0.05, 0.032, 0.026), float3(0.55, 0.06, 0.004), smoothstep(0.02, 0.34, heat));
                    c = lerp(c, float3(0.93, 0.26, 0.015), smoothstep(0.34, 0.6, heat));
                    c = lerp(c, float3(1.0, 0.58, 0.09), smoothstep(0.6, 0.85, heat));
                    c = lerp(c, float3(1.0, 0.88, 0.52), smoothstep(0.88, 1.0, heat) * max(0.0, 1.0 - _T * 3.0));
                    float soot = smoothstep(0.35, 0.0, heat) * smoothstep(0.25, 0.9, _T);
                    c = lerp(c, float3(0.08, 0.075, 0.07), soot * 0.85);
                    float body = smoothstep(0.30, 0.52, vD + 0.30 * (1.0 - _T));
                    float a = min(pow(1.0 - _T, 1.55) * body * 3.0, 0.99);
                    a *= 1.0 - smoothstep(0.55, 0.9, _T);
                #endif
                if (a < 0.02) discard;
                return half4(c, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
