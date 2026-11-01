using UnityEngine;
using UnityEngine.InputSystem;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// GTA-style lagging chase camera with a speed FOV kick and a left-drag
    /// look-around, ported from src/game.js:1677-1708 (the camera half of its
    /// `_flightUpdate`) and the mouse handling at game.js:646-675 (`this.look`).
    ///
    /// The look-around offset itself is NOT a ported physical quantity (unlike
    /// FlightModel) — it is a fresh camera-local degree of freedom defined here
    /// in Unity's own frame to reproduce the same subjective feel (drag left,
    /// see more of the plane's left side), not a bit-exact port, so it is not
    /// subject to FlightModel's handedness derivation. What IS carried over
    /// one-for-one: the behind-and-above offset, the exponential position lerp
    /// with its two time constants (dragging vs released), the ease-back-to-
    /// centre on release, the look-ahead shrink while orbiting, and the FOV
    /// formula.
    ///
    /// Local offset is (0, up, -back) rather than the JS's (0, up, +back):
    /// FlightModel's header gives Unity forward = +Z (nose), so "behind the
    /// plane" is -Z here, where the JS (forward = -Z) has it at +Z.
    /// </summary>
    public class FlightCamera : MonoBehaviour
    {
        public PlaneController Plane;

        const float MouseSens = 0.005f; // rad per pixel — src/game.js:77 MOUSE_SENS

        float _lookYaw, _lookPitch;
        bool _dragging;
        Camera _cam;

        void Awake() => _cam = GetComponent<Camera>();

        /// <summary>When true, GameLoop drives this camera itself by calling
        /// Drive(dt) right after the aeroplane has been stepped (src/game.js's
        /// own order: the flight update, THEN _updateCamera, then the shake and
        /// the keep-outside clamp, all in one tick), and this component's own
        /// Update/LateUpdate stand down. As a free-running MonoBehaviour its
        /// LateUpdate ran in an order against GameLoop.LateUpdate that Unity
        /// does not define: one order lerped the camera toward a plane position
        /// one frame stale (2 m behind at 136 m/s), the other lerped it back
        /// INTO the wall/ground the clamp had just pushed it out of, so every
        /// other frame rendered a different camera — the user's "camera is
        /// bouncing when I am flying" (2026-10-31).</summary>
        public bool Driven;

        public void Drive(float dt)
        {
            ReadLook(dt);
            Apply(dt);
        }

        void Update()
        {
            if (Driven) return;
            ReadLook(Time.deltaTime);
        }

        void ReadLook(float dt)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            if (mouse.leftButton.wasPressedThisFrame) _dragging = true;
            if (mouse.leftButton.wasReleasedThisFrame) _dragging = false;

            if (_dragging)
            {
                Vector2 d = mouse.delta.ReadValue();
                _lookYaw -= d.x * MouseSens;
                _lookPitch = Mathf.Clamp(_lookPitch - d.y * MouseSens, -1.2f, 1.2f);
            }
            else if (_lookYaw != 0f || _lookPitch != 0f)
            {
                // Ease the free-look back to straight ahead — game.js:1680.
                float back = 1f - Mathf.Exp(-6f * dt);
                _lookYaw *= 1f - back;
                _lookPitch *= 1f - back;
                if (Mathf.Abs(_lookYaw) < 0.001f) _lookYaw = 0f;
                if (Mathf.Abs(_lookPitch) < 0.001f) _lookPitch = 0f;
            }
        }

        void LateUpdate()
        {
            if (Driven) return;
            Apply(Time.deltaTime);
        }

        void Apply(float dt)
        {
            if (Plane == null || Plane.Model == null || _cam == null) return;
            var planeT = Plane.transform;
            float camScale = Mathf.Max(1f, Plane.Model.Span / 13f); // game.js:98

            Quaternion lookOffset = Quaternion.Euler(
                _lookPitch * Mathf.Rad2Deg, _lookYaw * Mathf.Rad2Deg, 0f);
            Quaternion camQuat = planeT.rotation * lookOffset;

            Vector3 localOffset = new Vector3(0f, 6.8f * camScale, -25f * camScale);
            Vector3 camTarget = planeT.position + camQuat * localOffset;

            float t = 1f - Mathf.Exp(-(_dragging ? 12f : 4.2f) * dt);
            transform.position = Vector3.Lerp(transform.position, camTarget, t);

            float aheadDist = 22f * camScale *
                Mathf.Max(0f, Mathf.Cos(_lookYaw) * Mathf.Cos(_lookPitch));
            transform.LookAt(planeT.position + planeT.forward * aheadDist, Vector3.up);

            float vMax = Mathf.Max(1f, Plane.Model.VMax);
            float speedFrac = Plane.Speed / vMax;
            _cam.fieldOfView = Mathf.Clamp(62f + 14f * speedFrac * speedFrac, 20f, 110f);
        }

        /// <summary>src/game.js:1321-1325 `_snapCamera` — an instant, non-lerped
        /// placement for the moment right after the plane's OWN transform was
        /// just teleported (reset/goAirport/resume), so this camera's usual
        /// exponential lerp in LateUpdate() above does not visibly glide across
        /// the map on the next frame. Deliberately NOT the same offset as
        /// LateUpdate's camScale-scaled (6.8, 25) and ignores the free-look
        /// offset entirely: the JS hardcodes `new THREE.Vector3(0, 7, 26)`
        /// here and applies only `plane.quaternion` — ported exactly as
        /// measured, not unified with the per-frame formula. Local Z is
        /// negated (-26 vs the JS's +26): Unity forward = +Z, JS forward =
        /// -Z (see this class's own header).</summary>
        public void SnapToPlane()
        {
            if (Plane == null || _cam == null) return;
            var planeT = Plane.transform;
            Vector3 offset = planeT.rotation * new Vector3(0f, 7f, -26f);
            transform.position = planeT.position + offset;
            transform.LookAt(planeT.position, Vector3.up);
        }
    }
}
