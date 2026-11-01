using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// ONE-SHOT: moves the art under a Resources root so a BUILT PLAYER can
    /// load it. See Scripts/Core/ArtAssets.cs for why Resources and not
    /// Addressables.
    ///
    /// AssetDatabase.MoveAsset and not `mv`: a move through the database keeps
    /// every GUID and every imported Library artifact, so the 196 Read/Write
    /// model importers set for mesh refinement, the plane livery material
    /// assignments and all the scene/material references survive untouched. A
    /// shell move would also keep the GUIDs (the .meta travels with the file)
    /// but costs a full reimport of 459 MB while other agents wait on the
    /// project lock.
    ///
    /// Idempotent: a folder already at the destination is reported as moved=no
    /// rather than failing.
    ///
    ///     Unity -batchmode -quit -nographics -projectPath unity \
    ///           -executeMethod TbilisiFly.EditorTools.MoveArtToResources.Run
    /// </summary>
    public static class MoveArtToResources
    {
        static readonly (string from, string to)[] Moves =
        {
            ("Assets/Art", "Assets/Resources/Art"),
            ("Assets/Audio", "Assets/Resources/Audio"),
            ("Assets/UI", "Assets/Resources/UI"),
            ("Assets/DefaultVolumeProfile.asset", "Assets/Resources/DefaultVolumeProfile.asset"),
        };

        public static void Run()
        {
            int moved = 0, already = 0, failed = 0;
            var errors = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (from, to) in Moves)
                {
                    bool srcIsFolder = AssetDatabase.IsValidFolder(from);
                    bool srcExists = srcIsFolder || AssetDatabase.LoadAssetAtPath<Object>(from) != null;
                    bool dstIsFolder = AssetDatabase.IsValidFolder(to);
                    bool dstExists = dstIsFolder || AssetDatabase.LoadAssetAtPath<Object>(to) != null;
                    if (!srcExists && dstExists) { already++; Debug.Log($"ARTMOVE from={from} to={to} moved=no reason=already-there"); continue; }
                    if (!srcExists) { failed++; errors.Add($"{from}: missing"); Debug.LogError($"ARTMOVE from={from} to={to} moved=no reason=source-missing"); continue; }
                    string err = AssetDatabase.MoveAsset(from, to);
                    if (string.IsNullOrEmpty(err)) { moved++; Debug.Log($"ARTMOVE from={from} to={to} moved=yes"); }
                    else { failed++; errors.Add($"{from}: {err}"); Debug.LogError($"ARTMOVE from={from} to={to} moved=no reason={err}"); }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            Debug.Log($"ARTMOVE done moved={moved} already={already} failed={failed}");
            if (failed > 0) EditorApplication.Exit(1);
        }
    }
}
