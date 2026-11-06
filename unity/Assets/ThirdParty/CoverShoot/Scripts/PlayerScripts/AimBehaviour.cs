using UnityEngine;
using System.Collections;
using TbilisiFly.Character; // CoverShootInput — see that file's header

// AimBehaviour inherits from GenericBehaviour. This class corresponds to aim and strafe behaviour.
public class AimBehaviour : GenericBehaviour
{
	public string aimButton = "Aim", shoulderButton = "Aim Shoulder";     // Default aim and switch shoulders buttons.
	public Texture2D crosshair;                                           // Crosshair texture.
	public float aimTurnSmoothing = 0.15f;                                // Speed of turn response when aiming to match camera facing.
	public Vector3 aimPivotOffset = new Vector3(0.5f, 1.2f,  0f);         // Offset to repoint the camera when aiming.
	public Vector3 aimCamOffset   = new Vector3(0f, 0.4f, -0.7f);         // Offset to relocate the camera when aiming.

	private int aimBool;                                                  // Animator variable related to aiming.
	private bool aim;                                                     // Boolean to determine whether or not the player is aiming.
	// TbilisiFly glue: no AnimatorController on rig.Anim under the
	// purchased-shooter path (Character/CoverShootHero.cs's class header),
	// so ShootBehaviour (and CoverShootRun.cs) read this instead of
	// `GetAnim.GetBool("Aim")`, which always reads false with no parameter table.
	public bool IsAiming => aim;
	private int cornerBool;                                               // Animator variable related to cover corner..
	private bool peekCorner;                                              // Boolean to get whether or not the player is on a cover corner.
	private Vector3 initialRootRotation;                                  // Initial root bone local rotation.
	private Vector3 initialHipsRotation;                                  // Initial hips rotation related to the root bone.
	private Vector3 initialSpineRotation;                                 // Initial spine rotation related to the root bone.

