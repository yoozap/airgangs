using System.Linq;
using UnityEngine;
using TbilisiFly.Core;
using TbilisiFly.Character;
using TbilisiFly.Flight;

namespace TbilisiFly.Props
{
    /// <summary>
    /// Port of src/crane.js's `wireUp`'s per-frame propUpdater (:741-756) PLUS
    /// `windTunnel`'s own propUpdater (:1848-1931) — the single master clock
    /// that drives everything downstream of the fan switch: the fan spin-up/
    /// coast-down ramp, the wind-tunnel force balance (lift/drag/haul-up/
    /// buffet, via Props/WindTunnel.cs, already ported pure math), the hero
    /// riding the wing (Props/WindRider.cs, already ported, ZERO call sites
    /// before this file — unity SKILL.md's own "a probe proves a
    /// CALCULATION, not that it is REACHED" warning named this file by path),
    /// the lamp/button colours, and the fan audio voice (Core/AudioFx.cs,
    /// already a separate ported file, called the same way crane.js's own
    /// propUpdater calls `voice.update`).
    ///
    /// Attached to the distribution board GameObject by
    /// <see cref="WarehouseCraneFill.Build"/> — ONE MonoBehaviour per file,
    /// named after it (unity SKILL.md's hard rule).
    ///
    /// PARTIALLY CLOSED (this pass): crane.js:833-840's B-key cycle is now
    /// read directly in Update() below, since Press()/CycleButton() (:821-828)
    /// already existed, called voice.Click correctly, and had ZERO callers
    /// anywhere outside Editor probes — the exact "finished code, no runtime
    /// caller" shape. STILL NOT PORTED (see WarehouseCraneFill.cs's class
    /// header): the visible #fan-ctl DOM card itself (state readout text +
    /// lo/hi buttons, crane.js:758-829) — a UI Toolkit view on the
    /// RangeLaneView.cs pattern, out of this pass's file scope.
    /// <see cref="NearSwitch"/>/<see cref="Mode"/>/
    /// <see cref="Press"/> are the exact surface such a card would bind to;
    /// the switch itself now works without it, just silently (B does the
    /// right thing with no on-screen confirmation yet).
    ///
    /// NOW WIRED (this pass): the wind_tunnel_flow haze sheets (<see
    /// cref="FlowRenderer"/>, built by WarehouseCraneFill.BuildFlowMesh off
    /// Props/WindFlowMesh.cs's already-ported mesh math — see that method's
    /// header) and the hung Mustang's own propeller windmilling (<see
    /// cref="Propellers"/>, built by WarehouseCraneFill.BuildPropellers
    /// through Flight/PropellerSystem.cs's BuildBladesMesh/FinishPropeller
    /// pipeline — the P-51 ships NO blade geometry at all, PlaneCatalog's
    /// PropMake "build one where the spinner is" path, not the mesh-cutting
    /// Search/Tag path the OTHER propeller aircraft would use). Driven every
    /// frame via <see cref="PropellerSystem.SpinPropellers"/>(Propellers,
    /// propRpm, dt) below, reusing WindTunnel.Step's existing propRpm output
    /// rather than a second spinner (the task's own instruction).
    /// </summary>
    public class WarehouseFanSwitch : MonoBehaviour
    {
        // ---- wired by WarehouseCraneFill.Build --------------------------------
        public Transform[] FanRotors;      // 3, may contain nulls if fan_rotor wasn't found
        public Transform SwingPivot;       // "crane_swing"
        public Transform Plane;            // the hung P-51
        public Collider[] SkinColliders; // for WindRider.SkinAt
        public Renderer LampRenderer, ButtonRenderer;
        public Transform ShedRoot;
        public float SwitchReach = 3.5f;
        public WindRider.PlaneBox OwnBox;
        public float PlaneHangY; // the plane's resting local Y under the swing pivot (crane.js's `hangY`)
        /// <summary>crane.js:1079-1087 `wind_tunnel_flow` — built by
        /// WarehouseCraneFill.BuildFlowMesh, left disabled (crane.js:1082
        /// `flow.visible = false`) until this Update() turns it on.</summary>
        public Renderer FlowRenderer;
        /// <summary>crane.js's `plane.userData.propellers` — built by
        /// WarehouseCraneFill.BuildPropellers. Spun every frame below via
        /// Flight/PropellerSystem.SpinPropellers (crane.js:1928).</summary>
        public System.Collections.Generic.List<PropellerSystem.PropellerInstance> Propellers;

        /// <summary>Optional — a scene normally has exactly one. Found once
        /// (FindFirstObjectByType, same cost profile as Camera.main) and
        /// cached; re-found if it goes null (a scene reload). WindRider.Tick
        /// needs the live FootController/CharacterRig/GameMode this class
        /// exposes (crane.js's own `game.foot`/`game.foot.active`).</summary>
        GameLoop _game;

