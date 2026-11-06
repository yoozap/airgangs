using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Data;

namespace TbilisiFly.Combat
{

    /// <summary>
    /// Port of src/pickups.js — PUBG-style ground loot: walk within range and a
    /// card would show (UI, out of scope — see VisibleCards below), F takes it
    /// into its weapon slot, taking a second weapon for an occupied slot throws
    /// the old one down at your feet as loot again. Cited
    /// `// src/pickups.js:NNN` throughout.
    ///
    /// ================================ NOT PORTED ================================
    /// The `#pickup-cards` DOM stack (pickups.js:29-100) is /UI/'s territory
    /// (hard rule) — `VisibleCards` below is the same kind of snapshot
    /// PaperTargets.cs's `ScoreSnapshot` and RangeLane.cs's `LaneHud` are, for a
    /// UI script to render however it likes.
    ///
    /// ================================ HOOKS NEEDED, NOT YET OWNED ================
    /// Exactly like RifleHolds.cs's own header: nothing in this port yet owns a
    /// "what weapon is in which slot" inventory (Guns.cs today is only the
    /// plane belly-gun) or a hero-save/ground-probe system. `IHost` below is
    /// this file's version of CombatWorldHooks.cs's `IDamageWorld` pattern — a
    /// narrow interface naming exactly the calls pickups.js makes on
    /// `game.weapons`/`game.gear`/`game.foot`/`game._groundAt`/`game.saveState`,
    /// so this file compiles and is usable today, and whichever system ends up
    /// owning the hero's loadout implements it without reaching into this file.
    /// The recorded project gotcha this file's `take()`/`Restore()` split is
    /// built around: the owned kit is saved SEPARATELY from the hero's spawn
    /// spot, so it survives death (see Restore()'s own note).
    ///
    /// ================================ HANDEDNESS =============================
    /// One direction formula, `Drop()`'s `fwd = (-sin(yaw), 0, -cos(yaw))`
    /// (pickups.js:132) — carried over UNCHANGED, the same established
    /// yaw-to-forward convention this project already uses verbatim in
    /// FootController.cs (its own header says so explicitly). No cross
    /// products anywhere in this file.
    /// </summary>
    public sealed class Pickups
    {
        const float Range = 2.6f;        // pickups.js:14 m
        const float DropForward = 0.8f;  // pickups.js:15 m
        const float Spin = 0.9f;         // pickups.js:16 rad/s
        const float Bob = 0.035f;        // pickups.js:17 m
        const int MaxCards = 4;          // pickups.js:18
        const float FocusStick = 0.15f;  // pickups.js:19 m

        public sealed class Item
        {
            public Transform Obj;
            public WeaponDef Weapon;   // exactly one of Weapon/Gear is set
            public GearDef Gear;
            public float HomeY;
            public float Phase;
            public bool IsGear => Gear != null;
            public string Name => IsGear ? Gear.Name : Weapon?.Name;

            /// <summary>src/pickups.js:77-80 `_card()`'s caliber line — the one
            /// piece of real formatting logic inside the DOM-building `_card()`
            /// (pickups.js:29-100, otherwise /UI/'s territory in full). Georgian
            /// text kept verbatim; `Math.round` -&gt; JsMath.Round per project rule.</summary>
            public string Caliber()
            {
                string cartId = IsGear ? null : Weapon?.CartridgeId;
                Cartridge cart = cartId != null && Cartridges.ById.TryGetValue(cartId, out var c) ? c : null;
                return cart != null
                    ? $"{cart.Label} · {cart.MassG} გ · {cart.V0} მ/წმ · {JsMath.Round(cart.Energy)} ჯ"
                    : IsGear ? "აღჭურვილობა — ჩაიცვამს" : "";
            }
        }

