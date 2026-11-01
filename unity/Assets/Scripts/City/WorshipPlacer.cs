using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Every Georgian Orthodox church and mosque at its real OSM position —
    /// ported from src/citymap.js:_buildWorship (citymap.js:935-1113). PURE
    /// DATA/MESH class: no GameObject/Instantiate, no Material/Texture (same
    /// minimalism as TerrainBuilder/WaterBodies — the photo-stone/arch
    /// textures the JS streams in over citymap.js:1066-1083 need new texture
    /// assets, out of scope for "create only the four named files").
    ///
    /// TEMPLATES, ONE EACH. <see cref="ChurchMesh"/> is the real Georgian
    /// silhouette: stone cross-plan with gabled arms, a 12-sided drum, the
    /// pointed conical "tent" dome and cross, a walled churchyard with a west
    /// gate and 8 cypresses — every primitive citymap.js builds, in the same
    /// positions, rebuilt here as hand-wound box/cylinder/cone/gable meshes
    /// (THREE has no such primitives in Unity, so each is written out; see
    /// the winding derivations in each helper's comment — verified by hand,
    /// the same discipline ParkBuilder/LandmarkPlacer's primitive builders
    /// use). It keeps the JS's 3 submeshes (stone walls / arch drum band /
    /// everything else) so a future pass can drop real photo materials onto
    /// submesh 0/1 the way citymap.js does; every vertex ALSO carries that
    /// part's real tint as vertex colour, so the mesh reads correctly even
    /// with nothing but a vertex-colour material assigned. <see cref="MosqueMesh"/>
    /// is the smaller domed-box + minaret silhouette, one submesh,
    /// vertex-coloured throughout.
    ///
    /// PLACEMENT. 421 churches + 9 mosques from StreamingAssets/tbilisi/
    /// city.json.gz (`churches`/`mosques`: [x_dm, z_dm, area_m2] triples),
    /// excluding footprints inside a hand-modelled landmark's exclusion
    /// circle (reuses LandmarkPlacer.Exclusions — citymap.js:1087-1091 reads
    /// the same src/landmarks.js EXCLUSIONS list), scaled by
    /// clamp(sqrt(area)/16, 0.7f, 2.4f), bucketed into 2000 m chunks exactly
    /// like citymap.js's _placeChunkedInstanced so only nearby ones need to
    /// render — the geometry itself needs no per-instance rotation (q is
    /// identity in the JS: "churches face east already"), so the GAME-frame
    /// template's own -X mirror handles every instance for free.
    /// hp = 620 + 420*scale (stone: tougher than a house, citymap.js:1110);
    /// label is the exact Georgian string, byte for byte.
    ///
    /// HANDEDNESS: both templates are authored in the GAME frame with
    /// straightforward right-handed primitives (same convention THREE.js
    /// itself builds in) and mirrored + GeoFrame.FlipWinding exactly once, at
    /// the end — so a bare instance translation (GeoFrame.ToUnity, no
    /// rotation) places it correctly, matching the JS's identity quaternion.
    /// </summary>
    public class WorshipPlacer
    {
        public struct Instance { public Vector3 PositionUnity; public float Scale; public float Hp; public string Kind; public string Label; }
        public struct Chunk { public int Cx, Cz; public List<Instance> Items; }

        public Mesh ChurchMesh;
        public Mesh MosqueMesh;
        public List<Chunk> Chunks = new List<Chunk>();
        public int ChurchCount, MosqueCount, ExcludedChurches, ExcludedMosques;
        public int ChunkCount => Chunks.Count;

        const float ChunkM = 2000f;
        const string ChurchLabel = "ეკლესია";
        const string MosqueLabel = "მეჩეთი";

        static readonly Color Stone = HexColor("#d9d0ba");
        static readonly Color Roof = HexColor("#a3a8ae");
        static readonly Color Tent = HexColor("#959ca4");
        static readonly Color Win = HexColor("#2b333d");
        static readonly Color Gold = HexColor("#d9b13b");
        static readonly Color Pave = HexColor("#b7b2a6");
        static readonly Color Cypress = HexColor("#2e5230");
        static readonly Color MosqueWall = HexColor("#e8e2d4");
        static readonly Color MosqueBase = HexColor("#d8d2c4");
        static readonly Color MosqueDome = HexColor("#3f7a6a");

        static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c.linear;
        }

        public static WorshipPlacer Build(TerrainSampler terrain, string cityPath = null)
        {
            var wp = new WorshipPlacer();
            wp.ChurchMesh = BuildChurchTemplate();
            wp.MosqueMesh = BuildMosqueTemplate();
            if (terrain == null) { Debug.LogWarning("WORSHIP built with no TerrainSampler — skipped"); return wp; }
            cityPath ??= CityData.DefaultPath;
            if (!File.Exists(cityPath)) { Debug.LogWarning($"WORSHIP missing {cityPath}"); return wp; }

            ReadTriples(cityPath, out var churches, out var mosques);

            var excl = new List<(float x, float z, float r2)>();
            foreach (var e in LandmarkPlacer.Exclusions)
            {
                var u = GeoFrame.FromLatLon(e.Lat, e.Lon); // Unity space; x_game = -u.x, z_game = u.z
                excl.Add((-u.x, u.z, e.R * e.R));
            }
            bool Excluded(float x, float z)
            {
                foreach (var e in excl)
                {
                    float dx = x - e.x, dz = z - e.z;
                    if (dx * dx + dz * dz < e.r2) return true;
                }
                return false;
            }

            var buckets = new Dictionary<(int, int), List<Instance>>();
            void Place(List<(float x, float z, float area)> list, string kind, string label, bool isChurch)
            {
                foreach (var (x, z, area) in list)
                {
                    if (Excluded(x, z)) { if (isChurch) wp.ExcludedChurches++; else wp.ExcludedMosques++; continue; }
                    float scale = Mathf.Clamp(Mathf.Sqrt(area) / 16f, 0.7f, 2.4f);
                    float y = terrain.HeightAtGame(x, z) - 0.3f;
                    var inst = new Instance
                    {
                        PositionUnity = GeoFrame.ToUnity(x, y, z),
                        Scale = scale,
                        Hp = 620f + 420f * scale,
                        Kind = kind,
                        Label = label,
                    };
                    int cx = Mathf.FloorToInt(x / ChunkM), cz = Mathf.FloorToInt(z / ChunkM);
                    var key = (cx, cz);
                    if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<Instance>();
                    l.Add(inst);
                    if (isChurch) wp.ChurchCount++; else wp.MosqueCount++;
                }
            }
            Place(churches, "church", ChurchLabel, true);
            Place(mosques, "mosque", MosqueLabel, false);

            foreach (var kv in buckets) wp.Chunks.Add(new Chunk { Cx = kv.Key.Item1, Cz = kv.Key.Item2, Items = kv.Value });
            Debug.Log($"WORSHIP churches={wp.ChurchCount}(excl={wp.ExcludedChurches}) "
                      + $"mosques={wp.MosqueCount}(excl={wp.ExcludedMosques}) chunks={wp.ChunkCount}");
            return wp;
        }

        /// <summary>Streams `churches` and `mosques` (each [x_dm,z_dm,area_m2]) out of city.json.gz.</summary>
        static void ReadTriples(string path, out List<(float, float, float)> churches, out List<(float, float, float)> mosques)
        {
            churches = new List<(float, float, float)>();
            mosques = new List<(float, float, float)>();
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            using var jr = new JsonTextReader(sr);
            int found = 0;
            while (jr.Read() && found < 2)
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                string name = (string)jr.Value;
                if (name != "churches" && name != "mosques") { jr.Skip(); continue; }
                var into = name == "churches" ? churches : mosques;
                jr.Read(); // StartArray
                if (jr.TokenType != JsonToken.StartArray) continue;
                while (jr.Read() && jr.TokenType == JsonToken.StartArray)
                {
                    float x = 0, z = 0, a = 0; int k = 0;
                    while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    {
                        float v = Convert.ToSingle(jr.Value);
                        if (k == 0) x = v / 10f; else if (k == 1) z = v / 10f; else if (k == 2) a = v;
                        k++;
                    }
                    if (k >= 3) into.Add((x, z, a));
                }
                found++;
            }
        }

        // ===================== primitives (GAME frame, right-handed, outward-facing — see class doc) =====================

        static Vector3 RotX(Vector3 p, float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Vector3(p.x, c * p.y - s * p.z, s * p.y + c * p.z); }
        static Vector3 RotY(Vector3 p, float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Vector3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z); }
        static Vector3 RotZ(Vector3 p, float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Vector3(c * p.x - s * p.y, s * p.x + c * p.y, p.z); }

        struct Part { public Vector3[] Verts; public int[] Tris; public Color Col; public int Sub; }

        static Part Xform(Part p, float x, float y, float z, float rx = 0f, float rz = 0f, float ry = 0f)
        {
            var o = new Vector3[p.Verts.Length];
            for (int i = 0; i < p.Verts.Length; i++)
            {
                var v = p.Verts[i];
                if (rx != 0f) v = RotX(v, rx);
                if (rz != 0f) v = RotZ(v, rz);
                if (ry != 0f) v = RotY(v, ry);
                o[i] = v + new Vector3(x, y, z);
            }
            return new Part { Verts = o, Tris = p.Tris, Col = p.Col, Sub = p.Sub };
        }

        /// <summary>Box centred at origin — same vertex/triangle layout as ParkBuilder.BoxAt (proven winding).</summary>
        static (Vector3[], int[]) Box(float w, float h, float d)
        {
            float x = w / 2f, y = h / 2f, z = d / 2f;
            var v = new[]
            {
                new Vector3(-x, -y, -z), new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(-x, y, -z),
                new Vector3(-x, -y, z), new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z),
            };
            var t = new[] { 4, 5, 6, 4, 6, 7, 1, 0, 3, 1, 3, 2, 5, 1, 2, 5, 2, 6, 0, 4, 7, 0, 7, 3, 7, 6, 2, 7, 2, 3, 0, 1, 5, 0, 5, 4 };
            return (v, t);
        }

        /// <summary>
        /// Cylinder centred at origin, side faces always, end caps unless
        /// openEnded. Side winding matches ParkBuilder.Cylinder; cap winding
        /// derived by hand (ring traversed in increasing angle, embedded flat,
        /// gives a -Y normal — so the BOTTOM cap uses that order directly and
        /// the TOP cap reverses it).
        /// </summary>
        static (Vector3[], int[]) Cylinder(float rBottom, float rTop, float height, int sides, bool openEnded)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float y0 = -height / 2f, y1 = height / 2f;
            int bStart = 0, tStart = sides;
            for (int i = 0; i < sides; i++) { float a = i / (float)sides * Mathf.PI * 2f; verts.Add(new Vector3(Mathf.Cos(a) * rBottom, y0, Mathf.Sin(a) * rBottom)); }
            for (int i = 0; i < sides; i++) { float a = i / (float)sides * Mathf.PI * 2f; verts.Add(new Vector3(Mathf.Cos(a) * rTop, y1, Mathf.Sin(a) * rTop)); }
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                tris.Add(bStart + i); tris.Add(tStart + i); tris.Add(tStart + j);
                tris.Add(bStart + i); tris.Add(tStart + j); tris.Add(bStart + j);
            }
            if (!openEnded)
            {
                int bc = verts.Count; verts.Add(new Vector3(0, y0, 0));
                for (int i = 0; i < sides; i++) { int j = (i + 1) % sides; tris.Add(bc); tris.Add(bStart + i); tris.Add(bStart + j); }
                int tc = verts.Count; verts.Add(new Vector3(0, y1, 0));
                for (int i = 0; i < sides; i++) { int j = (i + 1) % sides; tris.Add(tc); tris.Add(tStart + j); tris.Add(tStart + i); }
            }
            return (verts.ToArray(), tris.ToArray());
        }

        /// <summary>Cone centred at origin (base at -h/2, apex at +h/2), with a base cap. Winding derived by hand.</summary>
        static (Vector3[], int[]) Cone(float r, float height, int sides)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float y0 = -height / 2f, y1 = height / 2f;
            for (int i = 0; i < sides; i++) { float a = i / (float)sides * Mathf.PI * 2f; verts.Add(new Vector3(Mathf.Cos(a) * r, y0, Mathf.Sin(a) * r)); }
            int apex = verts.Count; verts.Add(new Vector3(0, y1, 0));
            for (int i = 0; i < sides; i++) { int j = (i + 1) % sides; tris.Add(i); tris.Add(apex); tris.Add(j); }
            int bc = verts.Count; verts.Add(new Vector3(0, y0, 0));
            for (int i = 0; i < sides; i++) { int j = (i + 1) % sides; tris.Add(bc); tris.Add(i); tris.Add(j); }
            return (verts.ToArray(), tris.ToArray());
        }

        /// <summary>Open-bottomed upper hemisphere (THREE.SphereGeometry(r,...,0,2pi,0,pi/2) equivalent).</summary>
        static (Vector3[], int[]) DomeHalf(float r, int sides, int rings)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int stride = sides + 1;
            for (int j = 0; j <= rings; j++)
            {
                float theta = (j / (float)rings) * (Mathf.PI / 2f);
                float y = r * Mathf.Sin(theta);
                float ringR = r * Mathf.Cos(theta);
                for (int i = 0; i <= sides; i++)
                {
                    float a = i / (float)sides * Mathf.PI * 2f;
                    verts.Add(new Vector3(ringR * Mathf.Cos(a), y, ringR * Mathf.Sin(a)));
                }
            }
            for (int j = 0; j < rings; j++)
            {
                for (int i = 0; i < sides; i++)
                {
                    int a = j * stride + i, b = a + 1, c = (j + 1) * stride + i, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
            return (verts.ToArray(), tris.ToArray());
        }

        /// <summary>
        /// Triangular-prism gable roof, ridge along local Z, base at y=0 —
        /// citymap.js:954-964 (ExtrudeGeometry of a triangle). All 5 face
        /// windings derived and verified by hand (see RiverSheet's EarClip
        /// doc for the same kind of derivation): z-minus cap (0,2,1),
        /// z-plus cap (3,4,5), bottom (0,1,4)(0,4,3), left slope
        /// (0,3,5)(0,5,2), right slope (1,2,5)(1,5,4).
        /// </summary>
        static (Vector3[], int[]) Gable(float w, float h, float len)
        {
            float hw = w / 2f, hl = len / 2f;
            var v = new[]
            {
                new Vector3(-hw, 0, -hl), new Vector3(hw, 0, -hl), new Vector3(0, h, -hl),
                new Vector3(-hw, 0, hl), new Vector3(hw, 0, hl), new Vector3(0, h, hl),
            };
            var t = new[]
            {
                0, 2, 1,
                3, 4, 5,
                0, 1, 4, 0, 4, 3,
                0, 3, 5, 0, 5, 2,
                1, 2, 5, 1, 5, 4,
            };
            return (v, t);
        }

        static Mesh BuildTemplate(List<Part> parts, int subMeshCount)
        {
            var vertsBySub = new List<Vector3>[subMeshCount];
            var trisBySub = new List<int>[subMeshCount];
            var colsBySub = new List<Color>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++) { vertsBySub[s] = new List<Vector3>(); trisBySub[s] = new List<int>(); colsBySub[s] = new List<Color>(); }

            foreach (var p in parts)
            {
                int s = p.Sub;
                int baseIdx = vertsBySub[s].Count;
                foreach (var v in p.Verts) { vertsBySub[s].Add(new Vector3(-v.x, v.y, v.z)); colsBySub[s].Add(p.Col); } // mirror X
                foreach (var ti in p.Tris) trisBySub[s].Add(baseIdx + ti);
            }

            // merge all submeshes' vertices into one buffer, remember per-submesh triangle ranges
            var allVerts = new List<Vector3>();
            var allCols = new List<Color>();
            var offsets = new int[subMeshCount];
            for (int s = 0; s < subMeshCount; s++)
            {
                offsets[s] = allVerts.Count;
                allVerts.AddRange(vertsBySub[s]);
                allCols.AddRange(colsBySub[s]);
            }

            var mesh = new Mesh { name = "worship_template", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(allVerts);
            mesh.SetColors(allCols);
            mesh.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
            {
                var t = trisBySub[s].ToArray();
                for (int i = 0; i < t.Length; i++) t[i] += offsets[s];
                GeoFrame.FlipWinding(t); // mirrored X above -> flip every submesh's winding
                mesh.SetTriangles(t, s);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildChurchTemplate()
        {
            var parts = new List<Part>();
            void Add((Vector3[] v, int[] t) prim, Color col, int sub, float x, float y, float z, float rx = 0f, float rz = 0f, float ry = 0f)
                => parts.Add(Xform(new Part { Verts = prim.v, Tris = prim.t, Col = col, Sub = sub }, x, y, z, rx, rz, ry));

            const int S0 = 0, S1 = 1, S2 = 2; // stone walls / arch drum / coloured roof bits — citymap.js:1048-1052

            // ---- cross-plan walls + crossing tower — citymap.js:1005-1010 ----
            Add(Box(20, 7.5f, 9), Stone, S0, 0, 3.75f, 0);
            Add(Box(9, 7.5f, 16), Stone, S0, 0, 3.75f, 0);
            Add(Box(9.5f, 11.5f, 9.5f), Stone, S0, 0, 5.75f, 0);

            // ---- walled churchyard, west gate gap at k==8 — citymap.js:1011-1021 ----
            for (int k = 0; k < 16; k++)
            {
                if (k == 8) continue;
                float a = (k / 16f) * Mathf.PI * 2f;
                Add(Box(5.95f, 1.7f, 0.5f), Stone, S0, Mathf.Cos(a) * 15f, 0.85f, Mathf.Sin(a) * 15f, 0f, 0f, -(a + Mathf.PI / 2f));
            }
            foreach (var s in new[] { -1f, 1f })
            {
                float a = Mathf.PI + s * (Mathf.PI / 16f);
                Add(Box(1.1f, 2.5f, 1.1f), Stone, S0, Mathf.Cos(a) * 15f, 1.25f, Mathf.Sin(a) * 15f);
            }

            // ---- drum band — citymap.js:1023-1025 ----
            Add(Cylinder(3.4f, 3.6f, 6.5f, 12, true), Stone, S1, 0, 14.75f, 0);

            // ---- roofs, dome, cross, windows, terrace, cypresses — citymap.js:1026-1047 ----
            Add(Gable(9, 3.6f, 20), Roof, S2, 0, 7.5f, 0, 0f, 0f, Mathf.PI / 2f);
            Add(Gable(9, 3.6f, 16), Roof, S2, 0, 7.5f, 0);
            Add(Cylinder(4, 4, 0.7f, 12, false), Roof, S2, 0, 18.2f, 0);
            Add(Cylinder(3.7f, 3.7f, 0.4f, 12, false), Roof, S2, 0, 11.4f, 0);
            Add(Cone(3.9f, 8.5f, 12), Tent, S2, 0, 22.8f, 0);
            Add(Box(0.3f, 2.6f, 0.3f), Gold, S2, 0, 28.3f, 0);
            Add(Box(1.6f, 0.3f, 0.3f), Gold, S2, 0, 28.6f, 0);
            foreach (var (wx, wz, ry) in new (float, float, float)[] { (10.1f, 0, Mathf.PI / 2f), (-10.1f, 0, Mathf.PI / 2f), (0, 8.1f, 0), (0, -8.1f, 0) })
                Add(Box(1.1f, 3.4f, 0.5f), Win, S2, wx, 4.5f, wz, 0f, 0f, ry);
            Add(Cylinder(14.8f, 14.8f, 0.9f, 16, false), Pave, S2, 0, 0.45f, 0);
            foreach (var (deg, r, h) in new (float, float, float)[]
                     {
                         (28, 12, 6), (64, 11.5f, 5), (116, 12.5f, 6.5f), (152, 11.8f, 5),
                         (206, 12, 6), (244, 12.4f, 5.5f), (296, 11.6f, 6), (332, 12.2f, 5),
                     })
            {
                float a = (deg / 180f) * Mathf.PI;
                Add(Cone(1.35f, h, 7), Cypress, S2, Mathf.Cos(a) * r, h / 2f + 0.3f, Mathf.Sin(a) * r);
            }

            return BuildTemplate(parts, 3);
        }

        static Mesh BuildMosqueTemplate()
        {
            var parts = new List<Part>();
            void Add((Vector3[] v, int[] t) prim, Color col, float x, float y, float z)
                => parts.Add(Xform(new Part { Verts = prim.v, Tris = prim.t, Col = col, Sub = 0 }, x, y, z));

            // citymap.js:1055-1063
            Add(Box(16, 8, 16), MosqueWall, 0, 4, 0);
            Add(Cylinder(6.8f, 7.2f, 1.5f, 16, false), MosqueBase, 0, 8.5f, 0);
            Add(DomeHalf(6.6f, 16, 10), MosqueDome, 0, 9, 0);
            Add(Cone(0.5f, 2, 8), Gold, 0, 16.5f, 0);
            Add(Cylinder(1.1f, 1.4f, 22, 10, false), MosqueWall, 9.5f, 11, 9.5f);
            Add(Cylinder(1.5f, 1.5f, 1.2f, 10, false), MosqueBase, 9.5f, 20, 9.5f);
            Add(Cone(1.6f, 3.5f, 10), MosqueDome, 9.5f, 23.5f, 9.5f);

            return BuildTemplate(parts, 1);
        }
    }
}
