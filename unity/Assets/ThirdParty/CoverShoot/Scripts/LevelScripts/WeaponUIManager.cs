using System;
using UnityEngine;

// This class corresponds to the weapon screen HUD features.
//
// TbilisiFly glue (2026-11-01 compile-fix pass): this project has NO uGUI
// package installed (UI Toolkit only — CS0234 "'UI' does not exist in
// namespace 'UnityEngine'" the moment this file referenced Image/Text), and
// never shows this pack's own English bullet-strip panel anyway (Georgian-
// UI-only rule, enforced by disabling its Canvas at spawn — Character/
// CoverShootHero.cs's EnsureGameController). So this component no longer
// touches Image/Text/Canvas at all: it is now a pure event bridge that keeps
// InteractiveWeapon.cs's calling API (Toggle/UpdateWeaponHUD) compiling and
// working. UI/Hud/CoverShootAmmoView.cs (UI Toolkit, real, visible,
// Georgian-labelled) is what actually paints weapon/ammo state, fed by the
// two static events below — unchanged from before this pass, just no
// longer ALSO driving a a parallel uGUI panel that could never exist here.
public class WeaponUIManager : MonoBehaviour
{
	public static event Action<Sprite, int, int, int> OnAmmoChanged;
	public static event Action<bool> OnArmedToggled;

	// Manage on-screen HUD visibility (InteractiveWeapon.Toggle/Drop call this).
	public void Toggle(bool active)
	{
		OnArmedToggled?.Invoke(active);
	}

	// Update the weapon HUD features (InteractiveWeapon.UpdateHud calls this).
	public void UpdateWeaponHUD(Sprite weaponSprite, int bulletsLeft, int fullMag, int extraBullets)
	{
		OnAmmoChanged?.Invoke(weaponSprite, bulletsLeft, fullMag, extraBullets);
	}
}
