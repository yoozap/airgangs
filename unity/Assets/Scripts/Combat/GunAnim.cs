using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>One cut of the whole clip: [start,end] seconds, frame-rounded at 30 fps
    /// exactly as THREE.AnimationUtils.subclip does (src/gunanim.js:127 CLIP_FPS), plus
    /// whether it loops. Mirrors the JS's {t:[a,b], loop} table entries.</summary>
    public struct ClipCut
    {
        public float Start, End;
        public bool Loop;
        public float Duration => End - Start;
        public ClipCut(float t0, float t1, bool loop)
        {
            const float fps = 30f;
            // BUG FOUND THIS PASS: Mathf.Round ties to EVEN (190.5 -> 190); JS's
            // Math.round (gunanim.js:133 `Math.round(c.t[0] * CLIP_FPS)`) always
            // ties up (190.5 -> 191). Nine of this file's own cut points land on
            // an exact half-frame (6.35*30=190.5, 7.55*30=226.5, 19.95*30=598.5,
            // 2.55*30=76.5, 13.15*30=394.5 — pistol.aimIdle.t0, rifle.reload.t0,
            // rifle.reloadEmpty.t1, rifle.draw.t0, riflegun.fire/fireLast.t1,
            // riflegun.reload.t0, riflegun.reloadEmpty.t1), so every one of them
            // was cutting one frame (1/30 s) early. Data.JsMath.Round matches the
            // JS tie-break; this is a count/frame-index per the project's own rule.
            Start = (float)TbilisiFly.Data.JsMath.Round(t0 * fps) / fps;
            End = (float)TbilisiFly.Data.JsMath.Round(t1 * fps) / fps;
            Loop = loop;
        }
    }

    /// <summary>
    /// Port of src/gunanim.js's clip tables and specs — the Beretta/AKM arms+gun
    /// animation cut points, verbatim. These are literally read off the downloaded
    /// clips' own keyframes (magazine bone leaving the gun, slide locking back,
    /// etc — see the JS's comments for how each number was measured) so every
    /// constant here is transcribed exactly, not re-derived.
    /// </summary>
    public static class GunClips
    {
        // ---- the ARMS clip (pistol_anim.glb), src/gunanim.js:27 CLIPS ----
        public static readonly Dictionary<string, ClipCut> Pistol = new Dictionary<string, ClipCut>
        {
            ["hipIdle"] = new ClipCut(2.60f, 3.58f, true),
            ["hipFire"] = new ClipCut(1.72f, 2.36f, false),
            ["aimIdle"] = new ClipCut(6.35f, 7.40f, true),
            ["aimFire"] = new ClipCut(7.54f, 8.12f, false),
            ["fireLast"] = new ClipCut(11.34f, 11.90f, false),
            ["reload"] = new ClipCut(8.42f, 10.40f, false),
            ["reloadEmpty"] = new ClipCut(12.05f, 15.00f, false),
            ["draw"] = new ClipCut(0.30f, 1.00f, false),
            ["holster"] = new ClipCut(16.36f, 16.82f, false),
            ["inspect"] = new ClipCut(3.70f, 6.05f, false),
        };

        // ---- the GUN's own parts (beretta_anim.glb), src/gunanim.js:60 GUN_CLIPS ----
        public static readonly Dictionary<string, ClipCut> PistolGun = new Dictionary<string, ClipCut>
        {
            ["fire"] = new ClipCut(7.56f, 7.90f, false),
            ["fireLast"] = new ClipCut(11.34f, 11.70f, false),
            ["reload"] = new ClipCut(8.42f, 10.40f, false),
            ["reloadEmpty"] = new ClipCut(12.05f, 15.00f, false),
        };

        // ---- the ARMS clip (akm_arms.glb), src/gunanim.js:83 AK_CLIPS ----
        public static readonly Dictionary<string, ClipCut> Rifle = new Dictionary<string, ClipCut>
        {
            ["aimIdle"] = new ClipCut(12.05f, 12.80f, true),
            ["hipIdle"] = new ClipCut(12.05f, 12.80f, true), // no one-handed hold on a rifle
            ["aimFire"] = new ClipCut(12.84f, 13.30f, false),
            ["hipFire"] = new ClipCut(12.84f, 13.30f, false),
            ["fireLast"] = new ClipCut(12.84f, 13.30f, false), // an AK's bolt never locks back
            ["dryFire"] = new ClipCut(13.84f, 14.10f, false),
            ["reload"] = new ClipCut(7.55f, 11.30f, false),
            ["reloadEmpty"] = new ClipCut(15.05f, 19.95f, false),
            ["draw"] = new ClipCut(2.55f, 4.45f, false), // the rack, on the way up
            ["holster"] = new ClipCut(20.30f, 21.60f, false),
            ["inspect"] = new ClipCut(2.00f, 5.70f, false),
        };

        // ---- the AKM's own parts (akm_anim.glb), src/gunanim.js:96 AK_GUN_CLIPS ----
        public static readonly Dictionary<string, ClipCut> RifleGun = new Dictionary<string, ClipCut>
        {
            ["fire"] = new ClipCut(12.86f, 13.15f, false),
            ["fireLast"] = new ClipCut(12.86f, 13.15f, false),
            ["dryFire"] = new ClipCut(13.84f, 14.10f, false),
            ["reload"] = new ClipCut(7.55f, 11.30f, false),
            ["reloadEmpty"] = new ClipCut(15.05f, 19.95f, false),
            ["draw"] = new ClipCut(2.55f, 4.45f, false),
        };

        /// <summary>src/gunanim.js:105 GUN_CLIP_SETS — which parts table a weapon's own
        /// GLB plays (WEAPON_DEFS[].anim names one — see Weapons.cs).</summary>
        public static readonly Dictionary<string, Dictionary<string, ClipCut>> GunClipSets =
            new Dictionary<string, Dictionary<string, ClipCut>> { ["pistol"] = PistolGun, ["rifle"] = RifleGun };
    }

    /// <summary>One cue: how far through a reload (0..1) a sound belongs, and which one.
    /// src/gunanim.js:111-122 PISTOL_SPEC.cues / RIFLE_SPEC.cues.</summary>
    public struct GunCue
    {
        public float At;
        public string Fn; // "magIn" | "magOut" | "slide" | "akMagIn" | "akMagOut" | "akRack"
        public GunCue(float at, string fn) { At = at; Fn = fn; }
    }

    /// <summary>src/gunanim.js:111/117 PISTOL_SPEC / RIFLE_SPEC — one arms clip per kind
    /// of weapon: which cut table, which bone in that file is the gun's own root (never
    /// classified by role, so named), where the idle holds are snapshotted from for the
    /// additive shots, and where in each reload the noises go.</summary>
    public sealed class GunAnimSpec
    {
        public string Name;
        public string Url;
        public Dictionary<string, ClipCut> Clips;
        public string GunBone;
        public (string clip, float t) RefAim;
        public (string clip, float t) RefHip;
        public Dictionary<string, GunCue[]> Cues;
    }

    public static class GunAnimSpecs
    {
        public static readonly GunAnimSpec Pistol = new GunAnimSpec
        {
            Name = "pistol", Url = "models/pistol_anim.glb", Clips = GunClips.Pistol, GunBone = "ARMA_043",
            RefAim = ("aimIdle", 6.8f), RefHip = ("hipIdle", 3.0f),
            Cues = new Dictionary<string, GunCue[]>
            {
                ["reload"] = new[] { new GunCue(0.67f, "magIn"), new GunCue(0.17f, "magOut") },
                ["reloadEmpty"] = new[] { new GunCue(0.93f, "slide"), new GunCue(0.60f, "magIn"), new GunCue(0.15f, "magOut") },
            },
        };

        public static readonly GunAnimSpec Rifle = new GunAnimSpec
        {
            Name = "rifle", Url = "models/akm_arms.glb", Clips = GunClips.Rifle, GunBone = "ARMA_044",
            RefAim = ("aimIdle", 12.4f), RefHip = ("aimIdle", 12.4f),
            Cues = new Dictionary<string, GunCue[]>
            {
                ["reload"] = new[] { new GunCue(0.90f, "akMagIn"), new GunCue(0.085f, "akMagOut") },
                ["reloadEmpty"] = new[] { new GunCue(0.95f, "akRack"), new GunCue(0.50f, "akMagIn"), new GunCue(0.05f, "akMagOut") },
            },
        };
    }

    /// <summary>
    /// Port of src/gunanim.js's WeaponAnim — the mixer on a gun model whose parts
    /// are keyed (the slide cycling, the magazine leaving, the AKM's bolt carrier
    /// and trigger). ONE per loaded instance; the one in the hand plays, the ones
    /// on the bench/belt sit at rest.
    ///
    /// RUNTIME ADAPTATION: THREE.AnimationUtils.subclip (src/gunanim.js:129
    /// cutClips) extracts a NEW in-memory AnimationClip containing only the
    /// [start,end] keyframes. Unity's AnimationClip has no runtime equivalent of
    /// that extraction (it is an editor-only asset operation), so this class
    /// instead keeps the ONE whole imported clip and samples it at
    /// `cut.Start + localTime` every frame via AnimationClip.SampleAnimation —
    /// behaviourally identical (the same keyframes drive the same bones over the
    /// same span), and it is what makes Reset() below trivial: it is just another
    /// sample, at each bone's rest pose captured once at construction.
    ///
    /// Three's crossFadeTo (two AnimationActions blending their SAMPLED POSES) is
    /// reproduced the same way: on a Play() while something is already running,
    /// the previous state's bone pose is snapshotted and blended toward the new
    /// state's sampled pose by a 0..1 weight over `fade` seconds.
    /// </summary>
    public sealed class WeaponAnim
    {
        readonly AnimationClip _whole;
        readonly GameObject _root;
        readonly Dictionary<string, ClipCut> _clips;
        readonly Transform[] _bones;
        readonly Vector3[] _restPos;
        readonly Quaternion[] _restRot;
        readonly Vector3[] _restScale;

        public string State { get; private set; }
        float _time;
        bool _finished;
        string _prevState;
        float _prevTimeAtSwitch;
        float _fadeT, _fadeDur;

        public WeaponAnim(GameObject root, AnimationClip whole, Dictionary<string, ClipCut> table)
        {
            _root = root;
            _whole = whole;
            _clips = table;
            _bones = root.GetComponentsInChildren<Transform>(true);
            _restPos = new Vector3[_bones.Length];
            _restRot = new Quaternion[_bones.Length];
            _restScale = new Vector3[_bones.Length];
            for (int i = 0; i < _bones.Length; i++)
            {
                _restPos[i] = _bones[i].localPosition;
                _restRot[i] = _bones[i].localRotation;
                _restScale[i] = _bones[i].localScale;
            }
        }

        // src/gunanim.js:156 play
        public void Play(string name, bool force = false, float fade = 0.04f)
        {
            if (!_clips.ContainsKey(name)) return;
            if (State == name && !force) return;
            if (State != null)
            {
                _prevState = State;
                _prevTimeAtSwitch = _time;
                _fadeT = 0f;
                _fadeDur = fade;
            }
            State = name;
            _time = 0f;
            _finished = false;
        }

        // src/gunanim.js:170 update
        public void Update(float dt)
        {
            if (State == null || _whole == null) return;
            var cur = _clips[State];
            if (!_finished)
            {
                _time += dt;
                if (_time >= cur.Duration)
                {
                    if (cur.Loop) _time = cur.Duration > 0f ? _time % cur.Duration : 0f;
                    else { _time = cur.Duration; _finished = true; } // a locked slide stays locked
                }
            }
            if (_prevState != null) _fadeT += dt;

            _whole.SampleAnimation(_root, cur.Start + _time);

            if (_prevState != null)
            {
                if (_fadeT >= _fadeDur)
                {
                    _prevState = null;
                }
                else
                {
                    // snapshot "now" (the state we just sampled onto the bones)
                    var nowPos = new Vector3[_bones.Length];
                    var nowRot = new Quaternion[_bones.Length];
                    var nowScale = new Vector3[_bones.Length];
                    for (int i = 0; i < _bones.Length; i++)
                    {
                        nowPos[i] = _bones[i].localPosition;
                        nowRot[i] = _bones[i].localRotation;
                        nowScale[i] = _bones[i].localScale;
                    }
                    var prev = _clips[_prevState];
                    float prevTime = Mathf.Min(_prevTimeAtSwitch + _fadeT, prev.Duration);
                    _whole.SampleAnimation(_root, prev.Start + prevTime);
                    float w = Mathf.Clamp01(_fadeT / Mathf.Max(0.0001f, _fadeDur)); // 0=prev .. 1=now
                    for (int i = 0; i < _bones.Length; i++)
                    {
                        _bones[i].localPosition = Vector3.Lerp(_bones[i].localPosition, nowPos[i], w);
                        _bones[i].localRotation = Quaternion.Slerp(_bones[i].localRotation, nowRot[i], w);
                        _bones[i].localScale = Vector3.Lerp(_bones[i].localScale, nowScale[i], w);
                    }
                }
            }
        }

        // src/gunanim.js:172 reset
        public void Reset()
        {
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i].localPosition = _restPos[i];
                _bones[i].localRotation = _restRot[i];
                _bones[i].localScale = _restScale[i];
            }
            State = null;
            _prevState = null;
        }
    }

    /// <summary>
    /// Port of src/gunanim.js's role classification — WHICH BONE IS WHICH, on a
    /// rig nobody agrees how to name. Pure string/tree logic, no handedness.
    /// </summary>
    public static class BoneRoles
    {
        static readonly string[] Fingers = { "thumb", "index", "middle", "ring", "pinky" };

        // src/gunanim.js:197 roleOf
        public static string RoleOf(string raw)
        {
            string n = (raw ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, "leaf|_end$|end$|ik|twist|roll")) return null;
            string side = null;
            if (Regex.IsMatch(n, "(^|[^a-z])left")) side = "l";
            else if (Regex.IsMatch(n, "(^|[^a-z])right")) side = "r";
            else if (Regex.IsMatch(n, "[._\\- ]l(\\d*)$")) side = "l";
            else if (Regex.IsMatch(n, "[._\\- ]r(\\d*)$")) side = "r";
            if (side == null) return null;

            string f = Fingers.FirstOrDefault(x => n.Contains(x) || (x == "pinky" && n.Contains("little")));
            if (f != null)
            {
                string needle = n.Contains(f) ? f : "little";
                string after = n.Substring(n.IndexOf(needle, StringComparison.Ordinal) + f.Length);
                var m = Regex.Match(after, "(\\d+)");
                int seg = 1;
                if (m.Success) seg = int.Parse(m.Groups[1].Value);
                else if (Regex.IsMatch(after, "upper|mid")) seg = 2;
                else if (Regex.IsMatch(after, "tip|distal")) seg = 3;
                if (seg < 1 || seg > 3) return null; // 4+ are fingertips
                return $"{side}.{f}{seg}";
            }
            if (Regex.IsMatch(n, "clav|shoulder")) return $"{side}.clav";
            if (Regex.IsMatch(n, "forearm|lowerarm|elbow")) return $"{side}.fore";
            if (Regex.IsMatch(n, "upperarm|upper_arm") || Regex.IsMatch(n, "(^|[^a-z])(left|right)arm")) return $"{side}.upper";
            if (Regex.IsMatch(n, "hand|wrist")) return $"{side}.hand";
            return null;
        }

        // src/gunanim.js:231 NEXT — what each role points AT down the chain.
        public static string Next(string role)
        {
            var parts = role.Split('.');
            string side = parts[0], part = parts[1];
            if (part == "clav") return $"{side}.upper";
            if (part == "upper") return $"{side}.fore";
            if (part == "fore") return $"{side}.hand";
            if (part == "hand") return $"{side}.middle1";
            var m = Regex.Match(part, "^([a-z]+)([123])$");
            if (m.Success && m.Groups[2].Value != "3") return $"{side}.{m.Groups[1].Value}{int.Parse(m.Groups[2].Value) + 1}";
            return null;
        }

        // src/gunanim.js:263-264 key() — letters and digits only, so a bone name
        // survives whatever the loader did to it (three's GLTFLoader runs every
        // node through PropertyBinding.sanitizeNodeName; glTFast may not match it
        // byte for byte). Used for the gun-root-bone fallback lookup in GunAxes.
        public static string Key(string n) => Regex.Replace((n ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        // src/gunanim.js:244 FALLBACK — roles whose joint is missing get bridged.
        public static readonly Dictionary<string, string[]> Fallback = new Dictionary<string, string[]>
        {
            ["hand"] = new[] { "index1", "thumb1" },
            ["fore"] = new[] { "middle1", "index1" },
        };

        // src/gunanim.js:252 slotsOf
        public static Dictionary<string, Transform> SlotsOf(Transform root, bool bonesOnly = true)
        {
            var slots = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                // Unity has no "is this node a Bone vs a Mesh" distinction the way three's
                // GLTFLoader does (a node is only promoted to THREE.Bone if some skin
                // claims it) — glTFast imports every node as a plain Transform, with
                // SkinnedMeshRenderer.bones pointing at the ones that matter. `bonesOnly`
                // therefore has no Unity-side meaning; the caller passes in whichever
                // root already contains just the bones (e.g. the clip's own hidden rig).
                string role = RoleOf(t.name);
                if (role != null && !slots.ContainsKey(role)) slots[role] = t;
            }
            return slots;
        }

        public static readonly Dictionary<string, int> Order = new Dictionary<string, int> { ["clav"] = 0, ["upper"] = 1, ["fore"] = 2, ["hand"] = 3 };

        // src/gunanim.js:688 depth
        public static int Depth(string role)
        {
            string part = role.Split('.')[1];
            if (Order.TryGetValue(part, out int d)) return d;
            var m = Regex.Match(part, "(\\d)$");
            int knuckle = m.Success ? int.Parse(m.Value) : 1;
            return 4 + knuckle;
        }
    }

    /// <summary>
    /// Port of src/gunanim.js's GunAnim — drives the clip's hidden reference
    /// skeleton, reads every joint it moves as a DIRECTION in a stable reference
    /// frame (+X shooter's right, +Y up, -Z the way the body faces), and hands
    /// those directions to BlendReloadPose below to lay over the player's own
    /// pose solve.
    /// </summary>
    public sealed class GunAnim
    {
        public GunAnimSpec Spec;
        public bool Ready { get; private set; }
        public string State { get; private set; }
        float _until;
        float _clipTime;
        // src/gunanim.js:416-429 play's `this._action.crossFadeTo(next, fade, false)` —
        // the reference rig's own crossfade on a state switch, same blend WeaponAnim.Play
        // /Update implements above. Needed here too: this rig is never drawn, but its
        // bone DIRECTIONS are read every frame (ReadDirs) and fed straight into
        // GunPoseBlend.Apply, so a hard pose cut on Play() would read back as a one-frame
        // pop in the visible arm, not a smooth 0.12s settle.
        string _prevState;
        float _prevClipTime, _fadeT, _fadeDur;
        Transform[] _bones;

        GameObject _root;
        AnimationClip _whole;
        Dictionary<string, ClipCut> _clips;
        Dictionary<string, Transform> _slots;
        Quaternion _ref = Quaternion.identity; // clip space -> reference frame
        Vector3 _gunBore = new Vector3(0, 0, -1), _gunUp = Vector3.up;
        Transform _gunBone;

        public readonly Dictionary<string, Vector3> Dirs = new Dictionary<string, Vector3>();
        public bool DirsOk { get; private set; }
        public Vector3 GunF = new Vector3(0, 0, -1), GunU = Vector3.up;

        public DirsSnapshot RefAim, RefHip;

        public sealed class DirsSnapshot
        {
            public Dictionary<string, Vector3> Dirs;
            public Vector3 GunF, GunU;
        }

        public GunAnim(GunAnimSpec spec) { Spec = spec; }

        /// <summary>Call once the GLB's AnimationClip and the rig's GameObject are loaded
        /// (Core/asset territory — this method just does the measurement src/gunanim.js's
        /// load() does once it has them in hand).</summary>
        public bool Initialize(GameObject root, AnimationClip whole)
        {
            _root = root;
            _whole = whole;
            _clips = Spec.Clips;
            _slots = BoneRoles.SlotsOf(root.transform, false);
            _bones = root.GetComponentsInChildren<Transform>(true);

            // Pose the rig at the two-handed aim reference and take the frame off the BODY.
            // Note: the JS does this via mixer.setTime(aimT - cut.start) on an action that
            // was already playing from the cut's own start, which nets out to sampling the
            // WHOLE clip at the absolute time aimT directly — so that is what this does.
            var aimCut = _clips[Spec.RefAim.clip];
            whole.SampleAnimation(root, Spec.RefAim.t);
            bool framed = SquareUp();
            if (framed) GunAxes();
            ReadDirs();
            RefAim = Snapshot();

            whole.SampleAnimation(root, Spec.RefHip.t);
            ReadDirs();
            RefHip = Snapshot();

            whole.SampleAnimation(root, aimCut.Start);
            Ready = true;
            // src/gunanim.js:356-357
            Debug.Log($"{Spec.Name} anim: {_clips.Count} clips cut from {whole.length:F1}s, "
                + $"frame {(framed ? "from the shoulders" : "UNSQUARED")}");
            return true;
        }

        /// <summary>src/gunanim.js:446-456 — the held weapon's own gun-parts mixer (the
        /// slide/magazine/bolt-carrier clip, Combat/WeaponAnimLoader.cs's WeaponAnim) is
        /// stepped from HERE, but ONLY by the GunAnim instance whose kind (pistol/rifle)
        /// matches the currently-held weapon — there is one GunAnim per kind and both are
        /// updated every frame, so without this guard the held weapon's mixer gets
        /// stepped TWICE as fast (tools/_probe_mag.mjs caught this: the AKM's magazine
        /// left and returned in half the real reload time). Caller passes the held
        /// weapon's own WeaponAnim and whether it is a rifle; does nothing if `heldAnim`
        /// is null or belongs to the other kind.</summary>
        public void UpdateHeldPartsGuard(float dt, WeaponAnim heldAnim, bool heldIsRifle)
        {
            if (heldAnim != null && (heldIsRifle ? "rifle" : "pistol") == Spec.Name)
                heldAnim.Update(dt);
        }

        /// <summary>src/gunanim.js:502-505 dispose — stops driving the reference rig.</summary>
        public void MarkDisposed() => Ready = false;

        // src/gunanim.js:371 _gunAxes
        bool GunAxes()
        {
            if (!_slots.TryGetValue("gun", out var gun))
            {
                var found = root_FindByName(Spec.GunBone);
                gun = found;
            }
            if (gun == null) return false;
            Quaternion q = gun.rotation; // world rotation (Unity keeps this live; no manual matrix needed)
            Quaternion inv = Quaternion.Inverse(q);
            Quaternion back = Quaternion.Inverse(_ref);
            _gunBore = inv * (back * new Vector3(0, 0, -1));
            _gunUp = inv * (back * new Vector3(0, 1, 0));
            _gunBone = gun;
            return true;
        }

        // src/gunanim.js:312,372 — matched through key() (not a raw string ==),
        // the same normalization the JS's this._bones[key(o.name)] dict uses.
        Transform root_FindByName(string name)
        {
            string wantKey = BoneRoles.Key(name);
            foreach (var t in _root.GetComponentsInChildren<Transform>(true))
                if (BoneRoles.Key(t.name) == wantKey) return t;
            return null;
        }

        // src/gunanim.js:384 _squareUp — the ONE axis a body can't be ambiguous about:
        // the shoulder line.
        bool SquareUp()
        {
            if (!_slots.TryGetValue("r.upper", out var rs) || !_slots.TryGetValue("l.upper", out var ls)
                || !_slots.TryGetValue("r.hand", out var rh) || !_slots.TryGetValue("l.hand", out var lh))
                return false;
            Vector3 x = rs.position - ls.position;
            if (x.sqrMagnitude < 1e-8f) return false;
            x.Normalize();
            Vector3 fAvg = (rh.position + lh.position) * 0.5f - (rs.position + ls.position) * 0.5f;
            fAvg -= x * Vector3.Dot(fAvg, x);
            if (fAvg.sqrMagnitude < 1e-8f) return false;
            fAvg.Normalize();
            // HANDEDNESS: JS builds u = cross(x, f); operands SWAPPED here (GeoFrame rule).
            Vector3 u = Vector3.Cross(fAvg, x).normalized;
            // columns are where clip X/Y/Z end up (x, u, -f); the inverse maps INTO the frame
            Quaternion basis = Quaternion.LookRotation(-fAvg, u); // local +Z -> -f, +Y -> u
            _ref = Quaternion.Inverse(basis);
            return true;
        }

        // src/gunanim.js:416 play
        public void Play(string name, bool force = false, float fade = 0.12f)
        {
            if (!Ready || !_clips.ContainsKey(name)) return;
            if (State == name && !force) return;
            if (State != null)
            {
                _prevState = State;
                _prevClipTime = _clipTime;
                _fadeT = 0f;
                _fadeDur = fade;
            }
            State = name;
            _clipTime = 0f;
            var spec = _clips[name];
            _until = spec.Loop ? 0f : spec.Duration;
        }

        // src/gunanim.js:431-435 `remaining` getter / src/gunanim.js:437-439 clipTime
        public float Remaining => (_until <= 0f) ? 0f : Mathf.Max(0f, _until - _clipTime);
        public float ClipTime(string name) => _clips.TryGetValue(name, out var c) ? c.Duration : 0f;

        // src/gunanim.js:441-456 update — mixer.update(dt) (which is where three applies
        // any crossFadeTo blend queued by play()) followed by _readDirs(); the held-weapon
        // mixer step (441-456's second half) lives in UpdateHeldPartsGuard above, called
        // from the wiring that actually knows which weapon is held (Core/HeldWeapon.cs).
        public void Update(float dt)
        {
            if (!Ready) return;
            if (State != null)
            {
                var cur = _clips[State];
                _clipTime += dt;
                if (cur.Loop && cur.Duration > 0f) _clipTime %= cur.Duration;
                else _clipTime = Mathf.Min(_clipTime, cur.Duration);
                if (_prevState != null) _fadeT += dt;

                _whole.SampleAnimation(_root, cur.Start + _clipTime);

                // src/gunanim.js:424 `this._action.crossFadeTo(next, fade, false)` — same
                // snapshot-and-blend technique as WeaponAnim.Update above (Unity's
                // AnimationClip has no runtime action-blend equivalent to crossFadeTo).
                if (_prevState != null)
                {
                    if (_fadeT >= _fadeDur)
                    {
                        _prevState = null;
                    }
                    else
                    {
                        var nowPos = new Vector3[_bones.Length];
                        var nowRot = new Quaternion[_bones.Length];
                        var nowScale = new Vector3[_bones.Length];
                        for (int i = 0; i < _bones.Length; i++)
                        {
                            nowPos[i] = _bones[i].localPosition;
                            nowRot[i] = _bones[i].localRotation;
                            nowScale[i] = _bones[i].localScale;
                        }
                        var prev = _clips[_prevState];
                        float prevTime = Mathf.Min(_prevClipTime + _fadeT, prev.Duration);
                        _whole.SampleAnimation(_root, prev.Start + prevTime);
                        float w = Mathf.Clamp01(_fadeT / Mathf.Max(0.0001f, _fadeDur)); // 0=prev .. 1=now
                        for (int i = 0; i < _bones.Length; i++)
                        {
                            _bones[i].localPosition = Vector3.Lerp(_bones[i].localPosition, nowPos[i], w);
                            _bones[i].localRotation = Quaternion.Slerp(_bones[i].localRotation, nowRot[i], w);
                            _bones[i].localScale = Vector3.Lerp(_bones[i].localScale, nowScale[i], w);
                        }
                    }
                }
            }
            ReadDirs();
        }

        // src/gunanim.js:458-462 _snapshotDirs
        DirsSnapshot Snapshot()
        {
            var d = new Dictionary<string, Vector3>(Dirs);
            return new DirsSnapshot { Dirs = d, GunF = GunF, GunU = GunU };
        }

        // src/gunanim.js:467 _readDirs
        void ReadDirs()
        {
            if (_slots == null) { DirsOk = false; return; }
            bool any = false;
            foreach (var role in _slots.Keys.ToArray())
            {
                string next = BoneRoles.Next(role);
                if (next == null || !_slots.TryGetValue(next, out var nextT)) continue;
                Vector3 a = _ref * _slots[role].position;
                Vector3 b = _ref * nextT.position;
                Vector3 outv = b - a;
                if (outv.sqrMagnitude < 1e-10f) continue;
                Dirs[role] = outv.normalized;
                any = true;
            }
            if (_gunBone != null)
            {
                Quaternion q = _gunBone.rotation;
                GunF = (_ref * (q * _gunBore)).normalized;
                GunU = (_ref * (q * _gunUp)).normalized;
            }
            DirsOk = any;
        }
    }

    /// <summary>
    /// Port of src/gunanim.js:555-686 blendReloadPose — lays the clip's arm directions
    /// over a hold the game's own pose solver already applied (Weapons.cs), either
    /// as the FULL reload pose (ref == null) or as an ADDITIVE recoil jolt relative
    /// to a remembered idle (ref == RefAim/RefHip snapshot).
    /// </summary>
    public static class GunPoseBlend
    {
        const float ClavMax = 0.12f; // src/gunanim.js:536 CLAV_MAX — rad, the most a clavicle is turned off its hold

        // src/gunanim.js:535 OUT — an outward-bias hook, left at zero: not inventing a
        // pose here (that's applyPistolPose's job), this IS the clip, and a "corrected"
        // clip is not the clip any more. Kept as a named dict, not a hardcoded 0f, so it
        // is the first thing reached for if a fat body ever elbows itself in the ribs.
        static readonly Dictionary<string, float> Out = new Dictionary<string, float> { ["upper"] = 0f, ["fore"] = 0f };

        public sealed class Result { public Vector3 F, U; }

        /// <summary>`slots` — the body's own role->bone map, cached by the caller (one
        /// BoneRoles.SlotsOf(root) per rig, never recomputed). `only` restricts the
        /// overlay to one side ("r"/"l") for one-handed moves (draw/holster). `anim` is
        /// the live GunAnim (for its CURRENT gunF/gunU heading and its RefAim/RefHip
        /// snapshots — src/gunanim.js's `anim` parameter is the GunAnim instance itself,
        /// not a snapshot). `dirs`/`dirsOk` are normally just `anim.Dirs`/`anim.DirsOk`,
        /// kept as separate parameters to match the JS call signature. `refSnap` — pass
        /// RefAim or RefHip for an ADDITIVE recoil jolt over that idle; null for the
        /// full reload pose outright.</summary>
        public static Result Apply(Transform root, Dictionary<string, Transform> slots, Transform hand,
            Vector3? aim, Dictionary<string, Vector3> dirs, bool dirsOk, float w, GunAnim anim,
            string only, GunAnim.DirsSnapshot refSnap)
        {
            if (!dirsOk || !(w > 0f) || slots == null) return null;

            // the frame is the BODY's — horizontal facing only, never the aim's pitch
            // HANDEDNESS: src/gunanim.js:565/567's no-aim fallback is `(0,0,-1)`,
            // Three's forward. This is a WORLD-space aim (unlike the clip-space
            // `_gunBore`/`GunF` defaults above, which stay -Z because `World()`
            // below flips their z itself), so Unity's forward is +Z. Retyped as
            // -Z it put the whole pose frame 180 degrees round whenever a caller
            // passed no aim.
            Vector3 f = (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f) ? aim.Value : Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-8f) f = Vector3.forward;
            f.Normalize();
            // HANDEDNESS: JS r = cross(f, worldUp); u = cross(r, f). Both SWAPPED here.
            Vector3 r = Vector3.Cross(Vector3.up, f);
            r = r.sqrMagnitude < 1e-8f ? Vector3.right : r.normalized;
            Vector3 u = Vector3.Cross(f, r).normalized;

            float side = 1f;
            if (slots.TryGetValue("r.upper", out var rUp))
                side = Vector3.Dot(r, rUp.position - root.position) < 0f ? -1f : 1f;

            Vector3 World(Vector3 d, float bias, float lean)
                => (r * (d.x + bias * lean) + u * d.y + f * -d.z).normalized;

            Transform Target(string role)
            {
                string next = BoneRoles.Next(role);
                if (next != null && slots.TryGetValue(next, out var nt)) return nt;
                string part = role.Split('.')[1];
                if (BoneRoles.Fallback.TryGetValue(part, out var alts))
                {
                    foreach (var alt in alts)
                        if (slots.TryGetValue($"{role.Split('.')[0]}.{alt}", out var ab)) return ab;
                }
                return slots[role].childCount > 0 ? slots[role].GetChild(0) : null;
            }

            var ordered = dirs.Keys.OrderBy(BoneRoles.Depth).ToList();
            var bones = new List<Transform>();
            var saved = new List<Quaternion>();

            foreach (var role in ordered)
            {
                if (only != null && !role.StartsWith(only + ".")) continue;
                if (!slots.TryGetValue(role, out var bone)) continue;
                var child = Target(role);
                if (child == null || child == bone) continue;
                string part = role.Split('.')[1];
                float lean = role.StartsWith("r") ? side : -side;

                // THE CLAVICLE IS NEVER LAID ON THE CLIP'S LINE — only its DELTA from its
                // own idle (a shrug), on top of wherever the body keeps it.
                var useRef = refSnap ?? (part == "clav" ? (anim?.RefAim ?? anim?.RefHip) : null);
                if (part == "clav" && useRef == null) continue;

                Vector3 dirW;
                if (useRef != null && useRef.Dirs.TryGetValue(role, out var r0))
                {
                    Vector3 cur = (child.position - bone.position);
                    if (cur.sqrMagnitude < 1e-10f) continue;
                    cur.Normalize();
                    Vector3 wa = World(r0, 0f, lean);
                    Vector3 wb = World(dirs[role], 0f, lean);
                    Quaternion qd = Quaternion.FromToRotation(wa, wb);
                    if (part == "clav")
                    {
                        // src/gunanim.js:650 — angle straight off the quaternion's w (2*acos|w|),
                        // not Quaternion.ToAngleAxis (which doesn't apply the abs()-shortest-path
                        // trick and can report the long way round for a negative-w quaternion).
                        float wComp = Mathf.Min(1f, Mathf.Abs(qd.w));
                        float ang = 2f * Mathf.Acos(wComp);
                        if (ang > ClavMax) qd = Quaternion.Slerp(qd, Quaternion.identity, 1f - ClavMax / ang);
                    }
                    dirW = qd * cur;
                }
                else if (useRef != null)
                {
                    continue; // no idle sample for this role: skip (JS: `if (!r0) continue;`)
                }
                else
                {
                    float bias = Out.TryGetValue(part, out var ob) ? ob : 0f;
                    dirW = World(dirs[role], bias, lean);
                }

                bones.Add(bone);
                saved.Add(bone.localRotation);
                // src/weapons.js (now ported, Weapons.cs) owns aimBone — the stopgap
                // WeaponAim.cs this used to call is deleted.
                Weapons.AimBone(bone, child, dirW);
            }
            if (bones.Count == 0) return null;

            if (w < 0.999f)
                for (int i = 0; i < bones.Count; i++) bones[i].localRotation = Quaternion.Slerp(bones[i].localRotation, saved[i], 1f - w);

            if (anim == null) return null;
            if (refSnap != null)
            {
                Vector3 wa = World(refSnap.GunF, 0f, 1f);
                Vector3 wb = World(anim.GunF, 0f, 1f);
                Quaternion qd = Quaternion.FromToRotation(wa, wb);
                Vector3 baseDir = (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f) ? aim.Value : f;
                return new Result { F = (qd * baseDir).normalized, U = (qd * Vector3.up).normalized };
            }
            return new Result { F = World(anim.GunF, 0f, 1f), U = World(anim.GunU, 0f, 1f) };
        }
    }
}
