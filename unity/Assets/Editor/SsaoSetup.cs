using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Adds URP's built-in Screen Space Ambient Occlusion Renderer Feature to
    /// Assets/Settings/TbilisiFly_Renderer.asset — src/game.js:371-395's N8AOPass,
    /// the one post-process left with `m_RendererFeatures: []` (Bloom is already
    /// on DefaultVolumeProfile.asset; AO needs a RENDERER FEATURE, not a Volume
    /// override, because it samples depth/normals before the lit color exists).
    ///
    /// Confirmed live before touching anything: TbilisiFly_Renderer.asset's own
    /// guid (6ff100e938f9c4be2a44209af7dc2168) is Settings/TbilisiFly_URP.asset's
    /// m_RendererDataList[0], and that URP asset's guid
    /// (4980f3e00829a4c2c8587febe861194f) is GraphicsSettings.asset's
    /// m_CustomRenderPipeline AND QualitySettings.asset's customRenderPipeline —
    /// this is not an orphaned asset nobody renders with.
    ///
    /// TYPED API ONLY, no hand-edited YAML enum ints (two earlier agents
    /// declined to guess at this asset's YAML and skipped it — seeing exactly
    /// how it is built):
    ///   - the FEATURE OBJECT and its slot in the list: ScriptableObject.
    ///     CreateInstance&lt;ScreenSpaceAmbientOcclusion&gt;() (a PUBLIC class) +
    ///     AssetDatabase.AddObjectToAsset() + the renderer data's own PUBLIC
    ///     `rendererFeatures` list (ScriptableRendererData.cs:50) — no YAML.
    ///   - its TUNING FIELDS: ScreenSpaceAmbientOcclusionSettings's own fields
    ///     (Intensity/Radius/Downsample/...) are `internal` to the URP runtime
    ///     assembly, unreachable by ordinary C# field access from here — but
    ///     SerializedObject/SerializedProperty read and write by SERIALIZED
    ///     PROPERTY PATH, which does not care about C# access modifiers; this
    ///     is the exact mechanism Unity's own Inspector uses, so it is typed,
    ///     validated (SerializedProperty throws on the wrong Type/path) and
    ///     logged here, never a blind byte/enum-int poke.
    ///
    /// VALUES: the algorithm is NOT the same one (N8AO is a from-scratch
    /// screen-space pass the composer runs after the main render; URP's SSAO
    /// is a renderer-feature prepass that modulates lit opaque color before
    /// transparents) — there is no byte-for-byte parameter equivalence to port.
    /// What DOES carry straight over is every constant that is genuinely the
    /// same quantity in both: a WORLD-SPACE radius in metres, an intensity
    /// multiplier, and "halve the working resolution for speed". Falloff
    /// (URP) and distanceFalloff (N8AO) are not the same unit (URP's is a
    /// metres cutoff default 100; N8AO's is a normalised curve exponent) so it
    /// is left at URP's own default rather than forcing a wrong-unit number
    /// across.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///     -executeMethod TbilisiFly.EditorTools.SsaoSetup.Run
    /// </summary>
    public static class SsaoSetup
    {
        public const string RendererPath = "Assets/Settings/TbilisiFly_Renderer.asset";

        public static void Run()
        {
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rendererData == null)
            {
                Debug.LogError($"SSAOSETUP missing renderer data at {RendererPath}");
                EditorApplication.Exit(1);
                return;
            }

            ScreenSpaceAmbientOcclusion feature;
            bool created = !rendererData.TryGetRendererFeature(out feature);
            if (created)
            {
                feature = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                feature.name = "ScreenSpaceAmbientOcclusion";
                feature.hideFlags = HideFlags.HideInHierarchy; // matches MigrateRendererFeatureHideFlags's own convention
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                rendererData.rendererFeatures.Add(feature);

                // Pad the feature-id map so its count matches the feature list —
                // ScriptableRendererData.ValidateRendererFeatures() (internal,
                // Editor-only self-heal) only ever reads this to re-link a
                // feature that went NULL; a 0 entry here is exactly what it
                // already treats as "unmapped, fall back to GetUnusedAsset()",
                // the same state a freshly-added feature is in regardless.
                var rdSo = new SerializedObject(rendererData);
                var map = rdSo.FindProperty("m_RendererFeatureMap");
                map.InsertArrayElementAtIndex(map.arraySize);
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = 0;
                rdSo.ApplyModifiedPropertiesWithoutUndo();
            }

            var so = new SerializedObject(feature);
            void Set(string path, System.Action<SerializedProperty> set)
            {
                var p = so.FindProperty(path);
                if (p == null) { Debug.LogWarning($"SSAOSETUP: no property '{path}' — URP package shape differs from expected"); return; }
                set(p);
            }
            // game.js:376 `ao.configuration.aoRadius = 4` — same quantity, metres.
            Set("m_Settings.Radius", p => p.floatValue = 4f);
            // game.js:378 `ao.configuration.intensity = 2.2`
            Set("m_Settings.Intensity", p => p.floatValue = 2.2f);
            // game.js:379 `ao.configuration.halfRes = true`
            Set("m_Settings.Downsample", p => p.boolValue = true);
            // Depth+normals source — N8AO also reconstructs normals from depth
            // rather than depth alone, for the same "creases, not slabs" reason
            // the 4 m radius comment gives.
            // ScreenSpaceAmbientOcclusionSettings is itself `internal`, so the
            // enum name can't be referenced from here — DepthSource.DepthNormals
            // is declared as index 1 (Depth = 0, DepthNormals = 1).
            Set("m_Settings.Source", p => p.enumValueIndex = 1);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(feature);
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(RendererPath, ImportAssetOptions.ForceUpdate);

            bool stillThere = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath)
                .TryGetRendererFeature<ScreenSpaceAmbientOcclusion>(out var readBack);
            Debug.Log($"SSAOSETUP created={created} feature_present_after_reimport={stillThere} "
                + $"intensity={(readBack != null ? new SerializedObject(readBack).FindProperty("m_Settings.Intensity").floatValue : -1f)} "
                + $"radius={(readBack != null ? new SerializedObject(readBack).FindProperty("m_Settings.Radius").floatValue : -1f)} "
                + $"downsample={(readBack != null && new SerializedObject(readBack).FindProperty("m_Settings.Downsample").boolValue ? 1 : 0)}");
        }
    }
}
