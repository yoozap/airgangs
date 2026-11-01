// PlaneHull.shader — the custom URP shader src/planedamage.js and src/dents.js need.
//
// TWO quality-bar features live here because the JS ALSO chains them onto one
// patched material (src/dents.js:499 "Whatever another system has already
// patched into this shader is called first" — referring directly to
// planedamage.js's hole patch):
//
//   1. THE METAL REALLY GOES. `_TearTex` holds every opening an airframe
//      carries (xyz = position in the airframe's OWN local frame, w = radius),
//      written by Combat/PlaneDamage.cs's HoleSet. Every fragment inside one is
//      clip()'d — not a decal, an actual hole you see through (the hull is
//      double-sided, `Cull Off`, so a hole in the top of a wing shows the
//      inside of the bottom skin, or daylight/the hangar floor on a through-hit).
//      Two ranges are tested per src/planedamage.js:330-338: every opening
//      belonging to NO panel (`_TearAll`, a severed piece's own holes, tested by
//      everyone) plus this renderer's own slice of the shared map (`_TearLo`..
//      `_TearHi`) — so a hole in the tail costs a wing's pixels nothing.
//
//   2. CRASH SCUFF. A dent's vertex colour (src/dents.js's SOOT/SCUFF, baked by
//      Combat/CrashDents.cs) multiplies the albedo AND drives roughness toward
//      matte / metalness toward zero, exactly as src/dents.js:505-514's
//      onBeforeCompile patch does: a crumpled panel reads as burnt, dull and
//      filthy instead of merely darker.
//
// Combat/PlaneDamage.cs's HoleSet.Patch and Combat/CrashDents.cs's Paint() both
// build a PER-RENDERER Material instance on this shader (never share one
// instance across renderers) — the Unity analogue of "clone the material per
// plane first": each aeroplane's holes are invisible to every other aeroplane's
// renderers because each one owns its own Material, not because the texture
// data differs (HoleSet is also one-per-airframe).
//
// Lighting is a hand-rolled, URP-API Lambert + Blinn-Phong (NOT
// UniversalFragmentPBR's full struct-heavy call) — deliberately: this project's
// other hand-written URP shaders (Assets/Core/Shaders/*) are out of this port's
// reach to inspect for the exact URP minor-version helper signatures in use, and
// GetMainLight()/GetAdditionalLight()/SampleSH() have been stable across many
// URP versions, which matters because nobody can compile-check this shader
// before the verifier agent runs it.
Shader "TbilisiFly/PlaneHull"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _Metallic("Metallic", Range(0,1)) = 0.35
        _Smoothness("Smoothness", Range(0,1)) = 0.55
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1
        _Cutoff("Alpha Cutoff (shadow pass)", Range(0,1)) = 0.5
        [HideInInspector] _TearAll("Tear All Count", Int) = 0
        [HideInInspector] _TearLo("Tear Range Lo", Int) = 0
        [HideInInspector] _TearHi("Tear Range Hi", Int) = 0
        // EVERY MEMBER OF UnityPerMaterial NEEDS A PROPERTY HERE, or the value
        // never reaches the GPU once more than a handful of materials use this
        // shader. `_TearTex` and the world->airframe matrix were set with
        // Material.SetTexture/SetMatrix against names that existed only inside
        // the HLSL, and the symptom was not "no holes": it was holes that
        // depended on WHAT ELSE had been hit. One airframe shot on its own lost
        // its pixels correctly; the same airframe shot after two others did not
        // discard a single fragment, with identical CPU state (same hit point to
        // the millimetre, same _TearLo..=0..4, same 0.0395 m opening in the slot
        // that covers the hit) — the kind of failure that reads as a GPU
        // mystery and is a missing declaration. ShaderLab has no matrix property
        // type, so the matrix is declared as its four ROWS and rebuilt in Vert
        // (Combat/PlaneDamage.cs HoleSet.Write writes them with GetRow).
        [HideInInspector] _TearTex("Tear Map", 2D) = "black" {}
        [HideInInspector] _TearInv0("Tear World->Local Row 0", Vector) = (1,0,0,0)
        [HideInInspector] _TearInv1("Tear World->Local Row 1", Vector) = (0,1,0,0)
        [HideInInspector] _TearInv2("Tear World->Local Row 2", Vector) = (0,0,1,0)
        [HideInInspector] _TearInv3("Tear World->Local Row 3", Vector) = (0,0,0,1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        // src/planes.js lineHullInside — the skin is seen from both sides once a
        // hole opens it up, so the whole hull is double-sided, not just the holes.
        Cull Off

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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define TEAR_W 64   // Combat/PlaneDamage.cs HoleSet.TearW — keep in lockstep
            #define TEAR_H 32   // Combat/PlaneDamage.cs HoleSet.TearH

            TEXTURE2D(_BaseMap);           SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);           SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);  SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);      SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_TearTex);           SAMPLER(sampler_TearTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _BumpScale;
                float _Metallic;
                float _Smoothness;
                float _OcclusionStrength;
                float _Cutoff;
                int _TearAll;
                int _TearLo;
                int _TearHi;
                // the four rows of the world->airframe-local matrix — see the
                // Properties block above for why it is not a float4x4 here
                float4 _TearInv0;
                float4 _TearInv1;
                float4 _TearInv2;
                float4 _TearInv3;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2; // w = bitangent sign
                float2 uv         : TEXCOORD3;
                float4 color      : TEXCOORD4;
                float3 tearPos    : TEXCOORD5; // src/planedamage.js:319 vTearPos, in the AIRFRAME's own local frame
                float fogCoord    : TEXCOORD6;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs nrmInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = nrmInputs.normalWS;
                OUT.tangentWS = float4(nrmInputs.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color = IN.color;
                // src/planedamage.js:319 — airframe-LOCAL frame; _TearInv is world->local for
                // the aeroplane this renderer belongs to (refreshed whenever it moves, see
                // Combat/PlaneDamage.cs's Update()/HoleSet.Write()). Once a round is tested
                // here, no further handedness mirroring applies — see this file's own header
                // and Combat/PlaneDamage.cs's class-header note on the local-frame boundary.
                float4x4 tearInv = float4x4(_TearInv0, _TearInv1, _TearInv2, _TearInv3);
                OUT.tearPos = mul(tearInv, float4(OUT.positionWS, 1.0)).xyz;
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            // HLSL has no closures/nested functions — diffuse/f0/shininess are passed in
            // explicitly rather than captured, unlike a JS inner function would.
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
                // ---- THE METAL REALLY GOES (src/planedamage.js:323-338) ----
                UNITY_LOOP
                for (int i = 0; i < _TearAll; i++)
                {
                    float4 t = LOAD_TEXTURE2D(_TearTex, int2(i % TEAR_W, i / TEAR_W));
                    if (distance(IN.tearPos, t.xyz) < t.w) discard;
                }
                UNITY_LOOP
                for (int j = _TearLo; j < _TearHi; j++)
                {
                    float4 t2 = LOAD_TEXTURE2D(_TearTex, int2(j % TEAR_W, j / TEAR_W));
                    if (distance(IN.tearPos, t2.xyz) < t2.w) discard;
                }

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                half3 albedo = baseTex.rgb;

                // ---- CRASH SCUFF (src/dents.js:505-514) ----
                // vertex colour multiplies albedo always (three.js's default vertexColors
                // behaviour, which the JS material already relies on), and on top of that
                // drives roughness up / metalness down — a crumpled panel reads as burnt,
                // matte and filthy, which is the whole point ("no one plane had dent" was
                // the crater being 40% darker in the buffer and barely visible on screen).
                albedo *= IN.color.rgb;
                float dentScuff = 1.0 - saturate(IN.color.r * 1.3);

                half metallic = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv).r * _Metallic;
                half smoothness = _Smoothness;
                metallic = lerp(metallic, 0.03h, dentScuff);
                smoothness = lerp(smoothness, 0.05h, dentScuff);
                // `1h` is NOT a valid HLSL literal — a bare-integer half needs the decimal
                // point (`1.0h`), the same trap that kept Shaders/CityWall.shader from
                // ever compiling. This one shader error turned every aeroplane MAGENTA on
                // its first hit (HoleSet.Patch swaps the skin onto this shader) and stopped
                // the `_TearInv` discard that makes a tear see-through. Found by the first
                // GPU render of the port, 2026-10-30.
                half occlusion = lerp((half)1.0, SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, IN.uv).g, _OcclusionStrength);

                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                float3 bitangentWS = cross(IN.normalWS, IN.tangentWS.xyz) * IN.tangentWS.w;
                float3x3 tbn = float3x3(IN.tangentWS.xyz, bitangentWS, IN.normalWS);
                float3 normalWS = normalize(mul(normalTS, tbn));

                float3 viewWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                // Double-sided hull: a backface's authored normal points away from the
                // camera through a hole, which would shade a torn-open interior pitch
                // black. Flip toward the viewer, same intent as planedamage.js's own
                // "FACING THE SHOOTER" normal flip (src/planedamage.js:676-683).
                if (dot(normalWS, viewWS) < 0) normalWS = -normalWS;

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 f0 = lerp(half3(0.04h, 0.04h, 0.04h), albedo, metallic);
                half3 diffuseColor = albedo * (1.0h - metallic);
                half shininess = exp2(10.0h * smoothness + 1.0h); // same bare-half trap, two more of them

                half3 color = half3(0, 0, 0);
                // ambient / GI — SampleSH, scaled by occlusion
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

        // Minimal, self-contained shadow caster — does not depend on URP's
        // LitInput.hlsl/ShadowCasterPass.hlsl internals (version-fragile), only on
        // Core.hlsl's stable TransformObjectToWorld/TransformWorldToHClip helpers.
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float3 _LightDirection; // set by URP's shadow pass (standard custom-shadow-caster global)

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            float4 GetShadowPositionHClip(float3 positionOS, float3 normalOS)
            {
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
                float3 lightDirWS = _LightDirection;
                float3 biased = positionWS + normalWS * 0.001; // small constant bias — robust, version-independent
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
                OUT.positionCS = GetShadowPositionHClip(IN.positionOS.xyz, IN.normalOS);
                return OUT;
            }
            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
