#if UNITY_INCLUDE_TESTS
using UnityEngine;
using UnityEngine.InputSystem;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Reads the virtual keyboard from a REAL LateUpdate, the same phase
    /// Core/GameLoop.ReadDiscreteKeys runs in. A test coroutine resumes
    /// between Update and LateUpdate, so it cannot see what
    /// `wasPressedThisFrame` looked like to the game — and "the key press was
    /// never delivered" and "the handler ran and declined" produce the exact
    /// same symptom (Mode never changes). This separates them.
    ///
    /// Its own file, named after it, per the project rule about a
    /// MonoBehaviour in a file named after something else.
    /// </summary>
    public sealed class KeySpy : MonoBehaviour
    {
        public int EnterEdges, SpaceEdges, WEdges;
        public int EnterHeld, SpaceHeld, WHeld, UpHeld;
        public int Frames;

        /// <summary>Every DISCRETE key Core/GameLoop.ReadDiscreteKeys binds.
        /// Summed into <see cref="AnyEdges"/> so a test can ask "did ANY key
        /// edge reach the game this phase" without naming one.</summary>
        static readonly Key[] Discrete =
        {
            Key.Enter, Key.NumpadEnter, Key.Tab, Key.V, Key.C, Key.X, Key.Z, Key.F, Key.G,
            Key.M, Key.H, Key.Escape, Key.P, Key.Backquote, Key.R, Key.N, Key.T, Key.Space,
            Key.LeftAlt, Key.RightAlt, Key.LeftShift,
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0,
        };

        /// <summary>Total `wasPressedThisFrame` hits across <see cref="Discrete"/>,
        /// counted in LateUpdate — i.e. counted where the game counts them.</summary>
        public int AnyEdges;

        void LateUpdate()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            Frames++;
            if (kb.enterKey.wasPressedThisFrame) EnterEdges++;
            if (kb.spaceKey.wasPressedThisFrame) SpaceEdges++;
            if (kb.wKey.wasPressedThisFrame) WEdges++;
            if (kb.enterKey.isPressed) EnterHeld++;
            if (kb.spaceKey.isPressed) SpaceHeld++;
            if (kb.wKey.isPressed) WHeld++;
            if (kb.upArrowKey.isPressed) UpHeld++;
            for (int i = 0; i < Discrete.Length; i++)
                if (kb[Discrete[i]].wasPressedThisFrame) AnyEdges++;
        }
    }
}
#endif
