using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the four PURE functions FootController.cs gained to close the
    /// "foot.js finish" gap this port session worked: the bodyOpts stance-
    /// offset layer (src/foot.js:6146-6204) that feeds CharacterRig.SwingBody's
    /// cover/weapon-reach terms. Before this session FootController.cs built
    /// `bodyOpts` without Lean/Bank/ChestPitch/HeadYaw/ChestYaw/GunTwist/GunDip/
    /// GunPitch at all — CharacterRig.SwingBody's own header said so
    /// explicitly ("minus the cover/weapon-reach extension terms").
    ///
    /// Paired with tools/_probe_footrest.mjs, same case ids, same key names —
    /// a probe that reads mesh vertices or bone positions beats a screenshot,
    /// but these four formulas need neither a live rig NOR a scene (no
    /// Transform, no Animator), so this is deterministic in batch mode, the
    /// same precedent as FootAcrobaticsProbe.cs/CrashProbe.cs.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FootRestProbe.Run
    /// </summary>
    public static class FootRestProbe
    {
        public static void Run()
        {
            // ---- src/foot.js:6146-6147 `this._coverW` spring step ----
            Case("coverWeightStep0", FootController.CoverWeightStep(0.0f, true, 1.0f, 0.0f, 0.0f, 0.0f, 0.016f));
            Case("coverWeightStep1", FootController.CoverWeightStep(0.5f, true, 0.8f, 0.3f, 0.0f, 0.0f, 0.016f));
            Case("coverWeightStep2", FootController.CoverWeightStep(0.3f, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.016f));
            Case("coverWeightStep3", FootController.CoverWeightStep(0.0015f, false, 0.0f, 0.0f, 0.0f, 0.0f, 0.016f));

            // ---- src/foot.js:6150-6162 the cover lean vector, projected ----
            Lean("coverLean0", true, new Vector3(1, 0, 0), new Vector3(0, 0, 1), 1, 0.6f, -1, 0.5f, 0.4f, 1.25f, 1.2f, 0.3f, 0.1f, new Vector3(0, 0, 1), new Vector3(1, 0, 0));
            Lean("coverLean1", true, new Vector3(0, 0, 1), new Vector3(1, 0, 0), -1, 0.0f, 0, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, new Vector3(0, 0, 1), new Vector3(1, 0, 0));
            Lean("coverLean2", false, new Vector3(1, 0, 0), new Vector3(0, 0, 1), 1, 0.6f, -1, 0.5f, 0.4f, 1.25f, 1.2f, 0.3f, 0.1f, new Vector3(0, 0, 1), new Vector3(1, 0, 0));

            // ---- src/foot.js:6197-6200 the cover stance's chest/head offsets ----
            Stance("stanceTerms0", true, 1.25f, 0.4f, -1, 0.7f, 1.3f);
            Stance("stanceTerms1", true, 0.0f, 0.6f, 0, 0.5f, -0.5f);
            Stance("stanceTerms2", false, 1.25f, 0.4f, -1, 0.7f, 1.3f);

            // ---- src/foot.js:6163-6171 the aim-twist want ----
            Twist("aimTwist0", true, false, 0.3f, false, false, 0.0f);
            Twist("aimTwist1", true, false, 0.9f, true, false, 0.2f);
            Twist("aimTwist2", true, false, -0.9f, true, true, 0.0f);
            Twist("aimTwist3", false, false, 0.9f, true, false, 0.0f);
            Twist("aimTwist4", true, true, 0.9f, true, false, 0.0f);

            ProbeExit.Exit(0);
        }

        static void Case(string id, float w) => Debug.Log($"FOOTREST case={id} w={w:F5}");

        static void Lean(string id, bool active, Vector3 t, Vector3 n, int side, float peek, int corner, float look,
            float cw, float time, float speed, float accel, float slam, Vector3 fwd, Vector3 right)
        {
            Vector3 lean = FootController.CoverLeanVector(active, t, n, side, peek, corner, look, cw, time, speed, accel, slam);
            var (leanP, leanR) = FootController.ProjectLean(lean, fwd, right);
            Debug.Log($"FOOTREST case={id} leanP={leanP:F5} leanR={leanR:F5}");
        }

        static void Stance(string id, bool active, float time, float cw, int corner, float camS, float headTurnWrapped)
        {
            var (chestPitch, headYaw, chestYaw) = FootController.CoverStanceTerms(active, time, cw, corner, camS, headTurnWrapped);
            Debug.Log($"FOOTREST case={id} chestPitch={chestPitch:F5} headYaw={headYaw:F5} chestYaw={chestYaw:F5}");
        }

        static void Twist(string id, bool held, bool stowing, float aimOff, bool isRifle, bool rifleLeft, float cw)
        {
            float want = FootController.AimTwistWant(held, stowing, aimOff, isRifle, rifleLeft, cw);
            Debug.Log($"FOOTREST case={id} want={want:F5}");
        }
    }
}