        readonly WarehouseCrane.FanSwitch _switch = new WarehouseCrane.FanSwitch();
        WindTunnel.State _wind;
        Vector2 _flowOffset; // crane.js:1926-1927's persistent `tex.offset`
        readonly WindRider.State _rideState = new WindRider.State();
        WindRider _rider;
        FlightModel _fm;
        Vector3 _axis;
        Quaternion _baseRotation;
        MachineVoice _voice;
        bool _initialized;

        public WarehouseCrane.FanSwitch.Setting Mode => _switch.Mode;
        public bool NearSwitch { get; internal set; } // internal setter: HandleBKeyProbe needs to force it

        /// <summary>crane.js:821-838 — the card's buttons / the B-cycle.</summary>
        public void Press(WarehouseCrane.FanSwitch.Setting m)
        {
            _switch.Press(m);
            _voice?.Click(DistanceToCamera(), SoundVolume());
        }
        public void CycleButton()
        {
            _switch.CycleButton();
            _voice?.Click(DistanceToCamera(), SoundVolume());
        }

        void Awake()
        {
            _voice = MachineVoice.FanVoice(); // crane.js:715 `fanVoice()`
            if (Plane != null) _baseRotation = Plane.localRotation; // crane.js:1135 `base`, captured once at rest
            _axis = ComputeWindAxis();
            _rider = new WindRider(OwnBox)
            {
                SkinAt = (x, z) => RaySkinAt(x, z),
            };
            var def = Planes.PlaneById(WarehouseCrane.PlaneId);
            if (def != null) _fm = new FlightModel(
                new AeroPlaneDef { Mass = def.Mass, Prop = def.Prop, ClMax = def.ClMax, GBreak = def.GBreak, Tw = def.Tw, MaxSpeed = def.MaxSpeed },
                def.Span ?? 11.23f, (def.Span ?? 11.23f) * 0.5f);
            _initialized = true;
        }

        /// <summary>crane.js:1090-1096 `wind`/`axis` — "WHICH WAY THE WIND IS
        /// BLOWING, in the pivot's own frame... the fans stand off the nose
        /// and blow back down the shed's -X." Computed off the REAL, already
        /// GeoFrame-correct shed Transform rather than re-deriving the matrix
        /// algebra by hand (Unity's own TransformDirection already carries
        /// whatever rotation is baked into the live hierarchy — the same
        /// "use the real Transform, don't re-derive" principle this port's
        /// other bake files rely on).</summary>
        Vector3 ComputeWindAxis()
        {
            if (ShedRoot == null) return Vector3.forward;
            // Game-frame "-X" becomes Unity shed-local "+X" under the single
            // scene-wide mirror (negate once); i.e. the WORLD direction is
            // shedRoot's own local +X axis.
            Vector3 wind = ShedRoot.TransformDirection(Vector3.right);
            wind.y = 0f; return wind.sqrMagnitude > 1e-8f ? wind.normalized : Vector3.forward;
        }

        float DistanceToCamera()
        {
            var cam = Camera.main;
            if (cam == null || FanRotors == null) return 12f;
            float d = float.PositiveInfinity;
            foreach (var r in FanRotors) if (r != null) d = Mathf.Min(d, Vector3.Distance(cam.transform.position, r.position));
            return float.IsFinite(d) ? d : 12f;
        }
        float SoundVolume() => 0.7f; // crane.js:746 `citymap.game?.sound?.volume ?? 0.7` — Core.Audio's own mixer seam, not reached from here

        Vector3? RaySkinAt(float x, float z)
        {
            if (Plane == null || SkinColliders == null || SkinColliders.Length == 0) return null;
            Vector3 from = Plane.TransformPoint(new Vector3(x, OwnBox.MaxY + 1.5f, z));
            Vector3 dir = (Plane.TransformPoint(new Vector3(x, OwnBox.MaxY + 0.5f, z)) - from).normalized;
            var hits = Physics.RaycastAll(from, dir, OwnBox.Hgt + 3f);
            float best = float.PositiveInfinity; Vector3? bestPt = null;
            foreach (var h in hits)
            {
                if (!SkinColliders.Contains(h.collider)) continue;
                if (h.distance < best) { best = h.distance; bestPt = Plane.InverseTransformPoint(h.point); }
            }
            return bestPt;
        }

