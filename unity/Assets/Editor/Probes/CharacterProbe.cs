using System.Linq;
using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves CharacterRig/CharacterRoster reproduce the 4-body roster's measured
    /// facts: bone count, whether Unity mapped a Humanoid Avatar, clip count, and
    /// standing height after CharacterRig's own normalisation (must land on
    /// CharacterRoster.HeroHeight = 1.80 m for all four). Also dumps the WALK_SET
    /// table so its speeds can be diffed against moves.json.
    ///
    /// tools/_probe_character.mjs prints the matching roster/WALK_SET facts from
    /// src/characters.js + moves.json directly (it does not load a GLB, so there
    /// is no JS-side BODY line to diff against — see that script's header);
    /// diff the WALK lines with tools/unity_port_check.py.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CharacterProbe.Run
    /// </summary>
    public static class CharacterProbe
    {
        static readonly string[] Bodies = { "banana", "eggplant", "toad", "elephant" };

        public static void Run()
        {
            foreach (var id in Bodies)
            {
                string path = $"Assets/Resources/Art/Characters/{id}.fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    Debug.Log($"BODY id={id} missing=1");
                    continue;
                }

                var all = AssetDatabase.LoadAllAssetsAtPath(path);
                int clipCount = all.OfType<AnimationClip>().Count(c => !c.name.StartsWith("__preview__"));

                var instance = Object.Instantiate(prefab);
                var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>();
                int bones = smr != null ? smr.bones.Length : 0;

                CharacterRig rig = null;
                try
                {
                    rig = new CharacterRig(instance, id);
                    Debug.Log(
                        $"BODY id={id} bones={bones} humanoid={(rig.IsHumanoid ? 1 : 0)} " +
                        $"procedural={(rig.Procedural ? 1 : 0)} clips={clipCount} " +
                        $"heightM={rig.BodyHeight:F4} aspect={rig.Aspect:F4}");

                    var legs = rig.LegPair();
                    Debug.Log($"RIG id={id} legPair={(legs.HasValue ? 1 : 0)} " +
                              $"animIsHuman={(rig.Anim != null && rig.Anim.avatar != null ? (rig.Anim.avatar.isHuman ? 1 : 0) : -1)}");
                }
                finally
                {
                    if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                    else Object.DestroyImmediate(instance);
                }
            }

            foreach (var w in CharacterRoster.WalkSet)
            {
                Debug.Log($"WALK id={w.Name} angleDeg={w.AngleDeg:F2} moveLat={w.MoveLateral:F4} " +
                          $"moveFwd={w.MoveForward:F4} dur={w.Dur:F4} speedLat={w.SpeedLateral:F5} " +
                          $"speedFwd={w.SpeedForward:F5}");
            }

            Debug.Log($"CONST id=_const heroHeight={CharacterRoster.HeroHeight:F3} " +
                      $"walkClipSpeed={CharacterRoster.WalkClipSpeed:F5} walkMaxRate={CharacterRoster.WalkMaxRate:F3}");

            foreach (var race in CharacterRoster.Races)
            {
                Debug.Log($"RACE id={race.Id} roleKa={race.RoleKa} runSpeed={race.RunSpeed:F3} " +
                          $"bodies={string.Join(",", race.Bodies)} defModel={race.DefModel}");
            }

            ProbeExit.Exit(0);
        }
    }
}
