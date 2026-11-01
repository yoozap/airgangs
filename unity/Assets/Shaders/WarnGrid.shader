// WarnGrid.shader — src/game.js:408-414's crash-warning grid material: a flat
// THREE.LineBasicMaterial (from `new THREE.GridHelper(240, 24, red, red)`),
// transparent, depth-write off, opacity driven per-frame by Core/WarnGrid.cs
// (game.js:2478-2480 `this.warnGrid.material.opacity = strength * 0.35 * pulse`).
// Handwritten rather than URP Unlit + runtime _Surface/_Blend property pokes —
// see Scripts/UI/Map/PinBeams.cs's header for why that trap doesn't work; same
// shape as Shaders/PinBeam.shader, just NORMAL (not additive) blending, which
// is THREE's own default blending mode for an untouched LineBasicMaterial.
Shader "TbilisiFly/WarnGrid"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.1019, 0.0627, 0) // game.js:409 0xff1a10, opacity driven at runtime
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off  // game.js:412 depthWrite: false
        Blend SrcAlpha OneMinusSrcAlpha // THREE's default NormalBlending

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
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
