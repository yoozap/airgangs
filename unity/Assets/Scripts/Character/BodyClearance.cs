// Keeps the hero's hands and elbows OUTSIDE the fruit.
//
// The purchased clips (Threepeat parkour, Cover+Shoot locomotion) were authored
// for a slim human. Our bodies are wide fruit — an apple is ~1.2 m across at
// the chest — with the shoulder pivots at the fruit's widest point
// (tools/build_veggies.py arm_root), so a swinging forearm or hand crosses
// INSIDE the hull and vanishes (user, 2026-11-01 22:50: "sometimes they are
// inside body and i dont see them when i am moving").
//
// BodyHull fits the fruit once at spawn: the skinned mesh is baked in its bind
// pose, the vertices that belong to the fruit (the one bone that carries most
// of the mesh — spine_02 in the builder's rig) are taken into that bone's local
// space and reduced to a radial map (max radius per azimuth/elevation bin
// around the fruit's centre). The map rides the chest bone, so a twisting
// spine moves the hull with the fruit.
//
// BodyClearance runs AFTER every other arm writer (the clip, the arm springs,
// the pose layers) and, when a hand or elbow reads inside the hull, solves the
// two-bone chain to a target pushed out along the hull's radial direction by
// the hull radius plus a margin, with the elbow pole pushed out the same way.
// The correction weight is smoothed so an arm never pops; when the chain is
// clear the clip pose passes through untouched. Fixed in the rig, not in the
// clips — the clips stay the pack's own.
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

        /// <summary>Hull radius toward a bone-local unit direction (max over the
        /// bin and its neighbours, so a point between bins is never judged by a
        /// smaller side bin).</summary>
        public float Radius(Vector3 dirLocal)
        {
            Bin(dirLocal, out int az, out int el);
            float m = 0f;
            for (int de = -1; de <= 1; de++) for (int da = -1; da <= 1; da++)
            {
                int e2 = Mathf.Clamp(el + de, 0, El - 1), a2 = (az + da + Az) % Az;
                m = Mathf.Max(m, R[e2 * Az + a2]);
            }
            return m;
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
        public const float MarginM = 0.04f;        // metres outside the hull
        public const float SwingInDegPerS = 540f, SwingOutDegPerS = 240f;
        public const float MaxSwingDeg = 120f, StepDeg = 2.5f;

        public struct Report { public float HandDepthBefore, ElbowDepthBefore, HandDepthAfter, ElbowDepthAfter, ThetaDeg; public bool Wrote; }

        static float ChainDepth(BodyHull hull, float marginU, Vector3 S, Vector3 E, Vector3 H, out float dh, out float de)
        {
            float mpu = hull.MetresPerUnit;
            dh = hull.Depth(H, marginU, out _, out _) * mpu;
            de = hull.Depth(E, marginU, out _, out _) * mpu;
            float dm1 = hull.Depth((S + E) * 0.5f, marginU, out _, out _) * mpu;   // mid upper arm
            float dm2 = hull.Depth((E + H) * 0.5f, marginU, out _, out _) * mpu;   // mid forearm
            return Mathf.Max(Mathf.Max(dh, de), Mathf.Max(dm1, dm2));
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
                float bestCost = float.MaxValue; needDeg = MaxSwingDeg; needPhi = 0f;
                float[] phis = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };
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
                        if (ChainDepth(hull, marginU, S, E2, H2, out _, out _) <= 0f) { bestCost = cost; needDeg = th; needPhi = ph; break; }
                    }
                }
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
