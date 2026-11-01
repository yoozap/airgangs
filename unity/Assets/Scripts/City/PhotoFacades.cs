using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real-photograph building facades: Wikimedia Commons photo cuts of actual
    /// Tbilisi buildings, matched EXCLUSIVELY one photo per building.
    ///
    /// Ported from src/citymap.js:4066-4176 (manifest load + pbMaterial) and
    /// :4095-4143 (the exclusive assignment pass). The whole point of this file
    /// is that pass: matching every building within the radius put the same
    /// facade on up to a DOZEN neighbours (citymap.js's own measurement, before
    /// the fix: 1,532 buildings wearing 586 photos, 86% of them cloned) — the
    /// most visible kind of repetition in a dense block. Instead: score every
    /// (photo, building) pair inside the radius, take them cheapest-first, and
    /// let each side be claimed once.
    ///
    /// PURE DATA: no GameObject, no Instantiate, no Resources/AssetDatabase
    /// texture loading. This class decides WHICH building gets WHICH photo and
    /// how its UVs must be laid out; wiring that onto a Material/Texture2D and
    /// an actual submesh is the renderer's job (see the class doc at the bottom
    /// of this file for exactly what that needs from here).
    ///
    /// HANDEDNESS: the manifest holds real lat/lon. Positions here are produced
    /// with GeoFrame.FromLatLon (photos) and GeoFrame.ToUnity (building
    /// centroids, which CityData hands over in the GAME frame, matching
    /// BuildingFacts/BuildingBuilder's own convention) — each mirrored exactly
    /// ONCE, so both sides land in the same Unity frame. Squared distance is
    /// invariant under a single consistent axis mirror, so this reproduces the
    /// JS's radius test and grid buckets exactly; only the grid key's sign
    /// flips, and the 3x3 neighbour search is symmetric about that key, so the
    /// candidate set is identical.
    /// </summary>
    public static class PhotoFacades
    {
        // citymap.js:4082 — Math.round(p.x / 30), Math.round(p.z / 30)
        const float GridCell = 30f;
        // citymap.js:4123 — if (d < 28 * 28) pairs.push(...)
        const float Radius = 28f;
        const float RadiusSq = Radius * Radius;

        /// <summary>One reviewed Commons photo cut, straight off manifest.json.</summary>
        public class ManifestEntry
        {
            public string Key;     // the manifest's own object key ("b146245") == file stem
            public double Lat, Lon;
            public string File;    // textures/pb/<File>, e.g. "b146245.jpg"
            public int Bays = 1;   // horizontal window bays — texture repeat.x = 1/Bays
            public int Floors = 1; // storeys in the photo    — texture repeat.y = 1/Floors
            public string Credit;  // "David Osipov, CC BY 4.0, via Wikimedia Commons"
            public string Title;   // Commons file title, for CREDITS.txt cross-reference

            /// <summary>Unity-frame position (GeoFrame.FromLatLon — mirrored once).</summary>
            public Vector3 Pos;
        }

        /// <summary>
        /// The result of the exclusive assignment pass: which building (by its
        /// index into CityData.Buildings) won which photo, plus the attribution
        /// string and the full manifest for anyone building materials from it.
        /// </summary>
        public class Result
        {
            public List<ManifestEntry> Manifest = new List<ManifestEntry>();

            /// <summary>Building index (CityData.Buildings) -> its own photo. One entry
            /// per WINNING building; a building absent here lost the draw or was never
            /// in radius of any photo (citymap.js: `pbOf.get(bi) || null`).</summary>
            public Dictionary<int, ManifestEntry> AssignedByBuilding = new Dictionary<int, ManifestEntry>();

            /// <summary>Manifest keys actually claimed — size must equal
            /// AssignedByBuilding.Count (1:1, never more than once each).</summary>
            public HashSet<string> UsedPhotoKeys = new HashSet<string>();

            /// <summary>
            /// citymap.js:4078 — appended to the attribution string the moment the
            /// manifest loads, counting the WHOLE manifest (not just the buildings
            /// that go on to win a photo): a licence obligation, carried verbatim.
            /// " · &lt;N&gt; building photos by &lt;M&gt; Wikimedia Commons photographers
            /// (CC BY/BY-SA/CC0 — full list: textures/pb/CREDITS.txt)"
            /// </summary>
            public string Attribution;
            public int PhotographerCount;
        }

        /// <summary>
        /// Per-building UV/texture plan for a winning photo (citymap.js:4899-4989
        /// and :4166 the repeat.set call). `height` is the building's own real
        /// height in metres (BuildingFacts/BuildingBuilder already compute it).
        /// </summary>
        public struct UVPlan
        {
            /// <summary>u origin of the wall loop, in 3 m window-cell units. ALWAYS 0 for
            /// a photo wall — citymap.js:4899: "pb photos must anchor at the real
            /// corner: no shift" (every other wall style gets a random per-building
            /// offset here, photos never do).</summary>
            public float UAnchor;

            /// <summary>v of the buried bottom ring. ALWAYS 0 for a photo wall —
            /// citymap.js:4917: with repeat 1/floors a negative v wraps the photo's
            /// FAR edge (roofline/sky) onto the base band and smears it across the
            /// building's foot. Painted/glass walls keep -2/3; photo walls never do.</summary>
            public float VBottom;

            /// <summary>v of the top ring: h / 3 (citymap.js:4909).</summary>
            public float VTop;

            /// <summary>Texture.repeat.x — 1 / bays (citymap.js:4166).</summary>
            public float RepeatU;

            /// <summary>Texture.repeat.y — 1 / floors (citymap.js:4166).</summary>
            public float RepeatV;

            /// <summary>citymap.js:4989 pbRelief — a photo building with more than one
            /// storey and at least 5 m of height gets 3D window frames / AC boxes /
            /// drainpipes laid over the photo so it reads as a volume, not wallpaper.
            /// floors === 1 (shopfront strips) stay flat: their grid wouldn't align.</summary>
            public bool WantRelief;
        }

        public static UVPlan PlanUV(ManifestEntry m, float height)
        {
            return new UVPlan
            {
                UAnchor = 0f,
                VBottom = 0f,
                VTop = height / 3f,
                RepeatU = 1f / Mathf.Max(1, m.Bays),
                RepeatV = 1f / Mathf.Max(1, m.Floors),
                WantRelief = m.Floors > 1 && height >= 5f,
            };
        }

        /// <summary>citymap.js:4072 — fetch('textures/pb/manifest.json'). The Unity
        /// import lives under Assets/Resources/Art/Textures/pb (not StreamingAssets — these are
        /// imported Texture2D assets, loaded as Materials, not raw bytes).</summary>
        /// A PROJECT ASSET PATH, resolved through Core/ArtAssets — not
        /// Application.dataPath, which in a player is the .app's Data folder
        /// and holds no loose JSON at all ("PHOTOFACADES missing
        /// <app>/Contents/Art/Textures/pb/manifest.json" on the first real
        /// player run, i.e. no building won a photo facade).
        public const string DefaultManifestPath = "Assets/Resources/Art/Textures/pb/manifest.json";

        /// <summary>
        /// Loads manifest.json. Object keys ARE the building-photo key ("b146245"),
        /// matching the file stem — citymap.js:4081 `for (const [key, m] of
        /// Object.entries(man))`.
        /// </summary>
        public static List<ManifestEntry> LoadManifest(string path)
        {
            var list = new List<ManifestEntry>();
            string json = Core.ArtAssets.LoadText(path);
            if (json == null)
            {
                Debug.Log($"PHOTOFACADES missing {path} — no building will win a photo facade");
                return list;
            }

            var root = JObject.Parse(json);
            foreach (var prop in root.Properties())
            {
                var o = (JObject)prop.Value;
                var m = new ManifestEntry
                {
                    Key = prop.Name,
                    Lat = (double?)o["lat"] ?? 0.0,
                    Lon = (double?)o["lon"] ?? 0.0,
                    File = (string)o["file"] ?? $"{prop.Name}.jpg",
                    Bays = (int?)o["bays"] ?? 1,
                    Floors = (int?)o["floors"] ?? 1,
                    Credit = (string)o["credit"] ?? "",
                    Title = (string)o["title"] ?? "",
                };
                // citymap.js:4079-4080 — x: -(m.lon-origin.lon)*mlon, z: (m.lat-origin.lat)*mlat.
                // GeoFrame.FromLatLon computes exactly that pair, already in the Unity
                // frame (mirrored once) — do not negate again.
                m.Pos = GeoFrame.FromLatLon(m.Lat, m.Lon);
                list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// citymap.js:4074-4078 — the attribution clause, computed over the WHOLE
        /// manifest the instant it loads (not the subset that goes on to win the
        /// exclusive draw below). A licence obligation: carry it verbatim.
        /// </summary>
        public static string BuildAttribution(List<ManifestEntry> manifest, out int photographerCount)
        {
            var artists = new HashSet<string>();
            foreach (var m in manifest)
            {
                if (string.IsNullOrEmpty(m.Credit)) continue;
                // citymap.js:4076 — (m.credit || '').split(',')[0]; no trim in the JS.
                var first = m.Credit.Split(',')[0];
                if (!string.IsNullOrEmpty(first)) artists.Add(first);
            }
            photographerCount = artists.Count;
            return $" · {manifest.Count} building photos by {artists.Count} Wikimedia Commons photographers" +
                   " (CC BY/BY-SA/CC0 — full list: textures/pb/CREDITS.txt)";
        }

        /// <summary>One building footprint's centroid, as the assignment pass needs it —
        /// deliberately narrow so this file never depends on CityData.Footprint/BuildingKit
        /// internals beyond "a list of (x,z) in the game frame".</summary>
        public static Vector3 CentroidUnity(float[] flatGameXZ)
        {
            int n = flatGameXZ.Length / 2;
            float gx = 0f, gz = 0f;
            for (int i = 0; i < n; i++) { gx += flatGameXZ[i * 2]; gz += flatGameXZ[i * 2 + 1]; }
            gx /= n; gz /= n;
            // BuildingFacts/BuildingBuilder convention: CityData hands footprints over
            // in the GAME frame (X=west), unmirrored. Mirror ONCE here via GeoFrame.ToUnity
            // so this centroid lands in the same frame as ManifestEntry.Pos above.
            return GeoFrame.ToUnity(gx, 0f, gz);
        }

        /// <summary>
        /// The exclusive cheapest-first assignment — citymap.js:4095-4143.
        /// `buildings` is CityData.Buildings (or any list exposing the same flat
        /// game-frame footprint arrays); a building with fewer than 3 vertices is
        /// skipped exactly as the JS skips `n &lt; 3`.
        /// </summary>
        public static Result Assign(List<ManifestEntry> manifest, IReadOnlyList<float[]> buildingFootprints)
        {
            var result = new Result { Manifest = manifest };
            result.Attribution = BuildAttribution(manifest, out result.PhotographerCount);
            if (manifest.Count == 0) return result;

            // pbGrid: 30 m cell -> photos in it (citymap.js:4082-4085)
            var pbGrid = new Dictionary<long, List<ManifestEntry>>();
            foreach (var m in manifest)
            {
                long gk = CellKey(m.Pos.x, m.Pos.z);
                if (!pbGrid.TryGetValue(gk, out var list)) pbGrid[gk] = list = new List<ManifestEntry>();
                list.Add(m);
            }

            // bGrid: 30 m cell -> building indices, from each footprint's own centroid
            // (citymap.js:4106-4115 — skips n < 3, exactly as BuildingBuilder does).
            var bGrid = new Dictionary<long, List<int>>();
            var centroids = new Vector3[buildingFootprints.Count];
            for (int bi = 0; bi < buildingFootprints.Count; bi++)
            {
                var flat = buildingFootprints[bi];
                if (flat == null || flat.Length < 6) continue; // n < 3
                var c = CentroidUnity(flat);
                centroids[bi] = c;
                long gk = CellKey(c.x, c.z);
                if (!bGrid.TryGetValue(gk, out var list)) bGrid[gk] = list = new List<int>();
                list.Add(bi);
            }

            // pairs: every (photo, building) within RADIUS, 3x3 neighbour cells
            // (citymap.js:4117-4131).
            var pairs = new List<(float d2, int bi, ManifestEntry m)>();
            foreach (var kv in pbGrid)
            {
                foreach (var p in kv.Value)
                {
                    int gi = Mathf.RoundToInt(p.Pos.x / GridCell);
                    int gj = Mathf.RoundToInt(p.Pos.z / GridCell);
                    for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        if (!bGrid.TryGetValue(Key(gi + di, gj + dj), out var list)) continue;
                        foreach (int bi in list)
                        {
                            var c = centroids[bi];
                            float dx = p.Pos.x - c.x, dz = p.Pos.z - c.z;
                            float d2 = dx * dx + dz * dz;
                            if (d2 < RadiusSq) pairs.Add((d2, bi, p));
                        }
                    }
                }
            }

            // cheapest-first, each side claimed once (citymap.js:4132-4143).
            pairs.Sort((a, b) => a.d2.CompareTo(b.d2));
            var usedBuilding = new HashSet<int>();
            foreach (var (_, bi, m) in pairs)
            {
                if (usedBuilding.Contains(bi) || result.UsedPhotoKeys.Contains(m.Key)) continue;
                result.AssignedByBuilding[bi] = m;
                usedBuilding.Add(bi);
                result.UsedPhotoKeys.Add(m.Key);
            }

            Debug.Log($"PHOTOFACADES manifest={manifest.Count} buildings={result.AssignedByBuilding.Count} " +
                      $"photosUsed={result.UsedPhotoKeys.Count} photographers={result.PhotographerCount}");
            return result;
        }

        static long Key(int gi, int gj) => (long)gi * 100003 + gj;
        static long CellKey(float x, float z) => Key(Mathf.RoundToInt(x / GridCell), Mathf.RoundToInt(z / GridCell));
    }

    // ------------------------------------------------------------------------
    // WHAT CityMaterials / CityRenderer NEED FROM THIS FILE TO ACTUALLY DRAW A
    // PHOTO FACADE (docs/QUALITY_BAR.md's "DECIDED vs RENDERED" split — this
    // class only decides):
    //
    //   1. Call PhotoFacades.LoadManifest + Assign ONCE at city-build time, with
    //      CityData.Buildings[i].Flat as buildingFootprints[i].
    //   2. For each building index bi, check Result.AssignedByBuilding.TryGetValue.
    //      BuildingBuilder.BuildRich currently builds every wall "as if pb were
    //      null" (its own doc comment, citymap.js:4729-4746 is the `pb` branch it
    //      deliberately skips) — a winning building needs its wall quads routed to
    //      a PER-PHOTO submesh/material instead of its wallBucket, using
    //      PlanUV(photo, height) for the UVs instead of the us[]/vBot/vTop it
    //      computes today.
    //   3. One Material per distinct ManifestEntry.Key (citymap.js:4145-4175
    //      pbMats/pbMaterial — textures are shared by FILE, not duplicated per
    //      building), each sampling Assets/Resources/Art/Textures/pb/<File> with
    //      wrapMode=Repeat and the UVPlan's RepeatU/RepeatV as the material's
    //      tiling (there is no texture atlas here — 609 separate small JPGs).
    //   4. Splice Result.Attribution into whatever string the Unity build shows
    //      for licence credit (the JS keeps one running `this.attribution` the
    //      whole load appends to).
    // ------------------------------------------------------------------------
}