        /// <summary>src/pickups.js's calls on `game.weapons`/`game.gear`/
        /// `game.foot`/`game._groundAt`/`game.saveState`/`game.scene` — see
        /// this file's header. No implementation ships with this port.</summary>
        public interface IHost
        {
            /// <summary>src/pickups.js:193 `w?.carried?.[slot]` — whatever is
            /// currently in that slot, or null.</summary>
            WeaponDef CarriedInSlot(int slot);
            Transform CarriedObjInSlot(int slot);
            /// <summary>pickups.js:200 `w?.setCarried(slot, held)`.</summary>
            void SetCarried(int slot, WeaponDef def, Transform obj);
            /// <summary>pickups.js:201 `w?.select(slot)`.</summary>
            void Select(int slot);
            /// <summary>pickups.js:184 `game.gear.set(worn.id, worn)`.</summary>
            void SetWornGear(GearDef def, Transform obj);
            /// <summary>pickups.js:185 `game.foot?.wearGear?.(worn)` — returns
            /// whether it actually fitted (used by Restore() below).</summary>
            bool WearGear(GearDef def, Transform obj);
            void SetHint(string text);
            void SaveState(bool immediate, string reason);
            /// <summary>pickups.js:134 `game._groundAt(x,z,fromY,probe)`.</summary>
            float? GroundAt(float x, float z, float fromY, float probe);
            Transform SceneRoot { get; }
            /// <summary>pickups.js:204 `game.foot?.pos`/`.yaw` (or the plane
            /// position fallback, out of this file's scope to choose between —
            /// the host decides which it is).</summary>
            Vector3 ActorPos { get; }
            float ActorYaw { get; }
        }

        readonly List<Item> _items = new List<Item>();
        readonly IHost _host;
        public Item Near { get; private set; }
        bool _scanned;
        float _scanCd;                   // retry timer while nothing has been found yet
        const float ScanRetry = 0.5f;    // s between scene-wide sweeps while empty
        float _t;

        public Pickups(IHost host) { _host = host; }

        // =====================================================================
        // src/pickups.js:104-125 scan/add — the scene is swept once on the
        // first update that finds loot (scenery builders finish after the
        // world is playable); `Add` is for loot that arrives later (a big
        // download landing on a bench already swept).
        // =====================================================================
        public int Scan(Transform root)
        {
            _items.Clear();
            foreach (var tag in root.GetComponentsInChildren<PickupTag>(true))
            {
                if (tag.transform.parent == null) continue;
                _items.Add(new Item
                {
                    Obj = tag.transform, Weapon = tag.Weapon, Gear = tag.Gear,
                    HomeY = tag.transform.position.y, Phase = Random.value * 6.28f,
                });
            }
            _scanned = _items.Count > 0;
            return _items.Count;
        }

        /// <summary>Scene-wide form of <see cref="Scan"/> — the faithful port of
        /// src/pickups.js scanning the scene graph ROOT. Used by Update()
        /// because the subtree form could only ever see loot parented under
        /// whatever Transform HeldWeapon happened to be constructed with (see
        /// Update's own note). Includes inactive objects for the same reason
        /// Scan(root) passes `true`: loot can be spawned disabled.</summary>
        public int ScanScene()
        {
            _items.Clear();
            var tags = UnityEngine.Object.FindObjectsByType<PickupTag>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var tag in tags)
            {
                if (tag == null || tag.transform.parent == null) continue;
                _items.Add(new Item
                {
                    Obj = tag.transform, Weapon = tag.Weapon, Gear = tag.Gear,
                    HomeY = tag.transform.position.y, Phase = Random.value * 6.28f,
                });
            }
            _scanned = _items.Count > 0;
            if (_scanned) Debug.Log($"PICKUPSCAN found={_items.Count} (scene-wide)");
            return _items.Count;
        }

        public bool Add(PickupTag tag)
        {
            if (tag == null || tag.transform.parent == null) return false;
            foreach (var it in _items) if (it.Obj == tag.transform) return true;
            _items.Add(new Item
            {
                Obj = tag.transform, Weapon = tag.Weapon, Gear = tag.Gear,
                HomeY = tag.transform.position.y, Phase = Random.value * 6.28f,
            });
            _scanned = true;
            return true;
        }

