// GlassGlint.shader — port of src/glass.js:124-151's inline THREE.Points
// ShaderMaterial: the additive flash that rides the first instant of a window
// bursting. The one bit of this file's scope that genuinely needs hand-written
// HLSL rather than a stock URP material (see Combat/BreakingGlass.cs's class
// header for why the shards themselves do not).
//
// NOT A GL_POINTS SPRITE. The JS drives gl_PointSize per-vertex from `life` and
// screen distance — WebGL/GLSL's point-size-from-shader trick. HLSL/URP has no
// reliably cross-platform equivalent (PSIZE output is fragile outside desktop
// GL), so this is a CAMERA-FACING BILLBOARD QUAD instead: the renderer feeds one
// unit quad through GPU instancing (Graphics.DrawMeshInstanced), one instance
// per live glint, with per-instance color.a carrying `1 - life` (set from
// Combat/BreakingGlass.cs's KLife) and per-instance transform scale carrying the
// JS's `(0.5 - 0.35*life)` size falloff — same visual result (a flash that
// shrinks and fades), same per-particle data, different primitive.
Shader "TbilisiFly/GlassGlint"
{
    Properties
    {
        _Tint("Tint", Color) = (0.85, 0.96, 1.1, 1)
        _Intensity("Intensity", Float) = 2.2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        Blend One One // additive — src/glass.js:127 THREE.AdditiveBlending

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Intensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION; // a unit quad, centred on the origin
                float2 uv : TEXCOORD0;
                float4 color : COLOR;         // per-instance: a = 1 - life (CPU-driven)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float alpha : TEXCOORD1;
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                // Billboard: take the instance's world position (object pivot) and offset
                // in VIEW space by the quad's local XY, so it always faces the camera —
                // the standard camera-facing-quad technique.
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                // object scale carries the glint's current size (JS: (0.5 - 0.35*life) * falloff)
                float3 scale = float3(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21), 1);
                // `float3 * float2` does not compile — scale each view axis by its OWN
                // component instead of trying to multiply the summed float3 by a float2
                // (found by the first GPU render of the port, 2026-10-30; the shader had
                // never compiled, so no glass glint has ever been drawn).
                float3 worldPos = centerWS + right * (IN.positionOS.x * scale.x) + up * (IN.positionOS.y * scale.y);
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                OUT.alpha = IN.color.a;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // src/glass.js:144-150 — circular falloff from the quad centre, discard
                // outside the disc, additive glow scaled by (1 - distance) * (1 - life).
                float d = distance(IN.uv, float2(0.5, 0.5)) * 2.0;
                if (d > 1.0) discard;
                float a = (1.0 - d) * IN.alpha;
                if (a < 0.02) discard;
                return half4(_Tint.rgb * _Intensity * a, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
