using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders src/shooting.js's on-foot pistol/rifle muzzle flash — the one
    /// Combat/Shooting.cs's Pistol computes (FlashVisible/FlashScale/
    /// FlashRotation, src/shooting.js:366-371,474-476) but that, until this
    /// class, had zero consumers (grepped project-wide before writing this: no
    /// "new OnFootMuzzleFlashFx" anywhere, no other reader of those three
    /// fields). Reuses the belly gun's own Shaders/MuzzleFlash.shader default
    /// pass (Fx/MuzzleFlashFx.cs's technique) rather than a new shader — a
    /// camera-facing billboard quad, ONE Material for this object's whole
    /// lifetime (this project's "never a Material per shot" house rule).
    ///
    /// THE HELD-WEAPON TRANSFORM, without a new Character/Core edit: the brief
    /// for this class offered two options — a `Func&lt;Transform&gt;` provider
    /// Character/Core assigns later (inert until someone remembers to wire it;
    /// this project already has eight examples of exactly that trap), or a
    /// hand-off asking for an exact Character/Core edit. Neither was needed.
    /// Combat/Shooting.cs's Pistol.Fire(FireRequest req, ...) is ALREADY handed
    /// `req.HeldWeapon`/`req.HeldWeaponDef` by its EXISTING caller (whatever
    /// Character/Core code fires the gun) on every shot — it previously just
    /// discarded that reference once EjectCase() returned. Pistol now keeps it
    /// (FlashHeldWeapon/FlashHeldWeaponDef, set in Fire() alongside
    /// FlashScale/FlashRotation), so this class reads a live, already-correct,
    /// already-moving weapon Transform every frame with NO new cross-boundary
    /// wiring at all — it renders a real flash today, not once someone else
    /// assigns a hook.
    ///
    /// Ticked from Combat/Shooting.cs's own Pistol.Update(dt, ...) — which Core
    /// already calls every frame — exactly the "owned by the class that already
    /// ticks every frame" pattern Combat/Rockets.cs/Guns.cs/Blast.cs/
    /// Combat/ImpactFx.cs/PlaneAirframeHost.cs all use, so no Core/* edit is
    /// needed here either.
    /// </summary>
    public sealed class OnFootMuzzleFlashFx
    {
        public Material FlashMat;

        public OnFootMuzzleFlashFx()
        {
            var sh = Shader.Find("TbilisiFly/MuzzleFlash");
            if (sh != null) FlashMat = new Material(sh);
        }

        /// <summary>Call once per frame (Combat/Shooting.cs's Pistol.Update already
        /// does). Draws nothing while FlashVisible is false, or before the first
        /// shot (FlashHeldWeapon still null).</summary>
        public void Render(Pistol p)
        {
            if (FlashMat == null || p == null || !p.FlashVisible) return;
            Transform held = p.FlashHeldWeapon;
            if (held == null) return;
            Vector3 muzzle = Pistol.Muzzle(held, p.FlashHeldWeaponDef, null);
            // src/shooting.js:369 `material.rotation = random()*2π` — the one-shot
            // roll. Shaders/MuzzleFlash.shader's default Vert pass billboards in
            // VIEW space (right/up off the view matrix) and reads only the
            // instance's own SCALE MAGNITUDE, never its rotation component — same
            // as Fx/MuzzleFlashFx.cs's belly-gun flash already does — so this roll
            // is carried through for parity with that house pattern (and is
            // forward-compatible with that shader later gaining a roll term) even
            // though it is not visually distinguishable through TODAY's Vert.
            var rot = Quaternion.Euler(0f, 0f, p.FlashRotation * Mathf.Rad2Deg);
            var m = Matrix4x4.TRS(muzzle, rot, Vector3.one * p.FlashScale);
            Graphics.DrawMesh(InstancedSpriteBatch.UnitQuad, m, FlashMat, 0, null, 0, null, false, false);
        }

        public void Dispose()
        {
            if (FlashMat != null) UnityEngine.Object.Destroy(FlashMat);
        }
    }
}
