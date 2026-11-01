// BulletStreak.shader — src/ballistics.js:465-470's rounds InstancedMesh
// material: a flat THREE.MeshBasicMaterial (color 0xb9b2a4, transparent,
// opacity 0.5, depthWrite: false, toneMapped: false). Handwritten rather
// than URP Lit/Unlit + runtime _Surface/_Blend property pokes — same
// precedent as Shaders/PinBeam.shader's header for why a flat unlit colour
// quad/mesh gets its own tiny shader instead.
//
// Geometry itself (the tapered 5-sided cylinder, radiusTop 0.013 / radiusBottom
// 0.008 / height 1, src/ballistics.js:466) is built in C# by
// Scripts/Fx/BulletTrailFx.cs — this shader only needs a flat, unlit,
// alpha-blended fill with GPU instancing for Graphics.DrawMeshInstanced
// (Scripts/Fx/InstancedSpriteBatch.cs's shared batching helper).
Shader "TbilisiFly/BulletStreak"
{
    Properties
    {
        _Color("Color", Color) = (0.7255, 0.698, 0.6431, 0.5) // src/ballistics.js:468 0xb9b2a4 @ 0.5 opacity
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off    // winding of the hand-built cylinder isn't guaranteed camera-consistent; the JS has no culling opinion either (MeshBasicMaterial default side: THREE.FrontSide, but these are thin streaks where backface loss would read as flicker)
        ZWrite Off  // src/ballistics.js:469 depthWrite: false
        Blend SrcAlpha OneMinusSrcAlpha // src/ballistics.js:468 transparent (THREE's default NormalBlending)

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // src/ballistics.js:469 toneMapped: false — flat colour, no ACES/exposure.
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
