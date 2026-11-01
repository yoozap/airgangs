using UnityEngine;

namespace TbilisiFly.UI.Target
{
    /// <summary>
    /// What TargetCardView needs to paint one frame of the WoW-style target
    /// frame — src/target.js:186-250's <c>_paint()</c>, reduced to plain
    /// data. target.js reads this straight off a plane record (planes.js /
    /// planehall.js) and off live hit/ray state; all of that is Combat/City/
    /// Flight territory outside this phase's scope (see HudData.cs's note on
    /// why this port's UI layer takes snapshots rather than live references),
    /// so Core fills one of these per target per frame instead.
    ///
    /// NOTE ON THE ROLE STRING: <see cref="RoleIsTank"/> is a bool, not the
    /// pre-formatted "🛡 TANK"/"⚔ DPS" string, specifically so
    /// TargetCardView owns writing that ENGLISH exception text itself in one
    /// place (target.js:213-214) rather than trusting every caller to get an
    /// English string right in a project that is Georgian-only everywhere
    /// else.
    /// </summary>
    public struct TargetSnapshot
    {
        public string Id;          // def.id — change-detection key, target.js:195,197
        public string Name;        // def.name, target.js:205
        public string Sub;         // code · class, or "<code> · შენი მანქანა" for own, target.js:206-210
        public Texture2D Portrait; // def.thumb, target.js:202
        public bool RoleIsTank;    // planeRole(def) === 'TANK', target.js:212
        public bool IsOwn;         // rec.own

        public int Hp;             // hpOf(rec), target.js:96,190
        public int MaxHp;          // rec.maxHp || 1, target.js:191
        public bool Dead;          // rec.dead

        public float DistanceM;    // target.js:194

        public bool HitFlash;      // this._hitT > 0, target.js:195,249

        // "what the machine is made of" — target.js:216-229
        public int StaminaPool;
        public int TopSpeedKmh;
        public int RamDamage;
        public float MassKg;
    }
}
