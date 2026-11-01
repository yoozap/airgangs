using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// BUILDS A REAL PLAYER, HEADLESSLY — the only way to prove the loader seam
    /// (Scripts/Core/ArtAssets.cs) actually works. A probe runs inside the
    /// editor, where AssetDatabase is present and every missing Resources key
    /// would be silently papered over; the only honest test is a standalone
    /// binary with no editor under it at all.
    ///
    ///     U=/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity
    ///     "$U" -batchmode -quit -nographics -projectPath unity \
    ///          -buildTarget OSXUniversal \
    ///          -executeMethod TbilisiFly.EditorTools.BuildPlayerHeadless.Run \
    ///          -logFile /tmp/build.log
    ///
    /// Then RUN what it built and read ITS log:
    ///     &lt;out&gt;/TbilisiFly.app/Contents/MacOS/TbilisiFly -batchmode -logFile &lt;log&gt;
    /// Every scenery renderer's own count line must appear there with
    /// playerSafe=true and the same numbers the editor run printed.
    ///
    /// SCENES: Assets/Scenes/Game.unity ONLY. It is the one scene that boots
    /// the game (Fly/Hangar/Tbilisi are legacy), and shipping the legacy ones
    /// would quietly change which boot path a player takes.
    ///
    /// Output goes to $TF_PLAYER_OUT when set, else &lt;project&gt;/../build/player.
    /// </summary>
    public static class BuildPlayerHeadless
    {
        public const string ScenePath = "Assets/Scenes/Game.unity";
        public const string ProductName = "TbilisiFly";

        /// <summary>
        /// EVERY PROJECT SHADER INTO m_AlwaysIncludedShaders, or the player
        /// draws nothing. 18 shaders live in Assets/Shaders and every one of
        /// them is reached ONLY by `Shader.Find("TbilisiFly/...")` at runtime —
        /// no material asset references them, so the build has no reason to
        /// include them and does not. The first real player run said so 11
        /// times ("VEGETATIONRENDERER: TbilisiFly/WindFoliage shader not
        /// found", "TbilisiFly/VegetationRock shader not found") and came out
        /// with partsResolved=0 instances=0: 145,959 trees and 28,599 rocks
        /// silently absent, against partsResolved=10 instances=496,835 in the
        /// editor, where Shader.Find searches the whole AssetDatabase.
        ///
        /// Persisted into ProjectSettings/GraphicsSettings.asset, so it is a
        /// property of the project and not of this one build method.
        /// </summary>
        public static int EnsureAlwaysIncludedShaders()
        {
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" });
            var want = new List<Shader>();
            foreach (var g in guids)
            {
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(g));
                if (sh != null) want.Add(sh);
            }
            // The three built-ins the code also reaches by name.
            foreach (var n in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit", "Unlit/Color" })
            {
                var sh = Shader.Find(n);
                if (sh != null) want.Add(sh);
            }

            var so = new SerializedObject(UnityEngine.Rendering.GraphicsSettings.GetGraphicsSettings());
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            var have = new HashSet<Shader>();
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue is Shader s) have.Add(s);

            int added = 0;
            foreach (var sh in want)
            {
                if (have.Contains(sh)) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
                have.Add(sh);
                added++;
                Debug.Log($"PLAYERBUILD shader_added name={sh.name}");
            }
            if (added > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"PLAYERBUILD always_included_shaders={have.Count} added={added}");
            return added;
        }

        public static void Run()
        {
            EnsureAlwaysIncludedShaders();
            string outDir = Environment.GetEnvironmentVariable("TF_PLAYER_OUT");
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build", "player"));
            Directory.CreateDirectory(outDir);
            string app = Path.Combine(outDir, ProductName + ".app");

            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"PLAYERBUILD ok=false reason=missing-scene scene={ScenePath}");
                EditorApplication.Exit(1);
                return;
            }

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = app,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                // Development: full Debug.Log output and stack traces in the
                // player log, which is the evidence this whole exercise reads.
                // NOT AllowDebugging: it turns on the managed debugger and the
                // CodeReloadManager, which is machinery this proof does not
                // need and which the first run's log was full of.
                options = BuildOptions.Development,
            };

            var sw = Stopwatch.StartNew();
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            sw.Stop();

            var s = report != null ? report.summary : default;
            bool ok = report != null && s.result == BuildResult.Succeeded;
            long bytes = DirSize(app);

            Debug.Log($"PLAYERBUILD ok={ok.ToString().ToLowerInvariant()} " +
                      $"result={(report != null ? s.result.ToString() : "null")} " +
                      $"scene={ScenePath} app={app}");
            Debug.Log($"PLAYERBUILD build_seconds={sw.Elapsed.TotalSeconds:F1} " +
                      $"build_size_mb={bytes / (1024.0 * 1024.0):F1} " +
                      $"total_errors={(report != null ? s.totalErrors : -1)} " +
                      $"total_warnings={(report != null ? s.totalWarnings : -1)}");

            if (!ok) { EditorApplication.Exit(1); return; }

            PatchBootConfig(app);

            // The binary inside the bundle is named after PlayerSettings
            // .productName, NOT after the .app — this project's is "unity".
            string exe = Path.Combine(app, "Contents", "MacOS", PlayerSettings.productName);
            Debug.Log($"PLAYERBUILD exe={exe} exe_exists={File.Exists(exe).ToString().ToLowerInvariant()}");
        }

        /// <summary>
        /// THE ALLOCATOR BLOCK SIZE — 629 MB of this player's resident memory,
        /// for four lines of boot.config.
        ///
        /// Unity's native allocators reserve in blocks and never hand a block
        /// back to the OS, so the process footprint is set by the PEAK, not by
        /// what is live. At the 16 MB default this player's `native_reserved`
        /// high-water of 2.111 GB stayed resident; at 4 MB the same build keeps
        /// the same 2.111 GB reserved and 0.815 GB live — the Profiler numbers
        /// do not move at all — but macOS only counts 2817 MB instead of
        /// 3446 MB of physical footprint, because far fewer whole blocks are
        /// left dirty by the allocate-then-free churn of building the city.
        ///
        /// MEASURED, four candidates patched into a built player's boot.config
        /// and run with the census (phys_footprint / peak / CITYGROUND built):
        ///   default 16 MB   3446 MB / 3901 / 9509 ms
        ///   4 MB            2817 MB / 3882 / 8552 ms   &lt;- this
        ///   2 MB            2924 MB / 3930 / 8664 ms
        ///   8 MB            3349 MB / 3893 / 9062 ms
        ///   1 MB            slower, no better
        ///   mimalloc off    2968 MB / 4009 / 10130 ms  (reserved 1.179 GB,
        ///                   unused 0.378 — much the healthiest INTERNAL
        ///                   picture, but a worse footprint and ~1.5 s slower,
        ///                   so not taken)
        ///   4 MB + small bucket/temp/profiler pools: 3351 MB — WORSE than
        ///                   4 MB alone; shrinking the small pools just moves
        ///                   the churn, so it is not taken either.
        /// No `[Allocator]` warnings, no exceptions and identical count lines
        /// in every candidate.
        ///
        /// Why boot.config and not ProjectSettings/MemorySettings.asset: the
        /// asset is shared with everyone working in this project and its
        /// EDITOR half would change every agent's editor on its next launch.
        /// This is the player-only form of the same setting. The editor can
        /// have it too — MemorySettings.asset's m_MainAllocatorBlockSize —
        /// and should be measured the same way before it is turned on.
        /// </summary>
        public static void PatchBootConfig(string app)
        {
            string cfg = Path.Combine(app, "Contents", "Resources", "Data", "boot.config");
            if (!File.Exists(cfg))
            {
                Debug.LogWarning($"PLAYERBUILD bootconfig_missing path={cfg}");
                return;
            }
            var lines = new List<string>(File.ReadAllLines(cfg));
            var want = new (string key, string val)[]
            {
                ("memorysetup-main-allocator-block-size", "4194304"),
                ("memorysetup-thread-allocator-block-size", "4194304"),
                ("memorysetup-gfx-main-allocator-block-size", "4194304"),
                ("memorysetup-gfx-thread-allocator-block-size", "4194304"),
            };
            int added = 0;
            foreach (var (key, val) in want)
            {
                int at = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
                if (at >= 0) lines[at] = key + "=" + val;
                else { lines.Add(key + "=" + val); added++; }
            }
            File.WriteAllLines(cfg, lines);
            Debug.Log($"PLAYERBUILD bootconfig_patched added={added} main_allocator_block_size=4194304");
        }

        static long DirSize(string path)
        {
            if (!Directory.Exists(path)) return File.Exists(path) ? new FileInfo(path).Length : 0;
            long n = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { n += new FileInfo(f).Length; } catch { }
            }
            return n;
        }
    }
}
