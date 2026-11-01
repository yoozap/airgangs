using TbilisiFly.Character;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE PROOF the task brief asked for: PlaneCockpitBaker.Bake() was fully
    /// built (PlaneCockpitMeasurement.Measure + PlaneCockpitKit.FitCockpitKit/
    /// BuildPlaneShell/HingeHood) and, before this pass, called from NOWHERE —
    /// not for the hall's 35 parked planes, not even for the player's own
    /// flyable aircraft. This probe runs the REAL bake against the REAL
    /// Assets/Resources/Art/Planes/p51.fbx + Assets/Resources/Art/Props/cockpit.fbx on disk (not a
    /// fixture) and prints the SAME fields tools/_probe_cockpit.mjs prints for
    /// p51 off the live src/planes.js (`node tools/_probe_cockpit.mjs p51`):
    ///
    ///   plane        span       ->  grown      x     cockpit room   seat y/z        found in
    ///   p51            11.3 m ->   11.3 m   x1.00     1.1 m   x    0   0.12  -0.99   hull profile
    ///
    /// i.e. seat=(0.00, 0.12, -0.99) wrapper-local, span unchanged (grow=1.00),
    /// room=1.1 m, found via the hull-profile fallback (no canopy/seat-named
    /// mesh in this FBX). Also proves the FOLLOW-ON furniture/controls/shell/
    /// hood actually get built from that measurement, and that the stick/
    /// throttle/pedal GRIPS (what FootBoarding solves hands onto) land within
    /// plausible reach of the seat — not just that Bake() returned non-null.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity ^
    ///         -executeMethod TbilisiFly.Probes.PlaneCockpitBakeProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class PlaneCockpitBakeProbe
    {
        const string PlaneFbxPath = "Assets/Resources/Art/Planes/p51.fbx";
        const string CockpitKitPath = "Assets/Resources/Art/Props/cockpit.fbx";

        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var planeFbx = AssetDatabase.LoadAssetAtPath<GameObject>(PlaneFbxPath);
                var kitFbx = AssetDatabase.LoadAssetAtPath<GameObject>(CockpitKitPath);
                if (planeFbx == null) { Debug.LogError($"COCKPITBAKE missing {PlaneFbxPath}"); ProbeExit.Exit(1); return; }
                if (kitFbx == null) { Debug.LogError($"COCKPITBAKE missing {CockpitKitPath}"); ProbeExit.Exit(1); return; }

                // Unparented, at identity — same "world == local" measurement
                // trick every other baker/probe in this project relies on
                // (PlaneHallFill.cs's own header documents it at length).
                var planeGo = (GameObject)PrefabUtility.InstantiatePrefab(planeFbx);
                planeGo.name = "p51";
                planeGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var def = Planes.PlaneById("p51");
                var airBox = PlaneCockpitMeasurement.BoxOf(planeGo.transform, planeGo.transform);
                Debug.Log($"AIRBOX id=p51 minY={airBox.min.y:F3} maxY={airBox.max.y:F3} minZ={airBox.min.z:F3} maxZ={airBox.max.z:F3} " +
                    $"height={(airBox.max.y - airBox.min.y):F3}");

                var baker = planeGo.AddComponent<PlaneCockpitBaker>();
                var rig = PlaneCockpitBaker.Bake(planeGo, def, kitFbx, flyable: true, out var measured);

                if (measured == null)
                {
                    Debug.LogError("COCKPITBAKE measured=null — findCockpit found nothing on the real p51.fbx mesh");
                    ProbeExit.Exit(1);
                    return;
                }

                float spanAfter = rig.HalfSpanValue * 2f;
                Debug.Log($"COCKPIT id=p51 seatX={measured.Seat.x:F2} seatY={measured.Seat.y:F2} seatZ={measured.Seat.z:F2} " +
                    $"span={spanAfter:F2} grow={measured.Grow:F2} room={measured.Room:F2} wantGrow={measured.WantGrow:F2} " +
                    $"sane={measured.Sane.ToString().ToLowerInvariant()} closed={measured.Closed.ToString().ToLowerInvariant()} " +
                    $"roomy={measured.Roomy.ToString().ToLowerInvariant()} from={measured.From} " +
                    $"deck={measured.Deck:F2} halfW={measured.HalfW:F2} hasHood={(measured.Hood != null)}");

                // ---- furniture/controls/shell/hood actually built? ----
                bool hasKit = rig.CockpitKitValue != null;
                bool hasControls = rig.ControlsValue != null;
                bool hasShell = rig.ShellValue != null;
                int shellBoxes = hasShell ? rig.ShellValue.GetComponents<BoxCollider>().Length : 0;
                Debug.Log($"COCKPITKIT id=p51 hasKit={hasKit} hasControls={hasControls} hasShell={hasShell} " +
                    $"shellBoxCount={shellBoxes} hoodHinge={(baker.Hood != null)}");

                if (hasControls)
                {
                    var c = rig.ControlsValue;
                    LogGrip("stick", c.Stick, measured.Seat);
                    LogGrip("throttle", c.Throttle, measured.Seat);
                    LogGrip("pedalR", c.PedalR, measured.Seat);
                    LogGrip("pedalL", c.PedalL, measured.Seat);
                }

                // ---- SetFlyable round-trip (planes.js:1392-1407) — a hall
                // plane's scenery kit swapped for the flyable one, same code
                // path PlaneHallFill.cs/a future "board this parked plane"
                // system would call. ----
                baker.SetFlyable(false);
                bool controlsGoneWhenParked = baker.Rig.ControlsValue == null;
                baker.SetFlyable(true);
                bool controlsBackWhenFlyable = baker.Rig.ControlsValue != null;
                Debug.Log($"COCKPITFLYABLE id=p51 controlsGoneWhenParked={controlsGoneWhenParked} " +
                    $"controlsBackWhenFlyable={controlsBackWhenFlyable}");

                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"COCKPITBAKE FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }

        /// <summary>Quick multi-airframe AIRBOX survey — checks whether p51's
        /// min.y==0.000 (belly AT the model's own local origin) is one
        /// asset's quirk or a project-wide FBX-import convention, before
        /// anyone decides PlanePhysicsConst.Wheel/PlaneHallLayout.Wheel (both
        /// documented as "plane origin ABOVE its wheels" — i.e. expecting
        /// min.y around -1.7, not 0) need fixing.
        ///   Unity -batchmode -quit -nographics -projectPath unity ^
        ///         -executeMethod TbilisiFly.Probes.PlaneCockpitBakeProbe.RunAirboxSurvey -logFile /dev/stdout
        /// </summary>
        public static void RunAirboxSurvey()
        {
            string[] ids = { "p51", "spitfire", "f15", "b737", "an225", "cessna310", "tu144", "mig21" };
            foreach (var id in ids)
            {
                string path = id == "spitfire" ? "Assets/Resources/Art/Props/spitfire.obj" : $"Assets/Resources/Art/Planes/{id}.fbx";
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (fbx == null) { Debug.Log($"AIRBOX id={id} missing={path}"); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var box = PlaneCockpitMeasurement.BoxOf(go.transform, go.transform);
                Debug.Log($"AIRBOX id={id} minY={box.min.y:F3} maxY={box.max.y:F3} height={(box.max.y - box.min.y):F3}");
                Object.DestroyImmediate(go);
            }
            ProbeExit.Exit(0);
        }

        static void LogGrip(string name, FootBoarding.ControlPart part, Vector3 seat)
        {
            var gp = part.GripPos;
            if (gp == null) { Debug.Log($"GRIP id={name} found=false"); return; }
            Vector3 v = gp.Value;
            float distFromSeat = Vector3.Distance(v, seat);
            Debug.Log($"GRIP id={name} found=true x={v.x:F3} y={v.y:F3} z={v.z:F3} distFromSeat={distFromSeat:F3}");
        }
    }
}
