using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js's belt / holster / gear-socket system
    /// (foot.js:1033, 1093-2124 — see each method's own citation).
    ///
    /// ============================== WHAT THIS PORTS ==============================
    /// Seating a piece of WORN gear (today: one belt, src/weapons.js:76-120
    /// `GEAR_DEFS.belt`) on whatever body is wearing it: finding the hips bone,
    /// measuring the body's own waist, scaling/conforming the strap to the
    /// body's real cross-section, standing holster pockets off the hip so they
    /// clear the thigh, then SKINNING the belt onto the body's own skeleton so
    /// it bends with a roll/crouch/slide instead of staying a rigid hoop, and
    /// riding the holster sockets every frame off that same skin.
    ///
    /// ============================= WHAT IS NOT HERE ==============================
    /// `wearGear`'s OWN GLB download (foot.js:1060-1068 `loadWeapon(def)`) and
    /// the whole draw/holster/sling system (`_toHolster`/`_fromHolster`/
    /// `_toBack`/`_fromBack`/`_seatInPocket`, foot.js:2125+) are a DIFFERENT,
    /// separately-owned system's (Character/FootWeapons.cs; the equip entry
    /// point itself, <see cref="WearGear"/>, is now ported here — see its own
    /// header for exactly which of its five steps are Unity-superseded).
    /// This file is the sole caller of none of the draw/holster methods. Its
    /// only touch points with that system are <see cref="IHolsterHost"/>, a
    /// narrow hook <see cref="RemoveGear"/> needs so a holstered weapon comes
    /// off with the belt it was sitting on (foot.js:1096-1099) and
    /// <see cref="WearGear"/> needs for its own holster fallback
    /// (foot.js:1088-1089) — there is no C# weapon/holster system yet to
    /// implement it, so it is an injected interface a future port can
    /// satisfy; passing `null` just skips both steps.
    /// `resume()` (foot.js:1033-1049) is a body-RESPAWN method that is mostly
    /// CharacterRoster/FootController's concern (loading a body, retrying on
    /// failure, the Georgian hint string) — it happens to fall inside this
    /// file's assigned line range but is not a belt/gear concept. Only the part
    /// this file can honestly own — snapping a resumed hero onto the ground —
    /// is ported, as <see cref="ResumeGroundSnap"/>. See that method's own
    /// header for the reduction. Likewise `_loadBody` (foot.js:615-643, the
    /// body-reload path `resume()` retries into) is mostly the same async-load
    /// orchestration — except its worn-gear re-seat loop (foot.js:624), which
    /// IS this file's own and is now ported as <see cref="ReseatAll"/>.
    ///
    /// ================================ HANDEDNESS =================================
    /// No cross products anywhere in this file. Every world direction needed
    /// (the belt's own forward/back, a ray direction, a socket's riding
    /// transform) is built from CharacterRig.Root's own `forward`/`up`
    /// (Unity-native, already handedness-correct — see CharacterRig.cs's class
    /// header) or from a pure yaw-only `Quaternion.Euler(0, yawDeg, 0)` (a
    /// single-axis rotation, which needs no handedness fix either way — see
    /// FlightModel.cs's header on why the "component-identical" case needs no
    /// swap). Matrix decomposition (<see cref="ExtractRotation"/>/
    /// <see cref="ExtractScale"/>) reconstructs a quaternion FROM a matrix's own
    /// already-correct basis columns via Quaternion.LookRotation — not from an
    /// externally cross-derived "right", which is the specific LookRotation
    /// trap the port skill warns about (see AirframeBreakup.cs's identical,
    /// already-shipped use of this exact pattern).
    /// The one brute-force geometry routine here, <see cref="RayTriangle"/>
    /// (Möller–Trumbore), uses `Vector3.Cross` internally but needs NO operand
    /// swap: it is a self-contained linear solve for a ray/triangle intersection
    /// (Cramer's rule via cross products), correct in any consistent coordinate
    /// system regardless of handedness, unlike a cross product standing in for
    /// a physical "which way does this rotation push a point" fact.
    ///
    /// ================================ LATEUPDATE =================================
    /// <see cref="RideSockets"/> re-poses a holster socket off the body's CURRENT
    /// bone transforms every frame, so it belongs in the owning
    /// MonoBehaviour's LateUpdate, after the Animator and after whatever else
    /// (CharacterRig/FootController/FootPoses) wrote bones this frame — never
    /// before, or a socket rides last frame's pose.
    ///
    /// ============================ UNITY-SPECIFIC ADAPTATION ============================
    /// `_skinBelt`/`_rideOne`/`_skinXform` (foot.js:1566-1836) solve, per vertex,
    /// "what BIND-SPACE position reproduces this CURRENT world position once
    /// skinned" — and the JS version carries three.js's own correction for a
    /// bug class that is THREE-SPECIFIC: a SkinnedMesh's `bindMatrixInverse` is
    /// a live property three.js recomputes from the mesh's CURRENT matrixWorld
    /// every render, so reading it mid-frame (as this code must, to re-skin a
    /// belt after the hero has already walked somewhere) can see a STALE value
    /// and silently reproduce the hero's last frame-to-frame displacement
    /// instead of identity (this project's own "mid-frame bindMatrixInverse"
    /// bug, see MEMORY.md "Belt & holster 2026-10"). Unity's SkinnedMeshRenderer
    /// has no such property: `Mesh.bindposes` is fixed import-time data and the
    /// renderer's OWN Transform plays no part in the skin computation at all
    /// (moving/scaling a SkinnedMeshRenderer's own Transform does not move its
    /// posed vertices — only its bones do). The forward formula is simply
    /// `worldPos = Σ w_j (bone_j.localToWorldMatrix * bindpose_j) * vertexLocal`
    /// (see <see cref="SkinXform"/>), with no mesh-transform term to cancel or
    /// go stale — so the JS's `bodyInv`/`bindMatrix`/`bindMatrixInverse` dance
    /// (foot.js:1598-1600, 1691-1692, 1830-1834) has no Unity counterpart to
    /// port: inverting the blended bone matrix directly IS the complete,
    /// always-correct solve here. This is a deliberate adaptation to a
    /// genuinely different engine contract, not a shortcut — flagged here and
    /// in the port report because it is the single highest-risk piece of this
    /// file to have gotten subtly wrong, and it cannot be checked without
    /// running the editor (see this file's report for what is unverified).
    /// </summary>
    public sealed class FootBelt
    {
        // =====================================================================
        // DEPENDENCIES — constructor-injected, per this port unit's brief.
        // =====================================================================
        readonly CharacterRig _rig;
        readonly IHolsterHost _holsterHost;

        public FootBelt(CharacterRig rig, IHolsterHost holsterHost = null)
        {
            _rig = rig ?? throw new ArgumentNullException(nameof(rig));
            _holsterHost = holsterHost;
        }

        /// <summary>src/foot.js:1096-1099 — what `removeGear` needs from the
        /// (not yet ported) holster/draw system: everything currently seated
        /// somewhere inside a piece of worn gear, and a way to take it off that
        /// gear before the gear itself is removed. `null` is a legal dependency
        /// (RemoveGear just skips this step) for as long as no weapon/holster
        /// system exists in C# to implement it.</summary>
        public interface IHolsterHost
        {
            IEnumerable<Transform> HolsteredIn(WornGear gear);
            void TakeOffGear(Transform heldObject);

            /// <summary>src/foot.js:1089 `if (!this.held)` — whether anything is
            /// currently drawn. Added for <see cref="WearGear"/>; passing a null
            /// IHolsterHost to the constructor skips this (and the holster
            /// fallback below) exactly as it already skips RemoveGear's step.</summary>
            bool HasHeldWeapon();

            /// <summary>src/foot.js:1089 `this._toHolster()` — put the hero's
            /// held weapon (if any) away now that it has somewhere to live.</summary>
            void ToHolster();
        }

        // src/foot.js:593 `this.worn = new Map()`
        readonly Dictionary<string, WornGear> _worn = new Dictionary<string, WornGear>();
        public IReadOnlyDictionary<string, WornGear> Worn => _worn;

        /// <summary>src/foot.js:1790 `this._backSock` — the rifle sling, which is
        /// not worn gear but rides the same skin. Set by the (not yet ported)
        /// sling system; <see cref="RideSockets"/> rides it too when present.</summary>
        public WornGear BackSling;

        // =====================================================================
        // MEASURED CONSTANTS — named (rather than left inline) so FootBeltProbe
        // (unity/Assets/Editor/Probes/FootBeltProbe.cs) can read them by
        // reflection and compare against tools/_probe_footbelt.mjs, which pulls
        // the same numbers out of the real src/foot.js and src/weapons.js text.
        // `internal`: visible to Assembly-CSharp-Editor via AssemblyInfo.cs's
        // InternalsVisibleTo, not meant as public API.
        // =====================================================================
        internal const float ClearHangersMargin = 0.008f;        // foot.js:1233 MARGIN
        internal const float ClearHangersShortfallLimit = 0.06f; // foot.js:1278
        internal const float ClearHangersPushClamp = 0.015f;     // foot.js:1288

        internal const int FitBeltConformN = 32;                 // foot.js:1353 N
        internal const float FitBeltHalfBand = 0.025f;           // foot.js:1355 halfBand
        internal const float FitBeltHitsFloorFrac = 0.6f;        // foot.js:1413 `N * 0.6`
        internal const int FitBeltFillHoleFrac = 4;              // foot.js:1369 `N / 4`

        internal const int RingRadiusN = 16;                     // foot.js:1499 N
        internal const float RingOfBandHalf = 0.025f;            // foot.js:1536, 1508 (independent 0.025 literals, same value)

        internal const float TorsoHullSlab = 0.45f;              // foot.js:1913 SLAB
        internal const int TorsoHullMinTris = 180;               // foot.js:1934 `9 * 20`

        internal const float WaistY0Frac = 0.46f;                // foot.js:2059
        internal const float WaistY1Frac = 0.56f;                // foot.js:2060
        internal const float WaistYFrac = 0.51f;                 // foot.js:2102, 2107
        internal const float WaistGenericFallbackFrac = 0.19f;   // foot.js:2106
        internal const float WaistClampLo = 0.26f;               // foot.js:2112
        internal const float WaistClampHi = 0.55f;               // foot.js:2112
        internal const float WaistDefault = 0.33f;               // foot.js:2112 `width || 0.33`
        internal const float WaistSpreadLoPctl = 0.03f;          // foot.js:2096
        internal const float WaistSpreadHiPctl = 0.97f;          // foot.js:2097
        internal const float WaistSpreadFudge = 1.03f;           // foot.js:2101

        internal const float GearWidthDefault = 0.33f;           // foot.js:1125 `w.width ?? 0.33` (same literal, different site than WaistDefault)

        internal const float ScaleBeltGrowClampLo = 0.5f;        // foot.js:1492
        internal const float ScaleBeltGrowClampHi = 1.8f;        // foot.js:1492

        // =====================================================================
        // RESUME — foot.js:1033-1049. See class header: reduced to the ground
        // snap, the only part that is honestly this file's to own.
        // =====================================================================
        /// <summary>src/foot.js:1037-1038 — `gY = g._groundAt(x,z,y+1.2,1.2); this._stand(x,(gY??y)+0.02,z,yaw)`.
        /// `groundAt` is the host's ground probe (FootController owns the real
        /// one, `GroundAt`, privately — not exposed to this file today; a
        /// caller must supply an equivalent). Returns the position to stand the
        /// hero at; the caller still owns actually placing/activating the rig
        /// (the body-load retry and the Georgian failure hint, foot.js:1041-1047,
        /// are not ported here at all).</summary>
        public static Vector3 ResumeGroundSnap(Vector3 pos, Func<float, float, float, float, float?> groundAt)
        {
            float? gY = groundAt?.Invoke(pos.x, pos.z, pos.y + 1.2f, 1.2f);
            return new Vector3(pos.x, (gY ?? pos.y) + 0.02f, pos.z);
        }

        // =====================================================================
        // REMOVE GEAR — src/foot.js:1093-1104.
        // =====================================================================
        public bool RemoveGear(string id)
        {
            if (!_worn.TryGetValue(id, out var w)) return false;
            // foot.js:1096-1099 — whatever is sitting in it comes off with it.
            if (_holsterHost != null && w.Obj != null)
            {
                foreach (var held in new List<Transform>(_holsterHost.HolsteredIn(w)))
                {
                    if (held != null && IsDescendantOf(held, w.Obj)) _holsterHost.TakeOffGear(held);
                }
            }
            UnskinBelt(w);                                   // foot.js:1100
            if (w.Obj != null) w.Obj.SetParent(null, true);  // foot.js:1101 `w.obj.parent?.remove(w.obj)`
            _worn.Remove(id);                                 // foot.js:1102
            return true;
        }

        static bool IsDescendantOf(Transform t, Transform root)
        {
            for (var p = t; p != null; p = p.parent) if (p == root) return true;
            return false;
        }

        // =====================================================================
        // SEAT GEAR — src/foot.js:1108-1188. Entry point for an already-
        // instantiated piece of gear (the equivalent of `wearGear`'s `obj`
        // after it finished loading — see class header on why loading itself
        // is out of scope here). `w` must already have Obj/Sockets populated;
        // this registers it, sizes and places it.
        // =====================================================================
        static readonly Regex HipsRe = new Regex("pelvis|hips?$|hip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SpineFallbackRe = new Regex("spine|torso|abdomen", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex NonAlpha = new Regex("[^a-z]", RegexOptions.Compiled);
        static string Norm(string n) => NonAlpha.Replace((n ?? string.Empty).ToLowerInvariant(), string.Empty);

        /// <summary>src/foot.js:1116-1119 — the hips bone, by name, same as the
        /// JS: a normalised (letters-only, lowercased) name test, falling back
        /// to the PARENT of the first spine/torso/abdomen bone found. Unlike
        /// CharacterRig's own introspection (humanoid Avatar bones), this walks
        /// every Transform under the model because the JS does too, and a
        /// belt is reasonable kit for any body, not only the 3 humanoid ones.</summary>
        public Transform FindHipsBone()
        {
            Transform hips = null, spineParent = null;
            foreach (var t in _rig.Model.GetComponentsInChildren<Transform>(true))
            {
                string n = Norm(t.name);
                if (hips == null && HipsRe.IsMatch(n)) hips = t;
                if (spineParent == null && SpineFallbackRe.IsMatch(n)) spineParent = t.parent;
            }
            return hips ?? spineParent;
        }

        /// <summary>Registers `w` under its own id and seats it. The JS's
        /// `wearGear` also loads the GLB and sets up `w.sockets`/`w.socket`
        /// before calling this (foot.js:1057-1086) — the caller here is
        /// expected to have already done the Unity equivalent (instantiate the
        /// gear prefab, resolve its socket Transforms by name).</summary>
        public void SeatGear(WornGear w)
        {
            _worn[w.Id] = w;
            DoSeatGear(w);
        }

        /// <summary>src/foot.js:624 `for (const w of this.worn.values())
        /// this._seatGear(w);` — the one line of `_loadBody` (foot.js:615-643)
        /// that is honestly this file's own: worn gear hangs off the OLD
        /// skeleton, so a body reload (a retry, a different personage) leaves
        /// it parented to bones no longer in the scene and it silently
        /// disappears unless every already-worn piece is re-seated onto the
        /// NEW rig. In the JS, `this.worn` lives on `OnFoot` and survives the
        /// reload untouched while only `this.rig` is replaced; in this port
        /// <see cref="_rig"/> is `readonly`, so a reload constructs a NEW
        /// FootBelt instead — `carriedOver` is the OLD instance's
        /// <see cref="Worn"/>.Values, handed in by whoever owns that lifetime
        /// (CharacterRoster/FootController; same scope cut as `resume()`
        /// above). Each piece is re-registered AND re-seated onto THIS
        /// instance's (new) rig via <see cref="SeatGear"/> — not a bare
        /// `DoSeatGear` loop, so <see cref="Worn"/> is correct on the new
        /// instance afterward too. The REST of `_loadBody` — the async
        /// body/GLB load itself, resetting the holster-draw state, and
        /// equipping the default belt the very FIRST time a hero has none —
        /// is deliberately NOT here; see this class's header ("WHAT IS NOT
        /// HERE") for why that is CharacterRoster/FootController's call to
        /// wire up, not a belt/gear concept.</summary>
        public void ReseatAll(IEnumerable<WornGear> carriedOver)
        {
            foreach (var w in carriedOver) SeatGear(w);
        }

        /// <summary>src/foot.js:1108-1188 `_seatGear`.</summary>
        void DoSeatGear(WornGear w)
        {
            var hips = FindHipsBone();
            // foot.js:1115 — a refit: the pockets are measured again (out of
            // scope data; see WornGear.StandExtra) and whatever is holstered
            // is re-seated (also out of scope: that's the holster system's).
            foreach (var sk in w.Sockets) w.StandExtra.Remove(sk);

            UnskinBelt(w);                                    // foot.js:1123
            float width = WaistWidth(hips);                    // foot.js:1124
            float scale = (width * w.Fit) / (w.Width <= 0f ? GearWidthDefault : w.Width); // foot.js:1125

            if (hips != null)
            {
                // foot.js:1130-1133 — the hips bone carries the body's own
                // 1.8 m normalisation scale (CharacterRig.NormalizeHeight);
                // Transform.lossyScale.x already IS that decomposed world
                // scale, no manual matrix decompose needed.
                float bs = hips.lossyScale.x;
                w.Obj.localScale = Vector3.one * (scale / (Mathf.Abs(bs) > 1e-6f ? bs : 1f));
                w.Obj.SetParent(hips, false);                   // foot.js:1134 `hips.add(w.obj)`

                // foot.js:1140-1147 — sit it at the WAIST height, not the hip
                // joint, using the height stored above the rig's own origin so
                // a re-seat after the hero walked somewhere still lands right.
                Vector3 at = hips.position;
                if (_waistY.HasValue) at.y = _rig.Root.position.y + _waistY.Value;
                at.y -= w.Drop;                                 // foot.js:1151
                w.Obj.localPosition = Vector3.zero;              // foot.js:1152
                w.Obj.localPosition = hips.InverseTransformPoint(at); // foot.js:1153

                // foot.js:1159-1175 — YAW ONLY: the wanted WORLD rotation is
                // built from the rig's own facing (not the hips bone's current
                // lean), then the bone's own rotation is divided back out.
                Quaternion bq = hips.rotation;
                Vector3 fwd = _rig.Root.forward; fwd.y = 0f;
                Quaternion rq = hips.rotation; // fallback: keep whatever it had if facing is degenerate
                if (fwd.sqrMagnitude > 1e-8f)
                {
                    fwd.Normalize();
                    float yawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    rq = Quaternion.Euler(0f, yawDeg, 0f);
                }
                w.Obj.localRotation = Quaternion.Inverse(bq) * rq; // foot.js:1175

                CentreBelt(w);                                  // foot.js:1176
                SkinBelt(w);                                     // foot.js:1177
            }
            else
            {
                // foot.js:1179-1186 — no pelvis to hang it on: ride the action
                // group at a measured waist height instead.
                w.Obj.localScale = Vector3.one * scale;
                w.Obj.SetParent(_rig.ActGroup, false);
                w.Obj.localRotation = Quaternion.identity;
                float h = _rig.BodyHeight > 0.01f ? _rig.BodyHeight : CharacterRoster.HeroHeight;
                w.Obj.localPosition = new Vector3(0f, h * 0.53f, 0f);
            }
            w.Scale = scale;                                    // foot.js:1187
        }

        // =====================================================================
        // WEAR GEAR — src/foot.js:1057-1091 `wearGear`, the top-level "equip"
        // entry point this file's own header previously listed as entirely
        // out of scope. Two of its five steps are genuinely Unity-superseded
        // (see below); the other three — resolving sockets, seating, and the
        // holster fallback — are real behaviour with no C# anywhere today, so
        // they are ported here now that SeatGear/RemoveGear give them
        // something to call into.
        // =====================================================================
        /// <summary>src/foot.js:1057-1091. `obj` must already be the
        /// INSTANTIATED gear (Unity's own asset pipeline — AssetDatabase/
        /// Resources/glTFast — replaces `loadWeapon(def)`'s download step,
        /// foot.js:1060-1068, same division of labour as every other "already
        /// loaded" entry point in this port wave, e.g. FootWeapons.DrawWeapon).
        /// Mesh culling flags and `biasWorn`'s depth bias (foot.js:1070-1082)
        /// are SUPERSEDED, not ported: the frustum-culled-against-a-stale-
        /// bind-pose bug is three.js-specific (nothing to set on a Unity
        /// Renderer), and Combat/Weapons.cs already decided Unity's ordinary
        /// depth test replaces `biasWorn` outright. What is ported: resolving
        /// the socket Transforms by name (foot.js:1083-1084), building the
        /// WornGear record and seating it (1085-1086), remembering it by id
        /// (1087, now inside SeatGear itself), and the holster fallback —
        /// "a pistol the hero already owns but is not holding now has
        /// somewhere to be" (1088-1089). Returns false exactly where the JS
        /// does: no id, no rig, or already worn (1058-1059) — not where the
        /// JS returns false for a failed download (1064-1067), since that
        /// step does not happen here.</summary>
        public bool WearGear(string id, Transform obj, IReadOnlyList<string> socketNames, float fit = 1f, float width = 0f, float back = 0f)
        {
            if (string.IsNullOrEmpty(id) || obj == null) return false;
            if (_worn.ContainsKey(id)) return true; // foot.js:1059
            var sockets = new List<Transform>();
            if (socketNames != null)
                foreach (var n in socketNames)
                {
                    var t = FindChildByName(obj, n);
                    if (t != null) sockets.Add(t);
                }
            var w = new WornGear
            {
                Id = id, Obj = obj, Sockets = sockets, Socket = sockets.Count > 0 ? sockets[0] : null,
                Fit = fit, Back = back,
            };
            if (width > 0f) w.Width = width;
            SeatGear(w);                 // foot.js:1086-1087 (registers AND seats — see SeatGear's own header)
            if (_holsterHost != null && !_holsterHost.HasHeldWeapon()) _holsterHost.ToHolster(); // foot.js:1088-1089
            return true;
        }

        static Transform FindChildByName(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // =====================================================================
        // CENTRE BELT — src/foot.js:1207-1210.
        // =====================================================================
        public void CentreBelt(WornGear w)
        {
            FitBelt(w);
            ClearHangers(w);
        }

        // =====================================================================
        // RING GEOMETRY — src/foot.js:1489-1544.
        // =====================================================================
        public struct Ring { public Vector3 C; public float Y; }
        public struct Section { public Vector3 C; public float R; }

        /// <summary>src/foot.js:1521-1544 `_ringOf` — every vertex of the gear's
        /// own (still rigid) meshes, the median-height band, and that band's
        /// XZ bounding-box centre.</summary>
        public Ring? RingOf(WornGear w)
        {
            var pts = new List<Vector3>();
            foreach (var mf in GearMeshFilters(w))
            {
                if (mf.sharedMesh == null) continue;
                var verts = mf.mesh.vertices; // .mesh: Unity's own clone-on-first-touch, see class header
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++) pts.Add(m.MultiplyPoint3x4(verts[i]));
            }
            if (pts.Count < 8) return null;
            var ys = new List<float>(pts.Count);
            foreach (var p in pts) ys.Add(p.y);
            ys.Sort();
            float y = ys[ys.Count / 2];
            float lox = float.PositiveInfinity, hix = float.NegativeInfinity;
            float loz = float.PositiveInfinity, hiz = float.NegativeInfinity;
            int band = 0;
            foreach (var p in pts)
            {
                if (Mathf.Abs(p.y - y) >= RingOfBandHalf) continue;
                band++;
                lox = Mathf.Min(lox, p.x); hix = Mathf.Max(hix, p.x);
                loz = Mathf.Min(loz, p.z); hiz = Mathf.Max(hiz, p.z);
            }
            if (band < 8) return null;
            return new Ring { C = new Vector3((lox + hix) * 0.5f, y, (loz + hiz) * 0.5f), Y = y };
        }

        /// <summary>src/foot.js:1498-1517 `_ringRadius` — the strap's median
        /// radius about its own world origin, at world height `y`.</summary>
        public float RingRadius(WornGear w, float y)
        {
            const int N = RingRadiusN;
            var best = new float[N];
            Vector3 c = w.Obj.position;
            foreach (var mf in GearMeshFilters(w))
            {
                if (mf.sharedMesh == null) continue;
                var verts = mf.mesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 v = m.MultiplyPoint3x4(verts[i]);
                    if (Mathf.Abs(v.y - y) > RingOfBandHalf) continue;
                    float a = Mathf.Atan2(v.z - c.z, v.x - c.x);
                    if (a < 0f) a += Mathf.PI * 2f;
                    int k = Mathf.Min(N - 1, Mathf.FloorToInt((a / (Mathf.PI * 2f)) * N));
                    float r = Mathf.Sqrt((v.x - c.x) * (v.x - c.x) + (v.z - c.z) * (v.z - c.z));
                    if (r > best[k]) best[k] = r;
                }
            }
            var used = new List<float>();
            foreach (var x in best) if (x > 0f) used.Add(x);
            if (used.Count < N / 2) return 0f;
            used.Sort();
            return used[used.Count / 2];
        }

        /// <summary>src/foot.js:1489-1495 `_scaleBelt` — the fallback: one
        /// uniform scale so the strap's typical radius clears the body.</summary>
        public void ScaleBelt(WornGear w, Section body, Ring band)
        {
            float r = RingRadius(w, band.Y);
            if (r <= 0f) return;
            float grow = Mathf.Clamp((body.R * w.Fit) / r, ScaleBeltGrowClampLo, ScaleBeltGrowClampHi);
            w.Obj.localScale *= grow;
            w.Scale *= grow;
        }

        // =====================================================================
        // FIT BELT — src/foot.js:1313-1484.
        // =====================================================================
        public void FitBelt(WornGear w)
        {
            if (w.Obj.parent == null) return;                  // foot.js:1314-1315
            var bandN = RingOf(w);
            if (bandN == null) return;                          // foot.js:1318
            var band = bandN.Value;
            var bodyN = BodySection(band.Y);
            if (bodyN == null) return;                           // foot.js:1320
            var body = bodyN.Value;

            // foot.js:1330-1340 — centre the ring on the body's own axis, then
            // push it back off that axis by `w.Back` metres.
            Vector3 shift = new Vector3(body.C.x - band.C.x, 0f, body.C.z - band.C.z);
            if (Mathf.Abs(w.Back) > 1e-6f)
            {
                Vector3 back = -_rig.Root.forward; back.y = 0f; // foot.js:1335 `(0,0,1).applyQuaternion(rq)` == -forward
                if (back.sqrMagnitude > 1e-8f) shift += back.normalized * w.Back;
            }
            w.Obj.position += shift;                            // foot.js:1339-1341 (Transform.position already handles the parent-local conversion)

            if (!w.Conform) { ScaleBelt(w, body, band); return; } // foot.js:1351

            const int N = FitBeltConformN;
            float bandY = band.Y;
            const float halfBand = FitBeltHalfBand;

            int BinOf(float x, float z)
            {
                float a = Mathf.Atan2(z, x);
                if (a < 0f) a += Mathf.PI * 2f;
                return Mathf.Min(N - 1, Mathf.FloorToInt((a / (Mathf.PI * 2f)) * N));
            }
            bool Fill(float[] prof)
            {
                int holes = 0;
                foreach (var x in prof) if (x <= 0f) holes++;
                if (holes > N / FitBeltFillHoleFrac) return false;
                for (int pass = 0; pass < 4; pass++)
                {
                    for (int k = 0; k < N; k++)
                    {
                        if (prof[k] > 0f) continue;
                        float a0 = prof[(k - 1 + N) % N], a1 = prof[(k + 1) % N];
                        int n = 0; float sum = 0f;
                        if (a0 > 0f) { sum += a0; n++; }
                        if (a1 > 0f) { sum += a1; n++; }
                        if (n > 0) prof[k] = sum / n;
                    }
                }
                foreach (var x in prof) if (x <= 0f) return false;
                return true;
            }

            var torso = TorsoHull(bandY, "torso");
            var bodyProf = new float[N];
            if (torso == null) { ScaleBelt(w, body, band); return; } // foot.js:1388

            Vector3 origin = w.Obj.position; origin.y = bandY;    // foot.js:1389-1391
            float invS = 1f / (Mathf.Abs(w.Obj.lossyScale.x) > 1e-6f ? w.Obj.lossyScale.x : 1f);
            int hits = 0;
            for (int k = 0; k < N; k++)
            {
                float a = ((k + 0.5f) / N) * Mathf.PI * 2f;
                Vector3 dir = w.Obj.rotation * new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-8f) continue;
                dir.Normalize();
                Vector3 from = origin + dir * 3f;
                if (torso.Raycast(from, -dir, 3f, out Vector3 hit))
                {
                    bodyProf[k] = new Vector3(hit.x - origin.x, 0f, hit.z - origin.z).magnitude * invS;
                    hits++;
                }
            }
            if (hits < N * FitBeltHitsFloorFrac) { ScaleBelt(w, body, band); return; } // foot.js:1413-1416
            if (!Fill(bodyProf)) { ScaleBelt(w, body, band); return; } // foot.js:1417-1420

            var strapProf = new float[N];
            foreach (var mf in GearMeshFilters(w))
            {
                if (mf.sharedMesh == null) continue;
                var verts = mf.mesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 lv = verts[i];
                    Vector3 wv = m.MultiplyPoint3x4(lv);
                    if (Mathf.Abs(wv.y - bandY) > halfBand) continue;
                    float r = Mathf.Sqrt(lv.x * lv.x + lv.z * lv.z);
                    int k = BinOf(lv.x, lv.z);
                    if (r > strapProf[k]) strapProf[k] = r;
                }
            }
            if (!Fill(strapProf)) { ScaleBelt(w, body, band); return; } // foot.js:1442-1445

            var push = new float[N];
            for (int k = 0; k < N; k++) push[k] = bodyProf[k] * w.Fit - strapProf[k];
            float At(float x, float z) => push[BinOf(x, z)];

            foreach (var mf in GearMeshFilters(w))
            {
                if (mf.sharedMesh == null) continue;
                var verts = mf.mesh.vertices;
                bool touched = false;
                for (int i = 0; i < verts.Length; i++)
                {
                    float x = verts[i].x, z = verts[i].z;
                    float r = Mathf.Sqrt(x * x + z * z);
                    if (r < 1e-5f) continue;
                    float d = At(x, z) / r;
                    verts[i].x = x + x * d;
                    verts[i].z = z + z * d;
                    touched = true;
                }
                if (touched)
                {
                    mf.mesh.vertices = verts;
                    mf.mesh.RecalculateNormals();
                    mf.mesh.RecalculateBounds();
                }
            }
            foreach (var sk in w.Sockets)
            {
                float r = Mathf.Sqrt(sk.localPosition.x * sk.localPosition.x + sk.localPosition.z * sk.localPosition.z);
                if (r < 1e-5f) continue;
                float d = At(sk.localPosition.x, sk.localPosition.z) / r;
                var p = sk.localPosition;
                p.x += p.x * d; p.z += p.z * d;
                sk.localPosition = p;
            }
        }

        // =====================================================================
        // CLEAR HANGERS — src/foot.js:1219-1311.
        // =====================================================================
        public void ClearHangers(WornGear w)
        {
            var root = w.Obj;
            if (root == null || w.Sockets.Count == 0) return;
            var bandN = RingOf(w);
            if (bandN == null) return;
            var band = bandN.Value;

            Matrix4x4 inv = root.worldToLocalMatrix;
            float wscale = Mathf.Abs(root.lossyScale.x) > 1e-6f ? root.lossyScale.x : 1f;
            float bandYL = inv.MultiplyPoint3x4(band.C).y;
            Quaternion rq = root.rotation;
            Vector3 origin = root.position;
            const float MARGIN = ClearHangersMargin;

            // gather gear-local vertices once per mesh (read+write target)
            var meshVerts = new List<(MeshFilter mf, Vector3[] verts)>();
            foreach (var mf in GearMeshFilters(w))
            {
                if (mf.sharedMesh == null) continue;
                meshVerts.Add((mf, mf.mesh.vertices));
            }

            foreach (var sk in w.Sockets)
            {
                float az = Mathf.Atan2(sk.localPosition.z, sk.localPosition.x);
                Vector3 rad = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
                Vector3 dirW = rq * rad; dirW.y = 0f;
                if (dirW.sqrMagnitude < 1e-8f) continue;
                dirW.Normalize();

                var cluster = new List<(MeshFilter mf, int i, float y, float r)>();
                float yLo = float.PositiveInfinity, yHi = float.NegativeInfinity;
                foreach (var (mf, verts) in meshVerts)
                {
                    Matrix4x4 toGearLocal = inv * mf.transform.localToWorldMatrix;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 v = toGearLocal.MultiplyPoint3x4(verts[i]);
                        if (v.y > bandYL - 0.03f) continue;
                        float ang = Mathf.Atan2(v.z, v.x);
                        float d = Mathf.Atan2(Mathf.Sin(ang - az), Mathf.Cos(ang - az));
                        if (Mathf.Abs(d) > 0.5f) continue;
                        cluster.Add((mf, i, v.y, v.x * rad.x + v.z * rad.z));
                        if (v.y < yLo) yLo = v.y;
                        if (v.y > yHi) yHi = v.y;
                    }
                }
                if (cluster.Count < 40) continue;

                float yMidL = (yLo + yHi) * 0.5f;
                float yMidW = root.TransformPoint(new Vector3(0f, yMidL, 0f)).y;
                var hull = TorsoHull(yMidW, "lower");
                if (hull == null) continue;

                float shortfall = 0f;
                for (int k = 0; k <= 5; k++)
                {
                    float yL = yHi - (yHi - yLo) * (k / 5f);
                    float inner = float.PositiveInfinity;
                    foreach (var c in cluster) if (Mathf.Abs(c.y - yL) < 0.012f && c.r < inner) inner = c.r;
                    if (float.IsPositiveInfinity(inner)) continue;
                    float yW = root.TransformPoint(new Vector3(0f, yL, 0f)).y;
                    Vector3 from = new Vector3(origin.x, yW, origin.z) + dirW * 3f;
                    if (!hull.Raycast(from, -dirW, 3f, out Vector3 hit)) continue;
                    float bodyR = Mathf.Sqrt((hit.x - origin.x) * (hit.x - origin.x) + (hit.z - origin.z) * (hit.z - origin.z)) / wscale;
                    shortfall = Mathf.Max(shortfall, bodyR + MARGIN - inner);
                }
                if (shortfall <= 0f || shortfall > ClearHangersShortfallLimit) continue; // foot.js:1278-1281

                float push = Mathf.Min(shortfall, ClearHangersPushClamp);
                foreach (var c in cluster)
                {
                    var entry = meshVerts.Find(e => e.mf == c.mf);
                    entry.verts[c.i].x += rad.x * push;
                    entry.verts[c.i].z += rad.z * push;
                }
                var p2 = sk.localPosition;
                p2 += rad * push;
                sk.localPosition = p2;
                w.StandExtra[sk] = shortfall - push; // foot.js:1307 `sk.userData.standExtra`
            }

            foreach (var (mf, verts) in meshVerts)
            {
                mf.mesh.vertices = verts;
                mf.mesh.RecalculateNormals();
                mf.mesh.RecalculateBounds();
            }
        }

        // =====================================================================
        // SKIN BELT — src/foot.js:1566-1836.
        // =====================================================================
        static readonly Regex SkinCandidateBoneRe = new Regex(
            "pelvis|hips?$|spine|torso|abdomen|waist|belly|body", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public struct SkinSample { public Vector3 P; public int[] Si; public float[] Sw; }

        /// <summary>src/foot.js:1566-1736 `_skinBelt`.</summary>
        public bool SkinBelt(WornGear w)
        {
            if (_rig == null) return false;
            UnskinBelt(w); // foot.js:1569

            SkinnedMeshRenderer body = null;
            float bestArea = 0f;
            foreach (var r in BodyMeshes())
            {
                if (!(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0) continue;
                var sz = smr.sharedMesh.bounds.size;
                float area = sz.x * sz.y + sz.y * sz.z + sz.z * sz.x;
                if (area > bestArea) { bestArea = area; body = smr; }
            }
            if (body == null) return false; // foot.js:1581

            Transform[] bones = body.bones;
            Matrix4x4[] bindposes = body.sharedMesh.bindposes;

            var cand = new List<(int i, Vector3 p)>();
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null || !SkinCandidateBoneRe.IsMatch(b.name ?? string.Empty)) continue;
                cand.Add((i, b.position));
            }
            if (cand.Count == 0) return false; // foot.js:1593

            var bandN = RingOf(w);
            float? bandY = bandN?.Y;
            var samples = SkinSamples(bones, bindposes, body, bandY, "torso");
            if (samples.Count < 12) samples = SkinSamples(bones, bindposes, body, bandY, "lower");
            bool useSkin = samples.Count >= 12;
            float bandYv = bandY ?? 0f;

            SkinSample? NearSample(Vector3 p)
            {
                float y = Mathf.Clamp(p.y, bandYv - 0.03f, bandYv + 0.03f);
                SkinSample? best = null; float bd = float.PositiveInfinity;
                foreach (var sm in samples)
                {
                    float dx = sm.P.x - p.x, dy = sm.P.y - y, dz = sm.P.z - p.z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bd) { bd = d; best = sm; }
                }
                return best;
            }

            void Weigh(Vector3 p, int[] idx, float[] wt)
            {
                if (useSkin)
                {
                    var sm = NearSample(p);
                    if (sm.HasValue)
                    {
                        for (int j = 0; j < 4; j++) { idx[j] = sm.Value.Si[j]; wt[j] = sm.Value.Sw[j]; }
                        return;
                    }
                }
                int a0 = -1, a1 = -1; float d0 = float.PositiveInfinity, d1 = float.PositiveInfinity;
                foreach (var c in cand)
                {
                    float d = (c.p - p).sqrMagnitude;
                    if (d < d0) { d1 = d0; a1 = a0; d0 = d; a0 = c.i; }
                    else if (d < d1) { d1 = d; a1 = c.i; }
                }
                float w0 = 1f / Mathf.Max(1e-4f, Mathf.Sqrt(d0));
                float w1 = a1 >= 0 ? 1f / Mathf.Max(1e-4f, Mathf.Sqrt(d1)) : 0f;
                float sum = w0 + w1;
                idx[0] = a0; idx[1] = a1 >= 0 ? a1 : a0; idx[2] = a0; idx[3] = a0;
                wt[0] = w0 / sum; wt[1] = w1 / sum; wt[2] = 0f; wt[3] = 0f;
            }

            // foot.js:1638-1640 — lift the sockets off the mesh first, keeping
            // their world transform, so hiding the rigid strap later doesn't
            // take them (and whatever is holstered in them) with it.
            foreach (var sk in w.Sockets) if (sk.parent != w.Obj) sk.SetParent(w.Obj, true);

            var made = new List<SkinnedMeshRenderer>();
            var idxBuf = new int[4]; var wtBuf = new float[4];
            foreach (var mf in GearMeshFilters(w))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null) continue;
                var srcMesh = mf.sharedMesh;
                var srcVerts = srcMesh.vertices;
                var worldM = mf.transform.localToWorldMatrix;
                var newVerts = new Vector3[srcVerts.Length];
                var boneWeights = new BoneWeight[srcVerts.Length];
                for (int k = 0; k < srcVerts.Length; k++)
                {
                    Vector3 vWorld = worldM.MultiplyPoint3x4(srcVerts[k]);
                    Weigh(vWorld, idxBuf, wtBuf);
                    boneWeights[k] = new BoneWeight
                    {
                        boneIndex0 = idxBuf[0], weight0 = wtBuf[0],
                        boneIndex1 = idxBuf[1], weight1 = wtBuf[1],
                        boneIndex2 = idxBuf[2], weight2 = wtBuf[2],
                        boneIndex3 = idxBuf[3], weight3 = wtBuf[3],
                    };
                    // UNITY ADAPTATION — see class header "UNITY-SPECIFIC
                    // ADAPTATION": no bodyInv/bindMatrix dance needed. The
                    // blended bone matrix alone maps bind-space -> current
                    // world space; its inverse is the vertex's new bind-space
                    // position, directly writable into the new mesh.
                    Matrix4x4 blended = SkinXform(bones, bindposes, idxBuf, wtBuf);
                    newVerts[k] = blended.inverse.MultiplyPoint3x4(vWorld);
                }

                var geo = new Mesh { name = srcMesh.name + "_skinned" };
                geo.vertices = newVerts;
                geo.triangles = srcMesh.triangles;
                geo.uv = srcMesh.uv;
                geo.boneWeights = boneWeights;
                geo.bindposes = bindposes;
                geo.RecalculateNormals();
                geo.RecalculateBounds();

                var go = new GameObject("belt_skinned");
                var smr2 = go.AddComponent<SkinnedMeshRenderer>();
                smr2.sharedMesh = geo;
                smr2.bones = bones;
                smr2.rootBone = body.rootBone;
                smr2.sharedMaterials = mr.sharedMaterials;
                smr2.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                smr2.receiveShadows = true;
                // foot.js:1704-1706 — cosmetic only (see class header): Unity's
                // skin ignores the renderer's own transform, but match body's
                // for sane bounds/fallbacks.
                go.transform.SetParent(body.transform.parent, false);
                go.transform.localPosition = body.transform.localPosition;
                go.transform.localRotation = body.transform.localRotation;
                go.transform.localScale = body.transform.localScale;

                mr.enabled = false; // foot.js:1709 `o.visible = false`
                made.Add(smr2);
            }
            if (made.Count == 0) return false;
            w.Skinned = made;

            // foot.js:1722-1733 — the sockets ride the strap point they hang from.
            w.Ride = new List<RideEntry>();
            w.RideBones = bones;
            w.RideBindposes = bindposes;
            w.RideBody = body;
            foreach (var sk in w.Sockets)
            {
                var r = new RideEntry { Socket = sk };
                Weigh(sk.position, r.Idx, r.Wt);
                Matrix4x4 cur = SkinXform(bones, bindposes, r.Idx, r.Wt);
                r.Local = cur.inverse * sk.localToWorldMatrix;
                w.Ride.Add(r);
            }
            return true;
        }

        /// <summary>src/foot.js:1741-1782 `_skinSamples` — the body's own skin
        /// at band height: world position + 4 bone weights of every torso (or
        /// torso+legs) vertex within ~20 cm of the belt, across EVERY skinned
        /// renderer sharing `bones` (remapped by bone name if a renderer uses a
        /// different-ordered copy of the same skeleton).</summary>
        public List<SkinSample> SkinSamples(Transform[] bones, Matrix4x4[] bindposes, SkinnedMeshRenderer targetSkel, float? bandY, string mode)
        {
            var outp = new List<SkinSample>();
            if (!bandY.HasValue) return outp; // foot.js:1742
            foreach (var r in BodyMeshes())
            {
                if (!(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                var bw = smr.sharedMesh.boneWeights;
                if (bw == null || bw.Length == 0) continue;
                bool same = smr.bones == bones;
                int[] map = null;
                if (!same)
                {
                    map = new int[smr.bones.Length];
                    for (int i = 0; i < smr.bones.Length; i++) map[i] = Array.IndexOf(bones, smr.bones[i]);
                }
                var torso = TorsoTest(smr, mode);

                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var worldM = smr.transform.localToWorldMatrix;

                for (int i = 0; i < verts.Length; i++)
                {
                    if (torso != null && !torso(i)) continue;
                    Vector3 v = worldM.MultiplyPoint3x4(verts[i]);
                    if (Mathf.Abs(v.y - bandY.Value) > 0.2f) continue;
                    var b = bw[i];
                    var ii = new int[] { b.boneIndex0, b.boneIndex1, b.boneIndex2, b.boneIndex3 };
                    var ww = new float[] { b.weight0, b.weight1, b.weight2, b.weight3 };
                    if (map != null)
                    {
                        float sum = 0f;
                        for (int j = 0; j < 4; j++)
                        {
                            ii[j] = ii[j] >= 0 && ii[j] < map.Length ? map[ii[j]] : -1;
                            if (ii[j] < 0) { ii[j] = 0; ww[j] = 0f; }
                            sum += ww[j];
                        }
                        if (sum < 1e-4f) continue;
                        for (int j = 0; j < 4; j++) ww[j] /= sum;
                    }
                    outp.Add(new SkinSample { P = v, Si = ii, Sw = ww });
                }
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return outp;
        }

        /// <summary>src/foot.js:1786-1791 `_rideSockets` — call every frame,
        /// after bones have been posed for the frame (LateUpdate).</summary>
        public void RideSockets()
        {
            foreach (var w in _worn.Values) RideOne(w);
            if (BackSling != null) RideOne(BackSling);
        }

        /// <summary>src/foot.js:1793-1804 `_rideOne`.</summary>
        public void RideOne(WornGear w)
        {
            if (w?.Ride == null || w.Ride.Count == 0 || w.RideBones == null || w.RideBody == null) return;
            foreach (var r in w.Ride)
            {
                Matrix4x4 worldMat = SkinXform(w.RideBones, w.RideBindposes, r.Idx, r.Wt) * r.Local;
                var sk = r.Socket;
                if (sk.parent == null) continue;
                Matrix4x4 local = sk.parent.worldToLocalMatrix * worldMat;
                sk.localPosition = local.GetColumn(3);
                sk.localRotation = ExtractRotation(local);
                sk.localScale = ExtractScale(local);
            }
        }

        /// <summary>src/foot.js:1806-1836 `_skinXform` — what the skin does,
        /// right now, to a point carrying these bone weights: the blended
        /// `bone.localToWorldMatrix * bindpose` (see class header's "UNITY-
        /// SPECIFIC ADAPTATION" for why this needs no extra mesh-transform
        /// term in Unity, unlike the three.js original).</summary>
        public static Matrix4x4 SkinXform(Transform[] bones, Matrix4x4[] bindposes, int[] idx, float[] wt)
        {
            var acc = Matrix4x4.zero;
            for (int j = 0; j < 4; j++)
            {
                if (!(wt[j] > 0f)) continue;
                int bi = idx[j];
                if (bi < 0 || bi >= bones.Length || bones[bi] == null) continue;
                Matrix4x4 m = bones[bi].localToWorldMatrix * bindposes[bi];
                for (int e = 0; e < 16; e++) acc[e] += m[e] * wt[j];
            }
            return acc;
        }

        /// <summary>src/foot.js:1840-1848 `_unskinBelt` — drop the skinned
        /// copies, show the rigid original again.</summary>
        public void UnskinBelt(WornGear w)
        {
            if (w.Skinned != null)
            {
                foreach (var m in w.Skinned)
                {
                    if (m == null) continue;
                    if (m.sharedMesh != null) UnityEngine.Object.DestroyImmediate(m.sharedMesh);
                    UnityEngine.Object.DestroyImmediate(m.gameObject);
                }
            }
            w.Skinned = null;
            w.Ride = null;
            if (w.Obj != null)
            {
                foreach (var mr in w.Obj.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = true;
            }
        }

        // =====================================================================
        // MESH GATHERING — src/foot.js:1858-1894.
        // =====================================================================
        /// <summary>src/foot.js:1858-1877 `_bodyMeshes` — the body's own
        /// renderers, skipping every piece of worn/held/holstered/slung gear.
        /// `externalExcludes` is this port's hook for the (not yet ported)
        /// held-weapon/holster/sling systems to add their own objects (the
        /// JS's `this.held`, `this._holstered`, `this._onBack`).</summary>
        public List<Transform> ExternalExcludes { get; } = new List<Transform>();

        public List<Renderer> BodyMeshes()
        {
            var skip = new HashSet<Transform>();
            foreach (var w in _worn.Values)
            {
                if (w.Obj != null) skip.Add(w.Obj);
                if (w.Skinned != null) foreach (var m in w.Skinned) if (m != null) skip.Add(m.transform);
            }
            foreach (var t in ExternalExcludes) if (t != null) skip.Add(t);
            var outp = new List<Renderer>();
            WalkRenderers(_rig.Model, skip, outp);
            return outp;
        }

        /// <summary>src/foot.js:1884-1894 `_gearMeshes` — the gear's own
        /// meshes, stopping at a socket (what hangs in one is held kit, not
        /// strap).</summary>
        public List<MeshFilter> GearMeshFilters(WornGear w)
        {
            var skip = new HashSet<Transform>(w.Sockets);
            var outRenderers = new List<Renderer>();
            if (w.Obj != null) WalkRenderers(w.Obj, skip, outRenderers);
            var outp = new List<MeshFilter>();
            foreach (var r in outRenderers)
            {
                if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) outp.Add(mf);
            }
            return outp;
        }

        static void WalkRenderers(Transform node, HashSet<Transform> skip, List<Renderer> outp)
        {
            if (skip.Contains(node)) return;
            if (node.TryGetComponent<Renderer>(out var r))
            {
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) outp.Add(r);
                else if (r is MeshRenderer && node.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) outp.Add(r);
            }
            for (int i = 0; i < node.childCount; i++) WalkRenderers(node.GetChild(i), skip, outp);
        }

        // =====================================================================
        // TORSO HULL / TEST — src/foot.js:1903-1982.
        // =====================================================================
        static readonly Regex CoreBoneRe = new Regex(
            "pelvis|hips?$|spine|torso|abdomen|waist|belly|body|root", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LegBoneRe = new Regex(
            "thigh|upleg|upperleg|leg|knee|shin|calf|hip|butt|glute|pelvis", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ArmExcludeNarrowRe = new Regex(
            "arm|hand|finger|thumb|clav|shoulder", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>src/foot.js:1960-1982 `_torsoTest` — which vertices count as
        /// "torso" (or "lower": torso + legs), by dominant bone weight. Returns
        /// null for a non-skinned mesh (prop, one-piece): everything counts.</summary>
        public Func<int, bool> TorsoTest(SkinnedMeshRenderer mesh, string mode = "torso")
        {
            if (mesh?.sharedMesh == null) return null;
            var bw = mesh.sharedMesh.boneWeights;
            if (bw == null || bw.Length == 0) return null;
            var bones = mesh.bones;
            var keep = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                string n = bones[i] != null ? bones[i].name ?? string.Empty : string.Empty;
                if (CoreBoneRe.IsMatch(n)) keep.Add(i);
                else if (mode == "lower" && LegBoneRe.IsMatch(n) && !ArmExcludeNarrowRe.IsMatch(n)) keep.Add(i);
            }
            if (keep.Count == 0) return null;
            return (v) =>
            {
                var w = bw[v];
                int best = -1; float bwv = 0f;
                if (w.weight0 > bwv) { bwv = w.weight0; best = w.boneIndex0; }
                if (w.weight1 > bwv) { bwv = w.weight1; best = w.boneIndex1; }
                if (w.weight2 > bwv) { bwv = w.weight2; best = w.boneIndex2; }
                if (w.weight3 > bwv) { bwv = w.weight3; best = w.boneIndex3; }
                return best < 0 || keep.Contains(best);
            };
        }

        /// <summary>A brute-force raycastable triangle soup, src/foot.js:1903-1945
        /// `_torsoHull`. Unity has no built-in "raycast this loose vertex/index
        /// buffer" primitive the way three.js's BVH-backed Raycaster does, so
        /// this keeps its own triangle list and does the ray/triangle test by
        /// hand (<see cref="RayTriangle"/>) — the JS comment's "a few hundred
        /// triangles, one build per body" sizing holds here too.</summary>
        public sealed class Hull
        {
            public List<Vector3> Tris = new List<Vector3>(); // flat triples
            public float Y;

            public bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out Vector3 hitPoint)
            {
                float bestT = float.PositiveInfinity;
                hitPoint = default;
                bool any = false;
                for (int i = 0; i + 2 < Tris.Count; i += 3)
                {
                    if (RayTriangle(origin, dir, Tris[i], Tris[i + 1], Tris[i + 2], out float t) && t <= maxDist && t < bestT)
                    {
                        bestT = t; any = true;
                        hitPoint = origin + dir * t;
                    }
                }
                return any;
            }
        }

        Hull _hullTorso, _hullLower;

        public Hull TorsoHull(float y, string mode = "torso")
        {
            bool lower = mode == "lower";
            Hull cache = lower ? _hullLower : _hullTorso;
            if (cache != null && Mathf.Abs(cache.Y - y) < 0.02f) return cache;

            const float SLAB = TorsoHullSlab;
            var tris = new List<Vector3>();
            foreach (var r in BodyMeshes())
            {
                var torso = r is SkinnedMeshRenderer smr0 ? TorsoTest(smr0, mode) : null;
                Vector3[] verts; Mesh owned = null;
                int[] indices;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    owned = new Mesh();
                    smr.BakeMesh(owned, true);
                    verts = owned.vertices;
                    indices = AllTriangles(owned);
                    var m = smr.transform.localToWorldMatrix;
                    for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);
                }
                else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                {
                    var src = mf.sharedMesh;
                    verts = (Vector3[])src.vertices.Clone();
                    indices = AllTriangles(src);
                    var m = mf.transform.localToWorldMatrix;
                    for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);
                }
                else { continue; }

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
                    if (torso != null && !(torso(ia) && torso(ib) && torso(ic))) { continue; }
                    Vector3 a = verts[ia], b = verts[ib], c = verts[ic];
                    float lo = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                    float hi = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                    if (lo > y + SLAB || hi < y - SLAB) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                }
                if (owned != null) UnityEngine.Object.DestroyImmediate(owned);
            }
            if (tris.Count < TorsoHullMinTris) return null; // foot.js:1934 — too little body to measure (60 verts)

            var hull = new Hull { Tris = tris, Y = y };
            if (lower) _hullLower = hull; else _hullTorso = hull;
            return hull;
        }

        static int[] AllTriangles(Mesh m)
        {
            if (m.subMeshCount <= 1) return m.triangles;
            var all = new List<int>();
            for (int s = 0; s < m.subMeshCount; s++) all.AddRange(m.GetTriangles(s));
            return all.ToArray();
        }

        /// <summary>Möller–Trumbore. See class header on why this needs no
        /// handedness operand swap.</summary>
        static bool RayTriangle(Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0f;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-9f) return false;
            float invDet = 1f / det;
            Vector3 tvec = orig - v0;
            float u = Vector3.Dot(tvec, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(tvec, e1);
            float v = Vector3.Dot(dir, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * invDet;
            return t > 0f;
        }

        // =====================================================================
        // BODY SECTION — src/foot.js:1985-2015.
        // =====================================================================
        /// <summary>src/foot.js:1985-2015 `_bodySection` — the body's
        /// cross-section at world height `y`: centre and widest radius.</summary>
        public Section? BodySection(float y)
        {
            var pts = new List<Vector3>();
            foreach (var r in BodyMeshes())
            {
                var torso = r is SkinnedMeshRenderer smr0 ? TorsoTest(smr0) : null;
                Mesh owned = null;
                Vector3[] verts;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    owned = new Mesh();
                    smr.BakeMesh(owned, true);
                    verts = owned.vertices;
                }
                else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                {
                    verts = mf.sharedMesh.vertices;
                }
                else { continue; }
                var m = r.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++)
                {
                    if (torso != null && !torso(i)) continue;
                    Vector3 v = m.MultiplyPoint3x4(verts[i]);
                    if (Mathf.Abs(v.y - y) > 0.05f) continue;
                    pts.Add(v);
                }
                if (owned != null) UnityEngine.Object.DestroyImmediate(owned);
            }
            if (pts.Count < 8) return null;
            float lox = float.PositiveInfinity, hix = float.NegativeInfinity;
            float loz = float.PositiveInfinity, hiz = float.NegativeInfinity;
            foreach (var p in pts)
            {
                lox = Mathf.Min(lox, p.x); hix = Mathf.Max(hix, p.x);
                loz = Mathf.Min(loz, p.z); hiz = Mathf.Max(hiz, p.z);
            }
            Vector3 c = new Vector3((lox + hix) * 0.5f, y, (loz + hiz) * 0.5f);
            float r2 = 0f;
            foreach (var p in pts) r2 = Mathf.Max(r2, Mathf.Sqrt((p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z)));
            return new Section { C = c, R = r2 };
        }

        // =====================================================================
        // WAIST WIDTH — src/foot.js:2027-2115.
        // =====================================================================
        static readonly Regex WaistArmRe = new Regex(
            "arm|hand|shoulder|clavicle|finger|thumb|index|middle|pinky|ring|wrist", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        float? _waistW;
        float? _waistY;

        /// <summary>src/foot.js:2027-2115 `_waistWidth` — how wide the body is
        /// at the waist, measured once and cached (<see cref="_waistW"/>,
        /// <see cref="_waistY"/> mirror the JS's `this._waistW`/`this._waistY`).</summary>
        public float WaistWidth(Transform hips)
        {
            if (_waistW.HasValue) return _waistW.Value; // foot.js:2028
            float rigY = _rig.Root.position.y;            // foot.js:2035

            var meshes = BodyMeshes();
            Bounds? box = null;
            foreach (var r in meshes)
            {
                var b = LocalBoundsWorld(r);
                if (b == null) continue;
                if (box == null) box = b.Value; else { var bb = box.Value; bb.Encapsulate(b.Value); box = bb; }
            }

            var xs = new List<float>(); var zs = new List<float>();
            int n = 0;
            if (box.HasValue && !IsBoundsEmpty(box.Value))
            {
                float y0 = box.Value.min.y + box.Value.size.y * WaistY0Frac;
                float y1 = box.Value.min.y + box.Value.size.y * WaistY1Frac;
                foreach (var r in meshes)
                {
                    Mesh owned = null;
                    Vector3[] verts; BoneWeight[] bw = null; bool[] isArm = null;
                    Matrix4x4 m;
                    if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        owned = new Mesh();
                        smr.BakeMesh(owned, true); // foot.js:2069 `applyBoneTransform` == current posed local position
                        verts = owned.vertices;
                        bw = smr.sharedMesh.boneWeights;
                        var bones = smr.bones;
                        if (bones != null)
                        {
                            isArm = new bool[bones.Length];
                            for (int i = 0; i < bones.Length; i++) isArm[i] = bones[i] != null && WaistArmRe.IsMatch(bones[i].name ?? string.Empty);
                        }
                        m = smr.transform.localToWorldMatrix;
                    }
                    else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                    {
                        verts = mf.sharedMesh.vertices;
                        m = mf.transform.localToWorldMatrix;
                    }
                    else { continue; }

                    for (int k = 0; k < verts.Length; k++)
                    {
                        Vector3 v = m.MultiplyPoint3x4(verts[k]);
                        if (v.y < y0 || v.y > y1) continue;
                        if (isArm != null && bw != null)
                        {
                            var w = bw[k];
                            int best = -1; float bwv = 0f;
                            if (w.weight0 > bwv) { bwv = w.weight0; best = w.boneIndex0; }
                            if (w.weight1 > bwv) { bwv = w.weight1; best = w.boneIndex1; }
                            if (w.weight2 > bwv) { bwv = w.weight2; best = w.boneIndex2; }
                            if (w.weight3 > bwv) { bwv = w.weight3; best = w.boneIndex3; }
                            if (best >= 0 && best < isArm.Length && isArm[best]) continue;
                        }
                        xs.Add(v.x); zs.Add(v.z); n++;
                    }
                    if (owned != null) UnityEngine.Object.DestroyImmediate(owned);
                }
            }

            float width = 0f;
            if (n > 20)
            {
                float Spread(List<float> a)
                {
                    a.Sort();
                    int lo = Mathf.FloorToInt(a.Count * WaistSpreadLoPctl);
                    int hi = Mathf.Min(a.Count - 1, Mathf.FloorToInt(a.Count * WaistSpreadHiPctl));
                    return a[hi] - a[lo];
                }
                width = Mathf.Max(Spread(xs), Spread(zs)) * WaistSpreadFudge;
                _waistY = box.Value.min.y + box.Value.size.y * WaistYFrac - rigY;
            }
            else if (box.HasValue && !IsBoundsEmpty(box.Value))
            {
                width = box.Value.size.y * WaistGenericFallbackFrac;
                _waistY = box.Value.min.y + box.Value.size.y * WaistYFrac - rigY;
            }

            _waistW = Mathf.Clamp(width > 0f ? width : WaistDefault, WaistClampLo, WaistClampHi);
            return _waistW.Value;
        }

        static bool IsBoundsEmpty(Bounds b) => b.size == Vector3.zero;

        static Bounds? LocalBoundsWorld(Renderer r)
        {
            Mesh m = null;
            if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
            else if (r.TryGetComponent<MeshFilter>(out var mf)) m = mf.sharedMesh;
            if (m == null) return null;
            var mtx = r.transform.localToWorldMatrix;
            var c = m.bounds.center; var e = m.bounds.extents;
            Bounds wb = new Bounds(mtx.MultiplyPoint3x4(c), Vector3.zero);
            for (int xs = -1; xs <= 1; xs += 2)
                for (int ys = -1; ys <= 1; ys += 2)
                    for (int zs = -1; zs <= 1; zs += 2)
                        wb.Encapsulate(mtx.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(xs, ys, zs))));
            return wb;
        }

        // =====================================================================
        // HOLSTER SOCKET — src/foot.js:2118-2121.
        // =====================================================================
        /// <summary>src/foot.js:2118-2121 — the holster on whatever is being
        /// worn, if anything has one.</summary>
        public WornGear HolsterSocket
        {
            get
            {
                foreach (var w in _worn.Values) if (w.Socket != null) return w;
                return null;
            }
        }

        // =====================================================================
        // MATRIX DECOMPOSE — same pattern as AirframeBreakup.cs's
        // ExtractRotation/ExtractScale (see class header on LookRotation safety).
        // =====================================================================
        static Quaternion ExtractRotation(Matrix4x4 m)
        {
            Vector3 fwd = new Vector3(m.m02, m.m12, m.m22);
            Vector3 up = new Vector3(m.m01, m.m11, m.m21);
            if (fwd.sqrMagnitude < 1e-12f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd.normalized, up.sqrMagnitude > 1e-12f ? up.normalized : Vector3.up);
        }

        static Vector3 ExtractScale(Matrix4x4 m)
        {
            Vector3 x = new Vector3(m.m00, m.m10, m.m20);
            Vector3 y = new Vector3(m.m01, m.m11, m.m21);
            Vector3 z = new Vector3(m.m02, m.m12, m.m22);
            return new Vector3(x.magnitude, y.magnitude, z.magnitude);
        }
    }

    // =========================================================================
    // WORN GEAR RECORD — src/foot.js's `w` / `def` object literal shape
    // (wearGear/`_seatGear`'s parameter, foot.js:1057-1091, 1108 on). Field
    // defaults mirror src/weapons.js:76-119 `GEAR_DEFS.belt`.
    // =========================================================================
    public sealed class WornGear
    {
        public string Id;
        public Transform Obj;                 // src/foot.js `w.obj`
        public List<Transform> Sockets = new List<Transform>();
        public Transform Socket;              // src/foot.js `w.socket` — Sockets[0]

        public float Fit = 1.0f;              // src/weapons.js:99
        public float Width = 0.365f;          // src/weapons.js:89 (as-exported strap width)
        public float Back;                    // src/weapons.js:109 (0.0 on the belt today)
        public bool Conform;                  // not set on GEAR_DEFS.belt today -> false path (foot.js:1351)
        public float Drop = 0.19f;            // src/weapons.js:105
        /// <summary>src/weapons.js:118 `holsterScale` — not read by FootBelt.cs
        /// itself (it scales a HELD pistol down for display IN the holster,
        /// `_seatInPocket`'s job, foot.js:2125+, not yet ported); carried here
        /// so a future holster system has the real data without re-deriving it.</summary>
        public float HolsterScale = 0.54f;    // src/weapons.js:118

        public float Scale;                   // computed — src/foot.js:1187 `w.scale`

        /// <summary>src/foot.js:1307 `sk.userData.standExtra` — how much more a
        /// holstered pistol must stand proud of the pocket beyond what the
        /// hanger itself moved (read by the not-yet-ported `_seatInPocket`).</summary>
        public Dictionary<Transform, float> StandExtra = new Dictionary<Transform, float>();

        public List<SkinnedMeshRenderer> Skinned;
        public List<RideEntry> Ride; // RideEntry is a namespace-level sibling, not nested in FootBelt
        public Transform[] RideBones;
        public Matrix4x4[] RideBindposes;
        public SkinnedMeshRenderer RideBody;
    }

    /// <summary>src/foot.js:1730 `r = { socket, si, sw, local }` — one holster
    /// socket's riding record.</summary>
    public sealed class RideEntry
    {
        public Transform Socket;
        public int[] Idx = new int[4];
        public float[] Wt = new float[4];
        public Matrix4x4 Local;
    }
}
