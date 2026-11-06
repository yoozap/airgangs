using System.Collections.Generic;
using System.IO;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Bakes Threepeat's root-motion clips into
    /// Assets/Resources/Art/Anims/ParkourRootTracks.asset — see
    /// Scripts/Character/ParkourRootTracks.cs for why the game needs them.
    ///
    /// Humanoid clips carry their root motion in the muscle curves
    /// `RootT.x/y/z` and `RootQ.x/y/z/w`; AnimationUtility reads them straight
    /// off the imported clip, so this needs no Animator, no scene and no play
    /// mode. (`RootT` is in the clip's own authored units — the retarget scales
    /// it per avatar at runtime, which ParkourRootTrack.Scale mirrors.)
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.EditorTools.BakeParkourRootMotion.Run
    /// </summary>
    public static class BakeParkourRootMotion
    {
        const string ClipRoot = "Assets/Resources/Art/Anims/Parkour";
        // The Cover+Shoot loops the parkour pack does not ship (walk/jog/strafe/
        // crouch/jump/fall) live here as .anim; bake whatever root motion they
        // carry so the locomotion rate-matching constants are MEASURED too
        // (ParkourMoves.SprintNominal was 8.4 m/s against a 6.06 m/s clip).
        const string LocoRoot = "Assets/Resources/Art/Anims/Locomotion";
        const string AssetPath = "Assets/Resources/Art/Anims/ParkourRootTracks.asset";
        const int Samples = 48;

        public static void Run()
        {
            var files = new List<string>(Directory.GetFiles(ClipRoot, "*.fbx", SearchOption.AllDirectories));
            if (Directory.Exists(LocoRoot))
            {
                files.AddRange(Directory.GetFiles(LocoRoot, "*.anim", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(LocoRoot, "*.fbx", SearchOption.AllDirectories));
            }
            files.Sort();

            var tracks = new List<ParkourRootTrack>();
            int skippedInPlace = 0, skippedNoCurves = 0, skippedStill = 0;

            foreach (var f in files)
            {
                string path = f.Replace('\\', '/');
                string stem = Path.GetFileNameWithoutExtension(path);
                // the IP- twins are the in-place ones: no displacement to bake
                if (stem.StartsWith("IP-")) { skippedInPlace++; continue; }

                var clip = LoadClip(path);
                if (clip == null) { skippedNoCurves++; continue; }

                AnimationCurve cx = null, cy = null, cz = null, qx = null, qy = null, qz = null, qw = null;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    switch (b.propertyName)
                    {
                        case "RootT.x": cx = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootT.y": cy = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootT.z": cz = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.x": qx = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.y": qy = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.z": qz = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.w": qw = AnimationUtility.GetEditorCurve(clip, b); break;
                    }
                }
                if (cx == null || cy == null || cz == null) { skippedNoCurves++; continue; }

                float len = clip.length;
                var p = new Vector3[Samples + 1];
                var yaw = new float[Samples + 1];
                Vector3 p0 = new Vector3(cx.Evaluate(0f), cy.Evaluate(0f), cz.Evaluate(0f));
                float yaw0 = YawOf(qx, qy, qz, qw, 0f);
                float peak = 0f, dip = 0f, pathLen = 0f;
                Vector3 prev = Vector3.zero;

                for (int i = 0; i <= Samples; i++)
                {
                    float t = len * i / Samples;
                    Vector3 v = new Vector3(cx.Evaluate(t), cy.Evaluate(t), cz.Evaluate(t)) - p0;
                    p[i] = v;
                    yaw[i] = Mathf.DeltaAngle(yaw0, YawOf(qx, qy, qz, qw, t));
                    if (v.y > peak) peak = v.y;
                    if (v.y < dip) dip = v.y;
                    if (i > 0) pathLen += new Vector2(v.x - prev.x, v.z - prev.z).magnitude;
                    prev = v;
                }

                var track = new ParkourRootTrack
                {
                    Stem = stem, Len = len, P = p, Yaw = yaw,
                    Net = p[Samples], Peak = peak, Dip = dip, Path = pathLen,
                };
                FindEngagement(track);
                // a stationary clip (idle, ledge hang, the midair loops) has no
                // physics to contribute — keep the asset to the moves that move
                if (track.Net.magnitude < 0.02f && peak < 0.02f && dip > -0.02f && pathLen < 0.05f)
                { skippedStill++; continue; }

                tracks.Add(track);
                Debug.Log($"BAKEROOT stem={stem} len={len:F3} net=({track.Net.x:F3},{track.Net.y:F3},{track.Net.z:F3}) peak={peak:F3} dip={dip:F3} path={pathLen:F3} yaw={yaw[Samples]:F1} span=[{track.U0:F2},{track.U1:F2}] spanNet=({track.SpanNet.x:F2},{track.SpanNet.y:F2},{track.SpanNet.z:F2}) spanLen={track.SpanLen:F2} exit={track.ExitSpeed:F2}");
            }

            var set = AssetDatabase.LoadAssetAtPath<ParkourRootTracks>(AssetPath);
            bool fresh = set == null;
            if (fresh) set = ScriptableObject.CreateInstance<ParkourRootTracks>();
            set.Tracks = tracks.ToArray();
            if (fresh)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                AssetDatabase.CreateAsset(set, AssetPath);
            }
            else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"BAKEROOTSUM files={files.Count} baked={tracks.Count} inPlace={skippedInPlace} noCurves={skippedNoCurves} stationary={skippedStill} asset={AssetPath} fresh={fresh.ToString().ToLowerInvariant()}");
        }

        /// <summary>Fills [U0,U1] — the part of the clip where the root is
        /// climbing the obstacle rather than running at it. See
        /// ParkourRootTrack's own header for why this matters.
        ///
        /// A RISING move (mantle, climb-up: the root ends higher than it
        /// started) engages from where the rise begins to where it tops out. A
        /// HOP (vault, dive: it goes up and comes back) engages from take-off to
        /// touch-down. Anything else (a flat run, a pure drop) is its whole self.</summary>
        static void FindEngagement(ParkourRootTrack t)
        {
            int n = t.P.Length - 1;
            float netY = t.Net.y, peak = t.Peak;
            float scale = Mathf.Max(Mathf.Abs(netY), Mathf.Abs(peak), Mathf.Abs(t.Dip));
            int i0 = 0, i1 = n;

            if (scale > 0.12f)
            {
                if (netY > 0.3f * scale)
                {
                    // rising: the last frame still at the bottom → the first at the top
                    float lo = 0.06f * netY, hi = 0.94f * netY;
                    for (int i = 0; i <= n; i++) { if (t.P[i].y <= lo) i0 = i; else break; }
                    i1 = n;
                    for (int i = i0; i <= n; i++) { if (t.P[i].y >= hi) { i1 = i; break; } }
                }
                else if (peak > 0.12f && Mathf.Abs(netY) <= 0.3f * scale)
                {
                    // a hop: take-off → touch-down around the peak
                    float lo = 0.12f * peak;
                    int ipk = 0; float best = float.NegativeInfinity;
                    for (int i = 0; i <= n; i++) if (t.P[i].y > best) { best = t.P[i].y; ipk = i; }
                    i0 = 0; for (int i = ipk; i >= 0; i--) { if (t.P[i].y <= lo) { i0 = i; break; } }
                    i1 = n; for (int i = ipk; i <= n; i++) { if (t.P[i].y <= lo) { i1 = i; break; } }
                }
            }
            if (i1 <= i0) { i0 = 0; i1 = n; }
            // never trim to a sliver — a span under 15% of the clip is a bad read
            if ((i1 - i0) < Mathf.Max(2, n / 7)) { i0 = 0; i1 = n; }

            t.U0 = (float)i0 / n;
            t.U1 = (float)i1 / n;
            t.SpanNet = t.P[i1] - t.P[i0];

            // ground speed at the end of the window, from the last two samples
            int ia = Mathf.Max(i0, i1 - 2);
            float dtS = (i1 - ia) * (t.Len / n);
            Vector2 dp = new Vector2(t.P[i1].x - t.P[ia].x, t.P[i1].z - t.P[ia].z);
            t.ExitSpeed = dtS > 1e-3f ? dp.magnitude / dtS : 0f;
        }

        static float YawOf(AnimationCurve qx, AnimationCurve qy, AnimationCurve qz, AnimationCurve qw, float t)
        {
            if (qy == null || qw == null) return 0f;
            var q = new Quaternion(qx != null ? qx.Evaluate(t) : 0f, qy.Evaluate(t),
                                   qz != null ? qz.Evaluate(t) : 0f, qw.Evaluate(t));
            if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w < 1e-6f) return 0f;
            q.Normalize();
            return q.eulerAngles.y;
        }

        static AnimationClip LoadClip(string path)
        {
            if (path.EndsWith(".anim")) return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var c = o as AnimationClip;
                if (c != null && !c.name.StartsWith("__preview")) return c;
            }
            return null;
        }
    }
}
