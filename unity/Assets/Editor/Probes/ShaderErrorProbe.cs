using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints the actual compile errors for a shader. FxProbe reports only a
    /// has-error boolean, and Unity does not put shader errors in the batch-mode
    /// log, so without this the failure is invisible: three of five new shaders
    /// report haserror=1 with no stated reason.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ShaderErrorProbe.Run
    /// </summary>
    public static class ShaderErrorProbe
    {
        static readonly string[] Paths =
        {
            "Assets/Shaders/ImpactSprite.shader",
            "Assets/Shaders/MuzzleFlash.shader",
            "Assets/Shaders/WindFoliage.shader",
            "Assets/Shaders/Fireball.shader",
            "Assets/Shaders/Blast.shader",
        };

        public static void Run()
        {
            foreach (var p in Paths)
            {
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(p);
                if (sh == null) { Debug.Log($"SHERR path={p} MISSING"); continue; }
                // GetShaderMessages returns nothing in batch mode even when
                // ShaderHasError is true, so force a reimport: THAT writes the real
                // compile error into the editor log, where it can be grepped.
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate
                                             | ImportAssetOptions.ForceSynchronousImport);
                sh = AssetDatabase.LoadAssetAtPath<Shader>(p);
                int n = ShaderUtil.GetShaderMessageCount(sh);
                Debug.Log($"SHERR path={p} hasError={ShaderUtil.ShaderHasError(sh)} messages={n}");
                if (n == 0) continue;
                var msgs = ShaderUtil.GetShaderMessages(sh);
                for (int i = 0; i < msgs.Length && i < 8; i++)
                {
                    var m = msgs[i];
                    Debug.Log($"SHMSG path={p} i={i} severity={m.severity} line={m.line} " +
                              $"platform={m.platform} text={m.message.Replace('\n', ' ')}");
                }
            }
            ProbeExit.Exit(0);
        }
    }
}
