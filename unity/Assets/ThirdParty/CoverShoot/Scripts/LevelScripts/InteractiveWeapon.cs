using UnityEngine;
using TbilisiFly.Character; // CoverShootInput — see that file's header

// This class corresponds to any in-game weapon interactions.
// TbilisiFly glue (2026-11-01 compile-fix pass): dropped `using
// UnityEngine.UI;` — this project has no uGUI package installed (UI Toolkit
// only) and never shows this pack's own English pickup label anyway
// (Georgian-UI-only rule; PickupHUD's Canvas is disabled at spawn —
// Character/CoverShootHero.cs's EnsureGameController). See
// TogglePickupHUD below for the one line this removed.
public class InteractiveWeapon : MonoBehaviour
{
	public string label;                                      // The weapon name. Same name will treat weapons as same regardless game object's name.
	public AudioClip shotSound, reloadSound,                  // Audio clips for shoot and reload.
		pickSound, dropSound, noBulletSound;                  // Audio clips for pick weapon , drop weapon, and no bullet shot try.
	public Sprite sprite;                                     // Weapon sprite to show on screen HUD.
	public Vector3 rightHandPosition;                         // Position offsets relative to the player's right hand.
	public Vector3 relativeRotation;                          // Rotation Offsets relative to the player's right hand.
	public float bulletDamage = 10f;                          // Damage of one shot.
	public float recoilAngle;                                 // Angle of weapon recoil.
	public enum WeaponType                                    // Weapon types, related to player's shooting animations.
	{
		NONE,
		SHORT,
		LONG
	}
	public enum WeaponMode                                    // Weapon shooting modes.
	{
		SEMI,
		BURST,
		AUTO
	}
	public WeaponType type = WeaponType.NONE;                 // Default weapon type, change in Inspector.
	public WeaponMode mode = WeaponMode.SEMI;                 // Default weapon mode, change in Inspector.
	public int burstSize = 0;                                 // How many shot are fired on burst mode.
	[SerializeField]
	private int mag, totalBullets;                            // Current mag capacity and total amount of bullets being carried.
	private int fullMag, maxBullets;                          // Default mag capacity and total bullets for reset purposes.
	// Read-only, for Tests/PlayMode/FirstPlayModeRun.cs's purchased-path
	// key_R check (2026-11-01) — mag/totalBullets have no public accessor
	// otherwise, and WeaponUIManager.OnAmmoChanged is an event, not state
	// a test can poll on demand after the fact.
	public int Mag => mag;
	public int TotalBullets => totalBullets;
	public int FullMag => fullMag;
	private GameObject player, gameController;                // References to the player and the game controller.
	private ShootBehaviour playerInventory;                   // Player's inventory to store weapons.
	private SphereCollider interactiveRadius;                 // In-game radius of interaction with player.
	private BoxCollider col;                                  // Weapon collider.
	private Rigidbody rbody;                                  // Weapon rigidbody.
	private WeaponUIManager weaponHud;                        // Reference to on-screen weapon HUD.
	private bool pickable;                                    // Boolean to store whether or not the weapon is pickable (player within radius).
	private Transform pickupHUD;                              // Reference to the weapon pickup in-game label.
	// Georgian pickup prompt (2026-11-02, user: "taking guns with F button
	// will works too, coz last time it not worked" — pickup_armed already
	// passes, so the MECHANISM works; the missing piece is the player ever
	// being told F does something here at all). pickupHUD above is the
	// stock pack's own in-world label transform, left permanently disabled
	// (Georgian-UI-only rule, see class header) — this static event is what
	// UI/Hud/CoverShootPromptView.cs actually listens to instead. Fired
	// only on a real state CHANGE (not every OnTriggerStay frame) from
	// TogglePickupHUD below, which both trigger callbacks already funnel
	// through.
	private bool _promptFired;
	public static event System.Action<InteractiveWeapon, bool> OnPickupRangeChanged;

	void Awake()
	{
		// Set up the references.
		this.gameObject.name = this.label;
		this.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
		foreach (Transform t in this.transform)
		{
			t.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
		}
		player = GameObject.FindGameObjectWithTag("Player");
		playerInventory = player.GetComponent<ShootBehaviour>();
		gameController = GameObject.FindGameObjectWithTag("GameController");
		// Assert that exists a on-screen HUD.
		if (GameObject.Find("ScreenHUD") == null)
		{
			Debug.LogError("No ScreenHUD canvas found. Create ScreenHUD inside the GameController");
		}
		weaponHud = GameObject.Find("ScreenHUD").GetComponent<WeaponUIManager>();
		pickupHUD = gameController.transform.Find("PickupHUD");

		// Create physics components and radius of interaction.
		col = this.transform.GetChild(0).gameObject.AddComponent<BoxCollider>();
		CreateInteractiveRadius(col.center);
		this.rbody = this.gameObject.AddComponent<Rigidbody>();

		// Assert that an weapon slot is set up.
		if (this.type == WeaponType.NONE)
		{
			Debug.LogWarning("Set correct weapon slot ( 1 - small/ 2- big)");
			type = WeaponType.SHORT;
		}

		// Assert that the gun muzzle is exists.
		if(!this.transform.Find("muzzle"))
		{
			Debug.LogError(this.name+" muzzle is not present. Create a game object named 'muzzle' as a child of this game object");
		}

		// Set default values.
		fullMag = mag;
		maxBullets = totalBullets;
		pickupHUD.gameObject.SetActive(false);
	}

	// Create the sphere of interaction with player.
	private void CreateInteractiveRadius(Vector3 center)
	{
		interactiveRadius = this.gameObject.AddComponent<SphereCollider>();
		interactiveRadius.center = center;
		interactiveRadius.radius = 1f;
		interactiveRadius.isTrigger = true;
	}

