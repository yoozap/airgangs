using System;
using UnityEngine;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Everything HudView.cs reads off <c>g</c> (the Game singleton) in
    /// src/hud.js's update()/_reticle()/_airdata() — src/hud.js:126-329.
    ///
    /// WHY A SNAPSHOT STRUCT AND NOT A LIVE GAME REFERENCE: this port runs as
    /// 4 agents in parallel on independent systems (docs/UNITY_PORT.md,
    /// "Decisions (2026-10-26)"); Core ("game.js" equivalent) and Flight are
    /// somebody else's phase and may not exist yet as compiled types while
    /// this file is being written. The task boundary for this phase is also
    /// explicitly "Scripts/UI/ only". So HudView depends on this plain-data
    /// struct instead of FlightModel/Game directly — whoever builds Core
    /// fills one of these every frame and calls HudView.Tick(data). That is
    /// also, incidentally, the idiomatic UI Toolkit pattern (a presentation
    /// view fed by a snapshot) rather than a God-object reference.
    /// </summary>
    public struct HudData
    {
        public bool Started;     // src/hud.js:131 g.started
        public bool Crashed;     // src/hud.js:131 g.crashed
        public bool Airborne;    // src/hud.js:268 g.airborne

        public Camera Camera;    // for world->screen projection, src/hud.js:55
        public Transform Plane;  // g.plane — position + quaternion only, hud.js:150-153
        public Vector3 Velocity; // g.vel, hud.js:268/303/320
        public float LastGroundDistance; // g.lastGroundDistance, hud.js:319

        // src/aero.js live readouts (FlightModel.cs's Alpha/N/Stalled/Overspeed)
        public float Alpha;       // radians; hud.js:310 a.alpha
        public float LoadFactorN; // hud.js:312 a.n
        public bool Stalled;      // hud.js:326 a.stalled
        public float Overspeed;   // hud.js:327 a.overspeed

        public bool RocketSelected; // hud.js:206 (g.skillbar?.current ?? 'gun') === 'rocket'

        /// <summary>
        /// hud.js:221-238's boresight range sample: a ray from the plane's
        /// nose out to 3000 m against the spatially-hashed world, re-cast at
        /// most every 0.15 s. The actual cast (against City's collidables
        /// hash) is Combat/City territory, outside this phase's scope, so
        /// Core hands the HUD a plain delegate instead: (origin, dir,
        /// maxDist) -> hit distance, or null for no hit. HudView still owns
        /// the 0.15 s re-sample CADENCE itself (hud.js:222-224) — that part
        /// really is HUD behavior, not physics.
        /// </summary>
        public Func<Vector3, Vector3, float, float?> BoresightCast;
    }
}
