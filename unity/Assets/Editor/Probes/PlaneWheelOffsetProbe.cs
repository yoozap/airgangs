using System.Collections.Generic;
using TbilisiFly.Core;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE PROOF for docs/QUALITY_BAR.md "HALF-FIXED: the 1.7 m wheel offset" —
    /// the flying half. src/planes.js:1563-1564 normalises every airframe at
    /// BUILD time so its belly sits at local y=-1.7, which is what makes the
    /// JS's global WHEEL_HEIGHT=1.7 safe everywhere downstream. This port never
    /// replicated that drop (PlaneHallFill.cs's own fix for the PARKED half is
    /// already landed and verified by PlaneHallFillProbe). This probe proves
    /// the FLYING half: PlaneController.BellyOffset / PlaneGroundProbe.
    /// WheelHeightFor / PlanePhysics.cs's LooseAirframeActor._bellyOffset all
    /// now key off each airframe's OWN measured belly (-BoxOf(w,w).min.y)
    /// instead of the universal 1.7 m constant, which was WRONG IN OPPOSITE
    /// DIRECTIONS for the two non-zero-belly airframes on disk: mig21
    /// (min.y=0.061) and spitfire (min.y=0.469).
    ///
    /// Two levels of proof, per airframe:
    ///   1. WHEELFIX — the raw formula (WheelHeightFor) placed at a KNOWN
    ///      ground height, then the WORLD belly re-measured independently
    ///      (a fresh BoxOf against an identity "world frame" object, not the
    ///      same transform the placement math used) and diffed against the
    ///      ground height it was asked to sit on.
    ///   2. TOUCHDOWN — the REAL code path (PlaneGroundRoll.Touchdown ->
    ///      Land()), not a reimplemented formula: a plane is dropped onto a
    ///      known ground height with a moderate sink rate and the resulting
    ///      world belly position is checked the same independent way. This is
    ///      the "proves the calculation is REACHED" half (unity SKILL.md
    ///      "A probe proves a CALCULATION, not that the calculation is
    ///      REACHED") — PlaneGroundRoll.Step/Touchdown/Land are the methods
    ///      PlaneController actually calls every frame.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity ^
    ///         -executeMethod TbilisiFly.Probes.PlaneWheelOffsetProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class PlaneWheelOffsetProbe
    {
        // Same id/path table as PlaneCockpitBakeProbe.RunAirboxSurvey — the
        // two known non-zero-belly airframes (mig21, spitfire) plus two
        // known zero-belly ones (p51, b737) for contrast.
        static readonly (string id, string path)[] Cases =
        {
            ("p51",      "Assets/Resources/Art/Planes/p51.fbx"),
            ("b737",     "Assets/Resources/Art/Planes/b737.fbx"),
            ("mig21",    "Assets/Resources/Art/Planes/mig21.fbx"),
            ("spitfire", "Assets/Resources/Art/Props/spitfire.obj"),
        };

        const float Tol = 0.01f; // m — project standard (2 cm geometry, this is coarser on purpose)

        sealed class NullWorld : IGroundProbeWorld
        {
            public float? HeightAt(float x, float z) => null;
            public float? BoreFloorAt(float x, float z, float refY) => null;
            public IReadOnlyList<Vector3> DownwardHits(float x, float z, float originY) => null;
        }

        public static void Run()
        {
            bool allOk = true;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // Identity reference frame: BoxOf(mesh, worldFrame) with
                // worldFrame at the scene origin/identity returns TRUE WORLD
                // coordinates, independent of wherever the plane's own
                // transform has been moved to — the whole point of
                // re-measuring rather than trusting the placement formula.
                var worldFrame = new GameObject("WORLD_FRAME").transform;

                foreach (var (id, path) in Cases)
                {
                    var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (fbx == null) { Debug.LogError($"WHEELFIX id={id} MISSING asset {path}"); allOk = false; continue; }

                    var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                    go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                    float rawMinY = PlaneCockpitMeasurement.BoxOf(go.transform, go.transform).min.y;
                    float bellyOffset = -rawMinY;
                    Debug.Log($"AIRBOX id={id} minY={rawMinY:F3} bellyOffset={bellyOffset:F3}");

                    var gp = new PlaneGroundProbe(new NullWorld()) { BellyOffset = bellyOffset };

                    // ---- 1. WHEELFIX: raw formula, both gear states, two ground heights ----
                    foreach (float groundY in new[] { 0f, 37.4f })
                    {
                        foreach (float gear in new[] { 1f, 0f })
                        {
                            float wheelH = gp.WheelHeightFor(gear);
                            go.transform.position = new Vector3(5f, groundY + wheelH, 5f);

                            float actualBellyY = PlaneCockpitMeasurement.BoxOf(go.transform, worldFrame).min.y;
                            float expectedBellyY = gear != 0f ? groundY : groundY - PlaneGroundProbe.GearCollapseSink;
                            float diff = actualBellyY - expectedBellyY;
                            bool pass = Mathf.Abs(diff) < Tol;
                            allOk &= pass;
                            Debug.Log($"WHEELFIX id={id} groundY={groundY:F2} gear={gear:F0} wheelH={wheelH:F3} " +
                                $"actualBellyY={actualBellyY:F3} expectedBellyY={expectedBellyY:F3} diff={diff:F4} " +
                                $"pass={pass.ToString().ToLowerInvariant()}");
                        }
                    }

                    // ---- 2. TOUCHDOWN: the real PlaneGroundRoll code path ----
                    var hull = new PlaneHullDamage(); // Struct = Intact -> Gear = 1
                    var groundRoll = new PlaneGroundRoll(gp, hull) { HalfSpan = 4.5f };
                    var aero = new FlightModel(
                        new AeroPlaneDef { Prop = true, ClMax = 1.6f, GBreak = 9f, Tw = 2.4f, MaxSpeed = 300f },
                        11f, 5.5f);

                    const float touchdownGroundY = 100f; // arbitrary known flat ground
                    go.transform.SetPositionAndRotation(
                        new Vector3(0f, touchdownGroundY + 5f, 0f), Quaternion.identity);
                    float speed = 40f;
                    Vector3 vel = new Vector3(0f, -3f, 40f); // sink=3 m/s — below every crash/collapse threshold
                    Vector3 rates = Vector3.zero;

                    bool grounded = groundRoll.Touchdown(go.transform, ref speed, ref vel, ref rates, touchdownGroundY, aero);
                    float bellyAfterTouchdown = PlaneCockpitMeasurement.BoxOf(go.transform, worldFrame).min.y;
                    float tdDiff = bellyAfterTouchdown - touchdownGroundY;
                    bool tdPass = grounded && !hull.Crashed && Mathf.Abs(tdDiff) < Tol;
                    allOk &= tdPass;
                    Debug.Log($"TOUCHDOWN id={id} groundY={touchdownGroundY:F2} grounded={grounded.ToString().ToLowerInvariant()} " +
                        $"crashed={hull.Crashed.ToString().ToLowerInvariant()} bellyAfter={bellyAfterTouchdown:F3} " +
                        $"diff={tdDiff:F4} pass={tdPass.ToString().ToLowerInvariant()}");

                    Object.DestroyImmediate(go);
                }

                Debug.Log($"WHEELFIX_ALL pass={allOk.ToString().ToLowerInvariant()}");
                ProbeExit.Exit(allOk ? 0 : 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"WHEELFIX FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}
