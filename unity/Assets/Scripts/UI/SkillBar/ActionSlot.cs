using UnityEngine;

namespace TbilisiFly.UI.SkillBar
{
    /// <summary>
    /// One ability slot — skillbar.js:137-140's <c>this.slots</c> entries.
    /// Georgian tooltip strings carried byte-for-byte.
    /// </summary>
    public struct SkillSlot
    {
        public string Id;      // 'gun' | 'rocket'
        public string Name;    // Georgian tooltip (d.title in the JS)
        public Texture2D Icon; // gun_icon.svg / rocket_icon.svg, rasterized — see report

        public static readonly SkillSlot Gun = new SkillSlot
        {
            Id = "gun",
            Name = "ტყვიამფრქვევი — სწრაფი ცეცხლი", // skillbar.js:137
        };

        public static readonly SkillSlot Rocket = new SkillSlot
        {
            Id = "rocket",
            Name = "რაკეტები — ანგრევს შენობებს", // skillbar.js:138
        };
    }

    /// <summary>
    /// One on-foot weapon CATEGORY socket — skillbar.js:250-259's
    /// <c>WEAPON_SLOTS</c>. All eight exist empty from the start (GTA's wheel
    /// is categories, not guns); Georgian strings carried byte-for-byte.
    /// </summary>
    public struct WeaponCategory
    {
        public string Id;
        public string Name;
        public string Emoji;

        public static readonly WeaponCategory[] All =
        {
            new WeaponCategory { Id = "melee",   Name = "ხელჩართული — ჯერ არაფერია",         Emoji = "🔪" },
            new WeaponCategory { Id = "pistol",  Name = "პისტოლეტი — ჯერ არაფერია",           Emoji = "🔫" },
            new WeaponCategory { Id = "smg",     Name = "ავტომატი — ჯერ არაფერია",            Emoji = "🌀" },
            new WeaponCategory { Id = "shotgun", Name = "ხელჩართული თოფი — ჯერ არაფერია",     Emoji = "💥" },
            new WeaponCategory { Id = "rifle",   Name = "ავტომატური შაშხანა — ჯერ არაფერია",  Emoji = "🎯" },
            new WeaponCategory { Id = "sniper",  Name = "სნაიპერი — ჯერ არაფერია",             Emoji = "🔭" },
            new WeaponCategory { Id = "heavy",   Name = "მძიმე იარაღი — ჯერ არაფერია",        Emoji = "🚀" },
            new WeaponCategory { Id = "thrown",  Name = "სასროლი — ჯერ არაფერია",             Emoji = "💣" },
        };
    }

    /// <summary>A weapon actually carried in a slot — skillbar.js:330-346 setCarried.</summary>
    public struct CarriedWeapon
    {
        public string Name;
        public Texture2D Thumb; // null -> the category emoji is shown instead
    }
}
