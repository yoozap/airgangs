using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/characters.js:927 <c>_measurePocket</c> — the REAL per-vertex
    /// skinned-mesh ellipse fit for a rifle's shoulder pocket.
    ///
    /// WHY THIS FILE EXISTS SEPARATELY FROM FootPoses.cs: an earlier agent's
    /// Character/FootPoses.cs:1137 <c>RifleShoulder</c> ships a documented,
    /// HONEST approximation (see that method's own header, FootPoses.cs:1120-
    /// 1136): it estimates the pocket from a measured body dimension
    /// (ClimbMetrics.ShoulderHalf, an isotropic radius) instead of scanning the
    /// skinned mesh for a true ellipse. FootPoses.cs is on this port's
    /// DO-NOT-EDIT list (FootProbe depends on it passing exactly as-is), so this
    /// file does not replace that approximation in place — it ports the REAL
    /// measurement characters.js:927-975 actually performs, as new, honestly
    /// separate code, and <see cref="TbilisiFly.Character.CharacterRig.MeasureShoulderPocket"/>
    /// is the new entry point a future pass can wire RifleShoulder up to.
    ///
    /// THE MEASUREMENT (characters.js:927-975): scan every SkinnedMeshRenderer
    /// under the model whose skeleton includes the side's upper-arm bone
    /// (excluding anything named belt/holster/gear), bake its CURRENT (posed)
    /// vertex positions, keep only the ones within an 8 cm band at shoulder
    /// height (in the rig's own locomotion frame, i.e. under the run clip's
    /// lean/bank but not the aeroplane's bank), and for each kept vertex look up
    /// its dominant skin-weight bone (excluding arm/hand/clavicle/shoulder/
    /// finger/head/neck bones — we want the TORSO bone it is really riding on).
    /// The centroid and per-axis max deviation of the kept (x,z) give an
    /// axis-aligned "ellipse" (not a true PCA fit — characters.js does not do
    /// one either, see its own cx/cz/rx/rz lines); the most-voted bone becomes
    /// the frame the result is expressed in, so it rides correctly through that
    /// bone's own animation (characters.js:893-898's comment on why).
    ///
    /// HANDEDNESS: one cross product, in <see cref="Resolve"/> (characters.js:
    /// 913 `crossVectors(a, up)`) — operands SWAPPED to `Cross(up, a)` for
    /// Unity's left-handedness, same pattern as CharacterRig.cs's own
    /// `_turnToward`. Every other transform here is a plain point/direction
    /// carried through Transform/Matrix4x4, which needs no swap (see
    /// CharacterRig.cs's class header on why that category is handedness-safe
    /// as-is).
    /// </summary>
    public static class ShoulderPocket
    {
        /// <summary>characters.js:969-974 — the measured record, kept in the
        /// dominant bone's own local frame so it rides correctly under that
        /// bone's own animation.</summary>
        public struct Pocket
        {
            public bool Valid;
            public Transform Bone;     // characters.js:970 `bone`
            public Vector3 C;          // characters.js:971 `c` — centre, Bone-local
            public Vector3 Ex, Ez, Up; // characters.js:972 — unit axes, Bone-local directions
            public float Rx, Rz;       // characters.js:973 — ellipse half-widths, metres
        }

        const float BandHalfHeight = 0.08f;     // characters.js:949
        const int VertexStride = 2;             // characters.js:947 `i += 2`
        const int MinSamples = 12;              // characters.js:958

        // characters.js:943, 953 — substring, case-insensitive, exactly like the JS regex tests.
        static readonly string[] ExcludeRenderer = { "belt", "holster", "gear" };
        static readonly string[] ExcludeBone =
            { "arm", "hand", "clav", "shoulder", "finger", "thumb", "index", "middle", "pinky", "ring", "head", "neck" };

        /// <summary>characters.js:927 _measurePocket(side). `side` is "L" or "R".</summary>
        public static Pocket Measure(CharacterRig rig, string side)
        {
            var result = new Pocket();
            if (rig == null || rig.Anim == null || !rig.IsHumanoid) return result;
            var up = rig.Anim.GetBoneTransform(side == "L" ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            if (up == null) return result;

            // characters.js:935 `frameObj = this.anim ?? this.act ?? this.obj` — the
            // locomotion layer: under the run clip's own lean/bank, above an
            // aeroplane's bank (if seated). AnimGroup is CharacterRig's equivalent.
            Transform frameObj = rig.AnimGroup != null ? rig.AnimGroup : rig.Root;
            Matrix4x4 invFrame = frameObj.worldToLocalMatrix;
            float shoulderY = invFrame.MultiplyPoint3x4(up.position).y;

            var xs = new List<float>();
            var zs = new List<float>();
            var counts = new Dictionary<Transform, int>();

            foreach (var smr in rig.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (ContainsAny(smr.name, ExcludeRenderer)) continue;
                Transform[] bones = smr.bones;
                if (bones == null || System.Array.IndexOf(bones, up) < 0) continue;
                Mesh shared = smr.sharedMesh;
                if (shared == null) continue;
                BoneWeight[] weights = shared.boneWeights;
                if (weights == null || weights.Length == 0) continue;

                var baked = new Mesh();
                smr.BakeMesh(baked, true); // posed vertices, in smr.transform's LOCAL space
                Vector3[] verts = baked.vertices;
                // world = smr.transform.localToWorldMatrix * local; frame-local = invFrame * world
                Matrix4x4 toFrame = invFrame * smr.transform.localToWorldMatrix;

                int n = Mathf.Min(verts.Length, weights.Length);
                for (int i = 0; i < n; i += VertexStride)
                {
                    Vector3 p = toFrame.MultiplyPoint3x4(verts[i]);
                    if (Mathf.Abs(p.y - shoulderY) > BandHalfHeight) continue; // characters.js:949
                    BoneWeight bw = weights[i];
                    int bi = bw.boneIndex0; float bwgt = bw.weight0;          // characters.js:950-951
                    if (bw.weight1 > bwgt) { bwgt = bw.weight1; bi = bw.boneIndex1; }
                    if (bw.weight2 > bwgt) { bwgt = bw.weight2; bi = bw.boneIndex2; }
                    if (bw.weight3 > bwgt) { bwgt = bw.weight3; bi = bw.boneIndex3; }
                    if (bi < 0 || bi >= bones.Length) continue;
                    Transform bone = bones[bi];
                    if (bone == null || ContainsAny(bone.name, ExcludeBone)) continue;    // characters.js:953
                    counts[bone] = counts.TryGetValue(bone, out int c0) ? c0 + 1 : 1;     // characters.js:954
                    xs.Add(p.x); zs.Add(p.z);                                             // characters.js:955
                }
                Object.DestroyImmediate(baked);
            }

            if (xs.Count < MinSamples) return result; // characters.js:958

            float cx = 0f, cz = 0f;
            for (int i = 0; i < xs.Count; i++) { cx += xs[i]; cz += zs[i]; }
            cx /= xs.Count; cz /= xs.Count;                                       // characters.js:959-961
            float rx = 0f, rz = 0f;
            for (int i = 0; i < xs.Count; i++)
            {
                rx = Mathf.Max(rx, Mathf.Abs(xs[i] - cx));
                rz = Mathf.Max(rz, Mathf.Abs(zs[i] - cz));
            }                                                                    // characters.js:962-963

            Transform domBone = null; int best = -1;
            foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; domBone = kv.Key; } // characters.js:964-965
            if (domBone == null) return result;

            Matrix4x4 binv = domBone.worldToLocalMatrix;
            Matrix4x4 frameDirToBone = binv * frameObj.localToWorldMatrix; // linear part used for directions only

            Vector3 cFrame = new Vector3(cx, shoulderY, cz);
            Vector3 cWorld = frameObj.localToWorldMatrix.MultiplyPoint3x4(cFrame);
            Vector3 cBone = binv.MultiplyPoint3x4(cWorld);                        // characters.js:971

            result.Valid = true;
            result.Bone = domBone;
            result.C = cBone;
            result.Ex = frameDirToBone.MultiplyVector(Vector3.right).normalized;  // characters.js:972 toBoneDir(1,0,0)
            result.Ez = frameDirToBone.MultiplyVector(Vector3.forward).normalized; // toBoneDir(0,0,1)
            result.Up = frameDirToBone.MultiplyVector(Vector3.up).normalized;      // toBoneDir(0,1,0)
            result.Rx = Mathf.Max(0.05f, rx);                                      // characters.js:973
            result.Rz = Mathf.Max(0.05f, rz);
            return result;
        }

        static bool ContainsAny(string name, string[] needles)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var s in needles) if (n.Contains(s)) return true;
            return false;
        }

        public const float PocketAhead = 0.22f;  // characters.js:385 POCKET_AHEAD
        public const float PocketProud = 0.015f; // characters.js:386 POCKET_PROUD
        public const float PocketDrop = 0.05f;   // characters.js:387 POCKET_DROP

        /// <summary>characters.js:888-925 rifleShoulder's per-frame read-out, given an
        /// already-<see cref="Measure"/>d pocket. `aim` is world-space (Y ignored);
        /// null falls back to the rig's own facing. Returns null if the pocket was
        /// never measured (no skinned mesh band found, e.g. too few vertices).</summary>
        public static Vector3? Resolve(CharacterRig rig, in Pocket pocket, string side, Vector3? aim)
        {
            if (!pocket.Valid || pocket.Bone == null) return null;
            Transform b = pocket.Bone;
            Vector3 exW = b.TransformDirection(pocket.Ex);
            Vector3 ezW = b.TransformDirection(pocket.Ez);
            Vector3 upW = b.TransformDirection(pocket.Up);

            Transform an = rig.AnimGroup != null ? rig.AnimGroup : rig.Root;
            Vector3 fwd = an.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f) fwd = new Vector3(0f, 0f, -1f);
            fwd.Normalize();
            Vector3 a = aim.HasValue && aim.Value.sqrMagnitude > 1e-8f ? new Vector3(aim.Value.x, 0f, aim.Value.z) : fwd;
            if (a.sqrMagnitude < 1e-8f) a = fwd;
            a.Normalize();

            // HANDEDNESS: characters.js:913 crossVectors(a, up) -> Cross(up, a), operands
            // swapped (see class header; CharacterRig.cs's _turnToward is the same pattern).
            Vector3 sideDir = Vector3.Cross(Vector3.up, a).normalized;
            if (side == "L") sideDir = -sideDir;

            Vector3 d = sideDir * Mathf.Cos(PocketAhead) + a * Mathf.Sin(PocketAhead); // characters.js:915
            float dx = Vector3.Dot(d, exW), dz = Vector3.Dot(d, ezW);                   // characters.js:917
            float dl = Mathf.Max(1e-6f, Mathf.Sqrt(dx * dx + dz * dz));
            dx /= dl; dz /= dl;
            float r = 1f / Mathf.Sqrt((dx / pocket.Rx) * (dx / pocket.Rx) + (dz / pocket.Rz) * (dz / pocket.Rz)); // :920

            Vector3 centerWorld = b.TransformPoint(pocket.C);
            return centerWorld
                + exW * (dx * (r + PocketProud))
                + ezW * (dz * (r + PocketProud))
                - upW * PocketDrop;                                                    // characters.js:921-923
        }
    }
}
