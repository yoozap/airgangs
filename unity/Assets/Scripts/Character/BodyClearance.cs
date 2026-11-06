// Keeps the hero's hands and elbows OUTSIDE the fruit.
//
// The purchased clips (Threepeat parkour, Cover+Shoot locomotion) were authored
// for a slim human; our bodies are wide fruit, so an authored arm pose puts the
// hand inside the body and it vanishes (user, 2026-11-01 22:50: "sometimes they
// are inside body and i dont see them when i am moving"). MEASURED on the apple
// 2026-11-02: the resting hand reads 0.26 m inside the hull, the sprint loop's
// worst frame 0.59 m. No shoulder rotation can undo that fully, so this layer
// is a compromise and is deliberately bounded.
//
// BodyHull fits the fruit once at spawn: the skinned mesh is baked in its bind
// pose, the vertices whose dominant bone is the fruit's own (spine_02 in the
// builder's rig) go into that bone's local space, and a 32x16 radial map keeps
// the max radius per azimuth/elevation bin. Radius() reads it BILINEARLY — it
// used to take the max over a 3x3 bin neighbourhood, +-11 deg in both angles,
// which rounded off every concavity and inflated the fruit toward its widest
// nearby radius, so an arm lying along the skin read as buried.
//
// BodyClearance runs after every other arm writer and swings the arm RIGIDLY
// about its shoulder — the clip's own elbow and wrist angles are untouched, so
// the authored articulation survives and only the arm's direction moves. The
// smallest swing that clears the hand, the elbow and the forearm's midpoint
// wins; nothing is required of the upper arm's own midpoint, which a shoulder
// rotation cannot move. Bounds that matter, all learned from measurement:
//   * MaxSwingDeg 45, not 120. At 120 the solver had enough authority to look
//     wrong, and when nothing cleared it SLAMMED to the maximum: measured
//     meanClearDeg=120 in every phase including standing still, which is what
//     the user saw as "my hands are not same physic like in parkour assets".
//   * When nothing clears, keep the angle that leaves the LEAST depth.
//   * DeadZoneM: a hand a few centimetres in is left exactly as authored.
// The rig helps too — tools/build_veggies.py now puts the shoulder pivot
// SHOULDER_OUT past the skin with a ball covering the join, which cut the
// poses that start buried from 19,095 to 15,357 of 23,100 sampled.
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    public sealed class BodyHull
    {
        public Transform Bone;          // the fruit rides this bone
        public Vector3 Centre;          // in Bone's local space
        public int Az = 32, El = 16;
        public float[] R;               // [el * Az + az] max radius, bone-local units
        public float Rmax;
        public int FruitVerts;

        /// <summary>Fit from a skinned mesh in its CURRENT pose (call at spawn,
        /// bind pose). Picks the bone that carries the most vertices as the
        /// fruit (the limbs are tubes on their own bones).</summary>
        public static BodyHull Fit(SkinnedMeshRenderer smr, Transform preferBone = null)
        {
            if (smr == null || smr.sharedMesh == null) return null;
            var mesh = smr.sharedMesh;
            var weights = mesh.boneWeights;
            var bones = smr.bones;
            if (weights == null || weights.Length != mesh.vertexCount || bones == null || bones.Length == 0) return null;
            // dominant bone per vertex
            var count = new int[bones.Length];
            var dom = new int[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                var w = weights[i];
                int b = w.boneIndex0; float bw = w.weight0;
                if (w.weight1 > bw) { b = w.boneIndex1; bw = w.weight1; }
                if (w.weight2 > bw) { b = w.boneIndex2; bw = w.weight2; }
                if (w.weight3 > bw) { b = w.boneIndex3; bw = w.weight3; }
                dom[i] = b; if (b >= 0 && b < count.Length) count[b]++;
            }
            int fruit = -1;
            if (preferBone != null)
                for (int b = 0; b < bones.Length; b++) if (bones[b] == preferBone) fruit = b;
            if (fruit < 0)
            {
                int best = -1;
                for (int b = 0; b < count.Length; b++) if (count[b] > best) { best = count[b]; fruit = b; }
            }
            if (fruit < 0 || bones[fruit] == null) return null;
            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var verts = baked.vertices;
            var bone = bones[fruit];
            var pts = new List<Vector3>(count[fruit]);
            for (int i = 0; i < verts.Length && i < dom.Length; i++)
            {
                if (dom[i] != fruit) continue;
                var wp = smr.transform.TransformPoint(verts[i]);
                pts.Add(bone.InverseTransformPoint(wp));
            }
            Object.DestroyImmediate(baked);
            if (pts.Count < 50) return null;
            var hull = new BodyHull { Bone = bone, FruitVerts = pts.Count };
            Vector3 c = Vector3.zero; foreach (var p in pts) c += p; c /= pts.Count;
            hull.Centre = c;
            hull.R = new float[hull.Az * hull.El];
            foreach (var p in pts)
            {
                var d = p - c; float r = d.magnitude; if (r < 1e-6f) continue;
                hull.Bin(d / r, out int az, out int el);
                int k = el * hull.Az + az; if (r > hull.R[k]) hull.R[k] = r;
                if (r > hull.Rmax) hull.Rmax = r;
            }
            // fill empty bins from their neighbours so a thin stalk column never reads as a hole
            for (int pass = 0; pass < 3; pass++)
                for (int el = 0; el < hull.El; el++)
                    for (int az = 0; az < hull.Az; az++)
                    {
                        int k = el * hull.Az + az; if (hull.R[k] > 0f) continue;
                        float m = 0f;
                        for (int de = -1; de <= 1; de++) for (int da = -1; da <= 1; da++)
                        {
                            int e2 = Mathf.Clamp(el + de, 0, hull.El - 1), a2 = (az + da + hull.Az) % hull.Az;
                            m = Mathf.Max(m, hull.R[e2 * hull.Az + a2]);
                        }
                        hull.R[k] = m;
                    }
            return hull;
        }

        void Bin(Vector3 d, out int az, out int el)
        {
            float a = Mathf.Atan2(d.z, d.x); if (a < 0f) a += 2f * Mathf.PI;
            az = Mathf.Clamp((int)(a / (2f * Mathf.PI) * Az), 0, Az - 1);
            float e = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)); // -pi/2..pi/2
            el = Mathf.Clamp((int)((e + Mathf.PI * 0.5f) / Mathf.PI * El), 0, El - 1);
        }

        /// <summary>Hull radius toward a bone-local unit direction, bilinear
        /// between the four surrounding bins. It used to be the MAX over a 3x3
        /// bin neighbourhood — +-11 deg in both angles — which rounds every
        /// concavity off and inflates the fruit toward its widest nearby
        /// radius, so an arm lying along the skin read as buried and the solver
        /// swung it out. Interpolating follows the real silhouette.</summary>
        public float Radius(Vector3 dirLocal)
        {
            float a = Mathf.Atan2(dirLocal.z, dirLocal.x); if (a < 0f) a += 2f * Mathf.PI;
            float fa = a / (2f * Mathf.PI) * Az - 0.5f;
            float e = Mathf.Asin(Mathf.Clamp(dirLocal.y, -1f, 1f));
            float fe = (e + Mathf.PI * 0.5f) / Mathf.PI * El - 0.5f;
            int a0 = Mathf.FloorToInt(fa), e0 = Mathf.FloorToInt(fe);
            float ta = fa - a0, te = Mathf.Clamp01(fe - e0);
            int a1 = ((a0 + 1) % Az + Az) % Az; a0 = ((a0 % Az) + Az) % Az;
            int e1 = Mathf.Clamp(e0 + 1, 0, El - 1); e0 = Mathf.Clamp(e0, 0, El - 1);
            float r00 = R[e0 * Az + a0], r10 = R[e0 * Az + a1];
            float r01 = R[e1 * Az + a0], r11 = R[e1 * Az + a1];
            return Mathf.Lerp(Mathf.Lerp(r00, r10, ta), Mathf.Lerp(r01, r11, ta), te);
        }

        /// <summary>Penetration of a WORLD point into the hull: &gt; 0 = inside by
        /// that many metres (bone-local units; the fruit bone carries the model
        /// scale, so divide by the bone's lossy scale to get metres). Also returns
        /// the outward world direction and the world surface point at
        /// radius+margin.</summary>
        public float Depth(Vector3 world, float margin, out Vector3 outDirWorld, out Vector3 surfaceWorld)
        {
            var q = Bone.InverseTransformPoint(world) - Centre;
            float r = q.magnitude;
            if (r < 1e-5f) { outDirWorld = Bone.TransformDirection(Vector3.forward); surfaceWorld = world; return Rmax; }
            var dir = q / r;
            float rr = Radius(dir);
            float need = rr + margin;
            outDirWorld = (Bone.TransformPoint(Centre + dir) - Bone.TransformPoint(Centre)).normalized;
            surfaceWorld = Bone.TransformPoint(Centre + dir * need);
            return need - r;
        }

        public float MetresPerUnit => Mathf.Max(1e-4f, (Bone.lossyScale.x + Bone.lossyScale.y + Bone.lossyScale.z) / 3f);
    }

    public sealed class ArmClearState
    {
        public float Theta;             // smoothed outward swing about k, degrees
        public float Phi;               // smoothed pre-roll about the shoulder's outward normal, degrees
        public float LastHandDepth, LastElbowDepth; // metres before the fix (> 0 = inside)
    }

    public static class BodyClearance
    {
        public const float MarginM = 0.02f;        // metres outside the hull
        /// <summary>Below this the pose is left exactly as the clip authored it.
        /// The hull is a 32x16 radial map of a lumpy fruit, so a hand sliding
        /// along the skin reads a centimetre or two inside; correcting that is
        /// all cost and no benefit — nobody can see two centimetres, and the
        /// correction is what the user notices.</summary>
        public const float DeadZoneM = 0.045f;
        public const float SwingInDegPerS = 540f, SwingOutDegPerS = 240f;
        /// <summary>The swing is capped well below a right angle on purpose: past
        /// that the arm no longer reads as the authored motion, and a pose that
        /// still cannot clear (a cross-body reach on a 1 m-wide fruit) is better
        /// left slightly buried than thrown out sideways. The search keeps the
        /// angle that leaves the LEAST depth when none clears.</summary>
        public const float MaxSwingDeg = 45f, StepDeg = 2.5f;

        public struct Report { public float HandDepthBefore, ElbowDepthBefore, HandDepthAfter, ElbowDepthAfter, ThetaDeg; public bool Wrote; }

        /// <summary>How deep inside the hull the arm reads, in metres. Only the
        /// points a SHOULDER ROTATION can actually move are required to clear:
        /// the hand, the elbow and the forearm's midpoint. The upper arm's own
        /// midpoint is deliberately NOT a requirement — it sits ~0.14 m from the
        /// pivot, so swinging the arm barely moves it, and on a wide fruit it
        /// grazes the skin at rest. Requiring it was unsolvable, and the search
        /// below answered an unsolvable case by slamming to its maximum swing:
        /// measured 2026-11-02, the layer sat at the full 120 deg in EVERY phase
        /// including standing still (PARKOUR_ARMWRITE meanClearDeg=120 at idle),
        /// which is the user's "my hands are not same physic like in parkour
        /// assets" — the arms were pinned 120 deg off the authored pose all the
        /// time.</summary>
        static float ChainDepth(BodyHull hull, float marginU, Vector3 S, Vector3 E, Vector3 H, out float dh, out float de)
        {
            float mpu = hull.MetresPerUnit;
            dh = hull.Depth(H, marginU, out _, out _) * mpu;
            de = hull.Depth(E, marginU, out _, out _) * mpu;
            float dm2 = hull.Depth((E + H) * 0.5f, marginU, out _, out _) * mpu;   // mid forearm
            return Mathf.Max(Mathf.Max(dh, de), dm2);
        }

        /// <summary>One arm. The clip's arm pose (shoulder→elbow→hand) is kept
        /// rigid and swung outward about the shoulder, toward the hull's
        /// radial direction at the shoulder, by the smallest angle that puts the
        /// elbow, the hand and both segment midpoints outside the hull plus
        /// margin. The swing angle is smoothed per arm so it never pops; a
        /// clear pose passes through untouched. Returns the depths in metres.</summary>
        public static Report Resolve(Transform upper, Transform fore, Transform hand, BodyHull hull, ArmClearState st, float dt, bool write)
        {
            var rep = new Report();
            if (upper == null || fore == null || hand == null || hull == null) return rep;
            if (st == null) st = new ArmClearState();
            float mpu = hull.MetresPerUnit;
            float marginU = MarginM / mpu;
            Vector3 S = upper.position, E = fore.position, H = hand.position;
            float before = ChainDepth(hull, marginU, S, E, H, out float dh, out float de);
            rep.HandDepthBefore = dh; rep.ElbowDepthBefore = de;
            st.LastHandDepth = dh; st.LastElbowDepth = de;
            if (before < DeadZoneM) before = 0f;    // leave the authored pose alone

            // outward direction at the shoulder, and the axis that swings the arm toward it
            hull.Depth(S, marginU, out Vector3 n, out _);
            Vector3 a = E - S;
            if (a.sqrMagnitude < 1e-8f) { rep.HandDepthAfter = dh; rep.ElbowDepthAfter = de; return rep; }
            Vector3 k = Vector3.Cross(a.normalized, n);
            if (k.sqrMagnitude < 1e-6f) k = Vector3.Cross(n, Vector3.up);
            if (k.sqrMagnitude < 1e-6f) k = Vector3.right;
            k.Normalize();
            // pick the sign that moves the arm toward n
            Vector3 aPlus = Quaternion.AngleAxis(10f, k) * a;
            float sign = Vector3.Dot(aPlus, n) >= Vector3.Dot(a, n) ? 1f : -1f;

            // Search: a pre-roll phi about the outward normal n (walks the arm
            // around the shoulder — forward → up/down/back), then the outward
            // swing theta about k; smallest |phi|+theta that clears wins. Stage 1
            // is phi=0 (the plain outward swing), which covers locomotion.
            float needDeg = 0f, needPhi = 0f;
            if (before > 0f)
            {
                // Stage 1 is phi = 0, the plain outward swing, which covers
                // locomotion; the pre-roll walks the arm around the shoulder
                // only when that alone cannot clear. The smallest correction
                // that CLEARS wins; if none does, the one that leaves the least
                // depth wins (never the maximum — see MaxSwingDeg).
                float bestCost = float.MaxValue;
                float fallbackDepth = before, fallbackDeg = 0f, fallbackPhi = 0f;
                float[] phis = { 0f, 25f, -25f, 50f, -50f };
                foreach (float ph in phis)
                {
                    var qp = Quaternion.AngleAxis(ph, n);
                    Vector3 a1 = qp * (E - S), b1 = qp * (H - E);
                    Vector3 k1 = Vector3.Cross(a1.normalized, n); if (k1.sqrMagnitude < 1e-6f) k1 = k; k1.Normalize();
                    float s1 = Vector3.Dot(Quaternion.AngleAxis(10f, k1) * a1, n) >= Vector3.Dot(a1, n) ? 1f : -1f;
                    for (float th = 0f; th <= MaxSwingDeg; th += StepDeg)
                    {
                        float cost = Mathf.Abs(ph) + th; if (cost >= bestCost) break;
                        var q = Quaternion.AngleAxis(s1 * th, k1);
                        Vector3 E2 = S + q * a1; Vector3 H2 = E2 + q * b1;
                        float d2 = ChainDepth(hull, marginU, S, E2, H2, out _, out _);
                        if (d2 <= 0f) { bestCost = cost; needDeg = th; needPhi = ph; break; }
                        if (d2 < fallbackDepth - 1e-4f) { fallbackDepth = d2; fallbackDeg = th; fallbackPhi = ph; }
                    }
                }
                if (bestCost == float.MaxValue) { needDeg = fallbackDeg; needPhi = fallbackPhi; }
            }
            float rate = needDeg > st.Theta ? SwingInDegPerS : SwingOutDegPerS;
            st.Theta = Mathf.MoveTowards(st.Theta, needDeg, rate * Mathf.Max(dt, 1e-4f));
            st.Phi = Mathf.MoveTowards(st.Phi, needPhi, (Mathf.Abs(needPhi) > Mathf.Abs(st.Phi) ? SwingInDegPerS : SwingOutDegPerS) * Mathf.Max(dt, 1e-4f));
            rep.ThetaDeg = st.Theta;
            if (!write || (st.Theta <= 0.01f && Mathf.Abs(st.Phi) <= 0.01f)) { rep.HandDepthAfter = dh; rep.ElbowDepthAfter = de; return rep; }
            {
                var qp = Quaternion.AngleAxis(st.Phi, n);
                Vector3 a1 = qp * (E - S);
                Vector3 k1 = Vector3.Cross(a1.normalized, n); if (k1.sqrMagnitude < 1e-6f) k1 = k; k1.Normalize();
                float s1 = Vector3.Dot(Quaternion.AngleAxis(10f, k1) * a1, n) >= Vector3.Dot(a1, n) ? 1f : -1f;
                upper.rotation = Quaternion.AngleAxis(s1 * st.Theta, k1) * qp * upper.rotation;
            }
            rep.Wrote = true;
            ChainDepth(hull, marginU, upper.position, fore.position, hand.position, out rep.HandDepthAfter, out rep.ElbowDepthAfter);
            return rep;
        }
    }

    /// <summary>Runs last in the frame (after GameLoop.LateUpdate's whole pose
    /// pipeline) on the hero's rig root. Active only on foot and outside a
    /// climb, where the hands are world-locked on purpose.</summary>
    [DefaultExecutionOrder(5000)]
    public sealed class BodyClearanceLayer : MonoBehaviour
    {
        public CharacterRig Rig;
        public BodyHull Hull;
        public System.Func<bool> Active;
        public readonly ArmClearState L = new ArmClearState(), R = new ArmClearState();
        public BodyClearance.Report LastL, LastR;
        public int FramesCorrected;

        public static BodyClearanceLayer Attach(CharacterRig rig, System.Func<bool> active)
        {
            if (rig == null || rig.Root == null || rig.Anim == null || !rig.IsHumanoid) return null;
            var smr = rig.Model != null ? rig.Model.GetComponentInChildren<SkinnedMeshRenderer>() : null;
            var chest = rig.Anim.GetBoneTransform(HumanBodyBones.Chest) ?? rig.Anim.GetBoneTransform(HumanBodyBones.Spine);
            var hull = BodyHull.Fit(smr, null);
            if (hull == null) { Debug.Log($"BODYCLEAR id={rig.Id} hull=none"); return null; }
            var layer = rig.Root.gameObject.AddComponent<BodyClearanceLayer>();
            layer.Rig = rig; layer.Hull = hull; layer.Active = active;
            Debug.Log($"BODYCLEAR id={rig.Id} hullBone={hull.Bone.name} fruitVerts={hull.FruitVerts} rmaxM={(hull.Rmax * hull.MetresPerUnit):F2} chest={(chest != null ? chest.name : "-")}");
            return layer;
        }

        void LateUpdate()
        {
            if (Rig == null || Hull == null || Rig.Anim == null) return;
            if (Active != null && !Active()) { L.Theta = 0f; R.Theta = 0f; L.Phi = 0f; R.Phi = 0f; return; }
            float dt = Time.deltaTime;
            var a = Rig.Anim;
            LastL = BodyClearance.Resolve(a.GetBoneTransform(HumanBodyBones.LeftUpperArm), a.GetBoneTransform(HumanBodyBones.LeftLowerArm), a.GetBoneTransform(HumanBodyBones.LeftHand), Hull, L, dt, true);
            LastR = BodyClearance.Resolve(a.GetBoneTransform(HumanBodyBones.RightUpperArm), a.GetBoneTransform(HumanBodyBones.RightLowerArm), a.GetBoneTransform(HumanBodyBones.RightHand), Hull, R, dt, true);
            if (LastL.Wrote || LastR.Wrote) FramesCorrected++;
        }
    }
}
