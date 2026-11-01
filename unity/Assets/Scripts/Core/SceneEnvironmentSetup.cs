using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TbilisiFly.Core
{
    /// <summary>
    /// Port of src/game.js:242-339 `_setupScene()` — the renderer/camera/sky/
    /// sun/hemisphere/fog half of the JS's one-time scene construction (run
    /// once, in the Game constructor, before any city geometry exists).
    ///
    /// NOT here, by design — owned elsewhere, superseded, or a real remaining
    /// gap (see docs/port_units/coredone.json for the exact accounting):
    ///   - game.js:254-270 WebGLRenderer flags (antialias/powerPreference/
    ///     pixelRatio/setSize/container.appendChild) — these are browser-canvas
    ///     bridging with no Unity analogue; Unity owns its own device/backbuffer.
    ///     The two REAL behavioural settings in that block — MSAA on/off and
    ///     shadow softness — are carried across onto
    ///     Assets/Settings/TbilisiFly_URP.asset instead (a static asset, since
    ///     the JS never flips either after this one-time assignment).
    ///   - game.js:266-268 toneMapping/toneMappingExposure — the VALUES are
    ///     Assets/Resources/DefaultVolumeProfile.asset's Tonemapping+ColorAdjustments
    ///     overrides (URP puts post on a Volume, not the renderer/camera), but
    ///     whether that stack RUNS is this file's job: see
    ///     SetupPostProcessing() for the two gates that were shut.
    ///   - game.js:299-313 PMREMGenerator sky bake + the Poly Haven HDR
    ///     upgrade (environment reflections/ambient) — PORTED, see
    ///     SetupEnvironmentReflections() below.
    ///   - game.js:341-357 the fx-light pool                 -> Fx/FxLightPool.cs
    ///   - game.js:358-364 `scene.add(plane)`/castShadow      -> Flight-owned
    ///     (the active plane's own renderers; this file only prepares what
    ///     lights/camera/sky they get drawn under).
    ///   - game.js:365-406 the N8AO/Bloom/OutputPass composer, split in two:
    ///       * Bloom (UnrealBloomPass, game.js:397-402) -> PORTED, as a Bloom
    ///         override added directly to Assets/Resources/DefaultVolumeProfile.asset
    ///         (threshold 0.9, intensity 0.14, scatter 0.5, downscale=Quarter
    ///         matching the JS's own w/4,h/4 target size, highQualityFiltering
    ///         off matching the JS's plain — not bicubic — blur). A Volume
    ///         override, like Tonemapping/ColorAdjustments above, because the
    ///         JS never changes these values after construction either.
    ///       * N8AO ambient occlusion (game.js:369-391) -> PORTED (2026-10-27,
    ///         the follow-up this note used to flag). Assets/Editor/SsaoSetup.cs
    ///         adds URP's own Screen Space Ambient Occlusion Renderer Feature to
    ///         Assets/Settings/TbilisiFly_Renderer.asset — through the typed
    ///         ScriptableObject.CreateInstance+AddObjectToAsset+rendererFeatures
    ///         API plus SerializedObject/SerializedProperty for the settings
    ///         class's internal fields, never hand-edited YAML — carrying over
    ///         aoRadius=4/intensity=2.2/halfRes=true (Radius/Intensity/Downsample);
    ///         verified by reading the values back off the asset after a real
    ///         AssetDatabase reimport, not just in-memory. See that file's own
    ///         header for why Falloff/AfterOpaque are not a 1:1 numeric port
    ///         (different algorithm, not the same parameterisation).
    ///   - game.js:408-414/2460-2492 the warning grid          -> Core/WarnGrid.cs
    ///     (already ported+wired by CombatWorldAdapter.Install(); this file
    ///     does not touch it).
    ///
    /// Every numeric constant below is the JS's own literal, carried across
    /// unchanged (not re-measured) — see each field's comment for the exact
    /// game.js line.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneEnvironmentSetup : MonoBehaviour
    {
        public Camera Cam;
        public Light Sun;

        // ---- environment reflections: game.js:299-313 ----
        // The Poly Haven HDR (public/textures/env.hdr, mirrored at
        // Assets/Resources/Art/Textures/env.hdr) re-imported as a Cubemap by
        // Editor/EnvironmentReflectionSetup.cs. Exposed as a field (rather than
        // hard-loaded here) so a scene builder can assign it directly; the
        // editor-only fallback below covers probes and in-editor testing, the
        // same pattern SetupSkybox() already uses for Shader.Find.
        public Cubemap EnvCubemap;
        public const string EnvHdrAssetPath = "Assets/Resources/Art/Textures/env.hdr";

        // ---- camera: game.js:279-284 `new THREE.PerspectiveCamera(62, aspect, 1, 60000)` ----
        public const float CameraFovDeg = 62f;
        public const float CameraNear = 1f;
        public const float CameraFar = 60000f;

        // ---- sun direction: game.js:291-297 "late-morning sun from the southeast" ----
        public const float SunElevationDeg = 48f; // game.js:295 `degToRad(48)`
        public const float SunAzimuthDeg = 135f;  // game.js:296 `degToRad(135)`

        // ---- sun light: game.js:315-337 `new THREE.DirectionalLight(0xfff0dc, 2.7)` ----
        static readonly Color SunColor = new Color32(0xff, 0xf0, 0xdc, 0xff); // game.js:315
        // Three.js's arbitrary PBR light-intensity scale and Unity's non-physical
        // Light.intensity are not the same unit system (see Core/
        // CharacterPreviewRig.cs's own AddDirLight note for the precedent this
        // follows) — carried across as the JS's own literal number.
        public const float SunIntensity = 2.7f;              // game.js:315
        public const float SunShadowStrength = 0.82f;        // game.js:320 `sun.shadow.intensity = 0.82`

        // ---- hemisphere ambient: game.js:339 `new THREE.HemisphereLight(0xbdd8ff, 0x77664e, 2.0)` ----
        static readonly Color HemiSky = new Color32(0xbd, 0xd8, 0xff, 0xff);
        static readonly Color HemiGround = new Color32(0x77, 0x66, 0x4e, 0xff);
        public const float HemiIntensity = 2.0f;

        // ---- fog: game.js:277 `new THREE.FogExp2(0xaec4d8, 0.00006)` ----
        // Three's FogExp2 and Unity's FogMode.ExponentialSquared are the SAME
        // curve, exp(-(density*z)^2) — a literal, unconverted port.
        static readonly Color FogColor = new Color32(0xae, 0xc4, 0xd8, 0xff);
        public const float FogDensity = 0.00006f;

        // ---- shadow box: game.js:38-39 `SHADOW_NEAR_M = 160` (the box this
        // setup actually builds; SHADOW_FAR_M=700 only feeds _tick's adaptive
        // resize, which Core/GameLoop.cs's own StageBoundaryAndZones header
        // already calls out as superseded by URP's cascaded shadow maps + a
        // single Shadow Distance — no per-light orthographic box exists in
        // URP to resize, so there is nothing left for this file to build
        // beyond the one static Shadow Distance value, set on
        // Assets/Settings/TbilisiFly_URP.asset.)
        public const float ShadowNearM = 160f;

        // ---- bloom: game.js:399 `new UnrealBloomPass(new Vector2(w/4,h/4),
        // 0.14, 0.5, 0.9)` (strength, radius, threshold) — these are NOT
        // consumed at runtime by this file (Bloom is a Volume override on
        // Assets/Resources/DefaultVolumeProfile.asset, a static asset edit like
        // Tonemapping/ColorAdjustments above, since the JS never changes
        // these either after construction); kept here so the probe can
        // assert the asset's live values against the same named constants
        // instead of a second copy of the literals.
        public const float BloomIntensity = 0.14f; // game.js:399 strength
        public const float BloomScatter = 0.5f;    // game.js:399 radius
        public const float BloomThreshold = 0.9f;  // game.js:399 threshold

        // ---- post-processing: game.js:243-259 (antialias) + 266-268 (ACES) ----
        // The JS's DEFAULT quality preset is "ultra", which runs the
        // EffectComposer, and game.js:243-259 switches canvas MSAA OFF for
        // exactly that case ("4x MSAA antialiased NOTHING while still
        // allocating a multisampled colour+depth backbuffer"). The composer
        // adds no FXAA/SMAA/TAA of its own either — N8AO, UnrealBloom,
        // OutputPass, and nothing else. So the ported camera runs with
        // AntialiasingMode.None, matching Assets/Settings/TbilisiFly_URP.asset's
        // own m_MSAA: 1 (disabled).
        public const AntialiasingMode CameraAntialiasing = AntialiasingMode.None;

        // The Volume this file keeps in the scene so that "what post stack runs"
        // is visible in the scene rather than only in Graphics settings. The
        // profile is Assets/Resources/DefaultVolumeProfile.asset — the same asset URP's
        // URPDefaultVolumeProfileSettings already points at, so the two agree by
        // construction (see EnsureGlobalVolume).
        public const string VolumeObjectName = "PostVolume";
        public const string VolumeProfileAssetPath = "Assets/Resources/DefaultVolumeProfile.asset";
        public VolumeProfile PostProfile;

        void Awake()
        {
            if (Cam == null) Cam = Camera.main;
            Setup();
        }

        /// <summary>Idempotent — safe to call more than once (e.g. after
        /// assigning Cam/Sun by hand, as the editor probe does). Mirrors
        /// `_setupScene()` itself: one function, builds everything it needs,
        /// including constructing the sun GameObject if none was handed in.</summary>
        public void Setup()
        {
            if (Sun == null)
            {
                var go = new GameObject("sun");
                go.transform.SetParent(transform, false);
                Sun = go.AddComponent<Light>();
                Sun.type = LightType.Directional;
            }
            SetupCamera();
            SetupPostProcessing();
            SetupSun();
            SetupAmbient();
            SetupFog();
            SetupSkybox();
            SetupEnvironmentReflections();
        }

        void SetupCamera()
        {
            if (Cam == null) return;
            Cam.fieldOfView = CameraFovDeg;
            Cam.nearClipPlane = CameraNear;
            Cam.farClipPlane = CameraFar;
        }

        /// <summary>game.js:266-268's `toneMapping = ACESFilmicToneMapping` is
        /// the one line of `_setupScene` that decides whether the whole city is
        /// a white wash or a photograph, and in URP it does not live on the
        /// camera: it lives on a Volume. Three gates stand between the override
        /// and the pixels, and the FIRST RENDERED FRAMES of this port failed
        /// two of them silently (mean RGB 234.7/231.2/211.3 over Old Town,
        /// 10.4% of the frame pure 255,255,255):
        ///
        ///   1. UniversalRenderer sets `postProcessEnabled = data.postProcessData
        ///      != null` (UniversalRenderer.cs:318). Assets/Settings/
        ///      TbilisiFly_Renderer.asset carried `postProcessData: {fileID: 0}`
        ///      because it was created by script rather than through the
        ///      Create ▸ Rendering ▸ URP Universal Renderer menu (which assigns
        ///      PostProcessData.GetDefaultPostProcessData()). With it null the
        ///      renderer drops EVERY post pass — tonemap, bloom and the colour
        ///      grading LUT — and logs nothing at all. Fixed on the asset.
        ///   2. `cameraData.postProcessEnabled = additionalCameraData
        ///      .renderPostProcessing`, and with NO UniversalAdditionalCameraData
        ///      on the camera the else-branch hard-codes it to false
        ///      (UniversalRenderPipeline.cs:1952/1964). That component is what
        ///      SetupPostProcessing() below guarantees.
        ///   3. A volume stack carrying the Tonemapping override. This one was
        ///      already satisfied: URP 17 initialises VolumeManager with
        ///      `URPDefaultVolumeProfileSettings.volumeProfile`
        ///      (UniversalRenderPipeline.cs:396-397), which is already
        ///      Assets/Resources/DefaultVolumeProfile.asset — so "there is no Volume in
        ///      the scene" was never the cause. The scene Volume added below is
        ///      belt and braces (and documentation), not the fix.
        ///
        /// Gate 1 is why forcing `renderPostProcessing = true` and adding a
        /// Volume by hand produced a byte-identical frame.</summary>
        void SetupPostProcessing()
        {
            if (Cam != null)
            {
                var ucd = Cam.GetComponent<UniversalAdditionalCameraData>();
                if (ucd == null) ucd = Cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                ucd.renderType = CameraRenderType.Base;
                ucd.renderPostProcessing = true;
                ucd.antialiasing = CameraAntialiasing;
                ucd.renderShadows = true;
                // The scene Volume below sits on the Default layer, which is
                // what UniversalAdditionalCameraData's own default mask (1)
                // already selects; set explicitly so a hand-edited camera
                // cannot quietly exclude it.
                ucd.volumeLayerMask = 1;
            }
            EnsureGlobalVolume();
        }

        /// <summary>One global Volume carrying Assets/Resources/DefaultVolumeProfile.asset
        /// (ACES Tonemapping + the -0.074 EV ColorAdjustments that is
        /// game.js:268's `toneMappingExposure = 0.95`, log2(0.95), + the
        /// game.js:399 Bloom). Idempotent: reuses the child by name, so calling
        /// Setup() again — at bake AND at Awake — never stacks Volumes.</summary>
        void EnsureGlobalVolume()
        {
            var t = transform.Find(VolumeObjectName);
            var go = t != null ? t.gameObject : null;

            if (PostProfile == null)
            {
                PostProfile = ArtAssets.Load<VolumeProfile>(VolumeProfileAssetPath);
                // In a player the asset is not loadable by path, but URP has
                // already resolved the very same profile as the pipeline's
                // global default — reuse that rather than shipping a second copy.
                if (PostProfile == null && VolumeManager.instance != null)
                    PostProfile = VolumeManager.instance.globalDefaultProfile;
            }
            // An empty Volume overrides nothing and only costs a stack blend;
            // if the profile cannot be resolved, leave the global default
            // profile to do the work on its own.
            if (PostProfile == null)
            {
                if (go != null) go.SetActive(false);
                return;
            }

            if (go == null)
            {
                go = new GameObject(VolumeObjectName);
                go.transform.SetParent(transform, false);
            }
            go.SetActive(true);
            go.layer = 0; // Default — see volumeLayerMask above
            var vol = go.GetComponent<Volume>();
            if (vol == null) vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 0f;
            vol.weight = 1f;
            vol.sharedProfile = PostProfile;
        }

        /// <summary>game.js:291-297's spherical sun direction, in the GAME
        /// frame, byte-for-byte three.js's own
        /// `Vector3.setFromSphericalCoords(1, PI/2-elev, azim)`:
        /// phi measured from +Y, x=sin(phi)sin(theta), y=cos(phi),
        /// z=sin(phi)cos(theta). Computed in double — see the project's own
        /// float32-vs-float64 rule; this result then goes through
        /// Data.GeoFrame.ToUnity exactly like any other game-frame vector (a
        /// pure direction mirrors the same way a position does, since
        /// ToUnity has no translation term).</summary>
        public static Vector3 SunDirGame(float elevDeg, float azimDeg)
        {
            double elev = elevDeg * Math.PI / 180.0;
            double azim = azimDeg * Math.PI / 180.0;
            double phi = Math.PI / 2.0 - elev;
            double theta = azim;
            double sinPhi = Math.Sin(phi);
            return new Vector3(
                (float)(sinPhi * Math.Sin(theta)),
                (float)Math.Cos(phi),
                (float)(sinPhi * Math.Cos(theta)));
        }

        void SetupSun()
        {
            if (Sun == null) return;
            Sun.type = LightType.Directional;
            Sun.color = SunColor;
            Sun.intensity = SunIntensity;
            Sun.shadows = LightShadows.Soft; // PCFSoftShadowMap, game.js:264
            Sun.shadowStrength = SunShadowStrength; // game.js:320

            Vector3 dirGame = SunDirGame(SunElevationDeg, SunAzimuthDeg);
            Vector3 dirUnity = Data.GeoFrame.ToUnity(dirGame.x, dirGame.y, dirGame.z);
            // JS: sun.position = dirUnity*2500, target = world origin -> the
            // light points FROM the sun TOWARD the origin, i.e. forward = -dirUnity.
            Sun.transform.rotation = Quaternion.LookRotation(-dirUnity.normalized);

            RenderSettings.sun = Sun; // drives the procedural skybox's sun disc, see SetupSkybox()
        }

        void SetupAmbient()
        {
            // three.js HemisphereLight blends ground->sky by dot(normal,up),
            // exactly Unity's Trilight ambient gradient; the equator stop is
            // the HemisphereLight's own midpoint (its blend has no third,
            // distinct equator band).
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = HemiSky;
            RenderSettings.ambientGroundColor = HemiGround;
            RenderSettings.ambientEquatorColor = Color.Lerp(HemiGround, HemiSky, 0.5f);
            RenderSettings.ambientIntensity = HemiIntensity;
        }

        void SetupFog()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogDensity = FogDensity;
        }

        Material _skyMat;

        /// <summary>game.js:287-298's three.js `Sky` (a Rayleigh/Mie
        /// atmospheric-scattering shader, turbidity 6 / rayleigh 1.6 /
        /// mieCoefficient 0.004 / mieDirectionalG 0.8) has no byte-for-byte
        /// Unity counterpart — Unity ships a DIFFERENT scattering model
        /// (Preetham) under "Skybox/Procedural". This is a VISUAL match
        /// (thin, lightly hazy midday sky, sun tracking RenderSettings.sun),
        /// not a numeric port of the three.js uniforms — there is nothing to
        /// convert them to.</summary>
        void SetupSkybox()
        {
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null) return; // stripped from this project's shader list — nothing to assign
            _skyMat = new Material(shader) { name = "sky_procedural" };
            _skyMat.SetFloat("_AtmosphereThickness", 1.0f);
            _skyMat.SetFloat("_Exposure", 1.3f);
            _skyMat.SetColor("_SkyTint", new Color(0.5f, 0.5f, 0.5f));
            _skyMat.SetColor("_GroundColor", HemiGround);
            _skyMat.SetFloat("_SunSize", 0.04f);
            _skyMat.SetFloat("_SunSizeConvergence", 5f);
            RenderSettings.skybox = _skyMat;
        }

        /// <summary>game.js:299-313 — three.js has no per-scene "ambient
        /// reflection cubemap" object; it bakes one straight onto
        /// `scene.environment`, a single GLOBAL source every PBR material
        /// samples for IBL (both specular reflections — water, metal — and
        /// the soft ambient tint the comment calls out). Unity's matching
        /// GLOBAL (non-spatial) knob is RenderSettings.defaultReflectionMode
        /// / customReflection, not a Reflection Probe GameObject: a probe
        /// captures surrounding SCENE geometry from a point in space, which
        /// is not what the JS does (it bakes a flat sky environment, not a
        /// self-reflection of the city), so a spatial probe would be solving a
        /// problem the three.js code never has.
        ///
        /// Stage 1 (game.js:299-306, `pmrem.fromScene(envScene)` baking the
        /// procedural Sky mesh): Skybox mode reads whatever SetupSkybox()
        /// just assigned to RenderSettings.skybox — the literal Unity
        /// equivalent of "bake whatever the sky looks like right now".
        ///
        /// Stage 2 (game.js:308-313, the async Poly Haven RGBELoader
        /// upgrade): once a real HDR cubemap is available, every material
        /// switches to being lit by the photographic HDRI instead. In
        /// three.js this arrives over the network some time after the first
        /// frame; Unity ships env.hdr in the build, so there is no actual
        /// wait — the end STATE (HDRI-lit, not sky-lit) is what's being
        /// ported, and it is reached immediately once EnvCubemap is
        /// assigned. See Editor/EnvironmentReflectionSetup.cs for the
        /// one-time texture-import change (Texture2D -> Cube/Cylindrical)
        /// this depends on.</summary>
        void SetupEnvironmentReflections()
        {
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;

            if (EnvCubemap == null)
                EnvCubemap = ArtAssets.Load<Cubemap>(EnvHdrAssetPath);
            if (EnvCubemap != null)
            {
                RenderSettings.customReflection = EnvCubemap;
                RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
            }
        }
    }
}
