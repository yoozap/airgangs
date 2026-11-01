// Every purchased clip the hero's graph can play (ParkourClipLibrary.Everything):
// loads, is a Humanoid motion clip, has a length, and one of them retargets
// onto a real veggie avatar with visible bone travel.
//
//   Unity -batchmode -quit -nographics -projectPath unity
//        -executeMethod TbilisiFly.Probes.ParkourClipsProbe.Run
using System.Globalization;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    public static class ParkourClipsProbe
    {
        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        public static void Run()
        {
            int ok = 0, bad = 0;
            foreach (var path in ParkourClipLibrary.Everything)
            {
                var clip = ParkourClipLibrary.Get(path);
                bool good = clip != null && clip.isHumanMotion; // a 0-length clip is a held pose (lying-back, crouch_idle, idle_sneak)
                if (good) ok++; else bad++;
                Debug.Log($"PARKOUR clip={(clip != null ? clip.name : ParkourClipLibrary.Stem(path))} length={(clip != null ? clip.length : 0f).ToString("0.00", CultureInfo.InvariantCulture)} " +
                          $"humanoid={(clip != null && clip.isHumanMotion).ToString().ToLowerInvariant()} loaded={(clip != null).ToString().ToLowerInvariant()} path={path}");
            }
            Debug.Log($"PARKOUR_SUMMARY ok={ok} bad={bad} total={ok + bad}");

            // retarget one clip onto a shipped avatar and read a hand moving
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Art/Characters/apple.fbx");
            var sample = ParkourClipLibrary.Get(ParkourClipLibrary.MantleLowFrontrollSprintToSprint);
            if (prefab != null && sample != null)
            {
                var go = Object.Instantiate(prefab);
                var anim = go.GetComponentInChildren<Animator>();
                var hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null;
                var foot = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
                if (anim != null && hand != null && foot != null)
                {
                    Vector3 h0 = hand.position, f0 = foot.position; float hd = 0f, fd = 0f;
                    for (int i = 0; i <= 20; i++)
                    {
                        sample.SampleAnimation(anim.gameObject, sample.length * i / 20f);
                        hd = Mathf.Max(hd, Vector3.Distance(hand.position, h0));
                        fd = Mathf.Max(fd, Vector3.Distance(foot.position, f0));
                    }
                    Debug.Log($"PARKOUR_RETARGET body=apple clip={sample.name} handDisp={hd.ToString("0.00", CultureInfo.InvariantCulture)} footDisp={fd.ToString("0.000", CultureInfo.InvariantCulture)} nonZero={(hd > 0.05f).ToString().ToLowerInvariant()}");
                }
                else Debug.Log("PARKOUR_RETARGET body=apple error=no_humanoid_bones");
                Object.DestroyImmediate(go);
            }
            // FOOT AXIS under the retarget: rest pose vs the sprint loop at mid-stride
            var sprint = ParkourClipLibrary.Get(ParkourClipLibrary.SprintLoopSmooth);
            if (prefab != null && sprint != null)
            {
                var go = Object.Instantiate(prefab);
                var anim = go.GetComponentInChildren<Animator>();
                Transform fl = null, bl = null, hips = null;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) { if (t.name == "foot_l") fl = t; if (t.name == "ball_l") bl = t; if (t.name == "pelvis") hips = t; }
                if (anim != null && fl != null && bl != null)
                {
                    Vector3 af = anim.transform.forward; Vector3 rf = go.transform.forward;
                    Vector3 toe0 = (bl.position - fl.position); toe0.y = 0f; toe0.Normalize();
                    Debug.Log($"FOOTAXIS stage=rest animNode={anim.transform.name} animLocalEuler={anim.transform.localEulerAngles} animFwd·rootFwd={Vector3.Dot(af, rf):0.00} toe·animFwd={Vector3.Dot(toe0, af):0.00} toe·rootFwd={Vector3.Dot(toe0, rf):0.00} footFwd·animFwd={Vector3.Dot(fl.forward, af):0.00} footUp·animFwd={Vector3.Dot(fl.up, af):0.00} footRight·animFwd={Vector3.Dot(fl.right, af):0.00}");
                    var hd = anim.avatar != null ? anim.avatar.humanDescription : default;
                    foreach (var hb in hd.human) if (hb.humanName == "LeftFoot" || hb.humanName == "LeftToes" || hb.humanName == "Hips") Debug.Log($"FOOTAXIS human={hb.humanName} bone={hb.boneName} useDefault={hb.limit.useDefaultValues}");
                    foreach (var sb in hd.skeleton) if (sb.name == "foot_l" || sb.name == "ball_l" || sb.name == "pelvis") Debug.Log($"FOOTAXIS skel={sb.name} pos={sb.position} rot={sb.rotation.eulerAngles}");
                    for (int i = 0; i < 4; i++)
                    {
                        sprint.SampleAnimation(anim.gameObject, sprint.length * (0.1f + 0.25f * i));
                        Vector3 toe = (bl.position - fl.position); toe.y = 0f; toe.Normalize();
                        Vector3 hf = hips != null ? hips.forward : Vector3.zero;
                        Debug.Log($"FOOTAXIS stage=sprint t={0.1f + 0.25f * i:0.00} toe·animFwd={Vector3.Dot(toe, af):0.00} footFwd·animFwd={Vector3.Dot(fl.forward, af):0.00} footUp·animFwd={Vector3.Dot(fl.up, af):0.00} hipsFwd·animFwd={Vector3.Dot(hf, af):0.00} hipsUp·animFwd={Vector3.Dot(hips != null ? hips.up : Vector3.zero, af):0.00}");
                    }
                }
                Object.DestroyImmediate(go);
            }
            // EXPERIMENTS: rebuild the apple's avatar four ways and read the toe
            // direction over the sprint stride (no asset changed)
            if (prefab != null && sprint != null)
            {
                foreach (var variant in new[] { "asis", "noToes", "flatFoot", "noToesFlatFoot", "bentKnee" })
                {
                    var go = Object.Instantiate(prefab);
                    var anim = go.GetComponentInChildren<Animator>();
                    if (anim == null || anim.avatar == null) { Object.DestroyImmediate(go); continue; }
                    var hd = anim.avatar.humanDescription;
                    var human = new System.Collections.Generic.List<HumanBone>(hd.human);
                    if (variant.Contains("noToes")) human.RemoveAll(h => h.humanName == "LeftToes" || h.humanName == "RightToes");
                    var skel = new System.Collections.Generic.List<SkeletonBone>(hd.skeleton);
                    if (variant.Contains("flatFoot"))
                        for (int i = 0; i < skel.Count; i++)
                            if (skel[i].name == "foot_l" || skel[i].name == "foot_r") { var sb = skel[i]; sb.rotation = sb.rotation * Quaternion.Euler(36f, 0f, 0f); skel[i] = sb; }
                    if (variant == "bentKnee")
                        for (int i = 0; i < skel.Count; i++)
                            if (skel[i].name == "thigh_l" || skel[i].name == "thigh_r") { var sb = skel[i]; sb.rotation = sb.rotation * Quaternion.Euler(-4f, 0f, 0f); skel[i] = sb; }
                            else if (skel[i].name == "calf_l" || skel[i].name == "calf_r") { var sb = skel[i]; sb.rotation = sb.rotation * Quaternion.Euler(8f, 0f, 0f); skel[i] = sb; }
                    var nd = new HumanDescription { human = human.ToArray(), skeleton = skel.ToArray(), upperArmTwist = hd.upperArmTwist, lowerArmTwist = hd.lowerArmTwist, upperLegTwist = hd.upperLegTwist, lowerLegTwist = hd.lowerLegTwist, armStretch = hd.armStretch, legStretch = hd.legStretch, feetSpacing = hd.feetSpacing, hasTranslationDoF = hd.hasTranslationDoF };
                    // the skeleton T-pose must be applied to the transforms before building
                    foreach (var sb in nd.skeleton) { var t = FindDeep(go.transform, sb.name); if (t != null) { t.localPosition = sb.position; t.localRotation = sb.rotation; t.localScale = sb.scale; } }
                    var av = AvatarBuilder.BuildHumanAvatar(go, nd);
                    bool valid = av != null && av.isValid && av.isHuman;
                    anim.avatar = av;
                    Transform fl = FindDeep(go.transform, "foot_l"), bl = FindDeep(go.transform, "ball_l");
                    var sbuf = new System.Text.StringBuilder();
                    for (int i = 0; i < 4 && fl != null && bl != null; i++)
                    {
                        sprint.SampleAnimation(anim.gameObject, sprint.length * (0.1f + 0.25f * i));
                        Vector3 toe = bl.position - fl.position; toe.y = 0f; toe.Normalize();
                        sbuf.Append($" t{i}={Vector3.Dot(toe, anim.transform.forward):0.00}");
                    }
                    Debug.Log($"FOOTEXP variant={variant} valid={valid.ToString().ToLowerInvariant()} toe·fwd:{sbuf}");
                    Object.DestroyImmediate(go);
                }
            }
            foreach (var m in new[] { "shoot_mask", "pistol_hold_mask" })
                Debug.Log($"PARKOUR_MASK name={m} loaded={(ParkourClipLibrary.GetMask(m) != null).ToString().ToLowerInvariant()}");
        }
    }
}
