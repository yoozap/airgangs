// WindFoliage.shader — wires Core/Shaders/WindDisplacement.hlsl (written but
// never attached to a material, per that file's own header) into a real,
// hand-rolled URP shader for the chestnut tree canopy / any other instanced
// foliage. Hand-rolled lighting (GetMainLight/GetAdditionalLight/SampleSH),
// NOT UniversalFragmentPBR — the same house choice Shaders/PlaneHull.shader
// made and documents at length in its own header (API stability across URP
// minor versions, verified nowhere but by this probe run).
//
// WIND IS ONE SHARED BREEZE (quality bar): every instance reads the SAME
// _WindTime/_WindDir/_WindAmp globals Core/Wind.cs writes once a frame
// (Shader.SetGlobalFloat/Vector) — declared here as plain globals OUTSIDE the
// UnityPerMaterial cbuffer so they are not accidentally treated as
// per-material and fail to update. The sway is applied AFTER instancing
// (instanceBaseWS is read from THIS instance's own GetObjectToWorldMatrix(),
// which IS the per-instance matrix under GPU instancing) and scales with
// height SQUARED (WindDisplace_float's `wH * wH`) — getting either of those
// wrong is exactly the "whole trees slide instead of bending" bug the brief
// warns about, because a height-independent or pre-instancing offset moves
// the trunk right along with the crown.
//
// Alpha-tested (Cutout), not alpha-blended: this project's chestnut import
// pipeline already standardised on alphaTest for leaf cards (see the project
// memory's "BLEND->alphaTest gotcha" — a blended leaf card sorts wrong against
// its neighbours in an instanced canopy; a clipped one does not need sorting).
Shader "TbilisiFly/WindFoliage"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.45
        _Metallic("Metallic", Range(0,1)) = 0
        _Smoothness("Smoothness", Range(0,1)) = 0.15
        // src/wind.js's own defaults for a tree crown (wind.js ~line 60-70's
        // per-material bend/flutter uniforms) — see WindDisplacement.hlsl's header.
        _Bend("Bend (lean per height^2)", Float) = 0.0024
        _Flutter("Flutter (leaf shiver)", Float) = 0.004
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull Off // a leaf card is thin — seen from both sides, like the chestnut's existing cards

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
            #include "Assets/Scripts/Core/Shaders/WindDisplacement.hlsl"

            // Shared globals written once a frame by Core/Wind.cs — NOT per-material,
            // so these live outside CBUFFER_START(UnityPerMaterial).
            float _WindTime;
            float2 _WindDir;
            float _WindAmp;

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _Metallic;
                float _Smoothness;
                float _Bend;
                float _Flutter;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fogCoord : TEXCOORD3;
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;

                // Undisplaced world position FIRST — WindDisplace_float needs the
                // vertex's world Y "before this displacement" (its own contract).
                float3 positionWS0 = TransformObjectToWorld(IN.positionOS.xyz);
                // Per-instance base: GetObjectToWorldMatrix() IS the per-instance
                // matrix once UNITY_SETUP_INSTANCE_ID has run — applying the sway
                // AFTER instancing, per the quality bar, means reading THIS call's
                // own translation, not a shared/object-level one.
                float3 instanceBaseWS = GetObjectToWorldMatrix()._m03_m13_m23;

                float3 displacedOS;
                WindDisplace_float(IN.positionOS.xyz, instanceBaseWS, positionWS0.y,
                    _WindTime, _WindDir, _WindAmp, _Bend, _Flutter, displacedOS);

                VertexPositionInputs posInputs = GetVertexPositionInputs(displacedOS);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
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
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                clip(baseTex.a - _Cutoff);

                float3 normalWS = normalize(IN.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                if (dot(normalWS, viewWS) < 0) normalWS = -normalWS; // thin double-sided card

                half3 albedo = baseTex.rgb;
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Scripts/Core/Shaders/WindDisplacement.hlsl"

            float _WindTime;
            float2 _WindDir;
            float _WindAmp;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _Metallic;
                float _Smoothness;
                float _Bend;
                float _Flutter;
            CBUFFER_END
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings ShadowVert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                float3 positionWS0 = TransformObjectToWorld(IN.positionOS.xyz);
                float3 instanceBaseWS = GetObjectToWorldMatrix()._m03_m13_m23;
                float3 displacedOS;
                WindDisplace_float(IN.positionOS.xyz, instanceBaseWS, positionWS0.y,
                    _WindTime, _WindDir, _WindAmp, _Bend, _Flutter, displacedOS);
                float3 positionWS = TransformObjectToWorld(displacedOS);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float3 biased = positionWS + normalWS * 0.001;
                float4 positionCS = TransformWorldToHClip(biased);
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.positionCS = positionCS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }
            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
