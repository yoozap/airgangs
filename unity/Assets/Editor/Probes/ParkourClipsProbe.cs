#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using TbilisiFly.Character;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proof (a) for the 2026-11-01 parkour-pack task: every imported clip
    /// actually imported as Humanoid, and at least one of them really moves
    /// bones when sampled onto a real veggie avatar (apple) — not just "the
    /// file exists". `-executeMethod TbilisiFly.Probes.ParkourClipsProbe.Run`.
    /// </summary>
    public static class ParkourClipsProbe
    {
        public static void Run()
        {
            int ok = 0, bad = 0;
            foreach (var path in ParkourClipLibrary.All)
            {
                var clip = ParkourClipLibrary.Get(path);
                if (clip == null)
                {
                    Debug.Log($"PARKOUR path={path} status=MISSING");
                    bad++;
                    continue;
                }
                bool human = clip.isHumanMotion;
                Debug.Log($"PARKOUR clip={clip.name} length={clip.length:0.###} humanoid={human.ToString().ToLowerInvariant()}");
                if (human) ok++; else bad++;
            }
            Debug.Log($"PARKOUR_SUMMARY ok={ok} bad={bad} total={ParkourClipLibrary.All.Length}");

            RetargetOntoApple();
        }

        /// <summary>Loads apple.fbx (a real shipped humanoid body, not a
        /// fixture), builds a real CharacterRig off it exactly as the game
        /// does, samples the roll clip at two times with UnityEditor's own
        /// AnimationMode clip-preview API (the Humanoid-aware one — plain
        /// AnimationClip.SampleAnimation is the legacy/generic path), and
        /// reads the left hand bone's WORLD position both times. A non-zero
        /// displacement is the "it actually retargets and moves bones" proof
        /// the brief asks for.</summary>
        static void RetargetOntoApple()
        {
            const string bodyPath = "Assets/Resources/Art/Characters/apple.fbx";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
            if (prefab == null) { Debug.Log("PARKOUR_RETARGET status=FAIL reason=noApplePrefab"); return; }

            var clip = ParkourClipLibrary.Get(ParkourClipLibrary.MantleLowFrontRoll);
            if (clip == null) { Debug.Log("PARKOUR_RETARGET status=FAIL reason=noClip"); return; }

            GameObject instance = null;
            bool sampling = false;
            try
            {
                instance = Object.Instantiate(prefab);
                var rig = new CharacterRig(instance, "apple");
                if (rig.Anim == null || !rig.IsHumanoid)
                {
                    Debug.Log($"PARKOUR_RETARGET status=FAIL reason=notHumanoid hasAnim={(rig.Anim != null).ToString().ToLowerInvariant()}");
                    return;
                }
                var hand = rig.Anim.GetBoneTransform(HumanBodyBones.LeftHand);
                var foot = rig.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                if (hand == null) { Debug.Log("PARKOUR_RETARGET status=FAIL reason=noLeftHandBone"); return; }

                AnimationMode.StartAnimationMode();
                sampling = true;

                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(rig.Anim.gameObject, clip, 0f);
                AnimationMode.EndSampling();
                Vector3 handAt0 = hand.position;
                Vector3 footAt0 = foot != null ? foot.position : Vector3.zero;

                float tMid = clip.length * 0.5f;
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(rig.Anim.gameObject, clip, tMid);
                AnimationMode.EndSampling();
                Vector3 handAtMid = hand.position;
                Vector3 footAtMid = foot != null ? foot.position : Vector3.zero;

                float handDisp = Vector3.Distance(handAt0, handAtMid);
                float footDisp = foot != null ? Vector3.Distance(footAt0, footAtMid) : -1f;
                Debug.Log($"PARKOUR_RETARGET body=apple clip={clip.name} humanoid={rig.IsHumanoid.ToString().ToLowerInvariant()} " +
                          $"handDisp={handDisp:0.###} footDisp={footDisp:0.###} nonZero={(handDisp > 0.01f).ToString().ToLowerInvariant()}");
            }
            finally
            {
                if (sampling) AnimationMode.StopAnimationMode();
                if (instance != null) Object.DestroyImmediate(instance);
            }
        }
    }
}
#endif
