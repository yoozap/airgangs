#if UNITY_INCLUDE_TESTS
using System.Collections;
using NUnit.Framework;
using TbilisiFly.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace TbilisiFly.Tests
{
    /// <summary>
    /// THE FIRST CLICK. The user's first run of the built player (2026-10-31):
    /// "clicks in login register pages not works, i cant focus on input".
    /// Every probe and the play-mode harness drove the shell by reflection
    /// (SubmitAuth/OpenCharacter/...), so no pointer event had ever been
    /// delivered to a runtime UI Toolkit panel in this port. This test sends a
    /// REAL synthetic mouse click through the Input System at the username
    /// field and reads back what the panel did: what Pick() finds under the
    /// cursor, whether a ClickEvent arrived, whether the field took focus, and
    /// whether typed text lands in it. One LOGINCLICK key=value line per fact.
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode
    ///        -testFilter LoginClickRun -testResults <xml> -logFile <log>
    /// </summary>
    public class LoginClickRun
    {
        static void Fact(string k, object v) => Debug.Log($"LOGINCLICK {k}={v}");
        static void FactB(string k, bool v) => Debug.Log($"LOGINCLICK {k}={v.ToString().ToLowerInvariant()}");

        // No longer [Explicit]: synthetic pointer delivery to a runtime UI
        // Toolkit panel WORKS in the batch harness now (see the bridge below),
        // so this guards the user's actual bug — "clicks in login register
        // pages not works, i cant focus on input" — on every run instead of
        // only when someone remembers to name it.
        [UnityTest, Timeout(600000)]
        public IEnumerator FirstClick()
        {
            // A BATCH EDITOR IS NEVER FOCUSED, and two separate gates drop
            // every pointer event because of it. Until both are opened this
            // test read arm1_pointerDowns=0 on a panel whose Pick() returned
            // the right element and whose Focus() worked — i.e. the symptom
            // looked exactly like the bug it was written to prove fixed.
            //
            //   1. InputManager.ShouldDeferEventBetweenEditorAndPlayerUpdates
            //      (InputManager.cs:3651) takes every Pointer/Keyboard event
            //      OUT of the player update under the default
            //      PointersAndKeyboardsRespectGameViewFocus, so the game never
            //      sees the mouse at all.
            //   2. UIElementsRuntimeUtility.eventSystemUpdateMode defaults to
            //      IgnoreIfAppNotFocused, so even a delivered event is dropped
            //      one step before the panel.
            //
            // Both are EDITOR focus behaviour. A built player has no editor
            // update to defer to and its window is focused, so it passes both
            // by itself — which is why opening them here proves the ShellView
            // `chrome` pickingMode fix on the path a real mouse takes, instead
            // of inventing a new one. Assets/Tests/PlayMode/HarnessInput.cs
            // carries the measurement (InputEdgeLab V1..V12, PD_ARM1).
            Fact("bridge", TbilisiFly.PlayModeTests.HarnessInput.Install());
            Fact("bridgeUi", TbilisiFly.PlayModeTests.HarnessInput.InstallUiPointer());

            TokenStore.ClearToken();
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            for (int i = 0; i < 10; i++) yield return null;

            var boot = Object.FindAnyObjectByType<Boot>();
            FactB("bootInScene", boot != null);
            Fact("screen", boot != null ? boot.Screen.ToString() : "none");

            // the shell panel: the one UIDocument alive at the login screen
            var docs = Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            Fact("uiDocuments", docs.Length);
            UIDocument shell = null;
            foreach (var d in docs) if (d.rootVisualElement != null && d.rootVisualElement.Q<TextField>("auth-user") != null) shell = d;
            FactB("shellDocFound", shell != null);
            if (shell == null) { Assert.Fail("no UIDocument carrying auth-user"); yield break; }
            var root = shell.rootVisualElement;
            var panel = root.panel;
            FactB("panelAttached", panel != null);
            Fact("screenSize", $"{Screen.width}x{Screen.height}");
            Fact("panelSettings", shell.panelSettings != null ? shell.panelSettings.name : "null");
            Fact("inputSystemActions", InputSystem.actions != null ? InputSystem.actions.name : "null(default UI actions)");
            FactB("mouseCurrent", Mouse.current != null);
            FactB("mouseEnabled", Mouse.current != null && Mouse.current.enabled);
            FactB("keyboardEnabled", Keyboard.current != null && Keyboard.current.enabled);

            var field = root.Q<TextField>("auth-user");
            var tabRegister = root.Q<Button>("tab-register");
            var r = field.worldBound;
            Fact("fieldRect", $"({r.x:F0},{r.y:F0},{r.width:F0},{r.height:F0})");
            FactB("fieldOnScreen", r.width > 0 && r.height > 0 && r.xMax <= Screen.width && r.yMax <= Screen.height);

            // what the panel itself says is under the cursor (layout + picking, no input involved)
            var centre = r.center;
            var picked = panel.Pick(centre);
            Fact("pickAtFieldCentre", picked != null ? $"{picked.GetType().Name}:{picked.name}" : "null");
            FactB("pickIsInsideField", picked != null && (picked == field || field.Contains(picked)));

            int clicks = 0, downs = 0, focusIns = 0;
            field.RegisterCallback<ClickEvent>(_ => clicks++);
            field.RegisterCallback<PointerDownEvent>(_ => downs++, TrickleDown.TrickleDown);
            field.RegisterCallback<FocusInEvent>(_ => focusIns++, TrickleDown.TrickleDown);

            // A listener on the panel ROOT as well: "the event never reached
            // this panel" and "it reached the panel and landed on something
            // else" look identical from the field's callbacks alone, and they
            // have completely different causes.
            int rootDowns = 0, rootMoves = 0;
            string lastRootTarget = "<none>";
            root.RegisterCallback<PointerDownEvent>(e =>
            {
                rootDowns++;
                lastRootTarget = (e.target as VisualElement) is VisualElement ve
                    ? $"{ve.GetType().Name}:{ve.name}" : e.target?.GetType().Name ?? "null";
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(_ => rootMoves++, TrickleDown.TrickleDown);

            // and the chain of ancestors over the field, with their picking
            // modes: the original bug was a full-screen `chrome` layer eating
            // the pick (fixed in ShellView to match style.css:1337's
            // pointer-events:none), so the chain is the evidence for that fix.
            {
                var chain = new System.Text.StringBuilder();
                for (var e = field as VisualElement; e != null; e = e.parent)
                    chain.Append(e.name.Length > 0 ? e.name : e.GetType().Name).Append('[').Append(e.pickingMode).Append("] ");
                Fact("fieldAncestry", chain.ToString().TrimEnd());
            }

            // ---- arm 1: a real Input System click (device -> InputForUI -> panel)
            var mouse = Mouse.current ?? InputSystem.AddDevice<Mouse>();
            // UI Toolkit panel space is top-left origin; Input System mouse position is bottom-left origin.
            var screenPos = new Vector2(centre.x, Screen.height - centre.y);
            Fact("mouseScreenPos", $"({screenPos.x:F0},{screenPos.y:F0})");
            // Queue only: the player loop's own input update applies the
            // event at the top of the next frame, which is a real mouse's
            // cadence. The manual InputSystem.Update() this used to call was a
            // placebo in a batch session (InputEdgeLab V1: the Manual update
            // never ran at all; the editor's update was doing the work).
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos });
            for (int i = 0; i < 3; i++) yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos }
                .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
            for (int i = 0; i < 3; i++) yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos });
            for (int i = 0; i < 6; i++) yield return null;

            var focused = panel.focusController?.focusedElement as VisualElement;
            Fact("uiState", TbilisiFly.PlayModeTests.HarnessInput.UiPointerState());
            Fact("arm1_pointerDowns", downs);
            Fact("arm1_clicks", clicks);
            Fact("arm1_focusIns", focusIns);
            Fact("arm1_focused", focused != null ? $"{focused.GetType().Name}:{focused.name}" : "null");
            Fact("arm1_rootPointerDowns", rootDowns);
            Fact("arm1_rootPointerMoves", rootMoves);
            Fact("arm1_rootLastTarget", lastRootTarget);
            bool arm1 = downs > 0 || clicks > 0 || focusIns > 0;
            FactB("arm1_inputReachedPanel", arm1);

            // ---- arm 1b: the same click with UI Toolkit's event system pumped
            // by hand on every frame. In the batch editor the player loop's own
            // pump is `autoUpdateEventSystem`, and if that is not running in
            // this session then nothing drains the InputForUI queue no matter
            // how many events the Input System delivers.
            if (!arm1)
            {
                downs = clicks = focusIns = rootDowns = rootMoves = 0;
                Fact("arm1b_pump", TbilisiFly.PlayModeTests.HarnessInput.PumpUiEvents());
                InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos });
                for (int i = 0; i < 4; i++) { TbilisiFly.PlayModeTests.HarnessInput.PumpUiEvents(); yield return null; }
                InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos }
                    .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
                for (int i = 0; i < 4; i++) { TbilisiFly.PlayModeTests.HarnessInput.PumpUiEvents(); yield return null; }
                InputSystem.QueueStateEvent(mouse, new MouseState { position = screenPos });
                for (int i = 0; i < 8; i++) { TbilisiFly.PlayModeTests.HarnessInput.PumpUiEvents(); yield return null; }
                Fact("arm1b_pointerDowns", downs);
                Fact("arm1b_clicks", clicks);
                Fact("arm1b_rootPointerDowns", rootDowns);
                Fact("arm1b_rootPointerMoves", rootMoves);
                Fact("arm1b_rootLastTarget", lastRootTarget);
                var f1b = panel.focusController?.focusedElement as VisualElement;
                Fact("arm1b_focused", f1b != null ? $"{f1b.GetType().Name}:{f1b.name}" : "null");
                arm1 = downs > 0 || clicks > 0 || focusIns > 0;
                FactB("arm1b_inputReachedPanel", arm1);
            }

            // typing after the click: text events are separate from key state
            if (arm1)
            {
                var kb = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();
                // CLEAR IT FIRST. The shell pre-fills the remembered user, so
                // the first version of this read `arm1_fieldValueAfterTyping=
                // probepilot` and called it a pass — a value that was already
                // there before a key was touched, and which does not even
                // contain the typed characters. "Not empty" is not evidence of
                // typing; the DELTA is.
                field.value = "";
                field.Focus();
                for (int i = 0; i < 3; i++) yield return null;
                Fact("arm1_fieldValueBeforeTyping", string.IsNullOrEmpty(field.value) ? "<empty>" : field.value);
                // Text goes in as TEXT events, not key state: a TextField reads
                // characters and a KeyboardState event carries none.
                foreach (char c in "ab")
                {
                    InputSystem.QueueTextEvent(kb, c);
                    for (int i = 0; i < 3; i++) yield return null;
                }
                for (int i = 0; i < 6; i++) yield return null;
                Fact("arm1_fieldValueAfterTyping", string.IsNullOrEmpty(field.value) ? "<empty>" : field.value);
                FactB("arm1_typingLands", field.value == "ab");
            }

            // ---- arm 2: the panel's own API (proves focus/typing work when input is delivered)
            field.Focus();
            yield return null;
            var focused2 = panel.focusController?.focusedElement as VisualElement;
            Fact("arm2_focusedAfterFocusCall", focused2 != null ? $"{focused2.GetType().Name}:{focused2.name}" : "null");
            FactB("arm2_focusWorks", focused2 != null && (focused2 == field || field.Contains(focused2)));

            // ---- arm 3: register tab through the same click path
            int tabClicks = 0;
            if (tabRegister != null)
            {
                tabRegister.RegisterCallback<ClickEvent>(_ => tabClicks++);
                var tr = tabRegister.worldBound.center;
                var tp = new Vector2(tr.x, Screen.height - tr.y);
                string screenBefore = boot.Screen.ToString();
                InputSystem.QueueStateEvent(mouse, new MouseState { position = tp });
                for (int i = 0; i < 3; i++) yield return null;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = tp }
                    .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
                for (int i = 0; i < 3; i++) yield return null;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = tp });
                for (int i = 0; i < 8; i++) yield return null;
                Fact("arm3_registerTabClicks", tabClicks);
                Fact("arm3_screenBefore", screenBefore);
                Fact("arm3_screen", boot.Screen.ToString());
                // The register TAB swaps the login form for the register form
                // in place, so what proves it is the click arriving and the
                // second password field appearing, not a Boot screen change.
                var passField = root.Q<TextField>("auth-pass2");
                FactB("arm3_registerFormShowing", passField != null && passField.resolvedStyle.display != DisplayStyle.None);
                FactB("arm3_registerTabClicked", tabClicks > 0);
            }

            // ---- arm 4: the UNFLIPPED position, as insurance against my own
            // coordinate reasoning. The field centre is near the BOTTOM of the
            // screen once y is flipped, so if the event system does not flip
            // after all, this is the arm that lands.
            if (!arm1)
            {
                downs = clicks = rootDowns = rootMoves = 0;
                var raw = centre;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = raw });
                for (int i = 0; i < 3; i++) yield return null;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = raw }
                    .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
                for (int i = 0; i < 3; i++) yield return null;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = raw });
                for (int i = 0; i < 6; i++) yield return null;
                Fact("arm4_unflippedPos", $"({raw.x:F0},{raw.y:F0})");
                Fact("arm4_rootPointerDowns", rootDowns);
                Fact("arm4_rootPointerMoves", rootMoves);
                Fact("arm4_pointerDowns", downs);
            }

            // ---- arm 5: THE CONTROL. A panel of our own, built at runtime in
            // this same play session with the same PanelSettings, clicked the
            // same way. InputEdgeLab's PointerDelivery passes on exactly this,
            // so if the control lands and the shell does not, the difference is
            // the SHELL (or a scene panel), not the session or the harness —
            // and that would be the user's bug, still live.
            {
                var ctlGo = new GameObject("LoginClickControlDoc");
                var ctlDoc = ctlGo.AddComponent<UIDocument>();
                ctlDoc.panelSettings = shell.panelSettings;
                for (int i = 0; i < 4; i++) yield return null;
                var ctlRoot = ctlDoc.rootVisualElement;
                Fact("arm5_rootPresent", ctlRoot != null ? "ok" : "null");
                if (ctlRoot != null)
                {
                    var ctlField = new TextField { name = "control-field" };
                    ctlField.style.position = Position.Absolute;
                    ctlField.style.left = 30; ctlField.style.top = 30;
                    ctlField.style.width = 220; ctlField.style.height = 32;
                    ctlRoot.Add(ctlField);
                    for (int i = 0; i < 4; i++) yield return null;
                    int cDowns = 0, cClicks = 0;
                    ctlField.RegisterCallback<PointerDownEvent>(_ => cDowns++, TrickleDown.TrickleDown);
                    ctlField.RegisterCallback<ClickEvent>(_ => cClicks++);
                    var cr = ctlField.worldBound;
                    Fact("arm5_fieldRect", $"({cr.x:F0},{cr.y:F0},{cr.width:F0},{cr.height:F0})");
                    var cp = new Vector2(cr.center.x, Screen.height - cr.center.y);
                    Fact("arm5_mouseScreenPos", $"({cp.x:F0},{cp.y:F0})");
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = cp });
                    for (int i = 0; i < 3; i++) yield return null;
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = cp }
                        .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
                    for (int i = 0; i < 3; i++) yield return null;
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = cp });
                    for (int i = 0; i < 6; i++) yield return null;
                    Fact("arm5_pointerDowns", cDowns);
                    Fact("arm5_clicks", cClicks);
                    var cf = ctlRoot.panel?.focusController?.focusedElement as VisualElement;
                    Fact("arm5_focused", cf != null ? $"{cf.GetType().Name}:{cf.name}" : "null");
                    FactB("arm5_controlPanelGetsClicks", cDowns > 0 || cClicks > 0);
                    Fact("arm5_shellVsControl", (arm1 ? "shell=yes" : "shell=no") + (cDowns > 0 ? " control=yes" : " control=no"));
                }
                Object.Destroy(ctlGo);
                yield return null;
            }

            FactB("RESULT_clickReachesLogin", arm1);
            Assert.IsTrue(arm1, "a synthetic Input System click never reached the login panel (see LOGINCLICK lines)");
        }
    }
}
#endif
