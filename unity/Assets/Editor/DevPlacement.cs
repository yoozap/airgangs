using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Port of src/devmode.js's PERSISTENCE half — the one piece of devmode
    /// that is real functionality, not just the gizmo. The project decided
    /// Unity's own Scene-view Move/Rotate/Scale tools replace devmode.js's
    /// TransformControls overlay outright ("the editor IS the gizmo" — a
    /// second in-game gizmo would be redundant with what every Unity agent
    /// already has on screen). What that decision does NOT replace is:
    ///   - saving a hand-placed prop's override so it survives a reload
    ///     (devmode.js:11-14 "Every drag is saved to localStorage and put
    ///     back on reload"), here across a Unity domain reload / scene
    ///     reopen via EditorPrefs (the Editor-side analogue of localStorage
    ///     — both are per-machine, neither travels with the repo, which is
    ///     the SAME trade-off the JS already made, not a new one)
    ///   - printing a ready-to-paste snippet of where something ended up
    ///     (devmode.js:242-255 snippet()), including the WORLD position/yaw
    ///     and lat/lon the JS panel also showed
    ///   - the opt-in registry + the "baked-collider props must not
    ///     register" rule (devmode.js:16-20: "Props whose colliders are
    ///     baked into a world-space soup ... must not register: the picture
    ///     would move and the wall would stay")
    ///
    /// USAGE — a prop opts in by carrying a
    /// <see cref="TbilisiFly.Tools.DevPlacementTag"/> component (add it by
    /// hand in the Inspector, or via
    /// <c>gameObject.AddComponent&lt;DevPlacementTag&gt;()</c> from whichever
    /// script places it) with a unique <c>id</c>. That component lives at
    /// Assets/Scripts/Tools/DevPlacementTag.cs — NOT in this Editor/ folder —
    /// because Unity treats any folder literally named "Editor" as an
    /// editor-only assembly, and a MonoBehaviour meant to be attached to a
    /// scene GameObject cannot live there (its calls into this class are
    /// still #if UNITY_EDITOR-guarded, so it compiles to a no-op in a player
    /// build). From then on:
    ///   - its saved override (if any) is applied automatically on every
    ///     domain reload / scene open (the "reload-lands-on-menu" class of
    ///     bug devmode.js's own header calls out is exactly this: an object
    ///     silently NOT reapplying its saved spot after a reload reads as
    ///     "my edit vanished" — this class re-applies from THREE separate
    ///     hooks, not one, so a missed domain-reload path doesn't quietly
    ///     drop every saved placement)
    ///   - moving it with Unity's native tools auto-commits the new
    ///     transform to EditorPrefs (polled once per editor tick — Unity has
    ///     no generic "gizmo drag ended" event for an arbitrary registered
    ///     object the way three.js's TransformControls fires
    ///     'dragging-changed', so this reproduces the SAME end state —
    ///     "every drag is saved" — by diffing against the last committed
    ///     TRS instead of listening for a drag-end event). An epsilon guard
    ///     (POS_EPS/ROT_EPS/SCALE_EPS) is devmode.js's CLICK_PX pointer-guard
    ///     carried over: a value that hasn't actually changed must never
    ///     re-commit and spam the console/EditorPrefs write.
    ///   - TbilisiFly/Dev Placement menu: list + select + copy-snippet + reset.
    ///
    /// NOT PORTED (the gizmo half, by the project decision above): the
    /// translate/rotate/scale mode buttons, snap toggle, world/local toggle,
    /// the in-game panel UI, the ` hotkey and click-to-select raycast — all
    /// of that is simply "use Unity's own Scene view", which already has
    /// every one of those as a first-class feature.
    /// </summary>
    [InitializeOnLoad]
    public static class DevPlacement
    {
        const string PrefsKey = "TbilisiFly.DevPlacementOverrides";
        const float PosEps = 1e-4f;
        const float RotEps = 1e-3f;   // degrees
        const float ScaleEps = 1e-4f;

        [Serializable]
        class OverrideEntry
        {
            public float px, py, pz;
            public float rx, ry, rz;
            public float sx, sy, sz;
        }

        [Serializable]
        class Store
        {
            public Dictionary<string, OverrideEntry> items = new Dictionary<string, OverrideEntry>();
        }

        class Tracked
        {
            public Transform T;
            public OverrideEntry Home;     // the code-authored TRS, snapshotted at Register() time
            public OverrideEntry LastCommitted; // last value written to the store, for the epsilon guard
            public bool HasOverride;
        }

        static Store _store;
        static readonly Dictionary<string, Tracked> _registry = new Dictionary<string, Tracked>();

        public static IReadOnlyCollection<string> RegisteredIds => _registry.Keys;

        static DevPlacement()
        {
            Load();
            // Three separate re-apply hooks, deliberately redundant (see the
            // class header's note on "reload-lands-on-menu"): a scene opened
            // fresh, a script recompile's domain reload, and ongoing polling
            // for anything that registers AFTER either of those (e.g. a
            // prefab instantiated later in the same session) are three
            // different moments something could silently fail to reapply.
            EditorSceneManager.sceneOpened += (_, __) => ReapplyAll();
            AssemblyReloadEvents.afterAssemblyReload += ReapplyAll;
            EditorApplication.update += Tick;
        }

        // ---- registry -----------------------------------------------------------

        /// <summary>
        /// Finds every DevPlacementTag in the open scene and registers it.
        ///
        /// This replaces the tag calling Register() from its own OnEnable, which
        /// CANNOT work: the tag is a runtime MonoBehaviour in Assembly-CSharp and
        /// this file is in Assembly-CSharp-Editor, which is compiled AFTER it — so
        /// runtime -> editor is not a legal dependency direction, `#if UNITY_EDITOR`
        /// or not (it failed as CS0234). Editor -> runtime is fine, so the pull
        /// direction is the only one available.
        /// </summary>
        public static int ScanForTags()
        {
            int n = 0;
            foreach (var tag in UnityEngine.Object.FindObjectsByType<TbilisiFly.Tools.DevPlacementTag>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Register(tag.transform, tag.id, tag.bakedCollider);
                n++;
            }
            return n;
        }

        /// <summary>Called by <see cref="DevPlacementTag"/>.OnEnable. A baked-collider
        /// prop (colliders baked into a world-space soup keyed by where the mesh WAS
        /// placed in code — devmode.js's own example: "the tables, the ranges") must
        /// refuse to register, exactly like devmode.js's class header documents.</summary>
        public static void Register(Transform t, string id, bool bakedCollider)
        {
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning($"DevPlacement: '{t.name}' has no id set on its DevPlacementTag — not registering.");
                return;
            }
            if (bakedCollider)
            {
                Debug.LogWarning($"DevPlacement: '{id}' is tagged bakedCollider — NOT registering (its collider " +
                                  "is baked into a world-space soup keyed by where the code put it; moving the " +
                                  "transform here would move the picture but leave the wall where it was).");
                return;
            }

            var tr = new Tracked { T = t, Home = Snapshot(t) };
            _registry[id] = tr;

            if (_store.items.TryGetValue(id, out var ov))
            {
                Apply(t, ov);
                tr.HasOverride = true;
                tr.LastCommitted = ov;
                Debug.Log($"DevPlacement: '{id}' placed from its saved override.");
            }
            else
            {
                tr.LastCommitted = tr.Home;
            }
        }

        public static void Unregister(string id) => _registry.Remove(id);

        static void ReapplyAll()
        {
            foreach (var kv in _registry)
            {
                if (_store.items.TryGetValue(kv.Key, out var ov) && kv.Value.T != null)
                {
                    Apply(kv.Value.T, ov);
                    kv.Value.HasOverride = true;
                    kv.Value.LastCommitted = ov;
                }
            }
        }

        // ---- auto-commit polling (devmode.js's "every drag is saved") ----------

        static void Tick()
        {
            if (_registry.Count == 0) return;
            foreach (var kv in _registry)
            {
                var tr = kv.Value;
                if (tr.T == null) continue; // destroyed/unloaded since registering
                var now = Snapshot(tr.T);
                if (!Changed(now, tr.LastCommitted)) continue; // the pointer-guard equivalent: no real move, no commit
                Commit(kv.Key, tr, now);
            }
        }

        static void Commit(string id, Tracked tr, OverrideEntry now)
        {
            tr.LastCommitted = now;
            tr.HasOverride = true;
            _store.items[id] = now;
            Save();
            Debug.Log($"DevPlacement: {id} ->\n{SnippetFor(id)}");
        }

        static bool Changed(OverrideEntry a, OverrideEntry b)
        {
            if (b == null) return true;
            return Mathf.Abs(a.px - b.px) > PosEps || Mathf.Abs(a.py - b.py) > PosEps || Mathf.Abs(a.pz - b.pz) > PosEps
                || Mathf.Abs(a.rx - b.rx) > RotEps || Mathf.Abs(a.ry - b.ry) > RotEps || Mathf.Abs(a.rz - b.rz) > RotEps
                || Mathf.Abs(a.sx - b.sx) > ScaleEps || Mathf.Abs(a.sy - b.sy) > ScaleEps || Mathf.Abs(a.sz - b.sz) > ScaleEps;
        }

        // ---- reset / commit-now / snippet, driven by the current Selection ------

        [MenuItem("TbilisiFly/Dev Placement/Commit Selected Now %#k")]
        public static void CommitSelectedNow()
        {
            var id = IdOfSelection(out var t, out var tracked);
            if (id == null) return;
            Commit(id, tracked, Snapshot(t));
        }

        [MenuItem("TbilisiFly/Dev Placement/Reset Selected To Code Position %#r")]
        public static void ResetSelected()
        {
            var id = IdOfSelection(out var t, out var tracked);
            if (id == null) return;
            Apply(t, tracked.Home);
            tracked.LastCommitted = tracked.Home;
            tracked.HasOverride = false;
            _store.items.Remove(id);
            Save();
            Debug.Log($"DevPlacement: '{id}' reset to its code-authored position.");
        }

        [MenuItem("TbilisiFly/Dev Placement/Copy Snippet For Selected %#c")]
        public static void CopySnippetForSelected()
        {
            var id = IdOfSelection(out _, out _);
            if (id == null) return;
            string s = SnippetFor(id);
            EditorGUIUtility.systemCopyBuffer = s;
            Debug.Log($"DevPlacement: copied —\n{s}");
        }

        [MenuItem("TbilisiFly/Dev Placement/Show Registry")]
        public static void ShowRegistry() => DevPlacementWindow.Open();

        static string IdOfSelection(out Transform t, out Tracked tracked)
        {
            t = Selection.activeTransform;
            tracked = null;
            if (t == null) { Debug.LogWarning("DevPlacement: nothing selected."); return null; }
            foreach (var kv in _registry)
            {
                if (kv.Value.T == t) { tracked = kv.Value; return kv.Key; }
            }
            Debug.LogWarning($"DevPlacement: '{t.name}' carries no registered DevPlacementTag.");
            return null;
        }

        // ---- the copyable snippet (devmode.js:242-255 snippet()) ----------------

        public static string SnippetFor(string id)
        {
            if (!_registry.TryGetValue(id, out var tr) || tr.T == null) return $"// '{id}' is not currently registered";
            var t = tr.T;
            var world = t.position;
            float yawDeg = t.eulerAngles.y;
            var (lat, lon) = GeoFrame.ToLatLon(world);
            string F(float v, int d = 2) => Math.Abs(v) < 5e-5f ? "0" : v.ToString("F" + d, CultureInfo.InvariantCulture);
            return string.Join("\n", new[]
            {
                $"// {id}  (parent: {(t.parent != null ? t.parent.name : "scene")})",
                $"transform.localPosition = new Vector3({F(t.localPosition.x)}f, {F(t.localPosition.y)}f, {F(t.localPosition.z)}f);",
                $"transform.localEulerAngles = new Vector3({F(t.localEulerAngles.x, 3)}f, {F(t.localEulerAngles.y, 3)}f, {F(t.localEulerAngles.z, 3)}f);",
                $"transform.localScale = new Vector3({F(t.localScale.x)}f, {F(t.localScale.y)}f, {F(t.localScale.z)}f);",
                $"// world x={F(world.x, 1)} y={F(world.y, 2)} z={F(world.z, 1)} yaw={F(yawDeg, 1)}°" +
                $"  lat={lat.ToString("F6", CultureInfo.InvariantCulture)} lon={lon.ToString("F6", CultureInfo.InvariantCulture)}",
            });
        }

        // ---- snapshot / apply -----------------------------------------------------

        static OverrideEntry Snapshot(Transform t) => new OverrideEntry
        {
            px = t.localPosition.x, py = t.localPosition.y, pz = t.localPosition.z,
            rx = t.localEulerAngles.x, ry = t.localEulerAngles.y, rz = t.localEulerAngles.z,
            sx = t.localScale.x, sy = t.localScale.y, sz = t.localScale.z,
        };

        static void Apply(Transform t, OverrideEntry o)
        {
            t.localPosition = new Vector3(o.px, o.py, o.pz);
            t.localEulerAngles = new Vector3(o.rx, o.ry, o.rz);
            t.localScale = new Vector3(o.sx, o.sy, o.sz);
        }

        // ---- EditorPrefs persistence (the localStorage analogue) -----------------

        static void Load()
        {
            try
            {
                string json = EditorPrefs.GetString(PrefsKey, "");
                _store = string.IsNullOrEmpty(json) ? new Store() : JsonConvert.DeserializeObject<Store>(json) ?? new Store();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DevPlacement: overrides unreadable, starting empty ({e.Message})");
                _store = new Store();
            }
        }

        static void Save()
        {
            try { EditorPrefs.SetString(PrefsKey, JsonConvert.SerializeObject(_store)); }
            catch (Exception e) { Debug.LogWarning($"DevPlacement: could not save overrides ({e.Message})"); }
        }

        /// <summary>For MaterialProbe/anyone else that wants a clean count without
        /// touching EditorPrefs directly.</summary>
        public static int SavedOverrideCount() => _store?.items.Count ?? 0;
        public static bool HasOverrideFor(string id) => _registry.TryGetValue(id, out var tr) && tr.HasOverride;

        public static bool TryGetTransform(string id, out Transform t)
        {
            t = null;
            if (_registry.TryGetValue(id, out var tr) && tr.T != null) { t = tr.T; return true; }
            return false;
        }
    }

    /// <summary>Minimal list window — select/reset/copy-snippet without needing the
    /// menu-item keyboard shortcuts or hand-typing an id. The JS panel's button-grid
    /// equivalent (devmode.js:298-320 _render), trimmed to what an EditorWindow needs
    /// since Unity's own Inspector already shows the live TRS values devmode.js's
    /// panel had to draw itself.</summary>
    public class DevPlacementWindow : EditorWindow
    {
        Vector2 _scroll;

        public static void Open() => GetWindow<DevPlacementWindow>("Dev Placement");

        void OnGUI()
        {
            EditorGUILayout.LabelField("რეგისტრირებული ობიექტები", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"შენახული override-ები: {DevPlacement.SavedOverrideCount()}");
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var id in DevPlacement.RegisteredIds)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(id + (DevPlacement.HasOverrideFor(id) ? "  •" : ""));
                if (GUILayout.Button("Select", GUILayout.Width(60)) && DevPlacement.TryGetTransform(id, out var sel))
                {
                    Selection.activeTransform = sel;
                }
                if (GUILayout.Button("Snippet", GUILayout.Width(70)))
                {
                    string s = DevPlacement.SnippetFor(id);
                    EditorGUIUtility.systemCopyBuffer = s;
                    Debug.Log(s);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
