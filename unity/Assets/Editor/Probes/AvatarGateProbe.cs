using System.Linq;
using System.Reflection;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the gate, not just the maths — see docs/QUALITY_BAR.md's "#1
    /// PRIORITY" entry. ~50 probes in this project build a CharacterRig and
    /// exercise FootPoses/ShoulderPocket's pure functions; none of them assert
    /// that `CharacterRig.IsHumanoid` is actually true on the real shipped
    /// asset, so all of them kept passing while the import setting silently
    /// left the gate shut (every body imported as Generic, animationType: 2).
    ///
    /// This probe loads the REAL FBX for each of the 4 roster bodies (no mocks,
    /// no synthetic Animator), builds a real CharacterRig exactly as
    /// FootController/CharacterSpawner do, and checks the gate is open for
    /// banana/eggplant/toad and CORRECTLY CLOSED for the elephant (it is
    /// deliberately Generic/Procedural — characters.js:514 — and must stay
    /// that way; see HumanizeCharacterRigs.cs's own header).
    ///
    /// Booleans are printed lower-case (`true`/`false`) on purpose —
    /// `bool.ToString()` gives `True` in C#, which breaks line-pairing against
    /// a JS-side probe and has cost real time twice before in this project.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.AvatarGateProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class AvatarGateProbe
    {
        // Every roster body: the 15 fruit/veg humanoids (tools/build_veggies.py,
        // 2026-10-31) plus the elephant, whose gate must stay SHUT.
        static readonly string[] Bodies = System.Array.ConvertAll(TbilisiFly.Character.CharacterRoster.Personages, p => p.Id);

        public static void Run()
        {
            bool allOk = true;
            foreach (var id in Bodies)
            {
                if (!Check(id)) allOk = false;
            }
            ProbeExit.Exit(allOk ? 0 : 1);
        }

        static string Lower(bool b) => b ? "true" : "false";

        // FootPoses.cs is on this port's DO-NOT-EDIT list (FootProbe pins its
        // exact behaviour), so this reaches its private Build()/PoseRig.Valid
        // via reflection instead of widening any access modifier there.
        static bool FootPosesBuildValid(CharacterRig rig)
        {
            var buildMethod = typeof(FootPoses).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static);
            var pr = buildMethod.Invoke(null, new object[] { rig });
            var validField = pr.GetType().GetField("Valid", BindingFlags.Public | BindingFlags.Instance);
            return (bool)validField.GetValue(pr);
        }

        static bool Check(string id)
        {
            string path = $"Assets/Resources/Art/Characters/{id}.fbx";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.Log($"GATE id={id} error=prefab_missing");
                return false;
            }

            bool expectHumanoid = CharacterRoster.IsHumanoidBody(id);
            bool ok = true;

            var instance = Object.Instantiate(prefab);
            CharacterRig rig = null;
            try
            {
                rig = new CharacterRig(instance, id);

                bool isHumanoid = rig.IsHumanoid;
                bool avatarValid = rig.Anim != null && rig.Anim.avatar != null && rig.Anim.avatar.isValid;

                Transform hips = rig.Anim != null ? rig.Anim.GetBoneTransform(HumanBodyBones.Hips) : null;
                Transform rHand = rig.Anim != null ? rig.Anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
                Transform lUpperArm = rig.Anim != null ? rig.Anim.GetBoneTransform(HumanBodyBones.LeftUpperArm) : null;

                bool footPosesValid = false;
                try { footPosesValid = FootPosesBuildValid(rig); }
                catch (System.Exception e) { Debug.Log($"GATE id={id} error=footposes_threw msg={e.Message}"); }

                bool pocketL = false, pocketR = false;
                if (rig.IsHumanoid && rig.Anim != null)
                {
                    try { pocketL = ShoulderPocket.Measure(rig, "L").Valid; } catch { pocketL = false; }
                    try { pocketR = ShoulderPocket.Measure(rig, "R").Valid; } catch { pocketR = false; }
                }

                Debug.Log($"GATE id={id} IsHumanoid={Lower(isHumanoid)} AvatarValid={Lower(avatarValid)} "
                    + $"HipsBone={Lower(hips != null)} RightHandBone={Lower(rHand != null)} LeftUpperArmBone={Lower(lUpperArm != null)} "
                    + $"FootPosesValid={Lower(footPosesValid)} ShoulderPocketL={Lower(pocketL)} ShoulderPocketR={Lower(pocketR)} "
                    + $"Procedural={Lower(rig.Procedural)}");

                if (isHumanoid != expectHumanoid)
                {
                    Debug.Log($"GATE id={id} error=unexpected_humanoid_flag expected={Lower(expectHumanoid)} got={Lower(isHumanoid)}");
                    ok = false;
                }

                if (expectHumanoid)
                {
                    // the whole point of this task: the gate must actually be open.
                    if (!isHumanoid) { Debug.Log($"GATE id={id} error=not_humanoid"); ok = false; }
                    if (!avatarValid) { Debug.Log($"GATE id={id} error=avatar_invalid"); ok = false; }
                    if (hips == null) { Debug.Log($"GATE id={id} error=hips_missing"); ok = false; }
                    if (rHand == null) { Debug.Log($"GATE id={id} error=righthand_missing"); ok = false; }
                    if (lUpperArm == null) { Debug.Log($"GATE id={id} error=leftupperarm_missing"); ok = false; }
                    if (!footPosesValid) { Debug.Log($"GATE id={id} error=footposes_invalid"); ok = false; }
                    if (!pocketL && !pocketR) { Debug.Log($"GATE id={id} error=shoulderpocket_empty_both_sides"); ok = false; }
                }
                else
                {
                    // the elephant — must stay Generic/Procedural, untouched.
                    if (isHumanoid) { Debug.Log($"GATE id={id} error=elephant_must_not_be_humanoid"); ok = false; }
                    if (!rig.Procedural) { Debug.Log($"GATE id={id} error=elephant_must_be_procedural"); ok = false; }
                }
            }
            finally
            {
                if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                else Object.DestroyImmediate(instance);
            }

            return ok;
        }
    }
}
