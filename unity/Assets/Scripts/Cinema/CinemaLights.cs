using UnityEngine;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js:1446-1478 `_flashLight()`/`_lights()` — the two
    // lights the reel brings with it: a muzzle flash that is the only light
    // for a frame, and a warm key off the hero's gun side plus a cold rim
    // behind him (the city's sun is one hard source from the south-east,
    // which leaves whichever side of him is away from it flat — cinema.js:
    // 1463-1465's own design note).
    //
    // PORTED, as pure data + pure math: both functions' ARITHMETIC in full —
    // neither reads or writes a live Light component, a pistol, or the DOM;
    // a caller applies the returned positions/intensity to its own
    // Light/Transform pair every frame CinemaReel.Tick runs (same "wiring
    // left for whoever owns GameLoop" boundary CinemaReel.cs's own header
    // already draws for this class).
    //
    // NOT PORTED: reading the pistol's own flash state (cinema.js:1447
    // `this.game.pistol`) and the "driven by INTENSITY, never by .visible"
    // reasoning for why (cinema.js:1449-1453, the 36-material-recompile
    // freeze) — Combat's territory; FlashIntensity below takes the two
    // booleans/floats that state already reduces to (`flashVisible`,
    // `flashTimer`) rather than reaching into Combat for them.
    //
    // ============================== HANDEDNESS ==============================
    // No cross product anywhere in this file. `right`/`fwd` are taken as
    // GIVEN, already-correct Unity-frame vectors (StageFrame's own `Stage.
    // Right`/`Stage.Fwd` — CinemaStage.cs's header already did the one
    // derivation this whole scene needs, `RightOfForward`); every operation
    // here is a scalar-weighted sum of vectors already in the right frame,
    // which commutes fine with whatever mirroring produced them.
    // =========================================================================
    public static class CinemaLights
    {
        /// <summary>crane.js's sibling in cinema.js:1466-1478 `_lights()` —
        /// the key/rim light pair's position and target, off the hero's own
        /// position and the stage's right/forward axes. `heroPos` is
        /// `foot.pos` (cinema.js:1469 `const h = this.foot.pos`).</summary>
        public readonly struct LightRig
        {
            public readonly Vector3 KeyPosition, KeyTarget, RimPosition, RimTarget;
            public LightRig(Vector3 keyPos, Vector3 keyTarget, Vector3 rimPos, Vector3 rimTarget)
            {
                KeyPosition = keyPos; KeyTarget = keyTarget;
                RimPosition = rimPos; RimTarget = rimTarget;
            }
        }

        /// <summary>cinema.js:1466-1478, in full.</summary>
        public static LightRig ComputeLights(Vector3 heroPos, Vector3 right, Vector3 fwd)
        {
            Vector3 keyPos = heroPos + Vector3.up * 3.4f + right * 2.6f + fwd * 2.2f;
            Vector3 keyTarget = heroPos + Vector3.up * 1.1f;
            Vector3 rimPos = heroPos + Vector3.up * 2.9f - right * 2.2f - fwd * 3.0f;
            Vector3 rimTarget = heroPos + Vector3.up * 1.2f;
            return new LightRig(keyPos, keyTarget, rimPos, rimTarget);
        }

        /// <summary>cinema.js:1446-1461 `_flashLight()`, the intensity
        /// formula only (the light's POSITION just copies the pistol's own
        /// muzzle transform every frame it is on, cinema.js:1459 — trivial
        /// transform-copy glue, a caller's job). `flashVisible` mirrors
        /// `p.flash.visible`; `flashTimer` mirrors `p._flash` (seconds left
        /// on the muzzle flash, cinema.js:1454's own `p._flash > 0` gate).
        /// Returns 0 when off, exactly matching the JS's own early return
        /// (cinema.js:1455-1458) rather than leaving a stale value for a
        /// caller to separately gate on `on`.</summary>
        public static float FlashIntensity(bool flashVisible, float flashTimer)
        {
            bool on = flashVisible && flashTimer > 0f;
            if (!on) return 0f;
            return 55f * Mathf.Clamp01(flashTimer / 0.045f);
        }
    }
}
