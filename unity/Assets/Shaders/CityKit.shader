// CityKit.shader — the per-instance TINT shader for the building kit.
//
// WHY A SECOND WALL SHADER EXISTS.
//
// src/citymap.js:7940 `_placeChunkedInstanced` takes a `makeColor` callback and
// writes ONE THREE.InstancedBufferAttribute of per-instance colour
// (`mesh.setColorAt(k, c)`), so the whole kit is one draw per
// (geometry, material, chunk) with the hash tint riding along per instance —
// citymap.js's five kit call sites each pass their own
// `(c, it) => { const h = ((it.bi * 2654435761) % 1024) / 1024; c.setHSL(...) }`
// (City/KitTint.cs computes the exact same colours).
//
// The port could not do that: `Graphics.RenderMeshInstanced`'s plain
// `Matrix4x4[]` overload carries no per-instance data, and neither stock URP
// Lit nor Shaders/CityWall.shader exposes a tint as an INSTANCED property
// (grep: URP 17.6 declares `UNITY_INSTANCING_BUFFER_START` in exactly three
// places — SpeedTree8, TerrainLit and the 2D core — and `_BaseColor` is a
// plain `UnityPerMaterial` uniform in Lit). So City/CityRenderer.cs quantised
// the tint to a 24-step grid and CLONED a material per bucket, which put the
// bucket in the batch key: measured 781,149 batches of 1.86 instances and 734
// materials for 1,453,630 instances (Editor/Probes/CityDrawProbe.cs). This
// shader is the other half of removing it: a second colour, `_InstTint`,
// lives in the `Props` instancing buffer, so ONE material serves every tint
// and `RenderMeshInstanced<CityRenderer.KitInstance>` feeds the colour per
// instance exactly like the JS attribute.
//
// WHY NOT JUST ADD THE PROP TO CityWall.shader: CityWall is also used for the
// city's building CHUNK meshes, which draw through ordinary renderers. Moving
// `_BaseColor` out of `CBUFFER_START(UnityPerMaterial)` and into an
// instancing cbuffer makes a shader SRP-Batcher-INcompatible, and the project
// memory records the building chunks' per-group draws as a measured
// 17 -> 4.4 ms cliff whose "far-twin" fix regressed old town and was reverted
// twice. That path is not to be touched, so the kit gets its own shader and
// CityWall is left byte-identical.
//
// EVERYTHING ELSE IS CityWall.shader, deliberately. Same hand-rolled
// Lambert + Blinn-Phong over GetMainLight()/GetAdditionalLight()/SampleSH()
// (the house choice Shaders/PlaneHull.shader and Shaders/WindFoliage.shader
// also make, and the only lighting model in this project that has ever been
// seen on screen), same cotangent-frame TBN so no vertex TANGENT stream is
// needed (the kit FBX carry none), same `_MetallicGlossMap` R/A modulation
// convention, same minimal shadow caster. Two deliberate differences:
//
//   1. NO VERTEX COLOUR. CityWall multiplies `IN.color` because BuildRich
//      bakes a per-building tint into the chunk meshes' `mesh.colors`. The kit
//      FBX meshes have no COLOR stream at all, and a missing vertex stream does
//      not reliably read as white — reading it would risk black buildings. The
//      kit's tint arrives per INSTANCE instead, which is what the JS does too
//      (`instanceColor`, not a vertex attribute).
//   2. TWO colours, multiplied: `_BaseColor` is the ordinary per-MATERIAL
//      value (copied straight off the source FBX material, which is where the
//      kit's real `h_plaster`/`a_brick`/`panel_concrete` tones live) and
//      `_InstTint` is the per-INSTANCE hash tint. That is citymap.js's
//      `instanceColor` semantics exactly — a MULTIPLY onto the material's own
//      colour, see `_loadAptKit`'s "NO multiplyScalar here" warning against
//      double-dimming — and it is also what lets ONE instance array serve all
//      of a mesh's submeshes: the per-submesh colour stays on the material, so
//      the per-instance payload is the tint alone.
Shader "TbilisiFly/CityKit"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        // Per-MATERIAL, copied from the source FBX material.
        _BaseColor("Base Color", Color) = (1,1,1,1)
        // Per-INSTANCE hash tint (City/KitTint.cs). White = untinted, which is
        // what a non-instanced draw or the material inspector sees.
        _InstTint("Instance Tint", Color) = (1,1,1,1)
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        _MetallicGlossMap("Metallic (R) / Smoothness (A) modulation", 2D) = "white" {}
        _Metallic("Metallic", Range(0,1)) = 0.0
        _Smoothness("Smoothness", Range(0,1)) = 0.2
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

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

            TEXTURE2D(_BaseMap);          SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);          SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);     SAMPLER(sampler_OcclusionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _BumpScale;
                float _Metallic;
                float _Smoothness;
                float _OcclusionStrength;
            CBUFFER_END

            // THE per-instance tint. With instancing ON this expands to a
            // `UnityInstancing_Props` cbuffer of `{ float4 _InstTint; }`
            // records indexed by unity_InstanceID — the layout
            // Graphics.RenderMeshInstanced<T> fills from the trailing fields of
            // CityRenderer.KitInstance (its leading Matrix4x4 goes to
            // unity_Builtins0's unity_ObjectToWorldArray). With instancing OFF
            // the macros (Packages/com.unity.render-pipelines.core/ShaderLibrary/
            // UnityInstancing.hlsl:495-500) degrade it to a plain uniform, so
            // the material inspector and any non-instanced draw still work.
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)
            UNITY_INSTANCING_BUFFER_END(Props)

            // UNITY_VERTEX_INPUT_INSTANCE_ID is MANDATORY in BOTH passes (see
            // CityWall.shader's own note, found 2026-10-27): without it
            // unity_ObjectToWorld never advances per instance and the whole kit
            // draws stacked on the first instance's transform. Here it is doubly
            // required — it is also what selects this instance's _InstTint.
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
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            // Cotangent-frame TBN with NO vertex tangent input — CityWall.shader's
            // derivation, kept because the kit FBX carry no tangent stream either.
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

            half3 AccumulateLight(Light light, float3 N, float3 V, half3 diffuseColor, half3 f0, half shininess)
            {
                half nDotL = saturate(dot(N, light.direction));
                half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation) * nDotL;
                half3 diff = diffuseColor * radiance;
                float3 h = normalize(light.direction + V);
                half spec = pow(saturate(dot(N, h)), shininess) * nDotL;
                half3 specCol = f0 * spec * radiance;
                return diff + specCol;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // THE per-instance read. Must come after UNITY_SETUP_INSTANCE_ID
                // in this stage or unity_InstanceID is not valid here.
                UNITY_SETUP_INSTANCE_ID(IN);
                half4 tint = (half4)UNITY_ACCESS_INSTANCED_PROP(Props, _InstTint);

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                half3 albedo = baseTex.rgb * tint.rgb;

                half4 mgs = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv);
                half metallic = saturate(_Metallic * mgs.r);
                half smoothness = saturate(_Smoothness * mgs.a);
                // A BARE-INTEGER half literal (1h, 10h) is not valid HLSL — the
                // trap that kept CityWall.shader from ever compiling. Every half
                // literal in this file carries a decimal point.
                half occlusion = lerp((half)1.0, SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, IN.uv).g, _OcclusionStrength);

                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                float3x3 tbn = CotangentFrame(IN.normalWS, IN.positionWS, IN.uv);
                float3 normalWS = normalize(mul(normalTS, tbn));

                float3 viewWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 f0 = lerp(half3(0.04h, 0.04h, 0.04h), albedo, metallic);
                half3 diffuseColor = albedo * (1.0h - metallic);
                half shininess = exp2(10.0h * smoothness + 1.0h);

                half3 color = half3(0, 0, 0);
                color += albedo * SampleSH(normalWS) * occlusion;
                color += AccumulateLight(mainLight, normalWS, viewWS, diffuseColor, f0, shininess);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light addLight = GetAdditionalLight(lightIndex, IN.positionWS);
                    color += AccumulateLight(addLight, normalWS, viewWS, diffuseColor, f0, shininess);
                LIGHT_LOOP_END
                #endif

                color = MixFog(color, IN.fogCoord);
                return half4(color, baseTex.a * tint.a);
            }
            ENDHLSL
        }

        // Same minimal, version-stable shadow caster CityWall/PlaneHull use.
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
            #pragma multi_compile_instancing   // per-pass: ForwardLit's pragma does NOT carry here
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(float3 positionOS, float3 normalOS)
            {
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
                float3 biased = positionWS + normalWS * 0.001;
                float4 positionCS = TransformWorldToHClip(biased);
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionCS = GetShadowPositionHClip(IN.positionOS.xyz, IN.normalOS);
                return OUT;
            }
            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
