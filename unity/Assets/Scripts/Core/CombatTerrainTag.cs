using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Core
{
    // SPLIT OUT OF CombatWorldAdapter.cs, and this is not a style preference.
    //
    // A MonoBehaviour must live in a file named after it or Unity cannot bind a
    // script ASSET to it. CombatWorldAdapter.cs declared three of them
    // (CombatTerrainTag, CombatBuildingChunk, PlayableSceneBoot) in a file named
    // after a fourth, non-MonoBehaviour class, so AddComponent<T>() produced
    // components that serialised with `m_Script: {fileID: 187830060}` and NO
    // guid, falling back to m_EditorClassIdentifier. The symptom was
    // PlayableProbe failing `playableSceneBootExists` on a freshly-built scene
    // that demonstrably contained a GameObject named PlayableSceneBoot and 605
    // chunks' worth of baked RangeStart data: the YAML was there, the live
    // component was not, so Install() never ran and CombatSystems/CrashFactory/
    // the hero body/the UI all came back missing TOGETHER.

    /// <summary>Tags a terrain tile's collider so IBallisticsWorld.IsTerrain can
    /// tell it apart from a building/kit-prop collider. Attached once per tile
    /// by Editor/BuildPlayableScene.cs — pure marker, no state.</summary>
    public sealed class CombatTerrainTag : MonoBehaviour { }
}
