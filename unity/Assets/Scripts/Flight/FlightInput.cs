using UnityEngine;
using UnityEngine.InputSystem;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// Keyboard -> StickInput. Byte-for-byte against src/game.js:1468-1481
    /// `_readStick()` — "GTA SA controls: W/Shift gas, S/Ctrl brake, up/down
    /// pitch, left/right or A/D roll, Q/E rudder." Uses the Input System
    /// package already in the project (com.unity.inputsystem 1.20.1) rather
    /// than the legacy Input class.
    ///
    /// The mouse does NOT feed the stick in the JS build: pointer movement
    /// (game.js:654-675, `this.look`) only orbits the chase camera, never
    /// `this.vel`/`this.rates`. That orbit is ported in FlightCamera.cs, not
    /// here — there is deliberately no mouse axis read in this file.
    ///
    /// Space (src/guns.js:482, fire the belly gun) is exposed as
    /// `FirePressed` so the binding exists for whatever wires up combat next,
    /// but nothing in this flyable slice consumes it — Combat/ is out of
    /// scope for this task.
    /// </summary>
    public class FlightInput : MonoBehaviour
    {
        public StickInput Read()
        {
            var kb = Keyboard.current;
            if (kb == null) return default;

            float throttle =
                ((kb.wKey.isPressed || kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) ? 1f : 0f) -
                ((kb.sKey.isPressed || kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) ? 1f : 0f);

            float pitch =
                (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);

            float roll =
                ((kb.leftArrowKey.isPressed || kb.aKey.isPressed) ? 1f : 0f) -
                ((kb.rightArrowKey.isPressed || kb.dKey.isPressed) ? 1f : 0f);

            float rudder =
                (kb.qKey.isPressed ? 1f : 0f) - (kb.eKey.isPressed ? 1f : 0f);

            return new StickInput { Throttle = throttle, Pitch = pitch, Roll = roll, Rudder = rudder };
        }

        public bool FirePressed => Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
    }
}
