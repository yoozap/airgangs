using System;
using UnityEngine;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js's `hangPlane` (crane.js:318-474) — the four strops
    // from the hook block down to the wing roots, and the resting pose of
    // the Mustang hanging from the pivot. WarehouseCrane.cs's header
    // explicitly left this whole function unported ("needs makeGameplayPlane
    // and a live Mesh/BVH to raycast the skin against"); that need is now
    // narrowed to exactly ONE delegate (`underRay`, the raycast itself — the
    // same seam pattern as WindRider.cs's `SkinAt`), with everything AROUND
    // it ported as pure math, exercised with a synthetic delegate, no
    // loaded plane required.
    //
    // PORTED:
    //   * FindFeet (crane.js:404-421) — the four strop attachment stations
    //     across the span, walking each one back toward the root until the
    //     probe finds structure ("a wing is narrower than the fuselage is
    //     long", crane.js:407-411), with the JS's own hard-coded fallback
    //     point when nothing answers at all (crane.js:417).
    //   * AimRopes (crane.js:442-460, the InstancedMesh's `onBeforeRender`)
    //     — the four strops re-aimed from their own two ends every frame:
    //     a unit cylinder (height 1 along +Y, crane.js:422) stretched and
    //     oriented along the segment, centred at its midpoint.
    //   * MidAirframe (crane.js:467-468) — the middle of the hanging
    //     airframe, which is FanLayout.Compute's `aim` parameter.
    //
    // NOT PORTED: loading/measuring the plane (makeGameplayPlane, own.min/
    // max) and building the swing-pivot Transform hierarchy — City/Flight's
    // PlaneCatalog territory, same boundary WarehouseCrane.cs already draws.
    // A caller supplies the measured WindRider.PlaneBox this file needs
    // (the same box WindRider.cs/WindFlowMesh.cs already take) instead of
    // this file re-deriving it from a GLB.
    //
    // ============================== HANDEDNESS ==============================
    // No cross product anywhere in this file. `Quaternion.FromToRotation`
    // (crane.js:454's `setFromUnitVectors`) is a built-in shortest-arc
    // rotation and, per this port's own rule (FlightModel.cs's header,
    // reused by WindRider.cs's class header for the identical reason), it
    // needs NO operand swap: both engines' native "rotate A onto B"
    // implementations already honour their own handedness internally —
    // there is no exposed cross product here for a caller to get backwards.
    // =========================================================================
    public static class PlaneSling
    {
        /// <summary>crane.js:413 — the stations walked back toward the root
        /// before giving up (1 = the full offset asked for, 0 = the root
        /// itself).</summary>
        public static readonly float[] FootFalloff = { 1f, 0.8f, 0.6f, 0.4f, 0.2f, 0f };

        /// <summary>crane.js:404-421 `for (sx of [-1,1]) for (sz of [-1,1])`.
        /// `span`/`len` are WindRider.PlaneBox.Span/Len; `maxY` is
        /// PlaneBox.MaxY (the fallback's `own.max.y - 0.55`, crane.js:417).
        /// `underRay(x, z)` mirrors the JS's `under(x, sz*len*0.13*f)`
        /// closure: a ray down the aeroplane's own up-axis against its real
        /// skin, in the plane's LOCAL space, returning null where nothing is
        /// hit (crane.js:394-403's own "two of the four stations fell off
        /// the chord" case). Returns exactly 4 points, in strop order
        /// sx=-1,sz=-1 / sx=-1,sz=1 / sx=1,sz=-1 / sx=1,sz=1 (crane.js's own
        /// nested-loop order).</summary>
        public static Vector3[] FindFeet(float span, float len, float maxY, Func<float, float, Vector3?> underRay)
        {
            var feet = new Vector3[4];
            int n = 0;
            foreach (float sx in new float[] { -1f, 1f })
            {
                foreach (float sz in new float[] { -1f, 1f })
                {
                    float x = sx * span * 0.17f;
                    Vector3? foot = null;
                    foreach (float f in FootFalloff)
                    {
                        foot = underRay?.Invoke(x, sz * len * 0.13f * f);
                        if (foot.HasValue) break;
                    }
                    Vector3 p = foot ?? new Vector3(x, maxY - 0.55f, sz * len * 0.13f);
                    p.y -= 0.03f;                // crane.js:418 — a shackle sits IN the skin, not on it
                    feet[n++] = p;
                }
            }
            return feet;
        }

        /// <summary>crane.js:442-460, one frame, minus the Transform-walking
        /// boilerplate (`updateWorldMatrix` calls — a caller's job): given
        /// the hook block and the four feet, ALL ALREADY IN THE SAME LOCAL
        /// SPACE the rope instances themselves live in (a caller converts:
        /// plane-local foot -&gt; world -&gt; ropes'-parent-local, and the
        /// block similarly off the swing pivot), returns one TRS matrix per
        /// rope. Returns Matrix4x4.identity for a degenerate (near-zero-
        /// length) segment, which the JS instead leaves un-set via
        /// `continue` (crane.js:453) — equivalent for a caller that starts
        /// from identity instance matrices, as a fresh InstancedMesh does.</summary>
        public static Matrix4x4[] AimRopes(Vector3 blockLocal, Vector3[] feetLocal, float minLength = 1e-4f)
        {
            var outp = new Matrix4x4[feetLocal.Length];
            for (int i = 0; i < feetLocal.Length; i++)
            {
                Vector3 d = feetLocal[i] - blockLocal;
                float l = d.magnitude;
                if (!(l > minLength)) { outp[i] = Matrix4x4.identity; continue; }
                Vector3 dir = d / l;
                Quaternion q = Quaternion.FromToRotation(Vector3.up, dir); // see class header: no swap needed
                Vector3 p = blockLocal + d * 0.5f;                          // crane.js:455, the cable's middle
                outp[i] = Matrix4x4.TRS(p, q, new Vector3(1f, l, 1f));      // crane.js:456, a unit cylinder stretched to length
            }
            return outp;
        }

        /// <summary>crane.js:467-468 — the middle of the hanging airframe,
        /// in the crane group's own frame (FanLayout.Compute's `aim`).
        /// `hookAt` is the hook position in the crane's frame
        /// (WarehouseCrane's own hookAt, crane.js:240-241); `planeLocalY`
        /// is the plane wrapper's own position.y under the swing pivot
        /// (crane.js:341 `-(SLING + own.max.y)`).</summary>
        public static Vector3 MidAirframe(Vector3 hookAt, float planeLocalY, WindRider.PlaneBox box)
            => new Vector3(hookAt.x, hookAt.y + planeLocalY + (box.MinY + box.MaxY) * 0.5f, hookAt.z);
    }
}
