using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/landmarks.js (1669 lines): real Tbilisi/Georgia landmarks and
    /// statues at their true OSM coordinates, real footprint-derived yaw, real
    /// world size.
    ///
    /// SCOPE AND WHAT "PLACE" MEANS HERE. The modelled landmark assets already
    /// exist — 13 OBJ under Assets/Resources/Art/Landmarks (exported once from these same
    /// JS builder functions by tools/export-obj.mjs, see landmarks.js:987) plus
    /// justice_house.fbx under Assets/Resources/Art/Props, and 19 statue FBX under
    /// Assets/Resources/Art/Statues. This file is DATA: lat/lon, yaw (JS convention,
    /// radians), scale and which asset goes where. It does not load assets or
    /// touch the scene — the same split BuildingBuilder/TerrainBuilder use with
    /// BuildCityScene.cs. A caller with an AssetDatabase (the editor bake step)
    /// or a runtime loader turns <see cref="BuiltLandmarks"/>/<see cref="HeroStatues"/>
    /// into GameObjects by looking up <c>AssetPath</c>.
    ///
    /// 10 of the 31 named landmarks placed by the JS (narikala, the funicular
    /// station, metekhi, svetitskhoveli, jvari, the baths, marriott, biltmore,
    /// sheraton metekhi, radisson red) were NEVER exported to OBJ — the JS
    /// itself builds them from box()/cyl() primitives, same as it builds every
    /// building wall. Those are ported here as real merged meshes
    /// (<see cref="BuildProcedural"/>), faithfully reproducing the JS's own
    /// primitive massing (not a placeholder box — the actual silhouette),
    /// INCLUDING real per-face/per-side UVs (box faces 0..1, cylinder sides
    /// wrap the full circumference, caps are a radial fan, hemisphere is
    /// equirectangular) — added so the 8 of these 10 landmarks that
    /// landmarks.js textures with a real Commons photo (photoMat(), see
    /// <see cref="LandmarkFacades"/>) can actually show one. Before this pass
    /// these meshes had NO texture coordinates at all (Buf only carried V/N/T),
    /// so even a correctly-assigned photo material would have sampled one
    /// texel for the whole surface — see LandmarkFacades.cs's header for the
    /// fallback-tint -> facade-tile mapping and the one seam left (the actual
    /// Texture2D load + Material.mainTextureScale assignment, a GameObject/
    /// rendering-layer job CityLoader.cs's own comment already defers:
    /// "mesh/tint pairs — consumed by a rendering layer, not stored here").
    ///
    /// HANDEDNESS. Every primitive below is authored in GAME-FRAME local
    /// coordinates (X=west), literally the JS numbers, including JS's own
    /// local rotation.y / rotation.x values UNNEGATED. Mirroring happens ONCE,
    /// at the end of each builder (<see cref="MakeMesh"/>: negate X, then
    /// GeoFrame.FlipWinding) — exactly BuildingBuilder's "one flip for the
    /// whole chunk, since every vertex was mirrored". World placement then uses
    /// GeoFrame.FromLatLon (already-mirrored) and GeoFrame.YawToUnity (negates
    /// the JS yaw) like every other hand-tuned constant in this port.
    /// </summary>
    public static class LandmarkPlacer
    {
        // =====================================================================
        // Exclusion rings — src/landmarks.js:9-42. Real landmarks sit on real
        // OSM footprints; whatever generates ordinary extruded buildings
        // (BuildingBuilder.cs) should skip a footprint whose centroid falls
        // inside one of these circles, or a generated grey box doubles up with
        // the real model standing on the same spot. Not consumed here — just
        // the data, for whoever extends BuildingBuilder with the exclusion.
        // =====================================================================
        public struct Exclusion { public double Lat, Lon; public float R; public string Note; }

        public static readonly Exclusion[] Exclusions =
        {
            new() { Lat = 41.69575,  Lon = 44.78499,  R = 45,  Note = "TV tower (way 437940450)" },
            new() { Lat = 41.69754,  Lon = 44.81654,  R = 150, Note = "Sameba cathedral + walled park (way 585352693)" },
            new() { Lat = 41.70392,  Lon = 44.79369,  R = 60,  Note = "Radisson Iveria (way 1299930496)" },
            new() { Lat = 41.77056,  Lon = 44.81026,  R = 70,  Note = "Chronicle of Georgia (way 707446533)" },
            new() { Lat = 41.65596,  Lon = 41.63934,  R = 30,  Note = "Alphabet Tower (way 405852444)" },
            new() { Lat = 41.6541,   Lon = 41.63674,  R = 35,  Note = "Ferris wheel (node 6822566652)" },
            new() { Lat = 41.70112,  Lon = 44.79626,  R = 50,  Note = "Opera House (way 59151604)" },
            new() { Lat = 41.69667,  Lon = 44.79805,  R = 100, Note = "Parliament complex (OSM rel 1020718)" },
            new() { Lat = 41.72285,  Lon = 44.78966,  R = 120, Note = "Dinamo Arena (rel 17823425)" },
            new() { Lat = 41.70747,  Lon = 44.78555,  R = 45,  Note = "Concert Hall (OSM rel 18129586)" },
            new() { Lat = 41.68787,  Lon = 44.80894,  R = 90,  Note = "Narikala fortress (way 9820020)" },
            new() { Lat = 41.69489,  Lon = 44.78652,  R = 30,  Note = "Funicular upper station (way 25699467)" },
            new() { Lat = 41.69015,  Lon = 44.8113,   R = 35,  Note = "Metekhi church (way 1064270003)" },
            new() { Lat = 41.6876,   Lon = 44.8111,   R = 70,  Note = "Abanotubani sulfur baths" },
            new() { Lat = 41.69916,  Lon = 44.7983,   R = 45,  Note = "Tbilisi Marriott (way 7108262)" },
            new() { Lat = 41.70245,  Lon = 44.79467,  R = 50,  Note = "Biltmore tower (way 19590999)" },
            new() { Lat = 41.68885,  Lon = 44.82317,  R = 65,  Note = "Sheraton Metechi Palace (way 20990671)" },
            new() { Lat = 41.70525,  Lon = 44.80045,  R = 25,  Note = "Radisson RED (node 11358652556)" },
            new() { Lat = 41.83838,  Lon = 44.73355,  R = 45,  Note = "Jvari monastery hilltop (way 1497018777)" },
            new() { Lat = 41.699024, Lon = 44.806656, R = 80,  Note = "Public Service Hall (way 1066392731)" },
            new() { Lat = 41.69339,  Lon = 44.8015,   R = 22,  Note = "Freedom Monument circle (way 1018836829)" },
            new() { Lat = 41.69032,  Lon = 44.81073,  R = 14,  Note = "Gorgasali statue footprint (way 1062990549)" },
            new() { Lat = 41.68812,  Lon = 44.8046,   R = 14,  Note = "Kartlis Deda footprint (way 1063062536)" },
            new() { Lat = 41.70389,  Lon = 44.78993,  R = 12,  Note = "Rustaveli monument footprint (way 1066637619)" },
            new() { Lat = 41.69775,  Lon = 44.79818,  R = 10,  Note = "writers pair footprint (way 1066626116)" },
            new() { Lat = 41.69525,  Lon = 44.8,      R = 62,  Note = "Galleria Tbilisi (real model in metro.js)" },
            new() { Lat = 41.70418,  Lon = 44.78971,  R = 26,  Note = "Rustaveli metro pavilion (real model in metro.js)" },
            new() { Lat = 41.72254,  Lon = 44.79777,  R = 46,  Note = "Station Square terminal (real model in metro.js)" },
            new() { Lat = 41.669742, Lon = 44.963939, R = 150, Note = "TBS main terminal (real model in airport.js)" },
            new() { Lat = 41.674056, Lon = 44.958342, R = 70,  Note = "TBS 1952 VIP terminal (airport.js)" },
            new() { Lat = 41.666319, Lon = 44.965312, R = 48,  Note = "TBS maintenance hangar (airport.js)" },
            new() { Lat = 41.680858, Lon = 44.944375, R = 28,  Note = "TBS north hangar (airport.js)" },
        };

        // =====================================================================
        // Built landmarks — a ready-made OBJ/FBX model exists, so PLACE it,
        // never approximate it with primitives. src/landmarks.js:1404-1434 (the
        // place() calls that pass a BUILDERS[...] result exported to OBJ) plus
        // justiceHouse (1416, FBX, built in Blender not THREE — landmarks.js:1367).
        //
        // AssetPath is relative to Assets/, exactly where
        // tools/unity_import_assets.sh / tools/glb_batch_fbx.py put the file.
        // =====================================================================
        public struct LandmarkDef
        {
            public string Name;      // Georgian, byte for byte — never translate
            public double Lat, Lon;
            public float YawJs;      // radians, JS `place(obj, lat, lon, yaw)` convention
            public string AssetPath; // Assets/Resources/Art/Landmarks/x.obj or Assets/Resources/Art/Props/x.fbx
            public string JsRef;
        }

        public static readonly LandmarkDef[] BuiltLandmarks =
        {
            new() { Name = "ტელეანძა",                    Lat = 41.69575,  Lon = 44.78499,  YawJs = 0f,
                    AssetPath = "Assets/Resources/Art/Landmarks/tvTower.obj",     JsRef = "landmarks.js:1404" },
            new() { Name = "სამების საკათედრო ტაძარი",     Lat = 41.69754,  Lon = 44.81654,  YawJs = 3.14f,
                    AssetPath = "Assets/Resources/Art/Landmarks/sameba.obj",      JsRef = "landmarks.js:1406" },
            new() { Name = "რადისონ ბლუ ივერია",            Lat = 41.70392,  Lon = 44.79369,  YawJs = -0.78f,
                    AssetPath = "Assets/Resources/Art/Landmarks/radisson.obj",    JsRef = "landmarks.js:1408" },
            new() { Name = "საქართველოს მატიანე",           Lat = 41.77056,  Lon = 44.81026,  YawJs = -1.38f,
                    AssetPath = "Assets/Resources/Art/Landmarks/chronicle.obj",   JsRef = "landmarks.js:1410" },
            new() { Name = "ანბანის კოშკი",                 Lat = 41.65596,  Lon = 41.63934,  YawJs = 0f,
                    AssetPath = "Assets/Resources/Art/Landmarks/alphabetTower.obj", JsRef = "landmarks.js:1411" },
            new() { Name = "ეშმაკის ბორბალი",               Lat = 41.6541,   Lon = 41.63674,  YawJs = 1.2f,
                    AssetPath = "Assets/Resources/Art/Landmarks/ferrisWheel.obj", JsRef = "landmarks.js:1412" },
            new() { Name = "ოპერისა და ბალეტის თეატრი",     Lat = 41.70112,  Lon = 44.79626,  YawJs = 0.75f,
                    AssetPath = "Assets/Resources/Art/Landmarks/opera.obj",       JsRef = "landmarks.js:1414" },
            new() { Name = "იუსტიციის სახლი",               Lat = 41.699024, Lon = 44.806656, YawJs = 0f,
                    AssetPath = "Assets/Resources/Art/Props/justice_house.fbx",  JsRef = "landmarks.js:1367,1416" },
            new() { Name = "საქართველოს პარლამენტი",        Lat = 41.69667,  Lon = 44.79805,  YawJs = -1.1f,
                    AssetPath = "Assets/Resources/Art/Landmarks/parliament.obj", JsRef = "landmarks.js:1419" },
            new() { Name = "დინამო არენა",                  Lat = 41.72285,  Lon = 44.78966,  YawJs = -0.88f,
                    AssetPath = "Assets/Resources/Art/Landmarks/dinamo.obj",      JsRef = "landmarks.js:1420" },
            // NOTE the lat/lon here (1423) differs slightly from this same
            // landmark's EXCLUSIONS entry above (41.70747/44.78555) — that is
            // a pre-existing discrepancy in landmarks.js itself; ported as-is,
            // using the actual place() call, which is what is visually real.
            new() { Name = "ფილარმონია",                    Lat = 41.70731,  Lon = 44.78601,  YawJs = -2.15f,
                    AssetPath = "Assets/Resources/Art/Landmarks/philharmonic.obj", JsRef = "landmarks.js:1423" },
        };

        /// <summary>
        /// BUILDERS also exports peaceBridge/mcdonalds/ship (landmarks.js:1359)
        /// with OBJ assets on disk, but NONE of the three has a place() call in
        /// current addLandmarks() — mcdonalds is placed per-POI by
        /// shopfronts.js (out of this port's scope: landmarks/airport only),
        /// and peaceBridge/ship are unreferenced anywhere in src/*.js. Matching
        /// current JS BEHAVIOUR means NOT instantiating them. Listed here only
        /// so the gap is a decision, not an oversight.
        /// </summary>
        public static readonly string[] UnplacedBuiltAssets =
        {
            "Assets/Resources/Art/Landmarks/peaceBridge.obj", // landmarks.js:525 — dead code upstream too
            "Assets/Resources/Art/Landmarks/mcdonalds.obj",   // landmarks.js:559 — placed by shopfronts.js, not this file
            "Assets/Resources/Art/Landmarks/ship.obj",        // landmarks.js:964 — dead code upstream too
        };

        /// <summary>World placement for a built landmark: position already in
        /// Unity space (ground height sampled from <paramref name="terrain"/>,
        /// the half-res TRIANGLE sampler — see TerrainSampler.cs) and yaw
        /// already converted to Unity's left-handed convention. Port of
        /// landmarks.js:1386-1403 <c>addLandmarks()</c>'s <c>place()</c>/
        /// <c>toLocal()</c> pair (lat/lon -> local xz via meta.mlat/mlon, then
        /// <c>citymap.heightAt</c>, then <c>obj.rotation.y = yaw</c>) — split
        /// into GeoFrame.FromLatLon + TerrainSampler.HeightAt + YawToUnity
        /// because this file returns pure data (mesh/position/yaw), not a
        /// placed GameObject; <c>group.add(obj)</c> and the destructible-
        /// landmark registration (<c>citymap.registerLandmark</c>, JS:1398-99)
        /// are the caller's job once it instantiates these.</summary>
        public static (Vector3 pos, float yawUnityDeg) PlaceWorld(LandmarkDef def, TerrainSampler terrain)
        {
            Vector3 p = GeoFrame.FromLatLon(def.Lat, def.Lon);
            p.y = terrain.HeightAt(p.x, p.z);
            float yawDeg = GeoFrame.YawToUnity(def.YawJs) * Mathf.Rad2Deg;
            return (p, yawDeg);
        }

        // =====================================================================
        // Procedural landmarks — no OBJ/FBX was ever exported for these ten;
        // the JS itself builds them out of box()/cyl() primitives
        // (landmarks.js:1021-1209), same as an ordinary building wall. Ported
        // here as real merged meshes, not a placeholder — the actual massing,
        // at the actual dimensions, just without the photo-texture facade
        // (the same gap BuildingBuilder.cs already has for ordinary buildings).
        // =====================================================================
        public struct ProceduralDef
        {
            public string Name, Key, JsRef;
            public double Lat, Lon;
            public float YawJs;
        }

        public static readonly ProceduralDef[] ProceduralLandmarks =
        {
            new() { Name = "ნარიყალას ციხე",            Key = "narikala",         Lat = 41.68787, Lon = 44.80894, YawJs = -0.7f,  JsRef = "landmarks.js:1021,1425" },
            new() { Name = "ფუნიკულიორის სადგური",      Key = "funicularStation", Lat = 41.69489, Lon = 44.78652, YawJs = -1.48f, JsRef = "landmarks.js:1054,1426" },
            new() { Name = "მეტეხის ტაძარი",            Key = "metekhi",          Lat = 41.69015, Lon = 44.8113,  YawJs = 0f,     JsRef = "landmarks.js:1070,1427" },
            new() { Name = "სვეტიცხოველი",              Key = "svetitskhoveli",   Lat = 41.84227, Lon = 44.72088, YawJs = 0f,     JsRef = "landmarks.js:1083,1428" },
            new() { Name = "ჯვრის მონასტერი",           Key = "jvari",            Lat = 41.83838, Lon = 44.73355, YawJs = 0f,     JsRef = "landmarks.js:1117,1429" },
            new() { Name = "აბანოთუბანი",               Key = "baths",            Lat = 41.6876,  Lon = 44.8111,  YawJs = 0f,     JsRef = "landmarks.js:1135,1430" },
            new() { Name = "მარიოტი",                   Key = "marriott",         Lat = 41.69916, Lon = 44.7983,  YawJs = 0.7f,   JsRef = "landmarks.js:1153,1431" },
            new() { Name = "ბილტმორი",                  Key = "biltmore",         Lat = 41.70245, Lon = 44.79467, YawJs = 0.75f,  JsRef = "landmarks.js:1170,1432" },
            new() { Name = "შერატონ მეტეხი",            Key = "sheratonMetechi",  Lat = 41.68885, Lon = 44.82317, YawJs = -1.55f, JsRef = "landmarks.js:1185,1433" },
            new() { Name = "რადისონ რედი",               Key = "radissonRed",      Lat = 41.70525, Lon = 44.80045, YawJs = -1.0f,  JsRef = "landmarks.js:1198,1434" },
        };

        // Same place()/toLocal() port as the LandmarkDef overload above (landmarks.js:1386-1403).
        public static (Vector3 pos, float yawUnityDeg) PlaceWorld(ProceduralDef def, TerrainSampler terrain)
        {
            Vector3 p = GeoFrame.FromLatLon(def.Lat, def.Lon);
            p.y = terrain.HeightAt(p.x, p.z);
            float yawDeg = GeoFrame.YawToUnity(def.YawJs) * Mathf.Rad2Deg;
            return (p, yawDeg);
        }

        /// <summary>Builds the one procedural landmark named by <paramref name="key"/>
        /// (matches ProceduralDef.Key) as a set of (mesh, flat tint) pairs, one
        /// mesh per colour bucket so the caller can assign one flat material
        /// per bucket (same division BuildCityScene.MakeMat already uses).</summary>
        public static List<(Mesh mesh, Color32 tint)> BuildProcedural(string key) => key switch
        {
            "narikala"         => Narikala(),
            "funicularStation" => FunicularStation(),
            "metekhi"          => Metekhi(),
            "svetitskhoveli"   => Svetitskhoveli(),
            "jvari"            => Jvari(),
            "baths"            => Baths(),
            "marriott"         => Marriott(),
            "biltmore"         => Biltmore(),
            "sheratonMetechi"  => SheratonMetechi(),
            "radissonRed"      => RadissonRed(),
            _ => new List<(Mesh, Color32)>(),
        };

        // ---- shared tints (src/landmarks.js hex literals, ported verbatim) ----
        // landmarks.js:1008 `const TILE_ROOF = lambert(0x9a5a44);` and :63 `const
        // GOLD = new THREE.MeshStandardMaterial({ color: 0xd9b13b, ... })` — both
        // previously used here with no per-line citation; closed by the
        // landmarks.js full-file audit (docs/port_units/landmarks_rest.json).
        const uint TILE_ROOF = 0x9a5a44;
        const uint GOLD = 0xd9b13b;

        // ---- primitive-mesh plumbing -------------------------------------------
        // UV (added for LandmarkFacades.cs — the 10 procedural landmarks had
        // ZERO texture coordinates before this; a photo facade material could
        // never have shown anything but a single sampled texel. Per-face
        // 0..1 UVs, matching three.js's own BoxGeometry/CylinderGeometry
        // default parameterisation closely enough that photoMat()'s literal
        // `texture.repeat.set(rx,ry)` (Unity: Material.mainTextureScale)
        // reproduces the same tile count landmarks.js itself asks for.
        // Buf/B/AddBox/AddCyl below are the ported equivalents of landmarks.js's
        // module-level mesh helpers — lambert()/WHITE/STEEL (landmarks.js:62-66,
        // a colour -> MeshLambertMaterial map, here a colour -> vertex Buf map
        // instead, since this file merges one mesh per flat tint rather than
        // keeping a THREE.Material per call) and box()/cyl() (landmarks.js:
        // 69-81, THREE.BoxGeometry/CylinderGeometry + position + castShadow).
        // Every one of the ten procedural landmark builders below (Narikala()
        // etc.) calls these in place of box()/cyl(), the same substitution this
        // file's own header already documents for BoxGeometry/CylinderGeometry.
        class Buf { public List<Vector3> V = new(); public List<Vector3> N = new(); public List<int> T = new(); public List<Vector2> UV = new(); }

        static Color32 Hex(uint h) => new Color32((byte)(h >> 16), (byte)(h >> 8), (byte)h, 255);

        static Buf B(Dictionary<uint, Buf> bufs, uint hex)
        {
            if (!bufs.TryGetValue(hex, out var b)) bufs[hex] = b = new Buf();
            return b;
        }

        static List<(Mesh, Color32)> Finish(Dictionary<uint, Buf> bufs, string name)
        {
            var outp = new List<(Mesh, Color32)>(bufs.Count);
            foreach (var kv in bufs)
                outp.Add((MakeMesh($"{name}_{kv.Key:x6}", kv.Value), Hex(kv.Key)));
            return outp;
        }

        /// <summary>Mirrors X and flips winding ONCE for the whole merged mesh —
        /// every primitive above is authored unmirrored, in the JS's own numbers.</summary>
        static Mesh MakeMesh(string name, Buf b)
        {
            int n = b.V.Count;
            var verts = new Vector3[n];
            var norms = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new Vector3(-b.V[i].x, b.V[i].y, b.V[i].z);
                norms[i] = new Vector3(-b.N[i].x, b.N[i].y, b.N[i].z);
            }
            var tris = b.T.ToArray();
            GeoFrame.FlipWinding(tris);
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            // UV is pure texture-space parameterisation (not a position or a
            // normal) so it is carried over UNMIRRORED — same posture
            // PhotoFacades-style tiling takes everywhere else in this project;
            // a repeating masonry/glass tile reads identically either way.
            if (b.UV.Count == n) mesh.SetUVs(0, b.UV);
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>landmarks.js:69-75 `function box(w, h, d, mat, x=0, y=0, z=0)`
        /// (THREE.BoxGeometry(w,h,d) + position.set) — the per-call rotation
        /// JS applies afterwards with `.rotateY()`/`.rotateX()` on the returned
        /// mesh is folded into this signature's own rotY/rotX instead, since C#
        /// builds one shared merged mesh per tint rather than one mesh per box.
        /// A box, centred at <paramref name="center"/>, with an optional
        /// LOCAL rotation about Y (narikala's wall groups) and/or X (a gabled
        /// roof panel's pitch — svetitskhoveli). Faces are built from an
        /// (normal, right, up) table with right×up = normal BY CONSTRUCTION, so
        /// winding is correct without a numeric check per call site.</summary>
        static void AddBox(Buf b, float w, float h, float d, Vector3 center, float rotY = 0f, float rotX = 0f)
        {
            float hw = w / 2f, hh = h / 2f, hd = d / 2f;
            (Vector3 nrm, Vector3 right, Vector3 up)[] faces =
            {
                (new Vector3( 1,0,0), new Vector3(0,1,0), new Vector3(0,0,1)),
                (new Vector3(-1,0,0), new Vector3(0,0,1), new Vector3(0,1,0)),
                (new Vector3(0, 1,0), new Vector3(0,0,1), new Vector3(1,0,0)),
                (new Vector3(0,-1,0), new Vector3(1,0,0), new Vector3(0,0,1)),
                (new Vector3(0,0, 1), new Vector3(1,0,0), new Vector3(0,1,0)),
                (new Vector3(0,0,-1), new Vector3(0,1,0), new Vector3(1,0,0)),
            };
            foreach (var (nrm, right, up) in faces)
            {
                Vector3 faceCenter = new Vector3(nrm.x * hw, nrm.y * hh, nrm.z * hd);
                float rMag = right.x * hw + right.y * hh + right.z * hd;
                float uMag = up.x * hw + up.y * hh + up.z * hd;
                Vector3 p0 = faceCenter - right * rMag - up * uMag;
                Vector3 p1 = faceCenter + right * rMag - up * uMag;
                Vector3 p2 = faceCenter + right * rMag + up * uMag;
                Vector3 p3 = faceCenter - right * rMag + up * uMag;
                Vector3 wn = RotYX(nrm, rotY, rotX);
                p0 = RotYX(p0, rotY, rotX) + center;
                p1 = RotYX(p1, rotY, rotX) + center;
                p2 = RotYX(p2, rotY, rotX) + center;
                p3 = RotYX(p3, rotY, rotX) + center;
                int i0 = b.V.Count;
                b.V.Add(p0); b.V.Add(p1); b.V.Add(p2); b.V.Add(p3);
                b.N.Add(wn); b.N.Add(wn); b.N.Add(wn); b.N.Add(wn);
                b.UV.Add(new Vector2(0, 0)); b.UV.Add(new Vector2(1, 0));
                b.UV.Add(new Vector2(1, 1)); b.UV.Add(new Vector2(0, 1));
                b.T.Add(i0); b.T.Add(i0 + 1); b.T.Add(i0 + 2);
                b.T.Add(i0); b.T.Add(i0 + 2); b.T.Add(i0 + 3);
            }
        }

        /// <summary>rotation.y then rotation.x, Three.js's right-handed convention
        /// (pre-mirror game frame — see the class header).</summary>
        static Vector3 RotYX(Vector3 p, float rotY, float rotX)
        {
            if (rotY != 0f)
            {
                float cy = Mathf.Cos(rotY), sy = Mathf.Sin(rotY);
                p = new Vector3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
            }
            if (rotX != 0f)
            {
                float cx = Mathf.Cos(rotX), sx = Mathf.Sin(rotX);
                p = new Vector3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
            }
            return p;
        }

        /// <summary>landmarks.js:76-82 `function cyl(r1, r2, h, mat, x=0, y=0,
        /// z=0, seg=10)` (THREE.CylinderGeometry(r1,r2,h,seg) + position.set).
        /// THREE.CylinderGeometry(radiusTop, radiusBottom, height, seg)
        /// equivalent: capped both ends (every procedural landmark's cylinders
        /// have non-zero radii at both ends, so there is no open-ended case to
        /// handle here). Side winding and cap winding were each verified by a
        /// hand cross-product check before being pinned here (see the PR notes
        /// — this is NOT the same vertex order as BuildingBuilder's wall quads,
        /// which rely on footprint ring winding rather than an angle sweep).</summary>
        static void AddCyl(Buf b, float rTop, float rBottom, float height, int seg, Vector3 center)
        {
            float y0 = -height / 2f, y1 = height / 2f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = (float)i / seg * Mathf.PI * 2f;
                float a1 = (float)(i + 1) / seg * Mathf.PI * 2f;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * rBottom, y0, Mathf.Sin(a0) * rBottom) + center;
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * rBottom, y0, Mathf.Sin(a1) * rBottom) + center;
                Vector3 p2 = new Vector3(Mathf.Cos(a1) * rTop,    y1, Mathf.Sin(a1) * rTop)    + center;
                Vector3 p3 = new Vector3(Mathf.Cos(a0) * rTop,    y1, Mathf.Sin(a0) * rTop)    + center;
                Vector3 n1 = Vector3.Cross(p3 - p0, p2 - p0).normalized; // verified outward
                int i0 = b.V.Count;
                b.V.Add(p0); b.V.Add(p1); b.V.Add(p2); b.V.Add(p3);
                b.N.Add(n1); b.N.Add(n1); b.N.Add(n1); b.N.Add(n1);
                // Side UV: u wraps the full 0..1 circumference (matches
                // three.js CylinderGeometry), v is bottom(0)/top(1).
                float u0 = (float)i / seg, u1 = (float)(i + 1) / seg;
                b.UV.Add(new Vector2(u0, 0)); b.UV.Add(new Vector2(u1, 0));
                b.UV.Add(new Vector2(u1, 1)); b.UV.Add(new Vector2(u0, 1));
                b.T.Add(i0); b.T.Add(i0 + 3); b.T.Add(i0 + 2);
                b.T.Add(i0); b.T.Add(i0 + 2); b.T.Add(i0 + 1);
            }
            AddCapFan(b, rTop, y1, center, seg, top: true);
            AddCapFan(b, rBottom, y0, center, seg, top: false);
        }

        static void AddCapFan(Buf b, float r, float y, Vector3 center, int seg, bool top)
        {
            int c = b.V.Count;
            b.V.Add(new Vector3(0, y, 0) + center);
            b.N.Add(top ? Vector3.up : Vector3.down);
            b.UV.Add(new Vector2(0.5f, 0.5f)); // cap centre — radial fan UV
            for (int i = 0; i <= seg; i++)
            {
                float a = (float)i / seg * Mathf.PI * 2f;
                b.V.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r) + center);
                b.N.Add(top ? Vector3.up : Vector3.down);
                b.UV.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
            }
            for (int i = 0; i < seg; i++)
            {
                if (top) { b.T.Add(c); b.T.Add(c + 2 + i); b.T.Add(c + 1 + i); }
                else      { b.T.Add(c); b.T.Add(c + 1 + i); b.T.Add(c + 2 + i); }
            }
        }

        /// <summary>Upper hemisphere only (THREE.SphereGeometry(r,wSeg,hSeg,0,2π,0,π/2))
        /// — the bath-house domes. Winding verified by hand cross-product check.</summary>
        static void AddHemisphere(Buf b, float r, int widthSeg, int heightSeg, Vector3 center)
        {
            int cols = widthSeg + 1;
            int baseI = b.V.Count;
            for (int j = 0; j <= heightSeg; j++)
            {
                float theta = Mathf.PI / 2f * j / heightSeg; // 0 = pole, PI/2 = equator
                for (int i = 0; i <= widthSeg; i++)
                {
                    float phi = Mathf.PI * 2f * i / widthSeg;
                    Vector3 p = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)) * r;
                    b.V.Add(p + center);
                    b.N.Add(p.normalized);
                    b.UV.Add(new Vector2((float)i / widthSeg, (float)j / heightSeg));
                }
            }
            for (int j = 0; j < heightSeg; j++)
                for (int i = 0; i < widthSeg; i++)
                {
                    int a = baseI + j * cols + i;
                    int b1 = baseI + j * cols + i + 1;
                    int c = baseI + (j + 1) * cols + i;
                    int d = baseI + (j + 1) * cols + i + 1;
                    b.T.Add(a); b.T.Add(b1); b.T.Add(d);
                    b.T.Add(a); b.T.Add(d); b.T.Add(c);
                }
        }

        /// <summary>Module-level goldCross — landmarks.js:1011-1017, used by the
        /// four church-topped procedural landmarks.</summary>
        static void AddGoldCross(Buf gold, Vector3 center, float s)
        {
            AddCyl(gold, 0.16f * s, 0.16f * s, 3f * s, 6, center + Vector3.up * (1.5f * s));
            AddBox(gold, 1.7f * s, 0.34f * s, 0.34f * s, center + Vector3.up * (2f * s));
        }

        // =====================================================================
        // Narikala fortress — landmarks.js:1021-1050
        // =====================================================================
        static List<(Mesh, Color32)> Narikala()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint brick = 0x9a7a5c, brickT = 0x9a6f52, stone = 0xd9c8a4;

            void Wall(float len, float x, float z, float ry)
            {
                var bk = B(bufs, brick);
                AddBox(bk, len, 9, 2.6f, RotYX(new Vector3(0, 4.5f, 0), ry, 0) + new Vector3(x, 0, z), ry);
                for (int i = 0; i < (int)(len / 4); i++)
                {
                    Vector3 local = new Vector3(-len / 2f + 2f + i * 4f, 9.8f, 0f);
                    AddBox(bk, 1.9f, 1.6f, 2.8f, RotYX(local, ry, 0) + new Vector3(x, 0, z), ry);
                }
            }
            Wall(75, 0, 18, 0.12f);
            Wall(70, -6, -16, -0.18f);
            Wall(40, 42, 4, 1.35f);
            Wall(40, -46, 2, 1.5f);

            (float tx, float tz, float r, float h)[] towers =
            {
                (40, 20, 5.5f, 14), (-42, 18, 5, 13), (40, -14, 5, 13), (-44, -14, 4.6f, 12), (0, 20, 4.4f, 12),
            };
            foreach (var (tx, tz, r, h) in towers)
            {
                AddCyl(B(bufs, brickT), r, r + 0.6f, h, 10, new Vector3(tx, h / 2f, tz));
                AddCyl(B(bufs, brickT), r + 0.9f, r + 0.9f, 1.6f, 10, new Vector3(tx, h + 0.8f, tz));
            }

            var st = B(bufs, stone);
            AddBox(st, 14, 7, 9, new Vector3(0, 3.5f, 2));
            AddBox(st, 9, 7, 14, new Vector3(0, 3.5f, 2));
            AddCyl(st, 3.2f, 3.2f, 5, 12, new Vector3(0, 9.5f, 2));
            AddCyl(B(bufs, TILE_ROOF), 0.2f, 3.4f, 4.4f, 12, new Vector3(0, 14.2f, 2));
            AddGoldCross(B(bufs, GOLD), new Vector3(0, 16.8f, 2), 0.9f);

            return Finish(bufs, "narikala");
        }

        // =====================================================================
        // Mtatsminda funicular upper station — landmarks.js:1054-1066
        // =====================================================================
        static List<(Mesh, Color32)> FunicularStation()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint stone = 0xd6cab0, cornice = 0xbfb49a, arcade = 0xd9cdb4;
            var st = B(bufs, stone);
            AddBox(st, 26, 14, 15, new Vector3(0, 7, 0));
            AddBox(st, 12, 5, 9, new Vector3(0, 17.8f, -2));
            var co = B(bufs, cornice);
            AddBox(co, 28, 1.4f, 17, new Vector3(0, 14.7f, 0));
            AddBox(co, 13, 0.9f, 10, new Vector3(0, 20.6f, -2));
            // the real arcade photo in the JS is a single-sided plane facing the
            // city; ported as a thin panel so the front still reads as a distinct
            // surface without the photo texture.
            AddBox(B(bufs, arcade), 24, 13.2f, 0.3f, new Vector3(0, 7.1f, 7.45f));
            return Finish(bufs, "funicular");
        }

        // =====================================================================
        // Metekhi church — landmarks.js:1070-1079
        // =====================================================================
        static List<(Mesh, Color32)> Metekhi()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint stone = 0xd3c3a2, arch = 0xcdbb97;
            var st = B(bufs, stone);
            AddBox(st, 16, 8, 11, new Vector3(0, 4, 0));
            AddBox(st, 11, 8, 16, new Vector3(0, 4, 0));
            AddCyl(B(bufs, arch), 3.6f, 3.6f, 6.5f, 12, new Vector3(0, 11.2f, 0));
            AddCyl(B(bufs, TILE_ROOF), 0.2f, 3.8f, 5.2f, 12, new Vector3(0, 17, 0));
            AddGoldCross(B(bufs, GOLD), new Vector3(0, 20.2f, 0), 1f);
            return Finish(bufs, "metekhi");
        }

        // =====================================================================
        // Svetitskhoveli cathedral, Mtskheta — landmarks.js:1083-1113
        // =====================================================================
        static List<(Mesh, Color32)> Svetitskhoveli()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint stone = 0xd9c8a4, arch = 0xcdbb97, brick = 0xa08163;
            var st = B(bufs, stone);
            AddBox(st, 52, 13, 22, new Vector3(0, 6.5f, 0));
            AddBox(st, 24, 15, 30, new Vector3(4, 7.5f, 0));

            // gableRoof(len,wid,rise,x,y,z): two sloped panels, PITCH about local X
            void GableRoof(float len, float wid, float rise, float x, float y, float z)
            {
                var roof = B(bufs, TILE_ROOF);
                float slope = Mathf.Sqrt(wid / 2f * (wid / 2f) + rise * rise) + 0.5f;
                float ang = Mathf.Atan2(rise, wid / 2f);
                foreach (int s in new[] { -1, 1 })
                    AddBox(roof, len, 0.5f, slope, new Vector3(x, y + rise / 2f, z + s * wid / 4f), 0f, s * ang);
            }
            GableRoof(52, 22, 6, 0, 13, 0);

            AddCyl(B(bufs, arch), 6.2f, 6.2f, 9, 12, new Vector3(4, 19, 0));
            AddCyl(B(bufs, TILE_ROOF), 0.25f, 6.6f, 7.5f, 12, new Vector3(4, 27.2f, 0));
            AddGoldCross(B(bufs, GOLD), new Vector3(4, 31.8f, 0), 1.2f);

            const float W = 120, D = 118;
            var bk = B(bufs, brick);
            (float w, float d, float x, float z)[] walls =
            {
                (W, 2.2f, 0, D / 2f), (W, 2.2f, 0, -D / 2f), (2.2f, D, W / 2f, 0), (2.2f, D, -W / 2f, 0),
            };
            foreach (var (w, d, x, z) in walls) AddBox(bk, w, 6.5f, d, new Vector3(x, 3.25f, z));
            foreach (var (x, z) in new[] { (W / 2f, D / 2f), (-W / 2f, D / 2f), (W / 2f, -D / 2f), (-W / 2f, -D / 2f) })
            {
                AddCyl(bk, 4.4f, 4.8f, 9.5f, 10, new Vector3(x, 4.75f, z));
                AddCyl(B(bufs, TILE_ROOF), 0.2f, 5, 3.6f, 10, new Vector3(x, 11.2f, z));
            }
            return Finish(bufs, "svetitskhoveli");
        }

        // =====================================================================
        // Jvari monastery — landmarks.js:1117-1131
        // =====================================================================
        static List<(Mesh, Color32)> Jvari()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint stone = 0xc4a982, ruin = 0xa5876a;
            var st = B(bufs, stone);
            AddBox(st, 21, 8, 15, new Vector3(0, 4, 0));
            AddBox(st, 15, 8, 21, new Vector3(0, 4, 0));
            foreach (var (ax, az) in new[] { (10f, 0f), (-10f, 0f), (0f, 10f), (0f, -10f) })
            {
                AddCyl(st, 4.2f, 4.2f, 7.5f, 10, new Vector3(ax * 0.72f, 3.75f, az * 0.72f));
                AddCyl(B(bufs, TILE_ROOF), 0.2f, 4.4f, 2.6f, 10, new Vector3(ax * 0.72f, 8.6f, az * 0.72f));
            }
            AddCyl(st, 4.6f, 4.9f, 6, 8, new Vector3(0, 10.8f, 0));
            AddCyl(B(bufs, TILE_ROOF), 0.25f, 5, 4.6f, 8, new Vector3(0, 16, 0));
            AddGoldCross(B(bufs, GOLD), new Vector3(0, 19.4f, 0), 1f);
            AddBox(B(bufs, ruin), 9, 12, 8, new Vector3(-16, 6, -2));
            return Finish(bufs, "jvari");
        }

        // =====================================================================
        // Abanotubani sulfur baths — landmarks.js:1135-1149
        // =====================================================================
        static List<(Mesh, Color32)> Baths()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint brick = 0x9c7355, dome = 0xa87f5e;
            AddBox(B(bufs, brick), 52, 4.5f, 34, new Vector3(0, 2.25f, 0));
            (float dx, float dz, float r)[] domes =
            {
                (-18, -8, 5.5f), (-6, -9, 6.5f), (7, -8, 5.8f), (18, -6, 4.8f), (-12, 6, 5), (2, 7, 6), (14, 7, 5.2f),
            };
            var dm = B(bufs, dome);
            var bk = B(bufs, brick);
            foreach (var (dx, dz, r) in domes)
            {
                AddHemisphere(dm, r, 14, 9, new Vector3(dx, 4.5f, dz));
                AddCyl(bk, 1.1f, 1.3f, 1.8f, 8, new Vector3(dx, 4.5f + r + 0.6f, dz));
            }
            return Finish(bufs, "baths");
        }

        // =====================================================================
        // Tbilisi Marriott — landmarks.js:1153-1167 (sign text dropped — no
        // canvas-label pipeline ported here; see the report)
        // =====================================================================
        static List<(Mesh, Color32)> Marriott()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint fac = 0xdfdad0, cornice = 0xc9c2b4;
            var f = B(bufs, fac);
            AddBox(f, 56, 25, 19, new Vector3(0, 12.5f, 0));
            AddBox(f, 19, 25, 42, new Vector3(20, 12.5f, -14));
            AddCyl(f, 10.5f, 10.5f, 25, 16, new Vector3(24, 12.5f, 8));
            AddBox(B(bufs, cornice), 58, 1.6f, 21, new Vector3(0, 25.9f, 0));
            return Finish(bufs, "marriott");
        }

        // =====================================================================
        // The Biltmore Tbilisi — landmarks.js:1170-1182
        // =====================================================================
        static List<(Mesh, Color32)> Biltmore()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint glass = 0x51707f, white = 0xeceae4, podium = 0xd9cfc0;
            AddBox(B(bufs, glass), 34, 104, 21, new Vector3(0, 58, 0));
            var w = B(bufs, white);
            AddBox(w, 3, 108, 23, new Vector3(-18, 56, 0));
            AddBox(w, 35, 4, 26, new Vector3(0, 112, 0));
            AddBox(B(bufs, podium), 40, 8, 26, new Vector3(0, 4, 0));
            return Finish(bufs, "biltmore");
        }

        // =====================================================================
        // Sheraton Grand Metechi Palace — landmarks.js:1185-1195
        // =====================================================================
        static List<(Mesh, Color32)> SheratonMetechi()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint tan = 0xc9b394, glassBand = 0x3e5a66;
            var t = B(bufs, tan);
            AddBox(t, 58, 12, 48, new Vector3(0, 6, 0));
            AddBox(t, 46, 10, 38, new Vector3(0, 17, -2));
            AddBox(t, 34, 9, 28, new Vector3(0, 26.5f, -4));
            var gb = B(bufs, glassBand);
            AddBox(gb, 22, 8, 20, new Vector3(0, 35, -6));
            foreach (float y in new[] { 12.6f, 22.6f, 31.4f }) AddBox(gb, 40, 1.1f, 32, new Vector3(0, y, -3));
            return Finish(bufs, "sheraton_metechi");
        }

        // =====================================================================
        // Radisson RED — landmarks.js:1198-1209 (brand-bar sign reduced to a
        // flat tinted accent slab — no canvas-label pipeline ported here)
        // =====================================================================
        static List<(Mesh, Color32)> RadissonRed()
        {
            var bufs = new Dictionary<uint, Buf>();
            const uint dark = 0x3c3f45, accent = 0xc8102e;
            AddBox(B(bufs, dark), 26, 34, 18, new Vector3(0, 17, 0));
            AddBox(B(bufs, accent), 12, 2.2f, 0.3f, new Vector3(0, 32, 9.1f));
            return Finish(bufs, "radisson_red");
        }

        // =====================================================================
        // Hero statues — a real figure (statueGLB, Art/Statues/*.fbx — PLACE
        // that, do not re-sculpt it) on a procedural granite/stone pedestal.
        // landmarks.js:1227-1357. Equestrian builders face +x, standing ones
        // face +z (the comment at 1437-1438); yaw below is the group's own, in
        // the JS's right-handed convention, unnegated — mirrored once at
        // placement time like everything else here.
        // =====================================================================
        public enum Tint { KeepOwn, Gold, Aluminum, BronzeV }

        public struct StatueSpot
        {
            public string File;      // Assets/Resources/Art/Statues/<File>.fbx
            public float Scale;      // GLB normalized to height 1 — scale == real metres
            public float LocalY;
            public float LocalX, LocalZ;   // pre-rotation offset, game frame (writersPair only)
            public float LocalYawJs;       // additional rotation.y on the figure itself
            public Tint Tint;
        }

        public struct HeroStatueDef
        {
            public string Name, JsRef;
            public double Lat, Lon;
            public float YawJs;
            public (string kind, float w, float h, float d, float x, float y, float z)[] Pedestal; // kind: "box"|"cyl" (w=rTop,h=rBottom,d=height for cyl)
            public StatueSpot[] Spots;
        }

        // landmarks.js:1218 `const GRANITE = lambert(0x8e8a80);` — previously
        // used here (GraniteTint below) with no per-line citation; closed by
        // the landmarks.js full-file audit (docs/port_units/landmarks_rest.json).
        const uint GRANITE = 0x8e8a80;

        public static readonly HeroStatueDef[] HeroStatues =
        {
            new()
            {
                Name = "თავისუფლების ძეგლი", JsRef = "landmarks.js:1254-1266,1439", Lat = 41.69339, Lon = 44.80150, YawJs = -0.79f,
                Pedestal = new[]
                {
                    ("cyl", 6.8f, 7.4f, 1.0f, 0f, 0.5f, 0f), ("cyl", 5.4f, 6.2f, 1.0f, 0f, 1.5f, 0f),
                    ("cyl", 3.2f, 4.4f, 2.6f, 0f, 3.3f, 0f), ("cyl", 1.35f, 1.8f, 23.5f, 0f, 16.35f, 0f),
                    ("cyl", 2.2f, 1.5f, 1.2f, 0f, 28.7f, 0f), ("box", 4.4f, 0.7f, 4.4f, 0f, 29.65f, 0f),
                },
                Spots = new[] { new StatueSpot { File = "george", Scale = 5.6f, LocalY = 30f, Tint = Tint.Gold } },
            },
            new()
            {
                Name = "ქართლის დედა", JsRef = "landmarks.js:1270-1276,1440", Lat = 41.68812, Lon = 44.80460, YawJs = -1.13f,
                Pedestal = new[] { ("box", 5.2f, 1.4f, 5.2f, 0f, 0.7f, 0f) },
                Spots = new[] { new StatueSpot { File = "kartlis_deda", Scale = 18.6f, LocalY = 1.4f, Tint = Tint.Aluminum } },
            },
            new()
            {
                Name = "ვახტანგ გორგასალი", JsRef = "landmarks.js:1281-1286,1441", Lat = 41.69032, Lon = 44.81073, YawJs = -0.35f,
                Pedestal = new[] { ("box", 7.5f, 2f, 5.5f, 0f, 1f, 0f) },
                Spots = new[] { new StatueSpot { File = "gorgasali", Scale = 11f, LocalY = 2f, Tint = Tint.KeepOwn } },
            },
            new()
            {
                Name = "შოთა რუსთაველი", JsRef = "landmarks.js:1290-1297,1442", Lat = 41.70389, Lon = 44.78993, YawJs = -2.36f,
                Pedestal = new[] { ("box", 4.6f, 0.9f, 4.6f, 0f, 0.45f, 0f), ("box", 2.7f, 4.0f, 2.7f, 0f, 2.9f, 0f) },
                Spots = new[] { new StatueSpot { File = "rustaveli", Scale = 4.6f, LocalY = 4.9f, Tint = Tint.BronzeV } },
            },
            new()
            {
                Name = "დავით აღმაშენებელი", JsRef = "landmarks.js:1301-1307,1443", Lat = 41.76157, Lon = 44.76860, YawJs = -1.05f,
                Pedestal = new[] { ("box", 8f, 1.2f, 6f, 0f, 0.6f, 0f), ("box", 4.6f, 5.4f, 3.4f, 0f, 3.9f, 0f) },
                Spots = new[] { new StatueSpot { File = "david", Scale = 8.4f, LocalY = 6.6f, Tint = Tint.BronzeV } },
            },
            new()
            {
                Name = "წმინდა ნინო", JsRef = "landmarks.js:1311-1319,1444", Lat = 41.76249, Lon = 44.76732, YawJs = 0.52f,
                Pedestal = new[]
                {
                    ("cyl", 2.6f, 3.2f, 1.2f, 0f, 0.6f, 0f), ("cyl", 0.85f, 1.25f, 12.5f, 0f, 7.45f, 0f),
                    ("cyl", 1.35f, 0.95f, 0.8f, 0f, 14.1f, 0f),
                },
                Spots = new[] { new StatueSpot { File = "nino", Scale = 4.5f, LocalY = 14.5f, Tint = Tint.BronzeV } },
            },
            new()
            {
                Name = "გიორგი სააკაძე", JsRef = "landmarks.js:1324-1329,1445", Lat = 41.7245, Lon = 44.7765, YawJs = 1.57f,
                Pedestal = new[] { ("box", 6f, 1f, 4.5f, 0f, 0.5f, 0f) },
                Spots = new[] { new StatueSpot { File = "saakadze", Scale = 9f, LocalY = 1f, Tint = Tint.KeepOwn } },
            },
            new()
            {
                Name = "ვაჟა-ფშაველა", JsRef = "landmarks.js:1332-1338,1446", Lat = 41.72384, Lon = 44.72901, YawJs = -1.75f,
                Pedestal = new[] { ("box", 4.4f, 0.8f, 4.4f, 0f, 0.4f, 0f), ("box", 2.6f, 2.8f, 2.6f, 0f, 2.2f, 0f) },
                Spots = new[] { new StatueSpot { File = "vazha", Scale = 4.5f, LocalY = 3.6f, Tint = Tint.BronzeV } },
            },
            new()
            {
                Name = "ილია და აკაკი", JsRef = "landmarks.js:1342-1348,1447", Lat = 41.69775, Lon = 44.79818, YawJs = -0.7f,
                Pedestal = new[] { ("box", 6.2f, 1.8f, 3.2f, 0f, 0.9f, 0f) },
                Spots = new[]
                {
                    new StatueSpot { File = "writer_ilia",  Scale = 3.5f, LocalY = 1.8f, LocalX = -1.25f, LocalZ = 0.08f,  Tint = Tint.BronzeV },
                    new StatueSpot { File = "writer_akaki", Scale = 3.5f, LocalY = 1.8f, LocalX = 1.25f,  LocalZ = -0.08f, Tint = Tint.BronzeV },
                },
            },
            new()
            {
                Name = "ფარნავაზ I", JsRef = "landmarks.js:1351-1357,1448", Lat = 41.68883, Lon = 44.82045, YawJs = 1.05f,
                Pedestal = new[] { ("box", 4f, 0.8f, 4f, 0f, 0.4f, 0f), ("box", 2.4f, 2.6f, 2.4f, 0f, 2.1f, 0f) },
                Spots = new[] { new StatueSpot { File = "pharnavaz", Scale = 5.4f, LocalY = 3.4f, Tint = Tint.BronzeV } },
            },
        };

        /// <summary>Builds the granite/stone pedestal under a hero statue as one
        /// merged mesh (single granite-ish tint per def — see each def's own
        /// JS colour; all ten happen to use GRANITE or a close variant, ported
        /// verbatim where it differs — rustaveli's red granite, davidBuilder's
        /// plain stone).</summary>
        public static Mesh BuildPedestal(HeroStatueDef def)
        {
            var b = new Buf();
            foreach (var (kind, w, h, d, x, y, z) in def.Pedestal)
            {
                if (kind == "box") AddBox(b, w, h, d, new Vector3(x, y, z));
                else AddCyl(b, w, h, d, kind == "cyl" && w > 2f ? 20 : 16, new Vector3(x, y, z));
            }
            return MakeMesh($"pedestal_{def.Name}", b);
        }

        /// <summary>World placement for the pedestal AND each statue spot, fully
        /// composed (group position + group yaw + the spot's own local offset
        /// and local yaw — only writersPair has a nonzero local offset/yaw; see
        /// landmarks.js:1227-1250 statueGLB). Computed in the GAME FRAME first,
        /// then mirrored once at the very end — see the class header.</summary>
        public static (Vector3 pedestalPos, float pedestalYawDeg,
                       Vector3[] spotPos, float[] spotYawDeg) PlaceWorld(HeroStatueDef def, TerrainSampler terrain)
        {
            Vector3 groupUnity = GeoFrame.FromLatLon(def.Lat, def.Lon);
            groupUnity.y = terrain.HeightAt(groupUnity.x, groupUnity.z);
            float pedestalYawDeg = GeoFrame.YawToUnity(def.YawJs) * Mathf.Rad2Deg;

            var spotPos = new Vector3[def.Spots.Length];
            var spotYaw = new float[def.Spots.Length];
            float cy = Mathf.Cos(def.YawJs), sy = Mathf.Sin(def.YawJs);
            for (int i = 0; i < def.Spots.Length; i++)
            {
                var s = def.Spots[i];
                // rotate the local (game-frame) offset by the group's JS yaw,
                // THEN mirror X — see the class header's "mirror once" rule.
                float rx = s.LocalX * cy + s.LocalZ * sy;
                float rz = -s.LocalX * sy + s.LocalZ * cy;
                spotPos[i] = groupUnity + new Vector3(-rx, s.LocalY, rz);
                spotYaw[i] = GeoFrame.YawToUnity(def.YawJs + s.LocalYawJs) * Mathf.Rad2Deg;
            }
            return (groupUnity, pedestalYawDeg, spotPos, spotYaw);
        }

        // =====================================================================
        // Generic street statues and busts — landmarks.js:1450-1505 (placement)
        // and :1591-1669 (the complete OSM inventory, 207 street points + 91
        // busts; verified by direct extraction from the source arrays, not
        // retyped by hand). Five sculpted street poses, three bust poses,
        // assigned deterministically by point index (JS `pick(pts,k,n)`).
        // Yaw is a DETERMINISTIC pseudo-random facing, not footprint-derived —
        // landmarks.js:1467: `(p.x*7.3+p.z) % (2*PI)` on the GAME-FRAME x/z, so
        // it is recomputed here from game-frame coordinates and then negated
        // like any other hand-tuned yaw, which reproduces the JS bit-for-bit
        // up to float rounding.
        // =====================================================================
        public struct InstancedStatue { public Vector3 Pos; public float YawDeg; public int Variant; }

        public static List<InstancedStatue> PlaceStreetStatues(TerrainSampler terrain) => PlaceInstanced(StreetStatuePoints, 5, 1.5f, terrain);
        public static List<InstancedStatue> PlaceBusts(TerrainSampler terrain) => PlaceInstanced(BustPoints, 3, 1.6f, terrain);

        static List<InstancedStatue> PlaceInstanced(double[,] points, int variants, float yOffset, TerrainSampler terrain)
        {
            int n = points.GetLength(0);
            var list = new List<InstancedStatue>(n);
            for (int i = 0; i < n; i++)
            {
                double lat = points[i, 0], lon = points[i, 1];
                Vector3 p = GeoFrame.FromLatLon(lat, lon);
                float gameX = -p.x, gameZ = p.z; // back to game frame — see class header
                float yawJs = Mod2Pi(gameX * 7.3f + gameZ);
                float groundY = terrain.HeightAt(p.x, p.z);
                list.Add(new InstancedStatue
                {
                    Pos = new Vector3(p.x, groundY + yOffset, p.z),
                    YawDeg = GeoFrame.YawToUnity(yawJs) * Mathf.Rad2Deg,
                    Variant = i % variants,
                });
            }
            return list;
        }

        /// <summary>JS `%` truncated-remainder semantics (same sign as the
        /// dividend) — C#'s own `%` on floats already matches this, so this is
        /// just the formula, not a semantic patch.</summary>
        static float Mod2Pi(float v) => v % (Mathf.PI * 2f);

        /// <summary>1.7x1.5x1.7 granite plinth under a street statue — landmarks.js:1495-1497.</summary>
        public static Mesh BuildPlinth()
        {
            var b = new Buf();
            AddBox(b, 1.7f, 1.5f, 1.7f, new Vector3(0, 0.75f, 0));
            return MakeMesh("statue_plinth", b);
        }

        /// <summary>1.0x1.6x1.0 granite pillar under a bust — landmarks.js:1501-1503.</summary>
        public static Mesh BuildPillar()
        {
            var b = new Buf();
            AddBox(b, 1f, 1.6f, 1f, new Vector3(0, 0.8f, 0));
            return MakeMesh("bust_pillar", b);
        }

        public static readonly Color32 GraniteTint = Hex(GRANITE);
        /// <summary>MeshLambertMaterial({color:0xcbb89a, vertexColors:true}) — landmarks.js:1498.</summary>
        public static readonly Color32 StreetStatueTint = Hex(0xcbb89a);
        /// <summary>MeshLambertMaterial({color:0xbfae92, vertexColors:true}) — landmarks.js:1504.</summary>
        public static readonly Color32 BustTint = Hex(0xbfae92);

        // Extracted directly from src/landmarks.js:1591-1643 (STREET_STATUE_POINTS,
        // 207 points) — not retyped by hand, pulled out with a script to avoid a
        // transcription error in 414 numbers.
        //
        // `double[,]`, NOT `float[,]`. These were float literals, and float32 holds
        // only ~7 significant decimal digits — one short of a Tbilisi coordinate.
        // Across the 298 points here the rounding moved statues by up to 0.212 m
        // (0.108 m mean), against a project standard of 2 cm established by the
        // BlenderGIS position audit. PlaceInstanced widens to double internally,
        // which is exactly why this was invisible: the precision was already gone
        // at the literal, before the widening. Same bug, same cause, as
        // ShedLayout.ShedLat's note.
        public static readonly double[,] StreetStatuePoints = {
            {41.70732,44.77498}, {41.73986,44.77016}, {41.69068,44.80771}, {41.70997,44.76782},
            {41.83903,44.71074}, {41.70735,44.79164}, {41.82956,44.73537}, {41.69733,44.80988},
            {41.70739,44.7873}, {41.69084,44.80846}, {41.7397,44.77946}, {41.7269,44.79948},
            {41.69693,44.79897}, {41.70128,44.7958}, {41.7019,44.79506}, {41.70888,44.77946},
            {41.6855,44.89179}, {41.68981,44.80887}, {41.69608,44.79694}, {41.70411,44.79889},
            {41.69186,44.81563}, {41.68677,44.80362}, {41.68881,44.8124}, {41.68066,44.84147},
            {41.69635,44.80654}, {41.6962,44.80654}, {41.69645,44.80622}, {41.69856,44.79873},
            {41.69628,44.79353}, {41.71886,44.79013}, {41.7188,44.79029}, {41.72219,44.78677},
            {41.70814,44.80015}, {41.70817,44.79977}, {41.70775,44.80024}, {41.70772,44.79971},
            {41.72155,44.74529}, {41.69623,44.80459}, {41.69646,44.80679}, {41.69969,44.80169},
            {41.75806,44.77355}, {41.68979,44.80891}, {41.69612,44.80328}, {41.6982,44.79916},
            {41.71009,44.80463}, {41.70808,44.76954}, {41.72217,44.74576}, {41.69568,44.80708},
            {41.69417,44.80647}, {41.69206,44.80744}, {41.70142,44.80237}, {41.70128,44.80049},
            {41.69721,44.70034}, {41.71031,44.76509}, {41.71172,44.7561}, {41.71063,44.76892},
            {41.69244,44.81953}, {41.82835,44.72577}, {41.72399,44.80054}, {41.70813,44.79328},
            {41.70794,44.79343}, {41.70757,44.79189}, {41.70737,44.79203}, {41.69172,44.81119},
            {41.70389,44.79232}, {41.69892,44.80166}, {41.68678,44.80143}, {41.70875,44.78128},
            {41.7123,44.77235}, {41.67571,44.82923}, {41.74801,44.79078}, {41.70722,44.77502},
            {41.70141,44.75292}, {41.69471,44.78391}, {41.70802,44.7748}, {41.68661,44.84053},
            {41.72852,44.72929}, {41.72417,44.73875}, {41.71182,44.79248}, {41.71697,44.72515},
            {41.72668,44.75425}, {41.71226,44.78187}, {41.72341,44.70219}, {41.72482,44.75403},
            {41.71117,44.78452}, {41.69377,44.7774}, {41.70606,44.72583}, {41.7032,44.79224},
            {41.69879,44.79963}, {41.7015,44.75426}, {41.69027,44.80735}, {41.69831,44.80955},
            {41.70996,44.80156}, {41.72609,44.72342}, {41.73322,44.7728}, {41.84339,44.71891},
            {41.71512,44.79455}, {41.69816,44.80047}, {41.70115,44.79549}, {41.70064,44.79618},
            {41.70003,44.79684}, {41.69598,44.78863}, {41.69617,44.78856}, {41.70956,44.77928},
            {41.71042,44.77936}, {41.77693,44.80626}, {41.71485,44.7954}, {41.70273,44.80459},
            {41.71103,44.75145}, {41.71103,44.75161}, {41.69026,44.80729}, {41.69025,44.80732},
            {41.72534,44.79372}, {41.69602,44.80653}, {41.69605,44.80653}, {41.69608,44.80653},
            {41.69612,44.80653}, {41.69622,44.80654}, {41.69625,44.80654}, {41.69629,44.80654},
            {41.69631,44.80654}, {41.68479,44.80603}, {41.69703,44.81738}, {41.70986,44.76854},
            {41.70937,44.76867}, {41.70725,44.85509}, {41.70857,44.80005}, {41.71251,44.79642},
            {41.69225,44.81499}, {41.71107,44.74683}, {41.71069,44.77}, {41.71965,44.77861},
            {41.7412,44.77782}, {41.74203,44.77821}, {41.70996,44.76856}, {41.72232,44.77813},
            {41.74759,44.78927}, {41.72635,44.76559}, {41.70717,44.77579}, {41.77497,44.76827},
            {41.67109,44.85888}, {41.69376,44.92382}, {41.7875,44.74893}, {41.6987,44.80135},
            {41.69601,44.81027}, {41.72982,44.79685}, {41.6908,44.83017}, {41.69159,44.87005},
            {41.70254,44.79393}, {41.69999,44.79689}, {41.69982,44.79709}, {41.69978,44.79714},
            {41.69976,44.79716}, {41.70036,44.79637}, {41.83842,44.70389}, {41.84084,44.71469},
            {41.70006,44.79617}, {41.7414,44.83279}, {41.68551,44.82048}, {41.6884,44.81087},
            {41.68647,44.81958}, {41.68584,44.86357}, {41.71209,44.75153}, {41.70362,44.74461},
            {41.69843,44.79971}, {41.7021,44.7937}, {41.70221,44.79357}, {41.70071,44.79539},
            {41.70093,44.79511}, {41.70187,44.79397}, {41.70053,44.79561}, {41.70081,44.79527},
            {41.70041,44.79575}, {41.7003,44.79589}, {41.69984,44.79647}, {41.69874,44.79957},
            {41.77765,44.80479}, {41.71673,44.79255}, {41.71664,44.7923}, {41.71661,44.79232},
            {41.68213,44.8241}, {41.72437,44.77776}, {41.75878,44.77441}, {41.69841,44.80666},
            {41.72223,44.77208}, {41.68817,44.79051}, {41.69151,44.87026}, {41.71905,44.80926},
            {41.70459,44.7807}, {41.71313,44.78249}, {41.68819,44.81075}, {41.72153,44.78883},
            {41.72279,44.79211}, {41.70057,44.804}, {41.69797,44.80003}, {41.69903,44.80043},
            {41.69293,44.80158}, {41.69293,44.80147}, {41.70732,44.78626}, {41.78802,44.76552},
            {41.72068,44.71416}, {41.70698,44.85571}, {41.70644,44.75153}, {41.70841,44.75153},
            {41.71768,44.87665}, {41.7172,44.87702}, {41.68328,44.82341},
        };

        // src/landmarks.js:1645-1669 (BUST_POINTS, 91 points) — same extraction.
        public static readonly double[,] BustPoints = {
            {41.69058,44.8074}, {41.69672,44.80069}, {41.71432,44.794}, {41.71452,44.79387},
            {41.69458,44.80197}, {41.71814,44.79148}, {41.68887,44.81116}, {41.69369,44.80432},
            {41.69854,44.8001}, {41.68925,44.83389}, {41.69861,44.79847}, {41.69856,44.79989},
            {41.69604,44.80366}, {41.70804,44.78244}, {41.70131,44.7957}, {41.70129,44.79567},
            {41.68858,44.81648}, {41.72527,44.73887}, {41.70908,44.78158}, {41.70783,44.78633},
            {41.70967,44.78005}, {41.74841,44.79062}, {41.69659,44.80078}, {41.70714,44.77345},
            {41.72756,44.73171}, {41.72505,44.71512}, {41.70722,44.80246}, {41.70889,44.79507},
            {41.70953,44.79436}, {41.7124,44.78192}, {41.72724,44.75696}, {41.70716,44.77494},
            {41.71082,44.7617}, {41.70099,44.80778}, {41.71234,44.7817}, {41.7222,44.77804},
            {41.70752,44.79114}, {41.70064,44.79625}, {41.72229,44.72947}, {41.70698,44.76548},
            {41.6919,44.79964}, {41.70949,44.77924}, {41.7094,44.77921}, {41.70946,44.77895},
            {41.70953,44.77891}, {41.7096,44.77889}, {41.70965,44.77929}, {41.70967,44.77929},
            {41.70978,44.77932}, {41.7099,44.77935}, {41.70993,44.77936}, {41.70998,44.77937},
            {41.71006,44.77938}, {41.71011,44.7794}, {41.71015,44.77941}, {41.71023,44.77942},
            {41.71031,44.77942}, {41.71062,44.77938}, {41.71017,44.77906}, {41.71022,44.77906},
            {41.70748,44.78498}, {41.71729,44.79182}, {41.68481,44.80598}, {41.68472,44.80599},
            {41.68474,44.80599}, {41.75039,44.78993}, {41.69549,44.80167}, {41.7542,44.77694},
            {41.65432,44.65781}, {41.69252,44.8139}, {41.71915,44.77915}, {41.72597,44.77414},
            {41.71583,44.79454}, {41.71099,44.74122}, {41.70977,44.76883}, {41.70637,44.79934},
            {41.71247,44.74948}, {41.71843,44.77885}, {41.69246,44.82338}, {41.70094,44.79649},
            {41.77774,44.80664}, {41.69709,44.81503}, {41.69722,44.81527}, {41.69716,44.81526},
            {41.71035,44.77941}, {41.70385,44.7445}, {41.69536,44.80716}, {41.6635,44.70554},
            {41.72172,44.78919}, {41.68649,44.81888}, {41.68877,44.81187},
        };
    }
}
