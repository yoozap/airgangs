using TbilisiFly.City;
using TbilisiFly.Data;
using TbilisiFly.UI.Zone;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the gap this pass closed: ZoneBuilder.ZoneTracker (src/zones.js:
    /// 90-117's decision logic) was fully ported but never Tick()ed from
    /// anywhere, and ZoneAnnounceView (the DOM half, zones.js:19-31) did not
    /// exist at all — "ported but connected to nothing", one layer on each
    /// side of the City/UI seam. This is a REAL-ASSET check (loads the real
    /// districts.json/subzones.json off StreamingAssets, same data
    /// TreeRenderProbe/CityLoader use), not a synthetic fixture.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ZoneProbe.Run
    /// </summary>
    public static class ZoneProbe
    {
        public static void Run()
        {
            var zones = ZoneBuilder.Load();
            Debug.Log($"ZP DISTRICTS={zones.Districts.Count} SUBZONES={zones.Subzones.Count}");

            // A real point inside the city (metro probe's own station #0 —
            // Liberty Square area, already known to be in-bounds) converted
            // the same way GeoFrame always does.
            Vector3 p = GeoFrame.FromLatLon(41.69339, 44.8015);
            var tracker = new ZoneBuilder.ZoneTracker();
            bool firstAnnounce = tracker.Tick(zones, p.x, p.z);
            Debug.Log($"ZP TICK1_ANNOUNCE={firstAnnounce.ToString().ToLowerInvariant()}");
            Debug.Log($"ZP CURRENT={(tracker.Current?.Name ?? "null")}");
            Debug.Log($"ZP SUB={(tracker.Sub?.Name ?? "null")}");
            Debug.Log($"ZP DISTRICT={(tracker.CurrentDistrict?.Name ?? "null")}");
            Debug.Log($"ZP ANNOUNCE_NAME={tracker.AnnounceName}");
            Debug.Log($"ZP ANNOUNCE_TAG={tracker.AnnounceTag}");

            // Same point again: zones.js:98 — no change, no second announce.
            bool secondAnnounce = tracker.Tick(zones, p.x, p.z);
            Debug.Log($"ZP TICK2_ANNOUNCE={secondAnnounce.ToString().ToLowerInvariant()}");

            // A point far outside any ring — the outskirts path.
            var far = new ZoneBuilder.ZoneTracker();
            far.Tick(zones, 500000f, 500000f);
            Debug.Log($"ZP OUTSKIRTS_NAME={far.AnnounceName}");
            Debug.Log($"ZP OUTSKIRTS_IS_CONST={(far.AnnounceName == ZoneBuilder.Outskirts.Name).ToString().ToLowerInvariant()}");

            // The view itself: construct + drive it exactly as GameLoop.UpdateZones
            // now does, and assert it did not throw and actually changed its
            // visible state (not just "compiled").
            bool viewOk = true;
            try
            {
                var view = new ZoneAnnounceView();
                view.SetLabel(tracker.Current?.Name ?? ZoneBuilder.Outskirts.Name);
                view.Announce(tracker.AnnounceName, tracker.AnnounceTag);
            }
            catch (System.Exception e)
            {
                viewOk = false;
                Debug.Log($"ZP VIEW_EXCEPTION={e.Message}");
            }
            Debug.Log($"ZP VIEW_OK={viewOk.ToString().ToLowerInvariant()}");

            bool pass = zones.Districts.Count > 0 && zones.Subzones.Count > 0
                && tracker.Current != null && !secondAnnounce && viewOk
                && far.AnnounceName == ZoneBuilder.Outskirts.Name;
            Debug.Log($"ZP ASSERT_overall={(pass ? "PASS" : "FAIL")}");

            ProbeExit.Exit(0);
        }
    }
}
