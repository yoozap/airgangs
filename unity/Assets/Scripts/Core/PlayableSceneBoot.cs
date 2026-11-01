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

    /// <summary>Runtime stand-in for Boot.cs's login->roster->fly flow, for a
    /// scene with no login UI/server reachable yet (Editor/BuildPlayableScene.cs
    /// wires this onto the scene's GameLoop instead). Also exactly what
    /// Editor/Probes/PlayableProbe.cs calls to get a ticking GameLoop. A
    /// TOP-LEVEL class, not nested inside CombatWorldAdapter — it was nested
    /// originally, and while a nested MonoBehaviour CAN be added/found via
    /// AddComponent/FindObjectByType, there is no upside to it either, and a
    /// run that came back with every one of CombatSystems/CrashFactory/body/UI
    /// missing together is exactly the shape a never-found component would
    /// produce, so this removes that as a possible cause outright rather than
    /// arguing it was fine.</summary>
    public sealed class PlayableSceneBoot : MonoBehaviour
    {
        public GameLoop Game;

        void Start() => Run();

        public void Run()
        {
            if (Game == null) return;
            // Every phase isolated and logged, same reasoning as Install()'s own
            // header: a body that never spawns and a session that never starts
            // must not be collateral damage from Install() throwing.
            try
            {
                // PlaneController.Awake() (which calls Init()) never runs just from
                // opening a scene in the Editor — PlaneController.cs's own header
                // says so. Safe/idempotent to call directly either way.
                Game.ActivePlane?.Init();
            }
            catch (System.Exception e) { Debug.LogException(e); }

            try { CombatWorldAdapter.Install(Game); }
            catch (System.Exception e) { Debug.LogException(e); }

            try
            {
                if (Game.Rig == null && Game.BodyPrefabToSpawn != null)
                {
                    var instance = Instantiate(Game.BodyPrefabToSpawn);
                    Vector3 spawnPos = Game.ActivePlane != null
                        ? Game.ActivePlane.transform.position + Game.ActivePlane.transform.right * 4f
                        : transform.position;
                    Game.SpawnBody(instance, Game.BodyId, spawnPos, 0f);
                }
            }
            catch (System.Exception e) { Debug.LogException(e); }

            try { Game.BeginSession(); }
            catch (System.Exception e) { Debug.LogException(e); }

            Debug.Log($"PLAYABLESCENEBOOT ran started={Game.Started} rig={Game.Rig != null} " +
                      $"plane={Game.ActivePlane != null} combatSystems={Game.CombatSystems.Count}");
        }
    }
}
