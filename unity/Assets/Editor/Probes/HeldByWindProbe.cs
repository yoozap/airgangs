using System.Reflection;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the src/foot.js:5491-5539 `heldByWind` standdown branch this pass
    /// added to FootController.Update() actually SUPPRESSES the movement/physics
    /// it claims to, rather than merely being readable. Same three claims as
    /// tools/_probe_heldbywind.mjs (the JS-side probe this mirrors — console
    /// comment there: "again i cant move on wing" is the bug this exists to
    /// keep fixed):
    ///   1. with the flag down, ordinary W/Shift/Space/strafe input moves the
    ///      body (the CONTROL — proves the harness itself is not inert);
    ///   2. with the flag up, the SAME input moves it not at all, Speed/Vy are
    ///      zeroed, Grounded is forced false, and a buffered jump does not fire
    ///      — and a roll/slide/dive/climb already in progress is torn down
    ///      rather than left running (an _endClimb('abort')/_endRoll/_endSlide/
    ///      _endDive each prints BEFORE=1 AFTER=0);
    ///   3. clearing the flag hands the body back — the same input moves it
    ///      again.
    ///
    /// No MonoBehaviour/scene/asset load needed for 1-3 and 5 (roll/slide/dive
    /// are pure timer+velocity state, no collider involved). Claim 4 (climb)
    /// needs one real BoxCollider standing in as the "ledge" FootClimb.TryStart
    /// raycasts for — built and torn down by this probe, the same technique
    /// tools/_probe_heldbywind.mjs uses on the warehouse's own ammo crate.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.HeldByWindProbe.Run
    /// </summary>
    public static class HeldByWindProbe
    {
        static CharacterRig MakeRig(string id = "banana")
        {
            var model = new GameObject("model_" + id);
            var rig = new CharacterRig(model, id);
            return rig;
        }

        static readonly BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static object Priv(FootController f, string name)
        {
            var fi = typeof(FootController).GetField(name, NP);
            if (fi == null) { Debug.LogError($"HELDBYWIND: no private field '{name}' on FootController — probe is stale"); return null; }
            return fi.GetValue(f);
        }
        static bool IsNull(FootController f, string name) => Priv(f, name) == null;

        public static void Run()
        {
            // ---- 1. CONTROL: not held, full input moves him -----------------------
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f };
                var m = new FootController.MoveInput { Z = -1f, Sprint = true };
                Vector3 p0 = foot.Pos;
                for (int i = 0; i < 90; i++) foot.Update(1f / 60f, rig, m);
                float moved = Vector3.Distance(p0, foot.Pos);
                Debug.Log($"HELDBYWIND id=control held=False moved={moved:F3} (this is the control — must be > 0)");
            }

            // ---- 2. HELD: the same input moves him not at all ---------------------
            FootController foot2; CharacterRig rig2;
            {
                rig2 = MakeRig();
                foot2 = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f };
                foot2.HeldByWindProvider = () => true;
                var m = new FootController.MoveInput { Z = -1f, X = 1f, StrafeX = 1f, Sprint = true, Jump = true, Roll = true };
                Vector3 a = foot2.Pos;
                for (int i = 0; i < 90; i++) foot2.Update(1f / 60f, rig2, m);
                Vector3 b = foot2.Pos;
                float moved = Vector3.Distance(a, b);
                bool rollStarted = !IsNull(foot2, "_roll");
                Debug.Log($"HELDBYWIND id=held held=True moved={moved:F3} speed={foot2.Speed:F3} vy={foot2.Vy:F3} " +
                    $"grounded={foot2.Grounded} jumpBuf={(float)Priv(foot2, "_jumpBuf"):F3} accV={(float)Priv(foot2, "_accV"):F3} " +
                    $"speedWas={(float)Priv(foot2, "_speedWas"):F3} rollStartedDespiteKeyHeld={rollStarted} " +
                    "(moved must be 0, grounded False, rollStartedDespiteKeyHeld False)");
            }

            // ---- 3. RELEASED: clearing the flag hands the body back ---------------
            {
                foot2.HeldByWindProvider = () => false;
                foot2.Grounded = true; // the rider would have put him back on something solid before letting go
                var m = new FootController.MoveInput { Z = -1f, Sprint = true };
                Vector3 c = foot2.Pos;
                for (int i = 0; i < 90; i++) foot2.Update(1f / 60f, rig2, m);
                float moved = Vector3.Distance(c, foot2.Pos);
                Debug.Log($"HELDBYWIND id=released held=False moved={moved:F3} (must be > 0 again, same body that read 0 above)");
            }

            // ---- 4. a ROLL already running is torn down, not left to finish -------
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f, Speed = 5f };
                bool started = foot.StartRoll(rig, true, new FootController.MoveInput { Z = -1f });
                bool before = !IsNull(foot, "_roll");
                foot.HeldByWindProvider = () => true;
                foot.Update(1f / 60f, rig, new FootController.MoveInput());
                bool after = !IsNull(foot, "_roll");
                Debug.Log($"HELDBYWIND id=rollabort started={started} before={before} after={after} (before True, after False)");
            }

            // ---- 5. a SLIDE already running is torn down ---------------------------
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f, Speed = 5f };
                bool started = foot.StartSlide(rig, true, force: true);
                bool before = !IsNull(foot, "_slide");
                foot.HeldByWindProvider = () => true;
                foot.Update(1f / 60f, rig, new FootController.MoveInput());
                bool after = !IsNull(foot, "_slide");
                Debug.Log($"HELDBYWIND id=slideabort started={started} before={before} after={after} (before True, after False)");
            }

            // ---- 6. a DIVE already running is torn down -----------------------------
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = false, Pos = Vector3.zero, Yaw = 0f, Speed = 5f, Vy = -2f };
                bool started = foot.StartDive(rig, new FootController.MoveInput { Z = -1f });
                bool before = !IsNull(foot, "_dive");
                foot.HeldByWindProvider = () => true;
                foot.Update(1f / 60f, rig, new FootController.MoveInput());
                bool after = !IsNull(foot, "_dive");
                Debug.Log($"HELDBYWIND id=diveabort started={started} before={before} after={after} (before True, after False)");
            }

            // ---- 7. COVER already active is left ('silent'), restoring crouch -----
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f };
                var fr = new FootCover.FindResult { Point = new Vector3(0f, 0f, -1f), N = new Vector3(0f, 0f, 1f), Low = false, Dist = 1f };
                foot.Cover.Enter(rig, fr, foot.Pos, foot.Yaw, wasCrouched: false);
                bool before = foot.Cover.Active;
                foot.HeldByWindProvider = () => true;
                foot.Update(1f / 60f, rig, new FootController.MoveInput());
                bool after = foot.Cover.Active;
                Debug.Log($"HELDBYWIND id=coverleave started={before} before={before} after={after} (before True, after False)");
            }

            // ---- 8. a CLIMB already in progress is ended ('abort'), not left to ---
            //         finish, mid-reach, while the rider owns the frame ------------
            {
                var rig = MakeRig();
                var foot = new FootController { Grounded = true, Pos = Vector3.zero, Yaw = 0f };
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    // top at y=1.0 (rise 1.0, between StepUp 0.65 and ClimbMax 2.6),
                    // footprint wide/deep enough to cover BOTH of FindLedge's probe
                    // reaches (0.55 and 1.0 m) straight ahead of yaw=0's facing
                    // (-Z, this project's own "faces -Z" convention).
                    box.transform.position = new Vector3(0f, 0.5f, -1.0f);
                    box.transform.localScale = new Vector3(4f, 1f, 4f);
                    Physics.SyncTransforms();
                    bool started = foot.Climb.TryStart(rig, foot.Pos, foot.Yaw, true, foot.Pos.y, "L",
                        new[] { new Vector3(0f, 0f, -1f) });
                    bool before = foot.Climb.Active;
                    foot.HeldByWindProvider = () => true;
                    foot.Update(1f / 60f, rig, new FootController.MoveInput());
                    bool after = foot.Climb.Active;
                    Debug.Log($"HELDBYWIND id=climbabort started={started} before={before} after={after} (started+before True, after False)");
                }
                finally { Object.DestroyImmediate(box); }
            }
        }
    }
}
