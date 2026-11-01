// PlaneMark.shader — the OTHER HALF of plane damage.
//
// Shaders/PlaneHull.shader already discards the pixels inside every tear, so
// the metal really goes. On its own that is a hole with NO ART AROUND IT — the
// exact inverse of src/planedamage.js's own rule that a drawn hole without a
// shader slot is "a PICTURE of a hole". Both halves have to exist. This is the
// drawn half: the instanced quad material src/planedamage.js's MarkLayer
// constructor builds (src/planedamage.js:417-456) and which
// Combat/PlaneDamage.cs's PlaneMarkLayer deliberately left to its consumer
// ("Pure data: Pose/Tint/Live drive whatever draws them"). Fx/PlaneMarkFx.cs is
// that consumer.
//
// Ported from that constructor, property for property:
//   map: texture          -> _MainTex (Combat/DamageDecals.cs's baked atlases)
//   transparent: true     -> Queue=Transparent, Blend SrcAlpha OneMinusSrcAlpha
//   depthWrite: false     -> ZWrite Off
//   side: DoubleSide      -> Cull Off   (a mark on a wing is seen from below too)
//   polygonOffsetFactor/Units = -8 - order*2 (src/planedamage.js:429-430)
//                         -> Offset [_OffsetFactor], [_OffsetUnits], set per
//                            layer by Fx/PlaneMarkFx.cs. The quad is ALSO
//                            lifted off the skin in metres by PlaneDamage's own
//                            Stamp() (`lift`), exactly as the JS does both.
//   mesh.renderOrder = 6 + order (:453)
//                         -> Material.renderQueue = 3000 + 6 + order, also in
//                            Fx/PlaneMarkFx.cs (a Queue tag cannot vary per
//                            material, renderQueue can).
//   onBeforeCompile's one injected line (:437-446):
//        vMapUv = vMapUv * k + uvOffset;   k = 1/TILES
//                         -> _UvScale (material, = 1/tiles) and the per-instance
//                            _PropsA.xy, below. That is the 2x2 ATLAS OF FOUR,
//                            and it is not decoration: one drawing stamped over
//                            and over is the single thing that made battle
//                            damage read as stickers (src/planedamage.js:
//                            1617-1623). Four drawings, picked per instance.
//
// NOT a camera-facing billboard — same reasoning as Shaders/ImpactDecal.shader,
// and here it matters even more: the whole point of PlaneDamage.cs's local-frame
// bookkeeping is that a mark sits on the AIRFRAME and flies with it, so the
// instance matrix (PlaneMarkLayer.Pose, a world matrix refreshed from
// wrapper.localToWorldMatrix * mark.Local whenever the machine moves) is the
// ENTIRE pose. The vertex shader adds nothing to it.
//
// Reuses the "_Tint"/"_PropsA" instanced-property NAMES already established by
// Fx/InstancedSpriteBatch.cs (TintId/PropsAId), so it works with that shared
// batcher's existing arrays with no change to it.
Shader "TbilisiFly/PlaneMark"
{
    Properties
    {
        _MainTex("Mark Atlas", 2D) = "white" {}
        // 1 / tiles — 0.5 for the four 2x2 atlases, 1 for the scrape strip
        // (src/planedamage.js:527 is the one MarkLayer built with tiles = 1).
        _UvScale("UV scale (1/tiles)", Float) = 0.5
        _OffsetFactor("Polygon offset factor", Float) = -8
        _OffsetUnits("Polygon offset units", Float) = -8
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Offset [_OffsetFactor], [_OffsetUnits]
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

            CBUFFER_START(UnityPerMaterial)
                float _UvScale;
                float _OffsetFactor;
                float _OffsetUnits;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(PerMark)
                // rgb = how shot-up the machine is (PlaneDamage.Soot / the
                // per-layer tints at src/planedamage.js's _stamp callers); a
                // unused — a mark NEVER fades out, it lives until its ring slot
                // is reused (src/planedamage.js:463 alloc).
                UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)
                // xy = this instance's own tile in the atlas (src/planedamage.js:
                // 471-477 uvOffset); zw unused.
                UNITY_DEFINE_INSTANCED_PROP(float4, _PropsA)
            UNITY_INSTANCING_BUFFER_END(PerMark)

            struct Attributes
            {
                float4 positionOS : POSITION; // Fx/InstancedSpriteBatch's unit quad, -0.5..0.5
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
                // The instance matrix is Matrix4x4.TRS(point + normal*lift,
                // LookRotation(normal, inPlaneUp), (w, h, 1)) — metres, already
                // in world space. No billboarding, no scaling here.
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(worldPos);
                // src/planedamage.js:441 `vMapUv = vMapUv * k + uvOffset`
                OUT.uv = IN.uv * _UvScale + UNITY_ACCESS_INSTANCED_PROP(PerMark, _PropsA).xy;
                OUT.tint = UNITY_ACCESS_INSTANCED_PROP(PerMark, _Tint).rgb;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half a = tex.a;
                // Nothing to blend where the atlas tile is empty, and skipping
                // it keeps a 3,200-slot layer off the blend unit.
                if (a < 0.004h) discard;
                return half4(tex.rgb * IN.tint, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
