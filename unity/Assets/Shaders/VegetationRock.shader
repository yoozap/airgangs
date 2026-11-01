// VegetationRock.shader — src/citymap.js extractRock() (122-151): "A rock/stone
// model: merged geometry with a stone-grey vertex colour, lighter on top faces
// so it reads as lit stone. No atlas needed." The JS bakes a top/side gradient
// into per-vertex colour and renders with MeshLambertMaterial({ vertexColors:
// true, flatShading: true }) — no texture at all. City/VegetationRenderer.cs
// bakes the SAME top/side gradient into Mesh.colors (see its RockMesh helper)
// off the FBX-converted rock-N geometry; this shader is the render half: pure
// per-vertex-colour Lambert + a little Blinn-Phong, no albedo map.
//
// WHY NOT REUSE Shaders/CityWall.shader (which already samples vertex colour):
// that file's Attributes struct has no UNITY_VERTEX_INPUT_INSTANCE_ID / its
// Vert() never calls UNITY_SETUP_INSTANCE_ID — under classic GPU instancing
// (Graphics.RenderMeshInstanced, what every City/ renderer here uses) that
// leaves unity_InstanceID at 0 for every instance in a draw call, so
// GetObjectToWorldMatrix() would return the SAME (first) transform for all
// of them instead of each instance's own — every rock in a chunk would stack
// on top of the first one. Flagged for the CityWall.shader owner separately;
// not edited here (out of this file's ownership). This shader starts from
// Shaders/WindFoliage.shader's (correctly instanced) Attributes/Vert shape
// instead, minus the wind displacement — rocks do not sway.
Shader "TbilisiFly/VegetationRock"
{
    Properties
    {
        _Metallic("Metallic", Range(0,1)) = 0.0
        _Smoothness("Smoothness", Range(0,1)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Metallic;
                float _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                float fogCoord    : TEXCOORD3;
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.color = IN.color;
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half3 AccumulateLight(Light light, float3 N, float3 V, half3 diffuseColor, half3 f0, half shininess)
            {
                half nDotL = saturate(dot(N, light.direction));
                half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation) * nDotL;
                half3 diff = diffuseColor * radiance;
                float3 h = normalize(light.direction + V);
                half spec = pow(saturate(dot(N, h)), shininess) * nDotL;
                return diff + f0 * spec * radiance;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                half3 albedo = IN.color.rgb; // the baked top/side stone gradient — no texture
                half3 f0 = lerp(half3(0.04, 0.04, 0.04), albedo, _Metallic);
                half3 diffuseColor = albedo * (1 - _Metallic);
                half shininess = exp2(10 * _Smoothness + 1);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 color = albedo * SampleSH(normalWS);
                color += AccumulateLight(mainLight, normalWS, viewWS, diffuseColor, f0, shininess);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light addLight = GetAdditionalLight(lightIndex, IN.positionWS);
                    color += AccumulateLight(addLight, normalWS, viewWS, diffuseColor, f0, shininess);
                LIGHT_LOOP_END
                #endif

                color = MixFog(color, IN.fogCoord);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float3 _LightDirection;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings ShadowVert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float3 biased = positionWS + normalWS * 0.001;
                float4 positionCS = TransformWorldToHClip(biased);
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.positionCS = positionCS;
                return OUT;
            }
            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
