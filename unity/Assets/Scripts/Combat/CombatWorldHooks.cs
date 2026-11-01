using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Shared hook surface between Combat and the systems it damages but does not
    /// own (City's buildings/windows, Flight's planedamage.js — both out of this
    /// port's scope). Every JS call like `g.world.damageHit?.(...)` or
    /// `g.planeDamage?.refine(...)` becomes one of these, so Guns/Rockets/
    /// Shooting/ImpactFx compile and read correctly today and the City/Flight
    /// agents have one narrow surface to implement against instead of reaching
    /// into Combat's files.
    ///
    /// None of this is guessed at random: every field/method here is read
    /// directly off what src/guns.js, src/rockets.js and src/impactfx.js
    /// actually call on `g.world` / `g.planeDamage` / `g.glass` / `g.fx`.
    /// </summary>
    public struct DamageResult
    {
        public bool Changed;      // a crumble stage actually advanced
        public bool Destroyed;    // the building is gone
        public string Addr;       // real street address, for the hit toast (src/guns.js:625)
        public float HpFrac;      // 0..1 remaining, for the toast's bar colour
        public float Cx, TopNow, Cz, Span; // where/how big the new roofline is, for the dust plume
        /// <summary>Opaque building/window record, handed back unchanged to NearWindow/
        /// PunchHole/IgniteBlast — Combat never looks inside it.</summary>
        public object Rec;
    }

    public interface IDamageWorld
    {
        /// <summary>src/guns.js:585, src/impactfx.js:716 — apply damage, return the
        /// updated state (null if the hit landed on something with no HP, e.g. the
        /// ground or a statue).</summary>
        DamageResult? DamageHit(RaycastHit hit, float damage);
        /// <summary>src/guns.js:595 nearWindow — is this point within the 1.7 m ball
        /// around a window cell of this building record.</summary>
        bool NearWindow(Collider hitObject, object rec, Vector3 point);
        /// <summary>src/rockets.js:305, src/impactfx.js:874 blastWindows — every pane
        /// within radius of (x,y,z) is knocked out; returns what was blown, or
        /// null/empty for none.
        ///
        /// FLAT and FIVE-WIDE, not a list of objects: src/citymap.js's
        /// blastWindows ends by flattening its records into
        /// `[x, y, z, nx, nz, x, y, z, nx, nz, ...]` **sorted nearest-first**,
        /// and src/glass.js:246 burstList reads it in strides of 5 and spends
        /// its shard budget on the leading entries precisely because of that
        /// ordering. This was declared `object[]` as a "Combat need not know the
        /// pane shape" placeholder, which made it structurally impossible to
        /// hand to BreakingGlass.BurstList(IReadOnlyList&lt;float&gt;) — so every
        /// call site found panes, then dropped them. Keep the stride and the
        /// order.</summary>
        float[] BlastWindows(float x, float y, float z, float radius);
        /// <summary>src/rockets.js:299 punchHole — tears a real hole in the facade
        /// centred on point with the given radius.</summary>
        object PunchHole(object rec, Vector3 point, float radius);
        /// <summary>src/rockets.js:301 stripPropsAt — everything bolted on inside the
        /// hole radius comes off (signs, shutters, AC units…).</summary>
        /// Returns the GLASS it tore out, in the same FLAT five-wide shape as
        /// BlastWindows above — src/citymap.js:2370 pushes
        /// `glass.push(x, y, z, arr[o+8], arr[o+10])` and src/rockets.js:302
        /// hands the result straight to `glass.burstList(ripped, 1.9)`, the very
        /// same call blastWindows feeds. It was declared `object[]` for the same
        /// placeholder reason and had the same consequence: the panes a rocket
        /// tore out could never be burst.
        float[] StripPropsAt(float x, float y, float z, float radius);
        /// <summary>src/rockets.js:313 igniteBlast — leaves a fire burning in the hole.</summary>
        void IgniteBlast(DamageResult res, Vector3 point, float radius);
        /// <summary>src/rockets.js:309 blastDebris — chunks torn out of whatever was hit.</summary>
        void BlastDebris(float x, float y, float z, Vector3 normal, int count);
        /// <summary>src/rockets.js:287 game.world.onShake — a camera/ground shake, scaled
        /// 0..1-ish by power. Namespaced under world in the JS even though it reads as a
        /// Core/camera concern; kept here so the call site matches 1:1.</summary>
        void OnShake(float power, float x, float z);
        /// <summary>src/ballistics.js classifySurface's city-side structural tags —
        /// see IBallisticsWorld in Ballistics.cs, which this typically also implements.</summary>
    }

    /// <summary>
    /// src/planedamage.js is explicitly OUT OF Combat's scope (shader-heavy,
    /// scheduled separately), but Guns/Rockets/ImpactFx all call into it for
    /// three things. Mirrors the calls exactly so Flight can implement this
    /// without touching any Combat file.
    /// </summary>
    public interface IPlaneDamageHooks
    {
        /// <summary>src/ballistics.js:551, src/guns.js:581 — a hit landed on a plane's
        /// climb-shell collider (a box, not the skin); re-cast onto the real airframe
        /// panel. Null = the crate lied, nothing is there, the round/tracer flies on.</summary>
        RaycastHit? Refine(RaycastHit hit, Vector3 dir);
        /// <summary>src/impactfx.js:722 — the bulleted-sim path: let planedamage mark
        /// its own hole/spall on the airframe. Returns true if it handled the hit
        /// (ImpactFx then does nothing further for this round).</summary>
        bool Mark(ImpactInfo info);
        /// <summary>src/impactfx.js:779 — the hitscan path (the belly gun): same as
        /// Mark but built from the raw hit/point/normal/dir/cartridge instead of a
        /// resolved ImpactInfo. Returns the surface id it classified as, or null.</summary>
        string Trace(RaycastHit hit, Vector3 point, Vector3 normal, Vector3 dir, Cartridge cart);
    }

    /// <summary>No-op stand-in so Guns/Rockets/Shooting/ImpactFx can be constructed and
    /// unit-probed before City/Flight wire the real hooks in.</summary>
    public sealed class NullPlaneDamageHooks : IPlaneDamageHooks
    {
        public RaycastHit? Refine(RaycastHit hit, Vector3 dir) => hit;
        public bool Mark(ImpactInfo info) => false;
        public string Trace(RaycastHit hit, Vector3 point, Vector3 normal, Vector3 dir, Cartridge cart) => null;
    }
}
