// MuzzleFlash.shader — src/guns.js's two VFX sites that sit on the plane
// itself (fire dry, no distance modelling — src/guns.js:258-260):
//
//   default (no keyword) — the muzzle flash (guns.js:420-439): a canvas-drawn
//     radial gradient Sprite in the JS (white-hot centre -> amber -> transparent
//     orange, additive). Reproduced here procedurally (no PNG dependency) so
//     the look is identical without needing textures/fx/ imported. ONE quad,
//     ONE Material, reused and re-triggered every shot — Scripts/Fx/
//     MuzzleFlashFx.cs never allocates a new Material or GameObject per round
//     (see that file's header for how the JS's "36 shader recompiles = an
//     0.8s freeze" bug is avoided here).
//   _TRACER — the tracer rod (guns.js:441-456): in the JS a bare
//     MeshBasicMaterial box with no shader at all; given a shader here anyway
//     so GPU instancing (Graphics.DrawMeshInstanced, up to MachineGun.MaxTracers
//     concurrent rods) can carry a soft longitudinal fade at the tip/tail
//     instead of a hard-edged box silhouette — a small, deliberate
//     enhancement over the flat JS box, not a behaviour change (same colour
//     0xffce6a, same opacity 0.95, same additive blend, same depthWrite off).
Shader "TbilisiFly/MuzzleFlash"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.81, 0.42, 1)
        _Intensity("Intensity", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha One // src/guns.js:434 / :448 — THREE.AdditiveBlending's real blend func

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _TRACER

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity;
            CBUFFER_END

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
            };

            #if defined(_TRACER)
            // One instance per live tracer (MachineGun.Live), a real 3D rod —
            // ordinary object-space transform, no billboarding.
            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }
            half4 Frag(Varyings IN) : SV_Target
            {
                // Soft fade along the rod's length (UV.y spans the muzzle-to-tip run) —
                // the enhancement over the JS's flat box, see this file's header.
                float fade = 1.0 - abs(IN.uv.y - 0.5) * 2.0;
                fade = smoothstep(0.0, 0.55, fade);
                half a = 0.95 * fade;
                if (a < 0.015) discard;
                return half4(_Color.rgb * _Intensity, a);
            }
            #else
            // The single reused flash quad — camera-facing billboard (the house
            // technique, Shaders/GlassGlint.shader), rotated per shot by feeding a
            // pre-rotated quad mesh / instance roll (Scripts/Fx/MuzzleFlashFx.cs
            // applies guns.js:527's random roll as a Z-rotation on the instance
            // transform itself, so this shader needs no extra rotation input).
            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 scale = float3(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21), 1);
                // scale per axis: a float3 basis cannot be multiplied by a float2,
                // and truncating to .x would silently drop the vertical scale.
                float3 worldPos = centerWS + (right * (IN.positionOS.x * scale.x) + up * (IN.positionOS.y * scale.y));
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                return OUT;
            }
            half4 Frag(Varyings IN) : SV_Target
            {
                // src/guns.js:424-427's canvas radialGradient, ported as a procedural
                // 3-stop radial ramp instead of a sampled PNG: centre white-hot (1,1,0.92)
                // -> amber (1,0.78,0.35) @ 0.35 -> transparent orange (1,0.55,0.16) @ 1.0.
                float d = saturate(distance(IN.uv, float2(0.5, 0.5)) * 2.0);
                float3 c = lerp(float3(1.0, 1.0, 0.92), float3(1.0, 0.78, 0.35), smoothstep(0.0, 0.35, d));
                c = lerp(c, float3(1.0, 0.55, 0.16), smoothstep(0.35, 1.0, d));
                float a = saturate(1.0 - smoothstep(0.55, 1.0, d));
                if (a < 0.02) discard;
                return half4(c * _Color.rgb * _Intensity, a);
            }
            #endif
            ENDHLSL
        }
    }
    FallBack Off
}
