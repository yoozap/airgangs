using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the purchased "Cover + Shooting System" import, not just that
    /// the files exist on disk: every AnimationClip under
    /// Assets/ThirdParty/CoverShoot/Animations really is Humanoid motion
    /// (`AnimationClip.isHumanMotion` — the same class of gate
    /// AvatarGateProbe.cs exists for: a clip imported as Generic silently
    /// plays nothing useful through a Humanoid Animator), and every state in
    /// the Resources-loaded CharacterController.controller resolves to a
    /// real Motion with no missing references, recursing into BlendTrees
    /// (Locomotion's directional blend) rather than only checking the
    /// top-level state list.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.CoverShootImportProbe.Run
    ///        -logFile &lt;log&gt;
    /// </summary>
    public static class CoverShootImportProbe
    {
        const string AnimRoot = "Assets/ThirdParty/CoverShoot/Animations";
        const string ControllerPath = "Assets/Resources/ThirdParty/CoverShoot/Animators/CharacterController.controller";

        static bool _allPass = true;

        public static void Run()
        {
            try { Body(); }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"COVERSHOOT_IMPORT id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Debug.Log("COVERSHOOT_IMPORT RESULT=" + (_allPass ? "PASS" : "FAIL"));
        }

        static void Body()
        {
            ClipHumanity();
            ControllerResolves();
            WeaponModels();
        }

        static void ClipHumanity()
        {
            var guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { AnimRoot });
            int n = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) { Fail($"COVERSHOOT_CLIP path={path} error=load_failed"); continue; }
                bool human = clip.isHumanMotion;
                n++;
                Debug.Log($"COVERSHOOT_CLIP name={clip.name} human={Lower(human)}");
                if (!human) Fail($"COVERSHOOT_CLIP name={clip.name} is NOT isHumanMotion");
            }
            Debug.Log($"COVERSHOOT_IMPORT clips_checked={n}");
            if (n == 0) Fail("COVERSHOOT_IMPORT found zero AnimationClips under " + AnimRoot);
        }

        static void ControllerResolves()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null) { Fail("COVERSHOOT_CONTROLLER path=" + ControllerPath + " error=load_failed"); return; }

            int states = 0, missing = 0;
            foreach (var layer in controller.layers)
            {
                var sm = layer.stateMachine;
                if (sm == null) continue;
                foreach (var child in sm.states)
                {
                    states++;
                    var state = child.state;
                    bool ok = CheckMotion(layer.name, state.name, state.motion, ref missing);
                    Debug.Log($"COVERSHOOT_STATE layer={layer.name} state={state.name} ok={Lower(ok)}");
                }
            }
            Debug.Log($"COVERSHOOT_IMPORT controller_layers={controller.layers.Length} states={states} missing={missing}");
            if (states == 0) Fail("COVERSHOOT_CONTROLLER has zero states across all layers");
            if (missing > 0) Fail($"COVERSHOOT_CONTROLLER has {missing} state(s)/blend-tree-children with a null Motion");
        }

        // Returns whether THIS state's own direct motion is resolved; a
        // BlendTree's children are walked and counted into `missing`
        // independently (so one bad child does not hide behind a fine
        // top-level state).
        static bool CheckMotion(string layerName, string stateName, Motion motion, ref int missing)
        {
            if (motion == null)
            {
                missing++;
                return false;
            }
            if (motion is BlendTree bt)
            {
                return CheckBlendTree(layerName, stateName, bt, ref missing);
            }
            return true;
        }

        static bool CheckBlendTree(string layerName, string stateName, BlendTree bt, ref int missing)
        {
            bool allOk = true;
            foreach (var child in bt.children)
            {
                if (child.motion == null)
                {
                    missing++;
                    allOk = false;
                    Debug.Log($"COVERSHOOT_BLENDCHILD layer={layerName} state={stateName} child=<null> ok=false");
                    continue;
                }
                if (child.motion is BlendTree nested)
                {
                    allOk &= CheckBlendTree(layerName, stateName, nested, ref missing);
                }
                else
                {
                    Debug.Log($"COVERSHOOT_BLENDCHILD layer={layerName} state={stateName} child={child.motion.name} ok=true");
                }
            }
            return allOk;
        }

        static void WeaponModels()
        {
            foreach (var id in new[] { "pistol", "rifle", "ak" })
            {
                var go = Resources.Load<GameObject>($"ThirdParty/CoverShoot/Models/Weapons/{id}");
                bool ok = go != null;
                Debug.Log($"COVERSHOOT_MODEL id={id} ok={Lower(ok)}");
                if (!ok) Fail($"COVERSHOOT_MODEL id={id} failed Resources.Load");
            }
            var ctrl = Resources.Load<RuntimeAnimatorController>("ThirdParty/CoverShoot/Animators/CharacterController");
            Debug.Log($"COVERSHOOT_MODEL id=controller_resources_load ok={Lower(ctrl != null)}");
            if (ctrl == null) Fail("COVERSHOOT_MODEL CharacterController failed Resources.Load (CoverShootHero.cs loads it by this exact path)");
        }

        static void Fail(string line)
        {
            _allPass = false;
            Debug.Log("COVERSHOOT_IMPORT FAIL_DETAIL " + line);
        }

        static string Lower(bool b) => b.ToString().ToLowerInvariant();
    }
}
