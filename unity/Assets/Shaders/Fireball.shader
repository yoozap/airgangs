// Fireball.shader — port of src/fire.js's FLAME billboard (fire.js:40-118),
// the single most distinctive of the 5 ShaderMaterials fire.js owns (the other
// 4 — embers/smoke/flashes/glows — are generic point-sprite shapes and are
// covered by the shared Shaders/ImpactSprite.shader instead; see
// Scripts/Fx/FireSystem.cs's class header for the full one-file-per-site map).
//
// THE FIREBALL BOILS (quality bar): a licking, curling flame body carved out
// of a 2D domain-warped fbm turbulence field (fire.js:17-38 FIRE_NOISE,
// ported verbatim below as FbmNoise), not a flat tint or a sprite-sheet flip.
//
// NOT A GL_POINTS SPRITE / NOT AN InstancedMesh's own vertex stream: fire.js
// drives this through instanceMatrix + a fixed 1x1 PlaneGeometry, which this
// port reproduces with Graphics.DrawMeshInstanced over a unit quad (built by
// Scripts/Fx/InstancedSpriteBatch.cs) and the SAME camera-facing-billboard
// technique Shaders/GlassGlint.shader already established for this house (view
// -space right/up vectors, instance scale carrying size) — chosen deliberately
// so every billboarded VFX shader in this project does the camera-facing math
// one, verified way, rather than reinventing gl_PointSize's perspective divide
// by hand per shader.
//
// Alpha-blended, NOT additive (fire.js:53-56 / quality bar): an additive flame
// over Tbilisi's bright hazy sky washes to pale cream under ACES tonemapping;
// alpha blending keeps the blackbody ramp saturated.
Shader "TbilisiFly/Fireball"
{
    Properties
    {
        _SrcBlend("Src Blend", Float) = 5   // SrcAlpha — three.js NormalBlending's own src factor (fire.js:52 default, not AdditiveBlending)
        _DstBlend("Dst Blend", Float) = 6   // OneMinusSrcAlpha
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; // unit quad, local range -0.5..0.5 — src/fire.js:47 PlaneGeometry(1,1)
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float seed : TEXCOORD1;
                float inten : TEXCOORD2;
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                // x = aSeed (fire.js:48/59), y = aInt (fire.js:49/64 vInt), zw unused.
                UNITY_DEFINE_INSTANCED_PROP(float4, _PropsA)
            UNITY_INSTANCING_BUFFER_END(Props)

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                float4 props = UNITY_ACCESS_INSTANCED_PROP(Props, _PropsA);
                OUT.seed = props.x;
                OUT.inten = props.y;

                // src/fire.js:70-74 — spherical (camera-facing) billboard, pivoted at
                // the flame BASE (the +0.5 bias on the vertical axis), breathing in
                // both width and height on two independent, out-of-phase sines so the
                // silhouette never looks metronomic.
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                // instance scale carries the JS's uniform `s` (fire.js:393 m.makeScale(s,s,s))
                float s = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float t = _Time.y;
                float w = s * (1.0 + 0.07 * sin(t * 11.0 + OUT.seed * 31.0));
                float h = s * 2.1 * (1.0 + 0.11 * sin(t * 8.7 + OUT.seed * 17.0));
                float3 worldPos = centerWS + right * (IN.positionOS.x * w) + up * ((IN.positionOS.y + 0.5) * h);
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                return OUT;
            }

            // ---- src/fire.js:17-38 FIRE_NOISE, ported verbatim ----
            float FbmHash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float FbmNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(FbmHash(i), FbmHash(i + float2(1, 0)), f.x),
                    lerp(FbmHash(i + float2(0, 1)), FbmHash(i + float2(1, 1)), f.x), f.y);
            }
            float Fbm(float2 p)
            {
                float v = 0.0;
                float a = 0.55;
                UNITY_UNROLL
                for (int k = 0; k < 3; k++)
                {
                    v += a * FbmNoise(p);
                    p = p * 2.13 + float2(11.3, 7.7);
                    a *= 0.5;
                }
                return v;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // src/fire.js:84-112, verbatim.
                float t = _Time.y * (1.05 + frac(IN.seed * 7.31) * 0.4) + IN.seed * 39.0;
                float2 q = float2(IN.uv.x * 2.6 + IN.seed * 13.0, IN.uv.y * 3.1 - t * 2.1);
                float w = Fbm(q + float2(0.0, t * 0.35));
                float n = Fbm(q + float2(w * 1.1, w * 0.8));
                float cx = abs(IN.uv.x - 0.5);
                float body = 1.0 - smoothstep(0.02, 0.5 - 0.3 * IN.uv.y, cx);
                body *= smoothstep(0.0, 0.06, IN.uv.y) * (1.0 - smoothstep(0.6, 1.0, IN.uv.y));
                float f = body * 1.5 - (1.0 - n) * (0.55 + 0.85 * IN.uv.y);
                f *= 0.8 + 0.4 * IN.inten;
                float a = smoothstep(0.03, 0.32, f);
                if (a < 0.02) discard;
                f = clamp(f * 0.72, 0.0, 1.0);
                float3 c = float3(0.52, 0.04, 0.004);
                c = lerp(c, float3(0.94, 0.24, 0.015), smoothstep(0.10, 0.42, f));
                c = lerp(c, float3(1.0, 0.55, 0.06), smoothstep(0.5, 0.8, f));
                c = lerp(c, float3(1.0, 0.86, 0.45), smoothstep(0.9, 1.0, f));
                return half4(c, a * (0.55 + 0.45 * smoothstep(0.05, 0.5, f)));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
