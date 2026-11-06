using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Character; // CoverShootInput — see that file's header
using TbilisiFly.Combat;

// ShootBehaviour inherits from GenericBehaviour. This class corresponds to shoot/reload/change/add/drop weapons behaviour.
// Due to its characteristics, this behaviour will always be called regardless the current state (including active and overriding ones).
// There is no need to use behaviour manager to watch it. Use direct call to all the MonoBehaviour basic functions.
public class ShootBehaviour : GenericBehaviour
{
	public string shootButton = "Fire1",                           // Default shoot weapon button.
		pickButton = "Interact",                                   // Default pick weapon button.
		changeButton = "Change",                                   // Default change weapon button.
		reloadButton = "Reload",                                   // Default reload weapon button.
		dropButton = "Drop";                                       // Default drop weapon button.
	public Texture2D aimCrosshair, shootCrosshair;                 // Crosshair textures for aiming and shooting.
	public GameObject muzzleFlash, shot, sparks;                   // Game objects for shot effects.
	public Material bulletHole;                                    // Material for the bullet hole placed on target shot.
	public int maxBulletHoles = 50;                                // Max bullet holes to draw on scene.
	public float shotErrorRate = 0.01f;                            // Shooting error margin. 0 is most accurate.
	public float shotRateFactor = 1f;                              // Rate of fire parameter. Higher is faster rate.
	public float armsRotation = 8f;                                // Rotation of arms to align with aim, according player height.
	// Resolved BY NAME, not hardcoded bit literals: the stock pack assumed
	// "Cover"=8/"Cover Invisible"=10 (see CoverBehaviour.cs's own comment),
	// but this project's TagManager.asset ended up with Cover=8/Cover
	// Invisible=9 -- a mismatch that silently broke cover detection
	// (SphereCast against coverMask never saw the markers). GetMask tracks
	// whatever TagManager.asset actually resolves to, so it can't drift
	// out of sync with coverMask below again. No "Player" layer exists in
	// this project (Player is a TAG here, not a layer), so the stock
	// mask's 4th bit (11) is dropped rather than guessed at.
	//
	// NOT computed in this field initializer: confirmed by the
	// shootdiag_ray log (2026-11-01) that LayerMask.GetMask() called from a
	// MonoBehaviour field initializer returns -1 here (native layer table
	// isn't attached yet at that point in construction), so `~(-1)` landed
	// shotMask on 0 — a mask that matches NOTHING, silently making every
	// shot a guaranteed miss. Assigned in Start() instead, see below.
	public LayerMask shotMask;
	public LayerMask organicMask;                                  // Layer mask to define organic matter.

	[Header("Advanced Rotation Adjustments")]
	public Vector3 LeftArmShortAim;                                // Local left arm rotation when aiming with a short gun.
	public Vector3 RightHandCover;                                 // Local right hand rotation when holding a long gun and covering.
	public Vector3 LeftArmLongGuard;                               // Local left arm rotation when guarding with a long gun.

