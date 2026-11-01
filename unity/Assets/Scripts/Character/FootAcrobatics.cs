using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// The two pieces of src/foot.js's acrobatics that FootController.cs does
    /// NOT already port: the aim-cone clamp (`_moveAim`, src/foot.js:4594-4623)
    /// and the third-person camera snap (`_snapCamera`, src/foot.js:4718-4724).
    ///
    /// ================================ WHY THIS FILE IS SMALL ===============
    /// It was first written as a full 680-line port of all 17 acrobatics
    /// methods — roll, slide, dive, air pose, backflip, crouch, stroll. Then a
    /// method-by-method grep showed FootController.cs ALREADY contained 15 of
    /// those 17, so 643 of those lines were a second copy of shipped logic,
    /// wired to nothing. Two copies of a tuned physics routine drift, and the
    /// project rule is that src/*.js is the one spec with one implementation,
    /// so the duplicates were deleted rather than left "just in case". Only
    /// MoveAim and SnapCamera were genuinely absent (grep for either across
    /// Assets/Scripts/Character returned zero hits before this file), and
    /// FootController's own header names `_moveAim` as outside its scope.
    ///
    /// Nothing calls these yet. FootController owns the acrobatics state, so
    /// wiring is its author's call: MoveAim belongs wherever a held weapon's
    /// aim vector is resolved during a roll/slide/dive, SnapCamera wherever the
    /// backflip starts.
    ///
    /// ================================ UNRESOLVED: WHICH WAY IS FORWARD =====
    /// MoveAim and SnapCamera below use (0, 0, -1) as the body's forward,
    /// transcribed from the JS. That contradicts this project's stated rule
    /// that Unity's forward is +Z, and it is NOT yet settled which is right
    /// here, so DO NOT WIRE EITHER METHOD UNTIL A PROBE DECIDES IT.
    ///
    /// The argument for leaving it as the JS has it: the X-mirror leaves a Z
    /// component untouched, this rig frame is driven by a live simulation yaw
    /// that never passes through GeoFrame, and the frame only has to be
    /// self-consistent with whatever FootController feeds it.
    /// The argument against: Unity reads (0, 0, -1) as BACKWARD, so if
    /// ActGroup's rotation is built from a GeoFrame-converted yaw, the aim cone
    /// is mirrored and the camera sits in front of the hero instead of behind.
    /// An earlier port elsewhere in this repo made exactly this mistake —
    /// transcribing THREE's (0,0,-1) as Vector3.back — and had to be corrected.
    ///
    /// FootController.cs cannot settle it: it contains no forward vector at
    /// all (grep for Vector3.forward/back/(0,0,-1) finds nothing), so the claim
    /// that this "matches FootController's shipped choice" has no basis. The
    /// deciding test is cheap and must read the RENDERED result, not the maths:
    /// pose a body at a known yaw, then check the snap camera lands behind the
    /// head and the clamped aim stays on the target side.
    /// </summary>
    public static class FootAcrobatics
    {
        // src/foot.js:260-262 — the aim workspace: how far the held weapon may
        // be swung before the body itself has to turn.
        const float AimYaw = 1.5f;
        const float AimUp = 1.35f;
        const float AimDown = -1.75f;
        const float AimArmYaw = 0.85f;  // src/foot.js:468 — the arm's own reach
        const float Eye = 1.55f;        // src/foot.js:264 — eye height on a 1.8 m body

        /// <summary>
        /// src/foot.js:4594-4623 `_moveAim`. Clamps an aim direction into the
        /// cone the body can actually reach, so a weapon held through a roll or
        /// slide keeps pointing at the shot instead of through the torso.
        /// Returns the aim unchanged when it is already inside the workspace.
        /// </summary>
        public static Vector3 MoveAim(CharacterRig rig, Vector3? aim, float? aimTwist = null)
        {
            if (!aim.HasValue || rig == null) return aim ?? Vector3.zero;

            Quaternion q = rig.ActGroup.rotation;
            // See the UNRESOLVED note in the class header before changing these.
            Vector3 fwd = q * new Vector3(0f, 0f, -1f);
            Vector3 up = q * new Vector3(0f, 1f, 0f);
            Vector3 side = q * new Vector3(1f, 0f, 0f);
            return MoveAimInBasis(fwd, up, side, aim.Value, aimTwist);
        }

        /// <summary>
        /// The clamp itself, taking the body basis directly instead of reading
        /// it off a rig. Split out so a probe can check the arithmetic without
        /// constructing a CharacterRig (which needs a loaded Animator and body);
        /// MoveAim above is the thin rig-reading wrapper. The basis vectors are
        /// whatever the caller's frame says they are, so this function is
        /// handedness-neutral — the unresolved question in the class header is
        /// about the WRAPPER's choice of forward, not about this maths.
        /// </summary>
        public static Vector3 MoveAimInBasis(Vector3 fwd, Vector3 up, Vector3 side,
                                             Vector3 a, float? aimTwist = null)
        {
            float z = Vector3.Dot(fwd, a), x = Vector3.Dot(side, a), y = Vector3.Dot(up, a);
            float pitch = Mathf.Asin(Mathf.Clamp(y, -1f, 1f));
            float yaw = Mathf.Atan2(x, z);

            float cp = Mathf.Clamp(pitch, AimDown, AimUp);
            // Across the body the reach is the ARM's reach across the turned
            // chest, plus however far the torso has already turned toward the
            // shot — src/foot.js:4610.
            float maxYaw = Mathf.Min(AimYaw, AimArmYaw + Mathf.Abs(aimTwist ?? 0f));
            float cy = Mathf.Clamp(yaw, -maxYaw, maxYaw);

            // src/foot.js:4616 uses EXACT equality (`cp === pitch`), not a
            // tolerance, and that is correct rather than sloppy: Mathf.Clamp,
            // like Math.min/max, returns its input bit for bit when the value
            // was already inside the range. Rounding this off with
            // Mathf.Approximately would make a hair's-breadth clamp read as "no
            // clamp" and skip the renormalise below.
            if (cp == pitch && cy == yaw) return a;

            float c = Mathf.Cos(cp);
            Vector3 outV = fwd * (c * Mathf.Cos(cy)) + side * (c * Mathf.Sin(cy)) + up * Mathf.Sin(cp);
            return outV.normalized;
        }

        /// <summary>
        /// src/foot.js:4718-4724 `_snapCamera`. Puts the chase camera straight
        /// behind the body at a known offset and aims it at eye height — used
        /// when a move (the backflip) would otherwise leave the camera lagging
        /// through the rotation. `camYaw` comes back so the caller's own camera
        /// spring can be reseated instead of unwinding.
        /// </summary>
        public static (Vector3 position, Vector3 lookAt) SnapCamera(Vector3 pos, float yaw, out float camYaw)
        {
            camYaw = yaw;
            // Euler-Y(yaw) applied to (0, 2.6, 6.2). See the UNRESOLVED note in
            // the class header: if this frame turns out to be mirrored, this
            // offset puts the camera in FRONT of the hero.
            Vector3 off = new Vector3(6.2f * Mathf.Sin(yaw), 2.6f, 6.2f * Mathf.Cos(yaw));
            return (pos + off, new Vector3(pos.x, pos.y + Eye, pos.z));
        }
    }
}
