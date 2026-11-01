// PropDisc.shader — the propeller BLUR DISC quad, src/propeller.js's
// `disc` object (a CircleGeometry + the canvas texture from `blurDisc()`,
// Flight/PropellerSystem.cs's BuildBlurDiscTexture — already ported pure
// pixel math). A plain ORIENTED quad (the pivot's own Transform IS the
// pose — PropellerSystem.FinishPropeller already computed DiscLocalRotation
// so local +Z sits on the spin axis), not camera-facing, so this does NOT
// reuse Shaders/ImpactSprite.shader's `_FLAT` mode: that mode's colour comes
// from a GPU-instanced per-draw `_Tint` property (InstancedSpriteBatch's
// own convention), where Flight/PropellerSystem.cs's SpinPropellers expects
// to drive a single ordinary `Material.color` directly
// (`p.DiscMaterial.color = c` with `c.a` the blur fraction) — the same
// plain-CBUFFER-`_Color` convention Shaders/MuzzleFlash.shader uses.
//
// Standard alpha blend, NOT additive (a spinning disc occludes the sky
// behind it; it does not glow) — the one deliberate difference from this
// project's other fixed-function effect shaders.
Shader "TbilisiFly/PropDisc"
{
    Properties
    {
        _MainTex("Blur Disc", 2D) = "white" {}
        _Color("Color", Color) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

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

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half a = tex.a * _Color.a;
                if (a < 0.004) discard;
                return half4(tex.rgb * (1 - _Color.a) + _Color.rgb * _Color.a, a); // dark disc tint over the painted streaks
            }
            ENDHLSL
        }
    }
    FallBack Off
}
