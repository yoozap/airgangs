using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// ONE AEROPLANE'S PROPELLERS, built once and spun every frame off the
    /// engine's revs — src/game.js:2583-2585:
    ///
    ///     // 0..1 of max revs -> real crankshaft rpm, for the blur threshold.
    ///     // A piston aero engine idles near 700 and redlines around 2700.
    ///     spinPropellers(this.plane, this.sound.rpm * 2700, dt);
    ///
    /// <see cref="PlaneProps.Build"/> does the detection/cut/finish (the two
    /// src/planes.js call sites); this component is the third piece, the
    /// per-frame drive. It is attached by the two places a real aeroplane gets
    /// built in this port:
    ///   * Flight/PlaneHallFill.cs — the 35 parked machines, so their blades
    ///     exist, carry Combat.SpinningPartTag and have a blur disc. They
    ///     never spin: <see cref="Rpm"/> is 0 for anything that is not the
    ///     plane the player is flying, which is also how the JS's own
    ///     "engine off when the pilot leaves" invariant falls out (the engine
    ///     voice's rpm runs down to 0 on shutdown).
    ///   * Flight/PlaneController.Init — the machine being flown, including
    ///     one the player walked up to and took with Enter (GameLoop.
    ///     SwapToPlane calls Init on it, and this component is idempotent).
    ///
    /// WHY THE RPM IS READ AND NOT PUSHED: src/game.js spins the propellers
    /// from inside its own engine/hood stage, i.e. Core/GameLoop.cs's
    /// StageEngineAndHood — Core/ is owned elsewhere this pass, so rather
    /// than leave the feature unwired for want of one line in another agent's
    /// file, the rpm is read back off Core/EngineSound the same way
    /// Props/WarehouseFanSwitch.cs reads GameLoop for the wind tunnel
    /// (FindAnyObjectByType, cached). If Core/ ever adds the push, delete
    /// <see cref="Rpm"/>'s lookup here — do not run both.
    ///
    /// LateUpdate, not Update: a propeller write is a Transform write on a
    /// model whose Animator (the two rigged aircraft) must have run first —
    /// the project's standing rule.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlanePropellers : MonoBehaviour
    {
        /// <summary>src/game.js:2585 — 0..1 of max revs to real crankshaft rpm.
        /// "A piston aero engine idles near 700 and redlines around 2700."</summary>
        public const float MaxCrankRpm = 2700f;

        /// <summary>The plane-catalog row this airframe is, for PlaneProps.Build.</summary>
        public string PlaneId = "p51";

        /// <summary>THE FILE FRAME, not the flight frame. PlaneProps.Build
        /// bakes every vertex into this Transform's local space and compares
        /// it against <see cref="PlaneProps.FileNose"/> and against the
        /// hand-measured hub hints in src/planes.js — all of which are quoted
        /// in the ASSET's own stored orientation, before src/planes.js:834's
        /// straightening and before def.Scale. Flight/PlaneModelFrame's
        /// `model_file` node is exactly that frame; left null it resolves to
        /// it (and to `transform` on a plane with no chain, which is the
        /// pre-straightening behaviour, so the detection numbers are
        /// unchanged either way — PROPSUM stays 31/31).</summary>
        public Transform ModelRoot;

        /// <summary>src/planes.js:1593 `wrapper.userData.propellers`.</summary>
        public List<PropellerSystem.PropellerInstance> Props { get; private set; }
            = new List<PropellerSystem.PropellerInstance>();

        /// <summary>How many propellers were actually found on this airframe —
        /// for the probe, and for an honest log line at build time.</summary>
        public int Count => Props.Count;

        /// <summary>The revs this component span the blades at last frame.</summary>
        public float Rpm { get; private set; }

        GameLoop _game;
        bool _built;
        bool _restZeroed;    // one zero-rpm pass has already settled the disc/blades

        // NO `void OnEnable() => EnsureBuilt();` — IT SELF-BUILT AS A P-51.
        //
        // Outside play mode AddComponent does not deliver OnEnable, which is
        // why both call sites assign PlaneId/ModelRoot and then call
        // EnsureBuilt() by hand. IN A PLAYER IT IS DELIVERED SYNCHRONOUSLY,
        // inside the AddComponent call itself — one line BEFORE
        // `props.PlaneId = p.Def.Id`. At that moment PlaneId still holds its
        // FIELD INITIALISER, "p51", so every parked machine detected itself
        // against the Mustang's def, found that one propeller, latched _built,
        // and made the later explicit EnsureBuilt() a no-op. Measured in the
        // first real player run: `PROPELLERS planes=15 propellers=15` against
        // the editor's `planes=15 propellers=31`, with `how=?` on 14 of the 15
        // PROPELLER lines (only "p51" was ever cached in PlaneProps._plans) and
        // a single blade on each four-engine machine (yc130/c130/carpet/
        // lancaster/xb35, want=4). A guard on PlaneId being empty does NOT fix
        // it — the initialiser is a real id.
        //
        // Nothing needs the self-build: no scene serialises this component
        // (its guid appears 0 times in all four .unity files), and all three
        // creators — Flight/PlaneHallFill.cs, Flight/PlaneController.cs and
        // Core/StarterPlaneSkin.cs — already configure it and then call
        // EnsureBuilt() explicitly. If a future scene ever does bake one in,
        // give that path its own [SerializeField] "configured" marker rather
        // than reinstating an OnEnable that cannot know whether its fields have
        // been written yet.

        /// <summary>Build this airframe's propellers if they are not built
        /// yet. Safe to call repeatedly and safe to call from editor code
        /// (an AddComponent outside play mode never delivers OnEnable, so
        /// both call sites call this explicitly straight after attaching —
        /// the trap .claude/skills/unity/SKILL.md documents for probes).</summary>
        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            var def = Planes.PlaneById(PlaneId);
            if (ModelRoot == null) ModelRoot = PlaneModelFrame.FileFrameOf(transform);
            Props = PlaneProps.Build(ModelRoot.gameObject, def);
        }

        void LateUpdate()
        {
            if (Props == null || Props.Count == 0) return;

            float rpm = EngineRpm();
            // A parked machine is the common case by 20 to 1: once it has had
            // one zero pass (disc hidden, blades back to full opacity) there is
            // nothing left to do until its engine turns over.
            if (rpm <= 0f && _restZeroed) { Rpm = 0f; return; }
            _restZeroed = rpm <= 0f;
            Rpm = rpm;
            PropellerSystem.SpinPropellers(Props, rpm, Time.deltaTime);
        }

        /// <summary>src/game.js:2585's `this.sound.rpm * 2700`, but only for
        /// the aeroplane the player is actually flying. Everything else reads
        /// 0: a parked machine's engine is off, and so is the engine of a
        /// machine the pilot has climbed out of (GameLoop enforces that
        /// separately — see StageEngineAndHood — so this needs no second
        /// shutdown, it simply follows the revs down).</summary>
        float EngineRpm()
        {
            if (_game == null) _game = FindAnyObjectByType<GameLoop>();
            if (_game == null || _game.Engine == null) return 0f;
            var active = _game.ActivePlane;
            if (active == null || active.transform != transform) return 0f;
            return Mathf.Max(0f, _game.Engine.Rpm) * MaxCrankRpm;
        }
    }
}
