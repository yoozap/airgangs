using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// THE PURCHASED PACK'S OWN PHYSICS, as data (2026-11-06, user: "we are
    /// using our parkour asset, u need check demo, and make same for all our
    /// personages, i mean physic").
    ///
    /// Threepeat's "Parkour Animation Set" ships every move twice — a
    /// root-motion file and an `IP-` in-place twin — and its demo scenes
    /// (Assets/Plugins/Threepeat/ParkourAnimations/scenes/*.unity) play the
    /// ROOT-MOTION ones on an Animator with `m_ApplyRootMotion: 1` and no
    /// controller, no gravity and no ground probe at all. That root motion IS
    /// the demo's physics: how far a vault carries, how high a mantle lifts,
    /// how fast a wall leap throws you.
    ///
    /// This port wired the in-place twins and invented the displacement
    /// instead — <see cref="ParkourMoves.Move.Arc"/>'s hand-written launch
    /// speeds and FootClimb's hand-tuned Lerp durations — so every move
    /// travelled a distance the animator never authored.
    ///
    /// A Humanoid clip keeps its root motion in the muscle curves `RootT.*` /
    /// `RootQ.*`. Editor/BakeParkourRootMotion.cs reads those off the 70
    /// root-motion FBXs and bakes this asset; the game then warps each track
    /// onto the obstacle actually in front of the hero (the pack ships no
    /// motion warping of its own — its manual, section 1.4.1, says to add it).
    ///
    /// WHY THIS IS AUTOMATICALLY RIGHT FOR EVERY PERSONAGE: the track is the
    /// clip's authored motion for the authoring rig, and the warp maps it onto
    /// measured world geometry, so a 1.8 m banana and a 1.8 m potato both end
    /// up standing on the real ledge. <see cref="Scale"/> carries the one
    /// body-size term (hero height / authored height) for bodies that are not
    /// 1.8 m.
    /// </summary>
    [System.Serializable]
    public sealed class ParkourRootTrack
    {
        /// <summary>File stem of the ROOT-MOTION clip (never the `IP-` twin).</summary>
        public string Stem;
        /// <summary>Clip length (s) — also the move's natural duration.</summary>
        public float Len;
        /// <summary>Root position at each sample, relative to the first frame,
        /// in the clip's own frame: +Z forward, +Y up, +X the character's left
        /// or right per the clip. Length is Samples+1, evenly spaced in time.</summary>
        public Vector3[] P;
        /// <summary>Root yaw at each sample (degrees), relative to the first frame.</summary>
        public float[] Yaw;
        /// <summary>P[last] — the move's whole displacement.</summary>
        public Vector3 Net;
        /// <summary>Highest and lowest the root goes relative to the start (m).</summary>
        public float Peak, Dip;
        /// <summary>Arc length of the planar path (m) — longer than |Net| for a
        /// move that curves or doubles back.</summary>
        public float Path;

        // ---- THE OBSTACLE-ENGAGEMENT WINDOW ---------------------------------
        // The pack's moves are whole run-ups: `mantle-low-frontroll-sprint-to-
        // sprint` travels 6.91 m, of which only ~1 m is the climb — the rest is
        // the sprint in and the sprint out. Our FootClimb starts with the hero
        // already at the wall and ends standing on it, so warping the WHOLE clip
        // onto that 1.16 m squashed it to a third of its authored speed
        // (measured: kz clamped to 0.35, residual 1.26 m).
        //
        // [U0,U1] is the part where the root actually climbs: from the moment
        // the rise begins to the moment it tops out (for a vault's hop, from
        // take-off to touch-down). The game plays the clip from U0·Len and warps
        // only that span, so the run-up and run-out go back to being ordinary
        // locomotion — which is what they are.
        public float U0, U1;
        /// <summary>Displacement across [U0,U1] — the span that gets warped.</summary>
        public Vector3 SpanNet;
        /// <summary>Planar ground speed (m/s) the clip is doing at U1, so the
        /// hero runs out of the move at the pace the animator gave it.</summary>
        public float ExitSpeed;

        /// <summary>Clip seconds of the engagement window.</summary>
        public float SpanLen => Len * Mathf.Max(0.02f, U1 - U0);

        public int Count => P != null ? P.Length : 0;

        /// <summary>Root offset at normalised time u (0..1), linearly
        /// interpolated between samples. Outside [0,1] it clamps.</summary>
        public Vector3 Sample(float u)
        {
            if (P == null || P.Length == 0) return Vector3.zero;
            if (P.Length == 1) return P[0];
            float f = Mathf.Clamp01(u) * (P.Length - 1);
            int i = Mathf.FloorToInt(f);
            if (i >= P.Length - 1) return P[P.Length - 1];
            return Vector3.Lerp(P[i], P[i + 1], f - i);
        }

        /// <summary>Root yaw (degrees) at normalised time u.</summary>
        public float SampleYaw(float u)
        {
            if (Yaw == null || Yaw.Length == 0) return 0f;
            if (Yaw.Length == 1) return Yaw[0];
            float f = Mathf.Clamp01(u) * (Yaw.Length - 1);
            int i = Mathf.FloorToInt(f);
            if (i >= Yaw.Length - 1) return Yaw[Yaw.Length - 1];
            return Mathf.Lerp(Yaw[i], Yaw[i + 1], f - i);
        }

        /// <summary>Root offset at normalised time within the engagement window
        /// (0 = U0, 1 = U1), relative to the window's own start.</summary>
        public Vector3 SampleSpan(float u)
        {
            float a = U0 + Mathf.Clamp01(u) * Mathf.Max(0f, U1 - U0);
            return Sample(a) - Sample(U0);
        }

        /// <summary>Root yaw (deg) within the engagement window, relative to its start.</summary>
        public float SampleSpanYaw(float u)
        {
            float a = U0 + Mathf.Clamp01(u) * Mathf.Max(0f, U1 - U0);
            return SampleYaw(a) - SampleYaw(U0);
        }

        /// <summary>Takeoff velocity (m/s) over the first `window` seconds —
        /// what a launch needs when our own gravity flies the rest.</summary>
        public Vector3 Takeoff(float window = 0.12f)
        {
            if (Len <= 1e-4f) return Vector3.zero;
            float u = Mathf.Clamp01(window / Len);
            return Sample(u) / Mathf.Max(1e-4f, u * Len);
        }
    }

    /// <summary>The baked set; one asset in Resources, loaded once.</summary>
    public sealed class ParkourRootTracks : ScriptableObject
    {
        public const string ResourcePath = "Art/Anims/ParkourRootTracks";

        public ParkourRootTrack[] Tracks;

        static ParkourRootTracks _inst;
        static Dictionary<string, ParkourRootTrack> _map;
        static bool _warned;

        public static ParkourRootTracks Instance
        {
            get
            {
                if (_inst == null) _inst = Resources.Load<ParkourRootTracks>(ResourcePath);
                return _inst;
            }
        }

        /// <summary>The track for a clip, given its asset path, file name or
        /// stem, with or without the `IP-` prefix — a move names its in-place
        /// clip and gets its root-motion twin's physics.</summary>
        public static ParkourRootTrack Get(string clipOrPath)
        {
            if (string.IsNullOrEmpty(clipOrPath)) return null;
            if (_map == null)
            {
                var inst = Instance;
                if (inst == null || inst.Tracks == null)
                {
                    if (!_warned) { _warned = true; Debug.LogWarning("PARKOUR root tracks asset missing — moves fall back to their authored Arc"); }
                    return null;
                }
                _map = new Dictionary<string, ParkourRootTrack>(inst.Tracks.Length);
                foreach (var t in inst.Tracks) if (t != null && !string.IsNullOrEmpty(t.Stem)) _map[t.Stem] = t;
            }
            return _map.TryGetValue(StemOf(clipOrPath), out var hit) ? hit : null;
        }

        /// <summary>`.../vault/IP-vault-low-kongshort-run-to-run.fbx` →
        /// `vault-low-kongshort-run-to-run`.</summary>
        public static string StemOf(string clipOrPath)
        {
            string s = clipOrPath;
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            if (s.StartsWith("IP-")) s = s.Substring(3);
            return s;
        }

        /// <summary>Test seam: let a probe inject a freshly baked set without
        /// going through Resources.</summary>
        public static void UseForTests(ParkourRootTracks set) { _inst = set; _map = null; _warned = false; }
    }
}
