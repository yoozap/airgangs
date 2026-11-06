using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Drop-in replacement for legacy `UnityEngine.Input`, used by every
    /// `Input.GetAxis`/`GetAxisRaw`/`GetButton`/`GetButtonDown`/`GetButtonUp`
    /// call across the purchased pack
    /// (ThirdParty/CoverShoot/Scripts/PlayerScripts/*.cs,
    /// LevelScripts/ThirdPersonOrbitCam.cs, LevelScripts/InteractiveWeapon.cs
    /// — the mechanical `Input.` -&gt; `CoverShootInput.` rename in each is
    /// the whole of this fix). Reads the exact same axis/button name strings
    /// the pack already uses (Horizontal/Vertical/Jump/Fire1/Mouse X/Mouse
    /// Y/Mouse ScrollWheel/CS_*), off `Keyboard.current`/`Mouse.current`
    /// (the project's own Input System) instead of the legacy Input Manager.
    ///
    /// FOUND 2026-11-01 by the first real CoverShootRun play-mode run: every
    /// pack behaviour was completely inert (F/RMB/Jump/Aim never fired, and
    /// the hero "moving" on W was gravity settling, not WASD — confirmed
    /// because A/D showed zero LATERAL motion and the Animator sat in
    /// "Fall"). This project's test harness drives input exclusively
    /// through `InputSystem.QueueStateEvent` (HarnessInput.cs), which the
    /// legacy `UnityEngine.Input` class in this project's configuration
    /// never observes. The real player needs this too, not just the test:
    /// every other on-foot/flying binding in this project already reads the
    /// new Input System (Core/GameLoop.cs's Keyboard/Mouse reads) — legacy
    /// Input was never the primary path here, so the pack would have been
    /// just as inert for a real player as it was for the test.
    /// </summary>
    public static class CoverShootInput
    {
        // Alt-toggles-aim latch (2026-11-01, coordinator): the JS build's
        // own semantics is "Alt TOGGLES aim when armed" (game.js:508-513,
        // kept alive in Tests/PlayMode/KeysRun.cs's key_Alt_armedToggles),
        // but AimBehaviour.cs's own Update() drives `aim` purely off
        // CS_Aim being HELD (RMB) — there is no toggle concept inside the
        // pack at all. Rather than teach AimBehaviour a second input model,
        // this latch makes CS_Aim read "held" independently of the mouse
        // once Alt has toggled it on, so a tap behaves exactly like holding
        // RMB from AimBehaviour's point of view (same mechanism, same
        // ToggleAimOn/Off coroutines) — "route Alt to the pack's aim... same
        // as holding RMB", per the coordinator's own wording. Toggled from
        // Core/GameLoop.cs's key dispatch, gated there on _foot.Armed the
        // same way the old HeldWeapon-path Alt binding was.
        static bool _aimLatch;
        public static void ToggleAimLatch() => _aimLatch = !_aimLatch;
        public static void ClearAimLatch() => _aimLatch = false;
        // 2026-11-02 (user: "i dont see aim and i cant shoot"): the latch is also
        // what RMB toggles (Core/GameLoop.cs, game.js:614-623 — the JS TOGGLES aim
        // on right-click, the pack alone only aims while RMB is HELD) and what a
        // bare left-click switches on (ShootBehaviour.Update — foot.js:2480
        // _aimOnDraw "on click we are in aim mode"), so the shot is not swallowed
        // by the pack's own `!isAiming` gate.
        public static bool AimLatched => _aimLatch;
        public static void SetAimLatch(bool on) => _aimLatch = on;

        public static float GetAxis(string name) => GetAxisRaw(name);

        public static float GetAxisRaw(string name)
        {
            var kb = Keyboard.current;
            var ms = Mouse.current;
            switch (name)
            {
                case "Horizontal":
                    if (kb == null) return 0f;
                    return (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f)
                         - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
                case "Vertical":
                    if (kb == null) return 0f;
                    return (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f)
                         - (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f);
                case "Mouse X": return ms != null ? ms.delta.ReadValue().x * 0.1f : 0f; // InputManager.asset's stock Mouse X sensitivity (0.1)
                case "Mouse Y": return ms != null ? ms.delta.ReadValue().y * 0.1f : 0f;
                case "Mouse ScrollWheel": return ms != null ? ms.scroll.ReadValue().y * 0.1f : 0f;
                case "Fire1": return RawButton(name) ? 1f : 0f; // shootButton default — ShootBehaviour.shootButton="Fire1"
                case "CS_Aim": return RawButton(name) ? 1f : 0f;
                case "CS_Change": return RawButton(name) ? 1f : 0f;
                case "Analog X": case "Analog Y": case "CS_AnalogX": case "CS_AnalogY":
                    return 0f; // no gamepad in this project's harness/keyboard-mouse play
                default: return RawButton(name) ? 1f : 0f;
            }
        }

        public static bool GetButton(string name) => RawButton(name);

        static readonly Dictionary<string, bool> _prev = new Dictionary<string, bool>();

        public static bool GetButtonDown(string name)
        {
            bool now = RawButton(name);
            bool was = _prev.TryGetValue(name, out var p) && p;
            _prev[name] = now;
            return now && !was;
        }

        public static bool GetButtonUp(string name)
        {
            bool now = RawButton(name);
            bool was = _prev.TryGetValue(name, out var p) && p;
            _prev[name] = now;
            return was && !now;
        }

        // The one raw boolean every axis/button name above resolves to —
        // matches InputManager.asset's CS_* bindings (Core/GameLoop.cs's
        // own edits list V=cover, R=reload, F=interact, G=drop, Tab=change,
        // mouse1=aim, mouse2=aim-shoulder) and the stock Jump/Sprint/Fire1
        // defaults this project's InputManager.asset also carries.
        static bool RawButton(string name)
        {
            var kb = Keyboard.current;
            var ms = Mouse.current;
            switch (name)
            {
                case "Jump": return kb != null && kb.spaceKey.isPressed;
                case "CS_Sprint": return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                case "CS_Aim": return (ms != null && ms.rightButton.isPressed) || _aimLatch;
                case "CS_AimShoulder": return ms != null && ms.middleButton.isPressed;
                case "Fire1": return (ms != null && ms.leftButton.isPressed) || (kb != null && kb.leftCtrlKey.isPressed);
                case "Fire2": return (ms != null && ms.rightButton.isPressed) || (kb != null && kb.leftAltKey.isPressed);
                case "Fire3": return ms != null && ms.middleButton.isPressed;
                case "CS_Interact": return kb != null && kb.fKey.isPressed;
                case "CS_Change": return kb != null && kb.tabKey.isPressed;
                case "CS_Reload": return kb != null && kb.rKey.isPressed;
                case "CS_Drop": return kb != null && kb.gKey.isPressed;
                case "CS_Cover": return kb != null && kb.vKey.isPressed;
                default: return false;
            }
        }
    }
}
