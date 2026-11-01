// CityImpostor.shader — the material the BAKED building-kit impostor wears.
//
// src/citymap.js:8127-8158 `_kitImposter` is explicit about what the far LOD
// is made of, and it is NOT the kit's own PBR material:
//
//     const opts = { transparent: false, alphaTest: 0.35, depthWrite: true };
//     const side = new THREE.MeshLambertMaterial({ map: b.tex.front, ...opts });
//     const top  = new THREE.MeshLambertMaterial({ map: b.tex.top ?? b.tex.front, ...opts });
//
// A LAMBERT material (no specular, no normal map, no metallic-smoothness) over
// a BAKED ALBEDO texture, with alphaTest 0.35. The alpha test is load-bearing:
// the impostor is the kit's bounding BOX, which is larger than the kit (a
// pitched roof, a setback, a yard shed), so the baked front view has
// transparent margins and without the cutout a house reads as a solid slab.
// citymap.js:8042-8046 says why the bake is albedo-only: "Basic materials =
// pure albedo capture; scene lighting is applied later by the imposter quad's
// own Lambert material, like every neighbor mesh."
//
// So this shader is Shaders/CityKit.shader with the PBR half removed and a
// clip added — same GetMainLight()/GetAdditionalLight()/SampleSH() lighting
// family as every other hand-written shader in this project, same
// `UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)` per-instance hash tint
// (citymap.js:8005-8009: "imposter shares the SAME instanceMatrix attribute"
// AND `lodMesh.instanceColor = mesh.instanceColor` — the far LOD keeps the
// per-building colour, which is the whole reason the swap is invisible), and
// no normal map, no cotangent frame, no metallic-gloss map, because a Lambert
// material has none of those.
//
// NO SHADOW CASTER PASS, on purpose: citymap.js:8010 sets
// `lodMesh.castShadow = false` outright, and City/CityRenderer.Submit now
// gates it off too (the chunk is past SHADOW_DIST by definition).
Shader "TbilisiFly/CityImpostor"
{
    Properties
    {
        _BaseMap("Baked Albedo Atlas", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _InstTint("Instance Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.35   // citymap.js:8130 alphaTest
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            // citymap.js:8130 `transparent: false, depthWrite: true` — an
            // alpha-TESTED opaque, not a blended one.
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
            CBUFFER_END

            // The per-instance hash tint, exactly as Shaders/CityKit.shader
            // declares it — see that file's header for why a
            // MaterialPropertyBlock vector array is the only mechanism that
            // actually delivers this (two others failed silently).
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float fogCoord    : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                // The atlas rect is baked into the MESH UVs
                // (City/KitImpostorBake.BuildBox), so _BaseMap_ST stays 1,1,0,0.
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                half4 tint = (half4)UNITY_ACCESS_INSTANCED_PROP(Props, _InstTint);
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                clip(baseTex.a - _Cutoff);                 // citymap.js:8130 alphaTest 0.35
                half3 albedo = baseTex.rgb * tint.rgb;

                float3 normalWS = normalize(IN.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // MeshLambertMaterial: diffuse only.
                half3 color = albedo * SampleSH(normalWS);
                half nDotL = saturate(dot(normalWS, mainLight.direction));
                color += albedo * mainLight.color *
                         (mainLight.distanceAttenuation * mainLight.shadowAttenuation) * nDotL;

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light addLight = GetAdditionalLight(lightIndex, IN.positionWS);
                    half nl = saturate(dot(normalWS, addLight.direction));
                    color += albedo * addLight.color *
                             (addLight.distanceAttenuation * addLight.shadowAttenuation) * nl;
                LIGHT_LOOP_END
                #endif

                color = MixFog(color, IN.fogCoord);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
