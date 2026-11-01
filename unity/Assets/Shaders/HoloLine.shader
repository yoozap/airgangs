Shader "TbilisiFly/HoloLine"
{
    // src/boundary.js — the blue holo border walls: LineSegments with a
    // LineBasicMaterial (cyan, transparent, AdditiveBlending, depthWrite off),
    // opacity driven per frame by boundary.update(). Lines topology, unlit,
    // additive: colour is premultiplied by the opacity the game writes.
    Properties
    {
        _Color ("Color", Color) = (0.247, 0.839, 1.0, 1.0)
        _Opacity ("Opacity", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "HoloLine"
            Blend One One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Opacity;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionHCS : SV_POSITION; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                return half4(_Color.rgb * _Opacity, _Opacity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
