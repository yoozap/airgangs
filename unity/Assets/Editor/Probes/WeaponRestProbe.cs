using TbilisiFly.Combat;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the small gaps closed on top of the already-shipped Range/Guns
    /// port units: Combat/Shooting.cs's new FlashVisible/FlashScale/FlashRotation
    /// (src/shooting.js:366-371, FLASH_T), Combat/Guns.cs's now-wired
    /// MachineGun.OnGlassBurst (src/guns.js:600), Combat/Pickups.cs's new
    /// Item.Caliber() (src/pickups.js:77-80), Combat/RangeLane.cs's new
    /// RangeLanes.HintText (src/rangelane.js:407-408), and Combat/Weapons.cs's
    /// new AimAcross (src/weapons.js:491).
    ///
    /// tools/_probe_weaponrest.mjs prints the matching JS-side facts — every
    /// line here pairs with one there (same KEY, same field order, same
    /// Georgian text byte-for-byte) for tools/unity_port_check.py to diff.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.WeaponRestProbe.Run
    /// </summary>
    public static class WeaponRestProbe
    {
        public static void Run()
        {
            // ---- src/shooting.js:31 FLASH_T + :366-371's scale formula --------
            // Pistol lives at namespace level (the FILE is Shooting.cs, the TYPE
            // is Pistol — not a nested Shooting.Pistol; project gotcha #6).
            Debug.Log($"FLASHDUR seconds={Pistol.FlashDuration:F4}");
            foreach (var id in new[] { "9x18", "9x19" })
            {
                var cart = Cartridges.ById[id];
                float Scale(float rng) => Mathf.Min(0.8f, 0.26f + 0.2f * (cart.Energy / 580f)) * (0.8f + rng * 0.5f);
                Debug.Log($"FLASHSCALE cart={id} rngMin={Scale(0f):F6} rngMax={Scale(1f):F6} energyJ={cart.Energy:F3}");
            }

            // ---- src/guns.js:600 g.glass.burstList(blown, 0.8, 3) --------------
            Debug.Log($"GLASSBURST power={MachineGun.GlassBurstPower:F3} max={MachineGun.GlassBurstMax:F3}");

            // ---- src/pickups.js:77-80 _card()'s caliber line -------------------
            var makarov = new Pickups.Item { Weapon = WeaponDefs.ById["makarov"] };
            var beretta = new Pickups.Item { Weapon = WeaponDefs.ById["beretta"] };
            var gear = new Pickups.Item { Gear = GearDefs.ById["belt"] };
            Debug.Log($"PICKCAL cart=9x18 text={makarov.Caliber()}");
            Debug.Log($"PICKCAL cart=9x19 text={beretta.Caliber()}");
            Debug.Log($"PICKCAL cart=gear text={gear.Caliber()}");

            // ---- src/rangelane.js:407-408 — the control card's hint line ------
            Debug.Log($"HINT dir=1 text={RangeLanes.HintText(1)}");
            Debug.Log($"HINT dir=-1 text={RangeLanes.HintText(-1)}");
            Debug.Log($"HINT dir=0 text={RangeLanes.HintText(0)}");

            // ---- src/weapons.js:491 aimAcross — synthetic two-armed rig, same
            // shoulder geometry as RangeProbe.cs's own RIFLEHOLD synthetic rig's
            // JS-side counterpart (tools/_probe_range.mjs's makeArm). -----------
            var root = new GameObject("Root").transform;
            Transform MakeArm(string side, float s)
            {
                var upper = new GameObject($"upperarm_{side}").transform;
                upper.SetParent(root, false);
                upper.localPosition = new Vector3(0.20f * s, 1.40f, 0f);
                var fore = new GameObject($"lowerarm_{side}").transform;
                fore.SetParent(upper, false);
                fore.localPosition = new Vector3(0.02f * s, -0.27f, 0.05f);
                return upper;
            }
            MakeArm("r", 1f);
            MakeArm("l", -1f);

            var chain = Weapons.FindArmChain(root);
            var chainL = Weapons.FindArmChainL(root);
            void PrintAimAcross(string label, Vector3 aim)
            {
                float v = Weapons.AimAcross(root, root, chain, chainL, aim);
                Debug.Log($"AIMACROSS dir={label} value={v:F6}");
            }
            PrintAimAcross("ahead", new Vector3(0, 0, -1));
            PrintAimAcross("right", new Vector3(1, 0, 0));
            PrintAimAcross("left", new Vector3(-1, 0, 0));

            Object.DestroyImmediate(root.gameObject);
        }
    }
}