	void Update()
	{
		// Handle player pick weapon action.
		if (this.pickable && CoverShootInput.GetButtonDown(playerInventory.pickButton))
		{
			// Disable weapon physics.
			rbody.isKinematic = true;
			this.col.enabled = false;
			
			// Setup weapon and add in player inventory.
			playerInventory.AddWeapon(this);
			Destroy(interactiveRadius);
			this.Toggle(true);
			this.pickable = false;

			// Change active weapon HUD.
			TogglePickupHUD(false);
		}
	}

	// Handle weapon collision with environment.
	private void OnCollisionEnter(Collision collision)
	{
		if(collision.collider.gameObject != player && Vector3.Distance(transform.position, player.transform.position) <= 5f)
		{
			if (dropSound != null) AudioSource.PlayClipAtPoint(dropSound, transform.position, 0.5f);
		}
	}

	// Handle player exiting radius of interaction.
	private void OnTriggerExit(Collider other)
	{
		if (other.gameObject == player)
		{
			pickable = false;
			TogglePickupHUD(false);
		}
	}

	// Handle player within radius of interaction.
	void OnTriggerStay(Collider other)
	{
		if (other.gameObject == player && playerInventory && playerInventory.isActiveAndEnabled)
		{
			pickable = true;
			TogglePickupHUD(true);
		}
	}

	// Draw in-game weapon pickup label.
	private void TogglePickupHUD(bool toggle)
	{
		if (toggle != _promptFired)
		{
			_promptFired = toggle;
			OnPickupRangeChanged?.Invoke(this, toggle);
		}
		pickupHUD.gameObject.SetActive(toggle);
		if (toggle)
		{
			pickupHUD.position = this.transform.position + Vector3.up * 0.5f;
			Vector3 direction = player.GetComponent<BasicBehaviour>().playerCamera.forward;
			direction.y = 0f;
			pickupHUD.rotation = Quaternion.LookRotation(direction);
			// Dropped: pickupHUD.Find("Label").GetComponent<Text>().text = "Pick "+name;
			// — UnityEngine.UI.Text does not exist here (no uGUI package), and
			// PickupHUD's Canvas is disabled anyway (Georgian-UI-only rule).
		}
	}

	// Manage weapon active status.
	public void Toggle(bool active)
	{
		if (active)
			// TbilisiFly glue: null-guarded — PlayClipAtPoint throws NRE on a
			// null clip, which was silently aborting the whole AddWeapon()
			// call chain mid-method (activeWeapon never got set) when the
			// Resources/Audio asset was briefly missing. See this file's
			// other PlayClipAtPoint call sites, all guarded the same way.
			if (pickSound != null) AudioSource.PlayClipAtPoint(pickSound, transform.position, 0.5f);
		weaponHud.Toggle(active);
		UpdateHud();
	}

	// Manage the drop action.
	public void Drop()
	{
		this.gameObject.SetActive(true);
		this.transform.position += Vector3.up;
		rbody.isKinematic = false;
		this.transform.parent = null;
		CreateInteractiveRadius(col.center);
		this.col.enabled = true;
		weaponHud.Toggle(false);
	}

	// Start the reload action (called by shoot behaviour).
	public bool StartReload()
	{
		if (mag == fullMag || totalBullets == 0)
			return false;
		else if(totalBullets < fullMag - mag)
		{
			mag += totalBullets;
			totalBullets = 0; 
		}
		else
		{
			totalBullets -= fullMag - mag;
			mag = fullMag;
		}

		return true;
	}

	// End the reload action (called by shoot behaviour).
	public void EndReload()
	{
		UpdateHud();
	}

	// Manage shoot action.
	public bool Shoot(bool firstShot=true)
	{
		if (mag > 0)
		{
			mag--;
			UpdateHud();
			return true;
		}
		if (firstShot && noBulletSound)
			if (noBulletSound != null) AudioSource.PlayClipAtPoint(noBulletSound, this.transform.Find("muzzle").position, 5f);
		return false;
	}

	// Reset the bullet parameters.
	// TbilisiFly glue (2026-11-02): the same prep Update() does before
	// AddWeapon when the PLAYER picks this up, for a weapon the project hands
	// over itself (Core/HeldWeapon.cs bridges the warehouse loot into one).
	public void PrepareForInventory()
	{
		if (rbody != null) rbody.isKinematic = true;
		if (col != null) col.enabled = false;
		if (interactiveRadius != null) Destroy(interactiveRadius);
		pickable = false;
		if (pickupHUD != null) TogglePickupHUD(false);
	}

	public void ResetBullets()
	{
		mag = fullMag;
		totalBullets = maxBullets;
	}

	// Update weapon screen HUD.
	private void UpdateHud()
	{
		weaponHud.UpdateWeaponHUD(sprite, mag, fullMag, totalBullets);
	}

	// TbilisiFly glue: `mag`/`totalBullets` are [SerializeField] PRIVATE, only
	// ever set from the Inspector in the stock asset — there is no hand-built
	// prefab here, every pickup this project spawns is built at runtime by
	// Character/CoverShootPickups.cs, which has no Inspector to set them in.
	// Safe to call any time (before or after Awake): it overwrites BOTH the
	// current and the "full/reset" copies together, so Awake's own
	// `fullMag = mag; maxBullets = totalBullets;` never gets a stale 0.
	public void ConfigureAmmo(int magSize, int reserve)
	{
		mag = magSize; fullMag = magSize;
		totalBullets = reserve; maxBullets = reserve;
	}
}
