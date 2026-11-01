using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>src/weapons.js:19 WEAPON_DEFS — one entry per weapon the game knows
    /// about, keyed by the id written into a hero's saved loadout. Fields the pose
    /// solvers below need are measured once and cached directly on the instance
    /// (Rise/Muzzle/Port/Butt/Guard — src/weapons.js:218-244 attachToHand), exactly
    /// as the JS mutates its own plain-object defs in place.</summary>
    public sealed class WeaponDef
    {
        public string Id;
        public string Name;           // Georgian — kept byte for byte
        public string Kind;           // null = pistol-like, "rifle" = two-handed
        public string CartridgeId;
        public int Slot;
        public string Url;
        public string ThumbUrl;
        public float Scale = 1f;
        public Vector3 GripPos;       // model-space grip offset
        public Vector3? GripRotEuler; // optional extra rotation (radians)
        public string Anim;           // which GUN_CLIP_SETS table its own clip is cut with

        // ---- measured once, on first AttachToHand (model-space / local units) ----
        public bool Measured;
        public float Rise;            // how far the model reaches above its own origin
        public Vector3 Muzzle;        // where the barrel ends
        public Vector3 Port;          // the ejection port
        public float Butt;            // how far the stock's butt is behind the grip (rifle)
        public float Guard;           // the rear of the handguard (rifle)
    }

    public static class WeaponDefs
    {
        public static readonly Dictionary<string, WeaponDef> ById = new Dictionary<string, WeaponDef>
        {
            ["makarov"] = new WeaponDef
            {
                Id = "makarov", Name = "მაკაროვი PM", CartridgeId = "9x18", Slot = 1,
                Url = "models/makarov.glb", ThumbUrl = "models/thumbs/makarov.png", Scale = 1.5f,
                GripPos = new Vector3(0.070f, -0.012f, 0.012f),
            },
            ["beretta"] = new WeaponDef
            {
                Id = "beretta", Name = "ბერეტა 92FS", CartridgeId = "9x19", Slot = 1,
                Url = "models/beretta_anim.glb", ThumbUrl = "models/thumbs/beretta.png", Scale = 1.5f,
                GripPos = new Vector3(0.075f, -0.012f, 0.012f), Anim = "pistol",
            },
            ["akm"] = new WeaponDef
            {
                Id = "akm", Name = "კალაშნიკოვი AKM", Kind = "rifle", CartridgeId = "7.62x39", Slot = 4,
                Url = "models/akm_anim.glb", ThumbUrl = "models/thumbs/akm.png", Scale = 1.1f, Anim = "rifle",
                GripPos = new Vector3(0.012f, -0.045f, 0.0f),
            },
        };

        public static WeaponDef ById_(string id) => (id != null && ById.TryGetValue(id, out var d)) ? d : null;
        public static bool IsRifle(WeaponDef d) => d?.Kind == "rifle";
    }

    /// <summary>src/weapons.js:76 GEAR_DEFS — worn kit (no slot, no trigger).</summary>
    public sealed class GearDef
    {
        public string Id;
        public string Name;
        public string Kind = "gear";
        public string Bone;
        public string Url;
        public string ThumbUrl;
        public string Socket;
        public string[] Sockets;
        public float Width;
        public float Fit = 1f;
        public float Drop;
        public float Back;
        public float HolsterScale;
    }

    public static class GearDefs
    {
        public static readonly Dictionary<string, GearDef> ById = new Dictionary<string, GearDef>
        {
            ["belt"] = new GearDef
            {
                Id = "belt", Name = "ქამარი ბუდით", Bone = "hips",
                Url = "models/belt.glb", ThumbUrl = "models/thumbs/belt.png",
                Socket = "holster", Sockets = new[] { "holster", "holster2" },
                Width = 0.365f, Fit = 1.0f, Drop = 0.19f, Back = 0.0f, HolsterScale = 0.54f,
            },
        };
        public static GearDef GearById(string id) => (id != null && ById.TryGetValue(id, out var d)) ? d : null;
    }

    /// <summary>
    /// Port of src/weapons.js — attaching a weapon to a hand and posing the arm
    /// around it on ANY of the game's rigs (a 22-bone banana, a 68-bone cyberfem,
    /// an astronaut with no hand bone at all). Every pose is SOLVED from measured
    /// points and the body's OWN axes (bodyAxes/chestForward/aimLine below), never
    /// from hard-coded per-rig angles — that is what makes one set of functions
    /// work on every body shape.
    ///
    /// HANDEDNESS, read before editing: forward flips sign (Three's -Z is Unity's
    /// +Z), so every `new Vector3(0,0,-1)` meaning "forward" below is
    /// `new Vector3(0,0,1)`. Three's position/rotation reads
    /// (`updateWorldMatrix`+`matrixWorld.decompose`) collapse to Unity's
    /// `Transform.position`/`.rotation`, which stay live without a manual update
    /// call. Every cross product is audited at its use site — this file has six:
    /// seatWeapon's basis (x2), chestForward/bodyFwd (shared via ChestAxis),
    /// applyRifleHold/boreReach/applyRifleReloadHand's "pouch" case (all the same
    /// ChestAxis helper) — all SWAPPED per GeoFrame's rule.
    ///
    /// NOT PORTED (out of scope / lower priority given the time available):
    /// biasWorn (a polygon-offset depth-sort trick — Unity's depth testing and
    /// render-queue ordering replace it outright) and the belt-fitting
    /// measurement (_scaleBelt/_centreBelt, foot.js territory). The REACH
    /// geometry (boreReach, the hand-to-bore clamping) IS ported below, since
    /// that is the part that keeps a hand from floating off a long rifle on a
    /// short-armed body. CORRECTION (this header previously said
    /// applyRifleSupport's/applyRifleReloadHand's magazine/bolt-bone tracking
    /// — "magBone" — was not ported; that was stale and sent an earlier pass
    /// to re-port already-existing work. It IS ported, in
    /// Combat/RifleHolds.cs's ApplyRifleReloadHand (MagBoneOf + the `onMag`
    /// blend against mag.Mag.localPosition/MagHome, ~lines 457-470) — the hand
    /// rides the moving magazine and bolt exactly as the JS does.
    /// </summary>
    public static class Weapons
    {
        const float HandOverGun = 4f; // depth bias: the hand draws over the gun it holds
        const float DropFraction = 0.05f;
        public const float MuzzleDip = 0.22f; // rad, barrel nosed down at rest

        public static bool IsHandBone(string name) => Regex.IsMatch(name ?? "", "hand|main1\\.D|wrist", RegexOptions.IgnoreCase);

        // src/weapons.js:175 findHandBone
        public static Transform FindHandBone(Transform root)
        {
            var bones = AllTransforms(root);
            if (bones.Count == 0) return null;
            Transform hand = Find(bones, b => Regex.IsMatch(b.name, "^(hand_r|main1\\.D)$", RegexOptions.IgnoreCase))
                ?? Find(bones, b => Regex.IsMatch(b.name, "hand", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(b.name, "(^|[^a-z])r($|[^a-z])|right", RegexOptions.IgnoreCase));
            if (hand != null) return hand;
            var fingers = FindAll(bones, b => Regex.IsMatch(b.name, "(index|middle|pinky|thumb|ring)1?.*r$", RegexOptions.IgnoreCase));
            foreach (var fg in fingers) if (fg.parent != null) return fg.parent;
            return Find(bones, b => Regex.IsMatch(b.name, "(lowerarm|forearm).*r$", RegexOptions.IgnoreCase));
        }

        // src/weapons.js:190 findHandBoneL
        public static Transform FindHandBoneL(Transform root)
        {
            var bones = AllTransforms(root);
            if (bones.Count == 0) return null;
            Transform hand = Find(bones, b => Regex.IsMatch(b.name, "^(hand_l|main1\\.G)$", RegexOptions.IgnoreCase))
                ?? Find(bones, b => Regex.IsMatch(b.name, "hand", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(b.name, "(^|[^a-z])l($|[^a-z])|left", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(b.name, "finger|thumb|index|middle|pinky|ring|digit", RegexOptions.IgnoreCase));
            if (hand != null) return hand;
            var fingers = FindAll(bones, b => Regex.IsMatch(b.name, "(index|middle|pinky|thumb|ring)1?.*l$", RegexOptions.IgnoreCase));
            foreach (var fg in fingers) if (fg.parent != null) return fg.parent;
            return Find(bones, b => Regex.IsMatch(b.name, "(lowerarm|forearm).*l$", RegexOptions.IgnoreCase));
        }

        static List<Transform> AllTransforms(Transform root) => new List<Transform>(root.GetComponentsInChildren<Transform>(true));
        static Transform Find(List<Transform> list, Func<Transform, bool> pred) { foreach (var t in list) if (pred(t)) return t; return null; }
        static List<Transform> FindAll(List<Transform> list, Func<Transform, bool> pred) { var o = new List<Transform>(); foreach (var t in list) if (pred(t)) o.Add(t); return o; }

        // ---- rotation-matrix(columns) -> quaternion, handedness-agnostic given a
        // properly orthonormal (x,y,z) triple in the CALLING engine's own convention
        // (Shepherd's method — the same algorithm THREE.Quaternion.setFromRotationMatrix
        // uses internally, engine-independent linear algebra).
        public static Quaternion QuaternionFromBasis(Vector3 x, Vector3 y, Vector3 z)
        {
            float m00 = x.x, m10 = x.y, m20 = x.z;
            float m01 = y.x, m11 = y.y, m21 = y.z;
            float m02 = z.x, m12 = z.y, m22 = z.z;
            float trace = m00 + m11 + m22;
            float qx, qy, qz, qw;
            if (trace > 0f)
            {
                float s = 0.5f / Mathf.Sqrt(trace + 1f);
                qw = 0.25f / s;
                qx = (m21 - m12) * s;
                qy = (m02 - m20) * s;
                qz = (m10 - m01) * s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = 2f * Mathf.Sqrt(1f + m00 - m11 - m22);
                qw = (m21 - m12) / s; qx = 0.25f * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = 2f * Mathf.Sqrt(1f + m11 - m00 - m22);
                qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25f * s; qz = (m12 + m21) / s;
            }
            else
            {
                float s = 2f * Mathf.Sqrt(1f + m22 - m00 - m11);
                qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25f * s;
            }
            return new Quaternion(qx, qy, qz, qw).normalized;
        }

        // src/weapons.js:437 aimBone — lay a bone along a world direction. The one
        // primitive every arm pose in the game is built from.
        public static void AimBone(Transform bone, Transform child, Vector3 dir)
        {
            Vector3 bp = bone.position, cp = child.position;
            Vector3 cur = cp - bp;
            if (cur.sqrMagnitude < 1e-10f) return;
            cur.Normalize();
            Quaternion rot = Quaternion.FromToRotation(cur, dir);     // world-space correction
            Quaternion desired = rot * bone.rotation;                // desired world rotation
            bone.rotation = desired; // Unity composes localRotation from parent automatically
        }

        // src/weapons.js:943 aimHand — turn a hand bone so IT points along dir (uses
        // the parent forearm's own line, since a mitten has no child to read a line from).
        public static bool AimHand(Transform hand, Vector3 dir)
        {
            Transform fore = hand.parent;
            if (fore == null) return false;
            Vector3 cur = hand.position - fore.position;
            if (cur.sqrMagnitude < 1e-10f) return false;
            cur.Normalize();
            Quaternion rot = Quaternion.FromToRotation(cur, dir) * hand.rotation;
            hand.rotation = rot;
            return true;
        }

        // ---- arm chains --------------------------------------------------------
        static readonly Regex UpperR = new Regex("^(upperarm_r|rightarm|upperarmr|bras1\\.D)$", RegexOptions.IgnoreCase);
        static readonly Regex ForeR = new Regex("^(lowerarm_r|rightforearm|lowerarmr|bras2\\.D)$", RegexOptions.IgnoreCase);
        static readonly Regex UpperL = new Regex("^(upperarm_l|leftarm|upperarml|bras1\\.G)$", RegexOptions.IgnoreCase);
        static readonly Regex ForeL = new Regex("^(lowerarm_l|leftforearm|lowerarml|bras2\\.G)$", RegexOptions.IgnoreCase);

        public struct ArmChain { public Transform Upper, Fore; public bool Valid => Upper != null && Fore != null; }

        public static ArmChain FindArmChain(Transform root)
        {
            ArmChain c = default;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (c.Upper == null && UpperR.IsMatch(t.name)) c.Upper = t;
                if (c.Fore == null && ForeR.IsMatch(t.name)) c.Fore = t;
            }
            return c.Valid ? c : default;
        }

        public static ArmChain FindArmChainL(Transform root)
        {
            ArmChain c = default;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (c.Upper == null && UpperL.IsMatch(t.name)) c.Upper = t;
                if (c.Fore == null && ForeL.IsMatch(t.name)) c.Fore = t;
            }
            return c.Valid ? c : default;
        }

        // ---- body-relative axes ------------------------------------------------
        const float ArmPitchMax = 0.9f; // rad

        // src/weapons.js:421 aimLine
        public static Vector3 AimLine(Vector3? aim, Quaternion bodyQuat)
        {
            if (!aim.HasValue || aim.Value.sqrMagnitude < 1e-8f)
            {
                Vector3 flat = bodyQuat * new Vector3(0, 0, 1); // HANDEDNESS: Unity forward is +Z
                flat.y = 0f;
                return flat.normalized;
            }
            Vector3 o = aim.Value.normalized;
            float lim = Mathf.Sin(ArmPitchMax);
            if (Mathf.Abs(o.y) > lim)
            {
                float flatLen = Mathf.Max(1e-6f, Mathf.Sqrt(o.x * o.x + o.z * o.z));
                float want = Mathf.Cos(ArmPitchMax) / flatLen;
                o = new Vector3(o.x * want, Mathf.Sign(o.y) * lim, o.z * want).normalized;
            }
            return o;
        }

        // src/weapons.js:484 crossBody — 0 = target on the gun hand's own side, 1 = across.
        public static float CrossBody(Vector3 aimDir, Vector3 right) => Mathf.Clamp01(-Vector3.Dot(aimDir, right));

        /// <summary>The body's own up/right axes AT THIS POSE (src/weapons.js:523
        /// bodyAxes). `frame` is the rig's tumble/recline layer if it has one (the
        /// JS's `rig.act`), else `root`.</summary>
        public static (Vector3 up, Vector3 right) BodyAxes(Transform frame, Transform root, Transform upper, Transform upperL = null)
        {
            Vector3 up = frame.rotation * Vector3.up;
            Vector3 right = frame.rotation * Vector3.right;
            Vector3 toUpper = upper.position - root.position;
            if (Vector3.Dot(right, toUpper) < 0f) right = -right;
            // ACROSS is the SHOULDER LINE when both are known, squared to body-up.
            if (upperL != null && upperL != upper)
            {
                Vector3 shoulderLine = upper.position - upperL.position;
                shoulderLine -= up * Vector3.Dot(shoulderLine, up);
                if (shoulderLine.sqrMagnitude > 1e-6f) right = shoulderLine.normalized;
            }
            return (up, right);
        }

        /// <summary>src/weapons.js:498/505 — chest-forward and clavicle-line-derived
        /// forward share one cross product; both SWAPPED here (GeoFrame rule):
        /// JS computes out = cross(up, right); Unity uses Cross(right, up).</summary>
        public static Vector3 ChestAxis(Vector3 up, Vector3 right)
        {
            Vector3 o = Vector3.Cross(right, up);
            o.y = 0f;
            return o.sqrMagnitude > 1e-8f ? o.normalized : Vector3.forward;
        }

        // src/weapons.js:517 bodyFwd — the elbow's "forward": pure aim-line at x=0,
        // the body's own chest-forward by the time the shot is all the way across.
        public static Vector3 BodyFwd(Vector3 up, Vector3 right, Vector3 fwdAim, float x)
        {
            Vector3 o = ChestAxis(up, right);
            if (x < 0.999f) o = Vector3.Lerp(o, fwdAim, 1f - x).normalized;
            return o;
        }

        /// <summary>src/weapons.js:491 aimAcross — which side of the body an aim
        /// direction is on: -1..1, POSITIVE toward the gun hand's own side, 0
        /// straight ahead or straight behind, negative toward the far side (the
        /// torso turns toward a shot by this before the arm has to cross the
        /// chest to reach it — foot.js's consumer). Not CrossBody (that one is
        /// 0..1 clamped of -dot, used to blend the ELBOW's forward, and its 0 IS
        /// the gun hand's own side — the two are deliberately different shapes
        /// for different callers, not a typo).
        /// `chainL` mirrors JS's `rig._armChainL` lazy cache (bodyAxes's own
        /// internal findArmChainL lookup, weapons.js:537) — pass `default` if
        /// unresolved, matching this file's own established call-site pattern
        /// (3 of ApplyPistolPose/ApplyAimPose/ApplyReachPose's 4 BodyAxes call
        /// sites already do the same; ApplyAimPose's own call is the one that
        /// passes a real chainL, same as here).
        /// NOT YET CALLED FROM Character/ (out of this agent's scope to wire;
        /// foot.js's torso-turn consumer lives there) — the primitive is ready.</summary>
        public static float AimAcross(Transform frame, Transform root, ArmChain chain, ArmChain chainL, Vector3? aim)
        {
            if (!chain.Valid || !aim.HasValue || aim.Value.sqrMagnitude < 1e-8f) return 0f;
            var (up, right) = BodyAxes(frame, root, chain.Upper, chainL.Upper);
            float len = aim.Value.magnitude;
            return len > 1e-8f ? Vector3.Dot(right, aim.Value) / len : 0f;
        }

        // ---- attaching + seating ------------------------------------------------

        /// <summary>src/weapons.js:218 attachToHand's measurement step — Unity's
        /// Renderer.bounds is already world-aligned post-transform, so (unlike the
        /// JS, which must zero the object's transform first to get a MODEL-space
        /// box) this measures the box directly off the mesh's LOCAL bounds union.</summary>
        public static void MeasureOnce(WeaponDef def, GameObject obj)
        {
            if (def.Measured) return;
            // Covers both static meshes (the pistols) and skinned ones (beretta_anim.glb's
            // slide/mag on bones, akm_anim.glb) — THREE.Box3.setFromObject(obj) walks both
            // kinds too, so matching it means checking both Unity renderer types.
            Bounds? b = null;
            foreach (var mf in obj.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var world = TransformBoundsLocal(mf.transform, obj.transform, mf.sharedMesh.bounds);
                b = b.HasValue ? Union(b.Value, world) : world;
            }
            foreach (var sm in obj.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sm.sharedMesh == null) continue;
                var world = TransformBoundsLocal(sm.transform, obj.transform, sm.sharedMesh.bounds);
                b = b.HasValue ? Union(b.Value, world) : world;
            }
            Bounds box = b ?? new Bounds(Vector3.zero, Vector3.zero);
            def.Rise = Mathf.Max(0f, box.max.y);
            float mid = (box.min.z + box.max.z) * 0.5f;
            def.Muzzle = new Vector3(box.max.x, box.max.y * 0.78f, mid);
            def.Port = new Vector3(box.max.x * 0.22f, box.max.y * 0.92f, box.max.z);
            def.Butt = Mathf.Max(0f, -box.min.x);
            def.Guard = box.max.x * 0.28f;
            def.Measured = true;
        }

        static Bounds TransformBoundsLocal(Transform meshT, Transform rootT, Bounds localBounds)
        {
            // express meshT's local bounds in rootT's local space (both assumed to share
            // the same orientation/scale chain here — these imports have one mesh at the
            // weapon's own root in practice, so this is exact for them).
            Vector3 c = meshT.TransformPoint(localBounds.center);
            c = rootT.InverseTransformPoint(c);
            Vector3 ext = localBounds.extents;
            Vector3 e2 = rootT.InverseTransformVector(meshT.TransformVector(ext));
            e2 = new Vector3(Mathf.Abs(e2.x), Mathf.Abs(e2.y), Mathf.Abs(e2.z));
            return new Bounds(c, e2 * 2f);
        }

        static Bounds Union(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        /// <summary>src/weapons.js:283 seatWeapon — orientation and seating, called
        /// EVERY FRAME (the carry pose moves the arm, so the gun has to follow).</summary>
        public static void SeatWeapon(Transform bone, Transform obj, WeaponDef def, Transform root,
            Vector3? aim = null, float dip = MuzzleDip, Vector3? upWant = null)
        {
            Vector3 bonePos = bone.position;
            Quaternion boneQuat = bone.rotation;
            float boneScale = bone.lossyScale.x;
            float inv = 1f / (Mathf.Abs(boneScale) > 1e-6f ? boneScale : 1f);

            Vector3 palm = bonePos;
            if (!IsHandBone(bone.name))
            {
                int kids = 0; Vector3 sum = Vector3.zero;
                foreach (Transform k in bone) { sum += k.position; kids++; }
                if (kids > 0) palm = sum / kids;
                else if (bone.parent != null) palm = bonePos + (bonePos - bone.parent.position) * 0.35f;
            }

            Vector3 forward = new Vector3(0, 0, 1); // HANDEDNESS: Unity forward is +Z
            if (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f)
            {
                forward = aim.Value.normalized;
            }
            else
            {
                if (root != null) forward = root.rotation * new Vector3(0, 0, 1);
                else forward = palm - bonePos;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-8f) forward = new Vector3(0, 0, 1);
                forward.Normalize();
            }
            if (dip != 0f)
            {
                forward.y -= Mathf.Tan(dip);
                forward.Normalize();
            }

            Vector3 up = (upWant.HasValue && upWant.Value.sqrMagnitude > 1e-8f) ? upWant.Value.normalized : Vector3.up;
            if (Mathf.Abs(Vector3.Dot(up, forward)) > 0.95f) up = Vector3.forward;
            // HANDEDNESS: JS right = cross(forward,up); up = cross(right,forward). SWAPPED.
            Vector3 right = Vector3.Cross(up, forward).normalized;
            up = Vector3.Cross(forward, right).normalized;
            // model +X -> forward (muzzle), +Y -> up (slide), +Z -> right
            Quaternion wantWorld = QuaternionFromBasis(forward, up, right);
            var extra = def.GripRotEuler;
            if (extra.HasValue) wantWorld *= Quaternion.Euler(extra.Value * Mathf.Rad2Deg);

            // Sit the weapon UNDER the hand, not centred on it (src/weapons.js:353-369).
            // Done in WORLD space throughout (rather than the JS's local-space adds under
            // `bone`) because `inv` already undoes the bone's own scale, which makes world
            // space the simpler, equally-correct frame to work in here.
            if (obj.transform.parent != bone) obj.transform.SetParent(bone, true);
            obj.transform.rotation = wantWorld;
            obj.transform.position = palm;
            Vector3 drop = wantWorld * new Vector3(0f, -def.Rise * (def.Scale > 0f ? def.Scale : 1f) * inv * DropFraction, 0f);
            obj.transform.position += drop;
            if (def.GripPos != Vector3.zero)
                obj.transform.position += wantWorld * (def.GripPos * inv);
        }

        /// <summary>src/weapons.js:218 attachToHand — measures the model once, sets the
        /// scale that undoes the bone's inherited body-normalisation scale, then calls
        /// SeatWeapon for the initial pose (which must be re-called every frame
        /// afterwards as the carry/aim pose moves the arm).</summary>
        public static void AttachToHand(Transform bone, GameObject obj, WeaponDef def, Transform root)
        {
            MeasureOnce(def, obj);
            float boneScale = bone.lossyScale.x;
            obj.transform.localScale = Vector3.one * (def.Scale / (Mathf.Abs(boneScale) > 1e-6f ? boneScale : 1f));
            // VFX note: src/weapons.js's HAND_OVER_GUN polygon-offset trick (biasing the
            // weapon a hair further from the camera so the hand draws over it) has no
            // Unity equivalent needed here — ordinary depth testing handles two opaque
            // meshes at different depths correctly; the JS only needed it because its
            // hand and gun meshes sit almost exactly coplanar at a few cm apart.
            SeatWeapon(bone, obj.transform, def, root);
        }

        // ---- GTA-style pistol carry / aim poses --------------------------------

        /// <summary>src/weapons.js:629 applyPistolPose — locks the gun arm into a
        /// tucked hip-carry. `frame` is the rig's tumble/recline layer (or root).</summary>
        public static bool ApplyPistolPose(Transform frame, Transform root, ArmChain chain, Transform hand, Vector3? aim = null)
        {
            if (!chain.Valid || hand == null) return false;
            Vector3 fwd = AimLine(aim, root.rotation);
            var (up, right) = BodyAxes(frame, root, chain.Upper);

            float x = CrossBody(fwd, right);
            Vector3 bf = BodyFwd(up, right, fwd, x);
            Vector3 dir = up * (-1f + fwd.y * 0.35f + 1.15f * x) + bf * (0.12f + 0.85f * x) + right * (0.26f - 0.45f * x);
            AimBone(chain.Upper, chain.Fore, dir.normalized);

            dir = fwd + up * 0.30f + right * 0.12f;
            AimBone(chain.Fore, hand, dir.normalized);
            return true;
        }

        /// <summary>src/weapons.js:566 applyAimPose — the two-handed aimed hold, gun
        /// arm up to eye line, support hand crossed in to meet it.</summary>
        public static bool ApplyAimPose(Transform frame, Transform root, ArmChain chain, ArmChain chainL, Transform hand, Vector3? aim = null)
        {
            if (!chain.Valid || hand == null) return false;
            Vector3 fwd = AimLine(aim, root.rotation);
            var (up, right) = BodyAxes(frame, root, chain.Upper, chainL.Upper);

            float x = CrossBody(fwd, right);
            Vector3 bf = BodyFwd(up, right, fwd, x);
            Vector3 dir = (bf * (1f - 0.1f * x) + up * (-0.55f + 0.7f * x) + right * (0.22f - 0.5f * x)).normalized;
            AimBone(chain.Upper, chain.Fore, dir);
            dir = (fwd + up * 0.10f + right * -0.10f).normalized;
            AimBone(chain.Fore, hand, dir);

            if (chainL.Valid)
            {
                Vector3 gunPos = hand.position;
                Vector3 lp = chainL.Upper.position;
                Vector3 d = (gunPos - lp).normalized;
                d = (d + up * (0.55f * Mathf.Max(0f, Mathf.Min(1f, Vector3.Dot(d, right))))).normalized;
                AimBone(chainL.Upper, chainL.Fore, d);
                Vector3 fp = chainL.Fore.position;
                Vector3 d2 = gunPos - fp;
                if (d2.sqrMagnitude > 1e-8f)
                {
                    d2.Normalize();
                    Transform lhand = chainL.Fore.childCount > 0 ? chainL.Fore.GetChild(0) : null;
                    if (lhand != null) AimBone(chainL.Fore, lhand, d2);
                }
            }
            return true;
        }

        static readonly Vector3 ReachHintDefault = new Vector3(-0.75f, 0.55f, -0.35f); // (up, right, fwd)

        /// <summary>src/weapons.js:691 applyReachPose — a two-bone IK solve to a world
        /// target point, with the elbow placed by a hint expressed in the body's own
        /// axes. Used while a pistol travels to/from the holster so the hand follows
        /// the weapon's authored path instead of snapping.</summary>
        public static bool ApplyReachPose(Transform frame, Transform root, ArmChain chain, Transform hand,
            Vector3 target, Vector3? aim, Vector3? hintUpRightFwd = null)
        {
            if (!chain.Valid || hand == null) return false;
            Vector3 fwd = AimLine(aim, root.rotation);
            var (up, right) = BodyAxes(frame, root, chain.Upper);

            Vector3 S = chain.Upper.position, E = chain.Fore.position, H = hand.position;
            float l1 = Vector3.Distance(S, E), l2 = Vector3.Distance(E, H);
            if (l1 < 1e-5f || l2 < 1e-5f) return false;
            Vector3 toT = target - S;
            float d = toT.magnitude;
            if (d < 1e-5f) return false;
            toT /= d;
            d = Mathf.Min(d, (l1 + l2) * 0.985f); // never quite straight
            float cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);

            Vector3 hint = hintUpRightFwd ?? ReachHintDefault;
            Vector3 hintDir = up * hint.x + right * hint.y + fwd * hint.z;
            Vector3 perp = hintDir - toT * Vector3.Dot(hintDir, toT);
            if (perp.sqrMagnitude < 1e-4f) perp = right - toT * Vector3.Dot(right, toT);
            perp.Normalize();

            Vector3 elbow = S + toT * (l1 * cosA) + perp * (l1 * sinA);
            AimBone(chain.Upper, chain.Fore, (elbow - S).normalized);
            Vector3 elbowNow = chain.Fore.position; // where it actually landed
            AimBone(chain.Fore, hand, (target - elbowNow).normalized);
            return true;
        }
    }
}
