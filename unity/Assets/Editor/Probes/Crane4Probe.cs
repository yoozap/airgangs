using UnityEngine;
using TbilisiFly.Props;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_crane4.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.Crane4Probe.Run -logFile /dev/stdout
    //
    // Exercises the port unit recorded in docs/port_units/crane4.json:
    // WarehouseCrane.SwitchboardLayout/LampColors (crane.js:666-707, 728-736)
    // and CinemaRangeMath's three new helpers — NearestLaneAt, TrolleyRunTo,
    // AimPointOnCard (cinema.js:1744-1795). KEY=value lines use the IDENTICAL
    // names the .mjs script prints, so the two outputs line up for a diff.
    public static class Crane4Probe
    {
        public static void Run()
        {
            // ---- WarehouseCrane.SwitchboardLayout --------------------------
            Vector3 board = new Vector3(13.0f, 0f, 30.2f);
            float wallAt = 23.3f;
            var sb = WarehouseCrane.ComputeSwitchboardLayout(board, wallAt);
            Debug.Log($"CRANE4_SB_BOARD_CENTER_X={sb.BoardBoxCenter.x:F4}");
            Debug.Log($"CRANE4_SB_BOARD_CENTER_Y={sb.BoardBoxCenter.y:F4}");
            Debug.Log($"CRANE4_SB_BOARD_CENTER_Z={sb.BoardBoxCenter.z:F4}");
            Debug.Log($"CRANE4_SB_SOCKET_CENTER_X={sb.SocketBoxCenter.x:F4}");
            Debug.Log($"CRANE4_SB_SOCKET_CENTER_Y={sb.SocketBoxCenter.y:F4}");
            Debug.Log($"CRANE4_SB_SOCKET_CENTER_Z={sb.SocketBoxCenter.z:F4}");
            Debug.Log($"CRANE4_SB_BUTTON_X={sb.ButtonPos.x:F4}");
            Debug.Log($"CRANE4_SB_BUTTON_Y={sb.ButtonPos.y:F4}");
            Debug.Log($"CRANE4_SB_BUTTON_Z={sb.ButtonPos.z:F4}");
            Debug.Log($"CRANE4_SB_LAMP_X={sb.LampPos.x:F4}");
            Debug.Log($"CRANE4_SB_LAMP_Y={sb.LampPos.y:F4}");
            Debug.Log($"CRANE4_SB_LAMP_Z={sb.LampPos.z:F4}");

            // ---- WarehouseCrane.LampColors ---------------------------------
            var modes = new[] { WarehouseCrane.FanSwitch.Setting.Off,
                                 WarehouseCrane.FanSwitch.Setting.Lo,
                                 WarehouseCrane.FanSwitch.Setting.Hi };
            for (int i = 0; i < modes.Length; i++)
            {
                var (lamp, emissive, button) = WarehouseCrane.LampColors(modes[i]);
                Debug.Log($"CRANE4_LAMP_MODE{i}_LAMP={ToHex(lamp)}");
                Debug.Log($"CRANE4_LAMP_MODE{i}_EMISSIVE={ToHex(emissive)}");
                Debug.Log($"CRANE4_LAMP_MODE{i}_BUTTON={ToHex(button)}");
            }

            // ---- CinemaRangeMath.NearestLaneAt -----------------------------
            float[] acrossOffset = { 0.0f, 1.67f, 3.34f, 3.3f };
            float[] railDotFwd = { 1.0f, 1.0f, 1.0f, 0.2f };
            int? hit = RangeStageMath.NearestLaneAt(acrossOffset, railDotFwd, 3.3f);
            int? miss = RangeStageMath.NearestLaneAt(acrossOffset, railDotFwd, 20.0f);
            Debug.Log($"CRANE4_LANEAT_HIT={(hit.HasValue ? hit.Value.ToString() : "null")}");
            Debug.Log($"CRANE4_LANEAT_MISS={(miss.HasValue ? miss.Value.ToString() : "null")}");

            // ---- CinemaRangeMath.TrolleyRunTo ------------------------------
            float distOut = RangeStageMath.TrolleyRunTo(12.4f, 2.0f, 0f, 8f, null, out float at);
            Debug.Log($"CRANE4_RUNTO_CLAMPED={distOut:F4}");
            Debug.Log($"CRANE4_RUNTO_AT={at:F4}");
            float distOverride = RangeStageMath.TrolleyRunTo(12.4f, 2.0f, 0f, 8f, 1.5f, out _);
            Debug.Log($"CRANE4_RUNTO_OVERRIDE={distOverride:F4}");

            // ---- CinemaRangeMath.AimPointOnCard ----------------------------
            Vector3 centre = new Vector3(10f, 1.6f, -5f);
            Vector3 ap = RangeStageMath.AimPointOnCard(centre, new Vector3(1, 0, 0), new Vector3(0, -1, 0),
                0.5f, 0.7f, 0.52f, 0.40f);
            Debug.Log($"CRANE4_AIMPOINT_X={ap.x:F4}");
            Debug.Log($"CRANE4_AIMPOINT_Y={ap.y:F4}");
            Debug.Log($"CRANE4_AIMPOINT_Z={ap.z:F4}");

            Debug.Log("CRANE4_PROBE_DONE=1");
        }

        // Matches JS's `(0xRRGGBB).toString(16)`: lowercase, no leading
        // zero-padding (0x000000 prints "0", not "000000").
        static string ToHex(Color32 c) => (((int)c.r << 16) | ((int)c.g << 8) | c.b).ToString("x");
    }
}
