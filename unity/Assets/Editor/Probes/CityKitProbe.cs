using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verifies the two genuinely new pieces this pass ported into
    /// CityRenderer.cs/BuildingKit.cs — the per-instance hash tint (KitTint.cs)
    /// and the panel-block collider bounds (BuildingKit.PanelColliderBounds) —
    /// neither of which any manifest unit claimed before. Same `bi` samples,
    /// same key names/shapes, as tools/_probe_citykit.mjs; diff the two by hand.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityKitProbe.Run
    /// </summary>
    public static class CityKitProbe
    {
        static readonly int[] Samples = { 0, 1, 7, 1000, 40503, 193145 };

        public static void Run()
        {
            foreach (int bi in Samples)
            {
                Log("out", bi, KitTint.Out(bi));
                Log("apt", bi, KitTint.Apt(bi));
                Log("shed", bi, KitTint.Shed(bi));
                Log("house", bi, KitTint.House(bi));
                Log("panel", bi, KitTint.Panel(bi));
            }

            // ---- panel collider bounds: one placement, hand-checkable ----
            // Pos/AngUnity are already UNITY-space fields on PanelPlacement
            // (BuildingKit.TryClaim ran them through GeoFrame.ToUnity/
            // YawToUnity when the placement was decided), so this constructs
            // the struct directly rather than going through TryClaim again.
            var p = new BuildingKit.PanelPlacement
            {
                Pos = new Vector3(10f, 2f, -5f),
                AngUnity = 0.3f,
                Sections = 4,
                Bi = 12345,
            };
            var b = BuildingKit.PanelColliderBounds(p);
            Debug.Log($"CRK id=panelcollider center_x={b.center.x:F4} center_y={b.center.y:F4} center_z={b.center.z:F4} " +
                      $"size_x={b.size.x:F4} size_y={b.size.y:F4} size_z={b.size.z:F4}");

            // A rotated LxD=48x11.4 box's AABB half-extent along each horizontal
            // axis is |L/2*cos|+|D/2*sin| (standard rotated-rect AABB formula) —
            // an INDEPENDENT closed-form check of the same Bounds.Encapsulate
            // result the method above already computed, not a re-run of it.
            float L = p.Sections * BuildingKit.PanelSectionW, D = BuildingKit.PanelDepth, H = BuildingKit.PanelColliderHeight;
            float c = Mathf.Cos(p.AngUnity), s = Mathf.Sin(p.AngUnity);
            float halfX = Mathf.Abs(L / 2f * c) + Mathf.Abs(D / 2f * s);
            float halfZ = Mathf.Abs(L / 2f * s) + Mathf.Abs(D / 2f * c);
            bool sizeOk = Mathf.Abs(b.size.x - 2f * halfX) < 1e-3f && Mathf.Abs(b.size.z - 2f * halfZ) < 1e-3f;
            bool heightOk = Mathf.Abs(b.size.y - H) < 1e-4f && Mathf.Abs(b.center.y - (p.Pos.y + H / 2f)) < 1e-4f;
            Debug.Log($"CRK id=assert_panelColliderSize result={(sizeOk ? "PASS" : "FAIL")}");
            Debug.Log($"CRK id=assert_panelColliderHeight result={(heightOk ? "PASS" : "FAIL")}");

            ProbeExit.Exit(0);
        }

        static void Log(string kind, int bi, Color c)
        {
            Debug.Log($"CKT id={kind} bi={bi} rgb={c.r:F6},{c.g:F6},{c.b:F6}");
        }
    }
}
