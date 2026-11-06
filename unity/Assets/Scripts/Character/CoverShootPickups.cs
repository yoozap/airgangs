using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Replaces our own ground loot (Combat/Pickups.cs — unwired in this
    /// project, see that file's header: "No implementation ships with this
    /// port") with the purchased pack's own `InteractiveWeapon` pickups, per
    /// the user's decision: "i dont need old gun reload shooting from js".
    /// Spawns a pistol, a rifle and an AK near wherever the hero spawned, each
    /// built from the pack's own FBX (Models/Weapons/*.fbx, Resources-loaded)
    /// wrapped in a fresh `InteractiveWeapon`, since this project ships no
    /// hand-authored prefab for them (there is no Editor session to build one
    /// in) — the weapon/mag-position/muzzle offsets below are a reasonable
    /// first guess, not a measured fit; a visual pass can tighten them later
    /// exactly the way the pack's own README describes doing for a custom
    /// avatar's `rightHandPosition`/`relativeRotation`.
    ///
    /// WIDENED 2026-11-02 (user, playing the build: "i hope takin guns with
    /// F button will works too, coz last time it not worked" — CoverShootRun's
    /// own pickup_armed check already proved the F-key MECHANISM works; what
    /// was missing is (a) nothing ever told the player a gun was there —
    /// UI/Hud/CoverShootPromptView.cs now does — and (b) a player who walked
    /// away from the tight 1.6-2.2 m cluster right at spawn had nothing left
    /// to find). Each weapon id now has a small array of candidate spots —
    /// the original close-in three PLUS a few scattered further out — and
    /// every one of them is independently re-placed if the hero respawns far
    /// away, not just spawned once total.
    /// </summary>
    public static class CoverShootPickups
    {
        struct Spec
        {
            public string Id;
            public InteractiveWeapon.WeaponType Type;
            public InteractiveWeapon.WeaponMode Mode;
            public int BurstSize;
            public float BulletDamage, RecoilAngle;
            public int MagSize, Reserve;
            public Vector3 RightHandPos, RelRot;
        }

        static readonly Spec Pistol = new Spec
        {
            Id = "pistol", Type = InteractiveWeapon.WeaponType.SHORT, Mode = InteractiveWeapon.WeaponMode.SEMI, BurstSize = 0,
            BulletDamage = 15f, RecoilAngle = 1.2f, MagSize = 12, Reserve = 48,
            RightHandPos = new Vector3(0.015f, -0.02f, 0.06f), RelRot = new Vector3(0f, 90f, 0f),
        };
        static readonly Spec Rifle = new Spec
        {
            Id = "rifle", Type = InteractiveWeapon.WeaponType.LONG, Mode = InteractiveWeapon.WeaponMode.BURST, BurstSize = 3,
            BulletDamage = 25f, RecoilAngle = 2.0f, MagSize = 20, Reserve = 90,
            RightHandPos = new Vector3(0.02f, -0.03f, 0.10f), RelRot = new Vector3(0f, 90f, 0f),
        };
        static readonly Spec Ak = new Spec
        {
            Id = "ak", Type = InteractiveWeapon.WeaponType.LONG, Mode = InteractiveWeapon.WeaponMode.AUTO, BurstSize = 0,
            BulletDamage = 30f, RecoilAngle = 2.6f, MagSize = 30, Reserve = 90,
            RightHandPos = new Vector3(0.02f, -0.03f, 0.12f), RelRot = new Vector3(0f, 90f, 0f),
        };

        // Re-placed, not just skipped, if the hero's current spawn is this
        // far from a pickup's last placement — far enough that a normal
        // walk-around would never reach it, close enough that a respawn a
        // few metres away (same warehouse, slightly different spot) does
        // not pointlessly relocate a weapon the player may already be
        // walking towards.
        const float RelocateDist = 20f;

        public static void EnsureSpawned(Core.GameLoop game, CharacterRig rig)
        {
            Vector3 origin = rig.Root.position;
            Vector3 fwd = rig.Root.forward;
            Vector3 right = rig.Root.right;

            // The original close-in three — always kept, so a player who
            // never moves still has an immediate, guaranteed pickup.
            PlaceOrMove(Pistol, "pistol", origin + fwd * 2.2f);
            PlaceOrMove(Rifle, "rifle", origin + fwd * 2.2f + right * 1.6f);
            PlaceOrMove(Ak, "ak", origin + fwd * 2.2f - right * 1.6f);

            // Scattered further out — "around the warehouse/range area the
            // player actually visits", not just the immediate spawn tile.
            // Distances/directions are deliberately modest (10-16 m) rather
            // than hand-converted warehouse-shed-local coordinates: SpawnOne
            // below already raycasts each one onto real ground, so a
            // distance-and-direction offset from wherever the hero ACTUALLY
            // spawned is self-correcting across different dev spots/city
            // positions, where a hand-placed absolute world position would
            // not be.
            PlaceOrMove(Pistol, "pistol_2", origin + fwd * 10f + right * 4f);
            PlaceOrMove(Rifle, "rifle_2", origin + fwd * 14f - right * 3f);
            PlaceOrMove(Ak, "ak_2", origin - fwd * 6f + right * 9f);
            PlaceOrMove(Pistol, "pistol_3", origin - fwd * 10f - right * 6f);
        }

        /// <summary>Spawn fresh if this id has never existed; move it onto
        /// real ground near `desiredPos` if it exists, is still a free
        /// ground pickup (unparented — nothing has reparented it onto the
        /// hero's hand bone) and is far from where it already sits; leave
        /// it alone otherwise (close enough already, or currently held —
        /// never yank an equipped weapon).</summary>
        static void PlaceOrMove(Spec spec, string id, Vector3 desiredPos)
        {
            var existingGo = GameObject.Find(id);
            if (existingGo == null)
            {
                SpawnOne(spec, id, desiredPos);
                return;
            }
            if (existingGo.transform.parent != null) return; // held — leave it exactly where it is
            if (Vector3.Distance(existingGo.transform.position, desiredPos) <= RelocateDist) return; // close enough
            if (Physics.Raycast(desiredPos + Vector3.up * 3f, Vector3.down, out var groundHit, 10f))
                desiredPos = groundHit.point;
            existingGo.transform.position = desiredPos;
            Debug.Log($"COVERSHOOT pickup id={id} pos={desiredPos} ok=true relocated=true");
        }

        /// <summary>A pack weapon for one of the project's OWN guns (the
        /// warehouse akm/beretta/makarov carry Combat/Pickups' PickupTag, not
        /// InteractiveWeapon). `specId` is "pistol" / "rifle" / "ak". The
        /// caller parks it in the inventory right away (InteractiveWeapon.
        /// PrepareForInventory + ShootBehaviour.AddWeapon).</summary>
        public static InteractiveWeapon SpawnBridge(string specId, string goName, Vector3 pos)
        {
            Spec spec = specId == "ak" ? Ak : specId == "rifle" ? Rifle : Pistol;
            return SpawnOne(spec, goName, pos);
        }

        static Material _gunmetal;
        /// <summary>A dark, slightly glossy URP Lit for the pack's untextured weapon
        /// meshes. Keeps an existing material's main texture if it has one (none do
        /// today) — only the colour is forced dark.</summary>
        static Material GunmetalFor(Material src)
        {
            if (_gunmetal == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                _gunmetal = new Material(sh) { name = "CoverShoot_Gunmetal" };
                if (_gunmetal.HasProperty("_BaseColor")) _gunmetal.SetColor("_BaseColor", new Color(0.11f, 0.115f, 0.125f, 1f));
                if (_gunmetal.HasProperty("_Color")) _gunmetal.SetColor("_Color", new Color(0.11f, 0.115f, 0.125f, 1f));
                if (_gunmetal.HasProperty("_Smoothness")) _gunmetal.SetFloat("_Smoothness", 0.55f);
                if (_gunmetal.HasProperty("_Metallic")) _gunmetal.SetFloat("_Metallic", 0.75f);
            }
            if (src != null && src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null) return src; // a textured one stays
            return _gunmetal;
        }

        static InteractiveWeapon SpawnOne(Spec spec, string id, Vector3 pos)
        {
            // Drop onto real ground (CombatTerrainTag's own real colliders) —
            // the offset points above are a flat in-front-of-spawn guess.
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out var groundHit, 10f))
                pos = groundHit.point;

            // All scattered ids share one weapon model/icon/audio set per
            // base type ("pistol_2"/"pistol_3" both load "pistol" assets) —
            // only the GameObject's own name (what CoverShootRun.cs and
            // UI/Hud/CoverShootPromptView.cs key off for in-range/F-press)
            // needs to be unique.
            var modelPrefab = Resources.Load<GameObject>($"ThirdParty/CoverShoot/Models/Weapons/{spec.Id}");
            if (modelPrefab == null)
            {
                Debug.LogWarning($"COVERSHOOT pickup id={id} missing model under Resources/ThirdParty/CoverShoot/Models/Weapons");
                return null;
            }

            var root = new GameObject(id);
            root.SetActive(false); // defer InteractiveWeapon.Awake until children + fields exist
            root.transform.position = pos;

            var mesh = Object.Instantiate(modelPrefab, root.transform);
            mesh.transform.localPosition = Vector3.zero;
            mesh.name = id + "_mesh"; // child[0] — InteractiveWeapon.Awake adds the BoxCollider onto this
            // THE PACK SHIPS NO WEAPON MATERIALS (2026-11-02): ak/pistol/rifle.fbx import
            // with materialImportMode=0 and there is no .mat for them anywhere in the
            // pack (only flash/smoke/spark/bullet_hole), so every gun rendered in
            // Unity's default light grey — invisible against the white hangar floor
            // even when GunHandRun put it 1.7 m from the hero, in frustum, renderers
            // on. Give them the gunmetal the demo's guns read as.
            foreach (var r in mesh.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = GunmetalFor(mats[i]);
                r.sharedMaterials = mats;
            }

            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(root.transform, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.06f, spec.Type == InteractiveWeapon.WeaponType.LONG ? 0.55f : 0.18f);

            var w = root.AddComponent<InteractiveWeapon>();
            w.label = spec.Id; // base type, so Georgian pickup-prompt text stays correct for pistol_2/pistol_3 etc.
            w.type = spec.Type;
            w.mode = spec.Mode;
            w.burstSize = spec.BurstSize;
            w.bulletDamage = spec.BulletDamage;
            w.recoilAngle = spec.RecoilAngle;
            w.rightHandPosition = spec.RightHandPos;
            w.relativeRotation = spec.RelRot;
            w.sprite = Resources.Load<Sprite>($"ThirdParty/CoverShoot/Textures/UI/{spec.Id}_HUD");
            w.shotSound = Resources.Load<AudioClip>($"ThirdParty/CoverShoot/Audio/{spec.Id}");
            w.pickSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/pickWeapon");
            w.dropSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/dropWeapon");
            w.reloadSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/reloadWeapon");
            w.noBulletSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/noBullet");

            root.SetActive(true); // Awake runs now — Player/GameController already exist (CoverShootHero.Setup builds them first)
            w.ConfigureAmmo(spec.MagSize, spec.Reserve);

            Debug.Log($"COVERSHOOT pickup id={id} pos={pos} ok=true");
            return w;
        }
    }
}
