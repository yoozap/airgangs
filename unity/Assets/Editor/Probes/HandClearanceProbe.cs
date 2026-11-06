// Do the hero's hands and elbows cross INSIDE the fruit under the purchased
// clips? (user 2026-11-01: "sometimes they are inside body and i dont see
// them when i am moving. i checked on apple but u need check in all")
//
// Run(): every veggie body × every clip in ParkourClipLibrary.Everything,
// 20 samples each, BodyHull penetration of hand/elbow before and after
// Character/BodyClearance.Resolve. Prints HANDCLEAR body=.. clip=.. lines and
// HANDCLEAR_SUMMARY.
// Shots(): apple/tomato/orange mid-stride under the sprint loop, after the fix,
// front and side PNGs into $TF_SHOTS (needs graphics: run without -nographics).
//
//   Unity -batchmode -quit -nographics -projectPath unity -executeMethod TbilisiFly.Probes.HandClearanceProbe.Run
//   TF_SHOTS=<dir> Unity -batchmode -quit -projectPath unity -executeMethod TbilisiFly.Probes.HandClearanceProbe.Shots
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    public static class HandClearanceProbe
    {
        const int Samples = 20;
        static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        static IEnumerable<string> Bodies()
        {
            foreach (var p in CharacterRoster.Personages)
                if (CharacterRoster.IsHumanoidBody(p.Id) && !p.ProceduralOnly) yield return p.Id;
        }

        static GameObject Spawn(string id, out Animator anim, out BodyHull hull)
        {
            anim = null; hull = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Resources/Art/Characters/{id}.fbx");
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab);
            go.transform.position = Vector3.zero; go.transform.rotation = Quaternion.identity;
            anim = go.GetComponentInChildren<Animator>();
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            hull = BodyHull.Fit(smr, null);
            return go;
        }

        public static void Run()
        {
            int bodies = 0, clipsChecked = 0, insideBefore = 0, insideAfter = 0, framesTotal = 0;
            var worstAfter = new List<(float depth, string body, string clip)>();
            foreach (var id in Bodies())
            {
                var go = Spawn(id, out var anim, out var hull);
                if (go == null || anim == null || !anim.isHuman || hull == null)
                {
                    Debug.Log($"HANDCLEAR body={id} error={(go == null ? "no_prefab" : anim == null || !anim.isHuman ? "no_humanoid" : "no_hull")}");
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                    continue;
                }
                bodies++;
                var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); var lf = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm); var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
                var ru = anim.GetBoneTransform(HumanBodyBones.RightUpperArm); var rf = anim.GetBoneTransform(HumanBodyBones.RightLowerArm); var rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
                int bodyInBefore = 0, bodyInAfter = 0, bodyFrames = 0; float bodyWorstBefore = 0f, bodyWorstAfter = 0f;
                var perClip = new List<(float before, float after, int fin, int fout, string clip)>();
                foreach (var path in ParkourClipLibrary.Everything)
                {
                    var clip = ParkourClipLibrary.Get(path);
                    if (clip == null || !clip.isHumanMotion || clip.length <= 0.02f) continue;
                    clipsChecked += bodies == 1 ? 1 : 0;
                    int fin = 0, fout = 0; float wb = 0f, wa = 0f;
                    var sl = new ArmClearState(); var sr = new ArmClearState();
                    for (int i = 0; i < Samples; i++)
                    {
                        clip.SampleAnimation(anim.gameObject, clip.length * i / (Samples - 1f));
                        var a = BodyClearance.Resolve(lu, lf, lh, hull, sl, 1f, true);   // dt=1 → full weight at once (a steady-state read)
                        var b = BodyClearance.Resolve(ru, rf, rh, hull, sr, 1f, true);
                        float before = Mathf.Max(Mathf.Max(a.HandDepthBefore, a.ElbowDepthBefore), Mathf.Max(b.HandDepthBefore, b.ElbowDepthBefore));
                        float after = Mathf.Max(Mathf.Max(a.HandDepthAfter, a.ElbowDepthAfter), Mathf.Max(b.HandDepthAfter, b.ElbowDepthAfter));
                        if (before > 0f) fin++;
                        if (after > 0.02f) fout++;  // residual tolerance 2 cm
                        wb = Mathf.Max(wb, before); wa = Mathf.Max(wa, after);
                        bodyFrames++;
                    }
                    perClip.Add((wb, wa, fin, fout, clip.name));
                    bodyInBefore += fin; bodyInAfter += fout; bodyWorstBefore = Mathf.Max(bodyWorstBefore, wb); bodyWorstAfter = Mathf.Max(bodyWorstAfter, wa);
                    if (wa > 0.02f) worstAfter.Add((wa, id, clip.name));
                }
                perClip.Sort((x, y) => y.before.CompareTo(x.before));
                for (int k = 0; k < Mathf.Min(5, perClip.Count); k++)
                {
                    var c = perClip[k];
                    Debug.Log($"HANDCLEAR body={id} clip={c.clip} framesInside={c.fin}/{Samples} worstDepth={F(c.before)} afterFramesInside={c.fout}/{Samples} afterWorst={F(c.after)}");
                }
                Debug.Log($"HANDCLEAR_BODY body={id} hullBone={hull.Bone.name} fruitVerts={hull.FruitVerts} rmaxM={F(hull.Rmax * hull.MetresPerUnit)} framesInsideBefore={bodyInBefore}/{bodyFrames} worstBefore={F(bodyWorstBefore)} framesInsideAfter={bodyInAfter}/{bodyFrames} worstAfter={F(bodyWorstAfter)}");
                insideBefore += bodyInBefore; insideAfter += bodyInAfter; framesTotal += bodyFrames;
                UnityEngine.Object.DestroyImmediate(go);
            }
            worstAfter.Sort((x, y) => y.depth.CompareTo(x.depth));
            for (int k = 0; k < Mathf.Min(10, worstAfter.Count); k++)
                Debug.Log($"HANDCLEAR_RESIDUAL body={worstAfter[k].body} clip={worstAfter[k].clip} depth={F(worstAfter[k].depth)}");
            Debug.Log($"HANDCLEAR_SUMMARY bodies={bodies} clipsChecked={clipsChecked} frames={framesTotal} inside={insideBefore} insideAfter={insideAfter}");
        }

        // ------------------------------------------------------------------
        public static void Shots()
        {
            string outDir = Environment.GetEnvironmentVariable("TF_SHOTS");
            if (string.IsNullOrEmpty(outDir)) outDir = "/tmp/handclear_shots";
            Directory.CreateDirectory(outDir);
            var sprint = ParkourClipLibrary.Get(ParkourClipLibrary.SprintLoopSmooth);
            if (sprint == null) { Debug.Log("HANDCLEAR_SHOT error=no_sprint_clip"); return; }
            var lightGo = new GameObject("sun"); var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.6f;
            lightGo.transform.rotation = Quaternion.Euler(45f, 30f, 0f);
            var camGo = new GameObject("shotcam"); var cam = camGo.AddComponent<Camera>(); cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.12f, 0.13f, 0.17f); cam.fieldOfView = 35f;
            foreach (var id in new[] { "apple", "kiwi", "strawberry" })
            {
                var go = Spawn(id, out var anim, out var hull);
                if (go == null || anim == null || hull == null) { Debug.Log($"HANDCLEAR_SHOT body={id} error=spawn"); continue; }
                var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm); var lf = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm); var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
                var ru = anim.GetBoneTransform(HumanBodyBones.RightUpperArm); var rf = anim.GetBoneTransform(HumanBodyBones.RightLowerArm); var rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
                // the frame where the clip pushes a hand deepest
                float worstT = 0f, worst = -1f;
                for (int i = 0; i < Samples; i++)
                {
                    float t = sprint.length * i / (Samples - 1f);
                    sprint.SampleAnimation(anim.gameObject, t);
                    var a = BodyClearance.Resolve(lu, lf, lh, hull, new ArmClearState(), 1f, false);
                    var b = BodyClearance.Resolve(ru, rf, rh, hull, new ArmClearState(), 1f, false);
                    float d = Mathf.Max(Mathf.Max(a.HandDepthBefore, a.ElbowDepthBefore), Mathf.Max(b.HandDepthBefore, b.ElbowDepthBefore));
                    if (d > worst) { worst = d; worstT = t; }
                }
                foreach (var fixedPose in new[] { false, true })
                {
                    sprint.SampleAnimation(anim.gameObject, worstT);
                    if (fixedPose)
                    {
                        var r1 = BodyClearance.Resolve(lu, lf, lh, hull, new ArmClearState(), 1f, true);
                        var r2 = BodyClearance.Resolve(ru, rf, rh, hull, new ArmClearState(), 1f, true);
                        Debug.Log($"HANDCLEAR_SHOTFIX body={id} L theta={F(r1.ThetaDeg)} handBefore={F(r1.HandDepthBefore)} handAfter={F(r1.HandDepthAfter)} wrote={r1.Wrote} R theta={F(r2.ThetaDeg)} handBefore={F(r2.HandDepthBefore)} handAfter={F(r2.HandDepthAfter)} wrote={r2.Wrote}");
                    }
                    // where the arm MESH actually is: mean baked position of the vertices on the left hand bone
                    {
                        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
                        var baked = new Mesh(); smr.BakeMesh(baked, true);
                        var bw = smr.sharedMesh.boneWeights; var bones = smr.bones; int hi = -1;
                        for (int bi = 0; bi < bones.Length; bi++) if (bones[bi] == lh) hi = bi;
                        Vector3 sum = Vector3.zero; int n = 0; var verts = baked.vertices;
                        for (int i = 0; i < bw.Length && i < verts.Length; i++) if (bw[i].boneIndex0 == hi && bw[i].weight0 > 0.5f) { sum += smr.transform.TransformPoint(verts[i]); n++; }
                        Debug.Log($"HANDCLEAR_MESH body={id} fixed={fixedPose} handBone={(lh != null ? lh.name : "-")} bonePos={(lh != null ? lh.position.ToString("F3") : "-")} meshHandVerts={n} meshHandMean={(n > 0 ? (sum / n).ToString("F3") : "-")}");
                        UnityEngine.Object.DestroyImmediate(baked);
                    }
                    // Camera.Render from an editor script skins with STALE bone matrices (the
                    // before/after PNGs came out identical while the baked mesh had moved), so
                    // render a CPU-baked copy of the skinned mesh instead.
                    var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
                    var temps = new List<GameObject>();
                    foreach (var smr in smrs)
                    {
                        var bakedM = new Mesh(); smr.BakeMesh(bakedM, true);
                        var tgo = new GameObject("baked_" + smr.name);
                        tgo.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                        tgo.transform.localScale = smr.transform.lossyScale;
                        tgo.AddComponent<MeshFilter>().sharedMesh = bakedM;
                        tgo.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                        smr.enabled = false; temps.Add(tgo);
                    }
                    var b = new Bounds(go.transform.position, Vector3.one);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.enabled) b.Encapsulate(r.bounds);
                    foreach (var t in temps) b.Encapsulate(t.GetComponent<Renderer>().bounds);
                    Vector3 c = b.center; float dist = Mathf.Max(b.size.y, b.size.x) * 1.6f;
                    Snap(cam, c + new Vector3(0f, 0f, dist), c, Path.Combine(outDir, $"hands_{id}_{(fixedPose ? "after" : "before")}_front.png"));
                    Snap(cam, c + new Vector3(dist, 0f, 0f), c, Path.Combine(outDir, $"hands_{id}_{(fixedPose ? "after" : "before")}_side.png"));
                    foreach (var t in temps) { UnityEngine.Object.DestroyImmediate(t.GetComponent<MeshFilter>().sharedMesh); UnityEngine.Object.DestroyImmediate(t); }
                    foreach (var smr in smrs) smr.enabled = true;
                }
                Debug.Log($"HANDCLEAR_SHOT body={id} t={F(worstT)} worstDepthBefore={F(worst)} files={outDir}/hands_{id}_*.png");
                UnityEngine.Object.DestroyImmediate(go);
            }
            UnityEngine.Object.DestroyImmediate(camGo); UnityEngine.Object.DestroyImmediate(lightGo);
        }

        static void Snap(Camera cam, Vector3 from, Vector3 at, string file)
        {
            cam.transform.position = from; cam.transform.LookAt(at);
            int w = 900, h = 900;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt; cam.Render();
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
