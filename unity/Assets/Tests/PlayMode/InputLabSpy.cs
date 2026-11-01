// Play-mode-only: lives in the predefined Assembly-CSharp, so a PLAYER build
// compiles it too unless it is gated. UNITY_INCLUDE_TESTS is Unity's own
// "the test framework is in this compilation" symbol (see FirstPlayModeRun.cs's
// header for the full story — a player build without this guard fails with
// CS0246 on the NUnit attributes).
#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// The instrument for <see cref="InputEdgeLab"/>. Reads ONE watched key
    /// from a real <c>Update()</c> and a real <c>LateUpdate()</c> — the two
    /// phases that matter, because Core/GameLoop.ReadDiscreteKeys runs in
    /// LateUpdate while a test coroutine resumes between the two — and can
    /// optionally run the test's input pump from inside <c>Update()</c>, which
    /// is a different point in the frame from a coroutine.
    ///
    /// Its own file, named after it, per the project rule about a MonoBehaviour
    /// in a file named after something else.
    /// </summary>
    public sealed class InputLabSpy : MonoBehaviour
    {
        public Key Watch = Key.Enter;
        public int UpEdges, LateEdges, UpHeld, LateHeld, UpFrames, LateFrames;
        public float LateDeltaSum;
        public bool Trace;
        public readonly List<string> Lines = new List<string>();
        /// <summary>Called first thing in Update(). The manual-pump variants
        /// put <c>InputSystem.Update()</c> here instead of in the coroutine.</summary>
        public Action PumpInUpdate;

        public void Zero()
        {
            UpEdges = LateEdges = UpHeld = LateHeld = UpFrames = LateFrames = 0;
            LateDeltaSum = 0f;
            Lines.Clear();
        }

        void Update()
        {
            PumpInUpdate?.Invoke();
            var kb = Keyboard.current;
            if (kb == null) return;
            UpFrames++;
            var k = kb[Watch];
            if (k.wasPressedThisFrame) UpEdges++;
            if (k.isPressed) UpHeld++;
        }

        void LateUpdate()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            LateFrames++;
            var k = kb[Watch];
            bool edge = k.wasPressedThisFrame, held = k.isPressed;
            if (edge) LateEdges++;
            if (held) LateHeld++;
            var m = Mouse.current;
            if (m != null) LateDeltaSum += m.delta.ReadValue().x;
            if (Trace && Lines.Count < 48) Lines.Add(InputLabDiag.Snapshot(k, edge, held));
        }
    }
}
#endif
