using System.Reflection;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Fx;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Pass-4 sweep: the three units yesterday's wiring left with NO paired
    /// oracle at all. Counterpart: tools/_probe_pass4extras.mjs, which reads
    /// every JS literal out of src/guns.js / src/rockets.js / src/characters.js
    /// / src/foot.js by regex rather than retyping it.
    ///
    ///   Combat.GunPod           vs src/guns.js:393-417
    ///   Fx.RocketBodyFx         vs src/rockets.js:147-187
    ///   Character.RigSeatExtras.SetStrafe
    ///                           vs src/characters.js:570-579, driven the way
    ///                              src/foot.js:6035-6057 drives it (and the
    ///                              way FootController.cs:1166-1185 now does)
    ///
    /// FRAME: all three are MODEL-LOCAL props, so GeoFrame's world mirror and
    /// winding flip do not apply (Combat/ProceduralPropMesh.cs's own header
    /// says so). The one convention that does is Three's -Z-forward vs Unity's
    /// +Z-forward, and the JS side prints its literals already flipped, so a
    /// mismatch below is a geometry disagreement, not a convention one.
    ///
    /// The cylinder radii are deliberately printed as rMuzzle/rAft rather than
    /// top/bottom: THREE.CylinderGeometry puts radiusTop at +Y, and the JS's
    /// `rotation.x = Math.PI / 2` sends +Y to +Z, which in Three's -Z-forward
    /// frame is AFT. Printing top/bottom would let the two sides "agree" on
    /// two numbers while putting the wide end at opposite ends of the pod.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.Pass4ExtrasProbe.Run
    /// </summary>
    public static class Pass4ExtrasProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            GunPodFacts();
            RocketFacts();
            StrafeFacts();
        }

        static string F(float v, int n = 6) => v.ToString("F" + n);

        // ------------------------------------------------------------------
        static void GunPodFacts()
        {
            var ml = GunPod.MuzzleLocal;
            Debug.Log($"GUNPOD_MUZZLE_LOCAL x={F(ml.x)} y={F(ml.y)} z={F(ml.z)}");

            // MuzzleWorld on a real Transform at the JS probe's own test pose
            // (game frame (-1200.5, 640.25, -800.75) yaw +0.6 rad -> Unity
            // (1200.5, 640.25, 800.75) yaw -0.6 rad, GeoFrame.ToUnity /
            // YawToUnity).
            var go = new GameObject("pass4_plane");
            try
            {
                go.transform.position = new Vector3(1200.5f, 640.25f, 800.75f);
                go.transform.rotation = Quaternion.Euler(0f, -0.6f * Mathf.Rad2Deg, 0f);
                Vector3 w = GunPod.MuzzleWorld(go.transform);
                Debug.Log($"GUNPOD_MUZZLE_WORLD x={F(w.x, 4)} y={F(w.y, 4)} z={F(w.z, 4)}");
            }
            finally { Object.DestroyImmediate(go); }

            // The pod hierarchy, measured off the REAL built GameObjects rather
            // than re-read from the constants, so a wiring slip shows up too.
            var plane = new GameObject("pass4_pod_host");
            try
            {
                var pod = GunPod.Build(plane.transform);
                var p = pod.transform;
                Debug.Log($"GUNPOD_POD_LOCAL x={F(p.localPosition.x)} y={F(p.localPosition.y)} z={F(p.localPosition.z)}");

                MeasureCyl(p, "pod_body", "GUNPOD_BODY");
                MeasureCyl(p, "pod_barrel", "GUNPOD_BARREL");
                MeasureCyl(p, "pod_brake", "GUNPOD_BRAKE");

                var body = p.Find("pod_body");
                if (body != null)
                {
                    var b = body.GetComponent<MeshFilter>().sharedMesh.bounds;
                    Debug.Log($"GUNPOD_BODY_ENDS zAft={F(b.min.z)} zMuzzle={F(b.max.z)}");
                }
                var barrel = p.Find("pod_barrel");
                if (barrel != null) Debug.Log($"GUNPOD_BARREL_Z z={F(barrel.localPosition.z)}");
                var brake = p.Find("pod_brake");
                if (brake != null) Debug.Log($"GUNPOD_BRAKE_Z z={F(brake.localPosition.z)}");

                // the two struts, in the order Build() adds them
                var zs = new System.Collections.Generic.List<float>();
                Bounds sb = default; bool haveSb = false;
                foreach (Transform c in p)
                {
                    if (c.name != "pod_strut") continue;
                    zs.Add(c.localPosition.z);
                    if (!haveSb) { sb = c.GetComponent<MeshFilter>().sharedMesh.bounds; haveSb = true; }
                    if (zs.Count == 1)
                        Debug.Log($"GUNPOD_STRUT w={F(sb.size.x)} h={F(sb.size.y)} d={F(sb.size.z)} y={F(c.localPosition.y)}");
                }
                if (zs.Count >= 2) Debug.Log($"GUNPOD_STRUT_Z z0={F(zs[0])} z1={F(zs[1])}");
            }
            finally { Object.DestroyImmediate(plane); }
        }

        /// <summary>A capped cylinder built along local Z: the muzzle end is the
        /// +Z ring and the aft end the -Z ring, so the radius at each end is the
        /// max |x| over the vertices sitting on that ring.</summary>
        static void MeasureCyl(Transform parent, string child, string key)
        {
            var t = parent.Find(child);
            if (t == null) { Debug.Log($"{key} missing=1"); return; }
            var mesh = t.GetComponent<MeshFilter>().sharedMesh;
            var v = mesh.vertices;
            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var q in v) { if (q.z < zMin) zMin = q.z; if (q.z > zMax) zMax = q.z; }
            float rAft = 0f, rMuzzle = 0f; int nAft = 0, nMuzzle = 0;
            foreach (var q in v)
            {
                float r = Mathf.Sqrt(q.x * q.x + q.y * q.y);
                if (Mathf.Abs(q.z - zMin) < 1e-4f) { rAft = Mathf.Max(rAft, r); nAft++; }
                if (Mathf.Abs(q.z - zMax) < 1e-4f) { rMuzzle = Mathf.Max(rMuzzle, r); nMuzzle++; }
            }
            // sides: the ring vertex count, minus the cap centre if it is there
            int sides = Mathf.Max(nAft, nMuzzle);
            if (rAft > 1e-5f && rMuzzle > 1e-5f) sides -= 1; // one cap centre on each ring
            Debug.Log($"{key} rMuzzle={F(rMuzzle)} rAft={F(rAft)} len={F(zMax - zMin)} sides={sides}");
        }

        // ------------------------------------------------------------------
        static void RocketFacts()
        {
            var fx = new RocketBodyFx();
            var body = fx.BodyMesh;
            var bb = body.bounds;
            Debug.Log($"ROCKET_BODY r={F(Mathf.Max(bb.extents.x, bb.extents.y))} len={F(bb.size.z)} sides={RingCount(body, bb.max.z)}");
            Debug.Log($"ROCKET_BODY_ENDS zTail={F(bb.min.z)} zNoseJoin={F(bb.max.z)}");

            // MagentaMesh is the nose cone AND the 4 fins merged into one mesh
            // (rockets.js:157 reuses noseMat for both), so a "widest ring behind
            // the tip" scan over its vertices picks up a FIN, not the cone's
            // base: the first version of this probe read r=0.350143 len=1.96
            // and looked like a port defect when it was the measurement that
            // was wrong. The cone's own dimensions therefore come off the
            // constants it is built from, and the MEASURED tip Z stays as the
            // independent check that those constants reached the mesh.
            var Tr = typeof(RocketBodyFx);  // private consts, same reflection the fin block below uses
            float noseLen = ConstF(Tr, "NoseLen");
            float noseCz = ConstF(Tr, "NoseCenterZ");
            float bodyR = ConstF(Tr, "BodyR");
            int bodySides = (int)Tr.GetField("BodySides", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            var mag = fx.MagentaMesh;
            float tipZ = float.MinValue;
            foreach (var q in mag.vertices) if (q.z > tipZ) tipZ = q.z;
            Debug.Log($"ROCKET_NOSE r={F(bodyR)} len={F(noseLen)} sides={bodySides}");
            Debug.Log($"ROCKET_NOSE_Z zTip={F(tipZ)} zBase={F(noseCz - noseLen / 2f)}");

            // the fin block, read back off the shared constants the mesh is
            // built from (private, so by reflection rather than re-derived)
            var T = typeof(RocketBodyFx);
            float finR = ConstF(T, "FinRadius"), finZ = ConstF(T, "FinZ");
            float radial = ConstF(T, "FinRadial"), tang = ConstF(T, "FinTangent"), depth = ConstF(T, "FinDepth");
            int n = (int)T.GetField("FinCount", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Debug.Log($"ROCKET_FIN n={n} radius={F(finR)} z={F(finZ)} radial={F(radial)} tangential={F(tang)} depth={F(depth)}");
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + Mathf.PI / 4f;
                Debug.Log($"ROCKET_FIN_{k} angDeg={F(a * Mathf.Rad2Deg, 4)} x={F(Mathf.Cos(a) * finR, 5)} " +
                          $"y={F(Mathf.Sin(a) * finR, 5)} z={F(finZ, 5)}");
            }
            Debug.Log($"ROCKET_GLOW z={F(ConstF(T, "GlowZ"))} scale={F(ConstF(T, "GlowScale"))}");
        }

        static float ConstF(System.Type t, string name)
        {
            var fi = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            return fi == null ? float.NaN : (float)fi.GetRawConstantValue();
        }

        static int RingCount(Mesh m, float z)
        {
            int n = 0;
            foreach (var q in m.vertices)
                if (Mathf.Abs(q.z - z) < 1e-4f && (q.x * q.x + q.y * q.y) > 1e-8f) n++;
            return n;
        }

        // ------------------------------------------------------------------
        static void StrafeFacts()
        {
            // the rate constant and the blend table, read off the port itself
            float k = (float)typeof(RigSeatExtras)
                .GetField("StrafeRate", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
                .GetRawConstantValue();
            Debug.Log($"STRAFE_RATE k={F(k, 1)}");
            // FootController.cs:1168 `sb = Armed ? (IsRifleHeld ? 0.35 : 0.6) : 1`
            // and :1177 `sb = 0.15` in cover (foot.js:298 COVER_SHUFFLE). Those
            // are the four blends the real caller can pass.
            Debug.Log($"STRAFE_BLEND rifle={F(0.35f, 3)} pistol={F(0.6f, 3)} bare={F(1f, 3)} cover={F(0.15f, 3)}");

            // Drive the REAL RigSeatExtras.SetStrafe on a real CharacterRig and
            // read its accumulator back by reflection (the Euler write would go
            // through a quaternion and lose the low digits).
            string path = "Assets/Resources/Art/Characters/banana.fbx";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.Log("STRAFE_RAMP missing_body=1"); return; }
            var acc = typeof(RigSeatExtras).GetField("_strafeY", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (System.Collections.Generic.Dictionary<CharacterRig, float>)acc.GetValue(null);

            foreach (var (tag, blend) in new[] { ("bare", 1f), ("pistol", 0.6f), ("rifle", 0.35f), ("cover", 0.15f) })
            {
                var rig = Fresh(prefab, dict, out var inst);
                try
                {
                    for (int i = 0; i < 20; i++) RigSeatExtras.SetStrafe(rig, 0.9f, 1f / 60f, blend);
                    Debug.Log($"STRAFE_RAMP id={tag} blend={F(blend, 3)} y20={F(dict[rig])}");
                }
                finally { Object.DestroyImmediate(inst); dict.Remove(rig); }
            }
            // the shortest-arc wrap: 3.0 rad to -3.0 rad must go the SHORT way
            {
                var rig = Fresh(prefab, dict, out var inst);
                try
                {
                    dict[rig] = 3.0f;
                    RigSeatExtras.SetStrafe(rig, -3.0f, 1f / 60f, 1f);
                    Debug.Log($"STRAFE_WRAP from={F(3.0f)} to={F(-3.0f)} y1={F(dict[rig])}");
                }
                finally { Object.DestroyImmediate(inst); dict.Remove(rig); }
            }
            // blend 0 (a walk clip is running) must ease the offset back to centre
            {
                var rig = Fresh(prefab, dict, out var inst);
                try
                {
                    for (int i = 0; i < 20; i++) RigSeatExtras.SetStrafe(rig, 0.9f, 1f / 60f, 1f);
                    for (int i = 0; i < 20; i++) RigSeatExtras.SetStrafe(rig, 0.9f, 1f / 60f, 0f);
                    Debug.Log($"STRAFE_WALK_RECENTRE y40={F(dict[rig])}");
                }
                finally { Object.DestroyImmediate(inst); dict.Remove(rig); }
            }
            // NaN in, centre out (characters.js:571's Number.isFinite guard)
            {
                var rig = Fresh(prefab, dict, out var inst);
                try
                {
                    dict[rig] = 0.4f;
                    RigSeatExtras.SetStrafe(rig, float.NaN, 1f / 60f, 1f);
                    Debug.Log($"STRAFE_NAN_GUARD y1={F(dict[rig])}");
                }
                finally { Object.DestroyImmediate(inst); dict.Remove(rig); }
            }
        }

        static CharacterRig Fresh(GameObject prefab,
            System.Collections.Generic.Dictionary<CharacterRig, float> dict, out GameObject inst)
        {
            inst = Object.Instantiate(prefab);
            var rig = new CharacterRig(inst, "banana");
            dict.Remove(rig);
            return rig;
        }
    }
}
