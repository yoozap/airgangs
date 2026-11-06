using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// WHAT THE PURCHASED CLIPS ACTUALLY CARRY (2026-11-06, user: "we are using
    /// our parkour asset, u need check demo, and make same for all our
    /// personages, i mean physic").
    ///
    /// Threepeat's pack ships every move TWICE — a root-motion file and an
    /// `IP-` in-place twin — and its demo scenes play the ROOT-MOTION ones with
    /// `m_ApplyRootMotion: 1`, which is where the demo's physics comes from.
    /// This port wired the in-place twins and invented the displacement
    /// (ParkourMoves.Move.Arc, FootClimb's hand-tuned Lerp durations), so no
    /// move travels the distance the animator authored.
    ///
    /// Before anything is rebuilt on top of the pack's motion, this probe
    /// proves the data is readable and prints what each clip actually does:
    /// a Humanoid clip keeps its root motion in the muscle curves
    /// `RootT.x/y/z` + `RootQ.x/y/z/w`, which AnimationUtility can read off the
    /// imported clip with no Animator, no scene and no play mode.
    /// </summary>
    public static class ParkourRootProbe
    {
        const string Root = "Assets/Resources/Art/Anims/Parkour";

        public static void Dump()
        {
            var files = new List<string>(Directory.GetFiles(Root, "*.fbx", SearchOption.AllDirectories));
            files.Sort();
            int rootMotion = 0, inPlace = 0, noCurves = 0;
            bool dumpedBindings = false;

            foreach (var f in files)
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                var clip = LoadClip(f);
                if (clip == null) { Debug.Log($"ROOTCLIP stem={stem} result=NOCLIP"); continue; }

                var bindings = AnimationUtility.GetCurveBindings(clip);
                if (!dumpedBindings && !stem.StartsWith("IP-"))
                {
                    var names = new List<string>();
                    foreach (var b in bindings) names.Add(b.propertyName);
                    names.Sort();
                    Debug.Log($"ROOTBINDINGS stem={stem} count={names.Count} sample={string.Join(",", names.GetRange(0, Mathf.Min(14, names.Count)))}");
                    dumpedBindings = true;
                }

                AnimationCurve cx = null, cy = null, cz = null, qy = null, qw = null;
                foreach (var b in bindings)
                {
                    switch (b.propertyName)
                    {
                        case "RootT.x": cx = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootT.y": cy = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootT.z": cz = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.y": qy = AnimationUtility.GetEditorCurve(clip, b); break;
                        case "RootQ.w": qw = AnimationUtility.GetEditorCurve(clip, b); break;
                    }
                }
                if (cx == null || cy == null || cz == null) { noCurves++; Debug.Log($"ROOTCLIP stem={stem} result=NOROOTCURVES bindings={bindings.Length}"); continue; }

                float len = clip.length;
                float x0 = cx.Evaluate(0f), y0 = cy.Evaluate(0f), z0 = cz.Evaluate(0f);
                float x1 = cx.Evaluate(len), y1 = cy.Evaluate(len), z1 = cz.Evaluate(len);
                // peak height above the start, and the lowest dip, over 64 samples
                float peak = 0f, dip = 0f, planar = 0f;
                float px = x0, pz = z0;
                for (int i = 1; i <= 64; i++)
                {
                    float t = len * i / 64f;
                    float yy = cy.Evaluate(t) - y0;
                    if (yy > peak) peak = yy;
                    if (yy < dip) dip = yy;
                    float ax = cx.Evaluate(t), az = cz.Evaluate(t);
                    planar += Mathf.Sqrt((ax - px) * (ax - px) + (az - pz) * (az - pz));
                    px = ax; pz = az;
                }
                float yaw = 0f;
                if (qy != null && qw != null)
                {
                    float w0 = qw.Evaluate(0f), sy0 = qy.Evaluate(0f);
                    float w1 = qw.Evaluate(len), sy1 = qy.Evaluate(len);
                    yaw = 2f * (Mathf.Atan2(sy1, w1) - Mathf.Atan2(sy0, w0)) * Mathf.Rad2Deg;
                    while (yaw > 180f) yaw -= 360f; while (yaw < -180f) yaw += 360f;
                }
                if (stem.StartsWith("IP-")) inPlace++; else rootMotion++;
                Debug.Log($"ROOTCLIP stem={stem} len={len:F3} net=({x1 - x0:F3},{y1 - y0:F3},{z1 - z0:F3}) path={planar:F3} peak={peak:F3} dip={dip:F3} yaw={yaw:F1}");
            }
            Debug.Log($"ROOTSUM files={files.Count} rootMotion={rootMotion} inPlace={inPlace} noRootCurves={noCurves}");
        }

        static AnimationClip LoadClip(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var c = o as AnimationClip;
                if (c != null && !c.name.StartsWith("__preview")) return c;
            }
            return null;
        }
    }
}
