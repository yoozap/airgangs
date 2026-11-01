using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// 42 real Tbilisi malls & market halls, Blender-built on their exact OSM
    /// footprints (tools/build_malls.py -> malls.glb -> Assets/Resources/Art/Props/malls.fbx,
    /// one child object per mall named "mall_&lt;bid&gt;"). Ported from
    /// src/malls.js. Each mall is a destructible landmark with the REAL name on
    /// a lit sign over its entrance — same category as parliament or the hotels.
    ///
    /// ASSET ALREADY IMPORTED: unity/Assets/Resources/Art/Props/malls.fbx exists (git
    /// status shows it). PLACE IT — do not approximate with a primitive box.
    ///
    /// Pure data class: no GameObject/Instantiate here (see TerrainBuilder for
    /// the established style). A consumer finds the child Transform named
    /// "mall_{bid}" inside the malls.fbx prefab for each <see cref="Placement"/>
    /// and sets its world position/rotation from this data (no scale/rotation
    /// change needed — see HANDEDNESS note below).
    ///
    /// HANDEDNESS, position: tools/build_malls.py's own header says the mesh is
    /// "authored directly in the GAME frame... runtime places each object at
    /// (x, groundY, z) with NO rotation" (src/malls.js:68, root.position.set,
    /// no root.rotation.y anywhere). So the FBX body needs GeoFrame.ToUnity on
    /// the position and NOTHING on rotation — the footprint shape is baked in
    /// and self-corrects through the Blender FBX export pipeline exactly like
    /// any other footprint-derived mesh (GeoFrame.cs point 2).
    ///
    /// HANDEDNESS, the sign plate: malls.json carries no entrance-anchor field
    /// (that lives as a Blender custom property "sign" on each mall_&lt;bid&gt;
    /// object, tools/build_malls.py:192-193) which Blender's bare
    /// export_scene.fbx call (tools/glb_batch_fbx.py, no custom-properties flag)
    /// does not carry into the FBX. So the anchor is RECOMPUTED here, straight
    /// from malls.json's own "poly" ring, by the identical longest-edge +
    /// outward-normal algorithm tools/build_malls.py:142-162 uses (ed=5.0 etc.)
    /// — this time directly in the GAME (x,z) frame, skipping Blender's own
    /// y = -z bookkeeping, which is internal to that script and not part of the
    /// real-world geometry. The resulting yaw IS an object-rotation constant
    /// (it will spin an independently-placed sign plate, not a mesh whose own
    /// vertices were mirrored), so it goes through GeoFrame.YawToUnity, same as
    /// a metro.json yaw or a planes.js hand-tuned yaw.
    /// </summary>
    public static class MallPlacer
    {
        public struct Placement
        {
            public int Bid;
            public string ChildName;   // "mall_{bid}" — the name to find inside malls.fbx
            public string Name;        // real Georgian name, byte-for-byte from malls.json
            public string DisplayName; // Name, or "Name 2"/"Name 3"... for duplicate names
            public Vector3 Position;   // Unity space, body root (no rotation applied)
            public Vector3 SignPosition; // Unity space, entrance sign-plate centre
            public float SignYawUnity;   // radians, faces outward from the entrance wall
            public float SignWidth;      // src/malls.js:74 — min(19, 3 + name.length*0.85)
        }

        public readonly struct Result
        {
            public readonly List<Placement> Malls;
            public readonly int TotalInJson;
            public Result(List<Placement> malls, int totalInJson) { Malls = malls; TotalInJson = totalInJson; }
        }

        public static Result Build(string streamingRoot = null, TerrainSampler terrain = null)
        {
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            string path = Path.Combine(streamingRoot, "malls.json");
            var result = new List<Placement>();
            if (!File.Exists(path))
            {
                Debug.LogWarning($"MALLS missing {path}");
                return new Result(result, 0);
            }

            var arr = JArray.Parse(File.ReadAllText(path));
            var dupes = new Dictionary<string, int>();

            foreach (var e in arr)
            {
                int bid = e["bid"].Value<int>();
                string name = (string)e["name"];
                float x = e["x"].Value<float>();
                float z = e["z"].Value<float>();
                float h = e["h"].Value<float>();

                float groundY = terrain != null ? terrain.HeightAtGame(x, z) : 0f;
                Vector3 bodyPos = GeoFrame.ToUnity(x, groundY + 0.35f, z); // src/malls.js:67-68

                // ring of LOCAL (game-frame) offsets around the mall's own origin
                var poly = (JArray)e["poly"];
                var ring = new Vector2[poly.Count];
                for (int i = 0; i < poly.Count; i++)
                {
                    var p = (JArray)poly[i];
                    ring[i] = new Vector2(p[0].Value<float>(), p[1].Value<float>());
                }

                ComputeSignAnchor(ring, h, out Vector2 signLocal, out float signLocalY, out float angGame);

                Vector3 signWorldGame = new Vector3(x + signLocal.x, groundY + 0.35f + signLocalY, z + signLocal.y);
                Vector3 signPos = GeoFrame.ToUnity(signWorldGame.x, signWorldGame.y, signWorldGame.z);
                float signYawUnity = GeoFrame.YawToUnity(angGame);

                int cnt = dupes.TryGetValue(name, out var c) ? c + 1 : 1;
                dupes[name] = cnt;
                string display = cnt > 1 ? $"{name} {cnt}" : name; // src/malls.js:82-84

                result.Add(new Placement
                {
                    Bid = bid,
                    ChildName = $"mall_{bid}",
                    Name = name,
                    DisplayName = display,
                    Position = bodyPos,
                    SignPosition = signPos,
                    SignYawUnity = signYawUnity,
                    SignWidth = Mathf.Min(19f, 3f + name.Length * 0.85f), // src/malls.js:74
                });
            }

            Debug.Log($"MALLS placed={result.Count}/{arr.Count} {ArtAssets.Tag}");
            return new Result(result, arr.Count);
        }

        /// <summary>
        /// tools/build_malls.py:142-162 + 192-193, re-derived from malls.json's
        /// own "poly" ring instead of a baked Blender custom property (see the
        /// class doc). ring is in GAME-frame LOCAL (x,z) metres. Returns the
        /// sign plate's local (x,z) offset, local height above the mall's own
        /// ground reference, and its outward-facing yaw (GAME-frame radians,
        /// atan2(nx,nz) convention — same as src/parks.js faceNearestPath and
        /// src/shopfronts.js's wall.ang).
        /// </summary>
        static void ComputeSignAnchor(Vector2[] ring, float mallH, out Vector2 signLocal, out float signLocalY, out float angGame)
        {
            int n = ring.Length;
            float hBuilding = Mathf.Min(26f, Mathf.Max(8f, mallH)); // tools/build_malls.py:135

            // longest edge — the entrance atrium goes on it
            int bi = 0;
            float best = -1f;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float l2 = (ring[j].x - ring[i].x) * (ring[j].x - ring[i].x)
                         + (ring[j].y - ring[i].y) * (ring[j].y - ring[i].y);
                if (l2 > best) { best = l2; bi = i; }
            }
            Vector2 a = ring[bi], b = ring[(bi + 1) % n];
            float ex = b.x - a.x, ez = b.y - a.y;
            float len = Mathf.Sqrt(ex * ex + ez * ez);
            float ux = ex / len, uz = ez / len;

            // outward perpendicular, self-corrected against the ring centroid —
            // the rotation direction chosen here doesn't matter (see MetroBuilder/
            // ShopfrontBuilder docs): whichever of the two perpendiculars we start
            // with, the centroid check below flips it to the true outward one.
            float nx = -uz, nz = ux;
            float cx = 0f, cz = 0f;
            for (int i = 0; i < n; i++) { cx += ring[i].x; cz += ring[i].y; }
            cx /= n; cz /= n;
            float mx = a.x + ex * 0.5f, mz = a.y + ez * 0.5f;
            if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }

            const float ed = 5.0f; // tools/build_malls.py ed
            float eh = Mathf.Min(hBuilding * 0.8f, 12.0f);

            signLocal = new Vector2(mx + nx * (ed + 0.6f), mz + nz * (ed + 0.6f));
            signLocalY = eh - 1.1f;
            angGame = Mathf.Atan2(nx, nz);
        }

        /// <summary>
        /// Editor-only coverage check: how many of the placed bids actually have
        /// a child transform named "mall_{bid}" inside the imported FBX. Lets
        /// the probe report real numbers instead of assuming the FBX matches
        /// malls.json 1:1.
        /// </summary>
        public static (int found, int total) CheckAssetCoverage(List<Placement> placements, string fbxPath = "Assets/Resources/Art/Props/malls.fbx")
        {
            var go = ArtAssets.Load<GameObject>(fbxPath);
            if (go == null) return (0, placements.Count);
            var names = new HashSet<string>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
            int found = 0;
            foreach (var p in placements) if (names.Contains(p.ChildName)) found++;
            return (found, placements.Count);
        }
    }
}
