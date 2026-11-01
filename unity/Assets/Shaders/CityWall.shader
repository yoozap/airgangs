// CityWall.shader — docs/QUALITY_BAR.md: "the 4 painted styles and the loggia
// buckets fall back to a flat colour because URP's stock Lit does not sample
// mesh vertex colour." BuildingBuilder.BuildRich (off-limits here, not edited)
// already writes a REAL per-building vertex tint into every wall/roof
// submesh's mesh.colors — 40 buildings carry a real OSM `building:colour`,
// and WallMaterials' own per-building hue cast / glass-tower tint / street-
// grime dirty band (its class header) tints thousands more. Nothing before
// CityMaterials.cs ever multiplied that colour into anything on screen.
//
// Modelled on Assets/Shaders/PlaneHull.shader's pattern: hand-rolled
// Lambert + Blinn-Phong via GetMainLight()/GetAdditionalLight()/SampleSH()
// rather than UniversalFragmentPBR, for the same reason PlaneHull gives —
// this project's other hand-written URP shaders are out of reach to check
// for the exact URP-minor-version struct shapes, and these three helper
// calls have been stable across many URP versions; nobody can compile-check
// this file before the verifier agent runs it.
//
// CityMaterials.cs uses this shader for every wall:*/roof:* bucket (painted
// style0..3, the reserved loggia buckets, AND the 31 real albedo tiles) —
// not only the painted ones named in the quality-bar entry. Two reasons:
//   1. It is the one place the vertex-tint fix belongs, and keeping ALL
//      wall/roof buckets on ONE shader is simpler than switching between two.
//   2. BuildingBuilder's wall/roof meshes carry no TANGENT stream (grep
//      confirms BuildRich never calls SetTangents/RecalculateTangents, and
//      that file is off-limits to add it to). Stock URP Lit's normal mapping
//      needs real per-vertex tangents or it lights flat/broken wherever the
//      stream is zero. This shader sidesteps that trap entirely by deriving
//      the TBN basis per-FRAGMENT from screen-space derivatives of world
//      position and UV (the standard "cotangent frame" trick) — correct
//      normal mapping with NO vertex tangent stream required, so Part B's
//      real normal maps light correctly however BuildRich builds its mesh.
// Real photo facades (pb:*) stay on stock Lit (CityMaterials.LitShader()) —
// those never carry a meaningful per-building tint and are explicitly another
// agent's exclusive system; this file does not touch them.
Shader "TbilisiFly/CityWall"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        // R = metallic MULTIPLIER, A = smoothness MULTIPLIER, both centred on
        // 1.0 (tools/build_tile_normals.py's _mgs.png convention) — default
        // "white" {} (reads 1,1,1,1) reproduces the OLD constant-only
        // behaviour exactly wherever no map is bound (the 4 painted styles +
        // loggia, which have no albedo tile to derive a map from at all).
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

            // UNITY_VERTEX_INPUT_INSTANCE_ID is MANDATORY here, not optional.
            // `#pragma multi_compile_instancing` is declared above and
            // City/CityMaterials.cs:200 sets `enableInstancing = true` on every
            // material it builds with this shader ("HARD RULE: every material
            // here must support GPU instancing"), and City/CityRenderer.cs then
            // submits them through Graphics.RenderMeshInstanced. Without this
            // member and the UNITY_SETUP_INSTANCE_ID below, unity_ObjectToWorld
            // never advances per instance, so EVERY instance in a batch draws
            // at the FIRST instance's transform — the whole building kit stacked
            // on one spot. Found 2026-10-27 by the vegetation renderer's author,
            // who hit it while looking for a vertex-colour shader to copy.
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float4 color      : TEXCOORD3; // the real per-building/per-vertex tint
                float fogCoord    : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);              // selects THIS instance's matrices
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color = IN.color;
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            // Cotangent-frame TBN with NO vertex tangent input — see this
            // file's header. Standard derivation (Lengyel/"Followup: Normal
            // Mapping Without Precomputed Tangents"): screen-space
            // derivatives of world position and UV give two edge vectors
            // whose combination reproduces the tangent/bitangent basis.
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
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                // THE FIX: multiply in the real per-building/per-vertex tint
                // BuildRich already bakes into mesh.colors. A flat painted
                // bucket has no _BaseMap (defaults to white), so this alone
                // is what turns "every Soviet block is the same flat tone"
                // into real per-building variation.
                half3 albedo = baseTex.rgb * IN.color.rgb;

                half4 mgs = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv);
                half metallic = saturate(_Metallic * mgs.r);
                half smoothness = saturate(_Smoothness * mgs.a);
                // A BARE-INTEGER half literal (1h, 10h) is not valid HLSL — DX11 rejects it ("no matching 0 parameter
                // intrinsic function" + "unexpected token 'h'"), so this shader has
                // NEVER compiled. Pre-existing, unrelated to the instancing fix above;
                // found 2026-10-27 while verifying that fix. Every material
                // City/CityMaterials.cs builds for a non-photo facade uses this shader,
                // so this is the whole city's wall material.
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
                return half4(color, baseTex.a);
            }
            ENDHLSL
        }

        // Same minimal, version-stable shadow caster PlaneHull.shader uses.
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

            // The shadow pass needs the instance id too. Fixing only ForwardLit
            // would draw the city correctly and then cast every instance's
            // shadow from the first instance's position.
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
                float3 lightDirWS = _LightDirection;
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
                UNITY_SETUP_INSTANCE_ID(IN);              // same requirement as ForwardLit
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
