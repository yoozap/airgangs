using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PlaneHallRenderer.cs — the CityLoader "plane hall" wiring. The
    /// underlying bake (ShedFill.BuildPlaneHall + PlaneHallFill.Fill) is
    /// ALREADY real and ALREADY probe-verified by PlaneHallFillProbe.cs
    /// against PlaneHallLayout's own pure math; this probe's job is narrower
    /// and CSONLY (no new JS counterpart — the call-site itself has none):
    /// prove CityLoader.Context's own real, already-loaded data is enough to
    /// drive the real call, prove the fleet actually got drawn (35 real
    /// GameObjects with renderers + colliders, not a stub), and prove the
    /// fleet-visibility switch this port's own perf history flags
    /// ("marks must hide when the hall switches its fleet off") actually
    /// flips the GameObject off, not just a bool nobody reads.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlaneHallRenderProbe.Run
    /// </summary>
    public static class PlaneHallRenderProbe
    {
        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                int lightsBefore = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length;

                // Exactly what CityLoader's "cityData" stage already put on
                // ctx by the time the "plane hall" scenery stage runs — no
                // second CityData.Load() inside the renderer itself.
                var ctx = new CityLoader.Context { City = CityData.Load(buildings: false) };

                var result = PlaneHallRenderer.Render(ctx);

                Debug.Log($"PLANEHALLRENDER built={(result.Built ? 1 : 0)}");
                if (!result.Built)
                {
                    Debug.LogError("PLANEHALLRENDER did not build — aborting probe");
                    ProbeExit.Exit(1);
                    return;
                }

                var hall = result.Hall;
                var fleet = result.Fleet;
                Debug.Log($"CSONLY HALLSHELL w={hall.Shell.Width:F3} l={hall.Shell.Length:F3} h={hall.Shell.Height:F3} " +
                          $"rootName={hall.Root.name} pos_x={hall.Root.transform.position.x:F3} pos_z={hall.Root.transform.position.z:F3}");
                Debug.Log($"CSONLY FLEET parked={fleet.Parked.Count} failed={fleet.Failed.Count} " +
                          $"tanks={fleet.TankCount} dps={fleet.DpsCount} expected=35");
                foreach (var id in fleet.Failed) Debug.Log($"CSONLY FLEETFAIL id={id}");

                // ---- really drawn: real renderers + real colliders on real GameObjects ----
                int withRenderer = 0, withCollider = 0, shadowOff = 0;
                foreach (var p in fleet.Parked)
                {
                    if (p.GameObject == null) continue;
                    var rends = p.GameObject.GetComponentsInChildren<Renderer>(true);
                    if (rends.Length > 0) withRenderer++;
                    if (p.GameObject.GetComponent<BoxCollider>() != null) withCollider++;
                    bool allOff = rends.Length > 0 && rends.All(r => r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off);
                    if (allOff) shadowOff++;
                }
                Debug.Log($"CSONLY FLEETDRAWN withRenderer={withRenderer} withCollider={withCollider} shadowOff={shadowOff} of={fleet.Parked.Count}");

                // ---- the fleet draw switch actually turns drawing off ----
                // Drives the REAL MonoBehaviour.Update() (private, Unity-magic
                // method) via reflection with a real Camera.main, rather than
                // only calling ComputeVisible() — ComputeVisible is pure math
                // (the doc comment's own "testable without a play loop"
                // promise), but the SetActive wiring this task explicitly
                // asks to prove lives in Update(), one line further.
                var camGo = new GameObject("ProbeMainCamera");
                var cam = camGo.AddComponent<Camera>();
                camGo.tag = "MainCamera";
                var updateMethod = typeof(PlaneHallVisibility).GetMethod("Update",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var vis = hall.Root.GetComponent<PlaneHallVisibility>();
                bool hasVis = vis != null;
                bool visibleUp = false, hiddenFar = false, fleetActiveNear = false, fleetActiveFar = true;
                if (hasVis && updateMethod != null)
                {
                    var centre = hall.Root.transform.position;
                    // close, straight down the taxi lane (dz along the hall's
                    // own forward axis) — matches src/planehall.js's own "you
                    // can see straight down the lane" case.
                    var nearPos = centre + hall.Root.transform.forward * 50f + Vector3.up * 2f;
                    camGo.transform.position = nearPos;
                    updateMethod.Invoke(vis, null);
                    visibleUp = vis.FleetVisible;
                    fleetActiveNear = fleet.Fleet != null && fleet.Fleet.activeSelf;

                    // far beyond SHOW_DIST (480 m), off the side (through the wall).
                    var farPos = centre + hall.Root.transform.right * 2000f;
                    camGo.transform.position = farPos;
                    updateMethod.Invoke(vis, null);
                    hiddenFar = vis.FleetVisible;
                    fleetActiveFar = fleet.Fleet != null && fleet.Fleet.activeSelf;
                }
                Debug.Log($"CSONLY VISSWITCH hasComponent={(hasVis ? 1 : 0)} nearVisible={(visibleUp ? 1 : 0)} " +
                          $"fleetActiveNear={(fleetActiveNear ? 1 : 0)} farVisible={(hiddenFar ? 1 : 0)} fleetActiveFar={(fleetActiveFar ? 1 : 0)}");

                // ---- light cost: this call must add zero real Lights ----
                int lightsAfter = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length;
                int added = lightsAfter - lightsBefore;
                Debug.Log($"CSONLY REALLIGHTS before={lightsBefore} after={lightsAfter} added={added}");

                // ---- explicit PASS/FAIL ----
                bool passCount = fleet.Parked.Count == 35 && fleet.Failed.Count == 0;
                bool passDrawn = withRenderer == fleet.Parked.Count && withCollider == fleet.Parked.Count;
                bool passShadowOff = shadowOff == fleet.Parked.Count;
                bool passSwitch = hasVis && fleetActiveNear && !fleetActiveFar;
                bool passNoLights = added == 0;

                Debug.Log($"ASSERT id=fleetCount value={fleet.Parked.Count} need=35 failed={fleet.Failed.Count} result={(passCount ? "PASS" : "FAIL")}");
                Debug.Log($"ASSERT id=fleetActuallyDrawn result={(passDrawn ? "PASS" : "FAIL")}");
                Debug.Log($"ASSERT id=shadowsOffInHall result={(passShadowOff ? "PASS" : "FAIL")}");
                Debug.Log($"ASSERT id=fleetSwitchWorks result={(passSwitch ? "PASS" : "FAIL")}");
                Debug.Log($"ASSERT id=noNewRealLights result={(passNoLights ? "PASS" : "FAIL")}");

                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PLANEHALLRENDERPROBE FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}
