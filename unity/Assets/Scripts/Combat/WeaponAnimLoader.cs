using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// The asset bridge GunAnim.cs/RifleHolds.cs were left without — see
    /// Core/HeldWeapon.cs's own former "SCOPE CUT" header: both need "a second,
    /// separate arms-skeleton GLB (pistol_anim.glb/akm_arms.glb) loaded and
    /// retargeted onto the body". That skeleton (and the two weapons' own
    /// parts clips, beretta_anim.glb/akm_anim.glb) are ALREADY sitting in this
    /// project, imported as generic-rig FBX next to the 3 humanoid bodies
    /// (unity/Assets/Resources/Art/Props/{pistol_anim,akm_arms,beretta_anim,akm_anim}.fbx
    /// — tools/glb_to_fbx.py's own convention, already run for these four by
    /// an earlier pass) but nothing referenced them: no script anywhere in
    /// this project reads WeaponDef.Url at all (confirmed by grep — Guns.cs/
    /// Pickups.cs's "load a weapon's own model" step, src/weapons.js:152
    /// loadWeapon, has no Unity port yet and is explicitly those files' scope,
    /// not this one's). This class is the missing link for exactly the two
    /// clips GunAnim/RifleHolds need, nothing more.
    ///
    /// PLAYER-SAFE through Core/ArtAssets, same precedent as Core/AudioFx.cs's
    /// FxAudio.LoadAllBanks and Core/CombatWorldAdapter.cs's own
    /// Settings/TargetCard/Credits UXML loads: AssetDatabase has no runtime
    /// equivalent without a Resources/Addressables pass (a production
    /// decision for later), but this project only ever runs inside the Editor
    /// today, so this is what makes the clips "just work" for in-Editor Play
    /// testing right now rather than staying a second silently-dead system.
    /// </summary>
    public static class WeaponAnimLoader
    {
        const string PropsDir = "Assets/Resources/Art/Props";

        // "models/pistol_anim.glb" -> "Assets/Resources/Art/Props/pistol_anim.fbx"
        static string FbxPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int slash = url.LastIndexOf('/');
            string name = slash >= 0 ? url.Substring(slash + 1) : url;
            if (name.EndsWith(".glb")) name = name.Substring(0, name.Length - 4) + ".fbx";
            return $"{PropsDir}/{name}";
        }

        static readonly Dictionary<string, AnimationClip> _clipCache = new Dictionary<string, AnimationClip>();
        static readonly Dictionary<string, GameObject> _sourceCache = new Dictionary<string, GameObject>();

        static AnimationClip LoadClip(string fbxPath)
        {
            if (fbxPath == null) return null;
            if (_clipCache.TryGetValue(fbxPath, out var cached)) return cached;
            AnimationClip found = null;
            foreach (var o in ArtAssets.LoadAllAt(fbxPath))
            {
                if (o is AnimationClip ac && !ac.name.StartsWith("__preview__")) { found = ac; break; }
            }
            if (found == null) Debug.LogWarning($"WeaponAnimLoader: no AnimationClip in {fbxPath} (not imported yet?)");
            _clipCache[fbxPath] = found;
            return found;
        }

        static GameObject LoadSourceRoot(string fbxPath)
        {
            if (fbxPath == null) return null;
            if (_sourceCache.TryGetValue(fbxPath, out var cached)) return cached;
            var go = ArtAssets.Load<GameObject>(fbxPath);
            if (go == null) Debug.LogWarning($"WeaponAnimLoader: {fbxPath} not found (not imported yet?)");
            _sourceCache[fbxPath] = go;
            return go;
        }

        /// <summary>One per GunAnimSpec (pistol/rifle), shared for the lifetime
        /// of the process — src/game.js:201 `this.gunAnims = {pistol:..,
        /// rifle:..}` is built once at startup the same way. Instantiates a
        /// private, hidden, non-rendered copy of the arms reference skeleton
        /// (pistol_anim.fbx/akm_arms.fbx) and samples it the way GunAnim.cs's
        /// own header documents. If the FBX isn't imported/has no clip, the
        /// returned GunAnim simply stays `Ready == false` — every caller in
        /// this port already treats that as the JS's own degraded fallback
        /// (Shooting.cs's `req.ClipShotAvailable = ga?.Ready ?? false`), not
        /// an exception.</summary>
        public static GunAnim CreateArmsAnim(GunAnimSpec spec)
        {
            var anim = new GunAnim(spec);
            string path = FbxPath(spec.Url);
            var src = LoadSourceRoot(path);
            var clip = LoadClip(path);
            if (src == null || clip == null) return anim;
            var inst = Object.Instantiate(src);
            inst.name = $"GunAnimRig_{spec.Name}";
            inst.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            // DontDestroyOnLoad THROWS outside play mode ("can only be used in
            // play mode and, as such, cannot be part of an editor script"), and an
            // editor probe or a scene-build step runs exactly there. It surfaced as
            // PlayableProbe's tick300NoLoggedError failing with errors=2 — one per
            // arms rig — on an otherwise fully-passing boot. The object already
            // carries HideFlags.DontSave, so skipping the call out of play mode
            // costs nothing: nothing is going to unload a scene mid-probe.
            if (Application.isPlaying) Object.DontDestroyOnLoad(inst);
            // THE PLAYER'S RED CONSOLE (user screenshot 2026-10-31 21:30): six
            // "Non-Legacy animations cannot be sampled outside the Editor
            // without an Animator" errors, one per cut sample below, and then
            // every per-frame GunAnim.SampleAnimation silently did nothing —
            // the weapon animations existed only in the editor. The editor
            // samples any clip; a built player samples only LEGACY clips (the
            // Animation-component kind). The JS build has no such split
            // (AnimationMixer samples any clip), so the parity move is a
            // runtime copy of the imported clip flagged legacy — the curves,
            // paths and timings are byte-identical, only the sampler changes.
            if (!Application.isEditor && !clip.legacy)
            {
                clip = Object.Instantiate(clip);
                clip.legacy = true;
            }
            anim.Initialize(inst, clip);
            return anim;
        }

        /// <summary>src/weapons.js:163 `obj.userData.anim = new WeaponAnim(obj,
        /// gltf.animations[0], GUN_CLIP_SETS[def.anim] ?? GUN_CLIP_SETS.pistol)`
        /// — attached in the JS the moment the weapon's own model finishes
        /// loading. There is no loadWeapon() in this build yet (see class
        /// header), so this does it lazily the first time ANY caller asks
        /// about a given carried-weapon instance — same end state (one
        /// WeaponAnim per live weapon GameObject, attached before its first
        /// use), just triggered from first use instead of from load. Returns
        /// null (cached) forever for a weapon with no clip (makarov: no
        /// `_anim.glb` counterpart at all) — exactly the JS's own
        /// `gltf.animations?.length` guard.</summary>
        static readonly ConditionalWeakTable<Transform, WeaponAnim> _gunParts = new ConditionalWeakTable<Transform, WeaponAnim>();

        public static WeaponAnim GunPartsFor(Transform obj, WeaponDef def)
        {
            if (obj == null || def == null) return null;
            if (_gunParts.TryGetValue(obj, out var existing)) return existing;
            WeaponAnim made = null;
            string path = FbxPath(def.Url);
            var clip = LoadClip(path);
            if (clip != null)
            {
                var table = (def.Anim != null && GunClips.GunClipSets.TryGetValue(def.Anim, out var t))
                    ? t : GunClips.GunClipSets["pistol"];
                made = new WeaponAnim(obj.gameObject, clip, table);
            }
            _gunParts.Add(obj, made);
            return made;
        }
    }
}
