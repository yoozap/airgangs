using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Fixes docs/QUALITY_BAR.md's "#1 PRIORITY" gap: every model .meta in this
    /// project imports as Generic (animationType: 2), so
    /// `CharacterRig.IsHumanoid` (`Anim.avatar.isHuman`) is false for every
    /// body, gating off torso twist, FootPoses, ShoulderPocket, seat/boarding
    /// poses and arm bones — all written and probe-verified, none of it reached.
    ///
    /// Only banana/eggplant/toad (characters.js's three humanoid bodies —
    /// CharacterRoster.IsHumanoidBody) are touched here. The elephant is
    /// deliberately Generic/Procedural (characters.js:514) and this script
    /// never names it.
    ///
    /// Approach per SKILL.md:
    ///   1. Print the body's real bone names (a deliverable in its own right —
    ///      these are a banana/aubergine/toad rig with non-standard names, not
    ///      a Mixamo rig, so nothing here should be assumed).
    ///   2. Try `ModelImporter.animationType = Human` +
    ///      `avatarSetup = CreateFromThisModel` and SaveAndReimport() — Unity's
    ///      own auto-mapper. Read back `humanDescription.human.Length` and the
    ///      resulting Avatar's `isHuman`/`isValid` and judge by THAT, not by
    ///      assumption.
    ///   3. If auto-mapping left any of the 15 bones `AvatarBuilder` requires
    ///      unmapped, build `HumanDescription.human`/`.skeleton` explicitly from
    ///      the inspected bone names (manual mapping below) and reimport again.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.EditorTools.HumanizeCharacterRigs.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class HumanizeCharacterRigs
    {
        // characters.js RACES[].bodies humanoid set == CharacterRoster.IsHumanoidBody.
        // The elephant is NOT in this list on purpose — do not add it.
        static readonly string[] Bodies = { "apple", "banana", "broccoli", "cherry", "cucumber", "kiwi", "lettuce", "onion", "orange", "pear", "plum", "potato", "red_pepper", "tomato", "strawberry" }; // the fruit/veg race, tools/build_veggies.py (2026-10-31)

        // Manual fallback mapping, built by inspecting the actual GLB/FBX node
        // names (see the BONES log line this script prints first): a 22-bone
        // UE-Mannequin-style skeleton (pelvis/spine_01-03/clavicle/upperarm/
        // lowerarm/hand/thigh/calf/foot/ball, left/right as "_l"/"_r" suffixes,
        // plus "neck_01" and "Head"). All 15 bones AvatarBuilder requires plus
        // shoulders/chest/upperChest/toes are present; no finger bones exist in
        // this rig (FootPoses.cs gates finger curling on their absence already).
        static readonly (string bone, HumanBodyBones human)[] Mapping =
        {
            ("pelvis",     HumanBodyBones.Hips),
            ("spine_01",   HumanBodyBones.Spine),
            ("spine_02",   HumanBodyBones.Chest),
            ("spine_03",   HumanBodyBones.UpperChest),
            ("neck_01",    HumanBodyBones.Neck),
            ("Head",       HumanBodyBones.Head),

            ("clavicle_l", HumanBodyBones.LeftShoulder),
            ("upperarm_l", HumanBodyBones.LeftUpperArm),
            ("lowerarm_l", HumanBodyBones.LeftLowerArm),
            ("hand_l",     HumanBodyBones.LeftHand),

            ("clavicle_r", HumanBodyBones.RightShoulder),
            ("upperarm_r", HumanBodyBones.RightUpperArm),
            ("lowerarm_r", HumanBodyBones.RightLowerArm),
            ("hand_r",     HumanBodyBones.RightHand),

            ("thigh_l",    HumanBodyBones.LeftUpperLeg),
            ("calf_l",     HumanBodyBones.LeftLowerLeg),
            ("foot_l",     HumanBodyBones.LeftFoot),
            ("ball_l",     HumanBodyBones.LeftToes),

            ("thigh_r",    HumanBodyBones.RightUpperLeg),
            ("calf_r",     HumanBodyBones.RightLowerLeg),
            ("foot_r",     HumanBodyBones.RightFoot),
            ("ball_r",     HumanBodyBones.RightToes),
        };

        public static void Run()
        {
            bool allOk = true;
            foreach (var body in Bodies)
            {
                if (!Humanize(body)) allOk = false;
            }
            EditorApplication.Exit(allOk ? 0 : 1);
        }

        static bool Humanize(string body)
        {
            string path = $"Assets/Resources/Art/Characters/{body}.fbx";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.Log($"HUMANIZE id={body} error=importer_missing");
                return false;
            }

            // --- 1. print the real bone names before mapping anything ---
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.Log($"HUMANIZE id={body} error=prefab_missing");
                return false;
            }
            var probeInst = Object.Instantiate(prefab);
            string[] boneNames;
            try
            {
                boneNames = probeInst.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.name)
                    .ToArray();
            }
            finally { Object.DestroyImmediate(probeInst); }
            Debug.Log($"BONES id={body} count={boneNames.Length} names={string.Join(",", boneNames)}");

            // --- 2. try Unity's own auto-mapper first ---
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();

            int autoCount = importer.humanDescription.human?.Length ?? 0;
            var autoAvatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            bool autoValid = autoAvatar != null && autoAvatar.isValid;
            bool autoIsHuman = autoAvatar != null && autoAvatar.isHuman;
            Debug.Log($"AUTOMAP id={body} humanBones={autoCount} avatarValid={Lower(autoValid)} avatarIsHuman={Lower(autoIsHuman)}");

            if (autoIsHuman && autoValid && autoCount >= 15)
            {
                Debug.Log($"HUMANIZE id={body} method=auto result=ok");
                return true;
            }

            // --- 3. auto-map failed or incomplete: build the mapping ourselves ---
            return ManualMap(body, path, importer, boneNames);
        }

        static bool ManualMap(string body, string path, ModelImporter importer, string[] boneNames)
        {
            var boneSet = new HashSet<string>(boneNames);
            var missing = Mapping.Where(m => !boneSet.Contains(m.bone)).Select(m => m.bone).ToArray();
            if (missing.Length > 0)
            {
                Debug.Log($"HUMANIZE id={body} method=manual error=missing_bones bones={string.Join(",", missing)}");
                return false;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var inst = Object.Instantiate(prefab);
            try
            {
                var allT = inst.GetComponentsInChildren<Transform>(true);

                var human = new List<HumanBone>(Mapping.Length);
                foreach (var (boneName, hb) in Mapping)
                {
                    human.Add(new HumanBone
                    {
                        boneName = boneName,
                        humanName = HumanTrait.BoneName[(int)hb],
                        limit = new HumanLimit { useDefaultValues = true },
                    });
                }

                var skeleton = new List<SkeletonBone>(allT.Length);
                foreach (var t in allT)
                {
                    skeleton.Add(new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale,
                    });
                }

                var hd = new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton.ToArray(),
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false,
                };

                importer.animationType = ModelImporterAnimationType.Human;
                importer.humanDescription = hd;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.SaveAndReimport();

                var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
                bool valid = avatar != null && avatar.isValid;
                bool isHuman = avatar != null && avatar.isHuman;
                Debug.Log($"HUMANIZE id={body} method=manual humanBones={hd.human.Length} avatarValid={Lower(valid)} avatarIsHuman={Lower(isHuman)}");
                return valid && isHuman;
            }
            finally
            {
                Object.DestroyImmediate(inst);
            }
        }

        static string Lower(bool b) => b ? "true" : "false";
    }
}
