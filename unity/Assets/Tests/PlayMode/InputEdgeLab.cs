// Play-mode-only — see InputLabSpy.cs's header for why the guard is here.
#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Reflection onto the Input System's own frame-press bookkeeping, so a
    /// run can say WHY `wasPressedThisFrame` did or did not fire instead of
    /// only that it did not. Every field here is `internal`/`private` in
    /// Unity.InputSystem; the names are from
    /// Library/PackageCache/com.unity.inputsystem@*/InputSystem/Runtime
    /// (ButtonControl.cs:23-34, Devices/InputDevice.cs:738,
    /// InputUpdateType.cs:76-82) at package version 1.20.1.
    ///
    /// The mechanism being inspected (ButtonControl.cs:282-301): the FIRST
    /// read of `wasPressedThisFrame` on a control compares the front and back
    /// state buffers; it also registers the control, and EVERY LATER read is
    /// just `InputUpdate.s_UpdateStepCount == m_UpdateCountLastPressed`, a
    /// number stamped from `device.m_CurrentUpdateStepCount` at the moment the
    /// state event was applied. So an edge is lost whenever those two counters
    /// belong to different update worlds — which is exactly what the editor's
    /// own `InputUpdateType.Editor` updates do.
    /// </summary>
    internal static class InputLabDiag
    {
        static readonly Type TUpdate =
            typeof(InputSystem).Assembly.GetType("UnityEngine.InputSystem.LowLevel.InputUpdate");
        static readonly FieldInfo FStep = TUpdate?.GetField("s_UpdateStepCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo FLatest = TUpdate?.GetField("s_LatestUpdateType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo FLatestNonEd = TUpdate?.GetField("s_LatestNonEditorUpdateType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo FPlayerCnt = TUpdate?.GetField("s_PlayerUpdateStepCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo FEditorCnt = TUpdate?.GetField("s_EditorUpdateStepCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        static readonly FieldInfo FDevStep = typeof(InputDevice).GetField("m_CurrentUpdateStepCount", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo FLastPressed = typeof(ButtonControl).GetField("m_UpdateCountLastPressed", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo FLastPressedEd = typeof(ButtonControl).GetField("m_UpdateCountLastPressedEditor", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo FWasPress = typeof(ButtonControl).GetField("m_LastUpdateWasPress", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo PNeedsCheck = typeof(ButtonControl).GetProperty("needsToCheckFramePress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static uint StepOf(FieldInfo f)
        {
            if (f == null) return uint.MaxValue;
            object box = f.GetValue(null);
            if (box == null) return uint.MaxValue;
            var p = box.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p == null ? uint.MaxValue : (uint)p.GetValue(box);
        }

        public static uint StepCount => FStep == null ? uint.MaxValue : (uint)FStep.GetValue(null);
        public static string LatestType => FLatest == null ? "?" : FLatest.GetValue(null).ToString();
        public static string LatestNonEditorType => FLatestNonEd == null ? "?" : FLatestNonEd.GetValue(null).ToString();
        public static uint PlayerStep => StepOf(FPlayerCnt);
        public static uint EditorStep => StepOf(FEditorCnt);
        public static uint DeviceStep(InputDevice d) => FDevStep == null ? uint.MaxValue : (uint)FDevStep.GetValue(d);

        public static string Snapshot(ButtonControl k, bool edge, bool held)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "edge={0} held={1} step={2} latest={3} nonEd={4} playerStep={5} editorStep={6} devStep={7} lastPressed={8} lastPressedEd={9} wasPress={10} needsCheck={11}",
                edge.ToString().ToLowerInvariant(), held.ToString().ToLowerInvariant(),
                StepCount, LatestType, LatestNonEditorType, PlayerStep, EditorStep,
                DeviceStep(k.device),
                FLastPressed == null ? "?" : FLastPressed.GetValue(k).ToString(),
                FLastPressedEd == null ? "?" : FLastPressedEd.GetValue(k).ToString(),
                FWasPress == null ? "?" : FWasPress.GetValue(k).ToString().ToLowerInvariant(),
                PNeedsCheck == null ? "?" : PNeedsCheck.GetValue(k).ToString().ToLowerInvariant());
        }
    }

    /// <summary>
    /// A SCENE-LESS play-mode lab for one question: which input mechanism makes
    /// a real key EDGE (`wasPressedThisFrame`) survive from a test into the
    /// game's LateUpdate, where Core/GameLoop.ReadDiscreteKeys reads it?
    ///
    /// FirstPlayModeRun could only report the symptom
    /// (`SPY_ENTER_HELD_FRAMES=8` with `SPY_ENTER_EDGES=0`), and every attempt
    /// to settle it there cost a 20-minute city boot. This test loads nothing,
    /// so each run is a domain reload. Run it alone:
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode \
    ///         -testFilter TbilisiFly.PlayModeTests.InputEdgeLab \
    ///         -testResults &lt;xml&gt; -logFile &lt;log&gt;
    ///
    /// Every line is `INPUTLAB key=value`, booleans lower-case.
    /// </summary>
    public class InputEdgeLab
    {
        static void Fact(string k, object v) => Debug.Log($"INPUTLAB {k}={v}");

        Keyboard _kb;
        Mouse _mouse;
        InputLabSpy _spy;

        enum Feed { QueueEvent, StateChange }

        void Send(Feed feed, params Key[] keys)
        {
            if (feed == Feed.QueueEvent) InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
            else InputState.Change(_kb, new KeyboardState(keys));
        }

        // [Explicit]: this is a LAB, not a regression guard. Play-mode tests
        // all share ONE play session, so run inside a full suite it would
        // press keys at whatever scene the previous test left loaded and add a
        // second UIDocument beside the game's own panel (which makes
        // FindAnyObjectByType<UIDocument> a coin toss — the project's own
        // documented trap). Name it to run it:
        //   -testFilter TbilisiFly.PlayModeTests.InputEdgeLab
        [UnityTest]
        [Timeout(600000)]
        [Explicit("input mechanism lab — name it with -testFilter; it mutates session-wide input settings")]
        public IEnumerator EdgeMechanism()
        {
            LogAssert.ignoreFailingMessages = true;

            _kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_kb);
            InputSystem.EnableDevice(_mouse);

            var go = new GameObject("InputLabSpy");
            _spy = go.AddComponent<InputLabSpy>();
            _spy.Watch = Key.Enter;

            var originalMode = InputSystem.settings.updateMode;
            Fact("ORIGINAL_UPDATE_MODE", originalMode);
            Fact("BATCHMODE", Application.isBatchMode.ToString().ToLowerInvariant());
            Fact("UPDATE_MASK", UpdateMask());
            yield return null;
            Fact("KEYBOARD_PRESENT", (Keyboard.current != null).ToString().ToLowerInvariant());

            // ---------------------------------------------------------- V1..V6
            // id                 mode                     feed            pump-in
            yield return Variant("V1_manual_queue_coro", InputSettings.UpdateMode.ProcessEventsManually, Feed.QueueEvent, "coroutine", true);
            yield return Variant("V2_manual_queue_update", InputSettings.UpdateMode.ProcessEventsManually, Feed.QueueEvent, "update", false);
            yield return Variant("V3_dynamic_queue_none", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "none", true);
            yield return Variant("V4_dynamic_change_none", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.StateChange, "none", false);
            yield return Variant("V5_manual_change_coro", InputSettings.UpdateMode.ProcessEventsManually, Feed.StateChange, "coroutine", false);
            yield return Variant("V6_dynamic_queue_coro", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "coroutine", false);
            yield return Variant("V7_dynamic_queue_update", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "update", false);

            // ----------------------------------------------------- mouse delta
            // 20 queued deltas of 1.0 each. The game reads Mouse.delta once a
            // frame; the delta control is RESET by every input update, so the
            // question is how many of the 20 survive to be read.
            yield return MouseVariant("M1_perframe_dynamic", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, true);
            yield return MouseVariant("M2_allinoneframe_dynamic", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, false);

            // ================================================================
            // THE FIX, from the measurement above. Every V1..V7 trace reads
            //   latest=Editor  step=<editorStep>  lastPressedEd=1  devStep=1
            // at the game's LateUpdate. So in a BATCH EDITOR play session the
            // editor's own InputUpdateType.Editor updates are what consume the
            // queued events (twice a frame — editorStep climbs by 2), they run
            // LAST before LateUpdate, and they leave the editor state buffers
            // switched in. `isPressed` therefore works, while
            // `wasPressedThisFrame` takes ButtonControl.cs:296-299's editor
            // branch and compares the EDITOR step count against
            // m_UpdateCountLastPressedEditor — a number stamped from
            // device.m_CurrentUpdateStepCount, which
            // InputManager.FlipBuffersForDeviceIfNecessary NEVER assigns on
            // the editor path (it returns at :4482, before the assignment at
            // :4490). The stamp is frozen at 1 while the counter climbs: the
            // equality can never hold again, so a key edge is STRUCTURALLY
            // impossible, no matter how the test presses the key.
            //
            // Unity's own InputTestFixture.Setup() does exactly one thing
            // about this (InputTestFixture.cs:122-126):
            //
            //   if (Application.isPlaying && IsUnityTest())
            //       InputSystem.manager.m_UpdateMask &= ~InputUpdateType.Editor;
            //
            // — "for a [UnityTest] play mode test, we don't want editor
            // updates interfering with the test". With Editor out of the mask,
            // InputManager.ShouldRunUpdate says no, native never delivers an
            // editor update, the events survive to the player-loop update, and
            // every counter is back in the player world.
            // ================================================================
            var maskBefore = UpdateMaskValue();
            Fact("MASKFIX_BEFORE", maskBefore);
            Fact("MASKFIX_APPLIED", DropEditorFromUpdateMask());
            Fact("MASKFIX_AFTER", UpdateMaskValue());
            yield return Variant("V8_maskfix_dynamic_queue", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "none", true);
            Fact("EDITORBEHAVIOUR_APPLIED", ForceGameViewInputBehaviour());
            yield return Variant("V9_maskfix_gameview_dynamic", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "none", true);
            yield return Variant("V10_maskfix_dynamic_pumped", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "coroutine", false);
            // ---- THE FULL RECIPE. V8/V9/V10 all read held=false with the mask
            // fixed, which named the second half of the cause:
            // InputManager.ShouldDeferEventBetweenEditorAndPlayerUpdates
            // (:3651) DEFERS every Pointer and Keyboard event out of the player
            // update and into the editor update whenever `gameIsPlaying &&
            // !gameHasFocus` under the DEFAULT
            // PointersAndKeyboardsRespectGameViewFocus. A batch editor never
            // has focus, so with Editor out of the mask the keys were deferred
            // to an update that no longer runs and were never applied at all.
            Fact("EDITORBEHAVIOUR_REAL", ForceGameViewInputBehaviour());
            Fact("UNFOCUSED_GATES", AllowInputWhileUnfocused());
            yield return Variant("V11_full_recipe", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "none", true);
            yield return Variant("V12_full_recipe_pumped", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, Feed.QueueEvent, "coroutine", false);
            yield return MouseVariant("M3_fullrecipe_perframe", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, true);
            yield return MouseVariant("M4_fullrecipe_allinone", InputSettings.UpdateMode.ProcessEventsInDynamicUpdate, false);
            RestoreUpdateMask(maskBefore);
            Fact("MASKFIX_RESTORED", UpdateMaskValue());

            InputSystem.settings.updateMode = originalMode;
            yield return null;
            Fact("RESTORED_UPDATE_MODE", InputSystem.settings.updateMode);
            UnityEngine.Object.Destroy(go);
            Debug.Log("INPUTLAB RESULT=DONE");
        }

        // ---- the three knobs InputTestFixture.Setup() turns, reachable from
        // here by reflection because InputManager/m_UpdateMask are internal.
        internal static object Manager =>
            typeof(InputSystem).GetProperty("manager", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);

        static FieldInfo MaskField(object mgr) =>
            mgr?.GetType().GetField("m_UpdateMask", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        internal static string UpdateMaskValue()
        {
            var mgr = Manager; var f = MaskField(mgr);
            return f == null ? "?" : f.GetValue(mgr).ToString();
        }

        /// <summary>InputTestFixture.cs:122-126, applied without the rest of
        /// the fixture (which does a SaveAndReset that would wipe every device
        /// and the settings out from under a running game).</summary>
        internal static string DropEditorFromUpdateMask()
        {
            var mgr = Manager; var f = MaskField(mgr);
            if (f == null) return "<no m_UpdateMask>";
            try
            {
                var cur = (InputUpdateType)f.GetValue(mgr);
                f.SetValue(mgr, cur & ~InputUpdateType.Editor);
                return "ok";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        internal static void RestoreUpdateMask(string before)
        {
            var mgr = Manager; var f = MaskField(mgr);
            if (f == null) return;
            try { f.SetValue(mgr, Enum.Parse(typeof(InputUpdateType), before)); } catch { }
        }

        /// <summary>InputTestFixture.cs:117-119 — "override the editor messing
        /// with logic like canRunInBackground and focus and make it behave
        /// like in the player". Editor-only property, so it is resolved by
        /// name rather than compiled against.</summary>
        internal static string ForceGameViewInputBehaviour()
        {
            var p = typeof(InputSettings).GetProperty("editorInputBehaviorInPlayMode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return "<no editorInputBehaviorInPlayMode>";
            try
            {
                object v = null;
                // The member MUST be AllDeviceInputAlwaysGoesToGameView, not
                // merely one containing "GameView": the other two both contain
                // it and both mean the OPPOSITE (route to the editor when the
                // game view is unfocused). Picking by substring took
                // PointersAndKeyboardsRespectGameViewFocus and changed nothing.
                foreach (var n in Enum.GetNames(p.PropertyType))
                    if (n.IndexOf("AlwaysGoesToGameView", StringComparison.OrdinalIgnoreCase) >= 0) { v = Enum.Parse(p.PropertyType, n); break; }
                if (v == null) return "<no AlwaysGoesToGameView in " + p.PropertyType.Name + ">";
                p.SetValue(InputSystem.settings, v);
                return v.ToString();
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>The second gate on the same path. With
        /// AllDeviceInputAlwaysGoesToGameView and no focus,
        /// InputManager.cs:3647-3654 DISCARDS every event unless the runtime
        /// may run in the background and the background behaviour is not
        /// reset-and-disable. A batch editor never has focus, so both of these
        /// have to be opened too.</summary>
        internal static string AllowInputWhileUnfocused()
        {
            var notes = new List<string>();
            try { Application.runInBackground = true; notes.Add("runInBackground=true"); }
            catch (Exception e) { notes.Add("runInBackground!" + e.GetType().Name); }
            var p = typeof(InputSettings).GetProperty("backgroundBehavior",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) notes.Add("<no backgroundBehavior>");
            else
            {
                try
                {
                    object v = null;
                    foreach (var n in Enum.GetNames(p.PropertyType))
                        if (n.IndexOf("IgnoreFocus", StringComparison.OrdinalIgnoreCase) >= 0) { v = Enum.Parse(p.PropertyType, n); break; }
                    if (v == null) notes.Add("backgroundEnum=" + string.Join("|", Enum.GetNames(p.PropertyType)));
                    else { p.SetValue(InputSystem.settings, v); notes.Add("backgroundBehavior=" + v); }
                }
                catch (Exception e) { notes.Add("backgroundBehavior!" + e.GetType().Name); }
            }
            return string.Join(" ", notes);
        }

        static string UpdateMask()
        {
            var pMgr = typeof(InputSystem).GetProperty("manager", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var mgr = pMgr?.GetValue(null);
            if (mgr == null) return "?";
            var pMask = mgr.GetType().GetProperty("updateMask", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return pMask?.GetValue(mgr)?.ToString() ?? "?";
        }

        /// <summary>One press/release cycle under one mechanism. Enter is held
        /// for 6 frames (re-sent every frame, as the real harness does) and
        /// released for 3. An honest mechanism gives LATE_EDGES=1 and
        /// LATE_HELD=6.</summary>
        IEnumerator Variant(string id, InputSettings.UpdateMode mode, Feed feed, string pumpIn, bool trace)
        {
            InputSystem.settings.updateMode = mode;
            // Settle: let the mode take effect and the key read as released
            // before the measured press, or the edge has already happened.
            for (int i = 0; i < 4; i++)
            {
                Send(feed);
                if (pumpIn == "coroutine" && mode == InputSettings.UpdateMode.ProcessEventsManually) InputSystem.Update();
                yield return null;
            }
            _spy.PumpInUpdate = pumpIn == "update" ? (Action)(() => InputSystem.Update()) : null;
            // One more settling frame so the pump hook is live before the press.
            yield return null;

            _spy.Trace = trace;
            _spy.Zero();

            for (int i = 0; i < 6; i++)
            {
                Send(feed, Key.Enter);
                if (pumpIn == "coroutine") InputSystem.Update();
                yield return null;
            }
            for (int i = 0; i < 3; i++)
            {
                Send(feed);
                if (pumpIn == "coroutine") InputSystem.Update();
                yield return null;
            }

            Fact($"{id}_MODE", mode);
            Fact($"{id}_FEED", feed);
            Fact($"{id}_PUMP", pumpIn);
            Fact($"{id}_MASK", UpdateMask());
            Fact($"{id}_LATE_FRAMES", _spy.LateFrames);
            Fact($"{id}_LATE_EDGES", _spy.LateEdges);
            Fact($"{id}_LATE_HELD", _spy.LateHeld);
            Fact($"{id}_UP_EDGES", _spy.UpEdges);
            Fact($"{id}_UP_HELD", _spy.UpHeld);
            Fact($"{id}_VERDICT", _spy.LateEdges == 1 && _spy.LateHeld >= 5 ? "pass" : "FAIL");
            if (trace)
                for (int i = 0; i < _spy.Lines.Count; i++) Fact($"{id}_TRACE_{i}", _spy.Lines[i]);
            _spy.PumpInUpdate = null;
            _spy.Trace = false;
        }

        // =================================================================
        // POINTER DELIVERY TO A RUNTIME UI TOOLKIT PANEL
        //
        // The user's first run of the built player: "clicks in login register
        // pages not works, i cant focus on input". One cause was found and
        // fixed (the full-screen `chrome` layer swallowed picks — ShellView
        // now sets pickingMode Ignore, matching style.css:1337's
        // pointer-events:none), but LoginClickRun's synthetic click still
        // produced 0 PointerDown/Click/FocusIn, so the FIX IS UNPROVEN: there
        // is no evidence any pointer event has ever reached a panel in this
        // port.
        //
        // This builds its own one-field panel — no Game.unity, no city, so a
        // run is a domain reload instead of twenty minutes — and tries each
        // delivery path in turn, printing the reflected member surface it
        // found so a failure names the API instead of guessing at it. There is
        // no uGUI EventSystem in this project, so the path a real mouse uses
        // in a player is UI Toolkit's own: UIElementsRuntimeUtility pumps a
        // DefaultEventSystem, which pulls from UnityEngine.InputForUI's
        // EventProvider, which the Input System package feeds
        // (InputSystemProvider.cs). Two gates in that chain are suspected:
        // the app is NOT FOCUSED in a batch editor, and the pump is a
        // player-loop callback that may not run in this session.
        // =================================================================
        static Type TRuntimeUtil =>
            typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");

        object _evSys;
        MethodInfo _evSysUpdate;
        object _evSysUpdateArg;

        void DumpMembers(string tag, Type t, Func<string, bool> want, object instance)
        {
            if (t == null) { Fact(tag, "<type not found>"); return; }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            int n = 0;
            foreach (var p in t.GetProperties(F))
                if (want(p.Name) && n < 40) Fact($"{tag}_PROP_{n++}", $"{p.Name}:{p.PropertyType.Name} canWrite={p.CanWrite.ToString().ToLowerInvariant()}");
            foreach (var f in t.GetFields(F))
                if (want(f.Name) && n < 40) Fact($"{tag}_FIELD_{n++}", $"{f.Name}:{f.FieldType.Name}");
            foreach (var m in t.GetMethods(F))
                if (want(m.Name) && n < 40) Fact($"{tag}_METHOD_{n++}", $"{m.Name}({string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name))})");
            foreach (var nt in t.GetNestedTypes(F))
                if (nt.IsEnum && n < 40) Fact($"{tag}_ENUM_{n++}", $"{nt.Name}: {string.Join("|", Enum.GetNames(nt))}");
            if (n == 0) Fact(tag, "<no matching members>");
        }

        /// <summary>Set every bool named like a not-focused gate to false, on
        /// the type and on the instance. Returns what it touched.</summary>
        string OpenFocusGates(Type t, object instance)
        {
            if (t == null) return "<no type>";
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var touched = new List<string>();
            foreach (var p in t.GetProperties(F))
            {
                if (p.PropertyType != typeof(bool) || !p.CanWrite) continue;
                if (p.Name.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool target = p.Name.IndexOf("Ignore", StringComparison.OrdinalIgnoreCase) >= 0 ? false : true;
                try { p.SetValue(p.GetGetMethod(true).IsStatic ? null : instance, target); touched.Add(p.Name + "=" + target.ToString().ToLowerInvariant()); }
                catch (Exception e) { touched.Add(p.Name + "!" + e.GetType().Name); }
            }
            foreach (var f in t.GetFields(F))
            {
                if (f.FieldType != typeof(bool)) continue;
                if (f.Name.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool target = f.Name.IndexOf("Ignore", StringComparison.OrdinalIgnoreCase) >= 0 ? false : true;
                try { f.SetValue(f.IsStatic ? null : instance, target); touched.Add(f.Name + "=" + target.ToString().ToLowerInvariant()); }
                catch (Exception e) { touched.Add(f.Name + "!" + e.GetType().Name); }
            }
            return touched.Count == 0 ? "<none>" : string.Join(" ", touched);
        }

        /// <summary>Find the live DefaultEventSystem and the argument that makes
        /// its Update() process events even though the batch editor has no
        /// focus (the enum member whose name is NOT the Ignore-if-unfocused
        /// one).</summary>
        /// <summary>Unity 6's UIElementsRuntimeUtility surface, as the lab's own
        /// first run printed it: a static <c>UpdateEventSystem()</c> pump, a
        /// settable <c>eventSystemUpdateMode</c> (whose enum carries
        /// <c>IgnoreIfAppNotFocused</c>) and <c>autoUpdateEventSystem</c>.
        /// `Application.isFocused` is FALSE in a batch editor, so the default
        /// mode is the gate that silently drops every pointer event here —
        /// and it is open in a real, focused player, which is why this is a
        /// harness accommodation and not a game fix.</summary>
        string ResolveEventSystemPump()
        {
            var t = TRuntimeUtil;
            if (t == null) return "<no UIElementsRuntimeUtility>";
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var notes = new List<string>();

            var pMode = t.GetProperty("eventSystemUpdateMode", F);
            if (pMode != null)
            {
                object always = null;
                foreach (var n in Enum.GetNames(pMode.PropertyType))
                    if (n.IndexOf("Ignore", StringComparison.OrdinalIgnoreCase) < 0) { always = Enum.Parse(pMode.PropertyType, n); break; }
                notes.Add("modeWas=" + pMode.GetValue(null));
                notes.Add("modeEnum=" + string.Join("|", Enum.GetNames(pMode.PropertyType)));
                if (always != null)
                {
                    try { pMode.SetValue(null, always); notes.Add("modeSet=" + always); }
                    catch (Exception e) { notes.Add("modeSet!" + e.GetType().Name); }
                }
            }
            else notes.Add("<no eventSystemUpdateMode>");

            var pAuto = t.GetProperty("autoUpdateEventSystem", F);
            if (pAuto != null)
            {
                notes.Add("autoWas=" + pAuto.GetValue(null).ToString().ToLowerInvariant());
                try { pAuto.SetValue(null, true); } catch { }
            }
            var pActive = t.GetProperty("activeEventSystem", F);
            if (pActive != null) notes.Add("active=" + (pActive.GetValue(null)?.GetType().Name ?? "null"));
            var pDefault = t.GetProperty("defaultEventSystem", F);
            if (pDefault != null) { try { _evSys = pDefault.GetValue(null); } catch { } }
            notes.Add("defaultEventSystem=" + (_evSys?.GetType().Name ?? "null"));

            // the pump: the static UpdateEventSystem(), then the instance
            // Update(...) on DefaultEventSystem as a fallback
            _evSysUpdate = t.GetMethod("UpdateEventSystem", F, null, Type.EmptyTypes, null);
            if (_evSysUpdate != null) { _evSysTarget = null; _evSysUpdateArg = null; notes.Add("pump=UIElementsRuntimeUtility.UpdateEventSystem()"); }
            else if (_evSys != null)
            {
                foreach (var m in _evSys.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "Update") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0) { _evSysUpdate = m; _evSysTarget = _evSys; _evSysUpdateArg = null; break; }
                    if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                    {
                        object arg = null;
                        foreach (var n in Enum.GetNames(ps[0].ParameterType))
                            if (n.IndexOf("Ignore", StringComparison.OrdinalIgnoreCase) < 0) { arg = Enum.Parse(ps[0].ParameterType, n); break; }
                        _evSysUpdate = m; _evSysTarget = _evSys;
                        _evSysUpdateArg = arg ?? Enum.GetValues(ps[0].ParameterType).GetValue(0);
                        break;
                    }
                }
                notes.Add("pump=" + (_evSysUpdate == null ? "<none>" : _evSys.GetType().Name + ".Update"));
            }

            // and the InputForUI side of the chain
            var tp = Type.GetType("UnityEngine.InputForUI.EventProvider, UnityEngine.InputForUIModule");
            _epNotify = tp?.GetMethod("NotifyUpdate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            notes.Add("EventProvider.NotifyUpdate=" + (_epNotify != null ? "found" : "missing"));
            return string.Join(" ", notes);
        }

        object _evSysTarget;
        MethodInfo _epNotify;

        string PumpEventSystem()
        {
            string r = "";
            if (_epNotify != null)
            {
                try { _epNotify.Invoke(null, Array.Empty<object>()); r += "notify=ok "; }
                catch (Exception e) { r += "notify!" + (e.InnerException ?? e).GetType().Name + " "; }
            }
            if (_evSysUpdate == null) return r + "<no pump>";
            try
            {
                _evSysUpdate.Invoke(_evSysTarget, _evSysUpdateArg == null ? Array.Empty<object>() : new[] { _evSysUpdateArg });
                return r + "pump=ok";
            }
            catch (Exception e) { return r + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
        }

        [UnityTest]
        [Timeout(900000)]
        [Explicit("pointer-delivery lab — name it with -testFilter; it adds a second UIDocument")]
        public IEnumerator PointerDelivery()
        {
            LogAssert.ignoreFailingMessages = true;
            var mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            var kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(mouse);
            InputSystem.EnableDevice(kb);
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;

            Fact("PD_APP_FOCUSED", Application.isFocused.ToString().ToLowerInvariant());
            Fact("PD_BATCHMODE", Application.isBatchMode.ToString().ToLowerInvariant());
            Fact("PD_SCREEN", $"{Screen.width}x{Screen.height}");
            Fact("PD_INPUTSYSTEM_ACTIONS", InputSystem.actions != null ? InputSystem.actions.name : "null");

            // ---- a panel of our own, so this costs a domain reload, not a city
            var ps = Resources.Load<PanelSettings>("UI/TbilisiFlyPanelSettings");
            Fact("PD_PANELSETTINGS", ps != null ? ps.name : "null");
            var go = new GameObject("PointerLabDoc");
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            for (int i = 0; i < 4; i++) yield return null;
            var root = doc.rootVisualElement;
            Fact("PD_ROOT", root != null ? "ok" : "null");
            if (root == null) { Debug.Log("INPUTLAB PD_RESULT=NO_ROOT"); UnityEngine.Object.Destroy(go); yield break; }

            var field = new TextField { name = "lab-field" };
            field.style.position = Position.Absolute;
            field.style.left = 30; field.style.top = 30; field.style.width = 220; field.style.height = 32;
            root.Add(field);
            for (int i = 0; i < 4; i++) yield return null;

            var panel = root.panel;
            Fact("PD_PANEL", panel != null ? "attached" : "null");
            var rect = field.worldBound;
            Fact("PD_FIELD_RECT", $"({rect.x:F0},{rect.y:F0},{rect.width:F0},{rect.height:F0})");
            var picked = panel != null ? panel.Pick(rect.center) : null;
            Fact("PD_PICK", picked != null ? $"{picked.GetType().Name}:{picked.name}" : "null");

            int downs = 0, clicks = 0, focusIns = 0;
            field.RegisterCallback<PointerDownEvent>(_ => downs++, TrickleDown.TrickleDown);
            field.RegisterCallback<ClickEvent>(_ => clicks++);
            field.RegisterCallback<FocusInEvent>(_ => focusIns++, TrickleDown.TrickleDown);

            // ---- the member surface, so a dead end names the API
            DumpMembers("PD_RU", TRuntimeUtil, n => n.IndexOf("EventSystem", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || n.IndexOf("UpdateRuntimePanels", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || n.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) >= 0, null);
            Fact("PD_PUMP_RESOLVED", ResolveEventSystemPump());
            if (_evSys != null)
            {
                DumpMembers("PD_ES", _evSys.GetType(), n => n == "Update" || n.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || n.IndexOf("InputForUI", StringComparison.OrdinalIgnoreCase) >= 0, _evSys);
                Fact("PD_ES_FOCUS_GATES", OpenFocusGates(_evSys.GetType(), _evSys));
            }
            Fact("PD_RU_FOCUS_GATES", OpenFocusGates(TRuntimeUtil, null));
            var tProvider = Type.GetType("UnityEngine.InputForUI.EventProvider, UnityEngine.InputForUIModule");
            DumpMembers("PD_EP", tProvider, n => n.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0
                                              || n.IndexOf("Provider", StringComparison.OrdinalIgnoreCase) >= 0
                                              || n.IndexOf("Subscribe", StringComparison.OrdinalIgnoreCase) >= 0, null);

            // ---- arm 1: the Input System alone, exactly as LoginClickRun does
            yield return Click(mouse, rect.center, false);
            Fact("PD_ARM1_DOWNS", downs); Fact("PD_ARM1_CLICKS", clicks); Fact("PD_ARM1_FOCUSINS", focusIns);
            Fact("PD_ARM1_FOCUSED", FocusedName(panel));
            Fact("PD_ARM1_VERDICT", downs > 0 || clicks > 0 ? "pass" : "FAIL");

            // ---- arm 2: the same, with (a) the editor input updates out of the
            // way so the Input System's own device state lands in the player
            // world, and (b) UI Toolkit's event system pumped by hand, because
            // its default mode ignores everything while the app is unfocused
            // and a batch editor is never focused.
            var maskBefore = UpdateMaskValue();
            Fact("PD_MASK_BEFORE", maskBefore);
            Fact("PD_MASK_FIX", DropEditorFromUpdateMask());
            Fact("PD_EDITOR_BEHAVIOUR", ForceGameViewInputBehaviour());
            Fact("PD_UNFOCUSED_GATES", AllowInputWhileUnfocused());
            Fact("PD_MASK_AFTER", UpdateMaskValue());
            // let the new routing settle before the click
            for (int i = 0; i < 4; i++) yield return null;
            downs = clicks = focusIns = 0;
            yield return Click(mouse, rect.center, true);
            Fact("PD_ARM2_PUMP", PumpEventSystem());
            Fact("PD_ARM2_DOWNS", downs); Fact("PD_ARM2_CLICKS", clicks); Fact("PD_ARM2_FOCUSINS", focusIns);
            Fact("PD_ARM2_FOCUSED", FocusedName(panel));
            Fact("PD_ARM2_VERDICT", downs > 0 || clicks > 0 ? "pass" : "FAIL");
            if (downs > 0 || clicks > 0)
            {
                InputSystem.QueueTextEvent(kb, 'a');
                InputSystem.QueueTextEvent(kb, 'b');
                InputSystem.Update();
                for (int i = 0; i < 4; i++) { PumpEventSystem(); yield return null; }
                Fact("PD_ARM2_TYPED", field.value ?? "<null>");
            }

            // ---- arm 3: the panel's own API, the floor of the stack. Proves
            // focus and the callback wiring work when something DOES reach the
            // panel, so arm 1/arm 2 failing is a delivery problem and not a
            // dead TextField.
            downs = clicks = focusIns = 0;
            field.Focus();
            yield return null;
            Fact("PD_ARM3_FOCUSINS", focusIns);
            Fact("PD_ARM3_FOCUSED", FocusedName(panel));

            RestoreUpdateMask(maskBefore);
            UnityEngine.Object.Destroy(go);
            yield return null;
            Debug.Log("INPUTLAB PD_RESULT=DONE");
        }

        static string FocusedName(IPanel panel)
        {
            var f = panel?.focusController?.focusedElement as VisualElement;
            return f == null ? "null" : $"{f.GetType().Name}:{f.name}";
        }

        /// <summary>A move, a press and a release at `where` (panel space,
        /// top-left origin), with a frame between each — the Input System
        /// mouse position is bottom-left origin.</summary>
        IEnumerator Click(Mouse mouse, Vector2 where, bool pump)
        {
            var p = new Vector2(where.x, Screen.height - where.y);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = p });
            for (int i = 0; i < 3; i++) { if (pump) PumpEventSystem(); yield return null; }
            InputSystem.QueueStateEvent(mouse, new MouseState { position = p }
                .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
            for (int i = 0; i < 3; i++) { if (pump) PumpEventSystem(); yield return null; }
            InputSystem.QueueStateEvent(mouse, new MouseState { position = p });
            for (int i = 0; i < 5; i++) { if (pump) PumpEventSystem(); yield return null; }
        }

        IEnumerator MouseVariant(string id, InputSettings.UpdateMode mode, bool perFrame)
        {
            InputSystem.settings.updateMode = mode;
            yield return null;
            _spy.Zero();
            if (perFrame)
            {
                for (int i = 0; i < 20; i++)
                {
                    InputSystem.QueueStateEvent(_mouse, new MouseState { delta = new Vector2(1f, 0f) });
                    yield return null;
                }
            }
            else
            {
                for (int i = 0; i < 20; i++)
                    InputSystem.QueueStateEvent(_mouse, new MouseState { delta = new Vector2(1f, 0f) });
                yield return null;
                yield return null;
            }
            Fact($"{id}_DELTA_SUM", _spy.LateDeltaSum.ToString("0.###", CultureInfo.InvariantCulture));
            Fact($"{id}_EXPECTED", 20);
            Fact($"{id}_VERDICT", Mathf.Abs(_spy.LateDeltaSum - 20f) < 0.5f ? "pass" : "FAIL");
        }
    }
}
#endif
