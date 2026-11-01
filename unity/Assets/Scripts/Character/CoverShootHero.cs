using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Assembles the purchased Asset Store "Cover + Shooting System" (Vinicius
    /// Marques) onto the hero's own CharacterRig when
    /// <see cref="Core.GameLoop.UsePurchasedShooter"/> is true (user decision,
    /// 2026-11-01: "shooting u can move new one from our assets ... i dont need
    /// old gun reload shooting from js"). Replaces the JS-ported on-foot
    /// shooting/aim/cover/weapon path wholesale — see Core/GameLoop.cs's
    /// StageOnFoot/StageOnFootPurchased split and Core/HeldWeapon.cs's own
    /// `UsePurchasedShooter` early-return.
    ///
    /// ================================ WHERE THE COMPONENTS LIVE =================
    /// The pack's `BasicBehaviour`/`MoveBehaviour`/`AimBehaviour`/
    /// `ShootBehaviour`/`CoverBehaviour` + `Rigidbody` + `CapsuleCollider` all go
    /// on `rig.Root.gameObject` — NOT on `rig.Model` (the Animator's own
    /// GameObject, 3 empty transforms below Root: Root/act/anim/Model, see
    /// CharacterRig.cs's header on ActGroup/AnimGroup). Root is already what
    /// every other system in this project reads (`GameLoop.Foot.Pos`/
    /// `Rig.Root.position`, `ToggleBoard`'s reach checks, `UpdateZones`), so
    /// putting the Rigidbody there means every one of those call sites keeps
    /// working with ZERO follow-sync code — Model just rides along as Root's
    /// ordinary visual descendant, exactly as it always has.
    ///
    /// The one price: `BasicBehaviour.Awake` was edited
    /// (ThirdParty/CoverShoot/Scripts/PlayerScripts/BasicBehaviour.cs) to find
    /// the Animator with `GetComponentInChildren` instead of `GetComponent`,
    /// since it does not live on the same object. The one thing that costs:
    /// `ShootBehaviour.OnAnimatorIK` never fires (Unity only calls it on a
    /// component sharing the Animator's own GameObject), so the fine
    /// torso-lean IK pass is inert — `AimBehaviour`'s whole-body turn
    /// (`Rotating()`) still works and is what actually turns the hero to face
    /// the camera/travel direction.
    ///
    /// ================================ AWAKE-ORDERING =============================
    /// Every one of the 5 behaviours reads a cross-reference in `Awake()`
    /// (`BasicBehaviour.playerCamera.GetComponent&lt;ThirdPersonOrbitCam&gt;()`,
    /// `GenericBehaviour.GetComponent&lt;BasicBehaviour&gt;()`) — fields that can
    /// only be assigned AFTER `AddComponent` returns, which is already too
    /// late because `AddComponent` fires `Awake` synchronously on an ACTIVE
    /// GameObject. So both the hero root and the camera are built while
    /// temporarily INACTIVE (Awake is deferred until `SetActive(true)`), wired,
    /// THEN activated — see <see cref="BuildCamera"/>/<see cref="Setup"/>.
    /// </summary>
    public sealed class CoverShootHero : MonoBehaviour
    {
        public static CoverShootHero Instance { get; private set; }

        Core.GameLoop _game;
        CharacterRig _rig;
        Rigidbody _rb;
        CapsuleCollider _capsule;
        BasicBehaviour _basic;
        MoveBehaviour _move;
        AimBehaviour _aim;
        ShootBehaviour _shoot;
        CoverBehaviour _cover;
        ThirdPersonOrbitCam _orbitCam;
        RuntimeAnimatorController _controller;

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

            Debug.Log($"COVERSHOOT attach id={rig.Id} humanoid=1 bodyHeight={rig.BodyHeight:F2} " +
                      $"controller={(_controller != null ? 1 : 0)}");
        }

        void BuildPhysics()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = 75f;
            _rb.freezeRotation = true; // the pack rotates via MoveRotation itself, not physics torque
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule == null) _capsule = gameObject.AddComponent<CapsuleCollider>();
            float h = Mathf.Max(1.2f, _rig.BodyHeight);
            _capsule.height = h;
            _capsule.radius = Mathf.Clamp(h * 0.16f, 0.22f, 0.32f);
            _capsule.center = new Vector3(0f, h * 0.5f, 0f); // Root sits at the feet/ground point (CharacterRig.NormalizeHeight)
            // PhysicMaterial/PhysicMaterialCombine are obsolete in this Unity
            // version (renamed PhysicsMaterial/PhysicsMaterialCombine).
            if (_capsule.material == null)
                _capsule.material = new PhysicsMaterial("CoverShootChar") { dynamicFriction = 0.6f, staticFriction = 0.6f, frictionCombine = PhysicsMaterialCombine.Average };
        }

        void BuildBehaviours()
        {
            _basic = GetComponent<BasicBehaviour>() ?? gameObject.AddComponent<BasicBehaviour>();
            _move = GetComponent<MoveBehaviour>() ?? gameObject.AddComponent<MoveBehaviour>();
            _aim = GetComponent<AimBehaviour>() ?? gameObject.AddComponent<AimBehaviour>();
            _shoot = GetComponent<ShootBehaviour>() ?? gameObject.AddComponent<ShootBehaviour>();
            _cover = GetComponent<CoverBehaviour>() ?? gameObject.AddComponent<CoverBehaviour>();

            _basic.sprintButton = "CS_Sprint";
            _move.jumpButton = "Jump"; // default Input Manager axis, unchanged
        }

        void BuildCamera()
        {
            if (_game.MainCamera == null) return;
            var camGo = _game.MainCamera.gameObject;
            _orbitCam = camGo.GetComponent<ThirdPersonOrbitCam>();
            bool firstTime = _orbitCam == null;
            if (firstTime) camGo.SetActive(false); // defer Awake until `player` is set below

            if (firstTime) _orbitCam = camGo.AddComponent<ThirdPersonOrbitCam>();
            _orbitCam.player = _rig.Root;
            _orbitCam.XAxis = "CS_AnalogX";
            _orbitCam.YAxis = "CS_AnalogY";

            if (firstTime) camGo.SetActive(true);
            _basic.playerCamera = _game.MainCamera.transform;
        }

        void WireShootFields()
        {
            _controller = Resources.Load<RuntimeAnimatorController>("ThirdParty/CoverShoot/Animators/CharacterController");
            if (_controller != null && _rig.Anim != null) _rig.Anim.runtimeAnimatorController = _controller;

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
            Vector3[] dirs = { transform.forward, -transform.forward, transform.right, -transform.right };
            int coverInvis = LayerMask.NameToLayer("Cover Invisible");
            if (coverInvis < 0) return;
            foreach (var d in dirs)
            {
                Vector3 origin = transform.position + Vector3.up * 1f;
                if (Physics.Raycast(origin, d, out var hit, 5f) && hit.collider != null && hit.collider.gameObject != gameObject)
                {
                    var marker = new GameObject("CoverMarker_" + hit.collider.name);
                    marker.layer = coverInvis;
                    marker.transform.position = hit.point - d.normalized * 0.05f;
                    marker.transform.rotation = Quaternion.LookRotation(hit.normal);
                    var box = marker.AddComponent<BoxCollider>();
                    box.size = new Vector3(2.2f, 2.0f, 0.1f);
                    box.isTrigger = false;
                    // Static per the README ("remember to set all cover objects
                    // on the scene as Static") — StaticEditorFlags is editor-
                    // only; a runtime-spawned collider has no batching to lose.
                }
            }
        }

        // =====================================================================
        // SUSPEND / RESUME — boarding (b) and climb/parkour (c) both pause the
        // pack rather than fight it for the body, per the task brief.
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
            // Climb.Active flips true the instant TryStart succeeds (the
            // "reach" beat, before any clip is chosen) — Clips.IsPlaying
            // alone misses that opening window, which is exactly when
            // FootController.TickClimbAndFreeRun starts writing Pos/Yaw.
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
            if (_rb != null) _rb.isKinematic = suspend;

            if (!suspend)
            {
                // Reclaim the Animator's output from whichever external
                // PlayableGraph was driving it (ParkourClipPlayer.Play's own
                // AnimationPlayableOutput.Create steals it — see that file's
                // header). Null/reassign forces Unity to rebuild the
                // AnimatorController's internal graph and rebind it as the
                // Animator's output; Rebind() is the documented follow-up.
                if (_rig.Anim != null && _controller != null)
                {
                    _rig.Anim.runtimeAnimatorController = null;
                    _rig.Anim.runtimeAnimatorController = _controller;
                    _rig.Anim.Rebind();
                }
            }
        }

        void SetBehavioursEnabled(bool on)
        {
            if (_basic != null) _basic.enabled = on;
            if (_move != null) _move.enabled = on;
            if (_aim != null) _aim.enabled = on;
            if (_shoot != null) _shoot.enabled = on;
            if (_cover != null) _cover.enabled = on;
            if (_orbitCam != null) _orbitCam.enabled = on;
        }

        public bool IsArmed => _shoot != null; // weapon-slot query lives on ShootBehaviour's own private inventory; exposed state is via WeaponUIManager's bridge events (UI/Hud/CoverShootAmmoView.cs)
    }
}
