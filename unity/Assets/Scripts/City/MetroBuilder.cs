using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// The real Tbilisi metro: 23 stations at their OSM entrance positions,
    /// Blender pavilions (tools/build_metro_assets.py — grand-arch type for the
    /// old downtown stations, glass-hall elsewhere), 5 hand-placed hero
    /// buildings (satellite-fit), stair portals + M totems at every mapped
    /// entrance, and a name plate (Georgian + English) on every pavilion
    /// fascia. Ported from src/metro.js.
    ///
    /// ASSET GAP (report this, don't hide it): the pavilion models only exist
    /// as GLB at public/models/metro/*.glb and models_raw/metro/*.glb — they
    /// have NOT been converted to FBX and copied into unity/Assets/Resources/Art/ the
    /// way Assets/Resources/Art/Parks and Assets/Resources/Art/Props/malls.fbx already were. That
    /// asset-pipeline step (tools/glb_batch_fbx.py models_raw/metro ->
    /// Assets/Resources/Art/Metro) is outside this file's scope (new files only under
    /// Scripts/City/, no binary assets) and is NOT done here. Everything in
    /// this class that does not need a mesh — station/entrance/totem COUNTS,
    /// POSITIONS and YAWS — is fully computed and verifiable right now; the
    /// Kind string on each <see cref="Placement"/> is the asset key
    /// ("pavilion_arch","entrance","totem",...) a later pass resolves once the
    /// FBX lands at the conventional Assets/Resources/Art/Metro/&lt;key&gt;.fbx path.
    ///
    /// Pure data class: no GameObject/Instantiate (see TerrainBuilder's style).
    ///
    /// HANDEDNESS: every position is produced via GeoFrame.ToUnity and every
    /// yaw that will spin a placed prop via GeoFrame.YawToUnity — including
    /// metro.json's own 23 stored "yaw" values (GeoFrame.cs's own gotcha list
    /// names this file) and the 5 hand-placed hero positions'
    /// SQR_POS/MARJ_POS/AVLA_POS/RUSTAVELI_POS/GALLERIA_POS yaw components.
    /// Several offsets below (the totem beside a pavilion door, the entrance
    /// yaw from atan2(s.x-ex, s.z-ez), the name board) are GAME-FRAME formulas
    /// that must be evaluated with the UN-negated game yaw/positions first —
    /// mirroring happens once, at the very end, on the finished (x,z) pair and
    /// on the yaw that will be applied as a rotation. Negating early would
    /// offset the totem to the wrong side of the door.
    /// </summary>
    public class MetroBuilder
    {
        public struct Placement
        {
            public string Kind;      // asset key: pavilion_arch, pavilion_hall, entrance,
                                      // totem, galleria, rustaveli, marjanishvili, avlabari,
                                      // station_square, name_board
            public Vector3 Position; // Unity space
            public float YawUnity;   // radians
            public string Ka, En;    // station names carried on fascia/plate placements ("" otherwise)
        }

        public readonly List<Placement> Placements = new List<Placement>();
        public int StationCount { get; private set; }
        public int EntranceCount { get; private set; }
        public int TotemCount { get; private set; }
        public int PortalOnlyCount { get; private set; } // pav === "none": real OSM building already on the map

        // src/metro.js:125-133 — hero-station placements (satellite-fit; GAME frame [x, z, yaw])
        static readonly (float x, float z, float yaw) SqrPos = (-4324.2f, -295.4f, 1.22f);
        static readonly (float x, float z, float yaw) MarjPos = (-4249.3f, -1733.8f, 2.4f);
        static readonly (float x, float z, float yaw) AvlaPos = (-5827.8f, -3658.6f, 2.4f);
        static readonly (float x, float z, float yaw) RustaveliPos = (-3653.6f, -2334.6f, -0.96f);
        static readonly (float x, float z, float yaw) GalleriaPos = (-4500.7f, -3330.7f, -1.13f);

        public static MetroBuilder Build(string streamingRoot = null, TerrainSampler terrain = null)
        {
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            string path = Path.Combine(streamingRoot, "metro.json");
            var m = new MetroBuilder();
            if (!File.Exists(path)) { Debug.LogWarning($"METRO missing {path}"); return m; }

            var root = JObject.Parse(File.ReadAllText(path));
            var stations = (JArray)root["stations"];
            if (stations == null) return m;

            float H(float x, float z) => terrain != null ? terrain.HeightAtGame(x, z) : 0f;

            foreach (var st in stations)
            {
                m.StationCount++;
                float sx = st["x"].Value<float>();
                float sz = st["z"].Value<float>();
                float syaw = st["yaw"]?.Value<float>() ?? 0f;
                string ka = (string)st["ka"] ?? "";
                string en = (string)st["en"] ?? "";
                string pav = (string)st["pav"] ?? "";
                var entrances = (JArray)st["entrances"] ?? new JArray();

                void AddEntrancePortalsOnly()
                {
                    foreach (var ent in entrances)
                    {
                        float ex = ((JArray)ent)[0].Value<float>();
                        float ez = ((JArray)ent)[1].Value<float>();
                        float eyaw = Mathf.Atan2(sx - ex, sz - ez); // src/metro.js:216 etc.
                        m.Placements.Add(new Placement
                        {
                            Kind = "entrance",
                            Position = GeoFrame.ToUnity(ex, H(ex, ez), ez),
                            YawUnity = GeoFrame.YawToUnity(eyaw),
                        });
                        m.EntranceCount++;
                    }
                }

                // src/metro.js:204-237 — the 5 hand-placed hero stations
                if (en == "Liberty Square")
                {
                    m.Placements.Add(Hero("galleria", GalleriaPos, H, ka, en));
                    AddEntrancePortalsOnly();
                    continue;
                }
                if (en == "Marjanishvili")
                {
                    m.Placements.Add(Hero("marjanishvili", MarjPos, H, string.IsNullOrEmpty(ka) ? "მარჯანიშვილი" : ka, en));
                    m.Placements.Add(Totem(MarjPos.x + 13f, MarjPos.z, MarjPos.yaw, H));
                    m.TotemCount++;
                    AddEntrancePortalsOnly();
                    continue;
                }
                if (en == "Avlabari")
                {
                    m.Placements.Add(Hero("avlabari", AvlaPos, H, string.IsNullOrEmpty(ka) ? "ავლაბარი" : ka, en));
                    m.Placements.Add(Totem(AvlaPos.x + 12f, AvlaPos.z, AvlaPos.yaw, H));
                    m.TotemCount++;
                    AddEntrancePortalsOnly();
                    continue;
                }
                if (en == "Station Square-1" || en == "Station Square")
                {
                    m.Placements.Add(Hero("station_square", SqrPos, H, string.IsNullOrEmpty(ka) ? "სადგურის მოედანი" : ka, en));
                    m.Placements.Add(Totem(SqrPos.x + 30f, SqrPos.z - 6f, SqrPos.yaw, H));
                    m.TotemCount++;
                    AddEntrancePortalsOnly();
                    continue;
                }
                if (en == "Rustaveli")
                {
                    m.Placements.Add(Hero("rustaveli", RustaveliPos, H, string.IsNullOrEmpty(ka) ? "რუსთაველი" : ka, en));
                    m.Placements.Add(Totem(RustaveliPos.x - 16f, RustaveliPos.z - 6f, RustaveliPos.yaw, H));
                    m.TotemCount++;
                    AddEntrancePortalsOnly();
                    continue;
                }

                // src/metro.js:281-304 — generic stations
                if (pav == "none")
                {
                    // the real station building is already on the OSM map; just the
                    // stair portal, name board, M totem and mapped entrances
                    m.Placements.Add(new Placement
                    {
                        Kind = "entrance",
                        Position = GeoFrame.ToUnity(sx, H(sx, sz), sz),
                        YawUnity = GeoFrame.YawToUnity(syaw),
                    });
                    m.PortalOnlyCount++;
                    float bx = sx + Mathf.Sin(syaw) * 4.5f;
                    float bz = sz + Mathf.Cos(syaw) * 4.5f;
                    m.Placements.Add(new Placement
                    {
                        Kind = "name_board",
                        Position = GeoFrame.ToUnity(bx, H(bx, bz) + 2.6f, bz),
                        YawUnity = GeoFrame.YawToUnity(syaw),
                        Ka = ka, En = en,
                    });
                }
                else
                {
                    string kind = pav == "arch" ? "pavilion_arch" : "pavilion_hall";
                    m.Placements.Add(new Placement
                    {
                        Kind = kind,
                        Position = GeoFrame.ToUnity(sx, H(sx, sz), sz),
                        YawUnity = GeoFrame.YawToUnity(syaw),
                        Ka = ka, En = en,
                    });
                }

                // M totem beside the pavilion door — shared by both branches above
                m.Placements.Add(Totem(sx + Mathf.Cos(syaw) * 8f, sz - Mathf.Sin(syaw) * 8f, syaw, H));
                m.TotemCount++;

                // stair portals + totems at every other mapped entrance
                foreach (var ent in entrances)
                {
                    float ex = ((JArray)ent)[0].Value<float>();
                    float ez = ((JArray)ent)[1].Value<float>();
                    float eyaw = Mathf.Atan2(sx - ex, sz - ez);
                    m.Placements.Add(new Placement
                    {
                        Kind = "entrance",
                        Position = GeoFrame.ToUnity(ex, H(ex, ez), ez),
                        YawUnity = GeoFrame.YawToUnity(eyaw),
                    });
                    m.EntranceCount++;
                    m.Placements.Add(Totem(ex + Mathf.Cos(eyaw) * 3.4f, ez - Mathf.Sin(eyaw) * 3.4f, eyaw, H));
                    m.TotemCount++;
                }
            }

            Debug.Log($"METRO stations={m.StationCount} entrances={m.EntranceCount} " +
                      $"totems={m.TotemCount} portalOnly={m.PortalOnlyCount} placements={m.Placements.Count}");
            return m;
        }

        static Placement Hero(string kind, (float x, float z, float yaw) pos, Func<float, float, float> h, string ka, string en)
            => new Placement
            {
                Kind = kind,
                Position = GeoFrame.ToUnity(pos.x, h(pos.x, pos.z), pos.z),
                YawUnity = GeoFrame.YawToUnity(pos.yaw),
                Ka = ka, En = en,
            };

        static Placement Totem(float x, float z, float yawGame, Func<float, float, float> h)
            => new Placement
            {
                Kind = "totem",
                Position = GeoFrame.ToUnity(x, h(x, z), z),
                YawUnity = GeoFrame.YawToUnity(yawGame),
            };
    }
}
