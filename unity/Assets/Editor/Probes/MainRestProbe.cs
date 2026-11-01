using System;
using System.Reflection;
using TbilisiFly.Cinema;
using TbilisiFly.Core;
using TbilisiFly.Flight;
using TbilisiFly.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints the Unity-side ground truth for this pass's three confirmed
    /// gaps in src/main.js, in the IDENTICAL "KEY=value" shape
    /// tools/_probe_mainrest.mjs prints from the real JS source — a plain
    /// `diff` (via tools/unity_port_check.py) between the two outputs IS the
    /// verification:
    ///   1. main.js:618-657  SPAWN_CITIES / chosenPlane / planeAllowed /
    ///      firstAllowedPlane  -> Core/SpawnCities.cs, Core/Boot.cs's new
    ///      PlaneAllowed/FirstAllowedPlane.
    ///   2. main.js:729-757  the 🎬 chip's dropdown + stop-button wiring ->
    ///      UI/Hud/CinemaMenuView.cs. The strings are read straight off the
    ///      LIVE compiled consts (reflection); the four behaviour facts are
    ///      proven by actually constructing a CinemaDirector +
    ///      CinemaMenuView and invoking the real (private) click handlers
    ///      via reflection — a genuine runtime call into the compiled code,
    ///      not a text echo of the JS side's own regex check.
    ///   3. main.js:132-139  updateBadge -> Core/Boot.cs's new
    ///      BadgeName/BadgeSub.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.MainRestProbe.Run
    /// </summary>
    public static class MainRestProbe
    {
        static string LcBool(bool b) => b ? "true" : "false"; // SKILL.md gotcha: lower-case on both sides

        static void Fact(string key, object value)
        {
            // Force invariant formatting for numbers — CurrentCulture on a
            // non-US machine prints "41,66" (comma decimal), which would
            // silently break tools/unity_port_check.py's numeric compare.
            string s = value switch
            {
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? "none",
            };
            Debug.Log($"{key}={s}");
        }

        public static void Run()
        {
            SpawnFacts();
            PlaneAllowedFacts();
            BadgeFacts();
            CinemaChipFacts();
        }

        // =====================================================================
        // ---- 1a. SPAWN_CITIES — main.js:639-657, Core/SpawnCities.cs -------
        // =====================================================================
        static void SpawnFacts()
        {
            Fact("SPAWN_COUNT", SpawnCities.All.Count);
            Fact("SPAWN_DEFAULT_ID", SpawnCities.Default);
            foreach (var kv in SpawnCities.All)
            {
                var p = kv.Value;
                Fact($"SPAWN_{kv.Key}_LAT", p.Lat);
                Fact($"SPAWN_{kv.Key}_LON", p.Lon);
                Fact($"SPAWN_{kv.Key}_YAW", p.YawRad.HasValue ? (object)p.YawRad.Value : "none");
            }
            var unknown = SpawnCities.Resolve("not-a-real-city"); // main.js:692 `?? SPAWN_CITIES.tbilisi`
            Fact("SPAWN_RESOLVE_UNKNOWN_LAT", unknown.Lat);
            Fact("SPAWN_RESOLVE_UNKNOWN_LON", unknown.Lon);
        }

        // =====================================================================
        // ---- 1b. planeAllowed / firstAllowedPlane — main.js:622-627, 663-664
        // =====================================================================
        static Boot NewBoot(string heroRace)
        {
            var go = new GameObject("ProbeBoot_" + (heroRace ?? "none"));
            var boot = go.AddComponent<Boot>();
            if (heroRace != null)
            {
                SetProp(boot, "Roster", new System.Collections.Generic.List<HeroRecord>
                {
                    new HeroRecord { Hid = "h0", Name = "გია", Race = heroRace, Model = "banana", Plane = "spitfire" },
                });
                SetProp(boot, "ActiveIndex", 0);
            }
            return boot;
        }

        static void SetProp(object obj, string name, object value)
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            prop.GetSetMethod(true).Invoke(obj, new[] { value });
        }

        static void PlaneAllowedFacts()
        {
            Fact("PLANE_COUNT", Planes.All.Length);

            foreach (var (tag, race) in new[] { ("NOHERO", (string)null), ("VEGGIE", "veggie"), ("TOYS", "toys") })
            {
                var boot = NewBoot(race);
                Fact($"{tag}_ALLOWED_SPITFIRE", LcBool(boot.PlaneAllowed("spitfire")));
                Fact($"{tag}_ALLOWED_B58", LcBool(boot.PlaneAllowed("b58")));
                Fact($"{tag}_FIRST_ALLOWED", boot.FirstAllowedPlane());
                UnityEngine.Object.DestroyImmediate(boot.gameObject);
            }

            // main.js:663-664, applied inside startFlight: a hero's STORED
            // plane that is not allowed for its race must fall back.
            var toysBoot = NewBoot("toys");
            Fact("STARTFLIGHT_FALLBACK_NEEDED_TOYS_SPITFIRE", LcBool(!toysBoot.PlaneAllowed("spitfire")));
            Fact("STARTFLIGHT_FALLBACK_RESULT_TOYS", toysBoot.FirstAllowedPlane());
            UnityEngine.Object.DestroyImmediate(toysBoot.gameObject);
        }

        // =====================================================================
        // ---- 3. updateBadge — main.js:132-139, Core/Boot.cs ----------------
        // =====================================================================
        static void BadgeFacts()
        {
            var user = new PublicUser { username = "testpilot", level = 7, stars = 3 };

            var noUserBoot = NewBoot(null);
            Fact("BADGE_NOUSER_NAME", noUserBoot.BadgeName ?? "none");
            Fact("BADGE_NOUSER_SUB", noUserBoot.BadgeSub ?? "none");
            UnityEngine.Object.DestroyImmediate(noUserBoot.gameObject);

            var noHeroBoot = NewBoot(null);
            SetProp(noHeroBoot, "CurrentUser", user);
            Fact("BADGE_NOHERO_NAME", noHeroBoot.BadgeName);
            Fact("BADGE_NOHERO_SUB", noHeroBoot.BadgeSub);
            UnityEngine.Object.DestroyImmediate(noHeroBoot.gameObject);

            var heroBoot = NewBoot("veggie");
            SetProp(heroBoot, "CurrentUser", user);
            Fact("BADGE_HERO_NAME", heroBoot.BadgeName);
            Fact("BADGE_HERO_SUB", heroBoot.BadgeSub);
            UnityEngine.Object.DestroyImmediate(heroBoot.gameObject);
        }

        // =====================================================================
        // ---- 2. the 🎬 chip — main.js:729-757, UI/Hud/CinemaMenuView.cs ----
        // =====================================================================
        static void CinemaChipFacts()
        {
            var t = typeof(CinemaMenuView);
            const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags privI = BindingFlags.NonPublic | BindingFlags.Instance;

            // ---- strings, read straight off the live compiled consts ------
            Fact("CINEMA_CHIP_TOOLTIP", (string)t.GetField("ChipTooltip", priv).GetValue(null));
            Fact("CINEMA_MENU_TITLE", (string)t.GetField("MenuTitle", priv).GetValue(null));
            var rows = (ValueTuple<string, string, string>[])t.GetField("Rows", priv).GetValue(null);
            for (int i = 0; i < rows.Length; i++)
            {
                Fact($"CINEMA_ROW_{i}_N", rows[i].Item1);
                Fact($"CINEMA_ROW_{i}_T", rows[i].Item2);
                Fact($"CINEMA_ROW_{i}_D", rows[i].Item3);
            }

            // ---- behaviour: construct the REAL objects and click them -----
            var cinema = new CinemaDirector();
            var view = new CinemaMenuView(cinema);
            var miChip = t.GetMethod("OnChipClicked", privI);
            var miRow = t.GetMethod("OnRowClicked", privI);
            var fiOpen = t.GetField("_open", privI);
            var fiMenu = t.GetField("_menu", privI);
            var menuEl = (VisualElement)fiMenu.GetValue(view);

            // main.js:739-745, menu closed + cinema idle -> opens.
            miChip.Invoke(view, new object[] { ClickEvent.GetPooled() });
            bool opened = (bool)fiOpen.GetValue(view) && menuEl.style.display == DisplayStyle.Flex;
            Fact("CHIP_OPENS_MENU_ON_CLICK", LcBool(opened));

            // main.js:746-751, a row click closes the menu AND starts that scene.
            miRow.Invoke(view, new object[] { ClickEvent.GetPooled(), 1 });
            bool closed = !(bool)fiOpen.GetValue(view) && menuEl.style.display == DisplayStyle.None;
            bool toggled = cinema.Active && cinema.SceneIndex == 1;
            Fact("ROW_CLICK_CLOSES_MENU", LcBool(closed));
            Fact("ROW_CLICK_TOGGLES_SCENE", LcBool(toggled));

            // main.js:741, chip click while a reel IS playing stops it instead.
            miChip.Invoke(view, new object[] { ClickEvent.GetPooled() });
            Fact("CHIP_STOPS_WHEN_ACTIVE", LcBool(!cinema.Active));
        }
    }
}