	// Start is always called after any Awake functions.
	void Start ()
	{
		// Set up the references.
		aimBool = Animator.StringToHash("Aim");

		cornerBool = Animator.StringToHash("Corner");

		// Get initial bone rotation values.
		Transform hips = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Hips);
		Transform spine = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Spine);
		Transform root = hips.parent;
		// Correctly set the hip and root bones.
		if (spine.parent != hips)
		{
			root = hips;
			hips = spine.parent;
		}
		initialRootRotation = (root == transform) ? Vector3.zero : root.localEulerAngles;
		initialHipsRotation = hips.localEulerAngles;
		initialSpineRotation = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Spine).localEulerAngles;
	}

	// Update is used to set features regardless the active behaviour.
	void Update ()
	{
		peekCorner = behaviourManager.GetAnim.GetBool(cornerBool);

		// Activate/deactivate aim by input.
		if (CoverShootInput.GetAxisRaw(aimButton) != 0 && !aim)
		{
			StartCoroutine(ToggleAimOn());
		}
		else if (aim && CoverShootInput.GetAxisRaw(aimButton) == 0)
		{
			StartCoroutine(ToggleAimOff());
		}

		// No sprinting while aiming.
		canSprint = !aim;

		// Toggle camera aim position left or right, switching shoulders.
		if (aim && CoverShootInput.GetButtonDown (shoulderButton) && !peekCorner)
		{
			aimCamOffset.x = aimCamOffset.x * (-1);
			aimPivotOffset.x = aimPivotOffset.x * (-1);
		}

		// Set aim boolean on the Animator Controller.
		behaviourManager.GetAnim.SetBool (aimBool, aim);
	}

	// Co-routine to start aiming mode with delay.
	private IEnumerator ToggleAimOn()
	{
		yield return new WaitForSeconds(0.05f);
		// Aiming is not possible.
		// TbilisiFly glue (2026-11-02): the stock `|| IsOverriding(this)` also
		// refused to aim for the 0.35 s after aiming STOPPED — ToggleAimOff
		// sets `aim = false` at once but only revokes this behaviour's override
		// at its end — so a click that re-aimed inside that window (the JS
		// click-to-aim-and-fire, ShootBehaviour.Update) was dropped. We are
		// that stale overrider; re-entering is safe (OverrideWithBehaviour is a
		// Contains() no-op and ToggleAimOff below keeps the override if aim is
		// back on). Only a temp lock (a cover transition, a jump) still refuses.
		if (behaviourManager.GetTempLockStatus(this.behaviourCode))
			yield return false;

		// Start aiming.
		else
		{
			if (!aim) Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, this, "AIM on latch={0}", CoverShootInput.AimLatched ? 1 : 0);
			aim = true;
			int signal = 1;
			if (peekCorner)
			{
				signal = (int)Mathf.Sign(behaviourManager.GetH);
			}
			aimCamOffset.x = Mathf.Abs(aimCamOffset.x) * signal;
			aimPivotOffset.x = Mathf.Abs(aimPivotOffset.x) * signal;
			yield return new WaitForSeconds(0.1f);
			behaviourManager.GetAnim.SetFloat(speedFloat, 0);
			// This state overrides the active one.
			behaviourManager.OverrideWithBehaviour(this);
		}
	}

	// Co-routine to end aiming mode with delay.
	private IEnumerator ToggleAimOff()
	{
		aim = false;
		Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, this, "AIM off");
		yield return new WaitForSeconds(0.3f);
		// TbilisiFly glue: GetCamScript is null now (ThirdPersonOrbitCam was
		// removed — Core/GameLoop.cs's own on-foot camera owns the camera
		// under the purchased-shooter path too, coordinator decision
		// 2026-11-01 15:5x, so A/D stay screen-relative). Every GetCamScript
		// call in this pack is null-guarded for that reason.
		behaviourManager.GetCamScript?.ResetTargetOffsets();
		behaviourManager.GetCamScript?.ResetMaxVerticalAngle();
		yield return new WaitForSeconds(0.05f);
		if (!aim) behaviourManager.RevokeOverridingBehaviour(this); // TbilisiFly glue: re-aimed inside the window → the override stays
	}

	// LocalFixedUpdate overrides the virtual function of the base class.
	public override void LocalFixedUpdate()
	{
		// Set camera position and orientation to the aim mode parameters.
		if(aim)
			behaviourManager.GetCamScript?.SetTargetOffsets (aimPivotOffset, aimCamOffset);
	}

	// LocalLateUpdate: manager is called here to set player rotation after camera rotates, avoiding flickering.
	public override void LocalLateUpdate()
	{
		AimManagement();
	}

	// Handle aim parameters when aiming is active.
	void AimManagement()
	{
		// Deal with the player orientation when aiming.
		Rotating();
	}

	// Rotate the player to match correct orientation, according to camera.
	void Rotating()
	{
		Vector3 forward = behaviourManager.playerCamera.TransformDirection(Vector3.forward);
		// Player is moving on ground, Y component of camera facing is not relevant.
		forward.y = 0.0f;
		forward = forward.normalized;

		// Always rotates the player according to the camera horizontal rotation in aim mode.
		// TbilisiFly glue: was GetCamScript.GetH — read playerCamera's own
		// yaw directly (same transform our camera drives; no ThirdPersonOrbitCam needed).
		Quaternion targetRotation =  Quaternion.Euler(0, behaviourManager.playerCamera.eulerAngles.y, 0);

		float minSpeed = Quaternion.Angle(transform.rotation, targetRotation) * aimTurnSmoothing;

		// Peeking corner situation.
		if (peekCorner)
		{
			// Rotate only player upper body when peeking a corner.
			transform.rotation = Quaternion.LookRotation(-behaviourManager.GetLastDirection());
			targetRotation *= Quaternion.Euler(initialRootRotation);
			targetRotation *= Quaternion.Euler(initialHipsRotation);
			targetRotation *= Quaternion.Euler(initialSpineRotation);
			Transform spine = behaviourManager.GetAnim.GetBoneTransform(HumanBodyBones.Spine);
			spine.rotation = targetRotation;
		}
		else
		{
			// Rotate entire player to face camera.
			behaviourManager.SetLastDirection(forward);
			transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, minSpeed * Time.deltaTime);
		}
	}

 	// Draw the crosshair when aiming.
	void OnGUI () 
	{
		if (crosshair)
		{
			float mag = behaviourManager.GetCamScript != null ? behaviourManager.GetCamScript.GetCurrentPivotMagnitude(aimPivotOffset) : 0f;
			if (mag < 0.05f)
				GUI.DrawTexture(new Rect(Screen.width / 2 - (crosshair.width * 0.5f),
										 Screen.height / 2 - (crosshair.height * 0.5f),
										 crosshair.width, crosshair.height), crosshair);
		}
	}
}
