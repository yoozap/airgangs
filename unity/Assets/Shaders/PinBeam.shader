// PinBeam.shader — src/game.js:2672-2699 `_syncPinBeams`'s beacon material
// (game.js:2683-2691): a flat THREE.MeshBasicMaterial, additive, transparent,
// no depth write, double-sided, unfogged. Handwritten rather than URP Lit/
// Unlit + runtime _Surface/_Blend property pokes (see Scripts/UI/Map/
// PinBeams.cs header for why) — same pattern as Shaders/MuzzleFlash.shader's
// plain quad pass, just without the billboard/UV work this one doesn't need.
Shader "TbilisiFly/PinBeam"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.8235, 0.3412, 0.16) // game.js:2684 0xffd257 @ 0.16 opacity
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off    // game.js:2689 side: THREE.DoubleSide
        ZWrite Off  // game.js:2688 depthWrite: false
        Blend SrcAlpha One // game.js:2687 THREE.AdditiveBlending's real blend func

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // game.js:2690 fog: false — no fog sampling here, so this is it.
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
