using System;
using TbilisiFly.Character;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TbilisiFly.Core
{
    // =========================================================================
    // CharacterPreviewRig.cs — port of src/main.js's live 3D hero-preview
    // canvas on the WoW-style character-create/roster screen:
    //   setCharPreviewModel   main.js:263-272
    //   sizeCharPreview       main.js:274-282
    //   startCharPreview      main.js:284-321
    //   wireCharDrag          main.js:323-350
    //   stopCharPreview       main.js:352-358
    //
    // Everything else in main.js's character-screen slice (main.js:257-262,
    // 368-616 — renderRaceColumn/renderCharList/renderCharInfo/selectBody/
    // applyCharLayout and the create/roster DOM building around them) is DOM
    // markup generation with no Core/ counterpart: Boot.cs's own header says
    // plainly "no UI.Boot/UI.Character views exist yet ... every screen action
    // is a public method [on Boot.cs] ... for a future view to bind to" — this
    // file is that same pattern applied to the ONE piece of that screen that
    // is genuine 3D/render logic rather than markup: the rotating preview
    // model a future UI.Character view would show behind the race cards.
    //
    // ============================== DESIGN ==============================
    // Renders into a RenderTexture (Target) a future UI Toolkit view displays
    // (an Image control bound to it), instead of a raw WebGL canvas — the
    // closest Unity analogue to the JS's own `<canvas id="char-preview">` + a
    // second THREE.WebGLRenderer sharing the page's GL context.
    //
    // Body loading is NOT this file's job: `spawnBody` is a caller-supplied
    // resolver (Func&lt;string, GameObject&gt;) that instantiates a FRESH copy of
    // the body prefab for `bodyId` — the Unity equivalent of main.js's
    // `makeCharacter(raceId)` (an async THREE loader). No such general-purpose
    // runtime body loader exists yet anywhere in this port (Character/
    // CharacterRig.cs's own constructor already takes an instantiated
    // GameObject, not a path/id — see that file's header), so this is left as
    // a hook for whoever builds it, exactly like IDamageWorld/IPlanePhysicsWorld
    // elsewhere in this port.
    //
    // `_token` guards against the same race main.js:265 `charPrevToken` does:
    // SetModel can be called again (clicking a different race card) before a
    // slow resolver finishes the previous one, and the stale result must be
    // dropped instead of clobbering a newer selection.
    // =========================================================================

    public sealed class CharacterPreviewRig
    {
        // ---- promoted literals (tools/_probe_gamerest.mjs / GameRestProbe.cs) ----
        internal const float PreviewFov = 38f;            // main.js:291 `new THREE.PerspectiveCamera(38, ...)`
        internal const float PreviewSunIntensity = 2.4f;  // main.js:296 `new THREE.DirectionalLight(0xffffff, 2.4)`
        internal const float PreviewRimIntensity = 1.2f;  // main.js:299 `new THREE.DirectionalLight(0x3fd6ff, 1.2)`
        internal const float PreviewDragScale = 0.012f;   // main.js:342 `charPrev.holder.rotation.y += dx * 0.012`

        public RenderTexture Target { get; private set; }

        readonly Camera _camera;
        readonly Transform _holder;     // main.js:303 `holder` — the drag-to-look pivot
        readonly Transform _heroHolder; // main.js:306 `heroHolder` — swapped per body
        readonly Func<string, GameObject> _spawnBody;

        GameObject _currentBody;
        GameObject _rigRoot;    // CharacterRig's own `rig_<id>` root — see SetModel
        CharacterRig _rig;
        int _token;
        bool _running;

        // =====================================================================
        // THE TWO PREVIEW LIGHTS MUST NOT BE THE GAME'S LIGHTS.
        //
        // main.js:295-301 gives the preview its own sun and a cyan holo rim, in
        // its own THREE.Scene, so the city never sees them. This port has one
        // Unity scene, and these were plain enabled DirectionalLights: global,
        // infinite-range, and therefore additional lights on EVERY camera for as
        // long as the menu was up. Invisible today (opaque bg0, the rig at
        // y = -5000, a 100 m far plane) — but this project has a hard-won perf
        // memory that a light appearing or disappearing recompiles material
        // variants, which is what the shooting/crash freeze was
        // (docs/QUALITY_BAR.md, project memory "Perf wave 2026-10"), and
        // UI/Shell/ShellUi.cs builds and destroys this rig on EVERY menu
        // open/close. docs/open_items.json `preview-lights`.
        //
        // URP has no per-light culling mask (that API is unsupported there, and
        // Rendering Layers would need the pipeline asset's own toggle flipped
        // for the whole game). What DOES work, and keeps main.js's two measured
        // intensities exactly as they are, is the frame order: URP raises
        // beginCameraRendering BEFORE it culls for that camera, so a light
        // enabled there is in that camera's visible-light set and in no other's.
        // So both lights live DISABLED and are switched on only while the
        // preview camera itself renders. The game camera's light count is
        // unchanged by opening or closing the menu — that is what
        // Editor/Probes/CorePreviewLightProbe.cs asserts.
        //
        // Unsubscribed in Stop(), which ShellUi.Dispose calls before it kills
        // the rig's root: a delegate left pointing at a destroyed camera is the
        // exact lifetime bug CharacterView.Unbind exists for.
        // =====================================================================
        readonly Light _sun, _rim;
        bool _hooked;

        /// <summary>`root` is an otherwise-empty parent (e.g. a disabled/offscreen
        /// GameObject the caller owns) this rig builds its camera/lights/holder
        /// under — main.js builds a whole separate THREE.Scene for this, so it
        /// never touches the game world; passing a dedicated root here gets the
        /// same isolation.</summary>
        public CharacterPreviewRig(Transform root, Func<string, GameObject> spawnBody)
        {
            _spawnBody = spawnBody;

            // main.js:291-293 `camera` (fov 38, position (0,1.5,4.6), lookAt (0,1.02,0)).
            var camGo = new GameObject("preview_camera");
            camGo.transform.SetParent(root, false);
            camGo.transform.localPosition = new Vector3(0f, 1.5f, 4.6f);
            _camera = camGo.AddComponent<Camera>();
            _camera.fieldOfView = PreviewFov;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 100f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // main.js:287 `alpha: true`
            _camera.enabled = false;                            // until Resize() says the target is usable
            _camera.transform.LookAt(root.TransformPoint(new Vector3(0f, 1.02f, 0f)), Vector3.up);
            // main.js's preview renderer has no post chain and no shadow map of
            // its own (a second plain THREE.WebGLRenderer); spelling that out
            // here keeps the render graph from allocating a post/depth/shadow
            // pyramid off this tiny target every frame the menu is up.
            var camData = camGo.GetComponent<UniversalAdditionalCameraData>();
            if (camData == null) camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderType = CameraRenderType.Base;
            camData.renderPostProcessing = false;
            camData.renderShadows = false;
            camData.antialiasing = AntialiasingMode.None;
            camData.requiresColorOption = CameraOverrideOption.Off;
            camData.requiresDepthOption = CameraOverrideOption.Off;
            camData.allowXRRendering = false;

            // main.js:295-301 — hemisphere fill + sun + a cyan "holo" rim light.
            // Both start DISABLED and are switched on for the preview camera's
            // own render only — see the _sun/_rim block above for why.
            _sun = AddDirLight(root, "preview_sun", new Color32(0xff, 0xff, 0xff, 0xff), PreviewSunIntensity, new Vector3(3f, 6f, 5f));
            _rim = AddDirLight(root, "preview_rim", Color32ToColor(0x3f, 0xd6, 0xff), PreviewRimIntensity, new Vector3(-4f, 3f, -5f));
            Hook();
            // Unity has no baked hemisphere light object the way three.js does;
            // RenderSettings.ambient* is the per-scene equivalent and is left to
            // whoever owns the preview's isolated scene/root to configure once —
            // not per-instance state this class should fight over.

            // main.js:303-307 `holder`/`heroHolder` — holder starts turned to
            // face the camera (the rig's own forward is -Z in the JS, +Z here;
            // see Character/CharacterRig.cs's own header for the per-body facing
            // convention this inherits).
            var holderGo = new GameObject("preview_holder");
            holderGo.transform.SetParent(root, false);
            holderGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _holder = holderGo.transform;
            var heroGo = new GameObject("preview_hero_holder");
            heroGo.transform.SetParent(_holder, false);
            _heroHolder = heroGo.transform;
        }

        static Color Color32ToColor(byte r, byte g, byte b) => new Color32(r, g, b, 255);

        static Light AddDirLight(Transform root, string name, Color color, float intensity, Vector3 dir)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.forward = dir.normalized;
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = color;
            // three.js DirectionalLight intensity and Unity's are not the same
            // unit system — kept as the JS's own number (2.4 / 1.2) since this
            // is a cosmetic preview light, not a physically metered one; tune
            // visually once this is wired into a real view.
            l.intensity = intensity;
            l.shadows = LightShadows.None;  // a menu prop must not join the shadow pass
            l.enabled = false;              // see the _sun/_rim block
            return l;
        }

        void Hook()
        {
            if (_hooked) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _hooked = true;
        }

        void Unhook()
        {
            if (!_hooked) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _hooked = false;
            if (_sun != null) _sun.enabled = false;
            if (_rim != null) _rim.enabled = false;
        }

        /// <summary>Raised by URP once per camera, BEFORE that camera's cull —
        /// which is what makes this an effective per-camera light mask. Mine
        /// only; everybody else's frame sees the scene's own lights and nothing
        /// of this rig.</summary>
        void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            bool mine = _running && _camera != null && cam == _camera;
            if (_sun != null) _sun.enabled = mine;
            if (_rim != null) _rim.enabled = mine;
        }

        // =====================================================================
        // A CAMERA MAY NOT RENDER INTO A DEGENERATE TARGET.
        //
        // UI/Character/CharacterView.cs:258 calls Start() with the preview
        // tile's resolvedStyle size, and before the panel's first layout that
        // is 0 -> clamped to 1. URP's render graph then derives half- and
        // quarter-resolution textures from a 1x1 target, and 1/2 is ZERO:
        //   ArgumentException: Texture using Explicit size mode was create with
        //   either width or height at zero  (RenderGraphResourceRegistry
        //   .ValidateTextureDesc)
        // three times a frame, which is what the first play-mode run logged
        // against this camera. The size self-corrects one layout later
        // (GeometryChangedEvent -> ResizePreview), so the fix is to keep the
        // camera OFF until the target is big enough to derive from rather
        // than to inflate the texture — which would also have made
        // UiShotProbe's `heroPreviewHasRealSize` pass without a real layout.
        // =====================================================================
        internal const int MinRenderSize = 64;

        bool TargetUsable => Target != null && Target.width >= MinRenderSize && Target.height >= MinRenderSize;

        /// <summary>main.js:274-282 `sizeCharPreview`.</summary>
        public void Resize(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (Target == null || Target.width != width || Target.height != height)
            {
                if (Target != null) Target.Release();
                Target = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32) { name = "char_preview_rt" };
                Target.Create();
                _camera.targetTexture = Target;
                _camera.aspect = (float)width / height;
            }
            _camera.enabled = _running && TargetUsable;
        }

        /// <summary>main.js:284-321 `startCharPreview`. Idempotent — calling it
        /// again while already running is a no-op, same as the JS's own
        /// `if (charPrev) return;` guard.</summary>
        public void Start(int width, int height, string initialBodyId)
        {
            if (_running) return;
            _running = true;
            Hook();                 // a Stop() -> Start() cycle must get its lights back
            Resize(width, height);  // sets _camera.enabled off a usable target — see MinRenderSize
            SetModel(initialBodyId);
        }

        /// <summary>Object.Destroy is DEFERRED and logs "Destroy may not be
        /// called from edit mode" outside play mode, so the preview body
        /// survives the call and the error count climbs — and this rig runs in
        /// EDIT mode on every hero-screen probe (Probes/HeroScreenProbe logged
        /// HEROSCREEN_EDITMODE_DESTROY_COUNT=81 from these six call sites
        /// alone, every one a `rig_&lt;body&gt;` root). Same Application.isPlaying
        /// switch Props/WarehouseInteriorFill.cs:222, Flight/PlaneProps.cs:815
        /// and Flight/PlaneCockpitKit.cs already use. A reverse child loop is
        /// safe with DestroyImmediate: the index only ever walks downward.</summary>
        static void DestroyEither(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>main.js:352-358 `stopCharPreview`.</summary>
        public void Stop()
        {
            // Unhook unconditionally, BEFORE the _running early-out: ShellUi
            // disposes the rig whether or not the preview was ever started, and
            // a live subscription outliving the rig's root is the bug
            // CharacterView.Unbind exists for.
            Unhook();
            if (!_running) return;
            _running = false;
            // At application quit Unity destroys objects in no fixed order, so by
            // the time Boot.OnDestroy -> ShellUi.Dispose -> Stop() runs the camera
            // may already be gone: the user's first real session logged exactly
            // one exception, a NullReferenceException here (2026-10-31). Unity's
            // overloaded null check is true for a destroyed object.
            if (_camera != null) _camera.enabled = false;
            // The rig root is the body's real parent (see SetModel) — killing
            // only `_currentBody` left an empty `rig_<id>/act/anim` chain in
            // the scene on every menu close.
            if (_rigRoot != null) DestroyEither(_rigRoot);
            else if (_currentBody != null) DestroyEither(_currentBody);
            _currentBody = null;
            _rigRoot = null;
            _rig = null;
            if (Target != null) { Target.Release(); Target = null; }
        }

        /// <summary>main.js:263-272 `setCharPreviewModel`. `bodyId` is one of
        /// CharacterRoster.BodiesOf(raceId) (Character/CharacterRoster.cs,
        /// already ported) — selecting a race picks its default body the same
        /// way main.js:371-381 `selectBody` does; that selection logic belongs
        /// to the future UI.Character view, not this file.</summary>
        public void SetModel(string bodyId)
        {
            int myToken = ++_token;
            GameObject instance = _spawnBody?.Invoke(bodyId);
            if (instance == null || myToken != _token)
            {
                // Resolver failed, or a newer SetModel call already landed —
                // main.js:267 `if (!charPrev || token !== charPrevToken) return;`
                if (instance != null) DestroyEither(instance);
                return;
            }
            for (int i = _heroHolder.childCount - 1; i >= 0; i--)
                DestroyEither(_heroHolder.GetChild(i).gameObject);
            if (_rigRoot != null) DestroyEither(_rigRoot);
            else if (_currentBody != null) DestroyEither(_currentBody);
            _rigRoot = null;

            _currentBody = instance;

            // =============================================================
            // THE RIG OWNS THE BODY'S PARENT — DO NOT PARENT THE INSTANCE.
            //
            // CharacterRig's constructor (Character/CharacterRig.cs:129-140)
            // builds its OWN `rig_<id>/act/anim` chain as a NEW scene-root
            // GameObject and calls `Model.SetParent(AnimGroup, false)`, so any
            // parenting done to `instance` here is undone one line later and
            // the body ends up at the WORLD ORIGIN. The preview camera lives
            // at UI/Shell/ShellUi.PreviewWorldY (-5000) with a 100 m far
            // plane, so the body was 5 km outside the frustum and the HOLO
            // SCAN tile measured 0.00% lit (UiShotProbe
            // `heroPreviewHasAnImage`; UIPREVIEW read
            // `bodyPos=(0.0, 0.6, 0.0) parent=anim parentWorld=(0.00,0.00,0.00)`
            // against `camPos=(0.0, -4998.5, 4.6)` — the measurement that
            // found this). docs/open_items.json `hero-preview-black`.
            //
            // So: construct the rig FIRST (it measures the body's height off
            // live world-space Renderer.bounds in NormalizeHeight, which is
            // scale-only and position-independent), then move the RIG ROOT —
            // the thing the rig actually owns — under the drag holder.
            // =============================================================
            _rig = new CharacterRig(instance, bodyId);
            _rigRoot = _rig.Root != null ? _rig.Root.gameObject : null;
            if (_rig.Root != null)
            {
                _rig.Root.SetParent(_heroHolder, false);
                _rig.Root.localPosition = Vector3.zero;
                _rig.Root.localRotation = Quaternion.identity;
                _rig.Root.localScale = Vector3.one;
            }
            // main.js:269 `rig.play('Idle')`. For the humanoid bodies (banana/
            // eggplant/toad) this needs no call at all: CharacterRig.cs's own
            // Animator defaults to its controller's entry state (Idle), and
            // Unity ticks the Animator component itself every frame with no
            // manual Update() — unlike three.js, which needed an explicit
            // AnimationMixer.update() per frame (main.js:315). Only the
            // elephant (Procedural, no Animator) needs a per-frame call, which
            // Tick() below supplies via Locomote(dt, 0) — see that method's own
            // guard (`if (!Procedural) return`) for why this is a no-op on the
            // other three bodies rather than a second, conflicting pose source.
        }

        /// <summary>main.js:313-316's per-frame tick (the preview's own
        /// requestAnimationFrame loop) — call once a frame while running.
        /// Must run in LateUpdate if the caller is a MonoBehaviour (CharacterRig.cs's
        /// own rule: "never rebase a pose off the Animator's own output").</summary>
        public void Tick(float dt)
        {
            if (!_running || _rig == null) return;
            _rig.Locomote(dt, 0f); // standing still — Procedural-only, see above
        }

        /// <summary>main.js:323-350 `wireCharDrag`. The caller feeds raw pointer
        /// delta-X (e.g. from a UI Toolkit PointerMoveEvent while a
        /// PointerDownEvent is active on the preview element) — this file does
        /// not own input binding, only the resulting rotation, same split as
        /// FlightCamera.cs's own drag handling.</summary>
        public void Drag(float deltaX) => _holder.Rotate(0f, deltaX * PreviewDragScale * Mathf.Rad2Deg, 0f, Space.Self);
    }
}
