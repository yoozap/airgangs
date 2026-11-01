// Which way does a bridged character body FACE in Unity? (2026-11-01, user:
// "my personages are not looking forward... apple is walking with backside".)
// The Blender lineup proves every veggie GLB faces Blender -Y (= glTF +Z);
// characters.js:467-468 then rotates the model by PI ("glTF faces +Z, gameplay
// forward is -Z"). Unity's gameplay forward is +Z, so the question is what the
// GLB -> FBX bridge (axis_forward=-Z) leaves us with. Finds the eyes through
// the albedo PNG (pupil-black / sclera-white at each triangle's UV centroid)
// and prints the eye direction in the prefab root's frame.
//
//   Unity -batchmode -quit -nographics -projectPath unity -executeMethod TbilisiFly.Probes.CharFacingProbe.Run
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    public static class CharFacingProbe
    {
        public static void Run()
        {
            foreach (var id in new[] { "apple", "banana", "cherry", "tomato", "elephant" })
            {
                string fbx = $"Assets/Resources/Art/Characters/{id}.fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                if (prefab == null) { Debug.Log($"FACING id={id} missing=true"); continue; }
                var go = Object.Instantiate(prefab);
                go.transform.position = Vector3.zero; go.transform.rotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                Texture2D tex = null;
                string png = $"Assets/Resources/Art/Characters/Textures/{id}.png";
                if (File.Exists(png)) { tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(png)); }
                var eyes = new List<Vector3>(); var all = new List<Vector3>();
                Bounds b = new Bounds(); bool first = true;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (m == null) continue;
                    var verts = m.vertices; var uvs = m.uv; var tris = m.triangles;
                    var l2w = r.transform.localToWorldMatrix;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var p = l2w.MultiplyPoint3x4(verts[i]); all.Add(p);
                        if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                    }
                    if (tex == null || uvs.Length != verts.Length) continue;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        var uv = (uvs[tris[t]] + uvs[tris[t + 1]] + uvs[tris[t + 2]]) / 3f;
                        var c = tex.GetPixelBilinear(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));
                        float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                        if (mx < 0.15f || (mn > 0.75f && mx - mn < 0.2f))
                            eyes.Add((l2w.MultiplyPoint3x4(verts[tris[t]]) + l2w.MultiplyPoint3x4(verts[tris[t + 1]]) + l2w.MultiplyPoint3x4(verts[tris[t + 2]])) / 3f);
                    }
                }
                // The limb builder points the toes and thumbs at Blender -Y (the front):
                // ball_l is the toe bone, so ball_l - foot_l is the TOE direction here.
                Transform fl = null, bl = null;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) { if (t.name == "foot_l") fl = t; if (t.name == "ball_l") bl = t; }
                if (fl != null && bl != null) { var toe = bl.position - fl.position; Debug.Log($"TOES id={id} toeDir=({toe.x:F3},{toe.y:F3},{toe.z:F3}) side={(Mathf.Abs(toe.z) >= Mathf.Abs(toe.x) ? (toe.z > 0 ? "+Z" : "-Z") : (toe.x > 0 ? "+X" : "-X"))}"); }
                else Debug.Log($"TOES id={id} bones=missing");
                Vector3 cen = Vector3.zero; foreach (var p in all) cen += p; if (all.Count > 0) cen /= all.Count;
                Vector3 ec = Vector3.zero; foreach (var p in eyes) ec += p; if (eyes.Count > 0) ec /= eyes.Count;
                var d = ec - cen; d.y = 0f;
                string side = eyes.Count == 0 ? "unknown" : (Mathf.Abs(d.z) >= Mathf.Abs(d.x) ? (d.z > 0 ? "+Z" : "-Z") : (d.x > 0 ? "+X" : "-X"));
                Debug.Log($"FACING id={id} eyeTris={eyes.Count} eyeDir=({d.x:F3},{d.y:F3},{d.z:F3}) side={side} rootRot={go.transform.rotation.eulerAngles} bbox=({b.min.x:F2},{b.min.y:F2},{b.min.z:F2})..({b.max.x:F2},{b.max.y:F2},{b.max.z:F2}) verts={all.Count}");
                Object.DestroyImmediate(go);
            }
        }
    }
}
