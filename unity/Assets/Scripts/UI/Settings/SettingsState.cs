using UnityEngine;

namespace TbilisiFly.UI.Settings
{
    /// <summary>
    /// Port of src/settings.js:6-22's <c>QUALITY</c> map and <c>DEFAULTS</c>,
    /// and :32-35/:73-77's localStorage persistence (here, PlayerPrefs under
    /// the same key name so a save file from one build is at least
    /// recognizable as "the same settings" when read by a person, even
    /// though the two storages are not binary compatible).
    /// </summary>
    [System.Serializable]
    public class SettingsState
    {
        public string Quality = "ultra"; // settings.js:14 — boots at max graphics
        public bool Engine = true;       // settings.js:15
        public int Volume = 70;          // settings.js:16
        public bool Minimap = true;      // settings.js:17
        public bool Hints = true;        // settings.js:18
        public bool SoundChosen = false; // settings.js:19
        public string Side = "right";    // settings.js:20
        public bool AimOnDraw = true;    // settings.js:21

        const string PrefsKey = "tf-settings"; // settings.js:34 localStorage key, kept identical

        public static SettingsState Load()
        {
            var s = new SettingsState();
            try
            {
                string json = PlayerPrefs.GetString(PrefsKey, null);
                if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, s);
            }
            catch { /* settings.js:33 silently falls back to DEFAULTS */ }

            // settings.js:49 — the one-time sound-forced-off migration: old
            // saves had engine:false baked in as a bug's residue, not a
            // preference. Anyone without SoundChosen set gets the real
            // default back once; after that their own choice sticks.
            if (!s.SoundChosen) s.Engine = true;
            return s;
        }

        public void Save()
        {
            try { PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(this)); PlayerPrefs.Save(); }
            catch { }
        }
    }

    /// <summary>settings.js:6-11 QUALITY — render-scale/shadow presets the player can pick.</summary>
    public struct QualityPreset
    {
        public float PixelRatio;
        public bool Hq;

        // The 'ultra' JS preset clamps to the BROWSER's devicePixelRatio
        // (Math.min(2, window.devicePixelRatio || 1.5)) — there is no Unity
        // equivalent of devicePixelRatio to clamp against here; 2x is kept as
        // a reasonable fixed ceiling, matching the common case, but the real
        // URP render-scale mapping is a Core/rendering decision, not a UI one.
        public static QualityPreset For(string key) => key switch
        {
            "low" => new QualityPreset { PixelRatio = 0.75f, Hq = false },
            "med" => new QualityPreset { PixelRatio = 1.0f, Hq = false },
            "high" => new QualityPreset { PixelRatio = 1.5f, Hq = true },
            _ => new QualityPreset { PixelRatio = 2.0f, Hq = true }, // "ultra"
        };
    }
}
