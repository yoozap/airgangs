#if UNITY_INCLUDE_TESTS
using UnityEngine;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Samples the hero's pose AFTER every other LateUpdate has run — which is
    /// the pose the renderer draws, and therefore the only one worth asserting
    /// about when the user reports a visual blink.
    ///
    /// A coroutine's `yield return null` resumes at the end of Update: before
    /// the Animator evaluates and before GameLoop's LateUpdate writes the
    /// procedural pose. Sampling there shows a half-built body that no frame
    /// ever displays (it reported a 70 cm hips spike every ~8 frames in a
    /// perfectly steady run). `Application.onBeforeRender` would do, but it
    /// does not fire in a headless test run, so this does it with execution
    /// order instead.
    ///
    /// Own file, named after the type, per this project's "one MonoBehaviour
    /// per file" rule.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class LatePoseSpy : MonoBehaviour
    {
        public Transform Bone;
        public System.Func<float> GroundY;

        public float Last = float.NaN;
        /// <summary>The two terms separately: "hips minus ground" cannot tell
        /// a body that rose from ground that fell away under it.</summary>
        public float LastBoneY = float.NaN, LastGroundY = float.NaN;
        public int Samples;

        void LateUpdate()
        {
            if (Bone == null || GroundY == null) return;
            LastBoneY = Bone.position.y;
            LastGroundY = GroundY();
            Last = LastBoneY - LastGroundY;
            Samples++;
        }
    }
}
#endif
