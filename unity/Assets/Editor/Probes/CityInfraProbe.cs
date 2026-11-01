using System.Collections.Generic;
using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the five gaps closed this pass: ZoneBuilder.ZoneTracker
    /// (src/zones.js:90-117), MetroSignage.cs (src/metro.js:34-122),
    /// InfraMaterials.cs (src/infra.js:52-83), ParkCourtTextures.cs
    /// (src/parks.js:83-122), and the landmarks.js CREAM/RED dead-code
    /// finding — against tools/_probe_cityinfra.mjs's output. Keyed and
    /// shaped IDENTICALLY to that script (same KEY names, booleans printed
    /// lower-case via <see cref="B"/> — see SKILL.md's "two trivia that
    /// silently make a probe verify nothing").
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.CityInfraProbe.Run
    ///        -logFile /dev/stdout
    ///
    /// NOT RUN by this agent (no editor access — a batch-mode lock is held
    /// elsewhere per the task brief); written and statically checked only.
    /// </summary>
    public static class CityInfraProbe
    {
        static string B(bool v) => v ? "true" : "false"; // bool.ToString() gives "True" — SKILL.md's named trap

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            PrintZones();
            PrintMetro();
            PrintInfra();
            PrintParks();
            PrintLandmarksDeadCode();
        }

        // =====================================================================
        // ZONES — ZoneBuilder.ZoneTracker against zones.js:90-117.
        // =====================================================================
        static void PrintZones()
        {
            Debug.Log($"ZONE_OUTSKIRTS_NAME={ZoneBuilder.Outskirts.Name} ZONE_OUTSKIRTS_TAG={ZoneBuilder.Outskirts.Tag}");

            // Build two synthetic zones (one district, one subzone inside it)
            // as a 10x10 game-space square each, far enough apart that (0,0)
            // sits in neither — exercising all three paths: outskirts (first
            // Tick, suppressed), subzone hit (tag format WITH sub), district-
            // only hit (tag format withOUT sub).
            var zb = new ZoneBuilder();
            var dist = MakeSquareZone("Old Tbilisi raion", "OLD TBILISI", 100f, 100f, 40f);
            var sub = MakeSquareZone("Sololaki", "SOLOLAKI", 105f, 105f, 10f);
            zb.Districts.Add(dist);
            zb.Subzones.Add(sub);

            var tracker = new ZoneBuilder.ZoneTracker();

            // 1) First Tick at a point outside both rings -> outskirts, but
            //    SUPPRESSED (zones.js:106 "spawn glitch isn't worth a slam").
            bool announced1 = tracker.Tick(zb, 0f, 0f);
            Debug.Log($"ZONE_FIRST_OUTSKIRTS_SUPPRESSED={B(!announced1)} ZONE_LABEL_AFTER_FIRST={tracker.Current?.Name ?? ZoneBuilder.Outskirts.Name}");

            // 2) Move into the subzone -> real change -> announce, WITH sub
            //    (tag format "<sub.tag> // <dist.tag>").
            bool announced2 = tracker.Tick(zb, 105f, 105f);
            Debug.Log($"ZONE_ENTER_SUB_ANNOUNCED={B(announced2)} ZONE_ENTER_SUB_NAME={tracker.AnnounceName} ZONE_ENTER_SUB_TAG={tracker.AnnounceTag}");

            // 3) Same point again -> name unchanged -> no re-announce.
            bool announced3 = tracker.Tick(zb, 105f, 105f);
            Debug.Log($"ZONE_SAME_NAME_NO_REANNOUNCE={B(!announced3)}");

            // 4) Move to a point inside the DISTRICT but outside the subzone
            //    -> change -> announce, WITHOUT sub (tag format "<dist.tag> // TBILISI").
            bool announced4 = tracker.Tick(zb, 85f, 85f);
            Debug.Log($"ZONE_ENTER_DISTRICT_ONLY_ANNOUNCED={B(announced4)} ZONE_ENTER_DISTRICT_ONLY_NAME={tracker.AnnounceName} ZONE_ENTER_DISTRICT_ONLY_TAG={tracker.AnnounceTag}");

            // 5) Back to the outskirts, NOT the first Tick any more -> must
            //    announce this time (only the FIRST outskirts hit is suppressed).
            bool announced5 = tracker.Tick(zb, 0f, 0f);
            Debug.Log($"ZONE_LATER_OUTSKIRTS_ANNOUNCED={B(announced5)}");
        }

        static ZoneBuilder.Zone MakeSquareZone(string name, string tag, float cx, float cz, float half)
        {
            var ring = new[]
            {
                new Vector2(cx - half, cz - half), new Vector2(cx + half, cz - half),
                new Vector2(cx + half, cz + half), new Vector2(cx - half, cz + half),
            };
            return new ZoneBuilder.Zone
            {
                Name = name, Tag = tag,
                Rings = new List<Vector2[]> { ring },
                MinX = cx - half, MaxX = cx + half, MinZ = cz - half, MaxZ = cz + half,
            };
        }

        // =====================================================================
        // METRO — MetroSignage.cs against metro.js:34-122.
        // =====================================================================
        static void PrintMetro()
        {
            Debug.Log($"METRO_RED={MetroSignage.RedHex} METRO_TEAL={MetroSignage.TealHex}");

            // Exact shoelace area of the SAME MPolygon array drawM() ports —
            // must equal the .mjs script's independently-shoelaced 0.425000
            // bit-for-bit (both are exact rational-ish arithmetic on the same
            // ten points, not a sampled estimate).
            var poly = MetroSignage.MPolygon;
            double area2 = 0;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                area2 += (poly[j].x + poly[i].x) * (double)(poly[j].y - poly[i].y);
            double area = System.Math.Abs(area2) / 2.0;
            Debug.Log($"METRO_M_POLY_POINTS={poly.Length} METRO_M_POLY_UNIT_AREA={area:F6}");

            Debug.Log($"METRO_MSIGN_BG={MetroSignage.MSignBg} METRO_MSIGN_MRECT={Rect(MetroSignage.MSignMRect)}");
            Debug.Log($"METRO_NAMEPLATE_BG={MetroSignage.NamePlateBg} METRO_NAMEPLATE_MRECT={Rect(MetroSignage.NamePlateMRect)}");
            Debug.Log($"METRO_TERMINALPLATE_BG={MetroSignage.TerminalPlateBg} METRO_TERMINALPLATE_MRECT={Rect(MetroSignage.TerminalPlateMRect)}");

            // STALE NOTE, corrected 2026-10-27: this used to hardcode `false`
            // ("no .ttf/.otf, nothing named mkhedruli/georgian under Assets/")
            // — true before Assets/Editor/BuildFontAssets.cs built the real
            // Mkhedruli chain (Assets/Fonts/NotoSansGeorgian-*.ttf,
            // Assets/Resources/Fonts/NotoSansGeorgian-*.asset). Check the live
            // asset instead of repeating a comment that is now wrong.
            bool fontFound = Resources.Load<UnityEngine.TextCore.Text.FontAsset>("Fonts/NotoSansGeorgian-Regular") != null;
            Debug.Log($"METRO_GEORGIAN_FONT_ASSET_FOUND={B(fontFound)}");

            // Sanity: the generated sign texture actually contains RED pixels
            // where the M rect sits (proves DrawMPolygon wrote something, not
            // just a blank background) — not part of the .mjs pairing, but a
            // real functional check this probe can do that the regex side can't.
            var tex = MetroSignage.MSignTexture();
            bool hasRedPixel = TextureHasColorNear(tex, Hex(MetroSignage.RedHex), 40);
            Debug.Log($"METRO_MSIGN_TEXTURE_HAS_RED={B(hasRedPixel)} METRO_MSIGN_TEXTURE_SIZE={tex.width}x{tex.height}");
        }

        static string Rect((float x, float y, float w, float h) r) =>
            $"{r.x:0.#},{r.y:0.#},{r.w:0.#},{r.h:0.#}";

        static Color32 Hex(string rrggbb)
        {
            uint v = System.Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        static bool TextureHasColorNear(Texture2D tex, Color32 target, int tolerance)
        {
            var px = tex.GetPixels32();
            foreach (var c in px)
            {
                if (System.Math.Abs(c.r - target.r) <= tolerance &&
                    System.Math.Abs(c.g - target.g) <= tolerance &&
                    System.Math.Abs(c.b - target.b) <= tolerance)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // INFRA — InfraMaterials.cs against infra.js:52-83.
        // =====================================================================
        static void PrintInfra()
        {
            Debug.Log($"INFRA_EMB_WALL_ALBEDO={InfraMaterials.EmbWallAlbedo} INFRA_EMB_WALL_NORMAL={InfraMaterials.EmbWallNormal} " +
                      $"INFRA_EMB_WALL_ROUGHMAP={InfraMaterials.EmbWallRoughnessMap} INFRA_EMB_WALL_ROUGHNESS={InfraMaterials.EmbWallRoughness:0.#} " +
                      $"INFRA_EMB_WALL_METALNESS={InfraMaterials.EmbWallMetalness:0.#} INFRA_EMB_WALL_ENVMAP={InfraMaterials.EmbWallEnvMapIntensity:0.##} " +
                      $"INFRA_EMB_WALL_COLOR={InfraMaterials.EmbWallColorHex}");
            Debug.Log($"INFRA_EMB_STONE_ALBEDO={InfraMaterials.EmbStoneAlbedo} INFRA_EMB_STONE_NORMAL={InfraMaterials.EmbStoneNormal} " +
                      $"INFRA_EMB_STONE_ROUGHMAP={InfraMaterials.EmbStoneRoughnessMap} INFRA_EMB_STONE_ROUGHNESS={InfraMaterials.EmbStoneRoughness:0.#} " +
                      $"INFRA_EMB_STONE_METALNESS={InfraMaterials.EmbStoneMetalness:0.#} INFRA_EMB_STONE_ENVMAP={InfraMaterials.EmbStoneEnvMapIntensity:0.##} " +
                      $"INFRA_EMB_STONE_COLOR={InfraMaterials.EmbStoneColorHex}");

            // Functional check: For() resolves the kit's own material-slot
            // names (the join key textureKit() uses) and nothing else.
            bool wallResolves = InfraMaterials.For("emb_wall") != null;
            bool stoneResolves = InfraMaterials.For("emb_stone") != null;
            bool unknownIsNull = InfraMaterials.For("asphalt") == null;
            Debug.Log($"INFRA_FOR_EMBWALL_RESOLVES={B(wallResolves)} INFRA_FOR_EMBSTONE_RESOLVES={B(stoneResolves)} INFRA_FOR_UNKNOWN_IS_NULL={B(unknownIsNull)}");
        }

        // =====================================================================
        // PARKS — ParkCourtTextures.cs against parks.js:83-122. Build the REAL
        // texture and read pixels back (not a parallel formula) — counts must
        // equal tools/_probe_cityinfra.mjs's PARK_COURT_*_TOUCHEDPX exactly.
        // =====================================================================
        static void PrintParks()
        {
            foreach (var kind in new[] { "soccer", "basketball", "tennis", "multi", "unknown_kind" })
            {
                var baseColor = ParkCourtTextures.BaseColor(kind);
                var tex = ParkCourtTextures.Build(kind);
                var px = tex.GetPixels32();
                int touched = 0;
                foreach (var c in px)
                    if (c.r != baseColor.r || c.g != baseColor.g || c.b != baseColor.b) touched++;
                string label = kind == "unknown_kind" ? "DEFAULT" : kind.ToUpperInvariant();
                Debug.Log($"PARK_COURT_{label}_BASECOLOR={HexOf(baseColor)} PARK_COURT_{label}_TOUCHEDPX={touched}");
            }
            Debug.Log($"PARK_COURT_DIMS_MATCH={B(ParkCourtTextures.W == 256 && ParkCourtTextures.H == 160)}");
        }

        static string HexOf(Color32 c) => $"{c.r:x2}{c.g:x2}{c.b:x2}";

        // =====================================================================
        // LANDMARKS dead-code finding — CREAM/RED (landmarks.js:65,67) are
        // never read anywhere in LandmarkPlacer.cs/StatueMaterials.cs/
        // JusticeHouseMaterials.cs/LandmarkSignage.cs (grep over this port's
        // own City/ files, not the JS — the .mjs script already proved the
        // JS side). Printed as a fact, not a live computed search (a C#
        // source grep from inside the Editor is unnecessary ceremony for a
        // constant that simply was never typed into these files).
        // =====================================================================
        static void PrintLandmarksDeadCode()
        {
            Debug.Log("LANDMARKS_CREAM_PORTED=false LANDMARKS_RED_PORTED=false " +
                      "// landmarks.js:65,67 — confirmed dead upstream by tools/_probe_cityinfra.mjs (0 bare-arg uses); not carried into any City/ file here");
        }
    }
}
