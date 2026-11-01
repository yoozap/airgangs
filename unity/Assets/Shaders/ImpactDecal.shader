// ImpactDecal.shader — the renderer slot src/impactfx.js's DecalLayer left
// unported (Combat/ImpactFx.cs's own DecalLayer/DecalStamp/DecalGeometry port
// is pose+size DATA only — the THREE.InstancedMesh/PlaneGeometry/
// MeshBasicMaterial construction in DecalLayer's constructor, src/impactfx.js:
// 81-110, was explicitly left to "whoever wires the renderer", same precedent
// Shaders/ImpactParticle.shader's own header already used for ParticleCloud).
// Fx/ImpactDecalFx.cs is that renderer.
//
// NOT a camera-facing billboard. Unlike every other Fx shader in this
// project's scope (MuzzleFlash/ImpactParticle/Fireball/GlassGlint all face the
// camera), a bullet-hole decal must lie FLAT AGAINST THE SURFACE IT HIT — so
// this shader draws the instance's own object-space TRS unmodified.
// DecalLayer.Stamp (Combat/ImpactFx.cs) already solved that orientation from
// the hit normal (Quaternion.LookRotation(z, y), the surface normal as +Z and
// the in-plane "up" as +Y, both already GeoFrame-handedness-correct — see that
// method's own header for the cross-product audit). All this shader does is
// sample the one baked texture (Combat/ImpactDecalTextures.cs, src/impactfx.js:
// 168-418) and multiply by a per-instance tint.
//
// No life/fade field at all: src/impactfx.js's DecalLayer never ages a mark
// out (a bullet hole stays until something else overwrites that ring-buffer
// slot), so _Tint carries ONLY colour (rgb), never a decay alpha the way
// Shaders/ImpactParticle.shader's _TintLife does. Reuses the same "_Tint"
// instanced-property NAME as Fx/InstancedSpriteBatch.cs's own TintId, so this
// shader works directly with InstancedSpriteBatch.Draw's existing tint-array
// parameter with no changes to that shared helper.
Shader "TbilisiFly/ImpactDecal"
{
    Properties
    {
        _MainTex("Decal Texture", 2D) = "white" {}
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

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            UNITY_INSTANCING_BUFFER_START(PerDecal)
                // rgb = tint only (src/impactfx.js's DecalLayer.stamp `tint`
                // argument, a THREE.Color — never a life/alpha decay); a unused.
                UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)
            UNITY_INSTANCING_BUFFER_END(PerDecal)

            struct Attributes
            {
                float4 positionOS : POSITION; // a unit quad, centred on the origin
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 tint : TEXCOORD1;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                // Plain object-space TRS -> world -> clip. The instance matrix
                // (Matrix4x4.TRS(stamp.Position, stamp.Rotation, stamp.Scale) in
                // Fx/ImpactDecalFx.cs) is the ENTIRE pose; no billboarding here.
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;
                OUT.tint = UNITY_ACCESS_INSTANCED_PROP(PerDecal, _Tint).rgb;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half3 c = tex.rgb * IN.tint;
                half a = tex.a;
                if (a < 0.01) discard;
                return half4(c, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
