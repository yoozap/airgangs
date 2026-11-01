using System.Linq;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves FootController/FootClimb/FootCover/FootPoses reproduce src/foot.js's
    /// measured facts. Prints the SAME KEY=value lines (same ids) as
    /// tools/_probe_foot.mjs so CONST/GAIT/CLIMBPLAN/COVER/LEANSPRING can be
    /// diffed 1:1 with tools/unity_port_check.py — those lines use ONLY the
    /// analytic formulas and FootPoses.ClimbMet.Fallback (the exact numbers
    /// foot.js's own CLIMB_MET_FALLBACK carries), so they do not depend on any
    /// loaded body and should match the JS side to float precision.
    ///
    /// The BODY lines are Unity-only (no JS counterpart, same asymmetry
    /// CharacterProbe.cs's own header explains for its BODY lines): the real
    /// measured climb metrics off the 3 Humanoid bodies' actual bones, which
    /// src/characters.js can only ever report for a live body too, never from
    /// its source text alone.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FootProbe.Run
    /// </summary>
    public static class FootProbe
    {
        static readonly string[] HumanoidBodies = { "banana", "eggplant", "toad" };

        const float Gravity = 18.5f, WalkSpeed = 1.9f, SprintMult = 1.6f, BackMult = 0.45f;
        const float CrouchSpeed = 1.5f, GentSpeed = 1.15f, TurnRate = 3.1f, JumpV = 6.0f, StepUp = 0.65f;
        const float ClimbMax = 2.6f, HangDropMax = 2.0f, HangArm = 0.95f, HangOff = 0.26f, HangMin = 0.7f;
        const float PullRate = 0.75f, PullMin = 0.50f, PullMomentum = 0.75f, PullHip = -0.08f, ChestOver = 0.12f;
        const float MantleTime = 0.55f, HandOver = 0.07f;
        const float CoverEdge = 0.34f, CoverNear = 0.7f, CoverHigh = 1.35f, OutStep = 1.2f;
        const float PeekMin = 1.35f, PeekMax = 2.95f, OutHalf = 1.3f, PopHalf = 1.45f, BlindTurn = 0.48f, BlindTurnOff = 1.0f;
        const float CoverCamBack = 3.9f, CoverCamSide = 1.5f, CoverCamUp = 1.95f, CoverCamMin = 0.9f, CoverCamAhead = 1.6f;
        const float BodyHz = 2.2f, BodyZeta = 0.9f, BodyMax = 0.36f;

        public static void Run()
        {
            Debug.Log($"CONST id=_const gravity={Gravity:F3} walkSpeed={WalkSpeed:F3} sprintMult={SprintMult:F3} " +
                $"crouchSpeed={CrouchSpeed:F3} gentSpeed={GentSpeed:F3} turnRate={TurnRate:F3} jumpV={JumpV:F3} " +
                $"stepUp={StepUp:F3} heroHeight={CharacterRoster.HeroHeight:F3}");

            foreach (var runSpeed in new[] { 7.2f, 5.6f })
            {
                Debug.Log($"GAIT id=run{runSpeed} runSpeed={runSpeed:F3} walk={WalkSpeed:F3} " +
                    $"sprint={runSpeed * SprintMult:F3} back={runSpeed * BackMult:F3} crouch={CrouchSpeed:F3} gent={GentSpeed:F3}");
            }

            var fb = FootPoses.ClimbMet.Fallback;
            foreach (var rise in new[] { StepUp + 0.1f, 1.1f, ClimbMax - 0.1f })
            {
                float h = HangOff + HandOver + fb.ShoulderZ;
                float vert = Mathf.Sqrt(Mathf.Max(0.05f, (HangArm * fb.ArmLen) * (HangArm * fb.ArmLen) - h * h));
                float hangDrop = Mathf.Min(HangDropMax, fb.ShoulderY + vert);
                float catchY = Mathf.Max(0f, rise - hangDrop);
                bool hold = catchY > HangMin || rise > ClimbMax * 0.85f;
                float chestY = Mathf.Max(rise - fb.HipY + PullHip, rise + ChestOver - fb.ShoulderY);
                float lift0 = Mathf.Max(0f, chestY - catchY);
                float pullDur = lift0 > 0.02f ? Mathf.Max(PullMin, PullRate * lift0) * PullMomentum : 0f;
                float high = Mathf.Clamp01((rise - StepUp) / (ClimbMax - StepUp));
                float mantleDur = MantleTime * (0.7f + 0.3f * high) * 0.8f;
                Debug.Log($"CLIMBPLAN id=rise{rise:F2} rise={rise:F3} hangDrop={hangDrop:F4} catchY={catchY:F4} " +
                    $"hold={(hold ? 1 : 0)} lift0={lift0:F4} pullDur={pullDur:F4} mantleDur={mantleDur:F4} highFrac={high:F4}");
            }

            Debug.Log($"COVER id=_edges coverEdge={CoverEdge:F4} coverNear={CoverNear:F4} " +
                $"peekOutGun={(CoverEdge + 0.06f):F4} peekOutOff={(CoverEdge + 0.30f):F4} coverLow={(StepUp + 0.1f):F4} " +
                $"coverHigh={CoverHigh:F4} outStep={OutStep:F4}");
            Debug.Log($"COVER id=_arc peekMinDeg={(PeekMin * Mathf.Rad2Deg):F3} peekMaxDeg={(PeekMax * Mathf.Rad2Deg):F3} " +
                $"outHalfDeg={(OutHalf * Mathf.Rad2Deg):F3} popHalfDeg={(PopHalf * Mathf.Rad2Deg):F3} " +
                $"blindTurnDeg={(BlindTurn * Mathf.Rad2Deg):F3} blindTurnOffDeg={(BlindTurnOff * Mathf.Rad2Deg):F3}");
            Debug.Log($"COVER id=_camera camBack={CoverCamBack:F4} camSide={CoverCamSide:F4} camUp={CoverCamUp:F4} " +
                $"camMin={CoverCamMin:F4} camAhead={CoverCamAhead:F4}");

            // src/foot.js:5062's lean-spring settling claim — CharacterRig's
            // OWN spring (BODY_HZ/BODY_ZETA/BODY_MAX, already ported there),
            // stepped the same number of frames FootClimb.Tick would call
            // SettleBody for a chest-height climb (~1.1 s @ 60 Hz).
            {
                float w0 = 2f * Mathf.PI * BodyHz;
                float x = BodyMax, v = 0f;
                const float h = 1f / 60f;
                int steps = Mathf.RoundToInt(1.1f / h);
                for (int i = 0; i < steps; i++)
                {
                    v += (w0 * w0 * (0f - x) - 2f * BodyZeta * w0 * v) * h;
                    x = Mathf.Clamp(x + v * h, -BodyMax, BodyMax);
                }
                Debug.Log($"LEANSPRING id=_settle startRad={BodyMax:F5} afterClimbRad={x:F6} " +
                    $"afterClimbDeg={(x * Mathf.Rad2Deg):F5} settled={(Mathf.Abs(x) < 0.01f ? 1 : 0)}");
            }

            // ---- real measured climb metrics per Humanoid body (Unity-only; see class header) ----
            foreach (var id in HumanoidBodies)
            {
                string path = $"Assets/Resources/Art/Characters/{id}.fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"BODY id={id} missing=1"); continue; }
                var instance = Object.Instantiate(prefab);
                CharacterRig rig = null;
                try
                {
                    rig = new CharacterRig(instance, id);
                    var m = FootPoses.ClimbMetrics(rig);
                    Debug.Log($"BODY id={id} shoulderY={m.ShoulderY:F4} shoulderZ={m.ShoulderZ:F4} " +
                        $"shoulderHalf={m.ShoulderHalf:F4} armLen={m.ArmLen:F4} hipY={m.HipY:F4} " +
                        $"hipHalf={m.HipHalf:F4} legLen={m.LegLen:F4} ankleOff={m.AnkleOff:F4} torso={m.Torso:F4}");

                    // the standing crouch — FootPoses.SetCrouch's own fall-back
                    // squash flag must be false (0) for every Humanoid body here
                    bool squashed = FootPoses.SetCrouch(rig, 1f);
                    FootPoses.SetCrouch(rig, 0f);
                    Debug.Log($"CROUCH id={id} squashFallback={(squashed ? 1 : 0)}");
                }
                finally
                {
                    if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                    else Object.DestroyImmediate(instance);
                }
            }
        }
    }
}
