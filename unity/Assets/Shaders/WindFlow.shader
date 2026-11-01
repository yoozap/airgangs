// WindFlow.shader — src/crane.js:1068-1078's `wind_tunnel_flow` material: a
// MeshBasicMaterial(map, color:0xaecde6, vertexColors:true, transparent,
// AdditiveBlending, depthWrite:false, side:DoubleSide, toneMapped:false).
//
// Same reason as Shaders/PinBeam.shader and Shaders/MuzzleFlash.shader for
// NOT using URP's stock Unlit shader with runtime `_Surface`/`_Blend` property
// pokes: those two only take effect through the material INSPECTOR's GUI
// script, which never runs for a Material built from code — poking them alone
// leaves the sheets opaque. A tiny fixed-function additive shader sidesteps
// that trap the same way every other additive effect in this project does.
//
// _MainTex is Props/WindTunnel.cs's BuildHazeTexture() output; its UV SCROLL
// (crane.js:1926-1927 `tex.offset.x -= ...; tex.offset.y += ...`) is driven at
// runtime via Material.mainTextureOffset (Unity's own `_MainTex_ST.zw`,
// TRANSFORM_TEX below reads it the ordinary way — ordinary tiling/offset, no
// extra uniform needed). _Opacity is WindTunnel.Step's own flowOpacity output
// (crane.js:1923) and Vertex Color carries the per-vertex squeeze/edge-fade
// baked into Props/WindFlowMesh.cs's BuildSheet (crane.js:1044-1049).
Shader "TbilisiFly/WindFlow"
{
    Properties
    {
        _MainTex("Haze", 2D) = "white" {}
        _Color("Color", Color) = (0.682, 0.804, 0.902, 1) // 0xaecde6
        _Opacity("Opacity", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off     // side: THREE.DoubleSide
        ZWrite Off   // depthWrite: false
        Blend SrcAlpha One // THREE.AdditiveBlending's real blend func (see Shaders/MuzzleFlash.shader)

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _Opacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.color = IN.color;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a; // hazeTexture() is white RGB, alpha-only noise
                half3 c = tex * IN.color.rgb * _Color.rgb;
                half a = tex * IN.color.r * _Opacity; // vertex colour is greyscale (crane.js:1049 `col.push(c,c,c)`), .r carries it
                if (a < 0.003) discard;
                return half4(c * _Opacity, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