	private int activeWeapon = 0;                                  //  Index of the active weapon.
	// TbilisiFly glue: no AnimatorController is assigned to rig.Anim under
	// the purchased-shooter path (see Character/CoverShootHero.cs's class
	// header), so gameplay state that used to be read off an Animator bool
	// is exposed as plain C# properties instead.
	private bool _reloading;                                      // Mirrors reloadBool's SetBool calls below — see IsReloading.
	private float _pendingFireUntil;                               // TbilisiFly glue: a click that arrived before aim engaged — fires as soon as AimBehaviour reports aiming (see Update).
	private float _reloadTimer;                                   // Counts down to EndReloadWeapon() — see Update()'s own note.
	const float ReloadSeconds = 1.6f;                              // A reasonable pistol/rifle reload duration (no animation length to read without a controller).
	public bool HasWeapon => activeWeapon > 0;
	// ---- TbilisiFly glue (2026-11-02): the JS belt/back stow + the on-foot bar ----
	// StowHook: asked when a weapon leaves the hand; return true to PARK it
	// (belt holster / back sling, renderers on) instead of the stock
	// SetActive(false). DrawHook: told when a weapon goes back to the hand so
	// the stow can let go of it. Wired by Core/CombatWorldAdapter.cs to
	// Core/HeldWeapon.cs's StowPack/DrawPack.
	public static System.Func<InteractiveWeapon, bool> StowHook;
	public static System.Action<InteractiveWeapon> DrawHook;
	public InteractiveWeapon ActiveWeapon => (weapons != null && activeWeapon > 0 && activeWeapon < weapons.Count) ? weapons[activeWeapon] : null;
	public InteractiveWeapon WeaponInSlot(int slot) { EnsureInit(); return (weapons != null && slot > 0 && slot < weapons.Count) ? weapons[slot] : null; }
	public bool IsReloadingNow => _reloading;
	/// <summary>Non-toggling slot select for the project's own bar/digit keys
	/// (skillbar.js:312 draw / :309 holster): 0 = bare hands, 1 = SHORT, 2 = LONG.
	/// The toggle itself lives in UI/SkillBar/WeaponBarView.Select.</summary>
	public void SelectSlot(int slot)
	{
		EnsureInit();
		if (weapons == null || slot < 0 || slot >= weapons.Count) return;
		if (slot == 0) { if (activeWeapon != 0) ChangeWeapon(activeWeapon, 0); return; }
		if (weapons[slot] == null || activeWeapon == slot) return;
		ChangeWeapon(activeWeapon, slot);
	}                     // Core/GameLoop.cs's StageOnFootPurchased feeds this into _foot.Armed.
	public bool IsReloading => _reloading;
	private int weaponTypeInt;                                     // Animator variable related to the weapon type.
	private int changeWeaponTrigger;                               // Animator trigger for changing weapon.
	private int shootingTrigger;                                   // Animator trigger for shooting weapon.
	private List<InteractiveWeapon> weapons;                       // Weapons inventory.
	private int coveringBool, aimBool,                             // Animator variables related to covering and aiming.
		blockedAimBool,                                            // Animator variable related to blocked aim.
		reloadBool;                                                // Animator variable related to reloading.
	private bool isAiming,                                         // Boolean to get whether or not the player is aiming.
		isAimBlocked;                                              // Boolean to determine whether or not the aim is blocked.
	private Transform gunMuzzle;                                   // World position of the gun muzzle.
	private float distToHand;                                      // Distance from neck to hand.
	private Vector3 castRelativeOrigin;                            // Position of neck to cast for blocked aim test.
	private Dictionary<InteractiveWeapon.WeaponType, int> slotMap; // Map to designate weapon types to inventory slots.
	private Transform hips, spine, chest, rightHand, leftArm;      // Avatar bone transforms.
	private Transform neckBone;                                    // TbilisiFly glue: live neck for the blocked-aim cast (was a frozen world offset).
	// TbilisiFly glue (2026-11-02): this rig FACES ROOT-LOCAL -Z (FootController
	// heading = (-sin yaw, 0, -cos yaw); CoverBehaviour.facingSourceNegativeZ
	// says the same), so the body's RIGHT is -transform.right. The aim-pitch
	// IK below rotates the chest about the body's right axis — with the raw
	// transform.right it pitched the torso UP when the camera looked DOWN.
	public bool facingNegativeZ;
	/// <summary>Aim-pitch IK applications (proof the pass is reached — Character/CoverShootIkRelay.cs).</summary>
	public int IkApplied;
	public bool IsAimBlocked => isAimBlocked;
	private Vector3 initialRootRotation;                           // Initial root bone local rotation.
	private Vector3 initialHipsRotation;                           // Initial hips rotation related to the root bone.
	private Vector3 initialSpineRotation;                          // Initial spine rotation related to the hips bone.
	private Vector3 initialChestRotation;                          // Initial chest rotation related to the spine bone.
	private float shotDecay, originalShotDecay = 0.5f;             // Default shot lifetime. Use shotRateFactor to modify speed.
	private List<GameObject> bulletHoles;                          // Bullet holes scene buffer.
	private int bulletHoleSlot = 0;                                // Number of active bullet holes on scene.
	private int burstShotCount = 0;                                // Number of burst shots fired.
	private AimBehaviour aimBehaviour;                             // Reference to the aim behaviour.
	private Texture2D originalCrosshair;                           // Original unarmed aim behaviour crosshair.
	private bool isShooting = false;                               // Boolean to determine if player is holding shoot button.
	private bool isChangingWeapon = false;                         // Boolean to determine if player is holding change weapon button.
	private bool isShotAlive = false;                              // Boolean to determine if there is any active shot on scene.

