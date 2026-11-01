using TbilisiFly.Character;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints the SAME KEY=value lines as tools/_probe_footboard.mjs so the
    /// two can be diffed 1:1 (see tools/unity_port_check.py's existing
    /// convention, e.g. FootProbe.cs / tools/_probe_foot.mjs). Covers the
    /// pure, body-independent slice of FootBoarding.cs — its ported constants
    /// (internal, read directly off the class — AssemblyInfo.cs's
    /// InternalsVisibleTo covers Assembly-CSharp-Editor) and its static
    /// FaceYaw/YawTo methods, run on the exact same input battery the .mjs
    /// probe feeds the REAL foot.js `_faceYaw`/`_yawTo` bodies.
    ///
    /// NOT covered here (needs a live scene this task was told not to open —
    /// see FootBoarding.cs's own header "SEAT-FIT MEASUREMENT (UNVERIFIED)"):
    /// BoardSpot/StartClimb/ClimbStep/SitInPlane/UpdateSeated/FlyControls,
    /// which all need a loaded CharacterRig, a parked plane with a
    /// PlaneCockpitRig, and Physics colliders.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity ^
    ///         -executeMethod TbilisiFly.Probes.FootBoardingProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class FootBoardingProbe
    {
        public static void Run()
        {
            Debug.Log($"CONST id=_const boardDist={FootBoarding.BoardDist:F3} eye={FootBoarding.Eye:F3} " +
                $"seatMargin={FootBoarding.SeatMargin:F3} seatTravel={FootBoarding.SeatTravel:F3} " +
                $"hintRup={FootBoarding.SeatHintR.x:F3} hintRright={FootBoarding.SeatHintR.y:F3} hintRfwd={FootBoarding.SeatHintR.z:F3} " +
                $"hintLup={FootBoarding.SeatHintL.x:F3} hintLright={FootBoarding.SeatHintL.y:F3} hintLfwd={FootBoarding.SeatHintL.z:F3} " +
                $"stickPitch={FootBoarding.StickPitch:F3} stickRoll={FootBoarding.StickRoll:F3} " +
                $"pedalThrow={FootBoarding.PedalThrow:F3} throttleThrow={FootBoarding.ThrottleThrow:F3}");

            // ---- FACEYAW — same 4 cases as tools/_probe_footboard.mjs -------
            RunFace(0, new Vector3(0, 0, 0), new Vector3(3, 0, 4));
            RunFace(1, new Vector3(1, 0, 1), new Vector3(1, 0, 1));
            RunFace(2, new Vector3(-2, 0, 5), new Vector3(6, 0, -3));
            RunFace(3, new Vector3(0, 0, 0), new Vector3(0.005f, 0, 0.005f));

            // ---- YAWTO — same 5 cases as tools/_probe_footboard.mjs ---------
            RunYaw(0, 0f, Mathf.PI / 2f, 0.5f);
            RunYaw(1, 3.0f, -3.0f, 1.0f);
            RunYaw(2, -Mathf.PI + 0.1f, Mathf.PI - 0.1f, 0.25f);
            RunYaw(3, 1.234f, 1.234f, 0.8f);
            RunYaw(4, 0f, Mathf.PI * 4f + 0.3f, 1.0f);
        }

        static void RunFace(int i, Vector3 a, Vector3 b)
        {
            float? r = FootBoarding.FaceYaw(a, b);
            string result = r.HasValue ? r.Value.ToString("F6") : "null";
            Debug.Log($"FACEYAW id=case{i} ax={a.x} az={a.z} bx={b.x} bz={b.z} result={result}");
        }

        static void RunYaw(int i, float a, float b, float k)
        {
            float r = FootBoarding.YawTo(a, b, k);
            Debug.Log($"YAWTO id=case{i} a={a:F6} b={b:F6} k={k:F3} result={r:F6}");
        }
    }
}
