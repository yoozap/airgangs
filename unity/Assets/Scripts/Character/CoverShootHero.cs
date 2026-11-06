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
            // THE PACK'S OWN ANIMATOR CONTROLLER (2026-11-02). Every SetBool/
            // SetFloat/SetTrigger the pack makes (Aim, Shooting, Weapon,
            // ChangeWeapon, Speed, H/V, Cover...) was a no-op because no
            // controller was ever assigned to the hero's Animator — the class
            // header above says so — so aim, shoot, reload and cover never
            // MOVED the body, only changed C# state. The user: "use our asset
            // for covering and shooting, i need same cool physics to take guns
            // in hands ... aim is not movin good". The hero is Humanoid (the
            // gate at the top of Attach), so the pack's humanoid clips retarget.
            // Root motion stays off (FootController owns the root).
            if (rig.Anim != null && rig.Anim.runtimeAnimatorController == null)
            {
                var ctrl = Resources.Load<RuntimeAnimatorController>("ThirdParty/CoverShoot/Animators/CharacterController");
                if (ctrl != null)
                {
                    rig.Anim.runtimeAnimatorController = ctrl;
                    rig.Anim.applyRootMotion = false;
                    Debug.Log($"COVERSHOOT controller assigned id={rig.Id} clips={ctrl.animationClips.Length}");
                }
                else Debug.LogWarning("COVERSHOOT controller missing: Resources/ThirdParty/CoverShoot/Animators/CharacterController");
            }

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
            // The IK pass (aim pitch / torso lean / blocked aim) lives on the
            // Animator's own object — see Character/CoverShootIkRelay.cs.
            if (rig.Anim != null)
            {
                var relay = rig.Anim.gameObject.GetComponent<CoverShootIkRelay>();
                if (relay == null) relay = rig.Anim.gameObject.AddComponent<CoverShootIkRelay>();
                relay.Target = _shoot;
                IkRelay = relay;
            }
            _shoot.facingNegativeZ = true; // same convention as _cover.facingSourceNegativeZ below
            // DYNAMIC now (2026-11-02, user: "i tried v to cover, and
            // nothing happend" — the OLD one-shot spawn-time scan left
            // `covermarkers built=0` on open tarmac and could never engage
            // again regardless of where the player walked). See
            // TickDynamicCover() below — it replaces BuildCoverMarkers()
            // entirely and runs every frame from Update() instead of once
            // here.
            int coverLayer = LayerMask.NameToLayer("Cover");
            int coverInvisLayer = LayerMask.NameToLayer("Cover Invisible");
            // Exclude Cover/Cover Invisible from the hero's OWN probe mask
            // — otherwise frame 2 onward, the dynamic marker THIS SAME
            // class just placed (on Cover Invisible) would be the nearest
            // thing FindCover sees, chasing its own shadow instead of the
            // real wall behind it. Character/FootCover.cs's `CoverMask` is
            // otherwise unused while UsePurchasedShooter is on (the JS V
            // key that would also touch it is gated off — Core/GameLoop.cs
            // ReadDiscreteKeys), so setting it once here is safe.
            int excludeMask = 0;
            if (coverLayer >= 0) excludeMask |= 1 << coverLayer;
            if (coverInvisLayer >= 0) excludeMask |= 1 << coverInvisLayer;
            if (_game.Foot != null) _game.Foot.Cover.CoverMask = ~excludeMask;
            CoverShootPickups.EnsureSpawned(game, rig);
            TbilisiFly.UI.Hud.CoverShootAmmoView.EnsureInstalled();
            TbilisiFly.UI.Hud.CoverShootPromptView.EnsureInstalled();

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
            // Root-at-feet fix (2026-11-02) — see CoverBehaviour.cs's own
            // `eyeHeight` field comment for the full "V does nothing"
            // chase: HasPossibleCover's wall-finding ray used to fire from
            // transform.position (our Root, at the FEET) and just clipped
            // the floor a few cm out. Chest height, not literal eye height,
            // is what a human actually presses against a wall at.
            _cover.eyeHeight = _rig.BodyHeight * 0.55f;

            // The hero's REAL facing — see CoverBehaviour.cs's facingSource
            // comment for the dead-FOV-gate story. FootController.cs:1428
            // writes the heading to rig.Root as Euler(0, Yaw, 0), but its
            // own heading vector is (-sin Yaw, 0, -cos Yaw), so the hero
            // faces Root's LOCAL -Z: hence the explicit flip. Root, never
            // Model — Model carries CharacterRig.cs:148's extra 180 deg
            // that only corrects the FBX art.
            _cover.facingSource = _rig.Root;
            _cover.facingSourceNegativeZ = true;

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
        // DYNAMIC COVER — rewritten 2026-11-02 (user: "i tried v to cover,
        // and nothing happend"; coordinator: the one-shot spawn-time scan
        // this used to be "is the design bug, not just a layer bug — make
        // cover detection DYNAMIC, evaluated from the hero's current
        // position every frame... reuse FootCover's wall-finding rather
        // than duplicating it"). TickDynamicCover() below is called from
        // Update() every frame: it asks Character/FootCover.cs's OWN
        // `FindCover` (the exact query src/foot.js's V key already uses to
        // locate a wall — raycast walls, nearest-first across a compass
        // sweep, no NavMesh) for whatever is in front of the hero RIGHT
        // NOW, then moves ONE reusable invisible marker onto it so
        // `CoverBehaviour.HasPossibleCover`/`CanTakeCover`/`StartTakeCover`
        // (the pack's own state machine, unchanged) have somewhere real to
        // engage — wherever the player actually is, not wherever they
        // happened to spawn. The README's documented technique ("you can
        // use a simpler mesh as a cover container... set its Layer as
        // Cover Invisible") is still how the marker itself is built; what
        // changed is WHEN and WHERE it gets placed.
        //
        // Planes excluded by name (not by `FootCover.ExtraTargets`, which
        // treats nearby parked-plane shells as valid cover for the JS path
        // — deliberately NOT reused here): a plane's hull sits on landing
        // gear, elevated above real ground, so earlier attempts at this
        // (the one-shot scan's own history, see git blame) proved it is
        // not safe to stand a player up against one. This system never
        // relocates the player's own Y at all — they walked here — so that
        // specific failure mode cannot recur, but the marker is still
        // rejected if the wall it would sit on turns out to be a plane,
        // for the same "not real cover" reason.
        // =====================================================================
        static bool IsPlaneShell(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == "plane_shell" || p.name == "cockpit_interior") return true;
            return false;
        }

        GameObject _coverMarker;
        BoxCollider _coverMarkerCol;

        // ---- THE PROMPT'S OWN DEBOUNCE (user, 2026-11-06: "when i am running,
        // i have blink v to cover message") -----------------------------------
        // The MARKER must follow the sweep frame by frame — V has to take the
        // wall the player is beside at the instant they press it. The PROMPT
        // must not: FootCover.FindCover is an 11-heading, 3.5 m raycast sweep
        // re-run every frame from a moving body, so running down a street past
        // doorways, gaps, parked cars and kerb gaps flips its answer several
        // times a second and the line strobed. Same predicate, asymmetric
        // debounce: it has to be true for ShowAfter before the line appears and
        // false for HideAfter (longer — it covers a doorway passing by) before
        // it goes away. Nothing here touches whether V WORKS.
        const float PromptShowAfter = 0.20f;
        const float PromptHideAfter = 0.55f;
        float _promptTrueT, _promptFalseT;
        bool _promptShown;

        /// <summary>Debounced prompt visibility — see the constants above.
        /// `raw` is this frame's honest answer from the cover sweep.</summary>
        void PromptCoverAvailable(bool raw, float dt)
        {
            if (raw) { _promptTrueT += dt; _promptFalseT = 0f; }
            else { _promptFalseT += dt; _promptTrueT = 0f; }

            bool want = _promptShown;
            if (!_promptShown && raw && _promptTrueT >= PromptShowAfter) want = true;
            else if (_promptShown && !raw && _promptFalseT >= PromptHideAfter) want = false;
            if (want == _promptShown) return;
            _promptShown = want;
            TbilisiFly.UI.Hud.CoverShootPromptView.Instance?.SetCoverAvailable(want);
        }

        /// <summary>Hide the line at once and forget the debounce — taking
        /// cover, boarding, a parkour move: the prompt is simply not the
        /// question any more, so it must not linger for HideAfter.</summary>
        void PromptCoverOff()
        {
            _promptTrueT = _promptFalseT = 0f;
            if (!_promptShown) return;
            _promptShown = false;
            TbilisiFly.UI.Hud.CoverShootPromptView.Instance?.SetCoverAvailable(false);
        }

        void EnsureCoverMarker()
        {
            if (_coverMarker != null) return;
            int coverInvis = LayerMask.NameToLayer("Cover Invisible");
            _coverMarker = new GameObject("CoverMarker_dynamic");
            if (coverInvis >= 0) _coverMarker.layer = coverInvis;
            _coverMarkerCol = _coverMarker.AddComponent<BoxCollider>();
            _coverMarkerCol.size = new Vector3(2.2f, 2.0f, 0.1f);
            _coverMarkerCol.isTrigger = false;
            _coverMarkerCol.enabled = false; // no wall found yet
        }

        /// <summary>Called every Update() while the pack owns the body
        /// (not boarding/parkour-suspended) and the player is not already
        /// covering (never touch the marker mid-session — CoverBehaviour
        /// caches `currentCoverObj`/`coverWall` off it).</summary>
        void TickDynamicCover(float dt)
        {
            EnsureCoverMarker();
            if (_cover != null && _cover.IsCovering)
            {
                PromptCoverOff();
                return;
            }

            Vector3 pos = _game.Foot.Pos;
            float yaw = _game.Foot.Yaw;
            var found = _game.Foot.Cover.FindCover(pos, yaw, true, _game.Foot.CamYaw, _game.Foot.Speed);
            if (found.HasValue)
            {
                // Confirm the SAME surface by name, straight-line from the
                // hero towards the found point (reconstructing FindCover's
                // own probe direction as -N, its own convention — see
                // FootCover.WallHitProbe) — FindCover's own FindResult does
                // not carry the collider, only the point/normal.
                Vector3 toWall = -found.Value.N; toWall.y = 0f;
                if (toWall.sqrMagnitude > 1e-6f)
                {
                    Vector3 origin = pos + Vector3.up * 1.1f;
                    if (Physics.Raycast(origin, toWall.normalized, out var confirm, found.Value.Dist + 0.5f)
                        && IsPlaneShell(confirm.collider.transform))
                        found = null;
                }
            }

            if (found.HasValue)
            {
                // FITTED TO THE REAL WALL (2026-11-02, "take the physics from the
                // bought asset"): the pack's corner peek fires where its cover
                // collider ENDS (CoverBehaviour.CheckForCorners' 1 m ray past the
                // shoulder) and its crouch/low-wall pop where the collider is
                // lower than the standing height (UpdateCoverParameters' ray at
                // col.height) — so a fixed 2.2 x 2.0 m marker gave a fake
                // corner 1.1 m along every wall and never a low wall. Measure
                // the facet instead: how far it runs each way at chest height
                // (same normal, same standoff) and how high it is, and size the
                // marker to that, so the pack's own rules see the real
                // geometry.
                Vector3 n = found.Value.N;
                Vector3 t = new Vector3(-n.z, 0f, n.x);
                Vector3 p = found.Value.Point; p.y = pos.y;
                float eR = WallExtent(p, n, t, +1f), eL = WallExtent(p, n, t, -1f);
                float top = WallTop(p, n);
                float width = (eR + eL) > 0.01f ? eR + eL + 0.5f : 2.2f;      // the facet ran to the last good sample each way (+0.25 beyond)
                float height = top > 0.01f ? Mathf.Clamp(top + 0.1f, 0.6f, 3.3f) : 2.0f;
                Vector3 markerPos = p - n * 0.05f + t * ((eR - eL) * 0.5f);
                // Y tracks the HERO's own current (already-grounded) height,
                // never the wall-probe's — the one-shot scan's entire
                // failure mode was trusting a remembered Y from a probe
                // struck who-knows-how-far away; the player is standing
                // right here, on real ground, by construction.
                markerPos.y = pos.y + height * 0.5f;
                _coverMarker.transform.position = markerPos;
                _coverMarker.transform.rotation = Quaternion.LookRotation(n);
                _coverMarkerCol.size = new Vector3(width, height, 0.1f);
                _coverMarkerCol.enabled = true;
                LastFit = (eL, eR, top);
                PromptCoverAvailable(true, dt);
            }
            else
            {
                _coverMarkerCol.enabled = false;
                PromptCoverAvailable(false, dt);
            }
        }

        /// <summary>The last marker fit: facet run left/right of the found point and its top (m) — tests read it.</summary>
        public (float Left, float Right, float Top) LastFit;

        static readonly RaycastHit[] _fitHits = new RaycastHit[16];
        /// <summary>Nearest non-hero hit along the probe; true only if it is the
        /// same facet (normal within ~37 deg of n) at roughly the expected standoff.</summary>
        bool SameFacet(Vector3 origin, Vector3 dir, float far, Vector3 n, float expectDist)
        {
            int mask = _game.Foot.Cover.CoverMask; // Cover / Cover Invisible already excluded (Setup)
            int k = Physics.RaycastNonAlloc(origin, dir, _fitHits, far, mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Vector3 bestN = Vector3.zero;
            for (int i = 0; i < k; i++)
            {
                var h = _fitHits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(transform) || IsPlaneShell(h.collider.transform)) continue;
                if (h.distance < best) { best = h.distance; bestN = h.normal; }
            }
            return best < float.MaxValue && Vector3.Dot(bestN, n) > 0.8f && Mathf.Abs(best - expectDist) <= 0.35f;
        }

        float WallExtent(Vector3 p, Vector3 n, Vector3 t, float sgn)
        {
            float e = 0f;
            for (float d = 0.25f; d <= 8f; d += 0.25f)
            {
                if (!SameFacet(p + n * 0.6f + t * (sgn * d) + Vector3.up * 1.0f, -n, 1.2f, n, 0.6f)) break;
                e = d;
            }
            return e;
        }

        float WallTop(Vector3 p, Vector3 n)
        {
            float top = 0f;
            for (float h = 0.4f; h <= 3.2f; h += 0.2f)
            {
                if (SameFacet(p + n * 0.6f + Vector3.up * h, -n, 1.2f, n, 0.6f)) top = h;
                else if (h > 0.5f) break;
            }
            return top;
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
            // SEATED FOLLOWS THE REAL MODE (2026-11-02). SetSeated(true) fires when
            // a board sequence STARTS and SetSeated(false) only on a COMPLETED
            // climb-out, so any spawn/resume that puts the hero on foot without
            // that sequence (the saved "ON FOOT" spot, a crash respawn) left
            // _suspendedByBoarding stuck true: player log
            // "COVERSHOOT suspend=on boarding=true parkour=false" with the hero
            // walking around — the whole pack off, so no pickup, aim or shot.
            if (_game != null)
            {
                bool seatedNow = _game.Mode != Core.GameMode.OnFoot || _game.Boarding;
                if (seatedNow != _suspendedByBoarding) { _suspendedByBoarding = seatedNow; ApplySuspendState(); }
            }
            bool parkour = _game != null && _game.Foot != null && (_game.Foot.Clips.IsPlaying || _game.Foot.Climb.Active);
            if (parkour != _suspendedByParkour)
            {
                _suspendedByParkour = parkour;
                ApplySuspendState();
            }

            bool suspended = _suspendedByBoarding || _suspendedByParkour;
            // SELF-HEAL (2026-11-02): the four behaviours must be enabled whenever
            // not suspended. ApplySuspendState keys its early-return off
            // _basic.enabled alone, so any path that left ONE of them off stayed
            // off forever — and ShootBehaviour.Start never ran (player log:
            // NullReferenceException at ShootBehaviour.WeaponInSlot on every F).
            if (!suspended && _shoot != null && (!_shoot.enabled || !_aim.enabled || !_cover.enabled || !_basic.enabled))
            {
                SetBehavioursEnabled(true);
                Debug.Log($"COVERSHOOT self-heal: behaviours re-enabled (shoot={_shoot.enabled.ToString().ToLowerInvariant()})");
            }
            if (!suspended && _game != null && _game.Foot != null) TickDynamicCover(Time.deltaTime);
            else if (suspended) PromptCoverOff();
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
            Debug.Log($"COVERSHOOT suspend={(on ? "off" : "on")} boarding={_suspendedByBoarding.ToString().ToLowerInvariant()} parkour={_suspendedByParkour.ToString().ToLowerInvariant()}");
            if (!on) CoverShootInput.ClearAimLatch(); // a latched aim must not survive the seat/climb that suspended the pack
            if (_basic != null) _basic.enabled = on;
            if (_aim != null) _aim.enabled = on;
            if (_shoot != null) _shoot.enabled = on;
            if (_cover != null) _cover.enabled = on;
        }

        public AimBehaviour Aim => _aim;
        public CoverShootIkRelay IkRelay { get; private set; }
        /// <summary>The live dynamic cover marker's box (world), for the cover camera plane and tests.</summary>
        public BoxCollider CoverMarker => _coverMarkerCol != null && _coverMarkerCol.enabled ? _coverMarkerCol : null;
        public ShootBehaviour Shoot => _shoot;
        public CoverBehaviour Cover => _cover;
    }
}
