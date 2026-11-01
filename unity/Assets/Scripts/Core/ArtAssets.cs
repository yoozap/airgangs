using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TbilisiFly.Core
{
    /// <summary>
    /// THE ONE LOADER SEAM. Every runtime system that needs a piece of art —
    /// an FBX/OBJ model, a photo texture, a UXML tree, an AudioClip, a
    /// VolumeProfile — asks for it BY PATH through here, and never touches
    /// UnityEditor.AssetDatabase or UnityEditor.PrefabUtility itself.
    ///
    /// WHY THIS EXISTS: 32 runtime files resolved their art with
    /// `AssetDatabase.LoadAssetAtPath` under `#if UNITY_EDITOR`, each with its
    /// own `#else` arm that logged `playerSafe=false
    /// reason=editor-only-AssetDatabase` and returned. A built player therefore
    /// rendered NONE of the scenery, the sheds, the fleet or the props — the
    /// game only existed inside the editor. That is one missing layer, not 32
    /// bugs, so it gets one fix.
    ///
    /// MECHANISM: Resources. `Assets/Art`, `Assets/Audio`, `Assets/UI` and
    /// `DefaultVolumeProfile.asset` were moved under `Assets/Resources/` (with
    /// their .meta files, by AssetDatabase.MoveAsset, so every GUID survived
    /// and the 196 Read/Write model importers and the livery assignments came
    /// along untouched). Resources is path-addressed, exactly like the JS
    /// build's fetch-by-URL, needs no extra package, is deterministic in
    /// batchmode, and the whole art set is 459 MB — two orders of magnitude
    /// under the 4 GB serialized-file limit that would have forced
    /// Addressables.
    ///
    /// CALL SITES STILL READ AS PATHS. A site passes the real project path
    /// ("Assets/Resources/Art/Props/malls.fbx"); <see cref="KeyOf"/> turns it
    /// into the Resources key ("Art/Props/malls") by dropping everything up to
    /// and including the Resources root and then the extension. In the editor
    /// AssetDatabase is still used first, so editor behaviour — sub-assets,
    /// importer state, prefab instances in a saved scene — is bit-for-bit what
    /// it was before this seam landed.
    /// </summary>
    public static class ArtAssets
    {
        /// <summary>Always true now: there is no editor-only art path left.
        /// Renderers print this on their own count line so a player log can be
        /// grepped for it.</summary>
        public static bool PlayerSafe => true;

        /// <summary>The token each renderer appends to its own count line, so a
        /// PLAYER log can be grepped for the same evidence the editor log
        /// carries. Lower-case, per the port's probe-pairing rule: C#'s
        /// bool.ToString() gives "True" and would never pair with the JS side's
        /// "true".</summary>
        public static string Tag => "playerSafe=" + PlayerSafe.ToString().ToLowerInvariant();

        const string ResourcesMarker = "/Resources/";

        // Objects that came off disk. A player has no EditorUtility.IsPersistent,
        // and GeneratedChildren.Own / CityGroundData.Pin must not dispose or
        // re-flag an imported asset, so the seam remembers what it handed out
        // (plus everything reachable from a loaded prefab's renderers, which is
        // where the FBX kits' own materials and meshes come from).
        static readonly HashSet<Object> _fromDisk = new HashSet<Object>();
        static readonly Dictionary<Object, string> _paths = new Dictionary<Object, string>();

        /// <summary>
        /// "Assets/Resources/Art/Props/malls.fbx" -> "Art/Props/malls".
        /// Tolerates a path that is already a key, and a legacy
        /// pre-move "Assets/&lt;folder&gt;/..." string with no Resources root.
        /// </summary>
        public static string KeyOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string p = assetPath.Replace('\\', '/');
            int i = p.IndexOf(ResourcesMarker, StringComparison.Ordinal);
            if (i >= 0) p = p.Substring(i + ResourcesMarker.Length);
            else if (p.StartsWith("Resources/", StringComparison.Ordinal)) p = p.Substring("Resources/".Length);
            else if (p.StartsWith("Assets/", StringComparison.Ordinal)) p = p.Substring("Assets/".Length);
            int dot = p.LastIndexOf('.');
            int slash = p.LastIndexOf('/');
            if (dot > slash && dot >= 0) p = p.Substring(0, dot);
            return p;
        }

        /// <summary>Loads one asset by project path. Null if it is not there —
        /// callers keep their own "missing art" warning.</summary>
        public static T Load<T>(string assetPath) where T : Object
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            T a = null;
#if UNITY_EDITOR
            a = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(assetPath);
#endif
            if (a == null)
            {
                string key = KeyOf(assetPath);
                if (!string.IsNullOrEmpty(key)) a = Resources.Load<T>(key);
            }
            if (a != null) Register(a, assetPath);
            return a;
        }

        /// <summary>Every asset at a path, main asset and sub-assets both —
        /// the seam's AssetDatabase.LoadAllAssetsAtPath. Used to pull the
        /// AnimationClips out of an imported FBX.</summary>
        public static Object[] LoadAllAt(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return Array.Empty<Object>();
#if UNITY_EDITOR
            var all = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (all != null && all.Length > 0)
            {
                foreach (var o in all) if (o != null) Register(o, assetPath);
                return all;
            }
#endif
            string key = KeyOf(assetPath);
            var res = string.IsNullOrEmpty(key) ? Array.Empty<Object>() : Resources.LoadAll(key);
            foreach (var o in res) if (o != null) Register(o, assetPath);
            return res;
        }

        /// <summary>The typed form of <see cref="LoadAllAt"/>.</summary>
        public static T[] LoadAllAt<T>(string assetPath) where T : Object
        {
            var all = LoadAllAt(assetPath);
            var hits = new List<T>();
            foreach (var o in all) if (o is T t) hits.Add(t);
            if (hits.Count == 0)
            {
                string key = KeyOf(assetPath);
                if (!string.IsNullOrEmpty(key))
                    foreach (var o in Resources.LoadAll<T>(key))
                    {
                        if (o == null) continue;
                        Register(o, assetPath);
                        hits.Add(o);
                    }
            }
            return hits.ToArray();
        }

        /// <summary>Is there art at this path at all?</summary>
        public static bool Exists(string assetPath) => Load<Object>(assetPath) != null;

        /// <summary>
        /// The text of a JSON/TXT sidecar that sits WITH the art rather than in
        /// StreamingAssets — facades.json, pb/manifest.json, walls.json,
        /// roofs.json, the audio credits. Null when it is not there.
        ///
        /// These were read with System.IO against Application.dataPath or the
        /// current working directory, which is the Assets folder only in the
        /// editor: the first real player run logged "facades.json missing at
        /// &lt;cwd&gt;/Assets/Resources/Art/Textures/facades/facades.json" and
        /// "PHOTOFACADES missing &lt;app&gt;/Contents/Art/Textures/pb/manifest.json",
        /// and every building lost its photo facade. Unity imports .json and
        /// .txt as TextAssets, so under a Resources root they load like any
        /// other asset. A filesystem path is still honoured as a fallback so an
        /// editor probe can point at a fixture.
        /// </summary>
        public static string LoadText(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var ta = Load<TextAsset>(assetPath);
            if (ta != null) return ta.text;
            try
            {
                if (System.IO.File.Exists(assetPath)) return System.IO.File.ReadAllText(assetPath);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// A model whose mesh data must be CPU-readable (Mesh.CombineMeshes and
        /// every mesh.vertices/triangles read need it). In the editor the
        /// ModelImporter flag is forced on and persisted to the .meta on first
        /// run — wooden_table.fbx and shooting_range.fbx ship isReadable: 0 and
        /// CombineMeshes then fails SILENTLY. In a player there is no importer
        /// to ask: the flag was baked at import time, and all 196 model
        /// importers in Assets/Resources/Art are isReadable: 1, so this is just
        /// a load.
        /// </summary>
        public static GameObject LoadModelReadable(string assetPath)
        {
#if UNITY_EDITOR
            var importer = UnityEditor.AssetImporter.GetAtPath(assetPath) as UnityEditor.ModelImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                Debug.Log($"ARTASSETS reimported isReadable=1 path={assetPath}");
            }
#endif
            return Load<GameObject>(assetPath);
        }

        /// <summary>
        /// "This shader is broken." A shader that fails to compile is still
        /// found by Shader.Find and still makes a Material — it just draws
        /// magenta, which is why CityRenderer.KitShader checks instead of only
        /// testing for null. ShaderUtil.ShaderHasError is editor-only; the
        /// runtime signal with the same meaning is `isSupported`.
        /// </summary>
        public static bool ShaderHasError(Shader sh)
        {
            if (sh == null) return true;
#if UNITY_EDITOR
            if (UnityEditor.ShaderUtil.ShaderHasError(sh)) return true;
#endif
            return !sh.isSupported;
        }

        /// <summary>
        /// Instantiates a model/prefab asset into the scene. In the editor this
        /// is still PrefabUtility.InstantiatePrefab, so a baked scene keeps the
        /// prefab link it has today and the existing per-site
        /// <see cref="UnpackCompletely"/> calls still mean what they meant; in
        /// a player it is a plain clone, which is the same thing an unpacked
        /// instance is.
        /// </summary>
        public static GameObject Instantiate(GameObject prefab, Transform parent = null)
        {
            if (prefab == null) return null;
#if UNITY_EDITOR
            var go = parent == null
                ? (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab)
                : (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
            return go;
#else
            return parent == null ? Object.Instantiate(prefab) : Object.Instantiate(prefab, parent);
#endif
        }

        /// <summary>True when the object is a live prefab INSTANCE whose link
        /// would block reparenting or component surgery. Never true in a
        /// player, where <see cref="Instantiate"/> already produced a plain
        /// clone.</summary>
        public static bool IsPrefabInstance(GameObject go)
        {
#if UNITY_EDITOR
            return go != null && UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go);
#else
            return false;
#endif
        }

        /// <summary>Breaks the prefab link on the outermost instance root, so
        /// the hierarchy can be rebuilt freely. No-op in a player.</summary>
        public static void UnpackCompletely(GameObject go)
        {
#if UNITY_EDITOR
            if (go == null) return;
            if (!UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go)) return;
            var outer = UnityEditor.PrefabUtility.GetOutermostPrefabInstanceRoot(go) ?? go;
            UnityEditor.PrefabUtility.UnpackPrefabInstance(
                outer, UnityEditor.PrefabUnpackMode.Completely,
                UnityEditor.InteractionMode.AutomatedAction);
#endif
        }

        /// <summary>Where an object came from, for a log line. Empty for
        /// anything this seam did not load.</summary>
        public static string PathOf(Object o)
        {
            if (o == null) return "";
#if UNITY_EDITOR
            var p = UnityEditor.AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(p)) return p;
