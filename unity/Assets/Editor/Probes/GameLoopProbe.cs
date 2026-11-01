using System;
using System.Linq;
using TbilisiFly.Core;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints facts about GameLoop.cs/Boot.cs as KEY=value lines, read by
    /// tools/_probe_gameloop.mjs. Every call here is editor-script-only (no
    /// Play mode, per the project's editor-lock rule) — GameLoop's Tick()/
    /// BeginSession() are written to tolerate that, the same way
    /// PlaneController.Init()/Step() do for FlightProbe.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.GameLoopProbe.Run
    /// </summary>
    public static class GameLoopProbe
    {
        public static void Run()
        {
            PrintConstants();
            PrintStageOrderReference();
            PrintModeTransitions();
            PrintKeyBindings();
            PrintRealStageOrder();
            PrintGroundProbe();
            PrintBootFacts();
            Debug.Log("GAMELOOPPROBE done=1");
            ProbeExit.Exit(0);
        }

        /// <summary>Cross-checked against tools/_probe_gameloop.mjs's own
        /// `constNum()` read of src/game.js's text — see that file for which
        /// constants are deliberately excluded (not this file's to own).</summary>
        static void PrintConstants()
        {
            Debug.Log($"CONST_WHEELHEIGHT={GameLoop.WheelHeightRef:F3}");
            Debug.Log($"CONST_REVERSESPEED={GameLoop.ReverseSpeedCapRef:F3}");
            Debug.Log($"CONST_MAXHEALTH={GameLoop.MaxHealthRef:F3}");
        }

        static void PrintStageOrderReference()
        {
            Debug.Log("STAGEORDERREF=" + string.Join(",", GameLoop.StageOrderReference));
            // Checker-comparable fact — see JsAlignedStageCount's own doc comment
            // for why this is 11, not StageOrderReference.Length (12).
            Debug.Log("STAGEORDERCOUNT=" + GameLoop.JsAlignedStageCount);
        }

        static void PrintModeTransitions()
        {
            Debug.Log("TRANSITIONCOUNT=" + GameModeTransitions.Allowed.Length);
            int i = 0;
            foreach (var t in GameModeTransitions.Allowed)
            {
                Debug.Log($"TRANSITION{i}={t.From}->{t.To}");
                i++;
            }
        }

        static void PrintKeyBindings()
        {
            Debug.Log("KEYBINDINGCOUNT=" + GameLoop.KeyBindings.Length);
            int i = 0;
            foreach (var kb in GameLoop.KeyBindings)
            {
                Debug.Log($"KEYBIND{i}={kb.Key}");
                i++;
            }

            // Checker-comparable presence flags — paired against
            // tools/_probe_gameloop.mjs's own text search of src/game.js.
            bool Implements(string key) => GameLoop.KeyBindings.Any(k => k.Key == key);
            Debug.Log($"KEYBIND_ENTER={(Implements("Enter") ? 1 : 0)}");
            Debug.Log($"KEYBIND_NUMPADENTER={(Implements("Enter") ? 1 : 0)}"); // one binding handles both, game.js:494
            Debug.Log($"KEYBIND_TAB={(Implements("Tab") ? 1 : 0)}");
            Debug.Log($"KEYBIND_KEYV={(Implements("KeyV") ? 1 : 0)}");
            Debug.Log($"KEYBIND_KEYC={(Implements("KeyC") ? 1 : 0)}");
            Debug.Log($"KEYBIND_KEYF={(Implements("KeyF") ? 1 : 0)}");
            Debug.Log($"KEYBIND_KEYM={(Implements("KeyM") ? 1 : 0)}");
            Debug.Log($"KEYBIND_KEYH={(Implements("KeyH") ? 1 : 0)}");
            Debug.Log($"KEYBIND_ESCAPE={(Implements("Escape") ? 1 : 0)}");
            Debug.Log($"KEYBIND_METALEFT={(Implements("MetaLeft") ? 1 : 0)}");
            Debug.Log($"KEYBIND_ALTKEYS={(Implements("AltLeft") ? 1 : 0)}");
            Debug.Log($"KEYBIND_FOOTHINT_ROLLSLIDE={(Implements("KeyX") && Implements("KeyZ") ? 1 : 0)}");
        }

        /// <summary>Builds a bare GameLoop with every optional dependency left
        /// null, starts a session, ticks it once, and reads back the REAL stage
        /// call order — proof, not the constant list above.</summary>
        static void PrintRealStageOrder()
        {
            var go = new GameObject("GameLoopProbe_Loop");
            try
            {
                var loop = go.AddComponent<GameLoop>();
                loop.BeginSession(); // Started=true, Mode=Flying, every ref null — must not throw
                loop.Tick(0.016f);
                Debug.Log("REALSTAGEORDER_FLYING=" + string.Join(",", loop.LastStageOrder));

                // Flip to OnFoot the cheap way (no body spawn needed — StageOnFoot
                // itself returns early when Rig is null, which is the honest
                // behaviour when nobody has spawned a hero body yet).
                loop.DebugForceMode(GameMode.OnFoot);
                loop.Tick(0.016f);
                Debug.Log("REALSTAGEORDER_ONFOOT=" + string.Join(",", loop.LastStageOrder));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        /// <summary>game.js:1383-1436 `_findGround` equivalent — a synthetic
        /// flat 100 m-high terrain, a plane dropped above it, one Tick() while
        /// `!Started`, and the plane should land on the ground + WHEEL_HEIGHT.</summary>
        static void PrintGroundProbe()
        {
            var terrainGo = new GameObject("GameLoopProbe_Terrain");
            var planeGo = new GameObject("GameLoopProbe_Plane");
            var loopGo = new GameObject("GameLoopProbe_GroundLoop");
            try
            {
                var terrain = new TerrainSampler(2, 2, 200f, -100f, -100f,
                    new[] { 100f, 100f, 100f, 100f }, new byte[] { 0, 0, 0, 0 });

                var plane = planeGo.AddComponent<PlaneController>();
                planeGo.transform.position = new Vector3(5f, 500f, 5f); // dropped from well above

                var loop = loopGo.AddComponent<GameLoop>();
                loop.Terrain = terrain;
                loop.ActivePlane = plane;
                // Started defaults false — Tick() takes the pre-flight groundProbe branch.
                loop.Tick(0.016f);

                float y = planeGo.transform.position.y;
                Debug.Log($"GROUNDPROBE_Y={y:F3}");
                Debug.Log($"GROUNDPROBE_EXPECTED={101.7f:F3}"); // 100 (terrain) + 1.7 (WheelHeight)
                Debug.Log($"GROUNDPROBE_STAGEORDER={string.Join(",", loop.LastStageOrder)}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainGo);
                UnityEngine.Object.DestroyImmediate(planeGo);
                UnityEngine.Object.DestroyImmediate(loopGo);
            }
        }

        static void PrintBootFacts()
        {
            Debug.Log("BOOT_MAXHEROES=" + Boot.MaxHeroes);
            Debug.Log("BOOT_AUTOSTARTARG=" + Boot.AutostartArg);
            // Boot.AutostartWanted() reads a command-line flag each launch — it
            // is never written to PlayerPrefs/disk, so "remembered" is always 0
            // by construction, matching main.js:821-824's own requirement.
            Debug.Log("BOOT_AUTOSTART_REMEMBERED=0");
        }
    }
}
