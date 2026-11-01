// Play-mode-only — see InputLabSpy.cs's header for why the guard is here.
#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Makes a BATCH EDITOR deliver input the way a focused player does, so a
    /// play-mode test can press a key, move a mouse and click a UI Toolkit
    /// button for real instead of calling the handler behind the game's back.
    ///
    /// WHY THIS FILE EXISTS — measured by Assets/Tests/PlayMode/InputEdgeLab.cs
    /// (run it to re-derive any of this; it prints one INPUTLAB line per fact):
    ///
    ///   V1..V7 all read, at the game's own LateUpdate,
    ///       latest=Editor step=<editorStep> lastPressedEd=1 devStep=1
    ///   with held=true and edge=false on every single frame.
    ///
    /// The chain:
    ///   1. A batch editor is NEVER focused (`Application.isFocused` is false),
    ///      and `gameIsPlaying && !gameHasFocus` sends
    ///      InputManager.ShouldDeferEventBetweenEditorAndPlayerUpdates
    ///      (InputManager.cs:3651) down its DEFER branch under the default
    ///      <c>PointersAndKeyboardsRespectGameViewFocus</c>: every Pointer and
    ///      Keyboard event is taken OUT of the player update and left for an
    ///      InputUpdateType.Editor update.
    ///   2. So the editor update is what applies the key, which leaves
    ///      `InputUpdate.s_LatestUpdateType = Editor` and the EDITOR state
    ///      buffers switched in. `isPressed` therefore works.
    ///   3. But `wasPressedThisFrame` then takes ButtonControl.cs:296-299's
    ///      editor branch and compares the editor step count against
    ///      <c>m_UpdateCountLastPressedEditor</c> — stamped from
    ///      <c>device.m_CurrentUpdateStepCount</c>, which
    ///      InputManager.FlipBuffersForDeviceIfNecessary NEVER assigns on the
    ///      editor path (it returns at :4482, before the assignment at :4490).
    ///      The stamp froze at 1 while the counter climbed by 2 a frame, so the
    ///      equality could never hold again and a key EDGE was structurally
    ///      impossible — no pressing technique could have produced one.
    ///
    /// Dropping Editor from <c>InputManager.m_UpdateMask</c> (what
    /// InputTestFixture.Setup() does, InputTestFixture.cs:122-126) is NOT
    /// enough on its own: step 1 still defers the keys, now to an update that
    /// no longer runs, so they are never applied at all (lab V8/V9/V10 read
    /// held=false). What works is to stop the deferral at its source and open
    /// the two gates that then guard an unfocused game
    /// (InputManager.cs:3647-3654): lab V11 reads LATE_EDGES=1 LATE_HELD=6
    /// UP_EDGES=1, i.e. exactly one edge from one press, seen in both Update
    /// and LateUpdate.
    ///
    /// Everything here is editor-session state about FOCUS. None of it changes
    /// what a built player does: a player has no editor update to defer to, and
    /// its window really is focused. So a binding proved through this bridge is
    /// proved on the same code path a real key or click takes.
    /// </summary>
    internal static class HarnessInput
    {
        /// <summary>Route keyboard and pointer input to the game even though
        /// the batch editor has no focus. Call once, before pressing anything.
        /// Returns a description for a PLAYMODE fact line.</summary>
        public static string Install()
        {
            var notes = new List<string>();

            // (1) stop the editor stealing pointer/keyboard events
            var pBehavior = typeof(InputSettings).GetProperty("editorInputBehaviorInPlayMode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pBehavior == null) notes.Add("behavior=<absent>");
            else
            {
                // It MUST be AllDeviceInputAlwaysGoesToGameView. The other two
                // members also contain "GameView" and both mean the opposite;
                // picking by substring took PointersAndKeyboardsRespectGameView-
                // Focus once and changed nothing at all.
                object v = null;
                foreach (var n in Enum.GetNames(pBehavior.PropertyType))
                    if (n.IndexOf("AlwaysGoesToGameView", StringComparison.OrdinalIgnoreCase) >= 0)
                    { v = Enum.Parse(pBehavior.PropertyType, n); break; }
                if (v == null) notes.Add("behavior=<no AlwaysGoesToGameView>");
                else { try { pBehavior.SetValue(InputSystem.settings, v); notes.Add("behavior=" + v); } catch (Exception e) { notes.Add("behavior!" + e.GetType().Name); } }
            }

            // (2) and then do not DISCARD them for being unfocused
            try { Application.runInBackground = true; notes.Add("runInBackground=true"); }
            catch (Exception e) { notes.Add("runInBackground!" + e.GetType().Name); }
            var pBg = typeof(InputSettings).GetProperty("backgroundBehavior",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pBg == null) notes.Add("background=<absent>");
            else
            {
                object v = null;
                foreach (var n in Enum.GetNames(pBg.PropertyType))
                    if (n.IndexOf("IgnoreFocus", StringComparison.OrdinalIgnoreCase) >= 0)
                    { v = Enum.Parse(pBg.PropertyType, n); break; }
                if (v == null) notes.Add("background=<no IgnoreFocus>");
                else { try { pBg.SetValue(InputSystem.settings, v); notes.Add("background=" + v); } catch (Exception e) { notes.Add("background!" + e.GetType().Name); } }
            }

            // The project default is ProcessEventsInDynamicUpdate and the
            // player-loop update is what must apply the events, so this is NOT
            // switched to ProcessEventsManually: a manual pump from a coroutine
            // was a placebo (lab V1: the Manual update never ran — playerStep
            // stayed frozen at 13 — and the editor update was quietly doing the
            // work all along).
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            notes.Add("updateMode=" + InputSettings.UpdateMode.ProcessEventsInDynamicUpdate);

            // (3) AND THE DEVICES THEMSELVES HAVE TO BE ON. An unfocused
            // editor disables pointer and keyboard devices
            // (`disabledWhileInBackground`), and a device already disabled
            // stays disabled after the settings above are changed — queued
            // events are simply dropped, with no error and no log line.
            //
            // This cost a whole diagnostic round: LoginClickRun read
            // arm1_pointerDowns=0 AND arm1_rootPointerMoves=0 (nothing at all
            // reached the panel) while InputEdgeLab's PointerDelivery passed on
            // the same PanelSettings in the same batch editor — because the lab
            // had called InputSystem.EnableDevice by hand and LoginClickRun had
            // not. A runtime-built CONTROL panel in LoginClickRun's own session
            // failed identically, which is what ruled the shell out and pointed
            // here (arm5_shellVsControl=shell=no control=no).
            var kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            var mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(kb);
            InputSystem.EnableDevice(mouse);
            notes.Add("kbEnabled=" + kb.enabled.ToString().ToLowerInvariant());
            notes.Add("mouseEnabled=" + mouse.enabled.ToString().ToLowerInvariant());
            return string.Join(" ", notes);
        }

        /// <summary>Also deliver POINTER events to runtime UI Toolkit panels.
        /// UIElementsRuntimeUtility already pumps its DefaultEventSystem from
        /// the player loop (`autoUpdateEventSystem` is true), but its
        /// `eventSystemUpdateMode` defaults to <c>IgnoreIfAppNotFocused</c> and
        /// the batch editor is never focused, so every click was dropped one
        /// step before the panel. A real player is focused and passes this gate
        /// by itself — so this opens a gate, it does not add a path.</summary>
        public static string InstallUiPointer()
        {
            var t = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            if (t == null) return "<no UIElementsRuntimeUtility>";
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var notes = new List<string>();
            var pMode = t.GetProperty("eventSystemUpdateMode", F);
            if (pMode == null) notes.Add("mode=<absent>");
            else
            {
                notes.Add("modeWas=" + pMode.GetValue(null));
                object v = null;
                foreach (var n in Enum.GetNames(pMode.PropertyType))
                    if (n.IndexOf("Ignore", StringComparison.OrdinalIgnoreCase) < 0)
                    { v = Enum.Parse(pMode.PropertyType, n); break; }
                if (v == null) notes.Add("mode=<no always member in " + string.Join("|", Enum.GetNames(pMode.PropertyType)) + ">");
                else { try { pMode.SetValue(null, v); notes.Add("mode=" + v); } catch (Exception e) { notes.Add("mode!" + e.GetType().Name); } }
            }
            var pAuto = t.GetProperty("autoUpdateEventSystem", F);
            if (pAuto != null)
            {
                notes.Add("autoWas=" + pAuto.GetValue(null).ToString().ToLowerInvariant());
                try { pAuto.SetValue(null, true); } catch { }
            }
            var pActive = t.GetProperty("activeEventSystem", F);
            if (pActive != null) notes.Add("active=" + (pActive.GetValue(null)?.GetType().Name ?? "null"));
            var pDefault = t.GetProperty("defaultEventSystem", F);
            if (pDefault != null) notes.Add("default=" + (pDefault.GetValue(null)?.GetType().Name ?? "null"));
            return string.Join(" ", notes);
        }

        /// <summary>Read back what InstallUiPointer left, for a run that needs
        /// to prove the gate is still open at the moment of the click (a scene
        /// load happens in between).</summary>
        public static string UiPointerState()
        {
            var t = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            if (t == null) return "<no UIElementsRuntimeUtility>";
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var notes = new List<string>();
            foreach (var name in new[] { "eventSystemUpdateMode", "autoUpdateEventSystem", "useDefaultEventSystem", "overrideUseDefaultEventSystem" })
            {
                var p = t.GetProperty(name, F);
                if (p == null) { notes.Add(name + "=<absent>"); continue; }
                try { notes.Add(name + "=" + (p.GetValue(null)?.ToString() ?? "null")); }
                catch (Exception e) { notes.Add(name + "!" + e.GetType().Name); }
            }
            var pA = t.GetProperty("activeEventSystem", F);
            if (pA != null) { try { notes.Add("activeEventSystem=" + (pA.GetValue(null)?.GetType().Name ?? "null")); } catch { } }
            return string.Join(" ", notes);
        }

        static MethodInfo _pump;
        static bool _pumpResolved;

        /// <summary>Run UI Toolkit's event-system update by hand, for a frame
        /// where the player loop's own call may not have happened yet.</summary>
        public static string PumpUiEvents()
        {
            if (!_pumpResolved)
            {
                _pumpResolved = true;
                var t = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
                _pump = t?.GetMethod("UpdateEventSystem",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            }
            if (_pump == null) return "<no UpdateEventSystem>";
            try { _pump.Invoke(null, Array.Empty<object>()); return "ok"; }
            catch (Exception e) { return (e.InnerException ?? e).GetType().Name; }
        }
    }
}
#endif
