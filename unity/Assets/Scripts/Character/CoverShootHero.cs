using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Assembles the purchased Asset Store "Cover + Shooting System" (Vinicius
    /// Marques) onto the hero's own CharacterRig when
    /// <see cref="Core.GameLoop.UsePurchasedShooter"/> is true (user decision,
    /// 2026-11-01: "shooting u can move new one from our assets ... i dont need
    /// old gun reload shooting from js").
    ///
    /// ================================ SCOPE (narrowed 2026-11-01 15:3x) ===========
    /// Coordinator decision: two systems must not fight over the hero's
    /// Animator. Character/FootController.cs (ticked fully by Core/
    /// GameLoop.StageOnFootPurchased, exactly like the JS-ported path) stays
    /// the ONLY owner of Pos/Yaw/Grounded/climb/boarding/handedness — so this
    /// class does NOT add `MoveBehaviour` (no Rigidbody-driven WASD
    /// locomotion from the pack) and does NOT assign
    /// Resources/ThirdParty/CoverShoot/Animators/CharacterController.controller
    /// onto `rig.Anim` (that stays free for the parkour/locomotion
    /// PlayableGraph another agent owns). What this class DOES still own:
    /// aim (RMB)/shoot (LMB, routed into Combat/CoverShootBridge.cs's
    /// IDamageWorld)/cover (take/leave/peek/shoot-from-cover against the
    /// invisible cover markers below)/reload/weapon pickups+switching
    /// (Character/CoverShootPickups.cs)/the third-person orbit camera/the
    /// Georgian HUD feed (UI/Hud/CoverShootAmmoView.cs). `BasicBehaviour` +
    /// a Rigidbody + CapsuleCollider are still attached — `AimBehaviour`/
    /// `ShootBehaviour`/`CoverBehaviour` all read `behaviourManager`
    /// (BasicBehaviour) for the Animator/camera/collider references and
    /// `GetRigidBody.MoveRotation`-style calls for their OWN facing turns
    /// (aiming at the camera, turning to face a cover wall) — none of that
    /// is ground locomotion, so a permanently-KINEMATIC Rigidbody is the
    /// right fit: `MoveRotation` still works on it (physics-interpolated),
    /// but nothing (no force/velocity) ever drives it, so it can never
    /// fight FootController's own direct `Rig.Root.position` writes.
    ///
    /// Visual feedback for aim/shoot/cover/reload is INTENTIONALLY not
    /// wired to Mecanim here (no controller = `Animator.SetBool`/
    /// `SetTrigger` calls the pack still makes are harmless no-ops) — the
    /// task calls for driving the body through
    /// `ParkourClipPlayer.PlayOverlay(clip, mask, weight)` with the pack's
    /// own pistol_/rifle_ clips instead, which is not wired up yet (see
    /// report). Gameplay state that used to be read off an Animator bool is
    /// exposed as a plain C# property instead for testability:
    /// `AimBehaviour.IsAiming`, `CoverBehaviour.IsCovering`,
    /// `ShootBehaviour.IsReloading` (all added to the pack's own files).
    ///
    /// ================================ WHERE THE COMPONENTS LIVE =================
    /// `BasicBehaviour`/`AimBehaviour`/`ShootBehaviour`/`CoverBehaviour` +
    /// `Rigidbody` + `CapsuleCollider` all go on `rig.Root.gameObject` — NOT
    /// on `rig.Model` (the Animator's own GameObject, 3 empty transforms
    /// below Root: Root/act/anim/Model, see CharacterRig.cs's header on
    /// ActGroup/AnimGroup). Root is already what every other system in this
    /// project reads (`GameLoop.Foot.Pos`/`Rig.Root.position`,
    /// `ToggleBoard`'s reach checks, `UpdateZones`), so this costs zero
    /// follow-sync code. `BasicBehaviour.Awake` was edited (ThirdParty/
    /// CoverShoot/Scripts/PlayerScripts/BasicBehaviour.cs) to find the
    /// Animator with `GetComponentInChildren` instead of `GetComponent`,
    /// since it does not live on the same object — the one cost:
    /// `ShootBehaviour.OnAnimatorIK` never fires (needs the same
    /// GameObject), so the fine torso-lean IK pass stays inert;
    /// `AimBehaviour`'s whole-body turn (`Rotating()`) still works.
    ///
    /// ================================ AWAKE-ORDERING =============================
    /// Every one of the 3 behaviours reads a cross-reference in `Awake()`
    /// (`BasicBehaviour.playerCamera.GetComponent&lt;ThirdPersonOrbitCam&gt;()`,
    /// `GenericBehaviour.GetComponent&lt;BasicBehaviour&gt;()`) — fields that can
    /// only be assigned AFTER `AddComponent` returns, which is already too
    /// late because `AddComponent` fires `Awake` synchronously on an ACTIVE
    /// GameObject. So both the hero root and the camera are built while
    /// temporarily INACTIVE (Awake is deferred until `SetActive(true)`), wired,
    /// THEN activated — see <see cref="BuildCamera"/>/<see cref="Setup"/>.
    ///
    /// ================================ INPUT =================================
    /// Every `Input.Get*` call across the pack's scripts was mechanically
    /// renamed to `CoverShootInput.Get*` (see that file's header): this
    /// project's play-mode harness drives input exclusively through the new
    /// Input System (`InputSystem.QueueStateEvent`), which legacy
    /// `UnityEngine.Input` never observed here — found by the first real
    /// CoverShootRun play-mode run (every pack behaviour was inert).
    /// </summary>
    public sealed class CoverShootHero : MonoBehaviour
    {
        public static CoverShootHero Instance { get; private set; }

        Core.GameLoop _game;
        CharacterRig _rig;
        Rigidbody _rb;
        CapsuleCollider _capsule;
        BasicBehaviour _basic;
        AimBehaviour _aim;
        ShootBehaviour _shoot;
        CoverBehaviour _cover;

        bool _suspendedByBoarding;
        bool _suspendedByParkour;
        bool _everActive;

        public static CoverShootHero Attach(Core.GameLoop game, CharacterRig rig)
        {
            if (rig == null || !rig.IsHumanoid || rig.Anim == null)
            {
                // The toy race (elephant, Generic rig) and any rigless body
                // cannot carry this pack (it is HumanBodyBones-only throughout
                // AimBehaviour/ShootBehaviour/CoverBehaviour) — fall back to
                // the JS-ported path for those rather than crash on them.
                Debug.Log($"COVERSHOOT attach skipped id={rig?.Id} humanoid={(rig != null && rig.IsHumanoid ? 1 : 0)}");
                return null;
            }
            if (game == null || game.MainCamera == null)
            {
                // BasicBehaviour.Awake reads `playerCamera.GetComponent<...>()`
                // unconditionally — without a MainCamera that field would stay
                // null and Awake would NRE the instant this GameObject (re)activates.
                Debug.Log("COVERSHOOT attach skipped id=" + rig.Id + " reason=no_main_camera");
                return null;
            }
            var go = rig.Root.gameObject;
            var hero = go.GetComponent<CoverShootHero>();
            if (hero == null) hero = go.AddComponent<CoverShootHero>();
            hero.Setup(game, rig);
            Instance = hero;
            return hero;
        }

        void Setup(Core.GameLoop game, CharacterRig rig)
        {
            _game = game;
            _rig = rig;

            if (!_everActive) gameObject.SetActive(false); // defer Awake until every cross-ref below is wired

            gameObject.tag = "Player";
            EnsureGameController();
            BuildPhysics();
            BuildBehaviours();
            BuildCamera();

            if (!_everActive)
            {
                gameObject.SetActive(true); // first-ever setup always ends active — hero is on screen
                _everActive = true;
            }

            // These are read by Start() (next engine Start-phase, strictly
            // after this method returns), never by Awake(), so it is safe to
            // assign them AFTER reactivating.
            WireShootFields();
            WireCoverFields();
            WireAimFields();
            BuildCoverMarkers();
            CoverShootPickups.EnsureSpawned(game, rig);
            TbilisiFly.UI.Hud.CoverShootAmmoView.EnsureInstalled();

            Debug.Log($"COVERSHOOT attach id={rig.Id} humanoid=1 bodyHeight={rig.BodyHeight:F2} movementOwner=FootController");
        }

        void BuildPhysics()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = 75f;
            _rb.freezeRotation = true;
            // ALWAYS kinematic: Character/FootController.cs owns Pos/Yaw now
            // (Core/GameLoop.StageOnFootPurchased calls its full Update()),
            // not this Rigidbody. `MoveRotation` still works fine on a
            // kinematic body (physics-interpolated) for AimBehaviour/
            // CoverBehaviour's own facing turns — it just never receives a
            // force/velocity, so it can never fight FootController's direct
            // `Rig.Root.position` writes.
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;

            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule == null) _capsule = gameObject.AddComponent<CapsuleCollider>();
            float h = Mathf.Max(1.2f, _rig.BodyHeight);
            _capsule.height = h;
            _capsule.radius = Mathf.Clamp(h * 0.16f, 0.22f, 0.32f);
            _capsule.center = new Vector3(0f, h * 0.5f, 0f); // Root sits at the feet/ground point (CharacterRig.NormalizeHeight)
        }

        void BuildBehaviours()
        {
            // No MoveBehaviour: ground locomotion stays with FootController
            // (coordinator decision 2026-11-01) — only the aim/shoot/cover
            // trio, riding on BasicBehaviour's shared references.
            _basic = GetComponent<BasicBehaviour>() ?? gameObject.AddComponent<BasicBehaviour>();
            _aim = GetComponent<AimBehaviour>() ?? gameObject.AddComponent<AimBehaviour>();
            _shoot = GetComponent<ShootBehaviour>() ?? gameObject.AddComponent<ShootBehaviour>();
            _cover = GetComponent<CoverBehaviour>() ?? gameObject.AddComponent<CoverBehaviour>();

            _basic.sprintButton = "CS_Sprint";
        }

        // Coordinator decision 2026-11-01 15:5x: no ThirdPersonOrbitCam —
        // Core/GameLoop.cs's own on-foot camera (TickOnFootCamera/
        // TickChaseCamera) keeps owning the camera under the purchased-
        // shooter path too, so FootController's camera-relative turning
        // (CamYaw) stays valid and A/D stay screen-relative. AimBehaviour/
        // CoverBehaviour/ShootBehaviour's own `GetCamScript.*` calls are all
        // null-guarded or replaced with direct `playerCamera` reads (see
        // their own files) since there is no ThirdPersonOrbitCam to find.
        void BuildCamera()
        {
            if (_game.MainCamera == null) return;
            _basic.playerCamera = _game.MainCamera.transform;
        }

        void WireShootFields()
        {
            // No AnimatorController assignment here (see class header) —
            // rig.Anim stays free for the parkour/locomotion PlayableGraph.
            _shoot.shootButton = "Fire1";       // left mouse, see InputManager.asset default alt button
            _shoot.pickButton = "CS_Interact";
            _shoot.changeButton = "CS_Change";
            _shoot.reloadButton = "CS_Reload";
            _shoot.dropButton = "CS_Drop";
            _shoot.bulletHole = Resources.Load<Material>("ThirdParty/CoverShoot/Materials/bullet_hole");

            var fxRoot = Resources.Load<GameObject>("ThirdParty/CoverShoot/Prefabs/ShotEffects");
            GameObject fxInstance = fxRoot != null ? Instantiate(fxRoot) : null;
            if (fxInstance != null)
            {
                fxInstance.name = "CoverShootShotEffects_" + _rig.Id;
                Transform Find(string n) => fxInstance.transform.Find(n);
                _shoot.muzzleFlash = (Find("flash") ?? fxInstance.transform).gameObject;
                _shoot.shot = (Find("tracer") ?? fxInstance.transform).gameObject;
                _shoot.sparks = (Find("sparks") ?? fxInstance.transform).gameObject;
            }
            else
            {
                // No ShotEffects.prefab reachable (Resources miss) — give
                // ShootBehaviour.Start() SOMETHING non-null so it does not NRE
                // on muzzleFlash.SetActive(false) etc.
                _shoot.muzzleFlash = new GameObject("CoverShoot_FlashFallback");
                _shoot.shot = new GameObject("CoverShoot_ShotFallback");
                _shoot.sparks = new GameObject("CoverShoot_SparksFallback");
            }
        }

        void WireCoverFields()
        {
            _cover.coverButton = "CS_Cover";
            int coverLayer = LayerMask.NameToLayer("Cover");
            int coverInvisLayer = LayerMask.NameToLayer("Cover Invisible");
            int mask = 0;
            if (coverLayer >= 0) mask |= 1 << coverLayer;
            if (coverInvisLayer >= 0) mask |= 1 << coverInvisLayer;
            _cover.coverMask = mask;

            // The pack's Update() unconditionally calls UndrawSign(coverSign)
            // (no null-check — see CoverBehaviour.cs) every frame there is no
            // possible cover, so these four MUST be non-null. They carry no
            // renderer: the pack's own cover/turn/change/jump arrow textures
            // (Textures/Signs/*.png) are imported but intentionally left
            // unwired — the Georgian UI rule is "no English strings on
            // screen", and this project has no Georgian replacement art for
            // these icons yet. See report: left for a follow-up pass.
            _cover.coverSign = NewHiddenSign("CS_CoverSign");
            _cover.turnCoverSign = NewHiddenSign("CS_TurnCoverSign");
            _cover.changeCoverSign = NewHiddenSign("CS_ChangeCoverSign");
            _cover.jumpCoverSign = NewHiddenSign("CS_JumpCoverSign");
        }

        GameObject NewHiddenSign(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            return go;
        }

        void WireAimFields()
        {
            _aim.aimButton = "CS_Aim";
            _aim.shoulderButton = "CS_AimShoulder";
        }

        void EnsureGameController()
        {
            var gc = GameObject.FindGameObjectWithTag("GameController");
            if (gc != null && gc.transform.Find("ScreenHUD") != null && GameObject.Find("ScreenHUD") != null) return;

            if (gc == null)
            {
                gc = new GameObject("CoverShootGameController");
                gc.tag = "GameController";
            }

            var screenPrefab = Resources.Load<GameObject>("ThirdParty/CoverShoot/Prefabs/ScreenHUD");
            if (screenPrefab != null && GameObject.Find("ScreenHUD") == null)
            {
                var screen = Instantiate(screenPrefab, gc.transform);
                screen.name = "ScreenHUD"; // InteractiveWeapon.Awake does GameObject.Find("ScreenHUD") by exact name
                // Georgian-UI-only rule: this prefab's own English bullet/ammo
                // panel is never shown. UI/Hud/CoverShootAmmoView.cs is the
                // Georgian-labelled replacement (fed by WeaponUIManager's
                // static bridge events, added to our ThirdParty copy).
                var canvas = screen.GetComponent<Canvas>() ?? screen.GetComponentInChildren<Canvas>(true);
                if (canvas != null) canvas.enabled = false;
            }

            var pickupPrefab = Resources.Load<GameObject>("ThirdParty/CoverShoot/Prefabs/PickupHUD");
            if (pickupPrefab != null && gc.transform.Find("PickupHUD") == null)
            {
                var pickup = Instantiate(pickupPrefab, gc.transform);
                pickup.name = "PickupHUD";
                var canvas = pickup.GetComponent<Canvas>() ?? pickup.GetComponentInChildren<Canvas>(true);
                if (canvas != null) canvas.enabled = false;
                pickup.SetActive(false); // InteractiveWeapon.Awake toggles this on/off itself
            }
        }

        // =====================================================================
        // COVER MARKERS — the README's own documented technique ("you can use
        // a simpler mesh as a cover container ... put the primitive collider
        // on a game object and set its Layer as Cover Invisible") rather than
        // changing any REAL building/terrain collider's layer, which other
        // systems (Combat raycasts, DamageReachProbe's mask assumptions) do
        // not expect to move. One invisible box per nearby solid surface, so
        // `CoverBehaviour.HasPossibleCover`'s close-range branch
        // (`HandleNavPath`'s `else` arm, coverDist=0.4 m — no NavMesh
        // required there) has somewhere real to engage.
        // =====================================================================
        void BuildCoverMarkers()
        {
            // Widened 2026-11-01: the original 4-direction/5 m scan found
            // ZERO markers when spawn happens to be in open ground (the
            // first real CoverShootRun run — airport tarmac, nothing within
            // 5 m in any cardinal direction). 16-direction compass, 15 m —
            // still a one-shot scan at spawn, not a moving search, but a
            // much better chance of finding SOMETHING cover-shaped nearby.
            int coverInvis = LayerMask.NameToLayer("Cover Invisible");
            Debug.Log($"COVERSHOOT covermarkers_enter coverInvisLayer={coverInvis} pos={transform.position}");
            if (coverInvis < 0) return;
            int built = 0;
            for (int i = 0; i < 16; i++)
            {
                float a = i * (360f / 16f) * Mathf.Deg2Rad;
                Vector3 d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                Vector3 origin = transform.position + Vector3.up * 1f;
                if (Physics.Raycast(origin, d, out var hit, 15f) && hit.collider != null && hit.collider.gameObject != gameObject
                    && !hit.collider.CompareTag("Player"))
                {
                    var marker = new GameObject("CoverMarker_" + hit.collider.name);
                    marker.layer = coverInvis;
                    marker.transform.position = hit.point - d.normalized * 0.05f;
                    marker.transform.rotation = Quaternion.LookRotation(hit.normal);
                    var box = marker.AddComponent<BoxCollider>();
                    box.size = new Vector3(2.2f, 2.0f, 0.1f);
                    box.isTrigger = false;
                    built++;
                    // Static per the README ("remember to set all cover objects
                    // on the scene as Static") — StaticEditorFlags is editor-
                    // only; a runtime-spawned collider has no batching to lose.
                }
            }
            Debug.Log($"COVERSHOOT covermarkers built={built}");
        }

        // =====================================================================
        // SUSPEND / RESUME — boarding (b) and climb/parkour (c) both pause the
        // pack's aim/shoot/cover trio rather than let it fight for control
        // while the body is seated or climbing. Movement itself was never
        // this class's to pause (FootController owns it unconditionally).
        // =====================================================================

        /// <summary>Core/GameLoop.cs's BoardSequence calls this: true right as
        /// a board-enter sequence begins (the walk+sit tween, then the seat,
        /// then flight, own the body), false once StageBoarding's climb-out
        /// completes and Mode returns to OnFoot.</summary>
        public void SetSeated(bool seated)
        {
            _suspendedByBoarding = seated;
            ApplySuspendState();
        }

        void Update()
        {
            if (_rig == null) return;
            bool parkour = _game != null && _game.Foot != null && (_game.Foot.Clips.IsPlaying || _game.Foot.Climb.Active);
            if (parkour != _suspendedByParkour)
            {
                _suspendedByParkour = parkour;
                ApplySuspendState();
            }
        }

        void ApplySuspendState()
        {
            bool suspend = _suspendedByBoarding || _suspendedByParkour;
            bool wasSuspended = !_basic.enabled;
            if (suspend == wasSuspended) return;
            SetBehavioursEnabled(!suspend);
        }

        void SetBehavioursEnabled(bool on)
        {
            if (_basic != null) _basic.enabled = on;
            if (_aim != null) _aim.enabled = on;
            if (_shoot != null) _shoot.enabled = on;
            if (_cover != null) _cover.enabled = on;
        }

        public AimBehaviour Aim => _aim;
        public ShootBehaviour Shoot => _shoot;
        public CoverBehaviour Cover => _cover;
    }
}
