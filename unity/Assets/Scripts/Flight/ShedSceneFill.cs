using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// THE SCENE COMPONENT for the player's warehouse (+ its crane and wind
    /// tunnel) and the plane hall with the parked fleet.
    ///
    /// WHY THIS FILE EXISTS: Editor/BuildPlayableScene.cs used to call
    /// ShedFill.BuildWarehouse / Props.WarehouseCraneFill.Build /
    /// PlaneHallFill.Fill inline, at BAKE time, so the shed kit, the crane's
    /// cable mesh, the 3 fans and all 35 parked airframes were SERIALISED into
    /// Assets/Scenes/Game.unity as scene objects (~2,000 GameObjects and their
    /// runtime Meshes/Materials). That is the opposite of the contract every
    /// other scenery renderer in this project follows, and which
    /// City/CityRenderer.cs's class header states outright:
    ///
    ///     nothing baked; OnEnable() -> EnsureBuilt() regenerates from the
    ///     data pipeline on every scene load.
    ///
    /// ShopfrontRenderer, VegetationRenderer, ParkRenderer, MetroRenderer,
    /// MallRenderer, StreetFurnitureRenderer and LandmarkRenderer are all
    /// shaped like that; this makes the sheds/crane/fleet the eighth. The
    /// scene now stores one component, and the geometry is built at load.
    ///
    /// IT MUST BE A SCENE COMPONENT AND NOT A CityLoader STAGE: City/
    /// PlaneHallRenderer.cs already exists and already calls
    /// BuildPlaneHall+Fill, but its only caller is CityLoader's "plane hall"
    /// scenery stage, and CityLoader.RunCritical/RunScenery/RunAll have NO
    /// runtime caller at all (only Editor/Probes/CityLoadProbe.cs) — see
    /// .claude/skills/unity/SKILL.md. A stage-only wiring renders during a
    /// probe and never in the game, which is the exact trap this whole
    /// feature was already in.
    ///
    /// ORDERING IS SAFE, AND IT MATTERS: Core/CombatWorldAdapter.Install()
    /// sweeps `FindObjectsByType&lt;PlaneDamageRecord&gt;()` ONCE to build
    /// PlaneDamage.Watch subjects and GameLoop.Targets entries, and
    /// Character/FootBoarding.NearestPlane() walks that registry to decide
    /// which machine Enter boards. Install() is reached from
    /// Core/PlayableSceneBoot.Start(), and Unity delivers EVERY OnEnable in a
    /// loading scene before ANY Start — so the fleet this component builds in
    /// OnEnable is already in the scene when that sweep runs. (PlaneHallFill
    /// is what attaches the PlaneDamageRecord; see its own note.)
    ///
    /// PLAYER-SAFE NOW (see Core/ArtAssets): all three fills resolve
    /// their art with AssetDatabase/PrefabUtility and are wrapped in
    /// `#if UNITY_EDITOR` (ShedFill.cs's header spells this out, as do
    /// PlaneHallFill.cs:272 and WarehouseCraneFill.cs:234), so this component
    /// builds nothing in a PLAYER build — it logs and returns, exactly like
    /// ParkRenderer/ShopfrontRenderer/MetroRenderer and every other member of
    /// the pattern, which share the same limitation. Baking was therefore the
    /// only player-functional option, at 1.7 GB of scene; a player build needs
    /// the FBX/OBJ reachable through Addressables or Resources instead of
    /// AssetDatabase, which is one change for all eight renderers and is NOT
    /// done here. Nothing is silently shipped: that is what this paragraph and
    /// the SHEDSCENEFILL log line below are for.
    /// </summary>
    [ExecuteAlways]
    public sealed class ShedSceneFill : MonoBehaviour
    {
        /// <summary>The warehouse shell/pad/ramps — src/shed.js's buildShed.</summary>
        public ShedFill.ShedBuildResult Warehouse { get; private set; }
        /// <summary>The crane + slung P-51 + 3 fans — src/crane.js.</summary>
        public TbilisiFly.Props.WarehouseCraneFill.Result Crane { get; private set; }
        /// <summary>The plane hall shell — src/planehall.js:116-134.</summary>
        public ShedFill.ShedBuildResult Hall { get; private set; }
        /// <summary>The 35 parked machines — src/planehall.js:221-312 `_fill`.</summary>
        public PlaneHallFill.FillResult Fleet { get; private set; }

        public bool Built { get; private set; }

        // Private and unserialised ON PURPOSE (same as CityRenderer/
        // ParkRenderer's own `_built`): a domain reload or a scene load must
        // come back false so EnsureBuilt runs again from the data pipeline.
        bool _built;

        void OnEnable() => EnsureBuilt();

        /// <summary>Regenerate everything if nothing is built yet — safe to
        /// call repeatedly, same contract as CityRenderer.EnsureBuilt.</summary>
        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            ClearChildren();

            // buildings:false — this component needs only the geo header
            // (OriginLat/OriginLon/MLat/MLon) and the terrain sampler, exactly
            // what ParkRenderer.EnsureBuilt loads for the same reason.
            var city = CityData.Load(buildings: false);
            if (city == null || city.Terrain == null)
            {
                Debug.LogWarning("SHEDSCENEFILL no city data — warehouse/plane hall not built.");
                return;
            }

            var meta = new PlaneHallLayout.GeoMeta
            {
                OriginLat = city.OriginLat, OriginLon = city.OriginLon,
                MLat = city.MLat, MLon = city.MLon,
            };
            System.Func<float, float, float?> heightAtGame = (x, z) => city.Terrain.HeightAtGame(x, z);

            // ---- the warehouse (the player's base) ----
            var warehouse = ShedFill.BuildWarehouse(transform, meta, heightAtGame);
            Warehouse = warehouse;
            Debug.Log($"GAMESCENE warehouse=1 shell_w={warehouse.Shell.Width:F1} shell_l={warehouse.Shell.Length:F1}");

            // ---- its crane / wind tunnel. Once this has run, Props/
            // WarehouseFanSwitch and Props/WarehouseCraneSlingRig are
            // self-attached MonoBehaviours Unity ticks itself (Update/
            // LateUpdate), and Props/WindRider.Tick has its only caller
            // (WarehouseFanSwitch.cs:220). AddComponent at runtime delivers
            // Awake synchronously, so moving the build from the bake into
            // OnEnable does not cost either of them a frame. ----
            var crane = TbilisiFly.Props.WarehouseCraneFill.Build(warehouse);
            Crane = crane;
            Debug.Log($"GAMESCENE crane={(crane.Built ? 1 : 0)} scale={crane.ScaleApplied:F3} " +
                $"hook_local_y={crane.HookLocalInCraneUnity.y:F4} cable_pts={crane.CablePolylinePoints}");

            // ---- the plane hall and the parked fleet ----
            var (hallX, hallZ, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw); // src/planehall.js:85-99
            var hall = ShedFill.BuildPlaneHall(transform, hallX, hallZ, ShedLayout.ShedYaw, heightAtGame);
            Hall = hall;
            var hallFill = PlaneHallFill.Fill(hall);
            Fleet = hallFill;
            Debug.Log($"GAMESCENE planehall=1 parked={hallFill.Parked.Count} failed={hallFill.Failed.Count} " +
                $"tanks={hallFill.TankCount} dps={hallFill.DpsCount}");
            foreach (var id in hallFill.Failed)
                Debug.LogWarning($"GAMESCENE plane_failed id={id}");
            if (hallFill.Parked.Count != 35)
                Debug.LogWarning($"GAMESCENE parked count {hallFill.Parked.Count} != 35 expected");

            // EVERY AEROPLANE THIS COMPONENT PUT IN THE SCENE, counted as real
            // components on real objects — this is the list
            // Core/CombatWorldAdapter.Install()'s
            // FindObjectsByType<PlaneDamageRecord>() sweep will find, and
            // therefore the exact set that is clickable/Tab-able, has a target
            // card with hull points, and takes gunfire damage.
            //
            // 36, not 35: the 35 parked machines (PlaneHallFill) PLUS the P-51
            // on the warehouse crane's hook. src/crane.js:361 registers the
            // slung machine exactly like a parked one and the port did not
            // (docs/open_items.json `crane-plane-no-record`, closed 2026-10-31
            // in Props/WarehouseCraneFill.HangPlane). A count line here rather
            // than in CombatWorldAdapter because the sweep is global: if this
            // reads 36 and the sweep reads fewer, the gap is a lifetime
            // problem and not a missing component.
            int hallRecs = 0, craneRecs = 0;
            foreach (var p in hallFill.Parked)
                if (p.GameObject != null && p.GameObject.GetComponent<TbilisiFly.Combat.PlaneDamageRecord>() != null) hallRecs++;
            if (crane.PlaneGo != null && crane.PlaneGo.GetComponent<TbilisiFly.Combat.PlaneDamageRecord>() != null) craneRecs++;
            int wantRecs = hallFill.Parked.Count + (crane.PlaneGo != null ? 1 : 0);
            Debug.Log($"GAMESCENE planeDamageRecords={hallRecs + craneRecs}/{wantRecs} " +
                      $"hall={hallRecs}/{hallFill.Parked.Count} crane={craneRecs}/{(crane.PlaneGo != null ? 1 : 0)} " +
                      $"craneNoBoard={(crane.PlaneGo != null && crane.PlaneGo.GetComponent<TbilisiFly.Character.NoBoardMarker>() != null).ToString().ToLowerInvariant()}");

            Built = true;
            Debug.Log($"SHEDSCENEFILL built=true {ArtAssets.Tag} " +
                      $"children={transform.childCount}");
        }

        /// <summary>Drop a previous build (a domain reload in an open editor
        /// re-fires OnEnable while the old GameObjects are still alive — the
        /// duplicate-geometry case ParkRenderer.ClearChildren exists for).</summary>
        void ClearChildren()
        {
            if (transform.childCount == 0) return;
            var kids = new List<GameObject>(transform.childCount);
            for (int i = 0; i < transform.childCount; i++) kids.Add(transform.GetChild(i).gameObject);
            foreach (var k in kids)
            {
                if (k == null) continue;
                if (Application.isPlaying) Destroy(k);
                else DestroyImmediate(k);
            }
        }
    }
}
