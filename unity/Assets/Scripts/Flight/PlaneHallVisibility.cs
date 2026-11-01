using UnityEngine;

namespace TbilisiFly.Flight
{
    /// <summary>Port of src/planehall.js:146-194 `PlaneHall.update`/`_walled` —
    /// the RUNTIME half PlaneHallLayout.cs's own header left for "whoever
    /// builds the MonoBehaviour... once that pipeline exists" (it now does:
    /// PlaneHallFill.Fill() bakes the real fleet at editor time and attaches
    /// one of these to the hall root).
    ///
    /// WHY THIS MATTERS: three.js has no occlusion culling, so a hall inside
    /// the show-distance radius draws all ~35 parked machines THROUGH its own
    /// walls — src/planehall.js:62-67 measured this at 1,087 meshes nobody can
    /// see. Unity's own frustum/occlusion culling does not make a hangar's
    /// contents disappear through its walls either (a BoxCollider hangar is
    /// not an occluder), so the same switch is worth keeping: hide the whole
    /// fleet by one GameObject.SetActive rather than trust per-renderer
    /// culling to do it for free.
    ///
    /// ONE MonoBehaviour PER FILE, NAMED AFTER IT (see the unity skill) — this
    /// file declares only PlaneHallVisibility.
    ///
    /// PORTED (this pass): _cockpits' per-aeroplane cockpit-kit draw-distance
    /// toggle (:196-219, KitDist = PlaneHallLayout.KitDist) — see
    /// <see cref="Cockpits"/>/<see cref="UpdateCockpits"/> below.
    /// PlaneHallFill.Fill() now calls PlaneCockpitBaker.Bake(flyable:false)
    /// per parked plane (src/planehall.js:240-243's own scenery-only
    /// convention) and hands this component the resulting kit GameObjects
    /// through <see cref="Cockpits"/>, exactly the `(Transform plane,
    /// Component kit)[]` shape this file's own header used to ask for.
    ///
    /// NOT PORTED, deliberately, and why:
    ///   - Registering/hiding each plane as a Combat target
    ///     (citymap.targets?.register, src/planehall.js:290, and the "marks
    ///     must hide when the fleet does" lesson) — the Combat target
    ///     registry is a sibling agent's territory (UI/Target/*, Combat/*),
    ///     not touched here. <see cref="FleetVisible"/> is exposed as a public
    ///     bool specifically so a Combat-owned script can read it each frame
    ///     and hide this hall's target markers in lockstep, without this file
    ///     reaching into Combat itself. EXACT WIRING NEEDED: whatever
    ///     per-target visibility flag Combat's target registry exposes should
    ///     be set to `hallVisibility.FleetVisible` for every target registered
    ///     from this hall's `ParkedPlanes` — a few lines once someone owns it.
    ///   - ShouldFill/the async load-one-per-frame gate (:146-161) has no
    ///     Unity analogue: PlaneHallFill.Fill() is an EDITOR-TIME bake, not a
    ///     runtime load, so by the time this component exists in a running
    ///     scene the fleet is already built. Only the SHOW_DIST switch below
    ///     is still live at runtime.</summary>
    public sealed class PlaneHallVisibility : MonoBehaviour
    {
        /// <summary>"planehall_fleet" — every parked aeroplane under one
        /// switch, src/planehall.js:228-230,307. Hidden/shown as a whole.</summary>
        public GameObject Fleet;

        /// <summary>The hall's own floor-plan half-extents, hall-local metres
        /// (ShedFill.ShedBuildResult.Shell.Width/Length) — src/planehall.js:183
        /// `d.width`/`d.length`.</summary>
        public float Width, Length;

        PlaneHallLayout.HallAxes _axes;
        Vector3 _centre;
        bool _axesReady;
        bool _lastActive = true;

        /// <summary>True exactly when src/planehall.js's `this.fleet.visible`
        /// would be this frame — see this file's header for why a sibling
        /// Combat script should read this rather than duplicate the math.</summary>
        public bool FleetVisible { get; private set; } = true;

        /// <summary>src/planehall.js:196-219 `_cockpits`'s per-plane list —
        /// `PlanePos` is the plane's real WORLD position at bake time (the
        /// JS's own frozen `matrixWorld`-derived `{x, z}`), `Kit` the baked
        /// cockpit-kit GameObject (src/planehall.js's `p.userData.cockpitKit`)
        /// to show/hide. Set once by PlaneHallFill.Fill(); a plane the hero
        /// has climbed into and flown away leaves the fleet entirely (same
        /// "skipped: k.p.parent !== this.fleet" guard the JS applies, done
        /// here by checking the kit's own GameObject is still active in the
        /// hierarchy under Fleet before touching it).</summary>
        public struct CockpitEntry { public Vector3 PlanePos; public GameObject Kit; }
        public CockpitEntry[] Cockpits = System.Array.Empty<CockpitEntry>();

        void EnsureAxes()
        {
            if (_axesReady) return;
            // src/planehall.js:177-190 `_axes`, memoised once off the hall's
            // own world matrix. Transform.right/forward are already UNIT
            // vectors in Unity regardless of scale (unlike THREE's raw matrix
            // columns, which the JS has to re-normalise by hand at :181-182)
            // — and ShedFill bakes its SCALE struct directly into the
            // generated geometry rather than into Transform.localScale (see
            // ShedFill.cs's header), so the hall root's own localScale is 1
            // and no further rescale of Width/Length is needed here.
            var t = transform;
            _axes = new PlaneHallLayout.HallAxes
            {
                Ux = t.right.x, Uz = t.right.z,
                Vx = t.forward.x, Vz = t.forward.z,
                HalfW = Width / 2f,
                HalfL = Length / 2f,
            };
            _centre = t.position;
            _axesReady = true;
        }

        /// <summary>The per-call logic Update() drives, split out so a probe
        /// can exercise it with a known camera position without a running
        /// Unity play loop (no Camera.main / no Update() callback needed
        /// outside play mode). Mutates FleetVisible and returns it.</summary>
        public bool ComputeVisible(Vector3 camPos)
        {
            EnsureAxes();
            bool near = PlaneHallLayout.FleetVisible(camPos.x, camPos.z, _centre.x, _centre.z, _axes);
            FleetVisible = near;
            return near;
        }

        void Update()
        {
            if (Fleet == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            // src/planehall.js:146-156 update()'s fleet-visibility gate —
            // pure distance/wall math, ported verbatim in PlaneHallLayout.
            bool near = ComputeVisible(cam.transform.position);
            if (near != _lastActive)
            {
                Fleet.SetActive(near);
                _lastActive = near;
            }
            UpdateCockpits(cam.transform.position);
        }

        /// <summary>src/planehall.js:196-219 `_cockpits`, split out (like
        /// <see cref="ComputeVisible"/> above) so a probe can drive it with a
        /// known camera position with no running Update() loop.</summary>
        public void UpdateCockpits(Vector3 camPos)
        {
            for (int i = 0; i < Cockpits.Length; i++)
            {
                var c = Cockpits[i];
                if (c.Kit == null) continue;
                // src/planehall.js:206 `if (k.p.parent !== this.fleet) continue`
                // — a plane the hero climbed into and flew away is no longer
                // the hall's; its frozen PlanePos would stale-hide the cockpit
                // the player is sitting in.
                if (Fleet != null && !c.Kit.transform.IsChildOf(Fleet.transform)) continue;
                bool on = PlaneHallLayout.CockpitVisible(camPos.x, camPos.z, c.PlanePos.x, c.PlanePos.z);
                if (c.Kit.activeSelf != on) c.Kit.SetActive(on);
            }
        }
    }
}
