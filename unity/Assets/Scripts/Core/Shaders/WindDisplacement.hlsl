// Port of the WIND_VERTEX/WIND_HEAD GLSL strings in src/wind.js:52-86 — the
// instanced-foliage sway. NOT wired into a material: shader work is out of
// this port's scope (see docs/UNITY_PORT.md — "12 files hooking
// onBeforeCompile ... 0% carries, HLSL/Shader Graph rewrite"), but wind.js
// itself IS in scope, so this is the as-faithful-as-possible HLSL translation
// of its displacement math, ready to paste into a Shader Graph "Custom
// Function" node (Vertex stage) once someone wires up the vegetation shader.
//
// Inputs expected from the node: ObjectToWorld's translation (the instance's
// own base position — glTFast/URP's instancing exposes this as
// GetObjectToWorldMatrix()._m03_m13_m23, the same role THREE's
// instanceMatrix[3].xyz plays), and the vertex position in object space.
// _WindTime/_WindDir/_WindAmp are the SAME globals Wind.cs (Core/Wind.cs)
// writes every frame with Shader.SetGlobalFloat/Vector — one write moves the
// whole city, exactly like the JS's shared uniforms object.
//
// ======================= HANDEDNESS =======================
// The displacement is expressed entirely in the OBJECT'S OWN local (x, z)
// plane, pushed by a world-frame wind direction that is ALREADY mirrored
// (Wind.UnityDir0 negates the game frame's X — see Core/Wind.cs). Because
// the formula only ever multiplies positions/directions together with no
// cross product anywhere in it, nothing here needs an extra sign flip beyond
// feeding it the already-converted _WindDir: the sway leans the SAME
// physical way in both engines because the input vector was corrected once,
// at the source, in Wind.cs.
// ============================================================

#ifndef TBILISIFLY_WIND_DISPLACEMENT_INCLUDED
#define TBILISIFLY_WIND_DISPLACEMENT_INCLUDED

// bend:    how far the crown leans per metre of height squared (src default 0.0024)
// flutter: the small cross-wind shiver of the leaves (src default 0.004)
void WindDisplace_float(
    float3 positionOS,    // vertex position, object space (THREE's `transformed`)
    float3 instanceBaseWS,// this instance's own world-space base position (wBase)
    float worldPosY,      // the vertex's WORLD-space Y, before this displacement
    float windTime,       // _WindTime
    float2 windDir,       // _WindDir — (east-component, north-component), already mirrored
    float windAmp,        // _WindAmp (the gust, 0.55..1.3)
    float bend,
    float flutter,
    out float3 displacedOS)
{
    // Height above the instance's own base — the trunk is stiff, the crown is not.
    float wH = max(worldPosY - instanceBaseWS.y, 0.0);
    float wPh = dot(instanceBaseWS.xz, float2(0.21, 0.37));
    float wT = windTime;

    // The whole crown leaning, two beats so it never looks metronomic.
    float wS = sin(wT * 1.15 + wPh) * 0.70 + sin(wT * 2.31 + wPh * 1.7) * 0.30;
    float2 bendOffset = windDir * (wH * wH * bend * windAmp * wS);

    // The leaves themselves, faster and far smaller, across the wind.
    float wF = sin(wT * 5.1 + positionOS.x * 1.7 + positionOS.z * 1.3 + wPh);
    float2 flutterOffset = float2(-windDir.y, windDir.x) * (wH * flutter * windAmp * wF);

    displacedOS = positionOS;
    displacedOS.xz += bendOffset + flutterOffset;
}

#endif
