using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the shopfronts.js SignPool port check. Mirrors
    /// tools/_probe_shopchar.mjs EXACTLY — same KEY names, same synthetic
    /// inputs, same shapes — per this task's own warning that a stray prefix
    /// or an extra field stops a line from pairing with its JS counterpart
    /// and the comparison then verifies nothing.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.ShopCharProbe.Run
    ///        -logFile /dev/stdout
    ///
    /// PART 1 (the decisive test) replicates ShopfrontBuilder's own wall-snap
    /// loop (src/shopfronts.js:142-185, City/ShopfrontBuilder.cs:274-324) for
    /// one synthetic 4-vertex building + one POI inline — ShopfrontBuilder.
    /// Build() itself needs real StreamingAssets JSON, so this is the same
    /// synthetic-input approach BridgeBuilder/AirportDetail probes use — then
    /// calls the REAL GeoFrame.ToUnity/YawToUnity and the REAL
    /// Quaternion.Euler/Vector3.forward (not a hand-derived sin/cos) to prove
    /// the mirrored sign faces outward.
    ///
    /// PARTS 2 and 3 call the REAL City/SignPool.cs (this unit's own new
    /// file) directly: Update() with real ShopfrontBuilder.SignSpot structs,
    /// and ComputeDraw/ComputeDrawBrand with real ShopfrontBuilder.BrandInfo
    /// structs — no reimplementation, an integration check of the actual
    /// shipped code.
    /// </summary>
    public static class ShopCharProbe
    {
        public static void Run()
        {
            try
            {
                RunWallDecisiveTest();
                RunPoolTest();
                RunDrawTest();
                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"SHOPCHAR_PROBE_EXCEPTION: {e}");
                ProbeExit.Exit(1);
            }
        }

        static string F(float v, int n = 4) => v.ToString("F" + n, System.Globalization.CultureInfo.InvariantCulture);

        // =====================================================================
        // 1. THE DECISIVE TEST
        // =====================================================================
        static void RunWallDecisiveTest()
        {
            // Same 10x10 square + POI as tools/_probe_shopchar.mjs.
            var verts = new (float x, float z)[] { (0, 0), (10, 0), (10, 10), (0, 10) };
            float poiX = 10.5f, poiZ = 5f, maxD = 2.0f, bestD = 1e9f;
            float wnx = 0, wnz = 0, wang = 0;
            int n = verts.Length;
            float cx = 0, cz = 0;
            foreach (var v in verts) { cx += v.x; cz += v.z; }
            cx /= n; cz /= n;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float x1 = verts[i].x, z1 = verts[i].z;
                float ex = verts[j].x - x1, ez = verts[j].z - z1;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                float mx = x1 + ex / 2f, mz = z1 + ez / 2f;
                float t = ((poiX - x1) * ex + (poiZ - z1) * ez) / (len * len);
                t = Mathf.Clamp(t, 0.08f, 0.92f);
                float px = x1 + ex * t, pz = z1 + ez * t;
                float d = Mathf.Sqrt((poiX - px) * (poiX - px) + (poiZ - pz) * (poiZ - pz));
                if (d < maxD && d < bestD)
                {
                    float nx = -ez / len, nz = ex / len;
                    if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }
                    bestD = d;
                    wnx = nx; wnz = nz; wang = Mathf.Atan2(nx, nz);
                }
            }
            Debug.Log($"SHOPCHAR_WALL_NX={F(wnx)}");
            Debug.Log($"SHOPCHAR_WALL_NZ={F(wnz)}");
            Debug.Log($"SHOPCHAR_WALL_ANG={F(wang)}");

            // ShopfrontBuilder.cs:358-359's own mirror, then the REAL GeoFrame.
            Vector3 normalUnity = new Vector3(-wnx, 0f, wnz);
            float yawUnity = GeoFrame.YawToUnity(wang);
            Debug.Log($"SHOPCHAR_WALL_UNITY_NORMAL_X={F(normalUnity.x)}");
            Debug.Log($"SHOPCHAR_WALL_UNITY_NORMAL_Z={F(normalUnity.z)}");
            Debug.Log($"SHOPCHAR_WALL_UNITY_YAW={F(yawUnity)}");

            // The REAL Unity rotation API, not a hand-derived formula.
            Vector3 forward = Quaternion.Euler(0f, yawUnity * Mathf.Rad2Deg, 0f) * Vector3.forward;
            Debug.Log($"SHOPCHAR_WALL_UNITY_FORWARD_X={F(forward.x)}");
            Debug.Log($"SHOPCHAR_WALL_UNITY_FORWARD_Z={F(forward.z)}");
            float dot = forward.x * normalUnity.x + forward.z * normalUnity.z;
            Debug.Log($"SHOPCHAR_WALL_DOT={F(dot)}"); // must be > 0 (and ~1 by construction)
        }

        // =====================================================================
        // 2. SignPool pooling algorithm — exercises the REAL SignPool.cs
        // =====================================================================
        static void RunPoolTest()
        {
            var spots = new List<ShopfrontBuilder.SignSpot>(250);
            for (int i = 0; i < 250; i++)
                spots.Add(new ShopfrontBuilder.SignSpot { Position = new Vector3(i * 10f, 0f, 0f) });

            var pool = new SignPool(spots);

            pool.Update(new Vector3(1000f, 0f, 0f), 1.0f);
            Debug.Log($"SHOPCHAR_POOL_CALL1_ASSIGNED={pool.Assigned.Count}");
            Debug.Log($"SHOPCHAR_POOL_CALL1_FREED={pool.FreedSlots.Count}");

            pool.Update(new Vector3(1200f, 0f, 0f), 1.0f);
            Debug.Log($"SHOPCHAR_POOL_CALL2_ASSIGNED={pool.Assigned.Count}");
            Debug.Log($"SHOPCHAR_POOL_CALL2_FREED={pool.FreedSlots.Count}");
            Debug.Log($"SHOPCHAR_POOL_CALL2_ACTIVE={pool.ActiveCount}");

            bool throttled = !pool.Update(new Vector3(1200f, 0f, 0f), 0.1f); // inside the 0.55s cooldown
            Debug.Log($"SHOPCHAR_POOL_THROTTLED={(throttled ? 1 : 0)}");
        }

        // =====================================================================
        // 3. draw()/drawBrand() decision logic — exercises the REAL
        //    SignPool.ComputeDraw/ComputeDrawBrand
        // =====================================================================
        static void RunDrawTest()
        {
            var a = SignPool.ComputeDraw(new ShopfrontBuilder.SignSpot { Cat = "hospital" });
            Debug.Log($"SHOPCHAR_DRAW_A_BG={a.Background}");
            Debug.Log($"SHOPCHAR_DRAW_A_FG={a.Foreground}");
            Debug.Log($"SHOPCHAR_DRAW_A_CIVIC={(a.IsCivic ? 1 : 0)}");
            Debug.Log($"SHOPCHAR_DRAW_A_LIGHTBG={(a.LightBg ? 1 : 0)}");

            var b = SignPool.ComputeDraw(new ShopfrontBuilder.SignSpot { Cat = "school" });
            Debug.Log($"SHOPCHAR_DRAW_B_LIGHTBG={(b.LightBg ? 1 : 0)}");
            Debug.Log($"SHOPCHAR_DRAW_B_FG={b.Foreground}");

            var c = SignPool.ComputeDraw(new ShopfrontBuilder.SignSpot { Cat = "pharmacy" });
            Debug.Log($"SHOPCHAR_DRAW_C_CIVIC={(c.IsCivic ? 1 : 0)}");
            Debug.Log($"SHOPCHAR_DRAW_C_LIGHTBG={(c.LightBg ? 1 : 0)}");
            Debug.Log($"SHOPCHAR_DRAW_C_FG={c.Foreground}");

            var d = SignPool.ComputeDraw(new ShopfrontBuilder.SignSpot { Cat = "named", Name = "საჯარო რეესტრის სააგენტო" });
            Debug.Log($"SHOPCHAR_DRAW_D_NAMED={(d.IsNamedPlate ? 1 : 0)}");
            Debug.Log($"SHOPCHAR_DRAW_D_FG={d.Foreground}");
            Debug.Log($"SHOPCHAR_DRAW_D_TEXT={d.Text}");

            var eSpot = new ShopfrontBuilder.SignSpot
            {
                Brand = new ShopfrontBuilder.BrandInfo
                {
                    Bg = "#00563f", Fg = "#ffffff", Wordmark = "ნიკორა",
                    Mark = "leaf", Accent = "#ffd200", Script = "georgian",
                },
            };
            var e = SignPool.ComputeDrawSpec(eSpot);
            Debug.Log($"SHOPCHAR_DRAW_E_TEXT={e.Text}");
            Debug.Log($"SHOPCHAR_DRAW_E_MARKKIND={e.MarkKind}");
            Debug.Log($"SHOPCHAR_DRAW_E_MARKCOLOR={e.MarkColor}");
            Debug.Log($"SHOPCHAR_DRAW_E_MARKACCENT={e.MarkAccent}");
            Debug.Log($"SHOPCHAR_DRAW_E_SCRIPTLATIN={(e.ScriptLatin ? 1 : 0)}");

            var fSpot = new ShopfrontBuilder.SignSpot
            {
                Brand = new ShopfrontBuilder.BrandInfo { Bg = "#333", Fg = "#fff", Mark = "none" },
            };
            var ff = SignPool.ComputeDrawSpec(fSpot);
            Debug.Log($"SHOPCHAR_DRAW_F_TEXT_EMPTY={(ff.Text == "" ? 1 : 0)}");
        }
    }
}
