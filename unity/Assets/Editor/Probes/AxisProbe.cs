using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Settles the one unverified assumption in GeoFrame.cs: which axis glTFast
    /// mirrors when it converts glTF (right-handed) to Unity (left-handed).
    ///
    /// If glTFast mirrors a DIFFERENT axis than GeoFrame does, imported props and
    /// the procedurally generated city disagree by a mirror — and a mirrored city
    /// still looks almost right, which is what makes it dangerous.
    ///
    /// Assets/Resources/Art/Debug/axis_probe.glb was authored in Blender with:
    ///   +X arm 4 m long   (centre at x=+2)
    ///   +Y arm 3 m        (centre at y=+1.5)
    ///   +Z arm 2 m        (centre at z=+1)
    ///   a chiral "F" at x=-3 (mirroring is invisible on lengths alone)
    /// glTF export is Y-up right-handed, so Blender's +Y became glTF +Z etc.
    ///
    /// This reads the imported MESH BOUNDS rather than eyeballing a screenshot:
    /// the longest arm tells us which Unity axis +X landed on, and its SIGN tells
    /// us whether it was mirrored.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.AxisProbe.Run
    /// </summary>
    public static class AxisProbe
    {
        const string Path = "Assets/Resources/Art/Debug/axis_probe.glb";

        public static void Run()
        {
            var objs = AssetDatabase.LoadAllAssetsAtPath(Path);
            if (objs == null || objs.Length == 0)
            {
                Debug.Log($"AXIS FAIL asset not imported at {Path} " +
                          "(is glTFast installed?)");
                ProbeExit.Exit(2);
                return;
            }

            Debug.Log($"AXIS loaded subassets={objs.Length}");

            foreach (var m in objs.OfType<Mesh>())
            {
                var b = m.bounds;
                Debug.Log($"AXIS mesh name={m.name} " +
                          $"center=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2}) " +
                          $"size=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2})");
            }

            // Every named node, with its local position — the arm names carry the
            // authored axis and length, so name vs. position is the whole answer.
            foreach (var go in objs.OfType<GameObject>())
                foreach (var t in go.GetComponentsInChildren<Transform>())
                    Debug.Log($"AXIS node name={t.name} " +
                              $"pos=({t.localPosition.x:F2},{t.localPosition.y:F2},{t.localPosition.z:F2})");

            ProbeExit.Exit(0);
        }
    }
}
