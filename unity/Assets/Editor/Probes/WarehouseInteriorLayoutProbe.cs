using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Flight;
using TbilisiFly.Props;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// WHERE DID THE WAREHOUSE INTERIOR GO? 2026-10-30: the scene log says
    /// `WAREHOUSEINTERIOR ranges=2 lanes=20 cards=20 crates=3 tables=3 guns=3`
    /// on every load, RangeBootstrap adopts both ranges, and the GPU frame shot
    /// from the crane end down the whole hall (RenderShotProbe warehouse_range)
    /// shows an EMPTY floor. A log line proves a builder ran, not that its
    /// output is where the camera is. This prints, for the shed root and every
    /// renderer under Props/WarehouseInteriorFill's holder: world position,
    /// world bounds, enabled, mesh/material presence and layer, plus the same
    /// for the crane and a fan as a known-good reference — so the number that
    /// is wrong names itself.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.WarehouseInteriorLayoutProbe.Run
    /// </summary>
    public static class WarehouseInteriorLayoutProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Debug.Log($"WHLAYOUT scene={scene.IsValid().ToString().ToLowerInvariant()}");

                var sheds = Object.FindFirstObjectByType<ShedSceneFill>(FindObjectsInactive.Include);
                if (sheds == null) { Debug.Log("WHLAYOUT shed=none"); Exit(1); return; }
                sheds.EnsureBuilt();
                var fill = Object.FindFirstObjectByType<WarehouseInteriorFill>(FindObjectsInactive.Include);
                if (fill == null) { Debug.Log("WHLAYOUT interiorFill=none"); Exit(1); return; }
                fill.EnsureBuilt();

                var root = sheds.Warehouse.Root != null ? sheds.Warehouse.Root.transform : null;
                if (root == null) { Debug.Log("WHLAYOUT warehouseRoot=null"); Exit(1); return; }
                Debug.Log($"WHLAYOUT root={root.name} pos={root.position.ToString("F2")} fwd={root.forward.ToString("F3")} " +
                          $"lossyScale={root.lossyScale.ToString("F3")} shell_w={sheds.Warehouse.Shell.Width:F1} " +
                          $"shell_l={sheds.Warehouse.Shell.Length:F1} shell_h={sheds.Warehouse.Shell.Height:F1}");

                // reference objects everyone can see in the frame
                Ref("warehouse_crane"); Ref("warehouse_fan_1");

                var holder = root.Find(WarehouseInteriorFill.HolderName);
                if (holder == null) { Debug.Log("WHLAYOUT holder=none (no child named warehouse_interior under the root)"); Exit(1); return; }
                Debug.Log($"WHLAYOUT holder pos={holder.position.ToString("F2")} localPos={holder.localPosition.ToString("F2")} " +
                          $"lossyScale={holder.lossyScale.ToString("F3")} children={holder.childCount} " +
                          $"activeInHierarchy={holder.gameObject.activeInHierarchy.ToString().ToLowerInvariant()}");

                // per top-level child: what and where
                for (int i = 0; i < holder.childCount; i++)
                {
                    var c = holder.GetChild(i);
                    var rs = c.GetComponentsInChildren<Renderer>(true);
                    var b = WorldBounds(rs);
                    int enabledN = rs.Count(r => r.enabled && r.gameObject.activeInHierarchy);
                    int noMesh = 0, noMat = 0;
                    foreach (var r in rs)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        var smr = r as SkinnedMeshRenderer;
                        if ((mf == null || mf.sharedMesh == null) && (smr == null || smr.sharedMesh == null)) noMesh++;
                        if (r.sharedMaterials.Length == 0 || r.sharedMaterials.Any(m => m == null)) noMat++;
                    }
                    var layers = string.Join("/", rs.Select(r => r.gameObject.layer).Distinct().Select(l => l.ToString()));
                    Debug.Log($"WHLAYOUT child={c.name} pos={c.position.ToString("F2")} localPos={c.localPosition.ToString("F2")} " +
                              $"lossyScale={c.lossyScale.ToString("F3")} active={c.gameObject.activeInHierarchy.ToString().ToLowerInvariant()} " +
                              $"renderers={rs.Length} enabled={enabledN} noMesh={noMesh} noMat={noMat} layers={layers} " +
                              $"bounds_c={(b.HasValue ? b.Value.center.ToString("F2") : "n/a")} " +
                              $"bounds_size={(b.HasValue ? b.Value.size.ToString("F2") : "n/a")}");
                    // one level deeper for the ranges (holder -> unit -> content)
                    if (c.name.Contains("range"))
                        for (int j = 0; j < Mathf.Min(c.childCount, 4); j++)
                        {
                            var u = c.GetChild(j);
                            var ur = u.GetComponentsInChildren<Renderer>(true);
                            var ub = WorldBounds(ur);
                            Debug.Log($"WHLAYOUT   sub={u.name} pos={u.position.ToString("F2")} lossyScale={u.lossyScale.ToString("F3")} " +
                                      $"renderers={ur.Length} bounds_size={(ub.HasValue ? ub.Value.size.ToString("F2") : "n/a")}");
                        }
                }

                // the two cameras RenderShotProbe used today, and whether the
                // interior's bounds fall inside their frusta
                var all = holder.GetComponentsInChildren<Renderer>(true);
                var allB = WorldBounds(all);
                Debug.Log($"WHLAYOUT interior renderers={all.Length} bounds_c={(allB.HasValue ? allB.Value.center.ToString("F2") : "n/a")} " +
                          $"bounds_size={(allB.HasValue ? allB.Value.size.ToString("F2") : "n/a")} " +
                          $"distToRoot={(allB.HasValue ? Vector3.Distance(allB.Value.center, root.position).ToString("F1") : "n/a")}");
                var cam = Object.FindFirstObjectByType<Camera>();
                Debug.Log($"WHLAYOUT sceneCamera={(cam != null ? cam.name : "none")} cullingMask={(cam != null ? cam.cullingMask : 0)}");
                Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.Log($"WHLAYOUT threw={e}");
                Exit(1);
            }
        }

        static void Ref(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) { Debug.Log($"WHLAYOUT ref={name} found=false"); return; }
            var rs = go.GetComponentsInChildren<Renderer>(true);
            var b = WorldBounds(rs);
            Debug.Log($"WHLAYOUT ref={name} pos={go.transform.position.ToString("F2")} lossyScale={go.transform.lossyScale.ToString("F3")} " +
                      $"parent={(go.transform.parent != null ? go.transform.parent.name : "none")} renderers={rs.Length} " +
                      $"bounds_size={(b.HasValue ? b.Value.size.ToString("F2") : "n/a")}");
        }

        static Bounds? WorldBounds(Renderer[] rs)
        {
            Bounds? b = null;
            foreach (var r in rs)
            {
                if (b == null) b = r.bounds; else { var t = b.Value; t.Encapsulate(r.bounds); b = t; }
            }
            return b;
        }

        static void Exit(int code) => ProbeExit.Exit(code);
    }
}
