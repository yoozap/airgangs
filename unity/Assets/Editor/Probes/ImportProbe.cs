using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Reports which importer each asset actually got and what it produced.
    /// A .glb imported before glTFast was installed keeps a DefaultImporter and
    /// yields no GameObject, which looks exactly like a missing file.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ImportProbe.Run
    /// </summary>
    public static class ImportProbe
    {
        static readonly string[] Paths =
        {
            "Assets/Resources/Art/Debug/axis_probe.glb",
            "Assets/Resources/Art/Planes/p51.glb",
            "Assets/Resources/Art/Planes/mig21.glb",
            "Assets/Resources/Art/Props/spitfire.obj",
            "Assets/Resources/Art/Characters/banana.fbx",
            "Assets/Resources/Art/Characters/elephant.glb",
        };

        public static void Run()
        {
            foreach (var p in Paths)
            {
                var imp = AssetImporter.GetAtPath(p);
                var all = AssetDatabase.LoadAllAssetsAtPath(p);
                var go  = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                Debug.Log($"IMP path={p} importer={(imp == null ? "NONE" : imp.GetType().Name)} " +
                          $"subassets={(all?.Length ?? 0)} " +
                          $"meshes={(all?.OfType<Mesh>().Count() ?? 0)} " +
                          $"gameobject={(go != null ? go.name : "NULL")}");
            }
            ProbeExit.Exit(0);
        }
    }
}
