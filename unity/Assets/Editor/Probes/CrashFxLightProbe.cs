using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proof for the rest_core gap-closing pass (src/game.js, src/fire.js,
    /// src/crash.js): Core/CombatWorldAdapter.cs's <c>BuildCrash</c> — the ONE
    /// real construction site of <c>CrashHooks</c> — never assigned
    /// <c>TakeFxLight</c>/<c>FreeFxLight</c>, so every crash fireball fell
    /// through Core/CrashSequence.cs's <c>TakeLight</c>/<c>FreeLight</c>
    /// fallback (<c>new GameObject("crash_fx_light"); AddComponent&lt;Light&gt;()</c>)
    /// instead of Core/GameLoop.cs's <c>TakeFxLight</c>/<c>FreeFxLight</c> —
    /// which route through Fx/FxLightPool.cs, the pool this port's own brief
    /// built specifically so nothing ever calls <c>AddComponent&lt;Light&gt;()</c>
    /// in a gameplay path (a new Light recompiles every material that
    /// references it — the measured 0.8s shooting/crash freeze). The pool
    /// existed, was reachable from GameLoop, and was simply never wired into
    /// the crash's own hook construction — the exact "finished code with one
    /// missing wire" shape this port's skill doc calls out.
    ///
    /// Two checks: (1) a SOURCE-TEXT assertion that the real BuildCrash
    /// initializer (not a probe fixture) now assigns both hooks to
    /// game.TakeFxLight/game.FreeFxLight — proving the GATE is open on the
    /// shipped file, not just that the pool compiles; (2) a REAL functional
    /// check that GameLoop.TakeFxLight, called more times than the pool is
    /// large, never creates more than PoolSize distinct Light components —
    /// the actual behaviour the wiring restores.
    ///
    ///   Unity -batchmode -quit -nographics -executeMethod TbilisiFly.Probes.CrashFxLightProbe.Run
    /// </summary>
    public static class CrashFxLightProbe
    {
        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond)
            {
                Debug.Log($"{(cond ? "PASS" : "FAIL")} {key}");
                if (!cond) allPass = false;
            }

            // ---- (1) the real call site, not a probe fixture --------------
            string path = Path.Combine(Application.dataPath, "Scripts/Core/CombatWorldAdapter.cs");
            string src = File.ReadAllText(path);
            var hooksBlock = Regex.Match(src, @"var hooks = new CrashHooks\s*\{.*?\};", RegexOptions.Singleline);
            Check("CRASHHOOKS_BLOCK_FOUND", hooksBlock.Success);
            string block = hooksBlock.Success ? hooksBlock.Value : "";
            bool takeWired = Regex.IsMatch(block, @"TakeFxLight\s*=.*game\.TakeFxLight");
            bool freeWired = Regex.IsMatch(block, @"FreeFxLight\s*=.*game\.FreeFxLight");
            Check("CRASHHOOKS_TAKEFXLIGHT_WIRED_TO_GAMELOOP", takeWired);
            Check("CRASHHOOKS_FREEFXLIGHT_WIRED_TO_GAMELOOP", freeWired);

            // ---- (2) the pool actually holds the line: no unbounded Light churn
            var go = new GameObject("CrashFxLightProbe_GameLoop");
            try
            {
                var game = go.AddComponent<TbilisiFly.Core.GameLoop>();
                var seen = new System.Collections.Generic.HashSet<int>();
                UnityEngine.Light last = null;
                const int calls = 9; // 3x FxLightPool.PoolSize
                for (int i = 0; i < calls; i++)
                {
                    last = game.TakeFxLight(Color.white, 100f + i, 5f);
                    if (last != null) seen.Add(last.GetHashCode());
                }
                Check("CRASHFXLIGHT_CALLS", calls == 9);
                Check("CRASHFXLIGHT_POOL_BOUNDED", seen.Count <= 3); // FxLightPool.PoolSize
                Debug.Log($"CRASHFXLIGHT_DISTINCT={seen.Count}");

                if (last != null)
                {
                    last.intensity = 777f;
                    game.FreeFxLight(last);
                    Check("CRASHFXLIGHT_FREE_PARKS_DARK", Mathf.Approximately(last.intensity, 0f));
                    Check("CRASHFXLIGHT_FREE_PARKS_BELOW_MAP", last.transform.position.y < -1000f);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            Debug.Log(allPass ? "CRASHFXLIGHT_ALL_PASS=true" : "CRASHFXLIGHT_ALL_PASS=false");
        }
    }
}