#endif
            return _paths.TryGetValue(o, out var known) ? known : "";
        }

        /// <summary>
        /// "This object lives on disk — do not destroy it and do not touch its
        /// hideFlags." EditorUtility.IsPersistent in the editor; in a player,
        /// anything the seam handed out (including a loaded prefab's own meshes,
        /// materials and textures) plus any GameObject that is not in a scene.
        /// </summary>
        public static bool IsPersistent(Object o)
        {
            if (o == null) return false;
#if UNITY_EDITOR
            return UnityEditor.EditorUtility.IsPersistent(o);
#else
            if (_fromDisk.Contains(o)) return true;
            if (o is GameObject g) return !g.scene.IsValid();
            if (o is Component c) return !c.gameObject.scene.IsValid();
            return false;
#endif
        }

        // ------------------------------------------------------------------

        static void Register(Object o, string assetPath)
        {
            if (o == null) return;
            if (!_paths.ContainsKey(o)) _paths[o] = assetPath;
            if (!_fromDisk.Add(o)) return;
            // A prefab's art is reached through its renderers, never through a
            // path of its own, so sweep it once here or IsPersistent would call
            // an imported FBX material "runtime" and GeneratedChildren would
            // destroy it.
            if (o is GameObject go) SweepPrefab(go);
        }

        static void SweepPrefab(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !_fromDisk.Add(m)) continue;
                    foreach (var id in m.GetTexturePropertyNameIDs())
                    {
                        var t = m.GetTexture(id);
                        if (t != null) _fromDisk.Add(t);
                    }
                }
                if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) _fromDisk.Add(mf.sharedMesh);
                }
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    _fromDisk.Add(smr.sharedMesh);
                }
            }
        }
    }
}
