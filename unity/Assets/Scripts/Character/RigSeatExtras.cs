using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of the four characters.js CharacterRig methods neither
    /// CharacterRig.cs nor FootPoses.cs ported, though both their headers
    /// name them as deferred ("rifleShoulder/_measurePocket, aimOffFacing,
    /// shoulderFront" / "...shoulderFront. All of them are..."): shoulderFront
    /// (characters.js:1009-1024), _hipHeight/_overSoles (characters.js:1370-
    /// 1396), and setStrafe (characters.js:570-579). Built only from
    /// CharacterRig's/FootPoses' PUBLIC surface, same pattern as
    /// ShoulderPocket.cs and FootPoses.cs itself — neither of those files is
    /// edited here.
    ///
    /// Callers: shoulderFront is read by src/weapons.js (its own comment,
    /// weapons.js:757, "rig.shoulderFront -- 3 cm on a man, 18 on the..."),
    /// which has no Unity port in this pass's scope (Combat/HeldWeapon.cs,
    /// RifleHolds.cs, WeaponAnim.cs are all on the forbidden-edit list — other
    /// agents own them) — STILL UNWIRED.
    ///
    /// setStrafe IS NOW WIRED (2026-10-27): FootController.cs's Update(),
    /// right before its `rig.Locomote(...)` call (src/foot.js:6057's own
    /// position, right before :6062-6063's `rig.locomote`) — see that call
    /// site's own comment for the sa/sb port (foot.js:6032-6057, including the
    /// cover-shuffle's wall-tangent atan2 and the backpedal's extra PI).
    ///
    /// HipHeight/OverSoles are NOT currently read by FootBoarding.cs — that
    /// file built its own documented substitute (MeasureSeatMetrics, see its
    /// "SEAT-FIT MEASUREMENT (UNVERIFIED)" header) rather than wait on this
    /// port landing. Flagged in this pass's report as a possible later swap,
    /// not applied here (FootBoarding.cs is not edited by this pass).
    ///
    /// ================================ HANDEDNESS ================================
    /// No cross product in any of the four methods (checked against
    /// characters.js:570-579, 1009-1024, 1370-1396 directly — every one is a
    /// dot-product-free scan of axis-aligned distances, or a plain angle wrap).
    /// shoulderFront/HipHeight/OverSoles measure a bone's position IN THE
    /// RIG'S OWN FRAME (Model/AnimGroup InverseTransformPoint) — self-
    /// consistent, nothing to mirror, same category FootPoses.cs's own header
    /// describes for its non-cross-product methods. SetStrafe writes a LOCAL-
    /// axis Euler angle (AnimGroup's own Y) — component-identical per
    /// CharacterRig.cs's own HANDEDNESS note ("every rotation here is built
    /// with Quaternion.AngleAxis about one of THIS RIG'S OWN local axes... that
    /// is handedness-SAFE as-is").
    ///
    /// VERIFIED: none of this file's geometry could be exercised (no Unity
    /// editor this pass — see tools/_probe_planechar.mjs for what WAS checked:
    /// the ported constants/thresholds against characters.js's own literals).
    /// Reviewed, not executed.
    /// </summary>
    public static class RigSeatExtras
    {
        // ---- magic numbers, named per this task's own porting rule ----
        internal const float ShoulderBandX = 0.07f;   // characters.js:1038
        internal const float ShoulderBandY = 0.1f;    // characters.js:1038
        internal const float ShoulderMin = 0.02f;     // characters.js:1040
        internal const float ShoulderDefault = 0.03f; // characters.js:1016, 1040
        internal const float HipRatio = 0.52f;        // characters.js:1381 HERO_HEIGHT * 0.52
        internal const float StrafeRate = 12f;        // characters.js:575

        // ---- characters.js:1009-1024 shoulderFront, cached per (rig, side) ----
        static readonly Dictionary<(CharacterRig rig, string side), float> _shFrontCache =
            new Dictionary<(CharacterRig, string), float>();

        /// <summary>characters.js:1009-1024 shoulderFront. How far in front of
        /// a shoulder joint the skin is, in the rig's own frame (faces -Z
        /// there) — a few centimetres on a human rig, ~18 cm on the banana.
        /// Only this rig's own skin is sampled (no weapon/gear meshes), same
        /// restriction the JS applies by only scanning `this.obj`.</summary>
        public static float ShoulderFront(CharacterRig rig, string side = "R")
        {
            var key = (rig, side);
            if (_shFrontCache.TryGetValue(key, out float cached)) return cached;

            var arm = FootPoses.GetArmSide(rig, side);
            Transform up = arm.Upper;
            if (up == null) { _shFrontCache[key] = ShoulderDefault; return ShoulderDefault; }

            // this.anim ?? this.act ?? this.obj — characters.js:1021
            Transform frame = rig.AnimGroup ?? rig.ActGroup ?? rig.Model;
            Vector3 sp = frame.InverseTransformPoint(up.position);

            float minZ = float.PositiveInfinity;
            foreach (var smr in rig.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null || System.Array.IndexOf(smr.bones, up) < 0) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked);
                var verts = baked.vertices;
                Matrix4x4 m = smr.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i += 2) // every other vertex — characters.js:1038
                {
                    Vector3 p = frame.InverseTransformPoint(m.MultiplyPoint3x4(verts[i]));
                    if (Mathf.Abs(p.x - sp.x) < ShoulderBandX && Mathf.Abs(p.y - sp.y) < ShoulderBandY && p.z < minZ) minZ = p.z;
                }
                Object.DestroyImmediate(baked);
            }
            float d = float.IsFinite(minZ) ? Mathf.Max(ShoulderMin, sp.z - minZ) : ShoulderDefault;
            _shFrontCache[key] = d;
            return d;
        }

        /// <summary>Clears a rig's cached measurements — call if a rig's body
        /// is replaced/rebuilt (mirrors the JS's per-instance `this._shFront`
        /// living and dying with the CharacterRig object; this static cache
        /// needs an explicit clear since it outlives any one rig instance).</summary>
        public static void ClearCache(CharacterRig rig)
        {
            _shFrontCache.Remove((rig, "L"));
            _shFrontCache.Remove((rig, "R"));
        }

        // =====================================================================
        // characters.js:1370-1396 _hipHeight / _overSoles
        // =====================================================================

        /// <summary>characters.js:1389-1395 _overSoles. How high `bone` sits
        /// above the body's own origin, IN THE BODY'S FRAME (rig.Model) — not
        /// a difference of world Ys, which a banked aeroplane would foreshorten.</summary>
        public static float OverSoles(CharacterRig rig, Transform bone)
        {
            if (bone == null) return 0f;
            return rig.Model.InverseTransformPoint(bone.position).y;
        }

        /// <summary>characters.js:1370-1381 _hipHeight. How far the hips stand
        /// above the soles — the pelvis/hips bone if the rig has one, else the
        /// anatomical half (characters.js's own HERO_HEIGHT * 0.52). For the 3
        /// Humanoid bodies this is Animator.GetBoneTransform(Hips) directly
        /// (Unity's avatar replacing the JS's name-sniffing traversal, same
        /// substitution CharacterRig.cs's own header documents elsewhere); the
        /// name-suffix scan below is the fallback for a Generic rig (the
        /// elephant) that still carries a skinned pelvis bone.</summary>
        public static float HipHeight(CharacterRig rig)
        {
            Transform hips = null;
            if (rig.IsHumanoid && rig.Anim != null) hips = rig.Anim.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) hips = FindBoneByNameSuffix(rig.Model, "pelvis", "hips", "hip");
            return hips != null ? OverSoles(rig, hips) : CharacterRoster.HeroHeight * HipRatio;
        }

        static Transform FindBoneByNameSuffix(Transform root, params string[] suffixes)
        {
            var seen = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null) continue;
                foreach (var b in smr.bones)
                {
                    if (b == null || !seen.Add(b)) continue;
                    string n = Normalize(b.name);
                    foreach (var suf in suffixes) if (n.EndsWith(suf)) return b;
                }
            }
            return null;
        }

        static string Normalize(string n) => Regex.Replace((n ?? "").ToLowerInvariant(), "[^a-z]", "");

        // =====================================================================
        // characters.js:570-579 setStrafe
        // =====================================================================
        static readonly Dictionary<CharacterRig, float> _strafeY = new Dictionary<CharacterRig, float>();

        /// <summary>characters.js:570-579 setStrafe. Turns AnimGroup to face
        /// the travel direction (so a rigless/procedural body's hop-lean goes
        /// sideways with it) by writing ONLY the Y Euler component, same as
        /// the JS's `this.anim.rotation.y = ...` leaving X/Z untouched. MUST
        /// run in the same per-frame order as foot.js's own call — after
        /// Locomote/SwingBody have written this frame's X/Z lean, or this will
        /// read back a stale X/Z and re-write it unchanged (a no-op either
        /// way, since it is preserved, not recomputed) — see class header
        /// HANDEDNESS. Called from FootController.cs's Update() — see class
        /// header "Callers".</summary>
        public static void SetStrafe(CharacterRig rig, float angle, float dt, float blend = 1f)
        {
            float want = (float.IsFinite(angle) ? angle : 0f) * blend;
            _strafeY.TryGetValue(rig, out float y);
            float d = want - y;
            while (d > Mathf.PI) d -= 2f * Mathf.PI;
            while (d < -Mathf.PI) d += 2f * Mathf.PI;
            y += d * Mathf.Min(1f, StrafeRate * dt);
            _strafeY[rig] = y;
            if (rig.AnimGroup == null) return;
            Vector3 e = rig.AnimGroup.localEulerAngles; // degrees; X/Z preserved
            rig.AnimGroup.localRotation = Quaternion.Euler(e.x, y * Mathf.Rad2Deg, e.z);
        }
    }
}
