using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TbilisiFly.Core;
using TbilisiFly.Data;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verification for Core/SceneEnvironmentSetup.cs — src/game.js:242-339
    /// `_setupScene()`'s renderer/camera/sky/sun/hemisphere/fog half.
    ///
    /// Builds a throwaway scene (one Camera + one empty GameObject carrying
    /// SceneEnvironmentSetup), runs its Awake()/Setup() for real, then reads
    /// the RESOLVED values back off: the actual Camera component, the actual
    /// Light component, RenderSettings (ambient/fog), the actual
    /// Assets/Settings/TbilisiFly_URP.asset (via GraphicsSettings.
    /// currentRenderPipeline), and the actual Assets/Resources/DefaultVolumeProfile.asset
    /// (via VolumeProfile.TryGet) — not the constants this file's own code
    /// typed, per this port's own "a probe proves a calculation is REACHED"
    /// rule.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///     -executeMethod TbilisiFly.Probes.SceneEnvironmentProbe.Run
    /// </summary>
    public static class SceneEnvironmentProbe
    {
        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                // the note explains what a FAILURE means, so it prints only on
                // one (see Editor/Probes/UiWireProbe.cs's own Check).
                string line = $"{(cond ? "PASS" : "FAIL")} {key}{(!cond && note != null ? $" ({note})" : "")}";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"{key}={value}");

            var camGo = new GameObject("probe_cam");
            var cam = camGo.AddComponent<Camera>();
            var envGo = new GameObject("probe_env");
            var env = envGo.AddComponent<SceneEnvironmentSetup>();
            env.Cam = cam;
            // Awake() already ran via AddComponent; call Setup() again
            // explicitly so this probe does not depend on Unity's Awake
            // timing inside -executeMethod (no domain reload happens here).
            env.Setup();

            // ---- camera: game.js:279-284 ----
            Fact("CAM_FOV", cam.fieldOfView);
            Fact("CAM_NEAR", cam.nearClipPlane);
            Fact("CAM_FAR", cam.farClipPlane);
            Check("cam_fov", Mathf.Approximately(cam.fieldOfView, SceneEnvironmentSetup.CameraFovDeg));
            Check("cam_near", Mathf.Approximately(cam.nearClipPlane, SceneEnvironmentSetup.CameraNear));
            Check("cam_far", Mathf.Approximately(cam.farClipPlane, SceneEnvironmentSetup.CameraFar));

            // ---- sun light: game.js:315-337 ----
            var sun = env.Sun;
            Fact("SUN_COLOR", sun != null ? ColorUtility.ToHtmlStringRGB(sun.color) : "null");
            Fact("SUN_INTENSITY", sun != null ? sun.intensity : -1);
            Fact("SUN_SHADOWS", sun != null ? sun.shadows.ToString() : "null");
            Fact("SUN_SHADOW_STRENGTH", sun != null ? sun.shadowStrength : -1);
            Fact("SUN_FORWARD", sun != null ? sun.transform.forward.ToString("F4") : "null");
            Check("sun_color", sun != null && ColorUtility.ToHtmlStringRGB(sun.color) == "FFF0DC");
            Check("sun_intensity", sun != null && Mathf.Approximately(sun.intensity, SceneEnvironmentSetup.SunIntensity));
            Check("sun_soft_shadows", sun != null && sun.shadows == LightShadows.Soft);
            Check("sun_shadow_strength", sun != null && Mathf.Approximately(sun.shadowStrength, SceneEnvironmentSetup.SunShadowStrength));
            Check("sun_is_render_settings_sun", RenderSettings.sun == sun);

            // Independent re-derivation of the sun direction (game-frame, then
            // mirrored through GeoFrame.ToUnity) to prove the Light's rotation
            // actually faces the opposite way, not just that SOME rotation got set.
            Vector3 dirGame = SceneEnvironmentSetup.SunDirGame(SceneEnvironmentSetup.SunElevationDeg, SceneEnvironmentSetup.SunAzimuthDeg);
            Vector3 dirUnity = GeoFrame.ToUnity(dirGame.x, dirGame.y, dirGame.z);
            Fact("SUN_DIR_GAME", dirGame.ToString("F4"));
            Fact("SUN_DIR_UNITY", dirUnity.ToString("F4"));
            float dot = sun != null ? Vector3.Dot(sun.transform.forward, -dirUnity.normalized) : -2f;
            Fact("SUN_FORWARD_DOT_EXPECTED", dot);
            Check("sun_direction", dot > 0.9999f, $"dot={dot}");

            // ---- ambient/hemisphere: game.js:339 ----
            Fact("AMBIENT_MODE", RenderSettings.ambientMode);
            Fact("AMBIENT_SKY", ColorUtility.ToHtmlStringRGB(RenderSettings.ambientSkyColor));
            Fact("AMBIENT_GROUND", ColorUtility.ToHtmlStringRGB(RenderSettings.ambientGroundColor));
            Fact("AMBIENT_EQUATOR", ColorUtility.ToHtmlStringRGB(RenderSettings.ambientEquatorColor));
            Fact("AMBIENT_INTENSITY", RenderSettings.ambientIntensity);
            Check("ambient_mode", RenderSettings.ambientMode == AmbientMode.Trilight);
            Check("ambient_sky", ColorUtility.ToHtmlStringRGB(RenderSettings.ambientSkyColor) == "BDD8FF");
            Check("ambient_ground", ColorUtility.ToHtmlStringRGB(RenderSettings.ambientGroundColor) == "77664E");
            Check("ambient_intensity", Mathf.Approximately(RenderSettings.ambientIntensity, SceneEnvironmentSetup.HemiIntensity));

            // ---- fog: game.js:277 ----
            Fact("FOG_ENABLED", RenderSettings.fog);
            Fact("FOG_MODE", RenderSettings.fogMode);
            Fact("FOG_COLOR", ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor));
            Fact("FOG_DENSITY", RenderSettings.fogDensity);
            Check("fog_enabled", RenderSettings.fog);
            Check("fog_mode", RenderSettings.fogMode == FogMode.ExponentialSquared);
            Check("fog_color", ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor) == "AEC4D8");
            Check("fog_density", Mathf.Approximately(RenderSettings.fogDensity, SceneEnvironmentSetup.FogDensity));

            // ---- skybox: game.js:287-298 ----
            Fact("SKYBOX_SHADER", RenderSettings.skybox != null ? RenderSettings.skybox.shader.name : "null");
            Check("skybox_assigned", RenderSettings.skybox != null);

            // ---- URP pipeline asset — Assets/Settings/TbilisiFly_URP.asset,
            // read through GraphicsSettings.currentRenderPipeline, i.e. the
            // ASSET ACTUALLY WIRED INTO THE PROJECT, not a loaded copy. ----
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Debug.LogWarning("SceneEnvironmentProbe: GraphicsSettings.currentRenderPipeline is not a UniversalRenderPipelineAsset (or is null) — falling back to AssetDatabase load");
                urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/TbilisiFly_URP.asset");
            }
            Fact("URP_SHADOW_DISTANCE", urp != null ? urp.shadowDistance : -1);
            Fact("URP_MAIN_SHADOW_RES", urp != null ? urp.mainLightShadowmapResolution : -1);
            Fact("URP_SOFT_SHADOWS", urp != null ? urp.supportsSoftShadows : (bool?)null);
            Fact("URP_COLOR_GRADING_MODE", urp != null ? urp.colorGradingMode.ToString() : "null");
            Fact("URP_SUPPORTS_HDR", urp != null ? urp.supportsHDR : (bool?)null);
            Check("urp_shadow_distance", urp != null && Mathf.Approximately(urp.shadowDistance, SceneEnvironmentSetup.ShadowNearM));
            Check("urp_main_shadow_res", urp != null && urp.mainLightShadowmapResolution == 2048);
            Check("urp_soft_shadows", urp != null && urp.supportsSoftShadows);
            Check("urp_color_grading_hdr", urp != null && urp.colorGradingMode == ColorGradingMode.HighDynamicRange);

            // ---- Volume profile — Assets/Resources/DefaultVolumeProfile.asset, read
            // through its REAL TryGet<Tonemapping/ColorAdjustments>, not a
            // hand-parsed YAML string. ----
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Resources/DefaultVolumeProfile.asset");
            if (profile != null && profile.TryGet(out Tonemapping tm))
            {
                Fact("VOL_TONEMAP_MODE", tm.mode.value);
                Fact("VOL_TONEMAP_ACTIVE", tm.active);
                Check("vol_tonemap_aces", tm.mode.value == TonemappingMode.ACES);
            }
            else
            {
                Check("vol_tonemap_aces", false, "Tonemapping override missing from DefaultVolumeProfile.asset");
            }
            if (profile != null && profile.TryGet(out ColorAdjustments ca))
            {
                Fact("VOL_POST_EXPOSURE", ca.postExposure.value);
                double expected = Math.Log(0.95, 2); // three.js renderer.toneMappingExposure = 0.95, converted to EV stops
                Check("vol_post_exposure", Math.Abs(ca.postExposure.value - expected) < 0.0005, $"expected={expected}");
            }
            else
            {
                Check("vol_post_exposure", false, "ColorAdjustments override missing from DefaultVolumeProfile.asset");
            }
            if (profile != null && profile.TryGet(out Bloom bloom))
            {
                Fact("VOL_BLOOM_ACTIVE", bloom.active);
                Fact("VOL_BLOOM_THRESHOLD", bloom.threshold.value);
                Fact("VOL_BLOOM_INTENSITY", bloom.intensity.value);
                Fact("VOL_BLOOM_SCATTER", bloom.scatter.value);
                Fact("VOL_BLOOM_DOWNSCALE", bloom.downscale.value);
                Fact("VOL_BLOOM_HQ_FILTER", bloom.highQualityFiltering.value);
                Check("vol_bloom_threshold", Mathf.Approximately(bloom.threshold.value, SceneEnvironmentSetup.BloomThreshold));
                Check("vol_bloom_intensity", Mathf.Approximately(bloom.intensity.value, SceneEnvironmentSetup.BloomIntensity));
                Check("vol_bloom_scatter", Mathf.Approximately(bloom.scatter.value, SceneEnvironmentSetup.BloomScatter));
                Check("vol_bloom_downscale_quarter", bloom.downscale.value == BloomDownscaleMode.Quarter,
                      "game.js:399 sizes the bloom RT at w/4,h/4 — Quarter is the direct match, not just visual");
                Check("vol_bloom_not_bicubic", bloom.highQualityFiltering.value == false,
                      "game.js's blur is plain bilinear, not the bicubic upsample highQualityFiltering turns on");
            }
            else
            {
                Check("vol_bloom_threshold", false, "Bloom override missing from DefaultVolumeProfile.asset");
            }

            // ---- environment reflections: game.js:299-313 ----
            Fact("REFLECTION_MODE", RenderSettings.defaultReflectionMode);
            Fact("REFLECTION_CUBEMAP", RenderSettings.customReflection != null ? RenderSettings.customReflection.name : "null");
            Fact("REFLECTION_CUBEMAP_DIM", RenderSettings.customReflection != null ? RenderSettings.customReflection.dimension.ToString() : "n/a");
            Check("reflection_mode_custom", RenderSettings.defaultReflectionMode == UnityEngine.Rendering.DefaultReflectionMode.Custom,
                  "env.hdr not loaded as a Cubemap — see Editor/EnvironmentReflectionSetup.cs");
            Check("reflection_cubemap_assigned", RenderSettings.customReflection != null);
            Check("reflection_cubemap_is_cube", RenderSettings.customReflection != null &&
                  RenderSettings.customReflection.dimension == UnityEngine.Rendering.TextureDimension.Cube);
            Check("reflection_cubemap_is_env_asset", env.EnvCubemap != null &&
                  AssetDatabase.GetAssetPath(env.EnvCubemap) == SceneEnvironmentSetup.EnvHdrAssetPath,
                  AssetDatabase.GetAssetPath(env.EnvCubemap));

            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(envGo);
            if (env.Sun != null) UnityEngine.Object.DestroyImmediate(env.Sun.gameObject);

            Debug.Log(allPass ? "SceneEnvironmentProbe: ALL PASS" : "SceneEnvironmentProbe: SOME FAILED");
        }
    }
}