        // src/pickups.js:129-141 _drop — a weapon already in hand, thrown back
        // down at the hero's feet (it keeps its own GameObject, nothing reloads).
        void Drop(WeaponDef def, Transform obj, Vector3 at, float yaw)
        {
            if (obj == null) return;
            Vector3 fwd = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw)); // HANDEDNESS: unchanged (file header)
            Vector3 pos = at + fwd * DropForward;
            float? gy = _host?.GroundAt(pos.x, pos.z, pos.y + 1.5f, 1.5f);
            pos.y = (gy ?? pos.y) + 0.02f;
            obj.position = pos;
            obj.rotation = Quaternion.Euler(0f, (yaw + Mathf.PI / 2f) * Mathf.Rad2Deg, 0f);
            var tag = obj.GetComponent<PickupTag>() ?? obj.gameObject.AddComponent<PickupTag>();
            tag.Weapon = def; tag.Gear = null;
            if (_host != null && obj.parent != _host.SceneRoot) obj.SetParent(_host.SceneRoot, true);
            _items.Add(new Item { Obj = obj, Weapon = def, HomeY = pos.y, Phase = 0f });
        }

        // =====================================================================
        // src/pickups.js:143-169 update — bob/spin every item, find what is in
        // RANGE, light the nearest with a little stickiness (FOCUS_STICK) so
        // two guns at the same distance do not flicker the light between them.
        // =====================================================================
        public IReadOnlyList<Item> VisibleCards { get; } = new List<Item>();

        public void Update(float dt, Transform sceneRoot, Vector3 heroPos)
        {
            // SCAN THE WHOLE SCENE, NOT ONE SUBTREE (2026-11-02). This used to
            // be `Scan(sceneRoot)`, and HeldWeapon passes the Transform it was
            // built with — CombatWorldAdapter.cs:514's `new
            // GameObject("CombatRuntime")`, a fresh EMPTY container for the
            // combat runtime systems. Scan() sweeps
            // root.GetComponentsInChildren<PickupTag>, so it only ever looked
            // inside CombatRuntime, while every real piece of ground loot
            // (the warehouse akm/beretta/makarov from
            // Props/WarehouseInteriorFill.cs:407) lives under the WORLD
            // hierarchy. It therefore found 0 items on every frame forever:
            // Near stayed null, no "F" card was ever drawn, and TakeNearest()
            // had nothing to take. The user hit it as "when i am goin to gun i
            // dont see press F key ... mby its problem with warehouse" — which
            // was the right instinct.
            // src/pickups.js scans the THREE scene graph root, so the faithful
            // port is scene-wide, not a subtree. Throttled while empty because
            // Scan() leaves _scanned false until it finds something, and a
            // scene-wide sweep every frame is not free.
            if (!_scanned)
            {
                _scanCd -= dt;
                if (_scanCd <= 0f) { _scanCd = ScanRetry; ScanScene(); }
            }
            _t += dt;

            var near = new List<(float d, Item it)>();
            foreach (var it in _items)
            {
                if (it.Obj == null) continue;
                it.Obj.Rotate(Vector3.up, Spin * dt * Mathf.Rad2Deg, Space.World);
                var p = it.Obj.position;
                p.y = it.HomeY + Mathf.Sin(_t * 2f + it.Phase) * Bob;
                it.Obj.position = p;
                float d = Vector3.Distance(it.Obj.position, heroPos);
                if (d < Range) near.Add((d, it));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));

            Item focus = near.Count > 0 ? near[0].it : null;
            (float d, Item it)? cur = null;
            foreach (var n in near) if (n.it == Near) { cur = n; break; }
            if (cur.HasValue && focus != Near && cur.Value.d < (near.Count > 0 ? near[0].d : 0f) + FocusStick)
                focus = Near;
            Near = focus;

            var list = (List<Item>)VisibleCards;
            list.Clear();
            int take = Mathf.Min(MaxCards, near.Count);
            for (int i = 0; i < take; i++) list.Add(near[i].it);
            // pickups.js:162-167 — move the sticky focus to the front, whether
            // or not it was even in the MAX_CARDS slice (it may rank beyond it
            // and only be shown because of stickiness); then truncate from the
            // END (JS's `list.length = MAX_CARDS`), not by removing the nearest.
            if (focus != null && (list.Count == 0 || list[0] != focus))
            {
                int idx = list.IndexOf(focus);
                if (idx > 0) list.RemoveAt(idx);
                list.Insert(0, focus);
                if (list.Count > MaxCards) list.RemoveRange(MaxCards, list.Count - MaxCards);
            }
        }

        // =====================================================================
        // src/pickups.js:173-215 take — F / a card click: into the slot. A
        // slot that already holds something throws that weapon down first.
        // =====================================================================
        public bool Take(Item which = null)
        {
            var it = which ?? Near;
            if (it == null || !_items.Contains(it)) return false;

            if (it.IsGear)
            {
                _items.Remove(it);
                it.Obj.SetParent(null, true);
                Forget(it);
                _host?.SetWornGear(it.Gear, it.Obj);
                _host?.WearGear(it.Gear, it.Obj);
                _host?.SetHint($"🩳 {it.Gear.Name} — ჩაცმულია");
                _host?.SaveState(true, "put gear on");
                return true;
            }

            int slot = it.Weapon?.Slot ?? 1;
            var oldDef = _host != null ? _host.CarriedInSlot(slot) : null;
            var oldObj = _host != null ? _host.CarriedObjInSlot(slot) : null;

            _items.Remove(it);
            it.Obj.SetParent(null, true);
            Forget(it);

            _host?.SetCarried(slot, it.Weapon, it.Obj);
            _host?.Select(slot);

            if (oldDef != null)
            {
                Drop(oldDef, oldObj, _host.ActorPos, _host.ActorYaw);
                _host?.SetHint($"🔫 {it.Weapon?.Name} — აღებულია · {oldDef.Name} დაგდებულია");
            }
            else
            {
                _host?.SetHint($"🔫 {it.Weapon?.Name} — აღებულია (სლოტი {slot + 1})");
            }
            _host?.SaveState(true, "took a weapon");
            return true;
        }

        // src/pickups.js:259-267 _forget — an item that is no longer loot: its
        // card goes, and the stack is rebuilt on the next update from whatever
        // is still in reach. (The DOM card-removal half, pickups.js:262-263,
        // has no analogue here — VisibleCards IS the already-built list.)
        void Forget(Item it)
        {
            if (Near == it) Near = null;
            var list = (List<Item>)VisibleCards;
            list.Remove(it);
        }

        /// <summary>src/pickups.js:224-257 restore — give a personage back the
        /// weapons/gear its saved state says it owns. THE KIT IS SAVED APART
        /// FROM THE HERO'S SPAWN SPOT (the project's own recorded design:
        /// "Hero spawn socket 2026-10" / "Pickups & weapons 2026-10"), so a
        /// caller runs this independently of wherever the hero's position was
        /// restored to — it survives death. Returns what did NOT load/fit, so
        /// the caller can keep retrying later rather than losing ownership of
        /// a weapon that merely failed one download.</summary>
        public interface ILoader
        {
            /// <summary>Loads and attaches `def`'s model, returning its root
            /// Transform, or null on failure (download/parse error).</summary>
            Transform LoadWeapon(WeaponDef def);
        }

        public (List<(string id, int slot)> LostGuns, List<string> LostGear) Restore(
            IEnumerable<(string id, int slot)> guns, IEnumerable<string> gear, ILoader loader)
        {
            var lostGuns = new List<(string, int)>();
            int n = 0;
            if (guns != null)
            {
                foreach (var g in guns)
                {
                    var def = WeaponDefs.ById_(g.id);
                    if (def == null) continue;
                    Transform obj = null;
                    try { obj = loader?.LoadWeapon(def); } catch { obj = null; }
                    if (obj == null) { lostGuns.Add(g); continue; }
                    _host?.SetCarried(g.slot != 0 ? g.slot : def.Slot, def, obj);
                    n++;
                }
            }
            var lostGear = new List<string>();
            int m = 0;
            if (gear != null)
            {
                foreach (var id in gear)
                {
                    var def = GearDefs.GearById(id);
                    if (def == null) continue; // an id this build no longer has: genuinely gone
                    _host?.SetWornGear(def, null);
                    if (_host != null && _host.WearGear(def, null)) m++;
                    else lostGear.Add(id);
                }
            }
            Debug.Log($"hero loadout: {n} owned weapon(s), {m} worn" +
                (lostGuns.Count > 0 ? $" — {string.Join(", ", lostGuns.ConvertAll(g => g.Item1))} did not load (still owned)" : ""));
            return (lostGuns, lostGear);
        }

        // src/pickups.js:269-271 setVisible — hides every card and drops the
        // sticky focus; the DOM hide/empty (pickups.js:270) is VisibleCards.Clear().
        public void SetVisible(bool on)
        {
            if (!on) { ((List<Item>)VisibleCards).Clear(); Near = null; }
        }

        // src/pickups.js:273-276 dispose.
        public void Dispose() { _items.Clear(); ((List<Item>)VisibleCards).Clear(); }
    }
}