        void Update()
        {
            if (!_initialized) return;
            float dt = Time.deltaTime;
            if (_game == null) _game = FindAnyObjectByType<GameLoop>();

            // ---- crane.js:741-756 — the fan ramp + voice ------------------
            float p = _switch.Tick(dt); // 0..1 of AirLo (the switch's own fraction)
            float airspeedKmh = _switch.Airspeed;
            _voice.Update(dt, _switch.Airspeed, WarehouseCrane.AirHi, DistanceToCamera(), SoundVolume());
            if (FanRotors != null)
            {
                float step = _switch.BladeAngularRate * dt;
                foreach (var r in FanRotors) if (r != null) r.Rotate(0f, 0f, step * Mathf.Rad2Deg, Space.Self);
            }
            UpdateLamp();

            // ---- crane.js:1848-1930 — the wind tunnel + the rider ---------
            if (SwingPivot != null && Plane != null && _fm != null)
            {
                float weightN = _fm.Mass * 9.81f;
                bool still = WindTunnel.Step(ref _wind, dt, airspeedKmh, weightN, _fm.S, _fm.Cd0, _fm.KInduced,
                    _axis, _baseRotation,
                    out var swingRot, out var planeLocalRot, out float haulFraction,
                    out float flowOpacity, out float scrollDx, out float scrollDy, out float propRpm, _fm.ClMax);

                SwingPivot.localRotation = swingRot;
                Plane.localRotation = planeLocalRot;
                Plane.localPosition = new Vector3(Plane.localPosition.x,
                    PlaneHangY * (1f - (1f - WindTunnel.HaulUp) * haulFraction), Plane.localPosition.z);

                // crane.js:1911-1912/1920-1927 — the wind_tunnel_flow haze
                // sheets' visibility/opacity/UV-scroll. NOW CONSUMED (this
                // pass): Props/WindFlowMesh.cs supplied the mesh, Props/
                // WindTunnel.Step above already computed these three numbers
                // every frame before this — they had nothing to apply them
                // to until WarehouseCraneFill.BuildFlowMesh existed.
                if (FlowRenderer != null)
                {
                    bool showFlow = !still && flowOpacity > 0.02f; // crane.js:1911 (still->false) / :1920 (p>0.02->true)
                    FlowRenderer.enabled = showFlow;
                    if (showFlow)
                    {
                        // tex.offset is PERSISTENT in the JS (crane.js:1926-1927
                        // `tex.offset.x -= ...; tex.offset.y += ...`) — accumulated
                        // here across frames the same way, via Material.mainTextureOffset.
                        _flowOffset.x += scrollDx;
                        _flowOffset.y += scrollDy;
                        var flowMat = FlowRenderer.material;
                        flowMat.mainTextureOffset = _flowOffset;
                        flowMat.SetFloat("_Opacity", flowOpacity);
                    }
                }

                // crane.js:1928 `spinPropellers(plane, PROP_RPM*p, dt)`.
                if (Propellers != null && Propellers.Count > 0)
                    PropellerSystem.SpinPropellers(Propellers, still ? 0f : propRpm, dt);

                // crane.js's own `rideWind(dt, p)` — p=0 while "still" (hands
                // the body back to foot.js, crane.js:1862/1917), the real p
                // once the wind has him.
                float riderP = still ? 0f : p; // `p` is already Airspeed/AirLo, FanSwitch.Tick's own return
                if (_game != null)
                    _rider.Tick(_rideState, dt, riderP, _game.Foot, _game.Rig, Plane, _axis,
                        _game.Mode == GameMode.OnFoot);
            }

            // ---- crane.js:759-763 — "the card you get when you walk up to
            // it", distance-gated exactly like the firing-point lane cards.
            if (_game != null && _game.Foot != null)
                NearSwitch = (_game.Foot.Pos - transform.position).sqrMagnitude <= SwitchReach * SwitchReach;

            // crane.js:833-840 — B, when standing at the board, walks round
            // the three settings. The visual #fan-ctl card (state readout +
            // lo/hi buttons) is still the documented UI/Hud scope-cut above;
            // this closes the GAME-LOGIC half, which needed no UI: pressing B
            // near the board now actually cycles the fans, matching Press()/
            // CycleButton()'s own long-ready, previously uncalled surface.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            HandleBKey(kb != null && kb.bKey.wasPressedThisFrame);
        }

        /// <summary>crane.js:834-839's gate, split out pure (no Keyboard
        /// dependency) so a probe can drive it directly — the same
        /// "mechanical extraction, zero behaviour change" split WindRider.
        /// OnWing()/AdvanceOnWing() already use for an edit-mode-testable
        /// surface. `pressed` is Update()'s own `kb.bKey.wasPressedThisFrame`;
        /// everything NearSwitch-dependent is real, live state.</summary>
        internal void HandleBKey(bool pressed)
        {
            if (NearSwitch && pressed) CycleButton();
        }

        void UpdateLamp() // crane.js:728-736 `setLamp()`
        {
            var (lamp, emissive, button) = WarehouseCrane.LampColors(_switch.Mode);
            if (LampRenderer != null && LampRenderer.material != null)
            {
                LampRenderer.material.color = lamp;
                if (LampRenderer.material.HasProperty("_EmissionColor"))
                {
                    LampRenderer.material.SetColor("_EmissionColor", emissive);
                    LampRenderer.material.EnableKeyword("_EMISSION");
                }
            }
            if (ButtonRenderer != null && ButtonRenderer.material != null)
                ButtonRenderer.material.color = button;
        }

        void OnDestroy() => _voice?.Dispose();
    }
}
