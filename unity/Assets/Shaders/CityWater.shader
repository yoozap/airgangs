// CityWater.shader — the Mtkvari / Tbilisi Sea / Lisi / Turtle Lake surface.
//
// WHY A WATER SHADER EXISTS (docs/open_items.json `water-envmap`).
//
// src/citymap.js:7314-7338 builds ONE MeshStandardMaterial for every body of
// water in the city and its two load-bearing numbers are `roughness: 0.5` and
// `envMapIntensity: 0.3`. The comment above them is the whole specification:
//
//     "0.5/0.3: the sky here is hazy WHITE — at grazing angles fresnel goes
//      to 1 and any glossier setting turns the whole river into a white
//      mirror sheet from a low pass (re-learned 2026-09; the original
//      0.52/0.3 had already converged on this — don't chase glossy water)"
//
// City/GroundMaterials.cs could port the roughness (URP/Lit's `_Smoothness`
// is the same knob) and had to leave the other one out, because stock URP/Lit
// has NO environment-reflection intensity: its own class header says so
// ("envMapIntensity — URP/Lit has an Environment Reflections toggle, not a
// float"). A toggle cannot express 0.3. Turning reflections OFF loses the
// grazing-angle sky/bank glints the river is supposed to have; leaving them at
// 1.0 is exactly the white mirror sheet the JS comment was written about —
// and it is NOT fixable from the specular colour, because URP's own
// EnvironmentBRDFSpecular (BRDF.hlsl:157-164) lerps from `brdfData.specular`
// to `brdfData.grazingTerm` as fresnel goes to 1, so at a low pass the
// reflection goes white whatever the normal-incidence reflectance is. The
// only place 0.3 can live is a multiply on the indirect specular term, which
// is precisely what three.js's `envMapIntensity` is.
//
// So this file is stock-URP lighting with ONE added line of meaning:
//
//     indirectSpecular = SampleEnvironment(...) * _EnvMapIntensity;
//
// EVERYTHING ELSE IS Shaders/CityKit.shader / Shaders/CityWall.shader,
// deliberately: the same hand-rolled Lambert + Blinn-Phong over
// GetMainLight()/GetAdditionalLight()/SampleSH(), and the same per-fragment
// cotangent-frame TBN so no vertex TANGENT stream is needed (City/RiverSheet
// and City/WaterBodies both build their meshes with positions + world-space
// UVs and never call RecalculateTangents, so stock Lit's normal mapping would
// light the ripples off a zero tangent stream). That lighting model is the
// only one in this project that has ever been seen on screen.
//
// THE TWO RIPPLE LAYERS are citymap.js:7340-7360's `onBeforeCompile` patch
// ported literally rather than approximated. GroundMaterials.Water() used to
// fake them with URP/Lit's _BumpMap + _DetailNormalMap and two scrolling
// texture offsets; here the JS's own fragment code is the fragment code:
//
//     vec2 wuvA = vNormalMapUv + vec2(uWaterTime*0.014, uWaterTime*0.009);
//     vec2 wuvB = vNormalMapUv*2.6 + vec2(-uWaterTime*0.011, uWaterTime*0.017);
//     vec3 mapN = normalize(vec3(wnA.xy + wnB.xy*0.65, wnA.z*wnB.z));
//
// — two counter-drifting layers, the second at 2.6x UV scale and 0.65 weight,
// combined BEFORE normalisation, "because the single scrolling normal map
// read as one pattern translating over an obviously static sheet".
//
// Transparency: `transparent: true, opacity: 0.97` with three.js's default
// `depthWrite: true` — so ZWrite stays ON here (the river is near-opaque; the
// 0.97 exists only to stop the carved bed reading as a hard edge, see
// citymap.js:7332-7334), and `side: THREE.DoubleSide` is `Cull Off`.
Shader "TbilisiFly/CityWater"
{
    Properties
    {
        // citymap.js:7315 color 0x566440 "darker than the photo olive:
        // reflections add light back"; alpha = citymap.js:7335 opacity 0.97.
        _BaseColor("Base Color", Color) = (0.337, 0.392, 0.251, 0.97)
        _BumpMap("Water Normals", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 0.9          // citymap.js:7337 normalScale
        _Smoothness("Smoothness", Range(0,1)) = 0.5      // 1 - citymap.js:7326 roughness 0.5
        _Metallic("Metallic", Range(0,1)) = 0.0          // citymap.js:7328 metalness 0
        // THE POINT OF THIS SHADER. citymap.js:7330 envMapIntensity.
        _EnvMapIntensity("Environment Reflection Intensity", Range(0,2)) = 0.3
        // citymap.js:7349 `uWaterTime`, driven once a frame by
        // City/GroundMaterials.DriveWaterRipple.
        _WaterTime("Ripple Time", Float) = 0
        _DriftA("Layer A drift (UV/s)", Vector) = (0.014, 0.009, 0, 0)   // citymap.js:7351
        _DriftB("Layer B drift (UV/s)", Vector) = (-0.011, 0.017, 0, 0)  // citymap.js:7352
        _RippleScaleB("Layer B UV scale", Float) = 2.6                   // citymap.js:7352 `* 2.6`
        _RippleWeightB("Layer B weight", Float) = 0.65                   // citymap.js:7355 `* 0.65`
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0  // side: DoubleSide
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            // three.js leaves material.depthWrite TRUE on this material; the
            // water is 0.97 opaque, so writing depth is the parity choice and
            // keeps the bridges/quays sorting against it the way they do in
            // the JS build.
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BumpMap_ST;
                float4 _BaseColor;
                float4 _DriftA;
                float4 _DriftB;
                float _BumpScale;
                float _Smoothness;
                float _Metallic;
                float _EnvMapIntensity;
                float _WaterTime;
                float _RippleScaleB;
                float _RippleWeightB;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float fogCoord    : TEXCOORD3;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BumpMap);
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            // Shaders/CityWall.shader's cotangent frame, verbatim — the water
            // meshes carry no TANGENT stream (City/RiverSheet.cs and
            // City/WaterBodies.cs write positions + world-space UVs only).
            float3x3 CotangentFrame(float3 N, float3 posWS, float2 uv)
            {
                float3 dp1 = ddx(posWS);
                float3 dp2 = ddy(posWS);
                float2 duv1 = ddx(uv);
                float2 duv2 = ddy(uv);
                float3 dp2perp = cross(dp2, N);
                float3 dp1perp = cross(N, dp1);
                float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
                float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
                float invmax = rsqrt(max(dot(T, T), dot(B, B)) + 1e-8);
                return float3x3(T * invmax, B * invmax, N);
            }

            /// citymap.js:7348-7358 — the two counter-drifting layers,
            /// combined before normalisation exactly as the JS patch does.
            float3 RippleNormalTS(float2 uv)
            {
                float2 uvA = uv + float2(_WaterTime * _DriftA.x, _WaterTime * _DriftA.y);
                float2 uvB = uv * _RippleScaleB + float2(_WaterTime * _DriftB.x, _WaterTime * _DriftB.y);
                float3 wnA = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvA).xyz * 2.0 - 1.0;
                float3 wnB = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvB).xyz * 2.0 - 1.0;
                float3 mapN = normalize(float3(wnA.xy + wnB.xy * _RippleWeightB, wnA.z * wnB.z));
                mapN.xy *= _BumpScale;   // citymap.js:7356 `mapN.xy *= normalScale`
                return normalize(mapN);
            }

            /// THE ONE LINE THIS FILE EXISTS FOR. three.js's `envMapIntensity`
            /// is a straight multiply on the IBL specular radiance, so the
            /// port is a straight multiply too. The cubemap sample is written
            /// out rather than calling GlossyEnvironmentReflection(), whose
            /// overload set moves between URP minor versions — these three
            /// symbols (unity_SpecCube0, PerceptualRoughnessToMipmapLevel,
            /// DecodeHDREnvironment) have been stable for many.
            half3 SampleEnvironment(half3 reflectVector, half perceptualRoughness)
            {
                half mip = PerceptualRoughnessToMipmapLevel(perceptualRoughness);
                half4 enc = half4(SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectVector, mip));
                return DecodeHDREnvironment(enc, unity_SpecCube0_HDR);
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
                half3 albedo = _BaseColor.rgb;
                half smoothness = saturate(_Smoothness);
                half metallic = saturate(_Metallic);

                float3 geomN = normalize(IN.normalWS);
                float3x3 tbn = CotangentFrame(geomN, IN.positionWS, IN.uv);
                float3 normalWS = normalize(mul(RippleNormalTS(IN.uv), tbn));

                float3 viewWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 f0 = lerp(half3(0.04h, 0.04h, 0.04h), albedo, metallic);
                half3 diffuseColor = albedo * (1.0h - metallic);
                half shininess = exp2(10.0h * smoothness + 1.0h);

                half3 color = half3(0, 0, 0);
                color += albedo * SampleSH(normalWS);
                color += AccumulateLight(mainLight, normalWS, viewWS, diffuseColor, f0, shininess);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light addLight = GetAdditionalLight(lightIndex, IN.positionWS);
                    color += AccumulateLight(addLight, normalWS, viewWS, diffuseColor, f0, shininess);
                LIGHT_LOOP_END
                #endif

                // ---- indirect specular, scaled by envMapIntensity ----------
                // The surfaceReduction / lerp(f0, grazingTerm, fresnel) shape
                // is URP's own EnvironmentBRDFSpecular (BRDF.hlsl:157-164) and
                // three.js's EnvironmentBRDF are the same approximation; what
                // the JS adds and URP/Lit cannot is the multiply below.
                half perceptualRoughness = 1.0h - smoothness;
                half roughness = max(perceptualRoughness * perceptualRoughness, HALF_MIN_SQRT);
                half roughness2 = max(roughness * roughness, HALF_MIN);
                half3 reflectVector = reflect(-viewWS, normalWS);
                half NoV = saturate(dot(normalWS, viewWS));
                half fresnelTerm = Pow4(1.0h - NoV);
                half grazingTerm = saturate(smoothness + 0.04h);
                half surfaceReduction = 1.0h / (roughness2 + 1.0h);
                half3 envSpec = SampleEnvironment(reflectVector, perceptualRoughness) * _EnvMapIntensity;
                color += envSpec * (surfaceReduction * lerp(f0, grazingTerm.xxx, fresnelTerm));

                color = MixFog(color, IN.fogCoord);
                return half4(color, _BaseColor.a);
            }
            ENDHLSL
        }
    }
    // The water casts no shadow in the JS build (City/RiverRenderer.Attach
    // sets ShadowCastingMode.Off on both meshes), so there is deliberately no
    // ShadowCaster pass here.
    FallBack "Universal Render Pipeline/Lit"
}
