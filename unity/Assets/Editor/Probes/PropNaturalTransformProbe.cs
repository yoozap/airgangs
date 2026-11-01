using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// What does each warehouse-interior FBX import AS? Prints the natural root
    /// transform (the thing Props/WarehouseCraneFill.cs preserves and an earlier
    /// draft of Props/WarehouseInteriorFill.cs reset to identity) plus the real
    /// measured size at that natural transform, so "~100x too small" names itself.
    ///   -executeMethod TbilisiFly.Probes.PropNaturalTransformProbe.Run
    /// </summary>
    public static class PropNaturalTransformProbe
    {
        static readonly string[] Paths =
        {
            "Assets/Resources/Art/Props/wooden_table.fbx",
            "Assets/Resources/Art/Props/ammo_box.fbx",
            "Assets/Resources/Art/Props/makarov.fbx",
            "Assets/Resources/Art/Props/beretta.fbx",
            "Assets/Resources/Art/Props/akm_anim.fbx",
            "Assets/Resources/Art/Props/shooting_range.fbx",
            "Assets/Resources/Art/Props/crane_machine.fbx",
            "Assets/Resources/Art/Props/fan.fbx",
            "Assets/Resources/Art/Props/p51.fbx",
        };

        public static void Run()
        {
            foreach (var p in Paths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (prefab == null) { Debug.Log($"NATTX path={p} found=false"); continue; }
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                inst.transform.position = Vector3.zero;
                var t = inst.transform;
                var e = t.localRotation.eulerAngles;
                var rs = inst.GetComponentsInChildren<Renderer>(true);
                var smr = rs.OfType<SkinnedMeshRenderer>().ToArray();
                Bounds? wb = null;
                foreach (var r in rs) { if (wb == null) wb = r.bounds; else { var b = wb.Value; b.Encapsulate(r.bounds); wb = b; } }
                // mesh-vertex truth, independent of Renderer.bounds (a
                // SkinnedMeshRenderer's bounds can be stale/zero in edit mode)
                Bounds? vb = null;
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var m = mf.transform.localToWorldMatrix;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        var w = m.MultiplyPoint3x4(v);
                        if (vb == null) vb = new Bounds(w, Vector3.zero); else { var b = vb.Value; b.Encapsulate(w); vb = b; }
                    }
                }
                foreach (var s in smr)
                {
                    if (s.sharedMesh == null) continue;
                    var m = s.transform.localToWorldMatrix;
                    foreach (var v in s.sharedMesh.vertices)
                    {
                        var w = m.MultiplyPoint3x4(v);
                        if (vb == null) vb = new Bounds(w, Vector3.zero); else { var b = vb.Value; b.Encapsulate(w); vb = b; }
                    }
                }
                Debug.Log($"NATTX path={p} pos={t.localPosition.ToString("F4")} rot={e.ToString("F1")} " +
                          $"scale={t.localScale.ToString("F4")} renderers={rs.Length} skinned={smr.Length} " +
                          $"rbounds={(wb.HasValue ? wb.Value.size.ToString("F4") : "n/a")} " +
                          $"vbounds={(vb.HasValue ? vb.Value.size.ToString("F4") : "n/a")} " +
                          $"vcenter={(vb.HasValue ? vb.Value.center.ToString("F4") : "n/a")}");
                foreach (var r in rs)
                    Debug.Log($"NATTX   rend path={p} name={r.name} type={r.GetType().Name} " +
                              $"parent={(r.transform.parent != null ? r.transform.parent.name : "none")} " +
                              $"lossy={r.transform.lossyScale.ToString("F3")} rbounds={r.bounds.size.ToString("F4")} " +
                              $"rcenter={r.bounds.center.ToString("F4")} enabled={r.enabled.ToString().ToLowerInvariant()}");
                foreach (var s in smr)
                    Debug.Log($"NATTX   skinned path={p} name={s.name} mesh={(s.sharedMesh != null ? s.sharedMesh.name : "null")} " +
                              $"verts={(s.sharedMesh != null ? s.sharedMesh.vertexCount : 0)} " +
                              $"localBounds={s.localBounds.size.ToString("F4")} rootBone={(s.rootBone != null ? s.rootBone.name : "null")} " +
                              $"bones={s.bones.Length} rbounds={s.bounds.size.ToString("F4")} " +
                              $"updateOffscreen={s.updateWhenOffscreen.ToString().ToLowerInvariant()} " +
                              $"lossy={s.transform.lossyScale.ToString("F4")}");
                Object.DestroyImmediate(inst);
            }
            ProbeExit.Exit(0);
        }
    }
}
