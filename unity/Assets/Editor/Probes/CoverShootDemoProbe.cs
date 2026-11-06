// Reads the PURCHASED pack's own demo scenes and prints, as KEY=value lines,
// exactly how its author configured a working player: every serialized field on
// every pack behaviour, the Animator/root-motion setup, the capsule the manual
// warns about, the orbit camera's offsets, the weapons' hand placement, and the
// layers the cover system is masked to.
//
// The demo is the spec. Our hero (Character/CoverShootHero.cs) has to be able to
// show the same numbers, or the pack is running on values its author never
// tested. Same reasoning as the parkour pack: its demo moved by root motion and
// the port had invented the displacement.
//
//   -executeMethod TbilisiFly.Probes.CoverShootDemoProbe.Run
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    public static class CoverShootDemoProbe
    {
        const string TimeTrial = "Assets/ThirdParty/CoverShoot/Demo/Scenes/TimeTrial.unity";
        const string Tutorial = "Assets/ThirdParty/CoverShoot/Demo/Scenes/Tutorial.unity";

        static void L(string s) { Debug.Log("CSDEMO " + s); }

        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
        static string B(bool b) => b.ToString().ToLowerInvariant();

        public static void Run()
        {
            foreach (var path in new[] { TimeTrial, Tutorial })
            {
                try { Scene(path); }
                catch (Exception e) { L($"scene={path} threw={e.GetType().Name} msg={e.Message}"); }
            }
            Avatar();
            L("done=true");
        }

        static void Scene(string path)
        {
            var sc = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            L($"scene={sc.name} loaded={B(sc.isLoaded)} rootCount={sc.rootCount}");

            GameObject player = null;
            var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in all)
            {
                if (t.CompareTag("Player")) { player = t.gameObject; break; }
            }
            if (player == null)
            {
                foreach (var t in all)
                    if (t.GetComponent<BasicBehaviour>() != null) { player = t.gameObject; break; }
            }
            if (player == null) { L($"scene={sc.name} player=MISSING"); return; }

            L($"scene={sc.name} player={player.name} tag={player.tag} layer={LayerMask.LayerToName(player.layer)}({player.layer}) static={B(player.isStatic)}");
            L($"scene={sc.name} playerPos={V(player.transform.position)} playerRot={V(player.transform.eulerAngles)} playerScale={V(player.transform.localScale)}");

            // ---- hierarchy under the player ----
            var sb = new StringBuilder();
            Walk(player.transform, player.transform, sb, 0);
            foreach (var line in sb.ToString().Split('\n'))
                if (line.Length > 0) L($"scene={sc.name} tree {line}");

            // ---- every component, every serialized field ----
            foreach (var c in player.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var tn = c.GetType().Name;
                bool interesting = tn is "BasicBehaviour" or "MoveBehaviour" or "AimBehaviour" or "ShootBehaviour"
                    or "CoverBehaviour" or "Footsteps" or "HealthManager" or "Animator" or "Rigidbody"
                    or "CapsuleCollider" or "SphereCollider" or "BoxCollider" or "SkinnedMeshRenderer";
                if (!interesting) continue;
                Dump(sc.name, c);
            }

            // ---- the camera rig ----
            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                L($"scene={sc.name} camera={cam.name} fov={F(cam.fieldOfView)} near={F(cam.nearClipPlane)} far={F(cam.farClipPlane)} pos={V(cam.transform.position)} parent={(cam.transform.parent ? cam.transform.parent.name : "-")}");
                var orbit = cam.GetComponent<ThirdPersonOrbitCam>();
                if (orbit != null) Dump(sc.name, orbit);
            }

            // ---- the weapons lying in the demo ----
            foreach (var w in UnityEngine.Object.FindObjectsByType<InteractiveWeapon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                L($"scene={sc.name} weaponObject={w.name} pos={V(w.transform.position)} layer={LayerMask.LayerToName(w.gameObject.layer)} scale={V(w.transform.localScale)}");
                Dump(sc.name, w);
            }

            // ---- cover geometry: what layers, how big, static? ----
            var byLayer = new Dictionary<string, int>();
            var statics = 0; var colliders = 0;
            foreach (var col in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                colliders++;
                var ln = LayerMask.LayerToName(col.gameObject.layer);
                if (string.IsNullOrEmpty(ln)) ln = "layer" + col.gameObject.layer;
                byLayer.TryGetValue(ln, out int n); byLayer[ln] = n + 1;
                if (col.gameObject.isStatic) statics++;
            }
            var parts = new List<string>();
            foreach (var kv in byLayer) parts.Add($"{kv.Key}:{kv.Value}");
            parts.Sort();
            L($"scene={sc.name} colliders={colliders} static={statics} byLayer={string.Join(",", parts)}");

            // a cover piece, measured — the size the cover maths was tuned against
            foreach (var n in new[] { "knee_cover", "cover", "car_cover" })
            {
                var go = GameObject.Find(n);
                if (go == null) continue;
                var col = go.GetComponentInChildren<Collider>();
                if (col == null) continue;
                L($"scene={sc.name} coverSample={n} layer={LayerMask.LayerToName(col.gameObject.layer)} boundsSize={V(col.bounds.size)} top={F(col.bounds.max.y)}");
            }

            // every layer name the project knows, so the cover masks below decode
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                var ln = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(ln)) names.Add($"{i}:{ln}");
            }
            L($"scene={sc.name} layerNames={string.Join(",", names)}");
            L($"scene={sc.name} gravity={V(Physics.gravity)} fixedDt={F(Time.fixedDeltaTime)}");
        }

        static void Walk(Transform root, Transform t, StringBuilder sb, int depth)
        {
            if (depth > 3) return;
            var comps = t.GetComponents<Component>();
            var names = new List<string>();
            foreach (var c in comps) names.Add(c == null ? "MISSING" : c.GetType().Name);
            sb.Append(new string(' ', depth * 2)).Append(t.name)
              .Append(" local=").Append(V(t.localPosition))
              .Append(" scale=").Append(V(t.localScale))
              .Append(" [").Append(string.Join(",", names)).Append("]\n");
            for (int i = 0; i < t.childCount; i++) Walk(root, t.GetChild(i), sb, depth + 1);
        }

        static void Dump(string scene, Component c)
        {
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            var fields = new List<string>();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (it.propertyPath == "m_Script") continue;
                fields.Add($"{it.propertyPath}={Val(it)}");
            }
            // chunk so one line never gets unreadably long
            const int per = 6;
            for (int i = 0; i < fields.Count; i += per)
                L($"scene={scene} comp={c.GetType().Name} on={c.gameObject.name} {string.Join(" ", fields.GetRange(i, Mathf.Min(per, fields.Count - i)))}");
            if (c is Animator a)
            {
                L($"scene={scene} animator on={a.gameObject.name} applyRootMotion={B(a.applyRootMotion)} avatar={(a.avatar ? a.avatar.name : "-")} isHuman={B(a.avatar && a.avatar.isHuman)} controller={(a.runtimeAnimatorController ? a.runtimeAnimatorController.name : "-")} updateMode={a.updateMode} culling={a.cullingMode} layers={a.layerCount}");
            }
        }

        static string Val(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float: return F(p.floatValue);
                case SerializedPropertyType.Integer: return p.intValue.ToString();
                case SerializedPropertyType.Boolean: return B(p.boolValue);
                case SerializedPropertyType.String: return "\"" + p.stringValue + "\"";
                case SerializedPropertyType.Vector3: return V(p.vector3Value);
                case SerializedPropertyType.Vector2: return $"({F(p.vector2Value.x)}, {F(p.vector2Value.y)})";
                case SerializedPropertyType.Color: return p.colorValue.ToString();
                case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue ? p.objectReferenceValue.name : "null";
                case SerializedPropertyType.LayerMask: return LayerMaskText(p.intValue);
                case SerializedPropertyType.ArraySize: return p.intValue.ToString();
                case SerializedPropertyType.Quaternion: return V(p.quaternionValue.eulerAngles);
                default: return p.propertyType.ToString();
            }
        }

        static string LayerMaskText(int mask)
        {
            var hit = new List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                {
                    var n = LayerMask.LayerToName(i);
                    hit.Add(string.IsNullOrEmpty(n) ? i.ToString() : n);
                }
            return $"{mask}[{string.Join("|", hit)}]";
        }

        // The rig the 45 clips were authored on: proportions decide whether an
        // authored hand position lands in a hand or inside a body.
        static void Avatar()
        {
            const string fbx = "Assets/ThirdParty/CoverShoot/Demo/Models/Demo Scene/char_shadow.fbx";
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (go == null) { L($"demoAvatar=MISSING path={fbx}"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(go);
            try
            {
                var anim = inst.GetComponentInChildren<Animator>();
                L($"demoAvatar={go.name} hasAnimator={B(anim != null)} isHuman={B(anim != null && anim.avatar && anim.avatar.isHuman)} scale={V(inst.transform.localScale)}");
                var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    var m = new Mesh();
                    smr.BakeMesh(m, true);
                    var b = m.bounds;
                    L($"demoAvatar bakedSize={V(Vector3.Scale(b.size, smr.transform.lossyScale))} bindBounds={V(smr.bounds.size)} bones={smr.bones.Length}");
                    UnityEngine.Object.DestroyImmediate(m);
                }
                if (anim != null && anim.avatar && anim.avatar.isHuman)
                {
                    foreach (var hb in new[] { HumanBodyBones.Hips, HumanBodyBones.Head, HumanBodyBones.RightHand, HumanBodyBones.LeftHand,
                                               HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightFoot })
                    {
                        var t = anim.GetBoneTransform(hb);
                        if (t == null) { L($"demoAvatar bone={hb} MISSING"); continue; }
                        L($"demoAvatar bone={hb} world={V(t.position)} lossyScale={V(t.lossyScale)}");
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(inst); }
        }
    }
}
