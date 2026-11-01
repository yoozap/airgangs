using System;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves ShedLayout.cs reproduces src/shed.js. ShedLatLon/ShedYaw are
    /// checked against the REAL, exported SHED_LL/SHED_YAW consts;
    /// LevelPad()/Ramps()/FloorTileUvScale() are checked against faithful
    /// replications of buildShed's analytical pieces (the full function
    /// cannot run standalone — it loads a real GLB — so this is the same
    /// documented pattern as PlaneHallLayoutProbe's Pack()/Walled()). Same
    /// synthetic height field, same two scale cases (warehouse, plane hall),
    /// as tools/_probe_shedlayout.mjs.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ShedLayoutProbe.Run
    /// </summary>
    public static class ShedLayoutProbe
    {
        // Same synthetic terrain as the JS probe: a gentle slope plus a sine
        // ripple, reproducible exactly from the same formula.
        static float? HeightAt(float x, float z) => 100f + x * 0.01f + Mathf.Sin(z * 0.003f) * 3f;

        static void RunScale(string id, Vector3 scale, float px, float pz, float apron, float rampLen)
        {
            var door = ShedLayout.ScaledDoor(scale);
            var unscaled = new Vector3(16.0f, 5.0f, 46.3f);
            var size = Vector3.Scale(unscaled, scale);
            float halfW = size.x / 2f + apron, halfL = size.z / 2f + apron;
            var pad = ShedLayout.LevelPad(px, pz, halfW, halfL, ShedLayout.ShedYaw, HeightAt);
            Debug.Log($"PAD id={id} padTop={pad.PadTop:F5} padDepth={pad.PadDepth:F5} hi={pad.Hi:F5} lo={pad.Lo:F5}");

            var rr = ShedLayout.Ramps(px, pz, halfL, rampLen, ShedLayout.ShedYaw, pad.PadTop, door.x, HeightAt);
            for (int i = 0; i < rr.Length; i++)
            {
                var r = rr[i];
                if (!r.Needed) { Debug.Log($"RAMP id={id}{i} needed=0"); continue; }
                Debug.Log($"RAMP id={id}{i} needed=1 depth={r.Depth:F5} k={r.K:F6} zNear={r.ZNear:F5} zFar={r.ZFar:F5} rampHalf={r.RampHalf:F5}");
            }

            var uv = ShedLayout.FloorTileUvScale(size.x, size.z, 12f);
            Debug.Log($"UV id={id} rx={uv.rx:F5} rz={uv.rz:F5}");
        }

        public static void Run()
        {
            // ShedLat/ShedLon are double now, not a float32 Vector2 — float32 rounding moved
            // the shed 0.147 m, which showed up as the KEEPOUT mismatch in
            // PlaneHallLayoutProbe. See ShedLayout.ShedLat's note.
            Debug.Log($"CONST id=_const lat={ShedLayout.ShedLat:F6} lon={ShedLayout.ShedLon:F6} yaw={ShedLayout.ShedYaw:F6}");

            RunScale("warehouse", new Vector3(3f, 3f, 3f), 1000f, -500f, 7f, 14f);
            RunScale("hall", new Vector3(16f, 7.5f, 9.3f), -18371f, -7059f, 15f, 80f);

            ProbeExit.Exit(0);
        }
    }
}
