using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real OSM geography on two levels: 10 raioni (districts.json) and 112
    /// neighbourhood polygons (subzones.json) inside them. Ported from
    /// src/zones.js. The query layer (<see cref="ZoneAt"/>) and the WoW-style
    /// zone-slam DECISION logic (<see cref="ZoneTracker"/>, src/zones.js:
    /// 90-117 minus the DOM) are both here. Only the actual DOM/HUD element
    /// creation (src/zones.js:19-31: the zone-label div, the zone-announce
    /// div, its CSS "show" class + 5200 ms timer) is NOT ported — that is a
    /// real UI concern, out of this port's City/ scope; a UI-owning script
    /// should call <see cref="ZoneTracker.Tick"/> every frame and render its result.
    ///
    /// GOTCHA (see memory "Zones/boundary/pins 2026-08"): raions are OSM
    /// admin_level=10 specifically. That filter already happened upstream, in
    /// the Python extractor that wrote districts.json — by the time the JSON
    /// lands here every ring is already a raion, so there is no admin_level
    /// field to check in this file (src/zones.js never checks one either).
    ///
    /// HANDEDNESS: ring points are lat/lon, converted with GeoFrame.FromLatLon,
    /// which already returns UNITY (x=east) coordinates — so, unlike a
    /// hand-tuned yaw constant, there is no separate mirror step to apply here.
    /// The even-odd point-in-ring test (_inRing) is a topological test and
    /// gives the same answer whether x is mirrored or not, as long as every
    /// point used in one test came from the same conversion (they do).
    /// </summary>
    public class ZoneBuilder
    {
        public struct Zone
        {
            public string Name, Tag;
            public List<Vector2[]> Rings; // Unity (x,z) pairs, per src/zones.js:42-46
            public float MinX, MaxX, MinZ, MaxZ; // bbox, src/zones.js:47-57
        }

        /// <summary>563-park-style "most specific first" rule: subzones.json is
        /// pre-sorted area-ascending by the Python extractor; src/zones.js never
        /// re-sorts it either (zones.js:15 comment), so neither do we.</summary>
        public readonly List<Zone> Districts = new List<Zone>(); // src/zones.js:13 — 10 raioni
        public readonly List<Zone> Subzones = new List<Zone>();  // src/zones.js:14 — 112 neighbourhoods

        /// <summary>src/zones.js:8 — shown when (x,z) falls outside every ring.</summary>
        public static readonly (string Name, string Tag) Outskirts =
            ("თბილისის შემოგარენი", "OUTSKIRTS // GEORGIA");

        public static ZoneBuilder Load(string streamingRoot = null)
        {
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            var z = new ZoneBuilder();
            ReadInto(Path.Combine(streamingRoot, "districts.json"), z.Districts);
            ReadInto(Path.Combine(streamingRoot, "subzones.json"), z.Subzones);
            Debug.Log($"ZONES districts={z.Districts.Count} subzones={z.Subzones.Count}");
            return z;
        }

        // src/zones.js:41-58 toWorld
        static void ReadInto(string path, List<Zone> into)
        {
            if (!File.Exists(path)) { Debug.LogWarning($"ZONES missing {path}"); return; }
            var arr = JArray.Parse(File.ReadAllText(path));
            foreach (var d in arr)
            {
                var rings = new List<Vector2[]>();
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var ringTok in (JArray)d["rings"])
                {
                    var ringArr = (JArray)ringTok;
                    var pts = new Vector2[ringArr.Count];
                    for (int i = 0; i < ringArr.Count; i++)
                    {
                        var pt = (JArray)ringArr[i];
                        double lat = pt[0].Value<double>();
                        double lon = pt[1].Value<double>();
                        Vector3 p = GeoFrame.FromLatLon(lat, lon);
                        pts[i] = new Vector2(p.x, p.z);
                        if (p.x < minX) minX = p.x;
                        if (p.x > maxX) maxX = p.x;
                        if (p.z < minZ) minZ = p.z;
                        if (p.z > maxZ) maxZ = p.z;
                    }
                    rings.Add(pts);
                }
                into.Add(new Zone
                {
                    Name = (string)d["name"],
                    Tag = (string)d["tag"],
                    Rings = rings,
                    MinX = minX, MaxX = maxX, MinZ = minZ, MaxZ = maxZ,
                });
            }
        }

        // src/zones.js:65-73 _inRing — even-odd ray cast, frame-agnostic.
        static bool InRing(Vector2[] ring, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                float xi = ring[i].x, zi = ring[i].y;
                float xj = ring[j].x, zj = ring[j].y;
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        // src/zones.js:75-82 _hit — bbox reject, then first ring match wins.
        static Zone? Hit(List<Zone> list, float x, float z)
        {
            for (int k = 0; k < list.Count; k++)
            {
                var d = list[k];
                if (x < d.MinX || x > d.MaxX || z < d.MinZ || z > d.MaxZ) continue;
                for (int r = 0; r < d.Rings.Count; r++)
                    if (InRing(d.Rings[r], x, z)) return d;
            }
            return null;
        }

        /// <summary>
        /// src/zones.js:85-88 zoneAt — the smallest neighbourhood and the raion
        /// containing a UNITY-space (x, z). Either half may be null (no match).
        /// </summary>
        public (Zone? Sub, Zone? Dist) ZoneAt(float x, float z)
            => (Hit(Subzones, x, z), Hit(Districts, x, z));

        /// <summary>
        /// Port of src/zones.js:90-117 update()/_announce() — the WoW-style
        /// zone-slam DECISION logic, minus the DOM (zone-label/zone-announce
        /// divs, the CSS "show" class restart, the 5200 ms setTimeout). Those
        /// are genuinely a UI concern (this port's UI/ folder, out of this
        /// file's scope — see the class header), but the STATE MACHINE that
        /// decides WHEN to slam and WHAT text to show is real behaviour, not
        /// DOM wiring, so it belongs here with the rest of the query layer. A
        /// UI-owning script should hold one <see cref="ZoneTracker"/> per
        /// local player, call <see cref="ZoneTracker.Tick"/> every frame, and
        /// render <see cref="AnnounceName"/>/<see cref="AnnounceTag"/> when it
        /// returns true (plus keep <see cref="ZoneTracker.Current"/>'s name on
        /// the persistent "where am I" label every frame, announce or not —
        /// zones.js:104 writes <c>this.label.textContent</c> unconditionally).
        /// </summary>
        public class ZoneTracker
        {
            /// <summary>Most specific zone, or null for the outskirts.
            /// Mirrors zones.js:15's `this.current` — see <see cref="HasCurrent"/>
            /// for the JS's separate "undefined" (never updated) state.</summary>
            public Zone? Current { get; private set; }

            /// <summary>false exactly once, before the first <see cref="Tick"/> —
            /// the JS's `this.current === undefined` (zones.js:15,98-99), kept
            /// distinct from Current being null (= resolved to the outskirts).</summary>
            public bool HasCurrent { get; private set; }

            /// <summary>The raion (district) containing the player, for a map
            /// highlight — zones.js:95 `this.currentDistrict`. Updated every
            /// Tick regardless of whether an announce fires.</summary>
            public Zone? CurrentDistrict { get; private set; }

            /// <summary>The raw subzone hit this Tick, independent of <see
            /// cref="Current"/> (which falls back to the district when there
            /// is no subzone match) — a UI-owning script wiring the existing
            /// (subzone, district) event contract needs this half too.</summary>
            public Zone? Sub { get; private set; }

            public string AnnounceName { get; private set; }
            public string AnnounceTag { get; private set; }

            /// <summary>
            /// src/zones.js:90-107. Returns true exactly when <see cref="Current"/>
            /// changed since the last call AND it is not the one suppressed
            /// case: entering the outskirts on the very first Tick (a spawn
            /// glitch, zones.js:105 — "isn't worth a slam"). The persistent
            /// label text is always <c>(Current?.Name ?? ZoneBuilder.Outskirts.Name)</c>
            /// after this returns, announce or not.
            /// </summary>
            public bool Tick(ZoneBuilder zones, float x, float z)
            {
                if (zones?.Districts == null) return false; // zones.js:86 — still loading
                var (sub, dist) = zones.ZoneAt(x, z);
                Zone? specific = sub ?? dist;
                CurrentDistrict = dist;
                Sub = sub;

                string name = specific?.Name;
                string curName = HasCurrent ? Current?.Name : null;
                // zones.js:98 — same specific zone as last Tick: no change, no announce.
                if (HasCurrent && name == curName) return false;

                bool first = !HasCurrent;
                Current = specific;
                HasCurrent = true;

                if (specific != null)
                {
                    AnnounceName = specific.Value.Name;
                    // zones.js:102 — sub present: "<sub.tag> // <dist.tag ?? TBILISI>";
                    // sub absent (specific IS the district): "<dist.tag> // TBILISI".
                    AnnounceTag = sub != null
                        ? $"{sub.Value.Tag} // {(dist?.Tag ?? "TBILISI")}"
                        : $"{dist.Value.Tag} // TBILISI";
                }
                else
                {
                    AnnounceName = Outskirts.Name;
                    AnnounceTag = Outskirts.Tag;
                }

                // zones.js:106 — "Entering the outskirts right at start (spawn
                // glitch) isn't worth a slam."
                bool suppressed = first && specific == null;
                return !suppressed;
            }
        }
    }
}
