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
    /// </summary>
    public static class CoverShootPickups
    {
        public static void EnsureSpawned(Core.GameLoop game, CharacterRig rig)
        {
            // NOT a one-shot static bool any more (2026-11-01): the PlayMode
            // test chain runs several test fixtures in the SAME domain
            // without a domain reload between them, and each one reboots
            // its own fresh scene/hero — a static "already spawned" flag
            // stayed true forever after the FIRST fixture's scene (and its
            // "pistol" GameObject) was torn down, so every later fixture's
            // CoverShootHero.Attach() call here silently did nothing
            // (confirmed: Tests/PlayMode/FirstPlayModeRun.cs's and
            // KeysRun.cs's purchased-path key_R/key_Alt_armedToggles checks
            // both read KEY_R_PISTOL_FOUND=false / "ShootBehaviour never
            // armed" despite the FIRST fixture's own pickup spawn having
            // logged ok=true). Check for the real, live GameObject instead.
            if (GameObject.Find("pistol") != null) return;

            Vector3 origin = rig.Root.position;
            Vector3 fwd = rig.Root.forward;
            Vector3 right = rig.Root.right;

            SpawnOne("pistol", origin + fwd * 2.2f, InteractiveWeapon.WeaponType.SHORT, InteractiveWeapon.WeaponMode.SEMI, 0,
                bulletDamage: 15f, recoilAngle: 1.2f, magSize: 12, reserve: 48,
                rightHandPos: new Vector3(0.015f, -0.02f, 0.06f), relRot: new Vector3(0f, 90f, 0f));

            SpawnOne("rifle", origin + fwd * 2.2f + right * 1.6f, InteractiveWeapon.WeaponType.LONG, InteractiveWeapon.WeaponMode.BURST, 3,
                bulletDamage: 25f, recoilAngle: 2.0f, magSize: 20, reserve: 90,
                rightHandPos: new Vector3(0.02f, -0.03f, 0.10f), relRot: new Vector3(0f, 90f, 0f));

            SpawnOne("ak", origin + fwd * 2.2f - right * 1.6f, InteractiveWeapon.WeaponType.LONG, InteractiveWeapon.WeaponMode.AUTO, 0,
                bulletDamage: 30f, recoilAngle: 2.6f, magSize: 30, reserve: 90,
                rightHandPos: new Vector3(0.02f, -0.03f, 0.12f), relRot: new Vector3(0f, 90f, 0f));
        }

        static void SpawnOne(string id, Vector3 pos, InteractiveWeapon.WeaponType type, InteractiveWeapon.WeaponMode mode, int burstSize,
            float bulletDamage, float recoilAngle, int magSize, int reserve, Vector3 rightHandPos, Vector3 relRot)
        {
            // Drop onto real ground (CombatTerrainTag's own real colliders) —
            // the offset points above are a flat in-front-of-spawn guess.
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out var groundHit, 10f))
                pos = groundHit.point;

            var modelPrefab = Resources.Load<GameObject>($"ThirdParty/CoverShoot/Models/Weapons/{id}");
            if (modelPrefab == null)
            {
                Debug.LogWarning($"COVERSHOOT pickup id={id} missing model under Resources/ThirdParty/CoverShoot/Models/Weapons");
                return;
            }

            var root = new GameObject(id);
            root.SetActive(false); // defer InteractiveWeapon.Awake until children + fields exist
            root.transform.position = pos;

            var mesh = Object.Instantiate(modelPrefab, root.transform);
            mesh.transform.localPosition = Vector3.zero;
            mesh.name = id + "_mesh"; // child[0] — InteractiveWeapon.Awake adds the BoxCollider onto this

            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(root.transform, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.06f, type == InteractiveWeapon.WeaponType.LONG ? 0.55f : 0.18f);

            var w = root.AddComponent<InteractiveWeapon>();
            w.label = id;
            w.type = type;
            w.mode = mode;
            w.burstSize = burstSize;
            w.bulletDamage = bulletDamage;
            w.recoilAngle = recoilAngle;
            w.rightHandPosition = rightHandPos;
            w.relativeRotation = relRot;
            w.sprite = Resources.Load<Sprite>($"ThirdParty/CoverShoot/Textures/UI/{id}_HUD");
            w.shotSound = Resources.Load<AudioClip>($"ThirdParty/CoverShoot/Audio/{id}");
            w.pickSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/pickWeapon");
            w.dropSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/dropWeapon");
            w.reloadSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/reloadWeapon");
            w.noBulletSound = Resources.Load<AudioClip>("ThirdParty/CoverShoot/Audio/noBullet");

            root.SetActive(true); // Awake runs now — Player/GameController already exist (CoverShootHero.Setup builds them first)
            w.ConfigureAmmo(magSize, reserve);

            Debug.Log($"COVERSHOOT pickup id={id} pos={pos} ok=true");
        }
    }
}
