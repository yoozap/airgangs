using System;
using TbilisiFly.Combat;

namespace TbilisiFly.Core
{
    /// <summary>
    /// The FX surface a BUILDING COLLAPSE publishes into — the Unity counterpart of
    /// the three assignments src/game.js:427-431 makes on the CityMap it just built:
    ///
    ///     this.world.onWindowsBlown = (list) => this.glass.burstList(list, 1.3, 60);
    ///     this.world.onBlast = (x, y, z, r) => this.blast.explode({x,y,z}, null, { r, marks: false });
    ///     this.world.onShake = (s, x, z) => ...   // distance-scaled camera kick
    ///
    /// Those three are NOT the gun/rocket glass path (that one is already wired:
    /// CombatWorldAdapter.Install subscribes MachineGun/RocketLauncher/ImpactFx's
    /// OnGlassBurst to BreakingGlass.BurstList). They are the COLLAPSE path:
    /// citymap.js:2833 blows every pane off a building that just dropped a crumble
    /// stage or went down, and citymap.js:2402/2472/2515 are the fireballs of the
    /// collapse itself — ignition, the secondaries tearing through the shell as it
    /// pancakes, and the ground slam. Until this existed the C# collapse raised
    /// nothing at all (City/CityCulling.cs's CollapseAnim doc said so itself:
    /// "NOT PORTED: the dust/smoke/secondary-blast FX ... fx.explosion/onBlast/
    /// _launchDebris").
    ///
    /// Assigned ONCE, as a whole object, by CombatWorldAdapter.Install() into
    /// CombatBuildingChunk.Fx — the same shape CrashSequence's CrashHooks uses, and
    /// deliberately an assignment rather than `+=` so a second Install() (editor
    /// domain reload, a re-boot in the same session) replaces the handlers instead
    /// of stacking a second copy of every fireball on top of the first.
    ///
    /// Every member is optional: a null hook means "that FX system is not in this
    /// scene", which is exactly what the JS's own `this.onBlast?.(...)` /
    /// `if (this.fx)` guards mean.
    /// </summary>
    public sealed class CollapseFxHooks
    {
        /// <summary>src/game.js:427 `world.onWindowsBlown` — a flat
        /// [x, y, z, nx, nz, ...] run of panes that just left their facade, straight
        /// into Combat/BreakingGlass.BurstList(list, 1.3, 60). Same five-wide shape
        /// IDamageWorld.BlastWindows returns (see its doc on why that is
        /// load-bearing); citymap.js:2833 is the only publisher.</summary>
        public Action<float[]> OnWindowsBlown;

        /// <summary>src/game.js:429 `world.onBlast` — (x, y, z, radius) of a
        /// secondary fireball during a collapse, into Combat/BlastFx.Explode with
        /// no surface normal and marks OFF (a collapse must not stamp rocket
        /// craters all over the block). citymap.js:2402 (ignition), :2472 (the
        /// mid-fall secondaries) and :2515 (the ground slam).</summary>
        public Action<float, float, float, float> OnBlast;

        /// <summary>src/game.js:431 `world.onShake` — (strength, x, z); the
        /// distance falloff lives in the handler (CombatWorldAdapter.OnShake), not
        /// here, exactly as in the JS. citymap.js:1554 (stage >= 3), :2404
        /// (collapse start, 0.45) and :2520 (the slam, 1.0).</summary>
        public Action<float, float, float> OnShake;

        /// <summary>The dust: citymap.js:2423-2440's `fx.spawnSmoke` collar and
        /// :2402/:2512's `fx.plume`/`fx.wave`. Core/CombatWorldAdapter.FxSink
        /// already implements this interface over Fx/FireSystem, so the collapse
        /// gets the real smoke system rather than a second delegate per call.
        /// Null in a scene with no fire system (and in the probes).</summary>
        public IFireFx Fire;

        /// <summary>Terrain height under (x, z) in UNITY space, or null off the
        /// grid — citymap.js:2509's `this.heightAt(rec.cx, rec.cz) ?? rec.base`,
        /// which is where the ground-slam fireball and shockwave ring sit.</summary>
        public Func<float, float, float?> GroundAt;

        /// <summary>THE DEBRIS: `_launchDebris(rec, n, fromY)` —
        /// (cx, cz, span, fromY, n). Published at all three of the JS's own call
        /// sites: citymap.js:2405 (collapse start, `8 + round(span/5)` pieces
        /// from `base + h*0.7`), :2473 (3 pieces per mid-fall secondary blast,
        /// at that blast's own height) and :2521 (the ground slam,
        /// `6 + round(span/6)` from `base + 2`).
        ///
        /// This was the ONE line of this file's family that was deliberately
        /// left out when it was written ("NOT ported here, and deliberately:
        /// _launchDebris/_spawnRubble ... it needs an INSTANCED RUBBLE RENDERER
        /// fed from public/models/rubble_kit.glb — nothing in the port loads
        /// that GLB yet. Publishing a hook with no consumer would just be a
        /// fourth dead delegate", Core/CombatBuildingChunk.cs). There is a
        /// renderer now (Fx/RubbleFx.cs over the imported
        /// Assets/Resources/Art/Props/rubble_kit.fbx) and a consumer
        /// (Core/CollapseDebris.cs, wired in CombatWorldAdapter.Install), so
        /// the hook is no longer a delegate leading nowhere.</summary>
        public Action<float, float, float, float, int> OnDebris;

        /// <summary>citymap.js:2505 `this._spawnRubble(rec)` — the PERMANENT
        /// rubble mound a fully collapsed building leaves, placed once from the
        /// footprint bbox: (minX, maxX, minZ, maxZ, baseY, top). Count scales
        /// with area, clamped [10, 46] (DebrisField.SpawnRubble).</summary>
        public Action<float, float, float, float, float, float> OnRubble;

        /// <summary>citymap.js:1553 `if (stage >= 6) this._shedDebris(rec, stage)`
        /// — 4 chunks spalling off a random wall as a building passes crumble
        /// stage 6, gated once per stage by the JS's own `rec.shedAt`:
        /// (minX, maxX, minZ, maxZ).</summary>
        public Action<float, float, float, float> OnShed;
    }
}
