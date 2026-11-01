using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Adds the port's packages by NAME, with no version, so Unity resolves the
    /// newest version compatible with this editor. Hardcoding versions here was
    /// wrong twice; let the package manager decide.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.SetupPackages.Run
    /// </summary>
    public static class SetupPackages
    {
        static readonly string[] Wanted =
        {
            "com.unity.render-pipelines.universal", // URP — mobile + console capable
            "com.unity.inputsystem",
            "com.unity.cinemachine",
            "com.unity.animation.rigging",          // bone writes for foot.js
            "com.unity.nuget.newtonsoft-json",      // city.json / roadnet parsing
            "com.unity.cloud.gltfast",              // the 221 static GLB
            "com.unity.cloud.draco",                // public/draco/ assets
            "com.unity.collections",
            "com.unity.burst",
            "com.unity.mathematics",
        };

        public static void Run()
        {
            var failed = new List<string>();
            foreach (var id in Wanted)
            {
                AddRequest req = Client.Add(id);
                while (!req.IsCompleted) System.Threading.Thread.Sleep(100);

                if (req.Status == StatusCode.Success)
                    Debug.Log($"PKG ok {req.Result.name}@{req.Result.version}");
                else
                {
                    Debug.Log($"PKG FAIL {id}: {req.Error?.message}");
                    failed.Add(id);
                }
            }
            Debug.Log($"PKG done ok={Wanted.Length - failed.Count}/{Wanted.Length} " +
                      $"failed={string.Join(",", failed)}");
            EditorApplication.Exit(failed.Count == 0 ? 0 : 1);
        }
    }
}
