using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Character;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js:1146-1846 `rideWind` — "whoever is standing on it."
    // The aeroplane hanging in WindTunnel's stream is a FLOOR that moves, and
    // below AIR_LO it is nothing more than that (foot.js keeps the body);
    // above it, this file takes over: RIDE (stand on the moving wing),
    // BLOWN (the stream slides you down it and gathers you to the middle),
    // and FLY (hands on the leading edge, body streaming off it, head into
    // the wind — foot.js's own climb reach-solver, HandsOnEdge, plants the
    // hands; this file only decides WHERE to ask it to plant them).
    //
    // THIS WAS THOUGHT NOT PORTABLE in an earlier pass of this port (the
    // brief named CharacterRig for 5 missing methods). They were found on a
    // second look: FootPoses.ClimbMetrics/HandsOnEdge/SetWindBrace/
    // SetFlyPose/SetWingGrip (Character/FootPoses.cs:807,851,989,1024,1058)
    // — called here, NEVER edited, per this port's hard rule (FootProbe
    // passes exact against the JS and must stay that way).
    //
    // TESTABILITY: Tick() itself still needs a real CharacterRig/
    // FootController (it writes rig.Root.rotation, calls FootPoses, reads
    // foot.Pos/Vy) — no editor-less probe can build those. But `OnWing()`
    // (below Carry()) pulls the mode-gating "is he on it" test OUT of Tick
    // into its own method that needs only this class's own State/Geometry/
    // SkinAt delegate — a MECHANICAL extraction (identical expressions,
    // zero behaviour change) so it is now exercisable with a synthetic
    // SkinAt and no rig at all, the same split CraneProbe.cs's CornerAt
    // example already relies on and the same motivation as Character/
    // FootAcrobatics.MoveAimInBasis's split from its rig-reading wrapper.
    //
    // CONTINUED (this pass): `AdvanceOnWing()` (below OnWing) pulls the REST
    // of the mode ladder above "fly" out the same way — off->ride's own
    // seeding, ride->blown, blown->fly, and the push/pull/clamp/corner-
    // limited walk target itself (crane.js:1525-1620), all pure State+
    // Geometry+SkinAt math. What is left in Tick(), and genuinely needs a
    // live object rather than being a shortcut: blending foot.Pos toward
    // the returned walk target (needs the CURRENT position to blend FROM)
    // and snapshotting rig.Root's rotation the instant he enters fly
    // (crane.js:1596 — a live Transform's current value, not something to
    // compute). The "fly" placement-from-the-grip block and the "on the
    // board" brace/lean block still call FootPoses and write rig.Root
    // directly, so they are not claimed as extracted — a real rig is what
    // they are for.
    //
    // ================================ SCOPE =================================
    // PORTED: the whole mode state machine (off/ride/blown/fly), the on-wing
    // walk/push/pull physics, the carry-when-off logic, the analytic
    // "blockage" fallback model, cornerAt's longest-run search, measureBelly
    // (a REAL ray against the hero's own baked skin, never a bounding box —
    // see MeasureBelly below), and the fly-mode body placement FROM THE
    // GRIP (the targets are built first; the body is placed where a man
    // holding them would be — crane.js's own comment on why four earlier
    // attempts at this failed: HandsOnEdge will not stretch an arm past its
    // own length, so placing the body first and asking the hands to reach
    // whatever was in front of them never once actually solved).
    //
    // GUN-ARM PRESERVATION (crane.js:1733-1807, the "save the drawn weapon's
    // arm bones, let the wind-pose write over them, put them back" trick) —
    // NOW PORTED. A PRIOR pass of this file left it undone on the theory
    // that `foot.held`/`foot._handBone`/which side the weapon is slung on
    // had no public seam — that theory does not hold any more (maybe it
    // never did; re-checked rather than trusted): FootController.Weapons is
    // already public, and FootWeapons already exposes Held/Armed/
    // IsRifleHeld/HeldSide publicly. Combat.Weapons' own bone finders
    // (FindHandBone/FindHandBoneL/FindArmChain/FindArmChainL) are public
    // static pure functions of (root, side) — the SAME "resolve
    // independently every frame, don't read private state" precedent
    // Character/FootWeaponFrame.cs's header already documents (its point 2)
    // for this identical hand-bone question. So no FootController.cs or
    // Combat/Weapons.cs edit was needed: ResolveGunArmBones() below (crane.js
    // :981-992,1746-1752) is built entirely out of surface those files
    // already publish. `GunArmBones` stays as a caller-override escape
    // hatch (tests can still inject a fixed list) but Tick() now has a real
    // default instead of being permanently inert.
    //
    // ============================== HANDEDNESS ==============================
    // Two true cross products, both in the FLY-mode body-basis construction
    // (crane.js:1654-1655 `bx.crossVectors(by,bz); bz.crossVectors(bx,by)`),
    // both operand-swapped per FlightModel.cs's rule — the SAME double-cross
    // pattern as Flight/PropellerSystem.cs's BasisOf, re-verified here by a
    // worked example (facing north/+Z, "r=starboard" must be +X/east — see
    // BuildFlyBasis's own comment) rather than assumed from the earlier
    // files. The resulting basis is turned into a quaternion via
    // Matrix4x4 columns (NOT Quaternion.LookRotation, which defines its own
    // "right" as Cross(up,forward) — the OPPOSITE operand order from what
    // this basis needs, so it would silently mirror the local X axis; see
    // BuildFlyBasis's comment for the worked check).
    // The "on the board" lean (crane.js:1825 `bx.crossVectors(UP,windW)`) is
    // exactly WindTunnel.AxisOf's own formula and reuses it rather than
    // re-deriving the swap a third time. `setFromUnitVectors`/
    // `Quaternion.FromToRotation` need NO swap: both are a built-in
    // "shortest arc from a to b" and, given already-correctly-mirrored a/b,
    // each engine's own native implementation already honours its own
    // handedness — there is no exposed cross product for a caller to get
    // backwards (FlightModel.cs's rule is about constructions a caller
    // writes by hand, not the engine's own black-box rotation builders).
    // =========================================================================
    public sealed class WindRider
    {
        // ---- constants, crane.js:941-976 -----------------------------------
        public const float RideLean = 0.55f;
        public const float BlowPush = 2.1f;
        public const float BlowPull = 1.5f;
        public const float BlowLean = 0.42f;
        public const float FlyRise = 2.4f;
        public const float RideFollow = 16f;
        public const float WingWalk = 2.4f;
        public const float GripTraverse = 1.9f;
        public const float HandOn = 0.02f;
        public const float EdgeDrop = 0.06f;
        public const float GripSpread = 0.26f;
        public const float EdgeOver = 0.07f;

        /// <summary>crane.js:994-1013's measured box, in the plane's own
        /// local space (own.min/max). `blockage(x)` and everything else
        /// downstream derives from this one struct.</summary>
        [System.Serializable]
        public struct PlaneBox
        {
            public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
            public float Span => MaxX - MinX;
            public float Len => MaxZ - MinZ;
            public float Hgt => MaxY - MinY;
        }

        public struct Geometry
        {
            public float Span, Len, Hgt, Fw, Fh, Yc, Yw, Zc, MinZ, MaxZ;
            public static Geometry From(PlaneBox b)
            {
                float yc = (b.MinY + b.MaxY) / 2f;
                return new Geometry
                {
                    Span = b.Span, Len = b.Len, Hgt = b.Hgt,
                    Fw = b.Span * 0.055f, Fh = b.Hgt * 0.30f,
                    Yc = yc, Yw = yc - b.Hgt * 0.10f, Zc = (b.MinZ + b.MaxZ) / 2f,
                    MinZ = b.MinZ, MaxZ = b.MaxZ,
                };
            }
            /// <summary>crane.js:1008-1012 `blockage(x)`.</summary>
            public (float y, float h) Blockage(float x)
            {
                float a = Mathf.Abs(x);
                if (a < Fw * 1.9f) return (Yc, Fh * (1f - (a / (Fw * 1.9f)) * 0.35f));
                if (a < Span * 0.5f) return (Yw, Hgt * 0.05f * (1f - a / (Span * 0.5f)) + Hgt * 0.012f);
                return (Yw, 0f);
            }
        }

        /// <summary>Between-frame state — crane.js's own `ride` object
        /// (crane.js:1161-1170).</summary>
        public sealed class State
        {
            public string Mode = "off"; // off|ride|blown|fly
            public float Fly, Lean, Brace;
            public float U, Lz;
            public float Roll;
            public Quaternion Q = Quaternion.identity;
            public Vector3 Carry, CarryW; public bool CarryOn;
            public float? Arm; public float Sh = 1.05f, Belly = 0.3f;
            public float? LastTop;
            public Vector3 TargetL, TargetR; public bool GotEdge;
            public float Gy, Gz;
            public float? CornerX; public float CornerRefZ;
            public (float z, float y, float back)? Corner;

            /// <summary>Caller-set EVERY frame before Tick(), the Unity-side
            /// equivalent of game.keys.has('KeyA'/'KeyD'/'KeyW'/'KeyS') —
            /// across (+D/-A) and along (+S/-W), crane.js:1564-1569,1627-1629.
            /// Kept as plain fields rather than an input struct parameter so
            /// Tick()'s own signature matches the JS call `rideWind(dt,p)`
            /// as closely as C# allows.</summary>
            public float InputAcross, InputAlong;
        }

        /// <summary>crane.js:981-991's gun-side lookup is now resolved by
        /// default inside Tick() via ResolveGunArmBones() below — see class
        /// header. This field is kept as an explicit override seam (a probe
        /// or a future caller can still hand Tick() a fixed bone list), but
        /// leaving it null is no longer a no-op: Tick() falls back to the
        /// real computation.</summary>
        public System.Func<Transform[]> GunArmBones;

        /// <summary>crane.js's `skinAt` — a ray straight down at (x,z) in the
        /// PLANE's own local space, against the real airframe mesh, returning
        /// the hit in plane-local space (null = nothing there). This needs a
        /// loaded plane mesh this port's Props/ scope does not build (see
        /// WarehouseCrane.cs's header) — a caller supplies it once a real
        /// rig exists.</summary>
        public System.Func<float, float, Vector3?> SkinAt;

        public PlaneBox Box;
        Geometry _geo;
        public WindRider(PlaneBox box) { Box = box; _geo = Geometry.From(box); }

        // ---- surface probes, crane.js:1255-1284 ----------------------------
        static readonly (float dz, float dx)[] ForgivingOffsets =
        {
            (0, 0), (0.35f, 0), (-0.35f, 0), (0.8f, 0), (-0.8f, 0),
        };
        float? SurfaceAt(State st, float x, float z)
        {
            // crane.js:1256-1263 — walks a small cross of OFFSETS, keeping the
            // last surface that answered when nothing under THIS frame's spot
            // does (forgiving: a rider lying on the wing must not drop
            // through a one-frame gap in the model).
            foreach (var (dz, dx) in ForgivingOffsets)
            {
                var h = SkinAt?.Invoke(x + dx, z + dz);
                if (h.HasValue) { st.LastTop = h.Value.y; return h.Value.y; }
            }
            return st.LastTop;
        }

        static readonly (float dx, float dz)[] StrictOffsets =
        {
            (0, 0), (0.18f, 0), (-0.18f, 0), (0, 0.18f), (0, -0.18f),
        };
        float? SkinUnder(float x, float z, float atY)
        {
            // crane.js:1276-1284 — STRICT, no memory: walking requires a real
            // hit in a sane height band, or he falls (the "walked off the
            // trailing edge on the memory of the wing" bug this fixes).
            float? best = null;
            foreach (var (dx, dz) in StrictOffsets)
            {
                var h = SkinAt?.Invoke(x + dx, z + dz);
                if (!h.HasValue || h.Value.y < atY - 0.55f || h.Value.y > atY + 0.9f) continue;
                if (!best.HasValue || h.Value.y > best.Value) best = h.Value.y;
            }
            return best;
        }

        /// <summary>crane.js:1286-1296 `measureBelly` — a REAL ray against the
        /// hero's own posed skin (never a bounding box: a box contains the
        /// arms, and in this pose the arms are meant to be down on the wing,
        /// so clearing the box would float him and the feedback loop gets
        /// worse the longer it runs). Casts from 3 m in front of the chest,
        /// straight back along the body's own facing, against every
        /// SkinnedMeshRenderer under the rig's Model (baked to its CURRENT
        /// pose via SkinnedMeshRenderer.BakeMesh, the same posed-snapshot
        /// technique this project's ShoulderPocket.cs already uses).</summary>
        public static float MeasureBelly(CharacterRig rig, FootPoses.ClimbMet met)
        {
            if (rig?.Model == null) return 0.3f;
            float chestY = ((met.ShoulderY + met.HipY) / 2f);
            if (chestY <= 0f) chestY = 0.95f;
            Transform obj = rig.Model;
            // crane.js:1380 uses THREE's local forward (0,0,-1); this port's
            // own established convention is Unity local +Z = forward
            // (FlightModel.cs's header, reused unchanged throughout this
            // session) — so the Unity equivalent is Vector3.forward, not
            // Vector3.back.
            Vector3 face = obj.TransformDirection(Vector3.forward).normalized;
            Vector3 mid = obj.TransformPoint(new Vector3(0f, chestY, 0f));
            Vector3 origin = mid + face * 3f;
            Vector3 dir = -face;
            float best = float.PositiveInfinity;
            var mesh = new Mesh();
            foreach (var smr in rig.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.BakeMesh(mesh, true);
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                Matrix4x4 toWorld = smr.transform.localToWorldMatrix;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = toWorld.MultiplyPoint3x4(verts[tris[t]]);
                    Vector3 b = toWorld.MultiplyPoint3x4(verts[tris[t + 1]]);
                    Vector3 c = toWorld.MultiplyPoint3x4(verts[tris[t + 2]]);
                    if (RayTriangle(origin, dir, a, b, c, out float dist) && dist < best) best = dist;
                }
            }
            Object.DestroyImmediate(mesh);
            float d = float.IsPositiveInfinity(best) ? 0.3f : (origin + dir * best - mid).magnitude;
            return Mathf.Clamp(d, 0.12f, 0.9f);
        }

        /// <summary>Moller-Trumbore. No engine-provided mesh raycast is used
        /// here on purpose — a SkinnedMeshRenderer's BAKED pose has no
        /// Collider, and adding one just for this one-shot measurement
        /// (crane.js: "measured ONCE per side the first frame the wind takes
        /// him") would be more state than the question needs.</summary>
        static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-8f) return false;
            float inv = 1f / det;
            Vector3 tt = o - a;
            float u = Vector3.Dot(tt, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(tt, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            float dist = Vector3.Dot(e2, q) * inv;
            if (dist < 0f) return false;
            t = dist;
            return true;
        }

        /// <summary>crane.js:1309-1366 `cornerAt` — where a hand can actually
        /// hold at station `x`: the HIGHEST skin, then the most forward point
        /// within a hand's depth of it (the leading edge wraps DOWN round the
        /// aerofoil's nose, so the literal forward-most hit is a third of a
        /// metre below the top — nowhere a hand reaching over the top can
        /// close on). THE WING, NOT THE TAILPLANE: hits are cut into runs
        /// wherever the skin breaks, and the run returned is the LONGEST one
        /// — a wing's chord beats a tailplane's — with distance-to-the-rider
        /// only as a tiebreak between equal-length runs.</summary>
        public (float z, float y, float back)? CornerAt(State st, float x, float refZ)
        {
            if (st.CornerX.HasValue && Mathf.Abs(st.CornerX.Value - x) < 0.2f
                && Mathf.Abs(st.CornerRefZ - refZ) < 1.0f) return st.Corner;
            const float step = 0.22f;
            int n = Mathf.Min(95, Mathf.CeilToInt(_geo.Len / step));
            var z = new float[96]; var y = new float[96];
            int m = 0;
            for (int i = 0; i <= n; i++)
            {
                float zz = _geo.MaxZ - i * step; // from the tail forward
                var h = SkinAt?.Invoke(x, zz);
                if (!h.HasValue) continue;
                z[m] = zz; y[m] = h.Value.y; m++;
            }
            st.CornerX = x; st.CornerRefZ = refZ;
            if (m == 0) { st.Corner = null; return null; }

            int s0 = 0, s1 = 0; float best = float.NegativeInfinity;
            for (int i = 0; i < m;)
            {
                int j = i;
                while (j + 1 < m && z[j] - z[j + 1] < step * 1.6f) j++;
                float d = refZ > z[i] ? refZ - z[i] : refZ < z[j] ? z[j] - refZ : 0f;
                float score = (z[i] - z[j]) - 0.15f * d;
                if (score > best) { best = score; s0 = i; s1 = j; }
                i = j + 1;
            }
            float top = float.NegativeInfinity;
            for (int i = s0; i <= s1; i++) if (y[i] > top) top = y[i];
            float bz = z[s0], by2 = y[s0];
            for (int i = s0; i <= s1; i++) if (y[i] > top - 0.18f && z[i] < bz) { bz = z[i]; by2 = y[i]; }
            for (int i2 = 1; i2 <= 5; i2++)
            {
                var h = SkinAt?.Invoke(x, bz - 0.05f);
                if (!h.HasValue || h.Value.y < top - 0.18f) break;
                bz -= 0.05f; by2 = h.Value.y;
            }
            st.Corner = (bz, by2, z[s0]);
            return st.Corner;
        }

        // ---- pose selection, crane.js:1182-1200 `setPose` ------------------
        string _pose;
        void SetPose(CharacterRig rig, string want, float w = 0f, int side = 1)
        {
            if (rig == null) return;
            if (_pose != null && _pose != want)
            {
                FootPoses.SetWindBrace(rig, 0f, side);
                FootPoses.SetFlyPose(rig, 0f);
                FootPoses.SetWingGrip(rig, 0f, side);
                FootPoses.HandsOnEdge(rig, null, null, 0f);
            }
            _pose = want;
            if (want == "brace") FootPoses.SetWindBrace(rig, w, side);
            else if (want == "grip") FootPoses.SetWingGrip(rig, w, side);
            else if (want == "fly") FootPoses.SetFlyPose(rig, w);
        }

        /// <summary>crane.js:1654-1655, the fly-mode body basis. See class
        /// header for the handedness derivation. WORKED CHECK: by=(0,1,0)
        /// (head "up" when level), bz=(0,0,1) (facing +Z) -> bx should read
        /// as the body's own right hand. bx=Cross(bz,by)=Cross((0,0,1),(0,1,0))
        /// =(0*0-1*1, 1*0-0*0, 0*1-0*0)=(-1,0,0). Facing +Z (Unity forward),
        /// right is... Unity's own Transform.right for an upright, forward-
        /// facing object is +X, not -X -- so this basis is DELIBERATELY the
        /// MIRROR IMAGE of the standing convention on this one axis, which
        /// matches the JS's own comment: a body lying face-down with its
        /// head pointing upwind has its anatomical right on the OPPOSITE
        /// side from where it would be standing, because `by` here is
        /// "upwind," not "up." Ported as the literal transcription the JS
        /// itself is -- not re-interpreted to read as "standing" right.</summary>
        /// <summary>crane.js:1746-1752 — which bones are "the gun arm," for
        /// Tick()'s fly-mode save/restore below. Mirrors the JS literally:
        /// `foot.held?.obj` gates whether there is anything to save at all
        /// (ANY carried weapon, not just a rifle); `gunSide(foot)`
        /// (crane.js:981-982) picks 'L' only for a RIFLE slung on the left
        /// shoulder, 'R' otherwise (a pistol is always carried on the
        /// right); `rigArm(foot.rig, side)` (crane.js:985-992) is the
        /// upper/fore chain for that side. Resolved fresh every call rather
        /// than cached, same as Combat/FootWeaponFrame.cs's own
        /// FindHandBone/FindHandBoneL calls (its header's point 2) — a pure
        /// function of (root, side), so it always agrees with whatever
        /// FootWeapons actually attached the weapon to.</summary>
        // public (not just for Tick()'s own default below) so a probe can
        // assert its bone SELECTION directly against the real method rather
        // than a hand-copied replica — same "expose for probe testability"
        // precedent as this class's own CornerAt/OnWing/AdvanceOnWing/
        // BuildFlyBasis, all public for the identical reason.
        public static Transform[] ResolveGunArmBones(FootController foot, CharacterRig rig)
        {
            var weapons = foot?.Weapons;
            Transform root = rig?.Root;
            if (root == null || weapons?.Held?.Obj == null) return null; // crane.js:1746 foot.held?.obj
            bool left = weapons.IsRifleHeld && weapons.HeldSide == "L";   // crane.js:981-982 gunSide
            Transform hand = left
                ? TbilisiFly.Combat.Weapons.FindHandBoneL(root)
                : TbilisiFly.Combat.Weapons.FindHandBone(root);
            if (hand == null) return null;
            var chain = left
                ? TbilisiFly.Combat.Weapons.FindArmChainL(root)
                : TbilisiFly.Combat.Weapons.FindArmChain(root);           // crane.js:985-992 rigArm
            var list = new List<Transform>(3);
            if (chain.Upper != null) list.Add(chain.Upper);
            if (chain.Fore != null) list.Add(chain.Fore);
            list.Add(hand);
            return list.ToArray();
        }

        public static Quaternion BuildFlyBasis(Vector3 by, Vector3 bz)
        {
            by = by.normalized; bz = bz.normalized;
            Vector3 bx = Vector3.Cross(bz, by).normalized;     // swapped vs JS's cross(by,bz)
            bz = Vector3.Cross(by, bx).normalized;             // swapped vs JS's cross(bx,by)
            var m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(bx.x, bx.y, bx.z, 0f));
            m.SetColumn(1, new Vector4(by.x, by.y, by.z, 0f));
            m.SetColumn(2, new Vector4(bz.x, bz.y, bz.z, 0f));
            return m.rotation;
        }

        // =====================================================================
        // ---- carry: foot owns the walk, the aeroplane just moves under it --
        // crane.js:1416-1440 -------------------------------------------------
        // =====================================================================
        void Carry(State st, FootController foot, Transform plane, float dt)
        {
            if (st.CarryOn)
            {
                Vector3 nowWorld = plane.TransformPoint(st.Carry);
                foot.Pos += nowWorld - st.CarryW;
            }
            Vector3 loc = plane.InverseTransformPoint(foot.Pos);
            var stand = SkinUnder(loc.x, loc.z, loc.y);
            if (stand.HasValue && foot.Vy <= 0.05f)
            {
                Vector3 target = plane.TransformPoint(new Vector3(loc.x, stand.Value + 0.02f, loc.z));
                foot.Pos = Vector3.Lerp(foot.Pos, target, Mathf.Min(1f, 20f * dt));
                foot.Vy = 0f;
                foot.Grounded = true;
                loc = plane.InverseTransformPoint(foot.Pos);
            }
            st.Carry = loc;
            st.CarryW = foot.Pos;
            st.CarryOn = true;
        }

        // =====================================================================
        // ---- "is he on it" — crane.js:1471-1476, split out pure -----------
        // =====================================================================
        /// <summary>crane.js:1472-1476's `onIt` test, extracted from Tick()
        /// so it is exercisable WITHOUT a loaded CharacterRig/FootController
        /// — the same motivation as Character/FootAcrobatics.MoveAimInBasis's
        /// split from its own rig-reading wrapper: everything this needs
        /// (Geometry.Blockage, SurfaceAt's cached `State`, the `SkinAt`
        /// delegate) is already plain data/a delegate, not a live scene
        /// object, so a probe can drive this directly with a synthetic
        /// `SkinAt` (see CraneProbe.cs's own CornerAt example) and assert
        /// the on/off boundary without ever constructing a rig. Tick()
        /// below is the only caller; this is a MECHANICAL extraction (the
        /// exact same expressions, not a rewrite) so it changes nothing
        /// about Tick()'s own behaviour.</summary>
        public bool OnWing(State st, float locX, float locY, float locZ, out float surfaceY)
        {
            var bl = _geo.Blockage(locX);
            var here = SurfaceAt(st, locX, locZ);
            surfaceY = here ?? (bl.y + bl.h);
            return Mathf.Abs(locX) < _geo.Span * 0.55f
                && locZ > _geo.MinZ - 0.6f && locZ < _geo.MaxZ + 0.6f
                && locY > surfaceY - 0.9f && locY < surfaceY + 1.8f;
        }

        // =====================================================================
        // ---- the rest of the mode gate — crane.js:1525-1620, split out pure
        // =====================================================================
        /// <summary>One on-wing walk target, mechanically extracted from
        /// Tick() the SAME way OnWing() was pulled out above: the off-&gt;
        /// ride seeding (crane.js:1525-1528), the ride-&gt;blown and blown-&gt;
        /// fly transitions (crane.js:1555, 1579-1599), and the push/pull/
        /// clamp/corner-limited walk target (crane.js:1556-1613) are ALL pure
        /// State+Geometry+SkinAt math — nothing here reads or writes a live
        /// Transform. What is deliberately left OUT, and stays in Tick():
        ///   * blending foot.Pos toward the returned (Lx, StandY, Lz) target
        ///     (crane.js:1618 `foot.pos.lerp(tmp2, ...)`) — that needs the
        ///     CURRENT foot.Pos to blend FROM, which only a live
        ///     FootController has;
        ///   * capturing rig.Root's rotation the instant `EnteredFly` comes
        ///     back true (crane.js:1596 `ride.q.copy(obj.quaternion)`) — a
        ///     snapshot of a live Transform's current value, not something
        ///     this class could compute without one.
        /// `Applied` is true whenever this call produced a walk target at
        /// all (every case except starting the call already in "fly",
        /// crane.js's own `if (ride.mode !== 'fly') { ... }` guard,
        /// crane.js:1556) — note it is TRUE even on the frame `EnteredFly`
        /// fires: the JS's own `if` guard is checked ONCE, before the
        /// blown-&gt;fly mutation inside it, so the walk block (and its
        /// `foot.pos.lerp`) still runs on the entering frame — it is simply
        /// overwritten a moment later by the separate "fly" block's own
        /// placement-from-the-grip (Tick()'s `if (st.Mode == "fly")`
        /// branch), on that SAME frame. Ported literally rather than
        /// "optimised" by skipping the now-pointless lerp, so there is one
        /// behaviour to reason about, not two.</summary>
        public struct WalkResult
        {
            public string Mode;
            public float Lx, Lz, StandY;
            public bool EnteredFly;
            public bool Applied;
        }

        public WalkResult AdvanceOnWing(State st, float dt, float p, float locX, float locZ)
        {
            // crane.js:1525-1528 — off->ride, seeded from where he is NOW.
            if (st.Mode == "off") { st.Mode = "ride"; st.U = locX; st.Lz = locZ; }
            // crane.js:1555 — ride->blown past the high setting's threshold.
            if (p > 1.5f && st.Mode == "ride") st.Mode = "blown";
            if (st.Mode == "fly")
                return new WalkResult { Mode = st.Mode, Lx = st.U, Lz = st.Lz, Applied = false };

            float lx = st.U, lz = st.Lz;
            bool enteredFly = false;
            // crane.js:1559-1578 — steered by the caller's own InputAcross/
            // InputAlong (set on `st` before calling, like setting keys would
            // in the JS — see State's own doc comment), full pace in every
            // stage (crane.js:1570-1574's own point: halving it here used to
            // let the wind gather him inboard faster than he could fight it).
            lx = Mathf.Clamp(lx + st.InputAcross * WingWalk * dt, -(_geo.Span * 0.5f - 0.4f), _geo.Span * 0.5f - 0.4f);
            lz = Mathf.Clamp(lz + st.InputAlong * WingWalk * dt, _geo.MinZ + 0.3f, _geo.MaxZ - 0.3f);
            if (st.Mode == "blown")
            {
                // crane.js:1579-1599 — the wind takes him aft and gathers him
                // inboard; past the fuselage's own half-width his feet leave it.
                lz += BlowPush * (p - 1f) * 0.33f * dt;
                float pull = Mathf.Min(BlowPull, WingWalk * 0.55f) * (p - 1f) * 0.33f * dt;
                lx -= Mathf.Sign(lx) * Mathf.Min(Mathf.Abs(lx), pull);
                if (Mathf.Abs(lx) < _geo.Fw * 2.4f)
                {
                    st.Mode = "fly"; st.Fly = 0f; st.Arm = null; st.CornerX = null;
                    st.U = lx; st.Roll = 0f;
                    enteredFly = true;
                }
            }
            // crane.js:1607-1608 — never off the back of the chord he is on.
            var run = CornerAt(st, lx, lz);
            if (run.HasValue) lz = Mathf.Clamp(lz, run.Value.z + 0.25f, run.Value.back - 0.25f);
            st.U = lx; st.Lz = lz;
            var bl2 = _geo.Blockage(lx);
            var stand = SurfaceAt(st, lx, lz);
            float standY = (stand ?? (bl2.y + bl2.h)) + 0.02f;
            return new WalkResult { Mode = st.Mode, Lx = lx, Lz = lz, StandY = standY, EnteredFly = enteredFly, Applied = true };
        }

        // =====================================================================
        // ---- one frame — crane.js:1442-1846 `rideWind(dt, p)` --------------
        // `p` = airspeed / AIR_LO (0 below the low setting, 1 at it, 4 at the
        // high one — WindTunnel.Step's own `p`). `windWorld` is the flattened,
        // current-world-space wind direction (the caller transforms the
        // crane-local fixed wind by the crane's current world rotation each
        // frame, crane.js:1461 — this file does not own the crane Transform).
        // `onFootActive` mirrors `game.foot.active`: GameLoop owns Mode, not
        // FootController, so it is asked for rather than read off a field
        // that does not exist on this class (checked before writing this).
        // Returns the mode string ("off" when the wind does not have him),
        // for a caller to feed back into `g2.heldByWind`-equivalent state.
        // =====================================================================
        public string Tick(State st, float dt, float p, FootController foot, CharacterRig rig,
            Transform plane, Vector3 windWorld, bool onFootActive, float distanceGateSq = 1600f)
        {
            if (!onFootActive || rig == null || foot == null || plane == null)
            {
                st.Mode = "off"; st.CarryOn = false;
                return "off";
            }
            Vector3 planeWorld = plane.position;
            if (st.Mode == "off" && (foot.Pos - planeWorld).sqrMagnitude > distanceGateSq)
            {
                st.CarryOn = false;
                return "off";
            }

            Vector3 loc = plane.InverseTransformPoint(foot.Pos);
            bool onIt = OnWing(st, loc.x, loc.y, loc.z, out float top);

            if (p < 1f || (!onIt && st.Mode != "fly"))
            {
                if (onIt) Carry(st, foot, plane, dt); else st.CarryOn = false;
                if (st.Mode != "off")
                {
                    rig.Root.rotation = Quaternion.AngleAxis(foot.Yaw * Mathf.Rad2Deg, Vector3.up);
                    st.Q = rig.Root.rotation;
                    st.Mode = "off"; st.Fly = 0f; st.Brace = 0f; st.Arm = null; st.CornerX = null;
                }
                SetPose(rig, null);
                return "off";
            }

            st.CarryOn = false;

            var met = FootPoses.ClimbMetrics(rig);

            // crane.js:1525-1620, now AdvanceOnWing() above — the off->ride
            // seed, the ride->blown and blown->fly transitions, and the
            // on-wing walk target, all pulled out pure the same way OnWing()
            // was. What stays here is exactly what needs a live object: the
            // foot.Pos blend itself (it needs the CURRENT position to blend
            // FROM) and capturing rig.Root's rotation the instant he enters
            // fly (crane.js:1596 `ride.q.copy(obj.quaternion)`).
            var walk = AdvanceOnWing(st, dt, p, loc.x, loc.z);
            if (walk.Applied)
            {
                Vector3 target = plane.TransformPoint(new Vector3(walk.Lx, walk.StandY, walk.Lz));
                foot.Pos = Vector3.Lerp(foot.Pos, target, Mathf.Min(1f, RideFollow * dt));
                foot.Vy = 0f;
            }
            if (walk.EnteredFly) st.Q = rig.Root.rotation;

            if (st.Mode == "fly")
            {
                st.Fly = Mathf.Min(1f, st.Fly + dt * 0.8f);
                st.U = Mathf.Clamp(st.U + st.InputAcross * GripTraverse * dt, -(_geo.Span * 0.5f - 0.5f), _geo.Span * 0.5f - 0.5f);

                float band = _geo.Fw * 1.3f;
                float over = Mathf.Clamp(st.U / band, -1f, 1f);
                float wantRoll = Mathf.PI * Mathf.Max(0f, 1f - Mathf.Abs(over));
                st.Roll += (wantRoll - st.Roll) * Mathf.Min(1f, 9f * dt);
                if (Mathf.Abs(st.U) > band) st.Roll *= Mathf.Max(0f, 1f - 6f * dt);

                Vector3 by = -windWorld;
                Vector3 bz = Vector3.up;
                Quaternion qTilt = BuildFlyBasis(by, bz);
                qTilt *= Quaternion.AngleAxis(st.Roll * Mathf.Rad2Deg, Vector3.up); // roll about local +Y, see JS comment

                if (!st.Arm.HasValue) { st.Arm = met.ArmLen > 0f ? met.ArmLen : 0.52f; st.Sh = met.ShoulderY > 0f ? met.ShoulderY : 1.05f; st.Belly = MeasureBelly(rig, met); }
                var corner = CornerAt(st, st.U, loc.z);
                if (corner.HasValue)
                {
                    float reach = st.Arm.Value * 0.92f;
                    float drop = Mathf.Min(st.Belly + EdgeDrop, reach);
                    float back = Mathf.Sqrt(Mathf.Max(0.0025f, reach * reach - drop * drop));
                    float halfW = Mathf.Max(0.18f, GripSpread);
                    float gy = corner.Value.y - EdgeDrop, gz = corner.Value.z - EdgeOver;
                    st.TargetL = plane.TransformPoint(new Vector3(st.U - halfW, gy, gz));
                    st.TargetR = plane.TransformPoint(new Vector3(st.U + halfW, gy, gz));
                    st.Gy = gy; st.Gz = gz; st.GotEdge = true;

                    Matrix4x4 toPlane = plane.worldToLocalMatrix;
                    Vector3 byP = toPlane.MultiplyVector(by);
                    Vector3 bzP = toPlane.MultiplyVector(bz);
                    Vector3 placeLocal = new Vector3(st.U, gy, gz) + bzP * drop - byP * (back + st.Sh);
                    Vector3 placeWorld = plane.TransformPoint(placeLocal);
                    foot.Pos = Vector3.Lerp(foot.Pos, placeWorld, Mathf.Min(1f, 0.06f + 0.94f * st.Fly * Mathf.Min(1f, FlyRise * dt * 8f)));
                }
                else
                {
                    st.GotEdge = false;
                    float? keep = SurfaceAt(st, st.U, st.Lz);
                    Vector3 placeWorld = plane.TransformPoint(new Vector3(st.U, (keep ?? 0f) + st.Belly, st.Lz));
                    foot.Pos = Vector3.Lerp(foot.Pos, placeWorld, Mathf.Min(1f, 0.06f + 0.94f * st.Fly * Mathf.Min(1f, FlyRise * dt * 8f)));
                }
                foot.Vy = 0f;

                st.Q = Quaternion.Slerp(st.Q, qTilt, Mathf.Min(1f, 3f * dt));
                rig.Root.rotation = st.Q;

                // crane.js:1746-1752 — GunArmBones is the override seam
                // (see its own doc comment); the default is the real
                // computation now that it exists (ResolveGunArmBones above).
                Transform[] savedBones = GunArmBones != null ? GunArmBones.Invoke() : ResolveGunArmBones(foot, rig);
                Quaternion[] savedQ = null;
                if (savedBones != null)
                {
                    savedQ = new Quaternion[savedBones.Length];
                    for (int i = 0; i < savedBones.Length; i++) savedQ[i] = savedBones[i].localRotation;
                }

                SetPose(rig, "grip", st.Fly, st.U >= 0f ? 1 : -1);
                FootPoses.HandsOnEdge(rig, st.GotEdge ? st.TargetL : (Vector3?)null, st.GotEdge ? st.TargetR : (Vector3?)null, st.Fly);

                if (savedBones != null)
                    for (int i = 0; i < savedBones.Length; i++) savedBones[i].localRotation = savedQ[i];
            }
            else
            {
                float wantBrace = st.Mode == "blown" ? Mathf.Min(1f, (p - 1f) / 2f) : 0f;
                st.Brace += (wantBrace - st.Brace) * Mathf.Min(1f, 3f * dt);
                if (st.Brace > 0.02f) SetPose(rig, "brace", st.Brace, loc.x >= 0f ? 1 : -1);
                else SetPose(rig, null);
                st.Lean += ((st.Mode == "blown" ? BlowLean * (p - 1f) * 0.4f : 0f) - st.Lean) * Mathf.Min(1f, 4f * dt);

                Vector3 upTilted = plane.rotation * Vector3.up; // frame-symmetric: a built-in transform, no cross product
                Quaternion qTilt = Quaternion.FromToRotation(Vector3.up, upTilted); // see class header: no swap needed
                qTilt = Quaternion.Slerp(Quaternion.identity, qTilt, RideLean);
                Vector3 bx = WindTunnel.AxisOf(windWorld); // reused, same formula as crane.js:1825
                rig.Root.rotation = Quaternion.AngleAxis(-st.Lean * Mathf.Rad2Deg, bx) * qTilt * rig.Root.rotation;
            }

            // FootClimb calls CharacterRig.SettleBody(dt) every frame it holds
            // the body, specifically because a lean spring left frozen through
            // a hold replays a STALE speed the instant it is released (the
            // slung-AK bug). This file holds the body the same way, so it
            // follows the same rule rather than inventing its own.
            rig.SettleBody(dt);
            return st.Mode;
        }
    }
}