	// Start is always called after any Awake functions.
	// TbilisiFly glue (2026-11-02): Start() only runs once the component is
	// ENABLED, and CoverShootHero suspends these behaviours (boarding/parkour)
	// — so a weapon handed over before the first enable hit a null `weapons`
	// list: NullReferenceException at WeaponInSlot, every F press, take
	// aborted (player log). The stock Start body is InitOnce() below, guarded,
	// and every public entry point calls EnsureInit() first.
	bool _inited;
	public bool Ready => _inited && weapons != null;
	void Start() { EnsureInit(); }
	void EnsureInit()
	{
		if (_inited) return;
		if (behaviourManager == null) behaviourManager = GetComponent<BasicBehaviour>();
		if (behaviourManager == null || behaviourManager.GetAnim == null) return;
		if (aimBehaviour == null) aimBehaviour = GetComponent<AimBehaviour>();
		_inited = true;
		InitOnce();
	}
	void InitOnce()
	{
		// Set up the references.
		shotMask = ~(LayerMask.GetMask("Ignore Raycast", "Cover", "Cover Invisible"));
		weaponTypeInt = Animator.StringToHash("Weapon");
		aimBool = Animator.StringToHash("Aim");
		coveringBool = Animator.StringToHash("Cover");
		blockedAimBool = Animator.StringToHash("BlockedAim");
		changeWeaponTrigger = Animator.StringToHash("ChangeWeapon");
		shootingTrigger = Animator.StringToHash("Shooting");
		reloadBool = Animator.StringToHash("Reload");
		weapons = new List<InteractiveWeapon>(new InteractiveWeapon[3]);
		aimBehaviour = this.GetComponent<AimBehaviour>();
		bulletHoles = new List<GameObject>();

		// Hide shot effects on scene.
		if (muzzleFlash != null) muzzleFlash.SetActive(false);
		if (shot != null) shot.SetActive(false);
		if (sparks != null) sparks.SetActive(false);

		// Create weapon slots. Different weapon types can be added in the same slot - ex.: (LONG_SPECIAL, 2) for a rocket launcher.
		slotMap = new Dictionary<InteractiveWeapon.WeaponType, int>
		{
			{ InteractiveWeapon.WeaponType.SHORT, 1 },
			{ InteractiveWeapon.WeaponType.LONG, 2 }
		};

		// Get player's avatar bone transforms for IK.
		Transform neck = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Neck);
		if (!neck)
		{
			neck = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Head).parent;
		}
		neckBone = neck;
		hips = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Hips);
		spine = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Spine);
		chest = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Chest);
		if (chest == null) chest = spine; // a short spine chain: pitch the spine itself
		rightHand = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.RightHand);
		leftArm = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
		Transform root = hips.parent;

		// Correctly set the hip and root bones.
		if (spine.parent != hips)
		{
			root = hips;
			hips = spine.parent;
		}

		// Get initial values.
		initialRootRotation = (root == transform) ? Vector3.zero : root.localEulerAngles;
		initialHipsRotation = hips.localEulerAngles;
		initialSpineRotation = spine.localEulerAngles;
		initialChestRotation = chest.localEulerAngles;
		originalCrosshair = aimBehaviour != null ? aimBehaviour.crosshair : null;
		shotDecay = originalShotDecay;
		castRelativeOrigin = neck.position - this.transform.position;
		distToHand = (rightHand.position - neck.position).magnitude * 1.5f;
	}

	// Update is used to set features regardless the active behaviour.
	private void Update()
	{
		// Handle shoot weapon action.
		if (CoverShootInput.GetAxisRaw(shootButton) != 0 && !isShooting && activeWeapon > 0 && burstShotCount == 0)
		{
			if (!isAiming)
			{
				// TbilisiFly glue (2026-11-02, user: "i dont see aim and i cant
				// shoot"): the stock pack SWALLOWS a click made with a drawn gun
				// unless RMB is held — ShootWeapon's `!isAiming` gate returned
				// silently and the player saw nothing. The JS fires on click and
				// enters aim on click (foot.js:2480 _aimOnDraw "on click we are in
				// aim mode"), so: switch the shared aim latch on (the same one Alt
				// and RMB toggle — Character/CoverShootInput.cs) and remember the
				// press; the shot goes the moment AimBehaviour reports aiming
				// (ToggleAimOn's 0.05 s), below, instead of being lost.
				if (!CoverShootInput.AimLatched) { CoverShootInput.SetAimLatch(true); LogShot("aim_on_click"); }
				_pendingFireUntil = Time.time + 0.6f;
			}
			else
			{
				isShooting = true;
				ShootWeapon(activeWeapon);
			}
		}
		else if (isShooting && CoverShootInput.GetAxisRaw(shootButton) == 0)
		{
			isShooting = false;
		}
		// Handle reload weapon action.
		else if (CoverShootInput.GetButtonUp(reloadButton) && activeWeapon > 0)
		{
			if (weapons[activeWeapon].StartReload())
			{
				if (weapons[activeWeapon].reloadSound != null) AudioSource.PlayClipAtPoint(weapons[activeWeapon].reloadSound, gunMuzzle.position, 0.5f);
				behaviourManager.GetAnim.SetBool(reloadBool, true);
				_reloading = true;
				_reloadTimer = ReloadSeconds;
			}
		}
		// Handle drop weapon action.
		else if (CoverShootInput.GetButtonDown(dropButton) && activeWeapon > 0)
		{
			// End reload parameters, drop weapon and change to another one in inventory.
			EndReloadWeapon();
			int weaponToDrop = activeWeapon;
			ChangeWeapon(activeWeapon, 0);
			weapons[weaponToDrop].Drop();
			weapons[weaponToDrop] = null;
		}
		// Handle change weapon action.
		else
		{
			if ((CoverShootInput.GetAxisRaw(changeButton) != 0 && !isChangingWeapon))
			{
				isChangingWeapon = true;
				int nextWeapon = activeWeapon + 1;
				ChangeWeapon(activeWeapon, (nextWeapon) % weapons.Count);
			}
			else if (CoverShootInput.GetAxisRaw(changeButton) == 0)
			{
				isChangingWeapon = false;
			}
		}

		// Manage shot parameters after shooting action.
		if (isShotAlive)
			ShotDecay();

		// TbilisiFly glue: was `behaviourManager.GetAnim.GetBool(aimBool)` —
		// with no AnimatorController assigned to rig.Anim (see Character/
		// CoverShootHero.cs's class header), Animator.GetBool has no
		// parameter table to read and always returns false, so `isAiming`
		// never went true and ShootWeapon's very first check
		// (`if (!isAiming ...) return;`) silently blocked every shot. Read
		// AimBehaviour's own C# state directly instead.
		isAiming = aimBehaviour != null && aimBehaviour.IsAiming;

		// TbilisiFly glue: the click-to-aim shot (see the shoot branch above).
		if (_pendingFireUntil > 0f)
		{
			if (isAiming && activeWeapon > 0 && burstShotCount == 0 && !_reloading)
			{
				_pendingFireUntil = 0f;
				isShooting = true; // the chain above releases it once the button is up
				ShootWeapon(activeWeapon);
			}
			else if (Time.time > _pendingFireUntil || activeWeapon == 0) _pendingFireUntil = 0f;
		}

		// Auto-complete reload on a timer — EndReloadWeapon() is "called by
		// animation" in the stock asset (an AnimationEvent on the reload
		// clip), which never fires with no AnimatorController driving one.
		if (_reloading)
		{
			_reloadTimer -= Time.deltaTime;
			if (_reloadTimer <= 0f) EndReloadWeapon();
		}
	}

	// Shoot the weapon.
	private void ShootWeapon(int weapon, bool firstShot = true)
	{
		// Check conditions to shoot. weapons[weapon].Shoot(firstShot) is
		// SIDE-EFFECTING (decrements mag) — this preserves the ORIGINAL
		// short-circuit so it is still called at most once, and only when
		// the other gates already pass (never called while blocked/
		// reloading/not aiming, exactly like the stock `||` chain below).
		bool blocked = !isAiming || isAimBlocked || _reloading;
		bool weaponOk = !blocked && weapons[weapon].Shoot(firstShot);
		if (blocked || !weaponOk)
		{
			// Player-side proof line: the next Player.log says WHY a click did nothing.
			if (firstShot) LogShot(blocked ? (!isAiming ? "blocked_not_aiming" : isAimBlocked ? "blocked_aim_obstructed" : "blocked_reloading") : "empty_mag");
			return;
		}

		// Update parameters: burst count, trigger for animation, crosshair change and recoil camera bounce.
		burstShotCount++;
		behaviourManager.GetAnim.SetTrigger(shootingTrigger);
		aimBehaviour.crosshair = shootCrosshair;
		behaviourManager.GetCamScript?.BounceVertical(weapons[weapon].recoilAngle);
		TbilisiFly.Combat.CoverShootBridge.CameraKick?.Invoke(weapons[weapon].recoilAngle); // TbilisiFly glue: our camera takes the pack's recoil

		// Cast the shot to find a target.
		Vector3 imprecision = Random.Range(-shotErrorRate, shotErrorRate) * behaviourManager.playerCamera.right;
		Ray ray = new Ray(behaviourManager.playerCamera.position, behaviourManager.playerCamera.forward + imprecision);
		// Target was hit.
		bool rayHit = RaycastSkippingSelf(ray, out RaycastHit hit, 500f);
		if (rayHit)
		{
			if (hit.collider.transform != transform)
			{
				// Is the target organic?
				bool isOrganic = (organicMask == (organicMask | (1 << hit.transform.root.gameObject.layer)));
				// Handle shot effects on target.
				DrawShoot(hit.point, hit.normal, hit.collider.transform, !isOrganic, !isOrganic);

				// Call the damage behaviour of target if exists.
				if(hit.collider)
					hit.collider.SendMessageUpwards("HitCallback", new HealthManager.DamageInfo(
						hit.point, ray.direction, weapons[weapon].bulletDamage, hit.collider), SendMessageOptions.DontRequireReceiver);

				// TbilisiFly glue: route the hit into the project's OWN building
				// damage world (Combat/CoverShootBridge.cs) so HP/crumble/glass/
				// holes react exactly like the belly gun or a rocket — see that
				// file's header. Added on top of the stock SendMessageUpwards
				// call above, which this project has no HealthManager receivers
				// for and leaves untouched. (Re-applied 2026-11-01 — a
				// concurrent import pass in this same session once reverted
				// this file to the stock pack source; verify this line survives
				// before calling the damage hook done.)
				TbilisiFly.Combat.CoverShootBridge.DamageWorld?.DamageHit(hit, weapons[weapon].bulletDamage);
			}
		}
		// No target was hit.
		else
		{
			Vector3 destination = (ray.direction * 500f) + ray.origin;
			// Handle shot effects without a specific target.
			DrawShoot(destination, Vector3.up, null, false, false);
		}
		// Play shot sound.
		if (weapons[weapon].shotSound != null) AudioSource.PlayClipAtPoint(weapons[weapon].shotSound, gunMuzzle.position, 5f);
		// Trigger alert callback
		GameObject.FindGameObjectWithTag("GameController").SendMessage("RootAlertNearby", ray.origin, SendMessageOptions.DontRequireReceiver);
		// Reset shot lifetime.
		shotDecay = originalShotDecay;
		isShotAlive = true;
		if (firstShot) LogShot(rayHit ? $"fired hit={hit.collider.name} dist={hit.distance:F1}" : "fired miss");
	}

	// TbilisiFly glue (2026-11-02): the project's chase camera sits behind and
	// above the hero, so the camera-forward shot ray can start INSIDE the hero's
	// own colliders (the pack's kinematic capsule on rig.Root, the body and the
	// belt/back props) — the stock `hit.collider.transform != transform` only
	// excluded the root object itself, so such a shot "hit" the player's own
	// back. Same fix class as Character/ParkourMoves.IgnoreRoot: the nearest
	// hit that is not under this transform.
	static readonly RaycastHit[] _shotHits = new RaycastHit[24];
	bool RaycastSkippingSelf(Ray ray, out RaycastHit best, float dist)
	{
		int n = Physics.RaycastNonAlloc(ray, _shotHits, dist, shotMask);
		best = default; float bestD = float.MaxValue; bool found = false;
		for (int i = 0; i < n; i++)
		{
			var h = _shotHits[i];
			if (h.collider == null || h.collider.transform.IsChildOf(this.transform)) continue;
			if (h.distance < bestD) { bestD = h.distance; best = h; found = true; }
		}
		return found;
	}

	// One SHOT line per trigger pull (not per auto round), no stack trace —
	// the player-side evidence this port's logs are read for.
	void LogShot(string what)
	{
		string label = (weapons != null && activeWeapon > 0 && activeWeapon < weapons.Count && weapons[activeWeapon] != null) ? weapons[activeWeapon].label : "<none>";
		Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, this, "SHOT weapon={0} aiming={1} latch={2} {3}", label, isAiming ? 1 : 0, CoverShootInput.AimLatched ? 1 : 0, what);
	}

	// Manage the shot visual effects.
	private void DrawShoot(Vector3 destination, Vector3 targetNormal, Transform parent,
		bool placeSparks = true, bool placeBulletHole = true)
	{
		Vector3 origin = gunMuzzle.position - gunMuzzle.right * 0.5f;

		// Draw the flash at the gun muzzle position.
		muzzleFlash.SetActive(true);
		muzzleFlash.transform.SetParent(gunMuzzle);
		muzzleFlash.transform.localPosition = Vector3.zero;
		muzzleFlash.transform.localEulerAngles = Vector3.back * 90f;

		// Create the shot tracer and smoke trail particle.
		GameObject instantShot = Instantiate(shot);
		instantShot.SetActive(true);
		instantShot.transform.position = origin;
		instantShot.transform.rotation = Quaternion.LookRotation(destination - origin);
		instantShot.transform.parent = shot.transform.parent;

		// Create the shot sparks at target.
		if (placeSparks)
		{
			GameObject instantSparks = Instantiate(sparks);
			instantSparks.SetActive(true);
			instantSparks.transform.position = destination;
			instantSparks.transform.parent = sparks.transform.parent;
		}

		// Put bullet hole on the target.
		if (placeBulletHole)
		{
			Quaternion hitRotation = Quaternion.FromToRotation(Vector3.back, targetNormal);
			GameObject bullet;
			if (bulletHoles.Count < maxBulletHoles)
			{
				// Instantiate new bullet if an empty slot is available.
				bullet = GameObject.CreatePrimitive(PrimitiveType.Quad);
				bullet.GetComponent<MeshRenderer>().material = bulletHole;
				bullet.GetComponent<Collider>().enabled = false;
				bullet.transform.localScale = Vector3.one * 0.07f;
				bullet.name = "BulletHole";
				bulletHoles.Add(bullet);
			}
			else
			{
				// Cycle through bullet slots to reposition the oldest one.
				bullet = bulletHoles[bulletHoleSlot];
				bulletHoleSlot++;
				bulletHoleSlot %= maxBulletHoles;
			}
			bullet.transform.position = destination + 0.01f * targetNormal;
			bullet.transform.rotation = hitRotation;
			bullet.transform.SetParent(parent);
		}
	}

	// Change the active weapon.
	private void ChangeWeapon(int oldWeapon, int newWeapon)
	{
		EnsureInit();
		if (weapons == null) return;
		// Previously armed? Disable weapon.
		if (oldWeapon > 0)
		{
			// TbilisiFly glue: park on the belt/back when the hook takes it,
			// else the stock hide.
			var old = weapons[oldWeapon];
			bool parked = old != null && StowHook != null && StowHook(old);
			if (!parked && old != null) old.gameObject.SetActive(false);
			gunMuzzle = null;
			if (old != null) old.Toggle(false);
		}
		// Cycle trough empty slots to find next existing weapon or the no weapon slot.
		while (weapons[newWeapon] == null && newWeapon > 0)
		{
			newWeapon = (newWeapon + 1) % weapons.Count;
		}
		// Next weapon exists? Activate it.
		if (newWeapon > 0)
		{
			// TbilisiFly glue: a parked weapon comes back from the belt/back
			// to the hand — re-seat it exactly as AddWeapon() did.
			var nw = weapons[newWeapon];
			DrawHook?.Invoke(nw);
			if (rightHand != null)
			{
				Vector3 wsBefore = nw.transform.lossyScale;
				nw.transform.SetParent(rightHand);
				nw.transform.localPosition = HandLocal(rightHand, nw.rightHandPosition);
				nw.transform.localRotation = Quaternion.Euler(nw.relativeRotation);
				KeepWorldScaleUnder(nw.transform, rightHand, wsBefore);
			}
			nw.gameObject.SetActive(true);
			gunMuzzle = nw.transform.Find("muzzle");
			nw.Toggle(true);
		}

		activeWeapon = newWeapon;
		// TbilisiFly glue: a latched aim (Alt/RMB/click) must not outlive the gun
		// — aim is a drawn-gun state in the JS (game.js:508 `foot.held` gate).
		if (newWeapon == 0) CoverShootInput.ClearAimLatch();

		// Call change weapon animation if new weapon type is different.
		if (oldWeapon != newWeapon)
		{
			behaviourManager.GetAnim.SetTrigger(changeWeaponTrigger);
			behaviourManager.GetAnim.SetInteger(weaponTypeInt, weapons[newWeapon] ? (int)weapons[newWeapon].type : 0);
		}

		// Set crosshair if armed.
		SetWeaponCrosshair(newWeapon > 0);
	}

	// Handle the shot parameters during its lifetime.
	private void ShotDecay()
	{
		// Update parameters on imminent shot death.
		if (shotDecay > 0.2)
		{
			shotDecay -= shotRateFactor * Time.deltaTime;
			if (shotDecay <= 0.4f)
			{
				// Return crosshair to normal aim mode and hide shot flash.
				SetWeaponCrosshair(activeWeapon > 0);
				muzzleFlash.SetActive(false);

				if (activeWeapon > 0)
				{
					// Set camera bounce return on recoil end.
					behaviourManager.GetCamScript?.BounceVertical(-weapons[activeWeapon].recoilAngle * 0.1f);

					// Handle next shot for burst or auto mode.
					if (shotDecay <= (0.4f - 2 * Time.deltaTime))
					{
						// Auto mode, keep firing while shoot button is pressed.
						if (weapons[activeWeapon].mode == InteractiveWeapon.WeaponMode.AUTO && CoverShootInput.GetAxisRaw(shootButton) != 0)
						{
							ShootWeapon(activeWeapon, false);
						}
						// Burst mode, keep shooting until reach weapon burst capacity.
						else if (weapons[activeWeapon].mode == InteractiveWeapon.WeaponMode.BURST && burstShotCount < weapons[activeWeapon].burstSize)
						{
							ShootWeapon(activeWeapon, false);
						}
						// Reset burst count for other modes.
						else if (weapons[activeWeapon].mode != InteractiveWeapon.WeaponMode.BURST)
						{
							burstShotCount = 0;
						}
					}
				}
			}
		}
		// Shot is dead, reset parameters.
		else
		{
			isShotAlive = false;
			behaviourManager.GetCamScript?.BounceVertical(0);
			burstShotCount = 0;
		}
	}

	// Add a new weapon to inventory (called by weapon object).
	// TbilisiFly glue (2026-11-02): the stock pack parents a weapon under the
	// hand and sets ONLY localPosition/localRotation — correct on its own
	// unit-scale avatar, wrong under this project's imported rigs whose bones
	// carry a non-unit lossy scale. Keep the weapon's world size as spawned.
	// TbilisiFly glue (2026-11-02): rightHandPosition is authored in the pack's
	// unit-scale hand space. This project's rigs carry an ~88x lossy scale on
	// the hand bone (the FBX imports at 0.01 and the body is normalised to
	// 1.8 m), so the raw offset put the gun 11 m from the hand (GunHandRun:
	// AK_HAND_distToHand=11.035). Express it in the bone's own units.
	static Vector3 HandLocal(Transform hand, Vector3 offset)
	{
		if (hand == null) return offset;
		Vector3 p = hand.lossyScale;
		return new Vector3(
			Mathf.Abs(p.x) > 1e-6f ? offset.x / p.x : offset.x,
			Mathf.Abs(p.y) > 1e-6f ? offset.y / p.y : offset.y,
			Mathf.Abs(p.z) > 1e-6f ? offset.z / p.z : offset.z);
	}

	static void KeepWorldScaleUnder(Transform weapon, Transform parent, Vector3 worldScaleBefore)
	{
		if (weapon == null || parent == null) return;
		Vector3 p = parent.lossyScale;
		weapon.localScale = new Vector3(
			Mathf.Abs(p.x) > 1e-6f ? worldScaleBefore.x / p.x : 1f,
			Mathf.Abs(p.y) > 1e-6f ? worldScaleBefore.y / p.y : 1f,
			Mathf.Abs(p.z) > 1e-6f ? worldScaleBefore.z / p.z : 1f);
	}

	public void AddWeapon(InteractiveWeapon newWeapon)
	{
		EnsureInit();
		if (weapons == null) { Debug.LogWarning("COVERSHOOT AddWeapon before the Animator exists — dropped"); return; }
		// Position new weapon in player's hand.
		Vector3 wsBefore = newWeapon.transform.lossyScale;
		newWeapon.gameObject.transform.SetParent(rightHand);
		newWeapon.transform.localPosition = HandLocal(rightHand, newWeapon.rightHandPosition);
		newWeapon.transform.localRotation = Quaternion.Euler(newWeapon.relativeRotation);
		KeepWorldScaleUnder(newWeapon.transform, rightHand, wsBefore);
		Debug.Log($"PACK_HAND weapon={newWeapon.label} hand={(rightHand != null ? rightHand.name : "<null>")} handLossy={(rightHand != null ? rightHand.lossyScale.ToString("F3") : "-")} worldScale={newWeapon.transform.lossyScale.ToString("F3")} worldPos={newWeapon.transform.position.ToString("F2")} enabled={enabled.ToString().ToLowerInvariant()} ready={Ready.ToString().ToLowerInvariant()}");

		// Handle inventory slot conflict.
		if (this.weapons[slotMap[newWeapon.type]])
		{
			// Same weapon type, recharge bullets and destroy duplicated object.
			if (this.weapons[slotMap[newWeapon.type]].label == newWeapon.label)
			{
				this.weapons[slotMap[newWeapon.type]].ResetBullets();
				ChangeWeapon(activeWeapon, slotMap[newWeapon.type]);
				GameObject.Destroy(newWeapon.gameObject);
				return;
			}
			// Different weapon type, grab the new one and drop the weapon in inventory.
			else
			{
				this.weapons[slotMap[newWeapon.type]].Drop();
			}
		}

		// Call change weapon action.
		this.weapons[slotMap[newWeapon.type]] = newWeapon;
		ChangeWeapon(activeWeapon, slotMap[newWeapon.type]);
	}

	// Handle reload weapon end (called by animation).
	public void EndReloadWeapon()
	{
		// TbilisiFly glue: with a real controller the reload clip's own
		// AnimationEvent fires this too, on top of the timer fallback.
		if (!_reloading) return;
		behaviourManager.GetAnim.SetBool(reloadBool, false);
		_reloading = false;
		weapons[activeWeapon].EndReload();
	}

	// Change HUD crosshair when aiming.
	private void SetWeaponCrosshair(bool armed)
	{
		aimBehaviour.crosshair = armed ? aimCrosshair : originalCrosshair;
	}

	// Check if aim is blocked by obstacles.
	private bool CheckForBlockedAim()
	{
		// TbilisiFly glue: was GetCamScript.transform.forward — playerCamera
		// IS that same transform (no ThirdPersonOrbitCam needed to read it).
		Vector3 camFwd = behaviourManager.playerCamera.forward;
		// TbilisiFly glue: castRelativeOrigin was captured ONCE in world space at
		// init and never turned with the hero; cast from the live neck instead,
		// and never let the hero's own colliders (capsule, slung guns, belt) count
		// as an obstruction — same self-skip as RaycastSkippingSelf.
		Vector3 castFrom = neckBone != null ? neckBone.position : this.transform.position + castRelativeOrigin;
		isAimBlocked = Physics.SphereCast(castFrom, 0.1f, camFwd, out RaycastHit hit, distToHand - 0.1f, shotMask);
		isAimBlocked = isAimBlocked && hit.collider.transform != this.transform && !hit.collider.transform.IsChildOf(this.transform);
		behaviourManager.GetAnim.SetBool(blockedAimBool, isAimBlocked);
		Debug.DrawRay(this.transform.position + castRelativeOrigin, camFwd * distToHand, isAimBlocked ? Color.red : Color.cyan);

		return isAimBlocked;
	}

	// Manage inverse kinematic parameters.
	public void OnAnimatorIK(int layerIndex)
	{
		if (isAiming && activeWeapon > 0)
		{
			if (CheckForBlockedAim())
				return;

			// Orientate upper body where camera  is targeting.
			Quaternion targetRot = Quaternion.Euler(0, transform.eulerAngles.y, 0);
			// TbilisiFly glue: the horizontal re-aim rebuilds the spine from the
			// bind-pose chain under hips.parent — on this rig the art's 180 deg
			// lives on Model, ABOVE that chain (CharacterRig.cs:148), so the
			// rebuilt spine would face backwards. FootController already turns
			// the whole body to the camera yaw while armed, so only the
			// vertical half is needed here.
			if (!facingNegativeZ)
			{
				targetRot *= Quaternion.Euler(initialRootRotation);
				targetRot *= Quaternion.Euler(initialHipsRotation);
				targetRot *= Quaternion.Euler(initialSpineRotation);
				// Set upper body horizontal orientation.
				behaviourManager.GetAnim.SetBoneLocalRotation(HumanBodyBones.Spine, Quaternion.Inverse(hips.rotation) * targetRot);
			}

			// Keep upper body orientation regardless strafe direction.
			Vector3 bodyRight = facingNegativeZ ? -this.transform.right : this.transform.right;
			float xCamRot = Quaternion.LookRotation(behaviourManager.playerCamera.forward).eulerAngles.x;
			targetRot = Quaternion.AngleAxis(xCamRot + armsRotation, bodyRight);
			if (weapons[activeWeapon] && weapons[activeWeapon].type == InteractiveWeapon.WeaponType.LONG)
			{
				// Correction for long weapons.
				targetRot *= Quaternion.AngleAxis(9f, bodyRight);
				targetRot *= Quaternion.AngleAxis(20f, this.transform.up);
			}
			IkApplied++;
			targetRot *= (chest == spine ? hips.rotation : spine.rotation);
			targetRot *= Quaternion.Euler(chest == spine ? initialSpineRotation : initialChestRotation);
			// Set upper body vertical orientation.
			behaviourManager.GetAnim.SetBoneLocalRotation(chest == spine ? HumanBodyBones.Spine : HumanBodyBones.Chest, Quaternion.Inverse(chest == spine ? hips.rotation : spine.rotation) * targetRot);
		}
	}

	// Manage post animation step corrections.
	private void LateUpdate()
	{
		if (!isAiming || isAimBlocked)
		{
			// Correct right hand position when covering.
			if (behaviourManager.GetAnim.GetBool(coveringBool)
			&& behaviourManager.GetAnim.GetFloat(Animator.StringToHash("Crouch")) > 0.5f)
			{
				rightHand.Rotate(RightHandCover);
			}
			// Correct left arm position when guarding with LONG gun.
			else if (weapons[activeWeapon] && weapons[activeWeapon].type == InteractiveWeapon.WeaponType.LONG)
			{
				leftArm.localEulerAngles += LeftArmLongGuard;
			}
		}

		// Correct left arm position when aiming with a SHORT gun.
		else if (isAiming && weapons[activeWeapon] && weapons[activeWeapon].type == InteractiveWeapon.WeaponType.SHORT)
		{
			leftArm.localEulerAngles += LeftArmShortAim;
		}
	}
}
